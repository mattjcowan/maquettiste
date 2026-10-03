using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Maquettiste.Engine.Jobs;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine;

/// <summary>
/// A bounded queue of generation jobs, one running at a time (host-contracts requirements 23 to 28; W6). The host's
/// <c>[BackgroundService]</c> awaits <see cref="RunAsync"/>.
/// </summary>
/// <remarks>
/// <para>Queued and running jobs live in memory, so <see cref="TryEnqueue"/>, <see cref="Cancel"/> and <see cref="GetAsync"/> for them
/// never touch the disk on the caller's thread. Records are written to <c>CacheDirectory/jobs/&lt;id&gt;.json</c> (with the request)
/// by a background loop that <see cref="RunAsync"/> runs: when a job is queued, when it starts and when it finishes. Timestamps come from
/// <see cref="EngineOptions.TimeProvider"/>. A persisted plan job keeps its plan's id, request, changes and diagnostics but not its
/// per-unit list (<see cref="GenerationPlan.Units"/> is emptied; <see cref="GenerationService.GetPlanAsync"/> has the full plan).</para>
/// <para>Resume after a restart: the first <see cref="RunAsync"/> queues again, ahead of anything new and in their original order,
/// the persisted jobs that were queued or running when the previous process stopped. A job that is running when the queue stops
/// (<see cref="RunAsync"/>'s token or <see cref="DisposeAsync"/>) is left recorded as running, so the next process runs it again,
/// and the generation run journal makes that run resume rather than see hand edits. Only <see cref="Cancel"/> records a job as
/// cancelled.</para>
/// </remarks>
public sealed class JobQueue : IAsyncDisposable
{
    private readonly GenerationService _generation;
    private readonly EngineOptions _options;
    private readonly int _capacity;
    private readonly JobStore _store;
    private readonly Lock _gate = new();
    private readonly LinkedList<JobEntry> _queued = new();
    private readonly Dictionary<string, JobEntry> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobEntry> _unsaved = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cleared = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Channel<(JobEntry Entry, JobRecord Record, bool Final)> _persist =
        Channel.CreateUnbounded<(JobEntry, JobRecord, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<(JobEntry Entry, ProgressUpdate Update)> _progress =
        Channel.CreateBounded<(JobEntry, ProgressUpdate)>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _disposed = new();
    private ImmutableList<Func<JobInfo, ProgressUpdate, ValueTask>> _progressHandlers = [];
    private ImmutableList<Func<JobInfo, ValueTask>> _completedHandlers = [];
    private int _resumed;
    private int _finishedSincePrune;

    /// <summary>Creates a queue.</summary>
    /// <param name="generation">The generation service.</param>
    /// <param name="options">The engine options.</param>
    /// <param name="capacity">The maximum number of queued jobs.</param>
    public JobQueue(GenerationService generation, EngineOptions options, int capacity = 16)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _generation = generation;
        _options = options;
        _capacity = capacity;
        _store = generation.Services.Jobs;
    }

