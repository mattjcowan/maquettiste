using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Dialect facts the validator needs: JSON names, default schemas, identifier limits (MQ4001) and the native type names each
/// dialect knows (MQ4006, MQ4016). The native type lists are the validator's own; the values of the resolver's dialect maps
/// (<c>Resolution/Dialects/*.json</c>) with the project's <c>typeMaps</c> applied extend them.
/// </summary>
internal static partial class DialectInfo
{
    private static readonly FrozenSet<string> PostgreSqlTypes = Set(
        "smallint", "integer", "int", "int2", "int4", "int8", "bigint", "decimal", "numeric", "real", "float", "float4", "float8",
        "double precision", "smallserial", "serial", "serial2", "serial4", "serial8", "bigserial", "money", "varchar", "character varying",
        "char", "character", "bpchar", "text", "citext", "bytea", "timestamp", "timestamptz", "timestamp with time zone",
        "timestamp without time zone", "date", "time", "timetz", "time with time zone", "time without time zone", "interval", "boolean",
        "bool", "uuid", "json", "jsonb", "xml", "inet", "cidr", "macaddr", "macaddr8", "bit", "bit varying", "varbit", "tsvector",
        "tsquery", "point", "line", "lseg", "box", "path", "polygon", "circle", "hstore", "int4range", "int8range", "numrange",
        "tsrange", "tstzrange", "daterange", "oid", "name",
        // The rest of the documented built-in data types: system identifiers and the object identifier aliases, log sequence numbers
        // and snapshots, multiranges and the SQL/JSON path type.
        "pg_lsn", "pg_snapshot", "txid_snapshot", "xid", "xid8", "cid", "tid", "regclass", "regcollation", "regconfig", "regdictionary",
        "regnamespace", "regoper", "regoperator", "regproc", "regprocedure", "regrole", "regtype", "jsonpath", "int4multirange",
        "int8multirange", "nummultirange", "tsmultirange", "tstzmultirange", "datemultirange",
        // Extension types, as citext and hstore above: ltree's three, cube and earth, and the spatial geometry and geography.
        "ltree", "lquery", "ltxtquery", "cube", "earth", "geometry", "geography");

    private static readonly FrozenSet<string> SqlServerTypes = Set(
        "bigint", "int", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real", "date", "time",
        "datetime", "datetime2", "datetimeoffset", "smalldatetime", "char", "varchar", "text", "nchar", "nvarchar", "ntext", "binary",
        "varbinary", "image", "uniqueidentifier", "xml", "json", "sql_variant", "rowversion", "timestamp", "hierarchyid", "geography",
        "geometry", "vector", "sysname",
        // The documented synonyms of the ISO names.
        "integer", "dec", "double precision", "character", "character varying", "char varying", "binary varying", "national character",
        "national char", "national character varying", "national char varying", "national text");

    private static readonly FrozenSet<string> MySqlTypes = Set(
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "dec", "numeric", "fixed", "float", "double",
        "double precision", "real", "bit", "bool", "boolean", "serial", "date", "datetime", "timestamp", "time", "year", "char", "varchar",
        "binary", "varbinary", "tinyblob", "blob", "mediumblob", "longblob", "tinytext", "text", "mediumtext", "longtext", "enum",
        "set", "json", "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon", "geometrycollection",
        "geomcollection", "vector", "uuid", "inet4", "inet6",
        // The documented synonyms: national character types and the names kept for other databases' types.
        "nchar", "nvarchar", "national char", "national character", "national varchar", "national character varying", "national char varying",
        "nchar varchar", "character", "character varying", "char varying", "long", "long varchar", "long varbinary", "int1", "int2", "int3",
        "int4", "int8", "middleint", "float4", "float8");

    private static readonly FrozenSet<string> OracleTypes = Set(
        "number", "integer", "int", "smallint", "float", "binary_float", "binary_double", "decimal", "numeric", "real",
        "double precision", "char", "nchar", "varchar2", "nvarchar2", "varchar", "clob", "nclob", "blob", "bfile", "raw", "long",
        "long raw", "date", "timestamp", "timestamp with time zone", "timestamp with local time zone", "interval year to month",
        "interval day to second", "boolean", "rowid", "urowid", "xmltype", "json", "vector",
        // The documented ANSI names, the spatial types and the supplied any and URI types.
        "dec", "character", "character varying", "char varying", "national character", "national char", "national character varying",
        "national char varying", "nchar varying", "sdo_geometry", "sdo_topo_geometry", "sdo_georaster", "anydata", "anytype", "anydataset",
        "uritype", "dburitype", "xdburitype", "httpuritype");

