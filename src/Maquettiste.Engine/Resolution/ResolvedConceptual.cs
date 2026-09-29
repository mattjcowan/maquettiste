using System.Collections.Frozen;

namespace Maquettiste.Engine.Resolution;

/// <summary>A resolved package.</summary>
public sealed class RPackage : RElement
{
    /// <inheritdoc/>
    public override string Kind => "package";

    /// <summary>The dotted qualified name, for example <c>Billing.Invoicing</c>.</summary>
    public string QualifiedName { get; internal set; } = "";

    /// <summary>The parent package.</summary>
    public RPackage? Parent { get; internal set; }

    /// <summary>Child packages.</summary>
    public RList<RPackage> Children { get; internal set; } = RList<RPackage>.Empty;

    /// <summary>Entities directly in the package.</summary>
    public RList<REntity> Entities { get; internal set; } = RList<REntity>.Empty;

    /// <summary>Value objects directly in the package.</summary>
    public RList<RValueObject> ValueObjects { get; internal set; } = RList<RValueObject>.Empty;

    /// <summary>Enums directly in the package.</summary>
    public RList<REnum> Enums { get; internal set; } = RList<REnum>.Empty;

    /// <summary>Relations directly in the package.</summary>
    public RList<RRelation> Relations { get; internal set; } = RList<RRelation>.Empty;
}

/// <summary>A resolved entity.</summary>
public sealed class REntity : RElement
{
    /// <inheritdoc/>
    public override string Kind => "entity";

    /// <summary>Whether the entity is abstract.</summary>
    public bool IsAbstract { get; internal set; }

    /// <summary>The base entity.</summary>
    public REntity? Base { get; internal set; }

    /// <summary>Entities that derive directly from this one.</summary>
    public RList<REntity> Derived { get; internal set; } = RList<REntity>.Empty;

    /// <summary>The flattened attributes (engine-design.md section 7.2): inherited, own, then stereotype virtual attributes, stably sorted by order.</summary>
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The entity's own attributes, in array order.</summary>
    public RList<RAttribute> OwnAttributes { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The primary key (inherited from the root of the hierarchy when the entity has a base).</summary>
    public RKey? Key { get; internal set; }

    /// <summary>Alternate keys.</summary>
    public IReadOnlyList<RAlternateKey> AlternateKeys { get; internal set; } = [];

    /// <summary>Navigation properties generated on this entity.</summary>
    public RList<RNavigation> Navigations { get; internal set; } = RList<RNavigation>.Empty;

    /// <summary>Relations with an end at this entity.</summary>
    public RList<RRelation> Relations { get; internal set; } = RList<RRelation>.Empty;

    /// <summary>Mappings by database name.</summary>
    public IReadOnlyDictionary<string, REntityMapping> Mappings { get; internal set; } = FrozenDictionary<string, REntityMapping>.Empty;

    /// <summary>Whether the entity was generated from a promoted relation.</summary>
    public bool IsPromoted { get; internal set; }

    /// <summary>The relation this entity was promoted from.</summary>
    public RRelation? PromotedFrom { get; internal set; }

    /// <summary>The seeds whose target is the entity, by (name, id).</summary>
    public RList<RSeed> Seeds { get; internal set; } = RList<RSeed>.Empty;
}

/// <summary>A resolved primary key.</summary>
public sealed class RKey
{
    /// <summary>The key attributes, in key order.</summary>
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>The identity strategy: <c>database-identity</c>, <c>sequence</c>, <c>uuid-v7</c>, <c>ulid</c> or <c>application</c>.</summary>
    public string Strategy { get; internal set; } = "application";

    /// <summary>
    /// For the <c>sequence</c> strategy: the key sequence per database name (from the key column's overlay, else synthesized by
    /// the <c>sequenceName</c> convention); empty otherwise. The same sequence is the key column's <see cref="RColumn.Sequence"/>.
    /// </summary>
    public IReadOnlyDictionary<string, RSequence> Sequences { get; internal set; } = FrozenDictionary<string, RSequence>.Empty;
}

/// <summary>A resolved alternate key.</summary>
public sealed class RAlternateKey
{
    /// <summary>The key id.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>The key name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The key attributes, in key order.</summary>
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;
}

/// <summary>A resolved attribute of an entity, value object or relation.</summary>
public sealed class RAttribute : RElement
{
    /// <inheritdoc/>
    public override string Kind => "attribute";

