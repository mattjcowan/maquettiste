using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>Unsaved text a preview renders with (generation-ui.md section 5.2); every member is optional.</summary>
public sealed record PreviewOptions
{
    /// <summary>A whole unit used instead of the saved one (the unsaved grid row); its id must equal the requested unit id.</summary>
    public PackUnit? UnitOverride { get; init; }

    /// <summary>Pack-relative path to unsaved text: templates, partials and scripts.</summary>
    public IReadOnlyDictionary<string, string>? Overlay { get; init; }

    /// <summary>Effective parameter values used instead of the pack defaults and the project values.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Parameters { get; init; }

    /// <summary>
    /// The caller's connection (with the operation, e.g. <c>preview:&lt;connection id&gt;</c>): a newer request with the same key cancels
    /// one still in flight (generation-ui.md section 5.2), which then ends with <see cref="OperationCanceledException"/>.
    /// </summary>
    public string? Connection { get; init; }

    /// <summary>Whether the request carries text not saved in the pack (it then needs the maintainer role).</summary>
    public bool CarriesUnsavedText => UnitOverride is not null || Overlay is { Count: > 0 } || Parameters is not null;
}

/// <summary>One unit of a stored plan with its read keys grouped, for "why not" (generation-ui.md section 4.3).</summary>
/// <param name="Unit">The unit with its reason and causes.</param>
/// <param name="Groups">Its read keys by cause kind, ordinal.</param>
/// <param name="Summary">One sentence.</param>
public sealed record PlanUnitDetail(PlanUnit Unit, IReadOnlyList<ReadKeyGroup> Groups, string Summary);

/// <summary>Read keys of one kind.</summary>
/// <param name="Kind">The cause kind the keys map to.</param>
/// <param name="Keys">The keys, ordinal.</param>
public sealed record ReadKeyGroup(string Kind, IReadOnlyList<string> Keys);

