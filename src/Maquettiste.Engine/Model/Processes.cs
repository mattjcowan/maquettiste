using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A sub-element of a process or a scenario (a state, transition, event, guard, action, invoke, gate, meaning or step): it carries a
/// ULID unique across the model, so the index registers it and references to it resolve (phase-3-design.md section 2.1).
/// </summary>
public interface IProcessNode
{
    /// <summary>The node's ULID.</summary>
    string Id { get; }
}

/// <summary>
/// A statechart (phase-3-design.md section 2.2; <c>model/processes/</c>): an entity's lifecycle or an orchestration. The process is the
/// implicit root compound state: its <see cref="States"/> are the root's children and <see cref="Initial"/> is the root's initial child.
/// The engine models intent only: it synthesizes no storage for process instances.
/// </summary>
public sealed record Process : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Process;

    /// <summary>The id of the owning package (the domain), or <see langword="null"/>.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>Whether the process is an entity's lifecycle or an orchestration.</summary>
    public ProcessUse Use { get; init; } = ProcessUse.Orchestration;

    /// <summary>
    /// The file's <c>subjects</c>: the entities the process governs, each entry with its own id (lifecycle-design.md decision 5);
    /// <see langword="null"/> when the file has none. Readers use <see cref="Subjects"/>, which serves both forms.
    /// </summary>
    [JsonPropertyName("subjects")]
    public IReadOnlyList<ProcessSubject>? SubjectList { get; init; }

    /// <summary>
    /// The one-subject form, deprecated (MQ9209): the entity whose lifecycle this is. A lifecycle needs a subject in one form or the other
    /// (MQ9201).
    /// </summary>
    [ElementRef(ElementKind.Entity)]
    public string? Subject { get; init; }

    /// <summary>The one-subject form, deprecated (MQ9209): an enum-typed attribute of the subject that holds the lifecycle state (MQ9202).</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public string? BoundAttribute { get; init; }

    /// <summary>
    /// What the lifecycle answers for a verb when no right of its own holds (rights-design.md decision 9): <see cref="RightAccess.Allow"/>
    /// when <see langword="null"/>. Written, even as allow, it makes the project use rights. Lifecycles only (MQ9901).
    /// </summary>
    public RightAccess? RightsDefault { get; init; }

    /// <summary>
    /// Every subject of the process in one list, whatever the form of the file (lifecycle-design.md decision 6): the entries of
    /// <c>subjects</c>, else one entry for the one-subject form, whose id is the process's own and which holds <see cref="Subject"/> and
    /// <see cref="BoundAttribute"/>; empty when the process has no subject.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ProcessSubject> Subjects =>
        SubjectList is { Count: > 0 } list ? list
        : Subject is { } entity ? [new ProcessSubject { Id = Id, Entity = entity, BoundAttribute = BoundAttribute }]
        : [];

    /// <summary>
    /// Whether the file writes its subjects in the deprecated one-subject form (<c>subject</c> or <c>boundAttribute</c>); MQ9209 warns when
    /// it has a <c>subject</c>.
    /// </summary>
    [JsonIgnore]
    public bool UsesOneSubjectForm => Subject is not null || BoundAttribute is not null;

    /// <summary>The running instance's data, in canonical order (stable-sorted by <see cref="ModelAttribute.Order"/>).</summary>
    public IReadOnlyList<ModelAttribute> Context { get; init; } = [];

    /// <summary>The process-local events, in display order.</summary>
    public IReadOnlyList<ProcessEvent> Events { get; init; } = [];

    /// <summary>The named guards, in display order.</summary>
    public IReadOnlyList<ProcessGuard> Guards { get; init; } = [];

    /// <summary>The named actions, in display order.</summary>
    public IReadOnlyList<ProcessAction> Actions { get; init; } = [];

    /// <summary>A library's gates, which a transition names with <see cref="ProcessTransition.GatePolicy"/>; other processes hold theirs on transitions (MQ9020).</summary>
    public IReadOnlyList<ProcessGate> Gates { get; init; } = [];

    /// <summary>
    /// The lifecycle's rights in file order (rights-design.md decisions 1 and 7): inline rights and uses of named rights. Lifecycles only
    /// (MQ9901).
    /// </summary>
    public IReadOnlyList<ProcessRight> Rights { get; init; } = [];

    /// <summary>
    /// The lifecycle's trigger points in file order, which is the order they run in at each moment (trigger-points-design.md decisions 1, 2
    /// and 19). Lifecycles only (MQ8101).
    /// </summary>
    public IReadOnlyList<ProcessTriggerPoint> TriggerPoints { get; init; } = [];

    /// <summary>
    /// The root's children in document order (entry order, priority and the bound enum's member order). A library has none, and any other
    /// process at least one (MQ9020).
    /// </summary>
    public IReadOnlyList<ProcessState> States { get; init; } = [];

    /// <summary>The root's initial child; <see langword="null"/> means the first child (MQ9001).</summary>
    [ElementRef(IndexKinds = ["state"])]
    public string? Initial { get; init; }

    /// <summary>The transitions, in priority order.</summary>
    public IReadOnlyList<ProcessTransition> Transitions { get; init; } = [];
}

/// <summary>What a process is for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessUse>))]
public enum ProcessUse
{
    /// <summary>The states an entity goes through (<c>lifecycle</c>).</summary>
    [JsonStringEnumMemberName("lifecycle")] Lifecycle,

    /// <summary>A flow of work across elements and actors (<c>orchestration</c>).</summary>
    [JsonStringEnumMemberName("orchestration")] Orchestration,

    /// <summary>Guards, actions and gates that other processes name by id; nothing in it runs (<c>library</c>, MQ9020).</summary>
    [JsonStringEnumMemberName("library")] Library,
}

