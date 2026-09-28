using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Maquettiste.Engine.Model;

/// <summary>
/// The serializer options for model records: <see cref="JsonSerializerDefaults.Web"/>, no trailing commas, nulls omitted.
/// No converter is registered on the options; the union types carry attribute-applied converters, so any
/// <see cref="JsonSerializerDefaults.Web"/> options serialize engine results (host-contracts requirement 5).
/// </summary>
public static class EngineJson
{
    /// <summary>The shared, read-only options.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        return options;
    }
}
