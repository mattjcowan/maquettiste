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
public sealed record ColumnChange(ChangeKind Kind, string Key, string? OldName, string? NewName, IReadOnlyList<PropertyChange> Changes, RColumn? Column);

/// <summary>A change to a key, constraint, index, view or sequence.</summary>
/// <param name="Kind">The change kind.</param>
/// <param name="Key">The object key.</param>
/// <param name="OldName">The old name (not for added).</param>
/// <param name="NewName">The new name (not for dropped).</param>
/// <param name="Changes">Property changes.</param>
public sealed record ObjectChange(ChangeKind Kind, string Key, string? OldName, string? NewName, IReadOnlyList<PropertyChange> Changes);

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
    IReadOnlyList<ObjectChange> Indexes);

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
    IReadOnlyList<ObjectChange> Sequences);
