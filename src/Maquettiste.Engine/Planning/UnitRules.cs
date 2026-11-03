using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The unit and parameter rules of generation-ui.md section 5.3 that do not need a render: MQ6019 (output path outside every root),
/// MQ6021 (unknown scope, with the nearest one), MQ6023 (a project value fails the parameter schema) and MQ6024 (a project value for an
/// undeclared parameter). The loader, the pack read and the paths check share them, so the editor and a run never disagree.
/// </summary>
internal static class UnitRules
{
    /// <summary>The fixed scopes a unit's <c>for</c> may name; <c>select &lt;name&gt;</c> is the open one.</summary>
    public static readonly IReadOnlyList<string> Scopes =
    [
        "model", "each package", "each entity", "each attribute", "each relation", "each enum", "each value object", "each database", "each table",
        "each view", "each sequence", "each routine", "each database type", "each sql object", "each query", "each reference type", "each seed", "each locale", "each process", "each actor", "each scenario",
        "each operation",
    ];

    /// <summary>The MQ6021 message for an unknown scope, with the nearest valid one.</summary>
    /// <param name="unitId">The unit id, or <see langword="null"/> when unknown.</param>
    /// <param name="scope">The scope as written.</param>
    /// <returns>The message.</returns>
    public static string UnknownScope(string? unitId, string scope)
    {
        var nearest = Scopes.OrderBy(s => Distance(s, scope)).ThenBy(s => s, StringComparer.Ordinal).First();
        var unit = unitId is null ? "A unit" : $"Unit '{unitId}'";
        return $"{unit} has the unknown scope '{scope}'; did you mean '{nearest}'? Valid scopes: {string.Join(", ", Scopes)}, or 'select <name>' for a selector the pack's scripts register.";
    }

    /// <summary>
    /// MQ6019: whether the unit's output base plus the pattern's literal prefix (up to the last <c>/</c> before the first code span)
    /// can stay under an allowed root or be an allowed file (an <c>outputs.allow</c> entry naming the file itself), and whether the
    /// pattern has a literal <c>.</c>, <c>..</c> or absolute segment.
    /// </summary>
    /// <param name="pattern">The output pattern (the unit's, or its companion's).</param>
    /// <param name="outputBase">The pack's output base, repo-relative.</param>
    /// <param name="roots">The allowed roots.</param>
    /// <returns>The reason it cannot, or <see langword="null"/>.</returns>
    public static string? OutputRootProblem(string pattern, string outputBase, IReadOnlyList<OutputRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(roots);
        var code = pattern.IndexOf("{{", StringComparison.Ordinal);
        var literal = code < 0 ? pattern : pattern[..code];
        if (literal.StartsWith('/') || literal.Contains('\\', StringComparison.Ordinal) || (literal.Length > 1 && literal[1] == ':'))
            return $"the output path '{pattern}' is absolute";
        var segments = literal.Split('/');
        var complete = code < 0 ? segments : segments[..^1];
        if (complete.Any(s => s is "." or ".."))
            return $"the output path '{pattern}' has a '.' or '..' segment";
        var prefix = Join(Normalize(outputBase), string.Join('/', complete.Where(s => s.Length > 0)));
        if (prefix.Length == 0 || roots.Count == 0)
            return roots.Count == 0 ? "no output root is allowed (outputs.allow is empty)" : null;
        foreach (var root in roots)
        {
            var r = Normalize(root.Path);
            if (r.Length == 0 || IsUnder(prefix, r) || IsUnder(r, prefix))
                return null;
        }

        return $"'{prefix}' is neither an allowed file nor under an allowed output root ({string.Join(", ", roots.Select(r => Normalize(r.Path)).Order(StringComparer.Ordinal))})";
    }

    /// <summary>MQ6019 diagnostics for every unit (and companion) of a pack.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="settings">The pack's project settings.</param>
    /// <param name="roots">The allowed roots.</param>
    /// <param name="packFile">The repo path of <c>pack.json</c>.</param>
    /// <returns>The diagnostics.</returns>
    public static IReadOnlyList<Diagnostic> OutputRoots(PackManifest manifest, PackSettings settings, IReadOnlyList<OutputRoot> roots, string packFile)
    {
        var result = new List<Diagnostic>();
        for (var i = 0; i < manifest.Units.Count; i++)
        {
            var unit = manifest.Units[i];
            var pointer = "/units/" + i.ToString(CultureInfo.InvariantCulture);
            if (unit.Output is { } output && OutputRootProblem(output, settings.Output, roots) is { } problem)
                result.Add(RuleCatalog.Create("MQ6019", $"Unit '{unit.Id}' cannot write under an allowed root: {problem}.", filePath: packFile, jsonPointer: pointer + "/output"));
            if (unit.Companion is { } companion && OutputRootProblem(companion.Output, settings.Output, roots) is { } other)
                result.Add(RuleCatalog.Create("MQ6019", $"Unit '{unit.Id}''s companion cannot write under an allowed root: {other}.", filePath: packFile,
                    jsonPointer: pointer + "/companion/output"));
        }

        return result;
    }

