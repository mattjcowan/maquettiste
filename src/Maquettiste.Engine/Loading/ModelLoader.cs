using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Loading;

/// <summary>What one load did, for tests and the benchmark.</summary>
/// <param name="Files">Files that make up the loaded model (settings, elements, extensions, rule scripts, sidecars).</param>
/// <param name="ReadFromDisk">Files whose bytes were read from disk.</param>
/// <param name="FromCache">Files whose bytes came from the index cache (length and last-write time matched).</param>
/// <param name="Reused">Files taken from the previous load without reading (stat or content unchanged).</param>
/// <param name="Parsed">Files parsed and deserialized.</param>
/// <param name="SchemaEvaluations">Files evaluated against their JSON Schema (a trusted cache record skips it).</param>
/// <param name="CacheWritten">Whether the index cache was rewritten.</param>
internal sealed record LoadStatistics(int Files, int ReadFromDisk, int FromCache, int Reused, int Parsed, int SchemaEvaluations, bool CacheWritten);

/// <summary>One file's stat as a load took it: its length and last-write time (UTC ticks) before its bytes were read.</summary>
/// <param name="Path">The model-relative path (or, outside the loader, a path relative to the folder it was listed under).</param>
/// <param name="Length">The length.</param>
/// <param name="LastWriteTicks">The last-write time, UTC ticks.</param>
internal readonly record struct FileStamp(string Path, long Length, long LastWriteTicks);

/// <summary>
/// The files a completed load read, with the stat each was read at (the generation run's last-run record, Generation/README.md):
/// the enumerated model files, the description sidecars they reference and the referenced sidecars that did not exist.
/// </summary>
/// <param name="Snapshot">The snapshot the load produced.</param>
/// <param name="Primary">Settings, element, extension and rule-script files, ordinal by path.</param>
/// <param name="Sidecars">Referenced sidecars that exist, ordinal by path.</param>
/// <param name="MissingSidecars">Referenced sidecars that do not exist, ordinal.</param>
internal sealed record LoadedFileStamps(ModelSnapshot Snapshot, IReadOnlyList<FileStamp> Primary, IReadOnlyList<FileStamp> Sidecars,
    IReadOnlyList<string> MissingSidecars);

