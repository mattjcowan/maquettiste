using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// Seed input by name (next-phase-1-design.md section 5.3): a convenience on the seed body of a create or an update, before anything
/// reads it as a typed record. <see cref="Rows"/> turns each row's <c>values</c> object into the positional cells the file keeps;
/// <see cref="Names"/> turns the bare names left in <c>columns</c> and <c>key</c> into ids, relative to the seed's target. Storage stays
/// keyed by id: a name is input only. In a batch, 5.1's resolver runs between the two, so its qualified names and placeholders are ids
/// by then; a bare name never starts with <c>@</c> or <c>$</c>, so the two never claim the same string. What cannot be turned into an id
/// is MQ1002 with a message that says why, and nothing is written.
/// </summary>
internal static class SeedInput
{
    /// <summary>The built-in columns of a reference type's seed: keywords only when the target is a reference type.</summary>
    private static readonly string[] Keywords = ["code", "label", "description"];

    /// <summary>Whether a body is a seed's (its <c>kind</c> is <c>seed</c>).</summary>
    /// <param name="node">The body.</param>
    /// <returns><see langword="true"/> for a seed.</returns>
    public static bool IsSeed(JsonNode? node) =>
        node is JsonObject o && o["kind"] is JsonValue kind && kind.TryGetValue<string>(out var name) && name == "seed";

    /// <summary>
    /// Whether a seed body holds a <c>columns</c> or <c>key</c> entry that <see cref="Names"/> may turn into an id: a string that is not
    /// an id and does not start with <c>@</c> or <c>$</c> (a reference type's keywords included, since only the target tells).
    /// </summary>
    /// <param name="node">The body.</param>
    /// <returns><see langword="true"/> when <see cref="Names"/> has work.</returns>
    public static bool NeedsNames(JsonNode? node)
    {
        if (!IsSeed(node))
            return false;
        var seed = (JsonObject)node!;
        if (Text(seed["key"]) is { } key && IsBareName(key))
            return true;
        return seed["columns"] is JsonArray columns && columns.Any(c => Text(c) is { } entry && IsBareName(entry));
    }

