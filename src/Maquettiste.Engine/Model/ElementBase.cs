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

    /// <summary>Markdown text: why the element is shaped this way. No sidecar.</summary>
    public string? Rationale { get; init; }

    /// <summary>A URL or a reference (such as <c>ADR-0042</c>) of the decision record. Not an element id, and not checked.</summary>
    public string? Decision { get; init; }

    /// <summary>The element's status, or <see langword="null"/> when none is written (there is no default).</summary>
    public ElementStatus? Status { get; init; }

    /// <summary>Tag keys, in the order written.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The id of the element's category in the category tree, or <see langword="null"/>.</summary>
    [ElementRef(IndexKinds = ["category"])]
    public string? Category { get; init; }

    /// <summary>Stereotype keys, in application order.</summary>
    [StereotypeKeys]
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
[JsonDerivedType(typeof(Routine))]
[JsonDerivedType(typeof(DatabaseType))]
[JsonDerivedType(typeof(SqlObject))]
[JsonDerivedType(typeof(Query))]
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
[JsonDerivedType(typeof(Migration))]
[JsonDerivedType(typeof(Operation))]
[JsonDerivedType(typeof(Constraint))]
[JsonDerivedType(typeof(Custom))]
[JsonDerivedType(typeof(VerbVocabulary))]
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

    /// <summary>
    /// Stereotype keys that a database schema's or a package's default stereotypes would give the element, left out of its
    /// effective set with the stereotypes they imply (next-phase-2-design.md section 3.1). Only defaults can be excluded (MQ2029).
    /// <see langword="null"/> when the file does not state the key. Virtual only so that <see cref="Migration"/>, whose file has no such
    /// key, can leave it out of its JSON contract.
    /// </summary>
    [StereotypeKeys]
    public virtual IReadOnlyList<string>? ExcludeStereotypes { get; init; }

    /// <summary>Findings this element accepts, each with its reason (next-phase-2-design.md section 5.6).</summary>
    public IReadOnlyList<Suppression> Suppressions { get; init; } = [];
}

/// <summary>One accepted finding on an element (<c>suppressions[i]</c>): a rule, optionally one part of the element, and why.</summary>
public sealed record Suppression
{
    /// <summary>The rule: a built-in id (<c>MQ</c> and four digits) or a project rule (<c>x/&lt;id&gt;</c>).</summary>
    public required string Rule { get; init; }

    /// <summary>Why this element is the exception.</summary>
    public required string Reason { get; init; }

    /// <summary>The id of one part of the element the entry is limited to (an inherited member's keyed id included),
    /// or null for the whole element.</summary>
    public string? Member { get; init; }
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
