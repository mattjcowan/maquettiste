using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The contract of trigger points, part 1 (trigger-points-design.md sections 5 and 6): the schemas of a lifecycle's trigger points, the tree
/// form of <c>old.</c>, a step's assumptions for checks, a create step's values and the new expectations; a model holding them loads, indexes
/// its parts and references, names its trigger points and round-trips; the trace's new members are left out when unused; and the rule catalog
/// holds the trigger point rules.
/// </summary>
public sealed class TriggerPointContractTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, bound to Order.status
    private const string Order = "01JENT00000000000000000001";
    private const string OrderId = "01JATT00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001";
    private const string Draft = "01JSTA00000000000000000001";
    private const string Review = "01JSTA00000000000000000003";
    private const string Submit = "01JTRN00000000000000000001";
    private const string IsLarge = "01JPRX00000000000000000011";
    private const string Title = "01JTGP00000000000000000001"; // a check with a condition reading old.
    private const string Number = "01JTGP00000000000000000002"; // a before action, the team's code
    private const string Stamp = "01JTGP00000000000000000003"; // a with-change action with sets
    private const string Credit = "01JTGP00000000000000000004"; // a warning check, the team's code, outside
    private const string Publish = "01JTGP00000000000000000005"; // an after-commit action, outside
    private const string Archive = "01JTGP00000000000000000006"; // a replace on delete
    private const string StampSet = "01JTGS00000000000000000001";
    private const string ArchiveSet = "01JTGS00000000000000000002";
    private const string Scenario = "01JSCN00000000000000000041";
    private const string CreateStep = "01JSTP00000000000000000041";
    private const string ModifyStep = "01JSTP00000000000000000042";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string ScenarioFile = "model/scenarios/order-lifecycle/trigger-points.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Points = $$"""
        [{"id":"{{Title}}","name":"IdBeforeReview","type":"check","verbs":["promote"],"targets":["{{Review}}"],"states":["{{Draft}}"],
          "condition":{"op":"or","args":[{"op":"isNull","args":[{"path":["{{OrderId}}"],"old":true}]},{"op":"isNotNull","args":[{"path":["{{OrderId}}"]}]}]},
          "description":"An order keeps its id once it has one."},
         {"id":"{{Number}}","name":"AssignNumber","type":"action","timing":"before","verbs":["create"]},
         {"id":"{{Stamp}}","name":"StampSubmit","type":"action","timing":"with-change","verbs":["promote"],"transitions":["{{Submit}}"],
          "sets":[{"id":"{{StampSet}}","attribute":"{{OrderId}}","value":{"null":true} }]},
         {"id":"{{Credit}}","name":"CreditLimit","type":"check","severity":"warning","verbs":["modify"],"attributes":["{{OrderId}}"],"outside":true,
          "displayName":"Credit limit"},
         {"id":"{{Publish}}","name":"PublishToPortal","type":"action","timing":"after-commit","verbs":["promote"],"outside":true},
         {"id":"{{Archive}}","name":"Archive","type":"replace","verbs":["delete"],"entities":["{{Order}}"],
          "sets":[{"id":"{{ArchiveSet}}","attribute":"{{OrderId}}","value":{"null":true} }],"when":{"value":true} }]
        """;

    private static readonly string StepsScenario = $$"""
        {"kind":"scenario","id":"{{Scenario}}","name":"TriggerPoints","process":"{{P}}",
         "steps":[
           {"id":"{{CreateStep}}","input":"verb","verb":"create","actor":"{{Clerk}}","values":{"{{OrderId}}":"9b2e0e8a-50a5-4a4c-8a37-5a5a5e3c7e11"},
            "assume":{"{{IsLarge}}":true,"{{Credit}}":"error"},
            "expect":{"triggerPoints":["{{Number}}"],"checkWarnings":[]} },
           {"id":"{{ModifyStep}}","input":"verb","verb":"modify","actor":"{{Clerk}}","values":{"{{OrderId}}":"9b2e0e8a-50a5-4a4c-8a37-5a5a5e3c7e12"},
            "assume":{"{{Credit}}":"warning"},
            "expect":{"checkWarnings":["{{Credit}}"],"refusedBy":"{{Credit}}","refusal":"check","accepted":false} }]}
        """;

    private static string Canonical(string json, string schema, string modelPath) =>
        Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, schema, ".maquettiste/" + modelPath));

    private static IReadOnlyList<Diagnostic> Evaluate(string schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate(schema, document.RootElement, "model/x.json");
    }

    private static void Valid(string schema, string json) =>
        Assert.True(Evaluate(schema, json) is { Count: 0 }, string.Join("; ", Evaluate(schema, json).Select(d => d.JsonPointer + " " + d.Message)));

    private static void Invalid(string schema, string json, string pointer)
    {
        var found = Evaluate(schema, json);
        Assert.True(found.Any(d => d.Rule == "MQ1002" && (d.JsonPointer ?? "").StartsWith(pointer, StringComparison.Ordinal)),
            pointer + ": " + string.Join("; ", found.Select(d => d.JsonPointer + " " + d.Message)));
    }

    private static string Lifecycle(string points) => $$"""{"kind":"process","id":"{{P}}","name":"Flow","use":"lifecycle","triggerPoints":{{points}}}""";

    private static string Point(string body) => Lifecycle($$"""[{"id":"{{Title}}","name":"T",{{body}}}]""");

    private static IReadOnlyList<NameEntry> Find(ModelSnapshot model, string text)
    {
        Assert.True(QualifiedName.TryParse(text, out var name, out var error), error);
        return model.Names(name.Kind).Find(name.Path);
    }

    // ---- schemas ----

    [Fact]
    public void A_lifecycles_trigger_points_take_the_three_types_and_leave_their_shape_to_MQ8101()
    {
        Valid("process.json", Lifecycle(Points));
        Valid("process.json", Lifecycle("[]"));

        // Keys that don't fit the type pass the schema: MQ8101 reports them, with the entry's id to point at.
        Valid("process.json", Point("""  "type":"check","timing":"before","verbs":["modify"],"sets":[] """));
        Valid("process.json", Point("""  "type":"action","severity":"warning","verbs":["modify"],"condition":{"value":true} """));

        Invalid("process.json", Point("""  "type":"override","verbs":["delete"] """), "/triggerPoints/0/type");
        Invalid("process.json", Point("""  "type":"action","timing":"later","verbs":["delete"] """), "/triggerPoints/0/timing");
        Invalid("process.json", Point("""  "type":"check","severity":"fatal","verbs":["delete"] """), "/triggerPoints/0/severity");
        Invalid("process.json", Point("""  "type":"check","verbs":[] """), "/triggerPoints/0/verbs");
        Invalid("process.json", Point("""  "type":"check","verbs":["modify","modify"] """), "/triggerPoints/0/verbs");
        Invalid("process.json", Point("""  "type":"check","verbs":["Modify"] """), "/triggerPoints/0/verbs");
        Invalid("process.json", Point("""  "type":"check" """), "/triggerPoints/0");
        Invalid("process.json", Point("""  "verbs":["modify"] """), "/triggerPoints/0");
        Invalid("process.json", Lifecycle($$"""[{"id":"{{Title}}","type":"check","verbs":["modify"]}]"""), "/triggerPoints/0");
        Invalid("process.json", Point("""  "type":"check","verbs":["modify"],"outside":"yes" """), "/triggerPoints/0/outside");
        Invalid("process.json", Point("""  "type":"check","verbs":["modify"],"states":["draft"] """), "/triggerPoints/0/states");
        Invalid("process.json", Point($$"""  "type":"check","verbs":["modify"],"entities":["{{Order}}","{{Order}}"] """), "/triggerPoints/0/entities");
        Invalid("process.json", Point("""  "type":"action","timing":"before","verbs":["create"],"sets":[{"attribute":"x","value":{"null":true} }] """), "/triggerPoints/0/sets/0");
        // Part 2's noBypass is shared by every trigger point (decision 20); subjects and exceptEntities are a named one's only (decision 33).
        Valid("process.json", Point("""  "type":"check","verbs":["modify"],"noBypass":true """));
        Invalid("process.json", Point("""  "type":"check","verbs":["modify"],"noBypass":"yes" """), "/triggerPoints/0/noBypass");
        Invalid("process.json", Point($$"""  "type":"check","verbs":["modify"],"subjects":[{"entity":"{{Order}}"}] """), "/triggerPoints/0");
        Invalid("process.json", Point($$"""  "type":"check","verbs":["modify"],"exceptEntities":["{{Order}}"] """), "/triggerPoints/0");
    }

    [Fact]
    public void Old_is_a_flag_on_a_path()
    {
        Valid("process.json", Point($$"""  "type":"check","verbs":["modify"],"condition":{"path":["{{OrderId}}"],"old":true} """));
        Invalid("process.json", Point("""  "type":"check","verbs":["modify"],"condition":{"value":1,"old":true} """), "/triggerPoints/0/condition");
        Invalid("process.json", Point($$"""  "type":"check","verbs":["modify"],"condition":{"path":["{{OrderId}}"],"old":false} """), "/triggerPoints/0/condition");
    }

    [Fact]
    public void A_step_assumes_guards_with_booleans_and_checks_with_words_and_expects_warnings_and_trigger_points()
    {
        Valid("scenario.json", StepsScenario);

        string Step(string step) => $$"""{"kind":"scenario","id":"{{Scenario}}","name":"S","process":"{{P}}","steps":[{"id":"{{CreateStep}}",{{step}}}]}""";
        Valid("scenario.json", Step($$"""  "assume":{"{{IsLarge}}":false,"{{Title}}":"pass"} """));
        Valid("scenario.json", Step("""  "expect":{"accepted":false,"refusal":"check","checkWarnings":[],"triggerPoints":[]} """));
        Valid("scenario.json", Step($$"""  "expect":{"triggerPoints":["{{Number}}","{{Title}}","{{Number}}"]} """));
        Invalid("scenario.json", Step($$"""  "assume":{"{{Title}}":"maybe"} """), "/steps/0/assume");
        Invalid("scenario.json", Step($$"""  "assume":{"{{Title}}":1} """), "/steps/0/assume");
        Invalid("scenario.json", Step($$"""  "assume":{"{{Title}}":null} """), "/steps/0/assume");
        Invalid("scenario.json", Step("""  "expect":{"checkWarnings":["CreditLimit"]} """), "/steps/0/expect/checkWarnings");
        Invalid("scenario.json", Step("""  "expect":{"triggerPoints":"all"} """), "/steps/0/expect/triggerPoints");
        Invalid("scenario.json", Step("""  "input":"verb","verb":"create","values":{"title":"x"} """), "/steps/0/values");
    }

    // ---- loading ----

    [Fact]
    public async Task Trigger_points_and_their_steps_load_index_and_round_trip_in_schema_order()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        process["triggerPoints"] = JsonNode.Parse(Points);
        h.Write(ProcessFile, Canonical(process.ToJsonString(), "process.json", ProcessFile));
        h.Write(ScenarioFile, Canonical(StepsScenario, "scenario.json", ScenarioFile));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var model = store.Current!;

        foreach (var file in new[] { ProcessFile, ScenarioFile })
            Assert.DoesNotContain(model.LoadDiagnostics, d => (d.FilePath ?? "").EndsWith(file, StringComparison.Ordinal) && d.Rule is "MQ1002" or "MQ1003");
        Assert.DoesNotContain(model.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error && !RuleCatalog.IsReplayFinding(d.Rule));

        // The files keep their keys in schema order: the trigger points after the rights, and each entry's own.
        var written = JsonNode.Parse(h.Read(ProcessFile))!.AsObject().Select(p => p.Key).ToList();
        Assert.True(written.IndexOf("actions") < written.IndexOf("triggerPoints") && written.IndexOf("triggerPoints") < written.IndexOf("states"));
        var entries = JsonNode.Parse(h.Read(ProcessFile))!["triggerPoints"]!.AsArray();
        Assert.Equal(["id", "name", "type", "verbs", "states", "targets", "condition", "description"], entries[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "name", "type", "timing", "verbs"], entries[1]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "name", "displayName", "type", "severity", "verbs", "attributes", "outside"], entries[3]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "name", "type", "verbs", "entities", "when", "sets"], entries[5]!.AsObject().Select(p => p.Key));
        Assert.Equal(["path", "old"], entries[0]!["condition"]!["args"]![0]!["args"]![0]!.AsObject().Select(p => p.Key));
        var steps = JsonNode.Parse(h.Read(ScenarioFile))!["steps"]!.AsArray();
        Assert.Equal(["id", "input", "verb", "actor", "values", "assume", "expect"], steps[0]!.AsObject().Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"{{IsLarge}}":true,"{{Credit}}":"error"}"""), steps[0]!["assume"]), steps[0]!["assume"]!.ToJsonString());
        Assert.Equal(["checkWarnings", "triggerPoints"], steps[0]!["expect"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["accepted", "refusal", "refusedBy", "checkWarnings"], steps[1]!["expect"]!.AsObject().Select(p => p.Key));

        // The records.
        var lifecycle = Assert.IsType<Process>(model.GetDocument(P)!.Element);
        Assert.Equal([Title, Number, Stamp, Credit, Publish, Archive], lifecycle.TriggerPoints.Select(t => t.Id));
        var title = lifecycle.TriggerPoints[0];
        Assert.Equal((TriggerPointType.Check, (TriggerTiming?)null, (TriggerSeverity?)null, false), (title.Type, title.Timing, title.Severity, title.TeamCode));
        Assert.Equal(["promote"], title.Verbs);
        Assert.Equal([Draft], title.States);
        Assert.Equal([Review], title.Targets);
        Assert.True(title.Condition!.Args![0].Args![0].Old);
        Assert.Null(title.Condition.Args[1].Args![0].Old);
        Assert.Equal("An order keeps its id once it has one.", title.Description!.Text);
        Assert.Equal((TriggerPointType.Action, TriggerTiming.Before, true), (lifecycle.TriggerPoints[1].Type, lifecycle.TriggerPoints[1].Timing, lifecycle.TriggerPoints[1].TeamCode));
        Assert.Equal([Submit], lifecycle.TriggerPoints[2].Transitions);
        Assert.Equal((StampSet, false), (Assert.Single(lifecycle.TriggerPoints[2].Sets).Id, lifecycle.TriggerPoints[2].TeamCode));
        var credit = lifecycle.TriggerPoints[3];
        Assert.Equal((TriggerSeverity.Warning, true, true, "Credit limit"), (credit.Severity!.Value, credit.Outside, credit.TeamCode, credit.DisplayName));
        Assert.Equal((TriggerTiming.AfterCommit, true), (lifecycle.TriggerPoints[4].Timing!.Value, lifecycle.TriggerPoints[4].Outside));
        Assert.Equal((TriggerPointType.Replace, false), (lifecycle.TriggerPoints[5].Type, lifecycle.TriggerPoints[5].TeamCode));
        Assert.Equal([Order], lifecycle.TriggerPoints[5].Entities);
        var read = Assert.IsType<Maquettiste.Engine.Model.Scenario>(model.GetDocument(Scenario)!.Element).Steps;
        Assert.Equal(((bool?)true, (AssumedCheck?)null), (read[0].Assume[IsLarge].Guard, read[0].Assume[IsLarge].Check));
        Assert.Equal(((bool?)null, (AssumedCheck?)AssumedCheck.Error), (read[0].Assume[Credit].Guard, read[0].Assume[Credit].Check));
        Assert.Equal((StepInput.Verb, "create", OrderId), (read[0].Input, read[0].Verb, Assert.Single(read[0].Values!.Keys)));
        Assert.Equal([Number], read[0].Expect!.TriggerPoints!);
        Assert.Empty(read[0].Expect!.CheckWarnings!);
        Assert.Equal((Refusals.Check, Credit), (read[1].Expect!.Refusal, read[1].Expect!.RefusedBy));
        Assert.Equal([Credit], read[1].Expect!.CheckWarnings!);
        Assert.Null(read[1].Expect!.TriggerPoints);

        // The records write back to the same bytes: nothing is lost, the old flag and the words included.
        foreach (var (path, element, schema) in new[] { (ProcessFile, (object)lifecycle, "process.json"), (ScenarioFile, model.GetDocument(Scenario)!.Element, "scenario.json") })
        {
            var node = JsonSerializer.SerializeToNode(element, element.GetType(), EngineJson.Options)!;
            Assert.Equal(h.Read(path), Encoding.UTF8.GetString(TestServices.Json.Write(node, schema, ".maquettiste/" + path)));
        }

        // The index: every entry of the trigger points is a part of kind process-trigger-point; its sets are sets.
        Assert.Equal(("process-trigger-point", P, "/triggerPoints/0"), (model.Index.Entries[Title].Kind, model.Index.Entries[Title].OwnerId, model.Index.Entries[Title].JsonPointer));
        Assert.Equal(("process-trigger-point", "/triggerPoints/5"), (model.Index.Entries[Archive].Kind, model.Index.Entries[Archive].JsonPointer));
        Assert.Equal(("set", P, "/triggerPoints/2/sets/0"), (model.Index.Entries[StampSet].Kind, model.Index.Entries[StampSet].OwnerId, model.Index.Entries[StampSet].JsonPointer));

        // The ids a trigger point names are references, and so are a step's word keys and expected ids.
        Assert.Contains(await store.GetReferencesAsync(Draft, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Title, "/triggerPoints/0/states/0"));
        Assert.Contains(await store.GetReferencesAsync(Review, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Title, "/triggerPoints/0/targets/0"));
        Assert.Contains(await store.GetReferencesAsync(Submit, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Stamp, "/triggerPoints/2/transitions/0"));
        Assert.Contains(await store.GetReferencesAsync(Order, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Archive, "/triggerPoints/5/entities/0"));
        Assert.Contains(await store.GetReferencesAsync(OrderId, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Credit, "/triggerPoints/3/attributes/0"));
        Assert.Contains(await store.GetReferencesAsync(Number, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (Scenario, CreateStep, "/steps/0/expect/triggerPoints/0"));
        Assert.Contains(await store.GetReferencesAsync(Credit, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (Scenario, ModifyStep, "/steps/1/expect/checkWarnings/0"));

        // A trigger point takes its lifecycle's forms.
        Assert.Equal(Title, Assert.Single(Find(model, "@process-trigger-point:OrderLifecycle.IdBeforeReview")).Id);
        Assert.Equal(Archive, Assert.Single(Find(model, "@process-trigger-point:Sales.OrderLifecycle.Archive")).Id);
    }

    // ---- the trace and the step ----

    [Fact]
    public void The_new_trace_members_are_left_out_when_unused()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var step = new ScenarioStep { Id = ModifyStep, Event = "01JPRX00000000000000000001", Expect = new StepExpectation { Accepted = false } };
        var trace = new StepTrace(0, step, false, Refusals.NoTransition, [], [], [], [Draft], new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(), "2000-01-01T00:00:00Z", false, []);

        var plain = JsonNode.Parse(JsonSerializer.Serialize(trace, web))!.AsObject();
        Assert.False(plain.ContainsKey("triggerPoints") || plain.ContainsKey("checkWarnings"));
        var expect = plain["input"]!["expect"]!.AsObject();
        Assert.False(expect.ContainsKey("checkWarnings") || expect.ContainsKey("triggerPoints"));
        Assert.Equal("{}", plain["input"]!["assume"]!.ToJsonString());

        var refused = trace with
        {
            Input = step with { Assume = new Dictionary<string, StepAssumption> { [IsLarge] = true, [Credit] = AssumedCheck.Warning } },
            Refusal = Refusals.Check,
            RefusedBy = Title,
            TriggerPoints =
            [
                new TriggerPointRun(Number, P, "promote", TriggerPointType.Action, TriggerTiming.Before, TriggerOutcome.Ran),
                new TriggerPointRun(Credit, P, "promote", TriggerPointType.Check, null, TriggerOutcome.Warned) { Assumed = true, Outside = true },
                new TriggerPointRun(Stamp, P, "promote", TriggerPointType.Action, TriggerTiming.WithChange, TriggerOutcome.Ran)
                {
                    Sets = new Dictionary<string, JsonElement> { [OrderId] = JsonDocument.Parse("null").RootElement },
                },
                new TriggerPointRun(Title, P, "promote", TriggerPointType.Check, null, TriggerOutcome.Refused),
            ],
            CheckWarnings = [Credit],
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(refused, web))!.AsObject();
        Assert.Equal((Title, "check"), (json["refusedBy"]!.GetValue<string>(), json["refusal"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            [{"triggerPoint":"{{Number}}","lifecycle":"{{P}}","verb":"promote","type":"action","timing":"before","outcome":"ran"},
             {"triggerPoint":"{{Credit}}","lifecycle":"{{P}}","verb":"promote","type":"check","outcome":"warned","assumed":true,"outside":true},
             {"triggerPoint":"{{Stamp}}","lifecycle":"{{P}}","verb":"promote","type":"action","timing":"with-change","outcome":"ran","sets":{"{{OrderId}}":null} },
             {"triggerPoint":"{{Title}}","lifecycle":"{{P}}","verb":"promote","type":"check","outcome":"refused"}]
            """), json["triggerPoints"]), json["triggerPoints"]!.ToJsonString());
        Assert.Equal($$"""["{{Credit}}"]""", json["checkWarnings"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"{{IsLarge}}":true,"{{Credit}}":"warning"}"""), json["input"]!["assume"]));

        // The engine's list of refusals ends with a trigger point's check, and the schema allows it.
        Assert.Equal(Refusals.Check, Refusals.All[^1]);
        var schema = JsonNode.Parse(TestServices.Schemas.GetFileBytes("scenario.json").Span)!["$defs"]!["step"]!["properties"]!["expect"]!["properties"]!["refusal"]!["enum"]!;
        Assert.Contains("check", schema.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void An_assumption_reads_a_boolean_or_a_word_and_refuses_anything_else()
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, StepAssumption>>("""{"a":true,"b":false,"c":"pass","d":"warning","e":"error"}""", EngineJson.Options)!;
        Assert.Equal([(true, null), (false, null), (null, AssumedCheck.Pass), (null, AssumedCheck.Warning), (null, AssumedCheck.Error)],
            map.Values.Select(v => (v.Guard, v.Check)));
        Assert.Equal([true, false, "pass", "warning", "error"], map.Values.Select(v => v.ToPlain()));
        Assert.Equal("""{"a":true,"b":false,"c":"pass","d":"warning","e":"error"}""", JsonSerializer.Serialize(map, EngineJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StepAssumption>("\"maybe\"", EngineJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StepAssumption>("1", EngineJson.Options));
    }

    // ---- rules ----

    [Fact]
    public void The_rule_catalog_holds_the_trigger_point_rules_in_their_own_family()
    {
        Assert.Equal(
            [("MQ8101", DiagnosticSeverity.Error), ("MQ8102", DiagnosticSeverity.Error), ("MQ8103", DiagnosticSeverity.Error),
             ("MQ8104", DiagnosticSeverity.Error), ("MQ8105", DiagnosticSeverity.Error), ("MQ8106", DiagnosticSeverity.Warning),
             ("MQ8107", DiagnosticSeverity.Warning)],
            RuleCatalog.All.Where(r => r.Id.StartsWith("MQ81", StringComparison.Ordinal)).Select(r => (r.Id, r.DefaultSeverity)));
        Assert.Equal("Trigger points", RuleCatalog.FamilyLabel("MQ81xx"));
        Assert.Equal((DiagnosticSeverity.Error, "refresh-scenario"), (RuleCatalog.Get("MQ9310").DefaultSeverity, RuleCatalog.Get("MQ9310").QuickFix));
        Assert.True(RuleCatalog.IsReplayFinding("MQ9310"));
        Assert.False(RuleCatalog.IsReplayFinding("MQ8101"));
        Assert.Contains("trigger point", RuleCatalog.Get("MQ3001").Description, StringComparison.Ordinal);
        Assert.Contains("trigger point", RuleCatalog.Get("MQ3102").Description, StringComparison.Ordinal);
        Assert.Contains("old.", RuleCatalog.Get("MQ3104").Description, StringComparison.Ordinal);
        Assert.Contains("assume word", RuleCatalog.Get("MQ9305").Description, StringComparison.Ordinal);
        Assert.Contains("trigger points", RuleCatalog.Get("MQ9308").Description, StringComparison.Ordinal);
        Assert.Contains("trigger point", RuleCatalog.Get("MQ9701").Description, StringComparison.Ordinal);
        Assert.Contains("trigger point", RuleCatalog.Get("MQ9803").Description, StringComparison.Ordinal);
        Assert.Equal(RuleCatalog.All.Select(r => r.Id).Order(StringComparer.Ordinal), RuleCatalog.All.Select(r => r.Id));
    }
}
