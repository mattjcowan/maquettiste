using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The resolved model of rights (rights-design.md section 6; section 14's <c>ResolvedRightsTests</c>): <c>model.rights</c>, a lifecycle's
/// <c>rights</c> in file order with each use's states and typed condition, <c>rights_default</c>, <c>may_refuse</c>, and a verb step's keys and
/// expected refusal. The model is built in memory and invented.
/// </summary>
public sealed class ResolvedRightsTests
{
    private const string Document = "01JRRE00000000000000000001";
    private const string Classification = "01JRRE00000000000000000011";
    private const string Title = "01JRRE00000000000000000012";
    private const string Employee = "01JRRE00000000000000000002";

    private const string Author = "01JRRA00000000000000000001";
    private const string Owner = "01JRRA00000000000000000002";
    private const string HrStaff = "01JRRA00000000000000000003";
    private const string ProjectManager = "01JRRA00000000000000000004";

    private const string Documents = "01JRRP00000000000000000001";
    private const string Hr = "01JRRP00000000000000000002";
    private const string Projects = "01JRRP00000000000000000003";
    private const string Onboarding = "01JRRP00000000000000000004"; // an orchestration of Employee
    private const string Loose = "01JRRP00000000000000000005"; // an orchestration with no subject

    private const string Draft = "01JRRS00000000000000000001";
    private const string Released = "01JRRS00000000000000000002";

    private const string AuthorsEditDrafts = "01JRRR00000000000000000001";
    private const string ReleasedIsReadOnly = "01JRRR00000000000000000002";
    private const string SecretUse = "01JRRR00000000000000000003";
    private const string SecretOnlyOwner = "01JRRR00000000000000000004";
    private const string Unused = "01JRRR00000000000000000005";
    private const string HrStaffModify = "01JRRR00000000000000000011";
    private const string PmModify = "01JRRR00000000000000000012";

    private const string Scenario = "01JRRC00000000000000000001";
    private const string Spec = "01JRRC00000000000000000002";
    private const string Carl = "01JRRC00000000000000000003";
    private const string Step = "01JRRC00000000000000000004";

    private static Expr IsSecret => new()
    {
        Op = "eq", Args = [new Expr { Path = [Classification] }, new Expr { Value = JsonSerializer.SerializeToElement("secret") }],
    };

    private static ModelSnapshot Model(bool rights = true, Func<Element, Element>? edit = null)
    {
        var documents = Lifecycle(Documents, "Documents", Document, [new ProcessState { Id = Draft, Name = "Draft" }, new ProcessState { Id = Released, Name = "Released" }]);
        var hr = Lifecycle(Hr, "HR", Employee, [new ProcessState { Id = "01JRRS00000000000000000011", Name = "Active" }]);
        var projects = Lifecycle(Projects, "Projects", Employee, [new ProcessState { Id = "01JRRS00000000000000000021", Name = "Open" }]);
        if (rights)
        {
            documents = documents with
            {
                RightsDefault = RightAccess.Deny,
                Rights =
                [
                    new ProcessRight
                    {
                        Id = AuthorsEditDrafts, Name = "AuthorsEditDrafts", Access = RightAccess.Allow, Actors = [Author], Verbs = ["modify", "connect"], States = [Draft],
                        Description = new Description { Text = "Authors edit their drafts." },
                    },
                    new ProcessRight { Id = ReleasedIsReadOnly, Name = "ReleasedIsReadOnly", Access = RightAccess.Deny, Verbs = ["modify", "delete"], States = [Released] },
                    new ProcessRight { Id = SecretUse, Right = SecretOnlyOwner, States = [Draft, Released] },
                ],
            };
            hr = hr with
            {
                RightsDefault = RightAccess.Deny,
                Rights = [new ProcessRight { Id = HrStaffModify, Name = "HrStaffModify", Access = RightAccess.Allow, Actors = [HrStaff], Verbs = ["modify"] }],
            };
            projects = projects with
            {
                Rights = [new ProcessRight { Id = PmModify, Name = "PmModify", Access = RightAccess.Allow, Actors = [ProjectManager], Verbs = ["modify"] }],
            };
        }

        List<Element> elements =
        [
            new Entity
            {
                Id = Document, Name = "Document",
                Attributes =
                [
                    new ModelAttribute { Id = Classification, Name = "classification", Type = new TypeRef { Builtin = "string" } },
                    new ModelAttribute { Id = Title, Name = "title", Type = new TypeRef { Builtin = "string" } },
                ],
            },
            new Entity { Id = Employee, Name = "Employee" },
            new Actor { Id = Author, Name = "Author", Type = ActorType.Role },
            new Actor { Id = Owner, Name = "DocumentOwner", Type = ActorType.Person, Relative = new ActorRelative { Owner = true } },
            new Actor { Id = HrStaff, Name = "HrStaff", Type = ActorType.Role },
            new Actor { Id = ProjectManager, Name = "ProjectManager", Type = ActorType.Role },
            documents, hr, projects,
            new Process
            {
                Id = Onboarding, Name = "Onboarding", Use = ProcessUse.Orchestration,
                SubjectList = [new ProcessSubject { Id = "01JRRB00000000000000000004", Entity = Employee }],
                Events = [new ProcessEvent { Id = "01JRRX00000000000000000001", Name = "edit", Verb = "modify" }],
                States = [new ProcessState { Id = "01JRRS00000000000000000041", Name = "Start" }],
            },
            new Process { Id = Loose, Name = "Loose", Use = ProcessUse.Orchestration, States = [new ProcessState { Id = "01JRRS00000000000000000051", Name = "Start" }] },
            new Scenario
            {
                Id = Scenario, Name = "carlIsRefused", Process = Documents, Subject = Spec,
                Objects = [new ScenarioObject { Id = Spec, Name = "spec", Entity = Document }],
                Participants = [new ScenarioParticipant { Id = Carl, Name = "carl", Actor = Author }],
                Steps =
                [
                    new ScenarioStep
                    {
                        Id = Step, Input = StepInput.Verb, Verb = "modify", Participant = Carl,
                        Values = new Dictionary<string, JsonElement> { [Title] = JsonSerializer.SerializeToElement("Pump spec") },
                        Expect = new StepExpectation { Accepted = false, Refusal = "right", RefusedBy = SecretOnlyOwner },
                    },
                ],
            },
        ];
        if (rights)
        {
            elements.Add(new Right
            {
                Id = SecretOnlyOwner, Name = "SecretOnlyOwner", Access = RightAccess.Deny, Except = [Owner], Verbs = ["modify", "delete", "change-owner"],
                Condition = IsSecret, Description = new Description { Text = "Only its owner may change a secret item." },
            });
            elements.Add(new Right
            {
                Id = Unused, Name = "NotYetUsed", Access = RightAccess.Deny, Verbs = ["delete"], Condition = new Expr { Text = "subject.archived = true" },
            });
        }

        if (edit is not null)
            elements = [.. elements.Select(edit)];
        var docs = elements.Select((e, i) => Doc(e, $"model/x/{i:D2}-{e.Name}.json")).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        return ModelSnapshot.Create(docs, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
    }

    private static Process Lifecycle(string id, string name, string entity, IReadOnlyList<ProcessState> states) => new()
    {
        Id = id, Name = name, Use = ProcessUse.Lifecycle, SubjectList = [new ProcessSubject { Id = "01JRRB" + id[6..], Entity = entity }], States = states,
    };

    private static ElementDocument Doc(Element element, string path)
    {
        var bytes = TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
        var hash = ContentHash.Of(bytes);
        using var json = JsonDocument.Parse(bytes);
        return new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null);
    }

    private static RProcess ProcessOf(ResolvedModel model, string id) => model.Processes.Single(p => p.Id == id);

    [Fact]
    public void A_lifecycle_lists_its_rights_in_file_order_with_each_use()
    {
        var model = ResolutionKit.Resolve(Model());
        var documents = ProcessOf(model, Documents);

        Assert.Equal("deny", documents.RightsDefault);
        Assert.Equal([AuthorsEditDrafts, ReleasedIsReadOnly, SecretUse], documents.Rights.Select(u => u.Id));
        Assert.All(documents.Rights, u => Assert.Same(documents, u.Process));
        Assert.All(documents.Rights, u => Assert.Equal("process-right", u.Kind));

        var edit = documents.Rights[0];
        Assert.Equal(AuthorsEditDrafts, edit.Right.Id);
        Assert.Equal(("process-right", "AuthorsEditDrafts", "AuthorsEditDrafts", "allow", false, false), (edit.Right.Kind, edit.Right.Name, edit.Right.DisplayName,
            edit.Right.Access, edit.Right.Everyone, edit.Right.Named));
        Assert.Equal("Authors edit their drafts.", edit.Right.Description);
        Assert.Equal(["Author"], edit.Right.Actors.Select(a => a.Name));
        Assert.Empty(edit.Right.Except);
        Assert.Equal(["modify", "connect"], edit.Right.Verbs.Select(v => v.Key));
        Assert.Empty(edit.Right.Ends);
        Assert.Null(edit.Right.Package);
        Assert.Null(edit.Right.ConditionText);
        Assert.Same(edit, Assert.Single(edit.Right.Uses));
        Assert.Equal(["Draft"], edit.States.Select(s => s.Name));
        Assert.Null(edit.Condition);
        Assert.Same(edit.Right, model.Find(AuthorsEditDrafts));

        var readOnly = documents.Rights[1].Right;
        Assert.True(readOnly.Everyone);
        Assert.Empty(readOnly.Actors);

        // A use of a named right: the right is the element, with its condition typed for this lifecycle's subjects.
        var secret = documents.Rights[2];
        Assert.Same(model.Find(SecretOnlyOwner), secret.Right);
        Assert.Same(secret, model.Find(SecretUse));
        Assert.Equal(("right", true, "deny"), (secret.Right.Kind, secret.Right.Named, secret.Right.Access));
        Assert.Equal(["DocumentOwner"], secret.Right.Except.Select(a => a.Name));
        Assert.Equal(["Draft", "Released"], secret.States.Select(s => s.Name));
        Assert.NotNull(secret.Condition);
        Assert.Contains("subject.classification", secret.Condition!.Text, StringComparison.Ordinal);
        Assert.NotNull(secret.Condition.JavaScript);
        Assert.Contains("subject.classification", secret.Right.ConditionText, StringComparison.Ordinal);
        Assert.Same(secret, Assert.Single(secret.Right.Uses));
        Assert.Equal("Only its owner may change a secret item.", secret.Right.Description);
    }

    [Fact]
    public void Named_rights_are_listed_whether_used_or_not()
    {
        var model = ResolutionKit.Resolve(Model());

        Assert.Equal(["NotYetUsed", "SecretOnlyOwner"], model.Rights.Select(r => r.Name));
        var unused = model.Rights[0];
        Assert.Empty(unused.Uses);
        Assert.Equal("subject.archived = true", unused.ConditionText); // no use, so no scope: the text stays text
        Assert.Contains("k:right", model.Rights.MembershipKeys);
        Assert.Contains(Keys(model, SecretOnlyOwner), k => k == "e:" + SecretOnlyOwner);
    }

    [Fact]
    public void May_refuse_names_the_other_lifecycles_that_could_say_no()
    {
        var model = ResolutionKit.Resolve(Model());

        // Projects names modify through its right; HR denies by default and governs Employee too.
        var projects = Assert.Single(ProcessOf(model, Projects).MayRefuse);
        Assert.Equal(("HR", false), (projects.Lifecycle.Name, projects.ByEvents));
        Assert.Equal(["modify"], projects.Verbs.Select(v => v.Key));

        // Projects allows by default and denies nothing, so HR's rights need no one else.
        Assert.Empty(ProcessOf(model, Hr).MayRefuse);

        // An orchestration with a subject: its event names modify.
        var onboarding = Assert.Single(ProcessOf(model, Onboarding).MayRefuse);
        Assert.Equal(("HR", true), (onboarding.Lifecycle.Name, onboarding.ByEvents));

        // No other lifecycle governs Document, and an orchestration with no subject has no object.
        Assert.Empty(ProcessOf(model, Documents).MayRefuse);
        Assert.Empty(ProcessOf(model, Loose).MayRefuse);
        Assert.Contains(RightsNaming.Key, ProcessOf(model, Projects).MayRefuse.MembershipKeys);
        Assert.Contains("r:" + Employee, ProcessOf(model, Projects).MayRefuse.MembershipKeys);
    }

    [Fact]
    public void A_project_without_rights_resolves_empty_lists_keyed_on_the_rights_naming_key_only()
    {
        var model = ResolutionKit.Resolve(Model(rights: false));

        Assert.Empty(model.Rights);
        foreach (var process in model.Processes)
        {
            Assert.Empty(process.Rights);
            Assert.Equal("allow", process.RightsDefault);
            Assert.Empty(process.MayRefuse);
            Assert.Equal(["e:" + process.Id, RightsNaming.Key], process.MayRefuse.MembershipKeys.Order(StringComparer.Ordinal));
        }

        // The key's hash holds nothing of a project that states no rights.
        Assert.Equal(RightsNaming.Hash(Model(rights: false)), RightsNaming.Hash(ModelSnapshot.Create([], new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1)));
        Assert.NotEqual(RightsNaming.Hash(Model(rights: false)), RightsNaming.Hash(Model()));
    }

    [Fact]
    public void A_verb_step_resolves_its_verb_values_and_expected_refusal()
    {
        var model = ResolutionKit.Resolve(Model());
        var step = Assert.Single(model.Scenarios.Single(s => s.Id == Scenario).Steps);

        Assert.Equal(("verb", "modify"), (step.Input, step.Verb?.Key));
        Assert.Equal("Pump spec", step.Values["title"]);
        Assert.Null(step.End);
        Assert.Null(step.Object);
        Assert.Equal((false, "right", SecretOnlyOwner), (step.Expect!.Accepted, step.Expect.Refusal, step.Expect.RefusedBy));
    }

    private static IReadOnlyList<string> Keys(ResolvedModel model, string id) => model.Find(id)!.Dependencies;

    private const string Active = "01JRRS00000000000000000011";
    private const string Open = "01JRRS00000000000000000021";

    // What a unit that read these keys hashes to over a snapshot: a changed hash renders or replays it again.
    private static string HashOf(ModelSnapshot snapshot, IEnumerable<string> keys) =>
        new DependencyHasher(snapshot, ResolutionKit.Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>())
            .InputHash("unit", [.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    private static Func<Element, Element> OnProcess(string id, Func<Process, Process> edit) => e => e is Process p && p.Id == id ? edit(p) : e;

    private static string Cell(RAccessCell c) =>
        $"{c.State.Name}/{c.Actor?.Name ?? "*"}/{c.Verb.Key}{(c.Entity is { } e ? "/" + e.Name : "")}: {c.Answer}{(c.ByDefault ? " by default" : "")}"
        + $" by {string.Join(",", c.By.Select(b => b.Id))}{(c.DependsOn.Count > 0 ? " on " + string.Join(",", c.DependsOn.Select(d => d.Name)) : "")}";

    [Fact]
    public void The_access_grid_answers_per_state_actor_and_verb()
    {
        var model = ResolutionKit.Resolve(Model());
        var grid = ProcessOf(model, Documents).AccessGrid.Select(Cell).ToList();

        // A deny whose condition or exception can't be known makes an allowed cell depend; the default answers everyone else.
        Assert.Contains($"Draft/Author/modify: depends by {SecretOnlyOwner}", grid);
        Assert.Contains($"Draft/*/modify: denied by default by {Documents}", grid);
        Assert.Contains($"Draft/DocumentOwner/modify: denied by default by {Documents}", grid);
        Assert.Contains($"Released/Author/modify: denied by {ReleasedIsReadOnly}", grid);
        Assert.Contains($"Draft/Author/connect: allowed by {AuthorsEditDrafts}", grid);
        Assert.Equal(["Author", "DocumentOwner", "*"], ProcessOf(model, Documents).AccessGrid.Select(c => c.Actor?.Name ?? "*").Distinct());
        Assert.Equal(["change-owner", "connect", "delete", "modify"], ProcessOf(model, Documents).AccessGrid.Select(c => c.Verb.Key).Distinct());

        // HR denies by default and lets HR staff modify, Projects lets project managers modify: a project manager is refused by HR's default.
        Assert.Equal([$"Open/ProjectManager/modify: denied by default by {Hr}", $"Open/*/modify: denied by default by {Hr}"],
            ProcessOf(model, Projects).AccessGrid.Select(Cell));
        Assert.Empty(ProcessOf(model, Onboarding).AccessGrid);
    }

    [Fact]
    public void A_cell_depends_on_another_lifecycle_and_splits_by_entity_where_answers_differ()
    {
        // HR allows by default and denies modify only in Active, beside another state: it could say no in only some of its states.
        var snapshot = Model(edit: e => e switch
        {
            Process { Id: Hr } p => p with
            {
                RightsDefault = null,
                States = [.. p.States, new ProcessState { Id = "01JRRS00000000000000000012", Name = "Leave" }],
                Rights = [new ProcessRight { Id = HrStaffModify, Name = "NoEditsWhileActive", Access = RightAccess.Deny, Verbs = ["modify"], States = [Active] }],
            },
            Process { Id: Projects } p => p with
            {
                SubjectList = [.. p.SubjectList!, new ProcessSubject { Id = "01JRRB00000000000000000099", Entity = Document }],
            },
            _ => e,
        });
        var model = ResolutionKit.Resolve(snapshot);
        var grid = ProcessOf(model, Projects).AccessGrid.Select(Cell).ToList();

        // Employee: HR could say no; Document: Documents says no in every state, by its default or its read-only right.
        Assert.Contains($"Open/ProjectManager/modify/Document: denied by default by {Documents}", grid);
        Assert.Contains($"Open/ProjectManager/modify/Employee: depends by {PmModify} on HR", grid);
        Assert.Equal(4, grid.Count);

        // get_rights gives the same rows, filters them and pages them.
        var projects = snapshot.Get<Process>(Projects)!;
        var rows = RightsGrid.Rows(snapshot, projects, null, TestContext.Current.CancellationToken);
        Assert.Equal(ProcessOf(model, Projects).AccessGrid.Select(c => (c.State.Id, c.Actor?.Id, c.Verb.Key, c.Entity?.Id, c.Answer)),
            rows.Select(r => (r.State, r.Actor, r.Verb, r.Entity, r.Answer)));
        Assert.Equal([Employee], RightsGrid.Rows(snapshot, projects, new RightsFilter { Actors = [ProjectManager], Entity = Employee }, TestContext.Current.CancellationToken).Select(r => r.Entity));
        var first = RightsGrid.Page(snapshot, projects, null, null, 3, TestContext.Current.CancellationToken)!;
        Assert.Equal(("3", "allow"), (first.Next, first.RightsDefault));
        var last = RightsGrid.Page(snapshot, projects, null, first.Next, 3, TestContext.Current.CancellationToken)!;
        Assert.Null(last.Next);
        Assert.Equal(rows.Select(r => (r.State, r.Actor, r.Verb, r.Entity)), first.Rows.Concat(last.Rows).Select(r => (r.State, r.Actor, r.Verb, r.Entity)));
        Assert.Null(RightsGrid.Page(snapshot, projects, null, "x", 3, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Get_rights_answers_from_the_closed_lists_and_the_default_without_rights()
    {
        var snapshot = Model(rights: false, edit: OnProcess(Documents, p => p with
        {
            Events = [new ProcessEvent { Id = "01JRRX00000000000000000011", Name = "edit", Verb = "modify", Actors = [Author] }],
            Transitions = [new ProcessTransition { Id = "01JRRT00000000000000000011", Source = Draft, Event = "01JRRX00000000000000000011", Targets = [Released] }],
        }));
        var model = ResolutionKit.Resolve(snapshot);
        Assert.Empty(ProcessOf(model, Documents).AccessGrid);

        var rows = RightsGrid.Rows(snapshot, snapshot.Get<Process>(Documents)!, null, TestContext.Current.CancellationToken).Select(r => $"{r.State}/{r.Actor ?? "*"}/{r.Verb}: {r.Answer} {string.Join(",", r.By)}");
        Assert.Equal([$"{Draft}/{Author}/modify: allowed 01JRRX00000000000000000011", $"{Draft}/*/modify: denied 01JRRX00000000000000000011",
            $"{Released}/{Author}/modify: allowed {Documents}", $"{Released}/*/modify: allowed {Documents}"], rows);
    }

    [Fact]
    public void Actors_and_verbs_list_the_rights_that_name_them()
    {
        var model = ResolutionKit.Resolve(Model());
        var owner = model.Actors.Single(a => a.Id == Owner);
        Assert.Equal([SecretOnlyOwner], owner.Rights.Select(r => r.Id));
        Assert.Equal([AuthorsEditDrafts], model.Actors.Single(a => a.Id == Author).Rights.Select(r => r.Id));
        Assert.Equal([SecretOnlyOwner, AuthorsEditDrafts, ReleasedIsReadOnly, HrStaffModify, PmModify],
            model.Verbs.Single(v => v.Key == "modify").Rights.Select(r => r.Id));

        // An actor page gains no key: the rights key on the actor's referrers, as its events do.
        Assert.Subset(owner.Events.MembershipKeys.ToHashSet(), owner.Rights.MembershipKeys.ToHashSet());

        // A project that uses verbs but no rights: verb.rights keys on the rights naming key only.
        var plain = ResolutionKit.Resolve(Model(rights: false));
        Assert.All(plain.Verbs, v => Assert.Equal([RightsNaming.Key], v.Rights.MembershipKeys));
        Assert.All(plain.Verbs, v => Assert.Empty(v.Rights));
    }

    [Fact]
    public void The_grid_renders_again_when_another_lifecycle_gets_its_first_right_or_changes_a_deny()
    {
        // HR states nothing at first; Projects' grid reads modify, which HR governs.
        var before = Model(edit: OnProcess(Hr, p => p with { RightsDefault = null, Rights = [] }));
        var keys = ProcessOf(ResolutionKit.Resolve(before), Projects).AccessGrid.MembershipKeys;
        var hash = HashOf(before, keys);
        Assert.NotEqual(hash, HashOf(Model(edit: OnProcess(Hr, p => p with { Rights = [] })), keys)); // a default of deny

        // A deny on a verb only the lifecycle's rights name, then a change to that deny's states.
        static Process WithDeny(Process p, IReadOnlyList<string> states) => p with
        {
            RightsDefault = null, Rights = [new ProcessRight { Id = HrStaffModify, Name = "Frozen", Access = RightAccess.Deny, Verbs = ["modify"], States = states }],
        };

        var denying = Model(edit: OnProcess(Hr, p => WithDeny(p, [Active])));
        Assert.NotEqual(hash, HashOf(denying, keys));
        var denyKeys = ProcessOf(ResolutionKit.Resolve(denying), Projects).AccessGrid.MembershipKeys;
        Assert.NotEqual(HashOf(denying, denyKeys), HashOf(Model(edit: OnProcess(Hr, p => WithDeny(p, []))), denyKeys));
    }

    [Fact]
    public void A_scenario_replays_when_another_lifecycle_could_now_refuse_its_verb_step()
    {
        const string Ann = "01JRRC00000000000000000013";
        Element WithScenario(Element e) => e is Scenario { Id: Scenario } s ? s with
        {
            Process = Projects, Subject = Ann, Objects = [new ScenarioObject { Id = Ann, Name = "ann", Entity = Employee }],
            Steps = [s.Steps[0] with { Verb = "delete", Values = new Dictionary<string, JsonElement>(), Expect = null }],
        } : e;
        Element Hr0(Element e) => WithScenario(e is Process { Id: Hr } p ? p with { RightsDefault = null, Rights = [] } : e);
        Func<Element, Element> HrDeny(Expr? condition) => e => WithScenario(e is Process { Id: Hr } p ? p with
        {
            RightsDefault = null, Rights = [new ProcessRight { Id = HrStaffModify, Name = "NoDelete", Access = RightAccess.Deny, Verbs = ["delete"], Condition = condition }],
        } : e);

        var before = Model(edit: Hr0);
        var keys = Keys(ResolutionKit.Resolve(before), Step);
        Assert.Contains(RightsNaming.Key, keys);
        Assert.NotEqual(HashOf(before, keys), HashOf(Model(edit: HrDeny(null)), keys)); // HR gains a deny on delete

        var denying = Model(edit: HrDeny(null));
        var denyKeys = Keys(ResolutionKit.Resolve(denying), Step);
        Assert.Contains("e:" + Hr, denyKeys);
        Assert.NotEqual(HashOf(denying, denyKeys), HashOf(Model(edit: HrDeny(IsSecret)), denyKeys)); // that deny's condition changes

        // A project without rights: the step keys on the rights naming key, whose hash never changes there.
        var plain = Model(rights: false, edit: WithScenario);
        var plainKeys = Keys(ResolutionKit.Resolve(plain), Step);
        Assert.Contains(RightsNaming.Key, plainKeys);
        Assert.DoesNotContain("e:" + Hr, plainKeys);
    }

    [Fact]
    public void Records_carry_rights_only_where_present()
    {
        var model = ResolutionKit.Resolve(Model());
        var documents = (ProcessRecord)ResolvedRecords.Entries(model, "processes", null).Single(e => e.Id == Documents).Project();
        Assert.Equal("deny", documents.RightsDefault);
        Assert.Equal([AuthorsEditDrafts, ReleasedIsReadOnly, SecretOnlyOwner], documents.Rights!.Select(r => r.Right));
        Assert.Equal(("AuthorsEditDrafts", "allow", false), (documents.Rights![0].Name, documents.Rights[0].Access, documents.Rights[0].Named));
        Assert.Null(documents.Rights[2].Access);
        var right = (RightRecord)ResolvedRecords.Entries(model, "all", null).Single(e => e.Id == SecretOnlyOwner).Project();
        Assert.Equal("deny", right.Access);
        Assert.Equal([Documents], right.Processes);
        var scenario = (ScenarioRecord)ResolvedRecords.Entries(model, "scenarios", null).Single(e => e.Id == Scenario).Project();
        Assert.Equal(("modify", "right", SecretOnlyOwner), (scenario.Steps[0].Verb, scenario.Steps[0].Expect!.Refusal, scenario.Steps[0].Expect!.RefusedBy));

        var plain = ResolutionKit.Resolve(Model(rights: false));
        var json = JsonSerializer.Serialize(ResolvedRecords.Entries(plain, "processes", null).Select(e => e.Project()).ToList(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"rights", json, StringComparison.Ordinal);
        Assert.DoesNotContain(ResolvedRecords.Entries(plain, "all", null), e => e.Kind == "right");
    }
}
