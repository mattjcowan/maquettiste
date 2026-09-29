using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>Builds the options of the <c>maquettiste mcp</c> server: its tools, the conventions resource and prompt, and its instructions.</summary>
internal static class McpServerSetup
{
    /// <summary>The URI of the modeling conventions resource (the <c>maquettiste-modeling</c> skill).</summary>
    public const string ConventionsUri = "maquettiste://conventions";

    /// <summary>The name of the prompt that carries the modeling conventions.</summary>
    public const string ConventionsPrompt = "modeling-conventions";

    /// <summary>The manifest resource name of the embedded <c>skills/maquettiste-modeling/SKILL.md</c>.</summary>
    internal const string ConventionsResource = "Maquettiste.Cli.Skills/maquettiste-modeling/SKILL.md";

    private const string Instructions = """
        Maquettiste model server. The model is one canonical JSON document per element under .maquettiste/; every reference between
        elements is an id. Read the conventions (resource maquettiste://conventions, or the modeling-conventions prompt) before editing.
        Start with get_project and get_model_index; get_schema returns the JSON schema of a kind so documents you build are valid.
        get_element returns a document and its hash: save_element and delete_element take that hash as expectedHash and never
        overwrite a newer file (a conflict returns the disk version). delete_element refuses while other elements reference the
        element unless resolution is remove-references. apply_batch applies several changes all or nothing. validate reports
        diagnostics with rule ids and positions. To generate code: plan, read get_plan_diff for the files you care about, then
        apply_plan with the plan id (a plan whose inputs changed is refused as stale). Failures are tool errors whose JSON has a
        stable code (not-found, conflict, invalid, referenced, stale, bad-request, ...) and the engine's diagnostics.
        """;

    /// <summary>Creates the server options.</summary>
    /// <param name="tools">The tool implementations.</param>
    /// <returns>The options.</returns>
    public static McpServerOptions CreateOptions(ModelTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal);
        var methods = typeof(ModelTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name, StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var tool = McpServerTool.Create(method, tools);
            tool.ProtocolTool.InputSchema = WithRequired(tool.ProtocolTool.InputSchema);
            toolCollection.Add(tool);
        }

        var conventions = ReadConventions();
        var resources = new McpServerResourceCollection()
        {
            McpServerResource.Create(() => conventions, new McpServerResourceCreateOptions
            {
                UriTemplate = ConventionsUri,
                Name = "conventions",
                Title = "Maquettiste modeling conventions",
                Description = "How a Maquettiste model is laid out (kinds, folders, ids, canonical JSON) and the workflow to change it, validate it and generate code.",
                MimeType = "text/markdown",
            }),
        };
        var prompts = new McpServerPrimitiveCollection<McpServerPrompt>(StringComparer.Ordinal)
        {
            McpServerPrompt.Create(() => conventions, new McpServerPromptCreateOptions
            {
                Name = ConventionsPrompt,
                Title = "Maquettiste modeling conventions",
                Description = "The modeling conventions as a prompt, to read before changing the model.",
            }),
        };

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "maquettiste", Version = EngineVersion.Value },
            ServerInstructions = Instructions.Replace("\r\n", "\n", StringComparison.Ordinal),
            ToolCollection = toolCollection,
            ResourceCollection = resources,
            PromptCollection = prompts,
        };
        options.Filters.Request.CallToolFilters.Add(next => async (request, ct) =>
            request.MatchedPrimitive is McpServerTool tool && request.Params?.Arguments is { } arguments
                && ArgumentProblem(tool.ProtocolTool.InputSchema, arguments) is { } detail
                ? tools.ArgumentError(detail)
                : await next(request, ct).ConfigureAwait(false));
        return options;
    }

    /// <summary>
    /// Lists the parameters whose description ends with "; required." in the schema's <c>required</c> array. They are optional in
    /// C# so a missing one reaches the tool's own check (a <c>bad-request</c> naming it) instead of the SDK binder's generic error,
    /// while the schema still tells the client which arguments to send.
    /// </summary>
    /// <param name="schema">The input schema reflected from the method.</param>
    /// <returns>The schema with the required list.</returns>
    internal static JsonElement WithRequired(JsonElement schema)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root || root["properties"] is not JsonObject properties)
            return schema;
        var required = properties
            .Where(p => p.Value?["description"]?.GetValue<string>() is { } d && d.EndsWith("; required.", StringComparison.Ordinal))
            .Select(p => (JsonNode?)JsonValue.Create(p.Key))
            .ToArray();
        if (required.Length == 0)
            return schema;
        root["required"] = new JsonArray(required);
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>
    /// Checks each argument's JSON type against the tool's input schema, so an argument of the wrong type gets the documented
    /// <c>bad-request</c> problem naming it (the SDK's binder would otherwise answer with a generic error).
    /// </summary>
    /// <param name="schema">The tool's input schema.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns>The problem detail, or null when every argument has a type its parameter accepts.</returns>
    internal static string? ArgumentProblem(JsonElement schema, IEnumerable<KeyValuePair<string, JsonElement>> arguments)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var (name, value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            if (!properties.TryGetProperty(name, out var parameter))
                continue;
            if (TypeProblem(parameter, value) is { } expected)
                return $"{name} must be {expected}, not {Describe(value.ValueKind)}.";
            if (value.ValueKind == JsonValueKind.Array && parameter.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (TypeProblem(items, item) is { } itemExpected)
                        return $"{name}[{index.ToString(CultureInfo.InvariantCulture)}] must be {itemExpected}, not {Describe(item.ValueKind)}.";
                    index++;
                }
            }
        }

        return null;
    }

    /// <summary>The accepted types, when the schema declares types and the value has none of them.</summary>
    private static string? TypeProblem(JsonElement parameter, JsonElement value)
    {
        if (!parameter.TryGetProperty("type", out var type))
            return null;
        var accepted = type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray(),
            _ => Array.Empty<string>(),
        };
        if (accepted.Length == 0 || accepted.Any(t => Accepts(t, value)))
            return null;
        return string.Join(" or ", accepted.Where(t => t != "null").DefaultIfEmpty("null").Select(t => t switch { "integer" => "an integer", "array" => "an array", "object" => "an object", "null" => "null", _ => "a " + t }));
    }

    private static bool Accepts(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.Null => "null",
        _ => "missing",
    };

    /// <summary>Returns the modeling conventions (the embedded <c>skills/maquettiste-modeling/SKILL.md</c>) without its front matter.</summary>
    /// <returns>The Markdown text, LF line endings.</returns>
    public static string ReadConventions()
    {
        using var stream = typeof(McpServerSetup).Assembly.GetManifestResourceStream(ConventionsResource)
            ?? throw new InvalidOperationException("The modeling conventions are not embedded in the CLI.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end >= 0)
                text = text[(end + 5)..].TrimStart('\n');
        }

        return text;
    }
}
