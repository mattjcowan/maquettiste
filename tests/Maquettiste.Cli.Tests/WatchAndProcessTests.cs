using System.Diagnostics;

namespace Maquettiste.Cli.Tests;

public sealed class WatchTests
{
    [Fact]
    public async Task Watch_regenerates_after_one_model_edit_and_stops_with_0()
    {
        using var repo = CliRepo.Billing();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new SharedWriter();
        var error = new SharedWriter();
        var run = CliRepo.RunWithAsync(repo.RepoRoot, output, error, cts.Token, _ => null,
            "generate", "--watch", "--repo", repo.RepoRoot, "--cache-dir", repo.CacheDirectory, "--progress", "none");
        try
        {
            await WaitForAsync(() => error.Text().Contains("[watch] watching", StringComparison.Ordinal), run, error);
            Assert.Contains("varchar(120)", repo.Read("db/main/customers.sql"), StringComparison.Ordinal);

            repo.Replace(".maquettiste/model/entities/customer.json", "\"length\": 120", "\"length\": 150");
            await WaitForAsync(() => error.Text().Contains("[watch] run 1:", StringComparison.Ordinal), run, error);
            Assert.Contains("varchar(150)", repo.Read("db/main/customers.sql"), StringComparison.Ordinal);
            Assert.Contains("M db/main/customers.sql", output.Text(), StringComparison.Ordinal);
            // Each watch run counts only the files it compared: the edit re-renders 3 units (1 file changed, 2 unchanged), and the
            // initial run's 11 files are not counted again.
            Assert.True(error.Text().Contains("[watch] run 1: Generated: 0 added, 1 modified, 0 deleted, 2 unchanged", StringComparison.Ordinal), error.Text());

            // The engine's own writes (manifests, journal) trigger no further runs: touch a manifest, then stay well past the
            // debounce (4 x 250 ms plus a run) before looking for a second run.
            var manifest = Directory.EnumerateFiles(repo.PathOf(".maquettiste/manifest"), "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
            File.SetLastWriteTimeUtc(manifest, DateTime.UtcNow);
            File.WriteAllBytes(manifest, File.ReadAllBytes(manifest));
            await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("[watch] run 2:", error.Text(), StringComparison.Ordinal);
        }
        finally
        {
            await cts.CancelAsync();
        }

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Contains("[watch] stopped.", error.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain("[watch] run 2:", error.Text(), StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<bool> condition, Task<int> run, SharedWriter error)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (run.IsCompleted)
                Assert.Fail("The watch command ended early with " + await run + ":\n" + error.Text());
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
                Assert.Fail("Timed out:\n" + error.Text());
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

public sealed class ProcessTests
{
    private static string CliPath => typeof(Program).Assembly.Location;

    private static async Task<(int Code, string Out, string Error)> RunProcessAsync(string cwd, params string[] args)
    {
        var host = Environment.ProcessPath is { } p && Path.GetFileNameWithoutExtension(p) == "dotnet" ? p : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(CliPath);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task The_tool_runs_as_a_process_and_returns_its_exit_codes()
    {
        using var repo = CliRepo.Billing();
        var version = await RunProcessAsync(repo.RepoRoot, "--version");
        Assert.Equal(0, version.Code);
        Assert.Equal(CliApp.VersionLine() + "\n", version.Out);
        Assert.StartsWith("maquettiste 0.", version.Out, StringComparison.Ordinal);
        Assert.Contains("engine contract " + Engine.EngineVersion.Value, version.Out, StringComparison.Ordinal);

        var generate = await RunProcessAsync(repo.RepoRoot, "generate", "--cache-dir", repo.CacheDirectory);
        Assert.True(generate.Code == 0, generate.Error);
        Assert.Contains("A db/main/customers.sql", generate.Out, StringComparison.Ordinal);
        Assert.Contains("[1/8 load]", generate.Error, StringComparison.Ordinal);

        repo.Write("db/main/customers.sql", "-- edited\n");
        var check = await RunProcessAsync(repo.RepoRoot, "generate", "--check", "--cache-dir", repo.CacheDirectory);
        Assert.Equal(3, check.Code);

        var usage = await RunProcessAsync(repo.RepoRoot, "nope");
        Assert.Equal(4, usage.Code);
    }
}
