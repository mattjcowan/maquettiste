using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Engine;

/// <summary>Runs the generation pipeline: generate, plan, apply, diff and preview (host-contracts requirements 29 to 35; W6).</summary>
/// <remarks>
/// Every mode takes the run lock first (D22), replays an unfinished run journal over the manifests (so files an interrupted run
/// wrote are never hand edits), then runs stages 1 to 8 (engine-design.md section 4.3). Cancellation returns
/// <see cref="RunOutcome.Cancelled"/> and leaves an apply's journal unfinished, so the next run resumes it. Other exceptions (an I/O
/// failure while writing, for example) propagate; the journal is left unfinished the same way.
/// </remarks>
public sealed class GenerationService
{
    private readonly ModelStore _store;
    private readonly EngineOptions _options;
    private readonly EngineServices _services;

    /// <summary>Creates the service.</summary>
    /// <param name="store">The model store.</param>
    /// <param name="options">The engine options.</param>
    public GenerationService(ModelStore store, EngineOptions options)
        : this(store, options, EngineServices.Create(options))
    {
    }

    /// <summary>Creates the service over substitute services (tests).</summary>
    /// <param name="store">The model store.</param>
    /// <param name="options">The engine options.</param>
    /// <param name="services">The services.</param>
    internal GenerationService(ModelStore store, EngineOptions options, EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        _store = store;
        _options = options;
        _services = services;
    }

    /// <summary>The services (the job queue reaches the job store through them).</summary>
    internal EngineServices Services => _services;

    /// <summary>The model store.</summary>
    internal ModelStore Store => _store;

    /// <summary>Runs generation in the request's mode.</summary>
    /// <param name="request">The request.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="ct">Cancellation; observed between files, returning within one second.</param>
    /// <returns>The result.</returns>
    public Task<GenerationResult> RunAsync(GenerationRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = new GenerationRun(_services, _store, progress);
        return ExecuteAsync(request, NewId(), run, null, ct);
    }

    /// <summary>Computes and persists a plan: a dry run whose post-processed bytes are stored (host-contracts requirement 29).</summary>
    /// <param name="request">The request.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public async Task<PlanResult> PlanAsync(GenerationRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var planId = NewId();
        var capture = new PlanCapture(_services.Plans, planId, RepoRoot);
        var run = new GenerationRun(_services, _store, progress);
        GenerationResult result;
        try
        {
            result = await ExecuteAsync(request with { Mode = GenerationMode.DryRun }, planId, run, capture, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeletePlan(planId);
            throw;
        }

        if (result.Outcome is RunOutcome.Busy or RunOutcome.Cancelled or RunOutcome.Failed)
        {
            TryDeletePlan(planId);
            return new PlanResult(result.Outcome, null);
        }

        var prepared = capture.Prepared;
        var plan = new GenerationPlan(planId, request, prepared?.Snapshot.Version ?? _store.Current?.Version ?? 0,
            prepared is null ? [.. request.Packs ?? []] : [.. prepared.Packs.Packs.Select(p => p.Name)],
            prepared is null ? [] : capture.Units(prepared.Plan.Units), result.Changes, result.Diagnostics);
        var policies = prepared is null
            ? new SortedDictionary<string, HandEditPolicy>(StringComparer.Ordinal)
            : GenerationRun.Policies(prepared, request, GenerationMode.DryRun);
        await _services.Plans.SaveWriteSettingsAsync(planId, policies, CancellationToken.None).ConfigureAwait(false);
        await _services.Plans.SaveAsync(plan, CancellationToken.None).ConfigureAwait(false);
        return new PlanResult(result.Outcome, plan);
    }

