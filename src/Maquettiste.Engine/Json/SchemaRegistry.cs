using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Json.Schema;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Json;

/// <summary>
/// Loads the embedded schemas into a private JsonSchema.Net registry under <c>https://maquettiste.invalid/schemas/v1/</c> bases
/// (D24), so references resolve offline and nothing touches the global registry, and derives an <see cref="ObjectLayout"/> per file.
/// Built lazily on first use; thread-safe.
/// </summary>
internal sealed class SchemaRegistry : ISchemaRegistry
{
    /// <summary>The embedded resource name prefix of schema files.</summary>
    internal const string ResourcePrefix = "Maquettiste.Engine.Schemas.v1.";

    /// <summary>The base URI of schema files.</summary>
    internal const string BaseUri = "https://maquettiste.invalid/schemas/v1/";

    private readonly Lazy<State> _state = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc/>
    public IReadOnlyList<string> FileNames => _state.Value.FileNames;

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> GetFileBytes(string fileName) => _state.Value.Bytes[fileName];

    /// <inheritdoc/>
    public ObjectLayout GetLayout(string fileName) => _state.Value.Layouts[fileName];

    /// <summary>The precheck verdict for a document, without the full evaluator (tests compare it with <see cref="EvaluateFully"/>).</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="document">The document.</param>
    /// <returns>The verdict.</returns>
    internal PrecheckVerdict Precheck(string fileName, JsonElement document) => _state.Value.Precheck.Check(fileName, document);

    /// <summary>Whether the full evaluator (JsonSchema.Net) finds a document valid, bypassing the precheck.</summary>
    /// <param name="fileName">The schema file name.</param>
    /// <param name="document">The document.</param>
    /// <returns>The full evaluator's validity.</returns>
    internal bool EvaluateFully(string fileName, JsonElement document) =>
        _state.Value.Schemas[fileName].Evaluate(document, _state.Value.EvaluationOptions).IsValid;

    /// <inheritdoc/>
    public IReadOnlyList<Diagnostic> Evaluate(string fileName, JsonElement document, string path)
    {
        var state = _state.Value;
        // The precheck decides almost every valid document without the full evaluator (a fraction of the time and allocation); any
        // other verdict runs the full evaluator, so failures are reported exactly as before.
        if (state.Precheck.Check(fileName, document) == PrecheckVerdict.Valid)
            return [];
        var results = state.Schemas[fileName].Evaluate(document, state.EvaluationOptions);
        if (results.IsValid)
            return [];

        string? elementId = null;
        if (document.ValueKind == JsonValueKind.Object && document.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            elementId = id.GetString();

        var diagnostics = new List<Diagnostic>();
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is null || InsideDiscardedBranch(detail))
                continue;
            foreach (var (keyword, message) in detail.Errors)
            {
                if (ApplicatorKeywords.Contains(keyword))
                    continue; // summarized by the failures of the subschemas it applies
                var pointer = detail.InstanceLocation.ToString();
                var text = keyword.Length == 0 ? message : $"{keyword}: {message}";
                diagnostics.Add(RuleCatalog.Create("MQ1002", $"{(pointer.Length == 0 ? "/" : pointer)} {text}", elementId, path, pointer));
            }
        }

