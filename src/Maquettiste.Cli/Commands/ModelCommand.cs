using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste model export|stats</c>: the whole model, or a filtered part of it, as data for another system. <c>export</c> writes the
/// canonical documents (or, with <c>--resolved</c>, the resolved model's flat records) as one JSON array or as one JSON value per line;
/// <c>stats</c> counts the kinds present, per package with <c>--by package</c>. Both read the same pages the editor API and the MCP
/// server serve (<c>GET /api/model/elements</c>, <c>/api/model/resolved</c>, <c>/api/model/kinds</c>).
/// </summary>
internal static class ModelCommand
{
    /// <summary>The options of the JSON written: the API's names, every property written, text not escaped.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Runs <c>model &lt;verb&gt;</c>.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        var verb = line.Positionals.Count > 1 ? line.Positionals[1] : throw new UsageException("'model' needs a verb: export or stats.");
        switch (verb)
        {
            case "export":
                line.Expect("model export", 2, "--format", "--out", "--kind", "--package", "--tag", "--category", "--stereotype", "--query", "--ids", "--fields",
                    "--resolved", "--scope", "--database");
                break;
            case "stats":
                line.Expect("model stats", 2, "--format", "--by");
                break;
            default:
                throw new UsageException($"Unknown model verb '{verb}': use export or stats.");
        }

        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;
        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            if (verb == "stats")
                return await StatsAsync(context, snapshot, ct).ConfigureAwait(false);
            var ndjson = line.Choice("--format", "json", "json", "ndjson") == "ndjson";
            if (line.Has("--resolved"))
            {
                foreach (var option in new[] { "--kind", "--package", "--tag", "--category", "--stereotype", "--query", "--ids", "--fields" })
                {
                    if (line.Has(option))
                        throw new UsageException($"{option} does not apply to --resolved; use --scope and --database.");
                }

                return await ExportResolvedAsync(context, store, options, ndjson, ct).ConfigureAwait(false);
            }

            if (line.Has("--scope") || line.Has("--database"))
                throw new UsageException("--scope and --database apply to --resolved only.");
            return await ExportDocumentsAsync(context, snapshot, ndjson, ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> ExportDocumentsAsync(GlobalContext context, Engine.Model.ModelSnapshot snapshot, bool ndjson, CancellationToken ct)
    {
        var line = context.Line;
        var filter = new ElementFilter(line.Value("--kind"), line.Value("--package"), line.Value("--tag"), line.Value("--category"), line.Value("--stereotype"), line.Value("--query"));
        var ids = ModelPages.SplitList(line.Value("--ids"));
        var fields = ModelPages.SplitList(line.Value("--fields"));
        var values = new List<string>();
        string? cursor = null;
        ElementPage page;
        do
        {
            ct.ThrowIfCancellationRequested();
            page = ModelPages.ReadElements(snapshot, ids, filter, fields, cursor, ModelPages.MaxLimit);
            values.AddRange(page.Items.Select(i => JsonSerializer.Serialize(i.Json, Options)));
            cursor = page.Next;
        }
        while (cursor is not null);

        foreach (var id in page.Missing)
            await context.Error.WriteLineAsync($"maquettiste: no element has the id {id}.").ConfigureAwait(false);
        await CliFiles.EmitAsync(context, Join(values, ndjson), ct).ConfigureAwait(false);
        context.Info(string.Create(CultureInfo.InvariantCulture, $"Exported {values.Count} {(values.Count == 1 ? "document" : "documents")}."));
        return page.Missing.Count > 0 ? Program.ExitCodes.Invalid : Program.ExitCodes.Success;
    }

    private static async Task<int> ExportResolvedAsync(GlobalContext context, ModelStore store, EngineOptions options, bool ndjson, CancellationToken ct)
    {
        var line = context.Line;
        var scope = line.Value("--scope") ?? "all";
        if (!ResolvedRecords.ScopeNames.Contains(scope, StringComparer.Ordinal))
            throw new UsageException($"The option --scope must be one of {string.Join(", ", ResolvedRecords.ScopeNames)}; got '{scope}'.");
        var database = line.Value("--database");
        if (database is not null)
        {
            var index = await store.GetIndexAsync(ct).ConfigureAwait(false);
            var matches = index.Where(s => s.Kind == "database" && (s.Id == database || string.Equals(s.Name, database, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count != 1)
                throw new UsageException(matches.Count == 0 ? $"no database has the id or name '{database}'." : $"'{database}' names {matches.Count} databases; pass one id.");
            database = matches[0].Id;
        }

        var generation = new GenerationService(store, options);
        var values = new List<string>();
        string? cursor = null;
        do
        {
            var page = await generation.GetResolvedAsync(new ResolvedQuery(scope, database, cursor, ModelPages.MaxLimit), ct).ConfigureAwait(false);
            if (page.Diagnostics.Count > 0)
            {
                foreach (var d in page.Diagnostics)
                    await context.Error.WriteLineAsync(DiagnosticOutput.Line(d)).ConfigureAwait(false);
                await context.Error.WriteLineAsync("maquettiste: the model has errors, so it cannot be resolved; fix them (maquettiste validate) and export again.").ConfigureAwait(false);
                return Program.ExitCodes.Invalid;
            }

            values.AddRange(page.Items.Select(i => JsonSerializer.Serialize(i, Options)));
            cursor = page.Next;
        }
        while (cursor is not null);

        await CliFiles.EmitAsync(context, Join(values, ndjson), ct).ConfigureAwait(false);
        context.Info(string.Create(CultureInfo.InvariantCulture, $"Exported {values.Count} resolved {(values.Count == 1 ? "record" : "records")} ({scope})."));
        return Program.ExitCodes.Success;
    }

    private static async Task<int> StatsAsync(GlobalContext context, Engine.Model.ModelSnapshot snapshot, CancellationToken ct)
    {
        var line = context.Line;
        var byPackage = line.Choice("--by", "kind", "kind", "package") == "package";
        var json = line.Choice("--format", "text", "text", "json") == "json";
        var result = ModelPages.Kinds(snapshot.Summaries(), byPackage);
        var text = new StringBuilder();
        if (json)
        {
            text.Append(JsonSerializer.Serialize(result, Options)).Append('\n');
        }
        else
        {
            Counts(text, "", result.Kinds);
            text.Append(CultureInfo.InvariantCulture, $"{"total",-24} {result.Total,7}\n");
            foreach (var package in result.Packages ?? [])
            {
                text.Append('\n').Append(package.Name ?? "(no package)");
                if (package.Package is not null)
                    text.Append(" (").Append(package.Package).Append(')');
                text.Append(CultureInfo.InvariantCulture, $": {package.Count}\n");
                Counts(text, "  ", package.Kinds);
            }
        }

        await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        await context.Out.FlushAsync(ct).ConfigureAwait(false);
        return Program.ExitCodes.Success;
    }

    private static void Counts(StringBuilder text, string indent, IReadOnlyList<KindCount> kinds)
    {
        foreach (var kind in kinds)
            text.Append(indent).Append(CultureInfo.InvariantCulture, $"{kind.Kind,-24} {kind.Count,7}\n");
    }

    /// <summary>A JSON array with one value per line, or one value per line (NDJSON); either ends with a line feed.</summary>
    private static string Join(List<string> values, bool ndjson)
    {
        if (ndjson)
            return values.Count == 0 ? "" : string.Join('\n', values) + "\n";
        return values.Count == 0 ? "[]\n" : "[\n" + string.Join(",\n", values) + "\n]\n";
    }
}
