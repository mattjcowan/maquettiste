using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// What the actors tests share (actors-design.md wave 1): the <c>process-basics</c> fixture in a store, with actors added, and a run of
/// its lifecycle through the interpreter. Clerk is a person who submits; Manager and Finance are roles that approve and sign the
/// two-signature gate, Finance required; Manager completes the escalation's human task.
/// </summary>
internal static class ActorKit
{
    public const string P = "01JPRC00000000000000000001";
    public const string Clerk = "01JACT00000000000000000001";
    public const string Manager = "01JACT00000000000000000002";
    public const string Finance = "01JACT00000000000000000003";
    public const string Submit = "01JPRX00000000000000000001";
    public const string Approve = "01JPRX00000000000000000002";
    public const string Ship = "01JPRX00000000000000000003";
    public const string ManagerReview = "01JPRX00000000000000000031";
    public const string AsManager = "01JPRX00000000000000000051";
    public const string AsFinance = "01JPRX00000000000000000052";
    public const string Pending = "01JSTA00000000000000000004";
    public const string Escalated = "01JSTA00000000000000000005";
    public const string Packing = "01JSTA00000000000000000009";
    public const string Invoicing = "01JSTA00000000000000000012";

    // Added actors.
    public const string Pat = "01JACT00000000000000000020";
    public const string Fay = "01JACT00000000000000000021";
    public const string Seniors = "01JACT00000000000000000022";
    public const string Auditor = "01JACT00000000000000000023";
    public const string Plant = "01JACT00000000000000000024";
    public const string Auto = "01JACT00000000000000000025";
    public const string Auto2 = "01JACT00000000000000000026";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An actor file's text.</summary>
    public static string Actor(string id, string name, string type, params string[] memberOf) =>
        JsonSerializer.Serialize(memberOf.Length == 0
            ? new Dictionary<string, object> { ["kind"] = "actor", ["id"] = id, ["name"] = name, ["type"] = type }
            : new Dictionary<string, object> { ["kind"] = "actor", ["id"] = id, ["name"] = name, ["type"] = type, ["memberOf"] = memberOf });

    public static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    public static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(LoaderHarness harness, ModelSnapshot model) =>
        (await EngineServices.Create(harness.Options).Validator.ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics;

    /// <summary>Saves an actor's new text over its current file.</summary>
    public static Task<SaveResult> SaveActorAsync(ModelStore store, string id, string text) =>
        store.SaveAsync(id, Encoding.UTF8.GetBytes(text), store.Current!.GetDocument(id)!.Hash, ChangeSource.Editor, Ct);

    public static ScenarioStep Step(string? @event = null, string? actor = null, string? signer = null, string? meaning = null, StepInput input = StepInput.Event,
        string? after = null, string? invoke = null) => new()
    {
        Id = "01JSTP00000000000000000900", Input = input, Event = @event, Actor = actor, Signer = signer, Meaning = meaning, After = after, Invoke = invoke,
        Reason = signer is null ? null : "ok",
        Payload = @event == Submit ? ImmutableDictionary<string, JsonElement>.Empty.Add("01JATT00000000000000000021", JsonSerializer.SerializeToElement("rush"))
            : ImmutableDictionary<string, JsonElement>.Empty,
    };

    /// <summary>Runs the lifecycle from its initial entry (amount 500, so it waits in Pending after submit).</summary>
    public static List<StepTrace> Run(ModelSnapshot model, params ScenarioStep[] steps)
    {
        using var runtime = new ProcessRuntime(model, 1, Ct);
        var interpreter = new StatechartInterpreter(runtime.Chart(P)!, runtime.Options("test"));
        var context = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["01JATT00000000000000000011"] = JsonSerializer.SerializeToElement(500),
            ["01JATT00000000000000000012"] = JsonSerializer.SerializeToElement(false),
        };
        var traces = new List<StepTrace> { interpreter.Start(context) };
        traces.AddRange(steps.Select(interpreter.Step));
        return traces;
    }
}

