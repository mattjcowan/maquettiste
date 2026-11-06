using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Stale replays with object effects (object-effects-design.md section 7, "Replay keys"), through the planner: generate, edit, plan, and the
/// scenario whose replay reads what changed renders again. Over the <c>process-basics</c> fixture with an invented model: Order gains
/// approvedBy; Sign's guard reads <c>actor()</c>; Audit's guard reads OrderLifecycle's state on the object, which OrderLifecycle binds to the
/// enum OrderStatus; Stamp sets approvedBy on a move its scenario never takes. A pack writes each scenario's replay.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ObjectEffectsStaleTests
{
    private const string Pack = "effects";
    private const string Sales = "01JPKG00000000000000000001";
    private const string Order = "01JENT00000000000000000001";
    private const string OrderLifecycle = "01JPRC00000000000000000001";
    private const string OrderStatus = "01JENM00000000000000000001";
    private const string Review = "01JSTA00000000000000000003";
    private const string ApprovedBy = "01JATT00000000000000000601";
    private const string Finance = "01JACT00000000000000000003";
    private const string Sign = "01JPRC00000000000000000601";
    private const string Audit = "01JPRC00000000000000000602";
    private const string Stamp = "01JPRC00000000000000000603";
    private const string SignPlain = "01JSCN00000000000000000601";
    private const string AuditPlain = "01JSCN00000000000000000602";
    private const string StampPlain = "01JSCN00000000000000000603";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The first 23 characters of a process's part ids: unique per process.
    private static string Prefix(string id) => id[..^6] + id[^3..];

    // A lifecycle of Order: Open, then go to Done, or stop to Done, guarded and running the action when given. Scenarios only go, so the
    // replay never runs what this item adds, and the keys are what is tested.
    private static string Process(string id, string name, string guard = "", string action = "")
    {
        var p = Prefix(id);
        var guards = guard.Length == 0 ? "" : $$"""
            "guards":[{"id":"{{p}}951","name":"check","condition":{{guard}}}],
            """;
        var actions = action.Length == 0 ? "" : $$"""
            "actions":[{"id":"{{p}}961","name":"stamp","sets":[{"id":"{{p}}962","attribute":"{{ApprovedBy}}","value":{{action}}}]}],
            """;
        return $$"""
            {"kind":"process","id":"{{id}}","name":"{{name}}","package":"{{Sales}}","use":"lifecycle","subjects":[{"id":"{{p}}901","entity":"{{Order}}"}],{{guards}}{{actions}}
             "events":[{"id":"{{p}}911","name":"go"},{"id":"{{p}}912","name":"stop"}],
             "states":[{"id":"{{p}}921","name":"Open"},{"id":"{{p}}923","name":"Done","type":"final"}],
             "transitions":[{"id":"{{p}}931","source":"{{p}}921","event":"{{p}}911","targets":["{{p}}923"]},
               {"id":"{{p}}932","source":"{{p}}921","event":"{{p}}912","targets":["{{p}}923"]{{(guard.Length == 0 ? "" : $",\"guard\":\"{p}951\"")}}{{(action.Length == 0 ? "" : $",\"actions\":[\"{p}961\"]")}}}]}
            """;
    }

    private static string Scenario(string id, string name, string process) => $$"""
        {"kind":"scenario","id":"{{id}}","name":"{{name}}","process":"{{process}}","outcome":"final","steps":[{"id":"{{id[..^3]}}9{{id[^2..]}}","event":"{{Prefix(process)}}911"}]}
        """;

    private static string Unit(string id) => Pack + "/scenario:" + id;

    [Fact]
    public async Task A_process_that_reads_actor_replays_its_scenarios_again_when_an_actor_changes()
    {
        await using var repo = await EffectsRepo.CreateAsync();

        // Finance acts in no step of Sign's scenario; actor() may still read it (the system actor, a participant's actor).
        await repo.EditAsync(Finance, n => n["description"] = "Pays the bills.");
        var rendered = await repo.RenderedAsync();
        Assert.Contains(Unit(SignPlain), rendered);
        Assert.DoesNotContain(Unit(AuditPlain), rendered);
        Assert.DoesNotContain(Unit(StampPlain), rendered);
        await repo.ApplyAsync();
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task A_process_that_reads_another_lifecycle_replays_again_when_that_lifecycle_changes()
    {
        await using var repo = await EffectsRepo.CreateAsync();
        await repo.EditAsync(OrderLifecycle, n => n["description"] = "From draft to closed, read by Audit.");
        var rendered = await repo.RenderedAsync();
        Assert.Contains(Unit(AuditPlain), rendered);
        Assert.DoesNotContain(Unit(SignPlain), rendered);
        await repo.ApplyAsync();
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task A_process_that_reads_another_lifecycle_replays_again_when_the_enum_of_its_binding_changes()
    {
        await using var repo = await EffectsRepo.CreateAsync();
        await repo.EditAsync(OrderStatus, n => n["members"]![1]!["description"] = "Being reviewed.");
        var rendered = await repo.RenderedAsync();
        Assert.Contains(Unit(AuditPlain), rendered);
        Assert.DoesNotContain(Unit(SignPlain), rendered);
        Assert.DoesNotContain(Unit(StampPlain), rendered);
        await repo.ApplyAsync();
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task A_process_that_sets_data_replays_again_when_the_entity_of_the_attribute_it_sets_changes()
    {
        await using var repo = await EffectsRepo.CreateAsync();
        await repo.EditAsync(Order, n => n["attributes"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == ApprovedBy)!["default"] = "someone");
        var rendered = await repo.RenderedAsync();
        Assert.Contains(Unit(StampPlain), rendered);
        await repo.ApplyAsync();
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    /// <summary>The fixture in a temporary repo with the <c>effects</c> pack writing under <c>gen/</c>, generated once.</summary>
    private sealed class EffectsRepo : IAsyncDisposable
    {
        private EffectsRepo()
        {
            Repo = new TempRepo();
            E2ERepo.CopyTree(Fixtures.Path("models", "process-basics"), Repo.RepoRoot);
            var orderPath = Path.Combine(Repo.ModelRoot, "model", "entities", "order.json");
            var order = JsonNode.Parse(File.ReadAllText(orderPath))!.AsObject();
            order["attributes"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"{{ApprovedBy}}","name":"approvedBy","type":"string","length":40,"default":"nobody"}"""));
            File.WriteAllText(orderPath, order.ToJsonString());
            Repo.WriteFile(".maquettiste/model/processes/sign.json",
                Process(Sign, "Sign", guard: $$"""{"op":"ne","args":[{"path":["{{ApprovedBy}}"]},{"actor":true}]}"""));
            Repo.WriteFile(".maquettiste/model/processes/audit.json", Process(Audit, "Audit", guard: $$"""{"inState":["{{Review}}"]}"""));
            Repo.WriteFile(".maquettiste/model/processes/stamp.json", Process(Stamp, "Stamp", action: """{"value":"x"}"""));
            Repo.WriteFile(".maquettiste/model/scenarios/sign/sign-plain.json", Scenario(SignPlain, "SignPlain", Sign));
            Repo.WriteFile(".maquettiste/model/scenarios/audit/audit-plain.json", Scenario(AuditPlain, "AuditPlain", Audit));
            Repo.WriteFile(".maquettiste/model/scenarios/stamp/stamp-plain.json", Scenario(StampPlain, "StampPlain", Stamp));

            var pack = Path.Combine(Repo.ModelRoot, "templates", Pack);
            Directory.CreateDirectory(pack);
            File.WriteAllText(Path.Combine(pack, "pack.json"), """
                {"name": "effects", "version": "1.0.0", "engine": ">=1.0", "units": [
                  {"id": "scenario", "template": "scenario.scriban", "for": "each scenario", "output": "scenarios/{{ scenario.name }}.txt"}]}
                """);
            File.WriteAllText(Path.Combine(pack, "scenario.scriban"), "{{ for s in scenario.steps }}{{ s.trace.accepted }}{{ end }}");

            var settingsPath = Path.Combine(Repo.ModelRoot, "maquettiste.json");
            var node = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
            node["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "gen" }) };
            node["packs"] = new JsonObject { [Pack] = new JsonObject { ["output"] = "gen" } };
            File.WriteAllBytes(settingsPath, TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));
            Store = new ModelStore(Repo.Options);
            Service = new GenerationService(Store, Repo.Options);
        }

        public static async Task<EffectsRepo> CreateAsync()
        {
            var repo = new EffectsRepo();
            await repo.ApplyAsync();
            Assert.Empty(await repo.RenderedAsync());
            return repo;
        }

        private TempRepo Repo { get; }

        private ModelStore Store { get; }

        private GenerationService Service { get; }

        public async Task ApplyAsync(bool force = false)
        {
            var result = await Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2, Force = force }, null, Ct);
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        }

        public async Task<List<string>> RenderedAsync()
        {
            var result = await Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, result.Outcome);
            return [.. result.Plan!.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal)];
        }

        public async Task EditAsync(string id, Action<JsonObject> edit)
        {
            var document = await Store.GetElementAsync(id, Ct);
            Assert.NotNull(document);
            var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
            edit(node);
            var result = await Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
            Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        /// <summary>The incremental outputs equal a forced run's: nothing was left stale.</summary>
        public async Task AssertIncrementalEqualsForcedAsync()
        {
            var before = Outputs();
            await ApplyAsync(force: true);
            Assert.Equal(before, Outputs());
        }

        private SortedDictionary<string, string> Outputs()
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Repo.ListFiles().Where(p => p.StartsWith("gen/", StringComparison.Ordinal)))
                files[path] = Repo.ReadFile(path);
            return files;
        }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            Repo.Dispose();
        }
    }
}
