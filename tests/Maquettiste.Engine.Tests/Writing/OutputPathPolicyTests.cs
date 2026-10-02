using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Tests.Writing;

public sealed class OutputPathPolicyTests
{
    [Theory]
    [InlineData("", "empty")]
    [InlineData("../outside.txt", "'..'")]
    [InlineData("db/../../outside.txt", "'..'")]
    [InlineData("db/./a.sql", "'.'")]
    [InlineData("./db/a.sql", "'.'")]
    [InlineData("/etc/passwd", "relative")]
    [InlineData("C:/db/a.sql", "drive")]
    [InlineData("c:db/a.sql", "drive")]
    [InlineData("db\\a.sql", "'/'")]
    [InlineData("\\\\server\\share\\a.sql", "'/'")]
    [InlineData("//server/share/a.sql", "relative")]
    [InlineData("db//a.sql", "empty segment")]
    [InlineData("db/", "empty segment")]
    [InlineData("db/a<b.sql", "invalid on Windows")]
    [InlineData("db/a>b.sql", "invalid on Windows")]
    [InlineData("db/a:b.sql", "invalid on Windows")]
    [InlineData("db/a\"b.sql", "invalid on Windows")]
    [InlineData("db/a|b.sql", "invalid on Windows")]
    [InlineData("db/a?b.sql", "invalid on Windows")]
    [InlineData("db/a*b.sql", "invalid on Windows")]
    [InlineData("db/a\tb.sql", "control character")]
    [InlineData("db/a\u0001b.sql", "control character")]
    [InlineData("db/a.sql.", "ends with")]
    [InlineData("db/a.sql ", "ends with")]
    [InlineData("db/con", "reserved")]
    [InlineData("db/CON.sql", "reserved")]
    [InlineData("db/lpt1.txt", "reserved")]
    [InlineData("db/nul/a.sql", "reserved")]
    [InlineData("db/.git/config", "always denied")]
    [InlineData("db/.GIT/config", "always denied")]
    [InlineData("db/.maquettiste/model/x.json", "always denied")]
    [InlineData(".maquettiste/manifest/p.json", "always denied")]
    [InlineData(".git/hooks/pre-commit", "always denied")]
    [InlineData("docs/readme.md", "outputs.allow")]
    [InlineData("dbx/a.sql", "outputs.allow")]
    [InlineData("src/a.cs", "outputs.allow")]
    public void Check_refuses_invalid_and_unlisted_paths(string path, string reason)
    {
        using var f = new WritingFixture();

        var check = f.Paths.Check(path);

        Assert.False(check.Allowed);
        Assert.Equal("MQ6004", check.RuleId);
        Assert.Contains(reason, check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_allows_paths_under_a_root_and_picks_the_longest_root()
    {
        using var repo = new Testing.TempRepo();
        var settings = new ProjectSettings
        {
            FormatVersion = 1,
            Outputs = new OutputSettings
            {
                Allow =
                [
                    new OutputRoot { Path = "src" },
                    new OutputRoot { Path = "src/Generated/" },
                    new OutputRoot { Path = "../escape" },
                ],
            },
        };
        var policy = new OutputPathPolicy(repo.Options, settings);

        var generated = policy.Check("src/Generated/Billing/Invoice.g.cs");
        var committed = policy.Check("src/Handwritten.cs");

        Assert.True(generated.Allowed);
        Assert.Equal(new OutputRootInfo("src/Generated"), generated.Root);
        Assert.Equal("src/Generated/Billing/Invoice.g.cs", generated.NormalizedPath);
        Assert.True(committed.Allowed);
        Assert.Equal(new OutputRootInfo("src"), committed.Root);
        Assert.False(policy.Check("escape/a.txt").Allowed);
    }

    [Fact]
    public void An_allow_entry_also_allows_the_file_of_its_own_path_unless_it_ends_with_a_slash()
    {
        using var repo = new Testing.TempRepo();
        var settings = new ProjectSettings
        {
            FormatVersion = 1,
            Outputs = new OutputSettings
            {
                Allow = [new OutputRoot { Path = ".gitignore" }, new OutputRoot { Path = "src/App/.gitignore" }, new OutputRoot { Path = "gen/" }, new OutputRoot { Path = "db" }],
                Deny = ["**/*.secret"],
            },
        };
        var policy = new OutputPathPolicy(repo.Options, settings);

        Assert.Equal(new OutputRootInfo(".gitignore"), policy.Check(".gitignore").Root);
        Assert.True(policy.Check("src/App/.gitignore").Allowed);
        // The file entry does not make its folder a root, so the repository root never becomes one.
        Assert.Contains("outputs.allow", policy.Check("src/App/Other.cs").Reason, StringComparison.Ordinal);
        Assert.Contains("outputs.allow", policy.Check("README.md").Reason, StringComparison.Ordinal);
        Assert.Contains("outputs.allow", policy.Check("src/.gitignore").Reason, StringComparison.Ordinal);
        // A trailing slash allows the folder only.
        Assert.True(policy.Check("gen/a.cs").Allowed);
        Assert.Contains("outputs.allow", policy.Check("gen").Reason, StringComparison.Ordinal);
        // A folder entry's own path is a file only while no folder has that name.
        Assert.True(policy.Check("db").Allowed);
        Directory.CreateDirectory(repo.PathOf("db"));
        Assert.Contains("is a folder", new OutputPathPolicy(repo.Options, settings).Check("db").Reason, StringComparison.Ordinal);
        Assert.Contains("outputs.deny", policy.Check("gen/a.secret").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_entry_that_is_a_link_out_of_the_repo_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var repo = new Testing.TempRepo();
        var outside = Path.Combine(Path.GetTempPath(), "mq-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "x\n");
        try
        {
            File.CreateSymbolicLink(repo.PathOf(".gitignore"), outside);
            var settings = new ProjectSettings { FormatVersion = 1, Outputs = new OutputSettings { Allow = [new OutputRoot { Path = ".gitignore" }] } };
            var check = new OutputPathPolicy(repo.Options, settings).Check(".gitignore");
            Assert.False(check.Allowed);
            Assert.Equal("MQ6004", check.RuleId);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("db/secret/*.sql", "db/secret/a.sql", true)]
    [InlineData("db/secret/*.sql", "db/secret/deep/a.sql", false)]
    [InlineData("db/**/*.bak", "db/a/b/c.bak", true)]
    [InlineData("db/**/*.bak", "db/c.bak", true)]
    [InlineData("**/*.pem", "src/Generated/keys/x.pem", true)]
    [InlineData("db/v?.sql", "db/v1.sql", true)]
    [InlineData("db/v?.sql", "db/v10.sql", false)]
    [InlineData("db/private/", "db/private/a/b.sql", true)]
    [InlineData("db/private", "db/private/a/b.sql", true)]
    [InlineData("/db/x.sql", "db/x.sql", true)]
    [InlineData("db/x.sql", "db/y.sql", false)]
    [InlineData("db/**", "db/anything/at/all.sql", true)]
    public void Check_applies_deny_globs(string glob, string path, bool denied)
    {
        using var f = new WritingFixture(deny: [glob]);

        var check = f.Paths.Check(path);

        Assert.Equal(!denied, check.Allowed);
        if (denied)
            Assert.Contains("outputs.deny", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_only_policy_refuses_every_output()
    {
        using var f = new WritingFixture();

        var check = f.EnginePaths.Check("db/a.sql");

        Assert.False(check.Allowed);
        Assert.Equal("MQ6004", check.RuleId);
    }

    [Fact]
    public void Check_refuses_a_symlinked_folder_that_leaves_the_root()
    {
        using var f = new WritingFixture();
        var outside = Path.Combine(f.Repo.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(f.Repo.PathOf("db"));
        Directory.CreateSymbolicLink(f.Repo.PathOf("db/link"), outside);

        var check = f.Paths.Check("db/link/a.sql");

        Assert.False(check.Allowed);
        Assert.Contains("symbolic link", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_refuses_a_symlinked_folder_into_another_part_of_the_repo()
    {
        using var f = new WritingFixture();
        Directory.CreateDirectory(f.Repo.PathOf("db"));
        Directory.CreateDirectory(f.Repo.PathOf("docs"));
        Directory.CreateSymbolicLink(f.Repo.PathOf("db/docs"), "../docs");

        Assert.False(f.Paths.Check("db/docs/a.sql").Allowed);
    }

    [Fact]
    public void Check_refuses_a_symlinked_target_file_that_leaves_the_root()
    {
        using var f = new WritingFixture();
        var outside = Path.Combine(f.Repo.Root, "outside.txt");
        File.WriteAllText(outside, "x");
        Directory.CreateDirectory(f.Repo.PathOf("db"));
        File.CreateSymbolicLink(f.Repo.PathOf("db/a.sql"), outside);

        Assert.False(f.Paths.Check("db/a.sql").Allowed);
    }

    [Fact]
    public void Check_refuses_an_output_root_that_is_a_symlink_out_of_the_repo()
    {
        using var f = new WritingFixture();
        var outside = Path.Combine(f.Repo.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(f.Repo.PathOf("db"), outside);

        var check = f.Paths.Check("db/a.sql");

        Assert.False(check.Allowed);
        Assert.Contains("outside the repo", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_allows_a_symlink_that_stays_inside_the_root()
    {
        using var f = new WritingFixture();
        Directory.CreateDirectory(f.Repo.PathOf("db/real"));
        Directory.CreateSymbolicLink(f.Repo.PathOf("db/alias"), "real");

        Assert.True(f.Paths.Check("db/alias/a.sql").Allowed);
    }

    [Fact]
    public void Check_refuses_a_symlink_cycle()
    {
        using var f = new WritingFixture();
        Directory.CreateDirectory(f.Repo.PathOf("db"));
        Directory.CreateSymbolicLink(f.Repo.PathOf("db/a"), "b");
        Directory.CreateSymbolicLink(f.Repo.PathOf("db/b"), "a");

        Assert.False(f.Paths.Check("db/a/x.sql").Allowed);
    }

    [Fact]
    public void Engine_writes_are_confined_to_their_target_folders()
    {
        using var f = new WritingFixture();
        var journalDir = Path.Combine(f.Repo.ModelRoot, ".cache");

        Assert.True(f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(f.Repo.CacheDirectory, "index.v1.bin")).Allowed);
        Assert.True(f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(journalDir, "journal.jsonl")).Allowed);
        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(f.Repo.Root, "elsewhere", "x.bin")).Allowed);
        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(f.Repo.ModelRoot, "model", "x.json")).Allowed);
        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, f.Repo.CacheDirectory).Allowed);
        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(f.Repo.CacheDirectory, "..", "escape.bin")).Allowed);

        Assert.True(f.EnginePaths.CheckEngineWrite(WriteTarget.Model, Path.Combine(f.Repo.ModelRoot, "model", "entities", "a.json")).Allowed);
        Assert.True(f.EnginePaths.CheckEngineWrite(WriteTarget.Model, Path.Combine(f.Repo.ModelRoot, "manifest", "p.json")).Allowed);
        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Model, Path.Combine(f.Repo.RepoRoot, "db", "a.sql")).Allowed);
        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Model, Path.Combine(f.Repo.CacheDirectory, "a.json")).Allowed);

        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Setup, Path.Combine(f.Repo.RepoRoot, ".gitignore")).Allowed);
        Assert.True(f.EnginePaths.CheckEngineWrite(WriteTarget.Setup, Path.Combine(f.Repo.RepoRoot, ".git", "hooks", "post-checkout")).Allowed);
        Assert.True(f.EnginePaths.CheckEngineWrite(WriteTarget.Setup, Path.Combine(f.Repo.RepoRoot, ".git", "hooks", "post-merge")).Allowed);
        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Setup, Path.Combine(f.Repo.RepoRoot, ".git", "hooks", "pre-commit")).Allowed);
        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Setup, Path.Combine(f.Repo.RepoRoot, ".git", "config")).Allowed);

        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, "relative/index.bin").Allowed);
    }

    [Fact]
    public void The_repository_gitignore_is_never_an_engine_write_and_is_an_output_only_through_an_allow_entry()
    {
        using var f = new WritingFixture();
        var gitignore = Path.Combine(f.Repo.RepoRoot, ".gitignore");

        // No engine write (setup, model, cache) reaches the customer's file, and without settings no output does either.
        foreach (var target in new[] { WriteTarget.Setup, WriteTarget.Model, WriteTarget.Cache, WriteTarget.Output })
        {
            var refused = f.EnginePaths.CheckEngineWrite(target, gitignore);
            Assert.False(refused.Allowed, target.ToString());
            Assert.Equal(OutputPathPolicy.RefusedRule, refused.RuleId);
        }

        Assert.DoesNotContain("gitignore", f.EnginePaths.CheckEngineWrite(WriteTarget.Setup, gitignore).Reason, StringComparison.Ordinal);

        // Generation reaches it only because the fixture's outputs.allow names the file.
        Assert.True(f.Paths.CheckEngineWrite(WriteTarget.Output, gitignore).Allowed);
        Assert.False(f.Paths.CheckEngineWrite(WriteTarget.Output, Path.Combine(f.Repo.RepoRoot, "src", ".gitignore")).Allowed);
        Assert.True(f.EnginePaths.CheckEngineWrite(WriteTarget.Setup, Path.Combine(f.Repo.RepoRoot, ".mcp.json")).Allowed);
    }

    [Fact]
    public void Engine_write_checks_follow_symlinks()
    {
        using var f = new WritingFixture();
        var outside = Path.Combine(f.Repo.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(f.Repo.CacheDirectory, "units"), outside);

        Assert.False(f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(f.Repo.CacheDirectory, "units", "p.v1.bin")).Allowed);
    }

    [Fact]
    public void Engine_write_checks_refuse_a_symlink_cycle_without_throwing()
    {
        using var f = new WritingFixture();
        Directory.CreateDirectory(f.Repo.CacheDirectory);
        Directory.CreateSymbolicLink(Path.Combine(f.Repo.CacheDirectory, "a"), "b");
        Directory.CreateSymbolicLink(Path.Combine(f.Repo.CacheDirectory, "b"), "a");

        var check = f.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(f.Repo.CacheDirectory, "a", "x.bin"));

        Assert.False(check.Allowed);
        Assert.Equal("MQ6004", check.RuleId);
    }

    [Fact]
    public void Setup_write_check_refuses_a_symlink_cycle_without_throwing()
    {
        using var f = new WritingFixture();
        Directory.CreateDirectory(f.Repo.PathOf(".git"));
        Directory.CreateSymbolicLink(f.Repo.PathOf(".git/hooks"), "loop");
        Directory.CreateSymbolicLink(f.Repo.PathOf(".git/loop"), "hooks");

        var check = f.EnginePaths.CheckEngineWrite(WriteTarget.Setup, Path.Combine(f.Repo.RepoRoot, ".git", "hooks", "post-merge"));

        Assert.False(check.Allowed);
        Assert.Equal("MQ6004", check.RuleId);
    }

    [Fact]
    public void Engine_write_of_an_output_goes_through_the_output_rules()
    {
        using var f = new WritingFixture();

        var allowed = f.Paths.CheckEngineWrite(WriteTarget.Output, f.Repo.PathOf("db/a.sql"));

        Assert.True(allowed.Allowed);
        Assert.Equal("db/a.sql", allowed.NormalizedPath);
        Assert.False(f.Paths.CheckEngineWrite(WriteTarget.Output, f.Repo.PathOf(".maquettiste/x.json")).Allowed);
        Assert.False(f.Paths.CheckEngineWrite(WriteTarget.Output, Path.Combine(f.Repo.Root, "cache", "x")).Allowed);
    }

    [Fact]
    public void Journal_directory_option_moves_the_cache_target()
    {
        using var repo = new Testing.TempRepo();
        var journal = Path.Combine(repo.Root, "journal");
        var policy = new OutputPathPolicy(repo.Options with { JournalDirectory = journal }, null);

        Assert.True(policy.CheckEngineWrite(WriteTarget.Cache, Path.Combine(journal, "run.lock")).Allowed);
        Assert.False(policy.CheckEngineWrite(WriteTarget.Cache, Path.Combine(repo.ModelRoot, ".cache", "run.lock")).Allowed);
    }

    [Theory]
    [InlineData("db/a.sql")]
    [InlineData("db/deep/b.sql")]
    [InlineData("db/secret/a.sql")]
    [InlineData("../outside.txt")]
    [InlineData("db/con")]
    [InlineData("db/.git/config")]
    [InlineData("docs/readme.md")]
    [InlineData("db")]
    [InlineData("")]
    public void Lexical_root_is_the_root_check_finds_before_it_reads_the_disk(string path)
    {
        using var f = new WritingFixture(deny: ["db/secret/"]);

        var check = f.Paths.Check(path);

        // Without symbolic links in the repo, the checks that need no disk decide alone: same root when allowed, none when refused.
        Assert.Equal(check.Allowed ? check.Root : null, f.Paths.LexicalRoot(path));
    }
}
