using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// The debounced watch hook behind <c>generate --watch</c> (SPEC section 13 "watch mode"; engine-design.md section 16): a file
/// watcher (the CLI's <c>FileSystemWatcher</c>, or the functions' watcher) calls <see cref="Notify"/> with changed paths; once no
/// notification has arrived for the debounce interval, the watcher refreshes the store with the paths (<see cref="ModelStore.RefreshAsync"/>)
/// and runs one incremental generation. Notifications during a run are collected and trigger one more run after it. Engine-owned
/// paths (the cache and journal, manifests, snapshots, <c>.schema</c>, staged temporary files) are ignored, so a run's own writes
/// never trigger the next run. <see cref="Attach"/> also subscribes to a store's change sets (editor saves).
/// </summary>
internal sealed class GenerationWatcher
{
    private readonly GenerationService _generation;
    private readonly GenerationRequest _request;
    private readonly TimeSpan _debounce;
    private readonly Func<GenerationResult, CancellationToken, ValueTask>? _onRun;
    private readonly IProgress<ProgressUpdate>? _progress;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly string _modelRoot;
    private readonly string _repoRoot;
    private HashSet<string> _paths = new(StringComparer.Ordinal);
    private bool _pending;
    private long _version;

    /// <summary>Creates a watcher.</summary>
    /// <param name="generation">The generation service.</param>
    /// <param name="request">The request each run uses (normally an incremental apply).</param>
    /// <param name="debounce">The quiet interval (the CLI uses 250 ms).</param>
    /// <param name="onRun">Called after each run.</param>
    /// <param name="progress">Progress for each run.</param>
    public GenerationWatcher(GenerationService generation, GenerationRequest request, TimeSpan debounce,
        Func<GenerationResult, CancellationToken, ValueTask>? onRun = null, IProgress<ProgressUpdate>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(debounce, TimeSpan.Zero);
        _generation = generation;
        _request = request;
        _debounce = debounce;
        _onRun = onRun;
        _progress = progress;
        var options = generation.Services.Options;
        _repoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RepoRoot));
        _modelRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.EffectiveModelRoot));
    }

    /// <summary>Runs completed so far.</summary>
    public int Runs { get; private set; }

    /// <summary>Reports changed paths (absolute or repo-relative); engine-owned paths are ignored.</summary>
    /// <param name="paths">The paths.</param>
    public void Notify(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var any = false;
        lock (_gate)
        {
            foreach (var path in paths)
            {
                if (IsEngineOwned(path))
                    continue;
                _paths.Add(path);
                any = true;
            }
        }

        if (any)
            Trigger();
    }

    /// <summary>Asks for a debounced run without paths to refresh (the store already knows the change, as after an editor save).</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            _version++;
            if (_pending)
                return;
            _pending = true;
        }

        _signal.Release();
    }

    /// <summary>Subscribes to a store's change sets (saves from the editor): each non-empty one triggers a debounced run.</summary>
    /// <param name="store">The store.</param>
    /// <returns>A handle that unsubscribes.</returns>
    public IDisposable Attach(ModelStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.OnChanged((changes, _) =>
        {
            // Disk change sets come from refreshes and rescans, which the watcher and the runs themselves cause.
            if (!changes.IsEmpty && changes.Source != ChangeSource.Disk)
                Trigger();
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Waits for notifications and runs, until cancelled.</summary>
    /// <param name="ct">Stops the loop; a run in progress is cancelled.</param>
    /// <returns>A task that completes when stopped.</returns>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                // Debounce: wait until no notification arrived for a whole interval.
                while (true)
                {
                    long seen;
                    lock (_gate)
                        seen = _version;
                    await Task.Delay(_debounce, ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_version == seen)
                            break;
                    }
                }

                HashSet<string> paths;
                lock (_gate)
                {
                    paths = _paths;
                    _paths = new HashSet<string>(StringComparer.Ordinal);
                    _pending = false;
                }

                if (paths.Count > 0)
                    await _generation.Store.RefreshAsync([.. paths.Order(StringComparer.Ordinal)], ct).ConfigureAwait(false);
                var result = await _generation.RunAsync(_request, _progress, ct).ConfigureAwait(false);
                Runs++;
                if (_onRun is not null)
                    await _onRun(result, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Whether a path is one the engine writes itself.</summary>
    /// <param name="path">An absolute or repo-relative path.</param>
    /// <returns><see langword="true"/> to ignore it.</returns>
    internal bool IsEngineOwned(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_repoRoot, path));
        var name = Path.GetFileName(full);
        if (name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal))
            return true;
        if (!full.StartsWith(_modelRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;
        var relative = full[(_modelRoot.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith(".cache/", StringComparison.Ordinal) || relative.StartsWith(".schema/", StringComparison.Ordinal)
            || relative.StartsWith("manifest/", StringComparison.Ordinal) || relative.StartsWith("snapshots/", StringComparison.Ordinal);
    }
}
