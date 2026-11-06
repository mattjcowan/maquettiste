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

    /// <summary>The subject entity, when the process has exactly one subject, in either form, and it resolves; with a stereotype entry,
    /// when <see cref="Subjects"/> holds exactly one (stereotype-sets-design.md decision 12).</summary>
    public REntity? Subject { get; internal set; }

    /// <summary>The bound attribute: the one subject's (flattened) attribute that holds the lifecycle state.</summary>
    public RAttribute? BoundAttribute { get; internal set; }

    /// <summary>The enum typing the bound attribute.</summary>
    public REnum? BoundEnum { get; internal set; }

    /// <summary>
    /// Every subject whose entity resolves, in file order, whatever the file's form (lifecycle-design.md section 7): the entries of
    /// <c>subjects</c>, or one entry, with the process's id, for the one-subject form. A stereotype entry is expanded at its place into
    /// one subject per carrier it governs, by qualified name, each with the id <c>&lt;entry id&gt;/&lt;entity id&gt;</c>
    /// (stereotype-sets-design.md decision 12); with no carrier it gives none.
    /// </summary>
    public RList<RSubject> Subjects { get; internal set; } = RList<RSubject>.Empty;

    /// <summary>
    /// The entries of <c>subjects</c> that name a stereotype, as written, in file order (stereotype-sets-design.md section 6): each with
    /// its stereotype, the stereotype's own bound and owner attributes, and the carriers it governs. Empty when the process has none; it
    /// lists a stereotype nothing carries yet, which <see cref="Subjects"/> then leaves out.
    /// </summary>
    public RList<RStereotypeSubject> StereotypeSubjects { get; internal set; } = RList<RStereotypeSubject>.Empty;

    /// <summary>
    /// Whether the states' codes form a map (lifecycle-design.md decision 4): some state has a code, no two coded states sit on one path
    /// from the root, none sits inside a parallel region, and the codes are unique and of one kind, so any configuration has at most one
    /// code and output may map states to codes and back.
    /// </summary>
    public bool CodesMap { get; internal set; }

    /// <summary>Whether the file lists its subjects in <c>subjects</c> (records show the list only then).</summary>
    internal bool ListsSubjects { get; set; }

    /// <summary>Whether some state has a code (records show <see cref="CodesMap"/> only then).</summary>
    internal bool HasCodes { get; set; }

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

    /// <summary>The libraries whose guards, actions and gates the process names, by (name, id).</summary>
    public RList<RProcess> Libraries { get; internal set; } = RList<RProcess>.Empty;

    /// <summary>The process's scenarios, by (name, id).</summary>
    public RList<RScenario> Scenarios { get; internal set; } = RList<RScenario>.Empty;

    /// <summary>The root's initial state; <see langword="null"/> only for a process without states (invalid).</summary>
    public RState? Initial { get; internal set; }

    /// <summary>
    /// A lifecycle's rights in file order (rights-design.md section 6): each inline right's entry, which shares the right's id, and each use
    /// of a named right. An entry in neither form, or a use whose named right does not resolve, is left out (MQ9901, MQ2001). Empty for a
    /// process that is no lifecycle. The list keys on the process's file and on each named right it uses.
    /// </summary>
    public RList<RRightUse> Rights { get; internal set; } = RList<RRightUse>.Empty;

    /// <summary>What the lifecycle answers when no right of its own holds (rights-design.md decision 9): <c>deny</c> when its file says so, else <c>allow</c>.</summary>
    public string RightsDefault { get; internal set; } = "allow";

    /// <summary>
    /// The lifecycles, other than this process, that govern one of its subjects and could say no to an object, link or own verb its events
    /// or rights name (rights-design.md section 6), by name then id. Filled for lifecycles and for orchestrations with subjects, only in a
    /// project that uses rights; empty otherwise. It keys on the process's file and the rights naming key, and in a project that uses
    /// rights also on the named rights the process uses and on what decides which lifecycles govern each subject's entity.
    /// </summary>
    public RList<RMayRefuse> MayRefuse { get; internal set; } = RList<RMayRefuse>.Empty;

    /// <summary>
    /// "Who can do what" in a lifecycle (rights-design.md decision 31): for each atomic state in document order, each actor its rights,
    /// events, human tasks and gates name (by name, then everyone else) and each verb its rights and events name (by key), the answer, with
    /// one cell per entity where answers differ by entity. Filled for lifecycles only in a project that uses rights; empty otherwise. It keys
    /// on the process's file and the rights naming key, and in a project that uses rights also on the named rights it uses, every actor
    /// file, the verb vocabulary, what decides which lifecycles govern each subject's entity, and each lifecycle of <see cref="MayRefuse"/>
    /// with the named rights it uses.
    /// </summary>
    public RList<RAccessCell> AccessGrid { get; internal set; } = RList<RAccessCell>.Empty;
}

