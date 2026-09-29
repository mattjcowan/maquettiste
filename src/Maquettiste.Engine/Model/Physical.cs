using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>A physical store with a dialect (<c>model/databases/&lt;db&gt;/database.json</c>; SPEC section 9).</summary>
public sealed record Database : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Database;

    /// <summary>The SQL dialect.</summary>
    public required Dialect Dialect { get; init; }

    /// <summary>The target server version.</summary>
    public string? Version { get; init; }

    /// <summary>The default schema; <see langword="null"/> means PostgreSQL <c>public</c>, SQL Server <c>dbo</c>, others none.</summary>
    public string? DefaultSchema { get; init; }

    /// <summary>The schemas declared in the database.</summary>
    public IReadOnlyList<DbSchema> Schemas { get; init; } = [];

    /// <summary>When identifiers are quoted.</summary>
    public Quoting Quoting { get; init; } = Quoting.Reserved;

    /// <summary>The identifier length limit; <see langword="null"/> uses the dialect's (PostgreSQL 63, SQL Server 128, MySQL 64, Oracle 128, SQLite none).</summary>
    public int? MaxIdentifierLength { get; init; }

    /// <summary>Ids of the packages whose entities (and sub-packages') map here; empty means every package (D6).</summary>
    [ElementRef(ElementKind.Package)]
    public IReadOnlyList<string> Packages { get; init; } = [];
}

/// <summary>A schema inside a database.</summary>
public sealed record DbSchema : ElementBase;

/// <summary>A SQL dialect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Dialect>))]
public enum Dialect
{
    /// <summary>PostgreSQL: <c>postgresql</c>.</summary>
    [JsonStringEnumMemberName("postgresql")] PostgreSql,

    /// <summary>Microsoft SQL Server: <c>sqlserver</c>.</summary>
    [JsonStringEnumMemberName("sqlserver")] SqlServer,

    /// <summary>MySQL or MariaDB: <c>mysql</c>.</summary>
    [JsonStringEnumMemberName("mysql")] MySql,

    /// <summary>SQLite: <c>sqlite</c>.</summary>
    [JsonStringEnumMemberName("sqlite")] Sqlite,

    /// <summary>Oracle: <c>oracle</c>.</summary>
    [JsonStringEnumMemberName("oracle")] Oracle,
}

/// <summary>When a database's identifiers are quoted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Quoting>))]
public enum Quoting
{
    /// <summary>Always quote: <c>always</c>.</summary>
    [JsonStringEnumMemberName("always")] Always,

    /// <summary>Quote reserved words and names that need it: <c>reserved</c>.</summary>
    [JsonStringEnumMemberName("reserved")] Reserved,

    /// <summary>Never quote: <c>never</c>.</summary>
    [JsonStringEnumMemberName("never")] Never,
}

/// <summary>
/// A physical table (<c>model/databases/&lt;db&gt;/tables/</c>). A synthesized table's file holds only overrides (D9);
/// designed and imported tables hold their full definition.
/// </summary>
public sealed record Table : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Table;

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The id of the schema; <see langword="null"/> means the database's default schema.</summary>
    [ElementRef(IndexKinds = ["schema"])]
    public string? Schema { get; init; }

    /// <summary>How the table came to exist.</summary>
    public TableOrigin Origin { get; init; } = TableOrigin.Designed;

    /// <summary>
    /// For a synthesized table's overlay: the id of the entity whose table it overrides, or, with <see cref="Attribute"/>, the
    /// entity whose child table it overrides. Exactly one of <see cref="Entity"/>, <see cref="Relation"/> and <see cref="Enum"/> is
    /// set on a synthesized table and none on a designed or imported one (D9; the schema enforces it).
    /// </summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>
    /// For a synthesized child table's overlay (a value-object collection or <c>table</c> storage): the id of the attribute, on
    /// <see cref="Entity"/> (own, inherited or virtual), whose child table it overrides; key <c>&lt;entityId&gt;.&lt;attributeId&gt;@&lt;databaseId&gt;</c>.
    /// </summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public string? Attribute { get; init; }

    /// <summary>For a synthesized junction table's overlay: the id of the relation.</summary>
    [ElementRef(ElementKind.Relation)]
    public string? Relation { get; init; }

    /// <summary>Columns: the full list for designed and imported tables; overlays and extra columns for synthesized ones.</summary>
    public IReadOnlyList<Column> Columns { get; init; } = [];

    /// <summary>The primary key.</summary>
    public PrimaryKey? PrimaryKey { get; init; }

    /// <summary>Unique constraints.</summary>
    public IReadOnlyList<UniqueConstraint> Uniques { get; init; } = [];

    /// <summary>Foreign keys.</summary>
    public IReadOnlyList<ForeignKey> ForeignKeys { get; init; } = [];

    /// <summary>Check constraints.</summary>
    public IReadOnlyList<CheckConstraint> Checks { get; init; } = [];

    /// <summary>Indexes.</summary>
    public IReadOnlyList<TableIndex> Indexes { get; init; } = [];

    /// <summary>A database comment.</summary>
    public string? Comment { get; init; }
}

