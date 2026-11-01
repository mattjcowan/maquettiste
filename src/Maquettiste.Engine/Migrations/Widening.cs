using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Migrations;

/// <summary>How a column's type changed (next-phase-1-design.md section 3.2, the widening table).</summary>
public enum TypeChange
{
    /// <summary>The same type, once aliases, case and spaces are folded.</summary>
    Same,

    /// <summary>Every stored value converts exactly and reads back as an equal value of the same kind; no fact is needed.</summary>
    Widening,

    /// <summary>The reverse change is a widening: some stored values may not fit; a conversion and a data check are needed.</summary>
    Narrowing,

    /// <summary>Any other change: a conversion is needed.</summary>
    Conversion,
}

/// <summary>When a <see cref="WideningRule"/> holds.</summary>
public enum WideningCondition
{
    /// <summary>Whatever the arguments.</summary>
    Always,

    /// <summary>The new length is greater (<c>m &gt; n</c>).</summary>
    LongerLength,

    /// <summary>
    /// The scale does not shrink and neither do the integer digits (<c>s' &gt;= s</c> and <c>p' - s' &gt;= p - s</c>).
    /// </summary>
    DigitsKept,

    /// <summary>The new fractional-second precision is greater (<c>p' &gt; p</c>).</summary>
    MorePrecision,
}

/// <summary>One row of a dialect's widening table: a native type pattern, the pattern it widens to, and when.</summary>
/// <param name="From">The type before, such as <c>varchar(n)</c>.</param>
/// <param name="To">The type after, such as <c>varchar(m)</c>.</param>
/// <param name="When">When the change is a widening.</param>
public sealed record WideningRule(string From, string To, WideningCondition When);

/// <summary>
/// The widening table (next-phase-1-design.md section 3.2; scope 6.2): code data read by the migration fact classifier, validation,
/// fold and later the live reader. A widening is a change after which every stored value converts exactly and reads back as an equal
/// value of the same kind. Only PostgreSQL has a table, so elsewhere every type change is a conversion. Native types are parsed first:
/// case and spaces folded, PostgreSQL aliases (<c>int4</c>, <c>character varying</c>, <c>timestamp with time zone</c>...) mapped,
/// <c>numeric(p)</c> read as <c>numeric(p, 0)</c>, a missing time precision read as 6 and array suffixes counted; an array widens
/// as its element type, with the same dimensions. Making a column nullable is a widening too, but nullability is not a type: the
/// classifier treats it apart. The table is also in <c>docs/guides/schema-changes.md</c>, and a test keeps the two in step.
/// </summary>
public static class Widening
{
    private const int DefaultTimePrecision = 6;

    private static readonly ImmutableArray<Row> PostgreSqlRows =
    [
        new(new("varchar(n)", "varchar(m)", WideningCondition.LongerLength), "varchar", true, "varchar", true),
        new(new("varchar(n)", "varchar", WideningCondition.Always), "varchar", true, "varchar", false),
        new(new("varchar(n)", "text", WideningCondition.Always), "varchar", true, "text", false),
        new(new("char(n)", "char(m)", WideningCondition.LongerLength), "char", true, "char", true),
        new(new("smallint", "integer", WideningCondition.Always), "smallint", false, "integer", false),
        new(new("smallint", "bigint", WideningCondition.Always), "smallint", false, "bigint", false),
        new(new("integer", "bigint", WideningCondition.Always), "integer", false, "bigint", false),
        new(new("numeric(p, s)", "numeric(p', s')", WideningCondition.DigitsKept), "numeric", true, "numeric", true),
        new(new("numeric(p, s)", "numeric", WideningCondition.Always), "numeric", true, "numeric", false),
        new(new("timestamp(p)", "timestamp(p')", WideningCondition.MorePrecision), "timestamp", true, "timestamp", true),
        new(new("timestamptz(p)", "timestamptz(p')", WideningCondition.MorePrecision), "timestamptz", true, "timestamptz", true),
        new(new("time(p)", "time(p')", WideningCondition.MorePrecision), "time", true, "time", true),
        new(new("timetz(p)", "timetz(p')", WideningCondition.MorePrecision), "timetz", true, "timetz", true),
        new(new("interval(p)", "interval(p')", WideningCondition.MorePrecision), "interval", true, "interval", true),
    ];

