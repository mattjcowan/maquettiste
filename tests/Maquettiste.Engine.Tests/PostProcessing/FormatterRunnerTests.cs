using System.Diagnostics;
using System.Text;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.PostProcessing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.PostProcessing;

/// <summary>
/// Runs <see cref="FormatterRunner"/> against real processes: a small Python fake formatter written by the test (stdin to
/// stdout), and <c>dotnet --version</c> for version pinning with a command every test machine has.
/// </summary>
public sealed class FormatterRunnerTests : IDisposable
{
    private const string Script = """
        import os, sys, time
        args = sys.argv[1:]
        if args[:1] == ["--version"]:
            print("fakefmt 1.2.3 (python)")
            sys.exit(0)
        mode = args[0] if args else "upper"
        data = sys.stdin.buffer.read()
        if mode == "upper":
            sys.stdout.buffer.write(data.upper())
        elif mode == "path":
            sys.stdout.buffer.write(data + b"// path=" + args[1].encode("utf-8") + b"\n")
        elif mode == "cwd":
            sys.stdout.buffer.write(os.getcwd().encode("utf-8"))
        elif mode == "fail":
            sys.stderr.write("syntax error on line 3\n")
            sys.exit(3)
        elif mode == "sleep":
            time.sleep(30)
        elif mode == "crlf":
            sys.stdout.buffer.write(b"\xef\xbb\xbf" + data.replace(b"\n", b"\r\n"))
        """;

    private readonly TempRepo _repo = new();
    private readonly string _script;

    public FormatterRunnerTests()
    {
        _script = Path.Combine(_repo.Root, "fakefmt.py");
        File.WriteAllText(_script, Script.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
    }

    public void Dispose() => _repo.Dispose();

    private FormatterRunner Runner() => new(_repo.Options);

    private FormatterSettings Fake(string python, string mode, params string[] extra) => new()
    {
        Name = "fake",
        Extensions = [".sql"],
        Command = python,
        Args = [_script, mode, .. extra],
        Version = "1.2.3",
        VersionArgs = [_script, "--version"],
        TimeoutSeconds = 10,
    };

    private static string Python()
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe", "py.exe" } : ["python3", "python"])
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        Assert.Skip("No python on PATH for the fake formatter.");
        return "";
    }

    private static string DotNet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
            return host;
        var main = Process.GetCurrentProcess().MainModule?.FileName;
        if (main is not null && Path.GetFileNameWithoutExtension(main).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return main;
        return "dotnet";
    }

