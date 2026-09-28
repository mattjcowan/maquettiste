using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Bench.Tests;

public sealed class BenchmarkHarnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Small_run_reports_every_stage_and_renders_less_incrementally()
    {
        using var temp = new TempFolder();
        // Fanout only: the unit and file counts below assume one file per unit and the same units for an edit and its revert,
        // which the example packs' file blocks, once-mode migrations and schema-diff units do not guarantee.
        var options = new BenchmarkOptions { OutputDirectory = temp.Path, Jobs = 2, Model = BenchTestModels.Small() with { IncludeExamplePacks = false } };

        var report = await BenchmarkHarness.RunAsync(options, null, Ct);

        Assert.Equal(
            [PipelineStage.Load, PipelineStage.Validate, PipelineStage.Resolve, PipelineStage.Plan, PipelineStage.Skip, PipelineStage.Render,
                PipelineStage.PostProcess, PipelineStage.Write],
            report.ColdStages.Select(s => s.Stage));
        foreach (var stage in new[] { PipelineStage.Load, PipelineStage.Render, PipelineStage.Write })
            Assert.True(report.ColdStages.Single(s => s.Stage == stage).Wall > TimeSpan.Zero, stage + " took no time");
        Assert.Equal(
            [BenchmarkHarness.LoadValidateResolve, BenchmarkHarness.Plan, BenchmarkHarness.Render, BenchmarkHarness.PostProcessWrite,
                BenchmarkHarness.ColdTotal, BenchmarkHarness.Incremental, BenchmarkHarness.Files],
            report.Budgets.Select(b => b.Name));

        // 60 entities × 3 fanout units + 5 package indexes (more when the example packs are embedded).
        Assert.True(report.Files >= 185, "files: " + report.Files);
        // Every fanout unit writes one file; the embedded example packs add files through file blocks and once-mode units.
        Assert.True(report.Files >= report.ColdUnitsRendered, $"files {report.Files} < units {report.ColdUnitsRendered}");
        Assert.True(report.IncrementalUnitsRendered > 0);
        Assert.True(report.IncrementalUnitsRendered < report.ColdUnitsRendered,
            $"incremental rendered {report.IncrementalUnitsRendered} of {report.ColdUnitsRendered} units");
        Assert.True(report.IncrementalFilesWritten > 0);
        Assert.Equal(report.ColdUnitsRendered, report.IncrementalUnitsRendered + report.IncrementalUnitsSkipped);
        Assert.Equal("Succeeded", report.CheckOutcome);
        Assert.True(report.OutputsMatch);
        Assert.True(report.ColdTotal > TimeSpan.Zero && report.Incremental > TimeSpan.Zero && report.CheckTotal > TimeSpan.Zero);
        Assert.True(report.PeakWorkingSetBytes > 0);
        Assert.True(report.IncrementalFreshStore > TimeSpan.Zero);
        Assert.Equal(report.IncrementalUnitsRendered, report.IncrementalFreshStoreUnitsRendered); // the reverted edit touches the same units
        Assert.NotEmpty(report.IncrementalFreshStoreStages);
        Assert.Equal(2, report.Jobs);
        Assert.Equal(3, report.Fanout);
        Assert.Contains("fanout", report.Packs);
        Assert.NotEmpty(report.Notes);
        Assert.False(report.Budgets.Single(b => b.Name == BenchmarkHarness.Files).Pass);   // a small model is far from 100,000 files
        Assert.False(report.Passed);

        // The work folders are removed unless --keep.
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "staged")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "pipelined")));

        // The JSON report carries the budgets back.
        var json = BenchmarkReportJson.Write(report);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(BenchmarkReportJson.FormatVersion, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal(8, document.RootElement.GetProperty("cold").GetProperty("stages").GetArrayLength());
        var budgets = BenchmarkReportJson.ReadBudgets(json);
        Assert.Equal(report.Budgets.Select(b => b.Actual), report.Budgets.Select(b => budgets[b.Name]));
        Assert.Equal((byte)'\n', json[^1]);
        Assert.DoesNotContain((byte)'\r', json);
        Assert.Contains("Result: FAIL", BenchmarkReportJson.ToText(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Budgets_follow_spec_section_13()
    {
        StageTiming T(PipelineStage stage, double seconds) => new(stage, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds), 1);
        var budgets = BenchmarkHarness.Budgets(
            [T(PipelineStage.Load, 1), T(PipelineStage.Validate, 0.5), T(PipelineStage.Resolve, 1.6), T(PipelineStage.Plan, 1.5), T(PipelineStage.Skip, 0.4),
                T(PipelineStage.Render, 39), T(PipelineStage.PostProcess, 5), T(PipelineStage.Write, 9)],
            TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(1.9), 100_000);

        Assert.Equal([3, 2, 40, 15, 60, 2, 100_000], budgets.Select(b => b.Limit));
        Assert.Equal([3.1, 1.9, 39, 14, 61, 1.9, 100_000], budgets.Select(b => b.Actual));
        Assert.Equal([false, true, true, true, false, true, true], budgets.Select(b => b.Pass));
    }

    [Fact]
    public void Regressions_beyond_the_threshold_fail()
    {
        var budgets = new[]
        {
            new BudgetResult(BenchmarkHarness.Render, 40, 33.1, true),
            new BudgetResult(BenchmarkHarness.Plan, 2, 1.0, true),
            new BudgetResult(BenchmarkHarness.Incremental, 2, 0.30, true),
            new BudgetResult(BenchmarkHarness.Files, 100_000, 90_000, false) { AtLeast = true, Unit = "files" },
        };
        var baseline = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [BenchmarkHarness.Render] = 30,       // +10.3%: regression
            [BenchmarkHarness.Plan] = 0.95,       // +5.3%: fine
            [BenchmarkHarness.Incremental] = 0.26, // +15%, but only 40 ms: noise
            [BenchmarkHarness.Files] = 100_000,    // not a time: not compared
        };

        var results = BenchmarkHarness.Compare(budgets, baseline, 10);

        Assert.Equal([BenchmarkHarness.Render, BenchmarkHarness.Plan, BenchmarkHarness.Incremental], results.Select(r => r.Name));
        Assert.Equal([false, true, true], results.Select(r => r.Pass));
        Assert.Equal(10.3, results[0].ChangePercent);
    }

    [Fact]
    public void The_incremental_budget_has_a_wider_noise_floor()
    {
        var budgets = new[]
        {
            new BudgetResult(BenchmarkHarness.Incremental, 2, 1.80, true),
            new BudgetResult(BenchmarkHarness.Plan, 2, 0.70, true),
        };
        var baseline = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [BenchmarkHarness.Incremental] = 1.58, // +14%, 220 ms: within the incremental noise floor
            [BenchmarkHarness.Plan] = 0.58,        // +21%, 120 ms: a regression
        };

        var results = BenchmarkHarness.Compare(budgets, baseline, 10);

        Assert.Equal([true, false], results.Select(r => r.Pass));
        Assert.False(BenchmarkHarness.Compare([new BudgetResult(BenchmarkHarness.Incremental, 2, 1.90, true)], baseline, 10).Single().Pass);
    }

    [Fact]
    public async Task A_baseline_from_another_machine_skips_the_gate_instead_of_failing()
    {
        using var temp = new TempFolder();
        var report = new BenchmarkReport([], TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(1), 100_000, 4, 8, "test",
            [new BudgetResult(BenchmarkHarness.Render, 40, 30, true)], []) { CheckOutcome = "Succeeded", OutputsMatch = true, Architecture = "X64", ServerGc = true };
        var baselinePath = Path.Combine(temp.Path, "baseline.json");
        var baseline = report with { MachineCores = 24, Budgets = [new BudgetResult(BenchmarkHarness.Render, 40, 20, true)] };
        await File.WriteAllBytesAsync(baselinePath, BenchmarkReportJson.Write(baseline), Ct);

        var compared = await BenchmarkHarness.CompareAsync(report, baselinePath, 10, Ct);

        Assert.Empty(compared.Regressions);
        Assert.NotNull(compared.RegressionGateSkipped);
        Assert.Contains("cores 24 in the baseline, 4 here", compared.RegressionGateSkipped, StringComparison.Ordinal);
        Assert.Contains(compared.Notes, n => n.StartsWith("Regression gate skipped", StringComparison.Ordinal));
        Assert.True(compared.Passed);
        using var document = JsonDocument.Parse(BenchmarkReportJson.Write(compared));
        Assert.Equal(compared.RegressionGateSkipped, document.RootElement.GetProperty("regressionGateSkipped").GetString());
        Assert.Contains("Regression gate skipped", BenchmarkReportJson.ToText(compared), StringComparison.Ordinal);

        // Runtime overrides and the GC heap count are part of the machine too.
        var overridden = report with { RuntimeEnvironment = ["DOTNET_TieredPGO=1"] };
        Assert.Single(BenchmarkHarness.MachineDifferences(overridden, BenchmarkReportJson.ReadMachine(BenchmarkReportJson.Write(report))));
        Assert.Single(BenchmarkHarness.MachineDifferences(report with { GcHeapCount = 8 }, BenchmarkReportJson.ReadMachine(BenchmarkReportJson.Write(report))));
        Assert.Empty(BenchmarkHarness.MachineDifferences(report, BenchmarkReportJson.ReadMachine(BenchmarkReportJson.Write(report))));
    }

    [Fact]
    public void A_baseline_without_the_runtime_fields_is_compared_on_the_rest()
    {
        var json = """
            { "machine": { "cores": 24, "jobs": 8, "os": "Ubuntu", "architecture": "X64", "serverGc": true }, "budgets": [] }
            """u8;
        var machine = BenchmarkReportJson.ReadMachine(json);
        Assert.Null(machine.GcHeapCount);
        Assert.Null(machine.RuntimeEnvironment);
        var report = new BenchmarkReport([], TimeSpan.Zero, TimeSpan.Zero, 0, 24, 8, "Ubuntu", [], [])
        {
            Architecture = "X64", ServerGc = true, GcHeapCount = 8, RuntimeEnvironment = ["DOTNET_gcServer=1"],
        };
        Assert.Empty(BenchmarkHarness.MachineDifferences(report, machine));
        Assert.Throws<BenchmarkException>(() => BenchmarkReportJson.ReadMachine("{ \"budgets\": [] }"u8));
    }

    [Fact]
    public async Task Missing_baseline_is_an_error_and_an_existing_one_is_compared()
    {
        using var temp = new TempFolder();
        var report = new BenchmarkReport([], TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(1), 100_000, 8, 8, "test",
            [new BudgetResult(BenchmarkHarness.Render, 40, 30, true)], []) { CheckOutcome = "Succeeded", OutputsMatch = true };
        Assert.True(report.Passed);

        var none = Path.Combine(temp.Path, "none.json");
        var error = await Assert.ThrowsAsync<BenchmarkException>(() => BenchmarkHarness.CompareAsync(report, none, 10, Ct));
        Assert.Contains("No baseline", error.Message, StringComparison.Ordinal);

        // RunAsync refuses a missing baseline before it writes anything.
        var work = Path.Combine(temp.Path, "work");
        await Assert.ThrowsAsync<BenchmarkException>(() => BenchmarkHarness.RunAsync(
            new BenchmarkOptions { OutputDirectory = work, Jobs = 2, Model = BenchTestModels.Small(), BaselinePath = none }, null, Ct));
        Assert.False(Directory.Exists(Path.Combine(work, "staged")));

        var baselinePath = Path.Combine(temp.Path, "baseline.json");
        await File.WriteAllBytesAsync(baselinePath, BenchmarkReportJson.Write(report with { Budgets = [new BudgetResult(BenchmarkHarness.Render, 40, 20, true)] }), Ct);
        var compared = await BenchmarkHarness.CompareAsync(report, baselinePath, 10, Ct);
        var regression = Assert.Single(compared.Regressions);
        Assert.False(regression.Pass);
        Assert.Equal(50, regression.ChangePercent);
        Assert.False(compared.Passed);
    }

    [Fact]
    public void Differing_or_missing_cold_outputs_fail_the_run()
    {
        var report = new BenchmarkReport([], TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(1), 100_000, 8, 8, "test",
            [new BudgetResult(BenchmarkHarness.Render, 40, 30, true)], []) { CheckOutcome = "Succeeded", OutputsMatch = true };

        Assert.True(report.Passed);
        Assert.False((report with { OutputsMatch = false }).Passed);
        Assert.False((report with { CheckOutcome = "Drift" }).Passed);
    }

    [Fact]
    public async Task Work_folders_the_benchmark_did_not_create_are_never_deleted()
    {
        using var temp = new TempFolder();
        var staged = Directory.CreateDirectory(Path.Combine(temp.Path, "staged")).FullName;
        var pipelined = Directory.CreateDirectory(Path.Combine(temp.Path, "pipelined")).FullName;
        await File.WriteAllTextAsync(Path.Combine(staged, "important.txt"), "keep me\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(pipelined, "notes.txt"), "keep me too\n", Ct);
        var options = new BenchmarkOptions { OutputDirectory = temp.Path, Jobs = 2, Model = BenchTestModels.Small() };

        var error = await Assert.ThrowsAsync<BenchmarkException>(() => BenchmarkHarness.RunAsync(options, null, Ct));

        Assert.Contains(BenchmarkHarness.MarkerFile, error.Message, StringComparison.Ordinal);
        Assert.Equal("keep me\n", await File.ReadAllTextAsync(Path.Combine(staged, "important.txt"), Ct));
        Assert.Equal("keep me too\n", await File.ReadAllTextAsync(Path.Combine(pipelined, "notes.txt"), Ct));
        Assert.Equal(["important.txt"], BenchTestModels.Files(staged));
    }

    [Fact]
    public async Task A_kept_run_is_replaced_by_the_next_run()
    {
        using var temp = new TempFolder();
        var options = new BenchmarkOptions { OutputDirectory = temp.Path, Jobs = 2, Model = BenchTestModels.Small() with { Entities = 20, Relations = 30, Fanout = 1 }, Keep = true };

        await BenchmarkHarness.RunAsync(options, null, Ct);
        var staged = Path.Combine(temp.Path, "staged");
        Assert.True(File.Exists(Path.Combine(staged, BenchmarkHarness.MarkerFile)));
        await File.WriteAllTextAsync(Path.Combine(staged, "stale.txt"), "left over\n", Ct);

        await BenchmarkHarness.RunAsync(options with { Keep = false }, null, Ct);

        Assert.False(Directory.Exists(staged));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "pipelined")));
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_removes_the_work_folders()
    {
        using var temp = new TempFolder();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var options = new BenchmarkOptions { OutputDirectory = temp.Path, Jobs = 2, Model = BenchTestModels.Small() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BenchmarkHarness.RunAsync(options, new CancelOnFirst(cancel), cancel.Token));

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "staged")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "pipelined")));
    }

    [Fact]
    public async Task Invalid_model_options_exit_4_instead_of_crashing()
    {
        // The default 20,000 relations are too many for 100 entities.
        Assert.Equal(4, await Program.Main(["--entities", "100"]));
        await Assert.ThrowsAsync<BenchmarkException>(() => BenchmarkHarness.RunAsync(
            new BenchmarkOptions { Model = new SyntheticModelOptions { Entities = 100 } }, null, Ct));
    }

    [Fact]
    public async Task The_one_shot_figure_runs_fresh_processes_after_the_edit_and_with_nothing_changed()
    {
        using var temp = new TempFolder();
        var app = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Maquettiste.Bench.exe" : "Maquettiste.Bench");
        var options = new BenchmarkOptions
        {
            OutputDirectory = temp.Path,
            Jobs = 2,
            Keep = true,
            Model = BenchTestModels.Small() with { IncludeExamplePacks = false },
            OneShotCommand = [app, OneShotGenerate.Verb],
        };

        var report = await BenchmarkHarness.RunAsync(options, null, Ct);

        Assert.Equal(app + " " + OneShotGenerate.Verb, report.OneShotCommand);
        Assert.True(report.OneShotEdit > TimeSpan.Zero && report.OneShotNoOp > TimeSpan.Zero);
        Assert.True(File.Exists(Path.Combine(temp.Path, "pipelined", "cache", "last-run.v1.bin")), "the one-shot process keeps the last-run record");
        // The edit run is timed after a one-shot run that recorded, so it pays for rejecting the record as after a no-op build.
        Assert.Contains(report.Notes, n => n.StartsWith("The one-shot figures", StringComparison.Ordinal)
            && n.Contains("left a last-run record, so the edit figure includes rejecting it", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(BenchmarkReportJson.Write(report));
        var oneShot = document.RootElement.GetProperty("incremental").GetProperty("oneShot");
        Assert.Equal(report.OneShotCommand, oneShot.GetProperty("command").GetString());
        Assert.Equal(Math.Round(report.OneShotNoOp.TotalSeconds, 3), oneShot.GetProperty("noOpSeconds").GetDouble());
        Assert.Contains("One-shot process (not a budget): ", BenchmarkReportJson.ToText(report), StringComparison.Ordinal);

        // Without a command there is no figure (and no oneShot object).
        var plain = await BenchmarkHarness.RunAsync(options with { OneShotCommand = null, Keep = false }, null, Ct);
        Assert.Null(plain.OneShotCommand);
        using var plainJson = JsonDocument.Parse(BenchmarkReportJson.Write(plain));
        Assert.False(plainJson.RootElement.GetProperty("incremental").TryGetProperty("oneShot", out _));
    }

    [Fact]
    public async Task A_failing_one_shot_process_fails_the_benchmark_with_its_error()
    {
        using var temp = new TempFolder();
        var app = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Maquettiste.Bench.exe" : "Maquettiste.Bench");
        var options = new BenchmarkOptions
        {
            OutputDirectory = temp.Path,
            Jobs = 2,
            Model = BenchTestModels.Small() with { IncludeExamplePacks = false },
            OneShotCommand = [app, OneShotGenerate.Verb, "--bogus"],
        };

        var error = await Assert.ThrowsAsync<BenchmarkException>(() => BenchmarkHarness.RunAsync(options, null, Ct));
        Assert.Contains("exited 4", error.Message, StringComparison.Ordinal);
        Assert.Contains("usage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_report_is_refused_as_baseline() =>
        Assert.Throws<BenchmarkException>(() => BenchmarkReportJson.ReadBudgets("{\"x\":1}"u8));

    [Fact]
    public void Command_line_follows_design_section_17()
    {
        Assert.True(Program.TryParse(
            ["--out", "o", "--jobs", "4", "--seed", "9", "--entities", "100", "--relations", "300", "--enums", "12", "--fanout", "5", "--keep",
                "--baseline", "b.json", "--max-regression", "12.5", "--format", "json", "--report", "r.json", "--no-example-packs"],
            out var options, out var format, out var report, out _));
        Assert.Equal("o", options.OutputDirectory);
        Assert.Equal(4, options.Jobs);
        Assert.Equal(9, options.Model.Seed);
        Assert.Equal(100, options.Model.Entities);
        Assert.Equal(300, options.Model.Relations);
        Assert.Equal(12, options.Model.Enums);
        Assert.Equal(5, options.Model.Fanout);
        Assert.True(options.Keep);
        Assert.False(options.Model.IncludeExamplePacks);
        Assert.Equal("b.json", options.BaselinePath);
        Assert.Equal(12.5, options.MaxRegressionPercent);
        Assert.Equal("json", format);
        Assert.Equal("r.json", report);

        Assert.True(Program.TryParse([], out var defaults, out var text, out _, out _));
        Assert.Equal(new BenchmarkOptions(), defaults);
        Assert.Equal("text", text);

        Assert.Null(defaults.OneShotCommand); // Main fills in the bench app itself, unless --no-one-shot
        Assert.True(Program.TryParse(["--cli", "/tools/maquettiste"], out var cli, out _, out _, out _));
        Assert.Equal(["/tools/maquettiste"], cli.OneShotCommand);
        Assert.True(Program.TryParse(["--cli", "x", "--no-one-shot"], out var none, out _, out _, out _));
        Assert.Null(none.OneShotCommand);
        Assert.False(Program.TryParse(["--cli"], out _, out _, out _, out _));

        Assert.False(Program.TryParse(["--jobs", "0"], out _, out _, out _, out _));
        Assert.False(Program.TryParse(["--jobs"], out _, out _, out _, out _));
        Assert.False(Program.TryParse(["--format", "xml"], out _, out _, out _, out _));
        Assert.False(Program.TryParse(["--bogus"], out _, out _, out _, out _));
    }
}

/// <summary>Cancels a token on the first progress update.</summary>
internal sealed class CancelOnFirst(CancellationTokenSource cancel) : IProgress<ProgressUpdate>
{
    public void Report(ProgressUpdate value) => cancel.Cancel();
}
