using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// One command over the model (next-phase-2-design.md section 4.1; <c>model/operations/</c>, in a sub-folder named after the subject's
/// file stem when it has one): what it acts on, whether it changes data, its input, its outcomes, its errors and the process events it
/// fires. The shape names no protocol: each pack maps it to its own transport. No database resolves an operation, so a write of one
/// is never a database change.
/// </summary>
public sealed record Operation : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Operation;

    /// <summary>The id of the owning package, or <see langword="null"/>; the resolved package falls back to the subject's.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>The entity the operation acts on, or <see langword="null"/>. Not owning: deleting the entity is refused while an
    /// operation names it.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Subject { get; init; }

    /// <summary>Whether the operation addresses one record of the subject or the whole set; meaningful only with a subject (MQ3023).</summary>
    public OperationLevel Level { get; init; } = OperationLevel.Record;

    /// <summary>Whether the operation only reads data or changes it.</summary>
    public required OperationEffect Effect { get; init; }

    /// <summary>The input members, in file order. A record-level operation takes the subject record's key implicitly.</summary>
    public IReadOnlyList<OperationMember> Input { get; init; } = [];

    /// <summary>The outcomes, in file order; at least one.</summary>
    public required IReadOnlyList<OperationOutcome> Outcomes { get; init; }

    /// <summary>The default outcome, one of <see cref="Outcomes"/> (MQ3028); <see langword="null"/> means the first.</summary>
    [ElementRef(IndexKinds = ["outcome"])]
    public string? DefaultOutcome { get; init; }

    /// <summary>The operation's own errors, in file order.</summary>
    public IReadOnlyList<OperationError> Errors { get; init; } = [];

    /// <summary>The process events the operation fires, of any process, in file order.</summary>
    [ElementRef(IndexKinds = ["event"])]
    public IReadOnlyList<string> Fires { get; init; } = [];

    /// <summary>Ids of any element or sub-element that stand for what the operation requires (an authorization concept); the engine
    /// gives the list no meaning.</summary>
    [ElementRef]
    public IReadOnlyList<string> Requires { get; init; } = [];
}

/// <summary>What an operation addresses when it has a subject.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationLevel>))]
public enum OperationLevel
{
    /// <summary>One record of the subject, whose key the operation takes implicitly (<c>record</c>).</summary>
    [JsonStringEnumMemberName("record")] Record,

    /// <summary>The whole set of the subject's records: a search, a create, a bulk change (<c>collection</c>).</summary>
    [JsonStringEnumMemberName("collection")] Collection,
}

/// <summary>Whether an operation changes data.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationEffect>))]
public enum OperationEffect
{
    /// <summary>It only reads (<c>read</c>).</summary>
    [JsonStringEnumMemberName("read")] Read,

    /// <summary>It changes data (<c>change</c>).</summary>
    [JsonStringEnumMemberName("change")] Change,
}

/// <summary>
/// A member of an operation's input or of an outcome's inline <c>returns</c> (index kind <c>operation-member</c>): exactly one of a
/// typed value (<see cref="Type"/>) or a record key (<see cref="KeyOf"/>) (MQ3024).
/// </summary>
public sealed record OperationMember : ElementBase
{
    /// <summary>A typed value's type, as an attribute takes it; <see langword="null"/> for a record key.</summary>
    public TypeRef? Type { get; init; }

    /// <summary>A record key's entity: the member holds the key of one record of it, and with <see cref="Collection"/> a list of keys.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? KeyOf { get; init; }

    /// <summary>The maximum length of a typed value.</summary>
    public int? Length { get; init; }

    /// <summary>The precision of a typed decimal value.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale of a typed decimal value.</summary>
    public int? Scale { get; init; }

    /// <summary>Whether a value must be given.</summary>
    public bool Required { get; init; }

    /// <summary>The value a typed input member takes when none is given; never on a member of a <c>returns</c>.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>Whether the member holds a list of values (or of keys).</summary>
    public bool Collection { get; init; }

    /// <summary>Whether the value is personal data or a secret.</summary>
    public Sensitivity? Sensitive { get; init; }

    /// <summary>Validation constraints of a typed value.</summary>
    public AttributeValidation? Validation { get; init; }
}

/// <summary>One way an operation ends (index kind <c>outcome</c>).</summary>
public sealed record OperationOutcome : ElementBase
{
    /// <summary>Whether the operation completes now or is accepted and completes later.</summary>
    public OperationCompletion Completes { get; init; } = OperationCompletion.Now;

    /// <summary>What the outcome returns, or <see langword="null"/> for nothing.</summary>
    public OperationReturns? Returns { get; init; }
}

/// <summary>When an outcome completes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationCompletion>))]
public enum OperationCompletion
{
    /// <summary>The work is done when the outcome is reported (<c>now</c>).</summary>
    [JsonStringEnumMemberName("now")] Now,

    /// <summary>The work was accepted and completes later (<c>later</c>).</summary>
    [JsonStringEnumMemberName("later")] Later,
}

/// <summary>
/// What an outcome returns: exactly one of an inline shape (<see cref="Members"/>) or records of an entity (<see cref="Entity"/>)
/// (MQ3025). Part of its outcome, without an id.
/// </summary>
public sealed record OperationReturns
{
    /// <summary>The inline shape's members, at least one; <see langword="null"/> when the returns is not of this form.</summary>
    public IReadOnlyList<OperationMember>? Members { get; init; }

    /// <summary>The entity whose records are returned, its attributes as the shape; <see langword="null"/> when not of this form.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>Whether a list is returned.</summary>
    public bool List { get; init; }
}

/// <summary>An error an operation can report (index kind <c>error</c>).</summary>
public sealed record OperationError : ElementBase
{
    /// <summary>The stable text clients see; <see langword="null"/> means the name.</summary>
    public string? Code { get; init; }

    /// <summary>The input member the error points at, one of the operation's own (MQ3027), or <see langword="null"/>.</summary>
    [ElementRef(IndexKinds = ["operation-member"])]
    public string? Input { get; init; }
}