    [Fact]
    public async Task Formats_stdin_to_stdout_in_memory()
    {
        var python = Python();
        _repo.WriteFile("db/a.sql", "on disk, untouched\n");

        var result = await Runner().FormatAsync(Fake(python, "upper"), "db/a.sql", Encoding.UTF8.GetBytes("select 1;\nwhere é\n"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal("SELECT 1;\nWHERE é\n", Encoding.UTF8.GetString(result.Output.Span));
        Assert.Equal("on disk, untouched\n", _repo.ReadFile("db/a.sql"));
    }

    [Fact]
    public async Task Replaces_path_placeholder_and_runs_in_the_repo_root()
    {
        var python = Python();
        var runner = Runner();

        var withPath = await runner.FormatAsync(Fake(python, "path", "{path}"), "db/tables/a b.sql", Encoding.UTF8.GetBytes("x\n"),
            TestContext.Current.CancellationToken);
        var cwd = await runner.FormatAsync(Fake(python, "cwd"), "db/a.sql", ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        Assert.Equal("x\n// path=db/tables/a b.sql\n", Encoding.UTF8.GetString(withPath.Output.Span));
        // Compare through a marker file: temp folders can sit behind a symlink (/var and /private/var on macOS).
        _repo.WriteFile("cwd-marker-7f3a.txt", "here");
        Assert.True(File.Exists(Path.Combine(Encoding.UTF8.GetString(cwd.Output.Span), "cwd-marker-7f3a.txt")));
    }

    [Fact]
    public async Task Large_input_does_not_deadlock()
    {
        var python = Python();
        var input = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("select * from invoice;\n", 50_000)));

        var result = await Runner().FormatAsync(Fake(python, "upper"), "db/a.sql", input, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(input.Length, result.Output.Length);
    }

    [Fact]
    public async Task Non_zero_exit_is_MQ6008_with_stderr()
    {
        var python = Python();

        var result = await Runner().FormatAsync(Fake(python, "fail"), "db/a.sql", Encoding.UTF8.GetBytes("x"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Equal("MQ6008", result.Error.Rule);
        Assert.Equal("db/a.sql", result.Error.FilePath);
        Assert.Contains("exited with code 3", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("syntax error on line 3", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_is_MQ6008()
    {
        var python = Python();
        var settings = Fake(python, "sleep") with { TimeoutSeconds = 1 };
        var watch = Stopwatch.StartNew();

        var result = await Runner().FormatAsync(settings, "db/a.sql", Encoding.UTF8.GetBytes("x"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("MQ6008", result.Error!.Rule);
        Assert.Contains("timed out after 1 s", result.Error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Cancellation_kills_the_formatter()
    {
        var python = Python();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner().FormatAsync(Fake(python, "sleep"), "db/a.sql", Encoding.UTF8.GetBytes("x"), cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Missing_command_is_MQ6008()
    {
        var settings = Units.Formatter("ghost", ".sql") with { Command = Path.Combine(_repo.Root, "no-such-formatter") };

        var result = await Runner().FormatAsync(settings, "db/a.sql", Encoding.UTF8.GetBytes("x"), TestContext.Current.CancellationToken);
        var versions = await Runner().VerifyVersionsAsync([settings], TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be started", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal("MQ6008", Assert.Single(versions).Rule);
    }

    [Fact]
    public async Task Pinned_version_is_checked_against_the_version_output()
    {
        var python = Python();
        var good = Fake(python, "upper");
        var bad = Fake(python, "upper") with { Name = "stale", Version = "2.0.0" };

        var diagnostics = await Runner().VerifyVersionsAsync([good, bad], TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MQ6008", diagnostic.Rule);
        Assert.Equal(".maquettiste/maquettiste.json", diagnostic.FilePath);
        Assert.Equal("/formatters/1", diagnostic.JsonPointer);
        Assert.Contains("pins '2.0.0'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("fakefmt 1.2.3", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pinned_version_check_with_dotnet()
    {
        var dotnet = DotNet();
        var match = new FormatterSettings
        {
            Name = "dotnet", Extensions = [".cs"], Command = dotnet, Version = Environment.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
            VersionArgs = ["--list-runtimes"],
        };
        var mismatch = match with { Version = "0.0.0-not-installed" };

        var ok = await Runner().VerifyVersionsAsync([match], TestContext.Current.CancellationToken);
        var stale = await Runner().VerifyVersionsAsync([mismatch], TestContext.Current.CancellationToken);

        Assert.Empty(ok);
        Assert.Equal("MQ6008", Assert.Single(stale).Rule);
    }

    [Fact]
    public async Task Post_processor_formats_with_the_real_runner_and_normalizes_its_output()
    {
        var python = Python();
        var paths = new FakePathPolicy(null, new OutputRootInfo("db", true));
        var processor = new PostProcessor(_repo.Options, Runner());
        var formatter = Fake(python, "crlf");
        var context = new PostProcessContext(_repo.RepoRoot, paths, [formatter], FormatterVersionsVerified: false);

        var result = await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "a\nb\n")), context,
            TestContext.Current.CancellationToken);

        Assert.False(result.Failed, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("a\nb\n", Encoding.UTF8.GetString(Assert.Single(result.Files).Content.Span));
        Assert.False(_repo.Exists("db/a.sql"));

        var stale = context with { Formatters = [formatter with { Version = "9.9.9" }] };
        var failed = await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "a\n")), stale,
            TestContext.Current.CancellationToken);
        Assert.True(failed.Failed);
        Assert.Equal("MQ6008", Assert.Single(failed.Diagnostics).Rule);
    }
}
