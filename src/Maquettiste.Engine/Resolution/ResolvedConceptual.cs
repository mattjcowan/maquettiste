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

    /// <summary>The packages its <c>dependsOn</c> names, in file order (dangling ids left out); empty when it declares none.</summary>
    public IReadOnlyList<RPackage> DependsOn { get; internal set; } = [];

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

    /// <summary>
    /// The entity checks (next-phase-3-design.md section 3.1): the root base's first, then each level's down to the entity's own, in file
    /// order; a database enforces one only through a binding that lists it.
    /// </summary>
    public RList<REntityCheck> Checks { get; internal set; } = RList<REntityCheck>.Empty;

    /// <summary>Navigation properties generated on this entity.</summary>
    public RList<RNavigation> Navigations { get; internal set; } = RList<RNavigation>.Empty;

    /// <summary>Relations with an end at this entity.</summary>
    public RList<RRelation> Relations { get; internal set; } = RList<RRelation>.Empty;

    /// <summary>Mappings by database name.</summary>
    public IReadOnlyDictionary<string, REntityMapping> Mappings { get; internal set; } = FrozenDictionary<string, REntityMapping>.Empty;

    /// <summary>Bindings by database name (erratum E43): how the entity reads from and writes to each database it is bound to.</summary>
    public IReadOnlyDictionary<string, REntityBinding> Bindings { get; internal set; } = FrozenDictionary<string, REntityBinding>.Empty;

    /// <summary>Whether the entity was generated from a promoted relation.</summary>
    public bool IsPromoted { get; internal set; }

    /// <summary>The relation this entity was promoted from.</summary>
    public RRelation? PromotedFrom { get; internal set; }

    /// <summary>The seeds whose target is the entity, in merge order (package qualified name with none first, name, id).</summary>
    public RList<RSeed> Seeds { get; internal set; } = RList<RSeed>.Empty;

    /// <summary>The lifecycle process the entity names, or <see langword="null"/>.</summary>
    public RProcess? Lifecycle { get; internal set; }

    /// <summary>
    /// The lifecycles that govern the entity: those that list it or a base of it as a subject, in either form, by name then id
    /// (lifecycle-design.md section 7).
    /// </summary>
    public RList<RProcess> Lifecycles { get; internal set; } = RList<RProcess>.Empty;

    /// <summary>
    /// The lifecycles that govern the entity and have trigger points on object, link or own verbs (trigger-points-design.md section 7), by
    /// name then id, whatever those points' <c>entities</c>, which a per-type runner tests at run time. Empty in a project without trigger
    /// points. It keys on the trigger points naming key, and in a project that uses trigger points also on what decides which lifecycles
    /// govern the entity and on each lifecycle it lists.
    /// </summary>
    public RList<RProcess> TriggerLifecycles { get; internal set; } = RList<RProcess>.Empty;

    /// <summary>
    /// The named trigger points that cover it (trigger-points-design.md decisions 33 and 34), in run order: the one whose subject is the
    /// farthest base first, at one depth those an entity brings before those a stereotype brings, then by name. Their <c>entities</c> aside,
    /// which a per-type runner tests at run time. Empty in a project without named trigger points, keyed on the trigger points naming key.
    /// </summary>
    public RList<RTriggerPoint> TriggerPoints { get; internal set; } = RList<RTriggerPoint>.Empty;

    /// <summary>The operations whose subject is the entity, in <see cref="ResolvedModel.Operations"/> order.</summary>
    public RList<ROperation> Operations { get; internal set; } = RList<ROperation>.Empty;

    /// <summary>
    /// What revisions of the entity look like and what a revise does to an instance (revisions-design.md section 8), merged through its
    /// bases; <see langword="null"/> when neither the entity nor a base has a <c>revisions</c> block. A template wanting every type with
    /// revisions filters <c>model.entities</c> by it.
    /// </summary>
    public RRevisions? Revisions { get; internal set; }
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

    /// <summary>
    /// <c>pii</c>, <c>secret</c> or <see langword="null"/>: its own; else the strongest own value of the columns its entity's bindings map it
    /// to (next-phase-2-design.md section 3.2, decision 9).
    /// </summary>
    public string? Sensitive { get; internal set; }

    /// <summary>
    /// The column whose comment <see cref="RAnnotated.Description"/> shows, when the attribute has no description of its own; otherwise
    /// <see langword="null"/> (next-phase-2-design.md section 3.3, decision 12): the first commented column its entity's bindings map
    /// it to, bindings in database name order.
    /// </summary>
    public RColumn? DescriptionColumn { get; internal set; }

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

