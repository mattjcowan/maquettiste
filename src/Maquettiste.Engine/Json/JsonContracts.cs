using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Json;

/// <summary>The embedded JSON Schemas (<c>schemas/v1/*.json</c>) and the key layouts derived from their <c>x-order</c> lists.</summary>
public interface ISchemaRegistry
{
    /// <summary>The schema file names, for example <c>entity.json</c>, ordinal.</summary>
    IReadOnlyList<string> FileNames { get; }

    /// <summary>Returns a schema file's embedded bytes (for <c>init</c>, which writes them to <c>.maquettiste/.schema/v1/</c>).</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <returns>The bytes.</returns>
    ReadOnlyMemory<byte> GetFileBytes(string fileName);

    /// <summary>Evaluates a document against a schema.</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="document">The document.</param>
    /// <param name="path">The document's repo-relative path, for diagnostics.</param>
    /// <returns>One MQ1002 diagnostic per failed keyword; empty when valid.</returns>
    IReadOnlyList<Diagnostic> Evaluate(string fileName, JsonElement document, string path);

    /// <summary>Returns the key layout of a schema's root object.</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <returns>The layout.</returns>
    ObjectLayout GetLayout(string fileName);
}

/// <summary>Writes canonical JSON (SPEC section 11; engine-design.md section 3).</summary>
public interface ICanonicalJson
{
    /// <summary>
    /// Writes a document in canonical form: keys in <c>x-order</c> (free-form maps ordinal), defaults and nulls omitted,
    /// <c>$schema</c> first and computed from <paramref name="documentPath"/>, two-space indent, LF, trailing newline, no BOM.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="fileName">The schema file name that lays the document out.</param>
    /// <param name="documentPath">The document's path, relative to the repo root or the model root, with <c>/</c> separators.</param>
    /// <returns>The canonical bytes.</returns>
    byte[] Write(JsonNode document, string fileName, string documentPath);

    /// <summary>Serializes a value with <see cref="Model.EngineJson.Options"/> and writes it canonically.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="documentPath">The document's path.</param>
    /// <returns>The canonical bytes.</returns>
    byte[] Serialize<T>(T value, string fileName, string documentPath);

    /// <summary>Whether bytes are already in canonical form.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="documentPath">The document's path.</param>
    /// <returns><see langword="true"/> when writing the parsed bytes reproduces them exactly.</returns>
    bool IsCanonical(ReadOnlySpan<byte> bytes, string fileName, string documentPath);
}

/// <summary>
/// The key layout of one object location in a schema: its ordered keys with defaults and child layouts, or its map value
/// layout, or its item layout. A layout with none of these is free-form: its keys are written in ordinal order.
/// </summary>
public sealed class ObjectLayout
{
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, LayoutProperty> _properties = new(StringComparer.Ordinal);

    internal ObjectLayout()
    {
    }

    /// <summary>A free-form layout.</summary>
    public static ObjectLayout FreeForm { get; } = new();

    /// <summary>The object's keys in <c>x-order</c>; empty for maps and free-form objects.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>The object's properties by key.</summary>
    public IReadOnlyDictionary<string, LayoutProperty> Properties => _properties;

    /// <summary>The layout of map values when the location is a map (<c>additionalProperties</c> schema).</summary>
    public ObjectLayout? MapValues { get; internal set; }

    /// <summary>The layout of array items when the location is an array.</summary>
    public ObjectLayout? Items { get; internal set; }

    /// <summary>
    /// For an array whose schema declares <c>"x-sort": "&lt;key&gt;"</c>: the integer item key the canonical writer stable-sorts the
    /// items by, a missing or non-integer value counting as 0 (SPEC section 11: attributes in <c>order</c>).
    /// </summary>
    public string? SortKey { get; internal set; }

    /// <summary>
    /// For an array whose schema declares <c>"x-layout": "row-per-line"</c>: each item is written on its own line in the compact
    /// row form (reference-types-seeds-localization.md section 2.1), with numbers in the shortest plain decimal text.
    /// </summary>
    public bool RowPerLine { get; internal set; }

    /// <summary>For an array whose schema declares <c>"x-trim": "trailing-nulls"</c>: trailing <c>null</c> items are dropped.</summary>
    public bool TrimTrailingNulls { get; internal set; }

    /// <summary>Whether the location is an object with declared keys.</summary>
    public bool HasKeys => _keys.Count > 0;

    /// <summary>Whether the location is a map.</summary>
    public bool IsMap => MapValues is not null && _keys.Count == 0;

    internal void AddProperty(LayoutProperty property)
    {
        if (_properties.TryAdd(property.Name, property))
            _keys.Add(property.Name);
    }
}

/// <summary>One declared key of an <see cref="ObjectLayout"/>.</summary>
/// <param name="Name">The key.</param>
/// <param name="Layout">The value's layout.</param>
/// <param name="Default">The schema <c>default</c>, when declared; a value equal to it is omitted.</param>
public sealed record LayoutProperty(string Name, ObjectLayout Layout, JsonElement? Default)
{
    /// <summary>
    /// A sibling key (the schema's <c>x-default-unless</c>) whose presence switches <see cref="Default"/> off: a column's
    /// <c>nullable: true</c> is the default of a designed column but an explicit override on an overlay column (one with <c>attribute</c>).
    /// </summary>
    public string? DefaultUnless { get; init; }

    /// <summary>The default as a node, for deep comparison.</summary>
    internal JsonNode? DefaultNode { get; } = Default is { } d ? JsonNode.Parse(d.GetRawText()) : null;
}
