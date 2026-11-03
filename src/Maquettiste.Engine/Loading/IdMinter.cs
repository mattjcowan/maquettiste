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
/// before its children. The root is never touched: a create's root id is set by its caller, an update's is its target's. A custom
/// element's <c>content</c> is free-form to the schema: its members (next-phase-2-design.md section 4.2) get their ids through the
/// custom kind's member tree from the snapshot, content keys in ordinal order, object members in ordinal order, items by index, a
/// member before its nested members. No key after <c>content</c> holds a sub-element, so this is the same order.
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
    /// <param name="schemas">The snapshot's property schema set, whose custom kinds give a custom element's member tree, or
    /// <see langword="null"/> to mint no member ids.</param>
    public static void Fill(JsonNode element, ObjectLayout layout, IIdGenerator ids, List<MintedId>? minted, PropertySchemaSet? schemas = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(ids);
        var walk = new Walk(element, ids, minted);
        walk.Children(element, layout);
        if (schemas is not null && element is JsonObject root && root["kind"] is JsonValue kind && kind.TryGetValue<string>(out var kindName)
            && kindName == "custom" && root["type"] is JsonValue type && type.TryGetValue<string>(out var typeName)
            && schemas.ContentOf(typeName) is { Members.Count: > 0 } source && root["content"] is JsonObject content)
        {
            walk.Content(content, source.Members);
        }
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

        /// <summary>Mints the ids of the members of a custom element's content through its custom kind's member tree.</summary>
        public void Content(JsonObject content, IReadOnlyDictionary<string, XMemberNode> roots)
        {
            _path.Add("content");
            foreach (var key in content.Select(p => p.Key).Order(StringComparer.Ordinal).ToList())
            {
                if (roots.TryGetValue(key, out var node))
                    Member(content[key], node, key);
            }

            _path.RemoveAt(_path.Count - 1);
        }

        private void Member(JsonNode? value, XMemberNode node, string segment)
        {
            _path.Add(segment);
            switch (value)
            {
                case JsonArray array when node.Items is { } items:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (items.IsMember && array[i] is JsonObject item && item["id"] is null)
                        {
                            var id = ids.NewId();
                            item["id"] = id;
                            if (minted is not null)
                            {
                                _path.Add(i.ToString(CultureInfo.InvariantCulture));
                                var placeholder = item["as"] is JsonValue marker && marker.TryGetValue<string>(out var text) ? text : null;
                                minted.Add(new MintedId(0, JsonPointer.Build(_path), "member", id, null, placeholder));
                                _path.RemoveAt(_path.Count - 1);
                            }
                        }

                        Member(array[i], items, i.ToString(CultureInfo.InvariantCulture));
                    }

                    break;
                case JsonObject obj when node.Properties is not null:
                    foreach (var key in obj.Select(p => p.Key).Order(StringComparer.Ordinal).ToList())
                    {
                        if (node.Properties.TryGetValue(key, out var child))
                            Member(obj[key], child, key);
                    }

                    break;
            }

            _path.RemoveAt(_path.Count - 1);
        }

        /// <summary>The index kind of the record type the current path reaches from the element's kind, or <see langword="null"/>.</summary>
        private string? KindAt() =>
            root is JsonObject element && element["kind"] is JsonValue kind && kind.TryGetValue<string>(out var name) ? IdMinter.KindAt(name, _path) : null;
    }

    /// <summary>
    /// The index kind of the record type a path reaches from an element's kind (each segment a property name, or an item of a list or a
    /// map), or <see langword="null"/> when the path reaches no sub-element type. A model comparison names the sub-element of a field
    /// change with it (<c>Snapshots/FieldOwners.cs</c>).
    /// </summary>
    /// <param name="kindName">The element's kind, as its <c>kind</c> states it.</param>
    /// <param name="path">The segments below the element's root.</param>
    /// <returns>The kind, such as <c>attribute</c>, or <see langword="null"/>.</returns>
    internal static string? KindAt(string kindName, IReadOnlyList<string> path)
    {
        if (!KindInfo.TryGet(kindName, out var info))
            return null;
        Type? type = info.ClrType;
        foreach (var segment in path)
        {
            type = type is not null && ItemType(type) is { } item ? item : PropertyType(type, segment);
            if (type is null)
                return null;
        }

        return ModelIndexer.SubElementKind(type);
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
