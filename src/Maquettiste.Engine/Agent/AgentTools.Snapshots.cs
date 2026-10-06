namespace Maquettiste.Engine;

/// <summary>The snapshot read tools (docs/engineering/snapshots.md): list and compare; taking and restoring are the MCP server's own tools.</summary>
public sealed partial class AgentTools
{
    private SnapshotLibrary? _snapshotLibrary;

    private SnapshotLibrary Snapshots => _snapshotLibrary ??= new SnapshotLibrary(_store);

    private async Task<AgentToolResult> CompareSnapshotsAsync(ToolArguments args, CancellationToken ct)
    {
        var from = args.String("from");
        if (string.IsNullOrEmpty(from))
            return BadRequest("from is required: a snapshot id or working.");
        var to = args.String("to") is { Length: > 0 } given ? given : SnapshotLibrary.Working;
        if (args.String("id") is { Length: > 0 } id)
        {
            var diff = await Snapshots.CompareElementAsync(from, to, id, ct).ConfigureAwait(false);
            return diff is null ? NotFound("element on either side", id) : Ok(diff);
        }

        var offset = args.Int("offset") ?? 0;
        var limit = args.Int("limit") ?? SnapshotLibrary.DefaultCompareLimit;
        if (offset < 0)
            return BadRequest("offset must be zero or more.");
        if (limit is < 1 or > SnapshotLibrary.MaxCompareLimit)
            return BadRequest($"limit must be from 1 to {SnapshotLibrary.MaxCompareLimit}.");
        var comparison = await Snapshots.CompareAsync(from, to, offset, limit, ct).ConfigureAwait(false);
        return comparison is null ? NotFound("snapshot", from + " or " + to) : Ok(comparison);
    }

    private async Task<AgentToolResult> ListSnapshotsAsync(ToolArguments args, CancellationToken ct) =>
        Ok(await Snapshots.ListAsync(ct).ConfigureAwait(false));
}