        if (diagnostics.Count == 0)
            diagnostics.Add(RuleCatalog.Create("MQ1002", "The document does not match its schema.", elementId, path, ""));
        diagnostics.Sort(Diagnostic.Order);
        return diagnostics;
    }

    /// <summary>
    /// Whether a failing node lies in a subschema whose failure does not fail the document (D34): an <c>if</c> condition, the
    /// operand of <c>not</c>, or a branch of <c>oneOf</c>, <c>anyOf</c> or <c>contains</c> whose keyword passed (its parent node
    /// reports no error for it). Such failures are noise: a string <c>description</c> fails the object branch of its <c>oneOf</c>.
    /// </summary>
    private static bool InsideDiscardedBranch(EvaluationResults node)
    {
        for (var child = node; child.Parent is { } parent; child = parent)
        {
            var parentPath = parent.EvaluationPath.ToString();
            var childPath = child.EvaluationPath.ToString();
            if (childPath.Length <= parentPath.Length + 1 || !childPath.StartsWith(parentPath + "/", StringComparison.Ordinal))
                continue;
            var rest = childPath.AsSpan(parentPath.Length + 1);
            var slash = rest.IndexOf('/');
            var keyword = (slash < 0 ? rest : rest[..slash]).ToString();
            switch (keyword)
            {
                case "if" or "not":
                    return true;
                case "oneOf" or "anyOf" or "contains" when parent.Errors is null || !parent.Errors.ContainsKey(keyword):
                    return true;
            }
        }

        return false;
    }

    private static readonly FrozenSet<string> ApplicatorKeywords =
        new[] { "properties", "items", "prefixItems", "allOf", "$ref", "additionalProperties", "patternProperties", "dependentSchemas", "then", "else" }
            .ToFrozenSet(StringComparer.Ordinal);

    private sealed record State(
        ImmutableArray<string> FileNames,
        FrozenDictionary<string, ReadOnlyMemory<byte>> Bytes,
        FrozenDictionary<string, JsonSchema> Schemas,
        FrozenDictionary<string, ObjectLayout> Layouts,
        EvaluationOptions EvaluationOptions,
        SchemaPrecheck Precheck);

    private static State Build()
    {
        var assembly = typeof(SchemaRegistry).Assembly;
        var bytes = new SortedDictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                continue;
            bytes[resource[ResourcePrefix.Length..]] = ReadResource(assembly, resource);
        }

        var documents = bytes.ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(kv.Value).RootElement.Clone(), StringComparer.Ordinal);

        var buildOptions = new BuildOptions { SchemaRegistry = new global::Json.Schema.SchemaRegistry() };
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        // common.json first: other files reference its $defs.
        foreach (var name in bytes.Keys.OrderBy(n => n == "common.json" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal))
            schemas[name] = JsonSchema.Build(documents[name], buildOptions, new Uri(BaseUri + name));

        var layouts = new LayoutBuilder(documents);
        var layoutByFile = bytes.Keys.ToDictionary(n => n, layouts.BuildRoot, StringComparer.Ordinal);

        var evaluation = new EvaluationOptions { OutputFormat = OutputFormat.List };
        return new State(
            [.. bytes.Keys],
            bytes.ToFrozenDictionary(StringComparer.Ordinal),
            schemas.ToFrozenDictionary(StringComparer.Ordinal),
            layoutByFile.ToFrozenDictionary(StringComparer.Ordinal),
            evaluation,
            SchemaPrecheck.Build(documents));
    }

    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Derives <see cref="ObjectLayout"/> trees from schema documents, following <c>$ref</c>, combinators, items and maps.</summary>
    private sealed class LayoutBuilder(IReadOnlyDictionary<string, JsonElement> documents)
    {
        private readonly Dictionary<string, ObjectLayout> _byRef = new(StringComparer.Ordinal);

        public ObjectLayout BuildRoot(string file) => BuildRef(file, "");

        private ObjectLayout BuildRef(string file, string pointer)
        {
            var key = file + "#" + pointer;
            if (_byRef.TryGetValue(key, out var existing))
                return existing;
            var layout = new ObjectLayout();
            _byRef[key] = layout; // registered before filling, so recursive references terminate
            Fill(layout, file, Resolve(file, pointer));
            return layout;
        }

        private ObjectLayout Build(string file, JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return ObjectLayout.FreeForm;
            if (node.TryGetProperty("$ref", out var reference) && !HasStructure(node))
            {
                var (targetFile, pointer) = SplitRef(file, reference.GetString()!);
                return BuildRef(targetFile, pointer);
            }

            var layout = new ObjectLayout();
            Fill(layout, file, node);
            return layout;
        }

        private void Fill(ObjectLayout layout, string file, JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return;

            if (node.TryGetProperty("$ref", out var reference))
            {
                var (targetFile, pointer) = SplitRef(file, reference.GetString()!);
                Fill(layout, targetFile, Resolve(targetFile, pointer));
            }

            if (node.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                var order = node.TryGetProperty("x-order", out var xo) && xo.ValueKind == JsonValueKind.Array
                    ? xo.EnumerateArray().Select(e => e.GetString()!).ToList()
                    : properties.EnumerateObject().Select(p => p.Name).ToList();
                foreach (var name in order)
                {
                    if (!properties.TryGetProperty(name, out var propertySchema))
                        continue;
                    var unless = propertySchema.ValueKind == JsonValueKind.Object && propertySchema.TryGetProperty("x-default-unless", out var u)
                        ? u.GetString()
                        : null;
                    layout.AddProperty(new LayoutProperty(name, Build(file, propertySchema), DefaultOf(file, propertySchema)) { DefaultUnless = unless });
                }
            }

            if (node.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
                layout.MapValues ??= Build(file, additional);

            if (node.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                layout.Items ??= Build(file, items);

            if (node.TryGetProperty("x-sort", out var sort) && sort.ValueKind == JsonValueKind.String)
                layout.SortKey ??= sort.GetString();

            foreach (var combinator in (ReadOnlySpan<string>)["allOf", "anyOf", "oneOf"])
            {
                if (node.TryGetProperty(combinator, out var branches) && branches.ValueKind == JsonValueKind.Array)
                {
                    foreach (var branch in branches.EnumerateArray())
                        Fill(layout, file, branch);
                }
            }
        }

        private JsonElement? DefaultOf(string file, JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object)
                return null;
            if (schema.TryGetProperty("default", out var value))
                return value.Clone();
            if (schema.TryGetProperty("$ref", out var reference))
            {
                var (targetFile, pointer) = SplitRef(file, reference.GetString()!);
                return DefaultOf(targetFile, Resolve(targetFile, pointer));
            }

            return null;
        }

        private static bool HasStructure(JsonElement node) =>
            node.TryGetProperty("properties", out _) || node.TryGetProperty("items", out _) || node.TryGetProperty("additionalProperties", out _)
            || node.TryGetProperty("x-sort", out _)
            || node.TryGetProperty("allOf", out _) || node.TryGetProperty("anyOf", out _) || node.TryGetProperty("oneOf", out _);

        private static (string File, string Pointer) SplitRef(string currentFile, string reference)
        {
            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            var file = hash < 0 ? reference : reference[..hash];
            var pointer = hash < 0 ? "" : reference[(hash + 1)..];
            return (file.Length == 0 ? currentFile : file, pointer);
        }

        private JsonElement Resolve(string file, string pointer)
        {
            if (!documents.TryGetValue(file, out var node))
                throw new InvalidOperationException($"Schema reference to unknown file '{file}'.");
            if (pointer.Length == 0)
                return node;
            foreach (var raw in pointer.TrimStart('/').Split('/'))
            {
                var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out var child))
                    node = child;
                else if (node.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index < node.GetArrayLength())
                    node = node[index];
                else
                    throw new InvalidOperationException($"Schema reference '{file}#{pointer}' does not resolve.");
            }

            return node;
        }
    }
}
