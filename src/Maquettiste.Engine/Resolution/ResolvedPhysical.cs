namespace Maquettiste.Engine.Resolution;

/// <summary>A resolved database, with the annotations (<see cref="RAnnotated"/>) of its file (no fallbacks for display and plural names).</summary>
public sealed class RDatabase : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "database";

    /// <summary>The database name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary><c>postgresql</c>, <c>sqlserver</c>, <c>mysql</c>, <c>sqlite</c> or <c>oracle</c>.</summary>
    public string Dialect { get; internal set; } = "";

    /// <summary>The target version.</summary>
    public string? Version { get; internal set; }

    /// <summary>The effective default schema, or <see langword="null"/> when the dialect has none.</summary>
    public string? DefaultSchema { get; internal set; }

    /// <summary>Schemas, by name.</summary>
    public RList<RSchema> Schemas { get; internal set; } = RList<RSchema>.Empty;

    /// <summary>Every table, by (schema, name).</summary>
    public RList<RTable> Tables { get; internal set; } = RList<RTable>.Empty;

    /// <summary>Every view, by (schema, name).</summary>
    public RList<RView> Views { get; internal set; } = RList<RView>.Empty;

    /// <summary>Every sequence, by (schema, name).</summary>
    public RList<RSequence> Sequences { get; internal set; } = RList<RSequence>.Empty;

    /// <summary>Every routine (function or procedure), by (schema, name).</summary>
    public RList<RRoutine> Routines { get; internal set; } = RList<RRoutine>.Empty;

    /// <summary>Every database type, by (schema, name).</summary>
    public RList<RDatabaseType> Types { get; internal set; } = RList<RDatabaseType>.Empty;

    /// <summary>Every SQL object, by (schema, name).</summary>
    public RList<RSqlObject> Objects { get; internal set; } = RList<RSqlObject>.Empty;

    /// <summary>Every query, by (name, id).</summary>
    public RList<RQuery> Queries { get; internal set; } = RList<RQuery>.Empty;

    /// <summary><c>always</c>, <c>reserved</c> or <c>never</c>.</summary>
    public string Quoting { get; internal set; } = "reserved";

    /// <summary>The effective identifier length limit, or <see langword="null"/> for none.</summary>
    public int? MaxIdentifierLength { get; internal set; }

    /// <summary>
    /// Which entities map here by convention: <c>all</c>, <c>packages</c> (the entities of <see cref="Packages"/>) or <c>none</c>. A file
    /// that leaves it out reads as <c>all</c> without packages and <c>packages</c> with them, as the resolver places tables.
    /// </summary>
    public string ByConvention { get; internal set; } = "all";

    /// <summary>The packages whose entities map here by convention, each with the schema its conventional tables go to, in file order.</summary>
    public IReadOnlyList<RConventionPackage> Packages { get; internal set; } = [];
}

/// <summary>One package a database takes by convention.</summary>
public sealed class RConventionPackage
{
    /// <summary>The package, or <see langword="null"/> when the id names no package.</summary>
    public RPackage? Package { get; internal set; }

    /// <summary>The package id as the database file writes it.</summary>
    public string PackageId { get; internal set; } = "";

    /// <summary>The name of the schema the package's conventional tables go to, or <see langword="null"/> for the default schema.</summary>
    public string? Schema { get; internal set; }
}

/// <summary>A resolved schema. A schema the database file declares carries its annotations (<see cref="RAnnotated"/>); a schema known only
/// because a table, view or sequence uses it (the dialect's default schema, say) has none.</summary>
public sealed class RSchema : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "schema";

    /// <summary>The schema name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>Whether the schema is the database's effective default schema.</summary>
    public bool IsDefault { get; internal set; }

    /// <summary>Whether the database file declares the schema.</summary>
    public bool IsDeclared { get; internal set; }

    /// <summary>Tables in the schema, by name.</summary>
    public RList<RTable> Tables { get; internal set; } = RList<RTable>.Empty;

    /// <summary>Views in the schema, by name.</summary>
    public RList<RView> Views { get; internal set; } = RList<RView>.Empty;

    /// <summary>Sequences in the schema, by name.</summary>
    public RList<RSequence> Sequences { get; internal set; } = RList<RSequence>.Empty;

    /// <summary>Routines in the schema, by name.</summary>
    public RList<RRoutine> Routines { get; internal set; } = RList<RRoutine>.Empty;

    /// <summary>Database types in the schema, by name.</summary>
    public RList<RDatabaseType> Types { get; internal set; } = RList<RDatabaseType>.Empty;

    /// <summary>SQL objects in the schema, by name.</summary>
    public RList<RSqlObject> Objects { get; internal set; } = RList<RSqlObject>.Empty;
}

