using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Resolution;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The actor rules of wave 1 (actors-design.md section 5): MQ9601, MQ9602 and MQ9605, and MQ9102, MQ9105 and MQ9106 following membership.
/// A model with today's actors gives today's findings with today's messages.
/// </summary>
public sealed class ActorRuleTests
{
    private const string TodaysMq9106 = "Actor 'Auditor' is not referenced by any process (event actors, gate signers or human tasks); use it in a process or remove it.";

    private static async Task<List<Diagnostic>> FindingsAsync(Action<Loading.LoaderHarness> prepare)
    {
        var (h, store) = await ActorKit.OpenAsync(prepare);
        using var _ = h;
        await using var __ = store;
        return [.. (await ActorKit.ValidateAsync(h, store.Current!)).Where(d => d.Rule.StartsWith("MQ9", StringComparison.Ordinal) && d.Rule != "MQ9209")];
    }

    [Fact]
    public async Task MQ9601_flags_a_member_of_a_person_itself_or_twice()
    {
        var findings = await FindingsAsync(h =>
        {
            h.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Clerk, ActorKit.Pat, ActorKit.Manager, ActorKit.Manager));
            h.Write("model/actors/auto.json", ActorKit.Actor(ActorKit.Auto, "Automatic", "system"));
            h.Write("model/actors/fay.json", ActorKit.Actor(ActorKit.Fay, "Fay", "person", ActorKit.Auto));
        });

        var hits = findings.Where(d => d.Rule == "MQ9601").OrderBy(d => d.FilePath, StringComparer.Ordinal).ThenBy(d => d.JsonPointer, StringComparer.Ordinal).ToList();
        Assert.Equal(["fay.json:/memberOf/0", "pat.json:/memberOf/0", "pat.json:/memberOf/1", "pat.json:/memberOf/3"],
            hits.Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer));
        Assert.Contains("which is a person", hits[1].Message, StringComparison.Ordinal);
        Assert.Contains("names itself", hits[2].Message, StringComparison.Ordinal);
        Assert.Contains("twice", hits[3].Message, StringComparison.Ordinal);
        Assert.Contains("which is the system actor", hits[0].Message, StringComparison.Ordinal);
        Assert.All(hits, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task MQ9601_accepts_roles_groups_and_organizations()
    {
        var findings = await FindingsAsync(h =>
        {
            h.Write("model/actors/plant.json", ActorKit.Actor(ActorKit.Plant, "Plant", "organization"));
            h.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Plant));
            h.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors, ActorKit.Manager));
        });

        Assert.DoesNotContain(findings, d => d.Rule is "MQ9601" or "MQ9602");
    }

    [Fact]
    public async Task MQ9602_names_the_cycle_on_each_actor_in_it()
    {
        var findings = await FindingsAsync(h =>
        {
            h.Write("model/actors/plant.json", ActorKit.Actor(ActorKit.Plant, "Plant", "organization", ActorKit.Seniors));
            h.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group", ActorKit.Manager, ActorKit.Plant));
            h.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors));
        });

        var hits = findings.Where(d => d.Rule == "MQ9602").OrderBy(d => d.FilePath, StringComparer.Ordinal).ToList();
        Assert.Equal(["plant.json:/memberOf/0", "seniors.json:/memberOf/1"], hits.Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer));
        Assert.Contains("Plant -> Seniors -> Plant", hits[0].Message, StringComparison.Ordinal);
        Assert.Contains("Seniors -> Plant -> Seniors", hits[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MQ9605_flags_every_system_actor_when_there_are_two()
    {
        var one = await FindingsAsync(h => h.Write("model/actors/auto.json", ActorKit.Actor(ActorKit.Auto, "Automatic", "system")));
        Assert.DoesNotContain(one, d => d.Rule == "MQ9605");

        var two = await FindingsAsync(h =>
        {
            h.Write("model/actors/auto.json", ActorKit.Actor(ActorKit.Auto, "Automatic", "system"));
            h.Write("model/actors/auto2.json", ActorKit.Actor(ActorKit.Auto2, "Robot", "system"));
        });
        var hits = two.Where(d => d.Rule == "MQ9605").OrderBy(d => d.FilePath, StringComparer.Ordinal).ToList();
        Assert.Equal(["auto.json:/type", "auto2.json:/type"], hits.Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer));
        Assert.Contains("as is 'Robot'", hits[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_second_system_actor_is_refused()
    {
        var (h, store) = await ActorKit.OpenAsync(harness => harness.Write("model/actors/auto.json", ActorKit.Actor(ActorKit.Auto, "Automatic", "system")));
        using var _ = h;
        await using var __ = store;

        var result = await store.CreateAsync(System.Text.Encoding.UTF8.GetBytes(ActorKit.Actor(ActorKit.Auto2, "Robot", "system")), ChangeSource.Editor, ActorKit.Ct);

        Assert.NotEqual(SaveOutcome.Saved, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ9605");
    }

    [Fact]
    public async Task MQ9105_and_MQ9102_count_members()
    {
        // The event lets only Manager raise approve; the gate's signers are Manager and Finance and it requires Pat.
        static void Narrow(Loading.LoaderHarness h)
        {
            var path = "model/processes/order-lifecycle.json";
            var text = h.Read(path);
            text = text.Replace($"\"actors\": [\n        \"{ActorKit.Manager}\",\n        \"{ActorKit.Finance}\"\n      ]", $"\"actors\": [\n        \"{ActorKit.Manager}\"\n      ]", StringComparison.Ordinal);
            text = text.Replace($"\"requiredActors\": [\n          \"{ActorKit.Finance}\"\n        ]", $"\"requiredActors\": [\n          \"{ActorKit.Pat}\"\n        ]", StringComparison.Ordinal);
            h.Write(path, text);
        }

        var plain = await FindingsAsync(h =>
        {
            Narrow(h);
            h.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person"));
        });
        Assert.Single(plain, d => d.Rule == "MQ9105");
        Assert.Single(plain, d => d.Rule == "MQ9102");

        var members = await FindingsAsync(h =>
        {
            Narrow(h);
            h.Write("model/actors/finance.json", ActorKit.Actor(ActorKit.Finance, "Finance", "role", ActorKit.Manager));
            h.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Finance));
        });
        Assert.DoesNotContain(members, d => d.Rule is "MQ9105" or "MQ9102");
    }

    [Fact]
    public async Task MQ9106_counts_uses_through_members_and_memberships()
    {
        var alone = await FindingsAsync(h => h.Write("model/actors/auditor.json", ActorKit.Actor(ActorKit.Auditor, "Auditor", "role")));
        Assert.Equal(TodaysMq9106, Assert.Single(alone, d => d.Rule == "MQ9106").Message);

        // Clerk, whom submit names, is a member of Auditor; Pat is a member of Manager, whom approve names.
        var used = await FindingsAsync(h =>
        {
            h.Write("model/actors/auditor.json", ActorKit.Actor(ActorKit.Auditor, "Auditor", "role"));
            h.Write("model/actors/clerk.json", ActorKit.Actor(ActorKit.Clerk, "Clerk", "person", ActorKit.Auditor));
            h.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Manager));
        });
        Assert.DoesNotContain(used, d => d.Rule == "MQ9106");

        var unused = await FindingsAsync(h =>
        {
            h.Write("model/actors/seniors.json", ActorKit.Actor(ActorKit.Seniors, "Seniors", "group"));
            h.Write("model/actors/pat.json", ActorKit.Actor(ActorKit.Pat, "Pat", "person", ActorKit.Seniors));
        });
        var hits = unused.Where(d => d.Rule == "MQ9106").OrderBy(d => d.FilePath, StringComparer.Ordinal).ToList();
        Assert.Equal(["pat.json", "seniors.json"], hits.Select(d => Path.GetFileName(d.FilePath)));
        Assert.All(hits, d => Assert.Contains("through its members or through the actors it is a member of", d.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_a_membership_rechecks_the_actor_it_named()
    {
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/auditor.json", ActorKit.Actor(ActorKit.Auditor, "Auditor", "role"));
            harness.Write("model/actors/clerk.json", ActorKit.Actor(ActorKit.Clerk, "Clerk", "person", ActorKit.Auditor));
        });
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain(await ActorKit.ValidateAsync(h, store.Current!), d => d.Rule == "MQ9106");

        var result = await ActorKit.SaveActorAsync(store, ActorKit.Clerk, ActorKit.Actor(ActorKit.Clerk, "Clerk", "person"));

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(TodaysMq9106, Assert.Single(result.Diagnostics, d => d.Rule == "MQ9106").Message);
    }

    [Fact]
    public async Task A_model_without_the_new_keys_and_types_answers_as_today()
    {
        var (h, store) = await ActorKit.OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        // No new rule fires, and nothing new is written in the index rows or the records.
        Assert.DoesNotContain(await ActorKit.ValidateAsync(h, model), d => d.Rule.StartsWith("MQ96", StringComparison.Ordinal));
        Assert.False(ActorMembership.Of(model).Any);
        Assert.Null(ActorMembership.Of(model).SystemActor);
        var rows = JsonSerializer.Serialize(model.Index.Summaries, EngineJson.Options);
        Assert.DoesNotContain("memberOf", rows, StringComparison.Ordinal);
        var resolved = ResolutionKit.Resolve(model);
        var records = JsonSerializer.Serialize(ResolvedRecords.Entries(resolved, "actors", null).Select(e => e.Project()).ToList(), EngineJson.Options);
        Assert.DoesNotContain("memberOf", records, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"external-system\"", records, StringComparison.Ordinal);

        // Without a system actor no trace names an actor, not even a step that names its own, so simulate answers keep their shape.
        var traces = ActorKit.Run(model, ActorKit.Step(ActorKit.Ship), ActorKit.Step(ActorKit.Submit, ActorKit.Clerk), ActorKit.Step(input: StepInput.Time, after: "P2D"),
            ActorKit.Step(input: StepInput.InvokeDone, invoke: ActorKit.ManagerReview, actor: ActorKit.Manager));
        Assert.True(traces[2].Accepted);
        Assert.All(traces, t => Assert.Null(t.Actor));
        foreach (var trace in traces)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(trace, EngineJson.Options));
            Assert.False(json.RootElement.TryGetProperty("actor", out var none), none.ToString());
        }
    }
}

