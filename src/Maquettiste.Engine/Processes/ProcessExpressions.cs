using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acornima.Ast;
using Jint;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using JsEngine = Jint.Engine;

namespace Maquettiste.Engine.Processes;

/// <summary>An expression that does not parse (MQ9501).</summary>
/// <param name="Id">The guard or action id.</param>
/// <param name="Pointer">The JSON pointer of the expression in the process file.</param>
/// <param name="Name">The guard or action name.</param>
/// <param name="Kind">"guard" or "action".</param>
/// <param name="Message">The parser's message.</param>
public sealed record ExpressionProblem(string Id, string Pointer, string Name, string Kind, string Message);

/// <summary>
/// A process's guard and action expressions compiled once (phase-3-design.md section 4.2): each expression that parses becomes a
/// prepared sandbox script <c>(context, event) =&gt; (expression)</c> registered under the guard or action id. Parsing never runs code
/// (MQ9501). A guard's condition, written in the expression language (next-phase-3-design.md section 3.1, "Process guards"), is typed in
/// its process's scope and lowered to JavaScript into the same script, after the expression runtime as a prelude; a process without one
/// loads no runtime. Compiled forms are cached by the content of the process's expressions and lowered conditions.
/// </summary>
public sealed class ProcessExpressions
{
    /// <summary>The per-expression deadline (ms).</summary>
    public const int DeadlineMs = 50;

    /// <summary>The per-expression statement limit.</summary>
    public const long StatementLimit = 100_000;

    // The compiled expressions travel with the process object: replaying one model reuses them however many processes it has,
    // and they go when the model does. The content cache only shares them across models (a process an edit left unchanged),
    // up to CacheLimit; it is consulted once per process object, so a large model no longer clears it on every replay.
    // A process with conditions lowers them in its model's scope (an enum member's code, a cited constraint), which can change
    // while the process object does not, so its compiled forms travel with the model and the process together.
    private static readonly ConditionalWeakTable<Process, ProcessExpressions> ByProcess = new();
    private static readonly ConditionalWeakTable<ModelSnapshot, ConditionalWeakTable<Process, ProcessExpressions>> ByModel = new();
    private const int CacheLimit = 512;
    private static readonly ConcurrentDictionary<string, ProcessExpressions> Cache = new(StringComparer.Ordinal);

    private readonly IReadOnlyList<PreparedScript> _scripts;

    private ProcessExpressions(Process process, IReadOnlyList<LoweredCondition> conditions)
    {
        var code = new StringBuilder();
        var compiled = new HashSet<string>(StringComparer.Ordinal);
        var replaced = conditions.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Problems = Collect(process, compiled, code, replaced);
        if (conditions.Count > 0)
        {
            // The runtime and the clock first, so a condition's function closes over them; the JavaScript expressions keep their form.
            code.Insert(0, Prelude);
            foreach (var condition in conditions)
            {
                if (!compiled.Add(condition.Id))
                    continue;
                code.Append("maquettiste.helper(").Append(JsonSerializer.Serialize(condition.Id)).Append(", ")
                    .Append(condition.ReadsNow ? "(row, now) => ((mq) => " + condition.JavaScript + ")(mqAt(now))(row)" : condition.JavaScript).Append(");\n");
            }
        }

        Compiled = compiled;
        Conditions = conditions.Select(c => c.Id).Where(compiled.Contains).ToHashSet(StringComparer.Ordinal);
        Script = compiled.Count == 0 ? "" : code.ToString();
        _scripts = compiled.Count == 0 ? [] : PreparedScript.PrepareAll([new ScriptSource("process:" + process.Name + ".js", Script, "")], Limits);
    }

    /// <summary>
    /// The prelude of a process with conditions: the expression runtime, and <c>mqAt</c>, the runtime whose <c>now()</c> is the
    /// simulation's clock, which a condition that reads <c>now</c> receives as its second argument, so replays stay deterministic.
    /// </summary>
    private static string Prelude => ExprRuntime.Source + "\nconst mqAt = (now) => Object.freeze({ ...mq, now: () => mq.tsz(now) });\n";

    /// <summary>The sandbox limits of an expression: 50 ms and 100,000 statements; the other limits are the sandbox defaults.</summary>
    public static SandboxLimits Limits { get; } = new() { ScriptTimeoutMs = DeadlineMs, ScriptStatements = StatementLimit };