/// <summary>The members common to the nodes of a process: states, transitions, events, guards, actions, invokes, gates, meanings.</summary>
public abstract class RProcessNode : RObject
{
    /// <summary>The display name, falling back to the name (a transition: to its label).</summary>
    public string DisplayName { get; internal set; } = "";

    /// <summary>The description text; <see langword="null"/> when there is none.</summary>
    public string? Description { get; internal set; }

    /// <summary>Stereotypes, in application order (a meaning has none).</summary>
    public IReadOnlyList<RStereotype> Stereotypes { get; internal set; } = [];

    /// <summary>Custom properties: stereotype defaults merged under the node's own, as plain CLR values.</summary>
    public IReadOnlyDictionary<string, object?> Properties { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Whether the node carries a stereotype.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool HasStereotype(string key) => Stereotypes.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal));
}

/// <summary>
/// A subject of a process (lifecycle-design.md section 7): an entity it governs, with the entities derived from it, and the attribute
/// that holds its state.
/// </summary>
public sealed class RSubject : RObject
{
    /// <inheritdoc/>
    public override string Kind => "subject";

    /// <summary>The entity.</summary>
    public REntity Entity { get; internal set; } = null!;

    /// <summary>The bound attribute: the entity's (flattened) attribute that holds the state, when set and resolved.</summary>
    public RAttribute? BoundAttribute { get; internal set; }

    /// <summary>The enum typing the bound attribute.</summary>
    public REnum? BoundEnum { get; internal set; }

    /// <summary>The reference type typing the bound attribute (lifecycle-design.md decision 11): its codes are the states' codes.</summary>
    public RReferenceType? BoundReferenceType { get; internal set; }

    /// <summary>The single-valued attribute that holds the object's owner (actors-design.md decision 10), when the entry names one.</summary>
    public RAttribute? OwnerAttribute { get; internal set; }

    /// <summary>The to-one relation end that leads to the object's owner (actors-design.md decision 10), when the entry names one.</summary>
    public REnd? OwnerEnd { get; internal set; }

    /// <summary>
    /// The stereotype whose entry gave the subject (stereotype-sets-design.md decision 12), or <see langword="null"/> on a subject an
    /// entity entry gave. Its bound and owner attributes are then the carrier's copies of the stereotype's own.
    /// </summary>
    public RStereotypeDefinition? Stereotype { get; internal set; }
}

/// <summary>
/// An entry of a process's <c>subjects</c> that names a stereotype, as written (stereotype-sets-design.md section 6): it covers every
/// entity that carries the stereotype and the entities derived from them.
/// </summary>
public sealed class RStereotypeSubject : RObject
{
    /// <inheritdoc/>
    public override string Kind => "stereotype-subject";

    /// <summary>The stereotype's definition; <see langword="null"/> when no stereotype has the entry's key (MQ2003).</summary>
    public RStereotypeDefinition? Stereotype { get; internal set; }

    /// <summary>The stereotype's own attribute that holds the state, when set and resolved.</summary>
    public RAttribute? BoundAttribute { get; internal set; }

    /// <summary>The stereotype's own single-valued attribute that holds the object's owner, when the entry names one that MQ9606 accepts.</summary>
    public RAttribute? OwnerAttribute { get; internal set; }

    /// <summary>The carriers the entry governs, by qualified name (decision 4): a carrier another entry wins is left out. Empty when nothing
    /// carries the stereotype yet.</summary>
    public RList<REntity> Entities { get; internal set; } = RList<REntity>.Empty;
}

