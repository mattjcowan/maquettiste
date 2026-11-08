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

    /// <summary>
    /// Whether an acting actor (the second argument) satisfies a listed actor (the first): it is that actor or one of its members at any
    /// depth (actors-design.md decision 5). Events, human tasks, gate signers and required actors use it; ordinal equality when absent.
    /// </summary>
    public Func<string, string, bool>? Accepts { get; init; }

    /// <summary>The project's system actor (actors-design.md decision 2), named by a trace whose move gives no actor: the initial entry,
    /// a time step or a finished invoke that is not a human task. No trace names it when absent.</summary>
    public string? SystemActor { get; init; }

    /// <summary>Why a value does not fit a context attribute (MQ9505), or <see langword="null"/>; no check when absent.</summary>
    public Func<ModelAttribute, JsonElement, string?>? Fits { get; init; }

    /// <summary>Cancellation of the expressions' sandbox calls.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// The object the run's process runs for (lifecycle-design.md decision 22): a JSON object of its entity's attributes and its bases' by
    /// name, each the value given, else the declaring attribute's default, else null, with each linked object nested by its navigation
    /// name (an object for a to-one end, a list for a to-many one). An empty object when absent or not an object; an invoked
    /// sub-process sees an empty object.
    /// </summary>
    public JsonElement? Subject { get; init; }

    /// <summary>
    /// The notes of the states the subject's row assumed for its objects (object-effects-design.md decision 10), by
    /// <see cref="StateRead.Key"/>: a guard or set whose <c>in_state</c> reads one puts its note in the step's trace.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? StateNotes { get; init; }

    /// <summary>
    /// The run's rights check (rights-design.md section 7): what a participant may do to the object, which events, human tasks, signatures and
    /// verb steps of the root process ask. Without it, verb steps are accepted as given and only the closed lists are checked, as before rights.
    /// </summary>
    internal RunRights? Rights { get; init; }

    /// <summary>
    /// The run's trigger points (trigger-points-design.md section 8), set only in a project that uses them: the root's moves, signatures, human
    /// tasks and verb steps run them in the fixed order. Without it, nothing runs and selection works as before trigger points.
    /// </summary>
    internal RunTriggers? Triggers { get; init; }
}

/// <summary>
/// The engine's statechart interpreter (phase-3-design.md section 4.1), used by validation, simulation, scenario verification and
/// recording, and the reference the generated interpreters are tested against. It serves those uses only: it persists nothing and runs
/// nothing for the host. Deterministic: the clock is simulated and advances only on <c>time</c> inputs (a guard's condition reads it as
/// <c>now()</c>), collections are ordered by document order, and the sandbox's <c>Math.random</c> is seeded with the process id and the
/// input index. Not thread-safe.
/// </summary>
public sealed partial class StatechartInterpreter
{
    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();
    private static readonly JsonElement NoObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly InterpreterOptions _options;
    private readonly Instance _root;
    private readonly JsonElement _subject;
    // The values the run's sets gave the object, by attribute id, and the object's row with them (object-effects-design.md section 7): the
    // row is copied only when a set runs, so a run without sets reads the row it was given.
    private readonly SortedDictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private JsonElement _current;
    private readonly Dictionary<StatechartModel, ProcessExpressionSession?> _sessions = new(ReferenceEqualityComparer.Instance);
    private long _auditSequence;
    private long _timerSequence;
    private int _index = -1;
    private int _micro;
    private bool _started;
    // The object was deleted by a verb step (rights-design.md decision 25): the chart stops where it stood.
    private bool _deleted;
    // Whether the subject is frozen, and the index of the step that froze it (-1: the run started it frozen); the subject's frozen links the
    // same way, by relation end and linked object (rights-design.md decision 16).
    private bool _frozen;
    private int _frozeAt = -1;
    private readonly Dictionary<(string End, string Object), int> _frozenLinks = [];
    private Scope _scope = new(null);

    /// <summary>Creates an interpreter for one instance of a chart.</summary>
    /// <param name="chart">The resolved chart.</param>
    /// <param name="options">The options.</param>
    public StatechartInterpreter(StatechartModel chart, InterpreterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        _options = options ?? new InterpreterOptions();
        _root = new Instance(chart, null, null, 0);
        _subject = _options.Subject is { ValueKind: JsonValueKind.Object } subject ? subject : NoObject;
        _current = _subject;
        if (_options.Rights is { } rights)
        {
            _frozen = rights.StartsFrozen;
            foreach (var link in rights.FrozenLinks)
                _frozenLinks.TryAdd(link, -1);
        }
    }

    /// <summary>The values the run's sets gave the object so far, by attribute id (object-effects-design.md decision 4).</summary>
    internal IReadOnlyDictionary<string, JsonElement> Values => _values;

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
    public StepTrace Step(ScenarioStep input) => Step(input, null);

    /// <summary>Runs one input as a macrostep, for a participant: <c>current_actor()</c> reads its identity when the step gives no signer.</summary>
    /// <param name="input">The input; its <c>expect</c> is ignored.</param>
    /// <param name="identity">The participant's identity as text (its name when it has none), or <see langword="null"/>.</param>
    /// <param name="pick">
    /// The named signature picked with the actor the participant acts as (signoff-design.md decision 8), or <see langword="null"/> for the step's
    /// own or one picked in the step; the trace still echoes the input as given.
    /// </param>
    /// <param name="parties">The participants the step names for a delegation (signoff-design.md decision 34), or <see langword="null"/>.</param>
    /// <returns>The trace.</returns>
    internal StepTrace Step(ScenarioStep input, string? identity, string? pick = null, StepParties? parties = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!_started)
            Start();
        _index++;
        _scope = new Scope(input) { Identity = identity, Pick = pick, Parties = parties };
        var before = Snapshot();
        // A word in assume names a check the team writes of a lifecycle the step reaches (MQ9305, trigger-points-design.md decision 26).
        if (_options.Triggers is { } triggers && input.Assume.Count > 0 && triggers.AssumeProblem(input) is { } problem)
        {
            Problem("MQ9305", char.ToUpperInvariant(problem[0]) + problem[1..]);
            Refuse(Refusals.NoTransition);
            return Finish(before);
        }

