using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The process batch operations <c>sync-enum</c>, <c>set-lifecycle</c> and <c>set-initial</c> (phase-3-design.md sections 3 and
/// 4.4) over the <c>process-basics</c> fixture, their MQ9019 refusals, the sync dry run, and the quick fixes the catalog carries.
/// </summary>
public sealed class ProcessOperationTests
{
    private const string P = "01JPRC00000000000000000001";
    private const string Order = "01JENT00000000000000000001";
    private const string Status = "01JATT00000000000000000002";
    private const string OrderStatus = "01JENM00000000000000000001";
    private const string Draft = "01JSTA00000000000000000001";
    private const string Review = "01JSTA00000000000000000003";
    private const string Pending = "01JSTA00000000000000000004";
    private const string Escalated = "01JSTA00000000000000000005";
    private const string EnumFile = "model/enums/order-status.json";
    private const string EntityFile = "model/entities/order.json";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string ScenarioFile = "model/scenarios/order-lifecycle/happy-path.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    // Draft and Review swapped, Fulfilment and Closed missing, an extra Archived; Review keeps a code and a description.
    private static void Drift(LoaderHarness h, bool archivedUsed = false)
    {
        var enumNode = JsonNode.Parse(h.Read(EnumFile))!.AsObject();
        enumNode["members"] = new JsonArray(
            new JsonObject { ["id"] = "01JMEM00000000000000000002", ["name"] = "Review", ["code"] = "R", ["description"] = "Under review." },
            new JsonObject { ["id"] = "01JMEM00000000000000000001", ["name"] = "Draft" },
            new JsonObject { ["id"] = "01JMEM00000000000000000009", ["name"] = "Archived" });
        h.Write(EnumFile, enumNode.ToJsonString());
        if (!archivedUsed)
            return;
        var entity = JsonNode.Parse(h.Read(EntityFile))!.AsObject();
        entity["attributes"]![1]!["validation"] = new JsonObject { ["allowedValues"] = new JsonArray("Draft", "Review", "Archived") };
        h.Write(EntityFile, entity.ToJsonString());
    }

    private static ModelBatch Batch(BatchOp op, string id, string? target = null, string? expectedHash = null) =>
        new([new BatchOperation(op, id, expectedHash, null, Target: target)]);

    private static async Task<IReadOnlyList<Diagnostic>> Validate(LoaderHarness h, ModelStore store) =>
        (await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct)).Diagnostics;

    private static Diagnostic Refusal(BatchResult result, int operation = 0)
    {
        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var refusal = Assert.Single(result.Items.SelectMany(i => i.Diagnostics));
        Assert.Equal(("MQ9019", "/operations/" + operation.ToString(System.Globalization.CultureInfo.InvariantCulture)), (refusal.Rule, refusal.JsonPointer));
        return refusal;
    }

    [Fact]
    public async Task The_sync_dry_run_reports_the_change_without_writing()
    {
        var (h, store) = await OpenAsync(x => Drift(x));
        using var _ = h;
        await using var __ = store;
        var before = h.Read(EnumFile);

        var plan = await store.PlanSyncEnumAsync(P, Ct);

        Assert.Null(plan.Problem);
        Assert.Equal(OrderStatus, plan.Enum);
        Assert.Equal(["Fulfilment", "Closed"], plan.Added);
        Assert.Equal(["Archived"], plan.Removed);
        Assert.True(plan.Reordered);
        Assert.Empty(plan.Refused);
        Assert.Equal(before, h.Read(EnumFile));
        Assert.Contains(await Validate(h, store), d => d.Rule == "MQ9203");
    }

    [Fact]
    public async Task Sync_enum_adds_removes_and_reorders_keeping_ids_codes_and_descriptions()
    {
        var (h, store) = await OpenAsync(x => Drift(x));
        using var _ = h;
        await using var __ = store;

        var result = await store.ApplyBatchAsync(Batch(BatchOp.SyncEnum, P), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var members = store.Current!.Get<EnumType>(OrderStatus)!.Members;
        Assert.Equal(["Draft", "Review", "Fulfilment", "Closed"], members.Select(m => m.Name));
        Assert.Equal("01JMEM00000000000000000001", members[0].Id);
        Assert.Equal(("01JMEM00000000000000000002", "R"), (members[1].Id, members[1].Code));
        Assert.NotNull(members[1].Description);
        Assert.DoesNotContain(members, m => m.Id == "01JMEM00000000000000000009");
        Assert.All(members.Skip(2), m => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", m.Id));
        Assert.DoesNotContain(await Validate(h, store), d => d.Rule == "MQ9203");

        var again = await store.PlanSyncEnumAsync(P, Ct);
        Assert.Equal((0, 0, false), (again.Added.Count, again.Removed.Count, again.Reordered));
        var unchanged = await store.ApplyBatchAsync(Batch(BatchOp.SyncEnum, P), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, unchanged.Outcome);
        Assert.Empty(unchanged.Items);
    }

    [Fact]
    public async Task Sync_enum_refuses_to_remove_a_member_in_use_until_the_use_changes()
    {
        var (h, store) = await OpenAsync(x => Drift(x, archivedUsed: true));
        using var _ = h;
        await using var __ = store;
        var before = h.Read(EnumFile);

        var plan = await store.PlanSyncEnumAsync(P, Ct);
        var used = Assert.Single(plan.Refused);
        Assert.Equal(("Archived", Status), (used.Member, Assert.Single(used.ReferencedBy)));

        var refused = Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SyncEnum, P), ChangeSource.Editor, Ct));
        Assert.Contains("'Archived' by 'Order.status'", refused.Message, StringComparison.Ordinal);
        Assert.Matches("[;.] \\S", refused.Message);
        Assert.Equal(before, h.Read(EnumFile));

        var entity = JsonNode.Parse(h.Read(EntityFile))!.AsObject();
        entity["attributes"]![1]!["validation"] = new JsonObject { ["allowedValues"] = new JsonArray("Draft", "Review") };
        var batch = new ModelBatch([
            new BatchOperation(BatchOp.Update, Order, store.Current!.GetDocument(Order)!.Hash, Element(entity)),
            new BatchOperation(BatchOp.SyncEnum, P, null, null)]);
        var enumNode = JsonNode.Parse(h.Read(EnumFile))!.AsObject();
        var enumAndSync = new ModelBatch([
            new BatchOperation(BatchOp.Update, OrderStatus, store.Current!.GetDocument(OrderStatus)!.Hash, Element(enumNode)),
            new BatchOperation(BatchOp.SyncEnum, P, null, null)]);
        Assert.Contains("batch of its own", Refusal(await store.ApplyBatchAsync(enumAndSync, ChangeSource.Editor, Ct), 1).Message, StringComparison.Ordinal);

        // The change of use and the sync in one batch: the sync sees the batch's own update.
        Assert.Equal(SaveOutcome.Saved, (await store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct)).Outcome);
        Assert.DoesNotContain("Archived", h.Read(EnumFile), StringComparison.Ordinal);
        Assert.DoesNotContain(await Validate(h, store), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Sync_enum_plans_against_the_documents_as_the_batch_leaves_them()
    {
        var (h, store) = await OpenAsync(x => Drift(x));
        using var _ = h;
        await using var __ = store;
        Assert.Empty((await store.PlanSyncEnumAsync(P, Ct)).Refused);
        var before = h.Read(EnumFile);

        // Another operation of the batch starts using Archived: the sync must see it and refuse.
        var entity = JsonNode.Parse(h.Read(EntityFile))!.AsObject();
        entity["attributes"]![1]!["validation"] = new JsonObject { ["allowedValues"] = new JsonArray("Draft", "Review", "Archived") };
        var batch = new ModelBatch([
            new BatchOperation(BatchOp.Update, Order, store.Current!.GetDocument(Order)!.Hash, Element(entity)),
            new BatchOperation(BatchOp.SyncEnum, P, null, null)]);

        var refused = Refusal(await store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct), 1);

        Assert.Contains("'Archived' by 'Order.status'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, h.Read(EnumFile));
    }

    [Fact]
    public async Task Sync_enum_counts_scenario_values_of_attributes_typed_by_the_enum_as_uses()
    {
        var (h, store) = await OpenAsync(x =>
        {
            Drift(x);
            var process = JsonNode.Parse(x.Read(ProcessFile))!.AsObject();
            process["context"]!.AsArray().Add(new JsonObject { ["id"] = "01JATT00000000000000000099", ["name"] = "previous", ["type"] = new JsonObject { ["ref"] = OrderStatus } });
            x.Write(ProcessFile, process.ToJsonString());
            var scenario = JsonNode.Parse(x.Read(ScenarioFile))!.AsObject();
            scenario["start"]!["context"]!["01JATT00000000000000000099"] = "Archived";
            x.Write(ScenarioFile, scenario.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        var used = Assert.Single((await store.PlanSyncEnumAsync(P, Ct)).Refused);
        Assert.Equal(("Archived", "01JSCN00000000000000000001"), (used.Member, Assert.Single(used.ReferencedBy)));
        Assert.Contains("scenario 'HappyPath'", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SyncEnum, P), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BatchOp.SyncEnum, Order, null)]
    [InlineData(BatchOp.SyncEnum, P, null)] // process-basics binds status, but drop the binding below
    [InlineData(BatchOp.SetLifecycle, P, null)]
    [InlineData(BatchOp.SetLifecycle, Order, Order)]
    [InlineData(BatchOp.SetInitial, Order, Draft)]
    public async Task Refusals_say_what_to_do(BatchOp op, string id, string? target)
    {
        var (h, store) = await OpenAsync(x =>
        {
            var process = JsonNode.Parse(x.Read(ProcessFile))!.AsObject();
            process.Remove("boundAttribute");
            x.Write(ProcessFile, process.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        var refusal = Refusal(await store.ApplyBatchAsync(Batch(op, id, target), ChangeSource.Editor, Ct));

        Assert.Matches("[;:] \\S.*\\.$", refusal.Message);
    }

    private static System.Text.Json.JsonElement Element(JsonObject node) => System.Text.Json.JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    [Fact]
    public async Task Sync_enum_refuses_a_process_that_binds_no_enum_and_a_stale_hash()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains("is not a process", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SyncEnum, Order), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);
        Assert.Contains("changed since", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SyncEnum, P, expectedHash: new string('0', 64)), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);

        Assert.Equal(SaveOutcome.Saved, (await store.ApplyBatchAsync(Batch(BatchOp.SetLifecycle, Order), ChangeSource.Editor, Ct)).Outcome);
        Assert.NotNull((await store.PlanSyncEnumAsync(P, Ct)).Problem);
        Assert.Contains("binds no attribute", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SyncEnum, P), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_lifecycle_clears_both_sides_and_binds_both_sides_in_one_change()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var cleared = await store.ApplyBatchAsync(Batch(BatchOp.SetLifecycle, Order), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, cleared.Outcome);
        Assert.Equal(2, cleared.Items.Count);
        var process = store.Current!.Get<Process>(P)!;
        Assert.Equal((ProcessUse.Orchestration, null, Order), (process.Use, process.BoundAttribute, process.Subject));
        Assert.Null(store.Current!.Get<Entity>(Order)!.Lifecycle);
        Assert.DoesNotContain("\"use\"", h.Read(ProcessFile), StringComparison.Ordinal);
        Assert.DoesNotContain(await Validate(h, store), d => d.Rule is "MQ9201" or "MQ9202");

        var bound = await store.ApplyBatchAsync(Batch(BatchOp.SetLifecycle, Order, P), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, bound.Outcome);
        Assert.Equal(2, bound.Changes!.Changed.Count);
        process = store.Current!.Get<Process>(P)!;
        Assert.Equal((ProcessUse.Lifecycle, Order), (process.Use, process.Subject));
        Assert.Equal(P, store.Current!.Get<Entity>(Order)!.Lifecycle);
        Assert.DoesNotContain(await Validate(h, store), d => d.Rule == "MQ9201");
    }

    [Fact]
    public async Task Set_lifecycle_moves_the_binding_off_the_previous_partners()
    {
        var (h, store) = await OpenAsync(x =>
        {
            // A second entity, Invoice, whose lifecycle the Order process becomes.
            x.Write("model/entities/invoice.json", """{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"01JENT00000000000000000002","name":"Invoice","package":"01JPKG00000000000000000001","attributes":[{"id":"01JATT00000000000000000091","name":"id","type":"uuid"}]}""");
        });
        using var _ = h;
        await using var __ = store;

        var moved = await store.ApplyBatchAsync(Batch(BatchOp.SetLifecycle, "01JENT00000000000000000002", P), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, moved.Outcome);
        var process = store.Current!.Get<Process>(P)!;
        Assert.Equal(("01JENT00000000000000000002", null), (process.Subject, process.BoundAttribute));
        Assert.Null(store.Current!.Get<Entity>(Order)!.Lifecycle);
        Assert.Equal(P, store.Current!.Get<Entity>("01JENT00000000000000000002")!.Lifecycle);
        Assert.DoesNotContain(await Validate(h, store), d => d.Rule is "MQ9201" or "MQ9202");

        Assert.Contains("is not a process", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SetLifecycle, Order, Order), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);
        Assert.Contains("is not an entity", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SetLifecycle, P, P), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_initial_sets_the_initial_child_of_the_root_or_a_compound_state()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Equal(SaveOutcome.Saved, (await store.ApplyBatchAsync(Batch(BatchOp.SetInitial, Review, Escalated), ChangeSource.Editor, Ct)).Outcome);
        Assert.Equal(Escalated, store.Current!.Get<Process>(P)!.States[2].Initial);
        Assert.Equal(SaveOutcome.Saved, (await store.ApplyBatchAsync(Batch(BatchOp.SetInitial, P, Review), ChangeSource.Editor, Ct)).Outcome);
        Assert.Equal(Review, store.Current!.Get<Process>(P)!.Initial);
        Assert.DoesNotContain(await Validate(h, store), d => d.Rule == "MQ9001");

        var before = h.Read(ProcessFile);
        Assert.Contains("not a direct child", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SetInitial, P, Pending), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);
        Assert.Contains("is not compound", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SetInitial, Draft, Pending), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);
        Assert.Contains("neither a process nor a state", Refusal(await store.ApplyBatchAsync(Batch(BatchOp.SetInitial, Order, Draft), ChangeSource.Editor, Ct)).Message, StringComparison.Ordinal);
        Assert.Equal(before, h.Read(ProcessFile));
    }

    [Fact]
    public async Task Process_operations_are_all_or_nothing_with_the_other_operations()
    {
        var (h, store) = await OpenAsync(x => Drift(x));
        using var _ = h;
        await using var __ = store;
        var before = h.Read(EnumFile);

        var batch = new ModelBatch([
            new BatchOperation(BatchOp.SyncEnum, P, null, null),
            new BatchOperation(BatchOp.SetInitial, P, null, null, Target: Pending)]);
        var result = await store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal("/operations/1", Assert.Single(result.Items.SelectMany(i => i.Diagnostics)).JsonPointer);
        Assert.Equal(before, h.Read(EnumFile));
    }

    [Fact]
    public async Task The_batch_parser_reads_the_process_operations()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes($$"""
            { "operations": [
              { "op": "sync-enum", "id": "{{P}}" },
              { "op": "set-lifecycle", "id": "{{Order}}", "target": "{{P}}" },
              { "op": "set-initial", "id": "{{P}}", "target": "{{Draft}}" } ] }
            """));
        var missingTarget = store.ParseBatch(Encoding.UTF8.GetBytes($$"""{ "operations": [ { "op": "set-initial", "id": "{{P}}" } ] }"""));

        Assert.Empty(parsed.Diagnostics);
        Assert.Equal([BatchOp.SyncEnum, BatchOp.SetLifecycle, BatchOp.SetInitial], parsed.Batch!.Operations.Select(o => o.Op));
        Assert.Equal(P, parsed.Batch.Operations[1].Target);
        Assert.Null(missingTarget.Batch);
    }

    [Fact]
    public void The_catalog_carries_the_quick_fixes_of_the_operations()
    {
        Assert.Equal("sync-enum", RuleCatalog.Get("MQ9203").QuickFix);
        Assert.Null(RuleCatalog.Get("MQ9201").QuickFix); // its lifecycle-without-subject branch has no entity to pass (DESIGN 3)
        Assert.Equal("set-initial", RuleCatalog.Get("MQ9001").QuickFix);
        Assert.Null(RuleCatalog.Get("MQ9003").QuickFix);
        Assert.Equal("sync-enum", RuleCatalog.Describe().Single(r => r.Id == "MQ9203").QuickFix);
        var fixes = RuleCatalog.All.Where(r => r.QuickFix is not null).Select(r => r.QuickFix!).ToHashSet(StringComparer.Ordinal);
        Assert.All(fixes, fix => Assert.Contains(fix, (IReadOnlySet<string>)new HashSet<string>(["sync-enum", "set-lifecycle", "set-initial", "refresh-scenario"], StringComparer.Ordinal)));
        Assert.Equal(DiagnosticSeverity.Error, RuleCatalog.Get("MQ9019").DefaultSeverity);
    }
}
