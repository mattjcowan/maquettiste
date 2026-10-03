using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The embedded default type maps (engine-design.md section 7.6), <c>Resolution/Dialects/&lt;dialect&gt;.json</c>: built-in keyword →
/// native pattern with <c>{length}</c>, <c>{precision}</c> and <c>{scale}</c> placeholders. The maps are read once into immutable
/// dictionaries.
/// </summary>
internal static class DialectTypeMaps
{
    private static readonly FrozenDictionary<Dialect, FrozenDictionary<string, string>> Defaults = Load();

    /// <summary>The kebab name of a dialect (<c>postgresql</c>, <c>sqlserver</c>, …), which is also its embedded file name.</summary>
    public static string Name(Dialect dialect) => dialect switch
    {
        Dialect.PostgreSql => "postgresql",
        Dialect.SqlServer => "sqlserver",
        Dialect.MySql => "mysql",
        Dialect.Sqlite => "sqlite",
        Dialect.Oracle => "oracle",
        _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
    };

    /// <summary>The embedded default map of a dialect.</summary>
    public static IReadOnlyDictionary<string, string> Default(Dialect dialect) => Defaults[dialect];

    /// <summary>The map of a dialect with the project's <c>typeMaps.&lt;dialect&gt;</c> entries applied one by one.</summary>
    public static IReadOnlyDictionary<string, string> Effective(Dialect dialect, ProjectSettings settings)
    {
        var map = Defaults[dialect];
        if (!settings.TypeMaps.TryGetValue(Name(dialect), out var overrides) || overrides.Count == 0)
            return map;
        var merged = new Dictionary<string, string>(map, StringComparer.Ordinal);
        foreach (var pair in overrides)
            merged[pair.Key] = pair.Value;
        return merged.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// The map entry a column's native type comes from: the keyword itself, or a variant entry when the column asks for a fixed-length
    /// or a Unicode (or single-byte) type: <c>&lt;keyword&gt;:fixed:unicode</c>, <c>&lt;keyword&gt;:fixed:ansi</c>, <c>&lt;keyword&gt;:fixed</c>,
    /// <c>&lt;keyword&gt;:unicode</c>, <c>&lt;keyword&gt;:ansi</c>, tried most specific first; the plain keyword when the map has none of
    /// them (a dialect that stores every text alike, such as PostgreSQL for Unicode).
    /// </summary>
    /// <param name="map">The effective map.</param>
    /// <param name="keyword">The built-in keyword.</param>
    /// <param name="fixedLength">Whether the column is fixed-length.</param>
    /// <param name="unicode"><see langword="true"/> for Unicode, <see langword="false"/> for single-byte, <see langword="null"/> for the default.</param>
    /// <returns>The map key.</returns>
    public static string VariantKey(IReadOnlyDictionary<string, string> map, string keyword, bool fixedLength, bool? unicode)
    {
        if (!fixedLength && unicode is null)
            return keyword;
        var charset = unicode switch { true => ":unicode", false => ":ansi", _ => "" };
        if (fixedLength)
        {
            if (charset.Length > 0 && map.ContainsKey(keyword + ":fixed" + charset))
                return keyword + ":fixed" + charset;
            if (map.ContainsKey(keyword + ":fixed"))
                return keyword + ":fixed";
        }

        return charset.Length > 0 && map.ContainsKey(keyword + charset) ? keyword + charset : keyword;
    }

    /// <summary>Renders a native type from a pattern; missing facets take the given defaults.</summary>
    /// <param name="map">The effective map.</param>
    /// <param name="keyword">The built-in keyword.</param>
    /// <param name="length">The length, or <see langword="null"/> for the default.</param>
    /// <param name="precision">The precision, or <see langword="null"/> for the default.</param>
    /// <param name="scale">The scale, or <see langword="null"/> for the default.</param>
    /// <param name="conventions">The conventions that supply facet defaults.</param>
    /// <returns>The native type; the keyword itself when the map has no entry.</returns>
    public static string Render(IReadOnlyDictionary<string, string> map, string keyword, int? length, int? precision, int? scale,
        EffectiveConventions conventions)
    {
        if (!map.TryGetValue(keyword, out var pattern))
            return keyword;
        return RenderPattern(pattern, keyword, length, precision, scale, conventions);
    }

    /// <summary>
    /// Renders a native type pattern, such as a custom type's native type for the dialect; missing facets take the given defaults.
    /// </summary>
    /// <param name="pattern">The pattern, with <c>{length}</c>, <c>{precision}</c> and <c>{scale}</c> placeholders.</param>
    /// <param name="keyword">The built-in keyword the value has (<c>decimal</c> takes the decimal precision default).</param>
    /// <param name="length">The length, or <see langword="null"/> for the default.</param>
    /// <param name="precision">The precision, or <see langword="null"/> for the default.</param>
    /// <param name="scale">The scale, or <see langword="null"/> for the default.</param>
    /// <param name="conventions">The conventions that supply facet defaults.</param>
    /// <returns>The native type.</returns>
    public static string RenderPattern(string pattern, string keyword, int? length, int? precision, int? scale, EffectiveConventions conventions)
    {
        var isDecimal = string.Equals(keyword, "decimal", StringComparison.Ordinal);
        return pattern
            .Replace("{length}", Invariant(length ?? conventions.DefaultStringLength), StringComparison.Ordinal)
            .Replace("{precision}", Invariant(precision ?? (isDecimal ? conventions.DecimalPrecision : conventions.DatetimePrecision)), StringComparison.Ordinal)
            .Replace("{scale}", Invariant(scale ?? conventions.DecimalScale), StringComparison.Ordinal);
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static FrozenDictionary<Dialect, FrozenDictionary<string, string>> Load()
    {
        var assembly = typeof(DialectTypeMaps).Assembly;
        var maps = new Dictionary<Dialect, FrozenDictionary<string, string>>();
        foreach (var dialect in Enum.GetValues<Dialect>())
        {
            var resource = "Maquettiste.Engine.Resolution.Dialects." + Name(dialect) + ".json";
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Missing embedded dialect map " + resource);
            using var document = JsonDocument.Parse(stream);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                map[property.Name] = property.Value.GetString() ?? "";
            maps[dialect] = map.ToFrozenDictionary(StringComparer.Ordinal);
        }

        return maps.ToFrozenDictionary();
    }
}
