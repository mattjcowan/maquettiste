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
/// Rights in simulation, recording and replay (rights-design.md decisions 10 to 14 and 23 to 27, section 7; section 14's
/// <c>RightsReplayTests</c>), over an invented model beside the <c>process-basics</c> fixture: a document lifecycle that denies by default
/// with the rights of section 4 and a named right; an employee governed by an HR lifecycle that denies by default and a project lifecycle that
/// allows, with an orchestration of employees; and a gated contract approval with a human task.
/// </summary>
public sealed class RightsReplayTests
{
    private const string Package = "01JPKG00000000000000000001";

    // Entities, attributes and the relation end from a document to its parts.
    private const string Document = "01JRRE00000000000000000001";
    private const string Part = "01JRRE00000000000000000002";
    private const string Employee = "01JRRE00000000000000000003";
    private const string Contract = "01JRRE00000000000000000004";
    private const string Title = "01JRRT00000000000000000002";
    private const string Classification = "01JRRT00000000000000000003";
    private const string SubmittedBy = "01JRRT00000000000000000008";
    private const string Components = "01JRRN00000000000000000003";

    // Actors.
    private const string Author = "01JRRA00000000000000000001";
    private const string Reviewer = "01JRRA00000000000000000003";
    private const string HrStaff = "01JRRA00000000000000000004";
    private const string ProjectManager = "01JRRA00000000000000000005";
    private const string DocumentOwner = "01JRRA00000000000000000006";
    private const string Clerk = "01JRRA00000000000000000007";
    private const string Approver = "01JRRA00000000000000000008";
    private const string Temp = "01JRRA00000000000000000009";

    // Processes.
    private const string Documents = "01JRRP00000000000000000001";
    private const string Hr = "01JRRP00000000000000000002";
    private const string Projects = "01JRRP00000000000000000003";
    private const string Onboarding = "01JRRP00000000000000000004";
    private const string Loose = "01JRRP00000000000000000005";
    private const string Approval = "01JRRP00000000000000000006";

    // States.
    private const string Draft = "01JRRS00000000000000000001";
    private const string Review = "01JRRS00000000000000000002";
    private const string Released = "01JRRS00000000000000000003";
    private const string HrActive = "01JRRS00000000000000000011";
    private const string ProjectOpen = "01JRRS00000000000000000021";
    private const string ProjectClosed = "01JRRS00000000000000000022";
    private const string Drafting = "01JRRS00000000000000000051";
    private const string Submitted = "01JRRS00000000000000000052";
    private const string Approved = "01JRRS00000000000000000053";

    // Rights.
    private const string SecretOnlyOwner = "01JRRR00000000000000000001";
    private const string AuthorsEditDrafts = "01JRRR00000000000000000003";
    private const string ReleasedIsReadOnly = "01JRRR00000000000000000004";
    private const string NoBomChangesInReview = "01JRRR00000000000000000005";
    private const string NoEditsWhenClosed = "01JRRR00000000000000000012";
    private const string NoSelfSign = "01JRRR00000000000000000021";
    private const string NoTempPromotes = "01JRRR00000000000000000022";

    // Events, guards, invokes and gates.
    private const string Submit = "01JRRX00000000000000000001";
    private const string Release = "01JRRX00000000000000000002";
    private const string Titled = "01JRRX00000000000000000004";
    private const string Assign = "01JRRX00000000000000000011";
    private const string Close = "01JRRX00000000000000000012";
    private const string Update = "01JRRX00000000000000000021";
    private const string Poke = "01JRRX00000000000000000022";
    private const string Prepare = "01JRRX00000000000000000031";
    private const string Approve = "01JRRX00000000000000000032";

    // Scenario objects.
    private const string Spec = "01JRRJ00000000000000000001";
    private const string Pump = "01JRRJ00000000000000000002";
    private const string Eve = "01JRRJ00000000000000000003";
    private const string Deal = "01JRRJ00000000000000000004";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathTo(string id) => $$"""{"path":["{{id}}"]}""";

    private static string Attribute(string id, string name, string type, string extra = "") =>
        $$"""{"id":"{{id}}","name":"{{name}}","type":"{{type}}"{{extra}}}""";

    private static string Entity(string id, string name, string key, string extra = "") => $$"""
        {"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Package}}","key":{"attributes":["{{key}}"]},
         "attributes":[{{Attribute(key, "id", "uuid")}}{{extra}}]}
        """;

