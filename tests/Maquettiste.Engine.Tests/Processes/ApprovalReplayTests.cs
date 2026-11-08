using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Approvals in simulation, recording and replay (signoff-design.md section 8; section 15's <c>ApprovalReplayTests</c>), over an invented model
/// beside the <c>process-basics</c> fixture: a document's release approval with the design's section 5 signatures (two engineers who did not
/// submit it, a quality lead, and a controller for costs above 10,000), its scenario, and the rules of decisions 7 to 16 and 26 to 28.
/// </summary>
public sealed class ApprovalReplayTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Document = "01JAPE00000000000000000001";
    private const string Cost = "01JAPA00000000000000000002";
    private const string Engineer = "01JAPR00000000000000000001";
    private const string QualityLead = "01JAPR00000000000000000002";
    private const string Controller = "01JAPR00000000000000000003";
    private const string Documents = "01JAPP00000000000000000001";
    private const string Draft = "01JAPS00000000000000000001";
    private const string Review = "01JAPS00000000000000000002";
    private const string Released = "01JAPS00000000000000000003";
    private const string SubmitEvent = "01JAPX00000000000000000001";
    private const string ApproveEvent = "01JAPX00000000000000000002";
    private const string SendBackEvent = "01JAPX00000000000000000003";
    private const string Submit = "01JAPK00000000000000000001";
    private const string Approve = "01JAPK00000000000000000002";
    private const string SendBack = "01JAPK00000000000000000003";
    private const string Gate = "01JAPG00000000000000000001";
    private const string Meaning = "01JAPG00000000000000000002";
    private const string Stage = "01JGST00000000000000000001";
    private const string Engineering = "01JNSG00000000000000000001";
    private const string Quality = "01JNSG00000000000000000002";
    private const string Finance = "01JNSG00000000000000000003";
    private const string RejectPoint = "01JAPQ00000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string T(string text) => JsonSerializer.Serialize(new { text });

    private static string DefaultSignatures(string financeWhen = "subject.cost > 10000") => $$"""
        {"id":"{{Engineering}}","name":"Engineering","signers":["{{Engineer}}"],"required":2,"notBy":[{"verb":"promote"}]},
        {"id":"{{Quality}}","name":"Quality","signers":["{{QualityLead}}"]},
        {"id":"{{Finance}}","name":"Finance","signers":["{{Controller}}"],"when":{{T(financeWhen)}} }
        """;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? signatures = null, string gateExtra = "", string points = "",
        string moreEvents = "", string moreTransitions = "", Action<LoaderHarness>? more = null, bool plain = false)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/document.json", $$"""
            {"kind":"entity","id":"{{Document}}","name":"Document","package":"{{Package}}","key":{"attributes":["01JAPA00000000000000000001"]},
             "attributes":[{"id":"01JAPA00000000000000000001","name":"id","type":"uuid"},{"id":"{{Cost}}","name":"cost","type":"int32"}]}
            """);
        foreach (var (id, name) in new[] { (Engineer, "Engineer"), (QualityLead, "QualityLead"), (Controller, "Controller") })
            harness.Write("model/actors/" + name.ToLowerInvariant() + ".json", $$"""{"kind":"actor","id":"{{id}}","name":"{{name}}","type":"role"}""");
        harness.Write("model/processes/documents.json", $$"""
            {"kind":"process","id":"{{Documents}}","name":"Documents","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JAPB00000000000000000001","entity":"{{Document}}"}],
             "events":[{"id":"{{SubmitEvent}}","name":"submit","verb":"promote"},{"id":"{{ApproveEvent}}","name":"approve","verb":"promote"},
               {"id":"{{SendBackEvent}}","name":"sendBack","verb":"reject"}{{moreEvents}}],
             "states":[{"id":"{{Draft}}","name":"Draft"},{"id":"{{Review}}","name":"Review"},{"id":"{{Released}}","name":"Released"}],
             "initial":"{{Draft}}",
             "transitions":[
               {"id":"{{Submit}}","source":"{{Draft}}","event":"{{SubmitEvent}}","targets":["{{Review}}"]},
               {"id":"{{Approve}}","source":"{{Review}}","event":"{{ApproveEvent}}","targets":["{{Released}}"],
                "gate":{"id":"{{Gate}}","name":"Release","meanings":[{"id":"{{Meaning}}","name":"Approved"}]{{gateExtra}},
                  {{(plain ? PlainGate : $$$"""
                  "stages":[{"id":"{{{Stage}}}","name":"Review","signatures":[{{{signatures ?? DefaultSignatures()}}}]}]
                  """)}} } },
               {"id":"{{SendBack}}","source":"{{Review}}","event":"{{SendBackEvent}}","targets":["{{Draft}}"]}{{moreTransitions}}]{{points}} }
            """);
        more?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    // Today's gate, without stages: two signatures from engineers and quality leads, one of them a quality lead's.
    private static readonly string PlainGate = $$""" "signers":["{{Engineer}}","{{QualityLead}}"],"required":2,"requiredActors":["{{QualityLead}}"] """;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement[] Doc(int cost) => [Json($$"""{"id":"o1","name":"doc","entity":"{{Document}}","values":{"{{Cost}}":{{cost}}} }""")];

    private static string Holds(string role, int n) => $$"""[{"id":"h{{n}}","actor":"{{role}}"}]""";

    private static readonly JsonElement[] People =
    [
        Json($$"""{"id":"ana","name":"Ana","holds":{{Holds(Engineer, 1)}} }"""),
        Json($$"""{"id":"ben","name":"Ben","holds":{{Holds(Engineer, 2)}} }"""),
        Json($$"""{"id":"cleo","name":"Cleo","holds":{{Holds(Engineer, 3)}} }"""),
        Json($$"""{"id":"dee","name":"Dee","holds":{{Holds(Controller, 4)}} }"""),
        Json($$"""{"id":"quinn","name":"Quinn","holds":{{Holds(QualityLead, 5)}} }"""),
    ];

    private static JsonElement Step(string ev, string who, string extra = "") => Json($$"""{"event":"{{ev}}","participant":"{{who}}"{{extra}} }""");

    private static Task<SimulationResult> SimulateAsync(ModelStore store, int cost, params JsonElement[] steps) => SimulateAsync(store, cost, People, null, steps);

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, int cost, JsonElement[] people, JsonElement? start, params JsonElement[] steps)
    {
        var call = await store.SimulateProcessAsync(Documents, new SimulateRequest { Steps = steps, Objects = Doc(cost), Subject = "o1", Participants = people, Start = start }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!;
    }

    private static List<(GateOutcome Outcome, string? Signature)> Audit(StepTrace trace) => [.. trace.Audit.Select(a => (a.Outcome, a.Signature))];

    private static Dictionary<string, int> Counts(SimulationResult result) =>
        result.Gates.Single(g => g.Gate == Gate).Stages!.Single().Signatures.ToDictionary(s => s.Signature, s => s.Counted, StringComparer.Ordinal);

    [Fact]
    public async Task The_design_scenario_excludes_the_submitter_counts_ignores_withdraws_and_vetoes()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 12000,
            Step(SubmitEvent, "ana"),
            Step(ApproveEvent, "ana"),
            Step(ApproveEvent, "ben"),
            Step(ApproveEvent, "cleo", ""","response":"ignore" """),
            Step(ApproveEvent, "ben", ""","response":"withdraw" """),
            Step(ApproveEvent, "dee", $$""","response":"reject","signature":"{{Finance}}","reason":"Cost not approved." """),
            Step(SendBackEvent, "dee"));
        var t = result.Trace;
        Assert.Equal(Refusals.GateExcluded, t[2].Refusal);
        Assert.Equal([(GateOutcome.Refused, Engineering)], Audit(t[2]));
        Assert.Equal(Stage, t[2].Audit[0].Stage);
        Assert.Equal([(GateOutcome.Signed, Engineering)], Audit(t[3]));
        Assert.Equal(Engineer, t[3].ActsAs);
        Assert.Equal([(GateOutcome.Ignored, Engineering)], Audit(t[4]));
        Assert.Equal([(GateOutcome.Withdrawn, Engineering)], Audit(t[5]));
        Assert.Equal([(GateOutcome.Rejected, Finance)], Audit(t[6]));
        Assert.Equal([Review], t[6].Configuration);
        Assert.Empty(t[6].Microsteps);
        // The trace echoes the step as given: no signature where the step named none, and the response it gave.
        Assert.Null(t[4].Input!.Signature);
        Assert.Equal(SignatureResponse.Ignore, t[4].Input!.Response);
        Assert.Equal([Draft], t[7].Configuration);
        Assert.Empty(t[7].Audit); // nothing held after the veto, so leaving discards nothing
    }

    [Fact]
    public async Task Signatures_complete_independently_and_the_last_needed_one_moves()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100,
            Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"), Step(ApproveEvent, "cleo"));
        var t = result.Trace;
        Assert.Equal([(GateOutcome.Signed, Quality)], Audit(t[3]));
        Assert.Equal([(GateOutcome.Signed, Engineering), (GateOutcome.Completed, null)], Audit(t[4]));
        Assert.Equal([Released], t[4].Configuration);

        // Mid-way, the gate state and the enabled trigger read the round's needed signatures; Finance is not needed at a cost of 100.
        var mid = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"));
        var gate = mid.Gates.Single();
        Assert.False(gate.Satisfied);
        Assert.Equal(Engineering, Assert.Single(gate.Signatures).Signature);
        var states = gate.Stages!.Single().Signatures;
        Assert.Equal([true, true, false], states.Select(s => s.Needed));
        Assert.Equal(["Ben"], states[0].Finished);
        Assert.False(gate.Stages![0].Complete);
        var enabled = mid.Enabled.Single(e => e.Event == ApproveEvent);
        Assert.Equal(new GateProgress(1, 3), enabled.Gate);
        Assert.Equal([Engineer, QualityLead], enabled.Actors);
        Assert.Equal([SignatureResponse.Sign, SignatureResponse.Reject, SignatureResponse.Ignore, SignatureResponse.Withdraw], enabled.Responses);
        Assert.Null(mid.Enabled.Single(e => e.Event == SendBackEvent).Responses);
    }

    [Fact]
    public async Task Conditions_are_judged_on_entry_again_after_a_rejection_and_nothing_needed_moves_as_without_a_gate()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var modify = Json($$"""{"input":"verb","verb":"modify","values":{"{{Cost}}":20000},"participant":"ana"}""");
        var after = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), modify);
        Assert.False(after.Gates.Single().Stages![0].Signatures[2].Needed); // a modify doesn't judge it again
        var rejected = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), modify, Step(ApproveEvent, "ben", ""","response":"reject" """));
        Assert.True(rejected.Gates.Single().Stages![0].Signatures[2].Needed);

        // A signature whose when is false is not needed; with none needed, the next occurrence moves, checked as a move without a gate.
        var (h2, none) = await OpenAsync($$"""{"id":"{{Quality}}","name":"Quality","signers":["{{QualityLead}}"],"when":{{T("subject.cost > 10000")}} }""");
        using var ___ = h2;
        await using var ____ = none;
        var waiting = await SimulateAsync(none, 100, Step(SubmitEvent, "ana"));
        Assert.True(waiting.Gates.Single().Satisfied);
        Assert.Equal([SignatureResponse.Sign], waiting.Enabled.Single(e => e.Event == ApproveEvent).Responses);
        var moved = await SimulateAsync(none, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "dee"), Step(SubmitEvent, "ana"));
        Assert.Equal([(GateOutcome.Completed, null)], Audit(moved.Trace[2]));
        Assert.Equal([Released], moved.Trace[2].Configuration);
        var refused = await SimulateAsync(none, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "dee", ""","response":"reject" """));
        Assert.Equal(Refusals.NoTransition, refused.Trace[2].Refusal);
    }

    [Fact]
    public async Task Row_7_refuses_in_order_and_by_response()
    {
        var (h, store) = await OpenAsync(gateExtra: ""","reasonRequired":true""");
        using var _ = h;
        await using var __ = store;
        const string Why = ""","reason":"ok" """;
        var result = await SimulateAsync(store, 100,
            Step(SubmitEvent, "ana"),
            Step(ApproveEvent, "dee", Why), // meets no needed signature's signers
            Step(ApproveEvent, "ben"), // no reason
            Step(ApproveEvent, "ben", ""","response":"withdraw","reason":"x" """), // nothing to withdraw
            Step(ApproveEvent, "ben", ""","response":"ignore","reason":"x" """),
            Step(ApproveEvent, "ben", ""","response":"reject","reason":"x" """), // an ignore blocks a rejection
            Step(ApproveEvent, "cleo", Why),
            Step(ApproveEvent, "cleo", Why), // already signed
            Step(ApproveEvent, "cleo", ""","response":"reject","reason":"x" """)); // a signature never blocks a rejection
        var refusals = result.Trace.Skip(2).Select(t => t.Refusal).ToList();
        Assert.Equal([Refusals.GateSigner, Refusals.GateReason, Refusals.GateNotSigned, null, Refusals.GateRepeat, null, Refusals.GateRepeat, null], refusals);
        Assert.All(result.Trace.Skip(2).Where(t => !t.Accepted), t => Assert.Equal(GateOutcome.Refused, Assert.Single(t.Audit).Outcome));
        Assert.Null(result.Trace[2].Audit[0].Signature); // nothing was picked
    }

    [Fact]
    public async Task One_person_gives_one_answer_unless_the_gate_allows_it()
    {
        // A person who holds both roles: the pick prefers file order, and a second signature is refused unless the flag is set.
        JsonElement[] people = [Json($$"""{"id":"pat","name":"Pat","holds":[{"id":"h1","actor":"{{Engineer}}"},{"id":"h2","actor":"{{QualityLead}}"}]}""")];
        foreach (var (flag, expected) in new[] { ("", Refusals.GateRepeat), (""","allowSignerAcrossSignatures":true""", (string?)null) })
        {
            var (h, store) = await OpenAsync(gateExtra: flag);
            using var _ = h;
            await using var __ = store;
            var call = await store.SimulateProcessAsync(Documents, new SimulateRequest
            {
                Steps = [Step(SubmitEvent, "dee2"), Step(ApproveEvent, "pat"), Step(ApproveEvent, "pat")], Objects = Doc(100), Subject = "o1",
                Participants = [.. people, Json("""{"id":"dee2","name":"Dee"}""")],
            }, Ct);
            var t = call.Value!.Trace;
            Assert.Equal(Engineering, t[2].Audit[0].Signature);
            Assert.Equal(expected, t[3].Refusal);
            if (expected is null)
                Assert.Equal(Quality, t[3].Audit[0].Signature);
        }
    }

    [Fact]
    public async Task The_acts_record_excludes_by_move_and_keeps_the_entering_move()
    {
        // notBy the move Submit: Ana submitted, so she is excluded; an automatic move would exclude nobody.
        var signatures = $$"""{"id":"{{Engineering}}","name":"Engineering","signers":["{{Engineer}}"],"notBy":[{"transition":"{{Submit}}"}]}""";
        var (h, store) = await OpenAsync(signatures);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100,
            Step(SubmitEvent, "ana"), Step(ApproveEvent, "ana"), Step(SendBackEvent, "ben"), Step(SubmitEvent, "ben"), Step(ApproveEvent, "ana"));
        Assert.Equal(Refusals.GateExcluded, result.Trace[2].Refusal);
        // Back in Review, entered by Ben's submit: Ana's earlier submit is outside the window.
        Assert.True(result.Trace[5].Accepted);
        Assert.Equal([Released], result.Trace[5].Configuration);
    }

    [Fact]
    public async Task Recording_writes_expect_signatures_and_a_difference_is_MQ9311()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        JsonElement[] steps = [Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "ana")];
        var call = await store.RecordScenarioAsync(Documents, new RecordScenarioRequest { Name = "release", Steps = steps, Objects = Doc(100), Subject = "o1", Participants = People },
            dryRun: true, ChangeSource.Editor, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        var recorded = call.Value!.Element.GetProperty("steps");
        Assert.False(recorded[0].GetProperty("expect").TryGetProperty("signatures", out var none), none.ToString());
        Assert.Equal([(Engineering, 1), (Quality, 0), (Finance, 0)],
            recorded[1].GetProperty("expect").GetProperty("signatures").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())));
        Assert.False(recorded[2].GetProperty("expect").TryGetProperty("signatures", out var refusedSignatures), refusedSignatures.ToString()); // refused: it answered nothing

        var scenario = call.Value.Element.Deserialize<Scenario>(EngineJson.Options)!;
        var wrong = scenario with
        {
            Steps = [scenario.Steps[0], scenario.Steps[1] with { Expect = scenario.Steps[1].Expect! with { Signatures = new Dictionary<string, int> { [Engineering] = 2 } } }, scenario.Steps[2]],
        };
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        Assert.True(ScenarioReplayer.Replay(scenario, runtime)!.Passed);
        var failure = ScenarioReplayer.Replay(wrong, runtime)!.Failure;
        Assert.Equal("MQ9311", failure?.Rule);
        Assert.Contains("leaves signature 'Engineering' 1 counted, but expect.signatures lists signature 'Engineering' 2", failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gated_move_that_completes_without_leaving_its_source_starts_round_1_again()
    {
        // An internal move on Review (no targets) with its own approval: completing it keeps the object in Review and starts the round again.
        const string RecheckEvent = "01JAPX00000000000000000004";
        const string Recheck = "01JAPK00000000000000000004";
        const string Checked = "01JNSG00000000000000000004";
        var events = $$""",{"id":"{{RecheckEvent}}","name":"recheck"}""";
        var transitions = $$"""
            ,{"id":"{{Recheck}}","source":"{{Review}}","event":"{{RecheckEvent}}",
              "gate":{"id":"01JAPG00000000000000000003","name":"Recheck","meanings":[{"id":"01JAPG00000000000000000004","name":"Checked"}],
                "stages":[{"id":"01JGST00000000000000000002","name":"Check","signatures":[{"id":"{{Checked}}","name":"Checked","signers":["{{QualityLead}}"]}]}]} }
            """;
        var (h, store) = await OpenAsync(moreEvents: events, moreTransitions: transitions);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(RecheckEvent, "quinn"), Step(RecheckEvent, "quinn"));
        Assert.Equal([(GateOutcome.Signed, Checked), (GateOutcome.Completed, null)], Audit(result.Trace[3]));
        Assert.Equal([Review], result.Trace[3].Configuration);
        // Quinn's answer went with the completed round, so Quinn may sign the new one; the other approval keeps Ben's signature.
        Assert.Equal([(GateOutcome.Signed, Checked), (GateOutcome.Completed, null)], Audit(result.Trace[4]));
        Assert.Equal(1, Counts(result)[Engineering]);
    }

    [Fact]
    public async Task A_deny_on_reject_refuses_a_signers_rejection_and_leaving_with_only_an_ignore_is_audited()
    {
        var rights = $$""","rights":[{"id":"01JAPZ00000000000000000001","name":"NoVeto","access":"deny","verbs":["reject"],"actors":["{{Engineer}}"]}]""";
        var (h, store) = await OpenAsync(points: rights);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben", ""","response":"reject" """),
            Step(ApproveEvent, "ben", ""","response":"ignore" """), Step(SendBackEvent, "dee")); // the deny covers the reject move too, so Dee sends it back
        Assert.Equal(Refusals.Right, result.Trace[2].Refusal);
        Assert.Equal("01JAPZ00000000000000000001", result.Trace[2].RefusedBy);
        Assert.Equal([(GateOutcome.Refused, Engineering)], Audit(result.Trace[2]));
        Assert.True(result.Trace[3].Accepted);
        Assert.Equal([(GateOutcome.Discarded, null)], Audit(result.Trace[4]));
    }

    [Fact]
    public async Task Trigger_points_on_reject_run_at_the_gate_and_a_refusing_check_leaves_the_approval()
    {
        var points = $$""","triggerPoints":[{"id":"{{RejectPoint}}","name":"NoVeto","type":"check","verbs":["reject"],"transitions":["{{Approve}}"],"condition":{{T("false")}} }]""";
        var (h, store) = await OpenAsync(points: points);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "cleo", ""","response":"reject" """),
            Step(SendBackEvent, "cleo"));
        Assert.Equal(Refusals.Check, result.Trace[3].Refusal);
        Assert.Equal(RejectPoint, result.Trace[3].RefusedBy);
        Assert.Equal([(GateOutcome.Refused, Engineering)], Audit(result.Trace[3]));
        // The ordinary reject move isn't the gated transition, so the point doesn't run there; leaving discards Ben's signature.
        Assert.True(result.Trace[4].Accepted);
        Assert.Equal([(GateOutcome.Discarded, null)], Audit(result.Trace[4]));
    }

    // ---- review fixes ----

    [Fact]
    public async Task Signing_a_complete_signature_again_is_refused_gate_repeat_with_or_without_a_participant()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "quinn"), Step(ApproveEvent, "quinn"),
            Json($$"""{"event":"{{ApproveEvent}}","actor":"{{QualityLead}}","signer":"quinn"}"""));
        var t = result.Trace;
        Assert.Equal([(GateOutcome.Signed, Quality)], Audit(t[2]));
        // Quality is complete: the repeat is refused on it, as on a gate without stages, acting as the quality lead Quinn is.
        Assert.Equal(Refusals.GateRepeat, t[3].Refusal);
        Assert.Equal(QualityLead, t[3].ActsAs);
        Assert.Equal([(GateOutcome.Refused, Quality)], Audit(t[3]));
        Assert.Equal(QualityLead, t[3].Audit[0].Actor);
        Assert.Equal(Refusals.GateRepeat, t[4].Refusal);
        Assert.Equal([(GateOutcome.Refused, Quality)], Audit(t[4]));
    }

    [Fact]
    public async Task A_withdraw_acts_as_the_actor_the_person_signed_as()
    {
        // Max holds Engineer and Controller; the signature needs a controller, so Max signs as Controller. After that no required actor is
        // unsigned, and the first signer Max meets would be Engineer: the withdraw still takes the signature back as Controller.
        var signatures = $$"""{"id":"{{Engineering}}","name":"Editor","signers":["{{Engineer}}","{{Controller}}"],"required":2,"requiredActors":["{{Controller}}"]}""";
        var (h, store) = await OpenAsync(signatures);
        using var _ = h;
        await using var __ = store;
        JsonElement[] people = [.. People, Json($$"""{"id":"max","name":"Max","holds":[{"id":"h6","actor":"{{Engineer}}"},{"id":"h7","actor":"{{Controller}}"}]}""")];
        var result = await SimulateAsync(store, 100, people, null, Step(SubmitEvent, "ana"), Step(ApproveEvent, "max"), Step(ApproveEvent, "max", ""","response":"withdraw" """));
        Assert.Equal(Controller, result.Trace[2].ActsAs);
        Assert.Equal(Controller, result.Trace[3].ActsAs);
        Assert.Equal([(GateOutcome.Withdrawn, Engineering)], Audit(result.Trace[3]));
        Assert.Equal(Controller, result.Trace[3].Audit[0].Actor);
    }

    [Fact]
    public async Task Simulate_and_record_take_a_steps_signature_by_name_and_record_and_refresh_keep_response_and_signature()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        JsonElement[] steps = [Step(SubmitEvent, "ana"), Step(ApproveEvent, "dee", ""","response":"reject","signature":"Finance","reason":"No." """)];
        var result = await SimulateAsync(store, 12000, steps);
        Assert.True(result.Trace[2].Accepted);
        Assert.Equal([(GateOutcome.Rejected, Finance)], Audit(result.Trace[2]));

        var call = await store.RecordScenarioAsync(Documents, new RecordScenarioRequest { Name = "veto", Steps = steps, Objects = Doc(12000), Subject = "o1", Participants = People },
            dryRun: false, ChangeSource.Editor, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        var recorded = call.Value!.Element.GetProperty("steps")[1];
        Assert.Equal(("reject", Finance), (recorded.GetProperty("response").GetString(), recorded.GetProperty("signature").GetString()));

        var scenario = store.Current!.All<Scenario>().Single(s => s.Name == "veto");
        var batch = store.ParseBatch(System.Text.Encoding.UTF8.GetBytes($$"""{"operations":[{"op":"refresh-scenario","id":"{{scenario.Id}}"}]}"""));
        Assert.Empty(batch.Diagnostics);
        await store.ApplyBatchAsync(batch.Batch!, ChangeSource.Editor, Ct);
        var refreshed = store.Current!.Get<Scenario>(scenario.Id)!.Steps[1];
        Assert.Equal((SignatureResponse.Reject, Finance), (refreshed.Response, refreshed.Signature));
    }

    [Fact]
    public async Task A_required_actor_is_met_through_the_signature_actors_membership()
    {
        // Auditor is a member of QualityLead: a signature given as Auditor counts as a signer and meets the required QualityLead.
        const string Auditor = "01JAPR00000000000000000009";
        var signatures = $$"""{"id":"{{Engineering}}","name":"Engineering","signers":["{{Engineer}}","{{QualityLead}}"],"requiredActors":["{{QualityLead}}"]}""";
        var (h, store) = await OpenAsync(signatures, more: harness => harness.Write("model/actors/auditor.json",
            $$"""{"kind":"actor","id":"{{Auditor}}","name":"Auditor","type":"role","memberOf":["{{QualityLead}}"]}"""));
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"),
            Json($$"""{"event":"{{ApproveEvent}}","actor":"{{Auditor}}","signer":"ivy"}"""));
        // Ben's signature fills the count but not the required quality lead; Ivy's, as Auditor, does, and the move follows.
        Assert.Equal([(GateOutcome.Signed, Engineering)], Audit(result.Trace[2]));
        Assert.Equal([Review], result.Trace[2].Configuration);
        Assert.Equal([(GateOutcome.Signed, Engineering), (GateOutcome.Completed, null)], Audit(result.Trace[3]));
        Assert.Equal([Released], result.Trace[3].Configuration);
    }

    [Fact]
    public async Task Repeat_signing_is_allowed_only_on_the_signature_that_allows_it()
    {
        var signatures = $$"""
            {"id":"{{Engineering}}","name":"Engineering","signers":["{{Engineer}}"],"required":2,"allowRepeatSigner":true},
            {"id":"{{Quality}}","name":"Quality","signers":["{{QualityLead}}"],"required":2}
            """;
        var (h, store) = await OpenAsync(signatures);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Step(ApproveEvent, "quinn"));
        Assert.Equal([(GateOutcome.Signed, Engineering)], Audit(result.Trace[3]));
        Assert.Equal([(GateOutcome.Signed, Quality)], Audit(result.Trace[4]));
        Assert.Equal(Refusals.GateRepeat, result.Trace[5].Refusal);
        Assert.Equal([(GateOutcome.Refused, Quality)], Audit(result.Trace[5]));
        Assert.Equal(2, Counts(result)[Engineering]);
        Assert.Equal(1, Counts(result)[Quality]);
    }

    [Fact]
    public async Task A_named_trigger_point_on_reject_runs_at_the_approval_and_a_refusing_check_leaves_it()
    {
        const string Named = "01JAPQ00000000000000000009";
        var (h, store) = await OpenAsync(more: harness => harness.Write("model/trigger-points/no-veto.json", $$"""
            {"kind":"trigger-point","id":"{{Named}}","name":"NoVetoAnywhere","type":"check","timing":"before","verbs":["reject"],
             "subjects":[{"entity":"{{Document}}"}],"condition":{{T("false")}} }
            """));
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "cleo", ""","response":"reject" """));
        Assert.Equal(Refusals.Check, result.Trace[3].Refusal);
        Assert.Equal(Named, result.Trace[3].RefusedBy);
        Assert.Equal([(GateOutcome.Refused, Engineering)], Audit(result.Trace[3]));
        Assert.Equal(1, Counts(result)[Engineering]);
    }

    [Fact]
    public async Task Two_acts_in_one_instant_are_ordered_by_step()
    {
        // Every step happens at the start's instant. Engineering is not by whoever promoted the document since it entered Review: Ana's submit
        // came before Ben's send back and resubmit, so only Ben is excluded, although all three acts share one time.
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, People, Json("""{"at":"2026-01-01T00:00:00Z"}"""),
            Step(SubmitEvent, "ana"), Step(SendBackEvent, "ben"), Step(SubmitEvent, "ben"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "ana"));
        Assert.Single(result.Trace.Select(t => t.Clock).Distinct());
        Assert.Equal(Refusals.GateExcluded, result.Trace[4].Refusal);
        Assert.Equal([(GateOutcome.Signed, Engineering)], Audit(result.Trace[5]));
    }

    [Fact]
    public async Task A_gate_without_stages_keeps_its_trace_byte_for_byte()
    {
        var (h, store) = await OpenAsync(plain: true);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, 100, Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "dee"),
            Step(ApproveEvent, "quinn"));
        var text = JsonSerializer.Serialize(result with { ProcessHash = "" }, EngineJson.Options);
        foreach (var member in new[] { "\"signature\"", "\"stage\"", "\"stages\"", "\"responses\"", "\"response\"", "\"signatureCounts\"", "\"acts\"" })
            Assert.DoesNotContain(member, text, StringComparison.Ordinal);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        Assert.Equal(PlainTrace, hash);
    }

    // The trace and gate state of a gate without stages, its process hash left out: today's members only (checked above when it was taken).
    private const string PlainTrace = "EB0F77DF9716FD6D257E205A38FF26D8794FFD4D6AC00CAC113CF179ABF9C50B";
}
