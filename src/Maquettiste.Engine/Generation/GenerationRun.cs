using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Generation;

/// <summary>What stages 1 to 4 produced for one run.</summary>
/// <param name="Snapshot">The snapshot.</param>
/// <param name="Resolved">The resolved model.</param>
/// <param name="Packs">The run's packs.</param>
/// <param name="SchemaDiffs">Schema diffs by database name (empty unless a run pack uses them).</param>
/// <param name="Plan">The unit plan.</param>
/// <param name="Manifests">The manifests, with any unfinished journal overlaid.</param>
/// <param name="Journal">The unfinished journal's records, if any.</param>
/// <param name="Hasher">Current dependency hashes.</param>
/// <param name="Paths">The run's output path policy.</param>
/// <param name="StatesLoaded">Completes when the run packs' unit states have been read (started beside resolve; the skip stage
/// awaits it before it loads them again from the store, which then reuses what it decoded).</param>
internal sealed record PreparedRun(
    ModelSnapshot Snapshot,
    ResolvedModel Resolved,
    PackSet Packs,
    IReadOnlyDictionary<string, SchemaDiffResult> SchemaDiffs,
    UnitPlan Plan,
    ManifestSet Manifests,
    IReadOnlyList<JournalRecord>? Journal,
    IDependencyHasher Hasher,
    IOutputPathPolicy Paths,
    Task? StatesLoaded = null)
{
    /// <summary>The stats of the outputs the loaded states record, taken beside resolve (<see cref="Planning.OutputStats"/>).</summary>
    public Task<Planning.OutputStats>? OutputStats { get; init; }
}

/// <summary>What stages 6 to 8 produced.</summary>
/// <param name="Summary">The writer's summary.</param>
/// <param name="Rendered">Units rendered.</param>
internal sealed record StreamResult(WriteSummary Summary, int Rendered);

/// <summary>
/// One generation run's stages over <see cref="EngineServices"/> (engine-design.md section 4.3): load, validate, pack load, resolve,
/// schema diffs and plan (<see cref="PrepareAsync"/>), skip (<see cref="SkipAsync"/>), then render, post-process and write as a
/// streaming pipeline (<see cref="StreamAsync"/>). Diagnostics of every stage accumulate in <see cref="Diagnostics"/>; timings in
/// <see cref="Clock"/>. The caller holds the run lock and owns the journal's begin and end.
/// </summary>
/// <param name="services">The services.</param>
/// <param name="store">The model store.</param>
/// <param name="progress">Progress.</param>
internal sealed class GenerationRun(EngineServices services, ModelStore store, IProgress<ProgressUpdate>? progress)
{
    private readonly ConcurrentQueue<Diagnostic> _diagnostics = new();

    /// <summary>Whether <see cref="VerifyFormattersAsync"/> checked every formatter the units to render could use.</summary>
    private bool _formattersVerified;

    /// <summary>The run's timings.</summary>
    public StageClock Clock { get; } = new();

    /// <summary>Every diagnostic so far, sorted.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => Outcomes.Sort(_diagnostics);

    /// <summary>Whether any diagnostic so far makes the run invalid.</summary>
    public bool Invalid => _diagnostics.Any(Outcomes.IsInvalid);

    /// <summary>Adds diagnostics.</summary>
    /// <param name="diagnostics">The diagnostics.</param>
    public void Add(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            _diagnostics.Enqueue(diagnostic);
    }