/// <summary>
/// An entry of a process's <c>subjects</c> (lifecycle-design.md decision 5, stereotype-sets-design.md decision 2): an entity the process
/// governs, with the entities derived from it, or a stereotype, which covers every entity that carries it and the entities derived from
/// them (<see cref="Processes.SubjectCoverage"/>); and the attribute that holds the state. An entry names exactly one of
/// <see cref="Entity"/> and <see cref="Stereotype"/>.
/// </summary>
public sealed record ProcessSubject : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The entity, or <see langword="null"/> when the entry names a <see cref="Stereotype"/>.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>
    /// The key of the stereotype the entry stands for (stereotype-sets-design.md decision 2), or <see langword="null"/> when it names an
    /// <see cref="Entity"/>. The index records it as a reference to the stereotype; an unknown key is MQ2003, and one whose stereotype does
    /// not apply to entities MQ9211.
    /// </summary>
    [StereotypeKeys]
    public string? Stereotype { get; init; }

    /// <summary>The attribute that holds the lifecycle state, or <see langword="null"/>: one of the entity's (own, inherited or from a
    /// stereotype it carries), or on a stereotype entry one of the stereotype's own attributes (MQ9202).</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public string? BoundAttribute { get; init; }

    /// <summary>Where the object's owner is held (actors-design.md decision 10): a single-valued attribute or a to-one relation end of the
    /// entity, or on a stereotype entry a single-valued attribute of the stereotype's own (MQ9606); <see langword="null"/> when the team's
    /// system holds it.</summary>
    [ElementRef(IndexKinds = ["attribute", "end"])]
    public string? Owner { get; init; }
}

/// <summary>A state of a process; recursive through <see cref="States"/>.</summary>
public sealed record ProcessState : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The state's name: an identifier unique among its siblings.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>The state's type.</summary>
    public StateType Type { get; init; } = StateType.Atomic;

    /// <summary>
    /// The state's code: its stable stored value, a string or an integer as the file writes it (lifecycle-design.md decision 1);
    /// <see langword="null"/> when it has none.
    /// </summary>
    public JsonElement? Code { get; init; }

    /// <summary>Compound only: the initial child; <see langword="null"/> means the first child (MQ9001).</summary>
    [ElementRef(IndexKinds = ["state"])]
    public string? Initial { get; init; }

    /// <summary>History only: shallow or deep.</summary>
    public HistoryType History { get; init; } = HistoryType.Shallow;

    /// <summary>History only: the state entered when no history is recorded; <see langword="null"/> means the parent's initial.</summary>
    [ElementRef(IndexKinds = ["state"])]
    public string? DefaultTarget { get; init; }

    /// <summary>The actions run on entry, in list order.</summary>
    [ElementRef(IndexKinds = ["action"])]
    public IReadOnlyList<string> Entry { get; init; } = [];

    /// <summary>The actions run on exit, in list order.</summary>
    [ElementRef(IndexKinds = ["action"])]
    public IReadOnlyList<string> Exit { get; init; } = [];

    /// <summary>The work started on entry and cancelled on exit.</summary>
    public IReadOnlyList<ProcessInvoke> Invoke { get; init; } = [];

    /// <summary>The children in document order; the regions of a parallel state.</summary>
    public IReadOnlyList<ProcessState> States { get; init; } = [];

    /// <summary>Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Custom property values, validated by extension schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>The type of a state.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StateType>))]
public enum StateType
{
    /// <summary>No children (<c>atomic</c>).</summary>
    [JsonStringEnumMemberName("atomic")] Atomic,

    /// <summary>Exactly one child active at a time (<c>compound</c>).</summary>
    [JsonStringEnumMemberName("compound")] Compound,

    /// <summary>Every child (region) active at once (<c>parallel</c>).</summary>
    [JsonStringEnumMemberName("parallel")] Parallel,

    /// <summary>Its parent is done when it is entered (<c>final</c>).</summary>
    [JsonStringEnumMemberName("final")] Final,

    /// <summary>A pseudo-state that re-enters the parent's last configuration (<c>history</c>).</summary>
    [JsonStringEnumMemberName("history")] History,

    /// <summary>A pseudo-state that picks one of its eventless transitions (<c>choice</c>).</summary>
    [JsonStringEnumMemberName("choice")] Choice,
}

/// <summary>How much of the parent's configuration a history state records.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HistoryType>))]
public enum HistoryType
{
    /// <summary>The parent's direct active child (<c>shallow</c>).</summary>
    [JsonStringEnumMemberName("shallow")] Shallow,

    /// <summary>Every active descendant (<c>deep</c>).</summary>
    [JsonStringEnumMemberName("deep")] Deep,
}

