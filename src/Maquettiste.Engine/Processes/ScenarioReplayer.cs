using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// What one validation, verification or simulation run shares across processes: the resolved charts (cached by process hash) and one
/// expression session per process, created on first use and disposed with the runtime. Thread-safe.
/// </summary>
public sealed class ProcessRuntime : IDisposable
{
    private readonly ConcurrentDictionary<StatechartModel, Lazy<ProcessExpressionSession?>> _sessions = new(ReferenceEqualityComparer.Instance);
    private readonly int _poolSize;
    private int _disposed;

    /// <summary>Creates a runtime over a model snapshot.</summary>
    /// <param name="model">The model.</param>
    /// <param name="poolSize">The most sandboxes a process's pool keeps idle (the run's parallelism).</param>
    /// <param name="ct">The run's cancellation.</param>
    public ProcessRuntime(ModelSnapshot model, int poolSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        _poolSize = Math.Max(1, poolSize);
        CancellationToken = ct;
    }

    /// <summary>The model.</summary>
    public ModelSnapshot Model { get; }

    /// <summary>The run's cancellation.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The resolved chart of a process of the model.</summary>
    /// <param name="processId">The process id.</param>
    /// <returns>The chart, or <see langword="null"/> when the id names no process.</returns>
    public StatechartModel? Chart(string processId) =>
        Model.Get<Process>(processId) is { } process ? StatechartModel.Get(process, Model.GetDocument(processId)?.Hash) : null;

    /// <summary>The expression session of a chart, or <see langword="null"/> when none of its expressions compiled.</summary>
    /// <param name="chart">The chart.</param>
    /// <returns>The session.</returns>
    public ProcessExpressionSession? Session(StatechartModel chart)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _sessions.GetOrAdd(chart, c => new Lazy<ProcessExpressionSession?>(() =>
        {
            var compiled = ProcessExpressions.Get(c.Process);
            return compiled.Compiled.Count == 0 ? null : compiled.Open(_poolSize, CancellationToken);
        })).Value;
    }

    /// <summary>The interpreter options over this runtime's model.</summary>
    /// <param name="instance">The instance identity recorded in audit records.</param>
    /// <returns>The options.</returns>
    public InterpreterOptions Options(string instance) => new()
    {
        Instance = instance,
        SubProcess = Chart,
        Expressions = Session,
        ActorName = id => Model.Get<Actor>(id)?.Name,
        Fits = Fits,
        CancellationToken = CancellationToken,
    };

    /// <summary>Why a value does not fit an attribute, or <see langword="null"/>.</summary>
    /// <param name="attribute">The attribute.</param>
    /// <param name="value">The value.</param>
    /// <returns>The reason.</returns>
    public string? Fits(ModelAttribute attribute, JsonElement value) =>
        AttributeRules.DefaultMismatch(AttributeRules.Resolve(Model, attribute.Type), attribute, value);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var lazy in _sessions.Values)
        {
            if (lazy.IsValueCreated)
                lazy.Value?.Dispose();
        }
    }
}

/// <summary>The first failed expectation of a replay.</summary>
/// <param name="Step">The step index, or -1 for the outcome.</param>
/// <param name="Rule">The rule (MQ9301 to MQ9304).</param>
/// <param name="Message">The message.</param>
/// <param name="Expected">What the scenario expects.</param>
/// <param name="Actual">What the replay gave.</param>
public sealed record ScenarioFailure(int Step, string Rule, string Message, JsonElement Expected, JsonElement Actual);

/// <summary>A scenario replayed through the interpreter (phase-3-design.md sections 3 and 4.1).</summary>
/// <param name="Scenario">The scenario.</param>
/// <param name="Start">The initial entry's trace.</param>
/// <param name="Steps">The traces of the steps that ran.</param>
/// <param name="Diagnostics">MQ93xx and MQ95xx findings, on the scenario.</param>
/// <param name="Complete">Whether every step ran to a meaningful result (no MQ9305, MQ9306 or MQ9507 stop).</param>
/// <param name="Final">Whether the root is final after the last step that ran.</param>
/// <param name="Failure">The first failed expectation.</param>
public sealed record ScenarioReplay(Scenario Scenario, StepTrace? Start, IReadOnlyList<StepTrace> Steps, IReadOnlyList<Diagnostic> Diagnostics, bool Complete,
    bool Final, ScenarioFailure? Failure)
{
    /// <summary>Whether the scenario ran to the end and every expectation held.</summary>
    public bool Passed => Complete && Failure is null && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>The expectations the replay gives: one per step that ran, and the outcome.</summary>
    /// <returns>The step expectations and the outcome.</returns>
    public (IReadOnlyList<StepExpectation> Steps, ScenarioOutcome Outcome) Observed() =>
        ([.. Steps.Select(t => new StepExpectation { Accepted = t.Accepted, States = t.Configuration, Context = t.Changed })],
            Final ? ScenarioOutcome.Final : ScenarioOutcome.Active);
}

