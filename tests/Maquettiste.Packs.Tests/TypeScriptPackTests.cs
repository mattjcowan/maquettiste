using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The TypeScript sample pack's process units (phase-3-design.md section 7.5) over the gate 3 fixture
/// (<c>tests/fixtures/models/processes</c>): generation is the same at one and eight jobs and a second run writes nothing; a
/// companion survives an edit and a regeneration; a model without processes gets no process file; and, where node and the
/// TypeScript compiler are available, the output type-checks (<c>tsc --noEmit</c>), its scenario tests pass under
/// <c>node --test</c> with the stub guard's rule written in its companion, the generated interpreter's result after every step
/// equals the engine's replay, and the unimplemented stub or the rule inverted makes the scenario tests that reach it fail. The compiler and node's types come from the editor's <c>node_modules</c>
/// (<c>npm ci</c> in <c>src/editor</c>), or from the folder <c>MAQUETTISTE_NODE_MODULES</c> names; without them, or without
/// node 22.18 or later (type stripping), those tests skip with a message (and fail when <c>MAQUETTISTE_REQUIRE_TYPESCRIPT</c> is set, as
/// the <c>typescript-pack</c> CI job sets it).
/// </summary>
public sealed class TypeScriptPackTests
{
    private static readonly FileChangeKind[] Writes = [FileChangeKind.Added, FileChangeKind.Modified, FileChangeKind.Deleted, FileChangeKind.HandEdited, FileChangeKind.Conflict, FileChangeKind.OrphanedOwned];

    private const string HandlersCompanion = "web/processes/sales-order-lifecycle/sales-order-lifecycle.handlers.ts";
    private const string CancelTests = "web/tests/sales-order-lifecycle/cancel-*.test.ts";
    private const string NotShippedStub = "throw new Error('Guard notShipped of SalesOrderLifecycle is not implemented yet.');";
    private const string NotShippedRule = "return !_states.includes('Fulfilment.Processing.Shipping.Shipped');";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Generation_is_the_same_at_one_and_eight_jobs_and_a_second_run_writes_nothing()
    {
        using var one = PackRepo.TypeScript("processes");
        using var eight = PackRepo.TypeScript("processes");
        await one.GenerateCleanlyAsync(jobs: 1);
        await eight.GenerateCleanlyAsync(jobs: 8);

        var files = Listing(one);
        Assert.Equal(files, Listing(eight));
        foreach (var file in files)
            Assert.True(one.Read(file) == eight.Read(file), $"{file} differs between --jobs 1 and --jobs 8.");

        // Every unit of section 7.2 wrote its files: 11 per process, one test per scenario, and the model-wide modules.
        Assert.Equal(14, files.Count(f => f.EndsWith(".test.ts", StringComparison.Ordinal)));
        foreach (var file in new[]
        {
            "web/runtime/statechart.gen.ts", "web/runtime/process-host.ts", "web/dispatch/dispatch.gen.ts", "web/dispatch/pipeline.ts",
            "web/dispatch/registry.ts", "web/dispatch/behaviours.ts", "web/processes/actors.ts", "web/endpoints/purchase-approval.endpoints.ts",
            "web/processes/purchase-approval/purchase-approval.states.ts", "web/processes/purchase-approval/purchase-approval.definition.ts",
            "web/processes/purchase-approval/purchase-approval.contracts.ts", "web/processes/purchase-approval/purchase-approval.handlers.gen.ts",
            "web/processes/purchase-approval/purchase-approval.handlers.ts", "web/processes/purchase-approval/purchase-approval.services.gen.ts",
            "web/processes/purchase-approval/purchase-approval.services.ts", "web/processes/purchase-approval/purchase-approval.machine.gen.ts",
            "web/processes/purchase-approval/purchase-approval.machine.ts", "web/processes/purchase-approval/purchase-approval.store.gen.ts",
            "web/processes/purchase-approval/purchase-approval.store.ts",
        })
        {
            Assert.Contains(file, files);
        }

        var again = await one.GenerateCleanlyAsync(jobs: 8);
        Assert.DoesNotContain(again.Changes, c => Writes.Contains(c.Kind));
        var forced = await one.GenerateCleanlyAsync(force: true);
        Assert.DoesNotContain(forced.Changes, c => Writes.Contains(c.Kind));
        var check = await one.GenerateAsync(GenerationMode.Check);
        Assert.True(check.Outcome == RunOutcome.Succeeded, PackRepo.Describe(check));
    }

