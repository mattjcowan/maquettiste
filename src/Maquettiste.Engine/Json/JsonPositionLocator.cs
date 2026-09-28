using System.Globalization;
using System.Text.Json;

namespace Maquettiste.Engine.Json;

/// <summary>
/// Maps a JSON pointer to a 1-based line and column by re-reading a file's bytes with <see cref="System.Text.Json.Utf8JsonReader"/>,
/// only when a diagnostic needs them (W1). Lines are counted by LF; columns count characters (UTF-8 code points), not bytes.
/// A leading UTF-8 byte order mark is skipped and does not count as a character.
/// </summary>
internal sealed class JsonPositionLocator
{
    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Locates a pointer.</summary>
    /// <param name="utf8">The file bytes.</param>
    /// <param name="jsonPointer">The pointer.</param>
    /// <returns>The line and column of the value, or <see langword="null"/> when the pointer does not resolve.</returns>
    public (int Line, int Column)? Locate(ReadOnlySpan<byte> utf8, string jsonPointer)
    {
        if (!JsonPointer.TryParse(jsonPointer, out var segments))
            return null;
        if (utf8.StartsWith(Bom))
            utf8 = utf8[Bom.Length..];

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        try
        {
            if (!reader.Read())
                return null;
            foreach (var segment in segments)
            {
                if (!Descend(ref reader, segment))
                    return null;
            }

            return Position(utf8, checked((int)reader.TokenStartIndex));
        }
        catch (JsonException)
        {
            return null; // the bytes are not valid JSON up to the pointer; the caller reports without a position
        }
    }

    /// <summary>Converts a <see cref="JsonException"/> position (0-based line, 0-based byte in line) into a 1-based line and character column.</summary>
    /// <param name="utf8">The bytes that failed to parse.</param>
    /// <param name="exception">The exception.</param>
    /// <returns>The position, or <see langword="null"/> when the exception carries none.</returns>
    public (int Line, int Column)? FromException(ReadOnlySpan<byte> utf8, JsonException exception)
    {
        if (exception.LineNumber is not { } line || exception.BytePositionInLine is not { } bytePosition)
            return null;
        if (utf8.StartsWith(Bom))
            utf8 = utf8[Bom.Length..];

        var offset = 0;
        for (long l = 0; l < line && offset < utf8.Length; l++)
        {
            var next = utf8[offset..].IndexOf((byte)'\n');
            if (next < 0)
                return ((int)line + 1, (int)bytePosition + 1);
            offset += next + 1;
        }

        var end = (int)Math.Min(utf8.Length, offset + bytePosition);
        return ((int)line + 1, CountCharacters(utf8[offset..end]) + 1);
    }

    /// <summary>Returns the 1-based line and column of a byte offset.</summary>
    /// <param name="utf8">The bytes.</param>
    /// <param name="offset">The byte offset.</param>
    /// <returns>The position.</returns>
    internal static (int Line, int Column) Position(ReadOnlySpan<byte> utf8, int offset)
    {
        var before = utf8[..offset];
        var line = before.Count((byte)'\n') + 1;
        var lineStart = before.LastIndexOf((byte)'\n') + 1;
        return (line, CountCharacters(before[lineStart..]) + 1);
    }

    private static int CountCharacters(ReadOnlySpan<byte> utf8)
    {
        var count = 0;
        foreach (var b in utf8)
        {
            if ((b & 0xC0) != 0x80)
                count++;
        }

        return count;
    }

    private static bool Descend(ref Utf8JsonReader reader, string segment)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var match = reader.ValueTextEquals(segment);
                    if (!reader.Read())
                        return false;
                    if (match)
                        return true;
                    reader.Skip();
                }

                return false;

            case JsonTokenType.StartArray:
                if (segment.Length == 0 || (segment.Length > 1 && segment[0] == '0')
                    || !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    return false;
                var i = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (i == index)
                        return true;
                    reader.Skip();
                    i++;
                }

                return false;

            default:
                return false;
        }
    }
}
