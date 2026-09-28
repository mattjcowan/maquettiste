using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Generation;

public sealed class GenerationRunTests
{
    [Fact]
    public async Task Apply_writes_outputs_and_manifest_then_skips_everything()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var first = await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, first.Outcome);
        Assert.DoesNotContain(first.Diagnostics, d => d.Severity == Diagnostics.DiagnosticSeverity.Error);
        Assert.Equal(3, first.UnitsRendered);
        Assert.Equal(3, first.FilesWritten);
        var outputs = f.Outputs();
        Assert.Equal(["out/entities/Customer.txt", "out/entities/Product.txt", "out/index.txt"], outputs.Keys);
        Assert.Equal("entity Customer\nattributes: id,name\ntable: customers\n", outputs["out/entities/Customer.txt"]);
        Assert.Equal("hello\nCustomer Product\n", outputs["out/index.txt"]);
        Assert.Contains("out/entities/Customer.txt", f.CommittedManifest("basic"), StringComparison.Ordinal);

        f.Renderer = new FakeRenderer();
        var second = await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, second.Outcome);
        Assert.Equal(0, second.UnitsRendered);
        Assert.Equal(3, second.UnitsSkipped);
        Assert.Equal(0, second.FilesWritten);
        Assert.Empty(second.Changes);
        Assert.Empty(f.Renderer.Rendered);
    }

    [Fact]
    public async Task Stage_timings_cover_every_stage()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var result = await f.RunAsync();
        var stages = result.Timings.Select(t => t.Stage).ToList();
        Assert.Equal([PipelineStage.Load, PipelineStage.Validate, PipelineStage.Resolve, PipelineStage.Plan, PipelineStage.Skip,
            PipelineStage.Render, PipelineStage.PostProcess, PipelineStage.Write], stages);
    }

    [Fact]
    public async Task Progress_is_reported_per_stage()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var updates = new List<ProgressUpdate>();
        var progress = new SyncProgress(u =>
        {
            lock (updates)
                updates.Add(u);
        });
        await f.RunAsync(progress: progress);
        List<PipelineStage> seen;
        lock (updates)
            seen = [.. updates.Select(u => u.Stage).Distinct()];
        foreach (var stage in Enum.GetValues<PipelineStage>())
            Assert.Contains(stage, seen);
    }

    [Fact]
    public async Task Stage_barriers_give_the_same_output()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var result = await f.Service.RunAsync(new GenerationRequest { StageBarriers = true, Jobs = 3 }, null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.FilesWritten);
        Assert.Equal(3, f.Renderer.LastParallelism);
        Assert.Equal("hello\nCustomer Product\n", f.Outputs()["out/index.txt"]);
    }

    [Fact]
    public async Task Dry_run_writes_nothing_and_lists_diffs()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var result = await f.RunAsync(GenerationMode.DryRun);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, result.FilesWritten);
        Assert.Equal(3, result.Changes.Count);
        Assert.All(result.Changes, c => Assert.Equal(FileChangeKind.Added, c.Kind));
        Assert.All(result.Changes, c => Assert.NotNull(c.Diff));
        Assert.Empty(f.Outputs());
        Assert.Equal("", f.CommittedManifest("basic"));
        Assert.False(Directory.Exists(Path.Combine(f.Repo.CacheDirectory, "units")));
    }

    [Fact]
    public async Task Force_renders_every_unit_and_writes_nothing_new()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.Renderer = new FakeRenderer();
        var forced = await f.RunAsync(force: true);
        Assert.Equal(3, forced.UnitsRendered);
        Assert.Equal(0, forced.UnitsSkipped);
        Assert.Equal(0, forced.FilesWritten);
        Assert.Empty(forced.Changes);
    }

    [Fact]
    public async Task Validation_errors_stop_the_run_before_anything_is_written()
    {
        await using var f = await GenerationFixture.CreateAsync(b =>
        {
            b.Entity("Keyless").Attr("name", "string");
        }, "basic");
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ3005");
        Assert.Empty(f.Renderer.Rendered);
        Assert.Empty(f.Outputs());
    }

    [Fact]
    public async Task A_render_error_is_invalid_and_keeps_the_unit_state()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.WritePackFile("basic", "index.tpl", "{{fail}}\n");
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6006");
        Assert.True(f.Repo.Exists("out/index.txt"));
        Assert.Equal(["basic/index"], f.Renderer.Rendered);
    }

    [Fact]
    public async Task Pack_errors_stop_the_run_so_outputs_are_never_orphaned()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.WritePackFile("basic", "pack.json", "{ \"name\": \"basic\", \"version\": \"1.0.0\", \"engine\": \">=1.0\", \"units\": [] }");
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6001");
        Assert.Equal(3, f.Outputs().Count);
    }

    [Fact]
    public async Task Busy_lock_with_fail_mode_returns_busy()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await using (await f.Services.RunLock.AcquireAsync(false, GenerationFixture.Ct))
        {
            var result = await f.Service.RunAsync(new GenerationRequest { Lock = LockMode.Fail }, null, GenerationFixture.Ct);
            Assert.Equal(RunOutcome.Busy, result.Outcome);
        }
    }

    [Fact]
    public async Task Cancellation_returns_cancelled_within_a_second()
    {
        await using var f = await GenerationFixture.CreateAsync(b =>
        {
            for (var i = 0; i < 30; i++)
                b.Entity("Thing" + i).Key("id", "uuid");
        }, "basic");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(GenerationFixture.Ct);
        var started = new TaskCompletionSource();
        f.Renderer.BeforeUnit = async (unit, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(20, ct);
        };
        var run = f.RunAsync(ct: cts.Token);
        await started.Task;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await cts.CancelAsync();
        var result = await run;
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());
        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Jobs_setting_reaches_the_renderer()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.Service.RunAsync(new GenerationRequest { Jobs = 5 }, null, GenerationFixture.Ct);
        Assert.Equal(5, f.Renderer.LastParallelism);
    }

    [Fact]
    public async Task Pack_filter_runs_only_that_pack()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.CopyPack("basic", "other");
        var result = await f.RunAsync(packs: ["other"]);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.All(f.Renderer.Rendered, key => Assert.StartsWith("other/", key, StringComparison.Ordinal));
        Assert.Equal("", f.CommittedManifest("basic"));
        Assert.Contains("out/index.txt", f.CommittedManifest("other"), StringComparison.Ordinal);
    }
}

/// <summary>An <see cref="IProgress{T}"/> that reports synchronously.</summary>
internal sealed class SyncProgress(Action<ProgressUpdate> report) : IProgress<ProgressUpdate>
{
    public void Report(ProgressUpdate value) => report(value);
}
