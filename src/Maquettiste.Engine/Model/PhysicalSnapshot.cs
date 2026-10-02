using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A committed snapshot of one database's resolved physical model (<c>.maquettiste/snapshots/&lt;db&gt;.json</c>), the
/// baseline of the schema diff (engine-design.md section 14). Every object is keyed by its section 7.3 key; lists are sorted by key.
/// </summary>
public sealed record PhysicalSnapshot
{
    /// <summary>The <c>$schema</c> value; the canonical writer rewrites it.</summary>
    [JsonPropertyName("$schema")]
    public string? SchemaPath { get; init; }

    /// <summary>The database id.</summary>
    public required string Database { get; init; }

    /// <summary>The database name.</summary>
    public required string Name { get; init; }

    /// <summary>The dialect.</summary>
    public required Dialect Dialect { get; init; }

    /// <summary>The revision; incremented by every non-empty diff that is applied.</summary>
    public int Revision { get; init; }

    /// <summary>Tables, sorted by key.</summary>
    public IReadOnlyList<SnapshotTable> Tables { get; init; } = [];

    /// <summary>Views, sorted by key.</summary>
    public IReadOnlyList<SnapshotView> Views { get; init; } = [];

    /// <summary>Sequences, sorted by key.</summary>
    public IReadOnlyList<SnapshotSequence> Sequences { get; init; } = [];

    /// <summary>Database types, sorted by key.</summary>
    public IReadOnlyList<SnapshotDefinition> Types { get; init; } = [];

    /// <summary>Routines, sorted by key.</summary>
    public IReadOnlyList<SnapshotDefinition> Routines { get; init; } = [];

    /// <summary>SQL objects, sorted by key.</summary>
    public IReadOnlyList<SnapshotDefinition> Objects { get; init; } = [];
}

/// <summary>A table in a <see cref="PhysicalSnapshot"/>.</summary>
public sealed record SnapshotTable
{
    /// <summary>The table key (a ULID for file-backed tables, a synthesized key otherwise).</summary>
    public required string Key { get; init; }

    /// <summary>The physical name.</summary>
    public required string Name { get; init; }

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; init; }

    /// <summary>Columns, in position order.</summary>
    public IReadOnlyList<SnapshotColumn> Columns { get; init; } = [];

    /// <summary>The primary key.</summary>
    public SnapshotConstraint? PrimaryKey { get; init; }

    /// <summary>Unique constraints, sorted by key.</summary>
    public IReadOnlyList<SnapshotConstraint> Uniques { get; init; } = [];

    /// <summary>Foreign keys, sorted by key.</summary>
    public IReadOnlyList<SnapshotForeignKey> ForeignKeys { get; init; } = [];

    /// <summary>Check constraints, sorted by key.</summary>
    public IReadOnlyList<SnapshotCheck> Checks { get; init; } = [];

    /// <summary>Indexes, sorted by key.</summary>
    public IReadOnlyList<SnapshotIndex> Indexes { get; init; } = [];

    /// <summary>The database comment.</summary>
    public string? Comment { get; init; }
}

/// <summary>A column in a <see cref="SnapshotTable"/>.</summary>
public sealed record SnapshotColumn
{
    /// <summary>The column key (an attribute path or a column id).</summary>
    public required string Key { get; init; }

    /// <summary>The physical name.</summary>
    public required string Name { get; init; }

    /// <summary>The built-in type keyword.</summary>
    public required string Type { get; init; }

    /// <summary>The length facet.</summary>
    public int? Length { get; init; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; init; }

    /// <summary>The effective native type.</summary>
    public string? NativeType { get; init; }

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; init; }

    /// <summary>The literal default.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>The default SQL expression for the database's dialect.</summary>
    public string? DefaultSql { get; init; }

    /// <summary>Whether the column is an identity column.</summary>
    public bool Identity { get; init; }

    /// <summary>The key of the sequence that supplies values (a sequence id or a synthesized sequence key), when sequence-generated.</summary>
    public string? Sequence { get; init; }

    /// <summary>The computed column expression.</summary>
    public string? Computed { get; init; }

    /// <summary>Whether the computed value is stored (persisted) rather than virtual.</summary>
    public bool ComputedStored { get; init; }

    /// <summary>The collation.</summary>
    public string? Collation { get; init; }

    /// <summary>The database comment.</summary>
    public string? Comment { get; init; }

    /// <summary>For a reference column (<see cref="Type"/> <c>reference</c>): the reference type's id.</summary>
    public string? ReferenceType { get; init; }

    /// <summary>For a reference column: the effective storage strategy, or <see langword="null"/> for template-defined.</summary>
    public string? Strategy { get; init; }
}

