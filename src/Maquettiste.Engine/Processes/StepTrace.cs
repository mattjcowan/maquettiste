using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>Why an input was refused (phase-3-design.md section 4.1).</summary>
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
}

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
public sealed record ActionRun(string Action, ActionSource Source, IReadOnlyDictionary<string, JsonElement> Changed);

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
    /// <summary>Whether a guard's result was missing (MQ9306): what followed in this input is not meaningful.</summary>
    public bool Incomplete => Guards.Any(g => g.Source == GuardSource.Missing) || Diagnostics.Any(d => d.Rule is "MQ9305" or "MQ9507");
}
