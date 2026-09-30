using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Localization;

/// <summary>
/// The project's <c>localization</c> settings interpreted (reference-types-seeds-localization.md sections 3.2 and 3.7): the declared
/// locales in unit order, the fallback chain of a locale and the MQ7201 checks.
/// </summary>
public static partial class LocaleChains
{
    /// <summary>The sub-element kinds a <c>require</c> entry may name, beside the localizable element kinds.</summary>
    public static readonly FrozenSet<string> SubElementKinds =
        FrozenSet.Create(StringComparer.Ordinal, "attribute", "end", "enum-member", "reference-field", "reference-row", "category",
            "state", "transition", "event", "guard", "action", "invoke", "gate", "meaning", "step");

    /// <summary>The element kinds whose standard fields are localizable: every domain-model kind (physical elements, mappings,
    /// diagrams and the two vocabulary containers are out of scope; the categories inside the tree are in).</summary>
    public static readonly FrozenSet<ElementKind> ElementKinds = KindInfo.All
        .Select(k => k.Kind)
        .Where(k => k is not (ElementKind.Database or ElementKind.Table or ElementKind.View or ElementKind.Sequence or ElementKind.Mapping
            or ElementKind.Diagram or ElementKind.TagVocabulary or ElementKind.CategoryTree))
        .ToFrozenSet();

