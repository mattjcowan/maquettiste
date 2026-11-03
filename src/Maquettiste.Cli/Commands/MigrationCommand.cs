using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste migration list|fold|ledger</c> (next-phase-1-design.md sections 3.2 and 3.3): the model's open migration facts, one
/// change stored rows must follow per file. <c>list</c> prints them in applied order; <c>fold</c> removes stale facts and joins
/// consecutive facts on one object (a dry run with <c>--dry-run</c>); <c>ledger</c> prints the migration ledger's DDL for a PostgreSQL
/// database and, with <c>--grant-read</c>, its read grant. Distinct from <c>migrate</c>, which upgrades the model format.
/// </summary>
internal static class MigrationCommand
{
    /// <summary>Runs <c>migration &lt;verb&gt;</c>.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        var verb = line.Positionals.Count > 1 ? line.Positionals[1] : throw new UsageException("'migration' needs a verb: list, fold or ledger.");
        switch (verb)
        {
            case "list":
                line.Expect("migration list", 2, "--database", "--format");
                break;
            case "fold":
                line.Expect("migration fold", 2, "--database", "--against", "--dry-run", "--format");
                break;
            case "ledger":
                line.Expect("migration ledger", 2, "--database", "--grant-read", "--format", "--output");
                break;
            default:
                throw new UsageException($"Unknown migration verb '{verb}': use list, fold or ledger.");
        }

