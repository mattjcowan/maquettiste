using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// The contract of revisions (revisions-design.md sections 6 to 8): the schema of an entity's <c>revisions</c> block, a transition's
/// <c>nextRevision</c> and a scenario's revision keys; a model holding them loads into the records and keeps its keys in schema order with
/// nothing added; the step's revision reads a name or a level; the trace's revision and the new refusal; and the rule catalog's Revisions
/// family. Nothing reads the keys yet.
/// </summary>
public sealed class RevisionContractTests
{
    private const string OrderEntity = "01JENT00000000000000000001"; // Order
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle
    private const string RevisionAttribute = "01JATT00000000000000000091";
    private const string ApprovedBy = "01JATT00000000000000000092";
    private const string Delivery = "01JPRC00000000000000000091";
    private const string Shipped = "01JSTA00000000000000000091";
    private const string Documents = "01JEND00000000000000000091";
    private const string Submit = "01JTRN00000000000000000001";
    private const string SubmitEvent = "01JPRX00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001";
    private const string ScenarioId = "01JSCN00000000000000000091";
    private const string OrderObject = "01JXBJ00000000000000000091";
    private const string ReviseStep = "01JSTP00000000000000000091";
    private const string EventStep = "01JSTP00000000000000000092";
    private const string EntityFile = "model/entities/order.json";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string ScenarioFile = "model/scenarios/order-lifecycle/revisions.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The drawing's block of section 6, with the invoice's lists.</summary>
    private static readonly string Block = $$"""
        {"levels":[{"name":"major","format":"letters","skip":["I","O","Q","S","X","Z"]},{"name":"minor","format":"numbers","startEmpty":true}],
         "separator":".","boundAttribute":"{{RevisionAttribute}}",
         "lifecycles":[{"lifecycle":"{{Delivery}}","onRevise":"keep"},{"lifecycle":"{{P}}","onRevise":"{{Shipped}}"}],
         "attributes":[{"attribute":"{{ApprovedBy}}","onRevise":"reset"}],
         "links":[{"end":"{{Documents}}","onRevise":"duplicate"}]}
        """;

    private static string EntityWith(string block) => $$"""{"kind":"entity","id":"{{OrderEntity}}","name":"Order","revisions":{{block}}}""";

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

    // ---- schemas ----