/// <summary>A transition. Among transitions with one source and one trigger, array order is priority.</summary>
public sealed record ProcessTransition : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The edge label when set (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>The source state.</summary>
    [ElementRef(IndexKinds = ["state"])]
    public required string Source { get; init; }

    /// <summary>What fires the transition.</summary>
    public TransitionTrigger Trigger { get; init; } = TransitionTrigger.Event;

    /// <summary>
    /// The verb of the automatic move, by key (verbs-design.md decision 12): only on a transition without an event, since one with an
    /// event takes its event's verb (MQ9705). <see langword="null"/> when it names none.
    /// </summary>
    [VerbKey]
    public string? Verb { get; init; }

    /// <summary>The event, when <see cref="Trigger"/> is <see cref="TransitionTrigger.Event"/>.</summary>
    [ElementRef(IndexKinds = ["event"])]
    public string? Event { get; init; }

    /// <summary>An ISO 8601 duration, when <see cref="Trigger"/> is <see cref="TransitionTrigger.After"/> (MQ9008).</summary>
    public string? After { get; init; }

    /// <summary>An invoke of the source, for <c>invoke-done</c> and <c>invoke-error</c>.</summary>
    [ElementRef(IndexKinds = ["invoke"])]
    public string? Invoke { get; init; }

    /// <summary>The guard; unguarded when <see langword="null"/>.</summary>
    [ElementRef(IndexKinds = ["guard"])]
    public string? Guard { get; init; }

    /// <summary>The targets: none is targetless, several are one per orthogonal region.</summary>
    [ElementRef(IndexKinds = ["state"])]
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>
    /// The revision level, 1 to 3, this move moves (revisions-design.md decision 19): on a move whose verb is <c>revise</c>, the level of
    /// that revise; on any other move, the move gives the object its next revision at that level. <see langword="null"/>, and left out of
    /// the JSON, when the move moves no revision (a revise move then moves level 1).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NextRevision { get; init; }

    /// <summary>The actions run on the transition, in list order.</summary>
    [ElementRef(IndexKinds = ["action"])]
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>Whether the source is exited and re-entered when a target is its descendant.</summary>
    public bool External { get; init; }

    /// <summary>The signature gate, on an event transition.</summary>
    public ProcessGate? Gate { get; init; }

    /// <summary>A library's gate used instead of <see cref="Gate"/> (MQ9021); signatures still count per transition.</summary>
    [ElementRef(IndexKinds = ["gate"])]
    public string? GatePolicy { get; init; }

    /// <summary>Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Custom property values, validated by extension schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>What fires a transition.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransitionTrigger>))]
public enum TransitionTrigger
{
    /// <summary>An occurrence of the transition's event (<c>event</c>).</summary>
    [JsonStringEnumMemberName("event")] Event,

    /// <summary>A delay after the source was entered (<c>after</c>).</summary>
    [JsonStringEnumMemberName("after")] After,

    /// <summary>The source compound reached a final child, or every region of the source parallel did (<c>done</c>).</summary>
    [JsonStringEnumMemberName("done")] Done,

    /// <summary>Eventless (<c>always</c>).</summary>
    [JsonStringEnumMemberName("always")] Always,

    /// <summary>An invoke of the source completed (<c>invoke-done</c>).</summary>
    [JsonStringEnumMemberName("invoke-done")] InvokeDone,

    /// <summary>An invoke of the source failed (<c>invoke-error</c>).</summary>
    [JsonStringEnumMemberName("invoke-error")] InvokeError,
}

/// <summary>A process-local event (phase 4 promotes events to an element kind with their ids unchanged).</summary>
public sealed record ProcessEvent : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The event's name: an identifier unique in the process.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// What the actor does by sending the event, by verb key (verbs-design.md decision 11): a built-in verb or one of the project's own;
    /// <see langword="null"/> when it names none.
    /// </summary>
    [VerbKey]
    public string? Verb { get; init; }

    /// <summary>The typed payload, in canonical order.</summary>
    public IReadOnlyList<ModelAttribute> Payload { get; init; } = [];

    /// <summary>The actors that may send the event; empty means any actor.</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> Actors { get; init; } = [];

    /// <summary>Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Custom property values, validated by extension schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>A named condition over context and event; without an expression it is a stub.</summary>
public sealed record ProcessGuard : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The guard's name: an identifier unique in the process.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>A JavaScript expression returning a boolean, or <see langword="null"/> for a stub.</summary>
    public string? Expression { get; init; }

    /// <summary>
    /// The condition as an expression tree over context and event (next-phase-3-design.md section 3.1), or <see langword="null"/>;
    /// only true passes.
    /// </summary>
    public Expr? Condition { get; init; }

    /// <summary>Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Custom property values, validated by extension schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>
/// A named effect; with neither an expression nor sets it is a stub, which the team's system implements. Actions stay: an action may call an
/// operation (<see cref="Call"/>, processes-operations-design.md decision 5), which is that work recorded; a call-only action is still a stub.
/// </summary>
public sealed record ProcessAction : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The action's name: an identifier unique in the process.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>A JavaScript expression returning an object of context updates, or <see langword="null"/>; with no <see cref="Sets"/> either, a stub.</summary>
    public string? Expression { get; init; }

    /// <summary>
    /// The values the action sets on the object (object-effects-design.md decision 1), in order: each value is worked out from the object and
    /// context as they were when the action started, then all are applied together.
    /// </summary>
    public IReadOnlyList<ProcessSet> Sets { get; init; } = [];

    /// <summary>
    /// The operation the action calls, and a value for any of its inputs (processes-operations-design.md decision 5), or <see langword="null"/>
    /// when it calls none. The simulator runs nothing for it; the trace records it. Not in a library (MQ9020).
    /// </summary>
    public ProcessCall? Call { get; init; }

    /// <summary>The internal events queued after the action.</summary>
    [ElementRef(IndexKinds = ["event"])]
    public IReadOnlyList<string> Raises { get; init; } = [];

    /// <summary>Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Custom property values, validated by extension schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>
/// One attribute of the object an action sets, and its value (object-effects-design.md decisions 1 to 3): an attribute every subject of the
/// process holds under one id, neither derived nor bound to a lifecycle (MQ9801, MQ9802), and a value in the expression language that reads
/// what a guard reads and fits the attribute's type (MQ3103).
/// </summary>
public sealed record ProcessSet : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The attribute set: an id, or the keyed id of an inherited or contributed attribute.</summary>
    [ElementRef(Keyed = true)]
    public required string Attribute { get; init; }

    /// <summary>The value; text is accepted on writes and stored as a tree (MQ3108).</summary>
    public required Expr Value { get; init; }
}

/// <summary>
/// The operation an action calls (processes-operations-design.md decisions 5 to 8): when the action runs, the team's system asks for the
/// operation to be run with the listed inputs set to the listed values. When, whether the move waits, retries and what each outcome or error
/// means are the team's.
/// </summary>
public sealed record ProcessCall
{
    /// <summary>The operation called.</summary>
    [ElementRef(ElementKind.Operation)]
    public required string Operation { get; init; }

    /// <summary>Values for some of the operation's input members, in order; inputs not listed are the team's (MQ9026).</summary>
    public IReadOnlyList<ProcessCallInput> Inputs { get; init; } = [];
}

