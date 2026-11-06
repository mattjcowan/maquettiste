namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolved operation (next-phase-2-design.md section 4.1): one command over the model, with its subject, effect, input, outcomes,
/// errors, the process events it fires and what it requires. Its package is its file's, else its subject's.
/// </summary>
public sealed class ROperation : RElement
{
    /// <inheritdoc/>
    public override string Kind => "operation";

    /// <summary>The entity the operation acts on, or <see langword="null"/>.</summary>
    public REntity? Subject { get; internal set; }

    /// <summary><c>record</c> or <c>collection</c>; <see langword="null"/> without a subject.</summary>
    public string? Level { get; internal set; }

    /// <summary><c>read</c> or <c>change</c>.</summary>
    public string Effect { get; internal set; } = "read";

    /// <summary>The verb the operation names (verbs-design.md decision 13), or <see langword="null"/> when it names none or the key names
    /// no verb. Nothing ties it to <see cref="Effect"/>.</summary>
    public RVerb? Verb { get; internal set; }

    /// <summary>The input members, in file order. A record-level operation takes the subject record's key besides them.</summary>
    public RList<ROperationMember> Input { get; internal set; } = RList<ROperationMember>.Empty;

    /// <summary>The outcomes, in file order.</summary>
    public RList<ROperationOutcome> Outcomes { get; internal set; } = RList<ROperationOutcome>.Empty;

    /// <summary>The default outcome: the one <c>defaultOutcome</c> names, else the first.</summary>
    public ROperationOutcome? DefaultOutcome { get; internal set; }

    /// <summary>The errors, in file order (the first cut resolves own errors only).</summary>
    public RList<ROperationError> Errors { get; internal set; } = RList<ROperationError>.Empty;

    /// <summary>
    /// The guards, in file order (next-phase-3-design.md section 3.1, "Operation guards"): checked in that order before the operation
    /// runs, the first whose condition does not hold reporting its error. Only true passes, so a null operand fails a guard.
    /// </summary>
    public RList<ROperationGuard> Guards { get; internal set; } = RList<ROperationGuard>.Empty;

    /// <summary>The process events the operation fires, in file order; ids that name no event are skipped.</summary>
    public RList<REvent> Fires { get; internal set; } = RList<REvent>.Empty;

    /// <summary>
    /// The elements and sub-elements the operation requires, in file order; ids that resolve to nothing are skipped. Linked after every
    /// other pass, so an id of any resolved kind (a stereotype definition included) resolves.
    /// </summary>
    public RList<IResolvedObject> Requires { get; internal set; } = RList<IResolvedObject>.Empty;
}

/// <summary>A resolved member of an operation's input or of an outcome's inline returns.</summary>
public sealed class ROperationMember : RElement
{
    /// <inheritdoc/>
    public override string Kind => "operation-member";

    /// <summary>The operation that holds the member.</summary>
    public ROperation Operation { get; internal set; } = null!;

    /// <summary>The outcome whose returns holds the member; <see langword="null"/> for an input member.</summary>
    public ROperationOutcome? Outcome { get; internal set; }

    /// <summary>A typed value's type, or a record key's: the single key attribute's type of <see cref="KeyOf"/> (<see langword="null"/>
    /// when its key has several attributes or none).</summary>
    public RType? Type { get; internal set; }

    /// <summary>The entity whose record key the member holds, or <see langword="null"/> for a typed value.</summary>
    public REntity? KeyOf { get; internal set; }

    /// <summary>Whether the member is a file: bytes with a name and a media type; <see cref="Length"/> caps its size in bytes.</summary>
    public bool IsFile { get; internal set; }

    /// <summary>The media types a file member accepts, in file order; empty accepts any.</summary>
    public IReadOnlyList<string> MediaTypes { get; internal set; } = [];

    /// <summary>Whether a value must be given.</summary>
    public bool Required { get; internal set; }

    /// <summary>The default value, as a plain CLR value, or <see langword="null"/>.</summary>
    public object? Default { get; internal set; }

    /// <summary>Whether the member holds a list of values (or of keys).</summary>
    public bool Collection { get; internal set; }

    /// <summary>The maximum length: the member's, else its custom scalar type's, else its reference type's code length.</summary>
    public int? Length { get; internal set; }

    /// <summary>The precision: the member's, else its custom scalar type's.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The scale: the member's, else its custom scalar type's.</summary>
    public int? Scale { get; internal set; }

    /// <summary><c>pii</c> or <c>secret</c>, or <see langword="null"/>.</summary>
    public string? Sensitive { get; internal set; }

