using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>What a trigger point is (trigger-points-design.md decision 5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerPointType>))]
public enum TriggerPointType
{
    /// <summary>It judges the change: it passes, warns or refuses with its message (<c>check</c>).</summary>
    [JsonStringEnumMemberName("check")] Check,

    /// <summary>It does something at a moment of the verb, its <see cref="ProcessTriggerPoint.Timing"/> (<c>action</c>).</summary>
    [JsonStringEnumMemberName("action")] Action,

    /// <summary>It takes the place of the verb's built-in effect, on the few verbs that allow it (<c>replace</c>, decisions 16 and 17).</summary>
    [JsonStringEnumMemberName("replace")] Replace,
}

/// <summary>When an action runs (trigger-points-design.md decision 7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerTiming>))]
public enum TriggerTiming
{
    /// <summary>Before the checks: it fills the proposed values, and nothing is written until the checks pass (<c>before</c>).</summary>
    [JsonStringEnumMemberName("before")] Before,

    /// <summary>After the effect, in the same unit of work: if it fails, the verb is undone (<c>with-change</c>).</summary>
    [JsonStringEnumMemberName("with-change")] WithChange,

    /// <summary>Once the change is saved: it cannot undo it, may run late and may run more than once (<c>after-commit</c>).</summary>
    [JsonStringEnumMemberName("after-commit")] AfterCommit,
}

/// <summary>What a check does when it fails (trigger-points-design.md decision 6).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerSeverity>))]
public enum TriggerSeverity
{
    /// <summary>It refuses the step, with refusal <c>check</c> (<c>error</c>, the default).</summary>
    [JsonStringEnumMemberName("error")] Error,

    /// <summary>It lets the step through and lists itself in the trace's check warnings (<c>warning</c>).</summary>
    [JsonStringEnumMemberName("warning")] Warning,
}

/// <summary>
/// An entry of a lifecycle's <see cref="Process.TriggerPoints"/> (index kind <c>process-trigger-point</c>, trigger-points-design.md decisions 1
/// to 3 and 10): what runs when one of its verbs happens, in the places its optional keys name. A check has a <see cref="Condition"/>, an action
/// or a replace has <see cref="Sets"/>; with neither, the body is the team's code. Lifecycles only (MQ8101).
/// </summary>
public sealed record ProcessTriggerPoint : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>An identifier unique among the lifecycle's trigger points, and apart from its process's guards (MQ3001).</summary>
    public required string Name { get; init; }

    /// <summary>An optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether it is a check, an action or a replace.</summary>
    public required TriggerPointType Type { get; init; }

    /// <summary>An action's moment; required on an action and allowed nowhere else (MQ8101).</summary>
    public TriggerTiming? Timing { get; init; }

    /// <summary>A check's severity, <see cref="TriggerSeverity.Error"/> when <see langword="null"/>; allowed only on a check (MQ8101).</summary>
    public TriggerSeverity? Severity { get; init; }

    /// <summary>The verbs it runs on, by key, at least one: built-in verbs or the project's own (MQ9701).</summary>
    [VerbKey]
    public required IReadOnlyList<string> Verbs { get; init; }

    /// <summary>The states of this lifecycle the object must be in (for a move, its source); empty for every state.</summary>
    [ElementRef(IndexKinds = ["state"])]
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>The states of this lifecycle a move must enter; empty for any (MQ8102 beside other verbs).</summary>
    [ElementRef(IndexKinds = ["state"])]
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>The transitions of this lifecycle a move must be, or for <c>sign</c> whose gates are signed; empty for any.</summary>
    [ElementRef(IndexKinds = ["transition"])]
    public IReadOnlyList<string> Transitions { get; init; } = [];

    /// <summary>
    /// The entities it runs for, each with its derived entities: some of those the lifecycle governs (MQ8102); empty for all. It also widens
    /// what its paths and sets may name to what every entity it covers holds under one id (decision 12).
    /// </summary>
    [ElementRef(ElementKind.Entity)]
    public IReadOnlyList<string> Entities { get; init; } = [];

    /// <summary>The attributes a <c>read</c> or <c>modify</c> step must read or change itself; empty for any.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>The relation ends of the subject whose links it speaks of; empty for the object itself.</summary>
    [ElementRef(IndexKinds = ["end"])]
    public IReadOnlyList<string> Ends { get; init; } = [];

    /// <summary>It runs only when this is true; false or unknown skips it. <see langword="null"/> for always.</summary>
    public Expr? When { get; init; }

    /// <summary>A check's condition: only true passes. <see langword="null"/> when the team writes the check (MQ8101 on another type).</summary>
    public Expr? Condition { get; init; }

    /// <summary>
    /// What an action or a replace sets on the object, as an action's sets (MQ9802, MQ9803); empty when the team writes the body. A check sets
    /// nothing, and neither does an after-commit action (MQ8101, MQ8105).
    /// </summary>
    public IReadOnlyList<ProcessSet> Sets { get; init; } = [];

    /// <summary>Whether the team's code reaches outside systems (decision 22); only a point with team code may say so (MQ8101).</summary>
    public bool Outside { get; init; }

    /// <summary>
    /// Why (localizable): for a check, the message shown when it refuses or warns. Markdown text, or a reference to a sidecar Markdown file.
    /// </summary>
    public Description? Description { get; init; }

    /// <summary>Whether the body is the team's code: a check with no condition, or an action or a replace with no sets.</summary>
    [JsonIgnore]
    public bool TeamCode => Type == TriggerPointType.Check ? Condition is null : Sets.Count == 0;
}

