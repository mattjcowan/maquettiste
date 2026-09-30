using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine;

/// <summary>
/// The process operations of phase-3-design.md section 4.4 (<c>simulate</c>, <c>scenarios</c>, <c>verify</c>, <c>export</c>,
/// <c>import</c>, <c>sync-enum</c>): one engine call each, shared by the editor API, the MCP tools and the CLI verbs.
/// </summary>
public sealed partial class ModelStore
{
    private const string ProcessFolder = ".maquettiste/model/processes/";

    /// <summary>Simulates a process (or an unsaved draft of it) from the start through a list of inputs.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result; <see cref="ProcessCallStatus.Invalid"/> with diagnostics for a draft that does not validate.</returns>
    public async Task<ProcessCall<SimulationResult>> SimulateProcessAsync(string processId, SimulateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Process>(processId) is not { } stored)
            return ProcessCall<SimulationResult>.Fail(ProcessCallStatus.NotFound, $"No process has the id {processId}.");
        if (ProcessApi.ReadInputs(request.Steps, stored, snapshot, out var error) is not { } inputs)
            return ProcessCall<SimulationResult>.Fail(ProcessCallStatus.BadRequest, error);
        var start = ProcessApi.ReadStart(request.Start, out error);
        if (error is not null)
            return ProcessCall<SimulationResult>.Fail(ProcessCallStatus.BadRequest, error);
        if (request.Scenario is { Length: > 0 } scenarioId)
        {
            if (snapshot.Get<Scenario>(scenarioId) is not { } scenario || scenario.Process != processId)
                return ProcessCall<SimulationResult>.Fail(ProcessCallStatus.NotFound, $"No scenario of this process has the id {scenarioId}.");
            start = scenario.Start;
            inputs = [.. scenario.Steps.Select(s => s with { Expect = null }), .. inputs];
        }

        var model = snapshot;
        if (request.Document is { ValueKind: not JsonValueKind.Null } draft)
        {
            var (candidate, diagnostics) = await CheckDraftAsync(snapshot, processId, JsonSerializer.SerializeToUtf8Bytes(draft), ct).ConfigureAwait(false);
            if (candidate is null)
                return ProcessCall<SimulationResult>.Fail(ProcessCallStatus.Invalid, "The draft does not validate.", diagnostics);
            model = candidate;
        }

