using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>
/// The batch operation <c>names-to-ids</c> (next-phase-1-design.md section 4.1): a custom property an <c>x-ref</c> types holds an id,
/// so a value written as a name (by hand, or before the schema typed it) is MQ2010 until it becomes the id it names. The operation
/// turns each such value into the one id it finds among the in-scope candidates of the keyword's kinds, all or nothing, through the
/// store's atomic write path; <see cref="PreviewBatchAsync"/> is its dry run. The resolver is shared with <c>tags-to-property</c>
/// (section 4.4), which resolves a suffix moving into a typed property the same way.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>
    /// <c>names-to-ids</c>: in the elements <c>elementIds</c> lists, or in every document with <c>x-ref</c> sites (an explicit
    /// whole-model write, like <c>retag</c>), each typed value that is not an id is matched and replaced by the one id it finds. A value
    /// that finds nothing or several refuses the operation with one MQ2010 at the value's file and pointer. Adds the updates to
    /// <paramref name="changes"/>; nothing to convert adds none.
    /// </summary>
    /// <returns>The diagnostics that refuse the operation, or <see langword="null"/>.</returns>
    private IReadOnlyList<Diagnostic>? NamesToIds(ModelSnapshot snapshot, BatchOperation o, List<PlannedChange> changes, string pointer)
    {
        List<ElementDocument> documents;
        if (o.ElementIds is { } ids)
        {
            documents = [];
            var listed = new HashSet<string>(StringComparer.Ordinal);
            for (var k = 0; k < ids.Count; k++)
            {
                if (snapshot.Get<Element>(ids[k]) is null || snapshot.GetDocument(ids[k]) is not { } document)
                {
                    return [RuleCatalog.Create("MQ1002", $"'{ids[k]}' is not an element: elementIds lists the elements whose typed values become ids.", o.Id, null,
                        pointer + "/elementIds/" + k.ToString(CultureInfo.InvariantCulture))];
                }

                if (listed.Add(document.Element.Id))
                    documents.Add(document);
            }
        }
        else
        {
            documents = ModelValidator.ActiveDocuments(snapshot);
        }

        var schemas = snapshot.PropertySchemas;
        if (!schemas.HasRefs)
            return null;
        var names = new XRefNames(snapshot);
        var problems = new List<Diagnostic>();
        var edits = new List<(ElementDocument Document, List<(string Pointer, string Id)> Values)>();
        foreach (var document in documents)
        {
            var values = new List<(string Pointer, string Id)>();
            // One value several schemas type is one site per schema: the id it becomes must satisfy each of them.
            foreach (var group in schemas.Sites(document.Element, snapshot.PropertyKeys(document.Element)).Where(s => BuiltinRules.XRefIds(s.Value) is null).GroupBy(s => s.Pointer, StringComparer.Ordinal))
            {
                IReadOnlyList<string>? candidates = null;
                string? why = null;
                foreach (var site in group)
                {
                    var found = names.Find(document.Element, site.Ref, site.Default, site.Value, out var outOfScope);
                    why ??= outOfScope;
                    candidates = candidates is null ? found : [.. candidates.Intersect(found, StringComparer.Ordinal)];
                }

                var first = group.First();
                if (candidates is [var id])
                    values.Add((group.Key, id));
                else
                    problems.Add(RuleCatalog.Create("MQ2010", names.Refusal(first.Field, first.Value, first.Ref, candidates ?? [], why), first.Host.Id, document.Path, group.Key));
            }

            if (values.Count > 0)
                edits.Add((document, values));
        }

        if (problems.Count > 0)
            return problems;
        foreach (var (document, values) in edits)
        {
            var node = (JsonObject)JsonNode.Parse(document.Json.GetRawText())!;
            foreach (var (at, id) in values)
                SetValue(node, at, id);
            var elementId = document.Element.Id;
            if (!TryParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()), elementId, out var parsed, out var failure))
                return failure.Diagnostics.Count > 0 ? failure.Diagnostics : [RuleCatalog.Create("MQ1002", $"The rewritten '{elementId}' is not valid.", elementId, document.Path, null)];
            var expected = o.ExpectedHashes is { } read ? read.GetValueOrDefault(elementId) ?? "unread" : document.Hash;
            changes.Add(new PlannedChange(BatchOp.Update, elementId, expected, parsed, DeleteResolution.Refuse));
        }

        return null;
    }

    /// <summary>Writes a string at a pointer of a document node: an object member or an array item that exists.</summary>
    private static void SetValue(JsonNode root, string pointer, JsonNode value)
    {
        if (!JsonPointer.TryGetParent(root, pointer, out var parent, out var last))
            return;
        switch (parent)
        {
            case JsonObject obj:
                obj[last] = value;
                break;
            case JsonArray array when JsonPointer.TryIndex(last, out var index) && index < array.Count:
                array[index] = value;
                break;
        }
    }

    /// <summary>
    /// The one definition of an <c>x-ref</c> value's name (next-phase-1-design.md section 4.1), which <c>names-to-ids</c> and
    /// <c>tags-to-property</c> share: a text is matched against the in-scope candidates of each of the keyword's kinds. Elements and
    /// sub-elements match through the qualified-name forms a batch resolves (<see cref="NameIndex"/>: stereotypes by key only, an
    /// attribute as <c>Owner.name</c>); a text that starts with <c>@</c> is a qualified name of one of the kinds. Within an
    /// <c>of: "$element"</c> scope a sub-element also matches its own name (a column its physical name), and under <c>of: &lt;id&gt;</c>
    /// a row matches its code (<see cref="ReferenceDataIndex.RowOf"/>), and a former code of the type the row that replaced it
    /// (<see cref="ReferenceDataIndex.ReplacedByOf"/>). The scope narrows every candidate: <c>of: "$element"</c> to the
    /// members of the element, its base entities and its stereotypes (implied ones included), <c>of: &lt;id&gt;</c> to rows of a seed of
    /// that element, and <c>sameDatabase</c> to the element's one database. Names compare ordinally with the names as stored.
    /// </summary>
    private sealed class XRefNames(ModelSnapshot snapshot)
    {
        private readonly Dictionary<string, HashSet<string>> _owners = new(StringComparer.Ordinal);
        private ReferenceDataIndex? _referenceData;

        /// <summary>The ids a text finds for one keyword, ordinal; empty with the reason when the keyword leaves no scope.</summary>
        /// <param name="element">The top-level element whose file holds the value.</param>
        /// <param name="xref">The keyword.</param>
        /// <param name="isDefault">Whether the value is a stereotype default, which serves many elements.</param>
        /// <param name="text">The value as written.</param>
        /// <param name="outOfScope">Why no candidate can be in scope, or <see langword="null"/>.</param>
        /// <returns>The ids.</returns>
        public IReadOnlyList<string> Find(Element element, XRef xref, bool isDefault, string text, out string? outOfScope)
        {
            outOfScope = null;
            HashSet<string>? owners = null;
            string? list = null;
            string? database = null;
            if (xref.Of == XRef.ElementScope)
            {
                if (isDefault)
                {
                    outOfScope = "a stereotype default cannot use of \"$element\", since it serves many elements";
                    return [];
                }

                owners = OwnersOf(element);
            }
            else if (xref.Of is { } of)
            {
                list = of;
            }

            if (xref.SameDatabase)
            {
                if (isDefault)
                {
                    outOfScope = "a stereotype default cannot use sameDatabase, since it serves many elements";
                    return [];
                }

                if ((database = BuiltinRules.XRefDatabaseOf(element)) is null)
                {
                    outOfScope = $"{element.KindName} {element.Name} has no single database for sameDatabase";
                    return [];
                }
            }

            QualifiedName? qualified = null;
            if (text.StartsWith('@') && !QualifiedName.TryParse(text, out qualified, out _))
                return [];
            var found = new SortedSet<string>(StringComparer.Ordinal);
            void Add(string id, string ownerId)
            {
                if ((owners is null || owners.Contains(ownerId))
                    && (list is null || (snapshot.Get<Seed>(ownerId) is { } seed && string.Equals(seed.Target, list, StringComparison.Ordinal)))
                    && (database is null || (snapshot.Get<Element>(ownerId) is { } owner && BuiltinRules.XRefDatabaseOf(owner) == database)))
                {
                    found.Add(id);
                }
            }

            foreach (var kind in xref.Kinds)
            {
                if ((qualified is not null && !string.Equals(qualified.Kind, kind, StringComparison.Ordinal)) || !NameIndex.NamedKinds.Contains(kind))
                    continue; // reserved kinds and kinds listed by id only have no names
                var index = snapshot.Names(kind);
                foreach (var entry in index.Find(qualified?.Path ?? PathOf(kind, text)))
                    Add(entry.Id, entry.ElementId);

                if (qualified is not null)
                    continue;
                if (owners is not null && XRef.IsSubElementKind(kind))
                {
                    foreach (var owner in owners.Order(StringComparer.Ordinal))
                    {
                        foreach (var entry in index.EntriesOf(owner))
                        {
                            if (string.Equals(entry.Kind, kind, StringComparison.Ordinal) && string.Equals(OwnName(entry), text, StringComparison.Ordinal))
                                Add(entry.Id, entry.ElementId);
                        }
                    }
                }

                // A row matches its code; a former code of the type finds the row that replaced it (next-phase-2-design.md section 3.5).
                if (list is not null && string.Equals(kind, "row", StringComparison.Ordinal)
                    && (_referenceData ??= ReferenceDataIndex.Build(snapshot.Documents.Select(d => d.Element))) is var referenceData
                    && (referenceData.RowOf(list, Text(text)) ?? referenceData.ReplacedByOf(list, Text(text))) is { } row
                    && snapshot.TryGetEntry(row, out var rowEntry))
                {
                    Add(row, rowEntry.OwnerId);
                }
            }

            return [.. found];
        }

        /// <summary>The MQ2010 message of a value that finds no id, or several.</summary>
        /// <param name="field">The top-level property.</param>
        /// <param name="value">The value as written.</param>
        /// <param name="xref">The keyword.</param>
        /// <param name="candidates">The ids it finds.</param>
        /// <param name="why">Why no candidate can be in scope, or <see langword="null"/>.</param>
        /// <returns>The message.</returns>
        public string Refusal(string field, string value, XRef xref, IReadOnlyList<string> candidates, string? why) =>
            $"'{field}' holds {Finds(value, xref, candidates, why)}. Write the id, or "
            + (candidates.Count == 0 ? "a name that finds one." : "a longer name (@kind:path) that finds one.");

        /// <summary>What a text finds, for a message: <c>'text', which finds no ... in scope</c> or the candidates it finds.</summary>
        /// <param name="text">The text.</param>
        /// <param name="xref">The keyword.</param>
        /// <param name="candidates">The ids it finds.</param>
        /// <param name="why">Why no candidate can be in scope, or <see langword="null"/>.</param>
        /// <returns>The clause.</returns>
        public string Finds(string text, XRef xref, IReadOnlyList<string> candidates, string? why)
        {
            var kinds = string.Join(" or ", xref.Kinds);
            return candidates.Count == 0
                ? $"'{text}', which finds no {kinds} in scope{(why is null ? "" : ": " + why)}"
                : $"'{text}', which finds {candidates.Count.ToString(CultureInfo.InvariantCulture)} of kind {kinds}: " + string.Join(", ", candidates.Select(Describe));
        }

        /// <summary>A candidate as <c>@kind:longest (id)</c>, as MQ1013 lists them.</summary>
        private string Describe(string id) =>
            snapshot.TryGetEntry(id, out var entry) && NameIndex.NamedKinds.Contains(entry.Kind) && snapshot.Names(entry.Kind).Get(id) is { } named
                ? "@" + named.Kind + ":" + named.Longest + " (" + id + ")"
                : id;

        /// <summary>The elements whose members <c>of: "$element"</c> takes: the element, its base entities and its stereotypes.</summary>
        private HashSet<string> OwnersOf(Element element)
        {
            if (_owners.TryGetValue(element.Id, out var owners))
                return owners;
            owners = new HashSet<string>(StringComparer.Ordinal) { element.Id };
            // The base chain, nearest first; a cycle stops at its first repeat.
            var current = (element as Entity)?.Base;
            while (current is not null && owners.Add(current) && snapshot.Get<Entity>(current) is { } ancestor)
                current = ancestor.Base;

            // The element's effective set, defaults included (next-phase-2-design.md section 3.1).
            foreach (var applied in snapshot.EffectiveStereotypes(element))
                owners.Add(applied.Stereotype.Id);
            _owners[element.Id] = owners;
            return owners;
        }

        /// <summary>A text as the canonical path of a kind's forms: the path it parses as, else the whole text as one segment.</summary>
        private static string PathOf(string kind, string text) =>
            QualifiedName.TryParse("@" + kind + ":" + text, out var name, out _) ? name.Path : QualifiedName.FormatSegment(text);

        /// <summary>A sub-element's own name: the last segment of its forms (a column's physical name, a row's code).</summary>
        private static string? OwnName(NameEntry entry) =>
            QualifiedName.TryParse("@" + entry.Kind + ":" + entry.Longest, out var name, out _) ? name.Segments[^1] : null;

        private static JsonElement Text(string text) => JsonElement.Parse(JsonValue.Create(text).ToJsonString());
    }
}
