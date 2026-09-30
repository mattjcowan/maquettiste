using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The model of processes, actors and scenarios (phase-3-design.md sections 2.1 to 2.7) over the <c>process-basics</c> fixture: a
/// lifecycle of <c>Order</c> with compound, parallel, history, choice and final states, every trigger, a gate, four actors (one a
/// persona through a project-defined stereotype), two scenarios and the process's statechart diagram.
/// </summary>
public sealed class ProcessModelTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Order = "01JENT00000000000000000001";
    private const string Process = "01JPRC00000000000000000001";
    private const string Diagram = "01JDGM00000000000000000001";
    private const string Clerk = "01JACT00000000000000000001";
    private const string Manager = "01JACT00000000000000000002";
    private const string HappyPath = "01JSCN00000000000000000001";
    private const string RefusedShip = "01JSCN00000000000000000002";
    private const string Draft = "01JSTA00000000000000000001";
    private const string Pending = "01JSTA00000000000000000004";
    private const string Cancel = "01JTRN00000000000000000010";
    private const string Submit = "01JPRX00000000000000000001";
    private const string IsLarge = "01JPRX00000000000000000011";
    private const string Amount = "01JATT00000000000000000011";
    private const string FirstStep = "01JSTP00000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ModelRoot => Fixtures.Path("models", "process-basics", ".maquettiste");

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    private static IReadOnlyList<Diagnostic> Evaluate(string schemaFile, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate(schemaFile, doc.RootElement, "model/x.json");
    }

    [Fact]
    public async Task Fixture_loads_and_validates_without_any_diagnostic_but_translation_progress()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        Assert.Empty(model.LoadDiagnostics);
        Assert.Single(model.All<Process>());
        Assert.Equal(4, model.All<Actor>().Count);
        Assert.Equal(2, model.All<Scenario>().Count);
        var report = await EngineServices.Create(h.Options).Validator.ValidateAsync(model, ValidationScope.All, null, Ct);
        Assert.All(report.Diagnostics, d => Assert.Equal("MQ7204", d.Rule)); // 'fr' declared, nothing translated yet

        var process = model.Get<Process>(Process)!;
        Assert.Equal(ProcessUse.Lifecycle, process.Use);
        Assert.Equal(StateType.Compound, process.States[2].Type);
        Assert.Equal(HistoryType.Deep, process.States[2].States[2].History);
        Assert.Equal(TransitionTrigger.InvokeDone, process.Transitions[4].Trigger);
        Assert.Equal(2, process.Transitions[5].Gate!.Required);
        Assert.Equal(InvokeType.HumanTask, process.States[2].States[1].Invoke[0].Type);
        Assert.Equal(Process, model.Get<Entity>(Order)!.Lifecycle);
        Assert.Equal(Process, model.Get<Diagram>(Diagram)!.Process);
        Assert.Equal(ActorType.ExternalSystem, model.Get<Actor>("01JACT00000000000000000004")!.Type);
        var scenario = model.Get<Scenario>(HappyPath)!;
        Assert.Equal(ScenarioOutcome.Final, scenario.Outcome);
        Assert.Equal(ScenarioStart.DefaultStart, scenario.Start!.At);
        Assert.False(scenario.Steps[3].Assume[IsLarge]);
        Assert.False(model.Get<Scenario>(RefusedShip)!.Steps[0].Expect!.Accepted);
    }

    [Theory]
    [InlineData("process-basics")]
    [InlineData("processes")] // the gate 3 fixture (section 8.1)
    public void Fixture_files_are_canonical_and_round_trip_through_the_records(string fixture)
    {
        var json = TestServices.Json;
        var root = Fixtures.Path("models", fixture, ".maquettiste");
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "model"), "*.json", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            var kind = JsonNode.Parse(bytes)!["kind"]!.GetValue<string>();
            Assert.True(KindInfo.TryGet(kind, out var info));
            var repoPath = ".maquettiste/" + Path.GetRelativePath(root, file).Replace('\\', '/');
            Assert.True(json.IsCanonical(bytes, info.SchemaFile, repoPath), repoPath + " is not canonical");
            Assert.Empty(TestServices.Schemas.Evaluate(info.SchemaFile, JsonDocument.Parse(bytes).RootElement, repoPath));

            // Record round trip: read into the record, serialize it and write it canonically: the same bytes.
            var element = (Element)JsonSerializer.Deserialize(bytes, info.ClrType, EngineJson.Options)!;
            var node = JsonSerializer.SerializeToNode(element, info.ClrType, EngineJson.Options)!;
            Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(json.Write(node, info.SchemaFile, repoPath)));
            kinds.Add(kind);
        }

        Assert.Superset(new HashSet<string>(["process", "actor", "scenario"], StringComparer.Ordinal), kinds);
    }

    [Fact]
    public void Canonical_form_keeps_meaningful_order_sorts_maps_and_omits_defaults()
    {
        const string Messy = """
            {"source":{"format":"xstate","extensions":{"/states/b":1,"/states/a":2}},"transitions":[{"external":false,"trigger":"event","source":"01JSTA00000000000000000002","id":"01JTRN00000000000000000002","targets":[]},{"id":"01JTRN00000000000000000001","source":"01JSTA00000000000000000001"}],
             "states":[{"name":"Zed","id":"01JSTA00000000000000000002","type":"atomic","history":"shallow","entry":[],"states":[]},{"id":"01JSTA00000000000000000001","name":"Alpha","type":"final"}],
             "context":[{"id":"01JATT00000000000000000002","name":"b","type":"string","order":2},{"id":"01JATT00000000000000000001","name":"a","type":"string","order":1}],
             "use":"orchestration","name":"P","id":"01JPRC00000000000000000009","kind":"process","events":[],"guards":[]}
            """;
        var written = Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(Messy)!, "process.json", ".maquettiste/model/processes/p.json"));
        var node = JsonNode.Parse(written)!.AsObject();

        Assert.Equal(["$schema", "kind", "id", "name", "context", "states", "transitions", "source"], node.Select(p => p.Key).ToArray());
        Assert.Equal(["Zed", "Alpha"], node["states"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToArray()); // document order
        Assert.Equal(["id", "name"], node["states"]![0]!.AsObject().Select(p => p.Key).ToArray()); // defaults omitted
        Assert.Equal(["01JTRN00000000000000000002", "01JTRN00000000000000000001"], node["transitions"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToArray()); // priority
        Assert.Equal(["id", "source"], node["transitions"]![0]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal(["a", "b"], node["context"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).ToArray()); // x-sort: order
        Assert.Equal(["/states/a", "/states/b"], node["source"]!["extensions"]!.AsObject().Select(p => p.Key).ToArray()); // maps ordinal
        Assert.True(TestServices.Json.IsCanonical(Encoding.UTF8.GetBytes(written), "process.json", ".maquettiste/model/processes/p.json"));
    }

    [Theory]
    [InlineData("process.json", """{"kind":"process","id":"01JPRC00000000000000000009","name":"P","states":[]}""", "/states")]
    [InlineData("process.json", """{"kind":"process","id":"01JPRC00000000000000000009","name":"P","use":"flow","states":[{"id":"01JSTA00000000000000000001","name":"A"}]}""", "/use")]
    [InlineData("process.json", """{"kind":"process","id":"01JPRC00000000000000000009","name":"P","states":[{"id":"01JSTA00000000000000000001","name":"A","type":"junction"}]}""", "/states/0/type")]
    [InlineData("process.json", """{"kind":"process","id":"01JPRC00000000000000000009","name":"P","states":[{"id":"01JSTA00000000000000000001","name":"A"}],"transitions":[{"id":"01JTRN00000000000000000001","source":"01JSTA00000000000000000001","trigger":"timer"}]}""", "/transitions/0/trigger")]
    [InlineData("process.json", """{"kind":"process","id":"01JPRC00000000000000000009","name":"P","states":[{"id":"01JSTA00000000000000000001","name":"A"}],"transitions":[{"id":"01JTRN00000000000000000001","source":"01JSTA00000000000000000001","gate":{"id":"01JPRX00000000000000000001","name":"g","required":0,"signers":["01JACT00000000000000000001"],"meanings":[{"id":"01JPRX00000000000000000002","name":"m"}]}}]}""", "/transitions/0/gate/required")]
    [InlineData("process.json", """{"kind":"process","id":"01JPRC00000000000000000009","name":"P","states":[{"id":"01JSTA00000000000000000001","name":"A"}],"transitions":[{"id":"01JTRN00000000000000000001","source":"01JSTA00000000000000000001","gate":{"id":"01JPRX00000000000000000001","name":"g","signers":[],"meanings":[]}}]}""", "/transitions/0/gate/signers")]
    [InlineData("process.json", """{"kind":"process","id":"01JPRC00000000000000000009","name":"P","states":[{"id":"01JSTA00000000000000000001","name":"A","invoke":[{"id":"01JPRX00000000000000000001","name":"i"}]}]}""", "/states/0/invoke/0")]
    [InlineData("actor.json", """{"kind":"actor","id":"01JACT00000000000000000009","name":"A"}""", "")]
    [InlineData("actor.json", """{"kind":"actor","id":"01JACT00000000000000000009","name":"A","type":"robot"}""", "/type")]
    [InlineData("scenario.json", """{"kind":"scenario","id":"01JSCN00000000000000000009","name":"S","process":"01JPRC00000000000000000009","steps":[]}""", "/steps")]
    [InlineData("scenario.json", """{"kind":"scenario","id":"01JSCN00000000000000000009","name":"S","process":"01JPRC00000000000000000009","steps":[{"id":"01JSTP00000000000000000001","input":"click"}]}""", "/steps/0/input")]
    [InlineData("scenario.json", """{"kind":"scenario","id":"01JSCN00000000000000000009","name":"S","process":"01JPRC00000000000000000009","steps":[{"id":"01JSTP00000000000000000001","payload":{"amount":1}}]}""", "/steps/0/payload")]
    [InlineData("scenario.json", """{"kind":"scenario","id":"01JSCN00000000000000000009","name":"S","process":"01JPRC00000000000000000009","outcome":"done","steps":[{"id":"01JSTP00000000000000000001"}]}""", "/outcome")]
    public void Schemas_reject_what_the_design_rules_out(string schemaFile, string json, string pointer)
    {
        var diagnostics = Evaluate(schemaFile, json);

        Assert.Contains(diagnostics, d => d.JsonPointer == pointer || (d.JsonPointer ?? "").StartsWith(pointer + "/", StringComparison.Ordinal));
    }

    [Fact]
    public void Schemas_accept_the_new_scopes_kinds_and_source_extensions()
    {
        Assert.Empty(Evaluate("actor.json", """{"kind":"actor","id":"01JACT00000000000000000009","name":"Buyer","type":"person","source":{"format":"xstate","extensions":{"/tags":["x"]}}}"""));
        Assert.Empty(Evaluate("diagram.json", """{"kind":"diagram","id":"01JDGM00000000000000000009","name":"D","process":"01JPRC00000000000000000009"}"""));
        Assert.Empty(Evaluate("extension.json", """{"name":"x","appliesTo":{"kinds":["process","actor","scenario","state","transition","event"]},"properties":{}}"""));
        Assert.Empty(Evaluate("stereotype.json", """{"kind":"stereotype","id":"01JSTR00000000000000000009","key":"persona","name":"Persona","appliesTo":["actor","state"]}"""));
        foreach (var scope in new[] { "each process", "each actor", "each scenario" })
        {
            var manifest = File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "schemas", "v1", "pack.json"));
            Assert.Matches(PackScopePattern(manifest), scope);
        }
    }

    private static string PackScopePattern(string packSchema)
    {
        var start = packSchema.IndexOf("\"^(model|each (", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = packSchema.IndexOf('"', start + 1);
        return JsonSerializer.Deserialize<string>(packSchema[start..(end + 1)])!;
    }

    [Fact]
    public async Task Index_rows_and_sub_element_entries_describe_processes_actors_and_scenarios()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var rows = model.Index.Summaries.ToDictionary(s => s.Id, StringComparer.Ordinal);

        var process = rows[Process];
        Assert.Equal(("process", "lifecycle", Order, 14, Package), (process.Kind, process.Use, process.Subject, process.StateCount, process.Package));
        Assert.Equal(".maquettiste/model/processes/order-lifecycle.json", process.Path);
        Assert.Equal(("actor", "person", (string?)null), (rows[Clerk].Kind, rows[Clerk].ActorType, rows[Clerk].Package));
        Assert.Equal("role", rows[Manager].ActorType);
        var scenario = rows[HappyPath];
        Assert.Equal(("scenario", Process, 5), (scenario.Kind, scenario.Process, scenario.StepCount));
        Assert.Equal(".maquettiste/model/scenarios/order-lifecycle/happy-path.json", scenario.Path);
        Assert.Null(rows[Order].StateCount);
        Assert.Null(rows[Order].Process);
        // A process diagram names its process on its row, so the Diagrams explorer labels it without reading it.
        Assert.Equal(("diagram", Process, 2), (rows["01JDGM00000000000000000001"].Kind, rows["01JDGM00000000000000000001"].Process, rows["01JDGM00000000000000000001"].MemberCount));

        foreach (var (id, kind) in new[]
        {
            (Draft, "state"), ("01JSTA00000000000000000013", "state"), (Cancel, "transition"), (Submit, "event"), (IsLarge, "guard"),
            ("01JPRX00000000000000000021", "action"), ("01JPRX00000000000000000031", "invoke"), ("01JPRX00000000000000000041", "gate"),
            ("01JPRX00000000000000000051", "meaning"), (FirstStep, "step"), (Amount, "attribute"), ("01JATT00000000000000000031", "attribute"),
        })
        {
            Assert.True(model.TryGetEntry(id, out var entry), id);
            Assert.Equal(kind, entry.Kind);
        }

        // References resolve to sub-elements, map keys included, and the stereotype of an actor is a reference to its definition.
        Assert.Contains(model.ReferencesTo(Pending), r => r.FromElementId == HappyPath && r.JsonPointer == "/steps/0/expect/states/0");
        Assert.Contains(model.ReferencesTo(Amount), r => r.FromElementId == HappyPath && r.JsonPointer == "/start/context/" + Amount);
        Assert.Contains(model.ReferencesTo(IsLarge), r => r.FromElementId == HappyPath && r.JsonPointer == "/steps/3/assume/" + IsLarge);
        Assert.Contains(model.ReferencesTo(Process), r => r.FromElementId == HappyPath && r.Owning);
        Assert.Contains(model.ReferencesTo(Process), r => r.FromElementId == Order && !r.Owning);
        Assert.Contains(model.ReferencesTo("01JSTR00000000000000000001"), r => r.FromElementId == Clerk);
    }

    [Fact]
    public void Index_format_moved_with_the_row_shape() =>
        Assert.Equal("maquettiste-index/e9", ModelReads.IndexFormat);

    [Fact]
    public async Task A_wrong_kind_or_dangling_sub_element_reference_is_reported()
    {
        var (h, store) = await OpenAsync(harness =>
        {
            var path = harness.Model("model/scenarios/order-lifecycle/refused-ship.json");
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            node["steps"]![0]!["event"] = Draft; // a state, not an event
            node["steps"]![0]!["actor"] = "01JACT00000000000000000099"; // no such actor
            File.WriteAllText(path, node.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        var report = await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct);

        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ2002" && d.JsonPointer == "/steps/0/event");
        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ2001" && d.JsonPointer == "/steps/0/actor");
    }

    [Fact]
    public async Task Deleting_a_process_deletes_its_scenarios_in_the_same_save()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var process = store.Current!.GetDocument(Process)!;

        // The entity's lifecycle and the diagram are other references: they refuse the delete; the owned scenarios do not.
        var refused = await store.DeleteAsync(Process, process.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
        Assert.Equal([Diagram, Order], refused.Referrers.Select(r => r.FromElementId).Distinct().Order(StringComparer.Ordinal).ToArray());

        var result = await store.DeleteAsync(Process, process.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Contains(HappyPath, result.Changes!.Deleted);
        Assert.Contains(RefusedShip, result.Changes.Deleted);
        Assert.False(h.Exists("model/scenarios/order-lifecycle/happy-path.json"));
        Assert.False(h.Exists("model/scenarios/order-lifecycle/refused-ship.json"));
        Assert.Null(store.Current!.GetDocument(HappyPath));
        Assert.Null(store.Current.Get<Entity>(Order)!.Lifecycle);
    }

    [Fact]
    public async Task A_process_whose_only_referrers_are_its_scenarios_deletes_under_refuse()
    {
        const string Solo = "01JPRC00000000000000000002";
        const string SoloScenario = "01JSCN00000000000000000003";
        var (h, store) = await OpenAsync(harness =>
        {
            harness.Write("model/processes/solo.json", $$$"""
                {"kind":"process","id":"{{{Solo}}}","name":"Solo","states":[{"id":"01JSTA00000000000000000101","name":"Only","type":"final"}]}
                """);
            harness.Write("model/scenarios/solo/start.json", $$$"""
                {"kind":"scenario","id":"{{{SoloScenario}}}","name":"Start","process":"{{{Solo}}}","steps":[{"id":"01JSTP00000000000000000101","input":"time","after":"PT1M","expect":{"states":["01JSTA00000000000000000101"]}}],"outcome":"final"}
                """);
        });
        using var _ = h;
        await using var __ = store;
        Assert.Equal(".maquettiste/model/scenarios/solo/start.json", store.Current!.GetDocument(SoloScenario)!.Path);

        var result = await store.DeleteAsync(Solo, store.Current.GetDocument(Solo)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal([Solo, SoloScenario], result.Changes!.Deleted.Order(StringComparer.Ordinal).ToArray());
        Assert.False(h.Exists("model/scenarios/solo/start.json"));
    }

    [Fact]
    public async Task A_scenario_outside_its_process_folder_is_MQ1005()
    {
        var (h, store) = await OpenAsync(harness =>
            File.Move(harness.Model("model/scenarios/order-lifecycle/refused-ship.json"), harness.Model("model/scenarios/refused-ship.json")));
        using var _ = h;
        await using var __ = store;

        var d = Assert.Single(store.Current!.LoadDiagnostics, d => d.Rule == "MQ1005");
        Assert.Contains("model/scenarios/order-lifecycle/refused-ship.json", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Localizable_nodes_cover_the_process_parts_and_land_in_the_package_shard()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var l10n = store.Current!.Localization;

        Assert.Equal((Package, "process", "Order lifecycle"), (l10n.Nodes[Process].Scope, l10n.Nodes[Process].Kind, l10n.Nodes[Process].DisplayName));
        Assert.Equal(Package, l10n.Nodes[HappyPath].Scope); // a scenario follows its process's package
        Assert.Equal(LocalizationIndex.RootScope, l10n.Nodes[Clerk].Scope); // actors are not in a domain
        Assert.Equal("Sales manager", l10n.Nodes[Manager].DisplayName);
        Assert.Equal(("state", "Draft", Package), (l10n.Nodes[Draft].Kind, l10n.Nodes[Draft].DisplayName, l10n.Nodes[Draft].Scope));
        Assert.Equal(("cancelled", (string?)null), (l10n.Nodes[Cancel].DisplayName, l10n.Nodes[Cancel].Description)); // the edge label only
        Assert.Equal(((string?)null, "The clerk submits the order."), (l10n.Nodes[FirstStep].DisplayName, l10n.Nodes[FirstStep].Description));
        Assert.Equal("Submit", l10n.Nodes[Submit].DisplayName);
        Assert.Equal("Reviewed as manager", l10n.Nodes["01JPRX00000000000000000051"].DisplayName);
        Assert.Equal("attribute", l10n.Nodes["01JATT00000000000000000021"].Kind); // an event payload field
        Assert.Null(l10n.Nodes["01JTRN00000000000000000001"].DisplayName); // no label: nothing to translate
    }

    [Fact]
    public async Task A_process_display_name_translation_is_written_to_the_package_shard()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var hashes = (await store.GetTranslationsAsync("fr", null, null, Ct)).ShardHashes;

        var result = await store.SaveTranslationsAsync("fr",
            [new TranslationEdit(Process, "displayName", "Cycle de vie de la commande"), new TranslationEdit(Clerk, "displayName", "Commis")],
            hashes, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var sales = JsonNode.Parse(h.Read("model/locales/fr/sales.json"))!["entries"]!.AsObject();
        Assert.Equal("Cycle de vie de la commande", sales[Process]!["displayName"]!.GetValue<string>());
        var root = JsonNode.Parse(h.Read("model/locales/fr/_root.json"))!["entries"]!.AsObject();
        Assert.True(root.ContainsKey(Clerk));
        Assert.Equal("Cycle de vie de la commande", store.Current!.Localization.Text("fr", Process, "displayName"));
    }
}