/// <summary>A resolved derivation: its raw text, or its value written in the expression language.</summary>
public sealed class RDerived
{
    /// <summary>The raw expression, interpreted by templates; empty when the value is written in the expression language.</summary>
    public string Expression { get; internal set; } = "";

    /// <summary>
    /// The value written in the expression language (next-phase-3-design.md section 3.1), typed against the entity's attributes; its
    /// JavaScript computes it from a record. <see langword="null"/> for a raw expression, outside an entity, or when it does not type.
    /// </summary>
    public RExpression? Value { get; internal set; }

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

    /// <summary>Markdown text: why the member is shaped this way, from its own entry; <see langword="null"/> when there is none.</summary>
    public string? Rationale { get; internal set; }

    /// <summary>A URL or a reference (such as <c>ADR-0042</c>) of the decision record; <see langword="null"/> when there is none.</summary>
    public string? Decision { get; internal set; }

    /// <summary>
    /// The status as its JSON value (<c>proposed</c>, <c>approved</c>, <c>deprecated</c> or <c>retired</c>); <see langword="null"/> when
    /// none is written.
    /// </summary>
    public string? Status { get; internal set; }

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

    /// <summary>
    /// The native type per dialect name, as written (a pattern with <c>{length}</c>, <c>{precision}</c> and <c>{scale}</c>), sorted by
    /// dialect name; a column of the type in a database of a listed dialect takes it instead of the base's type map entry.
    /// </summary>
    public IReadOnlyDictionary<string, string> NativeTypes { get; internal set; } = FrozenDictionary<string, string>.Empty;
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

    /// <summary>The seeds whose target is the relation, in merge order (package qualified name with none first, name, id).</summary>
    public RList<RSeed> Seeds { get; internal set; } = RList<RSeed>.Empty;
}

/// <summary>
/// A resolved relation end. A one-type end names one entity (<see cref="Entity"/>); an end with several types (several-types-design.md
/// decision 2: written with <c>anyEntity</c>, or with a <c>types</c> list that holds two or more entries or a stereotype entry) has a null
/// <see cref="Entity"/> and lists its <see cref="Types"/> (section 7).
/// </summary>
public sealed class REnd : RObject
{
    /// <inheritdoc/>
    public override string Kind => "end";

    /// <summary>The entity at a one-type end; <see langword="null"/> on an end with several types, whose <see cref="Types"/> list them.</summary>
    public REntity? Entity { get; internal set; }

    /// <summary>
    /// The entity of a one-type end, for the readers that run on one-type relations only: the database run and promotion skip a relation
    /// with an end of several types, since part 1 stores nothing for it (several-types-design.md decision 22).
    /// </summary>
    internal REntity OneEntity => Entity ?? throw new InvalidOperationException("The end " + Id + " has several types; no database stores its relation in part 1.");

    /// <summary>
    /// The end's types (several-types-design.md decision 5): the entity alone on a one-type end, so a pack can always loop over it; on an end
    /// with several types, the covered entities with no covered base, in the order the entries first reach them. Derived entities are
    /// reached through their base.
    /// </summary>
    public RList<REntity> Types
    {
        // A one-type end's list is made on first read, so a model without such ends allocates nothing more per end.
        get => _types ??= Entity is { } entity ? new RList<REntity>([entity], Dependencies) : RList<REntity>.Empty;
        internal set => _types = value;
    }

    private RList<REntity>? _types;

    /// <summary>Whether the end has several types (several-types-design.md decision 2), read from the file alone.</summary>
    public bool HasSeveralTypes { get; internal set; }

    /// <summary>The stereotypes the end's <c>types</c> entries name, in entry order (a key that names no stereotype is left out); empty on a one-type end.</summary>
    public RList<RStereotypeDefinition> Stereotypes { get; internal set; } = RList<RStereotypeDefinition>.Empty;

    /// <summary>Whether the end may be any entity of the model (<c>anyEntity</c>); <see langword="false"/> otherwise.</summary>
    public bool AnyEntity { get; internal set; }

    /// <summary>
    /// The nearest entity every one of <see cref="Types"/> is or derives from (several-types-design.md decision 19): the entity itself on a
    /// one-type end, a shared base when there is one, else <see langword="null"/>. A pack that stores links as one key to a shared base reads it.
    /// </summary>
    public REntity? CommonBase
    {
        get => HasSeveralTypes ? _commonBase : Entity;
        internal set => _commonBase = value;
    }

    private REntity? _commonBase;

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

