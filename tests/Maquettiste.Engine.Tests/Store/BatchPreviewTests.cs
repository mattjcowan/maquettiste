using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>A batch's dry run: diffs, the diagnostics the change adds and removes, and its facts (next-phase-1-design.md section 5.1).</summary>
public sealed class BatchPreviewTests
{
    private const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";
    private const string Product = "01J92P0V0JR8BE8253SKT29ZG7";
    private const string InvoiceOverlay = "01J92P0V1T0J6RH4MY9H81NYB4";
    private const string FindCustomers = "01K6QRY0000000000000000003";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelBatch Parse(BillingStore s, string json)
    {
        var parsed = s.Store.ParseBatch(Encoding.UTF8.GetBytes(json));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return parsed.Batch;
    }

    private static string Edited(ElementDocument document, Action<JsonObject> change) => Encoding.UTF8.GetString(BillingStore.Edit(document, change));

    private static List<string> Keys(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.Select(d => $"{d.Rule} {d.ElementId} {d.JsonPointer}").Order(StringComparer.Ordinal)];

    /// <summary>Retires an element with a plain save: its referrers get MQ2022.</summary>
    private static async Task RetireAsync(BillingStore s, string id)
    {
        var document = s.Model.GetDocument(id)!;
        var saved = await s.Store.SaveAsync(id, BillingStore.Edit(document, e => e["status"] = "retired"), document.Hash, ChangeSource.Cli, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public async Task A_dry_run_returns_diffs_and_the_diagnostics_the_change_adds_or_removes()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        await RetireAsync(s, Product);
        var product = s.Model.GetDocument(Product)!;
        var customer = s.Model.GetDocument(Customer)!;
        var batch = Parse(s, $$"""
            { "operations": [
              { "op": "update", "id": "@entity:Catalog.Product", "expectedHash": "{{product.Hash}}", "element": {{Edited(product, e => e.Remove("status"))}} },
              { "op": "update", "id": "{{Customer}}", "expectedHash": "{{customer.Hash}}", "element": {{Edited(customer, e => e["status"] = "retired")}} }
            ] }
            """);
        var validator = EngineServices.Create(s.Harness.Options).Validator;
        var before = await validator.ValidateAsync(s.Model, ValidationScope.All, null, Ct);
        var files = s.Files();

        var preview = await s.Store.PreviewBatchAsync(batch, Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Equal(files, s.Files());
        Assert.Equal([(customer.Path, "changed"), (product.Path, "changed")], preview.Files.Select(f => (f.Path, f.Action)));
        Assert.All(preview.Files, f => Assert.StartsWith($"--- a/{f.Path}\n+++ b/{f.Path}\n@@ ", f.Diff, StringComparison.Ordinal));
        Assert.Contains("\n+  \"status\": \"retired\",\n", preview.Files[0].Diff, StringComparison.Ordinal);
        Assert.Contains("\n-  \"status\": \"retired\",\n", preview.Files[1].Diff, StringComparison.Ordinal);
        Assert.Equal(Product, preview.Ids.Names["@entity:Catalog.Product"]);
        Assert.Empty(preview.Ids.Minted);

        // Retiring Customer warns its users; un-retiring Product clears the warnings of its users.
        Assert.NotEmpty(preview.DiagnosticsAdded);
        Assert.NotEmpty(preview.DiagnosticsRemoved);
        Assert.All(preview.DiagnosticsAdded, d => Assert.Equal("MQ2022", d.Rule));
        Assert.All(preview.DiagnosticsAdded, d => Assert.Contains("Customer", d.Message, StringComparison.Ordinal));
        Assert.All(preview.DiagnosticsRemoved, d => Assert.Equal("MQ2022", d.Rule));
        Assert.All(preview.DiagnosticsRemoved, d => Assert.Contains("Product", d.Message, StringComparison.Ordinal));
        Assert.Equal([.. preview.DiagnosticsAdded.Order(Diagnostic.Order)], preview.DiagnosticsAdded);

        // The delta over the change's scope is what the whole model would gain and lose.
        var applied = await s.Store.ApplyBatchAsync(batch, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, applied.Outcome);
        var after = await validator.ValidateAsync(s.Model, ValidationScope.All, null, Ct);
        var beforeKeys = Keys(before.Diagnostics);
        var afterKeys = Keys(after.Diagnostics);
        Assert.Equal(afterKeys.Except(beforeKeys, StringComparer.Ordinal), Keys(preview.DiagnosticsAdded));
        Assert.Equal(beforeKeys.Except(afterKeys, StringComparer.Ordinal), Keys(preview.DiagnosticsRemoved));
    }

    [Fact]
    public async Task A_dry_run_of_a_delete_lists_its_vanished_diagnostics_as_removed()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        await RetireAsync(s, Customer);
        var query = s.Model.GetDocument(FindCustomers)!;
        var validator = EngineServices.Create(s.Harness.Options).Validator;
        var own = (await validator.ValidateAsync(s.Model, new ValidationScope([FindCustomers], IncludeReferrers: false), null, Ct)).Diagnostics;
        Assert.Contains(own, d => d.Rule == "MQ2022");

        var preview = await s.Store.PreviewBatchAsync(Parse(s, $$"""
            { "operations": [ { "op": "delete", "id": "@query:main.FindCustomersWithIssuedInvoices", "expectedHash": "{{query.Hash}}" } ] }
            """), Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        var file = Assert.Single(preview.Files);
        Assert.Equal((query.Path, "deleted", FindCustomers, (string?)null), (file.Path, file.Action, file.Id, file.After));
        Assert.StartsWith($"--- a/{query.Path}\n+++ b/{query.Path}\n@@ -1,", file.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain(file.Diff!.Split('\n').Skip(3), line => line.StartsWith('+'));
        Assert.Empty(preview.DiagnosticsAdded);
        Assert.Equal(Keys(own), Keys(preview.DiagnosticsRemoved));
        Assert.NotNull(s.Model.GetDocument(FindCustomers));
    }

    [Fact]
    public async Task A_peer_that_only_the_candidate_reaches_keeps_its_findings_out_of_the_delta()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        var type = await s.Store.CreateAsync("""
            { "kind": "reference-type", "name": "Currency", "code": { "length": 8 }, "label": { "length": 32 } }
            """u8.ToArray(), ChangeSource.Cli, Ct);
        Assert.True(type.Outcome == SaveOutcome.Saved, string.Join("; ", type.Diagnostics.Select(d => d.Message)));
        // Two codes that differ only by case: the existing seed carries a warning (MQ7002).
        var seed = await s.Store.CreateAsync(Encoding.UTF8.GetBytes($$"""
            { "kind": "seed", "name": "Currencies", "target": "{{type.Id}}", "columns": [ "code", "label" ],
              "rows": [ { "values": [ "usd", "US dollar" ] }, { "values": [ "USD", "US dollar" ] } ] }
            """), ChangeSource.Cli, Ct);
        Assert.True(seed.Outcome == SaveOutcome.Saved, string.Join("; ", seed.Diagnostics.Select(d => d.Message)));
        var validator = EngineServices.Create(s.Harness.Options).Validator;
        var before = await validator.ValidateAsync(s.Model, ValidationScope.All, null, Ct);
        Assert.Contains(before.Diagnostics, d => d.Rule == "MQ7002" && s.Model.GetDocument(d.ElementId!)?.Element.Id == seed.Id);

        // A second seed of the type: the candidate's run reaches the first seed as a peer, the model as it was has no such peer.
        var batch = Parse(s, """
            { "operations": [ { "op": "create", "element": { "kind": "seed", "name": "MoreCurrencies", "target": "@reference-type:Currency",
                "columns": [ "code", "label" ], "rows": [ { "values": [ "eur", "Euro" ] } ] } } ] }
            """);
        var preview = await s.Store.PreviewBatchAsync(batch, Ct);

        Assert.True(preview.Outcome == SaveOutcome.Saved, string.Join("; ", preview.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message)));
        Assert.DoesNotContain(preview.DiagnosticsAdded, d => d.Rule == "MQ7002");
        var applied = await s.Store.ApplyBatchAsync(batch, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, applied.Outcome);
        var after = await validator.ValidateAsync(s.Model, ValidationScope.All, null, Ct);
        var beforeKeys = Keys(before.Diagnostics);
        var afterKeys = Keys(after.Diagnostics);
        Assert.Equal(afterKeys.Except(beforeKeys, StringComparer.Ordinal), Keys(preview.DiagnosticsAdded));
        Assert.Equal(beforeKeys.Except(afterKeys, StringComparer.Ordinal), Keys(preview.DiagnosticsRemoved));

        // Deleting it again: now only the model as it was reaches the first seed as a peer.
        var added = s.Model.GetDocument(applied.Items[0].Id!)!;
        var deleted = await s.Store.PreviewBatchAsync(Parse(s, $$"""
            { "operations": [ { "op": "delete", "id": "@seed:MoreCurrencies", "expectedHash": "{{added.Hash}}" } ] }
            """), Ct);

        Assert.True(deleted.Outcome == SaveOutcome.Saved, string.Join("; ", deleted.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message)));
        Assert.DoesNotContain(deleted.DiagnosticsRemoved, d => d.Rule == "MQ7002");
        Assert.Equal(beforeKeys.Except(afterKeys, StringComparer.Ordinal), Keys(deleted.DiagnosticsAdded));
        Assert.Equal(afterKeys.Except(beforeKeys, StringComparer.Ordinal), Keys(deleted.DiagnosticsRemoved));
    }

