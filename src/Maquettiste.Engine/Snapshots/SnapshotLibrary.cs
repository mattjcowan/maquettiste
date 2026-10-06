using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Snapshots;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine;

/// <summary>
/// The model's snapshots (docs/engineering/snapshots.md): named, immutable copies of the whole model as deterministic zip archives
/// under <c>.maquettiste/model-snapshots/</c>. Create, list, rename and publish, delete, open read-only ("as of"), compare (with each
/// other or with the working model), restore (after an automatic safety snapshot), export and import. Every operation is an explicit
/// call: nothing here runs in the background. Thread-safe; writes are serialized.
/// </summary>
public sealed partial class SnapshotLibrary
{
    /// <summary>The name of the working model in a comparison.</summary>
    public const string Working = "working";

    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest description.</summary>
    public const int MaxDescriptionLength = 4000;

    /// <summary>How many documents are read at once while an archive is written.</summary>
    private const int Chunk = 512;

    /// <summary>How many snapshots stay open read-only at once (each holds a whole model in memory).</summary>
    private const int MaxOpen = 2;

    private readonly ModelStore _store;
    private readonly EngineOptions _options;
    private readonly ModelPaths _paths;
    private readonly IOutputPathPolicy _policy;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Lock _cache = new();
    private readonly Dictionary<string, (long Length, long Ticks, SnapshotInfo Info)> _infos = new(StringComparer.Ordinal);
    private readonly List<(string Key, Lazy<Task<OpenSnapshot>> Open)> _open = [];

