using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Tests.Writing;

/// <summary>The engine's own folder, <c>.maquettiste/.cache/</c>, ignores itself; the repository's <c>.gitignore</c> is never written.</summary>
public sealed class CacheFolderTests
{
    private static readonly string H1 = new('a', 64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string IgnoreFile(WritingFixture f) => Path.Combine(f.Repo.ModelRoot, ".cache", ".gitignore");

    [Fact]
    public async Task The_run_lock_creates_the_cache_folder_with_a_gitignore_holding_one_star_line()
    {
        using var f = new WritingFixture();
        Assert.False(Directory.Exists(Path.Combine(f.Repo.ModelRoot, ".cache")));

        var held = await new RunLock(f.Repo.Options, f.EnginePaths).AcquireAsync(wait: false, Ct);
        Assert.NotNull(held);
        await held.DisposeAsync();

        Assert.Equal("*\n", File.ReadAllText(IgnoreFile(f)));
        Assert.False(File.Exists(Path.Combine(f.Repo.RepoRoot, ".gitignore")));
    }

    [Fact]
    public async Task The_journal_and_a_built_manifest_create_it_too()
    {
        using (var f = new WritingFixture())
        {
            await f.Journal.BeginAsync("RUN1", null, ["p"], Ct);
            Assert.Equal("*\n", File.ReadAllText(IgnoreFile(f)));
        }

        using (var f = new WritingFixture())
        {
            await f.Manifests.SavePackAsync("p", committed: false, [new ManifestEntry("src/Generated/a.cs", H1, "u:1")], Ct);
            Assert.Equal("*\n", File.ReadAllText(IgnoreFile(f)));
        }

        using (var f = new WritingFixture())
        {
            // A committed manifest lives in the model, not in the cache folder.
            await f.Manifests.SavePackAsync("p", committed: true, [new ManifestEntry("db/a.sql", H1, "u:1")], Ct);
            Assert.False(Directory.Exists(Path.Combine(f.Repo.ModelRoot, ".cache")));
        }
    }

    [Fact]
    public async Task A_cache_directory_under_the_engine_folder_makes_it_ignore_itself()
    {
        // The container form of the MCP server points the cache at .maquettiste/.cache/cli.
        using var f = new WritingFixture();
        var options = f.Repo.Options with { CacheDirectory = Path.Combine(f.Repo.ModelRoot, ".cache", "cli") };
        var files = new EngineFiles(new OutputPathPolicy(options, null), WriteTarget.Cache, options);
        await files.WriteAsync(Path.Combine(options.CacheDirectory, "units", "p.v4.bin"), new byte[] { 1 }, Ct);

        Assert.Equal("*\n", File.ReadAllText(IgnoreFile(f)));
    }

    [Fact]
    public async Task An_existing_file_is_kept_and_a_cache_outside_the_engine_folder_gets_none()
    {
        using var f = new WritingFixture();
        Directory.CreateDirectory(Path.Combine(f.Repo.ModelRoot, ".cache"));
        File.WriteAllText(IgnoreFile(f), "mine\n");
        var held = await new RunLock(f.Repo.Options, f.EnginePaths).AcquireAsync(wait: false, Ct);
        await held!.DisposeAsync();
        Assert.Equal("mine\n", File.ReadAllText(IgnoreFile(f)));

        var files = new EngineFiles(f.EnginePaths, WriteTarget.Cache, f.Repo.Options);
        await files.WriteAsync(Path.Combine(f.Repo.CacheDirectory, "units", "p.v4.bin"), new byte[] { 1 }, Ct);
        Assert.False(File.Exists(Path.Combine(f.Repo.CacheDirectory, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(f.Repo.CacheDirectory, "units", ".gitignore")));
    }

    [Fact]
    public async Task A_journal_folder_the_host_chose_gets_no_file()
    {
        using var repo = new Testing.TempRepo();
        var journal = Path.Combine(repo.Root, "journal");
        var options = repo.Options with { JournalDirectory = journal };
        var held = await new RunLock(options, new OutputPathPolicy(options, null)).AcquireAsync(wait: false, Ct);
        await held!.DisposeAsync();

        Assert.True(File.Exists(Path.Combine(journal, "run.lock")));
        Assert.False(File.Exists(Path.Combine(journal, ".gitignore")));
    }
}