/// <summary>
/// Replays a scenario through the engine interpreter and compares each step's acceptance, active states and changed context, and the
/// outcome (MQ9301 to MQ9306; phase-3-design.md section 3). A step's field problems (MQ9305) and a guard without expression or assume
/// value (MQ9306) stop the replay at that step. After a failed expectation the remaining steps still run (their traces feed
/// <c>refresh-scenario</c>) but are not compared: only the first failing step is reported, since later ones would only repeat it.
/// </summary>
public static class ScenarioReplayer
{
    /// <summary>Replays a scenario against its process in the model.</summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="runtime">The run's shared runtime.</param>
    /// <returns>The replay, or <see langword="null"/> when the scenario's process is not in the model (MQ2001 reports it).</returns>
    public static ScenarioReplay? Replay(Scenario scenario, ProcessRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Chart(scenario.Process) is { } chart ? Replay(scenario, chart, runtime) : null;
    }

    /// <summary>Replays a scenario against a chart (a saved process or a draft).</summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="chart">The chart.</param>
    /// <param name="runtime">The run's shared runtime.</param>
    /// <returns>The replay.</returns>
    public static ScenarioReplay Replay(Scenario scenario, StatechartModel chart, ProcessRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(runtime);
        var diagnostics = new List<Diagnostic>();
        var seen = new HashSet<(string, string)>();
        var interpreter = new StatechartInterpreter(chart, runtime.Options(scenario.Id));
        DateTimeOffset? at = null;
        var badAt = false;
        if (scenario.Start?.At is { } startAt)
        {
            if (ProcessApi.TryParseAt(startAt, out var parsed))
                at = parsed;
            else
                badAt = true;
        }

        var start = interpreter.Start(scenario.Start?.Context, at);
        Report(start, "/start");
        if (badAt)
            Add(RuleCatalog.Create("MQ9305", $"Scenario '{scenario.Name}' starts at '{scenario.Start!.At}', which is not an ISO 8601 date-time; give one such as 2026-01-01T09:00:00Z, or remove start.at.", scenario.Id, null, "/start/at"));
        var steps = new List<StepTrace>();
        ScenarioFailure? failure = null;
        var complete = !start.Incomplete && !badAt;
        for (var i = 0; complete && i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            var pointer = "/steps/" + N(i);
            if (CheckFields(chart, step, runtime, i) is { } problem)
            {
                Add(RuleCatalog.Create("MQ9305", problem.Message, scenario.Id, null, pointer + problem.Field));
                complete = false;
                break;
            }

            var trace = interpreter.Step(step);
            steps.Add(trace);
            Report(trace, pointer);
            if (trace.Incomplete)
            {
                complete = false;
                break;
            }

            if (failure is null && step.Expect is { } expect)
                failure = Compare(chart, scenario, i, step, expect, trace, pointer);
        }

        if (complete && failure is null)
        {
            var final = interpreter.IsFinal;
            if (final != (scenario.Outcome == ScenarioOutcome.Final))
            {
                var expected = scenario.Outcome == ScenarioOutcome.Final ? "final" : "active";
                var actual = final ? "final" : "active";
                var message = $"Scenario '{scenario.Name}' expects outcome {expected}, but the process is {actual} after the last step; set outcome to {actual}, fix the steps, or update the expectations from a replay.";
                failure = new ScenarioFailure(-1, "MQ9304", message, JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
                Add(RuleCatalog.Create("MQ9304", message, scenario.Id, null, "/outcome"));
            }
        }

        return new ScenarioReplay(scenario, start, steps, diagnostics, complete, interpreter.IsFinal, failure);

        void Report(StepTrace trace, string pointer)
        {
            foreach (var d in trace.Diagnostics)
                Add(d with { ElementId = scenario.Id, FilePath = null, JsonPointer = pointer, Message = At(trace.Index) + d.Message });
        }

        void Add(Diagnostic d)
        {
            if (seen.Add((d.Rule, d.Message)))
                diagnostics.Add(d);
        }

        ScenarioFailure? Compare(StatechartModel c, Scenario s, int i, ScenarioStep step, StepExpectation expect, StepTrace trace, string pointer)
        {
            ScenarioFailure? first = null;
            if (expect.Accepted != trace.Accepted)
            {
                var message = trace.Accepted
                    ? $"{At(i)}{Describe(c, step)} was accepted, but expect.accepted is false; fix the step's input, or set accepted to true."
                    : $"{At(i)}{Describe(c, step)} was refused ({trace.Refusal}), but expect.accepted is true; fix the step's input (event, actor, signer, reason or assume), or set accepted to false.";
                first ??= Fail("MQ9301", message, expect.Accepted, trace.Accepted, pointer + "/expect/accepted");
            }

            if (expect.States.Count > 0 && !expect.States.SequenceEqual(trace.Configuration, StringComparer.Ordinal))
            {
                var message = $"{At(i)}{Describe(c, step)} leaves the process in {Paths(c, trace.Configuration)}, but expect.states lists {Paths(c, expect.States)}; fix the chart or the step, or update the expectations from a replay.";
                first ??= Fail("MQ9302", message, expect.States, trace.Configuration, pointer + "/expect/states");
            }

            if (!SameMap(expect.Context, trace.Changed))
            {
                var message = $"{At(i)}{Describe(c, step)} changes {Values(c, trace.Changed)}, but expect.context lists {Values(c, expect.Context)}; fix the actions or the step, or update the expectations from a replay.";
                first ??= Fail("MQ9303", message, expect.Context, trace.Changed, pointer + "/expect/context");
            }

            return first;

            ScenarioFailure Fail(string rule, string message, object expected, object actual, string at)
            {
                Add(RuleCatalog.Create(rule, message, s.Id, null, at));
                return new ScenarioFailure(i, rule, message, JsonSerializer.SerializeToElement(expected), JsonSerializer.SerializeToElement(actual));
            }
        }
    }

    // MQ9305: fields the interpreter cannot judge without the model (payload keys and values, meanings); the interpreter itself reports
    // unknown events, bad durations and invokes that are not pending.
    private static (string Message, string Field)? CheckFields(StatechartModel chart, ScenarioStep step, ProcessRuntime runtime, int index)
    {
        if (step.Input != StepInput.Event || step.Event is null || !chart.Events.TryGetValue(step.Event, out var ev))
            return null;
        var gates = chart.Transitions.Where(t => t.Trigger == TransitionTrigger.Event && string.Equals(t.Transition.Event, step.Event, StringComparison.Ordinal))
            .Select(t => t.Gate).OfType<ProcessGate>().ToList();
        foreach (var (id, value) in step.Payload)
        {
            var attribute = ev.Payload.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))
                ?? gates.SelectMany(g => g.AuditAttributes).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            if (attribute is null)
                return ($"{At(index)}the payload names '{id}', which is not a payload attribute of event '{ev.Name}' or an audit attribute of its gate; remove it or add the attribute to the event.", "/payload");
            if (runtime.Fits(attribute, value) is { } why)
                return ($"{At(index)}the payload value of '{attribute.Name}' does not fit its type ({why}); give a value of the attribute's type.", "/payload");
        }

