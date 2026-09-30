using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>A namespace that groups elements and drives folders and namespaces in output (<c>model/packages/</c>).</summary>
public sealed record Package : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Package;

    /// <summary>The id of the parent package, or <see langword="null"/> for a root package.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Parent { get; init; }
}

/// <summary>A business object with identity (SPEC section 6; <c>model/entities/</c>).</summary>
public sealed record Entity : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Entity;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>Whether the entity is abstract (never instantiated; no table under TPC).</summary>
    public bool Abstract { get; init; }

    /// <summary>The id of the base entity, or <see langword="null"/>.</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Base { get; init; }

    /// <summary>The process that is this entity's lifecycle, or <see langword="null"/> (MQ9201 checks both sides).</summary>
    [ElementRef(ElementKind.Process)]
    public string? Lifecycle { get; init; }

    /// <summary>The primary key. Required unless the entity is abstract or has a base (MQ3005).</summary>
    public EntityKey? Key { get; init; }

    /// <summary>Named alternate (natural) keys.</summary>
    public IReadOnlyList<AlternateKey> AlternateKeys { get; init; } = [];

    /// <summary>The entity's own attributes, in canonical order: the canonical writer stable-sorts them by <see cref="ModelAttribute.Order"/> (missing = 0).</summary>
    public IReadOnlyList<ModelAttribute> Attributes { get; init; } = [];
}

/// <summary>An entity's primary key.</summary>
public sealed record EntityKey
{
    /// <summary>The ids of the key attributes, in key order.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public required IReadOnlyList<string> Attributes { get; init; }

    /// <summary>
    /// How key values are assigned. With <see cref="IdentityStrategy.Sequence"/>, the sequence is physical and chosen per database:
    /// the key column's overlay (<see cref="Column.Generated"/> <c>sequence</c> and <see cref="Column.Sequence"/>) in that database's
    /// table file names it; without one, the resolver synthesizes a sequence named by <see cref="Conventions.SequenceName"/> (D37).
    /// </summary>
    public IdentityStrategy Strategy { get; init; } = IdentityStrategy.Application;
}

/// <summary>How an entity's key values are assigned.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IdentityStrategy>))]
public enum IdentityStrategy
{
    /// <summary>The database assigns the value (identity column): <c>database-identity</c>.</summary>
    [JsonStringEnumMemberName("database-identity")] DatabaseIdentity,

    /// <summary>A database sequence supplies the value: <c>sequence</c>.</summary>
    [JsonStringEnumMemberName("sequence")] Sequence,

    /// <summary>A UUID version 7 is generated: <c>uuid-v7</c>.</summary>
    [JsonStringEnumMemberName("uuid-v7")] UuidV7,

    /// <summary>A ULID is generated: <c>ulid</c>.</summary>
    [JsonStringEnumMemberName("ulid")] Ulid,

    /// <summary>The application assigns the value: <c>application</c>.</summary>
    [JsonStringEnumMemberName("application")] Application,
}

/// <summary>A named alternate (natural) key.</summary>
public sealed record AlternateKey
{
    /// <summary>The key's id.</summary>
    public required string Id { get; init; }

    /// <summary>The key's name.</summary>
    public required string Name { get; init; }

    /// <summary>The ids of the key attributes, in key order.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public required IReadOnlyList<string> Attributes { get; init; }
}

/// <summary>A typed field on an entity, value object, relation or stereotype (SPEC section 6, attribute table).</summary>
public sealed record ModelAttribute : ElementBase
{
    /// <summary>A built-in keyword or a reference to an enum, value object or custom scalar type.</summary>
    public required TypeRef Type { get; init; }

    /// <summary>Whether a value is required (not nullable).</summary>
    public bool Required { get; init; }

    /// <summary>A literal default value.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>A named default expression: <c>now</c>, <c>today</c>, <c>new-uuid</c>, <c>new-ulid</c> or a pack-defined name (D4).</summary>
    public string? DefaultExpression { get; init; }

    /// <summary>The maximum length of a string or binary value.</summary>
    public int? Length { get; init; }

    /// <summary>The precision of a decimal (or fractional seconds of a time value).</summary>
    public int? Precision { get; init; }

