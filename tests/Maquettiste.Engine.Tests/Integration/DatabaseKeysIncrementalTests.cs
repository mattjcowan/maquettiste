using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// What a database's own record and its lists depend on (Resolution/README.md, "Dependency keys"): the database object carries only its
/// file's key, while its lists (tables, schemas, views, queries, sequences) keep the database's referrer key. Every binding names its
/// database, so a bound entity's edit moves that referrer key: a unit that lists the tables and what binds them renders again when a binding
/// comes or goes, and a unit that reads only the database's name and dialect does not render again when a bound entity's attribute changes.
/// Over the bindings fixture with a pack of two model units.
/// </summary>
public sealed class DatabaseKeysIncrementalTests
{
    private const string CustomerPath = ".maquettiste/model/entities/customer.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Binding_changes_rerender_table_listings_and_attribute_edits_leave_database_readers_alone()
    {
        using var repo = Expressions.ExpressionRuleTests.Repo();
        WriteProbePack(repo);
        var store = new ModelStore(repo.Options);
        await using (store.ConfigureAwait(false))
        {
            var service = new GenerationService(store, repo.Options);
            Assert.Equal(RunOutcome.Succeeded, (await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct)).Outcome);
            Assert.Contains("customers[Customer]", repo.ReadFile("gen/tables.txt"), StringComparison.Ordinal);
            Assert.Equal("main postgresql\n", repo.ReadFile("gen/databases.txt"));

            var dialectKeys = (await PlanAsync(service)).Units.Single(u => u.Key == "probe/databases").ReadKeys;
            Assert.DoesNotContain(dialectKeys, k => k.StartsWith("r:", StringComparison.Ordinal)); // the database's referrers are its lists'

            // A bound entity's attribute edit: the binding still names the database, so the database's referrers change.
            Expressions.ExpressionRuleTests.Edit(repo, CustomerPath, e => e["attributes"]![2]!["description"] = "Where receipts go.");
            Assert.Equal(["probe/tables"], Rendered(await PlanAsync(service)));
            Assert.Equal(RunOutcome.Succeeded, (await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct)).Outcome);

            // The binding goes: the customers table is bound by nothing, and the listing renders again; the database reader does not.
            JsonNode? binding = null;
            Expressions.ExpressionRuleTests.Edit(repo, CustomerPath, e =>
            {
                binding = e["bindings"]![0]!.DeepClone();
                e.Remove("bindings");
            });
            Assert.Equal(["probe/tables"], Rendered(await PlanAsync(service)));
            var removed = await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, removed.Outcome);
            Assert.Contains("customers[]", repo.ReadFile("gen/tables.txt"), StringComparison.Ordinal);

            // The binding comes back: the listing renders again and reads as before.
            Expressions.ExpressionRuleTests.Edit(repo, CustomerPath, e => e["bindings"] = new JsonArray(binding!));
            Assert.Equal(["probe/tables"], Rendered(await PlanAsync(service)));
            Assert.Equal(RunOutcome.Succeeded, (await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct)).Outcome);
            Assert.Contains("customers[Customer]", repo.ReadFile("gen/tables.txt"), StringComparison.Ordinal);
            Assert.Equal("main postgresql\n", repo.ReadFile("gen/databases.txt"));

            // An incremental run equals a forced one.
            var forced = await service.RunAsync(new GenerationRequest { Jobs = 2, Force = true }, null, Ct);
            Assert.All(forced.Changes, c => Assert.True(c.Kind == FileChangeKind.Kept, "forced run changed " + c.Kind + " " + c.Path));
        }
    }

    /// <summary>A pack of two model units: one lists every database's tables with the entities that bind each, one reads names and dialects.</summary>
    private static void WriteProbePack(TempRepo repo)
    {
        repo.WriteFile(".maquettiste/maquettiste.json", """
            {
              "$schema": ".schema/v1/maquettiste.json",
              "formatVersion": 1,
              "name": "bindings",
              "outputs": { "allow": [ { "path": "gen" } ] },
              "packs": { "probe": { "output": "gen" } }
            }
            """);
        repo.WriteFile(".maquettiste/templates/probe/pack.json", """
            {
              "name": "probe",
              "version": "1.0.0",
              "engine": ">=1.0 <2.0",
              "units": [
                { "id": "tables", "template": "tables.scriban", "for": "model", "output": "tables.txt" },
                { "id": "databases", "template": "databases.scriban", "for": "model", "output": "databases.txt" }
              ]
            }
            """);
        repo.WriteFile(".maquettiste/templates/probe/tables.scriban",
            "{{ for d in model.databases }}{{ for t in d.tables }}{{ t.name }}[{{ for b in t.bound_by }}{{ b.entity.name }}{{ end }}]\n{{ end }}{{ end }}");
        repo.WriteFile(".maquettiste/templates/probe/databases.scriban",
            "{{ for d in model.databases }}{{ d.name }} {{ d.dialect }}\n{{ end }}");
    }

    private static List<string> Rendered(GenerationPlan plan) =>
        [.. plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal)];

    private static async Task<GenerationPlan> PlanAsync(GenerationService service)
    {
        var planned = await service.PlanAsync(new GenerationRequest { Mode = GenerationMode.DryRun, Jobs = 2 }, null, Ct);
        return planned.Plan ?? throw new InvalidOperationException("No plan: " + planned.Outcome);
    }
}
