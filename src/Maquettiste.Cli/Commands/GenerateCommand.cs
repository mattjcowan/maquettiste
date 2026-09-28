using System.Globalization;
using System.Text;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste generate</c> (engine-design.md section 16; SPEC section 17): an incremental run, <c>--dry-run</c> (the plan as
/// <c>A</c>/<c>M</c>/<c>D</c>/<c>H</c>/<c>K</c>/<c>O</c>/<c>C</c> lines, <c>--diff</c> adds unified diffs), <c>--check</c> (committed roots
/// in memory: drift exits 2, hand edits 3) or <c>--watch</c> (the engine's debounced watch hook over a file watcher on the model root).
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
        line.Expect("generate", 1, "--pack", "--force", "--roots", "--hand-edits", "--watch", "--dry-run", "--diff", "--check", "--format", "--no-wait");
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
        var roots = line.Choice("--roots", check ? "committed" : "all", "all", "committed", "built") switch
        {
            "committed" => RootSelection.Committed,
            "built" => RootSelection.Built,
            _ => RootSelection.All,
        };
        if (check && roots != RootSelection.Committed)
            throw new UsageException("--check covers committed roots only.");
        HandEditPolicy? handEdits = line.Value("--hand-edits") is null ? null : line.Choice("--hand-edits", "fail", "fail", "overwrite", "skip") switch
        {
            "overwrite" => HandEditPolicy.Overwrite,
            "skip" => HandEditPolicy.Skip,
            _ => HandEditPolicy.Fail,
        };
        var packs = line.Values("--pack");
        if (packs.Any(string.IsNullOrWhiteSpace))
            throw new UsageException("--pack needs a pack name.");

        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var request = new GenerationRequest
        {
            Mode = check ? GenerationMode.Check : dryRun ? GenerationMode.DryRun : GenerationMode.Apply,
            Packs = packs.Count > 0 ? [.. packs.Distinct(StringComparer.Ordinal)] : null,
            Force = line.Has("--force"),
            Jobs = context.Jobs,
            HandEdits = handEdits,
            IncludeDiffs = diff,
            Roots = roots,
            Lock = line.Has("--no-wait") ? LockMode.Fail : LockMode.Wait,
        };

        var options = context.EngineOptions(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var service = new GenerationService(store, options);
            if (watch)
                return await WatchAsync(context, service, request, options, ct).ConfigureAwait(false);

            var progress = new ConsoleProgress(context.Progress, context.Error);
            var result = await service.RunAsync(request, progress, ct).ConfigureAwait(false);
            progress.Complete();
            await ReportAsync(context, result, format, progress.FilesCompared, prefix: "").ConfigureAwait(false);
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
            await ReportAsync(context, result, "text", progress.TakeFilesCompared(), prefix: string.Create(CultureInfo.InvariantCulture, $"[watch] run {runs}: ")).ConfigureAwait(false);
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
            await ReportAsync(context, first, "text", progress.TakeFilesCompared(), prefix: "[watch] initial run: ").ConfigureAwait(false);
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

    private static async Task ReportAsync(GlobalContext context, GenerationResult result, string format, int filesCompared, string prefix)
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
        var produced = Count(FileChangeKind.Added, FileChangeKind.Modified, FileChangeKind.Kept, FileChangeKind.Conflict)
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
        RunOutcome.Drift => "Drift: committed output does not match the model; run maquettiste generate and commit the result (exit 2).",
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
