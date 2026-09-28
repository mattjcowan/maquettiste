namespace Maquettiste.Testing;

/// <summary>Locates the repository and <c>tests/fixtures</c> from a test's output folder.</summary>
public static class Fixtures
{
    /// <summary>The repository root: the nearest ancestor of the test assembly's folder that holds <c>maquettiste.slnx</c>.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>The <c>tests/fixtures</c> folder.</summary>
    public static string Root => System.IO.Path.Combine(RepoRoot, "tests", "fixtures");

    /// <summary>Returns a path under <c>tests/fixtures</c>.</summary>
    /// <param name="parts">Path segments.</param>
    /// <returns>The absolute path.</returns>
    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    /// <summary>Reads a fixture file's bytes.</summary>
    /// <param name="parts">Path segments under <c>tests/fixtures</c>.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ReadBytes(params string[] parts) => File.ReadAllBytes(Path(parts));

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "maquettiste.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not find maquettiste.slnx above " + AppContext.BaseDirectory);
    }
}
