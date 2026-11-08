using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Processes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The process tools (phase-3-design.md section 4.4): one per process operation of the editor API, with its body. A process is named by
/// id, name or model path. Imports and enum syncs are dry runs unless <c>apply</c> is true.
/// </summary>
internal sealed partial class ModelTools
{
    /// <summary>Simulates a process from the start through inputs (simulateProcess).</summary>
    /// <param name="process">The process id, name or path.</param>
    /// <param name="steps">The inputs.</param>
    /// <param name="start">The start.</param>
    /// <param name="scenario">A scenario whose start and steps run first.</param>
    /// <param name="document">An unsaved draft of the process document.</param>
    /// <param name="from">The first trace index returned.</param>
    /// <param name="objects">The run's objects.</param>
    /// <param name="subject">The id of the object the process runs for.</param>
    /// <param name="participants">The run's participants.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The simulation.</returns>
    [McpServerTool(Name = "simulate_process", Title = "Simulate process", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Runs a process through the engine interpreter from its initial state: start, then each input (a scenario step without expect: { input: event|time|invoke-done|invoke-error|verb, event, participant, actor, signer, response, signature, meaning, reason, payload, assume, invoke, after, verb, values, attributes, end, object }; on an event gated by an approval (a gate with stages of named signatures), response answers it: sign (the default), reject (a veto: it clears every signature of the approval, starts it again and moves nothing), ignore (abstain: it finishes the person on that signature and leaves the count) or withdraw (it takes back the person's latest signature), and signature names the named signature it answers, else the engine picks one the same way every time; a response other than sign moves nothing, and a gate without stages takes sign only; a verb step has a participant do verb to the subject, or with end and object to one of its links: values: { <attribute id>: value } for modify on the subject, attributes: [<attribute id>] the attributes a read reads, each checked against the rights that name attributes, end a relation end id and object a scenario object id; values, attributes, end and object take ids, not names. freeze and thaw verb steps, and accepted events that name them, freeze or thaw the subject, or with a step's end and object its link; while frozen, modify, connect, disconnect and delete are refused before any right). Returns the trace of every input (transitions, exited and entered states, actions, each with the sets it ran as sets: { <attribute name>: new value }, guards, gate audit records (on an approval each names its signature and stage, and the outcomes add rejected, ignored and withdrawn), refusals (gate-excluded when a named signature's notBy keeps the person off it: they did a named verb or move since the object entered the gated move's source state; gate-not-signed when there is nothing to withdraw; frozen when the object or link is frozen and the verb is modify, connect, disconnect or delete: the first check, which no allow lifts, with no refusedBy), refusedBy: the id of the element that refused the step (in a project that uses rights or trigger points), rights: the rights that answered, each { right, lifecycle, access }, valuesChanged: the object's values the step set, by attribute name, and actor, the system actor when it made the move: the initial entry, a timer or a finished invoke that is not a human task, with no actor of its own; sets and valuesChanged only when something was set, frozen: true while the subject is frozen after the step), notes when there are any (a state of another lifecycle assumed, an identity current_actor() could not convert, sets in an invoked sub-process that changed nothing, a freeze of what is frozen or a thaw of what is not, which changes nothing, a move whose sets applied to a frozen object), then the configuration, context, the triggers enabled now (an actor list holds each listed actor, then its members at any depth), pending invokes, timers, gates (on an approval also stages, each with its signatures: needed, judged by its when when the approval started, counted, required, finished and complete; satisfied is then true only when nothing is needed, and the next occurrence moves), final and clock; each enabled trigger on an approval lists responses, the responses its event takes there, and its gate counts the needed signatures. In a lifecycle with trigger points (what runs when a verb happens: a check, an action with a timing of before, with-change or after-commit, or a replace of delete, disconnect, copy or revise; get_trigger_sequence gives their order), every input also runs them after rights, guards and signatures: before actions prepare the proposed values, then checks: a check that refuses (severity error) refuses the step with refusal check and refusedBy the check, and a move whose check refuses is left out as if its guard were false, so an automatic move is not taken and the step that led to it stays accepted; a warning check lets it through and lists it in checkWarnings. The trace then lists triggerPoints, the points that ran in run order, each { triggerPoint, lifecycle, verb, type, timing, outcome: passed, warned, refused, ran, replaced, queued (after commit) or skipped (its when was false), assumed, outside, sets }, and checkWarnings, each left out when empty. A when, a condition and a set value read the object as it will be through subject. and its values before the verb through old. (null on create). A step's assume maps a guard with no expression to true or false, as before, and a check the team writes in code (no condition) to pass, warning or error, keyed by the check's id (another lifecycle's check by Lifecycle.Name in answers); without an entry such a check passes, with assumed: true. A create verb step may give values, written to the subject when accepted. Each enabled trigger carries checksUnknown beside guardUnknown: the ids of the team-code checks its candidates reach, whose outcome assume gives; left out when empty. No state is kept: send the whole input list each time. document simulates an unsaved draft (invalid: 422 with diagnostics). objects and subject give the run its cast, as a scenario's: the process runs for the subject, actions' sets change the subject's values for later guards, sets and actor lookups, frozen: true starts the subject or one of its links frozen, and in_state reads an object's states (else the state its bound value maps to, else the lifecycle's initial state); with scenario and no objects, the scenario's cast. participants, as a scenario's, are who acts: a step's participant (id or name) acts, unless the step gives actor, as worked out from the moves the step can make now: for a gated move, a required actor of the gate not yet signed, else a signer the event's actors also accept, else the first signer it satisfies; otherwise the first of the event's or human task's actors it satisfies (its actor or a holding with no context, with their members; an actor from the object by the objects), or its actor when that list is empty. An event's signer defaults to its identity; the trace echoes the actor and signer used, and its actsAs names that actor.")]
    public Task<CallToolResult> SimulateProcess(
        [Description("The process id, name or model path; required.")] string? process = null,
        [Description("The inputs, in order (scenario steps without expect; ids are optional).")] JsonElement? steps = null,
        [Description("The start: { context: { <attribute id>: value }, at: ISO 8601 instant }.")] JsonElement? start = null,
        [Description("A scenario id of the process: its start and steps run first, then steps.")] string? scenario = null,
        [Description("An unsaved draft of the whole process document to simulate instead of the saved one.")] JsonElement? document = null,
        [Description("The first trace index returned; -1 (default) includes the initial entry.")] int? from = null,
        [Description("The run's objects, as a scenario's: [{ id, name, entity: <entity id>, values: { <attribute id>: value }, states?: [<state id of another lifecycle>], frozen?: true, links: [{ end: <relation end id>, object: <object id>, frozen?: true }] }]; requires subject. With scenario, its objects when absent.")] JsonElement? objects = null,
        [Description("The id of the object in objects the process runs for.")] string? subject = null,
        [Description("The run's participants, as a scenario's: [{ id, name, identity?, actor?: <person, external system or system actor id>, holds?: [{ id, actor: <role, group or organization id>, within?: <object id> }], object?: <object id> }]. With scenario, its participants when absent.")] JsonElement? participants = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (await ProcessIdAsync(process, ct).ConfigureAwait(false) is not { } id)
            return ProcessArgument(process);
        if (StepsOf(steps) is not { } inputs)
            return BadRequest("steps must be an array of scenario steps.");
        if (StepsOf(objects) is not { } cast)
            return BadRequest("objects must be an array of scenario objects.");
        if (StepsOf(participants) is not { } people)
            return BadRequest("participants must be an array of scenario participants.");
        var call = await _store.SimulateProcessAsync(id, new SimulateRequest
        {
            Steps = inputs, Start = start, Scenario = scenario, Document = document, From = from, Objects = cast, Subject = subject,
            Participants = people.Count > 0 ? people : null,
        }, ct).ConfigureAwait(false);
        return FromCall(call);
    }, ct);

    /// <summary>Records a scenario from inputs, its expectations filled by the replay (recordScenario).</summary>
    /// <param name="process">The process.</param>
    /// <param name="name">The scenario name.</param>
    /// <param name="steps">The inputs.</param>
    /// <param name="start">The start.</param>
    /// <param name="outcome">The expected outcome.</param>
    /// <param name="dryRun">Returns the scenario without writing it.</param>
    /// <param name="objects">The scenario's objects.</param>
    /// <param name="subject">The id of the object the process runs for.</param>
    /// <param name="participants">The scenario's participants.</param>
    /// <param name="triggerPoints">Also writes each step's <c>expect.triggerPoints</c> (trigger-points-design.md decision 28).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The scenario.</returns>
    [McpServerTool(Name = "record_scenario", Title = "Record scenario", Destructive = false, OpenWorld = false)]
    [Description("Records a scenario of a process: the engine replays start and steps and fills each step's expect (accepted, states, changed context, and values: the object's values the step set, by attribute name, only when it set one; on a step that answers an approval, signatures: the signatures counted after it on each of the approval's named signatures, by signature id; refusals as accepted false; a refused step's expect records refusal and refusedBy when the project uses rights or trigger points, and refusal frozen, with no refusedBy, in any project; in a project that uses trigger points it also records checkWarnings, the checks that warned, and with triggerPoints true triggerPoints, the trigger points that ran in run order, each only when not empty) and the outcome from the replay, gives new ids and saves the scenario under model/scenarios/<process>/. Each step's response and signature are written as given. A step may freeze or thaw (a freeze or thaw verb step, or an event that names the verb), and objects and links may start frozen. dryRun true returns the scenario without writing it. Inputs that cannot be replayed to the last step are refused with diagnostics. objects and subject are written as the scenario's cast, each object under a new id that subject and the links follow; participants likewise, each with its holdings under a new id that the steps' participant and the objects' owner follow (a step may name a participant by name).")]
    public Task<CallToolResult> RecordScenario(
        [Description("The process id, name or model path; required.")] string? process = null,
        [Description("The scenario name (an identifier); required.")] string? name = null,
        [Description("The inputs, in order (scenario steps; an expect given is replaced).")] JsonElement? steps = null,
        [Description("The start: { context, at }.")] JsonElement? start = null,
        [Description("final or active; the replay's outcome when absent.")] string? outcome = null,
        [Description("true returns the scenario without writing it.")] bool dryRun = false,
        [Description("The scenario's objects: [{ id, name, entity: <entity id>, values: { <attribute id>: value }, states?: [<state id of another lifecycle>], frozen?: true, links: [{ end: <relation end id>, object: <object id>, frozen?: true }] }]; requires subject.")] JsonElement? objects = null,
        [Description("The id of the object in objects the process runs for.")] string? subject = null,
        [Description("The scenario's participants: [{ id, name, identity?, actor?: <person, external system or system actor id>, holds?: [{ id, actor: <role, group or organization id>, within?: <object id> }], object?: <object id> }]; a step names one in participant.")] JsonElement? participants = null,
        [Description("true also writes each step's expect.triggerPoints, the ids of the trigger points that ran, in run order (a new trigger point then breaks the recording, so it is left out by default).")] bool triggerPoints = false,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (await ProcessIdAsync(process, ct).ConfigureAwait(false) is not { } id)
            return ProcessArgument(process);
        if (StepsOf(steps) is not { } inputs)
            return BadRequest("steps must be an array of scenario steps.");
        if (StepsOf(objects) is not { } cast)
            return BadRequest("objects must be an array of scenario objects.");
        if (StepsOf(participants) is not { } people)
            return BadRequest("participants must be an array of scenario participants.");
        var call = await _store.RecordScenarioAsync(id, new RecordScenarioRequest
        {
            Name = name, Steps = inputs, Start = start, Outcome = outcome, Objects = cast, Subject = subject, Participants = people,
            TriggerPoints = triggerPoints ? true : null,
        }, dryRun, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromCall(call);
    }, ct);

    /// <summary>Replays a process's scenarios (verifyScenarios).</summary>
    /// <param name="process">The process.</param>
    /// <param name="scenarios">Scenario ids or names.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The results.</returns>
    [McpServerTool(Name = "verify_scenarios", Title = "Verify scenarios", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Replays the scenarios of a process (all, or the ids or names given) through the engine interpreter. Each result has passed, the step count and the first failure: { step, rule (MQ9301 accepted, MQ9302 states, MQ9303 context, MQ9307 values, MQ9304 outcome, MQ9308 when a refused step's refusal (check for a trigger point's check, gate-excluded or gate-not-signed at an approval) or refusedBy differs from its expect, MQ9311 when the signatures counted after a step differ from expect.signatures (only the listed signatures are compared), MQ9310 when its check warnings or the trigger points that ran differ from expect.checkWarnings or expect.triggerPoints (exactly those ids; trigger points in run order, repeats kept, skipped ones left out), or the stop MQ9305, MQ9306, MQ9507), message, expected, actual }; a refusal as frozen names the step that froze it. Each result may carry warnings, such as MQ9309 for a step that freezes and is later followed by modify, connect, disconnect or delete; a refusal by actor of a step with a participant names the participant and what it failed (the list it satisfies none of, or the list that does not accept the actor it acted as). A step's assume words (pass, warning, error) give the outcome of checks the team writes in code. passed at the top is true when every scenario passed.")]
    public Task<CallToolResult> VerifyScenarios(
        [Description("The process id, name or model path; required.")] string? process = null,
        [Description("Scenario ids or names of the process; all of them when absent.")] string[]? scenarios = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (await ProcessIdAsync(process, ct).ConfigureAwait(false) is not { } id)
            return ProcessArgument(process);
        return FromCall(await _store.VerifyScenariosAsync(id, scenarios, ct).ConfigureAwait(false));
    }, ct);

    /// <summary>Exports a process as an XState config (exportProcess).</summary>
    /// <param name="process">The process.</param>
    /// <param name="format">The format.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The config text.</returns>
    [McpServerTool(Name = "export_process", Title = "Export process", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Exports a process as an XState v5 machine config (JSON, canonical key order). What has no XState home travels under meta.maquettiste (an action's sets among it), so import_process with into the same process gives the same file back. When the export has diagnostics (MQ9404 dropped pointers, MQ9406 notes), a second content block carries them as { diagnostics: [...] }.")]
    public Task<CallToolResult> ExportProcess(
        [Description("The process id, name or model path; required.")] string? process = null,
        [Description("xstate (the default and only format).")] string? format = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (format is not (null or "" or XStateProjection.Format))
            return BadRequest($"format must be '{XStateProjection.Format}'.");
        if (await ProcessIdAsync(process, ct).ConfigureAwait(false) is not { } id)
            return ProcessArgument(process);
        var call = await _store.ExportProcessAsync(id, ct).ConfigureAwait(false);
        if (call.Status != ProcessCallStatus.Ok)
            return FromCall(call);

        // The config text first; its diagnostics (MQ9404 dropped pointers, MQ9406 notes) follow as a second block when there are any.
        var result = Text(call.Value!.Json, isError: false);
        if (call.Value.Diagnostics.Count > 0)
            result.Content.Add(new TextContentBlock { Text = new JsonObject { ["diagnostics"] = JsonSerializer.SerializeToNode(call.Value.Diagnostics, JsonOptions) }.ToJsonString(JsonOptions) });
        return result;
    }, ct);

    /// <summary>Imports an XState config (importProcess).</summary>
    /// <param name="config">The config.</param>
    /// <param name="package">The package.</param>
    /// <param name="name">The name.</param>
    /// <param name="use">The use.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="into">The process to re-import over.</param>
    /// <param name="expectedHash">The hash of <paramref name="into"/> as read.</param>
    /// <param name="apply">Writes the import.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The preview or the saved import.</returns>
    [McpServerTool(Name = "import_process", Title = "Import process", Destructive = true, OpenWorld = false)]
    [Description("Imports an XState v5 machine config as a process: a new one (package, name, use, subject; a new process lists its subject in subjects), or into an existing process keeping the ids of the nodes it matches and the form its subjects are written in (removed lists what it drops). A dry run (the default) returns the process document, the diagnostics (MQ9401 to MQ9406) and the ids created; apply true saves it as one change, refused when it has errors or when into changed since expectedHash.")]
    public Task<CallToolResult> ImportProcess(
        [Description("The XState machine config, as an object or a string holding one; required.")] JsonElement? config = null,
        [Description("The package (id or name) of a new process.")] string? package = null,
        [Description("The name of a new process; the config's id when absent.")] string? name = null,
        [Description("lifecycle or orchestration.")] string? use = null,
        [Description("The subject entity (id or name); a new process lists it as the one entry of subjects.")] string? subject = null,
        [Description("A process (id or name) to re-import over.")] string? into = null,
        [Description("The hash of into from get_element; a changed process is a conflict.")] string? expectedHash = null,
        [Description("true writes the import; false or absent only previews it.")] bool apply = false,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var request = new ProcessImportRequest { Config = config, Package = package, Name = name, Use = use, Subject = subject, Into = into, ExpectedHash = expectedHash };
        return FromCall(await _store.ImportProcessAsync(request, !apply, ChangeSource.Cli, ct).ConfigureAwait(false));
    }, ct);

    /// <summary>Syncs a lifecycle's bound enum (syncEnumFromProcess).</summary>
    /// <param name="process">The process.</param>
    /// <param name="expectedHash">The process hash as read.</param>
    /// <param name="apply">Writes the sync.</param>
    /// <param name="entity">The subject whose enum binding to sync.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    [McpServerTool(Name = "sync_enum_from_process", Title = "Sync enum from process", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Makes a lifecycle's bound enum match its root-level states (members in state order; ids, codes and descriptions kept). entity picks the subject whose enum binding to sync; without it the lifecycle must have one enum binding. A dry run (the default) returns added, removed, reordered and refused (members still used by a default, allowedValues, a seed cell or a scenario value, with the ids that use them); apply true writes it. A refused member is never removed: change its uses first. To give states codes from a reference type, a plain status or an enum, use apply_batch with sync-codes.")]
    public Task<CallToolResult> SyncEnumFromProcess(
        [Description("The lifecycle process id, name or model path; required.")] string? process = null,
        [Description("The process hash from get_element; a changed process is a conflict.")] string? expectedHash = null,
        [Description("true writes the sync; false or absent only plans it.")] bool apply = false,
        [Description("The subject entity (id or name) whose enum binding to sync; absent means the lifecycle's one enum binding.")] string? entity = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (await ProcessIdAsync(process, ct).ConfigureAwait(false) is not { } id)
            return ProcessArgument(process);
        var entityId = entity;
        if (!string.IsNullOrEmpty(entity))
        {
            var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
            if (snapshot.Get<Maquettiste.Engine.Model.Entity>(entity) is null && snapshot.All<Maquettiste.Engine.Model.Entity>().Where(e => string.Equals(e.Name, entity, StringComparison.Ordinal)).ToList() is [var named])
                entityId = named.Id;
        }

        return FromCall(await _store.SyncEnumAsync(id, string.IsNullOrEmpty(entityId) ? null : entityId, !apply, expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false));
    }, ct);

    private async Task<string?> ProcessIdAsync(string? key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        var matches = ProcessApi.Resolve(await _store.GetSnapshotAsync(ct).ConfigureAwait(false), key);
        return matches.Count == 1 ? matches[0].Id : null;
    }

    private CallToolResult ProcessArgument(string? key) => string.IsNullOrEmpty(key)
        ? BadRequest("process is required: a process id, name or model path.")
        : Problem("not-found", 404, $"No single process has the id, name or path {key}; list them with get_model_index kind process.");

    private static IReadOnlyList<JsonElement>? StepsOf(JsonElement? steps) => steps switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => [],
        { ValueKind: JsonValueKind.Array } a => [.. a.EnumerateArray()],
        { ValueKind: JsonValueKind.String } s when JsonDocument.Parse(s.GetString() ?? "[]").RootElement is { ValueKind: JsonValueKind.Array } parsed => [.. parsed.Clone().EnumerateArray()],
        _ => null,
    };

    private CallToolResult FromCall<T>(ProcessCall<T> call) => call.Status switch
    {
        ProcessCallStatus.Ok => Ok(call.Value),
        ProcessCallStatus.NotFound => Problem("not-found", 404, call.Detail ?? "Not found."),
        ProcessCallStatus.BadRequest => BadRequest(call.Detail ?? "The request is not valid."),
        ProcessCallStatus.Conflict => Problem("conflict", 409, call.Detail ?? "The process changed since it was read; nothing was written.", body: call.Value is null ? null : Node(call.Value)),
        _ => Problem("invalid", 422, call.Detail ?? "Not valid; nothing was written. See diagnostics.",
            body: call.Value is null ? Node(ValidationReport.From(call.Diagnostics)) : Node(call.Value)),
    };
}
