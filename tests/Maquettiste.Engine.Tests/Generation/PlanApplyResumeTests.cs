using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>Applying a plan again after an interruption or a finished apply, and write settings pinned by the plan.</summary>
public sealed class PlanApplyResumeTests
{
    [Fact]
    public async Task An_interrupted_plan_apply_resumes_when_the_same_plan_is_applied_again()
    {
        await using var f = await GenerationFixture.CreateAsync(Many(60), "basic");
        var plan = (await f.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, GenerationFixture.Ct)).Plan!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(GenerationFixture.Ct);
        var progress = new SyncProgress(update =>
        {
            if (update.Stage == PipelineStage.Write && update.Done >= 10)
                cts.Cancel();
        });

        var first = await f.Service.ApplyAsync(plan.Id, progress, cts.Token);
        Assert.Equal(RunOutcome.Cancelled, first.Outcome);
        Assert.InRange(f.Outputs().Count, 1, 60);
        Assert.True(File.Exists(Path.Combine(f.Repo.ModelRoot, ".cache", "journal.jsonl")));

        var again = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Succeeded, again.Outcome);
        Assert.Empty(again.StalePaths);
        Assert.DoesNotContain(again.Result!.Changes, c => c.Kind is FileChangeKind.HandEdited or FileChangeKind.Conflict);
        Assert.Equal(61, f.Outputs().Count);
        Assert.False(File.Exists(Path.Combine(f.Repo.ModelRoot, ".cache", "journal.jsonl")));
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync(GenerationMode.Check)).Outcome);
    }

    [Fact]
    public async Task A_finished_plan_applied_again_writes_nothing()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        Assert.Equal(RunOutcome.Succeeded, (await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct)).Outcome);

        var again = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Succeeded, again.Outcome);
        Assert.Equal(0, again.Result!.FilesWritten);
    }

    [Fact]
    public async Task A_file_the_plan_writes_that_was_then_edited_is_still_stale()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        Assert.Equal(RunOutcome.Succeeded, (await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct)).Outcome);
        f.Repo.WriteFile("out/entities/Product.txt", "edited after the apply\n");

        var again = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Stale, again.Outcome);
        Assert.Equal(["out/entities/Product.txt"], again.StalePaths);
    }

    [Fact]
    public async Task A_hand_edit_policy_change_after_planning_makes_the_plan_stale()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.Repo.WriteFile("out/entities/Product.txt", "hand edit\n");
        var planned = await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Conflicts, planned.Outcome);
        Assert.Contains(planned.Plan!.Changes, c => c.Path == "out/entities/Product.txt" && c.Kind == FileChangeKind.Conflict);
        await f.WriteModelAsync(b => Models.Shop(b), s => s with { HandEdits = HandEditPolicy.Overwrite });

        var applied = await f.Service.ApplyAsync(planned.Plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Equal(["out/entities/Product.txt"], applied.StalePaths);
        Assert.Empty(applied.StaleUnits);
        Assert.Equal("hand edit\n", f.Repo.ReadFile("out/entities/Product.txt"));
    }

    [Fact]
    public async Task A_policy_change_without_hand_edits_in_the_plan_does_not_matter()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var planned = await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct);
        await f.WriteModelAsync(b => Models.Shop(b), s => s with { HandEdits = HandEditPolicy.Overwrite });

        var applied = await f.Service.ApplyAsync(planned.Plan!.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
    }

    [Fact]
    public async Task A_path_that_moved_to_another_root_after_planning_is_stale()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var planned = await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct);
        await f.WriteModelAsync(b => Models.Shop(b), s => s with
        {
            Outputs = new OutputSettings { Allow = [new OutputRoot { Path = "out" }, new OutputRoot { Path = "out/entities" }, new OutputRoot { Path = "gen" }] },
        });

        var applied = await f.Service.ApplyAsync(planned.Plan!.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Contains(applied.StalePaths, p => p.StartsWith("out/entities/", StringComparison.Ordinal));
        Assert.DoesNotContain("out/index.txt", applied.StalePaths);
        Assert.Empty(f.Outputs());
    }

    private static Action<ModelBuilder> Many(int count) => b =>
    {
        for (var i = 0; i < count; i++)
            b.Entity("Thing" + i).Key("id", "uuid");
        b.Database("main", Dialect.PostgreSql);
    };
}
