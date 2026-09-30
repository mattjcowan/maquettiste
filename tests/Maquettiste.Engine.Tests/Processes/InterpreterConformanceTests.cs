using System.Collections.Immutable;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The interpreter's conformance suite (phase-3-design.md section 4.1): one test per bullet of the semantics (configuration,
/// macrostep, selection, microstep, done events, delays, invokes, gates, actors), history, the SPEC section 8 example chart,
/// determinism, and the expression rules MQ9501 to MQ9507. Charts are built in code with state names as ids.
/// </summary>
public sealed class InterpreterConformanceTests : IDisposable
{
    private readonly List<IDisposable> _sessions = [];

    public void Dispose()
    {
        foreach (var s in _sessions)
            s.Dispose();
    }

    // ---- chart builders ----

    private static ProcessState S(string name, StateType type = StateType.Atomic, params ProcessState[] children) =>
        new() { Id = name, Name = name, Type = type, States = children };

    private static ProcessTransition On(string id, string source, string ev, params string[] targets) =>
        new() { Id = id, Source = source, Event = ev, Targets = targets };

    private static ProcessTransition Always(string id, string source, string? guard, params string[] targets) =>
        new() { Id = id, Source = source, Trigger = TransitionTrigger.Always, Guard = guard, Targets = targets };

    private static ProcessEvent E(string name, params string[] actors) => new() { Id = name, Name = name, Actors = actors };

    private static ProcessGuard G(string name, string? expression) => new() { Id = name, Name = name, Expression = expression };

    private static ProcessAction A(string name, string? expression = null, params string[] raises) =>
        new() { Id = name, Name = name, Expression = expression, Raises = raises };

    private static ModelAttribute Int(string name, int value) =>
        new() { Id = name, Name = name, Type = new TypeRef { Builtin = "int32" }, Default = JsonSerializer.SerializeToElement(value) };

    private static Process P(ProcessState[] states, ProcessTransition[] transitions, ProcessEvent[]? events = null, ProcessGuard[]? guards = null,
        ProcessAction[]? actions = null, ModelAttribute[]? context = null, string id = "P") =>
        new()
        {
            Id = id, Name = id, States = states, Transitions = transitions, Events = events ?? [], Guards = guards ?? [], Actions = actions ?? [],
            Context = context ?? [],
        };

    private StatechartInterpreter Run(Process process, Func<string, StatechartModel?>? sub = null, Func<ModelAttribute, JsonElement, string?>? fits = null) =>
        new(StatechartModel.Get(process, null), new InterpreterOptions
        {
            SubProcess = sub,
            Fits = fits,
            Expressions = chart =>
            {
                var session = ProcessExpressions.Get(chart.Process).Open(1, CancellationToken.None);
                _sessions.Add(session);
                return session;
            },
        });

    private static ScenarioStep Send(string ev, string? actor = null, string? signer = null, string? reason = null) =>
        new() { Id = "s", Event = ev, Actor = actor, Signer = signer, Reason = reason };

    private static ScenarioStep Wait(string after) => new() { Id = "s", Input = StepInput.Time, After = after };

    private static ScenarioStep Done(string invoke, string? actor = null, bool error = false) =>
        new() { Id = "s", Input = error ? StepInput.InvokeError : StepInput.InvokeDone, Invoke = invoke, Actor = actor };

    // ---- 4.1 bullets ----

    [Fact]
    public void Configuration_is_legal_and_reported_as_active_atomic_states_in_document_order()
    {
        var process = P(
            [S("A", StateType.Compound, S("A1"), S("A2")), S("Par", StateType.Parallel, S("R1", StateType.Compound, S("X1"), S("X2")), S("R2", StateType.Compound, S("Y1")))],
            [On("t1", "A", "go", "Par")], [E("go")]);
        var run = Run(process);

        Assert.Equal(["A1"], run.Start().Configuration);
        var trace = run.Step(Send("go"));

        Assert.Equal(["X1", "Y1"], trace.Configuration);
        Assert.Equal(["Par", "R1", "X1", "R2", "Y1"], Assert.Single(trace.Microsteps).Entered);
        Assert.Equal(["A1", "A"], trace.Microsteps[0].Exited);
    }

