using System.Collections.Frozen;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolved process (phase-3-design.md sections 2.2 and 4.3): a statechart that is an entity's lifecycle or an orchestration. The
/// state tree, document order, paths and priority come from the interpreter's resolved chart, so templates see what it runs.
/// </summary>
public sealed class RProcess : RElement
{
    /// <inheritdoc/>
    public override string Kind => "process";

    /// <summary><c>lifecycle</c> or <c>orchestration</c>.</summary>
    public string Use { get; internal set; } = "orchestration";

    /// <summary>The subject entity, when set and resolved.</summary>
    public REntity? Subject { get; internal set; }

    /// <summary>The bound attribute: the subject's (flattened) attribute that holds the lifecycle state.</summary>
    public RAttribute? BoundAttribute { get; internal set; }

    /// <summary>The enum typing the bound attribute.</summary>
    public REnum? BoundEnum { get; internal set; }

    /// <summary>The context attributes (the running instance's data), stably sorted by order.</summary>
    public RList<RAttribute> Context { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The events, in display order.</summary>
    public RList<REvent> Events { get; internal set; } = RList<REvent>.Empty;

    /// <summary>The guards, in display order.</summary>
    public RList<RGuard> Guards { get; internal set; } = RList<RGuard>.Empty;

    /// <summary>The actions, in display order.</summary>
    public RList<RAction> Actions { get; internal set; } = RList<RAction>.Empty;

    /// <summary>The root's children in document order (each with its <see cref="RState.Children"/>: the tree).</summary>
    public RList<RState> States { get; internal set; } = RList<RState>.Empty;

    /// <summary>Every state in document order (pre-order: a parent before its children).</summary>
    public RList<RState> AllStates { get; internal set; } = RList<RState>.Empty;

    /// <summary>The states a configuration reports as active leaves (<see cref="RState.IsAtomic"/>), in document order.</summary>
    public RList<RState> AtomicStates { get; internal set; } = RList<RState>.Empty;

    /// <summary>The root's children that are not history or choice states, in document order: the states a lifecycle's enum binds.</summary>
    public RList<RState> BoundStates { get; internal set; } = RList<RState>.Empty;

    /// <summary>The transitions whose source resolves, in priority order.</summary>
    public RList<RTransition> Transitions { get; internal set; } = RList<RTransition>.Empty;

    /// <summary>The gates of the gated transitions, in transition priority order.</summary>
    public RList<RGate> Gates { get; internal set; } = RList<RGate>.Empty;

    /// <summary>The invokes of every state, in state document order then invoke order.</summary>
    public RList<RInvoke> Invokes { get; internal set; } = RList<RInvoke>.Empty;

    /// <summary>Every actor the process references (event actors, invoke actors, gate signers and required actors), by (name, id).</summary>
    public RList<RActor> Actors { get; internal set; } = RList<RActor>.Empty;

    /// <summary>The process's scenarios, by (name, id).</summary>
    public RList<RScenario> Scenarios { get; internal set; } = RList<RScenario>.Empty;

    /// <summary>The root's initial state; <see langword="null"/> only for a process without states (invalid).</summary>
    public RState? Initial { get; internal set; }
}

/// <summary>The members common to the nodes of a process: states, transitions, events, guards, actions, invokes, gates, meanings.</summary>
public abstract class RProcessNode : RObject
{
    /// <summary>The display name, falling back to the name (a transition: to its label).</summary>
    public string DisplayName { get; internal set; } = "";

    /// <summary>The description text; <see langword="null"/> when there is none.</summary>
    public string? Description { get; internal set; }

    /// <summary>Stereotypes, in application order (states, transitions and events carry them; other nodes have none).</summary>
    public IReadOnlyList<RStereotype> Stereotypes { get; internal set; } = [];

    /// <summary>Custom properties: stereotype defaults merged under the node's own, as plain CLR values.</summary>
    public IReadOnlyDictionary<string, object?> Properties { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Whether the node carries a stereotype.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool HasStereotype(string key) => Stereotypes.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal));
}