    private static string ActorJson(string id, string name, string type, string extra = "") =>
        $$"""{"kind":"actor","id":"{{id}}","name":"{{name}}","type":"{{type}}"{{extra}}}""";

    // The invented model; vocabulary adds the project's own verb archive.
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(bool vocabulary = false, string? scenario = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/document.json", Entity(Document, "Document", "01JRRT00000000000000000001",
            "," + Attribute(Title, "title", "string", ",\"length\":40") + "," + Attribute(Classification, "classification", "string", ",\"length\":20")));
        harness.Write("model/entities/part.json", Entity(Part, "Part", "01JRRT00000000000000000004"));
        harness.Write("model/entities/employee.json", Entity(Employee, "Employee", "01JRRT00000000000000000005"));
        harness.Write("model/entities/contract.json", Entity(Contract, "Contract", "01JRRT00000000000000000007",
            "," + Attribute(SubmittedBy, "submittedBy", "string", ",\"length\":40")));
        harness.Write("model/relations/document-parts.json", $$"""
            {"kind":"relation","id":"01JRRN00000000000000000001","name":"DocumentParts",
             "ends":[{"id":"01JRRN00000000000000000002","entity":"{{Document}}","role":"document"},
               {"id":"{{Components}}","entity":"{{Part}}","role":"components","navigation":"components"}]}
            """);
        harness.Write("model/actors/author.json", ActorJson(Author, "Author", "role"));
        harness.Write("model/actors/reviewer.json", ActorJson(Reviewer, "Reviewer", "role"));
        harness.Write("model/actors/hr-staff.json", ActorJson(HrStaff, "HrStaff", "role"));
        harness.Write("model/actors/project-manager.json", ActorJson(ProjectManager, "ProjectManager", "role"));
        harness.Write("model/actors/document-owner.json", ActorJson(DocumentOwner, "DocumentOwner", "person", ",\"relative\":{\"owner\":true}"));
        harness.Write("model/actors/doc-clerk.json", ActorJson(Clerk, "DocClerk", "person"));
        harness.Write("model/actors/approver.json", ActorJson(Approver, "Approver", "role"));
        harness.Write("model/actors/temp.json", ActorJson(Temp, "Temp", "role"));
        harness.Write("model/rights/secret-only-owner.json", $$"""
            {"kind":"right","id":"{{SecretOnlyOwner}}","name":"SecretOnlyOwner","package":"{{Package}}","access":"deny","except":["{{DocumentOwner}}"],
             "verbs":["modify","delete","change-owner"],"condition":{"op":"eq","args":[{{PathTo(Classification)}},{"value":"secret"}]},
             "description":"Only its owner may change a secret item."}
            """);
        harness.Write("model/processes/documents.json", $$"""
            {"kind":"process","id":"{{Documents}}","name":"Documents","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JRRB00000000000000000001","entity":"{{Document}}"}],
             "rightsDefault":"deny",
             "events":[{"id":"{{Submit}}","name":"submit","verb":"promote","actors":["{{Author}}"]},
               {"id":"{{Release}}","name":"release","verb":"promote","actors":["{{Reviewer}}"]}],
             "guards":[{"id":"{{Titled}}","name":"titled","condition":{"op":"eq","args":[{{PathTo(Title)}},{"value":"Pump spec"}]} }],
             "states":[{"id":"{{Draft}}","name":"Draft"},{"id":"{{Review}}","name":"Review"},{"id":"{{Released}}","name":"Released"}],
             "initial":"{{Draft}}",
             "transitions":[{"id":"01JRRK00000000000000000001","source":"{{Draft}}","event":"{{Submit}}","targets":["{{Review}}"]},
               {"id":"01JRRK00000000000000000002","source":"{{Review}}","event":"{{Release}}","guard":"{{Titled}}","targets":["{{Released}}"]}],
             "rights":[{"id":"01JRRR00000000000000000002","name":"EveryoneReads","access":"allow","verbs":["read"]},
               {"id":"{{AuthorsEditDrafts}}","name":"AuthorsEditDrafts","access":"allow","actors":["{{Author}}"],
                "verbs":["modify","connect","disconnect","delete"],"states":["{{Draft}}"]},
               {"id":"{{ReleasedIsReadOnly}}","name":"ReleasedIsReadOnly","access":"deny","verbs":["modify","delete"],"states":["{{Released}}"],
                "description":"A released document changes only by revising it."},
               {"id":"{{NoBomChangesInReview}}","name":"NoBomChangesInReview","access":"deny","verbs":["connect","disconnect"],"ends":["{{Components}}"],
                "states":["{{Review}}"]},
               {"id":"01JRRR00000000000000000008","name":"PartsNotesAnywhere","access":"allow","verbs":["modify"],"ends":["{{Components}}"]},
               {"id":"01JRRR00000000000000000006","right":"{{SecretOnlyOwner}}"}]}
            """);
        harness.Write("model/processes/hr.json", $$"""
            {"kind":"process","id":"{{Hr}}","name":"HR","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JRRB00000000000000000002","entity":"{{Employee}}"}],
             "rightsDefault":"deny",
             "states":[{"id":"{{HrActive}}","name":"Active"},{"id":"01JRRS00000000000000000012","name":"Suspended"}],
             "initial":"{{HrActive}}",
             "rights":[{"id":"01JRRR00000000000000000011","name":"HrStaffModify","access":"allow","actors":["{{HrStaff}}"],"verbs":["modify"]},
               {"id":"{{NoEditsWhenClosed}}","name":"NoEditsWhenClosed","access":"deny","verbs":["modify"],"condition":{"inState":["{{ProjectClosed}}"]} }]}
            """);
        harness.Write("model/processes/projects.json", $$"""
            {"kind":"process","id":"{{Projects}}","name":"Projects","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JRRB00000000000000000003","entity":"{{Employee}}"}],
             "events":[{"id":"{{Assign}}","name":"assign","verb":"modify"},{"id":"{{Close}}","name":"close","verb":"promote"}],
             "states":[{"id":"{{ProjectOpen}}","name":"Open"},{"id":"{{ProjectClosed}}","name":"Closed"}],
             "initial":"{{ProjectOpen}}",
             "transitions":[{"id":"01JRRK00000000000000000011","source":"{{ProjectOpen}}","event":"{{Assign}}"},
               {"id":"01JRRK00000000000000000012","source":"{{ProjectOpen}}","event":"{{Close}}","targets":["{{ProjectClosed}}"]}]}
            """);
        harness.Write("model/processes/onboarding.json", $$"""
            {"kind":"process","id":"{{Onboarding}}","name":"Onboarding","package":"{{Package}}","use":"orchestration",
             "subjects":[{"id":"01JRRB00000000000000000004","entity":"{{Employee}}"}],
             "events":[{"id":"{{Update}}","name":"update","verb":"modify"}],
             "states":[{"id":"01JRRS00000000000000000031","name":"Start"}],
             "initial":"01JRRS00000000000000000031",
             "transitions":[{"id":"01JRRK00000000000000000021","source":"01JRRS00000000000000000031","event":"{{Update}}"}]}
            """);
        harness.Write("model/processes/loose.json", $$"""
            {"kind":"process","id":"{{Loose}}","name":"Loose","package":"{{Package}}","use":"orchestration",
             "events":[{"id":"{{Poke}}","name":"poke","verb":"modify"}],
             "states":[{"id":"01JRRS00000000000000000041","name":"Start"}],
             "initial":"01JRRS00000000000000000041",
             "transitions":[{"id":"01JRRK00000000000000000031","source":"01JRRS00000000000000000041","event":"{{Poke}}"}]}
            """);
        harness.Write("model/processes/approval.json", $$"""
            {"kind":"process","id":"{{Approval}}","name":"Approval","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JRRB00000000000000000006","entity":"{{Contract}}"}],
             "rightsDefault":"deny",
             "events":[{"id":"{{Approve}}","name":"approve","verb":"promote"}],
             "states":[{"id":"{{Drafting}}","name":"Drafting","invoke":[{"id":"{{Prepare}}","name":"prepare","type":"human-task","actors":["{{Clerk}}"]}]},
               {"id":"{{Submitted}}","name":"Submitted"},{"id":"{{Approved}}","name":"Approved","type":"final"}],
             "initial":"{{Drafting}}",
             "transitions":[{"id":"01JRRK00000000000000000041","source":"{{Drafting}}","trigger":"invoke-done","invoke":"{{Prepare}}","targets":["{{Submitted}}"]},
               {"id":"01JRRK00000000000000000042","source":"{{Submitted}}","event":"{{Approve}}","targets":["{{Approved}}"],
                "gate":{"id":"01JRRX00000000000000000033","name":"signoff","required":1,"signers":["{{Approver}}"],"meanings":[{"id":"01JRRX00000000000000000034","name":"approved"}]} }],
             "rights":[{"id":"{{NoSelfSign}}","name":"NoSelfSign","access":"deny","verbs":["sign"],
                 "condition":{"op":"eq","args":[{{PathTo(SubmittedBy)}},{"currentActor":true}]},"description":"No one signs off what they submitted."},
               {"id":"{{NoTempPromotes}}","name":"NoTempPromotes","access":"deny","actors":["{{Temp}}"],"verbs":["promote"]}]}
            """);
        if (vocabulary)
        {
            harness.Write("model/vocabularies/verbs.json", """
                {"kind":"verb-vocabulary","id":"01JRRV00000000000000000001","name":"verbs","verbs":[{"id":"01JRRV00000000000000000002","key":"archive"}]}
                """);
        }

        if (scenario is not null)
            harness.Write("model/scenarios/documents/archiving.json", scenario);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // The document Spec, secret, owned by bea, with the part Pump linked through components; states, when given, in any lifecycle.
    private static JsonElement[] SpecObjects() =>
    [
        Json($$"""{"id":"{{Spec}}","name":"spec","entity":"{{Document}}","owner":"p1","values":{"{{Classification}}":"secret"},"links":[{"end":"{{Components}}","object":"{{Pump}}"}]}"""),
        Json($$"""{"id":"{{Pump}}","name":"pump","entity":"{{Part}}"}"""),
    ];

    private static JsonElement[] People() =>
    [
        Json($$"""{"id":"p1","name":"bea","holds":[{"id":"h1","actor":"{{Author}}"}]}"""),
        Json($$"""{"id":"p2","name":"carl","holds":[{"id":"h2","actor":"{{Author}}"}]}"""),
        Json($$"""{"id":"p3","name":"rita","holds":[{"id":"h3","actor":"{{Reviewer}}"}]}"""),
        Json($$"""{"id":"p4","name":"pam","holds":[{"id":"h4","actor":"{{ProjectManager}}"}]}"""),
        Json($$"""{"id":"p5","name":"hal","holds":[{"id":"h5","actor":"{{HrStaff}}"}]}"""),
        Json($$"""{"id":"p6","name":"ann","holds":[{"id":"h6","actor":"{{Approver}}"}]}"""),
        Json($$"""{"id":"p7","name":"bob","holds":[{"id":"h7","actor":"{{Approver}}"}]}"""),
        Json($$"""{"id":"p8","name":"cy","holds":[{"id":"h8","actor":"{{Approver}}"},{"id":"h8b","actor":"{{Temp}}"}]}"""),
        Json($$"""{"id":"p9","name":"cleo","actor":"{{Clerk}}"}"""),
    ];

    private static JsonElement Verb(string verb, string participant, string extra = "") =>
        Json($$"""{"input":"verb","verb":"{{verb}}","participant":"{{participant}}"{{extra}}}""");

    private static JsonElement Event(string ev, string participant, string extra = "") => Json($$"""{"event":"{{ev}}","participant":"{{participant}}"{{extra}}}""");

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, string process, JsonElement[] steps, JsonElement[]? objects = null, string? subject = null)
    {
        var call = await store.SimulateProcessAsync(process, new SimulateRequest { Steps = steps, Objects = objects, Subject = subject, Participants = People() }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!;
    }

    private static void Refused(StepTrace trace, string refusal, string? by)
    {
        Assert.False(trace.Accepted, $"step {trace.Index} was accepted");
        Assert.Equal((refusal, by), (trace.Refusal, trace.RefusedBy));
    }

    [Fact]
    public async Task The_model_validates_without_errors()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("model/", StringComparison.Ordinal)
            && !(d.FilePath ?? "").Contains("order", StringComparison.Ordinal)).Select(d => d.Rule + " " + d.Message));
    }

    [Fact]
    public async Task The_document_scenario_refuses_carl_by_the_named_right_and_lets_the_owner_edit_and_a_later_guard_sees_it()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var titled = $",\"values\":{{\"{Title}\":\"Pump spec\"}}";
        var result = await SimulateAsync(store, Documents,
            [Verb("modify", "p2", titled), Verb("modify", "p1", titled), Event(Submit, "p1"), Event(Release, "p3")], SpecObjects(), Spec);
        // Carl is refused by the named right: a deny wins over AuthorsEditDrafts. Bea is the owner, so the named right says nothing for her.
        Refused(result.Trace[1], Refusals.Right, SecretOnlyOwner);
        Assert.Contains(result.Trace[1].Rights!, r => r.Right == SecretOnlyOwner && r.Access == RightAccess.Deny && r.Lifecycle == Documents);
        Assert.True(result.Trace[2].Accepted);
        Assert.Equal("Pump spec", result.Trace[2].ValuesChanged![Title].GetString());
        Assert.Equal([new RightEvaluation(AuthorsEditDrafts, Documents, RightAccess.Allow)], result.Trace[2].Rights);
        Assert.Null(result.Trace[2].RefusedBy);
        // The event's actors allow submit and release under a deny default, and the guard reads the title Bea set.
        Assert.True(result.Trace[3].Accepted && result.Trace[4].Accepted);
        Assert.Equal([Released], result.Configuration);
        Assert.Null(result.Trace[3].Rights);
    }

    [Fact]
    public async Task Each_check_refuses_in_its_order_and_names_what_refused()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Documents,
        [
            Event(Release, "p2"), // the closed list: carl is no reviewer
            Event(Release, "p3"), // allowed by its list, but no transition from Draft
            Verb("change-owner", "p1"), // bea is excepted from the named right, and nothing allows it: the default
            Verb("change-owner", "p2"), // the named right holds for carl before any default
            Event(Submit, "p1"),
            Event(Release, "p3"), // in Review, the guard: the title is not set
            Verb("read", "p3"),
        ], SpecObjects(), Spec);
        Refused(result.Trace[1], Refusals.Actor, Release);
        Assert.Null(result.Trace[1].Rights);
        Refused(result.Trace[2], Refusals.NoTransition, null);
        Refused(result.Trace[3], Refusals.Default, Documents);
        Refused(result.Trace[4], Refusals.Right, SecretOnlyOwner);
        Assert.True(result.Trace[5].Accepted);
        Refused(result.Trace[6], Refusals.Guard, Titled);
        Assert.True(result.Trace[7].Accepted);
        Assert.Contains("did not apply it", string.Join(" ", result.Trace[7].Notes!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_verbs_answer_through_ends_and_are_recorded_without_change()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var link = $",\"end\":\"{Components}\",\"object\":\"{Pump}\"";
        var result = await SimulateAsync(store, Documents,
            [Verb("disconnect", "p1", link), Verb("modify", "p2", link), Event(Submit, "p1"), Verb("disconnect", "p1", link), Verb("modify", "p1", link)],
            SpecObjects(), Spec);
        Assert.True(result.Trace[1].Accepted);
        Assert.Contains("on a link", string.Join(" ", result.Trace[1].Notes!), StringComparison.Ordinal);
        // modify on a link speaks of the link: the named right and AuthorsEditDrafts speak of the object, PartsNotesAnywhere of the link.
        Assert.True(result.Trace[2].Accepted);
        Assert.Null(result.Trace[2].ValuesChanged);
        Refused(result.Trace[4], Refusals.Right, NoBomChangesInReview);
        Assert.True(result.Trace[5].Accepted);
    }

    [Fact]
    public async Task A_delete_stops_the_chart_and_later_steps_are_refused_with_no_refusing_element()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Documents,
            [Verb("delete", "p1"), Event(Submit, "p1"), Verb("read", "p1"), Json("""{"input":"time","after":"P1D"}""")], SpecObjects(), Spec);
        Assert.True(result.Trace[1].Accepted);
        Assert.Contains("deleted the object", string.Join(" ", result.Trace[1].Notes!), StringComparison.Ordinal);
        Refused(result.Trace[2], Refusals.NoTransition, null);
        Refused(result.Trace[3], Refusals.NoTransition, null);
        Assert.True(result.Trace[4].Accepted);
        Assert.True(string.CompareOrdinal(result.Trace[3].Clock, result.Trace[4].Clock) < 0);
        Assert.Equal([Draft], result.Configuration);
        Assert.False(result.Final);
    }

    [Fact]
    public async Task Values_or_attributes_beside_another_verb_are_MQ9305()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        foreach (var step in new[] { Verb("modify", "p1", $",\"attributes\":[\"{Title}\"]"), Verb("read", "p1", $",\"values\":{{\"{Title}\":\"x\"}}") })
        {
            var result = await SimulateAsync(store, Documents, [step], SpecObjects(), Spec);
            Assert.Contains(result.Diagnostics, d => d.Rule == "MQ9305");
            Assert.False(result.Trace[1].Accepted);
        }
    }

    [Fact]
    public async Task Another_lifecycle_answers_on_its_own_and_any_no_wins()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var eve = new[] { Json($$"""{"id":"{{Eve}}","name":"eve","entity":"{{Employee}}","states":["{{HrActive}}","{{ProjectOpen}}"]}""") };
        // Projects allows by default; HR denies by default and lets only HR staff modify: a project manager is refused by HR's default.
        var run = await SimulateAsync(store, Projects, [Event(Assign, "p4"), Event(Assign, "p5"), Event(Close, "p4"), Verb("modify", "p5")], eve, Eve);
        Refused(run.Trace[1], Refusals.Default, Hr);
        Assert.True(run.Trace[2].Accepted);
        Assert.True(run.Trace[3].Accepted); // promote is a move of Projects: HR is not asked
        // HR's right reads Projects through in_state: the running chart's live state, Closed, not the state the object gave.
        Refused(run.Trace[4], Refusals.Right, NoEditsWhenClosed);

        // With no object, HR answers in its initial state, with a note.
        var none = await SimulateAsync(store, Projects, [Event(Assign, "p4")]);
        Refused(none.Trace[1], Refusals.Default, Hr);
        Assert.Contains(none.Trace[1].Notes!, n => n.Contains("initial state", StringComparison.Ordinal));
        // An object that gives no HR state answers in HR's initial state, with a note.
        var stateless = await SimulateAsync(store, Projects, [Event(Assign, "p5")],
            [Json($$"""{"id":"{{Eve}}","name":"eve","entity":"{{Employee}}"}""")], Eve);
        Assert.True(stateless.Trace[1].Accepted);
        Assert.Contains(stateless.Trace[1].Notes!, n => n.Contains("gives no state in lifecycle 'HR'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_orchestration_event_on_its_subject_is_answered_by_the_subjects_lifecycles()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var eve = new[] { Json($$"""{"id":"{{Eve}}","name":"eve","entity":"{{Employee}}"}""") };
        var run = await SimulateAsync(store, Onboarding, [Event(Update, "p4"), Event(Update, "p5")], eve, Eve);
        Refused(run.Trace[1], Refusals.Default, Hr);
        Assert.True(run.Trace[2].Accepted);
        // An orchestration with no subjects has no object: only its closed lists answer.
        var loose = await SimulateAsync(store, Loose, [Event(Poke, "p4")]);
        Assert.True(loose.Trace[1].Accepted);
        Assert.Null(loose.Trace[1].Rights);
    }

    [Fact]
    public async Task Tasks_and_gate_signers_count_as_allows_and_sign_is_checked_at_the_signatures_step()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var deal = new[] { Json($$"""{"id":"{{Deal}}","name":"deal","entity":"{{Contract}}","values":{"{{SubmittedBy}}":"ann"} }""") };
        var run = await SimulateAsync(store, Approval,
        [
            Json($$"""{"input":"invoke-error","invoke":"{{Prepare}}","participant":"p6"}"""), // a task's error is checked as complete
            Json($$"""{"input":"invoke-done","invoke":"{{Prepare}}","participant":"p9"}"""),
            Event(Approve, "p8"), // cy acts as Approver and holds Temp: a deny on the gated event's verb stops signing
            Event(Approve, "p6"), // ann submitted it: the signatures step refuses her by the right on sign
            Event(Approve, "p7"),
        ], deal, Deal);
        Refused(run.Trace[1], Refusals.Actor, Prepare);
        Assert.True(run.Trace[2].Accepted);
        Refused(run.Trace[3], Refusals.Right, NoTempPromotes);
        Refused(run.Trace[4], Refusals.Right, NoSelfSign);
        Assert.Contains(run.Trace[4].Audit, a => a.Outcome == GateOutcome.Refused);
        Assert.True(run.Trace[5].Accepted);
        Assert.True(run.Final);
    }

    [Fact]
    public async Task Recording_writes_refusals_and_replay_and_verify_compare_them()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var titled = $",\"values\":{{\"{Title}\":\"Pump spec\"}}";
        var recorded = await store.RecordScenarioAsync(Documents, new RecordScenarioRequest
        {
            Name = "secretEdits", Objects = SpecObjects(), Subject = Spec, Participants = People(),
            Steps = [Verb("modify", "p2", titled), Verb("modify", "p1", titled), Event(Release, "p2"), Event(Release, "p3")],
        }, false, ChangeSource.Editor, Ct);
        Assert.True(recorded.Status is ProcessCallStatus.Ok, recorded.Detail + string.Join(" | ", recorded.Diagnostics.Select(d => d.Message)));
        var steps = recorded.Value!.Element.GetProperty("steps");
        Assert.Equal(("right", SecretOnlyOwner), (steps[0].GetProperty("expect").GetProperty("refusal").GetString(),
            steps[0].GetProperty("expect").GetProperty("refusedBy").GetString()));
        Assert.False(steps[1].GetProperty("expect").TryGetProperty("refusal", out var noRefusal));
        Assert.Equal(Release, steps[2].GetProperty("expect").GetProperty("refusedBy").GetString());
        Assert.Equal("no-transition", steps[3].GetProperty("expect").GetProperty("refusal").GetString());
        Assert.False(steps[3].GetProperty("expect").TryGetProperty("refusedBy", out var noRefusedBy));
        var id = recorded.Value.Id;

        var verified = await store.VerifyScenariosAsync(Documents, [id], Ct);
        Assert.True(verified.Value!.Results.Single().Passed, verified.Value.Results.Single().Failure?.Message);

        // An expectation that names another refusing element is MQ9308.
        var scenario = store.Current!.Get<Scenario>(id)!;
        var wrong = scenario with
        {
            Steps = [scenario.Steps[0] with { Expect = scenario.Steps[0].Expect! with { RefusedBy = ReleasedIsReadOnly } }, .. scenario.Steps.Skip(1)],
        };
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        var replay = ScenarioReplayer.Replay(wrong, runtime)!;
        Assert.Equal("MQ9308", replay.Failure?.Rule);
        Assert.Equal("/steps/0/expect/refusedBy", replay.Diagnostics.Single(d => d.Rule == "MQ9308").JsonPointer);
        var refusal = scenario with
        {
            Steps = [scenario.Steps[0] with { Expect = scenario.Steps[0].Expect! with { Refusal = Refusals.Default, RefusedBy = null } }, .. scenario.Steps.Skip(1)],
        };
        Assert.Equal("MQ9308", ScenarioReplayer.Replay(refusal, runtime)!.Failure?.Rule);
    }

    [Fact]
    public async Task Rename_and_remove_verb_rewrite_and_remove_scenario_verb_steps()
    {
        var scenario = $$"""
            {"kind":"scenario","id":"01JRRC00000000000000000001","name":"archiving","process":"{{Documents}}",
             "objects":[{"id":"{{Spec}}","name":"spec","entity":"{{Document}}"}],"subject":"{{Spec}}",
             "participants":[{"id":"01JRRC00000000000000000002","name":"bea","holds":[{"id":"01JRRC00000000000000000005","actor":"{{Author}}"}]}],
             "steps":[{"id":"01JRRC00000000000000000003","input":"verb","verb":"archive","participant":"01JRRC00000000000000000002","expect":{"accepted":false,"states":["{{Draft}}"]} },
               {"id":"01JRRC00000000000000000004","input":"verb","verb":"read","participant":"01JRRC00000000000000000002","expect":{"states":["{{Draft}}"]} }]}
            """;
        var (h, store) = await OpenAsync(vocabulary: true, scenario: scenario);
        using var _ = h;
        await using var __ = store;
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(store, """{"op":"rename-verb","verb":"archive","to":"shelve"}""")).Outcome);
        Assert.Equal("shelve", store.Current!.Get<Scenario>("01JRRC00000000000000000001")!.Steps[0].Verb);
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(store, """{"op":"remove-verb","verb":"shelve"}""")).Outcome);
        Assert.Equal(["read"], store.Current!.Get<Scenario>("01JRRC00000000000000000001")!.Steps.Select(s => s.Verb));
    }

    [Fact]
    public async Task Export_and_import_carry_rights_unchanged()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var process = store.Current!.Get<Process>(Documents)!;
        var exported = XStateProjection.Export(process, TestServices.Json);
        var maquettiste = JsonNode.Parse(exported.Json)!["meta"]!["maquettiste"]!;
        Assert.Equal("deny", maquettiste["rightsDefault"]!.GetValue<string>());
        var imported = XStateProjection.Import(exported.Json, TestServices.Json, new XStateImportOptions { Model = store.Current, Into = process, Package = Package });
        Assert.False(imported.HasErrors, string.Join(" | ", imported.Diagnostics.Select(d => d.Message)));
        Assert.Equal(JsonSerializer.Serialize(process.Rights, EngineJson.Options), JsonSerializer.Serialize(imported.Process!.Rights, EngineJson.Options));
        Assert.Equal(RightAccess.Deny, imported.Process.RightsDefault);
    }

    [Fact]
    public async Task A_project_without_rights_keeps_its_traces_and_recordings()
    {
        using var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        await using var store = new ModelStore(harness.Options, harness.Services());
        await store.LoadAsync(Ct);
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        foreach (var scenario in store.Current!.All<Scenario>())
        {
            var replay = ScenarioReplayer.Replay(scenario, runtime)!;
            Assert.True(replay.Passed);
            Assert.False(replay.UsesRights);
            Assert.All(replay.Steps, t => Assert.Null(t.RefusedBy ?? (object?)t.Rights));
            Assert.All(replay.Observed().Steps, e => Assert.Null(e.Refusal ?? e.RefusedBy));
            Assert.DoesNotContain("refusedBy", JsonSerializer.Serialize(replay.Steps, EngineJson.Options), StringComparison.Ordinal);
        }
    }

    // Part 2's no change (rights-design.md section 14): a project that uses part 1 rights but no freezing and no attribute rights, and one
    // without rights, keep their traces and recorded expectations byte for byte. The hash was taken from the engine before freezing took effect.
    [Fact]
    public async Task A_project_without_freezing_or_attribute_rights_keeps_its_traces_and_recordings_byte_for_byte()
    {
        var (h, store) = await OpenAsync(vocabulary: true);
        using var _ = h;
        await using var __ = store;
        var titled = $",\"values\":{{\"{Title}\":\"Pump spec\"}}";
        var link = $",\"end\":\"{Components}\",\"object\":\"{Pump}\"";
        var runs = new (string Process, JsonElement[] Steps, JsonElement[]? Objects, string? Subject)[]
        {
            (Documents, [Verb("modify", "p2", titled), Verb("modify", "p1", titled), Verb("read", "p3"), Verb("archive", "p1"), Verb("disconnect", "p1", link),
                Event(Release, "p2"), Event(Submit, "p1"), Verb("connect", "p1", link), Verb("modify", "p1", link), Event(Release, "p3"), Verb("delete", "p1"),
                Event(Submit, "p1")], SpecObjects(), Spec),
            (Documents, [Verb("modify", "p1", titled), Event(Submit, "p1"), Event(Release, "p3")], null, null),
            (Onboarding, [Event(Update, "p4"), Event(Update, "p5")], [Json($$"""{"id":"{{Eve}}","name":"eve","entity":"{{Employee}}"}""")], Eve),
            (Loose, [Event(Poke, "p4")], null, null),
        };
        var text = new StringBuilder();
        foreach (var (process, steps, objects, subject) in runs)
        {
            var result = await SimulateAsync(store, process, steps, objects, subject);
            text.Append(JsonSerializer.Serialize(result.Trace, EngineJson.Options)).Append('\n');
            var recorded = await store.RecordScenarioAsync(process, new RecordScenarioRequest
            {
                // Recording mints the objects' ids again, so link steps are simulated only.
                Name = "run" + text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), Objects = objects, Subject = subject, Participants = People(), Steps = [.. steps.Where(s => !s.TryGetProperty("end", out var _))],
            }, false, ChangeSource.Editor, Ct);
            Assert.True(recorded.Status is ProcessCallStatus.Ok, process + " " + steps.Length + " " + recorded.Detail + string.Join(" | ", recorded.Diagnostics.Select(d => d.Message)));
            foreach (var step in recorded.Value!.Element.GetProperty("steps").EnumerateArray())
                text.Append(step.TryGetProperty("expect", out var expect) ? expect.GetRawText() : "-").Append('\n');
            text.Append(recorded.Value.Element.TryGetProperty("outcome", out var outcome) ? outcome.GetRawText() : "-").Append('\n');
        }

        using var plain = new LoaderHarness();
        plain.CopyFixture("models", "process-basics");
        await using var basics = new ModelStore(plain.Options, plain.Services());
        await basics.LoadAsync(Ct);
        using var runtime = new ProcessRuntime(basics.Current!, 1, Ct);
        foreach (var scenario in basics.Current!.All<Scenario>().OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var replay = ScenarioReplayer.Replay(scenario, runtime)!;
            text.Append(JsonSerializer.Serialize(replay.Steps, EngineJson.Options)).Append('\n');
            text.Append(JsonSerializer.Serialize(replay.Observed().Steps.Select(ProcessApi.ExpectNode), EngineJson.Options)).Append('\n');
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        Assert.Equal("1B5A7854B209FB564FB53A1FF11D8DEF610807512BFA728928107CFB08860F10", hash);
    }

    private static async Task<BatchResult> ApplyAsync(ModelStore store, string operation)
    {
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
    }
}
