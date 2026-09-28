using System.Collections.Concurrent;

namespace Maquettiste.Engine.Writing;

/// <summary>Real-path resolution and containment checks for the path policy (engine-design.md section 12.1).</summary>
internal static class FileSystemPaths
{
    private const int MaxLinkHops = 40;

    /// <summary>How absolute paths compare: case-insensitive where the usual file systems are (Windows, macOS).</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether <paramref name="path"/> is <paramref name="parent"/> or lies under it (both absolute and normalized).</summary>
    /// <param name="path">The candidate.</param>
    /// <param name="parent">The folder.</param>
    /// <param name="allowEqual">Whether the folder itself counts.</param>
    /// <returns>Whether it is contained.</returns>
    public static bool IsUnder(string path, string parent, bool allowEqual)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var root = Path.TrimEndingDirectorySeparator(parent);
        if (string.Equals(p, root, Comparison))
            return allowEqual;
        // A file-system root ("/" or "C:\") keeps its separator after trimming.
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// Resolves every symbolic link along an absolute path (the missing tail is appended as written). Reads the file system but
    /// never follows a link more than 40 times.
    /// </summary>
    /// <param name="fullPath">An absolute path.</param>
    /// <param name="cache">Optional cache of existing folders' real paths, for one run.</param>
    /// <returns>The real path.</returns>
    /// <exception cref="IOException">A link cycle.</exception>
    public static string RealPath(string fullPath, ConcurrentDictionary<string, string>? cache = null) => RealPath(fullPath, cache, 0);

    /// <summary>
    /// Returns how an existing repo-relative path is spelled on disk, segment by segment: a segment keeps its own spelling when the
    /// folder holds an entry with exactly that name, and takes the entry's spelling when only an entry differing by case exists and
    /// the file system resolves the requested spelling to it (a case-insensitive disk). Returns <see langword="null"/> when the path
    /// does not exist as written. Lists folders, so callers use it only for the rare paths that differ from a manifest entry by case.
    /// </summary>
    /// <param name="baseFolder">The absolute folder the path is relative to.</param>
    /// <param name="relativePath">The path, with <c>/</c> separators.</param>
    /// <returns>The on-disk spelling, with <c>/</c> separators.</returns>
    public static string? OnDiskSpelling(string baseFolder, string relativePath)
    {
        var current = baseFolder;
        var spelled = new List<string>();
        try
        {
            foreach (var segment in relativePath.Split('/'))
            {
                if (!Directory.Exists(current))
                    return null;
                string? exact = null;
                string? folded = null;
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var name = Path.GetFileName(entry);
                    if (string.Equals(name, segment, StringComparison.Ordinal))
                    {
                        exact = name;
                        break;
                    }

                    if (folded is null && string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                        folded = name;
                }

                var chosen = exact ?? (folded is not null && Path.Exists(Path.Combine(current, segment)) ? folded : null);
                if (chosen is null)
                    return null;
                spelled.Add(chosen);
                current = Path.Combine(current, chosen);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return string.Join('/', spelled);
    }

    private static string RealPath(string fullPath, ConcurrentDictionary<string, string>? cache, int hops)
    {
        if (hops > MaxLinkHops)
            throw new IOException($"Too many levels of symbolic links: {fullPath}");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || full.Length <= root.Length)
            return full;
        if (cache is not null && cache.TryGetValue(full, out var cached))
            return cached;

        var parent = Path.GetDirectoryName(full)!;
        var realParent = RealPath(parent, cache, hops);
        var name = Path.GetFileName(full);
        var candidate = Path.Combine(parent, name);
        string? link = null;
        try
        {
            link = new FileInfo(candidate).LinkTarget;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        string result;
        if (link is not null)
        {
            // A relative link resolves against the folder that holds it, which is realParent.
            var target = Path.IsPathRooted(link) ? link : Path.Combine(realParent, link);
            result = RealPath(target, null, hops + 1);
        }
        else
        {
            result = Path.Combine(realParent, name);
            if (cache is not null && Directory.Exists(candidate))
                cache.TryAdd(full, result);
        }

        return result;
    }
}
