using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>The result of reading one model file. Immutable; a function of the file's bytes and path only.</summary>
internal sealed record ParsedFile
{
    /// <summary>The element, for a valid element file.</summary>
    public Element? Element { get; init; }

    /// <summary>The parsed JSON (a clone that owns its memory); <c>default</c> when the file is not JSON.</summary>
    public JsonElement Json { get; init; }

    /// <summary>The settings, for a valid <c>maquettiste.json</c>.</summary>
    public ProjectSettings? Settings { get; init; }

    /// <summary>The extension, for a valid extension file.</summary>
    public ExtensionSchema? Extension { get; init; }

    /// <summary>The locale shard, for a locale shard file that read.</summary>
    public LocaleShard? LocaleShard { get; init; }

    /// <summary>The text of a rule script or sidecar.</summary>
    public string? Text { get; init; }

    /// <summary>The file's own diagnostics (positioned where possible).</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>Whether the file loads (warnings allowed).</summary>
    public bool Valid { get; init; }

    /// <summary>Whether the file is in canonical form (JSON files the canonical writer lays out).</summary>
    public bool Canonical { get; init; }

    /// <summary>Whether the schema was evaluated for this read (statistics).</summary>
    public bool SchemaEvaluated { get; init; }

    /// <summary>Every <c>id</c> in an element file with its pointer (the element's own id at <c>""</c>).</summary>
    public ImmutableArray<(string Id, string Pointer)> Ids { get; init; } = [];

    /// <summary>Every sidecar reference (<c>"description": { "file" }</c>) with the pointer of its description.</summary>
    public ImmutableArray<(string Pointer, string File)> SidecarReferences { get; init; } = [];
}

/// <summary>
/// Reads model files: parse (MQ1001) → schema (MQ1002, MQ1006 for ids, MQ1007 for the settings format) → deserialize → canonical
/// check (MQ1003, a warning). Diagnostics carry the repo-relative path, the JSON pointer and, through <see cref="JsonPositionLocator"/>,
/// the line and column. A <c>trusted</c> read (bytes that already passed under the same engine and schemas, per the index cache) skips
/// the schema and canonical checks and only deserializes.
/// </summary>
/// <param name="schemas">The schema registry.</param>
/// <param name="canonical">The canonical writer.</param>
internal sealed class DocumentReader(ISchemaRegistry schemas, ICanonicalJson canonical)
{
    /// <summary>
    /// How model files and requests are parsed: strict JSON, and a duplicated property name is a syntax error (MQ1001) rather than
    /// something that throws later, when a <see cref="System.Text.Json.Nodes.JsonObject"/> is built from the document.
    /// </summary>
    internal static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <summary>Keys whose values are free-form JSON: their contents hold no sub-elements and no sidecar references.</summary>
    private static readonly FrozenSet<string> FreeFormKeys = new[]
    {
        "properties", "generation", "defaultProperties", "default", "validation", "discriminatorValue", "source", "parameters", "variables",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary><see cref="FreeFormKeys"/> as UTF-8, for comparing property names without allocating them.</summary>
    private static readonly byte[][] FreeFormKeysUtf8 = [.. FreeFormKeys.Order(StringComparer.Ordinal).Select(Encoding.UTF8.GetBytes)];

    private readonly JsonPositionLocator _locator = new();

    /// <summary>Reads an element file.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="repoPath">The repo-relative path.</param>
    /// <param name="trusted">Whether the bytes passed schema validation before (the index cache says so).</param>
    /// <param name="trustedCanonical">For a trusted read, whether the bytes were canonical.</param>
    /// <returns>The result.</returns>
    public ParsedFile ReadElement(byte[] bytes, string repoPath, bool trusted, bool trustedCanonical)
    {
        if (!TryParse(bytes, repoPath, "MQ1001", out var document, out var parseError))
            return new ParsedFile { Diagnostics = [parseError] };

        using (document)
        {
            var root = document.RootElement;
            var json = root.Clone();
            var elementId = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString()
                : null;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kindValue)
                || kindValue.ValueKind != JsonValueKind.String
                || !KindInfo.TryGet(kindValue.GetString()!, out var info))
            {
                var pointer = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("kind", out _) ? "/kind" : "";
                var message = root.ValueKind != JsonValueKind.Object
                    ? "A model file must hold a JSON object."
                    : $"{pointer} The file has no known 'kind'; expected one of {string.Join(", ", KindInfo.All.Select(k => k.Name))}.";
                return new ParsedFile { Json = json, Diagnostics = [Locate(RuleCatalog.Create("MQ1002", message, elementId, repoPath, pointer), bytes)] };
            }

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            if (!trusted)
            {
                var schemaDiagnostics = MapIdFailures(schemas.Evaluate(info.SchemaFile, root, repoPath), root);
                foreach (var d in schemaDiagnostics)
                    diagnostics.Add(Locate(d, bytes));
                if (schemaDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                    return new ParsedFile { Json = json, Diagnostics = Sorted(diagnostics), SchemaEvaluated = true };
            }

            Element element;
            try
            {
                element = ElementReader.ReadElement(root);
            }
            catch (Exception ex) when (IsDeserializationFailure(ex))
            {
                diagnostics.Add(RuleCatalog.Create("MQ1002", $"The element does not deserialize: {ex.Message}", elementId, repoPath, ""));
                return new ParsedFile { Json = json, Diagnostics = Sorted(diagnostics), SchemaEvaluated = !trusted };
            }

            var isCanonical = trusted ? trustedCanonical : IsCanonical(bytes, root, info.SchemaFile, repoPath);
            if (!isCanonical)
                diagnostics.Add(RuleCatalog.Create("MQ1003", "The file is not in canonical form: rewrite it from the Problems panel or run maquettiste format.", element.Id, repoPath, ""));

            var (ids, sidecars) = Scan(root);
            return new ParsedFile
            {
                Element = element,
                Json = json,
                Diagnostics = Sorted(diagnostics),
                Valid = true,
                Canonical = isCanonical,
                SchemaEvaluated = !trusted,
                Ids = ids,
                SidecarReferences = sidecars,
            };
        }
    }

