using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Stale output with a stereotype entry (stereotype-sets-design.md section 6), through the planner: generate, edit, plan, and the unit that
/// read what changed renders again. Over the <c>process-basics</c> fixture with an invented model: Approval lists the stereotype
/// <c>approvable</c>; Audit lists Invoice by name; Check lists Invoice by name and its guard reads approvable's <c>requestedBy</c>. Invoice
/// carries approvable and reviewed, Memo carries reviewed, Request sits in Purchasing (no defaults yet), UrgentRequest derives from Request
/// in Sales, and Note carries nothing. A pack writes each entity's lifecycles, each process's subjects and each scenario's replay.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class StereotypeSubjectStaleTests
{
    private const string Pack = "stereo";
    private const string Sales = "01JPKG00000000000000000001";
    private const string Purchasing = "01JPKG00000000000000000501";
    private const string Approvable = "01JSTR00000000000000000501";
    private const string Reviewed = "01JSTR00000000000000000502";
    private const string RequestedBy = "01JATT00000000000000000501";
    private const string Invoice = "01JENT00000000000000000501";
    private const string Memo = "01JENT00000000000000000502";
    private const string Request = "01JENT00000000000000000503";
    private const string UrgentRequest = "01JENT00000000000000000504";
    private const string Note = "01JENT00000000000000000505";
    private const string Approval = "01JPRC00000000000000000501";
    private const string Audit = "01JPRC00000000000000000502";
    private const string Check = "01JPRC00000000000000000503";
    private const string Plain = "01JSCN00000000000000000501";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Entity(string id, string name, string package, string extra = "", string? @base = null) => @base is null
        ? $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{package}}"{{extra}},"key":{"attributes":["{{id[..^3]}}9{{id[^2..]}}"]},"attributes":[{"id":"{{id[..^3]}}9{{id[^2..]}}","name":"id","type":"uuid"}]}"""
        : $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{package}}","base":"{{@base}}"{{extra}}}""";

    // The first 23 characters of a process's part ids: unique per process.
    private static string Prefix(string id) => id[..^6] + id[^3..];

    private static string Process(string id, string name, string subjects, string guards = "")
    {
        var p = Prefix(id);
        var guard = guards.Length == 0 ? "" : $",\"guard\":\"{p}951\"";
        return $$"""
            {"kind":"process","id":"{{id}}","name":"{{name}}","package":"{{Sales}}","use":"lifecycle","subjects":[{{subjects}}],{{guards}}
             "events":[{"id":"{{p}}911","name":"submit"},{"id":"{{p}}912","name":"approve"}],
             "states":[{"id":"{{p}}921","name":"Draft"},{"id":"{{p}}922","name":"Submitted"},{"id":"{{p}}923","name":"Done","type":"final"}],
             "transitions":[{"id":"{{p}}931","source":"{{p}}921","event":"{{p}}911","targets":["{{p}}922"]{{guard}}},
               {"id":"{{p}}932","source":"{{p}}922","event":"{{p}}912","targets":["{{p}}923"]}]}
            """;
    }

    private static string StereotypeEntry(string id, string key) => $$"""{"id":"{{id}}","stereotype":"{{key}}"}""";

    private static string EntityEntry(string id, string entity) => $$"""{"id":"{{id}}","entity":"{{entity}}"}""";

    private static string Unit(string kind, string id) => Pack + "/" + kind + ":" + id;

    [Fact]
    public async Task A_process_whose_first_entry_names_a_stereotype_renders_the_lifecycles_of_its_carriers_again()
    {
        await using var repo = await StereoRepo.CreateAsync();
        Assert.Equal("", repo.Output("entities/Memo.txt"));

        // Audit lists Invoice by name; now also reviewed, which Memo carries and no entry named yet.
        await repo.EditAsync(Audit, n => n["subjects"]!.AsArray().Add(JsonNode.Parse(StereotypeEntry("01JPRX00000000000000000504", "reviewed"))));
        Assert.Contains(Unit("entity", Memo), await repo.RenderedAsync());
        await repo.ApplyAsync();
        Assert.Equal("Audit ", repo.Output("entities/Memo.txt"));
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task The_models_first_stereotype_entry_renders_the_lifecycles_of_its_carriers_again()
    {
        await using var repo = await StereoRepo.CreateAsync();

        // Approval lists Invoice by name instead, so no process names a stereotype; generate that model.
        await repo.EditAsync(Approval, n => n["subjects"] = JsonNode.Parse("[" + EntityEntry("01JPRX00000000000000000501", Invoice) + "]"));
        await repo.ApplyAsync();
        Assert.Equal("", repo.Output("entities/Memo.txt"));

        // The model's first stereotype entry: Audit now also covers reviewed, which Memo carries.
        await repo.EditAsync(Audit, n => n["subjects"]!.AsArray().Add(JsonNode.Parse(StereotypeEntry("01JPRX00000000000000000504", "reviewed"))));
        Assert.Contains(Unit("entity", Memo), await repo.RenderedAsync());
        await repo.ApplyAsync();
        Assert.Equal("Audit ", repo.Output("entities/Memo.txt"));
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task A_process_naming_a_second_stereotype_renders_the_lifecycles_of_its_carriers_again()
    {
        await using var repo = await StereoRepo.CreateAsync();
        await repo.EditAsync(Approval, n => n["subjects"]!.AsArray().Add(JsonNode.Parse(StereotypeEntry("01JPRX00000000000000000505", "reviewed"))));
        Assert.Contains(Unit("entity", Memo), await repo.RenderedAsync());
        await repo.ApplyAsync();
        Assert.Equal("Approval ", repo.Output("entities/Memo.txt"));
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task An_entity_gaining_or_losing_the_stereotype_renders_its_lifecycles_and_the_subjects_again()
    {
        await using var repo = await StereoRepo.CreateAsync();
        Assert.Equal("Invoice ", repo.Output("processes/Approval.txt"));

        // Purchasing gives approvable by default: Request carries it.
        await repo.EditAsync(Purchasing, n => n["defaultStereotypes"] = new JsonArray("approvable"));
        var rendered = await repo.RenderedAsync();
        Assert.Contains(Unit("entity", Request), rendered);
        Assert.Contains(Unit("process", Approval), rendered);
        await repo.ApplyAsync();
        Assert.Equal("Approval ", repo.Output("entities/Request.txt"));
        // By qualified name: Purchasing.Request before Sales.Invoice.
        Assert.Equal("Request Invoice ", repo.Output("processes/Approval.txt"));

        // Request excludes it again.
        await repo.EditAsync(Request, n => n["excludeStereotypes"] = new JsonArray("approvable"));
        rendered = await repo.RenderedAsync();
        Assert.Contains(Unit("entity", Request), rendered);
        Assert.Contains(Unit("process", Approval), rendered);
        await repo.ApplyAsync();
        Assert.Equal("", repo.Output("entities/Request.txt"));
        Assert.Equal("Invoice ", repo.Output("processes/Approval.txt"));
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task A_derived_entity_whose_base_gains_or_loses_the_stereotype_renders_its_lifecycles_again()
    {
        await using var repo = await StereoRepo.CreateAsync();
        Assert.Equal("", repo.Output("entities/UrgentRequest.txt"));

        // Request's package, not UrgentRequest's, gives approvable: UrgentRequest is covered through its base.
        await repo.EditAsync(Purchasing, n => n["defaultStereotypes"] = new JsonArray("approvable"));
        Assert.Contains(Unit("entity", UrgentRequest), await repo.RenderedAsync());
        await repo.ApplyAsync();
        Assert.Equal("Approval ", repo.Output("entities/UrgentRequest.txt"));

        await repo.EditAsync(Purchasing, n => n.Remove("defaultStereotypes"));
        Assert.Contains(Unit("entity", UrgentRequest), await repo.RenderedAsync());
        await repo.ApplyAsync();
        Assert.Equal("", repo.Output("entities/UrgentRequest.txt"));
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task A_guard_reading_a_stereotype_default_replays_the_scenario_again_when_the_default_changes()
    {
        await using var repo = await StereoRepo.CreateAsync();
        Assert.Equal("False", repo.Output("scenarios/Plain.txt"), ignoreCase: true);

        // Check lists Invoice by name only; its guard reads approvable's requestedBy, which the object leaves to the default.
        await repo.EditAsync(Approvable, n => n["attributes"]![0]!["default"] = "ann");
        Assert.Contains(Unit("scenario", Plain), await repo.RenderedAsync());
        await repo.ApplyAsync();
        Assert.Equal("True", repo.Output("scenarios/Plain.txt"), ignoreCase: true);
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    [Fact]
    public async Task An_entity_that_carries_no_named_stereotype_renders_no_process_with_a_stereotype_entry_again()
    {
        await using var repo = await StereoRepo.CreateAsync();
        await repo.EditAsync(Note, n => n["description"] = "Free text.");
        var rendered = await repo.RenderedAsync();
        Assert.Contains(Unit("entity", Note), rendered);
        Assert.DoesNotContain(Unit("process", Approval), rendered);
        await repo.ApplyAsync();
        await repo.AssertIncrementalEqualsForcedAsync();
    }

    /// <summary>The fixture in a temporary repo with the <c>stereo</c> pack writing under <c>gen/</c>, generated once.</summary>
    private sealed class StereoRepo : IAsyncDisposable
    {
        private StereoRepo()
        {
            Repo = new TempRepo();
            E2ERepo.CopyTree(Fixtures.Path("models", "process-basics"), Repo.RepoRoot);
            Repo.WriteFile(".maquettiste/model/packages/purchasing.json", $$"""{"kind":"package","id":"{{Purchasing}}","name":"Purchasing"}""");
            Repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/approvable.json", $$"""
                {"kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"approvable","appliesTo":["entity"],
                 "attributes":[{"id":"{{RequestedBy}}","name":"requestedBy","type":"string","length":40,"default":"nobody"}]}
                """);
            Repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/reviewed.json",
                $$"""{"kind":"stereotype","id":"{{Reviewed}}","key":"reviewed","name":"reviewed","appliesTo":["entity"]}""");
            Repo.WriteFile(".maquettiste/model/entities/invoice.json", Entity(Invoice, "Invoice", Sales, ",\"stereotypes\":[\"approvable\",\"reviewed\"]"));
            Repo.WriteFile(".maquettiste/model/entities/memo.json", Entity(Memo, "Memo", Sales, ",\"stereotypes\":[\"reviewed\"]"));
            Repo.WriteFile(".maquettiste/model/entities/request.json", Entity(Request, "Request", Purchasing));
            Repo.WriteFile(".maquettiste/model/entities/urgent-request.json", Entity(UrgentRequest, "UrgentRequest", Sales, @base: Request));
            Repo.WriteFile(".maquettiste/model/entities/note.json", Entity(Note, "Note", Sales));
            Repo.WriteFile(".maquettiste/model/processes/approval.json", Process(Approval, "Approval", StereotypeEntry("01JPRX00000000000000000501", "approvable")));
            Repo.WriteFile(".maquettiste/model/processes/audit.json", Process(Audit, "Audit", EntityEntry("01JPRX00000000000000000502", Invoice)));
            Repo.WriteFile(".maquettiste/model/processes/check.json", Process(Check, "Check", EntityEntry("01JPRX00000000000000000503", Invoice),
                $$$"""
                "guards":[{"id":"{{{Prefix(Check)}}}951","name":"hasRequester","condition":{"op":"ne","args":[{"path":["{{{RequestedBy}}}"]},{"value":"nobody"}]}}],
                """));
            Repo.WriteFile(".maquettiste/model/scenarios/check/plain.json", $$"""
                {"kind":"scenario","id":"{{Plain}}","name":"Plain","process":"{{Check}}",
                 "objects":[{"id":"01JSCN00000000000000000591","name":"inv","entity":"{{Invoice}}"}],"subject":"01JSCN00000000000000000591",
                 "steps":[{"id":"01JSTP00000000000000000591","event":"{{Prefix(Check)}}911"}]}
                """);

            var pack = Path.Combine(Repo.ModelRoot, "templates", Pack);
            Directory.CreateDirectory(pack);
            File.WriteAllText(Path.Combine(pack, "pack.json"), """
                {"name": "stereo", "version": "1.0.0", "engine": ">=1.0", "units": [
                  {"id": "entity", "template": "entity.scriban", "for": "each entity", "output": "entities/{{ entity.name }}.txt"},
                  {"id": "process", "template": "process.scriban", "for": "each process", "output": "processes/{{ process.name }}.txt"},
                  {"id": "scenario", "template": "scenario.scriban", "for": "each scenario", "output": "scenarios/{{ scenario.name }}.txt"}]}
                """);
            File.WriteAllText(Path.Combine(pack, "entity.scriban"), "{{ for l in entity.lifecycles }}{{ l.name }} {{ end }}");
            File.WriteAllText(Path.Combine(pack, "process.scriban"), "{{ for s in process.subjects }}{{ s.entity.name }} {{ end }}");
            File.WriteAllText(Path.Combine(pack, "scenario.scriban"), "{{ for s in scenario.steps }}{{ s.trace.accepted }}{{ end }}");

            var settingsPath = Path.Combine(Repo.ModelRoot, "maquettiste.json");
            var node = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
            node["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "gen" }) };
            node["packs"] = new JsonObject { [Pack] = new JsonObject { ["output"] = "gen" } };
            File.WriteAllBytes(settingsPath, TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));
            Store = new ModelStore(Repo.Options);
            Service = new GenerationService(Store, Repo.Options);
        }

        public static async Task<StereoRepo> CreateAsync()
        {
            var repo = new StereoRepo();
            await repo.ApplyAsync();
            Assert.Empty(await repo.RenderedAsync());
            return repo;
        }

        private TempRepo Repo { get; }

        private ModelStore Store { get; }

        private GenerationService Service { get; }

        public string Output(string path) => Repo.ReadFile("gen/" + path);

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