    /// <summary>The scale of a decimal.</summary>
    public int? Scale { get; init; }

    /// <summary>Whether the attribute holds a list of its type.</summary>
    public bool Collection { get; init; }

    /// <summary>Logical uniqueness intent.</summary>
    public bool Unique { get; init; }

    /// <summary>Logical indexing intent.</summary>
    public bool Indexed { get; init; }

    /// <summary>Not settable after create.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Never changed after insert.</summary>
    public bool Immutable { get; init; }

    /// <summary>When set, the value is computed from an expression.</summary>
    public DerivedSpec? Derived { get; init; }

    /// <summary>Whether the value is personal data or a secret.</summary>
    public Sensitivity? Sensitive { get; init; }

    /// <summary>Validation constraints on the value.</summary>
    public AttributeValidation? Validation { get; init; }

    /// <summary>
    /// The stable position (SPEC section 6): attribute arrays are stable-sorted by it, a missing value counting as 0, both in the
    /// canonical file form (the schema's <c>x-sort</c>) and in the resolved attribute order (engine-design.md section 7.2).
    /// </summary>
    public int? Order { get; init; }
}

/// <summary>How a derived attribute is computed.</summary>
public sealed record DerivedSpec
{
    /// <summary>The expression, interpreted by templates.</summary>
    public required string Expression { get; init; }

    /// <summary>Whether the computed value is stored.</summary>
    public bool Stored { get; init; }
}

/// <summary>The sensitivity of an attribute's value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Sensitivity>))]
public enum Sensitivity
{
    /// <summary>Personally identifiable information: <c>pii</c>.</summary>
    [JsonStringEnumMemberName("pii")] Pii,

    /// <summary>A secret: <c>secret</c>.</summary>
    [JsonStringEnumMemberName("secret")] Secret,
}

/// <summary>Validation constraints on an attribute or custom scalar type.</summary>
public sealed record AttributeValidation
{
    /// <summary>The minimum value (number, or an ISO 8601 string for temporal types).</summary>
    public JsonElement? Min { get; init; }

    /// <summary>The maximum value (number, or an ISO 8601 string for temporal types).</summary>
    public JsonElement? Max { get; init; }

    /// <summary>A regular expression the value must match.</summary>
    public string? Pattern { get; init; }

    /// <summary>The only values allowed.</summary>
    public IReadOnlyList<JsonElement> AllowedValues { get; init; } = [];

    /// <summary>Ids of JavaScript validation rules (<c>x/&lt;id&gt;</c> without the prefix).</summary>
    public IReadOnlyList<string> Rules { get; init; } = [];
}

/// <summary>A composite type without identity (Address, Money; <c>model/types/</c>).</summary>
public sealed record ValueObject : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.ValueObject;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>The value object's attributes, in canonical order (stable-sorted by <see cref="ModelAttribute.Order"/>).</summary>
    public IReadOnlyList<ModelAttribute> Attributes { get; init; } = [];
}

/// <summary>A named restriction of a built-in type (Email = string, max 254, pattern; <c>model/types/</c>).</summary>
public sealed record ScalarType : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.ScalarType;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>The built-in keyword this type restricts.</summary>
    public required string Base { get; init; }

    /// <summary>The maximum length.</summary>
    public int? Length { get; init; }

    /// <summary>The decimal precision.</summary>
    public int? Precision { get; init; }

    /// <summary>The decimal scale.</summary>
    public int? Scale { get; init; }

    /// <summary>Validation constraints every use of the type inherits.</summary>
    public AttributeValidation? Validation { get; init; }
}

/// <summary>A closed set of named members (<c>model/enums/</c>).</summary>
public sealed record EnumType : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Enum;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>Whether members combine as bit flags (values must be powers of two).</summary>
    public bool Flags { get; init; }

    /// <summary>The members, in canonical order.</summary>
    public IReadOnlyList<EnumMember> Members { get; init; } = [];
}

/// <summary>A member of an enum, with an optional integer value and string code.</summary>
public sealed record EnumMember : ElementBase
{
    /// <summary>The integer value.</summary>
    public long? Value { get; init; }

    /// <summary>The string code.</summary>
    public string? Code { get; init; }
}
