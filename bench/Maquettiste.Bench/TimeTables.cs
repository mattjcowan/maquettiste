using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Bench;

/// <summary>
/// The bench app's <c>time-tables</c> verb (explorer-redesign.md section 4.5, E5c): times the table summaries of every database of a
/// model written by <c>write-model</c>, cold and after an edit, and one table's detail (E5f). The edit rewrites one entity's
/// description through the model store, so run it on a scratch copy of the model.
/// </summary>
public static class TimeTables
{
    /// <summary>The first argument that selects the verb.</summary>
    public const string Verb = "time-tables";

    /// <summary>The usage text.</summary>
    public const string Usage = "usage: Maquettiste.Bench time-tables --model <dir> [--jobs 8] [--rounds 3]\n";

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
        string? model = null;
        var jobs = Environment.ProcessorCount;
        var rounds = 3;
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            switch (args[i])
            {
                case "--model": model = value; i++; break;
                case "--jobs" when int.TryParse(value, CultureInfo.InvariantCulture, out var j) && j > 0: jobs = j; i++; break;
                case "--rounds" when int.TryParse(value, CultureInfo.InvariantCulture, out var r) && r > 0: rounds = r; i++; break;
                default:
                    await error.WriteAsync($"unknown or invalid option '{args[i]}'.\n{Usage}").ConfigureAwait(false);
                    return 4;
            }
        }

        if (string.IsNullOrEmpty(model))
        {
            await error.WriteAsync(Usage).ConfigureAwait(false);
            return 4;
        }

        var options = new EngineOptions { RepoRoot = Path.GetFullPath(model), CacheDirectory = Path.Combine(Path.GetFullPath(model), ".maquettiste", "cache", "time-tables"), MaxDegreeOfParallelism = jobs };
        var store = new ModelStore(options);
        var generation = new GenerationService(store, options);
        var tables = new DatabaseTables(generation);
        var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var databases = snapshot.All<Database>().OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var entity = snapshot.All<Entity>().OrderBy(e => e.Id, StringComparer.Ordinal).First();
        for (var round = 1; round <= rounds; round++)
        {
            foreach (var database in databases)
            {
                if (round > 1)
                    await EditAsync(store, entity.Id, round * 100 + databases.IndexOf(database), ct).ConfigureAwait(false);
                var clock = Stopwatch.StartNew();
                var result = await tables.GetAsync(database.Id, ct).ConfigureAwait(false);
                var first = clock.Elapsed;
                clock.Restart();
                await tables.GetAsync(database.Id, ct).ConfigureAwait(false);
                var again = clock.Elapsed;
                clock.Restart();
                var key = result.Tables.Count > 0 ? result.Tables[result.Tables.Count / 2].Key : "";
                var detail = await tables.GetTableAsync(database.Id, key, ct).ConfigureAwait(false);
                var table = clock.Elapsed;
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"round {round} {(round > 1 ? "after an edit" : "cold")}: {database.Name} {result.Tables.Count} tables in {first.TotalMilliseconds:F0} ms (again {again.TotalMilliseconds:F1} ms), one table {table.TotalMilliseconds:F1} ms ({detail.Table?.Columns.Count ?? 0} columns), partial {result.Partial}")).ConfigureAwait(false);
            }
        }

        // The parts: validation (shared by every database of a snapshot), one database's resolve, and the whole-model resolve (the fallback).
        snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var services = generation.Services;
        var resolver = (Maquettiste.Engine.Resolution.ModelResolver)services.Resolver;
        for (var round = 1; round <= rounds; round++)
        {
            var clock = Stopwatch.StartNew();
            await services.Validator.ValidateAsync(snapshot, new ValidationScope(), null, ct).ConfigureAwait(false);
            var validate = clock.Elapsed;
            var parts = new StringBuilder();
            foreach (var database in databases)
            {
                clock.Restart();
                await resolver.ResolveDatabaseAsync(snapshot, database.Id, ct).ConfigureAwait(false);
                parts.Append(CultureInfo.InvariantCulture, $", resolve {database.Name} {clock.Elapsed.TotalMilliseconds:F0} ms");
            }

            clock.Restart();
            await resolver.ResolveDatabaseAsync(snapshot, "", ct).ConfigureAwait(false);
            var conceptual = clock.Elapsed;
            clock.Restart();
            await services.Resolver.ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
            var whole = clock.Elapsed;
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"parts {round}: validate {validate.TotalMilliseconds:F0} ms{parts}, conceptual only {conceptual.TotalMilliseconds:F0} ms, whole model {whole.TotalMilliseconds:F0} ms")).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task EditAsync(ModelStore store, string id, int n, CancellationToken ct)
    {
        var document = (await store.GetElementAsync(id, ct).ConfigureAwait(false))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["description"] = string.Create(CultureInfo.InvariantCulture, $"Edited by time-tables ({n}).");
        await store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, ct).ConfigureAwait(false);
    }
}
