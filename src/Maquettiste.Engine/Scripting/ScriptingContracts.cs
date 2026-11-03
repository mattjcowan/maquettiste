using System.Text.Json;
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

    /// <summary>
    /// Creates a pool whose scripts reach the given packs' helpers through <c>maquettiste.use(name)</c> (library packs,
    /// next-phase-2-design.md section 5.2). The scripts load exactly as they do without libraries; only <c>maquettiste.use</c> is added.
    /// </summary>
    /// <param name="scripts">The scripts, in load order.</param>
    /// <param name="libraries">The packs <c>maquettiste.use</c> reaches, with their helper names.</param>
    /// <param name="limits">The sandbox limits.</param>
    /// <param name="size">The number of engines.</param>
    /// <param name="ct">The run's cancellation.</param>
    /// <returns>The pool.</returns>
    /// <exception cref="NotSupportedException">The factory cannot reach other packs' helpers and <paramref name="libraries"/> is not empty.</exception>
    IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, IReadOnlyList<ScriptLibrary> libraries, SandboxLimits limits, int size,
        CancellationToken ct)
        => libraries.Count == 0 ? CreatePool(scripts, limits, size, ct)
            : throw new NotSupportedException("This factory cannot reach other packs' helpers.");
}

/// <summary>A pool of sandboxed engines that ran the same scripts.</summary>
public interface IScriptSandboxPool : IDisposable
{
    /// <summary>What the scripts registered.</summary>
    IReadOnlyList<ScriptRegistration> Registrations { get; }

    /// <summary>Load warnings, ordinal by path: MQ5005 per rule file with top-level let or var, at the first.</summary>
    IReadOnlyList<Diagnostic> Warnings { get; }

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
public sealed record ScriptRegistration(ScriptRegistrationKind Kind, string Name, string DeclaredIn)
{
    /// <summary>A rule registered with scope "model": run once per whole-model validation, not per element.</summary>
    public bool WholeModel { get; init; }

    /// <summary>
    /// The kinds an element rule checks, or <see langword="null"/> for every kind (and for other registrations), so the validator skips
    /// a document of another kind before it rents a sandbox or reads the result cache. Internal: not part of the contract.
    /// </summary>
    internal IReadOnlySet<string>? Kinds { get; init; }
}

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

    /// <summary>
    /// A seed's rows function (<c>maquettiste.rows(fn)</c>, next-phase-2-design.md section 3.4): <c>rows</c>. Its name is the rows script's
    /// path; only a script in <c>extensions/seeds/</c> may register one, exactly once.
    /// </summary>
    [JsonStringEnumMemberName("rows")] Rows,
}

/// <summary>The context of one top-level script call.</summary>
/// <param name="Reads">The unit's read recorder, or <see langword="null"/> outside rendering.</param>
/// <param name="Seed">The seed of the deterministic <c>Math.random</c> (the unit key or element id).</param>
/// <param name="Parameters">The pack parameters.</param>
/// <param name="CancellationToken">
/// Cancellation observed by this call (checked before it starts and, through the engine's constraint, while it runs); the pool's
/// run token applies as well.
/// </param>
public sealed record ScriptCallContext(IReadRecorder? Reads, string Seed, IReadOnlyDictionary<string, object?> Parameters, CancellationToken CancellationToken)
{
    /// <summary>
    /// Where <c>maquettiste.use</c> calls go (library packs, next-phase-2-design.md section 5.2); <see langword="null"/> when no
    /// pack is reachable (while scripts load, and in selectors and filters, which run while planning).
    /// </summary>
    public IScriptLibraryCalls? Libraries { get; init; }
}

/// <summary>A pack whose helpers <c>maquettiste.use</c> reaches: its name and its helper names, ordinal.</summary>
/// <param name="Pack">The used pack's name.</param>
/// <param name="Helpers">The helpers it registered, ordinal.</param>
public sealed record ScriptLibrary(string Pack, IReadOnlyList<string> Helpers);

/// <summary>Calls a used pack's helper for the current call (library packs, next-phase-2-design.md section 5.2).</summary>
public interface IScriptLibraryCalls
{
    /// <summary>
    /// Calls a helper of a used pack in a sandbox of that pack's own pool, with the caller's read recorder and seed and the used
    /// pack's parameters. A failure is the used pack's <see cref="ScriptErrorException"/> or <see cref="ScriptLimitException"/>, at its
    /// own script.
    /// </summary>
    /// <param name="pack">The used pack.</param>
    /// <param name="helper">The helper name.</param>
    /// <param name="args">The arguments, converted out of the calling script as a script's results are.</param>
    /// <param name="caller">The calling script's call context.</param>
    /// <returns>The result, converted into the calling script as helper results are.</returns>
    object? Call(string pack, string helper, IReadOnlyList<object?> args, ScriptCallContext caller);
}

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

    /// <summary>Runs a whole-model rule once; report(message, { element, pointer, severity }).</summary>
    /// <param name="ruleId">The rule id (without <c>x/</c>) of a rule registered with <c>scope: "model"</c>.</param>
    /// <param name="model">The snapshot.</param>
    /// <param name="ctx">The call context.</param>
    /// <returns>
    /// The rule's diagnostics, each on the file of the element or sub-element its <c>element</c> option names, the entry's pointer
    /// prefixing the given one; a rule that throws (an unknown element id included) ends with its MQ5002 on the rule file.
    /// </returns>
    IReadOnlyList<Diagnostic> RunModelRule(string ruleId, ModelSnapshot model, ScriptCallContext ctx);

    /// <summary>Runs a rows script; the array it returned, as JSON. Reads go to ctx.Reads.</summary>
    /// <param name="script">The rows script's path, as it registered its rows (<see cref="ScriptRegistrationKind.Rows"/>).</param>
    /// <param name="seed">The seed document; the function gets a frozen copy of its canonical JSON.</param>
    /// <param name="model">The snapshot, seen through the rule-script model view.</param>
    /// <param name="ctx">The call context (its seed seeds <c>Math.random</c>: the seed id).</param>
    /// <returns>The returned array: plain objects whose values are JSON values.</returns>
    /// <exception cref="ScriptLimitException">The call broke a sandbox limit.</exception>
    JsonElement RunRows(string script, ElementDocument seed, ModelSnapshot model, ScriptCallContext ctx);
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