    [Fact]
    public async Task A_companion_and_an_endpoint_region_keep_their_edits_when_the_model_changes_and_the_pack_regenerates()
    {
        using var repo = PackRepo.TypeScript("processes");
        await repo.GenerateCleanlyAsync();
        var edited = repo.Read(HandlersCompanion).Replace(NotShippedStub, NotShippedRule, StringComparison.Ordinal);
        Assert.Contains(NotShippedRule, edited, StringComparison.Ordinal);
        repo.Write(HandlersCompanion, edited);
        var generated = repo.Read("web/processes/sales-order-lifecycle/sales-order-lifecycle.handlers.gen.ts");
        // An edit inside an endpoint's region (submit's, keyed by the event's id) survives too.
        const string Endpoints = "web/endpoints/sales-order-lifecycle.endpoints.ts";
        const string SubmitRegion = "// maquettiste:keep id=01JQPRX0000000000000000001\n";
        const string Edit = "      respond = (outcome) => ({ status: outcome.result.accepted ? 202 : 409, body: null });\n";
        var endpoints = repo.Read(Endpoints);
        Assert.Contains(SubmitRegion, endpoints, StringComparison.Ordinal);
        repo.Write(Endpoints, endpoints.Replace(SubmitRegion, SubmitRegion + Edit, StringComparison.Ordinal));

        // A second stub guard: the generated half declares it, the companion is left alone (and the build would ask for it). And
        // submit is renamed: its endpoint's region is keyed by the event's id, so the edit inside it moves along.
        repo.EditJson(".maquettiste/model/processes/sales-order-lifecycle.json", process =>
        {
            process["guards"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "01JQPRX0000000000000000099", ["name"] = "customerKnown" });
            process["events"]!.AsArray().Single(e => (string?)e!["name"] == "submit")!["name"] = "place";
        });
        var result = await repo.GenerateCleanlyAsync();

        Assert.Equal(edited, repo.Read(HandlersCompanion));
        Assert.DoesNotContain(result.Changes, c => c.Path.EndsWith(".handlers.ts", StringComparison.Ordinal) && Writes.Contains(c.Kind));
        var regenerated = repo.Read("web/processes/sales-order-lifecycle/sales-order-lifecycle.handlers.gen.ts");
        Assert.NotEqual(generated, regenerated);
        Assert.Contains("guardCustomerKnown(context: Readonly<SalesOrderLifecycleContext>, event: EventView, states: readonly string[]): boolean;", regenerated,
            StringComparison.Ordinal);
        Assert.Contains(SubmitRegion + Edit, repo.Read(Endpoints), StringComparison.Ordinal);
        Assert.Contains("  place: {\n", repo.Read(Endpoints), StringComparison.Ordinal);
        Assert.Contains("type: 'SalesOrderLifecycle.place'", repo.Read(Endpoints), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule == "MQ6010");

