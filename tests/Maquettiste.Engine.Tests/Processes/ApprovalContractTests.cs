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
/// The contract of approvals, part 1 (signoff-design.md sections 5, 6 and 14): the schemas of a gate's stages and named signatures, a step's
/// response and signature and its expected signatures; a model holding them loads, indexes its parts and references, names its stages and
/// signatures and round-trips; a gate without stages keeps its schema finding word for word; the trace, audit and gate state members are left
/// out when unused; and the rule catalog holds the approval rules.
/// </summary>
public sealed class ApprovalContractTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle
    private const string OrderId = "01JATT00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001";
    private const string Manager = "01JACT00000000000000000002";
    private const string Finance = "01JACT00000000000000000003";
    private const string Review = "01JSTA00000000000000000003";
    private const string Submit = "01JTRN00000000000000000001";
    private const string Approve = "01JTRN00000000000000000006";
    private const string ApproveEvent = "01JPRX00000000000000000002";
    private const string Gate = "01JPRX00000000000000000041";
    private const string Stage = "01JGST00000000000000000001";
    private const string Managers = "01JNSG00000000000000000001";
    private const string FinanceCheck = "01JNSG00000000000000000002";
    private const string Scenario = "01JSCN00000000000000000051";
    private const string RejectStep = "01JSTP00000000000000000051";
    private const string SignStep = "01JSTP00000000000000000052";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string ScenarioFile = "model/scenarios/order-lifecycle/approval.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Stages = $$"""
        [{"id":"{{Stage}}","name":"Review","signatures":[
           {"id":"{{Managers}}","name":"Managers","signers":["{{Manager}}"],"required":2,"notBy":[{"verb":"promote"},{"transition":"{{Submit}}"}],
            "description":"Two managers who did not submit it."},
           {"id":"{{FinanceCheck}}","name":"FinanceCheck","displayName":"Finance check","signers":["{{Finance}}"],"requiredActors":["{{Finance}}"],
            "allowRepeatSigner":true,"when":{"op":"isNotNull","args":[{"path":["{{OrderId}}"]}]} }]}]
        """;

    private static readonly string StepsScenario = $$"""
        {"kind":"scenario","id":"{{Scenario}}","name":"Approval","process":"{{P}}",
         "steps":[
           {"id":"{{SignStep}}","event":"{{ApproveEvent}}","actor":"{{Manager}}","signer":"ana","reason":"Fine.",
            "expect":{"signatures":{"{{Managers}}":1} } },
           {"id":"{{RejectStep}}","event":"{{ApproveEvent}}","actor":"{{Finance}}","signer":"dee","signature":"{{FinanceCheck}}","response":"reject","reason":"No.",
            "expect":{"states":["{{Review}}"],"signatures":{"{{Managers}}":0,"{{FinanceCheck}}":0} } }]}
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

    /// <summary>A library holding one gate policy with the given body after its id.</summary>
    private static string Library(string gate) =>
        $$"""{"kind":"process","id":"{{P}}","name":"Policies","use":"library","gates":[{"id":"{{Gate}}",{{gate}}}]}""";

    private const string Meanings = """ "meanings":[{"id":"01JPRX00000000000000000051","name":"approved"}] """;

    private static string Approval(string signature, string gateExtra = "") =>
        Library($$""" "name":"Release",{{Meanings}},{{gateExtra}}"stages":[{"id":"{{Stage}}","name":"Review","signatures":[{"id":"{{Managers}}","name":"Managers",{{signature}}}]}] """);

    private static IReadOnlyList<NameEntry> Find(ModelSnapshot model, string text)
    {
        Assert.True(QualifiedName.TryParse(text, out var name, out var error), error);
        return model.Names(name.Kind).Find(name.Path);
    }

    // ---- schemas ----

    [Fact]
    public void A_gate_takes_one_stage_of_named_signatures_and_leaves_their_shape_to_MQ8201_and_MQ8202()
    {
        Valid("process.json", Library($$""" "name":"Release",{{Meanings}},"stages":{{Stages}},"allowSignerAcrossSignatures":true """));
        Valid("process.json", Approval($$""" "signers":["{{Manager}}"] """));

        // Keys on the wrong kind of gate pass the schema: MQ8201 reports them, with the gate's id to point at.
        Valid("process.json", Approval($$""" "signers":["{{Manager}}"] """, $$""" "signers":["{{Manager}}"],"required":2, """));
        Valid("process.json", Library($$""" "name":"Release",{{Meanings}},"signers":["{{Manager}}"],"allowSignerAcrossSignatures":true """));
        // A gate policy's when and notBy transition pass too: MQ8202 reports them.
        Valid("process.json", Approval($$""" "signers":["{{Manager}}"],"when":{"value":true},"notBy":[{"transition":"{{Submit}}"}] """));

        // Part 1 holds exactly one stage; part 2's keys are refused until then.
        var stage = $$"""{"id":"01JGST00000000000000000002","name":"Again","signatures":[{"id":"01JNSG00000000000000000009","name":"X","signers":["{{Manager}}"]}]}""";
        Invalid("process.json", Library($$""" "name":"Release",{{Meanings}},"stages":[{{stage}},{{stage}}] """), "/gates/0/stages");
        Invalid("process.json", Library($$""" "name":"Release",{{Meanings}},"stages":[] """), "/gates/0/stages");
        Invalid("process.json", Library($$""" "name":"Release",{{Meanings}},"stages":[{"id":"{{Stage}}","name":"Review","signatures":[]}] """), "/gates/0/stages/0/signatures");
        Invalid("process.json", Library($$""" "name":"Release",{{Meanings}},"stages":[{"id":"{{Stage}}","name":"Review","round":2,"signatures":[{"id":"{{Managers}}","name":"M","signers":["{{Manager}}"]}]}] """), "/gates/0/stages/0");
        Invalid("process.json", Approval($$""" "signers":["{{Manager}}"],"delegateTo":["{{Finance}}"] """), "/gates/0/stages/0/signatures/0");
        Invalid("process.json", Approval($$""" "signers":["{{Manager}}"],"reauthenticate":true """), "/gates/0/stages/0/signatures/0");

        // A signature's own shape.
        Invalid("process.json", Approval(""" "required":1 """), "/gates/0/stages/0/signatures/0");
        Invalid("process.json", Approval(""" "signers":[] """), "/gates/0/stages/0/signatures/0/signers");
        Invalid("process.json", Approval($$""" "signers":["{{Manager}}"],"required":0 """), "/gates/0/stages/0/signatures/0/required");
        Invalid("process.json", Approval($$""" "signers":["{{Manager}}"],"notBy":[{"verb":"promote","transition":"{{Submit}}"}] """), "/gates/0/stages/0/signatures/0/notBy/0");
        Invalid("process.json", Approval($$""" "signers":["{{Manager}}"],"notBy":[{}] """), "/gates/0/stages/0/signatures/0/notBy/0");
        Invalid("process.json", Approval($$""" "signers":["{{Manager}}"],"notBy":[{"verb":"Promote"}] """), "/gates/0/stages/0/signatures/0/notBy/0");
        Invalid("process.json", Approval($$""" "signers":["{{Manager}}"],"notBy":[{"transition":"Submit"}] """), "/gates/0/stages/0/signatures/0/notBy/0");
        Invalid("process.json", Library($$""" "name":"Release",{{Meanings}},"stages":{{Stages}},"allowSignerAcrossSignatures":"yes" """), "/gates/0/allowSignerAcrossSignatures");
    }

    [Fact]
    public void A_gate_without_stages_or_signers_keeps_its_finding_word_for_word()
    {
        // The finding the meaning's own required list gives, as the template every required list words.
        var meaning = Assert.Single(Evaluate("process.json", Library($$""" "name":"Release","signers":["{{Manager}}"],"meanings":[{"id":"01JPRX00000000000000000051"}] """)));
        Assert.Equal("/gates/0/meanings/0 required: Required properties [\"name\"] are not present", meaning.Message);

        // A gate with neither signers nor stages: one finding, at the gate, naming signers alone, as when signers sat in its required list.
        var gate = Assert.Single(Evaluate("process.json", Library($$""" "name":"Release",{{Meanings}} """)));
        Assert.Equal(("MQ1002", "/gates/0"), (gate.Rule, gate.JsonPointer));
        Assert.Equal(meaning.Message.Replace("/meanings/0", "", StringComparison.Ordinal).Replace("\"name\"", "\"signers\"", StringComparison.Ordinal), gate.Message);

        // A gate with stages needs no signers; its other keys stay required.
        var staged = Assert.Single(Evaluate("process.json", Library($$""" "displayName":"Release","stages":{{Stages}} """)));
        Assert.Equal(gate.Message.Replace("[\"signers\"]", "[\"name\",\"meanings\"]", StringComparison.Ordinal), staged.Message);
    }

    [Fact]
    public void A_step_answers_an_approval_with_a_response_and_a_signature_and_expects_signatures()
    {
        Valid("scenario.json", StepsScenario);

        string Step(string step) => $$"""{"kind":"scenario","id":"{{Scenario}}","name":"S","process":"{{P}}","steps":[{"id":"{{SignStep}}",{{step}}}]}""";
        foreach (var response in new[] { "sign", "reject", "ignore", "withdraw" })
            Valid("scenario.json", Step($$"""  "event":"{{ApproveEvent}}","response":"{{response}}" """));
        Valid("scenario.json", Step("""  "expect":{"accepted":false,"refusal":"gate-excluded"} """));
        Valid("scenario.json", Step("""  "expect":{"accepted":false,"refusal":"gate-not-signed"} """));
        Valid("scenario.json", Step("""  "expect":{"signatures":{}} """));
        Invalid("scenario.json", Step("""  "response":"delegate" """), "/steps/0/response");
        Invalid("scenario.json", Step("""  "response":"approve" """), "/steps/0/response");
        Invalid("scenario.json", Step("""  "signature":"Managers" """), "/steps/0/signature");
        Invalid("scenario.json", Step($$"""  "expect":{"signatures":{"{{Managers}}":-1} } """), "/steps/0/expect/signatures");
        Invalid("scenario.json", Step($$"""  "expect":{"signatures":{"{{Managers}}":"1"} } """), "/steps/0/expect/signatures");
        Invalid("scenario.json", Step("""  "expect":{"signatures":{"Managers":1}} """), "/steps/0/expect/signatures");
        Invalid("scenario.json", Step("""  "expect":{"accepted":false,"refusal":"gate-delegated"} """), "/steps/0/expect/refusal");
    }

    // ---- loading ----

    [Fact]
    public async Task An_approval_and_its_steps_load_index_and_round_trip_in_schema_order()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        var process = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        var gate = process["transitions"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == Approve)!["gate"]!.AsObject();
        foreach (var key in new[] { "required", "signers", "requiredActors" })
            gate.Remove(key);
        gate["stages"] = JsonNode.Parse(Stages);
        gate["allowSignerAcrossSignatures"] = true;
        h.Write(ProcessFile, Canonical(process.ToJsonString(), "process.json", ProcessFile));
        h.Write(ScenarioFile, Canonical(StepsScenario, "scenario.json", ScenarioFile));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var model = store.Current!;

        foreach (var file in new[] { ProcessFile, ScenarioFile })
            Assert.DoesNotContain(model.LoadDiagnostics, d => (d.FilePath ?? "").EndsWith(file, StringComparison.Ordinal) && d.Rule is "MQ1002" or "MQ1003");

        // The files keep their keys in schema order: stages after the gate's name, the flag after the repeat flag, and each part's own.
        var written = JsonNode.Parse(h.Read(ProcessFile))!["transitions"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == Approve)!["gate"]!.AsObject();
        Assert.Equal(["id", "name", "stages", "allowSignerAcrossSignatures", "reasonRequired", "meanings", "auditAttributes"], written.Select(p => p.Key));
        var signatures = written["stages"]![0]!["signatures"]!.AsArray();
        Assert.Equal(["id", "name", "signatures"], written["stages"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "name", "signers", "required", "notBy", "description"], signatures[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "name", "displayName", "signers", "requiredActors", "allowRepeatSigner", "when"], signatures[1]!.AsObject().Select(p => p.Key));
        var steps = JsonNode.Parse(h.Read(ScenarioFile))!["steps"]!.AsArray();
        Assert.Equal(["id", "event", "actor", "signer", "reason", "expect"], steps[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "event", "actor", "signer", "response", "signature", "reason", "expect"], steps[1]!.AsObject().Select(p => p.Key));
        Assert.Equal(["states", "signatures"], steps[1]!["expect"]!.AsObject().Select(p => p.Key));

        // The records.
        var lifecycle = Assert.IsType<Process>(model.GetDocument(P)!.Element);
        var approval = lifecycle.Transitions.Single(t => t.Id == Approve).Gate!;
        Assert.True(approval.UsesStages);
        Assert.True(approval.AllowSignerAcrossSignatures);
        Assert.Empty(approval.Signers);
        Assert.Empty(approval.RequiredActors);
        var stage = Assert.Single(approval.Stages!);
        Assert.Equal((Stage, "Review"), (stage.Id, stage.Name));
        Assert.Equal([Managers, FinanceCheck], stage.Signatures.Select(s => s.Id));
        var managers = stage.Signatures[0];
        Assert.Equal([Manager], managers.Signers);
        Assert.Equal((2, false, (Expr?)null), (managers.Required, managers.AllowRepeatSigner, managers.When));
        Assert.Equal([Managers, FinanceCheck], approval.AllSignatures.Select(s => s.Id));
        Assert.Equal([("promote", (string?)null), (null, Submit)], managers.NotBy.Select(n => (n.Verb, n.Transition)));
        Assert.Equal("Two managers who did not submit it.", managers.Description!.Text);
        var finance = stage.Signatures[1];
        Assert.Equal((1, true, "Finance check"), (finance.Required, finance.AllowRepeatSigner, finance.DisplayName));
        Assert.Equal([Finance], finance.RequiredActors);
        Assert.Equal("isNotNull", finance.When!.Op);
        Assert.False(lifecycle.Transitions.Single(t => t.Id == Submit).Gate is { UsesStages: true });
        var read = Assert.IsType<Maquettiste.Engine.Model.Scenario>(model.GetDocument(Scenario)!.Element).Steps;
        Assert.Equal((SignatureResponse.Sign, (string?)null), (read[0].Response, read[0].Signature));
        Assert.Equal(1, read[0].Expect!.Signatures![Managers]);
        Assert.Equal((SignatureResponse.Reject, FinanceCheck), (read[1].Response, read[1].Signature));
        Assert.Equal([(Managers, 0), (FinanceCheck, 0)], read[1].Expect!.Signatures!.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal));

        // The records write back to the same bytes.
        foreach (var (path, element, schema) in new[] { (ProcessFile, (object)lifecycle, "process.json"), (ScenarioFile, model.GetDocument(Scenario)!.Element, "scenario.json") })
        {
            var node = JsonSerializer.SerializeToNode(element, element.GetType(), EngineJson.Options)!;
            Assert.Equal(h.Read(path), Encoding.UTF8.GetString(TestServices.Json.Write(node, schema, ".maquettiste/" + path)));
        }

        // The index: a stage is a part of kind gate-stage, a signature of kind named-signature, both owned by the process.
        var gatePointer = model.Index.Entries[Gate].JsonPointer;
        Assert.Equal(("gate-stage", P, gatePointer + "/stages/0"), (model.Index.Entries[Stage].Kind, model.Index.Entries[Stage].OwnerId, model.Index.Entries[Stage].JsonPointer));
        Assert.Equal(("named-signature", P, gatePointer + "/stages/0/signatures/1"),
            (model.Index.Entries[FinanceCheck].Kind, model.Index.Entries[FinanceCheck].OwnerId, model.Index.Entries[FinanceCheck].JsonPointer));

        // The ids a signature names are references, and so are a step's signature and the keys of its expected signatures.
        Assert.Contains(await store.GetReferencesAsync(Manager, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Managers, gatePointer + "/stages/0/signatures/0/signers/0"));
        Assert.Contains(await store.GetReferencesAsync(Finance, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, FinanceCheck, gatePointer + "/stages/0/signatures/1/requiredActors/0"));
        Assert.Contains(await store.GetReferencesAsync(Submit, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Managers, gatePointer + "/stages/0/signatures/0/notBy/1/transition"));
        Assert.Contains(await store.GetReferencesAsync(FinanceCheck, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (Scenario, RejectStep, "/steps/1/signature"));
        Assert.Contains(await store.GetReferencesAsync(Managers, Ct), r => (r.FromElementId, r.FromId) == (Scenario, SignStep));

        // A stage and a signature take the gate's forms.
        Assert.Equal(Stage, Assert.Single(Find(model, "@gate-stage:OrderLifecycle.approval.Review")).Id);
        Assert.Equal(FinanceCheck, Assert.Single(Find(model, "@named-signature:OrderLifecycle.approval.FinanceCheck")).Id);
        Assert.Equal(Managers, Assert.Single(Find(model, "@named-signature:Sales.OrderLifecycle.approval.Managers")).Id);

        // The index row flags the inline approval for the approvals naming key; it uses no gate policy.
        var row = model.Index.Summaries.Single(d => d.Id == P);
        Assert.True(row.InlineApproval);
        Assert.Null(row.GatePolicyUses);
        Assert.Null(row.ApprovalGatePolicies);
    }

    [Fact]
    public async Task A_model_without_approvals_indexes_as_before()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        var before = h.Read(ProcessFile);
        await using var store = new ModelStore(h.Options, h.Services());
        await store.LoadAsync(Ct);
        var model = store.Current!;
        var row = model.Index.Summaries.Single(d => d.Id == P);
        Assert.Equal((false, (IReadOnlyList<string>?)null, (IReadOnlyList<string>?)null), (row.InlineApproval, row.GatePolicyUses, row.ApprovalGatePolicies));
        var gate = Assert.IsType<Process>(model.GetDocument(P)!.Element).Transitions.Single(t => t.Id == Approve).Gate!;
        Assert.Equal((false, 2, false), (gate.UsesStages, gate.Required, gate.AllowSignerAcrossSignatures));
        Assert.Null(gate.Stages);
        Assert.Empty(gate.AllSignatures);
        Assert.Equal([Manager, Finance], gate.Signers);
        var node = JsonSerializer.SerializeToNode(model.GetDocument(P)!.Element, typeof(Process), EngineJson.Options)!;
        Assert.Equal(before, Encoding.UTF8.GetString(TestServices.Json.Write(node, "process.json", ".maquettiste/" + ProcessFile)));
    }

    // ---- the trace, the audit and the gate state ----

    [Fact]
    public void The_new_trace_audit_and_gate_state_members_are_left_out_when_unused()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var step = new ScenarioStep { Id = SignStep, Event = ApproveEvent, Expect = new StepExpectation() };
        var audit = new GateAuditRecord("i-1", P, Gate, Approve, 1, "ana", Manager, null, "Fine.", "2000-01-01T00:00:00Z", GateOutcome.Signed,
            new Dictionary<string, JsonElement>());
        var trace = new StepTrace(0, step, true, null, [], [], [audit], [Review], new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(), "2000-01-01T00:00:00Z", false, []);

        var plain = JsonNode.Parse(JsonSerializer.Serialize(trace, web))!.AsObject();
        Assert.False(plain["input"]!.AsObject().ContainsKey("response") || plain["input"]!.AsObject().ContainsKey("signature"));
        Assert.False(plain["input"]!["expect"]!.AsObject().ContainsKey("signatures"));
        Assert.Equal(
            ["instance", "process", "gate", "transition", "sequence", "signer", "actor", "meaning", "reason", "at", "outcome", "attributes"],
            plain["audit"]![0]!.AsObject().Select(p => p.Key));
        var state = new GateState(Gate, Approve, [new GateSignature("ana", Manager, null, null)], false);
        var stateJson = JsonNode.Parse(JsonSerializer.Serialize(state, web))!.AsObject();
        Assert.Equal(["gate", "transition", "signatures", "satisfied"], stateJson.Select(p => p.Key));
        Assert.Equal(["signer", "actor", "meaning", "reason"], stateJson["signatures"]![0]!.AsObject().Select(p => p.Key));
        var enabled = new EnabledTrigger("event", ApproveEvent, null, [Approve], [], false, new GateProgress(0, 2));
        Assert.DoesNotContain("responses", JsonNode.Parse(JsonSerializer.Serialize(enabled, web))!.AsObject().Select(p => p.Key));

        // On an approval they are written.
        var rejected = trace with
        {
            Input = step with { Response = SignatureResponse.Reject, Signature = FinanceCheck, Expect = new StepExpectation { Signatures = new Dictionary<string, int> { [Managers] = 0 } } },
            Audit = [audit with { Outcome = GateOutcome.Rejected, Signature = FinanceCheck, Stage = Stage }],
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(rejected, web))!.AsObject();
        Assert.Equal(("reject", FinanceCheck), (json["input"]!["response"]!.GetValue<string>(), json["input"]!["signature"]!.GetValue<string>()));
        Assert.Equal($$"""{"{{Managers}}":0}""", json["input"]!["expect"]!["signatures"]!.ToJsonString());
        Assert.Equal(("rejected", FinanceCheck, Stage),
            (json["audit"]![0]!["outcome"]!.GetValue<string>(), json["audit"]![0]!["signature"]!.GetValue<string>(), json["audit"]![0]!["stage"]!.GetValue<string>()));
        Assert.Equal(["ignored", "withdrawn"], new[] { GateOutcome.Ignored, GateOutcome.Withdrawn }.Select(o => JsonSerializer.Serialize(o, web).Trim('"')));

        var approvalState = (state with { Signatures = [new GateSignature("ana", Manager, null, null) { Signature = Managers }] }) with
        {
            Stages = [new ApprovalStageState(Stage, false, [new ApprovalSignatureState(Managers, true, 1, 2, ["ana"], false), new ApprovalSignatureState(FinanceCheck, false, 0, 1, [], false)])],
        };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            {"gate":"{{Gate}}","transition":"{{Approve}}","signatures":[{"signer":"ana","actor":"{{Manager}}","meaning":null,"reason":null,"signature":"{{Managers}}"}],
             "satisfied":false,"stages":[{"stage":"{{Stage}}","complete":false,"signatures":[
               {"signature":"{{Managers}}","needed":true,"counted":1,"required":2,"finished":["ana"],"complete":false},
               {"signature":"{{FinanceCheck}}","needed":false,"counted":0,"required":1,"finished":[],"complete":false}]}]}
            """), JsonNode.Parse(JsonSerializer.Serialize(approvalState, web))), JsonSerializer.Serialize(approvalState, web));
        var responses = enabled with { Responses = [SignatureResponse.Sign, SignatureResponse.Reject, SignatureResponse.Ignore, SignatureResponse.Withdraw] };
        Assert.Equal("""["sign","reject","ignore","withdraw"]""", JsonNode.Parse(JsonSerializer.Serialize(responses, web))!["responses"]!.ToJsonString());

        // The engine's refusals follow row 7's order, a trigger point's check still last, and the schema allows the new ones.
        Assert.Equal(
            [Refusals.GateSigner, Refusals.GateExcluded, Refusals.GateRepeat, Refusals.GateNotSigned, Refusals.GateReason, Refusals.Check],
            Refusals.All.SkipWhile(r => r != Refusals.GateSigner));
        Assert.Equal(("gate-excluded", "gate-not-signed"), (Refusals.GateExcluded, Refusals.GateNotSigned));
        var schema = JsonNode.Parse(TestServices.Schemas.GetFileBytes("scenario.json").Span)!["$defs"]!["step"]!["properties"]!["expect"]!["properties"]!["refusal"]!["enum"]!;
        Assert.Equal(Refusals.All.Order(StringComparer.Ordinal), schema.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    // ---- rules ----

    [Fact]
    public void The_rule_catalog_holds_the_approval_rules_in_their_own_family()
    {
        Assert.Equal(
            [("MQ8201", DiagnosticSeverity.Error), ("MQ8202", DiagnosticSeverity.Error)],
            RuleCatalog.All.Where(r => r.Id.StartsWith("MQ82", StringComparison.Ordinal)).Select(r => (r.Id, r.DefaultSeverity)));
        Assert.Equal("Approvals", RuleCatalog.FamilyLabel("MQ82xx"));
        Assert.Equal((DiagnosticSeverity.Error, "refresh-scenario"), (RuleCatalog.Get("MQ9311").DefaultSeverity, RuleCatalog.Get("MQ9311").QuickFix));
        Assert.True(RuleCatalog.IsReplayFinding("MQ9311"));
        Assert.False(RuleCatalog.IsReplayFinding("MQ8201"));
        Assert.Contains("allowSignerAcrossSignatures", RuleCatalog.Get("MQ8201").Description, StringComparison.Ordinal);
        Assert.Contains("gate policy", RuleCatalog.Get("MQ8202").Description, StringComparison.Ordinal);
        foreach (var id in new[] { "MQ3102", "MQ3104", "MQ9102", "MQ9105", "MQ9106", "MQ9701", "MQ9704" })
            Assert.Contains("named signature", RuleCatalog.Get(id).Description, StringComparison.Ordinal);
        Assert.Contains("allowSignerAcrossSignatures", RuleCatalog.Get("MQ9101").Description, StringComparison.Ordinal);
        Assert.Contains("stages", RuleCatalog.Get("MQ3001").Description, StringComparison.Ordinal);
        Assert.Contains("expect.signatures", RuleCatalog.Get("MQ9305").Description, StringComparison.Ordinal);
        Assert.Contains("gate-not-signed", RuleCatalog.Get("MQ9308").Description, StringComparison.Ordinal);
        Assert.Contains("approval", RuleCatalog.Get("MQ8102").Description, StringComparison.Ordinal);
        Assert.Contains("approval", RuleCatalog.Get("MQ8107").Description, StringComparison.Ordinal);
        Assert.Equal(RuleCatalog.All.Select(r => r.Id).Order(StringComparer.Ordinal), RuleCatalog.All.Select(r => r.Id));
    }
}
