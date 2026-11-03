using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Snapshots;

namespace Maquettiste.Engine;

/// <summary>What a <see cref="ModelBaseline"/> was opened from.</summary>
public enum BaselineKind
{
    /// <summary>A model snapshot of the working project (<c>model-snapshots/&lt;id&gt;.zip</c>).</summary>
    Snapshot,

    /// <summary>A folder that holds another copy of the project.</summary>
    Folder,
}

/// <summary>
/// What <see cref="ModelBaseline.OpenAsync"/> throws for a value that opens no baseline, so a host tells a bad baseline from any other
/// argument error of a run that opens one.
/// </summary>
/// <param name="message">The message, naming the value.</param>
/// <param name="paramName">The parameter: <c>against</c>.</param>
internal sealed class BadBaselineException(string message, string paramName) : ArgumentException(message, paramName)
{
    /// <summary>The message without the parameter's name.</summary>
    public string Text { get; } = message;
}

/// <summary>
/// A read-only model to compare with (next-phase-1-design.md section 3.1, scope 5.10): a model snapshot id or a folder that holds
/// another copy of the project, never a repository. It loads through the document store seam with its own <c>maquettiste.json</c>,
/// without packs or templates; every write is refused (MQ6029). A folder baseline owns its store; a snapshot's store stays in the
/// library's open cache (two snapshots stay loaded), so <see cref="Dispose"/> leaves it alone.
/// </summary>
/// <remarks>
/// <see cref="OpenAsync"/> resolves the value in one order. A value that is rooted, starts with <c>.</c> or holds a path separator is a
/// folder. Otherwise it is a snapshot id when the archive exists, else a folder when the directory exists, else nothing. A folder is a
/// project root (holding <c>.maquettiste/maquettiste.json</c>) or the <c>.maquettiste</c> folder itself. A model snapshot or a folder
/// at a newer model format is refused with MQ1007's text. Load and validation errors of the baseline do not stop the open; the
/// callers decide how to use them.
/// </remarks>
public sealed class ModelBaseline : IDisposable
{
    /// <summary>The name of a project's model folder.</summary>
    private const string ModelFolder = ".maquettiste";

    private readonly bool _ownsStore;
    private bool _disposed;

    private ModelBaseline(BaselineKind kind, string against, string label, ModelStore store, string modelHash, bool ownsStore)
    {
        Kind = kind;
        Against = against;
        Label = label;
        Store = store;
        ModelHash = modelHash;
        _ownsStore = ownsStore;
    }

    /// <summary>What the baseline was opened from.</summary>
    public BaselineKind Kind { get; }

    /// <summary>The value as given.</summary>
    public string Against { get; }

    /// <summary>For messages: <c>snapshot &lt;id&gt;</c> or <c>folder &lt;full path&gt;</c>.</summary>
    public string Label { get; }

    /// <summary>The baseline's model, loaded; <see cref="ModelStore.IsReadOnly"/> is <see langword="true"/>.</summary>
    public ModelStore Store { get; }

    /// <summary>The hash over every document's path and hash, packs aside, as a snapshot's metadata computes it.</summary>
    public string ModelHash { get; }