    [Fact]
    public void A_macrostep_runs_eventless_transitions_then_internal_events_until_neither_exists()
    {
        var process = P(
            [S("Idle"), S("Check", StateType.Choice), S("Big"), S("Small"), S("End")],
            [On("t1", "Idle", "go", "Check") with { Actions = ["bump"] }, Always("t2", "Check", "isBig", "Big"), Always("t3", "Check", null, "Small"),
                On("t4", "Big", "ping", "End")],
            [E("go"), E("ping")], [G("isBig", "context.n > 0")], [A("bump", "({ n: context.n + 1 })", "ping")], [Int("n", 0)]);
        var run = Run(process);
        run.Start();

        var trace = run.Step(Send("go"));

        Assert.Equal([["t1"], ["t2"], ["t4"]], trace.Microsteps.Select(m => m.Transitions));
        Assert.Equal(["End"], trace.Configuration);
        Assert.Equal(1, trace.Changed["n"].GetInt32());
        Assert.Empty(trace.Diagnostics);
    }

    [Fact]
    public void A_macrostep_over_the_microstep_limit_is_MQ9507()
    {
        var process = P([S("L1"), S("L2")], [Always("t1", "L1", "yes", "L2"), Always("t2", "L2", "yes", "L1")], guards: [G("yes", "context.n >= 0")],
            context: [Int("n", 0)]);

        var trace = Run(process).Start();

        Assert.Contains(trace.Diagnostics, d => d.Rule == "MQ9507");
        Assert.Equal(1000, trace.Microsteps.Count);
        Assert.True(trace.Incomplete);
    }

    [Fact]
    public void Selection_prefers_descendants_then_priority_and_reports_the_refusal_reason()
    {
        var process = P(
            [S("C", StateType.Compound, S("C1")), S("X"), S("Y"), S("Z")],
            [On("t1", "C", "e", "X"), On("t2", "C1", "e", "Y"), On("t3", "Y", "f", "Z") with { Guard = "no" }, On("t4", "Y", "f", "X"),
                On("t5", "X", "g", "Z") with { Guard = "no" }, On("t6", "X", "only", "Z")],
            [E("e"), E("f"), E("g"), E("h"), E("only", "clerk")], [G("no", "false")]);
        var run = Run(process);
        run.Start();

        Assert.Equal(["Y"], run.Step(Send("e")).Configuration);                         // the descendant's transition wins
        Assert.Equal((true, "X"), Pair(run.Step(Send("f"))));              // the first enabled in priority order
        Assert.Equal((false, "guard"), Refusal(run.Step(Send("g"))));
        Assert.Equal((false, "no-transition"), Refusal(run.Step(Send("h"))));
        Assert.Equal((false, "actor"), Refusal(run.Step(Send("only", "boss"))));
        Assert.Equal(["Z"], run.Step(Send("only", "clerk")).Configuration);
    }

    [Fact]
    public void Conflicting_transitions_across_regions_keep_the_descendant_then_document_order()
    {
        var process = P(
            [S("Par", StateType.Parallel, S("R1", StateType.Compound, S("a1"), S("a2")), S("R2", StateType.Compound, S("b1"))), S("Out")],
            [On("t1", "a1", "e", "a2"), On("t2", "Par", "e", "Out")], [E("e")]);
        var run = Run(process);
        run.Start();

        var trace = run.Step(Send("e"));

        Assert.Equal(["t1"], Assert.Single(trace.Microsteps).Transitions);
        Assert.Equal(["a2", "b1"], trace.Configuration);
    }

