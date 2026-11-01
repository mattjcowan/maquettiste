using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>
/// Tags across the model (2026-10-07, the owner: a tag vocabulary created on a model that already uses tags turned every use into a
/// note; "can I delete the tag right there and automatically remove it from all the uses?"): <see cref="GetTagUsageAsync"/> counts
/// the uses of each tag a vocabulary scope governs, and the batch operation <c>retag</c> removes tags, or renames one, in that
/// vocabulary and in every element and sub-element that uses them, all or nothing. The batch operation <c>tags-to-property</c>
/// (next-phase-1-design.md section 4.4) moves the tags of one prefix into a typed custom property in the same scope.
/// </summary>
/// <remarks>
/// A use belongs to the nearest vocabulary on the element's domain chain that declares the tag, or to no vocabulary when none does.
/// A scope governs its own declared tags' uses inside it and the undeclared uses inside it: the global scope the whole model, a
/// domain its elements and its sub-domains'. A domain that declares the same key keeps its uses when the global tag goes.
/// </remarks>
public sealed partial class ModelStore
{
    /// <summary>The members of free-form maps a tag walk does not enter: their values are data, not model annotations.</summary>
    private static readonly HashSet<string> FreeForm = new(StringComparer.Ordinal)
    {
        "properties", "generation", "rows", "defaultProperties", "options", "body", "expression", "variables", "defaultSql", "parameters",
    };

