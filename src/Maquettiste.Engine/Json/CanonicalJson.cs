using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Json;

/// <summary>
/// The canonical JSON writer (SPEC section 11): UTF-8 without BOM, LF, two-space indent, one space after <c>:</c>, trailing
/// newline, minimal escaping; keys in the schema's <c>x-order</c>, free-form maps ordinal, arrays in their order except those
/// whose schema declares <c>x-sort</c> (stable-sorted by that integer key, missing = 0); values equal to their schema default
/// (unless an <c>x-default-unless</c> sibling is present) and nulls omitted; <c>$schema</c> first and computed from the document path. Numbers read from text
/// keep their text; numbers from CLR values are written by <see cref="Utf8JsonWriter"/>.
/// </summary>
/// <param name="schemas">The schema registry that supplies layouts.</param>
internal sealed class CanonicalJson(ISchemaRegistry schemas) : ICanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <inheritdoc/>
    public byte[] Write(JsonNode document, string fileName, string documentPath)
    {
        ArgumentNullException.ThrowIfNull(document);
        var layout = schemas.GetLayout(fileName);
        var normalized = Normalize(document, layout);
        if (normalized is JsonObject obj && layout.Properties.ContainsKey("$schema"))
        {
            var withSchema = new JsonObject { ["$schema"] = SchemaReference(fileName, documentPath) };
            foreach (var (key, value) in obj.ToList())
            {
                obj.Remove(key);
                withSchema[key] = value;
            }

            normalized = withSchema;
        }

        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            if (normalized is null)
                writer.WriteNullValue();
            else
                normalized.WriteTo(writer);
        }

        var result = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(result);
        result[^1] = (byte)'\n';
        return result;
    }

    /// <inheritdoc/>
    public byte[] Serialize<T>(T value, string fileName, string documentPath)
    {
        ArgumentNullException.ThrowIfNull(value);
        // The runtime type, so an Element-typed value serializes as its concrete record.
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), EngineJson.Options) ?? throw new ArgumentNullException(nameof(value));
        return Write(node, fileName, documentPath);
    }

    /// <inheritdoc/>
    public bool IsCanonical(ReadOnlySpan<byte> bytes, string fileName, string documentPath)
    {
        if (!System.Text.Unicode.Utf8.IsValid(bytes))
            return false; // canonical text is UTF-8; bad bytes would throw when a string is transcoded below
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), ReadOptions);
        }
        catch (JsonException)
        {
            return false; // includes a duplicated property name
        }

        using (document)
            return IsCanonical(bytes, document.RootElement, fileName, documentPath);
    }

    /// <summary>
    /// <see cref="IsCanonical(ReadOnlySpan{byte}, string, string)"/> for bytes already parsed (without duplicated property names), as
    /// the loader has them: decided on the element tree by <see cref="CanonicalCheck"/>, else by the node-based writer.
    /// </summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="root">The parsed bytes.</param>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="documentPath">The document's path.</param>
    /// <returns>Whether the bytes are canonical.</returns>
    internal bool IsCanonical(ReadOnlySpan<byte> bytes, JsonElement root, string fileName, string documentPath)
    {
        if (!System.Text.Unicode.Utf8.IsValid(bytes))
            return false;
        var layout = schemas.GetLayout(fileName);
        var reference = root.ValueKind == JsonValueKind.Object && layout.Properties.ContainsKey("$schema") ? SchemaReference(fileName, documentPath) : "";
        return CanonicalCheck.IsCanonical(bytes, root, layout, reference, WriterOptions) ?? IsCanonicalByNodes(bytes, fileName, documentPath);
    }

    /// <summary>The node-based check: parse into nodes, write canonically, compare.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="documentPath">The document's path.</param>
    /// <returns>Whether the bytes are canonical.</returns>
    internal bool IsCanonicalByNodes(ReadOnlySpan<byte> bytes, string fileName, string documentPath)
    {
        if (!System.Text.Unicode.Utf8.IsValid(bytes))
            return false;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bytes, documentOptions: ReadOptions);
        }
        catch (JsonException)
        {
            return false; // includes a duplicated property name
        }

        return node is not null && Write(node, fileName, documentPath).AsSpan().SequenceEqual(bytes);
    }

    /// <summary>
    /// Computes the <c>$schema</c> value: the relative path from the document's folder to <c>.maquettiste/.schema/v1/&lt;file&gt;</c>.
    /// A path containing a <c>.maquettiste</c> segment is taken relative to it; any other path is taken as model-root relative.
    /// </summary>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="documentPath">The document path.</param>
    /// <returns>The reference, for example <c>../../.schema/v1/entity.json</c>.</returns>
    internal static string SchemaReference(string fileName, string documentPath)
    {
        var segments = documentPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folders = segments.Length - 1;
        var root = Array.LastIndexOf(segments, ".maquettiste");
        var depth = root >= 0 && root < segments.Length - 1 ? folders - (root + 1) : Math.Max(folders, 0);
        return string.Concat(Enumerable.Repeat("../", depth)) + ".schema/v1/" + fileName;
    }

    private static JsonNode? Normalize(JsonNode? node, ObjectLayout layout)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj when layout.HasKeys:
            {
                var result = new JsonObject();
                foreach (var key in layout.Keys)
                {
                    if (key == "$schema" || !obj.TryGetPropertyValue(key, out var value) || value is null)
                        continue;
                    var property = layout.Properties[key];
                    var normalized = Normalize(value, property.Layout);
                    if (property.DefaultNode is { } defaultValue && JsonNode.DeepEquals(normalized, defaultValue)
                        && (property.DefaultUnless is not { } unless || !obj.TryGetPropertyValue(unless, out var gate) || gate is null))
                        continue;
                    result[key] = normalized;
                }

                // Keys the schema does not declare are kept, after the declared ones, in ordinal order.
                foreach (var (key, value) in obj.Where(p => !layout.Properties.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (value is not null)
                        result[key] = Normalize(value, ObjectLayout.FreeForm);
                }

                return result;
            }

            case JsonObject obj:
            {
                var valueLayout = layout.MapValues ?? ObjectLayout.FreeForm;
                var result = new JsonObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    result[key] = Normalize(value, valueLayout);
                return result;
            }

            case JsonArray array:
            {
                var itemLayout = layout.Items ?? ObjectLayout.FreeForm;
                var items = array.Select(item => Normalize(item, itemLayout));
                if (layout.SortKey is { } sortKey)
                    items = items.OrderBy(item => SortValue(item, sortKey)); // OrderBy is stable: equal positions keep array order
                var result = new JsonArray();
                foreach (var item in items.ToList())
                    result.Add(item);
                return result;
            }

            default:
                return node.DeepClone();
        }
    }

    private static long SortValue(JsonNode? item, string key) =>
        item is JsonObject obj && obj.TryGetPropertyValue(key, out var value) && value is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}
