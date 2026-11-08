using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The resolved model of approvals, part 2 (signoff-design.md section 7 and decision 32): a stage's round and condition, a signature's
/// delegation, reminder, escalation and password keys with their durations as numbers, the <c>delegated</c> audit outcome, a step's
/// delegation and its expected notices, the records, and <c>model.approvals_use_*</c>, each read keyed only on <c>ap:approvals</c>. A model
/// that uses part 1 only resolves and records as before. The model is built in memory and invented.
/// </summary>
public sealed class ResolvedApprovalPart2Tests
{
    private const string Document = "01JRBE00000000000000000001";
    private const string Cost = "01JRBA00000000000000000001";
    private const string Engineer = "01JRBC00000000000000000001";
    private const string Controller = "01JRBC00000000000000000002";
    private const string Deputy = "01JRBC00000000000000000003";
    private const string Director = "01JRBC00000000000000000004";
    private const string Counsel = "01JRBC00000000000000000005";

    private const string Documents = "01JRBP00000000000000000001";
    private const string Policies = "01JRBP00000000000000000002";
    private const string Orders = "01JRBP00000000000000000003";
    private const string Draft = "01JRBS00000000000000000001";
    private const string Review = "01JRBS00000000000000000002";
    private const string Approved = "01JRBS00000000000000000003";
    private const string ApproveEvent = "01JRBX00000000000000000001";
    private const string Approve = "01JRBT00000000000000000001";
    private const string Open = "01JRBS00000000000000000011";
    private const string Closed = "01JRBS00000000000000000012";
    private const string CloseEvent = "01JRBX00000000000000000011";
    private const string Close = "01JRBT00000000000000000011";

    private const string Release = "01JRBG00000000000000000001";
    private const string ReviewStage = "01JRBG00000000000000000002";
    private const string FinanceStage = "01JRBG00000000000000000003";
    private const string LegalStage = "01JRBG00000000000000000004";
    private const string Engineering = "01JRBG00000000000000000005";
    private const string Finance = "01JRBG00000000000000000006";
    private const string Legal = "01JRBG00000000000000000007";
    private const string Board = "01JRBG00000000000000000011";

    private const string Scenario = "01JRBN00000000000000000001";
    private const string Ana = "01JRBN00000000000000000002";
    private const string Dee = "01JRBN00000000000000000003";
    private const string DelegateStep = "01JRBN00000000000000000004";
    private const string OnBehalfStep = "01JRBN00000000000000000005";
    private const string TimeStep = "01JRBN00000000000000000006";

    private static GateMeaning[] Meanings(string id) => [new GateMeaning { Id = id, Name = "approved" }];

    private static NamedSignature Engineers => new() { Id = Engineering, Name = "Engineering", Signers = [Engineer] };

    /// <summary>The finance signature with every part 2 key, or none when <paramref name="part2"/> is false.</summary>
    private static NamedSignature FinanceSignature(bool part2) => part2
        ? new NamedSignature
        {
            Id = Finance, Name = "Controller", Signers = [Controller], DelegateTo = [Deputy], RemindAfter = "P2D", EscalateAfter = "PT36H",
            EscalateTo = [Director], Reauthenticate = true,
        }
        : new NamedSignature { Id = Finance, Name = "Controller", Signers = [Controller] };

    /// <summary>The release approval: one stage in part 1; with <paramref name="rounds"/>, finance and legal side by side in round 2.</summary>
    private static ProcessGate ReleaseGate(bool rounds, bool part2, bool stageWhen = false) => new()
    {
        Id = Release, Name = "Release", Meanings = Meanings("01JRBM00000000000000000001"),
        Stages = rounds
            ?
            [
                new GateStage { Id = ReviewStage, Name = "Review", Signatures = [Engineers] },
                new GateStage { Id = FinanceStage, Name = "Finance", Round = 2, Signatures = [FinanceSignature(part2)] },
                new GateStage
                {
                    Id = LegalStage, Name = "Legal", Round = 2, When = new Expr { Text = "subject.cost > 10000" },
                    Signatures = [new NamedSignature { Id = Legal, Name = "Counsel", Signers = [Counsel] }],
                },
            ]
            : [new GateStage { Id = ReviewStage, Name = "Review", When = stageWhen ? new Expr { Text = "subject.cost > 0" } : null, Signatures = [Engineers, FinanceSignature(part2)] }],
    };

