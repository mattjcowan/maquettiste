using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Materialize, both directions (erratum E43), over the billing fixture, whose entities are all bound: <c>materialize-tables</c> designs
/// a table for an entity bound to no database and binds the entity to it (a relation to a bound entity gets a foreign key to its table
/// and a relation mapping naming it); <c>materialize-entities</c> makes an entity per table with relations for the foreign keys;
/// <c>materialize-attributes</c> adds to a bound entity an attribute per column its binding leaves without one, <c>materialize-columns</c>
/// a column of its table per attribute its binding leaves unmapped; each previews without writing and refuses what is already bound;
/// deletes drop bindings and keep the entities.
/// </summary>
public sealed class MaterializeTests
{
    private const string NotesTableId = "01K6BND0000000000000000001";
    private const string InvoiceNoteId = "01K6BND0000000000000000010";
    private const string CustomerNoteId = "01K6BND0000000000000000020";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static async Task<BatchResult> ApplyAsync(EditorRepo r, string operation)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await r.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static string Tables(params string[] entities) =>
        $$"""{"op":"materialize-tables","database":"{{EditorRepo.MainDatabaseId}}","entities":[{{string.Join(",", entities.Select(e => "\"" + e + "\""))}}]}""";

    private const string ShipmentId = "01K6MSH0000000000000000001";

    /// <summary>
    /// Creates Shipment, an entity bound to no database (id, reference, shippedOn and the attributes in <paramref name="more"/>), in
    /// package Billing unless <paramref name="package"/> says another; <paramref name="top"/> adds members to the entity.
    /// </summary>
    private static Task CreateShipmentAsync(EditorRepo r, string more = "", string package = EditorRepo.BillingPackageId, string top = "") =>
        CreateAsync(r, $$"""
            {"kind":"entity","id":"{{ShipmentId}}","name":"Shipment","package":"{{package}}"{{top}},
             "key":{"attributes":["01K6MSH0000000000000000002"],"strategy":"uuid-v7"},
             "attributes":[{"id":"01K6MSH0000000000000000002","name":"id","type":"uuid","required":true},
               {"id":"01K6MSH0000000000000000003","name":"reference","type":"string","length":40,"required":true},
               {"id":"01K6MSH0000000000000000004","name":"shippedOn","type":"date"}{{more}}]}
            """);

