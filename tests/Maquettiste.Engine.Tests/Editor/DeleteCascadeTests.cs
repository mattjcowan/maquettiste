using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

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
    private const string InvoicesByCustomerId = "01K6QRY0000000000000000001";
    private const string RevenueByMonthId = "01K6QRY0000000000000000002";
    private const string FindCustomersWithIssuedInvoicesId = "01K6QRY0000000000000000003";
    private const string NotesTableId = "01K6BND0000000000000000001";
    private const string InvoiceNoteId = "01K6BND0000000000000000010";
    private const string CustomerNoteId = "01K6BND0000000000000000020";
    private const string RevenueMonthId = "01K6BND0000000000000000030";
    private const string ReviewerId = "01K9XRF00000000000000000A1";
    private const string ApproverId = "01K9XRF00000000000000000B1";
    private const string RefundId = "01K9XRF00000000000000000C1";
    private const string CurrencyId = "01K9XRF00000000000000000E1";
    private const string PricedId = "01K9XRF00000000000000000F1";

    private static CancellationToken Ct => EditorRepo.Ct;

    private const string VoucherId = "01JR5ND00000000000000000V1";
    private const string VoucherKeyId = "01JR5ND00000000000000000V2";
    private const string VoucherLifecycleId = "01JR5ND00000000000000000P1";
    private const string VoucherSentId = "01JR5ND00000000000000000E1";
    private const string VoucherDraftId = "01JR5ND00000000000000000S1";
    private const string IssueVoucherId = "01JR5ND00000000000000000Q1";
    private const string CouponId = "01JR5ND00000000000000000C1";
    private const string CouponKeyId = "01JR5ND00000000000000000C2";
    private const string RedeemCouponId = "01JR5ND00000000000000000R1";

    /// <summary>
    /// Billing with an entity Voucher, its lifecycle with the event sent, the operation issue on Voucher that fires it, and the
    /// operation redeem on an entity Coupon (next-phase-2-design.md section 4.1).
    /// </summary>
    private static async Task<EditorRepo> WithOperationAsync()
    {
        var r = EditorRepo.Create(packs: false);
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes($$"""
            { "operations": [
              { "op": "create", "element": { "kind": "entity", "id": "{{VoucherId}}", "name": "Voucher", "lifecycle": "{{VoucherLifecycleId}}",
                  "key": { "attributes": [ "{{VoucherKeyId}}" ] }, "attributes": [ { "id": "{{VoucherKeyId}}", "name": "id", "type": "uuid" } ] } },
              { "op": "create", "element": { "kind": "process", "id": "{{VoucherLifecycleId}}", "name": "VoucherLifecycle", "use": "lifecycle",
                  "subject": "{{VoucherId}}", "events": [ { "id": "{{VoucherSentId}}", "name": "sent" } ],
                  "states": [ { "id": "{{VoucherDraftId}}", "name": "draft" } ] } },
              { "op": "create", "element": { "kind": "operation", "id": "{{IssueVoucherId}}", "name": "issue", "subject": "{{VoucherId}}", "effect": "change",
                  "outcomes": [ { "id": "01JR5ND00000000000000000Q2", "name": "applied" } ], "fires": [ "{{VoucherSentId}}" ] } },
              { "op": "create", "element": { "kind": "entity", "id": "{{CouponId}}", "name": "Coupon",
                  "key": { "attributes": [ "{{CouponKeyId}}" ] }, "attributes": [ { "id": "{{CouponKeyId}}", "name": "id", "type": "uuid" } ] } },
              { "op": "create", "element": { "kind": "operation", "id": "{{RedeemCouponId}}", "name": "redeem", "subject": "{{CouponId}}", "effect": "change",
                  "outcomes": [ { "id": "01JR5ND00000000000000000R2", "name": "applied" } ] } }
            ] }
            """));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        var applied = await r.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.Message}")));
        return r;
    }

    [Fact]
    public async Task Deleting_the_lifecycle_whose_event_an_operation_fires_is_refused_and_remove_references_clears_the_entry()
    {
        await using var r = await WithOperationAsync();
        var process = (await r.Store.GetElementAsync(VoucherLifecycleId, Ct))!;

        var refused = await r.Store.DeleteAsync(VoucherLifecycleId, process.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Contains(refused.Referrers, x => x is { FromElementId: IssueVoucherId, JsonPointer: "/fires/0", ToId: VoucherSentId });

        var plan = await r.Store.GetDeletePlanAsync([VoucherLifecycleId], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Saved, plan.Outcome);
        Assert.Contains(plan.Clears, c => (c.Id, c.Pointer) == (IssueVoucherId, "/fires/0"));
        Assert.DoesNotContain(plan.Deletes, d => d.Id == IssueVoucherId);

        var cleared = await r.Store.DeleteAsync(VoucherLifecycleId, process.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.True(cleared.Outcome == SaveOutcome.Saved, string.Join("; ", cleared.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Null(await r.Store.GetElementAsync(VoucherLifecycleId, Ct));
        var operation = (await r.Store.GetElementAsync(IssueVoucherId, Ct))!;
        Assert.False(operation.Json.TryGetProperty("fires", out _));
        Assert.Equal(VoucherId, operation.Json.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Deleting_a_subject_entity_is_refused_and_both_resolutions_clear_subject()
    {
        await using var r = await WithOperationAsync();
        var coupon = (await r.Store.GetElementAsync(CouponId, Ct))!;

        var refused = await r.Store.DeleteAsync(CouponId, coupon.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Contains(refused.Referrers, x => x is { FromElementId: RedeemCouponId, JsonPointer: "/subject" });

        foreach (var resolution in new[] { DeleteResolution.RemoveReferences, DeleteResolution.DeleteDependents })
        {
            var plan = await r.Store.GetDeletePlanAsync([CouponId], resolution, Ct);
            Assert.True(plan.Outcome == SaveOutcome.Saved, resolution + ": " + string.Join("; ", plan.Refused.Select(x => x.Why)));
            Assert.DoesNotContain(plan.Deletes, d => d.Id == RedeemCouponId);
            Assert.Contains(plan.Clears.Select(c => (c.Id, c.Pointer)).Concat(plan.Removes.Select(m => (m.Id, m.Pointer))), c => c == (RedeemCouponId, "/subject"));
        }

        var cleared = await r.Store.DeleteAsync(CouponId, coupon.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);
        Assert.True(cleared.Outcome == SaveOutcome.Saved, string.Join("; ", cleared.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var operation = (await r.Store.GetElementAsync(RedeemCouponId, Ct))!;
        Assert.False(operation.Json.TryGetProperty("subject", out _));
        Assert.Null(await r.Store.GetElementAsync(CouponId, Ct));
    }

    [Fact]
    public async Task Deleting_the_entity_a_member_keys_or_an_outcome_returns_removes_that_part_and_remove_references_refuses()
    {
        const string FindId = "01JR5ND00000000000000000F1";
        await using var r = await WithOperationAsync();
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes($$"""
            { "operations": [
              { "op": "create", "element": { "kind": "operation", "id": "{{FindId}}", "name": "find", "effect": "read",
                  "input": [ { "id": "01JR5ND00000000000000000F2", "name": "coupon", "keyOf": "{{CouponId}}" },
                             { "id": "01JR5ND00000000000000000F3", "name": "note", "type": "string" } ],
                  "outcomes": [ { "id": "01JR5ND00000000000000000F4", "name": "found", "returns": { "entity": "{{CouponId}}" } } ] } }
            ] }
            """));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        var applied = await r.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.Message}")));
        var coupon = (await r.Store.GetElementAsync(CouponId, Ct))!;

        // Clearing `keyOf` or `entity` would leave a member with no form (MQ3024) or a returns with none (MQ3025).
        var refused = await r.Store.GetDeletePlanAsync([CouponId], DeleteResolution.RemoveReferences, Ct);
        Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
        Assert.Contains(refused.Refused, x => x.Why.Contains("operation member", StringComparison.Ordinal));

        var deleted = await r.Store.DeleteAsync(CouponId, coupon.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);
        Assert.True(deleted.Outcome == SaveOutcome.Saved, string.Join("; ", deleted.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var find = (await r.Store.GetElementAsync(FindId, Ct))!;
        Assert.Equal("note", Assert.Single(find.Json.GetProperty("input").EnumerateArray()).GetProperty("name").GetString());
        Assert.False(find.Json.GetProperty("outcomes")[0].TryGetProperty("returns", out _));
        Assert.DoesNotContain((await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule is "MQ3024" or "MQ3025");
    }

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
        Assert.Equal([SequenceId, EditorRepo.OverlayTableId, ViewId, SettlesMappingId, InvoiceMappingId, NotesTableId, InvoicesByCustomerId, RevenueByMonthId,
                FindCustomersWithIssuedInvoicesId],
            plan.Deletes.Select(d => d.Id));
        Assert.All(plan.Deletes, d => Assert.Equal("needs database main", d.Because));
        // The entities bound to the database lose their binding and stay (erratum E43).
        Assert.Equal([InvoiceNoteId, CustomerNoteId, RevenueMonthId], plan.Removes.Select(x => x.Id).Order(StringComparer.Ordinal));
        Assert.All(plan.Removes, x => Assert.Equal(("binding", "/bindings/0", "the binding needs database main; the entity stays"), (x.SubKind, x.Pointer, x.Because)));
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
        await using var r = WithoutFacts();
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
            [
                "01J92P0V1ACKN3G6TK3NJDTM82", "01J92P0V1BWHG0REWKSTR292RS", "01J92P0V1DKHD5Q02DV6S5M53D", EditorRepo.OverlayTableId, InvoiceMappingId,
                InvoicesByCustomerId, RevenueByMonthId, FindCustomersWithIssuedInvoicesId, SettlesMappingId,
            ],
            plan.Deletes.Select(d => d.Id));
        Assert.Equal(affected.Order(StringComparer.Ordinal), result.Changes!.Deleted.Order(StringComparer.Ordinal));
        // RevenueMonth reads the deleted RevenueByMonth query: its binding goes and the entity stays.
        Assert.Equal([DiagramId, RevenueMonthId], result.Changes.Changed.Select(c => c.Id));
        Assert.Empty(r.Store.Current!.Get<Entity>(RevenueMonthId)!.Bindings);
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
        await using var r = WithoutFacts();
        var money = (await r.Store.GetElementAsync(MoneyId, Ct))!;

        var plan = await r.Store.GetDeletePlanAsync([MoneyId], DeleteResolution.DeleteDependents, Ct);
        var result = await r.Store.DeleteAsync(MoneyId, money.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);

        // The revenue query reads total's amount column by its key (alias.attribute.member), so it goes whole with the attribute.
        var query = Assert.Single(plan.Deletes);
        Assert.Equal((RevenueByMonthId, "needs attribute amount of value-object Money"), (query.Id, query.Because));
        Assert.Contains(plan.Removes, x => x.Name == "InvoiceLine" && x.What == "attribute unitPrice" && x.SubKind == "attribute" && x.Because == "needs value-object Money");
        Assert.Contains(plan.Removes, x => x.Id == InvoiceMappingId && x.What == "attribute total" && x.Because == "needs attribute total of entity Invoice");
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.DoesNotContain(r.Store.Current!.Get<Entity>(EditorRepo.InvoiceId)!.Attributes, a => a.Name == "total");
        Assert.Single(r.Store.Current.Get<Mapping>(InvoiceMappingId)!.Attributes);
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task A_table_lists_the_queries_that_read_it_and_delete_dependents_removes_each_whole()
    {
        await using var r = EditorRepo.Create();
        var table = (await r.Store.GetElementAsync(EditorRepo.OverlayTableId, Ct))!;
        string[] queries = [InvoicesByCustomerId, RevenueByMonthId, FindCustomersWithIssuedInvoicesId];

        var refused = await r.Store.DeleteAsync(table.Element.Id, table.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        var cleared = await r.Store.GetDeletePlanAsync([EditorRepo.OverlayTableId], DeleteResolution.RemoveReferences, Ct);
        var plan = await r.Store.GetDeletePlanAsync([EditorRepo.OverlayTableId], DeleteResolution.DeleteDependents, Ct);
        var result = await r.Store.DeleteAsync(table.Element.Id, table.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Equal(queries.Order(StringComparer.Ordinal), refused.Referrers.Select(x => x.FromElementId).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(queries.Order(StringComparer.Ordinal), cleared.Refused.Select(x => x.Id).Distinct().Order(StringComparer.Ordinal)); // a source is required
        Assert.Equal(queries.Order(StringComparer.Ordinal), plan.Deletes.Select(d => d.Id).Order(StringComparer.Ordinal));
        Assert.All(plan.Deletes, d => Assert.Equal(("query", "needs table Invoice register"), (d.Kind, d.Because)));
        var binding = Assert.Single(plan.Removes); // RevenueMonth reads the RevenueByMonth query
        Assert.Equal((RevenueMonthId, "/bindings/0", "binding"), (binding.Id, binding.Pointer, binding.SubKind));
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.All(queries, id => Assert.Null(r.Store.Current!.GetDocument(id)));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task A_batch_delete_takes_the_resolution_and_shares_one_cascade()
    {
        await using var r = WithoutFacts();
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
        Assert.Equal(10, plan.Deletes.Count); // refers to, then Invoice's three relations, overlay, two mappings and the three queries over it
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(12, result.Changes!.Deleted.Count);
        Assert.Equal([DiagramId, RevenueMonthId], result.Changes.Changed.Select(c => c.Id));
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

    [Fact]
    public async Task An_optional_xref_is_cleared_and_a_required_one_refuses_remove_references()
    {
        // next-phase-1-design.md section 4.1: a value an x-ref types is a reference like any other; remove-references clears an
        // optional one and keeps its holder, and refuses a required one.
        await using var r = WithApprovals();

        var plan = await r.Store.GetDeletePlanAsync([ReviewerId], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Saved, plan.Outcome);
        var clear = Assert.Single(plan.Clears);
        Assert.Equal((EditorRepo.InvoiceId, "/properties/reviewer", "reviewer", ReviewerId), (clear.Id, clear.Pointer, clear.Field, clear.Target));
        Assert.Empty(plan.Deletes);

        var reviewer = (await r.Store.GetElementAsync(ReviewerId, Ct))!;
        var cleared = await r.Store.DeleteAsync(ReviewerId, reviewer.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, cleared.Outcome);
        var properties = (await r.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!.Json.GetProperty("properties");
        Assert.False(properties.TryGetProperty("reviewer", out _));
        Assert.Equal(ApproverId, properties.GetProperty("approver").GetString());

        var approver = (await r.Store.GetElementAsync(ApproverId, Ct))!;
        var refused = await r.Store.DeleteAsync(ApproverId, approver.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var d = Assert.Single(refused.Diagnostics, x => x.Rule == "MQ2001");
        Assert.Equal((EditorRepo.InvoiceId, "/properties/approver"), (d.ElementId, d.JsonPointer));
        Assert.Contains("extension:approvals types the link without optional, so remove-references cannot clear it", d.Message, StringComparison.Ordinal);
        Assert.NotNull(await r.Store.GetElementAsync(ApproverId, Ct));
    }

    [Fact]
    public async Task Delete_dependents_removes_a_required_xref_value_and_keeps_its_holder()
    {
        await using var r = WithApprovals();

        var plan = await r.Store.GetDeletePlanAsync([ApproverId], DeleteResolution.DeleteDependents, Ct);
        Assert.Equal(SaveOutcome.Saved, plan.Outcome);
        Assert.Empty(plan.Deletes);
        var clear = Assert.Single(plan.Clears);
        Assert.Equal((EditorRepo.InvoiceId, "/properties/approver"), (clear.Id, clear.Pointer));

        var approver = (await r.Store.GetElementAsync(ApproverId, Ct))!;
        var result = await r.Store.DeleteAsync(ApproverId, approver.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var invoice = (await r.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!;
        var properties = invoice.Json.GetProperty("properties");
        Assert.False(properties.TryGetProperty("approver", out _));
        Assert.Equal(ReviewerId, properties.GetProperty("reviewer").GetString());
        Assert.Equal(3650, properties.GetProperty("retentionDays").GetInt32());
        Assert.Null(await r.Store.GetElementAsync(ApproverId, Ct));
    }

    [Fact]
    public async Task A_resolution_refuses_when_the_extension_requires_the_property()
    {
        // Either resolution refuses when the edited holder would fail a schema that declares the property: its required names the
        // property, or an array drops below minItems. The refusal names the property and the schema.
        await using var r = WithApprovals();
        r.Repo.WriteFile(".maquettiste/extensions/sign-off.json", """
            { "name": "sign-off", "appliesTo": { "kinds": ["entity"], "stereotypes": ["approved"] },
              "properties": { "signedBy": { "type": "string", "x-ref": { "kind": "entity", "optional": true } },
                              "signers": { "type": "array", "minItems": 1, "items": { "type": "string", "x-ref": { "kind": "entity", "optional": true } } } },
              "required": ["signedBy"] }
            """);
        WriteElement(r, ".maquettiste/model/vocabularies/stereotypes/approved.json",
            new JsonObject { ["kind"] = "stereotype", ["id"] = "01K9XRF00000000000000000D1", ["key"] = "approved", ["name"] = "Approved" });
        WriteEntity(r, "Refund", RefundId, "01K9XRF00000000000000000C2", new JsonObject { ["signedBy"] = ApproverId, ["signers"] = new JsonArray(ApproverId) }, "approved");

        foreach (var resolution in new[] { DeleteResolution.RemoveReferences, DeleteResolution.DeleteDependents })
        {
            var plan = await r.Store.GetDeletePlanAsync([ApproverId], resolution, Ct);
            Assert.Equal(SaveOutcome.Invalid, plan.Outcome);
            var refusals = plan.Refused.Where(x => x.Id == RefundId).ToList();
            Assert.Equal(["/properties/signedBy", "/properties/signers/0"], refusals.Select(x => x.Pointer));
            Assert.Contains("its property 'signedBy' (extension:sign-off) is required, so the link cannot be cleared", refusals[0].Why, StringComparison.Ordinal);
            Assert.Contains("its property 'signers' (extension:sign-off) needs at least 1 item", refusals[1].Why, StringComparison.Ordinal);
        }

        var approver = (await r.Store.GetElementAsync(ApproverId, Ct))!;
        var result = await r.Store.DeleteAsync(ApproverId, approver.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var d = Assert.Single(result.Diagnostics, x => x.Rule == "MQ2001" && x.ElementId == RefundId);
        Assert.Contains("'signedBy' (extension:sign-off) is required", d.Message, StringComparison.Ordinal);
        Assert.NotNull(await r.Store.GetElementAsync(ApproverId, Ct));
    }

    [Fact]
    public async Task A_stereotype_parameter_of_refuses_remove_references_and_delete_dependents_removes_it()
    {
        // next-phase-1-design.md section 4.2: a stereotype parameter's x-ref of is a reference that remove-references
        // does not clear, since the parameter would then take a row of any list; delete-dependents removes the of key,
        // and the plan says so.
        await using var r = WithoutFacts();
        WriteEntity(r, "Currency", CurrencyId, "01K9XRF00000000000000000E2");
        const string Path = ".maquettiste/model/vocabularies/stereotypes/priced.json";
        WriteElement(r, Path, new JsonObject
        {
            ["kind"] = "stereotype", ["id"] = PricedId, ["key"] = "priced", ["name"] = "Priced",
            ["parameters"] = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["currency"] = new JsonObject { ["type"] = "string", ["x-ref"] = new JsonObject { ["kind"] = "row", ["of"] = CurrencyId } },
                },
            },
        });
        const string Of = "/parameters/properties/currency/x-ref/of";

        var refuse = await r.Store.GetDeletePlanAsync([CurrencyId], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, refuse.Outcome);
        var refusal = Assert.Single(refuse.Refused);
        Assert.Equal((PricedId, Of), (refusal.Id, refusal.Pointer));
        Assert.Contains("parameter 'currency' takes its values from it (x-ref of)", refusal.Why, StringComparison.Ordinal);
        var currency = (await r.Store.GetElementAsync(CurrencyId, Ct))!;
        var refused = await r.Store.DeleteAsync(CurrencyId, currency.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Contains(refused.Diagnostics, d => d.ElementId == PricedId && d.JsonPointer == Of);
        Assert.NotNull(await r.Store.GetElementAsync(CurrencyId, Ct));

        var cascade = await r.Store.GetDeletePlanAsync([CurrencyId], DeleteResolution.DeleteDependents, Ct);
        Assert.Equal(SaveOutcome.Saved, cascade.Outcome);
        var clear = Assert.Single(cascade.Clears);
        Assert.Equal((PricedId, Of, "of", CurrencyId), (clear.Id, clear.Pointer, clear.Field, clear.Target));
        var result = await r.Store.DeleteAsync(CurrencyId, currency.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Null(await r.Store.GetElementAsync(CurrencyId, Ct));
        var xref = (await r.Store.GetElementAsync(PricedId, Ct))!.Json.GetProperty("parameters").GetProperty("properties").GetProperty("currency")
            .GetProperty("x-ref");
        Assert.Equal("""{"kind":"row"}""", JsonSerializer.Serialize(xref));
        var report = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.ElementId == PricedId && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Remove_references_clears_default_and_exclude_lists()
    {
        // next-phase-2-design.md section 3.1 (D10): remove-references on a stereotype clears its keys from stereotypes, defaultStereotypes
        // and excludeStereotypes lists and the excludes entries naming its members, and drops the seed columns naming an inherited copy
        // with their cells; the delete plan lists each.
        await using var r = WithoutFacts();
        WriteElement(r, ".maquettiste/model/vocabularies/stereotypes/stamped.json", new JsonObject
        {
            ["kind"] = "stereotype", ["id"] = StampedId, ["key"] = "stamped", ["name"] = "Stamped", ["appliesTo"] = new JsonArray("table"),
            ["columns"] = new JsonArray(
                new JsonObject { ["id"] = StampedAtId, ["name"] = "stamped_at", ["type"] = "datetimeoffset" },
                new JsonObject { ["id"] = StampedById, ["name"] = "stamped_by", ["type"] = "string", ["length"] = 40 }),
            ["indexes"] = new JsonArray(new JsonObject { ["id"] = StampedIndexId, ["columns"] = new JsonArray(new JsonObject { ["column"] = StampedById }) }),
        });
        Edit(r, ".maquettiste/model/databases/main/database.json", d => d["schemas"]![0]!["defaultStereotypes"] = new JsonArray("stamped"));
        Edit(r, ".maquettiste/model/packages/catalog.json", p => p["defaultStereotypes"] = new JsonArray("stamped"));
        Edit(r, ".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json", t => t["excludeStereotypes"] = new JsonArray("stamped"));
        Edit(r, ".maquettiste/model/databases/main/tables/notes.json", t => t["excludes"] = new JsonArray(StampedIndexId));
        WriteElement(r, ".maquettiste/model/seeds/notes/notes.json", new JsonObject
        {
            ["kind"] = "seed", ["id"] = NotesSeedId, ["name"] = "Notes", ["target"] = NotesTableId,
            ["columns"] = new JsonArray("01K6BND0000000000000000002", "01K6BND0000000000000000003", "01K6BND0000000000000000004",
                "01K6BND0000000000000000005", NotesTableId + "." + StampedById),
            ["rows"] = new JsonArray(new JsonObject
            {
                ["id"] = "01K9XRF00000000000000000G2",
                ["values"] = new JsonArray("0192f0c4-7f00-7000-8000-000000000001", "invoice", "0192f0c4-7f00-7000-8000-000000000002", "Call back.", "ann"),
            }),
        });

        var plan = await r.Store.GetDeletePlanAsync([StampedId], DeleteResolution.RemoveReferences, Ct);

        Assert.Equal(SaveOutcome.Saved, plan.Outcome);
        Assert.Empty(plan.Refused);
        Assert.Equal(
            new[]
            {
                (EditorRepo.MainDatabaseId, "/schemas/0/defaultStereotypes/0"), ("01J92P0V1T0J6RH4MY9H81NYB4", "/excludeStereotypes/0"),
                (NotesTableId, "/excludes/0"),
            }.Order(),
            plan.Clears.Where(c => c.Id != CatalogId).Select(c => (c.Id, c.Pointer)).Order());
        Assert.Contains(plan.Clears, c => c.Id == CatalogId && c.Pointer == "/defaultStereotypes/0" && c.Field == "defaultStereotypes");
        var seedColumn = Assert.Single(plan.Removes);
        Assert.Equal((NotesSeedId, "/columns/4", "column " + NotesTableId + "." + StampedById, "column"),
            (seedColumn.Id, seedColumn.Pointer, seedColumn.What, seedColumn.SubKind));
        Assert.Contains("column stamped_by of stereotype Stamped", seedColumn.Because, StringComparison.Ordinal);

        var stamped = (await r.Store.GetElementAsync(StampedId, Ct))!;
        var result = await r.Store.DeleteAsync(StampedId, stamped.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var model = r.Store.Current!;
        Assert.All(model.Get<Database>(EditorRepo.MainDatabaseId)!.Schemas, schema => Assert.Empty(schema.DefaultStereotypes));
        Assert.Empty(model.Get<Package>(CatalogId)!.DefaultStereotypes);
        Assert.Empty(model.Get<Table>("01J92P0V1T0J6RH4MY9H81NYB4")!.ExcludeStereotypes ?? []);
        Assert.Empty(model.Get<Table>(NotesTableId)!.Excludes);
        var seed = model.Get<Seed>(NotesSeedId)!;
        Assert.Equal(4, seed.Columns.Count);
        Assert.Equal(4, Assert.Single(seed.Rows).Values.Count);
        var report = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    private const string StampedId = "01K9XRF00000000000000000G0";
    private const string StampedAtId = "01K9XRF00000000000000000G4";
    private const string StampedById = "01K9XRF00000000000000000G5";
    private const string StampedIndexId = "01K9XRF00000000000000000G6";
    private const string NotesSeedId = "01K9XRF00000000000000000G1";
    private const string CatalogId = "01J92P0V025DRFTXKMS240G2NG";

    private static void Edit(EditorRepo r, string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(r.Repo.ReadFile(path))!.AsObject();
        change(node);
        WriteElement(r, path, node);
    }

    /// <summary>
    /// The billing repo (no facts) with two entities, Reviewer and Approver, that Invoice names in custom properties an extension types:
    /// <c>reviewer</c> optional, <c>approver</c> required.
    /// </summary>
    private static EditorRepo WithApprovals()
    {
        var r = WithoutFacts();
        r.Repo.WriteFile(".maquettiste/extensions/approvals.json", """
            { "name": "approvals", "appliesTo": { "kinds": ["entity"] },
              "properties": { "reviewer": { "type": "string", "x-ref": { "kind": "entity", "optional": true } },
                              "approver": { "type": "string", "x-ref": { "kind": "entity" } } } }
            """);
        WriteEntity(r, "Reviewer", ReviewerId, "01K9XRF00000000000000000A2");
        WriteEntity(r, "Approver", ApproverId, "01K9XRF00000000000000000B2");
        var path = ".maquettiste/model/entities/invoice.json";
        var invoice = JsonNode.Parse(r.Repo.ReadFile(path))!.AsObject();
        invoice["properties"]!["reviewer"] = ReviewerId;
        invoice["properties"]!["approver"] = ApproverId;
        WriteElement(r, path, invoice);
        return r;
    }

    private static void WriteEntity(EditorRepo r, string name, string id, string keyId, JsonObject? properties = null, string? stereotype = null)
    {
        var node = new JsonObject
        {
            ["kind"] = "entity", ["id"] = id, ["name"] = name,
            ["key"] = new JsonObject { ["attributes"] = new JsonArray(keyId) },
            ["attributes"] = new JsonArray(new JsonObject { ["id"] = keyId, ["name"] = "id", ["type"] = "uuid", ["required"] = true }),
        };
        if (stereotype is not null)
            node["stereotypes"] = new JsonArray(stereotype);
        if (properties is not null)
            node["properties"] = properties;
        WriteElement(r, ".maquettiste/model/entities/" + ModelBuilder.Kebab(name) + ".json", node);
    }

    private static void WriteElement(EditorRepo r, string path, JsonObject node)
    {
        var schema = KindInfo.TryGet(node["kind"]!.GetValue<string>(), out var info) ? info.SchemaFile : throw new InvalidOperationException();
        r.Repo.WriteBytes(path, TestServices.Json.Write(node, schema, path));
    }

    /// <summary>
    /// The billing repo without the recording of migration facts: these tests check what a cascade deletes and changes, and the drops
    /// it causes would add fact files (pending until acknowledged) to every change set and to validation.
    /// </summary>
    private static EditorRepo WithoutFacts()
    {
        var r = EditorRepo.Create();
        r.EditSettingsOnDisk(s => s["migrations"] = new JsonObject { ["record"] = "off" });
        return r;
    }
}
