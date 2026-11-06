using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Actors worked out from the object (actors-design.md decisions 8 to 13) over the <c>process-basics</c> fixture with an invented model:
/// Item and Document derive from Work, which has an owner login, tags and a project; a project has a lead; a document has an author.
/// Review governs Item (its owner is Work.ownerLogin) and Document (its owner is the author end); Owner, Lead (the project's lead) and
/// ProjectReviewer (members of Reviewer within the project) are worked out from the object.
/// </summary>
public sealed class RelativeActorTests
{
    internal const string Pkg = "01JPKG00000000000000000001";
    internal const string Work = "01JENT00000000000000000101";
    internal const string Item = "01JENT00000000000000000102";
    internal const string Document = "01JENT00000000000000000103";
    internal const string Project = "01JENT00000000000000000104";
    internal const string Person = "01JENT00000000000000000105";
    internal const string OwnerLogin = "01JATT00000000000000000102";
    internal const string Tags = "01JATT00000000000000000103";
    internal const string ItemOnly = "01JATT00000000000000000104";
    internal const string ProjectName = "01JATT00000000000000000106";
    internal const string WorkProject = "01JRXN00000000000000000101";
    internal const string ProjectLead = "01JRXN00000000000000000102";
    internal const string DocumentAuthor = "01JRXN00000000000000000104";
    internal const string EProject = "01JEND00000000000000000102";
    internal const string ELead = "01JEND00000000000000000104";
    internal const string EWatchers = "01JEND00000000000000000106";
    internal const string EAuthor = "01JEND00000000000000000108";
    internal const string Reviewer = "01JACT00000000000000000101";
    internal const string Owner = "01JACT00000000000000000102";
    internal const string Lead = "01JACT00000000000000000103";
    internal const string ProjectReviewer = "01JACT00000000000000000104";
    internal const string Ann = "01JACT00000000000000000105";
    internal const string Review = "01JPRC00000000000000000101";
    internal const string ItemEntry = "01JPRX00000000000000000101";
    internal const string DocumentEntry = "01JPRX00000000000000000102";
    internal const string SubmitEvent = "01JPRC00000000000000000911"; // Review's events: its id's prefix, then 911 and 912
    internal const string ApproveEvent = "01JPRC00000000000000000912";
    internal const string ReviewFile = "model/processes/review.json";

    private static CancellationToken Ct => ActorKit.Ct;

    private static string Attr(string id, string name, string type = "string", bool collection = false) =>
        $$"""{"id":"{{id}}","name":"{{name}}","type":"{{type}}"{{(type == "string" ? ",\"length\":40" : "")}}{{(collection ? ",\"collection\":true" : "")}}}""";

