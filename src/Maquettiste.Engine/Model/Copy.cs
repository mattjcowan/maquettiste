using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// An entity's <c>copy</c> block (parts-and-copy-design.md): what a copy of a whole record carries over. A copy is a new record made from an
/// existing one, with copies of its composition parts. Fields and parts not listed are copied as they are; links inside the copy point at the
/// copies, and links out of the copy stay as they were. Every member is optional and left out of the file when unset; a derived entity inherits
/// its bases' block, a nearer entry replacing a farther one for the same attribute or end. This is the block as written, not merged.
/// </summary>
public sealed record EntityCopy
{
    /// <summary>What the copy's value of each listed attribute is; an attribute not listed is copied as it is.</summary>
    public IReadOnlyList<CopyAttributeEntry>? Attributes { get; init; }

    /// <summary>What the copy's link through each listed to-one relation end is; an end not listed keeps the original's link.</summary>
    public IReadOnlyList<CopyLinkEntry>? Links { get; init; }

    /// <summary>Which parts of each listed kind the copy carries over, and what their values are; a kind not listed is copied whole.</summary>
    public IReadOnlyList<CopyPartEntry>? Parts { get; init; }
}

/// <summary>What a copy's value of one attribute is (parts-and-copy-design.md).</summary>
public sealed record CopyAttributeEntry
{
    /// <summary>The id of an attribute the record holds: its own, an inherited one or one from a stereotype it carries (MQ3045).</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public required string Attribute { get; init; }

    /// <summary>What the copy's value is.</summary>
    public required AttributeOnCopy OnCopy { get; init; }
}

/// <summary>What a copy's value of an attribute is (parts-and-copy-design.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttributeOnCopy>))]
public enum AttributeOnCopy
{
    /// <summary>The original's value, as for an attribute not listed; a derived entity undoes a base's entry with it (<c>copy</c>).</summary>
    [JsonStringEnumMemberName("copy")] Copy,

    /// <summary>The attribute's default, else empty; on a column the database fills, the database's default, so the copy gets a fresh value (<c>reset</c>).</summary>
    [JsonStringEnumMemberName("reset")] Reset,

    /// <summary>Empty; never on a required attribute (<c>clear</c>, MQ3045).</summary>
    [JsonStringEnumMemberName("clear")] Clear,

    /// <summary>The id of the record it was copied from, for a "copied from" field (<c>original</c>, MQ3046).</summary>
    [JsonStringEnumMemberName("original")] Original,
}

/// <summary>What a copy's link through one to-one relation end is (parts-and-copy-design.md).</summary>
public sealed record CopyLinkEntry
{
    /// <summary>
    /// The id of a to-one end at the other side of a two-ended relation of the record's entity, not a composition (MQ3045); the end needs no
    /// navigation, since the record holds the link whether or not code walks it.
    /// </summary>
    [ElementRef(IndexKinds = ["end"])]
    public required string End { get; init; }

    /// <summary>What the copy's link is.</summary>
    public required LinkOnCopy OnCopy { get; init; }
}

/// <summary>What a copy's link through a to-one end is (parts-and-copy-design.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LinkOnCopy>))]
public enum LinkOnCopy
{
    /// <summary>The original's link, as for an end not listed; a derived entity undoes a base's entry with it (<c>copy</c>).</summary>
    [JsonStringEnumMemberName("copy")] Copy,

    /// <summary>A link to the record it was copied from, for a "copied from" link (<c>original</c>, MQ3046).</summary>
    [JsonStringEnumMemberName("original")] Original,
}

/// <summary>
/// Which parts of one kind a copy carries over (parts-and-copy-design.md): the parts reached through a composition's part end, at any level
/// of the record's parts. A part the copy leaves out leaves its own parts out too.
/// </summary>
public sealed record CopyPartEntry
{
    /// <summary>The id of the part end of a composition among the record's parts, at any level (MQ3047).</summary>
    [ElementRef(IndexKinds = ["end"])]
    public required string End { get; init; }

    /// <summary>Which parts of the kind the copy carries over, under each copied owner.</summary>
    public required PartOnCopy OnCopy { get; init; }

    /// <summary>With <see cref="PartOnCopy.One"/>, how the one part is chosen; never beside the other words (MQ3047).</summary>
    public CopyChoice? Choose { get; init; }

    /// <summary>What the copied parts' value of each listed attribute is; an attribute not listed is copied as it is.</summary>
    public IReadOnlyList<CopyAttributeEntry>? Attributes { get; init; }
}

/// <summary>Which parts of a kind a copy carries over (parts-and-copy-design.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PartOnCopy>))]
public enum PartOnCopy
{
    /// <summary>Every part, as for a kind not listed; a derived entity undoes a base's entry with it (<c>all</c>).</summary>
    [JsonStringEnumMemberName("all")] All,

    /// <summary>One chosen part under each copied owner (<c>one</c>, with <see cref="CopyPartEntry.Choose"/>).</summary>
    [JsonStringEnumMemberName("one")] One,

    /// <summary>No part of the kind, and so none of their parts (<c>none</c>).</summary>
    [JsonStringEnumMemberName("none")] None,
}

/// <summary>
/// How a copy chooses one part of a kind under each copied owner (parts-and-copy-design.md): the part that meets <see cref="Condition"/>
/// or is in <see cref="State"/> (at most one of the two, MQ3047), the latest by <see cref="LatestBy"/> among several that do; when none
/// does, the latest of all by <see cref="LatestBy"/>; with neither condition nor state, the latest of all. Without <see cref="LatestBy"/>,
/// no part is copied when none matches, and the team's system decides among several.
/// </summary>
public sealed record CopyChoice
{
    /// <summary>A condition on the part's own attributes, in the expression language: the part for which it is true.</summary>
    public Expr? Condition { get; init; }

    /// <summary>The id of a state of a lifecycle that governs the part's entity: the part in that state (MQ3048).</summary>
    [ElementRef(IndexKinds = ["state"])]
    public string? State { get; init; }

    /// <summary>The id of an attribute of the part with an order (a number, a text, a date or a time): the latest is the greatest (MQ3048).</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public string? LatestBy { get; init; }
}
