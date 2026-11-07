using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// Why an input was refused (phase-3-design.md section 4.1, rights-design.md decision 12): the values of a trace's refusal and of a step's
/// <c>expect.refusal</c>. The checks run in a fixed order, and the first that fails names the refusal.
/// </summary>
public static class Refusals
{
    /// <summary>No transition matches the input in the active configuration (or the input names nothing pending).</summary>
    public const string NoTransition = "no-transition";

    /// <summary>A matching transition's guard is false.</summary>
    public const string Guard = "guard";

    /// <summary>The input's actor may not send it.</summary>
    public const string Actor = "actor";

    /// <summary>The actor is not one of the gate's signers, or no signer identity was given.</summary>
    public const string GateSigner = "gate-signer";

    /// <summary>The signer already signed and the gate does not allow a repeat signer.</summary>
    public const string GateRepeat = "gate-repeat";

    /// <summary>The gate requires a reason and none was given.</summary>
    public const string GateReason = "gate-reason";

    /// <summary>A deny right that holds names the verb (rights-design.md decision 12): the right refused it.</summary>
    public const string Right = "right";

    /// <summary>No allow holds in a lifecycle whose default is deny (rights-design.md decision 12): the lifecycle refused it.</summary>
    public const string Default = "default";

    /// <summary>
    /// The object, or the link, is frozen and the verb is <c>modify</c>, <c>connect</c>, <c>disconnect</c> or <c>delete</c> (rights-design.md
    /// decision 17): the first check, which no allow lifts. Freezing is built in, so no element refused it and <c>refusedBy</c> is left out.
    /// </summary>
    public const string Frozen = "frozen";

    /// <summary>Every refusal value, in the order the checks run, then the gate's own: what <c>expect.refusal</c> may name.</summary>
    public static IReadOnlyList<string> All { get; } = [Frozen, Actor, Right, Default, NoTransition, Guard, GateSigner, GateRepeat, GateReason];
}

/// <summary>
/// A right that answered for a step (rights-design.md decision 26): it held for the actor, the verb and the object's state, and said its
/// access. A named right used by several lifecycles answers once for each lifecycle that asked.
/// </summary>
/// <param name="Right">The right's id: an inline right's own, or the named right's.</param>
/// <param name="Lifecycle">The id of the lifecycle whose rights list it.</param>
/// <param name="Access">Whether it allowed or denied.</param>
public sealed record RightEvaluation(string Right, string Lifecycle, RightAccess Access);

/// <summary>Where a guard's result came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GuardSource>))]
public enum GuardSource
{
    /// <summary>The guard's expression ran (<c>expression</c>).</summary>
    [JsonStringEnumMemberName("expression")] Expression,

    /// <summary>The step's <c>assume</c> gave it (<c>assumed</c>).</summary>
    [JsonStringEnumMemberName("assumed")] Assumed,

    /// <summary>The guard has no expression and the step no assume value (<c>missing</c>): MQ9306.</summary>
    [JsonStringEnumMemberName("missing")] Missing,
}

/// <summary>How an action ran.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionSource>))]
public enum ActionSource
{
    /// <summary>Its expression ran (<c>expression</c>).</summary>
    [JsonStringEnumMemberName("expression")] Expression,

    /// <summary>It has no expression: a named stub that changes nothing in the engine (<c>stub</c>).</summary>
    [JsonStringEnumMemberName("stub")] Stub,

    /// <summary>It has no expression but sets the object's data (<c>sets</c>; object-effects-design.md section 7).</summary>
    [JsonStringEnumMemberName("sets")] Sets,
}

/// <summary>The outcome an audit record states (phase-3-design.md section 2.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GateOutcome>))]
public enum GateOutcome
{
    /// <summary>A signature was counted (<c>signed</c>).</summary>
    [JsonStringEnumMemberName("signed")] Signed,

    /// <summary>The gate completed and its transition fired (<c>completed</c>).</summary>
    [JsonStringEnumMemberName("completed")] Completed,

    /// <summary>A signature attempt the gate did not count (<c>refused</c>).</summary>
    [JsonStringEnumMemberName("refused")] Refused,

    /// <summary>The source state was exited and the signatures were discarded (<c>discarded</c>).</summary>
    [JsonStringEnumMemberName("discarded")] Discarded,
}

/// <summary>One gate audit record (phase-3-design.md section 2.3).</summary>
/// <param name="Instance">The instance identity supplied by the host.</param>
/// <param name="Process">The process id.</param>
/// <param name="Gate">The gate id.</param>
/// <param name="Transition">The gated transition id.</param>
/// <param name="Sequence">The order within the instance.</param>
/// <param name="Signer">The signing person's identity.</param>
/// <param name="Actor">The actor the signer signs as.</param>
/// <param name="Meaning">The meaning id.</param>
/// <param name="Reason">The reason.</param>
/// <param name="At">The simulated clock (ISO 8601).</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Attributes">The audit attribute values by attribute id.</param>
public sealed record GateAuditRecord(
    string Instance, string Process, string Gate, string Transition, long Sequence, string? Signer, string? Actor, string? Meaning, string? Reason,
    string At, GateOutcome Outcome, IReadOnlyDictionary<string, JsonElement> Attributes);

