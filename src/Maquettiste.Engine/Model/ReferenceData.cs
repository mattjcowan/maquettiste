using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A named set of rows that attributes use as their type (<c>model/reference-types/</c>; reference-types-seeds-localization.md
/// section 1). Two built-in fields, <see cref="Code"/> and <see cref="Label"/>, the row description every row may carry, any number
/// of user fields, and rows held in seeds.
/// </summary>
public sealed record ReferenceType : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.ReferenceType;

    /// <summary>The built-in code field: the row's business key, unique in the type.</summary>
    public required ReferenceCode Code { get; init; }

    /// <summary>The built-in label field: the row's display text.</summary>
    public required ReferenceLabel Label { get; init; }

    /// <summary>The user fields, in canonical order.</summary>
    public IReadOnlyList<ModelAttribute> Attributes { get; init; } = [];

    /// <summary>Storage choices keyed by database id, or <c>*</c> for every database.</summary>
    public IReadOnlyDictionary<string, StorageChoice> Storage { get; init; } = ImmutableDictionary<string, StorageChoice>.Empty;
}

/// <summary>The built-in code field of a reference type.</summary>
public sealed record ReferenceCode
{
    /// <summary>The field's id, which translations key on.</summary>
    public required string Id { get; init; }

    /// <summary>The code's logical type: <c>string</c>, <c>int16</c>, <c>int32</c>, <c>int64</c> or <c>uuid</c> (written in its canonical
    /// lowercase hyphenated form, MQ7013).</summary>
    public string Type { get; init; } = "string";

    /// <summary>The maximum length of a string code.</summary>
    public int? Length { get; init; }

    /// <summary>A regular expression every code matches.</summary>
    public string? Pattern { get; init; }

    /// <summary>The field's display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The field's description.</summary>
    public Description? Description { get; init; }
}

/// <summary>The built-in label field of a reference type.</summary>
public sealed record ReferenceLabel
{
    /// <summary>The field's id, which translations key on.</summary>
    public required string Id { get; init; }

    /// <summary>The maximum length of a label.</summary>
    public int? Length { get; init; }

    /// <summary>The field's display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The field's description.</summary>
    public Description? Description { get; init; }
}

/// <summary>A storage choice for reference data: a strategy key the project declares, or none for template-defined.</summary>
public sealed record StorageChoice
{
    /// <summary>The strategy key, or <see langword="null"/> when the templates decide.</summary>
    public string? Strategy { get; init; }

    /// <summary>The strategy's options, validated against its declared option schema.</summary>
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>
/// Rows of data for one target: an entity, a relation or a reference type (<c>model/seeds/&lt;target&gt;/</c>;
/// reference-types-seeds-localization.md section 2). A seed belongs to its target and is deleted with it.
/// </summary>
public sealed record Seed : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Seed;

    /// <summary>The id of the entity, relation or reference type the rows are for.</summary>
    [ElementRef(ElementKind.Entity, ElementKind.Relation, ElementKind.ReferenceType, Owning = true)]
    public required string Target { get; init; }

    /// <summary>The columns: <c>code</c>, <c>label</c> or <c>description</c> for a reference type, else attribute or relation-end ids.</summary>
    [ElementRef(Keyed = true)]
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The rows, in file order, each written on its own line.</summary>
    public IReadOnlyList<SeedRow> Rows { get; init; } = [];
}

/// <summary>One row of a seed: its id and one cell per column (trailing nulls dropped).</summary>
public sealed record SeedRow
{
    /// <summary>The row's id.</summary>
    public required string Id { get; init; }

    /// <summary>The cells, in column order; a JSON null is no value.</summary>
    public IReadOnlyList<JsonElement> Values { get; init; } = [];
}
