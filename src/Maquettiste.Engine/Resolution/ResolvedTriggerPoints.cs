using System.Collections.Frozen;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolved trigger point (trigger-points-design.md section 7): an entry of a lifecycle's <c>triggerPoints</c> (kind
/// <c>process-trigger-point</c>), what runs when one of its verbs happens, in the places its optional keys name. A check has a
/// <see cref="Condition"/>, an action or a replace has <see cref="Sets"/>; with neither, the body is the team's code (<see cref="TeamCode"/>).
/// </summary>
public sealed class RTriggerPoint : RObject
{
    /// <inheritdoc/>
    public override string Kind => "process-trigger-point";

    /// <summary>The name, unique among the lifecycle's trigger points and apart from its process's guards.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The display name, falling back to the name.</summary>
    public string DisplayName { get; internal set; } = "";

    /// <summary>Why the trigger point is there; <see langword="null"/> when there is none.</summary>
    public string? Description { get; internal set; }

    /// <summary>
    /// What a refusal or a warning shows (decision 1): the description, else the display name, else the name.
    /// </summary>
    public string Message { get; internal set; } = "";

    /// <summary><c>check</c>, <c>action</c> or <c>replace</c>.</summary>
    public string Type { get; internal set; } = "";

    /// <summary>An action's moment, <c>before</c>, <c>with-change</c> or <c>after-commit</c>; <see langword="null"/> for a check or a replace.</summary>
    public string? Timing { get; internal set; }

    /// <summary>A check's severity, <c>error</c> (also when its file gives none) or <c>warning</c>; <see langword="null"/> for an action or a replace.</summary>
    public string? Severity { get; internal set; }

    /// <summary>The verbs it runs on, in file order: built-in verbs or the project's own. A key that names no verb is left out.</summary>
    public RList<RVerb> Verbs { get; internal set; } = RList<RVerb>.Empty;

    /// <summary>The states of the lifecycle the object must be in (for a move, its source), in file order; empty when not narrowed.</summary>
    public RList<RState> States { get; internal set; } = RList<RState>.Empty;

    /// <summary>The states of the lifecycle a move must enter, in file order; empty when not narrowed.</summary>
    public RList<RState> Targets { get; internal set; } = RList<RState>.Empty;

    /// <summary>The transitions of the lifecycle a move must be, or for <c>sign</c> whose gates are signed, in file order; empty when not narrowed.</summary>
    public RList<RTransition> Transitions { get; internal set; } = RList<RTransition>.Empty;

    /// <summary>
    /// The entities it is narrowed to, as written, in file order; empty when not narrowed. <see cref="Covers"/> lists every entity it runs for.
    /// </summary>
    public RList<REntity> Entities { get; internal set; } = RList<REntity>.Empty;

    /// <summary>The attributes a <c>read</c> or <c>modify</c> step must read or change itself, in file order; empty when not narrowed.</summary>
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The relation ends of the subject whose links it speaks of, in file order; empty when it speaks of the object.</summary>
    public RList<REnd> Ends { get; internal set; } = RList<REnd>.Empty;

    /// <summary>
    /// Its <c>when</c>, typed at a trigger point's site, its paths ending at the declaring entity's attributes; <see langword="null"/> when it has
    /// none, or when it does not type (validation says why).
    /// </summary>
    public RExpression? When { get; internal set; }

    /// <summary>
    /// A check's condition, typed at a trigger point's site, its paths ending at the declaring entity's attributes; <see langword="null"/> when
    /// the team writes the check, or when it does not type (validation says why).
    /// </summary>
    public RExpression? Condition { get; internal set; }

    /// <summary>
    /// What an action or a replace sets on the object, in file order, each value typed at a trigger point's set site; empty when the team writes
    /// the body. A set whose attribute does not resolve or whose value does not type is left out (validation says why).
    /// </summary>
    public RList<RSet> Sets { get; internal set; } = RList<RSet>.Empty;

    /// <summary>Whether the team's code reaches outside systems.</summary>
    public bool Outside { get; internal set; }