    /// <summary>
    /// MQ6023 and MQ6024 for the project's values of one pack's parameters and, with the snapshot, the <c>x-ref</c> checks of
    /// next-phase-1-design.md section 4.1: MQ2009 for a malformed or misplaced keyword in <c>parameterSchema</c> (in
    /// <c>pack.json</c>), and MQ2010, MQ2001, MQ2002 and MQ2011 for a project value it types (in <c>maquettiste.json</c>).
    /// </summary>
    /// <param name="name">The pack name.</param>
    /// <param name="manifest">The manifest.</param>
    /// <param name="settings">The pack's project settings.</param>
    /// <param name="settingsFile">The repo path of <c>maquettiste.json</c>.</param>
    /// <param name="model">The snapshot the values name ids of, or <see langword="null"/> to leave the <c>x-ref</c> checks out.</param>
    /// <param name="packFile">The repo path of the pack's <c>pack.json</c>, where MQ2009 is reported.</param>
    /// <returns>The diagnostics: MQ2009 in keyword order, then ordinal by parameter name.</returns>
    public static IReadOnlyList<Diagnostic> Parameters(string name, PackManifest manifest, PackSettings settings, string settingsFile,
        ModelSnapshot? model = null, string? packFile = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(settings);
        var properties = SchemaProperties(manifest);
        var result = new List<Diagnostic>();
        var refs = model is null ? null : ParameterRefs(manifest, model, packFile ?? "", result);
        var found = new List<(string Pointer, string Value, XRef Ref)>();
        foreach (var (key, value) in settings.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var pointer = "/packs/" + JsonPointer.Escape(name) + "/parameters/" + JsonPointer.Escape(key);
            if (properties.TryGetValue(key, out var schema))
            {
                if (Check(schema, value) is { } problem)
                    result.Add(RuleCatalog.Create("MQ6023", $"Parameter '{key}' of pack '{name}' {problem}.", filePath: settingsFile, jsonPointer: pointer));
                if (refs is not null && refs.TryGetValue(key, out var node))
                {
                    found.Clear();
                    PropertySchemaSet.Walk(node, value, pointer, found);
                    foreach (var (at, text, xref) in found)
                    {
                        foreach (var bad in ParameterRefProblems(model!, text, xref))
                            result.Add(RuleCatalog.Create(bad.Rule, $"Parameter '{key}' of pack '{name}' {bad.Message}.", filePath: settingsFile, jsonPointer: at));
                    }
                }
            }
            else if (!manifest.Parameters.ContainsKey(key))
            {
                result.Add(RuleCatalog.Create("MQ6024", $"Pack '{name}' neither defaults nor declares the parameter '{key}'; the templates never read it.",
                    filePath: settingsFile, jsonPointer: pointer));
            }
        }

        return result;
    }

    /// <summary>
    /// The <c>x-ref</c> trees of a manifest's <c>parameterSchema.properties</c> by parameter name, reporting MQ2009 in
    /// <paramref name="packFile"/>: a malformed keyword, and the keys that need what a parameter lacks (<c>of: "$element"</c> and
    /// <c>sameDatabase</c> need a host element), plus an <c>of</c> that names no element of the model.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="model">The snapshot, or <see langword="null"/> to skip the check of <c>of</c> ids.</param>
    /// <param name="packFile">The repo path of <c>pack.json</c>.</param>
    /// <param name="diagnostics">Receives MQ2009, or <see langword="null"/> to report nothing.</param>
    /// <returns>The trees.</returns>
    internal static IReadOnlyDictionary<string, XRefNode> ParameterRefs(PackManifest manifest, ModelSnapshot? model, string packFile, List<Diagnostic>? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ParameterSchema is not { ValueKind: JsonValueKind.Object } schema || !schema.TryGetProperty("properties", out var properties))
            return new Dictionary<string, XRefNode>(StringComparer.Ordinal);
        void Problem(string pointer, string message) => diagnostics?.Add(RuleCatalog.Create("MQ2009", message, filePath: packFile, jsonPointer: pointer));
        var (refs, keywords, _) = PropertySchemaSet.Read(properties, "/parameterSchema/properties", Problem);
        foreach (var (pointer, xref) in keywords)
        {
            if (xref.Of == XRef.ElementScope)
                Problem(pointer + "/of", "x-ref of \"$element\" names a member of the element that holds the value, and a pack parameter is held by no element: name an element id or leave of out.");
            else if (xref.Of is { } of && model is not null && !(model.TryGetEntry(of, out var entry) && entry.OwnerId == of))
                Problem(pointer + "/of", $"x-ref of names {of}, which is no element of the model.");
            if (xref.SameDatabase)
                Problem(pointer + "/sameDatabase", "x-ref sameDatabase compares with the database of the element that holds the value, and a pack parameter is held by no element.");
        }

