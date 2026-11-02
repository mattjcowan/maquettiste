using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance tests of columns typed by reference types, enums and scalar types (next-phase-2-design.md section 3.2, task D27),
/// each on a copy of the billing fixture with the sql-ddl and csharp-dapper packs. "The first copy" adds the reference type
/// <c>InvoiceState</c> (one-letter codes D, I, P and V), the designed table <c>invoice_events</c> whose <c>status</c> column is typed
/// by it, and the query <c>events-by-status</c>. The shared fixture does not change.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ColumnTypeTests
{
    private const string Main = E2ERepo.MainDatabaseId;
    private const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string BillingSchema = "01J92P0V1RC04SKQ5353EAKHG2";
    private const string InvoiceState = "01K9X3A0000000000000000001";
    private const string InvoiceStatus = "01J92P0V05WQDH2GZG539D9VG1";
    private const string Events = "01K9X3C0000000000000000001";
    private const string EventsStatus = "01K9X3C0000000000000000004";
    private const string States = "01K9X3D0000000000000000001";
    private const string StatesCode = "01K9X3D0000000000000000002";
    private const string Notes = "01K6BND0000000000000000001";
    private const string NotesId = "01K6BND0000000000000000002";
    private const string NotesBody = "01K6BND0000000000000000005";
    private const string InvoiceNote = "01K6BND0000000000000000010";
    private const string TablesFolder = ".maquettiste/model/databases/main/tables/";
    private const string EventsPath = TablesFolder + "invoice-events.json";
    private const string StatePath = ".maquettiste/model/reference-types/invoice-state.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- setup ----

    /// <summary>A copy of billing with the sql-ddl and csharp-dapper packs, nothing changed in the model yet.</summary>
    private static E2ERepo Copy()
    {
        var repo = E2ERepo.Create(demo: false);
        foreach (var pack in new[] { "sql-ddl", "csharp-dapper" })
            E2ERepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", pack), Path.Combine(repo.Repo.ModelRoot, "templates", pack));
        return repo;
    }

    /// <summary>
    /// The first copy, written on disk before the store reads it. <paramref name="bound"/> binds <c>InvoiceState</c> to the designed
    /// table <c>invoice_states</c>; <paramref name="foreignKeys"/> are <c>invoice_events</c>' declared foreign keys.
    /// </summary>
    private static E2ERepo FirstCopy(int codeLength = 1, bool bound = false, string foreignKeys = "[]")
    {
        var repo = Copy();
        var state = JsonNode.Parse($$"""
            {"$schema":"../../.schema/v1/reference-type.json","kind":"reference-type","id":"{{InvoiceState}}","name":"InvoiceState",
             "package":"{{Billing}}","code":{"id":"01K9X3A0000000000000000002","length":{{codeLength}}},"label":{"id":"01K9X3A0000000000000000003","length":40} }
            """)!.AsObject();
        if (bound)
        {
            Write(repo, TablesFolder + "invoice-states.json", "table.json", $$"""
                {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{States}}","name":"invoice_states","database":"{{Main}}",
                 "schema":"{{BillingSchema}}","columns":[
                  {"id":"{{StatesCode}}","name":"code","type":"string","length":1,"nullable":false},
                  {"id":"01K9X3D0000000000000000003","name":"label","type":"string","length":40,"nullable":false}],
                 "primaryKey":{"columns":["{{StatesCode}}"]} }
                """);
            state["bindings"] = JsonNode.Parse($$"""
                [{"id":"01K9X3D0000000000000000004","database":"{{Main}}","table":"{{States}}",
                  "fields":[{"attribute":"code","column":"{{StatesCode}}"},{"attribute":"label","column":"01K9X3D0000000000000000003"}]}]
                """);
        }

        Write(repo, StatePath, "reference-type.json", state.ToJsonString());
        Write(repo, ".maquettiste/model/seeds/invoice-state/invoice-state.json", "seed.json", $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01K9X3A0000000000000000010","name":"invoiceStates","target":"{{InvoiceState}}",
             "columns":["code","label"],"rows":[{"id":"01K9X3A0000000000000000011","values":["D","Draft"]},{"id":"01K9X3A0000000000000000012","values":["I","Issued"]},
               {"id":"01K9X3A0000000000000000013","values":["P","Paid"]},{"id":"01K9X3A0000000000000000014","values":["V","Void"]}]}
            """);
        Write(repo, EventsPath, "table.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{Events}}","name":"invoice_events","database":"{{Main}}",
             "schema":"{{BillingSchema}}","columns":[
              {"id":"01K9X3C0000000000000000002","name":"id","type":"uuid","nullable":false},
              {"id":"01K9X3C0000000000000000003","name":"invoice_id","type":"uuid","nullable":false},
              {"id":"{{EventsStatus}}","name":"status","type":"string","typeRef":{"ref":"{{InvoiceState}}"},"length":1,"nullable":false},
              {"id":"01K9X3C0000000000000000005","name":"occurred_at","type":"datetimeoffset","nullable":false}],
             "primaryKey":{"columns":["01K9X3C0000000000000000002"]},"foreignKeys":{{foreignKeys}} }
            """);
        Write(repo, ".maquettiste/model/databases/main/queries/events-by-status.json", "query.json", $$"""
            {"$schema":"../../../../.schema/v1/query.json","kind":"query","id":"01K9X3F0000000000000000001","name":"events-by-status","database":"{{Main}}",
             "from":{"source":"{{Events}}","alias":"e"},
             "select":[{"name":"id","expression":{"column":"e.01K9X3C0000000000000000002"} },{"name":"status","expression":{"column":"e.{{EventsStatus}}"} }]}
            """);
        return repo;
    }

    /// <summary>Writes a model file on disk, canonically, before the store reads it.</summary>
    private static void Write(E2ERepo repo, string path, string schema, string json) =>
        File.WriteAllBytes(EnsureFolder(repo.Repo.PathOf(path)), TestServices.Json.Write(JsonNode.Parse(json)!.AsObject(), schema, path));

    private static string EnsureFolder(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Rewrites a model file on disk, canonically, before the store reads it.</summary>
    private static void EditOnDisk(E2ERepo repo, string path, string schema, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllBytes(repo.Repo.PathOf(path)))!.AsObject();
        change(node);
        File.WriteAllBytes(repo.Repo.PathOf(path), TestServices.Json.Write(node, schema, path));
    }

    // ---- reading ----

    private static async Task<ResolvedModel> ResolveAsync(E2ERepo repo)
    {
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        return await repo.Store.Services.Resolver.ResolveAsync(snapshot, null, Ct);
    }

    private static RColumn Column(ResolvedModel model, string table, string column) =>
        model.Databases.Single(d => d.Id == Main).Tables.Single(t => t.Name == table).Columns.Single(c => c.Name == column);

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(E2ERepo repo) =>
        (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    private static async Task<IReadOnlyList<Diagnostic>> ValidateCleanAsync(E2ERepo repo)
    {
        var diagnostics = await ValidateAsync(repo);
        Assert.True(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}")));
        return diagnostics;
    }

    private static async Task GenerateAsync(E2ERepo repo, params string[] packs) =>
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Apply, packs: packs));

    // ---- the acceptance lines ----

    [Fact]
    public async Task An_invoice_event_status_typed_by_a_reference_type_is_in_its_usage_and_typed_in_csharp()
    {
        await using var repo = FirstCopy();

        await ValidateCleanAsync(repo);
        var status = Column(await ResolveAsync(repo), "invoice_events", "status");
        Assert.Equal(("string", "reference", InvoiceState), (status.Type, status.TypeRef?.Kind, status.ReferenceType?.Id));

        // The usage lists no attribute and the one column, required, stored as the templates decide, in no bound table.
        var usage = (await repo.Store.GetReferenceTypeUsageAsync(InvoiceState, Ct))!;
        Assert.Empty(usage.Usages);
        var column = Assert.Single(usage.Columns);
        Assert.Equal((Events, EventsStatus, Main, true, (StorageChoice?)null, (string?)null),
            (column.Table, column.Column, column.Database, column.Required, column.Storage, column.BoundTable));

        await GenerateAsync(repo, "sql-ddl", "csharp-dapper");

        // csharp-dapper keeps the code's type and names the reference type in the record's summary.
        var query = repo.Repo.ReadFile(Assert.Single(repo.Repo.ListFiles(),
            p => p.StartsWith("src/Generated/", StringComparison.Ordinal) && Path.GetFileName(p).StartsWith("EventsByStatusQuery", StringComparison.Ordinal)));
        Assert.Matches(@"/// <summary>One row of query events-by-status\. Status holds a code of <see cref=""global::[\w.]+\.InvoiceState""/>\.</summary>", query);
        Assert.Contains("public sealed partial record EventsByStatusRow(Guid Id, string Status);", query, StringComparison.Ordinal);

        // sql-ddl's default realization is a lookup table, and the typed column gets its key into the lookup table's code.
        var seed = repo.Repo.ReadFile("db/main/seed.sql");
        Assert.Contains("ADD CONSTRAINT fk_invoice_events_status_ref FOREIGN KEY (status) REFERENCES", seed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typed_column_points_at_the_bound_table_once()
    {
        // Bound: no lookup table, and the typed column's key points at invoice_states (code), after the type's rows.
        await using (var repo = FirstCopy(bound: true))
        {
            await ValidateCleanAsync(repo);
            await GenerateAsync(repo, "sql-ddl");
            var seed = repo.Repo.ReadFile("db/main/seed.sql");
            Assert.DoesNotContain("invoice_states_ref", seed, StringComparison.Ordinal);
            Assert.DoesNotContain("CREATE TABLE", seed, StringComparison.Ordinal);
            var key = seed.IndexOf("ADD CONSTRAINT fk_invoice_events_status_ref FOREIGN KEY (status) REFERENCES billing.invoice_states (code);", StringComparison.Ordinal);
            var rows = seed.IndexOf("INSERT INTO billing.invoice_states", StringComparison.Ordinal);
            Assert.True(rows >= 0 && key > rows, seed);
            var usage = Assert.Single((await repo.Store.GetReferenceTypeUsageAsync(InvoiceState, Ct))!.Columns);
            Assert.Equal(States, usage.BoundTable);
        }

        // A declared key from status into the bound table is the link: schema.sql holds it and seed.sql writes no key of its own.
        var declared = $$"""[{"id":"01K9X3C0000000000000000006","name":"fk_event_state","columns":["{{EventsStatus}}"],"referencesTable":"{{States}}"}]""";
        await using (var repo = FirstCopy(bound: true, foreignKeys: declared))
        {
            await ValidateCleanAsync(repo);
            await GenerateAsync(repo, "sql-ddl");
            Assert.Contains("fk_event_state", repo.Repo.ReadFile("db/main/schema.sql"), StringComparison.Ordinal);
            Assert.DoesNotContain("fk_invoice_events_status_ref", repo.Repo.ReadFile("db/main/seed.sql"), StringComparison.Ordinal);
        }

        // Pointed at notes.id, the declared key does not reach the type's rows.
        var wrong = $$"""[{"id":"01K9X3C0000000000000000006","name":"fk_event_state","columns":["{{EventsStatus}}"],"referencesTable":"{{Notes}}"}]""";
        await using (var repo = FirstCopy(bound: true, foreignKeys: wrong))
        {
            var finding = Assert.Single(await ValidateAsync(repo), d => d.Rule == "MQ4084");
            Assert.Equal((DiagnosticSeverity.Error, EventsPath, "/foreignKeys/0"), (finding.Severity, finding.FilePath, finding.JsonPointer));
        }
    }

    [Fact]
    public async Task A_status_column_shorter_than_the_codes_is_refused()
    {
        await using var repo = FirstCopy(codeLength: 2);

        var finding = Assert.Single(await ValidateAsync(repo), d => d.Rule == "MQ4083");
        Assert.Equal((DiagnosticSeverity.Error, EventsPath, "/columns/2/length"), (finding.Severity, finding.FilePath, finding.JsonPointer));

        // Without its own length the column takes the code's.
        await repo.EditAsync(Events, t => t["columns"]![2]!.AsObject().Remove("length"));
        Assert.DoesNotContain(await ValidateCleanAsync(repo), d => d.Rule == "MQ4083");
        Assert.Equal(2, Column(await ResolveAsync(repo), "invoice_events", "status").Length);
    }

    [Fact]
    public async Task Note_body_sensitivity_is_said_once_and_shown_on_both_sides()
    {
        await using var repo = Copy();
        EditOnDisk(repo, TablesFolder + "notes.json", "table.json", t => t["columns"]![3]!["sensitive"] = "pii");

        var model = await ResolveAsync(repo);
        foreach (var entity in new[] { "InvoiceNote", "CustomerNote" })
            Assert.Equal("pii", model.Entities.Single(e => e.Name == entity).Attributes.Single(a => a.Name == "body").Sensitive);
        Assert.Equal("pii", Column(model, "notes", "body").Sensitive);
        Assert.DoesNotContain(await ValidateCleanAsync(repo), d => d.Rule == "MQ4085");

        // Said differently on the attribute: a warning on the binding field that maps body.
        await repo.EditAsync(InvoiceNote, e => e["attributes"]![2]!["sensitive"] = "secret");
        var finding = Assert.Single(await ValidateCleanAsync(repo), d => d.Rule == "MQ4085");
        Assert.Equal((DiagnosticSeverity.Warning, ".maquettiste/model/entities/invoice-note.json", "/bindings/0/fields/2"),
            (finding.Severity, finding.FilePath, finding.JsonPointer));
        Assert.Equal(NotesBody, (await repo.Store.GetSnapshotAsync(Ct)).Get<Entity>(InvoiceNote)!.Bindings.Single().Fields[2].Column);
    }

    [Fact]
    public async Task Materializing_invoice_keeps_status_typed_by_its_enum()
    {
        await using var repo = Copy();
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes($$"""
            {"operations":[ { "op": "materialize-tables", "database": "{{Main}}", "entities": ["{{E2ERepo.InvoiceId}}"] } ]}
            """));
        Assert.Empty(parsed.Diagnostics);
        var result = await repo.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        var invoices = snapshot.All<Table>().Single(t => t.Name == "invoices" && t.Database == Main);
        var status = invoices.Columns.Single(c => c.Name == "status");
        Assert.Equal(("string", 1, "\"D\"", InvoiceStatus), (status.Type, status.Length, status.Default?.GetRawText(), status.TypeRef?.Ref));
        await ValidateCleanAsync(repo);
    }
}