/// <summary>A resolved table. Its annotations (<see cref="RAnnotated"/>) come from its own file: the designed or imported table, or a
/// synthesized table's overlay; a synthesized table without an overlay has none.</summary>
public sealed class RTable : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "table";

    /// <summary>The table key: the file's ULID, or a synthesized key such as <c>&lt;entityId&gt;@&lt;databaseId&gt;</c> (engine-design.md section 7.3).</summary>
    public string Key { get; internal set; } = "";

    /// <summary>The physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; internal set; }

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary><c>synthesized</c>, <c>designed</c> or <c>imported</c>.</summary>
    public string Origin { get; internal set; } = "synthesized";

    /// <summary>The mapped entity, for an entity table.</summary>
    public REntity? Entity { get; internal set; }

    /// <summary>The relation, for a junction table.</summary>
    public RRelation? Relation { get; internal set; }

    /// <summary>The attribute a child table stores (a value object stored as a table, or a collection), or <see langword="null"/>.</summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>Columns, by position.</summary>
    public RList<RColumn> Columns { get; internal set; } = RList<RColumn>.Empty;

    /// <summary>The primary key.</summary>
    public RPrimaryKey? PrimaryKey { get; internal set; }

    /// <summary>Unique constraints.</summary>
    public IReadOnlyList<RUnique> Uniques { get; internal set; } = [];

    /// <summary>Foreign keys.</summary>
    public IReadOnlyList<RForeignKey> ForeignKeys { get; internal set; } = [];

    /// <summary>Check constraints.</summary>
    public IReadOnlyList<RCheck> Checks { get; internal set; } = [];

    /// <summary>Exclusion constraints (PostgreSQL <c>EXCLUDE</c>).</summary>
    public IReadOnlyList<RExclusion> Exclusions { get; internal set; } = [];

    /// <summary>Indexes.</summary>
    public IReadOnlyList<RIndex> Indexes { get; internal set; } = [];

    /// <summary>How the table is partitioned, or <see langword="null"/>.</summary>
    public RPartitionBy? PartitionBy { get; internal set; }

    /// <summary>The partitions created with the table, in file order.</summary>
    public IReadOnlyList<RPartition> Partitions { get; internal set; } = [];

    /// <summary>
    /// The storage parameters for the database's dialect, by name in ordinal order: the table file's over its stereotypes' (in
    /// stereotype order, later wins). A partitioned table's are its partitions' (it takes none itself).
    /// </summary>
    public IReadOnlyList<RStorageParameter> Storage { get; internal set; } = [];

    /// <summary>The comment.</summary>
    public string? Comment { get; internal set; }

    /// <summary>Whether the table is a relation's junction table.</summary>
    public bool IsJunction { get; internal set; }

    /// <summary>
    /// The entity bindings that read or write the table (erratum E43), by (entity name, entity id): the entities that materialize from
    /// it, several when constants (an entity type column) tell them apart. The table itself knows nothing about them.
    /// </summary>
    public IReadOnlyList<REntityBinding> BoundBy { get; internal set; } = [];
}

/// <summary>A resolved primary key.</summary>
public sealed class RPrimaryKey
{
    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The key columns, in key order.</summary>
    public IReadOnlyList<RColumn> Columns { get; internal set; } = [];

    /// <summary>Whether the table file asks for a clustered (<see langword="true"/>) or nonclustered (<see langword="false"/>) key, or
    /// <see langword="null"/> when it says nothing.</summary>
    public bool? Clustered { get; internal set; }

