using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The trigger point sequence (trigger-points-design.md section 8's one evaluator; decisions 4, 10 to 13, 16 to 19 and 36; section 15's
/// <c>TriggerSequenceTests</c>): which lifecycles run their points per verb group; moments before lifecycles, the acting lifecycle first, then
/// the others by name, each in list order; every narrowing key, compound states in <c>states</c> and <c>targets</c>, <c>entities</c> with
/// derived entities and a stereotype's carriers; <c>attributes</c> and <c>ends</c> failing closed for checks only; <c>create</c>, <c>copy</c>
/// and <c>revise</c> states; a completing signature as two verbs in one candidate; no replace on a move; points narrowed to a move; an
/// orchestration with a subject; a run with no object; a move with no verb; and <c>get_trigger_sequence</c>'s answer. The document lifecycle
/// follows the design's section 5 example. The model is built in memory and invented.
/// </summary>
public sealed class TriggerSequenceTests
{
    // Entities and their parts.
    private const string Document = "01JTSE00000000000000000001";
    private const string Drawing = "01JTSE00000000000000000002"; // derived from Document
    private const string Sketch = "01JTSE00000000000000000003"; // derived from Drawing
    private const string Memo = "01JTSE00000000000000000004"; // derived from Document
    private const string Employee = "01JTSE00000000000000000005";
    private const string Contract = "01JTSE00000000000000000006"; // carries approvable
    private const string Amendment = "01JTSE00000000000000000007"; // derived from Contract
    private const string Price = "01JTSA00000000000000000001";
    private const string Title = "01JTSA00000000000000000002";
    private const string ArchivedOn = "01JTSA00000000000000000003";
    private const string Salary = "01JTSA00000000000000000004";
    private const string Reason = "01JTSA00000000000000000005"; // the escalate event's payload
    private const string Orders = "01JTSN00000000000000000001"; // an end of Document
    private const string Notes = "01JTSN00000000000000000002"; // another end
    private const string Approvable = "01JTSY00000000000000000001";

    // Processes.
    private const string Documents = "01JTSQ00000000000000000001";
    private const string Hr = "01JTSQ00000000000000000002";
    private const string Projects = "01JTSQ00000000000000000003";
    private const string Onboarding = "01JTSQ00000000000000000004"; // an orchestration of Employee
    private const string Approvals = "01JTSQ00000000000000000005"; // a lifecycle of what carries approvable
    private const string Mixed = "01JTSQ00000000000000000006"; // an orchestration of Employee and Document

    // States of Documents.
    private const string Draft = "01JTSS00000000000000000001";
    private const string Editing = "01JTSS00000000000000000002";
    private const string Polishing = "01JTSS00000000000000000003";
    private const string Review = "01JTSS00000000000000000004";
    private const string Checking = "01JTSS00000000000000000005"; // Review's initial child
    private const string Signing = "01JTSS00000000000000000006";
    private const string Released = "01JTSS00000000000000000007";

    // Events and transitions of Documents.
    private const string Submit = "01JTST00000000000000000001"; // Draft -> Review, promote
    private const string Recall = "01JTST00000000000000000002"; // Review -> Draft, demote
    private const string Approve = "01JTST00000000000000000003"; // Signing -> Released, promote, gated
    private const string Escalate = "01JTST00000000000000000004"; // Draft -> Draft, own verb escalate
    private const string Withdraw = "01JTST00000000000000000005"; // Released -> Draft, an event that names delete
    private const string Tidy = "01JTST00000000000000000006"; // Editing -> Polishing, always, no verb

    // Trigger points of Documents, in list order.
    private const string AssignNumber = "01JTSP00000000000000000001";
    private const string TitleBeforeReview = "01JTSP00000000000000000002";
    private const string IntoChecking = "01JTSP00000000000000000003";
    private const string NoDemoteFromSigning = "01JTSP00000000000000000004";
    private const string StampRelease = "01JTSP00000000000000000005";
    private const string PublishToPortal = "01JTSP00000000000000000006";
    private const string Archive = "01JTSP00000000000000000007";
    private const string DrawingsOnly = "01JTSP00000000000000000008";
    private const string LargePriceDrop = "01JTSP00000000000000000009";
    private const string PriceHistory = "01JTSP00000000000000000010";
    private const string CreditLimit = "01JTSP00000000000000000011";
    private const string LinkAudit = "01JTSP00000000000000000012";
    private const string SignerNote = "01JTSP00000000000000000013";
    private const string NoSelfSign = "01JTSP00000000000000000014";
    private const string ReleaseReady = "01JTSP00000000000000000015";
    private const string EscalateFromEvent = "01JTSP00000000000000000016";
    private const string EscalateWithReason = "01JTSP00000000000000000017";
    private const string EscalateAnywhere = "01JTSP00000000000000000018";
    private const string ReleasedCopies = "01JTSP00000000000000000019";
    private const string InReview = "01JTSP00000000000000000020";
    private const string DocumentsModify = "01JTSP00000000000000000021";

    // Trigger points of the other lifecycles.
    private const string SalaryBand = "01JTSP00000000000000000031"; // HR
    private const string HrAudit = "01JTSP00000000000000000032"; // HR
    private const string HrPrepare = "01JTSP00000000000000000033"; // HR
    private const string ProjectPrepare = "01JTSP00000000000000000041"; // Projects
    private const string ProjectCheck = "01JTSP00000000000000000042"; // Projects
    private const string ApprovalCheck = "01JTSP00000000000000000051"; // Approvals

    private static Expr Text(string text) => new() { Text = text };

    private static Expr Payload => new() { Op = "isNotNull", Args = [new Expr { Path = [Reason] }] };

    private static ProcessTriggerPoint Point(string id, string name, TriggerPointType type, string verb, TriggerTiming? timing = null) =>
        new() { Id = id, Name = name, Type = type, Verbs = [verb], Timing = timing };

    private static ModelSnapshot Model(Func<Process, Process>? documents = null, Func<Process, Process>? hr = null, bool points = true)
    {
        var gate = new ProcessGate
        {
            Id = "01JTSG00000000000000000001", Name = "release", Signers = ["01JTSR00000000000000000001"],
            Meanings = [new GateMeaning { Id = "01JTSG00000000000000000002", Name = "approved" }],
        };
        var lifecycle = Lifecycle(Documents, "Documents", Document,
        [
            new ProcessState
            {
                Id = Draft, Name = "Draft", Type = StateType.Compound, Initial = Editing,
                States = [new ProcessState { Id = Editing, Name = "Editing" }, new ProcessState { Id = Polishing, Name = "Polishing" }],
            },
            new ProcessState
            {
                Id = Review, Name = "Review", Type = StateType.Compound, Initial = Checking,
                States = [new ProcessState { Id = Checking, Name = "Checking" }, new ProcessState { Id = Signing, Name = "Signing" }],
            },
            new ProcessState { Id = Released, Name = "Released" },
        ]) with
        {
            Events =
            [
                new ProcessEvent { Id = "01JTSX00000000000000000001", Name = "submit", Verb = "promote" },
                new ProcessEvent { Id = "01JTSX00000000000000000002", Name = "recall", Verb = "demote" },
                new ProcessEvent { Id = "01JTSX00000000000000000003", Name = "approve", Verb = "promote" },
                new ProcessEvent
                {
                    Id = "01JTSX00000000000000000004", Name = "escalate", Verb = "escalate",
                    Payload = [new ModelAttribute { Id = Reason, Name = "reason", Type = new TypeRef { Builtin = "string" } }],
                },
                new ProcessEvent { Id = "01JTSX00000000000000000005", Name = "withdraw", Verb = "delete" },
            ],
            Transitions =
            [
                new ProcessTransition { Id = Submit, Source = Draft, Event = "01JTSX00000000000000000001", Targets = [Review] },
                new ProcessTransition { Id = Recall, Source = Review, Event = "01JTSX00000000000000000002", Targets = [Draft] },
                new ProcessTransition { Id = Approve, Source = Signing, Event = "01JTSX00000000000000000003", Targets = [Released], Gate = gate },
                new ProcessTransition { Id = Escalate, Source = Draft, Event = "01JTSX00000000000000000004", Targets = [Draft] },
                new ProcessTransition { Id = Withdraw, Source = Released, Event = "01JTSX00000000000000000005", Targets = [Draft] },
                new ProcessTransition { Id = Tidy, Source = Editing, Trigger = TransitionTrigger.Always, Targets = [Polishing] },
            ],
            TriggerPoints = !points ? [] :
            [
                Point(AssignNumber, "AssignNumber", TriggerPointType.Action, "create", TriggerTiming.Before) with { States = [Draft] },
                Point(TitleBeforeReview, "TitleBeforeReview", TriggerPointType.Check, "promote") with
                {
                    Targets = [Review], Condition = Text("coalesce(subject.title, '') <> ''"),
                },
                Point(IntoChecking, "IntoChecking", TriggerPointType.Check, "promote") with { Targets = [Checking] },
                Point(NoDemoteFromSigning, "NoDemoteFromSigning", TriggerPointType.Check, "demote") with { States = [Signing] },
                Point(StampRelease, "StampRelease", TriggerPointType.Action, "promote", TriggerTiming.WithChange) with
                {
                    Targets = [Released], Sets = [new ProcessSet { Id = "01JTSV00000000000000000001", Attribute = Title, Value = Text("'released'") }],
                },
                Point(PublishToPortal, "PublishToPortal", TriggerPointType.Action, "promote", TriggerTiming.AfterCommit) with
                {
                    Transitions = [Approve], Outside = true,
                },
                Point(Archive, "Archive", TriggerPointType.Replace, "delete") with
                {
                    Sets = [new ProcessSet { Id = "01JTSV00000000000000000002", Attribute = ArchivedOn, Value = Text("now()") }],
                },
                Point(DrawingsOnly, "DrawingsOnly", TriggerPointType.Check, "modify") with { Entities = [Drawing] },
                Point(LargePriceDrop, "LargePriceDrop", TriggerPointType.Check, "modify") with
                {
                    Severity = TriggerSeverity.Warning, Attributes = [Price],
                    Condition = Text("coalesce(old.price, 0) = 0 or subject.price >= old.price * 0.9"),
                },
                Point(PriceHistory, "PriceHistory", TriggerPointType.Action, "modify", TriggerTiming.WithChange) with { Attributes = [Price] },
                Point(CreditLimit, "CreditLimit", TriggerPointType.Check, "connect") with { Ends = [Orders], Outside = true },
                Point(LinkAudit, "LinkAudit", TriggerPointType.Action, "connect", TriggerTiming.AfterCommit) with { Ends = [Orders] },
                Point(SignerNote, "SignerNote", TriggerPointType.Action, "sign", TriggerTiming.Before) with { Transitions = [Approve] },
                Point(NoSelfSign, "NoSelfSign", TriggerPointType.Check, "sign"),
                Point(ReleaseReady, "ReleaseReady", TriggerPointType.Check, "promote") with { Targets = [Released] },
                Point(EscalateFromEvent, "EscalateFromEvent", TriggerPointType.Check, "escalate") with { Transitions = [Escalate] },
                Point(EscalateWithReason, "EscalateWithReason", TriggerPointType.Check, "escalate") with { Condition = Payload },
                Point(EscalateAnywhere, "EscalateAnywhere", TriggerPointType.Check, "escalate"),
                Point(ReleasedCopies, "ReleasedCopies", TriggerPointType.Check, "copy") with { States = [Released] },
                Point(InReview, "InReview", TriggerPointType.Check, "demote") with { States = [Review], When = Text("true") },
                Point(DocumentsModify, "DocumentsModify", TriggerPointType.Action, "modify", TriggerTiming.Before),
            ],
        };
        List<Element> elements =
        [
            new Entity
            {
                Id = Document, Name = "Document",
                Attributes =
                [
                    new ModelAttribute { Id = Price, Name = "price", Type = new TypeRef { Builtin = "decimal" } },
                    new ModelAttribute { Id = Title, Name = "title", Type = new TypeRef { Builtin = "string" } },
                    new ModelAttribute { Id = ArchivedOn, Name = "archivedOn", Type = new TypeRef { Builtin = "datetime" } },
                ],
            },
            new Entity { Id = Drawing, Name = "Drawing", Base = Document },
            new Entity { Id = Sketch, Name = "Sketch", Base = Drawing },
            new Entity { Id = Memo, Name = "Memo", Base = Document },
            new Entity
            {
                Id = Employee, Name = "Employee",
                Attributes = [new ModelAttribute { Id = Salary, Name = "salary", Type = new TypeRef { Builtin = "decimal" } }],
            },
            new Stereotype { Id = Approvable, Key = "approvable", Name = "Approvable", AppliesTo = ["entity"] },
            new Entity { Id = Contract, Name = "Contract", Stereotypes = ["approvable"] },
            new Entity { Id = Amendment, Name = "Amendment", Base = Contract },
            (documents ?? (p => p))(lifecycle),
            (hr ?? (p => p))(Lifecycle(Hr, "HR", Employee, [new ProcessState { Id = "01JTSS00000000000000000011", Name = "Active" }]) with
            {
                TriggerPoints = !points ? [] :
                [
                    Point(SalaryBand, "SalaryBand", TriggerPointType.Check, "modify") with { Severity = TriggerSeverity.Warning, Attributes = [Salary] },
                    Point(HrAudit, "HrAudit", TriggerPointType.Action, "modify", TriggerTiming.AfterCommit),
                    Point(HrPrepare, "HrPrepare", TriggerPointType.Action, "modify", TriggerTiming.Before),
                ],
            }),
            Lifecycle(Projects, "Projects", Employee, [new ProcessState { Id = "01JTSS00000000000000000021", Name = "Open" }]) with
            {
                TriggerPoints = !points ? [] :
                [
                    Point(ProjectCheck, "ProjectCheck", TriggerPointType.Check, "modify"),
                    Point(ProjectPrepare, "ProjectPrepare", TriggerPointType.Action, "modify", TriggerTiming.Before),
                ],
            },
            new Process
            {
                Id = Approvals, Name = "Approvals", Use = ProcessUse.Lifecycle,
                SubjectList = [new ProcessSubject { Id = "01JTSB00000000000000000005", Stereotype = "approvable" }],
                States = [new ProcessState { Id = "01JTSS00000000000000000031", Name = "Pending" }],
                TriggerPoints = !points ? [] : [Point(ApprovalCheck, "ApprovalCheck", TriggerPointType.Check, "modify")],
            },
            new Process
            {
                Id = Onboarding, Name = "Onboarding", Use = ProcessUse.Orchestration,
                SubjectList = [new ProcessSubject { Id = "01JTSB00000000000000000004", Entity = Employee }],
                States = [new ProcessState { Id = "01JTSS00000000000000000041", Name = "Start" }],
            },
            new Process
            {
                Id = Mixed, Name = "Mixed", Use = ProcessUse.Orchestration,
                SubjectList =
                [
                    new ProcessSubject { Id = "01JTSB00000000000000000006", Entity = Employee },
                    new ProcessSubject { Id = "01JTSB00000000000000000007", Entity = Document },
                ],
                States = [new ProcessState { Id = "01JTSS00000000000000000051", Name = "Start" }],
            },
        ];
        var documentsList = elements.Select((e, i) => Doc(e, $"model/x/{i:D2}-{e.Name}.json")).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        return ModelSnapshot.Create(documentsList, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
    }

    private static Process Lifecycle(string id, string name, string entity, IReadOnlyList<ProcessState> states) => new()
    {
        Id = id, Name = name, Use = ProcessUse.Lifecycle, SubjectList = [new ProcessSubject { Id = "01JTSB" + id[6..], Entity = entity }], States = states,
    };

    private static ElementDocument Doc(Element element, string path)
    {
        var bytes = TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
        var hash = ContentHash.Of(bytes);
        using var json = JsonDocument.Parse(bytes);
        return new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null);
    }

    private static TriggerVerbRun Run(ModelSnapshot model, string? verb, string? process, string? entity, string? state = null, string? transition = null,
        IReadOnlyList<string>? attributes = null, string? end = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? states = null) =>
        TriggerSequences.Of(model).For(new TriggerQuery
        {
            Verb = verb,
            Process = process is null ? null : model.Get<Process>(process),
            Entity = entity is null ? null : model.Get<Entity>(entity),
            States = states ?? (state is null || process is null
                ? new Dictionary<string, IReadOnlyList<string>>()
                : new Dictionary<string, IReadOnlyList<string>> { [process] = [state] }),
            Transition = transition,
            Attributes = attributes,
            End = end,
        });

    private static IReadOnlyList<string> Ids(IEnumerable<TriggerRun> runs) => [.. runs.Select(r => r.Point.Id)];

    private static IReadOnlyList<string> Rows(TriggerSequence sequence) =>
        [.. sequence.Steps.Select(s => string.Join(' ', new[] { s.Order.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Step, s.TriggerPoint }.OfType<string>()))];

    // ---- which lifecycles, and in what order ----

    [Fact]
    public void A_project_without_trigger_points_runs_none_and_answers_the_built_in_steps_alone()
    {
        var model = Model(points: false);
        var sequences = TriggerSequences.Of(model);

        Assert.False(sequences.Uses);
        Assert.Empty(sequences.Lifecycles);
        Assert.True(Run(model, "modify", Documents, Document).IsEmpty);
        Assert.True(Run(model, "promote", Documents, Document, Draft, Submit).IsEmpty);
        var described = sequences.Describe(model.Get<Process>(Documents)!, "delete", new TriggerSequenceFilter());
        Assert.Equal([(1, "frozen"), (10, "effect")], described.Steps.Select(s => (s.Order, s.Step)));
        Assert.All(described.Steps, s => Assert.Null(s.TriggerPoint));
    }

    [Fact]
    public void A_move_verb_runs_only_the_acting_lifecycles_points_and_a_move_with_no_verb_runs_none()
    {
        var model = Model();

        var submit = Run(model, "promote", Documents, Document, Polishing, Submit);
        Assert.Equal([Documents], submit.Lifecycles.Select(l => l.Id));
        Assert.Equal([TitleBeforeReview, IntoChecking], Ids(submit.Checks));
        Assert.Empty(submit.WithChange);
        Assert.Empty(submit.AfterCommit);
        Assert.True(Assert.Single(submit.InOrder.Take(1)).Acting);

        // A move with no verb (the always move Tidy) runs none, whatever the lifecycle holds.
        Assert.True(Run(model, null, Documents, Document, Editing, Tidy).IsEmpty);
        // A verb no point names runs none.
        Assert.True(Run(model, "freeze", Documents, Document).IsEmpty);
    }

    [Fact]
    public void Targets_count_a_compound_state_its_children_and_its_default_states_and_transitions_name_one_move()
    {
        var model = Model();
        var sequences = TriggerSequences.Of(model);

        // Submit enters Review and, by default, Checking: both TitleBeforeReview (Review) and IntoChecking (Checking) match.
        Assert.Equal([Review, Checking], sequences.Entered(model.Get<Process>(Documents)!, Submit));
        // Approve enters Released: ReleaseReady before, StampRelease with the change, PublishToPortal (on Approve) after commit.
        var approve = Run(model, "promote", Documents, Document, Signing, Approve);
        Assert.Equal([ReleaseReady], Ids(approve.Checks));
        Assert.Equal([StampRelease], Ids(approve.WithChange));
        Assert.Equal([PublishToPortal], Ids(approve.AfterCommit));
        // The run's own entered states, when given, are what targets reads: a move into Signing counts for Review, which holds it.
        var into = TriggerSequences.Of(model).For(new TriggerQuery
        {
            Verb = "promote", Process = model.Get<Process>(Documents), Entity = model.Get<Entity>(Document), Transition = Submit, Entered = [Signing],
        });
        Assert.Equal([TitleBeforeReview], Ids(into.Checks));
    }

    [Fact]
    public void States_read_the_source_configuration_where_a_compound_state_counts_for_its_children()
    {
        var model = Model();

        // NoDemoteFromSigning names Signing; InReview names the compound Review.
        Assert.Equal([NoDemoteFromSigning, InReview], Ids(Run(model, "demote", Documents, Document, Signing, Recall).Checks));
        Assert.Equal([InReview], Ids(Run(model, "demote", Documents, Document, Checking, Recall).Checks));
        // A lifecycle whose states the step doesn't give reads its initial state, Draft through Editing.
        Assert.Empty(Run(model, "demote", Documents, Document, transition: Recall).Checks);
    }

    [Fact]
    public void Create_reads_the_initial_state_and_copy_reads_the_source_states_given()
    {
        var model = Model();

        // AssignNumber names Draft, the initial state: create reads it whatever states are given.
        Assert.Equal([AssignNumber], Ids(Run(model, "create", Documents, Document, Released).Before));
        Assert.Equal([AssignNumber], Ids(Run(model, "create", Documents, Document).Before));
        // ReleasedCopies names Released: copy reads the source object's states, which the step gives.
        Assert.Equal([ReleasedCopies], Ids(Run(model, "copy", Documents, Document, Released).Checks));
        Assert.Empty(Run(model, "copy", Documents, Document, Polishing).Checks);
    }

    [Fact]
    public void Entities_narrow_to_an_entity_and_those_derived_from_it()
    {
        var model = Model();

        Assert.Contains(DrawingsOnly, Ids(Run(model, "modify", Documents, Drawing, attributes: [Title]).Checks));
        Assert.Contains(DrawingsOnly, Ids(Run(model, "modify", Documents, Sketch, attributes: [Title]).Checks));
        Assert.DoesNotContain(DrawingsOnly, Ids(Run(model, "modify", Documents, Document, attributes: [Title]).Checks));
        Assert.DoesNotContain(DrawingsOnly, Ids(Run(model, "modify", Documents, Memo, attributes: [Title]).Checks));

        // With no object, a point narrowed by entities runs only when they cover every subject: Drawing doesn't cover Document.
        var noObject = Run(model, "modify", Documents, null, attributes: [Title]);
        Assert.DoesNotContain(DrawingsOnly, Ids(noObject.Checks));
        Assert.Contains(noObject.Notes, n => n.Contains("'DrawingsOnly'", StringComparison.Ordinal));
        var covering = Model(documents: p => p with
        {
            TriggerPoints = [Point(DrawingsOnly, "AllDocuments", TriggerPointType.Check, "modify") with { Entities = [Document] }],
        });
        Assert.Equal([DrawingsOnly], Ids(Run(covering, "modify", Documents, null).Checks));
    }

    [Fact]
    public void Attributes_match_the_steps_own_change_and_an_unknown_change_runs_checks_but_not_actions()
    {
        var model = Model();

        var price = Run(model, "modify", Documents, Document, attributes: [Price, Title]);
        Assert.Equal([LargePriceDrop], Ids(price.Checks));
        Assert.Equal([PriceHistory], Ids(price.WithChange));
        Assert.Null(price.Checks[0].Assumed);
        var title = Run(model, "modify", Documents, Document, attributes: [Title]);
        Assert.Empty(title.Checks);
        Assert.Empty(title.WithChange);

        // An event that names modify doesn't say which attributes: the check runs, failing closed, and says so; the action doesn't.
        var unknown = Run(model, "modify", Documents, Document);
        Assert.Equal([LargePriceDrop], Ids(unknown.Checks));
        Assert.NotNull(unknown.Checks[0].Assumed);
        Assert.Empty(unknown.WithChange);
        Assert.Contains(unknown.Notes, n => n.Contains("action 'PriceHistory'", StringComparison.Ordinal) && n.Contains("does not run", StringComparison.Ordinal));
        Assert.Equal([DocumentsModify], Ids(unknown.Before)); // a point with no attributes runs whatever the step changes
    }

    [Fact]
    public void Ends_match_links_and_an_unknown_link_runs_checks_but_not_actions()
    {
        var model = Model();

        var orders = Run(model, "connect", Documents, Document, end: Orders);
        Assert.Equal([CreditLimit], Ids(orders.Checks));
        Assert.Equal([LinkAudit], Ids(orders.AfterCommit));
        Assert.True(Run(model, "connect", Documents, Document, end: Notes).IsEmpty);
        var unknown = Run(model, "connect", Documents, Document);
        Assert.Equal([CreditLimit], Ids(unknown.Checks));
        Assert.NotNull(unknown.Checks[0].Assumed);
        Assert.Empty(unknown.AfterCommit);

        // A point without ends speaks of the object: modifying a link through Orders doesn't run DocumentsModify.
        Assert.Empty(Run(model, "modify", Documents, Document, attributes: [Title], end: Orders).Before);
    }

    [Fact]
    public void Object_verbs_run_every_governing_lifecycle_acting_first_then_by_name_and_moments_come_before_lifecycles()
    {
        var model = Model();

        // Projects acts: its points come first at each moment, then HR's; within a lifecycle, list order.
        var fromProjects = Run(model, "modify", Projects, Employee, attributes: [Salary]);
        Assert.Equal([Projects, Hr], fromProjects.Lifecycles.Select(l => l.Id));
        Assert.Equal([ProjectPrepare, HrPrepare], Ids(fromProjects.Before));
        Assert.Equal([ProjectCheck, SalaryBand], Ids(fromProjects.Checks));
        Assert.Equal([HrAudit], Ids(fromProjects.AfterCommit));
        Assert.Equal([true, false], fromProjects.Checks.Select(c => c.Acting));
        Assert.Equal([ProjectPrepare, HrPrepare, ProjectCheck, SalaryBand, HrAudit], Ids(fromProjects.InOrder));

        // HR acts: HR first. Outside a process (the per-type runner) and from an orchestration: by name alone.
        Assert.Equal([Hr, Projects], Run(model, "modify", Hr, Employee).Lifecycles.Select(l => l.Id));
        Assert.Equal([Hr, Projects], Run(model, "modify", null, Employee).Lifecycles.Select(l => l.Id));
        var orchestrated = Run(model, "modify", Onboarding, Employee);
        Assert.Equal([Hr, Projects], orchestrated.Lifecycles.Select(l => l.Id));
        Assert.All(orchestrated.InOrder, r => Assert.False(r.Acting));
    }

    [Fact]
    public void A_run_with_no_object_asks_the_lifecycles_that_govern_every_subject_in_their_initial_state()
    {
        var model = Model();

        var onboarding = Run(model, "modify", Onboarding, null, attributes: [Salary]);
        Assert.Equal([Hr, Projects], onboarding.Lifecycles.Select(l => l.Id));
        Assert.Contains(onboarding.Notes, n => n.Contains("lifecycle 'HR' runs its trigger points in its initial state", StringComparison.Ordinal));

        // Mixed has Employee and Document: HR, Projects and Documents each govern only one, so none is asked.
        var mixed = Run(model, "modify", Mixed, null);
        Assert.Empty(mixed.Lifecycles);
        Assert.Contains(mixed.Notes, n => n.Contains("Lifecycle 'Documents' governs only some subjects of 'Mixed'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stereotype_entry_governs_its_carriers_and_their_derived_entities()
    {
        var model = Model();
        var sequences = TriggerSequences.Of(model);

        Assert.Equal([Approvals], Run(model, "modify", null, Amendment).Lifecycles.Select(l => l.Id));
        Assert.Equal([ApprovalCheck], Ids(Run(model, "modify", null, Contract).Checks));
        Assert.DoesNotContain(Approvals, Run(model, "modify", null, Employee).Lifecycles.Select(l => l.Id));
        Assert.Equal([Approvals], sequences.LifecyclesFor(model.Get<Entity>(Amendment)!).Select(l => l.Id));
        var approvals = model.Get<Process>(Approvals)!;
        Assert.Equal([Amendment, Contract], sequences.Covers(approvals, approvals.TriggerPoints[0]).Select(e => e.Id));
    }

    // ---- moves, replaces and signatures ----

    [Fact]
    public void An_event_that_names_delete_runs_no_replace_and_a_verb_step_does()
    {
        var model = Model();

        var byEvent = Run(model, "delete", Documents, Document, Released, Withdraw);
        Assert.Null(byEvent.Replace);
        Assert.Contains(byEvent.Notes, n => n.Contains("replace 'Archive' does not run", StringComparison.Ordinal));
        Assert.Equal(Archive, Run(model, "delete", Documents, Document, Released).Replace!.Point.Id);
        // A replace on a verb that allows none is never a moment of it (MQ8103).
        Assert.Null(TriggerSequences.MomentOf(Point(Archive, "X", TriggerPointType.Replace, "modify"), "modify"));
    }

    [Fact]
    public void One_replace_takes_the_place_of_the_effect_and_with_change_sets_on_delete_need_one()
    {
        var model = Model(documents: p => p with
        {
            TriggerPoints =
            [
                Point(Archive, "Archive", TriggerPointType.Replace, "delete") with { Entities = [Drawing] },
                Point(AssignNumber, "Shred", TriggerPointType.Replace, "delete"),
                Point(StampRelease, "StampDeleted", TriggerPointType.Action, "delete", TriggerTiming.WithChange) with
                {
                    Sets = [new ProcessSet { Id = "01JTSV00000000000000000003", Attribute = ArchivedOn, Value = Text("now()") }],
                },
            ],
        });

        var drawing = Run(model, "delete", Documents, Drawing);
        Assert.Equal(Archive, drawing.Replace!.Point.Id);
        Assert.Contains(drawing.Notes, n => n.Contains("replace 'Shred' does not run", StringComparison.Ordinal));
        var withNone = Run(Model(documents: p => p with
        {
            TriggerPoints =
            [
                Point(StampRelease, "StampDeleted", TriggerPointType.Action, "delete", TriggerTiming.WithChange) with
                {
                    Sets = [new ProcessSet { Id = "01JTSV00000000000000000003", Attribute = ArchivedOn, Value = Text("now()") }],
                },
            ],
        }), "delete", Documents, Document);
        Assert.Contains(withNone.Notes, n => n.Contains("the sets of 'StampDeleted' are skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void A_point_narrowed_to_a_move_on_an_own_verb_runs_only_when_its_lifecycles_event_sends_it()
    {
        var model = Model();

        // A verb step: neither the point on the Escalate transition nor the one that reads the payload runs.
        Assert.Equal([EscalateAnywhere], Ids(Run(model, "escalate", Documents, Document).Checks));
        // The event: all three.
        Assert.Equal([EscalateFromEvent, EscalateWithReason, EscalateAnywhere], Ids(Run(model, "escalate", Documents, Document, Draft, Escalate).Checks));
        // An own verb is an object verb: another lifecycle that governs the object asks its points, but a move of Documents never runs a
        // point of another lifecycle narrowed to a move, and outside a process none such runs.
        Assert.Equal([EscalateAnywhere], Ids(Run(model, "escalate", null, Document).Checks));
    }

    [Fact]
    public void A_completing_signature_runs_signs_rows_8_and_9_first_then_the_moves()
    {
        var model = Model();
        var move = new TriggerQuery
        {
            Verb = "promote", Process = model.Get<Process>(Documents), Entity = model.Get<Entity>(Document), Transition = Approve,
            States = new Dictionary<string, IReadOnlyList<string>> { [Documents] = [Signing] },
        };

        var completing = TriggerSequences.Of(model).Signature(move, completes: true);
        Assert.Equal([SignerNote, NoSelfSign, ReleaseReady], Ids(completing.Selection));
        Assert.Equal([StampRelease], Ids(completing.Move!.WithChange));
        var first = TriggerSequences.Of(model).Signature(move, completes: false);
        Assert.Null(first.Move);
        Assert.Equal([SignerNote, NoSelfSign], Ids(first.Selection));
        // sign's transitions narrow it to the gate signed: on another transition, only NoSelfSign.
        Assert.Equal([NoSelfSign], Ids(TriggerSequences.Of(model).Signature(move with { Transition = Submit }, false).Selection));
    }

    // ---- what a lifecycle holds, and get_trigger_sequence ----

    [Fact]
    public void A_lifecycles_points_by_verb_and_moment_whatever_their_places_and_cached_per_snapshot()
    {
        var model = Model();
        var sequences = TriggerSequences.Of(model);
        var documents = model.Get<Process>(Documents)!;

        Assert.Same(sequences, TriggerSequences.Of(model));
        Assert.Same(sequences.PointsOf(documents, "promote"), sequences.PointsOf(documents, "promote"));
        var promote = sequences.PointsOf(documents, "promote");
        Assert.Equal([TitleBeforeReview, IntoChecking, ReleaseReady], promote.Checks.Select(p => p.Id));
        Assert.Equal([StampRelease], promote.WithChange.Select(p => p.Id));
        Assert.Equal([PublishToPortal], promote.AfterCommit.Select(p => p.Id));
        // Built-in verbs in their table's order, then the project's own verbs.
        Assert.Equal(["promote", "demote", "sign", "create", "modify", "delete", "copy", "connect", "escalate"], sequences.VerbsOf(documents).Select(v => v.Verb));
        Assert.Equal([Approvals, Documents, Hr, Projects], sequences.Lifecycles.Select(l => l.Id));
        Assert.Empty(sequences.VerbsOf(model.Get<Process>(Onboarding)!));
        Assert.Equal([Documents], sequences.LifecyclesFor(model.Get<Entity>(Sketch)!).Select(l => l.Id));
        Assert.Equal([Drawing, Sketch], sequences.Covers(documents, documents.TriggerPoints.Single(p => p.Id == DrawingsOnly)).Select(e => e.Id));
    }

    [Fact]
    public void Describe_lists_the_built_in_steps_that_apply_and_each_point_with_its_places_in_run_order()
    {
        var model = Model();
        var sequences = TriggerSequences.Of(model);
        var documents = model.Get<Process>(Documents)!;

        // promote, with no transition: every move's points can run, the gate's signatures apply, and the built-in effect is the move.
        var promote = sequences.Describe(documents, "promote", new TriggerSequenceFilter());
        Assert.Equal(
            ["2 closed-lists", "5 no-move", "6 guards", "7 signatures", $"9 checks {TitleBeforeReview}", $"9 checks {IntoChecking}", $"9 checks {ReleaseReady}",
             "10 effect", $"11 with-change {StampRelease}", $"12 after-commit {PublishToPortal}"],
            Rows(promote));
        var publish = promote.Steps[^1];
        Assert.Equal((Documents, TriggerPointType.Action, TriggerTiming.AfterCommit, true), (publish.Lifecycle!, publish.Type!.Value, publish.Timing!.Value, publish.Outside == true));
        Assert.Equal([Approve], publish.Transitions!);
        Assert.Null(publish.Severity);
        Assert.Equal(TriggerSeverity.Error, promote.Steps[4].Severity);
        // With the transition: only that move's points; Submit has no gate.
        var submit = sequences.Describe(documents, "promote", new TriggerSequenceFilter { Transition = Submit });
        Assert.Equal([TitleBeforeReview, IntoChecking], submit.Steps.Where(s => s.TriggerPoint is not null).Select(s => s.TriggerPoint!));
        Assert.DoesNotContain(submit.Steps, s => s.Step == "signatures");

        // delete: freezing applies, the replace is the effect.
        var delete = sequences.Describe(documents, "delete", new TriggerSequenceFilter());
        Assert.Equal(["1 frozen", $"10 effect {Archive}"], Rows(delete));
        // demote with no state lists both points narrowed by states; with one, those that hold there; the when flag shows.
        Assert.Equal([NoDemoteFromSigning, InReview], sequences.Describe(documents, "demote", new TriggerSequenceFilter()).Steps.Where(s => s.Step == "checks").Select(s => s.TriggerPoint!));
        var fromChecking = sequences.Describe(documents, "demote", new TriggerSequenceFilter { State = Checking }).Steps.Single(s => s.Step == "checks");
        Assert.Equal(InReview, fromChecking.TriggerPoint);
        Assert.True(fromChecking.When);
        // modify with no attributes: the check narrowed by attributes is listed, the action narrowed so isn't; DrawingsOnly is listed with
        // no entity, and only for a drawing with one.
        var modify = sequences.Describe(documents, "modify", new TriggerSequenceFilter());
        Assert.Equal([DocumentsModify, DrawingsOnly, LargePriceDrop], modify.Steps.Where(s => s.TriggerPoint is not null).Select(s => s.TriggerPoint!));
        Assert.DoesNotContain(DrawingsOnly, sequences.Describe(documents, "modify", new TriggerSequenceFilter { Entity = Memo }).Steps.Select(s => s.TriggerPoint));
        Assert.Contains(PriceHistory, sequences.Describe(documents, "modify", new TriggerSequenceFilter { Attributes = [Price] }).Steps.Select(s => s.TriggerPoint));
        // An own verb as a verb step has no built-in effect; with its event, the move is the effect.
        Assert.DoesNotContain(sequences.Describe(documents, "escalate", new TriggerSequenceFilter()).Steps, s => s.Step == "effect");
        Assert.Contains(sequences.Describe(documents, "escalate", new TriggerSequenceFilter { Transition = Escalate }).Steps, s => s.Step == "effect");

        // The answer's JSON leaves out what doesn't apply.
        var json = JsonSerializer.Serialize(delete, EngineJson.Options);
        Assert.Equal($$"""{"lifecycle":"{{Documents}}","verb":"delete","steps":[{"order":1,"step":"frozen"},{"order":10,"step":"effect","triggerPoint":"{{Archive}}","lifecycle":"{{Documents}}","type":"replace"}]}""", json);
    }

    [Fact]
    public void Describe_names_the_rights_rows_that_apply_and_other_lifecycles_points()
    {
        var model = Model(hr: p => p with
        {
            RightsDefault = RightAccess.Deny,
            Rights = [new ProcessRight { Id = "01JTSR00000000000000000011", Name = "NoModify", Access = RightAccess.Deny, Verbs = ["modify"] }],
        });

        var modify = TriggerSequences.Of(model).Describe(model.Get<Process>(Projects)!, "modify", new TriggerSequenceFilter { Entity = Employee });
        Assert.Equal(
            ["1 frozen", "3 denies", "4 defaults", $"8 before {ProjectPrepare} {Projects}", $"8 before {HrPrepare} {Hr}", $"9 checks {ProjectCheck} {Projects}",
             $"9 checks {SalaryBand} {Hr}", "10 effect", $"12 after-commit {HrAudit} {Hr}"],
            modify.Steps.Select(s => string.Join(' ', new[] { s.Order.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Step, s.TriggerPoint, s.Lifecycle }.OfType<string>())));
    }
}
