using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acornima.Ast;
using Jint;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
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
/// (MQ9501). Compiled forms are cached by the content of the process's expressions.
/// </summary>
public sealed class ProcessExpressions
{
    /// <summary>The per-expression deadline (ms).</summary>
    public const int DeadlineMs = 50;

    /// <summary>The per-expression statement limit.</summary>
    public const long StatementLimit = 100_000;

    private const int CacheLimit = 512;
    private static readonly ConcurrentDictionary<string, ProcessExpressions> Cache = new(StringComparer.Ordinal);

    private readonly IReadOnlyList<PreparedScript> _scripts;

    private ProcessExpressions(Process process)
    {
        var problems = new List<ExpressionProblem>();
        var code = new StringBuilder();
        var compiled = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < process.Guards.Count; i++)
            Add(process.Guards[i].Id, process.Guards[i].Name, "guard", process.Guards[i].Expression, "/guards/" + N(i) + "/expression");
        for (var i = 0; i < process.Actions.Count; i++)
            Add(process.Actions[i].Id, process.Actions[i].Name, "action", process.Actions[i].Expression, "/actions/" + N(i) + "/expression");
        Problems = problems;
        Compiled = compiled;
        _scripts = compiled.Count == 0 ? [] : PreparedScript.PrepareAll([new ScriptSource("process:" + process.Name + ".js", code.ToString(), "")], Limits);

        void Add(string id, string name, string kind, string? expression, string pointer)
        {
            if (expression is null || compiled.Contains(id))
                return;
            if (Parse(expression) is { } problem)
            {
                problems.Add(new ExpressionProblem(id, pointer, name, kind, problem));
                return;
            }

            compiled.Add(id);
            code.Append("maquettiste.helper(").Append(JsonSerializer.Serialize(id)).Append(", ").Append(Wrap(expression)).Append(");\n");
        }
    }

    /// <summary>The sandbox limits of an expression: 50 ms and 100,000 statements; the other limits are the sandbox defaults.</summary>
    public static SandboxLimits Limits { get; } = new() { ScriptTimeoutMs = DeadlineMs, ScriptStatements = StatementLimit };

    /// <summary>The expressions that do not parse, in guard then action order.</summary>
    public IReadOnlyList<ExpressionProblem> Problems { get; }

    /// <summary>The ids of the guards and actions whose expressions compiled.</summary>
    public IReadOnlySet<string> Compiled { get; }

    /// <summary>The compiled expressions of a process (cached by the expressions' content).</summary>
    /// <param name="process">The process.</param>
    /// <returns>The compiled expressions.</returns>
    public static ProcessExpressions Get(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var key = Key(process);
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        if (Cache.Count >= CacheLimit)
            Cache.Clear();
        return Cache.GetOrAdd(key, _ => new ProcessExpressions(process));
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

    /// <summary>Opens a session with other sandbox limits (tests use it to reach one limit before the other).</summary>
    /// <param name="size">The most sandboxes kept idle.</param>
    /// <param name="ct">The run's cancellation.</param>
    /// <param name="limits">The limits of every expression run in the session.</param>
    /// <returns>The session; dispose it when done.</returns>
    public ProcessExpressionSession Open(int size, CancellationToken ct, SandboxLimits limits) =>
        new(_scripts.Count == 0 ? null : new ScriptSandboxPool(_scripts, limits, Math.Max(1, size), ct));

    private static string Wrap(string expression) => "(context, event) => (\n" + expression + "\n)";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Key(Process process)
    {
        var text = new StringBuilder(process.Name).Append('\0');
        foreach (var g in process.Guards)
            text.Append('g').Append(g.Id).Append('\0').Append(g.Expression ?? "\u0001").Append('\0');
        foreach (var a in process.Actions)
            text.Append('a').Append(a.Id).Append('\0').Append(a.Expression ?? "\u0001").Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
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

    internal ProcessExpressionSession(IScriptSandboxPool? pool) => _pool = pool;

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
            case int or long or decimal or float:
                writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
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