        if (step.Meaning is { } meaning && !gates.Any(g => g.Meanings.Any(m => string.Equals(m.Id, meaning, StringComparison.Ordinal))))
            return ($"{At(index)}the meaning '{meaning}' is not a meaning of a gate on event '{ev.Name}'; pick one of the gate's meanings.", "/meaning");
        return null;
    }

    private static string At(int index) => index < 0 ? "Initial entry: " : "Step " + N(index + 1) + ": ";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Describe(StatechartModel chart, ScenarioStep step) => step.Input switch
    {
        StepInput.Time => "advancing the clock by " + step.After,
        StepInput.InvokeDone or StepInput.InvokeError => (step.Input == StepInput.InvokeDone ? "completing " : "failing ")
            + (step.Invoke is not null && chart.Invokes.TryGetValue(step.Invoke, out var i) ? "'" + i.Invoke.Name + "'" : "the invoke"),
        _ => "event " + (step.Event is not null && chart.Events.TryGetValue(step.Event, out var e) ? "'" + e.Name + "'" : "'" + step.Event + "'"),
    };

    private static string Paths(StatechartModel chart, IEnumerable<string> states)
    {
        var list = states.Select(id => chart.ById.TryGetValue(id, out var s) ? s.Path : id).ToList();
        return list.Count == 0 ? "no states" : string.Join(", ", list);
    }

    private static string Values(StatechartModel chart, IReadOnlyDictionary<string, JsonElement> values) =>
        values.Count == 0 ? "nothing"
            : string.Join(", ", values.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => (chart.Attributes.TryGetValue(p.Key, out var a) ? a.Name : p.Key) + " = " + p.Value.GetRawText()));

    private static bool SameMap(IReadOnlyDictionary<string, JsonElement> a, IReadOnlyDictionary<string, JsonElement> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && JsonValues.Same(p.Value, v));
}
