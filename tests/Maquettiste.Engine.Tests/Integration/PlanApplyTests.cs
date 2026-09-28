using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Plan, per-file diff and apply by plan id with the real renderer (integration task 7; host-contracts 29 to 31; engine-design.md
/// section 15): a plan writes nothing, apply writes exactly the planned bytes without rendering again, and apply is refused as
/// stale when an input or a planned path changed after planning.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class PlanApplyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plan_writes_nothing_serves_per_file_diffs_and_applies_by_id()
    {
        await using var repo = await CheckTests.AppliedAsync();
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var tree = repo.Tree();

        var planned = await repo.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);

        Assert.Equal(RunOutcome.Succeeded, planned.Outcome);
        var plan = planned.Plan!;
        FullRunTests.AssertSameBytes(tree, repo.Tree());
        Assert.Contains(plan.Changes, c => c.Path == "db/e2e/entities/customer.txt" && c.Kind == FileChangeKind.Modified);
        Assert.Contains(plan.Changes, c => c.Path == "db/e2e/tables/customers.sql" && c.Kind == FileChangeKind.Modified);
        var stored = await repo.Service.GetPlanAsync(plan.Id, Ct);
        Assert.NotNull(stored);
        Assert.Equal(plan.Units.Select(u => (u.Key, u.InputHash, u.Skipped)), stored.Units.Select(u => (u.Key, u.InputHash, u.Skipped)));
        Assert.Equal(plan.Changes.Select(c => (c.Path, c.Kind)), stored.Changes.Select(c => (c.Path, c.Kind)));

        var diff = await repo.Service.GetPlanDiffAsync(plan.Id, "db/e2e/entities/customer.txt", Ct);
        Assert.NotNull(diff);
        Assert.Contains("-  name: string (required) length 120\n", diff, StringComparison.Ordinal);
        Assert.Contains("+  name: string (required) length 150\n", diff, StringComparison.Ordinal);
        Assert.Contains("db/e2e/entities/customer.txt", diff, StringComparison.Ordinal);
        Assert.Equal("", await repo.Service.GetPlanDiffAsync(plan.Id, "db/e2e/entities/product.txt", Ct)); // planned, unchanged
        Assert.Null(await repo.Service.GetPlanDiffAsync(plan.Id, "db/e2e/not-planned.txt", Ct));
        Assert.Null(await repo.Service.GetPlanDiffAsync("01J00000000000000000000000", "db/e2e/entities/customer.txt", Ct));

        var applied = await repo.Service.ApplyAsync(plan.Id, null, Ct);

        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.Empty(applied.StaleUnits);
        Assert.Empty(applied.StalePaths);
        Assert.NotNull(applied.Result);
        Assert.Equal(plan.Changes.Where(c => c.Kind == FileChangeKind.Modified).Select(c => c.Path),
            applied.Result.Changes.Where(c => c.Kind == FileChangeKind.Modified).Select(c => c.Path));
        Assert.Contains("length 150", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal);

        // The apply saved unit state from the plan: a normal run afterwards renders nothing, and check is clean.
        var after = await repo.ApplyAsync();
        Assert.Equal(0, after.UnitsRendered);
        Assert.Empty(after.Changes);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));

        // Applying the same plan again writes nothing (a resumed or repeated apply is not stale; Generation README).
        var twice = await repo.Service.ApplyAsync(plan.Id, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, twice.Outcome);
        Assert.Equal(0, twice.Result!.FilesWritten);
    }

    [Fact]
    public async Task Apply_is_stale_when_an_input_changed_after_planning()
    {
        await using var repo = await CheckTests.AppliedAsync();
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var plan = (await repo.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!;
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 160);
        var tree = repo.Tree();

        var applied = await repo.Service.ApplyAsync(plan.Id, null, Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Contains("e2e/entity:" + E2ERepo.CustomerId, applied.StaleUnits);
        Assert.DoesNotContain("e2e/entity:" + E2ERepo.ProductId, applied.StaleUnits);
        FullRunTests.AssertSameBytes(tree, repo.Tree());
        Assert.False(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"));
    }

    [Fact]
    public async Task Apply_is_stale_when_a_planned_path_or_a_protected_region_was_edited_after_planning()
    {
        await using var repo = await CheckTests.AppliedAsync();
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var first = (await repo.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!;
        repo.Repo.WriteFile("db/e2e/entities/customer.txt", "edited by hand\n");

        var handEdit = await repo.Service.ApplyAsync(first.Id, null, Ct);

        Assert.Equal(RunOutcome.Stale, handEdit.Outcome);
        Assert.Equal(["db/e2e/entities/customer.txt"], handEdit.StalePaths);
        Assert.Equal("edited by hand\n", repo.Repo.ReadFile("db/e2e/entities/customer.txt"));

        // An edit inside a region of a planned (here unchanged, skipped) file: the plan merged the old body, so it is stale too.
        await repo.RunAsync(handEdits: HandEditPolicy.Overwrite);
        var second = (await repo.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!;
        Assert.Contains(second.Units.SelectMany(u => u.Outputs), f => f.Path == "db/e2e/regions/product.txt");
        var region = repo.Repo.ReadFile("db/e2e/regions/product.txt").Replace("// default body", "// kept by hand", StringComparison.Ordinal);
        repo.Repo.WriteFile("db/e2e/regions/product.txt", region);

        var regionEdit = await repo.Service.ApplyAsync(second.Id, null, Ct);

        Assert.Equal(RunOutcome.Stale, regionEdit.Outcome);
        Assert.Equal(["db/e2e/regions/product.txt"], regionEdit.StalePaths);
        Assert.Equal(region, repo.Repo.ReadFile("db/e2e/regions/product.txt"));
    }

    [Fact]
    public async Task Apply_is_stale_when_a_unit_appeared_after_planning()
    {
        await using var repo = await CheckTests.AppliedAsync();
        var plan = (await repo.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!;
        await repo.CreateElementAsync("""
            {"kind": "entity", "name": "Refund", "package": "01J92P0V01KDRN8GX5PGYCNKSX",
             "key": {"attributes": ["01J92P0VZZ0000000000000010"]},
             "attributes": [{"id": "01J92P0VZZ0000000000000010", "name": "id", "type": "uuid", "required": true}]}
            """);

        var applied = await repo.Service.ApplyAsync(plan.Id, null, Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Contains(applied.StaleUnits, k => k.StartsWith("e2e/entity:", StringComparison.Ordinal) && !plan.Units.Any(u => u.Key == k));
        Assert.Contains("e2e/index", applied.StaleUnits); // it lists every entity
        Assert.False(repo.Repo.Exists("db/e2e/entities/refund.txt"));
    }

    [Fact]
    public async Task A_dry_run_lists_diffs_and_writes_nothing()
    {
        await using var repo = await CheckTests.AppliedAsync();
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var tree = repo.Tree();
        var times = repo.WriteTimes();

        var dry = await repo.RunAsync(GenerationMode.DryRun, diffs: true);

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, dry);
        var change = Assert.Single(dry.Changes, c => c.Path == "db/e2e/entities/customer.txt");
        Assert.Equal(FileChangeKind.Modified, change.Kind);
        Assert.Contains("+  name: string (required) length 150", change.Diff, StringComparison.Ordinal);
        Assert.Equal(0, dry.FilesWritten);
        FullRunTests.AssertSameBytes(tree, repo.Tree());
        Assert.Equal(times, repo.WriteTimes());

        // The dry run saved no unit state: the next apply still renders the changed units.
        var applied = await repo.ApplyAsync();
        Assert.Contains(applied.Changes, c => c.Path == "db/e2e/entities/customer.txt" && c.Kind == FileChangeKind.Modified);
    }
}
