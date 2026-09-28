using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Maquettiste.Engine.Json;

/// <summary>
/// Decides whether a parsed document is in canonical form without building a <see cref="System.Text.Json.Nodes.JsonNode"/> tree:
/// it writes the canonical form straight from the <see cref="JsonElement"/> tree, with the rules of <see cref="CanonicalJson"/>, into
/// a buffer writer that compares each written chunk with the file bytes. Where the node-based writer's default comparison could
/// differ from a plain comparison (numbers that are not both integer literals, non-empty object or array defaults), it answers
/// "undecided" and <see cref="CanonicalJson"/> falls back to the node-based check, so the verdict is always the same as before.
/// </summary>
internal static class CanonicalCheck
{
    /// <summary>Checks a document.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="root">The parsed document (without duplicated property names).</param>
    /// <param name="layout">The root layout.</param>
    /// <param name="schemaReference">The computed <c>$schema</c> value.</param>
    /// <param name="writerOptions">The canonical writer options.</param>
    /// <returns>Whether the bytes are canonical, or <see langword="null"/> when this check cannot decide.</returns>
    public static bool? IsCanonical(ReadOnlySpan<byte> bytes, JsonElement root, ObjectLayout layout, string schemaReference, JsonWriterOptions writerOptions)
    {
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n')
            return false; // canonical text ends with a newline
        var expected = bytes[..^1];
        var comparer = new ComparingWriter(expected.Length);
        try
        {
            bool decided;
            using (var writer = new Utf8JsonWriter(comparer, writerOptions))
            {
                comparer.Expected = expected;
                decided = root.ValueKind == JsonValueKind.Object && layout.Properties.ContainsKey("$schema")
                    ? WriteRoot(writer, root, layout, schemaReference, comparer)
                    : WriteValue(writer, root, layout, comparer);
                if (decided && !comparer.Mismatch)
                    writer.Flush();
            }

            if (!decided)
                return null;
            return !comparer.Mismatch && comparer.Compared == expected.Length;
        }
        finally
        {
            comparer.Release();
        }
    }

    private static bool WriteRoot(Utf8JsonWriter writer, JsonElement root, ObjectLayout layout, string schemaReference, ComparingWriter comparer)
    {
        writer.WriteStartObject();
        writer.WriteString("$schema", schemaReference);
        if (!WriteDeclared(writer, root, layout, comparer))
            return false;
        writer.WriteEndObject();
        return true;
    }

    /// <summary>Writes a value's canonical form; <see langword="false"/> when undecided. Stops early (returning true) once a mismatch is known.</summary>
    private static bool WriteValue(Utf8JsonWriter writer, JsonElement value, ObjectLayout layout, ComparingWriter comparer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object when layout.HasKeys:
                writer.WriteStartObject();
                if (!WriteDeclared(writer, value, layout, comparer))
                    return false;
                writer.WriteEndObject();
                return true;
            case JsonValueKind.Object:
            {
                // Maps and free-form objects: every key in ordinal order, nulls kept.
                var valueLayout = layout.MapValues ?? ObjectLayout.FreeForm;
                var properties = SortedProperties(value, includeNulls: true, skip: null);
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    if (!WriteValue(writer, property.Value, valueLayout, comparer))
                        return false;
                }

                writer.WriteEndObject();
                return true;
            }

            case JsonValueKind.Array:
            {
                var itemLayout = layout.Items ?? ObjectLayout.FreeForm;
                writer.WriteStartArray();
                if (layout.SortKey is { } sortKey)
                {
                    var items = new List<(long Key, int Index, JsonElement Item)>(value.GetArrayLength());
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (SortValue(item, itemLayout, sortKey) is not { } key)
                            return false;
                        items.Add((key, index++, item));
                    }

                    items.Sort(static (a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Index.CompareTo(b.Index)); // stable
                    foreach (var (_, _, item) in items)
                    {
                        if (!WriteValue(writer, item, itemLayout, comparer))
                            return false;
                    }
                }
                else
                {
                    foreach (var item in value.EnumerateArray())
                    {
                        if (!WriteValue(writer, item, itemLayout, comparer))
                            return false;
                    }
                }

                writer.WriteEndArray();
                return true;
            }

