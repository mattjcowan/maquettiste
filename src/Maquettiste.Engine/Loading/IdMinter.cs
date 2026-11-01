using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// Gives every object below an element's root that its schema says requires an id (<see cref="ObjectLayout.RequiresId"/>) and that
/// has none a new id (next-phase-1-design.md section 5.1, decision 5). The order is fixed, so a deterministic generator gives
/// byte-identical files: the keys of an object in <c>x-order</c> (a map's in ordinal order), the items of an array by index, an object
/// before its children. The root is never touched: a create's root id is set by its caller, an update's is its target's.
/// </summary>
internal static class IdMinter
{
    /// <summary>Mints the missing ids below the root of an element.</summary>
    /// <param name="element">The element document, changed in place.</param>
    /// <param name="layout">The layout of its kind's schema.</param>
    /// <param name="ids">The id generator.</param>
    /// <param name="minted">Receives one entry per minted id, in minting order, or <see langword="null"/>. Each entry has operation
    /// 0, the object's pointer in <paramref name="element"/>, its index kind (else <c>sub-element</c>), no name, and the placeholder
    /// its <c>as</c> key declares when it carries one; a batch's caller sets the operation and the name.</param>
    public static void Fill(JsonNode element, ObjectLayout layout, IIdGenerator ids, List<MintedId>? minted)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(ids);
        new Walk(element, ids, minted).Children(element, layout);
    }

    /// <summary>One fill: the path of the walk is kept as segments, and a pointer and a kind are built only for a minted id.</summary>
    private sealed class Walk(JsonNode root, IIdGenerator ids, List<MintedId>? minted)
    {
        private readonly List<string> _path = [];

        public void Children(JsonNode? node, ObjectLayout layout)
        {
            switch (node)
            {
                case JsonObject obj when layout.HasKeys:
                    foreach (var key in layout.Keys)
                    {
                        if (obj.TryGetPropertyValue(key, out var child) && child is JsonObject or JsonArray)
                            Visit(child, layout.Properties[key].Layout, key);
                    }

                    break;
                case JsonObject obj when layout.MapValues is { } values:
                    foreach (var key in obj.Select(p => p.Key).Order(StringComparer.Ordinal).ToList())
                    {
                        if (obj[key] is JsonObject or JsonArray)
                            Visit(obj[key], values, key);
                    }

                    break;
                case JsonArray array when layout.Items is { } items:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonObject or JsonArray)
                            Visit(array[i], items, i.ToString(CultureInfo.InvariantCulture));
                    }

                    break;
            }
        }

        private void Visit(JsonNode? node, ObjectLayout layout, string segment)
        {
            _path.Add(segment);
            if (node is JsonObject obj && layout.RequiresId && obj["id"] is null)
            {
                var id = ids.NewId();
                obj["id"] = id;
                if (minted is not null)
                {
                    var placeholder = obj["as"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                    minted.Add(new MintedId(0, JsonPointer.Build(_path), KindAt() ?? "sub-element", id, null, placeholder));
                }
            }

            Children(node, layout);
            _path.RemoveAt(_path.Count - 1);
        }

        /// <summary>The index kind of the record type the current path reaches from the element's kind, or <see langword="null"/>.</summary>
        private string? KindAt()
        {
            if (root is not JsonObject element || element["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var name)
                || !KindInfo.TryGet(name, out var info))
            {
                return null;
            }

            Type? type = info.ClrType;
            foreach (var segment in _path)
            {
                type = type is not null && ItemType(type) is { } item ? item : PropertyType(type, segment);
                if (type is null)
                    return null;
            }

            return ModelIndexer.SubElementKind(type);
        }
    }

    /// <summary>The CLR type of a model record's JSON property, or <see langword="null"/> when unknown.</summary>
    private static Type? PropertyType(Type? type, string name)
    {
        if (type is null || EngineJson.Options.GetTypeInfo(type) is not { Kind: JsonTypeInfoKind.Object } info)
            return null;
        var property = info.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        return property is null ? null : Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
    }

    /// <summary>The item type of a list or array, or the value type of a map; <see langword="null"/> for anything else.</summary>
    private static Type? ItemType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();
        if (type.IsGenericType && type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            return type.GetGenericArguments()[^1];
        return null;
    }
}
