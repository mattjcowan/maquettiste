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
/// (path policy, root selection, plan list, duplicate and case-colliding paths) and queues it; <c>min(jobs, 8)</c> tasks drain a
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
    private const string RegionsOnBuiltRule = "MQ6015";

    private readonly IOutputPathPolicy _paths;
    private readonly IManifestStore _manifests;
    private readonly IDiffGenerator _diffs;
    private readonly WriteContext _context;
    private readonly IProgress<ProgressUpdate>? _progress;
    private readonly int _workerCount;
    private readonly string _repoRoot;
    private readonly bool _apply;
    private readonly RootSelection _roots;
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
        _roots = context.Mode == GenerationMode.Check ? RootSelection.Committed : context.Roots;
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
        if (!RootSelected(root.Commit))
            return null;
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
        if (file.Mode == OutputMode.Regions && !root.Commit)
        {
            if (!unit.Diagnostics.Any(d => d.Rule == RegionsOnBuiltRule && d.FilePath == path))
                Report(RegionsOnBuiltRule, $"Regions mode on a built root: {path} is under '{root.Path}', which is not committed.", path);
            return null;
        }

        var owned = file.Mode == OutputMode.Once || file.Role == FileRole.Companion || ManifestHashes.IsOwned(file.ManifestHash);
        var manifestHash = owned && !ManifestHashes.IsOwned(file.ManifestHash) ? ManifestHashes.OwnedPrefix + file.ContentHash : file.ManifestHash;
        var unitName = UnitName(record.Unit.Rendered.Unit.Key, pack.Name) + (file.Role == FileRole.Companion ? "#companion" : "");
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
        else if (old is not null && string.Equals(ManifestHashes.Comparable(old.Hash, disk, diskHash!), old.Hash, StringComparison.Ordinal))
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

        if (kind != FileChangeKind.Unchanged)
        {
            string? diff = null;
            if (_context.IncludeDiffs && !job.File.ContentOmitted && kind is not FileChangeKind.Kept)
                diff = _diffs.Unified(job.Path, disk ?? [], content.Span);
            _changes.Enqueue(new FileChange(job.Path, kind, job.Pack, job.UnitKey, old?.Hash ?? diskHash, next?.Hash ?? job.ManifestHash, diff));
        }

        var done = Interlocked.Increment(ref _done);
        _progress?.Report(new ProgressUpdate(PipelineStage.Write, done, Volatile.Read(ref _queued), job.Path, job.Pack));
        return new FileResult(kind, next, keepPrevious, output);
    }

    private async Task ClosePackAsync(PackRun pack, CancellationToken ct)
    {
        pack.Closed = true;
        await Task.WhenAll(pack.Jobs.Select(j => j.Completion.Task)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var results = new Dictionary<string, FileResult>(StringComparer.Ordinal);
        // Keyed by path; the manifest store sorts the entries it writes, so no ordered map is needed while collecting them.
        var next = new Dictionary<string, (ManifestEntry Entry, ManifestBucket Bucket)>(StringComparer.Ordinal);
        // Paths whose old entry a job already carries (a conflict on a file renamed only by case keeps the old spelling's entry).
        var carried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in pack.Jobs)
        {
            var result = job.Completion.Task.Result;
            results[job.Path] = result;
            if (result.Entry is not null)
            {
                next[job.Path] = (result.Entry, job.Root.Commit ? ManifestBucket.Committed : ManifestBucket.Built);
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

        var policy = _paths as OutputPathPolicy;
        foreach (var (path, (entry, bucket)) in _context.Manifests.Data.Pack(pack.Name).Entries)
        {
            if (results.ContainsKey(path) || carried.Contains(path))
                continue;

            // Fast path for the entries this run keeps (skipped units: nearly all of them in an incremental run). When the lexical
            // rules put the path under a root of the entry's own bucket, the effective bucket below is that bucket whatever the
            // symbolic link check says (allowed: the root's kind; refused: the stored bucket), so the check's disk reads can wait
            // for the entries that may become orphans.
            if (bucket != ManifestBucket.Journal && policy?.LexicalRoot(path) is { } lexicalRoot
                && lexicalRoot.Commit == (bucket == ManifestBucket.Committed))
            {
                if (!RootSelected(lexicalRoot.Commit) || keepUnits.Contains(StripCompanion(entry.Unit)))
                {
                    next[path] = (entry, bucket);
                    continue;
                }

                if (_produced.Contains(path))
                    continue; // another pack produces it in this run
            }

            var check = _paths.Check(path);
            var effective = check.Allowed && check.Root is not null
                ? (check.Root.Commit ? ManifestBucket.Committed : ManifestBucket.Built)
                : bucket;
            if (effective == ManifestBucket.Journal)
            {
                Report(RefusedRule, $"Output path refused: {path} (from an unfinished run's journal): {check.Reason}", path);
                continue;
            }

            if (!RootSelected(effective == ManifestBucket.Committed) || keepUnits.Contains(StripCompanion(entry.Unit)))
            {
                next[path] = (entry, effective);
                continue;
            }

            if (_produced.Contains(path))
                continue; // another pack produces it in this run

            if (!check.Allowed || check.Root is null)
            {
                Report(RefusedRule, $"Output path refused: orphan {path} is not deleted: {check.Reason}", path);
                next[path] = (entry, effective);
                continue;
            }

            if (_context.PlannedPaths is { } planned && !planned.Contains(path))
            {
                Report(RefusedRule, $"Output path refused: orphan {path} is not in the plan being applied.", path);
                next[path] = (entry, effective);
                continue;
            }

            // Deferred to the end of the stream, since a later pack may still produce the path: the entry stays for now.
            next[path] = (entry, effective);
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
        var next = pack.Next!;
        await _manifests.SavePackAsync(pack.Name, true, Bucket(next, ManifestBucket.Committed), ct).ConfigureAwait(false);
        await _manifests.SavePackAsync(pack.Name, false, Bucket(next, ManifestBucket.Built), ct).ConfigureAwait(false);
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

        var diskHash = ContentHash.Of(disk);
        var intact = string.Equals(ManifestHashes.Comparable(entry.Hash, disk, diskHash), entry.Hash, StringComparison.Ordinal);
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

    private static IReadOnlyList<ManifestEntry> Bucket(Dictionary<string, (ManifestEntry Entry, ManifestBucket Bucket)> entries, ManifestBucket bucket)
    {
        var list = new List<ManifestEntry>();
        foreach (var (entry, entryBucket) in entries.Values)
        {
            if (entryBucket == bucket)
                list.Add(entry);
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    private bool RootSelected(bool committed) => _roots switch
    {
        RootSelection.Committed => committed,
        RootSelection.Built => !committed,
        _ => true,
    };

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

        public Dictionary<string, (ManifestEntry Entry, ManifestBucket Bucket)>? Next { get; set; }
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
    }

    private sealed record FileResult(FileChangeKind Kind, ManifestEntry? Entry, bool KeepPrevious, UnitOutput? Output);
}
