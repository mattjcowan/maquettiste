using System.ComponentModel;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The migration write tool (next-phase-1-design.md section 3.2): fold the open migration facts. Listing them is a catalog tool
/// (<c>list_migrations</c>).
/// </summary>
internal sealed partial class ModelTools
{
    /// <summary>Folds open migration facts.</summary>
    /// <param name="database">One database, by name or id.</param>
    /// <param name="against">A baseline whose facts stay: a model snapshot id or a folder.</param>
    /// <param name="dryRun">Whether to write nothing and return the files.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The fold result.</returns>
    [McpServerTool(Name = "fold_migrations", Title = "Fold migration facts", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Folds the open migration facts that a baseline lacks: removes stale ones (a change made and undone, a pending change reverted or widened back: MQ4103), then joins consecutive facts on one object into the last one, which keeps its id and file: renames a to b and b to c become a to c with via [b]; a change-type that only widens joins its neighbour, which keeps its conversion and data check; two pending change-types join; a rename then a drop join into the drop (the names in object.formerNames); consecutive rekeys chain. Two change-types that each carry a conversion are refused and stay in order, as is a join that would order facts in a cycle. The baseline is against (its facts stay), else the facts each database's committed physical snapshot lists as rendered, else only pending facts are folded. Never delete fact files by hand: fold them. Answers {outcome, joined: [{kept, removed, fact}], removedStale, refused: [{facts, reason}], files (with dryRun: each file it would change or delete, with its text before and after), diagnostics}. A fact file changed meanwhile is conflict; a model with errors outside the migration rules is invalid; an unknown database or baseline is bad-request.")]
    public Task<CallToolResult> FoldMigrations(
        [Description("Only this database's facts, by name or id (default: every database).")] string? database = null,
        [Description("A baseline whose facts are never stale and never folded: a model snapshot id (list_snapshots) or a folder that holds another copy of the project (relative to the server's current directory).")] string? against = null,
        [Description("true: write nothing; files lists what the fold would change.")] bool dryRun = false,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        ModelBaseline? baseline = null;
        if (against is { Length: > 0 })
        {
            try
            {
                baseline = await ModelBaseline.OpenAsync(_store, against, Environment.CurrentDirectory, allowFolders: true, Snapshots, ct)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        using (baseline)
        {
            FoldResult result;
            try
            {
                result = await _store.FoldMigrationsAsync(new FoldRequest(database is { Length: > 0 } ? database : null, baseline, dryRun, ChangeSource.Cli), ct)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message); // an unknown database
            }

            return result.Outcome switch
            {
                SaveOutcome.Saved => Ok(result),
                SaveOutcome.Conflict => Problem("conflict", 409, "A fact file changed since it was read; nothing was written. Fold again.", body: Node(result)),
                _ => Problem("invalid", 422, "The fold is not valid; nothing was written. See diagnostics.", body: Node(result)),
            };
        }
    }, ct);
}