    /// <summary>The JSON name of a dialect (<c>postgresql</c>, <c>sqlserver</c>, <c>mysql</c>, <c>sqlite</c>, <c>oracle</c>).</summary>
    /// <param name="dialect">The dialect.</param>
    /// <returns>The name.</returns>
    public static string Name(Dialect dialect) => dialect switch
    {
        Dialect.PostgreSql => "postgresql",
        Dialect.SqlServer => "sqlserver",
        Dialect.MySql => "mysql",
        Dialect.Sqlite => "sqlite",
        _ => "oracle",
    };

    /// <summary>The dialect's default schema: PostgreSQL <c>public</c>, SQL Server <c>dbo</c>, others none.</summary>
    /// <param name="dialect">The dialect, when known.</param>
    /// <returns>The schema name, or an empty string.</returns>
    public static string DefaultSchema(Dialect? dialect) => dialect switch
    {
        Dialect.PostgreSql => "public",
        Dialect.SqlServer => "dbo",
        _ => "",
    };

    /// <summary>The effective identifier limit of a database: its <c>maxIdentifierLength</c>, else the dialect's.</summary>
    /// <param name="database">The database.</param>
    /// <returns>The limit, or <see langword="null"/> when there is none (SQLite).</returns>
    public static int? IdentifierLimit(Database database) => database.MaxIdentifierLength ?? database.Dialect switch
    {
        Dialect.PostgreSql => 63,
        Dialect.SqlServer => 128,
        Dialect.MySql => 64,
        Dialect.Oracle => 128,
        _ => null,
    };

    /// <summary>
    /// The length of an identifier as the dialect measures it: UTF-8 bytes on PostgreSQL and Oracle, characters elsewhere.
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="identifier">The identifier.</param>
    /// <returns>The length.</returns>
    public static int IdentifierLength(Dialect dialect, string identifier) => dialect is Dialect.PostgreSql or Dialect.Oracle
        ? Encoding.UTF8.GetByteCount(identifier)
        : identifier.EnumerateRunes().Count();

    /// <summary>The unit <see cref="IdentifierLength"/> counts in: <c>bytes</c> on PostgreSQL and Oracle, <c>characters</c> elsewhere.</summary>
    /// <param name="dialect">The dialect.</param>
    /// <returns>The unit, plural.</returns>
    public static string IdentifierUnit(Dialect dialect) => dialect is Dialect.PostgreSql or Dialect.Oracle ? "bytes" : "characters";

    /// <summary>
    /// An identifier as a database keeps it: cut to the limit in the dialect's unit (<see cref="IdentifierLength"/>), never inside a
    /// character, as PostgreSQL truncates a longer name and only raises a notice. Without a limit the identifier is unchanged.
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="identifier">The identifier.</param>
    /// <param name="limit">The limit, or <see langword="null"/> for none.</param>
    /// <returns>The identifier, cut.</returns>
    public static string CutIdentifier(Dialect dialect, string identifier, int? limit)
    {
        if (limit is not { } max || IdentifierLength(dialect, identifier) <= max)
            return identifier;
        var bytes = dialect is Dialect.PostgreSql or Dialect.Oracle;
        var used = 0;
        var end = 0;
        foreach (var rune in identifier.EnumerateRunes())
        {
            var size = bytes ? rune.Utf8SequenceLength : 1;
            if (used + size > max)
                break;
            used += size;
            end += rune.Utf16SequenceLength;
        }

        return identifier[..end];
    }

    /// <summary>
    /// The namespaces a dialect keeps index and constraint names in (MQ4068, next-phase-1-design.md section 3.5), in the order the
    /// rule tries them. SQL objects are opaque text and join none; MQ4002 checks tables, views and sequences against each other.
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <returns>The namespaces.</returns>
    public static ImmutableArray<NameSpace> NameSpaces(Dialect dialect) => dialect switch
    {
        Dialect.PostgreSql => PostgreSqlNameSpaces,
        Dialect.SqlServer => SqlServerNameSpaces,
        Dialect.MySql => MySqlNameSpaces,
        Dialect.Oracle => OracleNameSpaces,
        _ => SqliteNameSpaces,
    };

    private static readonly ImmutableArray<NameSpace> PostgreSqlNameSpaces =
    [
        new(NameScope.Schema, SchemaObjects.Tables | SchemaObjects.Views | SchemaObjects.Sequences,
            NameHolders.Index | NameHolders.PrimaryKey | NameHolders.UniqueKey | NameHolders.Exclusion,
            "index and key names in one namespace per schema, shared with its tables, views and sequences"),
        new(NameScope.Table, SchemaObjects.None,
            NameHolders.PrimaryKey | NameHolders.UniqueKey | NameHolders.ForeignKey | NameHolders.Check | NameHolders.Exclusion,
            "constraint names in one namespace per table"),
    ];