    /// <summary>Reads <c>maquettiste.json</c>.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="repoPath">The repo-relative path.</param>
    /// <param name="trusted">Whether the bytes passed before.</param>
    /// <param name="trustedCanonical">For a trusted read, whether the bytes were canonical.</param>
    /// <returns>The result.</returns>
    public ParsedFile ReadSettings(byte[] bytes, string repoPath, bool trusted, bool trustedCanonical)
    {
        if (!TryParse(bytes, repoPath, "MQ1001", out var document, out var parseError))
            return new ParsedFile { Diagnostics = [parseError] };

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("formatVersion", out var version)
                && version.ValueKind == JsonValueKind.Number
                && (!version.TryGetInt64(out var v) || v != EngineVersion.FormatVersion))
            {
                var d = RuleCatalog.Create(
                    "MQ1007",
                    $"formatVersion {version.GetRawText()} is not supported: this engine reads model format {EngineVersion.FormatVersion}; run 'maquettiste migrate' with a newer engine.",
                    null,
                    repoPath,
                    "/formatVersion");
                return new ParsedFile { Json = root.Clone(), Diagnostics = [Locate(d, bytes)] };
            }

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            // A member an earlier release accepted (outputs.allow[].commit) is reported and read as if it were not there.
            var retired = RetiredSettings.Findings(root, repoPath);
            JsonDocument? stripped = null;
            if (retired.Count > 0)
            {
                foreach (var d in retired)
                    diagnostics.Add(Locate(d, bytes));
                var node = System.Text.Json.Nodes.JsonNode.Parse(root.GetRawText());
                RetiredSettings.Strip(node);
                stripped = JsonDocument.Parse(node!.ToJsonString());
                root = stripped.RootElement;
            }

