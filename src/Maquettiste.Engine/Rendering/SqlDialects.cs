using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Rendering;

/// <summary>
/// Per-dialect SQL identifier quoting and literals for the <c>sql_quote</c> and <c>sql_literal</c> helpers (engine-design.md
/// section 9). The reserved-word lists are embedded resources (<c>Rendering/Resources/reserved-&lt;dialect&gt;.txt</c>), loaded once
/// into immutable sets.
/// </summary>
internal static class SqlDialects
{
    private static readonly FrozenDictionary<Dialect, FrozenSet<string>> ReservedWords = LoadReservedWords();

    /// <summary>Parses a dialect name as templates write it (the database's <c>dialect</c> plus common aliases).</summary>
    /// <param name="name">The name.</param>
    /// <param name="dialect">The dialect.</param>
    /// <returns>Whether the name is a dialect.</returns>
    public static bool TryParse(string? name, out Dialect dialect)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "postgresql" or "postgres" or "pg" or "pgsql":
                dialect = Dialect.PostgreSql;
                return true;
            case "sqlserver" or "mssql" or "tsql":
                dialect = Dialect.SqlServer;
                return true;
            case "mysql" or "mariadb":
                dialect = Dialect.MySql;
                return true;
            case "sqlite":
                dialect = Dialect.Sqlite;
                return true;
            case "oracle":
                dialect = Dialect.Oracle;
                return true;
            default:
                dialect = default;
                return false;
        }
    }

    /// <summary>Whether a name is a reserved word of the dialect (case-insensitive).</summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="name">The identifier.</param>
    /// <returns>Whether it is reserved.</returns>
    public static bool IsReserved(Dialect dialect, string name) => ReservedWords[dialect].Contains(name.ToLowerInvariant());

    /// <summary>Whether an identifier can be written unquoted in every dialect: <c>^[A-Za-z_][A-Za-z0-9_]*$</c>.</summary>
    /// <param name="name">The identifier.</param>
    /// <returns>Whether it is a regular identifier.</returns>
    public static bool IsRegular(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Quotes an identifier: PostgreSQL, SQLite and Oracle <c>"x"</c>, SQL Server <c>[x]</c>, MySQL <c>`x`</c>, with the closing
    /// character doubled inside. <paramref name="quoting"/> <c>always</c> quotes, <c>never</c> leaves the name as is, and
    /// <c>reserved</c> quotes reserved words of the dialect, names that are not regular identifiers, and names that start with an
    /// underscore (Oracle's unquoted identifiers must start with a letter).
    /// </summary>
    /// <param name="name">The identifier.</param>
    /// <param name="dialect">The dialect.</param>
    /// <param name="quoting">always, reserved or never.</param>
    /// <returns>The identifier as SQL.</returns>
    public static string Quote(string name, Dialect dialect, Quoting quoting)
    {
        var quote = quoting switch
        {
            Quoting.Always => true,
            Quoting.Never => false,
            _ => !IsRegular(name) || name[0] == '_' || IsReserved(dialect, name),
        };
        if (!quote)
            return name;
        return dialect switch
        {
            Dialect.SqlServer => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]",
            Dialect.MySql => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`",
            _ => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        };
    }

    /// <summary>Parses a quoting mode (<c>always</c>, <c>reserved</c>, <c>never</c>).</summary>
    /// <param name="value">The text.</param>
    /// <param name="quoting">The mode.</param>
    /// <returns>Whether the text is a mode.</returns>
    public static bool TryParseQuoting(string? value, out Quoting quoting)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "always":
                quoting = Quoting.Always;
                return true;
            case "reserved":
                quoting = Quoting.Reserved;
                return true;
            case "never":
                quoting = Quoting.Never;
                return true;
            default:
                quoting = Quoting.Reserved;
                return false;
        }
    }

    /// <summary>
    /// Writes a value as a SQL literal: strings single-quoted with <c>''</c> (SQL Server <c>N'…'</c>; MySQL also doubles
    /// backslashes), booleans <c>true</c>/<c>false</c> on PostgreSQL and <c>1</c>/<c>0</c> elsewhere, numbers in invariant form,
    /// dates and times as quoted ISO 8601, <see langword="null"/> as <c>NULL</c>.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="dialect">The dialect.</param>
    /// <returns>The literal.</returns>
    /// <exception cref="ArgumentException">The value has no SQL literal form (a list, a map, a model object, NaN).</exception>
    public static string Literal(object? value, Dialect dialect)
    {
        switch (value)
        {
            case null:
                return "NULL";
            case string s:
                return QuoteString(s, dialect, unicode: true);
            case char c:
                return QuoteString(c.ToString(), dialect, unicode: true);
            case bool b:
                return dialect == Dialect.PostgreSql ? (b ? "true" : "false") : (b ? "1" : "0");
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case double d when double.IsFinite(d):
                return d.ToString("R", CultureInfo.InvariantCulture);
            case float f when float.IsFinite(f):
                return f.ToString("R", CultureInfo.InvariantCulture);
            case DateTime dt:
                return QuoteString(dt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'), dialect, unicode: false);
            case DateTimeOffset dto:
                return QuoteString(dto.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture), dialect, unicode: false);
            case DateOnly date:
                return QuoteString(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dialect, unicode: false);
            case TimeOnly time:
                return QuoteString(time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'), dialect, unicode: false);
            default:
                throw new ArgumentException($"A value of type '{TemplateValues.TypeName(value)}' has no SQL literal form.");
        }
    }

    private static string QuoteString(string text, Dialect dialect, bool unicode)
    {
        var escaped = text.Replace("'", "''", StringComparison.Ordinal);
        if (dialect == Dialect.MySql)
            escaped = escaped.Replace("\\", "\\\\", StringComparison.Ordinal);
        return (unicode && dialect == Dialect.SqlServer ? "N'" : "'") + escaped + "'";
    }

    private static FrozenDictionary<Dialect, FrozenSet<string>> LoadReservedWords()
    {
        var assembly = typeof(SqlDialects).Assembly;
        var result = new Dictionary<Dialect, FrozenSet<string>>();
        foreach (var dialect in Enum.GetValues<Dialect>())
        {
            var name = "Maquettiste.Engine.Rendering.Resources.reserved-" + Resolution.DialectTypeMaps.Name(dialect) + ".txt";
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Missing embedded reserved-word list " + name);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var words = new HashSet<string>(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                var word = line.Trim();
                if (word.Length > 0)
                    words.Add(word.ToLowerInvariant());
            }

            result[dialect] = words.ToFrozenSet(StringComparer.Ordinal);
        }

        return result.ToFrozenDictionary();
    }
}
