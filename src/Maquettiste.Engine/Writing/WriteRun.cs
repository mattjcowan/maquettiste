using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// One run of stage 8 (engine-design.md sections 12.3 and 12.4). The intake loop reads processed units in order, checks each file
/// (path policy, plan list, duplicate and case-colliding paths) and queues it; <c>min(jobs, 8)</c> tasks drain a
/// bounded queue, deciding and writing each file. When a pack's last unit has arrived (by <see cref="WriteContext.UnitCountByPack"/>,
/// counting processed units), or at the end of the stream, the pack is closed: in apply mode its manifests and unit states are saved
/// and the journal gets its <c>pack</c> line. Orphans are handled once every pack is closed (a later pack may still produce the path);
/// packs that lose entries then save their manifests again. Cancellation is observed between files: a file
/// being written is finished, queued files are dropped, no pack is closed, and <see cref="OperationCanceledException"/> is thrown.
/// An I/O failure stops the run the same way and is rethrown; the journal then lets the next run resume.
/// </summary>
internal sealed class WriteRun
{
    private const string HandEditRule = "MQ6009";
    private const string RefusedRule = "MQ6004";
    private const string CollisionRule = "MQ6005";
    private const string UnitCollisionRule = "MQ6020";
    private const string BrokenBlockRule = "MQ6027";
    private const string TargetMissingRule = "MQ6028";

    private readonly IOutputPathPolicy _paths;
    private readonly IManifestStore _manifests;
    private readonly IDiffGenerator _diffs;
    private readonly WriteContext _context;
    private readonly IProgress<ProgressUpdate>? _progress;
    private readonly int _workerCount;
    private readonly string _repoRoot;
    private readonly bool _apply;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, UnitState>>>> _localStates = new(StringComparer.Ordinal);
    private readonly Channel<FileJob> _queue;
    private readonly Dictionary<string, PackRun> _packs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _claims = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _claimedBy = new(StringComparer.Ordinal);
    private readonly HashSet<string> _produced = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FileChange> _changes = new();
    private readonly ConcurrentQueue<Diagnostic> _diagnostics = new();
    private readonly List<DeferredOrphan> _deferred = [];
    private CancellationTokenSource? _stop;
    private ExceptionDispatchInfo? _failure;
    private int _written;
    private int _deleted;
    private int _tempCounter;
    private int _queued;
    private int _done;

