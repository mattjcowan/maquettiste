using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>One reference value found in an element's records.</summary>
/// <param name="Pointer">The JSON pointer of the value (with the list index for list references).</param>
/// <param name="FromId">The innermost element, sub-element or keyed record holding the reference.</param>
/// <param name="Reference">The property's <see cref="ElementRefAttribute"/>.</param>
/// <param name="DeclaringType">The CLR type that declares the property.</param>
/// <param name="PropertyName">The CLR property name.</param>
/// <param name="Value">The referenced id or key.</param>
/// <param name="XRef">For a custom property value an <c>x-ref</c> types (next-phase-1-design.md section 4.1): the site, with its
/// keyword and the schema that declares it; <see langword="null"/> for every other reference.</param>
internal readonly record struct ReferenceSite(
    string Pointer, string FromId, ElementRefAttribute Reference, Type DeclaringType, string PropertyName, string Value, XRefSite? XRef = null);

/// <summary>
/// Walks an element's records the way <see cref="ModelSnapshot"/>'s indexer does (property metadata from
/// <see cref="EngineJson.Options"/>, so JSON names and pointers agree), yielding every <see cref="ElementRefAttribute"/> value
/// with the attribute itself, which the reverse index does not keep. Thread-safe; metadata is cached per instance.
/// </summary>
internal sealed class ReferenceWalker
{
    private const string ModelNamespace = "Maquettiste.Engine.Model";

    private static readonly ElementRefAttribute TypeRefTarget =
        typeof(TypeRef).GetProperty(nameof(TypeRef.Ref))!.GetCustomAttribute<ElementRefAttribute>()!;

    private static readonly ElementRefAttribute StorageTarget = new(ElementKind.Database);

    // A scenario step's assume keys, typed by their value (trigger-points-design.md decision 26): a boolean names a guard, as before trigger
    // points; a word names a trigger point.
    private static readonly ElementRefAttribute GuardAssumption = new() { IndexKinds = ["guard"] };

    private static readonly ElementRefAttribute CheckAssumption = new() { IndexKinds = ["process-trigger-point"] };

    private readonly ConcurrentDictionary<Type, PropertyMeta[]> _meta = new();

    // Keyed by keyword identity and held weakly: the walker lives as long as its validator, and a replaced schema set's
    // keywords must stay collectable.
    private readonly ConditionalWeakTable<XRef, ElementRefAttribute> _xrefTargets = new();

    private enum Role { Reference, TypeRef, Record, RecordList }

    private sealed record PropertyMeta(string Name, Func<object, object?> Get, Role Role, ElementRefAttribute? Reference, Type DeclaringType, string ClrName);

    /// <summary>
    /// Returns every reference in an element, in record walk order, then the values its custom properties' <c>x-ref</c> keywords
    /// type (one site per schema that types a value, so the validator judges each), in <see cref="PropertySchemaSet.Sites"/> order.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="schemas">The custom property schemas of the model.</param>
    /// <param name="effectiveKeys">The element's keys with defaults (<see cref="ModelSnapshot.PropertyKeys"/>), or <see langword="null"/>.</param>
    /// <returns>The references.</returns>
    public List<ReferenceSite> Walk(Element element, PropertySchemaSet schemas, IReadOnlyList<string>? effectiveKeys)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var sites = new List<ReferenceSite>();
        WalkProperties(element, "", element.Id, sites);
        if (element is ReferenceType type)
        {
            // Storage keys name databases (or "*"), so MQ2001 and MQ2002 cover them as any reference (section 1.4).
            foreach (var key in type.Storage.Keys.Order(StringComparer.Ordinal))
            {
                if (key != "*")
                    sites.Add(new ReferenceSite(Ptr.Prop(Ptr.Prop("", "storage"), key), type.Id, StorageTarget, typeof(ReferenceType), nameof(ReferenceType.Storage), key));
            }
        }

        // Custom property values an x-ref types (section 4.1): each carries a synthesized attribute, like the storage keys, and its keyword.
        foreach (var site in schemas.Sites(element, effectiveKeys))
            sites.Add(new ReferenceSite(site.Pointer, site.Host.Id, TargetOf(site.Ref), typeof(PropertyHost), site.Field, site.Value, site));