    /// <summary>
    /// The limits of an expression in a project: <see cref="Limits"/>, without the 50 ms deadline when the project turns the scripts'
    /// wall clock off (<c>limits.scriptTimeoutMs: 0</c>, next-phase-1-design.md section 5.7), so that validation, verification and
    /// generation do not depend on the machine's load there either. The statement limit then bounds every expression alone.
    /// </summary>
    /// <param name="project">The project's limits.</param>
    /// <returns>The expression limits.</returns>
    internal static SandboxLimits LimitsIn(SandboxLimits project) => project.ScriptTimeoutMs <= 0 ? Limits with { ScriptTimeoutMs = 0 } : Limits;

    /// <summary>The expressions that do not parse, in guard then action order.</summary>
    public IReadOnlyList<ExpressionProblem> Problems { get; }

    /// <summary>The ids of the guards and actions whose expressions, or conditions, compiled.</summary>
    public IReadOnlySet<string> Compiled { get; }

    /// <summary>The ids of the guards whose conditions compiled: each answers only true or false, a null operand failing it.</summary>
    public IReadOnlySet<string> Conditions { get; }

    /// <summary>The prepared script's text (empty when nothing compiled), for tests.</summary>
    internal string Script { get; }

    /// <summary>
    /// The compiled expressions of a process (held by the process object, and shared by the expressions' content). A guard's condition
    /// needs its model to compile: use <see cref="Get(ModelSnapshot, Process)"/>; here such a guard is a stub.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <returns>The compiled expressions.</returns>
    public static ProcessExpressions Get(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return ByProcess.GetValue(process, static p => FromContent(p, []));
    }

