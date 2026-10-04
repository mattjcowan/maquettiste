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
        To read a large model, get_model_kinds counts what there is, get_elements returns documents in pages (fields trims them) and
        get_resolved_model returns what generation sees as flat records in pages; pass next as cursor until it is null.
        get_element returns a document and its hash: save_element and delete_element take that hash as expectedHash and never
        overwrite a newer file (a conflict returns the disk version). delete_element refuses while other elements reference the
        element unless resolution is remove-references (clears optional references) or delete-dependents (also removes or deletes
        what cannot exist without it); dryRun true shows the plan first. apply_batch applies several changes all or nothing. validate reports
        diagnostics with rule ids and positions. To generate code: plan, read get_plan_diff for the files you care about, then
        apply_plan with the plan id (a plan whose inputs changed is refused as stale). Failures are tool errors whose JSON has a
        stable code (not-found, conflict, invalid, referenced, stale, bad-request, ...) and the engine's diagnostics.
        """;

    /// <summary>The most of the repository's <c>CONVENTIONS.md</c> the conventions resource and prompt carry, in bytes.</summary>
    public const int ProjectConventionsLimit = AgentConventions.ProjectConventionsLimit;

    /// <summary>Creates the server options.</summary>
    /// <param name="tools">The tool implementations.</param>
    /// <param name="repoRoot">The repository root, where the conventions resource and prompt look for the repository's own conventions.</param>
    /// <returns>The options.</returns>
    public static McpServerOptions CreateOptions(ModelTools tools, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(repoRoot);
        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal);
        // The read tools come from the catalog the editor's assistant shares (AgentTools); the others are this server's own methods.
        var all = typeof(ModelTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => (Name: m.GetCustomAttribute<McpServerToolAttribute>()!.Name!, Create: (Func<McpServerTool>)(() =>
            {
                var tool = McpServerTool.Create(m, tools);
                tool.ProtocolTool.InputSchema = WithRequired(tool.ProtocolTool.InputSchema);
                return tool;
            })))
            .Concat(AgentTools.All.Select(t => (t.Name, Create: (Func<McpServerTool>)(() => new CatalogTool(t, tools.Catalog)))))
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var (_, create) in all)
            toolCollection.Add(create());

        var conventions = ReadConventions();
        var resources = new McpServerResourceCollection()
        {
            McpServerResource.Create(() => WithProjectConventions(conventions, repoRoot), new McpServerResourceCreateOptions
            {
                UriTemplate = ConventionsUri,
                Name = "conventions",
                Title = "Maquettiste modeling conventions",
                Description = "How a Maquettiste model is laid out (kinds, folders, ids, canonical JSON) and the workflow to change it, validate it and generate code, followed by the repository's own conventions (CONVENTIONS.md) when it has them.",
                MimeType = "text/markdown",
            }),
        };
        var prompts = new McpServerPrimitiveCollection<McpServerPrompt>(StringComparer.Ordinal)
        {
            McpServerPrompt.Create(() => WithProjectConventions(conventions, repoRoot), new McpServerPromptCreateOptions
            {
                Name = ConventionsPrompt,
                Title = "Maquettiste modeling conventions",
                Description = "The modeling conventions as a prompt, followed by the repository's own conventions when it has them, to read before changing the model.",
            }),
        };

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "maquettiste", Version = EngineVersion.Product },
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
    internal static string? ArgumentProblem(JsonElement schema, IEnumerable<KeyValuePair<string, JsonElement>> arguments) =>
        AgentTools.ArgumentProblem(schema, arguments);

    /// <summary>
    /// Appends the repository's own conventions, <c>.claude/skills/maquettiste-modeling/CONVENTIONS.md</c>, read fresh on each request,
    /// after a separator and a heading; the text is unchanged when the file does not exist or is empty. At most
    /// <see cref="ProjectConventionsLimit"/> bytes are read, and a note says so when the file is longer.
    /// </summary>
    /// <param name="conventions">The embedded conventions (<see cref="ReadConventions"/>).</param>
    /// <param name="repoRoot">The repository root.</param>
    /// <returns>The Markdown text, LF line endings.</returns>
    public static string WithProjectConventions(string conventions, string repoRoot) => AgentConventions.WithProjectConventions(conventions, repoRoot);

    /// <summary>Returns the modeling conventions (the embedded <c>skills/maquettiste-modeling/SKILL.md</c>) without its front matter.</summary>
    /// <returns>The Markdown text, LF line endings.</returns>
    public static string ReadConventions() => AgentConventions.Read();
}

/// <summary>
/// A tool of the shared catalog (<see cref="AgentTools"/>) served over MCP: its name, title, description and input schema are the
/// catalog's, its annotations say read-only, idempotent and closed-world, and a call runs the catalog's handler.
/// </summary>
/// <param name="definition">The catalog entry.</param>
/// <param name="catalog">The catalog over this server's store.</param>
internal sealed class CatalogTool(AgentTool definition, AgentTools catalog) : McpServerTool
{
    /// <inheritdoc/>
    public override Tool ProtocolTool { get; } = new()
    {
        Name = definition.Name,
        Title = definition.Title,
        Description = definition.Description,
        InputSchema = definition.InputSchema,
        Annotations = new ToolAnnotations { Title = definition.Title, IdempotentHint = true, OpenWorldHint = false, ReadOnlyHint = true },
    };

    /// <inheritdoc/>
    public override IReadOnlyList<object> Metadata { get; } = [];

    /// <inheritdoc/>
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonElement? arguments = request.Params?.Arguments is { } given ? JsonSerializer.SerializeToElement(given) : null;
        var result = await catalog.CallAsync(definition.Name, arguments, cancellationToken).ConfigureAwait(false);
        return new CallToolResult { Content = [new TextContentBlock { Text = result.Text }], IsError = result.IsError ? true : null };
    }
}
