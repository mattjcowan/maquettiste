using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Loading;

/// <summary>How "Create tables…" stores a hierarchy (retire-projection-design.md section 7).</summary>
internal enum DesignHierarchy
{
    /// <summary>One table for the hierarchy, with a type column.</summary>
    OneTable,

    /// <summary>A table per concrete entity, each holding every attribute.</summary>
    PerConcrete,

    /// <summary>A table per entity, each derived table keyed by its base's (its key is also a foreign key to the base's table).</summary>
    PerType,
}

/// <summary>What a table proposed for an entity is for.</summary>
internal enum DesignedTableKind
{
    /// <summary>An entity's table (or a hierarchy's, for one table).</summary>
    Entity,

    /// <summary>An attribute stored in a table of its own: a list, a single value object, or a list of a reference type.</summary>
    Child,

    /// <summary>A many-to-many relation's junction table.</summary>
    Junction,

    /// <summary>The table that holds a reference type's rows: the lookup table the sql-ddl pack made, now designed and bound.</summary>
    Lookup,
}

/// <summary>A check the designer proposes: the one the sql-ddl pack wrote for a column holding a reference type's codes.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Column">The column's key.</param>
/// <param name="Expression">The condition in the database's dialect.</param>
internal sealed record DesignedCheck(string Name, string Column, string Expression);

/// <summary>What a relation's mapping to the database says about its storage, read by the designer.</summary>
/// <param name="Shape">The shape it asks for, or <see langword="null"/>.</param>
/// <param name="ForeignKeyEnd">The end whose table holds the foreign key, or <see langword="null"/>.</param>
/// <param name="Stored">Whether the mapping already names a designed junction table or foreign key (nothing to design).</param>
internal sealed record DesignRelationHints(RelationShape? Shape, string? ForeignKeyEnd, bool Stored);

/// <summary>A key sequence the designer proposes for a hierarchy's key.</summary>
internal sealed class DesignedSequence
{
    /// <summary>Its key in the proposal (<c>&lt;root&gt;.sequence@&lt;database&gt;</c>), which a snapshot alias recorded before 0.29.0 may name.</summary>
    public required string Key { get; init; }

    /// <summary>The sequence name.</summary>
    public required string Name { get; init; }

    /// <summary>The integer type.</summary>
    public required string Type { get; init; }

    /// <summary>The schema id, or <see langword="null"/> for the database's default.</summary>
    public string? SchemaId { get; init; }
}

/// <summary>A column the designer proposes.</summary>
internal sealed class DesignedColumn
{
    /// <summary>The column's key: the attribute path, a foreign key column's <c>&lt;end&gt;.&lt;key attribute&gt;</c>, or a role
    /// (<c>discriminator</c>, <c>position</c>, <c>id</c>), as the projection keyed it before 0.29.0 (a snapshot alias may name it).</summary>
    public required string Key { get; init; }

    public required string Name { get; set; }

    public required string Type { get; set; }

    public string? Subtype { get; set; }

    public int? Length { get; set; }

    public int? Precision { get; set; }

    public int? Scale { get; set; }

    public bool Nullable { get; set; }

    public object? Default { get; set; }

    /// <summary>A default in SQL for the database's dialect (an attribute's default expression).</summary>
    public string? DefaultSql { get; set; }

    public string? DefaultName { get; set; }

    public bool Identity { get; set; }

    public long? IdentitySeed { get; set; }

    public long? IdentityIncrement { get; set; }

    public bool IdentityAlways { get; set; }

    public DesignedSequence? Sequence { get; set; }

    public string? Computed { get; set; }

    public bool ComputedStored { get; set; }

    public bool StableId { get; set; }

    public string? Collation { get; set; }

    public string? Comment { get; set; }

    public bool? Unicode { get; set; }

    public bool FixedLength { get; set; }

    /// <summary>The native type the column gets in the dialect (as the dialect map renders it, or a scalar type's, or the referenced
    /// column's for a foreign key column).</summary>
    public string? NativeType { get; set; }

    /// <summary>The attribute path the column stores, or <see langword="null"/>.</summary>
    public string? AttributePath { get; init; }

    /// <summary>The attribute the column stores, or <see langword="null"/>.</summary>
    public RAttribute? Attribute { get; init; }

    /// <summary>The reference type a code column holds, or <see langword="null"/>.</summary>
    public RReferenceType? ReferenceType { get; init; }

    /// <summary>The custom scalar type whose native type the column takes.</summary>
    public RScalarType? Scalar { get; set; }

    public bool IsPrimaryKey { get; set; }

    /// <summary>Whether the SQL default comes from the attribute's default expression (which the sql-ddl pack wrote from the model).</summary>
    public bool DefaultFromExpression { get; set; }
}

