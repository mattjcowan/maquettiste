using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance case of seeds by natural key (next-phase-2-design.md section 3.4, D45; scope "Done when"): invoices name their customer
/// by email, both tables take stable ids, and no seed file holds an id. The tables sit in schema sales of main (decision 23), since billing
/// projects its own Customer and Invoice entities into billing.customers and billing.invoices.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed partial class SeedNaturalKeyTests
{
    private const string Sales = "01K9SNK0000000000000000099";
    private const string Customers = "01K9SNK0000000000000000001";
    private const string Invoices = "01K9SNK0000000000000000010";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{26}$")]
    private static partial Regex Ulid();

    [Fact]
    public async Task An_invoice_seed_names_its_customers_by_email_and_carries_no_ids()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: E2ERepo.RecordOff);
        await repo.EditAsync(E2ERepo.MainDatabaseId, n => n["schemas"]!.AsArray().Add(new JsonObject { ["id"] = Sales, ["name"] = "sales" }));
        await repo.CreateElementAsync($$"""
            {"kind":"table","id":"{{Customers}}","name":"customers","database":"{{E2ERepo.MainDatabaseId}}","schema":"{{Sales}}","columns":[
              {"id":"01K9SNK0000000000000000002","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"01K9SNK0000000000000000003","name":"name","type":"text","nullable":false},
              {"id":"01K9SNK0000000000000000004","name":"email","type":"string","length":254,"nullable":false}],
             "primaryKey":{"columns":["01K9SNK0000000000000000002"]},
             "uniques":[{"id":"01K9SNK0000000000000000005","name":"uq_customers_email","columns":["01K9SNK0000000000000000004"]}]}
            """);
        await repo.CreateElementAsync($$"""
            {"kind":"table","id":"{{Invoices}}","name":"invoices","database":"{{E2ERepo.MainDatabaseId}}","schema":"{{Sales}}","columns":[
              {"id":"01K9SNK0000000000000000011","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"01K9SNK0000000000000000012","name":"number","type":"string","length":32,"nullable":false},
              {"id":"01K9SNK0000000000000000013","name":"issued_on","type":"date"},
              {"id":"01K9SNK0000000000000000014","name":"customer_id","type":"uuid","nullable":false}],
             "primaryKey":{"columns":["01K9SNK0000000000000000011"]},
             "uniques":[{"id":"01K9SNK0000000000000000015","name":"uq_invoices_number","columns":["01K9SNK0000000000000000012"]}],
             "foreignKeys":[{"id":"01K9SNK0000000000000000016","name":"fk_invoices_customer","columns":["01K9SNK0000000000000000014"],
               "referencesTable":"{{Customers}}"}]}
            """);

        // Both seeds through the store, every column and key by name; the rows get ids, their cells hold none.
        var customers = await repo.CreateElementAsync($$"""
            {"kind":"seed","name":"customers","target":"{{Customers}}","key":"uq_customers_email","columns":["name","email"],
             "rows":[{"values":["Ada","ada@example.com"]},{"values":["Grace","grace@example.com"]}]}
            """);
        var invoices = await repo.CreateElementAsync($$"""
            {"kind":"seed","name":"invoices","target":"{{Invoices}}","key":"uq_invoices_number","columns":["number","issued_on","fk_invoices_customer"],
             "rows":[{"values":["INV-1","2026-01-05","ada@example.com"]},{"values":["INV-2","2026-01-06","grace@example.com"]},
                     {"values":["INV-3","2026-01-07","ada@example.com"]}]}
            """);

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.True(!report.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error),
            string.Join("; ", report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message)));
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        var invoiceSeed = (Seed)snapshot.GetDocument(invoices.Id!)!.Element;
        Assert.Equal(["01K9SNK0000000000000000012", "01K9SNK0000000000000000013", "01K9SNK0000000000000000016"], invoiceSeed.Columns);
        Assert.Equal("01K9SNK0000000000000000015", invoiceSeed.Key);
        foreach (var seed in new[] { (Seed)snapshot.GetDocument(customers.Id!)!.Element, invoiceSeed })
        {
            foreach (var cell in seed.Rows.SelectMany(r => r.Values))
            {
                Assert.Equal(JsonValueKind.String, cell.ValueKind);
                Assert.False(Guid.TryParse(cell.GetString(), out _), cell.GetString());
                Assert.DoesNotMatch(Ulid(), cell.GetString()!);
            }
        }

        // database.seed_tables: each invoice's customer_id is its customer's stable id, derived from the table and the email.
        var model = (await repo.Store.ResolvedAsync(snapshot, Ct)).Model!;
        var main = model.Databases.Single(d => d.Id == E2ERepo.MainDatabaseId);
        var customerRows = main.SeedTables.Single(t => t.Table.Name == "customers" && t.Table.Schema == "sales").Rows;
        var invoiceRows = main.SeedTables.Single(t => t.Table.Name == "invoices" && t.Table.Schema == "sales").Rows;
        var byEmail = customerRows.ToDictionary(r => (string)r.Values["email"]!, r => (string)r.Values["id"]!, StringComparer.Ordinal);
        Assert.Equal(StableIds.Of(Customers, ["ada@example.com"]), byEmail["ada@example.com"]);
        Assert.Equal(StableIds.Of(Customers, ["grace@example.com"]), byEmail["grace@example.com"]);
        Assert.Equal(["INV-1", "INV-2", "INV-3"], invoiceRows.Select(r => (string)r.Values["number"]!));
        var expected = new[] { "ada@example.com", "grace@example.com", "ada@example.com" };
        for (var i = 0; i < invoiceRows.Count; i++)
        {
            var row = invoiceRows[i];
            Assert.Equal(byEmail[expected[i]], row.Values["customer_id"]);
            Assert.Same(customerRows.Single(c => (string)c.Values["email"]! == expected[i]), row.Refs["fk_invoices_customer"].Row);
            Assert.Equal(StableIds.Of(Invoices, [(string)row.Values["number"]!]), row.Values["id"]);
        }
    }
}
