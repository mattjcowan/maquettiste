using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary><c>--check</c> (in memory, every root, every unit rendered) and hand-edit policies through to the writer.</summary>
public sealed class CheckAndHandEditTests
{
    [Fact]
    public async Task Check_after_apply_finds_no_drift_and_renders_every_unit()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.Renderer = new FakeRenderer();
        var check = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Succeeded, check.Outcome);
        Assert.Equal(3, check.UnitsRendered);
        Assert.Equal(0, check.UnitsSkipped);
        Assert.Empty(check.Changes);
    }

    [Fact]
    public async Task Check_reports_stale_output_as_drift_and_writes_nothing()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        var before = f.Outputs();
        var manifest = f.CommittedManifest("basic");
        await f.WriteModelAsync(b => Models.Shop(b, extra: (m, customer, main) => m.Mapping(main, customer).Ignore()));
        var check = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Drift, check.Outcome);
        Assert.Contains(check.Changes, c => c.Path == "out/entities/Customer.txt" && c.Kind == FileChangeKind.Modified);
        Assert.Equal(before, f.Outputs());
        Assert.Equal(manifest, f.CommittedManifest("basic"));
    }

    [Fact]
    public async Task Check_reports_a_missing_file_as_drift()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        File.Delete(f.Repo.PathOf("out/index.txt"));
        var check = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Drift, check.Outcome);
        Assert.Contains(check.Changes, c => c.Path == "out/index.txt" && c.Kind == FileChangeKind.Added);
        Assert.False(f.Repo.Exists("out/index.txt"));
    }

    [Fact]
    public async Task Check_reports_an_orphan_as_drift()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        File.Delete(Path.Combine(f.Repo.ModelRoot, "model", "entities", "product.json"));
        var check = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Drift, check.Outcome);
        Assert.Contains(check.Changes, c => c.Path == "out/entities/Product.txt" && c.Kind == FileChangeKind.Deleted);
        Assert.True(f.Repo.Exists("out/entities/Product.txt"));
    }

    [Fact]
    public async Task Check_reports_a_hand_edit_as_a_conflict_even_with_the_overwrite_policy()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.Repo.WriteFile("out/index.txt", "edited by hand\n");
        var check = await f.RunAsync(GenerationMode.Check, handEdits: HandEditPolicy.Overwrite);
        Assert.Equal(RunOutcome.Conflicts, check.Outcome);
        Assert.Contains(check.Changes, c => c.Path == "out/index.txt" && c.Kind is FileChangeKind.Conflict or FileChangeKind.HandEdited);
        Assert.Equal("edited by hand\n", f.Repo.ReadFile("out/index.txt"));
    }

    [Fact]
    public async Task Check_reports_a_missing_manifest_as_drift()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        File.Delete(f.Repo.PathOf(".maquettiste/manifest/basic.json"));
        var check = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Drift, check.Outcome);
        Assert.Contains(check.Changes, c => c.Path == "out/index.txt" && c.Kind == FileChangeKind.Modified && c.OldHash is null);
    }

    [Fact]
    public async Task Check_covers_every_root()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.WritePackFile("basic", "pack.json", File.ReadAllText(f.Repo.PathOf(".maquettiste/templates/basic/pack.json")).Replace("\"out/", "\"gen/", StringComparison.Ordinal));
        await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync(GenerationMode.Check)).Outcome);
        File.Delete(f.Repo.PathOf(f.Repo.ListFiles().First(p => p.StartsWith("gen/", StringComparison.Ordinal))));
        var check = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Drift, check.Outcome);
        Assert.Equal(FileChangeKind.Added, Assert.Single(check.Changes).Kind);
    }

    [Fact]
    public async Task Hand_edit_with_fail_policy_is_a_conflict_and_the_file_is_kept()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.Repo.WriteFile("out/index.txt", "mine\n");
        await f.WriteModelAsync(b => Models.Shop(b, customerName: "text"));
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Conflicts, result.Outcome);
        Assert.Contains(result.Changes, c => c.Path == "out/index.txt" && c.Kind == FileChangeKind.Conflict);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6009");
        Assert.Equal("mine\n", f.Repo.ReadFile("out/index.txt"));

        // The unit keeps its previous state, so the conflict is found again next time.
        var again = await f.RunAsync();
        Assert.Equal(RunOutcome.Conflicts, again.Outcome);
    }

    [Fact]
    public async Task Hand_edit_with_overwrite_policy_is_overwritten()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.Repo.WriteFile("out/index.txt", "mine\n");
        var result = await f.RunAsync(handEdits: HandEditPolicy.Overwrite);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Contains(result.Changes, c => c.Path == "out/index.txt" && c.Kind == FileChangeKind.HandEdited);
        Assert.Equal("hello\nCustomer Product\n", f.Repo.ReadFile("out/index.txt"));
    }

    [Fact]
    public async Task Hand_edit_with_skip_policy_is_left_alone()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.Repo.WriteFile("out/index.txt", "mine\n");
        var result = await f.RunAsync(handEdits: HandEditPolicy.Skip);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Contains(result.Changes, c => c.Path == "out/index.txt" && c.Kind == FileChangeKind.HandEdited);
        Assert.Equal("mine\n", f.Repo.ReadFile("out/index.txt"));
    }

    [Fact]
    public async Task Pack_settings_hand_edit_policy_reaches_the_writer()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b), s => s with
        {
            Packs = new Dictionary<string, PackSettings> { ["basic"] = new() { HandEdits = HandEditPolicy.Overwrite } },
        });
        f.Repo.WriteFile("out/index.txt", "mine\n");
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal("hello\nCustomer Product\n", f.Repo.ReadFile("out/index.txt"));
    }

    [Fact]
    public async Task An_untracked_file_in_the_way_is_a_hand_edit()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        f.Repo.WriteFile("out/index.txt", "someone else's\n");
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Conflicts, result.Outcome);
        Assert.Equal("someone else's\n", f.Repo.ReadFile("out/index.txt"));
    }
}
