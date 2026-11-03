using Maquettiste.Engine;

namespace Maquettiste.Functions;

/// <summary>
/// Detects what the served checkout is called (<see cref="WorkspaceInfo"/>) from the files git keeps (<c>.git/HEAD</c>, or a linked
/// worktree's <c>.git</c> file) without running git; nothing is cached or watched. A host feature: the engine reads no version
/// control, so the editor's host and <c>maquettiste mcp</c> detect the workspace and pass it to the engine
/// (<see cref="AgentToolsOptions.Workspace"/>). Outside a checkout it finds nothing, so it is never a dependency. Base class library
/// only: the command-line tool compiles this same file through a linked <c>Compile</c> item.
/// </summary>
public static class WorkspaceDetection
{
    /// <summary>
    /// Finds the nearest <c>.git</c> at or above <paramref name="repoRoot"/>: a folder (a main checkout) gives the branch from its
    /// <c>HEAD</c>; a file (a linked worktree, <c>gitdir: &lt;repo&gt;/.git/worktrees/&lt;name&gt;</c>) gives the worktree's name and
    /// the main checkout's folder name, and the branch when that git folder can be read.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="configured">The value of <see cref="WorkspaceInfo.Variable"/>; blank means unset.</param>
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
