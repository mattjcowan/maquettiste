using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Text;

/// <summary>
/// English pluralization with project overrides (W3; engine-design.md section 9). Fixed rules in the style of the usual English
/// inflectors, applied to the last word of a name (so <c>SalesPerson</c> → <c>SalesPeople</c>, <c>InvoiceLine</c> →
/// <c>InvoiceLines</c>); the word keeps its case shape (<c>Person</c> → <c>People</c>, <c>PERSON</c> → <c>PEOPLE</c>).
/// <see cref="InflectionSettings.Plurals"/> (singular → plural) and <see cref="InflectionSettings.Uncountable"/> win over the
/// built-in rules, matched case-insensitively against the whole name first and then against its last word. Culture-invariant and
/// thread-safe (results are memoized per instance), so one instance can be shared across a run's threads.
/// </summary>
internal sealed class Inflector
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly FrozenSet<string> BuiltinUncountable = FrozenSet.ToFrozenSet(
    [
        "equipment", "information", "rice", "money", "species", "series", "fish", "sheep", "deer", "news", "data", "metadata",
        "feedback", "software", "hardware", "firmware", "middleware", "staff", "moose", "bison", "police", "aircraft", "offspring",
    ], StringComparer.Ordinal);

    private static readonly (string Singular, string Plural)[] BuiltinIrregular =
    [
        ("person", "people"), ("man", "men"), ("woman", "women"), ("child", "children"), ("tooth", "teeth"), ("foot", "feet"),
        ("goose", "geese"), ("mouse", "mice"), ("louse", "lice"), ("ox", "oxen"), ("criterion", "criteria"),
        ("phenomenon", "phenomena"), ("datum", "data"), ("medium", "media"), ("leaf", "leaves"), ("loaf", "loaves"),
        ("thief", "thieves"), ("movie", "movies"), ("cookie", "cookies"), ("zombie", "zombies"), ("quiz", "quizzes"),
        ("die", "dice"), ("cactus", "cacti"), ("radius", "radii"), ("alumnus", "alumni"),
    ];

    /// <summary>Singular words ending in <c>-us</c> whose plural is <c>-uses</c> (the singular rules keep them and strip <c>es</c>).</summary>
    private const string UsWords = "status|bus|campus|virus|bonus|census|corpus|focus|genus|plus|minus|nexus|apparatus|prospectus|stimulus"
        + "|syllabus|thesaurus|consensus|chorus|circus|sinus|octopus|walrus|fungus|abacus|citrus|lotus|hiatus|impetus|onus|opus|surplus";

    private static readonly (Regex Pattern, string Replacement)[] PluralRules =
    [
        (new Regex("(alias|canvas|atlas|gas|iris|lens)$", Options), "$1es"),
        (new Regex("(matr|vert|ind)(ix|ex)$", Options), "$1ices"),
        (new Regex("(x|ch|ss|sh|zz)$", Options), "$1es"),
        (new Regex("us$", Options), "uses"),
        (new Regex("^(ax|test)is$", Options), "$1es"),
        (new Regex("sis$", Options), "ses"),
        (new Regex("([^aeiouy]|qu)y$", Options), "$1ies"),
        (new Regex("(wol|hal|shel|cal|el|sel|dwar|scar|whar)f$", Options), "$1ves"),
        (new Regex("(li|wi|kni)fe$", Options), "$1ves"),
        (new Regex("(buffal|tomat|potat|her|ech|volcan)o$", Options), "$1oes"),
        (new Regex("s$", Options), "s"),
        (new Regex("$", Options), "s"),
    ];

    private static readonly (Regex Pattern, string Replacement)[] SingularRules =
    [
        (new Regex("(alias|canvas|atlas|gas|iris|lens)es$", Options), "$1"),
        // Singular words that end in "s" stay as they are. Words in -us are listed rather than matched generically, so plurals
        // such as "menus" and "houses" still lose their "s" (as the Rails inflector does).
        (new Regex("(ss|is|alias|canvas|atlas|^gas|^lens|" + UsWords + ")$", Options), "$1"),
        (new Regex("(matr)ices$", Options), "$1ix"),
        (new Regex("(vert|ind)ices$", Options), "$1ex"),
        (new Regex("(analy|diagno|parenthe|progno|synop|the|cri|hypothe|empha|ellip|oa)ses$", Options), "$1sis"),
        (new Regex("^(ax|test)es$", Options), "$1is"),
        (new Regex("(wol|hal|shel|cal|el|sel|dwar|scar|whar)ves$", Options), "$1f"),
        (new Regex("(li|wi|kni)ves$", Options), "$1fe"),
        (new Regex("(.[^aeiouy]|qu)ies$", Options), "$1y"),
        (new Regex("(x|ch|ss|sh|zz)es$", Options), "$1"),
        (new Regex("(" + UsWords + ")es$", Options), "$1"),
        (new Regex("(buffal|tomat|potat|her|ech|volcan)oes$", Options), "$1o"),
        (new Regex("s$", Options), ""),
    ];

    private readonly FrozenDictionary<string, string> _plurals;
    private readonly FrozenDictionary<string, string> _singulars;
    private readonly FrozenSet<string> _uncountable;
    private readonly ConcurrentDictionary<string, string> _pluralCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _singularCache = new(StringComparer.Ordinal);

    /// <summary>Creates an inflector.</summary>
    /// <param name="settings">The project's <c>inflection</c> overrides, or <see langword="null"/> for the built-in rules only.</param>
    public Inflector(InflectionSettings? settings = null)
    {
        var plurals = new Dictionary<string, string>(StringComparer.Ordinal);
        var singulars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (singular, plural) in BuiltinIrregular)
        {
            plurals[singular] = plural;
            singulars[plural] = singular;
        }

        // Project overrides win over the built-in lists; they are applied in ordinal key order so a clash is deterministic.
        var overridden = new List<string>();
        foreach (var pair in (settings?.Plurals ?? FrozenDictionary<string, string>.Empty).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var singular = pair.Key.ToLowerInvariant();
            var plural = pair.Value.ToLowerInvariant();
            plurals[singular] = plural;
            singulars[plural] = singular;
            overridden.Add(singular);
            overridden.Add(plural);
        }

        var uncountable = new HashSet<string>(BuiltinUncountable, StringComparer.Ordinal);
        foreach (var word in settings?.Uncountable ?? [])
            uncountable.Add(word.ToLowerInvariant());
        foreach (var word in overridden)
            uncountable.Remove(word);
        _plurals = plurals.ToFrozenDictionary(StringComparer.Ordinal);
        _singulars = singulars.ToFrozenDictionary(StringComparer.Ordinal);
        _uncountable = uncountable.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>The number of memoized results (plural and singular); a long-lived owner uses it to bound the memo.</summary>
    internal int MemoizedWords => _pluralCache.Count + _singularCache.Count;

    /// <summary>Pluralizes a word or the last word of a name.</summary>
    /// <param name="word">The word.</param>
    /// <returns>The plural.</returns>
    public string Pluralize(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return _pluralCache.GetOrAdd(word, w => Inflect(w, _plurals, _singulars, PluralRules));
    }

    /// <summary>Singularizes a word or the last word of a name.</summary>
    /// <param name="word">The word.</param>
    /// <returns>The singular.</returns>
    public string Singularize(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return _singularCache.GetOrAdd(word, w => Inflect(w, _singulars, _plurals, SingularRules));
    }

    private string Inflect(string text, FrozenDictionary<string, string> irregular, FrozenDictionary<string, string> inverse,
        (Regex Pattern, string Replacement)[] rules)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return text;

        // The whole name first, so an override such as "Person": "Staff" or "SalesData" (uncountable) is honoured as written.
        var whole = text.ToLowerInvariant();
        if (_uncountable.Contains(whole) || inverse.ContainsKey(whole) && !irregular.ContainsKey(whole))
            return text;
        if (irregular.TryGetValue(whole, out var replaced))
            return MatchShape(text, replaced);

        var start = LastWordStart(text);
        if (start < 0)
            return text;
        var end = start;
        while (end < text.Length && char.IsLetterOrDigit(text[end]))
            end++;
        if (end == start)
            return text;
        var word = text[start..end];
        var lower = word.ToLowerInvariant();
        string result;
        if (_uncountable.Contains(lower) || inverse.ContainsKey(lower) && !irregular.ContainsKey(lower))
            result = lower;
        else if (!irregular.TryGetValue(lower, out result!))
            result = ApplyRules(lower, rules);
        return string.Concat(text.AsSpan(0, start), MatchShape(word, result), text.AsSpan(end));
    }

    private static string ApplyRules(string lower, (Regex Pattern, string Replacement)[] rules)
    {
        foreach (var (pattern, replacement) in rules)
        {
            if (pattern.IsMatch(lower))
                return pattern.Replace(lower, replacement, 1);
        }

        return lower;
    }

    /// <summary>Gives <paramref name="result"/> (lowercase) the case shape of <paramref name="original"/>.</summary>
    private static string MatchShape(string original, string result)
    {
        var letters = original.Where(char.IsLetter).ToList();
        if (letters.Count > 1 && letters.All(char.IsUpper))
            return result.ToUpperInvariant();
        return char.IsUpper(original[0]) ? Casing.Capitalize(result) : result;
    }

    /// <summary>
    /// The start index of the last word (the <see cref="Casing.Words"/> boundaries), or -1 when the text has no letter or digit.
    /// Trailing non-alphanumerics are skipped, so the word found is the last alphanumeric run's last word.
    /// </summary>
    private static int LastWordStart(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1]))
            end--;
        if (end == 0)
            return -1;
        var runStart = end;
        while (runStart > 0 && char.IsLetterOrDigit(text[runStart - 1]))
            runStart--;
        var start = runStart;
        for (var i = runStart + 1; i < end; i++)
        {
            var c = text[i];
            if (!char.IsUpper(c))
                continue;
            var previous = text[i - 1];
            var nextIsLower = i + 1 < end && char.IsLower(text[i + 1]);
            if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
                start = i;
        }

        return start;
    }
}
