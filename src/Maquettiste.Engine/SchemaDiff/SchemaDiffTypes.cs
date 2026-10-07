using System.Text.Json.Serialization;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.SchemaDiff;

/// <summary>How a physical object changed between the snapshot and the current model.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
public enum ChangeKind
{
    /// <summary><c>added</c>.</summary>
    [JsonStringEnumMemberName("added")] Added,

    /// <summary><c>dropped</c>.</summary>
    [JsonStringEnumMemberName("dropped")] Dropped,

    /// <summary>Same key, new name (and possibly property changes): <c>renamed</c>.</summary>
    [JsonStringEnumMemberName("renamed")] Renamed,

    /// <summary>Same key and name, changed properties: <c>altered</c>.</summary>
    [JsonStringEnumMemberName("altered")] Altered,
}

/// <summary>One changed property.</summary>
/// <param name="Property">The property name, for example <c>nullable</c> or <c>nativeType</c>.</param>
/// <param name="Old">The old value.</param>
/// <param name="New">The new value.</param>
public sealed record PropertyChange(string Property, object? Old, object? New);

/// <summary>A column change.</summary>
/// <param name="Kind">The change kind.</param>
/// <param name="Key">The column key.</param>
/// <param name="OldName">The old name (not for added).</param>
/// <param name="NewName">The new name (not for dropped).</param>
/// <param name="Changes">Property changes.</param>
/// <param name="Column">The current column (not for dropped).</param>
public sealed record ColumnChange(ChangeKind Kind, string Key, string? OldName, string? NewName, IReadOnlyList<PropertyChange> Changes, RColumn? Column)
{
    /// <summary>
    /// The name the committed snapshot gives the column's default constraint (not for added; <see langword="null"/> for the template's
    /// convention), so a migration can rename or drop the constraint the database has.
    /// </summary>
    public string? OldDefaultName { get; init; }
}

/// <summary>A change to a key, constraint, index, view or sequence.</summary>
/// <param name="Kind">The change kind.</param>
/// <param name="Key">The object key.</param>
/// <param name="OldName">The old name (not for added).</param>
/// <param name="NewName">The new name (not for dropped).</param>
/// <param name="Changes">Property changes.</param>
public sealed record ObjectChange(ChangeKind Kind, string Key, string? OldName, string? NewName, IReadOnlyList<PropertyChange> Changes)
{
    /// <summary>For a view or sequence (not added): its schema name before the change, or <see langword="null"/> for none.</summary>
    public string? OldSchema { get; init; }

    /// <summary>For a view (not added): the keys of the views it read before the change, so a migration drops it before them.</summary>
    public IReadOnlyList<string> OldDependsOn { get; init; } = [];

    /// <summary>For a view (not added): whether it was materialized, so a migration drops it as such.</summary>
    public bool OldMaterialized { get; init; }

    /// <summary>
    /// For a unique constraint or an index (not added): its column keys before the change, in order (an index's expression column is
    /// <c>null</c>), so a migration knows which foreign keys rely on a key it drops.
    /// </summary>
    public IReadOnlyList<string?> OldColumns { get; init; } = [];

    /// <summary>For a unique constraint or an index (not added): whether it was a key a foreign key can rely on (an index: unique, without a filter).</summary>
    public bool OldUnique { get; init; }

    /// <summary>For an index (not added): its storage parameters before the change, so a migration resets the ones that went.</summary>
    public IReadOnlyList<RStorageParameter> OldStorage { get; init; } = [];
}

