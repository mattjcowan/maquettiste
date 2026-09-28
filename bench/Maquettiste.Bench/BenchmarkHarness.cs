using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Maquettiste.Bench.Synthetic;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Bench;

/// <summary>
/// Runs the benchmark (engine-design.md section 17; W11) over two identical synthetic repos: a cold full run with stage barriers
/// (per-stage numbers), a cold pipelined run (the total), an incremental run after editing one attribute of one entity, and a
/// <c>--check</c> run. Compares the stages with the SPEC section 13 budgets and, when given, with a baseline.
/// </summary>
public static class BenchmarkHarness
{
    /// <summary>Budget: load, validate and resolve.</summary>
    public const string LoadValidateResolve = "load-validate-resolve";

    /// <summary>Budget: plan (pack load, unit planning and the skip check).</summary>
    public const string Plan = "plan";

    /// <summary>Budget: render.</summary>
    public const string Render = "render";

    /// <summary>Budget: post-process and write.</summary>
    public const string PostProcessWrite = "post-process-write";

    /// <summary>Budget: the cold pipelined run end to end.</summary>
    public const string ColdTotal = "cold-total";

    /// <summary>Budget: the incremental run end to end.</summary>
    public const string Incremental = "incremental";

    /// <summary>Budget: files generated.</summary>
    public const string Files = "files";

    /// <summary>Differences below this many seconds never count as a regression (timer and scheduler noise).</summary>
    public const double RegressionNoiseSeconds = 0.05;

    /// <summary>
    /// The noise floor of the <see cref="Incremental"/> budget: a run of about 1.5 s whose run-to-run spread on the reference machine
    /// is about ±0.2 s (more than the 10% gate), so only a slowdown beyond this many seconds counts as a regression.
    /// </summary>
    public const double IncrementalRegressionNoiseSeconds = 0.25;

    /// <summary>
    /// The marker file the harness writes into every work folder it creates (<c>staged/</c>, <c>pipelined/</c>). A later run only
    /// replaces or removes a work folder that carries it, so a user's own <c>staged/</c> or <c>pipelined/</c> folder is never deleted.
    /// </summary>
    public const string MarkerFile = ".maquettiste-bench";

    /// <summary>Runs the benchmark.</summary>
    /// <param name="options">The options.</param>
    /// <param name="progress">Progress of every engine run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report.</returns>
    /// <exception cref="BenchmarkException">
    /// The model options are invalid, the baseline file is missing, a work folder under <see cref="BenchmarkOptions.OutputDirectory"/>
    /// exists but was not created by the benchmark, or a run did not succeed.
    /// </exception>
    public static async Task<BenchmarkReport> RunAsync(BenchmarkOptions options, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Jobs < 1)
            throw new BenchmarkException("--jobs must be at least 1.");
        try
        {
            SyntheticModel.Validate(options.Model);
        }
        catch (ArgumentException e)
        {
            throw new BenchmarkException("Invalid synthetic model: " + e.Message, e);
        }

        if (options.BaselinePath is { } baselinePath && !File.Exists(baselinePath))
            throw new BenchmarkException("No baseline at " + baselinePath + "; the regression gate cannot run (omit --baseline to skip it).");

