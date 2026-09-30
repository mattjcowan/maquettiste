using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>How a process operation ended; the API maps it to a status, the CLI to an exit code, MCP to a problem.</summary>
public enum ProcessCallStatus
{
    /// <summary>Done (200, or 201 for a recorded scenario).</summary>
    Ok,

    /// <summary>The process, a scenario or a named element does not exist (404).</summary>
    NotFound,

    /// <summary>The request is malformed (400).</summary>
    BadRequest,

    /// <summary>The draft, the import or the write is not valid (422, with diagnostics).</summary>
    Invalid,

    /// <summary>What the caller read changed before the write (409).</summary>
    Conflict,
}

/// <summary>The result of a process operation.</summary>
/// <typeparam name="T">The body of a success.</typeparam>
/// <param name="Status">The status.</param>
/// <param name="Value">The body (also on <see cref="ProcessCallStatus.Invalid"/> and <see cref="ProcessCallStatus.Conflict"/> when the operation has one).</param>
/// <param name="Detail">Why the call failed, for 400 and 404.</param>
/// <param name="Diagnostics">The diagnostics of an invalid draft or write.</param>
public sealed record ProcessCall<T>(ProcessCallStatus Status, T? Value, string? Detail, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>A success.</summary>
    /// <param name="value">The body.</param>
    /// <returns>The call.</returns>
    public static ProcessCall<T> Ok(T value) => new(ProcessCallStatus.Ok, value, null, []);

    /// <summary>A failure without a body.</summary>
    /// <param name="status">The status.</param>
    /// <param name="detail">Why.</param>
    /// <param name="diagnostics">The diagnostics.</param>
    /// <returns>The call.</returns>
    public static ProcessCall<T> Fail(ProcessCallStatus status, string? detail, IReadOnlyList<Diagnostic>? diagnostics = null) => new(status, default, detail, diagnostics ?? []);
}

/// <summary>The body of <c>POST /api/processes/{id}/simulate</c> (phase-3-design.md section 4.4).</summary>
public sealed record SimulateRequest
{
    /// <summary>An unsaved draft of the process document to simulate instead of the saved one.</summary>
    public JsonElement? Document { get; init; }

    /// <summary>The start: <c>{ context?, at? }</c> (context values by attribute id, the clock's start).</summary>
    public JsonElement? Start { get; init; }

    /// <summary>A scenario id: its start and steps run first (the request's <see cref="Start"/> is then ignored).</summary>
    public string? Scenario { get; init; }

    /// <summary>The inputs: scenario steps without <c>expect</c> (an <c>id</c> is optional).</summary>
    public IReadOnlyList<JsonElement>? Steps { get; init; }

    /// <summary>The first trace index returned; -1 (the default) includes the initial entry.</summary>
    public int? From { get; init; }
}

/// <summary>The body of <c>POST /api/processes/{id}/scenarios</c>.</summary>
public sealed record RecordScenarioRequest
{
    /// <summary>The scenario's name (an identifier).</summary>
    public string? Name { get; init; }

    /// <summary>The start: <c>{ context?, at? }</c>.</summary>
    public JsonElement? Start { get; init; }

    /// <summary>The inputs (scenario steps; an <c>expect</c> given is replaced by the replay's).</summary>
    public IReadOnlyList<JsonElement>? Steps { get; init; }

    /// <summary><c>final</c> or <c>active</c>; the replay's outcome when absent.</summary>
    public string? Outcome { get; init; }
}

/// <summary>The body of <c>POST /api/processes/import</c>.</summary>
public sealed record ProcessImportRequest
{
    /// <summary>The XState machine config: a JSON object, or a string holding one.</summary>
    public JsonElement? Config { get; init; }

    /// <summary>The package (id or name) of a new process.</summary>
    public string? Package { get; init; }

    /// <summary>The name of a new process (the config's <c>id</c> when absent).</summary>
    public string? Name { get; init; }

    /// <summary><c>lifecycle</c> or <c>orchestration</c>.</summary>
    public string? Use { get; init; }

    /// <summary>The subject entity (id or name).</summary>
    public string? Subject { get; init; }

    /// <summary>A process (id or name) to re-import over, keeping its ids.</summary>
    public string? Into { get; init; }

    /// <summary>The hash of <see cref="Into"/> as the caller read it.</summary>
    public string? ExpectedHash { get; init; }
}

