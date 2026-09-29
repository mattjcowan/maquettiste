namespace Maquettiste.Cli.Tests;

/// <summary>A refused write names its path in one line (exit 1); <c>generate --check</c> progress; <c>maquettiste format</c>.</summary>
public sealed class PermissionAndFormatTests
{
    private const string EntityFile = ".maquettiste/model/entities/customer.json";

    /// <summary>Makes a folder read-only, or skips the test where that does not stop the process (Windows, root).</summary>
    private static void ReadOnly(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions only.");
            return;
        }

        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(Path.Combine(folder, ".probe"), "");
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        File.Delete(Path.Combine(folder, ".probe"));
        Writable(folder);
        Assert.Skip("The process can write to a read-only folder (running as root).");
    }

    private static void Writable(string folder)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void AssertOneLine(CliResult result, string path)
    {
        Assert.True(result.ExitCode == 1, result.ToString());
        var lines = Text.Lines(result.Error);
        var line = Assert.Single(lines, l => l.StartsWith("maquettiste: permission denied", StringComparison.Ordinal));
        Assert.Contains(path, line, StringComparison.Ordinal);
        Assert.DoesNotContain(".tmp", line, StringComparison.Ordinal);
        if (OperatingSystem.IsLinux())
            Assert.Contains("--user $(id -u):$(id -g)", line, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_output_folder_prints_one_line_naming_the_file_and_exits_1()
    {
        using var repo = CliRepo.Billing();
        Assert.Equal(0, (await repo.RunAsync("generate")).ExitCode);
        var folder = repo.PathOf("db/main");
        File.Delete(repo.PathOf("db/main/customers.sql"));
        ReadOnly(folder);
        try
        {
            AssertOneLine(await repo.RunAsync("generate", "--progress", "none"), Path.Combine(folder, "customers.sql"));
        }
        finally
        {
            Writable(folder);
        }
    }

    [Fact]
    public async Task A_run_lock_that_cannot_be_created_prints_one_line_naming_its_folder_and_exits_1()
    {
        using var repo = CliRepo.Billing();
        var folder = repo.PathOf(".maquettiste/.cache");
        ReadOnly(folder);
        try
        {
            AssertOneLine(await repo.RunAsync("generate", "--progress", "none"), folder);
        }
        finally
        {
            Writable(folder);
        }
    }

    [Fact]
    public void The_message_names_the_target_of_a_temporary_file_and_the_hint_is_for_linux_only()
    {
        var e = new UnauthorizedAccessException("Access to the path '/repo/db/.a.sql.mq-01ABC-3.tmp' is denied.");
        Assert.Equal(
            "maquettiste: permission denied: cannot write /repo/db/a.sql. Check that the user running maquettiste can write there; in a container, run it as the owner of the repo with --user $(id -u):$(id -g).",
            PermissionError.Message(e, linux: true));
        Assert.DoesNotContain("--user", PermissionError.Message(e, linux: false), StringComparison.Ordinal);
        Assert.Same(e, PermissionError.Find(new AggregateException(new InvalidOperationException("x", e))));
        Assert.Null(PermissionError.Find(new InvalidOperationException("x")));
    }

    [Fact]
    public async Task Check_progress_writes_each_stage_once_in_order_and_no_write_stage()
    {
        using var repo = CliRepo.Billing();
        Assert.Equal(0, (await repo.RunAsync("generate")).ExitCode);
        var result = await repo.RunAsync("generate", "--check", "--progress", "plain");
        Assert.True(result.ExitCode == 0, result.ToString());
        var started = Text.Lines(result.Error).Where(l => l.EndsWith("] started", StringComparison.Ordinal)).Select(l => l[1..l.IndexOf('/', StringComparison.Ordinal)]).Select(int.Parse).ToList();
        Assert.Equal(started.Order().Distinct(), started);
        Assert.DoesNotContain("write]", result.Error, StringComparison.Ordinal);
        var apply = await repo.RunAsync("generate", "--force", "--progress", "plain");
        var applied = Text.Lines(apply.Error).Where(l => l.EndsWith("] started", StringComparison.Ordinal)).ToList();
        Assert.Equal(applied.Distinct(), applied);
        Assert.Contains("[8/8 write] started", applied);
    }

    [Fact]
    public async Task Format_rewrites_hand_written_files_canonically_and_check_exits_2_when_one_would_change()
    {
        using var repo = CliRepo.Billing();
        var clean = await repo.RunAsync("format", "--check");
        Assert.True(clean.ExitCode == 0, clean.ToString());
        Assert.Empty(clean.Out);

        var canonical = repo.Read(EntityFile);
        repo.Write(EntityFile, canonical.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("  ", "    ", StringComparison.Ordinal));
        repo.Write(".maquettiste/model/entities/broken.json", "{ \"kind\": \"entity\", ");

        var check = await repo.RunAsync("format", "--check");
        Assert.True(check.ExitCode == 2, check.ToString());
        Assert.Equal(["would format " + EntityFile], Text.Lines(check.Out));
        Assert.Contains("broken.json was left as it is", check.Error, StringComparison.Ordinal);
        Assert.Contains("1 of ", check.Error, StringComparison.Ordinal);
        Assert.NotEqual(canonical, repo.Read(EntityFile));

        var format = await repo.RunAsync("format");
        Assert.True(format.ExitCode == 0, format.ToString());
        Assert.Equal(["formatted " + EntityFile], Text.Lines(format.Out));
        Assert.Contains("Formatted 1 of ", format.Error, StringComparison.Ordinal);
        Assert.Equal(canonical, repo.Read(EntityFile));

        File.Delete(repo.PathOf(".maquettiste/model/entities/broken.json"));
        Assert.Equal(0, (await repo.RunAsync("format", "--check")).ExitCode);
        Assert.Equal(0, (await repo.RunAsync("validate")).ExitCode);
    }
}