    /// <summary>Queues a job.</summary>
    /// <param name="request">The request.</param>
    /// <param name="job">The queued job.</param>
    /// <returns><see langword="false"/> when the queue is full.</returns>
    /// <exception cref="ArgumentException">A plan job without <see cref="JobRequest.Plan"/>, or an apply job without <see cref="JobRequest.PlanId"/>.</exception>
    public bool TryEnqueue(JobRequest request, [NotNullWhen(true)] out JobInfo? job)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == JobKind.Plan && request.Plan is null)
            throw new ArgumentException("A plan job needs a generation request.", nameof(request));
        if (request.Kind == JobKind.Apply && string.IsNullOrEmpty(request.PlanId))
            throw new ArgumentException("An apply job needs a plan id.", nameof(request));
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);

        JobEntry entry;
        lock (_gate)
        {
            if (_queued.Count >= _capacity)
            {
                job = null;
                return false;
            }

            entry = new JobEntry(_options.EffectiveIdGenerator.NewId(), request, Now);
            _queued.AddLast(entry);
            _live[entry.Id] = entry;
            job = entry.ToInfo(_queued.Count - 1);
            Persist(entry, final: false);
        }

        _signal.Release();
        return true;
    }

    /// <summary>
    /// Returns a job. Queued and running jobs are answered from memory without I/O; finished jobs persist in
    /// <c>CacheDirectory/jobs/&lt;id&gt;.json</c> (host-contracts requirement 27) and are read from there after a redeploy.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The job, or <see langword="null"/>.</returns>
    public async Task<JobInfo?> GetAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
        {
            if (_live.TryGetValue(id, out var live))
                return Info(live);
            if (_unsaved.TryGetValue(id, out var unsaved))
                return Trim(unsaved.ToInfo(null));
        }

        return (await _store.LoadAsync(id, ct).ConfigureAwait(false))?.Job;
    }

    /// <summary>Lists queued, running and recently finished jobs (finished ones read from <c>CacheDirectory/jobs/</c>).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The jobs, newest first.</returns>
    public async Task<IReadOnlyList<JobInfo>> ListAsync(CancellationToken ct)
    {
        var jobs = new Dictionary<string, JobInfo>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var entry in _live.Values)
                jobs[entry.Id] = Info(entry);
            foreach (var entry in _unsaved.Values)
                jobs.TryAdd(entry.Id, Trim(entry.ToInfo(null)));
        }

        foreach (var record in await _store.ListAsync(ct).ConfigureAwait(false))
            jobs.TryAdd(record.Job.Id, record.Job);
        return [.. jobs.Values.OrderByDescending(j => j.QueuedUtc).ThenByDescending(j => j.Id, StringComparer.Ordinal).Take(JobStore.Keep)];
    }

    /// <summary>Cancels a queued or running job.</summary>
    /// <param name="id">The job id.</param>
    /// <returns><see langword="true"/> when the job was found and not finished.</returns>
    public bool Cancel(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        JobEntry? cancelled = null;
        lock (_gate)
        {
            if (!_live.TryGetValue(id, out var entry))
                return false;
            if (entry.State == JobState.Queued)
            {
                _queued.Remove(entry);
                _live.Remove(entry.Id);
                entry.State = JobState.Cancelled;
                entry.FinishedUtc = Now;
                _unsaved[entry.Id] = entry;
                Persist(entry, final: true);
                cancelled = entry;
            }
            else
            {
                entry.CancelRequested = true;
                entry.Cancellation?.Cancel();
            }
        }

        if (cancelled is not null)
            _ = NotifyCompletedAsync(cancelled);
        return true;
    }

    /// <summary>
    /// Clears the run history: deletes every finished job record (succeeded, failed, cancelled) and every finished plan folder that no
    /// queued or running job refers to. A queued or running job, its record and the plan an apply job of it names are kept; so is a
    /// plan folder still being made (no <c>plan.json</c>), and, while a plan job runs, every plan no cleared job names (the running
    /// job's own plan is among them). Applying a plan needs its folder, so a plan not yet applied must be made again.
    /// </summary>
    /// <param name="ct">Cancellation, observed before anything is deleted.</param>
    /// <returns>How many job records and plans were removed.</returns>
    public async Task<JobHistoryCleared> ClearHistoryAsync(CancellationToken ct)
    {
        // Holding the persist lock, no record is written meanwhile: a job that finished but whose record is not written yet is in
        // _unsaved, and its pending records are skipped once it is marked cleared.
        await _persistLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await _store.ListAsync(ct).ConfigureAwait(false);
            HashSet<string> live;
            HashSet<string> keepPlans;
            bool planRunning;
            List<JobEntry> unsaved;
            lock (_gate)
            {
                live = [.. _live.Keys];
                keepPlans = [.. _live.Values.Select(e => e.Request.PlanId).OfType<string>()];
                planRunning = _live.Values.Any(e => e.Request.Kind == JobKind.Plan && e.State == JobState.Running);
                unsaved = [.. _unsaved.Values];
                foreach (var entry in unsaved)
                    _cleared.Add(entry.Id);
                _unsaved.Clear();
            }

            var cleared = unsaved.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var named = unsaved.Select(e => e.PlanResult?.Plan?.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var record in records)
            {
                var id = record.Job.Id;
                if (live.Contains(id))
                    continue;
                if (record.Job.State is JobState.Queued or JobState.Running && !cleared.Contains(id))
                {
                    // A job the next process resumes.
                    if (record.Request.PlanId is { } planId)
                        keepPlans.Add(planId);
                    continue;
                }

                if (record.Job.PlanResult?.Plan?.Id is { } made)
                    named.Add(made);
                if (Quietly(() => _store.Delete(id)))
                    cleared.Add(id);
            }

            var plans = 0;
            var store = _generation.Services.Plans;
            foreach (var planId in store.Ids())
            {
                if (keepPlans.Contains(planId) || !store.IsFinished(planId) || (planRunning && !named.Contains(planId)))
                    continue;
                if (Quietly(() =>
                    {
                        store.Delete(planId);
                        return true;
                    }))
                    plans++;
            }

            return new JobHistoryCleared(cleared.Count, plans);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    /// <summary>Runs jobs one at a time until stopped.</summary>
    /// <param name="stoppingToken">Stops the loop; a running job is cancelled and returns within one second.</param>
    /// <returns>A task that completes when stopped.</returns>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _disposed.Token);
        var token = stop.Token;
        var persister = PersistLoopAsync(token);
        var notifier = ProgressLoopAsync(token);
        try
        {
            if (Interlocked.Exchange(ref _resumed, 1) == 0)
                await ResumeAsync(token).ConfigureAwait(false);
            while (true)
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
                JobEntry? entry;
                lock (_gate)
                {
                    if (_queued.First is not { } first)
                        continue;
                    entry = first.Value;
                    _queued.RemoveFirst();
                    entry.State = JobState.Running;
                    entry.StartedUtc = Now;
                    entry.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    Persist(entry, final: false);
                }

                await ExecuteAsync(entry, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            await Quietly(notifier).ConfigureAwait(false);
            await Quietly(persister).ConfigureAwait(false);
        }
    }

    /// <summary>Subscribes to job progress.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A handle that unsubscribes.</returns>
    /// <remarks>Handlers run one after another on a background loop; when they fall behind, the oldest updates are dropped.</remarks>
    public IDisposable OnProgress(Func<JobInfo, ProgressUpdate, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ImmutableInterlocked.Update(ref _progressHandlers, list => list.Add(handler));
        return new Subscription(() => ImmutableInterlocked.Update(ref _progressHandlers, list => list.Remove(handler)));
    }

    /// <summary>Subscribes to job completion.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A handle that unsubscribes.</returns>
    public IDisposable OnCompleted(Func<JobInfo, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ImmutableInterlocked.Update(ref _completedHandlers, list => list.Add(handler));
        return new Subscription(() => ImmutableInterlocked.Update(ref _completedHandlers, list => list.Remove(handler)));
    }

    /// <inheritdoc/>
    /// <remarks>Stops <see cref="RunAsync"/>; a running job is cancelled and stays recorded as running, to be resumed by the next process.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested)
            return;
        await _disposed.CancelAsync().ConfigureAwait(false);
    }

    private DateTimeOffset Now => _options.EffectiveTimeProvider.GetUtcNow();

    private JobInfo Info(JobEntry entry)
    {
        var position = 0;
        foreach (var queued in _queued)
        {
            if (ReferenceEquals(queued, entry))
                return entry.ToInfo(position);
            position++;
        }

        return entry.ToInfo(null);
    }

    /// <summary>
    /// A finished job's record keeps no per-file or per-unit lists, which grow with the model (100,000 files at gate-1 scale): a plan
    /// without <see cref="GenerationPlan.Units"/> and <see cref="GenerationPlan.Changes"/> (<see cref="GenerationService.GetPlanAsync"/>
    /// has them), an apply result without <see cref="GenerationResult.Changes"/> (its counts stay). <c>OnCompleted</c> handlers get the
    /// full result.
    /// </summary>
    private static JobInfo Trim(JobInfo job)
    {
        if (job.PlanResult?.Plan is { } plan)
            job = job with { PlanResult = job.PlanResult with { Plan = plan with { Units = [], Changes = [] } } };
        if (job.ApplyResult?.Result is { } result)
            job = job with { ApplyResult = job.ApplyResult with { Result = result with { Changes = [] } } };
        return job;
    }

    /// <summary>Queues a record write; called under the lock, so records of one job are written in order.</summary>
    private void Persist(JobEntry entry, bool final) => _persist.Writer.TryWrite((entry, new JobRecord(Trim(Info(entry)), entry.Request), final));

    private async Task PersistLoopAsync(CancellationToken token)
    {
        await Task.Yield();
        try
        {
            while (await _persist.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                await DrainPersistAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopping: write what is already queued, so a finished job is not lost.
            await DrainPersistAsync().ConfigureAwait(false);
        }
    }

    private async Task DrainPersistAsync()
    {
        while (_persist.Reader.TryRead(out var item))
        {
            await _persistLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await PersistOneAsync(item.Entry, item.Record, item.Final).ConfigureAwait(false);
            }
            finally
            {
                _persistLock.Release();
            }
        }
    }

    private async Task PersistOneAsync(JobEntry entry, JobRecord record, bool final)
    {
        lock (_gate)
        {
            // ClearHistoryAsync removed this finished job before its records were written: they are dropped.
            if (_cleared.Contains(entry.Id))
            {
                if (final)
                    _cleared.Remove(entry.Id);
                return;
            }
        }

        try
        {
            await _store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is best effort: the job itself already ran.
        }

        if (!final)
            return;
        lock (_gate)
        {
            if (_unsaved.TryGetValue(entry.Id, out var unsaved) && ReferenceEquals(unsaved, entry))
                _unsaved.Remove(entry.Id);
        }

        if (Interlocked.Increment(ref _finishedSincePrune) % 32 == 0)
        {
            try
            {
                await _store.PruneAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task ProgressLoopAsync(CancellationToken token)
    {
        await Task.Yield();
        await foreach (var (entry, update) in _progress.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            JobInfo info;
            lock (_gate)
                info = Info(entry);
            foreach (var handler in _progressHandlers)
            {
                try
                {
                    await handler(info, update).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    // One subscriber must not break the others or the queue.
                }
            }
        }
    }

    private async Task ResumeAsync(CancellationToken token)
    {
        IReadOnlyList<JobRecord> records;
        try
        {
            records = await _store.ListAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var resume = records.Where(r => r.Job.State is JobState.Queued or JobState.Running)
            .OrderBy(r => r.Job.State == JobState.Running ? 0 : 1).ThenBy(r => r.Job.QueuedUtc).ThenBy(r => r.Job.Id, StringComparer.Ordinal).ToList();
        if (resume.Count == 0)
            return;
        lock (_gate)
        {
            var anchor = _queued.First;
            foreach (var record in resume)
            {
                if (_live.ContainsKey(record.Job.Id))
                    continue;
                var entry = JobEntry.Resume(record.Job, record.Request);
                if (anchor is null)
                    _queued.AddLast(entry);
                else
                    _queued.AddBefore(anchor, entry);
                _live[entry.Id] = entry;
                Persist(entry, final: false);
                _signal.Release();
            }
        }
    }

    private async Task ExecuteAsync(JobEntry entry, CancellationToken stopping)
    {
        var cancellation = entry.Cancellation!;
        var progress = new JobProgress(entry, _gate, _progress.Writer);
        JobState state;
        PlanResult? plan = null;
        ApplyResult? apply = null;
        string? error = null;
        try
        {
            if (entry.Request.Kind == JobKind.Plan)
            {
                plan = await _generation.PlanAsync(entry.Request.Plan!, progress, cancellation.Token).ConfigureAwait(false);
                state = StateOf(plan.Outcome);
                if (state == JobState.Failed)
                    error = "The plan could not be made.";
            }
            else
            {
                apply = await _generation.ApplyAsync(entry.Request.PlanId!, progress, cancellation.Token).ConfigureAwait(false);
                state = StateOf(apply.Outcome);
                if (state == JobState.Failed)
                    error = apply.StalePaths.Count > 0
                        ? $"Plan {entry.Request.PlanId} is missing stored content for {apply.StalePaths[0]}."
                        : $"Plan {entry.Request.PlanId} does not exist.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            state = JobState.Cancelled;
        }
        catch (Exception ex)
        {
            state = JobState.Failed;
            error = ex.Message;
        }

        JobEntry? finished = null;
        lock (_gate)
        {
            entry.Cancellation = null;
            _live.Remove(entry.Id);
            if (stopping.IsCancellationRequested && !entry.CancelRequested)
            {
                // Shutdown: the record stays "running", so the next process resumes the job.
            }
            else
            {
                entry.State = state;
                entry.PlanResult = plan;
                entry.ApplyResult = apply;
                entry.Error = error;
                entry.FinishedUtc = Now;
                _unsaved[entry.Id] = entry;
                Persist(entry, final: true);
                finished = entry;
            }
        }

        cancellation.Dispose();
        if (finished is not null)
            await NotifyCompletedAsync(finished).ConfigureAwait(false);
    }

    private static JobState StateOf(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Cancelled => JobState.Cancelled,
        RunOutcome.Failed => JobState.Failed,
        _ => JobState.Succeeded,
    };

    private async Task NotifyCompletedAsync(JobEntry entry)
    {
        JobInfo info;
        lock (_gate)
            info = entry.ToInfo(null);
        foreach (var handler in _completedHandlers)
        {
            try
            {
                await handler(info).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // One subscriber must not break the others or the queue.
            }
        }
    }

    /// <summary>Runs a deletion; a file in use or refused counts as not deleted.</summary>
    private static bool Quietly(Func<bool> delete)
    {
        try
        {
            return delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Records a job's latest progress and hands it to the progress loop without blocking the run.</summary>
    private sealed class JobProgress(JobEntry entry, Lock gate, ChannelWriter<(JobEntry, ProgressUpdate)> writer) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value)
        {
            lock (gate)
                entry.Progress = value;
            writer.TryWrite((entry, value));
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}

/// <summary>What <see cref="JobQueue.ClearHistoryAsync"/> removed.</summary>
/// <param name="Jobs">The finished job records removed.</param>
/// <param name="Plans">The stored plans removed.</param>
public sealed record JobHistoryCleared(int Jobs, int Plans);

/// <summary>A job kind.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobKind>))]
public enum JobKind
{
    /// <summary><c>plan</c>.</summary>
    [JsonStringEnumMemberName("plan")] Plan,

    /// <summary><c>apply</c>.</summary>
    [JsonStringEnumMemberName("apply")] Apply,
}

/// <summary>A job state.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState
{
    /// <summary><c>queued</c>.</summary>
    [JsonStringEnumMemberName("queued")] Queued,

    /// <summary><c>running</c>.</summary>
    [JsonStringEnumMemberName("running")] Running,

    /// <summary><c>succeeded</c>.</summary>
    [JsonStringEnumMemberName("succeeded")] Succeeded,

    /// <summary><c>failed</c>.</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary><c>cancelled</c>.</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>A job request.</summary>
/// <param name="Kind">The job kind.</param>
/// <param name="Plan">The generation request, for a plan job.</param>
/// <param name="PlanId">The plan to apply, for an apply job.</param>
public sealed record JobRequest(JobKind Kind, GenerationRequest? Plan, string? PlanId);

/// <summary>A job's state (host-contracts requirement 23).</summary>
/// <param name="Id">The job's ULID.</param>
/// <param name="Kind">The job kind.</param>
/// <param name="State">The state.</param>
/// <param name="QueuePosition">The 0-based position while queued.</param>
/// <param name="Progress">The latest progress while running.</param>
/// <param name="PlanResult">The result of a plan job.</param>
/// <param name="ApplyResult">The result of an apply job.</param>
/// <param name="Error">The error of a failed job.</param>
/// <param name="QueuedUtc">When the job was queued (from <see cref="EngineOptions.TimeProvider"/>).</param>
/// <param name="StartedUtc">When the job started running.</param>
/// <param name="FinishedUtc">When the job finished.</param>
public sealed record JobInfo(
    string Id,
    JobKind Kind,
    JobState State,
    int? QueuePosition,
    ProgressUpdate? Progress,
    PlanResult? PlanResult,
    ApplyResult? ApplyResult,
    string? Error,
    DateTimeOffset QueuedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? FinishedUtc);
