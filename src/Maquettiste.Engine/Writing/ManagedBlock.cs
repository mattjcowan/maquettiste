using System.Text;
using Maquettiste.Engine.Hashing;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// The managed block of a <c>block</c> unit (engine-design.md section 12.3b): the lines between
/// <c>&lt;comment&gt; maquettiste: begin &lt;pack&gt;/&lt;unit&gt;</c> and <c>&lt;comment&gt; maquettiste: end &lt;pack&gt;/&lt;unit&gt;</c> in a
/// file the team otherwise owns. Everything here works on bytes, so the rest of the file stays byte for byte as it was, whatever
/// its encoding or line ends. A delimiter line matches when, trimmed of surrounding whitespace (a <c>\r</c> included), it is one
/// run of non-whitespace characters (any comment marker), a space, then <c>maquettiste: begin &lt;pack&gt;/&lt;unit&gt;</c> (or
/// <c>end</c>); the comment of an existing block may differ from the unit's, and is rewritten when the block is.
/// </summary>
internal static class ManagedBlock
{
    /// <summary>The manifest prefix of a block in a file the engine did not create.</summary>
    public const string Prefix = "b:";

    /// <summary>The manifest prefix of a block in a file the engine created (deleted with its block when nothing else is left).</summary>
    public const string CreatedPrefix = "bc:";

    /// <summary>The default comment marker.</summary>
    public const string DefaultComment = "#";

    /// <summary>Whether a manifest hash marks a block.</summary>
    /// <param name="hash">The manifest hash.</param>
    /// <returns><see langword="true"/> for <c>b:</c> and <c>bc:</c>.</returns>
    public static bool IsBlock(string hash) =>
        hash.StartsWith(Prefix, StringComparison.Ordinal) || hash.StartsWith(CreatedPrefix, StringComparison.Ordinal);

    /// <summary>Whether a manifest hash marks a block in a file the engine created.</summary>
    /// <param name="hash">The manifest hash.</param>
    /// <returns><see langword="true"/> for <c>bc:</c>.</returns>
    public static bool IsCreated(string hash) => hash.StartsWith(CreatedPrefix, StringComparison.Ordinal);

    /// <summary>The hash part of a block manifest hash (no prefix).</summary>
    /// <param name="hash">The manifest hash.</param>
    /// <returns>The content hash of the block's lines.</returns>
    public static string BodyHash(string hash) =>
        IsCreated(hash) ? hash[CreatedPrefix.Length..] : hash.StartsWith(Prefix, StringComparison.Ordinal) ? hash[Prefix.Length..] : hash;

    /// <summary>The marker of a unit's block: <c>&lt;pack&gt;/&lt;unit id&gt;</c>, from a manifest unit name or a unit key.</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="unit">A manifest unit (<c>&lt;unitId&gt;</c>, <c>&lt;unitId&gt;:&lt;elementId&gt;</c>, optionally <c>#companion</c>).</param>
    /// <returns>The marker.</returns>
    public static string Marker(string pack, string unit)
    {
        var id = unit;
        var hash = id.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
            id = id[..hash];
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
            id = id[..colon];
        return pack + "/" + id;
    }

    /// <summary>The marker of a unit key (<c>&lt;pack&gt;/&lt;unitId&gt;</c> or <c>&lt;pack&gt;/&lt;unitId&gt;:&lt;elementId&gt;</c>).</summary>
    /// <param name="unitKey">The unit key.</param>
    /// <returns>The marker.</returns>
    public static string MarkerOfKey(string unitKey)
    {
        var slash = unitKey.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? unitKey : Marker(unitKey[..slash], unitKey[(slash + 1)..]);
    }

    /// <summary>The begin line (no line end).</summary>
    /// <param name="comment">The comment marker.</param>
    /// <param name="marker">The block marker.</param>
    /// <returns>The line.</returns>
    public static string BeginLine(string comment, string marker) => comment + " maquettiste: begin " + marker;

    /// <summary>The end line (no line end).</summary>
    /// <param name="comment">The comment marker.</param>
    /// <param name="marker">The block marker.</param>
    /// <returns>The line.</returns>
    public static string EndLine(string comment, string marker) => comment + " maquettiste: end " + marker;

