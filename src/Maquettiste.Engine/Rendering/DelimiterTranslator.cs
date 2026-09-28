using System.Text;

namespace Maquettiste.Engine.Rendering;

/// <summary>A template translated to <c>{{ }}</c> form, with the map from translated positions back to the source.</summary>
/// <param name="Text">The translated text Scriban parses.</param>
/// <param name="Map">Maps positions in <paramref name="Text"/> to positions in the source.</param>
internal sealed record TranslatedTemplate(string Text, PositionMap Map);

/// <summary>
/// Rewrites a template that uses custom delimiters (a unit's <c>delimiters</c> option, D11) into Scriban's <c>{{ }}</c> form before
/// parsing, because Scriban 7.5 has no delimiter option (engine-design.md section 9).
/// <list type="bullet">
/// <item>A code span <c>Open … Close</c> becomes <c>{{ … }}</c>; the code is copied verbatim, so whitespace control (<c>Open-</c>,
/// <c>~Close</c>) keeps working. <c>Close</c> inside a string literal or a comment of the code does not end the span.</item>
/// <item>A text span that Scriban would read as code (it contains <c>{{</c>, <c>}}</c> or an escape opener <c>{%{</c>, or ends
/// with <c>{</c>) is wrapped in an escape block <c>{%{…}%}</c> with enough <c>%</c> that the text cannot close it; its leading and
/// trailing whitespace stays outside the block, so whitespace control on the neighbouring code spans still trims it.</item>
/// <item>A Scriban escape block already in the source (<c>{%{ … }%}</c>, any number of <c>%</c>) is kept as it is: its content is
/// literal and is not scanned for <c>Open</c>, so a raw block passes both literal braces and literal custom delimiters through.</item>
/// </list>
/// Line breaks are copied one for one, so a translated line is the source line; columns are mapped back through
/// <see cref="PositionMap"/>.
/// </summary>
internal static class DelimiterTranslator
{
    /// <summary>Whether a unit's delimiters require translation (anything but <c>{{</c> and <c>}}</c>).</summary>
    /// <param name="open">The opening delimiter.</param>
    /// <param name="close">The closing delimiter.</param>
    /// <returns>Whether to translate.</returns>
    public static bool NeedsTranslation(string open, string close) =>
        !(string.Equals(open, "{{", StringComparison.Ordinal) && string.Equals(close, "}}", StringComparison.Ordinal));

