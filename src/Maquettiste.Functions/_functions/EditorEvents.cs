using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
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
    private ModelSnapshot? _lastSnapshot;

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

    /// <summary>
    /// Publishes <c>model.changed</c> to every connection, each change with the element's index row as the model holds it now (E5d), cut
    /// to 200 KB (<c>truncated</c> then true), then signals the validation loop.
    /// </summary>
    /// <param name="set">What changed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async ValueTask OnModelChangedAsync(ChangeSet set, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set);
        var cut = set.TruncateTo(Api.MaxEventBytes);
        var index = cut.Changed.Count == 0 ? [] : await _store.GetIndexAsync(ct).ConfigureAwait(false);
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var translations = set.Locales.Count == 0 ? null : Translations(snapshot, set.Locales);
        lock (_gate)
            _lastSnapshot = snapshot;
        await _realtime.PublishAsync("model.changed", ModelChangedEvent.Create(cut, index, Api.MaxEventBytes, translations), Api.JsonOptions, ct).ConfigureAwait(false);
        SignalValidation();
    }

    /// <summary>
    /// The <c>translations</c> of <c>model.changed</c> (reference-types-seeds-localization.md section 3.8): for each translated locale
    /// whose chain includes a changed locale, the display names that differ from the last snapshot published (every display name when
    /// no earlier snapshot is known), each the effective text after the change.
    /// </summary>
    private List<LocaleDisplayNames> Translations(ModelSnapshot snapshot, IReadOnlyList<string> changed)
    {
        ModelSnapshot? previous;
        lock (_gate)
            previous = _lastSnapshot;
        var l10n = snapshot.Localization;
        var list = new List<LocaleDisplayNames>();
        foreach (var locale in l10n.Locales.Where(l10n.IsTranslated))
        {
            if (!l10n.ChainOf(locale).Any(l => changed.Contains(l, StringComparer.Ordinal)))
                continue;
            var table = ModelReads.DisplayNames(snapshot, locale);
            var before = previous is not null && previous.Localization.IsTranslated(locale) ? ModelReads.DisplayNames(previous, locale) : null;
            var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, text) in table)
            {
                if (before is null || !before.TryGetValue(id, out var old) || old != text)
                    names[id] = text;
            }

            if (names.Count > 0)
                list.Add(new LocaleDisplayNames(locale, names));
        }

        return list;
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
    /// <param name="templates">The pack files that changed, by pack; <c>templates.changed</c> and <c>packs.changed</c> follow when any.</param>
    public async Task OnProjectFilesChangedAsync(string settingsHash, CancellationToken ct, IReadOnlyList<TemplatesChangedEvent>? templates = null)
    {
        ArgumentNullException.ThrowIfNull(settingsHash);
        lock (_gate)
            _lastSettingsHash = settingsHash;
        await _realtime.PublishAsync("project.changed", new ProjectChangedEvent(settingsHash), Api.JsonOptions, ct).ConfigureAwait(false);
        if (templates is { Count: > 0 })
            await OnPackFilesChangedAsync(templates, ct).ConfigureAwait(false);
        SignalValidation();
    }

    /// <summary>
    /// Publishes <c>templates.changed</c> once per pack, then <c>packs.changed</c> naming them (generation-ui.md section 5.1), so the
    /// editor reloads one file or one pack. Sent beside <c>project.changed</c>, never instead of it: the watcher sends both, and the
    /// functions' own pack writes send these two (the watcher's <c>project.changed</c> follows their disk change).
    /// </summary>
    /// <param name="templates">The changed files by pack.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task OnPackFilesChangedAsync(IReadOnlyList<TemplatesChangedEvent> templates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (templates.Count == 0)
            return;
        foreach (var change in templates.OrderBy(t => t.Pack, StringComparer.Ordinal))
            await _realtime.PublishAsync("templates.changed", change, Api.JsonOptions, ct).ConfigureAwait(false);
        var packs = templates.Select(t => t.Pack).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        await _realtime.PublishAsync("packs.changed", new PacksChangedEvent(packs), Api.JsonOptions, ct).ConfigureAwait(false);
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

/// <summary>The <c>templates.changed</c> payload: the files of one pack that changed.</summary>
/// <param name="Pack">The pack.</param>
/// <param name="Files">Pack-relative paths with their SHA-256 now, <see langword="null"/> when deleted; empty means "reload the pack".</param>
public sealed record TemplatesChangedEvent(string Pack, IReadOnlyList<TemplateFileHash> Files);

/// <summary>One changed pack file.</summary>
/// <param name="Path">The pack-relative path.</param>
/// <param name="Hash">The file's SHA-256 now, or <see langword="null"/> when it is gone.</param>
public sealed record TemplateFileHash(string Path, string? Hash);