    [Fact]
    public void A_microstep_exits_in_reverse_document_order_runs_transition_actions_then_enters_in_document_order()
    {
        var process = P(
            [S("A", StateType.Compound, S("A1") with { Entry = [], Exit = ["xA1"] }) with { Exit = ["xA"] },
                S("B", StateType.Compound, S("B1") with { Entry = ["eB1"] }) with { Entry = ["eB"] }],
            [On("t1", "A1", "e", "B1") with { Actions = ["act"] }], [E("e")], actions: [A("xA1"), A("xA"), A("act"), A("eB"), A("eB1")]);
        var run = Run(process);
        run.Start();

        var step = Assert.Single(run.Step(Send("e")).Microsteps);

        Assert.Equal(["A1", "A"], step.Exited);
        Assert.Equal(["B", "B1"], step.Entered);
        Assert.Equal(["xA1", "xA", "act", "eB", "eB1"], step.Actions.Select(a => a.Action));
        Assert.All(step.Actions, a => Assert.Equal(ActionSource.Stub, a.Source));
    }

    [Theory]
    [InlineData(false, new[] { "A1" }, new[] { "A2" })]
    [InlineData(true, new[] { "A1", "A" }, new[] { "A", "A2" })]
    public void Transitions_are_internal_unless_external_and_targetless_ones_exit_nothing(bool external, string[] exited, string[] entered)
    {
        var process = P([S("A", StateType.Compound, S("A1"), S("A2"))],
            [On("t1", "A", "e", "A2") with { External = external }, On("t2", "A", "stay") with { Actions = ["note"] }], [E("e"), E("stay")], actions: [A("note")]);
        var run = Run(process);
        run.Start();

        var step = Assert.Single(run.Step(Send("e")).Microsteps);
        var stay = Assert.Single(run.Step(Send("stay")).Microsteps);

        Assert.Equal(exited, step.Exited);
        Assert.Equal(entered, step.Entered);
        Assert.Equal((0, 0, "note"), (stay.Exited.Count, stay.Entered.Count, Assert.Single(stay.Actions).Action));
        Assert.Equal(["A2"], run.Configuration);
    }

    [Theory]
    [InlineData(HistoryType.Shallow, "D1a")]
    [InlineData(HistoryType.Deep, "D1b")]
    public void History_restores_the_shallow_or_deep_configuration_and_defaults_before_any(HistoryType kind, string restored)
    {
        var process = P(
            [S("O"), S("D", StateType.Compound, S("D1", StateType.Compound, S("D1a"), S("D1b")), S("H", StateType.History) with { History = kind })],
            [On("t1", "D1a", "next", "D1b"), On("t2", "D", "leave", "O"), On("t3", "O", "back", "H")], [E("next"), E("leave"), E("back")]);
        var run = Run(process);
        run.Start();

        Assert.Equal(["D1a"], run.Step(Send("back")).Configuration); // no history yet: the default (D's initial)
        run.Step(Send("next"));
        run.Step(Send("leave"));

        Assert.Equal([restored], run.Step(Send("back")).Configuration);
    }

    [Fact]
    public void Done_events_fire_for_compound_and_parallel_states_and_a_root_final_child_ends_the_instance()
    {
        var process = P(
            [S("C", StateType.Compound, S("C1"), S("Cf", StateType.Final)),
                S("Par", StateType.Parallel, S("R1", StateType.Compound, S("r1"), S("r1f", StateType.Final)), S("R2", StateType.Compound, S("r2"), S("r2f", StateType.Final))),
                S("End", StateType.Final)],
            [On("t1", "C1", "finish", "Cf"), new ProcessTransition { Id = "t2", Source = "C", Trigger = TransitionTrigger.Done, Targets = ["Par"] },
                On("t3", "r1", "one", "r1f"), On("t4", "r2", "two", "r2f"), new ProcessTransition { Id = "t5", Source = "Par", Trigger = TransitionTrigger.Done, Targets = ["End"] }],
            [E("finish"), E("one"), E("two")]);
        var run = Run(process);
        run.Start();

        Assert.Equal(["r1", "r2"], run.Step(Send("finish")).Configuration);
        Assert.Equal(["r1f", "r2"], run.Step(Send("one")).Configuration);
        var last = run.Step(Send("two"));

        Assert.Equal(["End"], last.Configuration);
        Assert.True(last.Final);
        Assert.Equal((false, "no-transition"), Refusal(run.Step(Send("one"))));
    }