        switch (input.Input)
        {
            case StepInput.Time:
                HandleTime(input);
                break;
            case StepInput.InvokeDone or StepInput.InvokeError:
                HandleInvoke(input);
                break;
            case StepInput.Verb:
                HandleVerb(input);
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
        if (_root.Done || _deleted)
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
        var root = ReferenceEquals(target, _root);
        // The root's events answer to rights before any guard picks a transition (rights-design.md decisions 12 to 14), with the signers of the
        // gates the event can reach as an allow; an invoked sub-process sees no object, so its events answer to their own actors only.
        if (root && _options.Rights is { Uses: true } rights)
        {
            var decision = CheckRights(rights, ev.Verb, [new RightsList(ev.Id, ev.Actors)], GateSigners(target, ev.Id), null);
            if (decision is { Allowed: false, Refusal: { } refusal })
            {
                Refuse(refusal, decision.RefusedBy);
                return;
            }
        }
        else
        {
            // Freezing refuses first (decision 17), in a project without rights too: naming freeze is the opt-in (decision 16). With no end
            // known, a frozen link of the subject refuses disconnect.
            if (root && RightsEvaluator.RefusedByFreezing(ev.Verb, null, _frozen, _frozenLinks.Count > 0))
            {
                Refuse(Refusals.Frozen);
                return;
            }

            if (ev.Actors.Count > 0 && (input.Actor is null || !Allows(ev.Actors, input.Actor)))
            {
                Refuse(Refusals.Actor, ev.Id);
                return;
            }
        }

        _scope.Thaws = root && string.Equals(ev.Verb, "thaw", StringComparison.Ordinal);
        _scope.Event = EventObject(ev.Name, input.Actor, ev.Payload, input.Payload);
        Macrostep(() =>
        {
            var selection = Select(target, TransitionTrigger.Event, ev.Id, external: true);
            if (selection.Transitions.Count == 0 && !selection.Consumed)
            {
                RefuseSelection(selection);
                return false;
            }

            // Whoever sent the event did its moves (signoff-design.md decision 28); a gated move's acts are recorded when it completes.
            foreach (var t in selection.Transitions)
            {
                if (t.Gate is null)
                    RecordAct(target, Person(), MoveVerb(t), t.Id);
            }

            if (selection.Transitions.Count > 0)
                Microstep(target, selection.Transitions);
            RunToCompletion(target);
            Propagate(target);
            return true;
        });

        // An accepted event that names freeze or thaw freezes or thaws the subject (decision 16), after its move.
        if (root && _scope.Refusal is null && ev.Verb is "freeze" or "thaw")
            Freeze(ev.Verb == "freeze", null, null);
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
        if (_deleted)
        {
            Clock = end; // the object is gone: no timer fires, and time only moves the clock
            return;
        }

        _scope.Event = EventObject(null, input.Actor, [], ImmutableDictionary<string, JsonElement>.Empty);
        while (!_root.Done)
        {
            var next = AllInstances().SelectMany(i => i.Timers).Where(t => t.Due <= end).OrderBy(t => t, TimerOrder.Instance).FirstOrDefault();
            if (next is null)
                break;
            // Reminders and escalations due by the timer's instant fall due before it fires (signoff-design.md decision 35).
            DueNotices(next.Due);
            Clock = next.Due;
            next.Owner.Timers.Remove(next);
            if (next.Owner.Done)
                continue;
            Macrostep(() =>
            {
                if (Guard(next.Owner, next.Transition) != true)
                    return true;
                // Rows 8 and 9 right after the timer's guard (trigger-points-design.md decision 18): a refusing check skips its move.
                if (TriggerCandidate(next.Owner, next.Transition, external: false) is not null)
                    return true;
                Microstep(next.Owner, [next.Transition]);
                RunToCompletion(next.Owner);
                Propagate(next.Owner);
                return true;
            });
        }

        if (!_root.Done)
            DueNotices(end);
        Clock = end;
    }

    private void HandleInvoke(ScenarioStep input)
    {
        if (_deleted)
        {
            Refuse(Refusals.NoTransition);
            return;
        }

        var found = AllInstances().SelectMany(i => i.Pending.Select(p => (Instance: i, p.Invoke, p.State)))
            .FirstOrDefault(p => string.Equals(p.Invoke.Id, input.Invoke, StringComparison.Ordinal));
        if (found.Instance is null)
        {
            var name = input.Invoke is not null && Chart.Invokes.TryGetValue(input.Invoke, out var known) ? known.Invoke.Name : input.Invoke;
            _scope.HumanTask = input.Invoke is not null && Chart.Invokes.TryGetValue(input.Invoke, out var declared) && declared.Invoke.Type == InvokeType.HumanTask;
            Problem("MQ9305", $"The invoke '{name}' is not pending (its state is not active, or it already completed); complete an invoke of an active state.");
            Refuse(Refusals.NoTransition);
            return;
        }

        _scope.HumanTask = found.Invoke.Type == InvokeType.HumanTask;
        // A human task of the root answers to rights on complete, done or failed (rights-design.md decision 14).
        if (_scope.HumanTask && ReferenceEquals(found.Instance, _root) && _options.Rights is { Uses: true } rights)
        {
            var decision = CheckRights(rights, "complete", [new RightsList(found.Invoke.Id, found.Invoke.Actors)], [], null);
            if (decision is { Allowed: false, Refusal: { } refusal })
            {
                Refuse(refusal, decision.RefusedBy);
                return;
            }
        }
        else if (_scope.HumanTask && found.Invoke.Actors.Count > 0 && (input.Actor is null || !Allows(found.Invoke.Actors, input.Actor)))
        {
            Refuse(Refusals.Actor, found.Invoke.Id);
            return;
        }

        var trigger = input.Input == StepInput.InvokeError ? TransitionTrigger.InvokeError : TransitionTrigger.InvokeDone;
        _scope.Event = EventObject(null, input.Actor, [], ImmutableDictionary<string, JsonElement>.Empty);
        // A human task of the root ends with the verb complete (trigger-points-design.md section 8): its before actions and checks run before
        // the transition is picked, and a refusing check refuses the step, so the task stays pending.
        var completion = _scope.HumanTask && ReferenceEquals(found.Instance, _root) ? PrepareVerb("complete", null, null, null, create: false) : null;
        if (completion?.Refused is { } refusing)
        {
            _scope.TriggerRuns.Add(refusing);
            Refuse(Refusals.Check, refusing.TriggerPoint);
            return;
        }

        Macrostep(() =>
        {
            var selection = SelectAt(found.Instance, found.State, trigger, found.Invoke.Id, own: true);
            if (selection.Transitions.Count == 0)
            {
                RefuseSelection(selection);
                return false;
            }

            // complete as a verb of its own around the task's end: its before values, the end, its with-change and after-commit points.
            if (completion is not null)
            {
                List(completion, []);
                Write(completion, []);
            }

            found.Instance.Pending.RemoveAll(p => ReferenceEquals(p.Invoke, found.Invoke));
            if (completion is not null)
                RunLater(completion, []);
            // A person who ends a human task did complete and its move (signoff-design.md decision 28); another invoke's end is nobody's.
            if (_scope.HumanTask)
            {
                RecordAct(found.Instance, Person(), "complete", null);
                foreach (var t in selection.Transitions)
                    RecordAct(found.Instance, Person(), MoveVerb(t), t.Id);
            }

            Microstep(found.Instance, selection.Transitions);
            RunToCompletion(found.Instance);
            Propagate(found.Instance);
            return true;
        });
    }