    /// <summary>Whether the last column is a period (PostgreSQL 18 <c>WITHOUT OVERLAPS</c>).</summary>
    public bool WithoutOverlaps { get; internal set; }
}

/// <summary>A resolved unique constraint.</summary>
public sealed class RUnique
{
    /// <summary>The id the table file gives the constraint, or <see langword="null"/> for one the resolver creates.</summary>
    public string? Id { get; internal set; }

    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The columns.</summary>
    public IReadOnlyList<RColumn> Columns { get; internal set; } = [];

    /// <summary>Whether rows with nulls in the columns conflict (PostgreSQL 15 <c>NULLS NOT DISTINCT</c>).</summary>
    public bool NullsNotDistinct { get; internal set; }

    /// <summary>Whether the last column is a period (PostgreSQL 18 <c>WITHOUT OVERLAPS</c>).</summary>
    public bool WithoutOverlaps { get; internal set; }
}

/// <summary>A resolved foreign key.</summary>
public sealed class RForeignKey
{
    /// <summary>The id the table file gives the constraint, or <see langword="null"/> for one the resolver creates.</summary>
    public string? Id { get; internal set; }

    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The referencing columns.</summary>
    public IReadOnlyList<RColumn> Columns { get; internal set; } = [];

    /// <summary>The referenced table.</summary>
    public RTable ReferencedTable { get; internal set; } = null!;

    /// <summary>The referenced columns.</summary>
    public IReadOnlyList<RColumn> ReferencedColumns { get; internal set; } = [];

    /// <summary>Whether the last column pair is a period (PostgreSQL 18 <c>PERIOD</c>).</summary>
    public bool Period { get; internal set; }

    /// <summary><c>no-action</c>, <c>restrict</c>, <c>cascade</c>, <c>set-null</c> or <c>set-default</c>.</summary>
    public string OnDelete { get; internal set; } = "no-action";

    /// <summary>
    /// With <see cref="OnDelete"/> <c>set-null</c> or <c>set-default</c>, the columns of <see cref="Columns"/> the action sets
    /// (PostgreSQL's <c>ON DELETE SET NULL (column, ...)</c>); empty sets every column of the key.
    /// </summary>
    public IReadOnlyList<RColumn> OnDeleteColumns { get; internal set; } = [];

    /// <summary>The on-update action, as <see cref="OnDelete"/>.</summary>
    public string OnUpdate { get; internal set; } = "no-action";

    /// <summary><c>not-deferrable</c>, <c>initially-immediate</c> or <c>initially-deferred</c>.</summary>
    public string Deferrable { get; internal set; } = "not-deferrable";

    /// <summary>The relation the key implements, if any.</summary>
    public RRelation? Relation { get; internal set; }

    /// <summary>The relation end whose table holds the key, if any.</summary>
    public REnd? End { get; internal set; }
}

/// <summary>A resolved check constraint.</summary>
public sealed class RCheck
{
    /// <summary>The id the table file gives the constraint, or <see langword="null"/> for one the resolver creates.</summary>
    public string? Id { get; internal set; }

    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The expression for the database's dialect.</summary>
    public string Expression { get; internal set; } = "";

    /// <summary>For a column check: the column it constrains; <see langword="null"/> for a table check.</summary>
    public RColumn? Column { get; internal set; }
}

/// <summary>A resolved index.</summary>
public sealed class RIndex
{
    /// <summary>The id the table file gives the index, or <see langword="null"/> for an index the resolver creates.</summary>
    public string? Id { get; internal set; }

    /// <summary>The index name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The indexed columns with sort order.</summary>
    public IReadOnlyList<RIndexColumn> Columns { get; internal set; } = [];

    /// <summary>Included columns.</summary>
    public IReadOnlyList<RColumn> Include { get; internal set; } = [];

    /// <summary>The partial-index predicate.</summary>
    public string? Where { get; internal set; }

    /// <summary>Whether the index is unique.</summary>
    public bool Unique { get; internal set; }

