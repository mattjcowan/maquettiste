namespace Maquettiste.Testing;

/// <summary>Thrown when an output tree differs from its golden tree.</summary>
/// <param name="message">A description of every difference.</param>
public sealed class GoldenMismatchException(string message) : Exception(message);

/// <summary>
/// Compares an output folder with a golden folder byte for byte. With <c>MAQUETTISTE_UPDATE_GOLDEN=1</c> (read only by tests)
/// the golden folder is replaced by the output instead.
/// </summary>
public static class Golden
{
    /// <summary>The environment variable that switches to update mode.</summary>
    public const string UpdateVariable = "MAQUETTISTE_UPDATE_GOLDEN";

    /// <summary>Lists the differences between two trees: missing, extra and changed files, ordinal by path.</summary>
    /// <param name="expectedDirectory">The golden folder.</param>
    /// <param name="actualDirectory">The output folder.</param>
    /// <returns>One line per difference.</returns>
    public static IReadOnlyList<string> Compare(string expectedDirectory, string actualDirectory)
    {
        var expected = Files(expectedDirectory);
        var actual = Files(actualDirectory);
        var differences = new List<string>();
        foreach (var path in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(path, out var a))
                differences.Add("missing: " + path);
            else if (!expected.TryGetValue(path, out var e))
                differences.Add("extra:   " + path);
            else if (!File.ReadAllBytes(e).AsSpan().SequenceEqual(File.ReadAllBytes(a)))
                differences.Add("changed: " + path);
        }

        return differences;
    }

    /// <summary>Asserts that two trees are identical, or updates the golden tree in update mode.</summary>
    /// <param name="expectedDirectory">The golden folder.</param>
    /// <param name="actualDirectory">The output folder.</param>
    /// <exception cref="GoldenMismatchException">The trees differ.</exception>
    public static void AssertMatches(string expectedDirectory, string actualDirectory)
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            if (Directory.Exists(expectedDirectory))
                Directory.Delete(expectedDirectory, recursive: true);
            CopyTree(actualDirectory, expectedDirectory);
            return;
        }

        var differences = Compare(expectedDirectory, actualDirectory);
        if (differences.Count > 0)
            throw new GoldenMismatchException($"Output differs from {expectedDirectory} (set {UpdateVariable}=1 to update):\n" + string.Join('\n', differences));
    }

    private static Dictionary<string, string> Files(string root) =>
        !Directory.Exists(root)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'), p => p, StringComparer.Ordinal);

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
