using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
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
internal readonly record struct ReferenceSite(
    string Pointer, string FromId, ElementRefAttribute Reference, Type DeclaringType, string PropertyName, string Value);

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

    private readonly ConcurrentDictionary<Type, PropertyMeta[]> _meta = new();

    private enum Role { Reference, TypeRef, Record, RecordList }

    private sealed record PropertyMeta(string Name, Func<object, object?> Get, Role Role, ElementRefAttribute? Reference, Type DeclaringType, string ClrName);

    /// <summary>Returns every reference in an element, in record walk order.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The references.</returns>
    public List<ReferenceSite> Walk(Element element)
    {
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

        return sites;
    }

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
