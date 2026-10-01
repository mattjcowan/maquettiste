using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// Deletes that resolve references (engine-design.md section 15.1) over the billing fixture with the real validator: the delete plan
/// read, <c>remove-references</c> refusals that read first, and <c>delete-dependents</c> cascades in one change that a batch undoes.
/// </summary>
public sealed class DeleteCascadeTests
{
    private const string ProductId = "01J92P0V0JR8BE8253SKT29ZG7";
    private const string MoneyId = "01J92P0V0304M76KA84NE4D1TE";
    private const string RefersToId = "01J92P0V1CC98R0RRCAV73SGF4";
    private const string DiagramId = "01J92P0V2164SDBW687ZV6E1MV";
    private const string InvoiceMappingId = "01J92P0V200XW93JQYSRCT2SE3";
    private const string SettlesMappingId = "01J92P0V1ZYK32MC97T5NBB5Y5";
    private const string SequenceId = "01J92P0V1S972MSFDJ8G6V6MWT";
    private const string ViewId = "01J92P0V1YZ4YP352KD1A50FXS";

    private static CancellationToken Ct => EditorRepo.Ct;

    [Fact]
    public async Task The_plan_names_what_each_resolution_does_and_writes_nothing()
    {
        await using var r = EditorRepo.Create();
        var before = r.Files();

        var refuse = await r.Store.GetDeletePlanAsync([ProductId], DeleteResolution.Refuse, Ct);
        var clear = await r.Store.GetDeletePlanAsync([ProductId], DeleteResolution.RemoveReferences, Ct);
        var cascade = await r.Store.GetDeletePlanAsync([ProductId], DeleteResolution.DeleteDependents, Ct);

        Assert.Equal(SaveOutcome.Referenced, refuse.Outcome);
        Assert.Equal([RefersToId, DiagramId], refuse.Refused.Select(x => x.Id).Distinct());
        Assert.Equal(SaveOutcome.Invalid, clear.Outcome);
        var required = Assert.Single(clear.Refused);
        Assert.Equal(("relation", "refers to", "/ends/1/entity", "MQ2001"), (required.Kind, required.Name, required.Pointer, required.Rule));
        Assert.Contains("entity Product", required.Why, StringComparison.Ordinal);
        Assert.DoesNotContain("Required properties", required.Why, StringComparison.Ordinal);
        Assert.Equal(SaveOutcome.Saved, cascade.Outcome);
        var relation = Assert.Single(cascade.Deletes);
        Assert.Equal((RefersToId, "relation", "refers to", "needs entity Product"), (relation.Id, relation.Kind, relation.Name, relation.Because));
        var member = Assert.Single(cascade.Removes);
        Assert.Equal((DiagramId, "/members/4", "member Product"), (member.Id, member.Pointer, member.What));
        Assert.Empty(cascade.Refused);
        Assert.Equal(before, r.Files());
    }

