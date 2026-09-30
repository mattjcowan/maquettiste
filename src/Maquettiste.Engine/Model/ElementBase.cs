using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// The fields every element and every sub-element (attribute, enum member, column, category, schema) carries (SPEC section 5).
/// </summary>
public abstract record ElementBase
{
    /// <summary>The element's permanent ULID (uppercase Crockford, 26 characters). Unique across the whole model.</summary>
    public required string Id { get; init; }

    /// <summary>The element's name. Required by the schema except on a synthesized table and an overlay column.</summary>
    public string Name { get; init; } = "";

    /// <summary>An optional human-readable name; templates fall back to <see cref="Name"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>An optional plural override; templates fall back to the inflector.</summary>
    public string? PluralName { get; init; }

    /// <summary>Markdown text, or a reference to a sidecar Markdown file beside the element's file.</summary>
    public Description? Description { get; init; }

    /// <summary>Tag keys, in the order written.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The id of the element's category in the category tree, or <see langword="null"/>.</summary>
    [ElementRef(IndexKinds = ["category"])]
    public string? Category { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Custom property values, validated by extension schemas.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>Per-pack generation hints; the key is a pack name or <c>"*"</c> for every pack.</summary>
    public IReadOnlyDictionary<string, GenerationHints> Generation { get; init; } = ImmutableDictionary<string, GenerationHints>.Empty;

    /// <summary>Provenance when the element was imported.</summary>
    public SourceInfo? Source { get; init; }
}

/// <summary>
/// A top-level model element: one file, with <c>$schema</c> and <c>kind</c>. Every concrete kind is registered as a derived type
/// without a discriminator, so an <see cref="Element"/>-typed property (<see cref="ElementDocument.Element"/> in API results)
/// serializes as its runtime record with any <see cref="JsonSerializerDefaults.Web"/> options (host-contracts requirement 5). The
/// JSON already carries <c>kind</c>; reading goes through the kind's concrete type.
/// </summary>
[JsonDerivedType(typeof(Package))]
[JsonDerivedType(typeof(Entity))]
[JsonDerivedType(typeof(ValueObject))]
[JsonDerivedType(typeof(ScalarType))]
[JsonDerivedType(typeof(EnumType))]
[JsonDerivedType(typeof(Relation))]
[JsonDerivedType(typeof(Database))]
[JsonDerivedType(typeof(Table))]
[JsonDerivedType(typeof(View))]
[JsonDerivedType(typeof(Sequence))]
[JsonDerivedType(typeof(Mapping))]
[JsonDerivedType(typeof(Diagram))]
[JsonDerivedType(typeof(TagVocabulary))]
[JsonDerivedType(typeof(CategoryTree))]
[JsonDerivedType(typeof(Stereotype))]
[JsonDerivedType(typeof(ReferenceType))]
[JsonDerivedType(typeof(Seed))]
[JsonDerivedType(typeof(Process))]
[JsonDerivedType(typeof(Actor))]
[JsonDerivedType(typeof(Scenario))]
public abstract record Element : ElementBase
{
    /// <summary>
    /// The <c>$schema</c> value. The canonical writer rewrites it to the relative path of the element's schema file.
    /// </summary>
    [JsonPropertyName("$schema")]
    public string? SchemaPath { get; init; }

    /// <summary>The <c>kind</c> field. Written on serialization, ignored on read (the loader dispatches on it).</summary>
    [JsonPropertyName("kind")]
    public string KindName => KindInfo.Get(Kind).Name;

    /// <summary>The element's kind.</summary>
    [JsonIgnore]
    public abstract ElementKind Kind { get; }
}

/// <summary>Per-pack generation hints on an element (<c>generation.&lt;pack&gt;</c>).</summary>
public sealed record GenerationHints
{
    /// <summary>When <see langword="true"/>, the pack produces no unit for this element.</summary>
    public bool Skip { get; init; }

    /// <summary>A name the pack should use instead of the element's name.</summary>
    public string? Rename { get; init; }

    /// <summary>Extra template variables, visible to templates as <c>hints.variables</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> Variables { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>Provenance of an imported element, so a re-import can reconcile.</summary>
public sealed record SourceInfo
{
    /// <summary>The import format: <c>dbml</c>, <c>sql</c>, <c>database</c> or <c>openapi</c>.</summary>
    public required string Format { get; init; }

    /// <summary>The element's name in the source.</summary>
    public string? Name { get; init; }

    /// <summary>Where the source was read from (a file path, a connection name or a URL).</summary>
    public string? Location { get; init; }

    /// <summary>A fingerprint of the source definition at import time.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>
    /// Opaque data an import kept (unknown source configuration, keyed by JSON pointer into the source), written back on export
    /// (phase-3-design.md section 5.2).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}
