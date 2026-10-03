using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The tools of <c>maquettiste mcp</c> (docs/mcp.md): thin wrappers over <see cref="ModelStore"/> and <see cref="GenerationService"/>
/// with the semantics and JSON bodies of the editor API operations (docs/api/openapi.yaml), so an agent and the editor see one model.
/// </summary>
/// <remarks>
/// A tool's success is one text block holding the operation's JSON body, serialized with <see cref="JsonSerializerDefaults.Web"/> like
/// the API's. A failure is a tool error (<see cref="CallToolResult.IsError"/>) whose text is a problem object: the API's
/// <c>code</c>, <c>status</c> and <c>title</c>, plus the body the API returns with that status (diagnostics, the disk version,
/// referrers, stale units). Every read starts with a stat rescan of the model folder, so edits made behind the server (by the agent's
/// own file tools, git or the editor) are seen. Writes are recorded as <see cref="ChangeSource.Cli"/>. Cancellation of a request
/// cancels the engine call; a cancelled request gets no result, as the protocol requires.
/// </remarks>
/// <param name="store">The model store.</param>
/// <param name="generation">The generation service over <paramref name="store"/>.</param>
/// <param name="repoRoot">The repo root, for the project name when the settings have none.</param>
/// <param name="log">Where an internal failure is reported, one line each (standard error, never the protocol stream).</param>
/// <param name="workspace">The value of <c>MAQUETTISTE_WORKSPACE</c>, which names the workspace in <c>get_project</c>; blank means unset.</param>
internal sealed partial class ModelTools(ModelStore store, GenerationService generation, string repoRoot, TextWriter log, string? workspace = null)
{
    private readonly TextWriter _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly ModelStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly GenerationService _generation = generation ?? throw new ArgumentNullException(nameof(generation));
    private readonly string _repoRoot = repoRoot ?? throw new ArgumentNullException(nameof(repoRoot));
    private readonly SchemaRegistry _schemas = new();

