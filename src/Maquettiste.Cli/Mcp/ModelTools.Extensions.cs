using System.ComponentModel;
using Maquettiste.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The extension file tools, mirroring the pack file tools: the model's custom property schemas (<c>extensions/&lt;name&gt;.json</c>) and
/// script rules (<c>extensions/rules/&lt;name&gt;.js</c>), read and written in place through the engine's write guard. Every write takes the
/// hash it read. A rule's findings (<c>x/&lt;id&gt;</c>) come from <c>validate</c>, which runs script rules by default.
/// </summary>
internal sealed partial class ModelTools
{
    /// <summary>The extension files (listExtensionFiles).</summary>
    [McpServerTool(Name = "list_extension_files", Title = "Extension files", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The files of .maquettiste/extensions/: custom property schemas (<name>.json, kind schema) and script rules (rules/<name>.js, kind rule), each with size, hash (the expectedHash for write_extension_file, move_extension_file and delete_extension_file) and what is wrong with it on its own: MQ5004 for an invalid schema, MQ5002 or MQ5003 for a rule script that does not load (a syntax error, a rule without an id), with line and column.")]
    public Task<CallToolResult> ListExtensionFiles(CancellationToken ct = default) => GuardAsync(async () =>
        Ok(await _store.ListExtensionFilesAsync(ct).ConfigureAwait(false)), ct);

    /// <summary>Reads one extension file (getExtensionFile).</summary>
    [McpServerTool(Name = "read_extension_file", Title = "Read extension file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The text and hash of one extension file by its path under extensions/: <name>.json (a custom property schema) or rules/<name>.js (a script rule).")]
    public Task<CallToolResult> ReadExtensionFile([Description("The path under extensions/, such as retention.json or rules/naming.js; required.")] string? path = null,
        CancellationToken ct = default) => GuardAsync(() => ExtensionAsync(async () =>
    {
        if (string.IsNullOrEmpty(path))
            return BadRequest("path is required.");
        return await _store.ReadExtensionFileAsync(path, ct).ConfigureAwait(false) is { } file ? Ok(file) : NotFound("extension file", path);
    }), ct);

    /// <summary>Writes one extension file (putExtensionFile).</summary>
    [McpServerTool(Name = "write_extension_file", Title = "Write extension file", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Writes one extension file if it still has expectedHash; expectedHash 'new' creates a file that must not exist. UTF-8 text, no NUL, at most 1 MB. A schema (<name>.json: name, description, appliesTo { kinds, stereotypes }, properties, required) must pass get_schema's extension schema, else invalid with MQ5004 and nothing written; it is written in canonical form and text returns what was written. A script rule (rules/<name>.js, registering maquettiste.rule({ id, severity, kinds, check(element, model, report) })) is written as sent and loaded alone in the sandbox: a syntax error or a rule without an id comes back in diagnostics (MQ5002 with line and column). Run validate for the rule's x/<id> findings.")]
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
    [Description("Deletes one extension file if it still has expectedHash. A deleted schema's custom properties stay in the elements' properties, unchecked; a deleted rule stops running.")]
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
    [Description("Renames one extension file if it still has expectedHash. The target must not exist and must be of the same kind: a schema stays <name>.json, a rule rules/<name>.js. Nothing refers to an extension file by its path.")]
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
