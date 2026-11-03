using System.Globalization;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// The portable subset of regular expressions <c>matches</c> takes (next-phase-3-design.md section 3.1): literal characters,
/// <c>.</c>, bracket classes with ranges and negation, the anchors <c>^</c> and <c>$</c>, the quantifiers <c>*</c>, <c>+</c>,
/// <c>?</c> and <c>{n}</c>, <c>{n,}</c>, <c>{n,m}</c> (bounds up to 255), each greedy or lazy, capturing and non-capturing groups,
/// and alternation. It refuses back-references, lookaround, POSIX classes, the shorthands <c>\d</c>, <c>\w</c>, <c>\s</c> (and
/// their negations and <c>\b</c>), whose meaning depends on the locale in PostgreSQL, and any escape beyond the syntax characters
/// and <c>\n</c>, <c>\r</c>, <c>\t</c>, since JavaScript's <c>u</c> flag refuses identity escapes, and a <c>-</c> right after a range
/// (<c>[a-z-9]</c>), which PostgreSQL refuses. A pattern outside it is MQ3105.
/// </summary>
public static class PortableRegex
{
    private const string SyntaxCharacters = "^$\\.*+?()[]{}|/";
    private const int MaxBound = 255;

    /// <summary>Checks a pattern.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="problem">Why the pattern is outside the subset, with the position, when it is.</param>
    /// <returns><see langword="true"/> when the pattern is in the subset.</returns>
    public static bool TryValidate(string pattern, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var parser = new Parser(pattern);
        problem = parser.Run();
        return problem is null;
    }

    private sealed class Parser(string pattern)
    {
        private int _at;
        private int _depth;

        public string? Run()
        {
            try
            {
                Alternation();
                if (_at < pattern.Length)
                    Fail(pattern[_at] == ')' ? "an unmatched ')'" : "an unexpected '" + pattern[_at] + "'");
                return null;
            }
            catch (RegexSubsetException e)
            {
                return e.Message;
            }
        }

        private void Alternation()
        {
            Sequence();
            while (Peek('|'))
            {
                _at++;
                Sequence();
            }
        }

        private void Sequence()
        {
            while (_at < pattern.Length && pattern[_at] is not ('|' or ')'))
            {
                var quantifiable = Atom();
                if (_at < pattern.Length && pattern[_at] is '*' or '+' or '?' or '{')
                {
                    if (!quantifiable)
                        Fail("a quantifier with nothing to repeat");
                    Quantifier();
                    if (_at < pattern.Length && pattern[_at] is '*' or '+' or '?' or '{')
                        Fail("a quantifier on a quantifier");
                }
            }
        }

        private bool Atom()
        {
            var c = pattern[_at];
            switch (c)
            {
                case '^' or '$':
                    _at++;
                    return false;
                case '.':
                    _at++;
                    return true;
                case '(':
                    Group();
                    return true;
                case '[':
                    Class();
                    return true;
                case '\\':
                    Escape(inClass: false);
                    return true;
                case '*' or '+' or '?' or '{':
                    Fail("a quantifier with nothing to repeat");
                    return false;
                case ']' or '}':
                    Fail("an unescaped '" + c + "'; write \\" + c);
                    return false;
                default:
                    _at += char.IsSurrogatePair(pattern, _at) ? 2 : 1;
                    return true;
            }
        }

        private void Group()
        {
            var start = _at;
            _at++;
            if (Peek('?'))
            {
                if (_at + 1 < pattern.Length && pattern[_at + 1] == ':')
                {
                    _at += 2;
                }
                else
                {
                    var next = _at + 1 < pattern.Length ? pattern[_at + 1] : '\0';
                    var after = _at + 2 < pattern.Length ? pattern[_at + 2] : '\0';
                    Fail(next is '=' or '!' || (next == '<' && after is '=' or '!') ? "lookaround"
                        : next == '<' ? "a named group" : "a group with '(?'; only '(?:' is portable");
                }
            }

            if (++_depth > 32)
                Fail("groups nested deeper than 32");
            Alternation();
            _depth--;
            if (!Peek(')'))
                Fail("an unclosed group", start);
            _at++;
        }

