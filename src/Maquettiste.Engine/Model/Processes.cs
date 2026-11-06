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
/// An entry of a process's <c>subjects</c> (lifecycle-design.md decision 5): an entity the process governs, with the entities derived
/// from it, and the attribute that holds its state.
/// </summary>
public sealed record ProcessSubject : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The entity.</summary>
    [ElementRef(ElementKind.Entity)]
    public required string Entity { get; init; }

    /// <summary>The entity's attribute that holds the lifecycle state, or <see langword="null"/>.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public string? BoundAttribute { get; init; }

    /// <summary>Where the object's owner is held (actors-design.md decision 10): a single-valued attribute or a to-one relation end of the
    /// entity (MQ9606), or <see langword="null"/> when the team's system holds it.</summary>
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

/// <summary>A named effect; without an expression it is a stub. Its name is the name phase 4 gives the operation it becomes.</summary>
public sealed record ProcessAction : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The action's name: an identifier unique in the process.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>A JavaScript expression returning an object of context updates, or <see langword="null"/> for a stub.</summary>
    public string? Expression { get; init; }

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

/// <summary>A signature gate on an event transition: the transition fires on the occurrence of its event that completes the gate.</summary>
public sealed record ProcessGate : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>The gate's name: an identifier unique in the process.</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>The number of signatures needed.</summary>
    public int Required { get; init; } = 1;

    /// <summary>The actors whose members may sign.</summary>
    [ElementRef(ElementKind.Actor)]
    public required IReadOnlyList<string> Signers { get; init; }

    /// <summary>Actors each of which must give at least one signature (a subset of <see cref="Signers"/>, MQ9102).</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> RequiredActors { get; init; } = [];

    /// <summary>Whether one signer may count twice.</summary>
    public bool AllowRepeatSigner { get; init; }

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

    /// <summary>The objects this object links to, each through a relation end.</summary>
    public IReadOnlyList<ScenarioLink> Links { get; init; } = [];
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

    /// <summary>The actor sending the input.</summary>
    [ElementRef(ElementKind.Actor)]
    public string? Actor { get; init; }

    /// <summary>The signing person's identity, for gated events.</summary>
    public string? Signer { get; init; }

    /// <summary>The meaning of the signature.</summary>
    [ElementRef(IndexKinds = ["meaning"])]
    public string? Meaning { get; init; }

    /// <summary>The signature's reason.</summary>
    public string? Reason { get; init; }

    /// <summary>Payload values by attribute id.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyDictionary<string, JsonElement> Payload { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>Results for guards without an expression, by guard id.</summary>
    [ElementRef(IndexKinds = ["guard"])]
    public IReadOnlyDictionary<string, bool> Assume { get; init; } = ImmutableDictionary<string, bool>.Empty;

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
}

/// <summary>What a step expects.</summary>
public sealed record StepExpectation
{
    /// <summary>Whether the input is accepted; <see langword="false"/> records a refusal.</summary>
    public bool Accepted { get; init; } = true;

    /// <summary>The active atomic states, in document order.</summary>
    [ElementRef(IndexKinds = ["state"])]
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>The attributes the step changed, by attribute id.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyDictionary<string, JsonElement> Context { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}