    // A participant does a verb to the subject or one of its links (rights-design.md decision 25): checked as rights say, then applied. modify
    // on the subject writes its values to the object's row, so later guards and sets see them; delete removes the object and stops the chart;
    // the rest are checked and recorded only.
    private void HandleVerb(ScenarioStep input)
    {
        if (_root.Done || _deleted)
        {
            Refuse(Refusals.NoTransition);
            return;
        }

        var rights = _options.Rights;
        if (input.Verb is not { } verb || rights?.Fields(input) is { } problem)
        {
            var message = input.Verb is null ? "the verb step names no verb; give the verb the participant does." : rights!.Fields(input)!.Value.Message;
            Problem("MQ9305", char.ToUpperInvariant(message[0]) + message[1..]);
            Refuse(Refusals.NoTransition);
            return;
        }

        if (rights is not null && CheckRights(rights, verb, [], [], input.End, StepAttributes(input, verb)) is { Allowed: false, Refusal: { } refusal } decision)
        {
            Refuse(refusal, decision.RefusedBy);
            return;
        }

        if (rights is null && RightsEvaluator.RefusedByFreezing(verb, input.End, _frozen, LinkFrozen(input.End, input.Object)))
        {
            Refuse(Refusals.Frozen);
            return;
        }

        // The values a modify step on the subject, or a create step, gives (trigger-points-design.md decision 27), by attribute id with its name.
        var own = new SortedDictionary<string, (string Name, JsonElement Value)>(StringComparer.Ordinal);
        if ((verb == "modify" && input.End is null) || verb == "create")
        {
            foreach (var (id, value) in input.Values ?? ImmutableDictionary<string, JsonElement>.Empty)
            {
                if (rights?.AttributeName(id) is { } name)
                    own[id] = (name, value);
            }
        }

        // Rows 8 and 9 after rights and freezing (trigger-points-design.md decision 18): matching reads the step's own attributes only.
        var prepared = PrepareVerb(verb, own, StepAttributes(input, verb), input.End, create: verb == "create");
        if (prepared?.Refused is { } refused)
        {
            _scope.TriggerRuns.Add(refused);
            Refuse(Refusals.Check, refused.TriggerPoint);
            return;
        }

        // The proposed values are written before the effect; copy and revise make a new object the run doesn't hold, so they write nothing.
        if (prepared is not null)
            List(prepared, []);
        if (verb is not ("copy" or "revise"))
        {
            Apply(own);
            if (prepared is not null)
                Write(prepared, []);
        }

        // A replace stands in for the effect of a verb step (decision 16): a replaced delete keeps the object.
        var replaced = prepared is not null && Replace(prepared);
        if (!replaced)
        {
            switch (verb)
            {
                case "modify" when input.End is null:
                case "create" when own.Count > 0 || prepared is not null:
                    break; // the values are written above
                case "freeze" or "thaw":
                    Freeze(verb == "freeze", input.End, input.Object);
                    break;
                case "delete" when input.End is null:
                    _deleted = true;
                    Note("The step deleted the object, so the process stops where it stood: later events, invokes and verb steps are refused, timers no longer fire, and time steps only move the clock.");
                    break;
                default:
                    Note(input.End is null
                        ? $"The run checked verb '{verb}' and recorded it, but did not apply it."
                        : $"The run checked verb '{verb}' on a link and recorded it, but did not apply it: a scenario's links hold no values and do not change.");
                    break;
            }
        }

        if (prepared is not null)
            RunLater(prepared, []);
        RecordAct(_root, Person(), verb, null);
    }

    // An accepted freeze or thaw (rights-design.md decision 16): of the subject, or with an end of its link to the object. Freezing what is
    // frozen, or thawing what is not, changes nothing and says so.
    private void Freeze(bool freeze, string? end, string? obj)
    {
        if (end is null)
        {
            if (_frozen == freeze)
            {
                Note(freeze ? "The object was already frozen, so freeze changed nothing." : "The object was not frozen, so thaw changed nothing.");
                return;
            }

            (_frozen, _frozeAt) = (freeze, _index);
            return;
        }

        var link = (end, obj ?? "");
        if (_frozenLinks.ContainsKey(link) == freeze)
        {
            Note(freeze ? "The link was already frozen, so freeze changed nothing." : "The link was not frozen, so thaw changed nothing.");
            return;
        }

        if (freeze)
            _frozenLinks[link] = _index;
        else
            _frozenLinks.Remove(link);
    }

