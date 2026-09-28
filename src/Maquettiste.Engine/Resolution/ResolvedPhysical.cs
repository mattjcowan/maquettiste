namespace Maquettiste.Engine.Resolution;

/// <summary>A resolved database.</summary>
public sealed class RDatabase : RObject
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

    /// <summary><c>always</c>, <c>reserved</c> or <c>never</c>.</summary>
    public string Quoting { get; internal set; } = "reserved";

    /// <summary>The effective identifier length limit, or <see langword="null"/> for none.</summary>
    public int? MaxIdentifierLength { get; internal set; }
}

/// <summary>A resolved schema.</summary>
public sealed class RSchema : RObject
{
    /// <inheritdoc/>
    public override string Kind => "schema";

    /// <summary>The schema name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>Tables in the schema, by name.</summary>
    public RList<RTable> Tables { get; internal set; } = RList<RTable>.Empty;

    /// <summary>Views in the schema, by name.</summary>
    public RList<RView> Views { get; internal set; } = RList<RView>.Empty;

    /// <summary>Sequences in the schema, by name.</summary>
    public RList<RSequence> Sequences { get; internal set; } = RList<RSequence>.Empty;
}

/// <summary>A resolved table.</summary>
public sealed class RTable : RObject
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

    /// <summary>Indexes.</summary>
    public IReadOnlyList<RIndex> Indexes { get; internal set; } = [];

    /// <summary>The comment.</summary>
    public string? Comment { get; internal set; }

    /// <summary>Whether the table is a relation's junction table.</summary>
    public bool IsJunction { get; internal set; }

    /// <summary>Whether the table is an enum lookup table.</summary>
    public bool IsLookup { get; internal set; }

    /// <summary>The rows of a lookup table.</summary>
    public IReadOnlyList<RLookupRow> LookupRows { get; internal set; } = [];
}

/// <summary>A resolved primary key.</summary>
public sealed class RPrimaryKey
{
    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The key columns, in key order.</summary>
    public IReadOnlyList<RColumn> Columns { get; internal set; } = [];
}

/// <summary>A resolved unique constraint.</summary>
public sealed class RUnique
{
    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The columns.</summary>
    public IReadOnlyList<RColumn> Columns { get; internal set; } = [];
}

/// <summary>A resolved foreign key.</summary>
public sealed class RForeignKey
{
    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The referencing columns.</summary>
    public IReadOnlyList<RColumn> Columns { get; internal set; } = [];

    /// <summary>The referenced table.</summary>
    public RTable ReferencedTable { get; internal set; } = null!;

    /// <summary>The referenced columns.</summary>
    public IReadOnlyList<RColumn> ReferencedColumns { get; internal set; } = [];

    /// <summary><c>no-action</c>, <c>restrict</c>, <c>cascade</c>, <c>set-null</c> or <c>set-default</c>.</summary>
    public string OnDelete { get; internal set; } = "no-action";

    /// <summary>The on-update action, as <see cref="OnDelete"/>.</summary>
    public string OnUpdate { get; internal set; } = "no-action";

    /// <summary>The relation the key implements, if any.</summary>
    public RRelation? Relation { get; internal set; }

    /// <summary>The relation end whose table holds the key, if any.</summary>
    public REnd? End { get; internal set; }
}

/// <summary>A resolved check constraint.</summary>
public sealed class RCheck
{
    /// <summary>The constraint name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The expression for the database's dialect.</summary>
    public string Expression { get; internal set; } = "";
}

/// <summary>A resolved index.</summary>
public sealed class RIndex
{
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

    /// <summary><c>default</c>, <c>btree</c>, <c>hash</c>, <c>gin</c>, <c>gist</c> or <c>clustered</c>.</summary>
    public string Method { get; internal set; } = "default";
}

/// <summary>A column in a resolved index.</summary>
public sealed class RIndexColumn
{
    /// <summary>The column.</summary>
    public RColumn Column { get; internal set; } = null!;

    /// <summary>Whether the column sorts descending.</summary>
    public bool Descending { get; internal set; }
}

/// <summary>A row of an enum lookup table.</summary>
public sealed class RLookupRow
{
    /// <summary>The row id (the member's value, else its ordinal).</summary>
    public long Id { get; internal set; }

    /// <summary>The member's code, else its name.</summary>
    public string Code { get; internal set; } = "";

    /// <summary>The member's display name.</summary>
    public string Name { get; internal set; } = "";
}

/// <summary>A resolved column.</summary>
public sealed class RColumn : RObject
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

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; internal set; }

    /// <summary>The literal default, as a plain CLR value.</summary>
    public object? Default { get; internal set; }

    /// <summary>The default SQL expression for the database's dialect.</summary>
    public string? DefaultSql { get; internal set; }

    /// <summary>Whether the column is an identity column.</summary>
    public bool Identity { get; internal set; }

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
}

/// <summary>A resolved view.</summary>
public sealed class RView : RObject
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

/// <summary>A resolved sequence.</summary>
public sealed class RSequence : RObject
{
    /// <inheritdoc/>
    public override string Kind => "sequence";

    /// <summary>The physical name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; internal set; }

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
