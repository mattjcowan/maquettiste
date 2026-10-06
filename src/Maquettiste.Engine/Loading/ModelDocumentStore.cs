using Maquettiste.Engine.Pipeline;

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
/// Two providers: <see cref="FileDocumentStore"/>, the model folder (the live model, read and written), and
/// <see cref="Snapshots.ZipDocumentStore"/>, a snapshot archive (read-only, never extracted to disk).
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