    /// <summary>The entity, value object or relation the attribute appears on.</summary>
    public IResolvedObject Owner { get; internal set; } = null!;

    /// <summary>The entity that declares the attribute, when it is an entity attribute (differs from <see cref="Owner"/> when inherited).</summary>
    public REntity? DeclaringEntity { get; internal set; }

    /// <summary>The resolved type.</summary>
    public RType Type { get; internal set; } = null!;

    /// <summary>Whether a value is required.</summary>
    public bool Required { get; internal set; }

    /// <summary>The literal default, as a plain CLR value.</summary>
    public object? Default { get; internal set; }

    /// <summary>The named default expression.</summary>
    public string? DefaultExpression { get; internal set; }

    /// <summary>The effective length (scalar facets applied).</summary>
    public int? Length { get; internal set; }

    /// <summary>The effective precision.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The effective scale.</summary>
    public int? Scale { get; internal set; }

    /// <summary>Whether the attribute is a collection.</summary>
    public bool Collection { get; internal set; }

    /// <summary>Logical uniqueness intent.</summary>
    public bool Unique { get; internal set; }

    /// <summary>Logical indexing intent.</summary>
    public bool Indexed { get; internal set; }

    /// <summary>Not settable after create.</summary>
    public bool ReadOnly { get; internal set; }

    /// <summary>Never changed after insert.</summary>
    public bool Immutable { get; internal set; }

    /// <summary>The derivation, when derived.</summary>
    public RDerived? Derived { get; internal set; }

    /// <summary><c>pii</c>, <c>secret</c> or <see langword="null"/>.</summary>
    public string? Sensitive { get; internal set; }

    /// <summary>Validation constraints (scalar type constraints merged).</summary>
    public RValidation? Validation { get; internal set; }

    /// <summary>The resolved position.</summary>
    public int Order { get; internal set; }

    /// <summary>Whether the attribute is inherited from a base entity.</summary>
    public bool IsInherited { get; internal set; }

    /// <summary>Whether the attribute is a stereotype's virtual attribute.</summary>
    public bool IsVirtual { get; internal set; }

    /// <summary>The stereotype that adds a virtual attribute.</summary>
    public RStereotype? FromStereotype { get; internal set; }

    /// <summary>How the attribute uses a reference type, when its type is one; otherwise <see langword="null"/>.</summary>
    public RReferenceUsage? Reference { get; internal set; }
}

/// <summary>A resolved derivation.</summary>
public sealed class RDerived
{
    /// <summary>The expression.</summary>
    public string Expression { get; internal set; } = "";

    /// <summary>Whether the computed value is stored.</summary>
    public bool Stored { get; internal set; }
}

/// <summary>Resolved validation constraints, as plain CLR values.</summary>
public sealed class RValidation
{
    /// <summary>The minimum.</summary>
    public object? Min { get; internal set; }

    /// <summary>The maximum.</summary>
    public object? Max { get; internal set; }

    /// <summary>The pattern.</summary>
    public string? Pattern { get; internal set; }

    /// <summary>The allowed values.</summary>
    public IReadOnlyList<object?> AllowedValues { get; internal set; } = [];

    /// <summary>JavaScript rule ids.</summary>
    public IReadOnlyList<string> Rules { get; internal set; } = [];
}

/// <summary>A resolved attribute type.</summary>
public sealed class RType
{
    /// <summary><c>builtin</c>, <c>enum</c>, <c>value-object</c>, <c>scalar</c> or <c>reference</c>.</summary>
    public string Kind { get; internal set; } = "builtin";

    /// <summary>The keyword or the referenced type's name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The effective built-in keyword (a scalar's base); <see langword="null"/> for an enum or value object.</summary>
    public string? Builtin { get; internal set; }

    /// <summary>The enum, when <see cref="Kind"/> is <c>enum</c>.</summary>
    public REnum? Enum { get; internal set; }

    /// <summary>The value object, when <see cref="Kind"/> is <c>value-object</c>.</summary>
    public RValueObject? ValueObject { get; internal set; }

    /// <summary>The custom scalar type, when <see cref="Kind"/> is <c>scalar</c>.</summary>
    public RScalarType? Scalar { get; internal set; }

    /// <summary>The reference type, when <see cref="Kind"/> is <c>reference</c> (<see cref="Builtin"/> is then the code's type).</summary>
    public RReferenceType? ReferenceType { get; internal set; }
}

/// <summary>A resolved enum.</summary>
public sealed class REnum : RElement
{
    /// <inheritdoc/>
    public override string Kind => "enum";