    /// <summary>
    /// Turns each row's <c>values</c> object into the positional cells: its keys match the <c>columns</c> entries as the body writes
    /// them, ordinally; a column the object leaves out is null and trailing nulls are dropped. A key that is no entry is MQ1002 at
    /// <c>/rows/&lt;i&gt;/values/&lt;key&gt;</c>, never appended, since that would shift the cells of every other row. Needs no model.
    /// </summary>
    /// <param name="seed">The seed body, changed in place.</param>
    /// <param name="errors">Receives the diagnostics.</param>
    public static void Rows(JsonObject seed, List<Diagnostic> errors)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(errors);
        if (!IsSeed(seed) || seed["rows"] is not JsonArray rows)
            return;
        Dictionary<string, int>? positions = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row || row["values"] is not JsonObject values)
                continue;
            positions ??= ColumnPositions(seed);
            var cells = new List<JsonNode?>();
            var known = true;
            foreach (var (key, value) in values)
            {
                if (!positions.TryGetValue(key, out var at))
                {
                    var pointer = "/rows/" + i.ToString(CultureInfo.InvariantCulture) + "/values/" + JsonPointer.Escape(key);
                    errors.Add(RuleCatalog.Create("MQ1002",
                        $"'{key}' is not one of the seed's columns: a row's values object is keyed by the columns entries as the body writes them.",
                        IdOf(seed), null, pointer));
                    known = false;
                    continue;
                }

                while (cells.Count <= at)
                    cells.Add(null);
                cells[at] = value?.DeepClone();
            }

            if (!known)
                continue;
            while (cells.Count > 0 && (cells[^1] is null || cells[^1]!.GetValueKind() == JsonValueKind.Null))
                cells.RemoveAt(cells.Count - 1);
            row["values"] = new JsonArray([.. cells]);
        }
    }

    /// <summary>
    /// Turns each bare name in <c>columns</c> and <c>key</c> into the id of the target's child it names: a table's columns, then its
    /// named foreign keys (a foreign-key entry, next-phase-2-design.md section 3.4; a name both match is MQ1002), and for <c>key</c> its
    /// unique constraints, by stored name; an entity's attributes, own and inherited, and the roles of the to-one ends a
    /// seed of it may fill; a relation's end roles and attributes; a reference type's attributes (<c>code</c>, <c>label</c> and
    /// <c>description</c> stay keywords there). These are the names the seed CSV headers use. An entry that is an id, starts with
    /// <c>@</c> or <c>$</c>, or whose target the view does not hold is left as it is. No match, or several, is MQ1002 at
    /// <c>/columns/&lt;i&gt;</c> or <c>/key</c>, naming the target and each candidate's id.
    /// </summary>
    /// <param name="seed">The seed body, changed in place.</param>
    /// <param name="view">The model the target is read from.</param>
    /// <param name="errors">Receives the diagnostics.</param>
    public static void Names(JsonObject seed, SeedTargets view, List<Diagnostic> errors)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(errors);
        if (!IsSeed(seed) || Text(seed["target"]) is not { } targetId || view.Get(targetId) is not ({ } target and (Table or Entity or Relation or ReferenceType)))
            return;
        var id = IdOf(seed);
        if (seed["columns"] is JsonArray columns)
        {
            List<Child>? children = null;
            for (var k = 0; k < columns.Count; k++)
            {
                if (Text(columns[k]) is not { } entry || !IsBareName(entry) || (target is ReferenceType && Keywords.Contains(entry, StringComparer.Ordinal)))
                    continue;
                children ??= ChildrenOf(target, view);
                var pointer = "/columns/" + k.ToString(CultureInfo.InvariantCulture);
                if (Match(entry, children, target, WhatOf(target), id, pointer, errors) is { } found)
                    columns[k] = found;
            }
        }

        if (Text(seed["key"]) is { } key && IsBareName(key))
        {
            if (target is not Table table)
            {
                errors.Add(RuleCatalog.Create("MQ1002",
                    $"'{key}' cannot be resolved: only a table seed has a key, the id or the name of one of its table's unique constraints; {target.KindName} '{target.Name}' ({target.Id}) is not a table.",
                    id, null, "/key"));
            }
            else if (Match(key, [.. table.Uniques.Where(u => u.Name is not null).Select(u => new Child(u.Name!, u.Id, "unique constraint"))], target, "unique constraint", id, "/key", errors) is { } found)
            {
                seed["key"] = found;
            }
        }
    }

    /// <summary>The id the name finds among the children, or <see langword="null"/> after reporting MQ1002.</summary>
    private static string? Match(string name, List<Child> children, Element target, string what, string? seedId, string pointer, List<Diagnostic> errors)
    {
        var found = children.Where(c => string.Equals(c.Name, name, StringComparison.Ordinal)).DistinctBy(c => c.Id, StringComparer.Ordinal)
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        if (found.Count == 1)
            return found[0].Id;
        var owner = $"{target.KindName} '{target.Name}' ({target.Id})";
        errors.Add(RuleCatalog.Create("MQ1002", found.Count == 0
                ? $"'{name}' names no {what} of {owner}, so it cannot be stored as an id: give the id, or the name as the target stores it."
                : $"'{name}' names {found.Count.ToString(CultureInfo.InvariantCulture)} children of {owner}: "
                    + string.Join(", ", found.Select(c => c.Kind + " " + c.Id)) + "; give the id.",
            seedId, null, pointer));
        return null;
    }

    /// <summary>What a column of a seed of the target is, for messages.</summary>
    private static string WhatOf(Element target) => target switch
    {
        Table => "column or foreign key",
        Entity => "attribute or to-one relation end",
        Relation => "end or attribute",
        _ => "attribute (code, label and description are built in)",
    };

    /// <summary>The children a seed of the target names in <c>columns</c>, by stored name.</summary>
    private static List<Child> ChildrenOf(Element target, SeedTargets view)
    {
        var children = new List<Child>();
        void Attributes(IEnumerable<ModelAttribute> attributes) => children.AddRange(attributes.Select(a => new Child(a.Name, a.Id, "attribute")));
        switch (target)
        {
            case Table table:
                children.AddRange(table.Columns.Select(c => new Child(c.Name, c.Id, "column")));
                children.AddRange(table.ForeignKeys.Where(f => f.Name is not null).Select(f => new Child(f.Name!, f.Id, "foreign key")));
                break;
            case Entity entity:
            {
                // Own and inherited attributes, then the to-one far ends of binary relations whose near end is the entity or an ancestor.
                var lineage = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
                Attributes(entity.Attributes);
                for (var current = entity.Base; current is not null && lineage.Add(current) && view.Get(current) is Entity ancestor; current = ancestor.Base)
                    Attributes(ancestor.Attributes);
                foreach (var relation in view.Relations())
                {
                    if (relation.Ends.Count != 2)
                        continue;
                    for (var end = 0; end < 2; end++)
                    {
                        var far = relation.Ends[end];
                        if (far.Max != MaxCardinality.Many && lineage.Contains(relation.Ends[1 - end].Entity))
                            children.Add(new Child(far.Role, far.Id, "end"));
                    }
                }

                break;
            }

            case Relation relation:
                children.AddRange(relation.Ends.Select(e => new Child(e.Role, e.Id, "end")));
                Attributes(relation.Attributes);
                break;
            case ReferenceType type:
                Attributes(type.Attributes);
                break;
        }

        return children;
    }

    /// <summary>Each <c>columns</c> entry's first position, as the body writes it.</summary>
    private static Dictionary<string, int> ColumnPositions(JsonObject seed)
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (seed["columns"] is JsonArray columns)
        {
            for (var k = 0; k < columns.Count; k++)
            {
                if (Text(columns[k]) is { } entry)
                    positions.TryAdd(entry, k);
            }
        }

        return positions;
    }

    /// <summary>
    /// A string that is not an id, a keyed id (ids joined by <c>.</c>, an inherited member's <c>&lt;tableId&gt;.&lt;memberId&gt;</c>,
    /// next-phase-2-design.md section 3.1), a qualified name or a placeholder.
    /// </summary>
    private static bool IsBareName(string value) => value.Length > 0 && Validation.BuiltinRules.XRefIds(value) is null && !QualifiedName.IsName(value);

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? IdOf(JsonObject seed) => Text(seed["id"]);

    /// <summary>A child a seed column may name.</summary>
    private sealed record Child(string Name, string Id, string Kind);
}

