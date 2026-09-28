using System.Collections.Concurrent;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>
/// Publishes the editor's realtime events (phase2-design.md section 3.6): <c>model.changed</c>, <c>validation.completed</c>,
/// <c>project.changed</c>, <c>job.progress</c>, <c>job.completed</c> and <c>presence.changed</c>, each within the hub's 256 KB, and keeps
/// the background services' state for <c>GET /api/health</c>.
/// </summary>
public sealed class EditorEvents
{
    /// <summary>How long validation waits for quiet after the last change.</summary>
    public static readonly TimeSpan ValidationQuiet = TimeSpan.FromMilliseconds(750);

    private readonly IRealtime _realtime;
    private readonly ModelStore _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Lock _gate = new();
    private long _changes;
    private string? _lastSettingsHash;

    /// <summary>Creates the publisher.</summary>
    /// <param name="realtime">The site's realtime side.</param>
    /// <param name="store">The model store.</param>
    /// <param name="loggers">The loggers.</param>
    public EditorEvents(IRealtime realtime, ModelStore store, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _realtime = realtime ?? throw new ArgumentNullException(nameof(realtime));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = loggers.CreateLogger("maquettiste.events");
    }

    /// <summary>The job worker's state: <c>running</c> or <c>stopped</c>.</summary>
    public string Worker { get; set; } = "stopped";

    /// <summary>The model watcher's state: <c>watching</c>, <c>polling</c> (the file watcher failed; only the rescan runs) or <c>stopped</c>.</summary>
    public string Watcher { get; set; } = "stopped";

    /// <summary>Whether the last attempt to load the model failed (the model folder cannot be read).</summary>
    public bool LoadFailed { get; set; }

    /// <summary>Publishes <c>model.changed</c> (the change set cut to 200 KB) to every connection, then signals the validation loop.</summary>
    /// <param name="set">What changed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async ValueTask OnModelChangedAsync(ChangeSet set, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set);
        await _realtime.PublishAsync("model.changed", set.TruncateTo(Api.MaxEventBytes), Api.JsonOptions, ct).ConfigureAwait(false);
        SignalValidation();
    }

    /// <summary>
    /// Publishes <c>project.changed</c> after <c>maquettiste.json</c> changed, unless <paramref name="settingsHash"/> is the one last
    /// published (a settings save and the watcher's report of the same write yield one event), then signals the validation loop.
    /// </summary>
    /// <param name="settingsHash">The settings hash now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task OnSettingsChangedAsync(string settingsHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settingsHash);
        lock (_gate)
        {
            if (string.Equals(_lastSettingsHash, settingsHash, StringComparison.Ordinal))
                return;
            _lastSettingsHash = settingsHash;
        }

        await _realtime.PublishAsync("project.changed", new ProjectChangedEvent(settingsHash), Api.JsonOptions, ct).ConfigureAwait(false);
        SignalValidation();
    }

    /// <summary>
    /// Publishes <c>project.changed</c> after a pack or an extension schema changed on disk. These leave the settings hash as it was, so
    /// nothing is deduplicated; then signals the validation loop.
    /// </summary>
    /// <param name="settingsHash">The settings hash now.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task OnProjectFilesChangedAsync(string settingsHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settingsHash);
        lock (_gate)
            _lastSettingsHash = settingsHash;
        await _realtime.PublishAsync("project.changed", new ProjectChangedEvent(settingsHash), Api.JsonOptions, ct).ConfigureAwait(false);
        SignalValidation();
    }

    /// <summary>
    /// Waits for a change, then for <see cref="ValidationQuiet"/> without another, validates the whole model and publishes
    /// <c>validation.completed</c> (the report cut to 200 KB; the counts stay whole, PD13). Runs until cancelled.
    /// </summary>
    /// <param name="ct">Stops the loop.</param>
    /// <returns>A task that completes when cancelled.</returns>
    public async Task RunValidationLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                long seen;
                do
                {
                    seen = Interlocked.Read(ref _changes);
                    await Task.Delay(ValidationQuiet, ct).ConfigureAwait(false);
                }
                while (Interlocked.Read(ref _changes) != seen);

                // Signals that arrived while waiting for quiet are covered by this run.
                _signal.Wait(0, CancellationToken.None);
                try
                {
                    var report = await _store.ValidateAsync(ValidationScope.All, ct).ConfigureAwait(false);
                    await _realtime.PublishAsync("validation.completed", report.TruncateTo(Api.MaxEventBytes), Api.JsonOptions, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "maquettiste: validation after a model change failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Publishes <c>job.progress</c> (the running job with its latest progress) to <c>job:{id}</c>.</summary>
    /// <param name="job">The job.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task PublishJobProgressAsync(JobInfo job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return _realtime.PublishToGroupAsync("job:" + job.Id, "job.progress", job, Api.JsonOptions, ct);
    }

    /// <summary>Publishes <c>job.completed</c> to <c>job:{id}</c>: the bounded <see cref="JobCompletedEvent"/>, never the record.</summary>
    /// <param name="job">The finished job, as the queue's completion handler receives it (untrimmed).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task PublishJobCompletedAsync(JobInfo job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return _realtime.PublishToGroupAsync("job:" + job.Id, "job.completed", JobCompletedEvent.From(job), Api.JsonOptions, ct);
    }

    /// <summary>Publishes <c>presence.changed</c> (every live editor connection and its selection) to <c>editors</c>.</summary>
    /// <param name="presence">The presence registry.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task PublishPresenceAsync(PresenceRegistry presence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(presence);
        var payload = PresenceChangedEvent.Bounded(presence.Live(_realtime), Api.MaxEventBytes);
        try
        {
            await _realtime.PublishToGroupAsync(RealtimeHooks.EditorsGroup, "presence.changed", payload, Api.JsonOptions, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // The host refuses a payload over 256 KB; one bad publish must not fail the report that caused it.
            _logger.LogWarning(ex, "maquettiste: presence.changed was not published");
        }
    }

    private void SignalValidation()
    {
        Interlocked.Increment(ref _changes);
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }
}

