using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>Plan and apply (engine-design.md section 15, host-contracts 29 to 31).</summary>
public sealed class PlanApplyTests
{
    [Fact]
    public async Task Plan_writes_nothing_and_persists_every_unit_with_its_outputs()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var result = await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        var plan = Assert.IsType<GenerationPlan>(result.Plan);
        Assert.Empty(f.Outputs());
        Assert.Equal(3, plan.Units.Count);
        Assert.All(plan.Units, u => Assert.False(u.Skipped));
        Assert.All(plan.Units.SelectMany(u => u.Outputs), o => Assert.Null(o.DiskHashAtPlan));
        Assert.Equal(3, plan.Changes.Count(c => c.Kind == FileChangeKind.Added));
        Assert.Equal(["basic"], plan.Packs);

        var stored = await f.Service.GetPlanAsync(plan.Id, GenerationFixture.Ct);
        Assert.NotNull(stored);
        Assert.Equal(JsonSerializer.Serialize(plan, Web), JsonSerializer.Serialize(stored, Web));
    }

    [Fact]
    public async Task Plan_diff_comes_from_the_stored_bytes()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        f.Renderer = new FakeRenderer();

        var diff = await f.Service.GetPlanDiffAsync(plan.Id, "out/index.txt", GenerationFixture.Ct);

        Assert.NotNull(diff);
        Assert.Contains("+hello", diff, StringComparison.Ordinal);
        Assert.Empty(f.Renderer.Rendered);
        Assert.Null(await f.Service.GetPlanDiffAsync(plan.Id, "out/nothing.txt", GenerationFixture.Ct));
        Assert.Null(await f.Service.GetPlanDiffAsync("01ARZ3NDEKTSV4RRFFQ69G5FAV", "out/index.txt", GenerationFixture.Ct));
    }

    [Fact]
    public async Task Apply_writes_the_planned_bytes_without_rendering_and_saves_unit_state()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        f.Renderer = new FakeRenderer();

        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.Empty(f.Renderer.Rendered);
        Assert.Equal(3, applied.Result!.FilesWritten);
        Assert.Equal("hello\nCustomer Product\n", f.Outputs()["out/index.txt"]);

        var next = await f.RunAsync();
        Assert.Equal(3, next.UnitsSkipped);
        Assert.Empty(next.Changes);
    }

    [Fact]
    public async Task Apply_equals_a_direct_run()
    {
        await using var planned = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic", "modes");
        await using var direct = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic", "modes");
        var plan = (await planned.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        Assert.Equal(RunOutcome.Succeeded, (await planned.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct)).Outcome);
        await direct.RunAsync();
        Assert.Equal(direct.Outputs(), planned.Outputs());
        Assert.Equal(direct.CommittedManifest("modes"), planned.CommittedManifest("modes"));
    }

    [Fact]
    public async Task Apply_is_stale_when_an_input_changed_since_planning()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        await f.WriteModelAsync(b => Models.Shop(b, customerName: "text"));

        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Contains("basic/index", applied.StaleUnits);
        Assert.Equal(2, applied.StaleUnits.Count);
        Assert.Null(applied.Result);
        Assert.Empty(f.Outputs());
        Assert.False(File.Exists(Path.Combine(f.Repo.ModelRoot, ".cache", "journal.jsonl")));
    }

    [Fact]
    public async Task Apply_is_stale_when_a_unit_appeared()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        await f.WriteModelAsync(b => Models.Shop(b, extra: (m, _, _) => m.Entity("Order").Key("id", "uuid")));

        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Contains(applied.StaleUnits, k => k.StartsWith("basic/entity:", StringComparison.Ordinal) && plan.Units.All(u => u.Key != k));
    }

    [Fact]
    public async Task Apply_is_stale_when_a_planned_path_was_edited_by_hand()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b, customerName: "text"));
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        Assert.Contains(plan.Units, u => u.Skipped);
        f.Repo.WriteFile("out/entities/Product.txt", "edited after the plan\n");

        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Equal(["out/entities/Product.txt"], applied.StalePaths);
        Assert.Equal("edited after the plan\n", f.Repo.ReadFile("out/entities/Product.txt"));
    }

    [Fact]
    public async Task Apply_is_stale_after_a_region_edit()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "modes");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b, customerName: "text"));
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        var text = f.Repo.ReadFile("out/regions/Customer.txt").Replace("// default body", "// late edit", StringComparison.Ordinal);
        f.Repo.WriteFile("out/regions/Customer.txt", text);

        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Stale, applied.Outcome);
        Assert.Contains("out/regions/Customer.txt", applied.StalePaths);
        Assert.Contains("// late edit", f.Repo.ReadFile("out/regions/Customer.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_with_skipped_units_keeps_them_and_their_state()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b, customerName: "text"));
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        var skipped = Assert.Single(plan.Units, u => u.Skipped);
        Assert.NotEmpty(skipped.Outputs);

        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.Equal(1, applied.Result!.UnitsSkipped);
        Assert.True(f.Repo.Exists("out/entities/Product.txt"));

        f.Renderer = new FakeRenderer();
        var next = await f.RunAsync();
        Assert.Equal(3, next.UnitsSkipped);
    }

    [Fact]
    public async Task Apply_deletes_planned_orphans_only()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        File.Delete(Path.Combine(f.Repo.ModelRoot, "model", "entities", "product.json"));
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        Assert.Contains(plan.Changes, c => c.Path == "out/entities/Product.txt" && c.Kind == FileChangeKind.Deleted);

        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.False(f.Repo.Exists("out/entities/Product.txt"));
        Assert.Equal(1, applied.Result!.FilesDeleted);
    }

    [Fact]
    public async Task Apply_of_an_unknown_plan_fails()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var applied = await f.Service.ApplyAsync("01ARZ3NDEKTSV4RRFFQ69G5FAV", null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Failed, applied.Outcome);
        Assert.Equal(RunOutcome.Failed, (await f.Service.ApplyAsync("../../etc", null, GenerationFixture.Ct)).Outcome);
    }

    [Fact]
    public async Task Apply_of_an_invalid_plan_is_refused()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.WritePackFile("basic", "index.tpl", "{{fail}}\n");
        var planned = await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Invalid, planned.Outcome);
        var applied = await f.Service.ApplyAsync(planned.Plan!.Id, null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Invalid, applied.Outcome);
        Assert.Empty(f.Outputs());
    }

    [Fact]
    public async Task Only_the_newest_plans_are_kept()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var store = f.Services.Plans;
        var ids = Enumerable.Range(0, Engine.Generation.PlanStore.Keep + 3).Select(_ => f.Repo.Options.EffectiveIdGenerator.NewId()).ToList();
        foreach (var id in ids)
            Directory.CreateDirectory(Path.Combine(store.Folder, id));
        store.Prune(ids[0]);
        var left = Directory.EnumerateDirectories(store.Folder).Select(Path.GetFileName).ToHashSet();
        Assert.Equal(Engine.Generation.PlanStore.Keep + 1, left.Count);
        Assert.Contains(ids[0], left);
        Assert.DoesNotContain(ids[1], left);
        Assert.Contains(ids[^1], left);
    }

    [Fact]
    public async Task Preview_renders_one_unit_without_writing()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var customer = (await f.Store.GetSnapshotAsync(GenerationFixture.Ct)).All<Engine.Model.Entity>().Single(e => e.Name == "Customer");
        var preview = await f.Service.PreviewAsync("basic", "entity", customer.Id, GenerationFixture.Ct);
        var file = Assert.Single(preview.Files);
        Assert.Equal("out/entities/Customer.txt", file.Path);
        Assert.StartsWith("entity Customer", file.Text, StringComparison.Ordinal);
        Assert.Empty(f.Outputs());

        var missing = await f.Service.PreviewAsync("basic", "nope", null, GenerationFixture.Ct);
        Assert.Empty(missing.Files);
        Assert.Contains(missing.Diagnostics, d => d.Rule == "MQ6001");
    }

    [Fact]
    public async Task Preview_outside_the_units_scope_is_MQ6026_not_a_render_error()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var customer = (await f.Store.GetSnapshotAsync(GenerationFixture.Ct)).All<Engine.Model.Entity>().Single(e => e.Name == "Customer");

        var none = await f.Service.PreviewAsync("basic", "entity", null, GenerationFixture.Ct);
        Assert.Empty(none.Files);
        var expects = Assert.Single(none.Diagnostics);
        Assert.Equal("MQ6026", expects.Rule);
        Assert.StartsWith("This template expects an entity", expects.Message, StringComparison.Ordinal);
        Assert.Equal("/units/0/for", expects.JsonPointer);

        var model = await f.Service.PreviewAsync("basic", "index", customer.Id, GenerationFixture.Ct);
        Assert.Equal("MQ6026", Assert.Single(model.Diagnostics).Rule);
        Assert.Contains("without an element", model.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.NotEmpty((await f.Service.PreviewAsync("basic", "index", null, GenerationFixture.Ct)).Files);
    }

    [Fact]
    public async Task A_forced_plan_lists_every_file_identical_ones_unchanged_and_owned_ones_kept()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic", "modes");
        await f.RunAsync();
        var before = f.Outputs();
        var times = WriteTimes(f);

        var plan = (await f.Service.PlanAsync(new GenerationRequest { Force = true }, null, GenerationFixture.Ct)).Plan!;

        // Every file the units produce is listed once, with what Apply does to it: nothing.
        var produced = plan.Units.SelectMany(u => u.Outputs).Select(o => o.Path).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(produced, plan.Changes.Select(c => c.Path).ToList());
        Assert.Equal(plan.Changes.OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => c.Path), plan.Changes.Select(c => c.Path));
        Assert.All(plan.Changes, c => Assert.Contains(c.Kind, new[] { FileChangeKind.Unchanged, FileChangeKind.Kept }));
        Assert.DoesNotContain(plan.Changes, c => c.Kind == FileChangeKind.NotRendered);
        Assert.Equal(FileChangeKind.Kept, plan.Changes.Single(c => c.Path == "out/scaffold/Customer.txt").Kind); // once
        Assert.Equal(FileChangeKind.Kept, plan.Changes.Single(c => c.Path == "gen/pair/Customer.txt").Kind); // companion
        var unchanged = plan.Changes.Single(c => c.Path == "out/entities/Customer.txt");
        Assert.Equal(FileChangeKind.Unchanged, unchanged.Kind);
        Assert.Equal("basic/entity:" + plan.Units.Single(u => u.Key == unchanged.UnitKey).ElementId, unchanged.UnitKey);
        Assert.NotNull(unchanged.NewHash);
        Assert.All(plan.Changes, c => Assert.Null(c.Diff));

        // Counts by kind (every kind, zero included) and units by reason.
        Assert.Equal(PlanEntriesKinds, plan.Counts.Keys.ToList());
        Assert.Equal(plan.Changes.Count(c => c.Kind == FileChangeKind.Unchanged), plan.Counts["unchanged"]);
        Assert.Equal(plan.Changes.Count(c => c.Kind == FileChangeKind.Kept), plan.Counts["kept"]);
        Assert.Equal(0, plan.Counts["not-rendered"]);
        Assert.Equal(0, plan.Counts["added"] + plan.Counts["modified"] + plan.Counts["deleted"]);
        Assert.Equal(new Dictionary<string, int> { ["forced"] = plan.Units.Count }, plan.UnitsRendered);
        Assert.Empty(plan.UnitsSkipped);

        // No diff for a file Apply leaves alone, a kept one included.
        Assert.Equal("", await f.Service.GetPlanDiffAsync(plan.Id, "out/scaffold/Customer.txt", GenerationFixture.Ct));
        Assert.Equal("", await f.Service.GetPlanDiffAsync(plan.Id, "out/entities/Customer.txt", GenerationFixture.Ct));

        // The stored plan keeps the entries and the counts.
        var stored = (await f.Service.GetPlanAsync(plan.Id, GenerationFixture.Ct))!;
        Assert.Equal(JsonSerializer.Serialize(plan, Web), JsonSerializer.Serialize(stored, Web));

        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.Equal(0, applied.Result!.FilesWritten);
        Assert.Equal(0, applied.Result.FilesDeleted);
        Assert.Equal(before, f.Outputs());
        Assert.Equal(times, WriteTimes(f));
    }

    [Fact]
    public async Task An_incremental_plan_lists_the_files_of_skipped_units_as_not_rendered()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic", "modes");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b, customerName: "text"));
        var times = WriteTimes(f);

        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;

        var skipped = plan.Units.Where(u => u.Skipped).ToList();
        Assert.NotEmpty(skipped);
        var produced = plan.Units.SelectMany(u => u.Outputs).Select(o => o.Path).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(produced, plan.Changes.Select(c => c.Path).ToList());
        foreach (var output in skipped.SelectMany(u => u.Outputs.Select(o => (Unit: u, Output: o))))
        {
            var entry = plan.Changes.Single(c => c.Path == output.Output.Path);
            Assert.Equal(output.Unit.Key, entry.UnitKey);
            Assert.Equal(output.Output.ManifestHash, entry.NewHash);
            Assert.Equal(output.Output.ManifestHash.StartsWith("o:", StringComparison.Ordinal) ? FileChangeKind.Kept : FileChangeKind.NotRendered, entry.Kind);
        }

        Assert.Equal(FileChangeKind.NotRendered, plan.Changes.Single(c => c.Path == "out/entities/Product.txt").Kind);
        Assert.Equal(FileChangeKind.Kept, plan.Changes.Single(c => c.Path == "gen/pair/Product.txt").Kind);
        // Customer's units render again; a file whose bytes come out the same is listed as unchanged.
        Assert.Contains(plan.Changes.Single(c => c.Path == "out/entities/Customer.txt").Kind, new[] { FileChangeKind.Unchanged, FileChangeKind.Modified });
        Assert.Equal("", await f.Service.GetPlanDiffAsync(plan.Id, "out/entities/Product.txt", GenerationFixture.Ct));
        Assert.Equal(plan.Changes.Count(c => c.Kind == FileChangeKind.NotRendered), plan.Counts["not-rendered"]);
        Assert.Equal(new Dictionary<string, int> { ["unchanged"] = skipped.Count }, plan.UnitsSkipped);
        Assert.Equal(plan.Units.Count - skipped.Count, plan.UnitsRendered.Values.Sum());

        // Apply writes the modified files only: nothing listed as not rendered, unchanged or kept is touched.
        var applied = await f.Service.ApplyAsync(plan.Id, null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.Equal(plan.Counts["modified"] + plan.Counts["added"], applied.Result!.FilesWritten);
        Assert.Equal(0, applied.Result.FilesDeleted);
        var after = WriteTimes(f);
        foreach (var c in plan.Changes.Where(c => c.Kind is FileChangeKind.NotRendered or FileChangeKind.Unchanged or FileChangeKind.Kept))
            Assert.Equal(times[c.Path], after[c.Path]);
    }

    [Fact]
    public async Task A_plan_after_an_apply_has_nothing_to_write_and_lists_every_file()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic", "modes");
        var first = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;
        Assert.Equal(RunOutcome.Succeeded, (await f.Service.ApplyAsync(first.Id, null, GenerationFixture.Ct)).Outcome);

        var again = (await f.Service.PlanAsync(new GenerationRequest(), null, GenerationFixture.Ct)).Plan!;

        Assert.Equal(first.Changes.Select(c => c.Path), again.Changes.Select(c => c.Path));
        Assert.All(again.Changes, c => Assert.Contains(c.Kind, new[] { FileChangeKind.NotRendered, FileChangeKind.Kept }));
        Assert.Equal(again.Changes.Count, again.Counts["not-rendered"] + again.Counts["kept"]);
        Assert.Empty(again.UnitsRendered);
    }

    private static readonly List<string> PlanEntriesKinds =
        ["added", "conflict", "deleted", "hand-edited", "kept", "modified", "not-rendered", "orphaned-owned", "unchanged"];

    private static Dictionary<string, DateTime> WriteTimes(GenerationFixture f) =>
        f.Outputs().Keys.ToDictionary(p => p, p => File.GetLastWriteTimeUtc(f.Repo.PathOf(p)), StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