    /// <summary>
    /// For a relation with an end of several types whose mapping states storage (several-types-design.md section 19.8): one entry per pair
    /// of targets, one target per end, in target order (decision 39), with the table that holds the link and how it points at each end.
    /// For a one-type relation whose designed link table's mapping writes constants or attribute columns (answer 9): its one link, each end
    /// pointed at by the key its entry names, since a key a shared link table's relations all use names the end of only one of them.
    /// Empty for every other relation.
    /// </summary>
    public IReadOnlyList<RRelationLink> Links { get; internal set; } = [];

    /// <summary>
    /// The link table's columns with a fixed value (decision 44), in file order: each a filter on the links the mapping reads and the value
    /// it sets on every link it inserts. Empty when the mapping writes no <c>constants</c>.
    /// </summary>
    public IReadOnlyList<RColumnCondition> Constants { get; internal set; } = [];

    /// <summary>
    /// The relation attributes' columns in the one table that holds every link (decision 45), in file order. Empty when the mapping names no
    /// attribute column.
    /// </summary>
    public IReadOnlyList<RRelationAttributeColumn> Attributes { get; internal set; } = [];

    /// <summary>
    /// Covered entities not placed in the database, by end in file order, then qualified name, then id (decision 39); no finding speaks of
    /// them. Empty for a relation whose ends each have one type.
    /// </summary>
    public IReadOnlyList<REndEntity> LeftOut { get; internal set; } = [];

    /// <summary>Placed targets no pointer of the mapping reaches (MQ4120), in the order of <see cref="LeftOut"/>.</summary>
    public IReadOnlyList<REndEntity> NotStored { get; internal set; } = [];

    /// <summary>The code of every entity a type column of the mapping holds (decision 42), by qualified name, then id.</summary>
    public IReadOnlyList<RTypeCode> TypeCodes { get; internal set; } = [];
}

/// <summary>
/// Where one pair of targets of a relation with an end of several types is linked in a database (several-types-design.md section 19.8): the
/// table that holds the link and how it points at each end's target.
/// </summary>
public sealed class RRelationLink
{
    /// <summary>The table that holds the link: a link table, or the table of the target whose rows hold it.</summary>
    public RTable Table { get; internal set; } = null!;

    /// <summary>How the holding table points at each end's target, one per relation end, in file order.</summary>
    public IReadOnlyList<RRelationLinkEnd> Ends { get; internal set; } = [];
}

/// <summary>How the table that holds a link points at one end's target (several-types-design.md section 19.8).</summary>
public sealed class RRelationLinkEnd
{
    /// <summary>The relation end.</summary>
    public REnd End { get; internal set; } = null!;

    /// <summary>The target at this end: a covered entity placed in the database whose rows aren't in a covered base's table (decision 39).</summary>
    public REntity Type { get; internal set; } = null!;

    /// <summary>Whether this end's rows hold the link: the holding table is this target's table, so the end has no pointer of its own.</summary>
    public bool IsHolder { get; internal set; }

    /// <summary>The pointer's columns in the holding table; empty on the end that holds the link.</summary>
    public IReadOnlyList<RColumn> Columns { get; internal set; } = [];

    /// <summary>The table the pointer refers to (as <see cref="RForeignKey.ReferencedTable"/>); <see langword="null"/> on the end that holds the link.</summary>
    public RTable? ReferencedTable { get; internal set; }

    /// <summary>The columns the pointer refers to, in the order of <see cref="Columns"/>; empty on the end that holds the link.</summary>
    public IReadOnlyList<RColumn> ReferencedColumns { get; internal set; } = [];

    /// <summary>The foreign key, when the pointer is one; <see langword="null"/> for an id column.</summary>
    public RForeignKey? ForeignKey { get; internal set; }

    /// <summary>The column that holds the linked object's type code, when the entry names one; else <see langword="null"/>.</summary>
    public RColumn? TypeColumn { get; internal set; }

    /// <summary>
    /// The codes <see cref="TypeColumn"/> holds for this target (decision 42): its own and those of its covered derived entities whose rows
    /// are in its table that can have objects, sorted ordinally; each a string or an integer. Empty without a type column.
    /// </summary>
    public IReadOnlyList<object> Codes { get; internal set; } = [];
}

/// <summary>A column and the values it holds: a condition a join step or a link table's rows meet (several-types-design.md decisions 44 and 48).</summary>
public sealed class RColumnCondition
{
    /// <summary>The column.</summary>
    public RColumn Column { get; internal set; } = null!;

    /// <summary>The values the column holds, any one of them: strings, numbers or booleans; <see langword="null"/> is SQL NULL.</summary>
    public IReadOnlyList<object?> Values { get; internal set; } = [];
}

/// <summary>A relation attribute and its column in the table that holds every link (several-types-design.md decision 45).</summary>
public sealed class RRelationAttributeColumn
{
    /// <summary>The relation attribute.</summary>
    public RAttribute Attribute { get; internal set; } = null!;

