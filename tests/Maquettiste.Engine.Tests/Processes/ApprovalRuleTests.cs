using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Approvals' expression site and rules (signoff-design.md decision 7 and section 6): a named signature's when at the approval site and the
/// session that judges it; MQ8201 and MQ8202; the new cases of MQ3001, MQ3101 to MQ3108, MQ9101, MQ9102, MQ9105, MQ9106, MQ9305, MQ9701,
/// MQ9704, MQ8102 and MQ8107; and the same findings, word for word, for gates that use only today's fields.
/// </summary>
public sealed class ApprovalRuleTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle
    private const string Library = "01JPRC00000000000000000061"; // Policies
    private const string Order = "01JENT00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001"; // a person
    private const string Manager = "01JACT00000000000000000002"; // a role
    private const string Finance = "01JACT00000000000000000003"; // a role
    private const string Ana = "01JACT00000000000000000061"; // a person
    private const string Ben = "01JACT00000000000000000062"; // a person
    private const string Auditor = "01JACT00000000000000000063"; // a person no process names but a signature
    private const string Amount = "01JATT00000000000000000011"; // context, decimal
    private const string Note = "01JATT00000000000000000021"; // submit's payload
    private const string Draft = "01JSTA00000000000000000001";
    private const string Submit = "01JTRN00000000000000000001";
    private const string Approve = "01JTRN00000000000000000006";
    private const string ApproveEvent = "01JPRX00000000000000000002";
    private const string Gate = "01JPRX00000000000000000041";
    private const string Policy = "01JPRX00000000000000000061";
    private const string Stage = "01JGST00000000000000000001";
    private const string Managers = "01JNSG00000000000000000001";
    private const string Controller = "01JNSG00000000000000000002";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string LibraryFile = "model/processes/policies.json";
    private const string Meanings = """ "meanings":[{"id":"01JPRX00000000000000000051","name":"approved"}] """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One stage named Review holding the given signatures.</summary>
    private static string Stages(params string[] signatures) => $$"""[{"id":"{{Stage}}","name":"Review","signatures":[{{string.Join(",", signatures)}}]}]""";

    private static string Signature(string id, string name, string rest) => $$"""{"id":"{{id}}","name":"{{name}}",{{rest}}}""";

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<JsonObject>? gate, Action<LoaderHarness>? more = null,
        Action<JsonObject>? process = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        foreach (var (id, name) in new[] { (Ana, "Ana"), (Ben, "Ben"), (Auditor, "Auditor") })
            h.Write("model/actors/" + name.ToLowerInvariant() + ".json", $$"""{"kind":"actor","id":"{{id}}","name":"{{name}}","type":"person"}""");
        var document = JsonNode.Parse(h.Read(ProcessFile))!.AsObject();
        var transition = document["transitions"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == Approve)!.AsObject();
        if (gate is not null)
            gate(transition["gate"]!.AsObject());
        process?.Invoke(document);
        h.Write(ProcessFile, document.ToJsonString());
        more?.Invoke(h);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        return (h, store);
    }

    private static async Task<List<Diagnostic>> ValidateAsync(Action<JsonObject>? gate, Action<LoaderHarness>? more = null, Action<JsonObject>? process = null)
    {
        var (h, store) = await OpenAsync(gate, more, process);
        using var _ = h;
        await using var __ = store;
        return [.. (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics];
    }

    /// <summary>Turns the fixture's gate into an approval with the given stages and gate keys.</summary>
    private static Action<JsonObject> AsApproval(string stages, Action<JsonObject>? extra = null) => gate =>
    {
        foreach (var key in new[] { "required", "signers", "requiredActors" })
            gate.Remove(key);
        gate["stages"] = JsonNode.Parse(stages);
        extra?.Invoke(gate);
    };

    private static List<string> Hits(IEnumerable<Diagnostic> diagnostics, string rule) =>
        [.. diagnostics.Where(d => d.Rule == rule).Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer).Order(StringComparer.Ordinal)];

    private const string At = "order-lifecycle.json:/transitions/5/gate";

    private static void WriteLibrary(LoaderHarness h, string signatures) => h.Write(LibraryFile, $$"""
        {"kind":"process","id":"{{Library}}","name":"Policies","use":"library",
         "gates":[{"id":"{{Policy}}","name":"Release",{{Meanings}},"stages":{{Stages(signatures)}}}]}
        """);

    // ---- no change ----

    [Fact]
    public async Task Gates_with_todays_fields_keep_their_findings_word_for_word()
    {
        var plain = await ValidateAsync(null);
        Assert.DoesNotContain(plain, d => d.Rule.StartsWith("MQ82", StringComparison.Ordinal));

        // A gate of one person who may not sign twice, needing a role outside its signers: today's three findings, today's words.
        var found = await ValidateAsync(gate =>
        {
            gate["signers"] = new JsonArray(Clerk);
            gate["required"] = 2;
            gate["requiredActors"] = new JsonArray(Finance);
        });
        Assert.Equal(
            "Gate 'approval' of process 'OrderLifecycle' needs 2 signatures, but its signers are 1 person actor(s) and a signer may not sign twice; lower required, add signers, or allow repeat signers.",
            Assert.Single(found, d => d.Rule == "MQ9101").Message);
        Assert.Equal("Gate 'approval' of process 'OrderLifecycle' requires a signature from 'Finance', which is not one of its signers; add it to signers.",
            Assert.Single(found, d => d.Rule == "MQ9102").Message);
        Assert.Equal([At + "/signers/0"], Hits(found, "MQ9105"));
        Assert.StartsWith("Gate 'approval' of process 'OrderLifecycle' lists signer 'Clerk', but event 'approve' may only be raised by ", Assert.Single(found, d => d.Rule == "MQ9105").Message);
        Assert.DoesNotContain(found, d => d.Rule.StartsWith("MQ82", StringComparison.Ordinal));
    }

    // ---- MQ8201 ----

    [Fact]
    public async Task MQ8201_flags_the_counting_keys_beside_stages_and_the_flag_without_them()
    {
        var found = await ValidateAsync(AsApproval(Stages(Signature(Managers, "Managers", $$""" "signers":["{{Manager}}"] """)), gate =>
        {
            gate["required"] = 3;
            gate["signers"] = new JsonArray(Manager);
            gate["requiredActors"] = new JsonArray(Manager);
            gate["allowRepeatSigner"] = true;
        }));
        Assert.Equal([At + "/allowRepeatSigner", At + "/required", At + "/requiredActors", At + "/signers"], Hits(found, "MQ8201"));
        Assert.Contains(found, d => d.Rule == "MQ8201" && d.Message == "Gate 'approval' of process 'OrderLifecycle' has stages, so its named signatures carry the count; remove required from the gate.");

        var flag = await ValidateAsync(gate => gate["allowSignerAcrossSignatures"] = true);
        Assert.Equal([At + "/allowSignerAcrossSignatures"], Hits(flag, "MQ8201"));

        var clean = await ValidateAsync(AsApproval(Stages(Signature(Managers, "Managers", $$""" "signers":["{{Manager}}"] """)),
            gate => gate["allowSignerAcrossSignatures"] = true));
        Assert.Empty(Hits(clean, "MQ8201"));
        Assert.Empty(Hits(clean, "MQ8202"));
    }

    // ---- MQ8202 ----

    [Fact]
    public async Task MQ8202_flags_a_foreign_transition_and_a_verb_no_run_sees_and_passes_what_runs_see()
    {
        var notBy = $$"""
            "signers":["{{Manager}}"],"notBy":[{"transition":"01JTRN00000000000000000099"},{"verb":"modify"},{"transition":"{{Submit}}"},
            {"verb":"promote"},{"verb":"sign"},{"verb":"complete"}]
            """;
        var found = await ValidateAsync(AsApproval(Stages(Signature(Managers, "Managers", notBy))));
        var sig = At + "/stages/0/signatures/0/notBy/";
        Assert.Equal([sig + "0/transition", sig + "1/verb"], Hits(found, "MQ8202"));
        Assert.Contains(found, d => d.Rule == "MQ8202" && d.ElementId == Managers && d.Message.Contains("which is not a transition of process 'OrderLifecycle'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MQ8202_flags_a_gate_policys_transition_and_when_and_verbs_no_run_sees()
    {
        var signature = Signature(Managers, "Managers", $$"""
            "signers":["{{Manager}}"],"when":{"op":"isNotNull","args":[{"path":["{{Amount}}"]}]},
            "notBy":[{"transition":"{{Submit}}"},{"verb":"modify"},{"verb":"promote"}]
            """);
        var found = await ValidateAsync(null, h => WriteLibrary(h, signature));
        var sig = "policies.json:/gates/0/stages/0/signatures/0";
        Assert.Equal([sig + "/notBy/0/transition", sig + "/notBy/1/verb", sig + "/when"], Hits(found, "MQ8202"));
    }

    // ---- MQ3001 ----

    [Fact]
    public async Task MQ3001_flags_two_signatures_of_one_approval_with_one_name()
    {
        var found = await ValidateAsync(AsApproval(Stages(
            Signature(Managers, "Review", $$""" "signers":["{{Manager}}"] """), Signature(Controller, "Review", $$""" "signers":["{{Finance}}"] """))));
        Assert.Contains(At + "/stages/0/signatures/1/name", Hits(found, "MQ3001"));
        Assert.Contains(found, d => d.Rule == "MQ3001" && d.Message.Contains("The signature name 'Review' is already used", StringComparison.Ordinal));
    }

    // ---- the approval site ----

    private static Action<JsonObject> WithWhen(string when) =>
        AsApproval(Stages(Signature(Managers, "Managers", $$""" "signers":["{{Manager}}"],"when":{{when}} """)));

    [Fact]
    public async Task A_signatures_when_reads_the_context_the_clock_and_states_but_not_the_actor_a_payload_or_old()
    {
        const string at = "/transitions/5/gate/stages/0/signatures/0/when";
        Assert.DoesNotContain(await ValidateAsync(WithWhen($$"""{"op":"and","args":[{"op":"isNotNull","args":[{"path":["{{Amount}}"]}]},{"op":"isNotNull","args":[{"now":true}]},{"inState":["{{Draft}}"]}]}""")),
            d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error);

        var actor = await ValidateAsync(WithWhen("""{"op":"isNotNull","args":[{"currentActor":true}]}"""));
        Assert.Contains(actor, d => d.Rule == "MQ3102" && d.Message.StartsWith("current_actor() is whoever acts, and nobody acts when a named signature's round starts", StringComparison.Ordinal));
        var payload = await ValidateAsync(WithWhen($$"""{"op":"isNotNull","args":[{"path":["{{Note}}"]}]}"""));
        Assert.Contains(payload, d => d.Rule == "MQ3102" && d.Message.EndsWith("a named signature's when reads the object under subject and the context, never a payload.", StringComparison.Ordinal));
        Assert.Contains(await ValidateAsync(WithWhen($$"""{"op":"isNotNull","args":[{"path":["{{Amount}}"],"old":true}]}""")), d => d.Rule == "MQ3104");
        Assert.Contains(await ValidateAsync(WithWhen($$"""{"path":["{{Amount}}"]}""")), d => d.Rule == "MQ3103" && d.JsonPointer!.StartsWith(at, StringComparison.Ordinal));
        Assert.Contains(await ValidateAsync(WithWhen("""{"op":"isNotNull","args":[{"now":true},{"now":true}]}""")), d => d.Rule == "MQ3101");
        Assert.Contains(await ValidateAsync(WithWhen("""{"text":"amount > 100"}""")), d => d.Rule == "MQ3108" && d.JsonPointer == at);
    }

    [Fact]
    public async Task The_session_judges_a_when_needed_unless_it_is_false()
    {
        var (h, store) = await OpenAsync(WithWhen($$"""{"op":"gt","args":[{"path":["{{Amount}}"]},{"value":100}]}"""));
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var process = Assert.IsType<Process>(model.GetDocument(P)!.Element);
        var compiled = ProcessExpressions.Get(model, process);
        Assert.Contains(Managers, compiled.Conditions);
        Assert.Contains("mq.passes(", compiled.Script, StringComparison.Ordinal);
        using var session = compiled.Open(1, Ct);
        bool? Needed(string context)
        {
            using var doc = JsonDocument.Parse(context);
            var result = session.EvaluateApproval(Managers, "Signature 'Managers'", doc.RootElement, JsonDocument.Parse("{}").RootElement, null, "2026-01-01T00:00:00Z", "seed", Ct);
            Assert.Null(result.Diagnostic);
            return (bool?)result.Value;
        }

        Assert.True(Needed("""{"amount":500}"""));
        Assert.False(Needed("""{"amount":5}"""));
        Assert.True(Needed("""{"amount":null}"""));
    }

    // ---- MQ9101, MQ9102, MQ9105, MQ9106 ----

    [Fact]
    public async Task Counting_and_actor_rules_read_each_named_signature_and_the_approval()
    {
        var found = await ValidateAsync(AsApproval(Stages(
            Signature(Managers, "Managers", $$""" "signers":["{{Ana}}"],"required":2 """),
            Signature(Controller, "Controller", $$""" "signers":["{{Manager}}"],"requiredActors":["{{Finance}}"] """))));
        var sig = At + "/stages/0/signatures/";
        Assert.Equal([sig + "0/required"], Hits(found, "MQ9101"));
        Assert.Equal("Signature 'Managers' of gate 'approval' of process 'OrderLifecycle' needs 2 signatures, but its signers are 1 person actor(s) and a signer may not sign twice; lower required, add signers, or allow repeat signers.",
            Assert.Single(found, d => d.Rule == "MQ9101").Message);
        Assert.Equal([sig + "1/requiredActors/0"], Hits(found, "MQ9102"));
        Assert.Equal([sig + "0/signers/0"], Hits(found, "MQ9105"));
        Assert.Contains(found, d => d.Rule == "MQ9105" && d.Message.Contains("lists signer 'Ana' in signature 'Managers'", StringComparison.Ordinal));
        // A role among the signers is not counted as people, so the approval itself is not judged.
        Assert.DoesNotContain(found, d => d.Rule == "MQ9101" && d.JsonPointer == At + "/stages");
    }

    [Fact]
    public async Task MQ9101_flags_an_approval_needing_more_people_than_its_signers_unless_one_may_give_several()
    {
        var stages = Stages(Signature(Managers, "Managers", $$""" "signers":["{{Ana}}","{{Ben}}"],"required":2 """),
            Signature(Controller, "Controller", $$""" "signers":["{{Ana}}"] """));
        Assert.Equal([At + "/stages"], Hits(await ValidateAsync(AsApproval(stages)), "MQ9101"));
        Assert.Empty(Hits(await ValidateAsync(AsApproval(stages, gate => gate["allowSignerAcrossSignatures"] = true)), "MQ9101"));
    }

    [Fact]
    public async Task An_actor_named_only_by_a_signature_counts_as_used()
    {
        var found = await ValidateAsync(AsApproval(Stages(Signature(Managers, "Managers", $$""" "signers":["{{Manager}}","{{Auditor}}"] """))));
        Assert.DoesNotContain(found, d => d.Rule == "MQ9106" && d.Message.Contains("'Auditor'", StringComparison.Ordinal));
        Assert.Contains(await ValidateAsync(null), d => d.Rule == "MQ9106" && d.Message.Contains("'Auditor'", StringComparison.Ordinal));
    }

    // ---- MQ9701, MQ9704 ----

    [Fact]
    public async Task Verb_rules_read_notBy_and_a_steps_response()
    {
        var found = await ValidateAsync(AsApproval(Stages(Signature(Managers, "Managers", $$""" "signers":["{{Manager}}"],"notBy":[{"verb":"bogus"},{"verb":"withdraw"}] """))),
            h =>
            {
                h.Write("model/vocabularies/verbs.json", """{"kind":"verb-vocabulary","id":"01JVRB00000000000000000961","name":"verbs","hidden":["withdraw","ignore"]}""");
                h.Write("model/scenarios/order-lifecycle/answers.json", $$"""
                    {"kind":"scenario","id":"01JSCN00000000000000000061","name":"Answers","process":"{{P}}",
                     "steps":[{"id":"01JSTP00000000000000000061","event":"{{ApproveEvent}}","actor":"{{Manager}}","signer":"ana","response":"ignore"}]}
                    """);
            });
        var sig = At + "/stages/0/signatures/0/notBy/";
        Assert.Equal([sig + "0/verb"], Hits(found, "MQ9701"));
        Assert.Equal(["answers.json:/steps/0/response", sig + "1/verb"], Hits(found, "MQ9704"));
    }

    // ---- MQ8102, MQ8107 ----

    private const string RejectPoint = $$"""
        [{"id":"01JTGP00000000000000000061","name":"OnReject","type":"check","timing":"before","verbs":["reject"],"transitions":["{{Approve}}"]}]
        """;

    [Fact]
    public async Task A_trigger_point_on_reject_at_the_gated_move_fits_only_a_lifecycle_with_an_approval()
    {
        void Points(JsonObject process) => process["triggerPoints"] = JsonNode.Parse(RejectPoint);
        var without = await ValidateAsync(null, process: Points);
        Assert.Contains(without, d => d.Rule == "MQ8102" && d.Message == "Trigger point 'OnReject' names transitions beside a verb that makes no move of this lifecycle; transitions fit move verbs, sign, and own verbs its events send. Remove them.");
        Assert.Contains(without, d => d.Rule == "MQ8107");

        var with = await ValidateAsync(AsApproval(Stages(Signature(Managers, "Managers", $$""" "signers":["{{Manager}}"] """))), process: Points);
        Assert.DoesNotContain(with, d => d.Rule is "MQ8102" or "MQ8107");
    }

    [Fact]
    public async Task A_named_trigger_point_on_reject_runs_where_a_governing_lifecycle_has_an_approval()
    {
        static void Named(LoaderHarness h) => h.Write("model/trigger-points/note-rejections.json", $$"""
            {"kind":"trigger-point","id":"01JTGP00000000000000000062","name":"NoteRejections","type":"check","timing":"before","verbs":["reject"],
             "subjects":[{"entity":"{{Order}}"}]}
            """);
        Assert.Contains(await ValidateAsync(null, Named), d => d.Rule == "MQ8107" && d.Message.StartsWith("Trigger point 'NoteRejections'", StringComparison.Ordinal));
        Assert.DoesNotContain(await ValidateAsync(AsApproval(Stages(Signature(Managers, "Managers", $$""" "signers":["{{Manager}}"] """))), Named),
            d => d.Rule == "MQ8107");
    }

    // ---- MQ9305 ----

    [Fact]
    public async Task MQ9305_flags_approval_fields_on_a_step_that_answers_no_approval()
    {
        static Action<LoaderHarness> Scenario(string step) => h => h.Write("model/scenarios/order-lifecycle/answers.json", $$"""
            {"kind":"scenario","id":"01JSCN00000000000000000061","name":"Answers","process":"{{P}}",
             "steps":[{"id":"01JSTP00000000000000000061","event":"{{ApproveEvent}}","actor":"{{Manager}}","signer":"ana",{{step}}}]}
            """);

        async Task<string?> FirstAsync(Action<JsonObject>? gate, string step)
        {
            var (h, store) = await OpenAsync(gate, Scenario(step));
            using var _ = h;
            await using var __ = store;
            var verified = await store.VerifyScenariosAsync(P, null, Ct);
            return verified.Value!.Results.Single(r => r.Name == "Answers").Failure is { Rule: "MQ9305" } failure ? failure.Message : null;
        }

        Assert.Contains("gives response 'reject', but only an event step whose event is gated by an approval takes a response", await FirstAsync(null, """ "response":"reject" """));
        Assert.Contains("names a signature, but only an event step", await FirstAsync(null, $$""" "signature":"{{Managers}}" """));
        var approval = AsApproval(Stages(Signature(Managers, "Managers", $$""" "signers":["{{Manager}}"] """)));
        Assert.Contains("is not a named signature of an approval on event 'approve'", await FirstAsync(approval, $$""" "signature":"{{Controller}}" """));
        Assert.Contains("expect.signatures names", await FirstAsync(approval, $$""" "expect":{"signatures":{"{{Controller}}":1} } """));
        Assert.Null(await FirstAsync(approval, $$""" "response":"reject","signature":"{{Managers}}","expect":{"signatures":{"{{Managers}}":0} } """));
    }
}
