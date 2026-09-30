using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The XState projection (phase-3-design.md section 5, gate 3 criteria 6 and 7): every fixture process exported then imported gives
/// the same canonical file and the same second export; export is byte-deterministic; the import samples under
/// <c>tests/fixtures/xstate</c> cover parallel states, history, delays, invokes, choice, inline functions and unknown keys; and
/// MQ9401 to MQ9406 each have a failing and a passing case.
/// </summary>
public sealed class XStateProjectionTests
{
    private const string Sales = "01JQPRC0000000000000000001";
    private const string Purchase = "01JQPRC0000000000000000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> FixtureProcesses => new()
    {
        { "processes", "sales-order-lifecycle.json" },
        { "processes", "purchase-approval.json" },
        { "process-basics", "order-lifecycle.json" },
    };

    private static string ProcessFile(string fixture, string file) =>
        Fixtures.Path("models", fixture, ".maquettiste", "model", "processes", file);

    private static Process Read(byte[] bytes) => JsonSerializer.Deserialize<Process>(bytes, EngineJson.Options)!;

    private static XStateExport Export(Process process, ModelSnapshot? model = null) =>
        XStateProjection.Export(process, TestServices.Json, model is null ? null : XStateExportOptions.For(model));

    private static XStateImport Import(string config, XStateImportOptions? options = null) =>
        XStateProjection.Import(config, TestServices.Json, (options ?? new()) with { Ids = options?.Ids ?? new SequentialIdGenerator(7) });

