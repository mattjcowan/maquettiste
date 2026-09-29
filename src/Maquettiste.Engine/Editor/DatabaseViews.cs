using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>The result of <see cref="GenerationService.GetDatabaseViewAsync"/> (E1, phase2-design.md section 3.8).</summary>
/// <param name="View">The resolved database, or <see langword="null"/> when the model has errors or the database is unknown.</param>
/// <param name="Diagnostics">Validation and resolution diagnostics; the errors that prevented the view when it is <see langword="null"/>.</param>
public sealed record DatabaseViewResult(DatabaseView? View, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The resolved physical model of one database: every table after conventions, mappings and overlays.</summary>
/// <param name="Id">The database element's id.</param>
/// <param name="Name">The database name.</param>
/// <param name="Dialect"><c>postgresql</c>, <c>sqlserver</c>, <c>mysql</c>, <c>sqlite</c> or <c>oracle</c>.</param>
/// <param name="Version">The target version.</param>
/// <param name="DefaultSchema">The effective default schema, or <see langword="null"/> when the dialect has none.</param>
/// <param name="Tables">Every table, in the resolver's (schema, name) order.</param>
public sealed record DatabaseView(string Id, string Name, string Dialect, string? Version, string? DefaultSchema, IReadOnlyList<TableView> Tables);

/// <summary>One resolved table, projected from the resolver's <see cref="RTable"/> (resolved objects reference each other, so they are
/// flattened: other objects appear by key or id).</summary>
/// <param name="Key">The table key: a table file's id, or a synthesized key such as <c>&lt;entityId&gt;@&lt;databaseId&gt;</c>.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Schema">The schema name, or <see langword="null"/>.</param>
/// <param name="Origin"><c>synthesized</c>, <c>designed</c> or <c>imported</c>.</param>
/// <param name="EntityId">The mapped entity's id, for an entity table.</param>
/// <param name="RelationId">The relation's id, for a junction table.</param>
/// <param name="IsJunction">Whether the table is a relation's junction table.</param>
/// <param name="IsLookup">Always <see langword="false"/>: the enum lookup-table option is retired (MQ7012); kept for the contract.</param>
/// <param name="Comment">The comment.</param>
/// <param name="Columns">The columns, by position.</param>
/// <param name="PrimaryKey">The primary key, or <see langword="null"/>.</param>
/// <param name="Uniques">Unique constraints.</param>
/// <param name="ForeignKeys">Foreign keys.</param>
/// <param name="Indexes">Indexes.</param>
public sealed record TableView(
    string Key,
    string Name,
    string? Schema,
    string Origin,
    string? EntityId,
    string? RelationId,
    bool IsJunction,
    bool IsLookup,
    string? Comment,
    IReadOnlyList<ColumnView> Columns,
    KeyView? PrimaryKey,
    IReadOnlyList<KeyView> Uniques,
    IReadOnlyList<ForeignKeyView> ForeignKeys,
    IReadOnlyList<IndexView> Indexes);

/// <summary>One resolved column, projected from <see cref="RColumn"/>.</summary>
/// <param name="Key">The column key: an attribute path, <c>discriminator</c>, <c>position</c>, <c>id</c>, or a column id.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Type">The built-in type keyword.</param>
/// <param name="NativeType">The dialect's type, such as <c>varchar(120)</c>.</param>
/// <param name="Length">The effective length.</param>
/// <param name="Precision">The effective precision.</param>
/// <param name="Scale">The effective scale.</param>
/// <param name="Nullable">Whether the column is nullable.</param>
/// <param name="DefaultSql">The default SQL expression for the dialect.</param>
/// <param name="Identity">Whether the column is an identity column.</param>
/// <param name="Computed">The computed expression.</param>
/// <param name="AttributeId">The id of the attribute the column stores, if any.</param>
/// <param name="AttributePath">The attribute path the column stores, if any.</param>
/// <param name="IsPrimaryKey">Whether the column is part of the primary key.</param>
/// <param name="IsForeignKey">Whether the column is part of a foreign key.</param>
/// <param name="IsDiscriminator">Whether the column is a TPH discriminator.</param>
/// <param name="Position">The 0-based position in the table.</param>
public sealed record ColumnView(
    string Key,
    string Name,
    string Type,
    string NativeType,
    int? Length,
    int? Precision,
    int? Scale,
    bool Nullable,
    string? DefaultSql,
    bool Identity,
    string? Computed,
    string? AttributeId,
    string? AttributePath,
    bool IsPrimaryKey,
    bool IsForeignKey,
    bool IsDiscriminator,
    int Position);

/// <summary>A primary key or unique constraint.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Columns">The column keys, in key order.</param>
public sealed record KeyView(string Name, IReadOnlyList<string> Columns);

/// <summary>A foreign key.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Columns">The referencing column keys.</param>
/// <param name="ReferencedTable">The referenced table's key.</param>
/// <param name="ReferencedColumns">The referenced column keys.</param>
/// <param name="OnDelete"><c>no-action</c>, <c>restrict</c>, <c>cascade</c>, <c>set-null</c> or <c>set-default</c>.</param>
/// <param name="OnUpdate">The on-update action, as <paramref name="OnDelete"/>.</param>
/// <param name="RelationId">The relation the key implements, if any.</param>
/// <param name="EndId">The relation end whose table holds the key, if any.</param>
public sealed record ForeignKeyView(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string OnDelete,
    string OnUpdate,
    string? RelationId,
    string? EndId);

/// <summary>An index.</summary>
/// <param name="Name">The index name.</param>
/// <param name="Columns">The indexed columns with their sort order.</param>
/// <param name="Unique">Whether the index is unique.</param>
/// <param name="Where">The partial-index predicate.</param>
public sealed record IndexView(string Name, IReadOnlyList<IndexColumnView> Columns, bool Unique, string? Where);

/// <summary>A column of an index.</summary>
/// <param name="Column">The column key.</param>
/// <param name="Descending">Whether the column sorts descending.</param>
public sealed record IndexColumnView(string Column, bool Descending);

/// <summary>Projects a resolved database into flat <see cref="DatabaseView"/> records (E1).</summary>
internal static class DatabaseViews
{
    /// <summary>Projects one resolved database.</summary>
    /// <param name="database">The resolved database.</param>
    /// <returns>The view.</returns>
    public static DatabaseView From(RDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new DatabaseView(database.Id, database.Name, database.Dialect, database.Version, database.DefaultSchema,
            [.. database.Tables.Select(Table)]);
    }

    public static TableView Table(RTable table) => new(
        table.Key,
        table.Name,
        table.Schema,
        table.Origin,
        table.Entity?.Id,
        table.Relation?.Id,
        table.IsJunction,
        false, // the enum lookup-table option is retired (MQ7012)
        table.Comment,
        [.. table.Columns.Select(Column)],
        table.PrimaryKey is { } key ? new KeyView(key.Name, [.. key.Columns.Select(c => c.Key)]) : null,
        [.. table.Uniques.Select(u => new KeyView(u.Name, [.. u.Columns.Select(c => c.Key)]))],
        [.. table.ForeignKeys.Select(ForeignKey)],
        [.. table.Indexes.Select(i => new IndexView(i.Name, [.. i.Columns.Select(c => new IndexColumnView(c.Column.Key, c.Descending))], i.Unique, i.Where))]);

    private static ColumnView Column(RColumn column) => new(
        column.Key,
        column.Name,
        column.Type,
        column.NativeType,
        column.Length,
        column.Precision,
        column.Scale,
        column.Nullable,
        column.DefaultSql,
        column.Identity,
        column.Computed,
        column.Attribute?.Id,
        column.AttributePath,
        column.IsPrimaryKey,
        column.IsForeignKey,
        column.IsDiscriminator,
        column.Position);

    private static ForeignKeyView ForeignKey(RForeignKey key) => new(
        key.Name,
        [.. key.Columns.Select(c => c.Key)],
        key.ReferencedTable.Key,
        [.. key.ReferencedColumns.Select(c => c.Key)],
        key.OnDelete,
        key.OnUpdate,
        key.Relation?.Id,
        key.End?.Id);
}