    /// <summary><c>default</c>, <c>btree</c>, <c>hash</c>, <c>gin</c>, <c>gist</c>, <c>spgist</c>, <c>brin</c>, <c>hnsw</c>, <c>ivfflat</c> or <c>clustered</c>.</summary>
    public string Method { get; internal set; } = "default";

    /// <summary>The storage parameters for the database's dialect, by name in ordinal order.</summary>
    public IReadOnlyList<RStorageParameter> Storage { get; internal set; } = [];
}

/// <summary>How a resolved table is partitioned.</summary>
public sealed class RPartitionBy
{
    /// <summary><c>range</c>, <c>list</c> or <c>hash</c>.</summary>
    public string Strategy { get; internal set; } = "range";

    /// <summary>The partition columns.</summary>
    public IReadOnlyList<RColumn> Columns { get; internal set; } = [];
}

/// <summary>A partition of a resolved table.</summary>
public sealed class RPartition
{
    /// <summary>The partition's id.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>The partition's table name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>What follows <c>FOR VALUES</c>; <see langword="null"/> for the default partition.</summary>
    public string? Bounds { get; internal set; }

    /// <summary>Whether this is the default partition.</summary>
    public bool IsDefault { get; internal set; }
}

/// <summary>A resolved exclusion constraint.</summary>
public sealed class RExclusion
{
    /// <summary>The id the table file gives the constraint.</summary>
    public string? Id { get; internal set; }

    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary><c>gist</c>, <c>spgist</c>, <c>btree</c> or <c>hash</c>.</summary>
    public string Method { get; internal set; } = "gist";

    /// <summary>The compared columns or expressions.</summary>
    public IReadOnlyList<RExclusionElement> Elements { get; internal set; } = [];

    /// <summary>The predicate limiting the rows compared, or <see langword="null"/>.</summary>
    public string? Where { get; internal set; }

    /// <summary><c>not-deferrable</c>, <c>initially-immediate</c> or <c>initially-deferred</c>.</summary>
    public string Deferrable { get; internal set; } = "not-deferrable";
}

/// <summary>A compared column or expression of a resolved exclusion constraint.</summary>
public sealed class RExclusionElement
{
    /// <summary>The column; <see langword="null"/> for an expression.</summary>
    public RColumn? Column { get; internal set; }

    /// <summary>The expression; <see langword="null"/> for a column.</summary>
    public string? Expression { get; internal set; }

    /// <summary>The operator class, or <see langword="null"/>.</summary>
    public string? OperatorClass { get; internal set; }

    /// <summary>The operator.</summary>
    public string Operator { get; internal set; } = "";
}

/// <summary>A storage parameter of a table or an index, for the database's dialect.</summary>
public sealed class RStorageParameter
{
    /// <summary>The parameter name as the file writes it (<c>fillfactor</c>, <c>autovacuum_vacuum_scale_factor</c>).</summary>
    public string Name { get; internal set; } = "";

    /// <summary>
    /// The value as the dialect writes it: a number as written, a boolean as <c>true</c>/<c>false</c> (PostgreSQL, Oracle),
    /// <c>ON</c>/<c>OFF</c> (SQL Server) or <c>1</c>/<c>0</c> (MySQL), a string as it is.
    /// </summary>
    public string Value { get; internal set; } = "";
}

/// <summary>A column in a resolved index, or an expression (a functional index).</summary>
public sealed class RIndexColumn
{
    /// <summary>The column; <see langword="null"/> for an expression.</summary>
    public RColumn? Column { get; internal set; }

    /// <summary>The indexed expression for the database's dialect; <see langword="null"/> for a column.</summary>
    public string? Expression { get; internal set; }

    /// <summary>The operator class (PostgreSQL), or <see langword="null"/> for the type's default.</summary>
    public string? OperatorClass { get; internal set; }

    /// <summary>Whether the column sorts descending.</summary>
    public bool Descending { get; internal set; }

    /// <summary>A key prefix length (MySQL), or <see langword="null"/>.</summary>
    public int? Length { get; internal set; }
}

