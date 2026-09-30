using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>What the interpreter needs beyond the chart.</summary>
public sealed record InterpreterOptions
{
    /// <summary>The instance identity recorded in audit records (supplied by the host).</summary>
    public string Instance { get; init; } = "instance";

    /// <summary>The most microsteps one macrostep may run (MQ9507 above).</summary>
    public int MaxMicrosteps { get; init; } = 1000;

    /// <summary>Resolves a sub-process invoke's process id to its chart; <see langword="null"/> leaves the invoke pending.</summary>
    public Func<string, StatechartModel?>? SubProcess { get; init; }

    /// <summary>Opens (or returns) the expression session of a chart; <see langword="null"/> treats every guard and action as a stub.</summary>
    public Func<StatechartModel, ProcessExpressionSession?>? Expressions { get; init; }

    /// <summary>An actor's name for the <c>event.actor</c> an expression sees; the id when absent.</summary>
    public Func<string, string?>? ActorName { get; init; }

    /// <summary>Why a value does not fit a context attribute (MQ9505), or <see langword="null"/>; no check when absent.</summary>
    public Func<ModelAttribute, JsonElement, string?>? Fits { get; init; }

    /// <summary>Cancellation of the expressions' sandbox calls.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// The engine's statechart interpreter (phase-3-design.md section 4.1), used by validation, simulation, scenario verification and
/// recording, and the reference the generated interpreters are tested against. It serves those uses only: it persists nothing and runs
/// nothing for the host. Deterministic: the clock is simulated and advances only on <c>time</c> inputs, collections are ordered by
/// document order, and the sandbox's <c>Math.random</c> is seeded with the process id and the input index. Not thread-safe.
/// </summary>
public sealed class StatechartInterpreter
{
    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    private readonly InterpreterOptions _options;
    private readonly Instance _root;
    private readonly Dictionary<StatechartModel, ProcessExpressionSession?> _sessions = new(ReferenceEqualityComparer.Instance);
    private long _auditSequence;
    private long _timerSequence;
    private int _index = -1;
    private int _micro;
    private bool _started;
    private Scope _scope = new(null);

    /// <summary>Creates an interpreter for one instance of a chart.</summary>
    /// <param name="chart">The resolved chart.</param>
    /// <param name="options">The options.</param>
    public StatechartInterpreter(StatechartModel chart, InterpreterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        _options = options ?? new InterpreterOptions();
        _root = new Instance(chart, null, null, 0);
    }

    /// <summary>The chart.</summary>
    public StatechartModel Chart => _root.Chart;

    /// <summary>The simulated clock.</summary>
    public DateTimeOffset Clock { get; private set; }

    /// <summary>Whether the root reached a final child.</summary>
    public bool IsFinal => _root.Done;

    /// <summary>The active atomic states, in document order.</summary>
    public IReadOnlyList<string> Configuration => Leaves(_root).Select(s => s.Id).ToList();

    /// <summary>The context by attribute id.</summary>
    public IReadOnlyDictionary<string, JsonElement> Context => new SortedDictionary<string, JsonElement>(_root.Context, StringComparer.Ordinal);

    /// <summary>The pending service and human tasks: the invoke id and the state that started it.</summary>
    public IReadOnlyList<(string Invoke, string State)> Pending =>
        [.. AllInstances().SelectMany(i => i.Pending).Select(p => (p.Invoke.Id, p.State.Id))];

    /// <summary>The scheduled timers: the transition and its due instant, in due order.</summary>
    public IReadOnlyList<(string Transition, DateTimeOffset DueAt)> Timers =>
        [.. AllInstances().SelectMany(i => i.Timers).OrderBy(t => t, TimerOrder.Instance).Select(t => (t.Transition.Id, t.Due))];

    /// <summary>The signatures collected on the gates of active states, by transition id.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string Signer, string Actor, string? Meaning, string? Reason)>> Signatures =>
        _root.Signatures.Where(p => p.Value.Count > 0).OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => (IReadOnlyList<(string, string, string?, string?)>)[.. p.Value.Select(s => (s.Signer, s.Actor, s.Meaning, s.Reason))], StringComparer.Ordinal);

    /// <summary>Enters the initial configuration (the initial entry is one macrostep).</summary>
    /// <param name="context">Initial context values over the attribute defaults, by attribute id.</param>
    /// <param name="at">The clock's start; the sandbox's fixed instant when absent.</param>
    /// <returns>The trace of the initial entry (index -1).</returns>
    public StepTrace Start(IReadOnlyDictionary<string, JsonElement>? context = null, DateTimeOffset? at = null)
    {
        if (_started)
            throw new InvalidOperationException("The interpreter has already started.");
        _started = true;
        Clock = at ?? DateTimeOffset.Parse(ScenarioStart.DefaultStart, CultureInfo.InvariantCulture);
        _scope = new Scope(null);
        InitContext(_root, context);
        var before = Snapshot();
        Macrostep(() =>
        {
            StartInstance(_root);
            return true;
        });
        return Finish(before);
    }