    // Whether the link a verb acts on is frozen; with no end known, whether a link of the subject is (rights-design.md decision 17).
    private bool LinkFrozen(string? end, string? obj) => end is null ? _frozenLinks.Count > 0 : _frozenLinks.ContainsKey((end, obj ?? ""));

    // The attributes a verb step reads or modifies on the subject (rights-design.md decision 21): a modify step's values, in ordinal order, and
    // a read step's attributes; null when it names none, which the check reads as not known.
    private static IReadOnlyList<string>? StepAttributes(ScenarioStep input, string verb) => verb switch
    {
        "modify" when input.End is null && input.Values is { Count: > 0 } values => [.. values.Keys.Order(StringComparer.Ordinal)],
        "read" when input.End is null && input.Attributes is { Count: > 0 } attributes => attributes,
        _ => null,
    };

    // The index of the step that froze what refused the input as frozen (MQ9309): the subject's freeze, else the link's; -1 when the run
    // started it frozen. With no end known, the latest freeze of a link.
    private int? FrozeBy(ScenarioStep? input)
    {
        if (input is null)
            return null;
        var verb = input.Input == StepInput.Verb ? input.Verb : input.Event is { } e && Chart.Events.TryGetValue(e, out var ev) ? ev.Verb : null;
        var end = input.Input == StepInput.Verb ? input.End : null;
        if (_frozen && (end is null || verb is "connect" or "disconnect"))
            return _frozeAt;
        if (end is not null)
            return _frozenLinks.TryGetValue((end, input.Object ?? ""), out var at) ? at : null;
        return _frozenLinks.Count > 0 ? _frozenLinks.Values.Max() : null;
    }

    // Asks the run's rights check for the root, with the input as it runs, and puts its notes, findings and answering rights in the trace.
    private RightsDecision CheckRights(RunRights rights, string? verb, IReadOnlyList<RightsList> closed, IReadOnlyList<RightsList> signers, string? end,
        IReadOnlyList<string>? attributes = null)
    {
        var result = rights.Check(new RunCheck
        {
            Verb = verb, Closed = closed, Signers = signers, End = end, Step = _scope.Input!,
            Frozen = _frozen, LinkFrozen = LinkFrozen(end, _scope.Input?.Object), Attributes = attributes,
            Running = [.. _root.Config.Where(s => !s.IsRoot).OrderBy(s => s.Order).Select(s => s.Id)],
            Identity = Identity(), Clock = ClockText(), Subject = _current, Values = _values, Seed = Seed(_root),
        });
        foreach (var note in result.Notes)
            Note(note);
        _scope.Diagnostics.AddRange(result.Diagnostics);
        foreach (var answered in result.Decision.Rights)
        {
            if (!_scope.Rights.Contains(answered))
                _scope.Rights.Add(answered);
        }

        return result.Decision;
    }

    // The gates on the transitions an event can take from the active states, whose signers count as an allow for it (rights-design.md
    // decision 14); guards are not consulted, since rights are checked before any guard picks a transition. An approval's are the signers of
    // the signatures its round needs; one that needs nothing is checked as a move without a gate (signoff-design.md decisions 7 and 18).
    private List<RightsList> GateSigners(Instance inst, string eventId)
    {
        var gates = new List<RightsList>();
        var seen = new HashSet<ChartTransition>(ReferenceEqualityComparer.Instance);
        foreach (var leaf in Leaves(inst))
        {
            foreach (var s in leaf.Ancestors.Insert(0, leaf))
            {
                if (s.IsRoot)
                    break;
                foreach (var t in inst.Chart.Candidates(s, TransitionTrigger.Event, eventId))
                {
                    if (!seen.Add(t) || t.Gate is not { } gate || gates.Any(g => string.Equals(g.Owner, gate.Id, StringComparison.Ordinal)))
                        continue;
                    if (!gate.UsesStages)
                        gates.Add(new RightsList(gate.Id, gate.Signers));
                    else if (ApprovalOf(inst, t, gate) is { Waiting: false } approval)
                        gates.Add(new RightsList(gate.Id, NeededSigners(gate, approval)));
                }
            }
        }

        return gates;
    }

    // ---- the moves an input can make now (actors-design.md decision 15) ----

    /// <summary>
    /// What a step's participant picks the actor it acts as from (actors-design.md decision 15), read from the moves the input can make in
    /// the current configuration. For an event: its actors, and the transitions it enables now in the order the next step tries them
    /// (source active, guard true; the signers are not checked), each with its gate and the gate's required actors not yet signed. For a
    /// completed or failed human task: its actors. Guards are evaluated as the step will evaluate them; nothing runs and nothing is recorded.
    /// </summary>
    /// <param name="input">The step about to run.</param>
    /// <param name="identity">The identity <c>current_actor()</c> reads in the guards it evaluates, or <see langword="null"/>.</param>
    /// <param name="parties">The participants the step names for a delegation (signoff-design.md decision 34), or <see langword="null"/>.</param>
    /// <returns>The choice.</returns>
    internal ActingChoice Acting(ScenarioStep input, string? identity = null, StepParties? parties = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Input is StepInput.InvokeDone or StepInput.InvokeError)
        {
            var pending = AllInstances().SelectMany(i => i.Pending).Select(p => p.Invoke).FirstOrDefault(p => string.Equals(p.Id, input.Invoke, StringComparison.Ordinal));
            return pending is { Type: InvokeType.HumanTask }
                ? new ActingChoice("human task", pending.Name, pending.Actors, [])
                : new ActingChoice(null, null, [], []);
        }

