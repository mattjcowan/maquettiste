using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>A named association between entities, with roles, cardinality and its own attributes (SPEC section 7).</summary>
public sealed record Relation : Element
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

/// <summary>One end (role) of a relation.</summary>
public sealed record RelationEnd
{
    /// <summary>The end's id; used in physical column keys (<c>&lt;endId&gt;.&lt;keyAttrId&gt;</c>).</summary>
    public required string Id { get; init; }

    /// <summary>The id of the entity at this end.</summary>
    [ElementRef(ElementKind.Entity)]
    public required string Entity { get; init; }

    /// <summary>The role name ("billTo", "approver").</summary>
    public required string Role { get; init; }

    /// <summary>
    /// The property generated on the opposite entity; empty (the default, which the canonical writer omits) means not navigable
    /// from there (SPEC section 7), so an absent key and <c>""</c> are the same value.
    /// </summary>
    public string Navigation { get; init; } = "";

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
