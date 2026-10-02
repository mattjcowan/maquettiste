namespace Maquettiste.Engine;

/// <summary>
/// What the served checkout is called, so two editors or servers running side by side can be told apart. Read from the files git keeps
/// (<c>.git/HEAD</c>, or a linked worktree's <c>.git</c> file) without running git; nothing is cached or watched.
/// </summary>
/// <param name="Workspace">The name to show: <see cref="Variable"/> when set, else <see cref="Branch"/>, else the short commit of a
/// detached head, else <see cref="Worktree"/>; <see langword="null"/> when none is known.</param>
/// <param name="Branch">The checked-out branch, or <see langword="null"/> on a detached head, outside git, or when the linked worktree's
/// git folder is out of reach (a container that mounts only the worktree).</param>
/// <param name="Worktree">The name of the linked worktree (the last segment of the git folder its <c>.git</c> file names), or
/// <see langword="null"/> for a main checkout.</param>
/// <param name="Repository">The folder name of the main checkout a linked worktree belongs to, as its <c>.git</c> file names it, or
/// <see langword="null"/>.</param>
public sealed record WorkspaceInfo(string? Workspace, string? Branch, string? Worktree, string? Repository)
{
    /// <summary>The environment variable that names the workspace, overriding what git says.</summary>
    public const string Variable = "MAQUETTISTE_WORKSPACE";

    /// <summary>
    /// Finds the nearest <c>.git</c> at or above <paramref name="repoRoot"/>: a folder (a main checkout) gives the branch from its
    /// <c>HEAD</c>; a file (a linked worktree, <c>gitdir: &lt;repo&gt;/.git/worktrees/&lt;name&gt;</c>) gives the worktree's name and
    /// the main checkout's folder name, and the branch when that git folder can be read.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="configured">The value of <see cref="Variable"/>; blank means unset.</param>
    /// <returns>The workspace.</returns>
    public static WorkspaceInfo Detect(string repoRoot, string? configured)
    {
        ArgumentNullException.ThrowIfNull(repoRoot);
        var name = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        string? branch = null, detached = null, worktree = null, repository = null;
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(repoRoot)); dir is not null; dir = dir.Parent)
            {
                var dotGit = Path.Combine(dir.FullName, ".git");
                string? gitDir = null;
                if (Directory.Exists(dotGit))
                    gitDir = dotGit;
                else if (File.Exists(dotGit))
                {
                    var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim() ?? "";
                    if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                        break;
                    var target = line["gitdir:".Length..].Trim();
                    var segments = target.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length >= 3 && segments[^2] == "worktrees")
                    {
                        worktree = segments[^1];
                        repository = segments[^3] == ".git"
                            ? segments.Length >= 4 ? segments[^4] : null
                            : segments[^3].EndsWith(".git", StringComparison.Ordinal) ? segments[^3][..^4] : segments[^3];
                    }

                    gitDir = Path.IsPathFullyQualified(target) ? target : Path.GetFullPath(Path.Combine(dir.FullName, target));
                }
                else
                    continue;

                var head = Path.Combine(gitDir, "HEAD");
                var text = File.Exists(head) ? File.ReadLines(head).FirstOrDefault()?.Trim() ?? "" : "";
                if (text.StartsWith("ref:", StringComparison.Ordinal))
                {
                    var reference = text["ref:".Length..].Trim();
                    branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
                }
                else if (text.Length >= 7 && text.All(char.IsAsciiHexDigit))
                    detached = text[..7];
                break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable .git says nothing; what was found so far stands.
        }

        return new WorkspaceInfo(name ?? NullIfEmpty(branch) ?? detached ?? worktree, NullIfEmpty(branch), worktree, NullIfEmpty(repository));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
