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
