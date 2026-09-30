using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The process units of the example packs over the gate 3 fixture (phase-3-design.md sections 7.2 and 8): golden output of
/// <c>csharp-dapper</c> and <c>sql-ddl</c>; determinism across <c>--jobs</c>; a second run that writes nothing and a clean check;
/// companions and regions kept across regeneration (a renamed event keeps its endpoint's region); the model-wide units written once
/// whichever process is skipped; dispatch and runtime code without reflection; the fixture's solution built with warnings as errors,
/// its generated scenario tests passing and the running generated interpreter equal to the engine's replay after every step (with a
/// small chart for the microstep bound and the clock); the generated tests failing against a guard inverted either way; and names
/// and expressions at the edges of the C# translation still building.
/// </summary>
public sealed class ProcessTests
{
    private const string Generated = "src/Processes.Data/Generated";
    private const string Custom = "src/Processes.Data/Custom";
    private const string Tests = "src/Processes.Tests/Generated";
    private const string ApproveEvent = "01JQPRX0000000000000000016";

    [Fact]
    public async Task Csharp_dapper_over_the_processes_fixture_matches_the_golden_tree()
    {
        using var repo = PackRepo.Processes();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "processes"), repo.PathOf("src"));
    }

    [Fact]
    public async Task Sql_ddl_process_tables_are_off_by_default_and_match_the_golden_tree_when_on()
    {
        using (var off = PackRepo.Processes())
        {
            await off.GenerateCleanlyAsync(packs: ["sql-ddl"]);
            Assert.False(Directory.Exists(off.PathOf("db/main/processes")), "process-tables wrote files without processTables");
        }

        using var repo = PackRepo.Processes();
        repo.EditJson(".maquettiste/maquettiste.json", settings => settings["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["processTables"] = true });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "processes"), repo.PathOf("db"));
    }

    [Fact]
    public async Task Output_is_byte_identical_at_one_and_eight_jobs()
    {
        using var one = PackRepo.Processes();
        using var eight = PackRepo.Processes();
        await one.GenerateCleanlyAsync(force: true, jobs: 1);
        await eight.GenerateCleanlyAsync(force: true, jobs: 8);
        var a = OutputFiles(one);
        var b = OutputFiles(eight);
        Assert.Equal(a.Keys, b.Keys);
        Assert.Contains(Generated + "/Runtime/Statechart.g.cs", a.Keys);
        Assert.Contains(Tests + "/PurchaseApproval/HappyPathTests.cs", a.Keys);
        foreach (var (path, bytes) in a)
            Assert.True(bytes.AsSpan().SequenceEqual(b[path]), "differs between --jobs 1 and 8: " + path);
    }

    [Fact]
    public async Task A_second_run_writes_nothing_and_the_check_is_clean()
    {
        using var repo = PackRepo.Processes();
        await repo.GenerateCleanlyAsync();
        var before = OutputFiles(repo);
        var second = await repo.GenerateCleanlyAsync(force: true);
        Assert.True(second.FilesWritten == 0 && second.FilesDeleted == 0, PackRepo.Describe(second));
        Assert.All(second.Changes, c => Assert.True(c.Kind is FileChangeKind.Unchanged or FileChangeKind.Kept, c.Kind + " " + c.Path));
        var after = OutputFiles(repo);
        Assert.Equal(before.Keys, after.Keys);
        Assert.All(before, p => Assert.True(p.Value.AsSpan().SequenceEqual(after[p.Key]), p.Key));
        var check = await repo.GenerateAsync(GenerationMode.Check);
        Assert.True(check.Outcome == RunOutcome.Succeeded && check.Changes.All(c => c.Kind is FileChangeKind.Unchanged or FileChangeKind.Kept), PackRepo.Describe(check));
    }

    [Fact]
    public async Task Companions_and_endpoint_regions_survive_regeneration_and_a_renamed_event_keeps_its_region()
    {
        using var repo = PackRepo.Processes();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        const string handlers = Custom + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleHandlers.cs";
        const string endpoints = Custom + "/Processes/PurchaseApproval/Endpoints/PurchaseApprovalEndpoints.cs";
        Assert.Contains("throw new NotImplementedException(\"Guard notShipped", repo.Read(handlers), StringComparison.Ordinal);
        var edited = repo.Read(handlers).Replace(
            "throw new NotImplementedException(\"Guard notShipped of process SalesOrderLifecycle is not implemented yet.\");",
            "!call.IsActive(SalesOrderLifecycleStates.FulfilmentProcessingShippingShipped);", StringComparison.Ordinal);
        repo.Write(handlers, edited);

        // The region of approve's endpoint is keyed by the event's id, not its name.
        const string approveRegion = "// maquettiste:keep id=" + ApproveEvent + "\n";
        Assert.Contains(approveRegion, repo.Read(endpoints), StringComparison.Ordinal);
        const string body = "        respond = r => Results.Accepted(value: r);\n";
        var region = repo.Read(endpoints).Replace(approveRegion, approveRegion + body, StringComparison.Ordinal);
        repo.Write(endpoints, region);

        // Every unit renders again (a model change and --force); the companion is kept and the region body carried over.
        repo.EditJson(".maquettiste/model/processes/purchase-approval.json", p => p["description"] = "Changed so that every unit of the process renders again.");
        var result = await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"], force: true);
        Assert.Equal(edited, repo.Read(handlers));
        Assert.Equal(region, repo.Read(endpoints));
        Assert.DoesNotContain(result.Changes, c => c.Path == handlers && c.Kind != FileChangeKind.Kept);

        // approve renamed to sign: the endpoint follows the new name and the region body stays in it (no MQ6010).
        repo.EditJson(".maquettiste/model/processes/purchase-approval.json", p =>
            p["events"]!.AsArray().Single(e => (string?)e!["id"] == ApproveEvent)!["name"] = "sign");
        var renamed = await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.DoesNotContain(renamed.Diagnostics, d => d.Rule == "MQ6010");
        var text = repo.Read(endpoints);
        Assert.Contains("public static async Task<IResult> SignAsync(", text, StringComparison.Ordinal);
        Assert.Contains(approveRegion + body, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ApproveAsync", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_units_are_written_once_whichever_process_is_skipped_and_not_for_a_model_without_processes()
    {
        // PurchaseApproval sorts first; skipping it for the pack leaves the runtime, dispatch and actors of every process in place.
        using var repo = PackRepo.Processes();
        repo.EditJson(".maquettiste/model/processes/purchase-approval.json", p => p["generation"] = new JsonObject { ["csharp-dapper"] = new JsonObject { ["skip"] = true } });
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        foreach (var file in new[]
        {
            Generated + "/Runtime/Statechart.g.cs", Generated + "/Dispatch/Dispatch.g.cs", Generated + "/Dispatch/HandlerRegistry.g.cs",
            Generated + "/Dispatch/Behaviours.g.cs", Generated + "/Processes/Actors.cs", Custom + "/Dispatch/Pipeline.cs", Custom + "/Runtime/ProcessHost.cs",
        })
        {
            Assert.True(File.Exists(repo.PathOf(file)), file + " was not written");
        }

        Assert.False(Directory.Exists(repo.PathOf(Generated + "/Processes/PurchaseApproval")), "the skipped process's own units were written");
        Assert.True(File.Exists(repo.PathOf(Generated + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleDefinition.cs")));

        using var billing = PackRepo.Billing();
        var result = await billing.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.DoesNotContain(result.Changes, c => c.Path.Contains("/Runtime/", StringComparison.Ordinal) || c.Path.Contains("/Dispatch/", StringComparison.Ordinal)
            || c.Path.EndsWith("Actors.cs", StringComparison.Ordinal) || c.Path.Contains("/Processes/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_dispatch_and_runtime_code_uses_no_reflection()
    {
        using var repo = PackRepo.Processes();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        var files = Directory.EnumerateFiles(repo.PathOf(Generated + "/Dispatch"), "*.cs")
            .Concat(Directory.EnumerateFiles(repo.PathOf(Generated + "/Runtime"), "*.cs")).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(4, files.Count);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            Assert.DoesNotContain("System.Reflection", text, StringComparison.Ordinal);
            Assert.DoesNotContain("GetType", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Activator", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dynamic", text, StringComparison.Ordinal);

            // The one type token: a service provider looks services up by type, so the registry's Resolve<T> hands it typeof(T) for the
            // machine and the store a handler factory needs. No other typeof appears.
            var tokens = Regex.Matches(text, @"typeof\(([^)]*)\)").Select(m => m.Value).ToList();
            Assert.Equal(name == "HandlerRegistry.g.cs" ? ["typeof(T)"] : [], tokens);
        }

        var registry = repo.Read(Generated + "/Dispatch/HandlerRegistry.g.cs");
        Assert.Contains("new HandlerRegistration<SalesOrderLifecycleCancelCommand>", registry, StringComparison.Ordinal);
        Assert.Contains("registrar.Add(services => new SalesOrderLifecycleCommandHandler(Resolve<SalesOrderLifecycleMachine>(services), Resolve<ISalesOrderLifecycleStore>(services)));",
            registry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_running_generated_interpreter_matches_the_engine_after_every_step_and_honours_the_bound_and_the_clock()
    {
        using var repo = PackRepo.Processes(sources: true);
        await repo.GenerateCleanlyAsync();
        repo.Write("src/Processes.Tests/RuntimeChecks.cs", RuntimeChecks);
        await BuildOrSkipAsync(repo);
        var trace = repo.PathOf("trace");
        var test = await TestAsync(repo, new Dictionary<string, string> { ["MAQUETTISTE_SCENARIO_TRACE"] = trace });
        Assert.True(test.ExitCode == 0 && test.Output.Contains("Total: 18, Errors: 0, Failed: 0,", StringComparison.Ordinal), test.Output);

        // Every scenario test ran the generated interpreter through the dispatcher; the host wrote what each command returned. The
        // start and every step must equal the engine's replay: acceptance, refusal, active states, audit outcomes, final.
        await using var store = new ModelStore(repo.Repo.Options);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        var model = store.Current!;
        using var runtime = new ProcessRuntime(model, 1, TestContext.Current.CancellationToken);
        var scenarios = model.All<Scenario>();
        Assert.Equal(14, scenarios.Count);
        var checkedSteps = 0;
        foreach (var scenario in scenarios)
        {
            var replay = ScenarioReplayer.Replay(scenario, runtime)!;
            Assert.True(replay.Passed, $"The engine does not pass scenario {scenario.Name}.");
            var chart = runtime.Chart(scenario.Process)!;
            var file = Path.Combine(trace, UuidOf(scenario.Id) + ".jsonl");
            Assert.True(File.Exists(file), "No trace of scenario " + scenario.Name);
            var observed = File.ReadAllLines(file).Select(l => JsonNode.Parse(l)!).ToList();
            var expected = new List<StepTrace> { replay.Start! };
            expected.AddRange(replay.Steps);
            Assert.Equal(expected.Count, observed.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var (engine, generated, at) = (expected[i], observed[i], $"{scenario.Name} step {i}");
                Assert.True(engine.Accepted == (bool)generated["accepted"]!, at + ": accepted");
                Assert.True(engine.Refusal == (string?)generated["refusal"], at + $": refusal {engine.Refusal} against {(string?)generated["refusal"]}");
                var states = generated["states"]!.AsArray().Select(x => (string)x!).ToList();
                var engineStates = engine.Configuration.Select(id => chart.ById[id].Path).ToList();
                Assert.True(engineStates.SequenceEqual(states, StringComparer.Ordinal), $"{at}: the engine is in {string.Join(", ", engineStates)}, the generated interpreter in {string.Join(", ", states)}");
                var audit = generated["audit"]!.AsArray().Select(x => (string)x!).ToList();
                Assert.Equal(engine.Audit.Select(a => ResolutionValues.Kebab(a.Outcome)), audit);
                Assert.Equal(engine.Audit.Select(a => a.Actor), generated["auditActors"]!.AsArray().Select(x => (string?)x)); // ids, as the engine's
                Assert.Equal(engine.Audit.Select(a => a.Meaning), generated["auditMeanings"]!.AsArray().Select(x => (string?)x));
                Assert.True(engine.Final == (bool)generated["final"]!, at + ": final");
                checkedSteps++;
            }
        }

        Assert.Equal(64, checkedSteps); // 50 steps and 14 starts
    }

    [Fact]
    public async Task The_generated_scenario_tests_fail_against_a_guard_flipped_either_way()
    {
        using var repo = PackRepo.Processes(sources: true);
        await repo.GenerateCleanlyAsync();
        const string handlers = Custom + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleHandlers.cs";
        var text = repo.Read(handlers);
        Assert.Contains("!call.IsActive(", text, StringComparison.Ordinal);

        // notShipped inverted: true after shipping (cancel taken where it must be refused) and false before (refused where it must be
        // taken), so both scenarios that reach it fail.
        repo.Write(handlers, text.Replace("!call.IsActive(", "call.IsActive(", StringComparison.Ordinal));
        await BuildOrSkipAsync(repo);
        var test = await TestAsync(repo);
        Assert.NotEqual(0, test.ExitCode);
        Assert.Contains("Processes.Data.Sales.Tests.CancelRefusedAfterShippingTests.CancelRefusedAfterShipping [FAIL]", test.Output, StringComparison.Ordinal);
        Assert.Contains("Processes.Data.Sales.Tests.CancelBeforeShippingTests.CancelBeforeShipping [FAIL]", test.Output, StringComparison.Ordinal);
        Assert.Contains("Total: 14, Errors: 0, Failed: 2,", test.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_guard_and_action_names_that_match_built_in_ones_multi_line_expressions_and_untranslatable_ones_build()
    {
        // Without the committed companions: the stub companion's comments and the generated half's declarations.
        using var fresh = PackRepo.Processes();
        AddEdgeCases(fresh);
        await fresh.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        var generated = fresh.Read(Generated + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleHandlers.g.cs");
        var companion = fresh.Read(Custom + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleHandlers.cs");
        Assert.Contains("private static bool GuardCheck(StatechartCall<SalesOrderLifecycleContext> call) => call.Context.Total > 0;", generated, StringComparison.Ordinal);
        Assert.Contains("private static SalesOrderLifecycleContext ActionCheck(StatechartCall<SalesOrderLifecycleContext> call) => call.Context with { Total = call.Context.Total };",
            generated, StringComparison.Ordinal);
        Assert.Contains("/// <summary>Guard twoLines: <c>context.total &gt;   context.creditLimit</c>.</summary>", generated, StringComparison.Ordinal);
        Assert.Contains("private static bool GuardTwoLines(StatechartCall<SalesOrderLifecycleContext> call) => call.Context.Total > call.Context.CreditLimit;", generated,
            StringComparison.Ordinal);

        // Outside the subset, as C# would compute them otherwise than the engine: stubs, with the expression on one comment line.
        foreach (var stub in new[] { "GuardHalves", "GuardNoteBefore", "GuardCountIsNull", "GuardLimitOrdered", "GuardSplitHalves" })
        {
            Assert.Contains("private partial bool " + stub + "(StatechartCall<SalesOrderLifecycleContext> call);", generated, StringComparison.Ordinal);
            Assert.Contains("private partial bool " + stub + "(StatechartCall<SalesOrderLifecycleContext> call) =>", companion, StringComparison.Ordinal);
        }

        Assert.Contains("    // The model's expression, outside the translated subset: context.count /   2 > 0\n", companion, StringComparison.Ordinal);
        var contracts = fresh.Read(Generated + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleContracts.cs");
        foreach (var type in new[] { "SalesOrderLifecycleStartCommand", "SalesOrderLifecycleStartControl", "SalesOrderLifecycleInvokeResultCommand",
            "SalesOrderLifecycleInvokeResultControl", "SalesOrderLifecycleTimersDueCommand", "SalesOrderLifecycleTimersDueControl" })
            Assert.Single(Regex.Matches(contracts, "public sealed partial record " + type + @"\("));
        Assert.Contains("public const string EventsValue = \"events\";", fresh.Read(Generated + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleDefinition.cs"),
            StringComparison.Ordinal);

        // With the committed companions (and the new stubs written there), the solution builds with warnings as errors.
        using var repo = PackRepo.Processes(sources: true);
        AddEdgeCases(repo);
        await repo.GenerateCleanlyAsync();
        const string handlers = Custom + "/Processes/SalesOrderLifecycle/SalesOrderLifecycleHandlers.cs";
        var text = repo.Read(handlers);
        var end = text.LastIndexOf('}');
        repo.Write(handlers, text[..end] + """

                private partial bool GuardHalves(StatechartCall<SalesOrderLifecycleContext> call) => call.Context.Count / 2m > 1;

                private partial bool GuardNoteBefore(StatechartCall<SalesOrderLifecycleContext> call) => string.CompareOrdinal(call.Context.Note, "b") < 0;

                private partial bool GuardCountIsNull(StatechartCall<SalesOrderLifecycleContext> call) => false;

                private partial bool GuardLimitOrdered(StatechartCall<SalesOrderLifecycleContext> call) => (call.Context.Limit ?? 0) >= 0;

                private partial bool GuardSplitHalves(StatechartCall<SalesOrderLifecycleContext> call) => call.Context.Count / 2m > 0;
            }

            """);
        await BuildOrSkipAsync(repo);
    }

    // Adds to SalesOrderLifecycle events named like the built-in commands and like the definition's nested class, a guard and an
    // action sharing a name, a guard written on two lines, and expressions the C# subset leaves to the companion.
    private static void AddEdgeCases(PackRepo repo) =>
        repo.EditJson(".maquettiste/model/processes/sales-order-lifecycle.json", p =>
        {
            var context = p["context"]!.AsArray();
            context.Add(new JsonObject { ["id"] = "01JQATT0000000000000000E03", ["name"] = "count", ["type"] = "int32", ["default"] = 0 });
            context.Add(new JsonObject { ["id"] = "01JQATT0000000000000000E04", ["name"] = "note", ["type"] = "string" });
            context.Add(new JsonObject { ["id"] = "01JQATT0000000000000000E05", ["name"] = "limit", ["type"] = "int32" });
            var events = p["events"]!.AsArray();
            foreach (var (id, name) in new[] { ("01JQPRX00000000000000000E1", "start"), ("01JQPRX00000000000000000E2", "invokeResult"),
                ("01JQPRX00000000000000000E3", "timersDue"), ("01JQPRX00000000000000000E4", "events") })
                events.Add(new JsonObject { ["id"] = id, ["name"] = name });
            var guards = p["guards"]!.AsArray();
            foreach (var (id, name, expression) in new[]
            {
                ("01JQPRX00000000000000000G1", "check", "context.total > 0"),
                ("01JQPRX00000000000000000G2", "twoLines", "context.total >\r\n  context.creditLimit"),
                ("01JQPRX00000000000000000G3", "halves", "context.count / 2 > 1"),
                ("01JQPRX00000000000000000G4", "noteBefore", "context.note < \"b\""),
                ("01JQPRX00000000000000000G5", "countIsNull", "context.count === null"),
                ("01JQPRX00000000000000000G6", "limitOrdered", "context.limit >= 0"),
                ("01JQPRX00000000000000000G7", "splitHalves", "context.count /\n  2 > 0"),
            })
                guards.Add(new JsonObject { ["id"] = id, ["name"] = name, ["expression"] = expression });
            p["actions"] = new JsonArray(new JsonObject { ["id"] = "01JQPRX00000000000000000A1", ["name"] = "check", ["expression"] = "({ total: context.total })" });
        });

    // Small charts run on the generated runtime (compiled into the fixture's test project for the test above): a macrostep over the
    // microstep bound fails the input and keeps the state reached; the instance's clock moves only on time inputs, to the tick (a
    // timer of half a millisecond included); a transition naming an undeclared guard never fires; and the fixture's store refuses
    // a save over a version it did not load.
    private const string RuntimeChecks = """
        using Processes.Data;
        using Processes.Data.Runtime;
        using Processes.Data.Sales;
        using Xunit;

        namespace Processes.Tests;

        public sealed class RuntimeChecks
        {
            private static readonly DateTimeOffset T0 = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

            [Fact]
            public void A_macrostep_over_the_bound_fails_and_keeps_the_state_reached()
            {
                // C --go--> A, then A and B pass to each other without an event: the macrostep never settles.
                var chart = new ChartDefinition("p", "Loop",
                    [new("p", "Loop", StateKind.Compound, -1, initial: 3), new("a", "A", StateKind.Atomic, 0), new("b", "B", StateKind.Atomic, 0), new("c", "C", StateKind.Atomic, 0)],
                    [new("t1", 3, TriggerKind.Event, "go", [1]), new("t2", 1, TriggerKind.Always, "", [2]), new("t3", 2, TriggerKind.Always, "", [1])],
                    [new("go", [])], [], [], []);
                var machine = new Statechart<Context>(chart, new Behaviour(), new ManualClock(T0), maxMicrosteps: 10);
                var start = machine.Start("i", new Context());
                Assert.Null(start.Failure);
                var step = machine.Send(start.Snapshot, "go", new ProcessEnvelope("i"));
                Assert.NotNull(step.Failure);
                Assert.True(step.Accepted);
                Assert.Single(step.States);
                Assert.Contains(step.States[0], new[] { "A", "B" });
                Assert.Equal(step.States, step.Snapshot.States);
            }

            [Fact]
            public void The_instance_clock_moves_only_on_time_inputs()
            {
                // C --after 0.5 ms--> D; ping is a targetless event on C.
                var chart = new ChartDefinition("p", "Clock",
                    [new("p", "Clock", StateKind.Compound, -1, initial: 1), new("c", "C", StateKind.Atomic, 0), new("d", "D", StateKind.Atomic, 0)],
                    [new("t1", 1, TriggerKind.After, "", [2], afterTicks: 5_000), new("t2", 1, TriggerKind.Event, "ping", [])],
                    [new("ping", [])], [], [], []);
                var clock = new ManualClock(T0);
                var machine = new Statechart<Context>(chart, new Behaviour(), clock);
                var snapshot = machine.Start("i", new Context()).Snapshot;
                clock.Advance(TimeSpan.FromTicks(4_000));
                var ping = machine.Send(snapshot, "ping", new ProcessEnvelope("i"));
                Assert.Equal(T0, ping.At);
                Assert.Equal(T0, ping.Snapshot.Clock);

                var early = machine.Tick(ping.Snapshot);
                Assert.Equal(["C"], early.States);
                Assert.Equal(T0.AddTicks(4_000), early.Snapshot.Clock);
                clock.Advance(TimeSpan.FromTicks(1_000));
                var due = machine.Tick(early.Snapshot);
                Assert.Equal(["D"], due.States);
                Assert.Equal(T0.AddTicks(5_000), due.At);
            }

            [Fact]
            public void A_transition_naming_an_undeclared_guard_never_fires()
            {
                var chart = new ChartDefinition("p", "Missing",
                    [new("p", "Missing", StateKind.Compound, -1, initial: 1), new("c", "C", StateKind.Atomic, 0), new("d", "D", StateKind.Atomic, 0)],
                    [new("t1", 1, TriggerKind.Event, "go", [2], guard: "undeclared", guardMissing: true)],
                    [new("go", [])], [], [], []);
                var machine = new Statechart<Context>(chart, new Behaviour(), new ManualClock(T0));
                var step = machine.Send(machine.Start("i", new Context()).Snapshot, "go", new ProcessEnvelope("i"));
                Assert.False(step.Accepted);
                Assert.Equal(Refusals.Guard, step.Refusal);
                Assert.Equal(["C"], step.States);
            }

            [Fact]
            public async Task A_save_over_a_version_the_command_did_not_load_is_refused()
            {
                var cancellationToken = TestContext.Current.CancellationToken;
                const string instance = "0195f2ca-8000-0000-0000-0000000000ff";
                await using var host = await ScenarioHost.StartAsync(new ManualClock(T0), cancellationToken);
                var started = await host.Dispatcher.SendAsync(new SalesOrderLifecycleStartControl(new ProcessEnvelope(instance)) { Context = new SalesOrderLifecycleContext { Total = 1, CreditLimit = 10 } }, cancellationToken);
                Assert.True(started.Accepted, started.Refusal);

                // Two commands load version 1; the first saves version 2, the second's save over version 1 is refused.
                var first = (await host.SalesOrders.LoadAsync(instance, cancellationToken))!;
                var second = (await host.SalesOrders.LoadAsync(instance, cancellationToken))!;
                Assert.Equal((1, 1), (first.Version, second.Version));
                first.Version = 2;
                await host.SalesOrders.SaveAsync(first, 1, cancellationToken);
                second.Version = 2;
                await Assert.ThrowsAsync<ProcessConcurrencyException>(() => host.SalesOrders.SaveAsync(second, 1, cancellationToken));
                Assert.Equal(2, (await host.SalesOrders.LoadAsync(instance, cancellationToken))!.Version);
            }

            private sealed class Context;

            private sealed class Behaviour : IStatechartBehaviour<Context>
            {
                public bool Guard(string guard, StatechartCall<Context> call) => true;

                public Context Action(string action, StatechartCall<Context> call) => call.Context;

                public IReadOnlyList<string> Changed(Context before, Context after) => [];
            }
        }

        """;

    // ---- helpers ----

    private static SortedDictionary<string, byte[]> OutputFiles(PackRepo repo)
    {
        var root = repo.Repo.RepoRoot;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.StartsWith(".maquettiste/", StringComparison.Ordinal))
                files[relative] = File.ReadAllBytes(file);
        }

        return files;
    }

    private static Dictionary<string, string> StatePaths(JsonArray states, string prefix)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var path = prefix + (string)state!["name"]!;
            map[(string)state["id"]!] = path;
            if (state["states"] is JsonArray children)
            {
                foreach (var (id, child) in StatePaths(children, path + "."))
                    map[id] = child;
            }
        }

        return map;
    }

    private static async Task BuildOrSkipAsync(PackRepo repo)
    {
        var solution = repo.PathOf("src/Processes.slnx");
        var restore = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, ["restore", solution], repo.Repo.RepoRoot, TimeSpan.FromMinutes(5));
        if (restore.ExitCode != 0)
        {
            if (IsNuGetUnreachable(restore.Output))
                Assert.Skip("NuGet is unreachable and the fixture's packages are not in the local package cache, so the fixture solution was not built:\n" + restore.Output);
            Assert.Fail("dotnet restore failed:\n" + restore.Output);
        }

        var build = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, ["build", solution, "--no-restore", "-warnaserror", "-nodeReuse:false", "-m:1"],
            repo.Repo.RepoRoot, TimeSpan.FromMinutes(5));
        Assert.True(build.ExitCode == 0, "The fixture solution does not build:\n" + build.Output);
    }

    // The xunit v3 test project is an executable: run it directly, without a test host or build nodes.
    private static Task<ProcessRunner.Result> TestAsync(PackRepo repo, IReadOnlyDictionary<string, string>? environment = null) =>
        ProcessRunner.RunAsync(ProcessRunner.Dotnet, [repo.PathOf("src/Processes.Tests/bin/Debug/net10.0/Processes.Tests.dll"), "-noColor"], repo.Repo.RepoRoot,
            TimeSpan.FromMinutes(5), environment: environment);

    // The instance identity a generated scenario test uses: the scenario id's 128 bits (a ULID in Crockford base32) as a UUID.
    private static string UuidOf(string ulid)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var value = System.Numerics.BigInteger.Zero;
        foreach (var c in ulid.ToUpperInvariant())
            value = (value * 32) + alphabet.IndexOf(c, StringComparison.Ordinal);
        var hex = value.ToString("x33", System.Globalization.CultureInfo.InvariantCulture)[^32..];
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static bool IsNuGetUnreachable(string output) =>
        output.Contains("NU1301", StringComparison.Ordinal)
        || output.Contains("NU1801", StringComparison.Ordinal)
        || output.Contains("Unable to load the service index", StringComparison.Ordinal)
        || output.Contains("No such host", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase);
}
