using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Maquettiste.Engine.Model;

/// <summary>A tag vocabulary: the global one (<c>model/vocabularies/tags.json</c>) or a domain's (<see cref="Package"/>).</summary>
public sealed record TagVocabulary : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.TagVocabulary;

    /// <summary>
    /// The domain the vocabulary is scoped to (that domain and the domains nested under it), or <see langword="null"/> for the global
    /// one. At most one vocabulary of each kind per scope (MQ1009).
    /// </summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

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

/// <summary>A category tree, global (<c>model/vocabularies/categories.json</c>) or a domain's: a flat list, a tree through <see cref="Category.Parent"/>.</summary>
public sealed record CategoryTree : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.CategoryTree;

    /// <summary>
    /// The domain the vocabulary is scoped to (that domain and the domains nested under it), or <see langword="null"/> for the global
    /// one. At most one vocabulary of each kind per scope (MQ1009).
    /// </summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

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
public sealed record Stereotype : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Stereotype;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root. Membership only: it never changes
    /// where the element goes in a database.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>
    /// The kebab-case key used in every element's <c>stereotypes</c> list. Immutable like an id: a save that changes it is refused
    /// (MQ3020). The reverse index records each <c>stereotypes[i]</c> entry as a reference to the stereotype's id.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>Kind names (engine-design.md section 2.2) plus <c>attribute</c>, <c>enum-member</c> and <c>column</c>; empty means all.</summary>
    public IReadOnlyList<string> AppliesTo { get; init; } = [];

    /// <summary>
    /// A label (not a link): an element carries at most one stereotype of a group, its own and implied ones together (MQ2013).
    /// </summary>
    public string? Group { get; init; }

    /// <summary>Stereotype keys an element that carries this one must also carry (MQ2014).</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Stereotype keys an element that carries this one must not carry (MQ2015).</summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>
    /// Stereotype keys applied with this one, one level deep (MQ2017): an element that carries it carries them too, before its own
    /// (<see cref="ModelSnapshot.EffectiveStereotypes(IReadOnlyList{string})"/>).
    /// </summary>
    [StereotypeKeys]
    public IReadOnlyList<string> Implies { get; init; } = [];

    /// <summary>Virtual attributes added to entities, value objects and relations that carry the stereotype.</summary>
    public IReadOnlyList<ModelAttribute> Attributes { get; init; } = [];

    /// <summary>
    /// The typed parameters: the schema of the <c>properties</c> values an element that carries the stereotype may hold, checked on
    /// the element's merged values (MQ2018). <see cref="DefaultProperties"/> give their defaults.
    /// </summary>
    public StereotypeParameters? Parameters { get; init; }

    /// <summary>Default custom property values, merged under the element's own <c>properties</c>.</summary>
    public IReadOnlyDictionary<string, JsonElement> DefaultProperties { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>Table storage parameters per dialect name the stereotype gives the tables that carry it, under their own.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Storage { get; init; } =
        ImmutableDictionary<string, IReadOnlyDictionary<string, JsonElement>>.Empty;

    /// <summary>
    /// Columns every table whose effective set holds the stereotype inherits (next-phase-2-design.md section 3.1), keyed
    /// <c>&lt;tableKey&gt;.&lt;memberId&gt;</c> in each table. With the keys, indexes and checks below they are the stereotype's
    /// <em>members</em>. A synthesized table that stores the stereotype's attributes as columns inherits none of them.
    /// </summary>
    public IReadOnlyList<Column> Columns { get; init; } = [];

    /// <summary>A primary key every carrier inherits, over the stereotype's own columns; it has an id and no name.</summary>
    public PrimaryKey? PrimaryKey { get; init; }

    /// <summary>Unique constraints every carrier inherits, over the stereotype's own columns.</summary>
    public IReadOnlyList<UniqueConstraint> Uniques { get; init; } = [];

    /// <summary>Foreign keys every carrier inherits, to one fixed table or to the table each carrier names in a parameter.</summary>
    public IReadOnlyList<MemberForeignKey> ForeignKeys { get; init; } = [];

    /// <summary>Check constraints every carrier inherits.</summary>
    public IReadOnlyList<CheckConstraint> Checks { get; init; } = [];

    /// <summary>Indexes every carrier inherits, over the stereotype's own columns.</summary>
    public IReadOnlyList<TableIndex> Indexes { get; init; } = [];

    /// <summary>An icon name.</summary>
    public string? Icon { get; init; }

    /// <summary>A display color.</summary>
    public string? Color { get; init; }
}

