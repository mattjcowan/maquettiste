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

/// <summary>The members common to every conceptual resolved object (package, entity, attribute, value object, enum, scalar type, relation).</summary>
public abstract class RElement : RObject
{
    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The display name, falling back to <see cref="Name"/>.</summary>
    public string DisplayName { get; internal set; } = "";

    /// <summary>The plural name, falling back to the inflector.</summary>
    public string PluralName { get; internal set; } = "";

    /// <summary>The description text, with a sidecar file loaded; <see langword="null"/> when there is none.</summary>
    public string? Description { get; internal set; }

    /// <summary>Tag keys.</summary>
    public IReadOnlyList<string> Tags { get; internal set; } = [];

    /// <summary>The category, when set.</summary>
    public RCategory? Category { get; internal set; }

    /// <summary>Stereotypes, in application order.</summary>
    public IReadOnlyList<RStereotype> Stereotypes { get; internal set; } = [];

    /// <summary>Custom properties: stereotype defaults merged under the element's own, as plain CLR values.</summary>
    public IReadOnlyDictionary<string, object?> Properties { get; internal set; } = FrozenDictionary<string, object?>.Empty;

    /// <summary>Generation hints by pack name or <c>"*"</c>.</summary>
    public IReadOnlyDictionary<string, GenerationHints> Generation { get; internal set; } = FrozenDictionary<string, GenerationHints>.Empty;

    /// <summary>The owning package, or <see langword="null"/> for the root (for a package: its parent).</summary>
    public RPackage? Package { get; internal set; }

    /// <summary>Whether the object carries a stereotype.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool HasStereotype(string key) => Stereotypes.Any(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    /// <summary>Whether the object carries a tag.</summary>
    /// <param name="key">The tag key.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool HasTag(string key) => Tags.Contains(key, StringComparer.Ordinal);
}

/// <summary>A resolved category.</summary>
public sealed class RCategory
{
    /// <summary>The category id.</summary>
    public string Id { get; internal set; } = "";

    /// <summary>The category name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>Names from the root to this category, joined with <c>/</c>.</summary>
    public string Path { get; internal set; } = "";
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

    /// <summary>Seeds, by (name, id).</summary>
    public RList<RSeed> Seeds { get; internal init; } = RList<RSeed>.Empty;

    /// <summary>Seeds in insert order: a seed whose rows another seed's rows name comes first; ties by (dependency depth, name, id).</summary>
    public RList<RSeed> SeedsInOrder { get; internal init; } = RList<RSeed>.Empty;

    /// <summary>The declared locales (reference-types-seeds-localization.md section 3.7): the default first, then ordinal; empty
    /// without a <c>localization</c> block.</summary>
    public RList<RLocale> Locales { get; internal init; } = RList<RLocale>.Empty;

    /// <summary>Databases, by name.</summary>
    public RList<RDatabase> Databases { get; internal init; } = RList<RDatabase>.Empty;

    /// <summary>Resolution diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal init; } = [];

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
