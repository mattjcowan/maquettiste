using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Rendering;

/// <summary>
/// The light reading of a query's SQL text (sql-queries-design.md decisions 4 and 6): never a parse. Per dialect, it skips comments,
/// string literals and quoted identifiers, and reports four things only: the <c>:name</c> placeholders with their offsets, the first
/// word and the statement's verb, the names that appear, and whether <c>ORDER BY</c> and <c>OFFSET</c>, <c>FETCH</c> or <c>TOP</c>
/// appear outside parentheses. Ordinal and culture-invariant. The editor keeps a TypeScript copy; both read the shared vectors of
/// <c>tests/fixtures/sql-text/scanner.json</c>, whose <c>format</c> entry states the rules below in words.
/// </summary>
internal static class SqlText
{
    /// <summary>The words that make a statement after a leading <c>WITH</c> (decision 6).</summary>
    private static readonly string[] StatementVerbs = ["SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "VALUES", "TABLE"];

    /// <summary>
    /// White space, for the scanner and for trimming a text: what <see cref="char.IsWhiteSpace(char)"/> calls white space, and U+FEFF
    /// (a byte order mark). The editor's copy uses the same set.
    /// </summary>
    /// <param name="c">A UTF-16 code unit.</param>
    /// <returns>Whether it is white space.</returns>
    public static bool IsWhite(char c) => char.IsWhiteSpace(c) || c == '\uFEFF';

    /// <summary>A text without the white space (<see cref="IsWhite"/>) at both ends.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The trimmed text.</returns>
    public static string Trim(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        var end = text.Length;
        while (start < end && IsWhite(text[start]))
            start++;
        while (end > start && IsWhite(text[end - 1]))
            end--;
        return start == 0 && end == text.Length ? text : text[start..end];
    }

    /// <summary>Reads <paramref name="text"/> as SQL of <paramref name="dialect"/>.</summary>
    /// <param name="text">The SQL text as written.</param>
    /// <param name="dialect">The dialect whose comments, literals and quoted identifiers apply.</param>
    /// <returns>What the text shows.</returns>
    public static SqlTextScan Scan(string text, Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Scanner(text, dialect).Run();
    }

    /// <summary>The 1-based line and column (in UTF-16 code units) of an offset; lines end at <c>\n</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="offset">The offset, from 0.</param>
    /// <returns>The line and column.</returns>
    public static (int Line, int Column) Position(string text, int offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart + 1);
    }

    private sealed class Scanner(string text, Dialect dialect)
    {
        private readonly List<SqlPlaceholder> _placeholders = [];
        private readonly List<string> _names = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private int _i;
        private int _depth;
        private bool _started;
        private string? _firstWord;
        private int _firstDepth;
        private string? _verb;
        private bool _verbFound;
        private bool _orderPending;
        private bool _orderBy;
        private bool _bounded;
        private int _finalSemicolon = -1;

        public SqlTextScan Run()
        {
            var n = text.Length;
            while (_i < n)
            {
                var c = text[_i];
                var next = _i + 1 < n ? text[_i + 1] : '\0';
                if (IsWhite(c))
                {
                    _i++;
                    continue;
                }

                if (c == '-' && next == '-')
                {
                    SkipLine();
                    continue;
                }

                if (c == '#' && dialect == Dialect.MySql)
                {
                    SkipLine();
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    SkipBlockComment(nested: dialect is Dialect.PostgreSql or Dialect.SqlServer);
                    continue;
                }

                // A token: only a ; at parenthesis depth 0 with nothing but white space and comments after it is the final one.
                _finalSemicolon = c == ';' && _depth == 0 ? _i : -1;
                if (c == '\'')
                {
                    SkipString(_i + 1, '\'', backslash: dialect == Dialect.MySql);
                    Other();
                }
                else if (c == '"')
                {
                    if (dialect == Dialect.MySql)
                    {
                        SkipString(_i + 1, '"', backslash: true);
                        Other();
                    }
                    else
                    {
                        QuotedName('"', doubled: true);
                    }
                }
                else if (c == '`' && dialect is Dialect.MySql or Dialect.Sqlite)
                {
                    QuotedName('`', doubled: true);
                }
                else if (c == '[' && dialect is Dialect.SqlServer or Dialect.Sqlite)
                {
                    QuotedName(']', doubled: dialect == Dialect.SqlServer);
                }
                else if (c == '$' && dialect == Dialect.PostgreSql && TryDollarQuote())
                {
                    Other();
                }
                else if (c == ':')
                {
                    Colon();
                }
                else if (c == '@')
                {
                    // A variable (@total, @@ROWCOUNT) or an operator (@>, <@): never a name, never a placeholder.
                    while (_i < n && text[_i] == '@')
                        _i++;
                    while (_i < n && IsWordPart(text[_i]))
                        _i++;
                    Other();
                }
                else if (c == '(')
                {
                    _i++;
                    if (_started)
                        Other();
                    _depth++;
                }
                else if (c == ')')
                {
                    _i++;
                    _depth = Math.Max(0, _depth - 1);
                    Other();
                }
                else if (IsWordStart(c))
                {
                    Word();
                }
                else if (char.IsAsciiDigit(c))
                {
                    // A number (or a number's tail such as 1e10 or 0x1F): no name.
                    while (_i < n && (char.IsLetterOrDigit(text[_i]) || text[_i] == '_'))
                        _i++;
                    Other();
                }
                else
                {
                    _i++;
                    Other();
                }
            }

            var verb = _firstWord is null ? null : _firstWord == "WITH" ? _verb : _firstWord;
            return new SqlTextScan(_placeholders, _firstWord, verb, _names, _orderBy, _bounded, _finalSemicolon < 0 ? null : _finalSemicolon);
        }

