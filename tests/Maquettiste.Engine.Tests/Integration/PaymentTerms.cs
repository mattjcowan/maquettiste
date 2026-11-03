using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The shared setup of the reference-type refactor tests (next-phase-2-design.md section 3.5, "Tests"), over a billing copy
/// (<see cref="E2ERepo"/>): the reference type <c>PaymentTerm</c> (codes <c>net30</c>, <c>net60</c> and <c>due</c>; a string code of
/// length 8), which types <c>Invoice.paymentTerm</c> (default <c>net30</c>), so <c>invoices.payment_term</c> in <c>main</c> holds its codes;
/// and <c>CreditTerm</c> (codes <c>net30</c> and <c>n90</c>), which types <c>Customer.creditTerm</c> and the column <c>term</c> added to the
/// notes table. The settings declare the strategies <c>lookup-table</c>, <c>check</c> and <c>native</c> and choose <c>lookup-table</c>.
/// </summary>
internal static class PaymentTerms
{
    public const string PaymentTerm = "01KAPT00000000000000000001";
    public const string PaymentTermSeed = "01KAPT00000000000000000010";
    public const string Net30 = "01KAPT00000000000000000011";
    public const string Net60 = "01KAPT00000000000000000012";
    public const string Due = "01KAPT00000000000000000013";
    public const string InvoicePaymentTerm = "01KAPT00000000000000000021";
    public const string CreditTerm = "01KACT00000000000000000001";
    public const string CreditTermSeed = "01KACT00000000000000000010";
    public const string CreditNet30 = "01KACT00000000000000000011";
    public const string N90 = "01KACT00000000000000000012";
    public const string CustomerCreditTerm = "01KACT00000000000000000021";
    public const string NotesTerm = "01KACT00000000000000000022";
    public const string NotesTable = "01K6BND0000000000000000001";

    /// <summary>The invoices table's key in main (a synthesized table with an overlay).</summary>
    public const string InvoicesTable = E2ERepo.InvoiceId + "@" + E2ERepo.MainDatabaseId;

    /// <summary>The customers table's key in main.</summary>
    public const string CustomersTable = E2ERepo.CustomerId + "@" + E2ERepo.MainDatabaseId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The settings of the setup: the three strategies, <c>lookup-table</c> for the project.</summary>
    public static void Settings(JsonObject settings)
    {
        settings["conventions"] = new JsonObject { ["referenceStorage"] = new JsonObject { ["strategy"] = "lookup-table" } };
        settings["referenceData"] = new JsonObject
        {
            ["strategies"] = new JsonObject
            {
                ["check"] = new JsonObject { ["description"] = "CHECK (col IN (...codes))" },
                ["lookup-table"] = new JsonObject { ["description"] = "Table keyed by code, FK from each column", ["collections"] = true },
                ["native"] = new JsonObject { ["collections"] = new JsonObject { ["*"] = false, ["postgresql"] = true } },
            },
        };
    }

    /// <summary>A billing copy with the setup's settings (and <paramref name="settings"/> after them).</summary>
    public static E2ERepo Create(Action<JsonObject>? settings = null) => E2ERepo.Create(demo: false, settings: s =>
    {
        Settings(s);
        settings?.Invoke(s);
    });

    /// <summary>
    /// Adds the two types, their seeds, the attributes and the notes column they type, through the store; then restarts it, so no
    /// capture is cached.
    /// </summary>
    /// <param name="repo">The billing copy (<see cref="Create"/>).</param>
    /// <param name="creditTerm">Extra keys of <c>CreditTerm</c> (such as its <c>storage</c>), JSON text after a comma.</param>
    public static async Task AddAsync(E2ERepo repo, string creditTerm = "")
    {
        await repo.CreateElementAsync($$"""
            { "kind": "reference-type", "id": "{{PaymentTerm}}", "name": "PaymentTerm",
              "code": { "id": "01KAPT00000000000000000002", "length": 8 }, "label": { "id": "01KAPT00000000000000000003", "length": 64 } }
            """);
        await repo.CreateElementAsync($$"""
            { "kind": "seed", "id": "{{PaymentTermSeed}}", "name": "PaymentTerms", "target": "{{PaymentTerm}}", "columns": ["code", "label"],
              "rows": [ { "id": "{{Net30}}", "values": ["net30", "Net 30"] }, { "id": "{{Net60}}", "values": ["net60", "Net 60"] },
                        { "id": "{{Due}}", "values": ["due", "Due on receipt"] } ] }
            """);
        await repo.CreateElementAsync($$"""
            { "kind": "reference-type", "id": "{{CreditTerm}}", "name": "CreditTerm",
              "code": { "id": "01KACT00000000000000000002", "length": 8 }, "label": { "id": "01KACT00000000000000000003", "length": 64 }{{creditTerm}} }
            """);
        await repo.CreateElementAsync($$"""
            { "kind": "seed", "id": "{{CreditTermSeed}}", "name": "CreditTerms", "target": "{{CreditTerm}}", "columns": ["code", "label"],
              "rows": [ { "id": "{{CreditNet30}}", "values": ["net30", "Net 30"] }, { "id": "{{N90}}", "values": ["n90", "Net 90"] } ] }
            """);
        await repo.EditAsync(E2ERepo.InvoiceId, e => e["attributes"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "{{InvoicePaymentTerm}}", "name": "paymentTerm", "type": { "ref": "{{PaymentTerm}}" }, "default": "net30" }
            """)));
        await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "{{CustomerCreditTerm}}", "name": "creditTerm", "type": { "ref": "{{CreditTerm}}" } }
            """)));
        await repo.EditAsync(NotesTable, e => e["columns"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "{{NotesTerm}}", "name": "term", "type": "string", "length": 8, "typeRef": { "ref": "{{CreditTerm}}" } }
            """)));
        await repo.RestartAsync();
    }

    /// <summary>Applies a batch of operations (JSON objects) through the store and asserts it saved.</summary>
    public static async Task<BatchResult> ApplyAsync(E2ERepo repo, string operations)
    {
        var result = await repo.Store.ApplyBatchAsync(Batch(repo, operations), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved,
            result.Outcome + ": " + string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
        return result;
    }

    /// <summary>Parses a batch of operations (JSON objects).</summary>
    public static ModelBatch Batch(E2ERepo repo, string operations)
    {
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes("{ \"operations\": [" + operations + "] }"));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return parsed.Batch;
    }

    /// <summary>The value-map facts of a database in the store's current model, by id.</summary>
    public static async Task<List<Migration>> ValueMapsAsync(E2ERepo repo, string databaseId) =>
        [.. (await repo.Store.GetSnapshotAsync(Ct)).All<Migration>()
            .Where(f => f.Fact == MigrationFact.ValueMap && string.Equals(f.Database, databaseId, StringComparison.Ordinal))
            .OrderBy(f => f.Id, StringComparer.Ordinal)];
}
