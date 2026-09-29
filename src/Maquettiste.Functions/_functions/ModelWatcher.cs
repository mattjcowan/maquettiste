using System.Threading.Channels;
using Maquettiste.Engine;
using Microsoft.Extensions.Logging;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>
/// Loads the model, publishes every change, and keeps the index in step with the disk (phase2-design.md section 3.6): a file watcher
/// on the model root with a 250 ms quiet period, the validation loop beside it, and a stat-based rescan every 30 seconds for mounts that
/// drop events.
/// </summary>
public static class ModelWatcher
{
    /// <summary>How long the watcher waits after the last event before it refreshes the store.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the watcher waits before it tries to start a file watcher again after one failed.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    /// <summary>Loads the model, subscribes <c>model.changed</c>, runs the validation loop and the file watcher until stopped.</summary>
    /// <param name="stoppingToken">Stops the service (a redeploy or shutdown).</param>
    /// <param name="store">The model store.</param>
    /// <param name="events">The publisher.</param>
    /// <param name="settings">The editor settings (the model root).</param>
    /// <param name="logger">The functions' logger.</param>
    /// <returns>A task that completes when stopped.</returns>
    [BackgroundService]
    public static async Task Run(CancellationToken stoppingToken, ModelStore store, EditorEvents events, EditorSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            await store.LoadAsync(stoppingToken).ConfigureAwait(false);
            events.LoadFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            events.LoadFailed = true;
            throw;
        }