/// <summary>A state of a process.</summary>
public sealed class RState : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "state";

    /// <summary>The name, unique among siblings.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The dotted name path from the root, for example <c>Fulfilment.Shipping.Packed</c>.</summary>
    public string Path { get; internal set; } = "";

    /// <summary><c>atomic</c>, <c>compound</c>, <c>parallel</c>, <c>final</c>, <c>history</c> or <c>choice</c>.</summary>
    public string Type { get; internal set; } = "atomic";

    /// <summary>The parent state; <see langword="null"/> for a child of the root.</summary>
    public RState? Parent { get; internal set; }

    /// <summary>The children in document order (the regions of a parallel state).</summary>
    public RList<RState> Children { get; internal set; } = RList<RState>.Empty;

    /// <summary>A compound state's initial child (its <c>initial</c>, else its first child); otherwise <see langword="null"/>.</summary>
    public RState? Initial { get; internal set; }

    /// <summary>A history state's depth, <c>shallow</c> or <c>deep</c>; otherwise <see langword="null"/>.</summary>
    public string? History { get; internal set; }

    /// <summary>A history state's default target (its <c>defaultTarget</c>, else the parent's initial child); otherwise <see langword="null"/>.</summary>
    public RState? DefaultTarget { get; internal set; }

    /// <summary>The actions run on entry, in list order.</summary>
    public RList<RAction> Entry { get; internal set; } = RList<RAction>.Empty;

    /// <summary>The actions run on exit, in list order.</summary>
    public RList<RAction> Exit { get; internal set; } = RList<RAction>.Empty;

    /// <summary>The invokes started on entry.</summary>
    public RList<RInvoke> Invoke { get; internal set; } = RList<RInvoke>.Empty;

    /// <summary>Whether the state is final.</summary>
    public bool IsFinal { get; internal set; }

    /// <summary>
    /// Whether the state is an active leaf a configuration reports: an atomic or final state, or a container without children; not a
    /// history or choice state.
    /// </summary>
    public bool IsAtomic { get; internal set; }

    /// <summary>The depth: 1 for a child of the root.</summary>
    public int Depth { get; internal set; }

    /// <summary>For a lifecycle's bound root state, the bound enum's member of the same name; otherwise <see langword="null"/>.</summary>
    public REnumMember? BoundMember { get; internal set; }

    /// <summary>The transitions whose source is this state, in priority order.</summary>
    public RList<RTransition> TransitionsOut { get; internal set; } = RList<RTransition>.Empty;

    /// <summary>For a child of a parallel state, its region's position among the siblings (0-based); otherwise <see langword="null"/>.</summary>
    public int? RegionIndex { get; internal set; }
}

/// <summary>A transition of a process.</summary>
public sealed class RTransition : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "transition";

    /// <summary>The source state.</summary>
    public RState Source { get; internal set; } = null!;

    /// <summary>The targets that resolve, in list order; empty for a targetless transition.</summary>
    public RList<RState> Targets { get; internal set; } = RList<RState>.Empty;

    /// <summary><c>event</c>, <c>after</c>, <c>done</c>, <c>always</c>, <c>invoke-done</c> or <c>invoke-error</c>.</summary>
    public string Trigger { get; internal set; } = "event";

    /// <summary>The event of an <c>event</c> transition.</summary>
    public REvent? Event { get; internal set; }

    /// <summary>The ISO 8601 duration of an <c>after</c> transition, as written.</summary>
    public string? After { get; internal set; }

    /// <summary>The delay in milliseconds (fixed spans: a month is 30 days, a year 365), or <see langword="null"/>.</summary>
    public long? AfterMs { get; internal set; }

    /// <summary>The exact delay in ticks of 100 ns (fixed spans), or <see langword="null"/>; <see cref="AfterMs"/> drops the part below a
    /// millisecond.</summary>
    public long? AfterTicks { get; internal set; }

    /// <summary>The invoke of an <c>invoke-done</c> or <c>invoke-error</c> transition.</summary>
    public RInvoke? Invoke { get; internal set; }

    /// <summary>The guard; <see langword="null"/> when unguarded or when the guard it names does not resolve.</summary>
    public RGuard? Guard { get; internal set; }

    /// <summary>Whether the transition names a guard the process does not declare (MQ2001): the engine's interpreter treats it as never
    /// holding, so a generated interpreter must too.</summary>
    public bool GuardMissing { get; internal set; }

    /// <summary>The actions, in list order.</summary>
    public RList<RAction> Actions { get; internal set; } = RList<RAction>.Empty;

    /// <summary>Whether the source is exited and re-entered when a target is its descendant.</summary>
    public bool External { get; internal set; }

    /// <summary>The signature gate.</summary>
    public RGate? Gate { get; internal set; }

    /// <summary>The edge label as the editor draws it: <c>event [guard] / actions</c>, <c>after 5d</c>, <c>done</c>, <c>always</c>.</summary>
    public string Label { get; internal set; } = "";

    /// <summary>Whether the transition has no target (it exits and enters nothing).</summary>
    public bool IsTargetless { get; internal set; }
}

