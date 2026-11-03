using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste binding drift|map|resync</c> (next-phase-2-design.md section 3.3, D35): what changed in each binding's table since its
/// provenance was recorded, and the two operations that catch up. <c>drift</c> prints the report from files only (with <c>--check</c>,
/// exit 2 when a listed binding drifts); <c>map</c> runs <c>map-by-name</c> and <c>resync</c> runs <c>resync-binding</c>, each planned
/// first and applied all or nothing as the batch operation, or only printed with <c>--dry-run</c>. Entity arguments take ids or names,
/// as <c>model materialize</c> does, and so do the exit codes: 0 saved, 1 refused or invalid, 3 on a conflict.
/// </summary>
internal static class BindingCommand
{
    /// <summary>Runs <c>binding &lt;verb&gt;</c>.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        var verb = line.Positionals.Count > 1 ? line.Positionals[1] : throw new UsageException("'binding' needs a verb: drift, map or resync.");
        switch (verb)
        {
            case "drift":
                line.Expect("binding drift", line.Positionals.Count, "--database", "--all", "--check", "--format");
                break;
            case "map":
                if (line.Positionals.Count < 3)
                    throw new UsageException("'binding map' needs at least one entity.");
                line.Expect("binding map", line.Positionals.Count, "--database", "--dry-run", "--format");
                break;
            case "resync":
                if (line.Positionals.Count < 3)
                    throw new UsageException("'binding resync' needs at least one entity.");
                line.Expect("binding resync", line.Positionals.Count, "--database", "--column", "--keep-names", "--dry-run", "--format");
                break;
            default:
                throw new UsageException($"Unknown binding verb '{verb}': use drift, map or resync.");
        }

