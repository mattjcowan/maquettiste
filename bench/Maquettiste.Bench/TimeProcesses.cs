using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Bench.Synthetic;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Bench;

/// <summary>One measured process budget (phase-3-design.md section 4.5).</summary>
/// <param name="Name">The measure.</param>
/// <param name="Limit">The budget, in milliseconds.</param>
/// <param name="Actual">The measured value, in milliseconds.</param>
public sealed record ProcessTiming(string Name, double Limit, double Actual)
{
    /// <summary>Whether the measure is within its budget.</summary>
    public bool Pass => Actual <= Limit;
}

/// <summary>
/// <c>Maquettiste.Bench time-processes</c>: the section 4.5 budgets on the synthetic model extended with processes and scenarios. The
/// extension lives only in this verb (the flag the design asks for), so the default bench model and its numbers do not move. Steps: the
/// base model's validate; the processes added (no scenarios) and validate again (the difference is the MQ90xx to MQ92xx cost); one
/// large process validated alone; the scenarios added and replayed at <c>--jobs</c>; a macrostep on a large chart; a guard evaluation on a
/// rented lease; a 200-input <c>simulate</c>; and the export and import of a large process.
/// </summary>
public static class TimeProcesses
{
    /// <summary>The verb.</summary>
    public const string Verb = "time-processes";

    /// <summary>The usage line.</summary>
    public const string Usage =
        "usage: Maquettiste.Bench time-processes [--out <dir>] [--entities 5000] [--processes 1000] [--large 5] [--scenarios 5000] [--jobs <cores>] [--rounds 3] [--keep]\n";

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after the verb.</param>
    /// <param name="output">Where the table goes.</param>
    /// <param name="error">Where usage errors go.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>0, or 4 for a usage error.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        string? folder = null;
        var keep = false;
        var model = new SyntheticModelOptions { IncludeExamplePacks = false };
        var processes = new SyntheticProcessOptions();
        var jobs = Environment.ProcessorCount;
        var rounds = 3;
        for (var i = 0; i < args.Count; i++)
        {
            var value = i + 1 < args.Count ? args[i + 1] : null;
            int? number = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
            switch (args[i])
            {
                case "--out" when value is not null: folder = value; i++; break;
                case "--keep": keep = true; break;
                case "--entities" when number >= 2: model = model with { Entities = n, Relations = Math.Min(model.Relations, n * 4) }; i++; break;
                case "--processes" when number is not null: processes = processes with { Processes = n, Large = Math.Min(processes.Large, n) }; i++; break;
                case "--large" when number is not null: processes = processes with { Large = n }; i++; break;
                case "--scenarios" when number is not null: processes = processes with { Scenarios = n }; i++; break;
                case "--jobs" when number is not null: jobs = n; i++; break;
                case "--rounds" when number is not null: rounds = n; i++; break;
                default:
                    await error.WriteAsync($"unknown or invalid option '{args[i]}'.\n{Usage}").ConfigureAwait(false);
                    return 4;
            }
        }

