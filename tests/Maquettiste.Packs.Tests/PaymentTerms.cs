using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The shared setup of the reference-type refactor tests (next-phase-2-design.md section 3.5, "Tests") over a billing copy with the
/// sql-ddl pack: the reference type <c>PaymentTerm</c> (codes <c>net30</c>, <c>net60</c> and <c>due</c>; a string code of length 8),
/// which types <c>Invoice.paymentTerm</c> (default <c>net30</c>), so <c>invoices.payment_term</c> in
/// <c>main</c> holds its codes; and <c>CreditTerm</c> (codes <c>net30</c> and <c>n90</c>), which types <c>Customer.creditTerm</c> and the
/// column <c>term</c> added to the notes table. The settings declare the strategies <c>lookup-table</c>, <c>check</c> and <c>native</c>
/// and choose one for the project. Elements are created and refactored through a model store, as an agent would.
/// </summary>
internal static class PaymentTerms
{
    public const string MainId = "01J92P0V1QRN2181XM2ZWE02W4";
    public const string InvoiceId = "01J92P0V0FJ23CGSNKM7P1W5V7";
    public const string CustomerId = "01J92P0V0ETQKXXP951CMMNHH3";
    public const string NotesTable = "01K6BND0000000000000000001";
    public const string PaymentTerm = "01KAPT00000000000000000001";
    public const string PaymentTermSeed = "01KAPT00000000000000000010";
    public const string Due = "01KAPT00000000000000000013";
    public const string CreditTerm = "01KACT00000000000000000001";
    public const string AcceptedTerms = "01KAPT00000000000000000031";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A billing copy whose project stores reference types with <paramref name="strategy"/>.</summary>
    public static PackRepo Create(string strategy = "lookup-table")
    {
        var repo = PackRepo.Billing();
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["conventions"] = new JsonObject { ["referenceStorage"] = new JsonObject { ["strategy"] = strategy } };
            settings["referenceData"] = JsonNode.Parse("""
                { "strategies": {
                    "check": { "description": "CHECK (col IN (...codes))" },
                    "lookup-table": { "description": "Table keyed by code, FK from each column", "collections": true },
                    "native": { "collections": { "*": false, "postgresql": true } } } }
                """);
        });
        return repo;
    }

    /// <summary>
    /// Adds the two types, their seeds, the attributes and the notes column they type; with <paramref name="collection"/> also
    /// <c>Customer.acceptedTerms</c>, a collection of <c>PaymentTerm</c>.
    /// </summary>
    public static async Task AddAsync(PackRepo repo, bool collection = false)
    {
        await using var store = new ModelStore(repo.Repo.Options);
        await CreateAsync(store, $$"""
            { "kind": "reference-type", "id": "{{PaymentTerm}}", "name": "PaymentTerm",
              "code": { "id": "01KAPT00000000000000000002", "length": 8 }, "label": { "id": "01KAPT00000000000000000003", "length": 64 } }
            """);
        await CreateAsync(store, $$"""
            { "kind": "seed", "id": "{{PaymentTermSeed}}", "name": "PaymentTerms", "target": "{{PaymentTerm}}", "columns": ["code", "label"],
              "rows": [ { "id": "01KAPT00000000000000000011", "values": ["net30", "Net 30"] }, { "id": "01KAPT00000000000000000012", "values": ["net60", "Net 60"] },
                        { "id": "{{Due}}", "values": ["due", "Due on receipt"] } ] }
            """);
        await CreateAsync(store, $$"""
            { "kind": "reference-type", "id": "{{CreditTerm}}", "name": "CreditTerm",
              "code": { "id": "01KACT00000000000000000002", "length": 8 }, "label": { "id": "01KACT00000000000000000003", "length": 64 } }
            """);
        await CreateAsync(store, $$"""
            { "kind": "seed", "id": "01KACT00000000000000000010", "name": "CreditTerms", "target": "{{CreditTerm}}", "columns": ["code", "label"],
              "rows": [ { "id": "01KACT00000000000000000011", "values": ["net30", "Net 30"] }, { "id": "01KACT00000000000000000012", "values": ["n90", "Net 90"] } ] }
            """);
        await EditAsync(store, InvoiceId, e => e["attributes"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "01KAPT00000000000000000021", "name": "paymentTerm", "type": { "ref": "{{PaymentTerm}}" }, "default": "net30" }
            """)));
        await EditAsync(store, CustomerId, e =>
        {
            e["attributes"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "01KACT00000000000000000021", "name": "creditTerm", "type": { "ref": "{{CreditTerm}}" } }"""));
            if (collection)
                e["attributes"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "{{AcceptedTerms}}", "name": "acceptedTerms", "type": { "ref": "{{PaymentTerm}}" }, "collection": true }"""));
        });
        await EditAsync(store, NotesTable, e => e["columns"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "01KACT00000000000000000022", "name": "term", "type": "string", "length": 8, "typeRef": { "ref": "{{CreditTerm}}" } }
            """)));
    }

    /// <summary>Applies a batch of operations (JSON objects) through a model store and asserts it saved.</summary>
    public static async Task<BatchResult> ApplyAsync(PackRepo repo, string operations)
    {
        await using var store = new ModelStore(repo.Repo.Options);
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{ \"operations\": [" + operations + "] }"));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var result = await store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved,
            result.Outcome + ": " + string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
        return result;
    }

    /// <summary>Edits an element through a model store and asserts it saved.</summary>
    public static async Task<SaveResult> EditAsync(PackRepo repo, string id, Action<JsonObject> edit)
    {
        await using var store = new ModelStore(repo.Repo.Options);
        return await EditAsync(store, id, edit);
    }

    private static async Task CreateAsync(ModelStore store, string json)
    {
        var result = await store.CreateAsync(Encoding.UTF8.GetBytes(json), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
    }

    private static async Task<SaveResult> EditAsync(ModelStore store, string id, Action<JsonObject> edit)
    {
        var document = await store.GetElementAsync(id, Ct);
        Assert.NotNull(document);
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        var result = await store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return result;
    }
}