    /// <summary>Every node kind a <c>require</c> entry may name.</summary>
    public static readonly FrozenSet<string> NodeKinds =
        SubElementKinds.Concat(ElementKinds.Select(k => KindInfo.Get(k).Name)).ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z]{4})?(-([A-Za-z]{2}|[0-9]{3}))?(-([A-Za-z0-9]{5,8}|[0-9][A-Za-z0-9]{3}))*$", RegexOptions.CultureInvariant)]
    private static partial Regex Bcp47();

    /// <summary>Whether a tag is a (simplified) BCP 47 language tag: language, optional script, region and variants.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns><see langword="true"/> when well formed.</returns>
    public static bool IsLanguageTag(string? tag) => tag is { Length: > 0 } && Bcp47().IsMatch(tag);

    /// <summary>
    /// A typed locale in canonical BCP 47 case, as the editor normalizes it: an underscore becomes a hyphen, the language is lowercase, a
    /// script Titlecase, a region uppercase (zh_cn to zh-CN, zh_hant_tw to zh-Hant-TW); other subtags lowercase.
    /// </summary>
    /// <param name="tag">The tag as typed.</param>
    /// <returns>The normalized tag (not necessarily well formed: check it with <see cref="IsLanguageTag"/>).</returns>
    public static string Normalize(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var parts = tag.Trim().Replace('_', '-').Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            parts[i] = i == 0 ? p.ToLowerInvariant()
                : i == 1 && p.Length == 4 && p.All(char.IsAsciiLetter) ? char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()
                : p.Length == 2 && p.All(char.IsAsciiLetter) ? p.ToUpperInvariant()
                : p.ToLowerInvariant();
        }

        return string.Join('-', parts);
    }

    /// <summary>How to write a locale, appended to every "not a BCP 47 tag" message.</summary>
    /// <param name="tag">The tag as written.</param>
    /// <returns>The advice, naming the normalized tag when it is well formed and differs.</returns>
    public static string TagAdvice(string? tag)
    {
        var normalized = tag is null ? null : Normalize(tag);
        var suggestion = normalized is not null && !string.Equals(normalized, tag, StringComparison.Ordinal) && IsLanguageTag(normalized)
            ? $"Use '{normalized}': " : "Use ";
        return suggestion + "language-REGION with a hyphen, such as zh-CN, or a language alone, such as fr.";
    }

    /// <summary>The declared locales in unit order: the default first, then ordinal; empty without a <c>localization</c> block.</summary>
    /// <param name="settings">The settings, or <see langword="null"/>.</param>
    /// <returns>The locales.</returns>
    public static IReadOnlyList<string> Ordered(LocalizationSettings? settings)
    {
        if (settings is null)
            return [];
        var rest = settings.Locales.Where(l => !string.Equals(l, settings.DefaultLocale, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return [settings.DefaultLocale, .. rest];
    }

    /// <summary>
    /// The chain of a locale: the locale; then its declared <c>fallbacks</c>, else its supported BCP 47 truncations
    /// (<c>fr-CA</c> gives <c>fr</c>); then the default locale. Distinct, in that order.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="locale">The locale.</param>
    /// <returns>The chain, ending with the default locale.</returns>
    public static IReadOnlyList<string> Chain(LocalizationSettings settings, string locale)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(locale);
        var chain = new List<string> { locale };
        if (settings.Fallbacks.TryGetValue(locale, out var fallbacks))
        {
            chain.AddRange(fallbacks);
        }
        else
        {
            for (var cut = locale.LastIndexOf('-'); cut > 0; cut = locale.LastIndexOf('-', cut - 1))
            {
                var truncated = locale[..cut];
                if (settings.Locales.Contains(truncated, StringComparer.Ordinal))
                    chain.Add(truncated);
            }
        }

        chain.Add(settings.DefaultLocale);
        return chain.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The MQ7201 findings of the settings, as (message, pointer relative to the settings file).</summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The findings, in a stable order.</returns>
    public static IReadOnlyList<(string Message, string Pointer)> Check(LocalizationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var findings = new List<(string, string)>();
        const string root = "/localization";
        if (!IsLanguageTag(settings.DefaultLocale))
            findings.Add(($"The default locale '{settings.DefaultLocale}' is not a BCP 47 language tag. {TagAdvice(settings.DefaultLocale)}", root + "/defaultLocale"));
        for (var i = 0; i < settings.Locales.Count; i++)
        {
            if (!IsLanguageTag(settings.Locales[i]))
                findings.Add(($"The locale '{settings.Locales[i]}' is not a BCP 47 language tag. {TagAdvice(settings.Locales[i])}", root + "/locales/" + Str(i)));
        }

        if (!settings.Locales.Contains(settings.DefaultLocale, StringComparer.Ordinal))
            findings.Add(($"The default locale '{settings.DefaultLocale}' is not in 'locales'.", root + "/locales"));
        foreach (var (locale, list) in settings.Fallbacks.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var pointer = root + "/fallbacks/" + Json.JsonPointer.Escape(locale);
            if (!settings.Locales.Contains(locale, StringComparer.Ordinal))
                findings.Add(($"The fallbacks name the locale '{locale}', which is not in 'locales'.", pointer));
            for (var i = 0; i < list.Count; i++)
            {
                if (!settings.Locales.Contains(list[i], StringComparer.Ordinal))
                    findings.Add(($"The fallback '{list[i]}' of '{locale}' is not in 'locales'.", pointer + "/" + Str(i)));
            }

            if (Cycle(settings, locale) is { } cycle)
                findings.Add(($"The fallbacks of '{locale}' form a cycle: {string.Join(" -> ", cycle)}.", pointer));
        }

        for (var i = 0; i < settings.Require.Count; i++)
        {
            if (!NodeKinds.Contains(settings.Require[i]))
                findings.Add(($"'require' names '{settings.Require[i]}', which is not a localizable node kind.", root + "/require/" + Str(i)));
        }

        return findings;
    }

    /// <summary>A path through the declared fallbacks that comes back to <paramref name="start"/>, or <see langword="null"/>.</summary>
    private static List<string>? Cycle(LocalizationSettings settings, string start)
    {
        var path = new List<string> { start };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Walk(string from)
        {
            if (!settings.Fallbacks.TryGetValue(from, out var next))
                return false;
            foreach (var to in next)
            {
                path.Add(to);
                if (string.Equals(to, start, StringComparison.Ordinal))
                    return true;
                if (seen.Add(to) && Walk(to))
                    return true;
                path.RemoveAt(path.Count - 1);
            }

            return false;
        }

        return Walk(start) ? path : null;
    }

    private static string Str(int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