        return sites;
    }

    /// <summary>The attribute an <c>x-ref</c> stands for: its element kinds as targets and its sub-element kinds as index kinds
    /// (reserved kinds have neither yet, so a value typed only by them matches nothing).</summary>
    private ElementRefAttribute TargetOf(XRef xref) => _xrefTargets.GetValue(xref, static x =>
    {
        var targets = x.Kinds.Select(k => KindInfo.TryGet(k, out var info) ? info.Kind : (ElementKind?)null).OfType<ElementKind>().ToArray();
        return new ElementRefAttribute(targets) { IndexKinds = [.. x.Kinds.Where(ModelIndexer.SubElementKinds.Contains)] };
    });

    private void WalkProperties(object value, string pointer, string fromId, List<ReferenceSite> sites)
    {
        foreach (var property in MetaOf(value.GetType()))
        {
            var child = property.Get(value);
            if (child is null)
                continue;
            var childPointer = Ptr.Prop(pointer, property.Name);
            switch (property.Role)
            {
                case Role.Reference when child is string id:
                    sites.Add(new ReferenceSite(childPointer, fromId, property.Reference!, property.DeclaringType, property.ClrName, id));
                    break;
                case Role.Reference when child is IReadOnlyDictionary<string, StepAssumption> assume:
                    foreach (var (key, entry) in assume.OrderBy(p => p.Key, StringComparer.Ordinal))
                        sites.Add(new ReferenceSite(Ptr.Prop(childPointer, key), fromId, entry.Check is null ? GuardAssumption : CheckAssumption,
                            property.DeclaringType, property.ClrName, key));
                    break;
                case Role.Reference when child is IDictionary map:
                    // A map keyed by ids (a scenario's context and payload values, guard assumptions): each key is a reference.
                    foreach (var key in map.Keys.Cast<string>().Order(StringComparer.Ordinal))
                        sites.Add(new ReferenceSite(Ptr.Prop(childPointer, key), fromId, property.Reference!, property.DeclaringType, property.ClrName, key));
                    break;
                case Role.Reference when child is IEnumerable<string> ids:
                    var i = 0;
                    foreach (var item in ids)
                        sites.Add(new ReferenceSite(Ptr.At(childPointer, i++), fromId, property.Reference!, property.DeclaringType, property.ClrName, item));
                    break;
                case Role.TypeRef when child is TypeRef { Ref: { } typeId }:
                    sites.Add(new ReferenceSite(childPointer + "/ref", fromId, TypeRefTarget, typeof(TypeRef), nameof(TypeRef.Ref), typeId));
                    break;
                case Role.Record:
                    WalkProperties(child, childPointer, IdOf(child) ?? fromId, sites);
                    break;
                case Role.RecordList when child is IEnumerable items:
                    var n = 0;
                    foreach (var item in items)
                    {
                        if (item is not null)
                            WalkProperties(item, Ptr.At(childPointer, n), IdOf(item) ?? fromId, sites);
                        n++;
                    }

                    break;
            }
        }
    }

    private static string? IdOf(object value) => value switch
    {
        ElementBase b => b.Id,
        AlternateKey k => k.Id,
        UniqueConstraint u => u.Id,
        ForeignKey f => f.Id,
        CheckConstraint c => c.Id,
        TableIndex x => x.Id,
        RelationEnd e => e.Id,
        IProcessNode n => n.Id,
        _ => null,
    };

    private PropertyMeta[] MetaOf(Type type) => _meta.GetOrAdd(type, static t =>
    {
        var list = new List<PropertyMeta>();
        var info = EngineJson.Options.GetTypeInfo(t);
        if (info.Kind != JsonTypeInfoKind.Object)
            return [];
        foreach (var property in info.Properties)
        {
            if (property.Get is null || property.AttributeProvider is not PropertyInfo pi)
                continue;
            var reference = pi.GetCustomAttribute<ElementRefAttribute>(inherit: true);
            var type = property.PropertyType;
            Role? role = reference is not null ? Role.Reference
                : type == typeof(TypeRef) ? Role.TypeRef
                : IsModelRecord(type) ? Role.Record
                : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) && IsModelRecord(type.GetGenericArguments()[0]) ? Role.RecordList
                : null;
            if (role is not null)
                list.Add(new PropertyMeta(property.Name, property.Get, role.Value, reference, pi.DeclaringType ?? t, pi.Name));
        }

        return [.. list];
    });

    private static bool IsModelRecord(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace == ModelNamespace && type != typeof(Description) && type != typeof(TypeRef)
        && !typeof(IEnumerable).IsAssignableFrom(type);
}