    [Fact]
    public void Delays_fire_on_time_inputs_in_due_order_each_as_a_macrostep_and_exit_cancels_them()
    {
        var process = P(
            [S("A"), S("B"), S("C"), S("Par", StateType.Parallel, S("R1", StateType.Compound, S("x1"), S("x2")), S("R2", StateType.Compound, S("y1"), S("y2")))],
            [new ProcessTransition { Id = "t1", Source = "A", Trigger = TransitionTrigger.After, After = "PT1H", Targets = ["B"] },
                new ProcessTransition { Id = "t2", Source = "A", Trigger = TransitionTrigger.After, After = "PT30M", Targets = ["C"] },
                On("t3", "C", "go", "Par"),
                new ProcessTransition { Id = "t4", Source = "x1", Trigger = TransitionTrigger.After, After = "P1D", Targets = ["x2"] },
                new ProcessTransition { Id = "t5", Source = "y1", Trigger = TransitionTrigger.After, After = "P1D", Targets = ["y2"] }],
            [E("go")]);
        var run = Run(process);
        run.Start();

        var first = run.Step(Wait("PT2H"));
        Assert.Equal(["C"], first.Configuration);
        Assert.Equal("2000-01-01T02:00:00Z", first.Clock);
        Assert.Equal(["t2"], Assert.Single(first.Microsteps).Transitions);          // the earlier timer; exiting A cancelled the other
        Assert.Empty(run.Timers);

        run.Step(Send("go"));
        Assert.Equal(["x1", "y1"], run.Step(Wait("PT23H")).Configuration);          // nothing due yet
        var ties = run.Step(Wait("PT1H"));
        Assert.Equal([["t4"], ["t5"]], ties.Microsteps.Select(m => m.Transitions)); // same instant: document order
        Assert.Equal(["x2", "y2"], ties.Configuration);
        Assert.Equal((false, "no-transition"), Refusal(run.Step(Wait("later"))));
    }

    [Fact]
    public void Invokes_run_sub_processes_as_nested_instances_and_wait_for_service_and_human_task_results()
    {
        var child = P([S("Work"), S("Finished", StateType.Final)], [On("c1", "Work", "finish", "Finished")], [E("finish")], id: "Child");
        var parent = P(
            [S("Wait") with { Invoke = [new ProcessInvoke { Id = "sub", Name = "sub", Type = InvokeType.Process, Process = "Child" }] },
                S("Svc") with { Invoke = [new ProcessInvoke { Id = "svc", Name = "svc", Type = InvokeType.Service }] },
                S("Human") with { Invoke = [new ProcessInvoke { Id = "task", Name = "task", Type = InvokeType.HumanTask, Actors = ["officer"] }] },
                S("Err"), S("Ok", StateType.Final)],
            [new ProcessTransition { Id = "t1", Source = "Wait", Trigger = TransitionTrigger.InvokeDone, Invoke = "sub", Targets = ["Svc"] },
                new ProcessTransition { Id = "t2", Source = "Svc", Trigger = TransitionTrigger.InvokeDone, Invoke = "svc", Targets = ["Human"] },
                new ProcessTransition { Id = "t3", Source = "Svc", Trigger = TransitionTrigger.InvokeError, Invoke = "svc", Targets = ["Err"] },
                new ProcessTransition { Id = "t4", Source = "Human", Trigger = TransitionTrigger.InvokeDone, Invoke = "task", Targets = ["Ok"] }]);
        var charts = new Dictionary<string, StatechartModel> { ["Child"] = StatechartModel.Get(child, null) };
        var run = Run(parent, id => charts.GetValueOrDefault(id));
        run.Start();

        Assert.Empty(run.Pending);                                                   // the sub-process is an instance, not a task
        Assert.Equal(["Svc"], run.Step(Send("finish")).Configuration);               // the child's final state completes the invoke
        Assert.Equal([("svc", "Svc")], run.Pending);
        Assert.Contains(run.Step(Done("task")).Diagnostics, d => d.Rule == "MQ9305"); // not pending yet
        Assert.Equal(["Human"], run.Step(Done("svc")).Configuration);
        Assert.Equal((false, "actor"), Refusal(run.Step(Done("task", "clerk"))));
        Assert.True(run.Step(Done("task", "officer")).Final);
    }