/// <summary>A trigger the configuration accepts now.</summary>
/// <param name="Trigger"><c>event</c>, <c>invoke-done</c>, <c>invoke-error</c> or <c>time</c>.</param>
/// <param name="Event">The event id.</param>
/// <param name="Invoke">The invoke id.</param>
/// <param name="Transitions">The candidate transitions, in selection order.</param>
/// <param name="Actors">The actors the input must come from (empty: any).</param>
/// <param name="GuardUnknown">Whether a candidate's guard has no expression (the input needs <c>assume</c>).</param>
/// <param name="Gate">The gate's signatures so far and the count it needs, for a gated event.</param>
public sealed record EnabledTrigger(string Trigger, string? Event, string? Invoke, IReadOnlyList<string> Transitions, IReadOnlyList<string> Actors, bool GuardUnknown,
    GateProgress? Gate);

/// <summary>A gate's progress.</summary>
/// <param name="Have">The signatures collected.</param>
/// <param name="Need">The signatures required.</param>
public sealed record GateProgress(int Have, int Need);

/// <summary>A pending service or human task.</summary>
/// <param name="Invoke">The invoke id.</param>
/// <param name="State">The state that started it.</param>
public sealed record PendingInvoke(string Invoke, string State);

/// <summary>A scheduled timer.</summary>
/// <param name="Transition">The delayed transition.</param>
/// <param name="DueAt">The simulated instant it fires (ISO 8601).</param>
public sealed record ScheduledTimer(string Transition, string DueAt);

/// <summary>One collected signature.</summary>
/// <param name="Signer">The signing person.</param>
/// <param name="Actor">The actor signed as.</param>
/// <param name="Meaning">The meaning id.</param>
/// <param name="Reason">The reason.</param>
public sealed record GateSignature(string Signer, string Actor, string? Meaning, string? Reason);

/// <summary>A gate of an active state.</summary>
/// <param name="Gate">The gate id.</param>
/// <param name="Transition">The gated transition.</param>
/// <param name="Signatures">The signatures collected.</param>
/// <param name="Satisfied">Whether the count and the required actors are covered.</param>
public sealed record GateState(string Gate, string Transition, IReadOnlyList<GateSignature> Signatures, bool Satisfied);