/// <summary>The <c>project.changed</c> payload.</summary>
/// <param name="SettingsHash">The hash of <c>maquettiste.json</c> now.</param>
public sealed record ProjectChangedEvent(string SettingsHash);

/// <summary>The <c>presence.changed</c> payload.</summary>
/// <param name="Editors">Every live editor connection and its selection.</param>
public sealed record PresenceChangedEvent(IReadOnlyList<PresenceEntry> Editors)
{
    /// <summary>The event for <paramref name="entries"/>, keeping entries in order while the serialized event stays within <paramref name="maxBytes"/>.</summary>
    /// <param name="entries">The live entries.</param>
    /// <param name="maxBytes">The payload budget.</param>
    /// <returns>The event.</returns>
    public static PresenceChangedEvent Bounded(IReadOnlyList<PresenceEntry> entries, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var kept = new List<PresenceEntry>(entries.Count);
        var size = 32; // {"editors":[]} and slack
        foreach (var entry in entries)
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(entry, Api.JsonOptions).Length + 1;
            if (size + bytes > maxBytes)
                continue;
            size += bytes;
            kept.Add(entry);
        }

        return new PresenceChangedEvent(kept);
    }
}

/// <summary>
/// The <c>job.completed</c> payload (phase2-design.md section 3.6): a fixed-size summary of a finished job. A finished
/// <see cref="JobInfo"/> keeps its stale lists, the plan request and every diagnostic, which at S13 scale run to megabytes, and the hub
/// refuses payloads above 256 KB; the editor reads <c>GET /api/jobs/{id}</c> for the record.
/// </summary>
/// <param name="Id">The job id.</param>
/// <param name="Kind">Plan or apply.</param>
/// <param name="State">The job's state: <c>succeeded</c> whenever the run completed, whatever its outcome.</param>
/// <param name="Outcome"><c>planResult.outcome</c> or <c>applyResult.outcome</c>; <see langword="null"/> when the job ended without a result.</param>
/// <param name="PlanId">The plan a plan job produced; <see langword="null"/> for an apply job.</param>
/// <param name="Error">The job's error, cut to 2,000 characters.</param>
/// <param name="Counts">The sizes of the lists the full record holds.</param>
/// <param name="FinishedUtc">When the job finished.</param>
public sealed record JobCompletedEvent(string Id, JobKind Kind, JobState State, RunOutcome? Outcome, string? PlanId, string? Error, JobCounts Counts,
    DateTimeOffset? FinishedUtc)
{
    /// <summary>The longest error the event carries.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>Summarizes a finished job from the full record the queue's completion handler receives.</summary>
    /// <param name="job">The job.</param>
    /// <returns>The summary.</returns>
    public static JobCompletedEvent From(JobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var plan = job.PlanResult?.Plan;
        var result = job.ApplyResult?.Result;
        var diagnostics = plan?.Diagnostics ?? result?.Diagnostics ?? [];
        var counts = new JobCounts(
            plan?.Changes.Count ?? result?.Changes.Count ?? 0,
            job.ApplyResult?.StaleUnits.Count ?? 0,
            job.ApplyResult?.StalePaths.Count ?? 0,
            diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error),
            diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning));
        var error = job.Error is { Length: > MaxErrorLength } text ? text[..MaxErrorLength] : job.Error;
        return new JobCompletedEvent(job.Id, job.Kind, job.State, job.PlanResult?.Outcome ?? job.ApplyResult?.Outcome,
            job.Kind == JobKind.Plan ? plan?.Id : null, error, counts, job.FinishedUtc);
    }
}