            default:
                value.WriteTo(writer);
                return true;
        }
    }

    /// <summary>The declared keys in layout order (defaults and nulls dropped), then undeclared non-null keys in ordinal order.</summary>
    private static bool WriteDeclared(Utf8JsonWriter writer, JsonElement value, ObjectLayout layout, ComparingWriter comparer)
    {
        foreach (var key in layout.Keys)
        {
            if (comparer.Mismatch)
                return true; // already known not canonical
            if (key == "$schema" || !value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null)
                continue;
            var property = layout.Properties[key];
            switch (Dropped(value, item, property))
            {
                case null: return false;
                case true: continue;
            }

            writer.WritePropertyName(key);
            if (!WriteValue(writer, item, property.Layout, comparer))
                return false;
        }

        foreach (var property in SortedProperties(value, includeNulls: false, skip: layout))
        {
            writer.WritePropertyName(property.Name);
            if (!WriteValue(writer, property.Value, ObjectLayout.FreeForm, comparer))
                return false;
        }

        return true;
    }

    /// <summary>Whether the canonical writer drops a declared, non-null property for equalling its default.</summary>
    private static bool? Dropped(JsonElement owner, JsonElement value, LayoutProperty property)
    {
        if (property.DefaultNode is null || property.Default is not { } defaultValue)
            return false;
        var equal = EqualsDefault(value, property.Layout, defaultValue);
        if (equal is not true)
            return equal;
        return property.DefaultUnless is not { } unless || !owner.TryGetProperty(unless, out var gate) || gate.ValueKind == JsonValueKind.Null;
    }

    /// <summary>Whether a value, once normalized, deep-equals a default; <see langword="null"/> when undecided.</summary>
    private static bool? EqualsDefault(JsonElement value, ObjectLayout layout, JsonElement defaultValue)
    {
        switch (defaultValue.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False:
                return value.ValueKind == defaultValue.ValueKind;
            case JsonValueKind.String:
                return value.ValueKind == JsonValueKind.String && value.ValueEquals(defaultValue.GetString());
            case JsonValueKind.Number:
                if (value.ValueKind != JsonValueKind.Number)
                    return false;
                if (JsonMarshal.GetRawUtf8Value(value).SequenceEqual(JsonMarshal.GetRawUtf8Value(defaultValue)))
                    return true;
                if (IsIntegerLiteral(value) && IsIntegerLiteral(defaultValue))
                    return value.GetInt64() == defaultValue.GetInt64();
                return null;
            case JsonValueKind.Array:
                if (value.ValueKind != JsonValueKind.Array)
                    return false;
                return defaultValue.GetArrayLength() == 0 ? value.GetArrayLength() == 0 : null;
            case JsonValueKind.Object:
                if (value.ValueKind != JsonValueKind.Object)
                    return false;
                foreach (var _ in defaultValue.EnumerateObject())
                    return null; // a non-empty object default
                return SurvivingCount(value, layout) is { } count ? count == 0 : null;
            default:
                return null;
        }
    }

    /// <summary>The number of properties an object keeps once normalized; <see langword="null"/> when undecided.</summary>
    private static int? SurvivingCount(JsonElement value, ObjectLayout layout)
    {
        var count = 0;
        if (!layout.HasKeys)
        {
            foreach (var _ in value.EnumerateObject())
                count++;
            return count;
        }

        foreach (var key in layout.Keys)
        {
            if (key == "$schema" || !value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null)
                continue;
            switch (Dropped(value, item, layout.Properties[key]))
            {
                case null: return null;
                case false: count++; break;
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Null && !layout.Properties.ContainsKey(property.Name))
                count++;
        }

        return count;
    }

    /// <summary>An array item's sort value as the node-based writer computes it on the normalized item; <see langword="null"/> when undecided.</summary>
    private static long? SortValue(JsonElement item, ObjectLayout itemLayout, string sortKey)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(sortKey, out var value) || value.ValueKind != JsonValueKind.Number)
            return 0;
        if (itemLayout.HasKeys && itemLayout.Properties.TryGetValue(sortKey, out var property))
        {
            switch (Dropped(item, value, property))
            {
                case null: return null;
                case true: return 0; // normalized away
            }
        }

        return value.TryGetInt64(out var n) ? n : 0;
    }

    private static bool IsIntegerLiteral(JsonElement number)
    {
        if (!number.TryGetInt64(out _))
            return false;
        foreach (var b in JsonMarshal.GetRawUtf8Value(number))
        {
            if (b is (byte)'.' or (byte)'e' or (byte)'E')
                return false;
        }

        return true;
    }

    private static List<JsonProperty> SortedProperties(JsonElement value, bool includeNulls, ObjectLayout? skip)
    {
        var list = new List<JsonProperty>();
        foreach (var property in value.EnumerateObject())
        {
            if ((includeNulls || property.Value.ValueKind != JsonValueKind.Null) && (skip is null || !skip.Properties.ContainsKey(property.Name)))
                list.Add(property);
        }

        if (list.Count > 1)
            list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>An <see cref="IBufferWriter{T}"/> that compares what is written with expected bytes instead of keeping it.</summary>
    private sealed class ComparingWriter(int expectedLength) : IBufferWriter<byte>
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Math.Clamp(expectedLength + 16, 256, 64 * 1024));
        private byte[] _expected = [];
        private int _expectedLength;

        /// <summary>The expected bytes (copied once, since a span cannot be kept on the heap).</summary>
        public ReadOnlySpan<byte> Expected
        {
            set
            {
                _expected = ArrayPool<byte>.Shared.Rent(Math.Max(1, value.Length));
                value.CopyTo(_expected);
                _expectedLength = value.Length;
            }
        }

        public bool Mismatch { get; private set; }

        public int Compared { get; private set; }

        public void Advance(int count)
        {
            if (Mismatch)
                return;
            if (Compared + count > _expectedLength || !_buffer.AsSpan(0, count).SequenceEqual(_expected.AsSpan(Compared, count)))
            {
                Mismatch = true;
                return;
            }

            Compared += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer;
        }

        public void Release()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            if (_expected.Length > 0)
                ArrayPool<byte>.Shared.Return(_expected);
            _buffer = [];
            _expected = [];
        }

        private void Grow(int sizeHint)
        {
            if (sizeHint <= _buffer.Length)
                return;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
        }
    }
}