        private void Class()
        {
            var start = _at;
            _at++;
            if (Peek('^'))
                _at++;
            if (Peek('[') && _at + 1 < pattern.Length && pattern[_at + 1] is ':' or '=' or '.')
                Fail("a POSIX class");
            var first = true;
            var afterRange = false;
            int? previous = null;
            while (true)
            {
                if (_at >= pattern.Length)
                    Fail("an unclosed bracket class", start);
                var c = pattern[_at];
                if (c == ']')
                {
                    if (first)
                        Fail("an empty bracket class; write \\] for a bracket");
                    _at++;
                    return;
                }

                if (c == '[')
                {
                    if (_at + 1 < pattern.Length && pattern[_at + 1] is ':' or '=' or '.')
                        Fail("a POSIX class");
                    Fail("an unescaped '[' in a bracket class; write \\[");
                }

                int code;
                if (c == '\\')
                {
                    code = Escape(inClass: true);
                }
                else
                {
                    code = char.ConvertToUtf32(pattern, _at);
                    _at += char.IsSurrogatePair(pattern, _at) ? 2 : 1;
                }

                // JavaScript reads [a-z-9] as a range and a '-'; PostgreSQL refuses it ("invalid character range"). Before ']' it is a '-' in both.
                if (c == '-' && afterRange && _at < pattern.Length && pattern[_at] != ']')
                    Fail("a '-' right after a range; write \\- for a hyphen, or put it last", _at - 1);
                afterRange = false;
                if (c == '-' && previous is { } low && _at < pattern.Length && pattern[_at] != ']')
                {
                    // A range: the '-' just read joins the previous character and the next one.
                    int high;
                    if (pattern[_at] == '\\')
                    {
                        high = Escape(inClass: true);
                    }
                    else
                    {
                        if (pattern[_at] == '[')
                            Fail("an unescaped '[' in a bracket class; write \\[");
                        high = char.ConvertToUtf32(pattern, _at);
                        _at += char.IsSurrogatePair(pattern, _at) ? 2 : 1;
                    }

                    if (high < low)
                        Fail("a range whose end comes before its start");
                    previous = null;
                    afterRange = true;
                }
                else
                {
                    previous = code;
                }

                first = false;
            }
        }

        private int Escape(bool inClass)
        {
            var start = _at;
            _at++;
            if (_at >= pattern.Length)
                Fail("a pattern that ends with '\\'", start);
            var c = pattern[_at];
            _at++;
            switch (c)
            {
                case 'd' or 'D' or 'w' or 'W' or 's' or 'S':
                    Fail("the shorthand \\" + c + ", whose meaning depends on the locale; write a bracket class such as [0-9]", start);
                    break;
                case 'b' or 'B':
                    Fail("the word boundary \\" + c, start);
                    break;
                case >= '0' and <= '9':
                    Fail("a back-reference", start);
                    break;
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case '-' when inClass:
                    return '-';
            }

            if (SyntaxCharacters.Contains(c, StringComparison.Ordinal))
                return c;
            Fail("the escape \\" + c, start);
            return 0;
        }

        private void Quantifier()
        {
            var start = _at;
            if (pattern[_at] == '{')
            {
                _at++;
                var low = Number();
                if (low is null)
                    Fail("a '{' that starts no bound; write \\{", start);
                int? high = low;
                if (Peek(','))
                {
                    _at++;
                    high = Number();
                    if (high is null)
                        high = int.MaxValue;
                }

                if (!Peek('}'))
                    Fail("an unclosed bound", start);
                _at++;
                if (low > MaxBound || (high != int.MaxValue && high > MaxBound))
                    Fail("a bound above " + MaxBound.ToString(CultureInfo.InvariantCulture), start);
                if (high < low)
                    Fail("a bound whose maximum is below its minimum", start);
            }
            else
            {
                _at++;
            }

            if (Peek('?'))
                _at++;
        }

        private int? Number()
        {
            var start = _at;
            while (_at < pattern.Length && pattern[_at] is >= '0' and <= '9')
                _at++;
            if (_at == start)
                return null;
            return _at - start > 4 ? int.MaxValue - 1 : int.Parse(pattern.AsSpan(start, _at - start), CultureInfo.InvariantCulture);
        }

        private bool Peek(char c) => _at < pattern.Length && pattern[_at] == c;

        private void Fail(string what, int? at = null) =>
            throw new RegexSubsetException(
                "The pattern holds " + what + " at position " + ((at ?? _at) + 1).ToString(CultureInfo.InvariantCulture) + ", outside the portable subset.");
    }

    private sealed class RegexSubsetException(string message) : Exception(message);
}
