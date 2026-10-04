using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine;

/// <summary>The handlers of the shared read tools, one per tool, in name order.</summary>
public sealed partial class AgentTools
{
    private async Task<AgentToolResult> GetDatabaseViewAsync(ToolArguments args, CancellationToken ct)
    {
        var id = args.String("id");
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var document = await _store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return NotFound("database", id);
        if (document.Element.Id != id || document.Element.KindName != "database")
            return NotFound("database", id, "not-a-database");
        return Ok(await _generation.GetDatabaseViewAsync(id, ct).ConfigureAwait(false));
    }

    private async Task<AgentToolResult> GetElementAsync(ToolArguments args, CancellationToken ct)
    {
        var id = args.String("id");
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var document = await _store.GetElementAsync(id, ct).ConfigureAwait(false);
        return document is null ? NotFound("element", id) : Ok(document);
    }

    private async Task<AgentToolResult> GetElementsAsync(ToolArguments args, CancellationToken ct)
    {
        var limit = args.Int("limit");
        var ids = args.Strings("ids");
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        if (ids is { Length: > ModelPages.MaxLimit })
            return BadRequest($"At most {ModelPages.MaxLimit} ids can be read at once; {ids.Length} were given.");
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        try
        {
            return Ok(ModelPages.ReadElements(snapshot, ids, Filter(args), args.Strings("fields"), args.String("cursor"), limit ?? ModelPages.DefaultLimit));
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<AgentToolResult> GetMaterializeStatusAsync(ToolArguments args, CancellationToken ct)
    {
        var database = args.String("database");
        if (string.IsNullOrEmpty(database))
            return BadRequest("database is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var status = await _store.GetMaterializeStatusAsync(database, ct).ConfigureAwait(false);
        return status is null ? NotFound("database", database, "not-a-database") : Ok(status);
    }

    private async Task<AgentToolResult> GetModelIndexAsync(ToolArguments args, CancellationToken ct)
    {
        var limit = args.Int("limit");
        var cursor = args.String("cursor");
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var index = await _store.GetIndexAsync(ct).ConfigureAwait(false);
        var rows = ModelPages.Filter(index, Filter(args));
        if (limit is null && string.IsNullOrEmpty(cursor))
            return Ok(rows);
        try
        {
            return Ok(ModelPages.PageIndex(rows, cursor, limit ?? ModelPages.DefaultLimit));
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<AgentToolResult> GetModelKindsAsync(ToolArguments args, CancellationToken ct)
    {
        var by = args.String("by");
        if (by is not (null or "" or "kind" or "package"))
            return BadRequest($"by must be kind or package, not '{by}'.");
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return Ok(ModelPages.Kinds(snapshot.Summaries(), by == "package"));
    }

    private async Task<AgentToolResult> GetPlanAsync(ToolArguments args, CancellationToken ct)
    {
        var planId = args.String("planId");
        if (string.IsNullOrEmpty(planId))
            return BadRequest("planId is required.");
        var plan = IsUlid(planId) ? await _generation.GetPlanAsync(planId, ct).ConfigureAwait(false) : null;
        if (plan is null)
            return NotFound("plan", planId);
        return Ok(args.Bool("units") == true ? plan : plan with { Units = [] });
    }

    private async Task<AgentToolResult> GetPlanDiffAsync(ToolArguments args, CancellationToken ct)
    {
        var planId = args.String("planId");
        var path = args.String("path");
        if (string.IsNullOrEmpty(planId))
            return BadRequest("planId is required.");
        if (string.IsNullOrEmpty(path))
            return BadRequest("path is required.");
        if (!IsUlid(planId))
            return NotFound("plan", planId);
        var diff = await _generation.GetPlanDiffAsync(planId, path, ct).ConfigureAwait(false);
        if (diff is null)
            return Problem("not-found", 404, $"Plan {planId} has no file {path}.");
        return Text(diff, isError: false);
    }

    private async Task<AgentToolResult> GetProjectAsync(ToolArguments args, CancellationToken ct)
    {
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var packs = await _generation.GetPacksAsync(ct).ConfigureAwait(false);
        var name = snapshot.Settings.Name is { Length: > 0 } n ? n : Path.GetFileName(_options.RepoRoot);
        var where = WorkspaceInfo.Detect(_options.RepoRoot, _options.Workspace);
        return Ok(new AgentProjectInfo(name, snapshot.Settings.FormatVersion, EngineVersion.Value, EngineVersion.Product, EngineVersion.Build, where.Workspace,
            where.Branch, where.Worktree, where.Repository, _options.Mode, snapshot.Settings, snapshot.SettingsHash,
            [.. snapshot.Summaries().Where(s => s.Kind == "database")], packs.Packs, packs.Diagnostics, [.. snapshot.Extensions.Select(e => e.Schema)], null));
    }

    private async Task<AgentToolResult> GetReferencesAsync(ToolArguments args, CancellationToken ct)
    {
        var id = args.String("id");
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (await _store.GetElementAsync(id, ct).ConfigureAwait(false) is null)
            return NotFound("element", id);
        return Ok(await _store.GetReferencesAsync(id, ct).ConfigureAwait(false));
    }

    private async Task<AgentToolResult> GetResolvedModelAsync(ToolArguments args, CancellationToken ct)
    {
        var limit = args.Int("limit");
        var scope = args.String("scope");
        var database = args.String("database");
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        var name = string.IsNullOrEmpty(scope) ? "all" : scope;
        if (!ResolvedRecords.ScopeNames.Contains(name, StringComparer.Ordinal))
            return BadRequest($"scope must be one of {string.Join(", ", ResolvedRecords.ScopeNames)}, not '{scope}'.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(database))
        {
            var document = await _store.GetElementAsync(database, ct).ConfigureAwait(false);
            if (document is null)
                return NotFound("database", database);
            if (document.Element.Id != database || document.Element.KindName != "database")
                return NotFound("database", database, "not-a-database");
        }

        try
        {
            return Ok(await _generation.GetResolvedAsync(new ResolvedQuery(name, database, args.String("cursor"), limit ?? ModelPages.DefaultLimit), ct).ConfigureAwait(false));
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<AgentToolResult> GetSchemaAsync(ToolArguments args, CancellationToken ct)
    {
        var kind = args.String("kind");
        if (string.IsNullOrEmpty(kind))
            return BadRequest("kind is required.");
        var elementKind = KindInfo.TryGet(kind, out var info) ? info : null;
        var file = elementKind is not null ? elementKind.SchemaFile
            : kind.EndsWith(".json", StringComparison.Ordinal) ? kind
            : kind + ".json";
        if (file is null || !_schemas.FileNames.Contains(file, StringComparer.Ordinal))
        {
            var known = string.Join(", ", KindInfo.All.Select(k => k.Name).Concat(_schemas.FileNames.Select(f => f[..^5])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            return Problem("not-found", 404, $"No schema is named '{kind}'.", "Known names: " + known + ".");
        }

        var result = new JsonObject { ["name"] = kind, ["file"] = file, ["schema"] = SchemaNode(file) };
        var references = new JsonObject();
        var pending = new Queue<string>([file]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { file };
        while (pending.TryDequeue(out var current))
        {
            foreach (var referenced in ReferencedFiles(current))
            {
                if (seen.Add(referenced))
                {
                    references[referenced] = SchemaNode(referenced);
                    pending.Enqueue(referenced);
                }
            }
        }

        result["references"] = references;
        if (elementKind is not null)
        {
            // The project's extension schemas constrain `properties` too; the loader applies them on every save.
            var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var extensions = snapshot.Extensions.Select(e => e.Schema)
                .Where(e => e.AppliesTo.Kinds.Contains(elementKind.Name, StringComparer.Ordinal))
                .Select(e => new { e.Name, e.Description, e.AppliesTo, e.Properties, e.Required, e.SchemaPath });
            result["extensions"] = JsonSerializer.SerializeToNode(extensions.ToList(), JsonOptions);
        }

        return Text(result.ToJsonString(JsonOptions), isError: false);
    }

    private async Task<AgentToolResult> GetSettingsAsync(ToolArguments args, CancellationToken ct) =>
        Ok(await _store.GetSettingsAsync(ct).ConfigureAwait(false));

    private async Task<AgentToolResult> ListPacksAsync(ToolArguments args, CancellationToken ct)
    {
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return Ok(await _generation.GetPacksAsync(ct).ConfigureAwait(false));
    }

    private Task<AgentToolResult> ListValidationRulesAsync(ToolArguments args, CancellationToken ct) => Task.FromResult(Ok(RuleCatalog.Describe()));

    private async Task<AgentToolResult> LocalizationStatusAsync(ToolArguments args, CancellationToken ct) =>
        Ok(await _store.GetLocalizationStatusAsync(ct).ConfigureAwait(false));

    private async Task<AgentToolResult> PreviewBindingSqlAsync(ToolArguments args, CancellationToken ct)
    {
        var entity = args.String("entity");
        var binding = args.String("binding");
        var placeholder = args.String("placeholder");
        var dialect = args.String("dialect");
        if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(binding))
            return BadRequest("entity and binding are required.");
        var options = new QuerySqlOptions { Placeholder = string.IsNullOrEmpty(placeholder) ? "@" : placeholder };
        if (options.Placeholder is not ("@" or ":" or "$"))
            return BadRequest($"placeholder must be @, : or $, not '{placeholder}'.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var document = await _store.GetElementAsync(entity, ct).ConfigureAwait(false);
        if (document is null)
            return NotFound("entity", entity);
        if (document.Element is not Entity e || e.Id != entity)
            return NotFound("entity", entity, "not-an-entity");
        if (!e.Bindings.Any(b => b.Id == binding))
            return NotFound("binding", binding, "not-a-binding");
        try
        {
            return Ok(await _generation.GetBindingSqlAsync(entity, binding, string.IsNullOrEmpty(dialect) ? null : dialect, options, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<AgentToolResult> PreviewMaterializeAsync(ToolArguments args, CancellationToken ct)
    {
        var database = args.String("database");
        var op = args.String("op");
        var entities = args.Strings("entities");
        var tables = args.Strings("tables");
        var package = args.String("package");
        if (string.IsNullOrEmpty(database))
            return BadRequest("database is required.");
        var request = op switch
        {
            "materialize-tables" when entities is { Length: > 0 } => new MaterializeRequest(op, database, entities, args.String("schema")),
            "materialize-entities" when tables is { Length: > 0 } && !string.IsNullOrEmpty(package) => new MaterializeRequest(op, database, tables, null, package),
            _ => null,
        };
        if (request is null)
            return BadRequest("op is materialize-tables (with entities) or materialize-entities (with tables and package).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (await _store.GetElementAsync(database, ct).ConfigureAwait(false) is not { } document || document.Element is not Database)
            return NotFound("database", database, "not-a-database");
        return Ok(await _store.PlanMaterializeAsync(request, ct).ConfigureAwait(false));
    }

    private async Task<AgentToolResult> PreviewQuerySqlAsync(ToolArguments args, CancellationToken ct)
    {
        var id = args.String("id");
        var placeholder = args.String("placeholder");
        var lists = args.String("lists");
        var dialect = args.String("dialect");
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        var options = new QuerySqlOptions
        {
            Placeholder = string.IsNullOrEmpty(placeholder) ? "@" : placeholder,
            Lists = string.IsNullOrEmpty(lists) ? "expand" : lists,
        };
        if (options.Placeholder is not ("@" or ":" or "$"))
            return BadRequest($"placeholder must be @, : or $, not '{placeholder}'.");
        if (options.Lists is not ("expand" or "any"))
            return BadRequest($"lists must be expand or any, not '{lists}'.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var document = await _store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return NotFound("query", id);
        if (document.Element.Id != id || document.Element.KindName != "query")
            return NotFound("query", id, "not-a-query");
        try
        {
            return Ok(await _generation.GetQuerySqlAsync(id, string.IsNullOrEmpty(dialect) ? null : dialect, options, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<AgentToolResult> PreviewUnitAsync(ToolArguments args, CancellationToken ct)
    {
        var pack = args.String("pack");
        var unit = args.String("unit");
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(unit))
            return BadRequest("pack and unit are required.");
        try
        {
            var options = new PreviewOptions
            {
                Overlay = args.Deserialize<Dictionary<string, string>>("overlay", EngineJson.Options),
                UnitOverride = args.Deserialize<PackUnit>("unitOverride", EngineJson.Options),
                Parameters = args.Deserialize<Dictionary<string, JsonElement>>("parameters", EngineJson.Options),
            };
            return Ok(await _generation.PreviewAsync(pack, unit, args.String("elementId"), options, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<AgentToolResult> ReferenceTypeUsageAsync(ToolArguments args, CancellationToken ct)
    {
        var id = args.String("id");
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        var usage = await _store.GetReferenceTypeUsageAsync(id, ct).ConfigureAwait(false);
        return usage is null ? NotFound("reference type", id) : Ok(usage);
    }

    private async Task<AgentToolResult> ValidateAsync(ToolArguments args, CancellationToken ct)
    {
        var elementIds = args.Strings("elementIds");
        if (elementIds is not null && elementIds.Any(id => !IsUlid(id)))
            return BadRequest("elementIds must be element ids (uppercase ULIDs).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var scope = new ValidationScope(elementIds, args.Bool("includeReferrers") ?? true, args.Bool("includeScriptRules") ?? true);
        return Ok(await _store.ValidateAsync(scope, ct).ConfigureAwait(false));
    }

    private static ElementFilter Filter(ToolArguments args) =>
        new(args.String("kind"), args.String("package"), args.String("tag"), args.String("category"), args.String("stereotype"), args.String("query"));

    private JsonNode SchemaNode(string file) => JsonNode.Parse(_schemas.GetFileBytes(file).Span)!;

    private IEnumerable<string> ReferencedFiles(string file)
    {
        var text = Encoding.UTF8.GetString(_schemas.GetFileBytes(file).Span);
        foreach (var name in _schemas.FileNames)
        {
            if (name != file && text.Contains("\"" + name, StringComparison.Ordinal))
                yield return name;
        }
    }
}

/// <summary>The body of <c>get_project</c>: the editor API's <c>ProjectInfo</c> (getProject) without the editor's git status and icon.</summary>
/// <param name="Name">The project name.</param>
/// <param name="FormatVersion">The model format version.</param>
/// <param name="EngineVersion">The engine contract version (<see cref="Engine.EngineVersion.Value"/>), not the release.</param>
/// <param name="ProductVersion">The release running (<see cref="Engine.EngineVersion.Product"/>).</param>
/// <param name="Build">The build of the release (<see cref="Engine.EngineVersion.Build"/>).</param>
/// <param name="Workspace">What the checkout is called (<see cref="WorkspaceInfo.Workspace"/>).</param>
/// <param name="Branch">The git branch, when it can be read.</param>
/// <param name="Worktree">The linked worktree's name.</param>
/// <param name="Repository">The main checkout's folder name, for a linked worktree.</param>
/// <param name="Mode">The host's mode (<c>local</c> for the CLI).</param>
/// <param name="Settings">The typed settings.</param>
/// <param name="SettingsHash">The settings file hash.</param>
/// <param name="Databases">The database summaries.</param>
/// <param name="Packs">The pack manifests.</param>
/// <param name="PackDiagnostics">The diagnostics of loading the packs.</param>
/// <param name="Extensions">The extension schemas.</param>
/// <param name="Git">Always <see langword="null"/> (the tools do not read git).</param>
public sealed record AgentProjectInfo(string Name, int FormatVersion, string EngineVersion, string ProductVersion, string Build, string? Workspace,
    string? Branch, string? Worktree, string? Repository, string Mode, ProjectSettings Settings, string SettingsHash,
    IReadOnlyList<ElementSummary> Databases, IReadOnlyList<PackManifest> Packs, IReadOnlyList<Diagnostic> PackDiagnostics,
    IReadOnlyList<ExtensionSchema> Extensions, object? Git);