    private static string Sample(string name) => File.ReadAllText(Fixtures.Path("xstate", name));

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync()
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "processes");
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    private static string[] Rules(IEnumerable<Diagnostic> diagnostics) => [.. diagnostics.Select(d => d.Rule).Distinct().Order(StringComparer.Ordinal)];

    // ---- round trip (gate 3 criterion 7) and determinism (criterion 6) ----

    [Theory]
    [MemberData(nameof(FixtureProcesses))]
    public void Every_fixture_process_round_trips_byte_identically(string fixture, string file)
    {
        var bytes = File.ReadAllBytes(ProcessFile(fixture, file));
        var first = Export(Read(bytes));
        var imported = Import(first.Json);

        Assert.Empty(imported.Diagnostics);
        Assert.Empty(imported.Created);
        Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(imported.File!));
        Assert.Equal(first.Json, Export(imported.Process!).Json);
    }

    [Theory]
    [InlineData(Sales)]
    [InlineData(Purchase)]
    public async Task Fixture_processes_round_trip_into_themselves_within_the_model(string id)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var process = model.Get<Process>(id)!;
        var original = TestServices.Json.Serialize(process, "process.json", ".maquettiste/model/processes/p.json");
        var export = Export(process, model);

        var imported = Import(export.Json, new() { Model = model, Into = process });

        Assert.DoesNotContain(imported.Diagnostics, d => d.Severity != DiagnosticSeverity.Info);
        Assert.Empty(imported.Created);
        Assert.Empty(imported.Removed);
        Assert.Equal(Encoding.UTF8.GetString(original), Encoding.UTF8.GetString(imported.File!));
        Assert.Equal(export.Json, Export(imported.Process!, model).Json);
    }

    [Fact]
    public void Export_is_deterministic_and_keys_follow_the_fixed_order()
    {
        var bytes = File.ReadAllBytes(ProcessFile("processes", "sales-order-lifecycle.json"));
        var runs = Enumerable.Range(0, 5).Select(_ => Export(Read(bytes)).Json).Distinct().ToList();
        Assert.Single(runs);
        Assert.EndsWith("}\n", runs[0], StringComparison.Ordinal);

        var config = JsonNode.Parse(runs[0])!.AsObject();
        Assert.Equal(["id", "initial", "states", "context", "meta"], config.Select(p => p.Key).ToArray());
        var order = XStateProjection.KeyOrder.ToList();
        foreach (var node in Objects(config))
        {
            var keys = node.Select(p => p.Key).Where(k => order.Contains(k) || k == "meta").ToList();
            if (keys.Contains("meta"))
                Assert.Equal("meta", node.Last().Key);
            var known = keys.Where(k => k != "meta").Select(k => order.IndexOf(k)).ToList();
            Assert.Equal(known.Order(), known);
        }

        // Absolute targets, milliseconds with the ISO duration in meta, the choice type and the gate in meta.
        var states = config["states"]!;
        Assert.Equal("#01JQSTA0000000000000000002", states["Draft"]!["on"]!["submit"]![0]!["target"]!.GetValue<string>());
        var overdue = states["Fulfilment"]!["states"]!["Processing"]!["states"]!["Payment"]!["states"]!["AwaitingPayment"]!["after"]!["2592000000"]![0]!;
        Assert.Equal("P30D", overdue["meta"]!["maquettiste"]!["after"]!.GetValue<string>());
        Assert.Equal("choice", states["CreditCheck"]!["meta"]!["maquettiste"]!["type"]!.GetValue<string>());
        Assert.NotNull(states["CreditReview"]!["on"]!["approveCredit"]![0]!["meta"]!["maquettiste"]!["gate"]);
        Assert.Equal("deep", states["Fulfilment"]!["states"]!["Resume"]!["history"]!.GetValue<string>());
        Assert.Equal(0, config["context"]!["total"]!.GetValue<int>());
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                yield return o;
                foreach (var (key, value) in o)
                {
                    if (key is "meta" or "context")
                        continue;
                    foreach (var child in Objects(value))
                        yield return child;
                }

                break;
            case JsonArray a:
                foreach (var child in a.SelectMany(Objects))
                    yield return child;
                break;
        }
    }

    // ---- import samples ----

    [Fact]
    public void Parallel_sample_imports_regions_done_and_reenter()
    {
        var result = Import(Sample("parallel.json"));
        Assert.Empty(result.Diagnostics);
        var p = result.Process!;
        Assert.Equal("Upload", p.Name);
        var working = p.States[1];
        Assert.Equal(StateType.Parallel, working.Type);
        Assert.Equal(["Files", "Checks"], working.States.Select(s => s.Name).ToArray());
        Assert.Equal(StateType.Compound, working.States[0].Type);
        Assert.Equal(working.States[0].States[0].Id, working.States[0].Initial);
        var done = p.Transitions.Single(t => t.Trigger == TransitionTrigger.Done);
        Assert.Equal(working.Id, done.Source);
        Assert.Equal(p.States[2].Id, Assert.Single(done.Targets));
        var abort = p.Transitions.Single(t => t.Event == p.Events.Single(e => e.Name == "abort").Id);
        Assert.True(abort.External);
        Assert.Equal(p.States[0].Id, Assert.Single(abort.Targets));
        Assert.Equal(["retries", "label", "ratio", "enabled", "extra"], p.Context.Select(a => a.Name).ToArray());
        Assert.Equal(["int32", "string", "decimal", "bool", "json"], p.Context.Select(a => a.Type.Builtin!).ToArray());
        Assert.Null(p.Context[4].Default);
        Assert.Equal("recordFile", Assert.Single(p.Actions).Name);
        Assert.Equal("xstate", p.Source!.Format);
        Assert.Equal("Upload", p.Source.Name);
    }

    [Fact]
    public void History_sample_imports_deep_and_shallow_history_with_default_targets()
    {
        var result = Import(Sample("history.json"));
        Assert.Empty(result.Diagnostics);
        var editing = result.Process!.States[0];
        var last = editing.States.Single(s => s.Name == "Last");
        Assert.Equal((StateType.History, HistoryType.Deep, editing.States[0].Id), (last.Type, last.History, last.DefaultTarget));
        var shallow = editing.States.Single(s => s.Name == "Shallow");
        Assert.Equal((StateType.History, HistoryType.Shallow, (string?)null), (shallow.Type, shallow.History, shallow.DefaultTarget));
        var resume = result.Process.Transitions.Where(t => t.Source == result.Process.States[1].Id).ToList();
        Assert.Equal([last.Id, shallow.Id], resume.Select(t => t.Targets.Single()).ToArray());
    }

    [Fact]
    public void Delays_sample_maps_milliseconds_named_delays_and_stub_durations()
    {
        var result = Import(Sample("delays.json"));
        var afters = result.Process!.Transitions.Where(t => t.Trigger == TransitionTrigger.After).Select(t => t.After!).ToArray();
        Assert.Equal(["PT1.5S", "PT1M30S", "PT0S", "P1D"], afters);
        Assert.Equal(["MQ9401", "MQ9404"], Rules(result.Diagnostics)); // delays kept opaque; someDelay has no value
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ9404" && d.JsonPointer == "/states/Waiting/after/someDelay/0");
    }

    [Fact]
    public void Invokes_sample_maps_services_done_error_and_keeps_input_opaque()
    {
        var result = Import(Sample("invokes.json"));
        var p = result.Process!;
        var charge = Assert.Single(p.States[0].Invoke);
        Assert.Equal(("chargeCard", InvokeType.Service), (charge.Name, charge.Type));
        var fromPaying = p.Transitions.Where(t => t.Source == p.States[0].Id).ToList();
        Assert.Equal([TransitionTrigger.InvokeDone, TransitionTrigger.InvokeError, TransitionTrigger.InvokeError], fromPaying.Select(t => t.Trigger).ToArray());
        Assert.All(fromPaying, t => Assert.Equal(charge.Id, t.Invoke));
        Assert.Equal("canRetry", p.Guards.Single(g => g.Id == fromPaying[1].Guard).Name);
        Assert.Equal(["sendReceipt", "notifyWarehouse"], p.States[1].Invoke.Select(i => i.Name).ToArray());
        Assert.Equal(["MQ9401"], Rules(result.Diagnostics));
        Assert.True(p.Source!.Extensions.ContainsKey("/states/Paid/invoke/1/input"));
    }

    [Fact]
    public async Task Invoke_source_naming_a_model_process_becomes_a_sub_process()
    {
        const string Config = """{ "id": "Outer", "initial": "A", "states": { "A": { "invoke": { "src": "PurchaseApproval" } } } }""";
        var model = await OpenModelAsync();
        var invoke = Assert.Single(Import(Config, new() { Model = model }).Process!.States[0].Invoke);
        Assert.Equal((InvokeType.Process, Purchase), (invoke.Type, invoke.Process));
    }

    [Fact]
    public void Choice_sample_keeps_priority_and_combines_guards()
    {
        var result = Import(Sample("choice.json"));
        var p = result.Process!;
        var decide = p.States[1];
        var always = p.Transitions.Where(t => t.Source == decide.Id).ToList();
        Assert.All(always, t => Assert.Equal(TransitionTrigger.Always, t.Trigger));
        Assert.Equal(["Urgent", "Normal", "Normal", "Backlog"], always.Select(t => p.States.Single(s => s.Id == t.Targets[0]).Name).ToArray());
        Assert.Null(always[3].Guard);
        var combined = p.Guards.Single(g => g.Id == always[1].Guard);
        Assert.Equal("always_to_normal_guard", combined.Name);
        Assert.Null(combined.Expression); // the parts are stubs
        Assert.Equal("hasPriority", p.Guards.Single(g => g.Id == always[2].Guard).Name);
        Assert.Equal(["MQ9401", "MQ9404"], Rules(result.Diagnostics));
        Assert.True(p.Source!.Extensions.ContainsKey("/states/Decide/always/2/guard"));
    }

    [Fact]
    public void Guard_combinators_combine_expressions_when_every_part_has_one()
    {
        const string Config = """
            { "id": "M", "initial": "A", "states": { "A": { "on": { "go": { "target": "B", "guard": { "type": "or", "guards": ["small", { "type": "not", "guards": ["blocked"] }] } } } }, "B": {} },
              "meta": { "maquettiste": { "guards": [ { "id": "01JQPRX0000000000000000901", "name": "small", "expression": "context.n < 3" },
                                                     { "id": "01JQPRX0000000000000000902", "name": "blocked", "expression": "context.blocked" } ] } } }
            """;
        var result = Import(Config);
        var guard = result.Process!.Guards.Single(g => g.Id == result.Process.Transitions[0].Guard);
        Assert.Equal(("go_to_b_guard", "(context.n < 3) || (!((context.blocked)))"), (guard.Name, guard.Expression));
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ9404");
    }

    [Fact]
    public void Inline_functions_become_named_stubs()
    {
        var result = Import(Sample("inline-functions.json"));
        var p = result.Process!;
        Assert.Equal(["MQ9402"], Rules(result.Diagnostics));
        Assert.Equal(5, result.Diagnostics.Count); // entry, guard, action, src, delay
        var guard = Assert.Single(p.Guards);
        Assert.Equal("submit_to_review_guard", guard.Name);
        Assert.Null(guard.Expression);
        Assert.Equal("```js\n({ context }) => context.amount > 0\n```", guard.Description!.Text);
        Assert.Equal(["draft_entry_action", "notify", "submit_to_review_action"], p.Actions.Select(a => a.Name).ToArray());
        var invoke = Assert.Single(p.States[1].Invoke);
        Assert.Equal(("review_invoke", InvokeType.Service), (invoke.Name, invoke.Type));
        Assert.Equal("PT0S", p.Transitions.Single(t => t.Trigger == TransitionTrigger.After).After);
    }

    [Fact]
    public void Unknown_config_is_kept_opaque_and_written_back_on_export()
    {
        var result = Import(Sample("unknown-keys.json"));
        var p = result.Process!;
        Assert.Equal(["MQ9401"], Rules(result.Diagnostics));
        Assert.Equal(
            ["/meta/owner", "/states/Closed/output", "/states/Open/meta/color", "/states/Open/on/*", "/states/Open/on/close/0/actions/0", "/states/Open/tags", "/version"],
            p.Source!.Extensions.Keys.ToArray());
        Assert.Equal("Waiting for an agent.", p.States[0].Description!.Text);
        Assert.Equal("sendTo", Assert.Single(p.Actions).Name);

        var export = JsonNode.Parse(Export(p).Json)!;
        Assert.Equal("1.2", export["version"]!.GetValue<string>());
        Assert.Equal("support", export["meta"]!["owner"]!.GetValue<string>());
        Assert.Equal("green", export["states"]!["Open"]!["meta"]!["color"]!.GetValue<string>());
        Assert.Equal("audit", export["states"]!["Open"]!["on"]!["close"]![0]!["actions"]![0]!["to"]!.GetValue<string>());
        Assert.True(export["states"]!["Closed"]!["output"]!["resolved"]!.GetValue<bool>());
        Assert.Equal("meta", export["states"]!["Open"]!.AsObject().Last().Key);

        // A second import of the export gives the same process: the kept data sits at the same pointers.
        var again = Import(Export(p).Json);
        Assert.Equal(Encoding.UTF8.GetString(result.File!), Encoding.UTF8.GetString(again.File!));
    }

    [Theory]
    [InlineData("parallel.json")]
    [InlineData("history.json")]
    [InlineData("delays.json")]
    [InlineData("invokes.json")]
    [InlineData("choice.json")]
    [InlineData("inline-functions.json")]
    [InlineData("unknown-keys.json")]
    public void Every_sample_is_stable_after_its_first_import(string sample)
    {
        var first = Import(Sample(sample));
        var export = Export(first.Process!).Json;
        var second = Import(export);
        Assert.DoesNotContain(second.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(second.Created);
        Assert.Equal(Encoding.UTF8.GetString(first.File!), Encoding.UTF8.GetString(second.File!));
        Assert.Equal(export, Export(second.Process!).Json);
    }

    [Fact]
    public void Created_ids_derive_from_the_path_within_one_import_and_are_fresh_across_imports()
    {
        var a = Import(Sample("parallel.json"), new() { Ids = new SequentialIdGenerator(1) });
        var b = Import(Sample("parallel.json"), new() { Ids = new SequentialIdGenerator(1) });
        var c = Import(Sample("parallel.json"), new() { Ids = new SequentialIdGenerator(2) });
        Assert.Equal(a.File, b.File);
        Assert.Empty(a.Created.Intersect(c.Created));
        Assert.All(a.Created, id => Assert.True(IdFormat.IsValid(id)));
        Assert.Equal(a.Created.Count, a.Created.Distinct().Count());
        Assert.Contains(a.Process!.Id, a.Created);
    }

    [Fact]
    public void Import_into_keeps_ids_matched_by_path_and_lists_removed_nodes()
    {
        var sales = Read(File.ReadAllBytes(ProcessFile("processes", "sales-order-lifecycle.json")));
        var config = JsonNode.Parse(Export(sales).Json)!.AsObject();
        StripMeta(config);
        ((JsonObject)config["states"]!).Remove("OnHold");
        foreach (var target in new[] { "Fulfilment" })
            ((JsonObject)config["states"]![target]!["on"]!).Remove("hold");
        var result = Import(config.ToJsonString(), new() { Into = sales });
        var p = result.Process!;
        Assert.Equal(sales.Id, p.Id);
        Assert.Equal(sales.States[0].Id, p.States[0].Id);
        Assert.Equal(sales.Transitions[0].Id, p.Transitions[0].Id);
        Assert.Equal(sales.Guards.Select(g => (g.Id, g.Expression)), p.Guards.Select(g => (g.Id, g.Expression)));
        Assert.Equal(JsonSerializer.Serialize(sales.Transitions[3].Gate, EngineJson.Options), JsonSerializer.Serialize(p.Transitions[3].Gate, EngineJson.Options));
        Assert.Equal(sales.Events.Select(e => e.Id).Where(id => id != "01JQPRX0000000000000000007" && id != "01JQPRX0000000000000000008"), p.Events.Select(e => e.Id));

        // States matched by path: a config with no ids re-imported over its first import keeps every id.
        var first = Import(Minimal);
        var again = Import(Minimal, new() { Into = first.Process, Ids = new SequentialIdGenerator(99) });
        Assert.Equal(Encoding.UTF8.GetString(first.File!), Encoding.UTF8.GetString(again.File!));
        Assert.Empty(again.Created);
        Assert.Contains("01JQSTA0000000000000000015", result.Removed); // OnHold
        Assert.Contains("01JQTRN0000000000000000012", result.Removed); // hold
        Assert.Contains("01JQTRN0000000000000000013", result.Removed); // release
    }

    private static void StripMeta(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                o.Remove("meta");
                foreach (var (_, value) in o.ToList())
                    StripMeta(value);
                break;
            case JsonArray a:
                foreach (var child in a)
                    StripMeta(child);
                break;
        }
    }

    // ---- MQ9401 to MQ9406: a failing and a passing case each ----

    private const string Minimal = """{ "id": "M", "initial": "A", "states": { "A": { "on": { "go": "B" } }, "B": { "type": "final" } } }""";

    [Fact]
    public void MQ9401_unknown_config_and_its_passing_case()
    {
        Assert.Contains(Import("""{ "id": "M", "initial": "A", "states": { "A": { "tags": ["x"] } } }""").Diagnostics, d => d.Rule == "MQ9401");
        Assert.DoesNotContain(Import(Minimal).Diagnostics, d => d.Rule == "MQ9401");
    }

    [Fact]
    public void MQ9402_inline_function_and_its_passing_case()
    {
        Assert.Contains(Import("""{ "id": "M", "initial": "A", "states": { "A": { "on": { "go": { "target": "B", "guard": "() => true" } } }, "B": {} } }""").Diagnostics,
            d => d.Rule == "MQ9402");
        Assert.DoesNotContain(Import("""{ "id": "M", "initial": "A", "states": { "A": { "on": { "go": { "target": "B", "guard": "ok" } } }, "B": {} } }""").Diagnostics,
            d => d.Rule == "MQ9402");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "id": "M" }""")]
    [InlineData("""{ "id": "M", "initial": "A", "states": { "A": { "on": { "go": "Nowhere" } } } }""")]
    [InlineData("""{ "id": "M", "initial": "Z", "states": { "A": {} } }""")]
    [InlineData("""{ "id": "M", "type": "parallel", "states": { "A": {}, "B": {} } }""")]
    [InlineData("""{ "id": "M", "initial": "A", "states": { "A": { "type": "sometimes" } } }""")]
    [InlineData("""{ "id": "M", "initial": "A", "states": { "A": {} }, "meta": { "maquettiste": { "initialImplied": 3 } } }""")]
    public void MQ9403_refused_input(string config)
    {
        var result = Import(config);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ9403");
        Assert.True(result.HasErrors);
        Assert.Null(result.File);
    }

    [Fact]
    public void MQ9403_passing_case() => Assert.False(Import(Minimal).HasErrors);

    [Fact]
    public async Task MQ9404_actor_not_in_the_model_is_dropped_and_its_passing_case()
    {
        var model = await OpenModelAsync();
        var sales = model.Get<Process>(Sales)!;
        var config = JsonNode.Parse(Export(sales, model).Json)!;
        var events = config["meta"]!["maquettiste"]!["events"]!.AsArray();
        events[1]!["actors"]!.AsArray().Add("01JQACT0000000000000000999");
        var dropped = Import(config.ToJsonString(), new() { Model = model, Into = sales });
        Assert.Contains(dropped.Diagnostics, d => d.Rule == "MQ9404" && d.Message.Contains("01JQACT0000000000000000999", StringComparison.Ordinal));
        Assert.Equal(sales.Events[1].Actors, dropped.Process!.Events[1].Actors);
        Assert.DoesNotContain(Import(Export(sales, model).Json, new() { Model = model, Into = sales }).Diagnostics, d => d.Rule == "MQ9404");
    }

    [Fact]
    public async Task MQ9405_id_of_another_kind_or_process_and_its_passing_case()
    {
        var model = await OpenModelAsync();
        var sales = model.Get<Process>(Sales)!;
        var purchase = model.Get<Process>(Purchase)!;
        var export = Export(sales, model).Json;

        // Into another process: every carried id belongs to the sales lifecycle.
        var wrongProcess = Import(export, new() { Model = model, Into = purchase });
        Assert.Contains(wrongProcess.Diagnostics, d => d.Rule == "MQ9405" && d.Message.Contains(Sales, StringComparison.Ordinal));
        Assert.True(wrongProcess.HasErrors);

        // A state carrying an event's id.
        var config = JsonNode.Parse(export)!;
        config["states"]!["Draft"]!["id"] = "01JQPRX0000000000000000001";
        var wrongKind = Import(config.ToJsonString(), new() { Model = model, Into = sales });
        Assert.Contains(wrongKind.Diagnostics, d => d.Rule == "MQ9405" && d.JsonPointer == "/states/Draft/id");
        Assert.NotEqual("01JQPRX0000000000000000001", wrongKind.Process!.States[0].Id);

        // Without into, a carried process id already in the model.
        Assert.Contains(Import(export, new() { Model = model }).Diagnostics, d => d.Rule == "MQ9405" && d.JsonPointer == "/meta/maquettiste/id");

        Assert.DoesNotContain(Import(export, new() { Model = model, Into = sales }).Diagnostics, d => d.Rule == "MQ9405");
    }

    [Fact]
    public void MQ9406_export_reports_what_went_into_meta()
    {
        var sales = Read(File.ReadAllBytes(ProcessFile("processes", "sales-order-lifecycle.json")));
        var info = Assert.Single(Export(sales).Diagnostics);
        Assert.Equal(("MQ9406", DiagnosticSeverity.Info, "/meta/maquettiste"), (info.Rule, info.Severity, info.JsonPointer));
        Assert.Contains("gates", info.Message, StringComparison.Ordinal);
        Assert.Contains("ISO durations", info.Message, StringComparison.Ordinal);
        // Passing case: an export has no other finding when every kept datum finds its place.
        Assert.DoesNotContain(Export(sales).Diagnostics, d => d.Rule != "MQ9406");
    }

    [Fact]
    public void Raises_travel_as_raise_entries_in_meta()
    {
        var process = Read(File.ReadAllBytes(ProcessFile("process-basics", "order-lifecycle.json")));
        process = process with { Actions = [process.Actions[0] with { Raises = [process.Events[4].Id] }, process.Actions[1]] };
        var export = Export(process);
        var raise = JsonNode.Parse(export.Json)!["meta"]!["maquettiste"]!["actions"]![0]!["raises"]![0]!;
        Assert.Equal("""{"type":"raise","event":"cancel"}""", raise.ToJsonString());
        Assert.Contains("raises", Assert.Single(export.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Equal([process.Events[4].Id], Import(export.Json).Process!.Actions[0].Raises);
    }

    private static async Task<ModelSnapshot> OpenModelAsync()
    {
        var (h, store) = await OpenAsync();
        using (h)
        {
            await using (store)
                return store.Current!;
        }
    }
}
