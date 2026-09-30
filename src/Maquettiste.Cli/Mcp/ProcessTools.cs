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
    /// <param name="ct">Cancellation.</param>
    /// <returns>The simulation.</returns>
    [McpServerTool(Name = "simulate_process", Title = "Simulate process", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Runs a process through the engine interpreter from its initial state: start, then each input (a scenario step without expect: { input: event|time|invoke-done|invoke-error, event, actor, signer, meaning, reason, payload, assume, invoke, after }). Returns the trace of every input (transitions, exited and entered states, actions, guards, gate audit records, refusals), then the configuration, context, the triggers enabled now, pending invokes, timers, gates, final and clock. No state is kept: send the whole input list each time. document simulates an unsaved draft (invalid: 422 with diagnostics).")]
    public Task<CallToolResult> SimulateProcess(
        [Description("The process id, name or model path; required.")] string? process = null,
        [Description("The inputs, in order (scenario steps without expect; ids are optional).")] JsonElement? steps = null,
        [Description("The start: { context: { <attribute id>: value }, at: ISO 8601 instant }.")] JsonElement? start = null,
        [Description("A scenario id of the process: its start and steps run first, then steps.")] string? scenario = null,
        [Description("An unsaved draft of the whole process document to simulate instead of the saved one.")] JsonElement? document = null,
        [Description("The first trace index returned; -1 (default) includes the initial entry.")] int? from = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (await ProcessIdAsync(process, ct).ConfigureAwait(false) is not { } id)
            return ProcessArgument(process);
        if (StepsOf(steps) is not { } inputs)
            return BadRequest("steps must be an array of scenario steps.");
        var call = await _store.SimulateProcessAsync(id, new SimulateRequest { Steps = inputs, Start = start, Scenario = scenario, Document = document, From = from }, ct)
            .ConfigureAwait(false);
        return FromCall(call);
    }, ct);

    /// <summary>Records a scenario from inputs, its expectations filled by the replay (recordScenario).</summary>
    /// <param name="process">The process.</param>
    /// <param name="name">The scenario name.</param>
    /// <param name="steps">The inputs.</param>
    /// <param name="start">The start.</param>
    /// <param name="outcome">The expected outcome.</param>
    /// <param name="dryRun">Returns the scenario without writing it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The scenario.</returns>
    [McpServerTool(Name = "record_scenario", Title = "Record scenario", Destructive = false, OpenWorld = false)]
    [Description("Records a scenario of a process: the engine replays start and steps and fills each step's expect (accepted, states, changed context; refusals as accepted false) and the outcome from the replay, gives new ids and saves the scenario under model/scenarios/<process>/. dryRun true returns the scenario without writing it. Inputs that cannot be replayed to the last step are refused with diagnostics.")]
    public Task<CallToolResult> RecordScenario(
        [Description("The process id, name or model path; required.")] string? process = null,
        [Description("The scenario name (an identifier); required.")] string? name = null,
        [Description("The inputs, in order (scenario steps; an expect given is replaced).")] JsonElement? steps = null,
        [Description("The start: { context, at }.")] JsonElement? start = null,
        [Description("final or active; the replay's outcome when absent.")] string? outcome = null,
        [Description("true returns the scenario without writing it.")] bool dryRun = false,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (await ProcessIdAsync(process, ct).ConfigureAwait(false) is not { } id)
            return ProcessArgument(process);
        if (StepsOf(steps) is not { } inputs)
            return BadRequest("steps must be an array of scenario steps.");
        var call = await _store.RecordScenarioAsync(id, new RecordScenarioRequest { Name = name, Steps = inputs, Start = start, Outcome = outcome }, dryRun, ChangeSource.Cli, ct)
            .ConfigureAwait(false);
        return FromCall(call);
    }, ct);

    /// <summary>Replays a process's scenarios (verifyScenarios).</summary>
    /// <param name="process">The process.</param>
    /// <param name="scenarios">Scenario ids or names.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The results.</returns>
    [McpServerTool(Name = "verify_scenarios", Title = "Verify scenarios", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Replays the scenarios of a process (all, or the ids or names given) through the engine interpreter. Each result has passed, the step count and the first failure: { step, rule (MQ9301 accepted, MQ9302 states, MQ9303 context, MQ9304 outcome, or the stop MQ9305, MQ9306, MQ9507), message, expected, actual }. passed at the top is true when every scenario passed.")]
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
    [Description("Exports a process as an XState v5 machine config (JSON, canonical key order). What has no XState home travels under meta.maquettiste, so import_process with into the same process gives the same file back. When the export has diagnostics (MQ9404 dropped pointers, MQ9406 notes), a second content block carries them as { diagnostics: [...] }.")]
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
    [Description("Imports an XState v5 machine config as a process: a new one (package, name, use, subject), or into an existing process keeping the ids of the nodes it matches (removed lists what it drops). A dry run (the default) returns the process document, the diagnostics (MQ9401 to MQ9406) and the ids created; apply true saves it as one change, refused when it has errors or when into changed since expectedHash.")]
    public Task<CallToolResult> ImportProcess(
        [Description("The XState machine config, as an object or a string holding one; required.")] JsonElement? config = null,
        [Description("The package (id or name) of a new process.")] string? package = null,
        [Description("The name of a new process; the config's id when absent.")] string? name = null,
        [Description("lifecycle or orchestration.")] string? use = null,
        [Description("The subject entity (id or name).")] string? subject = null,
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
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    [McpServerTool(Name = "sync_enum_from_process", Title = "Sync enum from process", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Makes a lifecycle's bound enum match its root-level states (members in state order; ids, codes and descriptions kept). A dry run (the default) returns added, removed, reordered and refused (members still used by a default, allowedValues, a seed cell or a scenario value, with the ids that use them); apply true writes it. A refused member is never removed: change its uses first.")]
    public Task<CallToolResult> SyncEnumFromProcess(
        [Description("The lifecycle process id, name or model path; required.")] string? process = null,
        [Description("The process hash from get_element; a changed process is a conflict.")] string? expectedHash = null,
        [Description("true writes the sync; false or absent only plans it.")] bool apply = false,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (await ProcessIdAsync(process, ct).ConfigureAwait(false) is not { } id)
            return ProcessArgument(process);
        return FromCall(await _store.SyncEnumAsync(id, !apply, expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false));
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