    /// <summary>Whether members are bit flags.</summary>
    public bool Flags { get; internal set; }

    /// <summary>The members, in canonical order.</summary>
    public RList<REnumMember> Members { get; internal set; } = RList<REnumMember>.Empty;
}

/// <summary>A resolved enum member.</summary>
public sealed class REnumMember : RObject
{
    /// <inheritdoc/>
    public override string Kind => "enum-member";

    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The display name, falling back to the name.</summary>
    public string DisplayName { get; internal set; } = "";

    /// <summary>The description text.</summary>
    public string? Description { get; internal set; }

    /// <summary>The integer value.</summary>
    public long? Value { get; internal set; }

    /// <summary>The string code.</summary>
    public string? Code { get; internal set; }

    /// <summary>Custom properties, as plain CLR values.</summary>
    public IReadOnlyDictionary<string, object?> Properties { get; internal set; } = FrozenDictionary<string, object?>.Empty;
}

/// <summary>A resolved value object.</summary>
public sealed class RValueObject : RElement
{
    /// <inheritdoc/>
    public override string Kind => "value-object";

    /// <summary>The attributes (stereotype virtual attributes included), in resolved order.</summary>
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;
}

/// <summary>A resolved custom scalar type.</summary>
public sealed class RScalarType : RElement
{
    /// <inheritdoc/>
    public override string Kind => "scalar-type";

    /// <summary>The built-in base keyword.</summary>
    public string Base { get; internal set; } = "";

    /// <summary>The length facet.</summary>
    public int? Length { get; internal set; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; internal set; }

    /// <summary>Validation constraints.</summary>
    public RValidation? Validation { get; internal set; }
}

/// <summary>A resolved relation.</summary>
public sealed class RRelation : RElement
{
    /// <inheritdoc/>
    public override string Kind => "relation";

    /// <summary>The inverse verb phrase.</summary>
    public string? InverseName { get; internal set; }

    /// <summary><c>association</c>, <c>aggregation</c>, <c>composition</c> or <c>n-ary</c>.</summary>
    public string RelationKind { get; internal set; } = "association";

    /// <summary>The ends, in file order.</summary>
    public RList<REnd> Ends { get; internal set; } = RList<REnd>.Empty;

    /// <summary>The relation's attributes (stereotype virtual attributes included).</summary>
    public RList<RAttribute> Attributes { get; internal set; } = RList<RAttribute>.Empty;

    /// <summary>Whether duplicate links are allowed.</summary>
    public bool AllowDuplicates { get; internal set; }

    /// <summary><c>one-to-one</c>, <c>one-to-many</c>, <c>many-to-many</c> or <c>n-ary</c>.</summary>
    public string Cardinality { get; internal set; } = "";

    /// <summary>Mappings by database name.</summary>
    public IReadOnlyDictionary<string, RRelationMapping> Mappings { get; internal set; } = FrozenDictionary<string, RRelationMapping>.Empty;

    /// <summary>The seeds whose target is the relation, by (name, id).</summary>
    public RList<RSeed> Seeds { get; internal set; } = RList<RSeed>.Empty;
}

/// <summary>A resolved relation end.</summary>
public sealed class REnd : RObject
{
    /// <inheritdoc/>
    public override string Kind => "end";

    /// <summary>The entity at this end.</summary>
    public REntity Entity { get; internal set; } = null!;

    /// <summary>The role name.</summary>
    public string Role { get; internal set; } = "";

    /// <summary>The navigation generated on the opposite entity, or <see langword="null"/>.</summary>
    public string? Navigation { get; internal set; }

    /// <summary>The lower bound (0 or 1).</summary>
    public int Min { get; internal set; }

    /// <summary>The upper bound: <c>"1"</c> or <c>"*"</c>.</summary>
    public string Max { get; internal set; } = "*";

    /// <summary>Whether <see cref="Max"/> is <c>"*"</c>.</summary>
    public bool IsMany { get; internal set; }

    /// <summary><c>none</c>, <c>cascade</c>, <c>restrict</c> or <c>set-null</c>.</summary>
    public string OnDelete { get; internal set; } = "none";

    /// <summary>Whether this end's collection keeps a position.</summary>
    public bool Ordered { get; internal set; }