    /// <summary>Whether the body is the team's code: a check with no condition, or an action or a replace with no sets.</summary>
    public bool TeamCode { get; internal set; }

    /// <summary>
    /// The entities it runs for (decision 12), by name then id: every entity the lifecycle governs (its subjects, their derived entities and a
    /// stereotype's carriers), narrowed by <see cref="Entities"/> to those derived from one.
    /// </summary>
    public RList<REntity> Covers { get; internal set; } = RList<REntity>.Empty;

    /// <summary>The lifecycle whose trigger points list it.</summary>
    public RProcess? Process { get; internal set; }
}

/// <summary>
/// One verb's trigger points in one lifecycle, by moment, each in run order (trigger-points-design.md section 7): what the lifecycle runs
/// when the verb happens, whatever the places. Other lifecycles join at run time.
/// </summary>
public sealed class RTriggerVerb : IResolvedObject
{
    /// <summary>The synthesized id, <c>&lt;process id&gt;/trigger-verb/&lt;verb key&gt;</c>: an entry is no element of the model.</summary>
    public string Id { get; internal set; } = "";

    /// <inheritdoc/>
    public string Kind => "trigger-verb";

    /// <summary>The keys of the process's <see cref="RProcess.TriggerVerbs"/> list, which decide the entry too.</summary>
    public IReadOnlyList<string> Dependencies { get; internal set; } = [];

    /// <summary>The verb; <see langword="null"/> when its key names no verb.</summary>
    public RVerb? Verb { get; internal set; }

    /// <summary>The before actions, in list order.</summary>
    public RList<RTriggerPoint> Before { get; internal set; } = RList<RTriggerPoint>.Empty;

    /// <summary>The checks, in list order.</summary>
    public RList<RTriggerPoint> Checks { get; internal set; } = RList<RTriggerPoint>.Empty;

    /// <summary>The replaces, on a verb that allows one, in list order; at most one is valid.</summary>
    public RList<RTriggerPoint> Replace { get; internal set; } = RList<RTriggerPoint>.Empty;

    /// <summary>The with-the-change actions, in list order.</summary>
    public RList<RTriggerPoint> WithChange { get; internal set; } = RList<RTriggerPoint>.Empty;

    /// <summary>The after-commit actions, in list order.</summary>
    public RList<RTriggerPoint> AfterCommit { get; internal set; } = RList<RTriggerPoint>.Empty;
}

/// <summary>
/// What one trigger point did in a scenario step, as the engine's replay ran it (trigger-points-design.md decision 28), for templates that
/// assert the trace.
/// </summary>
public sealed class RTriggerPointRun
{
    /// <summary>The trigger point.</summary>
    public RTriggerPoint TriggerPoint { get; internal set; } = null!;

    /// <summary>The lifecycle whose trigger points list it.</summary>
    public RProcess? Lifecycle { get; internal set; }

    /// <summary>The verb it ran on; <see langword="null"/> when its key names no verb.</summary>
    public RVerb? Verb { get; internal set; }

    /// <summary><c>check</c>, <c>action</c> or <c>replace</c>.</summary>
    public string Type { get; internal set; } = "";

    /// <summary>An action's moment; <see langword="null"/> for a check or a replace.</summary>
    public string? Timing { get; internal set; }

    /// <summary><c>passed</c>, <c>warned</c>, <c>refused</c>, <c>ran</c>, <c>replaced</c>, <c>queued</c> or <c>skipped</c>.</summary>
    public string Outcome { get; internal set; } = "";

    /// <summary>Whether a team-code check's outcome came from the step's <c>assume</c>, or from its default, pass.</summary>
    public bool Assumed { get; internal set; }

    /// <summary>Whether the trigger point says the team's code reaches outside systems.</summary>
    public bool Outside { get; internal set; }

    /// <summary>The values its sets gave, by attribute name (by id when the attribute does not resolve), as plain values; empty when it set nothing.</summary>
    public IReadOnlyDictionary<string, object?> Sets { get; internal set; } = FrozenDictionary<string, object?>.Empty;
}