    [Fact]
    public async Task Remove_references_refuses_with_the_readable_reason_first()
    {
        await using var r = EditorRepo.Create();
        var database = (await r.Store.GetElementAsync(EditorRepo.MainDatabaseId, Ct))!;

        var result = await r.Store.DeleteAsync(database.Element.Id, database.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal("MQ2001", result.Diagnostics[0].Rule);
        Assert.Contains("database main", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Contains("delete-dependents", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule == "MQ1002");
    }

    [Fact]
    public async Task Delete_dependents_takes_a_database_with_its_tables_views_sequences_mappings_and_conventions()
    {
        await using var r = EditorRepo.Create();
        r.EditSettingsOnDisk(s =>
        {
            s["databases"] = new JsonObject { ["main"] = new JsonObject { ["pluralTables"] = false } };
            s["conventions"] = new JsonObject { ["pluralTables"] = true };
        });
        var pack = Path.Combine(r.Repo.ModelRoot, "templates", "sql-ddl", "pack.json");
        var manifest = JsonNode.Parse(File.ReadAllText(pack))!.AsObject();
        manifest["units"]![0]!["where"] = new JsonObject { ["database"] = "main" };
        File.WriteAllText(pack, manifest.ToJsonString());
        await r.Store.LoadAsync(Ct);
        var database = (await r.Store.GetElementAsync(EditorRepo.MainDatabaseId, Ct))!;

        var plan = await r.Store.GetDeletePlanAsync([EditorRepo.MainDatabaseId], DeleteResolution.DeleteDependents, Ct);
        var result = await r.Store.DeleteAsync(database.Element.Id, database.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, plan.Outcome);
        Assert.Equal([SequenceId, EditorRepo.OverlayTableId, ViewId, SettlesMappingId, InvoiceMappingId], plan.Deletes.Select(d => d.Id));
        Assert.All(plan.Deletes, d => Assert.Equal("needs database main", d.Because));
        Assert.Equal("/databases/main", Assert.Single(plan.Settings).Pointer);
        var warning = Assert.Single(plan.Warnings);
        Assert.Equal(("sql-ddl", "table"), (warning.Pack, warning.Unit));

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(plan.Deletes.Select(d => d.Id).Prepend(EditorRepo.MainDatabaseId).Order(StringComparer.Ordinal), result.Changes!.Deleted.Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(r.Repo.PathOf(".maquettiste/model/databases/main")));
        Assert.False(r.Repo.Exists(".maquettiste/model/mappings/invoice-in-main.json"));
        var settings = await r.Store.GetSettingsAsync(Ct);
        Assert.Empty(settings.Settings.Databases);
        Assert.True(settings.Settings.Conventions.PluralTables);
        Assert.Contains("\"database\":\"main\"", File.ReadAllText(pack), StringComparison.Ordinal); // pack filters stay, with the warning
    }

    [Fact]
    public async Task Delete_dependents_takes_an_entity_with_its_relations_overlay_and_mappings_in_one_change_that_a_batch_undoes()
    {
        await using var r = EditorRepo.Create();
        var invoice = (await r.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!;
        var plan = await r.Store.GetDeletePlanAsync([EditorRepo.InvoiceId], DeleteResolution.DeleteDependents, Ct);
        var affected = plan.Deletes.Select(d => d.Id).Prepend(EditorRepo.InvoiceId).ToList();
        var changed = plan.Removes.Select(x => x.Id).Concat(plan.Clears.Select(x => x.Id)).Distinct(StringComparer.Ordinal).ToList();
        var before = affected.Concat(changed).ToDictionary(id => id, id => r.Store.Current!.GetDocument(id)!, StringComparer.Ordinal);
        var files = r.Files();
        var notifications = new List<ChangeSet>();
        using var subscription = r.Store.OnChanged((c, _) =>
        {
            notifications.Add(c);
            return ValueTask.CompletedTask;
        });

        var result = await r.Store.DeleteAsync(invoice.Element.Id, invoice.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Single(notifications);
        Assert.Equal(
            ["01J92P0V1ACKN3G6TK3NJDTM82", "01J92P0V1BWHG0REWKSTR292RS", "01J92P0V1DKHD5Q02DV6S5M53D", EditorRepo.OverlayTableId, InvoiceMappingId, SettlesMappingId],
            plan.Deletes.Select(d => d.Id));
        Assert.Equal(affected.Order(StringComparer.Ordinal), result.Changes!.Deleted.Order(StringComparer.Ordinal));
        Assert.Equal([DiagramId], result.Changes.Changed.Select(c => c.Id));
        var diagram = r.Store.Current!.Get<Diagram>(DiagramId)!;
        Assert.DoesNotContain(diagram.Members, m => affected.Contains(m.Element));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // The editor's undo: one batch that re-creates what went and restores what changed.
        var operations = new JsonArray();
        foreach (var id in affected)
            operations.Add(new JsonObject { ["op"] = "create", ["element"] = JsonNode.Parse(before[id].Json.GetRawText()) });
        foreach (var id in changed)
        {
            operations.Add(new JsonObject
            {
                ["op"] = "update", ["id"] = id, ["expectedHash"] = r.Store.Current!.GetDocument(id)!.Hash, ["element"] = JsonNode.Parse(before[id].Json.GetRawText()),
            });
        }

        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject { ["operations"] = operations }.ToJsonString()));
        var undo = await r.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, undo.Outcome);
        var after = r.Files();
        // Every file comes back byte for byte except Invoice's description sidecar, which a delete removes and a create cannot carry.
        Assert.Equal(files.Keys.Where(k => k != ".maquettiste/model/entities/invoice.md"), after.Keys);
        Assert.All(after, f => Assert.Equal(files[f.Key], f.Value));
    }

    [Fact]
    public async Task Delete_dependents_removes_the_attributes_a_deleted_type_typed_and_what_named_them()
    {
        await using var r = EditorRepo.Create();
        var money = (await r.Store.GetElementAsync(MoneyId, Ct))!;

        var plan = await r.Store.GetDeletePlanAsync([MoneyId], DeleteResolution.DeleteDependents, Ct);
        var result = await r.Store.DeleteAsync(MoneyId, money.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);

        Assert.Empty(plan.Deletes);
        Assert.Contains(plan.Removes, x => x.Name == "InvoiceLine" && x.What == "attribute unitPrice" && x.SubKind == "attribute" && x.Because == "needs value-object Money");
        Assert.Contains(plan.Removes, x => x.Id == InvoiceMappingId && x.What == "attribute total" && x.Because == "needs attribute total of entity Invoice");
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.DoesNotContain(r.Store.Current!.Get<Entity>(EditorRepo.InvoiceId)!.Attributes, a => a.Name == "total");
        Assert.Single(r.Store.Current.Get<Mapping>(InvoiceMappingId)!.Attributes);
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task A_batch_delete_takes_the_resolution_and_shares_one_cascade()
    {
        await using var r = EditorRepo.Create();
        var product = (await r.Store.GetElementAsync(ProductId, Ct))!;
        var invoice = (await r.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!;
        var plan = await r.Store.GetDeletePlanAsync([ProductId, EditorRepo.InvoiceId], DeleteResolution.DeleteDependents, Ct);
        var json = $$"""
            {"operations":[
              {"op":"delete","id":"{{ProductId}}","expectedHash":"{{product.Hash}}","resolution":"delete-dependents"},
              {"op":"delete","id":"{{EditorRepo.InvoiceId}}","expectedHash":"{{invoice.Hash}}","resolution":"delete-dependents"}]}
            """;

        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes(json));
        var result = await r.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);

        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(DeleteResolution.DeleteDependents, parsed.Batch!.Operations[0].Resolution);
        Assert.Equal(SaveOutcome.Saved, plan.Outcome);
        Assert.Equal([ProductId, EditorRepo.InvoiceId], plan.Ids);
        Assert.Equal(7, plan.Deletes.Count); // refers to, then Invoice's three relations, overlay and two mappings
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(9, result.Changes!.Deleted.Count);
        Assert.Equal([DiagramId], result.Changes.Changed.Select(c => c.Id));
    }

    [Fact]
    public async Task A_refused_batch_delete_writes_nothing_and_an_unknown_id_is_refused_in_the_plan()
    {
        await using var r = EditorRepo.Create();
        var files = r.Files();
        var product = (await r.Store.GetElementAsync(ProductId, Ct))!;

        var refused = await r.Store.DeleteAsync(ProductId, product.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        var plan = await r.Store.GetDeletePlanAsync(["01J92P0V0000000000000000ZZ"], DeleteResolution.DeleteDependents, Ct);

        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Equal(files, r.Files());
        Assert.Equal(SaveOutcome.NotFound, plan.Outcome);
        Assert.Equal("not-found", Assert.Single(plan.Refused).Rule);
    }
}