    private static readonly ImmutableArray<WideningRule> PostgreSqlRules = [.. PostgreSqlRows.Select(r => r.Rule)];

    /// <summary>The widening table of a dialect, in the order the guide lists it.</summary>
    /// <param name="dialect">The dialect.</param>
    /// <returns>The rules; empty outside PostgreSQL.</returns>
    public static IReadOnlyList<WideningRule> Rules(Dialect dialect) => dialect == Dialect.PostgreSql ? PostgreSqlRules : [];

    /// <summary>Classifies a change of native type.</summary>
    /// <param name="dialect">The database's dialect.</param>
    /// <param name="fromNativeType">The native type before, as written.</param>
    /// <param name="toNativeType">The native type after, as written.</param>
    /// <returns>
    /// <see cref="TypeChange.Same"/> when the parsed types are equal, <see cref="TypeChange.Widening"/> when a rule takes the
    /// change, <see cref="TypeChange.Narrowing"/> when one takes the reverse change, else <see cref="TypeChange.Conversion"/>.
    /// Outside PostgreSQL every change of the folded text is a conversion.
    /// </returns>
    public static TypeChange Classify(Dialect dialect, string fromNativeType, string toNativeType)
    {
        ArgumentNullException.ThrowIfNull(fromNativeType);
        ArgumentNullException.ThrowIfNull(toNativeType);
        if (dialect != Dialect.PostgreSql)
            return string.Equals(Fold(fromNativeType), Fold(toNativeType), StringComparison.Ordinal) ? TypeChange.Same : TypeChange.Conversion;
        var from = Parse(fromNativeType);
        var to = Parse(toNativeType);
        if (from == to)
            return TypeChange.Same;
        if (Widens(from, to))
            return TypeChange.Widening;
        return Widens(to, from) ? TypeChange.Narrowing : TypeChange.Conversion;
    }

    /// <summary>
    /// Classifies a change between two sides of a <c>change-type</c> fact: by their native types when both carry one; when only one
    /// does, on PostgreSQL, against the other side's keyword and facets rendered through the dialect's embedded type map (a conversion
    /// elsewhere, or when that side lacks a facet its pattern needs); else by the built-in type and its facets (a different keyword is
    /// a conversion; on PostgreSQL the facets are rendered through the dialect's embedded type map, a facet missing on one side taking
    /// the other side's value, and the rendered types classified). A reference
    /// column whose strategy or bound table changed is never the same nor widened: such a change is a conversion, or a narrowing when
    /// its type narrows.
    /// </summary>
    /// <param name="dialect">The database's dialect.</param>
    /// <param name="from">The state before.</param>
    /// <param name="to">The state after.</param>
    /// <returns>The change.</returns>
    internal static TypeChange Classify(Dialect dialect, MigrationState from, MigrationState to)
    {
        var change = ClassifyType(dialect, from, to);
        return change is TypeChange.Same or TypeChange.Widening && !SameReference(from, to) ? TypeChange.Conversion : change;
    }

    /// <summary>Whether two sides of a <c>change-type</c> fact store a reference column's codes alike: the same strategy and bound table.</summary>
    /// <param name="a">One side.</param>
    /// <param name="b">The other side.</param>
    /// <returns><see langword="true"/> when both match (two sides of a column that is not a reference column always do).</returns>
    internal static bool SameReference(MigrationState a, MigrationState b) =>
        string.Equals(a.Strategy, b.Strategy, StringComparison.Ordinal) && string.Equals(a.ReferenceTable, b.ReferenceTable, StringComparison.Ordinal);