/// <summary>How a table came to exist.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TableOrigin>))]
public enum TableOrigin
{
    /// <summary>Computed from an entity or relation mapping; the file holds overrides only: <c>synthesized</c>.</summary>
    [JsonStringEnumMemberName("synthesized")] Synthesized,

    /// <summary>Designed by hand, with no entity: <c>designed</c>.</summary>
    [JsonStringEnumMemberName("designed")] Designed,

    /// <summary>Reverse engineered, with <c>source</c> provenance: <c>imported</c>.</summary>
    [JsonStringEnumMemberName("imported")] Imported,
}

/// <summary>A table column, or an overlay on a synthesized column.</summary>
public sealed record Column : ElementBase
{
    /// <summary>For an overlay: the synthesized column key it overrides (engine-design.md section 7.3); <see langword="null"/> for a designed or extra column.</summary>
    [ElementRef(Keyed = true)]
    public string? Attribute { get; init; }

    /// <summary>The built-in type keyword; required on designed and extra columns.</summary>
    public string? Type { get; init; }

    /// <summary>The length facet.</summary>
    public int? Length { get; init; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; init; }

    /// <summary>A native type that replaces the dialect map's.</summary>
    public string? NativeType { get; init; }

    /// <summary>Nullability. On a designed column <see langword="null"/> means nullable; on an overlay it keeps the synthesized value.</summary>
    public bool? Nullable { get; init; }

    /// <summary>A literal default value.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>A default SQL expression per dialect name, or <c>"*"</c> for every dialect.</summary>
    public IReadOnlyDictionary<string, string> DefaultSql { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>How the database generates the value.</summary>
    public ColumnGeneration? Generated { get; init; }

    /// <summary>The id of the sequence that supplies values.</summary>
    [ElementRef(ElementKind.Sequence)]
    public string? Sequence { get; init; }

    /// <summary>A computed column expression.</summary>
    public string? Computed { get; init; }

    /// <summary>Whether the computed value is stored.</summary>
    public bool ComputedStored { get; init; }

    /// <summary>The collation.</summary>
    public string? Collation { get; init; }

    /// <summary>A database comment.</summary>
    public string? Comment { get; init; }
}

/// <summary>How a database generates a column's value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ColumnGeneration>))]
public enum ColumnGeneration
{
    /// <summary>An identity column: <c>identity</c>.</summary>
    [JsonStringEnumMemberName("identity")] Identity,

    /// <summary>A sequence default: <c>sequence</c>.</summary>
    [JsonStringEnumMemberName("sequence")] Sequence,
}

/// <summary>A table's primary key.</summary>
public sealed record PrimaryKey
{
    /// <summary>The constraint name; <see langword="null"/> uses the naming convention.</summary>
    public string? Name { get; init; }

    /// <summary>Column ids or synthesized column keys, in key order.</summary>
    [ElementRef(Keyed = true)]
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Whether the key is clustered (SQL Server).</summary>
    public bool? Clustered { get; init; }
}

/// <summary>A unique constraint.</summary>
public sealed record UniqueConstraint
{
    /// <summary>The constraint's id.</summary>
    public required string Id { get; init; }

    /// <summary>The constraint name; <see langword="null"/> uses the naming convention.</summary>
    public string? Name { get; init; }

    /// <summary>Column ids or synthesized column keys.</summary>
    [ElementRef(Keyed = true)]
    public required IReadOnlyList<string> Columns { get; init; }
}

/// <summary>A foreign key.</summary>
public sealed record ForeignKey
{
    /// <summary>The constraint's id.</summary>
    public required string Id { get; init; }

    /// <summary>The constraint name; <see langword="null"/> uses the naming convention.</summary>
    public string? Name { get; init; }

    /// <summary>Column ids or synthesized column keys.</summary>
    [ElementRef(Keyed = true)]
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>A table id or a synthesized table key (engine-design.md section 7.3).</summary>
    [ElementRef(Keyed = true)]
    public required string ReferencesTable { get; init; }

    /// <summary>Referenced column ids or keys; empty means the referenced primary key.</summary>
    [ElementRef(Keyed = true)]
    public IReadOnlyList<string> ReferencesColumns { get; init; } = [];

