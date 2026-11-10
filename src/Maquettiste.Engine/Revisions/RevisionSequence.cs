using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Revisions;

/// <summary>Whether a chosen name may be given on a revise (revisions-design.md decisions 14 and 22).</summary>
public enum RevisionNameCheck
{
    /// <summary>The name is a value of the sequence, later than the latest revision.</summary>
    Ok,

    /// <summary>The sequence can't form the name: it is not a value of the sequence (MQ9305 when a scenario is checked).</summary>
    NotAValue,

    /// <summary>The name is a value of the sequence, equal to or earlier than the latest revision (the refusal <c>revision</c>).</summary>
    NotLater,
}

/// <summary>
/// A revision sequence and its written form (revisions-design.md section 6; decisions 3 to 7, 14 and 22): the one function rules,
/// simulation, records and the editor's copy follow, pinned by <c>tests/fixtures/revisions/sequence-cases.json</c>. Built from an entity's
/// levels, as written or merged through its bases, and the separator in force. A revision is text; <c>""</c> is no revision. Levels are
/// numbered 1 to the number of levels, as lifecycles and steps name them. Instances are immutable; every comparison is ordinal and nothing
/// depends on the culture, so answers are the same byte for byte everywhere.
/// </summary>
public sealed class RevisionSequence
{
    private const int ExampleCount = 5;

    private readonly Level[] _levels;
    private readonly int _neverEmpty;

    /// <summary>Builds the sequence of the levels and the separator.</summary>
    /// <param name="levels">The levels, from the most significant.</param>
    /// <param name="separator">The separator; <see langword="null"/> is <see cref="EntityRevisions.DefaultSeparator"/>.</param>
    /// <exception cref="ArgumentException">The levels can't make a sequence whose revisions read back (see <see cref="TryCreate"/>).</exception>
    public RevisionSequence(IReadOnlyList<RevisionLevel> levels, string? separator = null)
        : this(levels, separator ?? EntityRevisions.DefaultSeparator, BuildOrThrow(levels, separator ?? EntityRevisions.DefaultSeparator))
    {
    }

    private RevisionSequence(IReadOnlyList<RevisionLevel> levels, string separator, Level[] built)
    {
        Separator = separator;
        _levels = built;
        Levels = levels;
        _neverEmpty = Array.FindIndex(_levels, l => l.StartEmpty) is var i and >= 0 ? i : _levels.Length;
        Start = Write([.. _levels.Take(_neverEmpty).Select(l => l.First)]);
    }

    /// <summary>The levels the sequence was built from.</summary>
    public IReadOnlyList<RevisionLevel> Levels { get; }

    /// <summary>The separator written between two levels that both hold a value.</summary>
    public string Separator { get; }

    /// <summary>
    /// The revision a new object takes (decision 6): the levels above the first level that starts empty at their first values, the rest
    /// empty; <c>""</c> when level 1 starts empty.
    /// </summary>
    public string Start { get; }

    /// <summary>
    /// Builds the sequence, or says why it can't: the shapes MQ8301 and MQ8303 report that leave a level without clear values or written
    /// revisions that would not read back. Those are a level with neither or both of <c>format</c> and <c>values</c>; a list value that is
    /// empty, repeated or holds the separator; a letters level that skips every letter; a <c>first</c> that is not a value of its format (a
    /// letter run of the level's letters, or a non-negative integer); a level without <c>startEmpty</c> below one with it; a separator that
    /// holds a letter or a digit; and an empty separator between two levels that are not one letters level and one numbers level. Keys
    /// that don't apply to a level (<c>skip</c> or <c>width</c> on another format, any of them on a list) are not read.
    /// </summary>
    /// <param name="levels">The levels, from the most significant.</param>
    /// <param name="separator">The separator; <see langword="null"/> is <see cref="EntityRevisions.DefaultSeparator"/>.</param>
    /// <param name="sequence">The sequence, when it can be built.</param>
    /// <param name="problem">Why it can't, in a sentence, when it can't.</param>
    /// <returns>Whether the sequence was built.</returns>
    public static bool TryCreate(IReadOnlyList<RevisionLevel> levels, string? separator, [NotNullWhen(true)] out RevisionSequence? sequence,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var written = separator ?? EntityRevisions.DefaultSeparator;
        var built = Build(levels, written, out problem);
        sequence = built is null ? null : new RevisionSequence(levels, written, built);
        return sequence is not null;
    }

    /// <summary>A level's first value on its own (decision 3): <c>first</c>, else the first letter not skipped, 1 padded to the width, or a list's first value.</summary>
    /// <param name="level">The level, from 1.</param>
    /// <returns>The value.</returns>
    public string FirstValue(int level) => At(level).First;