            using var strippedScope = stripped;
            if (!trusted)
            {
                var schemaDiagnostics = schemas.Evaluate(ModelPaths.SettingsFile, root, repoPath);
                foreach (var d in schemaDiagnostics)
                    diagnostics.Add(Locate(d, bytes));
                if (schemaDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                    return new ParsedFile { Json = root.Clone(), Diagnostics = Sorted(diagnostics), SchemaEvaluated = true };
            }

            ProjectSettings settings;
            try
            {
                settings = ElementReader.Read<ProjectSettings>(root);
            }
            catch (Exception ex) when (IsDeserializationFailure(ex))
            {
                diagnostics.Add(RuleCatalog.Create("MQ1002", $"The settings do not deserialize: {ex.Message}", null, repoPath, ""));
                return new ParsedFile { Json = root.Clone(), Diagnostics = Sorted(diagnostics), SchemaEvaluated = !trusted };
            }

            var isCanonical = trusted ? trustedCanonical : IsCanonical(bytes, root, ModelPaths.SettingsFile, repoPath);
            if (!isCanonical)
                diagnostics.Add(RuleCatalog.Create("MQ1003", "The file is not in canonical form: rewrite it from the Problems panel or run maquettiste format.", null, repoPath, ""));
            return new ParsedFile
            {
                Settings = settings,
                Json = root.Clone(),
                Diagnostics = Sorted(diagnostics),
                Valid = true,
                Canonical = isCanonical,
                SchemaEvaluated = !trusted,
            };
        }
    }

    /// <summary>A shard validated before, read from its bytes or from the parsed-shard cache (<see cref="ShardCache"/>).</summary>
    /// <param name="shard">The shard.</param>
    /// <param name="repoPath">The repo-relative path.</param>
    /// <param name="canonical">Whether the bytes were canonical when validated.</param>
    /// <returns>The result.</returns>
    internal static ParsedFile TrustedShard(LocaleShard shard, string repoPath, bool canonical) => new()
    {
        LocaleShard = shard,
        Diagnostics = canonical ? [] : [RuleCatalog.Create("MQ1003", "The file is not in canonical form: rewrite it from the Problems panel or run maquettiste format.", null, repoPath, "")],
        Valid = true,
        Canonical = canonical,
        SidecarReferences = LocaleShardReader.SidecarsOf(shard),
    };

    /// <summary>
    /// Reads a locale shard (<c>model/locales/&lt;locale&gt;/*.json</c>; reference-types-seeds-localization.md section 3.3): schema
    /// failures are MQ1002, a non-canonical file MQ1003, as for element files. A shard is not an element and has no id.
    /// </summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="repoPath">The repo-relative path.</param>
    /// <param name="trusted">Whether the bytes passed schema validation before.</param>
    /// <param name="trustedCanonical">For a trusted read, whether the bytes were canonical.</param>
    /// <returns>The result.</returns>
    public ParsedFile ReadLocaleShard(byte[] bytes, string repoPath, bool trusted, bool trustedCanonical)
    {
        // A shard validated before is read in one forward pass, without a document tree (section 3.8).
        if (trusted && LocaleShardReader.TryRead(bytes, out var streamed))
            return TrustedShard(streamed, repoPath, trustedCanonical);

        if (!TryParse(bytes, repoPath, "MQ1001", out var document, out var parseError))
            return new ParsedFile { Diagnostics = [parseError] };

        using (document)
        {
            var root = document.RootElement;
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            if (!trusted)
            {
                var schemaDiagnostics = schemas.Evaluate(LocaleShardSchema, root, repoPath);
                foreach (var d in schemaDiagnostics)
                    diagnostics.Add(Locate(d, bytes));
                if (schemaDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                    return new ParsedFile { Json = root.Clone(), Diagnostics = Sorted(diagnostics), SchemaEvaluated = true };
            }

            LocaleShard shard;
            try
            {
                shard = ElementReader.Read<LocaleShard>(root);
            }
            catch (Exception ex) when (IsDeserializationFailure(ex))
            {
                diagnostics.Add(RuleCatalog.Create("MQ1002", $"The locale shard does not deserialize: {ex.Message}", null, repoPath, ""));
                return new ParsedFile { Json = root.Clone(), Diagnostics = Sorted(diagnostics), SchemaEvaluated = !trusted };
            }

            var isCanonical = trusted ? trustedCanonical : IsCanonical(bytes, root, LocaleShardSchema, repoPath);
            if (!isCanonical)
                diagnostics.Add(RuleCatalog.Create("MQ1003", "The file is not in canonical form: rewrite it from the Problems panel or run maquettiste format.", null, repoPath, ""));
            return new ParsedFile
            {
                LocaleShard = shard,
                SidecarReferences = LocaleShardReader.SidecarsOf(shard),
                Json = root.Clone(),
                Diagnostics = Sorted(diagnostics),
                Valid = true,
                Canonical = isCanonical,
                SchemaEvaluated = !trusted,
            };
        }
    }

    /// <summary>The schema file of locale shards.</summary>
    public const string LocaleShardSchema = "locale.json";

    /// <summary>Reads an extension schema file; every failure is MQ5004 (an invalid extension file). Extensions are not checked for canonical form.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <param name="repoPath">The repo-relative path.</param>
    /// <param name="trusted">Whether the bytes passed before.</param>
    /// <returns>The result.</returns>
    public ParsedFile ReadExtension(byte[] bytes, string repoPath, bool trusted)
    {
        if (!TryParse(bytes, repoPath, "MQ5004", out var document, out var parseError))
            return new ParsedFile { Diagnostics = [parseError] };

        using (document)
        {
            var root = document.RootElement;
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            if (!trusted)
            {
                foreach (var d in schemas.Evaluate("extension.json", root, repoPath))
                    diagnostics.Add(Locate(d with { Rule = "MQ5004", Severity = RuleCatalog.Get("MQ5004").DefaultSeverity }, bytes));
                if (diagnostics.Count > 0)
                    return new ParsedFile { Json = root.Clone(), Diagnostics = Sorted(diagnostics), SchemaEvaluated = true };
            }

            try
            {
                var extension = ElementReader.Read<ExtensionSchema>(root);
                return new ParsedFile { Extension = extension, Json = root.Clone(), Valid = true, Canonical = true, SchemaEvaluated = !trusted };
            }
            catch (Exception ex) when (IsDeserializationFailure(ex))
            {
                return new ParsedFile
                {
                    Json = root.Clone(),
                    Diagnostics = [RuleCatalog.Create("MQ5004", $"The extension does not deserialize: {ex.Message}", null, repoPath, "")],
                    SchemaEvaluated = !trusted,
                };
            }
        }
    }

    /// <summary>The canonical check on the already parsed document (no second parse into nodes) when the writer is the engine's own.</summary>
    private bool IsCanonical(byte[] bytes, JsonElement root, string schemaFile, string repoPath) =>
        canonical is CanonicalJson engine ? engine.IsCanonical(bytes, root, schemaFile, repoPath) : canonical.IsCanonical(bytes, schemaFile, repoPath);

    /// <summary>Reads a text file (a rule script or a sidecar) as UTF-8, dropping a byte order mark.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns>The result.</returns>
    public static ParsedFile ReadText(byte[] bytes) =>
        new() { Text = new UTF8Encoding(false).GetString(StripBom(bytes).Span), Valid = true, Canonical = true };

    /// <summary>Parses bytes, reporting a JSON syntax error with its position.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="repoPath">The path for diagnostics, or <see langword="null"/>.</param>
    /// <param name="rule">The rule of a syntax error.</param>
    /// <param name="document">The document when parsed.</param>
    /// <param name="error">The diagnostic when not.</param>
    /// <returns>Whether the bytes parsed.</returns>
    public bool TryParse(byte[] bytes, string? repoPath, string rule, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonDocument? document, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out Diagnostic? error)
    {
        document = null;
        if (InvalidUtf8(bytes) is { } invalid)
        {
            error = new Diagnostic(rule, RuleCatalog.Get(rule).DefaultSeverity, invalid.Message, null, repoPath, null, invalid.Line, invalid.Column);
            return false;
        }

        try
        {
            document = JsonDocument.Parse(StripBom(bytes), ParseOptions);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            var position = ErrorPosition(bytes, ex);
            error = new Diagnostic(rule, RuleCatalog.Get(rule).DefaultSeverity, "Invalid JSON: " + FirstSentence(ex.Message), null, repoPath, null, position?.Line, position?.Column);
            return false;
        }
    }

    /// <summary>
    /// The position of a parse error: the one the exception carries, else (a duplicated property name, which
    /// <see cref="JsonDocument"/> reports without a position) the start of the second occurrence.
    /// </summary>
    /// <param name="utf8">The bytes that failed to parse (a leading byte order mark is allowed).</param>
    /// <param name="exception">The exception.</param>
    /// <returns>The 1-based line and column, or <see langword="null"/>.</returns>
    public static (int Line, int Column)? ErrorPosition(ReadOnlySpan<byte> utf8, JsonException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (new JsonPositionLocator().FromException(utf8, exception) is { } position)
            return position;
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            utf8 = utf8[3..];
        return DuplicatePropertyOffset(utf8) is { } offset ? JsonPositionLocator.Position(utf8, offset) : null;
    }

    /// <summary>The byte offset of the first property name repeated within its object, or <see langword="null"/>.</summary>
    /// <param name="utf8">The JSON text, without a byte order mark.</param>
    /// <returns>The offset of the repeated name's opening quote.</returns>
    public static int? DuplicatePropertyOffset(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        var scopes = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        scopes.Push(null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName when scopes.Peek() is { } names && !names.Add(reader.GetString()!):
                        return (int)reader.TokenStartIndex;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Finds the first byte sequence that is not UTF-8. <see cref="JsonDocument"/> does not validate string contents when it parses,
    /// so bad bytes would otherwise surface as an exception much later, when a string is read.
    /// </summary>
    /// <param name="bytes">The bytes (a leading byte order mark is allowed).</param>
    /// <returns><see langword="null"/> for valid UTF-8, else a message with the 1-based line and column of the first bad byte.</returns>
    public static (string Message, int Line, int Column)? InvalidUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            bytes = bytes[3..];
        if (System.Text.Unicode.Utf8.IsValid(bytes))
            return null;
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (System.Text.Rune.DecodeFromUtf8(bytes[offset..], out _, out var consumed) != System.Buffers.OperationStatus.Done)
                break;
            offset += consumed;
        }

        var (line, column) = JsonPositionLocator.Position(bytes, offset);
        return ("Invalid JSON: the text is not valid UTF-8 (byte offset " + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").", line, column);
    }

    /// <summary>Adds the line and column of a diagnostic's pointer.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <param name="bytes">The bytes its pointer refers into.</param>
    /// <returns>The positioned diagnostic.</returns>
    public Diagnostic Locate(Diagnostic diagnostic, byte[] bytes) =>
        diagnostic.JsonPointer is { } pointer && _locator.Locate(bytes, pointer) is { } position
            ? diagnostic with { Line = position.Line, Column = position.Column }
            : diagnostic;

    /// <summary>
    /// Replaces MQ1002 failures on an <c>id</c> property holding a malformed string with one MQ1006 per pointer (D1: ids are strict ULIDs).
    /// </summary>
    /// <param name="diagnostics">Schema diagnostics.</param>
    /// <param name="root">The document.</param>
    /// <returns>The mapped diagnostics.</returns>
    public static IReadOnlyList<Diagnostic> MapIdFailures(IReadOnlyList<Diagnostic> diagnostics, JsonElement root)
    {
        if (diagnostics.Count == 0)
            return diagnostics;
        var result = new List<Diagnostic>(diagnostics.Count);
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in diagnostics)
        {
            if (d.Rule == "MQ1002" && d.JsonPointer is { } pointer
                && (pointer == "/id" || pointer.EndsWith("/id", StringComparison.Ordinal))
                && JsonPointer.TryGet(root, pointer, out var value) && value.ValueKind == JsonValueKind.String
                && !IdFormat.IsValid(value.GetString()))
            {
                if (mapped.Add(pointer))
                {
                    result.Add(RuleCatalog.Create(
                        "MQ1006",
                        $"{pointer} '{value.GetString()}' is not a valid id: ids are 26-character uppercase Crockford ULIDs.",
                        d.ElementId,
                        d.FilePath,
                        pointer));
                }

                continue;
            }

            result.Add(d);
        }

        return result;
    }

    /// <summary>Collects every <c>id</c> and every sidecar reference in an element document, skipping free-form values.</summary>
    /// <param name="root">The document.</param>
    /// <returns>The ids with pointers, and the sidecar references with the pointers of their descriptions.</returns>
    /// <remarks>Pointers are built only for the objects that hold an id or a sidecar reference, from the path of the walk.</remarks>
    public static (ImmutableArray<(string Id, string Pointer)> Ids, ImmutableArray<(string Pointer, string File)> Sidecars) Scan(JsonElement root)
    {
        var ids = ImmutableArray.CreateBuilder<(string, string)>();
        var sidecars = ImmutableArray.CreateBuilder<(string, string)>();
        var path = new List<(JsonProperty Property, int Index)>(16);
        Walk(root);
        return (ids.ToImmutable(), sidecars.ToImmutable());

        void Walk(JsonElement node)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in node.EnumerateObject())
                    {
                        if (IsFreeForm(property))
                            continue;
                        if (property.NameEquals("id"u8) && property.Value.ValueKind == JsonValueKind.String)
                        {
                            ids.Add((property.Value.GetString()!, Pointer(path, null)));
                        }
                        else if (property.NameEquals("description"u8) && property.Value.ValueKind == JsonValueKind.Object
                            && property.Value.TryGetProperty("file"u8, out var file) && file.ValueKind == JsonValueKind.String)
                        {
                            sidecars.Add((Pointer(path, "description"), file.GetString()!));
                        }
                        else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            path.Add((property, -1));
                            Walk(property.Value);
                            path.RemoveAt(path.Count - 1);
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in node.EnumerateArray())
                    {
                        if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            path.Add((default, i));
                            Walk(item);
                            path.RemoveAt(path.Count - 1);
                        }

                        i++;
                    }

                    break;
            }
        }
    }

    private static bool IsFreeForm(JsonProperty property)
    {
        foreach (var key in FreeFormKeysUtf8)
        {
            if (property.NameEquals(key))
                return true;
        }

        return false;
    }

    private static string Pointer(List<(JsonProperty Property, int Index)> path, string? last)
    {
        if (path.Count == 0 && last is null)
            return "";
        var builder = new System.Text.StringBuilder();
        foreach (var (property, index) in path)
        {
            builder.Append('/');
            if (index >= 0)
                builder.Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else
                builder.Append(JsonPointer.Escape(property.Name));
        }

        if (last is not null)
            builder.Append('/').Append(last);
        return builder.ToString();
    }

    /// <summary>Whether an exception is a deserialization failure of well-formed JSON.</summary>
    /// <param name="ex">The exception.</param>
    /// <returns><see langword="true"/> for the exceptions System.Text.Json and the model converters throw.</returns>
    public static bool IsDeserializationFailure(Exception ex) =>
        ex is JsonException or InvalidOperationException or NotSupportedException or FormatException or ArgumentException or OverflowException;

    /// <summary>Drops a UTF-8 byte order mark.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The bytes without it.</returns>
    public static ReadOnlyMemory<byte> StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsMemory(3) : bytes;

    private static ImmutableArray<Diagnostic> Sorted(ImmutableArray<Diagnostic>.Builder diagnostics) => [.. diagnostics.Order(Diagnostic.Order)];

    private static string FirstSentence(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }
}
