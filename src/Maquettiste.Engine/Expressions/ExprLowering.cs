using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Expressions;

/// <summary>How an enum or reference-type column stores its values, which decides the literal an enum member or code lowers to.</summary>
public enum ExprStoredForm
{
    /// <summary>Text: an enum member's code, or its name when it has none; a text code.</summary>
    Text,

    /// <summary>An integer: an enum member's value, or its position from 0 when it has none; an integer code.</summary>
    Integer,

    /// <summary>A uuid code.</summary>
    Uuid,
}

/// <summary>
/// What a path reads in a target (next-phase-3-design.md section 3.1, lowering): a column of the site's table, the column a binding
/// maps an attribute to, or a scalar subquery through a relation.
/// </summary>
/// <param name="Name">The physical column name (unquoted), or the name a JavaScript row holds the value under.</param>
public sealed record ExprColumn(string Name)
{
    /// <summary>SQL that reads the value in place of the column name (a scalar subquery through a navigation), or <see langword="null"/>.</summary>
    public string? Sql { get; init; }

    /// <summary>Whether the column holds a fixed-length string, read without its trailing spaces (<c>rtrim</c>).</summary>
    public bool FixedLength { get; init; }

    /// <summary>The declared scale of a decimal column, or <see langword="null"/>.</summary>
    public int? Scale { get; init; }

    /// <summary>How an enum or reference-type column stores its values.</summary>
    public ExprStoredForm Form { get; init; } = ExprStoredForm.Text;
}

/// <summary>
/// The site's mapping onto a target, which both lowerings read: the column each path reads, the literal each enum member or code
/// lowers to, and the database's dialect and quoting. Built by the engine per site; at a table, <see cref="Physical"/> makes the
/// JavaScript lowering read the row by column names and write enum members and codes in their stored form.
/// </summary>
public sealed class ExprColumns
{
    /// <summary>The column a typed path reads, or <see langword="null"/> for the member's own name and properties.</summary>
    public Func<TypedExpr, ExprColumn?>? Column { get; init; }

    /// <summary>
    /// The literal of an enum member or reference-type code (by id) in a stored form: a <see cref="long"/> for
    /// <see cref="ExprStoredForm.Integer"/>, a <see cref="string"/> otherwise. A code that is not an integer stays its text in the
    /// integer form, which the SQL lowering reports (MQ3103) instead of converting.
    /// </summary>
    public Func<string, ExprStoredForm, object?>? Ref { get; init; }

    /// <summary>Whether the paths are a table's columns, so JavaScript reads the row by column names and writes stored forms.</summary>
    public bool Physical { get; init; }

    /// <summary>The database's dialect name; only <c>postgresql</c> lowers to SQL (MQ4056 otherwise).</summary>
    public string Dialect { get; init; } = "postgresql";

    /// <summary>How the database quotes identifiers.</summary>
    public Quoting Quoting { get; init; } = Quoting.Reserved;

    /// <summary>The id of the element that holds the expression, for diagnostics.</summary>
    public string? ElementId { get; init; }

    /// <summary>The path of the file that holds the expression, for diagnostics.</summary>
    public string? FilePath { get; init; }

    /// <summary>A site with no mapping: paths read their members by name, enum members and codes need <see cref="Ref"/>.</summary>
    public static ExprColumns None { get; } = new();

    /// <summary>The column a path reads: <see cref="Column"/>'s answer, else one named after the member with its own properties.</summary>
    internal ExprColumn ColumnOf(TypedExpr path)
    {
        if (Column?.Invoke(path) is { } column)
            return column;
        var member = path.Member!;
        return new ExprColumn(member.Name) { FixedLength = member.FixedLength, Scale = member.Scale };
    }

    /// <summary>The literal of an enum member or code in a form.</summary>
    internal object RefLiteral(string id, ExprStoredForm form) =>
        Ref?.Invoke(id, form) ?? throw new InvalidOperationException("The site gives no literal for the enum member or code '" + id + "'.");
}

/// <summary>Lowers a typed expression to its two targets (next-phase-3-design.md section 3.1, lowering).</summary>
public static class ExprLowering
{
    /// <summary>
    /// Lowers a typed expression to PostgreSQL text through the query tree: a check as a condition, a guard or rule condition as
    /// <c>COALESCE((…), false)</c>, a value as a value. Another dialect returns <see langword="null"/> and MQ4056.
    /// </summary>
    /// <param name="e">The typed expression.</param>
    /// <param name="columns">The site's mapping onto its table.</param>
    /// <param name="site">The site's kind.</param>
    /// <param name="problems">Receives the diagnostics.</param>
    /// <returns>The SQL text, or <see langword="null"/>.</returns>
    public static string? ToPostgreSql(TypedExpr e, ExprColumns columns, ExprSiteKind site, ICollection<Diagnostic> problems)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(problems);
        if (!string.Equals(columns.Dialect, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(RuleCatalog.Create("MQ4056", "An expression lowers to SQL for PostgreSQL only; on " + columns.Dialect
                + " the DDL leaves it out. Keep a SQL text for that dialect instead.", columns.ElementId, columns.FilePath, e.Pointer));
            return null;
        }

        return ExprSql.Lower(e, columns, site, problems);
    }