/// <summary>A unique constraint the designer proposes.</summary>
internal sealed record DesignedUnique(IReadOnlyList<string> Columns, string? Name, string? NameToken, string? Id = null, bool NullsNotDistinct = false);

/// <summary>An index column the designer proposes: a column key, or an expression for the dialect.</summary>
internal sealed record DesignedIndexColumn(string? Column, bool Descending, string? Expression = null, int? Length = null);

/// <summary>An index the designer proposes.</summary>
internal sealed record DesignedIndex(IReadOnlyList<DesignedIndexColumn> Columns, IReadOnlyList<string> Include, string? Where, bool Unique, string Method,
    string? Name, bool FromFile = false, string? Id = null);

/// <summary>Where a proposed foreign key points: a table of the proposal, or a table the database already has.</summary>
internal sealed class DesignTarget
{
    /// <summary>A table of the proposal.</summary>
    public DesignedTable? Designed { get; init; }

    /// <summary>A table the database already has (a designed table an entity is bound to): its key.</summary>
    public string? ExistingKey { get; init; }

    /// <summary>The existing table's name.</summary>
    public string? ExistingName { get; init; }

    /// <summary>The existing table's primary key columns: key and shape.</summary>
    public IReadOnlyList<KeyColumnShape> ExistingKeyColumns { get; init; } = [];
}

/// <summary>The shape of a referenced key column, which a foreign key column follows.</summary>
internal sealed record KeyColumnShape(string Key, string Type, int? Length, int? Precision, int? Scale, string? NativeType, RScalarType? Scalar);

/// <summary>A foreign key the designer proposes.</summary>
internal sealed class DesignedForeignKey
{
    public string? Id { get; init; }

    public string? Name { get; set; }

    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The referenced table, once known.</summary>
    public DesignTarget? Target { get; set; }

    /// <summary>The referenced columns' keys; empty for the referenced table's primary key.</summary>
    public IReadOnlyList<string> TargetColumns { get; init; } = [];

    public required string OnDelete { get; init; }

    public required string OnUpdate { get; init; }

    public IReadOnlyList<string> OnDeleteColumns { get; init; } = [];

    public string Deferrable { get; init; } = "not-deferrable";

    /// <summary>The relation the foreign key stores, or <see langword="null"/>.</summary>
    public RRelation? Relation { get; init; }

    /// <summary>The relation end whose table holds the key (foreign-key shape) or whose entity it references (junction).</summary>
    public REnd? End { get; init; }

    /// <summary>The table that holds the key (foreign-key shape).</summary>
    public DesignedTable? Host { get; init; }

    /// <summary>Whether the key joins a derived table to its base's (table per type).</summary>
    public bool IsBase { get; init; }

    /// <summary>Whether the key joins a child table to its owner's.</summary>
    public bool IsOwner { get; init; }

    /// <summary>Whether the key is the one the sql-ddl pack wrote for a column holding a reference type's codes.</summary>
    public bool IsReference { get; init; }

    /// <summary>The resolved columns (after finishing).</summary>
    public List<DesignedColumn> ResolvedColumns { get; } = [];

    /// <summary>The referenced columns' keys (after finishing).</summary>
    public List<string> ReferencedKeys { get; } = [];

    public bool Resolved { get; set; }
}

/// <summary>A table the designer proposes, with the shape the projection gave the same entity, attribute or relation before 0.29.0.</summary>
internal sealed class DesignedTable
{
    /// <summary>The table's key in the proposal (<c>&lt;entity&gt;@&lt;database&gt;</c>, <c>&lt;owner&gt;.&lt;attribute&gt;@&lt;database&gt;</c>,
    /// <c>&lt;relation&gt;@&lt;database&gt;</c>): the key a snapshot alias recorded before 0.29.0 may name, and the key prefix of inherited members.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>The schema id, or <see langword="null"/> for the database's default.</summary>
    public string? SchemaId { get; set; }

    /// <summary>A lookup table's schema by name (its storage's <c>schema</c> option), which the database may not declare yet.</summary>
    public string? SchemaName { get; init; }

    public required DesignedTableKind Kind { get; init; }

    /// <summary>The entity whose rows the table holds (an entity table, or a child table's owner).</summary>
    public REntity? Entity { get; init; }

    /// <summary>A child table's attribute.</summary>
    public RAttribute? Attribute { get; init; }

    /// <summary>A junction table's relation.</summary>
    public RRelation? Relation { get; init; }

    /// <summary>A child table's owner table.</summary>
    public DesignedTable? Owner { get; init; }

    /// <summary>Whether a child table holds a list (an order column) rather than a single value object.</summary>
    public bool IsList { get; init; }

