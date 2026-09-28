namespace Maquettiste.Engine.Generation;

/// <summary>
/// Path helpers for file watching. A file-system watcher may report paths through the real location of a root that was
/// configured through a symbolic link (macOS reports <c>/var/…</c> roots as <c>/private/var/…</c>), so reported paths are mapped
/// back onto the configured root before they are compared with it.
/// </summary>
internal static class WatchPaths
{
    /// <summary>Resolves every symbolic link along a directory path; falls back to the full path when the disk cannot be read.</summary>
    /// <param name="path">A directory path.</param>
    /// <returns>The link-free full path, without a trailing separator.</returns>
    public static string ResolveLinks(string path) => ResolveLinks(path, 0);

    private static string ResolveLinks(string path, int depth)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            var root = Path.GetPathRoot(full) ?? string.Empty;
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new DirectoryInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    // The target may itself run through a link (a link under /var resolves to a /var/... target on macOS),
                    // so resolve it again; the depth cap guards against link cycles.
                    current = depth < 32 ? ResolveLinks(target.FullName, depth + 1) : target.FullName;
                }
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    /// <summary>Maps a full path that lies under <paramref name="realRoot"/> onto the configured <paramref name="givenRoot"/>.</summary>
    /// <param name="fullPath">A full path.</param>
    /// <param name="givenRoot">The root as configured, without a trailing separator.</param>
    /// <param name="realRoot">The same root with symbolic links resolved.</param>
    /// <returns>The path under <paramref name="givenRoot"/>, or the input when it does not lie under <paramref name="realRoot"/>.</returns>
    public static string Map(string fullPath, string givenRoot, string realRoot)
    {
        if (string.Equals(givenRoot, realRoot, StringComparison.Ordinal))
            return fullPath;
        if (string.Equals(fullPath, realRoot, StringComparison.Ordinal))
            return givenRoot;
        return fullPath.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? givenRoot + fullPath[realRoot.Length..]
            : fullPath;
    }
}
