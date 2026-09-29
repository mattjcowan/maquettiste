using System.Globalization;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Bench;

/// <summary>
/// Console entry point (<c>dotnet run -c Release --project bench/Maquettiste.Bench -- [options]</c>), with the options of
/// <c>maquettiste bench</c> (engine-design.md section 17) plus <c>--report &lt;file&gt;</c> (write the JSON report) and
/// <c>--no-example-packs</c>. Exit codes: 0 pass, 2 a budget, the regression gate, the check run or the determinism cross-check
/// failed, 4 usage error, invalid model, missing baseline, I/O error or cancellation.
/// </summary>
public static class Program
{
    /// <summary>The usage text.</summary>
    public const string Usage =
        "usage: Maquettiste.Bench [--out <dir>] [--jobs 8] [--seed 42] [--entities 5000] [--relations 20000] [--enums 500] [--fanout <n>]\n" +
        "                         [--keep] [--baseline <file>] [--max-regression 10] [--format text|json] [--report <file>] [--no-example-packs]\n" +
        "                         [--cli <maquettiste executable> | --no-one-shot]\n" +
        "       Maquettiste.Bench write-model --out <dir> [options]   (the explorer's large mock model; see WriteModel.Usage)\n";

    /// <summary>Runs the benchmark.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The exit code: 0 pass, 2 budget or regression failure, 4 error.</returns>
    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        ArgumentNullException.ThrowIfNull(args);
        if (args.Length > 0 && args[0] == WriteModel.Verb)
            return await WriteModel.RunAsync(args[1..], Console.Out, Console.Error, cancel.Token).ConfigureAwait(false);
        if (args.Length > 0 && args[0] == OneShotGenerate.Verb)
            return await OneShotGenerate.RunAsync(args[1..], cancel.Token).ConfigureAwait(false);

        if (!TryParse(args, out var options, out var format, out var reportPath, out var error))
        {
            await Console.Error.WriteAsync(error + "\n" + Usage).ConfigureAwait(false);
            return 4;
        }

        // The one-shot figure runs this app again in its one-shot-generate mode unless --cli names a maquettiste to run instead
        // (a packed tool, for ReadyToRun numbers) or --no-one-shot turns it off.
        if (options.OneShotCommand is null && !args.Contains("--no-one-shot"))
            options = options with { OneShotCommand = OneShotGenerate.SelfCommand() };
        try
        {
            var progress = new StageProgress();
            var report = await BenchmarkHarness.RunAsync(options, progress, cancel.Token).ConfigureAwait(false);
            var json = BenchmarkReportJson.Write(report);
            if (reportPath is not null)
                await WriteReportAsync(reportPath, json, cancel.Token).ConfigureAwait(false);
            if (format == "json")
                await Console.OpenStandardOutput().WriteAsync(json, cancel.Token).ConfigureAwait(false);
            else
                await Console.Out.WriteAsync(BenchmarkReportJson.ToText(report)).ConfigureAwait(false);
            return report.Passed ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return 4;
        }
        catch (BenchmarkException e)
        {
            await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 4;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await Console.Error.WriteLineAsync("Maquettiste.Bench: " + e.Message).ConfigureAwait(false);
            return 4;
        }
    }

    /// <summary>Parses the command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="options">The options.</param>
    /// <param name="format">The output format, <c>text</c> or <c>json</c>.</param>
    /// <param name="reportPath">The report file, if any.</param>
    /// <param name="error">The error, when parsing fails.</param>
    /// <returns><see langword="true"/> when the arguments are valid.</returns>
    public static bool TryParse(IReadOnlyList<string> args, out BenchmarkOptions options, out string format, out string? reportPath, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = new BenchmarkOptions();
        format = "text";
        reportPath = null;
        error = "";
        var model = options.Model;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Value()
            {
                if (i + 1 < args.Count)
                    return args[++i];
                return null;
            }

            int? Int(int min)
            {
                var text = Value();
                return text is not null && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min ? n : null;
            }

            switch (arg)
            {
                case "--out": options = options with { OutputDirectory = Value() ?? "" }; break;
                case "--jobs": if (Int(1) is { } jobs) options = options with { Jobs = jobs }; else return Fail(arg, out error); break;
                case "--seed": if (Int(0) is { } seed) model = model with { Seed = seed }; else return Fail(arg, out error); break;
                case "--entities": if (Int(2) is { } entities) model = model with { Entities = entities }; else return Fail(arg, out error); break;
                case "--relations": if (Int(0) is { } relations) model = model with { Relations = relations }; else return Fail(arg, out error); break;
                case "--enums": if (Int(1) is { } enums) model = model with { Enums = enums }; else return Fail(arg, out error); break;
                case "--fanout": if (Int(1) is { } fanout) model = model with { Fanout = fanout }; else return Fail(arg, out error); break;
                case "--keep": options = options with { Keep = true }; break;
                case "--no-example-packs": model = model with { IncludeExamplePacks = false }; break;
                case "--cli": options = options with { OneShotCommand = [Value() ?? ""] }; break;
                case "--no-one-shot": options = options with { OneShotCommand = null }; break;
                case "--baseline": options = options with { BaselinePath = Value() ?? "" }; break;
                case "--report": reportPath = Value() ?? ""; break;
                case "--max-regression":
                    var text = Value();
                    if (text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct >= 0)
                        options = options with { MaxRegressionPercent = pct };
                    else
                        return Fail(arg, out error);
                    break;
                case "--format":
                    format = Value() ?? "";
                    if (format is not ("text" or "json"))
                        return Fail(arg, out error);
                    break;
                default:
                    error = "Unknown option '" + arg + "'.";
                    return false;
            }
        }

        if (options.OutputDirectory is "" || options.BaselinePath is "" || reportPath is "" || options.OneShotCommand is [""])
        {
            error = "--out, --baseline, --report and --cli need a path.";
            return false;
        }

        options = options with { Model = model };
        return true;
    }

    private static bool Fail(string option, out string error)
    {
        error = "Invalid or missing value for " + option + ".";
        return false;
    }

    /// <summary>Writes the report file through the engine-write guard, with the report's folder as the cache target.</summary>
    private static async Task WriteReportAsync(string path, byte[] json, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        var policy = new OutputPathPolicy(new EngineOptions { RepoRoot = folder, CacheDirectory = folder, JournalDirectory = folder }, null);
        var check = policy.CheckEngineWrite(WriteTarget.Cache, full);
        if (!check.Allowed)
            throw new BenchmarkException("Cannot write the report to " + full + ": " + check.Reason);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(check.NormalizedPath, json, ct).ConfigureAwait(false);
    }

    /// <summary>Prints one line to stderr when a stage starts.</summary>
    private sealed class StageProgress : IProgress<ProgressUpdate>
    {
        private readonly Lock _gate = new();
        private PipelineStage? _last;

        public void Report(ProgressUpdate value)
        {
            lock (_gate)
            {
                if (_last == value.Stage)
                    return;
                _last = value.Stage;
            }

            Console.Error.WriteLine("[" + value.Stage.ToString().ToLowerInvariant() + "]");
        }
    }
}
