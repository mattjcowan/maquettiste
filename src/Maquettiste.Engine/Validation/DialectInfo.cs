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
        "tsrange", "tstzrange", "daterange", "oid", "name");

    private static readonly FrozenSet<string> SqlServerTypes = Set(
        "bigint", "int", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real", "date", "time",
        "datetime", "datetime2", "datetimeoffset", "smalldatetime", "char", "varchar", "text", "nchar", "nvarchar", "ntext", "binary",
        "varbinary", "image", "uniqueidentifier", "xml", "json", "sql_variant", "rowversion", "timestamp", "hierarchyid", "geography",
        "geometry");

    private static readonly FrozenSet<string> MySqlTypes = Set(
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "dec", "numeric", "fixed", "float", "double",
        "double precision", "real", "bit", "bool", "boolean", "serial", "date", "datetime", "timestamp", "time", "year", "char", "varchar",
        "binary", "varbinary", "tinyblob", "blob", "mediumblob", "longblob", "tinytext", "text", "mediumtext", "longtext", "enum",
        "set", "json", "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon", "geometrycollection");

    private static readonly FrozenSet<string> OracleTypes = Set(
        "number", "integer", "int", "smallint", "float", "binary_float", "binary_double", "decimal", "numeric", "real",
        "double precision", "char", "nchar", "varchar2", "nvarchar2", "varchar", "clob", "nclob", "blob", "bfile", "raw", "long",
        "long raw", "date", "timestamp", "timestamp with time zone", "timestamp with local time zone", "interval year to month",
        "interval day to second", "boolean", "rowid", "urowid", "xmltype", "json");

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
    /// The base names a dialect knows: its fixed list and the base names of a type map's values (pass the effective map, which holds
    /// the embedded defaults with the project's <c>typeMaps</c> applied).
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="typeMap">The type map.</param>
    /// <returns>The base names.</returns>
    public static FrozenSet<string> KnownBaseNames(Dialect dialect, IReadOnlyDictionary<string, string> typeMap)
    {
        var names = new HashSet<string>(dialect switch
        {
            Dialect.PostgreSql => PostgreSqlTypes,
            Dialect.SqlServer => SqlServerTypes,
            Dialect.MySql => MySqlTypes,
            _ => OracleTypes,
        }, StringComparer.Ordinal);
        foreach (var value in typeMap.Values)
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