        private bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || (c == '#' && dialect == Dialect.SqlServer);

        private bool IsWordPart(char c) =>
            char.IsLetterOrDigit(c) || c is '_' or '$' || (c == '#' && dialect is Dialect.SqlServer or Dialect.Oracle);

        private void SkipLine()
        {
            while (_i < text.Length && text[_i] != '\n')
                _i++;
        }

        private void SkipBlockComment(bool nested)
        {
            _i += 2;
            var level = 1;
            while (_i < text.Length && level > 0)
            {
                if (nested && text[_i] == '/' && _i + 1 < text.Length && text[_i + 1] == '*')
                {
                    level++;
                    _i += 2;
                }
                else if (text[_i] == '*' && _i + 1 < text.Length && text[_i + 1] == '/')
                {
                    level--;
                    _i += 2;
                }
                else
                {
                    _i++;
                }
            }
        }

        /// <summary>Skips a literal whose body starts at <paramref name="from"/>, to after its closing quote (or the end).</summary>
        private void SkipString(int from, char quote, bool backslash)
        {
            _i = from;
            while (_i < text.Length)
            {
                var c = text[_i];
                if (backslash && c == '\\')
                {
                    _i += 2;
                }
                else if (c == quote)
                {
                    if (_i + 1 < text.Length && text[_i + 1] == quote)
                    {
                        _i += 2;
                    }
                    else
                    {
                        _i++;
                        return;
                    }
                }
                else
                {
                    _i++;
                }
            }

            _i = Math.Min(_i, text.Length);
        }

        /// <summary>A quoted identifier: its text without the quotes (a doubled closing quote read as one) is a name.</summary>
        private void QuotedName(char close, bool doubled)
        {
            _i++;
            var name = new System.Text.StringBuilder();
            while (_i < text.Length)
            {
                var c = text[_i];
                if (c == close)
                {
                    if (doubled && _i + 1 < text.Length && text[_i + 1] == close)
                    {
                        name.Append(close);
                        _i += 2;
                        continue;
                    }

                    _i++;
                    break;
                }

                name.Append(c);
                _i++;
            }

            AddName(name.ToString());
            Other();
        }

        /// <summary>A PostgreSQL dollar-quoted string (<c>$$…$$</c>, <c>$tag$…$tag$</c>); false when the <c>$</c> starts none.</summary>
        private bool TryDollarQuote()
        {
            var j = _i + 1;
            if (j < text.Length && (char.IsLetter(text[j]) || text[j] == '_'))
            {
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                    j++;
            }

            if (j >= text.Length || text[j] != '$')
                return false;
            var tag = text[_i..(j + 1)];
            var end = text.IndexOf(tag, j + 1, StringComparison.Ordinal);
            _i = end < 0 ? text.Length : end + tag.Length;
            return true;
        }

        /// <summary>
        /// A <c>:</c>: a placeholder when a name <c>[A-Za-z_][A-Za-z0-9_]*</c> follows, the <c>:</c> is not part of <c>::</c>, and the
        /// character before it is not a letter, digit, <c>_</c>, <c>]</c>, <c>)</c> or a quote (decision 4).
        /// </summary>
        private void Colon()
        {
            var at = _i;
            if (at + 1 < text.Length && text[at + 1] == ':')
            {
                while (_i < text.Length && text[_i] == ':')
                    _i++;
                Other();
                return;
            }

            _i++;
            var before = at > 0 ? text[at - 1] : ' ';
            var after = at + 1 < text.Length ? text[at + 1] : '\0';
            if (char.IsLetterOrDigit(before) || before is '_' or ']' or ')' or '\'' or '"' or '`' || !(char.IsAsciiLetter(after) || after == '_'))
            {
                Other();
                return;
            }

            var end = at + 1;
            while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
                end++;
            _placeholders.Add(new SqlPlaceholder(text[(at + 1)..end], at, end - at));
            _i = end;
            Other();
        }