    /// <summary>
    /// The compiled expressions of a process of a model, its guards' conditions included: each is typed in the process's scope and
    /// lowered as <see cref="RGuard.Condition"/> is, and one that does not type stays a stub (validation says why). A process
    /// without conditions is <see cref="Get(Process)"/>.
    /// </summary>
    /// <param name="model">The model that holds the process.</param>
    /// <param name="process">The process.</param>
    /// <returns>The compiled expressions.</returns>
    public static ProcessExpressions Get(ModelSnapshot model, Process process)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        if (process.Guards.All(g => g.Condition is null))
            return Get(process);
        return ByModel.GetOrCreateValue(model).GetValue(process, p => FromContent(p, Lower(model, p)));
    }

    private static ProcessExpressions FromContent(Process process, IReadOnlyList<LoweredCondition> conditions)
    {
        var key = Key(process, conditions);
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        if (Cache.Count >= CacheLimit)
            Cache.Clear();
        return Cache.GetOrAdd(key, _ => new ProcessExpressions(process, conditions));
    }

    /// <summary>The conditions of a process's guards that type, lowered at a guard site through the API's form of values.</summary>
    private static List<LoweredCondition> Lower(ModelSnapshot model, Process process)
    {
        var path = model.GetDocument(process.Id)?.Path;
        var lowered = new List<LoweredCondition>();
        foreach (var guard in process.Guards)
        {
            if (guard.Condition is null || ExprResolution.Type(model, process, guard.Id) is not { } typed)
                continue;
            var js = ExprLowering.ToJavaScript(typed.Typed, ExprSiteKind.Guard, ExprResolution.ApiColumns(model, guard.Id, path));
            lowered.Add(new LoweredCondition(guard.Id, js, ReadsNow(typed.Typed)));
        }

        return lowered;

        static bool ReadsNow(TypedExpr t) => t.Node.Now is not null || (t.Body is { } body && ReadsNow(body)) || t.Args.Any(ReadsNow);
    }

    /// <summary>
    /// The expressions of a process that do not parse (MQ9501), in guard then action order: the <see cref="Problems"/> of
    /// <see cref="Get(Process)"/>, found by parsing alone. Validation reads these for every process, so it neither compiles the expressions
    /// nor goes through the shared cache: a model with more processes than the cache holds would clear it on every run, and with
    /// every validation thread contending for it that cost more than the rest of the process rules together.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <returns>The problems.</returns>
    internal static IReadOnlyList<ExpressionProblem> ProblemsOf(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Collect(process, new HashSet<string>(StringComparer.Ordinal), null, null);
    }

    /// <summary>Whether <see cref="Get(Process)"/> holds compiled expressions for the process's current expressions (for tests).</summary>
    /// <param name="process">The process.</param>
    /// <returns><see langword="true"/> when they are cached.</returns>
    internal static bool IsCached(Process process) => ByProcess.TryGetValue(process, out _) || Cache.ContainsKey(Key(process, []));

    /// <summary>
    /// Whether <see cref="Get(ModelSnapshot, Process)"/> holds compiled expressions for the process's current expressions and lowered
    /// conditions in the model (for tests): a rename that changes a condition's JavaScript gives a new key.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns><see langword="true"/> when they are cached.</returns>
    internal static bool IsCached(ModelSnapshot model, Process process) =>
        process.Guards.All(g => g.Condition is null)
            ? IsCached(process)
            : (ByModel.TryGetValue(model, out var processes) && processes.TryGetValue(process, out _)) || Cache.ContainsKey(Key(process, Lower(model, process)));

    // The problems of a process's expressions; the ids that parse go to compiled and, with code, their helper registrations to code. A
    // guard whose condition replaces its expression is still parsed, so the problems are the same with or without the model.
    private static List<ExpressionProblem> Collect(Process process, HashSet<string> compiled, StringBuilder? code, IReadOnlySet<string>? replaced)
    {
        var problems = new List<ExpressionProblem>();
        for (var i = 0; i < process.Guards.Count; i++)
            Add(process.Guards[i].Id, process.Guards[i].Name, "guard", process.Guards[i].Expression, "/guards/" + N(i) + "/expression");
        for (var i = 0; i < process.Actions.Count; i++)
            Add(process.Actions[i].Id, process.Actions[i].Name, "action", process.Actions[i].Expression, "/actions/" + N(i) + "/expression");
        return problems;

        void Add(string id, string name, string kind, string? expression, string pointer)
        {
            if (expression is null || compiled.Contains(id))
                return;
            if (Parse(expression) is { } problem)
            {
                problems.Add(new ExpressionProblem(id, pointer, name, kind, problem));
                return;
            }

            if (replaced?.Contains(id) ?? false)
                return;
            compiled.Add(id);
            code?.Append("maquettiste.helper(").Append(JsonSerializer.Serialize(id)).Append(", ").Append(Wrap(expression)).Append(");\n");
        }
    }

    /// <summary>Returns why an expression does not parse as one expression, or <see langword="null"/>; nothing runs.</summary>
    /// <param name="expression">The expression text.</param>
    /// <returns>The problem.</returns>
    public static string? Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        try
        {
            var prepared = JsEngine.PrepareScript(Wrap(expression), "expression", strict: true);
            var body = prepared.Program!.Body;
            return body.Count == 1 && body[0] is ExpressionStatement { Expression: ArrowFunctionExpression }
                ? null
                : "it is not a single expression (a closing parenthesis or a statement separator leaves the expression)";
        }
        catch (ScriptPreparationException ex)
        {
            return ex.InnerException is Acornima.ParseErrorException parse
                ? parse.Description + " at column " + (Math.Max(0, parse.Column) + 1).ToString(CultureInfo.InvariantCulture) + " of line " + Math.Max(1, parse.LineNumber - 1).ToString(CultureInfo.InvariantCulture)
                : ex.Message;
        }
    }

    /// <summary>Opens a session: a sandbox pool over the compiled expressions, bound to <paramref name="ct"/>.</summary>
    /// <param name="size">The most sandboxes kept idle.</param>
    /// <param name="ct">The run's cancellation.</param>
    /// <returns>The session; dispose it when done.</returns>
    public ProcessExpressionSession Open(int size, CancellationToken ct) => Open(size, ct, Limits);

    /// <summary>
    /// Opens a session with other sandbox limits: a project's (<see cref="LimitsIn"/>), or a test's that reaches one limit before the
    /// other. A process with conditions runs without the wall clock, as <see cref="ExprEvaluator.Limits"/> do: loading the expression
    /// runtime and its exact arithmetic take a time that depends on the machine's load, and a guard's answer, so a replay, must not;
    /// the statement limit still bounds every call, its JavaScript expressions' too.
    /// </summary>
    /// <param name="size">The most sandboxes kept idle.</param>
    /// <param name="ct">The run's cancellation.</param>
    /// <param name="limits">The limits of every expression run in the session.</param>
    /// <returns>The session; dispose it when done.</returns>
    public ProcessExpressionSession Open(int size, CancellationToken ct, SandboxLimits limits)
    {
        if (_scripts.Count == 0)
            return new ProcessExpressionSession(null, Compiled, Conditions);
        var pool = new ScriptSandboxPool(_scripts, Conditions.Count > 0 ? limits with { ScriptTimeoutMs = 0 } : limits, Math.Max(1, size), ct);
        WarmUp(pool, Compiled.First(), ct);
        return new ProcessExpressionSession(pool, Compiled, Conditions);
    }

    // The first call into a fresh engine pays the runtime's one-time costs (code paths compiled on first use), which
    // on a cold or slow host can exceed the per-expression deadline by itself. One throwaway evaluation of the first
    // expression, with empty inputs and its outcome ignored, takes that cost before any real evaluation is timed.
    private static void WarmUp(IScriptSandboxPool pool, string id, CancellationToken ct)
    {
        using var empty = JsonDocument.Parse("{}");
        using var lease = pool.Rent();
        try
        {
            lease.Sandbox.CallHelper(id, [empty.RootElement, empty.RootElement], new ScriptCallContext(null, "warm-up", ImmutableDictionary<string, object?>.Empty, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A throw or a limit on empty inputs is expected for many expressions; only the warm-up matters.
        }
    }

    private static string Wrap(string expression) => "(context, event) => (\n" + expression + "\n)";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    // The content key: a condition's lowered JavaScript names what it reads by current name and holds each enum member's or code's
    // literal, so a rename that changes what it reads changes the key.
    private static string Key(Process process, IReadOnlyList<LoweredCondition> conditions)
    {
        var text = new StringBuilder(process.Name).Append('\0');
        foreach (var g in process.Guards)
            text.Append('g').Append(g.Id).Append('\0').Append(g.Expression ?? "\u0001").Append('\0');
        foreach (var a in process.Actions)
            text.Append('a').Append(a.Id).Append('\0').Append(a.Expression ?? "\u0001").Append('\0');
        foreach (var c in conditions)
            text.Append('c').Append(c.Id).Append('\0').Append(c.JavaScript).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>A guard's condition lowered to JavaScript.</summary>
    /// <param name="Id">The guard id.</param>
    /// <param name="JavaScript">The function over a row (<see cref="ExprLowering.ToJavaScript"/>).</param>
    /// <param name="ReadsNow">Whether it reads <c>now</c>, so it receives the simulation's clock.</param>
    private sealed record LoweredCondition(string Id, string JavaScript, bool ReadsNow);
}

/// <summary>The result of one expression evaluation.</summary>
/// <param name="Value">The result as JSON-like data (a bool, number, string, null, map or list); <see langword="null"/> on failure.</param>
/// <param name="Diagnostic">MQ9502 (the expression threw) or MQ9503 (a limit), or <see langword="null"/>.</param>
public sealed record ExpressionResult(object? Value, Diagnostic? Diagnostic);

/// <summary>
/// A sandbox pool over one process's compiled expressions (phase-3-design.md section 4.2). <c>context</c> and <c>event</c> cross as
/// frozen JSON-like data; <c>Math.random</c> is seeded per call. Thread-safe: each evaluation rents a lease.
/// </summary>
public sealed class ProcessExpressionSession : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = ImmutableDictionary<string, object?>.Empty;
    private readonly IScriptSandboxPool? _pool;

    internal ProcessExpressionSession(IScriptSandboxPool? pool, IReadOnlySet<string> compiled, IReadOnlySet<string> conditions)
    {
        _pool = pool;
        Compiled = compiled;
        Conditions = conditions;
    }

    /// <summary>The ids of the guards and actions whose expressions, or conditions, compiled (<see cref="ProcessExpressions.Compiled"/>).</summary>
    public IReadOnlySet<string> Compiled { get; }

    /// <summary>The ids of the guards whose conditions compiled (<see cref="ProcessExpressions.Conditions"/>): evaluate them with
    /// <see cref="EvaluateCondition"/>.</summary>
    public IReadOnlySet<string> Conditions { get; }

    /// <summary>Evaluates a compiled guard or action expression.</summary>
    /// <param name="id">The guard or action id.</param>
    /// <param name="label">"Guard 'name'" or "Action 'name'", for messages.</param>
    /// <param name="context">The context object (attribute names to values).</param>
    /// <param name="event">The event object (<c>name</c>, <c>actor</c>, <c>payload</c>).</param>
    /// <param name="seed">The seed of <c>Math.random</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    public ExpressionResult Evaluate(string id, string label, JsonElement context, JsonElement @event, string seed, CancellationToken ct)
    {
        if (_pool is null)
            throw new InvalidOperationException($"{label} has no compiled expression.");
        using var lease = _pool.Rent();
        try
        {
            return new ExpressionResult(lease.Sandbox.CallHelper(id, [context, @event], new ScriptCallContext(null, seed, NoParameters, ct)), null);
        }
        catch (ScriptLimitException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9503", $"{label} stopped: {Detail(ex.Diagnostic.Message)} Simplify the expression, or move the work to a handler (leave the expression empty).", id, null, null));
        }
        catch (ScriptErrorException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9502", $"{label} threw: {Detail(ex.Diagnostic.Message)} Fix the expression, for example guard against missing values.", id, null, null));
        }
    }

    /// <summary>
    /// Evaluates a compiled guard condition (next-phase-3-design.md section 3.1, "Process guards") over one row: the context's
    /// attributes by name, and the event's payload under <c>event</c>, every number crossing as its exact text. The answer is
    /// <see langword="true"/> only when the condition holds; unknown, a null operand among the causes, is <see langword="false"/>.
    /// </summary>
    /// <param name="id">The guard id.</param>
    /// <param name="label">"Guard 'name'", for messages.</param>
    /// <param name="context">The context object (attribute names to values).</param>
    /// <param name="event">The event object (<c>name</c>, <c>actor</c>, <c>payload</c>).</param>
    /// <param name="now">The simulation's clock, an ISO 8601 instant in UTC, which <c>now()</c> reads.</param>
    /// <param name="seed">The seed of <c>Math.random</c>, which no condition reads.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result: a <see cref="bool"/>, or MQ9502 or MQ9503.</returns>
    public ExpressionResult EvaluateCondition(string id, string label, JsonElement context, JsonElement @event, string now, string seed, CancellationToken ct)
    {
        if (_pool is null)
            throw new InvalidOperationException($"{label} has no compiled condition.");
        var row = Row(context, @event);
        using var lease = _pool.Rent();
        try
        {
            var value = lease.Sandbox.CallHelper(id, [new ExactJson(row), now], new ScriptCallContext(null, seed, NoParameters, ct));
            return new ExpressionResult(value is true, null);
        }
        catch (ScriptLimitException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9503", $"{label} stopped: {Detail(ex.Diagnostic.Message)} Simplify the condition.", id, null, null));
        }
        catch (ScriptErrorException ex)
        {
            return new ExpressionResult(null, RuleCatalog.Create("MQ9502", $"{label} threw: {Detail(ex.Diagnostic.Message)}", id, null, null));
        }
    }

    // The row a condition reads: the context's members, then the payload as event (a context attribute named event gives way to it).
    private static JsonElement Row(JsonElement context, JsonElement @event)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (context.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in context.EnumerateObject())
                {
                    if (!string.Equals(member.Name, "event", StringComparison.Ordinal))
                        member.WriteTo(writer);
                }
            }

            writer.WritePropertyName("event");
            if (@event.ValueKind == JsonValueKind.Object && @event.TryGetProperty("payload", out var payload))
                payload.WriteTo(writer);
            else
                writer.WriteNullValue();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Converts an evaluation result to JSON.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON value.</returns>
    public static JsonElement ToJson(object? value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, value);
        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    /// <inheritdoc/>
    public void Dispose() => _pool?.Dispose();

    private static string Detail(string message)
    {
        var text = message.Replace("Helper '", "'", StringComparison.Ordinal).Trim();
        return text.EndsWith('.') ? text : text + ".";
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case double d when double.IsFinite(d) && d == Math.Floor(d) && Math.Abs(d) < 9e15:
                writer.WriteNumberValue((long)d);
                break;
            case double d when double.IsFinite(d):
                writer.WriteNumberValue(d);
                break;
            case double:
                writer.WriteNullValue();
                break;
            case int or long or decimal or float or short or byte or sbyte or ushort or uint:
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                break;
            case ulong u:
                writer.WriteNumberValue(u);
                break;
            case System.Numerics.BigInteger big:
                // Every digit, as JSON text: a tree expression reads it exactly (mq.dec).
                writer.WriteRawValue(big.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonElement json:
                json.WriteTo(writer);
                break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (k, v) in map)
                {
                    writer.WritePropertyName(k);
                    Write(writer, v);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable list:
                writer.WriteStartArray();
                foreach (var item in list)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
