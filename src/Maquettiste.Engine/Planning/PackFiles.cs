using Maquettiste.Engine.Hashing;

namespace Maquettiste.Engine.Planning;

/// <summary>Pack-relative file paths: confinement to the pack folder and content hashes.</summary>
internal static class PackFiles
{
    /// <summary>
    /// Resolves a pack-relative path (with <c>/</c> separators) to an absolute path inside the pack folder, or returns
    /// <see langword="null"/> when it is empty, rooted, has an empty, <c>.</c> or <c>..</c> segment, a backslash, or leaves the folder.
    /// </summary>
    /// <param name="packRoot">The absolute pack folder.</param>
    /// <param name="relativePath">The pack-relative path.</param>
    /// <returns>The absolute path, or <see langword="null"/>.</returns>
    public static string? Resolve(string packRoot, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal) || relativePath.StartsWith('/')
            || Path.IsPathRooted(relativePath) || relativePath.Contains(':', StringComparison.Ordinal))
            return null;
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
                return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packRoot));
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Returns the content hash of a pack file, or <see langword="null"/> when it is missing or outside the pack.</summary>
    /// <param name="packRoot">The absolute pack folder.</param>
    /// <param name="relativePath">The pack-relative path.</param>
    /// <returns>The hash, or <see langword="null"/>.</returns>
    public static string? Hash(string packRoot, string relativePath)
    {
        var full = Resolve(packRoot, relativePath);
        if (full is null || !File.Exists(full))
            return null;
        try
        {
            return ContentHash.Of(File.ReadAllBytes(full));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