/// <summary>A word a scenario step's <c>assume</c> gives for a check the team writes (trigger-points-design.md decision 26).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AssumedCheck>))]
public enum AssumedCheck
{
    /// <summary>The check passes (<c>pass</c>).</summary>
    [JsonStringEnumMemberName("pass")] Pass,

    /// <summary>The check warns and lets the step through (<c>warning</c>).</summary>
    [JsonStringEnumMemberName("warning")] Warning,

    /// <summary>The check refuses the step (<c>error</c>).</summary>
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>
/// One entry of a scenario step's <c>assume</c> (trigger-points-design.md decision 26): a boolean for a guard with no expression, as before,
/// or a word for a check with no condition. In JSON it is the bare boolean or the word.
/// </summary>
[JsonConverter(typeof(StepAssumptionConverter))]
public readonly record struct StepAssumption
{
    private readonly bool _value;

    private StepAssumption(bool value, AssumedCheck? check)
    {
        _value = value;
        Check = check;
    }

    /// <summary>A boolean entry's value, for a guard; <see langword="null"/> for a word.</summary>
    public bool? Guard => Check is null ? _value : null;

    /// <summary>A word entry's outcome, for a check; <see langword="null"/> for a boolean.</summary>
    public AssumedCheck? Check { get; }

    /// <summary>A boolean entry, for a guard.</summary>
    /// <param name="value">The guard's result.</param>
    public static implicit operator StepAssumption(bool value) => new(value, null);

    /// <summary>A word entry, for a check.</summary>
    /// <param name="check">The check's outcome.</param>
    public static implicit operator StepAssumption(AssumedCheck check) => new(false, check);

    /// <summary>A boolean entry, for a guard.</summary>
    /// <param name="value">The guard's result.</param>
    /// <returns>The entry.</returns>
    public static StepAssumption FromBoolean(bool value) => value;

    /// <summary>A word entry, for a check.</summary>
    /// <param name="check">The check's outcome.</param>
    /// <returns>The entry.</returns>
    public static StepAssumption FromAssumedCheck(AssumedCheck check) => check;

    /// <summary>The word of a check outcome, as files write it.</summary>
    /// <param name="check">The outcome.</param>
    /// <returns><c>pass</c>, <c>warning</c> or <c>error</c>.</returns>
    public static string Word(AssumedCheck check) => check switch
    {
        AssumedCheck.Pass => "pass",
        AssumedCheck.Warning => "warning",
        _ => "error",
    };

    /// <summary>The value as a plain object: the boolean, or the word.</summary>
    /// <returns>A <see cref="bool"/> or a <see cref="string"/>.</returns>
    public object ToPlain() => Check is { } check ? Word(check) : _value;
}

/// <summary>Reads and writes a <see cref="StepAssumption"/> as a bare boolean or one of the words <c>pass</c>, <c>warning</c> and <c>error</c>.</summary>
public sealed class StepAssumptionConverter : JsonConverter<StepAssumption>
{
    /// <inheritdoc/>
    public override StepAssumption Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.String => reader.GetString() switch
        {
            "pass" => AssumedCheck.Pass,
            "warning" => AssumedCheck.Warning,
            "error" => AssumedCheck.Error,
            var other => throw new JsonException($"'{other}' is not an assumption: give true or false for a guard, or pass, warning or error for a check."),
        },
        _ => throw new JsonException("An assumption is true or false for a guard, or pass, warning or error for a check."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, StepAssumption value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value.Check is { } check)
            writer.WriteStringValue(StepAssumption.Word(check));
        else
            writer.WriteBooleanValue(value.Guard!.Value);
    }
}