/// <summary>How an actor is worked out from the object (actors-design.md decisions 8 to 12).</summary>
public sealed class RRelative
{
    /// <summary><c>owner</c>, <c>path</c> or <c>members-of</c>; with several forms (MQ9603) the first in that order, <c>none</c> with none.</summary>
    public string Form { get; internal set; } = "";

    /// <summary>For <c>path</c>, the <see cref="REnd"/> steps, then the last <see cref="RAttribute"/> (the declaring one) or
    /// <see cref="REnd"/>; for <c>members-of</c>, the <c>within</c> ends (empty for the object itself); empty for <c>owner</c>. An id the
    /// model does not hold is left out.</summary>
    public IReadOnlyList<RObject> Path { get; internal set; } = [];

    /// <summary>The role, group or organization whose members it is, for <c>members-of</c>.</summary>
    public RActor? MembersOf { get; internal set; }
}

/// <summary>A state of a process.</summary>
public sealed class RState : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "state";

    /// <summary>The name, unique among siblings.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The state's own code (lifecycle-design.md decision 1): a <see cref="string"/> or a <see cref="long"/>, or <see langword="null"/>.</summary>
    public object? Code { get; internal set; }

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

    /// <summary>The verb of the move (verbs-design.md decision 12): the transition's own, else its event's; <see langword="null"/> when
    /// neither names one or the key names no verb.</summary>
    public RVerb? Verb { get; internal set; }
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

    /// <summary>The operations whose <c>fires</c> names the event, in <see cref="ResolvedModel.Operations"/> order (next-phase-2-design.md
    /// section 4.1).</summary>
    public RList<ROperation> FiredBy { get; internal set; } = RList<ROperation>.Empty;

    /// <summary>The verb the event names (verbs-design.md decision 11), or <see langword="null"/> when it names none or the key names no
    /// verb.</summary>
    public RVerb? Verb { get; internal set; }
}

/// <summary>A named guard.</summary>
public sealed class RGuard : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "guard";

    /// <summary>The name, unique in the process.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The JavaScript expression, or <see langword="null"/> for a stub or a guard written as a condition.</summary>
    public string? Expression { get; internal set; }

    /// <summary>
    /// The condition written in the expression language (next-phase-3-design.md section 3.1, "Process guards"), over the context's
    /// attributes and the payload attributes of the events whose transitions use the guard; only true passes, so a null operand fails
    /// it. <see langword="null"/> for a guard without one, or one that does not type or reads a context attribute named <c>event</c>
    /// (validation says why). Its paths end at the context's and payloads' <see cref="RAttribute"/>s, and its JavaScript is the function
    /// simulation runs.
    /// </summary>
    public RExpression? Condition { get; internal set; }

    /// <summary>
    /// Whether the guard has nothing that runs: neither an expression nor a <see cref="Condition"/>, which a condition that does not type
    /// leaves <see langword="null"/>. The host answers it, and a scenario step assumes it.
    /// </summary>
    public bool IsStub { get; internal set; }

    /// <summary>The transitions it guards, in priority order; for a library's guard, across processes in <see cref="ResolvedModel.Processes"/> order.</summary>
    public RList<RTransition> UsedBy { get; internal set; } = RList<RTransition>.Empty;
}

/// <summary>A named action.</summary>
public sealed class RAction : RProcessNode
{
    /// <inheritdoc/>
    public override string Kind => "action";

    /// <summary>The name, unique in the process.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The JavaScript expression of its context changes, or <see langword="null"/> when it has none.</summary>
    public string? Expression { get; internal set; }

    /// <summary>Whether the action has neither an expression nor sets (the host implements it).</summary>
    public bool IsStub { get; internal set; }

    /// <summary>
    /// The values the action sets on the object, in file order (object-effects-design.md decision 1); empty when it sets none. A set whose
    /// attribute does not resolve or whose value does not type is left out (validation says why). The list keys on the process's file and
    /// on the entities (or stereotypes) that declare the attributes set.
    /// </summary>
    public RList<RSet> Sets { get; internal set; } = RList<RSet>.Empty;

    /// <summary>The internal events it raises, in list order.</summary>
    public RList<REvent> Raises { get; internal set; } = RList<REvent>.Empty;