/// <summary>
/// One input member of a called operation and its value (processes-operations-design.md decision 6): typed like a set's value, against the
/// member's type, and worked out from the object and context as they were when the action started.
/// </summary>
public sealed record ProcessCallInput : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The input member of the called operation.</summary>
    [ElementRef(IndexKinds = ["operation-member"])]
    public required string Member { get; init; }

    /// <summary>The value; text is accepted on writes and stored as a tree (MQ3108).</summary>
    public required Expr Value { get; init; }
}

/// <summary>Work a state starts on entry: a sub-process, a service task or a human task.</summary>
public sealed record ProcessInvoke : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The invoke's name: an identifier unique in the process.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>What is invoked.</summary>
    public required InvokeType Type { get; init; }

    /// <summary>The sub-process, for <see cref="InvokeType.Process"/>.</summary>
    [ElementRef(ElementKind.Process)]
    public string? Process { get; init; }

    /// <summary>
    /// The operation whose run does the work of a <see cref="InvokeType.Service"/> step (processes-operations-design.md decision 11), or
    /// <see langword="null"/>; on another type it is MQ9025.
    /// </summary>
    [ElementRef(ElementKind.Operation)]
    public string? Operation { get; init; }

    /// <summary>Who may complete a human task.</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> Actors { get; init; } = [];

    /// <summary>Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Custom property values, validated by extension schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>What an invoke starts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InvokeType>))]
public enum InvokeType
{
    /// <summary>A sub-process that runs to its final state (<c>process</c>).</summary>
    [JsonStringEnumMemberName("process")] Process,

    /// <summary>A service task completed from outside (<c>service</c>).</summary>
    [JsonStringEnumMemberName("service")] Service,

    /// <summary>A task an assigned actor completes (<c>human-task</c>).</summary>
    [JsonStringEnumMemberName("human-task")] HumanTask,
}

/// <summary>
/// A signature gate on an event transition: the transition fires on the occurrence of its event that completes the gate. A gate with
/// <see cref="Stages"/> is an approval (signoff-design.md decision 1): its signatures carry the signers and counts, and the gate's own are
/// empty.
/// </summary>
public sealed record ProcessGate : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The gate's name: an identifier unique in the process.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The approval's stages, each holding named signatures (signoff-design.md decision 1), listed in round order (decision 5).
    /// <see langword="null"/>, and left out of the file, for a gate without stages, which keeps today's meaning.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GateStage>? Stages { get; init; }

    /// <summary>Whether the gate is an approval: it has stages.</summary>
    [JsonIgnore]
    public bool UsesStages => Stages is { Count: > 0 };

    /// <summary>Every stage's signatures in file order, stage by stage; empty for a gate without stages.</summary>
    [JsonIgnore]
    public IEnumerable<NamedSignature> AllSignatures => Stages?.SelectMany(s => s.Signatures) ?? [];

    private readonly int? _required;

    /// <summary>The number of signatures needed: 1 when the file leaves it out. A gate without stages only (MQ8201).</summary>
    public int Required { get => _required ?? 1; init => _required = value; }

    /// <summary>Whether the file gives <see cref="Required"/>, even as its default 1: MQ8201 flags it on a gate with stages.</summary>
    [JsonIgnore]
    public bool HasRequired => _required is not null;

    /// <summary>The actors whose members may sign. Empty on a gate with stages (MQ8201), whose signatures name their own.</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> Signers { get; init; } = [];

    /// <summary>Actors each of which must give at least one signature (a subset of <see cref="Signers"/>, MQ9102).</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> RequiredActors { get; init; } = [];

    /// <summary>Whether one signer may count twice.</summary>
    public bool AllowRepeatSigner { get; init; }

    /// <summary>
    /// With stages: whether one person may give more than one response that counts on the approval, across its signatures
    /// (signoff-design.md decision 16). A gate with stages only (MQ8201).
    /// </summary>
    public bool AllowSignerAcrossSignatures { get; init; }

    /// <summary>Whether each signature must carry a reason.</summary>
    public bool ReasonRequired { get; init; }

    /// <summary>What a signature means.</summary>
    public required IReadOnlyList<GateMeaning> Meanings { get; init; }

    /// <summary>Extra fields recorded with each signature, in canonical order.</summary>
    public IReadOnlyList<ModelAttribute> AuditAttributes { get; init; } = [];

    /// <summary>Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Custom property values, validated by extension schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>A stage of an approval (signoff-design.md decision 4; part kind <c>gate-stage</c>): named signatures only.</summary>
public sealed record GateStage : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The stage's name: unique among the gate's stages (MQ3001).</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The round the stage runs in, from 1 (signoff-design.md decision 5): stages of one round run side by side, rounds one after another.
    /// </summary>
    public int Round { get; init; } = 1;

    /// <summary>
    /// Whether the stage runs, judged when its round starts (signoff-design.md decision 6): only false skips it. <see langword="null"/> for
    /// always. Never on a gate policy's stage (MQ8202).
    /// </summary>
    public Expr? When { get; init; }

    /// <summary>The stage's signatures, in file order: the order the engine picks one in when a step names none.</summary>
    public required IReadOnlyList<NamedSignature> Signatures { get; init; }

    /// <summary>Markdown text, or a reference to a sidecar Markdown file (localizable).</summary>
    public Description? Description { get; init; }
}

