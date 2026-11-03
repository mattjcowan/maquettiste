using System.ComponentModel;
using Maquettiste.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The extension file tools, mirroring the pack file tools: the model's custom property schemas (<c>extensions/&lt;name&gt;.json</c>),
/// script rules (<c>extensions/rules/&lt;name&gt;.js</c>), rule declaration files (<c>extensions/rules/&lt;name&gt;.json</c>) and seeds' rows scripts (<c>extensions/seeds/&lt;name&gt;.js</c>,
/// next-phase-2-design.md section 3.4), read and written in place through the engine's write guard. Every write takes the hash it read.
/// A rule's findings (<c>x/&lt;id&gt;</c>) come from <c>validate</c>, which runs script rules by default; a rows script's rows come from
/// <c>preview_seed_rows</c>.
/// </summary>
internal sealed partial class ModelTools
{
    /// <summary>The extension files (listExtensionFiles).</summary>
    [McpServerTool(Name = "list_extension_files", Title = "Extension files", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The files of .maquettiste/extensions/: custom property schemas (<name>.json, kind schema), script rules (rules/<name>.js, kind rule), rule declaration files (rules/<name>.json, kind declared-rules), seeds' rows scripts (seeds/<name>.js, kind seed-rows), custom kinds (kinds/<type>.json, kind custom-kind) and calculation files (calculated/<name>.js, kind calculated), each with size, hash (the expectedHash for write_extension_file, move_extension_file and delete_extension_file) and what is wrong with it on its own: MQ5004 for an invalid schema, MQ5002 or MQ5003 for a rule script or calculation file that does not load (a syntax error, a rule without an id, a malformed maquettiste.calculated call), with line and column, MQ7127 for a rows script that does not load or does not call maquettiste.rows exactly once, MQ5005 (a warning) for a script that declares let or var at its top level, MQ1001 or MQ1002 for a custom kind file that does not parse or pass custom-kind.json and its own findings (MQ1017, MQ1018, MQ2009, MQ7212), MQ5014 for a rule declaration file that does not fit get_schema's declared-rules schema and MQ5015 or MQ5016 for its rules (a repeated id, a kind, stereotype, package, path, link kind, bounds or placeholder that does not exist or apply), with pointers. Listing never runs a rows script. A rule script lists its rules: id, severity, wholeModel (true for a rule registered with scope \"model\", which runs once per whole-model validate) and resolved (true for a rule that runs on the resolved model: one registered with scope \"resolved\", which runs on each resolved object of its kinds after resolution, and every declared rule). A rule declaration file lists the rules it declares, each with resolved true. A calculation file lists its calculations: name, kinds, schema and description of each calculated value it declares; listing never evaluates one.")]
    public Task<CallToolResult> ListExtensionFiles(CancellationToken ct = default) => GuardAsync(async () =>
        Ok(await _store.ListExtensionFilesAsync(ct).ConfigureAwait(false)), ct);

    /// <summary>Reads one extension file (getExtensionFile).</summary>
    [McpServerTool(Name = "read_extension_file", Title = "Read extension file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The text and hash of one extension file by its path under extensions/: <name>.json (a custom property schema), rules/<name>.js (a script rule), rules/<name>.json (a rule declaration file), seeds/<name>.js (a seed's rows script), kinds/<type>.json (a custom kind) or calculated/<name>.js (a calculation file).")]
    public Task<CallToolResult> ReadExtensionFile([Description("The path under extensions/, such as retention.json, rules/naming.js, seeds/audited-records.js or kinds/setting.json; required.")] string? path = null,
        CancellationToken ct = default) => GuardAsync(() => ExtensionAsync(async () =>
    {
        if (string.IsNullOrEmpty(path))
            return BadRequest("path is required.");
        return await _store.ReadExtensionFileAsync(path, ct).ConfigureAwait(false) is { } file ? Ok(file) : NotFound("extension file", path);
    }), ct);

    /// <summary>Writes one extension file (putExtensionFile).</summary>
    [McpServerTool(Name = "write_extension_file", Title = "Write extension file", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Writes one extension file if it still has expectedHash; expectedHash 'new' creates a file that must not exist. UTF-8 text, no NUL, at most 1 MB. A schema (<name>.json: name, description, appliesTo { kinds, stereotypes }, properties, required) must pass get_schema's extension schema, else invalid with MQ5004 and nothing written; it is written in canonical form and text returns what was written. A script rule (rules/<name>.js, registering maquettiste.rule({ id, severity, kinds, check(element, model, report) })) is written as sent and loaded alone in the sandbox: a syntax error or a rule without an id comes back in diagnostics (MQ5002 with line and column), as does the warning MQ5005 for a top-level let or var. Run validate for the rule's x/<id> findings. A rule declaration file (rules/<name>.json: rules, each with id, severity, description, select { kind, stereotype, tag, property, package } and require, one condition of all, any, not, has, equals, count, references or referencedBy, and a message with {path} placeholders) must be JSON; it is written in canonical form, text returns what was written, and it is checked alone: MQ5014 when it does not fit get_schema's declared-rules schema, MQ5015 for an id another declaration or a rule script already has, MQ5016 for what does not exist or apply, with pointers; they never refuse the write. Its rules run on the resolved model: run validate for their x/<id> findings. A rows script (seeds/<name>.js, calling maquettiste.rows((model, seed) => [ { <column name>: value, ... } ]) exactly once; a seed names it with rowsFrom: { script: \"seeds/<name>.js\" }) is written as sent and loaded alone, never run: a script that does not load or does not register its rows exactly once comes back as MQ7127 with line and column, and MQ5005 for a top-level let or var. Read its rows with preview_seed_rows. A calculation file (calculated/<name>.js, calling maquettiste.calculated({ name, kinds, schema, description, value(element, model) }) and nothing else) is written as sent and loaded alone, never evaluated: a syntax error or a malformed declaration comes back as MQ5002 (MQ5003 for a limit) with line and column, and MQ5005 for a top-level let or var. Read its values with get_calculated. A custom kind (kinds/<type>.json: type equal to the file name, displayName, pluralName, description, properties with x-ref, x-translatable and x-member, required) must parse and pass get_schema's custom-kind schema, else invalid with MQ1001 or MQ1002 and nothing written; it is written in canonical form, and diagnostics carry its own findings (MQ1017, MQ1018, MQ2009, MQ7212) and the MQ3033 findings of the elements of its type (at most 50, then a line with the count), which never refuse the write.")]
    public Task<CallToolResult> WriteExtensionFile(
        [Description("The path under extensions/; required.")] string? path = null,
        [Description("The whole file text; required.")] string? text = null,
        [Description("The hash from read_extension_file or list_extension_files, or 'new' to create; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(() => ExtensionAsync(async () =>
    {
        if (string.IsNullOrEmpty(path) || text is null)
            return BadRequest("path and text are required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required: the file's hash, or 'new' to create it.");
        var result = await _store.WriteExtensionFileAsync(path, text, expectedHash == "new" ? null : expectedHash, ct, ChangeSource.Cli).ConfigureAwait(false);
        return FromOutcome(result.Outcome, result, null);
    }), ct);

    /// <summary>Deletes one extension file (deleteExtensionFile).</summary>
    [McpServerTool(Name = "delete_extension_file", Title = "Delete extension file", Destructive = true, OpenWorld = false)]
    [Description("Deletes one extension file if it still has expectedHash. A deleted schema's custom properties stay in the elements' properties, unchecked; a deleted rule stops running; a deleted calculation file's values are gone, so what reads them fails with MQ5012; a seed whose rows script is deleted reports it missing (MQ7116). A custom kind file that custom elements still use is refused: invalid, with one MQ1019 per element that would lose its kind (at most 50, then a line with the count).")]
    public Task<CallToolResult> DeleteExtensionFile(
        [Description("The path under extensions/; required.")] string? path = null,
        [Description("The file's hash; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(() => ExtensionAsync(async () =>
    {
        if (string.IsNullOrEmpty(path))
            return BadRequest("path is required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required.");
        var result = await _store.DeleteExtensionFileAsync(path, expectedHash, ct, ChangeSource.Cli).ConfigureAwait(false);
        return result.Outcome == SaveOutcome.NotFound ? NotFound("extension file", path) : FromOutcome(result.Outcome, result, null);
    }), ct);

    /// <summary>Renames one extension file (moveExtensionFile).</summary>
    [McpServerTool(Name = "move_extension_file", Title = "Move extension file", Destructive = true, OpenWorld = false)]
    [Description("Renames one extension file if it still has expectedHash. The target must not exist and must be of the same kind: a schema stays <name>.json, a rule rules/<name>.js, a rule declaration file rules/<name>.json, a rows script seeds/<name>.js, a custom kind kinds/<type>.json, a calculation file calculated/<name>.js. A custom kind file that custom elements still use is refused as delete_extension_file refuses it. Only a seed's rowsFrom.script names a file by its path: a seed whose rows script moved reports it missing (MQ7116) until the seed names the new path.")]
    public Task<CallToolResult> MoveExtensionFile(
        [Description("The source path under extensions/; required.")] string? from = null,
        [Description("The target path under extensions/; required.")] string? to = null,
        [Description("The source file's hash; required.")] string? expectedHash = null,
        CancellationToken ct = default) => GuardAsync(() => ExtensionAsync(async () =>
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            return BadRequest("from and to are required.");
        if (string.IsNullOrEmpty(expectedHash))
            return Problem("precondition-required", 428, "expectedHash is required.");
        var result = await _store.MoveExtensionFileAsync(new ExtensionFileMove(from, to), expectedHash, ct, ChangeSource.Cli).ConfigureAwait(false);
        return result.Outcome == SaveOutcome.NotFound ? NotFound("extension file", from) : FromOutcome(result.Outcome, result, null);
    }), ct);

    private async Task<CallToolResult> ExtensionAsync(Func<Task<CallToolResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (ExtensionPathException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