    /// <summary>
    /// The transitions that run it (priority order), then the states whose entry or exit run it (document order); for a library's action,
    /// that for each process in <see cref="ResolvedModel.Processes"/> order.
    /// </summary>
    public RList<RProcessNode> UsedBy { get; internal set; } = RList<RProcessNode>.Empty;
}

/// <summary>
/// One attribute of the object an action sets, and its value (object-effects-design.md decisions 1 to 3 and section 6). Every value of an
/// action is worked out from the object and context as they were when the action started, then all are applied together.
/// </summary>
public sealed class RSet : RObject
{
    /// <inheritdoc/>
    public override string Kind => "set";

    /// <summary>The attribute set, as the entity (or stereotype) that declares it resolves it: a base's own for an inherited one.</summary>
    public RAttribute Attribute { get; internal set; } = null!;

    /// <summary>
    /// The value, typed against the attribute's type; its paths end at the context's and payloads' attributes, and at the declaring entity's
    /// attribute for one of the object's. Its root carries the JavaScript, as a guard's condition does, except for a tree that reads
    /// <c>current_actor()</c> or <c>in_state</c>.
    /// </summary>
    public RExpression Value { get; internal set; } = null!;

    /// <summary>The action that holds it.</summary>
    public RAction? Action { get; internal set; }
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

    /// <summary>The gated transition; <see langword="null"/> for a library's gate, which <see cref="UsedBy"/> lists its transitions for.</summary>
    public RTransition? Transition { get; internal set; }

    /// <summary>The transitions that use the gate: its one transition, or, for a library's gate, each that names it, across processes.</summary>
    public RList<RTransition> UsedBy { get; internal set; } = RList<RTransition>.Empty;

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

/// <summary>A resolved actor (phase-3-design.md section 2.5, actors-design.md section 6): a person, a role, a group, an organization, an
/// external system or the system itself, with its memberships.</summary>
public sealed class RActor : RElement
{
    /// <inheritdoc/>
    public override string Kind => "actor";

    /// <summary><c>person</c>, <c>role</c>, <c>group</c>, <c>organization</c>, <c>external-system</c> or <c>system</c> (the system itself,
    /// shown as Automatic).</summary>
    public string Type { get; internal set; } = "person";

    /// <summary>The roles, groups and organizations the actor is directly a member of, in file order (actors-design.md section 6). An
    /// entry that names no role, group or organization, or the actor itself (MQ9601), is left out.</summary>
    public RList<RActor> MemberOf { get; internal set; } = RList<RActor>.Empty;

    /// <summary>The actors directly members of this one, by (name, id); empty unless it is a role, group or organization.</summary>
    public RList<RActor> Members { get; internal set; } = RList<RActor>.Empty;

    /// <summary>The roles, groups and organizations the actor is a member of at any depth, by (name, id), each once and never the actor
    /// itself, even in a cycle (MQ9602).</summary>
    public RList<RActor> AllMemberOf { get; internal set; } = RList<RActor>.Empty;

    /// <summary>The actors members of this one at any depth, by (name, id), each once and never the actor itself.</summary>
    public RList<RActor> AllMembers { get; internal set; } = RList<RActor>.Empty;

    /// <summary>How the actor is worked out from the object (actors-design.md section 6), or <see langword="null"/> for an actor that
    /// stands for itself.</summary>
    public RRelative? Relative { get; internal set; }

    /// <summary>The processes that reference the actor (event actors, invoke actors, gate signers), in process order.</summary>
    public RList<RProcess> Processes { get; internal set; } = RList<RProcess>.Empty;

    /// <summary>The events that name the actor among their actors, in process order then event order.</summary>
    public RList<REvent> Events { get; internal set; } = RList<REvent>.Empty;

    /// <summary>The gates the actor may sign, in process order then transition priority.</summary>
    public RList<RGate> Gates { get; internal set; } = RList<RGate>.Empty;

    /// <summary>
    /// The rights that name the actor directly, in <c>actors</c> or <c>except</c> (rights-design.md section 6): the named rights in
    /// <see cref="ResolvedModel.Rights"/> order, then the inline rights in process order and file order. It keys on the actor's referrers.
    /// </summary>
    public RList<RRight> Rights { get; internal set; } = RList<RRight>.Empty;
}

/// <summary>A resolved scenario (phase-3-design.md section 2.4): a recorded input sequence of one process.</summary>
public sealed class RScenario : RElement
{
    /// <inheritdoc/>
    public override string Kind => "scenario";

