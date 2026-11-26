using System.Collections.Frozen;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// One part of a whole record (parts-and-copy-design.md): records of <see cref="Entity"/> owned through a composition, at <see cref="Depth"/>
/// under <see cref="Parent"/>. <c>entity.parts</c> lists them in an order safe for foreign keys: owners before their parts, and a part after
/// the parts its to-one links point at; deleting deepest first is that order reversed. A composition whose part end has several types is not
/// listed, nor what lies below it.
/// <para>
/// Reading a part records its relation's keys, which cover the relation's storage in every database.
/// </para>
/// </summary>
public sealed class RPart : ITrackedValue
{
    /// <summary>The part's path from the record: the ids of the part ends on the way down, joined by <c>/</c>; unique among the record's parts.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>The composition.</summary>
    public RRelation Relation { get; internal set; } = null!;

    /// <summary>The composition's owner end, the one with max 1, at the owner.</summary>
    public REnd OwnerEnd { get; internal set; } = null!;

    /// <summary>The composition's part end, at the part: the end the owner navigates to.</summary>
    public REnd End { get; internal set; } = null!;

    /// <summary>The part's entity.</summary>
    public REntity Entity { get; internal set; } = null!;

    /// <summary>The entity that owns the part here: the record's entity, or the entity of <see cref="Parent"/>.</summary>
    public REntity Owner { get; internal set; } = null!;

    /// <summary>The part it belongs to; <see langword="null"/> for a part of the record itself.</summary>
    public RPart? Parent { get; internal set; }

    /// <summary>1 for a part of the record itself, 2 for a part of a part, and so on.</summary>
    public int Depth { get; internal set; }

    /// <summary>
    /// Whether the part's entity is already on the way down from the record, as in a tree of parts of one kind: its own parts are not listed
    /// again, and its rows point at rows of the same kind, which the team orders by their own links.
    /// </summary>
    public bool IsRecursive { get; internal set; }

    /// <summary>
    /// Whether the part's to-one links go round in a circle with other parts or the record, so no order puts it after every part it points at:
    /// one link of the circle has to be written empty and set after the rows are in.
    /// </summary>
    public bool InCycle { get; internal set; }

    /// <summary>
    /// By database name, the foreign key that links the part's rows to their owner's, in each database where the composition is stored with
    /// one (through bindings, a mapping or projection): its <c>columns</c> in the part's table and its <c>referenced_columns</c>, the owner's
    /// key. Its <c>end</c> is the part end, except for a one-to-one composition stored with the key in the owner's table, where the part is
    /// written before its owner. A composition stored through a link table, or with several types at the owner end, has none here.
    /// </summary>
    public IReadOnlyDictionary<string, RForeignKey> ForeignKeys { get; internal set; } = FrozenDictionary<string, RForeignKey>.Empty;

    IReadOnlyList<string> ITrackedValue.Dependencies => Relation.Dependencies;
}

/// <summary>
/// What a copy of a whole record of an entity carries over (parts-and-copy-design.md): its <c>copy</c> block merged through its bases, a nearer
/// entry replacing a farther one for the same attribute or end, with references resolved, so a template can write the copy statements itself.
/// A copy is a new record, so its key is always new and every lifecycle starts at its initial state. Links inside the copy point at the
/// copies; links out of the copy stay as they were.
/// <para>
/// It carries keys of its own, which a unit records when it reads the value or one of its members: what <c>entity.parts</c> keys on, the files
/// of the entity and its bases, and of what the entries name.
/// </para>
/// </summary>
public sealed class RCopy : ITrackedValue
{
    /// <summary>Each attribute the copy doesn't take as it is, in the entity's attribute order.</summary>
    public IReadOnlyList<RCopyAttribute> Attributes { get; internal set; } = [];

    /// <summary>Each to-one link the copy doesn't take as it is.</summary>
    public IReadOnlyList<RCopyLink> Links { get; internal set; } = [];

    /// <summary>
    /// Each part the copy carries over, in the order of <c>entity.parts</c>; a kind with no entry is filled in with <c>all</c> and no
    /// <c>from</c>. A kind the copy leaves out (<c>none</c>) is not listed, nor are its own parts.
    /// </summary>
    public IReadOnlyList<RCopyPart> Parts { get; internal set; } = [];

    /// <summary>The keys the value records when it is read, ordinal and distinct.</summary>
    internal IReadOnlyList<string> Keys { get; set; } = [];

    IReadOnlyList<string> ITrackedValue.Dependencies => Keys;
}

/// <summary>What a copy's value of one attribute is (parts-and-copy-design.md).</summary>
public sealed class RCopyAttribute
{
    /// <summary>The attribute, as the record's (or the part's) entity holds it.</summary>
    public RAttribute Attribute { get; internal set; } = null!;

    /// <summary>
    /// <c>reset</c>: the attribute's default, else empty; on a column the database fills, the insert leaves it to the column's default, so
    /// the copy gets a fresh value. <c>clear</c>: empty. <c>original</c>: the id of the record (or part) it was copied from.
    /// </summary>
    public string OnCopy { get; internal set; } = "";

    /// <summary>The entity whose block says it.</summary>
    public REntity From { get; internal set; } = null!;
}

/// <summary>What a copy's link through one to-one relation end is (parts-and-copy-design.md).</summary>
public sealed class RCopyLink
{
    /// <summary>The end the entity navigates to.</summary>
    public REnd End { get; internal set; } = null!;

    /// <summary><c>original</c>: the copy links to the record it was copied from.</summary>
    public string OnCopy { get; internal set; } = "";

    /// <summary>The entity whose block says it.</summary>
    public REntity From { get; internal set; } = null!;
}

/// <summary>Which parts of one kind a copy carries over, and what their values are (parts-and-copy-design.md).</summary>
public sealed class RCopyPart
{
    /// <summary>The part, one of <c>entity.parts</c>.</summary>
    public RPart Part { get; internal set; } = null!;

    /// <summary><c>all</c>: every part under each copied owner; <c>one</c>: one part under each copied owner, as <see cref="Choose"/> says.</summary>
    public string OnCopy { get; internal set; } = "all";

    /// <summary>With <c>one</c>, how the part is chosen; else <see langword="null"/>.</summary>
    public RCopyChoice? Choose { get; internal set; }

    /// <summary>Each attribute of the part the copy doesn't take as it is.</summary>
    public IReadOnlyList<RCopyAttribute> Attributes { get; internal set; } = [];

    /// <summary>The entity whose block says it; <see langword="null"/> for a kind with no entry.</summary>
    public REntity? From { get; internal set; }
}

/// <summary>
/// How a copy chooses one part under each copied owner (parts-and-copy-design.md): the part that meets <see cref="Condition"/> or is in
/// <see cref="State"/>, the latest by <see cref="LatestBy"/> among several that do; when none does, the latest of all by
/// <see cref="LatestBy"/>; with neither condition nor state, the latest of all. Without <see cref="LatestBy"/>, no part is copied when none
/// matches.
/// </summary>
public sealed class RCopyChoice
{
    /// <summary>A condition on the part's own attributes: the part for which it is true; <see langword="null"/> when none is given or it doesn't type.</summary>
    public RExpression? Condition { get; internal set; }

    /// <summary>A state of a lifecycle that governs the part's entity: the part in that state; else <see langword="null"/>.</summary>
    public RState? State { get; internal set; }

    /// <summary>The attribute of the part whose greatest value is the latest; else <see langword="null"/>.</summary>
    public RAttribute? LatestBy { get; internal set; }
}
