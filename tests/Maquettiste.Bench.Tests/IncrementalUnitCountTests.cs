using System.Text.RegularExpressions;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Bench.Tests;

/// <summary>
/// The benchmark's incremental measure on a small synthetic model with every pack it generates (fanout, sql-ddl, csharp-dapper):
/// after the benchmark's own one-entity edit, the units that re-render are exactly the units whose recorded reads changed (the
/// edited file's <c>e:</c> key, the <c>r:</c> keys of the ids it references, and at most the schema-diff <c>d:</c> keys), never a
/// unit that did not read the edit; and the pack that reads no reference data or localization carries none of those keys
/// (engine-design.md section 11, "Which units an entity edit re-renders").
/// </summary>
public sealed partial class IncrementalUnitCountTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_entity_edit_rerenders_only_the_units_that_read_it()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        var model = BenchTestModels.Small();
        Assert.True(model.IncludeExamplePacks);
        await SyntheticModelGenerator.WriteAsync(repo, model, Ct);

        var options = temp.Options(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var service = new GenerationService(store, options);
            var cold = await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, cold.Outcome);

            // The cold run saved the schema snapshots, which empties the diffs the migration units read: one more run settles them.
            Assert.Equal(RunOutcome.Succeeded, (await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct)).Outcome);
            var before = await PlanAsync(service);
            Assert.All(before.Units, u => Assert.True(u.Skipped, u.Key));
            var s0 = await store.GetSnapshotAsync(Ct);

            var edited = await SyntheticModelGenerator.EditOneEntityAsync(repo, model, Ct);
            var s1 = await store.GetSnapshotAsync(Ct);
            var referenced = ReferencedIds(s0, edited).Union(ReferencedIds(s1, edited)).ToHashSet(StringComparer.Ordinal);

            // Must render: a changed e:, r: or l: key. May render besides: a unit reading a schema diff (d:), which the edit can change.
            bool Changed(string key) => key[..2] switch
            {
                "e:" => !string.Equals(s0.GetDocument(key[2..])?.DependencyHash, s1.GetDocument(key[2..])?.DependencyHash, StringComparison.Ordinal),
                "r:" => referenced.Contains(key[2..]) && !string.Equals(s0.ReferrersHash(key[2..]), s1.ReferrersHash(key[2..]), StringComparison.Ordinal),
                "l:" => key.EndsWith(":" + edited, StringComparison.Ordinal),
                _ => false,
            };
            var must = before.Units.Where(u => u.ReadKeys.Any(Changed)).Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
            var may = before.Units.Where(u => must.Contains(u.Key) || u.ReadKeys.Any(k => k.StartsWith("d:", StringComparison.Ordinal)))
                .Select(u => u.Key).ToHashSet(StringComparer.Ordinal);

            var plan = await PlanAsync(service);
            var rendered = plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
            Assert.Contains("fanout/u01:" + edited, rendered);
            Assert.Empty(must.Except(rendered, StringComparer.Ordinal)); // every reader of the edit renders
            Assert.Empty(rendered.Except(may, StringComparer.Ordinal)); // and nothing else does
            Assert.True(rendered.Count <= may.Count && rendered.Count < plan.Units.Count / 4, $"{rendered.Count} of {plan.Units.Count} units re-render");

            // Reference-data and localization keys attach only to units that read those facts: the fanout pack reads neither.
            Assert.All(plan.Units.Where(u => u.Key.StartsWith("fanout/", StringComparison.Ordinal)), u => Assert.DoesNotContain(u.ReadKeys, k =>
                k is "s:referenceData" or "s:localization" || k.StartsWith("l:", StringComparison.Ordinal)));

            var result = await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, result.Outcome);
            Assert.Equal(rendered.Count, result.UnitsRendered);
            Assert.Equal(plan.Units.Count - rendered.Count, result.UnitsSkipped);
        }
    }

    private static async Task<GenerationPlan> PlanAsync(GenerationService service)
    {
        var planned = await service.PlanAsync(new GenerationRequest { Mode = GenerationMode.DryRun, Jobs = 2 }, null, Ct);
        return planned.Plan ?? throw new InvalidOperationException("No plan: " + planned.Outcome);
    }

    private static IEnumerable<string> ReferencedIds(ModelSnapshot snapshot, string id) =>
        snapshot.GetDocument(id) is { } document ? Ulid().Matches(document.Json.GetRawText()).Select(m => m.Value) : [];

    [GeneratedRegex("[0-9A-HJKMNP-TV-Z]{26}")]
    private static partial Regex Ulid();
}