    /// <summary>
    /// Applies a stored plan with the plan's own <see cref="GenerationPlan.Request"/>, without rendering again, or returns
    /// <see cref="RunOutcome.Stale"/> writing nothing (host-contracts requirement 30) when a unit's input hash, the unit set, or the
    /// disk hash of any planned path (<see cref="PlanFile.DiskHashAtPlan"/>: hand edits and region edits) changed, when a planned
    /// path's output root changed, or when the hand-edit policy behind a hand-edited path of the plan changed. A path that already
    /// holds exactly the planned bytes, recorded by the engine (an earlier apply of this plan, interrupted or finished), is not a
    /// change, so applying the same plan again resumes it. It writes nothing outside the plan's paths (SPEC section 19).
    /// </summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result: <see cref="RunOutcome.Failed"/> when the plan does not exist or its blobs are missing,
    /// <see cref="RunOutcome.Invalid"/> when the plan (or the model now) has errors.</returns>
    public async Task<ApplyResult> ApplyAsync(string planId, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(planId);
        var plan = await _services.Plans.LoadAsync(planId, ct).ConfigureAwait(false);
        if (plan is null)
            return new ApplyResult(RunOutcome.Failed, [], [], null);
        var runId = NewId();
        var run = new GenerationRun(_services, _store, progress);
        var request = plan.Request with { Mode = GenerationMode.Apply, IncludeDiffs = false };
        if (plan.Diagnostics.Any(Outcomes.IsInvalid))
        {
            run.Add(plan.Diagnostics);
            return new ApplyResult(RunOutcome.Invalid, [], [], Result(runId, GenerationMode.Apply, RunOutcome.Invalid, run));
        }

        IAsyncDisposable? held;
        try
        {
            held = await _services.RunLock.AcquireAsync(request.Lock == LockMode.Wait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ApplyResult(RunOutcome.Cancelled, [], [], Result(runId, GenerationMode.Apply, RunOutcome.Cancelled, run));
        }

        if (held is null)
            return new ApplyResult(RunOutcome.Busy, [], [], Result(runId, GenerationMode.Apply, RunOutcome.Busy, run));
        await using (held.ConfigureAwait(false))
        {
            try
            {
                return await ApplyLockedAsync(plan, request, runId, run, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new ApplyResult(RunOutcome.Cancelled, [], [], Result(runId, GenerationMode.Apply, RunOutcome.Cancelled, run));
            }
        }
    }

    /// <summary>Loads a stored plan.</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan, or <see langword="null"/>.</returns>
    public Task<GenerationPlan?> GetPlanAsync(string planId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(planId);
        return _services.Plans.LoadAsync(planId, ct);
    }

    /// <summary>Returns the unified diff of one path in a stored plan, without rendering again (host-contracts requirement 31).</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="path">A repo-relative path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The diff, or <see langword="null"/> when the path is not in the plan.</returns>
    /// <remarks>
    /// The diff runs from the file on disk now to the planned bytes (the plan's blob), so it is empty for a file the plan leaves
    /// unchanged or keeps; a deleted orphan diffs to nothing. A path is looked up in the plan before the disk is read, so only paths
    /// the plan names are ever read.
    /// </remarks>
    public async Task<string?> GetPlanDiffAsync(string planId, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(path);
        var plan = await _services.Plans.LoadAsync(planId, ct).ConfigureAwait(false);
        if (plan is null)
            return null;
        var file = plan.Units.SelectMany(u => u.Outputs).FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));
        var change = plan.Changes.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.Ordinal));
        if (file is null && change is null)
            return null;
        var planned = file?.Path ?? change!.Path;
        var full = Path.Combine(RepoRoot, planned.Replace('/', Path.DirectorySeparatorChar));
        var disk = File.Exists(full) ? await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false) : [];
        if (file is not null)
        {
            var blob = await _services.Plans.ReadBlobAsync(planId, file.ContentHash, ct).ConfigureAwait(false);
            return _services.Diffs.Unified(planned, disk, blob ?? disk);
        }

        return change!.Kind is FileChangeKind.Deleted or FileChangeKind.HandEdited or FileChangeKind.Conflict
            ? _services.Diffs.Unified(planned, disk, [])
            : _services.Diffs.Unified(planned, disk, disk);
    }

    /// <summary>Renders one unit with no writes (host-contracts requirement 35).</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="unitId">The unit id.</param>
    /// <param name="elementId">The element, for element-scoped units.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rendered files and diagnostics.</returns>
    /// <remarks>
    /// The unit renders against the element even when its <c>where</c> filters would not plan it, so a template can be previewed on
    /// any element. The model must be valid (validation errors come back as diagnostics, with no files).
    /// </remarks>
    public async Task<PreviewResult> PreviewAsync(string pack, string unitId, string? elementId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(unitId);
        var run = new GenerationRun(_services, _store, null);
        var prepared = await run.PrepareAsync([pack], GenerationMode.DryRun, ct, locked: false).ConfigureAwait(false);
        if (prepared is null)
            return new PreviewResult([], [.. run.Diagnostics.Where(Outcomes.IsInvalid)]);
        var loaded = prepared.Packs.Packs.FirstOrDefault(p => string.Equals(p.Name, pack, StringComparison.Ordinal));
        var unit = loaded?.Manifest.Units.FirstOrDefault(u => string.Equals(u.Id, unitId, StringComparison.Ordinal));
        if (loaded is null || unit is null)
        {
            return new PreviewResult([], [RuleCatalog.Create("MQ6001", loaded is null
                ? $"Pack '{pack}' is not enabled or does not exist."
                : $"Pack '{pack}' has no unit '{unitId}'.", filePath: loaded?.RelativePath + "/pack.json")]);
        }

        var element = elementId is null ? null : prepared.Resolved.Find(elementId);
        if (elementId is not null && element is null)
            return new PreviewResult([], [RuleCatalog.Create("MQ6017", $"Element '{elementId}' is not in the resolved model.", elementId)]);

        var key = UnitPlanner.KeyOf(loaded.Name, unit.Id, element?.Id);
        var planned = prepared.Plan.Units.FirstOrDefault(u => string.Equals(u.Key, key, StringComparison.Ordinal))
            ?? new PlannedUnit(key, loaded, unit, element, UnitPlanner.StaticHash(loaded, unit, prepared.Snapshot.Settings.Formatters, key));
        var renderer = _services.CreateRenderer();
        var context = new RenderContext(prepared.Resolved, prepared.Packs, prepared.SchemaDiffs, _services.Scripts, prepared.Hasher, 1);
        var rendered = await renderer.RenderOneAsync(planned, context, ct).ConfigureAwait(false);
        return new PreviewResult(rendered.Files, Outcomes.Sort(rendered.Diagnostics));
    }

    private string RepoRoot => Path.GetFullPath(_options.RepoRoot);

    private string NewId() => _options.EffectiveIdGenerator.NewId();

    private int Jobs(GenerationRequest request) => Math.Max(1, request.Jobs ?? _options.EffectiveParallelism);

    private static GenerationResult Result(string runId, GenerationMode mode, RunOutcome outcome, GenerationRun run, int skipped = 0) =>
        new(runId, mode, outcome, [], 0, skipped, 0, 0, run.Diagnostics, run.Clock.Timings());

    private void TryDeletePlan(string planId)
    {
        try
        {
            _services.Plans.Delete(planId);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Stages 1 to 8 of one run under the run lock (generate, dry run, check, and the dry run behind a plan).</summary>
    private async Task<GenerationResult> ExecuteAsync(GenerationRequest request, string runId, GenerationRun run, PlanCapture? capture, CancellationToken ct)
    {
        var mode = request.Mode;
        IAsyncDisposable? held;
        try
        {
            held = await _services.RunLock.AcquireAsync(request.Lock == LockMode.Wait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result(runId, mode, RunOutcome.Cancelled, run);
        }

        if (held is null)
            return Result(runId, mode, RunOutcome.Busy, run);
        await using (held.ConfigureAwait(false))
        {
            var skippedCount = 0;
            IRunJournal? open = null;
            try
            {
                var prepared = await run.PrepareAsync(request.Packs, mode, ct, barriers: request.StageBarriers).ConfigureAwait(false);
                if (prepared is null)
                    return Result(runId, mode, RunOutcome.Invalid, run);
                var skip = await run.SkipAsync(prepared, mode, request.Force, ct).ConfigureAwait(false);
                skippedCount = skip.Skipped.Count;
                if (!await run.VerifyFormattersAsync(prepared, skip.ToRender, ct).ConfigureAwait(false))
                    return Result(runId, mode, RunOutcome.Invalid, run, skippedCount);

                Func<ProcessedUnit, CancellationToken, Task>? hook = null;
                var stale = new ConcurrentQueue<FileChange>();
                if (capture is not null)
                {
                    capture.Prepared = prepared;
                    await capture.AddSkippedAsync(skip.Skipped, prepared.Paths, ct).ConfigureAwait(false);
                    hook = capture.HookAsync;
                }
                else if (mode == GenerationMode.Check)
                {
                    hook = (unit, _) =>
                    {
                        StaleManifestEntries(unit, prepared.Manifests, stale);
                        return Task.CompletedTask;
                    };
                }

                var journal = mode == GenerationMode.Apply ? _services.Journal : null;
                if (journal is not null)
                {
                    await journal.BeginAsync(runId, null, [.. prepared.Packs.Packs.Select(p => p.Name)], ct).ConfigureAwait(false);
                    open = journal;
                }

                var write = run.WriteContext(prepared, request, mode, runId, skip.ToRender, skip.Skipped, journal, null);
                var stream = await run.StreamAsync(prepared, skip.ToRender, write, Jobs(request), request.StageBarriers, hook, ct).ConfigureAwait(false);

                var changes = stream.Summary.Changes;
                if (!stale.IsEmpty)
                {
                    var listed = changes.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
                    changes = [.. changes.Concat(stale.Where(c => !listed.Contains(c.Path)))
                        .OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Pack, StringComparer.Ordinal)];
                }

                var outcome = Outcomes.Of(mode, run.Diagnostics, changes, !stale.IsEmpty);
                if (mode == GenerationMode.Apply && outcome == RunOutcome.Succeeded && request.Roots == RootSelection.All)
                    await run.SaveSnapshotsAsync(prepared, ct).ConfigureAwait(false);
                if (journal is not null)
                {
                    await journal.EndAsync(ct).ConfigureAwait(false);
                    open = null;
                }

                return new GenerationResult(runId, mode, outcome, changes, stream.Rendered, skippedCount, stream.Summary.Written, stream.Summary.Deleted,
                    run.Diagnostics, run.Clock.Timings());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Result(runId, mode, RunOutcome.Cancelled, run, skippedCount);
            }
            finally
            {
                // Interrupted: close the journal without "end", so the next run resumes it.
                if (open is not null)
                    await open.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Check mode: a committed output whose manifest entry is missing or differs from what an apply would record is drift even when
    /// the file on disk already has the new bytes (the committed manifest would change). Listed as <see cref="FileChangeKind.Modified"/>.
    /// </summary>
    private static void StaleManifestEntries(ProcessedUnit unit, ManifestSet manifests, ConcurrentQueue<FileChange> stale)
    {
        if (unit.Failed)
            return;
        var pack = unit.Rendered.Unit.Pack.Name;
        foreach (var file in unit.Files)
        {
            if (!file.Root.Commit)
                continue;
            var owned = PlanCapture.IsOwned(file.Mode, file.Role, file.ManifestHash);
            if (!manifests.TryGet(file.Path, out var entry, out var owner))
                stale.Enqueue(new FileChange(file.Path, FileChangeKind.Modified, pack, unit.Rendered.Unit.Key, null, file.ManifestHash, null));
            else if (!string.Equals(owner, pack, StringComparison.Ordinal) || (!owned && !string.Equals(entry.Hash, file.ManifestHash, StringComparison.Ordinal)))
                stale.Enqueue(new FileChange(file.Path, FileChangeKind.Modified, pack, unit.Rendered.Unit.Key, entry.Hash, file.ManifestHash, null));
        }
    }

    private async Task<ApplyResult> ApplyLockedAsync(GenerationPlan plan, GenerationRequest request, string runId, GenerationRun run, CancellationToken ct)
    {
        var prepared = await run.PrepareAsync(request.Packs, GenerationMode.Apply, ct).ConfigureAwait(false);
        if (prepared is null)
            return new ApplyResult(RunOutcome.Invalid, [], [], Result(runId, GenerationMode.Apply, RunOutcome.Invalid, run));

        // Inputs: every planned unit must still exist with the same input hash, and no unit may have appeared.
        var current = prepared.Plan.Units.ToDictionary(u => u.Key, StringComparer.Ordinal);
        var staleUnits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var unit in plan.Units)
        {
            if (!current.TryGetValue(unit.Key, out var now)
                || !string.Equals(prepared.Hasher.InputHash(now.StaticHash, unit.ReadKeys), unit.InputHash, StringComparison.Ordinal))
                staleUnits.Add(unit.Key);
        }

        var plannedKeys = plan.Units.Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in current.Keys.Where(k => !plannedKeys.Contains(k)))
            staleUnits.Add(key);

        // Disk: every planned path must hash as it did at plan time, or already hold exactly the planned bytes that an engine write
        // recorded (an earlier apply of this plan that was interrupted, then resumed through the journal overlay, or one that
        // finished); a planned deletion must still be intact. Roots: every planned path must still classify under the same root.
        var changedOnDisk = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(plan.Units.SelectMany(u => u.Outputs.Select(f => (Unit: u, File: f))),
            new ParallelOptions { MaxDegreeOfParallelism = _options.EffectiveParallelism, CancellationToken = ct },
            async (item, token) =>
            {
                var file = item.File;
                var root = prepared.Paths.Check(file.Path).Root ?? new OutputRootInfo("", false);
                var disk = await PlanCapture.DiskHashAsync(RepoRoot, file.Path, token).ConfigureAwait(false);
                if (root != file.Root || (!string.Equals(disk, file.DiskHashAtPlan, StringComparison.Ordinal)
                    && !AlreadyApplied(file, disk, PackOf(item.Unit.Key, current), prepared.Manifests)))
                    changedOnDisk.Add(file.Path);
            }).ConfigureAwait(false);
        var stalePaths = new SortedSet<string>(changedOnDisk, StringComparer.Ordinal);

        // Write settings: a hand-edit decision of the plan must be taken under the policy the plan was made with.
        var pinned = await _services.Plans.LoadWriteSettingsAsync(plan.Id, ct).ConfigureAwait(false);
        var policies = GenerationRun.Policies(prepared, request, GenerationMode.Apply);
        foreach (var change in plan.Changes.Where(c => c.Kind is FileChangeKind.HandEdited or FileChangeKind.Conflict))
        {
            if (pinned is null || !pinned.HandEdits.TryGetValue(change.Pack, out var then) || !policies.TryGetValue(change.Pack, out var now) || then != now)
                stalePaths.Add(change.Path);
        }

        foreach (var change in plan.Changes.Where(c => c.Kind == FileChangeKind.Deleted && c.OldHash is not null))
        {
            var full = Path.Combine(RepoRoot, change.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                continue;
            var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            if (!string.Equals(Writing.ManifestHashes.Comparable(change.OldHash!, bytes, Hashing.ContentHash.Of(bytes)), change.OldHash, StringComparison.Ordinal))
                stalePaths.Add(change.Path);
        }

        if (staleUnits.Count > 0 || stalePaths.Count > 0)
            return new ApplyResult(RunOutcome.Stale, [.. staleUnits], [.. stalePaths], null);

        // Every blob the apply needs must be there before anything is written.
        foreach (var file in plan.Units.Where(u => !u.Skipped).SelectMany(u => u.Outputs).Where(PlanCapture.NeedsBlob))
        {
            if (!_services.Plans.HasBlob(plan.Id, file.ContentHash))
                return new ApplyResult(RunOutcome.Failed, [], [file.Path], Result(runId, GenerationMode.Apply, RunOutcome.Failed, run));
        }

        var skipped = new List<SkippedUnit>();
        var states = new Dictionary<string, IReadOnlyDictionary<string, UnitState>>(StringComparer.Ordinal);
        foreach (var unit in plan.Units.Where(u => u.Skipped))
        {
            var planned = current[unit.Key];
            if (!states.TryGetValue(planned.Pack.Name, out var packStates))
                states[planned.Pack.Name] = packStates = await _services.UnitState.LoadAsync(planned.Pack.Name, ct).ConfigureAwait(false);
            var state = packStates.TryGetValue(unit.Key, out var stored) && string.Equals(stored.InputHash, unit.InputHash, StringComparison.Ordinal)
                ? stored
                : new UnitState(unit.Key, unit.InputHash, unit.ReadKeys, [.. unit.Outputs.Select(Stat)]);
            skipped.Add(new SkippedUnit(planned, state));
        }

        var rendered = plan.Units.Where(u => !u.Skipped).ToList();
        var plannedPaths = plan.Units.SelectMany(u => u.Outputs).Select(f => f.Path).Concat(plan.Changes.Select(c => c.Path)).ToHashSet(StringComparer.Ordinal);
        var journal = _services.Journal;
        await journal.BeginAsync(runId, plan.Id, [.. prepared.Packs.Packs.Select(p => p.Name)], ct).ConfigureAwait(false);
        WriteSummary summary;
        RunOutcome outcome;
        var ended = false;
        try
        {
            var write = run.WriteContext(prepared, request, GenerationMode.Apply, runId, rendered.Select(u => current[u.Key]), skipped, journal, plannedPaths);
            summary = await run.WriteAsync(prepared, FromPlan(plan.Id, rendered, current, ct), write, ct).ConfigureAwait(false);
            outcome = Outcomes.Of(GenerationMode.Apply, run.Diagnostics, summary.Changes);
            if (outcome == RunOutcome.Succeeded && request.Roots == RootSelection.All)
                await run.SaveSnapshotsAsync(prepared, ct).ConfigureAwait(false);
            await journal.EndAsync(ct).ConfigureAwait(false);
            ended = true;
        }
        finally
        {
            // Interrupted: close the journal without "end", so the next run resumes it.
            if (!ended)
                await journal.DisposeAsync().ConfigureAwait(false);
        }

        var result = new GenerationResult(runId, GenerationMode.Apply, outcome, summary.Changes, rendered.Count, skipped.Count, summary.Written, summary.Deleted,
            run.Diagnostics, run.Clock.Timings());
        return new ApplyResult(outcome, [], [], result);
    }

    /// <summary>
    /// Whether a planned path already holds exactly what the plan writes, put there by the engine: its disk hash is the planned
    /// content hash and the manifest (with an unfinished journal overlaid) records the planned manifest hash under the unit's pack.
    /// </summary>
    private static bool AlreadyApplied(PlanFile file, string? disk, string pack, ManifestSet manifests) =>
        disk is not null && string.Equals(disk, file.ContentHash, StringComparison.Ordinal)
        && manifests.TryGet(file.Path, out var entry, out var owner)
        && string.Equals(owner, pack, StringComparison.Ordinal) && string.Equals(entry.Hash, file.ManifestHash, StringComparison.Ordinal);

    /// <summary>The pack of a plan unit: from the current plan, else the key's <c>&lt;pack&gt;/</c> prefix.</summary>
    private static string PackOf(string key, IReadOnlyDictionary<string, PlannedUnit> current) =>
        current.TryGetValue(key, out var unit) ? unit.Pack.Name : key[..Math.Max(0, key.IndexOf('/', StringComparison.Ordinal))];

    private UnitOutput Stat(PlanFile file)
    {
        var info = new FileInfo(Path.Combine(RepoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        return info.Exists ? new UnitOutput(file.Path, file.ManifestHash, info.Length, info.LastWriteTimeUtc.Ticks) : new UnitOutput(file.Path, file.ManifestHash, -1, 0);
    }

    /// <summary>The processed units of a plan's rendered units, reading each blob only when the writer asks for its unit.</summary>
    private async IAsyncEnumerable<ProcessedUnit> FromPlan(string planId, IReadOnlyList<PlanUnit> units, IReadOnlyDictionary<string, PlannedUnit> current,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            var files = new List<OutputFile>(unit.Outputs.Count);
            foreach (var file in unit.Outputs)
            {
                var bytes = PlanCapture.NeedsBlob(file) ? await _services.Plans.ReadBlobAsync(planId, file.ContentHash, ct).ConfigureAwait(false) : null;
                if (PlanCapture.NeedsBlob(file) && bytes is null)
                    throw new IOException($"Plan {planId} lost the blob of {file.Path} while it was being applied.");
                files.Add(new OutputFile(file.Path, bytes ?? ReadOnlyMemory<byte>.Empty, file.ContentHash, file.ManifestHash, file.Mode, file.Role, file.Root,
                    ContentOmitted: bytes is null));
            }

            var rendered = new RenderedUnit(current[unit.Key], [], unit.ReadKeys, unit.InputHash, [], false);
            yield return new ProcessedUnit(rendered, files, [], false);
        }
    }
}

/// <summary>What a run does when the run lock is held.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LockMode>))]
public enum LockMode
{
    /// <summary>Poll until free: <c>wait</c>.</summary>
    [JsonStringEnumMemberName("wait")] Wait,

    /// <summary>Return <see cref="RunOutcome.Busy"/>: <c>fail</c>.</summary>
    [JsonStringEnumMemberName("fail")] Fail,
}

/// <summary>A generation request.</summary>
public sealed record GenerationRequest
{
    /// <summary>The mode.</summary>
    public GenerationMode Mode { get; init; } = GenerationMode.Apply;

    /// <summary>Pack names; <see langword="null"/> means every enabled pack.</summary>
    public IReadOnlyList<string>? Packs { get; init; }

    /// <summary>Whether to ignore the unit cache.</summary>
    public bool Force { get; init; }

    /// <summary>Parallelism for this run; <see langword="null"/> uses the engine's.</summary>
    public int? Jobs { get; init; }

    /// <summary>A hand-edit policy overriding the settings.</summary>
    public HandEditPolicy? HandEdits { get; init; }

    /// <summary>Whether to compute unified diffs (dry run).</summary>
    public bool IncludeDiffs { get; init; }

    /// <summary>Which roots to cover.</summary>
    public RootSelection Roots { get; init; } = RootSelection.All;

    /// <summary>What to do when the run lock is held.</summary>
    public LockMode Lock { get; init; } = LockMode.Wait;

    /// <summary>Whether stages 6 to 8 finish one after another (benchmark only).</summary>
    public bool StageBarriers { get; init; }
}

/// <summary>The outcome of a run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunOutcome>))]
public enum RunOutcome
{
    /// <summary><c>succeeded</c> (exit 0).</summary>
    [JsonStringEnumMemberName("succeeded")] Succeeded,

    /// <summary>Validation, template or script errors: <c>invalid</c> (exit 1).</summary>
    [JsonStringEnumMemberName("invalid")] Invalid,

    /// <summary><c>--check</c> found stale, missing or orphaned files: <c>drift</c> (exit 2).</summary>
    [JsonStringEnumMemberName("drift")] Drift,

    /// <summary>Hand-edit or region conflicts: <c>conflicts</c> (exit 3).</summary>
    [JsonStringEnumMemberName("conflicts")] Conflicts,

    /// <summary>The run lock was held and <see cref="LockMode.Fail"/> was requested: <c>busy</c> (exit 4).</summary>
    [JsonStringEnumMemberName("busy")] Busy,

    /// <summary>A plan's inputs changed: <c>stale</c>.</summary>
    [JsonStringEnumMemberName("stale")] Stale,

    /// <summary><c>cancelled</c> (exit 4).</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,

    /// <summary>An internal error: <c>failed</c> (exit 4).</summary>
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>The result of a run.</summary>
/// <param name="RunId">The run id.</param>
/// <param name="Mode">The mode.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Changes">File decisions, except unchanged ones, sorted by path.</param>
/// <param name="UnitsRendered">Units rendered.</param>
/// <param name="UnitsSkipped">Units skipped.</param>
/// <param name="FilesWritten">Files written.</param>
/// <param name="FilesDeleted">Files deleted.</param>
/// <param name="Diagnostics">Diagnostics.</param>
/// <param name="Timings">Per-stage timings.</param>
public sealed record GenerationResult(
    string RunId,
    GenerationMode Mode,
    RunOutcome Outcome,
    IReadOnlyList<FileChange> Changes,
    int UnitsRendered,
    int UnitsSkipped,
    int FilesWritten,
    int FilesDeleted,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<StageTiming> Timings);

/// <summary>A unit recorded in a plan.</summary>
/// <param name="Key">The unit key.</param>
/// <param name="InputHash">The input hash at plan time.</param>
/// <param name="ReadKeys">The read keys, ordinal.</param>
/// <param name="Skipped">Whether the unit was skipped at plan time (its <paramref name="Outputs"/> come from its stored state).</param>
/// <param name="Outputs">Every file the unit produces, unchanged ones included, so apply can feed the writer without rendering again.</param>
public sealed record PlanUnit(string Key, string InputHash, IReadOnlyList<string> ReadKeys, bool Skipped, IReadOnlyList<PlanFile> Outputs);

/// <summary>One output file of a planned unit.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="ContentHash">The hash of the post-processed bytes; added and modified files keep them in <c>blobs/&lt;ContentHash&gt;</c>.</param>
/// <param name="ManifestHash">The manifest hash (<c>r:</c> and <c>o:</c> prefixes as in engine-design.md section 12.2).</param>
/// <param name="Mode">The output mode.</param>
/// <param name="Role">The file's role.</param>
/// <param name="Root">The containing output root.</param>
/// <param name="DiskHashAtPlan">The content hash of the file on disk when planned; <see langword="null"/> when it did not exist.</param>
public sealed record PlanFile(string Path, string ContentHash, string ManifestHash, OutputMode Mode, FileRole Role, OutputRootInfo Root, string? DiskHashAtPlan);

/// <summary>A persisted plan (<c>CacheDirectory/plans/&lt;id&gt;/plan.json</c>).</summary>
/// <param name="Id">The plan's ULID.</param>
/// <param name="Request">The request the plan was made with (packs, roots, hand-edit policy, force, lock mode); apply reuses it.</param>
/// <param name="ModelVersion">The snapshot version planned against.</param>
/// <param name="Packs">The packs.</param>
/// <param name="Units">The units.</param>
/// <param name="Changes">The file decisions; with the units' outputs, the complete list of paths apply may touch.</param>
/// <param name="Diagnostics">Diagnostics.</param>
public sealed record GenerationPlan(
    string Id,
    GenerationRequest Request,
    long ModelVersion,
    IReadOnlyList<string> Packs,
    IReadOnlyList<PlanUnit> Units,
    IReadOnlyList<FileChange> Changes,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The result of planning.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Plan">The plan, when one was produced.</param>
public sealed record PlanResult(RunOutcome Outcome, GenerationPlan? Plan);

/// <summary>The result of applying a plan.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="StaleUnits">Unit keys whose inputs changed, or that were added or removed (when stale).</param>
/// <param name="StalePaths">Planned paths whose disk content changed since the plan (when stale).</param>
/// <param name="Result">The run result, when applied.</param>
public sealed record ApplyResult(RunOutcome Outcome, IReadOnlyList<string> StaleUnits, IReadOnlyList<string> StalePaths, GenerationResult? Result);

/// <summary>The result of a preview.</summary>
/// <param name="Files">The rendered files.</param>
/// <param name="Diagnostics">Diagnostics.</param>
public sealed record PreviewResult(IReadOnlyList<RenderedFile> Files, IReadOnlyList<Diagnostic> Diagnostics);