/// <summary>Stage 1: reads model files, the index cache and extensions into a snapshot (W1; engine-design.md section 5).</summary>
/// <remarks>
/// The loader keeps the files of its last load (bytes, stat, parse result), so a rescan re-reads only files whose length or
/// last-write time changed, and a changed file whose content hash is unchanged is not parsed again. On the first load it reads
/// <c>CacheDirectory/index.v1.bin</c>: a file whose stat matches its record is taken from the cache without reading it through the
/// (possibly slow) model mount, and a record that passed schema validation under the same engine version and schema set skips
/// schema evaluation. A full load that changed anything rewrites the cache. Loads are serialized; the loader holds no static state.
/// </remarks>
/// <param name="options">The engine options.</param>
/// <param name="schemas">The schema registry.</param>
/// <param name="json">The canonical writer (MQ1003 checks).</param>
/// <param name="paths">The engine-write guard; the index cache is written as a <see cref="WriteTarget.Cache"/> target.</param>
internal sealed class ModelLoader(EngineOptions options, ISchemaRegistry schemas, ICanonicalJson json, IOutputPathPolicy paths) : IModelLoader
{
    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchType = MatchType.Simple };
    private static readonly EnumerationOptions TopOnly = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, MatchType = MatchType.Simple };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ModelPaths _paths = new(options);
    private readonly DocumentReader _reader = new(schemas, json);
    private readonly Lazy<string> _schemaSetHash = new(() => IndexCache.SchemaSetHash(schemas));
    private LoaderState _state = LoaderState.Empty;
    private IReadOnlyDictionary<string, CacheRecord>? _diskCache;
    private int _tempCounter;

    /// <summary>The statistics of the last completed load.</summary>
    internal LoadStatistics LastStatistics { get; private set; } = new(0, 0, 0, 0, 0, 0, false);

    /// <summary>The model path conventions of this loader.</summary>
    internal ModelPaths Paths => _paths;

    /// <summary>The index cache file.</summary>
    internal string CachePath => Path.Combine(options.CacheDirectory, IndexCache.FileName);

    /// <inheritdoc/>
    public async Task<LoadResult> LoadAsync(LoadRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(request, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LoadResult> LoadCoreAsync(LoadRequest request, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var previous = _state;
        var full = request.ChangedPaths is null || previous.Snapshot is null;
        _diskCache ??= await IndexCache.ReadAsync(CachePath, _schemaSetHash.Value, ct, options.EffectiveParallelism).ConfigureAwait(false);
        var counters = new Counters();

        // 1. Which files to look at: every file on a full scan; the reported paths (folders expanded) otherwise.
        Dictionary<string, FileEntry> entries;
        HashSet<string> toCheck;
        var hints = new HashSet<string>(StringComparer.Ordinal);
        if (full)
        {
            entries = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
            toCheck = Enumerate();
        }
        else
        {
            entries = new Dictionary<string, FileEntry>(previous.Entries, StringComparer.Ordinal);
            toCheck = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in request.ChangedPaths!)
            {
                if (_paths.ToModelPath(raw) is not { } path)
                    continue;
                hints.Add(path);
                if (path.Length == 0 || Directory.Exists(_paths.FullPath(path)))
                    toCheck.UnionWith(EnumerateUnder(path));
                else if (ModelPaths.Classify(path) is not null)
                    toCheck.Add(path);
                var prefix = path.Length == 0 ? "" : path + "/";
                foreach (var known in previous.Entries.Keys)
                {
                    if (!known.StartsWith(prefix, StringComparison.Ordinal))
                        continue;
                    hints.Add(known);
                    if (ModelPaths.Classify(known) is not null)
                        toCheck.Add(known);
                }
            }
        }

        // 2. Stat, read or reuse each file, in parallel.
        var primary = toCheck.Where(p => ModelPaths.Classify(p) is not null).Order(StringComparer.Ordinal).ToArray();
        var checkedEntries = await CheckAllAsync(primary, previous, request.VerifyHashes, counters, progress, ct).ConfigureAwait(false);
        for (var i = 0; i < primary.Length; i++)
        {
            if (checkedEntries[i] is { } entry)
                entries[primary[i]] = entry;
            else
                entries.Remove(primary[i]);
        }

        // 3. Sidecars referenced by element files.
        var needed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.Values)
        {
            if (entry.Kind is not (ModelFileKind.Element or ModelFileKind.LocaleShard) || !entry.Parsed.Valid)
                continue;
            var isShard = entry.Parsed.LocaleShard is not null;
            foreach (var (_, file) in entry.Parsed.SidecarReferences)
            {
                var resolved = isShard ? LocaleShardDocument.ResolveSidecar(entry.ModelPath, file) : ModelPaths.ResolveSidecar(entry.ModelPath, file);
                if (resolved is { } sidecar && ModelPaths.Classify(sidecar) is null)
                    needed.Add(sidecar);
            }
        }

        foreach (var stale in entries.Where(kv => kv.Value.Kind == ModelFileKind.Sidecar && !needed.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            entries.Remove(stale);
        var sidecars = needed.Where(p => full || request.VerifyHashes || hints.Contains(p) || !entries.ContainsKey(p)).ToArray();
        var sidecarEntries = await CheckAllAsync(sidecars, previous, request.VerifyHashes, counters, null, ct).ConfigureAwait(false);
        for (var i = 0; i < sidecars.Length; i++)
        {
            if (sidecarEntries[i] is { } entry)
                entries[sidecars[i]] = entry with { Kind = ModelFileKind.Sidecar };
            else
                entries.Remove(sidecars[i]);
        }

        var missingSidecars = needed.Where(p => !entries.ContainsKey(p)).ToArray();

        // 4. A partial load that brings an id also held by a file it did not look at re-checks that file: a rename reported only
        //    by its new path must not leave the old path behind as a duplicate.
        if (!full)
        {
            var looked = new HashSet<string>(primary, StringComparer.Ordinal);
            var recheck = entries.Values
                .Where(e => e.Kind == ModelFileKind.Element && e.Parsed.Element is not null)
                .GroupBy(e => e.Parsed.Element!.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1 && g.Any(e => looked.Contains(e.ModelPath)))
                .SelectMany(g => g.Where(e => !looked.Contains(e.ModelPath)).Select(e => e.ModelPath))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var rechecked = await CheckAllAsync(recheck, previous, true, counters, null, ct).ConfigureAwait(false);
            for (var i = 0; i < recheck.Length; i++)
            {
                if (rechecked[i] is { } entry)
                    entries[recheck[i]] = entry;
                else
                    entries.Remove(recheck[i]);
            }
        }

        ct.ThrowIfCancellationRequested();

        // 5. Build the snapshot, unless nothing changed since the snapshot the caller holds.
        var contentChanged = entries.Count != previous.Entries.Count
            || entries.Any(kv => !previous.Entries.TryGetValue(kv.Key, out var old) || old.Hash != kv.Value.Hash || old.Kind != kv.Value.Kind);

        // The index cache needs only the files: a full load writes it (when anything changed) beside the snapshot assembly and
        // awaits it before returning, so the load still ends with the cache on disk.
        var cacheTask = full ? WriteCacheIfChangedAsync(entries, ct) : Task.FromResult(false);
        ModelSnapshot snapshot;
        IReadOnlyDictionary<string, ElementDocument> documents;
        ChangeSet changes;
        try
        {
            if (!contentChanged && previous.Snapshot is not null && ReferenceEquals(request.Previous, previous.Snapshot))
            {
                snapshot = previous.Snapshot;
                documents = previous.Documents;
            }
            else
            {
                var version = (request.Previous?.Version ?? previous.Snapshot?.Version ?? 0) + 1;
                (snapshot, documents) = Assemble(entries, previous.Documents, version, previous.Snapshot, ct);
            }

            changes = Compare(request.Previous, snapshot);
        }
        finally
        {
            if (!cacheTask.IsCompleted)
                await ((Task)cacheTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // never left running behind a failed load
        }

        var cacheWritten = await cacheTask.ConfigureAwait(false);
        _state = new LoaderState(entries, documents, snapshot) { MissingSidecars = missingSidecars };

        LastStatistics = new LoadStatistics(entries.Count, counters.Read, counters.FromCache, counters.Reused, counters.Parsed, counters.SchemaEvaluations, cacheWritten);
        return new LoadResult(snapshot, changes);
    }

    /// <summary>
    /// The files of the last completed load with the stat each was read at, or <see langword="null"/> before the first load. Computed
    /// on request (nothing is kept for it), so a long-lived host that never asks pays nothing.
    /// </summary>
    /// <returns>The stamps.</returns>
    internal LoadedFileStamps? LastFileStamps()
    {
        var state = _state;
        if (state.Snapshot is null)
            return null;
        var primary = new List<FileStamp>();
        var sidecars = new List<FileStamp>();
        foreach (var entry in state.Entries.Values)
            (entry.Kind == ModelFileKind.Sidecar ? sidecars : primary).Add(new FileStamp(entry.ModelPath, entry.Length, entry.LastWriteTicks));
        primary.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        sidecars.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new LoadedFileStamps(state.Snapshot, primary, sidecars, [.. state.MissingSidecars.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The model files a full load would look at (settings, <c>model/**</c>, extensions, rule scripts), model-relative.</summary>
    /// <returns>The paths.</returns>
    internal HashSet<string> EnumerateModelFiles() => Enumerate();

    private async Task<FileEntry?[]> CheckAllAsync(
        string[] modelPaths,
        LoaderState previous,
        bool verify,
        Counters counters,
        IProgress<ProgressUpdate>? progress,
        CancellationToken ct)
    {
        var results = new FileEntry?[modelPaths.Length];
        if (modelPaths.Length == 0)
            return results;
        var done = 0;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.EffectiveParallelism, CancellationToken = ct };
        // Locale shards first: they are the largest files, and started last they would form the tail of the parallel read.
        var order = Enumerable.Range(0, modelPaths.Length).OrderBy(i => ModelPaths.Classify(modelPaths[i]) == ModelFileKind.LocaleShard ? 0 : 1).ToArray();
        await Parallel.ForEachAsync(order, parallel, async (i, token) =>
        {
            var path = modelPaths[i];
            previous.Entries.TryGetValue(path, out var old);
            var kind = ModelPaths.Classify(path) ?? ModelFileKind.Sidecar;
            results[i] = await CheckAsync(path, kind, old, verify, counters, token).ConfigureAwait(false);
            progress?.Report(new ProgressUpdate(PipelineStage.Load, Interlocked.Increment(ref done), modelPaths.Length, _paths.ToRepoPath(path), null));
        }).ConfigureAwait(false);
        return results;
    }

    private async Task<FileEntry?> CheckAsync(string modelPath, ModelFileKind kind, FileEntry? old, bool verify, Counters counters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fullPath = _paths.FullPath(modelPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return null;
        var length = info.Length;
        var ticks = info.LastWriteTimeUtc.Ticks;
        if (old is not null && old.Kind == kind && !verify && old.Length == length && old.LastWriteTicks == ticks)
        {
            Interlocked.Increment(ref counters.Reused);
            return old;
        }

        var repoPath = _paths.ToRepoPath(modelPath);
        CacheRecord? record = null;
        _diskCache?.TryGetValue(modelPath, out record);
        byte[] bytes;
        string hash;
        if (!verify && record is not null && record.Length == length && record.LastWriteTicks == ticks)
        {
            bytes = record.Bytes;
            hash = record.Hash;
            Interlocked.Increment(ref counters.FromCache);
        }
        else
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var unreadable = new ParsedFile { Diagnostics = [RuleCatalog.Create("MQ1001", "The file cannot be read: " + ex.Message, null, repoPath, null)] };
                return new FileEntry(modelPath, kind, length, ticks, "", [], unreadable);
            }

            hash = ContentHash.Of(bytes);
            Interlocked.Increment(ref counters.Read);
        }

        if (old is not null && old.Kind == kind && old.Hash == hash)
        {
            Interlocked.Increment(ref counters.Reused);
            return old with { Length = length, LastWriteTicks = ticks };
        }

        var trusted = record is not null && record.Hash == hash && record.Flags.HasFlag(CacheRecordFlags.Validated);
        var trustedCanonical = trusted && record!.Flags.HasFlag(CacheRecordFlags.Canonical);
        ParsedFile parsed;
        try
        {
            parsed = kind switch
            {
                // A validated shard whose parsed form is in the shard cache skips its JSON (reference-types-seeds-localization.md §5).
                ModelFileKind.LocaleShard when trusted && ShardCache.TryRead(ShardCache.PathOf(options.CacheDirectory, hash)) is { } cached
                    => DocumentReader.TrustedShard(cached, repoPath, trustedCanonical),
                // Files dispatch on their kind: a shard outside model/locales/<locale>/ loads as a shard, with MQ1005 (section 3.3).
                ModelFileKind.Element when LocaleShardReader.IsShard(bytes) => MisplacedShard(_reader.ReadLocaleShard(bytes, repoPath, trusted, trustedCanonical), repoPath),
                ModelFileKind.Element => _reader.ReadElement(bytes, repoPath, trusted, trustedCanonical),
                ModelFileKind.Settings => _reader.ReadSettings(bytes, repoPath, trusted, trustedCanonical),
                ModelFileKind.Extension => _reader.ReadExtension(bytes, repoPath, trusted),
                ModelFileKind.LocaleShard => _reader.ReadLocaleShard(bytes, repoPath, trusted, trustedCanonical),
                _ => DocumentReader.ReadText(bytes),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            // Backstop: content the reader did not anticipate leaves this one file out instead of failing the whole load (design 5).
            var rule = kind == ModelFileKind.Extension ? "MQ5004" : "MQ1001";
            parsed = new ParsedFile { Diagnostics = [RuleCatalog.Create(rule, "The file cannot be read: " + ex.Message, null, repoPath, null)] };
        }

        Interlocked.Increment(ref counters.Parsed);
        if (parsed.SchemaEvaluated)
            Interlocked.Increment(ref counters.SchemaEvaluations);

        // An element without sidecar references has a dependency hash that depends on its bytes alone: computed here, on the
        // parallel readers, rather than in the sequential assembly.
        var ownDependencyHash = kind == ModelFileKind.Element && parsed.SidecarReferences.IsEmpty ? HashBuilder.Of(hash, null) : null;
        return new FileEntry(modelPath, kind, length, ticks, hash, bytes, parsed) { OwnDependencyHash = ownDependencyHash };
    }

    private (ModelSnapshot Snapshot, IReadOnlyDictionary<string, ElementDocument> Documents) Assemble(
        Dictionary<string, FileEntry> entries,
        IReadOnlyDictionary<string, ElementDocument> previousDocuments,
        long version,
        ModelSnapshot? previousSnapshot,
        CancellationToken ct)
    {
        var diagnostics = new List<Diagnostic>();

        var settings = new ProjectSettings { FormatVersion = EngineVersion.FormatVersion };
        var settingsHash = ContentHash.Of(ReadOnlySpan<byte>.Empty);
        if (entries.TryGetValue(ModelPaths.SettingsFile, out var settingsEntry))
        {
            diagnostics.AddRange(settingsEntry.Parsed.Diagnostics);
            settingsHash = settingsEntry.Hash;
            if (settingsEntry.Parsed.Settings is { } loaded)
                settings = loaded;
        }

        var documents = new List<ElementDocument>();
        var byPath = new Dictionary<string, ElementDocument>(StringComparer.Ordinal);
        var ids = new Dictionary<string, (string Path, string Pointer)>(StringComparer.Ordinal);
        foreach (var entry in entries.Values.Where(e => e.Kind == ModelFileKind.Element).OrderBy(e => e.ModelPath, StringComparer.Ordinal))
        {
            diagnostics.AddRange(entry.Parsed.Diagnostics);
            if (entry.Parsed.Element is not { } element || !entry.Parsed.Valid)
                continue;
            var repoPath = _paths.ToRepoPath(entry.ModelPath);
            if (ids.TryGetValue(element.Id, out var first))
            {
                var duplicate = RuleCatalog.Create(
                    "MQ1004",
                    $"The id {element.Id} is already used by {first.Path}{first.Pointer}; this file is not loaded.",
                    element.Id,
                    repoPath,
                    "/id");
                diagnostics.Add(_reader.Locate(duplicate, entry.Bytes));
                continue;
            }

            foreach (var (id, pointer) in entry.Parsed.Ids)
            {
                if (ids.TryAdd(id, (repoPath, pointer)))
                    continue;
                if (pointer.Length == 0)
                    continue; // the element's own id, added above
                var other = ids[id];
                var d = RuleCatalog.Create("MQ1004", $"{pointer}/id {id} is already used by {other.Path}{other.Pointer}.", id, repoPath, pointer + "/id");
                diagnostics.Add(_reader.Locate(d, entry.Bytes));
            }

            var document = BuildDocument(entry, repoPath, entries, previousDocuments);
            documents.Add(document);
            byPath[repoPath] = document;
        }

        diagnostics.AddRange(ConventionDiagnostics(documents));

        var extensions = new List<ExtensionDocument>();
        var scripts = new List<ScriptSource>();
        var shards = new List<LocaleShardDocument>();
        foreach (var entry in entries.Values.OrderBy(e => e.ModelPath, StringComparer.Ordinal))
        {
            switch (entry.Kind)
            {
                case ModelFileKind.Extension:
                    diagnostics.AddRange(entry.Parsed.Diagnostics);
                    if (entry.Parsed.Extension is { } extension)
                        extensions.Add(new ExtensionDocument(extension, _paths.ToRepoPath(entry.ModelPath), entry.Hash));
                    break;
                case ModelFileKind.LocaleShard:
                    diagnostics.AddRange(entry.Parsed.Diagnostics);
                    if (entry.Parsed.LocaleShard is not null && entry.Parsed.Valid)
                        shards.Add(ShardDocument(entry, entries));
                    break;
                case ModelFileKind.Element when entry.Parsed.LocaleShard is not null:
                    // A shard outside the locales folder: its diagnostics were added with the element files'.
                    if (entry.Parsed.Valid)
                        shards.Add(ShardDocument(entry, entries));
                    break;
                case ModelFileKind.RuleScript:
                    diagnostics.AddRange(entry.Parsed.Diagnostics);
                    if (entry.Parsed.Text is { } code)
                        scripts.Add(new ScriptSource(_paths.ToRepoPath(entry.ModelPath), code, entry.Hash));
                    break;
            }
        }

        diagnostics.Sort(Diagnostic.Order);
        var snapshot = ModelSnapshot.CreateAfter(previousSnapshot, documents, settings, settingsHash, extensions, scripts, version, diagnostics,
            options.EffectiveParallelism, ct, shards);
        return (snapshot, byPath);
    }

    private static ParsedFile MisplacedShard(ParsedFile parsed, string repoPath) => parsed with
    {
        Diagnostics = [.. parsed.Diagnostics.Append(RuleCatalog.Create("MQ1005",
            "This locale shard is outside model/locales/<locale>/; it loads, but belongs in its locale's folder.", null, repoPath, "/kind")).Order(Diagnostic.Order)],
    };

    private LocaleShardDocument ShardDocument(FileEntry entry, Dictionary<string, FileEntry> entries)
    {
        var sidecars = new SortedDictionary<string, LocaleSidecar>(StringComparer.Ordinal);
        using var hash = new HashBuilder();
        hash.Add(entry.Hash);
        foreach (var (_, file) in entry.Parsed.SidecarReferences)
        {
            if (sidecars.ContainsKey(file))
                continue;
            var path = LocaleShardDocument.ResolveSidecar(entry.ModelPath, file) ?? file;
            var sidecar = entries.TryGetValue(path, out var found) && found.Kind == ModelFileKind.Sidecar
                ? new LocaleSidecar(path, found.Parsed.Text, found.Hash)
                : new LocaleSidecar(path, null, "absent");
            sidecars[file] = sidecar;
        }

        foreach (var (file, sidecar) in sidecars)
            hash.Add(file).Add(sidecar.Hash);
        return new LocaleShardDocument(entry.Parsed.LocaleShard!, _paths.ToRepoPath(entry.ModelPath), entry.ModelPath, entry.Hash,
            sidecars.Count == 0 ? entry.Hash : hash.Finish(), sidecars);
    }

    private static ElementDocument BuildDocument(FileEntry entry, string repoPath, Dictionary<string, FileEntry> entries, IReadOnlyDictionary<string, ElementDocument> previousDocuments)
    {
        // Same bytes and no sidecar: the dependency hash is a function of the bytes alone, so the previous document stands as it is.
        if (entry.Parsed.SidecarReferences.IsEmpty && previousDocuments.TryGetValue(repoPath, out var unchanged) && unchanged.Hash == entry.Hash)
            return unchanged;

        string? sidecarHash = null;
        string? sidecarText = null;
        if (!entry.Parsed.SidecarReferences.IsEmpty)
        {
            using var hash = new HashBuilder();
            var resolved = entry.Parsed.SidecarReferences
                .Select(r => (r.Pointer, Path: ModelPaths.ResolveSidecar(entry.ModelPath, r.File) ?? r.File))
                .ToList();
            foreach (var path in resolved.Select(r => r.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                hash.Add(path).Add(entries.TryGetValue(path, out var sidecar) && sidecar.Kind == ModelFileKind.Sidecar ? sidecar.Hash : "absent");
            sidecarHash = hash.Finish();
            foreach (var (pointer, path) in resolved)
            {
                if (pointer == "/description" && entries.TryGetValue(path, out var own) && own.Kind == ModelFileKind.Sidecar)
                    sidecarText = own.Parsed.Text;
            }
        }

        var dependencyHash = sidecarHash is null && entry.OwnDependencyHash is { } bytesOnly ? bytesOnly : HashBuilder.Of(entry.Hash, sidecarHash);
        if (previousDocuments.TryGetValue(repoPath, out var previous) && previous.Hash == entry.Hash && previous.DependencyHash == dependencyHash)
            return previous;
        return new ElementDocument(entry.Parsed.Element!, repoPath, entry.Hash, dependencyHash, entry.Parsed.Json, sidecarText);
    }

    private IEnumerable<Diagnostic> ConventionDiagnostics(List<ElementDocument> documents)
    {
        var databaseFolders = documents
            .Where(d => d.Element is Database)
            .GroupBy(d => d.Element.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => ModelPaths.FolderOf(_paths.FromRepoPath(g.First().Path)), StringComparer.Ordinal);
        var stems = documents.Any(d => d.Element is Seed or Scenario)
            ? documents.GroupBy(d => d.Element.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => ModelPaths.Stem(g.First().Element), StringComparer.Ordinal)
            : [];
        foreach (var document in documents)
        {
            var modelPath = _paths.FromRepoPath(document.Path);
            var element = document.Element;
            var folder = ModelPaths.ConventionalFolder(element, id => databaseFolders.GetValueOrDefault(id), id => stems.GetValueOrDefault(id));
            if (ModelPaths.MatchesConvention(element, modelPath, folder))
                continue;
            var expected = element is Database ? folder + "/database.json" : ModelPaths.Join(folder, ModelPaths.FileName(element, false));
            yield return RuleCatalog.Create(
                "MQ1005",
                $"A {element.KindName} named '{element.Name}' belongs at {_paths.ToRepoPath(expected)}; it loads from here.",
                element.Id,
                document.Path,
                "");
        }
    }

    private static ChangeSet Compare(ModelSnapshot? before, ModelSnapshot after)
    {
        if (ReferenceEquals(before, after))
            return ChangeSet.Empty(ChangeSource.Disk);
        // A snapshot's documents have distinct ids, so the previous document of an id is the snapshot's element document for it.
        var changed = new List<ElementChange>();
        foreach (var document in after.Documents)
        {
            var id = document.Element.Id;
            if (before?.GetDocument(id) is { } previous && ReferenceEquals(previous.Element, before.Get<Element>(id))
                && previous.Hash == document.Hash && previous.DependencyHash == document.DependencyHash && previous.Path == document.Path)
                continue;
            changed.Add(new ElementChange(id, document.Element.KindName, document.Path, document.Hash));
        }

        var deleted = (before?.Documents ?? []).Select(d => d.Element.Id).Where(id => after.Get<Element>(id) is null).Order(StringComparer.Ordinal).ToArray();
        return new ChangeSet(changed, deleted, ChangeSource.Disk, false) { Locales = before is null ? [] : ChangedLocales(before, after) };
    }

    private static string[] ChangedLocales(ModelSnapshot before, ModelSnapshot after)
    {
        if (ReferenceEquals(before.LocaleShards, after.LocaleShards))
            return [];
        var old = before.LocaleShards.GroupBy(s => s.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().DependencyHash, StringComparer.Ordinal);
        var locales = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var shard in after.LocaleShards)
        {
            if (!old.Remove(shard.Path, out var hash) || hash != shard.DependencyHash)
                locales.Add(shard.FolderLocale);
        }

        foreach (var gone in before.LocaleShards.Where(s => old.ContainsKey(s.Path)))
            locales.Add(gone.FolderLocale);
        return [.. locales];
    }

    private async Task<bool> WriteCacheIfChangedAsync(Dictionary<string, FileEntry> entries, CancellationToken ct)
    {
        await Task.Yield(); // runs beside the snapshot assembly
        WriteShardCache(entries, ct);
        var records = entries.Values
            .Where(e => e.Hash.Length > 0)
            .Select(e => new CacheRecord(e.ModelPath, e.Length, e.LastWriteTicks, e.Hash, e.Flags, e.Bytes))
            .ToList();
        var current = _diskCache ?? FrozenDictionary<string, CacheRecord>.Empty;
        if (records.Count == current.Count && records.All(r => current.TryGetValue(r.Path, out var c) && c.SameAs(r)))
            return false;

        var suffix = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _tempCounter).ToString(CultureInfo.InvariantCulture);
        if (!await IndexCache.WriteAsync(CachePath, suffix, _schemaSetHash.Value, records, paths, ct).ConfigureAwait(false))
            return false;
        _diskCache = records.ToDictionary(r => r.Path, StringComparer.Ordinal);
        return true;
    }

    /// <summary>Fills the parsed-shard cache with every valid shard loaded now, in parallel, and removes the files of shards gone.</summary>
    private void WriteShardCache(Dictionary<string, FileEntry> entries, CancellationToken ct)
    {
        var shards = entries.Values.Where(e => e.Kind == ModelFileKind.LocaleShard && e.Hash.Length > 0 && e.Parsed is { Valid: true, LocaleShard: not null }).ToList();
        var live = shards.Select(e => e.Hash).ToHashSet(StringComparer.Ordinal);
        Parallel.ForEach(shards, new ParallelOptions { MaxDegreeOfParallelism = options.EffectiveParallelism, CancellationToken = ct },
            e => ShardCache.TryWrite(options.CacheDirectory, e.Hash, e.Parsed.LocaleShard!, paths));
        ShardCache.Prune(options.CacheDirectory, live, paths);
    }

    private HashSet<string> Enumerate()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(_paths.FullPath(ModelPaths.SettingsFile)))
            result.Add(ModelPaths.SettingsFile);
        result.UnionWith(EnumerateUnder("model"));
        result.UnionWith(EnumerateFiles("extensions", "*.json", TopOnly));
        result.UnionWith(EnumerateFiles("extensions/rules", "*.js", TopOnly));
        return result;
    }

    private IEnumerable<string> EnumerateUnder(string modelPath)
    {
        if (modelPath.Length == 0)
            return Enumerate();
        return EnumerateFiles(modelPath, "*", Recursive).Where(p => ModelPaths.Classify(p) is not null);
    }

    private IEnumerable<string> EnumerateFiles(string modelFolder, string pattern, EnumerationOptions enumeration)
    {
        var folder = _paths.FullPath(modelFolder);
        if (!Directory.Exists(folder))
            return [];
        try
        {
            return Directory.EnumerateFiles(folder, pattern, enumeration)
                .Select(p => _paths.ToModelPath(p))
                .Where(p => p is not null && ModelPaths.Classify(p) is not null)
                .Select(p => p!)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // a folder that vanished mid-scan
        }
    }

    /// <summary>One file of the last load.</summary>
    private sealed record FileEntry(string ModelPath, ModelFileKind Kind, long Length, long LastWriteTicks, string Hash, byte[] Bytes, ParsedFile Parsed)
    {
        /// <summary><c>H(Hash, null)</c>, the dependency hash of an element without sidecar references (computed by the reader), else null.</summary>
        public string? OwnDependencyHash { get; init; }

        public CacheRecordFlags Flags =>
            (Parsed.Valid ? CacheRecordFlags.Validated : CacheRecordFlags.None) | (Parsed.Canonical ? CacheRecordFlags.Canonical : CacheRecordFlags.None);
    }

    private sealed record LoaderState(
        IReadOnlyDictionary<string, FileEntry> Entries,
        IReadOnlyDictionary<string, ElementDocument> Documents,
        ModelSnapshot? Snapshot)
    {
        /// <summary>Sidecars the elements reference that did not exist at the load.</summary>
        public IReadOnlyList<string> MissingSidecars { get; init; } = [];

        public static LoaderState Empty { get; } = new(
            FrozenDictionary<string, FileEntry>.Empty,
            FrozenDictionary<string, ElementDocument>.Empty,
            null);
    }

    private sealed class Counters
    {
        public int Read;
        public int FromCache;
        public int Reused;
        public int Parsed;
        public int SchemaEvaluations;
    }
}