    /// <summary>
    /// Lowers a typed expression to one JavaScript arrow function over a row, which calls the runtime (<see cref="ExprRuntime"/>) as
    /// <c>mq</c>: a check wraps its result in <c>mq.passes</c>, a guard or rule condition in <c>mq.holds</c>, a value in <c>mq.out</c>.
    /// </summary>
    /// <param name="e">The typed expression.</param>
    /// <param name="site">The site's kind.</param>
    /// <param name="columns">The site's mapping; needed for enum members and codes, and at a table.</param>
    /// <returns>The function's text.</returns>
    public static string ToJavaScript(TypedExpr e, ExprSiteKind site, ExprColumns? columns = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        return ExprJs.Lower(e, site, columns ?? ExprColumns.None);
    }

    /// <summary>
    /// Whether a typed expression reads <c>current_actor()</c> or <c>in_state</c> (object-effects-design.md section 7): its JavaScript then calls
    /// <c>mq.currentActor</c> and <c>mq.inState</c>, which only the engine's <c>mqAt(now, actor)</c> (<see cref="ExprRuntime.Clock"/>) has, so
    /// it runs only with that runtime and no pack sees it.
    /// </summary>
    /// <param name="e">The typed expression.</param>
    /// <returns>Whether it reads either.</returns>
    public static bool IsEngineOnly(TypedExpr e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Node.CurrentActor is not null || e.Node.InState is not null || (e.Body is { } body && IsEngineOnly(body)) || e.Args.Any(IsEngineOnly);
    }
}

/// <summary>The JavaScript runtime lowered expressions call (<c>Expressions/expr-runtime.js</c>); packs that write JavaScript copy it.</summary>
public static class ExprRuntime
{
    private static readonly Lazy<string> Text = new(() =>
    {
        using var stream = typeof(ExprRuntime).Assembly.GetManifestResourceStream("Maquettiste.Engine.Expressions.expr-runtime.js")
            ?? throw new InvalidOperationException("The expression runtime is not embedded.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    });

    /// <summary>The runtime's source: it declares <c>const mq</c>.</summary>
    public static string Source => Text.Value;

    /// <summary>The path <see cref="Prepared"/> loads under, which a limit or an error inside the runtime names.</summary>
    internal const string PreparedPath = "expressions:runtime.js";

    /// <summary>
    /// What <see cref="Prepared"/> holds after <see cref="Source"/>, engine-only, so no pack's copy of the runtime changes:
    /// <c>mqAt(now, actor)</c>, the runtime whose <c>now()</c> is the instant given, which a process guard that reads <c>now</c> receives
    /// with the simulation's clock, so replays stay deterministic; whose <c>current_actor()</c> is the identity given, as text, or null
    /// (object-effects-design.md decision 5), which the lowering reads through the type <c>current_actor()</c> met; and whose
    /// <c>inState(object, lifecycle, states)</c> is true when one of the states is among the ids the object holds for the lifecycle under
    /// <c>$states</c> (a member no attribute can be named), false when none is, and null when the object or that list is missing
    /// (decisions 7 and 10).
    /// </summary>
    internal const string Clock = "\nconst mqInState = (o, lifecycle, states) => {\n"
        + "  const all = o !== null && typeof o === \"object\" ? o[\"$states\"] : undefined;\n"
        + "  const active = all !== null && typeof all === \"object\" ? all[lifecycle] : undefined;\n"
        + "  return Array.isArray(active) ? active.some((s) => states.includes(s)) : null;\n"
        + "};\n"
        + "const mqAt = (now, actor) => Object.freeze({ ...mq, now: () => mq.tsz(now), currentActor: () => (actor === undefined ? null : actor), inState: mqInState });\n";

    private static readonly Lazy<PreparedScript> Shared = new(() =>
        PreparedScript.PrepareAll([new ScriptSource(PreparedPath, Source + Clock, "")], ExprEvaluator.Limits)[0]);

    /// <summary>
    /// The runtime and <see cref="Clock"/>, prepared once for the whole process: every sandbox that runs lowered expressions (an
    /// <see cref="ExprEvaluator"/>'s, a process session's) loads it as its first script, and its own script, which registers the
    /// functions, reads <c>mq</c> and <c>mqAt</c> from the global scope the scripts of one engine share. A prepared copy holds the
    /// runtime's syntax tree, about 450 KiB, so pools share this one rather than each preparing its own. It is prepared under
    /// <see cref="ExprEvaluator.Limits"/>, the limits every pool that loads it runs under, so its regular expressions match within the
    /// same bound (250 ms; a pool with a lower bound lowers it, <see cref="ScriptSandbox"/>).
    /// </summary>
    internal static PreparedScript Prepared => Shared.Value;
}
