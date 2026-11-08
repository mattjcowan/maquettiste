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

    /// <summary>The run's objects, as a scenario's <c>objects</c> (lifecycle-design.md decision 22); with <see cref="Scenario"/>, that
    /// scenario's objects when absent. Requires <see cref="Subject"/>.</summary>
    public IReadOnlyList<JsonElement>? Objects { get; init; }

    /// <summary>The id of the object in <see cref="Objects"/> the process runs for; with <see cref="Scenario"/> and no objects, the
    /// scenario's subject when absent.</summary>
    public string? Subject { get; init; }

    /// <summary>The run's participants, as a scenario's <c>participants</c> (actors-design.md decision 14); with <see cref="Scenario"/>,
    /// that scenario's participants when absent.</summary>
    public IReadOnlyList<JsonElement>? Participants { get; init; }

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

    /// <summary>The scenario's objects (lifecycle-design.md decision 22), written to its <c>objects</c>. Requires <see cref="Subject"/>.</summary>
    public IReadOnlyList<JsonElement>? Objects { get; init; }

    /// <summary>The id of the object in <see cref="Objects"/> the process runs for.</summary>
    public string? Subject { get; init; }

    /// <summary>The scenario's participants (actors-design.md decision 14), written to its <c>participants</c>.</summary>
    public IReadOnlyList<JsonElement>? Participants { get; init; }

    /// <summary>The inputs (scenario steps; an <c>expect</c> given is replaced by the replay's).</summary>
    public IReadOnlyList<JsonElement>? Steps { get; init; }

    /// <summary><c>final</c> or <c>active</c>; the replay's outcome when absent.</summary>
    public string? Outcome { get; init; }

    /// <summary>
    /// Whether to also write each step's <c>expect.triggerPoints</c> (trigger-points-design.md decision 28); <see langword="null"/> or
    /// <see langword="false"/> leaves it out, so a new trigger point does not break the scenario.
    /// </summary>
    public bool? TriggerPoints { get; init; }
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
    GateProgress? Gate)
{
    /// <summary>
    /// The ids of the checks the team writes that the candidates reach (trigger-points-design.md section 9), whose outcome the input takes
    /// from <c>assume</c>; <see langword="null"/>, and left out of the JSON, when there are none.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ChecksUnknown { get; init; }
}

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
/// <param name="Warnings">The replay's warnings on the scenario, such as MQ9309 for a step refused as frozen after an earlier step froze
/// the subject or a link (rights-design.md decision 16), which a scenario that passes still has; <see langword="null"/>, and left out of the
/// JSON, when there are none.</param>
public sealed record ScenarioVerification(string Scenario, string Name, bool Passed, int Steps, ScenarioFailure? Failure,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Diagnostic>? Warnings = null);

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

/// <summary>A state's code after <c>sync-codes</c> (lifecycle-design.md decision 19).</summary>
/// <param name="State">The state id.</param>
/// <param name="Path">The state's dotted name path from the root.</param>
/// <param name="Code">The code, a string or an integer as written.</param>
/// <param name="Source"><c>kept</c> (the state had it), <c>given</c> (from <c>stateCodes</c>), <c>name</c> (a code equal to the state's
/// name, case, <c>_</c> and <c>-</c> ignored) or <c>label</c> (a row or member whose label equals the state's display name or name).</param>
public sealed record SyncCodesState(string State, string Path, JsonElement Code, string Source);

/// <summary>A state that needs a code and gets none from <c>sync-codes</c>.</summary>
/// <param name="State">The state id.</param>
/// <param name="Path">The state's dotted name path from the root.</param>
/// <param name="Candidates">The codes it matched more than one of, or that another state keeps or takes; empty when nothing matched.</param>
public sealed record SyncCodesUnmatched(string State, string Path, IReadOnlyList<JsonElement> Candidates);