    /// <summary>The on-delete action.</summary>
    public ReferentialAction OnDelete { get; init; } = ReferentialAction.NoAction;

    /// <summary>The on-update action.</summary>
    public ReferentialAction OnUpdate { get; init; } = ReferentialAction.NoAction;
}

/// <summary>A physical referential action.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReferentialAction>))]
public enum ReferentialAction
{
    /// <summary><c>no-action</c>.</summary>
    [JsonStringEnumMemberName("no-action")] NoAction,

    /// <summary><c>restrict</c>.</summary>
    [JsonStringEnumMemberName("restrict")] Restrict,

    /// <summary><c>cascade</c>.</summary>
    [JsonStringEnumMemberName("cascade")] Cascade,

    /// <summary><c>set-null</c>.</summary>
    [JsonStringEnumMemberName("set-null")] SetNull,

    /// <summary><c>set-default</c>.</summary>
    [JsonStringEnumMemberName("set-default")] SetDefault,
}

/// <summary>A check constraint.</summary>
public sealed record CheckConstraint
{
    /// <summary>The constraint's id.</summary>
    public required string Id { get; init; }

    /// <summary>The constraint name; <see langword="null"/> uses the naming convention.</summary>
    public string? Name { get; init; }

    /// <summary>The SQL expression per dialect name, or <c>"*"</c> for every dialect.</summary>
    public required IReadOnlyDictionary<string, string> Expression { get; init; }
}

/// <summary>An index.</summary>
public sealed record TableIndex
{
    /// <summary>The index's id.</summary>
    public required string Id { get; init; }

    /// <summary>The index name; <see langword="null"/> uses the naming convention.</summary>
    public string? Name { get; init; }

    /// <summary>The indexed columns with their sort order.</summary>
    public required IReadOnlyList<IndexColumn> Columns { get; init; }

    /// <summary>Included (covering) column ids or keys.</summary>
    [ElementRef(Keyed = true)]
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>A partial-index predicate.</summary>
    public string? Where { get; init; }

    /// <summary>Whether the index is unique.</summary>
    public bool Unique { get; init; }

    /// <summary>The index method.</summary>
    public IndexMethod Method { get; init; } = IndexMethod.Default;
}

/// <summary>A column in an index.</summary>
public sealed record IndexColumn
{
    /// <summary>A column id or synthesized column key.</summary>
    [ElementRef(Keyed = true)]
    public required string Column { get; init; }

    /// <summary>Whether the column sorts descending.</summary>
    public bool Descending { get; init; }
}

/// <summary>An index method.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IndexMethod>))]
public enum IndexMethod
{
    /// <summary>The dialect's default: <c>default</c>.</summary>
    [JsonStringEnumMemberName("default")] Default,

    /// <summary><c>btree</c>.</summary>
    [JsonStringEnumMemberName("btree")] Btree,

    /// <summary><c>hash</c>.</summary>
    [JsonStringEnumMemberName("hash")] Hash,

    /// <summary><c>gin</c>.</summary>
    [JsonStringEnumMemberName("gin")] Gin,

    /// <summary><c>gist</c>.</summary>
    [JsonStringEnumMemberName("gist")] Gist,

    /// <summary><c>clustered</c>.</summary>
    [JsonStringEnumMemberName("clustered")] Clustered,
}

/// <summary>A view with per-dialect SQL bodies (<c>model/databases/&lt;db&gt;/views/</c>).</summary>
public sealed record View : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.View;

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The id of the schema; <see langword="null"/> means the default schema.</summary>
    [ElementRef(IndexKinds = ["schema"])]
    public string? Schema { get; init; }

    /// <summary>The SELECT body per dialect name, or <c>"*"</c> for every dialect.</summary>
    public required IReadOnlyDictionary<string, string> Body { get; init; }

    /// <summary>The view's columns, for templates that need types.</summary>
    public IReadOnlyList<ViewColumn> Columns { get; init; } = [];

    /// <summary>A database comment.</summary>
    public string? Comment { get; init; }
}

/// <summary>A column of a view.</summary>
public sealed record ViewColumn
{
    /// <summary>The column name.</summary>
    public required string Name { get; init; }

    /// <summary>The built-in type keyword.</summary>
    public string? Type { get; init; }

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; init; } = true;
}

/// <summary>A database sequence (<c>model/databases/&lt;db&gt;/sequences/</c>).</summary>
public sealed record Sequence : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Sequence;

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The id of the schema; <see langword="null"/> means the default schema.</summary>
    [ElementRef(IndexKinds = ["schema"])]
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