        using var subscription = store.OnChanged(events.OnModelChangedAsync);
        var validation = events.RunValidationLoopAsync(stoppingToken);
        var root = settings.Engine.ModelRoot!;
        var paths = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = Start(root, paths.Writer, events, logger);
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await CollectAsync(paths.Reader, watcher is null ? RestartDelay : Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                if (watcher is null)
                {
                    // Polling: the file watcher failed. Rescan once, then try to watch again.
                    await store.RescanAsync(false, stoppingToken).ConfigureAwait(false);
                    watcher = Start(root, paths.Writer, events, logger);
                    continue;
                }

                if (batch.Overflowed)
                {
                    watcher.Dispose();
                    watcher = null;
                    events.Watcher = "polling";
                    logger.LogWarning("maquettiste: the model file watcher failed; rescanning, and watching again in {Delay}.", RestartDelay);
                    await store.RescanAsync(false, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ApplyAsync(batch.Paths, store, events, stoppingToken, root).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            watcher?.Dispose();
            events.Watcher = "stopped";
            try
            {
                await validation.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Rescans the model folder by stat (cheap when nothing changed) and prunes the presence of connections that went away.</summary>
    /// <param name="store">The model store.</param>
    /// <param name="presence">The presence registry.</param>
    /// <param name="events">The publisher.</param>
    /// <param name="realtime">The site's realtime side.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    [Every("30s")]
    public static async Task Rescan(ModelStore store, PresenceRegistry presence, EditorEvents events, IRealtime realtime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(events);
        if (store.Current is not null)
            await store.RescanAsync(false, ct).ConfigureAwait(false);
        if (presence.Prune(realtime))
            await events.PublishPresenceAsync(presence, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes the store for a batch of model-relative paths, and publishes <c>project.changed</c> when settings, packs or extension
    /// schemas changed: those yield no element change set, yet change validation, resolved tables and previews.
    /// </summary>
    /// <param name="paths">Model-relative paths with <c>/</c> separators.</param>
    /// <param name="store">The model store.</param>
    /// <param name="events">The publisher.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <param name="modelRoot">The model root, to hash the changed pack files for <c>templates.changed</c>; <see langword="null"/> sends no hashes.</param>
    public static async Task ApplyAsync(IReadOnlyCollection<string> paths, ModelStore store, EditorEvents events, CancellationToken ct, string? modelRoot = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        if (paths.Count == 0)
            return;

        // A folder event (a checkout, a renamed folder) says nothing about the files inside it: rescan instead.
        if (paths.Any(p => Path.GetExtension(p).Length == 0))
            await store.RescanAsync(false, ct).ConfigureAwait(false);
        else
            await store.RefreshAsync(paths, ct).ConfigureAwait(false);

        var hash = store.Current?.SettingsHash;
        if (hash is null)
            return;
        if (paths.Any(p => p.StartsWith("templates/", StringComparison.Ordinal) || p.StartsWith("extensions/", StringComparison.Ordinal)))
            await events.OnProjectFilesChangedAsync(hash, ct, TemplateChanges(paths, modelRoot)).ConfigureAwait(false);
        else if (paths.Contains("maquettiste.json"))
            await events.OnSettingsChangedAsync(hash, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The pack files among model-relative paths (<c>templates/&lt;pack&gt;/…</c>), by pack, each with its SHA-256 now (<see langword="null"/>
    /// when gone or when <paramref name="modelRoot"/> is unknown). A folder event names its pack with no files: reload the pack.
    /// </summary>
    /// <param name="paths">Model-relative paths.</param>
    /// <param name="modelRoot">The model root, or <see langword="null"/>.</param>
    /// <returns>One entry per pack, ordinal, files ordinal.</returns>
    internal static IReadOnlyList<TemplatesChangedEvent> TemplateChanges(IEnumerable<string> paths, string? modelRoot)
    {
        var byPack = new SortedDictionary<string, SortedDictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var segments = path.Split('/');
            if (segments.Length < 2 || segments[0] != "templates" || segments[1].Length == 0)
                continue;
            if (!byPack.TryGetValue(segments[1], out var files))
                byPack[segments[1]] = files = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            if (segments.Length < 3 || Path.GetExtension(path).Length == 0)
                continue;
            string? hash = null;
            if (modelRoot is not null)
            {
                try
                {
                    var full = Path.Combine(modelRoot, path.Replace('/', Path.DirectorySeparatorChar));
                    hash = File.Exists(full) ? Maquettiste.Engine.Hashing.ContentHash.Of(File.ReadAllBytes(full)) : null;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            files[string.Join('/', segments[2..])] = hash;
        }

        return [.. byPack.Select(p => new TemplatesChangedEvent(p.Key, [.. p.Value.Select(f => new TemplateFileHash(f.Key, f.Value))]))];
    }

    /// <summary>
    /// The model-relative path of a watcher event, or <see langword="null"/> for a path the engine owns or writes itself: the index
    /// cache and journal (<c>.cache/</c>), <c>manifest/</c>, <c>snapshots/</c>, <c>.schema/</c>, staged <c>.*.mq-*.tmp</c> and backup files
    /// (every dot-named file or folder, which the loader ignores too). A watcher may report paths through the real location of a root
    /// configured through a symbolic link (macOS reports <c>/var/…</c> as <c>/private/var/…</c>), so <paramref name="realRoot"/> is tried too.
    /// </summary>
    /// <param name="root">The model root as configured.</param>
    /// <param name="fullPath">The event's path.</param>
    /// <param name="realRoot">The model root with symbolic links resolved, when it differs.</param>
    /// <returns>The model-relative path, or <see langword="null"/>.</returns>
    public static string? Relevant(string root, string fullPath, string? realRoot = null)
    {
        var relative = Under(root, fullPath) ?? (realRoot is null ? null : Under(realRoot, fullPath));
        if (relative is null)
            return null;
        var segments = relative.Split('/');
        if (segments.Any(s => s.Length == 0 || s[0] == '.') || segments[0] is "manifest" or "snapshots")
            return null;
        if (relative.EndsWith(".bak", StringComparison.Ordinal) || relative.EndsWith(".tmp", StringComparison.Ordinal))
            return null;
        return relative;
    }

    /// <summary>Resolves every symbolic link along a directory path; the full path when the disk cannot be read.</summary>
    /// <param name="path">A directory path.</param>
    /// <returns>The link-free path, without a trailing separator.</returns>
    public static string ResolveLinks(string path) => ResolveLinks(path, 0);

    private static string ResolveLinks(string path, int depth)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            var current = Path.GetPathRoot(full) ?? "";
            foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (new DirectoryInfo(current) is { LinkTarget: not null } link && link.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    // The target may itself run through a link (on macOS a link under /var resolves to a /var/... target that
                    // still passes through /private/var), so resolve it again; the depth cap guards against cycles.
                    current = depth < 32 ? ResolveLinks(target.FullName, depth + 1) : target.FullName;
                }
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    private static string? Under(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return relative == "." || relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative) ? null : relative;
    }

    private static FileSystemWatcher? Start(string root, ChannelWriter<string> paths, EditorEvents events, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(root);
            var realRoot = ResolveLinks(root);
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            void Report(string? path)
            {
                if (path is not null && Relevant(root, path, realRoot) is { } relative)
                    paths.TryWrite(relative);
            }

            watcher.Changed += (_, e) => Report(e.FullPath);
            watcher.Created += (_, e) => Report(e.FullPath);
            watcher.Deleted += (_, e) => Report(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Report(e.OldFullPath);
                Report(e.FullPath);
            };
            watcher.Error += (_, _) => paths.TryWrite(OverflowMarker);
            watcher.EnableRaisingEvents = true;
            events.Watcher = "watching";
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            events.Watcher = "polling";
            logger.LogWarning(ex, "maquettiste: cannot watch {Root}; the 30 s rescan keeps the index current.", root);
            return null;
        }
    }

    private const string OverflowMarker = "\u0000overflow";

    private static async Task<(HashSet<string> Paths, bool Overflowed)> CollectAsync(ChannelReader<string> reader, TimeSpan firstWait, CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var overflowed = false;
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            if (firstWait != Timeout.InfiniteTimeSpan)
                first.CancelAfter(firstWait);
            try
            {
                if (!await reader.WaitToReadAsync(first.Token).ConfigureAwait(false))
                    return (paths, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (paths, false);
            }
        }

        while (true)
        {
            while (reader.TryRead(out var path))
            {
                if (path == OverflowMarker)
                    overflowed = true;
                else
                    paths.Add(path);
            }

            // Quiet period: a burst (a checkout, a save of several files) becomes one refresh.
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(Quiet);
            try
            {
                if (!await reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }

        return (paths, overflowed);
    }
}
