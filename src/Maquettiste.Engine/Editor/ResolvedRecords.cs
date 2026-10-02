using System.Collections;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>What <see cref="GenerationService.GetResolvedAsync"/> reads.</summary>
/// <param name="Scope">One of <see cref="ResolvedRecords.Scopes"/>: <c>all</c> (every kind but <c>tables</c>, <c>routines</c>,
/// <c>database-types</c> and <c>sql-objects</c>, which <c>databases</c> already holds), or one kind's plural, such as <c>entities</c> or
/// <c>databases</c>.</param>
/// <param name="Database">A database id: entities and relations mapped (not ignored) in it, that database, and its tables only; the
/// other kinds are not narrowed.</param>
/// <param name="Cursor">The previous page's <see cref="ResolvedPage.Next"/>, or <see langword="null"/> for the first page.</param>
/// <param name="Limit">The page size, 1 to <see cref="ModelPages.MaxLimit"/>.</param>
public sealed record ResolvedQuery(string Scope = "all", string? Database = null, string? Cursor = null, int Limit = ModelPages.DefaultLimit);

/// <summary>One page of the resolved model as flat records (resolved objects reference each other, so other objects appear by id).</summary>
/// <param name="Items">The records, by (kind, name, id): <see cref="PackageRecord"/>, <see cref="EntityRecord"/>, <see cref="RelationRecord"/>,
/// <see cref="EnumRecord"/>, <see cref="ValueObjectRecord"/>, <see cref="ScalarTypeRecord"/>, <see cref="ReferenceTypeRecord"/>,
/// <see cref="SeedRecord"/>, <see cref="ProcessRecord"/>, <see cref="ActorRecord"/>, <see cref="ScenarioRecord"/>,
/// <see cref="DatabaseRecord"/>, <see cref="TableRecord"/>, <see cref="RoutineRecord"/>, <see cref="DatabaseTypeRecord"/> or
/// <see cref="SqlObjectRecord"/>; each has <c>id</c>, <c>kind</c> and <c>name</c>.</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> on the last one.</param>
/// <param name="Diagnostics">When the model has errors, the errors that prevented resolution (and <paramref name="Items"/> is empty);
/// otherwise empty (validate reports the warnings).</param>
public sealed record ResolvedPage(IReadOnlyList<object> Items, string? Next, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>A resolved package.</summary>
/// <param name="Id">The package id.</param>
/// <param name="Kind"><c>package</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The parent package's id, or <see langword="null"/> at the root.</param>
/// <param name="QualifiedName">The names from the root, joined with dots.</param>
/// <param name="Children">The child packages' ids.</param>
/// <param name="Entities">The entities' ids.</param>
/// <param name="ValueObjects">The value objects' ids.</param>
/// <param name="Enums">The enums' ids.</param>
/// <param name="Relations">The relations' ids.</param>
/// <param name="DisplayName">The display name (the name when none is set).</param>
/// <param name="PluralName">The plural name (the inflector's when none is set).</param>
/// <param name="Description">The description text, a sidecar file loaded, or <see langword="null"/>.</param>
/// <param name="Tags">The tag keys.</param>
/// <param name="Category">The category's id, or <see langword="null"/>.</param>
/// <param name="Stereotypes">The stereotype keys, in application order.</param>
/// <param name="Properties">The custom properties: stereotype defaults merged under the element's own.</param>
/// <param name="Generation">The generation hints, by pack name or <c>"*"</c>.</param>
public sealed record PackageRecord(string Id, string Kind, string Name, string? Package, string QualifiedName, IReadOnlyList<string> Children,
    IReadOnlyList<string> Entities, IReadOnlyList<string> ValueObjects, IReadOnlyList<string> Enums, IReadOnlyList<string> Relations,
    string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category, IReadOnlyList<string> Stereotypes,
    IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A resolved entity: its attributes resolved (inherited ones included and marked), keys, inheritance, relations and mappings.</summary>
/// <param name="Id">The entity id (a promoted relation's entity has the relation's id).</param>
/// <param name="Kind"><c>entity</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id.</param>
/// <param name="IsAbstract">Whether the entity is abstract.</param>
/// <param name="Base">The base entity's id.</param>
/// <param name="Derived">The ids of the entities that derive from it directly.</param>
/// <param name="Attributes">Every attribute, the base's first (marked <c>isInherited</c>), then the stereotypes', then its own.</param>
/// <param name="Key">The primary key, or <see langword="null"/>.</param>
/// <param name="AlternateKeys">The alternate keys.</param>
/// <param name="Navigations">The navigations to related entities.</param>
/// <param name="Relations">The ids of the relations with an end at this entity.</param>
/// <param name="Mappings">One mapping per database the entity is mapped to (not ignored in), by database name.</param>
/// <param name="IsPromoted">Whether the entity is a promoted relation.</param>
/// <param name="PromotedFrom">The promoted relation's id.</param>
/// <param name="Seeds">The ids of the seeds that hold its rows.</param>
/// <param name="Lifecycle">The id of the process that is its lifecycle.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record EntityRecord(string Id, string Kind, string Name, string? Package, bool IsAbstract, string? Base, IReadOnlyList<string> Derived,
    IReadOnlyList<AttributeRecord> Attributes, KeyRecord? Key, IReadOnlyList<AlternateKeyRecord> AlternateKeys, IReadOnlyList<NavigationRecord> Navigations,
    IReadOnlyList<string> Relations, IReadOnlyList<EntityMappingRecord> Mappings, bool IsPromoted, string? PromotedFrom, IReadOnlyList<string> Seeds,
    string? Lifecycle, string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A resolved attribute of an entity, value object, relation, process context or event payload.</summary>
/// <param name="Id">The attribute id.</param>
/// <param name="Name">The name.</param>
/// <param name="Owner">The id of the element that holds it (for an inherited attribute, the entity that inherits it).</param>
/// <param name="DeclaringEntity">The id of the entity that declares it, for an entity attribute.</param>
/// <param name="Type">The resolved type.</param>
/// <param name="Required">Whether a value is required.</param>
/// <param name="Default">The default value, as a plain value (an enum member by name).</param>
/// <param name="DefaultExpression">The default expression.</param>
/// <param name="Length">The effective length.</param>
/// <param name="Precision">The effective precision.</param>
/// <param name="Scale">The effective scale.</param>
/// <param name="Collection">Whether the attribute holds a list.</param>
/// <param name="Unique">Whether values are unique.</param>
/// <param name="Indexed">Whether the attribute is indexed.</param>
/// <param name="ReadOnly">Whether the attribute is read-only.</param>
/// <param name="Immutable">Whether the attribute cannot change after creation.</param>
/// <param name="Derived">The derivation, or <see langword="null"/>.</param>
/// <param name="Sensitive">The sensitivity class, or <see langword="null"/>.</param>
/// <param name="Validation">The validation, or <see langword="null"/>.</param>
/// <param name="Order">The 0-based position in its owner's list.</param>
/// <param name="IsKey">Whether the attribute is part of its entity's primary key.</param>
/// <param name="IsInherited">Whether the attribute comes from a base entity.</param>
/// <param name="IsVirtual">Whether the attribute is virtual (not stored).</param>
/// <param name="FromStereotype">The key of the stereotype that adds the attribute, or <see langword="null"/>.</param>
/// <param name="Reference">For an attribute typed by a reference type: how it uses it.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record AttributeRecord(string Id, string Name, string Owner, string? DeclaringEntity, TypeRecord Type, bool Required, object? Default,
    string? DefaultExpression, int? Length, int? Precision, int? Scale, bool Collection, bool Unique, bool Indexed, bool ReadOnly, bool Immutable,
    DerivedRecord? Derived, string? Sensitive, ValidationRecord? Validation, int Order, bool IsKey, bool IsInherited, bool IsVirtual, string? FromStereotype,
    ReferenceUsageRecord? Reference, string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>An attribute's resolved type.</summary>
/// <param name="Kind"><c>builtin</c>, <c>enum</c>, <c>value-object</c>, <c>scalar</c> or <c>reference</c>.</param>
/// <param name="Name">The type's name as written.</param>
/// <param name="Builtin">The built-in type keyword at the bottom (a scalar type's base, for example).</param>
/// <param name="Enum">The enum's id.</param>
/// <param name="ValueObject">The value object's id.</param>
/// <param name="Scalar">The scalar type's id.</param>
/// <param name="ReferenceType">The reference type's id.</param>
public sealed record TypeRecord(string Kind, string Name, string? Builtin, string? Enum, string? ValueObject, string? Scalar, string? ReferenceType);

/// <summary>A derived attribute's expression.</summary>
/// <param name="Expression">The expression.</param>
/// <param name="Stored">Whether the value is stored.</param>
public sealed record DerivedRecord(string Expression, bool Stored);

/// <summary>Validation of an attribute or scalar type.</summary>
/// <param name="Min">The minimum.</param>
/// <param name="Max">The maximum.</param>
/// <param name="Pattern">The pattern.</param>
/// <param name="AllowedValues">The allowed values.</param>
/// <param name="Rules">The named rules.</param>
public sealed record ValidationRecord(object? Min, object? Max, string? Pattern, IReadOnlyList<object?> AllowedValues, IReadOnlyList<string> Rules);

/// <summary>How an attribute uses its reference type.</summary>
/// <param name="Type">The reference type's id.</param>
/// <param name="IsCollection">Whether the attribute holds many codes.</param>
/// <param name="Required">Whether a code is required.</param>
/// <param name="Allowed">The ids of the rows the attribute allows (all when the list is empty).</param>
/// <param name="DefaultRow">The id of the default row.</param>
/// <param name="Storage">The effective storage choice per database.</param>
public sealed record ReferenceUsageRecord(string Type, bool IsCollection, bool Required, IReadOnlyList<string> Allowed, string? DefaultRow,
    IReadOnlyList<StorageChoiceRecord> Storage);

/// <summary>The effective storage of a reference type in one database.</summary>
/// <param name="Database">The database's id.</param>
/// <param name="Strategy">The project's strategy key, or <see langword="null"/> for template-defined.</param>
/// <param name="Options">The choice's options.</param>
/// <param name="Source">Where the choice came from: <c>type</c>, <c>database</c>, <c>project</c>, or <see langword="null"/>.</param>
/// <param name="Description">The strategy's description.</param>
/// <param name="Collections">Whether the strategy supports collections in the database's dialect.</param>
public sealed record StorageChoiceRecord(string Database, string? Strategy, IReadOnlyDictionary<string, object?> Options, string? Source, string? Description, bool Collections);

/// <summary>An entity's primary key.</summary>
/// <param name="Attributes">The key attributes' ids, in key order.</param>
/// <param name="Strategy"><c>application</c>, <c>identity</c>, <c>sequence</c>, ...</param>
/// <param name="Sequences">For the <c>sequence</c> strategy, the key sequence per database.</param>
public sealed record KeyRecord(IReadOnlyList<string> Attributes, string Strategy, IReadOnlyList<KeySequenceRecord> Sequences);

/// <summary>The key sequence of an entity in one database.</summary>
/// <param name="Database">The database's id.</param>
/// <param name="Sequence">The sequence's id or synthesized key, as <see cref="SequenceView.Id"/>.</param>
public sealed record KeySequenceRecord(string Database, string Sequence);

/// <summary>An alternate key.</summary>
/// <param name="Id">The key id.</param>
/// <param name="Name">The name.</param>
/// <param name="Attributes">The attributes' ids, in key order.</param>
public sealed record AlternateKeyRecord(string Id, string Name, IReadOnlyList<string> Attributes);

/// <summary>A navigation from an entity across a relation.</summary>
/// <param name="Id">The navigation id (the end it starts from).</param>
/// <param name="Name">The navigation name.</param>
/// <param name="Relation">The relation's id.</param>
/// <param name="From">The id of the end at this entity.</param>
/// <param name="To">The id of the end it leads to.</param>
/// <param name="Target">The id of the entity it leads to.</param>
/// <param name="IsCollection">Whether it leads to many.</param>
public sealed record NavigationRecord(string Id, string Name, string Relation, string From, string To, string Target, bool IsCollection);

/// <summary>An entity's mapping to one database.</summary>
/// <param name="Database">The database's id.</param>
/// <param name="Table">The table's key, as <see cref="TableView.Key"/>.</param>
/// <param name="Inheritance">The inheritance strategy, or <see langword="null"/>.</param>
/// <param name="DiscriminatorColumn">The discriminator column's key.</param>
/// <param name="DiscriminatorValue">The discriminator value.</param>
/// <param name="Columns">Which column stores each attribute path.</param>
public sealed record EntityMappingRecord(string Database, string Table, string? Inheritance, string? DiscriminatorColumn, object? DiscriminatorValue,
    IReadOnlyList<ColumnMappingRecord> Columns);

/// <summary>The column that stores an attribute path.</summary>
/// <param name="AttributePath">The attribute path (a value object's members are dotted).</param>
/// <param name="Column">The column's key, as <see cref="ColumnView.Key"/>.</param>
public sealed record ColumnMappingRecord(string AttributePath, string Column);

/// <summary>A resolved relation.</summary>
/// <param name="Id">The relation id.</param>
/// <param name="Kind"><c>relation</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id.</param>
/// <param name="InverseName">The inverse name.</param>
/// <param name="RelationKind"><c>association</c>, <c>aggregation</c> or <c>composition</c>.</param>
/// <param name="Cardinality"><c>one-to-one</c>, <c>one-to-many</c>, <c>many-to-many</c> or <c>n-ary</c>.</param>
/// <param name="AllowDuplicates">Whether the same pair may occur more than once.</param>
/// <param name="Ends">The ends, in document order.</param>
/// <param name="Attributes">The relation's own attributes.</param>
/// <param name="Mappings">One mapping per database the relation is mapped to.</param>
/// <param name="Seeds">The ids of the seeds that hold its rows.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record RelationRecord(string Id, string Kind, string Name, string? Package, string? InverseName, string RelationKind, string Cardinality,
    bool AllowDuplicates, IReadOnlyList<EndRecord> Ends, IReadOnlyList<AttributeRecord> Attributes, IReadOnlyList<RelationMappingRecord> Mappings,
    IReadOnlyList<string> Seeds, string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A relation end.</summary>
/// <param name="Id">The end id.</param>
/// <param name="Entity">The entity's id.</param>
/// <param name="Role">The role name.</param>
/// <param name="Navigation">The navigation name, or <see langword="null"/>.</param>
/// <param name="Min">The lower bound.</param>
/// <param name="Max">The upper bound, a number or <c>*</c>.</param>
/// <param name="IsMany">Whether the upper bound is above one.</param>
/// <param name="OnDelete">What deleting the other side does.</param>
/// <param name="Ordered">Whether the end is ordered.</param>
/// <param name="Opposite">The opposite end's id, for a binary relation.</param>
public sealed record EndRecord(string Id, string Entity, string Role, string? Navigation, int Min, string Max, bool IsMany, string OnDelete, bool Ordered, string? Opposite);

/// <summary>A relation's mapping to one database.</summary>
/// <param name="Database">The database's id.</param>
/// <param name="Shape"><c>foreign-key</c>, <c>junction</c> or <c>promoted</c>.</param>
/// <param name="ForeignKey">The foreign key's name, for the <c>foreign-key</c> shape.</param>
/// <param name="JunctionTable">The junction table's key, for the <c>junction</c> shape.</param>
/// <param name="PromotedEntity">The promoted entity's id, for the <c>promoted</c> shape.</param>
public sealed record RelationMappingRecord(string Database, string Shape, string? ForeignKey, string? JunctionTable, string? PromotedEntity);

/// <summary>A resolved enum.</summary>
/// <param name="Id">The enum id.</param>
/// <param name="Kind"><c>enum</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id.</param>
/// <param name="Flags">Whether members combine as flags.</param>
/// <param name="Members">The members, in order.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record EnumRecord(string Id, string Kind, string Name, string? Package, bool Flags, IReadOnlyList<EnumMemberRecord> Members,
    string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category, IReadOnlyList<string> Stereotypes,
    IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>An enum member.</summary>
/// <param name="Id">The member id.</param>
/// <param name="Name">The name.</param>
/// <param name="DisplayName">The display name (the name when none is set).</param>
/// <param name="Description">The description.</param>
/// <param name="Value">The numeric value.</param>
/// <param name="Code">The code.</param>
/// <param name="Properties">The custom properties.</param>
public sealed record EnumMemberRecord(string Id, string Name, string DisplayName, string? Description, long? Value, string? Code, IReadOnlyDictionary<string, object?> Properties);

/// <summary>A resolved value object.</summary>
/// <param name="Id">The value object id.</param>
/// <param name="Kind"><c>value-object</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id.</param>
/// <param name="Attributes">The attributes.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record ValueObjectRecord(string Id, string Kind, string Name, string? Package, IReadOnlyList<AttributeRecord> Attributes,
    string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category, IReadOnlyList<string> Stereotypes,
    IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A resolved custom scalar type.</summary>
/// <param name="Id">The scalar type id.</param>
/// <param name="Kind"><c>scalar-type</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id.</param>
/// <param name="Base">The built-in base type keyword.</param>
/// <param name="Length">The length.</param>
/// <param name="Precision">The precision.</param>
/// <param name="Scale">The scale.</param>
/// <param name="Validation">The validation, or <see langword="null"/>.</param>
/// <param name="NativeTypes">The native type per dialect name, as written (sorted by dialect name; empty when none).</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record ScalarTypeRecord(string Id, string Kind, string Name, string? Package, string Base, int? Length, int? Precision, int? Scale,
    ValidationRecord? Validation, IReadOnlyDictionary<string, string> NativeTypes, string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A resolved reference type with its rows (every seed's rows merged).</summary>
/// <param name="Id">The reference type id.</param>
/// <param name="Kind"><c>reference-type</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id, or <see langword="null"/>.</param>
/// <param name="Code">The code field.</param>
/// <param name="Label">The label field.</param>
/// <param name="Attributes">The user fields.</param>
/// <param name="Rows">The rows, in order.</param>
/// <param name="Seeds">The ids of its seeds.</param>
/// <param name="UsedBy">The ids of the attributes typed by it.</param>
/// <param name="Storage">The effective storage choice per database.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record ReferenceTypeRecord(string Id, string Kind, string Name, string? Package, ReferenceFieldRecord Code, ReferenceFieldRecord Label,
    IReadOnlyList<AttributeRecord> Attributes, IReadOnlyList<RowRecord> Rows, IReadOnlyList<string> Seeds, IReadOnlyList<string> UsedBy,
    IReadOnlyList<StorageChoiceRecord> Storage, string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>The code or label field of a reference type.</summary>
/// <param name="Id">The field id.</param>
/// <param name="Type">The type keyword.</param>
/// <param name="Length">The length.</param>
/// <param name="Pattern">The pattern.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
public sealed record ReferenceFieldRecord(string Id, string Type, int? Length, string? Pattern, string DisplayName, string? Description);

/// <summary>A row of a reference type.</summary>
/// <param name="Id">The row id.</param>
/// <param name="Code">The code.</param>
/// <param name="Label">The label.</param>
/// <param name="Description">The description.</param>
/// <param name="Values">The user fields by name (a reference-typed field holds its code or codes).</param>
/// <param name="Seed">The id of the seed that holds it.</param>
/// <param name="Order">The 0-based position among the type's rows.</param>
public sealed record RowRecord(string Id, object? Code, string? Label, string? Description, IReadOnlyDictionary<string, object?> Values, string Seed, int Order);

/// <summary>A resolved seed.</summary>
/// <param name="Id">The seed id.</param>
/// <param name="Kind"><c>seed</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id, or <see langword="null"/>.</param>
/// <param name="Target">The id of the entity, relation or reference type it holds rows for.</param>
/// <param name="Columns">The columns.</param>
/// <param name="Rows">The rows, in order.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record SeedRecord(string Id, string Kind, string Name, string? Package, string? Target, IReadOnlyList<SeedColumnRecord> Columns,
    IReadOnlyList<SeedRowRecord> Rows, string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A seed column.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Kind"><c>builtin</c>, <c>attribute</c> or <c>end</c>.</param>
/// <param name="Attribute">The attribute's id, for an attribute column.</param>
/// <param name="End">The relation end's id, for an end column.</param>
public sealed record SeedColumnRecord(string Name, string Kind, string? Attribute, string? End);

/// <summary>A seed row.</summary>
/// <param name="Id">The row id.</param>
/// <param name="Order">The 0-based position.</param>
/// <param name="Values">The cells by column name (an enum cell by member name, a reference cell as its code, an end cell as a row id).</param>
public sealed record SeedRowRecord(string Id, int Order, IReadOnlyDictionary<string, object?> Values);

/// <summary>A resolved process: every state at every depth, transitions, events, guards, actions, gates and invokes, other objects by id.</summary>
/// <param name="Id">The process id.</param>
/// <param name="Kind"><c>process</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id.</param>
/// <param name="Use"><c>lifecycle</c> or <c>orchestration</c>.</param>
/// <param name="Subject">The id of the entity whose lifecycle it is.</param>
/// <param name="BoundAttribute">The id of the subject's attribute that holds the state.</param>
/// <param name="BoundEnum">The id of the enum whose members are the root-level states.</param>
/// <param name="Initial">The initial state's id.</param>
/// <param name="Context">The context attributes.</param>
/// <param name="States">Every state at every depth, in document order; <c>parent</c> and <c>children</c> give the tree.</param>
/// <param name="Transitions">Every transition.</param>
/// <param name="Events">The events.</param>
/// <param name="Guards">The guards.</param>
/// <param name="Actions">The actions.</param>
/// <param name="Gates">The approval gates.</param>
/// <param name="Invokes">The invocations.</param>
/// <param name="Actors">The ids of the actors that take part.</param>
/// <param name="Scenarios">The ids of its scenarios.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record ProcessRecord(string Id, string Kind, string Name, string? Package, string Use, string? Subject, string? BoundAttribute,
    string? BoundEnum, string? Initial, IReadOnlyList<AttributeRecord> Context, IReadOnlyList<StateRecord> States, IReadOnlyList<TransitionRecord> Transitions,
    IReadOnlyList<EventRecord> Events, IReadOnlyList<GuardRecord> Guards, IReadOnlyList<ActionRecord> Actions, IReadOnlyList<GateRecord> Gates,
    IReadOnlyList<InvokeRecord> Invokes, IReadOnlyList<string> Actors, IReadOnlyList<string> Scenarios, string DisplayName, string PluralName,
    string? Description, IReadOnlyList<string> Tags, string? Category, IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A state of a process.</summary>
/// <param name="Id">The state id.</param>
/// <param name="Name">The name.</param>
/// <param name="Path">The names from the root, joined with dots.</param>
/// <param name="Type"><c>atomic</c>, <c>compound</c>, <c>parallel</c>, <c>final</c> or <c>history</c>.</param>
/// <param name="Parent">The parent state's id.</param>
/// <param name="Children">The child states' ids.</param>
/// <param name="Initial">The initial child's id.</param>
/// <param name="History"><c>shallow</c> or <c>deep</c>, for a history state.</param>
/// <param name="DefaultTarget">A history state's default target's id.</param>
/// <param name="Entry">The entry actions' ids.</param>
/// <param name="Exit">The exit actions' ids.</param>
/// <param name="Invoke">The ids of the invocations that run while the state is active.</param>
/// <param name="IsFinal">Whether the state is final.</param>
/// <param name="IsAtomic">Whether the state has no children.</param>
/// <param name="Depth">The depth (0 at the root).</param>
/// <param name="BoundMember">The id of the enum member a lifecycle's root-level state stands for.</param>
/// <param name="TransitionsOut">The ids of the transitions that leave the state.</param>
/// <param name="RegionIndex">The region's index under a parallel parent.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Stereotypes">The stereotype keys.</param>
/// <param name="Properties">The custom properties.</param>
public sealed record StateRecord(string Id, string Name, string Path, string Type, string? Parent, IReadOnlyList<string> Children, string? Initial,
    string? History, string? DefaultTarget, IReadOnlyList<string> Entry, IReadOnlyList<string> Exit, IReadOnlyList<string> Invoke, bool IsFinal, bool IsAtomic,
    int Depth, string? BoundMember, IReadOnlyList<string> TransitionsOut, int? RegionIndex, string DisplayName, string? Description,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties);

/// <summary>A transition of a process.</summary>
/// <param name="Id">The transition id.</param>
/// <param name="Source">The source state's id.</param>
/// <param name="Targets">The target states' ids.</param>
/// <param name="Trigger"><c>event</c>, <c>after</c>, <c>done</c>, <c>error</c> or <c>always</c>.</param>
/// <param name="Event">The event's id.</param>
/// <param name="After">The delay as written.</param>
/// <param name="AfterMs">The delay in milliseconds.</param>
/// <param name="AfterTicks">The delay in ticks.</param>
/// <param name="Invoke">The invocation whose completion or failure triggers it.</param>
/// <param name="Guard">The guard's id.</param>
/// <param name="GuardMissing">Whether the guard named is not declared.</param>
/// <param name="Actions">The actions' ids.</param>
/// <param name="External">Whether the transition exits and re-enters its source.</param>
/// <param name="Gate">The approval gate's id.</param>
/// <param name="Label">The label the canvas shows.</param>
/// <param name="IsTargetless">Whether the transition has no target.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Stereotypes">The stereotype keys.</param>
/// <param name="Properties">The custom properties.</param>
public sealed record TransitionRecord(string Id, string Source, IReadOnlyList<string> Targets, string Trigger, string? Event, string? After, long? AfterMs,
    long? AfterTicks, string? Invoke, string? Guard, bool GuardMissing, IReadOnlyList<string> Actions, bool External, string? Gate, string Label,
    bool IsTargetless, string DisplayName, string? Description, IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties);

/// <summary>An event of a process.</summary>
/// <param name="Id">The event id.</param>
/// <param name="Name">The name.</param>
/// <param name="Payload">The payload attributes.</param>
/// <param name="Actors">The ids of the actors that may send it.</param>
/// <param name="Transitions">The ids of the transitions it triggers.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Stereotypes">The stereotype keys.</param>
/// <param name="Properties">The custom properties.</param>
public sealed record EventRecord(string Id, string Name, IReadOnlyList<AttributeRecord> Payload, IReadOnlyList<string> Actors, IReadOnlyList<string> Transitions,
    string DisplayName, string? Description, IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties);

/// <summary>A guard of a process.</summary>
/// <param name="Id">The guard id.</param>
/// <param name="Name">The name.</param>
/// <param name="Expression">The expression, or <see langword="null"/> for a stub.</param>
/// <param name="IsStub">Whether the guard has no expression.</param>
/// <param name="UsedBy">The ids of the transitions it guards.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Stereotypes">The stereotype keys.</param>
/// <param name="Properties">The custom properties.</param>
public sealed record GuardRecord(string Id, string Name, string? Expression, bool IsStub, IReadOnlyList<string> UsedBy, string DisplayName, string? Description,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties);

/// <summary>An action of a process.</summary>
/// <param name="Id">The action id.</param>
/// <param name="Name">The name.</param>
/// <param name="Expression">The expression, or <see langword="null"/> for a stub.</param>
/// <param name="IsStub">Whether the action has no expression.</param>
/// <param name="Raises">The ids of the events it raises.</param>
/// <param name="UsedBy">The ids of the states and transitions that run it.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Stereotypes">The stereotype keys.</param>
/// <param name="Properties">The custom properties.</param>
public sealed record ActionRecord(string Id, string Name, string? Expression, bool IsStub, IReadOnlyList<string> Raises, IReadOnlyList<string> UsedBy,
    string DisplayName, string? Description, IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties);

/// <summary>An invocation of a service or another process.</summary>
/// <param name="Id">The invocation id.</param>
/// <param name="Name">The name.</param>
/// <param name="Type"><c>service</c> or <c>process</c>.</param>
/// <param name="Process">The invoked process's id.</param>
/// <param name="Actors">The ids of the actors that answer it.</param>
/// <param name="State">The id of the state it runs in.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Stereotypes">The stereotype keys.</param>
/// <param name="Properties">The custom properties.</param>
public sealed record InvokeRecord(string Id, string Name, string Type, string? Process, IReadOnlyList<string> Actors, string State, string DisplayName,
    string? Description, IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties);

/// <summary>An approval gate on a transition.</summary>
/// <param name="Id">The gate id.</param>
/// <param name="Name">The name.</param>
/// <param name="Transition">The transition's id.</param>
/// <param name="Required">How many signatures it needs.</param>
/// <param name="Signers">The ids of the actors that may sign.</param>
/// <param name="RequiredActors">The ids of the actors that must sign.</param>
/// <param name="AllowRepeatSigner">Whether one signer may sign more than once.</param>
/// <param name="ReasonRequired">Whether a signature needs a reason.</param>
/// <param name="Meanings">The meanings a signature can carry.</param>
/// <param name="Audit">The audit fields a signature records.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
/// <param name="Stereotypes">The stereotype keys.</param>
/// <param name="Properties">The custom properties.</param>
public sealed record GateRecord(string Id, string Name, string Transition, int Required, IReadOnlyList<string> Signers, IReadOnlyList<string> RequiredActors,
    bool AllowRepeatSigner, bool ReasonRequired, IReadOnlyList<MeaningRecord> Meanings, IReadOnlyList<AuditFieldRecord> Audit, string DisplayName,
    string? Description, IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties);

/// <summary>A meaning of a gate's signature.</summary>
/// <param name="Id">The meaning id.</param>
/// <param name="Name">The name.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description.</param>
public sealed record MeaningRecord(string Id, string Name, string DisplayName, string? Description);

/// <summary>An audit field of a gate.</summary>
/// <param name="Id">The field id.</param>
/// <param name="Name">The name.</param>
/// <param name="Type">The type keyword.</param>
/// <param name="Required">Whether a value is required.</param>
/// <param name="Description">The description.</param>
/// <param name="Values">The allowed values.</param>
/// <param name="Attribute">The id of the context attribute it records into.</param>
public sealed record AuditFieldRecord(string Id, string Name, string Type, bool Required, string? Description, IReadOnlyList<string> Values, string? Attribute);

/// <summary>A resolved actor.</summary>
/// <param name="Id">The actor id.</param>
/// <param name="Kind"><c>actor</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id, or <see langword="null"/>.</param>
/// <param name="Type"><c>person</c>, <c>role</c> or <c>external-system</c>.</param>
/// <param name="Processes">The ids of the processes it takes part in.</param>
/// <param name="Events">The ids of the events it may send.</param>
/// <param name="Gates">The ids of the gates it may sign.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record ActorRecord(string Id, string Kind, string Name, string? Package, string Type, IReadOnlyList<string> Processes, IReadOnlyList<string> Events,
    IReadOnlyList<string> Gates, string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category,
    IReadOnlyList<string> Stereotypes, IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A resolved scenario.</summary>
/// <param name="Id">The scenario id.</param>
/// <param name="Kind"><c>scenario</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Package">The package's id, or <see langword="null"/>.</param>
/// <param name="Process">The process's id.</param>
/// <param name="Start">Where the run starts.</param>
/// <param name="Steps">The steps, in order.</param>
/// <param name="Outcome">The expected outcome.</param>
/// <param name="DisplayName">As <see cref="PackageRecord.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="PackageRecord.PluralName"/>.</param>
/// <param name="Description">As <see cref="PackageRecord.Description"/>.</param>
/// <param name="Tags">As <see cref="PackageRecord.Tags"/>.</param>
/// <param name="Category">As <see cref="PackageRecord.Category"/>.</param>
/// <param name="Stereotypes">As <see cref="PackageRecord.Stereotypes"/>.</param>
/// <param name="Properties">As <see cref="PackageRecord.Properties"/>.</param>
/// <param name="Generation">As <see cref="PackageRecord.Generation"/>.</param>
public sealed record ScenarioRecord(string Id, string Kind, string Name, string? Package, string Process, ScenarioStartRecord Start, IReadOnlyList<StepRecord> Steps,
    string Outcome, string DisplayName, string PluralName, string? Description, IReadOnlyList<string> Tags, string? Category, IReadOnlyList<string> Stereotypes,
    IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>Where a scenario's run starts.</summary>
/// <param name="Context">The starting context values.</param>
/// <param name="At">The starting clock, ISO 8601.</param>
public sealed record ScenarioStartRecord(IReadOnlyDictionary<string, object?> Context, string At);

/// <summary>A scenario step.</summary>
/// <param name="Id">The step id.</param>
/// <param name="Index">The 0-based position.</param>
/// <param name="Input"><c>event</c>, <c>advance</c>, <c>done</c>, <c>error</c> or <c>sign</c>.</param>
/// <param name="Event">The event's id.</param>
/// <param name="Invoke">The invocation's id.</param>
/// <param name="After">The delay as written.</param>
/// <param name="AfterMs">The delay in milliseconds.</param>
/// <param name="AfterTicks">The delay in ticks.</param>
/// <param name="Actor">The acting actor's id.</param>
/// <param name="Signer">The signer's name.</param>
/// <param name="Meaning">The meaning's id.</param>
/// <param name="Reason">The reason.</param>
/// <param name="Payload">The event payload.</param>
/// <param name="Assume">The guard outcomes assumed.</param>
/// <param name="Expect">What the step expects, or <see langword="null"/>.</param>
/// <param name="Description">The description.</param>
public sealed record StepRecord(string Id, int Index, string Input, string? Event, string? Invoke, string? After, long? AfterMs, long? AfterTicks, string? Actor,
    string? Signer, string? Meaning, string? Reason, IReadOnlyDictionary<string, object?> Payload, IReadOnlyDictionary<string, object?> Assume,
    ExpectationRecord? Expect, string? Description);

/// <summary>What a step expects.</summary>
/// <param name="Accepted">Whether the input is accepted.</param>
/// <param name="States">The active states' ids.</param>
/// <param name="StatePaths">The active states' paths.</param>
/// <param name="Context">The expected context values.</param>
public sealed record ExpectationRecord(bool Accepted, IReadOnlyList<string> States, IReadOnlyList<string> StatePaths, IReadOnlyDictionary<string, object?> Context);

/// <summary>A resolved database: the database view (<see cref="DatabaseView"/>) with its kind.</summary>
/// <param name="Id">The database id.</param>
/// <param name="Kind"><c>database</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Dialect">The dialect.</param>
/// <param name="Version">The target version.</param>
/// <param name="DefaultSchema">The effective default schema.</param>
/// <param name="Tables">Every table, as <see cref="DatabaseView.Tables"/>.</param>
/// <param name="Views">Every view.</param>
/// <param name="Sequences">Every sequence.</param>
/// <param name="Routines">Every routine, as <see cref="DatabaseView.Routines"/>.</param>
/// <param name="Types">Every database type, as <see cref="DatabaseView.Types"/>.</param>
/// <param name="Objects">Every SQL object, as <see cref="DatabaseView.Objects"/>.</param>
/// <param name="Schemas">Every schema, as <see cref="DatabaseView.Schemas"/>.</param>
/// <param name="Quoting">As <see cref="DatabaseView.Quoting"/>.</param>
/// <param name="MaxIdentifierLength">As <see cref="DatabaseView.MaxIdentifierLength"/>.</param>
/// <param name="ByConvention">As <see cref="DatabaseView.ByConvention"/>.</param>
/// <param name="Packages">As <see cref="DatabaseView.Packages"/>.</param>
/// <param name="DisplayName">As <see cref="DatabaseView.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="DatabaseView.PluralName"/>.</param>
/// <param name="Description">As <see cref="DatabaseView.Description"/>.</param>
/// <param name="Stereotypes">As <see cref="DatabaseView.Stereotypes"/>.</param>
/// <param name="Tags">As <see cref="DatabaseView.Tags"/>.</param>
/// <param name="Category">As <see cref="DatabaseView.Category"/>.</param>
/// <param name="Properties">As <see cref="DatabaseView.Properties"/>.</param>
/// <param name="Generation">As <see cref="DatabaseView.Generation"/>.</param>
public sealed record DatabaseRecord(string Id, string Kind, string Name, string Dialect, string? Version, string? DefaultSchema, IReadOnlyList<TableView> Tables,
    IReadOnlyList<ViewView> Views, IReadOnlyList<SequenceView> Sequences, IReadOnlyList<RoutineView> Routines, IReadOnlyList<DatabaseTypeView> Types,
    IReadOnlyList<SqlObjectView> Objects, IReadOnlyList<SchemaView> Schemas, string Quoting, int? MaxIdentifierLength,
    string ByConvention, IReadOnlyList<ConventionPackageView> Packages, string? DisplayName, string? PluralName, string? Description,
    IReadOnlyList<string> Stereotypes, IReadOnlyList<string> Tags, string? Category, IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>One table of a database, on its own (scope <c>tables</c>).</summary>
/// <param name="Id">The table's key, as <see cref="TableView.Key"/>.</param>
/// <param name="Kind"><c>table</c>.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Database">The database's id.</param>
/// <param name="Table">The table.</param>
public sealed record TableRecord(string Id, string Kind, string Name, string Database, TableView Table);

/// <summary>One routine of a database, on its own (scope <c>routines</c>).</summary>
/// <param name="Id">The routine's id.</param>
/// <param name="Kind"><c>routine</c>.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Database">The database's id.</param>
/// <param name="Routine">The routine.</param>
public sealed record RoutineRecord(string Id, string Kind, string Name, string Database, RoutineView Routine);

/// <summary>One database type of a database, on its own (scope <c>database-types</c>).</summary>
/// <param name="Id">The type's id.</param>
/// <param name="Kind"><c>database-type</c>.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Database">The database's id.</param>
/// <param name="DatabaseType">The type.</param>
public sealed record DatabaseTypeRecord(string Id, string Kind, string Name, string Database, DatabaseTypeView DatabaseType);

/// <summary>One SQL object of a database, on its own (scope <c>sql-objects</c>).</summary>
/// <param name="Id">The object's id.</param>
/// <param name="Kind"><c>sql-object</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Database">The database's id.</param>
/// <param name="SqlObject">The object.</param>
public sealed record SqlObjectRecord(string Id, string Kind, string Name, string Database, SqlObjectView SqlObject);

/// <summary>Projects the resolved model into flat records (resolved objects reference each other, so R-types are never serialized).</summary>
public static class ResolvedRecords
{
    /// <summary>The scopes <see cref="ResolvedQuery.Scope"/> takes, and the record kind each one holds.</summary>
    public static IReadOnlyDictionary<string, string> Scopes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["packages"] = "package",
        ["entities"] = "entity",
        ["relations"] = "relation",
        ["enums"] = "enum",
        ["value-objects"] = "value-object",
        ["scalar-types"] = "scalar-type",
        ["reference-types"] = "reference-type",
        ["seeds"] = "seed",
        ["processes"] = "process",
        ["actors"] = "actor",
        ["scenarios"] = "scenario",
        ["databases"] = "database",
        ["tables"] = "table",
        ["routines"] = "routine",
        ["database-types"] = "database-type",
        ["sql-objects"] = "sql-object",
    };

    /// <summary>Every scope name, <c>all</c> first, then the others in <see cref="Scopes"/> order.</summary>
    public static IReadOnlyList<string> ScopeNames { get; } = ["all", .. Scopes.Keys];

    /// <summary>One object of the resolved model with its sort key, projected on demand.</summary>
    internal readonly record struct Entry(string Kind, string Name, string Id, Func<object> Project);

    /// <summary>The objects of a scope, unordered, narrowed to a database when one is given.</summary>
    /// <param name="model">The resolved model.</param>
    /// <param name="scope">A scope name.</param>
    /// <param name="database">A database id, or <see langword="null"/>.</param>
    /// <returns>The entries.</returns>
    internal static IEnumerable<Entry> Entries(ResolvedModel model, string scope, string? database)
    {
        var context = new Projection(model);
        var all = scope == "all";
        string? databaseName = database is null ? null : model.Databases.FirstOrDefault(d => d.Id == database)?.Name ?? "\0";
        if (all || scope == "packages")
        {
            foreach (var p in model.Packages)
                yield return new Entry("package", p.Name, p.Id, () => context.Package(p));
        }

        if (all || scope == "entities")
        {
            foreach (var e in model.Entities.Where(e => databaseName is null || e.Mappings.ContainsKey(databaseName)))
                yield return new Entry("entity", e.Name, e.Id, () => context.Entity(e));
        }

        if (all || scope == "relations")
        {
            foreach (var r in model.Relations.Where(r => databaseName is null || r.Mappings.ContainsKey(databaseName)))
                yield return new Entry("relation", r.Name, r.Id, () => context.Relation(r));
        }

        if (all || scope == "enums")
        {
            foreach (var e in model.Enums)
                yield return new Entry("enum", e.Name, e.Id, () => context.Enum(e));
        }

        if (all || scope == "value-objects")
        {
            foreach (var v in model.ValueObjects)
                yield return new Entry("value-object", v.Name, v.Id, () => context.ValueObject(v));
        }

        if (all || scope == "scalar-types")
        {
            foreach (var s in model.ScalarTypes)
                yield return new Entry("scalar-type", s.Name, s.Id, () => context.ScalarType(s));
        }

        if (all || scope == "reference-types")
        {
            foreach (var t in model.ReferenceTypes)
                yield return new Entry("reference-type", t.Name, t.Id, () => context.ReferenceType(t));
        }

        if (all || scope == "seeds")
        {
            foreach (var s in model.Seeds)
                yield return new Entry("seed", s.Name, s.Id, () => context.Seed(s));
        }

        if (all || scope == "processes")
        {
            foreach (var p in model.Processes)
                yield return new Entry("process", p.Name, p.Id, () => context.Process(p));
        }

        if (all || scope == "actors")
        {
            foreach (var a in model.Actors)
                yield return new Entry("actor", a.Name, a.Id, () => context.Actor(a));
        }

        if (all || scope == "scenarios")
        {
            foreach (var s in model.Scenarios)
                yield return new Entry("scenario", s.Name, s.Id, () => context.Scenario(s));
        }

        if (all || scope == "databases")
        {
            foreach (var d in model.Databases.Where(d => database is null || d.Id == database))
                yield return new Entry("database", d.Name, d.Id, () => Database(d));
        }

        if (scope == "tables")
        {
            foreach (var d in model.Databases.Where(d => database is null || d.Id == database))
            {
                foreach (var t in d.Tables)
                    yield return new Entry("table", t.Name, t.Key, () => new TableRecord(t.Key, "table", t.Name, d.Id, DatabaseViews.Table(t)));
            }
        }

        if (scope is "routines" or "database-types" or "sql-objects")
        {
            foreach (var d in model.Databases.Where(d => database is null || d.Id == database))
            {
                if (scope == "routines")
                {
                    foreach (var r in d.Routines)
                        yield return new Entry("routine", r.Name, r.Id, () => new RoutineRecord(r.Id, "routine", r.Name, d.Id, DatabaseViews.ProjectRoutine(r)));
                }
                else if (scope == "database-types")
                {
                    foreach (var t in d.Types)
                        yield return new Entry("database-type", t.Name, t.Id, () => new DatabaseTypeRecord(t.Id, "database-type", t.Name, d.Id, DatabaseViews.ProjectType(t)));
                }
                else
                {
                    foreach (var o in d.Objects)
                        yield return new Entry("sql-object", o.Name, o.Id, () => new SqlObjectRecord(o.Id, "sql-object", o.Name, d.Id, DatabaseViews.ProjectObject(o)));
                }
            }
        }
    }

    private static DatabaseRecord Database(RDatabase database)
    {
        var view = DatabaseViews.From(database);
        return new DatabaseRecord(view.Id, "database", view.Name, view.Dialect, view.Version, view.DefaultSchema, view.Tables, view.Views, view.Sequences,
            view.Routines, view.Types, view.Objects, view.Schemas, view.Quoting, view.MaxIdentifierLength, view.ByConvention, view.Packages, view.DisplayName, view.PluralName, view.Description,
            view.Stereotypes, view.Tags, view.Category, view.Properties, view.Generation);
    }

    /// <summary>
    /// A plain value for JSON: an enum member as its name, another resolved object as its id, lists and dictionaries item by item, any
    /// other value as it is.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The plain value.</returns>
    internal static object? Plain(object? value) => value switch
    {
        null => null,
        string or bool or long or int or short or byte or decimal or double or float => value,
        REnumMember member => member.Name,
        IResolvedObject resolved => resolved.Id,
        IReadOnlyDictionary<string, object?> map => Plain(map),
        IDictionary dictionary => dictionary.Keys.Cast<object>().ToDictionary(k => Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture) ?? "", k => Plain(dictionary[k]), StringComparer.Ordinal),
        IEnumerable list => list.Cast<object?>().Select(Plain).ToList(),
        _ => value,
    };

    internal static IReadOnlyDictionary<string, object?> Plain(IReadOnlyDictionary<string, object?> map)
    {
        if (map.Count == 0)
            return map;
        if (map.Values.All(v => v is null or string or bool or long or int or decimal or double))
            return map;
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
            result[key] = Plain(value);
        return result;
    }

    /// <summary>The projection of one resolved model: the database name to id map the per-database dictionaries need.</summary>
    private sealed class Projection(ResolvedModel model)
    {
        private readonly Dictionary<string, string> _databaseIds = model.Databases.GroupBy(d => d.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        private string DatabaseId(string name) => _databaseIds.GetValueOrDefault(name) ?? name;

        public PackageRecord Package(RPackage p) => new(p.Id, "package", p.Name, p.Package?.Id ?? p.Parent?.Id, p.QualifiedName, Ids(p.Children),
            Ids(p.Entities), Ids(p.ValueObjects), Ids(p.Enums), Ids(p.Relations), p.DisplayName, p.PluralName, p.Description, p.Tags, p.Category?.Id,
            Keys(p.Stereotypes), Plain(p.Properties), p.Generation);

        public EntityRecord Entity(REntity e)
        {
            var key = e.Key is { } k ? k.Attributes.Select(a => a.Id).ToHashSet(StringComparer.Ordinal) : [];
            return new EntityRecord(e.Id, "entity", e.Name, e.Package?.Id, e.IsAbstract, e.Base?.Id, Ids(e.Derived),
                [.. e.Attributes.Select(a => Attribute(a, key))],
                e.Key is { } pk ? new KeyRecord(Ids(pk.Attributes), pk.Strategy, [.. pk.Sequences.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new KeySequenceRecord(DatabaseId(s.Key), s.Value.Id))]) : null,
                [.. e.AlternateKeys.Select(a => new AlternateKeyRecord(a.Id, a.Name, Ids(a.Attributes)))],
                [.. e.Navigations.Select(n => new NavigationRecord(n.Id, n.Name, n.Relation.Id, n.From.Id, n.To.Id, n.Target.Id, n.IsCollection))],
                Ids(e.Relations),
                [.. e.Mappings.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new EntityMappingRecord(m.Value.Database.Id, m.Value.Table.Key, m.Value.Inheritance,
                    m.Value.DiscriminatorColumn?.Key, Plain(m.Value.DiscriminatorValue), [.. m.Value.Columns.Select(c => new ColumnMappingRecord(c.AttributePath, c.Column.Key))]))],
                e.IsPromoted, e.PromotedFrom?.Id, Ids(e.Seeds), e.Lifecycle?.Id,
                e.DisplayName, e.PluralName, e.Description, e.Tags, e.Category?.Id, Keys(e.Stereotypes), Plain(e.Properties), e.Generation);
        }

        public AttributeRecord Attribute(RAttribute a, IReadOnlySet<string>? key = null) => new(
            a.Id, a.Name, a.Owner.Id, a.DeclaringEntity?.Id,
            new TypeRecord(a.Type.Kind, a.Type.Name, a.Type.Builtin, a.Type.Enum?.Id, a.Type.ValueObject?.Id, a.Type.Scalar?.Id, a.Type.ReferenceType?.Id),
            a.Required, Plain(a.Default), a.DefaultExpression, a.Length, a.Precision, a.Scale, a.Collection, a.Unique, a.Indexed, a.ReadOnly, a.Immutable,
            a.Derived is { } d ? new DerivedRecord(d.Expression, d.Stored) : null, a.Sensitive, Validation(a.Validation), a.Order,
            key is not null && key.Contains(a.Id), a.IsInherited, a.IsVirtual, a.FromStereotype?.Key,
            a.Reference is { } r ? new ReferenceUsageRecord(r.Type.Id, r.IsCollection, r.Required, Ids(r.Allowed), r.DefaultRow?.Id, Storage(r.Storage)) : null,
            a.DisplayName, a.PluralName, a.Description, a.Tags, a.Category?.Id, Keys(a.Stereotypes), Plain(a.Properties), a.Generation);

        public RelationRecord Relation(RRelation r) => new(r.Id, "relation", r.Name, r.Package?.Id, r.InverseName, r.RelationKind, r.Cardinality, r.AllowDuplicates,
            [.. r.Ends.Select(e => new EndRecord(e.Id, e.Entity.Id, e.Role, e.Navigation, e.Min, e.Max, e.IsMany, e.OnDelete, e.Ordered, e.Opposite?.Id))],
            [.. r.Attributes.Select(a => Attribute(a))],
            [.. r.Mappings.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => new RelationMappingRecord(DatabaseId(m.Key), m.Value.Shape, m.Value.ForeignKey?.Name,
                m.Value.JunctionTable?.Key, m.Value.PromotedEntity?.Id))],
            Ids(r.Seeds), r.DisplayName, r.PluralName, r.Description, r.Tags, r.Category?.Id, Keys(r.Stereotypes), Plain(r.Properties), r.Generation);

        public EnumRecord Enum(REnum e) => new(e.Id, "enum", e.Name, e.Package?.Id, e.Flags,
            [.. e.Members.Select(m => new EnumMemberRecord(m.Id, m.Name, m.DisplayName, m.Description, m.Value, m.Code, Plain(m.Properties)))],
            e.DisplayName, e.PluralName, e.Description, e.Tags, e.Category?.Id, Keys(e.Stereotypes), Plain(e.Properties), e.Generation);

        public ValueObjectRecord ValueObject(RValueObject v) => new(v.Id, "value-object", v.Name, v.Package?.Id, [.. v.Attributes.Select(a => Attribute(a))],
            v.DisplayName, v.PluralName, v.Description, v.Tags, v.Category?.Id, Keys(v.Stereotypes), Plain(v.Properties), v.Generation);

        public ScalarTypeRecord ScalarType(RScalarType s) => new(s.Id, "scalar-type", s.Name, s.Package?.Id, s.Base, s.Length, s.Precision, s.Scale, Validation(s.Validation),
            s.NativeTypes, s.DisplayName, s.PluralName, s.Description, s.Tags, s.Category?.Id, Keys(s.Stereotypes), Plain(s.Properties), s.Generation);

        public ReferenceTypeRecord ReferenceType(RReferenceType t) => new(t.Id, "reference-type", t.Name, t.Package?.Id, Field(t.Code), Field(t.Label),
            [.. t.Attributes.Select(a => Attribute(a))],
            [.. t.Rows.Select(r => new RowRecord(r.Id, Plain(r.Code), r.Label, r.Description, Plain(r.Values), r.Seed.Id, r.Order))],
            Ids(t.Seeds), Ids(t.UsedBy), Storage(t.Storage),
            t.DisplayName, t.PluralName, t.Description, t.Tags, t.Category?.Id, Keys(t.Stereotypes), Plain(t.Properties), t.Generation);

        public SeedRecord Seed(RSeed s) => new(s.Id, "seed", s.Name, s.Package?.Id, s.Target?.Id,
            [.. s.Columns.Select(c => new SeedColumnRecord(c.Name, c.Kind, c.Attribute?.Id, c.End?.Id))],
            [.. s.Rows.Select(r => new SeedRowRecord(r.Id, r.Order, Plain(r.Values)))],
            s.DisplayName, s.PluralName, s.Description, s.Tags, s.Category?.Id, Keys(s.Stereotypes), Plain(s.Properties), s.Generation);

        public ProcessRecord Process(RProcess p) => new(p.Id, "process", p.Name, p.Package?.Id, p.Use, p.Subject?.Id, p.BoundAttribute?.Id, p.BoundEnum?.Id, p.Initial?.Id,
            [.. p.Context.Select(a => Attribute(a))],
            [.. p.AllStates.Select(s => new StateRecord(s.Id, s.Name, s.Path, s.Type, s.Parent?.Id, Ids(s.Children), s.Initial?.Id, s.History, s.DefaultTarget?.Id,
                Ids(s.Entry), Ids(s.Exit), Ids(s.Invoke), s.IsFinal, s.IsAtomic, s.Depth, s.BoundMember?.Id, Ids(s.TransitionsOut), s.RegionIndex,
                s.DisplayName, s.Description, Keys(s.Stereotypes), Plain(s.Properties)))],
            [.. p.Transitions.Select(t => new TransitionRecord(t.Id, t.Source.Id, Ids(t.Targets), t.Trigger, t.Event?.Id, t.After, t.AfterMs, t.AfterTicks, t.Invoke?.Id,
                t.Guard?.Id, t.GuardMissing, Ids(t.Actions), t.External, t.Gate?.Id, t.Label, t.IsTargetless, t.DisplayName, t.Description, Keys(t.Stereotypes), Plain(t.Properties)))],
            [.. p.Events.Select(e => new EventRecord(e.Id, e.Name, [.. e.Payload.Select(a => Attribute(a))], Ids(e.Actors), Ids(e.Transitions),
                e.DisplayName, e.Description, Keys(e.Stereotypes), Plain(e.Properties)))],
            [.. p.Guards.Select(g => new GuardRecord(g.Id, g.Name, g.Expression, g.IsStub, Ids(g.UsedBy), g.DisplayName, g.Description, Keys(g.Stereotypes), Plain(g.Properties)))],
            [.. p.Actions.Select(a => new ActionRecord(a.Id, a.Name, a.Expression, a.IsStub, Ids(a.Raises), Ids(a.UsedBy), a.DisplayName, a.Description, Keys(a.Stereotypes), Plain(a.Properties)))],
            [.. p.Gates.Select(g => new GateRecord(g.Id, g.Name, g.Transition.Id, g.Required, Ids(g.Signers), Ids(g.RequiredActors), g.AllowRepeatSigner, g.ReasonRequired,
                [.. g.Meanings.Select(m => new MeaningRecord(m.Id, m.Name, m.DisplayName, m.Description))],
                [.. g.Audit.Select(f => new AuditFieldRecord(f.Id, f.Name, f.Type, f.Required, f.Description, f.Values, f.Attribute?.Id))],
                g.DisplayName, g.Description, Keys(g.Stereotypes), Plain(g.Properties)))],
            [.. p.Invokes.Select(i => new InvokeRecord(i.Id, i.Name, i.Type, i.Process?.Id, Ids(i.Actors), i.State.Id, i.DisplayName, i.Description, Keys(i.Stereotypes), Plain(i.Properties)))],
            Ids(p.Actors), Ids(p.Scenarios),
            p.DisplayName, p.PluralName, p.Description, p.Tags, p.Category?.Id, Keys(p.Stereotypes), Plain(p.Properties), p.Generation);

        public ActorRecord Actor(RActor a) => new(a.Id, "actor", a.Name, a.Package?.Id, a.Type, Ids(a.Processes), Ids(a.Events), Ids(a.Gates),
            a.DisplayName, a.PluralName, a.Description, a.Tags, a.Category?.Id, Keys(a.Stereotypes), Plain(a.Properties), a.Generation);

        public ScenarioRecord Scenario(RScenario s) => new(s.Id, "scenario", s.Name, s.Package?.Id, s.Process.Id, new ScenarioStartRecord(Plain(s.Start.Context), s.Start.At),
            [.. s.Steps.Select(t => new StepRecord(t.Id, t.Index, t.Input, t.Event?.Id, t.Invoke?.Id, t.After, t.AfterMs, t.AfterTicks, t.Actor?.Id, t.Signer, t.Meaning?.Id,
                t.Reason, Plain(t.Payload), Plain(t.Assume),
                t.Expect is { } x ? new ExpectationRecord(x.Accepted, Ids(x.States), x.StatePaths, Plain(x.Context)) : null, t.Description))],
            s.Outcome, s.DisplayName, s.PluralName, s.Description, s.Tags, s.Category?.Id, Keys(s.Stereotypes), Plain(s.Properties), s.Generation);

        private List<StorageChoiceRecord> Storage(IReadOnlyDictionary<string, RStorageChoice> storage) =>
            [.. storage.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new StorageChoiceRecord(DatabaseId(s.Key), s.Value.Strategy, Plain(s.Value.Options), s.Value.Source,
                s.Value.Description, s.Value.Collections))];

        private static ReferenceFieldRecord Field(RReferenceField f) => new(f.Id, f.Type, f.Length, f.Pattern, f.DisplayName, f.Description);

        private static ValidationRecord? Validation(RValidation? v) => v is null ? null : new ValidationRecord(Plain(v.Min), Plain(v.Max), v.Pattern,
            [.. v.AllowedValues.Select(Plain)], v.Rules);

        private static List<string> Ids<T>(IEnumerable<T> items) where T : IResolvedObject => [.. items.Select(i => i.Id)];

        private static List<string> Keys(IReadOnlyList<RStereotype> stereotypes) => [.. stereotypes.Select(s => s.Key)];
    }
}
