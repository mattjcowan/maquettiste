using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Bench;

/// <summary>Options of the synthetic model (engine-design.md section 17; W11).</summary>
public sealed record SyntheticModelOptions
{
    /// <summary>The random seed.</summary>
    public int Seed { get; init; } = 42;

    /// <summary>Packages.</summary>
    public int Packages { get; init; } = 50;

    /// <summary>Entities.</summary>
    public int Entities { get; init; } = 5_000;

    /// <summary>Relations.</summary>
    public int Relations { get; init; } = 20_000;

    /// <summary>Enums.</summary>
    public int Enums { get; init; } = 500;

    /// <summary>Value objects.</summary>
    public int ValueObjects { get; init; } = 250;

    /// <summary>Custom scalar types.</summary>
    public int ScalarTypes { get; init; } = 50;

    /// <summary>Files per entity from the fanout pack; <see langword="null"/> sizes it to pass 100,000 files.</summary>
    public int? Fanout { get; init; }

    /// <summary>
    /// Whether to write the example packs (<c>sql-ddl</c>, <c>csharp-dapper</c>) next to <c>fanout</c>, as the design asks. Only packs
    /// embedded in the bench assembly are written; while they are skeletons, nothing is.
    /// </summary>
    public bool IncludeExamplePacks { get; init; } = true;
}

/// <summary>Benchmark options (the <c>maquettiste bench</c> flags).</summary>
public sealed record BenchmarkOptions
{
    /// <summary>
    /// The work folder (it receives <c>staged/</c> and <c>pipelined/</c>, each marked with <see cref="BenchmarkHarness.MarkerFile"/>);
    /// <see langword="null"/> means a temporary folder. An existing non-empty <c>staged/</c> or <c>pipelined/</c> without the marker
    /// is refused, never deleted.
    /// </summary>
    public string? OutputDirectory { get; init; }

    /// <summary>The engine parallelism cap.</summary>
    public int Jobs { get; init; } = 8;

    /// <summary>The model options.</summary>
    public SyntheticModelOptions Model { get; init; } = new();

    /// <summary>Whether to keep the generated repos.</summary>
    public bool Keep { get; init; }

    /// <summary>The baseline file to compare with, if any; a path that does not exist fails the run.</summary>
    public string? BaselinePath { get; init; }

    /// <summary>The allowed regression, in percent.</summary>
    public double MaxRegressionPercent { get; init; } = 10;

    /// <summary>
    /// The command that starts a fresh <c>maquettiste</c> process (the program and its leading arguments), for the one-shot figure:
    /// the harness appends <c>--repo &lt;repo&gt; --cache-dir &lt;cache&gt; --jobs &lt;n&gt; generate --quiet</c> and runs it over the
    /// pipelined repo, once after the one-entity edit and once with nothing changed (<see cref="BenchmarkReport.OneShotEdit"/>,
    /// <see cref="BenchmarkReport.OneShotNoOp"/>). <see langword="null"/> (the default) skips the figure. <c>maquettiste bench</c>
    /// passes its own executable; the bench app passes itself in its <c>one-shot-generate</c> mode, or the <c>--cli</c> it is given.
    /// </summary>
    public IReadOnlyList<string>? OneShotCommand { get; init; }
}

/// <summary>One budget check.</summary>
/// <param name="Name">The budget name.</param>
/// <param name="Limit">The limit.</param>
/// <param name="Actual">The measured value.</param>
/// <param name="Pass">Whether the measurement is within the limit.</param>
public sealed record BudgetResult(string Name, double Limit, double Actual, bool Pass)
{
    /// <summary>The unit of <see cref="Limit"/> and <see cref="Actual"/>: <c>s</c> (seconds) or <c>files</c>.</summary>
    public string Unit { get; init; } = "s";

    /// <summary>Whether the limit is a minimum (files) rather than a maximum (times).</summary>
    public bool AtLeast { get; init; }
}