        var root = Path.GetFullPath(folder ?? Path.Combine(Path.GetTempPath(), "maquettiste-time-processes-" + Guid.NewGuid().ToString("N")));
        try
        {
            var timings = await MeasureAsync(root, model, processes, jobs, rounds, output, ct).ConfigureAwait(false);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{"measure",-58} {"budget ms",10} {"actual ms",10}")).ConfigureAwait(false);
            foreach (var t in timings)
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{t.Name,-58} {t.Limit,10:F2} {t.Actual,10:F3}  {(t.Pass ? "pass" : "MISS")}")).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (!keep && folder is null && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Writes the model and measures every budget.</summary>
    /// <param name="root">The repo root to write to.</param>
    /// <param name="model">The base model.</param>
    /// <param name="options">The process extension.</param>
    /// <param name="jobs">The parallelism.</param>
    /// <param name="rounds">Rounds of each whole-model validate (the fastest counts).</param>
    /// <param name="log">Progress lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The timings.</returns>
    public static async Task<IReadOnlyList<ProcessTiming>> MeasureAsync(string root, SyntheticModelOptions model, SyntheticProcessOptions options, int jobs, int rounds,
        TextWriter log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        var cache = Path.Combine(root, ".bench-cache");
        await SyntheticModelGenerator.WriteAsync(root, model, ct).ConfigureAwait(false);
        var baseline = await ValidateAsync(root, cache, jobs, rounds, ct).ConfigureAwait(false);
        var written = await SyntheticProcesses.WriteAsync(root, options, scenarios: false, ct).ConfigureAwait(false);
        var withProcesses = await ValidateAsync(root, cache, jobs, rounds, ct).ConfigureAwait(false);
        await SyntheticProcesses.WriteAsync(root, options, scenarios: true, ct).ConfigureAwait(false);
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"model: {model.Entities} entities, {options.Processes} processes ({options.Large} of {options.LargeStates} states), {options.Scenarios} scenarios of {options.Steps} steps, jobs {jobs}"))
            .ConfigureAwait(false);
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"validate: base {baseline.Ms:F0} ms ({baseline.Report.Errors} errors), with processes {withProcesses.Ms:F0} ms ({withProcesses.Report.Errors} errors)")).ConfigureAwait(false);

        var timings = new List<ProcessTiming>();
        var store = new ModelStore(new EngineOptions { RepoRoot = root, CacheDirectory = cache, MaxDegreeOfParallelism = jobs });
        await using (store.ConfigureAwait(false))
        {
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var large = written.FirstOrDefault(p => p.Guard is null) ?? written[0];
            var ordinary = written.FirstOrDefault(p => p.Guard is not null) ?? written[0];

            // The budgeted figure: MQ90xx-MQ92xx (ProcessRules, with the structural analysis) for the large process over a prebuilt
            // validation context, as a scoped validate runs them once its context exists.
            var context = new ValidationContext(snapshot, ModelValidator.ActiveDocuments(snapshot), new ReferenceWalker(), null);
            var largeDocument = snapshot.GetDocument(large.Id)!;
            var rules = new List<double>();
            for (var r = 0; r < Math.Max(5, rounds) + 1; r++)
            {
                var clock = Stopwatch.StartNew();
                ProcessRules.Validate(context, new Report(largeDocument));
                if (r > 0) // the first run warms the context's lazy indexes, which a scoped validate builds anyway
                    rules.Add(clock.Elapsed.TotalMilliseconds);
            }

            // One large process validated alone through the store (every rule plus the scoped run's fixed cost), for reference.
            var one = new List<double>();
            for (var r = 0; r < Math.Max(5, rounds); r++)
            {
                var clock = Stopwatch.StartNew();
                await store.ValidateAsync(new ValidationScope([large.Id], IncludeReferrers: false), ct).ConfigureAwait(false);
                one.Add(clock.Elapsed.TotalMilliseconds);
            }

            // The fixed cost of a scoped validate (context and peers over the whole model), for reading the figure above.
            var entity = snapshot.All<Entity>().OrderBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
            var fixedCost = new List<double>();
            for (var r = 0; entity is not null && r < Math.Max(5, rounds); r++)
            {
                var clock = Stopwatch.StartNew();
                await store.ValidateAsync(new ValidationScope([entity.Id], IncludeReferrers: false), ct).ConfigureAwait(false);
                fixedCost.Add(clock.Elapsed.TotalMilliseconds);
            }

            await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"scoped validate: the 400-state process {one.Min():F1} ms, one entity {(fixedCost.Count > 0 ? fixedCost.Min() : 0):F1} ms (the fixed cost of a scoped run)")).ConfigureAwait(false);

            // Every scenario replayed at --jobs, as validate replays them.
            var scenarios = snapshot.All<Scenario>();
            var replay = double.MaxValue;
            var failed = 0;
            for (var r = 0; r < rounds; r++)
            {
                var clock = Stopwatch.StartNew();
                var failures = 0;
                using (var runtime = new ProcessRuntime(snapshot, jobs, ct))
                {
                    Parallel.ForEach(scenarios, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, s =>
                    {
                        if (ScenarioReplayer.Replay(s, runtime) is not { Passed: true })
                            Interlocked.Increment(ref failures);
                    });
                }

                replay = Math.Min(replay, clock.Elapsed.TotalMilliseconds);
                failed = failures;
            }

            await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"replay: {scenarios.Count} scenarios, {failed} failed")).ConfigureAwait(false);

            timings.Add(new ProcessTiming("macrostep, 400-state chart, no expressions (p95)", 0.2, Macrostep(snapshot, large)));
            timings.Add(new ProcessTiming("guard evaluation, rented lease (p95)", 0.05, Guard(snapshot, ordinary, ct)));
            timings.Add(new ProcessTiming("simulate, 200 inputs, server time", 30, await SimulateAsync(store, ordinary, ct).ConfigureAwait(false)));
            timings.Add(new ProcessTiming("MQ90xx-MQ92xx, one 400-state process, rules only", 10, rules.Min()));
            timings.Add(new ProcessTiming("MQ90xx-MQ92xx, whole model, added to validate", 400, Math.Max(0, withProcesses.Ms - baseline.Ms)));
            timings.Add(new ProcessTiming(string.Create(CultureInfo.InvariantCulture, $"replay every scenario ({scenarios.Count} x {options.Steps}) at jobs {jobs}"), 3000, replay));
            var (export, import) = await ExportImportAsync(store, large, ct).ConfigureAwait(false);
            timings.Add(new ProcessTiming("export, 400-state process", 50, export));
            timings.Add(new ProcessTiming("import (dry run, into itself), 400-state process", 50, import));
        }

        return timings;
    }

    private static async Task<(double Ms, ValidationReport Report)> ValidateAsync(string root, string cache, int jobs, int rounds, CancellationToken ct)
    {
        var best = double.MaxValue;
        ValidationReport? report = null;
        for (var r = 0; r < rounds; r++)
        {
            var store = new ModelStore(new EngineOptions { RepoRoot = root, CacheDirectory = cache, MaxDegreeOfParallelism = jobs });
            await using (store.ConfigureAwait(false))
            {
                await store.LoadAsync(ct).ConfigureAwait(false);
                var clock = Stopwatch.StartNew();
                report = await store.ValidateAsync(new ValidationScope(), ct).ConfigureAwait(false);
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            }
        }

        return (best, report!);
    }

    // 2,000 next inputs on the large chart (a fresh instance each time the walk reaches the final state); the 95th percentile.
    private static double Macrostep(ModelSnapshot snapshot, SyntheticProcess process)
    {
        using var runtime = new ProcessRuntime(snapshot, 1, CancellationToken.None);
        var chart = runtime.Chart(process.Id)!;
        var input = new ScenarioStep { Id = "bench", Event = process.Next };
        var samples = new List<double>();
        StatechartInterpreter? interpreter = null;
        for (var i = 0; i < 2_500; i++)
        {
            if (interpreter is null || interpreter.IsFinal)
            {
                interpreter = new StatechartInterpreter(chart, runtime.Options("bench"));
                interpreter.Start();
            }

            var clock = Stopwatch.StartNew();
            interpreter.Step(input);
            if (i >= 500)
                samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        return P95(samples);
    }

    private static double Guard(ModelSnapshot snapshot, SyntheticProcess process, CancellationToken ct)
    {
        using var session = ProcessExpressions.Get(snapshot.Get<Process>(process.Id)!).Open(1, ct);
        var context = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["count"] = 3 });
        var ev = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["name"] = "next", ["actor"] = null, ["payload"] = new Dictionary<string, object>() });
        var samples = new List<double>();
        for (var i = 0; i < 3_000; i++)
        {
            var clock = Stopwatch.StartNew();
            session.Evaluate(process.Guard!, "Guard 'counted'", context, ev, "bench", ct);
            if (i >= 500)
                samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        return P95(samples);
    }

    private static async Task<double> SimulateAsync(ModelStore store, SyntheticProcess process, CancellationToken ct)
    {
        var steps = SyntheticProcesses.Inputs(process, 200).Select(n => JsonSerializer.SerializeToElement(n)).ToList();
        var request = new SimulateRequest { Steps = steps, From = 199 };
        var samples = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            var clock = Stopwatch.StartNew();
            var call = await store.SimulateProcessAsync(process.Id, request, ct).ConfigureAwait(false);
            if (call.Status != ProcessCallStatus.Ok)
                throw new BenchmarkException("simulate failed: " + call.Detail);
            if (i >= 2)
                samples.Add(clock.Elapsed.TotalMilliseconds);
        }

        return Median(samples);
    }

    private static async Task<(double Export, double Import)> ExportImportAsync(ModelStore store, SyntheticProcess process, CancellationToken ct)
    {
        var exports = new List<double>();
        var imports = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            var clock = Stopwatch.StartNew();
            var export = await store.ExportProcessAsync(process.Id, ct).ConfigureAwait(false);
            exports.Add(clock.Elapsed.TotalMilliseconds);
            var config = JsonSerializer.SerializeToElement(export.Value!.Json);
            clock.Restart();
            var import = await store.ImportProcessAsync(new ProcessImportRequest { Config = config, Into = process.Id }, true, ChangeSource.Cli, ct).ConfigureAwait(false);
            imports.Add(clock.Elapsed.TotalMilliseconds);
            if (import.Value is not { Created.Count: 0 })
                throw new BenchmarkException("the round trip of " + process.Name + " created ids.");
        }

        return (Median(exports.Skip(2).ToList()), Median(imports.Skip(2).ToList()));
    }

    private static double P95(List<double> samples)
    {
        samples.Sort();
        return samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
    }

    private static double Median(List<double> samples)
    {
        samples.Sort();
        return samples[samples.Count / 2];
    }
}