    [Fact]
    public void Gates_collect_signatures_refuse_with_reasons_complete_on_the_last_and_audit_every_attempt()
    {
        var gate = new ProcessGate
        {
            Id = "g", Name = "g", Required = 2, Signers = ["mgr", "dir"], RequiredActors = ["mgr"], ReasonRequired = true,
            Meanings = [new GateMeaning { Id = "ok", Name = "ok" }],
        };
        var process = P([S("A"), S("B"), S("C")], [On("t1", "A", "approve", "B") with { Gate = gate }, On("t2", "A", "cancel", "C"), On("t3", "C", "reopen", "A")],
            [E("approve"), E("cancel"), E("reopen")]);
        var run = Run(process);
        run.Start();

        var d1 = run.Step(Send("approve", "dir", "dana", "fine"));
        Assert.Equal((true, "A"), Pair(d1));
        Assert.Equal(GateOutcome.Signed, Assert.Single(d1.Audit).Outcome);
        Assert.Equal((false, "gate-repeat"), Refusal(run.Step(Send("approve", "dir", "dana", "again"))));
        Assert.Equal((false, "gate-signer"), Refusal(run.Step(Send("approve", "clerk", "carl", "why"))));
        Assert.Equal((false, "gate-reason"), Refusal(run.Step(Send("approve", "mgr", "mia"))));
        var d2 = run.Step(Send("approve", "dir", "dora", "fine too"));              // two signatures, but the manager is required
        Assert.Equal(["A"], d2.Configuration);
        var last = run.Step(Send("approve", "mgr", "mia", "agreed"));
        Assert.Equal(["B"], last.Configuration);
        Assert.Equal([GateOutcome.Signed, GateOutcome.Completed], last.Audit.Select(a => a.Outcome));
        Assert.Equal(("mia", "mgr", "ok", "agreed"), (last.Audit[0].Signer, last.Audit[0].Actor, last.Audit[0].Meaning, last.Audit[0].Reason));
        Assert.Equal([6L, 7L], last.Audit.Select(a => a.Sequence)); // three refused attempts were audited too

        var other = Run(process);
        other.Start();
        other.Step(Send("approve", "dir", "dana", "fine"));
        var cancelled = other.Step(Send("cancel"));
        Assert.Equal(GateOutcome.Discarded, Assert.Single(cancelled.Audit).Outcome);  // exiting the source discards the signatures
        other.Step(Send("reopen"));
        Assert.Empty(other.Signatures);
    }

    [Fact]
    public void An_event_with_actors_accepts_only_those_and_one_without_accepts_any()
    {
        var process = P([S("A"), S("B"), S("C")], [On("t1", "A", "restricted", "B"), On("t2", "B", "open", "C")], [E("restricted", "clerk", "manager"), E("open")]);
        var run = Run(process);
        run.Start();

        Assert.Equal((false, "actor"), Refusal(run.Step(Send("restricted"))));
        Assert.Equal((false, "actor"), Refusal(run.Step(Send("restricted", "guest"))));
        Assert.Equal(["B"], run.Step(Send("restricted", "manager")).Configuration);
        Assert.Equal(["C"], run.Step(Send("open")).Configuration);
    }