public sealed partial class GenerationService
{
    /// <summary>
    /// Renders one unit with no writes (host-contracts requirement 35), optionally with unsaved text: a unit override, an overlay of pack
    /// files and parameter values (generation-ui.md section 5.2). A disabled pack previews as an enabled one. The render runs under the
    /// sandbox limits and a deadline of <c>limits.scriptTimeoutMs</c> x 4; a render that does not finish fails with MQ6007.
    /// </summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="unitId">The unit id.</param>
    /// <param name="elementId">The element, for element-scoped units.</param>
    /// <param name="options">Unsaved text, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation (the request's).</param>
    /// <returns>The rendered files, diagnostics, read keys and elapsed time.</returns>
    public async Task<PreviewResult> PreviewAsync(string pack, string unitId, string? elementId, PreviewOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(unitId);
        using var turn = TakeTurn(options?.Connection, ct);
        ct = turn.Token;
        var clock = Stopwatch.StartNew();
        // The deadline covers the whole request: waiting for a preview slot, preparing and rendering.
        var deadline = PreviewDeadlineMs();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(deadline);
        UnitSession? session;
        IReadOnlyList<Diagnostic> failure;
        IDisposable? slot = null;
        try
        {
            slot = await EnterPreviewAsync(timeout.Token).ConfigureAwait(false);
            (session, failure) = await OpenUnitAsync(pack, unitId, elementId, options, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            slot?.Dispose();
            return new PreviewResult([], [RuleCatalog.Create("MQ6007", $"The preview of '{pack}/{unitId}' did not finish within {deadline} ms (limits.scriptTimeoutMs x 4).",
                elementId)]) { ElapsedMs = clock.ElapsedMilliseconds };
        }
        using var held = slot;
        if (session is null)
            return new PreviewResult([], failure) { ElapsedMs = clock.ElapsedMilliseconds };
        var (prepared, loaded, unit, element, overlay, changed, packs, renderer, context) = session;
        var key = UnitPlanner.KeyOf(loaded.Name, unit.Id, element?.Id);
        var planned = !changed && prepared.Plan.Units.FirstOrDefault(u => string.Equals(u.Key, key, StringComparison.Ordinal)) is { } saved
            ? saved
            : new PlannedUnit(key, loaded, unit, element, UnitPlanner.StaticHash(loaded, unit, prepared.Snapshot.Settings.Formatters, key));
        try
        {
            var rendered = await renderer.RenderOneAsync(planned, context, timeout.Token).ConfigureAwait(false);
            // A newer request on the connection (or the client) cancelled this one: the render may have ended as a unit failure.
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new OperationCanceledException(timeout.Token);
            return new PreviewResult(rendered.Files, Outcomes.Sort(rendered.Diagnostics))
            {
                ReadKeys = [.. rendered.ReadKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                ElapsedMs = clock.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PreviewResult([], [RuleCatalog.Create("MQ6007", $"The preview of '{pack}/{unit.Id}' did not finish within {deadline} ms (limits.scriptTimeoutMs x 4).",
                element?.Id, loaded.RelativePath + "/" + unit.Template)])
            {
                ElapsedMs = clock.ElapsedMilliseconds,
            };
        }
    }

    /// <summary>The previews and path listings rendering at once, across every connection; more wait for a slot within their deadline.</summary>
    private readonly SemaphoreSlim _previewSlots = new(Math.Max(2, Environment.ProcessorCount / 2));

    /// <summary>A preview's deadline: limits.scriptTimeoutMs x 4 from the current settings.</summary>
    private int PreviewDeadlineMs() => Math.Max(1, _store.Current?.Settings.Limits.ScriptTimeoutMs ?? 5000) * 4;

    /// <summary>Waits for a preview slot; disposing the result frees it.</summary>
    private async Task<IDisposable> EnterPreviewAsync(CancellationToken ct)
    {
        await _previewSlots.WaitAsync(ct).ConfigureAwait(false);
        return new Slot(_previewSlots);
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }

    /// <summary>The request in flight per connection key (<see cref="PreviewOptions.Connection"/>).</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _turns = new(StringComparer.Ordinal);

    /// <summary>
    /// Makes this request the connection's current one: its token is <paramref name="ct"/> linked to a source a newer request with the
    /// same key cancels. Without a key it is <paramref name="ct"/> alone.
    /// </summary>
    private Turn TakeTurn(string? connection, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (connection is null)
            return new Turn(this, null, source);
        CancellationTokenSource? previous = null;
        _turns.AddOrUpdate(connection, source, (_, old) =>
        {
            previous = old;
            return source;
        });
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The older request finished while this one started.
        }

        return new Turn(this, connection, source);
    }

    private sealed class Turn(GenerationService owner, string? connection, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;

        public void Dispose()
        {
            if (connection is not null)
                owner._turns.TryRemove(new KeyValuePair<string, CancellationTokenSource>(connection, source));
            source.Dispose();
        }
    }

    /// <summary>
    /// The preview session (generation-ui.md section 5.2): the prepared snapshot, resolved model, pack and schema diffs of a pack, reused
    /// while the model version, the settings hash, the pack folder (paths, sizes and write times) and the service's write epoch are the
    /// same and for at most <see cref="SessionTtl"/>, so a burst of previews while typing prepares once. The time bound covers writes
    /// this service does not see (the command line generating into the same repository).
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Key, PreparedRun Prepared, long At)> _sessions = new(StringComparer.Ordinal);

    private static readonly long SessionTtl = Stopwatch.Frequency * 2;
    private const int MaxSessions = 8;

    /// <summary>Bumped when a run takes the run lock: outputs, manifests, unit states and schema snapshots may change under it.</summary>
    private long _writeEpoch;

    /// <summary>Bumps the write epoch when the run lock is taken and again when it is released.</summary>
    private IAsyncDisposable? Epoched(IAsyncDisposable? held)
    {
        if (held is null)
            return null;
        Interlocked.Increment(ref _writeEpoch);
        return new EpochRelease(this, held);
    }

    private sealed class EpochRelease(GenerationService owner, IAsyncDisposable inner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner._writeEpoch);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string SessionKey(ModelSnapshot snapshot, string packStamp) =>
        string.Join('|', snapshot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.SettingsHash,
            Interlocked.Read(ref _writeEpoch).ToString(System.Globalization.CultureInfo.InvariantCulture), packStamp);

    /// <summary>The key a preview session of <paramref name="pack"/> would be stored under now (tests: what invalidates the session).</summary>
    internal async Task<string> PreviewSessionKeyAsync(string pack, CancellationToken ct) =>
        SessionKey(await _store.GetSnapshotAsync(ct).ConfigureAwait(false), PackStamp(pack));

    private string PackStamp(string pack)
    {
        var root = PackAuthoring.PackRoot(_options, pack);
        using var hash = new HashBuilder();
        hash.Add("mq-preview-session-1");
        foreach (var path in PackFileRoles.EnumerateFiles(root))
        {
            var info = new FileInfo(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            hash.Add(path).Add(info.Length).Add(info.LastWriteTimeUtc.Ticks);
        }

        return hash.Finish();
    }

    /// <summary>The pack's prepared run from the session, or a fresh one; the diagnostics say why it cannot be prepared.</summary>
    private async Task<(PreparedRun? Prepared, IReadOnlyList<Diagnostic> Failure)> PrepareSessionAsync(string pack, CancellationToken ct)
    {
        var stamp = PackStamp(pack);
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (_sessions.TryGetValue(pack, out var cached) && string.Equals(cached.Key, SessionKey(snapshot, stamp), StringComparison.Ordinal)
            && Stopwatch.GetTimestamp() - cached.At < SessionTtl)
            return (cached.Prepared, []);

        var key = SessionKey(snapshot, stamp);
        var run = new GenerationRun(_services, _store, null);
        var prepared = await run.PrepareAsync([pack], GenerationMode.DryRun, ct, locked: false).ConfigureAwait(false);
        if (prepared is null)
            return (null, [.. run.Diagnostics.Where(Outcomes.IsInvalid)]);
        // Keyed by what was read before preparing: a change during it makes the next key differ, never the reverse.
        if (prepared.Snapshot.Version == snapshot.Version && string.Equals(key, SessionKey(snapshot, stamp), StringComparison.Ordinal))
        {
            if (_sessions.Count >= MaxSessions)
                _sessions.Clear();
            _sessions[pack] = (key, prepared, Stopwatch.GetTimestamp());
        }

        return (prepared, []);
    }

    /// <summary>The prepared state a preview or a path listing renders one unit with.</summary>
    private sealed record UnitSession(PreparedRun Prepared, LoadedPack Loaded, PackUnit Unit, IResolvedObject? Element,
        IReadOnlyDictionary<string, string> Overlay, bool Changed, PackSet Packs, IRenderer Renderer, RenderContext Context);

    /// <summary>Prepares a unit for rendering with any unsaved text; the diagnostics say why it cannot be.</summary>
    private async Task<(UnitSession? Session, IReadOnlyList<Diagnostic> Failure)> OpenUnitAsync(string pack, string unitId, string? elementId,
        PreviewOptions? options, CancellationToken ct, bool checkScope = true)
    {
        if (options?.UnitOverride is { } overridden && !string.Equals(overridden.Id, unitId, StringComparison.Ordinal))
            throw new ArgumentException($"unitOverride.id '{overridden.Id}' must equal the unit '{unitId}'.", nameof(options));
        var (prepared, failure) = await PrepareSessionAsync(pack, ct).ConfigureAwait(false);
        if (prepared is null)
            return (null, failure);
        var loaded = prepared.Packs.Packs.FirstOrDefault(p => string.Equals(p.Name, pack, StringComparison.Ordinal));
        if (loaded is null)
        {
            var (named, errors) = await new PackLoader(_options, _services.Schemas).LoadNamedAsync(prepared.Snapshot, pack, ct).ConfigureAwait(false);
            if (named is null)
                return (null, errors);
            loaded = named;
        }

        var unit = options?.UnitOverride ?? loaded.Manifest.Units.FirstOrDefault(u => string.Equals(u.Id, unitId, StringComparison.Ordinal));
        if (unit is null)
        {
            return (null, [RuleCatalog.Create("MQ6001", $"Pack '{pack}' has no unit '{unitId}'.", filePath: loaded.RelativePath + "/pack.json")]);
        }

        var element = elementId is null ? null : prepared.Resolved.Find(elementId);
        if (elementId is not null && element is null)
            return (null, [RuleCatalog.Create("MQ6017", $"Element '{elementId}' is not in the resolved model.", elementId)]);

        if (checkScope && UnitPlanner.OutOfScope(prepared.Resolved, loaded, unit, element, _services.Scripts, ct) is { } outOfScope)
        {
            var index = loaded.Manifest.Units.ToList().FindIndex(u => string.Equals(u.Id, unit.Id, StringComparison.Ordinal));
            return (null, [RuleCatalog.Create("MQ6026", outOfScope, elementId, loaded.RelativePath + "/pack.json", index < 0 ? null : $"/units/{index}/for")]);
        }

        var overlay = options?.Overlay ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in overlay.Keys)
        {
            if (PackFiles.Resolve(loaded.RootPath, path) is null || string.Equals(path, "pack.json", StringComparison.Ordinal))
                throw new ArgumentException($"overlay path '{path}' must be a pack file other than pack.json.", nameof(options));
        }

        var changed = options is { CarriesUnsavedText: true };
        if (changed)
        {
            var units = loaded.Manifest.Units.Select(u => string.Equals(u.Id, unit.Id, StringComparison.Ordinal) ? unit : u).ToList();
            if (!units.Contains(unit))
                units.Add(unit);
            var scripts = loaded.Scripts.Select(s =>
            {
                var packRelative = s.Path[(loaded.RelativePath.Length + 1)..];
                return overlay.TryGetValue(packRelative, out var text) ? new ScriptSource(s.Path, text, ContentHash.Of(text)) : s;
            }).ToList();
            loaded = loaded with
            {
                Manifest = loaded.Manifest with { Units = units },
                Scripts = scripts,
                Parameters = options!.Parameters is { } parameters
                    ? new SortedDictionary<string, JsonElement>(parameters.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal)
                    : loaded.Parameters,
            };
        }

        var packs = new PackSet([.. prepared.Packs.Packs.Where(p => !string.Equals(p.Name, pack, StringComparison.Ordinal)).Append(loaded)], prepared.Packs.Diagnostics);
        var renderer = overlay.Count == 0 ? _services.CreateRenderer()
            : new Renderer(_options, new TemplateCache(overlay.ToDictionary(p => (loaded.Name, p.Key), p => p.Value)));
        var context = new RenderContext(prepared.Resolved, packs, prepared.SchemaDiffs, _services.Scripts, prepared.Hasher, 1);
        return (new UnitSession(prepared, loaded, unit, element, overlay, changed, packs, renderer, context), []);
    }

    /// <summary>Every pack folder with its units and load diagnostics, enabled or not (<c>GET /api/packs</c>).</summary>
    public async Task<IReadOnlyList<PackSummary>> ListPacksAsync(CancellationToken ct)
    {
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return await PackAuthoring.ListAsync(_options, _services.Schemas, snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>One pack's document, ETag, parameters, files and diagnostics; <see langword="null"/> when there is no such pack.</summary>
    /// <exception cref="PackPathException">The name is not a pack key.</exception>
    public async Task<PackDocument?> GetPackAsync(string pack, CancellationToken ct)
    {
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var document = await PackAuthoring.ReadAsync(_options, _services.Schemas, snapshot, pack, ct).ConfigureAwait(false);
        return document is null ? null : document with { Registrations = await RegistrationsAsync(snapshot, pack, ct).ConfigureAwait(false) };
    }

    /// <summary>The last registrations read per pack, keyed like the preview session minus the write epoch (the scripts run once per change).</summary>
    private readonly ConcurrentDictionary<string, (string Key, IReadOnlyList<ScriptRegistration> Registrations)> _registrations = new(StringComparer.Ordinal);

    /// <summary>
    /// What the pack's own scripts register, from one sandbox run under the project's limits; empty when the pack does not load or a script
    /// fails (the failure is the pack's diagnostics' business).
    /// </summary>
    private async Task<IReadOnlyList<ScriptRegistration>> RegistrationsAsync(ModelSnapshot snapshot, string pack, CancellationToken ct)
    {
        var key = snapshot.SettingsHash + "|" + PackStamp(pack);
        if (_registrations.TryGetValue(pack, out var cached) && string.Equals(cached.Key, key, StringComparison.Ordinal))
            return cached.Registrations;
        var (loaded, _) = await new PackLoader(_options, _services.Schemas).LoadNamedAsync(snapshot, pack, ct).ConfigureAwait(false);
        IReadOnlyList<ScriptRegistration> registrations = [];
        if (loaded is { Scripts.Count: > 0 })
        {
            try
            {
                using var pool = _services.Scripts.CreatePool(loaded.Scripts, snapshot.Settings.Limits, 1, ct);
                registrations = [.. pool.Registrations.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.DeclaredIn, StringComparer.Ordinal)];
            }
            catch (ScriptErrorException)
            {
            }
            catch (ScriptLimitException)
            {
            }
        }

        if (_registrations.Count >= MaxSessions)
            _registrations.Clear();
        _registrations[pack] = (key, registrations);
        return registrations;
    }

    /// <summary>Saves the whole <c>pack.json</c> document in canonical form when its hash is still <paramref name="expectedHash"/>.</summary>
    public async Task<PackWriteResult> SavePackAsync(string pack, ReadOnlyMemory<byte> document, string expectedHash, CancellationToken ct)
    {
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return await PackAuthoring.SaveManifestAsync(_services, snapshot, pack, document, expectedHash, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a pack from <c>empty</c>, a pack of this project, or one of <paramref name="starters"/>.</summary>
    public Task<PackWriteResult> CreatePackAsync(string pack, string from, IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, byte[]>>>? starters,
        CancellationToken ct) => PackAuthoring.CreateAsync(_services, pack, from, starters, ct);

    /// <summary>One pack file's text and hash; <see langword="null"/> when it does not exist.</summary>
    /// <exception cref="PackPathException">The path is refused.</exception>
    public Task<PackFileContent?> ReadPackFileAsync(string pack, string path, CancellationToken ct) => PackAuthoring.ReadFileAsync(_options, pack, path, ct);

    /// <summary>Writes one pack file; <paramref name="expectedHash"/> <see langword="null"/> creates it.</summary>
    /// <exception cref="PackPathException">The path is refused (including <c>pack.json</c>).</exception>
    public async Task<PackWriteResult> WritePackFileAsync(string pack, string path, string text, string? expectedHash, CancellationToken ct)
    {
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return await PackAuthoring.WriteFileAsync(_services, snapshot, pack, path, text, expectedHash, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes one pack file when its hash is still <paramref name="expectedHash"/> and nothing uses it.</summary>
    public Task<PackWriteResult> DeletePackFileAsync(string pack, string path, string expectedHash, CancellationToken ct) =>
        PackAuthoring.DeleteFileAsync(_services, pack, path, expectedHash, ct);

    /// <summary>
    /// Removes a pack when <c>pack.json</c> still has <paramref name="expectedHash"/>: its <c>packs.&lt;pack&gt;</c> settings entry, its folder
    /// under <c>.maquettiste/templates/</c>, its manifests and its unit states, under the run lock (waiting for a run in progress). The files
    /// it generated stay on disk and are no longer tracked (engine-design.md, pack removal).
    /// </summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="expectedHash">The <c>pack.json</c> hash the caller read (<c>GET /api/packs/{pack}</c>).</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="source">What caused the settings change.</param>
    /// <returns>Saved with what was deleted and what is now untracked; conflict, invalid or not-found with nothing removed.</returns>
    /// <exception cref="PackPathException">The name is not a pack key, or a path of the folder may not be deleted.</exception>
    public async Task<PackRemoveResult> DeletePackAsync(string pack, string expectedHash, CancellationToken ct, ChangeSource source = ChangeSource.Editor)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);
        if (!File.Exists(Path.Combine(PackAuthoring.PackRoot(_options, pack), "pack.json")))
            return new PackRemoveResult(SaveOutcome.NotFound, null, null, [], [], null, []);
        var held = Epoched(await _services.RunLock.AcquireAsync(true, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException("The run lock was not acquired.");
        await using (held.ConfigureAwait(false))
        {
            try
            {
                return await PackAuthoring.RemoveAsync(_services, _store, pack, expectedHash, source, ct).ConfigureAwait(false);
            }
            finally
            {
                _sessions.TryRemove(pack, out _);
                _registrations.TryRemove(pack, out _);
            }
        }
    }

    /// <summary>
    /// Renames a pack when <c>pack.json</c> still has <paramref name="expectedHash"/>: its <c>packs.&lt;pack&gt;</c> settings entry becomes
    /// <c>packs.&lt;newName&gt;</c>, its folder under <c>.maquettiste/templates/</c> is renamed and <c>pack.json</c> names it, and its manifests and
    /// unit states move to the new name, under the run lock (waiting for a run in progress), so the files it generated stay tracked
    /// (engine-design.md, pack rename). Generation hints keyed by the old name are listed; with <paramref name="updateHints"/> they then
    /// move to the new name in one model batch (<see cref="RenamePackHintsAsync"/>).
    /// </summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="newName">The new name: a pack key no other pack, settings entry or manifest uses.</param>
    /// <param name="expectedHash">The <c>pack.json</c> hash the caller read (<c>GET /api/packs/{pack}</c>).</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="source">What caused the settings and model changes.</param>
    /// <param name="dryRun">Check everything and report what would move, writing nothing.</param>
    /// <param name="updateHints">After the rename, move the hints keyed by the old name to the new one.</param>
    /// <returns>Saved with what moved; conflict, invalid or not-found with nothing changed.</returns>
    /// <exception cref="PackPathException">The name is not a pack key, the folder is a link, or a path may not be written.</exception>
    public async Task<PackRenameResult> RenamePackAsync(string pack, string newName, string expectedHash, CancellationToken ct,
        ChangeSource source = ChangeSource.Editor, bool dryRun = false, bool updateHints = false)
    {
        ArgumentNullException.ThrowIfNull(newName);
        ArgumentNullException.ThrowIfNull(expectedHash);
        if (!File.Exists(Path.Combine(PackAuthoring.PackRoot(_options, pack), "pack.json")))
            return new PackRenameResult(SaveOutcome.NotFound, pack, newName, null, null, null, [], [], [], []);
        PackRenameResult result;
        var held = Epoched(await _services.RunLock.AcquireAsync(true, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException("The run lock was not acquired.");
        await using (held.ConfigureAwait(false))
        {
            try
            {
                result = await PackAuthoring.RenameAsync(_services, _store, pack, newName, expectedHash, dryRun, source, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var name in new[] { pack, newName })
                {
                    _sessions.TryRemove(name, out _);
                    _registrations.TryRemove(name, out _);
                }
            }
        }

        if (dryRun || !updateHints || result.Outcome != SaveOutcome.Saved || result.Hints.Count == 0)
            return result;
        // The folder has moved: the hint update no longer depends on the caller's token.
        var batch = await RenamePackHintsAsync(pack, newName, CancellationToken.None, source).ConfigureAwait(false);
        if (batch is null)
            return result;
        if (batch.Outcome == SaveOutcome.Saved)
            return result with { HintsUpdated = [.. batch.Items.Select(i => i.Id).OfType<string>().Order(StringComparer.Ordinal)] };
        var why = batch.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message).FirstOrDefault() ?? batch.Outcome.ToString();
        return result with
        {
            Diagnostics = [RuleCatalog.Create("MQ6001", $"The pack was renamed, but its generation hints still name '{pack}': the hint update was refused ({why}).")
                with { Severity = DiagnosticSeverity.Warning }],
        };
    }

    /// <summary>
    /// Moves every generation hint keyed by <paramref name="from"/> to <paramref name="to"/> in one model batch (all or nothing), each element
    /// checked against the hash it has now (<see cref="PackHints.Rename"/>): what a pack rename does with <c>updateHints</c>.
    /// </summary>
    /// <param name="from">The old pack name.</param>
    /// <param name="to">The new pack name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="source">What caused the change.</param>
    /// <returns>The batch result, or <see langword="null"/> when no hint names <paramref name="from"/>.</returns>
    public async Task<BatchResult?> RenamePackHintsAsync(string from, string to, CancellationToken ct, ChangeSource source = ChangeSource.Editor)
    {
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var operations = new List<BatchOperation>();
        foreach (var document in snapshot.Documents.Where(d => PackHints.Names(d.Json, from)).OrderBy(d => d.Element.Id, StringComparer.Ordinal))
        {
            var node = JsonNode.Parse(document.Json.GetRawText())!;
            if (PackHints.Rename(node, from, to) == 0)
                continue;
            using var json = JsonDocument.Parse(node.ToJsonString());
            operations.Add(new BatchOperation(BatchOp.Update, document.Element.Id, document.Hash, json.RootElement.Clone()));
        }

        return operations.Count == 0 ? null : await _store.ApplyBatchAsync(new ModelBatch(operations), source, ct).ConfigureAwait(false);
    }

    /// <summary>One unit of a stored plan with its reason, causes and grouped read keys; <see langword="null"/> when not found.</summary>
    public async Task<PlanUnitDetail?> GetPlanUnitAsync(string planId, string key, CancellationToken ct)
    {
        var plan = await GetPlanAsync(planId, ct).ConfigureAwait(false);
        var unit = plan?.Units.FirstOrDefault(u => string.Equals(u.Key, key, StringComparison.Ordinal));
        if (unit is null)
            return null;
        var groups = unit.ReadKeys.GroupBy(PlanExplainer.KindOf, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ReadKeyGroup(g.Key, [.. g.Order(StringComparer.Ordinal)])).ToList();
        var outputs = unit.Outputs.Count;
        var summary = unit.Skipped
            ? $"Skipped: its {unit.ReadKeys.Count} recorded inputs ({string.Join(", ", groups.Select(g => g.Keys.Count + " " + g.Kind))}) are unchanged since its last render, and its {outputs} output{(outputs == 1 ? " is" : "s are")} intact."
            : $"Renders ({unit.Reason ?? "reason not recorded"}): {(unit.Causes.Count == 0 ? "no recorded state to compare with" : unit.Causes[0].Detail)}{(unit.CauseCount > 1 ? $" (+{unit.CauseCount - 1})" : "")}.";
        return new PlanUnitDetail(unit, groups, summary);
    }
}
