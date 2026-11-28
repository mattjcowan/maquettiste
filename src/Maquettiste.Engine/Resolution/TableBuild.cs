using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>A table under construction in one database run: its file, columns by key, pending constraints and dependencies.</summary>
internal sealed class TableBuild
{
    public TableBuild(DependencyKeyCache keys, RTable table, Table source)
    {
        Deps = new DependencySet(keys);
        Table = table;
        Source = source;
        table.FileId = source.Id;
    }

    public RTable Table { get; }

    /// <summary>The table file.</summary>
    public Table Source { get; }

    /// <summary>The element diagnostics about the table point at: the table file's id.</summary>
    public string SourceElementId => Source.Id;

    public List<RColumn> Columns { get; } = [];

    /// <summary>Columns by key (a column id, or the keyed id of an inherited column).</summary>
    public Dictionary<string, RColumn> ByKey { get; } = new(StringComparer.Ordinal);

    public List<string> PrimaryKey { get; } = [];

    public string? PrimaryKeyName { get; set; }

    /// <summary>The <c>clustered</c> flag of the table file's primary key, when it sets one.</summary>
    public bool? PrimaryKeyClustered { get; set; }

    /// <summary>Whether a table file makes the primary key temporal (WITHOUT OVERLAPS).</summary>
    public bool PrimaryKeyWithoutOverlaps { get; set; }

    /// <summary>The primary key's id: the table file's, or the keyed id of an inherited key; <see langword="null"/> without one.</summary>
    public string? PrimaryKeyId { get; set; }

    /// <summary>Where the primary key comes from: <see langword="null"/> for the table's own, else the stereotype that contributes it.</summary>
    public RMemberOrigin? PrimaryKeyOrigin { get; set; }

    public List<UniqueSpec> Uniques { get; } = [];

    public List<ForeignKeySpec> ForeignKeys { get; } = [];

    public List<IndexSpec> Indexes { get; } = [];

    public List<CheckSpec> Checks { get; } = [];

    /// <summary>The table file's partitioning, resolved once the columns are.</summary>
    public PartitionBy? PartitionBy { get; set; }

    /// <summary>The exclusion constraints the table file declares.</summary>
    public List<ExclusionConstraint> Exclusions { get; } = [];

    public DependencySet Deps { get; }

    /// <summary>
    /// What the table's effective stereotype set is built from (next-phase-2-design.md section 3.1): its file's holder
    /// (<see cref="ModelSnapshot.HolderOf"/>). Set by <c>DatabaseRun.AddTable</c>.
    /// </summary>
    public StereotypeHolder Holder { get; set; } = null!;

    /// <summary>The table's effective stereotype set: <see cref="ModelSnapshot.EffectiveStereotypes(StereotypeHolder)"/> of <see cref="Holder"/>.</summary>
    public IReadOnlyList<AppliedStereotype> Effective { get; set; } = [];

    /// <summary>A column by key or id.</summary>
    public RColumn? Resolve(string key) => ByKey.GetValueOrDefault(key);
}

/// <summary>A unique constraint to resolve; <paramref name="Origin"/> is <see langword="null"/> for the table's own, else the stereotype that
/// contributes it (next-phase-2-design.md section 3.1).</summary>
internal sealed record UniqueSpec(IReadOnlyList<string> Columns, string? Name, string? NameToken, string? Id = null, bool NullsNotDistinct = false,
    bool WithoutOverlaps = false, RMemberOrigin? Origin = null);

/// <summary>An index to resolve; <paramref name="Origin"/> is <see langword="null"/> for the table's own, else the stereotype that
/// contributes it.</summary>
internal sealed record IndexSpec(IReadOnlyList<IndexColumnSpec> Columns, IReadOnlyList<string> Include, string? Where, bool Unique,
    string Method, string? Name, bool FromFile = false, string? Id = null, IReadOnlyList<RStorageParameter>? Storage = null,
    RMemberOrigin? Origin = null);

