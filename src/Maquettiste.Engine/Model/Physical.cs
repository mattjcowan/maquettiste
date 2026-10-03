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

    /// <summary>
    /// Which entities map here by convention (D6, D46): <see cref="ConventionMapping.All"/> every entity,
    /// <see cref="ConventionMapping.Packages"/> the entities of <see cref="Packages"/> (none when it is empty),
    /// <see cref="ConventionMapping.None"/> none. <see langword="null"/> (a file written before the member existed) keeps the
    /// original rule: every entity when <see cref="Packages"/> is empty, else its packages'. A mapping element places a single
    /// entity whatever this says, and <c>ignore</c> removes one.
    /// </summary>
    public ConventionMapping? ByConvention { get; init; }

    /// <summary>
    /// The packages whose entities (and sub-packages') map here by convention, each with the schema its conventional tables go to;
    /// see <see cref="ByConvention"/>. In JSON an entry without a schema is the package id.
    /// </summary>
    [JsonConverter(typeof(ConventionPackageListConverter))]
    public IReadOnlyList<ConventionPackage> Packages { get; init; } = [];
}

/// <summary>One package a database takes by convention (D6, D46; erratum E26).</summary>
public sealed record ConventionPackage
{
    /// <summary>The package id.</summary>
    [ElementRef(ElementKind.Package)]
    public required string Package { get; init; }

    /// <summary>The id of the database schema the package's conventional tables go to; <see langword="null"/> uses the default.</summary>
    [ElementRef(IndexKinds = ["schema"])]
    public string? Schema { get; init; }

    /// <summary>An entry without a schema.</summary>
    /// <param name="package">The package id.</param>
    public static implicit operator ConventionPackage(string package) => new() { Package = package };
}

