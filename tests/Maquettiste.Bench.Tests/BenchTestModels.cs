using Maquettiste.Engine;

namespace Maquettiste.Bench.Tests;

/// <summary>Small synthetic models and temporary folders for the bench tests.</summary>
internal static class BenchTestModels
{
    /// <summary>A small model: 5 packages, 60 entities, 150 relations, 3 fanout units per entity.</summary>
    /// <param name="seed">The seed.</param>
    /// <returns>The options.</returns>
    public static SyntheticModelOptions Small(int seed = 7) => new()
    {
        Seed = seed,
        Packages = 5,
        Entities = 60,
        Relations = 150,
        Enums = 8,
        ValueObjects = 5,
        ScalarTypes = 3,
        Fanout = 3,
    };

    /// <summary>Lists every file under a folder, relative with <c>/</c>, ordinal.</summary>
    /// <param name="root">The folder.</param>
    /// <returns>The paths.</returns>
    public static IReadOnlyList<string> Files(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];
}

/// <summary>A temporary folder deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Path = Directory.CreateTempSubdirectory("maquettiste-bench-tests-").FullName;

    /// <summary>The folder.</summary>
    public string Path { get; }

    /// <summary>Engine options for a repo under this folder.</summary>
    /// <param name="repo">The repo root.</param>
    /// <returns>The options.</returns>
    public EngineOptions Options(string repo) => new()
    {
        RepoRoot = repo,
        CacheDirectory = System.IO.Path.Combine(Path, "cache"),
        MaxDegreeOfParallelism = 2,
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
