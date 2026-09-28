using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Json;

/// <summary>
/// Reads model documents into records with <see cref="EngineJson.Options"/>, dispatching elements on <c>kind</c>. Schema
/// validation runs first, so this reader assumes well-formed input and throws <see cref="JsonException"/> otherwise.
/// </summary>
internal static class ElementReader
{
    /// <summary>Reads an element, dispatching on its <c>kind</c>.</summary>
    /// <param name="document">The parsed file.</param>
    /// <returns>The element.</returns>
    /// <exception cref="JsonException">The document has no known <c>kind</c> or does not deserialize.</exception>
    public static Element ReadElement(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || !KindInfo.TryGet(kind.GetString()!, out var info))
            throw new JsonException("The document has no known 'kind'.");

        return (Element)(document.Deserialize(info.ClrType, EngineJson.Options) ?? throw new JsonException("The document is null."));
    }

    /// <summary>Reads a non-element document (settings, pack manifest, extension, manifest, snapshot).</summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="document">The parsed file.</param>
    /// <returns>The record.</returns>
    /// <exception cref="JsonException">The document does not deserialize.</exception>
    public static T Read<T>(JsonElement document) where T : class =>
        document.Deserialize<T>(EngineJson.Options) ?? throw new JsonException("The document is null.");

    /// <summary>Reads an element from bytes.</summary>
    /// <param name="utf8">The file bytes.</param>
    /// <returns>The element.</returns>
    public static Element ReadElement(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        using var doc = JsonDocument.ParseValue(ref reader);
        return ReadElement(doc.RootElement);
    }
}
