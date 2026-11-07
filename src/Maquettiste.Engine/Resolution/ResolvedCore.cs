using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>An object of the resolved model that templates can read and that records dependencies when read.</summary>
public interface IResolvedObject
{
    /// <summary>The object's id: the element or sub-element id, or a synthesized key (engine-design.md section 7.3).</summary>
    string Id { get; }

    /// <summary>The object's kind, for example <c>entity</c>, <c>attribute</c>, <c>table</c>, <c>column</c>.</summary>
    string Kind { get; }

    /// <summary>The dependency keys (engine-design.md section 11) of every file and setting that contributed to the object.</summary>
    IReadOnlyList<string> Dependencies { get; }
}

/// <summary>
/// A read-only list of resolved objects that knows which dependency keys decide its membership. Every list of resolved objects
/// in the resolved model is an <see cref="RList{T}"/>; enumerating it, indexing it or reading its size records
/// <see cref="MembershipKeys"/>.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class RList<T> : IReadOnlyList<T> where T : IResolvedObject
{
    private readonly ImmutableArray<T> _items;

    /// <summary>Creates a list.</summary>
    /// <param name="items">The items, in their resolved order.</param>
    /// <param name="membershipKeys">The keys that decide which items the list holds, for example <c>k:entity</c>.</param>
    internal RList(IEnumerable<T> items, IEnumerable<string> membershipKeys)
    {
        _items = [.. items];
        MembershipKeys = membershipKeys is FrozenKeys frozen ? frozen : [.. membershipKeys]; // a frozen list never changes: shared
    }

    /// <summary>An empty list with no membership keys.</summary>
    internal static RList<T> Empty { get; } = new([], []);

    /// <summary>The dependency keys that decide which items the list holds.</summary>
    public IReadOnlyList<string> MembershipKeys { get; }

    /// <inheritdoc/>
    public int Count => _items.Length;

    /// <inheritdoc/>
    public T this[int index] => _items[index];

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>The base of every resolved object.</summary>
public abstract class RObject : IResolvedObject
{
    /// <inheritdoc/>
    public string Id { get; internal set; } = "";

    /// <inheritdoc/>
    public abstract string Kind { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> Dependencies { get; internal set; } = [];

    /// <summary>The resolver's working dependency set while a resolution runs (frozen into <see cref="Dependencies"/> and cleared at its end).</summary>
    internal DependencySet? PendingDependencies;
}

/// <summary>
/// The annotations a model file carries besides its structure: display names, description, rationale, decision and status, tags,
/// category, stereotypes, custom properties and generation hints. Every conceptual object (<see cref="RElement"/>) has them, and so does every physical object
/// (<see cref="RDatabase"/>, <see cref="RSchema"/>, <see cref="RTable"/>, <see cref="RColumn"/>, <see cref="RView"/>,
/// <see cref="RSequence"/>): a physical object reads them from its own file or entry (the database file and its schema entries, a
/// designed or imported table, a synthesized table's overlay and its column entries, a view or sequence file) and never from the
/// entity, relation or attribute it comes from, which templates reach through <c>table.entity</c>, <c>table.relation</c> and
/// <c>column.attribute</c>.
/// </summary>
public abstract class RAnnotated : RObject
{
    /// <summary>
    /// The display name. A conceptual object falls back to its name, so it always has one; a physical object holds its file's value,
    /// or <see langword="null"/> when the file sets none or the object has no file.
    /// </summary>
    public string? DisplayName { get; internal set; }

    /// <summary>
    /// The plural name. A conceptual object falls back to the inflector, so it always has one; a physical object holds its file's
    /// value, or <see langword="null"/> when the file sets none or the object has no file.
    /// </summary>
    public string? PluralName { get; internal set; }

    /// <summary>
    /// The owning package, or <see langword="null"/> for the root (for a package: its parent). A conceptual object holds its own; a
    /// physical object its file's, where a table without one falls back to its entity's or relation's; a seed falls back to its
    /// target's and a scenario to its process's. Membership never changes where a table goes in a database.
    /// </summary>
    public RPackage? Package { get; internal set; }

    /// <summary>
    /// The description text, with a sidecar file loaded; <see langword="null"/> when there is none. An entity attribute with none of its own
    /// shows the comment of the column its entity's bindings map it to (<see cref="RAttribute.DescriptionColumn"/>).
    /// </summary>
    public string? Description { get; internal set; }

    /// <summary>Markdown text: why the object is shaped this way, from its own file or entry; <see langword="null"/> when there is none.</summary>
    public string? Rationale { get; internal set; }

    /// <summary>A URL or a reference (such as <c>ADR-0042</c>) of the decision record; <see langword="null"/> when there is none.</summary>
    public string? Decision { get; internal set; }

    /// <summary>
    /// The status as its JSON value (<c>proposed</c>, <c>approved</c>, <c>deprecated</c> or <c>retired</c>); <see langword="null"/> when
    /// none is written.
    /// </summary>
    public string? Status { get; internal set; }

    /// <summary>Tag keys.</summary>
    public IReadOnlyList<string> Tags { get; internal set; } = [];

    /// <summary>The category, when set.</summary>
    public RCategory? Category { get; internal set; }

    /// <summary>Stereotypes, in application order.</summary>
    public IReadOnlyList<RStereotype> Stereotypes { get; internal set; } = [];

    /// <summary>Custom properties: stereotype defaults merged under the object's own, as plain CLR values.</summary>
    public IReadOnlyDictionary<string, object?> Properties { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Generation hints by pack name or <c>"*"</c>.</summary>
    public IReadOnlyDictionary<string, GenerationHints> Generation { get; internal set; } = FrozenDictionary<string, GenerationHints>.Empty;

    /// <summary>The host of the resolution that built the object (next-phase-2-design.md section 5.5), or none.</summary>
    private readonly CalculationHost? _calculationHost = CalculationHost.Building;

    private RCalculated? _calculated;

    /// <summary>The named values the project's calculation files compute for this object (section 5.5); empty when
    /// its kind declares none. Never null.</summary>
    public RCalculated Calculated => _calculationHost is null ? RCalculated.Empty : _calculated ??= new RCalculated(_calculationHost, this);

    /// <summary>Whether the object carries a stereotype.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool HasStereotype(string key) => Stereotypes.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    /// <summary>Whether the object carries a tag.</summary>
    /// <param name="key">The tag key.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool HasTag(string key) => Tags.Contains(key, StringComparer.Ordinal);
}

/// <summary>The members common to every conceptual resolved object (package, entity, attribute, value object, enum, scalar type, relation).</summary>
public abstract class RElement : RAnnotated
{
    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";
}

/// <summary>
/// A resolved category: the one instance every annotated object in it carries as its category, and an item of its
/// <see cref="RCategoryTree"/>.
/// </summary>
public sealed class RCategory
{
    /// <summary>The category id.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>The category name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>Names from the root to this category, joined with <c>/</c>.</summary>
    public string Path { get; internal set; } = "";

    /// <summary>The parent category, or <see langword="null"/> for a root (or a parent the tree lacks).</summary>
    public RCategory? Parent { get; internal set; }

    /// <summary>The child categories, in sibling order: by <see cref="Order"/> (none counts as 0), then file order.</summary>
    public IReadOnlyList<RCategory> Children { get; internal set; } = [];

    /// <summary>The position among siblings, or <see langword="null"/> when the file sets none.</summary>
    public int? Order { get; internal set; }

    /// <summary>The description text, or <see langword="null"/> when there is none.</summary>
    public string? Description { get; internal set; }

    /// <summary>Markdown text: why the category is shaped this way, from its own entry; <see langword="null"/> when there is none.</summary>
    public string? Rationale { get; internal set; }

    /// <summary>A URL or a reference (such as <c>ADR-0042</c>) of the decision record; <see langword="null"/> when there is none.</summary>
    public string? Decision { get; internal set; }

    /// <summary>
    /// The status as its JSON value (<c>proposed</c>, <c>approved</c>, <c>deprecated</c> or <c>retired</c>); <see langword="null"/> when
    /// none is written.
    /// </summary>
    public string? Status { get; internal set; }
}

/// <summary>A stereotype applied to an object.</summary>
public sealed class RStereotype
{
    /// <summary>The stereotype key.</summary>
    public string Key { get; internal set; } = "";

    /// <summary>The display name (the stereotype's display name, else its key).</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The icon name.</summary>
    public string? Icon { get; internal set; }

    /// <summary>The display color.</summary>
    public string? Color { get; internal set; }

    /// <summary>
    /// The key of the stereotype the object carries that implies this one (next-phase-1-design.md section 4.2), or
    /// <see langword="null"/> when the object lists it itself.
    /// </summary>
    public string? ImpliedBy { get; internal set; }

    /// <summary>
    /// Where the object's stereotype comes from (next-phase-2-design.md section 3.1): <c>own</c> (its own list), <c>implied</c> (by an own
    /// one), <c>schema-default</c> or <c>package-default</c> (a database schema's or a package's <c>defaultStereotypes</c>, or a
    /// stereotype one of them implies). For an attribute's <see cref="RAttribute.FromStereotype"/>, where its owner got the stereotype.
    /// </summary>
    public string Source { get; internal set; } = "own";
}

/// <summary>
/// The resolved model templates see (SPEC section 10): every list sorted as engine-design.md section 7.1 states, never by hash order.
/// </summary>
public sealed class ResolvedModel
{
    /// <summary>The snapshot this model was resolved from.</summary>
    public ModelSnapshot Source { get; internal init; } = null!;

    /// <summary>The project settings.</summary>
    public ProjectSettings Settings { get; internal init; } = null!;

    /// <summary>Packages, by qualified name.</summary>
    public RList<RPackage> Packages { get; internal init; } = RList<RPackage>.Empty;

    /// <summary>Entities (promoted ones included), by (package qualified name, name, id).</summary>
    public RList<REntity> Entities { get; internal init; } = RList<REntity>.Empty;

    /// <summary>Value objects, by (package qualified name, name, id).</summary>
    public RList<RValueObject> ValueObjects { get; internal init; } = RList<RValueObject>.Empty;

    /// <summary>Enums, by (package qualified name, name, id).</summary>
    public RList<REnum> Enums { get; internal init; } = RList<REnum>.Empty;

    /// <summary>Custom scalar types, by (package qualified name, name, id).</summary>
    public RList<RScalarType> ScalarTypes { get; internal init; } = RList<RScalarType>.Empty;

    /// <summary>Relations, by (package qualified name, name, id).</summary>
    public RList<RRelation> Relations { get; internal init; } = RList<RRelation>.Empty;

    /// <summary>Reference types, by (name, id).</summary>
    public RList<RReferenceType> ReferenceTypes { get; internal init; } = RList<RReferenceType>.Empty;

    /// <summary>Seeds in merge order: by the qualified name of the package the seed's file states (none first), then name, then id.</summary>
    public RList<RSeed> Seeds { get; internal init; } = RList<RSeed>.Empty;

    private RList<RSeed>? _seedsInOrder;

    /// <summary>
    /// Seeds in insert order: a seed whose rows another seed's rows name comes first; ties by dependency depth, then merge order. Ordered on
    /// first read (it reads the rows of every seed whose cells name other rows).
    /// </summary>
    public RList<RSeed> SeedsInOrder
    {
        get
        {
            if (Volatile.Read(ref _seedsInOrder) is { } seeds)
                return seeds;
            var built = SeedsInOrderFactory?.Invoke() ?? RList<RSeed>.Empty;
            return Interlocked.CompareExchange(ref _seedsInOrder, built, null) ?? built;
        }
        internal init => _seedsInOrder = value;
    }

    /// <summary>Builds <see cref="SeedsInOrder"/> on its first read.</summary>
    internal Func<RList<RSeed>>? SeedsInOrderFactory { private get; init; }

    /// <summary>The declared locales (reference-types-seeds-localization.md section 3.7): the default first, then ordinal; empty
    /// without a <c>localization</c> block.</summary>
    public RList<RLocale> Locales { get; internal init; } = RList<RLocale>.Empty;

    /// <summary>Processes that run, by (package qualified name, name, id); a library is in <see cref="ProcessLibraries"/>.</summary>
    public RList<RProcess> Processes { get; internal init; } = RList<RProcess>.Empty;

    /// <summary>Library processes, which hold guards, actions and gates that others name, by (package qualified name, name, id).</summary>
    public RList<RProcess> ProcessLibraries { get; internal init; } = RList<RProcess>.Empty;

    /// <summary>Actors, by (name, id).</summary>
    public RList<RActor> Actors { get; internal init; } = RList<RActor>.Empty;

    /// <summary>Scenarios, by (process in <see cref="Processes"/> order, name, id).</summary>
    public RList<RScenario> Scenarios { get; internal init; } = RList<RScenario>.Empty;

    /// <summary>
    /// The named rights (rights-design.md section 6), by (package qualified name, name, id), whether a lifecycle uses them or not; a
    /// lifecycle's inline rights are in its <see cref="RProcess.Rights"/>.
    /// </summary>
    public RList<RRight> Rights { get; internal init; } = RList<RRight>.Empty;

    /// <summary>
    /// The lifecycles that state rights or a default (rights-design.md decision 28), by name then id: the ones that can answer for a verb.
    /// Empty in a project that uses no rights, so a template asks <c>model.rights_lifecycles.size &gt; 0</c> to learn whether the project
    /// uses rights. The list keys only on the rights naming key (<c>rn:rights</c>), whose hash never changes in a project without rights.
    /// </summary>
    public RList<RProcess> RightsLifecycles { get; internal init; } = RList<RProcess>.Empty;

    /// <summary>
    /// Whether the project uses freezing (rights-design.md decision 28): <c>model.freezing.used</c>. It keys only on the freezing naming key
    /// (<c>fn:freezing</c>), whose hash never changes in a project that doesn't freeze, so a template that asks pays for nothing else.
    /// </summary>
    public RFreezing Freezing { get; internal init; } = RFreezing.Unused;

    /// <summary>Operations, by (package qualified name, subject name with none first, name, id) (next-phase-2-design.md section 4.1).</summary>
    public RList<ROperation> Operations { get; internal init; } = RList<ROperation>.Empty;

    /// <summary>Custom elements, by (type, name, id) (next-phase-2-design.md section 4.2). <see cref="Find"/> reaches their members.</summary>
    public RList<RCustom> CustomElements { get; internal init; } = RList<RCustom>.Empty;

    /// <summary>Named constraints, by (package qualified name, name, id) (next-phase-3-design.md section 3.1).</summary>
    public RList<RConstraint> Constraints { get; internal init; } = RList<RConstraint>.Empty;

    /// <summary>Databases, by name.</summary>
    public RList<RDatabase> Databases { get; internal init; } = RList<RDatabase>.Empty;

    /// <summary>
    /// Every open migration fact (next-phase-1-design.md section 3.2), by (database name, applied order): each database's
    /// <see cref="RDatabase.Migrations"/> in turn.
    /// </summary>
    public RList<RMigration> Migrations { get; internal init; } = RList<RMigration>.Empty;

    /// <summary>The stereotype definitions (next-phase-1-design.md section 5.5), one per key, by key.</summary>
    public RList<RStereotypeDefinition> Stereotypes { get; internal init; } = RList<RStereotypeDefinition>.Empty;

    /// <summary>The tag vocabularies, one per scope: the global one first, then by (package qualified name, id).</summary>
    public RList<RTagVocabulary> TagVocabularies { get; internal init; } = RList<RTagVocabulary>.Empty;

    /// <summary>The category trees, one per scope: the global one first, then by (package qualified name, id).</summary>
    public RList<RCategoryTree> CategoryTrees { get; internal init; } = RList<RCategoryTree>.Empty;

    /// <summary>
    /// The verbs (verbs-design.md decision 19): empty unless the project uses verbs (it has a verb vocabulary or names a verb anywhere).
    /// Then the built-in verbs that are not hidden, in the table's order, then the project's own in file order, then any hidden built-in
    /// verb that is still named.
    /// </summary>
    public RList<RVerb> Verbs { get; internal init; } = RList<RVerb>.Empty;

    /// <summary>Resolution diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal init; } = [];

    /// <summary>The resolution findings the elements' suppressions moved out of <see cref="Diagnostics"/> (section 5.6), in input order.</summary>
    internal IReadOnlyList<SuppressedFinding> Suppressed { get; init; } = [];

    /// <summary>The calculated values of this model's objects (section 5.5), or <see langword="null"/> when the project declares none.</summary>
    internal CalculationHost? Calculated { get; init; }

    /// <summary>Every resolved object by id; filled by the resolver.</summary>
    internal IReadOnlyDictionary<string, IResolvedObject> ById { get; init; } = FrozenDictionary<string, IResolvedObject>.Empty;

    /// <summary>Finds a resolved object by id or synthesized key.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The object, or <see langword="null"/>.</returns>
    public IResolvedObject? Find(string id) => ById.TryGetValue(id, out var value) ? value : null;
}

/// <summary>
/// A declared locale (section 3.7): the element of an <c>each locale</c> unit, whose unit key is <c>locale:&lt;tag&gt;</c>, and an
/// item of <c>model.locales</c>. It depends on the <c>localization</c> settings only.
/// </summary>
public sealed class RLocale : RObject
{
    /// <summary>The settings dependency key every locale read records.</summary>
    public const string SettingsKey = "s:localization";

    private static readonly string[] Keys = [SettingsKey];

    /// <inheritdoc/>
    public override string Kind => "locale";

    /// <summary>The BCP 47 tag.</summary>
    public string Tag { get; internal set; } = "";

    /// <summary>Whether this is the default locale (the language of the element files).</summary>
    public bool IsDefault { get; internal set; }

    /// <summary>The fallback chain, starting with the locale and ending with the default.</summary>
    public IReadOnlyList<string> Chain { get; internal set; } = [];

    /// <summary>The locales of the settings, in unit order.</summary>
    /// <param name="settings">The <c>localization</c> block, or <see langword="null"/>.</param>
    /// <returns>The locales.</returns>
    internal static IEnumerable<RLocale> Of(LocalizationSettings? settings) => settings is null
        ? []
        : Localization.LocaleChains.Ordered(settings).Select(tag => new RLocale
        {
            Id = "locale:" + tag,
            Tag = tag,
            IsDefault = string.Equals(tag, settings.DefaultLocale, StringComparison.Ordinal),
            Chain = Localization.LocaleChains.Chain(settings, tag),
            Dependencies = Keys,
        });
}

/// <summary>
/// Whether the project uses freezing (rights-design.md decision 28): an event, a transition or a right names <c>freeze</c> or <c>thaw</c>,
/// or a right has <c>whileFrozen</c>. Worked out from the index rows; its one dependency is the freezing naming key.
/// </summary>
public sealed class RFreezing : RObject
{
    /// <summary>The flag of a project that doesn't freeze, still keyed on the freezing naming key.</summary>
    internal static RFreezing Unused { get; } = new() { Id = "freezing", Dependencies = [Planning.FreezingNaming.Key] };

    /// <inheritdoc/>
    public override string Kind => "freezing";

    /// <summary>Whether the project uses freezing.</summary>
    public bool Used { get; internal init; }

    /// <summary>The flag of a snapshot.</summary>
    internal static RFreezing Of(ModelSnapshot model) =>
        Planning.FreezingNaming.Uses(model) ? new() { Id = "freezing", Used = true, Dependencies = [Planning.FreezingNaming.Key] } : Unused;
}
