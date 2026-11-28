using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Maquettiste.Engine.Model;

/// <summary>
/// The serializer options for model records: <see cref="JsonSerializerDefaults.Web"/>, no trailing commas, nulls omitted.
/// The union types carry attribute-applied converters, so any <see cref="JsonSerializerDefaults.Web"/> options serialize engine results
/// (host-contracts requirement 5). The one converter registered here reads and writes strings exactly as the built-in one does; only
/// while the engine's loader reads its own files does it take ids from the loader's string pool (<see cref="Json.PooledStrings"/>), so
/// equal ids share one instance. One options instance serves both, so the contracts of the model types are built once.
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
        options.Converters.Add(new Json.PooledStringConverter());
        options.MakeReadOnly();
        return options;
    }
}
