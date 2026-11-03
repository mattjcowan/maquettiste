using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance tests of bindings that record their provenance and drift (next-phase-2-design.md section 3.3, task D36), each on a copy
/// of the billing fixture. <c>materialize-tables</c> stores Invoice's table in <c>main</c>, keeping its overlay's id (the overlay file
/// <c>tables/01j92p0v1t0j6rh4my9h81nyb4.json</c> becomes <c>tables/invoices.json</c>), and binds Invoice to
/// it with provenance; a change to that table is then drift that validation reports and one re-sync catches up with. The shared fixture
/// does not change.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class BindingProvenanceTests
{
    private const string Main = E2ERepo.MainDatabaseId;
    private const string Invoice = E2ERepo.InvoiceId;
    private const string InvoiceTable = "01J92P0V1T0J6RH4MY9H81NYB4";
    private const string InvoiceTablePath = ".maquettiste/model/databases/main/tables/invoices.json";
    private const string InvoicePath = ".maquettiste/model/entities/invoice.json";
    private const string NumberAttribute = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string DueOn = "01K9D0E0000000000000000001";
    private const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string BillingSchema = "01J92P0V1RC04SKQ5353EAKHG2";
    private const string InvoiceState = "01K9X3A0000000000000000001";
    private const string Events = "01K9X3C0000000000000000001";
    private const string EventsStatus = "01K9X3C0000000000000000004";
    private const string States = "01K9X3D0000000000000000001";
    private const string StatesCode = "01K9X3D0000000000000000002";
    private const string TablesFolder = ".maquettiste/model/databases/main/tables/";
    private const string Notes = "01K6BND0000000000000000001";
    private const string NotesBody = "01K6BND0000000000000000005";
    private const string InvoiceNote = "01K6BND0000000000000000010";
    private const string InvoiceNoteBody = "01K6BND0000000000000000013";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- setup ----

    /// <summary>A billing copy where <c>materialize-tables</c> has stored Invoice's table and bound Invoice to it with provenance.</summary>
    private static async Task<E2ERepo> MaterializedAsync()
    {
        var repo = E2ERepo.Create(demo: false);
        AssertSaved(await ApplyAsync(repo, $$"""{ "op": "materialize-tables", "database": "{{Main}}", "entities": ["{{Invoice}}"] }"""));
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        Assert.Equal(TableOrigin.Designed, snapshot.Get<Table>(InvoiceTable)!.Origin);
        Assert.Equal(InvoiceTablePath, snapshot.GetDocument(InvoiceTable)!.Path); // the overlay's file, renamed after its table
        var binding = Assert.Single(snapshot.Get<Entity>(Invoice)!.Bindings);
        Assert.Equal(InvoiceTable, binding.Source);
        Assert.NotNull(binding.Provenance);
        return repo;
    }

    private static async Task<BatchResult> ApplyAsync(E2ERepo repo, string operation)
    {
        await repo.Store.GetSnapshotAsync(Ct);
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await repo.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static string Resync(string entity, bool keepNames = false) =>
        $$"""{ "op": "resync-binding", "database": "{{Main}}", "entities": ["{{entity}}"]{{(keepNames ? ", \"keepNames\": true" : "")}} }""";

    private static void AssertSaved(BatchResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

    private static async Task<List<Diagnostic>> FindingsAsync(E2ERepo repo, string entity, params string[] rules) =>
        [.. (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics.Where(d => d.ElementId == entity && rules.Contains(d.Rule))];

    private static async Task<Entity> EntityAsync(E2ERepo repo, string id) => (await repo.Store.GetSnapshotAsync(Ct)).Get<Entity>(id)!;

    /// <summary>The value provenance records for a column of a table file as it is now.</summary>
    private static async Task<string> ShapeAsync(E2ERepo repo, string table, string column)
    {
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        return BindingDrift.ColumnsOf(snapshot, snapshot.Get<Table>(table)!).Single(c => c.Id == column).Shape;
    }

    /// <summary>Writes a model file on disk, canonically, before the store reads it.</summary>
    private static void Write(E2ERepo repo, string path, string schema, string json)
    {
        var full = repo.Repo.PathOf(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, TestServices.Json.Write(JsonNode.Parse(json)!.AsObject(), schema, path));
    }

    // ---- the acceptance lines ----

    [Fact]
    public async Task A_new_invoice_column_is_drift_and_one_resync_maps_it()
    {
        await using var repo = await MaterializedAsync();
        var entityBytes = repo.Repo.ReadFile(InvoicePath);

        await repo.EditAsync(InvoiceTable, t => t["columns"]!.AsArray().Add(new JsonObject { ["id"] = DueOn, ["name"] = "due_on", ["type"] = "date" }));

        // The entity's file is not touched: the binding links columns by id.
        Assert.Equal(entityBytes, repo.Repo.ReadFile(InvoicePath));
        var drift = await repo.Store.GetBindingDriftAsync(new BindingDriftQuery("main", [Invoice]), Ct);
        var entry = Assert.Single(drift.Bindings);
        Assert.Equal(("Invoice", true, false), (entry.EntityName, entry.HasProvenance, entry.Stale));
        Assert.Equal(new DriftColumn(DueOn, "due_on", "unaccounted"), Assert.Single(entry.Added));
        Assert.Empty(entry.Renamed);
        Assert.Empty(entry.Retyped);

        // MQ4047 is a warning, and an error that fails validation when the project says so.
        var warning = Assert.Single(await FindingsAsync(repo, Invoice, "MQ4047", "MQ4087"));
        Assert.Equal(("MQ4047", DiagnosticSeverity.Warning), (warning.Rule, warning.Severity));
        Assert.Contains("'due_on'", warning.Message, StringComparison.Ordinal);
        var settings = await repo.Store.GetSettingsAsync(Ct);
        var json = JsonNode.Parse(settings.Json.GetRawText())!.AsObject();
        json["validation"] = new JsonObject { ["rules"] = new JsonObject { ["MQ4047"] = "error" } };
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), settings.Hash, ChangeSource.Editor, Ct)).Outcome);
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(await FindingsAsync(repo, Invoice, "MQ4047")).Severity);

        // One re-sync writes the attribute, its field and the column's provenance entry in one batch.
        AssertSaved(await ApplyAsync(repo, Resync(Invoice)));
        var entity = await EntityAsync(repo, Invoice);
        var dueOn = Assert.Single(entity.Attributes, a => a.Name == "dueOn");
        Assert.Equal(("date", false), (dueOn.Type.Builtin, dueOn.Required));
        var binding = Assert.Single(entity.Bindings);
        Assert.Contains(binding.Fields, f => f.Attribute == dueOn.Id && f.Column == DueOn);
        Assert.Equal(await ShapeAsync(repo, InvoiceTable, DueOn), binding.Provenance!.Columns[DueOn]);
        Assert.Empty(await FindingsAsync(repo, Invoice, "MQ4047", "MQ4087"));
    }

    [Fact]
    public async Task A_renamed_invoice_column_is_MQ4087_and_resync_renames_the_attribute()
    {
        foreach (var keepNames in new[] { false, true })
        {
            await using var repo = await MaterializedAsync();
            var number = (await repo.Store.GetSnapshotAsync(Ct)).Get<Table>(InvoiceTable)!.Columns.Single(c => c.Name == "number").Id;
            await repo.EditAsync(InvoiceTable, t => t["columns"]!.AsArray().OfType<JsonObject>().Single(c => (string?)c["name"] == "number")["name"] = "invoice_number");

            var finding = Assert.Single(await FindingsAsync(repo, Invoice, "MQ4047", "MQ4087", "MQ4088"));
            Assert.Equal(("MQ4087", DiagnosticSeverity.Warning, InvoicePath, "/bindings/0"), (finding.Rule, finding.Severity, finding.FilePath, finding.JsonPointer));
            Assert.Contains("'invoice_number' renamed (attribute 'number')", finding.Message, StringComparison.Ordinal);

            AssertSaved(await ApplyAsync(repo, Resync(Invoice, keepNames)));

            var attribute = Assert.Single((await EntityAsync(repo, Invoice)).Attributes, a => a.Id == NumberAttribute);
            Assert.Equal(keepNames ? "number" : "invoiceNumber", attribute.Name);
            var recorded = Assert.Single((await EntityAsync(repo, Invoice)).Bindings).Provenance!.Columns[number];
            Assert.Equal(ColumnShapeHash.NamePart("invoice_number"), ColumnShapeHash.NamePartOf(recorded));
            Assert.Equal(await ShapeAsync(repo, InvoiceTable, number), recorded);
            Assert.Empty(await FindingsAsync(repo, Invoice, "MQ4047", "MQ4087", "MQ4088"));
        }
    }

    [Fact]
    public async Task An_event_status_typed_by_a_reference_type_becomes_one_attribute()
    {
        // 3.2's first copy, with InvoiceState bound to invoice_states and invoice_events declaring fk_event_state from status to its code.
        await using var repo = E2ERepo.Create(demo: false);
        Write(repo, TablesFolder + "invoice-states.json", "table.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{States}}","name":"invoice_states","database":"{{Main}}",
             "schema":"{{BillingSchema}}","columns":[
              {"id":"{{StatesCode}}","name":"code","type":"string","length":1,"nullable":false},
              {"id":"01K9X3D0000000000000000003","name":"label","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{StatesCode}}"]} }
            """);
        Write(repo, ".maquettiste/model/reference-types/invoice-state.json", "reference-type.json", $$"""
            {"$schema":"../../.schema/v1/reference-type.json","kind":"reference-type","id":"{{InvoiceState}}","name":"InvoiceState","package":"{{Billing}}",
             "code":{"id":"01K9X3A0000000000000000002","length":1},"label":{"id":"01K9X3A0000000000000000003","length":40},
             "bindings":[{"id":"01K9X3D0000000000000000004","database":"{{Main}}","table":"{{States}}",
               "fields":[{"attribute":"code","column":"{{StatesCode}}"},{"attribute":"label","column":"01K9X3D0000000000000000003"}]}]}
            """);
        Write(repo, ".maquettiste/model/seeds/invoice-state/invoice-state.json", "seed.json", $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01K9X3A0000000000000000010","name":"invoiceStates","target":"{{InvoiceState}}",
             "columns":["code","label"],"rows":[{"id":"01K9X3A0000000000000000011","values":["D","Draft"]},{"id":"01K9X3A0000000000000000012","values":["I","Issued"]},
               {"id":"01K9X3A0000000000000000013","values":["P","Paid"]},{"id":"01K9X3A0000000000000000014","values":["V","Void"]}]}
            """);
        Write(repo, TablesFolder + "invoice-events.json", "table.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{Events}}","name":"invoice_events","database":"{{Main}}",
             "schema":"{{BillingSchema}}","columns":[
              {"id":"01K9X3C0000000000000000002","name":"id","type":"uuid","nullable":false},
              {"id":"01K9X3C0000000000000000003","name":"invoice_id","type":"uuid","nullable":false},
              {"id":"{{EventsStatus}}","name":"status","type":"string","typeRef":{"ref":"{{InvoiceState}}"},"length":1,"nullable":false},
              {"id":"01K9X3C0000000000000000005","name":"occurred_at","type":"datetimeoffset","nullable":false}],
             "primaryKey":{"columns":["01K9X3C0000000000000000002"]},
             "foreignKeys":[{"id":"01K9X3C0000000000000000006","name":"fk_event_state","columns":["{{EventsStatus}}"],"referencesTable":"{{States}}"}] }
            """);
        await repo.Store.GetSnapshotAsync(Ct);
        var request = new MaterializeRequest("materialize-entities", Main, [Events], null, Billing);

        var plan = await repo.Store.PlanMaterializeAsync(request, Ct);

        Assert.True(plan.Valid, string.Join("; ", plan.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Contains(plan.Notes, n => n.Contains("fk_event_state", StringComparison.Ordinal)
            && n.Contains("attribute 'status' of entity 'InvoiceEvent' is typed by 'InvoiceState'", StringComparison.Ordinal));
        AssertSaved(await ApplyAsync(repo, $$"""{ "op": "materialize-entities", "database": "{{Main}}", "tables": ["{{Events}}"], "package": "{{Billing}}" }"""));

        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        var events = snapshot.All<Entity>().Single(e => e.Name == "InvoiceEvent");
        var status = Assert.Single(events.Attributes, a => a.Name == "status");
        Assert.Equal((InvoiceState, true, (int?)null), (status.Type.Ref, status.Required, status.Length));
        Assert.DoesNotContain(snapshot.All<Relation>(), r => r.Name == "fk_event_state" || r.Ends.Any(e => e.Entity == events.Id));
        Assert.DoesNotContain(snapshot.All<Mapping>(), m => m.ForeignKey == "01K9X3C0000000000000000006");
        var binding = Assert.Single(events.Bindings);
        Assert.Equal(
            ["01K9X3C0000000000000000002", "01K9X3C0000000000000000003", EventsStatus, "01K9X3C0000000000000000005"],
            binding.Provenance!.Columns.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_invoice_note_body_shows_the_column_comment()
    {
        await using var repo = E2ERepo.Create(demo: false);
        E2ERepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper"), Path.Combine(repo.Repo.ModelRoot, "templates", "csharp-dapper"));
        var notesPath = TablesFolder + "notes.json";
        var notes = JsonNode.Parse(File.ReadAllBytes(repo.Repo.PathOf(notesPath)))!.AsObject();
        notes["columns"]!.AsArray().OfType<JsonObject>().Single(c => (string?)c["id"] == NotesBody)["comment"] = "Text of the note, as typed.";
        File.WriteAllBytes(repo.Repo.PathOf(notesPath), TestServices.Json.Write(notes, "table.json", notesPath));

        var body = await BodyAsync(repo);
        Assert.Equal(("Text of the note, as typed.", NotesBody), (body.Description, body.DescriptionColumn?.Key));
        var record = await BodyRecordAsync(repo);
        Assert.Equal(("Text of the note, as typed.", NotesBody), (record.Description, record.DescriptionColumn));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Apply, packs: ["csharp-dapper"]));
        Assert.Contains("    /// <summary>Text of the note, as typed.</summary>\n    public string Body ", NoteClass(repo), StringComparison.Ordinal);

        // A description of the attribute's own replaces both.
        await repo.EditAsync(InvoiceNote, e => e["attributes"]!.AsArray().OfType<JsonObject>().Single(a => (string?)a["id"] == InvoiceNoteBody)["description"] = "What the author wrote.");
        body = await BodyAsync(repo);
        Assert.Equal(("What the author wrote.", (string?)null), (body.Description, body.DescriptionColumn?.Key));
        record = await BodyRecordAsync(repo);
        Assert.Equal(("What the author wrote.", (string?)null), (record.Description, record.DescriptionColumn));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Apply, packs: ["csharp-dapper"]));
        var regenerated = NoteClass(repo);
        Assert.Contains("    /// <summary>What the author wrote.</summary>\n    public string Body ", regenerated, StringComparison.Ordinal);
        Assert.DoesNotContain("Text of the note, as typed.", regenerated, StringComparison.Ordinal);

        static async Task<RAttribute> BodyAsync(E2ERepo repo)
        {
            var snapshot = await repo.Store.GetSnapshotAsync(Ct);
            var model = await repo.Store.Services.Resolver.ResolveAsync(snapshot, null, Ct);
            return model.Entities.Single(e => e.Id == InvoiceNote).Attributes.Single(a => a.Name == "body");
        }

        static async Task<AttributeRecord> BodyRecordAsync(E2ERepo repo)
        {
            var page = await repo.Service.GetResolvedAsync(new ResolvedQuery("entities", Limit: ModelPages.MaxLimit), Ct);
            Assert.Empty(page.Diagnostics);
            return page.Items.OfType<EntityRecord>().Single(e => e.Id == InvoiceNote).Attributes.Single(a => a.Name == "body");
        }

        static string NoteClass(E2ERepo repo) =>
            repo.Repo.ReadFile(Assert.Single(repo.Repo.ListFiles(), p => Path.GetFileName(p) == "InvoiceNote.g.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