    /// <summary>
    /// Opens a baseline: a model snapshot of <paramref name="working"/>, or (when <paramref name="allowFolders"/>) a folder that holds
    /// another copy of the project.
    /// </summary>
    /// <param name="working">The working model's store (the snapshots are its own).</param>
    /// <param name="against">A model snapshot id or a folder.</param>
    /// <param name="currentDirectory">The absolute folder a relative folder is resolved against.</param>
    /// <param name="allowFolders">Whether a folder may be opened: only the command-line tool and its MCP server allow it, since a path
    /// in a request to the editor's host would name a folder of the server.</param>
    /// <param name="library">The host's snapshot library, so a long-lived host loads one archive once; <see langword="null"/> builds one
    /// for this call.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The baseline, loaded.</returns>
    /// <exception cref="ArgumentException">The value resolves to nothing (the message names it), names a folder where folders are not
    /// allowed, or names a model at a newer format (MQ1007).</exception>
    public static async Task<ModelBaseline> OpenAsync(ModelStore working, string against, string currentDirectory,
        bool allowFolders, SnapshotLibrary? library, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        if (string.IsNullOrWhiteSpace(against))
            throw new BadBaselineException("A baseline needs a model snapshot id or a folder.", nameof(against));

        var pathLike = IsPathLike(against);
        if (!pathLike && SnapshotLibrary.IsValidId(against))
        {
            library ??= new SnapshotLibrary(working);
            if (await library.GetAsync(against, ct).ConfigureAwait(false) is { } info)
            {
                if (info.ModelFormat > EngineVersion.FormatVersion)
                    throw new BadBaselineException(NewerFormat("snapshot " + against, info.ModelFormat), nameof(against));
                if (await library.OpenAsync(against, ct).ConfigureAwait(false) is { } store)
                    return new ModelBaseline(BaselineKind.Snapshot, against, "snapshot " + against, store, info.ModelHash, ownsStore: false);
            }
        }

        if (!allowFolders)
        {
            throw new BadBaselineException(pathLike
                ? $"'{against}' is a folder; this host compares with model snapshots only."
                : $"'{against}' names no model snapshot.", nameof(against));
        }

        return await OpenFolderAsync(working, against, currentDirectory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a folder baseline, the part of <see cref="OpenAsync"/>'s order after the snapshot ids: the folder must exist and be a project
    /// root or its <c>.maquettiste</c> folder at a model format this release reads. A model comparison opens its folder sides here.
    /// </summary>
    /// <param name="working">The working model's store.</param>
    /// <param name="against">The value as given.</param>
    /// <param name="currentDirectory">The absolute folder a relative folder is resolved against.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The baseline, loaded; it owns its store.</returns>
    /// <exception cref="BadBaselineException">The folder does not exist, is not a project, or holds a newer model format.</exception>
    internal static async Task<ModelBaseline> OpenFolderAsync(ModelStore working, string against, string currentDirectory, CancellationToken ct)
    {
        var pathLike = IsPathLike(against);
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(against, currentDirectory));
        if (!Directory.Exists(folder))
        {
            throw new BadBaselineException(pathLike
                ? $"'{against}' names no folder ({folder})."
                : $"'{against}' names neither a model snapshot nor a folder.", nameof(against));
        }

        string repoRoot, modelRoot;
        if (File.Exists(Path.Combine(folder, ModelFolder, ModelPaths.SettingsFile)))
        {
            repoRoot = folder;
            modelRoot = Path.Combine(folder, ModelFolder);
        }
        else if (string.Equals(Path.GetFileName(folder), ModelFolder, StringComparison.Ordinal)
            && File.Exists(Path.Combine(folder, ModelPaths.SettingsFile)))
        {
            // Only the model folder itself: any other folder that holds a maquettiste.json (the settings schema under
            // .maquettiste/.schema/v1, for one) is not a project.
            repoRoot = Path.GetDirectoryName(folder) ?? folder;
            modelRoot = folder;
        }
        else
        {
            throw new BadBaselineException(
                $"'{against}' is not a project: {folder} is not a {ModelFolder} folder and holds no {ModelFolder}/{ModelPaths.SettingsFile}.",
                nameof(against));
        }

        return await LoadFolderAsync(working, against, folder, repoRoot, modelRoot, ct).ConfigureAwait(false);
    }

    /// <summary>Releases a folder baseline's store; a snapshot's store stays in its library.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsStore)
        {
            // The store holds no file handle; its dispose marks it disposed at once and only then waits for a call in flight.
            _ = Store.DisposeAsync().AsTask();
        }
    }

