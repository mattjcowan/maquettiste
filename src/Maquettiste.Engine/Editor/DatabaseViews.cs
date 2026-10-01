using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>The result of <see cref="GenerationService.GetDatabaseViewAsync"/> (E1, phase2-design.md section 3.8).</summary>
/// <param name="View">The resolved database, or <see langword="null"/> when the model has errors or the database is unknown.</param>
/// <param name="Diagnostics">Validation and resolution diagnostics; the errors that prevented the view when it is <see langword="null"/>.</param>
public sealed record DatabaseViewResult(DatabaseView? View, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The resolved physical model of one database: every table after conventions, mappings and overlays, and every view and sequence.</summary>
/// <param name="Id">The database element's id.</param>
/// <param name="Name">The database name.</param>
/// <param name="Dialect"><c>postgresql</c>, <c>sqlserver</c>, <c>mysql</c>, <c>sqlite</c> or <c>oracle</c>.</param>
/// <param name="Version">The target version.</param>
/// <param name="DefaultSchema">The effective default schema, or <see langword="null"/> when the dialect has none.</param>
/// <param name="Tables">Every table, in the resolver's (schema, name) order.</param>
/// <param name="Views">Every view, in the resolver's (schema, name) order.</param>
/// <param name="Sequences">Every sequence (the sequence files and the sequences the resolver creates for entity keys), in the resolver's
/// (schema, name) order.</param>
/// <param name="Schemas">Every schema, by name: the ones the database file declares and the ones its tables, views and sequences use.</param>
/// <param name="Quoting"><c>always</c>, <c>reserved</c> or <c>never</c>.</param>
/// <param name="MaxIdentifierLength">The effective identifier length limit, or <see langword="null"/> for none.</param>
/// <param name="ByConvention">Which entities map here by convention: <c>all</c>, <c>packages</c> or <c>none</c> (the effective value).</param>
/// <param name="Packages">The packages whose entities map here by convention, with the schema their tables go to.</param>
/// <param name="DisplayName">The display name the database file sets, or <see langword="null"/> (no fallback).</param>
/// <param name="PluralName">The plural name the database file sets, or <see langword="null"/>.</param>
/// <param name="Description">As <see cref="TableView.Description"/>.</param>
/// <param name="Stereotypes">As <see cref="TableView.Stereotypes"/>.</param>
/// <param name="Tags">As <see cref="TableView.Tags"/>.</param>
/// <param name="Category">As <see cref="TableView.Category"/>.</param>
/// <param name="Properties">As <see cref="TableView.Properties"/>.</param>
/// <param name="Generation">As <see cref="TableView.Generation"/>.</param>
public sealed record DatabaseView(string Id, string Name, string Dialect, string? Version, string? DefaultSchema, IReadOnlyList<TableView> Tables,
    IReadOnlyList<ViewView> Views, IReadOnlyList<SequenceView> Sequences, IReadOnlyList<SchemaView> Schemas, string Quoting, int? MaxIdentifierLength,
    string ByConvention, IReadOnlyList<ConventionPackageView> Packages, string? DisplayName, string? PluralName, string? Description,
    IReadOnlyList<string> Stereotypes, IReadOnlyList<string> Tags, string? Category, IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>One schema of a resolved database, projected from <see cref="RSchema"/>.</summary>
/// <param name="Id">The schema's id in the database file, or <c>&lt;databaseId&gt;/&lt;name&gt;</c> for a schema the file does not declare.</param>
/// <param name="Name">The schema name.</param>
/// <param name="IsDefault">Whether it is the database's effective default schema.</param>
/// <param name="IsDeclared">Whether the database file declares it (only a declared schema carries annotations).</param>
/// <param name="DisplayName">The display name the schema entry sets, or <see langword="null"/>.</param>
/// <param name="PluralName">The plural name the schema entry sets, or <see langword="null"/>.</param>
/// <param name="Description">As <see cref="TableView.Description"/>.</param>
/// <param name="Stereotypes">As <see cref="TableView.Stereotypes"/>.</param>
/// <param name="Tags">As <see cref="TableView.Tags"/>.</param>
/// <param name="Category">As <see cref="TableView.Category"/>.</param>
/// <param name="Properties">As <see cref="TableView.Properties"/>.</param>
/// <param name="Generation">As <see cref="TableView.Generation"/>.</param>
public sealed record SchemaView(
    string Id,
    string Name,
    bool IsDefault,
    bool IsDeclared,
    string? DisplayName,
    string? PluralName,
    string? Description,
    IReadOnlyList<string> Stereotypes,
    IReadOnlyList<string> Tags,
    string? Category,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>One package a database takes by convention.</summary>
/// <param name="PackageId">The package id.</param>
/// <param name="Schema">The schema name its conventional tables go to, or <see langword="null"/> for the default schema.</param>
public sealed record ConventionPackageView(string PackageId, string? Schema);

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
/// <param name="DisplayName">The display name the table's file sets, or <see langword="null"/> (no fallback; a synthesized table
/// without an overlay has no file).</param>
/// <param name="PluralName">The plural name the table's file sets, or <see langword="null"/>.</param>
/// <param name="Description">The description text (a sidecar file loaded), or <see langword="null"/>.</param>
/// <param name="Stereotypes">The stereotype keys, in application order.</param>
/// <param name="Tags">The tag keys.</param>
/// <param name="Category">The category's id, or <see langword="null"/>.</param>
/// <param name="Properties">The custom properties: stereotype defaults merged under the file's own.</param>
/// <param name="Generation">The generation hints, by pack name or <c>"*"</c>.</param>
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
    IReadOnlyList<IndexView> Indexes,
    string? DisplayName,
    string? PluralName,
    string? Description,
    IReadOnlyList<string> Stereotypes,
    IReadOnlyList<string> Tags,
    string? Category,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>One resolved view, projected from <see cref="RView"/>.</summary>
/// <param name="Id">The view file's id.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Schema">The schema name, or <see langword="null"/>.</param>
/// <param name="Body">The body for the database's dialect.</param>
/// <param name="Columns">The declared columns.</param>
/// <param name="Comment">The comment.</param>
/// <param name="DisplayName">As <see cref="TableView.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="TableView.PluralName"/>.</param>
/// <param name="Description">As <see cref="TableView.Description"/>.</param>
/// <param name="Stereotypes">As <see cref="TableView.Stereotypes"/>.</param>
/// <param name="Tags">As <see cref="TableView.Tags"/>.</param>
/// <param name="Category">As <see cref="TableView.Category"/>.</param>
/// <param name="Properties">As <see cref="TableView.Properties"/>.</param>
/// <param name="Generation">As <see cref="TableView.Generation"/>.</param>
public sealed record ViewView(
    string Id,
    string Name,
    string? Schema,
    string Body,
    IReadOnlyList<ViewColumnView> Columns,
    string? Comment,
    string? DisplayName,
    string? PluralName,
    string? Description,
    IReadOnlyList<string> Stereotypes,
    IReadOnlyList<string> Tags,
    string? Category,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A declared column of a view.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The built-in type keyword, or <see langword="null"/>.</param>
/// <param name="NativeType">The dialect's type, or <see langword="null"/>.</param>
/// <param name="Nullable">Whether the column is nullable.</param>
public sealed record ViewColumnView(string Name, string? Type, string? NativeType, bool Nullable);

/// <summary>One resolved sequence, projected from <see cref="RSequence"/>.</summary>
/// <param name="Id">The sequence file's id, or a synthesized key such as <c>&lt;entityId&gt;.sequence@&lt;databaseId&gt;</c>.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Schema">The schema name, or <see langword="null"/>.</param>
/// <param name="Type">The built-in integer type keyword.</param>
/// <param name="NativeType">The dialect's type.</param>
/// <param name="Start">The first value.</param>
/// <param name="Increment">The increment.</param>
/// <param name="Min">The minimum value.</param>
/// <param name="Max">The maximum value.</param>
/// <param name="Cycle">Whether the sequence wraps around.</param>
/// <param name="Cache">How many values the server caches.</param>
/// <param name="DisplayName">As <see cref="TableView.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="TableView.PluralName"/>.</param>
/// <param name="Description">As <see cref="TableView.Description"/>.</param>
/// <param name="Stereotypes">As <see cref="TableView.Stereotypes"/>.</param>
/// <param name="Tags">As <see cref="TableView.Tags"/>.</param>
/// <param name="Category">As <see cref="TableView.Category"/>.</param>
/// <param name="Properties">As <see cref="TableView.Properties"/>.</param>
/// <param name="Generation">As <see cref="TableView.Generation"/>.</param>
public sealed record SequenceView(
    string Id,
    string Name,
    string? Schema,
    string Type,
    string NativeType,
    long Start,
    long Increment,
    long? Min,
    long? Max,
    bool Cycle,
    int? Cache,
    string? DisplayName,
    string? PluralName,
    string? Description,
    IReadOnlyList<string> Stereotypes,
    IReadOnlyList<string> Tags,
    string? Category,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

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
/// <param name="Default">The literal default, as a plain value, or <see langword="null"/>.</param>
/// <param name="ComputedStored">Whether a computed value is stored.</param>
/// <param name="SequenceId">The id (or synthesized key) of the sequence that supplies values, or <see langword="null"/>.</param>
/// <param name="Collation">The collation, or <see langword="null"/>.</param>
/// <param name="Comment">The comment, or <see langword="null"/>.</param>
/// <param name="DisplayName">The display name the column's own entry sets (a designed or extra column, or a synthesized column's overlay
/// entry), or <see langword="null"/>; a synthesized column never takes its attribute's, which <paramref name="AttributeId"/> leads to.</param>
/// <param name="PluralName">The plural name the column's own entry sets, or <see langword="null"/>.</param>
/// <param name="Description">The description the column's own entry sets, or <see langword="null"/>.</param>
/// <param name="Stereotypes">As <see cref="TableView.Stereotypes"/>, from the column's own entry.</param>
/// <param name="Tags">As <see cref="TableView.Tags"/>, from the column's own entry.</param>
/// <param name="Category">As <see cref="TableView.Category"/>, from the column's own entry.</param>
/// <param name="Properties">As <see cref="TableView.Properties"/>, from the column's own entry.</param>
/// <param name="Generation">As <see cref="TableView.Generation"/>, from the column's own entry.</param>
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
    int Position,
    object? Default,
    bool ComputedStored,
    string? SequenceId,
    string? Collation,
    string? Comment,
    string? DisplayName,
    string? PluralName,
    string? Description,
    IReadOnlyList<string> Stereotypes,
    IReadOnlyList<string> Tags,
    string? Category,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

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
            [.. database.Tables.Select(Table)], [.. database.Views.Select(View)], [.. database.Sequences.Select(Sequence)],
            [.. database.Schemas.Select(Schema)], database.Quoting, database.MaxIdentifierLength, database.ByConvention,
            [.. database.Packages.Select(p => new ConventionPackageView(p.PackageId, p.Schema))],
            OrNull(database.DisplayName), OrNull(database.PluralName), database.Description, [.. database.Stereotypes.Select(s => s.Key)],
            database.Tags, database.Category?.Id, database.Properties, database.Generation);
    }

    private static SchemaView Schema(RSchema schema) => new(
        schema.Id,
        schema.Name,
        schema.IsDefault,
        schema.IsDeclared,
        OrNull(schema.DisplayName),
        OrNull(schema.PluralName),
        schema.Description,
        [.. schema.Stereotypes.Select(s => s.Key)],
        schema.Tags,
        schema.Category?.Id,
        schema.Properties,
        schema.Generation);

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
        [.. table.Indexes.Select(i => new IndexView(i.Name, [.. i.Columns.Select(c => new IndexColumnView(c.Column.Key, c.Descending))], i.Unique, i.Where))],
        OrNull(table.DisplayName),
        OrNull(table.PluralName),
        table.Description,
        [.. table.Stereotypes.Select(s => s.Key)],
        table.Tags,
        table.Category?.Id,
        table.Properties,
        table.Generation);

    private static ViewView View(RView view) => new(
        view.Id,
        view.Name,
        view.Schema,
        view.Body,
        [.. view.Columns.Select(c => new ViewColumnView(c.Name, c.Type, c.NativeType, c.Nullable))],
        view.Comment,
        OrNull(view.DisplayName),
        OrNull(view.PluralName),
        view.Description,
        [.. view.Stereotypes.Select(s => s.Key)],
        view.Tags,
        view.Category?.Id,
        view.Properties,
        view.Generation);

    private static SequenceView Sequence(RSequence sequence) => new(
        sequence.Id,
        sequence.Name,
        sequence.Schema,
        sequence.Type,
        sequence.NativeType,
        sequence.Start,
        sequence.Increment,
        sequence.Min,
        sequence.Max,
        sequence.Cycle,
        sequence.Cache,
        OrNull(sequence.DisplayName),
        OrNull(sequence.PluralName),
        sequence.Description,
        [.. sequence.Stereotypes.Select(s => s.Key)],
        sequence.Tags,
        sequence.Category?.Id,
        sequence.Properties,
        sequence.Generation);

    private static string? OrNull(string value) => value.Length == 0 ? null : value;

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
        column.Position,
        column.Default,
        column.ComputedStored,
        column.Sequence?.Id,
        column.Collation,
        column.Comment,
        OrNull(column.DisplayName),
        OrNull(column.PluralName),
        column.Description,
        [.. column.Stereotypes.Select(s => s.Key)],
        column.Tags,
        column.Category?.Id,
        column.Properties,
        column.Generation);

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