/// <summary>
/// A signature an approval needs (signoff-design.md decision 3; part kind <c>named-signature</c>): who may give it and how many, whether it
/// is needed, and who may not give it. It is complete when it has <see cref="Required"/> signatures and each required actor has signed.
/// </summary>
public sealed record NamedSignature : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The signature's name: unique among the gate's signatures, across its stages (MQ3001).</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>The actors whose members may give this signature.</summary>
    [ElementRef(ElementKind.Actor)]
    public required IReadOnlyList<string> Signers { get; init; }

    /// <summary>The number of signatures needed.</summary>
    public int Required { get; init; } = 1;

    /// <summary>Actors each of which must give at least one of these signatures (a subset of <see cref="Signers"/>).</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> RequiredActors { get; init; } = [];

    /// <summary>Whether one person may count twice on this signature.</summary>
    public bool AllowRepeatSigner { get; init; }

    /// <summary>
    /// Whether the signature is needed, judged when its round starts (signoff-design.md decision 7): only false means not needed.
    /// <see langword="null"/> for always needed. Never on a gate policy's signature (MQ8202).
    /// </summary>
    public Expr? When { get; init; }

    /// <summary>
    /// Who may not give the signature (signoff-design.md decision 15): whoever did one of these verbs or moves since the object entered the
    /// gated move's source state.
    /// </summary>
    public IReadOnlyList<NotByEntry> NotBy { get; init; } = [];

    /// <summary>
    /// The actors the signature may be delegated to (signoff-design.md decision 20); empty when it may not be. The person it goes to must be
    /// a member of one of them.
    /// </summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> DelegateTo { get; init; } = [];

    /// <summary>
    /// An ISO 8601 duration from when the signature's round started, after which a reminder falls due (signoff-design.md decision 21);
    /// <see langword="null"/> for none.
    /// </summary>
    public string? RemindAfter { get; init; }

    /// <summary>
    /// An ISO 8601 duration from when the signature's round started, after which an escalation to <see cref="EscalateTo"/> falls due
    /// (signoff-design.md decision 21); <see langword="null"/> for none. Only with <see cref="EscalateTo"/> (MQ8204).
    /// </summary>
    public string? EscalateAfter { get; init; }

    /// <summary>The actors an escalation goes to (signoff-design.md decision 21); empty for none. Only with <see cref="EscalateAfter"/> (MQ8204).</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> EscalateTo { get; init; } = [];

    /// <summary>Whether the person's identity is checked again for every response on the signature (signoff-design.md decision 22).</summary>
    public bool Reauthenticate { get; init; }

    /// <summary>Why the signature is needed: Markdown text, or a reference to a sidecar Markdown file (localizable).</summary>
    public Description? Description { get; init; }
}

/// <summary>An entry of a named signature's <c>notBy</c> (signoff-design.md decision 15): a verb, or a transition of the lifecycle.</summary>
public sealed record NotByEntry
{
    /// <summary>A verb, by key; <see langword="null"/> when the entry names a transition.</summary>
    [VerbKey]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verb { get; init; }

    /// <summary>A transition of the lifecycle; <see langword="null"/> when the entry names a verb.</summary>
    [ElementRef(IndexKinds = ["transition"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transition { get; init; }
}

/// <summary>What a signer answers an approval with (signoff-design.md decision 9): the built-in verb of the same key.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SignatureResponse>))]
public enum SignatureResponse
{
    /// <summary>A signature (<c>sign</c>), the default.</summary>
    [JsonStringEnumMemberName("sign")] Sign,

    /// <summary>A veto (<c>reject</c>): it fails the approval, clears its signatures and moves nothing.</summary>
    [JsonStringEnumMemberName("reject")] Reject,

    /// <summary>An abstention (<c>ignore</c>): it finishes the person on the signature and leaves the count.</summary>
    [JsonStringEnumMemberName("ignore")] Ignore,

    /// <summary>Taking back the person's latest signature (<c>withdraw</c>).</summary>
    [JsonStringEnumMemberName("withdraw")] Withdraw,

    /// <summary>
    /// Handing the person's part to someone in the signature's <c>delegateTo</c> (<c>delegate</c>, signoff-design.md decision 20): it
    /// finishes the person on the signature and moves nothing.
    /// </summary>
    [JsonStringEnumMemberName("delegate")] Delegate,
}

/// <summary>What a signature on a gate means ("Reviewed as finance controller").</summary>
public sealed record GateMeaning : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The meaning's name.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }
}

/// <summary>
/// A person, a role, a group, an organization, an external system or the system itself that takes part in processes (phase-3-design.md
/// section 2.5, actors-design.md; <c>model/actors/</c>). An actor may join a package (membership only), and be a member of roles, groups
/// and organizations (<see cref="MemberOf"/>). A persona is an actor carrying a project-defined stereotype: the product defines none.
/// </summary>
public sealed record Actor : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Actor;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root. Membership only: it never changes
    /// where the element goes in a database.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>What the actor is.</summary>
    public required ActorType Type { get; init; }

    /// <summary>The roles, groups and organizations the actor is a member of, in file order (actors-design.md decisions 3 to 5): an actor
    /// in a process's list stands for itself and its members at any depth.</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> MemberOf { get; init; } = [];

    /// <summary>How the actor is worked out from the object a process runs for (actors-design.md decisions 8 to 12), or
    /// <see langword="null"/> for an actor that stands for itself. Such an actor is never a member and has no members.</summary>
    public ActorRelative? Relative { get; init; }
}

/// <summary>
/// How an actor is worked out from the object (actors-design.md decision 9): the object's owner, the actor a field of the object names,
/// or the members of a role, group or organization within a context object. Exactly one form (MQ9603).
/// </summary>
public sealed record ActorRelative
{
    /// <summary><see langword="true"/> for the object's owner, held where each subject entry's <see cref="ProcessSubject.Owner"/> names;
    /// <see langword="null"/> when the file has no <c>owner</c>.</summary>
    public bool? Owner { get; init; }

    /// <summary>The actor a field of the object names: to-one relation ends, then a single-valued attribute or a to-one end;
    /// <see langword="null"/> when the file has no <c>path</c>.</summary>
    [ElementRef(IndexKinds = ["attribute", "end"])]
    public IReadOnlyList<string>? Path { get; init; }

    /// <summary>The role, group or organization whose members within the context object this actor is, or <see langword="null"/>.</summary>
    [ElementRef(ElementKind.Actor)]
    public string? MembersOf { get; init; }

    /// <summary>With <see cref="MembersOf"/>: the to-one relation ends that lead from the object to its context; empty for the object.</summary>
    [ElementRef(IndexKinds = ["end"])]
    public IReadOnlyList<string> Within { get; init; } = [];

    /// <summary>The forms the file gives, in the order owner, path, members-of; one for a well-formed actor (MQ9603).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Forms =>
        [.. new[] { Owner == true ? "owner" : null, Path is not null ? "path" : null, MembersOf is not null ? "members-of" : null }.OfType<string>()];