    [Fact]
    public void The_SPEC_section_8_example_chart_runs_reviews_in_parallel_loops_back_on_changes_and_gates_the_approval()
    {
        var gate = new ProcessGate { Id = "g", Name = "twoSignatures", Required = 2, Signers = ["approver"], Meanings = [new GateMeaning { Id = "m", Name = "approved" }] };
        var process = P(
            [S("Draft"), S("Submitted"),
                S("InReview", StateType.Parallel,
                    S("Finance", StateType.Compound, S("FinanceCheck"), S("FinanceDone", StateType.Final)),
                    S("Legal", StateType.Compound, S("LegalCheck"), S("LegalDone", StateType.Final))),
                S("Gate"), S("Approved"), S("Issued"), S("Paid", StateType.Final)],
            [On("t1", "Draft", "submit", "Submitted"), Always("t2", "Submitted", null, "InReview"), On("t3", "FinanceCheck", "financeOk", "FinanceDone"),
                On("t4", "LegalCheck", "legalOk", "LegalDone"), On("t5", "InReview", "changesRequested", "Draft") with { DisplayName = "changes requested" },
                new ProcessTransition { Id = "t6", Source = "InReview", Trigger = TransitionTrigger.Done, Targets = ["Gate"] },
                On("t7", "Gate", "approve", "Approved") with { Gate = gate }, On("t8", "Approved", "issue", "Issued"), On("t9", "Issued", "pay", "Paid")],
            [E("submit"), E("financeOk"), E("legalOk"), E("changesRequested"), E("approve", "approver"), E("issue"), E("pay")]);
        var run = Run(process);
        run.Start();

        Assert.Equal(["FinanceCheck", "LegalCheck"], run.Step(Send("submit")).Configuration);
        Assert.Equal(["FinanceDone", "LegalCheck"], run.Step(Send("financeOk")).Configuration);
        Assert.Equal(["Draft"], run.Step(Send("changesRequested")).Configuration);
        run.Step(Send("submit"));
        run.Step(Send("financeOk"));
        Assert.Equal(["Gate"], run.Step(Send("legalOk")).Configuration);
        Assert.Equal(["Gate"], run.Step(Send("approve", "approver", "ann")).Configuration);
        Assert.Equal(["Approved"], run.Step(Send("approve", "approver", "bob")).Configuration);
        run.Step(Send("issue"));
        Assert.True(run.Step(Send("pay")).Final);
    }

    [Fact]
    public void Two_runs_of_one_input_list_give_one_trace()
    {
        var process = P([S("A"), S("B")], [On("t1", "A", "e", "B") with { Actions = ["roll"] }, On("t2", "B", "e", "A") with { Actions = ["roll"] }], [E("e")],
            actions: [A("roll", "({ n: Math.floor(Math.random() * 1000000) })")], context: [Int("n", 0)]);
        string Trace()
        {
            var run = Run(process);
            var traces = new List<StepTrace> { run.Start() };
            for (var i = 0; i < 6; i++)
                traces.Add(run.Step(i % 3 == 2 ? Wait("PT1M") : Send("e")));
            return JsonSerializer.Serialize(traces);
        }

        var first = Trace();

        Assert.Equal(first, Trace());
        Assert.Contains("\"Changed\":{\"n\":", first, StringComparison.Ordinal);
    }

    // ---- expressions ----

    [Theory]
    [InlineData("context.total > context.limit", true)]
    [InlineData("({ reminders: context.reminders + 1 })", true)]
    [InlineData("context.total >", false)]
    [InlineData("1), (2", false)]
    [InlineData("0)); maquettiste.helper('x', () => 1); ((0", false)]
    public void MQ9501_expressions_parse_as_one_expression_without_running(string expression, bool parses) =>
        Assert.Equal(parses, ProcessExpressions.Parse(expression) is null);

    [Fact]
    public void MQ9501_problems_are_listed_and_the_rest_still_compiles()
    {
        var process = P([S("A")], [], guards: [G("bad", "context.n >"), G("good", "context.n > 1")], actions: [A("stub")]);

        var compiled = ProcessExpressions.Get(process);

        Assert.Equal(("bad", "/guards/0/expression", "guard"), (Assert.Single(compiled.Problems).Id, compiled.Problems[0].Pointer, compiled.Problems[0].Kind));
        Assert.Equal(["good"], compiled.Compiled);
    }