/// <summary>Sizes of the lists a finished job holds, counted from the full record before the engine trims it.</summary>
/// <param name="Changes">File decisions in the plan (plan job) or the run result (apply job).</param>
/// <param name="StaleUnits">Stale units of an apply.</param>
/// <param name="StalePaths">Stale paths of an apply.</param>
/// <param name="Errors">Error diagnostics of the plan or the run result.</param>
/// <param name="Warnings">Warning diagnostics of the plan or the run result.</param>
public sealed record JobCounts(int Changes, int StaleUnits, int StalePaths, int Errors, int Warnings);

/// <summary>One editor window's selection.</summary>
/// <param name="ConnectionId">The realtime connection.</param>
/// <param name="User">Its user.</param>
/// <param name="ElementId">The selected element, if any.</param>
/// <param name="Workspace">The workspace, if any.</param>
/// <param name="UpdatedUtc">When it was reported.</param>
public sealed record PresenceEntry(string ConnectionId, string User, string? ElementId, string? Workspace, DateTimeOffset UpdatedUtc);

/// <summary>Which element each open editor window has selected (PD14), by realtime connection.</summary>
public sealed class PresenceRegistry
{
    private readonly ConcurrentDictionary<string, PresenceEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Stores a window's selection.</summary>
    /// <param name="entry">The entry.</param>
    public void Report(PresenceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.ConnectionId] = entry;
    }

    /// <summary>The entries whose connection is still connected, by connection id.</summary>
    /// <param name="realtime">The site's realtime side.</param>
    /// <returns>The entries.</returns>
    public IReadOnlyList<PresenceEntry> Live(IRealtime realtime)
    {
        ArgumentNullException.ThrowIfNull(realtime);
        var live = realtime.Connections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return [.. _entries.Values.Where(e => live.Contains(e.ConnectionId)).OrderBy(e => e.ConnectionId, StringComparer.Ordinal)];
    }

    /// <summary>Forgets the entries of connections that went away.</summary>
    /// <param name="realtime">The site's realtime side.</param>
    /// <returns><see langword="true"/> when an entry was removed.</returns>
    public bool Prune(IRealtime realtime)
    {
        ArgumentNullException.ThrowIfNull(realtime);
        var live = realtime.Connections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var removed = false;
        foreach (var id in _entries.Keys)
        {
            if (!live.Contains(id))
                removed |= _entries.TryRemove(id, out _);
        }

        return removed;
    }
}
