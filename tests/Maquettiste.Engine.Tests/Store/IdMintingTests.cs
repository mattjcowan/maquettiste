using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>The engine mints every id a write needs and refuses a silent replacement (next-phase-1-design.md section 5.1).</summary>
public sealed class IdMintingTests
{
    private const string Database = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string Schema = "01J92P0V1RC04SKQ5353EAKHG2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Element(byte[] json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<string> Ids(JsonElement items) => [.. items.EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];

    [Fact]
    public async Task Create_and_save_mint_missing_sub_element_ids()
    {
        await using var s = await BillingStore.OpenAsync();
        var notes = s.Doc("table", "notes");
        var edited = BillingStore.Edit(notes, n =>
        {
            n["columns"]!.AsArray().Add(JsonNode.Parse("""{ "name": "author", "type": "string", "length": 64 }"""));
            n["indexes"]!.AsArray().Add(JsonNode.Parse("""{ "columns": [ { "column": "01K6BND0000000000000000005" } ] }"""));
        });

        var created = await s.Store.CreateAsync(
            """{ "kind": "entity", "name": "Refund", "attributes": [ { "name": "id", "type": "uuid" }, { "name": "amount", "type": "decimal" } ] }"""u8.ToArray(),
            ChangeSource.Cli, Ct);
        var saved = await s.Store.SaveAsync(notes.Element.Id, edited, notes.Hash, ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var refund = s.Model.Get<Entity>(created.Id!)!;
        Assert.All(refund.Attributes, a => Assert.True(IdFormat.IsValid(a.Id)));
        Assert.Equal(3, refund.Attributes.Select(a => a.Id).Append(refund.Id).Distinct(StringComparer.Ordinal).Count());
        var table = s.Model.Get<Table>(notes.Element.Id)!;
        var author = Assert.Single(table.Columns, c => c.Name == "author");
        Assert.True(IdFormat.IsValid(author.Id));
        Assert.Equal(2, table.Indexes.Count);
        Assert.True(IdFormat.IsValid(table.Indexes[1].Id));
        Assert.Equal(Ids(notes.Json.GetProperty("columns")), table.Columns.Take(5).Select(c => c.Id));
        Assert.True(s.Model.TryGetEntry(author.Id, out var entry));
        Assert.Equal(notes.Element.Id, entry.OwnerId);
    }

    [Fact]
    public async Task Minted_ids_follow_operation_and_document_order()
    {
        // The table's checks come before its columns in the request; minting follows x-order (columns, then checks).
        var batch = new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, null, null, Element(
                """{ "kind": "entity", "name": "Refund", "attributes": [ { "name": "id", "type": "uuid" }, { "name": "amount", "type": "decimal" } ] }""")),
            new BatchOperation(BatchOp.Create, null, null, Element(
                $$"""{ "kind": "table", "checks": [ { "name": "ck_refunds_amount", "expression": { "*": "amount > 0" } } ], "columns": [ { "name": "id", "type": "uuid" }, { "name": "amount", "type": "decimal" } ], "name": "refunds", "database": "{{Database}}", "schema": "{{Schema}}" }""")),
        ]);
        var expected = new SequentialIdGenerator();
        var order = Enumerable.Range(0, 7).Select(_ => expected.NewId()).ToList();

        await using var first = await BillingStore.OpenAsync();
        await using var second = await BillingStore.OpenAsync();
        var a = await first.Store.ApplyBatchAsync(batch, ChangeSource.Cli, Ct);
        var b = await second.Store.ApplyBatchAsync(batch, ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Saved, a.Outcome);
        Assert.Equal(SaveOutcome.Saved, b.Outcome);
        var entity = first.Model.Get<Entity>(a.Items[0].Id!)!;
        var table = first.Model.Get<Table>(a.Items[1].Id!)!;
        Assert.Equal(order,
            [entity.Id, .. entity.Attributes.Select(x => x.Id), table.Id, .. table.Columns.Select(c => c.Id), .. table.Checks.Select(c => c.Id)]);
        Assert.Equal(first.Files(), second.Files());

        // What a fill reports: pointers in minting order, the index kind of each object, and the placeholder its as declares.
        var process = JsonNode.Parse("""
            { "kind": "process", "name": "Approval", "states": [ { "name": "Open", "states": [ { "name": "Waiting", "as": "$waiting" } ] } ],
              "events": [ { "name": "submit", "payload": [ { "name": "note", "type": "string" } ] } ], "transitions": [ { "source": "x" } ] }
            """)!;
        var minted = new List<MintedId>();
        IdMinter.Fill(process, TestServices.Schemas.GetLayout("process.json"), new SequentialIdGenerator(), minted);
        Assert.Equal(
            [("/events/0", "event", null), ("/events/0/payload/0", "attribute", null), ("/states/0", "state", null), ("/states/0/states/0", "state", "$waiting"),
                ("/transitions/0", "transition", (string?)null)],
            minted.Select(m => (m.Pointer, m.Kind, m.Placeholder)));
        Assert.Null(process["id"]);
        Assert.Equal(order.Take(5), minted.Select(m => m.Id));
    }

    [Fact]
    public async Task A_create_takes_its_operation_id_and_an_update_its_target_as_the_root()
    {
        await using var s = await BillingStore.OpenAsync();
        const string Refund = "01K7A00000000000000000000R";
        var customer = s.Doc("entity", "Customer");
        var update = JsonNode.Parse(customer.Json.GetRawText())!.AsObject();
        update.Remove("id");
        update["displayName"] = "Client";

        var result = await s.Store.ApplyBatchAsync(new ModelBatch(
        [
            new BatchOperation(BatchOp.Create, Refund, null, Element("""{ "kind": "entity", "name": "Refund", "attributes": [ { "name": "id", "type": "uuid" } ] }""")),
            new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, Element(Encoding.UTF8.GetBytes(update.ToJsonString()))),
        ]), ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal([Refund, customer.Element.Id], result.Items.Select(i => i.Id));
        Assert.True(IdFormat.IsValid(Assert.Single(s.Model.Get<Entity>(Refund)!.Attributes).Id));
        Assert.Equal("Client", s.Model.Get<Entity>(customer.Element.Id)!.DisplayName);
    }

    [Fact]
    public async Task A_new_sub_element_without_an_id_in_an_update_gets_one()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var before = Ids(customer.Json.GetProperty("attributes"));
        var edited = BillingStore.Edit(customer, n => n["attributes"]!.AsArray().Add(JsonNode.Parse("""{ "name": "phone", "type": "string", "length": 32 }""")));

        var result = await s.Store.ApplyBatchAsync(
            new ModelBatch([new BatchOperation(BatchOp.Update, customer.Element.Id, customer.Hash, Element(edited))]), ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Empty(result.Items[0].Diagnostics);
        var attributes = s.Model.Get<Entity>(customer.Element.Id)!.Attributes;
        Assert.Equal(before, attributes.Take(before.Count).Select(a => a.Id));
        var phone = attributes[^1];
        Assert.Equal("phone", phone.Name);
        Assert.True(IdFormat.IsValid(phone.Id));
        Assert.DoesNotContain(phone.Id, before);
    }

    [Fact]
    public async Task Resending_an_entity_without_attribute_ids_is_MQ1014_and_drops_no_seed_column()
    {
        await using var s = await BillingStore.OpenAsync();
        var customer = s.Doc("entity", "Customer");
        var attributes = Ids(customer.Json.GetProperty("attributes"));
        var seed = await s.Store.CreateAsync(Encoding.UTF8.GetBytes(
            $$"""{ "kind": "seed", "id": "01K7A00000000000000000005D", "name": "customers", "target": "{{customer.Element.Id}}", "columns": ["{{attributes[0]}}", "{{attributes[1]}}"], "rows": [ { "id": "01K7A00000000000000000005E", "values": ["0192a6f4-58a3-7c8e-9f2b-3c1d4e5f6a7b", "Ada"] } ] }"""),
            ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, seed.Outcome);
        customer = s.Doc("entity", "Customer");
        var files = s.Files();
        var resent = BillingStore.Edit(customer, n =>
        {
            foreach (var attribute in n["attributes"]!.AsArray())
                attribute!.AsObject().Remove("id");
        });

        var result = await s.Store.SaveAsync(customer.Element.Id, resent, customer.Hash, ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var refusals = result.Diagnostics.Where(d => d.Rule == "MQ1014").ToList();
        Assert.Equal(["/attributes/0", "/attributes/1", "/attributes/2", "/attributes/3"], refusals.Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.Contains(refusals, d => d.Message.Contains(attributes[1], StringComparison.Ordinal));
        Assert.Equal(files, s.Files());
        Assert.Equal(attributes.Take(2), s.Model.Get<Seed>(seed.Id!)!.Columns.Select(c => c.ToString()));
    }

    [Fact]
    public async Task A_reference_type_row_resent_without_its_id_is_MQ1014_whether_its_code_is_a_string_or_a_number()
    {
        // A row of a reference-type seed is named by its code, keyed as the name index keys it: "1" and 1 are one code.
        await using var s = await BillingStore.OpenAsync();
        const string Priority = "01K7A00000000000000000006A";
        const string Seed = "01K7A00000000000000000006D";
        const string Row = "01K7A00000000000000000006E";
        Assert.Equal(SaveOutcome.Saved, (await s.Store.CreateAsync(Encoding.UTF8.GetBytes(
            $$"""{ "kind": "reference-type", "id": "{{Priority}}", "name": "Priority", "code": { "id": "01K7A00000000000000000006B" }, "label": { "id": "01K7A00000000000000000006C" } }"""),
            ChangeSource.Cli, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await s.Store.CreateAsync(Encoding.UTF8.GetBytes(
            $$"""{ "kind": "seed", "id": "{{Seed}}", "name": "priorities", "target": "{{Priority}}", "columns": ["code", "label"], "rows": [ { "id": "{{Row}}", "values": ["1", "High"] } ] }"""),
            ChangeSource.Cli, Ct)).Outcome);
        var seed = s.Model.GetDocument(Seed)!;
        var files = s.Files();
        var resent = BillingStore.Edit(seed, n => n["rows"] = JsonNode.Parse("""[ { "values": [1, "High"] } ]"""));

        var result = await s.Store.SaveAsync(Seed, resent, seed.Hash, ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var refusal = Assert.Single(result.Diagnostics, d => d.Rule == "MQ1014");
        Assert.Equal("/rows/0", refusal.JsonPointer);
        Assert.Contains($"'1' ({Row})", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(files, s.Files());
    }
}