    /// <summary>The process the scenario runs.</summary>
    public RProcess Process { get; internal set; } = null!;

    /// <summary>The scenario's objects in file order (lifecycle-design.md decision 22); an object whose entity does not resolve is left out.</summary>
    public RList<RScenarioObject> Objects { get; internal set; } = RList<RScenarioObject>.Empty;

    /// <summary>The object the process runs for, or <see langword="null"/> when the scenario has no objects (or names none of them).</summary>
    public RScenarioObject? Subject { get; internal set; }

    /// <summary>Who acts in the scenario, in file order (actors-design.md decision 14); empty for a scenario with none.</summary>
    public RList<RParticipant> Participants { get; internal set; } = RList<RParticipant>.Empty;

    /// <summary>The starting point.</summary>
    public RScenarioStart Start { get; internal set; } = new();

    /// <summary>The steps, in time order.</summary>
    public RList<RStep> Steps { get; internal set; } = RList<RStep>.Empty;

    /// <summary><c>final</c> or <c>active</c>: whether the root is final after the last step.</summary>
    public string Outcome { get; internal set; } = "active";
}

/// <summary>An object of a scenario (lifecycle-design.md decision 22): an instance of an entity, with the values it is given and its links.</summary>
public sealed class RScenarioObject : RObject
{
    /// <inheritdoc/>
    public override string Kind => "object";

    /// <summary>The name, unique in the scenario.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The entity the object is an instance of.</summary>
    public REntity Entity { get; internal set; } = null!;

    /// <summary>The values given, by attribute name (by id when the entity has no such attribute), as plain values; a value not given reads
    /// as the attribute's default.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>The links to other objects of the scenario, in file order; a link whose end or object does not resolve is left out.</summary>
    public IReadOnlyList<RScenarioLink> Links { get; internal set; } = [];

    /// <summary>
    /// The object's states in lifecycles other than the one the scenario runs, as given, in file order (object-effects-design.md decision 10);
    /// a state that does not resolve is left out. Empty when the file gives none.
    /// </summary>
    public RList<RState> States { get; internal set; } = RList<RState>.Empty;

    /// <summary>The participant that owns the object (actors-design.md decision 16), when the file names one of the scenario's.</summary>
    public RParticipant? Owner { get; internal set; }
}

/// <summary>A participant of a scenario (actors-design.md decision 14): who acts, with the actor it stands for and what it holds.</summary>
public sealed class RParticipant : RObject
{
    /// <inheritdoc/>
    public override string Kind => "participant";

    /// <summary>The name, unique in the scenario.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The display name, or <see langword="null"/> when the file gives none.</summary>
    public string? DisplayName { get; internal set; }

    /// <summary>The value the team's system would hold for them: a <see cref="string"/> or a <see cref="long"/>; the name when the file gives
    /// none.</summary>
    public object Identity { get; internal set; } = "";

    /// <summary>The person, external system or system actor they stand for, when the file names one that resolves.</summary>
    public RActor? Actor { get; internal set; }

    /// <summary>The roles, groups and organizations they hold, in file order; a holding whose actor does not resolve is left out.</summary>
    public IReadOnlyList<RHolding> Holds { get; internal set; } = [];

    /// <summary>The scenario object that is their record, when the file names one of the scenario's.</summary>
    public RScenarioObject? Object { get; internal set; }
}

/// <summary>A role, group or organization a participant holds (actors-design.md decision 14).</summary>
public sealed class RHolding
{
    /// <summary>The holding's id.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>The role, group or organization held.</summary>
    public RActor Actor { get; internal set; } = null!;

    /// <summary>The scenario object it counts within, or <see langword="null"/> for a holding with no context.</summary>
    public RScenarioObject? Within { get; internal set; }
}

/// <summary>A link from a scenario object to another object of the scenario, through a relation end.</summary>
public sealed class RScenarioLink
{
    /// <summary>The relation end navigated from the object.</summary>
    public REnd End { get; internal set; } = null!;

    /// <summary>The linked object.</summary>
    public RScenarioObject Object { get; internal set; } = null!;
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

