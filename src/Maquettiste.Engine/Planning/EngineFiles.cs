using System.Globalization;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// Guarded, atomic engine writes for W6's stores (unit state, plans, jobs): every target, temporary file and deletion is checked
/// with <see cref="IOutputPathPolicy.CheckEngineWrite"/> first (D40), a refusal throws <see cref="UnauthorizedAccessException"/> and
/// nothing is written, and a file is replaced by writing a temporary file in the same folder and renaming it over the target.
/// </summary>
/// <param name="paths">The engine-write guard.</param>
/// <param name="target">The write target of every path.</param>
/// <param name="options">The engine options; given, a write under the engine's own folder first makes the folder ignore itself (<see cref="Writing.CacheFolder"/>).</param>
internal sealed class EngineFiles(IOutputPathPolicy paths, WriteTarget target, EngineOptions? options = null)
{
    private long _counter;

    /// <summary>Writes a file atomically.</summary>
    /// <param name="fullPath">The absolute target path.</param>
    /// <param name="bytes">The content.</param>
    /// <param name="ct">Cancellation, observed before the rename.</param>
    /// <returns>A task.</returns>
    public async Task WriteAsync(string fullPath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        Check(fullPath);
        var folder = Path.GetDirectoryName(fullPath)!;
        var temp = Path.Combine(folder, "." + Path.GetFileName(fullPath) + ".mq-"
            + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-"
            + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture) + ".tmp");
        Check(temp);
        if (options is not null)
            Writing.CacheFolder.EnsureIgnored(options, paths, folder);
        Directory.CreateDirectory(folder);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Deletes a file if it exists.</summary>
    /// <param name="fullPath">The absolute path.</param>
    public void Delete(string fullPath)
    {
        if (!File.Exists(fullPath))
            return;
        Check(fullPath);
        File.Delete(fullPath);
    }

    /// <summary>Deletes a folder and everything in it, if it exists.</summary>
    /// <param name="fullPath">The absolute path.</param>
    public void DeleteFolder(string fullPath)
    {
        if (!Directory.Exists(fullPath))
            return;
        Check(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    /// <summary>Throws when the guard refuses a path.</summary>
    /// <param name="fullPath">The absolute path.</param>
    public void Check(string fullPath)
    {
        var check = paths.CheckEngineWrite(target, fullPath);
        if (!check.Allowed)
            throw new UnauthorizedAccessException($"Engine write refused ({check.RuleId}): {fullPath}: {check.Reason}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
