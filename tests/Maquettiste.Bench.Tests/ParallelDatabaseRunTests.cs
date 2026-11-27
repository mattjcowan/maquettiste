using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Bench.Tests;

/// <summary>
/// The resolver runs a model's databases side by side when it may (Resolution/README.md, "Bindings at benchmark scale"): over the small
/// synthetic model (three databases with designed tables, bindings and relation mappings, and every example pack), repeated runs with
/// parallelism give the same generated files, manifests (which hold every unit's input hash, so every dependency key it read) and resolved
/// records as a run with parallelism 1, which resolves the databases one by one.
/// </summary>
public sealed class ParallelDatabaseRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Databases_resolved_side_by_side_give_the_one_by_one_result_every_time()
    {
        var model = BenchTestModels.Small();
        var sequential = await RunAsync(model, parallelism: 1);
        Assert.Contains(sequential.Files, f => f.Key.StartsWith("db/", StringComparison.Ordinal)); // sql-ddl wrote per-database files
        for (var round = 0; round < 4; round++)
        {
            var parallel = await RunAsync(model, parallelism: 8);
            Assert.Equal(sequential.Files.Keys, parallel.Files.Keys);
            foreach (var (path, bytes) in sequential.Files)
                Assert.True(bytes.AsSpan().SequenceEqual(parallel.Files[path]), $"round {round}: {path} differs");
            Assert.Equal(sequential.Resolved, parallel.Resolved);
        }
    }

    private static async Task<(SortedDictionary<string, byte[]> Files, string Resolved)> RunAsync(SyntheticModelOptions model, int parallelism)
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await SyntheticModelGenerator.WriteAsync(repo, model, Ct);
        var options = temp.Options(repo) with { MaxDegreeOfParallelism = parallelism };
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var service = new GenerationService(store, options);
            var result = await service.RunAsync(new GenerationRequest { Jobs = parallelism }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, result.Outcome);
            var records = new List<string>();
            string? cursor = null;
            do
            {
                var page = await service.GetResolvedAsync(new ResolvedQuery("all", Cursor: cursor, Limit: 500), Ct);
                Assert.Empty(page.Diagnostics);
                records.AddRange(page.Items.Select(i => JsonSerializer.Serialize(i, i.GetType())));
                cursor = page.Next;
            }
            while (cursor is not null);

            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repo, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith(".maquettiste/model/", StringComparison.Ordinal) || relative.Contains("/.cache/", StringComparison.Ordinal))
                    continue; // the model itself, written by the generator
                files[relative] = await File.ReadAllBytesAsync(file, Ct);
            }

            return (files, string.Join('\n', records));
        }
    }
}
