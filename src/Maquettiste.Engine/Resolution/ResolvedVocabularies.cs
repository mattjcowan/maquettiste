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

    /// <summary>
    /// The errors the stereotype contributes to the operations that carry it, in file order, each by its own id with no operation and
    /// no input (next-phase-2-design.md section 4.1): a pack can write one set of constants per stereotype.
    /// </summary>
    public RList<ROperationError> Errors { get; internal set; } = RList<ROperationError>.Empty;

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

/// <summary>
/// A verb (verbs-design.md section 6): an item of <c>model.verbs</c>, a built-in verb with its fixed meaning or one of the project's own.
/// Uses name it by <see cref="Key"/>, which is what to store. Every member depends on the verb vocabulary and on every process and
/// operation file, since a use anywhere decides <see cref="Used"/> and the lists.
/// </summary>
public sealed class RVerb : IResolvedObject
{
    /// <summary>The key every use names, such as <c>promote</c> or <c>escalate</c>.</summary>
    public string Key { get; internal set; } = "";

    /// <summary>The fixed label of a built-in verb, or an own verb's display name, else its key with the first letter upper case and hyphens
    /// as spaces.</summary>
    public string DisplayName { get; internal set; } = "";

    /// <summary>The fixed meaning of a built-in verb, or an own verb's description (<c>""</c> when none).</summary>
    public string Description { get; internal set; } = "";

    /// <summary>Whether it is a built-in verb.</summary>
    public bool BuiltIn { get; internal set; }

    /// <summary>What a built-in verb acts on (<c>move</c>, <c>signature</c>, <c>task</c>, <c>object</c>, <c>link</c>); <c>project</c> for an own
    /// verb.</summary>
    public string Group { get; internal set; } = "";

    /// <summary>The own verb's id; <see langword="null"/> for a built-in verb.</summary>
    public string? Id { get; internal set; }

    /// <summary>Whether it is a hidden built-in verb that is still named.</summary>
    public bool Hidden { get; internal set; }

    /// <summary>The events that name it, in model order (process order, then event order).</summary>
    public RList<REvent> Events { get; internal set; } = RList<REvent>.Empty;

    /// <summary>The transitions whose move has it, their own verb or their event's, in model order.</summary>
    public RList<RTransition> Transitions { get; internal set; } = RList<RTransition>.Empty;

    /// <summary>The operations that name it, in <see cref="ResolvedModel.Operations"/> order.</summary>
    public RList<ROperation> Operations { get; internal set; } = RList<ROperation>.Empty;

    /// <summary>Whether an event, a transition or an operation names it.</summary>
    public bool Used { get; internal set; }

    /// <summary>
    /// The rights that name it (rights-design.md section 6): the named rights in <see cref="ResolvedModel.Rights"/> order, then the inline
    /// rights in process order and file order. It keys on the rights naming key, and in a project that uses rights also on every named
    /// right and every lifecycle with rights; empty in a project without rights.
    /// </summary>
    public RList<RRight> Rights { get; internal set; } = RList<RRight>.Empty;

    /// <inheritdoc/>
    public string Kind => "verb";

    /// <inheritdoc/>
    public IReadOnlyList<string> Dependencies { get; internal set; } = [];

    /// <summary>The own verb's id, else <c>verb:&lt;key&gt;</c>: built-in verbs have no id of their own.</summary>
    string IResolvedObject.Id => Id ?? "verb:" + Key;
}