/// <summary>The response of <c>sync-codes</c>.</summary>
/// <param name="Process">The process id.</param>
/// <param name="Entity">The subject.</param>
/// <param name="Attribute">The attribute that holds the state.</param>
/// <param name="Binds">Whether the sync writes the binding.</param>
/// <param name="Codes">Every state that needs a code and has one after the sync, with each state <c>stateCodes</c> names, in document order.</param>
/// <param name="Unmatched">Every state that needs a code and gets none, in document order.</param>
/// <param name="Applied">Whether the process was written.</param>
/// <param name="Diagnostics">MQ9019 when the sync is refused, or the findings that refuse the save.</param>
public sealed record SyncCodesResult(string Process, string Entity, string Attribute, bool Binds, IReadOnlyList<SyncCodesState> Codes,
    IReadOnlyList<SyncCodesUnmatched> Unmatched, bool Applied, IReadOnlyList<Diagnostic> Diagnostics);

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
            var guards = (snapshot is null ? process : ProcessLibraries.Effective(snapshot, process)).Guards.Select(g => (g.Id, g.Name)).ToList();
            List<(string Id, string Name)>? checks = null;
            foreach (var key in assume.Select(p => p.Key).ToList())
            {
                // A word names a check (trigger-points-design.md decision 26): this lifecycle's by its name, another's as Lifecycle.Name or
                // LifecycleId.Name; a boolean names a guard, as before.
                var word = assume[key] is JsonValue v && v.TryGetValue<string>(out _);
                if (word)
                    checks ??= CheckNames(process, snapshot);
                if (Find(word ? checks! : guards, key) is { } id && !assume.ContainsKey(id))
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

        static List<(string Id, string Name)> CheckNames(Process process, ModelSnapshot? snapshot)
        {
            var names = new List<(string Id, string Name)>();
            IReadOnlyList<Process> lifecycles = snapshot is null ? [process] : TriggerSequences.Of(snapshot).Lifecycles;
            foreach (var lifecycle in lifecycles)
            {
                var own = string.Equals(lifecycle.Id, process.Id, StringComparison.Ordinal);
                foreach (var point in lifecycle.TriggerPoints.Where(p => p.Type == TriggerPointType.Check))
                {
                    if (own)
                        names.Add((point.Id, point.Name));
                    names.Add((point.Id, lifecycle.Name + "." + point.Name));
                    names.Add((point.Id, lifecycle.Id + "." + point.Name));
                }
            }

            // A named trigger point's check (part 2) by its qualified name; a name that a lifecycle's check also takes names two, so it stays.
            if (snapshot is not null)
            {
                foreach (var point in TriggerSequences.Of(snapshot).Named.Where(p => p.Type == TriggerPointType.Check))
                    names.Add((point.Id, NamedTriggerPoints.QualifiedName(snapshot, point)));
            }

            return names;
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

    /// <summary>
    /// Reads the objects and the subject of a simulation or a recording (lifecycle-design.md decision 22): each object as a scenario's,
    /// ids unique, each link's object one of them, and the subject one of them, required with objects and refused without.
    /// </summary>
    /// <param name="objects">The objects.</param>
    /// <param name="subject">The subject's id.</param>
    /// <param name="error">Why they cannot be read.</param>
    /// <returns>The objects (empty when none are given), or null.</returns>
    public static IReadOnlyList<ScenarioObject>? ReadObjects(IReadOnlyList<JsonElement>? objects, string? subject, out string? error)
    {
        error = null;
        var result = new List<ScenarioObject>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < (objects?.Count ?? 0); i++)
        {
            var element = objects![i];
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"objects[{i}] must be an object: {{ id, name, entity, values?, links? }}.";
                return null;
            }

            try
            {
                var obj = element.Deserialize<ScenarioObject>(EngineJson.Options)!;
                if (!ids.Add(obj.Id))
                {
                    error = $"objects[{i}] has the id {obj.Id}, which another object has; give each object its own id.";
                    return null;
                }

                result.Add(obj);
            }
            catch (JsonException e)
            {
                error = $"objects[{i}] is not a scenario object: {e.Message}";
                return null;
            }
        }

        foreach (var link in result.SelectMany(o => o.Links))
        {
            if (!ids.Contains(link.Object))
            {
                error = $"A link names the object {link.Object}, which is not one of the objects; link to one of them.";
                return null;
            }
        }

        if (result.Count > 0 && subject is null)
            error = "subject is required with objects: give the id of the object the process runs for.";
        else if (subject is not null && !ids.Contains(subject))
            error = result.Count == 0 ? "subject names an object, but there are no objects; give objects, or leave subject out." : $"subject {subject} is not one of the objects; give the id of one of them.";
        return error is null ? result : null;
    }

    /// <summary>
    /// Reads the participants of a simulation or a recording (actors-design.md decision 14): each as a scenario's, ids unique among them
    /// and their holdings, names unique, each <c>within</c> and <c>object</c> one of the objects, each object's <c>owner</c> one of them.
    /// A step's <c>participant</c> may give a participant's name, which becomes its id; it must then name one of them.
    /// </summary>
    /// <param name="participants">The participants.</param>
    /// <param name="objects">The run's objects.</param>
    /// <param name="steps">The steps, whose participant names are replaced by ids; or null.</param>
    /// <param name="error">Why they cannot be read.</param>
    /// <returns>The participants (empty when none are given), or null.</returns>
    public static IReadOnlyList<ScenarioParticipant>? ReadParticipants(IReadOnlyList<JsonElement>? participants, IReadOnlyList<ScenarioObject> objects,
        IList<ScenarioStep>? steps, out string? error)
    {
        ArgumentNullException.ThrowIfNull(objects);
        error = null;
        var result = new List<ScenarioParticipant>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var objectIds = new HashSet<string>(objects.Select(o => o.Id), StringComparer.Ordinal);
        for (var i = 0; i < (participants?.Count ?? 0); i++)
        {
            ScenarioParticipant? participant;
            try
            {
                participant = participants![i].Deserialize<ScenarioParticipant>(EngineJson.Options);
            }
            catch (JsonException e)
            {
                error = $"participants[{i}] is not a participant: {e.Message}";
                return null;
            }

            if (participant is null || string.IsNullOrEmpty(participant.Id) || string.IsNullOrEmpty(participant.Name))
            {
                error = $"participants[{i}] needs an id and a name.";
                return null;
            }

            // "holds": null reads as no holdings.
            if (participant.Holds is null)
                participant = participant with { Holds = [] };
            if (!ids.Add(participant.Id) || participant.Holds.Any(h => !ids.Add(h.Id)))
            {
                error = $"participants[{i}] repeats an id another participant or holding has; give each its own id.";
                return null;
            }

            if (!names.Add(participant.Name))
            {
                error = $"participants[{i}] has the name {participant.Name}, which another participant has; give each participant its own name.";
                return null;
            }

            if (participant.Holds.Select(h => h.Within).Append(participant.Object).OfType<string>().FirstOrDefault(o => !objectIds.Contains(o)) is { } missing)
            {
                error = $"Participant {participant.Name} names the object {missing}, which is not one of the objects; name one of them.";
                return null;
            }

            result.Add(participant);
        }

        if (objects.FirstOrDefault(o => o.Owner is { } owner && !ids.Contains(owner)) is { } owned)
        {
            error = $"Object {owned.Name} names the owner {owned.Owner}, which is not one of the participants; name one of them.";
            return null;
        }

        for (var i = 0; i < (steps?.Count ?? 0); i++)
        {
            if (steps![i].Participant is not { } named)
                continue;
            var id = result.FirstOrDefault(p => string.Equals(p.Id, named, StringComparison.Ordinal))?.Id
                ?? result.FirstOrDefault(p => string.Equals(p.Name, named, StringComparison.Ordinal))?.Id;
            if (id is null)
            {
                error = $"steps[{i}] names the participant {named}, which is not one of the participants; name one of them.";
                return null;
            }

            steps[i] = steps[i] with { Participant = id };
        }

        return result;
    }

    /// <summary>Runs a start and inputs through the interpreter and describes where the process stands.</summary>
    /// <param name="chart">The chart.</param>
    /// <param name="processHash">The process document's hash.</param>
    /// <param name="runtime">The run's runtime (over the model, or the candidate model of a draft).</param>
    /// <param name="start">The start.</param>
    /// <param name="inputs">The inputs.</param>
    /// <param name="from">The first trace index returned.</param>
    /// <param name="objects">The run's objects (lifecycle-design.md decision 22); none when absent.</param>
    /// <param name="subject">The id of the object among them the process runs for.</param>
    /// <param name="participants">The run's participants (actors-design.md decision 14); none when absent. A step with a participant
    /// runs as the actor it acts as (the fixed rule of decision 15), and its trace echoes that actor and the signer.</param>
    /// <returns>The result.</returns>
    public static SimulationResult Simulate(StatechartModel chart, string processHash, ProcessRuntime runtime, ScenarioStart? start, IReadOnlyList<ScenarioStep> inputs, int from = -1,
        IReadOnlyList<ScenarioObject>? objects = null, string? subject = null, IReadOnlyList<ScenarioParticipant>? participants = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(inputs);
        var row = ScenarioObjects.Row(runtime.Model, chart.Process, objects ?? [], subject, out var notes);
        var cast = new ScenarioCast(objects ?? [], subject, participants ?? []);
        var interpreter = new StatechartInterpreter(chart, runtime.Options("simulation") with
        {
            Subject = row,
            StateNotes = notes.Count > 0 ? notes : null,
            Rights = new RunRights(runtime, chart.Process, cast),
            Triggers = RunTriggers.For(runtime, chart.Process, cast),
        });
        DateTimeOffset? at = null;
        if (start?.At is { } text && TryParseAt(text, out var parsed))
            at = parsed;
        var traces = new List<StepTrace> { interpreter.Start(start?.Context, at) };
        var complete = !traces[0].Incomplete;
        for (var i = 0; complete && i < inputs.Count; i++)
        {
            var trace = Participants.Run(runtime.Model, interpreter, chart.Process, Participants.Current(cast, interpreter.Values), inputs[i], out _);
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

        var membership = ActorMembership.Of(runtime.Model);
        var enabled = interpreter.IsFinal ? [] : Enabled(chart, interpreter, ProcessExpressions.Get(runtime.Model, chart.Process).Compiled, runtime.Model, membership);
        var signatures = interpreter.Signatures;
        var gates = new List<GateState>();
        foreach (var t in ActiveTransitions(chart, interpreter).Where(t => t.Gate is not null && t.Trigger == TransitionTrigger.Event))
        {
            var gate = t.Gate!;
            var signed = signatures.TryGetValue(t.Id, out var list) ? list : [];
            var satisfied = signed.Count >= Math.Max(1, gate.Required) && gate.RequiredActors.All(a => signed.Any(s => membership.Accepts(a, s.Actor)));
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
                results[i] = new ScenarioVerification(scenario.Id, scenario.Name, replay?.Passed ?? false, scenario.Steps.Count, replay is null ? null : FailureOf(replay),
                    replay is null ? null : WarningsOf(replay));
            });
        }

        return (new VerifyResult(process.Id, results), null);
    }

    /// <summary>
    /// The warnings verify answers for a replay, in the order it found them: MQ9309 only (a step refused as frozen that doesn't expect it),
    /// so a project that doesn't freeze gets the answers it got before freezing; <see langword="null"/> when there are none.
    /// </summary>
    /// <param name="replay">The replay.</param>
    /// <returns>The warnings, or null.</returns>
    public static IReadOnlyList<Diagnostic>? WarningsOf(ScenarioReplay replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var warnings = replay.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning && string.Equals(d.Rule, "MQ9309", StringComparison.Ordinal)).ToList();
        return warnings.Count > 0 ? warnings : null;
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
        // Why a refused step was refused, and what refused it (rights-design.md decision 26), when the replay records them.
        if (expect.Refusal is { } refusal)
            node["refusal"] = refusal;
        if (expect.RefusedBy is { } refusedBy)
            node["refusedBy"] = refusedBy;
        node["states"] = new JsonArray([.. expect.States.Select(s => (JsonNode?)JsonValue.Create(s))]);
        if (expect.Context.Count > 0)
        {
            var context = new JsonObject();
            foreach (var (id, value) in expect.Context.OrderBy(p => p.Key, StringComparer.Ordinal))
                context[id] = JsonNode.Parse(value.GetRawText());
            node["context"] = context;
        }

        // The object's values the step set (object-effects-design.md decision 4), only when it set one.
        if (expect.Values.Count > 0)
        {
            var values = new JsonObject();
            foreach (var (id, value) in expect.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
                values[id] = JsonNode.Parse(value.GetRawText());
            node["values"] = values;
        }

        // The checks that warned, and on request the trigger points that ran (trigger-points-design.md decision 28), each only when not empty.
        if (expect.CheckWarnings is { Count: > 0 } warned)
            node["checkWarnings"] = new JsonArray([.. warned.Select(id => (JsonNode?)JsonValue.Create(id))]);
        if (expect.TriggerPoints is { Count: > 0 } ran)
            node["triggerPoints"] = new JsonArray([.. ran.Select(id => (JsonNode?)JsonValue.Create(id))]);
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

    // compiled: the guards that run, an expression's or a condition's; any other guard needs an assume value.
    private static List<EnabledTrigger> Enabled(StatechartModel chart, StatechartInterpreter interpreter, IReadOnlySet<string> compiled, ModelSnapshot model,
        ActorMembership membership)
    {
        // Each listed actor, then its members at any depth that the list does not hold, by (name, id): the Act-as list offers them
        // (actors-design.md decision 5). Without memberships, the list as the model gives it.
        IReadOnlyList<string> WithMembers(IReadOnlyList<string> listed)
        {
            if (!membership.Any || listed.Count == 0)
                return listed;
            var result = new List<string>(listed);
            var seen = new HashSet<string>(listed, StringComparer.Ordinal);
            foreach (var id in listed)
            {
                foreach (var member in membership.AllMembers(id).Select(m => (Id: m, Name: model.Get<Actor>(m)?.Name ?? m))
                    .OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Id, StringComparer.Ordinal))
                {
                    if (seen.Add(member.Id))
                        result.Add(member.Id);
                }
            }

            return result;
        }

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

            result.Add(new EnabledTrigger("event", ev.Id, null, [.. candidates.Select(t => t.Id)], WithMembers(actors), candidates.Any(Unknown), gate)
            {
                ChecksUnknown = ChecksUnknown(candidates),
            });
        }

        foreach (var (invokeId, stateId) in interpreter.Pending)
        {
            if (!chart.Invokes.TryGetValue(invokeId, out var found))
                continue;
            foreach (var (trigger, name) in new[] { (TransitionTrigger.InvokeDone, "invoke-done"), (TransitionTrigger.InvokeError, "invoke-error") })
            {
                var candidates = active.Where(t => t.Trigger == trigger && string.Equals(t.Transition.Invoke, invokeId, StringComparison.Ordinal)).ToList();
                result.Add(new EnabledTrigger(name, null, invokeId, [.. candidates.Select(t => t.Id)], found.Invoke.Type == InvokeType.HumanTask ? WithMembers(found.Invoke.Actors) : [],
                    candidates.Any(Unknown), null) { ChecksUnknown = ChecksUnknown(candidates) });
            }

            _ = stateId;
        }

        var timers = interpreter.Timers;
        if (timers.Count > 0)
        {
            var timed = active.Where(t => timers.Any(x => x.Transition == t.Id)).ToList();
            result.Add(new EnabledTrigger("time", null, null, [.. timers.Select(t => t.Transition)], [], timed.Any(Unknown), null) { ChecksUnknown = ChecksUnknown(timed) });
        }

        return result;

        // The team-code checks the candidates reach, left out when none (trigger-points-design.md section 9).
        IReadOnlyList<string>? ChecksUnknown(List<ChartTransition> candidates) =>
            interpreter.ChecksUnknown(candidates.Select(t => t.Id)) is { Count: > 0 } checks ? checks : null;
    }
}