    /// <summary>The participant that acts (actors-design.md decision 15), when the step names one of the scenario's.</summary>
    public RParticipant? Participant { get; internal set; }

    /// <summary>The actor sending the input.</summary>
    public RActor? Actor { get; internal set; }

    /// <summary>
    /// The actor the step acts as (actors-design.md decision 15). For a step with a participant, the actor the engine's replay ran it as:
    /// worked out from the moves the step could make when it ran (for a gated move, a required actor of the gate not yet signed, else a
    /// signer the event's actors also accept, else the first signer the participant satisfies; otherwise the first of the event's or
    /// human task's actors it satisfies, or the participant's actor when that list is empty), and <see langword="null"/> when it
    /// satisfies none or the replay did not reach the step. Otherwise <see cref="Actor"/>. Generated tests that use it agree with the
    /// replay because it is the replay's.
    /// </summary>
    public RActor? ActsAs => ReplayedActsAs is { } replayed ? replayed.Value : Actor;

    /// <summary>For a step with a participant, the actor the replay ran it as (read on first use).</summary>
    internal Lazy<RActor?>? ReplayedActsAs { get; set; }

    /// <summary>The signing person's identity, for a gated event, as the file gives it.</summary>
    public string? Signer { get; internal set; }

    /// <summary>The meaning of the signature.</summary>
    public RMeaning? Meaning { get; internal set; }

    /// <summary>The signature's reason.</summary>
    public string? Reason { get; internal set; }

    /// <summary>Payload values by attribute name (by id when the attribute does not resolve), as plain values.</summary>
    public IReadOnlyDictionary<string, object?> Payload { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Results for guards without an expression, by guard name (by id when the guard does not resolve).</summary>
    public IReadOnlyDictionary<string, object?> Assume { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>
    /// For a <c>verb</c> step (rights-design.md decision 25): the verb the participant does to the subject or one of its links;
    /// <see langword="null"/> for other steps, or a key that names no verb.
    /// </summary>
    public RVerb? Verb { get; internal set; }

    /// <summary>
    /// For a <c>modify</c> verb step on the subject: the values it sets, by attribute name (by id when the attribute does not resolve), as
    /// plain values; empty when the file gives none.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>For a verb step on a link: the subject's relation end the link goes through; <see langword="null"/> for the subject itself.</summary>
    public REnd? End { get; internal set; }

    /// <summary>With <see cref="End"/>: the scenario object at the link's other end, when the file names one of the scenario's.</summary>
    public RScenarioObject? Object { get; internal set; }

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

    /// <summary>For a step with a participant, the id of the actor it acted as (<see cref="RStep.ActsAs"/>).</summary>
    internal string? ActsAs { get; set; }
}

/// <summary>What a scenario step expects.</summary>
public sealed class RExpectation
{
    /// <summary>Whether the input is accepted; <see langword="false"/> records a refusal.</summary>
    public bool Accepted { get; internal set; } = true;

    /// <summary>
    /// For a refused step: why it is expected to be refused (rights-design.md decision 26), such as <c>right</c>, <c>default</c>,
    /// <c>actor</c> or <c>guard</c>; <see langword="null"/> when the file expects none.
    /// </summary>
    public string? Refusal { get; internal set; }

    /// <summary>
    /// For a refused step: the id of the element expected to refuse it (rights-design.md decision 12), such as a right, a lifecycle, an event
    /// or a gate; <see langword="null"/> when the file expects none.
    /// </summary>
    public string? RefusedBy { get; internal set; }

    /// <summary>The expected active atomic states, in document order.</summary>
    public RList<RState> States { get; internal set; } = RList<RState>.Empty;

    /// <summary>The paths of <see cref="States"/>.</summary>
    public IReadOnlyList<string> StatePaths { get; internal set; } = [];

    /// <summary>The attributes the step changes, by attribute name (by id when the attribute does not resolve), as plain values.</summary>
    public IReadOnlyDictionary<string, object?> Context { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>
    /// The object's attributes the step sets (object-effects-design.md decision 4), by attribute name (by id when the attribute does not
    /// resolve), as plain values; empty when the file gives none.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Values { get; internal set; } = FrozenDictionary<string, object?>.Empty;
}
