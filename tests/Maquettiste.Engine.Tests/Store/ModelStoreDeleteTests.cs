using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

public sealed class ModelStoreDeleteTests
{
    private const string ScratchId = "01K9XRF00000000000000000E1";
    private const string Settings = ".maquettiste/maquettiste.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleting_a_referenced_element_is_refused_with_its_referrers()
    {
        await using var s = await BillingStore.OpenAsync();
        var invoice = s.Doc("entity", "Invoice");
        var before = s.Files();

        var result = await s.Store.DeleteAsync(invoice.Element.Id, invoice.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Referenced, result.Outcome);
        var kinds = result.Referrers.Select(r => s.Model.GetDocument(r.FromElementId)!.Element.KindName).Distinct().Order(StringComparer.Ordinal);
        Assert.Equal(["diagram", "mapping", "query", "relation", "table"], kinds);
        // Sub-element references count too: the overlay table's index and column name invoice attributes.
        Assert.Contains(result.Referrers, r => r.ToId != invoice.Element.Id);
        Assert.Equal(before, s.Files());
        Assert.Empty(s.Notifications);
    }

    [Fact]
    public async Task Remove_references_is_invalid_when_a_referrer_needs_the_reference()
    {
        await using var s = await BillingStore.OpenAsync();
        var product = s.Doc("entity", "Product");
        var before = s.Files();

        var result = await s.Store.DeleteAsync(product.Element.Id, product.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var required = Assert.Single(result.Diagnostics, d => d.Rule == "MQ2001");
        Assert.Equal(s.Id("relation", "refers to"), required.ElementId);
        Assert.Equal(before, s.Files());
    }

    [Fact]
    public async Task Remove_references_clears_optional_references_and_list_entries()
    {
        await using var s = await BillingStore.OpenAsync();
        var softDelete = s.Doc("stereotype", "Soft delete");
        var invoice = s.Id("entity", "Invoice");
        var product = s.Id("entity", "Product");

        var result = await s.Store.DeleteAsync(softDelete.Element.Id, softDelete.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.False(s.Harness.Exists("model/vocabularies/stereotypes/soft-delete.json"));
        Assert.Equal(["aggregate-root", "audited"], s.Store.Current!.Get<Entity>(invoice)!.Stereotypes);
        Assert.Empty(s.Store.Current.Get<Entity>(product)!.Stereotypes);
        Assert.Equal([softDelete.Element.Id], result.Changes!.Deleted);
        // The stereotype's columns go with it: their drops are recorded as migration facts in the same change.
        Assert.Equal([invoice, product], result.Changes.Changed.Where(c => c.Kind != "migration").Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, result.Referrers.Count);
        Assert.Single(s.Notifications);

        var catalog = s.Doc("package", "Catalog");
        var package = await s.Store.DeleteAsync(catalog.Element.Id, catalog.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, package.Outcome);
        Assert.Null(s.Store.Current!.Get<Entity>(product)!.Package);
    }

    [Fact]
    public async Task Deleting_a_stereotype_lists_member_links_and_inherited_keys_as_referrers()
    {
        // next-phase-2-design.md section 3.1 (D10): a stereotype's members are sub-elements of it, so a plain delete lists the tables
        // that carry it, redefine its members or name an inherited copy by keyed id; remove-references refuses the keyed use in an
        // index, and delete-dependents removes that index with the redefinition.
        await using var s = await BillingStore.OpenAsync(load: false, validate: true);
        const string Notes = "model/databases/main/tables/notes.json";
        s.Harness.Write(StampedPath, StampedJson);
        var notes = JsonNode.Parse(s.Harness.Read(Notes))!.AsObject();
        notes["stereotypes"] = new JsonArray("stamped");
        notes["redefines"] = new JsonArray(new JsonObject { ["member"] = StampedById, ["comment"] = "Who stamped the note." });
        notes["indexes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K9XRF00000000000000000F9",
            ["columns"] = new JsonArray(new JsonObject { ["column"] = NotesId + "." + StampedById }),
        });
        s.Harness.Write(Notes, notes.ToJsonString());
        await s.Store.LoadAsync(Ct);
        var stamped = s.Model.GetDocument(StampedId)!;
        var before = s.Files();

        var refused = await s.Store.DeleteAsync(StampedId, stamped.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Equal(
            [("/indexes/1/columns/0/column", StampedById), ("/redefines/0/member", StampedById), ("/stereotypes/0", StampedId)],
            refused.Referrers.Where(r => r.FromElementId == NotesId).Select(r => (r.JsonPointer, r.ToId)).Distinct().Order());
        Assert.Equal(before, s.Files());
        var plan = await s.Store.GetDeletePlanAsync([StampedId], DeleteResolution.Refuse, Ct);
        var keyed = Assert.Single(plan.Refused, r => r.Pointer == "/indexes/1/columns/0/column");
        Assert.Equal($"still references column stamped_by of stereotype Stamped through its inherited copy '{NotesId}.{StampedById}'.", keyed.Why);
        Assert.Contains(plan.Refused, r => r.Pointer == "/redefines/0/member" && r.Why == "still references column stamped_by of stereotype Stamped.");

        var clear = await s.Store.GetDeletePlanAsync([StampedId], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, clear.Outcome);
        var refusal = Assert.Single(clear.Refused);
        Assert.Equal((NotesId, "/indexes/1/columns/0/column", "MQ2001"), (refusal.Id, refusal.Pointer, refusal.Rule));
        Assert.Contains("delete-dependents", refusal.Why, StringComparison.Ordinal);

        var cascade = await s.Store.GetDeletePlanAsync([StampedId], DeleteResolution.DeleteDependents, Ct);
        Assert.Equal(SaveOutcome.Saved, cascade.Outcome);
        Assert.Equal(
            [("/indexes/1", "key stamped_by"), ("/redefines/0", "redefinition stamped_by")],
            cascade.Removes.Where(r => r.Id == NotesId).Select(r => (r.Pointer, r.What)));
        Assert.Equal(["/stereotypes/0"], cascade.Clears.Where(c => c.Id == NotesId).Select(c => c.Pointer));
        var deleted = await s.Store.DeleteAsync(StampedId, stamped.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        var table = s.Store.Current!.Get<Table>(NotesId)!;
        Assert.Empty(table.Stereotypes);
        Assert.Empty(table.Redefines);
        Assert.Equal(["01K6BND0000000000000000007"], table.Indexes.Select(i => i.Id));
    }

    private const string NotesId = "01K6BND0000000000000000001";
    private const string StampedPath = "model/vocabularies/stereotypes/stamped.json";
    private const string StampedId = "01K9XRF00000000000000000F1";
    private const string StampedAtId = "01K9XRF00000000000000000F2";
    private const string StampedById = "01K9XRF00000000000000000F3";
    private const string StampedIndexId = "01K9XRF00000000000000000F4";

    /// <summary>A stereotype <c>stamped</c> for tables: columns stamped_at and stamped_by, and an index on stamped_by.</summary>
    private const string StampedJson = $$"""
        { "kind": "stereotype", "id": "{{StampedId}}", "key": "stamped", "name": "Stamped", "appliesTo": ["table"],
          "columns": [ { "id": "{{StampedAtId}}", "name": "stamped_at", "type": "datetimeoffset" },
                       { "id": "{{StampedById}}", "name": "stamped_by", "type": "string", "length": 40 } ],
          "indexes": [ { "id": "{{StampedIndexId}}", "columns": [ { "column": "{{StampedById}}" } ] } ] }
        """;

    [Fact]
    public async Task Remove_references_drops_diagram_members_and_a_delete_removes_the_sidecar()
    {
        await using var s = await BillingStore.OpenAsync();
        s.Harness.Write("model/entities/scratch.md", "Scratch notes.\n");
        var created = await s.Store.CreateAsync("{\"kind\":\"entity\",\"name\":\"Scratch\",\"description\":{\"file\":\"scratch.md\"}}"u8.ToArray(), ChangeSource.Editor, Ct);
        Assert.Equal("Scratch notes.\n", created.Current!.SidecarText);
        var diagram = s.Doc("diagram", "Billing overview");
        var withMember = await s.Store.SaveAsync(
            diagram.Element.Id,
            BillingStore.Edit(diagram, n => n["members"]!.AsArray().Add(new JsonObject { ["element"] = created.Id, ["x"] = 10, ["y"] = 20 })),
            diagram.Hash,
            ChangeSource.Editor,
            Ct);
        Assert.Equal(SaveOutcome.Saved, withMember.Outcome);
        var refused = await s.Store.DeleteAsync(created.Id!, created.Hash!, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);

        var deleted = await s.Store.DeleteAsync(created.Id!, created.Hash!, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        Assert.False(s.Harness.Exists("model/entities/scratch.json"));
        Assert.False(s.Harness.Exists("model/entities/scratch.md"));
        Assert.True(s.Harness.Exists("model/entities/invoice.md"));
        var members = s.Store.Current!.Get<Diagram>(diagram.Element.Id)!.Members;
        Assert.Equal(((Diagram)diagram.Element).Members.Count, members.Count);
        Assert.DoesNotContain(members, m => m.Element == created.Id);
    }

    [Fact]
    public async Task Remove_references_clears_a_column_typeRef_and_keeps_its_type()
    {
        // next-phase-2-design.md section 3.2 (D20): remove-references drops the whole { "ref": ... } object, never leaving {}, and the
        // column keeps its physical type and facets.
        await using var s = await BillingStore.OpenAsync();
        var created = await s.Store.CreateAsync(
            "{\"kind\":\"enum\",\"name\":\"NoteSubject\",\"members\":[{\"name\":\"Invoice\",\"code\":\"invoice\"},{\"name\":\"Customer\",\"code\":\"customer\"}]}"u8.ToArray(),
            ChangeSource.Editor,
            Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        var notes = s.Doc("table", "notes");
        var typed = await s.Store.SaveAsync(
            notes.Element.Id,
            BillingStore.Edit(notes, n => n["columns"]![1]!["typeRef"] = new JsonObject { ["ref"] = created.Id }),
            notes.Hash,
            ChangeSource.Editor,
            Ct);
        Assert.Equal(SaveOutcome.Saved, typed.Outcome);
        Assert.Equal(created.Id, s.Store.Current!.Get<Table>(notes.Element.Id)!.Columns[1].TypeRef?.Ref);
        var refused = await s.Store.DeleteAsync(created.Id!, created.Hash!, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Contains(refused.Referrers, r => r.FromElementId == notes.Element.Id && r.JsonPointer == "/columns/1/typeRef/ref");

        var deleted = await s.Store.DeleteAsync(created.Id!, created.Hash!, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        Assert.Equal([notes.Element.Id], deleted.Changes!.Changed.Where(c => c.Kind != "migration").Select(c => c.Id));
        var column = s.Store.Current!.Get<Table>(notes.Element.Id)!.Columns[1];
        Assert.Equal(("entity_type", "string", 32, false), (column.Name, column.Type, column.Length, column.Nullable));
        Assert.Null(column.TypeRef);
        Assert.DoesNotContain("typeRef", s.Harness.Read("model/databases/main/tables/notes.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_column_delete_ignores_provenance()
    {
        // next-phase-2-design.md section 3.3 (D30, decision 2): a binding's provenance is a record keyed by column ids, not a link, so a
        // column it records but nothing in the binding names is deleted plainly; the entity's file is not touched and keeps the entry,
        // which then reads as removed.
        await using var s = await BillingStore.OpenAsync(load: false, validate: true);
        const string Notes = "model/databases/main/tables/notes.json", Note = "model/entities/invoice-note.json";
        const string Spare = "01K9XPV0000000000000000001";
        var notes = JsonNode.Parse(s.Harness.Read(Notes))!.AsObject();
        notes["columns"]!.AsArray().Add(new JsonObject { ["id"] = Spare, ["name"] = "spare", ["type"] = "text" });
        s.Harness.Write(Notes, notes.ToJsonString());
        var note = JsonNode.Parse(s.Harness.Read(Note))!.AsObject();
        note["bindings"]![0]!["provenance"] = new JsonObject
        {
            ["columns"] = new JsonObject { ["01K6BND0000000000000000002"] = "00000000:00000000", [Spare] = "11111111:11111111" },
        };
        s.Harness.Write(Note, note.ToJsonString());
        await s.Store.LoadAsync(Ct);
        var entity = s.Model.Get<Entity>(NoteId)!;
        Assert.Equal(["01K6BND0000000000000000002", Spare], entity.Bindings[0].Provenance!.Columns.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(s.Model.ReferencesTo(Spare));
        var noteBefore = s.Harness.Read(Note);
        var table = s.Model.GetDocument(NotesId)!;

        var saved = await s.Store.SaveAsync(NotesId, BillingStore.Edit(table, t =>
        {
            var columns = t["columns"]!.AsArray();
            columns.Remove(columns.Single(c => c!["id"]!.GetValue<string>() == Spare));
        }), table.Hash, ChangeSource.Editor, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.DoesNotContain(s.Store.Current!.Get<Table>(NotesId)!.Columns, c => c.Id == Spare);
        Assert.Equal(noteBefore, s.Harness.Read(Note));
        Assert.Contains(Spare, s.Store.Current!.Get<Entity>(NoteId)!.Bindings[0].Provenance!.Columns.Keys);
    }

    private const string NoteId = "01K6BND0000000000000000010";

    [Fact]
    public async Task Deleting_an_unreferenced_element_removes_its_file()
    {
        await using var s = await BillingStore.OpenAsync();
        var view = s.Doc("view", "outstanding_invoices");

        var result = await s.Store.DeleteAsync(view.Element.Id, view.Hash, DeleteResolution.Refuse, ChangeSource.Cli, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Null(result.Current);
        Assert.False(s.Harness.Exists("model/databases/main/views/outstanding-invoices.json"));
        Assert.False(Directory.Exists(s.Harness.Model("model/databases/main/views"))); // emptied folders go
        Assert.Equal([view.Element.Id], result.Changes!.Deleted);
        Assert.Equal(ChangeSource.Cli, result.Changes.Source);
        Assert.Null(s.Store.Current!.GetDocument(view.Element.Id));
    }

    [Fact]
    public async Task Delete_checks_the_etag_and_the_id()
    {
        await using var s = await BillingStore.OpenAsync();
        var view = s.Doc("view", "outstanding_invoices");

        var conflict = await s.Store.DeleteAsync(view.Element.Id, new string('0', 64), DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        var missing = await s.Store.DeleteAsync("01J92P0V0000000000000000ZZ", view.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        var attributeId = ((Entity)s.Doc("entity", "Customer").Element).Attributes[1].Id;
        var subElement = await s.Store.DeleteAsync(attributeId, view.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Conflict, conflict.Outcome);
        Assert.Equal(view.Hash, conflict.Hash);
        Assert.Same(view, conflict.Current);
        Assert.Equal(SaveOutcome.NotFound, missing.Outcome);
        Assert.Equal(SaveOutcome.NotFound, subElement.Outcome); // sub-elements change through their owner
        Assert.True(s.Harness.Exists("model/databases/main/views/outstanding-invoices.json"));
    }

    [Fact]
    public async Task Deleting_a_file_that_vanished_from_disk_is_not_found()
    {
        await using var s = await BillingStore.OpenAsync();
        var view = s.Doc("view", "outstanding_invoices");
        File.Delete(s.Harness.Model("model/databases/main/views/outstanding-invoices.json"));

        var result = await s.Store.DeleteAsync(view.Element.Id, view.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.NotFound, result.Outcome);
        Assert.Equal([view.Element.Id], Assert.Single(s.Notifications).Deleted);
    }

    [Fact]
    public async Task A_create_body_without_an_object_is_invalid()
    {
        await using var s = await BillingStore.OpenAsync();

        var array = await s.Store.CreateAsync("[1]"u8.ToArray(), ChangeSource.Editor, Ct);
        var noKind = await s.Store.CreateAsync(Encoding.UTF8.GetBytes("{\"name\":\"X\"}"), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, array.Outcome);
        Assert.Equal(("MQ1002", "/kind"), (noKind.Diagnostics[0].Rule, noKind.Diagnostics[0].JsonPointer));
    }

    [Fact]
    public async Task A_pack_parameter_naming_the_element_refuses_a_plain_delete()
    {
        // next-phase-1-design.md section 4.1: a pack parameter value an x-ref types is a setting, not a model document; a plain delete
        // never changes the settings, so it refuses with MQ2001 in maquettiste.json at the value, with no element id.
        await using var s = await WithReportsPack(optional: true);
        var scratch = s.Model.GetDocument(ScratchId)!;
        var before = s.Files();

        var result = await s.Store.DeleteAsync(ScratchId, scratch.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal(["/packs/reports/parameters/audited/0", "/packs/reports/parameters/rootEntity"],
            result.Diagnostics.Where(x => x.Rule == "MQ2001").Select(x => x.JsonPointer!).Order(StringComparer.Ordinal));
        var d = Assert.Single(result.Diagnostics, x => x.Rule == "MQ2001" && x.JsonPointer == "/packs/reports/parameters/rootEntity");
        Assert.Equal((null, Settings), (d.ElementId, d.FilePath));
        Assert.Contains("Parameter 'rootEntity' of pack 'reports' names entity Scratch", d.Message, StringComparison.Ordinal);
        Assert.Equal(before, s.Files());

        var plan = await s.Store.GetDeletePlanAsync([ScratchId], DeleteResolution.Refuse, Ct);
        Assert.Equal(SaveOutcome.Invalid, plan.Outcome);
        Assert.Equal(
            [(null, "/packs/reports/parameters/audited/0"), (null, "/packs/reports/parameters/rootEntity")],
            plan.Refused.Select(x => (x.Id, x.Pointer)).Order());
        Assert.All(plan.Refused, x => Assert.Equal("MQ2001", x.Rule));
        // A required value refuses remove-references the same way.
        await using var required = await WithReportsPack(optional: false);
        var refused = await required.Store.GetDeletePlanAsync([ScratchId], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Contains(refused.Refused, x => x.Id is null && x.Pointer == "/packs/reports/parameters/rootEntity" && x.Why.Contains("without optional", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_references_removes_an_optional_pack_parameter_in_the_same_change()
    {
        await using var s = await WithReportsPack(optional: true);
        var scratch = s.Model.GetDocument(ScratchId)!;
        var customer = s.Id("entity", "Customer");

        var plan = await s.Store.GetDeletePlanAsync([ScratchId], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Saved, plan.Outcome);
        Assert.Equal(["/packs/reports/parameters/audited/0", "/packs/reports/parameters/rootEntity"], plan.Settings.Select(x => x.Pointer).Order(StringComparer.Ordinal));
        Assert.Contains(plan.Settings, x => x.What == "parameter 'rootEntity' of pack 'reports', which names entity Scratch");

        var result = await s.Store.DeleteAsync(ScratchId, scratch.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.False(s.Harness.Exists("model/entities/scratch.json"));
        var parameters = s.Model.Settings.Packs["reports"].Parameters;
        Assert.False(parameters.ContainsKey("rootEntity"));
        Assert.Equal([customer], parameters["audited"].EnumerateArray().Select(e => e.GetString()));
        using var written = JsonDocument.Parse(s.Harness.Read("maquettiste.json"));
        var onDisk = written.RootElement.GetProperty("packs").GetProperty("reports").GetProperty("parameters");
        Assert.False(onDisk.TryGetProperty("rootEntity", out _));
        Assert.Equal(1, onDisk.GetProperty("audited").GetArrayLength());
        Assert.Single(s.Notifications);
    }

    /// <summary>
    /// The billing store with an entity Scratch that nothing in the model references, and a local pack <c>reports</c> whose
    /// parameterSchema types <c>rootEntity</c> and the items of <c>audited</c> as entity links; the project names Scratch in both.
    /// </summary>
    private static async Task<BillingStore> WithReportsPack(bool optional)
    {
        var s = await BillingStore.OpenAsync(load: false);
        var flag = optional ? "true" : "false";
        s.Harness.Write("templates/reports/pack.json", $$"""
            { "name": "reports", "version": "1.0.0", "engine": ">=1.0", "units": [],
              "parameterSchema": { "properties": {
                "rootEntity": { "type": "string", "x-ref": { "kind": "entity", "optional": {{flag}} } },
                "audited": { "type": "array", "items": { "type": "string", "x-ref": { "kind": "entity", "optional": {{flag}} } } } } } }
            """);
        var entity = new JsonObject
        {
            ["kind"] = "entity", ["id"] = ScratchId, ["name"] = "Scratch",
            ["key"] = new JsonObject { ["attributes"] = new JsonArray("01K9XRF00000000000000000E2") },
            ["attributes"] = new JsonArray(new JsonObject { ["id"] = "01K9XRF00000000000000000E2", ["name"] = "id", ["type"] = "uuid", ["required"] = true }),
        };
        s.Harness.Write("model/entities/scratch.json", Encoding.UTF8.GetString(TestServices.Json.Write(entity, "entity.json", ".maquettiste/model/entities/scratch.json")));
        var settings = JsonNode.Parse(s.Harness.Read("maquettiste.json"))!.AsObject();
        var customer = JsonNode.Parse(s.Harness.Read("model/entities/customer.json"))!["id"]!.GetValue<string>();
        settings["packs"]!["reports"] = new JsonObject
        {
            ["parameters"] = new JsonObject { ["rootEntity"] = ScratchId, ["audited"] = new JsonArray(ScratchId, customer) },
        };
        s.Harness.Write("maquettiste.json", Encoding.UTF8.GetString(TestServices.Json.Write(settings, "maquettiste.json", Settings)));
        await s.Store.LoadAsync(Ct);
        return s;
    }
}
