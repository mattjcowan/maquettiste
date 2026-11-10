namespace Maquettiste.Engine.Resolution;

/// <summary>
/// An entity's revisions (revisions-design.md section 8): its <c>revisions</c> block merged through its bases, so no template merges bases
/// itself. <c>levels</c>, <c>separator</c> and <c>bound_attribute</c> come from the nearest statement; the three lists add up through the family,
/// a nearer entry replacing a farther one for the same lifecycle, attribute or end, and each rule says which entity's block said it.
/// <para>
/// It carries keys of its own, which a unit records when it reads the value or one of its members: the files of the entity and its bases,
/// what decides which lifecycles govern the entity (as <c>entity.trigger_lifecycles</c>) with each governing lifecycle's file, the holder of
/// each attribute it names, and the relations with an end the entity reaches (decision 42). An entity with no block in its family has none,
/// so a project without a block keys on nothing new.
/// </para>
/// </summary>
public sealed class RRevisions : ITrackedValue
{
    /// <summary>The levels, from the most significant.</summary>
    public IReadOnlyList<RRevisionLevel> Levels { get; internal set; } = [];

    /// <summary>The separator written between two levels that both hold a value: the nearest one written, else <c>.</c>.</summary>
    public string Separator { get; internal set; } = Model.EntityRevisions.DefaultSeparator;

    /// <summary>The revision a new object takes, as text: the levels above the first that starts empty at their first values; <c>""</c> when level 1 starts empty.</summary>
    public string FirstRevision { get; internal set; } = "";

    /// <summary>The attribute that holds the current revision, as the entity holds it; <see langword="null"/> when the team's system holds it.</summary>
    public RAttribute? BoundAttribute { get; internal set; }

    /// <summary>
    /// Every lifecycle that governs the entity, by lifecycle name then id, with what a revise does to it. A lifecycle no entry names is filled
    /// in with <c>restart</c> and no <c>from</c>, so a reader never needs to know the fallback.
    /// </summary>
    public IReadOnlyList<RRevisionLifecycle> Lifecycles { get; internal set; } = [];

    /// <summary>Each attribute a revise doesn't copy, with what it does instead; an attribute not listed is copied.</summary>
    public IReadOnlyList<RRevisionAttribute> Attributes { get; internal set; } = [];

    /// <summary>Each relation end the entity reaches that has a rule for its links on a revise; an end not listed is the team's.</summary>
    public IReadOnlyList<RRevisionLink> Links { get; internal set; } = [];

    /// <summary>The keys the value records when it is read (decision 42), ordinal and distinct.</summary>
    internal IReadOnlyList<string> Keys { get; set; } = [];

    IReadOnlyList<string> ITrackedValue.Dependencies => Keys;
}

/// <summary>One level of an entity's revision sequence (revisions-design.md section 8).</summary>
public sealed class RRevisionLevel
{
    /// <summary>The level's number, 1 to 3, by which lifecycles and steps name it.</summary>
    public int Number { get; internal set; }

    /// <summary>The level's name; <see langword="null"/> when it has none.</summary>
    public string? Name { get; internal set; }

    /// <summary><c>letters</c>, <c>numbers</c> or <c>list</c>.</summary>
    public string Format { get; internal set; } = "letters";

    /// <summary>The list's values, in order; empty for letters and numbers.</summary>
    public IReadOnlyList<string> Values { get; internal set; } = [];

    /// <summary>The first value as text: the file's, else the format's default (the first letter not skipped, or 1 padded to the width); a list's first value.</summary>
    public string First { get; internal set; } = "";

    /// <summary>The skipped letters of a letters level; empty otherwise.</summary>
    public IReadOnlyList<string> Skip { get; internal set; } = [];

    /// <summary>The width a numbers level pads to; <see langword="null"/> when none, or for another format.</summary>
    public int? Width { get; internal set; }

    /// <summary>Whether the level holds nothing at first, and again whenever a higher level moves.</summary>
    public bool StartEmpty { get; internal set; }

    /// <summary>The level's first five values on their own (all of a shorter list), for docs and the editor.</summary>
    public IReadOnlyList<string> Examples { get; internal set; } = [];
}

/// <summary>What a revise does to one lifecycle that governs the entity (revisions-design.md decision 33).</summary>
public sealed class RRevisionLifecycle
{
    /// <summary>The lifecycle.</summary>
    public RProcess Lifecycle { get; internal set; } = null!;

    /// <summary><c>restart</c> (back to the initial state), <c>keep</c> (the states stay as they are) or <c>state</c> (the state <see cref="State"/> names).</summary>
    public string OnRevise { get; internal set; } = "restart";

    /// <summary>With <c>state</c>, the state entered; <see langword="null"/> otherwise.</summary>
    public RState? State { get; internal set; }

    /// <summary>The entity whose entry says it; <see langword="null"/> for the fallback (no entry names the lifecycle).</summary>
    public REntity? From { get; internal set; }
}

/// <summary>What a revise does to one attribute it doesn't copy (revisions-design.md decision 34).</summary>
public sealed class RRevisionAttribute
{
    /// <summary>The attribute, as the entity holds it.</summary>
    public RAttribute Attribute { get; internal set; } = null!;

    /// <summary><c>reset</c> (the attribute's default, else empty) or <c>clear</c> (empty).</summary>
    public string OnRevise { get; internal set; } = "reset";

    /// <summary>The entity whose entry says it.</summary>
    public REntity From { get; internal set; } = null!;
}

/// <summary>What a revise does to the links through one relation end the entity reaches (revisions-design.md decision 35).</summary>
public sealed class RRevisionLink
{
    /// <summary>The relation end, the one the entity navigates to.</summary>
    public REnd End { get; internal set; } = null!;

    /// <summary><c>stay</c> (the earlier revision keeps the link), <c>move</c> (it goes with the new revision) or <c>duplicate</c> (the new revision gets a link of its own).</summary>
    public string OnRevise { get; internal set; } = "stay";

    /// <summary>The entity whose entry says it.</summary>
    public REntity From { get; internal set; } = null!;
}