    /// <summary>Translates a template.</summary>
    /// <param name="source">The template text (LF line endings).</param>
    /// <param name="open">The opening delimiter, non-empty.</param>
    /// <param name="close">The closing delimiter, non-empty.</param>
    /// <returns>The translated template.</returns>
    /// <exception cref="DelimiterException">A code span or an escape block is not closed, or a delimiter is empty.</exception>
    public static TranslatedTemplate Translate(string source, string open, string close)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(open) || string.IsNullOrEmpty(close))
            throw new DelimiterException(0, "Custom delimiters must not be empty.");
        if (open.Contains('\n', StringComparison.Ordinal) || close.Contains('\n', StringComparison.Ordinal))
            throw new DelimiterException(0, "Custom delimiters must not contain a line break.");

        var output = new StringBuilder(source.Length + 16);
        var segments = new List<Segment>();
        var textStart = 0;
        var i = 0;
        while (i < source.Length)
        {
            if (source[i] == '{' && EscapeOpenerLength(source, i) is var opener and > 0)
            {
                var percents = opener - 2;
                var closer = "}" + new string('%', percents) + "}";
                var end = source.IndexOf(closer, i + opener, StringComparison.Ordinal);
                if (end < 0)
                    throw new DelimiterException(i, "The escape block is not closed (expected '" + closer + "').");
                FlushText(source, textStart, i, output, segments);
                Copy(source, i, end + closer.Length, output, segments);
                i = end + closer.Length;
                textStart = i;
                continue;
            }

            if (string.CompareOrdinal(source, i, open, 0, open.Length) == 0)
            {
                var codeStart = i + open.Length;
                var codeEnd = FindClose(source, codeStart, close);
                if (codeEnd < 0)
                    throw new DelimiterException(i, "The code span opened with '" + open + "' is not closed with '" + close + "'.");
                FlushText(source, textStart, i, output, segments);
                segments.Add(new Segment(output.Length, i, 0));
                output.Append("{{");
                Copy(source, codeStart, codeEnd, output, segments);
                segments.Add(new Segment(output.Length, codeEnd, 0));
                output.Append("}}");
                i = codeEnd + close.Length;
                textStart = i;
                continue;
            }

            i++;
        }

        FlushText(source, textStart, source.Length, output, segments);
        return new TranslatedTemplate(output.ToString(), new PositionMap(source, output.ToString(), segments));
    }

    /// <summary>Length of a Scriban escape opener (<c>{</c>, one or more <c>%</c>, <c>{</c>) at a position, or 0.</summary>
    private static int EscapeOpenerLength(string text, int index)
    {
        var j = index + 1;
        while (j < text.Length && text[j] == '%')
            j++;
        return j > index + 1 && j < text.Length && text[j] == '{' ? j + 1 - index : 0;
    }

    /// <summary>Finds <paramref name="close"/> after a code start, skipping string literals and comments.</summary>
    private static int FindClose(string source, int from, string close)
    {
        var i = from;
        while (i < source.Length)
        {
            if (string.CompareOrdinal(source, i, close, 0, close.Length) == 0)
                return i;
            var c = source[i];
            if (c is '"' or '\'')
            {
                var end = SkipQuoted(source, i, c);
                if (end < 0)
                    return NaiveClose(source, i, close);
                i = end;
                continue;
            }

            if (c == '`')
            {
                var end = source.IndexOf('`', i + 1);
                if (end < 0)
                    return NaiveClose(source, i, close);
                i = end + 1;
                continue;
            }

            if (c == '#')
            {
                if (i + 1 < source.Length && source[i + 1] == '#')
                {
                    // Multi-line comment: up to the next "##" (Close inside it still ends the span, as "}}" would).
                    var j = i + 2;
                    while (j < source.Length && string.CompareOrdinal(source, j, "##", 0, 2) != 0
                           && string.CompareOrdinal(source, j, close, 0, close.Length) != 0)
                        j++;
                    if (j < source.Length && string.CompareOrdinal(source, j, close, 0, close.Length) == 0)
                        return j;
                    i = Math.Min(source.Length, j + 2);
                    continue;
                }

                // Single-line comment: quotes inside it are not strings; it ends at the line end or at Close.
                var k = i + 1;
                while (k < source.Length && source[k] != '\n')
                {
                    if (string.CompareOrdinal(source, k, close, 0, close.Length) == 0)
                        return k;
                    k++;
                }

                i = k;
                continue;
            }

            i++;
        }

        return -1;
    }

    private static int NaiveClose(string source, int from, string close) => source.IndexOf(close, from, StringComparison.Ordinal);

    /// <summary>Index after the closing quote of a string literal starting at <paramref name="start"/>, or -1.</summary>
    private static int SkipQuoted(string source, int start, char quote)
    {
        for (var i = start + 1; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == quote)
                return i + 1;
            if (c == '\n')
                return -1;
        }

        return -1;
    }

    private static void FlushText(string source, int start, int end, StringBuilder output, List<Segment> segments)
    {
        if (end <= start)
            return;
        var text = source.AsSpan(start, end - start);
        if (!NeedsEscape(text))
        {
            Copy(source, start, end, output, segments);
            return;
        }

        // Leading and trailing whitespace stays outside the escape block (it can hold no brace), so whitespace control on the
        // neighbouring code spans ("<%-", "-%>") still trims it. Leading and trailing '-' and '~' stay outside too: right after
        // "{%{" or right before "}%}" Scriban reads them as whitespace control of the escape block itself.
        var coreStart = start;
        while (coreStart < end && IsEdge(source[coreStart]))
            coreStart++;
        var coreEnd = end;
        while (coreEnd > coreStart && IsEdge(source[coreEnd - 1]))
            coreEnd--;
        Copy(source, start, coreStart, output, segments);
        var core = source.AsSpan(coreStart, coreEnd - coreStart);
        var percents = 1;
        while (!CloserFitsAtEnd(core, percents))
            percents++;
        var fence = new string('%', percents);
        segments.Add(new Segment(output.Length, coreStart, 0));
        output.Append('{').Append(fence).Append('{');
        Copy(source, coreStart, coreEnd, output, segments);
        segments.Add(new Segment(output.Length, coreEnd, 0));
        output.Append('}').Append(fence).Append('}');
        Copy(source, coreEnd, end, output, segments);
    }

    private static bool IsEdge(char c) => char.IsWhiteSpace(c) || c is '-' or '~';

    /// <summary>Whether text + closer contains the closer only at its end, so the escape block ends where it should.</summary>
    private static bool CloserFitsAtEnd(ReadOnlySpan<char> text, int percents)
    {
        var closer = "}" + new string('%', percents) + "}";
        var combined = string.Concat(text, closer);
        return combined.IndexOf(closer, StringComparison.Ordinal) == text.Length;
    }

    private static bool NeedsEscape(ReadOnlySpan<char> text)
    {
        if (text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal) || text[^1] == '{')
            return true;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '{' || text[i + 1] != '%')
                continue;
            var j = i + 1;
            while (j < text.Length && text[j] == '%')
                j++;
            if (j < text.Length && text[j] == '{')
                return true;
        }

        return false;
    }

    private static void Copy(string source, int start, int end, StringBuilder output, List<Segment> segments)
    {
        if (end <= start)
            return;
        segments.Add(new Segment(output.Length, start, end - start));
        output.Append(source, start, end - start);
    }
}

