using System.ComponentModel;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The one-call generation tool (next-phase-1-design.md section 5.2): <c>generate</c> renders and writes in one run under the run lock,
/// as <c>maquettiste generate</c> does (<see cref="GenerationService.RunAsync"/> in apply mode), so it renders once and stores no plan.
/// The last-run record stays off in this long-lived server, as <see cref="GenerationService.ReuseLastRun"/> documents.
/// </summary>
internal sealed partial class ModelTools
{
    /// <summary>Generates in one call (plan and apply without storing a plan).</summary>
    /// <param name="packs">Pack names.</param>
    /// <param name="units">Units to run: <c>pack/unit</c> or a bare unit id.</param>
    /// <param name="elements">Element ids whose units run.</param>
    /// <param name="against">A baseline model to diff the schema from: a model snapshot id or a folder.</param>
    /// <param name="force">Whether to ignore the unit cache.</param>
    /// <param name="handEdits">A hand-edit policy.</param>
    /// <param name="jobs">Parallelism.</param>
    /// <param name="noWait">Whether to answer busy instead of waiting for another run's lock.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The run's summary (<see cref="GenerateSummary"/>).</returns>
    [McpServerTool(Name = "generate", Title = "Generate", Destructive = true, OpenWorld = false)]
    [Description("Generates in one call: renders what changed and writes it, as maquettiste generate does (the same writer, manifests and allowed outputs); no plan is stored. When the files should be reviewed first, use plan, get_plan_diff and apply_plan instead. Answers {outcome, unitsRendered, unitsSkipped, filesWritten, filesDeleted, packs: [{pack, counts, files: [{path, kind}], more}], diagnostics, moreDiagnostics}: for each pack that changed something, the count of files by kind (added, modified, deleted, kept, hand-edited, conflict, orphaned-owned), its first 50 paths in path order and how many more; files left alone as identical or not rendered are not listed. At most 100 diagnostics come, errors first, and moreDiagnostics counts the rest. units and elements limit the run as plan's do (values of one OR, the two AND): every other file and unit stays as it is; an unknown or ambiguous unit is MQ6032, a selected unit that reads the schema diff (sql-ddl/migration) is MQ6033, a filter that selects nothing is the warning MQ6034. against diffs the schema (migrations) from a baseline for this run and leaves the committed physical snapshots alone. An outcome other than succeeded is a tool error with the same fields: invalid (errors, see diagnostics; units without errors may still have written their files), conflicts (hand edits under the fail policy), busy (another run holds the lock, with noWait), failed.")]
    public Task<CallToolResult> Generate(
        [Description("Only these packs (default: every enabled pack).")] string[]? packs = null,
        [Description("Only these units: \"<pack>/<unit>\" (sql-ddl/schema), or a unit id that exactly one run pack has (default: every unit).")] string[]? units = null,
        [Description("Only the units of these elements, by id, never by name (a synthesized table's id is <entityId>@<databaseId>); units that run once for the whole model never match (default: every element).")] string[]? elements = null,
        [Description("A baseline model to diff the schema from: a model snapshot id (list_snapshots) or a folder that holds another copy of the project (relative to the server's current directory).")] string? against = null,
        [Description("Render every unit, ignoring the unit cache.")] bool force = false,
        [Description("fail, overwrite or skip: overrides the project's hand-edit policy.")] string? handEdits = null,
        [Description("Parallelism for this run (at least 1).")] int? jobs = null,
        [Description("true: answer busy at once when another run holds the run lock, instead of waiting for it.")] bool noWait = false,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (jobs is < 1)
            return BadRequest("jobs must be at least 1.");
        if (!TryHandEdits(handEdits, out var policy))
            return BadRequest($"handEdits must be fail, overwrite or skip, not '{handEdits}'.");
        if (ScopeProblem(units, elements) is { } scope)
            return BadRequest(scope);

        var request = new GenerationRequest
        {
            Mode = GenerationMode.Apply,
            Packs = packs is { Length: > 0 } ? packs : null,
            Units = Scope(units),
            Elements = Scope(elements),
            // A relative folder is resolved against the server's current directory, as plan resolves it.
            Against = against is { Length: > 0 }
                ? ModelBaseline.RequestText(Snapshots, against, Environment.CurrentDirectory)
                : null,
            Force = force,
            Jobs = jobs,
            HandEdits = policy,
            IncludeDiffs = false,
            Lock = noWait ? LockMode.Fail : LockMode.Wait,
        };
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        GenerationResult result;
        try
        {
            result = await _generation.RunAsync(request, null, ct).ConfigureAwait(false);
        }
        catch (BadBaselineException ex)
        {
            return BadRequest(ex.Message); // only the baseline's own errors: any other argument error is not relabelled
        }

        // The answer is the summary in every outcome, so a failed run's body has the same shape, without run id, timings or every change.
        var summary = GenerateSummary.Of(result);
        return result.Outcome == RunOutcome.Succeeded
            ? Ok(summary)
            : FromRun(result.Outcome, summary, ct,
                invalidTitle: "The model or the templates have errors; see diagnostics. Units without errors may have written their files (filesWritten, packs).");
    }, ct);
}
