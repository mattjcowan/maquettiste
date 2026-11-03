using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The indexes a script rule's <c>model</c> reads (next-phase-1-design.md section 5.7): the documents that list a stereotype, a tag or
/// an own custom property key, each list ordinal by (name, id). One instance per snapshot (<see cref="Of"/>), each index built on first
/// use; safe to read from the parallel engines of a validation. Only the first document of each id counts (a duplicate id, MQ1004, is
/// left out), and only top-level elements: what a file states about its sub-elements is read through the element's document.
/// </summary>
internal sealed class RuleModelIndex
{
    /// <summary>The index of <c>model.withStereotype(key)</c>: the keys an element's file lists in <c>stereotypes</c>.</summary>
    public const string Stereotype = "stereotype";

    /// <summary>The index of <c>model.withTag(tag)</c>: the keys an element's file lists in <c>tags</c>.</summary>
    public const string Tag = "tag";

    /// <summary>The index of <c>model.withProperty(key)</c>: the keys of an element's own <c>properties</c>.</summary>
    public const string Property = "property";

    /// <summary>The index of <c>model.byQualifiedName(name)</c>: the snapshot's <see cref="NameIndex"/> of the name's kind.</summary>
    public const string Qualified = "qn";

    private static readonly ConditionalWeakTable<ModelSnapshot, RuleModelIndex> Indexes = new();

    private readonly ModelSnapshot _model;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<ElementDocument>>> _byStereotype;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<ElementDocument>>> _byTag;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<ElementDocument>>> _byProperty;

    private RuleModelIndex(ModelSnapshot model)
    {
        _model = model;
        _byStereotype = new(() => Build(model, static e => e.Stereotypes), LazyThreadSafetyMode.ExecutionAndPublication);
        _byTag = new(() => Build(model, static e => e.Tags), LazyThreadSafetyMode.ExecutionAndPublication);
        _byProperty = new(() => Build(model, static e => e.Properties.Keys), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The indexes of a snapshot, created on first use and kept as long as the snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The indexes.</returns>
    public static RuleModelIndex Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Indexes.GetValue(model, static m => new RuleModelIndex(m));
    }

    /// <summary>The read key an index function records: <c>ix:&lt;index&gt;:&lt;value&gt;</c>.</summary>
    /// <param name="index">The index (<see cref="Stereotype"/>, <see cref="Tag"/>, <see cref="Property"/> or <see cref="Qualified"/>).</param>
    /// <param name="value">The value asked for (for <see cref="Qualified"/>, the canonical name text).</param>
    /// <returns>The key.</returns>
    public static string Key(string index, string value) => "ix:" + index + ":" + value;

    /// <summary>The documents an index holds under a value, ordinal by (name, id); empty for an unknown index or value.</summary>
    /// <param name="index">The index: <see cref="Stereotype"/>, <see cref="Tag"/> or <see cref="Property"/>.</param>
    /// <param name="value">The stereotype key, tag key or property key.</param>
    /// <returns>The documents.</returns>
    public ImmutableArray<ElementDocument> Find(string index, string value)
    {
        var map = index switch
        {
            Stereotype => _byStereotype.Value,
            Tag => _byTag.Value,
            Property => _byProperty.Value,
            _ => null,
        };
        return map is not null && map.TryGetValue(value, out var documents) ? documents : [];
    }

    /// <summary>
    /// The ids a qualified name finds in the snapshot's name index of its kind, the same lookup a batch and a template's
    /// <c>seed_named</c> make (next-phase-1-design.md section 5.1), ordinal by id: the candidates of one name share its last segment,
    /// so this is the (name, id) order of the other lists.
    /// </summary>
    /// <param name="name">The parsed name.</param>
    /// <returns>The ids, none when the name finds nothing.</returns>
    public IReadOnlyList<string> FindQualified(QualifiedName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return [.. _model.Names(name.Kind).Find(name.Path).Select(e => e.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The ids an <c>ix:</c> key's lookup returns, sorted ordinally (what the key's current hash covers), or <see langword="null"/> for a
    /// key that names no index or a malformed qualified name.
    /// </summary>
    /// <param name="indexAndValue">The key without <c>ix:</c>: <c>&lt;index&gt;:&lt;value&gt;</c>.</param>
    /// <returns>The sorted ids.</returns>
    public IReadOnlyList<string>? SortedIds(string indexAndValue)
    {
        var colon = indexAndValue.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return null;
        var index = indexAndValue[..colon];
        var value = indexAndValue[(colon + 1)..];
        if (string.Equals(index, Qualified, StringComparison.Ordinal))
        {
            return QualifiedName.TryParse(value, out var name, out _) ? FindQualified(name) : null;
        }

        if (index is not (Stereotype or Tag or Property))
            return null;
        return [.. Find(index, value).Select(d => d.Element.Id).Order(StringComparer.Ordinal)];
    }

    private static FrozenDictionary<string, ImmutableArray<ElementDocument>> Build(ModelSnapshot model, Func<Element, IEnumerable<string>> keys)
    {
        var lists = new Dictionary<string, List<ElementDocument>>(StringComparer.Ordinal);
        foreach (var document in model.Documents)
        {
            if (!ReferenceEquals(model.GetDocument(document.Element.Id), document))
                continue; // a duplicate id (MQ1004)
            foreach (var key in keys(document.Element).Distinct(StringComparer.Ordinal))
            {
                if (!lists.TryGetValue(key, out var list))
                    lists[key] = list = [];
                list.Add(document);
            }
        }

        return lists.ToFrozenDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(d => d.Element.Name, StringComparer.Ordinal).ThenBy(d => d.Element.Id, StringComparer.Ordinal).ToImmutableArray(),
            StringComparer.Ordinal);
    }
}
