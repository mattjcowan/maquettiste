using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>What types an attribute that holds a lifecycle's state (lifecycle-design.md decision 11).</summary>
internal enum BindingKind
{
    /// <summary>A type that cannot hold a state (MQ9202).</summary>
    Unsupported,

    /// <summary>A non-flags enum: states match members by name.</summary>
    Enum,

    /// <summary>A reference type: codes are the type's live codes.</summary>
    Reference,

    /// <summary>A plain status: <c>string</c>, <c>text</c>, <c>int16</c>, <c>int32</c> or <c>int64</c>, directly or through a scalar type.</summary>
    Plain,
}

/// <summary>How a bound attribute holds the state.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Enum">The enum, for <see cref="BindingKind.Enum"/>.</param>
/// <param name="Reference">The reference type, for <see cref="BindingKind.Reference"/>.</param>
/// <param name="Plain">The effective type, for <see cref="BindingKind.Plain"/>.</param>
/// <param name="Why">Why the attribute cannot hold a state, for <see cref="BindingKind.Unsupported"/>: "is a collection" and the like.</param>
internal sealed record BindingType(BindingKind Kind, EnumType? Enum = null, ReferenceType? Reference = null, EffectiveType? Plain = null, string? Why = null)
{
    /// <summary>Whether the binding maps states by code, so a state without one has no stored value (MQ9210): every kind but an enum.</summary>
    public bool NeedsCodes => Kind is BindingKind.Reference or BindingKind.Plain;
}

/// <summary>A lifecycle's binding of one subject: the process, the entity and the attribute.</summary>
/// <param name="Process">The lifecycle.</param>
/// <param name="Entity">The subject entity, or <see langword="null"/> for a stereotype entry.</param>
/// <param name="Attribute">The bound attribute's id.</param>
/// <param name="Stereotype">The stereotype key a stereotype entry names (stereotype-sets-design.md decision 8), else <see langword="null"/>.</param>
internal sealed record LifecycleBinding(Process Process, string? Entity, string Attribute, string? Stereotype = null);

/// <summary>
/// The lifecycles' bindings over a model (lifecycle-design.md decisions 11 and 18): how an attribute holds a state, and per snapshot which
/// lifecycles bind which attribute and which type, so a save of a reference type, seed or scalar type revisits only the lifecycles that
/// bind one. Both maps are empty, and cost one pass over the processes, when nothing binds.
/// </summary>
internal sealed class LifecycleBindings
{
    private static readonly ConditionalWeakTable<ModelSnapshot, LifecycleBindings> ByModel = new();

    /// <summary>The built-in types a plain status may have, directly or as a scalar type's base.</summary>
    internal static readonly FrozenSet<string> PlainKeywords = new[] { "string", "text", "int16", "int32", "int64" }.ToFrozenSet(StringComparer.Ordinal);

    private LifecycleBindings(ModelSnapshot model)
    {
        var byAttribute = new Dictionary<string, List<LifecycleBinding>>(StringComparer.Ordinal);
        var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var process in model.All<Process>())
        {
            if (process.Use != ProcessUse.Lifecycle)
                continue;
            foreach (var entry in process.Subjects)
            {
                if (entry.BoundAttribute is not { } attributeId)
                    continue;
                if (!byAttribute.TryGetValue(attributeId, out var list))
                    byAttribute[attributeId] = list = [];
                list.Add(new LifecycleBinding(process, entry.Entity, attributeId, entry.Stereotype));
                if (Find(model, attributeId)?.Type.Ref is { } typeId)
                {
                    if (!byType.TryGetValue(typeId, out var processes))
                        byType[typeId] = processes = [];
                    if (!processes.Contains(process.Id, StringComparer.Ordinal))
                        processes.Add(process.Id);
                }
            }
        }