/// <summary>
/// A resolved column. Its annotations (<see cref="RAnnotated"/>) come from its own entry in a table file: a designed or imported
/// column, an overlay's extra column, or the overlay entry of a synthesized column. A synthesized column without an overlay entry has
/// none, and never takes its attribute's, which templates reach through <see cref="Attribute"/>.
/// </summary>
public sealed class RColumn : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "column";

    /// <summary>The column key: an attribute path, <c>discriminator</c>, <c>position</c>, <c>id</c>, or a column id (engine-design.md section 7.3).</summary>
    public string Key { get; internal set; } = "";

    /// <summary>The physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The table.</summary>
    public RTable Table { get; internal set; } = null!;

    /// <summary>The built-in type keyword.</summary>
    public string Type { get; internal set; } = "";

    /// <summary>The effective length.</summary>
    public int? Length { get; internal set; }

    /// <summary>The effective precision.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The effective scale.</summary>
    public int? Scale { get; internal set; }

    /// <summary>The effective native type for the database's dialect.</summary>
    public string NativeType { get; internal set; } = "";

    /// <summary>
    /// The database type the column uses, when its file's <c>nativeType</c> names one of the database (by id or name); its native name
    /// for the dialect is then <see cref="NativeType"/>.
    /// </summary>
    public RDatabaseType? DbType { get; internal set; }

    /// <summary>
    /// For a string or text column: <see langword="true"/> for Unicode text, <see langword="false"/> for single-byte text,
    /// <see langword="null"/> for the dialect's default. <see cref="NativeType"/> already reflects it.
    /// </summary>
    public bool? Unicode { get; internal set; }

    /// <summary>For a string or binary column: whether the type is fixed-length. <see cref="NativeType"/> already reflects it.</summary>
    public bool FixedLength { get; internal set; }

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; internal set; }

    /// <summary>The literal default, as a plain CLR value.</summary>
    public object? Default { get; internal set; }

    /// <summary>The default SQL expression for the database's dialect.</summary>
    public string? DefaultSql { get; internal set; }

    /// <summary>The name its file gives the default constraint (SQL Server), or <see langword="null"/> for the template's convention.</summary>
    public string? DefaultName { get; internal set; }

    /// <summary>Whether the column is an identity column.</summary>
    public bool Identity { get; internal set; }

    /// <summary>An identity column's first value, or <see langword="null"/> for the dialect's (1).</summary>
    public long? IdentitySeed { get; internal set; }

    /// <summary>An identity column's step, or <see langword="null"/> for the dialect's (1).</summary>
    public long? IdentityIncrement { get; internal set; }

    /// <summary>Whether an identity column refuses values given by an insert (GENERATED ALWAYS) rather than generating them by default.</summary>
    public bool IdentityAlways { get; internal set; }

    /// <summary>The sequence that supplies values.</summary>
    public RSequence? Sequence { get; internal set; }

    /// <summary>The computed expression.</summary>
    public string? Computed { get; internal set; }

    /// <summary>Whether the computed value is stored.</summary>
    public bool ComputedStored { get; internal set; }

    /// <summary>The collation.</summary>
    public string? Collation { get; internal set; }

    /// <summary>The comment.</summary>
    public string? Comment { get; internal set; }

    /// <summary>The attribute the column stores, if any.</summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>The attribute path the column stores, if any.</summary>
    public string? AttributePath { get; internal set; }

    /// <summary>Whether the column is part of the primary key.</summary>
    public bool IsPrimaryKey { get; internal set; }

    /// <summary>Whether the column is part of a foreign key.</summary>
    public bool IsForeignKey { get; internal set; }

    /// <summary>Whether the column is a TPH discriminator.</summary>
    public bool IsDiscriminator { get; internal set; }

    /// <summary>The 0-based position in the table.</summary>
    public int Position { get; internal set; }

    /// <summary>
    /// For a column typed by a reference type (<see cref="Type"/> <c>reference</c>): the reference type. The code's facets ride
    /// along in <see cref="Length"/>, and <see cref="CodeType"/> is the code's logical type.
    /// </summary>
    public RReferenceType? ReferenceType { get; internal set; }

    /// <summary>For a reference column: the code's logical type (<c>string</c>, <c>int16</c>, <c>int32</c> or <c>int64</c>).</summary>
    public string? CodeType { get; internal set; }

    /// <summary>For a reference column: the effective storage strategy in the column's database, or <see langword="null"/> for template-defined.</summary>
    public string? Strategy { get; internal set; }
}

