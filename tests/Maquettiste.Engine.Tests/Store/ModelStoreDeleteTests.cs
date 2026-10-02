using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Store;

public sealed class ModelStoreDeleteTests
{
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
        Assert.Equal([invoice, product], result.Changes.Changed.Select(c => c.Id).Order(StringComparer.Ordinal));
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
}
