using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>Whether a right allows or denies (rights-design.md decision 3); also a lifecycle's default (decision 9).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RightAccess>))]
public enum RightAccess
{
    /// <summary>The right lets its actors do its verbs (<c>allow</c>).</summary>
    [JsonStringEnumMemberName("allow")] Allow,

    /// <summary>The right stops its actors doing its verbs (<c>deny</c>).</summary>
    [JsonStringEnumMemberName("deny")] Deny,
}

/// <summary>
/// A named right (rights-design.md decision 7; <c>model/rights/</c>): one rule that many lifecycles use, each with an entry of its
/// <see cref="Process.Rights"/> that names it and the states where it holds. It allows or denies its actors, less those in
/// <see cref="Except"/>, its verbs, under an optional condition; its description says why.
/// </summary>
public sealed record Right : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Right;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root. Its name is unique in the package (MQ3001).</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>Whether the right allows or denies.</summary>
    public required RightAccess Access { get; init; }

    /// <summary>The actors it applies to, with their members at any depth, in file order; empty means everyone.</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> Actors { get; init; } = [];

    /// <summary>The actors it does not apply to, with their members at any depth, in file order (decision 4): other rights and the
    /// default still answer for them, so it never grants.</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> Except { get; init; } = [];

    /// <summary>The verbs it speaks of, by key, at least one: built-in verbs or the project's own (decision 5).</summary>
    [VerbKey]
    public required IReadOnlyList<string> Verbs { get; init; }

    /// <summary>Relation ends of the subject: the right then speaks of the links through them (decision 20); empty for none.</summary>
    [ElementRef(IndexKinds = ["end"])]
    public IReadOnlyList<string> Ends { get; init; } = [];

    /// <summary>
    /// Attributes of the subject: the right then speaks of reading or modifying only those (decision 21), beside only <c>read</c> and
    /// <c>modify</c> (MQ9903); empty for the object as a whole.
    /// </summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Whether the right holds only while the object, or with <see cref="Ends"/> the link, is frozen (decision 18).</summary>
    public bool WhileFrozen { get; init; }

    /// <summary>
    /// The condition, or <see langword="null"/>: the right holds only when it is true (decision 6). Its scope is what every subject of every
    /// lifecycle that uses the right holds under one id; with no use, text written for it stays text (MQ3108).
    /// </summary>
    public Expr? Condition { get; init; }
}

/// <summary>
/// An entry of a lifecycle's <see cref="Process.Rights"/> (index kind <c>process-right</c>, rights-design.md decisions 1, 3 and 7): an inline
/// right, which shares its id with the entry, or a use of a named <see cref="Model.Right"/> with only the states where it holds. Exactly one
/// form (MQ9901): an inline right has a <see cref="Name"/>, an <see cref="Access"/> and at least one verb, and no <see cref="Right"/>.
/// </summary>
public sealed record ProcessRight : IProcessNode
{
    /// <inheritdoc/>
    public required string Id { get; init; }

    /// <summary>
    /// A use: the id of the named right that holds here; <see langword="null"/> for an inline right. Any element kind is a reference, so one
    /// that is not a named right is MQ9901 rather than MQ2002.
    /// </summary>
    [ElementRef]
    public string? Right { get; init; }

    /// <summary>An inline right's name: an identifier unique among the lifecycle's inline rights (MQ3001); <see langword="null"/> for a use.</summary>
    public string? Name { get; init; }

    /// <summary>An inline right's optional display name (localizable).</summary>
    public string? DisplayName { get; init; }

    /// <summary>An inline right's access; <see langword="null"/> for a use.</summary>
    public RightAccess? Access { get; init; }

    /// <summary>An inline right's actors, in file order; empty means everyone.</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> Actors { get; init; } = [];

    /// <summary>The actors an inline right does not apply to, in file order.</summary>
    [ElementRef(ElementKind.Actor)]
    public IReadOnlyList<string> Except { get; init; } = [];

    /// <summary>An inline right's verbs, by key; empty for a use.</summary>
    [VerbKey]
    public IReadOnlyList<string> Verbs { get; init; } = [];

    /// <summary>The relation ends of the subject whose links an inline right speaks of; empty for none.</summary>
    [ElementRef(IndexKinds = ["end"])]
    public IReadOnlyList<string> Ends { get; init; } = [];

    /// <summary>The attributes of the subject whose reading or modifying an inline right speaks of (decision 21); empty for the object.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Whether an inline right holds only while the object, or with <see cref="Ends"/> the link, is frozen (decision 18).</summary>
    public bool WhileFrozen { get; init; }

    /// <summary>An inline right's condition, read in its lifecycle's scope; <see langword="null"/> for none.</summary>
    public Expr? Condition { get; init; }

    /// <summary>The states of this lifecycle where the right holds; empty for every state.</summary>
    [ElementRef(IndexKinds = ["state"])]
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>Why: shown with every refusal (localizable). Markdown text, or a reference to a sidecar Markdown file.</summary>
    public Description? Description { get; init; }

    /// <summary>Whether the entry uses a named right rather than saying a right itself.</summary>
    [JsonIgnore]
    public bool IsUse => Right is not null;
}
