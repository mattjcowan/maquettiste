using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste pack test [&lt;pack&gt;...] [--case &lt;name&gt;]... [--update] [--format text|json]</c>: runs the tests of the named
/// packs, or of every pack with a <c>tests/</c> folder, through <see cref="GenerationService.TestPackAsync"/> (next-phase-2-design.md
/// section 5.4). The global <c>--jobs</c> sets each case run's parallelism. Text prints one line per case and per helper test
/// (<c>PASS</c>, <c>FAIL</c>, <c>UPDATED</c>, <c>ERROR</c>), the differing paths with their diffs under a failing case,
/// <c>file:line:column</c> and the message under a failing test, then <c>N passed, M failed</c>.
/// </summary>
internal static class PackTestCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>0 when everything passed or was updated, 1 when a case or test failed or errored, 4 for an unknown pack or case.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("pack test", Math.Max(2, context.Line.Positionals.Count), "--case", "--update", "--format");
        var packs = context.Line.Positionals.Skip(2).ToList();
        var json = context.Line.Choice("--format", "text", "text", "json") == "json";
        var options = new PackTestOptions { Cases = context.Line.Values("--case"), Update = context.Line.Has("--update"), Jobs = context.Jobs };
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var engineOptions = context.EngineOptions(repo);
        var store = new ModelStore(engineOptions);
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
            var service = new GenerationService(store, engineOptions);
            PackTestReport report;
            try
            {
                report = await service.TestPackAsync(packs, options, null, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await context.Error.WriteLineAsync("maquettiste: " + FirstSentence(ex.Message)).ConfigureAwait(false);
                return Program.ExitCodes.Internal;
            }

            if (json)
                await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer => JsonSerializer.Serialize(writer, report, CliJson.Options))).ConfigureAwait(false);
            else
                await context.Out.WriteAsync(Text(report)).ConfigureAwait(false);
            context.Info($"pack test: {report.Passed.ToString(CultureInfo.InvariantCulture)} passed, {report.Failed.ToString(CultureInfo.InvariantCulture)} failed"
                + (report.Updated > 0 ? $", {report.Updated.ToString(CultureInfo.InvariantCulture)} updated." : "."));
            return report.Failed > 0 ? Program.ExitCodes.Invalid : Program.ExitCodes.Success;
        }
    }

    /// <summary>The message of an argument exception without the parameter name the runtime appends.</summary>
    private static string FirstSentence(string message)
    {
        var at = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return at < 0 ? message : message[..at];
    }

    /// <summary>The text report: per pack, one line per case and per helper test, the details of failures, then the totals.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The text.</returns>
    internal static string Text(PackTestReport report)
    {
        var text = new StringBuilder();
        foreach (var pack in report.Packs)
        {
            text.Append(pack.Pack).Append('\n');
            foreach (var diagnostic in pack.Diagnostics.Where(d => d.Rule != "MQ6052"))
                text.Append("  ERROR ").Append(DiagnosticOutput.Line(diagnostic)).Append('\n');
            foreach (var test in pack.Cases)
            {
                text.Append("  ").Append(Word(test.Outcome)).Append(" case ").Append(test.Name);
                if (test.Description is { } description)
                    text.Append(" (").Append(description).Append(')');
                text.Append('\n');
                if (test.Outcome == PackTestOutcome.Passed)
                    continue;
                foreach (var file in test.Files)
                {
                    text.Append("    ").Append(Change(file.Change)).Append(' ').Append(file.Path).Append('\n');
                    if (test.Outcome == PackTestOutcome.Failed && file.Diff is { } diff)
                    {
                        foreach (var line in diff.TrimEnd('\n').Split('\n'))
                            text.Append("      ").Append(line).Append('\n');
                    }
                }

                if (test.Outcome == PackTestOutcome.Error)
                {
                    foreach (var diagnostic in test.Diagnostics)
                        text.Append("    ").Append(DiagnosticOutput.Line(diagnostic)).Append('\n');
                }
            }

            foreach (var test in pack.Tests)
            {
                text.Append("  ").Append(Word(test.Outcome)).Append(" test ").Append(test.File).Append(": ").Append(test.Name).Append('\n');
                if (test.Outcome == PackTestOutcome.Passed)
                    continue;
                text.Append("    ").Append(test.File);
                if (test.Line is { } line)
                {
                    text.Append(':').Append(line.ToString(CultureInfo.InvariantCulture));
                    if (test.Column is { } column)
                        text.Append(':').Append(column.ToString(CultureInfo.InvariantCulture));
                }

                text.Append(' ').Append(test.Message).Append('\n');
            }
        }

        text.Append(report.Passed.ToString(CultureInfo.InvariantCulture)).Append(" passed, ").Append(report.Failed.ToString(CultureInfo.InvariantCulture)).Append(" failed");
        if (report.Updated > 0)
            text.Append(", ").Append(report.Updated.ToString(CultureInfo.InvariantCulture)).Append(" updated");
        return text.Append('\n').ToString();
    }

    private static string Word(PackTestOutcome outcome) => outcome switch
    {
        PackTestOutcome.Passed => "PASS",
        PackTestOutcome.Failed => "FAIL",
        PackTestOutcome.Updated => "UPDATED",
        _ => "ERROR",
    };

    private static string Change(PackCaseFileChange change) => change switch
    {
        PackCaseFileChange.Added => "added",
        PackCaseFileChange.Missing => "missing",
        _ => "differs",
    };
}
