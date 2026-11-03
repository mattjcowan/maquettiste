using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Store;

public sealed class ModelStoreBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Element(byte[] json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Element(string json) => Element(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task ParseBatch_reads_operations_without_touching_disk()
    {
        await using var s = await BillingStore.OpenAsync(load: false);
        var json = """
            {
              "operations": [
                { "op": "create", "element": { "kind": "entity", "name": "Refund" } },
                { "op": "update", "id": "01J92P0V0FJ23CGSNKM7P1W5V7", "expectedHash": "0000000000000000000000000000000000000000000000000000000000000000", "element": { "kind": "entity", "name": "Invoice" } },
                { "op": "delete", "id": "01J92P0V0FJ23CGSNKM7P1W5V7", "expectedHash": "0000000000000000000000000000000000000000000000000000000000000000" }
              ]
            }
            """u8;

        var result = s.Store.ParseBatch(json);

        Assert.Empty(result.Diagnostics);
        Assert.Equal([BatchOp.Create, BatchOp.Update, BatchOp.Delete], result.Batch!.Operations.Select(o => o.Op));
        Assert.Null(result.Batch.Operations[0].Id);
        Assert.Equal("Refund", result.Batch.Operations[0].Element!.Value.GetProperty("name").GetString());
        Assert.Null(result.Batch.Operations[2].Element);
        Assert.Null(s.Store.Current);
    }

    [Fact]
    public async Task ParseBatch_reads_as_and_names()
    {
        await using var s = await BillingStore.OpenAsync(load: false);
        var json = """
            {
              "operations": [
                { "op": "create", "as": "$pm", "element": { "kind": "table", "name": "payment_method", "database": "@database:main", "schema": "@schema:billing" } },
                { "op": "update", "id": "@entity:Billing.Invoice", "expectedHash": "0000000000000000000000000000000000000000000000000000000000000000", "as": "$invoice", "element": { "kind": "entity", "name": "Invoice" } },
                { "op": "materialize-columns", "database": "@database:main", "entities": ["$type", "@entity:\"Billing\".Invoice"], "attributes": ["@attribute:Invoice.total"], "expectedHashes": { "@entity:Invoice": "0000000000000000000000000000000000000000000000000000000000000000" } },
                { "op": "remove-schema", "id": "@database:main", "schema": "@schema:billing", "target": "$other", "default": "01J92P0V0FJ23CGSNKM7P1W5V7" }
              ]
            }
            """u8;
        var malformed = s.Store.ParseBatch("""{ "operations": [ { "op": "create", "as": "pm", "element": { "kind": "table" } } ] }"""u8);
        var notAName = s.Store.ParseBatch("""{ "operations": [ { "op": "delete", "id": "Invoice", "expectedHash": "0000000000000000000000000000000000000000000000000000000000000000" } ] }"""u8);

        var result = s.Store.ParseBatch(json);

        Assert.Empty(result.Diagnostics);
        var operations = result.Batch!.Operations;
        Assert.Equal(["$pm", "$invoice", null, null], operations.Select(o => o.As));
        Assert.Equal("@entity:Billing.Invoice", operations[1].Id);
        Assert.Equal("@database:main", operations[2].Database);
        Assert.Equal(["$type", "@entity:\"Billing\".Invoice"], operations[2].Entities!);
        Assert.Equal(["@attribute:Invoice.total"], operations[2].Attributes!);
        Assert.Equal("@entity:Invoice", Assert.Single(operations[2].ExpectedHashes!).Key);
        Assert.Equal(("@database:main", "@schema:billing", "$other", "01J92P0V0FJ23CGSNKM7P1W5V7"),
            (operations[3].Id, operations[3].Schema, operations[3].Target, operations[3].Default));
        Assert.Equal("@database:main", operations[0].Element!.Value.GetProperty("database").GetString());
        Assert.Null(malformed.Batch);
        Assert.Contains(malformed.Diagnostics, d => d.Rule == "MQ1002" && d.JsonPointer == "/operations/0/as");
        Assert.Null(notAName.Batch);
        Assert.Contains(notAName.Diagnostics, d => d.Rule == "MQ1002" && d.JsonPointer == "/operations/0/id");
        Assert.Null(s.Store.Current);
    }

    [Fact]
    public async Task ParseBatch_reports_json_and_schema_errors_with_positions()
    {
        await using var s = await BillingStore.OpenAsync(load: false);

        var broken = s.Store.ParseBatch("{\n  \"operations\": [\n    { \"op\": \"create\", }\n  ]\n}"u8);
        var invalid = s.Store.ParseBatch("{\n  \"operations\": [\n    { \"op\": \"update\", \"element\": { \"kind\": \"entity\" } },\n    { \"op\": \"rename\" }\n  ]\n}"u8);
        var empty = s.Store.ParseBatch("{ \"operations\": [] }"u8);

        Assert.Null(broken.Batch);
        Assert.Equal(("MQ1001", 3), (Assert.Single(broken.Diagnostics).Rule, broken.Diagnostics[0].Line));
        Assert.Null(invalid.Batch);
        Assert.All(invalid.Diagnostics, d => Assert.Equal("MQ1002", d.Rule));
        Assert.All(invalid.Diagnostics, d => Assert.Null(d.FilePath));
        Assert.Contains(invalid.Diagnostics, d => d.JsonPointer == "/operations/0" && d.Line == 3);
        Assert.Contains(invalid.Diagnostics, d => d.JsonPointer == "/operations/1/op" && d.Line == 4);
        Assert.Null(empty.Batch);
    }

    [Fact]
    public async Task A_batch_applies_creates_updates_and_deletes_together_with_one_change_set()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var view = s.Doc("view", "outstanding_invoices");
        var batch = new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, null, null, Element("{\"kind\":\"entity\",\"name\":\"Refund\"}")),
            new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, Element(BillingStore.Edit(customer, n => n["displayName"] = "Client"))),
            new BatchOperation(BatchOp.Delete, view.Element.Id, view.Hash, null),
        ]);

        var result = await s.Store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.All(result.Items, i => Assert.Equal(SaveOutcome.Saved, i.Outcome));
        var refund = result.Items[0];
        Assert.True(s.Harness.Exists("model/entities/refund.json"));
        Assert.Equal(refund.Hash, s.Store.Current!.GetDocument(refund.Id!)!.Hash);
        Assert.Equal("Client", result.Items[1].Current!.Element.DisplayName);
        Assert.Null(result.Items[2].Current);
        Assert.False(s.Harness.Exists("model/databases/main/views/outstanding-invoices.json"));
        Assert.Equal(new[] { customer.Element.Id, refund.Id! }.Order(StringComparer.Ordinal), result.Changes!.Changed.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Equal([view.Element.Id], result.Changes.Deleted);
        Assert.Same(result.Changes, Assert.Single(s.Notifications));
        Assert.All(result.Items, i => Assert.Same(result.Changes, i.Changes));
    }

    [Fact]
    public async Task A_batch_with_one_bad_operation_changes_nothing()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var product = s.Doc("entity", "Product");
        var before = s.Files();
        var batch = new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, null, null, Element("{\"kind\":\"entity\",\"name\":\"Refund\"}")),
            new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, Element(BillingStore.Edit(customer, n => n["displayName"] = "Client"))),
            new BatchOperation(BatchOp.Update, product.Element.Id, new string('a', 64), Element(BillingStore.Edit(product, n => n["displayName"] = "Item"))),
        ]);

        var result = await s.Store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Conflict, result.Outcome);
        Assert.Null(result.Changes);
        Assert.Equal([SaveOutcome.Saved, SaveOutcome.Saved, SaveOutcome.Conflict], result.Items.Select(i => i.Outcome));
        Assert.All(result.Items.Take(2), i => Assert.Null(i.Hash));
        Assert.Equal(product.Hash, result.Items[2].Hash);
        Assert.Equal(before, s.Files());
        Assert.Empty(s.Notifications);
    }

    [Fact]
    public async Task A_batch_may_delete_an_element_whose_referrers_it_updates()
    {
        await using var s = await BillingStore.OpenAsync();
        var root = s.Doc("stereotype", "Aggregate root");
        var customer = s.Doc("entity", "Customer");
        var invoice = s.Doc("entity", "Invoice");
        static void Drop(JsonObject n) => n["stereotypes"]!.AsArray().RemoveAt(0);

        var partial = await s.Store.ApplyBatchAsync(
            new ModelBatch(
            [
                new BatchOperation(BatchOp.Delete, root.Element.Id, root.Hash, null),
                new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, Element(BillingStore.Edit(customer, Drop))),
            ]),
            ChangeSource.Editor,
            Ct);
        Assert.Equal(SaveOutcome.Referenced, partial.Outcome);
        Assert.Equal(invoice.Element.Id, Assert.Single(partial.Items[0].Referrers).FromElementId);

        var complete = await s.Store.ApplyBatchAsync(
            new ModelBatch(
            [
                new BatchOperation(BatchOp.Delete, root.Element.Id, root.Hash, null),
                new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, Element(BillingStore.Edit(customer, Drop))),
                new BatchOperation(BatchOp.Update, invoice.Element.Id, invoice.Hash, Element(BillingStore.Edit(invoice, Drop))),
            ]),
            ChangeSource.Editor,
            Ct);
        Assert.Equal(SaveOutcome.Saved, complete.Outcome);
        Assert.Null(s.Store.Current!.GetStereotype("aggregate-root"));
    }

    [Fact]
    public async Task An_element_may_appear_once_per_batch()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var body = Element(BillingStore.Edit(customer, n => n["displayName"] = "Client"));

        var result = await s.Store.ApplyBatchAsync(
            new ModelBatch([new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, body), new BatchOperation(BatchOp.Delete, customer.Element.Id, customer.Hash, null)]),
            ChangeSource.Editor,
            Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal(SaveOutcome.Invalid, result.Items[1].Outcome);
    }

    [Fact]
    public async Task A_rename_failure_rolls_back_every_file_already_renamed()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        Directory.CreateDirectory(s.Harness.Model("model/entities/zeta.json")); // the rename onto it fails after customer.json is replaced
        var before = s.Files();
        var batch = new ModelBatch(
        [
            new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, Element(BillingStore.Edit(customer, n => n["displayName"] = "Client"))),
            new BatchOperation(BatchOp.Create, null, null, Element("{\"kind\":\"entity\",\"name\":\"Zeta\"}")),
        ]);

        await Assert.ThrowsAsync<IOException>(() => s.Store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct));

        Assert.Equal(before, s.Files());
        Assert.Same(customer, s.Store.Current!.GetDocument(customer.Element.Id));
        Assert.Empty(s.Notifications);
    }

    [Fact]
    public async Task A_staging_failure_changes_nothing()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        Directory.Delete(h.Model("model/mappings"), recursive: true);
        h.Write("model/mappings", "a file where a folder should be");
        await using var store = h.NewStore();
        await store.LoadAsync(Ct);
        var customer = store.Current!.GetDocument(Billing.IdOf(store.Current, "entity", "Customer"))!;
        var db = Billing.IdOf(store.Current, "database", "main");
        var files = Directory.EnumerateFiles(h.Repo.ModelRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();

        var batch = new ModelBatch(
        [
            new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, Element(BillingStore.Edit(customer, n => n["displayName"] = "Client"))),
            new BatchOperation(BatchOp.Create, null, null, Element($"{{\"kind\":\"mapping\",\"name\":\"Customer in main\",\"database\":\"{db}\",\"entity\":\"{customer.Element.Id}\"}}")),
        ]);
        await Assert.ThrowsAsync<IOException>(() => store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct));

        Assert.Equal(files, Directory.EnumerateFiles(h.Repo.ModelRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Client", h.Read("model/entities/customer.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_parsed_from_json_applies()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var json = $$"""
            { "operations": [ { "op": "update", "id": "{{customer.Element.Id}}", "expectedHash": "{{customer.Hash}}", "element": {{Encoding.UTF8.GetString(BillingStore.Edit(customer, n => n["displayName"] = "Client"))}} } ] }
            """;

        var parsed = s.Store.ParseBatch(Encoding.UTF8.GetBytes(json));
        var result = await s.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal("Client", s.Store.Current!.Get<Entity>(customer.Element.Id)!.DisplayName);
    }

    [Fact]
    public async Task Dropping_a_named_member_and_adding_one_without_an_id_is_MQ1014()
    {
        // next-phase-2-design.md section 4.2: the "keep its id" check covers named members.
        await using var s = await CustomKinds.CustomFixture.OpenAsync(false, ("message", CustomKinds.CustomFixture.MessageKind));
        var created = await CustomKinds.CustomFixture.CreateAsync(s, """
            { "kind": "custom", "name": "invoice_sent", "type": "message", "content": { "parts": [ { "name": "subject" }, { "name": "body" } ] } }
            """);
        var document = s.Model.GetDocument(created.Id!)!;
        var subject = s.Model.Get<Custom>(created.Id!)!.Content["parts"][0].GetProperty("id").GetString()!;

        var replaced = await s.Store.SaveAsync(created.Id!, BillingStore.Edit(document, o => o["content"]!["parts"]![0] = JsonNode.Parse("""{ "name": "subject" }""")),
            document.Hash, ChangeSource.Cli, TestContext.Current.CancellationToken);

        Assert.Equal(SaveOutcome.Invalid, replaced.Outcome);
        var refusal = Assert.Single(replaced.Diagnostics, d => d.Rule == "MQ1014");
        Assert.Equal("/content/parts/0", refusal.JsonPointer);
        Assert.Contains(subject, refusal.Message, StringComparison.Ordinal);

        // A new member with a new name is fine.
        var added = await s.Store.SaveAsync(created.Id!, BillingStore.Edit(document, o => o["content"]!["parts"]!.AsArray().Add(JsonNode.Parse("""{ "name": "footer" }"""))),
            document.Hash, ChangeSource.Cli, TestContext.Current.CancellationToken);
        Assert.Equal(SaveOutcome.Saved, added.Outcome);
    }
}
