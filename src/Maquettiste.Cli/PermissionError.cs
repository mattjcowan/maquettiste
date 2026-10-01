using System.Text.RegularExpressions;

namespace Maquettiste.Cli;

/// <summary>
/// A write the operating system refused (the run lock, the cache, an output or model file): one line naming the path instead of
/// a stack trace (exit 1). The usual cause is a container that runs as another user than the one owning the mounted repo, so on
/// Linux the line suggests <c>--user 0:0</c>, with which the image's entrypoint repairs the files another user owns and runs the
/// command as the owner of the repo.
/// </summary>
internal static partial class PermissionError
{
    /// <summary>The permission failure in an exception, its inner exceptions or an aggregate's, if any.</summary>
    /// <param name="e">The exception.</param>
    /// <returns>The permission failure, or <see langword="null"/>.</returns>
    public static UnauthorizedAccessException? Find(Exception? e)
    {
        for (var guard = 0; e is not null && guard < 32; guard++)
        {
            switch (e)
            {
                case UnauthorizedAccessException denied:
                    return denied;
                case AggregateException aggregate:
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        if (Find(inner) is { } found)
                            return found;
                    }

                    return null;
            }

            e = e.InnerException;
        }

        return null;
    }

    /// <summary>The line for a refused write.</summary>
    /// <param name="e">The failure.</param>
    /// <param name="linux">Whether to suggest the container user option.</param>
    /// <returns>One line.</returns>
    public static string Message(UnauthorizedAccessException e, bool linux)
    {
        ArgumentNullException.ThrowIfNull(e);
        var match = QuotedPath().Match(e.Message);
        var path = match.Success ? Target(match.Groups[1].Value) : null;
        var what = path is null ? "a file or folder" : path;
        var hint = linux
            ? " Check that the user running maquettiste can write there; in the maquettiste image, start the container with --user 0:0 so it repairs the files another user owns and runs as the owner of the repo."
            : " Check that the user running maquettiste can write there.";
        return $"maquettiste: permission denied: cannot write {what}.{hint}";
    }

    /// <summary>The file a write was for: the engine writes <c>.name.mq-&lt;id&gt;-&lt;n&gt;.tmp</c> beside it, then renames.</summary>
    /// <param name="path">The refused path.</param>
    /// <returns>The path to name.</returns>
    internal static string Target(string path)
    {
        var name = Path.GetFileName(path);
        var temp = TempName().Match(name);
        return temp.Success ? Path.Combine(Path.GetDirectoryName(path) ?? "", temp.Groups[1].Value) : path;
    }

    [GeneratedRegex(@"^\.(.+)\.mq-[0-9A-Za-z]+-\d+\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex TempName();

    [GeneratedRegex("'([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPath();
}
