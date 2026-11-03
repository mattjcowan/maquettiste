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
/// foreign key dependency order, then renamed, then altered, then dropped in reverse dependency order. <see cref="Facts"/> lists the
/// migration facts that render with it (next-phase-1-design.md section 3.2, "Facts in the schema diff").
/// </summary>
/// <param name="Database">The database name.</param>
/// <param name="FromRevision">The snapshot's revision.</param>
/// <param name="ToRevision"><paramref name="FromRevision"/> + 1 when the diff is non-empty, else equal.</param>
/// <param name="IsEmpty">Whether nothing changed and no migration fact renders (<see cref="Facts"/> is empty).</param>
/// <param name="Hash">The diff's hash (the <c>d:&lt;databaseId&gt;</c> dependency key); it covers the ids and content hashes of
/// <see cref="Facts"/> when there are some.</param>
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

    /// <summary>
    /// The migration facts that render with this diff, in applied order: the database's complete and released facts that the baseline
    /// lacks (with the committed snapshot, those its <see cref="Model.PhysicalSnapshot.Facts"/> does not list; with a baseline model,
    /// those that model lacks or holds as held and not released). Pending facts, and held facts not released, are never listed.
    /// </summary>
    public IReadOnlyList<RMigration> Facts { get; init; } = [];

    /// <summary>
    /// Whether the diff runs from a baseline model (<c>generate --against</c>) rather than the committed snapshot: nothing counts
    /// revisions then, so a migration is named by its content alone (next-phase-1-design.md section 3.2, decision 40).
    /// </summary>
    public bool FromBaseline { get; init; }

    /// <summary>
    /// The state the diff starts from: 12 lowercase hex digits of SHA-256 over the canonical form of the baseline's capture as stored
    /// (before the alias and <c>rekey</c> steps), without its revision, aliases and facts, so it equals the <see cref="ToState"/> of
    /// the diff that produced that baseline; empty when there is no baseline (a diff from nothing). Computed on first read.
    /// </summary>
    public string FromState
    {
        get => _fromState.Value;
        init => _fromState = LazyState.Of(value);
    }

    /// <summary>
    /// The state the diff ends at: as <see cref="FromState"/>, over the capture of the current model. Computed on first read.
    /// </summary>
    public string ToState
    {
        get => _toState.Value;
        init => _toState = LazyState.Of(value);
    }

    private readonly LazyState _fromState = LazyState.Empty;
    private readonly LazyState _toState = LazyState.Empty;

    private LazyState FromStateValue
    {
        get => _fromState;
        init => _fromState = value;
    }

    private LazyState ToStateValue
    {
        get => _toState;
        init => _toState = value;
    }

    /// <summary>
    /// The diff with <see cref="FromState"/> and <see cref="ToState"/> computed on first read: the canonical form of a large capture is
    /// costly, and an empty diff renders nothing, so a run that never reads them pays nothing.
    /// </summary>
    /// <param name="from">Computes <see cref="FromState"/>.</param>
    /// <param name="to">Computes <see cref="ToState"/>.</param>
    /// <returns>The diff.</returns>
    internal SchemaDiffResult WithStates(Func<string> from, Func<string> to) =>
        this with { FromStateValue = LazyState.Of(from), ToStateValue = LazyState.Of(to) };

    /// <summary>A state hash given, or computed once on first read (thread-safe); equal when the values are equal.</summary>
    private sealed class LazyState : IEquatable<LazyState>
    {
        private readonly Lazy<string> _value;

        private LazyState(Lazy<string> value) => _value = value;

        public static LazyState Empty { get; } = Of("");

        public string Value => _value.Value;

        public static LazyState Of(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return new LazyState(new Lazy<string>(() => value, LazyThreadSafetyMode.PublicationOnly));
        }

        public static LazyState Of(Func<string> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            return new LazyState(new Lazy<string>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
        }

        public bool Equals(LazyState? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as LazyState);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    }
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