    /// <summary>The opposite end of a binary relation; <see langword="null"/> for n-ary relations.</summary>
    public REnd? Opposite { get; internal set; }
}

/// <summary>A resolved relation mapping in one database.</summary>
public sealed class RRelationMapping
{
    /// <summary><c>foreign-key</c>, <c>junction</c> or <c>promoted</c>.</summary>
    public string Shape { get; internal set; } = "foreign-key";

    /// <summary>The foreign key, for the <c>foreign-key</c> shape.</summary>
    public RForeignKey? ForeignKey { get; internal set; }

    /// <summary>The junction table, for the <c>junction</c> shape.</summary>
    public RTable? JunctionTable { get; internal set; }

    /// <summary>The generated entity, for the <c>promoted</c> shape.</summary>
    public REntity? PromotedEntity { get; internal set; }

    /// <summary>
    /// Collection attributes typed by a reference type: the resolver maps them to no column and no table, and the templates realize
    /// them as the effective storage strategy calls for (junction table, array column, JSON).
    /// </summary>
    public IReadOnlyList<RAttribute> TemplateDefined { get; internal set; } = [];
}

/// <summary>A resolved navigation property.</summary>
public sealed class RNavigation : RObject
{
    /// <inheritdoc/>
    public override string Kind => "navigation";

    /// <summary>The property name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The relation.</summary>
    public RRelation Relation { get; internal set; } = null!;

    /// <summary>The end at the entity that holds the navigation.</summary>
    public REnd From { get; internal set; } = null!;

    /// <summary>The end the navigation leads to.</summary>
    public REnd To { get; internal set; } = null!;

    /// <summary>The target entity.</summary>
    public REntity Target { get; internal set; } = null!;

    /// <summary>Whether the navigation is a collection.</summary>
    public bool IsCollection { get; internal set; }

    /// <summary>Join paths by database name.</summary>
    public IReadOnlyDictionary<string, RJoinPath> Joins { get; internal set; } = FrozenDictionary<string, RJoinPath>.Empty;

    /// <summary>The join paths the database runs record, by database name, until the resolver publishes them as <see cref="Joins"/>.</summary>
    internal SortedDictionary<string, RJoinPath>? PendingJoins;
}

/// <summary>A resolved entity mapping in one database.</summary>
public sealed class REntityMapping
{
    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary>The table.</summary>
    public RTable Table { get; internal set; } = null!;

    /// <summary><c>tph</c>, <c>tpt</c>, <c>tpc</c> or <see langword="null"/> outside a hierarchy.</summary>
    public string? Inheritance { get; internal set; }

    /// <summary>The discriminator column (TPH).</summary>
    public RColumn? DiscriminatorColumn { get; internal set; }

    /// <summary>The discriminator value.</summary>
    public object? DiscriminatorValue { get; internal set; }

    /// <summary>Attribute path → column, in column order.</summary>
    public IReadOnlyList<RColumnMapping> Columns { get; internal set; } = [];

    /// <summary>Join paths by navigation name.</summary>
    public IReadOnlyDictionary<string, RJoinPath> Joins { get; internal set; } = FrozenDictionary<string, RJoinPath>.Empty;

    /// <summary>Collection attributes typed by a reference type, which no column or table stores: the templates realize them.</summary>
    public IReadOnlyList<RAttribute> TemplateDefined { get; internal set; } = [];
}

/// <summary>One attribute path mapped to a column.</summary>
public sealed class RColumnMapping
{
    /// <summary>The attribute path (<c>&lt;attrId&gt;</c> or <c>&lt;attrId&gt;.&lt;memberAttrId&gt;</c>).</summary>
    public string AttributePath { get; internal set; } = "";

    /// <summary>The column.</summary>
    public RColumn Column { get; internal set; } = null!;
}

/// <summary>A join path between tables.</summary>
public sealed class RJoinPath
{
    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<RJoinStep> Steps { get; internal set; } = [];
}

/// <summary>One join step.</summary>
public sealed class RJoinStep
{
    /// <summary>The table joined from.</summary>
    public RTable FromTable { get; internal set; } = null!;

    /// <summary>The columns joined from.</summary>
    public IReadOnlyList<RColumn> FromColumns { get; internal set; } = [];

    /// <summary>The table joined to.</summary>
    public RTable ToTable { get; internal set; } = null!;

    /// <summary>The columns joined to.</summary>
    public IReadOnlyList<RColumn> ToColumns { get; internal set; } = [];

    /// <summary>Whether the step goes through a junction table.</summary>
    public bool ViaJunction { get; internal set; }
}