    /// <summary>Runs one input as a macrostep (a <c>time</c> input runs one macrostep per due timer).</summary>
    /// <param name="input">The input; its <c>expect</c> is ignored.</param>
    /// <returns>The trace.</returns>
    public StepTrace Step(ScenarioStep input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!_started)
            Start();
        _index++;
        _scope = new Scope(input);
        var before = Snapshot();
        switch (input.Input)
        {
            case StepInput.Time:
                HandleTime(input);
                break;
            case StepInput.InvokeDone or StepInput.InvokeError:
                HandleInvoke(input);
                break;
            default:
                HandleEvent(input);
                break;
        }

        return Finish(before);
    }

    // ---- inputs ----

    private void HandleEvent(ScenarioStep input)
    {
        if (_root.Done)
        {
            Refuse(Refusals.NoTransition);
            return;
        }

        var target = input.Event is null ? null : AllInstances().FirstOrDefault(i => i.Chart.Events.ContainsKey(input.Event));
        if (target is null)
        {
            Problem("MQ9305", $"The event '{input.Event}' is not an event of process '{Chart.Process.Name}' or of a running sub-process; pick one of its events.");
            Refuse(Refusals.NoTransition);
            return;
        }

        var ev = target.Chart.Events[input.Event!];
        if (ev.Actors.Count > 0 && (input.Actor is null || !ev.Actors.Contains(input.Actor, StringComparer.Ordinal)))
        {
            Refuse(Refusals.Actor);
            return;
        }

        _scope.Event = EventObject(ev.Name, input.Actor, ev.Payload, input.Payload);
        Macrostep(() =>
        {
            var selection = Select(target, TransitionTrigger.Event, ev.Id, external: true);
            if (selection.Transitions.Count == 0 && !selection.Consumed)
            {
                Refuse(selection.Reason);
                return false;
            }

            if (selection.Transitions.Count > 0)
                Microstep(target, selection.Transitions);
            RunToCompletion(target);
            Propagate(target);
            return true;
        });
    }

    private void HandleTime(ScenarioStep input)
    {
        if (StatechartModel.ParseDuration(input.After) is not { } span || span <= TimeSpan.Zero)
        {
            Problem("MQ9305", $"A time step needs a positive ISO 8601 duration in after (for example P5D), not '{input.After}'.");
            Refuse(Refusals.NoTransition);
            return;
        }

        var end = Clock + span;
        _scope.Event = EventObject(null, input.Actor, [], ImmutableDictionary<string, JsonElement>.Empty);
        while (!_root.Done)
        {
            var next = AllInstances().SelectMany(i => i.Timers).Where(t => t.Due <= end).OrderBy(t => t, TimerOrder.Instance).FirstOrDefault();
            if (next is null)
                break;
            Clock = next.Due;
            next.Owner.Timers.Remove(next);
            if (next.Owner.Done)
                continue;
            Macrostep(() =>
            {
                if (Guard(next.Owner, next.Transition) != true)
                    return true;
                Microstep(next.Owner, [next.Transition]);
                RunToCompletion(next.Owner);
                Propagate(next.Owner);
                return true;
            });
        }

        Clock = end;
    }

    private void HandleInvoke(ScenarioStep input)
    {
        var found = AllInstances().SelectMany(i => i.Pending.Select(p => (Instance: i, p.Invoke, p.State)))
            .FirstOrDefault(p => string.Equals(p.Invoke.Id, input.Invoke, StringComparison.Ordinal));
        if (found.Instance is null)
        {
            var name = input.Invoke is not null && Chart.Invokes.TryGetValue(input.Invoke, out var known) ? known.Invoke.Name : input.Invoke;
            Problem("MQ9305", $"The invoke '{name}' is not pending (its state is not active, or it already completed); complete an invoke of an active state.");
            Refuse(Refusals.NoTransition);
            return;
        }

        if (found.Invoke.Type == InvokeType.HumanTask && found.Invoke.Actors.Count > 0
            && (input.Actor is null || !found.Invoke.Actors.Contains(input.Actor, StringComparer.Ordinal)))
        {
            Refuse(Refusals.Actor);
            return;
        }

        var trigger = input.Input == StepInput.InvokeError ? TransitionTrigger.InvokeError : TransitionTrigger.InvokeDone;
        _scope.Event = EventObject(null, input.Actor, [], ImmutableDictionary<string, JsonElement>.Empty);
        Macrostep(() =>
        {
            var selection = SelectAt(found.Instance, found.State, trigger, found.Invoke.Id);
            if (selection.Transitions.Count == 0)
            {
                Refuse(selection.Reason);
                return false;
            }

            found.Instance.Pending.RemoveAll(p => ReferenceEquals(p.Invoke, found.Invoke));
            Microstep(found.Instance, selection.Transitions);
            RunToCompletion(found.Instance);
            Propagate(found.Instance);
            return true;
        });
    }

    // ---- macrostep ----

    private void Macrostep(Func<bool> body)
    {
        _micro = 0;
        try
        {
            body();
        }
        catch (MacrostepOverflowException)
        {
            Problem("MQ9507", $"A macrostep of process '{Chart.Process.Name}' ran more than {_options.MaxMicrosteps.ToString(CultureInfo.InvariantCulture)} microsteps; break the eventless loop or the chain of raised events with a guard that turns false.");
        }
    }

    private void RunToCompletion(Instance inst)
    {
        while (!inst.Done)
        {
            var always = Select(inst, TransitionTrigger.Always, "", external: false);
            if (always.Transitions.Count > 0)
            {
                Microstep(inst, always.Transitions);
                continue;
            }

            if (inst.Queue.Count == 0)
                break;
            var next = inst.Queue.Dequeue();
            var saved = _scope.Event;
            List<ChartTransition> transitions;
            switch (next.Kind)
            {
                case TransitionTrigger.Event:
                    var ev = inst.Chart.Events[next.Id];
                    _scope.Event = EventObject(ev.Name, null, [], ImmutableDictionary<string, JsonElement>.Empty);
                    transitions = Select(inst, TransitionTrigger.Event, next.Id, external: false).Transitions;
                    break;
                case TransitionTrigger.Done:
                    transitions = inst.Chart.ById.TryGetValue(next.Id, out var state) && inst.Config.Contains(state)
                        ? SelectAt(inst, state, TransitionTrigger.Done, "").Transitions
                        : [];
                    break;
                default:
                    var invoke = inst.Chart.Invokes[next.Id];
                    transitions = inst.Config.Contains(invoke.State) ? SelectAt(inst, invoke.State, next.Kind, next.Id).Transitions : [];
                    break;
            }

            if (transitions.Count > 0)
                Microstep(inst, transitions);
            _scope.Event = saved;
        }
    }

    private void Propagate(Instance inst)
    {
        for (var child = inst; child.Parent is { } parent && child.Done && !child.Reported; child = parent)
        {
            child.Reported = true;
            parent.Queue.Enqueue(new Internal(TransitionTrigger.InvokeDone, child.ParentInvoke!));
            RunToCompletion(parent);
        }
    }

    // ---- selection ----

    private sealed record Selection(List<ChartTransition> Transitions, bool Consumed, string Reason);

    private Selection Select(Instance inst, TransitionTrigger trigger, string key, bool external)
    {
        var enabled = new List<ChartTransition>();
        var tried = new Dictionary<ChartTransition, bool>(ReferenceEqualityComparer.Instance);
        var consumed = false;
        var reason = Refusals.NoTransition;
        var rank = 0;
        foreach (var leaf in Leaves(inst))
        {
            var found = false;
            foreach (var s in leaf.Ancestors.Insert(0, leaf))
            {
                if (s.IsRoot)
                    break;
                foreach (var t in inst.Chart.Candidates(s, trigger, key))
                {
                    if (tried.TryGetValue(t, out var taken))
                    {
                        if (taken)
                        {
                            found = true;
                            break;
                        }

                        continue;
                    }

                    tried[t] = false;
                    if (Guard(inst, t) != true)
                    {
                        Fail(Refusals.Guard, 1);
                        continue;
                    }

                    if (t.Gate is { } gate)
                    {
                        if (!external)
                        {
                            Fail(Refusals.GateSigner, 2);
                            continue;
                        }

                        // Checked now, signed only when the transition survives conflict removal (a preempted gate gets no signature).
                        if (CheckSigner(inst, t, gate) is { } refusal)
                        {
                            Fail(refusal, 2);
                            continue;
                        }
                    }

                    CheckOverlap(inst, s, t, trigger, key);
                    tried[t] = true;
                    enabled.Add(t);
                    found = true;
                    break;
                }

                if (found)
                    break;
            }
        }

        var selected = RemoveConflicts(inst, enabled, t =>
        {
            if (t.Gate is not { } gate || Sign(inst, t, gate))
                return true;
            consumed = true; // the occurrence signed an incomplete gate: it is accepted, and the transition waits for more signatures
            return false;
        });
        return new Selection(selected, consumed, reason);

        void Fail(string why, int level)
        {
            if (level > rank)
            {
                rank = level;
                reason = why;
            }
        }
    }

    private Selection SelectAt(Instance inst, ChartState source, TransitionTrigger trigger, string key)
    {
        var reason = Refusals.NoTransition;
        foreach (var t in inst.Chart.Candidates(source, trigger, key))
        {
            if (Guard(inst, t) == true)
            {
                CheckOverlap(inst, source, t, trigger, key);
                return new Selection([t], false, reason);
            }

            reason = Refusals.Guard;
        }

        return new Selection([], false, reason);
    }

    // MQ9506: a later guarded transition of the same source and trigger is also enabled. Unguarded fallbacks (a choice's else) are
    // intended and not reported; a guard without an expression is consulted only when the step assumes it.
    private void CheckOverlap(Instance inst, ChartState source, ChartTransition taken, TransitionTrigger trigger, string key)
    {
        if (taken.Guard is null)
            return;
        var after = false;
        foreach (var t in inst.Chart.Candidates(source, trigger, key))
        {
            if (ReferenceEquals(t, taken))
            {
                after = true;
                continue;
            }

            if (!after || t.Guard is null || t.Gate is not null)
                continue;
            if (!CanEvaluate(inst, t.Guard) || Guard(inst, t) != true)
                continue;
            Problem("MQ9506", $"Transitions {Label(taken)} and {Label(t)} of state '{source.Path}' were both enabled; the first in priority order was taken. Make guards '{taken.Guard.Name}' and '{t.Guard.Name}' exclusive, or reorder the transitions.");
        }
    }

    // `admit` runs for a transition that survives the transitions kept before it; returning false leaves it out without preempting any.
    private List<ChartTransition> RemoveConflicts(Instance inst, List<ChartTransition> enabled, Func<ChartTransition, bool> admit)
    {
        var filtered = new List<ChartTransition>();
        foreach (var t1 in enabled)
        {
            if (filtered.Contains(t1))
                continue;
            var preempted = false;
            var remove = new List<ChartTransition>();
            var exit1 = ExitSet(inst, t1);
            foreach (var t2 in filtered)
            {
                if (!exit1.Overlaps(ExitSet(inst, t2)))
                    continue;
                if (t1.Source.IsDescendantOf(t2.Source))
                {
                    remove.Add(t2);
                }
                else
                {
                    preempted = true;
                    break;
                }
            }

            if (preempted || !admit(t1))
                continue;
            filtered.RemoveAll(remove.Contains);
            filtered.Add(t1);
        }

        return filtered;
    }

    // ---- guards and actions ----

    private bool CanEvaluate(Instance inst, ProcessGuard guard) =>
        (guard.Expression is not null && Session(inst.Chart) is not null && ProcessExpressions.Get(inst.Chart.Process).Compiled.Contains(guard.Id))
        || (_scope.Input?.Assume.ContainsKey(guard.Id) ?? false);

    private bool? Guard(Instance inst, ChartTransition t)
    {
        if (t.Guard is not { } guard)
            return t.Transition.Guard is null; // an undeclared guard (MQ2001) never holds
        if (guard.Expression is not null && Session(inst.Chart) is { } session && ProcessExpressions.Get(inst.Chart.Process).Compiled.Contains(guard.Id))
        {
            var result = session.Evaluate(guard.Id, $"Guard '{guard.Name}'", ContextObject(inst), _scope.Event, Seed(inst), _options.CancellationToken);
            bool value;
            if (result.Diagnostic is { } d)
            {
                _scope.Diagnostics.Add(d);
                value = false;
            }
            else if (result.Value is bool b)
            {
                value = b;
            }
            else
            {
                Problem("MQ9504", $"Guard '{guard.Name}' returned {Describe(result.Value)}, which counts as false; make the expression return true or false.", guard.Id);
                value = false;
            }

            _scope.Guards.Add(new GuardEvaluation(guard.Id, t.Id, value, GuardSource.Expression));
            return value;
        }

        if (_scope.Input is { } input && input.Assume.TryGetValue(guard.Id, out var assumed))
        {
            _scope.Guards.Add(new GuardEvaluation(guard.Id, t.Id, assumed, GuardSource.Assumed));
            return assumed;
        }

        _scope.Guards.Add(new GuardEvaluation(guard.Id, t.Id, null, GuardSource.Missing));
        Problem("MQ9306", $"Guard '{guard.Name}' has no expression and the step gives no assume value for it, so the replay stops here; add it to the step's assume, or give the guard an expression.", guard.Id);
        return null;
    }

    private ActionRun RunAction(Instance inst, ProcessAction action)
    {
        var changed = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        var source = ActionSource.Stub;
        if (action.Expression is not null && Session(inst.Chart) is { } session && ProcessExpressions.Get(inst.Chart.Process).Compiled.Contains(action.Id))
        {
            source = ActionSource.Expression;
            var result = session.Evaluate(action.Id, $"Action '{action.Name}'", ContextObject(inst), _scope.Event, Seed(inst), _options.CancellationToken);
            if (result.Diagnostic is { } d)
            {
                _scope.Diagnostics.Add(d);
            }
            else if (result.Value is IReadOnlyDictionary<string, object?> map)
            {
                foreach (var (name, raw) in map)
                {
                    var attribute = inst.Chart.Process.Context.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
                    var value = ProcessExpressionSession.ToJson(raw);
                    if (attribute is null)
                    {
                        Problem("MQ9505", $"Action '{action.Name}' returned '{name}', which is not a context attribute of process '{inst.Chart.Process.Name}'; the entry is ignored. Return only context attribute names.", action.Id);
                        continue;
                    }

                    if (_options.Fits?.Invoke(attribute, value) is { } why)
                    {
                        Problem("MQ9505", $"Action '{action.Name}' returned a value for '{name}' that does not fit its type ({why}); the entry is ignored. Return a value of the attribute's type.", action.Id);
                        continue;
                    }

                    if (!inst.Context.TryGetValue(attribute.Id, out var old) || !JsonValues.Same(old, value))
                        changed[attribute.Id] = value;
                    inst.Context[attribute.Id] = value;
                }
            }
            else if (result.Value is not null)
            {
                Problem("MQ9505", $"Action '{action.Name}' returned {Describe(result.Value)}, not an object of context updates; the result is ignored. Return an object such as ({{ name: value }}).", action.Id);
            }
        }

        foreach (var raised in action.Raises)
        {
            if (inst.Chart.Events.ContainsKey(raised))
                inst.Queue.Enqueue(new Internal(TransitionTrigger.Event, raised));
        }

        return new ActionRun(action.Id, source, changed);
    }

    // ---- gates ----

    // Refuses (and audits the refused attempt) when the input cannot sign the gate; null when it can.
    private string? CheckSigner(Instance inst, ChartTransition t, ProcessGate gate)
    {
        var input = _scope.Input!;
        var signatures = inst.Signatures.TryGetValue(t.Id, out var existing) ? existing : [];
        string? refusal = null;
        if (input.Actor is null || string.IsNullOrEmpty(input.Signer) || !gate.Signers.Contains(input.Actor, StringComparer.Ordinal))
            refusal = Refusals.GateSigner;
        else if (!gate.AllowRepeatSigner && signatures.Any(s => string.Equals(s.Signer, input.Signer, StringComparison.Ordinal)))
            refusal = Refusals.GateRepeat;
        else if (gate.ReasonRequired && string.IsNullOrWhiteSpace(input.Reason))
            refusal = Refusals.GateReason;
        if (refusal is not null)
        {
            var (meaning, attributes) = AuditFields(input, gate);
            Audit(inst, gate, t, input.Signer, input.Actor, meaning, input.Reason, GateOutcome.Refused, attributes);
        }

        return refusal;
    }

    private static (string? Meaning, SortedDictionary<string, JsonElement> Attributes) AuditFields(ScenarioStep input, ProcessGate gate)
    {
        var meaning = input.Meaning ?? (gate.Meanings.Count == 1 ? gate.Meanings[0].Id : null);
        var attributes = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var a in gate.AuditAttributes)
        {
            if (input.Payload.TryGetValue(a.Id, out var value))
                attributes[a.Id] = value;
        }

        return (meaning, attributes);
    }

    // Adds the input's signature (already checked by CheckSigner); true when it completes the gate.
    private bool Sign(Instance inst, ChartTransition t, ProcessGate gate)
    {
        var input = _scope.Input!;
        if (!inst.Signatures.TryGetValue(t.Id, out var signatures))
            inst.Signatures[t.Id] = signatures = [];
        var (meaning, attributes) = AuditFields(input, gate);
        signatures.Add(new Signature(input.Signer!, input.Actor!, meaning, input.Reason));
        Audit(inst, gate, t, input.Signer, input.Actor, meaning, input.Reason, GateOutcome.Signed, attributes);
        var complete = signatures.Count >= Math.Max(1, gate.Required)
            && gate.RequiredActors.All(a => signatures.Any(s => string.Equals(s.Actor, a, StringComparison.Ordinal)));
        if (complete)
        {
            Audit(inst, gate, t, input.Signer, input.Actor, meaning, input.Reason, GateOutcome.Completed, attributes);
            signatures.Clear();
        }

        return complete;
    }

    private void Audit(Instance inst, ProcessGate gate, ChartTransition t, string? signer, string? actor, string? meaning, string? reason, GateOutcome outcome,
        IReadOnlyDictionary<string, JsonElement> attributes) =>
        _scope.Audit.Add(new GateAuditRecord(_options.Instance, inst.Chart.Process.Id, gate.Id, t.Id, ++_auditSequence, signer, actor, meaning, reason,
            ClockText(), outcome, attributes));

    // ---- microstep ----

    private void Microstep(Instance inst, IReadOnlyList<ChartTransition> transitions, ChartState? initial = null)
    {
        if (++_micro > _options.MaxMicrosteps)
            throw new MacrostepOverflowException();
        var exited = new List<string>();
        var entered = new List<string>();
        var actions = new List<ActionRun>();

        // Exit, in reverse document order.
        var exitSet = new HashSet<ChartState>(ReferenceEqualityComparer.Instance);
        foreach (var t in transitions)
            exitSet.UnionWith(ExitSet(inst, t));
        var exiting = exitSet.OrderByDescending(s => s.Order).ToList();
        foreach (var s in exiting)
        {
            foreach (var h in s.Children.Where(c => c.Type == StateType.History))
            {
                inst.History[h.Id] = h.State!.History == HistoryType.Deep
                    ? [.. inst.Config.Where(c => c.IsLeaf && c.IsDescendantOf(s)).OrderBy(c => c.Order)]
                    : [.. inst.Config.Where(c => ReferenceEquals(c.Parent, s)).OrderBy(c => c.Order)];
            }
        }

        foreach (var s in exiting)
        {
            foreach (var id in s.State?.Exit ?? [])
            {
                if (inst.Chart.Actions.TryGetValue(id, out var action))
                    actions.Add(RunAction(inst, action));
            }

            Cancel(inst, s);
            inst.Config.Remove(s);
            exited.Add(s.Id);
        }

        // Transition actions, in transition order.
        foreach (var t in transitions)
        {
            foreach (var action in t.Actions)
                actions.Add(RunAction(inst, action));
        }

        // Entry, in document order.
        var toEnter = new HashSet<ChartState>(ReferenceEqualityComparer.Instance);
        if (initial is not null)
        {
            AddDescendants(inst, initial, toEnter);
            AddAncestors(inst, initial, inst.Chart.Root, toEnter);
        }

        foreach (var t in transitions)
        {
            if (t.Targets.IsEmpty)
                continue;
            var domain = Domain(inst, t);
            foreach (var s in t.Targets)
                AddDescendants(inst, s, toEnter);
            foreach (var s in EffectiveTargets(inst, t))
                AddAncestors(inst, s, domain, toEnter);
        }

        var doneQueued = new HashSet<ChartState>(ReferenceEqualityComparer.Instance);
        foreach (var s in toEnter.OrderBy(s => s.Order))
        {
            if (s.IsRoot || !inst.Config.Add(s))
                continue;
            entered.Add(s.Id);
            foreach (var id in s.State?.Entry ?? [])
            {
                if (inst.Chart.Actions.TryGetValue(id, out var action))
                    actions.Add(RunAction(inst, action));
            }

            foreach (var timer in s.Timers)
                inst.Timers.Add(new Timer(inst, timer, Clock + timer.Delay!.Value, ++_timerSequence));
            StartInvokes(inst, s);
            if (s.Type == StateType.Final && s.Parent is { } parent)
            {
                if (parent.IsRoot)
                {
                    inst.Done = true;
                }
                else
                {
                    // A final child completes a compound parent; a parallel state completes when every region is final, and that
                    // completion travels up through enclosing parallel states (each queued once per microstep).
                    if (parent.Type != StateType.Parallel && doneQueued.Add(parent))
                        inst.Queue.Enqueue(new Internal(TransitionTrigger.Done, parent.Id));
                    for (var marker = parent.Type == StateType.Parallel ? parent : parent.Parent;
                         marker is { Type: StateType.Parallel, IsRoot: false } && marker.Children.All(r => InFinal(inst, r));
                         marker = marker.Parent)
                    {
                        if (doneQueued.Add(marker))
                            inst.Queue.Enqueue(new Internal(TransitionTrigger.Done, marker.Id));
                    }
                }
            }
        }

        if (inst.Done)
        {
            inst.Timers.Clear();
            inst.Pending.Clear();
            inst.Children.Clear();
            inst.Queue.Clear();
        }

        _scope.Microsteps.Add(new MicrostepTrace([.. transitions.Select(t => t.Id)], exited, entered, actions));
    }

    private void Cancel(Instance inst, ChartState s)
    {
        inst.Timers.RemoveAll(t => ReferenceEquals(t.Transition.Source, s));
        inst.Pending.RemoveAll(p => ReferenceEquals(p.State, s));
        foreach (var invoke in s.Invokes)
            inst.Children.Remove(invoke.Id);
        foreach (var t in s.Transitions)
        {
            if (t.Gate is { } gate && inst.Signatures.TryGetValue(t.Id, out var signatures) && signatures.Count > 0)
            {
                Audit(inst, gate, t, null, null, null, null, GateOutcome.Discarded, ImmutableDictionary<string, JsonElement>.Empty);
                signatures.Clear();
            }
        }
    }

    private void StartInvokes(Instance inst, ChartState s)
    {
        foreach (var invoke in s.Invokes)
        {
            if (invoke.Type == InvokeType.Process && invoke.Process is not null && inst.Depth < 16
                && _options.SubProcess?.Invoke(invoke.Process) is { } chart)
            {
                var child = new Instance(chart, inst, invoke.Id, inst.Depth + 1);
                inst.Children[invoke.Id] = child;
                InitContext(child, null);
                StartInstance(child);
                if (child.Done)
                {
                    child.Reported = true;
                    inst.Queue.Enqueue(new Internal(TransitionTrigger.InvokeDone, invoke.Id));
                }

                continue;
            }

            inst.Pending.Add((invoke, s));
        }
    }

    private void StartInstance(Instance inst)
    {
        inst.Config.Add(inst.Chart.Root);
        if (inst.Chart.Root.InitialChild is { } initial)
            Microstep(inst, [], initial);
        RunToCompletion(inst);
    }

    private bool InFinal(Instance inst, ChartState region) => region.Type switch
    {
        StateType.Parallel => region.Children.All(r => InFinal(inst, r)),
        StateType.Final => inst.Config.Contains(region),
        _ => region.Children.Any(c => c.Type == StateType.Final && inst.Config.Contains(c)),
    };

    // ---- exit and entry sets ----

    private HashSet<ChartState> ExitSet(Instance inst, ChartTransition t)
    {
        var set = new HashSet<ChartState>(ReferenceEqualityComparer.Instance);
        if (Domain(inst, t) is { } domain)
        {
            foreach (var s in inst.Config)
            {
                if (s.IsDescendantOf(domain))
                    set.Add(s);
            }
        }

        return set;
    }

    private ChartState? Domain(Instance inst, ChartTransition t)
    {
        var targets = EffectiveTargets(inst, t);
        if (targets.Count == 0)
            return null;
        if (!t.External && targets.All(s => ReferenceEquals(s, t.Source) || s.IsDescendantOf(t.Source)))
            return t.Source;
        var all = new List<ChartState>(targets) { t.Source };
        foreach (var ancestor in all[0].Ancestors)
        {
            if (all.Skip(1).All(s => s.IsDescendantOf(ancestor)))
                return ancestor;
        }

        return inst.Chart.Root;
    }

    private List<ChartState> EffectiveTargets(Instance inst, ChartTransition t)
    {
        var list = new List<ChartState>();
        foreach (var target in t.Targets)
            Effective(target, 0);
        return list;

        void Effective(ChartState s, int depth)
        {
            if (s.Type != StateType.History)
            {
                if (!list.Contains(s))
                    list.Add(s);
                return;
            }

            if (inst.History.TryGetValue(s.Id, out var recorded) && !recorded.IsEmpty)
            {
                foreach (var r in recorded)
                    Effective(r, depth + 1);
            }
            else if (s.DefaultTarget is { } d && depth < 8)
            {
                Effective(d, depth + 1);
            }
        }
    }

    private void AddDescendants(Instance inst, ChartState s, HashSet<ChartState> toEnter, int depth = 0)
    {
        if (depth > 64)
            return;
        if (s.Type == StateType.History)
        {
            var parent = s.Parent!;
            if (inst.History.TryGetValue(s.Id, out var recorded) && !recorded.IsEmpty)
            {
                foreach (var r in recorded)
                    AddDescendants(inst, r, toEnter, depth + 1);
                foreach (var r in recorded)
                    AddAncestors(inst, r, parent, toEnter, depth + 1);
            }
            else if (s.DefaultTarget is { } d)
            {
                AddDescendants(inst, d, toEnter, depth + 1);
                AddAncestors(inst, d, parent, toEnter, depth + 1);
            }

            return;
        }

        toEnter.Add(s);
        if (s.Type == StateType.Parallel)
        {
            foreach (var region in s.Children)
            {
                if (region.Type != StateType.History && !toEnter.Any(x => x.IsDescendantOf(region)))
                    AddDescendants(inst, region, toEnter, depth + 1);
            }
        }
        else if (!s.Children.IsEmpty && s.InitialChild is { } initial)
        {
            AddDescendants(inst, initial, toEnter, depth + 1);
            AddAncestors(inst, initial, s, toEnter, depth + 1);
        }
    }

    private void AddAncestors(Instance inst, ChartState s, ChartState? domain, HashSet<ChartState> toEnter, int depth = 0)
    {
        var ancestors = new List<ChartState>();
        foreach (var a in s.Ancestors)
        {
            if (ReferenceEquals(a, domain))
                break;
            ancestors.Add(a);
        }

        if (domain is { Type: StateType.Parallel })
            ancestors.Add(domain);
        foreach (var a in ancestors)
        {
            if (!ReferenceEquals(a, domain) && !a.IsRoot)
                toEnter.Add(a);
            if (a.Type != StateType.Parallel)
                continue;
            foreach (var region in a.Children)
            {
                if (region.Type != StateType.History && !toEnter.Any(x => x.IsDescendantOf(region)))
                    AddDescendants(inst, region, toEnter, depth + 1);
            }
        }
    }

    // ---- helpers ----

    private void InitContext(Instance inst, IReadOnlyDictionary<string, JsonElement>? start)
    {
        foreach (var attribute in inst.Chart.Process.Context)
            inst.Context[attribute.Id] = attribute.Default ?? Null;
        if (start is null)
            return;
        foreach (var (id, value) in start)
        {
            if (inst.Chart.Attributes.ContainsKey(id))
                inst.Context[id] = value;
        }
    }

    private StepTrace Finish(SortedDictionary<string, JsonElement> before)
    {
        var changed = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in _root.Context)
        {
            if (!before.TryGetValue(id, out var old) || !JsonValues.Same(old, value))
                changed[id] = value;
        }

        var diagnostics = _scope.Diagnostics.Distinct().ToList();
        return new StepTrace(_index, _scope.Input, _scope.Refusal is null, _scope.Refusal, _scope.Microsteps, _scope.Guards, _scope.Audit,
            Configuration, Snapshot(), changed, ClockText(), _root.Done, diagnostics);
    }

    private SortedDictionary<string, JsonElement> Snapshot() => new(_root.Context, StringComparer.Ordinal);

    private void Refuse(string reason) => _scope.Refusal ??= reason;

    private void Problem(string rule, string message, string? elementId = null) =>
        _scope.Diagnostics.Add(RuleCatalog.Create(rule, message, elementId ?? Chart.Process.Id));

    private ProcessExpressionSession? Session(StatechartModel chart)
    {
        if (_options.Expressions is null)
            return null;
        if (!_sessions.TryGetValue(chart, out var session))
            _sessions[chart] = session = _options.Expressions(chart);
        return session;
    }

    private string Seed(Instance inst) => inst.Chart.Process.Id + ":" + _index.ToString(CultureInfo.InvariantCulture);

    private string ClockText() => Clock.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static string Label(ChartTransition t) => t.Transition.DisplayName is { Length: > 0 } d ? "'" + d + "'" : "#" + t.Index.ToString(CultureInfo.InvariantCulture);

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => "the string '" + s + "'",
        double d => "the number " + d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IReadOnlyDictionary<string, object?> => "an object",
        _ => "a list",
    };

    private JsonElement ContextObject(Instance inst)
    {
        var map = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var attribute in inst.Chart.Process.Context)
            map[attribute.Name] = inst.Context.TryGetValue(attribute.Id, out var v) ? v : Null;
        return JsonSerializer.SerializeToElement(map);
    }

    private JsonElement EventObject(string? name, string? actor, IReadOnlyList<ModelAttribute> declared, IReadOnlyDictionary<string, JsonElement> payload)
    {
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var attribute in declared)
        {
            if (payload.TryGetValue(attribute.Id, out var v))
                values[attribute.Name] = v;
        }

        var actorName = actor is null ? null : _options.ActorName?.Invoke(actor) ?? actor;
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["name"] = name, ["actor"] = actorName, ["payload"] = values });
    }

    private static IEnumerable<ChartState> Leaves(Instance inst) =>
        inst.Config.Where(s => !s.IsRoot && s.IsLeaf).OrderBy(s => s.Order);

    private IEnumerable<Instance> AllInstances()
    {
        var stack = new Stack<Instance>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var inst = stack.Pop();
            if (inst.Done && inst.Parent is not null)
                continue;
            yield return inst;
            foreach (var child in inst.Children.OrderByDescending(c => c.Key, StringComparer.Ordinal))
                stack.Push(child.Value);
        }
    }

    // ---- state ----

    private sealed class Instance(StatechartModel chart, Instance? parent, string? parentInvoke, int depth)
    {
        public StatechartModel Chart { get; } = chart;
        public Instance? Parent { get; } = parent;
        public string? ParentInvoke { get; } = parentInvoke;
        public int Depth { get; } = depth;
        public HashSet<ChartState> Config { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, ImmutableArray<ChartState>> History { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, JsonElement> Context { get; } = new(StringComparer.Ordinal);
        public List<Timer> Timers { get; } = [];
        public List<(ProcessInvoke Invoke, ChartState State)> Pending { get; } = [];
        public SortedDictionary<string, Instance> Children { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<Signature>> Signatures { get; } = new(StringComparer.Ordinal);
        public Queue<Internal> Queue { get; } = new();
        public bool Done { get; set; }
        public bool Reported { get; set; }
    }

    private sealed record Timer(Instance Owner, ChartTransition Transition, DateTimeOffset Due, long Sequence);

    private sealed record Signature(string Signer, string Actor, string? Meaning, string? Reason);

    private sealed record Internal(TransitionTrigger Kind, string Id);

    private sealed class Scope(ScenarioStep? input)
    {
        public ScenarioStep? Input { get; } = input;
        public JsonElement Event { get; set; } = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["name"] = null, ["actor"] = null, ["payload"] = new Dictionary<string, object?>() });
        public string? Refusal { get; set; }
        public List<MicrostepTrace> Microsteps { get; } = [];
        public List<GuardEvaluation> Guards { get; } = [];
        public List<GateAuditRecord> Audit { get; } = [];
        public List<Diagnostic> Diagnostics { get; } = [];
    }

    private sealed class TimerOrder : IComparer<Timer>
    {
        public static TimerOrder Instance { get; } = new();

        public int Compare(Timer? x, Timer? y)
        {
            var c = x!.Due.CompareTo(y!.Due);
            if (c == 0) c = x.Owner.Depth.CompareTo(y.Owner.Depth);
            if (c == 0) c = x.Transition.Source.Order.CompareTo(y.Transition.Source.Order);
            if (c == 0) c = x.Transition.Index.CompareTo(y.Transition.Index);
            return c == 0 ? x.Sequence.CompareTo(y.Sequence) : c;
        }
    }

    private sealed class MacrostepOverflowException : Exception;
}

/// <summary>JSON value comparison that treats numbers by value (<c>1</c> equals <c>1.0</c>) and objects by their members.</summary>
public static class JsonValues
{
    /// <summary>Whether two JSON values are the same value.</summary>
    /// <param name="a">The first value.</param>
    /// <param name="b">The second value.</param>
    /// <returns><see langword="true"/> when they are.</returns>
    public static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : a.GetDouble().Equals(b.GetDouble());
        }

        if (a.ValueKind != b.ValueKind)
            return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength())
                    return false;
                return a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Same(p.First, p.Second));
            case JsonValueKind.Object:
                var left = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var right = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                return left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var v) && Same(p.Value, v));
            default:
                return true;
        }
    }
}
