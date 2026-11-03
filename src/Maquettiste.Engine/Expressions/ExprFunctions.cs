using System.Collections.Frozen;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// One operator of the core (next-phase-3-design.md section 3.1, the operator table): its stored name, its text, how it binds, how
/// many operands it takes and its two lowerings. A PostgreSQL template writes <c>{0}</c>, <c>{1}</c>… for the lowered operands and
/// <c>{*}</c> for all of them joined with <c>, </c>, <c>{rest}</c> for those after the first; strings add <c>COLLATE "C"</c> where <see cref="Collates"/> says so. The
/// JavaScript lowering is the runtime function that implements SQL's semantics.
/// </summary>
/// <param name="Op">The stored <c>op</c>.</param>
/// <param name="Text">The text form's keyword or symbol.</param>
/// <param name="Precedence">How tightly it binds, 1 (<c>implies</c>) to 10 (unary <c>-</c>); 11 is a primary.</param>
/// <param name="MinArgs">The fewest operands.</param>
/// <param name="MaxArgs">The most operands; <see cref="int.MaxValue"/> for no limit.</param>
/// <param name="PostgreSql">The PostgreSQL template.</param>
/// <param name="JavaScript">The runtime function, <c>mq.&lt;name&gt;</c>.</param>
/// <param name="Collates">Whether string operands compare with <c>COLLATE "C"</c>.</param>
public sealed record ExprOperator(string Op, string Text, int Precedence, int MinArgs, int MaxArgs, string PostgreSql, string JavaScript,
    bool Collates = false);

/// <summary>
/// One function of the closed table (next-phase-3-design.md section 3.1, the function table): its signature, its null rule and its
/// two lowerings. Adding one needs its signature, its null rule, both lowerings and corpus rows in one change. A function whose
/// PostgreSQL form depends on its first argument's type lists one template per type kind in <see cref="PostgreSqlByKind"/>.
/// </summary>
/// <param name="Name">The name the text and the tree write.</param>
/// <param name="Signature">The signature, for messages and documentation.</param>
/// <param name="MinArgs">The fewest arguments.</param>
/// <param name="MaxArgs">The most arguments.</param>
/// <param name="NullRule">What a null argument gives.</param>
/// <param name="PostgreSql">The PostgreSQL template.</param>
/// <param name="JavaScript">The runtime function, <c>mq.&lt;name&gt;</c>.</param>
public sealed record ExprFunction(string Name, string Signature, int MinArgs, int MaxArgs, string NullRule, string PostgreSql, string JavaScript)
{
    /// <summary>PostgreSQL templates by the first argument's type kind, where they differ from <see cref="PostgreSql"/>.</summary>
    public IReadOnlyDictionary<string, string> PostgreSqlByKind { get; init; } = FrozenDictionary<string, string>.Empty;
}

/// <summary>The operator and function tables of the core, as data both lowerings and the typer read.</summary>
public static class ExprFunctions
{
    /// <summary>The highest precedence, a primary: a literal, a path, a call, a case, a parenthesised expression.</summary>
    public const int Primary = 11;

    /// <summary>Every operator, by its stored name.</summary>
    public static FrozenDictionary<string, ExprOperator> Operators { get; } = new ExprOperator[]
    {
        new("implies", "implies", 1, 2, 2, "(NOT {0} OR {1})", "mq.implies"),
        new("or", "or", 2, 2, int.MaxValue, "({0} OR {1})", "mq.or"),
        new("and", "and", 3, 2, int.MaxValue, "({0} AND {1})", "mq.and"),
        new("not", "not", 4, 1, 1, "(NOT {0})", "mq.not"),
        new("eq", "=", 5, 2, 2, "({0} = {1})", "mq.eq", true),
        new("ne", "<>", 5, 2, 2, "({0} <> {1})", "mq.ne", true),
        new("lt", "<", 5, 2, 2, "({0} < {1})", "mq.lt", true),
        new("le", "<=", 5, 2, 2, "({0} <= {1})", "mq.le", true),
        new("gt", ">", 5, 2, 2, "({0} > {1})", "mq.gt", true),
        new("ge", ">=", 5, 2, 2, "({0} >= {1})", "mq.ge", true),
        new("isNull", "is null", 5, 1, 1, "({0} IS NULL)", "mq.isNull"),
        new("isNotNull", "is not null", 5, 1, 1, "({0} IS NOT NULL)", "mq.isNotNull"),
        new("distinct", "is distinct from", 5, 2, 2, "({0} IS DISTINCT FROM {1})", "mq.distinct", true),
        new("notDistinct", "is not distinct from", 5, 2, 2, "({0} IS NOT DISTINCT FROM {1})", "mq.notDistinct", true),
        new("between", "between", 6, 3, 3, "({0} BETWEEN {1} AND {2})", "mq.between", true),
        new("in", "in", 6, 2, int.MaxValue, "({0} IN ({rest}))", "mq.in", true),
        new("notIn", "not in", 6, 2, int.MaxValue, "({0} NOT IN ({rest}))", "mq.notIn", true),
        new("concat", "||", 7, 2, int.MaxValue, "({0} || {1})", "mq.concat"),
        new("add", "+", 8, 2, 2, "({0} + {1})", "mq.add"),
        new("sub", "-", 8, 2, 2, "({0} - {1})", "mq.sub"),
        new("mul", "*", 9, 2, 2, "({0} * {1})", "mq.mul"),
        new("div", "/", 9, 2, 2, "round(({0} + 0.000000000000000000) / NULLIF({1}, 0), 16)", "mq.div"),
        new("neg", "-", 10, 1, 1, "(- {0})", "mq.neg"),
        new("coalesce", "coalesce", Primary, 1, int.MaxValue, "COALESCE({*})", "mq.coalesce"),
    }.ToFrozenDictionary(o => o.Op, StringComparer.Ordinal);

