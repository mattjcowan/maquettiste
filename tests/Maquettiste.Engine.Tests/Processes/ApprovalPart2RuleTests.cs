using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Approvals, part 2 (signoff-design.md decisions 5, 6, 20 to 22 and 32 to 36, section 6): a stage's when at the approval site; MQ8202 on a
/// gate policy's stage, MQ8203 to MQ8205; the part 2 cases of MQ3001, MQ3101 to MQ3108, MQ9101, MQ9106, MQ9305, MQ8102 and MQ8107; the groups
/// on the index rows and the approvals naming key, which keeps a part-1-only model's hash; and the same findings for a model that uses part 1
/// only.
/// </summary>
public sealed class ApprovalPart2RuleTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle
    private const string Library = "01JPRC00000000000000000061"; // Policies
    private const string Order = "01JENT00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001"; // a person
    private const string Manager = "01JACT00000000000000000002"; // a role
    private const string Finance = "01JACT00000000000000000003"; // a role
    private const string Ana = "01JACT00000000000000000061"; // a person
    private const string Ben = "01JACT00000000000000000062"; // a person
    private const string Auditor = "01JACT00000000000000000063"; // a person no process names but a signature's delegateTo or escalateTo
    private const string Amount = "01JATT00000000000000000011"; // context, decimal
    private const string Note = "01JATT00000000000000000021"; // submit's payload
    private const string Draft = "01JSTA00000000000000000001";
    private const string Approve = "01JTRN00000000000000000006";
    private const string ApproveEvent = "01JPRX00000000000000000002";
    private const string Policy = "01JPRX00000000000000000061";
    private const string Review = "01JGST00000000000000000001";
    private const string Legal = "01JGST00000000000000000002";
    private const string Managers = "01JNSG00000000000000000001";
    private const string Counsel = "01JNSG00000000000000000002";
    private const string Controller = "01JNSG00000000000000000003";
    private const string AnaPart = "01JPTC00000000000000000001";
    private const string BenPart = "01JPTC00000000000000000002";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string LibraryFile = "model/processes/policies.json";
    private const string Meanings = """ "meanings":[{"id":"01JPRX00000000000000000051","name":"approved"}] """;
    private const string At = "order-lifecycle.json:/transitions/5/gate";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Stage(string id, string name, string rest, params string[] signatures) =>
        $$"""{"id":"{{id}}","name":"{{name}}",{{rest}}"signatures":[{{string.Join(",", signatures)}}]}""";

    private static string Signature(string id, string name, string rest) => $$"""{"id":"{{id}}","name":"{{name}}",{{rest}}}""";

    private static string ManagersSig(string extra = "") => Signature(Managers, "Managers", $$""" "signers":["{{Manager}}"]{{extra}} """);

    private static string CounselSig(string extra = "") => Signature(Counsel, "Counsel", $$""" "signers":["{{Finance}}"]{{extra}} """);

    /// <summary>The review stage with Managers, then a legal stage in round 2 with Counsel.</summary>
    private static string TwoRounds(string legal = "", string managers = "") =>
        "[" + Stage(Review, "Review", "", ManagersSig(managers)) + "," + Stage(Legal, "Legal", $$""" "round":2,{{legal}} """, CounselSig()) + "]";

    private static string OneStage(string managers = "") => "[" + Stage(Review, "Review", "", ManagersSig(managers)) + "]";

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

    private static Action<JsonObject> AsApproval(string stages, Action<JsonObject>? extra = null) => gate =>
    {
        foreach (var key in new[] { "required", "signers", "requiredActors" })
            gate.Remove(key);
        gate["stages"] = JsonNode.Parse(stages);
        extra?.Invoke(gate);
    };

    private static List<string> Hits(IEnumerable<Diagnostic> diagnostics, string rule) =>
        [.. diagnostics.Where(d => d.Rule == rule).Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer).Order(StringComparer.Ordinal)];

    private static Action<LoaderHarness> WithLibrary(string stages) => h => h.Write(LibraryFile, $$"""
        {"kind":"process","id":"{{Library}}","name":"Policies","use":"library","gates":[{"id":"{{Policy}}","name":"Release",{{Meanings}},"stages":{{stages}}}]}
        """);

    /// <summary>The approve move uses the library's gate policy in place of its own gate.</summary>
    private static void UsesPolicy(JsonObject process)
    {
        var transition = process["transitions"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == Approve)!.AsObject();
        transition.Remove("gate");
        transition["gatePolicy"] = Policy;
    }

    // ---- no change ----

    [Fact]
    public async Task A_model_that_uses_part_1_only_gets_no_part_2_finding()
    {
        var found = await ValidateAsync(AsApproval(OneStage(""","when":{"text":"amount > 100"},"notBy":[{"verb":"promote"}]""")), WithLibrary(OneStage()));
        Assert.DoesNotContain(found, d => d.Rule is "MQ8203" or "MQ8204" or "MQ8205");
        Assert.DoesNotContain(found, d => d.Message.Contains("stage's", StringComparison.Ordinal));
        Assert.DoesNotContain(found, d => d.Rule == "MQ8202");
    }

    // ---- MQ8203 ----

    [Fact]
    public async Task MQ8203_flags_a_stage_listed_after_a_later_round()
    {
        static string Rounds(params int[] rounds) => "[" + string.Join(",", rounds.Select((r, i) => Stage("01JGST0000000000000000000" + (i + 1), "S" + i,
            $$""" "round":{{r}}, """, Signature("01JNSG0000000000000000000" + (i + 1), "N" + i, $$""" "signers":["{{Manager}}"] """)))) + "]";
        var found = await ValidateAsync(AsApproval(Rounds(2, 1, 3, 2)));
        Assert.Equal([At + "/stages/1/round", At + "/stages/3/round"], Hits(found, "MQ8203"));
        Assert.Contains(found, d => d.Rule == "MQ8203" && d.Message == "Stage 'S1' of gate 'approval' of process 'OrderLifecycle' runs in round 1, but a stage before it runs in round 2; list the stages in round order, so the file reads from top to bottom.");
        // Side by side in one round, and rounds with a gap, read top to bottom.
        Assert.Empty(Hits(await ValidateAsync(AsApproval(Rounds(1, 1, 3))), "MQ8203"));
        // A gate policy's stages too.
        Assert.Equal(["policies.json:/gates/0/stages/1/round"], Hits(await ValidateAsync(null, WithLibrary(Rounds(2, 1))), "MQ8203"));
    }

    // ---- MQ8202 on a stage ----

    [Fact]
    public async Task MQ8202_flags_a_when_on_a_gate_policys_stage_only()
    {
        const string when = """ "when":{"text":"amount > 100"}, """;
        var policy = await ValidateAsync(null, WithLibrary("[" + Stage(Review, "Review", when, ManagersSig()) + "]"));
        Assert.Equal(["policies.json:/gates/0/stages/0/when"], Hits(policy, "MQ8202"));
        Assert.Contains(policy, d => d.Rule == "MQ8202" && d.Message == "Stage 'Review' of gate 'Release' of library 'Policies' has a when, but a gate policy has no subject to read; remove the when, or put the gate on its transition.");
        Assert.Empty(Hits(await ValidateAsync(AsApproval(TwoRounds(legal: """ "when":{"text":"amount > 100"}, """))), "MQ8202"));
    }

    // ---- MQ8204, MQ8205 ----

    [Fact]
    public async Task MQ8204_flags_an_escalation_without_its_pair_and_a_delay_that_is_not_a_positive_duration()
    {
        var sig = At + "/stages/0/signatures/0";
        var found = await ValidateAsync(AsApproval(OneStage($$""","escalateTo":["{{Finance}}"]""")));
        Assert.Equal([sig + "/escalateTo"], Hits(found, "MQ8204"));
        Assert.Contains(found, d => d.Rule == "MQ8204" && d.Message == "Signature 'Managers' of gate 'approval' of process 'OrderLifecycle' names escalateTo but no escalateAfter, so no escalation ever falls due; add escalateAfter, or remove escalateTo.");
        Assert.Equal([sig + "/escalateAfter"], Hits(await ValidateAsync(AsApproval(OneStage(""","escalateAfter":"P2D" """))), "MQ8204"));

        foreach (var bad in new[] { "2 days", "P0D", "PT0S", "P", "-P1D" })
        {
            var delays = await ValidateAsync(AsApproval(OneStage($$""","remindAfter":"{{bad}}","escalateAfter":"{{bad}}","escalateTo":["{{Finance}}"]""")));
            Assert.Equal([sig + "/escalateAfter", sig + "/remindAfter"], Hits(delays, "MQ8204"));
            Assert.Contains(delays, d => d.Rule == "MQ8204" && d.Message.EndsWith($"has remindAfter '{bad}', which is not a positive ISO 8601 duration; use a form such as P2D or PT30M.", StringComparison.Ordinal));
            Assert.Empty(Hits(delays, "MQ8205"));
        }

        Assert.Empty(Hits(await ValidateAsync(AsApproval(OneStage($$""","remindAfter":"PT36H","escalateAfter":"P1W","escalateTo":["{{Finance}}"]"""))), "MQ8204"));
        // A gate policy's signature too.
        Assert.Equal(["policies.json:/gates/0/stages/0/signatures/0/escalateTo"], Hits(await ValidateAsync(null, WithLibrary(OneStage($$""","escalateTo":["{{Finance}}"]"""))), "MQ8204"));
    }

    [Fact]
    public async Task MQ8205_warns_when_an_escalation_falls_due_no_later_than_its_reminder()
    {
        async Task<List<Diagnostic>> Of(string remind, string escalate) =>
            [.. (await ValidateAsync(AsApproval(OneStage($$""","remindAfter":"{{remind}}","escalateAfter":"{{escalate}}","escalateTo":["{{Finance}}"]""")))).Where(d => d.Rule == "MQ8205")];
        var earlier = Assert.Single(await Of("P4D", "P2D"));
        Assert.Equal((DiagnosticSeverity.Warning, "/transitions/5/gate/stages/0/signatures/0/escalateAfter"), (earlier.Severity, earlier.JsonPointer));
        Assert.Equal("Signature 'Managers' of gate 'approval' of process 'OrderLifecycle' escalates after P2D, no later than its reminder after P4D, so the reminder never comes first; lengthen escalateAfter, or shorten remindAfter.", earlier.Message);
        // Read as an after transition's delay is: two days and 48 hours are the same delay.
        Assert.Single(await Of("P2D", "PT48H"));
        Assert.Empty(await Of("P2D", "P4D"));
    }

    // ---- a stage's when ----

    private static Action<JsonObject> WithStageWhen(string when) => AsApproval(TwoRounds(legal: $$""" "when":{{when}}, """));

    [Fact]
    public async Task A_stages_when_reads_the_object_the_context_the_clock_and_states_at_the_approval_site()
    {
        const string at = "/transitions/5/gate/stages/1/when";
        Assert.DoesNotContain(await ValidateAsync(WithStageWhen($$"""{"op":"and","args":[{"op":"gt","args":[{"path":["{{Amount}}"]},{"value":100}]},{"op":"isNotNull","args":[{"now":true}]},{"inState":["{{Draft}}"]}]}""")),
            d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error);

        var actor = await ValidateAsync(WithStageWhen("""{"op":"isNotNull","args":[{"currentActor":true}]}"""));
        Assert.Contains(actor, d => d.Rule == "MQ3102" && d.JsonPointer!.StartsWith(at, StringComparison.Ordinal)
            && d.Message == "current_actor() is whoever acts, and nobody acts when an approval stage's round starts; read the object or the context instead.");
        var payload = await ValidateAsync(WithStageWhen($$"""{"op":"isNotNull","args":[{"path":["{{Note}}"]}]}"""));
        Assert.Contains(payload, d => d.Rule == "MQ3102" && d.Message.EndsWith("an approval stage's when reads the object under subject and the context, never a payload.", StringComparison.Ordinal));
        Assert.Contains(await ValidateAsync(WithStageWhen($$"""{"op":"isNotNull","args":[{"path":["{{Amount}}"],"old":true}]}""")), d => d.Rule == "MQ3104");
        Assert.Contains(await ValidateAsync(WithStageWhen($$"""{"path":["{{Amount}}"]}""")), d => d.Rule == "MQ3103" && d.Message.EndsWith("a stage's when must be bool.", StringComparison.Ordinal));
        Assert.Contains(await ValidateAsync(WithStageWhen("""{"text":"amount > 100"}""")), d => d.Rule == "MQ3108" && d.JsonPointer == at);
        Assert.Contains(await ValidateAsync(WithStageWhen("""{"text":"amount >"}""")), d => d.Rule == "MQ3101" && d.JsonPointer!.StartsWith(at, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stages_when_is_a_site_replay_keys_read_and_the_session_judges_it_under_the_stages_id()
    {
        var (h, store) = await OpenAsync(WithStageWhen($$"""{"op":"gt","args":[{"path":["{{Amount}}"]},{"value":100}]}"""));
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var process = Assert.IsType<Process>(model.GetDocument(P)!.Element);
        Assert.Contains(ExprSites.Of(model, process), s => (s.SubId, s.Kind, s.Pointer, s.StageCondition) == (Legal, ExprSiteKind.Approval, "/transitions/5/gate/stages/1/when", true));
        Assert.True(ExprSites.ApprovalData(process));
        Assert.Contains(Legal, ExprSites.ConditionIds(process));

        var compiled = ProcessExpressions.Get(model, process);
        Assert.Contains(Legal, compiled.Conditions);
        using var session = compiled.Open(1, Ct);
        bool? Runs(string context)
        {
            using var doc = JsonDocument.Parse(context);
            using var subject = JsonDocument.Parse("{}");
            var result = session.EvaluateApproval(Legal, "Stage 'Legal'", doc.RootElement, subject.RootElement, null, "2026-01-01T00:00:00Z", "seed", Ct);
            Assert.Null(result.Diagnostic);
            return (bool?)result.Value;
        }

        Assert.True(Runs("""{"amount":500}"""));
        Assert.False(Runs("""{"amount":5}"""));
        Assert.True(Runs("""{"amount":null}"""));
    }

    // ---- MQ3001, MQ9101, MQ9106 ----

    [Fact]
    public async Task MQ3001_flags_two_stages_or_two_signatures_with_one_name_across_rounds()
    {
        var stages = "[" + Stage(Review, "Review", "", ManagersSig()) + "," + Stage(Legal, "Review", """ "round":2, """, Signature(Counsel, "Managers", $$""" "signers":["{{Finance}}"] """)) + "]";
        var found = await ValidateAsync(AsApproval(stages));
        Assert.Contains(At + "/stages/1/name", Hits(found, "MQ3001"));
        Assert.Contains(At + "/stages/1/signatures/0/name", Hits(found, "MQ3001"));
    }

    [Fact]
    public async Task MQ9101_counts_only_the_signatures_of_stages_always_run()
    {
        // Ana and Ben, and two signatures needing two and one of them: only when both stages always run can the approval be known to lack people.
        string Of(string when) => "[" + Stage(Review, "Review", "", Signature(Managers, "Managers", $$""" "signers":["{{Ana}}","{{Ben}}"],"required":2 """)) + ","
            + Stage(Legal, "Legal", $$""" "round":2,{{when}} """, Signature(Counsel, "Counsel", $$""" "signers":["{{Ana}}"] """)) + "]";
        var always = await ValidateAsync(AsApproval(Of("")));
        Assert.Equal([At + "/stages"], Hits(always, "MQ9101"));
        Assert.Contains("across its signatures,", Assert.Single(always, d => d.Rule == "MQ9101").Message, StringComparison.Ordinal);
        Assert.Empty(Hits(await ValidateAsync(AsApproval(Of(""" "when":{"text":"amount > 100"}, """))), "MQ9101"));

        // A third signature needing a third person keeps the finding, worded for the stages that always run.
        var three = "[" + Stage(Review, "Review", "", Signature(Managers, "Managers", $$""" "signers":["{{Ana}}","{{Ben}}"],"required":2 """),
                Signature(Controller, "Controller", $$""" "signers":["{{Ben}}"] """)) + ","
            + Stage(Legal, "Legal", """ "round":2,"when":{"text":"amount > 100"}, """, Signature(Counsel, "Counsel", $$""" "signers":["{{Ana}}"] """)) + "]";
        var skipped = await ValidateAsync(AsApproval(three));
        Assert.Contains("needs 3 different people across the signatures it always needs (those without when, in stages without when), but their signers are 2 person actor(s)",
            Assert.Single(skipped, d => d.Rule == "MQ9101").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_actor_named_only_by_a_signatures_delegateTo_or_escalateTo_counts_as_used()
    {
        bool Unused(IEnumerable<Diagnostic> found) => found.Any(d => d.Rule == "MQ9106" && d.Message.Contains("'Auditor'", StringComparison.Ordinal));
        Assert.True(Unused(await ValidateAsync(AsApproval(OneStage()))));
        Assert.False(Unused(await ValidateAsync(AsApproval(OneStage($$""","delegateTo":["{{Auditor}}"]""")))));
        Assert.False(Unused(await ValidateAsync(AsApproval(OneStage($$""","escalateAfter":"P2D","escalateTo":["{{Auditor}}"]""")))));
        // Through a gate policy too.
        Assert.False(Unused(await ValidateAsync(null, WithLibrary(OneStage($$""","delegateTo":["{{Auditor}}"]""")), UsesPolicy)));
    }

    [Fact]
    public async Task MQ9604_flags_an_actor_from_the_object_in_delegateTo_or_escalateTo_that_cannot_be_worked_out()
    {
        // A person worked out through an attribute Order lacks (the submit event's payload).
        static void Holder(LoaderHarness h) => h.Write("model/actors/holder.json", """
            {"kind":"actor","id":"01JACT00000000000000000064","name":"Holder","type":"person","relative":{"path":["01JATT00000000000000000021"]}}
            """);
        var found = await ValidateAsync(AsApproval(OneStage(""","delegateTo":["01JACT00000000000000000064"],"escalateAfter":"P2D","escalateTo":["01JACT00000000000000000064"]""")), Holder);
        var sig = At + "/stages/0/signatures/0";
        Assert.Equal([sig + "/delegateTo/0", sig + "/escalateTo/0"], Hits(found, "MQ9604"));
    }

    // ---- MQ8102, MQ8107 ----

    private const string DelegatePoint = $$"""
        [{"id":"01JTGP00000000000000000061","name":"OnDelegate","type":"check","timing":"before","verbs":["delegate"],"transitions":["{{Approve}}"]}]
        """;

    [Fact]
    public async Task A_trigger_point_on_delegate_fits_only_a_lifecycle_whose_signature_has_delegateTo()
    {
        static void Points(JsonObject process) => process["triggerPoints"] = JsonNode.Parse(DelegatePoint);
        var approval = await ValidateAsync(AsApproval(OneStage()), process: Points);
        Assert.Contains(approval, d => d.Rule == "MQ8102" && d.Message == "Trigger point 'OnDelegate' names transitions beside a verb that makes no move of this lifecycle; transitions fit move verbs, sign, reject, ignore, withdraw, and own verbs its events send. Remove them.");
        Assert.Contains(approval, d => d.Rule == "MQ8107");

        var delegation = await ValidateAsync(AsApproval(OneStage($$""","delegateTo":["{{Finance}}"]""")), process: Points);
        Assert.DoesNotContain(delegation, d => d.Rule is "MQ8102" or "MQ8107");
        // Through a gate policy too.
        Assert.DoesNotContain(await ValidateAsync(null, WithLibrary(OneStage($$""","delegateTo":["{{Finance}}"]""")), p => { UsesPolicy(p); Points(p); }),
            d => d.Rule is "MQ8102" or "MQ8107");

        // Beside another verb that makes no move, the words name delegate among the verbs that fit.
        static void Modify(JsonObject process) => process["triggerPoints"] = JsonNode.Parse(DelegatePoint.Replace("\"delegate\"", "\"delegate\",\"modify\"", StringComparison.Ordinal));
        Assert.Contains(await ValidateAsync(AsApproval(OneStage($$""","delegateTo":["{{Finance}}"]""")), process: Modify),
            d => d.Rule == "MQ8102" && d.Message.EndsWith("transitions fit move verbs, sign, reject, ignore, withdraw, delegate, and own verbs its events send. Remove them.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_named_trigger_point_on_delegate_runs_where_a_governing_lifecycle_has_a_signature_with_delegateTo()
    {
        static void Named(LoaderHarness h) => h.Write("model/trigger-points/note-delegations.json", $$"""
            {"kind":"trigger-point","id":"01JTGP00000000000000000062","name":"NoteDelegations","type":"check","timing":"before","verbs":["delegate"],
             "subjects":[{"entity":"{{Order}}"}]}
            """);
        Assert.Contains(await ValidateAsync(AsApproval(OneStage()), Named), d => d.Rule == "MQ8107" && d.Message.StartsWith("Trigger point 'NoteDelegations'", StringComparison.Ordinal));
        Assert.DoesNotContain(await ValidateAsync(AsApproval(OneStage($$""","delegateTo":["{{Finance}}"]""")), Named), d => d.Rule == "MQ8107");

        // Validating what a save of the lifecycle changed reaches the named point, which names neither the lifecycle nor its gate.
        static async Task<bool> FlaggedAsync(Action<JsonObject> gate)
        {
            var (h, store) = await OpenAsync(gate, Named);
            using var _ = h;
            await using var __ = store;
            return (await store.ValidateAsync(new ValidationScope([P]), Ct)).Diagnostics.Any(d => d.Rule == "MQ8107");
        }

        Assert.True(await FlaggedAsync(AsApproval(OneStage())));
        Assert.False(await FlaggedAsync(AsApproval(OneStage($$""","delegateTo":["{{Finance}}"]"""))));
    }

    // ---- MQ9305 ----

    [Fact]
    public async Task MQ9305_flags_a_delegation_that_does_not_fit()
    {
        static Action<LoaderHarness> Scenario(string step) => h => h.Write("model/scenarios/order-lifecycle/answers.json", $$"""
            {"kind":"scenario","id":"01JSCN00000000000000000061","name":"Answers","process":"{{P}}",
             "participants":[{"id":"{{AnaPart}}","name":"Ana","identity":"ana","actor":"{{Ana}}","holds":[{"id":"01JPTH00000000000000000001","actor":"{{Manager}}"}]},
               {"id":"{{BenPart}}","name":"Ben","identity":"ben","actor":"{{Ben}}","holds":[{"id":"01JPTH00000000000000000002","actor":"{{Finance}}"}]}],
             "steps":[{"id":"01JSTP00000000000000000061","event":"{{ApproveEvent}}","participant":"{{AnaPart}}",{{step}}}]}
            """);

        async Task<string?> FirstAsync(Action<JsonObject>? gate, string step)
        {
            var (h, store) = await OpenAsync(gate, Scenario(step));
            using var _ = h;
            await using var __ = store;
            var verified = await store.VerifyScenariosAsync(P, null, Ct);
            return verified.Value!.Results.Single(r => r.Name == "Answers").Failure is { Rule: "MQ9305" } failure ? failure.Message : null;
        }

        var delegable = AsApproval("[" + Stage(Review, "Review", "", ManagersSig($$""","delegateTo":["{{Finance}}"]"""), CounselSig()) + "]");
        var plain = AsApproval(OneStage());
        Assert.Contains("names delegateTo, but only a delegate response hands a signature to someone", await FirstAsync(delegable, $$""" "delegateTo":"{{BenPart}}" """));
        Assert.Contains("gives response 'delegate', but names no delegateTo", await FirstAsync(delegable, """ "response":"delegate" """));
        Assert.Contains("a delegation can't be passed on", await FirstAsync(delegable, $$""" "response":"delegate","delegateTo":"{{BenPart}}","onBehalfOf":"{{BenPart}}" """));
        Assert.Contains("gives response 'delegate', but no named signature of an approval on event 'approve' has delegateTo",
            await FirstAsync(plain, $$""" "response":"delegate","delegateTo":"{{BenPart}}" """));
        Assert.Contains("answers on behalf of another person, but no named signature of an approval on event 'approve' has delegateTo",
            await FirstAsync(plain, $$""" "onBehalfOf":"{{BenPart}}" """));
        Assert.Contains($"signature '{Counsel}' has no delegateTo, so it can't be delegated",
            await FirstAsync(delegable, $$""" "response":"delegate","delegateTo":"{{BenPart}}","signature":"{{Counsel}}" """));
        // A gate without stages keeps part 1's words for a response.
        Assert.Contains("gives response 'delegate', but only an event step whose event is gated by an approval takes a response",
            await FirstAsync(null, $$""" "response":"delegate","delegateTo":"{{BenPart}}" """));
        Assert.Null(await FirstAsync(delegable, $$""" "response":"delegate","delegateTo":"{{BenPart}}","signature":"{{Managers}}" """));
        Assert.Null(await FirstAsync(delegable, $$""" "onBehalfOf":"{{BenPart}}" """));
    }

    // ---- the groups and the approvals naming key ----

    // The approvals key of a part-1-only model, inline and through a gate policy, computed on the engine before part 2's groups (ccf31017): a
    // project that uses part 1 only keeps its key (signoff-design.md decision 32).
    private const string Part1Hash = "45e887b8ed6a89785ffbed2aef6e4f04d08759567a2ba61eb240c957a8d8fc19";

    private static async Task<string> HashAsync(Action<JsonObject>? gate, Action<LoaderHarness>? more = null, Action<JsonObject>? process = null)
    {
        var (h, store) = await OpenAsync(gate, more, process);
        using var _ = h;
        await using var __ = store;
        return ApprovalNaming.Hash(store.Current!);
    }

    private static readonly string Part1Stages = "[" + Stage(Review, "Review", "", Signature(Managers, "Managers",
        $$""" "signers":["{{Manager}}"],"required":2,"notBy":[{"verb":"promote"}],"when":{"text":"amount > 100"} """), CounselSig()) + "]";

    [Fact]
    public async Task A_part_1_only_model_keeps_its_approvals_key_byte_for_byte()
    {
        Assert.Equal(Part1Hash, await HashAsync(AsApproval(Part1Stages), WithLibrary(Part1Stages)));
        Assert.Equal(Part1Hash, await HashAsync(AsApproval(Part1Stages), WithLibrary(Part1Stages), UsesPolicy));
    }

    [Fact]
    public async Task Each_groups_first_use_moves_the_approvals_key_and_a_later_edit_within_it_does_not()
    {
        var part1 = await HashAsync(AsApproval(OneStage()));
        var rounds = await HashAsync(AsApproval(TwoRounds()));
        var stageWhen = await HashAsync(AsApproval("[" + Stage(Review, "Review", """ "when":{"text":"amount > 100"}, """, ManagersSig()) + "]"));
        var delegation = await HashAsync(AsApproval(OneStage($$""","delegateTo":["{{Finance}}"]""")));
        var reminder = await HashAsync(AsApproval(OneStage(""","remindAfter":"P2D" """)));
        var escalation = await HashAsync(AsApproval(OneStage($$""","escalateAfter":"P4D","escalateTo":["{{Finance}}"]""")));
        var password = await HashAsync(AsApproval(OneStage(""","reauthenticate":true""")));
        var all = await HashAsync(AsApproval(TwoRounds(managers: $$""","delegateTo":["{{Finance}}"],"remindAfter":"P2D","reauthenticate":true""")));

        // Each group moves it, two forms of one group hash alike, and each group hashes apart.
        Assert.Equal(rounds, stageWhen);
        Assert.Equal(reminder, escalation);
        Assert.Equal(7, new[] { part1, rounds, delegation, reminder, password, all, await HashAsync(null) }.Distinct(StringComparer.Ordinal).Count());

        // Within a group, another signature's keys don't move it.
        Assert.Equal(rounds, await HashAsync(AsApproval("[" + Stage(Review, "Review", "", ManagersSig()) + "," + Stage(Legal, "Legal", """ "round":3, """, CounselSig(), Signature(Controller, "Controller", $$""" "signers":["{{Manager}}"] """)) + "]")));
        Assert.Equal(delegation, await HashAsync(AsApproval("[" + Stage(Review, "Review", "", ManagersSig($$""","delegateTo":["{{Finance}}","{{Manager}}"]"""), CounselSig($$""","delegateTo":["{{Manager}}"]""")) + "]")));
        Assert.Equal(reminder, await HashAsync(AsApproval(OneStage($$""","remindAfter":"P1D","escalateAfter":"P9D","escalateTo":["{{Clerk}}"]"""))));
        Assert.Equal(part1, await HashAsync(AsApproval(OneStage(""","reauthenticate":false"""))));
    }

    [Fact]
    public async Task A_gate_policys_group_reaches_the_lifecycles_that_use_it()
    {
        var plain = await HashAsync(null, WithLibrary(OneStage()), UsesPolicy);
        var delegable = await HashAsync(null, WithLibrary(OneStage($$""","delegateTo":["{{Finance}}"]""")), UsesPolicy);
        Assert.NotEqual(plain, delegable);
        // A group on an unused gate policy moves nothing.
        Assert.Equal(await HashAsync(null, WithLibrary(OneStage())), await HashAsync(null, WithLibrary(OneStage($$""","delegateTo":["{{Finance}}"]"""))));
        // A group through a gate policy hashes as the same group inline.
        Assert.Equal(await HashAsync(AsApproval(TwoRounds())), await HashAsync(null, WithLibrary(TwoRounds()), UsesPolicy));
    }

    [Fact]
    public async Task A_library_change_that_moves_a_group_touches_the_approvals_key_and_one_that_keeps_it_does_not()
    {
        async Task<(LoaderHarness, ModelStore)> Open(string stages) => await OpenAsync(null, WithLibrary(stages), UsesPolicy);
        var (h1, before) = await Open(OneStage());
        var (h2, delegated) = await Open(OneStage($$""","delegateTo":["{{Finance}}"]"""));
        var (h3, renamed) = await Open("[" + Stage(Review, "Review", """ "displayName":"Manager review", """, ManagersSig()) + "]");
        using var _1 = h1;
        using var _2 = h2;
        using var _3 = h3;
        await using var __1 = before;
        await using var __2 = delegated;
        await using var __3 = renamed;
        Assert.True(new ImpactKeys(before.Current!, delegated.Current!, [Library], [], [], []).IsTouched(ApprovalNaming.Key));
        Assert.False(new ImpactKeys(before.Current!, renamed.Current!, [Library], [], [], []).IsTouched(ApprovalNaming.Key));
    }
}
