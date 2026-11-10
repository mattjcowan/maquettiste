using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The resolved model of approvals (signoff-design.md section 7; section 15's <c>ResolvedApprovalTests</c>): <c>model.approval_lifecycles</c>,
/// the gate, stage and signature members, <c>actor.signatures</c>, a step's response and signature and its expected signatures, the audit
/// fields, the records, and the <c>ap:approvals</c> key: it changes on a process's first approval and on the first use of a gate policy with
/// stages, not on a signature edit, and never in a project without approvals. The model is built in memory and invented.
/// </summary>
public sealed class ResolvedApprovalTests
{
    private const string Document = "01JRAE00000000000000000001";
    private const string Order = "01JRAE00000000000000000002";
    private const string Cost = "01JRAA00000000000000000001";
    private const string Engineer = "01JRAC00000000000000000001";
    private const string QualityLead = "01JRAC00000000000000000002";
    private const string Controller = "01JRAC00000000000000000003";

    private const string Documents = "01JRAP00000000000000000001";
    private const string Orders = "01JRAP00000000000000000002";
    private const string Policies = "01JRAP00000000000000000003";

    private const string Draft = "01JRAS00000000000000000001";
    private const string Review = "01JRAS00000000000000000002";
    private const string Approved = "01JRAS00000000000000000003";
    private const string Open = "01JRAS00000000000000000011";
    private const string Closed = "01JRAS00000000000000000012";
    private const string SubmitEvent = "01JRAX00000000000000000001";
    private const string ApproveEvent = "01JRAX00000000000000000002";
    private const string CloseEvent = "01JRAX00000000000000000011";
    private const string Submit = "01JRAT00000000000000000001";
    private const string Approve = "01JRAT00000000000000000002";
    private const string Close = "01JRAT00000000000000000011";

    private const string Release = "01JRAG00000000000000000001";
    private const string Stage = "01JRAG00000000000000000002";
    private const string Engineering = "01JRAG00000000000000000003";
    private const string Quality = "01JRAG00000000000000000004";
    private const string Finance = "01JRAG00000000000000000005";
    private const string Board = "01JRAG00000000000000000011"; // a gate policy with stages
    private const string BoardStage = "01JRAG00000000000000000012";
    private const string BoardSignature = "01JRAG00000000000000000013";

    private const string Scenario = "01JRAC00000000000000000051";
    private const string RejectStep = "01JRAC00000000000000000052";

    private static Expr Text(string text) => new() { Text = text };

    private static GateMeaning[] Meanings(string id) => [new GateMeaning { Id = id, Name = "approved" }];

    /// <summary>The release approval on Documents' Approve move, or a gate with today's fields when <paramref name="approval"/> is false.</summary>
    private static ProcessGate ReleaseGate(bool approval, int engineers = 2) => approval
        ? new ProcessGate
        {
            Id = Release, Name = "Release", Meanings = Meanings("01JRAM00000000000000000001"), AllowSignerAcrossSignatures = true,
            Stages =
            [
                new GateStage
                {
                    Id = Stage, Name = "Review", DisplayName = "Review round",
                    Signatures =
                    [
                        new NamedSignature
                        {
                            Id = Engineering, Name = "Engineering", Signers = [Engineer], Required = engineers,
                            NotBy = [new NotByEntry { Verb = "promote" }, new NotByEntry { Transition = Submit }],
                            Description = new Description { Text = "Two engineers who did not submit it." },
                        },
                        new NamedSignature { Id = Quality, Name = "Quality", Signers = [QualityLead], RequiredActors = [QualityLead], AllowRepeatSigner = true },
                        new NamedSignature { Id = Finance, Name = "Finance", Signers = [Controller], When = Text("subject.cost > 10000") },
                    ],
                },
            ],
        }
        : new ProcessGate { Id = Release, Name = "Release", Meanings = Meanings("01JRAM00000000000000000001"), Signers = [Engineer], Required = 2 };

    internal static ModelSnapshot Model(bool approval = true, bool policyStages = true, bool policyUsed = false, Func<Element, Element>? edit = null)
    {
        var documents = new Process
        {
            Id = Documents, Name = "Documents", Use = ProcessUse.Lifecycle,
            SubjectList = [new ProcessSubject { Id = "01JRAB00000000000000000001", Entity = Document }],
            States = [new ProcessState { Id = Draft, Name = "Draft" }, new ProcessState { Id = Review, Name = "Review" }, new ProcessState { Id = Approved, Name = "Approved" }],
            Events = [new ProcessEvent { Id = SubmitEvent, Name = "submit", Verb = "promote" }, new ProcessEvent { Id = ApproveEvent, Name = "approve" }],
            Transitions =
            [
                new ProcessTransition { Id = Submit, Source = Draft, Event = SubmitEvent, Targets = [Review] },
                new ProcessTransition { Id = Approve, Source = Review, Event = ApproveEvent, Targets = [Approved], Gate = ReleaseGate(approval) },
            ],
        };
        var orders = new Process
        {
            Id = Orders, Name = "Orders", Use = ProcessUse.Lifecycle,
            SubjectList = [new ProcessSubject { Id = "01JRAB00000000000000000002", Entity = Order }],
            States = [new ProcessState { Id = Open, Name = "Open" }, new ProcessState { Id = Closed, Name = "Closed" }],
            Events = [new ProcessEvent { Id = CloseEvent, Name = "close" }],
            Transitions = [new ProcessTransition { Id = Close, Source = Open, Event = CloseEvent, Targets = [Closed], GatePolicy = policyUsed ? Board : null }],
        };
        var board = policyStages
            ? new ProcessGate
            {
                Id = Board, Name = "Board", Meanings = Meanings("01JRAM00000000000000000011"),
                Stages = [new GateStage { Id = BoardStage, Name = "Board", Signatures = [new NamedSignature { Id = BoardSignature, Name = "Chair", Signers = [Controller], NotBy = [new NotByEntry { Verb = "complete" }] }] }],
            }
            : new ProcessGate { Id = Board, Name = "Board", Meanings = Meanings("01JRAM00000000000000000011"), Signers = [Controller] };

        List<Element> elements =
        [
            new Entity { Id = Document, Name = "Document", Attributes = [new ModelAttribute { Id = Cost, Name = "cost", Type = new TypeRef { Builtin = "decimal" } }] },
            new Entity { Id = Order, Name = "Order" },
            new Actor { Id = Engineer, Name = "Engineer", Type = ActorType.Role },
            new Actor { Id = QualityLead, Name = "QualityLead", Type = ActorType.Role },
            new Actor { Id = Controller, Name = "Controller", Type = ActorType.Role },
            documents, orders,
            new Process { Id = Policies, Name = "Policies", Use = ProcessUse.Library, Gates = [board] },
            new Scenario
            {
                Id = Scenario, Name = "rejected", Process = Documents,
                Steps =
                [
                    new ScenarioStep
                    {
                        Id = RejectStep, Event = ApproveEvent, Actor = Controller, Signer = "dee", Reason = "No.",
                        Response = approval ? SignatureResponse.Reject : SignatureResponse.Sign, Signature = approval ? Finance : null,
                        Expect = new StepExpectation
                        {
                            States = [Review],
                            Signatures = approval ? new Dictionary<string, int> { [Finance] = 0, [Engineering] = 0, ["01JRAG99999999999999999999"] = 1 } : null,
                        },
                    },
                ],
            },
        ];
        if (edit is not null)
            elements = [.. elements.Select(edit)];
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

    // What a unit that read these keys hashes to over a snapshot: a changed hash renders or replays it again.
    private static string HashOf(ModelSnapshot snapshot, IEnumerable<string> keys) =>
        new DependencyHasher(snapshot, ResolutionKit.Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>())
            .InputHash("unit", [.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    private static RGate GateOf(ResolvedModel model) => model.Processes.Single(p => p.Id == Documents).Gates.Single(g => g.Id == Release);

    [Fact]
    public void An_approval_resolves_its_stages_and_named_signatures()
    {
        var model = ResolutionKit.Resolve(Model());
        var gate = GateOf(model);
        Assert.True(gate.UsesStages);
        Assert.True(gate.AllowSignerAcrossSignatures);
        Assert.Empty(gate.Signers);
        var stage = Assert.Single(gate.Stages);
        Assert.Equal(("gate-stage", "Review", "Review round"), (stage.Kind, stage.Name, stage.DisplayName));
        Assert.Same(gate, stage.Gate);
        Assert.Equal(["Engineering", "Quality", "Finance"], stage.Signatures.Select(s => s.Name));
        Assert.Equal(stage.Signatures.Select(s => s.Id), gate.AllSignatures.Select(s => s.Id));

        var engineering = gate.AllSignatures[0];
        Assert.Equal(("named-signature", 2, "Two engineers who did not submit it."), (engineering.Kind, engineering.Required, engineering.Description));
        Assert.Same(stage, engineering.Stage);
        Assert.Same(gate, engineering.Gate);
        Assert.Equal([Engineer], engineering.Signers.Select(a => a.Id));
        Assert.Null(engineering.When);
        Assert.Equal(2, engineering.NotBy.Count);
        Assert.Equal(("promote", "promote", null), (engineering.NotBy[0].VerbKey, engineering.NotBy[0].Verb?.Key, engineering.NotBy[0].Transition));
        Assert.Equal((null, Submit), (engineering.NotBy[1].VerbKey, engineering.NotBy[1].Transition?.Id));

        var quality = gate.AllSignatures[1];
        Assert.True(quality.AllowRepeatSigner);
        Assert.Equal([QualityLead], quality.RequiredActors.Select(a => a.Id));
        var finance = gate.AllSignatures[2];
        Assert.NotNull(finance.When);
        Assert.Equal("subject.cost > 10000", finance.When!.Text);
        Assert.Contains("e:" + Document, finance.Dependencies); // it reads the object

        // The audit names the signature and stage, with the responses' outcomes.
        Assert.Equal(["instance", "process", "gate", "transition", "sequence", "signer", "actor", "meaning", "reason", "at", "outcome", "signature", "stage"],
            gate.Audit.Select(f => f.Name));
        Assert.Equal(["signed", "completed", "refused", "discarded", "rejected", "ignored", "withdrawn"], gate.Audit.Single(f => f.Name == "outcome").Values);

        // A gate policy's signature: no when, and its stage and signature resolve in the library.
        var board = model.ProcessLibraries.Single().Gates.Single();
        Assert.Equal(["Chair"], board.AllSignatures.Select(s => s.Name));
        Assert.Contains("e:" + Policies, board.AllSignatures[0].Dependencies);
    }

    [Fact]
    public void Actors_steps_and_expectations_read_the_signatures()
    {
        var model = ResolutionKit.Resolve(Model(policyUsed: true));
        var controller = model.Actors.Single(a => a.Id == Controller);
        Assert.Equal([BoardSignature, Finance], controller.Signatures.Select(s => s.Id)); // by process, libraries first as for gates, then file order
        Assert.Contains(controller.Gates, g => g.Id == Release);
        Assert.Contains("r:" + Controller, controller.Signatures.MembershipKeys);
        Assert.Contains(model.Processes.Single(p => p.Id == Documents).Actors, a => a.Id == Engineer);

        var step = Assert.Single(Assert.Single(model.Scenarios).Steps);
        Assert.Equal("reject", step.Response);
        Assert.Equal(Finance, step.Signature?.Id);
        // In the gate's file order; an id of no signature is left out.
        Assert.Equal([(Engineering, 0), (Finance, 0)], step.Expect!.Signatures.Select(c => (c.Signature.Id, c.Count)));

        // A step that gives no response signs.
        var plain = Assert.Single(Assert.Single(ResolutionKit.Resolve(Model(approval: false)).Scenarios).Steps);
        Assert.Equal("sign", plain.Response);
        Assert.Null(plain.Signature);
        Assert.Empty(plain.Expect!.Signatures);
    }

    [Fact]
    public void Approval_lifecycles_list_the_processes_that_use_an_approval_inline_or_through_a_gate_policy()
    {
        var model = ResolutionKit.Resolve(Model(policyUsed: true));
        Assert.Equal(["Documents", "Orders"], model.ApprovalLifecycles.Select(p => p.Name));
        Assert.Equal([ApprovalNaming.Key], model.ApprovalLifecycles.MembershipKeys);

        // An unused gate policy with stages adds nothing; a gate policy without stages counts for nothing.
        Assert.Equal(["Documents"], ResolutionKit.Resolve(Model()).ApprovalLifecycles.Select(p => p.Name));
        Assert.Equal(["Documents"], ResolutionKit.Resolve(Model(policyStages: false, policyUsed: true)).ApprovalLifecycles.Select(p => p.Name));
    }

    [Fact]
    public void The_approvals_key_changes_on_a_first_approval_and_on_a_staged_gate_policys_first_use_not_on_a_signature_edit()
    {
        var none = Model(approval: false);
        var first = Model();
        var keys = ResolutionKit.Resolve(none).ApprovalLifecycles.MembershipKeys;
        Assert.NotEqual(ApprovalNaming.Hash(none), ApprovalNaming.Hash(first));
        Assert.NotEqual(HashOf(none, keys), HashOf(first, keys)); // the shared interpreter renders again

        // A signature's count, or a new signature, changes the process file only.
        var edited = Model(edit: e => e is Process { Id: Documents } p
            ? p with { Transitions = [p.Transitions[0], p.Transitions[1] with { Gate = ReleaseGate(true, engineers: 3) }] }
            : e);
        Assert.Equal(ApprovalNaming.Hash(first), ApprovalNaming.Hash(edited));
        Assert.Equal(HashOf(first, keys), HashOf(edited, keys));

        // A staged gate policy no process uses changes nothing; its first use renders the shared interpreter again.
        var unused = Model(policyStages: true, policyUsed: false);
        var plainPolicy = Model(policyStages: false, policyUsed: false);
        Assert.Equal(ApprovalNaming.Hash(plainPolicy), ApprovalNaming.Hash(unused));
        var used = Model(policyStages: true, policyUsed: true);
        Assert.NotEqual(HashOf(unused, keys), HashOf(used, keys));
        // Each user of a gate policy keys on the library's file, so a change to its stages renders it again.
        Assert.Contains("e:" + Policies, ResolutionKit.Resolve(used).Processes.Single(p => p.Id == Orders).Dependencies);
    }

    [Fact]
    public void A_project_without_approvals_resolves_empty_lists_and_keeps_its_keys_and_records()
    {
        var snapshot = Model(approval: false, policyStages: false, policyUsed: true);
        var model = ResolutionKit.Resolve(snapshot);
        Assert.Empty(model.ApprovalLifecycles);
        Assert.Equal([ApprovalNaming.Key], model.ApprovalLifecycles.MembershipKeys);
        var gate = GateOf(model);
        Assert.False(gate.UsesStages);
        Assert.Empty(gate.AllSignatures);
        Assert.False(gate.AllowSignerAcrossSignatures);
        Assert.Equal(2, gate.Required);
        Assert.Equal("outcome", gate.Audit[^1].Name);
        Assert.Equal(["signed", "completed", "refused", "discarded"], gate.Audit[^1].Values);
        Assert.All(model.Actors, a => Assert.Empty(a.Signatures));

        // The key's hash is the same for any project without approvals.
        var renamed = Model(approval: false, policyStages: false, edit: e => e is Entity { Id: Document } d ? d with { Name = "Paper" } : e);
        Assert.Equal(ApprovalNaming.Hash(snapshot), ApprovalNaming.Hash(renamed));
        Assert.Equal(HashOf(snapshot, model.ApprovalLifecycles.MembershipKeys), HashOf(renamed, model.ApprovalLifecycles.MembershipKeys));

        // Records carry none of the new members.
        var processes = Engine.ResolvedRecords.Entries(model, "processes", null).Select(e => (ProcessRecord)e.Project()).ToList();
        var scenario = (ScenarioRecord)Engine.ResolvedRecords.Entries(model, "scenarios", null).Single().Project();
        var json = JsonSerializer.Serialize(new object[] { processes, scenario }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var member in new[] { "\"stages\"", "\"allowSignerAcrossSignatures\"", "\"response\"", "\"signature\"", "\"signatures\"" })
            Assert.DoesNotContain(member, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_carry_stages_signatures_responses_and_expected_signatures_where_present()
    {
        var model = ResolutionKit.Resolve(Model());
        var documents = (ProcessRecord)Engine.ResolvedRecords.Entries(model, "processes", null).Single(e => e.Id == Documents).Project();
        var gate = documents.Gates.Single(g => g.Id == Release);
        Assert.True(gate.AllowSignerAcrossSignatures);
        var stage = Assert.Single(gate.Stages!);
        Assert.Equal(("Review", "Review round"), (stage.Name, stage.DisplayName));
        Assert.Equal([Engineering, Quality, Finance], stage.Signatures.Select(s => s.Id));
        var engineering = stage.Signatures[0];
        Assert.Equal((Stage, 2, false), (engineering.Stage, engineering.Required, engineering.AllowRepeatSigner));
        Assert.Equal([new NotByRecord("promote", null), new NotByRecord(null, Submit)], engineering.NotBy);
        Assert.Null(engineering.When);
        Assert.Equal("subject.cost > 10000", stage.Signatures[2].When!.Text);

        var json = JsonSerializer.Serialize(engineering, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"notBy\":[{\"verb\":\"promote\"},{\"transition\":\"" + Submit + "\"}]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"when\"", json, StringComparison.Ordinal);

        var step = Assert.Single(((ScenarioRecord)Engine.ResolvedRecords.Entries(model, "scenarios", null).Single().Project()).Steps);
        Assert.Equal(("reject", Finance), (step.Response, step.Signature));
        Assert.Equal(new Dictionary<string, int> { [Engineering] = 0, [Finance] = 0 }, step.Expect!.Signatures);
    }
}