    // A root entity is keyed by its first attribute.
    private static string Entity(string id, string name, string? @base, params string[] attributes) =>
        $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Pkg}}"{{(@base is null ? ",\"key\":{\"attributes\":[\"" + attributes[0][7..33] + "\"]}" : ",\"base\":\"" + @base + "\"")}},"attributes":[{{string.Join(",", attributes)}}]}""";

    private static string Relation(string id, string name, string fromEnd, string from, string fromRole, string toEnd, string to, string navigation, bool many = false) =>
        $$"""{"kind":"relation","id":"{{id}}","name":"{{name}}","ends":[{"id":"{{fromEnd}}","entity":"{{from}}","role":"{{fromRole}}"},{"id":"{{toEnd}}","entity":"{{to}}","role":"{{navigation}}"{{(many ? "" : ",\"max\":1")}},"navigation":"{{navigation}}"}]}""";

    /// <summary>An actor file's text with a relative (raw JSON) and optional memberOf.</summary>
    internal static string Relative(string id, string name, string type, string relative, params string[] memberOf) =>
        $$"""{"kind":"actor","id":"{{id}}","name":"{{name}}","type":"{{type}}"{{(memberOf.Length == 0 ? "" : ",\"memberOf\":[" + string.Join(",", memberOf.Select(m => "\"" + m + "\"")) + "]")}},"relative":{{relative}}}""";

    internal static string Process(string id, string name, string subjects, string submitActors, string approveActors, string use = "lifecycle", string gate = "") => $$"""
        {"kind":"process","id":"{{id}}","name":"{{name}}","package":"{{Pkg}}","use":"{{use}}"{{subjects}},
         "events":[{"id":"{{id[..^3]}}911","name":"submit","actors":[{{submitActors}}]},{"id":"{{id[..^3]}}912","name":"approve","actors":[{{approveActors}}]}],
         "states":[{"id":"{{id[..^3]}}921","name":"Draft"},{"id":"{{id[..^3]}}922","name":"Submitted"},{"id":"{{id[..^3]}}923","name":"Done","type":"final"}],
         "transitions":[{"id":"{{id[..^3]}}931","source":"{{id[..^3]}}921","event":"{{id[..^3]}}911","targets":["{{id[..^3]}}922"]},
           {"id":"{{id[..^3]}}932","source":"{{id[..^3]}}922","event":"{{id[..^3]}}912","targets":["{{id[..^3]}}923"]{{gate}}}]}
        """;

    internal static string Ids(params string[] ids) => string.Join(",", ids.Select(i => "\"" + i + "\""));

    internal const string ReviewSubjects = $$""","subjects":[{"id":"{{ItemEntry}}","entity":"{{Item}}","owner":"{{OwnerLogin}}"},{"id":"{{DocumentEntry}}","entity":"{{Document}}","owner":"{{EAuthor}}"}]""";

    // The invented model; prepare adds or overwrites files after it.
    internal static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null) =>
        await ActorKit.OpenAsync(h =>
        {
            h.Write("model/entities/work.json", Entity(Work, "Work", null, Attr("01JATT00000000000000000101", "id", "uuid"), Attr(OwnerLogin, "ownerLogin"), Attr(Tags, "tags", collection: true)));
            h.Write("model/entities/item.json", Entity(Item, "Item", Work, Attr(ItemOnly, "itemOnly")));
            h.Write("model/entities/document.json", Entity(Document, "Document", Work));
            h.Write("model/entities/project.json", Entity(Project, "Project", null, Attr("01JATT00000000000000000105", "id", "uuid"), Attr(ProjectName, "name")));
            h.Write("model/entities/person.json", Entity(Person, "Person", null, Attr("01JATT00000000000000000107", "id", "uuid")));
            h.Write("model/relations/work-project.json", Relation(WorkProject, "WorkProject", "01JEND00000000000000000101", Work, "works", EProject, Project, "project"));
            h.Write("model/relations/project-lead.json", Relation(ProjectLead, "ProjectLead", "01JEND00000000000000000103", Project, "led", ELead, Person, "lead"));
            h.Write("model/relations/work-watchers.json", Relation("01JRXN00000000000000000103", "WorkWatchers", "01JEND00000000000000000105", Work, "watched", EWatchers, Person, "watchers", many: true));
            h.Write("model/relations/document-author.json", Relation(DocumentAuthor, "DocumentAuthor", "01JEND00000000000000000107", Document, "authored", EAuthor, Person, "author"));
            h.Write("model/actors/reviewer.json", ActorKit.Actor(Reviewer, "Reviewer", "role"));
            h.Write("model/actors/ann.json", ActorKit.Actor(Ann, "Ann", "person", Reviewer));
            h.Write("model/actors/owner.json", Relative(Owner, "Owner", "person", """{"owner":true}"""));
            h.Write("model/actors/lead.json", Relative(Lead, "Lead", "person", $$"""{"path":[{{Ids(EProject, ELead)}}]}"""));
            h.Write("model/actors/project-reviewer.json", Relative(ProjectReviewer, "ProjectReviewer", "role", $$"""{"membersOf":"{{Reviewer}}","within":[{{Ids(EProject)}}]}"""));
            h.Write(ReviewFile, Process(Review, "Review", ReviewSubjects, Ids(Owner), Ids(ProjectReviewer, Lead)));
            prepare?.Invoke(h);
        });

    private static IEnumerable<string> Hits(IEnumerable<Diagnostic> diagnostics, string rule) =>
        diagnostics.Where(d => d.Rule == rule).Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer).Order(StringComparer.Ordinal);

    internal static JsonObject Record(Maquettiste.Engine.Resolution.ResolvedModel model, string id) =>
        JsonSerializer.SerializeToNode(ResolvedRecords.Entries(model, "all", null).Single(e => e.Id == id).Project(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();

    // ---- no change ----

    [Fact]
    public async Task A_project_without_actors_from_the_object_is_unchanged()
    {
        var (h, store) = await ActorKit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var diagnostics = await ActorKit.ValidateAsync(h, model);

        Assert.DoesNotContain(diagnostics, d => d.Rule is "MQ9603" or "MQ9604" or "MQ9606");
        Assert.All(model.Index.Summaries, s => Assert.Null(s.Relative));
        var resolved = ResolutionKit.Resolve(model);
        Assert.All(resolved.Actors, a => Assert.Null(a.Relative));
        Assert.All(resolved.Processes.SelectMany(p => p.Subjects), s => Assert.True(s.OwnerAttribute is null && s.OwnerEnd is null));
        Assert.False(Record(resolved, ActorKit.Clerk).ContainsKey("relative"));
        Assert.All(model.All<Actor>(), a => Assert.Null(a.Relative));
    }

    // ---- the forms, resolution and records ----

    [Fact]
    public async Task Each_form_resolves_on_two_subjects_with_a_shared_base()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var diagnostics = await ActorKit.ValidateAsync(h, model);
        Assert.DoesNotContain(diagnostics, d => d.Rule is "MQ2001" or "MQ2002" or "MQ9601" or "MQ9603" or "MQ9604" or "MQ9606" || (d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("review", StringComparison.Ordinal)));

        var review = model.Get<Process>(Review)!;
        Assert.Equal(OwnerLogin, review.Subjects[0].Owner);
        foreach (var id in new[] { Owner, Lead, ProjectReviewer })
            Assert.Null(RelativeActors.Resolve(model, review, model.Get<Actor>(id)!.Relative!).Why);
        Assert.Equal([EProject, ELead], RelativeActors.Resolve(model, review, model.Get<Actor>(Lead)!.Relative!).Steps.Select(s => s.End!.Id));

        // The index row's form, only on actors from the object.
        string? Form(string id) => model.Index.Summaries.Single(s => s.Id == id).Relative;
        Assert.Equal(("owner", "path", "members-of", (string?)null), (Form(Owner), Form(Lead), Form(ProjectReviewer), Form(Reviewer)));

        // The resolved model and the records.
        var resolved = ResolutionKit.Resolve(model);
        var lead = resolved.Actors.Single(a => a.Id == Lead).Relative!;
        Assert.Equal("path " + EProject + " " + ELead, lead.Form + " " + string.Join(" ", lead.Path.Select(o => o.Id)));
        var reviewers = resolved.Actors.Single(a => a.Id == ProjectReviewer).Relative!;
        Assert.Equal("members-of " + Reviewer + " " + EProject, reviewers.Form + " " + reviewers.MembersOf!.Id + " " + string.Join(" ", reviewers.Path.Select(o => o.Id)));
        Assert.Empty(resolved.Actors.Single(a => a.Id == Owner).Relative!.Path);
        var subjects = resolved.Processes.Single(p => p.Id == Review).Subjects;
        Assert.Equal((OwnerLogin, (string?)null), (subjects[0].OwnerAttribute?.Id, subjects[0].OwnerEnd?.Id));
        Assert.Equal(((string?)null, EAuthor), (subjects[1].OwnerAttribute?.Id, subjects[1].OwnerEnd?.Id));
        Assert.Equal("""{"form":"members-of","path":["01JEND00000000000000000102"],"membersOf":"01JACT00000000000000000101"}""", Record(resolved, ProjectReviewer)["relative"]!.ToJsonString());
        Assert.Equal("""{"form":"owner","path":[]}""", Record(resolved, Owner)["relative"]!.ToJsonString());
        var entries = Record(resolved, Review)["subjects"]!.AsArray();
        Assert.Equal((OwnerLogin, EAuthor), (entries[0]!["owner"]!.GetValue<string>(), entries[1]!["owner"]!.GetValue<string>()));
        Assert.False(Record(resolved, Ann).ContainsKey("relative"));

        // The index row follows a save (a patch), and goes when relative does.
        var saved = await store.SaveAsync(Lead, Encoding.UTF8.GetBytes(Relative(Lead, "Lead", "person", """{"owner":true}""")), model.GetDocument(Lead)!.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join(" | ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal("owner", store.Current!.Index.Summaries.Single(s => s.Id == Lead).Relative);
        saved = await store.SaveAsync(Lead, Encoding.UTF8.GetBytes(ActorKit.Actor(Lead, "Lead", "person")), store.Current.GetDocument(Lead)!.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Null(store.Current!.Index.Summaries.Single(s => s.Id == Lead).Relative);
    }

    // ---- rules ----

    [Fact]
    public async Task MQ9603_and_MQ9601_report_a_malformed_relative_and_memberships_through_it()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write("model/actors/no-form.json", Relative("01JACT00000000000000000111", "NoForm", "person", "{}"));
            h.Write("model/actors/two-forms.json", Relative("01JACT00000000000000000112", "TwoForms", "person", $$"""{"owner":true,"path":[{{Ids(EProject)}}]}"""));
            h.Write("model/actors/within-only.json", Relative("01JACT00000000000000000113", "WithinOnly", "person", $$"""{"path":[{{Ids(EProject, ELead)}}],"within":[{{Ids(EProject)}}]}"""));
            h.Write("model/actors/of-person.json", Relative("01JACT00000000000000000114", "OfPerson", "role", $$"""{"membersOf":"{{Ann}}"}"""));
            h.Write("model/actors/of-relative.json", Relative("01JACT00000000000000000115", "OfRelative", "role", $$"""{"membersOf":"{{ProjectReviewer}}"}"""));
            h.Write("model/actors/member-relative.json", Relative("01JACT00000000000000000116", "MemberRelative", "person", """{"owner":true}""", Reviewer));
            h.Write("model/actors/system-relative.json", Relative("01JACT00000000000000000117", "SystemRelative", "system", """{"owner":true}"""));
            h.Write("model/actors/bea.json", ActorKit.Actor("01JACT00000000000000000118", "Bea", "person", ProjectReviewer));
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var diagnostics = await ActorKit.ValidateAsync(h, model);

        Assert.Equal(["member-relative.json:/memberOf", "no-form.json:/relative", "of-person.json:/relative/membersOf", "of-relative.json:/relative/membersOf",
            "system-relative.json:/relative", "two-forms.json:/relative", "within-only.json:/relative/within"], Hits(diagnostics, "MQ9603"));
        Assert.Equal(["bea.json:/memberOf/0"], Hits(diagnostics, "MQ9601"));
        Assert.Contains("(owner, path)", diagnostics.Single(d => d.Rule == "MQ9603" && (d.FilePath ?? "").EndsWith("two-forms.json", StringComparison.Ordinal)).Message, StringComparison.Ordinal);

        // Neither membership counts, so simulation never lets them through (decision 12).
        var membership = ActorMembership.Of(model);
        Assert.Empty(membership.MemberOf("01JACT00000000000000000116"));
        Assert.Empty(membership.MemberOf("01JACT00000000000000000118"));
        Assert.False(membership.Accepts(ProjectReviewer, "01JACT00000000000000000118"));
        Assert.Equal("none", model.Index.Summaries.Single(s => s.Id == "01JACT00000000000000000111").Relative);
        Assert.Equal("owner", model.Index.Summaries.Single(s => s.Id == "01JACT00000000000000000112").Relative);
    }

    [Fact]
    public async Task MQ9604_reports_a_use_that_cannot_be_worked_out_at_the_use()
    {
        const string Flow = "01JPRC00000000000000000102";
        const string ItemReview = "01JPRC00000000000000000103";
        const string Library = "01JPRC00000000000000000104";
        const string Shared = "01JPRX00000000000000000141";
        var (h, store) = await OpenAsync(h =>
        {
            h.Write("model/actors/item-only.json", Relative("01JACT00000000000000000121", "ItemOnly", "person", $$"""{"path":[{{Ids(ItemOnly)}}]}"""));
            h.Write("model/actors/watchers.json", Relative("01JACT00000000000000000122", "Watchers", "person", $$"""{"path":[{{Ids(EWatchers)}}]}"""));
            h.Write("model/actors/tagged.json", Relative("01JACT00000000000000000123", "Tagged", "person", $$"""{"path":[{{Ids(Tags)}}]}"""));
            h.Write("model/actors/past-attribute.json", Relative("01JACT00000000000000000124", "PastAttribute", "person", $$"""{"path":[{{Ids(OwnerLogin, ELead)}}]}"""));
            h.Write("model/actors/within-attribute.json", Relative("01JACT00000000000000000125", "WithinAttribute", "role", $$"""{"membersOf":"{{Reviewer}}","within":[{{Ids(OwnerLogin)}}]}"""));
            h.Write(ReviewFile, Process(Review, "Review", ReviewSubjects, Ids(Owner, "01JACT00000000000000000121"),
                Ids(ProjectReviewer, "01JACT00000000000000000122", "01JACT00000000000000000123", "01JACT00000000000000000124", "01JACT00000000000000000125")));
            // One subject: the item's own attribute resolves.
            h.Write("model/processes/item-review.json", Process(ItemReview, "ItemReview", $$""","subjects":[{"id":"01JPRX00000000000000000103","entity":"{{Item}}"}]""",
                Ids("01JACT00000000000000000121"), Ids(Lead)));
            // No subjects, and a library gate naming Lead used there: reported at the gatePolicy.
            h.Write("model/processes/shared.json", $$"""
                {"kind":"process","id":"{{Library}}","name":"Shared","package":"{{Pkg}}","use":"library",
                 "gates":[{"id":"{{Shared}}","name":"leadSigns","signers":[{{Ids(Lead)}}],"meanings":[{"id":"01JPRX00000000000000000142","name":"ok"}]}]}
                """);
            h.Write("model/processes/flow.json", Process(Flow, "Flow", "", Ids(Owner), Ids(Ann), use: "orchestration", gate: $$""","gatePolicy":"{{Shared}}" """));
        });
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);

        Assert.Equal(["flow.json:/events/0/actors/0", "flow.json:/transitions/1/gatePolicy",
            "review.json:/events/0/actors/1", "review.json:/events/1/actors/1", "review.json:/events/1/actors/2", "review.json:/events/1/actors/3", "review.json:/events/1/actors/4"],
            Hits(diagnostics, "MQ9604"));
        string Message(string file, string pointer) => diagnostics.Single(d => d.Rule == "MQ9604" && (d.FilePath ?? "").EndsWith(file, StringComparison.Ordinal) && d.JsonPointer == pointer).Message;
        Assert.Contains("has no subjects", Message("flow.json", "/events/0/actors/0"), StringComparison.Ordinal);
        Assert.Contains("library 'Shared'", Message("flow.json", "/transitions/1/gatePolicy"), StringComparison.Ordinal);
        Assert.Contains("do not all hold under one id", Message("review.json", "/events/0/actors/1"), StringComparison.Ordinal);
        Assert.Contains("leads to many objects", Message("review.json", "/events/1/actors/1"), StringComparison.Ordinal);
        Assert.Contains("holds many values", Message("review.json", "/events/1/actors/2"), StringComparison.Ordinal);
        Assert.Contains("goes on after attribute", Message("review.json", "/events/1/actors/3"), StringComparison.Ordinal);
        Assert.Contains("within", Message("review.json", "/events/1/actors/4"), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ9604" && (d.FilePath ?? "").EndsWith("shared.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_edit_elsewhere_that_breaks_a_path_is_refused_at_the_use()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // The project's lead becomes to-many: Lead's path no longer ends at one actor, which only Review's use can say.
        var text = Relation(ProjectLead, "ProjectLead", "01JEND00000000000000000103", Project, "led", ELead, Person, "lead", many: true);
        var result = await store.SaveAsync(ProjectLead, Encoding.UTF8.GetBytes(text), store.Current!.GetDocument(ProjectLead)!.Hash, ChangeSource.Editor, Ct);

        Assert.NotEqual(SaveOutcome.Saved, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ9604" && (d.FilePath ?? "").EndsWith("review.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_actor_edit_is_checked_where_only_a_library_gate_uses_it()
    {
        const string Shared = "01JPRX00000000000000000161";
        var gate = $$""","gatePolicy":"{{Shared}}" """;
        var (h, store) = await OpenAsync(h =>
        {
            h.Write("model/processes/shared.json", $$"""
                {"kind":"process","id":"01JPRC00000000000000000105","name":"Shared","package":"{{Pkg}}","use":"library",
                 "gates":[{"id":"{{Shared}}","name":"leadSigns","signers":[{{Ids(Lead)}}],"meanings":[{"id":"01JPRX00000000000000000162","name":"ok"}]}]}
                """);
            h.Write(ReviewFile, Process(Review, "Review", ReviewSubjects, Ids(Owner), Ids(ProjectReviewer), gate: gate));
        });
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain(await ActorKit.ValidateAsync(h, store.Current!), d => d.Rule == "MQ9604");

        // Lead's path now names Item's own attribute, which Document lacks: Review, which uses Lead only through the library's gate, says so.
        var result = await store.SaveAsync(Lead, Encoding.UTF8.GetBytes(Relative(Lead, "Lead", "person", $$"""{"path":[{{Ids(ItemOnly)}}]}""")),
            store.Current!.GetDocument(Lead)!.Hash, ChangeSource.Editor, Ct);

        Assert.NotEqual(SaveOutcome.Saved, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ9604" && (d.FilePath ?? "").EndsWith("review.json", StringComparison.Ordinal) && d.JsonPointer == "/transitions/1/gatePolicy");
    }

    [Fact]
    public async Task MQ9606_reports_an_owner_that_holds_no_single_actor()
    {
        var (h, store) = await OpenAsync(h => h.Write(ReviewFile, Process(Review, "Review", $$"""
            ,"subjects":[{"id":"{{ItemEntry}}","entity":"{{Item}}","owner":"{{Tags}}"},{"id":"{{DocumentEntry}}","entity":"{{Document}}","owner":"{{EWatchers}}"},
              {"id":"01JPRX00000000000000000105","entity":"{{Project}}","owner":"{{OwnerLogin}}"},{"id":"01JPRX00000000000000000106","entity":"{{Person}}"}]
            """, Ids(Owner), Ids(Ann))));
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);

        Assert.Equal(["review.json:/subjects/0/owner", "review.json:/subjects/1/owner", "review.json:/subjects/2/owner"], Hits(diagnostics, "MQ9606"));
        var resolved = ResolutionKit.Resolve(store.Current!);
        Assert.All(resolved.Processes.Single(p => p.Id == Review).Subjects, s => Assert.True(s.OwnerAttribute is null && s.OwnerEnd is null));
        Assert.All(Record(resolved, Review)["subjects"]!.AsArray(), e => Assert.False(e!.AsObject().ContainsKey("owner")));
    }

    [Fact]
    public async Task MQ9105_and_MQ9106_follow_actors_from_the_object()
    {
        // The gate's signer Lead is not among approve's actors (ProjectReviewer), which only the object can settle; Reviewer is named only
        // as ProjectReviewer's membersOf.
        var gate = $$""","gate":{"id":"01JPRX00000000000000000151","name":"leadSigns","signers":[{{Ids(Lead, Ann)}}],"meanings":[{"id":"01JPRX00000000000000000152","name":"ok"}]}""";
        var (h, store) = await OpenAsync(h => h.Write(ReviewFile, Process(Review, "Review", ReviewSubjects, Ids(Owner), Ids(ProjectReviewer), gate: gate)));
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);

        Assert.Empty(Hits(diagnostics, "MQ9105"));
        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ9106" && ((d.FilePath ?? "").EndsWith("reviewer.json", StringComparison.Ordinal) || (d.FilePath ?? "").EndsWith("ann.json", StringComparison.Ordinal)));
    }

    // ---- simulation and the object ----

    [Fact]
    public async Task Simulation_accepts_an_actor_from_the_object_only_when_the_step_names_it()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);

        List<StepTrace> Run(string approver)
        {
            var interpreter = new StatechartInterpreter(runtime.Chart(Review)!, runtime.Options("test"));
            return [interpreter.Start(new Dictionary<string, JsonElement>(StringComparer.Ordinal)), interpreter.Step(Step(SubmitEvent, Owner)), interpreter.Step(Step(ApproveEvent, approver))];
        }

        Assert.True(Run(ProjectReviewer)[1].Accepted);
        Assert.True(Run(ProjectReviewer)[2].Accepted, Run(ProjectReviewer)[2].Refusal);
        // The strict rule: a plain Reviewer, and a member of Reviewer, are not a reviewer on the project (decision 13).
        Assert.Equal(Refusals.Actor, Run(Reviewer)[2].Refusal);
        Assert.Equal(Refusals.Actor, Run(Ann)[2].Refusal);
    }

    private static ScenarioStep Step(string @event, string actor) => new()
    {
        Id = "01JSTP00000000000000000901", Input = StepInput.Event, Event = @event, Actor = actor, Payload = ImmutableDictionary<string, JsonElement>.Empty,
    };

    [Fact]
    public async Task Reach_reads_each_form_on_the_scenario_objects()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var review = model.Get<Process>(Review)!;
        ScenarioObject Obj(string id, string entity, string? login = null, params (string End, string To)[] links) => new()
        {
            Id = id, Name = "o" + id[^3..], Entity = entity,
            Values = login is null ? ImmutableDictionary<string, JsonElement>.Empty : ImmutableDictionary<string, JsonElement>.Empty.Add(OwnerLogin, JsonSerializer.SerializeToElement(login)),
            Links = [.. links.Select(l => new ScenarioLink { End = l.End, Object = l.To })],
        };
        const string I = "01JXBJ00000000000000000101", D = "01JXBJ00000000000000000102", Apollo = "01JXBJ00000000000000000103", Pat = "01JXBJ00000000000000000104";
        ScenarioObject[] objects = [Obj(I, Item, "bea", (EProject, Apollo)), Obj(D, Document, null, (EAuthor, Pat), (EProject, Apollo)), Obj(Apollo, Project, null, (ELead, Pat)), Obj(Pat, Person)];
        RelativeTarget? Reach(string subject, string actor, Process? process = null) => RelativeActors.Reach(model, process ?? review, objects, subject, model.Get<Actor>(actor)!);

        Assert.Equal("bea", Reach(I, Owner)!.Value!.Value.GetString());
        Assert.Equal(Pat, Reach(D, Owner)!.Object);
        Assert.Equal(Pat, Reach(I, Lead)!.Object);
        Assert.Equal(Apollo, Reach(D, ProjectReviewer)!.Object);
        Assert.Null(Reach(Apollo, Lead)); // the project has no project link
        Assert.Null(Reach(I, Ann));
        // A subject entry without an owner field: the team's system holds the owner.
        var plain = review with { SubjectList = [new ProcessSubject { Id = ItemEntry, Entity = Item }] };
        Assert.Null(Reach(I, Owner, plain));
    }

    // ---- references and deletes ----

    [Fact]
    public async Task Deleting_what_a_path_or_membersOf_names_refuses_remove_references_unless_the_actor_goes()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        Assert.Contains(model.ReferencesTo(ELead), r => r.FromElementId == Lead && r.JsonPointer == "/relative/path/1");
        Assert.Contains(model.ReferencesTo(Reviewer), r => r.FromElementId == ProjectReviewer && r.JsonPointer == "/relative/membersOf");
        Assert.Contains(model.ReferencesTo(EAuthor), r => r.FromElementId == Review && r.JsonPointer == "/subjects/1/owner");

        var refused = await store.GetDeletePlanAsync([ProjectLead], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Equal("/relative/path/1", Assert.Single(refused.Refused, r => r.Id == Lead).Pointer);
        var both = await store.GetDeletePlanAsync([ProjectLead, Lead], DeleteResolution.RemoveReferences, Ct);
        Assert.True(both.Outcome == SaveOutcome.Saved, string.Join(" | ", both.Refused.Select(r => r.Rule + " " + r.Why)));
        var cascade = await store.GetDeletePlanAsync([ProjectLead], DeleteResolution.DeleteDependents, Ct);
        Assert.True(cascade.Outcome == SaveOutcome.Saved, string.Join(" | ", cascade.Refused.Select(r => r.Rule + " " + r.Why)));
        Assert.Contains(cascade.Deletes, d => d.Id == Lead);

        var role = await store.GetDeletePlanAsync([Reviewer], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal("/relative/membersOf", Assert.Single(role.Refused, r => r.Id == ProjectReviewer).Pointer);
        Assert.Contains((await store.GetDeletePlanAsync([Reviewer], DeleteResolution.DeleteDependents, Ct)).Deletes, d => d.Id == ProjectReviewer);

        // A subject's owner is optional: remove-references clears it.
        var hash = store.Current!.GetDocument(DocumentAuthor)!.Hash;
        var removed = await store.DeleteAsync(DocumentAuthor, hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.True(removed.Outcome == SaveOutcome.Saved, string.Join(" | ", removed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var entry = store.Current!.Get<Process>(Review)!.Subjects[1];
        Assert.Equal((Document, (string?)null), (entry.Entity, entry.Owner));
        Assert.DoesNotContain("owner", JsonNode.Parse(h.Read(ReviewFile))!["subjects"]![1]!.AsObject().Select(p => p.Key));
    }
}
