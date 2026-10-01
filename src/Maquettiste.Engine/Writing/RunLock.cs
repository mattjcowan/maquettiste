using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// The exclusive run lock <c>JournalDirectory/run.lock</c> (W7; engine-design.md section 12.4; host-contracts 28): the file is opened
/// with <see cref="FileShare.None"/>, which the runtime maps to an OS lock (<c>flock</c> on Unix, a share mode on Windows), so a second
/// holder in this or another process is refused until the handle is released. The file is kept between runs.
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="paths">The engine-write guard (<see cref="WriteTarget.Cache"/>).</param>
internal sealed class RunLock(EngineOptions options, IOutputPathPolicy paths) : IRunLock
{
    /// <summary>The poll interval while waiting.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>The lock file.</summary>
    internal string FilePath => Path.Combine(options.EffectiveJournalDirectory, "run.lock");

    /// <inheritdoc/>
    public async Task<IAsyncDisposable?> AcquireAsync(bool wait, CancellationToken ct)
    {
        var file = FilePath;
        var check = paths.CheckEngineWrite(WriteTarget.Cache, file);
        if (!check.Allowed)
            throw new UnauthorizedAccessException($"{check.RuleId}: run lock refused for {file}: {check.Reason}");
        CacheFolder.EnsureIgnored(options, paths, file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
                return new Handle(stream);
            }
            catch (IOException)
            {
                // Held by another run (a sharing violation, or EWOULDBLOCK from flock). A cancellation that
                // lands while the open attempt fails must still surface as cancellation, not as the I/O error.
                ct.ThrowIfCancellationRequested();
                if (!wait)
                    return null;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private sealed class Handle(FileStream stream) : IAsyncDisposable
    {
        private FileStream? _stream = stream;

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _stream, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
