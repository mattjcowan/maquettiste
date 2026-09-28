using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Maquettiste.Engine.Model;

/// <summary>The tag vocabulary (<c>model/vocabularies/tags.json</c>).</summary>
public sealed record TagVocabulary : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.TagVocabulary;

    /// <summary>When <see langword="true"/>, undeclared tags are errors (MQ2006); otherwise info.</summary>
    public bool Strict { get; init; }

    /// <summary>
    /// The declared tags (JSON <c>definitions</c>; named so because <see cref="ElementBase.Tags"/> already holds the file's own tag keys).
    /// </summary>
    public IReadOnlyList<TagDefinition> Definitions { get; init; } = [];
}

/// <summary>A declared tag.</summary>
public sealed record TagDefinition
{
    /// <summary>The tag: a free-form label (SPEC section 5) of up to 64 characters without whitespace or control characters.</summary>
    public required string Key { get; init; }

    /// <summary>What the tag means.</summary>
    public string? Description { get; init; }

    /// <summary>A display color.</summary>
    public string? Color { get; init; }
}

/// <summary>The category tree (<c>model/vocabularies/categories.json</c>): a flat list, a tree through <see cref="Category.Parent"/>.</summary>
public sealed record CategoryTree : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.CategoryTree;

    /// <summary>Every category.</summary>
    public IReadOnlyList<Category> Categories { get; init; } = [];
}

/// <summary>A category; elements reference it by id.</summary>
public sealed record Category : ElementBase
{
    /// <summary>The id of the parent category, or <see langword="null"/> for a root.</summary>
    [ElementRef(IndexKinds = ["category"])]
    public string? Parent { get; init; }

    /// <summary>The position among siblings.</summary>
    public int? Order { get; init; }
}

/// <summary>
/// A stereotype (<c>model/vocabularies/stereotypes/</c>). Elements list it by its immutable <see cref="Key"/>; its
/// <see cref="ElementBase.Name"/> is a label that can change freely (D2).
/// </summary>
public sealed record Stereotype : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Stereotype;

    /// <summary>
    /// The kebab-case key used in every element's <c>stereotypes</c> list. Immutable like an id: a save that changes it is refused
    /// (MQ3020). The reverse index records each <c>stereotypes[i]</c> entry as a reference to the stereotype's id.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Kind names (engine-design.md section 2.2) plus <c>attribute</c>, <c>enum-member</c> and <c>column</c>; empty means all.</summary>
    public IReadOnlyList<string> AppliesTo { get; init; } = [];

    /// <summary>Virtual attributes added to entities, value objects and relations that carry the stereotype.</summary>
    public IReadOnlyList<ModelAttribute> Attributes { get; init; } = [];

    /// <summary>Default custom property values, merged under the element's own <c>properties</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> DefaultProperties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>An icon name.</summary>
    public string? Icon { get; init; }

    /// <summary>A display color.</summary>
    public string? Color { get; init; }
}