    /// <summary>
    /// Whether a value names a folder whatever exists: rooted, starting with <c>.</c>, or holding a path separator (<c>/</c> or
    /// <c>\</c> on every platform; no snapshot id holds either).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    internal static bool IsPathLike(string value) =>
        Path.IsPathRooted(value) || value.StartsWith('.') || value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal);

    /// <summary>
    /// The text a generation request carries for a baseline value (<see cref="GenerationRequest.Against"/>), resolved in
    /// <see cref="OpenAsync"/>'s order: a snapshot id as given when its archive exists, else the full path of the folder the value
    /// names (relative to <paramref name="currentDirectory"/>), so a stored plan reopens the same folder whatever the current directory
    /// is then. A value that names neither is returned as given; opening it fails with the usual message.
    /// </summary>
    /// <param name="library">The working model's snapshot library.</param>
    /// <param name="against">The value as given.</param>
    /// <param name="currentDirectory">The absolute folder a relative folder is resolved against.</param>
    /// <returns>The snapshot id, the full folder path, or the value.</returns>
    internal static string RequestText(SnapshotLibrary library, string against, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(against);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        var pathLike = IsPathLike(against);
        if (!pathLike && SnapshotLibrary.IsValidId(against) && File.Exists(Path.Combine(library.Folder, against + ".zip")))
            return against;
        if (string.IsNullOrWhiteSpace(against))
            return against;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(against, currentDirectory));
        return pathLike || Directory.Exists(folder) ? folder : against;
    }

    private static async Task<ModelBaseline> LoadFolderAsync(ModelStore working, string against, string folder, string repoRoot,
        string modelRoot, CancellationToken ct)
    {
        var options = working.Options with { RepoRoot = repoRoot, ModelRoot = modelRoot };
        var documents = new ReadOnlyFolderDocumentStore(new ModelPaths(options));
        var label = "folder " + folder;
        if (await FormatVersionAsync(documents, ct).ConfigureAwait(false) is { } format && format > EngineVersion.FormatVersion)
            throw new BadBaselineException(NewerFormat(label, format), nameof(against));

        var hash = await HashAsync(documents, options.EffectiveParallelism, ct).ConfigureAwait(false);
        var store = new ModelStore(options, EngineServices.CreateReadOnly(options, documents));
        try
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new ModelBaseline(BaselineKind.Folder, against, label, store, hash, ownsStore: true);
    }

    /// <summary>The settings' <c>formatVersion</c>, or <see langword="null"/> when there is none to read (the load reports why).</summary>
    private static async Task<long?> FormatVersionAsync(ReadOnlyFolderDocumentStore documents, CancellationToken ct)
    {
        if (await documents.ReadAsync(ModelPaths.SettingsFile, ct).ConfigureAwait(false) is not { } bytes)
            return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("formatVersion", out var version)
                && version.TryGetInt64(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null; // formatVersion is not a number: MQ1002 at load
        }
    }

    /// <summary>
    /// The model hash of a folder: the documents a snapshot of it would hold (settings, <c>model/</c>, <c>extensions/</c>,
    /// <c>branding/</c>; never the packs), each path with its content hash in ordinal order, as <see cref="SnapshotMetadata.WithCounts"/>
    /// computes a snapshot's. The same documents give the hash of a snapshot taken of them.
    /// </summary>
    private static async Task<string> HashAsync(ReadOnlyFolderDocumentStore documents, int parallelism, CancellationToken ct)
    {
        var paths = new List<string>();
        if (documents.Stat(ModelPaths.SettingsFile) is not null)
            paths.Add(ModelPaths.SettingsFile);
        foreach (var top in SnapshotLayout.ContentFolders)
            paths.AddRange(documents.List(top, recursive: true).Where(SnapshotLayout.IsContentPath));
        paths.Sort(StringComparer.Ordinal);

        var rows = new SnapshotIndexRow?[paths.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count), new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (i, token) =>
            {
                if (await documents.ReadAsync(paths[i], token).ConfigureAwait(false) is { } bytes)
                    rows[i] = new SnapshotIndexRow(paths[i], ContentHash.Of(bytes), null, null, null);
            }).ConfigureAwait(false);
        var metadata = new SnapshotMetadata { Name = "", CreatedUtc = "" }.WithCounts([.. rows.OfType<SnapshotIndexRow>()]);
        return metadata.ModelHash;
    }

    /// <summary>MQ1007's text for a model at a newer format, naming it by <paramref name="label"/>.</summary>
    internal static string NewerFormat(string label, long format) =>
        $"MQ1007 {RuleCatalog.Get("MQ1007").Description.TrimEnd('.')}: the baseline ({label}) is at model format "
        + format.ToString(CultureInfo.InvariantCulture) + "; this release reads format "
        + EngineVersion.FormatVersion.ToString(CultureInfo.InvariantCulture) + ".";
}
