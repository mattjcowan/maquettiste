using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Rights part 2 in the evaluator and "who can do what" (rights-design.md decisions 16 to 18 and 21; section 14's part 2): freezing refuses
/// modify, connect, disconnect and delete before any right, an object's and a link's; <c>whileFrozen</c> rights hold only while frozen; each
/// attribute is checked on its own against the rights that name it and those that name none, and any no wins; unknown attributes fail closed;
/// a project without attribute rights gets part 1's answers; the grid's frozen cells and its "depends" for attribute rights. The model is
/// built in memory and invented.
/// </summary>
public sealed class FreezingAndAttributeRightsTests
{
    private const string Employee = "01JFAE00000000000000000001";
    private const string Badges = "01JFAE00000000000000000002"; // an end of Employee
    private const string Salary = "01JFAT00000000000000000001";
    private const string Title = "01JFAT00000000000000000002";
    private const string HrStaff = "01JFAA00000000000000000001";
    private const string Clerk = "01JFAA00000000000000000002";
    private const string Hr = "01JFAP00000000000000000001";
    private const string Active = "01JFAS00000000000000000001";
    private const string SalaryHrOnly = "01JFAR00000000000000000001";
    private const string OwnerStaysWhileFrozen = "01JFAR00000000000000000002";
    private const string NoBadgeReadWhileFrozen = "01JFAR00000000000000000003";
    private const string ClerksEditTitle = "01JFAR00000000000000000004";
    private const string EveryoneEdits = "01JFAR00000000000000000005";
    private const string Hire = "01JFAX00000000000000000001";

    private static readonly ProcessRight SalaryRight = new()
    {
        Id = SalaryHrOnly, Name = "SalaryHrOnly", Access = RightAccess.Deny, Except = [HrStaff], Verbs = ["read", "modify"], Attributes = [Salary],
    };

    private static readonly ProcessRight[] FrozenRights =
    [
        new() { Id = OwnerStaysWhileFrozen, Name = "OwnerStaysWhileFrozen", Access = RightAccess.Deny, Verbs = ["change-owner"], WhileFrozen = true },
        new() { Id = NoBadgeReadWhileFrozen, Name = "NoBadgeReadWhileFrozen", Access = RightAccess.Deny, Verbs = ["read"], Ends = [Badges], WhileFrozen = true },
    ];

    private static ModelSnapshot Model(IReadOnlyList<ProcessRight> rights, RightAccess? rightsDefault = null)
    {
        List<Element> elements =
        [
            new Entity { Id = Employee, Name = "Employee" },
            new Actor { Id = HrStaff, Name = "HrStaff", Type = ActorType.Role },
            new Actor { Id = Clerk, Name = "Clerk", Type = ActorType.Person },
            new Process
            {
                Id = Hr, Name = "HR", Use = ProcessUse.Lifecycle, SubjectList = [new ProcessSubject { Id = "01JFAB00000000000000000001", Entity = Employee }],
                States = [new ProcessState { Id = Active, Name = "Active" }], RightsDefault = rightsDefault, Rights = rights,
                Events = [new ProcessEvent { Id = Hire, Name = "hire", Verb = "promote", Actors = [HrStaff] }],
            },
        ];
        var documents = elements.Select((e, i) => Doc(e, $"model/x/{i:D2}-{e.Name}.json")).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        return ModelSnapshot.Create(documents, new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", [], [], 1);
    }

    private static ElementDocument Doc(Element element, string path)
    {
        var bytes = TestServices.Json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
        var hash = ContentHash.Of(bytes);
        using var json = JsonDocument.Parse(bytes);
        return new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), json.RootElement.Clone(), null);
    }

    private static RightsDecision Check(ModelSnapshot model, string verb, string actsAs, bool frozen = false, string? end = null, bool linkFrozen = false,
        IReadOnlyList<string>? attributes = null, IReadOnlyList<RightsList>? closed = null) =>
        RightsEvaluator.Of(model).Check(new RightsQuery
        {
            Verb = verb, Process = model.Get<Process>(Hr), Entity = model.Get<Entity>(Employee), End = end, Caller = RightsCaller.Acting(model, actsAs),
            States = new Dictionary<string, IReadOnlyList<string>> { [Hr] = [Active] }, Condition = _ => false,
            Frozen = frozen, LinkFrozen = linkFrozen, Attributes = attributes, Closed = closed ?? [],
        });

    private static void Refused(RightsDecision decision, string refusal, string? by)
    {
        Assert.Equal(RightAnswer.Denied, decision.Answer);
        Assert.Equal((refusal, by), (decision.Refusal, decision.RefusedBy));
    }

    private static void Allowed(RightsDecision decision) => Assert.True(decision.Allowed, $"{decision.Answer} {decision.Refusal} {decision.RefusedBy}");

    // ---- freezing ----

    [Fact]
    public void A_frozen_object_refuses_modify_delete_connect_and_disconnect_before_any_right_and_nothing_else()
    {
        var model = Model([new ProcessRight { Id = EveryoneEdits, Name = "EveryoneEdits", Access = RightAccess.Allow, Verbs = ["modify", "delete", "connect", "disconnect"] }],
            RightAccess.Deny);
        foreach (var verb in new[] { "modify", "delete", "connect", "disconnect" })
        {
            Allowed(Check(model, verb, Clerk));
            var frozen = Check(model, verb, Clerk, frozen: true);
            Refused(frozen, Refusals.Frozen, null); // built in: no element refused it, and no right answered
            Assert.Empty(frozen.Rights);
        }

        // The first check: before a closed list that leaves the caller out.
        Refused(Check(model, "modify", Clerk, frozen: true, closed: [new RightsList(Hire, [HrStaff])]), Refusals.Frozen, null);
        Refused(Check(model, "modify", Clerk, closed: [new RightsList(Hire, [HrStaff])]), Refusals.Actor, Hire);

        // Freezing stops nothing else: moves, revise, change-owner and the like answer as when thawed.
        foreach (var verb in new[] { "promote", "revise", "change-owner", "read", "copy", "lock" })
            Assert.Equal(Check(model, verb, Clerk).Refusal, Check(model, verb, Clerk, frozen: true).Refusal);
        Assert.False(RightsEvaluator.RefusedByFreezing("freeze", null, true, true));
        Assert.False(RightsEvaluator.RefusedByFreezing("thaw", null, true, true));
    }

    [Fact]
    public void A_frozen_link_refuses_disconnect_and_modify_through_it_and_leaves_the_object_alone()
    {
        var model = Model([], RightAccess.Allow);
        Refused(Check(model, "disconnect", Clerk, end: Badges, linkFrozen: true), Refusals.Frozen, null);
        Refused(Check(model, "modify", Clerk, end: Badges, linkFrozen: true), Refusals.Frozen, null);
        Allowed(Check(model, "modify", Clerk, linkFrozen: true)); // the object at its end is not frozen by it
        Allowed(Check(model, "delete", Clerk, linkFrozen: true));
        // A frozen object's links can't be added or removed, but a link it does not freeze can still have its attributes modified.
        Refused(Check(model, "connect", Clerk, frozen: true, end: Badges), Refusals.Frozen, null);
        Allowed(Check(model, "modify", Clerk, frozen: true, end: Badges));
    }

    [Fact]
    public void A_while_frozen_right_holds_only_while_the_object_or_with_ends_the_link_is_frozen()
    {
        var model = Model(FrozenRights);
        Allowed(Check(model, "change-owner", Clerk));
        Refused(Check(model, "change-owner", Clerk, frozen: true), Refusals.Right, OwnerStaysWhileFrozen);

        // With ends it speaks of the link: the link's flag decides, not the object's.
        Allowed(Check(model, "read", Clerk, end: Badges));
        Allowed(Check(model, "read", Clerk, frozen: true, end: Badges));
        Refused(Check(model, "read", Clerk, end: Badges, linkFrozen: true), Refusals.Right, NoBadgeReadWhileFrozen);
        Allowed(Check(model, "read", Clerk, frozen: true)); // reading the object: the right with ends says nothing
    }

    // ---- attributes ----

    [Fact]
    public void Each_attribute_is_checked_on_its_own_and_one_refused_attribute_refuses_the_step()
    {
        var model = Model([SalaryRight]);
        Assert.True(RightsEvaluator.Of(model).NamesAttributes);
        Allowed(Check(model, "modify", Clerk, attributes: [Title]));
        Refused(Check(model, "modify", Clerk, attributes: [Salary]), Refusals.Right, SalaryHrOnly);
        Refused(Check(model, "modify", Clerk, attributes: [Title, Salary]), Refusals.Right, SalaryHrOnly);
        Refused(Check(model, "read", Clerk, attributes: [Salary, Title]), Refusals.Right, SalaryHrOnly);
        Allowed(Check(model, "modify", HrStaff, attributes: [Title, Salary])); // except: the right does not apply to HR staff
        Assert.Equal([new RightEvaluation(SalaryHrOnly, Hr, RightAccess.Deny)], Check(model, "modify", Clerk, attributes: [Title, Salary]).Rights);
    }

    [Fact]
    public void An_allow_that_names_one_attribute_says_nothing_about_another_and_the_default_answers_for_it()
    {
        var model = Model(
        [
            new ProcessRight { Id = ClerksEditTitle, Name = "ClerksEditTitle", Access = RightAccess.Allow, Actors = [Clerk], Verbs = ["modify"], Attributes = [Title] },
        ], RightAccess.Deny);
        var title = Check(model, "modify", Clerk, attributes: [Title]);
        Allowed(title);
        Assert.Equal([new RightEvaluation(ClerksEditTitle, Hr, RightAccess.Allow)], title.Rights);
        Refused(Check(model, "modify", Clerk, attributes: [Title, Salary]), Refusals.Default, Hr);
        Refused(Check(model, "modify", Clerk, attributes: [Salary]), Refusals.Default, Hr);

        // A right that names no attributes applies to every attribute, with the attribute rights: a deny still wins.
        var denied = Model(
        [
            new ProcessRight { Id = ClerksEditTitle, Name = "ClerksEditTitle", Access = RightAccess.Allow, Actors = [Clerk], Verbs = ["modify"], Attributes = [Title] },
            new ProcessRight { Id = EveryoneEdits, Name = "NoEdits", Access = RightAccess.Deny, Verbs = ["modify"] },
        ], RightAccess.Deny);
        Refused(Check(denied, "modify", Clerk, attributes: [Title]), Refusals.Right, EveryoneEdits);
    }

    [Fact]
    public void Attributes_not_known_fail_closed_a_deny_that_names_attributes_holds_and_an_allow_grants_nothing()
    {
        var model = Model([SalaryRight]);
        // An event that names modify, or a modify or read step without attributes.
        Refused(Check(model, "modify", Clerk), Refusals.Right, SalaryHrOnly);
        Refused(Check(model, "read", Clerk, attributes: []), Refusals.Right, SalaryHrOnly);
        Allowed(Check(model, "modify", HrStaff));

        var allowing = Model(
        [
            new ProcessRight { Id = ClerksEditTitle, Name = "ClerksEditTitle", Access = RightAccess.Allow, Actors = [Clerk], Verbs = ["modify"], Attributes = [Title] },
        ], RightAccess.Deny);
        Refused(Check(allowing, "modify", Clerk), Refusals.Default, Hr);
        Assert.Empty(Check(allowing, "modify", Clerk).Rights);
    }

    [Fact]
    public void A_project_whose_rights_name_no_attributes_gets_part_1s_answers_whatever_attributes_a_check_gives()
    {
        var model = Model(
        [
            new ProcessRight { Id = EveryoneEdits, Name = "EveryoneEdits", Access = RightAccess.Allow, Actors = [HrStaff], Verbs = ["modify", "read"] },
            ..FrozenRights,
        ], RightAccess.Deny);
        Assert.False(RightsEvaluator.Of(model).NamesAttributes);
        foreach (var (verb, actor) in new[] { ("modify", Clerk), ("modify", HrStaff), ("read", Clerk), ("delete", HrStaff) })
        {
            var plain = Check(model, verb, actor);
            foreach (var attributes in new IReadOnlyList<string>[] { [Salary], [Title, Salary], [] })
            {
                var given = Check(model, verb, actor, attributes: attributes);
                Assert.Equal((plain.Answer, plain.Refusal, plain.RefusedBy), (given.Answer, given.Refusal, given.RefusedBy));
                Assert.Equal(plain.Rights, given.Rights);
                Assert.Equal(plain.Lifecycles.Select(a => (a.Answer, a.ByDefault, string.Join(",", a.By))), given.Lifecycles.Select(a => (a.Answer, a.ByDefault, string.Join(",", a.By))));
            }
        }
    }

    // ---- who can do what ----

    [Fact]
    public void Who_can_do_what_depends_on_an_attribute_right_and_answers_for_a_frozen_object_when_asked()
    {
        var model = Model([SalaryRight, ..FrozenRights]);
        var lifecycle = model.Get<Process>(Hr)!;
        string Cell(IReadOnlyList<RightsRow> rows, string? actor, string verb) =>
            rows.Single(r => r.Actor == actor && r.Verb == verb) is var row ? $"{row.Answer} {row.ByDefault} [{string.Join(",", row.By)}]" : "";

        var thawed = RightsGrid.Rows(model, lifecycle, ct: TestContext.Current.CancellationToken);
        // A read or modify cell that a right with attributes would change depends, and names that right.
        Assert.Equal($"depends False [{SalaryHrOnly}]", Cell(thawed, null, "modify"));
        Assert.Equal($"depends False [{SalaryHrOnly}]", Cell(thawed, null, "read"));
        Assert.Equal($"allowed True [{Hr}]", Cell(thawed, HrStaff, "modify"));
        Assert.Equal($"allowed True [{Hr}]", Cell(thawed, null, "change-owner"));
        static IEnumerable<string> Text(IReadOnlyList<RightsRow> rows) => rows.Select(r => $"{r.State} {r.Actor} {r.Verb} {r.Answer} {r.ByDefault} {string.Join(",", r.By)}");
        Assert.Equal(Text(thawed), Text(RightsGrid.Rows(model, lifecycle, new RightsFilter { Frozen = false }, TestContext.Current.CancellationToken)));

        var frozen = RightsGrid.Rows(model, lifecycle, new RightsFilter { Frozen = true }, TestContext.Current.CancellationToken);
        Assert.Equal("denied False []", Cell(frozen, HrStaff, "modify"));
        Assert.Equal("denied False []", Cell(frozen, null, "modify"));
        Assert.Equal($"denied False [{OwnerStaysWhileFrozen}]", Cell(frozen, null, "change-owner"));
        Assert.Equal($"allowed True [{Hr}]", Cell(frozen, HrStaff, "read")); // reading the object: the right with ends says nothing
        Assert.Equal($"depends False [{SalaryHrOnly}]", Cell(frozen, null, "read"));
    }
}