    /// <summary>
    /// The block's lines as they are written: the rendered text (already LF, no BOM) with a final line end added when the text is
    /// not empty and lacks one.
    /// </summary>
    /// <param name="text">The rendered, normalized text.</param>
    /// <returns>The body.</returns>
    public static string Body(string text) => text.Length == 0 || text[^1] == '\n' ? text : text + "\n";

    /// <summary>Whether a body holds one of the block's own delimiter lines (which would end the block early).</summary>
    /// <param name="body">The body.</param>
    /// <param name="marker">The block marker.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public static bool HoldsDelimiter(string body, string marker)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var span = bytes.AsSpan();
        while (!span.IsEmpty)
        {
            var newline = span.IndexOf((byte)'\n');
            var line = newline < 0 ? span : span[..newline];
            span = newline < 0 ? [] : span[(newline + 1)..];
            if (Delimiter(line, marker) != Kind.None)
                return true;
        }

        return false;
    }

    /// <summary>Finds the unit's block in a file's bytes.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="marker">The block marker.</param>
    /// <returns>The scan.</returns>
    public static Scan Find(ReadOnlySpan<byte> bytes, string marker)
    {
        int? begin = null;
        int? bodyStart = null;
        int? bodyEnd = null;
        int? end = null;
        var offset = 0;
        var lineNumber = 0;
        while (offset < bytes.Length)
        {
            lineNumber++;
            var rest = bytes[offset..];
            var newline = rest.IndexOf((byte)'\n');
            var length = newline < 0 ? rest.Length : newline + 1;
            var line = rest[..(newline < 0 ? rest.Length : newline)];
            switch (Delimiter(line, marker))
            {
                case Kind.Begin:
                    if (begin is not null)
                        return Scan.Broken($"line {lineNumber} opens the block '{marker}' a second time");
                    begin = offset;
                    bodyStart = offset + length;
                    break;
                case Kind.End:
                    if (begin is null || end is not null)
                        return Scan.Broken($"line {lineNumber} closes the block '{marker}', which is not open");
                    bodyEnd = offset;
                    end = offset + length;
                    break;
            }

            offset += length;
        }

        if (begin is not null && end is null)
            return Scan.Broken($"the block '{marker}' is opened but never closed");
        return begin is null ? Scan.None : new Scan(true, null, begin.Value, bodyStart!.Value, bodyEnd!.Value, end!.Value);
    }

    /// <summary>The manifest hash of a file's block in the form of <paramref name="form"/>, or <see langword="null"/> when the file has no single block.</summary>
    /// <param name="form">A block manifest hash (its prefix is kept).</param>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="marker">The block marker.</param>
    /// <returns>The comparable hash.</returns>
    public static string? Comparable(string form, ReadOnlySpan<byte> bytes, string marker)
    {
        var scan = Find(bytes, marker);
        if (!scan.Found)
            return null;
        var prefix = IsCreated(form) ? CreatedPrefix : Prefix;
        return prefix + ContentHash.Of(bytes[scan.BodyStart..scan.BodyEnd]);
    }

    /// <summary>The file with the block's lines replaced (delimiters rewritten with <paramref name="comment"/>).</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="scan">A scan that found the block.</param>
    /// <param name="comment">The comment marker.</param>
    /// <param name="marker">The block marker.</param>
    /// <param name="body">The new body.</param>
    /// <returns>The new file bytes.</returns>
    public static byte[] Replace(ReadOnlySpan<byte> bytes, Scan scan, string comment, string marker, string body)
    {
        var block = Encoding.UTF8.GetBytes(Text(comment, marker, body));
        var result = new byte[scan.Begin + block.Length + (bytes.Length - scan.End)];
        bytes[..scan.Begin].CopyTo(result);
        block.CopyTo(result, scan.Begin);
        bytes[scan.End..].CopyTo(result.AsSpan(scan.Begin + block.Length));
        return result;
    }

    /// <summary>
    /// The file with the block appended: after a line end when the file lacks a final one, then after a blank line unless the file
    /// already ends with one; an empty file (or a new one) holds just the block.
    /// </summary>
    /// <param name="bytes">The file bytes (empty for a new file).</param>
    /// <param name="comment">The comment marker.</param>
    /// <param name="marker">The block marker.</param>
    /// <param name="body">The body.</param>
    /// <returns>The new file bytes.</returns>
    public static byte[] Insert(ReadOnlySpan<byte> bytes, string comment, string marker, string body)
    {
        var block = Encoding.UTF8.GetBytes(Text(comment, marker, body));
        if (bytes.IsEmpty)
            return block;
        var separator = bytes[^1] != (byte)'\n' ? "\n\n" : bytes.Length >= 2 && bytes[^2] == (byte)'\n' ? "" : "\n";
        var result = new byte[bytes.Length + separator.Length + block.Length];
        bytes.CopyTo(result);
        for (var i = 0; i < separator.Length; i++)
            result[bytes.Length + i] = (byte)'\n';
        block.CopyTo(result, bytes.Length + separator.Length);
        return result;
    }

    /// <summary>
    /// The file without the block's lines. When the block was the end of the file and a blank line came before it (the one an
    /// insert adds), that blank line goes too, so an insert followed by a remove gives the file back.
    /// </summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="scan">A scan that found the block.</param>
    /// <returns>The new file bytes.</returns>
    public static byte[] Remove(ReadOnlySpan<byte> bytes, Scan scan)
    {
        var head = bytes[..scan.Begin];
        var tail = bytes[scan.End..];
        if (tail.IsEmpty && head.Length >= 2 && head[^1] == (byte)'\n' && head[^2] == (byte)'\n')
            head = head[..^1];
        var result = new byte[head.Length + tail.Length];
        head.CopyTo(result);
        tail.CopyTo(result.AsSpan(head.Length));
        return result;
    }

    /// <summary>Whether a file holds only whitespace (or nothing).</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns><see langword="true"/> when blank.</returns>
    public static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return false;
        }

        return true;
    }

    private static string Text(string comment, string marker, string body) =>
        BeginLine(comment, marker) + "\n" + body + EndLine(comment, marker) + "\n";

    private static Kind Delimiter(ReadOnlySpan<byte> line, string marker)
    {
        var trimmed = Trim(line);
        var space = trimmed.IndexOf((byte)' ');
        if (space <= 0)
            return Kind.None;
        var comment = trimmed[..space];
        foreach (var b in comment)
        {
            if (b is (byte)'\t' or (byte)'\r')
                return Kind.None;
        }

        var rest = trimmed[(space + 1)..];
        const string Begin = "maquettiste: begin ";
        const string End = "maquettiste: end ";
        if (Matches(rest, Begin, marker))
            return Kind.Begin;
        return Matches(rest, End, marker) ? Kind.End : Kind.None;
    }

    private static bool Matches(ReadOnlySpan<byte> rest, string keyword, string marker)
    {
        if (rest.Length != keyword.Length + Encoding.UTF8.GetByteCount(marker))
            return false;
        for (var i = 0; i < keyword.Length; i++)
        {
            if (rest[i] != (byte)keyword[i])
                return false;
        }

        return rest[keyword.Length..].SequenceEqual(Encoding.UTF8.GetBytes(marker));
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line)
    {
        var start = 0;
        var end = line.Length;
        while (start < end && line[start] is (byte)' ' or (byte)'\t' or (byte)'\r')
            start++;
        while (end > start && line[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r')
            end--;
        return line[start..end];
    }

    /// <summary>A delimiter line's kind.</summary>
    private enum Kind
    {
        None,
        Begin,
        End,
    }

    /// <summary>Where a unit's block is in a file.</summary>
    /// <param name="Found">Whether exactly one well-formed block is there.</param>
    /// <param name="Error">Why the file's block cannot be used (a second block, an unclosed one), or <see langword="null"/>.</param>
    /// <param name="Begin">The offset of the begin line.</param>
    /// <param name="BodyStart">The offset of the first body line.</param>
    /// <param name="BodyEnd">The offset of the end line (the body's end).</param>
    /// <param name="End">The offset just after the end line (its line end included).</param>
    internal readonly record struct Scan(bool Found, string? Error, int Begin, int BodyStart, int BodyEnd, int End)
    {
        /// <summary>No block.</summary>
        public static Scan None => default;

        /// <summary>A file whose block cannot be used.</summary>
        /// <param name="error">Why.</param>
        /// <returns>The scan.</returns>
        public static Scan Broken(string error) => new(false, error, 0, 0, 0, 0);
    }
}
