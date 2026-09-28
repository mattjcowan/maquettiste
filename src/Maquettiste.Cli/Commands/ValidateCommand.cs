using System.Text;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste validate [--format text|json|sarif] [--output &lt;file&gt;]</c>: validates the model (load, schema and semantic rules,
/// JavaScript rules) and loads the packs, so pack errors surface too. Exits 1 when any diagnostic is an error.
/// </summary>
internal static class ValidateCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("validate", 1, "--format", "--output");
        var format = context.Line.Choice("--format", "text", "text", "json", "sarif");
        var output = context.Line.Value("--output");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var model = await store.ValidateAsync(ValidationScope.All, ct).ConfigureAwait(false);
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var services = EngineServices.Create(options);
            var packs = await services.Packs.LoadAsync(snapshot, null, null, ct).ConfigureAwait(false);
            var report = ValidationReport.From(model.Diagnostics.Concat(packs.Diagnostics).Distinct());

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
        writer.WriteEndObject();
    });

    private static async Task<byte[]> SarifAsync(ValidationReport report, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await SarifWriter.WriteAsync(buffer, report.Diagnostics, EngineVersion.Value, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        return bytes.Length > 0 && bytes[^1] == (byte)'\n' ? bytes : [.. bytes, (byte)'\n'];
    }
}
