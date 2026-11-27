using System.Text.Json;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A document's JSON, parsed from its bytes on first read and kept after that. The loader keeps every file's bytes (the index cache is
/// written from them); keeping a parsed copy of every file beside them doubled what a large model holds in memory and what garbage
/// collection has to move, while most documents' JSON is never read during generation. The bytes are UTF-8 that already parsed once
/// with the same options, and are never changed, so every read gives the same element.
/// </summary>
/// <param name="utf8">The file's UTF-8 bytes, without a byte order mark.</param>
/// <param name="options">The options the bytes parsed with.</param>
internal sealed class LazyJson(ReadOnlyMemory<byte> utf8, JsonDocumentOptions options)
{
    private JsonDocument? _document;

    /// <summary>The root element, parsed on first read (thread-safe: racing readers keep the first document).</summary>
    public JsonElement Value
    {
        get
        {
            var document = Volatile.Read(ref _document);
            if (document is null)
            {
                // Never disposed, like a cloned element: the document lives as long as the element it hands out.
                var parsed = JsonDocument.Parse(utf8, options);
                document = Interlocked.CompareExchange(ref _document, parsed, null) ?? parsed;
                if (!ReferenceEquals(document, parsed))
                    parsed.Dispose();
            }

            return document.RootElement;
        }
    }
}
