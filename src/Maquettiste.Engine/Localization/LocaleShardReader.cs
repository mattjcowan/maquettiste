using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Localization;

/// <summary>
/// Forward-only reading of locale shards (reference-types-seeds-localization.md section 3.8): the kind probe that lets the loader
/// dispatch a file on its content, and the streamed read of a shard that passed schema validation before (no document tree).
/// </summary>
internal static class LocaleShardReader
{
    private static readonly JsonReaderOptions Options = new() { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 };

    /// <summary>Whether the file's top-level <c>kind</c> is <c>locale-shard</c> (a cheap scan that stops at the first top-level kind).</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns><see langword="true"/> for a shard.</returns>
    public static bool IsShard(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, Options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isKind = reader.ValueTextEquals("kind"u8);
                if (!reader.Read())
                    return false;
                if (isKind)
                    return reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("locale-shard"u8);
                reader.Skip();
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>Reads a schema-valid shard in one pass.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="shard">The shard.</param>
    /// <returns><see langword="false"/> when the content is not the expected shape (the caller then reads it the full way).</returns>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out LocaleShard shard)
    {
        shard = null!;
        try
        {
            var reader = new Utf8JsonReader(bytes, Options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            string? schema = null, kind = null, locale = null, scope = null;
            // A plain map in file order, as the full read deserializes it (a canonical shard is ordinal by id already); a sorted
            // immutable tree per shard and per entry was most of the read's allocations.
            var entries = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "$schema": schema = reader.GetString(); break;
                    case "kind": kind = reader.GetString(); break;
                    case "locale": locale = reader.GetString(); break;
                    case "scope": scope = reader.GetString(); break;
                    case "entries":
                        if (reader.TokenType != JsonTokenType.StartObject)
                            return false;
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var id = reader.GetString()!;
                            reader.Read();
                            if (ReadEntry(ref reader) is not { } entry)
                                return false;
                            entries[id] = entry;
                        }

                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (kind != "locale-shard" || locale is null || scope is null)
                return false;
            shard = new LocaleShard { SchemaPath = schema, Kind = kind, Locale = locale, Scope = scope, Entries = entries };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static TranslationEntry? ReadEntry(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return null;
        string? display = null, plural = null, label = null;
        Description? description = null;
        Dictionary<string, string>? src = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "displayName": display = reader.GetString(); break;
                case "pluralName": plural = reader.GetString(); break;
                case "label": label = reader.GetString(); break;
                case "description":
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        description = new Description { Text = reader.GetString() };
                    }
                    else if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        string? file = null;
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var key = reader.GetString();
                            reader.Read();
                            if (key == "file")
                                file = reader.GetString();
                            else
                                reader.Skip();
                        }

                        if (file is null)
                            return null;
                        description = new Description { File = file };
                    }
                    else
                    {
                        return null;
                    }

                    break;
                case "src":
                    if (reader.TokenType != JsonTokenType.StartObject)
                        return null;
                    src ??= new Dictionary<string, string>(4, StringComparer.Ordinal);
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var field = reader.GetString()!;
                        reader.Read();
                        src[field] = reader.GetString()!;
                    }

                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new TranslationEntry { DisplayName = display, PluralName = plural, Label = label, Description = description, Src = src ?? (IReadOnlyDictionary<string, string>)ImmutableDictionary<string, string>.Empty };
    }

    /// <summary>The description sidecars a shard references, as (pointer, file).</summary>
    /// <param name="shard">The shard.</param>
    /// <returns>The references, ordinal by id.</returns>
    public static ImmutableArray<(string Pointer, string File)> SidecarsOf(LocaleShard shard) =>
    [
        .. shard.Entries.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Where(p => p.Value.Description?.File is not null)
            .Select(p => ("/entries/" + p.Key + "/description", p.Value.Description!.File!)),
    ];
}