/// <summary>The response of <c>simulate</c> (phase-3-design.md section 4.4).</summary>
/// <param name="ProcessHash">The hash of the process document simulated (the draft's when one was sent).</param>
/// <param name="Trace">The traces from index <c>from</c> on (-1 is the initial entry).</param>
/// <param name="Configuration">The active atomic states, in document order.</param>
/// <param name="Context">The context by attribute id.</param>
/// <param name="Enabled">The triggers the configuration accepts.</param>
/// <param name="Pending">The pending service and human tasks.</param>
/// <param name="Timers">The scheduled timers, in due order.</param>
/// <param name="Gates">The gates of active states.</param>
/// <param name="Final">Whether the process reached a final state.</param>
/// <param name="Clock">The simulated clock (ISO 8601).</param>
/// <param name="Diagnostics">The findings of every input (MQ9305, MQ9306, MQ9502 to MQ9507), once each.</param>
public sealed record SimulationResult(string ProcessHash, IReadOnlyList<StepTrace> Trace, IReadOnlyList<string> Configuration, IReadOnlyDictionary<string, JsonElement> Context,
    IReadOnlyList<EnabledTrigger> Enabled, IReadOnlyList<PendingInvoke> Pending, IReadOnlyList<ScheduledTimer> Timers, IReadOnlyList<GateState> Gates, bool Final,
    string Clock, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One scenario's verification.</summary>
/// <param name="Scenario">The scenario id.</param>
/// <param name="Name">The scenario name.</param>
/// <param name="Passed">Whether it ran to the end and every expectation held.</param>
/// <param name="Steps">The scenario's step count.</param>
/// <param name="Failure">The first failure.</param>
public sealed record ScenarioVerification(string Scenario, string Name, bool Passed, int Steps, ScenarioFailure? Failure);

/// <summary>The response of <c>verify</c>.</summary>
/// <param name="Process">The process id.</param>
/// <param name="Results">One per scenario, by name then id.</param>
public sealed record VerifyResult(string Process, IReadOnlyList<ScenarioVerification> Results)
{
    /// <summary>Whether every scenario passed.</summary>
    public bool Passed => Results.All(r => r.Passed);
}

/// <summary>The response of <c>scenarios</c> (record).</summary>
/// <param name="Id">The scenario id.</param>
/// <param name="Element">The scenario document (canonical), as written or, for a preview, as it would be.</param>
/// <param name="Hash">The new file's hash; null for a preview.</param>
/// <param name="Applied">Whether it was written.</param>
/// <param name="Diagnostics">The replay's and the save's findings.</param>
public sealed record RecordScenarioResult(string Id, JsonElement Element, string? Hash, bool Applied, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The response of <c>import</c>.</summary>
/// <param name="Document">The process document the import gives (canonical), or null when the config cannot be read.</param>
/// <param name="Diagnostics">MQ9401 to MQ9406, and the save's findings.</param>
/// <param name="Created">Ids the import created.</param>
/// <param name="Removed">Ids of <c>into</c> the import drops.</param>
/// <param name="Applied">Whether the document was saved.</param>
/// <param name="Id">The process id.</param>
/// <param name="Hash">The saved file's hash.</param>
public sealed record ProcessImportResult(JsonElement? Document, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Created, IReadOnlyList<string> Removed,
    bool Applied, string? Id, string? Hash);

/// <summary>The response of <c>sync-enum</c>.</summary>
/// <param name="Process">The process id.</param>
/// <param name="Enum">The bound enum id.</param>
/// <param name="Added">Member names added.</param>
/// <param name="Removed">Member names removed.</param>
/// <param name="Reordered">Whether the order changes.</param>
/// <param name="Refused">Removals refused because the member is still used.</param>
/// <param name="Applied">Whether the enum was written.</param>
/// <param name="Diagnostics">MQ9019 when the sync is refused.</param>
public sealed record SyncEnumResult(string Process, string? Enum, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool Reordered,
    IReadOnlyList<SyncEnumRefusal> Refused, bool Applied, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// The engine side of the process operations (phase-3-design.md section 4.4) that the API, the MCP tools and the CLI verbs share:
/// resolving a process by id, name or path, reading inputs, simulating and verifying. Simulation state lives in the caller: every
/// call replays its inputs from the start, so one input list always gives one trace.
/// </summary>
public static class ProcessApi
{
    /// <summary>The processes a key names: an id, else a name (exact, then ignoring case), else a model path.</summary>
    /// <param name="model">The model.</param>
    /// <param name="key">The key.</param>
    /// <returns>The matches, by id.</returns>
    public static IReadOnlyList<Process> Resolve(ModelSnapshot model, string key)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(key);
        var all = model.All<Process>();
        if (model.Get<Process>(key) is { } byId)
            return [byId];
        var matches = all.Where(p => string.Equals(p.Name, key, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
            matches = [.. all.Where(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))];
        if (matches.Count == 0)
        {
            var path = key.Replace('\\', '/');
            matches = [.. all.Where(p => model.GetDocument(p.Id) is { } d && (string.Equals(d.Path, path, StringComparison.Ordinal)
                || d.Path.EndsWith("/" + path.TrimStart('.', '/'), StringComparison.Ordinal)))];
        }

        return [.. matches.OrderBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>Reads simulation inputs: each a scenario step whose <c>id</c> is optional and whose <c>expect</c> is ignored.</summary>
    /// <param name="steps">The steps.</param>
    /// <param name="error">Why a step cannot be read.</param>
    /// <returns>The steps, or null.</returns>
    public static IReadOnlyList<ScenarioStep>? ReadInputs(IReadOnlyList<JsonElement>? steps, out string? error) => ReadInputs(steps, null, null, out error);

    /// <summary>Reads simulation inputs, resolving event, actor, invoke and guard (assume) names to ids as <see cref="ResolveNames"/> does.</summary>
    /// <param name="steps">The steps.</param>
    /// <param name="process">The process whose names resolve, or null.</param>
    /// <param name="snapshot">The model whose actor names resolve, or null.</param>
    /// <param name="error">Why they cannot be read.</param>
    /// <returns>The inputs, or null.</returns>
    public static IReadOnlyList<ScenarioStep>? ReadInputs(IReadOnlyList<JsonElement>? steps, Process? process, ModelSnapshot? snapshot, out string? error)
    {
        error = null;
        var result = new List<ScenarioStep>();
        for (var i = 0; i < (steps?.Count ?? 0); i++)
        {
            var element = steps![i];
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"steps[{i}] must be an object (a scenario step without expect).";
                return null;
            }

            var node = JsonNode.Parse(element.GetRawText())!.AsObject();
            node.Remove("expect");
            if (process is not null)
                ResolveNames(node, process, snapshot);
            if (node["id"] is null)
                node["id"] = "input-" + i.ToString(CultureInfo.InvariantCulture);
            try
            {
                result.Add(node.Deserialize<ScenarioStep>(EngineJson.Options)!);
            }
            catch (JsonException e)
            {
                error = $"steps[{i}] is not a scenario step: {e.Message}";
                return null;
            }
        }

        return result;
    }

    /// <summary>
    /// Replaces names with ids in one input step: <c>event</c>, <c>invoke</c> and the keys of <c>assume</c> (guards) by the process's
    /// names, <c>actor</c> by the model's actor names. A value that is already an id, or that no single element is named, is left as is
    /// (an unknown one is then reported as usual).
    /// </summary>
    /// <param name="step">The step, changed in place.</param>
    /// <param name="process">The process.</param>
    /// <param name="snapshot">The model, or null to leave actors as given.</param>
    public static void ResolveNames(JsonObject step, Process process, ModelSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(process);
        Replace("event", process.Events.Select(e => (e.Id, e.Name)));
        Replace("invoke", StatechartModel.Get(process, null).Invokes.Values.Select(i => (i.Invoke.Id, i.Invoke.Name)));
        if (snapshot is not null)
            Replace("actor", snapshot.All<Actor>().Select(a => (a.Id, a.Name)));
        if (step["assume"] is JsonObject assume)
        {
            var guards = process.Guards.Select(g => (g.Id, g.Name)).ToList();
            foreach (var key in assume.Select(p => p.Key).ToList())
            {
                if (Find(guards, key) is { } id && !assume.ContainsKey(id))
                {
                    var value = assume[key];
                    assume.Remove(key);
                    assume[id] = value;
                }
            }
        }

        void Replace(string field, IEnumerable<(string Id, string Name)> elements)
        {
            if (step[field] is JsonValue value && value.TryGetValue<string>(out var text) && Find(elements.ToList(), text) is { } id)
                step[field] = id;
        }

        static string? Find(List<(string Id, string Name)> elements, string text)
        {
            if (elements.Any(e => string.Equals(e.Id, text, StringComparison.Ordinal)))
                return null;
            var named = elements.Where(e => string.Equals(e.Name, text, StringComparison.Ordinal)).Take(2).ToList();
            return named.Count == 1 ? named[0].Id : null;
        }
    }

    /// <summary>Parses a start instant (<c>start.at</c>) as an ISO 8601 date-time, UTC when no offset is given.</summary>
    /// <param name="text">The text.</param>
    /// <param name="at">The instant.</param>
    /// <returns>Whether the text is a date-time.</returns>
    public static bool TryParseAt(string text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);

    /// <summary>Reads a start object <c>{ context?, at? }</c>.</summary>
    /// <param name="start">The start, or null.</param>
    /// <param name="error">Why it cannot be read.</param>
    /// <returns>The start (null when absent).</returns>
    public static ScenarioStart? ReadStart(JsonElement? start, out string? error)
    {
        error = null;
        if (start is not { } s || s.ValueKind == JsonValueKind.Null)
            return null;
        if (s.ValueKind != JsonValueKind.Object)
        {
            error = "start must be an object: { context?, at? }.";
            return null;
        }

        try
        {
            var read = s.Deserialize<ScenarioStart>(EngineJson.Options);
            if (read?.At is { } at && !TryParseAt(at, out _))
            {
                error = $"start.at '{at}' is not an ISO 8601 date-time; give one such as 2026-01-01T09:00:00Z, or leave it out.";
                return null;
            }

            return read;
        }
        catch (JsonException e)
        {
            error = "start is not valid: " + e.Message;
            return null;
        }
    }

    /// <summary>Runs a start and inputs through the interpreter and describes where the process stands.</summary>
    /// <param name="chart">The chart.</param>
    /// <param name="processHash">The process document's hash.</param>
    /// <param name="runtime">The run's runtime (over the model, or the candidate model of a draft).</param>
    /// <param name="start">The start.</param>
    /// <param name="inputs">The inputs.</param>
    /// <param name="from">The first trace index returned.</param>
    /// <returns>The result.</returns>
    public static SimulationResult Simulate(StatechartModel chart, string processHash, ProcessRuntime runtime, ScenarioStart? start, IReadOnlyList<ScenarioStep> inputs, int from = -1)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(inputs);
        var interpreter = new StatechartInterpreter(chart, runtime.Options("simulation"));
        DateTimeOffset? at = null;
        if (start?.At is { } text && TryParseAt(text, out var parsed))
            at = parsed;
        var traces = new List<StepTrace> { interpreter.Start(start?.Context, at) };
        var complete = !traces[0].Incomplete;
        for (var i = 0; complete && i < inputs.Count; i++)
        {
            var trace = interpreter.Step(inputs[i]);
            traces.Add(trace);
            complete = !trace.Incomplete;
        }

        var diagnostics = new List<Diagnostic>();
        var seen = new HashSet<(string, string)>();
        foreach (var d in traces.SelectMany(t => t.Diagnostics))
        {
            if (seen.Add((d.Rule, d.Message)))
                diagnostics.Add(d);
        }

        var enabled = interpreter.IsFinal ? [] : Enabled(chart, interpreter);
        var signatures = interpreter.Signatures;
        var gates = new List<GateState>();
        foreach (var t in ActiveTransitions(chart, interpreter).Where(t => t.Gate is not null && t.Trigger == TransitionTrigger.Event))
        {
            var gate = t.Gate!;
            var signed = signatures.TryGetValue(t.Id, out var list) ? list : [];
            var satisfied = signed.Count >= Math.Max(1, gate.Required) && gate.RequiredActors.All(a => signed.Any(s => string.Equals(s.Actor, a, StringComparison.Ordinal)));
            gates.Add(new GateState(gate.Id, t.Id, [.. signed.Select(s => new GateSignature(s.Signer, s.Actor, s.Meaning, s.Reason))], satisfied));
        }

        return new SimulationResult(processHash, [.. traces.Where(t => t.Index >= from)], interpreter.Configuration, interpreter.Context, enabled,
            [.. interpreter.Pending.Select(p => new PendingInvoke(p.Invoke, p.State))],
            [.. interpreter.Timers.Select(t => new ScheduledTimer(t.Transition, Iso(t.DueAt)))], gates, interpreter.IsFinal, traces[^1].Clock, diagnostics);
    }

    /// <summary>Verifies scenarios of a process against the model.</summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <param name="scenarioIds">The scenarios to verify; every scenario of the process when null.</param>
    /// <param name="jobs">The parallelism.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The results, or null with the id of a scenario that is not the process's.</returns>
    public static (VerifyResult? Result, string? Unknown) Verify(ModelSnapshot model, Process process, IReadOnlyList<string>? scenarioIds, int jobs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        var own = model.All<Scenario>().Where(s => string.Equals(s.Process, process.Id, StringComparison.Ordinal)).ToList();
        List<Scenario> selected;
        if (scenarioIds is { Count: > 0 })
        {
            selected = [];
            foreach (var id in scenarioIds)
            {
                if (own.FirstOrDefault(s => s.Id == id || string.Equals(s.Name, id, StringComparison.Ordinal)) is not { } s)
                    return (null, id);
                if (!selected.Contains(s))
                    selected.Add(s);
            }
        }
        else
        {
            selected = own;
        }

        selected = [.. selected.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal)];
        var results = new ScenarioVerification[selected.Count];
        using (var runtime = new ProcessRuntime(model, Math.Max(1, jobs), ct))
        {
            Parallel.For(0, selected.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs), CancellationToken = ct }, i =>
            {
                var scenario = selected[i];
                var replay = ScenarioReplayer.Replay(scenario, runtime);
                results[i] = new ScenarioVerification(scenario.Id, scenario.Name, replay?.Passed ?? false, scenario.Steps.Count, replay is null ? null : FailureOf(replay));
            });
        }

        return (new VerifyResult(process.Id, results), null);
    }

    /// <summary>The first failure of a replay: its failed expectation, else its first error (a stop at MQ9305, MQ9306 or MQ9507).</summary>
    /// <param name="replay">The replay.</param>
    /// <returns>The failure, or null when it passed.</returns>
    public static ScenarioFailure? FailureOf(ScenarioReplay replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (replay.Failure is { } failure)
            return failure;
        if (replay.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is not { } error)
            return null;
        var step = -1;
        if (error.JsonPointer is { } pointer && pointer.StartsWith("/steps/", StringComparison.Ordinal))
        {
            var digits = pointer["/steps/".Length..].Split('/')[0];
            _ = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out step);
        }

        var nothing = JsonSerializer.SerializeToElement<object?>(null);
        return new ScenarioFailure(step, error.Rule, error.Message, nothing, nothing);
    }

    /// <summary>The <c>expect</c> object of a recorded step (canonical: <c>accepted</c> only when false).</summary>
    /// <param name="expect">The observed expectation.</param>
    /// <returns>The node.</returns>
    public static JsonObject ExpectNode(StepExpectation expect)
    {
        ArgumentNullException.ThrowIfNull(expect);
        var node = new JsonObject();
        if (!expect.Accepted)
            node["accepted"] = false;
        node["states"] = new JsonArray([.. expect.States.Select(s => (JsonNode?)JsonValue.Create(s))]);
        if (expect.Context.Count > 0)
        {
            var context = new JsonObject();
            foreach (var (id, value) in expect.Context.OrderBy(p => p.Key, StringComparer.Ordinal))
                context[id] = JsonNode.Parse(value.GetRawText());
            node["context"] = context;
        }

        return node;
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    // The transitions reachable from the active atomic states: each state and its ancestors, in selection order (state first, then
    // ancestors; priority order within a state).
    private static List<ChartTransition> ActiveTransitions(StatechartModel chart, StatechartInterpreter interpreter)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ChartTransition>();
        foreach (var id in interpreter.Configuration)
        {
            if (!chart.ById.TryGetValue(id, out var leaf))
                continue;
            foreach (var state in new[] { leaf }.Concat(leaf.Ancestors.Reverse()))
            {
                foreach (var t in state.Transitions.Concat(state.Timers))
                {
                    if (seen.Add(t.Id))
                        result.Add(t);
                }
            }
        }

        return result;
    }

    private static List<EnabledTrigger> Enabled(StatechartModel chart, StatechartInterpreter interpreter)
    {
        var compiled = ProcessExpressions.Get(chart.Process).Compiled;
        var active = ActiveTransitions(chart, interpreter);
        var signatures = interpreter.Signatures;
        bool Unknown(ChartTransition t) => t.Guard is { } g && !compiled.Contains(g.Id);
        var result = new List<EnabledTrigger>();
        foreach (var ev in chart.Process.Events)
        {
            var candidates = active.Where(t => t.Trigger == TransitionTrigger.Event && string.Equals(t.Transition.Event, ev.Id, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0)
                continue;
            GateProgress? gate = null;
            IReadOnlyList<string> actors = ev.Actors;
            if (candidates.FirstOrDefault(t => t.Gate is not null) is { } gated)
            {
                var have = signatures.TryGetValue(gated.Id, out var list) ? list.Count : 0;
                gate = new GateProgress(have, Math.Max(1, gated.Gate!.Required));
                if (actors.Count == 0)
                    actors = gated.Gate.Signers;
            }

            result.Add(new EnabledTrigger("event", ev.Id, null, [.. candidates.Select(t => t.Id)], actors, candidates.Any(Unknown), gate));
        }

        foreach (var (invokeId, stateId) in interpreter.Pending)
        {
            if (!chart.Invokes.TryGetValue(invokeId, out var found))
                continue;
            foreach (var (trigger, name) in new[] { (TransitionTrigger.InvokeDone, "invoke-done"), (TransitionTrigger.InvokeError, "invoke-error") })
            {
                var candidates = active.Where(t => t.Trigger == trigger && string.Equals(t.Transition.Invoke, invokeId, StringComparison.Ordinal)).ToList();
                result.Add(new EnabledTrigger(name, null, invokeId, [.. candidates.Select(t => t.Id)], found.Invoke.Type == InvokeType.HumanTask ? found.Invoke.Actors : [],
                    candidates.Any(Unknown), null));
            }

            _ = stateId;
        }

        var timers = interpreter.Timers;
        if (timers.Count > 0)
            result.Add(new EnabledTrigger("time", null, null, [.. timers.Select(t => t.Transition)], [], active.Where(t => timers.Any(x => x.Transition == t.Id)).Any(Unknown), null));
        return result;
    }
}
