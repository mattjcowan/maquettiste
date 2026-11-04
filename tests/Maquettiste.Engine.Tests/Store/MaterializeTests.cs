using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Expressions;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Materialize, both directions (erratum E43), over the billing fixture: <c>materialize-tables</c> turns a projected table into a
/// designed table with the same shape and binds the entity to it (the overlay folds in and keeps its id, the mapping element goes,
/// the relations keep their foreign keys, queries and other tables follow, the committed snapshot is read under the new keys so the
/// next migration changes no table: it carries only the write's <c>rekey</c> fact); <c>materialize-entities</c> makes an entity per table with relations for the foreign keys; <c>materialize-attributes</c>
/// adds to a bound entity an attribute per column its binding leaves without one, <c>materialize-columns</c> a column of its table per
/// attribute its binding leaves unmapped; each previews without
/// writing and refuse what is already bound; deletes drop bindings and keep the entities.
/// </summary>
public sealed class MaterializeTests
{
    private const string NotesTableId = "01K6BND0000000000000000001";
    private const string InvoiceNoteId = "01K6BND0000000000000000010";
    private const string CustomerNoteId = "01K6BND0000000000000000020";
    private const string InvoiceMappingId = "01J92P0V200XW93JQYSRCT2SE3";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static async Task<BatchResult> ApplyAsync(EditorRepo r, string operation)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await r.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static string Tables(params string[] entities) =>
        $$"""{"op":"materialize-tables","database":"{{EditorRepo.MainDatabaseId}}","entities":[{{string.Join(",", entities.Select(e => "\"" + e + "\""))}}]}""";

    [Fact]
    public async Task The_status_lists_unbound_entities_and_every_table_with_the_entities_bound_to_it()
    {
        await using var r = EditorRepo.Create(packs: false);

        var status = (await r.Store.GetMaterializeStatusAsync(EditorRepo.MainDatabaseId, Ct))!;

        Assert.Equal(["Customer", "Invoice", "InvoiceLine", "Payment", "Product"], status.Entities.Select(e => e.Name));
        Assert.All(status.Entities, e => Assert.True(e.Projected));
        var notes = Assert.Single(status.Sources, s => s.Id == NotesTableId);
        Assert.Equal(["CustomerNote", "InvoiceNote"], notes.BoundBy.Select(b => b.EntityName));
        Assert.Equal("entity_type", notes.BoundBy[0].Constants[0].Column);
        Assert.Contains(status.Sources, s => s.Kind == "view" && s.BoundBy.Count == 0);
        Assert.Null(await r.Store.GetMaterializeStatusAsync(EditorRepo.InvoiceId, Ct));
    }