/// <summary>Reads a convention package list whose entries are a package id or <c>{ "package", "schema" }</c>; writes the id when there is no schema.</summary>
internal sealed class ConventionPackageListConverter : JsonConverter<IReadOnlyList<ConventionPackage>>
{
    /// <inheritdoc/>
    public override IReadOnlyList<ConventionPackage> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected an array of convention packages.");
        var list = new List<ConventionPackage>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                list.Add(new ConventionPackage { Package = reader.GetString()! });
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("A convention package is an id or an object.");
            string? package = null, schema = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                if (name == "package")
                    package = reader.GetString();
                else if (name == "schema")
                    schema = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                else
                    reader.Skip();
            }

            list.Add(new ConventionPackage { Package = package ?? throw new JsonException("A convention package names its package."), Schema = schema });
        }

        return list;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ConventionPackage> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value)
        {
            if (entry.Schema is null)
            {
                writer.WriteStringValue(entry.Package);
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("package", entry.Package);
            writer.WriteString("schema", entry.Schema);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}

/// <summary>Which entities a database takes by convention (D46).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConventionMapping>))]
public enum ConventionMapping
{
    /// <summary>Every entity: <c>all</c>.</summary>
    [JsonStringEnumMemberName("all")] All,

    /// <summary>The entities of the database's <c>packages</c> and their sub-packages: <c>packages</c>.</summary>
    [JsonStringEnumMemberName("packages")] Packages,

    /// <summary>None; only mapping elements place entities here: <c>none</c>.</summary>
    [JsonStringEnumMemberName("none")] None,
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

    /// <summary>
    /// A native type that replaces the dialect map's, or the id or name of a database type of the same database, which the column then
    /// uses (its native name for the dialect).
    /// </summary>
    [ElementRef(Keyed = true)]
    public string? NativeType { get; init; }

    /// <summary>
    /// For string and text columns: <see langword="true"/> for Unicode text, <see langword="false"/> for single-byte text,
    /// <see langword="null"/> for the dialect's default (the type map's <c>string:unicode</c>, <c>string:ansi</c> and
    /// <c>text:unicode</c> entries).
    /// </summary>
    public bool? Unicode { get; init; }

    /// <summary>For string and binary columns: a fixed-length type (the type map's <c>string:fixed</c> and <c>binary:fixed</c> entries).</summary>
    public bool FixedLength { get; init; }

    /// <summary>Nullability. On a designed column <see langword="null"/> means nullable; on an overlay it keeps the synthesized value.</summary>
    public bool? Nullable { get; init; }

    /// <summary>A literal default value.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>A default SQL expression per dialect name, or <c>"*"</c> for every dialect.</summary>
    public IReadOnlyDictionary<string, string> DefaultSql { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>The name of the default constraint where the dialect names defaults (SQL Server); <see langword="null"/> uses <c>df_&lt;table&gt;_&lt;column&gt;</c>.</summary>
    public string? DefaultName { get; init; }

    /// <summary>How the database generates the value.</summary>
    public ColumnGeneration? Generated { get; init; }

    /// <summary>The identity's options (first value, step, always generated), read when <see cref="Generated"/> is identity.</summary>
    public ColumnIdentity? Identity { get; init; }

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

/// <summary>The options of an identity column.</summary>
public sealed record ColumnIdentity
{
    /// <summary>The first value; <see langword="null"/> for the dialect's (1).</summary>
    public long? Seed { get; init; }

    /// <summary>The step between values; <see langword="null"/> for the dialect's (1).</summary>
    public long? Increment { get; init; }

    /// <summary>
    /// Whether the database always generates the value and refuses one given by an insert (GENERATED ALWAYS), rather than generating
    /// it only when an insert gives none (GENERATED BY DEFAULT).
    /// </summary>
    public bool Always { get; init; }
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

    /// <summary>Whether two rows with nulls in the columns conflict (PostgreSQL 15 <c>NULLS NOT DISTINCT</c>).</summary>
    public bool NullsNotDistinct { get; init; }
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

    /// <summary>When the key is checked (PostgreSQL, SQLite and Oracle; the other dialects check every statement).</summary>
    public Deferrability Deferrable { get; init; } = Deferrability.NotDeferrable;
}

/// <summary>When a foreign key is checked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Deferrability>))]
public enum Deferrability
{
    /// <summary>At the end of every statement: <c>not-deferrable</c>.</summary>
    [JsonStringEnumMemberName("not-deferrable")] NotDeferrable,

    /// <summary>At the end of every statement unless a transaction defers it: <c>initially-immediate</c>.</summary>
    [JsonStringEnumMemberName("initially-immediate")] InitiallyImmediate,

    /// <summary>At commit unless a transaction asks for it sooner: <c>initially-deferred</c>.</summary>
    [JsonStringEnumMemberName("initially-deferred")] InitiallyDeferred,
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

    /// <summary>For a column check: the id or key of the column it constrains.</summary>
    [ElementRef(Keyed = true)]
    public string? Column { get; init; }

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

/// <summary>A column of an index, or an expression (a functional index).</summary>
public sealed record IndexColumn
{
    /// <summary>A column id or synthesized column key; <see langword="null"/> for an expression.</summary>
    [ElementRef(Keyed = true)]
    public string? Column { get; init; }

    /// <summary>An expression per dialect name, or <c>"*"</c> for every dialect, indexed instead of a column.</summary>
    public IReadOnlyDictionary<string, string>? Expression { get; init; }

    /// <summary>Whether the column sorts descending.</summary>
    public bool Descending { get; init; }

    /// <summary>A key prefix length: only the first characters or bytes are indexed (MySQL; a text or blob column needs one).</summary>
    public int? Length { get; init; }
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

    /// <summary>Whether the DDL names <see cref="Columns"/> in CREATE VIEW (<c>CREATE VIEW v (a, b) AS ...</c>).</summary>
    public bool ColumnList { get; init; }

    /// <summary>Whether writes through the view must satisfy its WHERE (<c>WITH CHECK OPTION</c>).</summary>
    public bool WithCheckOption { get; init; }

    /// <summary>Whether the view stores its rows (a materialized view: PostgreSQL and Oracle).</summary>
    public bool Materialized { get; init; }

    /// <summary>Ids of the tables, views, sequences, routines, database types and SQL objects that must exist first.</summary>
    [ElementRef(ElementKind.Table, ElementKind.View, ElementKind.Sequence, ElementKind.Routine, ElementKind.DatabaseType, ElementKind.SqlObject)]
    public IReadOnlyList<string> DependsOn { get; init; } = [];

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

/// <summary>A stored function or procedure of a database (<c>model/databases/&lt;db&gt;/routines/</c>).</summary>
public sealed record Routine : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Routine;

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The id of the schema; <see langword="null"/> means the default schema.</summary>
    [ElementRef(IndexKinds = ["schema"])]
    public string? Schema { get; init; }

    /// <summary>A function or a procedure.</summary>
    public RoutineKind RoutineKind { get; init; } = RoutineKind.Function;

    /// <summary>The parameters, in order.</summary>
    public IReadOnlyList<RoutineParameter> Parameters { get; init; } = [];

    /// <summary>The result, or <see langword="null"/> when the routine returns nothing (a procedure).</summary>
    public RoutineReturns? Returns { get; init; }

    /// <summary>The body's language, or <see langword="null"/> for the dialect's own.</summary>
    public string? Language { get; init; }

    /// <summary>The body per dialect name, or <c>"*"</c> for every dialect.</summary>
    public required IReadOnlyDictionary<string, string> Body { get; init; }

    /// <summary>Whether the routine returns the same result for the same arguments.</summary>
    public bool Deterministic { get; init; }

    /// <summary>Whose rights the routine runs with.</summary>
    public RoutineSecurity Security { get; init; } = RoutineSecurity.Invoker;

    /// <summary>Ids of the tables, views, sequences, routines, database types and SQL objects that must exist first.</summary>
    [ElementRef(ElementKind.Table, ElementKind.View, ElementKind.Sequence, ElementKind.Routine, ElementKind.DatabaseType, ElementKind.SqlObject)]
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>A database comment.</summary>
    public string? Comment { get; init; }
}

/// <summary>Whether a routine is a function or a procedure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoutineKind>))]
public enum RoutineKind
{
    /// <summary>Returns a value or a table: <c>function</c>.</summary>
    [JsonStringEnumMemberName("function")] Function,

    /// <summary>Called for its effects: <c>procedure</c>.</summary>
    [JsonStringEnumMemberName("procedure")] Procedure,
}

/// <summary>Whose rights a routine runs with.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoutineSecurity>))]
public enum RoutineSecurity
{
    /// <summary>The caller's: <c>invoker</c>.</summary>
    [JsonStringEnumMemberName("invoker")] Invoker,

    /// <summary>The owner's: <c>definer</c>.</summary>
    [JsonStringEnumMemberName("definer")] Definer,
}

/// <summary>The direction of a routine parameter.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ParameterMode>))]
public enum ParameterMode
{
    /// <summary>Passed in: <c>in</c>.</summary>
    [JsonStringEnumMemberName("in")] In,

    /// <summary>Passed out: <c>out</c>.</summary>
    [JsonStringEnumMemberName("out")] Out,

    /// <summary>Both: <c>inout</c>.</summary>
    [JsonStringEnumMemberName("inout")] InOut,
}

/// <summary>A parameter of a routine.</summary>
public sealed record RoutineParameter
{
    /// <summary>The parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>A built-in type keyword or the id of a database type of the same database.</summary>
    [ElementRef(Keyed = true)]
    public string? Type { get; init; }

    /// <summary>The length facet.</summary>
    public int? Length { get; init; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; init; }

    /// <summary>A native type that replaces the one the type gives.</summary>
    public string? NativeType { get; init; }

    /// <summary>The direction.</summary>
    public ParameterMode Mode { get; init; } = ParameterMode.In;

    /// <summary>A default value as SQL text.</summary>
    public string? Default { get; init; }
}

/// <summary>The result of a routine: a single value or a table.</summary>
public sealed record RoutineReturns
{
    /// <summary>For a single value: a built-in type keyword or the id of a database type of the same database.</summary>
    [ElementRef(Keyed = true)]
    public string? Type { get; init; }

    /// <summary>The length facet.</summary>
    public int? Length { get; init; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; init; }

    /// <summary>A native type that replaces the one the type gives, or a result no built-in type names.</summary>
    public string? NativeType { get; init; }

    /// <summary>For a table result: its columns; <see langword="null"/> for a single value.</summary>
    public IReadOnlyList<RoutineColumn>? Table { get; init; }
}

/// <summary>A column of a routine's table result.</summary>
public sealed record RoutineColumn
{
    /// <summary>The column name.</summary>
    public required string Name { get; init; }

    /// <summary>A built-in type keyword or the id of a database type of the same database.</summary>
    [ElementRef(Keyed = true)]
    public string? Type { get; init; }

    /// <summary>The length facet.</summary>
    public int? Length { get; init; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; init; }

    /// <summary>A native type that replaces the one the type gives.</summary>
    public string? NativeType { get; init; }

    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; init; } = true;
}

/// <summary>A type a database owns: a domain, composite, enumeration or range (<c>model/databases/&lt;db&gt;/types/</c>).</summary>
public sealed record DatabaseType : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.DatabaseType;

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The id of the schema; <see langword="null"/> means the default schema.</summary>
    [ElementRef(IndexKinds = ["schema"])]
    public string? Schema { get; init; }

    /// <summary>What kind of type it is.</summary>
    public required DatabaseTypeKind TypeKind { get; init; }

    /// <summary>For a domain: the built-in type it restricts.</summary>
    public string? Base { get; init; }

    /// <summary>The length facet of the base.</summary>
    public int? Length { get; init; }

    /// <summary>The precision facet of the base.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale facet of the base.</summary>
    public int? Scale { get; init; }

    /// <summary>For a domain: a CHECK expression over <c>VALUE</c>.</summary>
    public string? Check { get; init; }

    /// <summary>For an enum: the labels, in order.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>For a composite: the fields, in order.</summary>
    public IReadOnlyList<DatabaseTypeField> Fields { get; init; } = [];

    /// <summary>For a range: the built-in type of its bounds.</summary>
    public string? Subtype { get; init; }

    /// <summary>The definition per dialect name (the text after the type's name), or <c>"*"</c>; it replaces the structured form.</summary>
    public IReadOnlyDictionary<string, string> Definition { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>The name columns and parameters write, when it is not the type's own.</summary>
    public string? NativeName { get; init; }

    /// <summary>A database comment.</summary>
    public string? Comment { get; init; }
}

/// <summary>What kind of type a database type is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatabaseTypeKind>))]
public enum DatabaseTypeKind
{
    /// <summary>A built-in type with a constraint: <c>domain</c>.</summary>
    [JsonStringEnumMemberName("domain")] Domain,

    /// <summary>Named fields: <c>composite</c>.</summary>
    [JsonStringEnumMemberName("composite")] Composite,

    /// <summary>A list of labels: <c>enum</c>.</summary>
    [JsonStringEnumMemberName("enum")] Enum,

    /// <summary>A range over a subtype: <c>range</c>.</summary>
    [JsonStringEnumMemberName("range")] Range,
}

/// <summary>A field of a composite database type.</summary>
public sealed record DatabaseTypeField
{
    /// <summary>The field name.</summary>
    public required string Name { get; init; }

    /// <summary>A built-in type keyword or the id of a database type of the same database.</summary>
    [ElementRef(Keyed = true)]
    public string? Type { get; init; }

    /// <summary>The length facet.</summary>
    public int? Length { get; init; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; init; }

    /// <summary>A native type that replaces the one the type gives.</summary>
    public string? NativeType { get; init; }
}

/// <summary>A named database object the model does not type: SQL statements per dialect (<c>model/databases/&lt;db&gt;/objects/</c>).</summary>
public sealed record SqlObject : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.SqlObject;

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The id of the schema; <see langword="null"/> means the default schema.</summary>
    [ElementRef(IndexKinds = ["schema"])]
    public string? Schema { get; init; }

    /// <summary>What the object is, in free text (trigger, grant, extension...).</summary>
    public required string ObjectKind { get; init; }

    /// <summary>Whether the statements run before the types and tables or after the routines and views.</summary>
    public SqlObjectPhase Phase { get; init; } = SqlObjectPhase.After;

    /// <summary>Ids of the tables, views, sequences, routines, database types and SQL objects that must exist first.</summary>
    [ElementRef(ElementKind.Table, ElementKind.View, ElementKind.Sequence, ElementKind.Routine, ElementKind.DatabaseType, ElementKind.SqlObject)]
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>The statements per dialect name, or <c>"*"</c> for every dialect.</summary>
    public required IReadOnlyDictionary<string, string> Body { get; init; }
}

/// <summary>When a SQL object's statements run, relative to the tables.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SqlObjectPhase>))]
public enum SqlObjectPhase
{
    /// <summary>Before the database types and tables: <c>before</c>.</summary>
    [JsonStringEnumMemberName("before")] Before,

    /// <summary>After the routines and views: <c>after</c>.</summary>
    [JsonStringEnumMemberName("after")] After,
}