    private static readonly Regex TagLabel = new(@"^[^\s\u0000-\u001f\u007f]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>Whether an operation is a tag operation (<c>retag</c>, <c>tags-to-property</c>).</summary>
    /// <param name="op">The operation kind.</param>
    public static bool IsTagOperation(BatchOp op) => op is BatchOp.Retag or BatchOp.TagsToProperty;

    /// <summary>
    /// The tags a vocabulary scope governs: every tag its vocabulary declares and every undeclared tag used inside it, each with its
    /// uses there (the uses <c>retag</c> would change) and the elements that hold them.
    /// </summary>
    /// <param name="package">The domain whose vocabulary it is, or <see langword="null"/> for the global one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The usage, or <see langword="null"/> when <paramref name="package"/> is not a domain.</returns>
    public async Task<TagUsage?> GetTagUsageAsync(string? package, CancellationToken ct)
    {
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (package is not null && snapshot.Get<Package>(package) is null)
            return null;
        var vocabulary = snapshot.TagVocabularyOf(package);
        var declared = vocabulary?.Definitions.Select(d => d.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        var uses = new SortedDictionary<string, (int Uses, SortedSet<string> Elements, bool Declared)>(StringComparer.Ordinal);
        foreach (var key in declared)
            uses[key] = (0, new SortedSet<string>(StringComparer.Ordinal), true);
        foreach (var (document, tag) in GovernedUses(snapshot, package, null))
        {
            (int Uses, SortedSet<string> Elements, bool Declared) entry = uses.TryGetValue(tag, out var found) ? found : (0, new SortedSet<string>(StringComparer.Ordinal), false);
            entry.Elements.Add(document.Element.Id);
            uses[tag] = (entry.Uses + 1, entry.Elements, entry.Declared);
        }

        string NameOf(string id) => snapshot.GetDocument(id)?.Element is { } e ? ChangePlanner.ReadableName(snapshot, e) : id;
        return new TagUsage(package, vocabulary?.Id, vocabulary?.Strict ?? false,
            [.. uses.Select(p => new TagUse(p.Key, p.Value.Declared, p.Value.Uses, [.. p.Value.Elements],
                [.. p.Value.Elements.Select(NameOf).Order(StringComparer.Ordinal).Take(5)]))]);
    }

    /// <summary>
    /// The uses a scope governs (see the class remarks), as (document, tag) per use, in document order; with <paramref name="only"/>,
    /// only the uses of those tags.
    /// </summary>
    private static IEnumerable<(ElementDocument Document, string Tag)> GovernedUses(ModelSnapshot snapshot, string? package, IReadOnlySet<string>? only)
    {
        foreach (var document in ModelValidator.ActiveDocuments(snapshot))
        {
            var chain = snapshot.VocabularyChain(BuiltinRules.VocabularyScope(document.Element));
            if (package is not null && !chain.Contains(package))
                continue;
            foreach (var tag in TagsIn(document.Json))
            {
                if (only is not null && !only.Contains(tag))
                    continue;
                var owner = chain.FirstOrDefault(s => snapshot.TagVocabularyOf(s)?.Definitions.Any(d => d.Key == tag) == true, "\u0000none");
                if (owner == "\u0000none" || owner == package)
                    yield return (document, tag);
            }
        }
    }

    /// <summary>Every tag of a document: the strings of each <c>tags</c> array of the element and its sub-elements.</summary>
    private static IEnumerable<string> TagsIn(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                foreach (var tag in TagsIn(item))
                    yield return tag;
            }
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (FreeForm.Contains(property.Name))
                    continue;
                if (property.Name == "tags" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in property.Value.EnumerateArray())
                    {
                        if (tag.ValueKind == JsonValueKind.String)
                            yield return tag.GetString()!;
                    }
                }
                else
                {
                    foreach (var tag in TagsIn(property.Value))
                        yield return tag;
                }
            }
        }
    }

    /// <summary>
    /// <c>retag</c>: removes the tags (no <c>name</c>) or renames the one tag to <c>name</c> in the scope's vocabulary and in every use
    /// the scope governs, the uses of a renamed tag merging with the new key's where both are on one list. Adds the updates to
    /// <paramref name="changes"/>; returns why it is refused, or <see langword="null"/>.
    /// </summary>
    private string? Retag(ModelSnapshot snapshot, BatchOperation o, List<PlannedChange> changes)
    {
        var tags = (o.Tags ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (tags.Count == 0)
            return "Name at least one tag (tags).";
        if (o.Package is { } package && snapshot.Get<Package>(package) is null)
            return $"'{package}' is not a domain.";
        var rename = o.Name;
        if (rename is not null)
        {
            if (tags.Count != 1)
                return "A rename names one tag (tags) and its new key (name).";
            if (!TagLabel.IsMatch(rename))
                return $"'{rename}' is not a tag key: 1 to 64 characters without spaces or control characters.";
            if (rename == tags[0])
                return $"The tag is already '{rename}'.";
        }

        var only = tags.ToHashSet(StringComparer.Ordinal);
        var touched = new Dictionary<string, (string Hash, JsonObject Node)>(StringComparer.Ordinal);
        JsonObject Working(ElementDocument document)
        {
            if (!touched.TryGetValue(document.Element.Id, out var working))
                touched[document.Element.Id] = working = (document.Hash, (JsonObject)JsonNode.Parse(document.Json.GetRawText())!);
            return working.Node;
        }

        foreach (var document in GovernedUses(snapshot, o.Package, only).Select(u => u.Document).DistinctBy(d => d.Element.Id).ToList())
            Rewrite(Working(document), only, rename);

        if (snapshot.TagVocabularyOf(o.Package) is { } vocabulary && snapshot.GetDocument(vocabulary.Id) is { } vocabularyDocument
            && vocabulary.Definitions.Any(d => only.Contains(d.Key)))
        {
            var node = Working(vocabularyDocument);
            var definitions = node["definitions"] as JsonArray ?? [];
            var exists = rename is not null && definitions.OfType<JsonObject>().Any(d => d["key"]?.GetValue<string>() == rename);
            foreach (var definition in definitions.OfType<JsonObject>().Where(d => d["key"]?.GetValue<string>() is { } k && only.Contains(k)).ToList())
            {
                if (rename is not null && !exists)
                    definition["key"] = rename;
                else
                    definitions.Remove(definition);
            }
        }

        if (touched.Count == 0)
            return $"Nothing uses or declares {(tags.Count == 1 ? $"tag '{tags[0]}'" : "these tags")} here.";
        foreach (var (id, (hash, node)) in touched.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!TryParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()), id, out var parsed, out var failure))
                return failure.Diagnostics.FirstOrDefault()?.Message ?? $"The rewritten '{id}' is not valid.";
            var expected = o.ExpectedHashes is { } read ? read.GetValueOrDefault(id) ?? "unread" : hash;
            changes.Add(new PlannedChange(BatchOp.Update, id, expected, parsed, DeleteResolution.Refuse));
        }

        return null;
    }

    /// <summary>
    /// <c>tags-to-property</c> (next-phase-1-design.md section 4.4): in the holders of custom properties (<see cref="PropertyHost"/>)
    /// whose uses the scope of <c>package</c> governs (as for <c>retag</c>), limited to <c>elementIds</c>, the tags that start with
    /// <c>prefix</c> and leave a non-empty suffix move into the property <c>name</c>, converted by the first schema that applies to the
    /// holder and declares it (<see cref="FillProperty"/>). The tags leave the holder and, without <c>elementIds</c>, the declarations
    /// of the moved tags that no use still needs leave the vocabulary. Refused with MQ1002 when no tag matches, and with one MQ5001 per holder,
    /// at its file and <c>/properties/&lt;name&gt;</c>, when no schema declares the property, a suffix does not fit, or the holder
    /// already holds another value. Adds the updates to <paramref name="changes"/>.
    /// </summary>
    /// <returns>The diagnostics that refuse the operation, or <see langword="null"/>.</returns>
    private IReadOnlyList<Diagnostic>? TagsToProperty(ModelSnapshot snapshot, BatchOperation o, List<PlannedChange> changes, string pointer)
    {
        IReadOnlyList<Diagnostic> Refuse(string message, string? at = null) => [RuleCatalog.Create("MQ1002", message, o.Id, null, at ?? pointer)];
        if (o.Prefix is not { Length: > 0 } prefix)
            return Refuse("Name the prefix of the tags that move (prefix).");
        if (o.Name is not { Length: > 0 } name)
            return Refuse("Name the property the tags move to (name).");
        if (o.Package is { } package && snapshot.Get<Package>(package) is null)
            return Refuse($"'{package}' is not a domain.");
        HashSet<string>? only = null;
        if (o.ElementIds is { } ids)
        {
            for (var k = 0; k < ids.Count; k++)
            {
                if (snapshot.Get<Element>(ids[k]) is null)
                    return Refuse($"'{ids[k]}' is not an element: elementIds lists the elements whose tags move.", pointer + "/elementIds/" + k.ToString(CultureInfo.InvariantCulture));
            }

            only = ids.ToHashSet(StringComparer.Ordinal);
        }

        bool Prefixed(string tag) => tag.Length > prefix.Length && tag.StartsWith(prefix, StringComparison.Ordinal);

        // The prefixed tags the scope governs, per document, in document order.
        var governed = new List<(ElementDocument Document, HashSet<string> Tags)>();
        var byDocument = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (document, tag) in GovernedUses(snapshot, o.Package, null))
        {
            if (!Prefixed(tag) || (only is not null && !only.Contains(document.Element.Id)))
                continue;
            if (!byDocument.TryGetValue(document.Element.Id, out var tags))
            {
                byDocument[document.Element.Id] = tags = new HashSet<string>(StringComparer.Ordinal);
                governed.Add((document, tags));
            }

            tags.Add(tag);
        }

        var touched = new Dictionary<string, (string Hash, JsonObject Node)>(StringComparer.Ordinal);
        JsonObject Working(ElementDocument document)
        {
            if (!touched.TryGetValue(document.Element.Id, out var working))
                touched[document.Element.Id] = working = (document.Hash, (JsonObject)JsonNode.Parse(document.Json.GetRawText())!);
            return working.Node;
        }

        var names = new XRefNames(snapshot);
        var problems = new List<Diagnostic>();
        var moved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (document, tags) in governed)
        {
            var node = (JsonObject)JsonNode.Parse(document.Json.GetRawText())!;
            var changed = false;
            foreach (var host in PropertyHost.Of(document.Element))
            {
                if (NodeAt(node, host.Pointer) is not JsonObject holder || holder["tags"] is not JsonArray list)
                    continue;
                var held = list.Select(t => t is JsonValue v && v.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToList();
                var matched = held.Where(tags.Contains).Distinct(StringComparer.Ordinal).ToList();
                if (matched.Count == 0)
                    continue;
                var at = host.Pointer + "/properties/" + JsonPointer.Escape(name);
                var source = snapshot.PropertySchemas.For(host.KindName, host.Stereotypes)
                    .FirstOrDefault(s => s.Properties.ValueKind == JsonValueKind.Object && s.Properties.TryGetProperty(name, out _));
                string? why;
                JsonNode? value = null;
                if (source is null)
                {
                    why = $"no property schema that applies to this {host.KindName} declares '{name}': declare it in an extension file, or in the parameters of a stereotype the holder carries";
                }
                else
                {
                    source.Refs.TryGetValue(name, out var refs);
                    value = FillProperty(source.Properties.GetProperty(name), refs, [.. matched.Select(t => t[prefix.Length..])], document.Element, names, out why);
                    if (value is not null && host.Properties.TryGetValue(name, out var existing) && !JsonNode.DeepEquals(JsonNode.Parse(existing.GetRawText()), value))
                    {
                        why = $"it already holds {existing.GetRawText()} in '{name}'";
                        value = null;
                    }
                }

                if (value is null)
                {
                    problems.Add(RuleCatalog.Create("MQ5001",
                        $"The tags {string.Join(", ", matched.Select(t => "'" + t + "'"))} cannot move into property '{name}': {why}.", host.Id, document.Path, at));
                    continue;
                }

                if (holder["properties"] is not JsonObject properties)
                    holder["properties"] = properties = [];
                properties[name] = value;
                holder["tags"] = new JsonArray([.. held.Where(t => !tags.Contains(t)).Select(t => (JsonNode)t)]);
                changed = true;
                moved.UnionWith(matched);
            }

            if (changed)
                touched[document.Element.Id] = (document.Hash, node);
        }

        if (problems.Count > 0)
            return problems;
        if (moved.Count == 0)
            return Refuse($"Nothing here holds a tag that starts with '{prefix}' on an element or sub-element with properties.");

        // Without elementIds the move covers the whole scope, so the declarations of the moved tags go, unless a use the move leaves (on
        // an object that holds no properties) still needs one.
        if (only is null && snapshot.TagVocabularyOf(o.Package) is { } vocabulary && snapshot.GetDocument(vocabulary.Id) is { } vocabularyDocument)
        {
            var declared = vocabulary.Definitions.Select(d => d.Key).Where(moved.Contains).ToHashSet(StringComparer.Ordinal);
            var kept = new HashSet<string>(StringComparer.Ordinal);
            var rewritten = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (document, tag) in GovernedUses(snapshot, o.Package, declared))
            {
                var id = document.Element.Id;
                if (!touched.TryGetValue(id, out var working))
                {
                    kept.Add(tag);
                    continue;
                }

                if (!rewritten.TryGetValue(id, out var json))
                    rewritten[id] = json = JsonElement.Parse(working.Node.ToJsonString());
                if (TagsIn(json).Contains(tag, StringComparer.Ordinal))
                    kept.Add(tag);
            }

            if (declared.Any(k => !kept.Contains(k)))
            {
                var definitions = Working(vocabularyDocument)["definitions"] as JsonArray ?? [];
                foreach (var definition in definitions.OfType<JsonObject>().Where(d => d["key"]?.GetValue<string>() is { } k && declared.Contains(k) && !kept.Contains(k)).ToList())
                    definitions.Remove(definition);
            }
        }

        foreach (var (id, (hash, node)) in touched.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!TryParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()), id, out var parsed, out var failure))
                return failure.Diagnostics.Count > 0 ? failure.Diagnostics : Refuse($"The rewritten '{id}' is not valid.");
            var expected = o.ExpectedHashes is { } read ? read.GetValueOrDefault(id) ?? "unread" : hash;
            changes.Add(new PlannedChange(BatchOp.Update, id, expected, parsed, DeleteResolution.Refuse));
        }

        return null;
    }

    /// <summary>
    /// The value a property schema makes of tag suffixes (tags-to-property): an array takes every suffix in list order, each as its
    /// items' schema makes it; any other schema takes exactly one (<see cref="FillOne"/>).
    /// </summary>
    /// <returns>The value, or <see langword="null"/> with why the suffixes do not fit.</returns>
    private static JsonNode? FillProperty(JsonElement schema, XRefNode? refs, IReadOnlyList<string> suffixes, Element element, XRefNames names, out string? why)
    {
        if (schema.ValueKind == JsonValueKind.Object && string.Equals(SchemaType(schema), "array", StringComparison.Ordinal))
        {
            if (!schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            {
                why = "its schema is an array with no items schema";
                return null;
            }

            var array = new JsonArray();
            foreach (var suffix in suffixes)
            {
                if (FillOne(items, refs?.Items?.Ref, suffix, element, names, out why) is not { } item)
                    return null;
                array.Add(item);
            }

            why = null;
            return array;
        }

        if (suffixes.Count != 1)
        {
            why = $"the property takes one value, and {suffixes.Count.ToString(CultureInfo.InvariantCulture)} suffixes are left ({string.Join(", ", suffixes.Select(x => "'" + x + "'"))})";
            return null;
        }

        return FillOne(schema, refs?.Ref, suffixes[0], element, names, out why);
    }

    /// <summary>
    /// One suffix as a schema makes it: an <c>enum</c> member, a string (an <c>x-ref</c> resolves it to the one id it finds, as
    /// <c>names-to-ids</c> does), an integer, number or boolean parsed with the invariant culture.
    /// </summary>
    /// <returns>The value, or <see langword="null"/> with why the suffix does not fit.</returns>
    private static JsonNode? FillOne(JsonElement schema, XRef? xref, string suffix, Element element, XRefNames names, out string? why)
    {
        why = null;
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("enum", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in members.EnumerateArray())
            {
                if (member.ValueKind == JsonValueKind.String ? string.Equals(member.GetString(), suffix, StringComparison.Ordinal) : string.Equals(member.GetRawText(), suffix, StringComparison.Ordinal))
                    return JsonNode.Parse(member.GetRawText());
            }

            why = $"'{suffix}' is not one of its values ({string.Join(", ", members.EnumerateArray().Select(m => m.GetRawText()))})";
            return null;
        }

        var type = schema.ValueKind == JsonValueKind.Object ? SchemaType(schema) : null;
        switch (type ?? (xref is null ? null : "string"))
        {
            case "string" when xref is null:
                return JsonValue.Create(suffix);
            case "string":
            {
                var found = names.Find(element, xref, false, suffix, out var outOfScope);
                if (found is [var id])
                    return JsonValue.Create(id);
                why = "the suffix " + names.Finds(suffix, xref, found, outOfScope);
                return null;
            }

            case "integer" when long.TryParse(suffix, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole):
                return JsonValue.Create(whole);
            case "number" when decimal.TryParse(suffix, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out var number):
                return JsonValue.Create(number);
            case "boolean" when suffix is "true" or "false":
                return JsonValue.Create(string.Equals(suffix, "true", StringComparison.Ordinal));
            case "integer" or "number" or "boolean":
                why = $"'{suffix}' is not {(type == "integer" ? "an integer" : type == "number" ? "a number" : "true or false")}";
                return null;
            default:
                why = $"its schema's type ({type ?? "none"}) cannot hold a tag's text";
                return null;
        }
    }

    /// <summary>A schema's <c>type</c>: the string, or the first of its list that is not <c>null</c>; <see langword="null"/> without one.</summary>
    private static string? SchemaType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
            return null;
        if (type.ValueKind == JsonValueKind.String)
            return type.GetString();
        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String && t.GetString() != "null").Select(t => t.GetString()).FirstOrDefault()
            : null;
    }

    /// <summary>The node at a pointer of a document node (<c>""</c> for the root), or <see langword="null"/>.</summary>
    private static JsonNode? NodeAt(JsonNode root, string pointer)
    {
        if (pointer.Length == 0)
            return root;
        if (!JsonPointer.TryGetParent(root, pointer, out var parent, out var last))
            return null;
        return parent switch
        {
            JsonObject obj => obj[last],
            JsonArray array when JsonPointer.TryIndex(last, out var index) && index < array.Count => array[index],
            _ => null,
        };
    }

    /// <summary>Rewrites every <c>tags</c> array under a node: the tags removed, or renamed and deduplicated in order.</summary>
    private static void Rewrite(JsonNode? node, IReadOnlySet<string> tags, string? rename)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                    Rewrite(item, tags, rename);
                break;
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    if (FreeForm.Contains(name))
                        continue;
                    if (name == "tags" && value is JsonArray list)
                    {
                        var next = new List<string>();
                        foreach (var item in list)
                        {
                            if (item is not JsonValue v || !v.TryGetValue<string>(out var tag))
                                continue;
                            var kept = tags.Contains(tag) ? rename : tag;
                            if (kept is not null && !next.Contains(kept, StringComparer.Ordinal))
                                next.Add(kept);
                        }

                        obj["tags"] = new JsonArray([.. next.Select(t => (JsonNode)t)]);
                    }
                    else
                    {
                        Rewrite(value, tags, rename);
                    }
                }

                break;
        }
    }
}

/// <summary>The tags a vocabulary scope governs (<see cref="ModelStore.GetTagUsageAsync"/>).</summary>
/// <param name="Package">The domain, or <see langword="null"/> for the global vocabulary.</param>
/// <param name="Vocabulary">The scope's tag vocabulary id, or <see langword="null"/> when it has none.</param>
/// <param name="Strict">Whether the vocabulary is strict (an undeclared tag is an error).</param>
/// <param name="Tags">Each tag, ordinal by key.</param>
public sealed record TagUsage(string? Package, string? Vocabulary, bool Strict, IReadOnlyList<TagUse> Tags);

/// <summary>One tag of a scope.</summary>
/// <param name="Tag">The key.</param>
/// <param name="Declared">Whether the scope's vocabulary declares it.</param>
/// <param name="Uses">The uses the scope governs (the element's own tags and its sub-elements').</param>
/// <param name="Elements">The ids of the elements holding those uses, ordinal.</param>
/// <param name="Examples">The readable names of up to five of those elements, ordinal.</param>
public sealed record TagUse(string Tag, bool Declared, int Uses, IReadOnlyList<string> Elements, IReadOnlyList<string> Examples);
