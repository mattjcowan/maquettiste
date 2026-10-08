using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>
/// Tags across the model (2026-10-07, the owner: a tag vocabulary created on a model that already uses tags turned every use into a
/// note; "can I delete the tag right there and automatically remove it from all the uses?"): <see cref="GetTagUsageAsync"/> counts
/// the uses of each tag a vocabulary scope governs, and the batch operation <c>retag</c> removes tags, or renames one, in that
/// vocabulary and in every element and sub-element that uses them, all or nothing.
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

    /// <summary>Whether an operation is a tag operation.</summary>
    /// <param name="op">The operation kind.</param>
    public static bool IsTagOperation(BatchOp op) => op is BatchOp.Retag;

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