    [Fact]
    public void The_revisions_block_takes_levels_a_separator_a_bound_attribute_and_the_three_lists()
    {
        Valid("entity.json", EntityWith(Block));
        Valid("entity.json", EntityWith("""{"levels":[{"format":"numbers"}]}"""));
        Valid("entity.json", EntityWith($$"""{"attributes":[{"attribute":"{{ApprovedBy}}","onRevise":"clear"}]}"""));
        Valid("entity.json", EntityWith("""{"levels":[{"values":["Prototype","Pilot run","Production"]},{"format":"numbers","first":0,"width":2}],"separator":""}"""));

        // Every sequence of the shared fixture is a block the schema takes.
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Fixtures.Path("revisions", "sequence-cases.json")));
        foreach (var sequence in fixture.RootElement.GetProperty("sequences").EnumerateObject())
            Valid("entity.json", EntityWith(sequence.Value.GetRawText()));

        // A level's shape beyond the schema (both or neither of format and values, skip or width on the wrong format, a first that is not
        // a value) is MQ8301's, which names the level.
        Valid("entity.json", EntityWith("""{"levels":[{"format":"letters","values":["x"],"width":2,"first":3}]}"""));
        Valid("entity.json", EntityWith("""{"levels":[{"name":"minor"}]}"""));

        Invalid("entity.json", EntityWith("{}"), "/revisions");
        Invalid("entity.json", EntityWith("""{"levels":[]}"""), "/revisions/levels");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"letters"},{"format":"numbers"},{"format":"letters"},{"format":"numbers"}]}"""), "/revisions/levels");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"roman"}]}"""), "/revisions/levels/0/format");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"letters","last":"Z"}]}"""), "/revisions/levels/0");
        Invalid("entity.json", EntityWith("""{"levels":[{"name":"1st","format":"letters"}]}"""), "/revisions/levels/0/name");
        Invalid("entity.json", EntityWith("""{"levels":[{"values":[]}]}"""), "/revisions/levels/0/values");
        Invalid("entity.json", EntityWith("""{"levels":[{"values":["A","A"]}]}"""), "/revisions/levels/0/values");
        Invalid("entity.json", EntityWith("""{"levels":[{"values":[""]}]}"""), "/revisions/levels/0/values");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"numbers","first":-1}]}"""), "/revisions/levels/0/first");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"letters","skip":["i"]}]}"""), "/revisions/levels/0/skip");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"letters","skip":["IO"]}]}"""), "/revisions/levels/0/skip");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"letters","skip":["I","I"]}]}"""), "/revisions/levels/0/skip");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"numbers","width":0}]}"""), "/revisions/levels/0/width");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"numbers","width":10}]}"""), "/revisions/levels/0/width");
        Invalid("entity.json", EntityWith("""{"levels":[{"format":"numbers","startEmpty":"yes"}]}"""), "/revisions/levels/0/startEmpty");
        Invalid("entity.json", EntityWith("""{"separator":1}"""), "/revisions/separator");
        Invalid("entity.json", EntityWith("""{"boundAttribute":"revision"}"""), "/revisions/boundAttribute");
        Invalid("entity.json", EntityWith($$"""{"lifecycles":[{"lifecycle":"{{Delivery}}","onRevise":"Restart"}]}"""), "/revisions/lifecycles/0/onRevise");
        Invalid("entity.json", EntityWith($$"""{"lifecycles":[{"lifecycle":"{{Delivery}}"}]}"""), "/revisions/lifecycles/0");
        Invalid("entity.json", EntityWith($$"""{"attributes":[{"attribute":"{{ApprovedBy}}","onRevise":"keep"}]}"""), "/revisions/attributes/0/onRevise");
        Invalid("entity.json", EntityWith($$"""{"links":[{"end":"{{Documents}}","onRevise":"float"}]}"""), "/revisions/links/0/onRevise");
        Invalid("entity.json", EntityWith("""{"colour":"red"}"""), "/revisions");
    }

    [Fact]
    public void A_transition_names_a_level_and_a_scenario_says_and_expects_revisions()
    {
        string Process(string transition) => $$"""
            {"kind":"process","id":"{{P}}","name":"OrderLifecycle","use":"lifecycle","states":[{"id":"01JSTA00000000000000000001","name":"Draft"}],
             "transitions":[{"id":"{{Submit}}","source":"01JSTA00000000000000000001","targets":["01JSTA00000000000000000001"]{{transition}}}]}
            """;
        foreach (var level in new[] { 1, 2, 3 })
            Valid("process.json", Process($$""","nextRevision":{{level}}"""));
        Invalid("process.json", Process(""","nextRevision":0"""), "/transitions/0/nextRevision");
        Invalid("process.json", Process(""","nextRevision":4"""), "/transitions/0/nextRevision");
        Invalid("process.json", Process(""","nextRevision":"major" """), "/transitions/0/nextRevision");

        string ScenarioWith(string objectKeys, string step) => $$"""
            {"kind":"scenario","id":"{{ScenarioId}}","name":"Revisions","process":"{{P}}",
             "objects":[{"id":"{{OrderObject}}","name":"order","entity":"{{OrderEntity}}"{{objectKeys}}}],"subject":"{{OrderObject}}",
             "steps":[{"id":"{{ReviseStep}}",{{step}}}]}
            """;
        Valid("scenario.json", ScenarioWith(""","revision":"A.2" """, """ "input":"verb","verb":"revise","revision":2,"expect":{"revision":"A.3"} """));
        Valid("scenario.json", ScenarioWith(""","revision":"" """, """ "input":"verb","verb":"revise","revision":"C" """));
        Valid("scenario.json", ScenarioWith("", $$""" "event":"{{SubmitEvent}}","revision":"B","expect":{"accepted":false,"refusal":"revision","refusedBy":"{{OrderEntity}}"} """));
        Invalid("scenario.json", ScenarioWith(""","revision":2""", """ "input":"verb","verb":"revise" """), "/objects/0/revision");
        Invalid("scenario.json", ScenarioWith("", """ "input":"verb","verb":"revise","revision":4 """), "/steps/0/revision");
        Invalid("scenario.json", ScenarioWith("", """ "input":"verb","verb":"revise","revision":0 """), "/steps/0/revision");
        Invalid("scenario.json", ScenarioWith("", """ "input":"verb","verb":"revise","revision":true """), "/steps/0/revision");
        Invalid("scenario.json", ScenarioWith("", """ "input":"verb","verb":"revise","expect":{"revision":3} """), "/steps/0/expect/revision");
        Invalid("scenario.json", ScenarioWith("", """ "input":"verb","verb":"revise","expect":{"accepted":false,"refusal":"sequence"} """), "/steps/0/expect/refusal");
    }

    // ---- writing and loading ----

    [Fact]
    public void Canonical_writing_keeps_the_schema_order_and_adds_nothing()
    {
        // Keys given out of order come back in the schema's order: revisions after attributes and before bindings, the block's own keys,
        // a level's, and each entry's; a separator equal to the usual one, and startEmpty false, stay as written, since no new key has a
        // schema default.
        var shuffled = $$"""
            {"bindings":[],"revisions":{"links":[{"onRevise":"move","end":"{{Documents}}"}],"separator":".","levels":[
               {"startEmpty":false,"skip":["I"],"first":"B","format":"letters","name":"major"}]},
             "attributes":[{"id":"{{RevisionAttribute}}","name":"revision","type":"string"}],"name":"Order","id":"{{OrderEntity}}","kind":"entity"}
            """;
        var written = JsonNode.Parse(Canonical(shuffled, "entity.json", EntityFile))!.AsObject();
        Assert.Equal(["$schema", "kind", "id", "name", "attributes", "revisions"], written.Select(p => p.Key));
        var block = written["revisions"]!.AsObject();
        Assert.Equal(["levels", "separator", "links"], block.Select(p => p.Key));
        Assert.Equal(["name", "format", "first", "skip", "startEmpty"], block["levels"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["end", "onRevise"], block["links"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(".", block["separator"]!.GetValue<string>());
        Assert.False(block["levels"]![0]!["startEmpty"]!.GetValue<bool>());

        // A block of only levels gains no lists, and an entity without a block gains none.
        var bare = JsonNode.Parse(Canonical(EntityWith("""{"levels":[{"format":"numbers"}]}"""), "entity.json", EntityFile))!["revisions"]!.AsObject();
        Assert.Equal(["levels"], bare.Select(p => p.Key));
        Assert.Equal(["format"], bare["levels"]![0]!.AsObject().Select(p => p.Key));
        var none = JsonNode.Parse(Canonical($$"""{"kind":"entity","id":"{{OrderEntity}}","name":"Order"}""", "entity.json", EntityFile))!.AsObject();
        Assert.False(none.ContainsKey("revisions"));
    }

    [Fact]
    public void The_records_read_the_block_and_write_it_back_unchanged()
    {
        var json = Canonical(EntityWith(Block), "entity.json", EntityFile);
        var entity = JsonSerializer.Deserialize<Entity>(json, EngineJson.Options)!;
        var revisions = entity.Revisions!;

        Assert.Equal(2, revisions.Levels!.Count);
        var major = revisions.Levels[0];
        Assert.Equal("major", major.Name);
        Assert.Equal(RevisionFormat.Letters, major.Format);
        Assert.Null(major.StartEmpty);
        Assert.Null(major.Values);
        Assert.Null(major.FirstText);
        Assert.Equal(["I", "O", "Q", "S", "X", "Z"], major.Skip!);
        Assert.Equal(RevisionFormat.Numbers, revisions.Levels[1].Format);
        Assert.True(revisions.Levels[1].StartEmpty);
        Assert.Equal(".", revisions.Separator);
        Assert.Equal(RevisionAttribute, revisions.BoundAttribute);
        Assert.Equal([(Delivery, RevisionLifecycleEntry.Keep), (P, Shipped)], revisions.Lifecycles!.Select(l => (l.Lifecycle, l.OnRevise)));
        Assert.Equal([(ApprovedBy, AttributeOnRevise.Reset)], revisions.Attributes!.Select(a => (a.Attribute, a.OnRevise)));
        Assert.Equal([(Documents, LinkOnRevise.Duplicate)], revisions.Links!.Select(l => (l.End, l.OnRevise)));

        // first as written: a letter run for letters, an integer's digits for numbers; values as a list.
        var levels = JsonSerializer.Deserialize<EntityRevisions>(
            """{"levels":[{"values":["Prototype","Pilot run"]},{"format":"numbers","first":0,"width":2},{"format":"letters","first":"AA"}]}""", EngineJson.Options)!.Levels!;
        Assert.Null(levels[0].Format);
        Assert.Equal(["Prototype", "Pilot run"], levels[0].Values!);
        Assert.Equal("0", levels[1].FirstText);
        Assert.Equal(2, levels[1].Width);
        Assert.Equal("AA", levels[2].FirstText);

        // Back to the file: the record writes the same canonical text.
        var node = JsonSerializer.SerializeToNode(entity, EngineJson.Options)!;
        Assert.Equal(json, Encoding.UTF8.GetString(TestServices.Json.Write(node, "entity.json", ".maquettiste/" + EntityFile)));
        var plain = JsonSerializer.Deserialize<Entity>($$"""{"kind":"entity","id":"{{OrderEntity}}","name":"Order"}""", EngineJson.Options)!;
        Assert.Null(plain.Revisions);
        Assert.False(JsonSerializer.SerializeToNode(plain, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject().ContainsKey("revisions"));
    }

    [Fact]
    public async Task A_model_holding_the_keys_loads_them_and_keeps_its_files_in_schema_order()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        var entity = JsonNode.Parse(h.Read(EntityFile))!.AsObject();
        entity["revisions"] = JsonNode.Parse(Block);
        h.Write(EntityFile, Canonical(entity.ToJsonString(), "entity.json", EntityFile));
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        process["transitions"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == Submit)!["nextRevision"] = 2;
        h.Write(ProcessFile, Canonical(process.ToJsonString(), "process.json", ProcessFile));
        h.Write(ScenarioFile, Canonical($$"""
            {"kind":"scenario","id":"{{ScenarioId}}","name":"Revisions","process":"{{P}}",
             "objects":[{"id":"{{OrderObject}}","name":"order","entity":"{{OrderEntity}}","states":[],"revision":"A.2"}],"subject":"{{OrderObject}}",
             "steps":[{"id":"{{ReviseStep}}","input":"verb","verb":"revise","actor":"{{Clerk}}","revision":1,"expect":{"values":{},"revision":"B"} },
                      {"id":"{{EventStep}}","event":"{{SubmitEvent}}","actor":"{{Clerk}}","revision":"D",
                       "expect":{"accepted":false,"refusal":"revision","refusedBy":"{{OrderEntity}}"} }]}
            """, "scenario.json", ScenarioFile));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var model = store.Current!;

        foreach (var file in new[] { EntityFile, ProcessFile, ScenarioFile })
            Assert.DoesNotContain(model.LoadDiagnostics, d => (d.FilePath ?? "").EndsWith(file, StringComparison.Ordinal) && d.Rule is "MQ1001" or "MQ1002" or "MQ1003");

        var order = Assert.IsType<Entity>(model.GetDocument(OrderEntity)!.Element);
        Assert.Equal(RevisionAttribute, order.Revisions!.BoundAttribute);
        var lifecycle = Assert.IsType<Process>(model.GetDocument(P)!.Element);
        Assert.Equal(2, lifecycle.Transitions.Single(t => t.Id == Submit).NextRevision);
        Assert.All(lifecycle.Transitions.Where(t => t.Id != Submit), t => Assert.Null(t.NextRevision));
        var scenario = Assert.IsType<Scenario>(model.GetDocument(ScenarioId)!.Element);
        Assert.Equal("A.2", scenario.Objects.Single().Revision);
        Assert.Equal(RevisionChoice.FromLevel(1), scenario.Steps[0].Revision);
        Assert.Equal("B", scenario.Steps[0].Expect!.Revision);
        Assert.Equal(RevisionChoice.FromName("D"), scenario.Steps[1].Revision);
        Assert.Equal(Refusals.Revision, scenario.Steps[1].Expect!.Refusal);

        // The files keep their keys in schema order.
        Assert.Equal(["$schema", "kind", "id", "name", "package", "lifecycle", "key", "attributes", "revisions"],
            JsonNode.Parse(h.Read(EntityFile))!.AsObject().Select(p => p.Key));
        var transition = JsonNode.Parse(h.Read(ProcessFile))!["transitions"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == Submit)!.AsObject();
        var keys = transition.Select(p => p.Key).ToList();
        Assert.Equal(keys.IndexOf("targets") + 1, keys.IndexOf("nextRevision"));
        var written = JsonNode.Parse(h.Read(ScenarioFile))!;
        Assert.Equal(["id", "name", "entity", "revision"], written["objects"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "input", "verb", "actor", "revision", "expect"], written["steps"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["revision"], written["steps"]![0]!["expect"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "event", "actor", "revision", "expect"], written["steps"]![1]!.AsObject().Select(p => p.Key));
    }

    // ---- the step's revision, the trace and the refusal ----

    [Fact]
    public void A_steps_revision_is_a_chosen_name_or_a_level()
    {
        var name = JsonSerializer.Deserialize<ScenarioStep>($$"""{"id":"{{ReviseStep}}","revision":"B.1"}""", EngineJson.Options)!.Revision!.Value;
        var level = JsonSerializer.Deserialize<ScenarioStep>($$"""{"id":"{{ReviseStep}}","revision":2}""", EngineJson.Options)!.Revision!.Value;
        Assert.Equal("B.1", name.Name);
        Assert.Null(name.Level);
        Assert.Null(level.Name);
        Assert.Equal(2, level.Level);
        Assert.Equal<object>("B.1", name.ToPlain());
        Assert.Equal<object>(2, level.ToPlain());
        Assert.Equal("\"B.1\"", JsonSerializer.Serialize(name, EngineJson.Options));
        Assert.Equal("2", JsonSerializer.Serialize(level, EngineJson.Options));
        Assert.Equal("\"\"", JsonSerializer.Serialize(RevisionChoice.FromName(""), EngineJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioStep>($$"""{"id":"{{ReviseStep}}","revision":true}""", EngineJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScenarioStep>($$"""{"id":"{{ReviseStep}}","revision":1.5}""", EngineJson.Options));
    }

    [Fact]
    public void The_trace_gives_the_revision_moved_and_leaves_the_new_members_out_when_unused()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var step = new ScenarioStep { Id = ReviseStep, Input = StepInput.Verb, Verb = "revise", Expect = new StepExpectation() };
        var trace = new StepTrace(0, step, true, null, [], [], [], [], new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(),
            "2000-01-01T00:00:00Z", false, []);

        var plain = JsonNode.Parse(JsonSerializer.Serialize(trace, web))!.AsObject();
        Assert.False(plain.ContainsKey("revision"));
        Assert.False(plain["input"]!.AsObject().ContainsKey("revision"));
        Assert.False(plain["input"]!["expect"]!.AsObject().ContainsKey("revision"));
        var transition = new ProcessTransition { Id = Submit, Source = "01JSTA00000000000000000001" };
        Assert.False(JsonNode.Parse(JsonSerializer.Serialize(transition, web))!.AsObject().ContainsKey("nextRevision"));
        var obj = new ScenarioObject { Id = OrderObject, Name = "order", Entity = OrderEntity };
        Assert.False(JsonNode.Parse(JsonSerializer.Serialize(obj, web))!.AsObject().ContainsKey("revision"));

        // A revise at a level, and a chosen name, whose level is written as null.
        var atLevel = trace with { Input = step with { Revision = RevisionChoice.FromLevel(2) }, Revision = new RevisionChange("A", "A.1", 2) };
        var json = JsonNode.Parse(JsonSerializer.Serialize(atLevel, web))!.AsObject();
        Assert.Equal("""{"from":"A","to":"A.1","level":2}""", json["revision"]!.ToJsonString());
        Assert.Equal(2, json["input"]!["revision"]!.GetValue<int>());
        var chosen = trace with { Revision = new RevisionChange("", "C", null) };
        Assert.Equal("""{"from":"","to":"C","level":null}""", JsonNode.Parse(JsonSerializer.Serialize(chosen, web))!["revision"]!.ToJsonString());
        Assert.Equal("""{"from":"","to":"C","level":null}""", JsonSerializer.Serialize(chosen.Revision, EngineJson.Options));

        // The new refusal, after the gate's own and before a trigger point's check, and the schema allows it.
        Assert.Equal("revision", Refusals.Revision);
        Assert.Equal([Refusals.GateReason, Refusals.Revision, Refusals.Check], Refusals.All.TakeLast(3));
        var schema = JsonNode.Parse(TestServices.Schemas.GetFileBytes("scenario.json").Span)!["$defs"]!["step"]!["properties"]!["expect"]!["properties"]!["refusal"]!["enum"]!;
        Assert.Equal(Refusals.All.Order(StringComparer.Ordinal), schema.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    // ---- rules ----

    [Fact]
    public void The_rule_catalog_holds_the_revision_rules_in_their_own_family()
    {
        var family = RuleCatalog.All.Where(r => r.Id.StartsWith("MQ83", StringComparison.Ordinal)).ToList();
        Assert.Equal(["MQ8301", "MQ8303", "MQ8305", "MQ8306", "MQ8308", "MQ8312"], family.Select(r => r.Id));
        Assert.All(family, r => Assert.Equal(DiagnosticSeverity.Error, r.DefaultSeverity));
        Assert.Equal("Revisions", RuleCatalog.FamilyLabel("MQ83xx"));
        Assert.Equal((DiagnosticSeverity.Error, "refresh-scenario"), (RuleCatalog.Get("MQ9312").DefaultSeverity, RuleCatalog.Get("MQ9312").QuickFix));
        Assert.True(RuleCatalog.IsReplayFinding("MQ9312"));
        Assert.False(RuleCatalog.IsReplayFinding("MQ8301"));
        Assert.Contains("startEmpty", RuleCatalog.Get("MQ8301").Description, StringComparison.Ordinal);
        Assert.Contains("separator", RuleCatalog.Get("MQ8303").Description, StringComparison.Ordinal);
        Assert.Contains("boundAttribute", RuleCatalog.Get("MQ8305").Description, StringComparison.Ordinal);
        Assert.Contains("nextRevision", RuleCatalog.Get("MQ8306").Description, StringComparison.Ordinal);
        Assert.Contains("max 1", RuleCatalog.Get("MQ8308").Description, StringComparison.Ordinal);
        Assert.Contains("key attributes", RuleCatalog.Get("MQ8312").Description, StringComparison.Ordinal);
        Assert.Contains("expect.revision", RuleCatalog.Get("MQ9312").Description, StringComparison.Ordinal);
        Assert.Contains("revisions block", RuleCatalog.Get("MQ2020").Description, StringComparison.Ordinal);
        Assert.Contains("only revising moves", RuleCatalog.Get("MQ9802").Description, StringComparison.Ordinal);
        Assert.Contains("expect.revision", RuleCatalog.Get("MQ9305").Description, StringComparison.Ordinal);
        Assert.Contains("revise with end", RuleCatalog.Get("MQ9305").Description, StringComparison.Ordinal);
        Assert.Equal(RuleCatalog.All.Select(r => r.Id).Order(StringComparer.Ordinal), RuleCatalog.All.Select(r => r.Id));
    }
}
