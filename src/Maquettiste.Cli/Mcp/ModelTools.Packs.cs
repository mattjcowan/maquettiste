using System.ComponentModel;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The pack authoring tools (generation-ui.md section 5.4): a client can create a pack, add a unit, write its template, preview it on an
/// element with unsaved text, fix the diagnostics, plan and read why each unit renders. Every write takes the hash it read.
/// </summary>
internal sealed partial class ModelTools
{
    /// <summary>One pack (getPack).</summary>
    [McpServerTool(Name = "get_pack", Title = "Get pack", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One template pack: pack.json as a document with its hash (for save_pack), the parameters (default, project value, schema), every file with its hash, role (template, partial, script, type-map, manifest, other) and users, and the diagnostics (load errors, MQ6019 output paths outside every root, MQ6003 templates that do not parse).")]
    public Task<CallToolResult> GetPack([Description("The pack name (its folder under .maquettiste/templates/); required.")] string? pack = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack))
            return BadRequest("pack is required.");
        return await _generation.GetPackAsync(pack, ct).ConfigureAwait(false) is { } document ? Ok(document) : NotFound("pack", pack);
    }), ct);

    /// <summary>A pack's files (part of getPack).</summary>
    [McpServerTool(Name = "list_pack_files", Title = "Pack files", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The files of one template pack with size, hash (the expectedHash for write_pack_file and delete_pack_file), role and users (unit:<id>, companion:<id>, include:<path>).")]
    public Task<CallToolResult> ListPackFiles([Description("The pack name; required.")] string? pack = null, CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack))
            return BadRequest("pack is required.");
        return await _generation.GetPackAsync(pack, ct).ConfigureAwait(false) is { } document ? Ok(new { files = document.Files }) : NotFound("pack", pack);
    }), ct);

    /// <summary>Saves pack.json (savePack).</summary>
    [McpServerTool(Name = "save_pack", Title = "Save pack.json", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Replaces a pack's pack.json with a whole document (units, parameters, parameterSchema and every other member) if it still has expectedHash; written in canonical form. A schema failure (MQ6001, MQ6021 unknown scope) writes nothing; unit problems (MQ6019, MQ6022) are saved and reported.")]
    public Task<CallToolResult> SavePack(
        [Description("The pack name; required.")] string? pack = null,
        [Description("The whole pack.json document (the document from get_pack, edited); required.")] JsonElement? document = null,
        [Description("The hash returned by get_pack; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack))
            return BadRequest("pack is required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: read the pack with get_pack first.");
        if (DocumentBytes(document) is not { } body)
            return BadRequest("document is required: a JSON object (the whole pack.json).");
        var result = await _generation.SavePackAsync(pack, body, expectedHash, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, document);
    }), ct);

    /// <summary>Creates a pack (createPack).</summary>
    [McpServerTool(Name = "new_pack", Title = "New pack", Destructive = false, OpenWorld = false)]
    [Description("Creates .maquettiste/templates/<name>/ from 'empty' (one each-entity unit and its template), a built-in starter (sql-ddl, csharp-dapper) or a pack of this project (copied). Set packs.<name>.output in the settings to generate it.")]
    public Task<CallToolResult> NewPack(
        [Description("The new pack's name (lowercase letters, digits, single hyphens); required.")] string? name = null,
        [Description("'empty' (the default), a built-in starter, or a pack of this project.")] string? from = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(name))
            return BadRequest("name is required.");
        var starters = StarterPacks.Names.ToDictionary(n => n, n => StarterPacks.Files(n, out _), StringComparer.Ordinal);
        var result = await _generation.CreatePackAsync(name, from ?? "empty", starters, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }), ct);

    /// <summary>Reads one pack file (getPackFile).</summary>
    [McpServerTool(Name = "read_pack_file", Title = "Read pack file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The text and hash of one pack file (a template, partial, script or other file) by its pack-relative path.")]
    public Task<CallToolResult> ReadPackFile([Description("The pack name; required.")] string? pack = null,
        [Description("The pack-relative path with '/' separators; required.")] string? path = null, CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(path))
            return BadRequest("pack and path are required.");
        return await _generation.ReadPackFileAsync(pack, path, ct).ConfigureAwait(false) is { } file ? Ok(file) : NotFound("pack file", pack + "/" + path);
    }), ct);

    /// <summary>Writes one pack file (putPackFile).</summary>
    [McpServerTool(Name = "write_pack_file", Title = "Write pack file", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Writes one pack file if it still has expectedHash; expectedHash 'new' creates a file that must not exist. UTF-8 text, no NUL, at most 1 MB. A template that does not parse is saved and its MQ6003 diagnostics returned. pack.json is refused: use save_pack.")]
    public Task<CallToolResult> WritePackFile(
        [Description("The pack name; required.")] string? pack = null,
        [Description("The pack-relative path; required.")] string? path = null,
        [Description("The whole file text; required.")] string? text = null,
        [Description("The hash from read_pack_file or get_pack, or 'new' to create; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(path) || text is null)
            return BadRequest("pack, path and text are required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: the file's hash, or 'new' to create it.");
        var result = await _generation.WritePackFileAsync(pack, path, text, expectedHash == "new" ? null : expectedHash, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }), ct);

    /// <summary>Deletes one pack file (deletePackFile).</summary>
    [McpServerTool(Name = "delete_pack_file", Title = "Delete pack file", Destructive = true, OpenWorld = false)]
    [Description("Deletes one pack file if it still has expectedHash; refused (referenced) while a unit names it or a template includes it.")]
    public Task<CallToolResult> DeletePackFile(
        [Description("The pack name; required.")] string? pack = null,
        [Description("The pack-relative path; required.")] string? path = null,
        [Description("The file's hash; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(path))
            return BadRequest("pack and path are required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required.");
        var result = await _generation.DeletePackFileAsync(pack, path, expectedHash, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }), ct);

    /// <summary>Previews one unit (previewTemplate).</summary>
    [McpServerTool(Name = "preview_unit", Title = "Preview unit", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Renders one unit of a pack for one element with no writes and returns each file's output path and text, the diagnostics and the keys the render read. overlay (pack-relative path to unsaved text), unitOverride (an unsaved unit with the same id) and parameters render unsaved work; a disabled pack previews too. A render that does not finish within limits.scriptTimeoutMs x 4 fails with MQ6007.")]
    public Task<CallToolResult> PreviewUnit(
        [Description("The pack name; required.")] string? pack = null,
        [Description("The unit id; required.")] string? unit = null,
        [Description("The element id (or a resolved table key); omit for a model unit.")] string? elementId = null,
        [Description("Pack-relative path to unsaved text of templates, partials and scripts.")] Dictionary<string, string>? overlay = null,
        [Description("An unsaved unit object (as in pack.json units) used instead of the saved one; its id must equal unit.")] PackUnit? unitOverride = null,
        [Description("Effective parameter values used instead of the defaults and project values.")] Dictionary<string, JsonElement>? parameters = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(unit))
            return BadRequest("pack and unit are required.");
        try
        {
            var options = new PreviewOptions { Overlay = overlay, UnitOverride = unitOverride, Parameters = parameters };
            return Ok(await _generation.PreviewAsync(pack, unit, elementId, options, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }, ct);

    /// <summary>Why a unit renders or not (getPlanUnit, explainUnit).</summary>
    [McpServerTool(Name = "explain_unit", Title = "Explain unit", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Why a unit renders an element or not. With planId and key: that unit of a stored plan, its reason (new, forced, inputs, outputs, unchanged), causes, read keys grouped by kind and a one-sentence summary (for a skipped unit, why it did not re-render). With pack, unit and elementId (omit elementId for a model unit): the first reason that applies (pack-invalid, unknown-unit, pack-disabled, unknown-element, scope, selector, skip-hint, filter) or, when planned, the plan's reason and causes, from planId when given else from a new dry-run plan of the pack.")]
    public Task<CallToolResult> ExplainUnit(
        [Description("A plan id: with key, the unit of that plan; with pack and unit, the plan to take the reason from.")] string? planId = null,
        [Description("A unit key from get_plan's units (units true).")] string? key = null,
        [Description("The pack name, to explain any element.")] string? pack = null,
        [Description("The unit id, with pack.")] string? unit = null,
        [Description("The element id (or a resolved table key), with pack and unit.")] string? elementId = null,
        [Description("The run's pack selection; a pack outside it answers 'not-selected'.")] string[]? packs = null,
        [Description("The run's roots: all (default), committed or built; a unit writing under another root answers 'root-not-selected'.")] string? roots = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        var selection = roots switch
        {
            null or "" or "all" => RootSelection.All,
            "committed" => RootSelection.Committed,
            "built" => RootSelection.Built,
            _ => (RootSelection?)null,
        };
        if (selection is null)
            return BadRequest("roots must be all, committed or built.");
        if (planId is not null && !IsUlid(planId))
            return BadRequest("planId must be a ULID.");
        if (!string.IsNullOrEmpty(key))
        {
            if (planId is null)
                return BadRequest("key needs planId.");
            return await _generation.GetPlanUnitAsync(planId, key, ct).ConfigureAwait(false) is { } detail ? Ok(detail) : NotFound("plan unit", key);
        }

        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(unit))
            return BadRequest("Give planId and key, or pack and unit (and elementId).");
        return await _generation.ExplainAsync(pack, unit, elementId, planId, ct, packs, selection.Value).ConfigureAwait(false) is { } answer ? Ok(answer) : NotFound("pack", pack);
    }), ct);

    /// <summary>A unit's output paths (unitPaths).</summary>
    [McpServerTool(Name = "unit_paths", Title = "Unit output paths", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The output paths a unit renders over its scope (after its filter and generation.skip hints, as the planner counts them) or over the listed elements: count, the paths rendered (up to limit, default 200) with their root and whether the writer allows them, and MQ6019 (outside every root), MQ6020 (two elements on one path), MQ6005 and MQ6007. overlay, unitOverride and parameters use unsaved text. Nothing is written.")]
    public Task<CallToolResult> UnitPaths(
        [Description("The pack name; required.")] string? pack = null,
        [Description("The unit id; required.")] string? unit = null,
        [Description("Only these element ids.")] string[]? elementIds = null,
        [Description("How many elements to render (default 200, at most 2000).")] int? limit = null,
        [Description("Pack-relative path to unsaved text.")] Dictionary<string, string>? overlay = null,
        [Description("An unsaved unit with the same id.")] PackUnit? unitOverride = null,
        [Description("Unsaved parameter values.")] Dictionary<string, JsonElement>? parameters = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(unit))
            return BadRequest("pack and unit are required.");
        try
        {
            var options = new PreviewOptions { Overlay = overlay, UnitOverride = unitOverride, Parameters = parameters };
            return Ok(await _generation.PathsAsync(pack, unit, elementIds, options, limit ?? 200, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }, ct);

    /// <summary>A pack's recorded outputs (getPackOutputs).</summary>
    [McpServerTool(Name = "get_pack_outputs", Title = "Pack outputs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The files a pack's manifests record: path, unit, element, companion, root, committed or built, mode (overwrite, regions, once) and state on disk (intact, edited, missing), with when a manifest was last written.")]
    public Task<CallToolResult> GetPackOutputs([Description("The pack name; required.")] string? pack = null, CancellationToken ct = default) =>
        GuardAsync(() => PackAsync(async () =>
        {
            if (string.IsNullOrEmpty(pack))
                return BadRequest("pack is required.");
            return await _generation.GetPackOutputsAsync(pack, ct).ConfigureAwait(false) is { } outputs ? Ok(outputs) : NotFound("pack", pack);
        }), ct);

    /// <summary>Moves a pack file (movePackFile).</summary>
    [McpServerTool(Name = "move_pack_file", Title = "Move pack file", Destructive = true, OpenWorld = false)]
    [Description("Moves one pack file if it still has expectedHash; the target must not exist. updateUnits rewrites the units and scripts of pack.json that name it (canonical) and then needs expectedPackHash (the hash from get_pack). Refused while a template includes the file, or while a unit names it without updateUnits.")]
    public Task<CallToolResult> MovePackFile(
        [Description("The pack name; required.")] string? pack = null,
        [Description("The pack-relative source path; required.")] string? from = null,
        [Description("The pack-relative target path; required.")] string? to = null,
        [Description("The source file's hash; required.")] string? expectedHash = null,
        [Description("Rewrite the units that name the file.")] bool updateUnits = false,
        [Description("pack.json's hash, with updateUnits.")] string? expectedPackHash = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            return BadRequest("pack, from and to are required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required.");
        var result = await _generation.MovePackFileAsync(pack, new PackFileMove(from, to, updateUnits, expectedPackHash), expectedHash, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }), ct);

    /// <summary>Saves one pack's settings entry (savePackSettings).</summary>
    [McpServerTool(Name = "save_pack_settings", Title = "Save pack settings", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Replaces packs.<pack> of maquettiste.json (enabled, output, parameters) and nothing else, if the settings still have expectedHash (the hash of get_settings); an empty object removes the entry. Validated as a settings save (MQ6023, MQ6024 among the diagnostics).")]
    public Task<CallToolResult> SavePackSettings(
        [Description("The pack name; required.")] string? pack = null,
        [Description("The pack's settings object: enabled, output, parameters; required.")] JsonElement? settings = null,
        [Description("The settings hash; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack))
            return BadRequest("pack is required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required.");
        if (settings is not { ValueKind: JsonValueKind.Object } section)
            return BadRequest("settings must be a JSON object.");
        var result = await _generation.SavePackSettingsAsync(pack, section, expectedHash, ct).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }), ct);

    /// <summary>Completion data for a unit's templates (getTemplateContext).</summary>
    [McpServerTool(Name = "get_template_context", Title = "Template context", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What a unit's templates can use: the globals (model, element, the scope alias, pack.params.*, mapping, hints, data, unit), the members of the model and of the scope's records in snake_case, and the built-in helpers.")]
    public Task<CallToolResult> GetTemplateContext(
        [Description("The pack name; required.")] string? pack = null,
        [Description("The unit id; required.")] string? unit = null,
        CancellationToken ct = default) => GuardAsync(() => PackAsync(async () =>
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(unit))
            return BadRequest("pack and unit are required.");
        return await _generation.GetTemplateContextAsync(pack, unit, ct).ConfigureAwait(false) is { } context ? Ok(context) : NotFound("unit", pack + "/" + unit);
    }), ct);

    private async Task<CallToolResult> PackAsync(Func<Task<CallToolResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (PackPathException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