        var forced = await repo.GenerateCleanlyAsync(force: true);
        Assert.Equal(edited, repo.Read(HandlersCompanion));
        Assert.DoesNotContain(forced.Changes, c => Writes.Contains(c.Kind));
    }

    [Fact]
    public async Task A_model_without_processes_gets_only_the_entity_model_and_schema_files()
    {
        using var repo = PackRepo.TypeScript("billing");
        await repo.GenerateCleanlyAsync();

        var files = Listing(repo);
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, f => f.StartsWith("web/runtime/", StringComparison.Ordinal) || f.StartsWith("web/dispatch/", StringComparison.Ordinal)
            || f.StartsWith("web/processes/", StringComparison.Ordinal) || f.StartsWith("web/endpoints/", StringComparison.Ordinal)
            || f.StartsWith("web/tests/", StringComparison.Ordinal));
        Assert.Contains("web/index.ts", files);
    }

    [Fact]
    public async Task The_output_type_checks_its_scenario_tests_pass_and_every_step_matches_the_engine()
    {
        var tools = await ToolsOrSkipAsync();
        using var repo = PackRepo.TypeScript("processes");
        await repo.GenerateCleanlyAsync();
        WriteProject(repo, tools);
        ImplementNotShipped(repo);

        var tsc = await ProcessRunner.RunAsync(tools.Node, [tools.Tsc, "--noEmit", "-p", "tsconfig.json"], repo.Repo.RepoRoot, TimeSpan.FromMinutes(3));
        Assert.True(tsc.ExitCode == 0, "tsc --noEmit failed:\n" + tsc.Output);

        var run = await RunScenarioTestsAsync(repo, tools, "web/tests/**/*.test.ts");
        Assert.True(run.ExitCode == 0, "node --test failed:\n" + run.Output);
        Assert.Contains("# pass 14", run.Output, StringComparison.Ordinal);

        // Conformance: the generated interpreter's result after the start and after every step equals the engine's replay.
        var generated = Steps(run.Output);
        await using var store = new ModelStore(repo.Repo.Options);
        await store.LoadAsync(Ct);
        var model = store.Current!;
        using var runtime = new ProcessRuntime(model, 1, Ct);
        var scenarios = model.All<Scenario>();
        Assert.Equal(14, scenarios.Count);
        foreach (var scenario in scenarios)
        {
            var replay = ScenarioReplayer.Replay(scenario, runtime)!;
            Assert.True(replay.Passed, $"The engine does not pass scenario {scenario.Name}.");
            var chart = runtime.Chart(scenario.Process)!;
            var expected = new List<(int Step, StepTrace Trace)> { (-1, replay.Start!) };
            expected.AddRange(replay.Steps.Select((t, i) => (i, t)));
            Assert.True(generated.TryGetValue(scenario.Id, out var actual), $"No step trace for scenario {scenario.Name}.");
            Assert.Equal(expected.Count, actual.Count);
            foreach (var ((index, trace), step) in expected.Zip(actual))
            {
                var at = $"{scenario.Name}, step {index}";
                Assert.Equal(index, step.GetProperty("step").GetInt32());
                Assert.True(trace.Accepted == step.GetProperty("accepted").GetBoolean(), at + ": accepted");
                Assert.Equal(trace.Refusal, step.GetProperty("refusal").ValueKind == JsonValueKind.Null ? null : step.GetProperty("refusal").GetString());
                Assert.Equal(trace.Configuration.Select(id => chart.ById[id].Path).ToList(), step.GetProperty("states").EnumerateArray().Select(s => s.GetString()!).ToList());
                var changed = step.GetProperty("changed").EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                Assert.Equal(trace.Changed.Count, changed.Count);
                foreach (var (id, value) in trace.Changed)
                    Assert.True(changed.TryGetValue(chart.Attributes[id].Name, out var other) && JsonValues.Same(value, other), at + ": changed " + chart.Attributes[id].Name);
                Assert.True(trace.Final == step.GetProperty("final").GetBoolean(), at + ": final");
            }
        }
    }

    [Fact]
    public async Task Events_named_like_the_built_in_commands_and_a_guard_and_an_action_sharing_a_name_type_check()
    {
        var tools = await ToolsOrSkipAsync();
        using var repo = PackRepo.TypeScript("processes");
        repo.EditJson(".maquettiste/model/processes/sales-order-lifecycle.json", p =>
        {
            var events = p["events"]!.AsArray();
            events.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "01JQPRX00000000000000000E1", ["name"] = "start" });
            events.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "01JQPRX00000000000000000E2", ["name"] = "tick" });
            p["guards"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "01JQPRX00000000000000000G1", ["name"] = "check" });
            p["actions"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = "01JQPRX00000000000000000A1", ["name"] = "check" });
        });
        repo.EditJson(".maquettiste/model/processes/purchase-approval.json", p =>
        {
            // The invoke checkBudget's done and error commands are built in: events of those names must not meet them.
            var events = p["events"]!.AsArray();
            events.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "01JQPRX00000000000000000E3", ["name"] = "checkBudgetDone" });
            events.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "01JQPRX00000000000000000E4", ["name"] = "checkBudgetError" });
        });
        await repo.GenerateCleanlyAsync();
        WriteProject(repo, tools);
        ImplementNotShipped(repo);

        var contracts = repo.Read("web/processes/sales-order-lifecycle/sales-order-lifecycle.contracts.ts");
        Assert.Contains("export interface SalesOrderLifecycleStartCommand extends", contracts, StringComparison.Ordinal);
        Assert.Contains("export interface SalesOrderLifecycleStartControl {", contracts, StringComparison.Ordinal);
        Assert.Contains("export interface SalesOrderLifecycleTickCommand extends", contracts, StringComparison.Ordinal);
        Assert.Contains("export interface SalesOrderLifecycleTickControl {", contracts, StringComparison.Ordinal);
        var handlers = repo.Read("web/processes/sales-order-lifecycle/sales-order-lifecycle.handlers.gen.ts");
        Assert.Contains("  guardCheck(context:", handlers, StringComparison.Ordinal);
        Assert.Contains("  actionCheck(context:", handlers, StringComparison.Ordinal);

        var companion = repo.Read(HandlersCompanion);
        Assert.Contains("  guardCheck(_context, _event, _states): boolean {", companion, StringComparison.Ordinal);
        Assert.Contains("  actionCheck(_context, _event, _states): Partial<SalesOrderLifecycleContext> {", companion, StringComparison.Ordinal);
        Assert.Contains("sendStart(snapshot: ProcessSnapshot,", repo.Read("web/processes/sales-order-lifecycle/sales-order-lifecycle.machine.gen.ts"), StringComparison.Ordinal);
        var tsc = await ProcessRunner.RunAsync(tools.Node, [tools.Tsc, "--noEmit", "-p", "tsconfig.json"], repo.Repo.RepoRoot, TimeSpan.FromMinutes(3));
        Assert.True(tsc.ExitCode == 0, "tsc --noEmit failed:\n" + tsc.Output);
    }

    [Fact]
    public async Task The_scenario_tests_fail_against_the_unimplemented_stub_and_a_guard_flipped_either_way()
    {
        var tools = await ToolsOrSkipAsync();
        using var repo = PackRepo.TypeScript("processes");
        await repo.GenerateCleanlyAsync();
        WriteProject(repo, tools);

        // The stub companion throws until the rule is written: both cancel scenarios reach notShipped and fail.
        var stub = await RunScenarioTestsAsync(repo, tools, CancelTests);
        Assert.True(stub.ExitCode != 0, "The scenario tests passed against the unimplemented stub:\n" + stub.Output);
        Assert.Contains("# fail 2", stub.Output, StringComparison.Ordinal);

        ImplementNotShipped(repo);
        var passing = await RunScenarioTestsAsync(repo, tools, CancelTests);
        Assert.True(passing.ExitCode == 0, "The scenario tests should pass with the rule written:\n" + passing.Output);
        Assert.Contains("# pass 2", passing.Output, StringComparison.Ordinal);

        // The rule inverted: cancel is taken after shipping and refused before it, both of which the scenarios say it must not be.
        var companion = repo.Read(HandlersCompanion);
        repo.Write(HandlersCompanion, companion.Replace("return !_states.includes(", "return _states.includes(", StringComparison.Ordinal));
        await repo.GenerateCleanlyAsync();
        Assert.Contains("return _states.includes(", repo.Read(HandlersCompanion), StringComparison.Ordinal);

        var failing = await RunScenarioTestsAsync(repo, tools, CancelTests);
        Assert.True(failing.ExitCode != 0, "The scenario tests passed against a broken handler:\n" + failing.Output);
        Assert.Contains("# fail 2", failing.Output, StringComparison.Ordinal);
        Assert.Contains("step 4: guard notShipped answers as the scenario assumes", failing.Output, StringComparison.Ordinal);
        Assert.Contains("step 2: guard notShipped answers as the scenario assumes", failing.Output, StringComparison.Ordinal);
    }

    // ---- helpers ----

    // Writes the rule of the stub guard notShipped in its companion: nothing has shipped while no Shipped state is active.
    private static void ImplementNotShipped(PackRepo repo)
    {
        var companion = repo.Read(HandlersCompanion);
        Assert.Contains(NotShippedStub, companion, StringComparison.Ordinal);
        repo.Write(HandlersCompanion, companion.Replace(NotShippedStub, NotShippedRule, StringComparison.Ordinal));
    }

    private sealed record Tools(string Node, string Tsc, string TypeRoots, bool StripFlag);

    private static List<string> Listing(PackRepo repo)
    {
        var root = repo.PathOf("web");
        return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => "web/" + Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
    }

    // node with type stripping (22.18 or later; 22.6 to 22.17 with the flag) and a TypeScript compiler with node's types. Without
    // them the test skips, unless MAQUETTISTE_REQUIRE_TYPESCRIPT is set (the CI job), where it fails.
    private static async Task<Tools> ToolsOrSkipAsync()
    {
        var node = ProcessRunner.FindOnPath("node");
        if (node is null)
            Skip("node is not on the PATH, so the generated TypeScript was not checked or run.");
        var version = await ProcessRunner.RunAsync(node, ["--version"], Directory.GetCurrentDirectory(), TimeSpan.FromSeconds(30));
        var parts = version.Output.Trim().TrimStart('v').Split('.');
        var major = parts.Length > 1 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        if (major < 22 || (major == 22 && minor < 6))
            Skip($"node {version.Output.Trim()} cannot run TypeScript (22.6 or later is needed), so the generated tests were not run.");
        var strip = (major == 22 && minor < 18) || (major == 23 && minor < 6);

        var modules = Environment.GetEnvironmentVariable("MAQUETTISTE_NODE_MODULES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Fixtures.RepoRoot, "src", "editor", "node_modules");
        var tsc = Path.Combine(modules, "typescript", "bin", "tsc");
        var types = Path.Combine(modules, "@types");
        if (!File.Exists(tsc) || !File.Exists(Path.Combine(types, "node", "package.json")))
            Skip($"No TypeScript compiler and node types under {modules}: run `npm ci` in src/editor (or set MAQUETTISTE_NODE_MODULES), so the generated TypeScript was not checked or run.");
        return new Tools(node, tsc, types, strip);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Skip(string reason)
    {
        if (Environment.GetEnvironmentVariable("MAQUETTISTE_REQUIRE_TYPESCRIPT") is { Length: > 0 })
            Assert.Fail(reason);
        Assert.Skip(reason);
    }

    // A package.json (ES modules) and a strict tsconfig.json at the repo root, typing node's modules from the tools' folder.
    private static void WriteProject(PackRepo repo, Tools tools)
    {
        repo.Write("package.json", """
            { "private": true, "type": "module" }

            """);
        var typeRoots = JsonSerializer.Serialize(tools.TypeRoots);
        repo.Write("tsconfig.json", $$"""
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "NodeNext",
                "moduleResolution": "NodeNext",
                "strict": true,
                "noUncheckedIndexedAccess": true,
                "noEmit": true,
                "allowImportingTsExtensions": true,
                "verbatimModuleSyntax": true,
                "erasableSyntaxOnly": true,
                "skipLibCheck": true,
                "types": ["node"],
                "typeRoots": [{{typeRoots}}]
              },
              "include": ["web/**/*.ts"]
            }

            """);
    }

    private static Task<ProcessRunner.Result> RunScenarioTestsAsync(PackRepo repo, Tools tools, string pattern)
    {
        List<string> arguments = tools.StripFlag ? ["--experimental-strip-types"] : [];
        arguments.AddRange(["--test", "--test-reporter=tap", pattern]);
        return ProcessRunner.RunAsync(tools.Node, arguments, repo.Repo.RepoRoot, TimeSpan.FromMinutes(3));
    }

    // The "maquettiste-step" diagnostics of a TAP run, by scenario id, in order.
    private static Dictionary<string, List<JsonElement>> Steps(string output)
    {
        const string marker = "maquettiste-step ";
        var steps = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
                continue;
            var step = JsonDocument.Parse(line[(at + marker.Length)..].Trim()).RootElement.Clone();
            var scenario = step.GetProperty("scenario").GetString()!;
            if (!steps.TryGetValue(scenario, out var list))
                steps[scenario] = list = [];
            list.Add(step);
        }

        return steps;
    }
}
