using System.Text;
using Maquettiste.Engine;

namespace Maquettiste.Testing;

/// <summary>
/// A temporary repo root with a <c>.maquettiste/</c> folder and a separate cache folder, deleted on dispose. Real folders, so
/// path policy and symlink tests run against the real file system (D27).
/// </summary>
public sealed class TempRepo : IDisposable
{
    /// <summary>Creates the folders.</summary>
    /// <param name="idGenerator">The id generator for <see cref="Options"/>; <see langword="null"/> uses a <see cref="SequentialIdGenerator"/>.</param>
    public TempRepo(IIdGenerator? idGenerator = null)
    {
        Root = System.IO.Path.Combine(RealPath(System.IO.Path.GetTempPath()), "maquettiste-tests", Guid.NewGuid().ToString("N"));
        RepoRoot = System.IO.Path.Combine(Root, "repo");
        ModelRoot = System.IO.Path.Combine(RepoRoot, ".maquettiste");
        CacheDirectory = System.IO.Path.Combine(Root, "cache");
        Directory.CreateDirectory(ModelRoot);
        Directory.CreateDirectory(CacheDirectory);
        Options = new EngineOptions
        {
            RepoRoot = RepoRoot,
            CacheDirectory = CacheDirectory,
            IdGenerator = idGenerator ?? new SequentialIdGenerator(),
            MaxDegreeOfParallelism = 2,
        };
    }

    /// <summary>
    /// A path with every directory link resolved. macOS's temp folder lies under <c>/var</c>, a link to <c>/private/var</c>;
    /// the tools a test runs there (a solution restore and build) see the folder through the link and through its target,
    /// and a project reached by two paths is restored twice (<c>nuget.g.props already exists</c>) and built astray.
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <returns>The path with links resolved.</returns>
    public static string RealPath(string path)
    {
        var root = System.IO.Path.GetPathRoot(path) ?? path;
        var current = root;
        foreach (var part in path[root.Length..].Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = target.FullName;
        }

        return current;
    }

    /// <summary>The folder that holds the repo and the cache.</summary>
    public string Root { get; }

    /// <summary>The repo root.</summary>
    public string RepoRoot { get; }

    /// <summary>The model root (<c>&lt;RepoRoot&gt;/.maquettiste</c>).</summary>
    public string ModelRoot { get; }

    /// <summary>The cache folder, outside the repo.</summary>
    public string CacheDirectory { get; }

    /// <summary>Engine options over these folders.</summary>
    public EngineOptions Options { get; }

    /// <summary>Returns the absolute path of a repo-relative path.</summary>
    /// <param name="repoRelativePath">A path with <c>/</c> separators.</param>
    /// <returns>The absolute path.</returns>
    public string PathOf(string repoRelativePath) => System.IO.Path.Combine(RepoRoot, repoRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Writes a UTF-8 file (no BOM), creating folders.</summary>
    /// <param name="repoRelativePath">The repo-relative path.</param>
    /// <param name="text">The content.</param>
    public void WriteFile(string repoRelativePath, string text) => WriteBytes(repoRelativePath, new UTF8Encoding(false).GetBytes(text));

    /// <summary>Writes a file, creating folders.</summary>
    /// <param name="repoRelativePath">The repo-relative path.</param>
    /// <param name="bytes">The content.</param>
    public void WriteBytes(string repoRelativePath, byte[] bytes)
    {
        var path = PathOf(repoRelativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Reads a file as UTF-8.</summary>
    /// <param name="repoRelativePath">The repo-relative path.</param>
    /// <returns>The text.</returns>
    public string ReadFile(string repoRelativePath) => File.ReadAllText(PathOf(repoRelativePath), Encoding.UTF8);

    /// <summary>Whether a file exists.</summary>
    /// <param name="repoRelativePath">The repo-relative path.</param>
    /// <returns><see langword="true"/> when it exists.</returns>
    public bool Exists(string repoRelativePath) => File.Exists(PathOf(repoRelativePath));

    /// <summary>Lists every file under the repo root, repo-relative with <c>/</c> separators, ordinal.</summary>
    /// <returns>The paths.</returns>
    public IReadOnlyList<string> ListFiles() =>
        [.. Directory.EnumerateFiles(RepoRoot, "*", SearchOption.AllDirectories)
            .Select(p => System.IO.Path.GetRelativePath(RepoRoot, p).Replace(System.IO.Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a locked file on some platforms must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
