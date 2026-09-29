using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// An element description: Markdown text (JSON string) or a sidecar file reference (JSON <c>{ "file": "invoice.md" }</c>).
/// Exactly one of <see cref="Text"/> and <see cref="File"/> is set.
/// </summary>
[JsonConverter(typeof(DescriptionJsonConverter))]
public sealed record Description
{
    /// <summary>The Markdown text, when the description is inline.</summary>
    public string? Text { get; init; }

    /// <summary>The sidecar file name, relative to the element file's folder.</summary>
    public string? File { get; init; }
}

/// <summary>
/// An attribute type: a built-in scalar keyword (JSON string, for example <c>"uuid"</c>) or a reference to an enum,
/// value object or custom scalar type (JSON <c>{ "ref": "&lt;id&gt;" }</c>). Exactly one of the two is set.
/// </summary>
[JsonConverter(typeof(TypeRefJsonConverter))]
public sealed record TypeRef
{
    /// <summary>A built-in scalar keyword from <see cref="BuiltinTypes.All"/>.</summary>
    public string? Builtin { get; init; }

    /// <summary>The id of an enum, value object, custom scalar type or reference type.</summary>
    [ElementRef(ElementKind.Enum, ElementKind.ValueObject, ElementKind.ScalarType, ElementKind.ReferenceType)]
    public string? Ref { get; init; }
}

/// <summary>The upper bound of a relation end: JSON <c>1</c> or <c>"*"</c>.</summary>
[JsonConverter(typeof(MaxCardinalityJsonConverter))]
public enum MaxCardinality
{
    /// <summary>At most one (JSON <c>1</c>).</summary>
    One,

    /// <summary>Any number (JSON <c>"*"</c>).</summary>
    Many,
}

internal sealed class DescriptionJsonConverter : JsonConverter<Description>
{
    public override Description? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new Description { Text = reader.GetString() };
            case JsonTokenType.StartObject:
                string? file = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("Expected a property name in a description object.");
                    var name = reader.GetString();
                    reader.Read();
                    if (name == "file" && reader.TokenType == JsonTokenType.String)
                        file = reader.GetString();
                    else
                        throw new JsonException($"Unexpected property '{name}' in a description object.");
                }

                return file is null ? throw new JsonException("A description object needs a 'file'.") : new Description { File = file };
            default:
                throw new JsonException("A description is a string or an object with 'file'.");
        }
    }

    public override void Write(Utf8JsonWriter writer, Description value, JsonSerializerOptions options)
    {
        if (value.File is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("file", value.File);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStringValue(value.Text ?? "");
        }
    }
}

internal sealed class TypeRefJsonConverter : JsonConverter<TypeRef>
{
    public override TypeRef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new TypeRef { Builtin = reader.GetString() };
            case JsonTokenType.StartObject:
                string? id = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("Expected a property name in a type reference.");
                    var name = reader.GetString();
                    reader.Read();
                    if (name == "ref" && reader.TokenType == JsonTokenType.String)
                        id = reader.GetString();
                    else
                        throw new JsonException($"Unexpected property '{name}' in a type reference.");
                }

                return id is null ? throw new JsonException("A type reference object needs a 'ref'.") : new TypeRef { Ref = id };
            default:
                throw new JsonException("A type is a built-in keyword or an object with 'ref'.");
        }
    }

    public override void Write(Utf8JsonWriter writer, TypeRef value, JsonSerializerOptions options)
    {
        if (value.Ref is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("ref", value.Ref);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStringValue(value.Builtin ?? "");
        }
    }
}

internal sealed class MaxCardinalityJsonConverter : JsonConverter<MaxCardinality>
{
    public override MaxCardinality Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n) && n == 1)
            return MaxCardinality.One;
        if (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("*"u8))
            return MaxCardinality.Many;
        throw new JsonException("A maximum cardinality is 1 or \"*\".");
    }

    public override void Write(Utf8JsonWriter writer, MaxCardinality value, JsonSerializerOptions options)
    {
        if (value == MaxCardinality.One)
            writer.WriteNumberValue(1);
        else
            writer.WriteStringValue("*");
    }
}