    private static TypeChange ClassifyType(Dialect dialect, MigrationState from, MigrationState to)
    {
        if (from.NativeType is { } fromNative && to.NativeType is { } toNative)
            return Classify(dialect, fromNative, toNative);
        if (from.NativeType is not null || to.NativeType is not null)
        {
            // One side pins a native type: on PostgreSQL the other side's keyword and facets are rendered first, so text to a
            // string of length 20 reads as text to varchar(20), a narrowing. A side that does not render is a conversion.
            return dialect == Dialect.PostgreSql && (from.NativeType ?? RenderAlone(dialect, from)) is { } fromText
                && (to.NativeType ?? RenderAlone(dialect, to)) is { } toText
                ? Classify(dialect, fromText, toText)
                : TypeChange.Conversion;
        }

        if (from.Type is null || to.Type is null)
            return SameFacets(from, to) ? TypeChange.Same : TypeChange.Conversion;
        if (!string.Equals(from.Type, to.Type, StringComparison.Ordinal))
            return TypeChange.Conversion;
        if (SameFacets(from, to))
            return TypeChange.Same;
        if (dialect != Dialect.PostgreSql || !DialectTypeMaps.Default(dialect).TryGetValue(from.Type, out var pattern))
            return TypeChange.Conversion;
        return Classify(dialect, Render(pattern, from, to), Render(pattern, to, from));
    }

    private static bool SameFacets(MigrationState a, MigrationState b) =>
        string.Equals(a.Type, b.Type, StringComparison.Ordinal) && a.Length == b.Length && a.Precision == b.Precision && a.Scale == b.Scale;

    /// <summary>
    /// A side's keyword and facets rendered through the dialect's embedded type map, or <see langword="null"/> when the map has no
    /// entry or the side lacks a length, a scale or a decimal's precision its pattern needs (a missing time precision reads as 6).
    /// </summary>
    private static string? RenderAlone(Dialect dialect, MigrationState side)
    {
        if (side.Type is not { } type || !DialectTypeMaps.Default(dialect).TryGetValue(type, out var pattern))
            return null;
        if ((side.Length is null && pattern.Contains("{length}", StringComparison.Ordinal))
            || (side.Scale is null && pattern.Contains("{scale}", StringComparison.Ordinal))
            || (side.Precision is null && pattern.Contains("{precision}", StringComparison.Ordinal) && string.Equals(type, "decimal", StringComparison.Ordinal)))
            return null;
        return Render(pattern, side, side);
    }

    private static string Render(string pattern, MigrationState side, MigrationState other)
    {
        static string Text(int? value) => (value ?? 0).ToString(CultureInfo.InvariantCulture);
        return pattern
            .Replace("{length}", Text(side.Length ?? other.Length), StringComparison.Ordinal)
            .Replace("{precision}", Text(side.Precision ?? other.Precision ?? DefaultTimePrecision), StringComparison.Ordinal)
            .Replace("{scale}", Text(side.Scale ?? other.Scale), StringComparison.Ordinal);
    }

    private static bool Widens(PgType from, PgType to)
    {
        if (from.Dimensions != to.Dimensions || from.Raw is not null || to.Raw is not null)
            return false;
        foreach (var row in PostgreSqlRows)
        {
            if (!string.Equals(from.Name, row.FromName, StringComparison.Ordinal) || from.First.HasValue != row.FromArguments
                || !string.Equals(to.Name, row.ToName, StringComparison.Ordinal) || to.First.HasValue != row.ToArguments)
                continue;
            var holds = row.Rule.When switch
            {
                WideningCondition.Always => true,
                WideningCondition.LongerLength or WideningCondition.MorePrecision => to.First > from.First,
                _ => to.Second >= from.Second && to.First - to.Second >= from.First - from.Second,
            };
            if (holds)
                return true;
        }

        return false;
    }