    private static readonly ImmutableArray<NameSpace> SqlServerNameSpaces =
    [
        new(NameScope.Schema, SchemaObjects.Tables | SchemaObjects.Views | SchemaObjects.Sequences | SchemaObjects.Routines,
            NameHolders.PrimaryKey | NameHolders.UniqueKey | NameHolders.ForeignKey | NameHolders.Check | NameHolders.DefaultConstraint,
            "constraint names in one namespace per schema, shared with its tables, views, sequences and routines"),
        new(NameScope.Table, SchemaObjects.None, NameHolders.Index | NameHolders.PrimaryKey | NameHolders.UniqueKey,
            "index names, the primary and unique keys' indexes included, in one namespace per table"),
    ];

    private static readonly ImmutableArray<NameSpace> MySqlNameSpaces =
    [
        new(NameScope.Schema, SchemaObjects.None, NameHolders.ForeignKey, "foreign key names in one namespace per schema"),
        new(NameScope.Schema, SchemaObjects.None, NameHolders.Check, "check constraint names in one namespace per schema"),
        new(NameScope.Table, SchemaObjects.None, NameHolders.Index | NameHolders.UniqueKey,
            "index and unique key names in one namespace per table (the primary key is always PRIMARY)"),
    ];

    private static readonly ImmutableArray<NameSpace> OracleNameSpaces =
    [
        new(NameScope.Schema, SchemaObjects.None, NameHolders.Index | NameHolders.PrimaryKey | NameHolders.UniqueKey,
            "index names, the keys' indexes included, in one namespace per schema"),
        new(NameScope.Schema, SchemaObjects.None,
            NameHolders.PrimaryKey | NameHolders.UniqueKey | NameHolders.ForeignKey | NameHolders.Check,
            "constraint names in one namespace per schema"),
    ];

    private static readonly ImmutableArray<NameSpace> SqliteNameSpaces =
    [
        new(NameScope.Schema, SchemaObjects.Tables | SchemaObjects.Views, NameHolders.Index,
            "index names in one namespace per schema, shared with its tables and views"),
    ];

    /// <summary>
    /// Whether a native type is known to a dialect: its base name (see <see cref="Parse"/>) is a native type of the dialect, the base
    /// of a value of the dialect's embedded type map (<c>Resolution/Dialects/*.json</c>) or the base of a <c>typeMaps</c> value.
    /// SQLite accepts any type name. The model's own reference types and enums are not considered here (see
    /// <see cref="ValidationContext.ClassifyNativeType"/>).
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="nativeType">The native type as written.</param>
    /// <param name="typeMap">The project's <c>typeMaps</c> entry for the dialect, when any.</param>
    /// <returns><see langword="true"/> when known.</returns>
    public static bool IsKnownNativeType(Dialect dialect, string nativeType, IReadOnlyDictionary<string, string>? typeMap)
    {
        if (dialect == Dialect.Sqlite)
            return true;
        var name = Parse(nativeType).BaseName;
        if (name.Length == 0)
            return false;
        if (KnownBaseNames(dialect, Resolution.DialectTypeMaps.Default(dialect)).Contains(name))
            return true;
        return typeMap is not null && typeMap.Values.Any(v => Parse(v).BaseName == name);
    }

