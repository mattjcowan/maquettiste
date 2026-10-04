using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Materialize, both directions (erratum E43), over the billing fixture: <c>materialize-tables</c> turns a projected table into a
/// designed table with the same shape and binds the entity to it (the overlay folds in and keeps its id, the mapping element goes,
/// the relations keep their foreign keys, queries and other tables follow, the committed snapshot is rekeyed so no migration
/// appears); <c>materialize-entities</c> makes an entity per table with relations for the foreign keys; both preview without
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
    public async Task The_committed_snapshot_is_rekeyed_so_the_next_run_writes_no_migration()
    {
        await using var r = EditorRepo.Create();
        var first = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, first.Outcome);
        var migrations = r.Repo.ListFiles().Count(p => p.Contains("/migrations/", StringComparison.Ordinal));

        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, Tables(EditorRepo.InvoiceId, EditorRepo.CustomerId))).Outcome);
        var second = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);

        Assert.Equal(RunOutcome.Succeeded, second.Outcome);
        Assert.Equal(migrations, r.Repo.ListFiles().Count(p => p.Contains("/migrations/", StringComparison.Ordinal)));
        // A table's script says nothing of the entity laid out on it, so storing the tables as files changes no script.
        Assert.Empty(second.Changes.Where(c => c.Kind == FileChangeKind.Modified).Select(c => c.Path));
        foreach (var table in new[] { "customers", "invoices" })
            Assert.DoesNotContain("entity", r.Repo.ReadFile($"db/main/billing/tables/{table}.sql"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Undoing_and_redoing_a_materialize_writes_no_migration_and_rewrites_no_snapshot_key()
    {
        await using var r = EditorRepo.Create();
        Assert.Equal(RunOutcome.Succeeded, (await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct)).Outcome);
        int Migrations() => r.Repo.ListFiles().Count(p => p.Contains("/migrations/", StringComparison.Ordinal));
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

        // The snapshot keeps its keys and records the aliases.
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

        foreach (var round in new[] { "stored", "undone", "redone" })
        {
            if (round == "undone")
                await Step(id => after.GetValueOrDefault(id), id => priors.GetValueOrDefault(id), id => hashes.GetValueOrDefault(id));
            else if (round == "redone")
                await Step(id => priors.GetValueOrDefault(id), id => after.GetValueOrDefault(id), id => hashes.GetValueOrDefault(id));
            var run = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, run.Outcome);
            Assert.True(migrations == Migrations(), $"{round}: a migration was written: " + string.Join(", ", run.Changes.Select(c => c.Path)));
        }

        Assert.Equal(keys, Keys());
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
            }
            else
            {
                Assert.Empty(await Generate());
            }

            await undoStore();
            var undoAgain = await StoreAsync(r, entities);
            Assert.Equal(firstId, r.Store.Current!.All<Table>().Single(t => t.Name == tableName).Id);
            var migrations = await Generate();
            if (addColumn)
            {
                // (PostgreSQL: dropping a column drops every view first and creates it again after, whichever table it reads.)
                var migration = Assert.Single(migrations);
                Assert.Single(Regex.Matches(migration, "DROP COLUMN"));
                Assert.Contains("scratch_" + cycle, migration, StringComparison.Ordinal);
                foreach (var statement in new[] { "DROP TABLE", "CREATE TABLE", "ADD COLUMN", "ADD CONSTRAINT", "DROP CONSTRAINT", "INDEX" })
                    Assert.False(migration.Contains(statement, StringComparison.Ordinal), $"cycle {cycle}, {statement}:\n{migration}");
            }
            else
            {
                Assert.True(migrations.Count == 0, $"cycle {cycle}: " + string.Join("\n", migrations));
            }

            // Back to the projection for the next cycle; generating there writes nothing either.
            await undoAgain();
            Assert.Empty(await Generate());
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

    private static async Task CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }
}
