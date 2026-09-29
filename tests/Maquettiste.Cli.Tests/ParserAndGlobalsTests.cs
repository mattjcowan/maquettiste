using Maquettiste.Cli.Commands;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli.Tests;

public sealed class ParserAndGlobalsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Exit_codes_follow_spec_section_17() =>
        Assert.Equal([0, 1, 2, 3, 4], new[] { Program.ExitCodes.Success, Program.ExitCodes.Invalid, Program.ExitCodes.Drift, Program.ExitCodes.Conflicts, Program.ExitCodes.Internal });

    [Theory]
    [InlineData(RunOutcome.Succeeded, 0)]
    [InlineData(RunOutcome.Invalid, 1)]
    [InlineData(RunOutcome.Drift, 2)]
    [InlineData(RunOutcome.Conflicts, 3)]
    [InlineData(RunOutcome.Busy, 4)]
    [InlineData(RunOutcome.Stale, 4)]
    [InlineData(RunOutcome.Cancelled, 4)]
    [InlineData(RunOutcome.Failed, 4)]
    public void Every_run_outcome_maps_to_its_exit_code(RunOutcome outcome, int code) => Assert.Equal(code, GenerateCommand.ExitCode(outcome));

    [Theory]
    [InlineData(FileChangeKind.Added, 'A')]
    [InlineData(FileChangeKind.Modified, 'M')]
    [InlineData(FileChangeKind.Deleted, 'D')]
    [InlineData(FileChangeKind.HandEdited, 'H')]
    [InlineData(FileChangeKind.Kept, 'K')]
    [InlineData(FileChangeKind.OrphanedOwned, 'O')]
    [InlineData(FileChangeKind.Conflict, 'C')]
    public void Plan_letters_follow_the_design(FileChangeKind kind, char letter) => Assert.Equal(letter, GenerateCommand.Letter(kind));

    [Fact]
    public async Task Version_prints_the_product_and_engine_contract_versions()
    {
        var result = await CliRepo.RunInAsync(Path.GetTempPath(), Ct, "--version");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CliApp.VersionLine() + "\n", result.Out);
        Assert.StartsWith("maquettiste 0.", result.Out, StringComparison.Ordinal);
        Assert.Contains("engine contract " + EngineVersion.Value + ", model format " + EngineVersion.FormatVersion, result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_exits_0_on_stdout_and_no_command_exits_4_on_stderr()
    {
        var help = await CliRepo.RunInAsync(Path.GetTempPath(), Ct, "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("Usage: maquettiste", help.Out, StringComparison.Ordinal);

        var none = await CliRepo.RunInAsync(Path.GetTempPath(), Ct);
        Assert.Equal(4, none.ExitCode);
        Assert.Equal("", none.Out);
        Assert.Contains("Usage: maquettiste", none.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frob")]
    [InlineData("validate", "--force")]
    [InlineData("validate", "--bogus")]
    [InlineData("validate", "--format", "xml")]
    [InlineData("generate", "--jobs", "0")]
    [InlineData("generate", "--jobs")]
    [InlineData("generate", "--dry-run", "--check")]
    [InlineData("generate", "--diff")]
    [InlineData("generate", "--watch", "--check")]
    [InlineData("generate", "--check", "--roots", "built")]
    [InlineData("migrate", "extra")]
    [InlineData("pack", "old", "x")]
    [InlineData("pack", "new")]
    [InlineData("pack", "new", "Bad_Name")]
    [InlineData("--quiet", "--verbosity", "detailed", "validate")]
    [InlineData("--version", "validate")]
    public async Task Usage_errors_exit_4(params string[] args)
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync(args);
        Assert.Equal(4, result.ExitCode);
        Assert.StartsWith("maquettiste: ", result.Error, StringComparison.Ordinal);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public void Parser_reads_equals_values_repeated_options_aliases_and_the_double_dash()
    {
        var line = CommandLine.Parse(["generate", "--pack=a", "-p", "b", "--jobs=3", "-q", "--", "--not-an-option"]);
        Assert.Equal(["generate", "--not-an-option"], line.Positionals);
        Assert.Equal(["a", "b"], line.Values("--pack"));
        Assert.Equal(3, line.Int("--jobs", 1));
        Assert.True(line.Has("--quiet"));
        Assert.Throws<UsageException>(() => line.Value("--pack"));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["--force=yes"]));
    }

    [Fact]
    public void Cache_directory_prefers_the_option_then_the_environment_then_a_hashed_user_folder()
    {
        var cwd = Path.GetTempPath();
        GlobalContext Context(Func<string, string?> env, params string[] args) =>
            new(new CliEnvironment { Out = TextWriter.Null, Error = TextWriter.Null, CurrentDirectory = cwd, GetEnvironmentVariable = env },
                CommandLine.Parse(args), Verbosity.Normal, ProgressStyle.None, null);

        var repo = Path.Combine(cwd, "some-repo");
        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "c1")), Context(_ => "/ignored", "--cache-dir", "c1").CacheDirectory(repo));
        var fromEnv = Path.Combine(cwd, "from-env");
        Assert.Equal(fromEnv, Context(n => n == "MAQUETTISTE_CACHE_DIR" ? fromEnv : null).CacheDirectory(repo));

        var home = Path.Combine(cwd, "home");
        var defaultDir = Context(n => n == "HOME" ? home : null).CacheDirectory(repo);
        var hash = Path.GetFileName(defaultDir);
        Assert.Equal(16, hash.Length);
        Assert.Matches("^[0-9a-f]{16}$", hash);
        Assert.Equal("maquettiste", Path.GetFileName(Path.GetDirectoryName(defaultDir)));
        Assert.Equal(defaultDir, Context(n => n == "HOME" ? home : null).CacheDirectory(repo));
        Assert.NotEqual(defaultDir, Context(n => n == "HOME" ? home : null).CacheDirectory(repo + "-other"));
    }

    [Fact]
    public async Task Repo_defaults_to_the_nearest_ancestor_holding_the_model()
    {
        using var repo = CliRepo.Billing();
        var sub = Directory.CreateDirectory(repo.PathOf("src/deep/folder")).FullName;
        var result = await CliRepo.RunInAsync(sub, Ct, "migrate");
        Assert.Equal(0, result.ExitCode);

        // Without a model above, the current directory is the repo, and it holds no model: exit 1.
        using var empty = CliRepo.Empty();
        var none = await CliRepo.RunInAsync(empty.RepoRoot, Ct, "validate", "--cache-dir", empty.CacheDirectory);
        Assert.Equal(1, none.ExitCode);
        Assert.Contains("no model found", none.Error, StringComparison.Ordinal);
    }
}