        if (input.Input != StepInput.Event || input.Event is null || _root.Done
            || AllInstances().FirstOrDefault(i => i.Chart.Events.ContainsKey(input.Event)) is not { } target)
            return new ActingChoice(null, null, [], []);
        var ev = target.Chart.Events[input.Event];
        var saved = _scope;
        _index++; // a guard's seed is the step's
        _scope = new Scope(input) { Event = EventObject(ev.Name, input.Actor, ev.Payload, input.Payload), Identity = identity, Parties = parties };
        try
        {
            var moves = new List<ActingMove>();
            var seen = new HashSet<ChartTransition>(ReferenceEqualityComparer.Instance);
            foreach (var leaf in Leaves(target))
            {
                foreach (var s in leaf.Ancestors.Insert(0, leaf))
                {
                    if (s.IsRoot)
                        break;
                    foreach (var t in target.Chart.Candidates(s, TransitionTrigger.Event, ev.Id))
                    {
                        // A response other than sign is offered only by an open approval (signoff-design.md decision 9).
                        if (input.Response != SignatureResponse.Sign && !OpenApproval(target, t))
                            continue;
                        if (!seen.Add(t) || Guard(target, t) != true)
                            continue;
                        if (t.Gate is { UsesStages: true } approvalGate)
                        {
                            // The round's needed signatures, for the pick with the actor (decision 8); nothing needed moves as without a gate. An
                            // answer on behalf picks among the signatures the delegator handed to the person (decision 34).
                            var approval = ApprovalOf(target, t, approvalGate);
                            var behalf = input.OnBehalfOf is null ? null : parties?.OnBehalfOf ?? "";
                            moves.Add(approval.Waiting ? new ActingMove(null, []) : new ActingMove(approvalGate, [])
                            {
                                Signatures = behalf is null ? NeededSignatures(target, t, approvalGate, approval, identity, input.Response)
                                    : BehalfSignatures(target, t, approvalGate, approval, identity, behalf, input.Response),
                                Latest = Latest(approval, identity, behalf),
                                LatestActor = LatestSigned(approval, identity)?.Actor,
                            });
                            continue;
                        }

                        var signed = target.Signatures.TryGetValue(t.Id, out var list) ? list : [];
                        moves.Add(t.Gate is { } gate
                            ? new ActingMove(gate, [.. gate.RequiredActors.Where(a => !signed.Any(x => Accepts(a, x.Actor))).Distinct(StringComparer.Ordinal)])
                            : new ActingMove(null, []));
                    }
                }
            }

            return new ActingChoice("event", ev.Name, ev.Actors, moves);
        }
        finally
        {
            _index--;
            _scope = saved;
        }
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

    // Refusing: the check that refused the most telling candidate, which a refused step lists (trigger-points-design.md decision 28).
    private sealed record Selection(List<ChartTransition> Transitions, bool Consumed, string Reason, string? By = null, TriggerPointRun? Refusing = null);

    // Refuses the input with the selection's most telling reason; a check that refused it is listed in the trace.
    private void RefuseSelection(Selection selection)
    {
        if (selection.Refusing is { } refusing && _scope.Refusal is null)
            _scope.TriggerRuns.Add(refusing);
        Refuse(selection.Reason, selection.By);
    }

    private Selection Select(Instance inst, TransitionTrigger trigger, string key, bool external)
    {
        var enabled = new List<ChartTransition>();
        var tried = new Dictionary<ChartTransition, bool>(ReferenceEqualityComparer.Instance);
        var consumed = false;
        var reason = Refusals.NoTransition;
        string? by = null;
        TriggerPointRun? refusing = null;
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

                    // A response other than sign selects only gated moves with an open approval (signoff-design.md decision 9).
                    if (external && _scope.Input is { Response: not SignatureResponse.Sign } && !OpenApproval(inst, t))
                        continue;
                    tried[t] = false;
                    if (Guard(inst, t) != true)
                    {
                        Fail(Refusals.Guard, 1, t.Guard?.Id ?? t.Transition.Guard);
                        continue;
                    }

                    if (t.Gate is { } gate)
                    {
                        if (!external)
                        {
                            Fail(Refusals.GateSigner, 2, gate.Id);
                            continue;
                        }

                        // Checked now, signed only when the transition survives conflict removal (a preempted gate gets no signature). An
                        // approval with nothing needed is checked as a move without a gate (signoff-design.md decision 7).
                        var check = !gate.UsesStages ? CheckSigner(inst, t, gate)
                            : ApprovalOf(inst, t, gate) is { Waiting: false } approval ? CheckApproval(inst, t, gate, approval)
                            : null;
                        if (check is { } refusal)
                        {
                            Fail(refusal.Refusal, 2, refusal.By);
                            continue;
                        }
                    }

                    // Rows 8 and 9 (trigger-points-design.md decision 18): a refusing check leaves the candidate out, as a false guard does,
                    // and outranks a gate as the reason.
                    if (TriggerCandidate(inst, t, external) is { } refused)
                    {
                        Fail(Refusals.Check, 3, refused.TriggerPoint, refused);
                        continue;
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
            if (t.Gate is not { } gate || Admit(inst, t, gate))
                return true;
            consumed = true; // the occurrence signed an incomplete gate: it is accepted, and the transition waits for more signatures
            return false;
        });
        return new Selection(selected, consumed, reason, by, refusing);

