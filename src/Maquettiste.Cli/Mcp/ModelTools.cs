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

    /// <summary>
    /// The read tools shared with the editor's assistant (<see cref="AgentTools"/>): <see cref="McpServerSetup"/> lists them beside this
    /// class's own tools, with their schemas from the one catalog, so the two cannot drift.
    /// </summary>
    public AgentTools Catalog { get; } = new(store, generation, new AgentToolsOptions
    {
        RepoRoot = repoRoot ?? throw new ArgumentNullException(nameof(repoRoot)),
        Workspace = workspace,
        Mode = "local",
        LogFailure = async (tool, failure, ct) =>
        {
            await log.WriteLineAsync($"maquettiste mcp: {tool} failed: {failure}".AsMemory(), ct).ConfigureAwait(false);
            await log.FlushAsync(ct).ConfigureAwait(false);
        },
    });

    /// <summary>The options bodies are written with: <see cref="JsonSerializerDefaults.Web"/> and nothing else, as the API's.</summary>
    public JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

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