    [Theory]
    [InlineData("context.missing.deep", "MQ9502")]
    [InlineData("(() => { while (true) { } })()", "MQ9503")]
    [InlineData("1", "MQ9504")]
    public void Guard_failures_count_as_false_with_their_rule(string expression, string rule)
    {
        var process = P([S("A"), S("B")], [On("t1", "A", "e", "B") with { Guard = "g" }], [E("e")], [G("g", expression)]);
        var run = Run(process);
        run.Start();
        var watch = Stopwatch.StartNew();

        var trace = run.Step(Send("e"));

        Assert.Equal((false, "guard"), Refusal(trace));
        Assert.Equal(rule, Assert.Single(trace.Diagnostics).Rule);
        Assert.True(watch.ElapsedMilliseconds < 1000, "a stuck expression stops within the deadline");
    }

    [Theory]
    [InlineData("context.x > 0")]                                                                    // MQ9502 passing: an existing attribute
    [InlineData("(() => { let n = 0; for (let i = 0; i < 100; i++) { n++; } return n === 100; })()")] // MQ9503 passing: bounded work
    [InlineData("true")]                                                                             // MQ9504 passing: a boolean result
    public void Guards_that_read_existing_attributes_stay_within_limits_and_return_booleans_report_nothing(string expression)
    {
        var process = P([S("A"), S("B")], [On("t1", "A", "e", "B") with { Guard = "g" }], [E("e")], [G("g", expression)], context: [Int("x", 1)]);
        var run = Run(process);
        run.Start();

        var trace = run.Step(Send("e"));

        Assert.Equal((true, "B"), Pair(trace));
        Assert.Empty(trace.Diagnostics);
    }