    /// <summary>The column.</summary>
    public RColumn Column { get; internal set; } = null!;
}

/// <summary>A covered entity at one end of a relation stored in a database (several-types-design.md section 19.8).</summary>
public sealed class REndEntity
{
    /// <summary>The relation end.</summary>
    public REnd End { get; internal set; } = null!;

    /// <summary>The entity.</summary>
    public REntity Entity { get; internal set; } = null!;
}

/// <summary>An entity's type code in a database (several-types-design.md decision 42).</summary>
public sealed class RTypeCode
{
    /// <summary>The entity.</summary>
    public REntity Entity { get; internal set; } = null!;

    /// <summary>
    /// The code: the <c>discriminatorValue</c> of the entity's binding to the database, else of its mapping there, else its name; a string
    /// or an integer.
    /// </summary>
    public object Code { get; internal set; } = null!;
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

    /// <summary>
    /// The end at the entity that holds the navigation. A navigation held through an end with several types is one object, listed in the
    /// navigations of each of that end's types; its <c>From.Entity</c> is <see langword="null"/>.
    /// </summary>
    public REnd From { get; internal set; } = null!;

    /// <summary>The end the navigation leads to.</summary>
    public REnd To { get; internal set; } = null!;

    /// <summary>The target entity; <see langword="null"/> when the navigation leads to an end with several types, whose types <see cref="Targets"/> lists.</summary>
    public REntity? Target { get; internal set; }

    /// <summary>The types of the end it leads to (<see cref="REnd.Types"/>): the target alone, or that end's types when it has several.</summary>
    public RList<REntity> Targets => To.Types;

    /// <summary>Whether the navigation is a collection.</summary>
    public bool IsCollection { get; internal set; }

    /// <summary>
    /// Join paths by database name. For a relation with an end of several types, set only where the database has exactly one path for the
    /// navigation (several-types-design.md decision 48); <see cref="TypeJoins"/> holds every path.
    /// </summary>
    public IReadOnlyDictionary<string, RJoinPath> Joins { get; internal set; } = FrozenDictionary<string, RJoinPath>.Empty;

    /// <summary>The join paths the database runs record, by database name, until the resolver publishes them as <see cref="Joins"/>.</summary>
    internal SortedDictionary<string, RJoinPath>? PendingJoins;

    /// <summary>
    /// For a navigation of a relation with an end of several types (several-types-design.md decision 48): by database name, the path of every
    /// pair of targets, ordered by the target at the navigation's from end, then at its to end, each in target order. Empty otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RJoinPath>> TypeJoins { get; internal set; } = FrozenDictionary<string, IReadOnlyList<RJoinPath>>.Empty;

    /// <summary>The paths per pair the database runs record, by database name, until the resolver publishes them as <see cref="TypeJoins"/>.</summary>
    internal SortedDictionary<string, IReadOnlyList<RJoinPath>>? PendingTypeJoins;
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

    /// <summary>
    /// By navigation name, the paths of a navigation of the entity that leads to an end with several types, one per pair of targets
    /// (several-types-design.md decision 48), in the order of <see cref="RNavigation.TypeJoins"/>; also of a navigation with more than one path
    /// from the entity, such as to a one-type end over an abstract TPC root, which <see cref="Joins"/> can't hold. Empty otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RJoinPath>> TypeJoins { get; internal set; } = FrozenDictionary<string, IReadOnlyList<RJoinPath>>.Empty;

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
public sealed class RJoinPath : ITrackedValue
{
    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<RJoinStep> Steps { get; internal set; } = [];

    /// <summary>
    /// What a path of a relation stored as several types reads (several-types-design.md section 19.8, "Dependency keys"): the relation's storage
    /// keys and the files that pick a target's rows; recorded when a unit reads the path. Empty on every other path, whose holder's keys cover it.
    /// </summary>
    internal IReadOnlyList<string> Keys { get; set; } = [];

    IReadOnlyList<string> ITrackedValue.Dependencies => Keys;

    /// <summary>The target at the path's from end, set only when that end has several types (several-types-design.md decision 48).</summary>
    public REntity? FromType { get; internal set; }

    /// <summary>The target at the path's to end, set only when that end has several types (several-types-design.md decision 48).</summary>
    public REntity? ToType { get; internal set; }
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

    /// <summary>
    /// What the step's rows must also meet (several-types-design.md decision 48): a type column's codes, a link table's constants, or what
    /// picks a target's rows in a table shared with other entities. Empty on a path of a relation whose ends each have one type.
    /// </summary>
    public IReadOnlyList<RColumnCondition> Conditions { get; internal set; } = [];
}