        var json = line.Choice("--format", "text", "text", "json") == "json";
        if (line.Value("--database") is { Length: 0 })
            throw new UsageException("--database needs a database name or id.");
        if (verb != "drift" && line.Value("--database") is null)
            throw new UsageException("--database is required.");
        if (line.Values("--column").Count > 0 && line.Positionals.Count > 3)
            throw new UsageException("--column names the columns of one entity.");
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var store = new ModelStore(context.EngineOptions(repo));
        await using (store.ConfigureAwait(false))
        {
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var database = line.Value("--database") is { } wanted ? ModelCommand.One(snapshot, "database", wanted, null) : null;
            var entities = line.Positionals.Skip(2).Select(name => ModelCommand.One(snapshot, "entity", name, null)).Distinct(StringComparer.Ordinal).ToList();
            return verb == "drift"
                ? await DriftAsync(context, store, database, entities, json, ct).ConfigureAwait(false)
                : await SyncAsync(context, store, verb, database!, entities, json, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>binding drift</c>: every page of the report, printed at once. Without <c>--all</c> only the bindings with something to report
    /// are listed; with <c>--check</c> a listed binding with drift exits 2.
    /// </summary>
    private static async Task<int> DriftAsync(GlobalContext context, ModelStore store, string? database, IReadOnlyList<string> entities, bool json,
        CancellationToken ct)
    {
        var line = context.Line;
        var bindings = new List<BindingDriftEntry>();
        string? cursor = null;
        do
        {
            var page = await store.GetBindingDriftAsync(new BindingDriftQuery(database, entities.Count > 0 ? entities : null, line.Has("--all"), cursor,
                ModelPages.MaxLimit), ct).ConfigureAwait(false);
            bindings.AddRange(page.Bindings);
            cursor = page.Next;
        }
        while (cursor is not null);

        await context.Out.WriteAsync(json
            ? JsonSerializer.Serialize(new BindingDriftPage(bindings, null), CliJson.Options) + "\n"
            : string.Concat(bindings.Select(Text))).ConfigureAwait(false);
        await context.Out.FlushAsync(ct).ConfigureAwait(false);
        var drifted = bindings.Count(HasDrift);
        context.Info(string.Create(CultureInfo.InvariantCulture,
            $"{bindings.Count} binding{(bindings.Count == 1 ? "" : "s")} listed, {drifted} with drift."));
        return line.Has("--check") && drifted > 0 ? Program.ExitCodes.Drift : Program.ExitCodes.Success;
    }

    /// <summary>Whether a binding has something to catch up with: stale provenance, or a column added, renamed, retyped or removed.</summary>
    private static bool HasDrift(BindingDriftEntry b) => b.Stale || b.Added.Count > 0 || b.Renamed.Count > 0 || b.Retyped.Count > 0 || b.Removed.Count > 0;

    /// <summary>
    /// One binding as lines: <c>&lt;entity&gt; in &lt;database&gt; (&lt;source&gt;)</c>, then one line per added, renamed, retyped and
    /// removed column, or <c>in step</c> (<c>nothing to report</c> without provenance).
    /// </summary>
    internal static string Text(BindingDriftEntry b)
    {
        var text = new StringBuilder();
        text.Append(b.EntityName).Append(" in ").Append(b.DatabaseName).Append(" (").Append(b.SourceName).Append(')');
        if (!b.HasProvenance)
            text.Append(", no provenance");
        text.Append('\n');
        if (b.Stale)
            text.Append("  stale provenance: it does not describe the source; re-sync to record it again\n");
        foreach (var c in b.Added)
            text.Append(CultureInfo.InvariantCulture, $"  added {c.Name} ({c.Status})\n");
        foreach (var f in b.Renamed)
            text.Append(CultureInfo.InvariantCulture, $"  renamed {f.Name} (attribute {f.AttributeName})\n");
        foreach (var f in b.Retyped)
            text.Append(CultureInfo.InvariantCulture, $"  retyped {f.Name} (attribute {f.AttributeName})\n");
        foreach (var id in b.Removed)
            text.Append("  removed ").Append(id).Append('\n');
        if (!HasDrift(b))
            text.Append(b.HasProvenance ? "  in step\n" : "  nothing to report\n");
        return text.ToString();
    }

    /// <summary>
    /// <c>binding map</c> and <c>binding resync</c>: plans the operation; with <c>--dry-run</c> prints the plan, else applies it as the
    /// batch operation, all or nothing.
    /// </summary>
    private static async Task<int> SyncAsync(GlobalContext context, ModelStore store, string verb, string database, IReadOnlyList<string> entities, bool json,
        CancellationToken ct)
    {
        var line = context.Line;
        var resync = verb == "resync";
        IReadOnlyList<string>? columns = resync && line.Values("--column") is { Count: > 0 } named ? named : null;
        var keepNames = resync && line.Has("--keep-names");
        var request = new MaterializeRequest(resync ? "resync-binding" : "map-by-name", database, entities, Columns: columns, KeepNames: keepNames);
        var plan = await store.PlanMaterializeAsync(request, ct).ConfigureAwait(false);
        BatchResult? result = null;
        if (!line.Has("--dry-run") && plan.Valid)
        {
            var operation = new BatchOperation(resync ? BatchOp.ResyncBinding : BatchOp.MapByName, null, null, null, Database: database, Entities: entities,
                Columns: columns, KeepNames: resync ? keepNames : null);
            result = await store.ApplyBatchAsync(new ModelBatch([operation]), ChangeSource.Cli, ct).ConfigureAwait(false);
        }

        if (json)
        {
            var body = result is null ? JsonSerializer.Serialize(plan, CliJson.Options) : JsonSerializer.Serialize(new { plan, result }, CliJson.Options);
            await context.Out.WriteLineAsync(body).ConfigureAwait(false);
        }
        else
        {
            await context.Out.WriteAsync(ModelCommand.MaterializeText(plan, result is null)).ConfigureAwait(false);
            foreach (var d in (result?.Items.SelectMany(i => i.Diagnostics) ?? plan.Diagnostics).Where(d => d.Severity == Engine.Diagnostics.DiagnosticSeverity.Error))
                await context.Error.WriteLineAsync(DiagnosticOutput.Line(d)).ConfigureAwait(false);
        }

        await context.Out.FlushAsync(ct).ConfigureAwait(false);
        if (result is null)
            return plan.Valid ? Program.ExitCodes.Success : Program.ExitCodes.Invalid;
        if (result.Outcome == SaveOutcome.Saved)
        {
            context.Info(string.Create(CultureInfo.InvariantCulture, $"{(resync ? "Re-synced" : "Mapped")}: {result.Changes?.Changed.Count ?? 0} changed."));
            return Program.ExitCodes.Success;
        }

        await context.Error.WriteLineAsync("maquettiste: nothing was written.").ConfigureAwait(false);
        return result.Outcome == SaveOutcome.Conflict ? Program.ExitCodes.Conflicts : Program.ExitCodes.Invalid;
    }
}
