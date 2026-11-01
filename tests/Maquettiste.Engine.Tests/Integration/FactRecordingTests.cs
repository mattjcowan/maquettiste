using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Snapshots;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Migration facts in a copy of the billing model (next-phase-1-design.md section 3.2): the kind, its rules and how pending facts
/// meet resolution and generation. Each test copies billing into a temporary folder.
/// </summary>
public sealed class FactRecordingTests
{
    private const string NotesTableId = "01K6BND0000000000000000001";
    private const string NotesBodyColumnId = "01K6BND0000000000000000005";
    private const string InvoiceNumberAttributeId = "01J92P0V0R3VSP7D5238DTNZX1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_recorded_fact_without_a_name_validates_clean()
    {
        await using var repo = E2ERepo.Create(demo: false);

        // A complete rename of the notes table's body column (from "text" to "body", the name the model shows): no name, no intent.
        var created = await repo.Store.CreateAsync(Encoding.UTF8.GetBytes($$"""
            { "kind": "migration", "database": "{{E2ERepo.MainDatabaseId}}", "fact": "rename",
              "object": { "kind": "column", "table": "{{NotesTableId}}", "key": "{{NotesBodyColumnId}}", "schema": "billing",
                "tableName": "notes", "name": "text" },
              "from": { "name": "text" }, "to": { "name": "body" } }
            """), ChangeSource.Cli, Ct);

        Assert.True(created.Outcome == SaveOutcome.Saved, string.Join('\n', created.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        Assert.Empty(created.Diagnostics);
        var document = Assert.IsType<ElementDocument>(created.Current);
        var fact = Assert.IsType<Migration>(document.Element);
        Assert.Equal("", fact.Name);
        Assert.Equal(MigrationFact.Rename, fact.Fact);
        Assert.Equal(".maquettiste/model/databases/main/migrations/open/" + fact.Id.ToLowerInvariant() + ".json", document.Path);

        var text = repo.Repo.ReadFile(document.Path);
        Assert.StartsWith("{\n  \"$schema\": \"../../../../../.schema/v1/migration.json\",\n  \"kind\": \"migration\",\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\": \"\"", text, StringComparison.Ordinal);

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.ElementId == fact.Id || d.FilePath == document.Path);
        Assert.Equal(0, report.Errors);

        // A restart reads it back from its file.
        await repo.RestartAsync();
        var reloaded = await repo.Store.GetElementAsync(fact.Id, Ct);
        Assert.Equal(document.Path, reloaded?.Path);
        Assert.DoesNotContain((await repo.Store.GetSnapshotAsync(Ct)).LoadDiagnostics, d => d.FilePath == document.Path);
    }

    [Fact]
    public async Task A_pending_fact_keeps_previews_and_resolved_reads_and_stops_generation()
    {
        await using var repo = E2ERepo.Create(demo: false);
        // A pending narrowing of the invoice number (text to varchar(20)): no conversion, no data check. Written as a merge would
        // leave it, then read on a restart.
        const string id = "01JFCT00000000000000000001";
        var path = ".maquettiste/model/databases/main/migrations/open/" + id.ToLowerInvariant() + ".json";
        repo.Repo.WriteFile(path, $$"""
            {
              "$schema": "../../../../../.schema/v1/migration.json",
              "kind": "migration",
              "id": "{{id}}",
              "database": "{{E2ERepo.MainDatabaseId}}",
              "fact": "change-type",
              "object": {
                "kind": "column",
                "table": "{{E2ERepo.InvoiceId}}@{{E2ERepo.MainDatabaseId}}",
                "key": "{{InvoiceNumberAttributeId}}",
                "schema": "billing",
                "tableName": "invoices",
                "name": "number"
              },
              "from": {
                "type": "string",
                "length": 32,
                "nativeType": "text"
              },
              "to": {
                "type": "string",
                "length": 32,
                "nativeType": "varchar(20)"
              }
            }

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        await repo.RestartAsync();

        // Validation names both missing fields; nothing else is wrong with the model.
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        var pending = report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Equal(["/conversion", "/dataCheck"], pending.Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.All(pending, d => Assert.Equal(("MQ4101", path), (d.Rule, d.FilePath)));

        // Previews and resolved reads keep working.
        var preview = await repo.Service.PreviewAsync("e2e", "table", NotesTableId, Ct);
        Assert.DoesNotContain(preview.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(preview.Files, f => f.Path.EndsWith("tables/notes.sql", StringComparison.Ordinal));
        var resolved = await repo.Service.GetResolvedAsync(new ResolvedQuery("entities"), Ct);
        Assert.Empty(resolved.Diagnostics);
        Assert.NotEmpty(resolved.Items);

        // Generation stops on it, and writes nothing.
        var run = await repo.RunAsync(GenerationMode.Apply);
        E2ERepo.AssertOutcome(RunOutcome.Invalid, run);
        Assert.Contains(run.Diagnostics, d => d.Rule == "MQ4101");
        Assert.DoesNotContain(repo.Repo.ListFiles(), E2ERepo.IsOutput);

        // Held, it is complete for validate, and generation runs.
        var held = repo.Repo.ReadFile(path).Replace("\n  }\n}\n", "\n  },\n  \"held\": true\n}\n", StringComparison.Ordinal);
        repo.Repo.WriteFile(path, held);
        await repo.RestartAsync();
        Assert.Equal(0, (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);
        await repo.ApplyAsync();
    }

    // ---------------------------------------------------------------- recording on store writes (A10)

    private const string NotesEntityTypeColumnId = "01K6BND0000000000000000003";
    private const string FactFolder = ".maquettiste/model/databases/main/migrations/open/";

    [Fact]
    public async Task Renaming_a_column_records_one_complete_rename_in_the_same_write()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var saved = await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text");

        var fact = Assert.Single(saved.Facts!);
        Assert.Equal(("rename", "complete", E2ERepo.MainDatabaseId), (fact.Fact, fact.State, fact.Database));
        Assert.Empty(fact.Missing);
        Assert.Equal("rename column billing.notes.body to text", fact.Summary);
        Assert.Equal(FactFolder + fact.Id.ToLowerInvariant() + ".json", fact.Path);
        Assert.Contains(saved.Changes!.Changed, c => c.Id == fact.Id);
        var recorded = Assert.IsType<Migration>((await repo.Store.GetElementAsync(fact.Id, Ct))!.Element);
        Assert.Equal(("column", NotesTableId, NotesBodyColumnId), (recorded.Object!.Kind, recorded.Object.Table, recorded.Object.Key));
        Assert.Equal(("body", "text"), (recorded.From!.Name, recorded.To!.Name));
        Assert.Equal(0, (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);
    }

    [Fact]
    public async Task Changing_an_attribute_length_records_a_change_type_on_its_column()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var saved = await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80);

        var fact = Assert.Single(saved.Facts!);
        Assert.Equal(("change-type", "pending"), (fact.Fact, fact.State));
        Assert.Equal(["conversion", "dataCheck"], fact.Missing);
        Assert.Equal("change-type column billing.customers.name varchar(120) to varchar(80)", fact.Summary);
        Assert.Equal(["/conversion", "/dataCheck"], saved.Diagnostics.Where(d => d.Rule == "MQ4101").Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.All(saved.Diagnostics.Where(d => d.Rule == "MQ4101"), d => Assert.Equal((fact.Id, fact.Path), (d.ElementId, d.FilePath)));
    }

    [Fact]
    public async Task Changing_an_attribute_length_under_a_pinned_native_type_records_nothing()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var saved = await repo.EditAsync(E2ERepo.InvoiceId, e => e["attributes"]!.AsArray().First(a => (string?)a!["name"] == "number")!["length"] = 20);
        Assert.Empty(saved.Facts!);
        Assert.Empty(FactPaths(repo));
    }

    [Fact]
    public async Task Record_off_records_nothing()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["migrations"] = new JsonObject { ["record"] = "off" });
        var saved = await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text");
        Assert.Empty(saved.Facts!);
        Assert.Empty(FactPaths(repo));
        Assert.Empty(repo.Store.RecordingResolves);
    }

    [Fact]
    public async Task Description_edits_resolve_no_database()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var saved = await repo.EditAsync(E2ERepo.CustomerId, e =>
        {
            e["description"] = "Someone we bill, monthly.";
            e["attributes"]![1]!["description"] = "The name on the invoice.";
            e["tags"] = new JsonArray("billing");
        });
        Assert.Empty(saved.Facts!);
        Assert.Empty(repo.Store.RecordingResolves);
    }

    [Fact]
    public async Task Batch_answers_carry_facts_on_the_batch_and_none_on_items()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var notes = (await repo.Store.GetElementAsync(NotesTableId, Ct))!;
        var customer = (await repo.Store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var notesJson = JsonNode.Parse(notes.Json.GetRawText())!;
        notesJson["columns"]!.AsArray().First(c => (string?)c!["id"] == NotesBodyColumnId)!["name"] = "text";
        var customerJson = JsonNode.Parse(customer.Json.GetRawText())!;
        customerJson["attributes"]![1]!["length"] = 80;
        var batch = repo.Store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["operations"] = new JsonArray(
                new JsonObject { ["op"] = "update", ["id"] = NotesTableId, ["expectedHash"] = notes.Hash, ["element"] = notesJson },
                new JsonObject { ["op"] = "update", ["id"] = E2ERepo.CustomerId, ["expectedHash"] = customer.Hash, ["element"] = customerJson }),
        }.ToJsonString()));
        var result = await repo.Store.ApplyBatchAsync(batch.Batch!, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(["change-type", "rename"], result.Facts!.Select(f => f.Fact).Order(StringComparer.Ordinal));
        Assert.All(result.Items, i => Assert.Null(i.Facts));
        Assert.Equal(2, FactPaths(repo).Count);
    }

    [Fact]
    public async Task Renaming_an_entity_records_renames_of_its_table_and_foreign_key_columns()
    {
        // Foreign key columns named by the entity they reference (the default names them by the end's role).
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["conventions"] = new JsonObject { ["foreignKeyColumn"] = "{entity}_{key}" });
        var saved = await repo.EditAsync(E2ERepo.CustomerId, e => e["name"] = "Client");

        Assert.All(saved.Facts!, f => Assert.Equal(("rename", "complete"), (f.Fact, f.State)));
        Assert.Equal(["rename column billing.invoices.customer_id to client_id", "rename table billing.customers to clients"],
            saved.Facts!.Select(f => f.Summary).Order(StringComparer.Ordinal));
        Assert.Equal(0, (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);
    }

    [Fact]
    public async Task Renaming_a_database_schema_records_one_schema_rename()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var result = await ApplyAsync(repo, new JsonObject
        {
            ["op"] = "rename-schema", ["id"] = E2ERepo.MainDatabaseId, ["schema"] = BillingSchemaId, ["name"] = "finance",
        });

        var fact = Assert.Single(result.Facts!);
        Assert.Equal(("rename", "complete", "rename schema billing to finance"), (fact.Fact, fact.State, fact.Summary));
    }

    [Fact]
    public async Task Materialize_records_one_rekey_from_its_key_map_and_no_drop()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var result = await ApplyAsync(repo, Materialize(E2ERepo.CustomerId));

        var fact = Assert.Single(result.Facts!);
        Assert.Equal(("rekey", "complete"), (fact.Fact, fact.State));
        var rekey = await FactAsync(repo, fact.Id);
        Assert.Contains(rekey.Keys, k => k is { Kind: "table", From: E2ERepo.CustomerId + "@" + E2ERepo.MainDatabaseId });
        Assert.Equal(0, (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);
    }

    [Fact]
    public async Task Undoing_a_materialize_records_the_rekey_back_and_no_drop()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var customer = (await repo.Store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var query = (await repo.Store.GetElementAsync(FindCustomersQueryId, Ct))!;
        var stored = await ApplyAsync(repo, Materialize(E2ERepo.CustomerId));
        var rekey = Assert.Single(stored.Facts!);
        var (undo, tableId) = await UndoMaterializeAsync(repo, customer, query);

        var back = Assert.Single(undo.Facts!);
        Assert.Equal("rekey", back.Fact);
        var keys = (await FactAsync(repo, back.Id)).Keys;
        Assert.Contains(keys, k => k is { Kind: "table" } && k.From == tableId && k.To == E2ERepo.CustomerId + "@" + E2ERepo.MainDatabaseId);
        Assert.Equal([rekey.Id], (await FactAsync(repo, back.Id)).After);
    }

    /// <summary>
    /// The undo the editor builds for a materialize of Customer: the entity and the query that read the projection as they were read
    /// before it (<paramref name="customer"/>, <paramref name="query"/>), and the stored table deleted.
    /// </summary>
    private static async Task<(BatchResult Result, string TableId)> UndoMaterializeAsync(E2ERepo repo, ElementDocument customer, ElementDocument query)
    {
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        var table = snapshot.All<Table>().Single(t => t.Database == E2ERepo.MainDatabaseId && t.Name == "customers");
        var result = await ApplyAsync(repo,
            new JsonObject
            {
                ["op"] = "update", ["id"] = E2ERepo.CustomerId, ["expectedHash"] = snapshot.GetDocument(E2ERepo.CustomerId)!.Hash,
                ["element"] = JsonNode.Parse(customer.Json.GetRawText()),
            },
            new JsonObject
            {
                ["op"] = "update", ["id"] = FindCustomersQueryId, ["expectedHash"] = snapshot.GetDocument(FindCustomersQueryId)!.Hash,
                ["element"] = JsonNode.Parse(query.Json.GetRawText()),
            },
            new JsonObject { ["op"] = "delete", ["id"] = table.Id, ["expectedHash"] = snapshot.GetDocument(table.Id)!.Hash });
        return (result, table.Id);
    }

    [Fact]
    public async Task A_rename_recorded_before_a_materialize_projects_onto_the_stored_column()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var renamed = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["name"] = "fullName")).Facts!);
        Assert.Equal("rename column billing.customers.name to full_name", renamed.Summary);
        await ApplyAsync(repo, Materialize(E2ERepo.CustomerId));

        var model = (await repo.Store.ResolvedAsync(await repo.Store.GetSnapshotAsync(Ct), Ct)).Model!;
        var table = model.Databases[0].Tables.Single(t => t.Name == "customers");
        Assert.NotEqual(E2ERepo.CustomerId + "@" + E2ERepo.MainDatabaseId, table.Key); // stored as a table file
        var column = table.Columns.Single(c => c.Name == "full_name");
        Assert.Equal(["name"], column.FormerNames);
        Assert.Equal(renamed.Id, column.Since);
    }

    [Fact]
    public async Task Deleting_a_column_and_adding_one_with_its_name_records_a_drop()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo);
        // The memo column goes and a new memo column (an explicit new id) takes its place: a drop, and nothing for the add.
        var saved = await repo.EditAsync(JournalTableId, e => e["columns"]!.AsArray().First(c => (string?)c!["id"] == JournalMemoColumnId)!["id"] = "01JFRD0000000000000000000A");

        var fact = Assert.Single(saved.Facts!);
        Assert.Equal(("drop", "pending", "drop column ledger.journal.memo"), (fact.Fact, fact.State, fact.Summary));
        Assert.Equal(["acknowledge"], fact.Missing);
        Assert.Equal(LedgerDatabaseId, fact.Database);
        Assert.StartsWith(".maquettiste/model/databases/ledger/migrations/open/", fact.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_restore_records_no_facts()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var library = new SnapshotLibrary(repo.Store);
        var before = await library.CreateAsync(new SnapshotCreateRequest("Before the rename"), Ct);
        await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text");
        Assert.Single(FactPaths(repo));

        var restored = await library.RestoreAsync(before.Id, new SnapshotRestoreRequest(), Ct);
        Assert.Equal(SnapshotRestoreOutcome.Restored, restored.Outcome);
        Assert.Empty(FactPaths(repo)); // the model rewinds, facts included, and the restore records none
        Assert.Empty((await repo.Store.GetSnapshotAsync(Ct)).All<Migration>());
    }

    [Fact]
    public async Task Apply_batch_dry_run_lists_the_facts_it_would_record()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo);
        var notes = (await repo.Store.GetElementAsync(NotesTableId, Ct))!;
        var json = JsonNode.Parse(notes.Json.GetRawText())!;
        json["columns"]!.AsArray().First(c => (string?)c!["id"] == NotesBodyColumnId)!["name"] = "text";
        var batch = Batch(repo, new JsonObject { ["op"] = "update", ["id"] = NotesTableId, ["expectedHash"] = notes.Hash, ["element"] = json });

        var preview = await repo.Store.PreviewBatchAsync(batch, Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        var fact = Assert.Single(preview.Facts!);
        Assert.Equal(("rename", "rename column billing.notes.body to text"), (fact.Fact, fact.Summary));
        var file = Assert.Single(preview.Files, f => f.Path == fact.Path);
        Assert.Equal(("created", fact.Id, "migration"), (file.Action, file.Id, file.Kind));
        Assert.Empty(FactPaths(repo));
        Assert.Equal([E2ERepo.MainDatabaseId, E2ERepo.MainDatabaseId], repo.Store.RecordingResolves);
    }

    [Fact]
    public async Task A_dry_run_that_reaches_every_database_computes_no_facts()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo);
        var entry = (await repo.Store.GetElementAsync(LedgerEntryId, Ct))!;
        var json = JsonNode.Parse(entry.Json.GetRawText())!;
        json["attributes"]![1]!["length"] = 100;
        var batch = Batch(repo, new JsonObject { ["op"] = "update", ["id"] = LedgerEntryId, ["expectedHash"] = entry.Hash, ["element"] = json });

        // The entry's table is in both databases: the dry run resolves neither and leaves the facts to the save.
        var preview = await repo.Store.PreviewBatchAsync(batch, Ct);
        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Null(preview.Facts);
        Assert.DoesNotContain(preview.Files, f => f.Path.Contains("/migrations/", StringComparison.Ordinal));
        Assert.Empty(repo.Store.RecordingResolves);

        var saved = await repo.Store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct);
        Assert.Equal([E2ERepo.MainDatabaseId, LedgerDatabaseId], saved.Facts!.Select(f => f.Database).Order(StringComparer.Ordinal));
        Assert.All(saved.Facts!, f => Assert.Equal(("change-type", "pending"), (f.Fact, f.State)));
    }

    [Fact]
    public async Task After_is_set_to_the_heads_on_the_same_table()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var a = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text")).Facts!);
        var b = Assert.Single((await RenameNotesColumnAsync(repo, NotesEntityTypeColumnId, "kind")).Facts!);
        var c = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        var d = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "body")).Facts!);

        Assert.Empty((await FactAsync(repo, a.Id)).After);
        Assert.Equal([a.Id], (await FactAsync(repo, b.Id)).After);
        Assert.Empty((await FactAsync(repo, c.Id)).After); // another table
        Assert.Equal([b.Id], (await FactAsync(repo, d.Id)).After); // b follows a: b is the table's only head
    }

    [Fact]
    public async Task A_second_narrowing_of_a_pending_column_keeps_one_pending_fact()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var first = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        var second = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 60)).Facts!);

        Assert.Equal((first.Id, first.Path, "pending"), (second.Id, second.Path, second.State));
        Assert.Equal(["conversion", "dataCheck"], second.Missing);
        Assert.Single(FactPaths(repo));
        var fact = await FactAsync(repo, first.Id);
        Assert.Equal((120, 60), (fact.From!.Length, fact.To!.Length));
    }

    [Fact]
    public async Task A_pending_fact_changed_on_disk_behind_the_model_is_read_from_the_disk_by_the_save_that_follows_it()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var first = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        // Someone holds the fact in a text editor; the store has not seen it yet.
        var text = HoldOnDisk(repo, first.Path);
        var customer = (await repo.Store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var node = JsonNode.Parse(customer.Json.GetRawText())!;
        node["attributes"]![1]!["length"] = 60;

        var saved = await repo.Store.SaveAsync(E2ERepo.CustomerId, Encoding.UTF8.GetBytes(node.ToJsonString()), customer.Hash, ChangeSource.Editor, Ct);

        // The store indexes the held fact and classifies again: a held fact no longer follows its column, so the save records a
        // second fact after it and leaves the hand edit as it was.
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(text, repo.Repo.ReadFile(first.Path));
        var second = Assert.Single(saved.Facts!);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(("change-type", "pending"), (second.Fact, second.State));
        Assert.Equal([first.Id], (await FactAsync(repo, second.Id)).After);
        Assert.DoesNotContain(saved.Diagnostics, d => string.Equals(d.ElementId, first.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_settings_save_reads_a_pending_fact_changed_on_disk_from_the_disk()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var first = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        var text = HoldOnDisk(repo, first.Path);
        var settings = await repo.Store.GetSettingsAsync(Ct);
        var json = JsonNode.Parse(settings.Json.GetRawText())!.AsObject();
        json["typeMaps"] = new JsonObject { ["postgresql"] = new JsonObject { ["string"] = "varchar(40)" } }; // retypes the column again

        var saved = await repo.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), settings.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(text, repo.Repo.ReadFile(first.Path));
        Assert.True((await FactAsync(repo, first.Id)).Held);
        Assert.DoesNotContain(saved.Facts!, f => string.Equals(f.Id, first.Id, StringComparison.Ordinal));
        Assert.Contains(saved.Facts!, f => f.Summary == "change-type column billing.customers.name varchar(80) to varchar(40)");
    }

    /// <summary>Holds a fact in its file behind the store's back, as a text editor would, and returns the new text.</summary>
    private static string HoldOnDisk(E2ERepo repo, string path)
    {
        var text = repo.Repo.ReadFile(path).Replace("\n  \"from\": {", "\n  \"held\": true,\n  \"from\": {", StringComparison.Ordinal);
        Assert.Contains("\"held\": true", text, StringComparison.Ordinal);
        repo.Repo.WriteFile(path, text);
        return text;
    }

    [Fact]
    public async Task A_widening_after_a_pending_narrowing_keeps_the_fact_pending()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var first = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        var widened = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 100)).Facts!);

        Assert.Equal((first.Id, "pending"), (widened.Id, widened.State));
        Assert.Equal(["conversion", "dataCheck"], widened.Missing); // 120 to 100 still narrows
        Assert.Single(FactPaths(repo));
        Assert.Equal((120, 100), ((await FactAsync(repo, first.Id)).From!.Length, (await FactAsync(repo, first.Id)).To!.Length));
    }

    [Fact]
    public async Task Objects_of_an_element_with_errors_record_no_facts_and_the_save_carries_MQ4110()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo);
        // The journal table already has an error (unicode on a uuid column, MQ4057), so a save that keeps it goes through.
        const string path = "model/databases/ledger/tables/journal.json";
        var node = JsonNode.Parse(repo.Repo.ReadFile(".maquettiste/" + path))!;
        node["columns"]![0]!["unicode"] = true;
        repo.Repo.WriteFile(".maquettiste/" + path, Encoding.UTF8.GetString(TestServices.Json.Write(node, "table.json", path)));
        await repo.RestartAsync();
        Assert.Contains((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ4057");

        var saved = await repo.EditAsync(JournalTableId, e => e["columns"]![1]!["name"] = "note");

        Assert.Empty(saved.Facts!);
        Assert.Empty(FactPaths(repo));
        var left = Assert.Single(saved.Diagnostics, d => d.Rule == "MQ4110");
        Assert.Equal((DiagnosticSeverity.Info, JournalTableId), (left.Severity, left.ElementId));
        Assert.Contains("database 'ledger'", left.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- cost and walker

    [Fact]
    public async Task A_change_in_one_database_resolves_only_that_database()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo);
        await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text"); // no capture yet: before and after
        await RenameNotesColumnAsync(repo, NotesBodyColumnId, "body"); // the cache holds the before capture
        var saved = await repo.EditAsync(JournalTableId, e => e["columns"]![1]!["name"] = "note");

        Assert.Equal([E2ERepo.MainDatabaseId, E2ERepo.MainDatabaseId, E2ERepo.MainDatabaseId, LedgerDatabaseId, LedgerDatabaseId], repo.Store.RecordingResolves);
        Assert.Equal((LedgerDatabaseId, "rename column ledger.journal.memo to note"), (Assert.Single(saved.Facts!).Database, saved.Facts![0].Summary));
    }

    [Fact]
    public async Task First_entity_save_after_load_resolves_only_its_databases()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo); // restarts: no capture, no shared resolve
        var saved = await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 200); // a widening: no fact

        Assert.Empty(saved.Facts!);
        Assert.Equal([E2ERepo.MainDatabaseId, E2ERepo.MainDatabaseId], repo.Store.RecordingResolves);
    }

    [Fact]
    public async Task A_package_edit_chooses_only_databases_whose_conventions_reach_it()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo);
        await repo.EditAsync(BillingPackageId, e => e["name"] = "Invoicing"); // no database's convention entries name it
        Assert.Empty(repo.Store.RecordingResolves);
        await repo.EditAsync(LedgerPackageId, e => e["name"] = "Books");
        Assert.Equal([LedgerDatabaseId, LedgerDatabaseId], repo.Store.RecordingResolves);
    }

    [Fact]
    public async Task Footprint_selection_covers_every_database_a_change_reshapes()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo);
        var changes = new (string Id, Action<JsonObject> Edit)[]
        {
            (E2ERepo.CustomerId, e => e["name"] = "Client"),
            (LedgerEntryId, e => e["attributes"]![1]!["length"] = 300),
            (MoneyId, e => e["attributes"]![0]!["precision"] = 20),
            (EmailAddressId, e => e["length"] = 320),
            (JournalTableId, e => e["columns"]![1]!["name"] = "note"),
            (LedgerEntryId, e => e["package"] = BillingPackageId),
            (LedgerPackageId, e => e["parent"] = BillingPackageId),
        };
        foreach (var (id, edit) in changes)
        {
            var before = await CapturesAsync(repo);
            var count = repo.Store.RecordingResolveCount;
            await repo.EditAsync(id, edit);
            var after = await CapturesAsync(repo);
            var chosen = repo.Store.RecordingResolvesSince(count).ToHashSet(StringComparer.Ordinal);
            foreach (var (database, capture) in before)
            {
                if (!string.Equals(capture, after.GetValueOrDefault(database), StringComparison.Ordinal))
                    Assert.True(chosen.Contains(database), $"Changing {id} reshaped {database}, which was not chosen.");
            }
        }
    }

    [Theory]
    [InlineData("models", "billing")]
    [InlineData("validation", "clean")]
    [InlineData("validation", "reference-data")]
    public async Task Database_reach_covers_every_resolved_footprint(string group, string fixture)
    {
        using var temp = new TempRepo();
        E2ERepo.CopyTree(Fixtures.Path(group, fixture), temp.RepoRoot);
        await using var store = new ModelStore(temp.Options);
        var snapshot = await store.GetSnapshotAsync(Ct);
        var whole = await store.Services.Resolver.ResolveAsync(snapshot, null, Ct);
        var resolver = Assert.IsType<ModelResolver>(store.Services.Resolver);
        var ids = snapshot.All<Database>().Select(d => d.Id).ToList();
        Assert.NotEmpty(ids);
        var each = (await resolver.ResolveDatabasesAsync(snapshot, ids, freeze: true, Ct)).Databases;
        foreach (var database in whole.Databases.Concat(each.OfType<RDatabase>()))
        {
            var reach = DatabaseReach.Of(snapshot, snapshot.Get<Database>(database.Id)!);
            var missing = ModelStore.Footprint(database).Where(id => !reach.Contains(id)).Order(StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, $"{fixture}: database {database.Name} depends on {string.Join(", ", missing)} outside its reach.");
        }
    }

    [Fact]
    public async Task Fact_object_keys_are_historical_and_never_dangle()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var renamed = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text")).Facts!);
        Assert.DoesNotContain(await repo.Store.GetReferencesAsync(NotesTableId, Ct), r => r.FromElementId == renamed.Id);
        Assert.DoesNotContain(await repo.Store.GetReferencesAsync(NotesBodyColumnId, Ct), r => r.FromElementId == renamed.Id);

        // The table goes: its keys stay in the fact as history, and nothing dangles.
        var notes = (await repo.Store.GetElementAsync(NotesTableId, Ct))!;
        var deleted = await repo.Store.DeleteAsync(NotesTableId, notes.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        Assert.Contains(deleted.Facts!, f => f is { Fact: "drop", State: "pending", Summary: "drop table billing.notes" });
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ2001");
        Assert.NotNull(await repo.Store.GetElementAsync(renamed.Id, Ct));
    }

    [Fact]
    public async Task Deleting_a_database_deletes_its_facts_and_records_none()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await AddLedgerAsync(repo);
        var renamed = Assert.Single((await repo.EditAsync(JournalTableId, e => e["columns"]![1]!["name"] = "note")).Facts!);
        var database = (await repo.Store.GetElementAsync(LedgerDatabaseId, Ct))!;

        var deleted = await repo.Store.DeleteAsync(LedgerDatabaseId, database.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        Assert.Empty(deleted.Facts!);
        Assert.Null(await repo.Store.GetElementAsync(renamed.Id, Ct));
        Assert.Empty(FactPaths(repo));
    }

    [Fact]
    public async Task A_dangling_after_link_is_a_warning_and_ignored_for_ordering()
    {
        await using var repo = E2ERepo.Create(demo: false);
        // A complete rename whose after names a fact another copy folded away.
        var created = await repo.Store.CreateAsync(Encoding.UTF8.GetBytes($$"""
            { "kind": "migration", "database": "{{E2ERepo.MainDatabaseId}}", "fact": "rename", "after": [ "01JFCT00000000000000000009" ],
              "object": { "kind": "column", "table": "{{NotesTableId}}", "key": "{{NotesBodyColumnId}}", "schema": "billing",
                "tableName": "notes", "name": "text" },
              "from": { "name": "text" }, "to": { "name": "body" } }
            """), ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.Empty(created.Facts!); // a write of facts alone records none

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.Equal(0, report.Errors);
        var warning = Assert.Single(report.Diagnostics, d => d.Rule == "MQ4111");
        Assert.Equal((DiagnosticSeverity.Warning, created.Id), (warning.Severity, warning.ElementId));

        // The next fact on the table follows it, and the order holds.
        var next = Assert.Single((await RenameNotesColumnAsync(repo, NotesEntityTypeColumnId, "kind")).Facts!);
        Assert.Equal([created.Id!], (await FactAsync(repo, next.Id)).After);
        var model = (await repo.Store.ResolvedAsync(await repo.Store.GetSnapshotAsync(Ct), Ct)).Model!;
        Assert.Equal([created.Id!, next.Id], model.Migrations.Select(m => m.Id));
    }

    [Fact]
    public async Task Deleting_a_fact_that_another_follows_reports_the_dangling_link_on_the_delete()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var first = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text")).Facts!);
        var second = Assert.Single((await repo.EditAsync(NotesTableId, e =>
            e["columns"]!.AsArray().First(c => (string?)c!["id"] == NotesEntityTypeColumnId)!["length"] = 16)).Facts!);
        Assert.Equal(("change-type", "pending"), (second.Fact, second.State));
        Assert.Equal([first.Id], (await FactAsync(repo, second.Id)).After);

        // The follower names the deleted fact in after, which is not a reference: the store still revisits it, and its own
        // pending intent (MQ4101, there before the delete) does not refuse the delete.
        var document = (await repo.Store.GetElementAsync(first.Id, Ct))!;
        var deleted = await repo.Store.DeleteAsync(first.Id, document.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        var dangling = Assert.Single(deleted.Diagnostics, d => d.Rule == "MQ4111");
        Assert.Equal((second.Id, DiagnosticSeverity.Warning, "/after/0"), (dangling.ElementId, dangling.Severity, dangling.JsonPointer));
        Assert.Contains(deleted.Diagnostics, d => d.Rule == "MQ4101" && d.ElementId == second.Id);
    }

    // ---------------------------------------------------------------- other write paths (A11)

    [Fact]
    public async Task A_conventions_change_records_renames_through_save_settings()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var settings = await repo.Store.GetSettingsAsync(Ct);
        var json = JsonNode.Parse(settings.Json.GetRawText())!.AsObject();
        json["conventions"] = new JsonObject { ["pluralTables"] = false };

        var saved = await repo.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), settings.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Contains("rename table billing.customers to customer", saved.Facts!.Select(f => f.Summary));
        Assert.All(saved.Facts!, f => Assert.Equal(("rename", "complete"), (f.Fact, f.State)));
        Assert.DoesNotContain(saved.Facts!, f => f.Summary.Contains(".notes", StringComparison.Ordinal)); // a designed table keeps its name
        Assert.Equal(saved.Facts!.Select(f => f.Path).Order(StringComparer.Ordinal), FactPaths(repo));

        // A settings change that shapes no table records nothing.
        var again = await repo.Store.GetSettingsAsync(Ct);
        var other = JsonNode.Parse(again.Json.GetRawText())!.AsObject();
        other["handEdits"] = "overwrite";
        var count = repo.Store.RecordingResolveCount;
        var plain = await repo.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(other.ToJsonString()), again.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, plain.Outcome);
        Assert.Empty(plain.Facts!);
        Assert.Equal(count, repo.Store.RecordingResolveCount);
    }

    [Fact]
    public async Task Default_locale_change_of_a_plural_name_records_a_table_rename()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["localization"] = new JsonObject
        {
            ["defaultLocale"] = "en", ["locales"] = new JsonArray("en", "fr"),
        });
        await repo.EditAsync(E2ERepo.CustomerId, e => e["pluralName"] = "Customers");
        var translated = await repo.Store.SaveTranslationsAsync("fr", [new TranslationEdit(E2ERepo.CustomerId, "pluralName", "Clients")],
            new Dictionary<string, string>(), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, translated.Outcome);
        var count = FactPaths(repo).Count;

        var changed = await repo.Store.ChangeDefaultLocaleAsync("fr", false, ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Saved, changed!.Outcome);
        var fact = Assert.Single(changed.Facts!);
        Assert.Equal(("rename", "rename table billing.customers to clients"), (fact.Fact, fact.Summary));
        Assert.Equal(count + 1, FactPaths(repo).Count);

        // The change reloads the whole model, so it hands no capture on: the next save resolves its database before the change too.
        var resolves = repo.Store.RecordingResolveCount;
        await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80);
        Assert.Equal([E2ERepo.MainDatabaseId, E2ERepo.MainDatabaseId], repo.Store.RecordingResolvesSince(resolves));
    }

    [Fact]
    public async Task A_hand_edit_made_before_a_default_locale_change_is_never_recorded()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["localization"] = new JsonObject
        {
            ["defaultLocale"] = "en", ["locales"] = new JsonArray("en", "fr"),
        });
        await repo.EditAsync(E2ERepo.CustomerId, e => e["pluralName"] = "Customers"); // captures the main database
        var translated = await repo.Store.SaveTranslationsAsync("fr", [new TranslationEdit(E2ERepo.CustomerId, "pluralName", "Clients")],
            new Dictionary<string, string>(), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, translated.Outcome);

        // The notes table's body column renamed in a text editor, behind the loaded model.
        var notesPath = (await repo.Store.GetElementAsync(NotesTableId, Ct))!.Path;
        repo.Repo.WriteFile(notesPath, repo.Repo.ReadFile(notesPath).Replace("\"name\": \"body\"", "\"name\": \"text\"", StringComparison.Ordinal));

        var changed = await repo.Store.ChangeDefaultLocaleAsync("fr", false, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, changed!.Outcome);
        Assert.Equal("rename table billing.customers to clients", Assert.Single(changed.Facts!).Summary);

        // The change's reload indexed the hand edit, and the next save that reshapes the database records only its own change.
        var notes = Assert.IsType<Table>((await repo.Store.GetElementAsync(NotesTableId, Ct))!.Element);
        Assert.Equal("text", notes.Columns.Single(c => c.Id == NotesBodyColumnId).Name);
        var saved = await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80);
        Assert.Equal("change-type column billing.clients.name varchar(120) to varchar(80)", Assert.Single(saved.Facts!).Summary);
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        Assert.DoesNotContain(snapshot.All<Migration>(), f => f.Object?.Table == NotesTableId);
    }

    // ---------------------------------------------------------------- validate --against and stale facts (A12)

    private const string InvoiceOverlayId = "01J92P0V1T0J6RH4MY9H81NYB4";

    [Fact]
    public async Task Narrowing_invoice_number_fails_validate_against_a_snapshot_until_conversion_and_data_check_are_recorded() =>
        await NarrowingAgainstAsync(folder: false);

    [Fact]
    public async Task Narrowing_invoice_number_fails_validate_against_a_folder_until_conversion_and_data_check_are_recorded() =>
        await NarrowingAgainstAsync(folder: true);

    /// <summary>
    /// The scope's done-when line: the overlay pins the invoice number to <c>text</c>, the baseline is taken, then it goes to
    /// <c>varchar(20)</c>. With recording off nothing explains the narrowing (MQ4102 at the overlay's column entry); recorded, the
    /// pending fact explains it but lacks its intent (MQ4101); with a conversion and a data check the model validates against the
    /// baseline.
    /// </summary>
    private static async Task NarrowingAgainstAsync(bool folder)
    {
        await using var repo = E2ERepo.Create(demo: false, settings: E2ERepo.RecordOff);
        using var copy = folder ? new TempRepo() : null;
        await SetNumberTypeAsync(repo, "text");
        var against = await BaselineAsync(repo, copy);
        await SetNumberTypeAsync(repo, "varchar(20)");

        var unexplained = await ValidateAgainstAsync(repo, against);
        var missing = Assert.Single(unexplained.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("MQ4102", InvoiceOverlayId, "/columns/0"), (missing.Rule, missing.ElementId, missing.JsonPointer));
        Assert.Equal(".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json", missing.FilePath);
        Assert.NotNull(missing.Line);
        Assert.Contains("change-type column billing.invoices.number text to varchar(20)", missing.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Errors); // plain validate has no baseline to compare with

        // Recorded: the narrowing is explained, and its fact is pending.
        await SetNumberTypeAsync(repo, "text");
        await SetRecordAsync(repo, "automatic");
        var fact = Assert.Single((await SetNumberTypeAsync(repo, "varchar(20)")).Facts!);
        Assert.Equal(("change-type", "pending"), (fact.Fact, fact.State));
        var pending = await ValidateAgainstAsync(repo, against);
        Assert.DoesNotContain(pending.Diagnostics, d => d.Rule == "MQ4102");
        Assert.Equal(["/conversion", "/dataCheck"], pending.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Rule + " " + d.JsonPointer).Order(StringComparer.Ordinal).Select(t => t["MQ4101 ".Length..]));

        // The conversion alone still lacks the data check a narrowing needs.
        await repo.EditAsync(fact.Id, e => e["conversion"] = new JsonObject { ["cast"] = true });
        var half = await ValidateAgainstAsync(repo, against);
        Assert.Equal(["MQ4101 /dataCheck"], half.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.JsonPointer));

        await repo.EditAsync(fact.Id, e => e["dataCheck"] = new JsonObject
        {
            ["query"] = new JsonObject { ["postgresql"] = "select 1 from {table} where length({column}) > 20" },
        });
        var complete = await ValidateAgainstAsync(repo, against);
        Assert.Equal(0, complete.Errors);
        Assert.DoesNotContain(complete.Diagnostics, d => d.Rule.StartsWith("MQ41", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changing_a_jsonb_column_to_json_needs_a_change_type_fact()
    {
        await using var repo = E2ERepo.Create(demo: false);
        const string metaId = "01K6BND00000000000000000M1";
        await repo.EditAsync(NotesTableId, e => e["columns"]!.AsArray().Add(new JsonObject
        {
            ["id"] = metaId, ["name"] = "meta", ["type"] = "json", ["nullable"] = true,
        }));
        var against = await BaselineAsync(repo, null);

        // jsonb to json is no widening: a conversion, and no data check (it is no narrowing either).
        var saved = await repo.EditAsync(NotesTableId, e => e["columns"]!.AsArray().First(c => (string?)c!["id"] == metaId)!["nativeType"] = "json");
        var fact = Assert.Single(saved.Facts!);
        Assert.Equal(("change-type", "pending"), (fact.Fact, fact.State));
        Assert.Equal(["conversion"], fact.Missing);
        Assert.Equal("change-type column billing.notes.meta jsonb to json", fact.Summary);
        var pending = await ValidateAgainstAsync(repo, against);
        Assert.Equal(["MQ4101 /conversion"], pending.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.JsonPointer));

        // Without the fact, the change is unexplained, at the column's entry in the table file.
        var document = (await repo.Store.GetElementAsync(fact.Id, Ct))!;
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.DeleteAsync(fact.Id, document.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct)).Outcome);
        var unexplained = Assert.Single((await ValidateAgainstAsync(repo, against)).Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("MQ4102", NotesTableId, "/columns/5"), (unexplained.Rule, unexplained.ElementId, unexplained.JsonPointer));
    }

    [Fact]
    public async Task Two_copies_that_each_rename_a_different_column_merge_with_no_conflict()
    {
        await using var a = E2ERepo.Create(demo: false);
        await using var b = E2ERepo.Create(demo: false, seed: 2);
        using var baseline = new TempRepo();
        E2ERepo.CopyTree(a.Repo.ModelRoot, baseline.ModelRoot);
        var before = Hashes(a);

        var renamedNote = Assert.Single((await RenameNotesColumnAsync(a, NotesBodyColumnId, "text")).Facts!);
        var renamedNumber = Assert.Single((await b.EditAsync(InvoiceOverlayId, e => e["columns"]![0]!["name"] = "invoice_number")).Facts!);
        Assert.Equal(("rename", "rename"), (renamedNote.Fact, renamedNumber.Fact));

        // The copies changed disjoint files: their union is a merge with no conflict.
        var changedA = Changed(before, Hashes(a));
        var changedB = Changed(before, Hashes(b));
        Assert.Empty(changedA.Intersect(changedB, StringComparer.Ordinal));
        foreach (var path in changedB)
            a.Repo.WriteFile(path, b.Repo.ReadFile(path));
        await a.RestartAsync();

        var report = await ValidateAgainstAsync(a, baseline.RepoRoot);
        Assert.Equal(0, report.Errors);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule.StartsWith("MQ41", StringComparison.Ordinal));
        var facts = (await a.Store.GetSnapshotAsync(Ct)).All<Migration>().Select(f => f.Id).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { renamedNote.Id, renamedNumber.Id }.Order(StringComparer.Ordinal), facts);
    }

    [Fact]
    public async Task Two_copies_that_each_rename_a_different_column_of_one_table_merge_with_no_conflict()
    {
        await using var a = E2ERepo.Create(demo: false);
        await using var b = E2ERepo.Create(demo: false, seed: 2);
        using var baseline = new TempRepo();
        E2ERepo.CopyTree(a.Repo.ModelRoot, baseline.ModelRoot);

        var body = Assert.Single((await RenameNotesColumnAsync(a, NotesBodyColumnId, "text")).Facts!);
        var kind = Assert.Single((await RenameNotesColumnAsync(b, NotesEntityTypeColumnId, "kind")).Facts!);

        // The merge: both renames in the table file (a line each), both fact files.
        const string notesPath = ".maquettiste/model/databases/main/tables/notes.json";
        var merged = JsonNode.Parse(a.Repo.ReadFile(notesPath))!;
        merged["columns"]!.AsArray().First(c => (string?)c!["id"] == NotesEntityTypeColumnId)!["name"] = "kind";
        a.Repo.WriteFile(notesPath, Encoding.UTF8.GetString(TestServices.Json.Write(merged, "table.json", "model/databases/main/tables/notes.json")));
        a.Repo.WriteFile(kind.Path, b.Repo.ReadFile(kind.Path));
        await a.RestartAsync();

        var report = await ValidateAgainstAsync(a, baseline.RepoRoot);
        Assert.Equal(0, report.Errors);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule.StartsWith("MQ41", StringComparison.Ordinal));
        Assert.Equal(new[] { body.Id, kind.Id }.Order(StringComparer.Ordinal),
            (await a.Store.GetSnapshotAsync(Ct)).All<Migration>().Select(f => f.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Open_facts_reach_templates_with_no_seal_and_no_physical_snapshot()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["packs"]!["facts"] = new JsonObject { ["output"] = "db/facts" });
        repo.Repo.WriteFile(".maquettiste/templates/facts/pack.json", """
            { "name": "facts", "version": "1.0.0", "engine": ">=1.0 <2.0", "description": "Open migration facts in templates.",
              "units": [ { "id": "table", "template": "table.scriban", "for": "each table", "where": { "database": "main" },
                "output": "{{ table.name }}.txt" } ] }
            """);
        repo.Repo.WriteFile(".maquettiste/templates/facts/table.scriban", """
            {{ table.name }}{{ for c in table.columns }} {{ c.name }}({{ c.former_names | array.join "," }}){{ end }}
            {{ for m in table.database.migrations }}{{ m.order }} {{ m.fact }} {{ m.state }} {{ m.object.name }}
            {{ end }}
            """);
        await repo.RestartAsync();

        var fact = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text")).Facts!);
        var preview = await repo.Service.PreviewAsync("facts", "table", NotesTableId, Ct);

        Assert.DoesNotContain(preview.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var text = Assert.Single(preview.Files).Text;
        Assert.Contains(" text(body) ", text, StringComparison.Ordinal);
        Assert.Contains("1 rename complete body", text, StringComparison.Ordinal);
        Assert.Equal(fact.Path, FactPaths(repo).Single());
        // Nothing was sealed and no physical snapshot is kept: the open fact alone carries the history.
        Assert.DoesNotContain(repo.Repo.ListFiles(), p => p.StartsWith(".maquettiste/snapshots/", StringComparison.Ordinal));
        Assert.DoesNotContain(repo.Repo.ListFiles(), p => p.Contains("/migrations/", StringComparison.Ordinal) && !p.Contains("/migrations/open/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_change_made_and_undone_leaves_stale_facts_as_a_warning()
    {
        await using var repo = E2ERepo.Create(demo: false, migrations: true);
        var against = await BaselineAsync(repo, null);
        var there = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text")).Facts!);
        var back = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "body")).Facts!);

        var report = await ValidateAgainstAsync(repo, against);
        Assert.Equal(0, report.Errors);
        var stale = report.Diagnostics.Where(d => d.Rule == "MQ4103").ToList();
        Assert.Equal(new[] { there.Id, back.Id }.Order(StringComparer.Ordinal), stale.Select(d => d.ElementId!).Order(StringComparer.Ordinal));
        Assert.All(stale, d => Assert.Equal((DiagnosticSeverity.Warning, "/fact"), (d.Severity, d.JsonPointer)));
        Assert.Contains("nets to nothing", stale[0].Message, StringComparison.Ordinal);

        // Plain validate judges complete facts against the committed physical snapshot: none yet, so none is judged.
        Assert.DoesNotContain((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ4103");
        // The first apply renders both facts (a diff from nothing lists every complete fact), so the committed snapshot lists them as
        // rendered: they are in the baseline now, and a fact in the baseline is never stale.
        await repo.ApplyAsync();
        Assert.Contains(repo.Repo.ListFiles(), p => p.StartsWith(".maquettiste/snapshots/", StringComparison.Ordinal));
        Assert.DoesNotContain((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ4103");

        // The same change made and undone again: the committed snapshot lists neither new fact, so plain validate calls both stale.
        there = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text")).Facts!);
        back = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "body")).Facts!);
        var plain = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.Equal(new[] { there.Id, back.Id }.Order(StringComparer.Ordinal),
            plain.Diagnostics.Where(d => d.Rule == "MQ4103").Select(d => d.ElementId!).Order(StringComparer.Ordinal));

        // The chains are judged once per snapshot and committed snapshot: a repeated validate judges nothing again, and a committed
        // snapshot that goes away is seen.
        var runs = repo.Store.CommittedStaleRuns;
        var again = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.Equal(runs, repo.Store.CommittedStaleRuns);
        Assert.Equal(plain.Diagnostics, again.Diagnostics);
        foreach (var file in repo.Repo.ListFiles().Where(p => p.StartsWith(".maquettiste/snapshots/", StringComparison.Ordinal)))
            File.Delete(repo.Repo.PathOf(file));
        Assert.DoesNotContain((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ4103");
        Assert.Equal(runs + 1, repo.Store.CommittedStaleRuns);
    }

    [Fact]
    public async Task A_reverted_pending_change_is_stale_not_pending()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var narrowed = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        Assert.Contains((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ4101" && d.ElementId == narrowed.Id);

        // Back to 120: the fact is left as it was, and it is stale, not pending.
        Assert.Empty((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 120)).Facts!);
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.Equal(0, report.Errors);
        var stale = Assert.Single(report.Diagnostics, d => d.ElementId == narrowed.Id);
        Assert.Equal(("MQ4103", DiagnosticSeverity.Warning, "/fact"), (stale.Rule, stale.Severity, stale.JsonPointer));
        Assert.Contains("shows its from type again", stale.Message, StringComparison.Ordinal);

        // So generation runs.
        await repo.ApplyAsync();
    }

    [Fact]
    public async Task A_held_fact_is_complete_for_validate_until_released()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var against = await BaselineAsync(repo, null);
        var fact = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        Assert.Equal(2, (await ValidateAgainstAsync(repo, against)).Errors);

        // Held: complete for validate, and it explains its change.
        await repo.EditAsync(fact.Id, e => e["held"] = true);
        var held = await ValidateAgainstAsync(repo, against);
        Assert.Equal(0, held.Errors);
        Assert.DoesNotContain(held.Diagnostics, d => d.Rule is "MQ4101" or "MQ4102");

        // Released (by hand: a save that releases it without its intent is refused), it needs its intent again.
        var text = repo.Repo.ReadFile(fact.Path);
        Assert.Contains("\"held\": true", text, StringComparison.Ordinal);
        repo.Repo.WriteFile(fact.Path, text.Replace("\"held\": true", "\"held\": true,\n  \"released\": true", StringComparison.Ordinal));
        await repo.RestartAsync();
        Assert.Equal(["MQ4101 /conversion", "MQ4101 /dataCheck"], (await ValidateAgainstAsync(repo, against)).Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.JsonPointer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_baseline_with_errors_skips_its_objects_with_MQ4109()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: E2ERepo.RecordOff);
        using var copy = new TempRepo();
        E2ERepo.CopyTree(repo.Repo.ModelRoot, copy.ModelRoot);
        // In the baseline the notes table has an error (unicode on a uuid column, MQ4057).
        var notes = Path.Combine(copy.ModelRoot, "model", "databases", "main", "tables", "notes.json");
        var node = JsonNode.Parse(File.ReadAllText(notes))!;
        node["columns"]![0]!["unicode"] = true;
        File.WriteAllBytes(notes, TestServices.Json.Write(node, "table.json", "model/databases/main/tables/notes.json"));

        await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text");
        var report = await ValidateAgainstAsync(repo, copy.RepoRoot);

        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ4102");
        var skipped = Assert.Single(report.Diagnostics, d => d.Rule == "MQ4109");
        Assert.Equal((DiagnosticSeverity.Warning, E2ERepo.MainDatabaseId), (skipped.Severity, skipped.ElementId));
        Assert.Contains("table notes", skipped.Message, StringComparison.Ordinal);
        Assert.Equal(0, report.Errors);

        // Without the error, the same rename is unexplained.
        E2ERepo.CopyTree(repo.Repo.ModelRoot, copy.ModelRoot);
        File.Copy(Path.Combine(Fixtures.Path("models", "billing"), ".maquettiste", "model", "databases", "main", "tables", "notes.json"), notes, overwrite: true);
        var unexplained = Assert.Single((await ValidateAgainstAsync(repo, copy.RepoRoot)).Diagnostics, d => d.Rule == "MQ4102");
        Assert.Equal((NotesTableId, "/columns/3"), (unexplained.ElementId, unexplained.JsonPointer));
    }

    [Fact]
    public async Task A_baseline_file_that_does_not_parse_is_skipped_with_MQ4109()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: E2ERepo.RecordOff);
        using var copy = new TempRepo();
        E2ERepo.CopyTree(repo.Repo.ModelRoot, copy.ModelRoot);
        // In the baseline the notes table's file does not parse: the table is missing from the baseline's model.
        var notes = Path.Combine(copy.ModelRoot, "model", "databases", "main", "tables", "notes.json");
        File.WriteAllText(notes, "{ \"kind\": \"table\", broken");

        await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text");
        var report = await ValidateAgainstAsync(repo, copy.RepoRoot);

        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ4102");
        var skipped = Assert.Single(report.Diagnostics, d => d.Rule == "MQ4109");
        Assert.Equal((DiagnosticSeverity.Warning, E2ERepo.MainDatabaseId), (skipped.Severity, skipped.ElementId));
        Assert.Contains("files that did not load (.maquettiste/model/databases/main/tables/notes.json)", skipped.Message, StringComparison.Ordinal);
        Assert.Equal(0, report.Errors);

        // A database file that does not parse: the database is not compared, and says so.
        File.Copy(Path.Combine(Fixtures.Path("models", "billing"), ".maquettiste", "model", "databases", "main", "tables", "notes.json"), notes, overwrite: true);
        File.WriteAllText(Path.Combine(copy.ModelRoot, "model", "databases", "main", "database.json"), "{ \"kind\": \"database\", broken");
        var unread = Assert.Single((await ValidateAgainstAsync(repo, copy.RepoRoot)).Diagnostics, d => d.Rule == "MQ4109");
        Assert.Equal(E2ERepo.MainDatabaseId, unread.ElementId);
        Assert.Contains("database.json", unread.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_materialize_since_the_baseline_is_explained_by_its_rekey_and_reads_as_a_drop_without_it()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var against = await BaselineAsync(repo, null);
        var rekey = Assert.Single((await ApplyAsync(repo, Materialize(E2ERepo.CustomerId))).Facts!);

        // The rekey step reads the baseline's derived table under the stored table's key: one table, not a drop and an add.
        var report = await ValidateAgainstAsync(repo, against);
        Assert.Equal(0, report.Errors);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule.StartsWith("MQ41", StringComparison.Ordinal));

        // Without the rekey fact, the key moved with nothing to say so: a drop, on the database file, with the rekey hint.
        var document = (await repo.Store.GetElementAsync(rekey.Id, Ct))!;
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.DeleteAsync(rekey.Id, document.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct)).Outcome);
        var unexplained = (await ValidateAgainstAsync(repo, against)).Diagnostics.Where(d => d.Rule == "MQ4102").ToList();
        var drop = Assert.Single(unexplained, d => d.Message.Contains("drop table billing.customers", StringComparison.Ordinal));
        Assert.Equal((E2ERepo.MainDatabaseId, (string?)null), (drop.ElementId, drop.JsonPointer));
        Assert.Contains("record a rekey fact instead", drop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_materialize_made_and_undone_since_the_baseline_leaves_both_rekeys_stale()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var against = await BaselineAsync(repo, null);
        var customer = (await repo.Store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var query = (await repo.Store.GetElementAsync(FindCustomersQueryId, Ct))!;
        var rekey = Assert.Single((await ApplyAsync(repo, Materialize(E2ERepo.CustomerId))).Facts!);
        var back = Assert.Single((await UndoMaterializeAsync(repo, customer, query)).Result.Facts!);
        Assert.Equal(("rekey", "rekey"), (rekey.Fact, back.Fact));

        var report = await ValidateAgainstAsync(repo, against);
        Assert.Equal(0, report.Errors);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ4102");
        var stale = report.Diagnostics.Where(d => d.Rule == "MQ4103").ToList();
        Assert.Equal(new[] { rekey.Id, back.Id }.Order(StringComparer.Ordinal), stale.Select(d => d.ElementId!).Order(StringComparer.Ordinal));
        Assert.All(stale, d => Assert.Equal((DiagnosticSeverity.Warning, "/fact"), (d.Severity, d.JsonPointer)));
        Assert.Contains("every key it moved is back where it was", stale[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entity_rename_reports_its_table_rename_on_the_entity_when_the_overlay_has_no_name()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: E2ERepo.RecordOff);
        var against = await BaselineAsync(repo, null);
        await repo.EditAsync(E2ERepo.InvoiceId, e => e["name"] = "Bill");

        // The invoices table has an overlay that does not name it: its name follows the entity's, so the finding is there.
        var renamed = Assert.Single((await ValidateAgainstAsync(repo, against)).Diagnostics,
            d => d.Rule == "MQ4102" && d.Message.Contains("rename table billing.invoices to bills", StringComparison.Ordinal));
        Assert.Equal((E2ERepo.InvoiceId, "/name"), (renamed.ElementId, renamed.JsonPointer));
        Assert.Equal(".maquettiste/model/entities/bill.json", renamed.FilePath); // the file follows the name
        Assert.NotNull(renamed.Line);
    }

    // ---------------------------------------------------------------- migration list and fold (A13, A14)

    [Fact]
    public async Task Open_facts_list_in_after_order_with_ties_by_id()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var f1 = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "content")).Facts!);
        var f2 = Assert.Single((await RenameCustomerNameAsync(repo, "fullName")).Facts!);
        var f3 = Assert.Single((await RenameNotesColumnAsync(repo, NotesEntityTypeColumnId, "kind")).Facts!);
        var f4 = Assert.Single((await SetNumberTypeAsync(repo, "varchar(20)")).Facts!); // another table: invoices
        Assert.Equal([f1.Id], (await FactAsync(repo, f3.Id)).After);
        Assert.Equal(SaveOutcome.Saved, (await repo.EditAsync(f2.Id, e => e["after"] = new JsonArray(f3.Id))).Outcome);

        // f1 and f4 are ready: f1 has the smaller id. Then f3 (after f1), f2 (after f3), f4.
        var list = await repo.Store.ListMigrationsAsync(null, null, 100, Ct);
        string[] expected = [f1.Id, f3.Id, f2.Id, f4.Id];
        Assert.Equal(expected, list.Facts.Select(f => f.Id));
        Assert.Equal([1, 2, 3, 4], list.Facts.Select(f => f.Order));
        Assert.Equal(4, list.Total);
        Assert.Null(list.Next);
        var first = list.Facts[0];
        Assert.Equal((E2ERepo.MainDatabaseId, "main", "rename", "complete", false), (first.Database, first.DatabaseName, first.Fact, first.State, first.Stale));
        Assert.Equal("rename column billing.notes.body to content", first.Summary);
        Assert.Equal(f1.Path, first.Path);
        Assert.Equal(64, first.Hash.Length);

        // The resolved model's records share the order.
        var resolved = await repo.Service.GetResolvedAsync(new ResolvedQuery("migrations"), Ct);
        Assert.Equal(expected, resolved.Items.Cast<MigrationRecord>().Select(r => r.Id));
        Assert.Equal(list.Facts.Select(f => f.Hash), resolved.Items.Cast<MigrationRecord>().Select(r => r.Hash));
    }

    [Fact]
    public async Task List_migrations_pages_with_a_cursor_in_applied_order()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await RenameNotesColumnAsync(repo, NotesBodyColumnId, "content");
        await RenameNotesColumnAsync(repo, NotesBodyColumnId, "copy");
        await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text_body");
        var narrowed = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        var all = await repo.Store.ListMigrationsAsync(null, null, 1000, Ct);
        Assert.Equal(4, all.Total);

        var pages = new List<MigrationEntry>();
        string? cursor = null;
        var count = 0;
        do
        {
            var page = await repo.Store.ListMigrationsAsync(null, cursor, 3, Ct);
            Assert.Equal(4, page.Total);
            pages.AddRange(page.Facts);
            cursor = page.Next;
            count++;
        }
        while (cursor is not null);
        Assert.Equal(2, count);
        Assert.Equal(all.Facts.Select(f => (f.Id, f.Order, f.Hash)), pages.Select(f => (f.Id, f.Order, f.Hash)));
        var pending = Assert.Single(all.Facts, f => f.Id == narrowed.Id);
        Assert.Equal(("change-type", "pending"), (pending.Fact, pending.State));
        Assert.Equal(["conversion", "dataCheck"], pending.Missing);

        // By database name or id; an unknown database, an unknown cursor or a cursor whose fact is gone is an argument error.
        Assert.Equal(4, (await repo.Store.ListMigrationsAsync("main", null, 10, Ct)).Total);
        Assert.Equal(4, (await repo.Store.ListMigrationsAsync(E2ERepo.MainDatabaseId, null, 10, Ct)).Total);
        Assert.Contains("nowhere", (await Assert.ThrowsAsync<ArgumentException>(() => repo.Store.ListMigrationsAsync("nowhere", null, 10, Ct))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.Store.ListMigrationsAsync(null, "not-a-cursor", 10, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.Store.ListMigrationsAsync(null, null, 0, Ct));
        var second = (await repo.Store.ListMigrationsAsync(null, null, 1, Ct)).Next!;
        var gone = (await repo.Store.GetElementAsync(all.Facts[0].Id, Ct))!;
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.DeleteAsync(gone.Element.Id, gone.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct)).Outcome);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.Store.ListMigrationsAsync(null, second, 10, Ct));
    }

    [Fact]
    public async Task Fold_joins_renames_keeping_intermediate_names()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var against = await BaselineAsync(repo, null);
        var a = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "content")).Facts!);
        var b = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "copy")).Facts!);
        var c = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text_body")).Facts!);

        // A dry run writes nothing and lists the files.
        var before = Hashes(repo);
        var dry = await FoldAsync(repo, against, dryRun: true);
        Assert.Equal(SaveOutcome.Saved, dry.Outcome);
        Assert.Equal(before, Hashes(repo));
        Assert.Equal([(a.Path, "deleted"), (b.Path, "deleted"), (c.Path, "changed")],
            dry.Files.Select(f => (f.Path, f.Action)).OrderBy(f => f.Path, StringComparer.Ordinal));

        var folded = await FoldAsync(repo, against);
        Assert.Equal(SaveOutcome.Saved, folded.Outcome);
        var join = Assert.Single(folded.Joined);
        Assert.Equal((c.Id, "rename"), (join.Kept, join.Fact));
        Assert.Equal([a.Id, b.Id], join.Removed);
        Assert.Empty(folded.RemovedStale);
        Assert.Equal([c.Path], FactPaths(repo));
        var kept = await FactAsync(repo, c.Id);
        Assert.Equal(("body", "text_body"), (kept.From!.Name, kept.To!.Name));
        Assert.Equal(["content", "copy"], kept.Via);
        Assert.Equal("body", kept.Object!.Name);
        Assert.Empty(kept.After);

        // The joined fact still explains the change, and the projection keeps every former name.
        var report = await ValidateAgainstAsync(repo, against);
        Assert.Equal(0, report.Errors);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ4102" or "MQ4103");
        var model = await repo.Store.Services.Resolver.ResolveAsync(await repo.Store.GetSnapshotAsync(Ct), null, Ct);
        var column = model.Databases.Single().Tables.Single(t => t.Key == NotesTableId).Columns.Single(col => col.Key == NotesBodyColumnId);
        Assert.Equal(["body", "content", "copy"], column.FormerNames);
        Assert.Empty((await FoldAsync(repo, against)).Joined); // nothing more to fold
    }

    [Fact]
    public async Task Fold_joins_a_conversion_and_a_widening()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var against = await BaselineAsync(repo, null);
        var narrowed = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        await CompleteAsync(repo, narrowed.Id);
        Assert.Empty((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 100)).Facts!); // a widening records nothing

        // The widening written as a fact of its own (an author's, or another copy's): from 80 to 100, after the narrowing.
        var widening = JsonNode.Parse(repo.Repo.ReadFile(narrowed.Path))!.AsObject();
        foreach (var key in new[] { "$schema", "id", "dataCheck" })
            widening.Remove(key);
        widening["from"] = widening["to"]!.DeepClone();
        widening["to"]!["length"] = 100;
        widening["after"] = new JsonArray(narrowed.Id);
        var created = await repo.Store.CreateAsync(Encoding.UTF8.GetBytes(widening.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(created.Outcome == SaveOutcome.Saved, string.Join("; ", created.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var wideningId = created.Id!;

        var folded = await FoldAsync(repo, against);
        Assert.Equal(SaveOutcome.Saved, folded.Outcome);
        var join = Assert.Single(folded.Joined);
        Assert.Equal((wideningId, "change-type"), (join.Kept, join.Fact));
        Assert.Equal([narrowed.Id], join.Removed);
        var kept = await FactAsync(repo, wideningId);
        Assert.Equal((120, 100), (kept.From!.Length, kept.To!.Length));
        Assert.True(kept.Conversion!.Cast);
        Assert.Equal("Every customer name fits.", kept.DataCheck!.Accept); // the narrowing's data check stays
        Assert.Equal(0, (await ValidateAgainstAsync(repo, against)).Errors);
    }

    [Fact]
    public async Task Fold_refuses_two_conversions_that_carry_casts()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var against = await BaselineAsync(repo, null);
        var first = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 80)).Facts!);
        await CompleteAsync(repo, first.Id);
        var second = Assert.Single((await repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["length"] = 60)).Facts!);
        Assert.NotEqual(first.Id, second.Id); // the first was complete: the column records a new fact
        await CompleteAsync(repo, second.Id);

        var before = Hashes(repo);
        var folded = await FoldAsync(repo, against);
        Assert.Equal(SaveOutcome.Saved, folded.Outcome);
        Assert.Empty(folded.Joined);
        var refusal = Assert.Single(folded.Refused);
        Assert.Equal([first.Id, second.Id], refusal.Facts);
        Assert.Contains("conversion", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(before, Hashes(repo));
    }

    [Fact]
    public async Task Fold_keeps_the_last_id_and_rewires_after()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var against = await BaselineAsync(repo, null);
        var a = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "content")).Facts!);
        var b = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text_body")).Facts!);
        var other = Assert.Single((await RenameCustomerNameAsync(repo, "fullName")).Facts!);
        Assert.Equal(SaveOutcome.Saved, (await repo.EditAsync(other.Id, e => e["after"] = new JsonArray(a.Id))).Outcome);

        var folded = await FoldAsync(repo, against);
        var join = Assert.Single(folded.Joined);
        Assert.Equal(b.Id, join.Kept);
        Assert.Equal([a.Id], join.Removed);
        Assert.Equal(b.Path, (await repo.Store.GetElementAsync(b.Id, Ct))!.Path); // its id and file stay
        Assert.Null(await repo.Store.GetElementAsync(a.Id, Ct));
        Assert.Equal([b.Id], (await FactAsync(repo, other.Id)).After); // a link to a removed fact names the kept one
        Assert.DoesNotContain((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ4111");
    }

    [Fact]
    public async Task Fold_never_creates_an_after_cycle()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var against = await BaselineAsync(repo, null);
        var first = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "content")).Facts!);
        var second = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text_body")).Facts!);
        var between = Assert.Single((await RenameCustomerNameAsync(repo, "fullName")).Facts!);

        // between follows the first rename and the second follows it: joining the renames into the second would make between follow
        // the second, and the second follow between.
        Assert.Equal(SaveOutcome.Saved, (await repo.EditAsync(between.Id, e => e["after"] = new JsonArray(first.Id))).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await repo.EditAsync(second.Id, e => e["after"] = new JsonArray(first.Id, between.Id))).Outcome);

        var before = Hashes(repo);
        var folded = await FoldAsync(repo, against);
        Assert.Equal(SaveOutcome.Saved, folded.Outcome);
        Assert.Empty(folded.Joined);
        var refusal = Assert.Single(folded.Refused);
        Assert.Equal([first.Id, second.Id], refusal.Facts);
        Assert.Equal("the join would order facts in a cycle", refusal.Reason);
        Assert.Equal(before, Hashes(repo));
    }

    [Fact]
    public async Task Fold_against_a_baseline_leaves_its_facts_alone()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var earlier = await BaselineAsync(repo, null);
        var a = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "content")).Facts!);
        var later = await BaselineAsync(repo, null); // holds a
        Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "text_body")).Facts!);

        // Against the earlier baseline the two renames join; against the later one, a is the baseline's and stays.
        Assert.Single((await FoldAsync(repo, earlier, dryRun: true)).Joined);
        var before = Hashes(repo);
        var folded = await FoldAsync(repo, later);
        Assert.Equal(SaveOutcome.Saved, folded.Outcome);
        Assert.Empty(folded.Joined);
        Assert.Empty(folded.RemovedStale);
        Assert.Equal(before, Hashes(repo));
        Assert.NotNull(await repo.Store.GetElementAsync(a.Id, Ct));
    }

    [Fact]
    public async Task Fold_without_a_baseline_leaves_rendered_facts_alone()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var earlier = await BaselineAsync(repo, null);
        var there = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "content")).Facts!);

        // The committed snapshot lists the first rename as rendered into a migration; then the column is renamed back.
        repo.Repo.WriteFile(".maquettiste/snapshots/main.json", $$"""
            {
              "database": "{{E2ERepo.MainDatabaseId}}",
              "name": "main",
              "dialect": "postgresql",
              "revision": 1,
              "facts": [
                "{{there.Id}}"
              ]
            }

            """);
        var back = Assert.Single((await RenameNotesColumnAsync(repo, NotesBodyColumnId, "body")).Facts!);

        // Without a baseline the committed snapshot's facts are the baseline's: the rename back alone is not stale, and stays.
        var before = Hashes(repo);
        var folded = await FoldAsync(repo, null);
        Assert.Equal(SaveOutcome.Saved, folded.Outcome);
        Assert.Empty(folded.RemovedStale);
        Assert.Empty(folded.Joined);
        Assert.Equal(before, Hashes(repo));
        Assert.DoesNotContain((await repo.Store.ListMigrationsAsync(null, null, 10, Ct)).Facts, f => f.Stale);

        // Against a model from before the first rename, the two net to nothing: both are stale and fold removes them.
        var against = await FoldAsync(repo, earlier, dryRun: true);
        Assert.Equal([there.Id, back.Id], against.RemovedStale);
    }

    /// <summary>Renames Customer's name attribute, which renames its column.</summary>
    private static Task<SaveResult> RenameCustomerNameAsync(E2ERepo repo, string name) =>
        repo.EditAsync(E2ERepo.CustomerId, e => e["attributes"]![1]!["name"] = name);

    /// <summary>Completes a pending change-type fact: a cast and a data check that names why none is needed.</summary>
    private static async Task CompleteAsync(E2ERepo repo, string factId)
    {
        var saved = await repo.EditAsync(factId, e =>
        {
            e["conversion"] = new JsonObject { ["cast"] = true };
            e["dataCheck"] = new JsonObject { ["accept"] = "Every customer name fits." };
        });
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    /// <summary>Folds every database's facts, against a baseline (a snapshot id or a folder) or the committed snapshots.</summary>
    private static async Task<FoldResult> FoldAsync(E2ERepo repo, string? against, bool dryRun = false)
    {
        using var baseline = against is null ? null : await ModelBaseline.OpenAsync(repo.Store, against, repo.Repo.RepoRoot, allowFolders: true, library: null, Ct);
        return await repo.Store.FoldMigrationsAsync(new FoldRequest(null, baseline, dryRun, ChangeSource.Editor), Ct);
    }

    /// <summary>Sets the invoice number's native type on its overlay column.</summary>
    private static Task<SaveResult> SetNumberTypeAsync(E2ERepo repo, string nativeType) =>
        repo.EditAsync(InvoiceOverlayId, e => e["columns"]![0]!["nativeType"] = nativeType);

    /// <summary>Sets <c>migrations.record</c>.</summary>
    private static async Task SetRecordAsync(E2ERepo repo, string record)
    {
        var settings = await repo.Store.GetSettingsAsync(Ct);
        var json = JsonNode.Parse(settings.Json.GetRawText())!.AsObject();
        json["migrations"] = new JsonObject { ["record"] = record };
        var saved = await repo.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), settings.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
    }

    /// <summary>Takes the baseline: a model snapshot (its id), or a copy of the model folder in <paramref name="copy"/> (its full path).</summary>
    private static async Task<string> BaselineAsync(E2ERepo repo, TempRepo? copy)
    {
        if (copy is not null)
        {
            E2ERepo.CopyTree(repo.Repo.ModelRoot, copy.ModelRoot);
            return copy.RepoRoot;
        }

        return (await new SnapshotLibrary(repo.Store).CreateAsync(new SnapshotCreateRequest("Base"), Ct)).Id;
    }

    private static async Task<ValidationReport> ValidateAgainstAsync(E2ERepo repo, string against)
    {
        using var baseline = await ModelBaseline.OpenAsync(repo.Store, against, repo.Repo.RepoRoot, allowFolders: true, library: null, Ct);
        return await repo.Store.ValidateAgainstAsync(baseline, Ct);
    }

    private static Dictionary<string, string> Hashes(E2ERepo repo) =>
        repo.Repo.ListFiles().Where(p => p.StartsWith(".maquettiste/model/", StringComparison.Ordinal))
            .ToDictionary(p => p, p => E2ERepo.Sha(File.ReadAllBytes(repo.Repo.PathOf(p))), StringComparer.Ordinal);

    private static List<string> Changed(Dictionary<string, string> before, Dictionary<string, string> after) =>
        [.. after.Where(kv => !before.TryGetValue(kv.Key, out var hash) || hash != kv.Value).Select(kv => kv.Key)
            .Concat(before.Keys.Where(k => !after.ContainsKey(k))).Order(StringComparer.Ordinal)];

    private const string BillingSchemaId = "01J92P0V1RC04SKQ5353EAKHG2";
    private const string FindCustomersQueryId = "01K6QRY0000000000000000003";
    private const string BillingPackageId = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string MoneyId = "01J92P0V0304M76KA84NE4D1TE";
    private const string EmailAddressId = "01J92P0V04TDYE2C73WMNXVDBV";
    private const string LedgerPackageId = "01JFRD00000000000000000001";
    private const string LedgerDatabaseId = "01JFRD00000000000000000002";
    private const string LedgerSchemaId = "01JFRD00000000000000000003";
    private const string LedgerEntryId = "01JFRD00000000000000000004";
    private const string LedgerEntryKeyId = "01JFRD00000000000000000005";
    private const string LedgerEntryMemoId = "01JFRD00000000000000000006";
    private const string JournalTableId = "01JFRD00000000000000000007";
    private const string JournalIdColumnId = "01JFRD00000000000000000008";
    private const string JournalMemoColumnId = "01JFRD00000000000000000009";

    /// <summary>
    /// Adds a second database, <c>ledger</c>, that takes the Ledger package only (with one entity, which the main database also takes,
    /// by its every-entity convention) and has one designed table, <c>journal</c>; then restarts the store, so no capture is cached.
    /// </summary>
    private static async Task AddLedgerAsync(E2ERepo repo)
    {
        await repo.CreateElementAsync($$"""{ "kind": "package", "id": "{{LedgerPackageId}}", "name": "Ledger" }""");
        await repo.CreateElementAsync($$"""
            { "kind": "database", "id": "{{LedgerDatabaseId}}", "name": "ledger", "dialect": "postgresql", "version": "16",
              "defaultSchema": "ledger", "schemas": [ { "id": "{{LedgerSchemaId}}", "name": "ledger" } ],
              "byConvention": "packages", "packages": [ "{{LedgerPackageId}}" ] }
            """);
        await repo.CreateElementAsync($$"""
            { "kind": "entity", "id": "{{LedgerEntryId}}", "name": "LedgerEntry", "package": "{{LedgerPackageId}}",
              "key": { "attributes": [ "{{LedgerEntryKeyId}}" ], "strategy": "uuid-v7" },
              "attributes": [ { "id": "{{LedgerEntryKeyId}}", "name": "id", "type": "uuid", "required": true },
                { "id": "{{LedgerEntryMemoId}}", "name": "memo", "type": "string", "length": 200, "required": true } ] }
            """);
        await repo.CreateElementAsync($$"""
            { "kind": "table", "id": "{{JournalTableId}}", "name": "journal", "database": "{{LedgerDatabaseId}}", "schema": "{{LedgerSchemaId}}",
              "columns": [ { "id": "{{JournalIdColumnId}}", "name": "id", "type": "uuid", "nullable": false },
                { "id": "{{JournalMemoColumnId}}", "name": "memo", "type": "string", "length": 200, "nullable": true } ],
              "primaryKey": { "columns": [ "{{JournalIdColumnId}}" ] } }
            """);
        Assert.Empty(FactPaths(repo));
        await repo.RestartAsync();
    }

    /// <summary>Each database's capture in the store's current model (a whole-model resolve), as JSON text.</summary>
    private static async Task<Dictionary<string, string>> CapturesAsync(E2ERepo repo)
    {
        var model = await repo.Store.Services.Resolver.ResolveAsync(await repo.Store.GetSnapshotAsync(Ct), null, Ct);
        return model.Databases.ToDictionary(d => d.Id,
            d => System.Text.Json.JsonSerializer.Serialize(SnapshotCapture.Capture(d, 0), EngineJson.Options), StringComparer.Ordinal);
    }

    private static JsonObject Materialize(string entityId) => new()
    {
        ["op"] = "materialize-tables", ["database"] = E2ERepo.MainDatabaseId, ["entities"] = new JsonArray(entityId),
    };

    private static ModelBatch Batch(E2ERepo repo, params JsonObject[] operations)
    {
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject { ["operations"] = new JsonArray([.. operations]) }.ToJsonString()));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return parsed.Batch;
    }

    private static async Task<BatchResult> ApplyAsync(E2ERepo repo, params JsonObject[] operations)
    {
        var result = await repo.Store.ApplyBatchAsync(Batch(repo, operations), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved,
            result.Outcome + ": " + string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
        return result;
    }

    private static async Task<Migration> FactAsync(E2ERepo repo, string id) =>
        Assert.IsType<Migration>((await repo.Store.GetElementAsync(id, Ct))!.Element);

    private static async Task<SaveResult> RenameNotesColumnAsync(E2ERepo repo, string columnId, string name) =>
        await repo.EditAsync(NotesTableId, e => e["columns"]!.AsArray().First(c => (string?)c!["id"] == columnId)!["name"] = name);

    private static List<string> FactPaths(E2ERepo repo) =>
        [.. repo.Repo.ListFiles().Where(p => p.Contains("/migrations/open/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
}