    /// <summary>Whether a child table holds a reference type's codes (the shape the sql-ddl pack gave a template-defined collection).</summary>
    public bool IsReferenceList { get; init; }

    public string? Comment { get; set; }

    /// <summary>The package of the entity or relation the table is for (a child table: its owner's).</summary>
    public string? PackageId { get; init; }

    public List<DesignedColumn> Columns { get; } = [];

    public Dictionary<string, DesignedColumn> ByKey { get; } = new(StringComparer.Ordinal);

    public List<string> PrimaryKey { get; } = [];

    public string? PrimaryKeyName { get; set; }

    public bool? PrimaryKeyClustered { get; set; }

    public List<DesignedUnique> Uniques { get; } = [];

    public List<DesignedForeignKey> ForeignKeys { get; } = [];

    public List<DesignedIndex> Indexes { get; } = [];

    public List<DesignedTable> Children { get; } = [];

    /// <summary>The checks the designer adds (a reference column's codes).</summary>
    public List<DesignedCheck> Checks { get; } = [];

    /// <summary>A lookup table's reference type.</summary>
    public RReferenceType? ReferenceType { get; init; }

    /// <summary>A lookup table's columns by what they hold: <c>code</c>, <c>label</c>, <c>description</c> or a field's id.</summary>
    public List<(string Field, string Column)> LookupFields { get; } = [];

    internal List<Action> Deferred { get; } = [];

    /// <summary>After finishing: the primary key's name and columns.</summary>
    public (string Name, List<DesignedColumn> Columns)? ResolvedPrimaryKey { get; set; }

    /// <summary>After finishing: the unique constraints, named.</summary>
    public List<(string? Id, string Name, List<DesignedColumn> Columns, bool NullsNotDistinct)> ResolvedUniques { get; } = [];

    /// <summary>After finishing: the indexes, named.</summary>
    public List<(DesignedIndex Index, string Name, List<(DesignedColumn? Column, DesignedIndexColumn Spec)> Columns, List<DesignedColumn> Include)> ResolvedIndexes { get; } = [];

    /// <summary>A column by key.</summary>
    public DesignedColumn? Resolve(string key) => ByKey.GetValueOrDefault(key);
}

/// <summary>How a relation the designer saw is stored by the proposal.</summary>
/// <param name="Relation">The relation.</param>
/// <param name="Junction">Its junction table, or <see langword="null"/> for the foreign-key shape.</param>
/// <param name="ForeignKeys">The foreign-key shape's keys, one per table of the dependent end.</param>
/// <param name="EndKeys">The junction's keys by end id.</param>
/// <param name="AttributeColumns">The relation attributes' columns, by attribute path, in the table that holds them.</param>
internal sealed record DesignedRelation(RRelation Relation, DesignedTable? Junction, IReadOnlyList<DesignedForeignKey> ForeignKeys,
    IReadOnlyDictionary<string, DesignedForeignKey> EndKeys, IReadOnlyList<(string Path, DesignedTable Table, DesignedColumn Column)> AttributeColumns);

/// <summary>A requested entity's place in the proposal.</summary>
internal sealed class DesignedEntity
{
    public required REntity Entity { get; init; }

    /// <summary>The hierarchy shape, or <see langword="null"/> outside a hierarchy.</summary>
    public DesignHierarchy? Shape { get; set; }

    /// <summary>The table holding its rows (one table: the root's; per concrete: none for an abstract entity).</summary>
    public DesignedTable? Table { get; set; }

    /// <summary>Its in-request base, or <see langword="null"/>.</summary>
    public DesignedEntity? Base { get; set; }

    public List<DesignedEntity> Derived { get; } = [];

    public DesignedEntity Root { get; set; } = null!;

    /// <summary>The key attribute ids of the hierarchy root.</summary>
    public HashSet<string> KeyIds { get; } = new(StringComparer.Ordinal);

    /// <summary>For one table: the type column; for per type: the foreign key to the base's table.</summary>
    public DesignedColumn? Discriminator { get; set; }

    public DesignedForeignKey? BaseForeignKey { get; set; }
}

/// <summary>What the designer proposes for the requested entities.</summary>
/// <param name="Entities">The requested entities, in model order.</param>
/// <param name="Tables">The tables, in creation order (entity tables first, each followed by its child tables, then junctions).</param>
/// <param name="Relations">The relations stored by the proposal, in model order.</param>
/// <param name="Notes">What the proposal leaves out, in words.</param>
internal sealed record TableProposal(IReadOnlyList<DesignedEntity> Entities, IReadOnlyList<DesignedTable> Tables, IReadOnlyList<DesignedRelation> Relations,
    IReadOnlyList<string> Notes)
{
    /// <summary>Why the request cannot be stored, or <see langword="null"/>.</summary>
    public string? Refusal { get; init; }
}