    /// <summary>
    /// The base names a dialect knows: its fixed list, the base names of a type map's values (pass the effective map, which holds
    /// the embedded defaults with the project's <c>typeMaps</c> applied) and the base names of further native types, such as the
    /// native types the model's custom types declare for the dialect.
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="typeMap">The type map.</param>
    /// <param name="more">Further native types, as written, or <see langword="null"/>.</param>
    /// <returns>The base names.</returns>
    public static FrozenSet<string> KnownBaseNames(Dialect dialect, IReadOnlyDictionary<string, string> typeMap, IEnumerable<string>? more = null)
    {
        var names = new HashSet<string>(dialect switch
        {
            Dialect.PostgreSql => PostgreSqlTypes,
            Dialect.SqlServer => SqlServerTypes,
            Dialect.MySql => MySqlTypes,
            _ => OracleTypes,
        }, StringComparer.Ordinal);
        foreach (var value in more is null ? typeMap.Values : typeMap.Values.Concat(more))
        {
            var name = Parse(value).BaseName;
            if (name.Length > 0)
                names.Add(name);
        }

        return names.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads a native type as written: arguments and array brackets are removed, identifier quotes (<c>"..."</c>, <c>[...]</c>,
    /// <c>`...`</c>) are stripped and dots outside quotes separate a schema (or other) prefix from the type name. The base name is
    /// the last part reduced by <see cref="BaseName"/>.
    /// </summary>
    /// <param name="nativeType">The native type.</param>
    /// <returns>The parsed name.</returns>
    public static NativeTypeName Parse(string nativeType)
    {
        var text = ArraySuffix().Replace(Arguments().Replace(nativeType, " "), " ");
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var close = c switch { '"' => '"', '[' => ']', '`' => '`', _ => '\0' };
            if (close != '\0')
            {
                quoted = true;
                for (i++; i < text.Length; i++)
                {
                    if (text[i] == close)
                    {
                        if (i + 1 < text.Length && text[i + 1] == close)
                        {
                            current.Append(close); // a doubled quote inside a quoted identifier
                            i++;
                            continue;
                        }

                        break;
                    }

                    current.Append(text[i]);
                }

                continue;
            }

            if (c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        var cleaned = parts.Select(p => Spaces().Replace(p, " ").Trim().ToLowerInvariant()).ToImmutableArray();
        return new NativeTypeName(BaseName(cleaned[^1]), cleaned, quoted || cleaned.Length > 1);
    }

    /// <summary>
    /// Reduces a native type to its base name: lower-cased, arguments, array brackets and MySQL <c>unsigned</c>/<c>zerofill</c>
    /// removed, whitespace collapsed.
    /// </summary>
    /// <param name="nativeType">The native type.</param>
    /// <returns>The base name.</returns>
    public static string BaseName(string nativeType)
    {
        var text = Arguments().Replace(nativeType.ToLowerInvariant(), " ");
        text = text.Replace("[]", " ", StringComparison.Ordinal);
        text = Modifiers().Replace(text, " ");
        return Spaces().Replace(text, " ").Trim();
    }

    private static FrozenSet<string> Set(params string[] names) => names.ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Arguments();

    [GeneratedRegex(@"\[\d*\]", RegexOptions.CultureInvariant)]
    private static partial Regex ArraySuffix();

    [GeneratedRegex(@"\b(unsigned|zerofill|signed)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Modifiers();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}

/// <summary>A native type as written, read by <see cref="DialectInfo.Parse"/>.</summary>
/// <param name="BaseName">The type name's base (lower-cased, arguments, array brackets and modifiers removed), or empty.</param>
/// <param name="Parts">The dot-separated parts without quotes, lower-cased; the last is the type name.</param>
/// <param name="IsQuotedOrQualified">Whether the type was written with identifier quotes or a prefix such as a schema.</param>
internal sealed record NativeTypeName(string BaseName, ImmutableArray<string> Parts, bool IsQuotedOrQualified);

/// <summary>Where a namespace of <see cref="DialectInfo.NameSpaces"/> is kept: one per schema, or one per table.</summary>
internal enum NameScope
{
    /// <summary>One namespace per schema (per database where the dialect has no schemas).</summary>
    Schema,

    /// <summary>One namespace per table.</summary>
    Table,
}

/// <summary>The schema objects whose names a schema namespace shares with indexes and constraints.</summary>
[Flags]
internal enum SchemaObjects
{
    /// <summary>None: the namespace holds index or constraint names only.</summary>
    None = 0,

    /// <summary>Tables.</summary>
    Tables = 1,

    /// <summary>Views.</summary>
    Views = 2,

    /// <summary>Sequences.</summary>
    Sequences = 4,

    /// <summary>Routines.</summary>
    Routines = 8,
}

/// <summary>The indexes and constraints that hold a name in a namespace of <see cref="DialectInfo.NameSpaces"/>.</summary>
[Flags]
internal enum NameHolders
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The primary key (or, where the namespace holds index names, the index it creates).</summary>
    PrimaryKey = 1,

    /// <summary>Unique keys (or the indexes they create).</summary>
    UniqueKey = 2,

    /// <summary>Foreign keys.</summary>
    ForeignKey = 4,

    /// <summary>Check constraints.</summary>
    Check = 8,

    /// <summary>Exclusion constraints (or the indexes they create).</summary>
    Exclusion = 16,

    /// <summary>Default constraints, by a column's <c>defaultName</c>.</summary>
    DefaultConstraint = 32,

    /// <summary>Indexes.</summary>
    Index = 64,
}

/// <summary>A namespace a dialect keeps index and constraint names in (MQ4068).</summary>
/// <param name="Scope">Whether one is kept per schema or per table.</param>
/// <param name="Objects">The schema objects whose names it shares, so an index or constraint may not take them.</param>
/// <param name="Holders">The indexes and constraints whose names it holds.</param>
/// <param name="Text">How the dialect keeps it, for messages ("constraint names in one namespace per table").</param>
internal sealed record NameSpace(NameScope Scope, SchemaObjects Objects, NameHolders Holders, string Text);