/// <summary>A resolved view, with the annotations (<see cref="RAnnotated"/>) of its file.</summary>
public sealed class RView : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "view";

    /// <summary>The physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; internal set; }

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary>The body for the database's dialect.</summary>
    public string Body { get; internal set; } = "";

    /// <summary>The columns.</summary>
    public IReadOnlyList<RViewColumn> Columns { get; internal set; } = [];

    /// <summary>Whether the DDL names <see cref="Columns"/> in CREATE VIEW.</summary>
    public bool ColumnList { get; internal set; }

    /// <summary>Whether writes through the view must satisfy its WHERE (WITH CHECK OPTION).</summary>
    public bool WithCheckOption { get; internal set; }

    /// <summary>Whether the view is materialized (it stores its rows).</summary>
    public bool Materialized { get; internal set; }

    /// <summary>Whether the view reads its tables with the caller's rights (PostgreSQL <c>security_invoker</c>).</summary>
    public bool SecurityInvoker { get; internal set; }

    /// <summary>Whether the view's filter runs before the query's functions that are not leakproof (PostgreSQL <c>security_barrier</c>).</summary>
    public bool SecurityBarrier { get; internal set; }

    /// <summary>
    /// What must exist before the view: the objects its file's <c>dependsOn</c> names, then the other views of the database its body
    /// names (a best-effort reading of the body text), each once.
    /// </summary>
    public IReadOnlyList<IResolvedObject> DependsOn { get; internal set; } = [];

    /// <summary>The comment.</summary>
    public string? Comment { get; internal set; }

    /// <summary>The entity bindings that read the view (erratum E43), by (entity name, entity id).</summary>
    public IReadOnlyList<REntityBinding> BoundBy { get; internal set; } = [];
}

/// <summary>A column of a resolved view.</summary>
public sealed class RViewColumn
{
    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The built-in type keyword.</summary>
    public string? Type { get; internal set; }

    /// <summary>The native type for the database's dialect.</summary>
    public string? NativeType { get; internal set; }

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; internal set; } = true;
}

/// <summary>A resolved sequence. Its annotations (<see cref="RAnnotated"/>) come from its file; a sequence the resolver creates for an
/// entity key has none.</summary>
public sealed class RSequence : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "sequence";

    /// <summary>The physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; internal set; }

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary>The built-in integer type keyword.</summary>
    public string Type { get; internal set; } = "int64";

    /// <summary>The native type for the database's dialect.</summary>
    public string NativeType { get; internal set; } = "";

    /// <summary>The first value.</summary>
    public long Start { get; internal set; } = 1;

    /// <summary>The increment.</summary>
    public long Increment { get; internal set; } = 1;

    /// <summary>The minimum value.</summary>
    public long? Min { get; internal set; }

    /// <summary>The maximum value.</summary>
    public long? Max { get; internal set; }

    /// <summary>Whether the sequence wraps around.</summary>
    public bool Cycle { get; internal set; }

    /// <summary>How many values the server caches.</summary>
    public int? Cache { get; internal set; }
}

