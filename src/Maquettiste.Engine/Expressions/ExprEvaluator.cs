using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// Runs lowered JavaScript in the engine's sandbox (next-phase-3-design.md section 3.1, decision 13): one prepared script holds the
/// runtime (<see cref="ExprRuntime.Source"/>) and the lowered functions, each registered as a helper under its id, and every call
/// passes the row with its numbers as exact text (<see cref="ExactJson"/>), so decimals and 64-bit integers reach <c>mq.dec</c>
/// with every digit. The engine's guards and rule conditions, and the corpus, evaluate through it.
/// </summary>
public sealed class ExprEvaluator : IDisposable
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = ImmutableDictionary<string, object?>.Empty;
    private readonly ScriptSandboxPool _pool;

    private ExprEvaluator(ScriptSandboxPool pool) => _pool = pool;

    /// <summary>The limits a lowered expression runs under: statements only, so an answer never depends on the machine's load.</summary>
    public static SandboxLimits Limits { get; } = new() { ScriptTimeoutMs = 0, ScriptStatements = 100_000 };

    /// <summary>The script that registers the runtime and the functions: <c>maquettiste.helper(id, fn)</c> for each, in order.</summary>
    /// <param name="functions">The lowered functions by id (<see cref="ExprLowering.ToJavaScript"/>).</param>
    /// <returns>The script's text.</returns>
    public static string Script(IEnumerable<KeyValuePair<string, string>> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var code = new StringBuilder(ExprRuntime.Source).Append('\n');
        foreach (var (id, fn) in functions)
            code.Append("maquettiste.helper(").Append(ExprJs.Str(id)).Append(", ").Append(fn).Append(");\n");
        return code.ToString();
    }

    /// <summary>Prepares lowered functions to run.</summary>
    /// <param name="functions">The lowered functions by id.</param>
    /// <param name="limits">The limits, or <see langword="null"/> for <see cref="Limits"/>.</param>
    /// <param name="ct">Cancels the pool's runs.</param>
    /// <returns>The evaluator.</returns>
    public static ExprEvaluator Create(IEnumerable<KeyValuePair<string, string>> functions, SandboxLimits? limits = null, CancellationToken ct = default)
    {
        var effective = limits ?? Limits;
        var scripts = PreparedScript.PrepareAll([new ScriptSource("expressions:lowered.js", Script(functions), "")], effective);
        return new ExprEvaluator(new ScriptSandboxPool(scripts, effective, 1, ct));
    }

    /// <summary>
    /// Evaluates one function over a row: a check or guard answers a <see cref="bool"/>; a value answers its JSON form (a decimal or
    /// integer as exact text, a date or timestamp as ISO text, a double as a number) or <see langword="null"/>.
    /// </summary>
    /// <param name="id">The function's id.</param>
    /// <param name="row">The row, context or payload: an object whose members the expression reads by name.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The answer.</returns>
    public object? Evaluate(string id, JsonElement row, CancellationToken ct = default)
    {
        using var lease = _pool.Rent();
        return lease.Sandbox.CallHelper(id, [new ExactJson(row)], new ScriptCallContext(null, "expression", NoParameters, ct));
    }

    /// <inheritdoc/>
    public void Dispose() => _pool.Dispose();
}