        var json = line.Choice("--format", "text", "text", "json") == "json";
        var database = line.Value("--database");
        if (database is { Length: 0 })
            throw new UsageException("--database needs a database name or id.");
        var against = line.Value("--against");
        if (against is { Length: 0 })
            throw new UsageException("--against needs a model snapshot id or a folder.");
        var grantRead = line.Value("--grant-read");
        if (grantRead is { Length: 0 })
            throw new UsageException("--grant-read needs a role name.");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            return verb switch
            {
                "list" => await ListAsync(context, store, database, json, ct).ConfigureAwait(false),
                "fold" => await FoldAsync(context, store, database, against, line.Has("--dry-run"), json, ct).ConfigureAwait(false),
                _ => await LedgerAsync(context, options, store, database, grantRead, line.Value("--output"), json, ct).ConfigureAwait(false),
            };
        }
    }

    /// <summary>One line of <c>migration list</c>: database, order, id, fact, state (with the missing intent), the stale mark, summary.</summary>
    /// <param name="entry">The fact.</param>
    /// <returns>The line, without its newline.</returns>
    internal static string Line(MigrationEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append(entry.DatabaseName).Append(' ').Append(entry.Order.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(entry.Id)
            .Append(' ').Append(entry.Fact).Append(' ').Append(entry.State);
        if (entry.Missing.Count > 0)
            sb.Append(" (needs ").Append(string.Join(", ", entry.Missing)).Append(')');
        if (entry.Stale)
            sb.Append(" stale");
        return sb.Append(": ").Append(entry.Summary).ToString();
    }

    private static async Task<int> ListAsync(GlobalContext context, ModelStore store, string? database, bool json, CancellationToken ct)
    {
        // Every page, so the command prints the whole list in one answer.
        var facts = new List<MigrationEntry>();
        string? cursor = null;
        try
        {
            do
            {
                var page = await store.ListMigrationsAsync(database, cursor, ModelPages.MaxLimit, ct).ConfigureAwait(false);
                facts.AddRange(page.Facts);
                cursor = page.Next;
            }
            while (cursor is not null);
        }
        catch (ArgumentException ex)
        {
            await context.Error.WriteLineAsync("maquettiste: " + ex.Message).ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        var list = new MigrationList(facts, facts.Count, null);
        await context.Out.WriteAsync(json
            ? JsonSerializer.Serialize(list, CliJson.Options) + "\n"
            : string.Concat(facts.Select(f => Line(f) + "\n"))).ConfigureAwait(false);
        await context.Out.FlushAsync(ct).ConfigureAwait(false);
        var pending = facts.Count(f => f.State == "pending");
        var stale = facts.Count(f => f.Stale);
        context.Info(string.Create(CultureInfo.InvariantCulture,
            $"{facts.Count} open fact{(facts.Count == 1 ? "" : "s")}: {pending} pending, {stale} stale."));
        return Program.ExitCodes.Success;
    }

    /// <summary>
    /// <c>migration ledger</c> (next-phase-1-design.md section 3.3): the ledger DDL of one PostgreSQL database, then the read grant when
    /// <c>--grant-read</c> names a role (text), or the <see cref="LedgerDdlAnswer"/> (JSON); to stdout, or to <c>--output</c> through the
    /// path policy as an output file.
    /// </summary>
    private static async Task<int> LedgerAsync(GlobalContext context, EngineOptions options, ModelStore store, string? database, string? grantRead,
        string? output, bool json, CancellationToken ct)
    {
        LedgerDdlAnswer answer;
        try
        {
            answer = await store.GetLedgerDdlAsync(database, grantRead, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await context.Error.WriteLineAsync("maquettiste: " + ex.Message).ConfigureAwait(false);
            return Program.ExitCodes.Invalid; // no PostgreSQL database, the named one missing or not PostgreSQL, several and none named
        }

        var text = json
            ? JsonSerializer.Serialize(answer, CliJson.Options) + "\n"
            : answer.Ddl + (answer.Grant is null ? "" : "\n" + answer.Grant);
        if (output is not null)
        {
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var full = Path.GetFullPath(output, context.Environment.CurrentDirectory);
            var files = new GuardedFiles(EngineServices.Create(options).CreatePathPolicy(snapshot.Settings));
            await files.WriteAsync(WriteTarget.Output, full, Encoding.UTF8.GetBytes(text), overwrite: true, ct).ConfigureAwait(false);
            context.Info("Wrote " + full + ".");
        }
        else
        {
            await context.Out.WriteAsync(text).ConfigureAwait(false);
            await context.Out.FlushAsync(ct).ConfigureAwait(false);
        }

        context.Info(string.Create(CultureInfo.InvariantCulture,
            $"Ledger contract version {answer.ContractVersion} for database {answer.Database} ({answer.Dialect}), schema {answer.Schema}{(answer.Grant is null ? "" : ", with the read grant")}."));
        return Program.ExitCodes.Success;
    }

    private static async Task<int> FoldAsync(GlobalContext context, ModelStore store, string? database, string? against, bool dryRun, bool json,
        CancellationToken ct)
    {
        ModelBaseline? baseline = null;
        if (against is not null)
        {
            try
            {
                // A relative folder is resolved against the current directory; the command line accepts folders.
                baseline = await ModelBaseline.OpenAsync(store, against, context.Environment.CurrentDirectory, allowFolders: true, library: null, ct)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                throw new UsageException(ex.Message);
            }
        }

        FoldResult result;
        using (baseline)
        {
            try
            {
                result = await store.FoldMigrationsAsync(new FoldRequest(database, baseline, dryRun, ChangeSource.Cli), ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await context.Error.WriteLineAsync("maquettiste: " + ex.Message).ConfigureAwait(false);
                return Program.ExitCodes.Invalid; // an unknown database
            }
        }

        if (json)
        {
            await context.Out.WriteAsync(JsonSerializer.Serialize(result, CliJson.Options) + "\n").ConfigureAwait(false);
        }
        else
        {
            var text = new StringBuilder();
            foreach (var join in result.Joined)
                text.Append("joined ").Append(join.Fact).Append(' ').Append(string.Join(", ", join.Removed)).Append(" into ").Append(join.Kept).Append('\n');
            foreach (var id in result.RemovedStale)
                text.Append("removed stale ").Append(id).Append('\n');
            foreach (var refusal in result.Refused)
                text.Append("kept ").Append(string.Join(", ", refusal.Facts)).Append(": ").Append(refusal.Reason).Append('\n');
            foreach (var file in result.Files)
                text.Append(file.Action switch { "created" => "A ", "deleted" => "D ", _ => "M " }).Append(file.Path).Append('\n');
            foreach (var diagnostic in result.Diagnostics)
                text.Append(DiagnosticOutput.Line(diagnostic)).Append('\n');
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        await context.Out.FlushAsync(ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case SaveOutcome.Saved:
                var removed = result.Joined.Sum(j => j.Removed.Count) + result.RemovedStale.Count;
                context.Info(string.Create(CultureInfo.InvariantCulture,
                    $"{(dryRun ? "Would fold" : "Folded")}: {result.Joined.Count} join{(result.Joined.Count == 1 ? "" : "s")}, {removed} fact{(removed == 1 ? "" : "s")} removed, {result.Refused.Count} refused."));
                return Program.ExitCodes.Success;
            case SaveOutcome.Conflict:
                context.Info("A fact file changed while the fold ran; nothing was written. Run it again.");
                return Program.ExitCodes.Conflicts;
            default:
                context.Info("The fold was refused; nothing was written. See the diagnostics.");
                return Program.ExitCodes.Invalid;
        }
    }
}
