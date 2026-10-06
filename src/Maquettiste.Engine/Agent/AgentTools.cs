using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;

namespace Maquettiste.Engine;

/// <summary>
/// One read tool of the shared catalog (<see cref="AgentTools"/>): what <c>maquettiste mcp</c> lists and what the editor's assistant
/// offers the site's model, from one definition.
/// </summary>
/// <param name="Name">The tool name (<c>^[a-z_]+$</c>).</param>
/// <param name="Title">A short title for people.</param>
/// <param name="Description">What the tool does, for the model.</param>
/// <param name="InputSchema">The JSON schema of the tool's arguments (an object schema).</param>
public sealed record AgentTool(string Name, string Title, string Description, JsonElement InputSchema);

/// <summary>A tool's answer: the text the caller sees (JSON for every tool but <c>get_plan_diff</c>) and whether it is an error.</summary>
/// <param name="Text">The text: the operation's JSON body, or a problem object (<c>code</c>, <c>status</c>, <c>title</c>, ...) on error.</param>
/// <param name="IsError">Whether the call failed.</param>
public sealed record AgentToolResult(string Text, bool IsError);

/// <summary>The settings of an <see cref="AgentTools"/> catalog that depend on its host.</summary>
public sealed record AgentToolsOptions
{
    /// <summary>The repository root (the project name when the settings have none, and the workspace).</summary>
    public required string RepoRoot { get; init; }

    /// <summary>The value of <c>MAQUETTISTE_WORKSPACE</c>, which names the workspace in <c>get_project</c>; blank means unset.</summary>
    public string? Workspace { get; init; }

    /// <summary>The mode <c>get_project</c> reports: <c>local</c> (the CLI) or the editor's mode.</summary>
    public string Mode { get; init; } = "local";

    /// <summary>
    /// Called with one line when a tool fails unexpectedly (the line names the tool, the exception type and its message); the CLI
    /// writes it to standard error, the editor to its log.
    /// </summary>
    public Func<string, string, CancellationToken, Task>? LogFailure { get; init; }
}

/// <summary>
/// The read tools shared by <c>maquettiste mcp</c> and the editor's assistant (docs/mcp.md, erratum E44), so the two cannot drift: each
/// tool's name, title, description and input schema come from one definition (<c>Agent/tools.json</c>, embedded), and each handler is one
/// method over <see cref="ModelStore"/> and <see cref="GenerationService"/> with the semantics and JSON bodies of the editor API's
/// operations. A success is the operation's body serialized with <see cref="JsonSerializerDefaults.Web"/>; a failure is a problem object
/// (the API's <c>code</c>, <c>status</c> and <c>title</c>, plus the body the API returns with that status). Every read starts with a stat
/// rescan of the model folder, so edits made behind the host are seen. Arguments are checked against the tool's schema first
/// (<see cref="ArgumentProblem"/>), so a wrongly typed one gets the <c>bad-request</c> problem naming it.
/// </summary>
public sealed partial class AgentTools
{
    private static readonly Lazy<IReadOnlyList<AgentTool>> Definitions = new(LoadDefinitions, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ModelStore _store;
    private readonly GenerationService _generation;
    private readonly AgentToolsOptions _options;
    private readonly SchemaRegistry _schemas = new();
    private readonly Dictionary<string, Func<ToolArguments, CancellationToken, Task<AgentToolResult>>> _handlers;

    /// <summary>Creates the catalog over a store and its generation service.</summary>
    /// <param name="store">The model store.</param>
    /// <param name="generation">The generation service over <paramref name="store"/>.</param>
    /// <param name="options">The host's settings.</param>
    public AgentTools(ModelStore store, GenerationService generation, AgentToolsOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _generation = generation ?? throw new ArgumentNullException(nameof(generation));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handlers = new(StringComparer.Ordinal)
        {
            ["compare_snapshots"] = CompareSnapshotsAsync,
            ["get_database_view"] = GetDatabaseViewAsync,
            ["get_element"] = GetElementAsync,
            ["get_elements"] = GetElementsAsync,
            ["get_materialize_status"] = GetMaterializeStatusAsync,
            ["get_model_index"] = GetModelIndexAsync,
            ["get_model_kinds"] = GetModelKindsAsync,
            ["get_plan"] = GetPlanAsync,
            ["get_plan_diff"] = GetPlanDiffAsync,
            ["get_project"] = GetProjectAsync,
            ["get_references"] = GetReferencesAsync,
            ["get_resolved_model"] = GetResolvedModelAsync,
            ["get_schema"] = GetSchemaAsync,
            ["get_settings"] = GetSettingsAsync,
            ["list_packs"] = ListPacksAsync,
            ["list_snapshots"] = ListSnapshotsAsync,
            ["list_validation_rules"] = ListValidationRulesAsync,
            ["localization_status"] = LocalizationStatusAsync,
            ["preview_binding_sql"] = PreviewBindingSqlAsync,
            ["preview_materialize"] = PreviewMaterializeAsync,
            ["preview_query_sql"] = PreviewQuerySqlAsync,
            ["preview_unit"] = PreviewUnitAsync,
            ["reference_type_usage"] = ReferenceTypeUsageAsync,
            ["validate"] = ValidateAsync,
        };
    }

    /// <summary>The tools, ordered by name.</summary>
    public static IReadOnlyList<AgentTool> All => Definitions.Value;

    /// <summary>The options bodies are written with: <see cref="JsonSerializerDefaults.Web"/> and nothing else, as the API's.</summary>
    public JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Whether a tool of this name is in the catalog.</summary>
    /// <param name="name">The tool name.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool Contains(string name) => All.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>Calls a tool.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="arguments">The arguments: an object, or <see langword="null"/> / an undefined element for none.</param>
    /// <param name="ct">Cancellation; a cancelled call throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The answer; an unknown tool is a <c>not-found</c> problem.</returns>
    public Task<AgentToolResult> CallAsync(string name, JsonElement? arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_handlers.TryGetValue(name, out var handler) || All.FirstOrDefault(t => t.Name == name) is not { } tool)
            return Task.FromResult(Problem("not-found", 404, $"No tool is named '{name}'."));
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (arguments is { ValueKind: JsonValueKind.Object } given)
        {
            foreach (var property in given.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        else if (arguments is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) })
        {
            return Task.FromResult(BadRequest("The arguments must be a JSON object."));
        }

        if (ArgumentProblem(tool.InputSchema, values) is { } detail)
            return Task.FromResult(BadRequest(detail));
        return GuardAsync(() => handler(new ToolArguments(values), ct), name, ct);
    }