/// <summary>An action run in a microstep.</summary>
/// <param name="Action">The action id.</param>
/// <param name="Source">Whether its expression ran or it is a stub.</param>
/// <param name="Changed">The context attributes it changed, by id.</param>
public sealed record ActionRun(string Action, ActionSource Source, IReadOnlyDictionary<string, JsonElement> Changed)
{
    /// <summary>
    /// The values the action's sets gave the object, by attribute id (object-effects-design.md decision 4): each worked out from the object
    /// and context as they were when the action started, then applied together. <see langword="null"/>, and left out of the JSON, when the
    /// action has no sets, or when they ran in an invoked sub-process, which sees no object.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Sets { get; init; }
}

/// <summary>One microstep: the transitions taken together, the states exited and entered, and the actions run.</summary>
/// <param name="Transitions">The transition ids.</param>
/// <param name="Exited">The exited state ids, in exit order.</param>
/// <param name="Entered">The entered state ids, in entry order.</param>
/// <param name="Actions">The actions, in run order (exit, transition, entry).</param>
public sealed record MicrostepTrace(IReadOnlyList<string> Transitions, IReadOnlyList<string> Exited, IReadOnlyList<string> Entered, IReadOnlyList<ActionRun> Actions);

/// <summary>One guard evaluation.</summary>
/// <param name="Guard">The guard id.</param>
/// <param name="Transition">The transition it guards.</param>
/// <param name="Result">The result; <see langword="null"/> when missing.</param>
/// <param name="Source">Where the result came from.</param>
public sealed record GuardEvaluation(string Guard, string Transition, bool? Result, GuardSource Source);

/// <summary>What one input did (phase-3-design.md section 4.4).</summary>
/// <param name="Index">The input's index; -1 for the initial entry.</param>
/// <param name="Input">The input, or <see langword="null"/> for the initial entry.</param>
/// <param name="Accepted">Whether the input was accepted.</param>
/// <param name="Refusal">Why it was refused (<see cref="Refusals"/>).</param>
/// <param name="Microsteps">The microsteps, in order.</param>
/// <param name="Guards">The guard evaluations, in order.</param>
/// <param name="Audit">The gate audit records, in order.</param>
/// <param name="Configuration">The active atomic states after the input, in document order.</param>
/// <param name="Context">The context after the input, by attribute id.</param>
/// <param name="Changed">The attributes whose values the input changed, by id.</param>
/// <param name="Clock">The simulated clock after the input (ISO 8601).</param>
/// <param name="Final">Whether the root is final.</param>
/// <param name="Diagnostics">MQ9305, MQ9306 and MQ9502 to MQ9507 findings.</param>
public sealed record StepTrace(
    int Index, ScenarioStep? Input, bool Accepted, string? Refusal, IReadOnlyList<MicrostepTrace> Microsteps, IReadOnlyList<GuardEvaluation> Guards,
    IReadOnlyList<GateAuditRecord> Audit, IReadOnlyList<string> Configuration, IReadOnlyDictionary<string, JsonElement> Context,
    IReadOnlyDictionary<string, JsonElement> Changed, string Clock, bool Final, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>
    /// The system actor when it made the move (actors-design.md decision 2): for the initial entry, a time step or a finished invoke that
    /// is not a human task, whose input names no actor, the project's system actor when it has exactly one. Otherwise <see langword="null"/>
    /// and the member is left out of the JSON; a step's own actor is in its <see cref="Input"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actor { get; init; }

    /// <summary>
    /// The actor a step with a participant acted as (actors-design.md decision 15), worked out from the moves the step could make when it
    /// ran; the <see cref="Input"/> echoes it as its actor, with the signer it signed as. <see langword="null"/>, and left out of the
    /// JSON, for a step that names no participant, and for one whose participant satisfies none of the actors it could act as.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActsAs { get; init; }

    /// <summary>
    /// The object's attributes the input's actions set to a new value, by attribute id (object-effects-design.md decision 4), as
    /// <see cref="Changed"/> is for the context; a step's <c>expect.values</c> is compared with it (MQ9307). <see langword="null"/>, and left
    /// out of the JSON, when the input set nothing.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? ValuesChanged { get; init; }

    /// <summary>
    /// What the run assumed or could not do, in the order it happened (object-effects-design.md section 7): a state assumed for an object's
    /// lifecycle, an identity that did not convert to the type <c>current_actor()</c> met, sets an invoked sub-process ran. <see langword="null"/>,
    /// and left out of the JSON, when there is none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>
    /// The id of the element that refused the input (rights-design.md decision 12): the event or invoke whose actors leave the actor out, the
    /// right (an inline right, or the named right), the lifecycle whose default denies, the guard, or the gate or gate policy. Written for a
    /// refused input in a project that uses rights, and for a step that expects it; <see langword="null"/>, and left out of the JSON,
    /// otherwise, and when nothing refused it (<see cref="Refusals.NoTransition"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefusedBy { get; init; }

    /// <summary>
    /// The rights that answered for the input, each with its access, in check order (rights-design.md decision 26). <see langword="null"/>,
    /// and left out of the JSON, when none did.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RightEvaluation>? Rights { get; init; }

    /// <summary>
    /// Whether the subject is frozen after the input (rights-design.md section 7): <see langword="true"/> from an accepted <c>freeze</c>, or a
    /// start with the subject <c>frozen</c>, until an accepted <c>thaw</c>. Left out of the JSON when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Frozen { get; init; }

    /// <summary>Whether a guard's result was missing (MQ9306): what followed in this input is not meaningful.</summary>
    public bool Incomplete => Guards.Any(g => g.Source == GuardSource.Missing) || Diagnostics.Any(d => d.Rule is "MQ9305" or "MQ9507");
}
