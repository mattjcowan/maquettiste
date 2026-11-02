using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Migrations;

/// <summary>A column that stores a reference type's codes: its table's and its own capture keys, and its names in that resolve.</summary>
/// <param name="Table">The table key.</param>
/// <param name="Key">The column key.</param>
/// <param name="Schema">The table's database schema name.</param>
/// <param name="TableName">The table's name.</param>
/// <param name="Name">The column's name.</param>
internal sealed record HolderColumn(string Table, string Key, string? Schema, string TableName, string Name);

/// <summary>
/// A template-defined collection of a reference type's codes: a collection attribute that no column stores, which the templates realize
/// for its owner table (a junction table, an array column).
/// </summary>
/// <param name="Table">The owner table's key.</param>
/// <param name="Schema">The owner table's database schema name.</param>
/// <param name="TableName">The owner table's name.</param>
/// <param name="Attribute">The collection attribute's id.</param>
/// <param name="Name">The attribute's name.</param>
internal sealed record HolderCollection(string Table, string? Schema, string TableName, string Attribute, string Name);

/// <summary>
/// Where a resolved database stores reference-type codes (next-phase-2-design.md section 3.5, "Recording the value map"): per type, the
/// columns whose <see cref="RColumn.ReferenceType"/> is the type (a projected reference column, and since 3.2 a designed, imported,
/// extra or inherited column its <c>typeRef</c> types), and the template-defined collections typed by it (an entity mapping's, with
/// its table; a junction-mapped relation's, with its junction table). Columns are sorted by (table key, column key), collections by
/// (table key, attribute id), ordinal; a collection a TPH table lists for several entities is listed once.
/// </summary>
internal sealed class CodeHolders
{
    private readonly Dictionary<string, List<HolderColumn>> _columns;
    private readonly Dictionary<string, List<HolderCollection>> _collections;

    private CodeHolders(Dictionary<string, List<HolderColumn>> columns, Dictionary<string, List<HolderCollection>> collections)
    {
        _columns = columns;
        _collections = collections;
    }

    /// <summary>No holders.</summary>
    public static CodeHolders Empty { get; } = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));

    /// <summary>The columns that store a type's codes.</summary>
    /// <param name="typeId">The reference type's id.</param>
    /// <returns>The columns, sorted.</returns>
    public IReadOnlyList<HolderColumn> Columns(string typeId) => _columns.TryGetValue(typeId, out var list) ? list : [];

    /// <summary>The template-defined collections of a type's codes.</summary>
    /// <param name="typeId">The reference type's id.</param>
    /// <returns>The collections, sorted.</returns>
    public IReadOnlyList<HolderCollection> Collections(string typeId) => _collections.TryGetValue(typeId, out var list) ? list : [];

    /// <summary>Whether the database stores a type's codes anywhere.</summary>
    /// <param name="typeId">The reference type's id.</param>
    /// <returns>Whether a column or a collection holds them.</returns>
    public bool Holds(string typeId) => _columns.ContainsKey(typeId) || _collections.ContainsKey(typeId);

    /// <summary>The holders of every reference type in a resolved database (a whole-model or a partial run).</summary>
    /// <param name="database">The database.</param>
    /// <returns>The holders.</returns>
    public static CodeHolders Of(RDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var columns = new List<(string Type, HolderColumn Holder)>();
        foreach (var table in database.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.ReferenceType is { } type)
                    columns.Add((type.Id, new HolderColumn(table.Key, column.Key, table.Schema, table.Name, column.Name)));
            }
        }

        var collections = new List<(string Type, HolderCollection Holder)>();
        lock (database.TemplateCollections)
        {
            foreach (var (owner, attribute) in database.TemplateCollections)
            {
                if (attribute.Type.ReferenceType is { } type)
                    collections.Add((type.Id, new HolderCollection(owner.Key, owner.Schema, owner.Name, attribute.Id, attribute.Name)));
            }
        }

        return From(columns, collections);
    }

    /// <summary>Holders from lists (tests build them so).</summary>
    /// <param name="columns">The columns, each with its type's id.</param>
    /// <param name="collections">The collections, each with its type's id.</param>
    /// <returns>The holders, sorted and without duplicates.</returns>
    public static CodeHolders From(IEnumerable<(string Type, HolderColumn Holder)> columns, IEnumerable<(string Type, HolderCollection Holder)> collections)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(collections);
        var byColumn = columns.GroupBy(c => c.Type, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(c => c.Holder).DistinctBy(h => (h.Table, h.Key)).OrderBy(h => h.Table, StringComparer.Ordinal)
                .ThenBy(h => h.Key, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var byCollection = collections.GroupBy(c => c.Type, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(c => c.Holder).DistinctBy(h => (h.Table, h.Attribute)).OrderBy(h => h.Table, StringComparer.Ordinal)
                .ThenBy(h => h.Attribute, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        return new CodeHolders(byColumn, byCollection);
    }
}