/// <summary>The system actor in traces (actors-design.md decision 2): only for a move no one sends, never for a human task.</summary>
public sealed class SystemActorTests
{
    [Fact]
    public async Task Automatic_moves_name_the_system_actor_when_the_project_has_one()
    {
        var (h, store) = await ActorKit.OpenAsync(harness => harness.Write("model/actors/auto.json", ActorKit.Actor(ActorKit.Auto, "Automatic", "system")));
        using var _ = h;
        await using var __ = store;

        var traces = ActorKit.Run(store.Current!,
            ActorKit.Step(ActorKit.Ship),
            ActorKit.Step(ActorKit.Submit, ActorKit.Clerk),
            ActorKit.Step(input: StepInput.Time, after: "P2D"),
            ActorKit.Step(input: StepInput.InvokeDone, invoke: ActorKit.ManagerReview),
            ActorKit.Step(input: StepInput.InvokeDone, invoke: ActorKit.ManagerReview, actor: ActorKit.Manager));

        Assert.Equal(ActorKit.Auto, traces[0].Actor); // the initial entry
        Assert.Null(traces[1].Actor); // an event no actor sent
        Assert.Null(traces[2].Actor); // the step's own actor is in its input, not repeated
        Assert.Equal(ActorKit.Auto, traces[3].Actor); // the timer
        Assert.Equal([ActorKit.Escalated], traces[3].Configuration);
        Assert.Null(traces[4].Actor); // a human task is never completed by the system
        Assert.Equal(Refusals.Actor, traces[4].Refusal);
        Assert.True(traces[5].Accepted);
        Assert.Null(traces[5].Actor);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(traces[3], EngineJson.Options));
        Assert.Equal(ActorKit.Auto, json.RootElement.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Without_exactly_one_system_actor_automatic_moves_have_no_actor()
    {
        var (h, store) = await ActorKit.OpenAsync(harness =>
        {
            harness.Write("model/actors/auto.json", ActorKit.Actor(ActorKit.Auto, "Automatic", "system"));
            harness.Write("model/actors/auto2.json", ActorKit.Actor(ActorKit.Auto2, "Robot", "system"));
        });
        using var _ = h;
        await using var __ = store;

        var traces = ActorKit.Run(store.Current!, ActorKit.Step(ActorKit.Submit, ActorKit.Clerk), ActorKit.Step(input: StepInput.Time, after: "P2D"));

        Assert.Null(traces[0].Actor);
        Assert.Null(traces[2].Actor);
        Assert.Contains(await ActorKit.ValidateAsync(h, store.Current!), d => d.Rule == "MQ9605");
    }
}
