using System.Globalization;
using System.Text;
using Maquettiste.Bench;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste bench</c> (engine-design.md sections 16 and 17): runs <see cref="BenchmarkHarness.RunAsync"/> with the options given and
/// prints its report in the same text and JSON forms as <c>bench/Maquettiste.Bench</c> (<see cref="BenchmarkReportJson"/>), so the JSON
/// can be read back as a <c>--baseline</c>. Exits 0 when the report passed, 2 when a budget, the regression gate or the check run
/// failed (<see cref="BenchmarkReport.Passed"/>), 4 for invalid options or a benchmark that could not complete.
/// </summary>
internal static class BenchCommand
{
    /// <summary>
    /// The marker file the command writes into a <c>--out</c> folder. The harness deletes <c>staged/</c> and <c>pipelined/</c> in that
    /// folder, so the command only accepts a folder that is new, empty, or carries this marker from an earlier bench run.
    /// </summary>
    public const string Marker = ".maquettiste-bench";

    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("bench", 1, "--out", "--seed", "--entities", "--relations", "--enums", "--fanout", "--keep", "--baseline", "--max-regression", "--format");
        var format = context.Line.Choice("--format", "text", "text", "json");
        var defaults = new BenchmarkOptions();

        // The synthetic generator's minimums (bench Program.TryParse and SyntheticModel.Validate): 2 entities, 1 enum, fanout 1.
        var model = defaults.Model with
        {
            Seed = context.Line.Int("--seed", 0) ?? defaults.Model.Seed,
            Entities = context.Line.Int("--entities", 2) ?? defaults.Model.Entities,
            Relations = context.Line.Int("--relations", 0) ?? defaults.Model.Relations,
            Enums = context.Line.Int("--enums", 1) ?? defaults.Model.Enums,
            Fanout = context.Line.Int("--fanout", 1) ?? defaults.Model.Fanout,
        };
        var maxRelations = (long)model.Entities * (model.Entities - 1) / 4;
        if (model.Relations > maxRelations)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture,
                $"--relations {model.Relations} is too many for {model.Entities} entities (at most {maxRelations}, half the entity pairs)."));
        }

        var cwd = context.Environment.CurrentDirectory;
        var outDir = context.Line.Value("--out") is { } outText ? Path.GetFullPath(outText, cwd) : null;
        var options = defaults with
        {
            Model = model,
            Jobs = context.Jobs ?? defaults.Jobs,
            OutputDirectory = outDir,
            Keep = context.Line.Has("--keep"),
            BaselinePath = context.Line.Value("--baseline") is { } baseline ? Path.GetFullPath(baseline, cwd) : null,
            MaxRegressionPercent = context.Line.Number("--max-regression") ?? defaults.MaxRegressionPercent,
        };
        if (outDir is not null)
            await ClaimOutputFolderAsync(outDir, ct).ConfigureAwait(false);

        BenchmarkReport report;
        var progress = new ConsoleProgress(context.Progress, context.Error);
        try
        {
            report = await BenchmarkHarness.RunAsync(options, progress, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is BenchmarkException or ArgumentException)
        {
            // An invalid synthetic model or baseline, or a run that did not succeed: a message, not a stack trace.
            progress.Complete();
            await context.Error.WriteLineAsync("maquettiste: bench: " + e.Message).ConfigureAwait(false);
            return Program.ExitCodes.Internal;
        }

        progress.Complete();
        if (format == "json")
            await context.Out.WriteAsync(Encoding.UTF8.GetString(BenchmarkReportJson.Write(report))).ConfigureAwait(false);
        else
            await context.Out.WriteAsync(BenchmarkReportJson.ToText(report)).ConfigureAwait(false);
        return ExitCode(report);
    }

    /// <summary>The exit code of a report: 0 when it passed, 2 when a budget, the regression gate or the check run failed.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The exit code.</returns>
    public static int ExitCode(BenchmarkReport report) => report.Passed ? Program.ExitCodes.Success : Program.ExitCodes.Drift;

    /// <summary>
    /// Refuses a <c>--out</c> folder the harness could damage (one with content and no <see cref="Marker"/>), then writes the marker
    /// through the engine's path policy.
    /// </summary>
    /// <param name="folder">The absolute folder.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <exception cref="UsageException">The folder is a file, or holds files that are not a bench run's.</exception>
    public static async Task ClaimOutputFolderAsync(string folder, CancellationToken ct)
    {
        var marker = Path.Combine(folder, Marker);
        if (File.Exists(folder))
            throw new UsageException($"--out {folder} is a file, not a folder.");
        if (Directory.Exists(folder) && !File.Exists(marker) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new UsageException($"--out {folder} is not empty and was not made by maquettiste bench (no {Marker} file); the benchmark "
                + "deletes its staged/ and pipelined/ subfolders, so give it a new or empty folder.");
        }

        var policy = new OutputPathPolicy(new EngineOptions { RepoRoot = folder, CacheDirectory = folder, JournalDirectory = folder }, null);
        var files = new GuardedFiles(policy);
        await files.WriteAsync(WriteTarget.Cache, marker, Encoding.UTF8.GetBytes("maquettiste bench work folder: staged/ and pipelined/ are deleted and rewritten by each run.\n"),
            overwrite: false, ct).ConfigureAwait(false);
    }
}
