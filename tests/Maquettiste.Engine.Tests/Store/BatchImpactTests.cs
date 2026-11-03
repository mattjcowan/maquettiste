using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// The store's part of a dry run's impact (next-phase-2-design.md section 6.1, decisions 6 and 7): the referrers of what the change
/// alters or deletes, minus the batch's own documents, and the databases it can reach. Packs are the generation service's.
/// </summary>
public sealed class BatchImpactTests
{
    private const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string Number = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string InvoiceOverlay = "01J92P0V1T0J6RH4MY9H81NYB4";
    private const string InvoicesByCustomer = "01K6QRY0000000000000000001";
    private const string InvoiceStatus = "01J92P0V05WQDH2GZG539D9VG1";
    private const string Main = "01J92P0V1QRN2181XM2ZWE02W4";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelBatch Parse(BillingStore s, string json)
    {
        var parsed = s.Store.ParseBatch(Encoding.UTF8.GetBytes(json));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return parsed.Batch;
    }

    private static string Update(BillingStore s, string id, Action<JsonObject> change)
    {
        var document = s.Model.GetDocument(id)!;
        return $$"""{ "op": "update", "id": "{{id}}", "expectedHash": "{{document.Hash}}", "element": {{Encoding.UTF8.GetString(BillingStore.Edit(document, change))}} }""";
    }

    private static Action<JsonObject> Rename(string attribute, string name) =>
        e => e["attributes"]!.AsArray().Single(a => (string?)a!["id"] == attribute)!["name"] = name;

