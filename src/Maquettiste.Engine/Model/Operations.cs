using System.Collections.Frozen;
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

    /// <summary>The operation's own errors, in file order. The stereotypes of its effective set contribute more (<see cref="OperationErrors"/>).</summary>
    public IReadOnlyList<OperationError> Errors { get; init; } = [];

    /// <summary>
    /// The guards, checked in order (next-phase-3-design.md section 3.1): the first that does not hold reports its error. Only true
    /// passes.
    /// </summary>
    public IReadOnlyList<OperationGuard> Guards { get; init; } = [];

    /// <summary>
    /// Per-operation exceptions to the errors the operation inherits from its stereotypes (next-phase-2-design.md section 3.1): each
    /// entry names a stereotype error and states only the facets it changes (<c>input</c>, <c>displayName</c>, <c>description</c>).
    /// </summary>
    public IReadOnlyList<OperationErrorRedefinition> Redefines { get; init; } = [];

    /// <summary>Ids of stereotype errors the operation does not inherit.</summary>
    [ElementRef(IndexKinds = ["error"])]
    public IReadOnlyList<string> Excludes { get; init; } = [];

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
/// typed value (<see cref="Type"/>), a record key (<see cref="KeyOf"/>) or a file (<see cref="File"/>) (MQ3024).
/// </summary>
public sealed record OperationMember : ElementBase
{
    /// <summary>A typed value's type, as an attribute takes it; <see langword="null"/> for a record key.</summary>
    public TypeRef? Type { get; init; }

    /// <summary>A record key's entity: the member holds the key of one record of it, and with <see cref="Collection"/> a list of keys.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? KeyOf { get; init; }

    /// <summary>Whether the member is a file: bytes with a name and a media type.</summary>
    public bool File { get; init; }

    /// <summary>The media types a file member accepts, in file order; empty accepts any. Only on a file (MQ3024).</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = [];

    /// <summary>The maximum length of a typed value, or a file's size cap in bytes.</summary>
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
/// What an outcome returns: exactly one of an inline shape (<see cref="Members"/>), records of an entity (<see cref="Entity"/>) or a
/// file (<see cref="File"/>, a download) (MQ3025). Part of its outcome, without an id.
/// </summary>
public sealed record OperationReturns
{
    /// <summary>The inline shape's members, at least one; <see langword="null"/> when the returns is not of this form.</summary>
    public IReadOnlyList<OperationMember>? Members { get; init; }

    /// <summary>The entity whose records are returned, its attributes as the shape; <see langword="null"/> when not of this form.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>Whether a file is returned (a download; streaming is a file).</summary>
    public bool File { get; init; }

    /// <summary>The media types a file result may have, in file order, the first the usual one. Only beside <see cref="File"/> (MQ3025).</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = [];

    /// <summary>Whether a list is returned; never beside <see cref="File"/> (MQ3025).</summary>
    public bool List { get; init; }

    /// <summary>How a list is paged, or <see langword="null"/> for an unpaged list; set with <see cref="MaxPageSize"/> (MQ3025). The paging
    /// inputs (a page or a cursor, and a page size) are implied, never declared as members.</summary>
    public OperationPaging? Paging { get; init; }

    /// <summary>The largest page a caller may ask for; set with <see cref="Paging"/> (MQ3025).</summary>
    public int? MaxPageSize { get; init; }
}

/// <summary>How a returned list is paged.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationPaging>))]
public enum OperationPaging
{
    /// <summary>By page number and page size (<c>page</c>).</summary>
    [JsonStringEnumMemberName("page")] Page,

    /// <summary>By an opaque cursor the previous page returned, and a page size (<c>cursor</c>).</summary>
    [JsonStringEnumMemberName("cursor")] Cursor,
}

/// <summary>An error an operation can report (index kind <c>error</c>), its own or one a stereotype contributes (<see cref="Stereotype.Errors"/>).</summary>
public sealed record OperationError : ElementBase
{
    /// <summary>The stable text clients see; <see langword="null"/> means the name.</summary>
    public string? Code { get; init; }

