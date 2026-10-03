using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>A table under construction in one database run: columns by key, pending constraints, overlay and dependencies.</summary>
internal sealed class TableBuild
{
    public TableBuild(DependencyKeyCache keys, RTable table, Table? source, Table? overlay, string sourceElementId)
    {
        Deps = new DependencySet(keys);
        Table = table;
        Source = source;
        Overlay = overlay;
        SourceElementId = sourceElementId;
        if (overlay is not null)
        {
            foreach (var column in overlay.Columns)
            {
                if (column.Attribute is { } key)
                    OverlayColumns.TryAdd(key, column);
            }
        }
    }

    public RTable Table { get; }

    /// <summary>The designed or imported table file, when the table is one.</summary>
    public Table? Source { get; }

    /// <summary>The overlay file of a synthesized table.</summary>
    public Table? Overlay { get; }

    /// <summary>The element diagnostics about the table point at (entity, relation, enum or table id).</summary>
    public string SourceElementId { get; }

    public bool IsDesigned => Source is not null;

    public List<RColumn> Columns { get; } = [];

    /// <summary>Columns by key, and by overlay or designed column id.</summary>
    public Dictionary<string, RColumn> ByKey { get; } = new(StringComparer.Ordinal);

    /// <summary>Overlay columns by the synthesized column key they override.</summary>
    public Dictionary<string, Column> OverlayColumns { get; } = new(StringComparer.Ordinal);

    /// <summary>Sequence ids named by overlay columns (<c>generated: sequence</c>), by column key.</summary>
    public Dictionary<string, string> SequenceOverrides { get; } = new(StringComparer.Ordinal);

    public List<string> PrimaryKey { get; } = [];

    public string? PrimaryKeyName { get; set; }

    /// <summary>The <c>clustered</c> flag of the table file's primary key, when it sets one.</summary>
    public bool? PrimaryKeyClustered { get; set; }

    public List<UniqueSpec> Uniques { get; } = [];

    public List<ForeignKeySpec> ForeignKeys { get; } = [];

    public List<IndexSpec> Indexes { get; } = [];

    public List<CheckSpec> Checks { get; } = [];

    /// <summary>Child tables of this table, in creation order.</summary>
    public List<TableBuild> Children { get; } = [];

    /// <summary>Work that needs the primary key (child tables), run once the table's attribute columns exist.</summary>
    public List<Action> Deferred { get; } = [];

    public DependencySet Deps { get; }

    /// <summary>A column by key or id.</summary>
    public RColumn? Resolve(string key) => ByKey.GetValueOrDefault(key);
}

/// <summary>A unique constraint to resolve.</summary>
internal sealed record UniqueSpec(IReadOnlyList<string> Columns, string? Name, string? NameToken, string? Id = null, bool NullsNotDistinct = false);

/// <summary>An index to resolve.</summary>
internal sealed record IndexSpec(IReadOnlyList<IndexColumnSpec> Columns, IReadOnlyList<string> Include, string? Where, bool Unique,
    string Method, string? Name, bool FromFile = false, string? Id = null);

/// <summary>
/// A column of an index to resolve: a column key, or an expression for the dialect (<paramref name="Column"/> is then
/// <see langword="null"/>).
/// </summary>
internal sealed record IndexColumnSpec(string? Column, bool Descending, string? Expression = null, int? Length = null);

/// <summary>A check constraint to resolve.</summary>
internal sealed record CheckSpec(string Expression, string? Name, int Ordinal, string? Id = null, string? Column = null);

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

    /// <summary>Whether the key resolved to real columns and a real target.</summary>
    public bool Resolved { get; set; }

    /// <summary>The designed table or overlay file that declares the key; <see langword="null"/> for a synthesized key.</summary>
    public string? FileId { get; init; }

    /// <summary>The key's id in its file; <see langword="null"/> for a synthesized key.</summary>
    public string? Id { get; init; }
}

/// <summary>Where an entity's rows live in one database.</summary>
internal sealed class Placement(REntity entity, Entity source, Mapping? mapping)
{
    public REntity Entity { get; } = entity;
    public Entity Source { get; } = source;
    public Mapping? Mapping { get; } = mapping;

    /// <summary>The in-scope hierarchy root.</summary>
    public Placement Root { get; set; } = null!;

    /// <summary>The in-scope base, if any.</summary>
    public Placement? Base { get; set; }

    public List<Placement> Derived { get; } = [];

    /// <summary>The hierarchy's strategy, or <see langword="null"/> outside a hierarchy.</summary>
    public InheritanceStrategy? Strategy { get; set; }

    /// <summary>The table holding the entity's key row (the table a foreign key references); <see langword="null"/> for a TPC abstract entity.</summary>
    public TableBuild? Table { get; set; }

    /// <summary>Whether the entity is bound to a designed or imported table (<c>Mapping.Table</c>, or a binding: <see cref="ViaBinding"/>).</summary>
    public bool Bound { get; set; }

    /// <summary>
    /// Whether the placement comes from the entity's binding to the database (erratum E43): the entity is never projected, its
    /// table is the binding's write (else source) table for the foreign keys of relations, and it has no entity mapping.
    /// </summary>
    public bool ViaBinding { get; set; }

    /// <summary>The key attribute ids of the hierarchy root.</summary>
    public HashSet<string> KeyIds { get; } = new(StringComparer.Ordinal);
}
