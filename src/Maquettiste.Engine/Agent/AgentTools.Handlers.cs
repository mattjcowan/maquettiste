using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine;

/// <summary>The handlers of the shared read tools, one per tool, in name order.</summary>
public sealed partial class AgentTools
{
    /// <summary>The sections <c>get_project</c> adds on request, in name order.</summary>
    private static readonly IReadOnlyList<string> ProjectSections = ["extensions", "packs", "settings"];

    private async Task<AgentToolResult> GetBindingDriftAsync(ToolArguments args, CancellationToken ct)
    {
        var limit = args.Int("limit") ?? ModelPages.DefaultLimit;
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        var query = new BindingDriftQuery(args.String("database") is { Length: > 0 } database ? database : null,
            args.Strings("entities") is { Length: > 0 } entities ? entities : null, args.Bool("all") ?? false, args.String("cursor"), limit);
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        try
        {
            return Ok(await _store.GetBindingDriftAsync(query, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return BadRequest(ex.Message); // an unknown database, entity or cursor
        }
    }

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
        var sections = args.Strings("sections") ?? [];
        if (sections.FirstOrDefault(s => !ProjectSections.Contains(s, StringComparer.Ordinal)) is { } unknown)
            return BadRequest($"sections holds {string.Join(", ", ProjectSections)}, not '{unknown}'.");
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var packs = await _generation.GetPacksAsync(ct).ConfigureAwait(false);
        var name = snapshot.Settings.Name is { Length: > 0 } n ? n : Path.GetFileName(_options.RepoRoot);
        var where = _options.Workspace ?? WorkspaceInfo.None;
        bool Wants(string section) => sections.Contains(section, StringComparer.Ordinal);
        return Ok(new AgentProjectInfo(name, snapshot.Settings.FormatVersion, EngineVersion.Value, EngineVersion.Product, EngineVersion.Build, where.Workspace,
            where.Branch, where.Worktree, where.Repository, _options.Mode, snapshot.SettingsHash,
            [.. snapshot.Summaries().Where(s => s.Kind == "database")],
            [.. packs.Packs.Select(p => new AgentPackSummary(p.Name, p.Version, p.Description, p.Units.Count))],
            new AgentDiagnosticCounts(packs.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error), packs.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning),
                packs.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info)),
            [.. snapshot.Extensions.Select(e => e.Schema.Name)], ProjectSections)
        {
            CustomKinds = [.. snapshot.PropertySchemas.ContentSources.Select(c => c.CustomType!)],
            Settings = Wants("settings") ? snapshot.Settings : null,
            PackManifests = Wants("packs") ? packs.Packs : null,
            PackDiagnostics = Wants("packs") ? packs.Diagnostics : null,
            ExtensionSchemas = Wants("extensions") ? [.. snapshot.Extensions.Select(e => e.Schema)] : null,
        });
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

    private async Task<AgentToolResult> GetCalculatedAsync(ToolArguments args, CancellationToken ct)
    {
        var ids = args.Strings("ids");
        if (ids is not { Length: > 0 })
            return BadRequest("ids is required: 1 to 100 ids.");
        if (ids.Length > GenerationService.MaxCalculatedIds)
            return BadRequest($"At most {GenerationService.MaxCalculatedIds} ids can be read at once; {ids.Length} were given.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        try
        {
            return Ok(await _generation.GetCalculatedAsync(new CalculatedValueQuery(ids, args.Strings("names")), ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
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
        PropertySchemaSource? content = null;
        if (kind.StartsWith(ModelPages.CustomKindPrefix, StringComparison.Ordinal))
        {
            // A custom kind (next-phase-2-design.md section 4.2): custom.json, plus the declaration and its compiled content schema.
            var type = kind[ModelPages.CustomKindPrefix.Length..];
            var declared = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
            content = declared.PropertySchemas.ContentOf(type);
            if (content is null)
            {
                var types = declared.PropertySchemas.ContentSources.Select(c => ModelPages.CustomKindPrefix + c.CustomType);
                return Problem("not-found", 404, $"The project declares no custom kind '{type}'.",
                    "Declared custom kinds: " + (types.Any() ? string.Join(", ", types) : "none") + ".");
            }
        }

        var elementKind = content is not null ? KindInfo.Get(ElementKind.Custom) : KindInfo.TryGet(kind, out var info) ? info : null;
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
            // The typed parameters of the stereotypes that may apply to the kind (next-phase-1-design.md section 4.2): they constrain
            // `properties` on the elements that carry them, implied ones included.
            var parameters = snapshot.All<Stereotype>()
                .Where(s => s.Parameters is not null && (s.AppliesTo.Count == 0 || s.AppliesTo.Contains(elementKind.Name, StringComparer.Ordinal))
                    && ReferenceEquals(snapshot.GetStereotype(s.Key), s))
                .OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => new JsonObject { ["stereotype"] = s.Key, ["parameters"] = JsonSerializer.SerializeToNode(s.Parameters, JsonOptions) });
            result["stereotypeParameters"] = new JsonArray([.. parameters]);
            if (content is not null && snapshot.CustomKinds.FirstOrDefault(k => string.Equals(k.Path, content.Path, StringComparison.Ordinal)) is { } declaration)
            {
                var customKind = JsonSerializer.SerializeToNode(declaration.Kind, JsonOptions)!.AsObject();
                customKind["path"] = declaration.Path;
                result["customKind"] = customKind;
                result["contentSchema"] = content.SchemaJson();
            }
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

    private Task<AgentToolResult> ListValidationRulesAsync(ToolArguments args, CancellationToken ct)
    {
        var family = args.String("family");
        var ids = args.Strings("ids");
        var query = args.String("query");
        var cursor = args.String("cursor");
        var limit = args.Int("limit");
        if (limit is < 1 or > ModelPages.MaxLimit)
            return Task.FromResult(BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}."));
        if (string.IsNullOrEmpty(family) && ids is null && string.IsNullOrEmpty(query) && string.IsNullOrEmpty(cursor) && limit is null)
            return Task.FromResult(Ok(RuleCatalog.Summarize()));
        try
        {
            return Task.FromResult(Ok(RuleCatalog.Page(family, ids, query, cursor, limit ?? ModelPages.DefaultLimit)));
        }
        catch (FormatException ex)
        {
            return Task.FromResult(BadRequest(ex.Message));
        }
    }

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

    private async Task<AgentToolResult> PreviewExpressionAsync(ToolArguments args, CancellationToken ct)
    {
        var element = args.String("element");
        if (string.IsNullOrEmpty(element))
            return BadRequest("element is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        // One element and what its expression names are read; the model is neither validated nor resolved (decision 24).
        if (await _store.GetElementAsync(element, ct).ConfigureAwait(false) is null)
            return NotFound("element", element);
        var request = new ExpressionPreviewRequest
        {
            Element = element,
            At = args.String("at") is { Length: > 0 } at ? at : null,
            Text = args.String("text"),
            Site = args.String("site") is { Length: > 0 } site ? site : null,
            Attribute = args.String("attribute") is { Length: > 0 } attribute ? attribute : null,
            Database = args.String("database") is { Length: > 0 } database ? database : null,
            Lifecycle = args.String("lifecycle") is { Length: > 0 } lifecycle ? lifecycle : null,
            Values = args.Json("values"),
        };
        try
        {
            return Ok(await _store.PreviewExpressionAsync(request, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message.Split(" (Parameter", 2)[0]);
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
            "materialize-attributes" when entities is { Length: > 0 } => new MaterializeRequest(op, database, entities, Columns: args.Strings("columns") is { Length: > 0 } columns ? columns : null),
            "materialize-columns" when entities is { Length: > 0 } => new MaterializeRequest(op, database, entities, Attributes: args.Strings("attributes") is { Length: > 0 } refs ? refs : null),
            "map-by-name" when entities is { Length: > 0 } => new MaterializeRequest(op, database, entities),
            "resync-binding" when entities is { Length: > 0 } => new MaterializeRequest(op, database, entities, Columns: args.Strings("columns") is { Length: > 0 } named ? named : null,
                KeepNames: args.Bool("keepNames") ?? false),
            _ => null,
        };
        if (request is null)
            return BadRequest("op is materialize-tables (with entities), materialize-entities (with tables and package), materialize-attributes (with entities, and columns for one), materialize-columns (with entities, and attributes for one), map-by-name (with entities) or resync-binding (with entities, columns for one, and keepNames).");
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

    private async Task<AgentToolResult> PreviewSeedRowsAsync(ToolArguments args, CancellationToken ct)
    {
        var seed = args.String("seed");
        if (string.IsNullOrEmpty(seed))
            return BadRequest("seed is required.");
        var limit = args.Int("limit");
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        var database = args.String("database");
        var cursor = args.String("cursor");
        try
        {
            var preview = await _store.PreviewSeedRowsAsync(seed, string.IsNullOrEmpty(database) ? null : database, string.IsNullOrEmpty(cursor) ? null : cursor,
                limit ?? ModelPages.DefaultLimit, ct).ConfigureAwait(false);
            return preview is null ? NotFound("seed", seed) : Ok(preview);
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message.Split(" (Parameter", 2)[0]);
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

    private async Task<AgentToolResult> TagUsageAsync(ToolArguments args, CancellationToken ct)
    {
        var package = args.String("package") is { Length: > 0 } p ? p : null;
        var tag = args.String("tag") is { Length: > 0 } t ? t : null;
        var cursor = args.String("cursor");
        var limit = args.Int("limit");
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        var usage = await _store.GetTagUsageAsync(package, ct).ConfigureAwait(false);
        if (usage is null)
            return NotFound("package", package!);
        try
        {
            if (tag is not null)
            {
                if (usage.Tags.FirstOrDefault(u => string.Equals(u.Tag, tag, StringComparison.Ordinal)) is not { } use)
                    return Problem("not-found", 404, $"The tag vocabulary scope has no tag '{tag}': it is neither declared nor used there.");
                var (elements, nextElements) = ModelPages.Page(use.Elements, e => ("element", e, ""), cursor, limit ?? ModelPages.DefaultLimit);
                return Ok(new TagUseElements(usage.Package, use.Tag, use.Declared, use.Uses, elements, nextElements));
            }

            IReadOnlyList<TagUse> tags = usage.Tags;
            string? next = null;
            if (limit is not null || !string.IsNullOrEmpty(cursor))
                (tags, next) = ModelPages.Page(usage.Tags, u => ("tag", u.Tag, ""), cursor, limit ?? ModelPages.DefaultLimit);
            return Ok(new TagUsageSummary(usage.Package, usage.Vocabulary, usage.Strict, [.. tags.Select(u => new TagUseSummary(u.Tag, u.Declared, u.Uses, u.Examples))], next));
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<AgentToolResult> ValidateAsync(ToolArguments args, CancellationToken ct)
    {
        var elementIds = args.Strings("elementIds");
        if (elementIds is not null && elementIds.Any(id => !IsUlid(id)))
            return BadRequest("elementIds must be element ids (uppercase ULIDs).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (args.String("against") is { Length: > 0 } against)
        {
            if (elementIds is not null)
                return BadRequest("against compares the whole model with a baseline; leave elementIds out.");
            ModelBaseline baseline;
            try
            {
                baseline = await ModelBaseline.OpenAsync(_store, against, Environment.CurrentDirectory, _options.AllowFolderBaselines, Snapshots, ct)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }

            using (baseline)
                return Ok(await _store.ValidateAgainstAsync(baseline, ct).ConfigureAwait(false));
        }

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

/// <summary>
/// The body of <c>get_project</c> (next-phase-1-design.md section 5.2): a summary that fits an agent's context. The settings, the pack
/// manifests with their diagnostics and the extension schemas come only when <c>sections</c> asks for them.
/// </summary>
/// <param name="Name">The project name.</param>
/// <param name="FormatVersion">The model format version.</param>
/// <param name="EngineVersion">The engine contract version (<see cref="Engine.EngineVersion.Value"/>), not the release.</param>
/// <param name="ProductVersion">The release running (<see cref="Engine.EngineVersion.Product"/>).</param>
/// <param name="Build">The build of the release (<see cref="Engine.EngineVersion.Build"/>).</param>
/// <param name="Workspace">What the checkout is called (<see cref="WorkspaceInfo.Workspace"/>).</param>
/// <param name="Branch">The branch the host detected, when it knows one.</param>
/// <param name="Worktree">The linked worktree's name.</param>
/// <param name="Repository">The main checkout's folder name, for a linked worktree.</param>
/// <param name="Mode">The host's mode (<c>local</c> for the CLI).</param>
/// <param name="SettingsHash">The settings file hash.</param>
/// <param name="Databases">The database summaries.</param>
/// <param name="Packs">Each pack's name, version, description and unit count.</param>
/// <param name="PackDiagnosticCounts">The diagnostics of loading the packs, counted by severity.</param>
/// <param name="Extensions">The extension schemas' names.</param>
/// <param name="Sections">The sections <c>sections</c> may ask for: always <c>extensions</c>, <c>packs</c> and <c>settings</c>.</param>
public sealed record AgentProjectInfo(string Name, int FormatVersion, string EngineVersion, string ProductVersion, string Build, string? Workspace,
    string? Branch, string? Worktree, string? Repository, string Mode, string SettingsHash, IReadOnlyList<ElementSummary> Databases,
    IReadOnlyList<AgentPackSummary> Packs, AgentDiagnosticCounts PackDiagnosticCounts, IReadOnlyList<string> Extensions, IReadOnlyList<string> Sections)
{
    /// <summary>The type keys of the custom kinds the project declares (next-phase-2-design.md section 4.2), ordinal.</summary>
    public IReadOnlyList<string> CustomKinds { get; init; } = [];

    /// <summary>With the <c>settings</c> section: the typed settings.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectSettings? Settings { get; init; }

    /// <summary>With the <c>packs</c> section: the pack manifests.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PackManifest>? PackManifests { get; init; }

    /// <summary>With the <c>packs</c> section: the diagnostics of loading the packs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Diagnostic>? PackDiagnostics { get; init; }

    /// <summary>With the <c>extensions</c> section: the extension schemas.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExtensionSchema>? ExtensionSchemas { get; init; }
}

/// <summary>A template pack in the <c>get_project</c> summary.</summary>
/// <param name="Name">The pack name.</param>
/// <param name="Version">The pack version.</param>
/// <param name="Description">What the pack generates.</param>
/// <param name="Units">The number of render units.</param>
public sealed record AgentPackSummary(string Name, string Version, string? Description, int Units);

/// <summary>Diagnostics counted by severity.</summary>
/// <param name="Errors">The errors.</param>
/// <param name="Warnings">The warnings.</param>
/// <param name="Info">The infos.</param>
public sealed record AgentDiagnosticCounts(int Errors, int Warnings, int Info);

/// <summary>
/// The body of <c>tag_usage</c> without a tag (next-phase-1-design.md section 5.2): the tags one vocabulary scope governs, in key order,
/// without their element ids; the whole list unless <c>limit</c> or <c>cursor</c> pages it.
/// </summary>
/// <param name="Package">The domain whose vocabulary it is, or <see langword="null"/> for the global one.</param>
/// <param name="Vocabulary">The vocabulary's id, or <see langword="null"/> when the scope has none.</param>
/// <param name="Strict">Whether the vocabulary is strict.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> on the last one or when not paged.</param>
public sealed record TagUsageSummary(string? Package, string? Vocabulary, bool Strict, IReadOnlyList<TagUseSummary> Tags, string? Next);

/// <summary>One tag in <see cref="TagUsageSummary"/>.</summary>
/// <param name="Tag">The tag key.</param>
/// <param name="Declared">Whether the scope's vocabulary declares it.</param>
/// <param name="Uses">Its uses in the scope (the element's own tags and its sub-elements').</param>
/// <param name="Examples">Up to five names of the elements that hold it.</param>
public sealed record TagUseSummary(string Tag, bool Declared, int Uses, IReadOnlyList<string> Examples);

/// <summary>The body of <c>tag_usage</c> with a tag: the ids of the elements that hold it, a page in id order.</summary>
/// <param name="Package">The domain whose vocabulary it is, or <see langword="null"/> for the global one.</param>
/// <param name="Tag">The tag key.</param>
/// <param name="Declared">Whether the scope's vocabulary declares it.</param>
/// <param name="Uses">Its uses in the scope.</param>
/// <param name="Elements">The element ids of this page.</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> on the last one.</param>
public sealed record TagUseElements(string? Package, string Tag, bool Declared, int Uses, IReadOnlyList<string> Elements, string? Next);
