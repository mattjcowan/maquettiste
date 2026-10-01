using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli;

/// <summary>What a guarded write did.</summary>
internal enum WriteOutcome
{
    /// <summary>The file did not exist and was written.</summary>
    Created,

    /// <summary>The file existed with other bytes and was replaced.</summary>
    Updated,

    /// <summary>The file already held these bytes; nothing was written.</summary>
    Unchanged,

    /// <summary>The file existed and was kept (no overwrite).</summary>
    Kept,
}

/// <summary>A write the path policy refused (exit 4).</summary>
/// <param name="message">The policy's reason.</param>
internal sealed class RefusedWriteException(string message) : Exception(message);

/// <summary>
/// Every file and folder the CLI itself creates (<c>init</c>, <c>pack new</c>, <c>validate --output</c>) goes through
/// <see cref="IOutputPathPolicy.CheckEngineWrite"/> first (S23, D40): <see cref="WriteTarget.Model"/> under the model root,
/// <see cref="WriteTarget.Setup"/> for the git hooks, the agent setup and (only with <c>init --gitignore</c>) <c>.gitignore</c>, <see cref="WriteTarget.Output"/> for a report file.
/// </summary>
/// <param name="policy">The policy.</param>
internal sealed class GuardedFiles(IOutputPathPolicy policy)
{
    /// <summary>Creates a folder (and its parents) after checking it.</summary>
    /// <param name="target">The write target.</param>
    /// <param name="fullPath">The absolute folder path.</param>
    /// <returns><see langword="true"/> when the folder was created.</returns>
    public bool CreateDirectory(WriteTarget target, string fullPath)
    {
        Check(target, fullPath);
        if (Directory.Exists(fullPath))
            return false;
        Directory.CreateDirectory(fullPath);
        return true;
    }

    /// <summary>Writes a file after checking it; the parent folder is created only after the check passed.</summary>
    /// <param name="target">The write target.</param>
    /// <param name="fullPath">The absolute file path.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="overwrite">Whether an existing file with other bytes is replaced.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What happened.</returns>
    public async Task<WriteOutcome> WriteAsync(WriteTarget target, string fullPath, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken ct)
    {
        Check(target, fullPath);
        var exists = File.Exists(fullPath);
        if (exists)
        {
            if (!overwrite)
                return WriteOutcome.Kept;
            var existing = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            if (existing.AsSpan().SequenceEqual(content.Span))
                return WriteOutcome.Unchanged;
        }

        if (Path.GetDirectoryName(fullPath) is { } parent)
            Directory.CreateDirectory(parent);
        await File.WriteAllBytesAsync(fullPath, content, ct).ConfigureAwait(false);
        return exists ? WriteOutcome.Updated : WriteOutcome.Created;
    }

    /// <summary>Deletes a file after checking it.</summary>
    /// <param name="target">The write target.</param>
    /// <param name="fullPath">The absolute file path.</param>
    public void Delete(WriteTarget target, string fullPath)
    {
        Check(target, fullPath);
        File.Delete(fullPath);
    }

    private void Check(WriteTarget target, string fullPath)
    {
        var check = policy.CheckEngineWrite(target, fullPath);
        if (!check.Allowed)
            throw new RefusedWriteException($"Refused to write {fullPath}: {check.Reason} ({check.RuleId}).");
    }
}
