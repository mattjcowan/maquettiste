using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>A named association between entities, with roles, cardinality and its own attributes (SPEC section 7).</summary>
public sealed record Relation : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Relation;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>The inverse verb phrase ("is placed by").</summary>
    public string? InverseName { get; init; }

    /// <summary>The kind of association.</summary>
    public RelationKind RelationKind { get; init; } = RelationKind.Association;

    /// <summary>The ends: two, or three or more when <see cref="RelationKind"/> is <see cref="RelationKind.NAry"/>.</summary>
    public required IReadOnlyList<RelationEnd> Ends { get; init; }

    /// <summary>Attributes carried by the relation itself.</summary>
    public IReadOnlyList<ModelAttribute> Attributes { get; init; } = [];

    /// <summary>Whether the same tuple of entities may be linked more than once.</summary>
    public bool AllowDuplicates { get; init; }
}

/// <summary>The kind of a relation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RelationKind>))]
public enum RelationKind
{
    /// <summary>A plain association: <c>association</c>.</summary>
    [JsonStringEnumMemberName("association")] Association,

    /// <summary>A whole-part relation without owned lifetime: <c>aggregation</c>.</summary>
    [JsonStringEnumMemberName("aggregation")] Aggregation,

    /// <summary>A whole-part relation where the child's lifetime is owned: <c>composition</c>.</summary>
    [JsonStringEnumMemberName("composition")] Composition,

    /// <summary>Three or more ends: <c>n-ary</c>.</summary>
    [JsonStringEnumMemberName("n-ary")] NAry,
}

/// <summary>
/// One end (role) of a relation. It names its type in exactly one of three ways (several-types-design.md decision 1): <see cref="Entity"/>,
/// <see cref="Types"/> or <see cref="AnyEntity"/>. <see cref="HasSeveralTypes"/> and <see cref="SingleEntity"/> answer decision 2's test
/// from the file alone; the end coverage helper (<c>EndCoverage</c>) answers which entities an end covers.
/// </summary>
public sealed record RelationEnd
{
    /// <summary>The end's id; used in physical column keys (<c>&lt;endId&gt;.&lt;keyAttrId&gt;</c>).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The id of the entity at this end, or <see langword="null"/> when the end names its <see cref="Types"/> or <see cref="AnyEntity"/>.
    /// Read <see cref="SingleEntity"/> for the one entity of a one-type end, which a one-entry <see cref="Types"/> list also has.
    /// </summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>
    /// The types the end may be (several-types-design.md decision 1): entities, each with the entities derived from it, and stereotypes, each
    /// standing for its carriers and the entities derived from them; <see langword="null"/> when the end names an <see cref="Entity"/> or
    /// <see cref="AnyEntity"/>. At least one entry, with no exact repeats.
    /// </summary>
    public IReadOnlyList<RelationEndType>? Types { get; init; }

    /// <summary>
    /// <see langword="true"/> when the end may be any entity of the model; <see langword="null"/> otherwise (the schema allows only
    /// <c>true</c>, as for an expression's <c>old</c>).
    /// </summary>
    public bool? AnyEntity { get; init; }

    /// <summary>The role name ("billTo", "approver").</summary>
    public required string Role { get; init; }

    /// <summary>
    /// The property generated on the opposite entity; empty (the default, which the canonical writer omits) means not navigable
    /// from there (SPEC section 7), so an absent key and <c>""</c> are the same value.
    /// </summary>
    public string Navigation { get; init; } = "";

    /// <summary>The end's display name, translatable like an element's.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The end's plural name (to-many ends only; a to-one end's is never read).</summary>
    public string? PluralName { get; init; }

    /// <summary>The lower bound: 0 or 1.</summary>
    public int Min { get; init; }

    /// <summary>The upper bound: 1 or many.</summary>
    public MaxCardinality Max { get; init; } = MaxCardinality.Many;

    /// <summary>What happens to the other ends' rows when this end's row is deleted.</summary>
    public ReferentialIntent OnDelete { get; init; } = ReferentialIntent.None;

    /// <summary>Whether this end's collection keeps a position (D5).</summary>
    public bool Ordered { get; init; }

    /// <summary>A short description of the role.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether the end has several types (several-types-design.md decision 2): it is written with <see cref="AnyEntity"/>, or with a
    /// <see cref="Types"/> list that holds two or more entries or any stereotype entry. The test reads the file only, so it never changes
    /// when carriers change.
    /// </summary>
    [JsonIgnore]
    public bool HasSeveralTypes => AnyEntity == true || Types is { } types && (types.Count > 1 || types.Any(t => t.Stereotype is not null));

    /// <summary>
    /// The one entity of a one-type end (several-types-design.md decision 2): <see cref="Entity"/>, or the entity of a <see cref="Types"/>
    /// list that holds a single entity entry; <see langword="null"/> for an end with several types (and for an end that names nothing, which
    /// the schema refuses).
    /// </summary>
    [JsonIgnore]
    public string? SingleEntity => HasSeveralTypes ? null : Entity ?? (Types is [{ Entity: { } only }] ? only : null);
}

/// <summary>
/// An entry of a relation end's <see cref="RelationEnd.Types"/> (several-types-design.md decision 1), schema title "End type": an entity,
/// with the entities derived from it, or a stereotype, which covers every entity that carries it and the entities derived from them. It names
/// exactly one of <see cref="Entity"/> and <see cref="Stereotype"/>, and has no id of its own, as <see cref="TriggerPointSubject"/>.
/// </summary>
public sealed record RelationEndType
{
    /// <summary>The entity, or <see langword="null"/> when the entry names a <see cref="Stereotype"/>.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>
    /// The key of the stereotype the entry stands for, or <see langword="null"/> when it names an <see cref="Entity"/>. The index records it as
    /// a reference to the stereotype; an unknown key is MQ2003, and one whose stereotype does not apply to entities MQ9211.
    /// </summary>
    [StereotypeKeys]
    public string? Stereotype { get; init; }
}

/// <summary>The logical delete intent of a relation end.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReferentialIntent>))]
public enum ReferentialIntent
{
    /// <summary>No intent: <c>none</c>.</summary>
    [JsonStringEnumMemberName("none")] None,

    /// <summary>Delete the dependent rows: <c>cascade</c>.</summary>
    [JsonStringEnumMemberName("cascade")] Cascade,

    /// <summary>Refuse the delete while dependents exist: <c>restrict</c>.</summary>
    [JsonStringEnumMemberName("restrict")] Restrict,

    /// <summary>Clear the dependents' reference: <c>set-null</c>.</summary>
    [JsonStringEnumMemberName("set-null")] SetNull,
}