    /// <summary>Every function, by name.</summary>
    public static FrozenDictionary<string, ExprFunction> Functions { get; } = new ExprFunction[]
    {
        new("trim", "string → string", 1, 1, "null in, null out", "btrim({0}, ' ')", "mq.trim"),
        new("length", "string → int32", 1, 1, "null in, null out", "char_length({0})", "mq.length"),
        new("matches", "string, pattern literal → bool", 2, 2, "null when the string is", "({0} ~ {1})", "mq.matches"),
        new("nonnull_count", "1 to 16 values → int32", 1, 16, "never null", "num_nonnulls({*})", "mq.nonnullCount"),
        new("add_days", "date, datetime or datetimeoffset, int → the same", 2, 2, "null in, null out", "({0} + CAST({1} AS integer))", "mq.addDays")
        {
            PostgreSqlByKind = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["date"] = "({0} + CAST({1} AS integer))",
                ["datetime"] = "({0} + make_interval(days => CAST({1} AS integer)))",
                // n × 24 hours: an interval of hours never meets daylight saving time, and an integer never rounds.
                ["datetimeoffset"] = "({0} + make_interval(hours => CAST({1} AS integer) * 24))",
            }.ToFrozenDictionary(StringComparer.Ordinal),
        },
        new("add_months", "date, datetime or datetimeoffset, int → the same", 2, 2, "null in, null out",
            "CAST({0} + make_interval(months => CAST({1} AS integer)) AS date)", "mq.addMonths")
        {
            PostgreSqlByKind = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["date"] = "CAST({0} + make_interval(months => CAST({1} AS integer)) AS date)",
                ["datetime"] = "({0} + make_interval(months => CAST({1} AS integer)))",
                ["datetimeoffset"] = "((({0} AT TIME ZONE 'UTC') + make_interval(months => CAST({1} AS integer))) AT TIME ZONE 'UTC')",
            }.ToFrozenDictionary(StringComparer.Ordinal),
        },
        new("date_part", "'year', 'month', 'day' or 'isodow' and a date or timestamp, or 'hour' or 'minute' and a timestamp → int32", 2, 2,
            "null in, null out", "CAST(extract({0} from {1}) AS integer)", "mq.datePart")
        {
            PostgreSqlByKind = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["datetimeoffset"] = "CAST(extract({0} from ({1} AT TIME ZONE 'UTC')) AS integer)",
            }.ToFrozenDictionary(StringComparer.Ordinal),
        },
        new("date_of", "datetime or datetimeoffset → date", 1, 1, "null in, null out", "CAST({0} AS date)", "mq.dateOf")
        {
            PostgreSqlByKind = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["datetimeoffset"] = "CAST(({0} AT TIME ZONE 'UTC') AS date)",
            }.ToFrozenDictionary(StringComparer.Ordinal),
        },
        new("round", "decimal, int literal 0 to 18 → decimal", 2, 2, "null in, null out", "round({0}, {1})", "mq.round"),
        new("json_type", "json → string", 1, 1, "null in, null out; JSON null is 'null'", "jsonb_typeof(CAST({0} AS jsonb))", "mq.jsonType"),
        new("json_length", "json → int32", 1, 1, "null unless an array",
            "CASE WHEN jsonb_typeof(CAST({0} AS jsonb)) = 'array' THEN jsonb_array_length(CAST({0} AS jsonb)) END", "mq.jsonLength"),
    }.ToFrozenDictionary(f => f.Name, StringComparer.Ordinal);

    /// <summary>The parts <c>date_part</c> reads from a date as well as a timestamp.</summary>
    public static FrozenSet<string> DateParts { get; } = new[] { "year", "month", "day", "isodow" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The parts <c>date_part</c> reads from a timestamp only.</summary>
    public static FrozenSet<string> TimeParts { get; } = new[] { "hour", "minute" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The most nodes one expression holds (MQ3113).</summary>
    public const int MaxNodes = 512;

    /// <summary>The deepest one expression nests (MQ3113).</summary>
    public const int MaxDepth = 64;
}
