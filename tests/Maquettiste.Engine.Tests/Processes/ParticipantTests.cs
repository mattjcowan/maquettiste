using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using R = Maquettiste.Engine.Tests.Processes.RelativeActorTests;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// A scenario's participants (actors-design.md decisions 14 to 17) over <see cref="RelativeActorTests"/>' invented model: a scenario of
/// Review whose item (owner login bea) is on project apollo, led by pat; zeus is another project. Ann holds Reviewer within apollo, bea
/// is the owner by her identity, carl holds Reviewer within zeus, dan stands for the person Ann (a member of Reviewer), and pat's record
/// is the pat object.
/// </summary>
public sealed class ParticipantTests
{
    private const string Scn = "01JSCN00000000000000000101";
    private const string OItem = "01JSBJ00000000000000000101";
    private const string OApollo = "01JSBJ00000000000000000102";
    private const string OZeus = "01JSBJ00000000000000000103";
    private const string OPat = "01JSBJ00000000000000000104";
    private const string PAnn = "01JPTC00000000000000000101";
    private const string PBea = "01JPTC00000000000000000102";
    private const string PCarl = "01JPTC00000000000000000103";
    private const string PDan = "01JPTC00000000000000000104";
    private const string PPat = "01JPTC00000000000000000105";
    private const string HAnn = "01JHDG00000000000000000101";
    private const string HCarl = "01JHDG00000000000000000102";
    private const string ScenarioFile = "model/scenarios/review/flow.json";

    private static CancellationToken Ct => ActorKit.Ct;

    private const string Objects = $$"""
        "objects":[
          {"id":"{{OItem}}","name":"item","entity":"{{R.Item}}","values":{"{{R.OwnerLogin}}":"bea"},"links":[{"end":"{{R.EProject}}","object":"{{OApollo}}"}]{{"{{ITEM_OWNER}}"}}},
          {"id":"{{OApollo}}","name":"apollo","entity":"{{R.Project}}","links":[{"end":"{{R.ELead}}","object":"{{OPat}}"}]},
          {"id":"{{OZeus}}","name":"zeus","entity":"{{R.Project}}"},
          {"id":"{{OPat}}","name":"pat","entity":"{{R.Person}}"}],
        "subject":"{{OItem}}"
        """;

    private const string People = $$"""
        [{"id":"{{PAnn}}","name":"ann","holds":[{"id":"{{HAnn}}","actor":"{{R.Reviewer}}","within":"{{OApollo}}"}]},
         {"id":"{{PBea}}","name":"bea"},
         {"id":"{{PCarl}}","name":"carl","holds":[{"id":"{{HCarl}}","actor":"{{R.Reviewer}}","within":"{{OZeus}}"}]},
         {"id":"{{PDan}}","name":"dan","actor":"{{R.Ann}}"},
         {"id":"{{PPat}}","name":"pat","identity":7,"object":"{{OPat}}"}]
        """;

    private const string Flow = $$"""
        [{"id":"01JSTP00000000000000000101","event":"{{R.SubmitEvent}}","participant":"{{PBea}}","expect":{"accepted":true} },
         {"id":"01JSTP00000000000000000102","event":"{{R.ApproveEvent}}","participant":"{{PCarl}}","expect":{"accepted":false} },
         {"id":"01JSTP00000000000000000103","event":"{{R.ApproveEvent}}","participant":"{{PAnn}}","expect":{"accepted":true} }]
        """;

    private static string Scenario(string participants = People, string steps = Flow, string itemOwner = "", string outcome = "final") =>
        $$"""{"kind":"scenario","id":"{{Scn}}","name":"flow","process":"{{R.Review}}",{{Objects.Replace("{{ITEM_OWNER}}", itemOwner, StringComparison.Ordinal)}},"participants":{{participants}},"steps":{{steps}},"outcome":"{{outcome}}"}""";