    /// <summary>The form: the first of <see cref="Forms"/>, or <c>none</c> when the file gives none.</summary>
    [JsonIgnore]
    public string Form => Forms is [var first, ..] ? first : "none";

    /// <summary>The ids along the path: <see cref="Path"/> for a path, <see cref="Within"/> for members-of, empty for the owner.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Steps => Form switch { "path" => Path ?? [], "members-of" => Within, _ => [] };
}

/// <summary>What an actor is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActorType>))]
public enum ActorType
{
    /// <summary>A person (<c>person</c>).</summary>
    [JsonStringEnumMemberName("person")] Person,

    /// <summary>A role people hold (<c>role</c>).</summary>
    [JsonStringEnumMemberName("role")] Role,

    /// <summary>A system outside the model (<c>external-system</c>).</summary>
    [JsonStringEnumMemberName("external-system")] ExternalSystem,

    /// <summary>A set of actors, such as a shift or a board (<c>group</c>; actors-design.md decision 1).</summary>
    [JsonStringEnumMemberName("group")] Group,

    /// <summary>A unit with a place in a hierarchy, such as a supplier or a plant (<c>organization</c>; actors-design.md decision 1).</summary>
    [JsonStringEnumMemberName("organization")] Organization,

    /// <summary>The system itself, shown as Automatic (<c>system</c>; actors-design.md decision 2): at most one per project (MQ9605).</summary>
    [JsonStringEnumMemberName("system")] System,
}

/// <summary>What the actor types mean for membership (actors-design.md decision 4).</summary>
internal static class ActorTypes
{
    /// <summary>Whether actors of a type may have members: a role, a group or an organization.</summary>
    /// <param name="type">The type.</param>
    /// <returns><see langword="true"/> for a type that holds members.</returns>
    public static bool HoldsMembers(ActorType type) => type is ActorType.Role or ActorType.Group or ActorType.Organization;
}

/// <summary>
/// A recorded event sequence of one process with the expected states after each step (phase-3-design.md section 2.4;
/// <c>model/scenarios/&lt;process stem&gt;/</c>). A scenario belongs to its process and is deleted with it.
/// </summary>
public sealed record Scenario : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Scenario;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root. Membership only: it never changes
    /// where the element goes in a database.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>The process the scenario runs; the scenario belongs to it.</summary>
    [ElementRef(ElementKind.Process, Owning = true)]
    public required string Process { get; init; }

    /// <summary>The scenario's cast (lifecycle-design.md decision 22): the object its process runs for and the objects they link to.</summary>
    public IReadOnlyList<ScenarioObject> Objects { get; init; } = [];

    /// <summary>The object the process runs for; required once there are <see cref="Objects"/>.</summary>
    [ElementRef(IndexKinds = ["object"])]
    public string? Subject { get; init; }

    /// <summary>Who acts in the scenario (actors-design.md decision 14), in file order; parts of the scenario with name forms.</summary>
    public IReadOnlyList<ScenarioParticipant> Participants { get; init; } = [];

    /// <summary>The starting point; <see langword="null"/> means the attribute defaults and the fixed start instant.</summary>
    public ScenarioStart? Start { get; init; }

    /// <summary>The steps, in time order.</summary>
    public required IReadOnlyList<ScenarioStep> Steps { get; init; }

    /// <summary>Whether the root is final after the last step.</summary>
    public ScenarioOutcome Outcome { get; init; } = ScenarioOutcome.Active;
}

