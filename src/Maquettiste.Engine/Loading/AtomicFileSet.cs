using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Loading;

/// <summary>A refused or failed multi-file write. Nothing was changed on disk.</summary>
/// <param name="Path">The absolute path concerned.</param>
/// <param name="Reason">Why.</param>
/// <param name="Refused">Whether the path policy refused it (as opposed to an I/O failure).</param>
internal sealed record FileSetFailure(string Path, string Reason, bool Refused);

/// <summary>
/// Applies a set of model file writes and deletes all or nothing (S16 write safety; host-contracts requirement 15): every path is
/// checked with <see cref="IOutputPathPolicy.CheckEngineWrite"/> (<see cref="WriteTarget.Model"/>) first; every new file is staged
/// as <c>.&lt;name&gt;.mq-&lt;batchId&gt;.tmp</c> in its target folder and every file about to be replaced or deleted is copied to
/// <c>.&lt;name&gt;.mq-&lt;batchId&gt;.bak</c> beside it; only then are the temp files renamed into place and the deletes done. A
/// failure while staging removes the staged files; a failure while renaming restores every file already replaced or deleted from its
/// copy. Either way the model folder is left as it was. A staged name that already exists is a leftover of a process that died
/// mid-write (batch ids restart with the process, and a container often gets the same process id back), so it is overwritten
/// rather than failing every later save that happens to reuse the name.
/// </summary>
/// <param name="paths">The engine-write guard.</param>
/// <param name="modelRoot">The absolute model root; emptied folders below it are removed after deletes.</param>
/// <param name="onStaged">Called with each target once its file is staged (tests use it to cancel midway); <see langword="null"/> in production.</param>
internal sealed class AtomicFileSet(IOutputPathPolicy paths, string modelRoot, Action<string>? onStaged = null)
{
    /// <summary>Applies the writes and deletes.</summary>
    /// <param name="writes">Absolute paths and their new bytes.</param>
    /// <param name="deletes">Absolute paths to delete (missing files are ignored).</param>
    /// <param name="batchId">A token that makes staged file names unique.</param>
    /// <param name="ct">Cancellation, observed until the first rename; after that the set completes or rolls back.</param>
    /// <returns><see langword="null"/> on success, else the failure.</returns>
    public async Task<FileSetFailure?> ApplyAsync(IReadOnlyList<(string Path, byte[] Bytes)> writes, IReadOnlyList<string> deletes, string batchId, CancellationToken ct)
    {
        var ordered = writes.OrderBy(w => w.Path, StringComparer.Ordinal).ToList();
        var toDelete = deletes.Where(d => !ordered.Any(w => string.Equals(w.Path, d, StringComparison.Ordinal))).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();

        var staged = new List<Staged>();
        foreach (var (path, bytes) in ordered)
            staged.Add(new Staged(path, Sibling(path, batchId, "tmp"), Sibling(path, batchId, "bak"), bytes));
        var removals = toDelete.Select(p => new Staged(p, null, Sibling(p, batchId, "bak"), null)).ToList();

        // 1. Policy: every file this set creates, replaces, stages or deletes.
        foreach (var item in staged.Concat(removals))
        {
            foreach (var path in new[] { item.Target, item.Temp, item.Backup })
            {
                if (path is null)
                    continue;
                var check = paths.CheckEngineWrite(WriteTarget.Model, path);
                if (!check.Allowed)
                    return new FileSetFailure(item.Target, check.Reason ?? "The path policy refused the write.", true);
            }
        }

        // 2. Stage: temp files for new content, copies of everything about to be replaced or deleted.
        var current = "";
        try
        {
            foreach (var item in staged)
            {
                ct.ThrowIfCancellationRequested();
                current = item.Target;
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                await using (var stream = new FileStream(item.Temp!, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await stream.WriteAsync(item.Bytes!, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                item.TempWritten = true;
                if (File.Exists(item.Target))
                {
                    File.Copy(item.Target, item.Backup, overwrite: true);
                    item.BackedUp = true;
                }

                onStaged?.Invoke(item.Target);
            }

            foreach (var item in removals)
            {
                ct.ThrowIfCancellationRequested();
                current = item.Target;
                if (!File.Exists(item.Target))
                    continue;
                File.Copy(item.Target, item.Backup, overwrite: true);
                item.BackedUp = true;
                onStaged?.Invoke(item.Target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Cleanup(staged, removals);
            if (ex is OperationCanceledException)
                throw;
            return new FileSetFailure(current, "Staging failed: " + ex.Message, false);
        }

        // 3. Commit: renames, then deletes; roll back everything done so far on any failure.
        var done = new List<Staged>();
        try
        {
            foreach (var item in staged)
            {
                File.Move(item.Temp!, item.Target, overwrite: true);
                item.TempWritten = false;
                done.Add(item);
            }

            foreach (var item in removals)
            {
                if (!item.BackedUp)
                    continue;
                File.Delete(item.Target);
                done.Add(item);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failed = staged.Concat(removals).FirstOrDefault(s => !done.Contains(s))?.Target ?? "";
            Rollback(done);
            Cleanup(staged, removals);
            return new FileSetFailure(failed, "Rename failed and every change was rolled back: " + ex.Message, false);
        }

        Cleanup(staged, removals);
        foreach (var item in removals)
            RemoveEmptyFolders(Path.GetDirectoryName(item.Target)!);
        return null;
    }

    /// <summary>Whether a file name is one of the staged names this class creates (<c>.&lt;name&gt;.mq-&lt;batchId&gt;.tmp</c> or <c>.bak</c>).</summary>
    /// <param name="fileName">A file name without folder.</param>
    /// <returns><see langword="true"/> for a staged temp file or backup.</returns>
    public static bool IsStagedName(string fileName) =>
        fileName.StartsWith('.')
        && fileName.Contains(".mq-", StringComparison.Ordinal)
        && (fileName.EndsWith(".tmp", StringComparison.Ordinal) || fileName.EndsWith(".bak", StringComparison.Ordinal));

    private static string Sibling(string path, string batchId, string extension) =>
        Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".mq-" + batchId + "." + extension);

    private static void Rollback(List<Staged> done)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var item = done[i];
            try
            {
                if (item.BackedUp)
                    File.Copy(item.Backup, item.Target, overwrite: true);
                else
                    File.Delete(item.Target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the copy stays beside the target, so nothing is lost.
                item.BackedUp = false;
            }
        }
    }

    private static void Cleanup(List<Staged> staged, List<Staged> removals)
    {
        foreach (var item in staged.Concat(removals))
        {
            if (item.Temp is not null && item.TempWritten)
                TryDelete(item.Temp);
            if (item.BackedUp)
                TryDelete(item.Backup);
        }
    }

    private void RemoveEmptyFolders(string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelRoot));
        var model = Path.Combine(root, "model");
        var current = Path.TrimEndingDirectorySeparator(folder);
        while (current.StartsWith(model + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            try
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                    return;
                if (!paths.CheckEngineWrite(WriteTarget.Model, current).Allowed)
                    return;
                Directory.Delete(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Staged(string target, string? temp, string backup, byte[]? bytes)
    {
        public string Target { get; } = target;
        public string? Temp { get; } = temp;
        public string Backup { get; } = backup;
        public byte[]? Bytes { get; } = bytes;
        public bool TempWritten { get; set; }
        public bool BackedUp { get; set; }
    }
}