/// <summary>The <c>packs.changed</c> payload.</summary>
/// <param name="Packs">The packs whose files changed, ordinal.</param>
public sealed record PacksChangedEvent(IReadOnlyList<string> Packs);

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

/// <summary>One change of <c>model.changed</c>: the engine's <see cref="ElementChange"/> plus the element's index row (E5d).</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Hash">The new file hash.</param>
/// <param name="Summary">The element's index row now, or <see langword="null"/> (left out) when it was deleted again before the event went out.</param>
public sealed record SummarizedChange(string Id, string Kind, string Path, string Hash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementSummary? Summary);

/// <summary>The display names of one locale in <c>model.changed</c> after a translation change.</summary>
/// <param name="Locale">The locale.</param>
/// <param name="DisplayNames">Element id to the effective display name.</param>
public sealed record LocaleDisplayNames(string Locale, IReadOnlyDictionary<string, string> DisplayNames);

/// <summary>The <c>model.changed</c> payload: the engine's <see cref="ChangeSet"/> shape with a summary on each change (E5d).</summary>
/// <param name="Changed">Changed and added elements.</param>
/// <param name="Deleted">Ids of deleted elements.</param>
/// <param name="Source">What caused the change.</param>
/// <param name="Truncated">Whether items were dropped to fit the size limit; the receiver refetches the index.</param>
public sealed record ModelChangedEvent(IReadOnlyList<SummarizedChange> Changed, IReadOnlyList<string> Deleted, ChangeSource Source, bool Truncated)
{
    /// <summary>The effective display names after a translation change, per affected locale (left out when none changed).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LocaleDisplayNames>? Translations { get; init; }

    /// <summary>Whether <see cref="Translations"/> was dropped to fit the size limit; the editor refetches the index in its content locale.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TranslationsTruncated { get; init; }

    /// <summary>Whether nothing changed (the engine's <see cref="ChangeSet.IsEmpty"/>, kept in the payload).</summary>
    public bool IsEmpty => Changed.Count == 0 && Deleted.Count == 0 && Translations is null && !TranslationsTruncated;

    /// <summary>
    /// Builds the payload of a change set already cut to <paramref name="maxJsonBytes"/>: each change gets its row from
    /// <paramref name="index"/>, then trailing items are dropped (and <see cref="Truncated"/> set) while the payload is over the limit.
    /// </summary>
    /// <param name="set">The change set.</param>
    /// <param name="index">The index now.</param>
    /// <param name="maxJsonBytes">The size limit.</param>
    /// <param name="translations">The display names of the translation change, or <see langword="null"/>; dropped (and
    /// <see cref="TranslationsTruncated"/> set) when the payload with them would be over the limit.</param>
    /// <returns>The payload.</returns>
    public static ModelChangedEvent Create(ChangeSet set, IReadOnlyList<ElementSummary> index, int maxJsonBytes, IReadOnlyList<LocaleDisplayNames>? translations = null)
    {
        var payload = CreateCore(set, index, maxJsonBytes);
        if (translations is not { Count: > 0 })
            return payload;
        var with = payload with { Translations = translations };
        return Size(with) <= maxJsonBytes ? with : payload with { TranslationsTruncated = true };
    }

    private static ModelChangedEvent CreateCore(ChangeSet set, IReadOnlyList<ElementSummary> index, int maxJsonBytes)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(index);
        var rows = new Dictionary<string, ElementSummary?>(set.Changed.Count, StringComparer.Ordinal);
        foreach (var change in set.Changed)
            rows[change.Id] = null;
        if (rows.Count > 0)
        {
            foreach (var summary in index)
            {
                if (rows.ContainsKey(summary.Id))
                    rows[summary.Id] = summary;
            }
        }

        var changed = set.Changed.Select(c => new SummarizedChange(c.Id, c.Kind, c.Path, c.Hash, rows[c.Id])).ToArray();
        var full = new ModelChangedEvent(changed, set.Deleted, set.Source, set.Truncated);
        if (Size(full) <= maxJsonBytes)
            return full;

        var total = changed.Length + set.Deleted.Count;
        int lo = 0, hi = total;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Size(full.Take(mid)) <= maxJsonBytes)
                lo = mid;
            else
                hi = mid - 1;
        }

        return full.Take(lo);
    }

    private ModelChangedEvent Take(int count) => new(
        [.. Changed.Take(Math.Min(count, Changed.Count))],
        [.. Deleted.Take(Math.Max(0, count - Changed.Count))],
        Source,
        true);

    private static int Size(ModelChangedEvent value) => JsonSerializer.SerializeToUtf8Bytes(value, Api.JsonOptions).Length;
}
