using System.Text;

namespace Maquettiste.Engine.PostProcessing;

/// <summary>
/// Step 1 of post-processing (engine-design.md section 13): line endings to LF, no byte order mark, UTF-8 bytes. Every text that
/// reaches an output file or a hash passes through here, including formatter output and the region bodies read from disk.
/// </summary>
internal static class TextNormalizer
{
    /// <summary>UTF-8 without a byte order mark, throwing on invalid bytes so hand-written content is never silently replaced.</summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Strips a leading byte order mark and turns CRLF and lone CR into LF.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        if (text.IndexOf('\r', StringComparison.Ordinal) < 0)
            return text;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>Normalizes and encodes text as UTF-8 without a byte order mark.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Encode(string text) => StrictUtf8.GetBytes(Normalize(text));

    /// <summary>Encodes text as UTF-8 without a byte order mark (no normalization).</summary>
    /// <param name="text">The text.</param>
    /// <param name="bytes">The bytes, or an empty array on failure.</param>
    /// <returns><see langword="false"/> when the text is not valid Unicode (it holds an unpaired UTF-16 surrogate).</returns>
    public static bool TryEncode(string text, out byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            bytes = StrictUtf8.GetBytes(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>Decodes UTF-8 bytes (a leading byte order mark is dropped) and normalizes line endings.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="text">The normalized text.</param>
    /// <returns><see langword="false"/> when the bytes are not valid UTF-8.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out string text)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            bytes = bytes[3..];
        try
        {
            text = Normalize(StrictUtf8.GetString(bytes));
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }
}