    /// <summary>Creates a run.</summary>
    /// <param name="options">The engine options.</param>
    /// <param name="paths">The path policy.</param>
    /// <param name="manifests">The manifest store.</param>
    /// <param name="diffs">The diff generator.</param>
    /// <param name="context">The run context.</param>
    /// <param name="progress">Progress.</param>
    /// <param name="workerCount">Tasks draining the queue.</param>
    /// <param name="capacity">The queue capacity.</param>
    public WriteRun(EngineOptions options, IOutputPathPolicy paths, IManifestStore manifests, IDiffGenerator diffs, WriteContext context,
        IProgress<ProgressUpdate>? progress, int workerCount, int capacity)
    {
        _paths = paths;
        _manifests = manifests;
        _diffs = diffs;
        _context = context;
        _progress = progress;
        _workerCount = workerCount;
        _repoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RepoRoot));
        _apply = context.Mode == GenerationMode.Apply;
        _queue = Channel.CreateBounded<FileJob>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Runs the stage.</summary>
    /// <param name="units">Processed units, pack by pack.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The summary.</returns>
    public async Task<WriteSummary> RunAsync(IAsyncEnumerable<ProcessedUnit> units, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop = stop;
        InitializePacks();
        var workers = Enumerable.Range(0, _workerCount).Select(_ => Task.Run(WorkerAsync, CancellationToken.None)).ToArray();
        try
        {
            await foreach (var unit in units.WithCancellation(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                _failure?.Throw();
                await IntakeUnitAsync(unit, ct).ConfigureAwait(false);
            }

            foreach (var pack in _packs.Values.Where(p => !p.Closed).OrderBy(p => p.Name, StringComparer.Ordinal).ToList())
            {
                ct.ThrowIfCancellationRequested();
                await ClosePackAsync(pack, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            await FinishOrphansAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (_failure is not null)
        {
            // A worker's I/O failure surfaces here as a cancelled or faulted job; report the original failure.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            _queue.Writer.TryComplete();
            await Task.WhenAll(workers).ConfigureAwait(false);
            _stop = null;
        }

        _failure?.Throw();
        ct.ThrowIfCancellationRequested();

        var changes = _changes.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Pack, StringComparer.Ordinal).ThenBy(c => c.Kind).ToList();
        var diagnostics = _diagnostics
            .OrderBy(d => d.FilePath ?? "", StringComparer.Ordinal)
            .ThenBy(d => d.Rule, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToList();
        return new WriteSummary(changes, diagnostics, _written, _deleted);
    }

    private void InitializePacks()
    {
        foreach (var (pack, count) in _context.UnitCountByPack)
            GetPack(pack).Expected = count;
        foreach (var skipped in _context.Skipped)
        {
            GetPack(skipped.Unit.Pack.Name).Skipped.Add(skipped);
            // A skipped unit's outputs are produced by this run: they claim their paths and are never orphans.
            foreach (var output in skipped.Previous.Outputs)
            {
                _claims.TryAdd(output.Path, output.Path);
                _produced.Add(output.Path);
            }
        }

        if (_context.AllPacks)
        {
            // Packs that no longer exist have their manifests orphaned in full.
            foreach (var pack in _context.Manifests.Packs)
                GetPack(pack);
        }
    }

    private PackRun GetPack(string name)
    {
        if (!_packs.TryGetValue(name, out var pack))
        {
            pack = new PackRun(name);
            _packs[name] = pack;
        }

        return pack;
    }

    private async Task IntakeUnitAsync(ProcessedUnit unit, CancellationToken ct)
    {
        var planned = unit.Rendered.Unit;
        var pack = GetPack(planned.Pack.Name);
        if (pack.Closed)
            throw new InvalidOperationException($"A unit of pack '{pack.Name}' arrived after the pack was closed; units must arrive pack by pack.");
        pack.Received++;
        var record = new UnitRecord(unit, unit.Failed || unit.Rendered.Failed);
        pack.Units.Add(record);
        if (!record.Failed)
        {
            foreach (var file in unit.Files)
            {
                ct.ThrowIfCancellationRequested();
                var job = Intake(unit, file, pack, record);
                if (job is null)
                {
                    record.KeepPrevious = true;
                    continue;
                }

                pack.Jobs.Add(job);
                record.Jobs.Add(job);
                Interlocked.Increment(ref _queued);
                await _queue.Writer.WriteAsync(job, ct).ConfigureAwait(false);
            }
        }

        if (pack.Expected is { } expected && pack.Received >= expected)
            await ClosePackAsync(pack, ct).ConfigureAwait(false);
    }

    private FileJob? Intake(ProcessedUnit unit, OutputFile file, PackRun pack, UnitRecord record)
    {
        var check = _paths.Check(file.Path);
        if (!check.Allowed || check.Root is null)
        {
            Report(check.RuleId ?? RefusedRule, $"Output path refused: {file.Path}: {check.Reason}", file.Path);
            return null;
        }

        var path = check.NormalizedPath;
        var root = check.Root;
        if (_context.PlannedPaths is { } planned && !planned.Contains(path))
        {
            Report(RefusedRule, $"Output path refused: {path} is not in the plan being applied.", path);
            return null;
        }

        var unitKey = record.Unit.Rendered.Unit.Key;
        if (_claims.TryGetValue(path, out var first))
        {
            // Two elements of one unit rendering the same path is a pattern that is not unique per element (generation-ui.md 5.3).
            if (string.Equals(first, path, StringComparison.Ordinal) && _claimedBy.TryGetValue(path, out var otherKey)
                && SameUnit(otherKey, unitKey) && !string.Equals(otherKey, unitKey, StringComparison.Ordinal))
            {
                var (a, b) = string.CompareOrdinal(otherKey, unitKey) < 0 ? (otherKey, unitKey) : (unitKey, otherKey);
                Report(UnitCollisionRule, $"Unit '{UnitIdOf(unitKey)}' renders {path} for two elements, '{ElementOf(a)}' and '{ElementOf(b)}': its output pattern is not unique per element.", path);
                return null;
            }

            Report(CollisionRule, string.Equals(first, path, StringComparison.Ordinal)
                ? $"Duplicate output path: {path} is produced more than once."
                : $"Case-colliding output path: {path} differs from {first} only by case.", path);
            return null;
        }

        _claims[path] = path;
        _claimedBy[path] = unitKey;
        _produced.Add(path);

        var unitName = UnitName(record.Unit.Rendered.Unit.Key, pack.Name) + (file.Role == FileRole.Companion ? "#companion" : "");
        if (file.Mode == OutputMode.Block)
        {
            var packUnit = record.Unit.Rendered.Unit.Unit;
            var block = new BlockInfo(string.IsNullOrWhiteSpace(packUnit.BlockComment) ? ManagedBlock.DefaultComment : packUnit.BlockComment, ManagedBlock.Marker(pack.Name, unitName), packUnit.CreateFile);
            var blockHash = ManagedBlock.Prefix + ManagedBlock.BodyHash(file.ManifestHash);
            return new FileJob(pack.Name, record.Unit.Rendered.Unit.Key, unitName, path, root, file, false, blockHash) { Block = block };
        }

        var owned = file.Mode == OutputMode.Once || file.Role == FileRole.Companion || ManifestHashes.IsOwned(file.ManifestHash);
        var manifestHash = owned && !ManifestHashes.IsOwned(file.ManifestHash) ? ManifestHashes.OwnedPrefix + file.ContentHash : file.ManifestHash;
        return new FileJob(pack.Name, record.Unit.Rendered.Unit.Key, unitName, path, root, file, owned, manifestHash);
    }

    private async Task WorkerAsync()
    {
        await foreach (var job in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Cancellation between files: a queued file is dropped, a started one is finished.
            if (_stop is not { IsCancellationRequested: false })
            {
                job.Completion.TrySetCanceled();
                continue;
            }

            try
            {
                job.Completion.TrySetResult(await ProcessAsync(job).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(ex), null);
                job.Completion.TrySetException(ex);
                try
                {
                    await (_stop?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private async Task<FileResult> ProcessAsync(FileJob job)
    {
        var full = FullPath(job.Path);
        var disk = File.Exists(full) ? await File.ReadAllBytesAsync(full, CancellationToken.None).ConfigureAwait(false) : null;
        var diskHash = disk is null ? null : ContentHash.Of(disk);
        var old = _context.Manifests.TryGet(job.Path, out var entry, out _) ? entry : null;
        if (job.Block is not null)
            return await ProcessBlockAsync(job, full, disk, old).ConfigureAwait(false);
        string? respell = null;
        if (old is null && disk is not null && CaseVariant(job) is { } variant)
        {
            // A case-insensitive disk resolved this path to the file of an entry spelled with other case (an output renamed only by
            // case): that entry is M, so the file is not taken for an untracked file in the way.
            old = variant.Entry;
            respell = variant.OnDisk;
        }

        var policy = Policy(job.Pack);
        var content = job.File.Content;

        FileChangeKind kind;
        ManifestEntry? next;
        var write = false;
        var keepPrevious = false;
        var fresh = new ManifestEntry(job.Path, job.ManifestHash, job.UnitName);
        if (job.Owned)
        {
            if (disk is not null)
            {
                kind = FileChangeKind.Kept;
                var hash = old is not null && ManifestHashes.IsOwned(old.Hash) ? old.Hash : ManifestHashes.OwnedPrefix + diskHash;
                next = new ManifestEntry(job.Path, hash, job.UnitName);
            }
            else if (job.File.ContentOmitted)
            {
                kind = FileChangeKind.Conflict;
                next = old;
                keepPrevious = true;
                Report(HandEditRule, $"Hand edit: {job.Path} was removed after the plan was made.", job.Path);
            }
            else
            {
                kind = FileChangeKind.Added;
                next = fresh;
                write = true;
            }
        }
        else if (job.File.ContentOmitted)
        {
            if (diskHash is not null && string.Equals(diskHash, job.File.ContentHash, StringComparison.Ordinal))
            {
                kind = FileChangeKind.Unchanged;
                next = fresh;
            }
            else
            {
                kind = FileChangeKind.Conflict;
                next = old;
                keepPrevious = true;
                Report(HandEditRule, $"Hand edit: {job.Path} changed after the plan was made.", job.Path);
            }
        }
        else if (disk is null)
        {
            kind = FileChangeKind.Added;
            next = fresh;
            write = true;
        }
        else if (string.Equals(diskHash, job.File.ContentHash, StringComparison.Ordinal))
        {
            // Identical bytes: no write and the mtime is left alone; an untracked identical file is adopted.
            kind = FileChangeKind.Unchanged;
            next = fresh;
        }
        else if ((old is not null && string.Equals(ManifestHashes.Comparable(old.Hash, disk, diskHash!), old.Hash, StringComparison.Ordinal))
            || await WrittenHereAsync(job.Pack, job.UnitKey, job.Path, disk, diskHash!).ConfigureAwait(false))
        {
            kind = FileChangeKind.Modified;
            next = fresh;
            write = true;
        }
        else
        {
            var what = old is null ? "an untracked file is in the way" : "the file differs from its manifest entry";
            switch (policy)
            {
                case HandEditPolicy.Overwrite:
                    kind = FileChangeKind.HandEdited;
                    next = fresh;
                    write = true;
                    Report(HandEditRule, $"Hand edit overwritten: {job.Path}: {what}.", job.Path, DiagnosticSeverity.Warning);
                    break;
                case HandEditPolicy.Skip:
                    kind = FileChangeKind.HandEdited;
                    next = old;
                    keepPrevious = true;
                    Report(HandEditRule, $"Hand edit skipped: {job.Path}: {what}.", job.Path, DiagnosticSeverity.Warning);
                    break;
                default:
                    kind = FileChangeKind.Conflict;
                    next = old;
                    keepPrevious = true;
                    Report(HandEditRule, $"Hand edit: {job.Path}: {what}; the hand-edit policy is fail.", job.Path);
                    break;
            }
        }

        if (_apply && respell is not null && !keepPrevious && !job.Owned)
            Respell(respell, full);

        return await FinishFileAsync(job, full, kind, next, write, keepPrevious, disk, content, old?.Hash ?? diskHash,
            _context.IncludeDiffs && !job.File.ContentOmitted && kind is not FileChangeKind.Kept).ConfigureAwait(false);
    }

    /// <summary>Writes a decided file (apply only), records its stat for the unit state, lists the change and reports progress.</summary>
    private async Task<FileResult> FinishFileAsync(FileJob job, string full, FileChangeKind kind, ManifestEntry? next, bool write, bool keepPrevious,
        byte[]? disk, ReadOnlyMemory<byte> content, string? oldHash, bool includeDiff)
    {
        if (write && _apply)
        {
            var tag = _context.RunId + "-" + Interlocked.Increment(ref _tempCounter).ToString(CultureInfo.InvariantCulture);
            await AtomicFile.WriteAsync(full, content, tag, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _written);
            if (_context.Journal is { } journal)
                await journal.RecordWriteAsync(job.Pack, next!, CancellationToken.None).ConfigureAwait(false);
        }

        UnitOutput? output = null;
        if (_apply && next is not null && !keepPrevious)
        {
            var info = new FileInfo(full);
            if (info.Exists)
                output = new UnitOutput(job.Path, next.Hash, info.Length, info.LastWriteTimeUtc.Ticks);
        }

        if (kind != FileChangeKind.Unchanged || _context.ListUnchanged)
        {
            var diff = includeDiff && kind != FileChangeKind.Unchanged ? _diffs.Unified(job.Path, disk ?? [], content.Span) : null;
            _changes.Enqueue(new FileChange(job.Path, kind, job.Pack, job.UnitKey, oldHash, next?.Hash ?? job.ManifestHash, diff));
        }

        var done = Interlocked.Increment(ref _done);
        _progress?.Report(new ProgressUpdate(PipelineStage.Write, done, Volatile.Read(ref _queued), job.Path, job.Pack));
        return new FileResult(kind, next, keepPrevious, output);
    }

    /// <summary>
    /// The decision for a <c>block</c> file (engine-design.md section 12.3b): the unit's block in the file on disk is the file for
    /// every rule of section 12.3 (its lines against the manifest hash), and only the block's lines are ever changed. A missing
    /// file is created only with <c>createFile</c>; otherwise the unit writes nothing (MQ6028, info) and renders again next run.
    /// A file holding the block twice, or a block that is not closed, is left alone (MQ6027).
    /// </summary>
    private async Task<FileResult> ProcessBlockAsync(FileJob job, string full, byte[]? disk, ManifestEntry? old)
    {
        var block = job.Block!;
        var body = job.File.ContentOmitted ? null : System.Text.Encoding.UTF8.GetString(job.File.Content.Span);
        var bodyHash = ManagedBlock.BodyHash(job.ManifestHash);
        // A block keeps the "created" mark of its entry, so the file it created is still removed with it.
        var created = old is not null && ManagedBlock.IsCreated(old.Hash);
        ManifestEntry Entry(bool createdFile) => new(job.Path, (createdFile ? ManagedBlock.CreatedPrefix : ManagedBlock.Prefix) + bodyHash, job.UnitName);

        if (disk is null)
        {
            if (!block.CreateFile)
            {
                Report(TargetMissingRule, $"Block target missing: {job.Path} does not exist, so unit '{job.UnitName}' wrote nothing (target-missing); set createFile to create it.",
                    job.Path, DiagnosticSeverity.Info);
                var done = Interlocked.Increment(ref _done);
                _progress?.Report(new ProgressUpdate(PipelineStage.Write, done, Volatile.Read(ref _queued), job.Path, job.Pack));
                return new FileResult(FileChangeKind.Unchanged, null, true, null);
            }

            if (body is null)
                return Conflict("was removed after the plan was made");
            var added = ManagedBlock.Insert([], block.Comment, block.Marker, body);
            return await FinishFileAsync(job, full, FileChangeKind.Added, Entry(true), true, false, null, added, old?.Hash, _context.IncludeDiffs).ConfigureAwait(false);
        }

        var scan = ManagedBlock.Find(disk, block.Marker);
        if (scan.Error is not null)
        {
            Report(BrokenBlockRule, $"Block not updated: {job.Path}: {scan.Error}; the file is left alone until it holds the block once.", job.Path);
            return await FinishFileAsync(job, full, FileChangeKind.Conflict, old, false, true, disk, disk, old?.Hash, false).ConfigureAwait(false);
        }

        var diskBody = scan.Found ? ContentHash.Of(disk.AsSpan(scan.BodyStart, scan.BodyEnd - scan.BodyStart)) : null;
        if (body is null)
        {
            // A plan apply that carries no bytes: the plan found the block unchanged, so it must still be.
            return string.Equals(diskBody, bodyHash, StringComparison.Ordinal)
                ? await FinishFileAsync(job, full, FileChangeKind.Unchanged, Entry(created), false, false, disk, disk, old?.Hash, false).ConfigureAwait(false)
                : Conflict("changed after the plan was made");
        }

        var updated = scan.Found ? ManagedBlock.Replace(disk, scan, block.Comment, block.Marker, body) : ManagedBlock.Insert(disk, block.Comment, block.Marker, body);
        if (updated.AsSpan().SequenceEqual(disk))
            return await FinishFileAsync(job, full, FileChangeKind.Unchanged, Entry(created), false, false, disk, updated, old?.Hash, false).ConfigureAwait(false);

        FileChangeKind kind;
        if (!scan.Found && old is null)
            kind = FileChangeKind.Added; // a new block in a file the team has
        else if (scan.Found && string.Equals(diskBody, bodyHash, StringComparison.Ordinal))
            kind = FileChangeKind.Modified; // only the delimiter lines' comment changes
        else if (old is not null && scan.Found && string.Equals(diskBody, ManagedBlock.BodyHash(old.Hash), StringComparison.Ordinal))
            kind = FileChangeKind.Modified;
        else if (scan.Found && await WrittenHereAsync(job.Pack, job.UnitKey, job.Path, disk, ContentHash.Of(disk)).ConfigureAwait(false))
            kind = FileChangeKind.Modified;
        else
        {
            var what = scan.Found ? old is null ? "an untracked block is in the way" : "the block differs from its manifest entry" : "its block was removed by hand";
            switch (Policy(job.Pack))
            {
                case HandEditPolicy.Overwrite:
                    Report(HandEditRule, $"Hand edit overwritten: {job.Path}: {what}.", job.Path, DiagnosticSeverity.Warning);
                    kind = FileChangeKind.HandEdited;
                    break;
                case HandEditPolicy.Skip:
                    Report(HandEditRule, $"Hand edit skipped: {job.Path}: {what}.", job.Path, DiagnosticSeverity.Warning);
                    return await FinishFileAsync(job, full, FileChangeKind.HandEdited, old, false, true, disk, updated, old?.Hash, _context.IncludeDiffs).ConfigureAwait(false);
                default:
                    Report(HandEditRule, $"Hand edit: {job.Path}: {what}; the hand-edit policy is fail.", job.Path);
                    return await FinishFileAsync(job, full, FileChangeKind.Conflict, old, false, true, disk, updated, old?.Hash, _context.IncludeDiffs).ConfigureAwait(false);
            }
        }

        return await FinishFileAsync(job, full, kind, Entry(created), true, false, disk, updated,
            old?.Hash ?? (diskBody is null ? null : ManagedBlock.Prefix + diskBody), _context.IncludeDiffs).ConfigureAwait(false);

        FileResult Conflict(string why)
        {
            Report(HandEditRule, $"Hand edit: {job.Path} {why}.", job.Path);
            _changes.Enqueue(new FileChange(job.Path, FileChangeKind.Conflict, job.Pack, job.UnitKey, old?.Hash, old?.Hash ?? job.ManifestHash, null));
            var done = Interlocked.Increment(ref _done);
            _progress?.Report(new ProgressUpdate(PipelineStage.Write, done, Volatile.Read(ref _queued), job.Path, job.Pack));
            return new FileResult(FileChangeKind.Conflict, old, true, null);
        }
    }

    /// <summary>
    /// Whether the engine itself last wrote these bytes at this path in this checkout: the unit's recorded state (in the cache, never
    /// shared) lists the path with a manifest hash the disk still matches. Such a file is not a hand edit even when the manifest,
    /// which lives with the model and moves with it (a pull), records newer bytes the checkout has not generated yet.
    /// </summary>
    private async Task<bool> WrittenHereAsync(string pack, string unitKey, string path, byte[] disk, string diskHash)
    {
        var states = await _localStates.GetOrAdd(pack, p => new Lazy<Task<IReadOnlyDictionary<string, UnitState>>>(
            () => _context.State.LoadAsync(p, CancellationToken.None))).Value.ConfigureAwait(false);
        if (!states.TryGetValue(unitKey, out var state))
            return false;
        foreach (var output in state.Outputs)
        {
            if (string.Equals(output.Path, path, StringComparison.Ordinal))
            {
                return !ManifestHashes.IsOwned(output.ManifestHash)
                    && string.Equals(ManifestHashes.Comparable(output.ManifestHash, disk, diskHash, ManagedBlock.MarkerOfKey(unitKey)), output.ManifestHash, StringComparison.Ordinal);
            }
        }

        return false;
    }

    private async Task ClosePackAsync(PackRun pack, CancellationToken ct)
    {
        pack.Closed = true;
        await Task.WhenAll(pack.Jobs.Select(j => j.Completion.Task)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var results = new Dictionary<string, FileResult>(StringComparer.Ordinal);
        // Keyed by path; the manifest store sorts the entries it writes, so no ordered map is needed while collecting them.
        var next = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        // Paths whose old entry a job already carries (a conflict on a file renamed only by case keeps the old spelling's entry).
        var carried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in pack.Jobs)
        {
            var result = job.Completion.Task.Result;
            results[job.Path] = result;
            if (result.Entry is not null)
            {
                next[job.Path] = result.Entry;
                carried.Add(result.Entry.Path);
            }
        }

        pack.Next = next;

        // Units whose old entries stay: skipped units, failed units, and units with a file this run could not take.
        var keepUnits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skipped in pack.Skipped)
            keepUnits.Add(UnitName(skipped.Unit.Key, pack.Name));
        foreach (var record in pack.Units.Where(u => u.Failed || u.KeepPrevious))
            keepUnits.Add(UnitName(record.Unit.Rendered.Unit.Key, pack.Name));

        foreach (var (path, (entry, bucket)) in _context.Manifests.Data.Pack(pack.Name).Entries)
        {
            if (results.ContainsKey(path) || carried.Contains(path))
                continue;

            // The entries this run keeps (skipped units: nearly all of them in an incremental run) and the paths another pack
            // produces need no path check: nothing is written or deleted for them. Only an entry an unfinished journal alone
            // knows about is checked first, since no manifest ever held it.
            if (bucket != ManifestBucket.Journal)
            {
                if (keepUnits.Contains(StripCompanion(entry.Unit)))
                {
                    next[path] = entry;
                    continue;
                }

                if (_produced.Contains(path))
                    continue; // another pack produces it in this run
            }

            var check = _paths.Check(path);
            if (bucket == ManifestBucket.Journal && (!check.Allowed || check.Root is null))
            {
                Report(RefusedRule, $"Output path refused: {path} (from an unfinished run's journal): {check.Reason}", path);
                continue;
            }

            if (keepUnits.Contains(StripCompanion(entry.Unit)))
            {
                next[path] = entry;
                continue;
            }

            if (_produced.Contains(path))
                continue; // another pack produces it in this run

            if (!check.Allowed || check.Root is null)
            {
                Report(RefusedRule, $"Output path refused: orphan {path} is not deleted: {check.Reason}", path);
                next[path] = entry;
                continue;
            }

            if (_context.PlannedPaths is { } planned && !planned.Contains(path))
            {
                Report(RefusedRule, $"Output path refused: orphan {path} is not in the plan being applied.", path);
                next[path] = entry;
                continue;
            }

            // Deferred to the end of the stream, since a later pack may still produce the path: the entry stays for now.
            next[path] = entry;
            _deferred.Add(new DeferredOrphan(pack, entry, check.Root));
        }

        if (!_apply)
            return;

        // Two independent files (the manifests and the unit states read the closed pack only): built and saved concurrently, both
        // before the journal's pack line.
        await Task.WhenAll(
            Task.Run(() => SaveManifestsAsync(pack, ct), CancellationToken.None),
            Task.Run(() => SaveUnitStatesAsync(pack, results, ct), CancellationToken.None)).ConfigureAwait(false);
        if (_context.Journal is { } journal)
            await journal.RecordPackCompleteAsync(pack.Name, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the orphans deferred when their packs closed, once every pack is closed: an orphan that a later pack produced (or, on
    /// a case-insensitive disk, that is the same file as a path produced under other case) is dropped without touching the file;
    /// the others are deleted or kept by <see cref="OrphanAsync"/>. In apply mode, emptied folders are pruned, and each pack that
    /// lost an entry has its manifests saved again and a new journal <c>pack</c> line.
    /// </summary>
    private async Task FinishOrphansAsync(CancellationToken ct)
    {
        var deletedFolders = new List<(string Folder, string RootPath)>();
        var touched = new SortedDictionary<string, PackRun>(StringComparer.Ordinal);
        foreach (var orphan in _deferred)
        {
            ct.ThrowIfCancellationRequested();
            var path = orphan.Entry.Path;
            var keep = !_produced.Contains(path)
                && !SameFileProducedUnderOtherCase(path)
                && await OrphanAsync(orphan.Pack.Name, orphan.Entry, orphan.Root, deletedFolders).ConfigureAwait(false);
            if (!keep)
            {
                orphan.Pack.Next!.Remove(path);
                touched[orphan.Pack.Name] = orphan.Pack;
            }
        }

        if (!_apply)
            return;

        foreach (var (folder, rootPath) in deletedFolders)
            PruneEmptyFolders(folder, rootPath);

        foreach (var pack in touched.Values)
        {
            await SaveManifestsAsync(pack, ct).ConfigureAwait(false);
            if (_context.Journal is { } journal)
                await journal.RecordPackCompleteAsync(pack.Name, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task SaveManifestsAsync(PackRun pack, CancellationToken ct)
    {
        var list = pack.Next!.Values.ToList();
        list.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        await _manifests.SavePackAsync(pack.Name, list, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the entry of the job's pack whose path differs from the job's only by case, when the disk resolves the job's path to
    /// that entry's file (a case-insensitive disk). On a case-sensitive disk the job's path names a different file, so there is none.
    /// </summary>
    private (ManifestEntry Entry, string OnDisk)? CaseVariant(FileJob job)
    {
        foreach (var (path, (entry, _)) in _context.Manifests.Data.Pack(job.Pack).Entries)
        {
            if (!string.Equals(path, job.Path, StringComparison.OrdinalIgnoreCase) || string.Equals(path, job.Path, StringComparison.Ordinal))
                continue;
            var onDisk = FileSystemPaths.OnDiskSpelling(_repoRoot, job.Path);
            return onDisk is not null && string.Equals(onDisk, path, StringComparison.Ordinal) ? (entry, onDisk) : null;
        }

        return null;
    }

    /// <summary>Whether a path produced in this run differs from <paramref name="path"/> only by case and the disk holds one file for both.</summary>
    private bool SameFileProducedUnderOtherCase(string path)
    {
        if (!_claims.TryGetValue(path, out var claimed) || string.Equals(claimed, path, StringComparison.Ordinal))
            return false;
        var mine = FileSystemPaths.OnDiskSpelling(_repoRoot, path);
        return mine is not null && string.Equals(mine, FileSystemPaths.OnDiskSpelling(_repoRoot, claimed), StringComparison.Ordinal);
    }

    /// <summary>
    /// Makes the file name on disk follow the new case, in two renames (to a temporary name, then to the target), since a
    /// case-insensitive disk treats a direct rename as a no-op. Only the last segment is respelled; folders keep their case.
    /// </summary>
    private void Respell(string onDisk, string full)
    {
        var folder = Path.GetDirectoryName(full)!;
        var from = Path.GetFileName(onDisk);
        if (string.Equals(from, Path.GetFileName(full), StringComparison.Ordinal))
            return;
        var temp = AtomicFile.TempPath(full, _context.RunId + "-" + Interlocked.Increment(ref _tempCounter).ToString(CultureInfo.InvariantCulture));
        File.Move(Path.Combine(folder, from), temp);
        File.Move(temp, full);
    }

    /// <summary>Handles one orphan; returns whether its entry stays in the manifest.</summary>
    private async Task<bool> OrphanAsync(string pack, ManifestEntry entry, OutputRootInfo root, List<(string, string)> deletedFolders)
    {
        var unitKey = pack + "/" + StripCompanion(entry.Unit);
        if (ManifestHashes.IsOwned(entry.Hash))
        {
            // Owned files belong to the team: kept on disk, dropped from the manifest (D29).
            _changes.Enqueue(new FileChange(entry.Path, FileChangeKind.OrphanedOwned, pack, unitKey, entry.Hash, null, null));
            return false;
        }

        var full = FullPath(entry.Path);
        var disk = File.Exists(full) ? await File.ReadAllBytesAsync(full, CancellationToken.None).ConfigureAwait(false) : null;
        if (disk is null)
        {
            _changes.Enqueue(new FileChange(entry.Path, FileChangeKind.Deleted, pack, unitKey, entry.Hash, null, null));
            return false;
        }

        if (ManagedBlock.IsBlock(entry.Hash))
            return await OrphanBlockAsync(pack, unitKey, entry, full, disk, root, deletedFolders).ConfigureAwait(false);

        var diskHash = ContentHash.Of(disk);
        var intact = string.Equals(ManifestHashes.Comparable(entry.Hash, disk, diskHash), entry.Hash, StringComparison.Ordinal)
            || await WrittenHereAsync(pack, unitKey, entry.Path, disk, diskHash).ConfigureAwait(false);
        FileChangeKind kind;
        bool delete;
        if (intact)
        {
            kind = FileChangeKind.Deleted;
            delete = true;
        }
        else
        {
            switch (Policy(pack))
            {
                case HandEditPolicy.Overwrite:
                    kind = FileChangeKind.HandEdited;
                    delete = true;
                    Report(HandEditRule, $"Hand edit overwritten: orphan {entry.Path} differs from its manifest entry and is deleted.", entry.Path, DiagnosticSeverity.Warning);
                    break;
                case HandEditPolicy.Skip:
                    kind = FileChangeKind.HandEdited;
                    delete = false;
                    Report(HandEditRule, $"Hand edit skipped: orphan {entry.Path} differs from its manifest entry and is kept.", entry.Path, DiagnosticSeverity.Warning);
                    break;
                default:
                    kind = FileChangeKind.Conflict;
                    delete = false;
                    Report(HandEditRule, $"Hand edit: orphan {entry.Path} differs from its manifest entry; the hand-edit policy is fail.", entry.Path);
                    break;
            }
        }

        var diff = _context.IncludeDiffs ? _diffs.Unified(entry.Path, disk, []) : null;
        _changes.Enqueue(new FileChange(entry.Path, kind, pack, unitKey, entry.Hash, null, diff));
        if (delete && _apply)
        {
            File.Delete(full);
            Interlocked.Increment(ref _deleted);
            if (_context.Journal is { } journal)
                await journal.RecordDeleteAsync(pack, entry.Path, CancellationToken.None).ConfigureAwait(false);
            deletedFolders.Add((Path.GetDirectoryName(full)!, root.Path));
        }

        return !delete;
    }

    /// <summary>
    /// Handles an orphaned block (its unit is gone or no longer produces the file): the block's lines are removed and the rest of the
    /// file is kept, and the file is deleted when the engine created it and nothing but whitespace is left. A block edited by hand
    /// follows the hand-edit policy; a file that no longer holds the block just loses the entry; a file holding it twice is left
    /// alone (MQ6027) with its entry. Returns whether the entry stays.
    /// </summary>
    private async Task<bool> OrphanBlockAsync(string pack, string unitKey, ManifestEntry entry, string full, byte[] disk, OutputRootInfo root,
        List<(string, string)> deletedFolders)
    {
        var marker = ManagedBlock.Marker(pack, entry.Unit);
        var scan = ManagedBlock.Find(disk, marker);
        if (scan.Error is not null)
        {
            Report(BrokenBlockRule, $"Block not removed: {entry.Path}: {scan.Error}; the file is left alone until it holds the block once.", entry.Path);
            _changes.Enqueue(new FileChange(entry.Path, FileChangeKind.Conflict, pack, unitKey, entry.Hash, null, null));
            return true;
        }

        if (!scan.Found)
        {
            // The block is already gone: only the entry goes.
            _changes.Enqueue(new FileChange(entry.Path, FileChangeKind.Deleted, pack, unitKey, entry.Hash, null, null));
            return false;
        }

        var diskBody = ContentHash.Of(disk.AsSpan(scan.BodyStart, scan.BodyEnd - scan.BodyStart));
        var intact = string.Equals(diskBody, ManagedBlock.BodyHash(entry.Hash), StringComparison.Ordinal)
            || await WrittenHereAsync(pack, unitKey, entry.Path, disk, ContentHash.Of(disk)).ConfigureAwait(false);
        var kind = FileChangeKind.Deleted;
        if (!intact)
        {
            switch (Policy(pack))
            {
                case HandEditPolicy.Overwrite:
                    kind = FileChangeKind.HandEdited;
                    Report(HandEditRule, $"Hand edit overwritten: orphan block in {entry.Path} differs from its manifest entry and is removed.", entry.Path, DiagnosticSeverity.Warning);
                    break;
                case HandEditPolicy.Skip:
                    Report(HandEditRule, $"Hand edit skipped: orphan block in {entry.Path} differs from its manifest entry and is kept.", entry.Path, DiagnosticSeverity.Warning);
                    _changes.Enqueue(new FileChange(entry.Path, FileChangeKind.HandEdited, pack, unitKey, entry.Hash, null, null));
                    return true;
                default:
                    Report(HandEditRule, $"Hand edit: orphan block in {entry.Path} differs from its manifest entry; the hand-edit policy is fail.", entry.Path);
                    _changes.Enqueue(new FileChange(entry.Path, FileChangeKind.Conflict, pack, unitKey, entry.Hash, null, null));
                    return true;
            }
        }

        var remaining = ManagedBlock.Remove(disk, scan);
        var deleteFile = ManagedBlock.IsCreated(entry.Hash) && ManagedBlock.IsBlank(remaining);
        var diff = _context.IncludeDiffs ? _diffs.Unified(entry.Path, disk, deleteFile ? [] : remaining) : null;
        _changes.Enqueue(new FileChange(entry.Path, kind, pack, unitKey, entry.Hash, null, diff));
        if (!_apply)
            return false;
        if (deleteFile)
        {
            File.Delete(full);
            Interlocked.Increment(ref _deleted);
            if (_context.Journal is { } journal)
                await journal.RecordDeleteAsync(pack, entry.Path, CancellationToken.None).ConfigureAwait(false);
            deletedFolders.Add((Path.GetDirectoryName(full)!, root.Path));
        }
        else
        {
            var tag = _context.RunId + "-" + Interlocked.Increment(ref _tempCounter).ToString(CultureInfo.InvariantCulture);
            await AtomicFile.WriteAsync(full, remaining, tag, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _written);
            if (_context.Journal is { } journal)
                await journal.RecordDeleteAsync(pack, entry.Path, CancellationToken.None).ConfigureAwait(false);
        }

        return false;
    }

    private async Task SaveUnitStatesAsync(PackRun pack, Dictionary<string, FileResult> results, CancellationToken ct)
    {
        // The previous states matter only for units that keep theirs, so the file is read only when one does.
        IReadOnlyDictionary<string, UnitState>? previous = null;
        var states = new Dictionary<string, UnitState>(pack.Skipped.Count + pack.Units.Count, StringComparer.Ordinal);
        foreach (var skipped in pack.Skipped)
            states[skipped.Unit.Key] = skipped.Previous;
        foreach (var record in pack.Units)
        {
            var rendered = record.Unit.Rendered;
            var key = rendered.Unit.Key;
            var keep = record.Failed || record.KeepPrevious || record.Jobs.Any(j => results[j.Path].KeepPrevious || results[j.Path].Output is null);
            if (keep)
            {
                // The unit renders again next run: its previous state (if any) no longer matches its inputs or outputs.
                previous ??= await _context.State.LoadAsync(pack.Name, ct).ConfigureAwait(false);
                if (previous.TryGetValue(key, out var old))
                    states[key] = old;
                continue;
            }

            var outputs = record.Jobs.Select(j => results[j.Path].Output!).OrderBy(o => o.Path, StringComparer.Ordinal).ToList();
            var readKeys = rendered.ReadKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            states[key] = new UnitState(key, rendered.InputHash, readKeys, outputs)
            {
                KeyHashes = rendered.KeyHashes.Length == readKeys.Count * Planning.KeyHashes.Size ? rendered.KeyHashes : default,
                StaticParts = rendered.Unit.StaticParts,
                Names = rendered.Names,
            };
        }

        var ordered = states.Values.ToList();
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        await _context.State.SaveAsync(pack.Name, ordered, ct).ConfigureAwait(false);
    }

    private void PruneEmptyFolders(string folder, string rootPath)
    {
        var rootFull = rootPath.Length == 0 ? _repoRoot : Path.Combine(_repoRoot, rootPath.Replace('/', Path.DirectorySeparatorChar));
        var current = folder;
        while (FileSystemPaths.IsUnder(current, rootFull, allowEqual: false) && Directory.Exists(current))
        {
            var relative = Path.GetRelativePath(_repoRoot, current).Replace(Path.DirectorySeparatorChar, '/');
            if (!_paths.Check(relative).Allowed || Directory.EnumerateFileSystemEntries(current).Any())
                return;
            try
            {
                Directory.Delete(current, recursive: false);
            }
            catch (IOException)
            {
                return;
            }

            current = Path.GetDirectoryName(current)!;
        }
    }

    private HandEditPolicy Policy(string pack) => _context.PolicyByPack.TryGetValue(pack, out var policy) ? policy : HandEditPolicy.Fail;

    private string FullPath(string repoRelativePath) => Path.Combine(_repoRoot, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private void Report(string rule, string message, string? path, DiagnosticSeverity? severity = null)
    {
        var diagnostic = RuleCatalog.Create(rule, message, filePath: path);
        _diagnostics.Enqueue(severity is { } s ? diagnostic with { Severity = s } : diagnostic);
    }

    private static string UnitName(string unitKey, string pack) =>
        unitKey.StartsWith(pack + "/", StringComparison.Ordinal) ? unitKey[(pack.Length + 1)..] : unitKey;

    /// <summary>The unit part of a unit key: <c>&lt;pack&gt;/&lt;unitId&gt;</c> with the element removed.</summary>
    private static string UnitIdOf(string unitKey) => unitKey.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? unitKey[..colon] : unitKey;

    private static string ElementOf(string unitKey) => unitKey.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0 ? unitKey[(colon + 1)..] : "(model)";

    private static bool SameUnit(string a, string b) => string.Equals(UnitIdOf(a), UnitIdOf(b), StringComparison.Ordinal);

    private static string StripCompanion(string unit) =>
        unit.EndsWith("#companion", StringComparison.Ordinal) ? unit[..^"#companion".Length] : unit;

    private sealed class PackRun(string name)
    {
        public string Name { get; } = name;

        public int? Expected { get; set; }

        public int Received { get; set; }

        public bool Closed { get; set; }

        public List<FileJob> Jobs { get; } = [];

        public List<UnitRecord> Units { get; } = [];

        public List<SkippedUnit> Skipped { get; } = [];

        public Dictionary<string, ManifestEntry>? Next { get; set; }
    }

    private sealed record DeferredOrphan(PackRun Pack, ManifestEntry Entry, OutputRootInfo Root);

    private sealed class UnitRecord(ProcessedUnit unit, bool failed)
    {
        public ProcessedUnit Unit { get; } = unit;

        public bool Failed { get; } = failed;

        public bool KeepPrevious { get; set; }

        public List<FileJob> Jobs { get; } = [];
    }

    private sealed record FileJob(string Pack, string UnitKey, string UnitName, string Path, OutputRootInfo Root, OutputFile File, bool Owned, string ManifestHash)
    {
        public TaskCompletionSource<FileResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The block of a <c>block</c> file, else <see langword="null"/>.</summary>
        public BlockInfo? Block { get; init; }
    }

    /// <summary>A <c>block</c> file's delimiters and whether a missing file is created.</summary>
    private sealed record BlockInfo(string Comment, string Marker, bool CreateFile);

    private sealed record FileResult(FileChangeKind Kind, ManifestEntry? Entry, bool KeepPrevious, UnitOutput? Output);
}