    /// <summary>Whether a text is a value of the sequence (section 6, rules 2, 4 and 5).</summary>
    /// <param name="text">The text, read exactly: nothing is trimmed and case counts.</param>
    /// <returns><see langword="true"/> for a value.</returns>
    public bool IsValue(string text) => TryRead(text, out _);

    /// <summary>
    /// Reads a revision into its filled levels (section 6, rule 4): split on the separator, or with an empty separator where a digit meets
    /// anything else; a sequence of one level is not split. A text is a value when it has no more parts than levels, at least as many as the levels that are never empty (those
    /// above the first level that starts empty), and each part is a value of its level. <c>""</c> has no parts.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="parts">The values of the filled levels, from level 1, when the text is a value.</param>
    /// <returns>Whether the text is a value.</returns>
    public bool TryRead(string text, [NotNullWhen(true)] out IReadOnlyList<string>? parts)
    {
        ArgumentNullException.ThrowIfNull(text);
        parts = null;
        var split = Split(text);
        if (split.Count > _levels.Length || split.Count < _neverEmpty)
            return false;
        for (var i = 0; i < split.Count; i++)
        {
            if (!_levels[i].Holds(split[i]))
                return false;
        }

        parts = split;
        return true;
    }

    /// <summary>Writes filled levels (section 6, rule 3): their values joined by the separator; no parts is <c>""</c>.</summary>
    /// <param name="parts">The values of the filled levels, from level 1.</param>
    /// <returns>The revision.</returns>
    /// <exception cref="ArgumentException">There are more parts than levels.</exception>
    public string Write(IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count > _levels.Length)
            throw new ArgumentException($"{parts.Count} parts for a sequence of {_levels.Length} levels.", nameof(parts));
        return string.Join(Separator, parts);
    }

    /// <summary>
    /// Compares two revisions (decision 7; section 6, rules 6 and 7): level by level from level 1, an empty level before any value; within a
    /// level, letters by length then alphabetically, numbers by value, a list by position.
    /// </summary>
    /// <param name="a">A value of the sequence.</param>
    /// <param name="b">A value of the sequence.</param>
    /// <returns>-1 when <paramref name="a"/> comes first, 0 when they are equal, 1 when <paramref name="b"/> comes first.</returns>
    /// <exception cref="ArgumentException">Either text is not a value of the sequence.</exception>
    public int Compare(string a, string b)
    {
        var left = Read(a, nameof(a));
        var right = Read(b, nameof(b));
        for (var i = 0; i < _levels.Length; i++)
        {
            var hasLeft = i < left.Count;
            var hasRight = i < right.Count;
            if (!hasLeft || !hasRight)
                return hasLeft == hasRight ? 0 : hasLeft ? 1 : -1;
            var order = _levels[i].Compare(left[i], right[i]);
            if (order != 0)
                return order;
        }

        return 0;
    }

    /// <summary>
    /// The revision after moving at a level (decision 5): levels above it keep their values, an empty one taking its first value; the level
    /// takes its next value, or its first when it was empty; levels below go back to their start, empty when they start empty, else their
    /// first value. A list at its last value can't move (decision 22).
    /// </summary>
    /// <param name="from">A value of the sequence.</param>
    /// <param name="level">The level that moves, from 1.</param>
    /// <returns>The next revision, or <see langword="null"/> when the level is a list at its last value (the refusal <c>revision</c>).</returns>
    /// <exception cref="ArgumentException"><paramref name="from"/> is not a value of the sequence.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The sequence has no such level.</exception>
    public string? Next(string from, int level)
    {
        var moving = At(level);
        var parts = Read(from, nameof(from));
        var next = new List<string>(_levels.Length);
        for (var i = 0; i < level - 1; i++)
            next.Add(i < parts.Count ? parts[i] : _levels[i].First);
        if (level - 1 < parts.Count)
        {
            if (moving.Next(parts[level - 1]) is not { } value)
                return null;
            next.Add(value);
        }
        else
        {
            next.Add(moving.First);
        }

        for (var i = level; i < _levels.Length && !_levels[i].StartEmpty; i++)
            next.Add(_levels[i].First);
        return Write(next);
    }

    /// <summary>
    /// Whether a chosen name may be given (decision 14): a value the sequence can form, later than the item's latest revision. A name the
    /// sequence can't form is <see cref="RevisionNameCheck.NotAValue"/> whether or not it would be later.
    /// </summary>
    /// <param name="latest">The item's latest revision, a value of the sequence.</param>
    /// <param name="name">The chosen name.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentException"><paramref name="latest"/> is not a value of the sequence.</exception>
    public RevisionNameCheck CheckChosen(string latest, string name)
    {
        Read(latest, nameof(latest));
        if (!IsValue(name))
            return RevisionNameCheck.NotAValue;
        return Compare(name, latest) > 0 ? RevisionNameCheck.Ok : RevisionNameCheck.NotLater;
    }

    /// <summary>A level's first five values on their own, in order (all of a shorter list), for docs and the editor.</summary>
    /// <param name="level">The level, from 1.</param>
    /// <returns>The values.</returns>
    public IReadOnlyList<string> Examples(int level)
    {
        var at = At(level);
        var examples = new List<string>(ExampleCount);
        for (var value = (string?)at.First; value is not null && examples.Count < ExampleCount; value = at.Next(value))
            examples.Add(value);
        return examples;
    }

    private Level At(int level) =>
        level >= 1 && level <= _levels.Length
            ? _levels[level - 1]
            : throw new ArgumentOutOfRangeException(nameof(level), level, $"The sequence has levels 1 to {_levels.Length}.");

    private IReadOnlyList<string> Read(string text, string parameter)
    {
        ArgumentNullException.ThrowIfNull(text, parameter);
        return TryRead(text, out var parts) ? parts : throw new ArgumentException($"'{text}' is not a value of the sequence.", parameter);
    }

    private List<string> Split(string text)
    {
        if (text.Length == 0)
            return [];

        // A separator only stands between two levels, so a sequence of one level reads the whole text as its value: a list value such
        // as "V1" with an empty separator reads back whole (section 6, rules 3 and 4).
        if (_levels.Length == 1)
            return [text];
        if (Separator.Length > 0)
            return [.. text.Split(Separator)];
        var parts = new List<string>();
        var start = 0;
        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]) != char.IsAsciiDigit(text[i - 1]))
            {
                parts.Add(text[start..i]);
                start = i;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static Level[] BuildOrThrow(IReadOnlyList<RevisionLevel> levels, string separator)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return Build(levels, separator, out var problem) ?? throw new ArgumentException(problem, nameof(levels));
    }

    private static Level[]? Build(IReadOnlyList<RevisionLevel> levels, string separator, out string? problem)
    {
        problem = null;
        if (levels.Count == 0)
        {
            problem = "A revision sequence has at least one level.";
            return null;
        }

        if (separator.Any(char.IsAsciiLetterOrDigit))
        {
            problem = $"The separator '{separator}' holds a letter or a digit.";
            return null;
        }

        var built = new Level[levels.Count];
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i] ?? throw new ArgumentException($"Level {i + 1} is null.", nameof(levels));
            built[i] = Level.Build(level, i + 1, separator, out problem)!;
            if (problem is not null)
                return null;
            if (i > 0 && built[i - 1].StartEmpty && !built[i].StartEmpty)
            {
                problem = $"Level {i + 1} does not start empty, below level {i}, which does.";
                return null;
            }

            if (i > 0 && separator.Length == 0 && (built[i - 1].Kind, built[i].Kind) is not ((Kind.Letters, Kind.Numbers) or (Kind.Numbers, Kind.Letters)))
            {
                problem = $"Levels {i} and {i + 1} are not one letters level and one numbers level, so they need a separator.";
                return null;
            }
        }

        return built;
    }

    private enum Kind
    {
        Letters,
        Numbers,
        List,
    }

    /// <summary>One level, ready to read, compare and move.</summary>
    private sealed class Level
    {
        private const string AllLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        private readonly string _alphabet = "";
        private readonly int[] _position = [];
        private readonly string _firstDigits = "";
        private readonly int? _width;
        private readonly IReadOnlyList<string> _values = [];
        private readonly Dictionary<string, int> _listPosition = new(StringComparer.Ordinal);

        private Level(Kind kind, bool startEmpty, string alphabet, string first, int? width, IReadOnlyList<string> values)
        {
            Kind = kind;
            StartEmpty = startEmpty;
            switch (kind)
            {
                case Kind.Letters:
                    _alphabet = alphabet;
                    _position = new int[AllLetters.Length];
                    Array.Fill(_position, -1);
                    for (var i = 0; i < alphabet.Length; i++)
                        _position[alphabet[i] - 'A'] = i;
                    First = first;
                    break;
                case Kind.Numbers:
                    _width = width;
                    _firstDigits = Strip(first);
                    First = Pad(_firstDigits);
                    break;
                default:
                    _values = values;
                    for (var i = 0; i < values.Count; i++)
                        _listPosition[values[i]] = i;
                    First = values[0];
                    break;
            }
        }

        public Kind Kind { get; }

        public bool StartEmpty { get; }

        public string First { get; } = "";

        public static Level? Build(RevisionLevel level, int number, string separator, out string? problem)
        {
            problem = null;
            var startEmpty = level.StartEmpty == true;
            if (level.Format is not null && level.Values is not null)
            {
                problem = $"Level {number} has both a format and values.";
                return null;
            }

            if (level.Values is { } values)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in values)
                {
                    if (string.IsNullOrEmpty(value) || !seen.Add(value) || (separator.Length > 0 && value.Contains(separator, StringComparison.Ordinal)))
                    {
                        problem = $"Level {number} has a list value that is empty, repeated or holds the separator.";
                        return null;
                    }
                }

                if (values.Count == 0)
                {
                    problem = $"Level {number} has an empty list.";
                    return null;
                }

                return new Level(Kind.List, startEmpty, "", "", null, values);
            }

            switch (level.Format)
            {
                case RevisionFormat.Letters:
                {
                    var skip = level.Skip ?? [];
                    var alphabet = string.Concat(AllLetters.Where(c => !skip.Any(s => s.Length == 1 && s[0] == c)));
                    if (alphabet.Length == 0)
                    {
                        problem = $"Level {number} skips every letter.";
                        return null;
                    }

                    var first = level.First is null ? alphabet[..1] : level.First is { ValueKind: JsonValueKind.String } ? level.FirstText! : null;
                    if (first is null || first.Length == 0 || first.Any(c => !alphabet.Contains(c, StringComparison.Ordinal)))
                    {
                        problem = $"Level {number}'s first is not a run of its letters.";
                        return null;
                    }

                    return new Level(Kind.Letters, startEmpty, alphabet, first, null, []);
                }

                case RevisionFormat.Numbers:
                {
                    var first = level.First is null ? "1" : level.First is { ValueKind: JsonValueKind.Number } ? level.FirstText! : null;
                    if (first is null || first.Length == 0 || !first.All(char.IsAsciiDigit))
                    {
                        problem = $"Level {number}'s first is not a whole number of zero or more.";
                        return null;
                    }

                    return new Level(Kind.Numbers, startEmpty, "", first, level.Width is > 0 ? level.Width : null, []);
                }

                default:
                    problem = $"Level {number} has neither a format nor values.";
                    return null;
            }
        }

        /// <summary>Whether the text is a value of the level (section 6, rule 5).</summary>
        public bool Holds(string text)
        {
            switch (Kind)
            {
                case Kind.Letters:
                    if (text.Length == 0)
                        return false;
                    foreach (var c in text)
                    {
                        if (c is < 'A' or > 'Z' || _position[c - 'A'] < 0)
                            return false;
                    }

                    return CompareLetters(text, First) >= 0;
                case Kind.Numbers:
                    if (text.Length == 0 || !text.All(char.IsAsciiDigit))
                        return false;
                    var padded = _width is { } width ? text.Length >= width && (text.Length == width || text[0] != '0') : text.Length == 1 || text[0] != '0';
                    return padded && CompareDigits(Strip(text), _firstDigits) >= 0;
                default:
                    return _listPosition.ContainsKey(text);
            }
        }

        /// <summary>The order of two values of the level (section 6, rule 6), as -1, 0 or 1.</summary>
        public int Compare(string a, string b) => Kind switch
        {
            Kind.Letters => CompareLetters(a, b),
            Kind.Numbers => CompareDigits(Strip(a), Strip(b)),
            _ => Math.Sign(_listPosition[a] - _listPosition[b]),
        };

        /// <summary>The value after one of the level's values, or <see langword="null"/> at a list's end.</summary>
        public string? Next(string value)
        {
            switch (Kind)
            {
                case Kind.Letters:
                {
                    var letters = value.ToCharArray();
                    for (var i = letters.Length - 1; i >= 0; i--)
                    {
                        var at = _position[letters[i] - 'A'];
                        if (at + 1 < _alphabet.Length)
                        {
                            letters[i] = _alphabet[at + 1];
                            return new string(letters);
                        }

                        letters[i] = _alphabet[0];
                    }

                    return _alphabet[0] + new string(letters);
                }

                case Kind.Numbers:
                {
                    var digits = Strip(value).ToCharArray();
                    for (var i = digits.Length - 1; i >= 0; i--)
                    {
                        if (digits[i] != '9')
                        {
                            digits[i]++;
                            return Pad(new string(digits));
                        }

                        digits[i] = '0';
                    }

                    return Pad("1" + new string(digits));
                }

                default:
                {
                    var at = _listPosition[value];
                    return at + 1 < _values.Count ? _values[at + 1] : null;
                }
            }
        }

        private string Pad(string digits) => _width is { } width && digits.Length < width ? digits.PadLeft(width, '0') : digits;

        private static int CompareLetters(string a, string b) =>
            a.Length != b.Length ? Math.Sign(a.Length - b.Length) : Math.Sign(string.CompareOrdinal(a, b));

        private static int CompareDigits(string a, string b) =>
            a.Length != b.Length ? Math.Sign(a.Length - b.Length) : Math.Sign(string.CompareOrdinal(a, b));

        private static string Strip(string digits)
        {
            var start = 0;
            while (start < digits.Length - 1 && digits[start] == '0')
                start++;
            return digits[start..];
        }
    }
}