    [Theory]
    [InlineData(true, "statement limit")]  // a generous deadline: the 100,000-statement limit stops the loop
    [InlineData(false, "time limit")]      // a generous statement limit: the 50 ms deadline stops the loop
    public void MQ9503_reports_the_statement_limit_and_the_deadline_separately(bool statements, string limit)
    {
        var process = P([S("A"), S("B")], [On("t1", "A", "e", "B") with { Guard = "g" }], [E("e")], [G("g", "(() => { while (true) { } })()")]);
        var limits = statements
            ? ProcessExpressions.Limits with { ScriptTimeoutMs = 60_000 }
            : ProcessExpressions.Limits with { ScriptStatements = int.MaxValue };
        var run = new StatechartInterpreter(StatechartModel.Get(process, null), new InterpreterOptions
        {
            Expressions = chart =>
            {
                var session = ProcessExpressions.Get(chart.Process).Open(1, CancellationToken.None, limits);
                _sessions.Add(session);
                return session;
            },
        });
        run.Start();

        var diagnostic = Assert.Single(run.Step(Send("e")).Diagnostics);

        Assert.Equal("MQ9503", diagnostic.Rule);
        Assert.Contains(limit, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MQ9505_unknown_attributes_and_ill_typed_values_are_ignored()
    {
        var process = P([S("A"), S("B")], [On("t1", "A", "e", "B") with { Actions = ["set"] }], [E("e")],
            actions: [A("set", "({ nope: 1, n: 'text', m: 5 })")], context: [Int("n", 0), Int("m", 0)]);
        var run = Run(process, fits: (_, v) => v.ValueKind == JsonValueKind.Number ? null : "not a number");
        run.Start();

        var trace = run.Step(Send("e"));

        Assert.Equal(2, trace.Diagnostics.Count(d => d.Rule == "MQ9505"));
        Assert.Equal(["m"], trace.Changed.Keys);
        Assert.Equal(0, trace.Context["n"].GetInt32());
    }

    [Theory]
    [InlineData("context.n > 0", true)]
    [InlineData("context.n > 5", false)]
    public void MQ9506_two_guarded_transitions_enabled_at_once_take_the_first(string second, bool overlap)
    {
        var process = P([S("A"), S("B"), S("C")], [On("t1", "A", "e", "B") with { Guard = "g1" }, On("t2", "A", "e", "C") with { Guard = "g2" }, On("t3", "A", "e", "C")],
            [E("e")], [G("g1", "context.n > 0"), G("g2", second)], context: [Int("n", 1)]);
        var run = Run(process);
        run.Start();

        var trace = run.Step(Send("e"));

        Assert.Equal(["B"], trace.Configuration);
        Assert.Equal(overlap, trace.Diagnostics.Any(d => d.Rule == "MQ9506"));
    }

    [Fact]
    public void A_guard_without_expression_takes_the_assume_value_or_is_missing()
    {
        var process = P([S("A"), S("B")], [On("t1", "A", "e", "B") with { Guard = "stub" }], [E("e")], [G("stub", null)]);
        var run = Run(process);
        run.Start();

        var missing = run.Step(Send("e"));
        var assumed = run.Step(Send("e") with { Assume = ImmutableDictionary<string, bool>.Empty.Add("stub", true) });

        Assert.Equal((GuardSource.Missing, "MQ9306", true), (Assert.Single(missing.Guards).Source, Assert.Single(missing.Diagnostics).Rule, missing.Incomplete));
        Assert.Equal((GuardSource.Assumed, true, "B"), (Assert.Single(assumed.Guards).Source, assumed.Accepted, string.Join(",", assumed.Configuration)));
    }

    [Fact]
    public void Done_travels_up_through_nested_parallel_states_once_every_region_is_final()
    {
        var process = P(
            [S("O", StateType.Parallel,
                S("PP", StateType.Parallel, S("RA", StateType.Compound, S("a1"), S("aF", StateType.Final)), S("RB", StateType.Compound, S("b1"), S("bF", StateType.Final))),
                S("C", StateType.Compound, S("cF", StateType.Final))),
                S("Out")],
            [On("t1", "a1", "e", "aF"), On("t2", "b1", "e", "bF"), new ProcessTransition { Id = "t3", Source = "O", Trigger = TransitionTrigger.Done, Targets = ["Out"] }],
            [E("e")]);
        var run = Run(process);
        run.Start();

        Assert.Equal(["Out"], run.Step(Send("e")).Configuration);
    }

    [Fact]
    public void A_final_region_of_a_parallel_state_waits_for_the_other_regions()
    {
        var process = P(
            [S("Par", StateType.Parallel, S("R1", StateType.Compound, S("X1"), S("XF", StateType.Final)), S("F", StateType.Final)), S("Out")],
            [On("t1", "X1", "e", "XF"), new ProcessTransition { Id = "t2", Source = "Par", Trigger = TransitionTrigger.Done, Targets = ["Out"] }],
            [E("e")]);
        var run = Run(process);

        Assert.Equal(["X1", "F"], run.Start().Configuration);
        Assert.Equal(["Out"], run.Step(Send("e")).Configuration);
    }

    [Fact]
    public void A_gated_transition_preempted_by_a_conflict_gets_no_signature()
    {
        var gate = new ProcessGate { Id = "g", Name = "g", Required = 1, Signers = ["a"], Meanings = [new GateMeaning { Id = "ok", Name = "ok" }] };
        var process = P(
            [S("Par", StateType.Parallel, S("R1", StateType.Compound, S("X1")), S("R2", StateType.Compound, S("Y1"), S("Y2"))), S("Out")],
            [On("t1", "X1", "e", "Out"), On("t2", "Y1", "e", "Y2") with { Gate = gate }],
            [E("e")]);
        var run = Run(process);
        run.Start();

        var trace = run.Step(Send("e", "a", "u1"));

        Assert.Equal(["Out"], trace.Configuration);
        Assert.Equal(["t1"], trace.Microsteps[0].Transitions);
        Assert.Empty(trace.Audit);
    }

    private static (bool, string?) Refusal(StepTrace trace) => (trace.Accepted, trace.Refusal);

    private static (bool, string) Pair(StepTrace trace) => (trace.Accepted, string.Join(",", trace.Configuration));
}