    private static ModelSnapshot Model(bool rounds = true, bool part2 = true, ProcessGate? policy = null, bool stageWhen = false)
    {
        var documents = new Process
        {
            Id = Documents, Name = "Documents", Use = ProcessUse.Lifecycle,
            SubjectList = [new ProcessSubject { Id = "01JRBB00000000000000000001", Entity = Document }],
            States = [new ProcessState { Id = Draft, Name = "Draft" }, new ProcessState { Id = Review, Name = "Review" }, new ProcessState { Id = Approved, Name = "Approved" }],
            Events = [new ProcessEvent { Id = ApproveEvent, Name = "approve" }],
            Initial = Review,
            Transitions = [new ProcessTransition { Id = Approve, Source = Review, Event = ApproveEvent, Targets = [Approved], Gate = ReleaseGate(rounds, part2, stageWhen) }],
        };
        var steps = part2
            ? new List<ScenarioStep>
            {
                new() { Id = DelegateStep, Event = ApproveEvent, Participant = Ana, Response = SignatureResponse.Delegate, DelegateTo = Dee },
                new() { Id = OnBehalfStep, Event = ApproveEvent, Participant = Dee, OnBehalfOf = Ana },
                new()
                {
                    Id = TimeStep, Input = StepInput.Time, After = "P3D",
                    Expect = new StepExpectation
                    {
                        Notices = [new ExpectedNotice { Signature = Finance, Kind = NoticeKind.Remind }, new ExpectedNotice { Signature = "01JRBG99999999999999999999", Kind = NoticeKind.Escalate },
                            new ExpectedNotice { Signature = Finance, Kind = NoticeKind.Escalate }],
                    },
                },
            }
            : [new ScenarioStep { Id = DelegateStep, Event = ApproveEvent, Participant = Ana, Expect = new StepExpectation { States = [Review] } }];

        List<Element> elements =
        [
            new Entity { Id = Document, Name = "Document", Attributes = [new ModelAttribute { Id = Cost, Name = "cost", Type = new TypeRef { Builtin = "decimal" } }] },
            new Actor { Id = Engineer, Name = "Engineer", Type = ActorType.Role },
            new Actor { Id = Controller, Name = "Controller", Type = ActorType.Role },
            new Actor { Id = Deputy, Name = "DeputyController", Type = ActorType.Role },
            new Actor { Id = Director, Name = "FinanceDirector", Type = ActorType.Role },
            new Actor { Id = Counsel, Name = "Counsel", Type = ActorType.Role },
            documents,
            new Scenario
            {
                Id = Scenario, Name = "handover", Process = Documents,
                Participants =
                [
                    new ScenarioParticipant { Id = Ana, Name = "Ana", Identity = JsonSerializer.SerializeToElement("ana"), Actor = Controller },
                    new ScenarioParticipant { Id = Dee, Name = "Dee", Identity = JsonSerializer.SerializeToElement("dee"), Actor = Deputy },
                ],
                Steps = steps,
            },
        ];
        if (policy is not null)
        {
            elements.Add(new Process { Id = Policies, Name = "Policies", Use = ProcessUse.Library, Gates = [policy] });
            elements.Add(new Process
            {
                Id = Orders, Name = "Orders", Use = ProcessUse.Lifecycle,
                States = [new ProcessState { Id = Open, Name = "Open" }, new ProcessState { Id = Closed, Name = "Closed" }],
                Events = [new ProcessEvent { Id = CloseEvent, Name = "close" }],
                Transitions = [new ProcessTransition { Id = Close, Source = Open, Event = CloseEvent, Targets = [Closed], GatePolicy = Board }],
            });
        }

        var docs = elements.Select((e, i) => Doc(e, $"model/x/{i:D2}-{e.Name}.json")).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        return ModelSnapshot.Create(docs, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
    }

    private static ElementDocument Doc(Element element, string path)
    {
        var bytes = TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
        var hash = ContentHash.Of(bytes);
        using var json = JsonDocument.Parse(bytes);
        return new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null);
    }