/// <summary>One comparison with the baseline.</summary>
/// <param name="Name">The budget name.</param>
/// <param name="Baseline">The baseline value, in seconds.</param>
/// <param name="Actual">The measured value, in seconds.</param>
/// <param name="ChangePercent">The change against the baseline, in percent (positive is slower).</param>
/// <param name="Pass">Whether the change is within the allowed regression.</param>
public sealed record RegressionResult(string Name, double Baseline, double Actual, double ChangePercent, bool Pass);

/// <summary>The benchmark report.</summary>
/// <param name="ColdStages">Per-stage timings of the cold run with stage barriers.</param>
/// <param name="ColdTotal">The cold pipelined run's total.</param>
/// <param name="Incremental">The incremental run's total.</param>
/// <param name="Files">Files generated.</param>
/// <param name="MachineCores">Logical cores of the machine.</param>
/// <param name="Jobs">The parallelism used.</param>
/// <param name="OperatingSystem">The OS description.</param>
/// <param name="Budgets">Budget checks.</param>
/// <param name="Notes">Caveats, such as the approximation of <c>--jobs</c>.</param>
public sealed record BenchmarkReport(
    IReadOnlyList<StageTiming> ColdStages,
    TimeSpan ColdTotal,
    TimeSpan Incremental,
    int Files,
    int MachineCores,
    int Jobs,
    string OperatingSystem,
    IReadOnlyList<BudgetResult> Budgets,
    IReadOnlyList<string> Notes)
{
    /// <summary>The model seed.</summary>
    public int Seed { get; init; }

    /// <summary>Entities in the model.</summary>
    public int Entities { get; init; }

    /// <summary>Relations in the model.</summary>
    public int Relations { get; init; }

    /// <summary>Fanout units per entity.</summary>
    public int Fanout { get; init; }

    /// <summary>The packs the runs used, ordinal.</summary>
    public IReadOnlyList<string> Packs { get; init; } = [];

    /// <summary>Time to write one synthetic repo.</summary>
    public TimeSpan ModelWrite { get; init; }

    /// <summary>Units rendered by the cold (stage barrier) run.</summary>
    public int ColdUnitsRendered { get; init; }

    /// <summary>Per-stage timings of the cold pipelined run (stages overlap, so walls do not add up).</summary>
    public IReadOnlyList<StageTiming> ColdPipelinedStages { get; init; } = [];

    /// <summary>Per-stage timings of the incremental run.</summary>
    public IReadOnlyList<StageTiming> IncrementalStages { get; init; } = [];

    /// <summary>Units the incremental run rendered.</summary>
    public int IncrementalUnitsRendered { get; init; }

    /// <summary>Units the incremental run skipped.</summary>
    public int IncrementalUnitsSkipped { get; init; }

    /// <summary>Files the incremental run wrote.</summary>
    public int IncrementalFilesWritten { get; init; }

    /// <summary>
    /// A second one-entity edit (the first one reverted) run by a new <c>ModelStore</c> and <c>GenerationService</c> over the same
    /// repo and cache folder: a new host process's first incremental run, but with a warm JIT. Reported, not a budget (see the notes).
    /// </summary>
    public TimeSpan IncrementalFreshStore { get; init; }

    /// <summary>The garbage collector's pauses during the incremental run (<see cref="GC.GetTotalPauseDuration"/>): its main noise.</summary>
    public TimeSpan IncrementalGcPause { get; init; }

    /// <summary>The garbage collector's pauses during the <see cref="IncrementalFreshStore"/> run.</summary>
    public TimeSpan IncrementalFreshStoreGcPause { get; init; }

    /// <summary>Per-stage timings of <see cref="IncrementalFreshStore"/>.</summary>
    public IReadOnlyList<StageTiming> IncrementalFreshStoreStages { get; init; } = [];

    /// <summary>Units the <see cref="IncrementalFreshStore"/> run rendered.</summary>
    public int IncrementalFreshStoreUnitsRendered { get; init; }

    /// <summary>
    /// The one-shot figure's command (<see cref="BenchmarkOptions.OneShotCommand"/>, space-separated), or <see langword="null"/> when
    /// it was not measured.
    /// </summary>
    public string? OneShotCommand { get; init; }

    /// <summary>
    /// A fresh process's <c>generate</c> over the pipelined repo and its cache folder after the same one-entity edit (process start,
    /// JIT, index-cache load, full resolve, the render of the changed units, the writes): what CI and build integration pay for one
    /// changed entity. Reported, not a budget.
    /// </summary>
    public TimeSpan OneShotEdit { get; init; }

    /// <summary>
    /// A fresh process's <c>generate</c> right after <see cref="OneShotEdit"/>, with nothing changed: what every build pays when the
    /// model did not change (the engine's last-run record answers it). Reported, not a budget.
    /// </summary>
    public TimeSpan OneShotNoOp { get; init; }

    /// <summary>The <c>--check</c> run's total.</summary>
    public TimeSpan CheckTotal { get; init; }

    /// <summary>The <c>--check</c> run's outcome (<c>Succeeded</c> when nothing drifted).</summary>
    public string CheckOutcome { get; init; } = "";

    /// <summary>Units the check run rendered.</summary>
    public int CheckUnitsRendered { get; init; }

    /// <summary>Whether the stage-barrier and pipelined cold runs produced identical, non-empty manifests (a determinism check).</summary>
    public bool OutputsMatch { get; init; }

    /// <summary>The process's peak working set over the whole benchmark, in bytes.</summary>
    public long PeakWorkingSetBytes { get; init; }

    /// <summary>The .NET runtime.</summary>
    public string Framework { get; init; } = "";

    /// <summary>The process architecture.</summary>
    public string Architecture { get; init; } = "";

    /// <summary>Whether the server garbage collector was on.</summary>
    public bool ServerGc { get; init; }

    /// <summary>The server GC heap count the runtime was configured with (0: one per core).</summary>
    public int GcHeapCount { get; init; }

    /// <summary>The GC dynamic adaptation mode (DATAS; 0 off, 1 on).</summary>
    public int GcDynamicAdaptation { get; init; }

    /// <summary>
    /// The <c>DOTNET_</c> environment variables that override the process's GC or JIT settings (<c>DOTNET_gcServer=0</c> and the like),
    /// sorted; empty when the project's runtime settings apply unchanged.
    /// </summary>
    public IReadOnlyList<string> RuntimeEnvironment { get; init; } = [];

    /// <summary>
    /// Why the regression gate did not run although a baseline was given (the baseline was recorded on another machine class or
    /// runtime configuration), or <see langword="null"/>.
    /// </summary>
    public string? RegressionGateSkipped { get; init; }

    /// <summary>The baseline the report was compared with, if any.</summary>
    public string? Baseline { get; init; }

    /// <summary>Comparisons with the baseline; empty without one.</summary>
    public IReadOnlyList<RegressionResult> Regressions { get; init; } = [];

    /// <summary>
    /// Whether every budget and every regression check passed, the check run found no drift, and the stage-barrier and pipelined
    /// cold runs wrote identical manifests (<see cref="OutputsMatch"/>; SPEC section 21 gate 1 requires byte-identical output).
    /// </summary>
    public bool Passed => Budgets.All(b => b.Pass) && Regressions.All(r => r.Pass) && CheckOutcome == "Succeeded" && OutputsMatch;
}

/// <summary>The machine and runtime a benchmark report was measured on (the <c>machine</c> section of a report file).</summary>
/// <param name="Cores">Logical cores.</param>
/// <param name="Jobs">The <c>--jobs</c> value.</param>
/// <param name="OperatingSystem">The OS description.</param>
/// <param name="Architecture">The process architecture.</param>
/// <param name="ServerGc">Whether the server garbage collector was on.</param>
/// <param name="GcHeapCount">The configured GC heap count, when the report records it.</param>
/// <param name="RuntimeEnvironment">The runtime-overriding environment variables, when the report records them.</param>
public sealed record BenchmarkMachine(int Cores, int Jobs, string OperatingSystem, string Architecture, bool ServerGc, int? GcHeapCount,
    IReadOnlyList<string>? RuntimeEnvironment);

/// <summary>A benchmark run that could not complete (the synthetic model or a pack is invalid, or a run failed).</summary>
public sealed class BenchmarkException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BenchmarkException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public BenchmarkException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public BenchmarkException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