/// <summary>
/// A foreign key a stereotype contributes (next-phase-2-design.md section 3.1): a <see cref="ForeignKey"/>'s members without a name,
/// since every carrier would share it, and with a target that is either one fixed table (<see cref="ReferencesTable"/>) or the
/// table each carrier names in a stereotype parameter (<see cref="ReferencesParameter"/>), never both (MQ2026).
/// </summary>
public sealed record MemberForeignKey
{
    /// <summary>The key's id: the member id, keyed <c>&lt;tableKey&gt;.&lt;id&gt;</c> in each carrying table.</summary>
    public required string Id { get; init; }

    /// <summary>The referencing columns: ids of the stereotype's own columns, in key order.</summary>
    [ElementRef(Keyed = true)]
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The id of the one table every carrier references, or <see langword="null"/> with <see cref="ReferencesParameter"/>.</summary>
    [ElementRef(Keyed = true)]
    public string? ReferencesTable { get; init; }

    /// <summary>
    /// The name of a required stereotype parameter typed <c>{type: string, x-ref: {kind: table, sameDatabase: true}}</c>, in which each
    /// carrier names the table it references in its <c>properties</c>; <see langword="null"/> with <see cref="ReferencesTable"/>.
    /// </summary>
    public string? ReferencesParameter { get; init; }

    /// <summary>With <see cref="ReferencesTable"/>: referenced column ids or keys; empty means the referenced primary key.</summary>
    [ElementRef(Keyed = true)]
    public IReadOnlyList<string> ReferencesColumns { get; init; } = [];

    /// <summary>Whether the last column pair is a period the referenced rows' periods must cover (PostgreSQL 18 <c>PERIOD</c>).</summary>
    public bool Period { get; init; }

    /// <summary>The on-delete action.</summary>
    public ReferentialAction OnDelete { get; init; } = ReferentialAction.NoAction;

    /// <summary>With <see cref="OnDelete"/> set-null or set-default, the columns of <see cref="Columns"/> the action sets; empty sets them all.</summary>
    [ElementRef(Keyed = true)]
    public IReadOnlyList<string> OnDeleteColumns { get; init; } = [];

    /// <summary>The on-update action.</summary>
    public ReferentialAction OnUpdate { get; init; } = ReferentialAction.NoAction;

    /// <summary>When the key is checked.</summary>
    public Deferrability Deferrable { get; init; } = Deferrability.NotDeferrable;

    /// <summary>
    /// The constraint name one carrying table's <c>redefines</c> entry gives the key in that table's member set; never read from or
    /// written to a file (a stereotype's foreign key has no name), so it is internal and outside the JSON contract.
    /// </summary>
    internal string? Name { get; init; }
}

/// <summary>
/// A stereotype's typed parameters (next-phase-1-design.md section 4.2), shaped like an extension file's: one JSON Schema per
/// property name, which may carry <c>x-ref</c> and <c>x-translatable</c>, and the names a carrier must end up with.
/// </summary>
public sealed record StereotypeParameters
{
    /// <summary>A JSON Schema <c>properties</c> object: one schema per parameter name.</summary>
    public required JsonElement Properties { get; init; }

    /// <summary>The parameters each carrier must have, judged on its merged values (the stereotype's defaults count).</summary>
    public IReadOnlyList<string> Required { get; init; } = [];
}

/// <summary>Where a stereotype of an effective set comes from (next-phase-2-design.md section 3.1).</summary>
public enum StereotypeSource
{
    /// <summary>The element's own <c>stereotypes</c> list.</summary>
    Own,

    /// <summary>Implied by a stereotype of the element's own list.</summary>
    Implied,

    /// <summary>A database schema's <c>defaultStereotypes</c>, or a stereotype one of them implies.</summary>
    SchemaDefault,

    /// <summary>A package's <c>defaultStereotypes</c>, or a stereotype one of them implies.</summary>
    PackageDefault,
}

/// <summary>One stereotype of an effective set (<see cref="ModelSnapshot.EffectiveStereotypes(Element)"/>, or of a key list).</summary>
/// <param name="Stereotype">The stereotype.</param>
/// <param name="Entry">The element's own entry, or the implying stereotype's; -1 for a default and the stereotypes it implies.</param>
/// <param name="ImpliedBy">The key of the stereotype that implies it, or <see langword="null"/> for an own or default entry.</param>
public sealed record AppliedStereotype(Stereotype Stereotype, int Entry, string? ImpliedBy)
{
    /// <summary>
    /// Where the stereotype comes from: <see cref="StereotypeSource.Own"/>, <see cref="StereotypeSource.Implied"/> (by an own one),
    /// or the default that gives it, which a stereotype implied by a default shares (with <see cref="ImpliedBy"/> set).
    /// </summary>
    public StereotypeSource Source { get; init; } = StereotypeSource.Own;

    /// <summary>The id of the database schema entry or package whose default list gives the stereotype; <see langword="null"/> otherwise.</summary>
    public string? DefaultHolder { get; init; }
}