    private static async Task<BatchPreview> DryRunAsync(BillingStore s, params string[] operations)
    {
        var preview = await s.Store.PreviewBatchAsync(Parse(s, $$"""{ "operations": [ {{string.Join(", ", operations)}} ] }"""), Ct);
        Assert.True(preview.Outcome == SaveOutcome.Saved,
            preview.Outcome + ": " + string.Join("; ", preview.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.NotNull(preview.Impact);
        return preview;
    }

    [Fact]
    public async Task Referrers_of_a_renamed_attribute_exclude_the_batch_documents()
    {
        await using var s = await BillingStore.OpenAsync();
        var alone = await DryRunAsync(s, Update(s, Invoice, Rename(Number, "code")));
        Assert.Contains(alone.Impact!.Referrers, r => r.Reference.FromElementId == InvoiceOverlay);
        Assert.Contains(alone.Impact.Referrers, r => r.Reference.FromElementId == InvoicesByCustomer);
        Assert.DoesNotContain(alone.Impact.Referrers, r => r.Reference.FromElementId == Invoice);
        Assert.All(alone.Impact.Referrers, r => Assert.Equal((Number, "changed"), (r.Target, r.Change)));
        var query = alone.Impact.Referrers.First(r => r.Reference.FromElementId == InvoicesByCustomer);
        Assert.Equal(("query", "InvoicesByCustomer"), (query.FromKind, query.FromName));

        // The overlay written by the same batch is in its files, not among the referrers.
        var both = await DryRunAsync(s, Update(s, Invoice, Rename(Number, "code")), Update(s, InvoiceOverlay, e => e["description"] = "Invoices."));
        Assert.DoesNotContain(both.Impact!.Referrers, r => r.Reference.FromElementId == InvoiceOverlay);
        Assert.Contains(both.Impact.Referrers, r => r.Reference.FromElementId == InvoicesByCustomer);
        Assert.Equal(both.Impact.Referrers.Count, both.Impact.ReferrersTotal);
    }

    [Fact]
    public async Task Referrers_of_a_deleted_element_say_deleted()
    {
        await using var s = await BillingStore.OpenAsync();
        // The harness's validator accepts the dangling references, so the dry run saves and lists them.
        var preview = await DryRunAsync(s, Update(s, Invoice, e => e["attributes"]!.AsArray().Remove(e["attributes"]!.AsArray().Single(a => (string?)a!["id"] == Number))));
        Assert.Contains(preview.Impact!.Referrers, r => r.Reference.FromElementId == InvoicesByCustomer);
        Assert.All(preview.Impact.Referrers.Where(r => r.Target == Number), r => Assert.Equal("deleted", r.Change));
        Assert.Contains(preview.Impact.Referrers, r => r.Target == Number);
    }

    [Fact]
    public async Task An_entity_whose_own_fields_did_not_change_is_not_a_target()
    {
        await using var s = await BillingStore.OpenAsync();
        var renamed = await DryRunAsync(s, Update(s, Invoice, Rename(Number, "code")));
        Assert.DoesNotContain(renamed.Impact!.Referrers, r => r.Target == Invoice);

        // Its own field changed: the relations that reference it are referrers.
        var described = await DryRunAsync(s, Update(s, Invoice, e => e["description"] = "A bill."));
        Assert.Contains(described.Impact!.Referrers, r => r.Target == Invoice && r.FromKind == "relation");
        Assert.DoesNotContain(described.Impact.Referrers, r => r.Target == Number);
    }

    [Fact]
    public async Task Databases_list_what_the_change_reaches_when_facts_are_null()
    {
        await using var s = await BillingStore.OpenAsync();
        var preview = await DryRunAsync(s, Update(s, Invoice, Rename(Number, "code")));
        Assert.Null(preview.Facts); // one database: the change reaches every database
        Assert.Equal([new ImpactDatabase(Main, "main")], preview.Impact!.Databases);
    }

    [Fact]
    public async Task A_description_change_reaches_its_database()
    {
        await using var s = await BillingStore.OpenAsync();
        var preview = await DryRunAsync(s, Update(s, Invoice, e => e["description"] = "A bill."));
        Assert.NotNull(preview.Facts);
        Assert.Empty(preview.Facts); // a description reshapes nothing
        Assert.Equal([Main], preview.Impact!.Databases.Select(d => d.Id));
    }

    [Fact]
    public async Task An_invalid_dry_run_has_no_impact()
    {
        await using var s = await BillingStore.OpenAsync();
        var preview = await s.Store.PreviewBatchAsync(Parse(s, $$"""
            { "operations": [ { "op": "update", "id": "{{Invoice}}", "expectedHash": "{{s.Model.GetDocument(InvoiceOverlay)!.Hash}}", "element": {{Encoding.UTF8.GetString(BillingStore.Edit(s.Model.GetDocument(Invoice)!, Rename(Number, "code")))}} } ] }
            """), Ct);
        Assert.NotEqual(SaveOutcome.Saved, preview.Outcome);
        Assert.Null(preview.Impact);
        Assert.DoesNotContain("\"impact\"", System.Text.Json.JsonSerializer.Serialize(preview, EngineJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_store_alone_leaves_packs_null()
    {
        await using var s = await BillingStore.OpenAsync();
        var preview = await DryRunAsync(s, Update(s, Invoice, Rename(Number, "code")));
        Assert.Null(preview.Impact!.Packs);
        var json = System.Text.Json.JsonSerializer.Serialize(preview, EngineJson.Options);
        Assert.Contains("\"impact\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"packs\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Referrers_are_capped_with_a_total()
    {
        await using var s = await BillingStore.OpenAsync();
        var attributes = string.Join(", ", Enumerable.Range(0, 201).Select(i => $$"""{ "name": "s{{i:D3}}", "type": { "ref": "{{InvoiceStatus}}" } }"""));
        var created = await s.Store.ApplyBatchAsync(Parse(s, $$"""
            { "operations": [ { "op": "create", "element": { "kind": "entity", "name": "StatusLog", "attributes": [ {{attributes}} ] } } ] }
            """), ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);

        var preview = await DryRunAsync(s, Update(s, InvoiceStatus, e => e["description"] = "Where an invoice is."));
        Assert.Equal(BatchImpact.MaxReferrers, preview.Impact!.Referrers.Count);
        Assert.True(preview.Impact.ReferrersTotal > 201, preview.Impact.ReferrersTotal.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var order = preview.Impact.Referrers.Select(r => (r.Target, r.Reference.FromElementId, r.Reference.JsonPointer)).ToList();
        Assert.Equal(order.OrderBy(o => o.Target, StringComparer.Ordinal).ThenBy(o => o.FromElementId, StringComparer.Ordinal)
            .ThenBy(o => o.JsonPointer, StringComparer.Ordinal), order);
    }
}