        using var runtime = new ProcessRuntime(model, 1, ct);
        var chart = runtime.Chart(processId)!;
        return ProcessCall<SimulationResult>.Ok(ProcessApi.Simulate(chart, model.GetDocument(processId)!.Hash, runtime, start, inputs, request.From ?? -1));
    }

    /// <summary>Records a scenario: the inputs replayed by the engine, each step's <c>expect</c> and the outcome filled from the replay.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="request">The request.</param>
    /// <param name="dryRun">Returns the scenario without writing it.</param>
    /// <param name="source">Who changes the model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The scenario (and its hash when written).</returns>
    public async Task<ProcessCall<RecordScenarioResult>> RecordScenarioAsync(string processId, RecordScenarioRequest request, bool dryRun, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Process>(processId) is not { } process)
            return ProcessCall<RecordScenarioResult>.Fail(ProcessCallStatus.NotFound, $"No process has the id {processId}.");
        if (string.IsNullOrWhiteSpace(request.Name))
            return ProcessCall<RecordScenarioResult>.Fail(ProcessCallStatus.BadRequest, "name is required.");
        if (request.Outcome is not (null or "final" or "active"))
            return ProcessCall<RecordScenarioResult>.Fail(ProcessCallStatus.BadRequest, "outcome must be 'final' or 'active'.");
        _ = ProcessApi.ReadStart(request.Start, out var error);
        if (error is not null || ProcessApi.ReadInputs(request.Steps, out error) is null)
            return ProcessCall<RecordScenarioResult>.Fail(ProcessCallStatus.BadRequest, error);

        var ids = _options.EffectiveIdGenerator;
        var steps = new JsonArray();
        foreach (var step in request.Steps ?? [])
        {
            var node = JsonNode.Parse(step.GetRawText())!.AsObject();
            node.Remove("expect");
            ProcessApi.ResolveNames(node, process, snapshot);
            node["id"] = ids.NewId();
            steps.Add(node);
        }

        var document = new JsonObject { ["kind"] = "scenario", ["id"] = ids.NewId(), ["name"] = request.Name, ["process"] = processId };
        if (request.Start is { ValueKind: JsonValueKind.Object } start)
            document["start"] = JsonNode.Parse(start.GetRawText());
        document["steps"] = steps;
        Scenario scenario;
        try
        {
            scenario = document.Deserialize<Scenario>(EngineJson.Options)!;
        }
        catch (JsonException e)
        {
            return ProcessCall<RecordScenarioResult>.Fail(ProcessCallStatus.BadRequest, "The steps are not scenario steps: " + e.Message);
        }

        ScenarioReplay replay;
        using (var runtime = new ProcessRuntime(snapshot, 1, ct))
            replay = ScenarioReplayer.Replay(scenario, runtime)!;
        if (!replay.Complete)
            return ProcessCall<RecordScenarioResult>.Fail(ProcessCallStatus.Invalid, "The inputs cannot be replayed to the last step.", replay.Diagnostics);
        var (expects, outcome) = replay.Observed();
        for (var i = 0; i < steps.Count; i++)
            steps[i]!["expect"] = ProcessApi.ExpectNode(expects[i]);
        var outcomeText = request.Outcome ?? (outcome == ScenarioOutcome.Final ? "final" : "active");
        if (outcomeText == "final")
            document["outcome"] = "final";

        var stem = snapshot.GetDocument(processId)!.Path;
        stem = stem[(stem.LastIndexOf('/') + 1)..^".json".Length];
        var path = $".maquettiste/model/scenarios/{stem}/{ModelPaths.Kebab(request.Name)}.json";
        var bytes = _services.Json.Write(document, "scenario.json", path);
        var id = document["id"]!.GetValue<string>();
        if (dryRun)
            return ProcessCall<RecordScenarioResult>.Ok(new RecordScenarioResult(id, JsonDocument.Parse(bytes).RootElement.Clone(), null, false, []));
        var saved = await CreateAsync(bytes, source, ct).ConfigureAwait(false);
        if (saved.Outcome != SaveOutcome.Saved)
            return ProcessCall<RecordScenarioResult>.Fail(saved.Outcome == SaveOutcome.Conflict ? ProcessCallStatus.Conflict : ProcessCallStatus.Invalid, "The scenario was not saved.", saved.Diagnostics);
        _ = process;
        return ProcessCall<RecordScenarioResult>.Ok(new RecordScenarioResult(id, saved.Current?.Json ?? JsonDocument.Parse(bytes).RootElement.Clone(), saved.Hash, true, saved.Diagnostics));
    }

    /// <summary>Replays scenarios of a process and reports each one's first failure.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="scenarios">Scenario ids or names; every scenario of the process when null or empty.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The results.</returns>
    public async Task<ProcessCall<VerifyResult>> VerifyScenariosAsync(string processId, IReadOnlyList<string>? scenarios, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Process>(processId) is not { } process)
            return ProcessCall<VerifyResult>.Fail(ProcessCallStatus.NotFound, $"No process has the id {processId}.");
        var (result, unknown) = ProcessApi.Verify(snapshot, process, scenarios, _options.EffectiveParallelism, ct);
        return result is null
            ? ProcessCall<VerifyResult>.Fail(ProcessCallStatus.NotFound, $"No scenario of process '{process.Name}' has the id or name {unknown}.")
            : ProcessCall<VerifyResult>.Ok(result);
    }

    /// <summary>Exports a process as an XState machine config (canonical text).</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The config and MQ9406 (and MQ9404) diagnostics.</returns>
    public async Task<ProcessCall<XStateExport>> ExportProcessAsync(string processId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        return snapshot.Get<Process>(processId) is { } process
            ? ProcessCall<XStateExport>.Ok(XStateProjection.Export(process, _services.Json, XStateExportOptions.For(snapshot)))
            : ProcessCall<XStateExport>.Fail(ProcessCallStatus.NotFound, $"No process has the id {processId}.");
    }

    /// <summary>
    /// Imports an XState machine config as a new process, or over <c>into</c> keeping its ids: a dry run returns the document the
    /// import gives; otherwise it is saved as one change (409 when <c>into</c> changed since <c>expectedHash</c>).
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="dryRun">Returns the document without writing it.</param>
    /// <param name="source">Who changes the model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    public async Task<ProcessCall<ProcessImportResult>> ImportProcessAsync(ProcessImportRequest request, bool dryRun, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        string text;
        switch (request.Config)
        {
            case { ValueKind: JsonValueKind.String } s:
                text = s.GetString()!;
                break;
            case { ValueKind: JsonValueKind.Object } o:
                text = o.GetRawText();
                break;
            default:
                return ProcessCall<ProcessImportResult>.Fail(ProcessCallStatus.BadRequest, "config is required: the XState machine config, as an object or a string.");
        }

        Process? into = null;
        if (request.Into is { Length: > 0 } intoKey)
        {
            var matches = ProcessApi.Resolve(snapshot, intoKey);
            if (matches.Count != 1)
                return ProcessCall<ProcessImportResult>.Fail(ProcessCallStatus.NotFound, matches.Count == 0 ? $"No process has the id or name {intoKey}." : $"'{intoKey}' names {matches.Count} processes; pass an id.");
            into = matches[0];
            var current = snapshot.GetDocument(into.Id)!.Hash;
            if (request.ExpectedHash is { Length: > 0 } expected && !string.Equals(expected, current, StringComparison.Ordinal))
                return ProcessCall<ProcessImportResult>.Fail(ProcessCallStatus.Conflict, $"Process '{into.Name}' changed since it was read; export it again and retry.");
        }

        string? package = null;
        if (request.Package is { Length: > 0 } packageKey)
        {
            package = ElementKey<Package>(snapshot, packageKey)?.Id;
            if (package is null)
                return ProcessCall<ProcessImportResult>.Fail(ProcessCallStatus.NotFound, $"No package has the id or name {packageKey}.");
        }

        string? subject = null;
        if (request.Subject is { Length: > 0 } subjectKey)
        {
            subject = ElementKey<Entity>(snapshot, subjectKey)?.Id;
            if (subject is null)
                return ProcessCall<ProcessImportResult>.Fail(ProcessCallStatus.NotFound, $"No entity has the id or name {subjectKey}.");
        }

        ProcessUse? use = request.Use switch
        {
            null or "" => null,
            "lifecycle" => ProcessUse.Lifecycle,
            "orchestration" => ProcessUse.Orchestration,
            _ => (ProcessUse)(-1),
        };
        if (use == (ProcessUse)(-1))
            return ProcessCall<ProcessImportResult>.Fail(ProcessCallStatus.BadRequest, "use must be 'lifecycle' or 'orchestration'.");

        var import = XStateProjection.Import(text, _services.Json, new XStateImportOptions
        {
            Model = snapshot,
            Into = into,
            Package = package,
            Name = request.Name is { Length: > 0 } n ? n : null,
            Use = use,
            Subject = subject,
            Ids = _options.EffectiveIdGenerator,
            DocumentPath = into is null ? ProcessFolder + "process.json" : snapshot.GetDocument(into.Id)!.Path,
        });
        JsonElement? document = import.File is { } file ? JsonDocument.Parse(file).RootElement.Clone() : null;
        var result = new ProcessImportResult(document, import.Diagnostics, import.Created, import.Removed, false, import.Process?.Id ?? into?.Id, null);
        if (dryRun)
            return ProcessCall<ProcessImportResult>.Ok(result);
        if (import.HasErrors || import.File is null)
            return new(ProcessCallStatus.Invalid, result, "The import has errors; nothing was written.", import.Diagnostics);

        var saved = into is null
            ? await CreateAsync(import.File, source, ct).ConfigureAwait(false)
            : await SaveAsync(into.Id, import.File, request.ExpectedHash is { Length: > 0 } h ? h : snapshot.GetDocument(into.Id)!.Hash, source, ct).ConfigureAwait(false);
        var diagnostics = import.Diagnostics.Concat(saved.Diagnostics).ToList();
        return saved.Outcome switch
        {
            SaveOutcome.Saved => ProcessCall<ProcessImportResult>.Ok(result with
            {
                Applied = true, Id = saved.Id, Hash = saved.Hash, Diagnostics = diagnostics, Document = saved.Current?.Json ?? result.Document,
            }),
            SaveOutcome.Conflict => new(ProcessCallStatus.Conflict, result with { Diagnostics = diagnostics }, "The process changed while the import ran; nothing was written.", diagnostics),
            _ => new(ProcessCallStatus.Invalid, result with { Diagnostics = diagnostics }, "The imported process does not validate; nothing was written.", diagnostics),
        };
    }

    /// <summary>
    /// Syncs a lifecycle's bound enum with its root-level states: the plan (a dry run), or the plan applied as one batch. A removal of
    /// a member still in use is refused, never forced.
    /// </summary>
    /// <param name="processId">The process id.</param>
    /// <param name="dryRun">Returns the plan without writing.</param>
    /// <param name="expectedHash">The process hash as the caller read it (409 when it changed).</param>
    /// <param name="source">Who changes the model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan and whether it was applied.</returns>
    public async Task<ProcessCall<SyncEnumResult>> SyncEnumAsync(string processId, bool dryRun, string? expectedHash, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Process>(processId) is not { } process)
            return ProcessCall<SyncEnumResult>.Fail(ProcessCallStatus.NotFound, $"No process has the id {processId}.");
        if (expectedHash is { Length: > 0 } && !string.Equals(expectedHash, snapshot.GetDocument(processId)!.Hash, StringComparison.Ordinal))
            return ProcessCall<SyncEnumResult>.Fail(ProcessCallStatus.Conflict, $"Process '{process.Name}' changed since it was read; reload it and sync again.");
        var plan = await PlanSyncEnumAsync(processId, ct).ConfigureAwait(false);
        var result = new SyncEnumResult(processId, plan.Enum, plan.Added, plan.Removed, plan.Reordered, plan.Refused, false, []);
        if (plan.Problem is not null)
        {
            var refusal = RuleCatalog.Create("MQ9019", plan.Problem, processId);
            return new(ProcessCallStatus.Invalid, result with { Diagnostics = [refusal] }, plan.Problem, [refusal]);
        }

        if (dryRun || (plan.Added.Count == 0 && plan.Removed.Count == 0 && !plan.Reordered))
            return ProcessCall<SyncEnumResult>.Ok(result);
        var batch = await ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.SyncEnum, processId, expectedHash is { Length: > 0 } ? expectedHash : null, null)]), source, ct)
            .ConfigureAwait(false);
        var diagnostics = batch.Items.SelectMany(i => i.Diagnostics).ToList();
        return batch.Outcome switch
        {
            SaveOutcome.Saved => ProcessCall<SyncEnumResult>.Ok(result with { Applied = true, Diagnostics = diagnostics }),
            SaveOutcome.Conflict => new(ProcessCallStatus.Conflict, result with { Diagnostics = diagnostics }, "The model changed while the sync ran; nothing was written.", diagnostics),
            _ => new(ProcessCallStatus.Invalid, result with { Diagnostics = diagnostics }, "The sync was refused; nothing was written.", diagnostics),
        };
    }

    // A draft checked as a save would check it, without writing: the candidate model, or null when the draft adds an error.
    private async Task<(ModelSnapshot? Candidate, IReadOnlyList<Diagnostic> Diagnostics)> CheckDraftAsync(ModelSnapshot snapshot, string processId, byte[] json, CancellationToken ct)
    {
        if (!TryParseRequest(json, processId, out var node, out var invalid))
            return (null, invalid.Diagnostics);
        var plan = Plan(snapshot, [new PlannedChange(BatchOp.Update, processId, snapshot.GetDocument(processId)!.Hash, node, DeleteResolution.Refuse)], ct);
        if (!plan.Failed)
            await ValidateAsync(plan, snapshot, ct).ConfigureAwait(false);
        var diagnostics = plan.Outcomes.SelectMany(o => o.Diagnostics).ToList();
        return (plan.Failed ? null : plan.Candidate, diagnostics);
    }

    private static T? ElementKey<T>(ModelSnapshot snapshot, string key) where T : Element
    {
        if (snapshot.Get<T>(key) is { } byId)
            return byId;
        var all = snapshot.All<T>();
        return all.FirstOrDefault(e => string.Equals(e.Name, key, StringComparison.Ordinal))
            ?? all.FirstOrDefault(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase));
    }
}
