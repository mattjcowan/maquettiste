using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Approvals, part 2, in simulation, recording and replay (signoff-design.md decisions 5, 6, 20, 21 and 33 to 35; section 15's part 2 list), over
/// an invented model beside the <c>process-basics</c> fixture: a document's release approval with stages in rounds, a stage skipped by its
/// <c>when</c>, a controller's signature that may be delegated, and reminders and escalations. A model that uses part 1 only keeps its traces,
/// audits, gate states, enabled triggers, recordings and replays byte for byte.
/// </summary>
public sealed class ApprovalPart2ReplayTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Document = "01JAPE00000000000000000001";
    private const string Cost = "01JAPA00000000000000000002";
    private const string IsContract = "01JAPA00000000000000000003";
    private const string Engineer = "01JAPR00000000000000000001";
    private const string QualityLead = "01JAPR00000000000000000002";
    private const string Controller = "01JAPR00000000000000000003";
    private const string Deputy = "01JAPR00000000000000000004";
    private const string Director = "01JAPR00000000000000000005";
    private const string Lawyer = "01JAPR00000000000000000006";
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
    private const string ReviewStage = "01JGST00000000000000000001";
    private const string FinanceStage = "01JGST00000000000000000002";
    private const string LegalStage = "01JGST00000000000000000003";
    private const string Engineering = "01JNSG00000000000000000001";
    private const string Quality = "01JNSG00000000000000000002";
    private const string Finance = "01JNSG00000000000000000003";
    private const string ControllerSig = "01JNSG00000000000000000004";
    private const string CounselSig = "01JNSG00000000000000000005";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string T(string text) => JsonSerializer.Serialize(new { text });

    // Part 1's section 5 approval: one stage, two engineers who did not submit it, a quality lead, and a controller for costs above 10,000.
    private static readonly string Part1Stages = $$"""
        [{"id":"{{ReviewStage}}","name":"Review","signatures":[
          {"id":"{{Engineering}}","name":"Engineering","signers":["{{Engineer}}"],"required":2,"notBy":[{"verb":"promote"}]},
          {"id":"{{Quality}}","name":"Quality","signers":["{{QualityLead}}"]},
          {"id":"{{Finance}}","name":"Finance","signers":["{{Controller}}"],"when":{{T("subject.cost > 10000")}} }]}]
        """;

    // Part 2's section 5 approval: the review round, then finance and legal side by side, legal only for contracts.
    private static string RoundStages(string controller = "", string engineering = "", string quality = "", string legalWhen = "subject.isContract") => $$"""
        [{"id":"{{ReviewStage}}","name":"Review","signatures":[
           {"id":"{{Engineering}}","name":"Engineering","signers":["{{Engineer}}"],"required":1{{engineering}}},
           {"id":"{{Quality}}","name":"Quality","signers":["{{QualityLead}}"]{{quality}}}]},
         {"id":"{{FinanceStage}}","name":"Finance","round":2,"signatures":[
           {"id":"{{ControllerSig}}","name":"Controller","signers":["{{Controller}}"]{{controller}} }]},
         {"id":"{{LegalStage}}","name":"Legal","round":2,"when":{{T(legalWhen)}},"signatures":[
           {"id":"{{CounselSig}}","name":"Counsel","signers":["{{Lawyer}}"]}]}]
        """;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string stages, string gateExtra = "", string moreEvents = "",
        string moreTransitions = "", string points = "")
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/document.json", $$"""
            {"kind":"entity","id":"{{Document}}","name":"Document","package":"{{Package}}","key":{"attributes":["01JAPA00000000000000000001"]},
             "attributes":[{"id":"01JAPA00000000000000000001","name":"id","type":"uuid"},{"id":"{{Cost}}","name":"cost","type":"int32"},
               {"id":"{{IsContract}}","name":"isContract","type":"bool"}]}
            """);
        foreach (var (id, name) in new[] { (Engineer, "Engineer"), (QualityLead, "QualityLead"), (Controller, "Controller"), (Deputy, "Deputy"), (Director, "Director"), (Lawyer, "Lawyer") })
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
                "gate":{"id":"{{Gate}}","name":"Release","meanings":[{"id":"{{Meaning}}","name":"Approved"}]{{gateExtra}},"stages":{{stages}} } },
               {"id":"{{SendBack}}","source":"{{Review}}","event":"{{SendBackEvent}}","targets":["{{Draft}}"]}{{moreTransitions}}]{{points}} }
            """);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement[] Doc(int cost, bool contract = false) =>
        [Json($$"""{"id":"o1","name":"doc","entity":"{{Document}}","values":{"{{Cost}}":{{cost}},"{{IsContract}}":{{(contract ? "true" : "false")}}} }""")];

    private static string Holds(string role, int n) => $$"""[{"id":"h{{n}}","actor":"{{role}}"}]""";

    private static readonly JsonElement[] People =
    [
        Json($$"""{"id":"ana","name":"Ana","holds":{{Holds(Engineer, 1)}} }"""),
        Json($$"""{"id":"ben","name":"Ben","holds":{{Holds(Engineer, 2)}} }"""),
        Json($$"""{"id":"cleo","name":"Cleo","holds":{{Holds(Engineer, 3)}} }"""),
        Json($$"""{"id":"dee","name":"Dee","holds":{{Holds(Controller, 4)}} }"""),
        Json($$"""{"id":"quinn","name":"Quinn","holds":{{Holds(QualityLead, 5)}} }"""),
        Json($$"""{"id":"vic","name":"Vic","holds":{{Holds(Deputy, 6)}} }"""),
        Json($$"""{"id":"lou","name":"Lou","holds":{{Holds(Lawyer, 7)}} }"""),
        Json($$"""{"id":"rae","name":"Rae","holds":[{"id":"h8","actor":"{{Deputy}}"},{"id":"h9","actor":"{{Engineer}}"}]}"""),
    ];

    private static JsonElement Step(string ev, string who, string extra = "") => Json($$"""{"event":"{{ev}}","participant":"{{who}}"{{extra}} }""");

    private static JsonElement Time(string after) => Json($$"""{"input":"time","after":"{{after}}"}""");

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, JsonElement[] objects, params JsonElement[] steps)
    {
        var call = await store.SimulateProcessAsync(Documents, new SimulateRequest
        {
            Steps = steps, Objects = objects, Subject = "o1", Participants = People, Start = Json("""{"at":"2026-01-01T00:00:00Z"}"""),
        }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!;
    }

    private static async Task<JsonElement> RecordAsync(ModelStore store, JsonElement[] objects, params JsonElement[] steps)
    {
        var call = await store.RecordScenarioAsync(Documents, new RecordScenarioRequest
        {
            Name = "recorded", Steps = steps, Objects = objects, Subject = "o1", Participants = People, Start = Json("""{"at":"2026-01-01T00:00:00Z"}"""),
        }, dryRun: true, ChangeSource.Editor, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!.Element;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private const string Expire = "01JAPK00000000000000000009";
    private const string NoDelegate = "01JAPZ00000000000000000001";
    private const string DelegatePoint = "01JAPQ00000000000000000001";

    private static List<(GateOutcome Outcome, string? Signature)> Audit(StepTrace trace) => [.. trace.Audit.Select(a => (a.Outcome, a.Signature))];

    private static Dictionary<string, int> Counts(SimulationResult result) =>
        result.Gates.Single(g => g.Gate == Gate).Stages!.SelectMany(s => s.Signatures).ToDictionary(s => s.Signature, s => s.Counted, StringComparer.Ordinal);

    private static List<(string Signature, NoticeKind Kind)> Notices(StepTrace trace) => [.. (trace.Notices ?? []).Select(n => (n.Signature, n.Kind))];

    private static string Delegable(string extra = "") => $$""","delegateTo":["{{Deputy}}"],"requiredActors":["{{Controller}}"]{{extra}}""";

    [Fact]
    public async Task Rounds_run_side_by_side_then_in_order_and_only_the_last_round_moves()
    {
        var (h, store) = await OpenAsync(RoundStages());
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Doc(100, contract: true),
            Step(SubmitEvent, "ana"), Step(ApproveEvent, "dee"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"), Step(ApproveEvent, "dee"), Step(ApproveEvent, "lou"));
        var t = result.Trace;
        Assert.Equal(Refusals.GateSigner, t[2].Refusal); // the controller signs in round 2, not before
        Assert.Equal([(GateOutcome.Signed, Engineering)], Audit(t[3]));
        // The signature that completes round 1 is consumed in the admit: accepted, no move, round 2 starts.
        Assert.True(t[4].Accepted);
        Assert.Equal([(GateOutcome.Signed, Quality)], Audit(t[4]));
        Assert.Empty(t[4].Microsteps);
        Assert.Equal([Review], t[4].Configuration);
        Assert.Equal([(GateOutcome.Signed, ControllerSig)], Audit(t[5]));
        Assert.Equal([(GateOutcome.Signed, CounselSig), (GateOutcome.Completed, null)], Audit(t[6]));
        Assert.Equal([Released], t[6].Configuration);

        // In round 1, the gate state reads later rounds as needed and not complete; the enabled trigger reads round 1 only.
        var first = await SimulateAsync(store, Doc(100, contract: true), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"));
        var stages = first.Gates.Single().Stages!;
        Assert.Equal([1, 2, 2], stages.Select(s => s.Round));
        Assert.Equal([false, false, false], stages.Select(s => s.Complete));
        Assert.All(stages.SelectMany(s => s.Signatures), s => Assert.True(s.Needed));
        var enabled = first.Enabled.Single(e => e.Event == ApproveEvent);
        Assert.Equal(new GateProgress(1, 2), enabled.Gate);
        Assert.Equal([Engineer, QualityLead], enabled.Actors);
        Assert.Equal([SignatureResponse.Sign, SignatureResponse.Reject, SignatureResponse.Ignore, SignatureResponse.Withdraw], enabled.Responses);

        // In round 2: round 1's stage is complete, and the enabled trigger reads round 2's signatures.
        var second = await SimulateAsync(store, Doc(100, contract: true), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"));
        Assert.Equal([true, false, false], second.Gates.Single().Stages!.Select(s => s.Complete));
        Assert.False(second.Gates.Single().Satisfied);
        enabled = second.Enabled.Single(e => e.Event == ApproveEvent);
        Assert.Equal(new GateProgress(0, 2), enabled.Gate);
        Assert.Equal([Controller, Lawyer], enabled.Actors);
        Assert.Equal(new Dictionary<string, int> { [Engineering] = 1, [Quality] = 1, [ControllerSig] = 0, [CounselSig] = 0 }, Counts(second));
    }

    [Fact]
    public async Task A_stage_is_skipped_by_its_when_at_its_rounds_start_and_rounds_with_nothing_needed_complete_at_once()
    {
        var (h, store) = await OpenAsync(RoundStages());
        using var _ = h;
        await using var __ = store;
        // Not a contract: Legal is skipped when round 2 starts, counts as complete, and the controller's signature alone completes the approval.
        var skipped = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"));
        var legal = skipped.Gates.Single().Stages![2];
        Assert.True(legal.Skipped);
        Assert.True(legal.Complete);
        Assert.False(legal.Signatures.Single().Needed);
        Assert.False(skipped.Gates.Single().Stages![1].Skipped);
        Assert.Equal([Controller], skipped.Enabled.Single(e => e.Event == ApproveEvent).Actors);
        var json = JsonSerializer.Serialize(skipped.Gates.Single(), EngineJson.Options);
        Assert.Equal(1, json.Split("\"skipped\"").Length - 1); // left out unless true

        // Judged when round 2 starts, not on entry: a modify during round 1 makes it a contract, so Legal is needed.
        var modify = Json($$"""{"input":"verb","verb":"modify","values":{"{{IsContract}}":true},"participant":"ana"}""");
        var judged = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), modify, Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"));
        Assert.False(judged.Gates.Single().Stages![2].Skipped);
        Assert.True(judged.Gates.Single().Stages![2].Signatures.Single().Needed);
    }

    [Fact]
    public async Task A_signature_that_completes_round_1_with_every_later_round_needing_nothing_completes_the_approval_and_moves()
    {
        // Round 2 needs nothing (the controller only above 10,000, Legal skipped): the signature that completes round 1 completed the approval,
        // so its occurrence moves at once, as the occurrence that completes a gate does (decisions 4 and 33).
        var below = $$""","when":{{T("subject.cost > 10000")}}""";
        var (h, store) = await OpenAsync(RoundStages(controller: below));
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"));
        var t = result.Trace;
        Assert.Equal([(GateOutcome.Signed, Engineering)], Audit(t[2]));
        Assert.Equal([Review], t[2].Configuration);
        Assert.True(t[3].Accepted);
        Assert.Equal([(GateOutcome.Signed, Quality), (GateOutcome.Completed, null)], Audit(t[3]));
        Assert.NotEmpty(t[3].Microsteps);
        Assert.Equal([Released], t[3].Configuration);

        // The move's own checks run on that occurrence, as on any occurrence that completes the approval: one that fails refuses it.
        var points = $$""","triggerPoints":[{"id":"{{DelegatePoint}}","name":"NoRelease","type":"check","verbs":["promote"],"transitions":["{{Approve}}"],"condition":{{T("false")}} }]""";
        var (h2, checks) = await OpenAsync(RoundStages(controller: below), points: points);
        using var ___ = h2;
        await using var ____ = checks;
        var refused = await SimulateAsync(checks, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"));
        Assert.Equal((Refusals.Check, DelegatePoint), (refused.Trace[3].Refusal, refused.Trace[3].RefusedBy));
        Assert.Equal([Review], refused.Trace[3].Configuration);
    }

    [Fact]
    public async Task An_approval_whose_round_1_starts_with_nothing_needed_waits_for_the_next_occurrence()
    {
        // Nothing is needed in any round on entry: the approval is complete when it starts, the entering move does nothing more, and the gated
        // event's next occurrence moves as if there were no gate (decision 7).
        var below = $$""","when":{{T("subject.cost > 10000")}}""";
        var (h, store) = await OpenAsync(RoundStages(controller: below, engineering: below, quality: below));
        using var _ = h;
        await using var __ = store;
        var waiting = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"));
        Assert.Empty(waiting.Trace[1].Audit);
        Assert.Equal([Review], waiting.Trace[1].Configuration);
        Assert.True(waiting.Gates.Single().Satisfied);
        Assert.Equal([SignatureResponse.Sign], waiting.Enabled.Single(e => e.Event == ApproveEvent).Responses);
        var moved = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "cleo"));
        Assert.Equal([(GateOutcome.Completed, null)], Audit(moved.Trace[2]));
        Assert.Equal([Released], moved.Trace[2].Configuration);
    }

    [Fact]
    public async Task Withdraw_acts_in_the_current_round_and_a_rejection_in_round_2_clears_every_round()
    {
        var (h, store) = await OpenAsync(RoundStages());
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Step(ApproveEvent, "ben", ""","response":"withdraw" """), Step(ApproveEvent, "ben", $$""","response":"withdraw","signature":"{{Engineering}}" """),
            Step(ApproveEvent, "dee", ""","response":"reject","reason":"No budget." """));
        var t = result.Trace;
        Assert.Equal(Refusals.GateNotSigned, t[4].Refusal);
        Assert.Equal(Refusals.GateNotSigned, t[5].Refusal);
        Assert.Equal([(GateOutcome.Rejected, ControllerSig)], Audit(t[6]));
        Assert.Equal(FinanceStage, t[6].Audit[0].Stage);
        Assert.Equal([Review], t[6].Configuration);
        Assert.All(Counts(result).Values, n => Assert.Equal(0, n));
        Assert.Equal([false, false, false], result.Gates.Single().Stages!.Select(s => s.Complete));
        Assert.False(result.Gates.Single().Stages![2].Skipped); // round 2 hasn't started again
        Assert.Equal([Engineer, QualityLead], result.Enabled.Single(e => e.Event == ApproveEvent).Actors);
    }

    [Fact]
    public async Task A_delegate_finishes_the_delegator_and_the_delegate_signs_on_behalf_as_the_delegators_actor()
    {
        var (h, store) = await OpenAsync(RoundStages(controller: Delegable()));
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Step(ApproveEvent, "dee", ""","response":"delegate","delegateTo":"ben" """),
            Step(ApproveEvent, "dee", ""","response":"delegate","delegateTo":"vic" """),
            Step(ApproveEvent, "dee"),
            Step(ApproveEvent, "vic"),
            Step(ApproveEvent, "vic", ""","onBehalfOf":"ben" """),
            Step(ApproveEvent, "vic", ""","onBehalfOf":"dee" """));
        var t = result.Trace;
        Assert.Equal(Refusals.GateSigner, t[4].Refusal); // Ben is no deputy
        Assert.Contains("The person it goes to isn't one of the actors this signature may be delegated to.", t[4].Notes ?? []);
        Assert.Equal([(GateOutcome.Refused, ControllerSig)], Audit(t[4]));
        Assert.Equal([(GateOutcome.Delegated, ControllerSig)], Audit(t[5]));
        Assert.Equal(("Dee", Controller, "Vic"), (t[5].Audit[0].Signer, t[5].Audit[0].Actor, t[5].Audit[0].DelegateTo));
        Assert.Null(t[5].Audit[0].OnBehalfOf);
        Assert.Empty(t[5].Microsteps);
        Assert.Equal(Refusals.GateRepeat, t[6].Refusal); // the delegator is finished on it
        Assert.Equal(Refusals.GateSigner, t[7].Refusal); // the deputy signs only on behalf
        Assert.Equal(Refusals.GateSigner, t[8].Refusal); // and only for whoever delegated to them
        // Vic's signature counts as Dee's actor, so the controller required actor is met and the approval completes.
        Assert.Equal([(GateOutcome.Signed, ControllerSig), (GateOutcome.Completed, null)], Audit(t[9]));
        Assert.Equal(("Vic", Controller, "Dee"), (t[9].Audit[0].Signer, t[9].Audit[0].Actor, t[9].Audit[0].OnBehalfOf));
        Assert.Equal(Deputy, t[9].ActsAs);
        Assert.Equal("dee", t[9].Input!.OnBehalfOf); // the trace echoes the step as given
        Assert.Equal([Released], t[9].Configuration);
        var text = JsonSerializer.Serialize(t[5].Audit[0], EngineJson.Options);
        Assert.Contains("\"outcome\":\"delegated\"", text, StringComparison.Ordinal);
        Assert.Contains("\"delegateTo\":\"Vic\"", text, StringComparison.Ordinal);

        // A delegation goes to someone else (decision 34): Vic, acting as a controller, can't hand the signature to themselves.
        var self = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Json($$"""{"event":"{{ApproveEvent}}","actor":"{{Controller}}","signer":"Vic","response":"delegate","delegateTo":"vic"}"""));
        Assert.Equal(Refusals.GateSigner, self.Trace[4].Refusal);
        Assert.Equal([(GateOutcome.Refused, ControllerSig)], Audit(self.Trace[4]));
        Assert.Contains("A signer can't delegate a signature to themselves; delegate it to someone else.", self.Trace[4].Notes ?? []);

        // Mid-way, the gate state lists both people as finished, and the enabled trigger offers delegate.
        var mid = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Step(ApproveEvent, "dee", ""","response":"delegate","delegateTo":"vic" """));
        Assert.Equal(["Dee"], mid.Gates.Single().Stages![1].Signatures.Single().Finished);
        Assert.Equal([SignatureResponse.Sign, SignatureResponse.Reject, SignatureResponse.Ignore, SignatureResponse.Withdraw, SignatureResponse.Delegate],
            mid.Enabled.Single(e => e.Event == ApproveEvent).Responses);
    }

    [Fact]
    public async Task Answers_on_behalf_ignore_withdraw_and_reject_and_a_rejection_or_leaving_ends_the_delegation()
    {
        var (h, store) = await OpenAsync(RoundStages(controller: Delegable(""","notBy":[{"verb":"promote"}]""")));
        using var _ = h;
        await using var __ = store;
        JsonElement[] round2 = [Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn")];
        var delegated = Step(ApproveEvent, "dee", ""","response":"delegate","delegateTo":"vic" """);
        JsonElement Behalf(string extra = "") => Step(ApproveEvent, "vic", ""","onBehalfOf":"dee" """ + extra);

        // A withdraw on behalf reopens the delegation; the deputy may sign on behalf again.
        var withdrawn = await SimulateAsync(store, Doc(100), [.. round2, delegated, Step(ApproveEvent, "dee", ""","response":"ignore" """)]);
        Assert.Equal(Refusals.GateRepeat, withdrawn.Trace[^1].Refusal);
        var reopened = await SimulateAsync(store, Doc(100, contract: true), [.. round2, delegated, Behalf(), Behalf(""","response":"withdraw" """), Behalf(), Behalf()]);
        var t = reopened.Trace;
        Assert.Equal([(GateOutcome.Signed, ControllerSig)], Audit(t[5])); // Legal still needed
        Assert.Equal([(GateOutcome.Withdrawn, ControllerSig)], Audit(t[6]));
        Assert.Equal("Dee", t[6].Audit[0].OnBehalfOf);
        Assert.Equal([(GateOutcome.Signed, ControllerSig)], Audit(t[7]));
        Assert.Equal(Refusals.GateRepeat, t[8].Refusal); // one delegation gives one answer that counts

        // An ignore on behalf finishes the deputy too.
        var ignored = await SimulateAsync(store, Doc(100), [.. round2, delegated, Behalf(""","response":"ignore" """), Behalf()]);
        Assert.Equal([(GateOutcome.Ignored, ControllerSig)], Audit(ignored.Trace[5]));
        Assert.Equal(Refusals.GateRepeat, ignored.Trace[6].Refusal);

        // A rejection on behalf vetoes the approval and ends the delegation with it.
        var rejected = await SimulateAsync(store, Doc(100), [.. round2, delegated, Behalf(""","response":"reject" """), .. round2[1..], Behalf()]);
        Assert.Equal([(GateOutcome.Rejected, ControllerSig)], Audit(rejected.Trace[5]));
        Assert.Equal("Dee", rejected.Trace[5].Audit[0].OnBehalfOf);
        Assert.Equal(Refusals.GateSigner, rejected.Trace[8].Refusal);

        // Leaving the state ends it too, audited discarded.
        var left = await SimulateAsync(store, Doc(100), [.. round2, delegated, Step(SendBackEvent, "dee"), .. round2, Behalf()]);
        Assert.Equal([(GateOutcome.Discarded, null)], Audit(left.Trace[5]));
        Assert.Equal(Refusals.GateSigner, left.Trace[9].Refusal);

        // notBy is checked on the person who answers: Rae, a deputy, submitted it.
        var excluded = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "rae"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Step(ApproveEvent, "dee", ""","response":"delegate","delegateTo":"rae" """), Step(ApproveEvent, "rae", ""","onBehalfOf":"dee" """));
        Assert.Equal([(GateOutcome.Delegated, ControllerSig)], Audit(excluded.Trace[4]));
        Assert.Equal(Refusals.GateExcluded, excluded.Trace[5].Refusal);
    }

    [Fact]
    public async Task Rights_and_trigger_points_run_on_delegate()
    {
        var rights = $$""","rights":[{"id":"{{NoDelegate}}","name":"NoDelegating","access":"deny","verbs":["delegate"],"actors":["{{Controller}}"]}]""";
        var (h, store) = await OpenAsync(RoundStages(controller: Delegable()), points: rights);
        using var _ = h;
        await using var __ = store;
        var denied = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Step(ApproveEvent, "dee", ""","response":"delegate","delegateTo":"vic" """), Step(ApproveEvent, "dee"));
        Assert.Equal((Refusals.Right, NoDelegate), (denied.Trace[4].Refusal, denied.Trace[4].RefusedBy));
        Assert.Equal([(GateOutcome.Refused, ControllerSig)], Audit(denied.Trace[4]));
        Assert.Equal([(GateOutcome.Signed, ControllerSig), (GateOutcome.Completed, null)], Audit(denied.Trace[5]));

        var points = $$""","triggerPoints":[{"id":"{{DelegatePoint}}","name":"NoHandoff","type":"check","verbs":["delegate"],"transitions":["{{Approve}}"],"condition":{{T("false")}} }]""";
        var (h2, checks) = await OpenAsync(RoundStages(controller: Delegable()), points: points);
        using var ___ = h2;
        await using var ____ = checks;
        var refused = await SimulateAsync(checks, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Step(ApproveEvent, "dee", ""","response":"delegate","delegateTo":"vic" """), Step(ApproveEvent, "vic", ""","onBehalfOf":"dee" """));
        Assert.Equal((Refusals.Check, DelegatePoint), (refused.Trace[4].Refusal, refused.Trace[4].RefusedBy));
        Assert.Equal([(GateOutcome.Refused, ControllerSig)], Audit(refused.Trace[4]));
        Assert.Equal(Refusals.GateSigner, refused.Trace[5].Refusal); // nothing was delegated
    }

    [Fact]
    public async Task Notices_fall_due_once_per_round_start_in_file_order_and_a_rejection_starts_them_again()
    {
        var stages = RoundStages(controller: $$""","remindAfter":"P2D","escalateAfter":"P4D","escalateTo":["{{Director}}"]""", engineering: ""","remindAfter":"P1D" """,
            quality: ""","remindAfter":"PT12H" """);
        var (h, store) = await OpenAsync(stages);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Doc(100), Step(SubmitEvent, "ana"), Time("PT6H"), Time("P1D"), Time("P1D"), Step(ApproveEvent, "ben"),
            Step(ApproveEvent, "quinn"), Time("P5D"), Step(ApproveEvent, "dee", ""","response":"reject" """), Time("P1D"), Step(ApproveEvent, "quinn"), Time("P1D"));
        var t = result.Trace;
        Assert.Empty(Notices(t[2]));
        Assert.Null(t[2].Notices);
        // Quality fell due first in time, but the trace lists file order: stage, then signature.
        Assert.Equal([(Engineering, NoticeKind.Remind), (Quality, NoticeKind.Remind)], Notices(t[3]));
        Assert.Equal([Engineer], t[3].Notices![0].Recipients);
        Assert.Empty(Notices(t[4])); // once per round start
        // Round 2 started with Quinn's signature; both of the controller's fall due in one step, a reminder before an escalation.
        Assert.Equal([(ControllerSig, NoticeKind.Remind), (ControllerSig, NoticeKind.Escalate)], Notices(t[7]));
        Assert.Equal([Controller], t[7].Notices![0].Recipients);
        Assert.Equal([Director], t[7].Notices![1].Recipients);
        // The rejection starts round 1 again, so its reminders fall due again; a complete signature has none.
        Assert.Equal([(Engineering, NoticeKind.Remind), (Quality, NoticeKind.Remind)], Notices(t[9]));
        Assert.Empty(Notices(t[11]));
        var text = JsonSerializer.Serialize(t[7].Notices, EngineJson.Options);
        Assert.Equal($$"""[{"signature":"{{ControllerSig}}","kind":"remind","recipients":["{{Controller}}"]},{"signature":"{{ControllerSig}}","kind":"escalate","recipients":["{{Director}}"]}]""", text);

        // A reminder due before a timer leaves the state falls due; nothing falls due after leaving.
        var timer = $$""",{"id":"{{Expire}}","source":"{{Review}}","trigger":"after","after":"P3D","targets":["{{Draft}}"]}""";
        var (h2, timed) = await OpenAsync(stages, moreTransitions: timer);
        using var ___ = h2;
        await using var ____ = timed;
        var expired = await SimulateAsync(timed, Doc(100), Step(SubmitEvent, "ana"), Time("P10D"));
        Assert.Equal([Draft], expired.Trace[2].Configuration);
        Assert.Equal([(Engineering, NoticeKind.Remind), (Quality, NoticeKind.Remind)], Notices(expired.Trace[2]));
    }

    [Fact]
    public async Task Recording_writes_expect_notices_only_where_due_and_replay_compares_them_as_a_set()
    {
        var stages = RoundStages(engineering: ""","remindAfter":"P1D" """, quality: ""","remindAfter":"P1D" """);
        var (h, store) = await OpenAsync(stages);
        using var _ = h;
        await using var __ = store;
        var recorded = await RecordAsync(store, Doc(100), Step(SubmitEvent, "ana"), Time("P2D"), Time("P1D"));
        var steps = recorded.GetProperty("steps");
        Assert.False(steps[0].GetProperty("expect").TryGetProperty("notices", out var none0), none0.ToString());
        Assert.Equal($$"""[{"signature":"{{Engineering}}","kind":"remind"},{"signature":"{{Quality}}","kind":"remind"}]""",
            JsonSerializer.Serialize(steps[1].GetProperty("expect").GetProperty("notices")));
        Assert.False(steps[2].GetProperty("expect").TryGetProperty("notices", out var none2), none2.ToString());

        var scenario = recorded.Deserialize<Scenario>(EngineJson.Options)!;
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        Assert.True(ScenarioReplayer.Replay(scenario, runtime)!.Passed);
        ScenarioStep Expecting(int i, params ExpectedNotice[] notices) => scenario.Steps[i] with { Expect = scenario.Steps[i].Expect! with { Notices = notices } };
        var reversed = scenario with
        {
            Steps = [scenario.Steps[0], Expecting(1, new ExpectedNotice { Signature = Quality, Kind = NoticeKind.Remind }, new ExpectedNotice { Signature = Engineering, Kind = NoticeKind.Remind }), scenario.Steps[2]],
        };
        Assert.True(ScenarioReplayer.Replay(reversed, runtime)!.Passed);
        var wrong = scenario with { Steps = [scenario.Steps[0], scenario.Steps[1], Expecting(2, new ExpectedNotice { Signature = Engineering, Kind = NoticeKind.Remind })] };
        var failure = ScenarioReplayer.Replay(wrong, runtime)!.Failure;
        Assert.Equal(("MQ9311", 2), (failure?.Rule, failure!.Step));
        Assert.Contains("brings nothing due, but expect.notices lists a reminder on signature 'Engineering'", failure.Message, StringComparison.Ordinal);
        var empty = scenario with { Steps = [scenario.Steps[0], Expecting(1), scenario.Steps[2]] };
        Assert.Equal("MQ9311", ScenarioReplayer.Replay(empty, runtime)!.Failure?.Rule); // an empty list expects none
    }

    [Fact]
    public async Task Recording_and_replay_keep_a_delegation_and_bad_delegation_keys_are_MQ9305()
    {
        var (h, store) = await OpenAsync(RoundStages(controller: Delegable()));
        using var _ = h;
        await using var __ = store;
        var recorded = await RecordAsync(store, Doc(100), Step(SubmitEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"),
            Step(ApproveEvent, "dee", ""","response":"delegate","delegateTo":"vic" """), Step(ApproveEvent, "vic", ""","onBehalfOf":"dee" """));
        var steps = recorded.GetProperty("steps");
        // The participants are recorded under new ids, and the delegation's keys follow them.
        var ids = recorded.GetProperty("participants").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("id").GetString()!);
        Assert.Equal(("delegate", ids["Vic"]), (steps[3].GetProperty("response").GetString(), steps[3].GetProperty("delegateTo").GetString()));
        Assert.Equal(ids["Dee"], steps[4].GetProperty("onBehalfOf").GetString());
        var scenario = recorded.Deserialize<Scenario>(EngineJson.Options)!;
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        var replay = ScenarioReplayer.Replay(scenario, runtime)!;
        Assert.True(replay.Passed, string.Join(" | ", replay.Diagnostics.Select(d => d.Message)));
        Assert.Equal("Dee", replay.Steps[4].Audit[0].OnBehalfOf);

        var delegate3 = scenario.Steps[3];
        var behalf4 = scenario.Steps[4];
        (string Field, ScenarioStep Step)[] bad =
        [
            ("/delegateTo", delegate3 with { Response = SignatureResponse.Sign }),
            ("/response", delegate3 with { DelegateTo = null }),
            ("/onBehalfOf", delegate3 with { OnBehalfOf = ids["Ben"] }),
            ("/signature", delegate3 with { Signature = Engineering }),
            ("/delegateTo", delegate3 with { DelegateTo = "nobody" }),
            ("/onBehalfOf", behalf4 with { OnBehalfOf = "nobody" }),
        ];
        foreach (var (field, step) in bad)
        {
            var found = ScenarioReplayer.Replay(scenario with { Steps = [.. scenario.Steps.Take(3), step] }, runtime)!;
            Assert.Contains(found.Diagnostics, d => d.Rule == "MQ9305" && d.JsonPointer == "/steps/3" + field);
        }

        // A gate whose signatures can't be delegated takes neither a delegation nor an answer on behalf.
        var (h2, plain) = await OpenAsync(Part1Stages);
        using var ___ = h2;
        await using var ____ = plain;
        using var plainRuntime = new ProcessRuntime(plain.Current!, 1, Ct);
        foreach (var (field, step) in new[] { ("/response", delegate3), ("/onBehalfOf", behalf4) })
        {
            var found = ScenarioReplayer.Replay(scenario with { Steps = [scenario.Steps[0], step] }, plainRuntime)!;
            Assert.Contains(found.Diagnostics, d => d.Rule == "MQ9305" && d.JsonPointer == "/steps/1" + field && d.Message.Contains("delegateTo", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_model_that_uses_part_1_only_keeps_its_answers_byte_for_byte()
    {
        var (h, store) = await OpenAsync(Part1Stages);
        using var _ = h;
        await using var __ = store;
        JsonElement[] steps =
        [
            Step(SubmitEvent, "ana"), Step(ApproveEvent, "ana"), Step(ApproveEvent, "ben"), Step(ApproveEvent, "cleo", ""","response":"ignore" """),
            Step(ApproveEvent, "ben", ""","response":"withdraw" """), Time("P3D"),
            Step(ApproveEvent, "dee", $$""","response":"reject","signature":"{{Finance}}","reason":"Cost not approved." """),
            Step(ApproveEvent, "ben"), Step(ApproveEvent, "quinn"), Time("P1D"), Step(ApproveEvent, "dee"), Step(ApproveEvent, "cleo"),
        ];
        var simulated = await SimulateAsync(store, Doc(12000), steps);
        var text = JsonSerializer.Serialize(simulated with { ProcessHash = "" }, EngineJson.Options);
        foreach (var member in new[] { "\"notices\"", "\"round\"", "\"skipped\"", "\"onBehalfOf\"", "\"delegateTo\"", "\"delegate\"", "\"delegated\"" })
            Assert.DoesNotContain(member, text, StringComparison.Ordinal);
        var recorded = await RecordAsync(store, Doc(12000), steps);
        Assert.DoesNotContain("\"notices\"", recorded.GetRawText(), StringComparison.Ordinal);
        var scenario = recorded.Deserialize<Scenario>(EngineJson.Options)!;
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        var replay = ScenarioReplayer.Replay(scenario, runtime)!;
        Assert.True(replay.Passed, string.Join(" | ", replay.Diagnostics.Select(d => d.Message)));
        var replayed = JsonSerializer.Serialize(replay.Steps, EngineJson.Options);
        Assert.Equal(Part1Hashes, (Hash(text), Hash(recorded.GetRawText()), Hash(replayed)));
    }

    // The simulation answer, the recording and the replayed traces of a part 1 approval, taken before part 2 was built.
    private static readonly (string, string, string) Part1Hashes =
        ("3E6E66F587432EAAFCF13FBEF79C7AA58C0CEFDE902722FCB37DD55FFEE97AB0", "31297A421638D89622B031EE7E191D869996B71448A0994E02A5A860F6519372",
            "094500FC914338A6BABD0A05CD9DC20FFC637F99A41302EB8840DF00B07D65B3");
}
