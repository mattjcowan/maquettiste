using System.Collections.Frozen;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A stereotype's definition (next-phase-1-design.md section 5.5): an item of <c>model.stereotypes</c>, one per key, listed by key.
/// Its <see cref="RElement.Name"/> is the label; <see cref="RAnnotated.Package"/>, rationale, decision and status are its file's.
/// The rules and parameters of section 4.2 (group, requires, conflicts, implies, parameters) and its storage are not members:
/// validation judges them, and templates read their effect on each object that carries the stereotype.
/// </summary>
public sealed class RStereotypeDefinition : RElement
{
    /// <inheritdoc/>
    public override string Kind => "stereotype";

    /// <summary>The immutable key every element's <c>stereotypes</c> list names.</summary>
    public string Key { get; internal set; } = "";

    /// <summary>The kind names the stereotype applies to, in file order; empty means every kind.</summary>
    public IReadOnlyList<string> AppliesTo { get; internal set; } = [];

    /// <summary>
    /// The virtual attributes the stereotype adds, stably by order, each with this definition as its <see cref="RAttribute.Owner"/>.
    /// They keep the ids their copies on the carrying entities, value objects, relations and reference types have.
    /// </summary>
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The default custom property values, as plain CLR values by ordinal key.</summary>
    public IReadOnlyDictionary<string, object?> DefaultProperties { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>The icon name, or <see langword="null"/>.</summary>
    public string? Icon { get; internal set; }

    /// <summary>The display color, or <see langword="null"/>.</summary>
    public string? Color { get; internal set; }
}

/// <summary>
/// A tag vocabulary (next-phase-1-design.md section 5.5): an item of <c>model.tag_vocabularies</c>. Its
/// <see cref="RAnnotated.Package"/> is the domain it is scoped to, <see langword="null"/> for the global one.
/// </summary>
public sealed class RTagVocabulary : RElement
{
    /// <inheritdoc/>
    public override string Kind => "tag-vocabulary";

    /// <summary>Whether undeclared tags in its scope are errors.</summary>
    public bool Strict { get; internal set; }

    /// <summary>The declared tags, in file order.</summary>
    public IReadOnlyList<RTagDefinition> Definitions { get; internal set; } = [];
}

/// <summary>A declared tag of a <see cref="RTagVocabulary"/>.</summary>
public sealed class RTagDefinition
{
    /// <summary>The tag.</summary>
    public string Key { get; internal set; } = "";

    /// <summary>What the tag means, or <see langword="null"/>.</summary>
    public string? Description { get; internal set; }

    /// <summary>The display color, or <see langword="null"/>.</summary>
    public string? Color { get; internal set; }
}

/// <summary>
/// A category tree (next-phase-1-design.md section 5.5): an item of <c>model.category_trees</c>. Its
/// <see cref="RAnnotated.Package"/> is the domain it is scoped to, <see langword="null"/> for the global one. Its categories are
/// the same <see cref="RCategory"/> instances annotated objects carry as their category.
/// </summary>
public sealed class RCategoryTree : RElement
{
    /// <inheritdoc/>
    public override string Kind => "category-tree";

    /// <summary>The categories without a parent (or whose parent the tree lacks), in sibling order.</summary>
    public IReadOnlyList<RCategory> Roots { get; internal set; } = [];

    /// <summary>
    /// Every category of the tree once, depth-first from <see cref="Roots"/> in sibling order; a category caught in a parent cycle
    /// (an invalid file) follows, in file order.
    /// </summary>
    public IReadOnlyList<RCategory> Categories { get; internal set; } = [];
}
