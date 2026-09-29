using System.Collections.Frozen;
using System.Text.Json;

namespace Maquettiste.Engine.Model;

/// <summary>
/// The model-wide facts the index needs to turn seed cells, defaults and <c>allowedValues</c> entries into references
/// (reference-types-seeds-localization.md section 2.2): which attributes are typed by a reference type, which ids are relation ends,
/// and each reference type's codes with the row that holds them. A code cell or default becomes a reference to its row's id; an end
/// cell (a row id) becomes a reference to that row. Built once per full index build; a patch keeps it (see
/// <see cref="ModelIndexer"/>, which rebuilds in full whenever a change could alter it).
/// </summary>
internal sealed class ReferenceDataIndex
{
    private ReferenceDataIndex(
        FrozenSet<string> referenceTypeIds,
        FrozenDictionary<string, string> attributeTypes,
        FrozenSet<string> endIds,
        FrozenDictionary<string, FrozenDictionary<string, string>> codes,
        FrozenSet<string> seedColumns)
    {
        ReferenceTypeIds = referenceTypeIds;
        AttributeTypes = attributeTypes;
        EndIds = endIds;
        Codes = codes;
        SeedColumns = seedColumns;
    }

    /// <summary>The ids of the model's reference types.</summary>
    public FrozenSet<string> ReferenceTypeIds { get; }

    /// <summary>Attribute id to the id of the reference type that types it, for every reference-typed attribute.</summary>
    public FrozenDictionary<string, string> AttributeTypes { get; }

    /// <summary>The ids of every relation end.</summary>
    public FrozenSet<string> EndIds { get; }

    /// <summary>Reference type id to code key (<see cref="CodeKey"/>) to the id of the first row holding that code, in (seed name, seed id, file order).</summary>
    public FrozenDictionary<string, FrozenDictionary<string, string>> Codes { get; }

    /// <summary>Every attribute or end id a seed column names.</summary>
    public FrozenSet<string> SeedColumns { get; }

    /// <summary>Builds the facts from the model's elements.</summary>
    /// <param name="elements">The elements (duplicates by id keep the first).</param>
    /// <returns>The facts.</returns>
    public static ReferenceDataIndex Build(IEnumerable<Element> elements)
    {
        var referenceTypes = new HashSet<string>(StringComparer.Ordinal);
        var attributeTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var ends = new HashSet<string>(StringComparer.Ordinal);
        var seeds = new List<Seed>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<Element>();
        foreach (var element in elements)
        {
            if (!seen.Add(element.Id))
                continue;
            all.Add(element);
            if (element is ReferenceType rt)
                referenceTypes.Add(rt.Id);
        }

        foreach (var element in all)
        {
            foreach (var attribute in AttributesOf(element))
            {
                if (attribute.Type.Ref is { } typeId && referenceTypes.Contains(typeId))
                    attributeTypes.TryAdd(attribute.Id, typeId);
            }

            switch (element)
            {
                case Relation relation:
                    foreach (var end in relation.Ends)
                        ends.Add(end.Id);
                    break;
                case Seed seed:
                    seeds.Add(seed);
                    break;
            }
        }

        var codes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in seeds.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            foreach (var column in seed.Columns)
            {
                if (IdFormat.IsValid(column))
                    columns.Add(column);
            }

            if (!referenceTypes.Contains(seed.Target))
                continue;
            var codeColumn = IndexOf(seed.Columns, "code");
            if (codeColumn < 0)
                continue;
            if (!codes.TryGetValue(seed.Target, out var map))
                codes[seed.Target] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in seed.Rows)
            {
                if (codeColumn < row.Values.Count && CodeKey(row.Values[codeColumn]) is { } key)
                    map.TryAdd(key, row.Id);
            }
        }

        return new ReferenceDataIndex(
            referenceTypes.ToFrozenSet(StringComparer.Ordinal),
            attributeTypes.ToFrozenDictionary(StringComparer.Ordinal),
            ends.ToFrozenSet(StringComparer.Ordinal),
            codes.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToFrozenDictionary(StringComparer.Ordinal), StringComparer.Ordinal),
            columns.ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>The attributes an element declares (entity, value object, relation, stereotype, reference type).</summary>
    /// <param name="element">The element.</param>
    /// <returns>The attributes, or an empty list.</returns>
    public static IReadOnlyList<ModelAttribute> AttributesOf(Element element) => element switch
    {
        Entity e => e.Attributes,
        ValueObject v => v.Attributes,
        Relation r => r.Attributes,
        Stereotype s => s.Attributes,
        ReferenceType t => t.Attributes,
        _ => [],
    };

    /// <summary>The key of a code cell: a string's text, a number's raw text; null for anything else.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The key, or <see langword="null"/>.</returns>
    public static string? CodeKey(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.String => cell.GetString(),
        JsonValueKind.Number => cell.GetRawText(),
        _ => null,
    };

    /// <summary>The id of the row of a reference type that holds a code, or <see langword="null"/>.</summary>
    /// <param name="typeId">The reference type id.</param>
    /// <param name="cell">The code.</param>
    /// <returns>The row id.</returns>
    public string? RowOf(string typeId, JsonElement cell) =>
        CodeKey(cell) is { } key && Codes.TryGetValue(typeId, out var map) && map.TryGetValue(key, out var row) ? row : null;

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }
}