/// <summary>A copied span: translated offset, source offset, length (0 for an inserted marker).</summary>
/// <param name="Translated">The offset in the translated text.</param>
/// <param name="Source">The offset in the source text.</param>
/// <param name="Length">The span length.</param>
internal readonly record struct Segment(int Translated, int Source, int Length);

/// <summary>Maps 0-based positions in a translated template back to 1-based source positions.</summary>
internal sealed class PositionMap
{
    private readonly Segment[] _segments;
    private readonly int[] _sourceLines;
    private readonly int[] _translatedLines;

    /// <summary>Creates a map.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="translated">The translated text.</param>
    /// <param name="segments">Segments in translated order.</param>
    public PositionMap(string source, string translated, IReadOnlyList<Segment> segments)
    {
        _segments = [.. segments];
        _sourceLines = LineStarts(source);
        _translatedLines = LineStarts(translated);
    }

    /// <summary>Maps a 0-based translated (line, column) to a 1-based source (line, column).</summary>
    /// <param name="line">The 0-based line in the translated text.</param>
    /// <param name="column">The 0-based column in the translated text.</param>
    /// <returns>The 1-based source line and column.</returns>
    public (int Line, int Column) Map(int line, int column)
    {
        if (line < 0 || line >= _translatedLines.Length)
            return (Math.Max(line, 0) + 1, Math.Max(column, 0) + 1);
        var offset = _translatedLines[line] + Math.Max(column, 0);
        var source = SourceOffset(offset);
        var sourceLine = Array.BinarySearch(_sourceLines, source);
        if (sourceLine < 0)
            sourceLine = ~sourceLine - 1;
        sourceLine = Math.Max(sourceLine, 0);
        return (sourceLine + 1, source - _sourceLines[sourceLine] + 1);
    }

    private int SourceOffset(int translated)
    {
        var lo = 0;
        var hi = _segments.Length - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_segments[mid].Translated <= translated)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
            return 0;
        var segment = _segments[found];
        return translated < segment.Translated + segment.Length ? segment.Source + (translated - segment.Translated) : segment.Source + segment.Length;
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }

        return [.. starts];
    }
}

/// <summary>A malformed custom-delimiter template.</summary>
/// <param name="offset">The source offset of the problem.</param>
/// <param name="message">The message.</param>
internal sealed class DelimiterException(int offset, string message) : Exception(message)
{
    /// <summary>The source offset of the problem.</summary>
    public int Offset { get; } = offset;
}
