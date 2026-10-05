using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;

namespace Maquettiste.Bench;

/// <summary>
/// The bench app's <c>time-preview</c> verb (generation-ui.md section 5.2, "Bounds"): builds a model shaped like a large customer
/// model (the explorer's synthetic model with thousands of entities, every entity of one database stored as a designed table bound to
/// it, and one seed of 100,000 rows) and times what the Templates tab asks for while a template is being written: the template
/// context, the unit's scope listing, one element's preview cold and warm, and the first preview after a model edit. The model is
/// written to a scratch folder (<c>--out</c>, kept for a second run with <c>--reuse</c>).
/// </summary>
public static class TimePreview
{
    /// <summary>The first argument that selects the verb.</summary>
    public const string Verb = "time-preview";

    /// <summary>The usage text.</summary>
    public const string Usage = "usage: Maquettiste.Bench time-preview --out <dir> [--entities 3000] [--seed-rows 100000] [--rounds 3] [--reuse]\n";

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after <see cref="Verb"/>.</param>
    /// <param name="output">Where the timings go.</param>
    /// <param name="error">Where usage errors go.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code: 0, or 4 on a usage error.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        string? folder = null;
        var entities = 3000;
        var seedRows = 100_000;
        var rounds = 3;
        var reuse = false;
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--out": folder = value; i++; break;
                case "--reuse": reuse = true; break;
                case "--entities" when int.TryParse(value, CultureInfo.InvariantCulture, out var e) && e > 1: entities = e; i++; break;
                case "--seed-rows" when int.TryParse(value, CultureInfo.InvariantCulture, out var s) && s > 0: seedRows = s; i++; break;
                case "--rounds" when int.TryParse(value, CultureInfo.InvariantCulture, out var r) && r > 0: rounds = r; i++; break;
                default:
                    await error.WriteAsync($"unknown or invalid option '{args[i]}'.\n{Usage}").ConfigureAwait(false);
                    return 4;
            }
        }

        if (string.IsNullOrEmpty(folder))
        {
            await error.WriteAsync(Usage).ConfigureAwait(false);
            return 4;
        }

        var root = Path.GetFullPath(folder);
        if (!reuse || !Directory.Exists(Path.Combine(root, ".maquettiste", "model")))
        {
            var clock = Stopwatch.StartNew();
            await BuildAsync(root, entities, seedRows, output, ct).ConfigureAwait(false);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"built the model in {clock.Elapsed.TotalSeconds:F1} s")).ConfigureAwait(false);
        }

        for (var round = 1; round <= rounds; round++)
            await MeasureAsync(root, round, output, ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Writes the model: the synthetic explorer model, a bulk entity with a large seed, then every entity of the main database stored as a table.</summary>
    /// <param name="root">The repository folder.</param>
    /// <param name="entities">The entity count.</param>
    /// <param name="seedRows">The seed's rows.</param>
    /// <param name="output">Progress lines.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task BuildAsync(string root, int entities, int seedRows, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        var files = await WriteModel.WriteAsync(root, WriteModel.ExplorerDefaults with { Entities = entities }, ct).ConfigureAwait(false);
        var model = Path.Combine(root, ".maquettiste", "model");
        var package = Directory.EnumerateFiles(Path.Combine(model, "packages"), "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        var packageId = JsonNode.Parse(await File.ReadAllTextAsync(package, ct).ConfigureAwait(false))!["id"]!.GetValue<string>();
        await File.WriteAllTextAsync(Path.Combine(model, "entities", "bulk-item.json"), BulkEntity(packageId), ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(model, "seeds"));
        await File.WriteAllTextAsync(Path.Combine(model, "seeds", "bulk-item.json"), BulkSeed(seedRows), ct).ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"wrote {files} synthetic files, a bulk entity and a seed of {seedRows} rows")).ConfigureAwait(false);

        var options = Options(root);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var database = snapshot.All<Database>().First(d => d.Name == "main");
            var status = (await store.GetMaterializeStatusAsync(database.Id, ct).ConfigureAwait(false))!;
            // Materialize does not write inheritance hierarchies' tables yet (MQ4055): those entities stay projected.
            var all = snapshot.All<Entity>().ToList();
            var bases = all.Select(e => e.Base).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var flat = all.Where(e => e.Base is null && !bases.Contains(e.Id)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var ids = status.Entities.Where(e => e.Projected && flat.Contains(e.Id)).Select(e => e.Id).ToList();
            var stored = 0;
            foreach (var chunk in ids.Chunk(500))
            {
                var operation = "{\"operations\":[{\"op\":\"materialize-tables\",\"database\":\"" + database.Id + "\",\"entities\":[" +
                    string.Join(",", chunk.Select(id => "\"" + id + "\"")) + "]}]}";
                var parsed = store.ParseBatch(Encoding.UTF8.GetBytes(operation));
                var result = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, ct).ConfigureAwait(false);
                if (result.Outcome != SaveOutcome.Saved)
                {
                    throw new InvalidOperationException("materialize-tables was refused: " +
                        string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Take(5).Select(d => d.Rule + " " + d.Message)));
                }

                stored += chunk.Length;
            }

            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"stored {stored} entities of database main as designed tables with bindings")).ConfigureAwait(false);
        }
    }

    private static EngineOptions Options(string root) => new()
    {
        RepoRoot = root,
        CacheDirectory = Path.Combine(root, ".maquettiste", "cache", "time-preview"),
    };

    private static async Task MeasureAsync(string root, int round, TextWriter output, CancellationToken ct)
    {
        var options = Options(root);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var generation = new GenerationService(store, options);
            var clock = Stopwatch.StartNew();
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var load = clock.Elapsed;
            var entity = snapshot.All<Entity>().Where(e => e.Name != "BulkItem").OrderBy(e => e.Id, StringComparer.Ordinal).First();
            var table = snapshot.All<Table>().OrderBy(t => t.Id, StringComparer.Ordinal).First();
            var lines = new List<string> { $"round {round}: load {load.TotalMilliseconds:F0} ms, {snapshot.Documents.Count} documents, {snapshot.All<Table>().Count()} table files" };

            // Opening a template: what the Templates tab sends at once (context, the scope listing, the preview of one element).
            async Task<string> Open(string pack, string unit, string element)
            {
                var clock = Stopwatch.StartNew();
                var context = Time(() => generation.GetTemplateContextAsync(pack, unit, ct));
                var paths = Time(() => generation.PathsAsync(pack, unit, null, null, 200, ct));
                var preview = Time(() => generation.PreviewAsync(pack, unit, element, null, ct));
                await Task.WhenAll(context, paths, preview).ConfigureAwait(false);
                var (c, cMs) = await context.ConfigureAwait(false);
                var (p, pMs) = await paths.ConfigureAwait(false);
                var (r, rMs) = await preview.ConfigureAwait(false);
                return $"open {pack}/{unit} {clock.Elapsed.TotalMilliseconds:F0} ms (context {cMs:F0}, paths {pMs:F0} [{p.Rendered} rendered of {p.Count}], preview {rMs:F0} [{r.Files.Count} files, {Errors(r.Diagnostics)}]){(c is null ? " no context" : "")}";
            }

            async Task<string> Preview(string label, string pack, string unit, string element)
            {
                var (r, ms) = await Time(() => generation.PreviewAsync(pack, unit, element, null, ct)).ConfigureAwait(false);
                return $"{label} {pack}/{unit} {ms:F0} ms [{r.Files.Count} files, {Errors(r.Diagnostics)}]";
            }

            lines.Add(await Open("csharp-dapper", "entity", entity.Id).ConfigureAwait(false));
            lines.Add(await Preview("  warm preview", "csharp-dapper", "entity", entity.Id).ConfigureAwait(false));
            lines.Add(await Open("sql-ddl", "table", table.Id).ConfigureAwait(false));
            lines.Add(await Preview("  warm preview", "sql-ddl", "table", table.Id).ConfigureAwait(false));
            lines.Add(await Preview("  warm preview again", "sql-ddl", "table", table.Id).ConfigureAwait(false));
            if (Environment.GetEnvironmentVariable("MQ_TIME_PREVIEW_LOOP") is { } loop && int.TryParse(loop, CultureInfo.InvariantCulture, out var count))
            {
                var all = Stopwatch.StartNew();
                for (var i = 0; i < count; i++)
                    await generation.PreviewAsync("sql-ddl", "table", table.Id, null, ct).ConfigureAwait(false);
                lines.Add($"  {count} warm previews: {all.Elapsed.TotalMilliseconds / count:F0} ms each");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            lines.Add(await Preview("  idle 3 s, preview", "sql-ddl", "table", table.Id).ConfigureAwait(false));
            var (paths, pathsMs) = await Time(() => generation.PathsAsync("sql-ddl", "table", null, null, 200, ct)).ConfigureAwait(false);
            lines.Add($"  paths sql-ddl/table {pathsMs:F0} ms [{paths.Rendered} rendered of {paths.Count}]");

            await EditAsync(store, entity.Id, round, ct).ConfigureAwait(false);
            lines.Add(await Preview("  after a model edit, preview", "sql-ddl", "table", table.Id).ConfigureAwait(false));
            lines.Add(await Preview("  after a model edit, warm preview", "sql-ddl", "table", table.Id).ConfigureAwait(false));
            // For comparison: what a full dry-run preparation of the sql-ddl pack costs, stage by stage (what Generate pays).
            var run = new Maquettiste.Engine.Generation.GenerationRun(generation.Services, store, null);
            clock.Restart();
            var prepared = await run.PrepareAsync(["sql-ddl"], Maquettiste.Engine.Pipeline.GenerationMode.DryRun, ct, locked: false).ConfigureAwait(false);
            lines.Add($"  full dry-run preparation of sql-ddl {clock.Elapsed.TotalMilliseconds:F0} ms ({prepared?.Plan.Units.Count ?? 0} units planned): " +
                string.Join(", ", run.Clock.Timings().Select(t => $"{t.Stage} {t.Wall.TotalMilliseconds:F0}")) +
                (prepared is null ? "; " + Errors(run.Diagnostics) : ""));
            foreach (var line in lines)
                await output.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static string Errors(IReadOnlyList<Maquettiste.Engine.Diagnostics.Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == Maquettiste.Engine.Diagnostics.DiagnosticSeverity.Error).ToList();
        return errors.Count == 0 ? "no errors" : $"{errors.Count} errors: {errors[0].Rule} {errors[0].Message}";
    }

    private static async Task<(T Result, double Ms)> Time<T>(Func<Task<T>> body)
    {
        var clock = Stopwatch.StartNew();
        var result = await body().ConfigureAwait(false);
        return (result, clock.Elapsed.TotalMilliseconds);
    }

    private static async Task EditAsync(ModelStore store, string id, int n, CancellationToken ct)
    {
        var document = (await store.GetElementAsync(id, ct).ConfigureAwait(false))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["description"] = string.Create(CultureInfo.InvariantCulture, $"Edited by time-preview ({n}).");
        await store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, ct).ConfigureAwait(false);
    }

    private const string EntityId = "01K8B0000000000000000000E0";
    private const string SeedId = "01K8B0000000000000000000S0";

    private static string AttributeId(int n) => "01K8B00000000000000000000" + n.ToString(CultureInfo.InvariantCulture);

    private static string BulkEntity(string packageId) => $$"""
        {
          "$schema": "../../.schema/v1/entity.json",
          "kind": "entity",
          "id": "{{EntityId}}",
          "name": "BulkItem",
          "package": "{{packageId}}",
          "key": { "attributes": ["{{AttributeId(4)}}"], "strategy": "application" },
          "attributes": [
            { "id": "{{AttributeId(1)}}", "name": "code", "type": "string", "length": 32, "required": true },
            { "id": "{{AttributeId(2)}}", "name": "label", "type": "string", "length": 200 },
            { "id": "{{AttributeId(3)}}", "name": "amount", "type": "decimal", "precision": 18, "scale": 2 },
            { "id": "{{AttributeId(4)}}", "name": "counter", "type": "int64" }
          ]
        }
        """;

    private static string BulkSeed(int rows)
    {
        var text = new StringBuilder(rows * 96);
        text.Append(CultureInfo.InvariantCulture, $$"""
            {
              "$schema": "../../.schema/v1/seed.json",
              "kind": "seed",
              "id": "{{SeedId}}",
              "name": "BulkItem",
              "target": "{{EntityId}}",
              "columns": ["{{AttributeId(1)}}", "{{AttributeId(2)}}", "{{AttributeId(3)}}", "{{AttributeId(4)}}"],
              "rows": [
            """);
        text.Append('\n');
        for (var i = 0; i < rows; i++)
        {
            // Row ids: valid ULIDs, distinct per row (digits only, so no excluded letter).
            var id = "01K8C" + i.ToString("D21", CultureInfo.InvariantCulture);
            text.Append(CultureInfo.InvariantCulture, $"    {{ \"id\": \"{id}\", \"values\": [\"item-{i}\", \"Item {i}\", {i % 1000}.25, {i}] }}");
            text.Append(i + 1 < rows ? ",\n" : "\n");
        }

        text.Append("  ]\n}\n");
        return text.ToString();
    }
}
