using System.IO.Compression;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Snapshots;

/// <summary>
/// A snapshot archive as a read-only document store (docs/engineering/snapshots.md section 2): the model documents it holds are served
/// straight from the zip, never extracted to disk. Its table of entries is read once; the archive itself is opened on the first read
/// and kept open for the load; after <see cref="Close"/> each later read opens and closes it, so an opened snapshot holds no file handle.
/// </summary>
internal sealed class ZipDocumentStore : IModelDocumentStore, IDisposable
{
    /// <summary>The stamp every entry reports: the archive's documents never change.</summary>
    internal static readonly long FixedTicks = SnapshotLayout.EntryTime.UtcTicks;

    private readonly string _file;
    private readonly Dictionary<string, long> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal) { "" };
    private readonly Lock _gate = new();
    private FileStream? _stream;
    private ZipArchive? _archive;
    private bool _closeAfterRead;

    /// <summary>Reads the archive's table of entries; only the content paths of <see cref="SnapshotLayout"/> are served.</summary>
    /// <param name="file">The archive.</param>
    /// <param name="includeTemplates">Whether the packs under <c>templates/</c> are served too.</param>
    public ZipDocumentStore(string file, bool includeTemplates = true)
    {
        _file = file;
        using var stream = OpenFile(file);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!SnapshotLayout.IsContentPath(name) || (!includeTemplates && SnapshotLayout.IsTemplatePath(name)))
                continue;
            _entries[name] = entry.Length;
            for (var slash = name.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = name.IndexOf('/', slash + 1))
                _folders.Add(name[..slash]);
        }
    }

    /// <summary>The content paths the archive serves.</summary>
    public IReadOnlyCollection<string> Paths => _entries.Keys;

    /// <inheritdoc/>
    public string Description => _file;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool UsesIndexCache => false;

    /// <inheritdoc/>
    public DocumentStat? Stat(string modelPath) =>
        _entries.TryGetValue(modelPath, out var length) ? new DocumentStat(length, FixedTicks) : null;

    /// <inheritdoc/>
    public bool FolderExists(string modelPath) => _folders.Contains(modelPath);

    /// <inheritdoc/>
    public IReadOnlyList<string> List(string modelFolder, bool recursive)
    {
        var prefix = modelFolder.Length == 0 ? "" : modelFolder + "/";
        var found = new List<string>();
        foreach (var path in _entries.Keys)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal) && (recursive || path.IndexOf('/', prefix.Length) < 0))
                found.Add(path);
        }

        return found;
    }

    /// <inheritdoc/>
    public Task<byte[]?> ReadAsync(string modelPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(modelPath, out var length))
            return Task.FromResult<byte[]?>(null);
        lock (_gate)
        {
            if (_archive is null)
            {
                _stream = OpenFile(_file);
                _archive = new ZipArchive(_stream, ZipArchiveMode.Read, leaveOpen: true);
            }

            try
            {
                var entry = _archive.GetEntry(modelPath) ?? throw new IOException($"{modelPath} is missing from {_file}.");
                var bytes = new byte[checked((int)length)];
                using var source = entry.Open();
                source.ReadExactly(bytes);
                return Task.FromResult<byte[]?>(bytes);
            }
            finally
            {
                if (_closeAfterRead)
                    CloseArchive();
            }
        }
    }

    /// <inheritdoc/>
    public Task<FileSetFailure?> ApplyAsync(IReadOnlyList<(string Path, byte[] Bytes)> writes, IReadOnlyList<string> deletes, string batchId, CancellationToken ct) =>
        Task.FromResult<FileSetFailure?>(new FileSetFailure(_file, ReadOnlyPathPolicy.Reason, true));

    /// <summary>Closes the archive; from now on each read opens it and closes it again (an opened snapshot holds no file handle).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closeAfterRead = true;
            CloseArchive();
        }
    }

    private void CloseArchive()
    {
        _archive?.Dispose();
        _stream?.Dispose();
        _archive = null;
        _stream = null;
    }

    /// <inheritdoc/>
    public void Dispose() => Close();

    private static FileStream OpenFile(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
}

/// <summary>The engine-write guard of a snapshot opened read-only: every write is refused (MQ6029).</summary>
internal sealed class ReadOnlyPathPolicy : IOutputPathPolicy
{
    /// <summary>Why every write is refused.</summary>
    public const string Reason = "this model is a snapshot opened read-only; restore it to change it";

    /// <summary>The one instance.</summary>
    public static ReadOnlyPathPolicy Instance { get; } = new();

    /// <inheritdoc/>
    public PathCheck Check(string repoRelativePath) => new(false, repoRelativePath ?? "", null, "MQ6029", Reason);

    /// <inheritdoc/>
    public PathCheck CheckEngineWrite(WriteTarget target, string fullPath) => new(false, fullPath ?? "", null, "MQ6029", Reason);
}
