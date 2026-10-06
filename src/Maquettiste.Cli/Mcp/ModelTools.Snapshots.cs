using System.ComponentModel;
using Maquettiste.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The snapshot write tools (docs/engineering/snapshots.md): take a snapshot, and restore one (destructive, after an automatic safety
/// snapshot). Listing and comparing are catalog tools (<c>list_snapshots</c>, <c>compare_snapshots</c>).
/// </summary>
internal sealed partial class ModelTools
{
    private SnapshotLibrary? _snapshots;

    private SnapshotLibrary Snapshots => _snapshots ??= new SnapshotLibrary(_store);

    /// <summary>Takes a snapshot (createSnapshot).</summary>
    /// <param name="name">The name.</param>
    /// <param name="description">What it is for.</param>
    /// <param name="includePacks">Whether to hold the template packs.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshot.</returns>
    [McpServerTool(Name = "create_snapshot", Title = "Take snapshot", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Takes a snapshot of the whole model: a named, immutable zip of maquettiste.json, model/, extensions/ and branding/ (and templates/ with includePacks) under .maquettiste/model-snapshots/<id>.zip. Returns its id (<name in kebab case>-<yyyymmdd-hhmmss>), counts and model hash. Take one before a large change so it can be compared (compare_snapshots) or restored (restore_snapshot).")]
    public Task<CallToolResult> CreateSnapshot(
        [Description("The name, 1 to 200 characters; required.")] string? name = null,
        [Description("What the snapshot is for.")] string? description = null,
        [Description("true: hold the template packs too, so generating from the snapshot reproduces exactly (default false).")] bool? includePacks = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("name is required.");
        try
        {
            return Ok(await Snapshots.CreateAsync(new SnapshotCreateRequest(name, description, includePacks ?? false, "agent"), ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }, ct);

    /// <summary>Restores a snapshot (restoreSnapshot).</summary>
    /// <param name="id">The snapshot id.</param>
    /// <param name="includePacks">Whether to restore its packs too.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What the restore did.</returns>
    [McpServerTool(Name = "restore_snapshot", Title = "Restore snapshot", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Replaces the working model with a snapshot's content in one write, after taking a safety snapshot of the working model (before-restore-<time>); restoring that one undoes it (the result's undo says so). Every document that differs is written and every document the snapshot lacks is deleted. Packs only with includePacks and when the snapshot holds them. Refused with code run-locked while a generation run holds the run lock. Read compare_snapshots with from=working and to=<id> first to see what it changes.")]
    public Task<CallToolResult> RestoreSnapshot(
        [Description("The snapshot id (list_snapshots); required.")] string? id = null,
        [Description("true: restore the snapshot's packs too (default false).")] bool? includePacks = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(id))
            return BadRequest("id is required.");
        var result = await Snapshots.RestoreAsync(id, new SnapshotRestoreRequest(includePacks ?? false, "agent", ChangeSource.Cli), ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            SnapshotRestoreOutcome.Restored => Ok(result),
            SnapshotRestoreOutcome.NotFound => NotFound("snapshot", id),
            SnapshotRestoreOutcome.Locked => Problem("run-locked", 409, "A generation run is writing; restore the snapshot when it ends."),
            _ => Problem("invalid", 422, "The restore was refused; nothing changed.", null, Node(result)),
        };
    }, ct);
}
