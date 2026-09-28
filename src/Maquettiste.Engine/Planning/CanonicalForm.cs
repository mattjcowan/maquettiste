using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// A canonical text form for hashing values that are not whole documents (settings sections, a pack unit, pack parameters): the
/// value serialized with <see cref="EngineJson.Options"/>, then written compactly with every object's keys in ordinal order, so
/// dictionary enumeration order never reaches a hash.
/// </summary>
internal static class CanonicalForm
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    /// <summary>Returns the canonical compact JSON of a value.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The JSON text.</returns>
    public static string Json<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, EngineJson.Options);
        return Json(element);
    }

    /// <summary>Returns the canonical compact JSON of an element.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The JSON text.</returns>
    public static string Json(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            Write(writer, element);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Returns the canonical compact JSON of a string-keyed map of JSON values.</summary>
    /// <param name="map">The map.</param>
    /// <returns>The JSON text.</returns>
    public static string Json(IReadOnlyDictionary<string, JsonElement> map)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var key in map.Keys.Order(StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                Write(writer, map[key]);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Returns <c>H(tag, canonical JSON)</c> of a value.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="tag">A domain tag.</param>
    /// <param name="value">The value.</param>
    /// <returns>The hash.</returns>
    public static string Hash<T>(string tag, T value) => HashBuilder.Of(tag, Json(value));

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
