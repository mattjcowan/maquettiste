using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>A document's stat: its length and a last-write stamp (UTC ticks); equal stats mean the loader may reuse what it read.</summary>
/// <param name="Length">The length in bytes.</param>
/// <param name="LastWriteTicks">The last-write time, UTC ticks (a fixed value for a store whose documents never change).</param>
internal readonly record struct DocumentStat(long Length, long LastWriteTicks);

/// <summary>
/// Where the model's documents come from and go to (docs/engineering/snapshots.md section 2): the seam under <see cref="ModelStore"/>
/// and <see cref="ModelLoader"/>. Paths are model-relative (<c>model/entities/invoice.json</c>, <c>maquettiste.json</c>) with <c>/</c>
/// separators. Everything above it (validation, resolution, delete plans, batches, the live feed) is the same whatever the store.
/// </summary>
/// <remarks>
/// Three providers: <see cref="FileDocumentStore"/>, the model folder (the live model, read and written);
/// <see cref="Snapshots.ZipDocumentStore"/>, a snapshot archive (read-only, never extracted to disk); and
/// <see cref="ReadOnlyFolderDocumentStore"/>, another copy of a project opened as a baseline (read-only).
/// </remarks>
internal interface IModelDocumentStore
{
    /// <summary>What the store reads, for messages (the model folder, or the snapshot file).</summary>
    string Description { get; }

    /// <summary>Whether every write is refused (a snapshot); its documents then never change either.</summary>
    bool IsReadOnly { get; }

    /// <summary>Whether the loader may read and write the index cache for these documents (only the model folder: the cache is keyed by stat).</summary>
    bool UsesIndexCache { get; }

    /// <summary>Stats a document.</summary>
    /// <param name="modelPath">The model-relative path.</param>
    /// <returns>The stat, or <see langword="null"/> when there is no such document.</returns>
    DocumentStat? Stat(string modelPath);

    /// <summary>Whether a folder holds anything (the model root is <c>""</c>).</summary>
    /// <param name="modelPath">The model-relative folder.</param>
    /// <returns><see langword="true"/> when it exists.</returns>
    bool FolderExists(string modelPath);

    /// <summary>Lists the documents under a folder, model-relative, in no particular order; a missing folder lists nothing.</summary>
    /// <param name="modelFolder">The model-relative folder (<c>""</c> for the root).</param>
    /// <param name="recursive">Whether to include subfolders.</param>
    /// <returns>The paths, hidden ones (a segment starting with <c>.</c>) included; callers filter.</returns>
    IReadOnlyList<string> List(string modelFolder, bool recursive);

    /// <summary>Reads a document's bytes.</summary>
    /// <param name="modelPath">The model-relative path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes, or <see langword="null"/> when there is no such document.</returns>
    /// <exception cref="IOException">The document exists but cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The document exists but cannot be read.</exception>
    Task<byte[]?> ReadAsync(string modelPath, CancellationToken ct);

    /// <summary>Writes and deletes documents all or nothing.</summary>
    /// <param name="writes">Model-relative paths and their new bytes.</param>
    /// <param name="deletes">Model-relative paths to delete (missing ones are ignored).</param>
    /// <param name="batchId">A token that makes staged names unique.</param>
    /// <param name="ct">Cancellation, observed until the first document is replaced.</param>
    /// <returns><see langword="null"/> on success, else the failure; nothing changed then.</returns>
    Task<FileSetFailure?> ApplyAsync(IReadOnlyList<(string Path, byte[] Bytes)> writes, IReadOnlyList<string> deletes, string batchId, CancellationToken ct);
}