        return refs;
    }

    /// <summary>
    /// What is wrong with one value an <c>x-ref</c> types in a pack parameter, as <c>BuiltinRules.CheckXRef</c> judges
    /// a model value: MQ2010 for text, else MQ2001 for each id of a keyed value that does not exist, else MQ2002 or
    /// MQ2011 on the last id.
    /// </summary>
    private static List<(string Rule, string Message)> ParameterRefProblems(ModelSnapshot model, string value, XRef xref)
    {
        var kinds = string.Join(" or ", xref.Kinds);
        if (Validation.BuiltinRules.XRefIds(value) is not { } ids)
            return [("MQ2010", $"holds '{value}', which is a name or other text, not an id: the pack's parameterSchema types it as the id of {Article(kinds)} {kinds}")];
        var missing = ids.Distinct(StringComparer.Ordinal).Where(id => !model.TryGetEntry(id, out _)).ToList();
        if (missing.Count > 0)
        {
            var note = Validation.BuiltinRules.ReservedKindsNote(xref);
            return [.. missing.Select(id => ("MQ2001", $"references '{id}', which does not exist" + (note.Length > 0 ? $" ({note})" : "")))];
        }

        model.TryGetEntry(ids[^1], out var entry);
        if (!xref.Kinds.Contains(entry!.Kind, StringComparer.Ordinal))
            return [("MQ2002", $"references '{ids[^1]}', {Article(entry.Kind)} {entry.Kind}; the pack's parameterSchema expects {Article(kinds)} {kinds}")];
        if (xref.Of is { } listId && listId != XRef.ElementScope
            && (entry.Kind != "row" || model.Get<Seed>(entry.OwnerId) is not { } seed || seed.Target != listId))
            return [("MQ2011", $"references '{ids[^1]}', which is not a row of a seed of {listId} (x-ref of)")];
        return [];
    }

    private static string Article(string noun) => Validation.BuiltinRules.Article(noun);

    /// <summary>The <c>parameterSchema.properties</c> of a manifest, by name.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The property schemas.</returns>
    public static IReadOnlyDictionary<string, JsonElement> SchemaProperties(PackManifest manifest)
    {
        var map = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (manifest.ParameterSchema is { ValueKind: JsonValueKind.Object } schema && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
                map[property.Name] = property.Value;
        }

        return map;
    }

    /// <summary>Checks a value against the parameter-schema subset: <c>type</c>, <c>enum</c>, <c>minimum</c>, <c>maximum</c>,
    /// <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>.</summary>
    /// <param name="schema">The property schema.</param>
    /// <param name="value">The value.</param>
    /// <returns>What is wrong, or <see langword="null"/>.</returns>
    public static string? Check(JsonElement schema, JsonElement value)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return null;
        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString() ?? "").ToList() : [type.GetString() ?? ""];
            if (!types.Any(t => HasType(value, t)))
                return $"must be of type {string.Join(" or ", types)}, not {TypeName(value)}";
        }

        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
            && !values.EnumerateArray().Any(v => JsonElement.DeepEquals(v, value)))
            return $"must be one of {string.Join(", ", values.EnumerateArray().Select(v => v.GetRawText()))}";
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            if (schema.TryGetProperty("minimum", out var min) && min.ValueKind == JsonValueKind.Number && number < min.GetDouble())
                return $"must be at least {min.GetRawText()}";
            if (schema.TryGetProperty("maximum", out var max) && max.ValueKind == JsonValueKind.Number && number > max.GetDouble())
                return $"must be at most {max.GetRawText()}";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out var minLength) && minLength.TryGetInt32(out var least) && text.Length < least)
                return $"must have at least {least} characters";
            if (schema.TryGetProperty("maxLength", out var maxLength) && maxLength.TryGetInt32(out var most) && text.Length > most)
                return $"must have at most {most} characters";
            if (schema.TryGetProperty("pattern", out var pattern) && pattern.GetString() is { } regex
                && !Regex.IsMatch(text, regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                return $"must match the pattern {regex}";
        }

        return null;
    }

    private static bool HasType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string TypeName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').Trim('/');

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b;

    private static bool IsUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal) || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var current = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }
        }

        return row[b.Length];
    }
}