/// <summary>
/// An object of a scenario (lifecycle-design.md decision 22): an instance of an entity, with its values and its links to other objects of
/// the scenario. A value not given reads as the attribute's default, then null.
/// </summary>
public sealed record ScenarioObject : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The name, unique in the scenario.</summary>
    public required string Name { get; init; }

    /// <summary>The entity the object is an instance of.</summary>
    [ElementRef(ElementKind.Entity)]
    public required string Entity { get; init; }

    /// <summary>Values by attribute id.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyDictionary<string, JsonElement> Values { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>
    /// The object's states in lifecycles other than the one the scenario runs, by state id (object-effects-design.md decision 10): at most
    /// one active configuration per lifecycle (MQ9305). <c>in_state</c> reads them.
    /// </summary>
    [ElementRef(IndexKinds = ["state"])]
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>
    /// The object's current revision, a value of its entity's revision sequence (revisions-design.md decision 40), for an entity with
    /// revisions and no bound revision attribute. <see langword="null"/>, and left out of the JSON, when absent: the bound attribute's value
    /// or default, else the sequence's start.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; init; }

    /// <summary>
    /// Whether the object starts frozen (rights-design.md decision 16): <c>modify</c> and <c>delete</c> are refused, and so are
    /// <c>connect</c> and <c>disconnect</c> on its links, until a <c>thaw</c>. Only the subject's is read. Left out of the JSON when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Frozen { get; init; }

    /// <summary>The objects this object links to, each through a relation end.</summary>
    public IReadOnlyList<ScenarioLink> Links { get; init; } = [];

    /// <summary>The participant that owns the object (actors-design.md decision 16), for a subject whose entry names no owner field.</summary>
    [ElementRef(IndexKinds = ["participant"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Owner { get; init; }
}

/// <summary>
/// Someone or something that acts in a scenario (actors-design.md decision 14): it stands for an actor, holds roles, groups and
/// organizations, optionally within one of the scenario's objects, and may have an object as its record. It lives in the scenario only.
/// </summary>
public sealed record ScenarioParticipant : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The name, unique in the scenario.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The value the team's system would hold for them (a string or an integer); the name when absent.</summary>
    public JsonElement? Identity { get; init; }

    /// <summary>The person, external system or system actor they stand for, whose memberships they then have.</summary>
    [ElementRef(ElementKind.Actor)]
    public string? Actor { get; init; }

    /// <summary>The roles, groups and organizations they hold, in file order.</summary>
    public IReadOnlyList<ScenarioHolding> Holds { get; init; } = [];

    /// <summary>The scenario object that is their record.</summary>
    [ElementRef(IndexKinds = ["object"])]
    public string? Object { get; init; }

    /// <summary>The identity's text: a string as written, an integer in invariant digits, else the name.</summary>
    [JsonIgnore]
    public string IdentityText => Identity switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString() ?? Name,
        { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
        _ => Name,
    };
}

/// <summary>A role, group or organization a participant holds, with no context or within one of the scenario's objects.</summary>
public sealed record ScenarioHolding
{
    /// <summary>The holding's ULID.</summary>
    public required string Id { get; init; }

    /// <summary>The role, group or organization held.</summary>
    [ElementRef(ElementKind.Actor)]
    public required string Actor { get; init; }

    /// <summary>The scenario object it counts within; without it, it counts only where no context is asked (decision 13).</summary>
    [ElementRef(IndexKinds = ["object"])]
    public string? Within { get; init; }
}

/// <summary>A link from a scenario object to another object of the scenario, through a relation end.</summary>
public sealed record ScenarioLink
{
    /// <summary>The relation end navigated from the object.</summary>
    [ElementRef(IndexKinds = ["end"])]
    public required string End { get; init; }

    /// <summary>The linked object.</summary>
    [ElementRef(IndexKinds = ["object"])]
    public required string Object { get; init; }

    /// <summary>
    /// Whether the link starts frozen (rights-design.md decision 16): it cannot be disconnected or have its attributes modified until a
    /// <c>thaw</c>, and the objects at its ends are not frozen by it. Only on a link of the subject (MQ9305). Left out of the JSON when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Frozen { get; init; }
}

/// <summary>A scenario's starting point.</summary>
public sealed record ScenarioStart
{
    /// <summary>Initial context values over the attribute defaults, by attribute id.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyDictionary<string, JsonElement> Context { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>The simulated clock's start (an ISO 8601 instant).</summary>
    public string At { get; init; } = DefaultStart;

    /// <summary>The default start instant: the sandbox's fixed instant.</summary>
    public const string DefaultStart = "2000-01-01T00:00:00Z";
}

/// <summary>Whether a scenario ends with the root final.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScenarioOutcome>))]
public enum ScenarioOutcome
{
    /// <summary>The root is final after the last step (<c>final</c>).</summary>
    [JsonStringEnumMemberName("final")] Final,

    /// <summary>The root is still active (<c>active</c>).</summary>
    [JsonStringEnumMemberName("active")] Active,
}

/// <summary>One input to the process and what is expected after it.</summary>
public sealed record ScenarioStep : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The kind of input.</summary>
    public StepInput Input { get; init; } = StepInput.Event;

    /// <summary>The event sent, for <see cref="StepInput.Event"/>.</summary>
    [ElementRef(IndexKinds = ["event"])]
    public string? Event { get; init; }

    /// <summary>The invoke that completed or failed.</summary>
    [ElementRef(IndexKinds = ["invoke"])]
    public string? Invoke { get; init; }

    /// <summary>An ISO 8601 duration to advance the clock by, for <see cref="StepInput.Time"/>.</summary>
    public string? After { get; init; }

    /// <summary>
    /// For <see cref="StepInput.Verb"/>: the verb done, by key (rights-design.md decision 25), an object, link or own verb. Left out of a
    /// trace's echo when absent.
    /// </summary>
    [VerbKey]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verb { get; init; }

    /// <summary>The scenario's participant that acts (actors-design.md decision 15); the engine works out the actor it acts as unless
    /// <see cref="Actor"/> is given. Left out of a trace's echo when absent.</summary>
    [ElementRef(IndexKinds = ["participant"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Participant { get; init; }

    /// <summary>The actor sending the input.</summary>
    [ElementRef(ElementKind.Actor)]
    public string? Actor { get; init; }

    /// <summary>The signing person's identity, for gated events.</summary>
    public string? Signer { get; init; }

    /// <summary>
    /// How the step answers an approval on its event (signoff-design.md decision 25); a signature by default, which a trace's echo leaves out.
    /// Another response only on an event step whose event is gated by an approval (MQ9305).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SignatureResponse Response { get; init; } = SignatureResponse.Sign;

    /// <summary>
    /// The approval's named signature the step answers (signoff-design.md decision 8); <see langword="null"/>, and left out of a trace's echo,
    /// when the engine picks it.
    /// </summary>
    [ElementRef(IndexKinds = ["named-signature"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; init; }

    /// <summary>
    /// With <see cref="SignatureResponse.Delegate"/>: the scenario's participant the signature is delegated to (signoff-design.md decision
    /// 34). <see langword="null"/>, and left out of a trace's echo, when absent.
    /// </summary>
    [ElementRef(IndexKinds = ["participant"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DelegateTo { get; init; }

    /// <summary>
    /// The scenario's participant who delegated the signature to the person answering (signoff-design.md decision 34); the answer counts as
    /// theirs. <see langword="null"/>, and left out of a trace's echo, when absent.
    /// </summary>
    [ElementRef(IndexKinds = ["participant"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OnBehalfOf { get; init; }

    /// <summary>The meaning of the signature.</summary>
    [ElementRef(IndexKinds = ["meaning"])]
    public string? Meaning { get; init; }

    /// <summary>The signature's reason.</summary>
    public string? Reason { get; init; }

    /// <summary>Payload values by attribute id.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyDictionary<string, JsonElement> Payload { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>
    /// For a <c>modify</c> verb step on the subject: the values it sets, by attribute id (rights-design.md decision 25), seen by later guards
    /// and sets; for a <c>create</c> verb step: the new object's values, which before actions and checks read and an accepted step writes
    /// (trigger-points-design.md decision 27). <see langword="null"/>, and left out of a trace's echo, when absent.
    /// </summary>
    [ElementRef(IndexKinds = ["attribute"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Values { get; init; }

    /// <summary>
    /// For a <c>read</c> verb step on the subject: the attributes it reads (rights-design.md decision 25), each checked against the rights
    /// that name attributes (decision 21). <see langword="null"/>, and left out of a trace's echo, when absent: the read is of the object.
    /// </summary>
    [ElementRef(IndexKinds = ["attribute"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Attributes { get; init; }

    /// <summary>For a verb step on a link: the subject's relation end the link goes through; <see langword="null"/> for the subject itself.</summary>
    [ElementRef(IndexKinds = ["end"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? End { get; init; }

    /// <summary>With <see cref="End"/>: the scenario object at the link's other end.</summary>
    [ElementRef(IndexKinds = ["object"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Object { get; init; }

    /// <summary>
    /// The step's revision (revisions-design.md decisions 14 and 40): on a verb step that names <c>revise</c>, a chosen name or the level
    /// that moves; on an event step, a chosen name only. <see langword="null"/>, and left out of a trace's echo, when absent.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RevisionChoice? Revision { get; init; }

    /// <summary>
    /// Results for guards without an expression, by guard id, as booleans; and outcomes for checks the team writes, by trigger point id, as
    /// the words <c>pass</c>, <c>warning</c> and <c>error</c> (trigger-points-design.md decision 26).
    /// </summary>
    [ElementRef(IndexKinds = ["guard", "process-trigger-point", "trigger-point"])]
    public IReadOnlyDictionary<string, StepAssumption> Assume { get; init; } = ImmutableDictionary<string, StepAssumption>.Empty;

    /// <summary>What is expected after the step; <see langword="null"/> checks nothing.</summary>
    public StepExpectation? Expect { get; init; }

    /// <summary>Markdown text, or a reference to a sidecar Markdown file (localizable).</summary>
    public Description? Description { get; init; }
}

/// <summary>The kind of input a scenario step gives.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepInput>))]
public enum StepInput
{
    /// <summary>An event (<c>event</c>).</summary>
    [JsonStringEnumMemberName("event")] Event,

    /// <summary>The clock advances (<c>time</c>).</summary>
    [JsonStringEnumMemberName("time")] Time,

    /// <summary>An invoke completed (<c>invoke-done</c>).</summary>
    [JsonStringEnumMemberName("invoke-done")] InvokeDone,

    /// <summary>An invoke failed (<c>invoke-error</c>).</summary>
    [JsonStringEnumMemberName("invoke-error")] InvokeError,

    /// <summary>A participant does a verb to the subject or one of its links (<c>verb</c>, rights-design.md decision 25).</summary>
    [JsonStringEnumMemberName("verb")] Verb,
}

/// <summary>What a step expects.</summary>
public sealed record StepExpectation
{
    /// <summary>Whether the input is accepted; <see langword="false"/> records a refusal.</summary>
    public bool Accepted { get; init; } = true;

    /// <summary>
    /// With <see cref="Accepted"/> <see langword="false"/>: why the step is refused, a value of <see cref="Processes.Refusals"/>
    /// (rights-design.md decision 26; MQ9308); <see langword="null"/> checks nothing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Refusal { get; init; }

    /// <summary>
    /// With <see cref="Accepted"/> <see langword="false"/>: the id of the element that refused the step (rights-design.md decision 12; MQ9308);
    /// <see langword="null"/> checks nothing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefusedBy { get; init; }

    /// <summary>The active atomic states, in document order.</summary>
    [ElementRef(IndexKinds = ["state"])]
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>The attributes the step changed, by attribute id.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyDictionary<string, JsonElement> Context { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>The object's attributes the step set, by attribute id (object-effects-design.md decision 4; MQ9307).</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyDictionary<string, JsonElement> Values { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>
    /// The subject's revision after the step (revisions-design.md decision 40; MQ9312); <see langword="null"/> checks nothing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; init; }

    /// <summary>
    /// The ids of exactly the checks that warned, each once (trigger-points-design.md decision 28; MQ9310); <see langword="null"/> checks nothing, and an
    /// empty list expects none.
    /// </summary>
    [ElementRef(IndexKinds = ["process-trigger-point", "trigger-point"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? CheckWarnings { get; init; }

    /// <summary>
    /// The ids of exactly the trigger points that ran, in run order, repeats included and skipped ones left out (trigger-points-design.md
    /// decision 28; MQ9310); <see langword="null"/> checks nothing, and an empty list expects none.
    /// </summary>
    [ElementRef(IndexKinds = ["process-trigger-point", "trigger-point"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? TriggerPoints { get; init; }

    /// <summary>
    /// The number of signatures counted after the step, by named signature id (signoff-design.md decision 27; MQ9311); only the listed
    /// signatures are compared. <see langword="null"/> checks nothing.
    /// </summary>
    [ElementRef(IndexKinds = ["named-signature"])]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? Signatures { get; init; }

    /// <summary>
    /// The reminders and escalations due at the step (signoff-design.md decisions 27 and 35; MQ9311), compared as a set;
    /// <see langword="null"/> checks nothing, and an empty list expects none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExpectedNotice>? Notices { get; init; }
}

/// <summary>A reminder or an escalation a step expects due (signoff-design.md decision 35): a named signature and a kind.</summary>
public sealed record ExpectedNotice
{
    /// <summary>The named signature the notice is about.</summary>
    [ElementRef(IndexKinds = ["named-signature"])]
    public required string Signature { get; init; }

    /// <summary>Whether it is a reminder or an escalation.</summary>
    public required NoticeKind Kind { get; init; }
}

/// <summary>The kind of a notice that falls due on an approval (signoff-design.md decision 21).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoticeKind>))]
public enum NoticeKind
{
    /// <summary>A reminder to the people who may still answer the signature (<c>remind</c>).</summary>
    [JsonStringEnumMemberName("remind")] Remind,

    /// <summary>An escalation to the signature's <c>escalateTo</c> actors (<c>escalate</c>).</summary>
    [JsonStringEnumMemberName("escalate")] Escalate,
}
