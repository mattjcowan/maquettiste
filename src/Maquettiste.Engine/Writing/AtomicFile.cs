namespace Maquettiste.Engine.Writing;

/// <summary>Staged writes: a temporary file beside the target, then a rename over it (engine-design.md section 12.4).</summary>
internal static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="content"/> to <c>&lt;dir&gt;/.&lt;name&gt;.mq-&lt;tag&gt;.tmp</c> and renames it over
    /// <paramref name="fullPath"/>. The folder is created first. On any failure the temporary file is removed and the target is
    /// left as it was. The caller has already checked the path.
    /// </summary>
    /// <param name="fullPath">The target.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="tag">A tag unique among concurrent writes to the folder.</param>
    /// <param name="ct">Cancellation, observed before the write starts only.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(string fullPath, ReadOnlyMemory<byte> content, string tag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folder = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        var temp = TempPath(fullPath, tag);
        try
        {
            var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = 0,
            });
            await using (stream.ConfigureAwait(false))
            {
                // The file is finished once started, so a cancellation never leaves half a file behind.
                await stream.WriteAsync(content, CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>The temporary file name for a target.</summary>
    /// <param name="fullPath">The target.</param>
    /// <param name="tag">The tag.</param>
    /// <returns>The temporary path.</returns>
    public static string TempPath(string fullPath, string tag) =>
        Path.Combine(Path.GetDirectoryName(fullPath)!, "." + Path.GetFileName(fullPath) + ".mq-" + tag + ".tmp");

    /// <summary>Deletes a file, ignoring failures.</summary>
    /// <param name="path">The file.</param>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reads a file, or returns <see langword="null"/> when it does not exist.</summary>
    /// <param name="fullPath">The file.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bytes.</returns>
    public static async Task<byte[]?> ReadIfExistsAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