    /// <summary>Creates the library of a store's model. Performs no I/O.</summary>
    /// <param name="store">The working model's store (not a snapshot opened read-only).</param>
    public SnapshotLibrary(ModelStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.IsReadOnly)
            throw new ArgumentException("A snapshot library belongs to the working model, not to a snapshot.", nameof(store));
        _store = store;
        _options = store.Options;
        _paths = new ModelPaths(_options);
        _policy = store.Services.EnginePaths;
    }

    /// <summary>The folder that holds the archives: <c>&lt;model root&gt;/model-snapshots</c>.</summary>
    public string Folder => Path.Combine(_paths.ModelRoot, SnapshotLayout.Folder);

    /// <summary>The import limits (tests lower them).</summary>
    internal SnapshotLimits Limits { get; init; } = SnapshotLimits.Default;

    /// <summary>Whether a string is a snapshot id: lowercase letters and digits in hyphen-separated words, at most 120 characters.</summary>
    /// <param name="id">The candidate.</param>
    /// <returns><see langword="true"/> when it is one.</returns>
    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 120 } && IdPattern().IsMatch(id);

    /// <summary>Every snapshot, newest first (then by id). An archive whose metadata cannot be read is left out.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshots.</returns>
    public Task<IReadOnlyList<SnapshotInfo>> ListAsync(CancellationToken ct)
    {
        var found = new List<SnapshotInfo>();
        if (Directory.Exists(Folder))
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*.zip", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(file);
                if (IsValidId(id) && TryInfo(id) is { } info)
                    found.Add(info);
            }
        }

        IReadOnlyList<SnapshotInfo> sorted = [.. found.OrderByDescending(s => s.CreatedUtc, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal)];
        return Task.FromResult(sorted);
    }

    /// <summary>One snapshot.</summary>
    /// <param name="id">The id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshot, or <see langword="null"/>.</returns>
    public Task<SnapshotInfo?> GetAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(IsValidId(id) ? TryInfo(id) : null);
    }

    /// <summary>
    /// Takes a snapshot of the working model: every document under <c>maquettiste.json</c>, <c>model/</c>, <c>extensions/</c> and
    /// <c>branding/</c>, and <c>templates/</c> when asked, read from disk in parallel chunks and streamed into the archive (never the
    /// whole model in memory at once). The archive is written beside its final name and renamed into place.
    /// </summary>
    /// <param name="request">The name, description, author and whether to hold the packs.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentException">The name or description is not valid.</exception>
    /// <exception cref="UnauthorizedAccessException">The path policy refused the write.</exception>
    public async Task<SnapshotInfo> CreateAsync(SnapshotCreateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await CreateCoreAsync(request, "user", ct).ConfigureAwait(false)).Info;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Renames, describes or publishes a snapshot: only its <c>snapshot.json</c> (the last entry) is rewritten; the id stays.</summary>
    /// <param name="id">The id.</param>
    /// <param name="update">What to change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshot, or <see langword="null"/> when no snapshot has the id.</returns>
    /// <exception cref="ArgumentException">The name or description is not valid.</exception>
    public async Task<SnapshotInfo?> UpdateAsync(string id, SnapshotUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Name is not null)
            CheckName(update.Name);
        if (update.Description is not null)
            CheckDescription(update.Description);
        if (!IsValidId(id))
            return null;
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = FileOf(id);
            if (!File.Exists(file))
                return null;
            Guard(file);
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
                var entry = zip.GetEntry(SnapshotLayout.MetadataEntry) ?? throw new InvalidDataException($"{file} has no {SnapshotLayout.MetadataEntry}.");
                SnapshotMetadata metadata;
                await using (var read = entry.Open())
                    metadata = SnapshotMetadata.Read(ReadBounded(read, 1024 * 1024));
                var changed = metadata with
                {
                    Name = update.Name?.Trim() ?? metadata.Name,
                    Description = update.Description ?? metadata.Description,
                    Published = update.Published ?? metadata.Published,
                };
                if (changed != metadata)
                {
                    entry.Delete();
                    var replacement = zip.CreateEntry(SnapshotLayout.MetadataEntry, SnapshotLayout.Compression);
                    replacement.LastWriteTime = SnapshotLayout.EntryTime;
                    await using var write = replacement.Open();
                    await write.WriteAsync(changed.Write(), ct).ConfigureAwait(false);
                }
            }

            return TryInfo(id);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Deletes a snapshot's archive.</summary>
    /// <param name="id">The id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether a snapshot had the id.</returns>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id))
            return false;
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = FileOf(id);
            if (!File.Exists(file))
                return false;
            Guard(file);
            Forget(id);
            File.Delete(file);
            return true;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// Opens a snapshot read-only ("as of"): a <see cref="ModelStore"/> whose documents come straight from the archive (nothing is
    /// extracted), so the model index, elements, validation, resolved views and database views read as the snapshot was. Every write
    /// is refused with MQ6029. The two most recently opened snapshots stay loaded.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="ct">Cancellation (of this wait; the load itself completes for the next caller).</param>
    /// <returns>The store, or <see langword="null"/> when no snapshot has the id.</returns>
    public async Task<ModelStore?> OpenAsync(string id, CancellationToken ct) => (await OpenCoreAsync(id, ct).ConfigureAwait(false))?.Store;

    /// <summary>The generation service over a snapshot opened read-only, for the previews (it renders with the working model's packs).</summary>
    /// <param name="id">The id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The service, or <see langword="null"/> when no snapshot has the id.</returns>
    public async Task<GenerationService?> OpenGenerationAsync(string id, CancellationToken ct) => (await OpenCoreAsync(id, ct).ConfigureAwait(false))?.Generation;

    /// <summary>Opens a snapshot's archive for reading (the export): the file as stored, which is the deterministic archive.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The stream, or <see langword="null"/> when no snapshot has the id.</returns>
    public Stream? OpenRead(string id)
    {
        if (!IsValidId(id))
            return null;
        try
        {
            return new FileStream(FileOf(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, useAsync: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private async Task<OpenSnapshot?> OpenCoreAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id))
            return null;
        var file = FileOf(id);
        var info = new FileInfo(file);
        if (!info.Exists || TryInfo(id) is not { } snapshot)
            return null;
        var key = id + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        Lazy<Task<OpenSnapshot>> open;
        lock (_cache)
        {
            var index = _open.FindIndex(o => o.Key == key);
            if (index >= 0)
            {
                open = _open[index].Open;
                _open.RemoveAt(index);
            }
            else
            {
                open = new Lazy<Task<OpenSnapshot>>(() => LoadAsync(file, snapshot), LazyThreadSafetyMode.ExecutionAndPublication);
            }

            _open.RemoveAll(o => o.Key.StartsWith(id + "|", StringComparison.Ordinal));
            _open.Insert(0, (key, open));
            if (_open.Count > MaxOpen)
                _open.RemoveRange(MaxOpen, _open.Count - MaxOpen);
        }

        try
        {
            return await open.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            lock (_cache)
                _open.RemoveAll(o => o.Key == key);
            throw;
        }
    }

    private async Task<OpenSnapshot> LoadAsync(string file, SnapshotInfo snapshot)
    {
        var documents = new ZipDocumentStore(file, includeTemplates: false);
        var services = EngineServices.CreateReadOnly(_options, documents);
        var store = new ModelStore(_options, services);
        try
        {
            await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            documents.Close();
        }

        return new OpenSnapshot(snapshot, store, new GenerationService(store, _options, services));
    }

    private void Forget(string id)
    {
        lock (_cache)
        {
            _open.RemoveAll(o => o.Key.StartsWith(id + "|", StringComparison.Ordinal));
            _infos.Remove(id);
        }
    }

    private async Task<(SnapshotInfo Info, IReadOnlyList<SnapshotIndexRow> Rows)> CreateCoreAsync(SnapshotCreateRequest request, string origin, CancellationToken ct)
    {
        CheckName(request.Name);
        if (request.Description is not null)
            CheckDescription(request.Description);
        if (_store.Documents is not FileDocumentStore live)
            throw new InvalidOperationException("Snapshots are taken of the model folder.");

        var created = _options.EffectiveTimeProvider.GetUtcNow().ToUniversalTime();
        var id = NewId(request.Name, created);
        var final = FileOf(id);
        var temp = Path.Combine(Folder, "." + id + ".zip.tmp");
        Guard(final);
        Guard(temp);
        var paths = ScopePaths(live, request.IncludePacks);
        Directory.CreateDirectory(Folder);
        try
        {
            SnapshotMetadata metadata;
            IReadOnlyList<SnapshotIndexRow> rows;
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024))
            {
                using var writer = new SnapshotArchiveWriter(stream);
                await ReadChunksAsync(paths, live.ReadAsync, (row, bytes) => writer.Add(row, bytes), ct).ConfigureAwait(false);
                rows = [.. writer.Rows];
                metadata = writer.Finish(new SnapshotMetadata
                {
                    Name = request.Name.Trim(),
                    Description = request.Description ?? "",
                    Author = request.Author ?? "",
                    CreatedUtc = created.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    Origin = origin,
                    IncludesPacks = request.IncludePacks,
                    ModelFormat = _store.Current?.Settings.FormatVersion ?? EngineVersion.FormatVersion,
                });
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temp, final, overwrite: false);
            return (InfoOf(id, metadata, new FileInfo(final).Length), rows);
        }
        catch
        {
            AtomicFile.TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Reads documents in chunks of <see cref="Chunk"/>, each chunk in parallel, and hands them to <paramref name="add"/> in path order,
    /// so at most one chunk's bytes are held at once. A document that vanished between the listing and the read is left out.
    /// </summary>
    private async Task ReadChunksAsync(IReadOnlyList<string> paths, Func<string, CancellationToken, Task<byte[]?>> read,
        Action<SnapshotIndexRow, byte[]> add, CancellationToken ct)
    {
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.EffectiveParallelism, CancellationToken = ct };
        for (var start = 0; start < paths.Count; start += Chunk)
        {
            var count = Math.Min(Chunk, paths.Count - start);
            var results = new (SnapshotIndexRow Row, byte[] Bytes)?[count];
            await Parallel.ForEachAsync(Enumerable.Range(0, count), parallel, async (i, token) =>
            {
                var path = paths[start + i];
                if (await read(path, token).ConfigureAwait(false) is { } bytes)
                    results[i] = (SnapshotIndexRow.Of(path, bytes), bytes);
            }).ConfigureAwait(false);
            foreach (var result in results)
            {
                if (result is { } r)
                    add(r.Row, r.Bytes);
            }
        }
    }

    /// <summary>The documents a snapshot of the working model holds, ordinal: settings, the content folders, the packs when asked.</summary>
    private static List<string> ScopePaths(IModelDocumentStore live, bool includePacks)
    {
        var paths = new List<string>();
        if (live.Stat(ModelPaths.SettingsFile) is not null)
            paths.Add(ModelPaths.SettingsFile);
        IEnumerable<string> folders = includePacks ? SnapshotLayout.ContentFolders.Append(SnapshotLayout.TemplatesFolder) : SnapshotLayout.ContentFolders;
        foreach (var folder in folders)
            paths.AddRange(live.List(folder, recursive: true).Where(SnapshotLayout.IsContentPath));
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private string NewId(string name, DateTimeOffset created)
    {
        var stamp = created.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var slug = ModelPaths.Kebab(name);
        if (slug.Length > 80)
            slug = slug[..80].TrimEnd('-');
        if (slug.Length == 0)
            slug = "snapshot";
        var id = slug.EndsWith("-" + stamp, StringComparison.Ordinal) ? slug : slug + "-" + stamp;
        var candidate = id;
        for (var n = 2; File.Exists(FileOf(candidate)) || File.Exists(Path.Combine(Folder, "." + candidate + ".zip.tmp")); n++)
            candidate = id + "-" + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private string FileOf(string id) => Path.Combine(Folder, id + ".zip");

    private void Guard(string file)
    {
        var check = _policy.CheckEngineWrite(WriteTarget.Model, file);
        if (!check.Allowed)
            throw new UnauthorizedAccessException($"{check.RuleId}: the snapshot write to {file} was refused: {check.Reason}");
    }

    private SnapshotInfo? TryInfo(string id)
    {
        var file = FileOf(id);
        var stat = new FileInfo(file);
        if (!stat.Exists)
            return null;
        lock (_cache)
        {
            if (_infos.TryGetValue(id, out var cached) && cached.Length == stat.Length && cached.Ticks == stat.LastWriteTimeUtc.Ticks)
                return cached.Info;
        }

        SnapshotInfo info;
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var entry = zip.GetEntry(SnapshotLayout.MetadataEntry);
            if (entry is null)
                return null;
            using var read = entry.Open();
            info = InfoOf(id, SnapshotMetadata.Read(ReadBounded(read, 1024 * 1024)), stat.Length);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }

        lock (_cache)
            _infos[id] = (stat.Length, stat.LastWriteTimeUtc.Ticks, info);
        return info;
    }

    private static SnapshotInfo InfoOf(string id, SnapshotMetadata m, long size) =>
        new(id, m.Name, m.Description, m.Author, m.CreatedUtc, m.Origin, m.Published, m.IncludesPacks, m.ModelHash, m.ModelFormat, m.Engine,
            m.Files, m.Elements, m.Kinds, size);

    /// <summary>Reads a stream to its end, refusing more than <paramref name="limit"/> bytes.</summary>
    internal static byte[] ReadBounded(Stream stream, long limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidDataException($"An entry is larger than {limit.ToString(CultureInfo.InvariantCulture)} bytes.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void CheckName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
            throw new ArgumentException($"A snapshot name has 1 to {MaxNameLength} characters and no control character.", nameof(name));
    }

    private static void CheckDescription(string description)
    {
        if (description.Length > MaxDescriptionLength)
            throw new ArgumentException($"A snapshot description has at most {MaxDescriptionLength} characters.", nameof(description));
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private sealed record OpenSnapshot(SnapshotInfo Info, ModelStore Store, GenerationService Generation);
}

/// <summary>The limits an imported archive is held to.</summary>
/// <param name="MaxArchiveBytes">The archive's size.</param>
/// <param name="MaxEntryBytes">One document's size, uncompressed.</param>
/// <param name="MaxTotalBytes">Every document's size together, uncompressed.</param>
/// <param name="MaxEntries">The number of entries.</param>
/// <param name="MaxRatio">The most an entry over 1 MB may expand (uncompressed over compressed size).</param>
internal sealed record SnapshotLimits(long MaxArchiveBytes, long MaxEntryBytes, long MaxTotalBytes, int MaxEntries, int MaxRatio)
{
    /// <summary>512 MB archives, 64 MB documents, 4 GB in all, 500,000 entries, 200 to 1.</summary>
    public static SnapshotLimits Default { get; } = new(512L * 1024 * 1024, 64L * 1024 * 1024, 4L * 1024 * 1024 * 1024, 500_000, 200);
}