/// <summary>A table change.</summary>
/// <param name="Kind">The change kind.</param>
/// <param name="Key">The table key.</param>
/// <param name="OldName">The old name (not for added).</param>
/// <param name="NewName">The new name (not for dropped).</param>
/// <param name="Table">The current table (not for dropped).</param>
/// <param name="Columns">Column changes.</param>
/// <param name="PrimaryKey">Primary key changes.</param>
/// <param name="Uniques">Unique constraint changes.</param>
/// <param name="ForeignKeys">Foreign key changes.</param>
/// <param name="Checks">Check constraint changes.</param>
/// <param name="Indexes">Index changes.</param>
public sealed record TableChange(
    ChangeKind Kind,
    string Key,
    string? OldName,
    string? NewName,
    RTable? Table,
    IReadOnlyList<ColumnChange> Columns,
    IReadOnlyList<ObjectChange> PrimaryKey,
    IReadOnlyList<ObjectChange> Uniques,
    IReadOnlyList<ObjectChange> ForeignKeys,
    IReadOnlyList<ObjectChange> Checks,
    IReadOnlyList<ObjectChange> Indexes)
{
    /// <summary>The table's schema name before the change (not for added), or <see langword="null"/> for none.</summary>
    public string? OldSchema { get; init; }

    /// <summary>The table's comment before the change (not for added), or <see langword="null"/> for none.</summary>
    public string? OldComment { get; init; }

    /// <summary>Exclusion constraint changes.</summary>
    public IReadOnlyList<ObjectChange> Exclusions { get; init; } = [];

    /// <summary>Partition changes (key: the partition's id); a changed partition's property is <c>bounds</c>.</summary>
    public IReadOnlyList<ObjectChange> Partitions { get; init; } = [];

    /// <summary>Changes to the table's own properties that have no list of their own: <c>storage</c>, <c>partitionBy</c>.</summary>
    public IReadOnlyList<PropertyChange> Changes { get; init; } = [];

    /// <summary>The table's storage parameters before the change (not for added), so a migration resets the ones that went.</summary>
    public IReadOnlyList<RStorageParameter> OldStorage { get; init; } = [];
}

/// <summary>
/// The structured diff of one database, as templates see it in <c>schema_diff</c>. <see cref="Tables"/> lists added tables in
/// foreign key dependency order, then renamed, then altered, then dropped in reverse dependency order.
/// </summary>
/// <param name="Database">The database name.</param>
/// <param name="FromRevision">The snapshot's revision.</param>
/// <param name="ToRevision"><paramref name="FromRevision"/> + 1 when the diff is non-empty, else equal.</param>
/// <param name="IsEmpty">Whether nothing changed.</param>
/// <param name="Hash">The diff's hash (the <c>d:&lt;databaseId&gt;</c> dependency key).</param>
/// <param name="Tables">Table changes.</param>
/// <param name="Views">View changes.</param>
/// <param name="Sequences">Sequence changes.</param>
public sealed record SchemaDiffResult(
    string Database,
    int FromRevision,
    int ToRevision,
    bool IsEmpty,
    string Hash,
    IReadOnlyList<TableChange> Tables,
    IReadOnlyList<ObjectChange> Views,
    IReadOnlyList<ObjectChange> Sequences)
{
    /// <summary>
    /// Declared schema changes (key: the schema's id): added, renamed, then dropped, each by key. Empty when either snapshot
    /// predates schema recording, or there is no previous snapshot (a first migration creates every schema with its tables).
    /// </summary>
    public IReadOnlyList<ObjectChange> Schemas { get; init; } = [];

    /// <summary>Database type changes: added, renamed, altered, then dropped, each by key.</summary>
    public IReadOnlyList<DefinitionChange> Types { get; init; } = [];

    /// <summary>Routine changes, as <see cref="Types"/>.</summary>
    public IReadOnlyList<DefinitionChange> Routines { get; init; } = [];

    /// <summary>SQL object changes, as <see cref="Types"/>.</summary>
    public IReadOnlyList<DefinitionChange> Objects { get; init; } = [];
}

/// <summary>A change to a routine, database type or SQL object.</summary>
/// <param name="Kind">The change kind.</param>
/// <param name="Key">The object's key (its id).</param>
/// <param name="OldName">The old name (not for added).</param>
/// <param name="NewName">The new name (not for dropped).</param>
/// <param name="OldKind">What the object was (<c>function</c>, <c>domain</c>, the SQL object's kind...), so a migration can drop it (not for added).</param>
/// <param name="NewKind">What it is now (not for dropped).</param>
/// <param name="OldSchema">The old schema name (not for added).</param>
/// <param name="Changes">Property changes: <c>schema</c>, <c>kind</c>, <c>definition</c>.</param>
public sealed record DefinitionChange(ChangeKind Kind, string Key, string? OldName, string? NewName, string? OldKind, string? NewKind, string? OldSchema,
    IReadOnlyList<PropertyChange> Changes);