/// <summary>A primary key or unique constraint in a snapshot.</summary>
public sealed record SnapshotConstraint
{
    /// <summary>The constraint key.</summary>
    public required string Key { get; init; }

    /// <summary>The physical name.</summary>
    public required string Name { get; init; }

    /// <summary>Column keys.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Whether the constraint is clustered (SQL Server); <see langword="null"/> means the dialect's default.</summary>
    public bool? Clustered { get; init; }
}

/// <summary>A foreign key in a snapshot.</summary>
public sealed record SnapshotForeignKey
{
    /// <summary>The constraint key.</summary>
    public required string Key { get; init; }

    /// <summary>The physical name.</summary>
    public required string Name { get; init; }

    /// <summary>Column keys.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The referenced table key.</summary>
    public required string ReferencedTable { get; init; }

    /// <summary>Referenced column keys.</summary>
    public required IReadOnlyList<string> ReferencedColumns { get; init; }

    /// <summary>The on-delete action.</summary>
    public ReferentialAction OnDelete { get; init; } = ReferentialAction.NoAction;

    /// <summary>The on-update action.</summary>
    public ReferentialAction OnUpdate { get; init; } = ReferentialAction.NoAction;
}

/// <summary>A check constraint in a snapshot.</summary>
public sealed record SnapshotCheck
{
    /// <summary>The constraint key.</summary>
    public required string Key { get; init; }

    /// <summary>The physical name.</summary>
    public required string Name { get; init; }

    /// <summary>The expression for the database's dialect.</summary>
    public required string Expression { get; init; }
}

/// <summary>An index in a snapshot.</summary>
public sealed record SnapshotIndex
{
    /// <summary>The index key.</summary>
    public required string Key { get; init; }

    /// <summary>The physical name.</summary>
    public required string Name { get; init; }

    /// <summary>Indexed columns with sort order (column keys).</summary>
    public required IReadOnlyList<IndexColumn> Columns { get; init; }

    /// <summary>Included column keys.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>The partial-index predicate.</summary>
    public string? Where { get; init; }

    /// <summary>Whether the index is unique.</summary>
    public bool Unique { get; init; }

    /// <summary>The index method.</summary>
    public IndexMethod Method { get; init; } = IndexMethod.Default;
}

/// <summary>A view in a snapshot.</summary>
public sealed record SnapshotView
{
    /// <summary>The view key.</summary>
    public required string Key { get; init; }

    /// <summary>The physical name.</summary>
    public required string Name { get; init; }

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; init; }

    /// <summary>The body for the database's dialect.</summary>
    public required string Body { get; init; }
}

/// <summary>A sequence in a snapshot.</summary>
public sealed record SnapshotSequence
{
    /// <summary>The sequence key.</summary>
    public required string Key { get; init; }

    /// <summary>The physical name.</summary>
    public required string Name { get; init; }

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; init; }

    /// <summary>The built-in integer type keyword.</summary>
    public string Type { get; init; } = "int64";

    /// <summary>The first value.</summary>
    public long Start { get; init; } = 1;

    /// <summary>The increment.</summary>
    public long Increment { get; init; } = 1;

    /// <summary>The minimum value.</summary>
    public long? Min { get; init; }

    /// <summary>The maximum value.</summary>
    public long? Max { get; init; }

    /// <summary>Whether the sequence wraps around.</summary>
    public bool Cycle { get; init; }

    /// <summary>How many values the server caches.</summary>
    public int? Cache { get; init; }
}

/// <summary>
/// A routine, database type or SQL object in a snapshot: what it is and a text that changes whenever its definition for the dialect
/// does (the schema diff compares the text; the template reads the current object for the statements).
/// </summary>
public sealed record SnapshotDefinition
{
    /// <summary>The object's key (its id).</summary>
    public required string Key { get; init; }

    /// <summary>The name.</summary>
    public required string Name { get; init; }

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; init; }

    /// <summary><c>function</c> or <c>procedure</c>; <c>domain</c>, <c>composite</c>, <c>enum</c> or <c>range</c>; or the SQL object's <c>objectKind</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The definition text.</summary>
    public required string Definition { get; init; }
}
