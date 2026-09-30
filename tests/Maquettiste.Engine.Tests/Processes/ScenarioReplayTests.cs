using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Scenario replay (phase-3-design.md sections 3, 4.1 and 4.5): the gate 3 fixture's 14 scenarios pass under the interpreter; MQ9301 to
/// MQ9306 each fail on a broken copy of a fixture scenario; validation replays scenarios in whole-model and scoped runs; the
/// <c>refresh-scenario</c> batch operation rewrites expectations from a replay; and the macrostep and expression budgets are measured.
/// </summary>
public sealed class ScenarioReplayTests
{
    private const string SmallOrder = "01JQSCN0000000000000000001";
    private const string RepeatSigner = "01JQSCN0000000000000000007";
    private const string CancelRefused = "01JQSCN0000000000000000006";
    private const string Reminder = "01JQSCN0000000000000000012";
    private const string Rejection = "01JQSCN0000000000000000013";
    private const string HappyPath = "01JQSCN0000000000000000008";
    private const string Sales = "01JQPRC0000000000000000001";
    private const string SmallOrderFile = "model/scenarios/sales-order-lifecycle/small-order.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "processes");
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        return (harness, store);
    }

    private static ScenarioReplay Replay(ModelSnapshot model, Scenario scenario)
    {
        using var runtime = new ProcessRuntime(model, 1, Ct);
        return ScenarioReplayer.Replay(scenario, runtime)!;
    }

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private static Scenario WithStep(Scenario s, int index, Func<ScenarioStep, ScenarioStep> change) =>
        s with { Steps = [.. s.Steps.Select((step, i) => i == index ? change(step) : step)] };

    [Fact]
    public async Task Every_fixture_scenario_passes_under_the_interpreter()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        var replays = model.All<Scenario>().Select(s => Replay(model, s)).ToList();

        Assert.Equal(14, replays.Count);
        Assert.All(replays, r => Assert.True(r.Passed && r.Diagnostics.Count == 0, r.Scenario.Name + ": " + string.Join(" | ", r.Diagnostics.Select(d => d.Message))));
        Assert.Equal(50, replays.Sum(r => r.Steps.Count));
        var happy = replays.Single(r => r.Scenario.Id == HappyPath);
        Assert.Equal([GateOutcome.Signed, GateOutcome.Signed, GateOutcome.Completed], happy.Steps.SelectMany(t => t.Audit).Select(a => a.Outcome));
        Assert.Equal("01JQATT0000000000000000204", Assert.Single(model.Get<Process>(happy.Scenario.Process)!.Transitions.Single(t => t.Gate is not null).Gate!.AuditAttributes).Id);
    }

    public static TheoryData<string, string, string> Broken => new()
    {
        { "MQ9301", RepeatSigner, "/steps/2/expect/accepted" },
        { "MQ9302", SmallOrder, "/steps/3/expect/states" },
        { "MQ9303", Reminder, "/steps/3/expect/context" },
        { "MQ9304", SmallOrder, "/outcome" },
        { "MQ9305", SmallOrder, "/steps/1" },
        { "MQ9306", CancelRefused, "/steps/3" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public async Task Each_scenario_rule_fails_on_a_broken_copy_of_a_passing_scenario(string rule, string scenarioId, string pointer)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var original = model.Get<Scenario>(scenarioId)!;
        Assert.True(Replay(model, original).Passed); // the passing case

        var broken = rule switch
        {
            "MQ9301" => WithStep(original, 2, s => s with { Expect = s.Expect! with { Accepted = true } }),
            "MQ9302" => WithStep(original, 3, s => s with { Expect = s.Expect! with { States = ["01JQSTA0000000000000000015"] } }),
            "MQ9303" => WithStep(original, 3, s => s with { Expect = s.Expect! with { Context = ImmutableDictionary<string, JsonElement>.Empty.Add("01JQATT0000000000000000202", J(2)) } }),
            "MQ9304" => original with { Outcome = ScenarioOutcome.Active },
            "MQ9305" => WithStep(original, 1, s => s with { Event = "01JQPRX0000000000000000014" }), // an event of the other process
            _ => WithStep(original, 3, s => s with { Assume = ImmutableDictionary<string, bool>.Empty }),
        };
        var replay = Replay(model, broken);

        var finding = Assert.Single(replay.Diagnostics);
        Assert.Equal((rule, pointer, scenarioId), (finding.Rule, finding.JsonPointer, finding.ElementId));
        Assert.Matches("; \\S.*\\.$", finding.Message);
        Assert.False(replay.Passed);
        Assert.Equal(rule is "MQ9305" or "MQ9306", !replay.Complete);
    }

    [Theory]
    [InlineData("time")]
    [InlineData("invoke")]
    [InlineData("payload-key")]
    [InlineData("payload-type")]
    [InlineData("meaning")]
    public async Task MQ9305_reports_each_inconsistent_step_field(string field)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var rejection = model.Get<Scenario>(Rejection)!;

        var broken = field switch
        {
            "time" => WithStep(rejection, 3, s => s with { Input = StepInput.Time, Event = null, After = null, Payload = ImmutableDictionary<string, JsonElement>.Empty }),
            "invoke" => WithStep(rejection, 3, s => s with { Input = StepInput.InvokeDone, Event = null, Invoke = "01JQPRX0000000000000000019", Payload = ImmutableDictionary<string, JsonElement>.Empty }),
            "payload-key" => WithStep(rejection, 3, s => s with { Payload = ImmutableDictionary<string, JsonElement>.Empty.Add("01JQATT0000000000000000201", J(1)) }),
            "payload-type" => WithStep(rejection, 3, s => s with { Payload = ImmutableDictionary<string, JsonElement>.Empty.Add("01JQATT0000000000000000203", J(5)) }),
            _ => WithStep(rejection, 3, s => s with { Meaning = "01JQPRX0000000000000000013" }),
        };

        var finding = Assert.Single(Replay(model, broken).Diagnostics);

        Assert.Equal("MQ9305", finding.Rule);
        Assert.StartsWith("/steps/3", finding.JsonPointer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MQ9305_reports_an_unparsable_start_instant_and_stops_the_replay()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var rejection = model.Get<Scenario>(Rejection)!;
        Assert.DoesNotContain(Replay(model, rejection with { Start = (rejection.Start ?? new ScenarioStart()) with { At = "2026-01-05T09:00:00Z" } }).Diagnostics, d => d.Rule == "MQ9305");

        var replay = Replay(model, rejection with { Start = (rejection.Start ?? new ScenarioStart()) with { At = "not-a-date" } });

        var finding = Assert.Single(replay.Diagnostics);
        Assert.Equal(("MQ9305", "/start/at"), (finding.Rule, finding.JsonPointer));
        Assert.False(replay.Complete);
    }

    [Fact]
    public async Task Validation_replays_scenarios_in_whole_model_and_scoped_runs_and_parses_expressions()
    {
        var (h, store) = await OpenAsync(x =>
        {
            var scenario = JsonNode.Parse(x.Read(SmallOrderFile))!.AsObject();
            scenario["steps"]![3]!["expect"]!["states"] = new JsonArray("01JQSTA0000000000000000015");
            x.Write(SmallOrderFile, scenario.ToJsonString());
            var process = JsonNode.Parse(x.Read("model/processes/purchase-approval.json"))!.AsObject();
            process["actions"]![0]!["expression"] = "({ reminders: context.reminders + })";
            x.Write("model/processes/purchase-approval.json", process.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;
        var validator = EngineServices.Create(h.Options).Validator;

        var whole = (await validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct)).Diagnostics;
        var scoped = (await validator.ValidateAsync(store.Current!, new ValidationScope([Sales]), null, Ct)).Diagnostics;

        var states = Assert.Single(whole, d => d.Rule == "MQ9302");
        Assert.Equal((SmallOrder, "/steps/3/expect/states"), (states.ElementId, states.JsonPointer));
        Assert.EndsWith("small-order.json", states.FilePath, StringComparison.Ordinal);
        Assert.Equal("/actions/0/expression", Assert.Single(whole, d => d.Rule == "MQ9501").JsonPointer);
        Assert.Contains(scoped, d => d.Rule == "MQ9302" && d.ElementId == SmallOrder); // a process change re-checks its scenarios
    }

    [Fact]
    public async Task Refresh_scenario_rewrites_expectations_from_a_replay()
    {
        string original = "";
        var (h, store) = await OpenAsync(x =>
        {
            original = x.Read(SmallOrderFile);
            var scenario = JsonNode.Parse(original)!.AsObject();
            scenario["steps"]![3]!["expect"]!["states"] = new JsonArray("01JQSTA0000000000000000015");
            scenario["steps"]![1]!["expect"]!["accepted"] = false;
            scenario.Remove("outcome");
            x.Write(SmallOrderFile, scenario.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        var result = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.RefreshScenario, SmallOrder, null, null)]), ChangeSource.Editor, Ct);

        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(original, h.Read(SmallOrderFile));
        var again = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.RefreshScenario, SmallOrder, null, null)]), ChangeSource.Editor, Ct);
        Assert.True(again.Outcome == SaveOutcome.Saved, string.Join(" | ", again.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.DoesNotContain((await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct)).Diagnostics,
            d => d.Severity != DiagnosticSeverity.Info);
    }

    [Theory]
    [InlineData(Sales, "is not a scenario")]
    [InlineData(CancelRefused, "cannot be replayed to its last step")]
    public async Task Refresh_scenario_is_refused_with_what_to_do(string id, string text)
    {
        var (h, store) = await OpenAsync(x =>
        {
            const string file = "model/scenarios/sales-order-lifecycle/cancel-refused-after-shipping.json";
            var scenario = JsonNode.Parse(x.Read(file))!.AsObject();
            scenario["steps"]![3]!.AsObject().Remove("assume");
            x.Write(file, scenario.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        var result = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.RefreshScenario, id, null, null)]), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var refusal = Assert.Single(result.Items.SelectMany(i => i.Diagnostics));
        Assert.Equal("MQ9019", refusal.Rule);
        Assert.Contains(text, refusal.Message, StringComparison.Ordinal);
        Assert.Matches("[;:] \\S.*\\.$", refusal.Message);
    }

    [Fact]
    public async Task Refresh_scenario_parses_from_a_batch()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var parsed = store.ParseBatch(System.Text.Encoding.UTF8.GetBytes($$"""{ "operations": [ { "op": "refresh-scenario", "id": "{{SmallOrder}}" } ] }"""));

        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(BatchOp.RefreshScenario, Assert.Single(parsed.Batch!.Operations).Op);
        Assert.Equal("refresh-scenario", RuleCatalog.Get("MQ9302").QuickFix);
    }

    [Fact]
    public async Task Macrostep_and_expression_budgets_measured_on_the_fixture()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var scenarios = model.All<Scenario>();
        using var runtime = new ProcessRuntime(model, 1, Ct);
        foreach (var s in scenarios)
            ScenarioReplayer.Replay(s, runtime); // warm: charts, compiled expressions, sandboxes

        var macro = new List<double>();
        for (var round = 0; round < 20; round++)
        {
            foreach (var s in scenarios)
            {
                var interpreter = new StatechartInterpreter(runtime.Chart(s.Process)!, runtime.Options(s.Id));
                interpreter.Start(s.Start?.Context);
                foreach (var step in s.Steps)
                {
                    var watch = Stopwatch.StartNew();
                    interpreter.Step(step);
                    macro.Add(watch.Elapsed.TotalMilliseconds);
                }
            }
        }

        var chart = runtime.Chart(Sales)!;
        var session = runtime.Session(chart)!;
        var context = J(new Dictionary<string, object> { ["total"] = 5000, ["creditLimit"] = 1000 });
        var ev = J(new Dictionary<string, object?> { ["name"] = "submit", ["actor"] = null, ["payload"] = new Dictionary<string, object>() });
        var guard = new List<double>();
        for (var i = 0; i < 2000; i++)
        {
            var watch = Stopwatch.StartNew();
            var result = session.Evaluate("01JQPRX0000000000000000010", "Guard 'exceedsCreditLimit'", context, ev, "seed", Ct);
            guard.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(true, result.Value);
        }

        var macroP95 = P95(macro);
        var guardP95 = P95(guard);
        TestContext.Current.SendDiagnosticMessage(string.Create(CultureInfo.InvariantCulture,
            $"Fixture: {macro.Count} macrosteps p95 {macroP95:0.000} ms (budget 0.2 ms on a 400-state chart, bench in P2c); guard p95 {guardP95:0.000} ms (budget 0.05 ms)."));
        Assert.True(macroP95 < 5, $"macrostep p95 {macroP95} ms");
        Assert.True(guardP95 < 2, $"guard p95 {guardP95} ms");
    }

    private static double P95(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
    }
}