        ByAttribute = byAttribute.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutableArray(), StringComparer.Ordinal);
        ByType = byType.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutableArray(), StringComparer.Ordinal);
    }

    /// <summary>Bound attribute id to the lifecycles' bindings of it, in process order then entry order.</summary>
    public FrozenDictionary<string, ImmutableArray<LifecycleBinding>> ByAttribute { get; }

    /// <summary>The id an attribute's <c>type.ref</c> names (an enum, a reference type or a scalar type) to the lifecycles that bind such an attribute.</summary>
    public FrozenDictionary<string, ImmutableArray<string>> ByType { get; }

    /// <summary>The bindings of a model, built once per snapshot.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The bindings.</returns>
    public static LifecycleBindings Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ByModel.GetValue(model, static m => new LifecycleBindings(m));
    }

    /// <summary>The attribute an id names, wherever it is declared (an entity, a value object, a relation, a stereotype), or <see langword="null"/>.</summary>
    /// <param name="model">The model.</param>
    /// <param name="attributeId">The attribute id.</param>
    /// <returns>The attribute.</returns>
    public static ModelAttribute? Find(ModelSnapshot model, string attributeId)
    {
        if (!model.TryGetEntry(attributeId, out var entry) || entry.Kind != "attribute" || model.GetDocument(entry.OwnerId)?.Element is not { } owner)
            return null;
        foreach (var attribute in ReferenceDataIndex.AttributesOf(owner))
        {
            if (string.Equals(attribute.Id, attributeId, StringComparison.Ordinal))
                return attribute;
        }

        return null;
    }

    /// <summary>How an attribute holds a state, or why it cannot (lifecycle-design.md decision 11).</summary>
    /// <param name="model">The model.</param>
    /// <param name="attribute">The attribute.</param>
    /// <returns>The binding type.</returns>
    public static BindingType Classify(ModelSnapshot model, ModelAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(attribute);
        if (attribute.Collection)
            return new BindingType(BindingKind.Unsupported, Why: "is a collection");
        if (attribute.Type.Ref is { } typeId)
        {
            if (model.Get<EnumType>(typeId) is { } enumType)
            {
                return enumType.Flags
                    ? new BindingType(BindingKind.Unsupported, Why: $"is typed by the flags enum '{enumType.Name}', which holds several members at once")
                    : new BindingType(BindingKind.Enum, Enum: enumType);
            }

            if (model.Get<ReferenceType>(typeId) is { } referenceType)
                return new BindingType(BindingKind.Reference, Reference: referenceType);
        }

        var type = AttributeRules.Resolve(model, attribute.Type);
        if (type.Keyword is { } keyword && PlainKeywords.Contains(keyword))
            return new BindingType(BindingKind.Plain, Plain: type);
        return new BindingType(BindingKind.Unsupported, Why: $"is typed by {type.Describe()}, which cannot hold a state");
    }

    /// <summary>
    /// The states a binding needs a code on (lifecycle-design.md decisions 4 and 13), in document order: walking down from the root, a
    /// state with no code and no coded state below it; one with a coded state below is walked into. History and choice states never
    /// count, and a state that carries a code is covered with its subtree.
    /// </summary>
    /// <param name="analysis">The process's analysis.</param>
    /// <param name="codes">The process's codes.</param>
    /// <returns>The states.</returns>
    public static IReadOnlyList<StateNode> Uncoded(ProcessAnalysis analysis, ProcessCodes codes)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(codes);
        var above = new HashSet<string>(StringComparer.Ordinal); // states with a coded state below them
        foreach (var coded in codes.Coded)
        {
            if (coded.State.Type is StateType.History or StateType.Choice || analysis.Find(coded.State.Id) is not { } node)
                continue;
            for (var parent = node.Parent; parent is not null && above.Add(parent.Id); parent = parent.Parent)
            {
            }
        }

        var uncoded = new List<StateNode>();
        Walk(null);
        return uncoded;

        void Walk(StateNode? parent)
        {
            foreach (var child in analysis.ChildrenOf(parent))
            {
                if (child.Type is StateType.History or StateType.Choice)
                    continue;
                if (codes.Find(child.Id) is not null)
                    continue;
                if (above.Contains(child.Id))
                    Walk(child);
                else
                    uncoded.Add(child);
            }
        }
    }
}
