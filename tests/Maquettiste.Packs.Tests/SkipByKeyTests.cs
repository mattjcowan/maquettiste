using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The skip check by key on the billing fixture with the example packs (change-driven-edits-design.md section 5): through a sequence
/// of edits, a long-lived host that skips by key plans, renders and writes exactly what a host checking every unit in full does, and
/// none of the units it accepts by key fails the full check.
/// </summary>
public sealed class SkipByKeyTests
{
    private const string CustomerPath = ".maquettiste/model/entities/customer.json";
    private const string CustomerName = "01J92P0V0MS09YFZHX07JQ3KMN";
    private const string CustomerSince = "01J92P0V0P4MBJRM6QN6H0RSVP";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Skipping_by_key_plans_and_writes_what_the_full_check_does_through_a_sequence_of_edits()
    {
        using var keyed = PackRepo.Billing();
        using var full = PackRepo.Billing();
        await using var both = new SkipEquivalence(keyed.Repo.Options, full.Repo.Options);

        async Task Step(string name, Action<PackRepo> edit, bool byKey = true)
        {
            edit(keyed);
            edit(full);
            var differences = await both.RunAsync(Ct);
            Assert.True(differences.Count == 0, name + ":\n" + string.Join('\n', differences));
            Assert.True(both.CrossCheckFailures.Count == 0, name + ": accepted by key but changed: " + string.Join(", ", both.CrossCheckFailures));
            if (byKey)
                Assert.True(both.AcceptedByKey > 0, name + ": no unit was accepted by key");
        }

        await Step("cold", _ => { }, byKey: false);
        await Step("nothing changed", _ => { });
        await Step("change an attribute", r =>
        {
            r.EditJson(CustomerPath, c => c["attributes"]!.AsArray().Single(a => (string?)a!["id"] == CustomerName)!["length"] = 150);
            ModelChanges.EditCustomerColumn(r, CustomerName, column => column["length"] = 150);
        });
        await Step("rename an entity", r => r.EditJson(".maquettiste/model/entities/product.json", p => p["name"] = "CatalogItem"));
        await Step("add a relation mapping", ModelChanges.AddFeaturedInvoice);
        // The mapping goes with its relation (a relation bound on both ends needs one); the foreign key stays a table's own.
        await Step("remove a relation mapping", r =>
        {
            foreach (var file in Directory.EnumerateFiles(r.PathOf(".maquettiste/model/mappings"), "features-*.json"))
                File.Delete(file);
            File.Delete(r.PathOf(".maquettiste/model/relations/features.json"));
        });
        await Step("change a binding", r => r.EditJson(CustomerPath, c =>
        {
            var fields = c["bindings"]![0]!["fields"]!.AsArray();
            fields.Remove(fields.Single(f => (string?)f!["attribute"] == CustomerSince));
        }));
        await Step("change a table", r => r.EditJson(".maquettiste/model/databases/main/tables/products.json", t =>
            t["columns"]!.AsArray().Add(new JsonObject { ["id"] = "01J92P0V2K0000000000000001", ["name"] = "shelf_code", ["type"] = "string", ["length"] = 12 })));
        await Step("add an element", r => r.Write(".maquettiste/model/entities/coupon.json", """
            {
              "kind": "entity",
              "id": "01J92P0V2K0000000000000002",
              "name": "Coupon",
              "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "key": { "attributes": ["01J92P0V2K0000000000000004"], "strategy": "uuid-v7" },
              "attributes": [
                { "id": "01J92P0V2K0000000000000004", "name": "id", "type": "uuid", "required": true },
                { "id": "01J92P0V2K0000000000000003", "name": "code", "type": "string", "length": 20, "required": true }
              ]
            }

            """));
        await Step("delete an element", r => File.Delete(r.PathOf(".maquettiste/model/entities/coupon.json")));
        // Other settings: the record of the previous run is not used, every unit takes the full check.
        await Step("change a setting", r => r.EditJson(".maquettiste/maquettiste.json", s => s["properties"] = new JsonObject { ["team"] = "billing" }),
            byKey: false);
        await Step("after a setting", _ => { });
        await Step("change a pack template", r => File.AppendAllText(r.PathOf(".maquettiste/templates/csharp-dapper/enum.scriban"), "// edited\n"));
        await Step("edit the same attribute again", r =>
            r.EditJson(CustomerPath, c => c["attributes"]!.AsArray().Single(a => (string?)a!["id"] == CustomerName)!["description"] = "The customer's name."));
    }
}
