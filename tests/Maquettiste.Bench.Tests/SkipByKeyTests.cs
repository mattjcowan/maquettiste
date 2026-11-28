using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Bench.Tests;

/// <summary>
/// The skip check by key on a small synthetic model with every benchmark pack (fanout, sql-ddl, csharp-dapper): through the
/// benchmark's edit, its revert, a template edit and a settings edit, a long-lived host that skips by key plans, renders and writes
/// exactly what a host checking every unit in full does (change-driven-edits-design.md section 5).
/// </summary>
public sealed class SkipByKeyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Skipping_by_key_plans_and_writes_what_the_full_check_does_on_the_benchmark_packs()
    {
        using var temp = new TempFolder();
        var model = BenchTestModels.Small();
        EngineOptions Copy(string name)
        {
            var root = Path.Combine(temp.Path, name);
            return new EngineOptions { RepoRoot = Path.Combine(root, "repo"), CacheDirectory = Path.Combine(root, "cache"), MaxDegreeOfParallelism = 2 };
        }

        var keyed = Copy("keyed");
        var full = Copy("full");
        await SyntheticModelGenerator.WriteAsync(keyed.RepoRoot, model, Ct);
        await SyntheticModelGenerator.WriteAsync(full.RepoRoot, model, Ct);
        await using var both = new SkipEquivalence(keyed, full);

        async Task Step(string name, Func<string, Task> edit, bool byKey = true, GenerationMode mode = GenerationMode.Apply, IReadOnlyList<string>? packs = null)
        {
            await edit(keyed.RepoRoot);
            await edit(full.RepoRoot);
            var differences = await both.RunAsync(mode, packs, Ct);
            Assert.True(differences.Count == 0, name + ":\n" + string.Join('\n', differences));
            Assert.True(both.CrossCheckFailures.Count == 0, name + ": accepted by key but changed: " + string.Join(", ", both.CrossCheckFailures));
            if (byKey)
                Assert.True(both.AcceptedByKey > 0, name + ": no unit was accepted by key");
        }

        await Step("cold", _ => Task.CompletedTask, byKey: false);
        await Step("schema snapshots settle", _ => Task.CompletedTask);
        await Step("nothing changed", _ => Task.CompletedTask);
        Assert.True(both.KeysFromRecord > 0, "the record's own hashes were never compared");
        await Step("edit one entity", repo => SyntheticModelGenerator.EditOneEntityAsync(repo, model, Ct));
        await Step("revert the edit", repo => SyntheticModelGenerator.RevertEditAsync(repo, model, Ct));
        await Step("edit a fanout template", repo =>
        {
            var template = Directory.EnumerateFiles(Path.Combine(repo, ".maquettiste", "templates"), "doc.scriban", SearchOption.AllDirectories).Single();
            File.AppendAllText(template, "edited\n");
            return Task.CompletedTask;
        });
        await Step("change a setting", repo =>
        {
            var path = Path.Combine(repo, ".maquettiste", "maquettiste.json");
            var settings = JsonNode.Parse(File.ReadAllText(path))!;
            settings["properties"] = new JsonObject { ["team"] = "bench" };
            File.WriteAllText(path, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            return Task.CompletedTask;
        }, byKey: false);
        await Step("edit one entity again", repo => SyntheticModelGenerator.EditOneEntityAsync(repo, model, Ct));

        // A run cancelled once its skip check has decided writes nothing; the next run renders what the full check renders.
        await SyntheticModelGenerator.RevertEditAsync(keyed.RepoRoot, model, Ct);
        await SyntheticModelGenerator.RevertEditAsync(full.RepoRoot, model, Ct);
        await both.RunCancelledAsync(Ct);
        await Step("after a cancelled run", _ => Task.CompletedTask);

        // A run of one pack, then of all.
        await Step("one pack", repo => SyntheticModelGenerator.EditOneEntityAsync(repo, model, Ct), packs: ["fanout"]);
        await Step("all packs after one", _ => Task.CompletedTask);

        // A dry run of an edit, the edit reverted, then a run.
        await Step("dry run", repo => SyntheticModelGenerator.RevertEditAsync(repo, model, Ct), mode: GenerationMode.DryRun);
        await Step("reverted after a dry run", repo => SyntheticModelGenerator.EditOneEntityAsync(repo, model, Ct));
        await Step("nothing changed at the end", _ => Task.CompletedTask);
    }
}
