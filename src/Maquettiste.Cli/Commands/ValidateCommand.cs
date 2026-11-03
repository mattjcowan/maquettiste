using System.Text;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste validate [--against &lt;snapshot id | folder&gt;] [--format text|json|sarif] [--output &lt;file&gt;]</c>: validates
/// the model (load, schema and semantic rules, JavaScript rules) and loads the packs, so pack errors surface too, and warns MQ1008
/// when <c>.maquettiste/.schema/v1/</c> differs from the embedded schemas (it never refreshes them). <c>--against</c> also compares
/// the model with a baseline, a model snapshot or a folder holding another copy of the project (<see cref="ModelBaseline"/>): a change
/// since it that no migration fact explains is MQ4102 (next-phase-1-design.md section 3.2). Exits 1 when any diagnostic is an error,
/// 4 when <c>--against</c> names nothing.
/// </summary>
internal static class ValidateCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("validate", 1, "--format", "--output", "--against");
        var format = context.Line.Choice("--format", "text", "text", "json", "sarif");
        var output = context.Line.Value("--output");
        var against = context.Line.Value("--against");
        if (against is { Length: 0 })
            throw new UsageException("--against needs a model snapshot id or a folder.");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            ValidationReport model;
            if (against is null)
            {
                model = await store.ValidateAsync(ValidationScope.All, ct).ConfigureAwait(false);
            }
            else
            {
                ModelBaseline baseline;
                try
                {
                    // A relative folder is resolved against the current directory; the command line accepts folders.
                    baseline = await ModelBaseline.OpenAsync(store, against, context.Environment.CurrentDirectory, allowFolders: true, library: null, ct)
                        .ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    throw new UsageException(ex.Message);
                }

                using (baseline)
                    model = await store.ValidateAgainstAsync(baseline, ct).ConfigureAwait(false);
                context.Info("Compared with the baseline: " + baseline.Label + ".");
            }

            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var services = EngineServices.Create(options);
            var packs = await services.Packs.LoadAsync(snapshot, null, null, ct).ConfigureAwait(false);
            // MQ1008: the .schema copies differ from the embedded schemas. Reported only; validate never writes them.
            var schemaFindings = SchemaFolder.Findings(options, snapshot.Settings);
            // The merge keeps the model's suppressed findings (next-phase-2-design.md section 5.6).
            var report = ValidationReport.From(model.Diagnostics.Concat(packs.Diagnostics).Concat(schemaFindings).Distinct(), model.Suppressed);

            var bytes = format switch
            {
                "json" => Encoding.UTF8.GetBytes(Json(report) + "\n"),
                "sarif" => await SarifAsync(report, ct).ConfigureAwait(false),
                _ => Encoding.UTF8.GetBytes(string.Concat(report.Diagnostics.Select(d => DiagnosticOutput.Line(d) + "\n"))),
            };

            if (output is not null)
            {
                var full = Path.GetFullPath(output, context.Environment.CurrentDirectory);
                var files = new GuardedFiles(services.CreatePathPolicy(snapshot.Settings));
                await files.WriteAsync(WriteTarget.Output, full, bytes, overwrite: true, ct).ConfigureAwait(false);
                context.Info("Wrote " + full + ".");
            }
            else
            {
                await context.Out.WriteAsync(Encoding.UTF8.GetString(bytes)).ConfigureAwait(false);
                await context.Out.FlushAsync(ct).ConfigureAwait(false);
            }

            context.Info((report.HasErrors ? "Validation failed: " : "Validation passed: ") + DiagnosticOutput.Counts(report.Errors, report.Warnings, report.Infos) + ".");
            if (report.Suppressed.Count > 0)
                context.Info(DiagnosticOutput.SuppressedCount(report.Suppressed.Count) + ".");
            return report.HasErrors ? Program.ExitCodes.Invalid : Program.ExitCodes.Success;
        }
    }

    /// <summary>The <c>diagnostics.json</c> document.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The JSON text.</returns>
    public static string Json(ValidationReport report) => DiagnosticOutput.ToJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("errors", report.Errors);
        writer.WriteNumber("warnings", report.Warnings);
        writer.WriteNumber("infos", report.Infos);
        DiagnosticOutput.WriteArray(writer, "diagnostics", report.Diagnostics);
        if (report.Suppressed.Count > 0)
            DiagnosticOutput.WriteSuppressed(writer, report.Suppressed);
        writer.WriteEndObject();
    });

    private static async Task<byte[]> SarifAsync(ValidationReport report, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await SarifWriter.WriteAsync(buffer, report, EngineVersion.Value, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        return bytes.Length > 0 && bytes[^1] == (byte)'\n' ? bytes : [.. bytes, (byte)'\n'];
    }
}
