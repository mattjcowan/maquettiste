using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The rights evaluator (rights-design.md decisions 4 to 14, 24 and 27; section 14's <c>RightsEvaluatorTests</c>): each lifecycle answers on
/// its own, its denies, then its allows, then its default, and any "no" wins; except never grants; closed lists count as allows that rights
/// only narrow; the refusal order and the refusing id; actors by membership, holdings and the object; states, compound states, conditions,
/// create and ends. The model is built in memory and invented.
/// </summary>
public sealed class RightsEvaluatorTests
{
    // Entities.
    private const string Document = "01JRVE00000000000000000001";
    private const string Employee = "01JRVE00000000000000000002";
    private const string Contract = "01JRVE00000000000000000003";
    private const string Components = "01JRVE00000000000000000004"; // an end of Document
    private const string Notes = "01JRVE00000000000000000005"; // another end of Document

    // Actors.
    private const string Author = "01JRVA00000000000000000001";
    private const string LeadAuthor = "01JRVA00000000000000000002"; // a member of Author
    private const string ChiefAuthor = "01JRVA00000000000000000003"; // a member of LeadAuthor
    private const string Reviewer = "01JRVA00000000000000000004";
    private const string HrStaff = "01JRVA00000000000000000005";
    private const string ProjectManager = "01JRVA00000000000000000006";
    private const string Owner = "01JRVA00000000000000000007"; // the object's owner
    private const string Clerk = "01JRVA00000000000000000008";
    private const string Approver = "01JRVA00000000000000000009";

    // Lifecycles and orchestrations.
    private const string Documents = "01JRVP00000000000000000001";
    private const string Hr = "01JRVP00000000000000000002";
    private const string Projects = "01JRVP00000000000000000003";
    private const string Flow = "01JRVP00000000000000000004"; // an orchestration of Employee
    private const string Loose = "01JRVP00000000000000000005"; // an orchestration with no subject
    private const string Approval = "01JRVP00000000000000000006"; // a gated approval of Contract

    // States.
    private const string Draft = "01JRVS00000000000000000001";
    private const string Review = "01JRVS00000000000000000002";
    private const string Released = "01JRVS00000000000000000003";
    private const string Editing = "01JRVS00000000000000000004"; // Draft's first child
    private const string Polishing = "01JRVS00000000000000000005"; // Draft's second child
    private const string Active = "01JRVS00000000000000000011";
    private const string Suspended = "01JRVS00000000000000000012";
    private const string Open = "01JRVS00000000000000000021";
    private const string Submitted = "01JRVS00000000000000000031";
    private const string Approved = "01JRVS00000000000000000032";

    // Rights, events, gates and tasks.
    private const string EveryoneReads = "01JRVR00000000000000000001";
    private const string AuthorsEditDrafts = "01JRVR00000000000000000002";
    private const string ReleasedIsReadOnly = "01JRVR00000000000000000003";
    private const string NoBomChangesInReview = "01JRVR00000000000000000004";
    private const string SecretUse = "01JRVR00000000000000000005";
    private const string SecretOnlyOwner = "01JRVR00000000000000000006"; // the named right
    private const string HrStaffModify = "01JRVR00000000000000000011";
    private const string PmModify = "01JRVR00000000000000000012";
    private const string NoSelfSign = "01JRVR00000000000000000021";
    private const string Submit = "01JRVX00000000000000000001";
    private const string Approve = "01JRVX00000000000000000002";
    private const string Gate = "01JRVX00000000000000000003";
    private const string Task = "01JRVX00000000000000000004";

    private static Expr Secret => new() { Value = JsonSerializer.SerializeToElement(true) };

    private static ModelSnapshot Model(Func<Process, Process>? documents = null, Func<Process, Process>? hr = null, Func<Process, Process>? projects = null,
        Func<Process, Process>? approval = null)
    {
        List<Element> elements =
        [
            new Entity { Id = Document, Name = "Document" },
            new Entity { Id = Employee, Name = "Employee" },
            new Entity { Id = Contract, Name = "Contract" },
            Actor(Author, "Author", ActorType.Role),
            Actor(LeadAuthor, "LeadAuthor", ActorType.Role, Author),
            Actor(ChiefAuthor, "ChiefAuthor", ActorType.Person, LeadAuthor),
            Actor(Reviewer, "Reviewer", ActorType.Role),
            Actor(HrStaff, "HrStaff", ActorType.Role),
            Actor(ProjectManager, "ProjectManager", ActorType.Role),
            new Actor { Id = Owner, Name = "DocumentOwner", Type = ActorType.Person, Relative = new ActorRelative { Owner = true } },
            Actor(Clerk, "Clerk", ActorType.Person),
            Actor(Approver, "Approver", ActorType.Role),
            new Right
            {
                Id = SecretOnlyOwner, Name = "SecretOnlyOwner", Access = RightAccess.Deny, Except = [Owner], Verbs = ["modify", "delete", "change-owner"],
                Condition = Secret,
            },
            (documents ?? (p => p))(Lifecycle(Documents, "Documents", Document,
                [new ProcessState
                    {
                        Id = Draft, Name = "Draft", Type = StateType.Compound, Initial = Editing,
                        States = [new ProcessState { Id = Editing, Name = "Editing" }, new ProcessState { Id = Polishing, Name = "Polishing" }],
                    },
                    new ProcessState { Id = Review, Name = "Review" }, new ProcessState { Id = Released, Name = "Released" }]) with
            {
                RightsDefault = RightAccess.Deny,
                Rights =
                [
                    new ProcessRight { Id = EveryoneReads, Name = "EveryoneReads", Access = RightAccess.Allow, Verbs = ["read"] },
                    new ProcessRight
                    {
                        Id = AuthorsEditDrafts, Name = "AuthorsEditDrafts", Access = RightAccess.Allow, Actors = [Author], Verbs = ["modify", "connect", "disconnect"],
                        States = [Draft],
                    },
                    new ProcessRight { Id = ReleasedIsReadOnly, Name = "ReleasedIsReadOnly", Access = RightAccess.Deny, Verbs = ["modify", "delete"], States = [Released] },
                    new ProcessRight
                    {
                        Id = NoBomChangesInReview, Name = "NoBomChangesInReview", Access = RightAccess.Deny, Verbs = ["connect", "disconnect", "freeze"],
                        Ends = [Components], States = [Review],
                    },
                    new ProcessRight { Id = SecretUse, Right = SecretOnlyOwner },
                ],
            }),
            (hr ?? (p => p))(Lifecycle(Hr, "HR", Employee, [new ProcessState { Id = Active, Name = "Active" }, new ProcessState { Id = Suspended, Name = "Suspended" }]) with
            {
                RightsDefault = RightAccess.Deny,
                Rights = [new ProcessRight { Id = HrStaffModify, Name = "HrStaffModify", Access = RightAccess.Allow, Actors = [HrStaff], Verbs = ["modify", "promote"] }],
            }),
            (projects ?? (p => p))(Lifecycle(Projects, "Projects", Employee, [new ProcessState { Id = Open, Name = "Open" }]) with
            {
                Rights = [new ProcessRight { Id = PmModify, Name = "PmModify", Access = RightAccess.Allow, Actors = [ProjectManager], Verbs = ["modify"] }],
                Events = [new ProcessEvent { Id = Submit, Name = "submit", Verb = "promote", Actors = [Clerk] }],
            }),
            new Process
            {
                Id = Flow, Name = "Onboarding", Use = ProcessUse.Orchestration, SubjectList = [new ProcessSubject { Id = "01JRVB00000000000000000004", Entity = Employee }],
                States = [new ProcessState { Id = "01JRVS00000000000000000041", Name = "Start" }],
            },
            new Process { Id = Loose, Name = "Loose", Use = ProcessUse.Orchestration, States = [new ProcessState { Id = "01JRVS00000000000000000051", Name = "Start" }] },
            (approval ?? (p => p))(Lifecycle(Approval, "Approval", Contract,
                [new ProcessState { Id = Submitted, Name = "Submitted" }, new ProcessState { Id = Approved, Name = "Approved" }]) with
            {
                RightsDefault = RightAccess.Deny,
                Events = [new ProcessEvent { Id = Approve, Name = "approve", Verb = "promote" }],
                Rights = [new ProcessRight { Id = NoSelfSign, Name = "NoSelfSign", Access = RightAccess.Deny, Verbs = ["sign"], Condition = Secret }],
            }),
        ];
        var documentsList = elements.Select((e, i) => Doc(e, $"model/x/{i:D2}-{e.Name}.json")).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        return ModelSnapshot.Create(documentsList, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
    }

    private static Actor Actor(string id, string name, ActorType type, params string[] memberOf) =>
        new() { Id = id, Name = name, Type = type, MemberOf = memberOf };

    private static Process Lifecycle(string id, string name, string entity, IReadOnlyList<ProcessState> states) => new()
    {
        Id = id, Name = name, Use = ProcessUse.Lifecycle, SubjectList = [new ProcessSubject { Id = "01JRVB" + id[6..], Entity = entity }], States = states,
    };

    private static ElementDocument Doc(Element element, string path)
    {
        var bytes = TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
        var hash = ContentHash.Of(bytes);
        using var json = JsonDocument.Parse(bytes);
        return new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null);
    }

    private static RightsDecision Check(ModelSnapshot model, string verb, string actsAs, string process, string? entity, string? state = null,
        Func<RightRule, bool?>? condition = null, string? end = null, IReadOnlyList<string>? held = null)
    {
        var p = model.Get<Process>(process)!;
        return RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = verb, Process = p, Entity = entity is null ? null : model.Get<Entity>(entity), End = end,
            Caller = RightsCaller.Acting(model, actsAs, held),
            States = state is null ? new Dictionary<string, IReadOnlyList<string>>() : new Dictionary<string, IReadOnlyList<string>> { [process] = [state] },
            Condition = condition ?? (_ => false),
        });
    }

    private static void Refused(RightsDecision decision, string refusal, string by)
    {
        Assert.Equal(RightAnswer.Denied, decision.Answer);
        Assert.Equal((refusal, by), (decision.Refusal, decision.RefusedBy));
    }

    private static void Allowed(RightsDecision decision)
    {
        Assert.True(decision.Allowed, $"{decision.Answer} {decision.Refusal} {decision.RefusedBy}");
        Assert.Null(decision.Refusal);
        Assert.Null(decision.RefusedBy);
    }

    // ---- one lifecycle ----

    [Fact]
    public void One_lifecycle_answers_with_its_denies_then_its_allows_then_its_default()
    {
        var model = Model();
        // An allow holds in Draft (Editing is inside it); a deny holds in Released; nothing holds for delete in Draft, so the default denies.
        Allowed(Check(model, "modify", Author, Documents, Document, Editing));
        Refused(Check(model, "modify", Author, Documents, Document, Released), Refusals.Right, ReleasedIsReadOnly);
        Refused(Check(model, "delete", Author, Documents, Document, Editing), Refusals.Default, Documents);
        Allowed(Check(model, "read", Clerk, Documents, Document, Released)); // empty actors: everyone

        var decision = Check(model, "modify", Author, Documents, Document, Released);
        Assert.Equal([new RightEvaluation(ReleasedIsReadOnly, Documents, RightAccess.Deny)], decision.Rights);
        Assert.True(Assert.Single(decision.Lifecycles).Answer == RightAnswer.Denied);

        // Under allow, a lifecycle with no right that holds says yes by its default.
        var allowing = Model(documents: p => p with { RightsDefault = null });
        var byDefault = Check(allowing, "delete", Author, Documents, Document, Editing);
        Allowed(byDefault);
        Assert.True(Assert.Single(byDefault.Lifecycles).ByDefault);
    }

    [Fact]
    public void A_state_compound_state_and_create_read_the_configuration()
    {
        var model = Model();
        // AuthorsEditDrafts names the compound Draft: it holds in either child, not in Review.
        Allowed(Check(model, "modify", Author, Documents, Document, Polishing));
        Refused(Check(model, "modify", Author, Documents, Document, Review), Refusals.Default, Documents);
        // A lifecycle missing from the states reads as its initial state: Draft, through Editing.
        Allowed(Check(model, "modify", Author, Documents, Document));
        // create is checked in the initial state whatever states are given: an allow on create in Draft lets it through from Released.
        var creating = Model(documents: p => p with
        {
            Rights = [.. p.Rights, new ProcessRight { Id = "01JRVR00000000000000000099", Name = "AuthorsCreate", Access = RightAccess.Allow, Actors = [Author], Verbs = ["create"], States = [Draft] }],
        });
        Allowed(Check(creating, "create", Author, Documents, Document, Released));
    }

    [Fact]
    public void Conditions_hold_only_when_true_and_an_unknown_one_depends()
    {
        var model = Model();
        // SecretOnlyOwner denies modify when its condition holds; the callback decides it.
        Refused(Check(model, "modify", Author, Documents, Document, Editing, _ => true), Refusals.Right, SecretOnlyOwner);
        Allowed(Check(model, "modify", Author, Documents, Document, Editing, _ => false));
        var unknown = RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = "modify", Process = model.Get<Process>(Documents), Entity = model.Get<Entity>(Document), Caller = RightsCaller.Acting(model, Author),
        });
        Assert.Equal(RightAnswer.Depends, unknown.Answer);
        Assert.Equal([SecretOnlyOwner], Assert.Single(unknown.Lifecycles).By);
        Assert.Null(unknown.Refusal);
        // The callback sees the use with the named right's body.
        RightRule? seen = null;
        Check(model, "modify", Author, Documents, Document, Editing, r => { seen = r; return false; });
        Assert.Equal((SecretOnlyOwner, SecretUse, Documents, RightAccess.Deny), (seen!.Id, seen.Entry.Id, seen.Lifecycle.Id, seen.Access));
    }

    // ---- who ----

    [Fact]
    public void Membership_reaches_any_depth_for_allows_and_denies()
    {
        var model = Model();
        Allowed(Check(model, "modify", ChiefAuthor, Documents, Document, Editing)); // a member of a member of Author
        var denying = Model(documents: p => p with
        {
            Rights = [.. p.Rights, new ProcessRight { Id = "01JRVR00000000000000000098", Name = "NoAuthorDeletes", Access = RightAccess.Deny, Actors = [Author], Verbs = ["delete"] }],
            RightsDefault = null,
        });
        Refused(Check(denying, "delete", ChiefAuthor, Documents, Document, Editing), Refusals.Right, "01JRVR00000000000000000098");
        Allowed(Check(denying, "delete", Reviewer, Documents, Document, Editing));
        // A held actor counts as the acting one does (RightRequest.Held): a reviewer who holds LeadAuthor may edit a draft.
        Allowed(Check(model, "modify", Reviewer, Documents, Document, Editing, held: [LeadAuthor]));
        Refused(Check(denying, "delete", Reviewer, Documents, Document, Editing, held: [LeadAuthor]), Refusals.Right, "01JRVR00000000000000000098");
    }

    [Fact]
    public void Except_takes_an_actor_out_of_a_right_and_never_grants()
    {
        // Deny delete except LeadAuthor, under allow: an excepted actor (and its members) falls to the default, which allows.
        Process With(Process p, RightAccess? fallback) => p with
        {
            RightsDefault = fallback,
            Rights = [new ProcessRight { Id = "01JRVR00000000000000000097", Name = "NoDeletes", Access = RightAccess.Deny, Except = [LeadAuthor], Verbs = ["delete"] }],
        };
        var allowing = Model(documents: p => With(p, null));
        Refused(Check(allowing, "delete", Author, Documents, Document), Refusals.Right, "01JRVR00000000000000000097");
        Allowed(Check(allowing, "delete", LeadAuthor, Documents, Document));
        Allowed(Check(allowing, "delete", ChiefAuthor, Documents, Document));
        // Under deny the excepted actor is still refused, by the default: except never grants.
        var denying = Model(documents: p => With(p, RightAccess.Deny));
        Refused(Check(denying, "delete", LeadAuthor, Documents, Document), Refusals.Default, Documents);
    }

    [Fact]
    public void The_objects_owner_is_excepted_with_and_without_an_owner_field()
    {
        // The design's example (section 4): Carl and Bea are authors; Bea owns the secret spec. No owner field: the object's owner names her.
        var model = Model();
        var process = model.Get<Process>(Documents)!;
        var spec = new ScenarioObject { Id = "01JRVO00000000000000000001", Name = "spec", Entity = Document, Owner = "01JRVQ00000000000000000001" };
        var bea = new ScenarioParticipant { Id = "01JRVQ00000000000000000001", Name = "bea", Actor = Author };
        var carl = new ScenarioParticipant { Id = "01JRVQ00000000000000000002", Name = "carl", Actor = Author };
        var cast = new ScenarioCast([spec], spec.Id, [bea, carl]);
        RightsDecision As(ScenarioParticipant who) => RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = "modify", Process = process, Entity = model.Get<Entity>(Document), Caller = RightsCaller.Participant(model, process, cast, who, Author),
            Condition = _ => true, // subject.classification = 'secret'
        });
        Refused(As(carl), Refusals.Right, SecretOnlyOwner);
        var beas = As(bea);
        Allowed(beas);
        Assert.Equal([new RightEvaluation(AuthorsEditDrafts, Documents, RightAccess.Allow)], beas.Rights);

        // With an owner field, a caller that only acts as an actor names the owner exactly; outside a run it is unknown.
        Allowed(Check(model, "modify", Owner, Documents, Document, Editing, _ => true, held: [Author]));
        var unknown = RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = "modify", Process = process, Entity = model.Get<Entity>(Document), Caller = RightsCaller.Acting(model, Author, objectUnknown: true),
            Condition = _ => true,
        });
        Assert.Equal(RightAnswer.Depends, unknown.Answer);
    }

    [Fact]
    public void A_participant_counts_every_actor_it_satisfies_not_only_the_one_it_acts_as()
    {
        var model = Model(documents: p => p with
        {
            RightsDefault = null,
            Rights = [new ProcessRight { Id = "01JRVR00000000000000000096", Name = "NoReviewerDeletes", Access = RightAccess.Deny, Actors = [Reviewer], Verbs = ["delete"] }],
        });
        var process = model.Get<Process>(Documents)!;
        var dana = new ScenarioParticipant
        {
            Id = "01JRVQ00000000000000000003", Name = "dana", Actor = Author, Holds = [new ScenarioHolding { Id = "01JRVH00000000000000000001", Actor = Reviewer }],
        };
        var cast = new ScenarioCast([], null, [dana]);
        var decision = RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = "delete", Process = process, Caller = RightsCaller.Participant(model, process, cast, dana, Author),
        });
        Refused(decision, Refusals.Right, "01JRVR00000000000000000096");
    }

    // ---- across lifecycles ----

    [Fact]
    public void Across_lifecycles_any_no_wins_and_a_silent_lifecycle_that_allows_says_yes()
    {
        var model = Model();
        // HR denies by default and lets HR staff modify; Projects lets project managers modify. A manager is refused by HR's default.
        var manager = Check(model, "modify", ProjectManager, Projects, Employee);
        Refused(manager, Refusals.Default, Hr);
        Assert.Equal([Projects, Hr], manager.Lifecycles.Select(l => l.Lifecycle.Id));
        Assert.Equal(RightAnswer.Allowed, manager.Lifecycles[0].Answer);
        // HR staff: HR allows, and Projects allows by default since it says nothing for them.
        var staff = Check(model, "modify", HrStaff, Projects, Employee);
        Allowed(staff);
        Assert.Equal((false, true), (staff.Lifecycles[1].ByDefault, staff.Lifecycles[0].ByDefault));
        // A lifecycle that allows by default and states nothing is never asked.
        Allowed(Check(Model(projects: p => p with { Rights = [] }), "modify", HrStaff, Projects, Employee));
    }

    [Fact]
    public void Move_signature_and_task_verbs_are_answered_by_the_acting_lifecycle_alone()
    {
        var model = Model();
        // promote in Projects: HR's default doesn't reach it, though HR governs the object too.
        var promote = Check(model, "promote", Clerk, Projects, Employee);
        Allowed(promote);
        Assert.Equal([Projects], promote.Lifecycles.Select(l => l.Lifecycle.Id));
        Assert.Equal("move", RightsEvaluator.GroupOf("promote"));
        Assert.Equal(BuiltInVerbs.ProjectGroup, RightsEvaluator.GroupOf("archive"));
        Assert.True(RightsEvaluator.SpeaksOfObject("archive"));
        // An own verb is answered by every governing lifecycle: HR denies it by default.
        Refused(Check(model, "archive", ProjectManager, Projects, Employee), Refusals.Default, Hr);
    }

    [Fact]
    public void An_orchestrations_object_verbs_are_answered_by_its_subjects_lifecycles_by_name()
    {
        var model = Model();
        var onboarding = Check(model, "modify", ProjectManager, Flow, Employee);
        Refused(onboarding, Refusals.Default, Hr);
        Assert.Equal([Hr, Projects], onboarding.Lifecycles.Select(l => l.Lifecycle.Id)); // none is acting, so by name
        // Its own moves answer to closed lists only; an orchestration with no subject has no object to ask about.
        Assert.Empty(Check(model, "promote", ProjectManager, Flow, Employee).Lifecycles);
        Allowed(Check(model, "modify", ProjectManager, Loose, null));
        Assert.Empty(RightsEvaluator.Of(model).Answering(new RightsQuery { Verb = "modify", Process = model.Get<Process>(Loose), Caller = RightsCaller.Acting(model, null) }).Lifecycles);
    }

    [Fact]
    public void A_run_with_no_object_asks_the_lifecycles_that_govern_every_subject_in_their_initial_state()
    {
        var model = Model();
        var decision = Check(model, "modify", ProjectManager, Flow, null);
        Refused(decision, Refusals.Default, Hr);
        Assert.Contains(decision.Notes, n => n.Contains("lifecycle 'HR' answers in its initial state", StringComparison.Ordinal));
        // A lifecycle that governs only some subjects is not asked.
        var mixed = Model(documents: p => p with { SubjectList = [new ProcessSubject { Id = "01JRVB00000000000000000091", Entity = Document }] }, projects: p => p with
        {
            SubjectList = [new ProcessSubject { Id = "01JRVB00000000000000000092", Entity = Employee }, new ProcessSubject { Id = "01JRVB00000000000000000093", Entity = Document }],
        });
        var some = Check(mixed, "modify", Author, Projects, null);
        Assert.DoesNotContain(some.Lifecycles, l => l.Lifecycle.Id == Hr);
        Assert.Contains(some.Notes, n => n.Contains("'HR' governs only some subjects of 'Projects'", StringComparison.Ordinal));
    }

    // ---- closed lists and the order ----

    [Fact]
    public void A_closed_list_is_an_allow_that_rights_narrow_but_never_widen()
    {
        var model = Model(hr: p => p with { Events = [new ProcessEvent { Id = Submit, Name = "submit", Verb = "promote", Actors = [Clerk] }] });
        var hr = model.Get<Process>(Hr)!;
        RightsDecision Send(string actor) => RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = "promote", Process = hr, Entity = model.Get<Entity>(Employee), Caller = RightsCaller.Acting(model, actor),
            Closed = [new RightsList(Submit, [Clerk])],
        });
        // The clerk is let through by the event's list, under HR's deny default.
        var clerk = Send(Clerk);
        Allowed(clerk);
        Assert.Equal([Submit], clerk.Lifecycles[0].By);
        // HR staff hold an allow on promote, but the event lists only the clerk: the event refuses, before any right.
        Refused(Send(HrStaff), Refusals.Actor, Submit);
        Assert.Empty(Send(HrStaff).Rights);
        // A human task's completion, and its error, check its actors the same way.
        var task = RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = "complete", Process = hr, Caller = RightsCaller.Acting(model, HrStaff), Closed = [new RightsList(Task, [Clerk])],
        });
        Refused(task, Refusals.Actor, Task);
    }

    [Fact]
    public void Gate_signers_allow_the_gated_event_and_a_deny_on_its_verb_stops_signing()
    {
        var model = Model();
        var approval = model.Get<Process>(Approval)!;
        RightsDecision Gated(Process process, ModelSnapshot m, string actor, string verb) => RightsEvaluator.Of(m).Check(new RightsQuery
        {
            Verb = verb, Process = process, Entity = m.Get<Entity>(Contract), Caller = RightsCaller.Acting(m, actor),
            Closed = [new RightsList(Approve, [])], Signers = [new RightsList(Gate, [Approver])], Condition = _ => false,
        });
        // Under deny, a signer may send the gated event without an allow on promote; anyone else is refused by the default.
        var signer = Gated(approval, model, Approver, "promote");
        Allowed(signer);
        Assert.Equal([Gate], signer.Lifecycles[0].By);
        Refused(Gated(approval, model, Clerk, "promote"), Refusals.Default, Approval);
        // A deny on the gated event's verb stops signing, since every signature is sent as that event.
        var denying = Model(approval: p => p with
        {
            Rights = [new ProcessRight { Id = "01JRVR00000000000000000095", Name = "Frozen", Access = RightAccess.Deny, Verbs = ["promote"] }],
        });
        Refused(Gated(denying.Get<Process>(Approval)!, denying, Approver, "promote"), Refusals.Right, "01JRVR00000000000000000095");
        // At the signatures step: the signer passes sign, unless the deny on sign holds for it (the submitter).
        Allowed(Gated(approval, model, Approver, "sign"));
        var self = RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = "sign", Process = approval, Entity = model.Get<Entity>(Contract), Caller = RightsCaller.Acting(model, Approver),
            Signers = [new RightsList(Gate, [Approver])], Condition = r => r.Id == NoSelfSign, // subject.submittedBy = current_actor()
        });
        Refused(self, Refusals.Right, NoSelfSign);
    }

    [Fact]
    public void An_explicit_deny_anywhere_is_reported_before_any_default()
    {
        // Projects (acting) denies by default; HR denies modify for project managers outright. HR's right is the reason, not Projects' default.
        var model = Model(
            projects: p => p with { RightsDefault = RightAccess.Deny, Rights = [] },
            hr: p => p with
            {
                Rights = [new ProcessRight { Id = "01JRVR00000000000000000094", Name = "NoPmEdits", Access = RightAccess.Deny, Actors = [ProjectManager], Verbs = ["modify"] }],
            });
        var decision = Check(model, "modify", ProjectManager, Projects, Employee);
        Refused(decision, Refusals.Right, "01JRVR00000000000000000094");
        Assert.Equal([new RightEvaluation("01JRVR00000000000000000094", Hr, RightAccess.Deny)], decision.Rights);
        // The acting lifecycle's denies come first, in file order, then the others by name.
        var both = Model(
            projects: p => p with
            {
                Rights = [new ProcessRight { Id = "01JRVR00000000000000000093", Name = "NoEdits", Access = RightAccess.Deny, Verbs = ["modify"] }],
            },
            hr: p => p with
            {
                Rights = [new ProcessRight { Id = "01JRVR00000000000000000094", Name = "NoPmEdits", Access = RightAccess.Deny, Actors = [ProjectManager], Verbs = ["modify"] }],
            });
        var first = Check(both, "modify", ProjectManager, Projects, Employee);
        Refused(first, Refusals.Right, "01JRVR00000000000000000093");
        Assert.Equal(["01JRVR00000000000000000093", "01JRVR00000000000000000094"], first.Rights.Select(r => r.Right));
    }

    [Fact]
    public void An_event_with_no_verb_is_answered_by_its_list_and_its_default_only()
    {
        var model = Model();
        var hr = model.Get<Process>(Hr)!;
        var none = RightsEvaluator.Of(model).Check(new RightsQuery { Process = hr, Caller = RightsCaller.Acting(model, HrStaff) });
        Refused(none, Refusals.Default, Hr);
        var listed = RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Process = hr, Caller = RightsCaller.Acting(model, HrStaff), Closed = [new RightsList(Submit, [HrStaff])],
        });
        Allowed(listed);
    }

    // ---- links ----

    [Fact]
    public void Ends_narrow_a_right_to_links_and_freeze_speaks_of_them()
    {
        var model = Model();
        // In Review, NoBomChangesInReview denies connect through components; another end, and the object, fall to the default.
        Refused(Check(model, "connect", Author, Documents, Document, Review, end: Components), Refusals.Right, NoBomChangesInReview);
        Refused(Check(model, "connect", Author, Documents, Document, Review, end: Notes), Refusals.Default, Documents);
        Refused(Check(model, "freeze", Author, Documents, Document, Review, end: Components), Refusals.Right, NoBomChangesInReview);
        Assert.DoesNotContain(Check(model, "freeze", Author, Documents, Document, Review).Rights, r => r.Right == NoBomChangesInReview);
        // Without ends, a link verb speaks of every link, and modify speaks of the object only.
        Allowed(Check(model, "connect", Author, Documents, Document, Editing, end: Notes));
        Refused(Check(model, "modify", Author, Documents, Document, Editing, end: Notes), Refusals.Default, Documents);
    }

    [Fact]
    public void A_link_verb_with_no_end_fails_closed_for_rights_with_ends()
    {
        // An event that names connect names no end. NoBomChangesInReview, a deny with ends, still holds; an allow with ends grants nothing,
        // so the default answers; a right without ends answers as before.
        const string ReviewersLink = "01JRVR00000000000000000007";
        var model = Model(documents: p => p with
        {
            Rights =
            [
                .. p.Rights,
                new ProcessRight { Id = ReviewersLink, Name = "ReviewersLink", Access = RightAccess.Allow, Actors = [Reviewer], Verbs = ["connect"], Ends = [Components] },
            ],
        });
        Refused(Check(model, "connect", Author, Documents, Document, Review), Refusals.Right, NoBomChangesInReview);
        Refused(Check(model, "disconnect", Author, Documents, Document, Review), Refusals.Right, NoBomChangesInReview);
        Allowed(Check(model, "connect", Reviewer, Documents, Document, Editing, end: Components));
        Refused(Check(model, "connect", Reviewer, Documents, Document, Editing), Refusals.Default, Documents);
        Allowed(Check(model, "connect", Author, Documents, Document, Editing)); // AuthorsEditDrafts names no ends
        Assert.DoesNotContain(Check(model, "modify", Author, Documents, Document, Review).Rights, r => r.Right == NoBomChangesInReview); // not a link verb

        // The grid has no end either: Review's connect cell is denied by the deny with ends; Draft's for Reviewer falls to the default.
        var rows = RightsGrid.Rows(model, model.Get<Process>(Documents)!, ct: TestContext.Current.CancellationToken);
        Assert.Contains(rows, r => r is { State: Review, Actor: Author, Verb: "connect", Answer: "denied" } && r.By.SequenceEqual([NoBomChangesInReview]));
        Assert.Contains(rows, r => r is { State: Editing, Actor: Reviewer, Verb: "connect", Answer: "denied", ByDefault: true });
    }

    [Fact]
    public void Rights_on_a_demoted_lifecycle_use_nothing()
    {
        // A lifecycle demoted to an orchestration keeps its rights (MQ9901 asks the user), but only lifecycles hold rights: the project uses
        // none, and the rights naming key hashes as a project without them.
        static Process Plain(Process p) => p with { Rights = [], RightsDefault = null };
        var demoted = Model(documents: p => p with { Use = ProcessUse.Orchestration }, hr: Plain, projects: Plain, approval: Plain);
        var none = Model(documents: Plain, hr: Plain, projects: Plain, approval: Plain);
        Assert.False(RightsEvaluator.Of(demoted).Uses);
        Assert.Equal(Maquettiste.Engine.Planning.RightsNaming.Hash(none), Maquettiste.Engine.Planning.RightsNaming.Hash(demoted));
        Assert.NotEqual(Maquettiste.Engine.Planning.RightsNaming.Hash(none), Maquettiste.Engine.Planning.RightsNaming.Hash(Model(hr: Plain, projects: Plain, approval: Plain)));
    }

    [Fact]
    public void A_project_without_rights_is_never_asked()
    {
        var model = Model(
            documents: p => p with { Rights = [], RightsDefault = null }, hr: p => p with { Rights = [], RightsDefault = null },
            projects: p => p with { Rights = [] }, approval: p => p with { Rights = [], RightsDefault = null });
        Assert.False(RightsEvaluator.Of(model).Uses);
        var decision = Check(model, "modify", Clerk, Projects, Employee);
        Allowed(decision);
        Assert.Empty(decision.Lifecycles);
        Assert.True(RightsEvaluator.Of(Model()).Uses);
        Assert.Same(RightsEvaluator.Of(model), RightsEvaluator.Of(model));
    }
}