    /// <summary>Lower case, spaces collapsed, no space next to a parenthesis, comma or bracket.</summary>
    private static string Fold(string nativeType)
    {
        var text = nativeType.Trim().ToLowerInvariant();
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0 && c is not ('(' or ')' or ',' or '[' or ']') && sb[^1] is not ('(' or ',' or '['))
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Parses a PostgreSQL native type into its canonical name, its integer arguments and its array dimensions.</summary>
    private static PgType Parse(string nativeType)
    {
        var text = Fold(nativeType);
        var dimensions = 0;
        while (true)
        {
            if (text.EndsWith(']') && text.LastIndexOf('[') is var open and > 0
                && text.AsSpan(open + 1, text.Length - open - 2).IndexOfAnyExceptInRange('0', '9') < 0)
            {
                dimensions++;
                text = text[..open].TrimEnd();
                if (text.EndsWith(" array", StringComparison.Ordinal))
                    text = text[..^" array".Length]; // ARRAY[n] is one dimension
                continue;
            }

            if (text.EndsWith(" array", StringComparison.Ordinal))
            {
                dimensions++;
                text = text[..^" array".Length];
                continue;
            }

            break;
        }

        string name;
        string? arguments = null;
        var parenthesis = text.IndexOf('(', StringComparison.Ordinal);
        var close = parenthesis < 0 ? -1 : text.IndexOf(')', parenthesis);
        if (parenthesis > 0 && close > parenthesis)
        {
            arguments = text[(parenthesis + 1)..close];
            name = (text[..parenthesis] + " " + text[(close + 1)..]).Trim();
            while (name.Contains("  ", StringComparison.Ordinal))
                name = name.Replace("  ", " ", StringComparison.Ordinal);
        }
        else
        {
            name = text;
        }

        int? first = null;
        int? second = null;
        if (arguments is not null)
        {
            var parts = arguments.Split(',');
            if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a))
                return new PgType(name, null, null, dimensions, arguments);
            first = a;
            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var b))
                    return new PgType(name, null, null, dimensions, arguments);
                second = b;
            }
        }

        switch (name)
        {
            case "int2" or "smallint" or "smallserial" or "serial2":
                return new PgType("smallint", null, null, dimensions, arguments);
            case "int" or "int4" or "integer" or "serial" or "serial4":
                return new PgType("integer", null, null, dimensions, arguments);
            case "int8" or "bigint" or "bigserial" or "serial8":
                return new PgType("bigint", null, null, dimensions, arguments);
            case "bool" or "boolean":
                return new PgType("boolean", null, null, dimensions, arguments);
            case "float4" or "real":
                return new PgType("real", null, null, dimensions, arguments);
            case "float8" or "double precision":
                return new PgType("double precision", null, null, dimensions, arguments);
            case "float":
                return new PgType(first is <= 24 ? "real" : "double precision", null, null, dimensions, null);
            case "varchar" or "character varying":
                return new PgType("varchar", first, null, dimensions, second is null ? null : arguments);
            case "char" or "character":
                return new PgType("char", first ?? 1, null, dimensions, second is null ? null : arguments);
            case "bpchar":
                return new PgType("char", first, null, dimensions, second is null ? null : arguments);
            case "numeric" or "decimal":
                return new PgType("numeric", first, first is null ? null : second ?? 0, dimensions, null);
            case "timestamp" or "timestamp without time zone":
                return Time("timestamp");
            case "timestamptz" or "timestamp with time zone":
                return Time("timestamptz");
            case "time" or "time without time zone":
                return Time("time");
            case "timetz" or "time with time zone":
                return Time("timetz");
            case "interval":
                return Time("interval");
            default:
                return new PgType(name, first, second, dimensions, null);
        }

        PgType Time(string canonical) => second is null
            ? new PgType(canonical, first ?? DefaultTimePrecision, null, dimensions, null)
            : new PgType(canonical, null, null, dimensions, arguments);
    }

    /// <summary>
    /// A parsed PostgreSQL type. <see cref="Raw"/> keeps arguments that do not fit the type (text, or too many), so such a type is
    /// equal only to the same text and widens nothing.
    /// </summary>
    private readonly record struct PgType(string Name, int? First, int? Second, int Dimensions, string? Raw);

    /// <summary>A rule with the canonical names and argument presence it matches.</summary>
    private sealed record Row(WideningRule Rule, string FromName, bool FromArguments, string ToName, bool ToArguments);
}