        private void Word()
        {
            var start = _i;
            _i++;
            while (_i < text.Length && IsWordPart(text[_i]))
                _i++;
            var word = text[start.._i];

            // A word written against a quote is the quote's prefix (N'…', E'…', X'…', _utf8'…', q'[…]'), not a name.
            if (_i < text.Length && text[_i] == '\'')
            {
                var upper = word.ToUpperInvariant();
                if (dialect == Dialect.Oracle && upper is "Q" or "NQ" && _i + 1 < text.Length)
                    SkipQQuote();
                else
                    SkipString(_i + 1, '\'', backslash: dialect == Dialect.MySql || (dialect == Dialect.PostgreSql && upper == "E"));
                Other();
                return;
            }

            AddName(word);
            var keyword = word.ToUpperInvariant();
            if (!_started)
            {
                _started = true;
                _firstWord = keyword;
                _firstDepth = _depth;
            }
            else if (_firstWord == "WITH" && !_verbFound && _depth == _firstDepth && StatementVerbs.Contains(keyword, StringComparer.Ordinal))
            {
                _verb = keyword;
                _verbFound = true;
            }

            if (_depth == 0)
            {
                if (_orderPending && keyword == "BY")
                    _orderBy = true;
                if (keyword is "OFFSET" or "FETCH" or "TOP")
                    _bounded = true;
                _orderPending = keyword == "ORDER";
            }
            else
            {
                _orderPending = false;
            }
        }

        /// <summary>An Oracle alternative-quoting literal <c>q'x…x'</c>, with <c>[]</c>, <c>{}</c>, <c>()</c> and <c>&lt;&gt;</c> pairs.</summary>
        private void SkipQQuote()
        {
            var open = text[_i + 1];
            var close = open switch
            {
                '[' => ']',
                '{' => '}',
                '(' => ')',
                '<' => '>',
                _ => open,
            };
            var j = _i + 2;
            while (j < text.Length && !(text[j] == close && j + 1 < text.Length && text[j + 1] == '\''))
                j++;
            _i = Math.Min(j + 2, text.Length);
        }

        private void AddName(string name)
        {
            if (name.Length > 0 && _seen.Add(name))
                _names.Add(name);
        }

        /// <summary>A token that is not a word: the first token when nothing came before, and the end of a pending ORDER.</summary>
        private void Other()
        {
            _started = true;
            _orderPending = false;
        }
    }
}

/// <summary>What <see cref="SqlText.Scan"/> reads from a SQL text (sql-queries-design.md decision 6), and nothing else.</summary>
/// <param name="Placeholders">The <c>:name</c> placeholders, in text order (a name may repeat).</param>
/// <param name="FirstWord">The first word outside comments, upper-cased (leading <c>(</c> skipped); <see langword="null"/> when the
/// first token is anything else.</param>
/// <param name="Verb">The statement's verb, upper-cased: the first word, or after a leading <c>WITH</c> the first of <c>SELECT</c>,
/// <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>MERGE</c>, <c>REPLACE</c>, <c>VALUES</c> or <c>TABLE</c> at the <c>WITH</c>'s
/// parenthesis depth; <see langword="null"/> when there is none.</param>
/// <param name="Names">The words and quoted identifiers (without their quotes) that appear, distinct, in order of first appearance,
/// as written: keywords included, placeholders, string prefixes and <c>@</c> words left out.</param>
/// <param name="OrderBy">Whether <c>ORDER BY</c> appears outside parentheses.</param>
/// <param name="OffsetFetchOrTop">Whether <c>OFFSET</c>, <c>FETCH</c> or <c>TOP</c> appears outside parentheses.</param>
/// <param name="FinalSemicolon">The offset of the text's final <c>;</c>: the last token outside comments, strings and quoted identifiers,
/// when it is a <c>;</c> outside parentheses (only white space and comments may follow it); <see langword="null"/> when there is none.</param>
internal sealed record SqlTextScan(
    IReadOnlyList<SqlPlaceholder> Placeholders,
    string? FirstWord,
    string? Verb,
    IReadOnlyList<string> Names,
    bool OrderBy,
    bool OffsetFetchOrTop,
    int? FinalSemicolon = null)
{
    /// <summary>Whether <paramref name="name"/> appears in the text, ignoring case (MQ4098 compares table and view names so).</summary>
    /// <param name="name">A table or view name.</param>
    /// <returns>Whether it appears.</returns>
    public bool Mentions(string name) => Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A <c>:name</c> placeholder of a SQL text.</summary>
/// <param name="Name">The parameter name, without the colon.</param>
/// <param name="Offset">The offset of the colon, in UTF-16 code units from 0.</param>
/// <param name="Length">The length with the colon.</param>
internal readonly record struct SqlPlaceholder(string Name, int Offset, int Length);