/// <summary>
/// A resolved routine: a stored function or procedure, with the annotations (<see cref="RAnnotated"/>) of its file. Its parameter and
/// result types are resolved to native types for the database's dialect, a database type of the same database included.
/// </summary>
public sealed class RRoutine : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "routine";

    /// <summary>The physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; internal set; }

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary><c>function</c> or <c>procedure</c>.</summary>
    public string RoutineKind { get; internal set; } = "function";

    /// <summary>The parameters, in order.</summary>
    public IReadOnlyList<RRoutineParameter> Parameters { get; internal set; } = [];

    /// <summary>The result, or <see langword="null"/> when the routine returns nothing.</summary>
    public RRoutineReturns? Returns { get; internal set; }

    /// <summary>The body's language: the file's, else the dialect's own (<c>plpgsql</c>, <c>tsql</c>, <c>sql</c>).</summary>
    public string Language { get; internal set; } = "";

    /// <summary>The body for the database's dialect (or the <c>"*"</c> body); empty when the file has none for it.</summary>
    public string Body { get; internal set; } = "";

    /// <summary>Whether the file has a body for the database's dialect or for every dialect.</summary>
    public bool HasBody { get; internal set; }

    /// <summary>Whether the routine returns the same result for the same arguments.</summary>
    public bool Deterministic { get; internal set; }

    /// <summary><c>volatile</c>, <c>stable</c> or <c>immutable</c>: the file's, else <c>immutable</c> when deterministic, else <c>volatile</c>.</summary>
    public string Volatility { get; internal set; } = "volatile";

    /// <summary><c>invoker</c> or <c>definer</c>.</summary>
    public string Security { get; internal set; } = "invoker";

    /// <summary>Configuration parameters set while the routine runs (PostgreSQL <c>SET name = value</c>), by name in ordinal order.</summary>
    public IReadOnlyList<RRoutineSetting> Settings { get; internal set; } = [];

    /// <summary>The tables, views, sequences, routines, database types and SQL objects of the database it names in <c>dependsOn</c>, in file order.</summary>
    public IReadOnlyList<IResolvedObject> DependsOn { get; internal set; } = [];

    /// <summary>The comment.</summary>
    public string? Comment { get; internal set; }
}

/// <summary>A configuration parameter a resolved routine sets while it runs.</summary>
public sealed class RRoutineSetting
{
    /// <summary>The parameter's name, such as <c>search_path</c>.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The value, as SQL.</summary>
    public string Value { get; internal set; } = "";
}

/// <summary>A parameter of a resolved routine.</summary>
public sealed class RRoutineParameter
{
    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The built-in type keyword, or <see langword="null"/> when the parameter is typed by a database type or a native type only.</summary>
    public string? Type { get; internal set; }

    /// <summary>The database type, when the file names one.</summary>
    public RDatabaseType? DbType { get; internal set; }

    /// <summary>The length facet.</summary>
    public int? Length { get; internal set; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; internal set; }

    /// <summary>The native type for the database's dialect: the file's, else the database type's native name, else the type map's.</summary>
    public string NativeType { get; internal set; } = "";

    /// <summary><c>in</c>, <c>out</c> or <c>inout</c>.</summary>
    public string Mode { get; internal set; } = "in";

    /// <summary>The default value as SQL text, or <see langword="null"/>.</summary>
    public string? Default { get; internal set; }
}

/// <summary>The result of a resolved routine: a single value, or a table when <see cref="Table"/> is set.</summary>
public sealed class RRoutineReturns
{
    /// <summary>The built-in type keyword of a single value, or <see langword="null"/>.</summary>
    public string? Type { get; internal set; }

    /// <summary>The database type of a single value, when the file names one.</summary>
    public RDatabaseType? DbType { get; internal set; }

    /// <summary>The length facet.</summary>
    public int? Length { get; internal set; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; internal set; }

    /// <summary>The native type of a single value for the database's dialect; empty for a table result.</summary>
    public string NativeType { get; internal set; } = "";

    /// <summary>The columns of a table result, or <see langword="null"/> for a single value.</summary>
    public IReadOnlyList<RRoutineColumn>? Table { get; internal set; }
}

/// <summary>A column of a resolved routine's table result.</summary>
public sealed class RRoutineColumn
{
    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The built-in type keyword, or <see langword="null"/>.</summary>
    public string? Type { get; internal set; }

    /// <summary>The database type, when the file names one.</summary>
    public RDatabaseType? DbType { get; internal set; }

    /// <summary>The length facet.</summary>
    public int? Length { get; internal set; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; internal set; }

    /// <summary>The native type for the database's dialect.</summary>
    public string NativeType { get; internal set; } = "";

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; internal set; } = true;
}

