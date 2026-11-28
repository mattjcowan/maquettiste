using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Json;

/// <summary>
/// The string pool a model read on this thread interns ids into (<see cref="StringPool"/>): a reader that deserializes an element with a
/// pool sets it for the call (<see cref="Use"/>), and every id string the read meets (a string value or property name that is a valid id)
/// comes from the pool, so the element's own id, the ids its references hold and the ids of the file that defines them are one instance
/// (and a built-in type keyword is <see cref="BuiltinTypes"/>' own instance).
/// The pool is per thread and per call (a deserialization is synchronous); without one, ids read as plain strings.
/// </summary>
internal static class PooledStrings
{
    [ThreadStatic]
    private static StringPool? t_pool;

    /// <summary>Sets the pool of this thread's reads until the returned scope is disposed.</summary>
    /// <param name="pool">The pool, or <see langword="null"/> for none.</param>
    /// <returns>The scope that restores the previous pool.</returns>
    public static Scope Use(StringPool? pool)
    {
        var previous = t_pool;
        t_pool = pool;
        return new Scope(previous);
    }

    /// <summary>
    /// The current string or property name token as a string: when a pool is set, the pool's instance of a valid id or a schema reference
    /// (<c>$schema</c>) and the one instance of a built-in type keyword (<see cref="BuiltinTypes.Keyword"/>); else
    /// <see cref="Utf8JsonReader.GetString"/>.
    /// </summary>
    /// <param name="reader">The reader, on a string or property name.</param>
    /// <returns>The string.</returns>
    public static string? ReadString(ref Utf8JsonReader reader)
    {
        if (t_pool is { } pool && reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && !reader.HasValueSequence
            && !reader.ValueIsEscaped)
        {
            var value = reader.ValueSpan;
            if (IdFormat.IsValid(value) && pool.InternAscii(value) is { } pooled)
                return pooled;
            if (BuiltinTypes.Keyword(value) is { } keyword)
                return keyword;
            // A file's $schema (../../.schema/v1/entity.json): one of a few values, written in every file.
            if (value.EndsWith(".json"u8) && value.IndexOf("/.schema/"u8) >= 0 && pool.InternAscii(value) is { } schema)
                return schema;
        }

        return reader.GetString();
    }

    /// <summary>Restores the pool that was set before <see cref="Use"/>.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly StringPool? _previous;

        internal Scope(StringPool? previous) => _previous = previous;

        /// <inheritdoc/>
        public void Dispose() => t_pool = _previous;
    }
}

/// <summary>
/// The string converter of <see cref="EngineJson.Options"/>: reads as the built-in one does (<see cref="Utf8JsonReader.GetString"/>), and
/// while a read on this thread has a pool, ids and the like through it (<see cref="PooledStrings.ReadString"/>); writes exactly as the
/// built-in one.
/// </summary>
internal sealed class PooledStringConverter : JsonConverter<string>
{
    /// <inheritdoc/>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => PooledStrings.ReadString(ref reader);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

    /// <inheritdoc/>
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PooledStrings.ReadString(ref reader)!;

    /// <inheritdoc/>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(value);
}
