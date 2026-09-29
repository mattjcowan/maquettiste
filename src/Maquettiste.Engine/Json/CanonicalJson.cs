using System.Buffers;
using System.Globalization;
using System.Text;
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
            WriteNode(writer, normalized, layout);
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
                var list = items.ToList();
                if (layout.TrimTrailingNulls)
                {
                    while (list.Count > 0 && list[^1] is null)
                        list.RemoveAt(list.Count - 1);
                }

                var result = new JsonArray();
                foreach (var item in list)
                    result.Add(item);
                return result;
            }

            default:
                return node.DeepClone();
        }
    }

    /// <summary>Writes a normalized node, following the layout so that <c>x-layout: row-per-line</c> arrays get their row form.</summary>
    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node, ObjectLayout layout)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj)
                {
                    writer.WritePropertyName(key);
                    var child = layout.HasKeys
                        ? layout.Properties.TryGetValue(key, out var property) ? property.Layout : ObjectLayout.FreeForm
                        : layout.MapValues ?? ObjectLayout.FreeForm;
                    WriteNode(writer, value, child);
                }

                writer.WriteEndObject();
                return;
            case JsonArray array when layout.RowPerLine && array.Count > 0:
            {
                writer.WriteStartArray();
                var indent = "\n" + new string(' ', writer.CurrentDepth * 2);
                foreach (var item in array)
                {
                    var row = new StringBuilder(indent);
                    WriteCompact(row, item);
                    writer.WriteRawValue(row.ToString(), skipInputValidation: true);
                }

                writer.WriteEndArray();
                return;
            }

            case JsonArray array:
            {
                var itemLayout = layout.Items ?? ObjectLayout.FreeForm;
                writer.WriteStartArray();
                foreach (var item in array)
                    WriteNode(writer, item, itemLayout);
                writer.WriteEndArray();
                return;
            }

            default:
                node.WriteTo(writer);
                return;
        }
    }

    /// <summary>
    /// The one-line row form (reference-types-seeds-localization.md section 2.1): one space inside braces, none inside brackets,
    /// <c>", "</c> between items, <c>": "</c> after keys, numbers as their shortest plain decimal text.
    /// </summary>
    private static void WriteCompact(StringBuilder text, JsonNode? node)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                return;
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    text.Append("{}");
                    return;
                }

                text.Append("{ ");
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                        text.Append(", ");
                    first = false;
                    text.Append(JsonValue.Create(key).ToJsonString(CompactOptions)).Append(": ");
                    WriteCompact(text, value);
                }

                text.Append(" }");
                return;
            case JsonArray array:
                text.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                        text.Append(", ");
                    WriteCompact(text, array[i]);
                }

                text.Append(']');
                return;
            default:
                var json = node.ToJsonString(CompactOptions);
                text.Append(node.GetValueKind() == JsonValueKind.Number ? CanonicalNumber(json) : json);
                return;
        }
    }

    private static readonly JsonSerializerOptions CompactOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The shortest plain decimal text of a JSON number, computed on its text (never through <see cref="double"/>): no exponent, no
    /// <c>+</c>, no leading zeros, no trailing fractional zeros; <c>1e3</c> and <c>1000.0</c> give <c>1000</c>, <c>0.360</c> gives <c>0.36</c>.
    /// </summary>
    /// <param name="text">A JSON number's text.</param>
    /// <returns>The canonical text; the input when its exponent is beyond ±1000.</returns>
    internal static string CanonicalNumber(string text)
    {
        var negative = text.StartsWith('-');
        var body = negative || text.StartsWith('+') ? text[1..] : text;
        var exponent = 0;
        var e = body.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            if (!int.TryParse(body.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) || Math.Abs(exponent) > 1000)
                return text;
            body = body[..e];
        }

        var dot = body.IndexOf('.', StringComparison.Ordinal);
        var digits = dot < 0 ? body : string.Concat(body.AsSpan(0, dot), body.AsSpan(dot + 1));
        var point = (dot < 0 ? body.Length : dot) + exponent;
        var lead = 0;
        while (lead < digits.Length && digits[lead] == '0')
            lead++;
        digits = digits[lead..].TrimEnd('0');
        point -= lead;
        if (digits.Length == 0)
            return "0";
        string whole, fraction;
        if (point <= 0)
        {
            whole = "0";
            fraction = new string('0', -point) + digits;
        }
        else if (point >= digits.Length)
        {
            whole = digits + new string('0', point - digits.Length);
            fraction = "";
        }
        else
        {
            whole = digits[..point];
            fraction = digits[point..];
        }

        return (negative ? "-" : "") + whole + (fraction.Length > 0 ? "." + fraction : "");
    }

    private static long SortValue(JsonNode? item, string key) =>
        item is JsonObject obj && obj.TryGetPropertyValue(key, out var value) && value is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}
