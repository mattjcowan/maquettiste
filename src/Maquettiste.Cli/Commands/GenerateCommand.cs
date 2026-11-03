using System.Globalization;
using System.Text;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste generate</c> (engine-design.md section 16; SPEC section 17): an incremental run, <c>--dry-run</c> (the plan as
/// <c>A</c>/<c>M</c>/<c>D</c>/<c>H</c>/<c>K</c>/<c>O</c>/<c>C</c> lines, <c>--diff</c> adds unified diffs), <c>--check</c> (every root
/// in memory: a file that would be added, changed or deleted exits 2, hand edits 3) or <c>--watch</c> (the engine's debounced watch hook over a file watcher on the model root).
/// <c>--against &lt;snapshot id | folder&gt;</c> diffs the schema from a baseline model for one run instead of the committed physical
/// snapshots, and leaves those alone (next-phase-1-design.md section 3.1); a value that names nothing exits 4.
/// <c>--unit &lt;pack/unit | unit&gt;</c> and <c>--element &lt;id&gt;</c> (both repeatable; values of one flag OR, the two flags AND)
/// limit the run to those units, keeping every other output and unit state (next-phase-1-design.md section 5.6): an unknown or
/// ambiguous unit (MQ6032) or a selected unit that reads the schema diff (MQ6033) exits 1, a filter that selects nothing (MQ6034)
/// warns and exits 0.
/// </summary>
internal static class GenerateCommand
{
    /// <summary>The watch debounce (engine-design.md section 16).</summary>
    public static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        line.Expect("generate", 1, "--pack", "--force", "--roots", "--hand-edits", "--watch", "--dry-run", "--diff", "--check", "--format", "--no-wait",
            "--against", "--unit", "--element");
        var dryRun = line.Has("--dry-run");
        var check = line.Has("--check");
        var watch = line.Has("--watch");
        var diff = line.Has("--diff");
        var format = line.Choice("--format", "text", "text", "json");
        if (dryRun && check)
            throw new UsageException("--dry-run and --check cannot be combined.");
        if (watch && (dryRun || check))
            throw new UsageException("--watch cannot be combined with --dry-run or --check.");
        if (watch && format == "json")
            throw new UsageException("--watch prints text only.");
        if (diff && !dryRun && !check)
            throw new UsageException("--diff needs --dry-run (or --check).");
        var against = line.Value("--against");
        if (against is { Length: 0 })
            throw new UsageException("--against needs a model snapshot id or a folder.");
        if (watch && against is not null)
            throw new UsageException("--watch cannot be combined with --against: a baseline serves one run.");
        if (line.Value("--roots") is not null)
            throw new UsageException("--roots was removed in 0.5.5: generate (and --check) covers every output root.");
        HandEditPolicy? handEdits = line.Value("--hand-edits") is null ? null : line.Choice("--hand-edits", "fail", "fail", "overwrite", "skip") switch
        {
            "overwrite" => HandEditPolicy.Overwrite,
            "skip" => HandEditPolicy.Skip,
            _ => HandEditPolicy.Fail,
        };
        var packs = line.Values("--pack");
        if (packs.Any(string.IsNullOrWhiteSpace))
            throw new UsageException("--pack needs a pack name.");
        var units = line.Values("--unit");
        if (units.Any(string.IsNullOrWhiteSpace))
            throw new UsageException("--unit needs a unit: <pack>/<unit>, or a unit id that only one pack has.");
        var elements = line.Values("--element");
        if (elements.Any(string.IsNullOrWhiteSpace))
            throw new UsageException("--element needs an element id.");

        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var request = new GenerationRequest
        {
            Mode = check ? GenerationMode.Check : dryRun ? GenerationMode.DryRun : GenerationMode.Apply,
            Packs = packs.Count > 0 ? [.. packs.Distinct(StringComparer.Ordinal)] : null,
            Units = units.Count > 0 ? [.. units.Distinct(StringComparer.Ordinal)] : null,
            Elements = elements.Count > 0 ? [.. elements.Distinct(StringComparer.Ordinal)] : null,
            Force = line.Has("--force"),
            Jobs = context.Jobs,
            HandEdits = handEdits,
            IncludeDiffs = diff,
            Lock = line.Has("--no-wait") ? LockMode.Fail : LockMode.Wait,
        };

        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            // The command line accepts folders: a relative one is resolved against the current directory, and the request carries
            // the full path (or the snapshot id).
            var library = new SnapshotLibrary(store);
            var service = new GenerationService(store, options) { Baselines = library, AllowFolderBaselines = true };
            if (against is not null)
                request = request with { Against = ModelBaseline.RequestText(library, against, context.Environment.CurrentDirectory) };
            if (watch)
                return await WatchAsync(context, service, request, options, ct).ConfigureAwait(false);

            // A one-shot apply keeps and uses the engine's last-run record: a process whose model, templates, engine files and outputs
            // are as the last apply left them answers without loading, validating, resolving or planning (Generation/README.md). A run
            // against a baseline or limited by unit or element only forgets it, so a later full run never replays a stale record.
            service.ReuseLastRun = true;

            var progress = new ConsoleProgress(context.Progress, context.Error, hideWrite: check);
            GenerationResult result;
            try
            {
                result = await service.RunAsync(request, progress, ct).ConfigureAwait(false);
            }
            catch (BadBaselineException ex)
            {
                // The baseline resolves to nothing (or is at a newer model format): a usage error. Any other argument error is not
                // relabelled.
                progress.Complete();
                throw new UsageException(ex.Message);
            }

            progress.Complete();
            result = WithSchemaFindings(result, store, options);
            await ReportAsync(context, result, format, progress.FilesCompared, progress.Compared, prefix: "").ConfigureAwait(false);
            return ExitCode(result.Outcome);
        }
    }

    /// <summary>Maps a run outcome to the exit code (the engine already applied the precedence 4, 1, 3, 2).</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The exit code.</returns>
    public static int ExitCode(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Succeeded => Program.ExitCodes.Success,
        RunOutcome.Invalid => Program.ExitCodes.Invalid,
        RunOutcome.Drift => Program.ExitCodes.Drift,
        RunOutcome.Conflicts => Program.ExitCodes.Conflicts,
        _ => Program.ExitCodes.Internal,
    };

    /// <summary>The one-letter code of a file change in plan listings.</summary>
    /// <param name="kind">The change kind.</param>
    /// <returns>The letter.</returns>
    public static char Letter(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => 'A',
        FileChangeKind.Modified => 'M',
        FileChangeKind.Deleted => 'D',
        FileChangeKind.HandEdited => 'H',
        FileChangeKind.Kept => 'K',
        FileChangeKind.OrphanedOwned => 'O',
        FileChangeKind.Conflict => 'C',
        _ => 'U',
    };

    /// <summary>The JSON name of a file change kind.</summary>
    /// <param name="kind">The change kind.</param>
    /// <returns>The kebab-case name.</returns>
    public static string KindName(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => "added",
        FileChangeKind.Modified => "modified",
        FileChangeKind.Deleted => "deleted",
        FileChangeKind.HandEdited => "hand-edited",
        FileChangeKind.Kept => "kept",
        FileChangeKind.OrphanedOwned => "orphaned-owned",
        FileChangeKind.Conflict => "conflict",
        _ => "unchanged",
    };

    private static async Task<int> WatchAsync(GlobalContext context, GenerationService service, GenerationRequest request, EngineOptions options, CancellationToken ct)
    {
        var progress = new ConsoleProgress(context.Progress, context.Error);
        var runs = 0;
        var modelRoot = Path.Combine(options.RepoRoot, GlobalContext.ModelFolder);
        var watcher = new GenerationWatcher(service, request, WatchDebounce, async (result, token) =>
        {
            progress.Complete();
            runs++;
            result = WithSchemaFindings(result, service.Store, options);
            await ReportAsync(context, result, "text", progress.TakeFilesCompared(), progress.TakeCompared(), prefix: string.Create(CultureInfo.InvariantCulture, $"[watch] run {runs}: ")).ConfigureAwait(false);
        }, progress);

        // The watcher is live before the initial run: an edit saved during that run queues a notification, and the watch loop
        // below turns it into one follow-up run (GenerationWatcher collects notifications until its loop picks them up).
        using var files = new FileSystemWatcher(modelRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        files.Changed += (_, e) => watcher.Notify([e.FullPath]);
        files.Created += (_, e) => watcher.Notify([e.FullPath]);
        files.Deleted += (_, e) => watcher.Notify([e.FullPath]);
        files.Renamed += (_, e) => watcher.Notify([e.OldFullPath, e.FullPath]);
        // A buffer overflow loses paths; a run without paths still rescans by stat (ModelStore.GetSnapshotAsync).
        files.Error += (_, _) => watcher.Trigger();
        files.EnableRaisingEvents = true;
        using var attachment = watcher.Attach(service.Store);

        try
        {
            var first = await service.RunAsync(request, progress, ct).ConfigureAwait(false);
            progress.Complete();
            first = WithSchemaFindings(first, service.Store, options);
            await ReportAsync(context, first, "text", progress.TakeFilesCompared(), progress.TakeCompared(), prefix: "[watch] initial run: ").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Program.ExitCodes.Success;
        }

        await context.Error.WriteLineAsync($"[watch] watching {modelRoot} for changes; press Ctrl+C to stop.").ConfigureAwait(false);
        await watcher.RunAsync(ct).ConfigureAwait(false);
        progress.Complete();
        context.Info("[watch] stopped.");
        return Program.ExitCodes.Success;
    }

    /// <summary>
    /// Adds MQ1008 when <c>.maquettiste/.schema/v1/</c> differs from the embedded schemas (engine-design.md section 3), as
    /// <c>validate</c> does; the folder is never refreshed here, and the run's outcome stays the engine's. A run answered from the
    /// last-run record loaded no snapshot, so the severity override then comes from the settings file.
    /// </summary>
    /// <param name="result">The run's result.</param>
    /// <param name="store">The run's store.</param>
    /// <param name="options">The engine options.</param>
    /// <returns>The result with the finding among its diagnostics, in diagnostic order.</returns>
    internal static GenerationResult WithSchemaFindings(GenerationResult result, ModelStore store, EngineOptions options)
    {
        if (result.Outcome == RunOutcome.Cancelled)
            return result;
        var findings = SchemaFolder.Findings(options, store.Current?.Settings);
        if (findings.Count == 0)
            return result;
        var diagnostics = result.Diagnostics.Concat(findings).Distinct().ToList();
        diagnostics.Sort(Diagnostic.Order);
        return result with { Diagnostics = diagnostics };
    }

    private static async Task ReportAsync(GlobalContext context, GenerationResult result, string format, int filesCompared, Func<string, bool> compared,
        string prefix)
    {
        var quiet = context.Verbosity == Verbosity.Quiet;
        var listing = result.Mode != GenerationMode.Apply || !quiet;
        if (format == "json")
        {
            await context.Out.WriteLineAsync(Json(result)).ConfigureAwait(false);
        }
        else if (listing)
        {
            var text = new StringBuilder();
            foreach (var change in result.Changes)
                text.Append(Letter(change.Kind)).Append(' ').Append(change.Path).Append('\n');
            foreach (var change in result.Changes.Where(c => !string.IsNullOrEmpty(c.Diff)))
            {
                text.Append(change.Diff);
                if (!change.Diff!.EndsWith('\n'))
                    text.Append('\n');
            }

            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        await context.Out.FlushAsync().ConfigureAwait(false);

        foreach (var d in result.Diagnostics.Where(d => !quiet || d.Severity == DiagnosticSeverity.Error))
            await context.Error.WriteLineAsync(DiagnosticOutput.Line(d)).ConfigureAwait(false);

        var message = Message(result);
        if (message is not null)
            await context.Error.WriteLineAsync(prefix + message).ConfigureAwait(false);
        if (quiet)
            return;

        int Count(params FileChangeKind[] kinds) => result.Changes.Count(c => kinds.Contains(c.Kind));
        // A kept owned file counts as produced only when this run rendered it: a once file kept on disk that no unit produces any
        // more (an earlier migration) was never compared, so it takes nothing from the unchanged count.
        var produced = Count(FileChangeKind.Added, FileChangeKind.Modified, FileChangeKind.Conflict)
            + result.Changes.Count(c => c.Kind == FileChangeKind.Kept && compared(c.Path))
            + result.Changes.Count(c => c.Kind == FileChangeKind.HandEdited && c.NewHash is not null);
        var unchanged = Math.Max(0, filesCompared - produced);
        var verb = result.Mode switch
        {
            GenerationMode.DryRun => "Dry run",
            GenerationMode.Check => "Check",
            _ => "Generated",
        };
        context.Error.WriteLine(prefix + string.Create(CultureInfo.InvariantCulture,
            $"{verb}: {Count(FileChangeKind.Added)} added, {Count(FileChangeKind.Modified)} modified, {Count(FileChangeKind.Deleted)} deleted, {unchanged} unchanged, {result.UnitsSkipped} units skipped, {Count(FileChangeKind.HandEdited, FileChangeKind.Conflict)} hand edits ({result.UnitsRendered} units rendered; {result.FilesWritten} written, {result.FilesDeleted} deleted on disk). Outcome: {result.Outcome}."));
        if (context.Verbosity == Verbosity.Detailed)
        {
            foreach (var timing in result.Timings)
            {
                context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  [{(int)timing.Stage}/8 {ConsoleProgress.Name(timing.Stage)}] wall {timing.Wall.TotalMilliseconds:0} ms, busy {timing.Busy.TotalMilliseconds:0} ms, {timing.Items:N0} items"));
            }
        }
    }

    private static string? Message(GenerationResult result) => result.Outcome switch
    {
        RunOutcome.Invalid => "Generation stopped: the model or a pack has errors (exit 1).",
        RunOutcome.Drift => "Drift: the output on disk does not match the model; run maquettiste generate (exit 2).",
        RunOutcome.Conflicts => "Conflicts: generated files were edited by hand or lost protected regions (exit 3).",
        RunOutcome.Busy => "Another maquettiste run holds the run lock (exit 4).",
        RunOutcome.Cancelled => "Cancelled (exit 4).",
        RunOutcome.Stale => "The plan is stale (exit 4).",
        RunOutcome.Failed => "Generation failed (exit 4).",
        _ => null,
    };

    /// <summary>The JSON result of a run; the run id and timings are left out, so the output is deterministic.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The JSON text.</returns>
    public static string Json(GenerationResult result) => DiagnosticOutput.ToJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("mode", result.Mode switch
        {
            GenerationMode.DryRun => "dry-run",
            GenerationMode.Check => "check",
            _ => "apply",
        });
        writer.WriteString("outcome", result.Outcome.ToString().ToLowerInvariant());
        writer.WriteNumber("exitCode", ExitCode(result.Outcome));
        writer.WriteNumber("unitsRendered", result.UnitsRendered);
        writer.WriteNumber("unitsSkipped", result.UnitsSkipped);
        writer.WriteNumber("filesWritten", result.FilesWritten);
        writer.WriteNumber("filesDeleted", result.FilesDeleted);
        writer.WriteStartArray("changes");
        foreach (var change in result.Changes)
        {
            writer.WriteStartObject();
            writer.WriteString("path", change.Path);
            writer.WriteString("kind", KindName(change.Kind));
            writer.WriteString("pack", change.Pack);
            writer.WriteString("unit", change.UnitKey);
            if (change.OldHash is not null)
                writer.WriteString("oldHash", change.OldHash);
            if (change.NewHash is not null)
                writer.WriteString("newHash", change.NewHash);
            if (change.Diff is not null)
                writer.WriteString("diff", change.Diff);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        DiagnosticOutput.WriteArray(writer, "diagnostics", result.Diagnostics);
        writer.WriteEndObject();
    });
}