    private static RGate GateOf(ResolvedModel model) => model.Processes.Single(p => p.Id == Documents).Gates.Single(g => g.Id == Release);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Stages_resolve_their_round_and_condition_and_signatures_their_part_2_keys()
    {
        var model = ResolutionKit.Resolve(Model());
        var gate = GateOf(model);
        Assert.Equal([("Review", 1, false), ("Finance", 2, false), ("Legal", 2, true)], gate.Stages.Select(s => (s.Name, s.Round, s.When is not null)));
        var legal = gate.Stages[2];
        Assert.Equal("subject.cost > 10000", legal.When!.Text);
        Assert.Contains("e:" + Document, legal.Dependencies); // it reads the object

        var finance = gate.AllSignatures.Single(s => s.Id == Finance);
        Assert.Equal([Deputy], finance.DelegateTo.Select(a => a.Id));
        Assert.Equal([Director], finance.EscalateTo.Select(a => a.Id));
        Assert.True(finance.Reauthenticate);
        Assert.Equal(("P2D", 172_800_000L, 1_728_000_000_000L), (finance.RemindAfter, finance.RemindAfterMs, finance.RemindAfterTicks));
        Assert.Equal(("PT36H", 129_600_000L, 1_296_000_000_000L), (finance.EscalateAfter, finance.EscalateAfterMs, finance.EscalateAfterTicks));
        Assert.Contains("k:actor", finance.DelegateTo.MembershipKeys);

        // A signature without them, and a stage without them.
        var engineering = gate.AllSignatures.Single(s => s.Id == Engineering);
        Assert.Equal((0, 0, false, null, null, null), (engineering.DelegateTo.Count, engineering.EscalateTo.Count, engineering.Reauthenticate,
            engineering.RemindAfter, engineering.RemindAfterMs, engineering.EscalateAfterTicks));
        Assert.Null(gate.Stages[0].When);

        // A signature that may be delegated adds the delegated outcome, and the fields naming the delegator and who a delegation went to.
        Assert.Equal(["signed", "completed", "refused", "discarded", "rejected", "ignored", "withdrawn", "delegated"], gate.Audit.Single(f => f.Name == "outcome").Values);
        Assert.Equal(["instance", "process", "gate", "transition", "sequence", "signer", "actor", "meaning", "reason", "at", "outcome", "signature", "stage", "onBehalfOf", "delegateTo"],
            gate.Audit.Select(f => f.Name));
        Assert.Equal([("string", false), ("string", false)], gate.Audit.Where(f => f.Name is "onBehalfOf" or "delegateTo").Select(f => (f.Type, f.Required)));
    }

    [Fact]
    public void Steps_resolve_their_delegation_and_expected_notices()
    {
        var steps = Assert.Single(ResolutionKit.Resolve(Model()).Scenarios).Steps;
        Assert.Equal(("delegate", "Dee", null), (steps[0].Response, steps[0].DelegateTo?.Name, steps[0].OnBehalfOf?.Name));
        Assert.Equal(("sign", null, "Ana"), (steps[1].Response, steps[1].DelegateTo?.Name, steps[1].OnBehalfOf?.Name));
        Assert.Null(steps[1].Expect);
        // As written; an id of no signature is left out.
        Assert.Equal([(Finance, "remind"), (Finance, "escalate")], steps[2].Expect!.Notices!.Select(n => (n.Signature.Id, n.Kind)));

        var plain = Assert.Single(Assert.Single(ResolutionKit.Resolve(Model(part2: false)).Scenarios).Steps);
        Assert.Equal((null, null), (plain.DelegateTo, plain.OnBehalfOf));
        Assert.Null(plain.Expect!.Notices);
    }

    [Fact]
    public void Each_approvals_use_flag_answers_for_its_group_alone()
    {
        var all = ResolutionKit.Resolve(Model());
        Assert.Equal((true, true, true, true), (all.ApprovalsUseRounds, all.ApprovalsUseDelegation, all.ApprovalsUseNotices, all.ApprovalsUseReauthenticate));
        var part1 = ResolutionKit.Resolve(Model(rounds: false, part2: false));
        Assert.Equal((false, false, false, false), (part1.ApprovalsUseRounds, part1.ApprovalsUseDelegation, part1.ApprovalsUseNotices, part1.ApprovalsUseReauthenticate));
        var roundsOnly = ResolutionKit.Resolve(Model(part2: false));
        Assert.Equal((true, false, false, false), (roundsOnly.ApprovalsUseRounds, roundsOnly.ApprovalsUseDelegation, roundsOnly.ApprovalsUseNotices, roundsOnly.ApprovalsUseReauthenticate));
        var keysOnly = ResolutionKit.Resolve(Model(rounds: false));
        Assert.Equal((false, true, true, true), (keysOnly.ApprovalsUseRounds, keysOnly.ApprovalsUseDelegation, keysOnly.ApprovalsUseNotices, keysOnly.ApprovalsUseReauthenticate));

        // One stage with a when uses rounds too.
        Assert.True(ResolutionKit.Resolve(Model(rounds: false, part2: false, stageWhen: true)).ApprovalsUseRounds);

        // A gate policy counts once a lifecycle uses it.
        var policy = new ProcessGate
        {
            Id = Board, Name = "Board", Meanings = Meanings("01JRBM00000000000000000011"),
            Stages = [new GateStage { Id = "01JRBG00000000000000000012", Name = "Board", Signatures = [new NamedSignature { Id = "01JRBG00000000000000000013", Name = "Chair", Signers = [Director], Reauthenticate = true }] }],
        };
        var viaPolicy = ResolutionKit.Resolve(Model(rounds: false, part2: false, policy: policy));
        Assert.Equal((false, false, false, true), (viaPolicy.ApprovalsUseRounds, viaPolicy.ApprovalsUseDelegation, viaPolicy.ApprovalsUseNotices, viaPolicy.ApprovalsUseReauthenticate));
    }

