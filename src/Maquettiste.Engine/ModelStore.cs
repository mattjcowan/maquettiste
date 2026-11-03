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
using Maquettiste.Engine.Migrations;
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

    /// <summary>Whether the store serves a snapshot opened read-only (every write is refused with MQ6029).</summary>
    public bool IsReadOnly => _services.Documents.IsReadOnly;

    /// <summary>Where the documents come from and go to (docs/engineering/snapshots.md section 2).</summary>
    internal IModelDocumentStore Documents => _services.Documents;

    /// <summary>The engine options the store was created with.</summary>
    internal EngineOptions Options => _options;

    /// <summary>The services the store was created with.</summary>
    internal EngineServices Services => _services;

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

        if (IsReadOnly)
            return _current; // a snapshot never changes: nothing to rescan
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
        if (await SeedInputAsync(node, null, ct).ConfigureAwait(false) is { } refused)
            return refused;
        var result = await ExecuteAsync([new PlannedChange(BatchOp.Create, null, null, node, DeleteResolution.Refuse)], source, ct).ConfigureAwait(false);
        return Single(result);
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
        if (await SeedInputAsync(node, id, ct).ConfigureAwait(false) is { } refused)
            return refused;
        var result = await ExecuteAsync([new PlannedChange(BatchOp.Update, id, expectedHash, node, DeleteResolution.Refuse)], source, ct).ConfigureAwait(false);
        return Single(result);
    }

    /// <summary>
    /// Seed input by name on a create's or a save's body (next-phase-1-design.md section 5.3): each row's <c>values</c> object becomes
    /// its cells, then the bare names in <c>columns</c> and <c>key</c> become ids on the model as loaded.
    /// </summary>
    /// <returns><see langword="null"/> when the body is no seed or its input resolved; otherwise the invalid result (MQ1002).</returns>
    private async Task<SaveResult?> SeedInputAsync(JsonNode? node, string? id, CancellationToken ct)
    {
        if (!SeedInput.IsSeed(node))
            return null;
        var seed = (JsonObject)node!;
        var errors = new List<Diagnostic>();
        SeedInput.Rows(seed, errors);
        if (SeedInput.NeedsNames(seed))
            SeedInput.Names(seed, new SeedTargets(await LoadedAsync(ct).ConfigureAwait(false)), errors);
        return errors.Count == 0 ? null
            : new SaveResult(SaveOutcome.Invalid, id ?? (seed["id"] as JsonValue)?.ToString(), null, null, [.. errors.Order(Diagnostic.Order)], [], null);
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
        return Single(result);
    }

    /// <summary>A single-element call's result: its one item, carrying every fact the write recorded (cascades included).</summary>
    private static SaveResult Single(BatchResult result) =>
        result.Facts is { } facts && result.Items[0].Outcome == SaveOutcome.Saved ? result.Items[0] with { Facts = facts } : result.Items[0];

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
    /// with no hash and no changes. Id fields may hold qualified names (<c>@kind:path</c>) and placeholders (<c>$name</c>), and the
    /// engine mints every missing id (next-phase-1-design.md section 5.1); a saved result's <see cref="BatchResult.Ids"/> lists both.
    /// </remarks>
    public Task<BatchResult> ApplyBatchAsync(ModelBatch batch, ChangeSource source, CancellationToken ct) => ApplyBatchCoreAsync(batch, source, null, ct);

    /// <summary>
    /// Runs a batch as a dry run: the same resolution of names, minting of ids, parsing of hand-built elements, expansion of schema,
    /// process and materialize operations, planning, expected-hash checks and validation as <see cref="ApplyBatchAsync"/>, but nothing
    /// is written. The result says what an apply would answer and, when every operation would succeed, every file it would create,
    /// change or delete with the text before and after and a unified diff (the assistant's proposals, erratum E44), the ids it would
    /// mint, the diagnostics the change would add and remove over its scope, and the migration facts it would record.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome per operation and, when saved, the files, ids, diagnostics delta and facts.</returns>
    public async Task<BatchPreview> PreviewBatchAsync(ModelBatch batch, CancellationToken ct)
    {
        var sink = new BatchPreviewSink();
        var result = await ApplyBatchCoreAsync(batch, ChangeSource.Editor, sink, ct).ConfigureAwait(false);
        if (result.Outcome != SaveOutcome.Saved)
            return new BatchPreview(result.Outcome, result.Items, []);
        return new BatchPreview(result.Outcome, result.Items, sink.Files)
        {
            Ids = result.Ids,
            DiagnosticsAdded = sink.Added,
            DiagnosticsRemoved = sink.Removed,
            Facts = sink.Facts,
        };
    }

    private async Task<BatchResult> ApplyBatchCoreAsync(ModelBatch batch, ChangeSource source, BatchPreviewSink? preview, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var nodes = new JsonNode?[batch.Operations.Count];
        var invalid = new SaveResult?[batch.Operations.Count];
        for (var i = 0; i < batch.Operations.Count; i++)
        {
            var o = batch.Operations[i];
            if (o.Op == BatchOp.Translate)
            {
                var pointer = "/operations/" + i.ToString(CultureInfo.InvariantCulture) + "/op";
                invalid[i] = new SaveResult(SaveOutcome.Invalid, o.Id, null, null,
                    [RuleCatalog.Create("MQ1002", pointer + " The translate operation is declared in the contract; its handler lands in a later step.", o.Id, null, pointer)], [], null);
                continue;
            }

            // A hand-built operation may carry an element ParseBatch never saw: parse it as strictly as a request body.
            if (!IsExpandedOperation(o.Op) && o.Element is { } e && !TryParseRequest(JsonMarshal.GetRawUtf8Value(e).ToArray(), o.Id, out nodes[i], out var failure))
            {
                invalid[i] = failure with { Diagnostics = [.. failure.Diagnostics.Select(d => d with { JsonPointer = "/operations/" + i.ToString(CultureInfo.InvariantCulture) + "/element", Line = null, Column = null })] };
                continue;
            }

            // A seed row's values object becomes its cells (section 5.3) before 5.1's resolver reads the element as a typed record.
            if (o.Op is BatchOp.Create or BatchOp.Update && SeedInput.IsSeed(nodes[i]))
            {
                var errors = new List<Diagnostic>();
                SeedInput.Rows((JsonObject)nodes[i]!, errors);
                if (errors.Count > 0)
                    invalid[i] = SeedRefusal(i, o.Id, errors);
            }
        }

        if (invalid.Any(r => r is not null))
            return Invalid(batch, invalid);

        // Names, placeholders and minted ids (next-phase-1-design.md section 5.1): resolved on the snapshot as loaded, outside the gate,
        // before anything is planned or expanded; a change in between is caught as for any write (hashes on disk, MQ2001).
        var loaded = await LoadedAsync(ct).ConfigureAwait(false);
        var resolved = BatchNames.Resolve(loaded, batch, nodes, _services.Schemas, _options.EffectiveIdGenerator);
        if (resolved.Failed)
        {
            for (var i = 0; i < batch.Operations.Count; i++)
            {
                if (resolved.Diagnostics[i].Count > 0)
                    invalid[i] = new SaveResult(SaveOutcome.Invalid, resolved.Batch.Operations[i].Id, null, null, [.. resolved.Diagnostics[i].Order(Diagnostic.Order)], [], null);
            }

            return Invalid(resolved.Batch, invalid);
        }

        batch = resolved.Batch;
        if (SeedNames(loaded, batch, nodes, invalid))
            return Invalid(batch, invalid);
        var changes = new List<PlannedChange>(batch.Operations.Count);
        var schemaOps = false;
        for (var i = 0; i < batch.Operations.Count; i++)
        {
            var o = batch.Operations[i];
            if (IsExpandedOperation(o.Op))
            {
                schemaOps = true;
                continue;
            }

            changes.Add(new PlannedChange(o.Op, o.Id, o.ExpectedHash, nodes[i], o.Op == BatchOp.Delete ? o.Resolution ?? DeleteResolution.Refuse : DeleteResolution.Refuse,
                resolved.Minted[i]));
        }

        var result = schemaOps ? await ApplyWithSchemasAsync(batch, changes, invalid, source, preview, ct).ConfigureAwait(false)
            : changes.Count == 0 ? new BatchResult(SaveOutcome.Saved, [], ChangeSet.Empty(source)) { Facts = [] }
            : await ExecuteAsync(changes, source, ct, preview).ConfigureAwait(false);
        return result.Outcome == SaveOutcome.Saved ? result with { Ids = resolved.Ids } : result;

        static BatchResult Invalid(ModelBatch batch, SaveResult?[] invalid) => new(SaveOutcome.Invalid,
            [.. invalid.Select((r, i) => r ?? new SaveResult(SaveOutcome.Saved, batch.Operations[i].Id, null, null, [], [], null))], null);
    }

    /// <summary>
    /// Seed columns and keys by name in a batch (next-phase-1-design.md section 5.3), after 5.1's resolver: each seed create or update
    /// turns its bare names into ids on the model as loaded with the batch's earlier creates, updates and deletes laid over it, so a
    /// seed names the columns of a table an earlier operation creates. Reads elements only up to the last seed that has names.
    /// </summary>
    /// <returns><see langword="true"/> when a name failed; its operation's slot of <paramref name="invalid"/> says why.</returns>
    private static bool SeedNames(ModelSnapshot loaded, ModelBatch batch, JsonNode?[] nodes, SaveResult?[] invalid)
    {
        var last = -1;
        for (var i = 0; i < batch.Operations.Count; i++)
        {
            if (batch.Operations[i].Op is BatchOp.Create or BatchOp.Update && SeedInput.NeedsNames(nodes[i]))
                last = i;
        }

        if (last < 0)
            return false;
        var view = new SeedTargets(loaded);
        var failed = false;
        for (var i = 0; i <= last; i++)
        {
            var o = batch.Operations[i];
            if (o.Op is BatchOp.Create or BatchOp.Update && nodes[i] is JsonObject node)
            {
                if (SeedInput.NeedsNames(node))
                {
                    var errors = new List<Diagnostic>();
                    SeedInput.Names(node, view, errors);
                    if (errors.Count > 0)
                    {
                        invalid[i] = SeedRefusal(i, o.Id ?? (node["id"] as JsonValue)?.ToString(), errors);
                        failed = true;
                    }
                }

                if (i < last)
                    view.Lay(node);
            }
            else if (o.Op == BatchOp.Delete && o.Id is { } id)
            {
                view.Remove(id);
            }
        }

        return failed;
    }

    /// <summary>A batch operation refused by its seed input, the diagnostics' pointers under <c>/operations/&lt;i&gt;/element</c>.</summary>
    private static SaveResult SeedRefusal(int i, string? id, List<Diagnostic> errors)
    {
        var prefix = "/operations/" + i.ToString(CultureInfo.InvariantCulture) + "/element";
        return new SaveResult(SaveOutcome.Invalid, id, null, null, [.. errors.Select(d => d with { JsonPointer = prefix + d.JsonPointer }).Order(Diagnostic.Order)], [], null);
    }

    /// <summary>
    /// Whether an operation expands into updates (schema, process, materialize, binding and tag operations, <c>names-to-ids</c>,
    /// <c>fold-into-stereotype</c> and the reference-type refactors) rather than naming its element.
    /// </summary>
    private static bool IsExpandedOperation(BatchOp op) =>
        IsSchemaOperation(op) || IsProcessOperation(op) || IsMaterializeOperation(op) || IsBindingOperation(op) || IsTagOperation(op)
        || IsReferenceRefactor(op) || op is BatchOp.NamesToIds or BatchOp.FoldIntoStereotype;

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
        if (IsReadOnly && _current is not null)
            return ChangeSet.Empty(ChangeSource.Disk); // a snapshot never changes
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
    /// file that failed to load is never silently missing from a report, plus what the resolver finds once the whole model
    /// validates without error (MQ4005 over the resolved columns and the other resolution findings; once per snapshot, see
    /// <see cref="ResolverFindingsAsync"/>), so validate reports what generation would. It also adds the stale chains of complete
    /// migration facts (MQ4103), judged against each database's committed physical snapshot: those are not part of the resolver's
    /// once-per-snapshot findings, since generation rewrites a committed snapshot with no model change, so each call reads the
    /// committed snapshots again; the captures they are judged against are cached per snapshot, and so are the findings while the
    /// committed snapshots list the same rendered facts. A whole-model report judges seeds with a rows script, which the shared
    /// validation previews wait on leaves out (<see cref="WholeReportAsync"/>; next-phase-2-design.md section 3.4, decision 31).
    /// </remarks>
    public async Task<ValidationReport> ValidateAsync(ValidationScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        return await ValidateCoreAsync(snapshot, scope, committedStale: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="ValidateAsync(ValidationScope, CancellationToken)"/> over a snapshot; <paramref name="committedStale"/> adds the stale chains judged against the
    /// committed physical snapshots, which <see cref="ValidateAgainstAsync"/> judges against its baseline instead.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="scope">What to validate.</param>
    /// <param name="committedStale">Whether to add the stale chains judged against the committed physical snapshots.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="more">Findings the caller adds (<see cref="ValidateAgainstAsync"/>'s baseline findings), after severities: the report
    /// suppresses them too, and MQ5020 is judged after them.</param>
    private async Task<ValidationReport> ValidateCoreAsync(ModelSnapshot snapshot, ValidationScope scope, bool committedStale, CancellationToken ct,
        IReadOnlyList<Diagnostic>? more = null)
    {
        // The whole model's report is the one the snapshot's shared resolution validated with, or with seeds that have a rows script
        // its own (once per snapshot either way). When that one has an error the shared one lacks, generation would stop before
        // resolving, so the resolver's findings are left out.
        var whole = scope.ElementIds is null && scope.IncludeReferrers && scope.IncludeScriptRules && !scope.DeferScriptedRows;
        var report = whole ? await WholeReportAsync(snapshot, ct).ConfigureAwait(false)
            : await _services.Validator.ValidateAsync(snapshot, scope, null, ct).ConfigureAwait(false);
        var findings = whole && report.Diagnostics.Any(Generation.Outcomes.BlocksResolution) ? ResolverFindings.Empty
            : await ResolverFindingsAsync(snapshot, ct).ConfigureAwait(false);
        // The resolved rules' findings are script rules' (next-phase-2-design.md section 5.5): left out as the validator leaves its own.
        IReadOnlyList<Diagnostic> found = scope.IncludeScriptRules ? [.. findings.Findings, .. findings.ScriptFindings] : findings.Findings;
        IEnumerable<SuppressedFinding> moved = scope.IncludeScriptRules ? [.. findings.Suppressed, .. findings.ScriptSuppressed] : findings.Suppressed;
        if (committedStale && await CommittedStaleFindingsAsync(snapshot, ct).ConfigureAwait(false) is { Count: > 0 } stale)
            found = [.. found, .. stale];
        // A load diagnostic the validator suppressed is not added back.
        var seen = report.Diagnostics.Concat(report.Suppressed.Select(s => s.Finding)).Select(LoadKey).ToHashSet(StringComparer.Ordinal);
        IEnumerable<Diagnostic> load = snapshot.LoadDiagnostics;
        IEnumerable<Diagnostic> resolved = found;
        if (scope.ElementIds is { } ids)
        {
            var owners = ids.Select(id => snapshot.TryGetEntry(id, out var e) ? e.OwnerId : id).ToHashSet(StringComparer.Ordinal);
            var files = owners.Select(id => snapshot.GetDocument(id)?.Path).OfType<string>().ToHashSet(StringComparer.Ordinal);
            bool InScope(Diagnostic d) => (d.ElementId is { } e && (owners.Contains(e) || ids.Contains(e))) || (d.FilePath is { } f && files.Contains(f));
            load = load.Where(InScope);
            resolved = resolved.Where(InScope);
            moved = moved.Where(s => InScope(s.Finding));
        }

        // Suppressions (section 5.6) on what this layer adds: the resolver's and the checks' findings are already suppressed, the stale
        // chains and the caller's findings are not. Every suppressed list is merged into the report.
        var suppressions = Validation.Suppressions.Of(snapshot);
        var extra = load.Where(d => !seen.Contains(LoadKey(d))).Concat(NewFindings(report.Diagnostics, [.. resolved])).ToList();
        IReadOnlyList<Diagnostic> kept = suppressions.Apply(extra, out var keptMoved);
        IReadOnlyList<SuppressedFinding> moreMoved = [];
        var added = more is null ? [] : suppressions.Apply(more, out moreMoved);
        var suppressed = report.Suppressed.Concat(moved).Concat(keptMoved).Concat(moreMoved).ToList();
        if (kept.Count == 0 && added.Count == 0 && suppressed.Count == report.Suppressed.Count && !(whole && findings.Resolved && !suppressions.IsEmpty))
            return report;
        var merged = ValidationReport.From(report.Diagnostics.Concat(kept).Concat(added).Distinct(), suppressed);

        // MQ5020, judged once, by the outermost report, after every finding: an entry nothing matched in a whole-model validate whose
        // model resolved.
        if (whole && findings.Resolved && Validation.Suppressions.Unused(snapshot, merged) is { Count: > 0 } unused)
        {
            var positions = new Validation.Positions(_options, snapshot);
            for (var i = 0; i < unused.Count; i++)
            {
                if (await positions.LocateAsync(unused[i].FilePath!, unused[i].JsonPointer!, ct).ConfigureAwait(false) is { } at)
                    unused[i] = unused[i] with { Line = at.Line, Column = at.Column };
            }

            merged = ValidationReport.From(merged.Diagnostics.Concat(Validation.ModelValidator.ApplySettings(snapshot.Settings, unused)), merged.Suppressed);
        }

        return merged;
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
        if (IsReadOnly)
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null, [ReadOnlyRefusal()]);
        await LoadedAsync(ct).ConfigureAwait(false);
        var paths = _paths.Value;
        var repoPath = paths.ToRepoPath(ModelPaths.SettingsFile);
        var reader = new DocumentReader(_services.Schemas, _services.Json);
        if (!TryParseRequest(json, null, out var node, out var parseFailure))
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null, [.. parseFailure.Diagnostics.Select(d => d with { FilePath = repoPath })]);

        var request = json.ToArray();
        // outputs.allow[].commit (ignored since 0.5.5, MQ1010) is dropped from what is saved, so an older client can still save.
        if (RetiredSettings.Strip(node))
            request = System.Text.Encoding.UTF8.GetBytes(node!.ToJsonString());
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
            return new SettingsSaveResult(SaveOutcome.Saved, diskHash, SettingsDocumentOf(before.Settings, repoPath, diskHash, disk), []) { Facts = [] };

        var newHash = ContentHash.Of(bytes);
        var (introduced, recording) = await CheckSettingsAsync(before, settings, newHash, repoPath, ct).ConfigureAwait(false);

        // A fact file the recording rewrites (a pending fact that follows its column) that changed on disk behind the index: index it
        // and check again from what is really there, as an element save does.
        if (await ChangedFactFilesAsync(recording, ct).ConfigureAwait(false) is { Count: > 0 } changedFacts)
        {
            await RefreshFactFilesAsync(changedFacts, notifications, ct).ConfigureAwait(false);
            before = _current!;
            (introduced, recording) = await CheckSettingsAsync(before, settings, newHash, repoPath, ct).ConfigureAwait(false);
            if ((await ChangedFactFilesAsync(recording, ct).ConfigureAwait(false)).Count > 0)
                return new SettingsSaveResult(SaveOutcome.Conflict, diskHash, SettingsDocumentOf(before.Settings, repoPath, diskHash, disk), []);
        }

        if (introduced.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null, Sorted(introduced));
        introduced.AddRange(recording.Diagnostics.Select(d => d.Diagnostic));

        var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
        var failure = await Documents.ApplyAsync([(ModelPaths.SettingsFile, bytes), .. recording.Writes], [], batchId, ct).ConfigureAwait(false);
        if (failure is { Refused: true })
        {
            return new SettingsSaveResult(SaveOutcome.Invalid, null, null,
                [RuleCatalog.Create("MQ6004", $"The model write to {failure.Path} was refused: {failure.Reason}", null, repoPath, null)]);
        }

        if (failure is not null)
            throw new IOException($"The settings could not be written ({failure.Path}); nothing was changed. {failure.Reason}");

        // The files are on disk: index them even if the caller has given up.
        var changes = await ReloadAsync([repoPath, .. recording.Writes.Select(w => paths.ToRepoPath(w.Path))], false, source, CancellationToken.None).ConfigureAwait(false);
        if (!changes.IsEmpty)
            notifications.Add(changes);
        var saved = _current!;
        HandOffCaptures(before, saved, recording.Handoff);
        RemoveUnnamedUploads(before.Settings.Branding.Icon, saved.Settings.Branding.Icon);
        return new SettingsSaveResult(SaveOutcome.Saved, saved.SettingsHash, SettingsDocumentOf(saved.Settings, repoPath, saved.SettingsHash, bytes), Sorted(introduced))
        {
            Facts = recording.Facts ?? [],
        };
    }

    /// <summary>
    /// Validates the model with new settings and classifies the change. Only diagnostics the change introduces come back, and only
    /// errors it introduces refuse it (the element-save rule), so pre-existing errors never block an unrelated settings save; a change
    /// with such an error records nothing.
    /// </summary>
    private async Task<(List<Diagnostic> Introduced, FactRecording Recording)> CheckSettingsAsync(ModelSnapshot before, ProjectSettings settings,
        string newHash, string repoPath, CancellationToken ct)
    {
        var loadDiagnostics = before.LoadDiagnostics.Where(d => !string.Equals(d.FilePath, repoPath, StringComparison.Ordinal)).ToList();
        var candidate = ModelSnapshot.CreateAfter(before, before.Documents, settings, newHash, before.Extensions, before.RuleScripts, before.Version,
            loadDiagnostics, _options.EffectiveParallelism, ct, calculationScripts: before.CalculationScripts, declaredRules: before.DeclaredRules);
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
            return (introduced, FactRecording.Nothing(CacheHandoff.Clear));

        // Migration facts (next-phase-1-design.md section 3.2): a change of conventions, per-database conventions, type maps or
        // inflection can rename or retype objects of every database, so every database is classified; other settings shape no table.
        var recording = ReshapesEveryDatabase(before.Settings, settings)
            ? await RecordFactsAsync(new RecordingRequest(before, candidate, new Dictionary<string, int>(), true, [], false,
                [.. baseline.Diagnostics.Concat(report.Diagnostics).Where(d => d.Severity == DiagnosticSeverity.Error)]), ct).ConfigureAwait(false)
            : FactRecording.Nothing(CacheHandoff.Clear);
        return (introduced, recording);
    }

    private static string SettingsKey(Diagnostic d) =>
        d.Rule + "\u0000" + d.Severity.ToString() + "\u0000" + d.ElementId + "\u0000" + d.FilePath + "\u0000" + d.JsonPointer + "\u0000" + d.Message;

    private Task<byte[]?> ReadSettingsFileAsync(CancellationToken ct) => Documents.ReadAsync(ModelPaths.SettingsFile, ct);

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

    private async Task<BatchResult> ExecuteAsync(IReadOnlyList<PlannedChange> changes, ChangeSource source, CancellationToken ct, BatchPreviewSink? preview = null,
        IReadOnlyList<MaterializeResult>? materialized = null, IReadOnlyList<CodeMap>? codeMaps = null)
    {
        await LoadedAsync(ct).ConfigureAwait(false);
        var notifications = new List<ChangeSet>();
        BatchResult result;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                result = await ExecuteCoreAsync(changes, source, notifications, preview, materialized ?? [], codeMaps ?? [], ct).ConfigureAwait(false);
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

    private async Task<BatchResult> ExecuteCoreAsync(IReadOnlyList<PlannedChange> changes, ChangeSource source, List<ChangeSet> notifications,
        BatchPreviewSink? preview, IReadOnlyList<MaterializeResult> materialized, IReadOnlyList<CodeMap> codeMaps, CancellationToken ct)
    {
        if (IsReadOnly && preview is null)
        {
            return new BatchResult(SaveOutcome.Invalid,
                [.. changes.Select(c => new SaveResult(SaveOutcome.Invalid, c.Id, null, null, [ReadOnlyRefusal()], [], null))], null);
        }

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

        var errors = await ValidateAsync(plan, snapshot, preview, ct).ConfigureAwait(false);
        if (plan.Failed)
            return Failed(plan);

        // Migration facts (next-phase-1-design.md section 3.2): classified after validation and before the preview branch, so a dry
        // run lists the facts it would record; their files join the write's own in the same atomic set.
        var recording = await RecordAsync().ConfigureAwait(false);

        // A fact file the recording rewrites (a pending fact that follows its column) that changed on disk behind the index: index it
        // and plan, validate and classify again from what is really there, as for the plan's own files.
        if (await ChangedFactFilesAsync(recording, ct).ConfigureAwait(false) is { Count: > 0 } changedFacts)
        {
            await RefreshFactFilesAsync(changedFacts, notifications, ct).ConfigureAwait(false);
            snapshot = _current!;
            plan = Plan(snapshot, changes, ct);
            if (!plan.Failed)
                errors = await ValidateAsync(plan, snapshot, preview, ct).ConfigureAwait(false);
            if (plan.Failed)
                return Failed(plan);
            recording = await RecordAsync().ConfigureAwait(false);
            if ((await ChangedFactFilesAsync(recording, ct).ConfigureAwait(false)).Count > 0)
            {
                foreach (var outcome in plan.Outcomes)
                    outcome.Fail(SaveOutcome.Conflict); // still changing under us: let the caller reload and retry
                return Failed(plan);
            }
        }

        foreach (var (change, diagnostic) in recording.Diagnostics)
            plan.Outcomes[Math.Clamp(change, 0, plan.Outcomes.Count - 1)].Diagnostics.Add(diagnostic);
        if (plan is { IsNoOp: false, Candidate: { } changed } && changed.Settings.Migrations.Record != MigrationRecording.Off)
        {
            // A kept reference row's code or a type's names changed outside the refactors: no value map was recorded (MQ4114).
            foreach (var (change, diagnostic) in PlainCodeEdits(snapshot, changed, plan.ChangeByElement, codeMaps))
                plan.Outcomes[Math.Clamp(change, 0, plan.Outcomes.Count - 1)].Diagnostics.Add(diagnostic);
        }

        var factWrites = recording.Writes.ToDictionary(w => w.Path, w => w.Bytes, StringComparer.Ordinal);

        if (preview is not null)
        {
            // The dry run's files, with diffs, and its facts; the MQ4101 of each pending fact it would record is a diagnostic it adds.
            preview.Files.Clear();
            preview.Files.AddRange(await PreviewFilesAsync(plan, factWrites, ct).ConfigureAwait(false));
            preview.Facts = recording.Facts;
            if (recording.Diagnostics.Any(d => d.Diagnostic.Rule == "MQ4101"))
                preview.Added = [.. preview.Added.Concat(recording.Diagnostics.Select(d => d.Diagnostic).Where(d => d.Rule == "MQ4101")).Order(Diagnostic.Order)];
            return new BatchResult(SaveOutcome.Saved,
                [.. plan.Outcomes.Select(o => new SaveResult(SaveOutcome.Saved, o.Id, null, null, Sorted(o.Diagnostics), [.. o.Referrers], null))], null)
            {
                Facts = recording.Facts,
            };
        }

        ChangeSet changeSet;
        if (plan.IsNoOp)
        {
            changeSet = ChangeSet.Empty(source);
        }
        else
        {
            var writes =plan.Writes.Where(kv => !factWrites.ContainsKey(kv.Key)).Select(kv => (kv.Key, kv.Value)).Concat(factWrites.Select(kv => (kv.Key, kv.Value))).ToList();
            var deletes = plan.Deletes.ToList();
            var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
            var failure = await Documents.ApplyAsync(writes, deletes, batchId, ct).ConfigureAwait(false);
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
            var written = plan.Writes.Keys.Concat(factWrites.Keys).Concat(plan.Deletes).Distinct(StringComparer.Ordinal).Select(paths.ToRepoPath).ToList();
            changeSet = await ReloadAsync(written, false, source, CancellationToken.None).ConfigureAwait(false);
            if (!changeSet.IsEmpty)
                notifications.Add(changeSet);
            HandOffCaptures(snapshot, _current!, recording.Handoff);
        }

        var current = _current!;
        var items = plan.Outcomes.Select((o, i) =>
        {
            var document = changes[i].Op == BatchOp.Delete || o.Id is null ? null : current.GetDocument(o.Id);
            return new SaveResult(SaveOutcome.Saved, o.Id, document?.Hash, document, Sorted(o.Diagnostics), [.. o.Referrers], changeSet);
        }).ToList();
        return new BatchResult(SaveOutcome.Saved, items, changeSet) { Facts = recording.Facts ?? [] };

        Task<FactRecording> RecordAsync() => plan.IsNoOp || plan.Candidate is null
            ? Task.FromResult(FactRecording.Nothing(CacheHandoff.Carry))
            : RecordFactsAsync(new RecordingRequest(snapshot, plan.Candidate, plan.ChangeByElement, false, materialized, preview is not null, errors, codeMaps), ct);
    }

    /// <summary>
    /// MQ4114 (next-phase-2-design.md section 3.5, "Recording the value map"): a change that alters the code of a reference row it keeps
    /// (the same row id in a seed of the same reference type, before and after), or a kept reference type's <c>name</c> or
    /// <c>pluralName</c>, while no code map of the write covers the type, records no value map; each finding goes on the change of its
    /// element, with the severity <c>validation.rules</c> gives it. Only the changed documents are read.
    /// </summary>
    private static IEnumerable<(int Change, Diagnostic Diagnostic)> PlainCodeEdits(ModelSnapshot before, ModelSnapshot after,
        IReadOnlyDictionary<string, int> changed, IReadOnlyList<CodeMap> codeMaps)
    {
        var covered = codeMaps.SelectMany(m => new[] { m.ReferenceType, m.HeldType }).ToHashSet(StringComparer.Ordinal);
        var found = new List<(int Change, Diagnostic Diagnostic)>();
        foreach (var (id, change) in changed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var document = after.GetDocument(id);
            switch (before.GetDocument(id)?.Element, document?.Element)
            {
                case (ReferenceType old, ReferenceType type) when !covered.Contains(type.Id):
                    if (!string.Equals(old.Name, type.Name, StringComparison.Ordinal))
                    {
                        Add(change, $"Reference type '{old.Name}' was renamed to '{type.Name}' outside the refactor operations, so no value map was recorded and objects a pack named after '{old.Name}' keep that name; use rename-reference-type.",
                            type.Id, document!.Path, "/name");
                    }

                    if (!string.Equals(old.PluralName, type.PluralName, StringComparison.Ordinal))
                    {
                        Add(change, $"The plural name of reference type '{type.Name}' changed from '{old.PluralName ?? "(inflected)"}' to '{type.PluralName ?? "(inflected)"}' outside the refactor operations, so no value map was recorded and objects a pack named after the old plural keep it; write a value-map fact whose from and to carry the two plural names.",
                            type.Id, document!.Path, "/pluralName");
                    }

                    break;
                case (Seed old, Seed seed) when string.Equals(old.Target, seed.Target, StringComparison.Ordinal) && !covered.Contains(seed.Target)
                    && after.Get<ReferenceType>(seed.Target) is { } target:
                    var was = Codes(old);
                    var at = CodeColumn(seed);
                    for (var r = 0; at >= 0 && r < seed.Rows.Count; r++)
                    {
                        var row = seed.Rows[r];
                        if (at < row.Values.Count && ReferenceDataIndex.CodeKey(row.Values[at]) is { } code && was.TryGetValue(row.Id, out var previous)
                            && !string.Equals(previous, code, StringComparison.Ordinal))
                        {
                            Add(change, $"The code of row {row.Id} of reference type '{target.Name}' changed from '{previous}' to '{code}' outside the refactor operations, so no value map was recorded and a database that stores '{previous}' keeps it; use rename-code (or retire-code to fold it into another code).",
                                seed.Id, document!.Path, "/rows/" + r.ToString(CultureInfo.InvariantCulture) + "/values/" + at.ToString(CultureInfo.InvariantCulture));
                        }
                    }

                    break;
            }
        }

        return found;

        void Add(int change, string message, string elementId, string path, string pointer)
        {
            foreach (var settled in Validation.ModelValidator.ApplySettings(after.Settings, [RuleCatalog.Create("MQ4114", message, elementId, path, pointer)]))
                found.Add((change, settled));
        }

        static int CodeColumn(Seed seed)
        {
            for (var k = 0; k < seed.Columns.Count; k++)
            {
                if (string.Equals(seed.Columns[k], "code", StringComparison.Ordinal))
                    return k;
            }

            return -1;
        }

        static Dictionary<string, string> Codes(Seed seed)
        {
            var codes = new Dictionary<string, string>(StringComparer.Ordinal);
            var at = CodeColumn(seed);
            foreach (var row in seed.Rows)
            {
                if (at >= 0 && at < row.Values.Count && ReferenceDataIndex.CodeKey(row.Values[at]) is { } code)
                    codes.TryAdd(row.Id, code);
            }

            return codes;
        }
    }

    /// <summary>The files a plan would write and delete (with the fact files it would record), ordered by path, with their text before and after.</summary>
    private async Task<List<BatchPreviewFile>> PreviewFilesAsync(ChangePlan plan, IReadOnlyDictionary<string, byte[]> factWrites, CancellationToken ct)
    {
        var paths = _paths.Value;
        var files = new List<BatchPreviewFile>();
        foreach (var modelPath in plan.Writes.Keys.Concat(factWrites.Keys).Concat(plan.Deletes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            byte[]? before = null;
            try
            {
                before = await Documents.ReadAsync(modelPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                before = null;
            }

            var after = factWrites.TryGetValue(modelPath, out var bytes) || plan.Writes.TryGetValue(modelPath, out bytes) ? bytes : null;
            if (after is not null && before is not null && before.AsSpan().SequenceEqual(after))
                continue;
            var action = after is null ? "deleted" : before is null ? "created" : "changed";
            var (id, kind, name) = Identify(after ?? before);
            var repoPath = paths.ToRepoPath(modelPath);
            files.Add(new BatchPreviewFile(repoPath, action, id, kind, name, before is null ? null : ContentHash.Of(before),
                before is null ? null : System.Text.Encoding.UTF8.GetString(before), after is null ? null : System.Text.Encoding.UTF8.GetString(after))
            {
                Diff = _services.Diffs.Unified(repoPath, before ?? [], after ?? []),
            });
        }

        return files;

        static (string? Id, string? Kind, string? Name) Identify(byte[]? bytes)
        {
            if (bytes is null)
                return (null, null, null);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return (null, null, null);
                string? Text(string property) =>
                    document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                return (Text("id"), Text("kind"), Text("name"));
            }
            catch (JsonException)
            {
                return (null, null, null); // a sidecar or another text file
            }
        }
    }

    private ChangePlan Plan(ModelSnapshot snapshot, IReadOnlyList<PlannedChange> changes, CancellationToken ct) =>
        new ChangePlanner(snapshot, _services.Schemas, _services.Json, _paths.Value, _options.EffectiveIdGenerator, _options.EffectiveParallelism,
            // Pack manifests are read only when a change removes something a pack parameter may name (next-phase-1-design.md section 4.1).
            new Lazy<PackParameterRefs>(() => PackParameterRefs.Read(_paths.Value, snapshot.Settings), LazyThreadSafetyMode.None)).Plan(
            [.. changes.Select(c => c with { Element = c.Element?.DeepClone() })], ct);

    private async Task<List<string>> StalePathsAsync(ChangePlan plan, CancellationToken ct)
    {
        var stale = new List<string>();
        foreach (var (modelPath, indexHash) in plan.Touched.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string? diskHash = null;
            try
            {
                if (await Documents.ReadAsync(modelPath, ct).ConfigureAwait(false) is { } bytes)
                    diskHash = ContentHash.Of(bytes);
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
    /// <returns>The errors of the validation, before the change and after it (the recording of migration facts leaves out the
    /// objects their elements own).</returns>
    private Task<IReadOnlyList<Diagnostic>> ValidateAsync(ChangePlan plan, ModelSnapshot before, CancellationToken ct) => ValidateAsync(plan, before, null, ct);

    /// <summary><see cref="ValidateAsync(ChangePlan, ModelSnapshot, CancellationToken)"/>, collecting a dry run's diagnostics delta.</summary>
    /// <remarks>
    /// A dry run (<paramref name="preview"/>) also validates <paramref name="before"/> over the change's scope: every element the
    /// change created, updated, deleted or rewrote (deletes included), their referrers before the change and the placements, each
    /// that the model had. Each side then validates the documents only the other side's run reached and that it has (peers are
    /// worked out on each model), so both cover the same documents. The two reports, compared as multisets keyed like
    /// <see cref="ErrorKey"/> (every severity), give the diagnostics the change adds and removes (next-phase-1-design.md section
    /// 5.1). The scope is the apply's, never the whole model, and the refusals do not change.
    /// </remarks>
    /// <returns>The errors of the validation, before the change and after it.</returns>
    private async Task<IReadOnlyList<Diagnostic>> ValidateAsync(ChangePlan plan, ModelSnapshot before, BatchPreviewSink? preview, CancellationToken ct)
    {
        if (preview is not null)
        {
            preview.Added = [];
            preview.Removed = [];
        }

        if (plan.Candidate is not { } candidate)
            return [];
        // A deleted database or mapping is not among the changed ids, but the entities it placed are revisited (7.2a); they are in the
        // baseline too, so their pre-existing errors never block the delete.
        var changed = plan.ChangeByElement.Keys.Order(StringComparer.Ordinal).ToList();
        var placements = FormerPlacements(changed, before, candidate).Concat(FormerRowTables(changed, before, candidate)).Distinct(StringComparer.Ordinal).ToList();
        var followers = FormerFactFollowers(changed, before, candidate).ToList();
        // A dry run counts the deletes too: what a deleted element had shows as removed.
        if ((preview is null ? plan.ChangedIds.Count : changed.Count) == 0 && placements.Count == 0 && followers.Count == 0)
            return [];
        // Files that referenced a changed element or sub-element before the change are validated too: a reference the change broke
        // (a seed row or code gone) no longer shows in the candidate's index.
        var scope = plan.ChangedIds.Concat(FormerReferrers(plan.ChangedIds, before, candidate)).Concat(placements).Concat(followers)
            .Distinct(StringComparer.Ordinal).ToList();
        var report = scope.Count > 0
            ? await _services.Validator.ValidateAsync(candidate, new ValidationScope(scope, IncludeReferrers: true), null, ct).ConfigureAwait(false)
            : ValidationReport.From([]);
        var existing = plan.ChangedIds.Concat(placements).Concat(followers).Distinct(StringComparer.Ordinal).Where(id => before.TryGetEntry(id, out _)).ToList();
        var baseline = existing.Count > 0
            ? await _services.Validator.ValidateAsync(before, new ValidationScope(existing, IncludeReferrers: true), null, ct).ConfigureAwait(false)
            : ValidationReport.From([]);
        if (preview is not null)
        {
            var delta = changed.Concat(placements).Concat(followers).Distinct(StringComparer.Ordinal).Where(id => before.TryGetEntry(id, out _)).ToList();
            var prior = delta.ToHashSet(StringComparer.Ordinal).SetEquals(existing) ? baseline
                : await _services.Validator.ValidateAsync(before, new ValidationScope(delta, IncludeReferrers: true), null, ct).ConfigureAwait(false);
            // The validator widens each scope with conflict and row peers worked out on its own model, so a document that only one of
            // the two runs reached (a peer of a created seed or name, or of a deleted one) is validated on the other side too: else
            // its findings would show as added or removed.
            var after = Validation.ModelValidator.TargetIds(candidate, new ValidationScope(scope, IncludeReferrers: true));
            var formerly = Validation.ModelValidator.TargetIds(before, new ValidationScope(delta, IncludeReferrers: true));
            var now = await WithDocumentsAsync(candidate, report.Diagnostics, formerly.Except(after, StringComparer.Ordinal), ct).ConfigureAwait(false);
            var then = await WithDocumentsAsync(before, prior.Diagnostics, after.Except(formerly, StringComparer.Ordinal), ct).ConfigureAwait(false);
            (preview.Added, preview.Removed) = (Exceeding(now, then), Exceeding(then, now));
        }

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

        return [.. baseline.Diagnostics.Concat(report.Diagnostics).Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    /// <summary>
    /// <paramref name="diagnostics"/> with the findings of the <paramref name="documents"/> that <paramref name="model"/> has, validated
    /// on their own: the validator widens their scope with their peers, whose findings are left out. A document's findings do not
    /// depend on the scope it is validated in.
    /// </summary>
    private async Task<IReadOnlyList<Diagnostic>> WithDocumentsAsync(ModelSnapshot model, IReadOnlyList<Diagnostic> diagnostics, IEnumerable<string> documents,
        CancellationToken ct)
    {
        var ids = documents.Where(id => model.GetDocument(id) is not null).ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            return diagnostics;
        var report = await _services.Validator.ValidateAsync(model, new ValidationScope([.. ids.Order(StringComparer.Ordinal)], IncludeReferrers: false), null, ct)
            .ConfigureAwait(false);
        var paths = ids.Select(id => model.GetDocument(id)!.Path).ToHashSet(StringComparer.Ordinal);
        return [.. diagnostics.Concat(report.Diagnostics.Where(d => d.FilePath is { } path ? paths.Contains(path)
            : d.ElementId is { } id && model.GetDocument(id) is { } owner && ids.Contains(owner.Element.Id)))];
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

    /// <summary>
    /// The tables whose rows a changed seed or reference type gave rows to before the change (a seed's table, a type's bound tables):
    /// a binding removed or retargeted, or a seed deleted, changes the row checks of files that do not reference it (MQ7111, MQ7112,
    /// MQ7119); in scope, a table brings those files in through the validator's peers.
    /// </summary>
    private static IEnumerable<string> FormerRowTables(IReadOnlyList<string> changedIds, ModelSnapshot before, ModelSnapshot candidate)
    {
        foreach (var id in changedIds)
        {
            IEnumerable<string> tables = before.GetDocument(id)?.Element switch
            {
                Seed seed when before.GetDocument(seed.Target)?.Element is Table => [seed.Target],
                Seed seed when before.GetDocument(seed.Target)?.Element is ReferenceType type => type.Bindings.Select(b => b.Table),
                ReferenceType type => type.Bindings.Select(b => b.Table),
                _ => [],
            };
            foreach (var table in tables)
            {
                if (candidate.GetDocument(table) is { Element: Table })
                    yield return table;
            }
        }
    }

    /// <summary>
    /// The migration facts whose <c>after</c> links name a fact the change deleted (MQ4111 now): links are not references, and the
    /// validator's peers of a fact (<see cref="Validation.ModelValidator.ConflictPeers"/>) come from its document, which a deleted fact no longer
    /// has. A fact moved to another database keeps its document, so its peers still bring in the facts that name it. One pass over
    /// the candidate's facts, only when the change deleted a fact.
    /// </summary>
    private static IEnumerable<string> FormerFactFollowers(IReadOnlyList<string> changedIds, ModelSnapshot before, ModelSnapshot candidate)
    {
        HashSet<string>? deleted = null;
        foreach (var id in changedIds)
        {
            if (before.GetDocument(id)?.Element is Migration && candidate.GetDocument(id) is null)
                (deleted ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
        }

        if (deleted is null)
            yield break;
        foreach (var fact in candidate.All<Migration>())
        {
            if (fact.After.Any(deleted.Contains))
                yield return fact.Id;
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

    /// <summary>
    /// The diagnostics of <paramref name="report"/> beyond those <paramref name="other"/> has, compared as multisets keyed like
    /// <see cref="ErrorKey"/> (rule, element, pointer), every severity; in <see cref="Diagnostic.Order"/>.
    /// </summary>
    private static List<Diagnostic> Exceeding(IReadOnlyList<Diagnostic> report, IReadOnlyList<Diagnostic> other)
    {
        var counts = other.Distinct().GroupBy(ErrorKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var exceeding = new List<Diagnostic>();
        foreach (var diagnostic in report.Distinct().Order(Diagnostic.Order))
        {
            var key = ErrorKey(diagnostic);
            if (counts.TryGetValue(key, out var count) && count > 0)
                counts[key] = count - 1;
            else
                exceeding.Add(diagnostic);
        }

        return exceeding;
    }

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
    ChangeSet? Changes)
{
    /// <summary>
    /// The migration facts the write created or updated (next-phase-1-design.md section 3.2): every fact a single-element call (create,
    /// save, delete) recorded, cascades included; <see langword="null"/> on an item of a batch, whose facts are on the batch, and when the
    /// change was not saved. The pending ones' MQ4101 is in <see cref="Diagnostics"/>.
    /// </summary>
    public IReadOnlyList<RecordedFact>? Facts { get; init; }

    /// <summary>
    /// What a reference-type refactor (next-phase-2-design.md section 3.5) rewrites, read from the index: on the item of the type the
    /// operation names (for a merge, the merged-away type's delete), in a dry run and on apply; <see langword="null"/> on every other
    /// item.
    /// </summary>
    public RefactorUsage? Usage { get; init; }
}

/// <summary>What a reference-type refactor rewrites (next-phase-2-design.md section 3.5), found through the index, never a resolve.</summary>
/// <param name="UseCount">The number of uses rewritten: codes in defaults, <c>allowedValues</c> entries and cells, ids that name a removed
/// row, and references to a merged-away type.</param>
/// <param name="Uses">The first 200 of them, ordinal by element id, then pointer; <see cref="ReferenceInfo.ToId"/> is the row or type
/// the use named before the refactor.</param>
/// <param name="RowsMoved">The rows a merge moves into the target type.</param>
/// <param name="RowsRemoved">The rows removed: a retired code's row, a merged-away type's rows whose codes the target holds.</param>
/// <param name="TranslationsRemoved">The translation entries removed with them (one per locale and node).</param>
public sealed record RefactorUsage(int UseCount, IReadOnlyList<ReferenceInfo> Uses, int RowsMoved, int RowsRemoved, int TranslationsRemoved);

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
    /// <c>materialize-tables</c>: for each of <c>entities</c>, a designed table of <c>database</c> with the shape the entity's projection
    /// has, and a binding of the entity to it (erratum E43); the entity's overlay folds into the table and its mapping to the database
    /// is deleted. Refused (MQ4055) for an entity already bound to the database.
    /// </summary>
    [JsonStringEnumMemberName("materialize-tables")] MaterializeTables,

    /// <summary>
    /// <c>materialize-entities</c>: for each of <c>tables</c> (designed or imported tables and views of <c>database</c>), an entity in
    /// <c>package</c> with one attribute per column, its key from the primary key and a binding (erratum E43); foreign keys between them
    /// become many-to-one relations. Refused (MQ4055) for a table an entity is already bound to.
    /// </summary>
    [JsonStringEnumMemberName("materialize-entities")] MaterializeEntities,

    /// <summary>
    /// <c>materialize-attributes</c>: for each of <c>entities</c>, an attribute per column of the source of its binding to
    /// <c>database</c>, mapped to it in the binding: the <c>columns</c> named (one entity only), or every column nothing in the binding
    /// names. Refused (MQ4055) for an entity with no binding to the database, a query source, or a column already mapped.
    /// </summary>
    [JsonStringEnumMemberName("materialize-attributes")] MaterializeAttributes,

    /// <summary>
    /// <c>materialize-columns</c>: for each of <c>entities</c>, bound to <c>database</c> and reading a designed or imported table it
    /// writes, a column of that table per attribute the binding leaves unmapped (the <c>attributes</c> named, one entity only), shaped
    /// as the entity's projection would make it, and a field mapping it. Refused (MQ4055) for an entity with no binding, a source that
    /// is not a table file, an attribute already mapped or no column holds.
    /// </summary>
    [JsonStringEnumMemberName("materialize-columns")] MaterializeColumns,

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

    /// <summary>
    /// <c>retag</c>: removes <c>tags</c>, or renames the one tag to <c>name</c>, in the tag vocabulary of <c>package</c> (the global one
    /// without it) and in every element and sub-element whose use of the tag that vocabulary governs: uses inside the scope that the
    /// vocabulary declares, or that no vocabulary on their domain chain declares (<see cref="ModelStore.GetTagUsageAsync"/>). A
    /// renamed tag merges with the new key where both are on one list. Refused (MQ1002) when nothing uses or declares the tags.
    /// </summary>
    [JsonStringEnumMemberName("retag")] Retag,

    /// <summary>
    /// <c>names-to-ids</c> (next-phase-1-design.md section 4.1): turns every value an <c>x-ref</c> types that is a name, not an id, into
    /// the id it finds, in the elements <c>elementIds</c> lists or, without it, in every document with <c>x-ref</c> sites (stereotype
    /// defaults included). A name matches the in-scope candidates of the keyword's kinds: elements by their qualified-name forms
    /// (stereotypes by key), and within the <c>of</c> scope sub-elements by name, columns by physical name and rows by code. A value
    /// that finds nothing, or several, refuses the batch with one MQ2010 at the value; nothing to convert writes nothing.
    /// </summary>
    [JsonStringEnumMemberName("names-to-ids")] NamesToIds,

    /// <summary>
    /// <c>tags-to-property</c> (next-phase-1-design.md section 4.4): moves the tags that start with <c>prefix</c> into the custom
    /// property <c>name</c> of each element and sub-element that holds them, in the scope the tag vocabulary of <c>package</c> governs
    /// (as for <c>retag</c>), limited to <c>elementIds</c>. The schema that declares the property converts the suffixes: a string
    /// takes one, a string array all of them in list order, an integer, number or boolean one (invariant culture), an <c>enum</c> one
    /// member, and an <c>x-ref</c> resolves the suffix as <c>names-to-ids</c> does. The tags leave their holders and, without
    /// <c>elementIds</c>, their declarations leave the vocabulary, except a tag that something without properties (a relation end,
    /// for example) still uses, whose declaration stays. Refused with MQ1002 when no tag matches, and with one MQ5001 per holder
    /// when no schema declares the property, a suffix does not fit or the holder already holds another value.
    /// </summary>
    [JsonStringEnumMemberName("tags-to-property")] TagsToProperty,

    /// <summary>
    /// <c>fold-into-stereotype</c> (next-phase-2-design.md section 3.1): stereotype <c>id</c>'s members replace the matching own members
    /// of the designed and imported tables <c>tables</c> lists, or of every such table of schema <c>schema</c>. Each table carries the
    /// stereotype; own columns match its columns by name and own keys, indexes and checks match its members structurally; what matched
    /// leaves the table file and every reference to it names the inherited member's keyed id (<c>&lt;tableId&gt;.&lt;memberId&gt;</c>).
    /// The database sees nothing: a redefinable facet that differs becomes a <c>redefines</c> entry with <c>redefine</c>, a member the
    /// table lacks an <c>excludes</c> entry with <c>excludeMissing</c>; anything else refuses the table (MQ4077), and the batch writes
    /// nothing. The write records one <c>rekey</c> fact per database. A table where nothing matches is skipped.
    /// </summary>
    [JsonStringEnumMemberName("fold-into-stereotype")] FoldIntoStereotype,

    /// <summary>
    /// <c>map-by-name</c> (next-phase-2-design.md section 3.3, decision 7): for each of <c>entities</c>, bound to <c>database</c>, maps
    /// every attribute its binding does not map to a free column of the source with the same loose name (case and every character
    /// that is not an ASCII letter or digit ignored): an attribute's name, <c>attributeMember</c> for a value object member, a to-one
    /// navigation's name plus <c>Id</c> (or without it). Fields name a table file's columns by id and a view's by name; a table file's
    /// binding records the provenance of the columns it maps. It writes no attribute and no column. Refused (MQ4089) for an entity with
    /// no binding to the database, a source it cannot read (a query, a projected table), or when nothing matches for every entity.
    /// </summary>
    [JsonStringEnumMemberName("map-by-name")] MapByName,

    /// <summary>
    /// <c>resync-binding</c> (next-phase-2-design.md section 3.3, decision 8): brings the binding to <c>database</c> of each of
    /// <c>entities</c>, whose source is a table file, into step with the table: renames an own attribute that followed its column's
    /// name (unless <c>keepNames</c>), retypes an own attribute whose column was retyped, maps by name, adds an attribute per added
    /// column still free, removes the fields of removed columns (never the attributes) and records the provenance of every column, or
    /// with <c>columns</c> (one entity) works on those columns and updates only their entries. Refused (MQ4089) for an entity with no
    /// binding, a source that is not a table file, <c>columns</c> misused, or when every binding it names is in step.
    /// </summary>
    [JsonStringEnumMemberName("resync-binding")] ResyncBinding,

    /// <summary>
    /// <c>rename-code</c> (next-phase-2-design.md section 3.5): gives the row of reference type <c>id</c> whose code is <c>code</c> the code
    /// <c>to</c>, rewrites every default, <c>allowedValues</c> entry, seed cell and typed column's default, table seed cell or binding
    /// constant that names <c>code</c>, and keeps <c>code</c> as a former code of the row. Refused (MQ7020) when <c>code</c> is no code
    /// of a written row, or <c>to</c> equals it, is live, is a former code or does not fit the type's code.
    /// </summary>
    [JsonStringEnumMemberName("rename-code")] RenameCode,

    /// <summary>
    /// <c>retire-code</c>: removes the row of reference type <c>id</c> whose code is <c>code</c>, with its translations, rewrites the uses
    /// of <c>code</c> to <c>to</c> and the ids that name the removed row (<c>x-ref</c> values, former codes) to the row of <c>to</c>, and
    /// keeps <c>code</c> as a former code of that row. Refused (MQ7020) when either code is no code of a written row, or they are equal.
    /// </summary>
    [JsonStringEnumMemberName("retire-code")] RetireCode,

    /// <summary>
    /// <c>merge-reference-types</c>: merges reference type <c>id</c> (A) into <c>target</c> (B). An A code equal to a live B code maps to
    /// it, <c>codes</c> maps others to B codes, and every other A row moves into B with its id, values and translations; A's seeds are
    /// retargeted to B (one with the name of a B seed joins it), every reference to A names B, and B gains A's former codes and one per
    /// mapped code; A is deleted. Refused (MQ7020) for mismatched code types, a binding or scripted rows on A, a code that is no live
    /// code, a moving code B cannot take, a field B lacks, or two seeds of one name that differ in environments or apply.
    /// </summary>
    [JsonStringEnumMemberName("merge-reference-types")] MergeReferenceTypes,

    /// <summary>
    /// <c>rename-reference-type</c>: gives reference type <c>id</c> the name <c>name</c>; its file and the seeds in its seed folder move
    /// as the name does. Refused (MQ7020) when the name is the current one, is not an element name or another reference type has it.
    /// </summary>
    [JsonStringEnumMemberName("rename-reference-type")] RenameReferenceType,
}

/// <summary>One batch operation.</summary>
/// <param name="Op">The operation.</param>
/// <param name="Id">The element id (update, delete; optional for create); the reference type a refactor acts on (rename-code, retire-code,
/// rename-reference-type), the one merged away (merge-reference-types).</param>
/// <param name="ExpectedHash">The ETag the caller loaded (update, delete).</param>
/// <param name="Element">The element JSON (create, update).</param>
/// <param name="Locale">The locale of a translation (translate).</param>
/// <param name="Field">The translated field: displayName, pluralName, label or description (translate).</param>
/// <param name="Value">The translated text, a sidecar reference, or null to remove it (translate).</param>
/// <param name="Schema">The schema id (rename-schema, remove-schema, set-default-schema; optional for add-schema).</param>
/// <param name="Name">The schema name (add-schema, rename-schema); the new tag key (retag); the property the tags move to
/// (tags-to-property); the reference type's new name (rename-reference-type).</param>
/// <param name="Target">The schema id that what lives in the removed schema moves to (remove-schema); the process (set-lifecycle); the
/// child state (set-initial); the reference type merged into (merge-reference-types).</param>
/// <param name="Default">The schema id that becomes the default when the removed schema is the default (remove-schema).</param>
/// <param name="Resolution">What a delete does with references to the element (delete); absent means <see cref="DeleteResolution.Refuse"/>.</param>
/// <param name="Database">The database id (materialize-tables, materialize-entities, materialize-attributes, materialize-columns,
/// map-by-name, resync-binding).</param>
/// <param name="Entities">The entity ids (materialize-tables, materialize-attributes, materialize-columns, map-by-name,
/// resync-binding).</param>
/// <param name="Tables">The table and view ids (materialize-entities).</param>
/// <param name="Package">The package id the entities go to (materialize-entities); the domain whose tag vocabulary it is (retag and
/// tags-to-property; absent for the global one).</param>
/// <param name="Columns">The source columns, as keys or physical names (materialize-attributes and resync-binding, one entity); absent
/// means every column nothing in the binding names (materialize-attributes) or every column of the source (resync-binding).</param>
/// <param name="Attributes">The binding field references, attribute ids among them (materialize-columns, one entity); absent means every
/// attribute the binding leaves unmapped.</param>
/// <param name="Tags">The tag keys (retag).</param>
/// <param name="ExpectedHashes">Materialize, retag, tags-to-property, names-to-ids and the reference-type refactors: the hash the caller read of each element it expects the operation to change or delete; given,
/// an element that changed since, or that the operation changes but the map does not name, is a conflict.</param>
/// <param name="As">Create and update: the placeholder (<c>$name</c>) that names the operation's element for the whole batch
/// (next-phase-1-design.md section 5.1). Every id field of an operation may hold an id, a qualified name (<c>@kind:path</c>) or a
/// placeholder; the batch resolver turns them into ids before the operation is planned.</param>
/// <param name="ElementIds">The elements <c>names-to-ids</c> and <c>tags-to-property</c> act on, each an id, a qualified name or a
/// placeholder; absent means every element they apply to.</param>
/// <param name="Prefix">The prefix of the tags that move into the property <c>name</c> (tags-to-property).</param>
/// <param name="Redefine">fold-into-stereotype: write a <c>redefines</c> entry with the table's value for each redefinable facet in
/// which an own member differs from the stereotype's, instead of refusing the table; absent means false.</param>
/// <param name="ExcludeMissing">fold-into-stereotype: exclude each member the table lacks, instead of refusing the table; absent means
/// false.</param>
/// <param name="KeepNames">resync-binding: keep every attribute's name, even one that followed its column's name; absent means false.</param>
/// <param name="Code">The code a refactor renames or retires, as the code's canonical text (rename-code, retire-code).</param>
/// <param name="To">The new code (rename-code); the live code that replaces the retired one (retire-code).</param>
/// <param name="Codes">merge-reference-types: codes of the merged-away type mapped to live codes of the target, for codes the target does
/// not hold itself; absent maps none.</param>
public sealed record BatchOperation(
    BatchOp Op, string? Id, string? ExpectedHash, JsonElement? Element, string? Locale = null, string? Field = null, JsonElement? Value = null,
    string? Schema = null, string? Name = null, string? Target = null, string? Default = null, DeleteResolution? Resolution = null,
    string? Database = null, IReadOnlyList<string>? Entities = null, IReadOnlyList<string>? Tables = null, string? Package = null,
    IReadOnlyDictionary<string, string>? ExpectedHashes = null, IReadOnlyList<string>? Columns = null, IReadOnlyList<string>? Attributes = null,
    IReadOnlyList<string>? Tags = null, string? As = null, IReadOnlyList<string>? ElementIds = null, string? Prefix = null, bool? Redefine = null,
    bool? ExcludeMissing = null, bool? KeepNames = null, string? Code = null, string? To = null, IReadOnlyDictionary<string, string>? Codes = null);

/// <summary>An atomic batch of operations.</summary>
/// <param name="Operations">The operations, applied in order.</param>
public sealed record ModelBatch(IReadOnlyList<BatchOperation> Operations);

/// <summary>The result of parsing a batch.</summary>
/// <param name="Batch">The batch, or <see langword="null"/> when invalid.</param>
/// <param name="Diagnostics">Parse and schema diagnostics.</param>
public sealed record BatchParseResult(ModelBatch? Batch, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The result of a batch's dry run (<see cref="ModelStore.PreviewBatchAsync"/>).</summary>
/// <param name="Outcome">Saved when every operation would succeed; otherwise the first failure's outcome.</param>
/// <param name="Items">The result per operation, as an apply would report it, with no hashes or documents.</param>
/// <param name="Files">When saved, every file the batch would write or delete, ordered by path; empty otherwise.</param>
public sealed record BatchPreview(SaveOutcome Outcome, IReadOnlyList<SaveResult> Items, IReadOnlyList<BatchPreviewFile> Files)
{
    /// <summary>When saved, the placeholders and qualified names the batch resolved and the ids it would mint; empty otherwise.</summary>
    public BatchIds Ids { get; init; } = BatchIds.Empty;

    /// <summary>
    /// When saved, the diagnostics the change would add over its scope (never the whole model), in <see cref="Diagnostic.Order"/>;
    /// empty otherwise.
    /// </summary>
    public IReadOnlyList<Diagnostic> DiagnosticsAdded { get; init; } = [];

    /// <summary>When saved, the diagnostics the change would remove over its scope, in <see cref="Diagnostic.Order"/>; empty otherwise.</summary>
    public IReadOnlyList<Diagnostic> DiagnosticsRemoved { get; init; } = [];

    /// <summary>
    /// When saved, the migration facts the batch would record (their files are among <see cref="Files"/>); <see langword="null"/> when
    /// the change reaches every database, whose facts are computed on save (a dry run never resolves every database), or when not saved.
    /// </summary>
    public IReadOnlyList<RecordedFact>? Facts { get; init; }
}

/// <summary>A file a batch would write or delete.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Action"><c>created</c>, <c>changed</c> or <c>deleted</c>.</param>
/// <param name="Id">The element id the file holds, when it is an element document.</param>
/// <param name="Kind">The element kind, when it is an element document.</param>
/// <param name="Name">The element name, when it has one.</param>
/// <param name="BeforeHash">The hash of the file on disk, or <see langword="null"/> for a created file.</param>
/// <param name="Before">The file's text on disk, or <see langword="null"/> for a created file.</param>
/// <param name="After">The text the batch would write, or <see langword="null"/> for a deleted file.</param>
public sealed record BatchPreviewFile(string Path, string Action, string? Id, string? Kind, string? Name, string? BeforeHash, string? Before, string? After)
{
    /// <summary>The change as a unified diff with three lines of context, or <see langword="null"/> when none was computed.</summary>
    public string? Diff { get; init; }
}

/// <summary>The names a batch resolved and the ids it minted (next-phase-1-design.md section 5.1).</summary>
/// <param name="Names">Each placeholder declared and each qualified name used, to its id, ordinal by key.</param>
/// <param name="Minted">The ids the engine minted, in operation and document order.</param>
public sealed record BatchIds(IReadOnlyDictionary<string, string> Names, IReadOnlyList<MintedId> Minted)
{
    /// <summary>No names and no minted ids.</summary>
    public static BatchIds Empty { get; } = new(ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), []);
}

/// <summary>An id the engine minted for a batch.</summary>
/// <param name="Operation">The operation's index in the batch.</param>
/// <param name="Pointer">The JSON pointer into the operation's element (<c>""</c> for its root).</param>
/// <param name="Kind">The element kind, else the index kind of the sub-element, else <c>sub-element</c>.</param>
/// <param name="Id">The minted id.</param>
/// <param name="Name">The longest qualified form when the kind has names, computed once the element's references are ids.</param>
/// <param name="Placeholder">The placeholder the batch declared on the object with <c>as</c>, if any.</param>
public sealed record MintedId(int Operation, string Pointer, string Kind, string Id, string? Name, string? Placeholder);

/// <summary>The result of applying a batch.</summary>
/// <param name="Outcome">Saved when every operation succeeded; otherwise the first failure's outcome, and nothing was written.</param>
/// <param name="Items">The result per operation.</param>
/// <param name="Changes">What changed when saved.</param>
public sealed record BatchResult(SaveOutcome Outcome, IReadOnlyList<SaveResult> Items, ChangeSet? Changes)
{
    /// <summary>When saved, the placeholders and qualified names the batch resolved and the ids it minted; empty otherwise.</summary>
    public BatchIds Ids { get; init; } = BatchIds.Empty;

    /// <summary>The migration facts the batch created or updated (its items carry none); <see langword="null"/> when nothing was written.</summary>
    public IReadOnlyList<RecordedFact>? Facts { get; init; }
}

/// <summary>
/// What a batch's dry run collects on its way through the store (next-phase-1-design.md section 5.1): the files it would write or
/// delete, the diagnostics the change would add and remove over its scope, and the migration facts it would record.
/// </summary>
internal sealed class BatchPreviewSink
{
    /// <summary>The files, ordered by path, with their diffs.</summary>
    public List<BatchPreviewFile> Files { get; } = [];

    /// <summary>The diagnostics the change would add, in <see cref="Diagnostic.Order"/>, the MQ4101 of the facts it would record included.</summary>
    public IReadOnlyList<Diagnostic> Added { get; set; } = [];

    /// <summary>The diagnostics the change would remove, in <see cref="Diagnostic.Order"/>.</summary>
    public IReadOnlyList<Diagnostic> Removed { get; set; } = [];

    /// <summary>The facts the batch would record (none until a plan records some); <see langword="null"/> when the change reaches every database.</summary>
    public IReadOnlyList<RecordedFact>? Facts { get; set; } = [];
}
