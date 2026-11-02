using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Acceptance of the reference-type refactors (next-phase-2-design.md section 3.5, "Tests"), over a billing copy with the shared setup
/// (<see cref="PaymentTerms"/>), an Invoice seed <c>DemoInvoices</c> (the design's demo-invoices; a seed name is an identifier) whose cells are <c>net30</c> and <c>due</c>, and a German label on
/// the <c>due</c> row: a rename keeps the old code as a former code and records one value map, a retire removes the row and moves its
/// uses, and a merge moves every use of the merged type to the target. sql-ddl's data step for each is in the packs' ReferenceDataTests.
/// </summary>
public sealed class ReferenceRefactorTests
{
    private const string DemoInvoices = "01KAPT00000000000000000040";
    private const string DueLabel = "Sofort fällig";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<E2ERepo> SetupAsync()
    {
        var repo = PaymentTerms.Create(s => s["localization"] = JsonNode.Parse("""{ "defaultLocale": "en", "locales": ["en", "de"] }"""));
        await PaymentTerms.AddAsync(repo);
        await repo.CreateElementAsync($$"""
            { "kind": "seed", "id": "{{DemoInvoices}}", "name": "DemoInvoices", "target": "{{E2ERepo.InvoiceId}}",
              "columns": ["01J92P0V0Q9EK961M5HAQ3C5MY", "01J92P0V0R3VSP7D5238DTNZX1", "01J92P0V0SNXS6PZ42VDK42HP9", "{{PaymentTerms.InvoicePaymentTerm}}", "01J92P0V26XYZRDQ8FJ6KZXT6S"],
              "rows": [ { "id": "01KAPT00000000000000000041", "values": ["0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a01", "INV-1", "2026-01-31", "net30", "2026-01-31T09:00:00Z"] },
                        { "id": "01KAPT00000000000000000042", "values": ["0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a02", "INV-2", "2026-02-28", "due", "2026-02-28T09:00:00Z"] } ] }
            """);
        var hashes = (await repo.Store.GetTranslationsAsync("de", null, null, Ct)).ShardHashes;
        var translated = await repo.Store.SaveTranslationsAsync("de", [new TranslationEdit(PaymentTerms.Due, "label", DueLabel)], hashes, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, translated.Outcome);
        return repo;
    }

    /// <summary>A document's JSON as the store holds it.</summary>
    private static async Task<JsonObject> DocumentAsync(E2ERepo repo, string id) =>
        JsonNode.Parse((await repo.Store.GetElementAsync(id, Ct))!.Json.GetRawText())!.AsObject();

    private static async Task<string?> PaymentTermDefaultAsync(E2ERepo repo) =>
        (string?)(await DocumentAsync(repo, E2ERepo.InvoiceId))["attributes"]!.AsArray().Single(a => (string?)a!["id"] == PaymentTerms.InvoicePaymentTerm)!["default"];

    private static async Task<List<string?>> DemoTermsAsync(E2ERepo repo) =>
        [.. (await DocumentAsync(repo, DemoInvoices))["rows"]!.AsArray().Select(r => (string?)r!["values"]![3])];

    private static async Task<List<(string Code, string ReplacedBy)>> FormerCodesAsync(E2ERepo repo) =>
        [.. ((await DocumentAsync(repo, PaymentTerms.PaymentTerm))["formerCodes"]?.AsArray() ?? []).Select(f => ((string)f!["code"]!, (string)f["replacedBy"]!))];

