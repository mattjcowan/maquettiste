using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Trigger points in the model's bookkeeping (trigger-points-design.md section 9): own verbs, entities, attributes and set attributes a point
/// names are references, and so are a step's word keys; deletes drop an entry from a narrowing list but take the point with its last one, take
/// a set with its attribute, and drop the point's word entries and expected ids from scenarios; an expression that names what goes refuses
/// remove-references; rename-verb and remove-verb follow trigger points; index rows count trigger points and carry their verbs and replaces by
/// patch and by full build; trigger points localize their display name and description.
/// </summary>
public sealed class TriggerPointReferenceTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle in the process-basics fixture
    private const string Order = "01JENT00000000000000000001";
    private const string OrderId = "01JATT00000000000000000001";
    private const string IsLarge = "01JPRX00000000000000000011"; // a guard
    private const string Clerk = "01JACT00000000000000000001";
    private const string Record = "01JTRE00000000000000000001"; // a base of Order
    private const string Title = "01JTRA00000000000000000001"; // Record's attribute
    private const string Rush = "01JTRE00000000000000000002"; // derived from Order
    private const string Vocabulary = "01JTRV00000000000000000001";
    private const string Escalate = "01JTRV00000000000000000002";
    private const string RushOnly = "01JTRP00000000000000000001";
    private const string TwoTypes = "01JTRP00000000000000000002";
    private const string TitleWatch = "01JTRP00000000000000000003";
    private const string IdOrTitle = "01JTRP00000000000000000004";
    private const string Stamp = "01JTRP00000000000000000005";
    private const string Escalated = "01JTRP00000000000000000006";
    private const string OnlyEscalate = "01JTRP00000000000000000007";
    private const string TitleSet = "01JTRS00000000000000000001";
    private const string IdSet = "01JTRS00000000000000000002";
    private const string Scenario = "01JSCN00000000000000000071";
    private const string ProcessFile = "model/processes/order-lifecycle.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Points = $$"""
        [{"id":"{{RushOnly}}","name":"RushOnly","type":"check","verbs":["modify"],"entities":["{{Rush}}"]},
         {"id":"{{TwoTypes}}","name":"TwoTypes","type":"check","verbs":["modify"],"entities":["{{Order}}","{{Rush}}"]},
         {"id":"{{TitleWatch}}","name":"TitleWatch","displayName":"Title watch","type":"check","severity":"warning","verbs":["modify"],
          "attributes":["{{Title}}"],"description":"The title changed."},
         {"id":"{{IdOrTitle}}","name":"IdOrTitle","type":"check","verbs":["read"],"attributes":["{{Title}}","{{OrderId}}"]},
         {"id":"{{Stamp}}","name":"Stamp","type":"action","timing":"with-change","verbs":["promote"],
          "sets":[{"id":"{{TitleSet}}","attribute":"{{Title}}","value":{"null":true} },{"id":"{{IdSet}}","attribute":"{{OrderId}}","value":{"null":true} }]},
         {"id":"{{Escalated}}","name":"Escalated","type":"check","verbs":["escalate","modify"]},
         {"id":"{{OnlyEscalate}}","name":"OnlyEscalate","type":"check","verbs":["escalate"]}]
        """;

    private static readonly string Steps = $$"""
        {"kind":"scenario","id":"{{Scenario}}","name":"TriggerBookkeeping","process":"{{P}}",
         "steps":[
           {"id":"01JSTP00000000000000000071","input":"verb","verb":"modify","actor":"{{Clerk}}",
            "assume":{"{{IsLarge}}":true,"{{RushOnly}}":"error"},
            "expect":{"checkWarnings":["{{TitleWatch}}"],"triggerPoints":["{{RushOnly}}","{{TwoTypes}}"]} },
           {"id":"01JSTP00000000000000000072","input":"verb","verb":"modify","actor":"{{Clerk}}",
            "assume":{"{{OnlyEscalate}}":"pass"},
            "expect":{"triggerPoints":["{{OnlyEscalate}}"]} }]}
        """;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? points = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/entities/record.json", Canonical($$"""
            {"kind":"entity","id":"{{Record}}","name":"Record","attributes":[{"id":"{{Title}}","name":"title","type":"string","length":80}]}
            """, "entity.json", "model/entities/record.json"));
        h.Write("model/entities/rush.json", Canonical($$"""{"kind":"entity","id":"{{Rush}}","name":"Rush","base":"{{Order}}"}""", "entity.json", "model/entities/rush.json"));
        var order = JsonNode.Parse(h.Read("model/entities/order.json"))!.AsObject();
        order["base"] = Record;
        h.Write("model/entities/order.json", Canonical(order.ToJsonString(), "entity.json", "model/entities/order.json"));
        h.Write("model/vocabularies/verbs.json", Canonical($$"""
            {"kind":"verb-vocabulary","id":"{{Vocabulary}}","name":"verbs","verbs":[{"id":"{{Escalate}}","key":"escalate"}]}
            """, "verb-vocabulary.json", "model/vocabularies/verbs.json"));
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        process["triggerPoints"] = JsonNode.Parse(points ?? Points);
        h.Write(ProcessFile, Canonical(process.ToJsonString(), "process.json", ProcessFile));
        h.Write("model/scenarios/order-lifecycle/trigger-bookkeeping.json",
            Canonical(Steps, "scenario.json", "model/scenarios/order-lifecycle/trigger-bookkeeping.json"));
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        return (h, store);
    }

    private static string Canonical(string json, string schema, string modelPath) =>
        Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, schema, ".maquettiste/" + modelPath));

    private static async Task<BatchResult> ApplyAsync(ModelStore store, string operation)
    {
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }

    private static IReadOnlyList<ProcessTriggerPoint> PointsOf(ModelStore store) => store.Current!.Get<Process>(P)!.TriggerPoints;

    private static IReadOnlyList<ScenarioStep> StepsOf(ModelStore store) => store.Current!.Get<Maquettiste.Engine.Model.Scenario>(Scenario)!.Steps;

    private static void Saved(SaveResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));

    [Fact]
    public async Task What_a_trigger_point_names_and_a_steps_word_keys_are_references()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Contains(await store.GetReferencesAsync(Escalate, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Escalated, "/triggerPoints/5/verbs/0"));
        Assert.Contains(await store.GetReferencesAsync(Rush, Ct), r => (r.FromId, r.JsonPointer) == (RushOnly, "/triggerPoints/0/entities/0"));
        Assert.Contains(await store.GetReferencesAsync(Title, Ct), r => (r.FromId, r.JsonPointer) == (TitleSet, "/triggerPoints/4/sets/0/attribute"));
        Assert.Contains(await store.GetReferencesAsync(RushOnly, Ct), r => (r.FromElementId, r.JsonPointer) == (Scenario, $"/steps/0/assume/{RushOnly}"));
        // A boolean key stays a reference to its guard, as today.
        Assert.Contains(await store.GetReferencesAsync(IsLarge, Ct), r => (r.FromElementId, r.JsonPointer) == (Scenario, $"/steps/0/assume/{IsLarge}"));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task The_last_entity_takes_its_trigger_point_and_the_points_word_entries_and_expected_ids_with_it(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        Assert.NotEqual(SaveOutcome.Saved, (await store.DeleteAsync(Rush, store.Current!.GetDocument(Rush)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct)).Outcome);

        var plan = await store.GetDeletePlanAsync([Rush], resolution, Ct);
        Saved(await store.DeleteAsync(Rush, store.Current!.GetDocument(Rush)!.Hash, resolution, ChangeSource.Editor, Ct));

        // An empty entities list would mean every type: RushOnly goes, and TwoTypes keeps Order.
        Assert.DoesNotContain(PointsOf(store), p => p.Id == RushOnly);
        Assert.Equal([Order], PointsOf(store).Single(p => p.Id == TwoTypes).Entities);
        // The step loses the word entry for RushOnly and its expected id, and keeps the guard's boolean entry.
        var step = StepsOf(store)[0];
        Assert.Equal([IsLarge], step.Assume.Keys);
        Assert.Equal([TwoTypes], step.Expect!.TriggerPoints!);
        var json = JsonSerializer.Serialize(plan, EngineJson.Options);
        Assert.Contains("\"what\":\"trigger point RushOnly\"", json, StringComparison.Ordinal);
        Assert.Contains("names no other entity than entity Rush", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task An_attribute_goes_from_a_points_list_with_its_set_and_its_last_entry_takes_the_point(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Saved(await store.DeleteAsync(Record, store.Current!.GetDocument(Record)!.Hash, resolution, ChangeSource.Editor, Ct));

        var points = PointsOf(store);
        Assert.DoesNotContain(points, p => p.Id == TitleWatch);
        Assert.Equal([OrderId], points.Single(p => p.Id == IdOrTitle).Attributes);
        Assert.Equal([IdSet], points.Single(p => p.Id == Stamp).Sets.Select(s => s.Id));
        Assert.Empty(StepsOf(store)[0].Expect!.CheckWarnings!);
    }

    [Fact]
    public async Task An_expression_that_names_what_goes_refuses_remove_references_and_delete_dependents_takes_the_point()
    {
        const string Watch = "01JTRP00000000000000000011";
        var (h, store) = await OpenAsync($$"""
            [{"id":"{{Watch}}","name":"TitleGiven","type":"check","verbs":["modify"],"condition":{"op":"isNotNull","args":[{"path":["{{Title}}"]}]} },
             {"id":"{{TwoTypes}}","name":"TwoTypes","type":"check","verbs":["modify"]}]
            """);
        using var _ = h;
        await using var __ = store;

        var refused = await store.GetDeletePlanAsync([Record], DeleteResolution.RemoveReferences, Ct);
        Assert.Contains("delete-dependents to remove the trigger point that holds it", JsonSerializer.Serialize(refused, EngineJson.Options), StringComparison.Ordinal);

        Saved(await store.DeleteAsync(Record, store.Current!.GetDocument(Record)!.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct));
        Assert.Equal([TwoTypes], PointsOf(store).Select(p => p.Id));
    }

    [Fact]
    public async Task Rename_verb_renames_and_merges_in_trigger_points()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(store, """{"op":"rename-verb","verb":"escalate","to":"raise"}""")).Outcome);
        Assert.Equal(["raise", "modify"], PointsOf(store).Single(p => p.Id == Escalated).Verbs);
        Assert.Equal(["raise"], PointsOf(store).Single(p => p.Id == OnlyEscalate).Verbs);

        // Into a built-in verb the point already names: the two merge into one entry.
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(store, """{"op":"rename-verb","verb":"raise","to":"modify"}""")).Outcome);
        Assert.Equal(["modify"], PointsOf(store).Single(p => p.Id == Escalated).Verbs);
    }

    [Fact]
    public async Task Remove_verb_clears_it_from_trigger_points_and_a_point_whose_last_verb_goes_goes_with_its_step_entries()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await ApplyAsync(store, """{"op":"remove-verb","verb":"escalate"}""");

        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(["modify"], PointsOf(store).Single(p => p.Id == Escalated).Verbs);
        Assert.DoesNotContain(PointsOf(store), p => p.Id == OnlyEscalate);
        var step = StepsOf(store)[1];
        Assert.Empty(step.Assume);
        Assert.Empty(step.Expect!.TriggerPoints!);
        Assert.Equal([IsLarge, RushOnly], StepsOf(store)[0].Assume.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ2001" && d.Message.Contains(OnlyEscalate, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Index_rows_count_trigger_points_and_carry_their_verbs_and_replaces_by_patch_and_by_full_build()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var row = store.Current!.Index.Summaries.Single(s => s.Id == P);
        Assert.Equal(7, row.TriggerPoints);
        Assert.Equal(["escalate", "modify", "promote", "read"], row.TriggerVerbs!);
        Assert.Null(row.Replaces);
        Assert.Null(row.ReplaceScopes);
        Assert.True(row.UsesTriggerPoints);
        Assert.Contains("escalate", row.Verbs!);
        Assert.Contains("read", row.Verbs!);
        var text = JsonSerializer.Serialize(row, EngineJson.Options);
        Assert.Contains("\"triggerPoints\":7", text, StringComparison.Ordinal);
        Assert.Contains("\"triggerVerbs\":[\"escalate\",\"modify\",\"promote\",\"read\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("replaceScopes", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usesTriggerPoints", text, StringComparison.OrdinalIgnoreCase);

        // A save that adds two replaces: the patched row equals a full build's.
        var current = (await store.GetElementAsync(P, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        node["triggerPoints"]!.AsArray().Add(JsonNode.Parse("""{"id":"01JTRP00000000000000000021","name":"Archive","type":"replace","verbs":["delete"],"entities":["01JTRE00000000000000000002","01JENT00000000000000000001"]}"""));
        node["triggerPoints"]!.AsArray().Add(JsonNode.Parse("""{"id":"01JTRP00000000000000000022","name":"Keep","type":"replace","verbs":["disconnect","copy"]}"""));
        Saved(await store.SaveAsync(P, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct));
        Assert.True(store.Current!.Index.Patched);
        var patched = store.Current!.Index.Summaries.Single(s => s.Id == P);
        Assert.Equal(9, patched.TriggerPoints);
        Assert.Equal(["copy", "delete", "disconnect"], patched.Replaces!);
        Assert.Equal([$"copy,disconnect|", $"delete|{Order},{Rush}"], patched.ReplaceScopes!);
        Assert.Contains("\"replaces\":[\"copy\",\"delete\",\"disconnect\"]", JsonSerializer.Serialize(patched, EngineJson.Options), StringComparison.Ordinal);

        var fresh = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await using var ___ = fresh;
        await fresh.LoadAsync(Ct);
        Assert.Equal(patched, fresh.Current!.Index.Summaries.Single(s => s.Id == P));
    }

    [Fact]
    public async Task A_project_without_trigger_points_keeps_its_index_rows()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var row = store.Current!.Index.Summaries.Single(s => s.Id == P);
        Assert.Equal((null, null, null, null, false), (row.TriggerPoints, row.TriggerVerbs, row.Replaces, row.ReplaceScopes, row.UsesTriggerPoints));
        var text = JsonSerializer.Serialize(row, EngineJson.Options);
        Assert.DoesNotContain("\"triggerPoints\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"triggerVerbs\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"replaces\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trigger_points_localize_their_display_name_and_description()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var l10n = store.Current!.Localization;

        Assert.Equal(("process-trigger-point", "Title watch", "The title changed."),
            (l10n.Nodes[TitleWatch].Kind, l10n.Nodes[TitleWatch].DisplayName, l10n.Nodes[TitleWatch].Description));
        Assert.Equal("RushOnly", l10n.Nodes[RushOnly].DisplayName);
        Assert.Contains("process-trigger-point", (IReadOnlySet<string>)Maquettiste.Engine.Localization.LocaleChains.NodeKinds);
    }
}