    /// <summary>The options bodies are written with: <see cref="JsonSerializerDefaults.Web"/> and nothing else, as the API's.</summary>
    public JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>The project: name, versions, settings with their hash, databases, packs and extension schemas (getProject).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The project.</returns>
    [McpServerTool(Name = "get_project", Title = "Project", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The project: name, the release running (productVersion, build) beside the engine contract (engineVersion) and the model format, the workspace (MAQUETTISTE_WORKSPACE, else the git branch, else the worktree name) with the branch and worktree, settings and their hash, databases, template packs (with pack diagnostics) and extension schemas. Start here.")]
    public Task<CallToolResult> GetProject(CancellationToken ct) => GuardAsync(async () =>
    {
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var packs = await _generation.GetPacksAsync(ct).ConfigureAwait(false);
        var name = snapshot.Settings.Name is { Length: > 0 } n ? n : Path.GetFileName(_repoRoot);
        var where = WorkspaceInfo.Detect(_repoRoot, workspace);
        return Ok(new ProjectInfo(name, snapshot.Settings.FormatVersion, EngineVersion.Value, EngineVersion.Product, EngineVersion.Build, where.Workspace,
            where.Branch, where.Worktree, where.Repository, "local", snapshot.Settings, snapshot.SettingsHash,
            [.. snapshot.Summaries().Where(s => s.Kind == "database")], packs.Packs, packs.Diagnostics, [.. snapshot.Extensions.Select(e => e.Schema)], null));
    }, ct);

    /// <summary>The element summaries, filtered, and paged when asked (getModelIndex).</summary>
    /// <param name="kind">A kind name.</param>
    /// <param name="package">A package id or name.</param>
    /// <param name="tag">A tag.</param>
    /// <param name="category">A category.</param>
    /// <param name="stereotype">A stereotype.</param>
    /// <param name="query">A name fragment.</param>
    /// <param name="cursor">The previous page's next.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The matching summaries, in index order; with a cursor or limit, one page of them by (kind, name, id) and the next cursor.</returns>
    [McpServerTool(Name = "get_model_index", Title = "Model index", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Summaries of the top-level elements (id, kind, name, package, tags, category, stereotypes, hash, path). Every filter is optional and they combine with AND. Without cursor or limit the answer is the whole list; with either it is one page, {items, next}, ordered by kind, name and id: pass next as cursor until it is null. Use the index to find elements, get_elements to read their documents in bulk, get_resolved_model for what generation sees (resolved types, inherited attributes, tables), get_model_kinds for the counts.")]
    public Task<CallToolResult> GetModelIndex(
        [Description("Only this kind, for example entity, relation, enum, value-object, scalar-type, package, database, table, view, sequence, routine, database-type, sql-object, query, mapping, diagram.")] string? kind = null,
        [Description("Only elements directly in this package, by package id or package name.")] string? package = null,
        [Description("Only elements with this tag.")] string? tag = null,
        [Description("Only elements in this category.")] string? category = null,
        [Description("Only elements with this stereotype.")] string? stereotype = null,
        [Description("Only elements whose name contains this text, ignoring case.")] string? query = null,
        [Description("The next value of the previous page; pages the answer.")] string? cursor = null,
        [Description("The page size, 1 to 1000 (100 when only a cursor is given); pages the answer.")] int? limit = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var index = await _store.GetIndexAsync(ct).ConfigureAwait(false);
        var rows = ModelPages.Filter(index, new ElementFilter(kind, package, tag, category, stereotype, query));
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
    }, ct);

    /// <summary>One element's document and hash (getElement).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The document.</returns>
    [McpServerTool(Name = "get_element", Title = "Get element", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One element: its canonical JSON document (json), its hash (pass it as expectedHash to save or delete), its path, the typed element and its Markdown sidecar text.")]
    public Task<CallToolResult> GetElement([Description("The element id (a ULID); required.")] string? id = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var document = await _store.GetElementAsync(id, ct).ConfigureAwait(false);
        return document is null ? NotFound("element", id) : Ok(document);
    }, ct);

    /// <summary>Saves an element if its file still has the expected hash (saveElement).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="element">The whole document.</param>
    /// <param name="expectedHash">The hash the caller read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result.</returns>
    [McpServerTool(Name = "save_element", Title = "Save element", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Replaces an element with a whole document if the file still has expectedHash. A conflict writes nothing and returns the disk version (current, hash) and the submitted document; an invalid document returns the diagnostics. A rename moves the file in the same save.")]
    public Task<CallToolResult> SaveElement(
        [Description("The element id; the id in the document must be the same; required.")] string? id = null,
        [Description("The whole element document (the json from get_element, edited); required.")] JsonElement? element = null,
        [Description("The hash returned by get_element (or by the last save); required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: read the element with get_element first.");
        if (DocumentBytes(element) is not { } body)
            return BadRequest("element is required: a JSON object (the whole element document).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _store.SaveAsync(id, body, expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, element);
    }, ct);

    /// <summary>Creates an element (createElement).</summary>
    /// <param name="element">The document.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result.</returns>
    [McpServerTool(Name = "create_element", Title = "Create element", Destructive = false, OpenWorld = false)]
    [Description("Creates an element from a whole document; an id is assigned when the document has none. Returns the id, the hash and what changed, or the diagnostics.")]
    public Task<CallToolResult> CreateElement(
        [Description("The element document, for example {\"kind\":\"entity\",\"name\":\"Coupon\",\"package\":\"<package id>\",\"attributes\":[...]}. Routines, database types, SQL objects and queries belong to a database: {\"kind\":\"routine\",\"name\":\"invoice_total\",\"database\":\"<database id>\",\"body\":{\"postgresql\":\"...\"}}; a query is a JSON tree, {\"kind\":\"query\",\"name\":\"OpenInvoices\",\"database\":\"<database id>\",\"entity\":\"<entity id>\",\"from\":{\"source\":\"<table id or key>\",\"alias\":\"i\"},\"select\":[{\"attribute\":\"<attribute id>\",\"expression\":{\"column\":\"i.id\"}}]}. Use get_schema for the fields of a kind; required.")] JsonElement? element = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (DocumentBytes(element) is not { } body)
            return BadRequest("element is required: a JSON object (the whole element document).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _store.CreateAsync(body, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }, ct);

    /// <summary>Deletes an element (deleteElement), or with <paramref name="dryRun"/> says what the delete would do (getDeletePlan).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="expectedHash">The hash the caller read.</param>
    /// <param name="resolution">What to do with references.</param>
    /// <param name="dryRun">Return the delete plan and write nothing.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The delete result, or the plan.</returns>
    [McpServerTool(Name = "delete_element", Title = "Delete element", Destructive = true, OpenWorld = false)]
    [Description("Deletes an element if its file still has expectedHash. While other elements reference it, the delete is refused (code referenced, with the referrers) unless resolution says otherwise: remove-references clears optional references (a required one makes the delete invalid, with an MQ2001 diagnostic naming it); delete-dependents also resolves required references in the same change, removing the part of the referrer that needs the element (an attribute whose type is deleted, a diagram member, a key) or deleting the referrer with its own dependents (the tables, views, sequences, routines, database types, SQL objects, queries and mappings of a database; the relations, mappings and overlays of an entity; a query that reads a deleted table, view or entity, deleted whole), and removes a deleted database's databases.<name> conventions. With dryRun true nothing is written and no expectedHash is needed: the result is the plan, {ids, resolution, outcome, deletes:[{id,kind,name,path,because}], clears:[{id,kind,name,pointer,field,target,because}], removes:[{id,kind,name,pointer,what,subId,subKind,because}], refused:[{id,kind,name,pointer,why,rule}], settings:[{pointer,what}], warnings:[{message,pack,unit}]}; read it before deleting with delete-dependents.")]
    public Task<CallToolResult> DeleteElement(
        [Description("The element id; required.")] string? id = null,
        [Description("The hash returned by get_element; required unless dryRun.")] string? expectedHash = null,
        [Description("refuse (default), remove-references or delete-dependents.")] string? resolution = null,
        [Description("true: return what the delete would do (with resolution, default delete-dependents) and write nothing.")] bool? dryRun = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        DeleteResolution mode;
        switch (resolution)
        {
            case null or "":
                mode = dryRun == true ? DeleteResolution.DeleteDependents : DeleteResolution.Refuse;
                break;
            case "refuse":
                mode = DeleteResolution.Refuse;
                break;
            case "remove-references":
                mode = DeleteResolution.RemoveReferences;
                break;
            case "delete-dependents":
                mode = DeleteResolution.DeleteDependents;
                break;
            default:
                return BadRequest($"resolution must be 'refuse', 'remove-references' or 'delete-dependents', not '{resolution}'.");
        }

        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (dryRun == true)
        {
            if (await _store.GetElementAsync(id, ct).ConfigureAwait(false) is not { } document || document.Element.Id != id)
                return NotFound("element", id);
            return Ok(await _store.GetDeletePlanAsync([id], mode, ct).ConfigureAwait(false));
        }

        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: read the element with get_element first.");
        var result = await _store.DeleteAsync(id, expectedHash, mode, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }, ct);

    /// <summary>Applies creates, updates and deletes all or nothing (applyBatch).</summary>
    /// <param name="operations">The operations.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The batch result.</returns>
    [McpServerTool(Name = "apply_batch", Title = "Apply batch", Destructive = true, OpenWorld = false)]
    [Description("Applies several operations all or nothing. Each operation is {\"op\":\"create\",\"element\":{...}}, {\"op\":\"update\",\"id\":...,\"expectedHash\":...,\"element\":{...}} or {\"op\":\"delete\",\"id\":...,\"expectedHash\":...,\"resolution\":\"refuse|remove-references|delete-dependents\"} (resolution as in delete_element; default refuse). Database schemas: {\"op\":\"add-schema\",\"id\":<database>,\"name\":...}, {\"op\":\"rename-schema\",\"id\":<database>,\"schema\":<schema id>,\"name\":...}, {\"op\":\"set-default-schema\",\"id\":<database>,\"schema\":...} and {\"op\":\"remove-schema\",\"id\":<database>,\"schema\":...,\"target\":<schema id to move its tables to>,\"default\":<new default when removing the default>}; a remove that would strand tables is refused with MQ4015 listing them. Materialize (entities own their bindings; preview with preview_materialize): {\"op\":\"materialize-tables\",\"database\":<database>,\"entities\":[...],\"schema\":<optional schema id>} writes a designed table per entity with its projection's shape and binds the entity to it, {\"op\":\"materialize-entities\",\"database\":<database>,\"tables\":[<tables or views>],\"package\":<package>} writes an entity per table bound to it; an entity or table already bound is refused with MQ4055. Processes: {\"op\":\"sync-enum\",\"id\":<lifecycle process>} makes the bound enum's members the process's root-level states in order (kept members keep their ids, codes and descriptions; removing a member a default, allowed values, a seed cell or a scenario value still uses is refused with MQ9019: change those uses first), {\"op\":\"set-lifecycle\",\"id\":<entity>,\"target\":<process>} binds the entity and the process to each other (omit target to clear the lifecycle), {\"op\":\"set-initial\",\"id\":<process or compound state>,\"target\":<direct child state>}, {\"op\":\"refresh-scenario\",\"id\":<scenario>} (rewrites its expectations and outcome from the engine's replay). An element may appear in one operation only. When one fails nothing is written and the result says which and why.")]
    public Task<CallToolResult> ApplyBatch(
        [Description("The operations, applied in order; required.")] JsonElement? operations = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (operations is not { ValueKind: JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.String } ops)
            return BadRequest("operations is required: an array of operations (or a batch document with an operations array).");
        var batchJson = ops.ValueKind == JsonValueKind.String ? ops.GetString() ?? string.Empty : ops.GetRawText();
        var trimmed = batchJson.TrimStart();
        var body = trimmed.StartsWith('{') ? batchJson : "{\"operations\":" + batchJson + "}";
        var parsed = _store.ParseBatch(Encoding.UTF8.GetBytes(body));
        if (parsed.Batch is null)
            return Problem("invalid", 422, "The batch is not valid.", body: Node(parsed));
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _store.ApplyBatchAsync(parsed.Batch, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }, ct);

    /// <summary>The localization settings and completeness (getLocalizationStatus).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status.</returns>
    [McpServerTool(Name = "localization_status", Title = "Localization status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The default locale, the declared locales and, per translated locale, its fallback chain and per shard the expected, translated, missing and stale counts (as GET /api/localization).")]
    public Task<CallToolResult> LocalizationStatus(CancellationToken ct = default) => GuardAsync(async () =>
        Ok(await _store.GetLocalizationStatusAsync(ct).ConfigureAwait(false)), ct);

    /// <summary>Translation entries of one locale (getTranslations).</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="owner">An owner element id.</param>
    /// <param name="shard">A shard repo path.</param>
    /// <param name="missing">Only the entries that need work, paged.</param>
    /// <param name="cursor">The cursor of the page.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entries.</returns>
    [McpServerTool(Name = "get_translations", Title = "Get translations", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Translation entries of one locale: id, owner, field (displayName, pluralName, label, description), source text, translation, effective text, state (translated, missing, stale, fallback), shard and shardHash (pass the hashes as expected to set_translations). Filter by owner or shard; missing=true pages the entries that need work, 200 at a time, with a cursor.")]
    public Task<CallToolResult> GetTranslations(
        [Description("A declared locale other than the default, for example fr; required.")] string? locale = null,
        [Description("Only the entries of this element and its sub-elements (seed rows belong to their seed).")] string? owner = null,
        [Description("Only the entries of this shard (its repo path).")] string? shard = null,
        [Description("Only the entries that are missing, fallback or stale, paged.")] bool missing = false,
        [Description("The cursor from the previous page.")] string? cursor = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(locale))
            return BadRequest("locale is required.");
        if (!await _store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return Problem("not-found", 404, $"'{locale}' is not a declared locale other than the default.");
        return Ok(await _store.GetTranslationRowsAsync(locale, owner, shard, missing, cursor, ct).ConfigureAwait(false));
    }, ct);

    /// <summary>Writes translations of one locale (putTranslations).</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="entries">The edits.</param>
    /// <param name="expected">Shard path to the hash read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    [McpServerTool(Name = "set_translations", Title = "Set translations", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Writes, removes (value null) or confirms (confirm true: the default text changed but the translation still holds) translations of one locale in one atomic save. entries: [{\"id\",\"field\",\"value\",\"confirm\"}]; expected: {\"<shard path>\": \"<shardHash from get_translations>\"}. A changed shard is a conflict (nothing written); an unknown id or field is invalid.")]
    public Task<CallToolResult> SetTranslations(
        [Description("A declared locale other than the default; required.")] string? locale = null,
        [Description("The edits: [{id, field, value (text or null), confirm?}]; required.")] JsonElement? entries = null,
        [Description("Shard repo path to the shardHash read with get_translations.")] JsonElement? expected = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(locale))
            return BadRequest("locale is required.");
        if (entries is not { ValueKind: JsonValueKind.Array } list)
            return BadRequest("entries is required: an array of {id, field, value, confirm?}.");
        if (!await _store.IsTranslatedLocaleAsync(locale, ct).ConfigureAwait(false))
            return Problem("not-found", 404, $"'{locale}' is not a declared locale other than the default.");
        var edits = new List<TranslationEdit>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var id) || !entry.TryGetProperty("field", out var field)
                || id.ValueKind != JsonValueKind.String || field.ValueKind != JsonValueKind.String)
                return BadRequest("Each entry needs an id and a field.");
            var value = entry.TryGetProperty("value", out var v) ? v : default;
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined))
                return BadRequest($"The value of {id.GetString()}/{field.GetString()} must be a string or null.");
            var confirm = entry.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.True;
            edits.Add(new TranslationEdit(id.GetString()!, field.GetString()!, value.ValueKind == JsonValueKind.String ? value.GetString() : null, confirm));
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (expected is { ValueKind: JsonValueKind.Object } map)
        {
            foreach (var property in map.EnumerateObject())
                hashes[property.Name] = property.Value.GetString() ?? "";
        }

        var result = await _store.SaveTranslationsAsync(locale, edits, hashes, ChangeSource.Cli, ct).ConfigureAwait(false);
        var body = new { outcome = result.Outcome switch { SaveOutcome.Saved => "saved", SaveOutcome.Conflict => "conflict", _ => "invalid" }, hashes = result.ShardHashes, diagnostics = result.Diagnostics };
        return result.Outcome switch
        {
            SaveOutcome.Saved => Ok(body),
            SaveOutcome.Conflict => Problem("conflict", 409, "A shard changed since it was read; nothing was written. hashes holds the current hashes.", body: Node(body)),
            _ => Problem("invalid", 422, "The translations are not valid; nothing was written. See diagnostics.", body: Node(body)),
        };
    }, ct);

    /// <summary>A seed's rows as CSV (exportSeedCsv).</summary>
    /// <param name="id">The seed id.</param>
    /// <param name="bom">Byte order mark and CRLF.</param>
    /// <param name="locales">Locales whose label and description columns are added.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The CSV text.</returns>
    [McpServerTool(Name = "export_seed_csv", Title = "Export seed CSV", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A seed's rows as CSV text: @id, then @code, @label, @description for a reference type, then attribute names and end roles, then @label:<locale> and @description:<locale> for each requested locale.")]
    public Task<CallToolResult> ExportSeedCsv(
        [Description("The seed id; required.")] string? id = null,
        [Description("Adds a byte order mark and CRLF line ends, for spreadsheet programs.")] bool bom = false,
        [Description("Translated locales whose label and description columns are added.")] string[]? locales = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        var text = await _store.ExportSeedCsvAsync(id, bom, locales ?? [], ct).ConfigureAwait(false);
        return text is null ? NotFound("seed", id) : Text(text, isError: false);
    }, ct);

    /// <summary>Previews or applies a CSV import into a seed (importSeedCsv).</summary>
    /// <param name="id">The seed id.</param>
    /// <param name="csv">The CSV text.</param>
    /// <param name="mode">merge or replace.</param>
    /// <param name="apply">Applies instead of previewing.</param>
    /// <param name="expectedHash">The seed hash read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The preview.</returns>
    [McpServerTool(Name = "import_seed_csv", Title = "Import seed CSV", Destructive = true, OpenWorld = false)]
    [Description("Imports CSV rows into a seed: rows match by @id, else by @code for a reference type, else are new. mode merge (default) updates and adds; replace also removes rows the file leaves out, except rows other seeds reference (blocked). A dry run (the default) returns the preview (added, changed with before and after, removed, blocked, ignoredHeaders); apply true writes it with expectedHash.")]
    public Task<CallToolResult> ImportSeedCsv(
        [Description("The seed id; required.")] string? id = null,
        [Description("The CSV text, with a header line; required.")] string? csv = null,
        [Description("merge (default) or replace.")] string? mode = null,
        [Description("true writes the import; false or absent only previews it.")] bool apply = false,
        [Description("The seed's hash from get_element; a changed seed is a conflict.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        if (csv is null)
            return BadRequest("csv is required.");
        if (mode is not (null or "" or "merge" or "replace"))
            return BadRequest($"mode must be merge or replace, not '{mode}'.");
        ImportResult? result;
        try
        {
            result = await _store.ImportSeedCsvAsync(id, csv, mode == "replace", !apply, expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }

        return result is null ? NotFound("seed", id) : FromOutcome(result.Outcome, result.Preview, null);
    }, ct);

    /// <summary>Creates a reference type's seed with the code, label and description columns.</summary>
    /// <param name="type">The reference type id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result.</returns>
    [McpServerTool(Name = "create_seed", Title = "Create seed", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Creates the seed of a reference type that has none, named after the type, with the columns code, label and description and no rows (then import_seed_csv or save_element adds rows). Returns the seed id; a type that has a seed returns that seed's id and changes nothing. A seed created with create_element for a reference type without columns gets the same three.")]
    public Task<CallToolResult> CreateSeed([Description("The reference type id; required.")] string? type = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(type))
            return BadRequest("type is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _store.CreateSeedAsync(type, ChangeSource.Cli, ct).ConfigureAwait(false);
        return result is null ? NotFound("reference type", type) : FromOutcome(result.Outcome, result, null);
    }, ct);

    /// <summary>The attributes typed by a reference type (getReferenceTypeUsage).</summary>
    /// <param name="id">The reference type id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The usages.</returns>
    [McpServerTool(Name = "reference_type_usage", Title = "Reference type usage", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every attribute typed by a reference type: attribute, owner, domain (the owner's package), collection, required and the effective storage choice per database.")]
    public Task<CallToolResult> ReferenceTypeUsage([Description("The reference type id; required.")] string? id = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        var usage = await _store.GetReferenceTypeUsageAsync(id, ct).ConfigureAwait(false);
        return usage is null ? NotFound("reference type", id) : Ok(usage);
    }, ct);

    /// <summary>Where an element is used (getReferences).</summary>
    /// <param name="id">The element id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The references.</returns>
    [McpServerTool(Name = "get_references", Title = "Where used", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every reference to an element: the referring element id, the referring (sub-)element id, the JSON pointer and field in the referring document, and the referenced id.")]
    public Task<CallToolResult> GetReferences([Description("The element id; required.")] string? id = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (await _store.GetElementAsync(id, ct).ConfigureAwait(false) is null)
            return NotFound("element", id);
        return Ok(await _store.GetReferencesAsync(id, ct).ConfigureAwait(false));
    }, ct);

    /// <summary>The built-in rule catalog (listValidationRules).</summary>
    /// <returns>Every rule with its default severity, description, family and whether it can be turned off.</returns>
    [McpServerTool(Name = "list_validation_rules", Title = "Validation rules", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The built-in validation rules, ordered by id: id, defaultSeverity, description, family (the hundreds group such as MQ72xx) and familyLabel, and canBeOff (false for MQ1xxx). Override a rule's severity with validation.rules in the settings (get_settings, save_settings): error, warning, info or off.")]
    public CallToolResult ListValidationRules() => Ok(RuleCatalog.Describe());

    /// <summary>Validates the model or a scope (validate).</summary>
    /// <param name="elementIds">The scope.</param>
    /// <param name="includeReferrers">Whether to include referrers.</param>
    /// <param name="includeScriptRules">Whether to run JavaScript rules.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report.</returns>
    [McpServerTool(Name = "validate", Title = "Validate", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Validates the whole model, or only some elements, with the built-in rules and, by default, the project's script rules (extensions/rules/*.js, findings x/<id>; a rule file that does not load is MQ5002 on that file). Returns the diagnostics (rule id such as MQ3001, severity, message, element id, file path, JSON pointer, line and column) and the error, warning and info counts.")]
    public Task<CallToolResult> Validate(
        [Description("Only these element ids (and, by default, the elements that reference them); omit for the whole model.")] string[]? elementIds = null,
        [Description("Whether to include the elements that reference the scope (default true).")] bool includeReferrers = true,
        [Description("Whether to run the project's JavaScript rules (default true).")] bool includeScriptRules = true,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (elementIds is not null && elementIds.Any(id => !IsUlid(id)))
            return BadRequest("elementIds must be element ids (uppercase ULIDs).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var scope = new ValidationScope(elementIds, includeReferrers, includeScriptRules);
        return Ok(await _store.ValidateAsync(scope, ct).ConfigureAwait(false));
    }, ct);

    /// <summary>A database's physical view (getDatabaseView).</summary>
    /// <param name="id">The database id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The view.</returns>
    [McpServerTool(Name = "get_database_view", Title = "Database view", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The resolved physical view of one database: tables (from the entities mapped to it, by its byConvention setting or by mapping elements, their relations and overlays) with columns (dbTypeId when a column uses a database type), keys, indexes and foreign keys, views, sequences, routines (parameters and result with native types), database types (nativeName, isCreated), SQL objects (phase, dependsOn) and queries (parameters, sources, the select list with types, the trees as written, collections with their keys, and the SQL for the database's dialect), as generation sees them. Each table, view, sequence, routine, database type, SQL object and query carries its own file's annotations (displayName, pluralName, description, stereotypes, tags, category, properties, generation); a synthesized table without an overlay has none.")]
    public Task<CallToolResult> GetDatabaseView([Description("The database element id; required.")] string? id = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var document = await _store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return NotFound("database", id);
        if (document.Element.Id != id || document.Element.KindName != "database")
            return NotFound("database", id, "not-a-database");
        return Ok(await _generation.GetDatabaseViewAsync(id, ct).ConfigureAwait(false));
    }, ct);

    /// <summary>The SQL of one query for a dialect (the editor's SQL preview).</summary>
    /// <param name="id">The query id.</param>
    /// <param name="dialect">The dialect, or null for the query's database's.</param>
    /// <param name="placeholder">The placeholder style.</param>
    /// <param name="lists">How list parameters are written.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The statements and diagnostics.</returns>
    [McpServerTool(Name = "preview_query_sql", Title = "Preview query SQL", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The SQL a query renders to, for its database's dialect or another: {preview: {id, name, database, dialect, sql, parameters, collections: [{name, sql, parameters, keys}]}, diagnostics}. sql uses @name placeholders (placeholder : or $ for :name or $1, $2...); a list parameter is written IN @name, for a data access library that expands lists (lists any writes = ANY(@name) on PostgreSQL). Each collection is a second statement run once for all parent rows: it takes the parent rows' key values as the list parameter mq_keys0 (mq_keys1...) and returns each row's key as mq_key0, which matches the parent row's keys[i].parentField. preview is null when the model has errors (they are in diagnostics); an sql expression without a text for the dialect is MQ4029.")]
    public Task<CallToolResult> PreviewQuerySql(
        [Description("The query element id; required.")] string? id = null,
        [Description("postgresql, sqlserver, mysql, sqlite or oracle; omit for the query's database's dialect.")] string? dialect = null,
        [Description("The placeholder style: @ (default), : or $.")] string? placeholder = null,
        [Description("How list parameters are written: expand (default) or any.")] string? lists = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
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
    }, ct);

    /// <summary>The template packs (part of getProject).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The packs and their diagnostics.</returns>
    [McpServerTool(Name = "list_packs", Title = "Template packs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The template packs under .maquettiste/templates/ with their manifests (units, output roots, options) and the diagnostics of loading them.")]
    public Task<CallToolResult> ListPacks(CancellationToken ct = default) => GuardAsync(async () =>
    {
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return Ok(await _generation.GetPacksAsync(ct).ConfigureAwait(false));
    }, ct);

    /// <summary>Reads <c>maquettiste.json</c> (getSettings).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The settings document.</returns>
    [McpServerTool(Name = "get_settings", Title = "Project settings", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The project settings (.maquettiste/maquettiste.json): the typed settings, the canonical JSON (json) and its hash for save_settings.")]
    public Task<CallToolResult> GetSettings(CancellationToken ct = default) => GuardAsync(async () =>
        Ok(await _store.GetSettingsAsync(ct).ConfigureAwait(false)), ct);

    /// <summary>Saves <c>maquettiste.json</c> (saveSettings).</summary>
    /// <param name="settings">The whole settings document.</param>
    /// <param name="expectedHash">The hash the caller read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The save result.</returns>
    [McpServerTool(Name = "save_settings", Title = "Save project settings", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Replaces the project settings with a whole document if the file still has expectedHash. A conflict writes nothing and returns the disk version; an invalid document returns the diagnostics.")]
    public Task<CallToolResult> SaveSettings(
        [Description("The whole settings document (the json from get_settings, edited); required.")] JsonElement? settings = null,
        [Description("The hash returned by get_settings; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: read the settings with get_settings first.");
        if (DocumentBytes(settings) is not { } body)
            return BadRequest("settings is required: a JSON object (the whole maquettiste.json document).");
        var result = await _store.SaveSettingsAsync(body, expectedHash, ChangeSource.Cli, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, settings);
    }, ct);

    /// <summary>Rewrites model files in canonical form (formatModel).</summary>
    /// <param name="paths">Repo-relative model files; absent for every one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The files rewritten and skipped.</returns>
    [McpServerTool(Name = "format_model", Title = "Rewrite model files in canonical form", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rewrites model files in canonical form, as maquettiste format does, so their MQ1003 warnings go away (maquettiste.json also loses the retired commit flag, MQ1010). Content does not change; a file already canonical is untouched and a file that does not pass its schema is listed in skipped. A path that is not a model file is refused and nothing is written.")]
    public Task<CallToolResult> FormatModel(
        [Description("Repo-relative model files (.maquettiste/maquettiste.json, .maquettiste/model/...); default: every model file.")] string[]? paths = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        var result = await _store.FormatAsync(paths, ChangeSource.Cli, ct).ConfigureAwait(false);
        return result.Refused.Count > 0
            ? BadRequest("Only model files can be formatted (maquettiste.json, model/**/*.json): " + string.Join(", ", result.Refused) + ".")
            : Ok(result);
    }, ct);

    /// <summary>Plans generation (startPlan, run to completion).</summary>
    /// <param name="packs">Pack names.</param>
    /// <param name="force">Whether to ignore the unit cache.</param>
    /// <param name="handEdits">A hand-edit policy.</param>
    /// <param name="jobs">Parallelism.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan result, without the per-unit list.</returns>
    [McpServerTool(Name = "plan", Title = "Plan generation", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Plans generation without touching the repository: renders what changed and stores the plan. Returns the outcome and the plan (id, packs, file changes with their actions, counts of every kind, units rendered and skipped by reason, diagnostics). Files identical to the disk (unchanged) or not rendered because their inputs did not change (not-rendered) are only counted; get_plan lists every file. Read diffs with get_plan_diff, then apply with apply_plan.")]
    public Task<CallToolResult> Plan(
        [Description("Only these packs (default: every enabled pack).")] string[]? packs = null,
        [Description("Render every unit, ignoring the unit cache.")] bool force = false,
        [Description("fail, overwrite or skip: overrides the project's hand-edit policy.")] string? handEdits = null,
        [Description("Parallelism for this run (at least 1).")] int? jobs = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (jobs is < 1)
            return BadRequest("jobs must be at least 1.");
        HandEditPolicy? policy;
        switch (handEdits)
        {
            case null or "":
                policy = null;
                break;
            case "fail":
                policy = HandEditPolicy.Fail;
                break;
            case "overwrite":
                policy = HandEditPolicy.Overwrite;
                break;
            case "skip":
                policy = HandEditPolicy.Skip;
                break;
            default:
                return BadRequest($"handEdits must be fail, overwrite or skip, not '{handEdits}'.");
        }

        var request = new GenerationRequest
        {
            Mode = GenerationMode.Apply,
            Packs = packs is { Length: > 0 } ? packs : null,
            Force = force,
            Jobs = jobs,
            HandEdits = policy,
            IncludeDiffs = false,
            StageBarriers = false,
            Lock = LockMode.Wait,
        };
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _generation.PlanAsync(request, null, ct).ConfigureAwait(false);
        // Files the apply leaves as they are because they are identical or were not rendered are only counted (counts), so the
        // answer stays small on a large model; get_plan lists every file.
        var summary = result with
        {
            Plan = result.Plan is null ? null : result.Plan with
            {
                Units = [],
                Changes = [.. result.Plan.Changes.Where(c => c.Kind is not (FileChangeKind.Unchanged or FileChangeKind.NotRendered))],
            },
        };
        if (summary.Plan is null)
            return FromRun(summary.Outcome, summary, ct);
        return Ok(summary);
    }, ct);

    /// <summary>A stored plan (getPlan).</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="units">Whether to include the per-unit list.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    [McpServerTool(Name = "get_plan", Title = "Get plan", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A stored plan: its request, packs, every file with what apply does to it (added, modified, deleted; unchanged, not-rendered and kept are left alone; hand-edited, conflict, orphaned-owned), counts by kind, units by reason, and diagnostics (and, with units, every planned unit and output).")]
    public Task<CallToolResult> GetPlan(
        [Description("The plan id returned by plan; required.")] string? planId = null,
        [Description("Include the per-unit list (large).")] bool units = false,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(planId))
            return BadRequest("planId is required.");
        var plan = IsUlid(planId) ? await _generation.GetPlanAsync(planId, ct).ConfigureAwait(false) : null;
        if (plan is null)
            return NotFound("plan", planId);
        return Ok(units ? plan : plan with { Units = [] });
    }, ct);

    /// <summary>The unified diff of one file in a stored plan (getPlanDiff).</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The diff text.</returns>
    [McpServerTool(Name = "get_plan_diff", Title = "Plan diff", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The unified diff of one file of a stored plan (the path as it appears in the plan's changes), without rendering again.")]
    public Task<CallToolResult> GetPlanDiff(
        [Description("The plan id returned by plan; required.")] string? planId = null,
        [Description("The repo-relative path of a file in the plan's changes, with / separators; required.")] string? path = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(planId))
            return BadRequest("planId is required.");
        if (string.IsNullOrEmpty(path))
            return BadRequest("path is required.");
        if (!IsUlid(planId))
            return NotFound("plan", planId);
        var diff = await _generation.GetPlanDiffAsync(planId, path, ct).ConfigureAwait(false);
        if (diff is null)
            return Problem("not-found", 404, $"Plan {planId} has no file {path}.");
        return new CallToolResult { Content = [new TextContentBlock { Text = diff }] };
    }, ct);

    /// <summary>Applies a stored plan (startApply, run to completion).</summary>
    /// <param name="planId">The plan id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The apply result.</returns>
    [McpServerTool(Name = "apply_plan", Title = "Apply plan", Destructive = true, OpenWorld = false)]
    [Description("Writes a stored plan's files without rendering again. Refused with code stale, writing nothing, when the model, the templates or a planned file changed since the plan (plan again). Returns the files written and deleted.")]
    public Task<CallToolResult> ApplyPlan([Description("The plan id returned by plan; required.")] string? planId = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (!IsUlid(planId))
            return BadRequest("planId must be a plan id (an uppercase ULID).");
        if (await _generation.GetPlanAsync(planId, ct).ConfigureAwait(false) is null)
            return NotFound("plan", planId);
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var result = await _generation.ApplyAsync(planId, null, ct).ConfigureAwait(false);
        return result.Outcome == RunOutcome.Succeeded ? Ok(result) : FromRun(result.Outcome, result, ct);
    }, ct);

    /// <summary>The JSON schema of a kind or document.</summary>
    /// <param name="kind">The kind or document name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The schema and the schemas it references.</returns>
    [McpServerTool(Name = "get_schema", Title = "JSON schema", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The JSON schema (draft 2020-12) of an element kind or document, with the schema files it references (common.json) and, for an element kind, the project's extension schemas that apply to it (extensions: each constrains the element's properties object), so documents built for create_element, save_element, apply_batch or save_settings are valid.")]
    public Task<CallToolResult> GetSchema(
        [Description("An element kind (entity, relation, enum, value-object, scalar-type, package, database, table, view, sequence, routine, database-type, sql-object, query, mapping, diagram, stereotype, tag-vocabulary, category-tree) or a document (maquettiste for the settings, batch, pack, extension); required.")] string? kind = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
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
    }, ct);

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

    private CallToolResult Ok<T>(T value) => Text(JsonSerializer.Serialize(value, JsonOptions), isError: false);

    private static CallToolResult Text(string text, bool isError) => new() { Content = [new TextContentBlock { Text = text }], IsError = isError ? true : null };

    private JsonObject? Node<T>(T value) => JsonSerializer.SerializeToNode(value, JsonOptions) as JsonObject;

    /// <summary>A problem: the API's code, status and title, an optional detail, and the fields of the API's body for that status.</summary>
    private CallToolResult Problem(string code, int status, string title, string? detail = null, JsonObject? body = null)
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

    /// <summary>The <c>bad-request</c> problem for an argument the server's binder would reject (see <see cref="McpServerSetup.ArgumentProblem"/>).</summary>
    /// <param name="detail">Which argument and why.</param>
    /// <returns>The tool error.</returns>
    internal CallToolResult ArgumentError(string detail) => BadRequest(detail);

    private CallToolResult BadRequest(string detail) => Problem("bad-request", 400, "The request is not valid.", detail);

    private CallToolResult NotFound(string what, string? id, string code = "not-found") => Problem(code, 404, $"No {what} has the id {id}.");

    /// <summary>A save, create, delete or batch result: the body when saved, else the problem for the outcome with the body's fields.</summary>
    private CallToolResult FromOutcome<T>(SaveOutcome outcome, T result, JsonElement? submitted)
    {
        if (outcome == SaveOutcome.Saved)
            return Ok(result);
        var body = Node(result);
        if (outcome == SaveOutcome.Conflict && submitted is { } mine && body is not null)
            body["submitted"] = JsonNode.Parse(mine.ValueKind == JsonValueKind.String ? mine.GetString() ?? "null" : mine.GetRawText());
        return outcome switch
        {
            SaveOutcome.Conflict => Problem("conflict", 409, "The file changed on disk since it was read; nothing was written. current and hash are the disk version; submitted is yours.", body: body),
            SaveOutcome.Invalid => Problem("invalid", 422, "The change is not valid; nothing was written. See diagnostics.", body: body),
            SaveOutcome.NotFound => Problem("not-found", 404, "No such element.", body: body),
            SaveOutcome.Referenced => Problem("referenced", 409, "Other elements reference this element; nothing was deleted. See referrers, or pass resolution remove-references or delete-dependents (delete_element with dryRun shows what each does).", body: body),
            _ => Problem("internal", 500, "Unexpected outcome.", body: body),
        };
    }

    /// <summary>A plan or apply that did not succeed: the problem for the run outcome with the result's fields.</summary>
    private CallToolResult FromRun<T>(RunOutcome outcome, T result, CancellationToken ct)
    {
        if (outcome == RunOutcome.Cancelled)
            ct.ThrowIfCancellationRequested();
        var body = Node(result);
        return outcome switch
        {
            RunOutcome.Stale => Problem("stale", 409, "The plan is stale: its inputs or planned files changed since it was made; nothing was written. Plan again.", body: body),
            RunOutcome.Invalid => Problem("invalid", 422, "The model or the templates have errors; nothing was written. See diagnostics.", body: body),
            RunOutcome.Conflicts => Problem("conflicts", 409, "Generated files were edited by hand; see the changes and the hand-edit policy.", body: body),
            RunOutcome.Drift => Problem("drift", 409, "Generated files are stale, missing or orphaned.", body: body),
            RunOutcome.Busy => Problem("busy", 503, "Another generation run holds the run lock.", body: body),
            RunOutcome.Cancelled => Problem("cancelled", 409, "The run was cancelled.", body: body),
            _ => Problem("failed", 500, "The run failed.", body: body),
        };
    }

    /// <summary>Runs a tool body and turns an exception into a problem; a cancelled request stays cancelled.</summary>
    /// <remarks>
    /// Only the tool's own argument checks answer <c>bad-request</c>; an exception from the engine is the server's failure, so it
    /// is <c>internal</c> and one line goes to the log (as the API's handler logs it).
    /// </remarks>
    private async Task<CallToolResult> GuardAsync(Func<Task<CallToolResult>> body, CancellationToken ct, [CallerMemberName] string tool = "")
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
#pragma warning disable CA1031 // A tool never crashes the server: any other failure is the internal problem with its message.
        catch (Exception e)
#pragma warning restore CA1031
        {
            var name = typeof(ModelTools).GetMethod(tool)?.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? tool;
            await _log.WriteLineAsync($"maquettiste mcp: {name} failed: {e.GetType().FullName}: {e.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            await _log.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return Problem("internal", 500, "The server failed to answer this request.", e.Message);
        }
    }

    /// <summary>The UTF-8 bytes of a document argument: a JSON object, or a string holding one.</summary>
    private static byte[]? DocumentBytes(JsonElement? document)
    {
        var text = document?.ValueKind switch
        {
            JsonValueKind.Object => document.Value.GetRawText(),
            JsonValueKind.String => document.Value.GetString(),
            _ => null,
        };
        return text is null || !text.TrimStart().StartsWith('{') ? null : Encoding.UTF8.GetBytes(text);
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
}

/// <summary>The body of <c>get_project</c>: the editor API's <c>ProjectInfo</c> (getProject).</summary>
/// <param name="Name">The project name.</param>
/// <param name="FormatVersion">The model format version.</param>
/// <param name="EngineVersion">The engine contract version (<see cref="Engine.EngineVersion.Value"/>), not the release.</param>
/// <param name="ProductVersion">The release running (<see cref="Engine.EngineVersion.Product"/>).</param>
/// <param name="Build">The build of the release (<see cref="Engine.EngineVersion.Build"/>).</param>
/// <param name="Workspace">What the checkout is called (<see cref="WorkspaceInfo.Workspace"/>).</param>
/// <param name="Branch">The git branch, when it can be read.</param>
/// <param name="Worktree">The linked worktree's name.</param>
/// <param name="Repository">The main checkout's folder name, for a linked worktree.</param>
/// <param name="Mode">Always <c>local</c>.</param>
/// <param name="Settings">The typed settings.</param>
/// <param name="SettingsHash">The settings file hash.</param>
/// <param name="Databases">The database summaries.</param>
/// <param name="Packs">The pack manifests.</param>
/// <param name="PackDiagnostics">The diagnostics of loading the packs.</param>
/// <param name="Extensions">The extension schemas.</param>
/// <param name="Git">Always <see langword="null"/> (the MCP server does not read git).</param>
internal sealed record ProjectInfo(string Name, int FormatVersion, string EngineVersion, string ProductVersion, string Build, string? Workspace,
    string? Branch, string? Worktree, string? Repository, string Mode, ProjectSettings Settings, string SettingsHash,
    IReadOnlyList<ElementSummary> Databases, IReadOnlyList<PackManifest> Packs, IReadOnlyList<Diagnostic> PackDiagnostics,
    IReadOnlyList<ExtensionSchema> Extensions, object? Git);
