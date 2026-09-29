using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// The translations of one locale for one shard: a package, the elements with no package (<c>root</c>) or the reference data
/// (<c>model/locales/&lt;locale&gt;/</c>; reference-types-seeds-localization.md section 3.3). Not an element: it has no id.
/// </summary>
public sealed record LocaleShard
{
    /// <summary>The <c>$schema</c> value.</summary>
    [JsonPropertyName("$schema")]
    public string? SchemaPath { get; init; }

    /// <summary>Always <c>locale-shard</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The BCP 47 locale.</summary>
    public required string Locale { get; init; }

    /// <summary>The package id, <c>root</c> or <c>reference-data</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>The translations, keyed by element or sub-element id.</summary>
    public IReadOnlyDictionary<string, TranslationEntry> Entries { get; init; } = ImmutableDictionary<string, TranslationEntry>.Empty;
}

/// <summary>The translated standard fields of one node, with the hash of the default text each was translated from.</summary>
public sealed record TranslationEntry
{
    /// <summary>The translated display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The translated plural name.</summary>
    public string? PluralName { get; init; }

    /// <summary>The translated label of a reference row.</summary>
    public string? Label { get; init; }

    /// <summary>The translated description (text or a Markdown sidecar).</summary>
    public Description? Description { get; init; }

    /// <summary>Field name to the first 8 hex digits of SHA-256 over the default-locale text it was translated from.</summary>
    public IReadOnlyDictionary<string, string> Src { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>The <c>localization</c> block of the project settings.</summary>
public sealed record LocalizationSettings
{
    /// <summary>The language of the texts inside element files.</summary>
    public required string DefaultLocale { get; init; }

    /// <summary>The supported locales (BCP 47), including the default.</summary>
    public IReadOnlyList<string> Locales { get; init; } = [];

    /// <summary>Per-locale fallback chains that replace the default chain.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Fallbacks { get; init; } = ImmutableDictionary<string, IReadOnlyList<string>>.Empty;

    /// <summary>The node kinds the completeness rules count; empty means every localizable node.</summary>
    public IReadOnlyList<string> Require { get; init; } = [];
}

/// <summary>The <c>referenceData</c> block of the project settings.</summary>
public sealed record ReferenceDataSettings
{
    /// <summary>The storage strategies the project declares, by key; the engine knows none.</summary>
    public IReadOnlyDictionary<string, StrategyDeclaration> Strategies { get; init; } = ImmutableDictionary<string, StrategyDeclaration>.Empty;

    /// <summary>How the Reference data screen groups types: <c>category</c>, <c>tag:&lt;prefix&gt;</c> or <c>property:&lt;name&gt;</c>.</summary>
    public string GroupBy { get; init; } = "category";
}

/// <summary>A storage strategy the project declares.</summary>
public sealed record StrategyDeclaration
{
    /// <summary>What the strategy means, for people.</summary>
    public string? Description { get; init; }

    /// <summary>Whether collection attributes may use the strategy, overall or per dialect.</summary>
    public CollectionSupport Collections { get; init; } = CollectionSupport.None;

    /// <summary>The JSON Schema <c>properties</c> of the strategy's options.</summary>
    public JsonElement? Options { get; init; }

    /// <summary>The option names a choice must give.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];
}

/// <summary>A boolean, or a map from dialect (or <c>*</c> for the rest) to boolean.</summary>
[JsonConverter(typeof(CollectionSupportJsonConverter))]
public sealed record CollectionSupport
{
    /// <summary>Collections are never supported.</summary>
    public static CollectionSupport None { get; } = new() { All = false };

    /// <summary>The single answer, when the value is a boolean.</summary>
    public bool? All { get; init; }

    /// <summary>The per-dialect answers, when the value is a map.</summary>
    public IReadOnlyDictionary<string, bool> ByDialect { get; init; } = ImmutableDictionary<string, bool>.Empty;

    /// <summary>Evaluates the support for a dialect: the boolean, else the dialect's entry, else <c>*</c>, else false.</summary>
    /// <param name="dialect">The database's dialect.</param>
    /// <returns>Whether collections are supported.</returns>
    public bool For(string dialect) =>
        All ?? (ByDialect.TryGetValue(dialect, out var value) ? value : ByDialect.TryGetValue("*", out var rest) && rest);
}

internal sealed class CollectionSupportJsonConverter : JsonConverter<CollectionSupport>
{
    public override CollectionSupport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
            case JsonTokenType.False:
                return new CollectionSupport { All = reader.GetBoolean() };
            case JsonTokenType.StartObject:
                var map = JsonSerializer.Deserialize<Dictionary<string, bool>>(ref reader, options) ?? [];
                return new CollectionSupport { ByDialect = map.ToImmutableSortedDictionary(StringComparer.Ordinal) };
            default:
                throw new JsonException("Collection support is a boolean or a map from dialect to boolean.");
        }
    }

    public override void Write(Utf8JsonWriter writer, CollectionSupport value, JsonSerializerOptions options)
    {
        if (value.All is { } all)
        {
            writer.WriteBooleanValue(all);
            return;
        }

        writer.WriteStartObject();
        foreach (var (dialect, supported) in value.ByDialect.OrderBy(p => p.Key, StringComparer.Ordinal))
            writer.WriteBoolean(dialect, supported);
        writer.WriteEndObject();
    }
}