        void Fail(string why, int level, string? refusedBy, TriggerPointRun? run = null)
        {
            if (level > rank)
            {
                rank = level;
                reason = why;
                by = refusedBy;
                refusing = run;
            }
        }
    }

    // own: the move is the step's own (a finished invoke), not an automatic one, so a check that refuses it can refuse the step.
    private Selection SelectAt(Instance inst, ChartState source, TransitionTrigger trigger, string key, bool own = false)
    {
        var reason = Refusals.NoTransition;
        string? by = null;
        TriggerPointRun? refusing = null;
        foreach (var t in inst.Chart.Candidates(source, trigger, key))
        {
            if (Guard(inst, t) == true)
            {
                // Rows 8 and 9 (trigger-points-design.md decision 18): a check outranks a guard as the reason, the first of each kept.
                if (TriggerCandidate(inst, t, own) is { } refused)
                {
                    if (reason != Refusals.Check)
                        (reason, by, refusing) = (Refusals.Check, refused.TriggerPoint, refused);
                    continue;
                }

                CheckOverlap(inst, source, t, trigger, key);
                return new Selection([t], false, reason);
            }

            if (reason == Refusals.NoTransition)
            {
                by = t.Guard?.Id ?? t.Transition.Guard;
                reason = Refusals.Guard;
            }
        }

        return new Selection([], false, reason, by, refusing);
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

    // A guard runs when its expression, or its condition (next-phase-3-design.md section 3.1), compiled into the chart's session.
    private bool CanEvaluate(Instance inst, ProcessGuard guard) =>
        (Session(inst.Chart)?.Compiled.Contains(guard.Id) ?? false) || (_scope.Input?.Assume.TryGetValue(guard.Id, out var assumed) == true && assumed.Guard is not null);

    private bool? Guard(Instance inst, ChartTransition t)
    {
        if (t.Guard is not { } guard)
            return t.Transition.Guard is null; // an undeclared guard (MQ2001) never holds
        if (Session(inst.Chart) is { } session && session.Compiled.Contains(guard.Id))
        {
            // A condition answers true or false, so MQ9504 cannot occur for it; its now() is the simulated clock, and it reads the instance's
            // object under subject (none for a sub-process, so its attributes read as their defaults).
            var label = $"Guard '{guard.Name}'";
            var result = !session.Conditions.Contains(guard.Id)
                ? session.Evaluate(guard.Id, label, ContextObject(inst), _scope.Event, Seed(inst), _options.CancellationToken)
                : session.Lowered.TryGetValue(guard.Id, out var lowered) && lowered.EngineOnly
                    ? session.EvaluateCondition(guard.Id, label, ContextObject(inst), _scope.Event, SubjectOf(inst), lowered.ReadsRunning ? Running(inst) : null,
                        ClockText(), Reads(inst, lowered), Seed(inst), _options.CancellationToken)
                    : session.EvaluateCondition(guard.Id, label, ContextObject(inst), _scope.Event, SubjectOf(inst), ClockText(), Seed(inst), _options.CancellationToken);
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

        if (_scope.Input is { } input && input.Assume.TryGetValue(guard.Id, out var entry) && entry.Guard is { } assumed)
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
        var source = action.Sets.Count > 0 ? ActionSource.Sets : ActionSource.Stub;
        // Every set's value is worked out from the object and the context as they were when the action started, then all are applied after
        // its expression ran (object-effects-design.md decision 4).
        var sets = action.Sets.Count > 0 ? WorkOutSets(inst, action) : null;
        if (action.Expression is not null && Session(inst.Chart) is { } session && session.Compiled.Contains(action.Id))
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

        if (sets is not null)
        {
            // Freezing doesn't stop moves, and a move's sets are part of it (rights-design.md decision 17); a thaw's own move runs thawed.
            if (sets.Count > 0 && _frozen && !_scope.Thaws)
                Note("The object is frozen, but the move's sets applied: freezing stops modify, connect, disconnect and delete, not moves. A whileFrozen deny on the move's verb stops it.");
            Apply(sets);
        }
        foreach (var raised in action.Raises)
        {
            if (inst.Chart.Events.ContainsKey(raised))
                inst.Queue.Enqueue(new Internal(TransitionTrigger.Event, raised));
        }

        return new ActionRun(action.Id, source, changed) { Sets = sets?.ToImmutableSortedDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal) };
    }

    // The values an action's sets give the object, by attribute id with the attribute's name (decisions 3 and 4); a set that did not compile is
    // left out (validation says why). An invoked sub-process sees no object, so its sets change nothing, and the trace says so.
    private SortedDictionary<string, (string Name, JsonElement Value)>? WorkOutSets(Instance inst, ProcessAction action)
    {
        if (inst.Parent is not null)
        {
            Note($"Action '{action.Name}' of invoked process '{inst.Chart.Process.Name}' sets the object's data, but an invoked process sees no object, so its sets change nothing.");
            return null;
        }

        var values = new SortedDictionary<string, (string Name, JsonElement Value)>(StringComparer.Ordinal);
        if (Session(inst.Chart) is not { } session)
            return values;
        var context = ContextObject(inst);
        var subject = SubjectOf(inst);
        foreach (var set in action.Sets)
        {
            if (!session.Lowered.TryGetValue(set.Id, out var lowered) || lowered.Set is not { } target)
                continue;
            var result = session.EvaluateSet(set.Id, $"Action '{action.Name}'", context, _scope.Event, subject, lowered.ReadsRunning ? Running(inst) : null, ClockText(),
                lowered.EngineOnly ? Reads(inst, lowered) : null, Seed(inst), _options.CancellationToken);
            if (result.Diagnostic is { } d)
                _scope.Diagnostics.Add(d);
            else if (result.Value is JsonElement value)
                values[target.Attribute] = (target.Name, value);
        }

        return values;
    }

    // Applies the values of an action's sets to the object's row, keeping each attribute's value from before the step for valuesChanged.
    private void Apply(SortedDictionary<string, (string Name, JsonElement Value)> sets)
    {
        if (sets.Count == 0)
            return;
        var row = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in _current.EnumerateObject())
            row[member.Name] = member.Value;
        var defaults = Session(_root.Chart)?.SubjectDefaults;
        foreach (var (id, (name, value)) in sets)
        {
            if (!_scope.ValuesBefore.ContainsKey(id))
            {
                _scope.ValuesBefore[id] = row.TryGetValue(name, out var old) ? old
                    : defaults is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var fallback) ? fallback.Clone()
                    : Null;
            }

            row[name] = value;
            _values[id] = value;
        }

        _current = JsonSerializer.SerializeToElement(row);
    }

    // What a lowered condition or set that reads current_actor() or in_state is given (decisions 5 and 10): the acting identity, with a note when it
    // does not convert to a type current_actor() met, and the note of each state its in_state reads that the run assumed.
    private string? Reads(Instance inst, ProcessExpressions.LoweredCondition lowered)
    {
        var identity = Identity();
        if (identity is not null)
        {
            foreach (var kind in lowered.ActorKinds)
            {
                if (!Converts(identity, kind))
                    Note($"The identity '{identity}' is not {Article(kind)}, so current_actor() reads null where it meets one.");
            }
        }

        if (inst.Parent is null && _options.StateNotes is { Count: > 0 } notes)
        {
            foreach (var read in lowered.StateReads)
            {
                if (notes.TryGetValue(read.Key, out var note))
                    Note(note);
            }
        }

        return identity;

        static string Article(string kind) => kind switch
        {
            "int" => "an integer",
            "decimal" or "double" => "a number",
            "uuid" => "a uuid",
            _ => "a " + kind,
        };
    }

    // Whether an identity's text converts to a type current_actor() met, as the runtime's reader of that type reads it.
    private static bool Converts(string identity, string kind) => kind switch
    {
        "int" => System.Numerics.BigInteger.TryParse(identity.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
        "decimal" => decimal.TryParse(identity.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out _),
        "double" => double.TryParse(identity.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        "uuid" => identity.Where(c => c is not ('{' or '}' or '-')).ToList() is { Count: 32 } hex && hex.All(Uri.IsHexDigit),
        _ => true,
    };

    // The identity current_actor() reads (object-effects-design.md decision 5): the step's signer, else its participant's identity (its name when it
    // has none), else the name of the actor the step acts as; a move no one sends acts as the system actor when the project has one.
    private string? Identity()
    {
        var input = _scope.Input;
        if (input?.Signer is { Length: > 0 } signer)
            return signer;
        if (_scope.Identity is { } identity)
            return identity;
        if (input?.Actor is { } actor)
            return _options.ActorName?.Invoke(actor) ?? actor;
        if (_options.SystemActor is { } system
            && (input is null || input.Input == StepInput.Time || (input.Input is StepInput.InvokeDone or StepInput.InvokeError && !_scope.HumanTask)))
            return _options.ActorName?.Invoke(system) ?? system;
        return null;
    }

    // The running chart's active states, by its process id, which an in_state over it reads.
    private static JsonElement Running(Instance inst) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [inst.Chart.Process.Id] = [.. inst.Config.Where(s => !s.IsRoot).OrderBy(s => s.Order).Select(s => s.Id)],
        });

    private void Note(string note)
    {
        if (!_scope.Notes.Contains(note, StringComparer.Ordinal))
            _scope.Notes.Add(note);
    }

    // ---- gates ----

    // Refuses (and audits the refused attempt) when the input cannot sign the gate, with what refused it; null when it can. The order is
    // rights-design.md decision 12's last row: the gate's signers, then the lifecycle's denies on sign, then a repeat signer, then a reason.
    private (string Refusal, string? By)? CheckSigner(Instance inst, ChartTransition t, ProcessGate gate)
    {
        var input = _scope.Input!;
        var signatures = inst.Signatures.TryGetValue(t.Id, out var existing) ? existing : [];
        string? refusal = null;
        var by = gate.Id;
        if (input.Actor is null || string.IsNullOrEmpty(input.Signer) || !Allows(gate.Signers, input.Actor))
            refusal = Refusals.GateSigner;
        else if (ReferenceEquals(inst, _root) && _options.Rights is { Uses: true } rights
            && CheckRights(rights, "sign", [], [new RightsList(gate.Id, gate.Signers)], null) is { Refusal: Refusals.Right } denied)
            (refusal, by) = (Refusals.Right, denied.RefusedBy ?? gate.Id);
        else if (!gate.AllowRepeatSigner && signatures.Any(s => string.Equals(s.Signer, input.Signer, StringComparison.Ordinal)))
            refusal = Refusals.GateRepeat;
        else if (gate.ReasonRequired && string.IsNullOrWhiteSpace(input.Reason))
            refusal = Refusals.GateReason;
        if (refusal is not null)
        {
            var (meaning, attributes) = AuditFields(input, gate);
            Audit(inst, gate, t, input.Signer, input.Actor, meaning, input.Reason, GateOutcome.Refused, attributes);
            return (refusal, by);
        }

        return null;
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
        RecordAct(inst, input.Signer, "sign", null);
        var complete = signatures.Count >= Math.Max(1, gate.Required)
            && gate.RequiredActors.All(a => signatures.Any(s => Accepts(a, s.Actor)));
        if (complete)
        {
            Audit(inst, gate, t, input.Signer, input.Actor, meaning, input.Reason, GateOutcome.Completed, attributes);
            // The gated move is done by everyone whose signature counted (signoff-design.md decision 15).
            foreach (var s in signatures)
                RecordAct(inst, s.Signer, MoveVerb(t), t.Id);
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
        // The points of the moves taken (trigger-points-design.md decision 18): listed once per microstep, and their proposed values written
        // before the exit actions, once per point, in selection order, so the later transition wins an attribute both set.
        var taken = TakenCandidates(inst, transitions);
        if (taken is not null)
        {
            HashSet<string> listed = [], written = [];
            foreach (var move in taken)
            {
                List(move, listed);
                Write(move, written);
            }
        }

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
        var arrived = new List<ChartState>();
        foreach (var s in toEnter.OrderBy(s => s.Order))
        {
            if (s.IsRoot || !inst.Config.Add(s))
                continue;
            entered.Add(s.Id);
            arrived.Add(s);
            if (inst.RecordsActs)
                inst.EnteredAt[s.Id] = _index;
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

        // Approvals start once the entering move's actions and sets have run (signoff-design.md decision 7).
        if (inst.HasApprovals)
            StartApprovals(inst, transitions, arrived);

        // With-the-change points after the entry actions; after-commit points are queued for the end of the step.
        if (taken is not null)
        {
            HashSet<string> later = [];
            foreach (var move in taken)
                RunLater(move, later);
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
            // Leaving discards an approval's progress, audited when it holds any response, ignores included (signoff-design.md section 8).
            if (t.Gate is { UsesStages: true } approvalGate)
            {
                if (inst.Approvals.Remove(t.Id, out var approval) && approval.Holds)
                    Audit(inst, approvalGate, t, null, null, null, null, GateOutcome.Discarded, ImmutableDictionary<string, JsonElement>.Empty);
                continue;
            }

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

        // The object's attributes the input set to a new value (object-effects-design.md decision 4).
        SortedDictionary<string, JsonElement>? values = null;
        foreach (var (id, old) in _scope.ValuesBefore)
        {
            if (_values.TryGetValue(id, out var value) && !JsonValues.Same(old, value))
                (values ??= new(StringComparer.Ordinal))[id] = value;
        }

        TrimActs();
        var (counts, answered) = SignatureCounts();
        var diagnostics = _scope.Diagnostics.Distinct().ToList();
        return new StepTrace(_index, _scope.Input, _scope.Refusal is null, _scope.Refusal, _scope.Microsteps, _scope.Guards, _scope.Audit,
            Configuration, Snapshot(), changed, ClockText(), _root.Done, diagnostics)
        {
            Actor = MoveActor(),
            ValuesChanged = values,
            Notes = _scope.Notes.Count > 0 ? [.. _scope.Notes] : null,
            // Written in a project that uses rights, and for a step that expects it, so today's traces keep their members (decision 26).
            // ... and in a project that uses trigger points (trigger-points-design.md decision 28).
            RefusedBy = _scope.RefusedBy is { } by && (_options.Rights is { Uses: true } || _options.Triggers is not null || _scope.Input?.Expect?.RefusedBy is not null) ? by : null,
            Rights = _scope.Rights.Count > 0 ? [.. _scope.Rights] : null,
            TriggerPoints = _scope.TriggerRuns.Count + _scope.Queued.Count > 0 ? [.. _scope.TriggerRuns, .. _scope.Queued] : null,
            CheckWarnings = _scope.CheckWarnings.Count > 0 ? [.. _scope.CheckWarnings] : null,
            Frozen = _frozen,
            FrozenBy = _scope.Refusal == Refusals.Frozen ? FrozeBy(_scope.Input) : null,
            SignatureCounts = counts,
            AnsweredSignatures = answered,
            // The reminders and escalations that fell due, in file order, a reminder before an escalation (signoff-design.md decision 35).
            Notices = _scope.Notices.Count > 0 ? [.. _scope.Notices.OrderBy(n => n.Order).Select(n => n.Notice)] : null,
        };
    }

    // The actor of the move (actors-design.md decision 2), named only when the system actor made it: a move no one sends (the initial
    // entry, a time step, a finished invoke that is not a human task) whose input names no actor, in a project with exactly one system
    // actor. A step's own actor is already in its input, so the trace leaves it out and answers stay as they were without a system actor.
    // A refused step moved nothing, so it names no actor.
    private string? MoveActor()
    {
        var input = _scope.Input;
        if (_options.SystemActor is not { } system || input?.Actor is not null || _scope.Refusal is not null)
            return null;
        return input is null || input.Input == StepInput.Time || (input.Input is StepInput.InvokeDone or StepInput.InvokeError && !_scope.HumanTask)
            ? system
            : null;
    }

    // Whether the acting actor satisfies the listed one: itself, or one of its members at any depth (actors-design.md decision 5).
    private bool Accepts(string listed, string actor) =>
        _options.Accepts?.Invoke(listed, actor) ?? string.Equals(listed, actor, StringComparison.Ordinal);

    private bool Allows(IReadOnlyList<string> list, string actor)
    {
        foreach (var listed in list)
        {
            if (Accepts(listed, actor))
                return true;
        }

        return false;
    }

    private SortedDictionary<string, JsonElement> Snapshot() => new(_root.Context, StringComparer.Ordinal);

    // The first refusal of an input wins, with the element that refused it (rights-design.md decision 12); none for no-transition.
    private void Refuse(string reason, string? by = null)
    {
        if (_scope.Refusal is not null)
            return;
        _scope.Refusal = reason;
        _scope.RefusedBy = by;
    }

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

    /// <summary>The object each running instance sees, root first: the run's subject for the root, an empty object for a sub-process.</summary>
    internal IReadOnlyList<(string Process, JsonElement Subject)> SubjectsSeen => [.. AllInstances().Select(i => (i.Chart.Process.Id, SubjectOf(i)))];

    // The object an instance's guards read (lifecycle-design.md decision 22): the run's subject for the root; an invoked sub-process sees none.
    private JsonElement SubjectOf(Instance inst) => inst.Parent is null ? _current : NoObject;

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
        // Approvals by gated transition, and the record of acts with each active state's entry (signoff-design.md decisions 7 and 28).
        public bool HasApprovals { get; } = chart.Transitions.Any(t => t.Gate is { UsesStages: true });
        public bool RecordsActs { get; } = KeepsActs(chart);
        public Dictionary<string, ApprovalProgress> Approvals { get; } = new(StringComparer.Ordinal);
        public List<Act> Acts { get; } = [];
        public Dictionary<string, int> EnteredAt { get; } = new(StringComparer.Ordinal);
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
        public string? RefusedBy { get; set; }
        public List<RightEvaluation> Rights { get; } = [];
        public bool HumanTask { get; set; }
        public bool Thaws { get; set; }
        public string? Identity { get; init; }
        // The named signature picked with the participant's actor, and the approvals the step answered (signoff-design.md decisions 8 and 27).
        public string? Pick { get; init; }
        public List<ProcessGate> Answered { get; } = [];
        // The participants named for a delegation, and the reminders and escalations that fell due with their file order (decisions 34 and 35).
        public StepParties? Parties { get; init; }
        public List<((int Instance, int Transition, int Signature, NoticeKind Kind) Order, ApprovalNotice Notice)> Notices { get; } = [];
        public Dictionary<string, JsonElement> ValuesBefore { get; } = new(StringComparer.Ordinal);
        public List<string> Notes { get; } = [];
        public List<MicrostepTrace> Microsteps { get; } = [];
        public List<GuardEvaluation> Guards { get; } = [];
        public List<GateAuditRecord> Audit { get; } = [];
        public List<Diagnostic> Diagnostics { get; } = [];
        // The trigger points listed in the trace, the after-commit ones queued for its end, and the checks that warned (decision 28).
        public List<TriggerPointRun> TriggerRuns { get; } = [];
        public List<TriggerPointRun> Queued { get; } = [];
        public List<string> CheckWarnings { get; } = [];
        // The automatic moves a check refused in this step, each noted once.
        public HashSet<string> RefusedMoves { get; } = new(StringComparer.Ordinal);
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