    private static async Task AssertValidAsync(E2ERepo repo)
    {
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.True(report.Errors == 0, string.Join("\n", report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Rule} {d.ElementId} {d.JsonPointer} {d.Message}")));
    }

    [Fact]
    public async Task Renaming_net30_keeps_a_former_code_and_records_one_value_map()
    {
        await using var repo = await SetupAsync();
        // A second database that stores no codes keeps the change's reach to some databases, so the dry run computes its facts (a dry
        // run that reaches every database leaves them to the save).
        await repo.CreateElementAsync("""
            { "kind": "database", "id": "01KAPD00000000000000000031", "name": "archive", "dialect": "postgresql", "version": "16", "defaultSchema": "archive",
              "schemas": [ { "id": "01KAPD00000000000000000032", "name": "archive" } ], "byConvention": "none" }
            """);
        await repo.RestartAsync();
        var operation = $$"""{ "op": "rename-code", "id": "{{PaymentTerms.PaymentTerm}}", "code": "net30", "to": "net-30" }""";
        var typeFile = repo.Repo.ReadFile(".maquettiste/model/reference-types/payment-term.json");

        // The dry run writes nothing; its usage lists the default of paymentTerm and the cell of DemoInvoices, and its facts one value
        // map in main for invoices.payment_term.
        var preview = await repo.Store.PreviewBatchAsync(PaymentTerms.Batch(repo, operation), Ct);
        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Equal(typeFile, repo.Repo.ReadFile(".maquettiste/model/reference-types/payment-term.json"));
        Assert.Empty(await PaymentTerms.ValueMapsAsync(repo, E2ERepo.MainDatabaseId));
        var usage = Assert.Single(preview.Items, i => i.Usage is not null).Usage!;
        Assert.Equal(2, usage.UseCount);
        Assert.Equal([E2ERepo.InvoiceId, DemoInvoices], usage.Uses.Select(u => u.FromElementId));
        Assert.Equal("/rows/0/values/3", usage.Uses[1].JsonPointer);
        var planned = Assert.Single(preview.Facts!);
        Assert.Equal((E2ERepo.MainDatabaseId, "value-map", "maps PaymentTerm codes net30 -> net-30 in 1 column"), (planned.Database, planned.Fact, planned.Summary));

        await PaymentTerms.ApplyAsync(repo, operation);

        Assert.Equal([("net30", "01KAPT00000000000000000011")], await FormerCodesAsync(repo));
        Assert.Equal("net-30", await PaymentTermDefaultAsync(repo));
        Assert.Equal(["net-30", "due"], await DemoTermsAsync(repo));
        var map = Assert.Single(await PaymentTerms.ValueMapsAsync(repo, E2ERepo.MainDatabaseId));
        Assert.Equal(("invoices", "payment_term"), (Assert.Single(map.Objects).TableName, map.Objects[0].Name));
        Assert.Equal([("net30", "net-30")], map.Values.Select(v => (v.From, v.To)));
        await AssertValidAsync(repo);
    }

    [Fact]
    public async Task Retiring_due_into_net60_removes_its_row_and_moves_its_uses()
    {
        await using var repo = await SetupAsync();
        Assert.Contains(DueLabel, repo.Repo.ReadFile(".maquettiste/model/locales/de/_reference-data.json"), StringComparison.Ordinal);

        await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "retire-code", "id": "{{PaymentTerms.PaymentTerm}}", "code": "due", "to": "net60" }""");

        // The due row and its German label are gone; the cell names net60; due is a former code replaced by the net60 row.
        var rows = (await DocumentAsync(repo, PaymentTerms.PaymentTermSeed))["rows"]!.AsArray();
        Assert.Equal([PaymentTerms.Net30, PaymentTerms.Net60], rows.Select(r => (string)r!["id"]!));
        var shard = ".maquettiste/model/locales/de/_reference-data.json";
        Assert.False(File.Exists(repo.Repo.PathOf(shard)) && repo.Repo.ReadFile(shard).Contains(PaymentTerms.Due, StringComparison.Ordinal));
        Assert.Equal(["net30", "net60"], await DemoTermsAsync(repo));
        Assert.Equal([("due", PaymentTerms.Net60)], await FormerCodesAsync(repo));
        var map = Assert.Single(await PaymentTerms.ValueMapsAsync(repo, E2ERepo.MainDatabaseId));
        Assert.Equal([("due", "net60")], map.Values.Select(v => (v.From, v.To)));
        await AssertValidAsync(repo);
    }

    [Fact]
    public async Task Merging_credit_terms_into_payment_terms_moves_every_use()
    {
        await using var repo = await SetupAsync();

        await PaymentTerms.ApplyAsync(repo, $$"""
            { "op": "merge-reference-types", "id": "{{PaymentTerms.CreditTerm}}", "target": "{{PaymentTerms.PaymentTerm}}", "codes": { "n90": "net60" } }
            """);

        // Customer.creditTerm and notes.term are typed by PaymentTerm; CreditTerm and its seed, whose rows all matched or mapped, are gone.
        var creditTerm = (await DocumentAsync(repo, E2ERepo.CustomerId))["attributes"]!.AsArray().Single(a => (string?)a!["id"] == PaymentTerms.CustomerCreditTerm)!;
        Assert.Equal(PaymentTerms.PaymentTerm, (string?)creditTerm["type"]!["ref"]);
        var term = (await DocumentAsync(repo, PaymentTerms.NotesTable))["columns"]!.AsArray().Single(c => (string?)c!["id"] == PaymentTerms.NotesTerm)!;
        Assert.Equal(PaymentTerms.PaymentTerm, (string?)term["typeRef"]!["ref"]);
        Assert.Null(await repo.Store.GetElementAsync(PaymentTerms.CreditTerm, Ct));
        Assert.Null(await repo.Store.GetElementAsync(PaymentTerms.CreditTermSeed, Ct));
        Assert.Equal([("n90", PaymentTerms.Net60)], await FormerCodesAsync(repo));

        // One value map in main maps n90 to net60 for both columns, from CreditTerm into PaymentTerm.
        var map = Assert.Single(await PaymentTerms.ValueMapsAsync(repo, E2ERepo.MainDatabaseId));
        Assert.Equal((PaymentTerms.PaymentTerm, PaymentTerms.CreditTerm, "CreditTerm", "PaymentTerm"), (map.ReferenceType, map.FromReferenceType, map.From!.Name, map.To!.Name));
        Assert.Equal([("n90", "net60")], map.Values.Select(v => (v.From, v.To)));
        Assert.Equal([("customers", "credit_term"), ("notes", "term")], map.Objects.Select(o => (o.TableName!, o.Name!)).Order());
        await AssertValidAsync(repo);
    }
}