/// <summary>A process-local event.</summary>
public sealed class REvent : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "event";

    /// <summary>The name, unique in the process.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The typed payload.</summary>
    public RList<RAttribute> Payload { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The actors that may send the event, in list order; empty means any actor.</summary>
    public RList<RActor> Actors { get; internal set; } = RList<RActor>.Empty;

    /// <summary>The transitions that trigger on the event, in priority order.</summary>
    public RList<RTransition> Transitions { get; internal set; } = RList<RTransition>.Empty;
}

/// <summary>A named guard.</summary>
public sealed class RGuard : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "guard";

    /// <summary>The name, unique in the process.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The JavaScript expression, or <see langword="null"/> for a stub.</summary>
    public string? Expression { get; internal set; }

    /// <summary>Whether the guard has no expression (the host answers it).</summary>
    public bool IsStub { get; internal set; }

    /// <summary>The transitions it guards, in priority order.</summary>
    public RList<RTransition> UsedBy { get; internal set; } = RList<RTransition>.Empty;
}

/// <summary>A named action.</summary>
public sealed class RAction : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "action";

    /// <summary>The name, unique in the process.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The JavaScript expression, or <see langword="null"/> for a stub.</summary>
    public string? Expression { get; internal set; }

    /// <summary>Whether the action has no expression (the host implements it).</summary>
    public bool IsStub { get; internal set; }

    /// <summary>The internal events it raises, in list order.</summary>
    public RList<REvent> Raises { get; internal set; } = RList<REvent>.Empty;

    /// <summary>The transitions that run it (priority order), then the states whose entry or exit run it (document order).</summary>
    public RList<RProcessNode> UsedBy { get; internal set; } = RList<RProcessNode>.Empty;
}

/// <summary>Work a state starts on entry.</summary>
public sealed class RInvoke : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "invoke";

    /// <summary>The name, unique in the process.</summary>
    public string Name { get; internal set; } = "";

    /// <summary><c>process</c>, <c>service</c> or <c>human-task</c>.</summary>
    public string Type { get; internal set; } = "service";

    /// <summary>The sub-process, for a <c>process</c> invoke.</summary>
    public RProcess? Process { get; internal set; }

    /// <summary>The actors who may complete a human task.</summary>
    public RList<RActor> Actors { get; internal set; } = RList<RActor>.Empty;

    /// <summary>The state that starts it.</summary>
    public RState State { get; internal set; } = null!;
}

/// <summary>A signature gate on an event transition.</summary>
public sealed class RGate : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "gate";

    /// <summary>The name, unique in the process.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The gated transition.</summary>
    public RTransition Transition { get; internal set; } = null!;

    /// <summary>The number of signatures needed.</summary>
    public int Required { get; internal set; } = 1;

    /// <summary>The actors whose members may sign.</summary>
    public RList<RActor> Signers { get; internal set; } = RList<RActor>.Empty;

    /// <summary>The actors each of which must give at least one signature.</summary>
    public RList<RActor> RequiredActors { get; internal set; } = RList<RActor>.Empty;

    /// <summary>Whether one signer may count twice.</summary>
    public bool AllowRepeatSigner { get; internal set; }

    /// <summary>Whether each signature must carry a reason.</summary>
    public bool ReasonRequired { get; internal set; }

    /// <summary>What a signature means.</summary>
    public RList<RMeaning> Meanings { get; internal set; } = RList<RMeaning>.Empty;

    /// <summary>The extra fields recorded with each signature.</summary>
    public RList<RAttribute> AuditAttributes { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>
    /// The audit record shape (phase-3-design.md section 2.3): <c>instance</c>, <c>process</c>, <c>gate</c>, <c>transition</c>,
    /// <c>sequence</c>, <c>signer</c>, <c>actor</c>, <c>meaning</c>, <c>reason</c>, <c>at</c>, <c>outcome</c>, then one field per audit
    /// attribute.
    /// </summary>
    public RList<RAuditField> Audit { get; internal set; } = RList<RAuditField>.Empty;
}

