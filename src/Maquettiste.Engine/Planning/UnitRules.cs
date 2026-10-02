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
        "model", "each package", "each entity", "each relation", "each enum", "each value object", "each table", "each view", "each sequence",
        "each routine", "each database type", "each sql object", "each reference type", "each seed", "each locale", "each process", "each actor", "each scenario",
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
    /// can stay under an allowed root, and whether the pattern has a literal <c>.</c>, <c>..</c> or absolute segment.
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

        return $"'{prefix}' is under no allowed output root ({string.Join(", ", roots.Select(r => Normalize(r.Path)).Order(StringComparer.Ordinal))})";
    }

    /// <summary>
    /// Why a unit's outputs fall outside the selected roots (generation-ui.md section 4.3): the root holding the output base plus the
    /// pattern's literal prefix is committed and the selection is <c>built</c>, or the reverse. <see langword="null"/> when it is
    /// selected, when every root is, or when the root is only known once the path is rendered.
    /// </summary>
    /// <param name="pattern">The output pattern.</param>
    /// <param name="outputBase">The pack's output base.</param>
    /// <param name="roots">The allowed roots.</param>
    /// <param name="selection">The selected roots.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public static string? RootNotSelected(string pattern, string outputBase, IReadOnlyList<OutputRoot> roots, RootSelection selection)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (selection == RootSelection.All)
            return null;
        var code = pattern.IndexOf("{{", StringComparison.Ordinal);
        var literal = code < 0 ? pattern : pattern[..code];
        var segments = literal.Split('/');
        var prefix = Join(Normalize(outputBase), string.Join('/', (code < 0 ? segments : segments[..^1]).Where(s => s.Length > 0)));
        var root = roots.Where(r => IsUnder(prefix, Normalize(r.Path))).OrderByDescending(r => Normalize(r.Path).Length).FirstOrDefault();
        if (root is null || root.Commit == (selection == RootSelection.Committed))
            return null;
        var kind = root.Commit ? "committed" : "built";
        return $"Its outputs go under '{Normalize(root.Path)}', a {kind} root, and this run selects {(selection == RootSelection.Committed ? "committed" : "built")} roots only.";
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

    /// <summary>MQ6023 and MQ6024 for the project's values of one pack's parameters.</summary>
    /// <param name="name">The pack name.</param>
    /// <param name="manifest">The manifest.</param>
    /// <param name="settings">The pack's project settings.</param>
    /// <param name="settingsFile">The repo path of <c>maquettiste.json</c>.</param>
    /// <returns>The diagnostics, ordinal by parameter name.</returns>
    public static IReadOnlyList<Diagnostic> Parameters(string name, PackManifest manifest, PackSettings settings, string settingsFile)
    {
        var properties = SchemaProperties(manifest);
        var result = new List<Diagnostic>();
        foreach (var (key, value) in settings.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var pointer = "/packs/" + JsonPointer.Escape(name) + "/parameters/" + JsonPointer.Escape(key);
            if (properties.TryGetValue(key, out var schema))
            {
                if (Check(schema, value) is { } problem)
                    result.Add(RuleCatalog.Create("MQ6023", $"Parameter '{key}' of pack '{name}' {problem}.", filePath: settingsFile, jsonPointer: pointer));
            }
            else if (!manifest.Parameters.ContainsKey(key))
            {
                result.Add(RuleCatalog.Create("MQ6024", $"Pack '{name}' neither defaults nor declares the parameter '{key}'; the templates never read it.",
                    filePath: settingsFile, jsonPointer: pointer));
            }
        }

        return result;
    }

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