    /// <summary>
    /// Stages 1 to 4: journal read, load (the store rescans by stat), validate, pack load, resolve, schema diffs, plan, manifests.
    /// Returns <see langword="null"/> when a stage reported errors (the run is invalid; nothing was written).
    /// </summary>
    /// <remarks>
    /// Without stage barriers, resolution starts right after the load, beside validation and pack loading, since it needs only the
    /// snapshot. Its result is used only once validation and pack loading have passed, exactly where the sequential order would
    /// resolve, so diagnostics, outcomes and everything downstream are unchanged; when a stage before it fails, the speculative
    /// resolution is cancelled, awaited (so it never outlives the run or the run lock) and its result (or failure) ignored. The
    /// resolver therefore sees snapshots that validation would reject; <see cref="IModelResolver"/> requires it to terminate and
    /// observe cancellation on any loadable snapshot. With barriers (the benchmark's per-stage timings) the stages run
    /// one after another as before.
    /// </remarks>
    /// <param name="packNames">The requested packs, or <see langword="null"/> for every enabled pack.</param>
    /// <param name="mode">The run mode (check reports stale schema snapshots).</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="locked">Whether the caller holds the run lock; a preview does not, so it neither reads the journal (another run
    /// may have it open) nor overlays it.</param>
    /// <param name="barriers">Whether stages run one after another (<see cref="GenerationRequest.StageBarriers"/>).</param>
    /// <returns>The prepared run, or <see langword="null"/>.</returns>
    public async Task<PreparedRun?> PrepareAsync(IReadOnlyList<string>? packNames, GenerationMode mode, CancellationToken ct, bool locked = true,
        bool barriers = true)
    {
        var journal = locked ? await services.Journal.ReadUnfinishedAsync(ct).ConfigureAwait(false) : null;
        var snapshot = await Clock.TimeAsync(PipelineStage.Load, progress, () => store.GetSnapshotAsync(ct), s => s.Documents.Count).ConfigureAwait(false);

        var resolveStart = Clock.Now;
        var speculative = barriers ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ResolvedModel>? early = null;
        if (speculative is not null)
        {
            var token = speculative.Token;
            early = Observed(Task.Run(() => services.Resolver.ResolveAsync(snapshot, null, token), token));
            _ = early.ContinueWith(_ => speculative.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        var used = false;
        try
        {
            var start = Clock.Now;
            var report = await services.Validator.ValidateAsync(snapshot, new ValidationScope(), progress, ct).ConfigureAwait(false);
            Clock.Record(PipelineStage.Validate, start, snapshot.Documents.Count);
            Add(report.Diagnostics);
            if (report.HasErrors)
                return null;

            start = Clock.Now;
            var packs = await services.Packs.LoadAsync(snapshot, packNames, progress, ct).ConfigureAwait(false);
            Clock.Record(PipelineStage.Plan, start, packs.Packs.Count);
            Add(packs.Diagnostics);
            if (packs.Diagnostics.Any(Outcomes.IsInvalid))
                return null;

            // Reads that need neither the resolved model nor the plan go on beside resolve and plan: the manifests, and the unit states
            // the skip stage loads again (the store keeps what it decoded, so its second load reuses this one). A preview (not locked)
            // runs no skip stage, so it reads no states.
            var manifestsTask = Observed(Task.Run(() => services.Manifests.LoadAsync([], ct), ct));
            var statesTask = mode == GenerationMode.Check || !locked ? null
                : Observed(Task.Run(() => Task.WhenAll(packs.Packs.Select(p => services.UnitState.LoadAsync(p.Name, ct))), ct));
            Task? statesLoaded = statesTask;

            // The skip stage stats every output those states record; that too needs neither the resolved model nor the plan.
            var outputStats = statesTask is null ? null : Observed(Task.Run(async () =>
                Planning.OutputStats.Collect(services.Options.RepoRoot, await statesTask.ConfigureAwait(false), services.Options.EffectiveParallelism, ct), ct));

            ResolvedModel resolved;
            if (early is not null)
            {
                progress?.Report(new ProgressUpdate(PipelineStage.Resolve, 0, 0, null, null));
                used = true;
                resolved = await early.ConfigureAwait(false);
                Clock.Record(PipelineStage.Resolve, resolveStart, snapshot.Documents.Count);
                progress?.Report(new ProgressUpdate(PipelineStage.Resolve, snapshot.Documents.Count, snapshot.Documents.Count, null, null));
            }
            else
            {
                start = Clock.Now;
                resolved = await services.Resolver.ResolveAsync(snapshot, progress, ct).ConfigureAwait(false);
                Clock.Record(PipelineStage.Resolve, start, snapshot.Documents.Count);
            }

            Add(resolved.Diagnostics);
            if (resolved.Diagnostics.Any(Outcomes.IsInvalid))
                return null;

            var diffs = await SchemaDiffsAsync(snapshot, resolved, packs, mode, ct).ConfigureAwait(false);
            if (diffs is null)
                return null;

            start = Clock.Now;
            var plan = await services.Planner.PlanAsync(resolved, packs, services.Scripts, progress, ct).ConfigureAwait(false);
            Clock.Record(PipelineStage.Plan, start, plan.Units.Count);
            Add(plan.Diagnostics);
            if (plan.Diagnostics.Any(Outcomes.IsInvalid))
                return null;

            var manifests = await manifestsTask.ConfigureAwait(false);
            if (journal is not null)
                manifests = manifests.WithJournalOverlay(journal);
            var hasher = services.CreateHasher(snapshot, resolved, packs, diffs);
            return new PreparedRun(snapshot, resolved, packs, diffs, plan, manifests, journal, hasher, services.CreatePathPolicy(snapshot.Settings),
                statesLoaded)
            {
                OutputStats = outputStats,
            };
        }
        finally
        {
            if (!used && early is not null)
            {
                if (!early.IsCompleted)
                {
                    try
                    {
                        speculative!.Cancel(); // an earlier stage failed or threw: the speculative resolution is not needed
                    }
                    catch (ObjectDisposedException)
                    {
                        // it completed meanwhile
                    }
                }

                // Wait for it to stop (its result or failure is ignored), so no resolution of this run outlives it, or the run lock.
                await ((Task)early).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>Stage 5.</summary>
    /// <param name="run">The prepared run.</param>
    /// <param name="mode">The mode.</param>
    /// <param name="force">Whether to ignore the unit state.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The selection.</returns>
    public async Task<SkipResult> SkipAsync(PreparedRun run, GenerationMode mode, bool force, CancellationToken ct)
    {
        var start = Clock.Now;
        if (run.StatesLoaded is { } statesLoaded && !force && mode != GenerationMode.Check)
        {
            try
            {
                await statesLoaded.ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Only a head start: the change detector loads the states itself and meets any failure there.
            }
        }

        Planning.OutputStats? stats = null;
        if (run.OutputStats is { } outputStats && !force && mode != GenerationMode.Check)
        {
            try
            {
                stats = await outputStats.ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Only a head start: without it, each output is stat'ed during the check.
            }
        }

        var skip = services.ChangeDetector is Planning.ChangeDetector own
            ? await own.SelectAsync(run.Plan, run.Hasher, services.UnitState, run.Manifests, mode, force, progress, ct, stats).ConfigureAwait(false)
            : await services.ChangeDetector.SelectAsync(run.Plan, run.Hasher, services.UnitState, run.Manifests, mode, force, progress, ct)
                .ConfigureAwait(false);
        Clock.Record(PipelineStage.Skip, start, run.Plan.Units.Count);
        return skip;
    }

    /// <summary>
    /// Before anything is rendered, checks once the pinned version of every formatter a unit to render could use (MQ6008 errors):
    /// the formatters units name, and, when any unit chooses by extension (no <c>formatter</c>), every configured formatter with
    /// extensions, since output paths are known only after rendering (engine-design.md section 13: a mismatch fails the run before
    /// stage 7). When every check passes, the post-processor does not check again in this run.
    /// </summary>
    /// <param name="run">The prepared run.</param>
    /// <param name="units">The units to render.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="false"/> when a version does not match.</returns>
    public async Task<bool> VerifyFormattersAsync(PreparedRun run, IReadOnlyList<PlannedUnit> units, CancellationToken ct)
    {
        var configured = run.Snapshot.Settings.Formatters;
        var named = units.Select(u => u.Unit.Formatter).Where(n => n is not null && n != "none").ToHashSet(StringComparer.Ordinal);
        var byExtension = units.Any(u => u.Unit.Formatter is null);
        var used = configured.Where(f => named.Contains(f.Name) || (byExtension && f.Extensions.Count > 0)).ToList();
        if (used.Count == 0)
        {
            _formattersVerified = true;
            return true;
        }

        var failures = await services.Formatters.VerifyVersionsAsync(used, ct).ConfigureAwait(false);
        Add(failures.Select(d => d with { JsonPointer = ConfiguredPointer(d.JsonPointer, used, configured) }));
        _formattersVerified = !failures.Any(Outcomes.IsInvalid);
        return _formattersVerified;
    }

    /// <summary>Maps a <c>/formatters/&lt;i&gt;</c> pointer into the verified subset back to the index in <c>maquettiste.json</c>.</summary>
    private static string? ConfiguredPointer(string? pointer, List<FormatterSettings> used, IReadOnlyList<FormatterSettings> configured)
    {
        const string Prefix = "/formatters/";
        if (pointer is null || !pointer.StartsWith(Prefix, StringComparison.Ordinal)
            || !int.TryParse(pointer.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i >= used.Count)
            return pointer;
        for (var j = 0; j < configured.Count; j++)
        {
            if (ReferenceEquals(configured[j], used[i]))
                return Prefix + j.ToString(CultureInfo.InvariantCulture);
        }

        return pointer;
    }

    /// <summary>Builds the writer context of a run.</summary>
    /// <param name="run">The prepared run.</param>
    /// <param name="request">The request.</param>
    /// <param name="mode">The mode.</param>
    /// <param name="runId">The run id.</param>
    /// <param name="toRender">The units the writer will receive (per pack counts).</param>
    /// <param name="skipped">The skipped units.</param>
    /// <param name="journal">The journal (apply only).</param>
    /// <param name="plannedPaths">The plan's paths (plan apply only).</param>
    /// <returns>The context.</returns>
    public WriteContext WriteContext(PreparedRun run, GenerationRequest request, GenerationMode mode, string runId, IEnumerable<PlannedUnit> toRender,
        IReadOnlyList<SkippedUnit> skipped, IRunJournal? journal, IReadOnlySet<string>? plannedPaths)
    {
        var counts = run.Packs.Packs.ToDictionary(p => p.Name, _ => 0, StringComparer.Ordinal);
        foreach (var unit in toRender)
            counts[unit.Pack.Name] = counts.GetValueOrDefault(unit.Pack.Name) + 1;
        var policies = Policies(run, request, mode);

        return new WriteContext(mode, runId, policies, run.Manifests, skipped, counts, request.Packs is null, request.Roots,
            request.IncludeDiffs && mode != GenerationMode.Apply, mode == GenerationMode.Apply ? journal : null, services.UnitState, plannedPaths);
    }

    /// <summary>
    /// The effective hand-edit policy of every run pack and every pack with a manifest: <see cref="GenerationRequest.HandEdits"/>, else
    /// <c>packs.&lt;name&gt;.handEdits</c>, else <c>handEdits</c>; <see cref="HandEditPolicy.Fail"/> for every pack in check mode.
    /// </summary>
    /// <param name="run">The prepared run.</param>
    /// <param name="request">The request.</param>
    /// <param name="mode">The mode.</param>
    /// <returns>The policies by pack name, ordinal.</returns>
    public static SortedDictionary<string, HandEditPolicy> Policies(PreparedRun run, GenerationRequest request, GenerationMode mode)
    {
        var settings = run.Snapshot.Settings;
        var policies = new SortedDictionary<string, HandEditPolicy>(StringComparer.Ordinal);
        foreach (var pack in run.Manifests.Packs)
            policies[pack] = request.HandEdits ?? (settings.Packs.TryGetValue(pack, out var s) ? s.HandEdits : null) ?? settings.HandEdits;
        foreach (var pack in run.Packs.Packs)
            policies[pack.Name] = request.HandEdits ?? pack.Settings.HandEdits ?? settings.HandEdits;
        if (mode == GenerationMode.Check)
        {
            foreach (var pack in policies.Keys.ToList())
                policies[pack] = HandEditPolicy.Fail;
        }

        return policies;
    }

    /// <summary>
    /// Stages 6 to 8. Streaming (the default): the renderer's stream feeds a bounded channel of <c>2 × jobs</c> post-processing tasks
    /// (at most <c>jobs</c> running), read in order into the writer, whose own queue is bounded (256 files); so at any time only a
    /// bounded number of units exist between the renderer and the disk. With <see cref="GenerationRequest.StageBarriers"/> each
    /// stage finishes before the next starts.
    /// </summary>
    /// <param name="run">The prepared run.</param>
    /// <param name="toRender">The units to render, in plan order.</param>
    /// <param name="write">The writer context.</param>
    /// <param name="jobs">Render and post-processing workers.</param>
    /// <param name="barriers">Whether stages finish one after another.</param>
    /// <param name="hook">Called in the post-processing worker for each processed unit (plan capture, check).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    public async Task<StreamResult> StreamAsync(PreparedRun run, IReadOnlyList<PlannedUnit> toRender, WriteContext write, int jobs, bool barriers,
        Func<ProcessedUnit, CancellationToken, Task>? hook, CancellationToken ct)
    {
        var renderer = services.CreateRenderer();
        var writer = services.CreateWriter(run.Paths);
        var renderContext = new RenderContext(run.Resolved, run.Packs, run.SchemaDiffs, services.Scripts, run.Hasher, jobs);
        var postContext = new PostProcessContext(Path.GetFullPath(services.Options.RepoRoot), run.Paths, run.Snapshot.Settings.Formatters, _formattersVerified);
        var processor = new UnitProcessor(this, services.PostProcessor, postContext, hook, toRender.Count, progress);

        IAsyncEnumerable<ProcessedUnit> source;
        if (barriers)
        {
            var start = Clock.Now;
            var rendered = new List<RenderedUnit>(toRender.Count);
            await foreach (var unit in renderer.RenderAsync(toRender, renderContext, progress, ct).ConfigureAwait(false))
            {
                processor.RenderedOne();
                rendered.Add(unit);
            }

            Clock.Record(PipelineStage.Render, start, rendered.Count);
            var processed = new ProcessedUnit[rendered.Count];
            await Parallel.ForAsync(0, rendered.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct },
                async (i, token) => processed[i] = await processor.ProcessAsync(rendered[i], token).ConfigureAwait(false)).ConfigureAwait(false);
            source = Replay(processed, ct);
        }
        else
        {
            source = Pipeline(renderer.RenderAsync(toRender, renderContext, progress, ct), processor, jobs, ct);
        }

        var writeStart = Clock.Now;
        var summary = await writer.WriteAsync(Observe(source), write, progress, ct).ConfigureAwait(false);
        Clock.Record(PipelineStage.Write, writeStart, summary.Changes.Count);
        Add(summary.Diagnostics);
        return new StreamResult(summary, processor.Rendered);
    }

    /// <summary>Writes processed units that already exist (plan apply).</summary>
    /// <param name="run">The prepared run.</param>
    /// <param name="units">The units.</param>
    /// <param name="write">The writer context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The summary.</returns>
    public async Task<WriteSummary> WriteAsync(PreparedRun run, IAsyncEnumerable<ProcessedUnit> units, WriteContext write, CancellationToken ct)
    {
        var writer = services.CreateWriter(run.Paths);
        var start = Clock.Now;
        var summary = await writer.WriteAsync(units, write, progress, ct).ConfigureAwait(false);
        Clock.Record(PipelineStage.Write, start, summary.Changes.Count);
        Add(summary.Diagnostics);
        return summary;
    }

    /// <summary>Saves the schema snapshot of every database whose diff is not empty (after a successful apply of every root).</summary>
    /// <param name="run">The prepared run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SaveSnapshotsAsync(PreparedRun run, CancellationToken ct)
    {
        foreach (var database in run.Resolved.Databases)
        {
            if (run.SchemaDiffs.TryGetValue(database.Name, out var diff) && !diff.IsEmpty)
                await services.Snapshots.SaveAsync(services.SchemaDiffer.Capture(database, diff.ToRevision), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads each database's schema snapshot and diffs it against the model. A snapshot that cannot be read (corrupt JSON, or not a
    /// snapshot) is an MQ1001 error on that file and stops the run: generating migrations from a guessed base would be wrong.
    /// </summary>
    /// <returns>The diffs by database name, or <see langword="null"/> when a snapshot could not be read.</returns>
    private async Task<IReadOnlyDictionary<string, SchemaDiffResult>?> SchemaDiffsAsync(ModelSnapshot snapshot, ResolvedModel resolved, PackSet packs,
        GenerationMode mode, CancellationToken ct)
    {
        var diffs = new SortedDictionary<string, SchemaDiffResult>(StringComparer.Ordinal);
        if (!packs.Packs.Any(p => p.Manifest.UsesSchemaDiff))
            return diffs;
        var paths = new ModelPaths(services.Options);
        foreach (var database in resolved.Databases)
        {
            ct.ThrowIfCancellationRequested();
            PhysicalSnapshot? previous;
            try
            {
                previous = await services.Snapshots.LoadAsync(database.Name, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                Add([RuleCatalog.Create("MQ1001", ex.Message, database.Id,
                    paths.ToRepoPath("snapshots/" + SchemaDiff.SnapshotStore.Kebab(database.Name) + ".json"))]);
                return null;
            }

            var diff = services.SchemaDiffer.Diff(previous, database);
            diffs[database.Name] = diff;
            if (mode == GenerationMode.Check && !diff.IsEmpty)
            {
                Add([RuleCatalog.Create(Outcomes.StaleSnapshot,
                    $"The schema snapshot of database '{database.Name}' is stale: the model is at revision {diff.ToRevision}, the snapshot at {diff.FromRevision}.",
                    database.Id, paths.ToRepoPath("snapshots/" + SchemaDiff.SnapshotStore.Kebab(database.Name) + ".json"))]);
            }
        }

        return diffs;
    }

    /// <summary>
    /// Marks a background task's failure as observed, for tasks a run may abandon when a later stage reports errors; a task that is
    /// awaited still throws there.
    /// </summary>
    private static T Observed<T>(T task) where T : Task
    {
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private static async IAsyncEnumerable<ProcessedUnit> Replay(IReadOnlyList<ProcessedUnit> units, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            yield return unit;
            await Task.Yield();
        }
    }

    /// <summary>Collects each unit's diagnostics as the writer takes it.</summary>
    private async IAsyncEnumerable<ProcessedUnit> Observe(IAsyncEnumerable<ProcessedUnit> source)
    {
        await foreach (var unit in source.ConfigureAwait(false))
        {
            Add(unit.Rendered.Diagnostics);
            Add(unit.Diagnostics);
            yield return unit;
        }
    }

    /// <summary>
    /// The streaming part: a producer reads the renderer's stream, starts one post-processing task per unit (at most
    /// <paramref name="jobs"/> at a time) and queues the tasks in order on a channel of capacity <c>2 × jobs</c>; the consumer awaits
    /// them in order, so units reach the writer in plan order.
    /// </summary>
    private static async IAsyncEnumerable<ProcessedUnit> Pipeline(IAsyncEnumerable<RenderedUnit> rendered, UnitProcessor processor, int jobs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<Task<ProcessedUnit>>(new BoundedChannelOptions(Math.Max(2, 2 * jobs))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var slots = new SemaphoreSlim(jobs, jobs);
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var unit in rendered.WithCancellation(stop.Token).ConfigureAwait(false))
                {
                    processor.RenderedOne();
                    await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                    var task = ProcessAndReleaseAsync(processor, unit, slots, stop.Token);
                    try
                    {
                        await channel.Writer.WriteAsync(task, stop.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        await Quietly(task).ConfigureAwait(false);
                        throw;
                    }
                }

                processor.RenderingDone();
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var task in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return await task.ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Quietly(producer).ConfigureAwait(false);
            while (channel.Reader.TryRead(out var pending))
                await Quietly(pending).ConfigureAwait(false);
        }
    }

    private static async Task<ProcessedUnit> ProcessAndReleaseAsync(UnitProcessor processor, RenderedUnit unit, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            return await processor.ProcessAsync(unit, ct).ConfigureAwait(false);
        }
        finally
        {
            slots.Release();
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Observed: the run's own failure or cancellation is what the caller sees.
        }
    }

    /// <summary>Post-processes units, times them and reports progress.</summary>
    private sealed class UnitProcessor(GenerationRun run, IPostProcessor postProcessor, PostProcessContext context,
        Func<ProcessedUnit, CancellationToken, Task>? hook, int total, IProgress<ProgressUpdate>? progress)
    {
        private readonly TimeSpan _renderStart = run.Clock.Now;
        private int _rendered;
        private int _processed;
        private bool _renderRecorded;

        /// <summary>Units rendered so far.</summary>
        public int Rendered => Volatile.Read(ref _rendered);

        public void RenderedOne() => Interlocked.Increment(ref _rendered);

        public void RenderingDone()
        {
            if (_renderRecorded)
                return;
            _renderRecorded = true;
            run.Clock.Record(PipelineStage.Render, _renderStart, Rendered);
        }

        public async Task<ProcessedUnit> ProcessAsync(RenderedUnit unit, CancellationToken ct)
        {
            var start = run.Clock.Now;
            var processed = await postProcessor.ProcessAsync(unit, context, ct).ConfigureAwait(false);
            if (hook is not null)
                await hook(processed, ct).ConfigureAwait(false);
            run.Clock.Item(PipelineStage.PostProcess, start, run.Clock.Now - start);
            var done = Interlocked.Increment(ref _processed);
            progress?.Report(new ProgressUpdate(PipelineStage.PostProcess, done, total, processed.Files.Count > 0 ? processed.Files[0].Path : null,
                unit.Unit.Pack.Name));
            return processed;
        }
    }
}