    [Fact]
    public async Task The_status_lists_unbound_entities_and_every_table_with_the_entities_bound_to_it()
    {
        await using var r = EditorRepo.Create(packs: false);
        await CreateShipmentAsync(r);

        var status = (await r.Store.GetMaterializeStatusAsync(EditorRepo.MainDatabaseId, Ct))!;

        Assert.Equal(["Shipment"], status.Entities.Select(e => e.Name));
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
        await CreateShipmentAsync(r);
        var files = r.Files();

        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-tables", EditorRepo.MainDatabaseId, [ShipmentId]), Ct);

        Assert.True(plan.Valid, string.Join("; ", plan.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(files, r.Files());
        var table = Assert.Single(plan.Creates, c => c.Kind == "table");
        Assert.Equal("shipments", table.Name);
        Assert.Equal([ShipmentId], plan.Updates.Select(u => u.Id));
        Assert.Empty(plan.Deletes);
        Assert.DoesNotContain(plan.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Materializing_an_entity_already_bound_is_refused_and_the_next_one_follows_a_foreign_key_to_it()
    {
        await using var r = EditorRepo.Create(packs: false);
        var again = await ApplyAsync(r, Tables(EditorRepo.InvoiceId));
        Assert.Equal(SaveOutcome.Invalid, again.Outcome);
        var refusal = Assert.Single(again.Items.SelectMany(i => i.Diagnostics));
        Assert.Equal("MQ4055", refusal.Rule);
        Assert.Contains("already bound", refusal.Message, StringComparison.Ordinal);
        var bound = await ApplyAsync(r, Tables(InvoiceNoteId));
        Assert.Contains("already bound", bound.Items.SelectMany(i => i.Diagnostics).Single().Message, StringComparison.Ordinal);

        // A shipment goes to one customer, who is bound: the shipments table gets a foreign key to the customers table, and a relation
        // mapping names it.
        await CreateShipmentAsync(r);
        const string ShipsTo = "01K6MSH0000000000000000010";
        await CreateAsync(r, $$"""
            {"kind":"relation","id":"{{ShipsTo}}","name":"shipsTo","package":"{{EditorRepo.BillingPackageId}}","ends":[
              {"id":"01K6MSH0000000000000000011","entity":"{{EditorRepo.CustomerId}}","role":"customer","navigation":"customer","min":1,"max":1},
              {"id":"01K6MSH0000000000000000012","entity":"{{ShipmentId}}","role":"shipments","navigation":"shipments"}]}
            """);
        var stored = await ApplyAsync(r, Tables(ShipmentId));
        Assert.True(stored.Outcome == SaveOutcome.Saved, string.Join("; ", stored.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var snapshot = r.Store.Current!;
        var customers = snapshot.Get<Table>(CustomersTableId)!;
        var fk = snapshot.All<Table>().Single(t => t.Name == "shipments").ForeignKeys.Single();
        Assert.Equal(customers.Id, fk.ReferencesTable);
        Assert.Equal(customers.PrimaryKey!.Columns, fk.ReferencesColumns);
        Assert.Equal(fk.Id, snapshot.All<Mapping>().Single(m => m.Relation == ShipsTo).ForeignKey);
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task With_the_hashes_the_caller_read_a_document_changed_since_or_not_read_is_a_conflict()
    {
        await using var r = EditorRepo.Create(packs: false);
        await CreateShipmentAsync(r);
        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-tables", EditorRepo.MainDatabaseId, [ShipmentId]), Ct);
        var read = plan.Updates.Concat(plan.Deletes).ToDictionary(c => c.Id, c => r.Store.Current!.GetDocument(c.Id)!.Hash, StringComparer.Ordinal);
        string Op(IReadOnlyDictionary<string, string> hashes) =>
            Tables(ShipmentId)[..^1] + ",\"expectedHashes\":{" + string.Join(",", hashes.Select(p => $"\"{p.Key}\":\"{p.Value}\"")) + "}}";

        // Another save changes the shipment entity after it was read: the operation is a conflict and writes nothing.
        var shipment = (await r.Store.GetElementAsync(ShipmentId, Ct))!;
        var edited = JsonNode.Parse(shipment.Json.GetRawText())!.AsObject();
        edited["displayName"] = "Shipment (edited)";
        var save = await r.Store.SaveAsync(ShipmentId, Encoding.UTF8.GetBytes(edited.ToJsonString()), shipment.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, save.Outcome);
        var files = r.Files();
        var stale = await ApplyAsync(r, Op(read));
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        Assert.Equal(files, r.Files());

        // Not naming a document the operation changes is a conflict too; the hashes read now go through.
        var fresh = plan.Updates.Concat(plan.Deletes).ToDictionary(c => c.Id, c => r.Store.Current!.GetDocument(c.Id)!.Hash, StringComparer.Ordinal);
        Assert.Equal(SaveOutcome.Conflict, (await ApplyAsync(r, Op(fresh.Where(p => p.Key != ShipmentId).ToDictionary(p => p.Key, p => p.Value)))).Outcome);
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
        await CreateShipmentAsync(r);
        Assert.Contains("has no binding", await RefusedAsync(
            $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{ShipmentId}}"]}"""), StringComparison.Ordinal);
        Assert.Contains("one entity at a time", await RefusedAsync(
            $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Supplier}}","{{InvoiceNoteId}}"],"columns":["note"]}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Materializing_columns_adds_one_per_unmapped_attribute_shaped_and_mapped()
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
        // The names follow the conventions (snake case), and the enum is stored as the project stores enums by default (its member
        // values).
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
        await CreateShipmentAsync(r);
        Assert.Contains("has no binding", await RefusedAsync(
            $$"""{"op":"materialize-columns","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{ShipmentId}}"]}"""), StringComparison.Ordinal);
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
    private const string CustomersTableId = "01M8EJPZN867GR1CJR5ZERBJ0V";
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
    /// schema billing gives audited to every table. A table audited reaches that has its own created_at or updated_at (notes, and the
    /// tables of the entities that carry audited) keeps it by excluding the member, and the index with created_at.
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
        if (schemaDefault)
            EditOnDisk(r, ".maquettiste/model/databases/main/database.json", d => d["schemas"]![0]!["defaultStereotypes"] = new JsonArray("audited"));
        foreach (var file in Directory.EnumerateFiles(r.Repo.PathOf(".maquettiste/model/databases/main/tables"), "*.json").Order(StringComparer.Ordinal))
        {
            EditOnDisk(r, ".maquettiste/model/databases/main/tables/" + Path.GetFileName(file), t =>
            {
                if (!schemaDefault && !(t["stereotypes"]?.AsArray().Any(k => (string?)k == "audited") ?? false))
                    return;
                var names = t["columns"]!.AsArray().Select(c => (string?)c!["name"]).ToHashSet(StringComparer.Ordinal);
                var excludes = new JsonArray();
                if (names.Contains("created_at"))
                    excludes.Add(CreatedAt);
                if (names.Contains("updated_at"))
                    excludes.Add(UpdatedAt);
                if (names.Contains("created_at"))
                    excludes.Add(CreatedIndex);
                if (excludes.Count > 0)
                    t["excludes"] = excludes;
            });
        }
    }

    private static string Errors(ValidationReport report) =>
        string.Join("; ", report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message));

    [Fact]
    public async Task Materialize_tables_writes_no_index_an_inherited_index_replaces()
    {
        // An inherited index covers only its stereotype's own columns (an index redefinition cannot change them, MQ2027), so it meets an
        // indexed attribute's index only when the attribute's column takes a stereotype column's name, which validation reports (MQ4003).
        // The inherited index then stands for both: the designed table writes no index of its own for the column, so no second index of
        // the same name appears.
        await using var r = EditorRepo.Create(packs: false);
        AuditedMembers(r, schemaDefault: true);
        await CreateShipmentAsync(r, """,{ "id": "01K9MATER00000000000000080", "name": "createdAt", "type": "datetimeoffset", "indexed": true }""");

        // The change would add MQ4003, so it cannot be saved; the plan shows the table it would write.
        var plan = await r.Store.PlanMaterializeAsync(new MaterializeRequest("materialize-tables", EditorRepo.MainDatabaseId, [ShipmentId]), Ct);
        var shipments = JsonNode.Parse(plan.Creates.Single(u => u.Kind == "table" && u.Name == "shipments").Element!.Value.GetRawText())!;
        var created = shipments["columns"]!.AsArray().Single(c => (string?)c!["name"] == "created_at")!["id"]!.GetValue<string>();
        Assert.DoesNotContain(shipments["indexes"]?.AsArray() ?? [], i => i!["columns"]!.AsArray().Any(c => (string?)c!["column"] == created));
    }

    [Fact]
    public async Task Materialize_tables_keeps_inherited_members()
    {
        // The shipments table inherits audited's columns and index from schema billing's default; its designed table leaves them
        // inherited, keyed by the new table id.
        await using var r = EditorRepo.Create(packs: false);
        AuditedMembers(r, schemaDefault: true);
        await CreateShipmentAsync(r);

        var stored = await ApplyAsync(r, Tables(ShipmentId));

        Assert.True(stored.Outcome == SaveOutcome.Saved, string.Join("; ", stored.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var snapshot = r.Store.Current!;
        var file = snapshot.All<Table>().Single(t => t.Name == "shipments");
        // The file holds the own members only: no audit column, no index on created_at, and no stereotype (the default still reaches it).
        Assert.DoesNotContain(file.Columns, c => c.Name is "created_at" or "updated_at");
        Assert.DoesNotContain(file.Indexes, i => i.Columns.Any(c => c.Column?.Contains(CreatedAt, StringComparison.Ordinal) == true));
        Assert.Empty(file.Stereotypes);
        Assert.Empty(file.Excludes);
        Assert.Equal(0, (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);

        var after = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "shipments");
        var created = after.Columns.Single(c => c.Name == "created_at");
        Assert.Equal((file.Id + "." + CreatedAt, "schema-default"), (created.Key, created.MemberOrigin!.Source));
        Assert.Contains(after.Columns, c => c.Name == "updated_at" && c.Key == file.Id + "." + UpdatedAt);
        var index = after.Indexes.Single(i => i.MemberOrigin is not null);
        Assert.Equal((CreatedIndex, created.Key), (index.MemberOrigin!.MemberId, Assert.Single(index.Columns).Column));
    }

    [Fact]
    public async Task Materialize_tables_excludes_the_members_a_stereotype_realizes_through_attributes()
    {
        // Shipment carries audited, and schema billing's default reaches its table: the entity stores audited's attributes as columns,
        // so its table inherits no member; the designed table holds those columns as its own and excludes every member of audited.
        await using var r = EditorRepo.Create(packs: false);
        AuditedMembers(r, schemaDefault: true);
        await CreateShipmentAsync(r, top: ",\"stereotypes\":[\"audited\"]");

        var stored = await ApplyAsync(r, Tables(ShipmentId));

        Assert.True(stored.Outcome == SaveOutcome.Saved, string.Join("; ", stored.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var snapshot = r.Store.Current!;
        var file = snapshot.All<Table>().Single(t => t.Name == "shipments");
        Assert.Equal([CreatedAt, UpdatedAt, CreatedIndex], file.Excludes);
        Assert.Contains(file.Columns, c => c.Name == "created_at");
        Assert.Contains(file.Columns, c => c.Name == "updated_at");
        Assert.Equal("", Errors(await r.Store.ValidateAsync(ValidationScope.All, Ct)));
        var view = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;
        Assert.All(view.Tables.Single(t => t.Name == "shipments").Columns, c => Assert.Null(c.MemberOrigin));
    }

    [Fact]
    public async Task Materialize_tables_states_the_package_whose_default_reaches_the_table()
    {
        // Package Catalog gives its tables stamped, which only tables take; a designed table does not fall back to its entity's package,
        // so the table of an entity of Catalog states the package and inherits the member.
        await using var r = EditorRepo.Create(packs: false);
        const string Catalog = "01J92P0V025DRFTXKMS240G2NG", StampedAt = "01JC7AVD1TC0000000000000C3";
        File.WriteAllText(r.Repo.PathOf(".maquettiste/model/vocabularies/stereotypes/stamped.json"), $$"""
            {"kind": "stereotype", "id": "01JC7AVD1TC0000000000000S5", "key": "stamped", "name": "Stamped", "appliesTo": ["table"],
             "columns": [{"id": "{{StampedAt}}", "name": "stamped_at", "type": "datetimeoffset"}]}
            """, new UTF8Encoding(false));
        EditOnDisk(r, ".maquettiste/model/packages/catalog.json", p => p["defaultStereotypes"] = new JsonArray("stamped"));
        await CreateShipmentAsync(r, package: Catalog);

        var stored = await ApplyAsync(r, Tables(ShipmentId));

        Assert.True(stored.Outcome == SaveOutcome.Saved, string.Join("; ", stored.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var file = r.Store.Current!.All<Table>().Single(t => t.Name == "shipments");
        Assert.Equal(Catalog, file.Package);
        Assert.DoesNotContain(file.Columns, c => c.Name == "stamped_at");
        Assert.Equal("", Errors(await r.Store.ValidateAsync(ValidationScope.All, Ct)));
        var table = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "shipments");
        var stamped = table.Columns.Single(c => c.Name == "stamped_at");
        Assert.Equal((file.Id + "." + StampedAt, "package-default"), (stamped.Key, stamped.MemberOrigin!.Source));
    }

    [Fact]
    public async Task Materialize_columns_maps_an_attribute_to_an_inherited_column_of_its_name()
    {
        // vendors carries audited and inherits created_at and updated_at; Vendor carries audited too, so it has those attributes.
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
    public async Task Materialize_tables_writes_no_type_and_the_binding_keeps_the_columns_types()
    {
        // 0.28.0: a designed table names no type of the model. A column that stores one attribute typed by an enum, a reference type or a
        // scalar type gets its physical shape, the scalar type's native type written out; the entity's binding maps the column to that
        // attribute, so the column's bound type keeps the type.
        await using var r = EditorRepo.Create(packs: false);
        TypedBilling(r);
        // "Create tables…" never guesses how a pack stores a reference type: the project says it (here a CHECK, which the table states).
        EditOnDisk(r, ".maquettiste/maquettiste.json", s =>
        {
            s["conventions"] = new JsonObject { ["referenceStorage"] = new JsonObject { ["strategy"] = "check" } };
            s["referenceData"] = new JsonObject { ["strategies"] = new JsonObject { ["check"] = new JsonObject() } };
        });
        await CreateShipmentAsync(r, $$"""
            ,{"id":"01K6MSH0000000000000000021","name":"status","type":{"ref":"{{InvoiceStatusId}}"} },
             {"id":"01K6MSH0000000000000000022","name":"email","type":{"ref":"{{EmailAddressId}}"} },
             {"id":"01K6MSH0000000000000000023","name":"state","type":{"ref":"{{InvoiceStateId}}"} }
            """);

        var result = await ApplyAsync(r, Tables(ShipmentId));

        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var snapshot = r.Store.Current!;
        var shipments = snapshot.All<Table>().Single(t => t.Name == "shipments");
        Column Named(string name) => shipments.Columns.Single(c => c.Name == name);
        Assert.Equal("int32", Named("status").Type);
        Assert.Equal(("string", 1), (Named("state").Type, Named("state").Length));
        Assert.Equal(("string", 254, "citext"), (Named("email").Type, Named("email").Length, Named("email").NativeType));
        Assert.Equal(0, (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);

        // The bindings give the columns their types back.
        var columns = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "shipments").Columns;
        Assert.Equal(InvoiceStateId, columns.Single(c => c.Name == "state").BoundType?.ReferenceType);
        Assert.Equal(InvoiceStatusId, columns.Single(c => c.Name == "status").BoundType?.Enum);
        Assert.Equal(EmailAddressId, columns.Single(c => c.Name == "email").BoundType?.Scalar);
        Assert.Equal("citext", columns.Single(c => c.Name == "email").NativeType);
        Assert.Null(columns.Single(c => c.Name == "reference").BoundType);
    }

    [Fact]
    public async Task Materialize_tables_writes_no_attribute_sensitivity_on_the_column()
    {
        // Decision 12: an attribute's sensitive stays on the attribute; the designed column does not state it, and the bound column shows it.
        await using var r = EditorRepo.Create(packs: false);
        await CreateShipmentAsync(r, """,{"id":"01K6MSH0000000000000000031","name":"contact","type":"string","length":80,"sensitive":"pii"}""");

        var result = await ApplyAsync(r, Tables(ShipmentId));

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var snapshot = r.Store.Current!;
        Assert.Null(snapshot.All<Table>().Single(t => t.Name == "shipments").Columns.Single(c => c.Name == "contact").Sensitive);
        Assert.Equal(Sensitivity.Pii, snapshot.Get<Entity>(ShipmentId)!.Attributes.Single(a => a.Name == "contact").Sensitive);
        Assert.Equal(0, (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);
        var view = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;
        Assert.Equal("pii", view.Tables.Single(t => t.Name == "shipments").Columns.Single(c => c.Name == "contact").Sensitive);
    }

    [Fact]
    public async Task Materialize_columns_shapes_a_column_by_its_attributes_type()
    {
        // Decision 12: materialize-columns shapes a new column by its attribute's type: the enum (its member values, int32), the scalar
        // type (its base, length and native type) and the reference type (its code's type and length). It writes no type of the model
        // (0.28.0); the binding it adds a field to gives the column its bound type.
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
                ("id", "uuid", null, null),
                ("status", "int32", null, null),
                ("email", "string", 254, "citext"),
                ("state", "string", 1, null),
            ],
            table.Columns.Select(c => (c.Name, c.Type, c.Length, c.NativeType)));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => (d.ElementId == Vendor || d.ElementId == Vendors) && d.Severity == DiagnosticSeverity.Error);
        var view = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!;
        Assert.Equal("citext", view.Tables.Single(t => t.Name == "vendors").Columns.Single(c => c.Name == "email").NativeType);
    }

    // ---- attributes typed by their column's type (next-phase-2-design.md section 3.3, decision 11, D32) --------------------------------

    private const string EventsTable = "01K9X3C0000000000000000001", EventsId = "01K9X3C0000000000000000002", EventsState = "01K9X3C0000000000000000003";
    private const string EventsStatus = "01K9X3C0000000000000000004", EventsEmail = "01K9X3C0000000000000000005", EventsNote = "01K9X3C0000000000000000006";
    /// <summary>
    /// TypedBilling, with the designed table <c>invoice_events</c> (id, state not null, status, email, note), whose columns name no type of
    /// the model.
    /// </summary>
    private static async Task TypedEventsAsync(EditorRepo r)
    {
        TypedBilling(r);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{EventsTable}}","name":"invoice_events","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{EventsId}}","name":"id","type":"uuid","nullable":false},
              {"id":"{{EventsState}}","name":"state","type":"string","length":1,"nullable":false},
              {"id":"{{EventsStatus}}","name":"status","type":"string","length":1},
              {"id":"{{EventsEmail}}","name":"email","type":"string","length":254},
              {"id":"{{EventsNote}}","name":"note","type":"string","length":400}],
             "primaryKey":{"columns":["{{EventsId}}"]} }
            """);
    }

    private static string TypeOf(ModelAttribute attribute) => attribute.Type.Ref ?? attribute.Type.Builtin!;

    [Fact]
    public async Task Materialize_entities_types_an_attribute_by_its_columns_physical_type()
    {
        // Decision 11, since 0.28.0: a column of a table file names no type of the model, so each column becomes an attribute of its
        // keyword and facets, required when the column is not nullable; the team retypes an attribute by an enum, a reference type or a
        // scalar type, and the binding then gives the column that type.
        await using var r = EditorRepo.Create(packs: false);
        await TypedEventsAsync(r);

        var result = await ApplyAsync(r, $$"""{"op":"materialize-entities","database":"{{EditorRepo.MainDatabaseId}}","tables":["{{EventsTable}}"],"package":"{{EditorRepo.BillingPackageId}}"}""");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var entity = r.Store.Current!.All<Entity>().Single(e => e.Name == "InvoiceEvent");
        Assert.Equal(
            [
                ("id", "uuid", null, true, true),
                ("state", "string", 1, true, true),
                ("status", "string", 1, true, false),
                ("email", "string", 254, true, false),
                ("note", "string", 400, true, false),
            ],
            entity.Attributes.Select(a => (a.Name, TypeOf(a), a.Length, a.Type.Builtin is not null, a.Required)));
        Assert.All(entity.Attributes, a => Assert.Equal((null, null), (a.Precision, a.Scale)));
        Assert.Equal([EventsId, EventsState, EventsStatus, EventsEmail, EventsNote], entity.Bindings.Single().Fields.Select(f => f.Column));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => (d.ElementId == entity.Id || d.ElementId == EventsTable) && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Materialize_attributes_types_an_attribute_by_its_columns_physical_type()
    {
        // Decision 11, for materialize-attributes: a column of a table file gives an attribute of its keyword and facets (it names no type
        // of the model, 0.28.0).
        await using var r = EditorRepo.Create(packs: false);
        await TypedEventsAsync(r);
        const string Event = "01K9X3G0000000000000000001", EventKey = "01K9X3G0000000000000000002";
        await CreateAsync(r, $$"""
            {"kind":"entity","id":"{{Event}}","name":"Event","package":"{{EditorRepo.BillingPackageId}}","key":{"attributes":["{{EventKey}}"]},
             "attributes":[{"id":"{{EventKey}}","name":"id","type":"uuid","required":true}],
             "bindings":[{"id":"01K9X3G0000000000000000003","database":"{{EditorRepo.MainDatabaseId}}","source":"{{EventsTable}}",
               "fields":[{"attribute":"{{EventKey}}","column":"{{EventsId}}"}]}]}
            """);

        var typed = await ApplyAsync(r, $$"""{"op":"materialize-attributes","database":"{{EditorRepo.MainDatabaseId}}","entities":["{{Event}}"]}""");

        Assert.Equal(SaveOutcome.Saved, typed.Outcome);
        Assert.Equal(
            [("id", "uuid", null, true), ("state", "string", 1, true), ("status", "string", 1, false), ("email", "string", 254, false), ("note", "string", 400, false)],
            r.Store.Current!.Get<Entity>(Event)!.Attributes.Select(a => (a.Name, TypeOf(a), a.Length, a.Required)));
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
        await CreateShipmentAsync(r);
        Saved(await ApplyAsync(r, Tables(ShipmentId)));
        Assert.Equal(Shapes(r.Store.Current!.All<Table>().Single(t => t.Name == "shipments").Id), BindingOf(ShipmentId).Provenance!.Columns);

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
}
