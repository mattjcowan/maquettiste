using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Scripting;

/// <summary>Creates pools of sandboxed Jint engines for a set of scripts (W4; engine-design.md section 10).</summary>
public interface IScriptSandboxFactory
{
    /// <summary>Creates a pool; scripts are prepared once per pool. Pools live for one run (or one validation).</summary>
    /// <param name="scripts">The scripts, in load order.</param>
    /// <param name="limits">The sandbox limits.</param>
    /// <param name="size">The number of engines (the render parallelism).</param>
    /// <param name="ct">
    /// The run's cancellation, wired into every engine (Jint <c>Options.CancellationToken</c>), so a running script stops within the
    /// run's one-second cancellation budget (host-contracts requirement 26) even though the script timeout is longer.
    /// </param>
    /// <returns>The pool.</returns>
    IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, SandboxLimits limits, int size, CancellationToken ct);
}

/// <summary>A pool of sandboxed engines that ran the same scripts.</summary>
public interface IScriptSandboxPool : IDisposable
{
    /// <summary>What the scripts registered.</summary>
    IReadOnlyList<ScriptRegistration> Registrations { get; }

    /// <summary>Rents an engine for a whole unit; dispose the lease to return it.</summary>
    /// <returns>The lease.</returns>
    IScriptSandboxLease Rent();
}

/// <summary>A rented sandbox.</summary>
public interface IScriptSandboxLease : IDisposable
{
    /// <summary>The sandbox.</summary>
    IScriptSandbox Sandbox { get; }
}

/// <summary>One registration made by a script through the <c>maquettiste</c> global.</summary>
/// <param name="Kind">What was registered.</param>
/// <param name="Name">The registered name (the rule id for rules).</param>
/// <param name="DeclaredIn">The script path.</param>
public sealed record ScriptRegistration(ScriptRegistrationKind Kind, string Name, string DeclaredIn);

/// <summary>What a script registered.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScriptRegistrationKind>))]
public enum ScriptRegistrationKind
{
    /// <summary>A template helper: <c>helper</c>.</summary>
    [JsonStringEnumMemberName("helper")] Helper,

    /// <summary>A unit selector (<c>for: select &lt;name&gt;</c>): <c>selector</c>.</summary>
    [JsonStringEnumMemberName("selector")] Selector,

    /// <summary>A <c>where.script</c> filter: <c>filter</c>.</summary>
    [JsonStringEnumMemberName("filter")] Filter,

    /// <summary>A pre-render transform: <c>transform</c>.</summary>
    [JsonStringEnumMemberName("transform")] Transform,

    /// <summary>A validation rule: <c>rule</c>.</summary>
    [JsonStringEnumMemberName("rule")] Rule,
}

/// <summary>The context of one top-level script call.</summary>
/// <param name="Reads">The unit's read recorder, or <see langword="null"/> outside rendering.</param>
/// <param name="Seed">The seed of the deterministic <c>Math.random</c> (the unit key or element id).</param>
/// <param name="Parameters">The pack parameters.</param>
/// <param name="CancellationToken">
/// Cancellation observed by this call (checked before it starts and, through the engine's constraint, while it runs); the pool's
/// run token applies as well.
/// </param>
public sealed record ScriptCallContext(IReadRecorder? Reads, string Seed, IReadOnlyDictionary<string, object?> Parameters, CancellationToken CancellationToken);

/// <summary>One sandboxed Jint engine. Not thread-safe: one unit at a time, on one thread.</summary>
public interface IScriptSandbox
{
    /// <summary>Calls a helper.</summary>
    /// <param name="name">The helper name.</param>
    /// <param name="args">The arguments (JSON-like values or resolved objects).</param>
    /// <param name="ctx">The call context.</param>
    /// <returns>The result, as a JSON-like value or resolved object.</returns>
    object? CallHelper(string name, IReadOnlyList<object?> args, ScriptCallContext ctx);

    /// <summary>Runs a selector.</summary>
    /// <param name="name">The selector name.</param>
    /// <param name="model">The resolved model.</param>
    /// <param name="ctx">The call context.</param>
    /// <returns>Element ids.</returns>
    IReadOnlyList<string> Select(string name, ResolvedModel model, ScriptCallContext ctx);

    /// <summary>Runs a filter.</summary>
    /// <param name="name">The filter name.</param>
    /// <param name="element">The candidate element.</param>
    /// <param name="model">The resolved model.</param>
    /// <param name="ctx">The call context.</param>
    /// <returns>Whether the element is kept.</returns>
    bool Filter(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx);

    /// <summary>Runs a pre-render transform.</summary>
    /// <param name="name">The transform name.</param>
    /// <param name="element">The unit's element.</param>
    /// <param name="model">The resolved model.</param>
    /// <param name="ctx">The call context.</param>
    /// <returns>Values merged into the template's <c>data</c>.</returns>
    IReadOnlyDictionary<string, object?> Transform(string name, IResolvedObject element, ResolvedModel model, ScriptCallContext ctx);

    /// <summary>Runs a validation rule on one element.</summary>
    /// <param name="ruleId">The rule id (without <c>x/</c>).</param>
    /// <param name="element">The element document.</param>
    /// <param name="model">The snapshot.</param>
    /// <param name="ctx">The call context.</param>
    /// <returns>The rule's diagnostics.</returns>
    IReadOnlyList<Diagnostic> RunRule(string ruleId, ElementDocument element, ModelSnapshot model, ScriptCallContext ctx);
}

/// <summary>Thrown when a script or template exceeds a sandbox limit (MQ6007 while rendering, MQ5003 in validation rules).</summary>
public sealed class ScriptLimitException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="diagnostic">The diagnostic describing the breach.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    public ScriptLimitException(Diagnostic diagnostic, Exception? inner = null)
        : base(diagnostic.Message, inner)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>The diagnostic describing the breach.</summary>
    public Diagnostic Diagnostic { get; }
}
