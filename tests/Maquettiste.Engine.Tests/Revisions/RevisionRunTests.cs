using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// Revisions in simulation, scenarios and recording (revisions-design.md decisions 19, 22 and 37 to 40, sections 9 and 13), over an invented
/// drawing: letters then numbers, the revision held in <c>revision</c>, <c>approvedBy</c> reset on a revise. Its lifecycle moves In Work,
/// Approved, Released; release names promote and gives the next letter (a label), and revise names revise and moves Released back to In Work at
/// the minor level. A second lifecycle, Delivery (Waiting, Shipped), may govern it, and approve may then need it Waiting.
/// </summary>
public sealed class RevisionRunTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Drawing = "01JRVC00000000000000000001";
    private const string RevisionAttr = "01JRVC00000000000000000003";
    private const string ApprovedBy = "01JRVC00000000000000000004";
    private const string Trail = "01JRVC00000000000000000005";
    private const string Life = "01JRVP00000000000000000001";
    private const string Delivery = "01JRVP00000000000000000002";
    private const string Approve = "01JRVE00000000000000000001";
    private const string Release = "01JRVE00000000000000000002";
    private const string ReviseEvent = "01JRVE00000000000000000003";
    private const string InWork = "01JRVS00000000000000000001";
    private const string Approved = "01JRVS00000000000000000002";
    private const string Released = "01JRVS00000000000000000003";
    private const string Waiting = "01JRVS00000000000000000011";
    private const string Shipped = "01JRVS00000000000000000012";

    private const string Levels = """
        "levels":[{"name":"major","format":"letters","skip":["I","O","Q","S","X","Z"]},{"name":"minor","format":"numbers","startEmpty":true}]
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Block(string lifecycles = "") =>
        $$"""{{{Levels}},"boundAttribute":"{{RevisionAttr}}","attributes":[{"attribute":"{{ApprovedBy}}","onRevise":"reset"}]{{lifecycles}}}""";

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? block, bool nextRevision = true, bool releasedFinal = false,
        bool delivery = false)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/entities/drawing.json", $$"""
            {"kind":"entity","id":"{{Drawing}}","name":"Drawing","package":"{{Package}}","key":{"attributes":["01JRVC00000000000000000002"]},
             "attributes":[{"id":"01JRVC00000000000000000002","name":"id","type":"uuid"},{"id":"{{RevisionAttr}}","name":"revision","type":"string","length":10},
               {"id":"{{ApprovedBy}}","name":"approvedBy","type":"string","length":40},{"id":"{{Trail}}","name":"trail","type":"string","length":40}]
             {{(block is null ? "" : ",\"revisions\":" + block)}}}
            """);
        var guard = delivery ? $$""","guards":[{"id":"01JRVG00000000000000000001","name":"waiting","condition":{"inState":["{{Waiting}}"]} }]""" : "";
        var next1 = nextRevision ? ",\"nextRevision\":1" : "";
        var next2 = nextRevision ? ",\"nextRevision\":2" : "";
        var final = releasedFinal ? ",\"type\":\"final\"" : "";
        var guarded = delivery ? ",\"guard\":\"01JRVG00000000000000000001\"" : "";
        var reviseMove = releasedFinal ? "" : $$""",{"id":"01JRVT00000000000000000003","source":"{{Released}}","event":"{{ReviseEvent}}","targets":["{{InWork}}"]{{next2}} }""";
        h.Write("model/processes/drawing-life.json", $$"""
            {"kind":"process","id":"{{Life}}","name":"DrawingLife","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JRVB00000000000000000001","entity":"{{Drawing}}"}],
             "events":[{"id":"{{Approve}}","name":"approve","verb":"promote"},{"id":"{{Release}}","name":"release","verb":"promote"},
               {"id":"{{ReviseEvent}}","name":"revise","verb":"revise"}],
             "actions":[{"id":"01JRVA00000000000000000001","name":"markEntry","sets":[{"id":"01JRVF00000000000000000001","attribute":"{{Trail}}",
               "value":{"op":"concat","args":[{"path":["{{Trail}}"]},{"value":"i"}]} }]}]{{guard}},
             "states":[{"id":"{{InWork}}","name":"InWork","entry":["01JRVA00000000000000000001"]},{"id":"{{Approved}}","name":"Approved"},
               {"id":"{{Released}}","name":"Released"{{final}} }],
             "initial":"{{InWork}}",
             "transitions":[{"id":"01JRVT00000000000000000001","source":"{{InWork}}","event":"{{Approve}}","targets":["{{Approved}}"]{{guarded}} },
               {"id":"01JRVT00000000000000000002","source":"{{Approved}}","event":"{{Release}}","targets":["{{Released}}"]{{next1}} }
               {{reviseMove}}]}
            """);
        if (delivery)
        {
            h.Write("model/processes/delivery.json", $$"""
                {"kind":"process","id":"{{Delivery}}","name":"Delivery","package":"{{Package}}","use":"lifecycle",
                 "subjects":[{"id":"01JRVB00000000000000000002","entity":"{{Drawing}}"}],
                 "states":[{"id":"{{Waiting}}","name":"Waiting"},{"id":"{{Shipped}}","name":"Shipped"}],"initial":"{{Waiting}}"}
                """);
        }

        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (h, store);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement[] Objects(string extra = "") =>
        [Json($$"""{"id":"d1","name":"drawing","entity":"{{Drawing}}","values":{"{{ApprovedBy}}":"kim","{{Trail}}":"-"}{{extra}}}""")];

    private static JsonElement Event(string ev, string extra = "") => Json($$"""{"event":"{{ev}}"{{extra}}}""");

    private static JsonElement Verb(string verb, string extra = "") => Json($$"""{"input":"verb","verb":"{{verb}}"{{extra}}}""");

    private static async Task<IReadOnlyList<StepTrace>> SimulateAsync(ModelStore store, JsonElement[] steps, JsonElement[]? objects = null)
    {
        var call = await store.SimulateProcessAsync(Life, new SimulateRequest { Steps = steps, Objects = objects ?? Objects(), Subject = "d1" }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!.Trace;
    }

    private static string Notes(StepTrace trace) => string.Join(" ", trace.Notes ?? []);

    private static (string From, string To, int? Level)? Moved(StepTrace trace) => trace.Revision is { } r ? (r.From, r.To, r.Level) : null;

    [Fact]
    public async Task The_drawing_model_validates_without_errors()
    {
        var (h, store) = await OpenAsync(Block(), delivery: true);
        using var _ = h;
        await using var __ = store;
        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    // Section 17's acceptance: released twice by a promote that gives the next letter, frozen on release and revised while frozen, revised from
    // Released into In Work at the minor level, and a chosen name refused for going back.
    [Fact]
    public async Task Releases_label_the_next_letter_and_a_revise_move_and_a_verb_step_make_new_revisions()
    {
        var (h, store) = await OpenAsync(Block());
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store,
        [
            Event(Approve), Event(Release), Verb("freeze"), // 1 to 3: A becomes B on release, then frozen
            Event(ReviseEvent), // 4: B.1, thawed, approvedBy reset, In Work because the move goes there
            Event(Approve), Event(Release), // 5, 6: B.1 becomes C
            Verb("revise", ",\"revision\":\"B.5\""), // 7: going back is refused
            Verb("revise", ",\"revision\":\"E\""), // 8: skipping ahead is a chosen name; the lifecycle starts over
        ]);

        Assert.Null(Moved(t[1]));
        Assert.Equal(("A", "B", 1), Moved(t[2]));
        Assert.Null(t[2].ValuesChanged); // a label carries nothing over, and the bound attribute is no set
        Assert.True(t[3].Frozen);
        Assert.Equal(("B", "B.1", 2), Moved(t[4]));
        Assert.False(t[4].Frozen);
        Assert.Equal([InWork], t[4].Configuration);
        Assert.Equal(JsonValueKind.Null, t[4].ValuesChanged![ApprovedBy].ValueKind);
        Assert.False(t[4].ValuesChanged!.ContainsKey(RevisionAttr));
        Assert.Contains("revised from 'B' to 'B.1': lifecycle 'DrawingLife' went where the move goes. The earlier revision stays in 'Released' of 'DrawingLife'.",
            Notes(t[4]), StringComparison.Ordinal);
        Assert.Equal(("B.1", "C", 1), Moved(t[6]));
        Assert.False(t[7].Accepted);
        Assert.Equal((Refusals.Revision, Drawing), (t[7].Refusal, t[7].RefusedBy));
        Assert.Null(Moved(t[7]));
        Assert.Equal(("C", "E", null), Moved(t[8]));
        Assert.Equal([Released], t[7].Configuration);
        Assert.Equal([InWork], t[8].Configuration);
        var restart = Assert.Single(t[8].Microsteps);
        Assert.Empty(restart.Transitions);
        Assert.Equal([Released], restart.Exited);
        Assert.Equal([InWork], restart.Entered);
        Assert.Contains("lifecycle 'DrawingLife' started over", Notes(t[8]), StringComparison.Ordinal);
        Assert.Contains("\"revision\":{\"from\":\"C\",\"to\":\"E\",\"level\":null}", JsonSerializer.Serialize(t[8], EngineJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revise_starts_an_ended_chart_again_and_keep_leaves_it_ended_but_accepted()
    {
        JsonElement[] steps = [Event(Approve), Event(Release), Verb("revise", ",\"revision\":2")];
        var (h, store) = await OpenAsync(Block(), releasedFinal: true);
        using var _ = h;
        await using var __ = store;
        var restarted = await SimulateAsync(store, steps);
        Assert.True(restarted[2].Final);
        Assert.True(restarted[3].Accepted);
        Assert.False(restarted[3].Final);
        Assert.Equal([InWork], restarted[3].Configuration);
        Assert.Equal("-ii", restarted[3].ValuesChanged![Trail].GetString());

        var (h2, keeping) = await OpenAsync(Block($$""","lifecycles":[{"lifecycle":"{{Life}}","onRevise":"keep"}]"""), releasedFinal: true);
        using var ___ = h2;
        await using var ____ = keeping;
        var kept = await SimulateAsync(keeping, steps);
        Assert.True(kept[3].Accepted);
        Assert.True(kept[3].Final);
        Assert.Empty(kept[3].Microsteps);
        Assert.Equal(("B", "B.1", 2), Moved(kept[3]));
        Assert.Contains("lifecycle 'DrawingLife' kept its states", Notes(kept[3]), StringComparison.Ordinal);

        var (h3, named) = await OpenAsync(Block($$""","lifecycles":[{"lifecycle":"{{Life}}","onRevise":"{{Approved}}"}]"""), releasedFinal: true);
        using var _____ = h3;
        await using var ______ = named;
        var toState = await SimulateAsync(named, steps);
        Assert.Equal([Approved], toState[3].Configuration);
        Assert.False(toState[3].Final);
    }

    // Section 9's other lifecycles: Delivery restarts by the fallback, so a guard's in_state reads Waiting after the revise; with keep it stays
    // Shipped.
    [Fact]
    public async Task Other_lifecycles_take_their_new_states_in_a_guard_after_the_revise()
    {
        JsonElement[] steps = [Event(Approve), Verb("revise"), Event(Approve)];
        JsonElement[] shipped = Objects($$""","states":["{{Shipped}}"]""");
        var (h, store) = await OpenAsync(Block(), delivery: true);
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store, steps, shipped);
        Assert.Equal((false, Refusals.Guard), (t[1].Accepted, t[1].Refusal));
        Assert.Equal(("A", "B", 1), Moved(t[2]));
        Assert.Contains("lifecycle 'Delivery' started over", Notes(t[2]), StringComparison.Ordinal);
        Assert.Contains("'Shipped' of 'Delivery'", Notes(t[2]), StringComparison.Ordinal);
        Assert.True(t[3].Accepted);

        var (h2, keeping) = await OpenAsync(Block($$""","lifecycles":[{"lifecycle":"{{Delivery}}","onRevise":"keep"}]"""), delivery: true);
        using var ___ = h2;
        await using var ____ = keeping;
        var kept = await SimulateAsync(keeping, steps, shipped);
        Assert.Contains("lifecycle 'Delivery' kept its states", Notes(kept[2]), StringComparison.Ordinal);
        Assert.False(kept[3].Accepted);
    }

    // Decision 22: a list at its end refuses a revise step and a revise move with the refusal revision, by the entity whose block holds the
    // sequence; with no subject object, a revise and a label only say so.
    [Fact]
    public async Task A_list_at_its_end_refuses_with_revision_and_a_run_with_no_object_only_notes_it()
    {
        var (h, store) = await OpenAsync("""{"levels":[{"values":["P","Q"]}]}""");
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store, [Event(Approve), Event(Release), Verb("revise"), Event(ReviseEvent)]);
        Assert.Equal(("P", "Q", 1), Moved(t[2]));
        Assert.Equal((false, Refusals.Revision, Drawing), (t[3].Accepted, t[3].Refusal, t[3].RefusedBy));
        Assert.Equal((false, Refusals.Revision, Drawing), (t[4].Accepted, t[4].Refusal, t[4].RefusedBy));
        Assert.Equal([Released], t[4].Configuration);

        var call = await store.SimulateProcessAsync(Life, new SimulateRequest { Steps = [Event(Approve), Event(Release), Verb("revise")] }, Ct);
        var none = call.Value!.Trace;
        Assert.All(none, trace => Assert.Null(trace.Revision));
        Assert.Contains("the move gave no revision", Notes(none[2]), StringComparison.Ordinal);
        Assert.Contains("the revise moved no revision", Notes(none[3]), StringComparison.Ordinal);
        Assert.Contains("did not apply it", Notes(none[3]), StringComparison.Ordinal);
    }

    // Section 13: a project without a block runs revise steps and revise events exactly as before.
    [Fact]
    public async Task A_project_without_a_block_checks_and_records_a_revise_step_and_a_revise_event_only_moves()
    {
        var (h, store) = await OpenAsync(null, nextRevision: false);
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store, [Event(Approve), Event(Release), Verb("revise"), Event(ReviseEvent)]);
        Assert.All(t, trace => Assert.Null(trace.Revision));
        Assert.All(t, trace => Assert.Null(trace.RevisionAfter));
        Assert.Equal(["The run checked verb 'revise' and recorded it, but did not apply it."], t[3].Notes);
        Assert.Equal([Released], t[3].Configuration);
        Assert.Null(t[3].ValuesChanged);
        Assert.Equal([InWork], t[4].Configuration);
        Assert.Null(t[4].Notes);
        Assert.Equal("-ii", t[4].ValuesChanged![Trail].GetString());
        Assert.DoesNotContain("\"revision\"", string.Join("", t.Select(x => JsonSerializer.Serialize(x, EngineJson.Options))), StringComparison.Ordinal);
    }

    private static Scenario Scenario(string steps, string objectExtra = "")
    {
        var numbered = System.Text.Json.Nodes.JsonNode.Parse("[" + steps + "]")!.AsArray();
        for (var i = 0; i < numbered.Count; i++)
            numbered[i]!["id"] = "01JRVY0000000000000000000" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Parse(numbered.ToJsonString()[1..^1], objectExtra);
    }

    private static Scenario Parse(string steps, string objectExtra) => JsonSerializer.Deserialize<Scenario>($$"""
        {"kind":"scenario","id":"01JRVZ00000000000000000001","name":"drawing","process":"{{Life}}","subject":"d1",
         "objects":[{"id":"d1","name":"drawing","entity":"{{Drawing}}","values":{"{{ApprovedBy}}":"kim","{{Trail}}":"-"}{{objectExtra}}}],
         "steps":[{{steps}}]}
        """, EngineJson.Options)!;

    [Fact]
    public async Task Scenario_revisions_that_do_not_fit_are_MQ9305_and_a_different_revision_is_MQ9312()
    {
        var (h, store) = await OpenAsync(Block());
        using var _ = h;
        await using var __ = store;
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        string? Problem(string steps, string objectExtra = "") =>
            ScenarioReplayer.Replay(Scenario(steps, objectExtra), runtime)!.Diagnostics.FirstOrDefault(d => d.Rule == "MQ9305")?.Message;

        Assert.Contains("beside verb 'modify'", Problem("""{"input":"verb","verb":"modify","revision":1}"""), StringComparison.Ordinal);
        Assert.Contains("an event step's revision is a chosen name", Problem($$"""{"event":"{{Release}}","revision":1}"""), StringComparison.Ordinal);
        Assert.Contains("event 'approve' takes no move that revises", Problem($$"""{"event":"{{Approve}}","revision":"B"}"""), StringComparison.Ordinal);
        Assert.Contains("names revision level 3, but 'Drawing' has 2 levels", Problem("""{"input":"verb","verb":"revise","revision":3}"""), StringComparison.Ordinal);
        Assert.Contains("names revision 'I', which the revision sequence of 'Drawing' can't form", Problem("""{"input":"verb","verb":"revise","revision":"I"}"""),
            StringComparison.Ordinal);
        Assert.Contains("holds its revision in an attribute", Problem($$"""{"event":"{{Approve}}"}""", ",\"revision\":\"B\""), StringComparison.Ordinal);
        Assert.Contains("gives the revision \"I\"", Problem($$"""{"event":"{{Approve}}"}""", $$""","values":{"{{RevisionAttr}}":"I"}"""), StringComparison.Ordinal);
        Assert.Null(Problem($$"""{"event":"{{Approve}}"}""", $$""","values":{"{{RevisionAttr}}":"B.2"}"""));

        // The bound attribute's value is the start; expect.revision is the revision after the step.
        var passing = ScenarioReplayer.Replay(Scenario($$"""
            {"event":"{{Approve}}","expect":{"states":["{{Approved}}"],"revision":"B.2"} },
            {"input":"verb","verb":"revise","expect":{"states":["{{InWork}}"],"values":{"{{ApprovedBy}}":null,"{{Trail}}":"-ii"},"revision":"C"} }
            """, $$""","values":{"{{RevisionAttr}}":"B.2","{{ApprovedBy}}":"kim","{{Trail}}":"-"}"""), runtime)!;
        Assert.True(passing.Passed, string.Join(" | ", passing.Diagnostics.Select(d => d.Message)));
        var failing = ScenarioReplayer.Replay(Scenario($$"""{"input":"verb","verb":"revise","expect":{"states":["{{InWork}}"],"values":{"{{ApprovedBy}}":null,"{{Trail}}":"-ii"},"revision":"A.1"} }"""), runtime)!;
        Assert.Equal("MQ9312", failing.Failure?.Rule);
        Assert.Contains("leaves the subject at revision 'B', but expect.revision is 'A.1'", failing.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_writes_expect_revision_on_the_steps_that_moved_it()
    {
        var (h, store) = await OpenAsync(Block());
        using var _ = h;
        await using var __ = store;
        var call = await store.RecordScenarioAsync(Life, new RecordScenarioRequest
        {
            Name = "release", Steps = [Event(Approve), Event(Release), Verb("revise", ",\"revision\":2")], Objects = Objects(), Subject = "d1",
        }, dryRun: true, ChangeSource.Editor, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        var steps = call.Value!.Element.GetProperty("steps");
        Assert.False(steps[0].GetProperty("expect").TryGetProperty("revision", out var none), none.ToString());
        Assert.Equal("B", steps[1].GetProperty("expect").GetProperty("revision").GetString());
        Assert.Equal("B.1", steps[2].GetProperty("expect").GetProperty("revision").GetString());
        Assert.False(steps[2].GetProperty("expect").GetProperty("values").TryGetProperty(RevisionAttr, out var bound), bound.ToString());
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        Assert.True(ScenarioReplayer.Replay(call.Value.Element.Deserialize<Scenario>(EngineJson.Options)!, runtime)!.Passed);
    }
}
