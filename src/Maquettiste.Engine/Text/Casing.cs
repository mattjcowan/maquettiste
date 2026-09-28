using System.Text;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Text;

/// <summary>
/// Word splitting and casing (W3; engine-design.md section 9): split on non-alphanumerics and at lower→upper and acronym→word
/// boundaries; digits join the preceding word; acronyms are lowercased (D26): <c>HTTPServer2Id</c> → <c>http_server2_id</c>.
/// Every operation is culture-invariant.
/// </summary>
internal static class Casing
{
    /// <summary>Splits a name into lowercase words.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The words.</returns>
    public static IReadOnlyList<string> Words(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush(words, current);
                continue;
            }

            if (current.Length > 0 && char.IsUpper(c))
            {
                var previous = name[i - 1];
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                // lower→upper or digit→upper starts a word; in a run of capitals the last one starts a word when a lowercase follows.
                if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
                    Flush(words, current);
            }

            current.Append(char.ToLowerInvariant(c));
        }

        Flush(words, current);
        return words;
    }

    /// <summary>Applies a case style to a name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="style">The style; <see cref="CaseStyle.Preserve"/> returns the name unchanged.</param>
    /// <returns>The re-cased name.</returns>
    public static string Apply(string name, CaseStyle style) =>
        style == CaseStyle.Preserve ? name : Join(Words(name), style);

    /// <summary>Joins lowercase words in a case style.</summary>
    /// <param name="words">The words (lowercase).</param>
    /// <param name="style">The style; <see cref="CaseStyle.Preserve"/> concatenates the words as given.</param>
    /// <returns>The joined name.</returns>
    public static string Join(IReadOnlyList<string> words, CaseStyle style)
    {
        ArgumentNullException.ThrowIfNull(words);
        return style switch
        {
            CaseStyle.Snake => string.Join('_', words),
            CaseStyle.Kebab => string.Join('-', words),
            CaseStyle.UpperSnake => string.Join('_', words).ToUpperInvariant(),
            CaseStyle.Pascal => string.Concat(words.Select(Capitalize)),
            CaseStyle.Camel => string.Concat(words.Select((w, i) => i == 0 ? w : Capitalize(w))),
            _ => string.Concat(words),
        };
    }

    /// <summary><c>snake_case</c>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The re-cased name.</returns>
    public static string Snake(string name) => Apply(name, CaseStyle.Snake);

    /// <summary><c>kebab-case</c>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The re-cased name.</returns>
    public static string Kebab(string name) => Apply(name, CaseStyle.Kebab);

    /// <summary><c>PascalCase</c>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The re-cased name.</returns>
    public static string Pascal(string name) => Apply(name, CaseStyle.Pascal);

    /// <summary><c>camelCase</c>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The re-cased name.</returns>
    public static string Camel(string name) => Apply(name, CaseStyle.Camel);

    /// <summary><c>UPPER_SNAKE_CASE</c>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The re-cased name.</returns>
    public static string UpperSnake(string name) => Apply(name, CaseStyle.UpperSnake);

    /// <summary>Upper-cases the first character of a word, invariantly.</summary>
    /// <param name="word">The word.</param>
    /// <returns>The capitalized word.</returns>
    public static string Capitalize(string word) =>
        string.IsNullOrEmpty(word) ? word ?? "" : string.Concat(word[..1].ToUpperInvariant(), word.AsSpan(1));

    private static void Flush(List<string> words, StringBuilder current)
    {
        if (current.Length == 0)
            return;
        words.Add(current.ToString());
        current.Clear();
    }
}
