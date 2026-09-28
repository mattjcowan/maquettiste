using System.Globalization;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The <c>engine</c> range of a <c>pack.json</c> (<c>"&gt;=1.0 &lt;2.0"</c>): alternatives separated by <c>||</c>, each a list of
/// comparators separated by spaces that must all hold. A comparator is <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c> or
/// <c>=</c> followed by a version (an operator may be separated from its version by spaces), a bare version (<c>1.2</c> means
/// <c>&gt;=1.2.0 &lt;1.3.0</c>, <c>1</c> means <c>&gt;=1.0.0 &lt;2.0.0</c>, <c>1.2.3</c> exactly), <c>^v</c>, <c>~v</c>, <c>x</c>
/// wildcards (<c>1.x</c>) or <c>*</c>. Versions have up to three numeric parts; a pre-release or build suffix is ignored.
/// </summary>
internal static class EngineRange
{
    /// <summary>Checks a version against a range.</summary>
    /// <param name="range">The range.</param>
    /// <param name="version">The engine version.</param>
    /// <param name="valid"><see langword="false"/> when the range cannot be parsed.</param>
    /// <returns>Whether the version satisfies the range.</returns>
    public static bool Satisfies(string range, string version, out bool valid)
    {
        valid = false;
        if (!TryParse(version, out var current, out _))
            return false;
        var any = false;
        foreach (var alternative in range.Split("||"))
        {
            if (!TryAlternative(alternative, current, out var matches))
                return false;
            any |= matches;
        }

        valid = true;
        return any;
    }

    private static bool TryAlternative(string alternative, (int, int, int) current, out bool matches)
    {
        matches = true;
        var tokens = alternative.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count == 0)
            return false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token is ">=" or ">" or "<=" or "<" or "=" or "^" or "~")
            {
                if (i + 1 >= tokens.Count)
                    return false;
                token += tokens[++i];
            }

            if (!TryComparator(token, current, out var ok))
                return false;
            matches &= ok;
        }

        return true;
    }

    private static bool TryComparator(string token, (int, int, int) v, out bool ok)
    {
        ok = false;
        string op;
        if (token.StartsWith(">=", StringComparison.Ordinal) || token.StartsWith("<=", StringComparison.Ordinal))
            op = token[..2];
        else if (token.Length > 0 && token[0] is '>' or '<' or '=' or '^' or '~')
            op = token[..1];
        else
            op = "";
        var text = token[op.Length..];
        if (op.Length == 0 && text is "*" or "x" or "X")
        {
            ok = true;
            return true;
        }

        if (!TryParse(text, out var bound, out var parts))
            return false;
        var cmp = Compare(v, bound);
        switch (op)
        {
            case ">=": ok = cmp >= 0; break;
            case ">": ok = cmp > 0; break;
            case "<=": ok = cmp <= 0; break;
            case "<": ok = cmp < 0; break;
            case "^":
                ok = cmp >= 0 && Compare(v, bound.Item1 > 0 || parts == 1 ? (bound.Item1 + 1, 0, 0)
                    : bound.Item2 > 0 || parts == 2 ? (0, bound.Item2 + 1, 0) : (0, 0, bound.Item3 + 1)) < 0;
                break;
            case "~":
                ok = cmp >= 0 && Compare(v, parts == 1 ? (bound.Item1 + 1, 0, 0) : (bound.Item1, bound.Item2 + 1, 0)) < 0;
                break;
            default:
                // Bare or "=": a partial version is a range over its missing parts.
                ok = parts switch
                {
                    1 => v.Item1 == bound.Item1,
                    2 => v.Item1 == bound.Item1 && v.Item2 == bound.Item2,
                    _ => cmp == 0,
                };
                break;
        }

        return true;
    }

    private static int Compare((int, int, int) a, (int, int, int) b) =>
        a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2) : a.Item3.CompareTo(b.Item3);

    private static bool TryParse(string text, out (int, int, int) version, out int parts)
    {
        version = default;
        parts = 0;
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
            text = text[..cut];
        var pieces = text.Split('.');
        if (pieces.Length is 0 or > 3)
            return false;
        var values = new int[3];
        foreach (var piece in pieces)
        {
            if (piece is "x" or "X" or "*")
                break;
            if (piece.Length == 0 || !piece.All(char.IsAsciiDigit)
                || !int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out values[parts]))
                return false;
            parts++;
        }

        if (parts == 0)
            return false;
        version = (values[0], values[1], values[2]);
        return true;
    }
}