    [Fact]
    public async Task Reading_an_approvals_use_flag_keys_only_on_the_approvals_naming_key()
    {
        var model = ResolutionKit.Resolve(Model());
        foreach (var flag in new[] { "rounds", "delegation", "notices", "reauthenticate" })
        {
            var unit = await Adhoc.RenderAsync("{{ model.approvals_use_" + flag + " }}", model: model);
            Assert.Equal("true", unit.Text());
            Assert.Equal([ApprovalNaming.Key, "t:adhoc/main.scriban"], unit.ReadKeys);
        }

        var part1 = await Adhoc.RenderAsync("{{ model.approvals_use_rounds }}", model: ResolutionKit.Resolve(Model(rounds: false, part2: false)));
        Assert.Equal("false", part1.Text());
    }

    [Fact]
    public void Records_carry_the_part_2_keys_where_present()
    {
        var model = ResolutionKit.Resolve(Model());
        var documents = (ProcessRecord)Engine.ResolvedRecords.Entries(model, "processes", null).Single(e => e.Id == Documents).Project();
        var stages = documents.Gates.Single(g => g.Id == Release).Stages!;
        Assert.Equal([(null, false), (2, false), (2, true)], stages.Select(s => (s.Round, s.When is not null)));
        Assert.Equal("subject.cost > 10000", stages[2].When!.Text);
        var finance = stages[1].Signatures.Single();
        Assert.Equal([Deputy], finance.DelegateTo);
        Assert.Equal([Director], finance.EscalateTo);
        Assert.Equal(("P2D", "PT36H", true), (finance.RemindAfter, finance.EscalateAfter, finance.Reauthenticate));
        var json = JsonSerializer.Serialize(stages[0], Web);
        foreach (var member in new[] { "\"round\"", "\"when\"", "\"delegateTo\"", "\"remindAfter\"", "\"escalateAfter\"", "\"escalateTo\"", "\"reauthenticate\"" })
            Assert.DoesNotContain(member, json, StringComparison.Ordinal);
        Assert.Contains("\"round\":2,\"when\":{", JsonSerializer.Serialize(stages[2], Web), StringComparison.Ordinal);

        var steps = ((ScenarioRecord)Engine.ResolvedRecords.Entries(model, "scenarios", null).Single().Project()).Steps;
        Assert.Equal(("delegate", Dee, null), (steps[0].Response, steps[0].DelegateTo, steps[0].OnBehalfOf));
        Assert.Equal((null, null, Ana), (steps[1].Response, steps[1].DelegateTo, steps[1].OnBehalfOf));
        Assert.Equal([new ExpectedNoticeRecord(Finance, "remind"), new ExpectedNoticeRecord(Finance, "escalate")], steps[2].Expect!.Notices);
        Assert.Contains("\"notices\":[{\"signature\":\"" + Finance + "\",\"kind\":\"remind\"}", JsonSerializer.Serialize(steps[2], Web), StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_that_uses_part_1_only_resolves_and_records_as_before()
    {
        var model = ResolutionKit.Resolve(Model(rounds: false, part2: false));
        var gate = GateOf(model);
        Assert.All(gate.Stages, s => Assert.Equal((1, null), (s.Round, s.When)));
        Assert.Equal(["signed", "completed", "refused", "discarded", "rejected", "ignored", "withdrawn"], gate.Audit.Single(f => f.Name == "outcome").Values);
        Assert.Equal("stage", gate.Audit[^1].Name); // no delegation fields without a signature that may be delegated

        var processes = Engine.ResolvedRecords.Entries(model, "processes", null).Select(e => (ProcessRecord)e.Project()).ToList();
        var scenario = (ScenarioRecord)Engine.ResolvedRecords.Entries(model, "scenarios", null).Single().Project();
        var json = JsonSerializer.Serialize(new object[] { processes, scenario }, Web);
        foreach (var member in new[] { "\"round\"", "\"delegateTo\"", "\"remindAfter\"", "\"escalateAfter\"", "\"escalateTo\"", "\"reauthenticate\"", "\"onBehalfOf\"", "\"notices\"", "delegated" })
            Assert.DoesNotContain(member, json, StringComparison.Ordinal);
    }
}
