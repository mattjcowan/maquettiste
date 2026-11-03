using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste pack exports &lt;pack&gt; [--element &lt;id&gt;]... [--format text|json]</c>: the pack's export records for the elements
/// asked (at most <see cref="GenerationService.MaxRenderedPaths"/>), or its model-scope record without <c>--element</c>, evaluated now
/// through <see cref="GenerationService.GetPackExportsAsync"/> (next-phase-2-design.md section 5.3). Text prints one block per element
/// (<c>Invoice (entity)</c>) and one line per value (<c>class_name = InvoiceRecord</c>, <c>members/&lt;attribute id&gt; = Number</c>).
/// </summary>
internal static class PackExportsCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>0 when every record evaluated, 1 when one failed or the pack does not load.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("pack exports", 3, "--element", "--format");
        var name = context.Line.Positionals[2];
        var json = context.Line.Choice("--format", "text", "text", "json") == "json";
        var elements = context.Line.Values("--element");
        if (elements.Count > GenerationService.MaxRenderedPaths)
            throw new UsageException($"pack exports takes at most {GenerationService.MaxRenderedPaths} --element values.");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
            var service = new GenerationService(store, options);
            PackExportsResult result;
            try
            {
                result = await service.GetPackExportsAsync(name, elements, ct).ConfigureAwait(false);
            }
            catch (PackPathException ex)
            {
                await context.Error.WriteLineAsync("maquettiste: " + ex.Message).ConfigureAwait(false);
                return Program.ExitCodes.Internal;
            }

            if (json)
            {
                await context.Out.WriteLineAsync(DiagnosticOutput.ToJson(writer => JsonSerializer.Serialize(writer, result, CliJson.Options))).ConfigureAwait(false);
            }
            else
            {
                await context.Out.WriteAsync(Text(result)).ConfigureAwait(false);
                foreach (var diagnostic in result.Diagnostics)
                    await context.Error.WriteLineAsync(DiagnosticOutput.Line(diagnostic)).ConfigureAwait(false);
            }

            var failed = result.Diagnostics.Any(d => d.Severity == Engine.Diagnostics.DiagnosticSeverity.Error);
            context.Info($"pack exports {name}: {result.Elements.Count.ToString(CultureInfo.InvariantCulture)} {(result.Elements.Count == 1 ? "record" : "records")}{(failed ? ", failed" : "")}.");
            return failed ? Program.ExitCodes.Invalid : Program.ExitCodes.Success;
        }
    }

    /// <summary>One block per element: its label, then one line per value, an <c>each</c> map as one line per item id.</summary>
    /// <param name="result">The records.</param>
    /// <returns>The text.</returns>
    internal static string Text(PackExportsResult result)
    {
        var text = new StringBuilder();
        foreach (var element in result.Elements)
        {
            if (text.Length > 0)
                text.Append('\n');
            text.Append(element.ElementId is null ? "model" : element.Name is { } label ? label + " (" + element.Kind + ")" : element.ElementId).Append('\n');
            if (element.Values is not { ValueKind: JsonValueKind.Object } values)
            {
                text.Append("  (no exports)\n");
                continue;
            }

            foreach (var value in values.EnumerateObject())
            {
                if (value.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var item in value.Value.EnumerateObject())
                        text.Append("  ").Append(value.Name).Append('/').Append(item.Name).Append(" = ").Append(item.Value.GetString()).Append('\n');
                }
                else
                {
                    text.Append("  ").Append(value.Name).Append(" = ").Append(value.Value.GetString()).Append('\n');
                }
            }
        }

        return text.ToString();
    }
}
