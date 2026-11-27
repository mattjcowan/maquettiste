namespace Maquettiste.Testing;

/// <summary>Locates the repository and <c>tests/fixtures</c> from a test's output folder.</summary>
public static class Fixtures
{
    /// <summary>The repository root: the nearest ancestor of the test assembly's folder that holds <c>maquettiste.slnx</c>.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>The <c>tests/fixtures</c> folder.</summary>
    public static string Root => System.IO.Path.Combine(RepoRoot, "tests", "fixtures");

    /// <summary>
    /// Whether the running test reads the projected copies of the model fixtures: each test project sets it to a check of the running
    /// test class's <see cref="UsesProjectedFixturesAttribute"/>. Temporary: pull request 3 of retiring projection deletes the copies,
    /// this hook and the attribute.
    /// </summary>
    public static Func<bool> UsesProjectedFixtures { get; set; } = () => false;

    /// <summary>
    /// Returns a path under <c>tests/fixtures</c>. For a test whose class has <see cref="UsesProjectedFixturesAttribute"/>,
    /// <c>models/&lt;name&gt;</c> is <c>models/&lt;name&gt;-projected</c> when that copy exists: the fixture as it was before its
    /// projected tables became designed tables with bindings.
    /// </summary>
    /// <param name="parts">Path segments.</param>
    /// <returns>The absolute path.</returns>
    public static string Path(params string[] parts)
    {
        if (parts.Length >= 2 && parts[0] == "models" && UsesProjectedFixtures()
            && Directory.Exists(System.IO.Path.Combine(Root, "models", parts[1] + "-projected")))
            parts = ["models", parts[1] + "-projected", .. parts[2..]];
        return System.IO.Path.Combine([Root, .. parts]);
    }

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