    /// <summary>The input member the error points at, one of the operation's own (MQ3027), or <see langword="null"/>; a stereotype's
    /// error has none.</summary>
    [ElementRef(IndexKinds = ["operation-member"])]
    public string? Input { get; init; }
}

/// <summary>A condition an operation requires before it runs (index kind <c>guard</c>), and the error it raises when the condition fails.</summary>
public sealed record OperationGuard : ElementBase
{
    /// <summary>The condition, a boolean expression over the input members and, for a record operation, the subject.</summary>
    public required Expr Condition { get; init; }

    /// <summary>The error the guard raises: an own error's id or an inherited one's keyed id (MQ3111).</summary>
    [ElementRef(Keyed = true)]
    public string? Error { get; init; }
}

/// <summary>
/// One entry of an operation's <c>redefines</c> list (next-phase-2-design.md sections 3.1 and 4.1): the stereotype error it redefines
/// and the facets it states, each <see langword="null"/> when not stated. <c>name</c> and <c>code</c> are not redefinable, so a shared
/// code stays shared.
/// </summary>
public sealed record OperationErrorRedefinition : IMemberRedefinition
{
    /// <summary>The id of the stereotype error the entry redefines.</summary>
    [ElementRef(IndexKinds = ["error"])]
    public required string Member { get; init; }

    /// <summary>The input member of the operation the error points at (MQ3027).</summary>
    [ElementRef(IndexKinds = ["operation-member"])]
    public string? Input { get; init; }

    /// <summary>The display name for this operation; it shows in every locale.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The description for this operation; it shows in every locale.</summary>
    public Description? Description { get; init; }

    /// <summary>The JSON names of the facets the entry states (the non-null ones), in ordinal order; never <c>member</c>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Facets
    {
        get
        {
            var facets = new List<string>(3);
            if (Description is not null) facets.Add("description");
            if (DisplayName is not null) facets.Add("displayName");
            if (Input is not null) facets.Add("input");
            return facets;
        }
    }
}

/// <summary>
/// The errors an operation inherits from the stereotypes of its effective set (next-phase-2-design.md section 4.1, through section
/// 3.1's <see cref="ContributedMembers"/>): each keyed <c>&lt;operationId&gt;.&lt;errorId&gt;</c>, less what it excludes, with the
/// facets its redefinitions state. Pure functions of the snapshot.
/// </summary>
internal static class OperationErrors
{
    /// <summary>The inherited errors, in effective-set order, then each stereotype's file order.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="operation">The operation.</param>
    /// <returns>The inherited errors.</returns>
    public static IReadOnlyList<InheritedMember<OperationError>> Of(ModelSnapshot model, Operation operation)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(operation);
        var effective = model.EffectiveStereotypes(operation);
        return HasErrors(effective)
            ? ContributedMembers.Inherit(operation.Id, effective, s => s.Errors, e => e.Id, operation.Redefines, operation.Excludes,
                FrozenSet<string>.Empty, Apply)
            : [];
    }

    /// <summary>Every error the operation inherits before its excludes, by the stereotype error's id (the first stereotype's when two
    /// share one).</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="operation">The operation.</param>
    /// <returns>The inheritable errors with the entry that contributes each.</returns>
    public static IReadOnlyDictionary<string, (OperationError Error, AppliedStereotype From)> Inheritable(ModelSnapshot model, Operation operation)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(operation);
        var result = new Dictionary<string, (OperationError, AppliedStereotype)>(StringComparer.Ordinal);
        foreach (var applied in model.EffectiveStereotypes(operation))
        {
            foreach (var error in applied.Stereotype.Errors)
            {
                if (error.Id.Length > 0)
                    result.TryAdd(error.Id, (error, applied));
            }
        }

        return result;
    }

    /// <summary>A stereotype error with a redefinition's stated facets applied.</summary>
    internal static OperationError Apply(OperationError error, OperationErrorRedefinition redefinition) => error with
    {
        Input = redefinition.Input ?? error.Input,
        DisplayName = redefinition.DisplayName ?? error.DisplayName,
        Description = redefinition.Description ?? error.Description,
    };

    private static bool HasErrors(IReadOnlyList<AppliedStereotype> effective)
    {
        foreach (var applied in effective)
        {
            if (applied.Stereotype.Errors.Count > 0)
                return true;
        }

        return false;
    }
}