/// <summary>
/// A resolved database type (a domain, composite, enumeration or range), with the annotations (<see cref="RAnnotated"/>) of its file.
/// <see cref="NativeName"/> is what a column or parameter typed by it writes.
/// </summary>
public sealed class RDatabaseType : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "database-type";

    /// <summary>The physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; internal set; }

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary><c>domain</c>, <c>composite</c>, <c>enum</c> or <c>range</c>.</summary>
    public string TypeKind { get; internal set; } = "domain";

    /// <summary>For a domain: the built-in type it restricts.</summary>
    public string? Base { get; internal set; }

    /// <summary>For a domain: the native type of its base with the facets, for the database's dialect.</summary>
    public string? BaseNativeType { get; internal set; }

    /// <summary>The length facet of the base.</summary>
    public int? Length { get; internal set; }

    /// <summary>The precision facet of the base.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The scale facet of the base.</summary>
    public int? Scale { get; internal set; }

    /// <summary>For a domain: the CHECK expression over <c>VALUE</c>.</summary>
    public string? Check { get; internal set; }

    /// <summary>For an enum: the labels, in order.</summary>
    public IReadOnlyList<string> Members { get; internal set; } = [];

    /// <summary>For a composite: the fields, in order.</summary>
    public IReadOnlyList<RDatabaseTypeField> Fields { get; internal set; } = [];

    /// <summary>For a range: the built-in type of its bounds.</summary>
    public string? Subtype { get; internal set; }

    /// <summary>For a range: the native type of its subtype for the database's dialect.</summary>
    public string? SubtypeNativeType { get; internal set; }

    /// <summary>The definition for the database's dialect (the text after the type's name), or <see langword="null"/> to build it from the structured form.</summary>
    public string? Definition { get; internal set; }

    /// <summary>
    /// Whether the database creates the type: it has a definition for the dialect, or the dialect has the kind (PostgreSQL every kind,
    /// SQL Server a domain as an alias type).
    /// </summary>
    public bool IsCreated { get; internal set; }

    /// <summary>
    /// What a column or parameter typed by it writes: the file's <c>nativeName</c>; else, when the database creates the type, its name
    /// (schema-qualified outside the default schema); else the native type a dialect without the kind stores it as (a domain's base,
    /// an enum's string as long as its longest label), or the empty string (a composite or range there).
    /// </summary>
    public string NativeName { get; internal set; } = "";

    /// <summary>The database types its fields use, in field order (a type is created after them).</summary>
    public IReadOnlyList<IResolvedObject> DependsOn { get; internal set; } = [];

    /// <summary>The comment.</summary>
    public string? Comment { get; internal set; }
}

/// <summary>A field of a resolved composite database type.</summary>
public sealed class RDatabaseTypeField
{
    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The built-in type keyword, or <see langword="null"/>.</summary>
    public string? Type { get; internal set; }

    /// <summary>The database type, when the file names one.</summary>
    public RDatabaseType? DbType { get; internal set; }

    /// <summary>The length facet.</summary>
    public int? Length { get; internal set; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; internal set; }

    /// <summary>The native type for the database's dialect.</summary>
    public string NativeType { get; internal set; } = "";
}

/// <summary>A resolved SQL object: statements the model does not type (a trigger, a grant, an extension), with the annotations of its file.</summary>
public sealed class RSqlObject : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "sql-object";

    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; internal set; }

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary>What the object is, as the file says (trigger, grant, extension...).</summary>
    public string ObjectKind { get; internal set; } = "";

    /// <summary><c>before</c> (ahead of the database types and tables) or <c>after</c> (behind the routines and views).</summary>
    public string Phase { get; internal set; } = "after";

    /// <summary>The tables, views, sequences, routines, database types and SQL objects of the database it names in <c>dependsOn</c>, in file order.</summary>
    public IReadOnlyList<IResolvedObject> DependsOn { get; internal set; } = [];

    /// <summary>The statements for the database's dialect (or the <c>"*"</c> ones); empty when the file has none for it.</summary>
    public string Body { get; internal set; } = "";

    /// <summary>Whether the file has statements for the database's dialect or for every dialect.</summary>
    public bool HasBody { get; internal set; }
}