    [Fact]
    public async Task A_preview_writes_nothing_and_lists_what_the_operation_would_do()
    {
        await using var r = EditorRepo.Create(packs: false);
        var files = r.Files();

        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-tables", EditorRepo.MainDatabaseId, [EditorRepo.InvoiceId]), Ct);

        Assert.True(plan.Valid, string.Join("; ", plan.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(files, r.Files());
        var table = Assert.Single(plan.Updates, u => u.Kind == "table");
        Assert.Equal((EditorRepo.OverlayTableId, "invoices"), (table.Id, table.Name));
        Assert.Contains(plan.Updates, u => u.Id == EditorRepo.InvoiceId);
        Assert.Equal([InvoiceMappingId], plan.Deletes.Select(d => d.Id));
        var mapping = Assert.Single(plan.Creates);
        Assert.Equal(("mapping", "places in main"), (mapping.Kind, mapping.Name));
        Assert.Contains(plan.Notes, n => n.Contains("still projects", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Materializing_tables_keeps_the_shape_folds_the_overlay_and_binds_every_column()
    {
        await using var r = EditorRepo.Create(packs: false);
        var before = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;

        var result = await ApplyAsync(r, Tables(EditorRepo.InvoiceId, EditorRepo.CustomerId));

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snapshot = r.Store.Current!;
        Assert.Null(snapshot.GetDocument(InvoiceMappingId));
        var invoices = snapshot.Get<Table>(EditorRepo.OverlayTableId)!;
        Assert.Equal((TableOrigin.Designed, "invoices", "Invoice register"), (invoices.Origin, invoices.Name, invoices.DisplayName));
        Assert.Null(invoices.Entity);
        var invoice = snapshot.Get<Entity>(EditorRepo.InvoiceId)!;
        var binding = Assert.Single(invoice.Bindings);
        Assert.Equal((EditorRepo.MainDatabaseId, EditorRepo.OverlayTableId), (binding.Database, binding.Source));
        var places = snapshot.All<Mapping>().Single(m => m.Relation == EditorRepo.PlacesRelationId);
        Assert.Equal(invoices.ForeignKeys.Single().Id, places.ForeignKey);
        var customers = snapshot.All<Table>().Single(t => t.Name == "customers");
        Assert.Equal(customers.Id, invoices.ForeignKeys.Single().ReferencesTable);

        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity != DiagnosticSeverity.Info && d.Rule.StartsWith("MQ40", StringComparison.Ordinal));
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // The physical model is the same, column by column, under new keys; the bound entities are no longer projected.
        var after = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;
        foreach (var name in new[] { "invoices", "customers" })
        {
            var old = before.Tables.Single(t => t.Name == name);
            var now = after.Tables.Single(t => t.Name == name);
            Assert.Equal(("designed", (string?)null), (now.Origin, now.EntityId));
            Assert.Equal(old.Columns.Select(c => (c.Name, c.NativeType, c.Nullable, c.DefaultSql)), now.Columns.Select(c => (c.Name, c.NativeType, c.Nullable, c.DefaultSql)));
            Assert.Equal(old.PrimaryKey!.Name, now.PrimaryKey!.Name);
            Assert.Equal(old.ForeignKeys.Select(f => f.Name), now.ForeignKeys.Select(f => f.Name));
            Assert.Equal(old.Indexes.Select(i => i.Name), now.Indexes.Select(i => i.Name));
            Assert.Single(now.BoundBy);
        }

        Assert.Equal("01J92P0V1ACKN3G6TK3NJDTM82", after.Tables.Single(t => t.Name == "invoices").ForeignKeys.Single().RelationId);
        Assert.All(after.Queries, q => Assert.NotEqual("", q.Sql)); // the queries over the invoices follow the new column ids
    }

    [Fact]
    public async Task Materializing_an_entity_already_bound_is_refused_and_the_next_one_follows_a_foreign_key_to_it()
    {
        await using var r = EditorRepo.Create(packs: false);
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, Tables(EditorRepo.InvoiceId))).Outcome);
        var invoices = r.Store.Current!.Get<Table>(EditorRepo.OverlayTableId)!;
        Assert.Equal(EditorRepo.CustomerId + "@" + EditorRepo.MainDatabaseId, invoices.ForeignKeys.Single().ReferencesTable);

        var again = await ApplyAsync(r, Tables(EditorRepo.InvoiceId));
        Assert.Equal(SaveOutcome.Invalid, again.Outcome);
        var refusal = Assert.Single(again.Items.SelectMany(i => i.Diagnostics));
        Assert.Equal("MQ4055", refusal.Rule);
        Assert.Contains("already bound", refusal.Message, StringComparison.Ordinal);
        var bound = await ApplyAsync(r, Tables(InvoiceNoteId));
        Assert.Contains("already bound", bound.Items.SelectMany(i => i.Diagnostics).Single().Message, StringComparison.Ordinal);

        // Materializing the customer later points the invoices' foreign key at its designed table.
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, Tables(EditorRepo.CustomerId))).Outcome);
        var customers = r.Store.Current!.All<Table>().Single(t => t.Name == "customers");
        var fk = r.Store.Current.Get<Table>(EditorRepo.OverlayTableId)!.ForeignKeys.Single();
        Assert.Equal(customers.Id, fk.ReferencesTable);
        Assert.Equal(customers.PrimaryKey!.Columns, fk.ReferencesColumns);
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task The_committed_snapshot_is_rekeyed_so_the_next_migration_changes_no_table()
    {
        await using var r = EditorRepo.Create();
        var first = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, first.Outcome);
        var migrations = r.Repo.ListFiles().Count(IsMigrationScript);

        var stored = await ApplyAsync(r, Tables(EditorRepo.InvoiceId, EditorRepo.CustomerId));
        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var rekey = Assert.Single(stored.Facts!, f => f.Fact == "rekey").Id;
        var second = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);

        // The rekey fact alone makes the diff non-empty (next-phase-1-design.md section 3.2): its migration changes no table, and the
        // snapshot lists the fact as rendered.
        Assert.Equal(RunOutcome.Succeeded, second.Outcome);
        Assert.Equal(migrations + 1, r.Repo.ListFiles().Count(IsMigrationScript));
        AssertNoTableChange(r, second);
        var snapshotPath = r.Repo.ListFiles().Single(p => p.EndsWith("snapshots/main.json", StringComparison.Ordinal));
        Assert.Equal([rekey], JsonNode.Parse(r.Repo.ReadFile(snapshotPath))!["facts"]!.AsArray().Select(f => (string)f!));
        // A table's script says nothing of the entity laid out on it, so storing the tables as files changes no table script; the
        // schema script records the rekey fact, which a build reaches, in the migration ledger (section 3.3).
        Assert.Equal(["db/main/schema.sql"], second.Changes.Where(c => c.Kind == FileChangeKind.Modified).Select(c => c.Path));
        Assert.Contains($"VALUES ('{rekey}', ", r.Repo.ReadFile("db/main/schema.sql"), StringComparison.Ordinal);
        foreach (var table in new[] { "customers", "invoices" })
            Assert.DoesNotContain("entity", r.Repo.ReadFile($"db/main/billing/tables/{table}.sql"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Undoing_and_redoing_a_materialize_writes_migrations_that_change_no_table()
    {
        await using var r = EditorRepo.Create();
        Assert.Equal(RunOutcome.Succeeded, (await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct)).Outcome);
        int Migrations() => r.Repo.ListFiles().Count(IsMigrationScript);
        var migrations = Migrations();
        var snapshotPath = r.Repo.ListFiles().Single(p => p.EndsWith("snapshots/main.json", StringComparison.Ordinal));
        string Keys() => string.Join(",", JsonNode.Parse(r.Repo.ReadFile(snapshotPath))!["tables"]!.AsArray().Select(t => (string)t!["key"]!));
        var keys = Keys();

        // The editor's way: what the operation will change is read first; undo puts it back, redo applies the result again.
        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-tables", EditorRepo.MainDatabaseId, [EditorRepo.InvoiceId, EditorRepo.CustomerId]), Ct);
        var priors = plan.Updates.Concat(plan.Deletes).ToDictionary(c => c.Id, c => r.Store.Current!.GetDocument(c.Id)!.Json.GetRawText(), StringComparer.Ordinal);
        var stored = await ApplyAsync(r, Tables(EditorRepo.InvoiceId, EditorRepo.CustomerId));
        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var items = stored.Items.Where(i => i.Id is not null).ToList();
        var after = items.ToDictionary(i => i.Id!, i => i.Current?.Json.GetRawText(), StringComparer.Ordinal);
        var hashes = items.ToDictionary(i => i.Id!, i => i.Hash, StringComparer.Ordinal);

        // The materialize leaves the snapshot's keys and records the aliases.
        Assert.Equal(keys, Keys());
        var aliases = JsonNode.Parse(r.Repo.ReadFile(snapshotPath))!["aliases"]!.AsArray();
        Assert.Contains(aliases, a => (string)a!["key"]! == EditorRepo.InvoiceId + "@" + EditorRepo.MainDatabaseId && (string)a["alias"]! == EditorRepo.OverlayTableId);

        async Task Step(Func<string, string?> from, Func<string, string?> to, Func<string, string?> hash)
        {
            var ops = new List<string>();
            foreach (var id in after.Keys.Concat(priors.Keys).Distinct(StringComparer.Ordinal))
            {
                var (was, becomes) = (from(id), to(id));
                if (becomes is null)
                    ops.Add($$"""{"op":"delete","id":"{{id}}","expectedHash":"{{hash(id)}}"}""");
                else if (was is null)
                    ops.Add($$"""{"op":"create","element":{{becomes}}}""");
                else
                    ops.Add($$"""{"op":"update","id":"{{id}}","expectedHash":"{{hash(id)}}","element":{{becomes}}}""");
            }

            var result = await ApplyAsync(r, string.Join(",", ops));
            Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
            foreach (var item in result.Items.Where(i => i.Id is not null))
                hashes[item.Id!] = item.Hash;
        }

        // Each step records one rekey fact (the undo, its move back), so each run writes one migration, which changes no table; the
        // snapshot it saves takes the keys the model gives the tables then.
        foreach (var round in new[] { "stored", "undone", "redone" })
        {
            if (round == "undone")
                await Step(id => after.GetValueOrDefault(id), id => priors.GetValueOrDefault(id), id => hashes.GetValueOrDefault(id));
            else if (round == "redone")
                await Step(id => priors.GetValueOrDefault(id), id => after.GetValueOrDefault(id), id => hashes.GetValueOrDefault(id));
            var run = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, run.Outcome);
            Assert.True(++migrations == Migrations(), $"{round}: " + string.Join(", ", run.Changes.Select(c => c.Path)));
            AssertNoTableChange(r, run);
            Assert.Equal(round == "undone", keys == Keys());
            Assert.Equal(RunOutcome.Succeeded, (await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Check, Packs = ["sql-ddl"] }, null, Ct)).Outcome);
        }
    }

    /// <summary>
    /// Storing a table again after an undo (a new materialize, not a redo) reuses the ids the committed snapshot's alias recorded for
    /// the projected key: the table's (customers has no overlay, so the store draws it) and every column's (invoices keeps its overlay's
    /// table id, but most of its columns have no overlay entry). The snapshot then reads as the same table, so the next migration drops
    /// only the column added and undone in between, and without that column there is no migration at all; twice over.
    /// </summary>
    [Theory]
    [InlineData("customers", true)]
    [InlineData("invoices", true)]
    [InlineData("customers", false)]
    [InlineData("invoices", false)]
    public async Task Storing_a_table_again_after_an_undo_reuses_its_ids_so_the_next_migration_drops_only_what_was_undone(string tableName, bool addColumn)
    {
        await using var r = EditorRepo.Create();
        async Task<IReadOnlyList<string>> Generate()
        {
            var run = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, run.Outcome);
            return [.. run.Changes.Where(c => c.Kind == FileChangeKind.Added && c.Path.Contains("/migrations/", StringComparison.Ordinal))
                .Select(c => r.Repo.ReadFile(c.Path))];
        }

        await Generate();
        string[] entities = [EditorRepo.InvoiceId, EditorRepo.CustomerId];
        string? firstId = null;
        for (var cycle = 1; cycle <= 2; cycle++)
        {
            var undoStore = await StoreAsync(r, entities);
            var table = r.Store.Current!.All<Table>().Single(t => t.Name == tableName);
            firstId ??= table.Id;
            Assert.Equal(firstId, table.Id);
            if (addColumn)
            {
                var document = (await r.Store.GetElementAsync(table.Id, Ct))!;
                var stored = document.Json.GetRawText();
                var edited = JsonNode.Parse(stored)!.AsObject();
                edited["columns"]!.AsArray().Add(new JsonObject { ["id"] = "01K6REST00000000000000000" + cycle, ["name"] = "scratch_" + cycle, ["type"] = "string", ["length"] = 20 });
                var added = await r.Store.SaveAsync(table.Id, Encoding.UTF8.GetBytes(edited.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
                Assert.True(added.Outcome == SaveOutcome.Saved, string.Join("; ", added.Diagnostics.Select(d => d.Rule + " " + d.Message)));
                var adding = Assert.Single(await Generate());
                Assert.Contains("scratch_" + cycle, adding, StringComparison.Ordinal);
                var undone = await r.Store.SaveAsync(table.Id, Encoding.UTF8.GetBytes(stored), added.Hash!, ChangeSource.Editor, Ct);
                Assert.Equal(SaveOutcome.Saved, undone.Outcome);
                // The column's drop is a migration fact pending its acknowledgement, which generation waits for.
                await AcknowledgeAsync(r, Assert.Single(undone.Facts!, f => f.Fact == "drop"));
            }
            else
            {
                AssertNoTableChange(await Generate()); // the store's rekey fact only
            }

            await undoStore();
            var undoAgain = await StoreAsync(r, entities);
            Assert.Equal(firstId, r.Store.Current!.All<Table>().Single(t => t.Name == tableName).Id);
            var migrations = await Generate();
            if (addColumn)
            {
                // (PostgreSQL: dropping a column drops every view first and creates it again after, whichever table it reads.) The
                // migration records its facts, so it opens with the ledger DDL, whose tables are no table of the model.
                var migration = Assert.Single(migrations).Replace(LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema), "", StringComparison.Ordinal);
                Assert.Single(Regex.Matches(migration, "DROP COLUMN"));
                Assert.Contains("scratch_" + cycle, migration, StringComparison.Ordinal);
                foreach (var statement in new[] { "DROP TABLE", "CREATE TABLE", "ADD COLUMN", "ADD CONSTRAINT", "DROP CONSTRAINT", "INDEX" })
                    Assert.False(migration.Contains(statement, StringComparison.Ordinal), $"cycle {cycle}, {statement}:\n{migration}");
            }
            else
            {
                AssertNoTableChange(migrations); // the undo's and the new store's rekey facts only
            }

            // Back to the projection for the next cycle; generating there changes no table either.
            await undoAgain();
            AssertNoTableChange(await Generate());
        }
    }

    /// <summary>Applies materialize-tables the editor's way and returns its undo (what the operation changed, put back).</summary>
    private static async Task<Func<Task>> StoreAsync(EditorRepo r, string[] entities)
    {
        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-tables", EditorRepo.MainDatabaseId, entities), Ct);
        var priors = plan.Updates.Concat(plan.Deletes).ToDictionary(c => c.Id, c => r.Store.Current!.GetDocument(c.Id)!.Json.GetRawText(), StringComparer.Ordinal);
        var stored = await ApplyAsync(r, Tables(entities));
        Assert.True(stored.Outcome == SaveOutcome.Saved, string.Join("; ", stored.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var after = stored.Items.Where(i => i.Id is not null).ToDictionary(i => i.Id!, i => i.Current?.Json.GetRawText(), StringComparer.Ordinal);
        return async () =>
        {
            var ops = new List<string>();
            foreach (var id in after.Keys.Concat(priors.Keys).Distinct(StringComparer.Ordinal))
            {
                var (was, becomes) = (after.GetValueOrDefault(id), priors.GetValueOrDefault(id));
                var hash = r.Store.Current!.GetDocument(id)?.Hash;
                if (becomes is null)
                    ops.Add($$"""{"op":"delete","id":"{{id}}","expectedHash":"{{hash}}"}""");
                else if (was is null)
                    ops.Add($$"""{"op":"create","element":{{becomes}}}""");
                else
                    ops.Add($$"""{"op":"update","id":"{{id}}","expectedHash":"{{hash}}","element":{{becomes}}}""");
            }

            var result = await ApplyAsync(r, string.Join(",", ops));
            Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        };
    }

    [Fact]
    public async Task With_the_hashes_the_caller_read_a_document_changed_since_or_not_read_is_a_conflict()
    {
        await using var r = EditorRepo.Create(packs: false);
        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-tables", EditorRepo.MainDatabaseId, [EditorRepo.InvoiceId]), Ct);
        var read = plan.Updates.Concat(plan.Deletes).ToDictionary(c => c.Id, c => r.Store.Current!.GetDocument(c.Id)!.Hash, StringComparer.Ordinal);
        string Op(IReadOnlyDictionary<string, string> hashes) =>
            Tables(EditorRepo.InvoiceId)[..^1] + ",\"expectedHashes\":{" + string.Join(",", hashes.Select(p => $"\"{p.Key}\":\"{p.Value}\"")) + "}}";

        // Another save changes the invoice entity after it was read: the operation is a conflict and writes nothing.
        var invoice = (await r.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!;
        var edited = JsonNode.Parse(invoice.Json.GetRawText())!.AsObject();
        edited["displayName"] = "Invoice (edited)";
        var save = await r.Store.SaveAsync(EditorRepo.InvoiceId, Encoding.UTF8.GetBytes(edited.ToJsonString()), invoice.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, save.Outcome);
        var files = r.Files();
        var stale = await ApplyAsync(r, Op(read));
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        Assert.Equal(files, r.Files());

        // Not naming a document the operation changes is a conflict too; the hashes read now go through.
        var fresh = plan.Updates.Concat(plan.Deletes).ToDictionary(c => c.Id, c => r.Store.Current!.GetDocument(c.Id)!.Hash, StringComparer.Ordinal);
        Assert.Equal(SaveOutcome.Conflict, (await ApplyAsync(r, Op(fresh.Where(p => p.Key != EditorRepo.InvoiceId).ToDictionary(p => p.Key, p => p.Value)))).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, Op(fresh))).Outcome);
    }

    [Fact]
    public async Task Materializing_entities_makes_one_per_table_with_relations_for_the_foreign_keys()
    {
        await using var r = EditorRepo.Create(packs: false);
        const string Suppliers = "01K6MAT0000000000000000001", SupplierId = "01K6MAT0000000000000000002", SupplierName = "01K6MAT0000000000000000003";
        const string Contacts = "01K6MAT0000000000000000004", ContactId = "01K6MAT0000000000000000005", ContactSupplier = "01K6MAT0000000000000000006";
        const string ContactEmail = "01K6MAT0000000000000000007", ContactKey = "01K6MAT0000000000000000008";
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Suppliers}}","name":"suppliers","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{SupplierId}}","name":"id","type":"int64","nullable":false,"generated":"identity"},
              {"id":"{{SupplierName}}","name":"name","type":"string","length":120,"nullable":false}],
             "primaryKey":{"columns":["{{SupplierId}}"]} }
            """);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Contacts}}","name":"supplier_contacts","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{ContactId}}","name":"id","type":"uuid","nullable":false},
              {"id":"{{ContactSupplier}}","name":"supplier_id","type":"int64","nullable":false},
              {"id":"{{ContactEmail}}","name":"email","type":"string","length":254}],
             "primaryKey":{"columns":["{{ContactId}}"]},
             "foreignKeys":[{"id":"{{ContactKey}}","name":"fk_contacts_supplier","columns":["{{ContactSupplier}}"],"referencesTable":"{{Suppliers}}","onDelete":"cascade"}]}
            """);
        var operation = $$"""{"op":"materialize-entities","database":"{{EditorRepo.MainDatabaseId}}","tables":["{{Suppliers}}","{{Contacts}}"],"package":"{{EditorRepo.BillingPackageId}}"}""";

        var preview = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-entities", EditorRepo.MainDatabaseId, [Suppliers, Contacts], null, EditorRepo.BillingPackageId), Ct);
        var result = await ApplyAsync(r, operation);

        Assert.True(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(["entity", "entity", "relation", "mapping"], preview.Creates.Select(c => c.Kind));
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snapshot = r.Store.Current!;
        var supplier = snapshot.All<Entity>().Single(e => e.Name == "Supplier");
        var contact = snapshot.All<Entity>().Single(e => e.Name == "SupplierContact");
        Assert.Equal(["id", "name"], supplier.Attributes.Select(a => a.Name));
        Assert.Equal(IdentityStrategy.DatabaseIdentity, supplier.Key!.Strategy);
        Assert.Equal(["id", "supplierId", "email"], contact.Attributes.Select(a => a.Name));
        Assert.Equal([true, true, false], contact.Attributes.Select(a => a.Required));
        Assert.Equal(Contacts, contact.Bindings.Single().Source);
        var relation = snapshot.All<Relation>().Single(x => x.Name == "fk_contacts_supplier");
        Assert.Equal(["supplier", "supplierContacts"], relation.Ends.Select(e => e.Role));
        Assert.Equal((MaxCardinality.One, 1, ReferentialIntent.Cascade), (relation.Ends[0].Max, relation.Ends[0].Min, relation.Ends[0].OnDelete));
        Assert.Equal(ContactKey, snapshot.All<Mapping>().Single(m => m.Relation == relation.Id).ForeignKey);

        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Rule is "MQ4011" or "MQ4047");

        // A table an entity is bound to is refused.
        var again = await ApplyAsync(r, operation);
        Assert.Equal("MQ4055", again.Items.SelectMany(i => i.Diagnostics).Single().Rule);
        var notes = await ApplyAsync(r, $$"""{"op":"materialize-entities","database":"{{EditorRepo.MainDatabaseId}}","tables":["{{NotesTableId}}"],"package":"{{EditorRepo.BillingPackageId}}"}""");
        Assert.Contains("already bound by entity", notes.Items.SelectMany(i => i.Diagnostics).Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Materializing_attributes_adds_one_per_column_nothing_names_mapped_in_the_binding()
    {
        await using var r = EditorRepo.Create(packs: false);
        const string Suppliers = "01K6MAA0000000000000000001", SupplierId = "01K6MAA0000000000000000002", SupplierName = "01K6MAA0000000000000000003";
        const string SupplierNote = "01K6MAA0000000000000000004", SupplierCreated = "01K6MAA0000000000000000005", SupplierRating = "01K6MAA0000000000000000006";
        const string Supplier = "01K6MAA0000000000000000010", IdAttribute = "01K6MAA0000000000000000011", Binding = "01K6MAA0000000000000000012";
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Suppliers}}","name":"suppliers","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{SupplierId}}","name":"id","type":"int64","nullable":false,"generated":"identity"},
              {"id":"{{SupplierName}}","name":"name","type":"string","length":120,"nullable":false},
              {"id":"{{SupplierNote}}","name":"note","type":"string","length":400},
              {"id":"{{SupplierCreated}}","name":"created_at","type":"datetimeoffset","nullable":false,"defaultSql":{"*":"CURRENT_TIMESTAMP"} },
              {"id":"{{SupplierRating}}","name":"rating","type":"decimal","precision":3,"scale":1}],
             "primaryKey":{"columns":["{{SupplierId}}"]} }
            """);
        // An entity with its key only, bound to the table: id is mapped, note is ignored, the rest nothing names.
        await CreateAsync(r, $$"""
            {"kind":"entity","id":"{{Supplier}}","name":"Supplier","package":"{{EditorRepo.BillingPackageId}}",
             "key":{"attributes":["{{IdAttribute}}"],"strategy":"database-identity"},
             "attributes":[{"id":"{{IdAttribute}}","name":"id","type":"int64","required":true}],
             "bindings":[{"id":"{{Binding}}","database":"{{EditorRepo.MainDatabaseId}}","source":"{{Suppliers}}",
               "fields":[{"attribute":"{{IdAttribute}}","column":"{{SupplierId}}"}],
               "columns":[{"column":"{{SupplierNote}}","status":"ignored"}]}]}
            """);
        string Attributes(string? columns = null) =>
            "{\"op\":\"materialize-attributes\",\"database\":\"" + EditorRepo.MainDatabaseId + "\",\"entities\":[\"" + Supplier + "\"]"
            + (columns is null ? "" : ",\"columns\":[" + columns + "]") + "}";

        var preview = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-attributes", EditorRepo.MainDatabaseId, [Supplier]), Ct);
        var result = await ApplyAsync(r, Attributes());

        Assert.True(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal([Supplier], preview.Updates.Select(u => u.Id));
        Assert.Empty(preview.Creates);
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var supplier = r.Store.Current!.Get<Entity>(Supplier)!;
        Assert.Equal(["id", "name", "createdAt", "rating"], supplier.Attributes.Select(a => a.Name));
        Assert.Equal(["int64", "string", "datetimeoffset", "decimal"], supplier.Attributes.Select(a => a.Type.Builtin));
        Assert.Equal([true, true, true, false], supplier.Attributes.Select(a => a.Required));
        Assert.Equal(120, supplier.Attributes[1].Length);
        Assert.Equal((3, 1), (supplier.Attributes[3].Precision, supplier.Attributes[3].Scale));
        Assert.Equal([IdAttribute], supplier.Key!.Attributes);
        var binding = supplier.Bindings.Single();
        Assert.Equal([SupplierId, SupplierName, SupplierCreated, SupplierRating], binding.Fields.Select(f => f.Column));
        Assert.Equal([SupplierNote], binding.Columns.Select(c => c.Column));

        // A column named by its physical name, ignored before: its listing goes with it.
        var note = await ApplyAsync(r, Attributes("\"note\""));
        Assert.Equal(SaveOutcome.Saved, note.Outcome);
        supplier = r.Store.Current!.Get<Entity>(Supplier)!;
        Assert.Equal("note", supplier.Attributes[^1].Name);
        Assert.Empty(supplier.Bindings.Single().Columns);
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.ElementId == Supplier && (d.Severity == DiagnosticSeverity.Error || d.Rule == "MQ4047"));

        // Refusals: nothing left, a column already mapped, a constant, an entity with no binding, columns for several entities.
        async Task<string> RefusedAsync(string operation)
        {
            var refused = await ApplyAsync(r, operation);
            Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
            var diagnostic = refused.Items.SelectMany(i => i.Diagnostics).Single();
            Assert.Equal("MQ4055", diagnostic.Rule);
            return diagnostic.Message;
        }

        Assert.Contains("nothing to add", await RefusedAsync(Attributes()), StringComparison.Ordinal);
        Assert.Contains("already mapped", await RefusedAsync(Attributes($"\"{SupplierId}\"")), StringComparison.Ordinal);
        Assert.Contains("a constant", await RefusedAsync(
            $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{InvoiceNoteId}}"],"columns":["entity_type"]}"""), StringComparison.Ordinal);
        Assert.Contains("has no binding", await RefusedAsync(
            $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{EditorRepo.InvoiceId}}"]}"""), StringComparison.Ordinal);
        Assert.Contains("one entity at a time", await RefusedAsync(
            $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Supplier}}","{{InvoiceNoteId}}"],"columns":["note"]}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Materializing_columns_adds_one_per_unmapped_attribute_shaped_as_the_projection_and_mapped()
    {
        await using var r = EditorRepo.Create(packs: false);
        const string Vendors = "01K6MAC0000000000000000001", VendorId = "01K6MAC0000000000000000002", VendorName = "01K6MAC0000000000000000003";
        const string Vendor = "01K6MAC0000000000000000010", IdAttribute = "01K6MAC0000000000000000011", NameAttribute = "01K6MAC0000000000000000012";
        const string CreatedAttribute = "01K6MAC0000000000000000013", StatusAttribute = "01K6MAC0000000000000000014", InvoiceStatus = "01J92P0V05WQDH2GZG539D9VG1";
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Vendors}}","name":"vendors","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{VendorId}}","name":"id","type":"uuid","nullable":false},
              {"id":"{{VendorName}}","name":"name","type":"string","length":80,"nullable":false}],
             "primaryKey":{"columns":["{{VendorId}}"]} }
            """);
        // Bound with its key mapped; name has a column of that name already, createdAt and status have none.
        await CreateAsync(r, $$"""
            {"kind":"entity","id":"{{Vendor}}","name":"Vendor","package":"{{EditorRepo.BillingPackageId}}",
             "key":{"attributes":["{{IdAttribute}}"],"strategy":"uuid-v7"},
             "attributes":[{"id":"{{IdAttribute}}","name":"id","type":"uuid","required":true},
               {"id":"{{NameAttribute}}","name":"name","type":"string","length":80,"required":true},
               {"id":"{{CreatedAttribute}}","name":"createdAt","type":"datetimeoffset","required":true},
               {"id":"{{StatusAttribute}}","name":"status","type":{"ref":"{{InvoiceStatus}}"} }],
             "bindings":[{"id":"01K6MAC0000000000000000015","database":"{{EditorRepo.MainDatabaseId}}","source":"{{Vendors}}",
               "fields":[{"attribute":"{{IdAttribute}}","column":"{{VendorId}}"}]}]}
            """);
        var operation = $$"""{"op":"materialize-columns","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Vendor}}"]}""";

        var preview = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-columns", EditorRepo.MainDatabaseId, [Vendor]), Ct);
        var result = await ApplyAsync(r, operation);

        Assert.True(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal([Vendors, Vendor], preview.Updates.Select(u => u.Id));
        Assert.Contains(preview.Notes, n => n.Contains("already has a column named 'name'", StringComparison.Ordinal));
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snapshot = r.Store.Current!;
        var table = snapshot.Get<Table>(Vendors)!;
        Assert.Equal(["id", "name", "created_at", "status"], table.Columns.Select(c => c.Name));
        Assert.False(table.Columns[2].Nullable);
        // The shape is the projection's: the names follow the conventions (snake case), and the enum is stored as the project
        // stores enums by default (its member values).
        Assert.Equal("datetimeoffset", table.Columns[2].Type);
        Assert.Equal("int32", table.Columns[3].Type);
        var binding = snapshot.Get<Entity>(Vendor)!.Bindings.Single();
        Assert.Equal([IdAttribute, CreatedAttribute, StatusAttribute], binding.Fields.Select(f => f.Attribute));
        Assert.Equal([VendorId, table.Columns[2].Id, table.Columns[3].Id], binding.Fields.Select(f => f.Column));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => (d.ElementId == Vendor || d.ElementId == Vendors) && d.Severity == DiagnosticSeverity.Error);

        // Refusals: nothing left (name is left to map by hand), an attribute already mapped, a table the column name has, no binding.
        async Task<string> RefusedAsync(string json)
        {
            var refused = await ApplyAsync(r, json);
            Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
            return refused.Items.SelectMany(i => i.Diagnostics).Single(d => d.Rule == "MQ4055").Message;
        }

        Assert.Contains("nothing to add", await RefusedAsync(operation), StringComparison.Ordinal);
        Assert.Contains("already mapped", await RefusedAsync(
            $$"""{"op":"materialize-columns","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Vendor}}"],"attributes":["{{CreatedAttribute}}"]}"""), StringComparison.Ordinal);
        Assert.Contains("map the attribute to it instead", await RefusedAsync(
            $$"""{"op":"materialize-columns","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Vendor}}"],"attributes":["{{NameAttribute}}"]}"""), StringComparison.Ordinal);
        Assert.Contains("has no binding", await RefusedAsync(
            $$"""{"op":"materialize-columns","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{EditorRepo.InvoiceId}}"]}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_bound_table_lists_the_entities_and_drops_their_bindings_without_deleting_them()
    {
        await using var r = EditorRepo.Create(packs: false);
        var table = (await r.Store.GetElementAsync(NotesTableId, Ct))!;

        var refused = await r.Store.DeleteAsync(NotesTableId, table.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        var cleared = await r.Store.GetDeletePlanAsync([NotesTableId], DeleteResolution.RemoveReferences, Ct);
        var dependents = await r.Store.GetDeletePlanAsync([NotesTableId], DeleteResolution.DeleteDependents, Ct);
        var result = await r.Store.DeleteAsync(NotesTableId, table.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Equal([InvoiceNoteId, CustomerNoteId], refused.Referrers.Select(x => x.FromElementId).Distinct().Order(StringComparer.Ordinal));
        foreach (var plan in new[] { cleared, dependents })
        {
            Assert.Equal(SaveOutcome.Saved, plan.Outcome);
            Assert.Empty(plan.Deletes);
            Assert.Equal([InvoiceNoteId, CustomerNoteId], plan.Removes.Select(x => x.Id));
            Assert.All(plan.Removes, x => Assert.Equal(("binding", "the binding needs table notes; the entity stays"), (x.SubKind, x.Because)));
        }

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Empty(r.Store.Current!.Get<Entity>(InvoiceNoteId)!.Bindings);
        Assert.Empty(r.Store.Current.Get<Entity>(CustomerNoteId)!.Bindings);
    }

    // ---- stereotypes that contribute structure (next-phase-2-design.md section 3.1, D16) ------------------------------------------

    private const string AuditedPath = ".maquettiste/model/vocabularies/stereotypes/audited.json";
    private const string OverlayPath = ".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json";
    private const string ProductId = "01J92P0V0JR8BE8253SKT29ZG7";
    private const string CreatedAt = "01JC7AVD1TC0000000000000C1", UpdatedAt = "01JC7AVD1TC0000000000000C2", CreatedIndex = "01JC7AVD1TC0000000000000X1";

    /// <summary>Rewrites a model file on disk before the store reads it.</summary>
    private static void EditOnDisk(EditorRepo r, string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(r.Repo.PathOf(path)))!.AsObject();
        change(node);
        File.WriteAllText(r.Repo.PathOf(path), node.ToJsonString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// The design's billing example: audited gains created_at and updated_at and an index on created_at; with <paramref name="schemaDefault"/>,
    /// schema billing gives audited to every table, and notes keeps its own created_at by excluding the member and the index.
    /// </summary>
    private static void AuditedMembers(EditorRepo r, bool schemaDefault)
    {
        EditOnDisk(r, AuditedPath, s =>
        {
            s["columns"] = JsonNode.Parse($$"""
                [{"id": "{{CreatedAt}}", "name": "created_at", "type": "datetimeoffset", "nullable": false, "defaultSql": {"*": "CURRENT_TIMESTAMP"} },
                 {"id": "{{UpdatedAt}}", "name": "updated_at", "type": "datetimeoffset"}]
                """);
            s["indexes"] = JsonNode.Parse($$"""[{"id": "{{CreatedIndex}}", "columns": [{"column": "{{CreatedAt}}"}]}]""");
        });
        if (!schemaDefault)
            return;
        EditOnDisk(r, ".maquettiste/model/databases/main/database.json", d => d["schemas"]![0]!["defaultStereotypes"] = new JsonArray("audited"));
        EditOnDisk(r, ".maquettiste/model/databases/main/tables/notes.json", t => t["excludes"] = new JsonArray(CreatedAt, CreatedIndex));
    }

    private static string Errors(ValidationReport report) =>
        string.Join("; ", report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message));

    [Fact]
    public async Task Materialize_tables_keeps_inherited_members_and_moves_their_keys()
    {
        // products inherits audited's columns and index from schema billing's default; its designed table leaves them inherited, keyed by
        // the new table id, and the rekey moves their keys with the table, so the next migration changes no table.
        await using var r = EditorRepo.Create();
        AuditedMembers(r, schemaDefault: true);
        var first = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, first.Outcome);
        var before = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "products");
        var projectedKey = ProductId + "@" + EditorRepo.MainDatabaseId;
        Assert.Equal(projectedKey + "." + CreatedAt, before.Columns.Single(c => c.Name == "created_at").Key);

        var stored = await ApplyAsync(r, Tables(ProductId));

        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var snapshot = r.Store.Current!;
        var file = snapshot.All<Table>().Single(t => t.Name == "products");
        // The file holds the own members only: no audit column, no index on created_at, and no stereotype (the default still reaches it).
        Assert.DoesNotContain(file.Columns, c => c.Name is "created_at" or "updated_at");
        Assert.DoesNotContain(file.Indexes, i => i.Columns.Any(c => c.Column?.Contains(CreatedAt, StringComparison.Ordinal) == true));
        Assert.Empty(file.Stereotypes);
        Assert.Empty(file.Excludes);
        var binding = snapshot.Get<Entity>(ProductId)!.Bindings.Single();
        Assert.Contains(binding.Columns, c => c.Column == file.Id + "." + UpdatedAt);
        Assert.Equal(0, (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);

        var after = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "products");
        Assert.Equal(before.Columns.Select(c => (c.Name, c.NativeType, c.Nullable, c.DefaultSql)), after.Columns.Select(c => (c.Name, c.NativeType, c.Nullable, c.DefaultSql)));
        Assert.Equal(before.Indexes.Select(i => i.Name), after.Indexes.Select(i => i.Name));
        var created = after.Columns.Single(c => c.Name == "created_at");
        Assert.Equal((file.Id + "." + CreatedAt, "schema-default"), (created.Key, created.MemberOrigin!.Source));
        var index = after.Indexes.Single(i => i.MemberOrigin is not null);
        Assert.Equal((CreatedIndex, created.Key), (index.MemberOrigin!.MemberId, Assert.Single(index.Columns).Column));

        // The write's rekey moves the inherited columns' keys with the table key.
        var rekey = snapshot.Get<Migration>(Assert.Single(stored.Facts!, f => f.Fact == "rekey").Id)!;
        Assert.Contains(rekey.Keys, k => k.Kind == "column" && k.From == projectedKey + "." + CreatedAt && k.To == file.Id + "." + CreatedAt);
        Assert.Contains(rekey.Keys, k => k.Kind == "column" && k.From == projectedKey + "." + UpdatedAt && k.To == file.Id + "." + UpdatedAt);
        var second = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, second.Outcome);
        AssertNoTableChange(r, second);
    }

    [Fact]
    public async Task Materialize_tables_carries_the_overlays_redefines_and_excludes()
    {
        // Product's overlay lists versioned (a column and a unique), drops the tracked default, makes created_at nullable, redefines the
        // unique's columns and excludes updated_at: all of it moves into the designed table, the unique's column list by the new ids.
        await using var r = EditorRepo.Create(packs: false);
        const string Overlay = "01JC7AVD1TC0000000000000T2", Version = "01JC7AVD1TC0000000000000V1", VersionUnique = "01JC7AVD1TC0000000000000Q3";
        const string ProductKeyAttribute = "01J92P0V15PMP0E4P5DSNZVETR";
        var projectedKey = ProductId + "@" + EditorRepo.MainDatabaseId;
        AuditedMembers(r, schemaDefault: true);
        File.WriteAllText(r.Repo.PathOf(".maquettiste/model/vocabularies/stereotypes/tracked.json"),
            """{"kind": "stereotype", "id": "01JC7AVD1TC0000000000000S2", "key": "tracked", "name": "Tracked", "appliesTo": ["table"]}""", new UTF8Encoding(false));
        File.WriteAllText(r.Repo.PathOf(".maquettiste/model/vocabularies/stereotypes/versioned.json"), $$"""
            {"kind": "stereotype", "id": "01JC7AVD1TC0000000000000S3", "key": "versioned", "name": "Versioned", "appliesTo": ["table"],
             "columns": [{"id": "{{Version}}", "name": "row_version", "type": "int64", "nullable": false, "default": 0}],
             "uniques": [{"id": "{{VersionUnique}}", "columns": ["{{Version}}"]}]}
            """, new UTF8Encoding(false));
        EditOnDisk(r, ".maquettiste/model/databases/main/database.json", d => d["schemas"]![0]!["defaultStereotypes"] = new JsonArray("audited", "tracked"));
        File.WriteAllText(r.Repo.PathOf(".maquettiste/model/databases/main/tables/products-overlay.json"), $$"""
            {"kind": "table", "id": "{{Overlay}}", "database": "{{EditorRepo.MainDatabaseId}}", "origin": "synthesized", "entity": "{{ProductId}}",
             "stereotypes": ["versioned"], "excludeStereotypes": ["tracked"],
             "redefines": [{"member": "{{CreatedAt}}", "nullable": true},
               {"member": "{{VersionUnique}}", "columns": ["{{projectedKey}}.{{Version}}", "{{ProductKeyAttribute}}"]}],
             "excludes": ["{{UpdatedAt}}"]}
            """, new UTF8Encoding(false));
        Assert.Equal("", Errors(await r.Store.ValidateAsync(ValidationScope.All, Ct)));

        var stored = await ApplyAsync(r, Tables(ProductId));

        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var file = r.Store.Current!.Get<Table>(Overlay)!;
        Assert.Equal((TableOrigin.Designed, "products"), (file.Origin, file.Name));
        Assert.Equal(["versioned"], file.Stereotypes);
        Assert.Equal(["tracked"], file.ExcludeStereotypes!);
        Assert.Equal([UpdatedAt], file.Excludes);
        Assert.Equal([CreatedAt, VersionUnique], file.Redefines.Select(x => x.Member));
        Assert.True(file.Redefines[0].Nullable);
        var idColumn = file.Columns.Single(c => c.Name == "id").Id;
        Assert.Equal([Overlay + "." + Version, idColumn], file.Redefines[1].Columns!);
        Assert.DoesNotContain(file.Columns, c => c.Name is "created_at" or "row_version");
        Assert.DoesNotContain(file.Uniques, u => u.Columns.Any(c => c.Contains(Version, StringComparison.Ordinal)));
        Assert.Equal("", Errors(await r.Store.ValidateAsync(ValidationScope.All, Ct)));

        var table = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "products");
        Assert.Equal(["audited", "versioned"], table.Stereotypes);
        var created = table.Columns.Single(c => c.Name == "created_at");
        Assert.Equal((Overlay + "." + CreatedAt, true), (created.Key, created.Nullable));
        Assert.Equal(["nullable"], created.MemberOrigin!.Redefined);
        Assert.DoesNotContain(table.Columns, c => c.Name == "updated_at");
        var unique = table.Uniques.Single(u => u.MemberOrigin is not null);
        Assert.Equal([Overlay + "." + Version, idColumn], unique.Columns);
        Assert.Equal(["columns"], unique.MemberOrigin!.Redefined);
    }

    [Fact]
    public async Task Materialize_tables_excludes_the_members_a_stereotype_realizes_through_attributes()
    {
        // invoices (whose overlay lists audited) and customers (which schema billing's default reaches) store audited's attributes as
        // columns, so they inherit no member; their designed tables hold those columns as their own and exclude every member of audited.
        await using var r = EditorRepo.Create();
        AuditedMembers(r, schemaDefault: true);
        var first = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, first.Outcome);

        var stored = await ApplyAsync(r, Tables(EditorRepo.InvoiceId, EditorRepo.CustomerId));

        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var snapshot = r.Store.Current!;
        foreach (var name in new[] { "invoices", "customers" })
        {
            var file = snapshot.All<Table>().Single(t => t.Name == name);
            Assert.Equal([CreatedAt, UpdatedAt, CreatedIndex], file.Excludes);
            Assert.Contains(file.Columns, c => c.Name == "created_at");
            Assert.Contains(file.Columns, c => c.Name == "updated_at");
        }

        Assert.Equal(["audited"], snapshot.Get<Table>(EditorRepo.OverlayTableId)!.Stereotypes);
        Assert.Equal("", Errors(await r.Store.ValidateAsync(ValidationScope.All, Ct)));
        var view = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;
        Assert.All(view.Tables.Where(t => t.Name is "invoices" or "customers").SelectMany(t => t.Columns), c => Assert.Null(c.MemberOrigin));
        var second = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, second.Outcome);
        AssertNoTableChange(r, second);
    }

    [Fact]
    public async Task Materialize_tables_keeps_the_package_whose_default_reaches_the_projected_table()
    {
        // Package Catalog gives its tables stamped, which only tables take; products took it through Product's package, which a designed
        // table does not fall back to, so the designed table states the package and keeps inheriting the member.
        await using var r = EditorRepo.Create(packs: false);
        const string Catalog = "01J92P0V025DRFTXKMS240G2NG", StampedAt = "01JC7AVD1TC0000000000000C3";
        File.WriteAllText(r.Repo.PathOf(".maquettiste/model/vocabularies/stereotypes/stamped.json"), $$"""
            {"kind": "stereotype", "id": "01JC7AVD1TC0000000000000S5", "key": "stamped", "name": "Stamped", "appliesTo": ["table"],
             "columns": [{"id": "{{StampedAt}}", "name": "stamped_at", "type": "datetimeoffset"}]}
            """, new UTF8Encoding(false));
        EditOnDisk(r, ".maquettiste/model/packages/catalog.json", p => p["defaultStereotypes"] = new JsonArray("stamped"));

        var stored = await ApplyAsync(r, Tables(ProductId));

        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var file = r.Store.Current!.All<Table>().Single(t => t.Name == "products");
        Assert.Equal(Catalog, file.Package);
        Assert.DoesNotContain(file.Columns, c => c.Name == "stamped_at");
        Assert.Equal("", Errors(await r.Store.ValidateAsync(ValidationScope.All, Ct)));
        var table = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "products");
        var stamped = table.Columns.Single(c => c.Name == "stamped_at");
        Assert.Equal((file.Id + "." + StampedAt, "package-default"), (stamped.Key, stamped.MemberOrigin!.Source));
    }

    [Fact]
    public async Task Materialize_tables_moves_an_overlay_tree_check_on_a_contributed_column_to_the_new_key()
    {
        // The overlay's check reads stamp, which stamped contributes, by its keyed id under the projected table's key; the designed
        // table's check reads it under the table's id, as its columns, keys and redefinitions do, so the write is not refused.
        await using var r = EditorRepo.Create(packs: false);
        const string Stamp = "01K9EXR0000000000000000M02", CheckId = "01K9EXR0000000000000000M03";
        File.WriteAllText(r.Repo.PathOf(".maquettiste/model/vocabularies/stereotypes/stamped.json"), $$"""
            {"kind": "stereotype", "id": "01K9EXR0000000000000000M01", "key": "stamped", "name": "Stamped", "appliesTo": ["table"],
             "columns": [{"id": "{{Stamp}}", "name": "stamp", "type": "datetimeoffset"}]}
            """, new UTF8Encoding(false));
        EditOnDisk(r, OverlayPath, t =>
        {
            t["stereotypes"] = new JsonArray("audited", "stamped");
            t["checks"]!.AsArray().Add(JsonNode.Parse($$"""
                {"id": "{{CheckId}}", "name": "ck_invoices_stamp",
                 "condition": {"op": "isNotNull", "args": [{"path": ["{{EditorRepo.InvoiceId}}@{{EditorRepo.MainDatabaseId}}.{{Stamp}}"] }] } }
                """));
        });

        var stored = await ApplyAsync(r, Tables(EditorRepo.InvoiceId));

        Assert.True(stored.Outcome == SaveOutcome.Saved, string.Join("; ", stored.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var check = Assert.Single(r.Store.Current!.Get<Table>(EditorRepo.OverlayTableId)!.Checks, c => c.Id == CheckId);
        Assert.Equal(EditorRepo.OverlayTableId + "." + Stamp, check.Condition!.Args![0].Path![0]);
        Assert.Equal("", Errors(await r.Store.ValidateAsync(ValidationScope.All, Ct)));
        var resolved = await new ModelResolver(r.Repo.Options).ResolveAsync(r.Store.Current!, null, Ct);
        var invoices = resolved.Databases.Single(d => d.Id == EditorRepo.MainDatabaseId).Tables.Single(t => t.Name == "invoices");
        Assert.Contains(invoices.Checks, c => c.Name == "ck_invoices_stamp" && c.Expression == "stamp IS NOT NULL");
    }

    [Fact]
    public async Task Materialize_tables_keeps_an_overlay_tree_check_the_DDL_of_its_dialect_leaves_out()
    {
        // On SQL Server a tree check is MQ4056 and out of the DDL, so the projected table has no such check; the designed table still
        // holds it as the overlay defined it.
        await using var r = EditorRepo.Create(packs: false);
        const string Flag = "01K9EXR0000000000000000M11", CheckId = "01K9EXR0000000000000000M12";
        EditOnDisk(r, ".maquettiste/model/databases/main/database.json", d => d["dialect"] = "sqlserver");
        EditOnDisk(r, OverlayPath, t =>
        {
            t["columns"]!.AsArray().Add(JsonNode.Parse($$"""{"id": "{{Flag}}", "name": "extra_flag", "type": "bool"}"""));
            t["checks"]!.AsArray().Add(JsonNode.Parse($$"""
                {"id": "{{CheckId}}", "name": "ck_invoices_extra", "condition": {"op": "eq", "args": [{"path": ["{{Flag}}"]}, {"value": true}] } }
                """));
        });
        Assert.Contains((await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ4056" && d.ElementId == EditorRepo.OverlayTableId);

        var stored = await ApplyAsync(r, Tables(EditorRepo.InvoiceId));

        Assert.Equal(SaveOutcome.Saved, stored.Outcome);
        var file = r.Store.Current!.Get<Table>(EditorRepo.OverlayTableId)!;
        Assert.Equal(["ck_invoice_status", "ck_invoices_extra"], file.Checks.Select(c => c.Name));
        var check = file.Checks.Single(c => c.Id == CheckId);
        Assert.Equal((Flag, true), (check.Condition!.Args![0].Path![0], check.Expression.Count == 0));
    }

    [Fact]
    public async Task Materialize_tables_turns_an_overlay_check_on_attributes_into_one_on_the_new_columns()
    {
        // The overlay's checks read Invoice's attributes by their column keys, and number through its override entry: the designed
        // table's checks read the columns the projection gave them, by their new ids (number's is the entry's id), and print by their names.
        await using var r = EditorRepo.Create(packs: false);
        ExpressionResolutionTests.OverlayChecks(r, ExpressionResolutionTests.DueAfterIssue, $$"""
            { "id": "01K9EXR0000000000000000W51", "name": "ck_invoices_number",
              "condition": { "op": "and", "args": [
                { "op": "ge", "args": [{ "path": ["{{ExpressionResolutionTests.TotalAmount}}"] }, { "value": 0 }] },
                { "op": "ge", "args": [{ "call": "length", "args": [{ "path": ["{{ExpressionResolutionTests.NumberEntryId}}"] }] }, { "value": 3 }] }] } }
            """);

        var stored = await ApplyAsync(r, Tables(EditorRepo.InvoiceId));

        Assert.True(stored.Outcome == SaveOutcome.Saved, string.Join("; ", stored.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var file = r.Store.Current!.Get<Table>(EditorRepo.OverlayTableId)!;
        string Id(string name) => file.Columns.Single(c => c.Name == name).Id;
        Assert.Equal(ExpressionResolutionTests.NumberEntryId, Id("number"));
        var due = file.Checks.Single(c => c.Name == "ck_invoices_due_after_issue").Condition!;
        Assert.Equal([Id("issued_on"), Id("due_on")], due.Args!.Select(a => a.Path![0]));
        var number = file.Checks.Single(c => c.Name == "ck_invoices_number").Condition!;
        Assert.Equal([Id("total_amount"), Id("number")], number.Args!.Select(a => a.Args![0].Path?[0] ?? a.Args[0].Args![0].Path![0]));
        Assert.Equal("", Errors(await r.Store.ValidateAsync(ValidationScope.All, Ct)));

        var resolved = await new ModelResolver(r.Repo.Options).ResolveAsync(r.Store.Current!, null, Ct);
        var invoices = resolved.Databases.Single(d => d.Id == EditorRepo.MainDatabaseId).Tables.Single(t => t.Name == "invoices");
        Assert.Equal(
            [("ck_invoices_due_after_issue", "issued_on <= due_on", "issued_on <= due_on"),
             ("ck_invoices_number", "total_amount >= 0 AND char_length(number) >= 3", "total_amount >= 0 and length(number) >= 3")],
            invoices.Checks.Where(c => c.Condition is not null).Select(c => (c.Name, c.Expression, c.Condition!.Text)));
    }

    [Fact]
    public async Task Materialize_tables_refuses_while_an_overlay_check_reads_an_attribute_its_table_does_not_store()
    {
        // The mapping ignores notes, which an overlay check reads (MQ4008 there): the designed table would have no column for it, so
        // materialize names the attribute and the finding, and changes nothing, rather than drop the check or write one that cannot type.
        await using var r = EditorRepo.Create(packs: false);
        EditOnDisk(r, ".maquettiste/model/mappings/invoice-in-main.json", m => m["attributes"]!.AsArray().Add(JsonNode.Parse("""
            { "attribute": "01J92P0V0WKRGKH7YBKA2V30NC", "ignore": true }
            """)));
        ExpressionResolutionTests.OverlayChecks(r, ExpressionResolutionTests.DueAfterIssue, """
            { "id": "01K9EXR0000000000000000W14", "name": "ck_invoices_notes",
              "condition": { "op": "isNotNull", "args": [{ "path": ["01J92P0V0WKRGKH7YBKA2V30NC"] }] } }
            """);
        var before = File.ReadAllText(r.Repo.PathOf(ExpressionResolutionTests.OverlayPath));

        var refused = await ApplyAsync(r, Tables(EditorRepo.InvoiceId));

        Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
        var diagnostic = Assert.Single(refused.Items.SelectMany(i => i.Diagnostics));
        Assert.Equal(("MQ4055", "Check 'ck_invoices_notes' of the overlay of entity 'Invoice' reads 'notes', which table 'invoices' stores in no column "
            + "of its own (MQ4008 at /checks/2/condition of the overlay), so the designed table would have no column for it. Store 'notes' in the table, or "
            + "change or remove the check, then materialize again."), (diagnostic.Rule, diagnostic.Message));
        Assert.Equal(before, File.ReadAllText(r.Repo.PathOf(ExpressionResolutionTests.OverlayPath)));
    }

    [Fact]
    public async Task Materialize_columns_maps_an_attribute_to_an_inherited_column_of_its_name()
    {
        // vendors carries audited and inherits created_at and updated_at; Vendor carries audited too, so its projection has those columns.
        // materialize-columns adds a column for status only and maps createdAt and updatedAt to the inherited columns' keyed ids.
        await using var r = EditorRepo.Create(packs: false);
        AuditedMembers(r, schemaDefault: false);
        const string Vendors = "01K6MAC0000000000000000001", VendorId = "01K6MAC0000000000000000002";
        const string Vendor = "01K6MAC0000000000000000010", IdAttribute = "01K6MAC0000000000000000011", StatusAttribute = "01K6MAC0000000000000000014";
        const string CreatedAttribute = "01J92P0V26XYZRDQ8FJ6KZXT6S", UpdatedAttribute = "01J92P0V27P8NW8KPEDG0RBZ4W";
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Vendors}}","name":"vendors","database":"{{EditorRepo.MainDatabaseId}}","stereotypes":["audited"],"columns":[
              {"id":"{{VendorId}}","name":"id","type":"uuid","nullable":false}],
             "primaryKey":{"columns":["{{VendorId}}"]} }
            """);
        await CreateAsync(r, $$"""
            {"kind":"entity","id":"{{Vendor}}","name":"Vendor","package":"{{EditorRepo.BillingPackageId}}","stereotypes":["audited"],
             "key":{"attributes":["{{IdAttribute}}"],"strategy":"uuid-v7"},
             "attributes":[{"id":"{{IdAttribute}}","name":"id","type":"uuid","required":true},
               {"id":"{{StatusAttribute}}","name":"status","type":"string","length":20}],
             "bindings":[{"id":"01K6MAC0000000000000000015","database":"{{EditorRepo.MainDatabaseId}}","source":"{{Vendors}}",
               "fields":[{"attribute":"{{IdAttribute}}","column":"{{VendorId}}"}]}]}
            """);

        // Named one at a time, an attribute whose column the table inherits maps to it, and the table file is not touched.
        var preview = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-columns", EditorRepo.MainDatabaseId, [Vendor], Attributes: [CreatedAttribute]), Ct);
        var result = await ApplyAsync(r, $$"""{"op":"materialize-columns","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Vendor}}"]}""");

        Assert.True(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal([Vendor], preview.Updates.Select(u => u.Id));
        Assert.Contains(preview.Notes, n => n.Contains("inherits column 'created_at'", StringComparison.Ordinal));
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snapshot = r.Store.Current!;
        var table = snapshot.Get<Table>(Vendors)!;
        Assert.Equal(["id", "status"], table.Columns.Select(c => c.Name));
        var fields = snapshot.Get<Entity>(Vendor)!.Bindings.Single().Fields;
        Assert.Equal(
            [(IdAttribute, VendorId), (StatusAttribute, table.Columns[1].Id), (CreatedAttribute, Vendors + "." + CreatedAt), (UpdatedAttribute, Vendors + "." + UpdatedAt)],
            fields.Select(f => (f.Attribute, f.Column)));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => (d.ElementId == Vendor || d.ElementId == Vendors) && d.Severity == DiagnosticSeverity.Error);
    }

    // ---- columns typed by enums, reference types and scalar types (next-phase-2-design.md section 3.2, D25) ----------------------------

    private const string InvoiceStatusId = "01J92P0V05WQDH2GZG539D9VG1", EmailAddressId = "01J92P0V04TDYE2C73WMNXVDBV";
    private const string InvoiceStateId = "01K9X3A0000000000000000001", InvoiceStateAttribute = "01K9X3D0000000000000000001";
    private const string InvoiceStateJson = """
        {"kind":"reference-type","id":"01K9X3A0000000000000000001","name":"InvoiceState","package":"01J92P0V01KDRN8GX5PGYCNKSX",
         "code":{"id":"01K9X3A0000000000000000002","length":1},"label":{"id":"01K9X3A0000000000000000003","length":40}}
        """;

    /// <summary>
    /// EmailAddress gains a PostgreSQL native type of its own (<c>citext</c>), and Invoice an attribute <c>state</c> typed by the reference
    /// type InvoiceState (a string code of length 1), on disk before the store reads them.
    /// </summary>
    private static void TypedBilling(EditorRepo r)
    {
        EditOnDisk(r, ".maquettiste/model/types/email-address.json", t => t["nativeTypes"] = new JsonObject { ["postgresql"] = "citext" });
        r.Repo.WriteFile(".maquettiste/model/reference-types/invoice-state.json", JsonNode.Parse(InvoiceStateJson)!.ToJsonString());
        EditOnDisk(r, ".maquettiste/model/entities/invoice.json", e => e["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = InvoiceStateAttribute, ["name"] = "state", ["type"] = new JsonObject { ["ref"] = InvoiceStateId },
        }));
    }

    [Fact]
    public async Task Materialize_tables_writes_typeRef_for_enum_reference_and_scalar_columns()
    {
        // Decision 12: a column that stores one attribute typed by an enum, a reference type or a scalar type keeps that type as its
        // typeRef beside its physical type, so the stored table keeps its storage objects; the native type the scalar type gives is left
        // out, since typeRef brings it back. The embedded Money columns store builtin members and get none.
        await using var r = EditorRepo.Create(packs: false);
        TypedBilling(r);
        var before = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;

        var result = await ApplyAsync(r, Tables(EditorRepo.InvoiceId, EditorRepo.CustomerId));

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snapshot = r.Store.Current!;
        var invoices = snapshot.Get<Table>(EditorRepo.OverlayTableId)!;
        var status = invoices.Columns.Single(c => c.Name == "status");
        Assert.Equal(("string", 1, "\"D\"", InvoiceStatusId), (status.Type, status.Length, status.Default?.GetRawText(), status.TypeRef?.Ref));
        var state = invoices.Columns.Single(c => c.Name == "state");
        Assert.Equal(("string", 1, InvoiceStateId), (state.Type, state.Length, state.TypeRef?.Ref));
        Assert.All(invoices.Columns.Where(c => c.Name.StartsWith("total_", StringComparison.Ordinal)), c => Assert.Null(c.TypeRef));
        Assert.Null(invoices.Columns.Single(c => c.Name == "number").TypeRef);
        var email = snapshot.All<Table>().Single(t => t.Name == "customers").Columns.Single(c => c.Name == "email");
        Assert.Equal(("string", 254, EmailAddressId, (string?)null), (email.Type, email.Length, email.TypeRef?.Ref, email.NativeType));
        Assert.Equal(0, (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);

        // The resolved columns are what they were: the scalar's native type and the reference column's type come back through typeRef.
        var after = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;
        foreach (var (table, column) in new[] { ("customers", "email"), ("invoices", "status"), ("invoices", "state") })
        {
            var old = before.Tables.Single(t => t.Name == table).Columns.Single(c => c.Name == column);
            var now = after.Tables.Single(t => t.Name == table).Columns.Single(c => c.Name == column);
            Assert.Equal((old.NativeType, old.Length, old.Nullable), (now.NativeType, now.Length, now.Nullable));
        }

        Assert.Equal("citext", after.Tables.Single(t => t.Name == "customers").Columns.Single(c => c.Name == "email").NativeType);
        Assert.Equal(InvoiceStateId, after.Tables.Single(t => t.Name == "invoices").Columns.Single(c => c.Name == "state").TypeRef?.ReferenceType);
    }

    [Fact]
    public async Task Materialize_tables_keeps_an_overlay_entry_sensitivity_and_writes_no_attribute_one()
    {
        // Decision 12: the overlay entry's own sensitive is the column's own statement and moves to the stored column; Customer's email
        // says pii on the attribute, which keeps saying it, and the bound column shows it without stating it.
        await using var r = EditorRepo.Create(packs: false);
        EditOnDisk(r, ".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json", t => t["columns"]![0]!["sensitive"] = "secret");

        var result = await ApplyAsync(r, Tables(EditorRepo.InvoiceId, EditorRepo.CustomerId));

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snapshot = r.Store.Current!;
        Assert.Equal(Sensitivity.Secret, snapshot.Get<Table>(EditorRepo.OverlayTableId)!.Columns.Single(c => c.Name == "number").Sensitive);
        Assert.Null(snapshot.All<Table>().Single(t => t.Name == "customers").Columns.Single(c => c.Name == "email").Sensitive);
        Assert.Equal(Sensitivity.Pii, snapshot.Get<Entity>(EditorRepo.CustomerId)!.Attributes.Single(a => a.Name == "email").Sensitive);
        Assert.Equal(0, (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);
        var view = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;
        Assert.Equal("secret", view.Tables.Single(t => t.Name == "invoices").Columns.Single(c => c.Name == "number").Sensitive);
        Assert.Equal("pii", view.Tables.Single(t => t.Name == "customers").Columns.Single(c => c.Name == "email").Sensitive);
    }

    [Fact]
    public async Task Materialize_columns_writes_typeRef()
    {
        // Decision 12: materialize-columns shapes a new column as the projection does, and keeps the logical type of the attribute it
        // stores: the enum (its member values, int32), the scalar type (its base and length, without the native type it gives) and the
        // reference type (its code's type and length).
        await using var r = EditorRepo.Create(packs: false);
        TypedBilling(r);
        const string Vendors = "01K6MAC0000000000000000001", VendorId = "01K6MAC0000000000000000002";
        const string Vendor = "01K6MAC0000000000000000010", IdAttribute = "01K6MAC0000000000000000011";
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Vendors}}","name":"vendors","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{VendorId}}","name":"id","type":"uuid","nullable":false}],
             "primaryKey":{"columns":["{{VendorId}}"]} }
            """);
        await CreateAsync(r, $$"""
            {"kind":"entity","id":"{{Vendor}}","name":"Vendor","package":"{{EditorRepo.BillingPackageId}}",
             "key":{"attributes":["{{IdAttribute}}"],"strategy":"uuid-v7"},
             "attributes":[{"id":"{{IdAttribute}}","name":"id","type":"uuid","required":true},
               {"id":"01K6MAC0000000000000000012","name":"status","type":{"ref":"{{InvoiceStatusId}}"} },
               {"id":"01K6MAC0000000000000000013","name":"email","type":{"ref":"{{EmailAddressId}}"} },
               {"id":"01K6MAC0000000000000000014","name":"state","type":{"ref":"{{InvoiceStateId}}"},"required":true} ],
             "bindings":[{"id":"01K6MAC0000000000000000015","database":"{{EditorRepo.MainDatabaseId}}","source":"{{Vendors}}",
               "fields":[{"attribute":"{{IdAttribute}}","column":"{{VendorId}}"}]}]}
            """);

        var result = await ApplyAsync(r, $$"""{"op":"materialize-columns","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Vendor}}"]}""");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var table = r.Store.Current!.Get<Table>(Vendors)!;
        Assert.Equal(
            [
                ("id", "uuid", null, null, null),
                ("status", "int32", null, null, InvoiceStatusId),
                ("email", "string", 254, null, EmailAddressId),
                ("state", "string", 1, null, InvoiceStateId),
            ],
            table.Columns.Select(c => (c.Name, c.Type, c.Length, c.NativeType, c.TypeRef?.Ref)));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => (d.ElementId == Vendor || d.ElementId == Vendors) && d.Severity == DiagnosticSeverity.Error);
        var view = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;
        Assert.Equal("citext", view.Tables.Single(t => t.Name == "vendors").Columns.Single(c => c.Name == "email").NativeType);
    }

    // ---- attributes typed by their column's type (next-phase-2-design.md section 3.3, decision 11, D32) --------------------------------

    private const string EventsTable = "01K9X3C0000000000000000001", EventsId = "01K9X3C0000000000000000002", EventsState = "01K9X3C0000000000000000003";
    private const string EventsStatus = "01K9X3C0000000000000000004", EventsEmail = "01K9X3C0000000000000000005", EventsNote = "01K9X3C0000000000000000006";
    private const string StatesTable = "01K9X3E0000000000000000001", StatesCode = "01K9X3E0000000000000000002", StatesLabel = "01K9X3E0000000000000000003";

    /// <summary>
    /// TypedBilling, with the designed table <c>invoice_events</c> whose columns hold InvoiceState (state, not null), InvoiceStatus
    /// (status) and EmailAddress (email) by their typeRef, beside a plain note. <paramref name="bound"/> also binds InvoiceState to the
    /// designed table <c>event_states</c> (code, label) and gives <c>invoice_events</c> the key <c>fk_event_state</c> from state to its
    /// code.
    /// </summary>
    private static async Task TypedEventsAsync(EditorRepo r, bool bound)
    {
        TypedBilling(r);
        if (bound)
        {
            await CreateAsync(r, $$"""
                {"kind":"table","id":"{{StatesTable}}","name":"event_states","database":"{{EditorRepo.MainDatabaseId}}","columns":[
                  {"id":"{{StatesCode}}","name":"code","type":"string","length":1,"nullable":false},
                  {"id":"{{StatesLabel}}","name":"label","type":"string","length":40,"nullable":false}],
                 "primaryKey":{"columns":["{{StatesCode}}"]} }
                """);
            var state = (await r.Store.GetElementAsync(InvoiceStateId, Ct))!;
            var node = JsonNode.Parse(state.Json.GetRawText())!.AsObject();
            node["bindings"] = JsonNode.Parse($$"""
                [{"id":"01K9X3E0000000000000000004","database":"{{EditorRepo.MainDatabaseId}}","table":"{{StatesTable}}",
                  "fields":[{"attribute":"code","column":"{{StatesCode}}"},{"attribute":"label","column":"{{StatesLabel}}"}]}]
                """);
            var saved = await r.Store.SaveAsync(InvoiceStateId, Encoding.UTF8.GetBytes(node.ToJsonString()), state.Hash, ChangeSource.Editor, Ct);
            Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        var foreignKeys = bound
            ? $$""","foreignKeys":[{"id":"01K9X3C0000000000000000007","name":"fk_event_state","columns":["{{EventsState}}"],"referencesTable":"{{StatesTable}}","referencesColumns":["{{StatesCode}}"]}]"""
            : "";
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{EventsTable}}","name":"invoice_events","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{EventsId}}","name":"id","type":"uuid","nullable":false},
              {"id":"{{EventsState}}","name":"state","type":"string","typeRef":{"ref":"{{InvoiceStateId}}"},"length":1,"nullable":false},
              {"id":"{{EventsStatus}}","name":"status","type":"string","typeRef":{"ref":"{{InvoiceStatusId}}"},"length":1},
              {"id":"{{EventsEmail}}","name":"email","type":"string","typeRef":{"ref":"{{EmailAddressId}}"},"length":254},
              {"id":"{{EventsNote}}","name":"note","type":"string","length":400}],
             "primaryKey":{"columns":["{{EventsId}}"]}{{foreignKeys}} }
            """);
    }

    private static string TypeOf(ModelAttribute attribute) => attribute.Type.Ref ?? attribute.Type.Builtin!;

    [Fact]
    public async Task Materialize_entities_types_an_attribute_by_its_column_typeRef()
    {
        // Decision 11: a column that holds an enum, a reference type or a scalar type becomes one attribute typed by it, { "ref": id },
        // with no length, precision or scale (the type states them), required when the column is not nullable; a plain column keeps its
        // keyword and facets.
        await using var r = EditorRepo.Create(packs: false);
        await TypedEventsAsync(r, bound: false);

        var result = await ApplyAsync(r, $$"""{"op":"materialize-entities","database":"{{EditorRepo.MainDatabaseId}}","tables":["{{EventsTable}}"],"package":"{{EditorRepo.BillingPackageId}}"}""");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var entity = r.Store.Current!.All<Entity>().Single(e => e.Name == "InvoiceEvent");
        Assert.Equal(
            [
                ("id", "uuid", null, true, true),
                ("state", InvoiceStateId, null, false, true),
                ("status", InvoiceStatusId, null, false, false),
                ("email", EmailAddressId, null, false, false),
                ("note", "string", 400, true, false),
            ],
            entity.Attributes.Select(a => (a.Name, TypeOf(a), a.Length, a.Type.Builtin is not null, a.Required)));
        Assert.All(entity.Attributes, a => Assert.Equal((null, null), (a.Precision, a.Scale)));
        Assert.Equal([EventsId, EventsState, EventsStatus, EventsEmail, EventsNote], entity.Bindings.Single().Fields.Select(f => f.Column));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => (d.ElementId == entity.Id || d.ElementId == EventsTable) && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Materialize_entities_makes_no_relation_for_a_key_into_the_types_bound_table()
    {
        // Decision 11: a foreign key that pairs a column typed by a reference type with the code column of the table the type is bound
        // to makes no relation and no mapping, even when the referenced table gets an entity too; a note says the attribute is typed.
        await using var r = EditorRepo.Create(packs: false);
        await TypedEventsAsync(r, bound: true);
        var request = new MaterializeRequest("materialize-entities", EditorRepo.MainDatabaseId, [EventsTable, StatesTable], null, EditorRepo.BillingPackageId);

        var preview = await r.Store.PlanMaterializeAsync(request, Ct);

        Assert.True(preview.Valid, string.Join("; ", preview.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(["entity", "entity"], preview.Creates.Select(c => c.Kind));
        var note = Assert.Single(preview.Notes, n => n.Contains("fk_event_state", StringComparison.Ordinal));
        Assert.Equal(
            "Foreign key 'fk_event_state' of table 'invoice_events' points at the rows of reference type 'InvoiceState' in table 'event_states': attribute 'state' of entity 'InvoiceEvent' is typed by 'InvoiceState' instead, so no relation is made for it.",
            note);

        var result = await ApplyAsync(r, $$"""{"op":"materialize-entities","database":"{{EditorRepo.MainDatabaseId}}","tables":["{{EventsTable}}","{{StatesTable}}"],"package":"{{EditorRepo.BillingPackageId}}"}""");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snapshot = r.Store.Current!;
        var events = snapshot.All<Entity>().Single(e => e.Name == "InvoiceEvent");
        var state = Assert.Single(events.Attributes, a => a.Name == "state");
        Assert.Equal((InvoiceStateId, true, (int?)null), (state.Type.Ref, state.Required, state.Length));
        Assert.DoesNotContain(snapshot.All<Relation>(), x => x.Name == "fk_event_state");
        Assert.DoesNotContain(snapshot.All<Mapping>(), m => m.ForeignKey == "01K9X3C0000000000000000007");
    }

    [Fact]
    public async Task Materialize_attributes_types_by_typeRef()
    {
        // Decision 11, for materialize-attributes: a typed column of a table file gives an attribute typed by its typeRef, and a projected
        // reference column (a source no table file holds: another entity's projected table) one typed by the column's reference type.
        await using var r = EditorRepo.Create(packs: false);
        await TypedEventsAsync(r, bound: false);
        const string Event = "01K9X3G0000000000000000001", EventKey = "01K9X3G0000000000000000002";
        const string Digest = "01K9X3G0000000000000000011", DigestKey = "01K9X3G0000000000000000012";
        await CreateAsync(r, $$"""
            {"kind":"entity","id":"{{Event}}","name":"Event","package":"{{EditorRepo.BillingPackageId}}","key":{"attributes":["{{EventKey}}"]},
             "attributes":[{"id":"{{EventKey}}","name":"id","type":"uuid","required":true}],
             "bindings":[{"id":"01K9X3G0000000000000000003","database":"{{EditorRepo.MainDatabaseId}}","source":"{{EventsTable}}",
               "fields":[{"attribute":"{{EventKey}}","column":"{{EventsId}}"}]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"entity","id":"{{Digest}}","name":"InvoiceDigest","package":"{{EditorRepo.BillingPackageId}}","key":{"attributes":["{{DigestKey}}"]},
             "attributes":[{"id":"{{DigestKey}}","name":"id","type":"uuid","required":true}],
             "bindings":[{"id":"01K9X3G0000000000000000013","database":"{{EditorRepo.MainDatabaseId}}","source":"{{EditorRepo.InvoiceId}}@{{EditorRepo.MainDatabaseId}}",
               "write":"none","fields":[{"attribute":"{{DigestKey}}","column":"id"}]}]}
            """);

        var typed = await ApplyAsync(r, $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Event}}"]}""");
        var projected = await ApplyAsync(r, $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Digest}}"],"columns":["state","number"]}""");

        Assert.Equal(SaveOutcome.Saved, typed.Outcome);
        Assert.Equal(SaveOutcome.Saved, projected.Outcome);
        var snapshot = r.Store.Current!;
        Assert.Equal(
            [("id", "uuid", null, true), ("state", InvoiceStateId, null, true), ("status", InvoiceStatusId, null, false), ("email", EmailAddressId, null, false), ("note", "string", 400, false)],
            snapshot.Get<Entity>(Event)!.Attributes.Select(a => (a.Name, TypeOf(a), a.Length, a.Required)));
        var digest = snapshot.Get<Entity>(Digest)!;
        Assert.Equal([("id", "uuid"), ("state", InvoiceStateId), ("number", "string")], digest.Attributes.Select(a => (a.Name, TypeOf(a))));
        Assert.Null(digest.Attributes[1].Length);
        // A projected column's key is the attribute it stores: Invoice's state and number.
        Assert.Equal(["id", InvoiceStateAttribute, "01J92P0V0R3VSP7D5238DTNZX1"], digest.Bindings.Single().Fields.Select(f => f.Column));
    }

    [Fact]
    public async Task Every_materialize_operation_records_provenance()
    {
        // next-phase-2-design.md section 3.3, decision 4: an operation that creates a binding records every column of a table-file
        // source; one that adds fields records the columns whose fields it writes, and every column while the binding has none.
        await using var r = EditorRepo.Create(packs: false);
        Dictionary<string, string> Shapes(string table)
        {
            var model = r.Store.Current!;
            return BindingDrift.ColumnsOf(model, model.Get<Table>(table)!).ToDictionary(c => c.Id, c => c.Shape, StringComparer.Ordinal);
        }

        EntityBinding BindingOf(string entity) => r.Store.Current!.Get<Entity>(entity)!.Bindings.Single();
        static void Saved(BatchResult result) =>
            Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

        // materialize-tables: every column of the table it writes, hashed from the entries it writes.
        Saved(await ApplyAsync(r, Tables(EditorRepo.InvoiceId)));
        Assert.Equal(Shapes(EditorRepo.OverlayTableId), BindingOf(EditorRepo.InvoiceId).Provenance!.Columns);

        // materialize-entities: a table file's binding records every column; a view's records none.
        const string Vendors = "01K6MAP0000000000000000001", VendorId = "01K6MAP0000000000000000002", View = "01J92P0V1YZ4YP352KD1A50FXS";
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Vendors}}","name":"vendors","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{VendorId}}","name":"id","type":"uuid","nullable":false},
              {"id":"01K6MAP0000000000000000003","name":"name","type":"string","length":80,"nullable":false}],
             "primaryKey":{"columns":["{{VendorId}}"]} }
            """);
        Saved(await ApplyAsync(r, $$"""{"op":"materialize-entities","database":"{{EditorRepo.MainDatabaseId}}","tables":["{{Vendors}}","{{View}}"],"package":"{{EditorRepo.BillingPackageId}}"}"""));
        var vendor = r.Store.Current!.All<Entity>().Single(e => e.Name == "Vendor");
        Assert.Equal(Shapes(Vendors), vendor.Bindings.Single().Provenance!.Columns);
        Assert.Null(r.Store.Current!.All<Entity>().Single(e => e.Name == "OutstandingInvoice").Bindings.Single().Provenance);

        // materialize-columns: the new column's entry hashed from the entry it writes, beside the entries the binding has.
        var attribute = JsonNode.Parse("""{"id":"01K6MAP0000000000000000011","name":"rating","type":"decimal","precision":3,"scale":1}""")!;
        var document = r.Store.Current!.GetDocument(vendor.Id)!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["attributes"]!.AsArray().Add(attribute);
        Assert.Equal(SaveOutcome.Saved, (await r.Store.SaveAsync(vendor.Id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct)).Outcome);
        Saved(await ApplyAsync(r, $$"""{"op":"materialize-columns","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{vendor.Id}}"]}"""));
        Assert.Equal(Shapes(Vendors), BindingOf(vendor.Id).Provenance!.Columns);
        Assert.Equal(3, BindingOf(vendor.Id).Provenance!.Columns.Count);

        // materialize-attributes: only the entries of the columns it maps change; a renamed column it does not map keeps its entry.
        var before = BindingOf(vendor.Id).Provenance!.Columns;
        var table = r.Store.Current!.GetDocument(Vendors)!;
        var tableNode = JsonNode.Parse(table.Json.GetRawText())!.AsObject();
        tableNode["columns"]!.AsArray().Add(JsonNode.Parse("""{"id":"01K6MAP0000000000000000004","name":"region","type":"string","length":8}"""));
        tableNode["columns"]![1]!["name"] = "vendor_name";
        Assert.Equal(SaveOutcome.Saved, (await r.Store.SaveAsync(Vendors, Encoding.UTF8.GetBytes(tableNode.ToJsonString()), table.Hash, ChangeSource.Editor, Ct)).Outcome);
        Saved(await ApplyAsync(r, $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{vendor.Id}}"],"columns":["region"]}"""));
        var after = BindingOf(vendor.Id).Provenance!.Columns;
        Assert.Equal(Shapes(Vendors)["01K6MAP0000000000000000004"], after["01K6MAP0000000000000000004"]);
        Assert.Equal(before["01K6MAP0000000000000000003"], after["01K6MAP0000000000000000003"]);

        // A binding with no provenance records every column of its table.
        Assert.Null(BindingOf(InvoiceNoteId).Provenance);
        var notes = r.Store.Current!.GetDocument(NotesTableId)!;
        var notesNode = JsonNode.Parse(notes.Json.GetRawText())!.AsObject();
        notesNode["columns"]!.AsArray().Add(JsonNode.Parse("""{"id":"01K6MAP0000000000000000005","name":"subject","type":"string","length":80}"""));
        Assert.Equal(SaveOutcome.Saved, (await r.Store.SaveAsync(NotesTableId, Encoding.UTF8.GetBytes(notesNode.ToJsonString()), notes.Hash, ChangeSource.Editor, Ct)).Outcome);
        Saved(await ApplyAsync(r, $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{InvoiceNoteId}}"]}"""));
        Assert.Equal(Shapes(NotesTableId), BindingOf(InvoiceNoteId).Provenance!.Columns);
    }

    private static async Task CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    /// <summary>Whether a repo path is a generated migration script (the model's migration facts live under <c>.maquettiste/</c>).</summary>
    private static bool IsMigrationScript(string path) =>
        path.Contains("/migrations/", StringComparison.Ordinal) && !path.StartsWith(".maquettiste/", StringComparison.Ordinal);

    /// <summary>The run wrote one migration (for its rekey facts) and it names no table of the billing schema.</summary>
    private static void AssertNoTableChange(EditorRepo r, GenerationResult run) =>
        AssertNoTableChange([.. run.Changes.Where(c => c.Kind == FileChangeKind.Added && IsMigrationScript(c.Path)).Select(c => r.Repo.ReadFile(c.Path))]);

    /// <summary>One migration, which names no table of the billing schema: a rekey fact writes no DDL.</summary>
    private static void AssertNoTableChange(IReadOnlyList<string> migrations)
    {
        var migration = Assert.Single(migrations);
        Assert.False(migration.Contains("billing.", StringComparison.Ordinal), migration);
    }

    /// <summary>Completes a pending drop fact with its acknowledgement.</summary>
    private static async Task AcknowledgeAsync(EditorRepo r, RecordedFact fact)
    {
        var document = (await r.Store.GetElementAsync(fact.Id, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["acknowledge"] = "Scratch data only.";
        var saved = await r.Store.SaveAsync(fact.Id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }
}