/// <summary>What a signature on a gate means.</summary>
public sealed class RMeaning : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "meaning";

    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";
}

/// <summary>One field of a gate's audit record.</summary>
public sealed class RAuditField : RObject
{
    /// <inheritdoc/>
    public override string Kind => "audit-field";

    /// <summary>The field name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>
    /// The type: a built-in keyword (<c>string</c>, <c>int64</c>, <c>text</c>, <c>datetimeoffset</c>), <c>id</c> for a model id, or
    /// for an audit attribute its built-in keyword (a scalar's base), else its type's name.
    /// </summary>
    public string Type { get; internal set; } = "string";

    /// <summary>Whether every record carries a value (a discarded record has no signer, actor, meaning or reason).</summary>
    public bool Required { get; internal set; }

    /// <summary>What the field holds.</summary>
    public string? Description { get; internal set; }

    /// <summary>The allowed values of a field with a fixed set (<c>outcome</c>); empty otherwise.</summary>
    public IReadOnlyList<string> Values { get; internal set; } = [];

    /// <summary>The audit attribute behind the field; <see langword="null"/> for the fixed fields.</summary>
    public RAttribute? Attribute { get; internal set; }
}

/// <summary>A resolved actor (phase-3-design.md section 2.5): a person, a role or an external system; in no package.</summary>
public sealed class RActor : RElement
{
    /// <inheritdoc/>
    public override string Kind => "actor";

    /// <summary><c>person</c>, <c>role</c> or <c>external-system</c>.</summary>
    public string Type { get; internal set; } = "person";

    /// <summary>The processes that reference the actor (event actors, invoke actors, gate signers), in process order.</summary>
    public RList<RProcess> Processes { get; internal set; } = RList<RProcess>.Empty;

    /// <summary>The events that name the actor among their actors, in process order then event order.</summary>
    public RList<REvent> Events { get; internal set; } = RList<REvent>.Empty;

    /// <summary>The gates the actor may sign, in process order then transition priority.</summary>
    public RList<RGate> Gates { get; internal set; } = RList<RGate>.Empty;
}

/// <summary>A resolved scenario (phase-3-design.md section 2.4): a recorded input sequence of one process.</summary>
public sealed class RScenario : RElement
{
    /// <inheritdoc/>
    public override string Kind => "scenario";

    /// <summary>The process the scenario runs.</summary>
    public RProcess Process { get; internal set; } = null!;

    /// <summary>The starting point.</summary>
    public RScenarioStart Start { get; internal set; } = new();

    /// <summary>The steps, in time order.</summary>
    public RList<RStep> Steps { get; internal set; } = RList<RStep>.Empty;

    /// <summary><c>final</c> or <c>active</c>: whether the root is final after the last step.</summary>
    public string Outcome { get; internal set; } = "active";
}

/// <summary>A scenario's starting point.</summary>
public sealed class RScenarioStart
{
    /// <summary>Initial context values over the attribute defaults, by attribute name (by id when the attribute does not resolve).</summary>
    public IReadOnlyDictionary<string, object?> Context { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>The simulated clock's start (an ISO 8601 instant).</summary>
    public string At { get; internal set; } = Model.ScenarioStart.DefaultStart;
}

/// <summary>One step of a scenario, with its references resolved.</summary>
public sealed class RStep : RObject
{
    /// <inheritdoc/>
    public override string Kind => "step";