        ct.ThrowIfCancellationRequested();
        var temporary = options.OutputDirectory is null;
        var work = temporary ? Directory.CreateTempSubdirectory("maquettiste-bench-").FullName : Path.GetFullPath(options.OutputDirectory!);
        var policy = WorkPolicy(work);
        var staged = Path.Combine(work, "staged");
        var pipelined = Path.Combine(work, "pipelined");
        var created = new List<string>();
        try
        {
            // Check both folders before touching either, then replace them.
            CheckReplaceable(policy, staged);
            CheckReplaceable(policy, pipelined);
            foreach (var folder in new[] { staged, pipelined })
            {
                PrepareFolder(policy, folder);
                created.Add(folder);
            }

            return await RunCoreAsync(options, policy, staged, pipelined, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!options.Keep)
            {
                foreach (var folder in created)
                    RemoveOwnFolder(policy, folder);
                if (temporary)
                    Directory.Delete(work, recursive: true);
            }
        }
    }

    private static async Task<BenchmarkReport> RunCoreAsync(BenchmarkOptions options, IOutputPathPolicy policy, string staged, string pipelined,
        IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        var model = options.Model;
        var clock = Stopwatch.StartNew();
        await SyntheticModelGenerator.WriteAsync(RepoOf(staged), model, ct).ConfigureAwait(false);
        var modelWrite = clock.Elapsed;
        await SyntheticModelGenerator.WriteAsync(RepoOf(pipelined), model, ct).ConfigureAwait(false);

        // 1. Cold, stage barriers: clean per-stage wall times.
        Settle();
        GenerationResult cold;
        await using (var session = new Session(policy, staged, options.Jobs))
            cold = (await session.RunAsync(GenerationMode.Apply, barriers: true, progress, ct).ConfigureAwait(false)).Result;
        var stagedManifests = ManifestDigest(staged);

        // 2. Cold, pipelined: the real end-to-end total. 3. Incremental after one edit. 4. Check.
        Settle();
        GenerationResult pipelinedRun, incremental, check;
        TimeSpan coldTotal, incrementalTotal, checkTotal, incrementalPause;
        bool outputsMatch;
        await using (var live = new Session(policy, pipelined, options.Jobs))
        {
            (pipelinedRun, coldTotal, _) = await live.RunAsync(GenerationMode.Apply, barriers: false, progress, ct).ConfigureAwait(false);
            var pipelinedManifests = ManifestDigest(pipelined);
            outputsMatch = stagedManifests is not null && string.Equals(stagedManifests, pipelinedManifests, StringComparison.Ordinal);

            await SyntheticModelGenerator.EditOneEntityAsync(RepoOf(pipelined), model, ct).ConfigureAwait(false);
            (incremental, incrementalTotal, incrementalPause) = await live.RunAsync(GenerationMode.Apply, barriers: false, progress, ct).ConfigureAwait(false);
            (check, checkTotal, _) = await live.RunAsync(GenerationMode.Check, barriers: false, progress, ct, requireSuccess: false).ConfigureAwait(false);
        }

        // 5. The edit reverted, run by a new store and service over the same repo and cache (a new host process's first run, JIT warm).
        await SyntheticModelGenerator.RevertEditAsync(RepoOf(pipelined), model, ct).ConfigureAwait(false);
        GenerationResult fresh;
        TimeSpan freshTotal, freshPause;
        await using (var reopened = new Session(policy, pipelined, options.Jobs))
            (fresh, freshTotal, freshPause) = await reopened.RunAsync(GenerationMode.Apply, barriers: false, progress, ct).ConfigureAwait(false);

        // 6. One-shot processes (reported, not budgets): the same one-entity edit, then nothing changed, each generated by a fresh
        //    process over the same repo and cache folder, as CI and build integration run it. An untimed one-shot run first leaves
        //    the last-run record a real edit after a no-op build finds and rejects, so the edit figure includes that check.
        TimeSpan oneShotEdit = TimeSpan.Zero, oneShotNoOp = TimeSpan.Zero;
        var oneShotRecorded = false;
        var oneShot = options.OneShotCommand is { Count: > 0 } command ? command : null;
        if (oneShot is not null)
        {
            await OneShotAsync(oneShot, policy, pipelined, options.Jobs, ct).ConfigureAwait(false);
            oneShotRecorded = File.Exists(Path.Combine(pipelined, "cache", RunRecord.FileName));
            await SyntheticModelGenerator.EditOneEntityAsync(RepoOf(pipelined), model, ct).ConfigureAwait(false);
            oneShotEdit = await OneShotAsync(oneShot, policy, pipelined, options.Jobs, ct).ConfigureAwait(false);
            oneShotNoOp = await OneShotAsync(oneShot, policy, pipelined, options.Jobs, ct).ConfigureAwait(false);
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var budgets = Budgets(cold.Timings, coldTotal, incrementalTotal, cold.FilesWritten);
        var notes = Notes(options, cold, check, outputsMatch);
        if (oneShot is not null)
        {
            notes.Add("The one-shot figures run a fresh process (" + string.Join(" ", oneShot) + ") over the pipelined repo and its cache folder: once "
                + "after the same one-entity edit, once with nothing changed. They include process start and JIT (ReadyToRun only in a packed tool) and "
                + "are reported, not budgets (bench/README.md). "
                + (oneShotRecorded
                    ? "An untimed one-shot run before the edit left a last-run record, so the edit figure includes rejecting it, as after a no-op build."
                    : "The untimed one-shot run before the edit left no last-run record, so the edit figure does not include rejecting one."));
        }

        var report = new BenchmarkReport(cold.Timings, coldTotal, incrementalTotal, cold.FilesWritten, Environment.ProcessorCount, options.Jobs,
            RuntimeInformation.OSDescription, budgets, notes)
        {
            Seed = model.Seed,
            Entities = model.Entities,
            Relations = model.Relations,
            Fanout = FanoutPack.EffectiveFanout(model),
            Packs = PacksOf(cold),
            ModelWrite = modelWrite,
            ColdUnitsRendered = cold.UnitsRendered,
            ColdPipelinedStages = pipelinedRun.Timings,
            IncrementalStages = incremental.Timings,
            IncrementalUnitsRendered = incremental.UnitsRendered,
            IncrementalUnitsSkipped = incremental.UnitsSkipped,
            IncrementalFilesWritten = incremental.FilesWritten,
            IncrementalGcPause = incrementalPause,
            IncrementalFreshStore = freshTotal,
            IncrementalFreshStoreGcPause = freshPause,
            IncrementalFreshStoreStages = fresh.Timings,
            IncrementalFreshStoreUnitsRendered = fresh.UnitsRendered,
            OneShotCommand = oneShot is null ? null : string.Join(" ", oneShot),
            OneShotEdit = oneShotEdit,
            OneShotNoOp = oneShotNoOp,
            CheckTotal = checkTotal,
            CheckOutcome = check.Outcome.ToString(),
            CheckUnitsRendered = check.UnitsRendered,
            OutputsMatch = outputsMatch,
            // PeakWorkingSet64 is not implemented on macOS (it reads as 0), so fall back to the current working set there.
            PeakWorkingSetBytes = Math.Max(process.PeakWorkingSet64, process.WorkingSet64),
            Framework = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ServerGc = GCSettings.IsServerGC,
            GcHeapCount = GcSetting("HeapCount"),
            GcDynamicAdaptation = GcSetting("GCDynamicAdaptationMode"),
            RuntimeEnvironment = RuntimeOverrides(),
        };

        return options.BaselinePath is { } baseline ? await CompareAsync(report, baseline, options.MaxRegressionPercent, ct).ConfigureAwait(false) : report;
    }

    /// <summary>Compares a report with a baseline report file (the JSON of <see cref="BenchmarkReportJson"/>).</summary>
    /// <param name="report">The report.</param>
    /// <param name="baselinePath">The baseline file.</param>
    /// <param name="maxRegressionPercent">The allowed regression, in percent.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report with <see cref="BenchmarkReport.Regressions"/> set.</returns>
    /// <exception cref="BenchmarkException">The baseline file does not exist or is not a report.</exception>
    public static async Task<BenchmarkReport> CompareAsync(BenchmarkReport report, string baselinePath, double maxRegressionPercent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrEmpty(baselinePath);
        if (!File.Exists(baselinePath))
            throw new BenchmarkException("No baseline at " + baselinePath + "; the regression gate cannot run (omit --baseline to skip it).");
        var bytes = await File.ReadAllBytesAsync(baselinePath, ct).ConfigureAwait(false);
        var baseline = BenchmarkReportJson.ReadBudgets(bytes);
        var differences = MachineDifferences(report, BenchmarkReportJson.ReadMachine(bytes));
        if (differences.Count > 0)
        {
            // Times from another machine class or runtime configuration are not comparable: the gate is skipped, not failed.
            var reason = "the baseline was recorded on a different machine or runtime (" + string.Join("; ", differences) +
                "); record a baseline on this machine class to enable the gate.";
            return report with { Baseline = baselinePath, Regressions = [], RegressionGateSkipped = reason, Notes = [.. report.Notes, "Regression gate skipped: " + reason] };
        }

        return report with { Baseline = baselinePath, Regressions = Compare(report.Budgets, baseline, maxRegressionPercent) };
    }

    /// <summary>
    /// How the machine and runtime of a report differ from a baseline's (cores, <c>--jobs</c>, OS, architecture, server GC, and the
    /// GC heap count and runtime overrides when the baseline records them); empty when the baseline is comparable.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <param name="baseline">The baseline's machine.</param>
    /// <returns>One description per difference.</returns>
    public static IReadOnlyList<string> MachineDifferences(BenchmarkReport report, BenchmarkMachine baseline)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(baseline);
        var differences = new List<string>();
        void Check<T>(string name, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {expected} in the baseline, {actual} here"));
        }

        Check("cores", baseline.Cores, report.MachineCores);
        Check("jobs", baseline.Jobs, report.Jobs);
        Check("os", baseline.OperatingSystem, report.OperatingSystem);
        Check("architecture", baseline.Architecture, report.Architecture);
        Check("serverGc", baseline.ServerGc, report.ServerGc);
        if (baseline.GcHeapCount is { } heaps)
            Check("gcHeapCount", heaps, report.GcHeapCount);
        if (baseline.RuntimeEnvironment is { } environment)
            Check("runtimeEnvironment", "[" + string.Join(" ", environment) + "]", "[" + string.Join(" ", report.RuntimeEnvironment) + "]");
        return differences;
    }

    /// <summary>
    /// Compares time budgets with baseline values: a regression is a slowdown beyond the allowed percentage and beyond
    /// <see cref="RegressionNoiseSeconds"/> (<see cref="IncrementalRegressionNoiseSeconds"/> for <see cref="Incremental"/>).
    /// </summary>
    /// <param name="budgets">The measured budgets.</param>
    /// <param name="baseline">Baseline actual values by budget name.</param>
    /// <param name="maxRegressionPercent">The allowed regression, in percent.</param>
    /// <returns>One result per time budget present in both, in budget order.</returns>
    public static IReadOnlyList<RegressionResult> Compare(IReadOnlyList<BudgetResult> budgets, IReadOnlyDictionary<string, double> baseline,
        double maxRegressionPercent)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(baseline);
        var results = new List<RegressionResult>();
        foreach (var budget in budgets)
        {
            if (budget.AtLeast || !baseline.TryGetValue(budget.Name, out var reference))
                continue;
            var change = reference > 0 ? (budget.Actual - reference) / reference * 100 : 0;
            var noise = budget.Name == Incremental ? IncrementalRegressionNoiseSeconds : RegressionNoiseSeconds;
            var pass = budget.Actual <= reference * (1 + maxRegressionPercent / 100) || budget.Actual - reference <= noise;
            results.Add(new RegressionResult(budget.Name, reference, budget.Actual, Math.Round(change, 1), pass));
        }

        return results;
    }

    /// <summary>The SPEC section 13 budgets for a set of cold timings.</summary>
    /// <param name="cold">The stage-barrier run's timings.</param>
    /// <param name="coldTotal">The pipelined run's total.</param>
    /// <param name="incremental">The incremental run's total.</param>
    /// <param name="files">Files the cold run wrote.</param>
    /// <returns>The budgets.</returns>
    public static IReadOnlyList<BudgetResult> Budgets(IReadOnlyList<StageTiming> cold, TimeSpan coldTotal, TimeSpan incremental, int files)
    {
        ArgumentNullException.ThrowIfNull(cold);
        double Wall(params PipelineStage[] stages) => Seconds(cold.Where(t => stages.Contains(t.Stage)).Aggregate(TimeSpan.Zero, (s, t) => s + StageTime(t)));
        BudgetResult Time(string name, double limit, double actual) => new(name, limit, actual, actual <= limit);

        return
        [
            Time(LoadValidateResolve, 3, Wall(PipelineStage.Load, PipelineStage.Validate, PipelineStage.Resolve)),
            Time(Plan, 2, Wall(PipelineStage.Plan, PipelineStage.Skip)),
            Time(Render, 40, Wall(PipelineStage.Render)),
            Time(PostProcessWrite, 15, Wall(PipelineStage.PostProcess, PipelineStage.Write)),
            Time(ColdTotal, 60, Seconds(coldTotal)),
            Time(Incremental, 2, Seconds(incremental)),
            new BudgetResult(Files, FanoutPack.TargetFiles, files, files >= FanoutPack.TargetFiles) { Unit = "files", AtLeast = true },
        ];
    }

    /// <summary>
    /// The time a stage took in a stage-barrier run. Post-processing runs units in parallel and measures busy time per unit, so its
    /// wall time counts. Every other stage is recorded as one or more sequential spans, whose sum is the busy time; the wall time
    /// would be wrong for plan, which the engine records twice (pack loading before resolve, unit planning after it), so its wall
    /// spans resolve too.
    /// </summary>
    /// <param name="timing">The stage timing.</param>
    /// <returns>The stage time.</returns>
    public static TimeSpan StageTime(StageTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        return timing.Stage == PipelineStage.PostProcess ? timing.Wall : timing.Busy;
    }

    private static double Seconds(TimeSpan span) => Math.Round(span.TotalSeconds, 3);

    private static int GcSetting(string name) =>
        GC.GetConfigurationVariables().TryGetValue(name, out var value) ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;

    /// <summary>The <c>DOTNET_</c> (or legacy <c>COMPlus_</c>) variables that override GC, JIT tiering or the processor count, sorted.</summary>
    private static IReadOnlyList<string> RuntimeOverrides()
    {
        var found = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            var bare = name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ? name[7..]
                : name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) ? name[8..] : null;
            if (bare is not null && (bare.StartsWith("GC", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("Tiered", StringComparison.OrdinalIgnoreCase)
                || bare.StartsWith("TC_", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("ReadyToRun", StringComparison.OrdinalIgnoreCase)
                || bare.Equals("PROCESSOR_COUNT", StringComparison.OrdinalIgnoreCase)))
                found.Add(name + "=" + entry.Value);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static List<string> Notes(BenchmarkOptions options, GenerationResult cold, GenerationResult check, bool outputsMatch)
    {
        var notes = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"--jobs {options.Jobs} caps the engine's render, post-processing, writer and loader parallelism on a {Environment.ProcessorCount}-core machine; GC and I/O threads are not capped, so this approximates an {options.Jobs}-core machine."),
            "Stage budgets come from the cold run with stage barriers; the cold total from a second cold run (fresh repo and cache) with the stages pipelined, in the same process, so it runs with a warm JIT.",
            "The incremental run edits one attribute of one entity on disk and runs again with the same model store (the editor and watch scenario, which the 2 s budget covers); its time includes the rescan, validation, resolution, planning and skip check.",
            "The new-store incremental run reverts that edit and runs with a new model store and generation service over the same repo and cache (a new host process's first run, but with a warm JIT); it is reported, not a budget. A one-shot CLI process also pays process start and JIT (see bench/README.md).",
            "Peak working set is the whole process's peak over every run.",
            "Stage times are busy times (the sum of each stage's spans), except post-processing, whose units run in parallel, which counts wall time: the engine records the plan stage twice, pack loading before resolve and unit planning after it, so plan's wall time also covers resolve.",
        };
        if (cold.Diagnostics.Count > 0)
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"The cold run reported {cold.Diagnostics.Count} warnings or infos; first: {Describe(cold.Diagnostics[0])}"));
        if (check.Outcome != RunOutcome.Succeeded)
        {
            var first = check.Changes.Take(5).Select(c => c.Kind + " " + c.Path).Concat(check.Diagnostics.Take(5).Select(Describe));
            notes.Add("The check run after the incremental run was " + check.Outcome + ": " + string.Join("; ", first));
        }

        if (!outputsMatch)
            notes.Add("The stage-barrier and pipelined cold runs wrote different manifests, or none: output is not deterministic.");
        return notes;
    }

    /// <summary>
    /// Runs one fresh <c>generate</c> process over a work folder's repo and cache and returns its wall time (process start to exit).
    /// A non-zero exit is a <see cref="BenchmarkException"/> with the end of its stderr; cancellation kills the process.
    /// </summary>
    private static async Task<TimeSpan> OneShotAsync(IReadOnlyList<string> command, IOutputPathPolicy policy, string folder, int jobs, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var argument in command.Skip(1))
            start.ArgumentList.Add(argument);
        foreach (var argument in (string[])["--repo", RepoOf(folder), "--cache-dir", Guarded(policy, Path.Combine(folder, "cache")), "--jobs",
                     jobs.ToString(CultureInfo.InvariantCulture), "generate", "--quiet"])
            start.ArgumentList.Add(argument);

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new BenchmarkException("Could not start " + command[0] + ".");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var elapsed = clock.Elapsed;
        await output.ConfigureAwait(false);
        var stderr = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var tail = stderr.Length > 2000 ? stderr[^2000..] : stderr;
            throw new BenchmarkException(string.Create(CultureInfo.InvariantCulture,
                $"The one-shot generate ({string.Join(" ", command)}) exited {process.ExitCode}:\n{tail}"));
        }

        return elapsed;
    }

    private static IReadOnlyList<string> PacksOf(GenerationResult result) =>
        [.. result.Changes.Select(c => c.Pack).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string Describe(Diagnostic d) => d.Rule + " " + d.Severity + " " + (d.FilePath ?? d.ElementId ?? "") + ": " + d.Message;

    private static string RepoOf(string folder) => Path.Combine(folder, "repo");

    /// <summary>
    /// A digest of every manifest file (committed and built), so two runs can be compared without keeping the files;
    /// <see langword="null"/> when the run wrote no manifest, which never counts as a match.
    /// </summary>
    private static string? ManifestDigest(string folder)
    {
        var repo = RepoOf(folder);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var found = false;
        foreach (var dir in new[] { Path.Combine(repo, ".maquettiste", "manifest"), Path.Combine(repo, ".maquettiste", ".cache", "manifest") })
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
            {
                found = true;
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(repo, file).Replace('\\', '/') + "\n"));
                hash.AppendData(File.ReadAllBytes(file));
            }
        }

        return found ? Convert.ToHexStringLower(hash.GetHashAndReset()) : null;
    }

    private static void Settle()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>The engine-write guard for the work folder: every folder the harness creates or deletes lies under it.</summary>
    private static OutputPathPolicy WorkPolicy(string work) =>
        new(new EngineOptions { RepoRoot = work, CacheDirectory = work, JournalDirectory = work }, null);

    private static string Guarded(IOutputPathPolicy policy, string path)
    {
        var check = policy.CheckEngineWrite(WriteTarget.Cache, path);
        if (!check.Allowed)
            throw new BenchmarkException("The engine-write guard refused " + path + ": " + check.Reason);
        return check.NormalizedPath;
    }

    /// <summary>Refuses a work folder that exists, is not empty and lacks <see cref="MarkerFile"/>: the benchmark did not create it.</summary>
    private static void CheckReplaceable(IOutputPathPolicy policy, string folder)
    {
        var path = Guarded(policy, folder);
        if (File.Exists(path))
            throw new BenchmarkException(path + " is a file; the benchmark needs that path for a work folder. Choose another --out.");
        if (Directory.Exists(path) && !File.Exists(Path.Combine(path, MarkerFile)) && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new BenchmarkException(path + " exists and was not created by the benchmark (it has no " + MarkerFile +
                " file); the benchmark will not delete it. Choose another --out or remove the folder.");
        }
    }

    /// <summary>Replaces a work folder the benchmark created earlier (or an empty one) with an empty one carrying the marker.</summary>
    private static void PrepareFolder(IOutputPathPolicy policy, string folder)
    {
        var path = Guarded(policy, folder);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Guarded(policy, Path.Combine(path, MarkerFile)), "Created by the Maquettiste benchmark; deleted by its next run.\n"u8.ToArray());
    }

    /// <summary>Removes a work folder, only when it still carries the marker.</summary>
    private static void RemoveOwnFolder(IOutputPathPolicy policy, string folder)
    {
        var path = Guarded(policy, folder);
        if (File.Exists(Path.Combine(path, MarkerFile)))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>One repo with its own cache folder, model store and generation service.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly ModelStore _store;
        private readonly GenerationService _service;
        private readonly int _jobs;

        public Session(IOutputPathPolicy policy, string folder, int jobs)
        {
            var cache = Guarded(policy, Path.Combine(folder, "cache"));
            Directory.CreateDirectory(cache);
            var options = new EngineOptions { RepoRoot = RepoOf(folder), CacheDirectory = cache, MaxDegreeOfParallelism = jobs };
            _store = new ModelStore(options);
            _service = new GenerationService(_store, options);
            _jobs = jobs;
        }

        public async Task<(GenerationResult Result, TimeSpan Total, TimeSpan GcPause)> RunAsync(GenerationMode mode, bool barriers, IProgress<ProgressUpdate>? progress,
            CancellationToken ct, bool requireSuccess = true)
        {
            var request = new GenerationRequest { Mode = mode, Jobs = _jobs, StageBarriers = barriers, Lock = LockMode.Fail };
            var clock = Stopwatch.StartNew();
            var pausedBefore = GC.GetTotalPauseDuration();
            var result = await _service.RunAsync(request, progress, ct).ConfigureAwait(false);
            var total = clock.Elapsed;
            var paused = GC.GetTotalPauseDuration() - pausedBefore;

            // The engine reports cancellation as an outcome; the benchmark surfaces it as the exception its callers expect.
            if (result.Outcome == RunOutcome.Cancelled)
                throw new OperationCanceledException("The " + mode + " run was cancelled.", ct);
            if (requireSuccess && result.Outcome != RunOutcome.Succeeded)
            {
                var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10).Select(Describe).ToList();
                throw new BenchmarkException($"The {mode} run ended {result.Outcome}" + (errors.Count > 0 ? ":\n" + string.Join("\n", errors) : "."));
            }

            return (result, total, paused);
        }

        public ValueTask DisposeAsync() => _store.DisposeAsync();
    }
}