    /// <summary>The validation constraints, the custom scalar type's merged under the member's own, or <see langword="null"/>.</summary>
    public RValidation? Validation { get; internal set; }

    /// <summary>The member's position in its list (the input, or its returns' members), from 0.</summary>
    public int Order { get; internal set; }
}

/// <summary>A resolved outcome of an operation.</summary>
public sealed class ROperationOutcome : RElement
{
    /// <inheritdoc/>
    public override string Kind => "outcome";

    /// <summary>The operation that holds the outcome.</summary>
    public ROperation Operation { get; internal set; } = null!;

    /// <summary>Whether the outcome is its operation's default.</summary>
    public bool IsDefault { get; internal set; }

    /// <summary><c>now</c> or <c>later</c>.</summary>
    public string Completes { get; internal set; } = "now";

    /// <summary>What the outcome returns; <see langword="null"/> when it returns nothing.</summary>
    public ROperationReturns? Returns { get; internal set; }
}

/// <summary>What a resolved outcome returns: an inline shape (<see cref="Members"/>) or records of an entity (<see cref="Entity"/>).</summary>
public sealed class ROperationReturns
{
    /// <summary>The inline shape's members, in file order; empty unless the returns is of that form.</summary>
    public RList<ROperationMember> Members { get; internal set; } = RList<ROperationMember>.Empty;

    /// <summary>The entity whose records are returned, its attributes as the shape, or <see langword="null"/>.</summary>
    public REntity? Entity { get; internal set; }

    /// <summary>Whether a file is returned (a download; streaming is a file).</summary>
    public bool IsFile { get; internal set; }

    /// <summary>The media types a file result may have, in file order, the first the usual one.</summary>
    public IReadOnlyList<string> MediaTypes { get; internal set; } = [];

    /// <summary>Whether a list is returned.</summary>
    public bool IsList { get; internal set; }

    /// <summary>How a list is paged: <c>page</c> or <c>cursor</c>; <see langword="null"/> for an unpaged list or no list.</summary>
    public string? Paging { get; internal set; }

    /// <summary>The largest page a caller may ask for, set with <see cref="Paging"/>.</summary>
    public int? MaxPageSize { get; internal set; }
}

/// <summary>
/// A resolved error of an operation: its own (by its id) or one a stereotype contributes (by its keyed id
/// <c>&lt;operationId&gt;.&lt;errorId&gt;</c>, with <see cref="MemberOrigin"/> naming the stereotype); or a stereotype's error as its
/// definition lists it (<see cref="RStereotypeDefinition.Errors"/>), with no operation.
/// </summary>
public sealed class ROperationError : RElement
{
    /// <inheritdoc/>
    public override string Kind => "error";

    /// <summary>The operation that holds the error; <see langword="null"/> in <see cref="RStereotypeDefinition.Errors"/>.</summary>
    public ROperation? Operation { get; internal set; }

    /// <summary>The stable text clients see: the code, else the name.</summary>
    public string Code { get; internal set; } = "";

    /// <summary>The input member the error points at, or <see langword="null"/>.</summary>
    public ROperationMember? Input { get; internal set; }

    /// <summary>Where the error comes from (next-phase-2-design.md section 3.1); own errors share one instance.</summary>
    public RMemberOrigin MemberOrigin { get; internal set; } = RMemberOrigin.Own;
}

/// <summary>
/// A resolved guard of an operation (next-phase-3-design.md section 3.1, "Operation guards"): a condition the operation requires before
/// it runs, over its input members and, for a record-level operation, its subject's attributes under <c>subject</c>, and the error it
/// reports when the condition does not hold. The engine runs no operation: packs render the condition from its typed tree, or run its
/// JavaScript.
/// </summary>
public sealed class ROperationGuard : RElement
{
    /// <inheritdoc/>
    public override string Kind => "guard";

    /// <summary>The operation that holds the guard.</summary>
    public ROperation Operation { get; internal set; } = null!;

    /// <summary>
    /// The condition, or <see langword="null"/> when it does not type (validation says why). Its paths end at the input's
    /// <see cref="ROperationMember"/>s and the subject's <see cref="RAttribute"/>s; only true passes.
    /// </summary>
    public RExpression? Condition { get; internal set; }

    /// <summary>The error the guard reports when its condition does not hold: an own or inherited error of the operation, or
    /// <see langword="null"/> when it names none or one the operation does not have (MQ3111).</summary>
    public ROperationError? Error { get; internal set; }
}