/// <summary>
/// The model a seed's names are read from (next-phase-1-design.md section 5.3): the loaded snapshot with a batch's earlier operations
/// laid over it, a create or an update replacing the element's document and a delete removing it. Like 5.1's names, it does not see
/// what schema, materialize, process and tag operations produce.
/// </summary>
/// <param name="snapshot">The model as loaded.</param>
internal sealed class SeedTargets(ModelSnapshot snapshot)
{
    private readonly Dictionary<string, Element?> _laid = new(StringComparer.Ordinal);

    /// <summary>An element as the batch leaves it so far: written, deleted (<see langword="null"/>) or as loaded.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The element.</returns>
    public Element? Get(string id) => _laid.TryGetValue(id, out var element) ? element : snapshot.Get<Element>(id);

    /// <summary>Every relation: the loaded ones the batch has not written, then the batch's own.</summary>
    /// <returns>The relations.</returns>
    public IEnumerable<Relation> Relations() =>
        snapshot.ElementsOf(ElementKind.Relation).OfType<Relation>().Where(r => !_laid.ContainsKey(r.Id)).Concat(_laid.Values.OfType<Relation>());

    /// <summary>Lays a create's or an update's body over the view; a body that does not read as an element changes nothing.</summary>
    /// <param name="node">The body, its ids already resolved.</param>
    public void Lay(JsonNode? node)
    {
        if (node is not JsonObject)
            return;
        try
        {
            var element = ElementReader.ReadElement(JsonElement.Parse(node.ToJsonString(), DocumentReader.ParseOptions));
            if (element.Id.Length > 0)
                _laid[element.Id] = element;
        }
        catch (Exception ex) when (DocumentReader.IsDeserializationFailure(ex))
        {
            // The planner reports a body that does not read (MQ1002).
        }
    }

    /// <summary>Removes a deleted element from the view.</summary>
    /// <param name="id">The id.</param>
    public void Remove(string id) => _laid[id] = null;
}