    /// <summary>The <c>bad-request</c> problem with a detail.</summary>
    /// <param name="detail">What is wrong.</param>
    /// <returns>The tool error.</returns>
    public AgentToolResult BadRequest(string detail) => Problem("bad-request", 400, "The request is not valid.", detail);

    /// <summary>
    /// Checks each argument's JSON type against a tool's input schema, so an argument of the wrong type gets the documented
    /// <c>bad-request</c> problem naming it.
    /// </summary>
    /// <param name="schema">The tool's input schema.</param>
    /// <param name="arguments">The call's arguments.</param>
    /// <returns>The problem detail, or null when every argument has a type its parameter accepts.</returns>
    public static string? ArgumentProblem(JsonElement schema, IEnumerable<KeyValuePair<string, JsonElement>> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
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

    private static IReadOnlyList<AgentTool> LoadDefinitions()
    {
        using var stream = typeof(AgentTools).Assembly.GetManifestResourceStream("Maquettiste.Engine.Agent.tools.json")
            ?? throw new InvalidOperationException("The agent tool catalog is not embedded in the engine.");
        using var document = JsonDocument.Parse(stream);
        return [.. document.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => new AgentTool(t.GetProperty("name").GetString()!, t.GetProperty("title").GetString()!, t.GetProperty("description").GetString()!,
                t.GetProperty("inputSchema").Clone()))
            .OrderBy(t => t.Name, StringComparer.Ordinal)];
    }

    private AgentToolResult Ok<T>(T value) => new(JsonSerializer.Serialize(value, JsonOptions), false);

    private static AgentToolResult Text(string text, bool isError) => new(text, isError);

    private JsonObject? Node<T>(T value) => JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject;

    /// <summary>A problem: the API's code, status and title, an optional detail, and the fields of the API's body for that status.</summary>
    private AgentToolResult Problem(string code, int status, string title, string? detail = null, JsonObject? body = null)
    {
        var problem = new JsonObject { ["code"] = code, ["status"] = status, ["title"] = title };
        if (detail is not null)
            problem["detail"] = detail;
        if (body is not null)
        {
            foreach (var (name, value) in body.ToList())
            {
                if (!problem.ContainsKey(name))
                {
                    body.Remove(name);
                    problem[name] = value;
                }
            }
        }

        return Text(problem.ToJsonString(JsonOptions), isError: true);
    }

    private AgentToolResult NotFound(string what, string? id, string code = "not-found") => Problem(code, 404, $"No {what} has the id {id}.");

    /// <summary>Runs a tool body and turns an exception into a problem; a cancelled call stays cancelled.</summary>
    /// <remarks>
    /// Only the tool's own argument checks answer <c>bad-request</c>; an exception from the engine is the host's failure, so it is
    /// <c>internal</c> and one line goes to the log.
    /// </remarks>
    private async Task<AgentToolResult> GuardAsync(Func<Task<AgentToolResult>> body, string tool, CancellationToken ct)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Problem("model-unavailable", 503, "The model folder cannot be read.", e.Message);
        }
        catch (JsonException e)
        {
            return BadRequest(e.Message);
        }
#pragma warning disable CA1031 // A tool never crashes its host: any other failure is the internal problem with its message.
        catch (Exception e)
#pragma warning restore CA1031
        {
            if (_options.LogFailure is { } log)
                await log(tool, $"{e.GetType().FullName}: {e.Message}", CancellationToken.None).ConfigureAwait(false);
            return Problem("internal", 500, "The server failed to answer this request.", e.Message);
        }
    }

    /// <summary>Whether a value is an uppercase ULID (26 Crockford base-32 characters).</summary>
    private static bool IsUlid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value)
    {
        if (value is not { Length: 26 })
            return false;
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'A' and <= 'Z' && c is not ('I' or 'L' or 'O' or 'U')))
                return false;
        }

        return true;
    }

    /// <summary>A call's arguments, read by name; their JSON types were checked against the schema before the handler runs.</summary>
    private sealed class ToolArguments(Dictionary<string, JsonElement> values)
    {
        private bool TryGet(string name, out JsonElement value) =>
            values.TryGetValue(name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

        public string? String(string name) => TryGet(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public int? Int(string name) => TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

        public bool? Bool(string name) => TryGet(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

        public string[]? Strings(string name) => TryGet(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())]
            : null;

        public T? Deserialize<T>(string name, JsonSerializerOptions options) where T : class =>
            TryGet(name, out var v) ? v.Deserialize<T>(options) : null;
    }
}