/// <summary>
/// The model folder as a document store: the files under <c>ModelRoot</c>. Reads go to the file system; writes go through
/// <see cref="AtomicFileSet"/> and so through <see cref="IOutputPathPolicy.CheckEngineWrite"/> (<see cref="WriteTarget.Model"/>).
/// </summary>
/// <param name="paths">The model path conventions.</param>
/// <param name="policy">The engine-write guard.</param>
internal sealed class FileDocumentStore(ModelPaths paths, IOutputPathPolicy policy) : IModelDocumentStore
{
    private static readonly EnumerationOptions Recursive = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchType = MatchType.Simple };
    private static readonly EnumerationOptions TopOnly = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, MatchType = MatchType.Simple };

    /// <summary>The conventions this store resolves paths with.</summary>
    public ModelPaths Paths => paths;

    /// <inheritdoc/>
    public string Description => paths.ModelRoot;

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public bool UsesIndexCache => true;

    /// <inheritdoc/>
    public DocumentStat? Stat(string modelPath)
    {
        var info = new FileInfo(paths.FullPath(modelPath));
        return info.Exists ? new DocumentStat(info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    /// <inheritdoc/>
    public bool FolderExists(string modelPath) => Directory.Exists(paths.FullPath(modelPath));

    /// <inheritdoc/>
    public IReadOnlyList<string> List(string modelFolder, bool recursive)
    {
        var folder = paths.FullPath(modelFolder);
        if (!Directory.Exists(folder))
            return [];
        try
        {
            return Directory.EnumerateFiles(folder, "*", recursive ? Recursive : TopOnly)
                .Select(paths.ToModelPath)
                .Where(p => p is { Length: > 0 })
                .Select(p => p!)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // a folder that vanished mid-scan
        }
    }

    /// <inheritdoc/>
    public async Task<byte[]?> ReadAsync(string modelPath, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(paths.FullPath(modelPath), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<FileSetFailure?> ApplyAsync(IReadOnlyList<(string Path, byte[] Bytes)> writes, IReadOnlyList<string> deletes, string batchId, CancellationToken ct) =>
        new AtomicFileSet(policy, paths.ModelRoot).ApplyAsync(
            [.. writes.Select(w => (paths.FullPath(w.Path), w.Bytes))], [.. deletes.Select(paths.FullPath)], batchId, ct);
}

/// <summary>
/// Another copy of a project's model folder as a read-only document store: a baseline folder (<see cref="ModelBaseline"/>,
/// next-phase-1-design.md section 3.1). It reads as <see cref="FileDocumentStore"/> does, refuses every write with MQ6029 as a
/// snapshot does, and keeps the loader away from the index cache, so neither the folder nor the user cache is written.
/// </summary>
/// <param name="paths">The model path conventions of the folder.</param>
internal sealed class ReadOnlyFolderDocumentStore(ModelPaths paths) : IModelDocumentStore
{
    private readonly FileDocumentStore _files = new(paths, Snapshots.ReadOnlyPathPolicy.Instance);

    /// <inheritdoc/>
    public string Description => paths.ModelRoot;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool UsesIndexCache => false;

    /// <inheritdoc/>
    public DocumentStat? Stat(string modelPath) => _files.Stat(modelPath);

    /// <inheritdoc/>
    public bool FolderExists(string modelPath) => _files.FolderExists(modelPath);

    /// <inheritdoc/>
    public IReadOnlyList<string> List(string modelFolder, bool recursive) => _files.List(modelFolder, recursive);

    /// <inheritdoc/>
    public Task<byte[]?> ReadAsync(string modelPath, CancellationToken ct) => _files.ReadAsync(modelPath, ct);

    /// <inheritdoc/>
    public Task<FileSetFailure?> ApplyAsync(IReadOnlyList<(string Path, byte[] Bytes)> writes, IReadOnlyList<string> deletes, string batchId, CancellationToken ct) =>
        Task.FromResult<FileSetFailure?>(new FileSetFailure(paths.ModelRoot, Snapshots.ReadOnlyPathPolicy.Reason, true));
}

/// <summary>
/// The documents of a baseline or a snapshot opened read-only, read tolerantly (docs/engineering/retire-projection-design.md section 9): a
/// database, mapping, table or stereotype file taken before 0.29.0 has the keys that release removed with projection dropped
/// (<see cref="Json.RemovedProjectionKeys.Strip"/>), so it loads and an older migration history still compares; an entity mapping or an
/// overlay file reads as no document. Every other document passes through unchanged.
/// </summary>
internal sealed class TolerantDocumentStore(IModelDocumentStore inner) : IModelDocumentStore
{
    private volatile bool _laidOut;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _conventionless = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the model read laid out tables by convention before 0.29.0, which the dropped keys no longer say: an entity mapping, an
    /// overlay, the promoted shape or a database's <c>byConvention</c> other than <c>none</c> was read, or a database had no
    /// <c>byConvention</c> while some entity of <paramref name="snapshot"/> is not bound to it. A schema comparison with such a model
    /// would see every one of those tables as new.
    /// </summary>
    /// <param name="snapshot">The model as loaded through this store.</param>
    /// <returns><see langword="true"/> when it did.</returns>
    public bool LaidOutTables(ModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_laidOut)
            return true;
        if (_conventionless.IsEmpty)
            return false;
        foreach (var entity in snapshot.All<Entity>())
        {
            foreach (var database in _conventionless.Keys)
            {
                if (!entity.Bindings.Any(b => string.Equals(b.Database, database, StringComparison.Ordinal)))
                    return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public string Description => inner.Description;

    /// <inheritdoc/>
    public bool IsReadOnly => inner.IsReadOnly;

    /// <inheritdoc/>
    public bool UsesIndexCache => false;

    /// <inheritdoc/>
    public DocumentStat? Stat(string modelPath) => inner.Stat(modelPath);

    /// <inheritdoc/>
    public bool FolderExists(string modelPath) => inner.FolderExists(modelPath);

    /// <inheritdoc/>
    public IReadOnlyList<string> List(string modelFolder, bool recursive) => inner.List(modelFolder, recursive);

    /// <inheritdoc/>
    public async Task<byte[]?> ReadAsync(string modelPath, CancellationToken ct)
    {
        var bytes = await inner.ReadAsync(modelPath, ct).ConfigureAwait(false);
        if (bytes is null || !modelPath.EndsWith(".json", StringComparison.Ordinal) || !MayHoldRemovedKeys(bytes))
            return bytes;
        System.Text.Json.Nodes.JsonObject? node;
        try
        {
            node = System.Text.Json.Nodes.JsonNode.Parse(bytes, documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip }) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return bytes;
        }

        var fileName = node?["kind"] is System.Text.Json.Nodes.JsonValue kind && kind.TryGetValue<string>(out var k) ? k + ".json" : null;
        if (node is null || fileName is null || !Json.RemovedProjectionKeys.Applies(fileName) || fileName == "maquettiste.json")
            return bytes;
        var before = node.ToJsonString();
        var stripped = Json.RemovedProjectionKeys.Strip(fileName, node);
        if (stripped.Layout == Json.StripLayout.LaidOut)
            _laidOut = true;
        else if (stripped.Layout == Json.StripLayout.NoConvention && node["id"] is System.Text.Json.Nodes.JsonValue id && id.TryGetValue<string>(out var databaseId))
            _conventionless[databaseId] = 0;
        if (!stripped.Keep)
            return null;
        var after = node.ToJsonString();
        return string.Equals(before, after, StringComparison.Ordinal) ? bytes
            : System.Text.Encoding.UTF8.GetBytes(node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <inheritdoc/>
    public Task<FileSetFailure?> ApplyAsync(IReadOnlyList<(string Path, byte[] Bytes)> writes, IReadOnlyList<string> deletes, string batchId, CancellationToken ct) =>
        inner.ApplyAsync(writes, deletes, batchId, ct);

    private static readonly byte[][] Kinds =
        [.. new[] { "\"database\"", "\"mapping\"", "\"table\"", "\"stereotype\"" }.Select(System.Text.Encoding.UTF8.GetBytes)];

    /// <summary>Whether a document may be of a kind that can hold a removed key, before parsing it: most of a model's files are not.</summary>
    private static bool MayHoldRemovedKeys(byte[] bytes)
    {
        var span = bytes.AsSpan();
        foreach (var kind in Kinds)
        {
            if (span.IndexOf(kind) >= 0)
                return true;
        }

        return false;
    }
}
