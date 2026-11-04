using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// Runs lowered JavaScript in the engine's sandbox (next-phase-3-design.md section 3.1, decision 13): each engine loads the runtime
/// prepared once for every pool (<see cref="ExprRuntime.Prepared"/>), then a script of the lowered functions, each registered as a
/// helper under its id, and every call passes the row with its numbers as exact text (<see cref="ExactJson"/>), so decimals and
/// 64-bit integers reach <c>mq.dec</c> with every digit. The engine's guards and rule conditions, and the corpus, evaluate through it.
/// The pool is built on the first call, so a failure to load is that call's limit or error.
/// </summary>
public sealed class ExprEvaluator : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = ImmutableDictionary<string, object?>.Empty;
    private readonly LazySandboxPool _pool;

    private ExprEvaluator(IReadOnlyList<PreparedScript> scripts, SandboxLimits limits, CancellationToken ct)
    {
        Scripts = scripts;
        _pool = new LazySandboxPool(() => new ScriptSandboxPool(scripts, limits, 1, ct));
    }

    /// <summary>The scripts each engine loads, in order: the shared runtime, then the functions (for tests).</summary>
    internal IReadOnlyList<PreparedScript> Scripts { get; }

    /// <summary>The limits a lowered expression runs under: statements only, so an answer never depends on the machine's load.</summary>
    public static SandboxLimits Limits { get; } = new() { ScriptTimeoutMs = 0, ScriptStatements = 100_000 };

    /// <summary>
    /// The script that registers the functions, <c>maquettiste.helper(id, fn)</c> for each, in order; it loads after the runtime
    /// (<see cref="ExprRuntime.Prepared"/>), whose <c>mq</c> the functions read.
    /// </summary>
    /// <param name="functions">The lowered functions by id (<see cref="ExprLowering.ToJavaScript"/>).</param>
    /// <returns>The script's text.</returns>
    public static string Script(IEnumerable<KeyValuePair<string, string>> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var code = new StringBuilder();
        foreach (var (id, fn) in functions)
            code.Append("maquettiste.helper(").Append(ExprJs.Str(id)).Append(", ").Append(fn).Append(");\n");
        return code.ToString();
    }

    /// <summary>Prepares lowered functions to run; nothing loads before the first call.</summary>
    /// <param name="functions">The lowered functions by id.</param>
    /// <param name="limits">The limits, or <see langword="null"/> for <see cref="Limits"/>.</param>
    /// <param name="ct">Cancels the pool's runs.</param>
    /// <returns>The evaluator.</returns>
    public static ExprEvaluator Create(IEnumerable<KeyValuePair<string, string>> functions, SandboxLimits? limits = null, CancellationToken ct = default)
    {
        var effective = limits ?? Limits;
        var scripts = PreparedScript.PrepareAll([new ScriptSource("expressions:lowered.js", Script(functions), "")], effective);
        return new ExprEvaluator([ExprRuntime.Prepared, .. scripts], effective, ct);
    }

    /// <summary>
    /// Evaluates one function over a row: a check or guard answers a <see cref="bool"/>; a value answers its JSON form (a decimal or
    /// integer as exact text, a date or timestamp as ISO text, a double as a number) or <see langword="null"/>.
    /// </summary>
    /// <param name="id">The function's id.</param>
    /// <param name="row">The row, context or payload: an object whose members the expression reads by name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ScriptLimitException">The call, or the first call's load, reached a limit.</exception>
    /// <exception cref="ScriptErrorException">The function, or the first call's load, threw.</exception>
    public object? Evaluate(string id, JsonElement row, CancellationToken ct = default)
    {
        using var lease = _pool.Rent();
        return lease.Sandbox.CallHelper(id, [new ExactJson(row)], new ScriptCallContext(null, "expression", NoParameters, ct));
    }

    /// <inheritdoc/>
    public void Dispose() => _pool.Dispose();
}
