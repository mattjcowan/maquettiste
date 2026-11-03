namespace Maquettiste.Engine;

/// <summary>The snapshot read tools (docs/engineering/snapshots.md): list and compare; taking and restoring are the MCP server's own tools.</summary>
public sealed partial class AgentTools
{
    private SnapshotLibrary? _snapshotLibrary;

    /// <summary>The host's library (the generation service's), so a snapshot is loaded once per host; else one of the catalog's own.</summary>
    private SnapshotLibrary Snapshots => _generation.Baselines ?? (_snapshotLibrary ??= new SnapshotLibrary(_store));

    /// <summary>
    /// <c>compare_snapshots</c> (next-phase-2-design.md section 6.2): always grouped by package; <c>fieldChanges</c> adds each changed
    /// element's field changes (and a default page of 50); <c>format</c> <c>markdown</c> answers <c>{ markdown, next }</c>, the bytes
    /// <c>maquettiste model diff --format markdown</c> prints for the page. A side may be a folder only where the host allows folders.
    /// </summary>
    private async Task<AgentToolResult> CompareSnapshotsAsync(ToolArguments args, CancellationToken ct)
    {
        var from = args.String("from");
        if (string.IsNullOrEmpty(from))
            return BadRequest("from is required: working, a snapshot id or a folder.");
        var to = args.String("to") is { Length: > 0 } given ? given : SnapshotLibrary.Working;
        var format = args.String("format") is { Length: > 0 } f ? f : "json";
        if (format is not ("json" or "markdown"))
            return BadRequest("format must be json or markdown.");
        var fieldChanges = args.Bool("fieldChanges") ?? false;
        var options = new SnapshotCompareOptions
        {
            ByPackage = true,
            FieldChanges = fieldChanges || format == "markdown", // the review form shows the field changes
            AllowFolders = _options.AllowFolderBaselines,
            CurrentDirectory = Environment.CurrentDirectory,
        };
        foreach (var side in (string[])[from, to])
        {
            // An id that names no snapshot (and no folder where folders are allowed) is not found, as before folders.
            if (side != SnapshotLibrary.Working && !ModelBaseline.IsPathLike(side) && SnapshotLibrary.IsValidId(side)
                && await Snapshots.GetAsync(side, ct).ConfigureAwait(false) is null
                && !(options.AllowFolders && Directory.Exists(Path.GetFullPath(side, options.CurrentDirectory))))
            {
                return NotFound("snapshot", side);
            }
        }

        try
        {
            if (args.String("id") is { Length: > 0 } id)
            {
                var diff = await Snapshots.CompareElementAsync(from, to, id, options, ct).ConfigureAwait(false);
                if (diff is null)
                    return NotFound("element on either side", id);
                return format == "markdown" ? Ok(new { markdown = ModelDiffFormat.Markdown(diff), next = (int?)null }) : Ok(diff);
            }

            var offset = args.Int("offset") ?? 0;
            var limit = args.Int("limit") ?? (options.FieldChanges ? SnapshotCompareOptions.MaxElementFields : SnapshotLibrary.DefaultCompareLimit);
            if (offset < 0)
                return BadRequest("offset must be zero or more.");
            if (limit is < 1 or > SnapshotLibrary.MaxCompareLimit)
                return BadRequest($"limit must be from 1 to {SnapshotLibrary.MaxCompareLimit}.");
            var comparison = (await Snapshots.CompareAsync(from, to, offset, limit, options, ct).ConfigureAwait(false))!;
            return format == "markdown" ? Ok(new { markdown = ModelDiffFormat.Markdown(comparison), next = comparison.Next }) : Ok(comparison);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private async Task<AgentToolResult> ListSnapshotsAsync(ToolArguments args, CancellationToken ct) =>
        Ok(await Snapshots.ListAsync(ct).ConfigureAwait(false));
}