    private static Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? scenario = null, Action<LoaderHarness>? prepare = null) =>
        R.OpenAsync(h =>
        {
            h.Write(ScenarioFile, scenario ?? Scenario());
            prepare?.Invoke(h);
        });

    private static Scenario Parse(string text) => JsonSerializer.Deserialize<Scenario>(text, EngineJson.Options)!;

    private static ScenarioReplay Replay(ModelSnapshot model, string text)
    {
        using var runtime = new ProcessRuntime(model, 1, Ct);
        return ScenarioReplayer.Replay(Parse(text), runtime)!;
    }

    private static string Pointers(ScenarioReplay replay, string rule) =>
        string.Join(",", replay.Diagnostics.Where(d => d.Rule == rule).Select(d => d.JsonPointer));

    // ---- no change ----

    [Fact]
    public async Task A_project_without_participants_is_unchanged()
    {
        var (h, store) = await ActorKit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        var diagnostics = await ActorKit.ValidateAsync(h, model);
        Assert.DoesNotContain(diagnostics, d => d.Rule is "MQ9301" or "MQ9305");
        var resolved = ResolutionKit.Resolve(model);
        foreach (var scenario in resolved.Scenarios)
        {
            Assert.Empty(scenario.Participants);
            Assert.All(scenario.Steps, s => Assert.Same(s.Actor, s.ActsAs));
            Assert.All(scenario.Steps, s => Assert.Null(s.Participant));
            Assert.All(scenario.Steps, s => Assert.DoesNotContain("k:actor", s.Dependencies));
            var record = R.Record(resolved, scenario.Id);
            Assert.False(record.ContainsKey("participants"));
            Assert.All(record["steps"]!.AsArray(), s => Assert.False(s!.AsObject().ContainsKey("participant") || s.AsObject().ContainsKey("actsAs")));
        }

        // A trace echoes its input with every member written, except participant when the input has none.
        var call = await store.SimulateProcessAsync(ActorKit.P, new SimulateRequest { Scenario = "01JSCN00000000000000000001" }, Ct);
        Assert.Equal(ProcessCallStatus.Ok, call.Status);
        var trace = JsonSerializer.SerializeToNode(call.Value!.Trace, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsArray();
        var inputs = trace.Select(t => t!["input"]).OfType<JsonObject>().ToList();
        Assert.NotEmpty(inputs);
        Assert.All(inputs, i => Assert.True(i.ContainsKey("actor") && !i.ContainsKey("participant")));
    }

    // ---- satisfaction and the actor a step acts as ----

    [Fact]
    public async Task Each_satisfaction_case_and_the_actor_each_step_acts_as()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var scenario = model.Get<Scenario>(Scn)!;
        var cast = ScenarioCast.Of(scenario);
        var review = model.Get<Process>(R.Review)!;
        bool Satisfies(string participant, string actor) => Participants.Satisfies(model, review, cast, cast.Participant(participant)!, actor);

        // The owner by identity; a path by the participant's object; members of Reviewer within the item's project only (decision 13).
        Assert.True(Satisfies(PBea, R.Owner));
        Assert.False(Satisfies(PAnn, R.Owner));
        Assert.True(Satisfies(PAnn, R.ProjectReviewer));
        Assert.False(Satisfies(PAnn, R.Reviewer));
        Assert.False(Satisfies(PCarl, R.ProjectReviewer));
        Assert.True(Satisfies(PPat, R.Lead));
        Assert.False(Satisfies(PBea, R.Lead));
        // A plain actor through the participant's actor and its memberships, never a context.
        Assert.True(Satisfies(PDan, R.Ann));
        Assert.True(Satisfies(PDan, R.Reviewer));
        Assert.False(Satisfies(PDan, R.ProjectReviewer));

        var diagnostics = await ActorKit.ValidateAsync(h, model);
        Assert.DoesNotContain(diagnostics, d => d.Rule != "MQ1003" && (d.FilePath ?? "").EndsWith("flow.json", StringComparison.Ordinal));
        var replay = Replay(model, h.Read(ScenarioFile));
        Assert.True(replay.Passed, string.Join(" | ", replay.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(new string?[] { R.Owner, null, R.ProjectReviewer }, replay.Steps.Select(t => t.Input!.Actor));

        // The resolved model and the records give the actor each step acts as.
        var resolved = ResolutionKit.Resolve(model);
        var r = resolved.Scenarios.Single(s => s.Id == Scn);
        Assert.Equal(["ann", "bea", "carl", "dan", "pat"], r.Participants.Select(p => p.Name));
        Assert.Equal(new object[] { "bea", 7L }, r.Participants.Where(p => p.Name is "bea" or "pat").Select(p => p.Identity));
        Assert.Equal(OApollo, r.Participants[0].Holds.Single().Within!.Id);
        Assert.Equal(new string?[] { R.Owner, null, R.ProjectReviewer }, r.Steps.Select(s => s.ActsAs?.Id));
        Assert.Equal(new[] { PBea, PCarl, PAnn }, r.Steps.Select(s => s.Participant!.Id));
        Assert.All(r.Steps, s => Assert.Contains("k:actor", s.Dependencies));
        var record = R.Record(resolved, Scn);
        var steps = record["steps"]!.AsArray();
        Assert.Equal(R.Owner, (string?)steps[0]!["actsAs"]);
        Assert.Equal(PCarl, (string?)steps[1]!["participant"]);
        Assert.False(steps[1]!.AsObject().ContainsKey("actsAs"));
        var participants = record["participants"]!.AsArray();
        Assert.Equal(7, (int)participants[4]!["identity"]!);
        Assert.Equal("bea", (string?)participants[1]!["identity"]);
        Assert.Equal(OZeus, (string?)participants[2]!["holds"]![0]!["within"]);
        Assert.False(participants[1]!.AsObject().ContainsKey("actor"));
    }

    [Fact]
    public async Task The_first_list_picks_in_file_order_and_a_gate_takes_the_signers_and_the_identity()
    {
        // Approve is gated: its signers are Reviewer then ProjectReviewer, and its event takes either.
        const string Gate = $$""","gate":{"id":"01JPRC00000000000000000941","name":"approval","signers":["{{R.Reviewer}}","{{R.ProjectReviewer}}"],"meanings":[{"id":"01JPRC00000000000000000942","name":"ok"}]}""";
        const string Both = $$"""
            [{"id":"{{PAnn}}","name":"ann","identity":"ann.k","holds":[{"id":"{{HAnn}}","actor":"{{R.Reviewer}}","within":"{{OApollo}}"}]},
             {"id":"{{PBea}}","name":"bea"},
             {"id":"{{PCarl}}","name":"carl","holds":[{"id":"{{HCarl}}","actor":"{{R.Reviewer}}"}]},
             {"id":"{{PPat}}","name":"pat","object":"{{OPat}}","holds":[{"id":"01JHDG00000000000000000103","actor":"{{R.Reviewer}}","within":"{{OApollo}}"}]}]
            """;
        const string SubmitByBea = $$"""[{"id":"01JSTP00000000000000000101","event":"{{R.SubmitEvent}}","participant":"{{PBea}}"}]""";
        var (h, store) = await OpenAsync(Scenario(Both, SubmitByBea, outcome: "active"), harness =>
            harness.Write(R.ReviewFile, R.Process(R.Review, "Review", R.ReviewSubjects, R.Ids(R.Owner), R.Ids(R.Reviewer, R.ProjectReviewer, R.Lead), gate: Gate)));
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var scenario = model.Get<Scenario>(Scn)!;
        using var runtime = new ProcessRuntime(model, 1, Ct);
        var chart = runtime.Chart(R.Review)!;
        string? ActsAs(string participant) => Participants.ActsAs(model, chart, ScenarioCast.Of(scenario),
            new ScenarioStep { Id = "x", Event = R.ApproveEvent, Participant = participant });

        Assert.Equal(R.ProjectReviewer, ActsAs(PAnn)); // the signers, not the event's actors
        Assert.Equal(R.Reviewer, ActsAs(PCarl));
        Assert.Equal(R.ProjectReviewer, ActsAs(PPat)); // satisfies ProjectReviewer and Lead: the first listed
        Assert.Null(ActsAs(PBea));
        Assert.Equal(R.Owner, Participants.ActsAs(model, chart, ScenarioCast.Of(scenario), new ScenarioStep { Id = "x", Event = R.SubmitEvent, Participant = PBea }));
        // A time step has no list: the participant's actor, here none.
        Assert.Null(Participants.ActsAs(model, chart, ScenarioCast.Of(scenario), new ScenarioStep { Id = "x", Input = StepInput.Time, After = "P1D", Participant = PAnn }));

        // Simulation by name: ann signs as ProjectReviewer under her identity.
        var call = await store.SimulateProcessAsync(R.Review, new SimulateRequest
        {
            Scenario = Scn,
            Steps = [JsonSerializer.SerializeToElement(new { @event = R.ApproveEvent, participant = "ann", meaning = "01JPRC00000000000000000942" })],
        }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
        var last = call.Value!.Trace[^1];
        Assert.True(last.Accepted, last.Refusal);
        Assert.Equal((PAnn, R.ProjectReviewer, "ann.k"), (last.Input!.Participant, last.Input.Actor, last.Input.Signer));
        Assert.Contains(last.Audit, a => a.Signer == "ann.k" && a.Actor == R.ProjectReviewer);
        Assert.True(call.Value.Final);
    }

    [Fact]
    public async Task An_owner_participant_counts_without_an_owner_field_and_MQ9305_beside_one()
    {
        var owned = $$""","owner":"{{PBea}}" """;
        var noOwnerField = $$""","subjects":[{"id":"{{R.ItemEntry}}","entity":"{{R.Item}}"},{"id":"{{R.DocumentEntry}}","entity":"{{R.Document}}"}]""";
        var (h, store) = await OpenAsync(Scenario(itemOwner: owned), harness =>
            harness.Write(R.ReviewFile, R.Process(R.Review, "Review", noOwnerField, R.Ids(R.Owner), R.Ids(R.ProjectReviewer, R.Lead))));
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var review = model.Get<Process>(R.Review)!;
        var cast = ScenarioCast.Of(model.Get<Scenario>(Scn)!);
        Assert.True(Participants.Satisfies(model, review, cast, cast.Participant(PBea)!, R.Owner));
        Assert.False(Participants.Satisfies(model, review, cast, cast.Participant(PDan)!, R.Owner));
        Assert.True(Replay(model, h.Read(ScenarioFile)).Passed);
        Assert.Equal(PBea, ResolutionKit.Resolve(model).Scenarios.Single(s => s.Id == Scn).Objects[0].Owner!.Id);

        // Without the object's owner, nobody is the owner, even with the identity an owner field would hold.
        var unowned = cast with { Objects = [.. cast.Objects.Select(o => o with { Owner = null })] };
        Assert.False(Participants.Satisfies(model, review, unowned, cast.Participant(PBea)!, R.Owner));

        // Beside the subject entry's owner field, the object's owner is MQ9305.
        var (h2, store2) = await OpenAsync(Scenario(itemOwner: owned));
        using var ___ = h2;
        await using var ____ = store2;
        Assert.Equal("/objects/0/owner", Pointers(Replay(store2.Current!, h2.Read(ScenarioFile)), "MQ9305"));
    }

    [Fact]
    public async Task The_actor_a_step_acts_as_keys_on_the_actors_files()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var before = store.Current!;
        var step = ResolutionKit.Resolve(before).Scenarios.Single(s => s.Id == Scn).Steps[2];
        Assert.Equal(R.ProjectReviewer, step.ActsAs!.Id);

        // An edit of Lead's file, an actor of the list the step's actor comes from, is an input of the step.
        var saved = await ActorKit.SaveActorAsync(store, R.Lead,
            $$"""{"kind":"actor","id":"{{R.Lead}}","name":"Lead","displayName":"Project lead","type":"person","relative":{"path":[{{R.Ids(R.EProject, R.ELead)}}] } }""");
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join(" | ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var after = store.Current!;
        Assert.Equal(R.ProjectReviewer, ResolutionKit.Resolve(after).Scenarios.Single(s => s.Id == Scn).Steps[2].ActsAs!.Id);

        static string Hash(ModelSnapshot model, IEnumerable<string> keys) => string.Join("|", keys.Select(k => k[..2] switch
        {
            "e:" => model.GetDocument(k[2..])?.DependencyHash ?? "absent",
            "r:" => model.ReferrersHash(k[2..]),
            "k:" => KindInfo.TryGet(k[2..], out var info) ? model.KindSetHash(info.Kind) : "absent",
            _ => k,
        }));
        Assert.NotEqual(Hash(before, step.Dependencies), Hash(after, step.Dependencies));
    }

    // ---- MQ9305 and MQ9301 ----

    [Fact]
    public async Task MQ9305_reports_inconsistent_participants_and_steps()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        string Problem(string participants, string? steps = null, string itemOwner = "") =>
            Pointers(Replay(model, Scenario(participants, steps ?? Flow, itemOwner)), "MQ9305");
        string One(string participant) => "[" + participant + "]";
        const string Submit = $$"""[{"id":"01JSTP00000000000000000101","event":"{{R.SubmitEvent}}","participant":"{{PBea}}"}]""";

        Assert.Equal("/participants/0/actor", Problem(One($$"""{"id":"{{PBea}}","name":"bea","actor":"{{R.Reviewer}}"}"""), Submit));
        Assert.Equal("/participants/0/actor", Problem(One($$"""{"id":"{{PBea}}","name":"bea","actor":"{{R.Owner}}"}"""), Submit));
        Assert.Equal("/participants/0/holds/0/actor", Problem(One($$"""{"id":"{{PBea}}","name":"bea","holds":[{"id":"{{HAnn}}","actor":"{{R.Ann}}"}]}"""), Submit));
        Assert.Equal("/participants/0/holds/0/actor", Problem(One($$"""{"id":"{{PBea}}","name":"bea","holds":[{"id":"{{HAnn}}","actor":"{{R.ProjectReviewer}}"}]}"""), Submit));
        Assert.Equal("/participants/0/holds/0/within", Problem(One($$"""{"id":"{{PBea}}","name":"bea","holds":[{"id":"{{HAnn}}","actor":"{{R.Reviewer}}","within":"{{PDan}}"}]}"""), Submit));
        Assert.Equal("/participants/0/object", Problem(One($$"""{"id":"{{PBea}}","name":"bea","object":"{{PDan}}"}"""), Submit));
        Assert.Equal("/objects/0/owner", Problem(People, Flow, $$""","owner":"{{OApollo}}" """));
        Assert.Equal("/steps/0/participant", Problem(People, $$"""[{"id":"01JSTP00000000000000000101","event":"{{R.SubmitEvent}}","participant":"{{OPat}}"}]"""));
        Assert.Equal("/steps/0/actor", Problem(People, $$"""[{"id":"01JSTP00000000000000000101","event":"{{R.SubmitEvent}}","participant":"{{PDan}}","actor":"{{R.Owner}}"}]"""));
        // With a participant, an actor the participant satisfies is accepted.
        Assert.Equal("", Problem(People, $$"""[{"id":"01JSTP00000000000000000101","event":"{{R.SubmitEvent}}","participant":"{{PBea}}","actor":"{{R.Owner}}"}]"""));
    }

    [Fact]
    public async Task MQ9301_names_the_participant_and_the_list_it_did_not_satisfy()
    {
        var steps = Flow.Replace("\"participant\":\"" + PCarl + "\",\"expect\":{\"accepted\":false}", "\"participant\":\"" + PCarl + "\",\"expect\":{\"accepted\":true}", StringComparison.Ordinal);
        var (h, store) = await OpenAsync(Scenario(steps: steps));
        using var _ = h;
        await using var __ = store;
        var replay = Replay(store.Current!, h.Read(ScenarioFile));
        Assert.Equal("MQ9301", replay.Failure!.Rule);
        Assert.Contains("participant 'carl' satisfies none of ProjectReviewer, Lead", replay.Failure.Message, StringComparison.Ordinal);
        Assert.Contains(replay.Diagnostics, d => d.Rule == "MQ9301" && d.Message.Contains("'carl'", StringComparison.Ordinal));
    }

    // ---- MQ9106 ----

    [Fact]
    public async Task MQ9106_counts_an_actor_a_participant_names()
    {
        const string Clerk = "01JACT00000000000000000106";
        var clerkOnly = $$"""[{"id":"{{PDan}}","name":"dan","actor":"{{Clerk}}"}]""";
        const string Unnamed = $$"""[{"id":"01JSTP00000000000000000101","event":"{{R.SubmitEvent}}"}]""";
        var (h, store) = await OpenAsync(Scenario(clerkOnly, Unnamed, outcome: "active"),
            harness => harness.Write("model/actors/sam.json", ActorKit.Actor(Clerk, "Sam", "person")));
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ9106" && d.ElementId == Clerk);

        // Dropping the participant makes Sam unused again, and the scoped check after the save says so.
        var saved = await store.SaveAsync(Scn, Encoding.UTF8.GetBytes(Scenario("[]", Unnamed, outcome: "active")),
            store.Current!.GetDocument(Scn)!.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ9106" && d.ElementId == Clerk);
    }

    [Fact]
    public async Task A_scenario_without_participants_brings_no_actor_into_its_save()
    {
        // Saving a scenario re-checks the actors (MQ9106) only while it has participants; one without them keeps its scope small.
        const string Unnamed = $$"""[{"id":"01JSTP00000000000000000101","event":"{{R.SubmitEvent}}"}]""";
        var (h, store) = await OpenAsync(Scenario("[]", Unnamed, outcome: "active"));
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var scope = new ValidationScope([Scn], IncludeReferrers: true);
        Assert.DoesNotContain(Maquettiste.Engine.Validation.ModelValidator.ProcessPeers(model, model.Get<Scenario>(Scn)!), d => d.Element is Actor);
        Assert.DoesNotContain(Maquettiste.Engine.Validation.ModelValidator.TargetIds(model, scope), id => model.Get<Actor>(id) is not null);

        // With participants it reaches every actor, since any of them may be the one a participant names.
        var (h2, withPeople) = await OpenAsync();
        using var ___ = h2;
        await using var ____ = withPeople;
        var full = withPeople.Current!;
        var targets = Maquettiste.Engine.Validation.ModelValidator.TargetIds(full, scope);
        Assert.All(full.All<Actor>(), a => Assert.Contains(a.Id, targets));
    }

    // ---- parts, references and deletes ----

    [Fact]
    public async Task Participants_are_parts_with_name_forms_references_and_deletes()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        Assert.EndsWith("Review.flow.ann", model.Names("participant").Get(PAnn)!.Longest, StringComparison.Ordinal);
        Assert.Contains(model.ReferencesTo(R.Ann), r => r.FromElementId == Scn && r.JsonPointer == "/participants/3/actor");
        Assert.Contains(model.ReferencesTo(R.Reviewer), r => r.FromElementId == Scn && r.JsonPointer == "/participants/0/holds/0/actor");
        Assert.Contains(model.ReferencesTo(OApollo), r => r.FromElementId == Scn && r.JsonPointer == "/participants/0/holds/0/within");
        Assert.Contains(model.ReferencesTo(PBea), r => r.FromElementId == Scn && r.JsonPointer == "/steps/0/participant");

        // An actor a participant stands for: remove-references clears the participant's actor.
        var hash = model.GetDocument(R.Ann)!.Hash;
        var deleted = await store.DeleteAsync(R.Ann, hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.True(deleted.Outcome == SaveOutcome.Saved, string.Join(" | ", deleted.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var dan = JsonNode.Parse(h.Read(ScenarioFile))!["participants"]![3]!.AsObject();
        Assert.Equal(["id", "name"], dan.Select(p => p.Key));

        // A role a holding names: the holding goes (Reviewer goes with ProjectReviewer, whose membersOf names it).
        var plan = await store.GetDeletePlanAsync([R.Reviewer, R.ProjectReviewer], DeleteResolution.RemoveReferences, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, string.Join(" | ", plan.Refused.Select(r => r.Why)));
        Assert.Contains(plan.Removes, r => r.Id == Scn && r.Pointer == "/participants/0/holds/0");
        Assert.Contains(plan.Removes, r => r.Id == Scn && r.Pointer == "/participants/2/holds/0");

        // An object a holding counts within: the holding goes with it, and a participant's object and an object's owner are cleared.
        var objectGone = await store.GetDeletePlanAsync([R.Person, R.ProjectLead, R.DocumentAuthor, "01JRXN00000000000000000103", R.Lead], DeleteResolution.RemoveReferences, Ct);
        Assert.True(objectGone.Outcome == SaveOutcome.Saved, string.Join(" | ", objectGone.Refused.Select(r => r.Pointer + " " + r.Why)));
        Assert.Contains(objectGone.Removes, r => r.Id == Scn && r.Pointer == "/objects/3");
        Assert.Contains(objectGone.Clears, r => r.Id == Scn && r.Pointer == "/participants/4/object");
        var projects = await store.GetDeletePlanAsync([R.Project, R.WorkProject, R.ProjectLead, R.Lead, R.ProjectReviewer], DeleteResolution.RemoveReferences, Ct);
        Assert.True(projects.Outcome == SaveOutcome.Saved, string.Join(" | ", projects.Refused.Select(r => r.Pointer + " " + r.Why)));
        Assert.Contains(projects.Removes, r => r.Id == Scn && r.Pointer == "/participants/0/holds/0");
        Assert.DoesNotContain(projects.Clears, r => r.Id == Scn && r.Pointer.EndsWith("/within", StringComparison.Ordinal));
    }

    // ---- simulate and record ----

    [Fact]
    public async Task Recording_carries_participants_under_new_ids_that_steps_owners_and_holdings_follow()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var scenario = JsonNode.Parse(h.Read(ScenarioFile))!.AsObject();
        JsonElement[] Of(string key) => [.. scenario[key]!.AsArray().Select(n => JsonSerializer.SerializeToElement(n))];
        var steps = Of("steps").Select(s =>
        {
            var node = JsonNode.Parse(s.GetRawText())!.AsObject();
            if ((string?)node["participant"] == PAnn)
                node["participant"] = "ann"; // by name
            return JsonSerializer.SerializeToElement(node);
        }).ToList();

        var call = await store.RecordScenarioAsync(R.Review, new RecordScenarioRequest
        {
            Name = "again", Objects = Of("objects"), Subject = OItem, Participants = Of("participants"), Steps = steps,
        }, false, ChangeSource.Editor, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        var saved = store.Current!.Get<Scenario>(call.Value!.Id)!;
        Assert.Equal(["ann", "bea", "carl", "dan", "pat"], saved.Participants.Select(p => p.Name));
        Assert.DoesNotContain(saved.Participants, p => p.Id is PAnn or PBea or PCarl or PDan or PPat);
        Assert.DoesNotContain(saved.Participants.SelectMany(p => p.Holds), x => x.Id is HAnn or HCarl);
        var objects = saved.Objects.ToDictionary(o => o.Name, o => o.Id);
        var people = saved.Participants.ToDictionary(p => p.Name, p => p.Id);
        Assert.Equal(objects["apollo"], saved.Participants[0].Holds[0].Within);
        Assert.Equal(objects["pat"], saved.Participants[4].Object);
        Assert.Equal([people["bea"], people["carl"], people["ann"]], saved.Steps.Select(s => s.Participant));
        Assert.Equal([true, false, true], saved.Steps.Select(s => s.Expect!.Accepted));
        Assert.Equal(7, saved.Participants[4].Identity!.Value.GetInt32());

        // A step naming no participant of the run is refused.
        var bad = await store.RecordScenarioAsync(R.Review, new RecordScenarioRequest
        {
            Name = "bad", Objects = Of("objects"), Subject = OItem, Participants = Of("participants"),
            Steps = [JsonSerializer.SerializeToElement(new { @event = R.SubmitEvent, participant = "nobody" })],
        }, true, ChangeSource.Editor, Ct);
        Assert.Equal(ProcessCallStatus.BadRequest, bad.Status);
    }

    [Fact]
    public async Task Simulation_takes_the_scenarios_participants_or_its_own()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var call = await store.SimulateProcessAsync(R.Review, new SimulateRequest { Scenario = Scn }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
        Assert.Equal([true, false, true], call.Value!.Trace.Skip(1).Select(t => t.Accepted));
        Assert.Equal(Refusals.Actor, call.Value.Trace[2].Refusal);
        Assert.True(call.Value.Final);

        // The request's own participants replace the scenario's: carl now holds Reviewer within apollo.
        var people = JsonNode.Parse(People)!.AsArray();
        people[2]!["holds"]![0]!["within"] = OApollo;
        var own = await store.SimulateProcessAsync(R.Review, new SimulateRequest
        {
            Scenario = Scn, Participants = [.. people.Select(p => JsonSerializer.SerializeToElement(p))],
        }, Ct);
        Assert.True(own.Status == ProcessCallStatus.Ok, own.Detail);
        Assert.True(own.Value!.Trace[2].Accepted, own.Value.Trace[2].Refusal);
        Assert.Equal(R.ProjectReviewer, own.Value.Trace[2].Input!.Actor);
    }
}