/// <summary>
/// A column of an index to resolve: a column key, or an expression for the dialect (<paramref name="Column"/> is then
/// <see langword="null"/>). <paramref name="Nulls"/> is <c>first</c> or <c>last</c> on PostgreSQL when it differs from the
/// direction's default, else <see langword="null"/>.
/// </summary>
internal sealed record IndexColumnSpec(string? Column, bool Descending, string? Expression = null, int? Length = null, string? OperatorClass = null,
    string? Nulls = null);

/// <summary>A check constraint to resolve; <paramref name="Origin"/> is <see langword="null"/> for the table's own, else the stereotype that
/// contributes it.</summary>
internal sealed record CheckSpec(string Expression, string? Name, int Ordinal, string? Id = null, string? Column = null, RMemberOrigin? Origin = null)
{
    /// <summary>A check written in the expression language: its typed tree, lowered once the table's columns are final; else <see langword="null"/>.</summary>
    public TreeCheck? Tree { get; init; }

    /// <summary>The custom properties of the check as written.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> Properties { get; init; } = System.Collections.Frozen.FrozenDictionary<string, System.Text.Json.JsonElement>.Empty;
}

/// <summary>A table's check written in the expression language (next-phase-3-design.md section 3.1), typed when its file is read.</summary>
/// <param name="Source">The tree, typed in the scope of the file that holds it.</param>
/// <param name="KeyPrefix">For a check a stereotype contributes, the table key its columns are keyed under; else <see langword="null"/>.</param>
/// <param name="ElementId">The element a finding about it goes on.</param>
/// <param name="Pointer">Its pointer there.</param>
internal sealed record TreeCheck(ExprRoot Source, string? KeyPrefix, string ElementId, string? Pointer);

/// <summary>A foreign key to resolve; <see cref="Result"/> exists from the start so mappings can hold it.</summary>
internal sealed class ForeignKeySpec(TableBuild host, IReadOnlyList<string> columns, TableBuild? target, string? targetKey,
    IReadOnlyList<string> targetColumns, string onDelete, string onUpdate, string? name)
{
    public TableBuild Host { get; } = host;
    public IReadOnlyList<string> Columns { get; } = columns;
    public TableBuild? Target { get; set; } = target;
    public string? TargetKey { get; } = targetKey;
    public IReadOnlyList<string> TargetColumns { get; } = targetColumns;
    public string OnDelete { get; } = onDelete;
    public string OnUpdate { get; } = onUpdate;
    public string? Name { get; } = name;
    public RForeignKey Result { get; } = new() { OnDelete = onDelete, OnUpdate = onUpdate };

    /// <summary>The columns (of <see cref="Columns"/>) the on-delete action sets; empty sets them all.</summary>
    public IReadOnlyList<string> OnDeleteColumns { get; init; } = [];

    /// <summary>Whether the key resolved to real columns and a real target.</summary>
    public bool Resolved { get; set; }

    /// <summary>The table file that declares the key.</summary>
    public string? FileId { get; init; }

    /// <summary>The key's id in its file, or an inherited key's keyed id.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// For a key a stereotype contributes (next-phase-2-design.md section 3.1): the member's id and the effective-set entry that gives
    /// it, which place a finding about it; <see langword="null"/> for the table's own. Its origin is on <see cref="Result"/>.
    /// </summary>
    public (string MemberId, AppliedStereotype From)? Inherited { get; init; }
}

/// <summary>Where a bound entity's rows live in one database: its binding's write (else source) table.</summary>
internal sealed class Placement(REntity entity, Entity source)
{
    public REntity Entity { get; } = entity;
    public Entity Source { get; } = source;

    /// <summary>The in-scope hierarchy root.</summary>
    public Placement Root { get; set; } = null!;

    /// <summary>The in-scope base, if any.</summary>
    public Placement? Base { get; set; }

    public List<Placement> Derived { get; } = [];

    /// <summary>The table holding the entity's rows: its binding's write table, else its source table.</summary>
    public TableBuild Table { get; set; } = null!;

    /// <summary>The key attribute ids of the hierarchy root.</summary>
    public HashSet<string> KeyIds { get; } = new(StringComparer.Ordinal);
}