    /// <summary>The position in the scenario (0-based).</summary>
    public int Index { get; internal set; }

    /// <summary><c>event</c>, <c>time</c>, <c>invoke-done</c> or <c>invoke-error</c>.</summary>
    public string Input { get; internal set; } = "event";

    /// <summary>The event sent.</summary>
    public REvent? Event { get; internal set; }

    /// <summary>The invoke that completed or failed.</summary>
    public RInvoke? Invoke { get; internal set; }

    /// <summary>The ISO 8601 duration a <c>time</c> step advances the clock by, as written.</summary>
    public string? After { get; internal set; }

    /// <summary>The duration in milliseconds (fixed spans), or <see langword="null"/>.</summary>
    public long? AfterMs { get; internal set; }

    /// <summary>The exact duration in ticks of 100 ns (fixed spans), or <see langword="null"/>; <see cref="AfterMs"/> drops the part below
    /// a millisecond.</summary>
    public long? AfterTicks { get; internal set; }

    /// <summary>The actor sending the input.</summary>
    public RActor? Actor { get; internal set; }

    /// <summary>The signing person's identity, for a gated event.</summary>
    public string? Signer { get; internal set; }

    /// <summary>The meaning of the signature.</summary>
    public RMeaning? Meaning { get; internal set; }

    /// <summary>The signature's reason.</summary>
    public string? Reason { get; internal set; }

    /// <summary>Payload values by attribute name (by id when the attribute does not resolve), as plain values.</summary>
    public IReadOnlyDictionary<string, object?> Payload { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Results for guards without an expression, by guard name (by id when the guard does not resolve).</summary>
    public IReadOnlyDictionary<string, object?> Assume { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>What is expected after the step; <see langword="null"/> when the step checks nothing.</summary>
    public RExpectation? Expect { get; internal set; }

    /// <summary>The description text.</summary>
    public string? Description { get; internal set; }

    /// <summary>
    /// What the engine's interpreter did with the step when it replayed the scenario (computed on first read, once per scenario):
    /// <see langword="null"/> when the replay did not reach the step (it stopped earlier) or could not run.
    /// </summary>
    public RStepTrace? Trace => Replay?.Value is { } steps && Index < steps.Count ? steps[Index] : null;

    internal Lazy<IReadOnlyList<RStepTrace>>? Replay { get; set; }
}

/// <summary>What the engine's interpreter did with one scenario step (phase-3-design.md section 4.1), for templates that assert it.</summary>
public sealed class RStepTrace
{
    /// <summary>Whether the input was accepted.</summary>
    public bool Accepted { get; internal set; }

    /// <summary>Why it was refused (<c>no-transition</c>, <c>guard</c>, <c>actor</c>, <c>gate-signer</c>, <c>gate-repeat</c>,
    /// <c>gate-reason</c>), or <see langword="null"/>.</summary>
    public string? Refusal { get; internal set; }

    /// <summary>The outcomes of the gate audit records the step wrote, in order (<c>signed</c>, <c>completed</c>, <c>refused</c>,
    /// <c>discarded</c>).</summary>
    public IReadOnlyList<string> Audit { get; internal set; } = [];

    /// <summary>The active atomic state paths after the step, in document order.</summary>
    public IReadOnlyList<string> StatePaths { get; internal set; } = [];

    /// <summary>Whether the root is final after the step.</summary>
    public bool Final { get; internal set; }
}

/// <summary>What a scenario step expects.</summary>
public sealed class RExpectation
{
    /// <summary>Whether the input is accepted; <see langword="false"/> records a refusal.</summary>
    public bool Accepted { get; internal set; } = true;

    /// <summary>The expected active atomic states, in document order.</summary>
    public RList<RState> States { get; internal set; } = RList<RState>.Empty;

    /// <summary>The paths of <see cref="States"/>.</summary>
    public IReadOnlyList<string> StatePaths { get; internal set; } = [];

    /// <summary>The attributes the step changes, by attribute name (by id when the attribute does not resolve), as plain values.</summary>
    public IReadOnlyDictionary<string, object?> Context { get; internal set; } = FrozenDictionary<string, object?>.Empty;
}