/// <summary>
/// Membership (actors-design.md decisions 3 to 6): closures at any depth, simulation and replay by membership, deletes, the index row and
/// the resolved model's lists with their dependency keys.
/// </summary>
public sealed class ActorMembershipTests
{
    private static CancellationToken Ct => ActorKit.Ct;

    [Fact]
    public async Task Membership_reaches_any_depth_by_role_group_and_organization()
    {
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/plant.json", ActorKit.Actor(ActorKit.Plant, "Plant", "organization"));
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Manager, ActorKit.Plant));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors, ActorKit.Seniors));
            harness.Write("model/actors/fay.json", ActorKit.Actor(ActorKit.Fay, "Fay", "person", ActorKit.Finance, ActorKit.Clerk));
        });
        using var _ = h;
        await using var __ = store;
        var membership = ActorMembership.Of(store.Current!);

        Assert.True(membership.Any);
        Assert.Equal([ActorKit.Seniors], membership.MemberOf(ActorKit.Pat)); // listed twice, counted once (MQ9601)
        Assert.Equal([ActorKit.Finance], membership.MemberOf(ActorKit.Fay)); // Clerk is a person: not counted (MQ9601)
        Assert.True(membership.Accepts(ActorKit.Manager, ActorKit.Pat));
        Assert.True(membership.Accepts(ActorKit.Plant, ActorKit.Pat));
        Assert.False(membership.Accepts(ActorKit.Finance, ActorKit.Pat));
        Assert.False(membership.Accepts(ActorKit.Pat, ActorKit.Manager));

        var resolved = ResolutionKit.Resolve(store.Current!);
        var actors = resolved.Actors.ToDictionary(a => a.Id, StringComparer.Ordinal);
        Assert.Equal(["Seniors"], actors[ActorKit.Pat].MemberOf.Select(a => a.Name));
        Assert.Equal(["Manager", "Plant", "Seniors"], actors[ActorKit.Pat].AllMemberOf.Select(a => a.Name));
        Assert.Equal(["Pat"], actors[ActorKit.Seniors].Members.Select(a => a.Name));
        Assert.Equal(["Pat", "Seniors"], actors[ActorKit.Manager].AllMembers.Select(a => a.Name));
        Assert.Equal(["Seniors"], actors[ActorKit.Manager].Members.Select(a => a.Name));
        Assert.Empty(actors[ActorKit.Clerk].Members);
        Assert.Empty(actors[ActorKit.Clerk].AllMemberOf);

        var record = (ActorRecord)ResolvedRecords.Entries(resolved, "actors", null).Single(e => e.Id == ActorKit.Pat).Project();
        Assert.Equal([ActorKit.Seniors], record.MemberOf!); // as resolved: each counted entry once
    }

    [Fact]
    public async Task A_cycle_never_puts_an_actor_among_its_own_members()
    {
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Plant));
            harness.Write("model/actors/plant.json", ActorKit.Actor(ActorKit.Plant, "Plant", "organization", ActorKit.Seniors));
        });
        using var _ = h;
        await using var __ = store;
        var resolved = ResolutionKit.Resolve(store.Current!);
        var seniors = resolved.Actors.Single(a => a.Id == ActorKit.Seniors);

        Assert.Equal(["Plant"], seniors.AllMemberOf.Select(a => a.Name));
        Assert.Equal(["Plant"], seniors.AllMembers.Select(a => a.Name));
    }

    [Fact]
    public async Task Events_gates_required_actors_and_human_tasks_accept_members()
    {
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Manager));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors));
            harness.Write("model/actors/fay.json", ActorKit.Actor(ActorKit.Fay, "Fay", "person", ActorKit.Finance));
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        var signed = ActorKit.Run(model,
            ActorKit.Step(ActorKit.Submit, ActorKit.Clerk),
            ActorKit.Step(ActorKit.Approve, ActorKit.Pat, "ann", ActorKit.AsManager),
            ActorKit.Step(ActorKit.Approve, ActorKit.Fay, "bob", ActorKit.AsFinance));
        Assert.All(signed, t => Assert.True(t.Accepted, t.Refusal));
        Assert.Equal([ActorKit.Packing, ActorKit.Invoicing], signed[^1].Configuration); // Fay's signature counts for the required Finance

        var escalated = ActorKit.Run(model,
            ActorKit.Step(ActorKit.Submit, ActorKit.Clerk),
            ActorKit.Step(input: StepInput.Time, after: "P2D"),
            ActorKit.Step(input: StepInput.InvokeDone, invoke: ActorKit.ManagerReview, actor: ActorKit.Pat));
        Assert.Equal([ActorKit.Escalated], escalated[2].Configuration);
        Assert.True(escalated[3].Accepted, escalated[3].Refusal);

        // Without the memberships the same steps are refused, as today.
        var plain = await SaveWithoutMembershipsAsync(store);
        var refused = ActorKit.Run(plain,
            ActorKit.Step(ActorKit.Submit, ActorKit.Clerk),
            ActorKit.Step(ActorKit.Approve, ActorKit.Pat, "ann", ActorKit.AsManager));
        Assert.Equal(Refusals.Actor, refused[2].Refusal);
        var task = ActorKit.Run(plain,
            ActorKit.Step(ActorKit.Submit, ActorKit.Clerk),
            ActorKit.Step(input: StepInput.Time, after: "P2D"),
            ActorKit.Step(input: StepInput.InvokeDone, invoke: ActorKit.ManagerReview, actor: ActorKit.Pat));
        Assert.Equal(Refusals.Actor, task[3].Refusal);
    }

    [Fact]
    public async Task A_required_actor_is_not_met_by_a_signer_outside_its_members()
    {
        // Pat is a member of Manager only: two signatures as Manager leave the required Finance unsigned, so the gate stays open.
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Manager));
        });
        using var _ = h;
        await using var __ = store;

        var traces = ActorKit.Run(store.Current!,
            ActorKit.Step(ActorKit.Submit, ActorKit.Clerk),
            ActorKit.Step(ActorKit.Approve, ActorKit.Pat, "ann", ActorKit.AsManager),
            ActorKit.Step(ActorKit.Approve, ActorKit.Manager, "bob", ActorKit.AsManager));

        Assert.True(traces[3].Accepted);
        Assert.Equal([ActorKit.Pending], traces[3].Configuration);
    }

    [Fact]
    public async Task The_enabled_actors_offer_members_after_the_listed_actors()
    {
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Manager));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors));
            harness.Write("model/actors/fay.json", ActorKit.Actor(ActorKit.Fay, "Fay", "person", ActorKit.Finance, ActorKit.Seniors));
        });
        using var _ = h;
        await using var __ = store;
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        var start = new ScenarioStart
        {
            Context = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["01JATT00000000000000000011"] = JsonSerializer.SerializeToElement(500),
                ["01JATT00000000000000000012"] = JsonSerializer.SerializeToElement(false),
            },
        };

        var result = ProcessApi.Simulate(runtime.Chart(ActorKit.P)!, "h", runtime, start, [ActorKit.Step(ActorKit.Submit, ActorKit.Clerk)]);

        var approve = result.Enabled.Single(e => e.Event == ActorKit.Approve);
        Assert.Equal([ActorKit.Manager, ActorKit.Finance, ActorKit.Fay, ActorKit.Pat, ActorKit.Seniors], approve.Actors);
    }

    [Fact]
    public async Task A_membership_edit_along_a_chain_replays_the_scenarios_of_processes_that_name_actors()
    {
        // Pat approves as a member of Seniors, a member of Manager; neither the scenario nor the process names Seniors.
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Manager));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors));
            harness.Write("model/scenarios/order-lifecycle/member-approves.json", $$$$"""
                {"kind":"scenario","id":"01JSCN00000000000000000020","name":"MemberApproves","process":"{{{{ActorKit.P}}}}",
                 "start":{"context":{"01JATT00000000000000000011":500,"01JATT00000000000000000012":false}},
                 "steps":[
                   {"id":"01JSTP00000000000000000901","event":"{{{{ActorKit.Submit}}}}","actor":"{{{{ActorKit.Clerk}}}}","payload":{"01JATT00000000000000000021":"rush"},"expect":{"states":["{{{{ActorKit.Pending}}}}"],"context":{"01JATT00000000000000000012":true}}},
                   {"id":"01JSTP00000000000000000902","event":"{{{{ActorKit.Approve}}}}","actor":"{{{{ActorKit.Pat}}}}","signer":"ann","meaning":"{{{{ActorKit.AsManager}}}}","reason":"ok","expect":{"states":["{{{{ActorKit.Pending}}}}"]}}]}
                """);
        });
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain(await ActorKit.ValidateAsync(h, store.Current!), d => d.Rule.StartsWith("MQ93", StringComparison.Ordinal));

        var result = await ActorKit.SaveActorAsync(store, ActorKit.Seniors, ActorKit.Actor(ActorKit.Seniors, "Seniors", "group"));

        Assert.Equal(SaveOutcome.Saved, result.Outcome); // a replay finding does not refuse a save
        var refused = Assert.Single(result.Diagnostics, d => d.Rule == "MQ9301");
        Assert.Equal(".maquettiste/model/scenarios/order-lifecycle/member-approves.json", refused.FilePath);
    }

    [Fact]
    public async Task Deleting_an_actor_named_in_member_of_follows_each_choice()
    {
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group"));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors, ActorKit.Manager));
        });
        using var _ = h;
        await using var __ = store;
        var hash = store.Current!.GetDocument(ActorKit.Seniors)!.Hash;
        Assert.Contains(store.Current.ReferencesTo(ActorKit.Seniors), r => r.FromElementId == ActorKit.Pat && r.Field == "memberOf");

        var refused = await store.DeleteAsync(ActorKit.Seniors, hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);

        var removed = await store.DeleteAsync(ActorKit.Seniors, hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, removed.Outcome);
        Assert.Equal([ActorKit.Manager], store.Current!.Get<Actor>(ActorKit.Pat)!.MemberOf);
        Assert.Equal([ActorKit.Manager], store.Current.Index.Summaries.Single(s => s.Id == ActorKit.Pat).MemberOf!);
    }

    [Fact]
    public async Task Deleting_the_last_holder_under_delete_dependents_keeps_the_member()
    {
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group"));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors));
        });
        using var _ = h;
        await using var __ = store;

        var result = await store.DeleteAsync(ActorKit.Seniors, store.Current!.GetDocument(ActorKit.Seniors)!.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Empty(store.Current!.Get<Actor>(ActorKit.Pat)!.MemberOf);
        Assert.Null(store.Current.Index.Summaries.Single(s => s.Id == ActorKit.Pat).MemberOf);
        Assert.DoesNotContain("memberOf", h.Read("model/actors/pat.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_index_row_names_memberships_by_patch_and_by_full_build()
    {
        var (h, store) = await ActorKit.OpenAsync(harness => harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person")));
        using var _ = h;
        await using var __ = store;
        Assert.Null(store.Current!.Index.Summaries.Single(s => s.Id == ActorKit.Pat).MemberOf);

        var saved = await ActorKit.SaveActorAsync(store, ActorKit.Pat, ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Finance, ActorKit.Manager));
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var patched = store.Current!.Index.Summaries.Single(s => s.Id == ActorKit.Pat);
        Assert.Equal([ActorKit.Finance, ActorKit.Manager], patched.MemberOf!);

        var fresh = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await using var ___ = fresh;
        await fresh.LoadAsync(Ct);
        Assert.Equal(patched, fresh.Current!.Index.Summaries.Single(s => s.Id == ActorKit.Pat));
        Assert.Contains("\"memberOf\":[", JsonSerializer.Serialize(patched, EngineJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_member_changes_the_keys_of_members_and_all_members_and_no_others()
    {
        // Pat exists before the change and only gains memberOf: the actor set is the same, so the keys must follow the file, not the kind.
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Manager));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person"));
        });
        using var _ = h;
        await using var __ = store;
        var before = store.Current!;
        var manager = ResolutionKit.Resolve(before).Actors.Single(a => a.Id == ActorKit.Manager);

        await ActorKit.SaveActorAsync(store, ActorKit.Pat, ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors));
        var after = store.Current!;
        var seniorsAfter = ResolutionKit.Resolve(after).Actors.Single(a => a.Id == ActorKit.Seniors);

        static string Hash(ModelSnapshot model, IEnumerable<string> keys) => string.Join("|", keys.Select(k => k[..2] switch
        {
            "e:" => model.GetDocument(k[2..])?.DependencyHash ?? "absent",
            "r:" => model.ReferrersHash(k[2..]),
            "k:" => KindInfo.TryGet(k[2..], out var info) ? model.KindSetHash(info.Kind) : "absent",
            _ => throw new InvalidOperationException(k),
        }));

        var seniors = ResolutionKit.Resolve(before).Actors.Single(a => a.Id == ActorKit.Seniors);
        Assert.NotEqual(Hash(before, seniors.Members.MembershipKeys), Hash(after, seniors.Members.MembershipKeys));
        Assert.NotEqual(Hash(before, manager.AllMembers.MembershipKeys), Hash(after, manager.AllMembers.MembershipKeys));
        Assert.Equal(Hash(before, manager.Members.MembershipKeys), Hash(after, manager.Members.MembershipKeys));
        Assert.Equal(Hash(before, manager.MemberOf.MembershipKeys), Hash(after, manager.MemberOf.MembershipKeys));
        Assert.Equal(Hash(before, manager.AllMemberOf.MembershipKeys), Hash(after, manager.AllMemberOf.MembershipKeys));
        Assert.Equal(["Pat"], seniorsAfter.Members.Select(a => a.Name));
    }

    [Fact]
    public async Task An_intermediate_holder_whose_type_changes_changes_the_keys_of_all_members()
    {
        // Pat is a member of Plant, a member of Seniors, a member of Manager. Plant becoming a person stops it holding Pat (an outside
        // edit: the editor refuses it, MQ9601), so Manager's all_members loses Pat. Plant names Seniors, a member of Manager, so the
        // referrers key of Seniors follows Plant's file and the list renders again.
        static Action<Loading.LoaderHarness> Chain(string plantType) => harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Manager));
            harness.Write("model/actors/plant.json", ActorKit.Actor(ActorKit.Plant, "Plant", plantType, ActorKit.Seniors));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Plant));
        };
        var (h, store) = await ActorKit.OpenAsync(Chain("organization"));
        using var _ = h;
        await using var __ = store;
        var (h2, edited) = await ActorKit.OpenAsync(Chain("person"));
        using var ___ = h2;
        await using var ____ = edited;
        var before = store.Current!;
        var after = edited.Current!;
        var manager = ResolutionKit.Resolve(before).Actors.Single(a => a.Id == ActorKit.Manager);
        Assert.Equal(["Pat", "Plant", "Seniors"], manager.AllMembers.Select(a => a.Name));

        Assert.Equal(["Plant", "Seniors"], ResolutionKit.Resolve(after).Actors.Single(a => a.Id == ActorKit.Manager).AllMembers.Select(a => a.Name));
        Assert.NotEqual(KeysHash(before, manager.AllMembers.MembershipKeys), KeysHash(after, manager.AllMembers.MembershipKeys));
    }

    [Fact]
    public async Task A_gate_counts_a_signature_by_a_member_of_a_required_actor()
    {
        // The approval gate needs two signatures and Finance. Fay, a member of Finance, signs first: one signature, the gate stays open
        // and unsatisfied; Pat, a member of Manager, signs second and Fay's signature stands for Finance, so the gate completes.
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Manager));
            harness.Write("model/actors/fay.json", ActorKit.Actor(ActorKit.Fay, "Fay", "person", ActorKit.Finance));
        });
        using var _ = h;
        await using var __ = store;
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        var start = new ScenarioStart
        {
            Context = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["01JATT00000000000000000011"] = JsonSerializer.SerializeToElement(500),
                ["01JATT00000000000000000012"] = JsonSerializer.SerializeToElement(false),
            },
        };
        var submit = ActorKit.Step(ActorKit.Submit, ActorKit.Clerk);
        var fay = ActorKit.Step(ActorKit.Approve, ActorKit.Fay, "bob", ActorKit.AsFinance);

        var open = ProcessApi.Simulate(runtime.Chart(ActorKit.P)!, "h", runtime, start, [submit, fay]);
        var gate = Assert.Single(open.Gates);
        Assert.Equal([ActorKit.Fay], gate.Signatures.Select(s => s.Actor));
        Assert.False(gate.Satisfied);

        var done = ProcessApi.Simulate(runtime.Chart(ActorKit.P)!, "h", runtime, start, [submit, fay, ActorKit.Step(ActorKit.Approve, ActorKit.Pat, "ann", ActorKit.AsManager)]);
        Assert.Empty(done.Gates);
        Assert.Equal([ActorKit.Packing, ActorKit.Invoicing], done.Configuration);
    }

    [Fact]
    public async Task The_first_membership_of_a_model_changes_the_keys_of_members()
    {
        // Before the change no actor has memberOf, so the lists are empty; they still carry keys, else nothing would render again.
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group"));
            harness.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person"));
        });
        using var _ = h;
        await using var __ = store;
        var before = store.Current!;
        Assert.False(ActorMembership.Of(before).Any);
        var seniors = ResolutionKit.Resolve(before).Actors.Single(a => a.Id == ActorKit.Seniors);
        var pat = ResolutionKit.Resolve(before).Actors.Single(a => a.Id == ActorKit.Pat);

        await ActorKit.SaveActorAsync(store, ActorKit.Pat, ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors));
        var after = store.Current!;

        Assert.NotEqual(KeysHash(before, seniors.Members.MembershipKeys), KeysHash(after, seniors.Members.MembershipKeys));
        Assert.NotEqual(KeysHash(before, seniors.AllMembers.MembershipKeys), KeysHash(after, seniors.AllMembers.MembershipKeys));
        Assert.NotEqual(KeysHash(before, pat.MemberOf.MembershipKeys), KeysHash(after, pat.MemberOf.MembershipKeys));
        Assert.NotEqual(KeysHash(before, pat.AllMemberOf.MembershipKeys), KeysHash(after, pat.AllMemberOf.MembershipKeys));
        Assert.Equal(KeysHash(before, seniors.MemberOf.MembershipKeys), KeysHash(after, seniors.MemberOf.MembershipKeys));
    }

    private static string KeysHash(ModelSnapshot model, IEnumerable<string> keys) => string.Join("|", keys.Select(k => k[..2] switch
    {
        "e:" => model.GetDocument(k[2..])?.DependencyHash ?? "absent",
        "r:" => model.ReferrersHash(k[2..]),
        _ => throw new InvalidOperationException(k),
    }));

    private static async Task<ModelSnapshot> SaveWithoutMembershipsAsync(ModelStore store)
    {
        foreach (var (id, name) in new[] { (ActorKit.Pat, "Pat"), (ActorKit.Fay, "Fay") })
            Assert.Equal(SaveOutcome.Saved, (await ActorKit.SaveActorAsync(store, id, ActorKit.Actor(id, name, "person"))).Outcome);
        return store.Current!;
    }
}
