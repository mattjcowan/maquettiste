using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// State codes under bindings (lifecycle-design.md decisions 11 to 18) over the <c>process-basics</c> fixture, to which the tests add two
/// reference types that share the codes DRF, REV, FUL and CLS, an entity Return, and a lifecycle Fulfil in the subjects form that binds
/// Order.state (OrderState) and Return.state (ReturnState): a code under a reference binding is a use of its row (field <c>code</c>), the
/// code refactors rewrite it and are judged when the batch ends, deleting its row is refused, the index reads it alike by patch and by full
/// build, and a save of a type or seed revisits the lifecycles that bind it.
/// </summary>
public sealed class LifecycleCodeTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Order = "01JENT00000000000000000001";
    private const string Return = "01JENT00000000000000000005";
    private const string OrderState = "01JRTP00000000000000000001";
    private const string ReturnState = "01JRTP00000000000000000011";
    private const string OrderSeed = "01JSDE00000000000000000001";
    private const string ReturnSeed = "01JSDE00000000000000000011";
    private const string OrderStateAttr = "01JATT00000000000000000050";
    private const string ReturnStateAttr = "01JATT00000000000000000052";
    private const string ReturnPhaseAttr = "01JATT00000000000000000053";
    private const string ReturnPhase = "01JENM00000000000000000005";
    private const string F = "01JPRC00000000000000000005";
    private const string Draft = "01JSTA00000000000000000051";
    private const string Review = "01JSTA00000000000000000052";
    private const string Closed = "01JSTA00000000000000000053";
    private const string OrderFile = "model/entities/order.json";
    private const string ReturnFile = "model/entities/return.json";
    private const string FFile = "model/processes/fulfil.json";
    private const string OrderSeedFile = "model/seeds/order-state/order-state.json";

    private static readonly string[] Codes = ["DRF", "REV", "FUL", "CLS"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string OrderRow(int i) => "01JRWS0000000000000000000" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ReturnRow(int i) => "01JRWS0000000000000000001" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string TypeJson(string id, string name) =>
        $$$"""{"$schema":"../../.schema/v1/reference-type.json","kind":"reference-type","id":"{{{id}}}","name":"{{{name}}}","package":"{{{Package}}}","code":{"id":"{{{id[..^1]}}}2","length":8},"label":{"id":"{{{id[..^1]}}}3"}}""";

    private static string SeedJson(string id, string name, string type, Func<int, string> row) =>
        $$"""{"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{id}}","name":"{{name}}","target":"{{type}}","columns":["code","label"],"rows":[{{string.Join(",", Codes.Select((c, i) => $$"""{"id":"{{row(i + 1)}}","values":["{{c}}","{{c.ToLowerInvariant()}}"]}"""))}}]}""";

    private static string FulfilJson(string subjects) => $$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{F}}","name":"Fulfil","package":"{{Package}}","use":"lifecycle","subjects":[{{subjects}}],
         "states":[{"id":"{{Draft}}","name":"Draft","code":"DRF"},{"id":"{{Review}}","name":"Review","code":"REV"},{"id":"{{Closed}}","name":"Closed","code":"CLS","type":"final"}],
         "events":[{"id":"01JSTA00000000000000000054","name":"submit"},{"id":"01JSTA00000000000000000055","name":"close"}],
         "transitions":[{"id":"01JSTA00000000000000000056","source":"{{Draft}}","event":"01JSTA00000000000000000054","targets":["{{Review}}"]},
                        {"id":"01JSTA00000000000000000057","source":"{{Review}}","event":"01JSTA00000000000000000055","targets":["{{Closed}}"]}]}
        """;

    private static string Entry(string id, string entity, string? bound) =>
        $"{{\"id\":\"{id}\",\"entity\":\"{entity}\"" + (bound is null ? "" : $",\"boundAttribute\":\"{bound}\"") + "}";

    /// <summary>
    /// The fixture plus OrderState and ReturnState (rows DRF, REV, FUL, CLS), Order.state (OrderState), Return with state (ReturnState) and
    /// phase (enum ReturnPhase, members Draft, Review, Closed keyed DRF, REV, CLS), and Fulfil listing Order and Return by their state.
    /// </summary>
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null, bool orderState = true)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/reference-types/order-state.json", TypeJson(OrderState, "OrderState"));
        h.Write("model/reference-types/return-state.json", TypeJson(ReturnState, "ReturnState"));
        h.Write(OrderSeedFile, SeedJson(OrderSeed, "OrderState", OrderState, OrderRow));
        h.Write("model/seeds/return-state/return-state.json", SeedJson(ReturnSeed, "ReturnState", ReturnState, ReturnRow));
        h.Write("model/enums/return-phase.json", $$"""{"$schema":"../../.schema/v1/enum.json","kind":"enum","id":"{{ReturnPhase}}","name":"ReturnPhase","package":"{{Package}}","members":[{"id":"01JMEM00000000000000000051","name":"Draft","code":"DRF"},{"id":"01JMEM00000000000000000052","name":"Review","code":"REV"},{"id":"01JMEM00000000000000000053","name":"Closed","code":"CLS"}]}""");
        if (orderState)
        {
            var order = File(h, OrderFile);
            order["attributes"]!.AsArray().Add(new JsonObject { ["id"] = OrderStateAttr, ["name"] = "state", ["type"] = new JsonObject { ["ref"] = OrderState }, ["default"] = "DRF" });
            h.Write(OrderFile, order.ToJsonString());
        }

        h.Write(ReturnFile, $$"""{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Return}}","name":"Return","package":"{{Package}}","attributes":[{"id":"{{ReturnStateAttr}}","name":"state","type":{"ref":"{{ReturnState}}"},"default":"DRF"},{"id":"{{ReturnPhaseAttr}}","name":"phase","type":{"ref":"{{ReturnPhase}}"},"default":"DRF"}]}""");
        h.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000051", Order, orderState ? OrderStateAttr : null) + "," + Entry("01JSBJ00000000000000000052", Return, ReturnStateAttr)));
        prepare?.Invoke(h);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (h, store);
    }

    private static JsonObject File(LoaderHarness h, string path) => JsonNode.Parse(h.Read(path))!.AsObject();

    private static async Task<SaveResult> SaveAsync(ModelStore store, string id, Action<JsonObject> edit)
    {
        var document = store.Current!.GetDocument(id)!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        return await store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
    }

    private static void Saved(SaveResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));

    private static void Applied(BatchResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

    private static async Task<IReadOnlyList<Diagnostic>> FindingsAsync(LoaderHarness h, ModelStore store) =>
        (await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct)).Diagnostics;

    /// <summary>The code references of the lifecycle Fulfil, as (state, row), ordered; the same however the index was built.</summary>
    private static List<(string State, string Row)> CodeUses(ModelSnapshot model) =>
        [.. model.ReferencesFrom(F).Where(r => r.Field == "code").Select(r => (r.FromId, r.ToId)).Order()];

    /// <summary>The same model loaded from disk: a full index build.</summary>
    private static async Task<ModelSnapshot> ReloadAsync(LoaderHarness h)
    {
        var store = new ModelStore(h.Options, h.Services());
        await using var _ = store;
        await store.LoadAsync(Ct);
        return store.Current!;
    }

    private static BatchOperation RenameCode(string type, string code, string to) => new(BatchOp.RenameCode, type, null, null, Code: code, To: to);

    // ---- uses of the row (decision 18) ----

    [Fact]
    public async Task A_code_under_a_reference_binding_is_a_use_of_its_row_in_every_bound_type()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        var use = Assert.Single(model.ReferencesTo(OrderRow(1)), r => r.Field == "code");
        Assert.Equal((F, Draft, "/states/0/code"), (use.FromElementId, use.FromId, use.JsonPointer));
        Assert.Contains(model.ReferencesTo(ReturnRow(1)), r => r.Field == "code" && r.FromId == Draft);
        Assert.Equal(6, CodeUses(model).Count); // three codes in two types; FUL is no state's
        Assert.DoesNotContain(model.ReferencesTo(OrderRow(3)), r => r.Field == "code");
        Assert.DoesNotContain(await FindingsAsync(h, store), d => d.Severity == DiagnosticSeverity.Error && d.Rule.StartsWith("MQ92", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_enum_binding_or_none_adds_no_code_use()
    {
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000052", Return, ReturnPhaseAttr))));
        using var _ = h;
        await using var __ = store;

        Assert.Empty(CodeUses(store.Current!));
        Assert.DoesNotContain(await FindingsAsync(h, store), d => d.Rule.StartsWith("MQ92", StringComparison.Ordinal) && d.Rule is not ("MQ9205" or "MQ9209"));
    }

    [Fact]
    public async Task Deleting_a_row_a_state_code_uses_is_refused()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await SaveAsync(store, OrderSeed, seed => seed["rows"]!.AsArray().RemoveAt(3));

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var finding = Assert.Single(result.Diagnostics, d => d.Rule == "MQ9207");
        Assert.Equal((Closed, "/states/2/code"), (finding.ElementId, finding.JsonPointer));
        Assert.Equal(4, store.Current!.Get<Seed>(OrderSeed)!.Rows.Count);
    }

    [Fact]
    public async Task A_seed_save_revisits_the_lifecycles_that_bind_its_type()
    {
        // The validator's map from a bound attribute's type to the lifecycles (decision 18): a new row no state has is MQ9208 on Fulfil,
        // which references no row of it.
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await SaveAsync(store, OrderSeed, seed => seed["rows"]!.AsArray().Add(new JsonObject { ["id"] = OrderRow(5), ["values"] = new JsonArray("ARC", "archived") }));

        Saved(result);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ9208" && d.FilePath!.EndsWith("fulfil.json", StringComparison.Ordinal) && d.Message.Contains("'ARC'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scalar_type_save_revisits_the_lifecycles_that_bind_it()
    {
        // A plain status through a scalar type (decision 16): shortening the scalar below the codes is MQ9207 on Fulfil, a new error, so the
        // save is refused.
        const string Scalar = "01JSCT00000000000000000005";
        var (h, store) = await OpenAsync(x =>
        {
            x.Write("model/types/state-code.json", $$"""{"$schema":"../../.schema/v1/scalar-type.json","kind":"scalar-type","id":"{{Scalar}}","name":"StateCode","package":"{{Package}}","base":"string","length":8}""");
            var entity = File(x, ReturnFile);
            entity["attributes"]![0]!["type"]!["ref"] = Scalar;
            x.Write(ReturnFile, entity.ToJsonString());
            x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000052", Return, ReturnStateAttr)));
        });
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain(await FindingsAsync(h, store), d => d.Rule is "MQ9202" or "MQ9207");

        var result = await SaveAsync(store, Scalar, scalar => scalar["length"] = 2);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Equal([Draft, Review, Closed], result.Diagnostics.Where(d => d.Rule == "MQ9207").Select(d => d.ElementId));
    }

    // ---- refactors (decisions 16 and 18) ----

    [Fact]
    public async Task Renaming_a_code_two_bound_types_share_is_refused_in_one_type_alone()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var alone = await store.ApplyBatchAsync(new ModelBatch([RenameCode(OrderState, "REV", "RVW")]), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, alone.Outcome);
        var refusal = Assert.Single(alone.Items[0].Diagnostics);
        Assert.Equal(("MQ7020", "/operations/0"), (refusal.Rule, refusal.JsonPointer));
        Assert.Contains("rename the code in 'ReturnState' in this batch as well", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("REV", File(h, FFile)["states"]![1]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Renaming_a_code_in_both_bound_types_in_one_batch_rewrites_the_state()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var both = await store.ApplyBatchAsync(new ModelBatch([RenameCode(OrderState, "REV", "RVW"), RenameCode(ReturnState, "REV", "RVW")]), ChangeSource.Editor, Ct);

        Applied(both);
        Assert.Equal("RVW", File(h, FFile)["states"]![1]!["code"]!.GetValue<string>());
        Assert.Contains(both.Items[0].Usage!.Uses, u => u.FromElementId == F && u.JsonPointer == "/states/1/code");
        Assert.Contains(store.Current!.ReferencesTo(OrderRow(2)), r => r.Field == "code" && r.FromId == Review);
        Assert.Contains(store.Current!.ReferencesTo(ReturnRow(2)), r => r.Field == "code" && r.FromId == Review);
    }

    [Fact]
    public async Task Renaming_a_code_one_bound_type_holds_rewrites_the_state()
    {
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000051", Order, OrderStateAttr))));
        using var _ = h;
        await using var __ = store;

        Applied(await store.ApplyBatchAsync(new ModelBatch([RenameCode(OrderState, "CLS", "END")]), ChangeSource.Editor, Ct));

        Assert.Equal("END", File(h, FFile)["states"]![2]!["code"]!.GetValue<string>());
        Assert.DoesNotContain(await FindingsAsync(h, store), d => d.Severity == DiagnosticSeverity.Error && d.Rule.StartsWith("MQ92", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retiring_a_code_into_another_states_code_is_refused()
    {
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000051", Order, OrderStateAttr))));
        using var _ = h;
        await using var __ = store;

        var result = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.RetireCode, OrderState, null, null, Code: "REV", To: "DRF")]), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Contains("States 'Draft' and 'Review' of process 'Fulfil' would share code 'DRF'", Assert.Single(result.Items[0].Diagnostics).Message, StringComparison.Ordinal);

        // A retire into a code no state has rewrites the state.
        Applied(await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.RetireCode, OrderState, null, null, Code: "REV", To: "FUL")]), ChangeSource.Editor, Ct));
        Assert.Equal("FUL", File(h, FFile)["states"]![1]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Merging_a_bound_type_keeps_the_codes_it_shares_with_the_target()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Applied(await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.MergeReferenceTypes, ReturnState, null, null, Target: OrderState)]), ChangeSource.Editor, Ct));

        Assert.Equal(["DRF", "REV", "CLS"], File(h, FFile)["states"]!.AsArray().Select(s => s!["code"]!.GetValue<string>()));
        Assert.Equal(OrderState, File(h, ReturnFile)["attributes"]![0]!["type"]!["ref"]!.GetValue<string>());
        Assert.Equal([(Draft, OrderRow(1)), (Review, OrderRow(2)), (Closed, OrderRow(4))], CodeUses(store.Current!));
    }

    [Fact]
    public async Task Merging_with_a_code_map_rewrites_the_state()
    {
        var (h, store) = await OpenAsync(x =>
        {
            x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000052", Return, ReturnStateAttr)));
            var seed = File(x, "model/seeds/return-state/return-state.json");
            seed["rows"]![3]!["values"]![0] = "DONE"; // ReturnState holds DONE where OrderState holds CLS
            x.Write("model/seeds/return-state/return-state.json", seed.ToJsonString());
            var fulfil = File(x, FFile);
            fulfil["states"]![2]!["code"] = "DONE";
            x.Write(FFile, fulfil.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        Applied(await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.MergeReferenceTypes, ReturnState, null, null, Target: OrderState,
            Codes: new Dictionary<string, string> { ["DONE"] = "CLS" })]), ChangeSource.Editor, Ct));

        Assert.Equal("CLS", File(h, FFile)["states"]![2]!["code"]!.GetValue<string>());
    }

    // ---- the index, by patch and by full build alike (decision 18) ----

    [Fact]
    public async Task An_attribute_retyped_in_another_file_moves_the_code_uses()
    {
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000051", Order, OrderStateAttr))));
        using var _ = h;
        await using var __ = store;
        Assert.Equal([(Draft, OrderRow(1)), (Review, OrderRow(2)), (Closed, OrderRow(4))], CodeUses(store.Current!));

        Saved(await SaveAsync(store, Order, order => order["attributes"]!.AsArray().Single(a => (string?)a!["id"] == OrderStateAttr)!["type"]!["ref"] = ReturnState));

        Assert.Equal([(Draft, ReturnRow(1)), (Review, ReturnRow(2)), (Closed, ReturnRow(4))], CodeUses(store.Current!));
        Assert.Equal(CodeUses(await ReloadAsync(h)), CodeUses(store.Current!));
        Assert.DoesNotContain(store.Current!.ReferencesTo(OrderRow(1)), r => r.Field == "code");
    }

    [Fact]
    public async Task An_attribute_added_then_bound_has_its_code_uses()
    {
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000051", Order, null))), orderState: false);
        using var _ = h;
        await using var __ = store;

        Saved(await SaveAsync(store, Order, order => order["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = OrderStateAttr, ["name"] = "state", ["type"] = new JsonObject { ["ref"] = OrderState }, ["default"] = "DRF",
        })));
        Assert.True(store.Current!.Index.Patched); // an attribute nothing binds yet: no full build
        Assert.Empty(CodeUses(store.Current!));
        Saved(await SaveAsync(store, F, process => process["subjects"]![0]!["boundAttribute"] = OrderStateAttr));

        Assert.Equal([(Draft, OrderRow(1)), (Review, OrderRow(2)), (Closed, OrderRow(4))], CodeUses(store.Current!));
        Assert.Equal(CodeUses(await ReloadAsync(h)), CodeUses(store.Current!));
    }

    [Fact]
    public async Task An_enum_binding_retyped_to_a_reference_type_has_its_code_uses()
    {
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000052", Return, ReturnPhaseAttr))));
        using var _ = h;
        await using var __ = store;
        Assert.Empty(CodeUses(store.Current!));

        Saved(await SaveAsync(store, Return, entity => entity["attributes"]![1]!["type"]!["ref"] = ReturnState));

        Assert.Equal([(Draft, ReturnRow(1)), (Review, ReturnRow(2)), (Closed, ReturnRow(4))], CodeUses(store.Current!));
        Assert.Equal(CodeUses(await ReloadAsync(h)), CodeUses(store.Current!));
    }

    [Fact]
    public async Task A_model_that_binds_nothing_keeps_patching()
    {
        // Retyping an attribute and editing a process's states are patches when nothing is bound: the fallback costs such a model nothing.
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000051", Order, null))));
        using var _ = h;
        await using var __ = store;
        Saved(await SaveAsync(store, Return, entity => entity["attributes"]![0]!["type"]!["ref"] = OrderState));
        Assert.True(store.Current!.Index.Patched);
        Saved(await SaveAsync(store, F, process => process["states"]![0]!["code"] = "D0"));
        Assert.True(store.Current!.Index.Patched);
    }

    // ---- sync-enum's entity (decision 15) ----

    [Fact]
    public async Task Sync_enum_takes_the_subject_whose_enum_it_syncs()
    {
        // Fulfil binds two enums: Order.status (OrderStatus, which has Fulfilment too) and Return.phase (ReturnPhase, in step).
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000051", Order, "01JATT00000000000000000002") + ","
            + Entry("01JSBJ00000000000000000052", Return, ReturnPhaseAttr))));
        using var _ = h;
        await using var __ = store;

        Assert.Contains("pass the subject whose enum to sync as entity", (await store.PlanSyncEnumAsync(F, Ct)).Problem, StringComparison.Ordinal);
        var phase = await store.PlanSyncEnumAsync(F, Return, Ct);
        Assert.Equal((ReturnPhase, 0, 0), (phase.Enum, phase.Added.Count, phase.Removed.Count));
        var status = await store.PlanSyncEnumAsync(F, Order, Ct);
        Assert.Equal(("01JENM00000000000000000001", "Fulfilment"), (status.Enum, Assert.Single(status.Removed)));
        Assert.Contains("does not list '01JENT00000000000000000003' as a subject", (await store.PlanSyncEnumAsync(F, "01JENT00000000000000000003", Ct)).Problem, StringComparison.Ordinal);

        // Applying it would drift OrderLifecycle, which binds Order.status too (MQ9202 already says so): the batch takes entity, and the save
        // refuses the new MQ9203 on OrderLifecycle.
        var applied = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.SyncEnum, F, null, null, Entity: Order)]), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, applied.Outcome);
        Assert.Contains(applied.Items.SelectMany(i => i.Diagnostics), d => d.Rule == "MQ9203" && d.FilePath!.EndsWith("order-lifecycle.json", StringComparison.Ordinal));
        Assert.Equal(4, File(h, "model/enums/order-status.json")["members"]!.AsArray().Count);
    }

    [Fact]
    public async Task Sync_enum_defaults_to_the_one_enum_binding_among_others()
    {
        // A reference binding beside an enum one: no entity needed, as the MQ9203 quick fix sends none after converting.
        var (h, store) = await OpenAsync(x => x.Write(FFile, FulfilJson(Entry("01JSBJ00000000000000000051", Order, OrderStateAttr) + ","
            + Entry("01JSBJ00000000000000000052", Return, ReturnPhaseAttr))));
        using var _ = h;
        await using var __ = store;

        var plan = await store.PlanSyncEnumAsync(F, Ct);

        Assert.Null(plan.Problem);
        Assert.Equal(ReturnPhase, plan.Enum);
    }

    // ---- the resolved model ----

    [Fact]
    public async Task A_subject_resolves_its_bound_reference_type()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var resolved = ResolutionKit.Resolve(store.Current!);
        var fulfil = resolved.Processes.Single(p => p.Id == F);

        Assert.Equal([OrderState, ReturnState], fulfil.Subjects.Select(s => s.BoundReferenceType!.Id));
        Assert.All(fulfil.Subjects, s => Assert.Null(s.BoundEnum));
        Assert.Null(resolved.Processes.Single(p => p.Id == "01JPRC00000000000000000001").Subjects.Single().BoundReferenceType);
    }
}
