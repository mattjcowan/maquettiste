using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine;

/// <summary>
/// The long-lived, thread-safe model store (host-contracts requirements 6 to 19; W1). Constructible synchronously without I/O;
/// loading is explicit and idempotent; reads return immutable snapshots; saves return results instead of throwing.
/// </summary>
/// <remarks>
/// Reads never block: they return the current immutable <see cref="ModelSnapshot"/>. Loads, refreshes and writes are serialized by
/// one <see cref="SemaphoreSlim"/>. Every write plans the whole change in memory (schema, canonical bytes, file paths, expected
/// hashes, reference removal), checks the files it touches against their indexed hashes on disk, validates the changed elements and
/// their referrers through <see cref="IModelValidator"/> (only errors the change introduces refuse it), then stages every file and
/// renames it into place (<see cref="AtomicFileSet"/>), re-reads the written paths and publishes one <see cref="ChangeSet"/>.
/// </remarks>
public sealed partial class ModelStore : IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly EngineServices _services;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<ModelPaths> _paths;
    private readonly Lock _handlersLock = new();
    private ImmutableArray<Func<ChangeSet, CancellationToken, ValueTask>> _handlers = [];
    private volatile ModelSnapshot? _current;
    private volatile bool _disposed;
    private int _batchCounter;

    /// <summary>Creates a store. Performs no I/O and never throws for model content.</summary>
    /// <param name="options">The engine options.</param>
    public ModelStore(EngineOptions options)
        : this(options, EngineServices.Create(options))
    {
    }

    /// <summary>Creates a store over substitute services (tests).</summary>
    /// <param name="options">The engine options.</param>
    /// <param name="services">The services.</param>
    internal ModelStore(EngineOptions options, EngineServices services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        _options = options;
        _services = services;
        _paths = new Lazy<ModelPaths>(() => new ModelPaths(_options), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The current snapshot, or <see langword="null"/> before the first load.</summary>
    public ModelSnapshot? Current => _current;

    /// <summary>
    /// The files of the store's last load with the stat each was read at (the generation run's last-run record), or
    /// <see langword="null"/> before the first load or with a substitute loader.
    /// </summary>
    /// <returns>The stamps.</returns>
    internal Loading.LoadedFileStamps? LastFileStamps() => (_services.Loader as Loading.ModelLoader)?.LastFileStamps();

    /// <summary>Loads the model; idempotent under concurrent callers.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task LoadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not null)
            return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_current is null)
                await ReloadAsync(null, false, ChangeSource.Disk, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the current snapshot, loading on first call and rescanning by stat.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshot.</returns>
    public async Task<ModelSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null)
        {
            await LoadAsync(ct).ConfigureAwait(false);
            return _current!;
        }

        await RescanAsync(false, ct).ConfigureAwait(false);
        return _current!;
    }

    /// <summary>Returns a summary of every element.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The summaries.</returns>
    public async Task<IReadOnlyList<ElementSummary>> GetIndexAsync(CancellationToken ct) =>
        (await LoadedAsync(ct).ConfigureAwait(false)).Summaries();

    /// <summary>Returns an element's document.</summary>
    /// <param name="id">An element id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The document, or <see langword="null"/>.</returns>
    public async Task<ElementDocument?> GetElementAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        return (await LoadedAsync(ct).ConfigureAwait(false)).GetDocument(id);
    }

    /// <summary>Returns every reference to an id.</summary>
    /// <param name="id">An element or sub-element id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The references.</returns>
    public async Task<IReadOnlyList<ReferenceInfo>> GetReferencesAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        return (await LoadedAsync(ct).ConfigureAwait(false)).ReferencesTo(id);
    }

    /// <summary>Creates an element; an id is assigned when absent (host-contracts requirement 13).</summary>
    /// <param name="json">The element JSON.</param>
    /// <param name="source">What caused the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see cref="SaveOutcome.Saved"/> with the new id and hash, or <see cref="SaveOutcome.Invalid"/>.</returns>
    public async Task<SaveResult> CreateAsync(ReadOnlyMemory<byte> json, ChangeSource source, CancellationToken ct)
    {
        if (!TryParseRequest(json, null, out var node, out var invalid))
            return invalid;
        await DefaultSeedColumnsAsync(node, ct).ConfigureAwait(false);
        var result = await ExecuteAsync([new PlannedChange(BatchOp.Create, null, null, node, DeleteResolution.Refuse)], source, ct).ConfigureAwait(false);
        return result.Items[0];
    }

    /// <summary>Saves an element if its file still has the expected hash (host-contracts requirement 12).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="json">The element JSON.</param>
    /// <param name="expectedHash">The ETag the caller loaded.</param>
    /// <param name="source">What caused the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Saved, Conflict, Invalid or NotFound.</returns>
    public async Task<SaveResult> SaveAsync(string id, ReadOnlyMemory<byte> json, string expectedHash, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!TryParseRequest(json, id, out var node, out var invalid))
            return invalid;
        var result = await ExecuteAsync([new PlannedChange(BatchOp.Update, id, expectedHash, node, DeleteResolution.Refuse)], source, ct).ConfigureAwait(false);
        return result.Items[0];
    }

    /// <summary>Deletes an element (host-contracts requirement 13).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="expectedHash">The ETag the caller loaded.</param>
    /// <param name="resolution">What to do with references to it.</param>
    /// <param name="source">What caused the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Saved, Conflict, NotFound, Referenced (with referrers) or Invalid.</returns>
    public async Task<SaveResult> DeleteAsync(string id, string expectedHash, DeleteResolution resolution, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var result = await ExecuteAsync([new PlannedChange(BatchOp.Delete, id, expectedHash, null, resolution)], source, ct).ConfigureAwait(false);
        return result.Items[0];
    }

    /// <summary>Parses an untrusted batch, validated against <c>batch.json</c>, with no disk access (host-contracts requirement 16).</summary>
    /// <param name="json">The batch JSON.</param>
    /// <returns>The batch or diagnostics.</returns>
    public BatchParseResult ParseBatch(ReadOnlySpan<byte> json) => new BatchParser(_services.Schemas, _services.Json).Parse(json);

    /// <summary>Applies a batch all or nothing (host-contracts requirement 15).</summary>
    /// <param name="batch">The batch.</param>
    /// <param name="source">What caused the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result per operation and the overall outcome.</returns>
    /// <remarks>
    /// Deletes in a batch refuse while anything that survives the batch still references the element. An element may appear in one
    /// operation only, and each expected hash is checked against the file as it was before the batch. When the batch fails, nothing
    /// is written: failed operations carry their outcome and diagnostics, and the others report <see cref="SaveOutcome.Saved"/>
    /// with no hash and no changes.
    /// </remarks>
    public Task<BatchResult> ApplyBatchAsync(ModelBatch batch, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var changes = new List<PlannedChange>(batch.Operations.Count);
        var invalid = new SaveResult?[batch.Operations.Count];
        var schemaOps = false;
        for (var i = 0; i < batch.Operations.Count; i++)
        {
            var o = batch.Operations[i];
            JsonNode? node = null;
            if (o.Op == BatchOp.Translate)
            {
                var pointer = "/operations/" + i.ToString(CultureInfo.InvariantCulture) + "/op";
                invalid[i] = new SaveResult(SaveOutcome.Invalid, o.Id, null, null,
                    [RuleCatalog.Create("MQ1002", pointer + " The translate operation is declared in the contract; its handler lands in a later step.", o.Id, null, pointer)], [], null);
                changes.Add(new PlannedChange(BatchOp.Update, o.Id, o.ExpectedHash, null, DeleteResolution.Refuse));
                continue;
            }

            if (IsSchemaOperation(o.Op) || IsProcessOperation(o.Op))
            {
                schemaOps = true;
                continue;
            }

            // A hand-built operation may carry an element ParseBatch never saw: parse it as strictly as a request body.
            if (o.Element is { } e && !TryParseRequest(JsonMarshal.GetRawUtf8Value(e).ToArray(), o.Id, out node, out var failure))
                invalid[i] = failure with { Diagnostics = [.. failure.Diagnostics.Select(d => d with { JsonPointer = "/operations/" + i.ToString(CultureInfo.InvariantCulture) + "/element", Line = null, Column = null })] };
            changes.Add(new PlannedChange(o.Op, o.Id, o.ExpectedHash, node, o.Op == BatchOp.Delete ? o.Resolution ?? DeleteResolution.Refuse : DeleteResolution.Refuse));
        }

        if (invalid.Any(r => r is not null))
        {
            var items = invalid.Select((r, i) => r ?? new SaveResult(SaveOutcome.Saved, batch.Operations[i].Id, null, null, [], [], null)).ToList();
            return Task.FromResult(new BatchResult(SaveOutcome.Invalid, items, null));
        }

        if (schemaOps)
            return ApplyWithSchemasAsync(batch, changes, invalid, source, ct);

        if (changes.Count == 0)
            return Task.FromResult(new BatchResult(SaveOutcome.Saved, [], ChangeSet.Empty(source)));
        return ExecuteAsync(changes, source, ct);
    }

    /// <summary>Re-reads changed paths and updates the index (host-contracts requirement 17).</summary>
    /// <param name="paths">Repo-relative or absolute paths reported by a watcher.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What changed; the store's own writes yield nothing.</returns>
    public async Task<ChangeSet> RefreshAsync(IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null)
        {
            await LoadAsync(ct).ConfigureAwait(false);
            return ChangeSet.Empty(ChangeSource.Disk);
        }

        ChangeSet changes;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            changes = await ReloadAsync([.. paths], false, ChangeSource.Disk, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await NotifyAsync(changes, ct).ConfigureAwait(false);
        return changes;
    }

    /// <summary>Reconciles the whole model folder by content hash (host-contracts requirement 18).</summary>
    /// <param name="verify">Whether to re-hash every file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What changed.</returns>
    public async Task<ChangeSet> RescanAsync(bool verify, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ChangeSet changes;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var first = _current is null;
            changes = await ReloadAsync(null, verify, ChangeSource.Disk, ct).ConfigureAwait(false);
            if (first)
                return ChangeSet.Empty(ChangeSource.Disk);
        }
        finally
        {
            _gate.Release();
        }

        await NotifyAsync(changes, ct).ConfigureAwait(false);
        return changes;
    }

    /// <summary>Validates the current snapshot (host-contracts requirement 22).</summary>
    /// <param name="scope">What to validate.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report.</returns>
    /// <remarks>
    /// The validator's report, plus the snapshot's load diagnostics (MQ1xxx) for the scope that it did not already carry, so a
    /// file that failed to load is never silently missing from a report.
    /// </remarks>
    public async Task<ValidationReport> ValidateAsync(ValidationScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var report = await _services.Validator.ValidateAsync(snapshot, scope, null, ct).ConfigureAwait(false);
        var seen = report.Diagnostics.Select(LoadKey).ToHashSet(StringComparer.Ordinal);
        IEnumerable<Diagnostic> load = snapshot.LoadDiagnostics;
        if (scope.ElementIds is { } ids)
        {
            var owners = ids.Select(id => snapshot.TryGetEntry(id, out var e) ? e.OwnerId : id).ToHashSet(StringComparer.Ordinal);
            var files = owners.Select(id => snapshot.GetDocument(id)?.Path).OfType<string>().ToHashSet(StringComparer.Ordinal);
            load = load.Where(d => (d.ElementId is { } e && (owners.Contains(e) || ids.Contains(e))) || (d.FilePath is { } f && files.Contains(f)));
        }

        var extra = load.Where(d => !seen.Contains(LoadKey(d))).ToList();
        return extra.Count == 0 ? report : ValidationReport.From(report.Diagnostics.Concat(extra));
    }

    /// <summary>Reads <c>maquettiste.json</c> with its hash (E3, phase2-design.md section 3.8).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The settings document; when the file changed on disk behind the index, the store refreshes it first.</returns>
    public async Task<SettingsDocument> GetSettingsAsync(CancellationToken ct)
    {
        await LoadedAsync(ct).ConfigureAwait(false);
        var paths = _paths.Value;
        var repoPath = paths.ToRepoPath(ModelPaths.SettingsFile);
        for (var attempt = 0; ; attempt++)
        {
            var disk = await ReadSettingsFileAsync(ct).ConfigureAwait(false);
            var hash = ContentHash.Of(disk ?? []);
            var snapshot = _current!;
            if (string.Equals(hash, snapshot.SettingsHash, StringComparison.Ordinal) || attempt > 0)
                return SettingsDocumentOf(snapshot.Settings, repoPath, hash, disk);
            await RefreshAsync([repoPath], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Saves <c>maquettiste.json</c> if it still has the expected hash (E3). Follows the element save rules: the body is checked against
    /// the <c>maquettiste.json</c> schema and written in canonical form under the model root, the expected hash is compared with the file
    /// on disk, the change is validated on a candidate snapshot (only errors it introduces refuse it), and the file is staged and renamed
    /// into place under the store's write gate, then reloaded.
    /// </summary>
    /// <param name="json">The settings JSON.</param>
    /// <param name="expectedHash">The hash the caller loaded (<see cref="SettingsDocument.Hash"/>).</param>
    /// <param name="source">What caused the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Saved (an unchanged file too), Conflict with the disk version, or Invalid with the diagnostics; nothing is written
    /// unless Saved.</returns>
    public async Task<SettingsSaveResult> SaveSettingsAsync(ReadOnlyMemory<byte> json, string expectedHash, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);
        await LoadedAsync(ct).ConfigureAwait(false);
        var paths = _paths.Value;
        var repoPath = paths.ToRepoPath(ModelPaths.SettingsFile);
        var reader = new DocumentReader(_services.Schemas, _services.Json);
        if (!TryParseRequest(json, null, out var node, out var parseFailure))
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null, [.. parseFailure.Diagnostics.Select(d => d with { FilePath = repoPath })]);

        var request = json.ToArray();
        IReadOnlyList<Diagnostic> schemaErrors;
        using (var document = JsonDocument.Parse(DocumentReader.StripBom(request), DocumentReader.ParseOptions))
            schemaErrors = _services.Schemas.Evaluate(ModelPaths.SettingsFile, document.RootElement, repoPath);
        if (schemaErrors.Count > 0)
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null, [.. schemaErrors.Select(d => reader.Locate(d, request)).Order(Diagnostic.Order)]);

        var bytes = _services.Json.Write(node!, ModelPaths.SettingsFile, ModelPaths.SettingsFile);
        var parsed = reader.ReadSettings(bytes, repoPath, trusted: false, trustedCanonical: false);
        if (!parsed.Valid || parsed.Settings is null)
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null, [.. parsed.Diagnostics]);

        var notifications = new List<ChangeSet>();
        SettingsSaveResult result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await SaveSettingsLockedAsync(bytes, parsed.Settings, expectedHash, source, notifications, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var changeSet in notifications)
            await NotifyAsync(changeSet, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<SettingsSaveResult> SaveSettingsLockedAsync(byte[] bytes, ProjectSettings settings, string expectedHash, ChangeSource source,
        List<ChangeSet> notifications, CancellationToken ct)
    {
        var paths = _paths.Value;
        var repoPath = paths.ToRepoPath(ModelPaths.SettingsFile);

        // The file changed on disk behind the index: index it first, so the conflict is judged against what is really there.
        var disk = await ReadSettingsFileAsync(ct).ConfigureAwait(false);
        var diskHash = ContentHash.Of(disk ?? []);
        if (!string.Equals(diskHash, _current!.SettingsHash, StringComparison.Ordinal))
        {
            var refreshed = await ReloadAsync([repoPath], false, ChangeSource.Disk, ct).ConfigureAwait(false);
            if (!refreshed.IsEmpty)
                notifications.Add(refreshed);
        }

        var before = _current!;
        if (!string.Equals(expectedHash, diskHash, StringComparison.Ordinal))
            return new SettingsSaveResult(SaveOutcome.Conflict, diskHash, SettingsDocumentOf(before.Settings, repoPath, diskHash, disk), []);
        if (disk is not null && disk.AsSpan().SequenceEqual(bytes))
            return new SettingsSaveResult(SaveOutcome.Saved, diskHash, SettingsDocumentOf(before.Settings, repoPath, diskHash, disk), []);

        // Validate the candidate model with the new settings; only diagnostics the change introduces come back, and only errors it
        // introduces refuse it (the element-save rule), so pre-existing errors never block an unrelated settings save.
        var newHash = ContentHash.Of(bytes);
        var loadDiagnostics = before.LoadDiagnostics.Where(d => !string.Equals(d.FilePath, repoPath, StringComparison.Ordinal)).ToList();
        var candidate = ModelSnapshot.CreateAfter(before, before.Documents, settings, newHash, before.Extensions, before.RuleScripts, before.Version,
            loadDiagnostics, _options.EffectiveParallelism, ct);
        var baseline = await _services.Validator.ValidateAsync(before, ValidationScope.All, null, ct).ConfigureAwait(false);
        var report = await _services.Validator.ValidateAsync(candidate, ValidationScope.All, null, ct).ConfigureAwait(false);
        var known = baseline.Diagnostics.GroupBy(SettingsKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var introduced = new List<Diagnostic>();
        foreach (var diagnostic in report.Diagnostics)
        {
            var key = SettingsKey(diagnostic);
            if (known.TryGetValue(key, out var count) && count > 0)
                known[key] = count - 1;
            else
                introduced.Add(diagnostic);
        }

        if (introduced.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null, Sorted(introduced));

        var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
        var failure = await new AtomicFileSet(_services.EnginePaths, paths.ModelRoot)
            .ApplyAsync([(paths.FullPath(ModelPaths.SettingsFile), bytes)], [], batchId, ct).ConfigureAwait(false);
        if (failure is { Refused: true })
        {
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null,
                [RuleCatalog.Create("MQ6004", $"The model write to {failure.Path} was refused: {failure.Reason}", null, repoPath, null)]);
        }

        if (failure is not null)
            throw new IOException($"The settings could not be written ({failure.Path}); nothing was changed. {failure.Reason}");

        // The file is on disk: index it even if the caller has given up.
        var changes = await ReloadAsync([repoPath], false, source, CancellationToken.None).ConfigureAwait(false);
        if (!changes.IsEmpty)
            notifications.Add(changes);
        var saved = _current!;
        RemoveUnnamedUploads(before.Settings.Branding.Icon, saved.Settings.Branding.Icon);
        return new SettingsSaveResult(SaveOutcome.Saved, saved.SettingsHash, SettingsDocumentOf(saved.Settings, repoPath, saved.SettingsHash, bytes), Sorted(introduced));
    }

    private static string SettingsKey(Diagnostic d) =>
        d.Rule + "\u0000" + d.Severity.ToString() + "\u0000" + d.ElementId + "\u0000" + d.FilePath + "\u0000" + d.JsonPointer + "\u0000" + d.Message;

    private async Task<byte[]?> ReadSettingsFileAsync(CancellationToken ct)
    {
        var full = _paths.Value.FullPath(ModelPaths.SettingsFile);
        try
        {
            return File.Exists(full) ? await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false) : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private SettingsDocument SettingsDocumentOf(ProjectSettings settings, string repoPath, string hash, byte[]? bytes)
    {
        JsonElement json;
        try
        {
            if (bytes is null)
                throw new JsonException("missing");
            using var document = JsonDocument.Parse(DocumentReader.StripBom(bytes), DocumentReader.ParseOptions);
            json = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Missing or unparsable: the canonical form of the settings the snapshot uses (the defaults for a missing file).
            using var document = JsonDocument.Parse(_services.Json.Serialize(settings, ModelPaths.SettingsFile, ModelPaths.SettingsFile));
            json = document.RootElement.Clone();
        }

        return new SettingsDocument(settings, repoPath, hash, json);
    }

    /// <summary>Subscribes to every non-empty change set.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>A handle that unsubscribes.</returns>
    /// <remarks>
    /// Handlers run one after another after the change is published and outside the store's lock, so a handler may read or write
    /// the store. A handler's exception (cancellation included) is swallowed: the change already happened, and one subscriber must
    /// not break the others or turn a completed save into a failure.
    /// </remarks>
    public IDisposable OnChanged(Func<ChangeSet, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_handlersLock)
            _handlers = _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    /// <inheritdoc/>
    /// <remarks>Waits up to ten seconds for a write in progress, so no staged file is left half-applied; performs no I/O itself.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_handlersLock)
            _handlers = [];
        if (await _gate.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
            _gate.Release();
    }

    private async Task<ModelSnapshot> LoadedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is null)
            await LoadAsync(ct).ConfigureAwait(false);
        return _current!;
    }

    /// <summary>Loads (all files, or the given paths) through the loader and publishes the snapshot. Call under the gate.</summary>
    private async Task<ChangeSet> ReloadAsync(IReadOnlyCollection<string>? paths, bool verify, ChangeSource source, CancellationToken ct)
    {
        var result = await _services.Loader.LoadAsync(new LoadRequest(_current, paths, verify), null, ct).ConfigureAwait(false);
        _current = result.Snapshot;
        return result.Changes with { Source = source };
    }

    private async Task<BatchResult> ExecuteAsync(IReadOnlyList<PlannedChange> changes, ChangeSource source, CancellationToken ct)
    {
        await LoadedAsync(ct).ConfigureAwait(false);
        var notifications = new List<ChangeSet>();
        BatchResult result;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                result = await ExecuteCoreAsync(changes, source, notifications, ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // A disk edit indexed on the way (stale files refreshed before the save failed) is published all the same: a watcher
            // refresh of that path would find nothing new, so this is the only model.changed it gets.
            foreach (var changeSet in notifications)
                await NotifyAsync(changeSet, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        foreach (var changeSet in notifications)
            await NotifyAsync(changeSet, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<BatchResult> ExecuteCoreAsync(IReadOnlyList<PlannedChange> changes, ChangeSource source, List<ChangeSet> notifications, CancellationToken ct)
    {
        var paths = _paths.Value;
        var snapshot = _current!;
        var plan = Plan(snapshot, changes, ct);

        // Files changed on disk behind the index: refresh them, so conflicts are judged against what is really there.
        var stale = await StalePathsAsync(plan, ct).ConfigureAwait(false);
        if (stale.Count > 0)
        {
            var refreshed = await ReloadAsync([.. stale.Select(paths.ToRepoPath)], false, ChangeSource.Disk, ct).ConfigureAwait(false);
            if (!refreshed.IsEmpty)
                notifications.Add(refreshed);
            snapshot = _current!;
            plan = Plan(snapshot, changes, ct);
            if ((await StalePathsAsync(plan, ct).ConfigureAwait(false)).Count > 0)
            {
                foreach (var outcome in plan.Outcomes)
                    outcome.Fail(SaveOutcome.Conflict); // still changing under us: let the caller reload and retry
            }
        }

        if (plan.Failed)
            return Failed(plan);

        await ValidateAsync(plan, snapshot, ct).ConfigureAwait(false);
        if (plan.Failed)
            return Failed(plan);

        ChangeSet changeSet;
        if (plan.IsNoOp)
        {
            changeSet = ChangeSet.Empty(source);
        }
        else
        {
            var writes = plan.Writes.Select(kv => (paths.FullPath(kv.Key), kv.Value)).ToList();
            var deletes = plan.Deletes.Select(paths.FullPath).ToList();
            var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
            var failure = await new AtomicFileSet(_services.EnginePaths, paths.ModelRoot).ApplyAsync(writes, deletes, batchId, ct).ConfigureAwait(false);
            if (failure is { Refused: true })
            {
                var refused = RuleCatalog.Create("MQ6004", $"The model write to {failure.Path} was refused: {failure.Reason}", null, null, null);
                foreach (var outcome in plan.Outcomes)
                    outcome.Fail(SaveOutcome.Invalid, refused);
                return Failed(plan);
            }

            if (failure is not null)
                throw new IOException($"The model change could not be written ({failure.Path}); nothing was changed. {failure.Reason}");

            // The files are on disk: index them even if the caller has given up.
            var written = plan.Writes.Keys.Concat(plan.Deletes).Distinct(StringComparer.Ordinal).Select(paths.ToRepoPath).ToList();
            changeSet = await ReloadAsync(written, false, source, CancellationToken.None).ConfigureAwait(false);
            if (!changeSet.IsEmpty)
                notifications.Add(changeSet);
        }

        var current = _current!;
        var items = plan.Outcomes.Select((o, i) =>
        {
            var document = changes[i].Op == BatchOp.Delete || o.Id is null ? null : current.GetDocument(o.Id);
            return new SaveResult(SaveOutcome.Saved, o.Id, document?.Hash, document, Sorted(o.Diagnostics), [.. o.Referrers], changeSet);
        }).ToList();
        return new BatchResult(SaveOutcome.Saved, items, changeSet);
    }

    private ChangePlan Plan(ModelSnapshot snapshot, IReadOnlyList<PlannedChange> changes, CancellationToken ct) =>
        new ChangePlanner(snapshot, _services.Schemas, _services.Json, _paths.Value, _options.EffectiveIdGenerator, _options.EffectiveParallelism).Plan(
            [.. changes.Select(c => c with { Element = c.Element?.DeepClone() })], ct);

    private async Task<List<string>> StalePathsAsync(ChangePlan plan, CancellationToken ct)
    {
        var stale = new List<string>();
        foreach (var (modelPath, indexHash) in plan.Touched.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var full = _paths.Value.FullPath(modelPath);
            string? diskHash = null;
            try
            {
                if (File.Exists(full))
                {
                    await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                    diskHash = await ContentHash.OfAsync(stream, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diskHash = null;
            }

            if (diskHash != indexHash)
                stale.Add(modelPath);
        }

        return stale;
    }

    /// <summary>
    /// Validates the changed elements and their referrers in the candidate model. Every diagnostic is attached to the change it
    /// concerns; an error that the same scope did not already have before the change (compared by rule, element and pointer)
    /// makes that change invalid, so pre-existing errors never block an unrelated save.
    /// </summary>
    private async Task ValidateAsync(ChangePlan plan, ModelSnapshot before, CancellationToken ct)
    {
        if (plan.Candidate is not { } candidate)
            return;
        // A deleted database or mapping is not among the changed ids, but the entities it placed are revisited (7.2a); they are in the
        // baseline too, so their pre-existing errors never block the delete.
        var placements = FormerPlacements([.. plan.ChangeByElement.Keys.Order(StringComparer.Ordinal)], before, candidate).Distinct(StringComparer.Ordinal).ToList();
        if (plan.ChangedIds.Count == 0 && placements.Count == 0)
            return;
        // Files that referenced a changed element or sub-element before the change are validated too: a reference the change broke
        // (a seed row or code gone) no longer shows in the candidate's index.
        var scope = plan.ChangedIds.Concat(FormerReferrers(plan.ChangedIds, before, candidate)).Concat(placements)
            .Distinct(StringComparer.Ordinal).ToList();
        var report = await _services.Validator.ValidateAsync(candidate, new ValidationScope(scope, IncludeReferrers: true), null, ct).ConfigureAwait(false);
        var existing = plan.ChangedIds.Concat(placements).Distinct(StringComparer.Ordinal).Where(id => before.TryGetEntry(id, out _)).ToList();
        var baseline = existing.Count > 0
            ? await _services.Validator.ValidateAsync(before, new ValidationScope(existing, IncludeReferrers: true), null, ct).ConfigureAwait(false)
            : ValidationReport.From([]);
        var known = baseline.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .GroupBy(ErrorKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byPath = candidate.Documents.ToDictionary(d => d.Path, d => d.Element.Id, StringComparer.Ordinal);

        foreach (var diagnostic in report.Diagnostics)
        {
            var outcome = plan.Outcomes[ChangeIndexOf(diagnostic, candidate, plan, byPath)];
            outcome.Diagnostics.Add(diagnostic);
            if (diagnostic.Severity != DiagnosticSeverity.Error || RuleCatalog.IsReplayFinding(diagnostic.Rule))
                continue;
            var key = ErrorKey(diagnostic);
            if (known.TryGetValue(key, out var count) && count > 0)
                known[key] = count - 1;
            else
                outcome.Fail(SaveOutcome.Invalid);
        }
    }

    /// <summary>
    /// The entities whose MQ4012 a change may have altered through what the model had before it (engine-design 7.2a): every entity
    /// when a database changed or went, and the entity a changed or deleted mapping named before (a mapping retargeted from A to B
    /// revisits A; B is in the scope through the validator's peers). The candidate's own elements cover the rest.
    /// </summary>
    private static IEnumerable<string> FormerPlacements(IReadOnlyList<string> changedIds, ModelSnapshot before, ModelSnapshot candidate)
    {
        var everyEntity = false;
        foreach (var id in changedIds)
        {
            switch (before.GetDocument(id)?.Element)
            {
                case Database:
                    everyEntity = true;
                    break;
                case Mapping { Entity: { } entity } when candidate.GetDocument(entity) is { Element: Entity }:
                    yield return entity;
                    break;
            }
        }

        if (everyEntity)
        {
            foreach (var entity in candidate.All<Entity>())
                yield return entity.Id;
        }
    }

    private static IEnumerable<string> FormerReferrers(IReadOnlyList<string> changedIds, ModelSnapshot before, ModelSnapshot candidate)
    {
        foreach (var id in changedIds)
        {
            if (before.GetDocument(id) is not { } document)
                continue;
            // Only referrers the candidate lost: the ones it still has are in the scope through IncludeReferrers.
            var kept = new HashSet<string>(StringComparer.Ordinal);
            if (candidate.GetDocument(id) is { } after)
            {
                foreach (var (subId, _) in DocumentReader.Scan(after.Json).Ids)
                {
                    foreach (var reference in candidate.ReferencesTo(subId))
                        kept.Add(reference.FromElementId);
                }
            }

            foreach (var (subId, _) in DocumentReader.Scan(document.Json).Ids)
            {
                foreach (var reference in before.ReferencesTo(subId))
                {
                    if (reference.FromElementId != id && !kept.Contains(reference.FromElementId) && candidate.GetDocument(reference.FromElementId) is not null)
                        yield return reference.FromElementId;
                }
            }
        }
    }

    private static int ChangeIndexOf(Diagnostic diagnostic, ModelSnapshot candidate, ChangePlan plan, Dictionary<string, string> byPath)
    {
        string? owner = null;
        if (diagnostic.ElementId is { } id)
            owner = candidate.TryGetEntry(id, out var entry) ? entry.OwnerId : id;
        else if (diagnostic.FilePath is { } path)
            owner = byPath.GetValueOrDefault(path);
        if (owner is null)
            return 0;
        if (plan.ChangeByElement.TryGetValue(owner, out var index))
            return index;
        // A referrer the change did not touch: attach its diagnostic to the change of the element it references.
        foreach (var reference in candidate.ReferencesFrom(owner))
        {
            var target = candidate.TryGetEntry(reference.ToId, out var targetEntry) ? targetEntry.OwnerId : reference.ToId;
            if (plan.ChangeByElement.TryGetValue(target, out index))
                return index;
        }

        return 0;
    }

    private static string ErrorKey(Diagnostic d) => d.Rule + "\u0000" + d.ElementId + "\u0000" + d.JsonPointer;

    private static string LoadKey(Diagnostic d) => d.Rule + "\u0000" + d.ElementId + "\u0000" + d.FilePath + "\u0000" + d.JsonPointer + "\u0000" + d.Message;

    private static BatchResult Failed(ChangePlan plan)
    {
        var items = plan.Outcomes
            .Select(o => o.Outcome == SaveOutcome.Saved
                ? new SaveResult(SaveOutcome.Saved, o.Id, null, null, Sorted(o.Diagnostics), [.. o.Referrers], null)
                : new SaveResult(o.Outcome, o.Id, o.CurrentHash, o.Current, Sorted(o.Diagnostics), [.. o.Referrers], null))
            .ToList();
        var first = plan.Outcomes.First(o => o.Outcome != SaveOutcome.Saved).Outcome;
        return new BatchResult(first, items, null);
    }

    /// <summary>
    /// Diagnostics in <see cref="Diagnostic.Order"/>, except that a readable refusal (MQ2001, a reference a delete cannot resolve) comes
    /// before the raw schema failures (MQ1002) the same change produced, so the first diagnostic a caller shows says what to do.
    /// </summary>
    private static IReadOnlyList<Diagnostic> Sorted(List<Diagnostic> diagnostics)
    {
        var sorted = diagnostics.Distinct().Order(Diagnostic.Order).ToList();
        if (!sorted.Any(d => d.Rule == "MQ2001") || !sorted.Any(d => d.Rule == "MQ1002"))
            return sorted;
        return [.. sorted.Where(d => d.Rule != "MQ1002"), .. sorted.Where(d => d.Rule == "MQ1002")];
    }

    private static bool TryParseRequest(ReadOnlyMemory<byte> json, string? id, out JsonNode? node, out SaveResult invalid)
    {
        node = null;
        if (DocumentReader.InvalidUtf8(json.Span) is { } bad)
        {
            invalid = new SaveResult(SaveOutcome.Invalid, id, null, null, [new Diagnostic("MQ1001", DiagnosticSeverity.Error, bad.Message, id, null, null, bad.Line, bad.Column)], [], null);
            return false;
        }

        try
        {
            // Duplicated property names are refused here (MQ1001), before anything reads the node.
            node = JsonNode.Parse(DocumentReader.StripBom(json.ToArray()).Span, documentOptions: DocumentReader.ParseOptions);
            invalid = null!;
            return true;
        }
        catch (JsonException ex)
        {
            var position = DocumentReader.ErrorPosition(json.Span, ex);
            var diagnostic = new Diagnostic("MQ1001", DiagnosticSeverity.Error, "Invalid JSON: " + ex.Message, id, null, null, position?.Line, position?.Column);
            invalid = new SaveResult(SaveOutcome.Invalid, id, null, null, [diagnostic], [], null);
            return false;
        }
    }

    private async Task NotifyAsync(ChangeSet changes, CancellationToken ct)
    {
        if (changes.IsEmpty)
            return;
        var handlers = _handlers;
        foreach (var handler in handlers)
        {
            try
            {
                await handler(changes, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Swallowed on purpose (see OnChanged): the change is already on disk and published, cancellation included.
            }
        }
    }

    private void Unsubscribe(Func<ChangeSet, CancellationToken, ValueTask> handler)
    {
        lock (_handlersLock)
            _handlers = _handlers.Remove(handler);
    }

    private sealed class Subscription(ModelStore store, Func<ChangeSet, CancellationToken, ValueTask> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                store.Unsubscribe(handler);
        }
    }
}

/// <summary>The outcome of a save, create, delete or batch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SaveOutcome>))]
public enum SaveOutcome
{
    /// <summary>Written: HTTP 200. <c>saved</c>.</summary>
    [JsonStringEnumMemberName("saved")] Saved,

    /// <summary>The file changed since it was loaded: HTTP 409. <c>conflict</c>.</summary>
    [JsonStringEnumMemberName("conflict")] Conflict,

    /// <summary>The element or batch is invalid: HTTP 422. <c>invalid</c>.</summary>
    [JsonStringEnumMemberName("invalid")] Invalid,

    /// <summary>No such element: HTTP 404. <c>not-found</c>.</summary>
    [JsonStringEnumMemberName("not-found")] NotFound,

    /// <summary>A delete refused because other elements reference it: HTTP 409. <c>referenced</c>.</summary>
    [JsonStringEnumMemberName("referenced")] Referenced,
}

/// <summary>The result of one save, create or delete (host-contracts requirements 12 and 13).</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Id">The element id.</param>
/// <param name="Hash">The new hash when saved; the current hash on conflict.</param>
/// <param name="Current">The current element on conflict.</param>
/// <param name="Diagnostics">Validation diagnostics.</param>
/// <param name="Referrers">References that blocked a delete.</param>
/// <param name="Changes">What changed when saved.</param>
public sealed record SaveResult(
    SaveOutcome Outcome,
    string? Id,
    string? Hash,
    ElementDocument? Current,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ReferenceInfo> Referrers,
    ChangeSet? Changes);

/// <summary>What a delete does with references to the deleted element.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeleteResolution>))]
public enum DeleteResolution
{
    /// <summary>Refuse the delete while references exist: <c>refuse</c>.</summary>
    [JsonStringEnumMemberName("refuse")] Refuse,

    /// <summary>Clear optional references; a required reference makes the delete invalid: <c>remove-references</c>.</summary>
    [JsonStringEnumMemberName("remove-references")] RemoveReferences,

    /// <summary>
    /// Clear optional references, and delete what cannot exist without the element: <c>delete-dependents</c>. A referrer whose
    /// reference is required loses the smallest part of it that is valid without the reference (an attribute, an end, a key, a
    /// foreign key), or, when nothing smaller will do, is deleted with its own dependents, recursively (engine-design.md section 15.1).
    /// </summary>
    [JsonStringEnumMemberName("delete-dependents")] DeleteDependents,
}

/// <summary>A batch operation kind.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BatchOp>))]
public enum BatchOp
{
    /// <summary><c>create</c>.</summary>
    [JsonStringEnumMemberName("create")] Create,

    /// <summary><c>update</c>.</summary>
    [JsonStringEnumMemberName("update")] Update,

    /// <summary><c>delete</c>.</summary>
    [JsonStringEnumMemberName("delete")] Delete,

    /// <summary><c>translate</c>: writes one translated field (reference-types-seeds-localization.md section 3.9); declared in the contract, applied in a later step.</summary>
    [JsonStringEnumMemberName("translate")] Translate,

    /// <summary><c>add-schema</c>: adds a schema named <c>name</c> (id <c>schema</c>, or a new one) to database <c>id</c> (erratum E26).</summary>
    [JsonStringEnumMemberName("add-schema")] AddSchema,

    /// <summary><c>rename-schema</c>: renames schema <c>schema</c> of database <c>id</c> to <c>name</c>; the default follows.</summary>
    [JsonStringEnumMemberName("rename-schema")] RenameSchema,

    /// <summary>
    /// <c>remove-schema</c>: removes schema <c>schema</c> of database <c>id</c>. Refused (MQ4015, listing what lives there) while tables,
    /// views, sequences, convention entries or mappings live in it, unless <c>target</c> names the schema they move to; the default
    /// schema is refused unless <c>default</c> names the schema that becomes the default.
    /// </summary>
    [JsonStringEnumMemberName("remove-schema")] RemoveSchema,

    /// <summary><c>set-default-schema</c>: makes schema <c>schema</c> the default of database <c>id</c>.</summary>
    [JsonStringEnumMemberName("set-default-schema")] SetDefaultSchema,

    /// <summary>
    /// <c>sync-enum</c>: makes the members of the enum bound by lifecycle process <c>id</c> its bound states in document order, keeping
    /// the ids, codes and descriptions of the members it keeps (phase-3-design.md section 2.3). A removal of a member a default,
    /// allowed values, a seed cell or a scenario value uses refuses the operation (MQ9019), as does another operation of the batch
    /// that writes the process or the enum.
    /// </summary>
    [JsonStringEnumMemberName("sync-enum")] SyncEnum,

    /// <summary>
    /// <c>set-lifecycle</c>: makes process <c>target</c> the lifecycle of entity <c>id</c> and the entity its subject in one change, or,
    /// without <c>target</c>, clears the entity's lifecycle and turns the process that was bound back into an orchestration.
    /// </summary>
    [JsonStringEnumMemberName("set-lifecycle")] SetLifecycle,

    /// <summary><c>set-initial</c>: makes state <c>target</c> the initial child of process <c>id</c> (its root) or of compound state <c>id</c>.</summary>
    [JsonStringEnumMemberName("set-initial")] SetInitial,

    /// <summary>
    /// <c>refresh-scenario</c>: rewrites the <c>expect</c> of every step of scenario <c>id</c> and its <c>outcome</c> from a replay in the
    /// engine interpreter (phase-3-design.md section 3); refused when the replay cannot reach the last step.
    /// </summary>
    [JsonStringEnumMemberName("refresh-scenario")] RefreshScenario,
}

/// <summary>One batch operation.</summary>
/// <param name="Op">The operation.</param>
/// <param name="Id">The element id (update, delete; optional for create).</param>
/// <param name="ExpectedHash">The ETag the caller loaded (update, delete).</param>
/// <param name="Element">The element JSON (create, update).</param>
/// <param name="Locale">The locale of a translation (translate).</param>
/// <param name="Field">The translated field: displayName, pluralName, label or description (translate).</param>
/// <param name="Value">The translated text, a sidecar reference, or null to remove it (translate).</param>
/// <param name="Schema">The schema id (rename-schema, remove-schema, set-default-schema; optional for add-schema).</param>
/// <param name="Name">The schema name (add-schema, rename-schema).</param>
/// <param name="Target">The schema id that what lives in the removed schema moves to (remove-schema); the process (set-lifecycle); the
/// child state (set-initial).</param>
/// <param name="Default">The schema id that becomes the default when the removed schema is the default (remove-schema).</param>
/// <param name="Resolution">What a delete does with references to the element (delete); absent means <see cref="DeleteResolution.Refuse"/>.</param>
public sealed record BatchOperation(
    BatchOp Op, string? Id, string? ExpectedHash, JsonElement? Element, string? Locale = null, string? Field = null, JsonElement? Value = null,
    string? Schema = null, string? Name = null, string? Target = null, string? Default = null, DeleteResolution? Resolution = null);

/// <summary>An atomic batch of operations.</summary>
/// <param name="Operations">The operations, applied in order.</param>
public sealed record ModelBatch(IReadOnlyList<BatchOperation> Operations);

/// <summary>The result of parsing a batch.</summary>
/// <param name="Batch">The batch, or <see langword="null"/> when invalid.</param>
/// <param name="Diagnostics">Parse and schema diagnostics.</param>
public sealed record BatchParseResult(ModelBatch? Batch, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The result of applying a batch.</summary>
/// <param name="Outcome">Saved when every operation succeeded; otherwise the first failure's outcome, and nothing was written.</param>
/// <param name="Items">The result per operation.</param>
/// <param name="Changes">What changed when saved.</param>
public sealed record BatchResult(SaveOutcome Outcome, IReadOnlyList<SaveResult> Items, ChangeSet? Changes);