    [Fact]
    public async Task An_invalid_dry_run_returns_errors_in_items_and_an_empty_delta()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        await RetireAsync(s, Product);
        var product = s.Model.GetDocument(Product)!;

        // A name that finds nothing: refused before anything is planned.
        var unnamed = await s.Store.PreviewBatchAsync(Parse(s, """
            { "operations": [ { "op": "create", "element": { "kind": "mapping", "name": "m", "database": "@database:archive" } } ] }
            """), Ct);
        // A change that brings a new error (a dangling reference), next to one that would clear warnings.
        var dangling = await s.Store.PreviewBatchAsync(Parse(s, $$"""
            { "operations": [
              { "op": "update", "id": "{{Product}}", "expectedHash": "{{product.Hash}}", "element": {{Edited(product, e => e.Remove("status"))}} },
              { "op": "create", "element": { "kind": "entity", "name": "Refund", "attributes": [ { "name": "reason", "type": { "ref": "01J92P0V1QRN2181XM2ZWE0000" } } ] } }
            ] }
            """), Ct);

        foreach (var (preview, rule) in new[] { (unnamed, "MQ1012"), (dangling, "MQ2001") })
        {
            Assert.Equal(SaveOutcome.Invalid, preview.Outcome);
            Assert.Contains(preview.Items.SelectMany(i => i.Diagnostics), d => d.Rule == rule && d.Severity == DiagnosticSeverity.Error);
            Assert.Empty(preview.Files);
            Assert.Same(BatchIds.Empty, preview.Ids);
            Assert.Empty(preview.DiagnosticsAdded);
            Assert.Empty(preview.DiagnosticsRemoved);
            Assert.Null(preview.Facts);
        }
    }

    [Fact]
    public async Task A_dry_run_lists_the_facts_it_would_record_and_their_MQ4101_as_added()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        // A second database, so the narrowing reaches one database of two and the dry run computes its facts.
        var archive = await s.Store.CreateAsync("""
            { "kind": "database", "name": "archive", "dialect": "postgresql", "version": "16", "defaultSchema": "archive", "schemas": [ { "name": "archive" } ],
              "byConvention": "none" }
            """u8.ToArray(), ChangeSource.Cli, Ct);
        Assert.True(archive.Outcome == SaveOutcome.Saved, string.Join("; ", archive.Diagnostics.Select(d => d.Message)));
        // The invoice number's column is varchar(32) in the Invoice table's overlay, which has no stored name: it is reached by id.
        var overlay = s.Model.GetDocument(InvoiceOverlay)!;
        var files = s.Files();

        var preview = await s.Store.PreviewBatchAsync(Parse(s, $$"""
            { "operations": [ { "op": "update", "id": "{{InvoiceOverlay}}", "expectedHash": "{{overlay.Hash}}",
                "element": {{Edited(overlay, e => e["columns"]![0]!["nativeType"] = "varchar(20)")}} } ] }
            """), Ct);

        Assert.True(preview.Outcome == SaveOutcome.Saved, string.Join("; ", preview.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message)));
        Assert.Equal(files, s.Files());
        var fact = Assert.Single(preview.Facts!);
        Assert.Equal(("change-type", "pending"), (fact.Fact, fact.State));
        var factFile = Assert.Single(preview.Files, f => f.Path == fact.Path);
        Assert.Equal(("created", "migration"), (factFile.Action, factFile.Kind));
        Assert.StartsWith($"--- a/{fact.Path}\n+++ b/{fact.Path}\n@@ -0,0 +1,", factFile.Diff, StringComparison.Ordinal);
        Assert.Equal(["/conversion", "/dataCheck"], fact.Missing.Select(m => "/" + m));
        Assert.Contains(preview.Files, f => f.Id == InvoiceOverlay && f.Action == "changed");
        var pending = preview.DiagnosticsAdded.Where(d => d.Rule == "MQ4101").ToList();
        Assert.NotEmpty(pending);
        Assert.All(pending, d => Assert.Equal((fact.Id, DiagnosticSeverity.Error), (d.ElementId, d.Severity)));
        Assert.Empty(preview.DiagnosticsRemoved);
    }

    [Fact]
    public async Task A_resync_dry_run_lists_the_facts_of_a_projected_entity()
    {
        await using var s = await BillingStore.OpenAsync(validate: true);
        const string InvoiceNote = "01K6BND0000000000000000010", Notes = "01K6BND0000000000000000001", NotesBody = "01K6BND0000000000000000005";
        // InvoiceNote is bound to notes in main and also projected into archive by a mapping element; a third database keeps the
        // write's reach to some databases, so the dry run computes their facts.
        string archive = "";
        foreach (var name in new[] { "archive", "scratch" })
        {
            var created = await s.Store.CreateAsync(Encoding.UTF8.GetBytes($$"""
                { "kind": "database", "name": "{{name}}", "dialect": "postgresql", "version": "16", "defaultSchema": "{{name}}", "schemas": [ { "name": "{{name}}" } ],
                  "byConvention": "none" }
                """), ChangeSource.Cli, Ct);
            Assert.True(created.Outcome == SaveOutcome.Saved, string.Join("; ", created.Diagnostics.Select(d => d.Message)));
            if (name == "archive")
                archive = created.Id!;
        }

        var mapping = await s.Store.CreateAsync(Encoding.UTF8.GetBytes($$"""{ "kind": "mapping", "name": "notes in archive", "database": "{{archive}}", "entity": "{{InvoiceNote}}" }"""),
            ChangeSource.Cli, Ct);
        Assert.True(mapping.Outcome == SaveOutcome.Saved, string.Join("; ", mapping.Diagnostics.Select(d => d.Message)));
        var resync = $$"""{ "operations": [ { "op": "resync-binding", "database": "01J92P0V1QRN2181XM2ZWE02W4", "entities": ["{{InvoiceNote}}"] } ] }""";
        // A re-sync from scratch records the provenance of every column of notes; then the body column is renamed.
        var recorded = await s.Store.ApplyBatchAsync(Parse(s, resync), ChangeSource.Cli, Ct);
        Assert.True(recorded.Outcome == SaveOutcome.Saved, string.Join("; ", recorded.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message)));
        var notes = s.Model.GetDocument(Notes)!;
        var renamed = await s.Store.SaveAsync(Notes, BillingStore.Edit(notes, t => t["columns"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == NotesBody)!["name"] = "note_text"),
            notes.Hash, ChangeSource.Cli, Ct);
        Assert.True(renamed.Outcome == SaveOutcome.Saved, string.Join("; ", renamed.Diagnostics.Select(d => d.Message)));
        var files = s.Files();

        var preview = await s.Store.PreviewBatchAsync(Parse(s, resync), Ct);

        Assert.True(preview.Outcome == SaveOutcome.Saved, string.Join("; ", preview.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message)));
        Assert.Equal(files, s.Files());
        var entity = Assert.Single(preview.Files, f => f.Id == InvoiceNote);
        Assert.Contains("\"name\": \"noteText\"", entity.After, StringComparison.Ordinal);
        // The attribute's new name renames the column InvoiceNote's projection has in archive: the dry run lists that fact.
        var fact = Assert.Single(preview.Facts!);
        Assert.Equal((archive, "rename"), (fact.Database, fact.Fact));
        Assert.Contains(preview.Files, f => f.Path == fact.Path && f.Action == "created");
    }

    [Fact]
    public async Task A_refactor_preview_carries_usage_and_facts()
    {
        // next-phase-2-design.md section 3.5: a code rename's dry run lists what it rewrites (a typed column's default and a table seed's
        // cell, found through the index) on the type's item; a second database keeps the change's reach to some databases, so the dry
        // run computes the facts the write would record.
        await using var s = await BillingStore.OpenAsync(validate: true);
        var archive = await s.Store.CreateAsync("""
            { "kind": "database", "name": "archive", "dialect": "postgresql", "version": "16", "defaultSchema": "archive", "schemas": [ { "name": "archive" } ],
              "byConvention": "none" }
            """u8.ToArray(), ChangeSource.Cli, Ct);
        Assert.True(archive.Outcome == SaveOutcome.Saved, string.Join("; ", archive.Diagnostics.Select(d => d.Message)));
        const string Notes = "01K6BND0000000000000000001";
        var notes = s.Model.GetDocument(Notes)!;
        var term = Edited(notes, t => t["columns"]!.AsArray().Add(JsonNode.Parse("""
            { "name": "term", "type": "string", "length": 8, "typeRef": { "ref": "$term" }, "default": "net30" }
            """)));
        var setup = await s.Store.ApplyBatchAsync(Parse(s, $$"""
            { "operations": [
              { "op": "create", "as": "$term", "element": { "kind": "reference-type", "name": "PaymentTerm", "code": { "length": 8 }, "label": { "length": 64 } } },
              { "op": "create", "element": { "kind": "seed", "name": "PaymentTerms", "target": "$term", "columns": ["code", "label"],
                  "rows": [ { "values": ["net30", "Net 30"] }, { "values": ["net60", "Net 60"] } ] } },
              { "op": "update", "id": "{{Notes}}", "expectedHash": "{{notes.Hash}}", "element": {{term}} },
              { "op": "create", "element": { "kind": "seed", "name": "WelcomeNotes", "target": "{{Notes}}", "columns": ["id", "entity_type", "entity_id", "body", "term"],
                  "rows": [ { "values": ["0193a4b2-0000-7000-8000-000000000001", "invoice", "0193a4b2-0000-7000-8000-000000000002", "Welcome", "net30"] } ] } }
            ] }
            """), ChangeSource.Cli, Ct);
        Assert.True(setup.Outcome == SaveOutcome.Saved, string.Join("; ", setup.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message)));
        var type = setup.Ids.Names["$term"];
        var seed = s.Model.All<Seed>().Single(x => x.Name == "WelcomeNotes").Id;
        var files = s.Files();

        var preview = await s.Store.PreviewBatchAsync(Parse(s, """
            { "operations": [ { "op": "rename-code", "id": "@reference-type:PaymentTerm", "code": "net30", "to": "net-30" } ] }
            """), Ct);

        Assert.True(preview.Outcome == SaveOutcome.Saved, string.Join("; ", preview.Items.SelectMany(i => i.Diagnostics).Select(d => d.Message)));
        Assert.Equal(files, s.Files());
        var usage = Assert.Single(preview.Items, i => i.Usage is not null);
        Assert.Equal(type, usage.Id);
        Assert.Equal(
            new[] { (Notes, "/columns/5/default"), (seed, "/rows/0/values/4") }.OrderBy(u => u.Item1, StringComparer.Ordinal),
            usage.Usage!.Uses.Select(u => (u.FromElementId, u.JsonPointer)));
        Assert.Equal(2, usage.Usage.UseCount);
        Assert.Contains("\"net-30\"", Assert.Single(preview.Files, f => f.Id == Notes).After, StringComparison.Ordinal);
        // The write's code map becomes one value map in main, where notes.term holds the codes; archive holds none.
        var fact = Assert.Single(preview.Facts!);
        Assert.Equal(("value-map", "maps PaymentTerm codes net30 -> net-30 in 1 column"), (fact.Fact, fact.Summary));
        Assert.Contains(preview.Files, f => f.Path == fact.Path);
        Assert.Empty(preview.DiagnosticsAdded);
    }
}
