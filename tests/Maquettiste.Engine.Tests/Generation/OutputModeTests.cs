using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>Output modes end to end through the post-processor and the writer (engine-design.md section 8).</summary>
public sealed class OutputModeTests
{
    [Fact]
    public async Task Modes_write_owned_pair_region_and_block_files()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "modes");
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        var outputs = f.Outputs();
        Assert.Equal("scaffold for Customer\n", outputs["out/scaffold/Customer.txt"]);
        Assert.Equal("generated half of Customer\n", outputs["gen/pair/Customer.g.txt"]);
        Assert.Equal("hand-written half of Customer\n", outputs["gen/pair/Customer.txt"]);
        Assert.Contains("maquettiste:keep id=body", outputs["out/regions/Customer.txt"], StringComparison.Ordinal);
        Assert.Equal("block a\n", outputs["out/blocks/a.txt"]);
        Assert.Equal("block b Customer Product\n", outputs["out/blocks/b.txt"]);
    }

    [Fact]
    public async Task Owned_files_are_kept_and_region_bodies_survive_regeneration()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "modes");
        await f.RunAsync();
        f.Repo.WriteFile("out/scaffold/Customer.txt", "the team's version\n");
        f.Repo.WriteFile("gen/pair/Customer.txt", "the team's half\n");
        var regions = f.Repo.ReadFile("out/regions/Customer.txt").Replace("// default body", "// kept by hand", StringComparison.Ordinal);
        f.Repo.WriteFile("out/regions/Customer.txt", regions);

        var result = await f.RunAsync(force: true);

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal("the team's version\n", f.Repo.ReadFile("out/scaffold/Customer.txt"));
        Assert.Equal("the team's half\n", f.Repo.ReadFile("gen/pair/Customer.txt"));
        Assert.Contains("// kept by hand", f.Repo.ReadFile("out/regions/Customer.txt"), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Changes, c => c.Kind is FileChangeKind.Conflict or FileChangeKind.HandEdited);
    }

    [Fact]
    public async Task Owned_orphans_stay_on_disk()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "modes");
        await f.RunAsync();
        File.Delete(Path.Combine(f.Repo.ModelRoot, "model", "entities", "product.json"));
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Contains(result.Changes, c => c.Path == "out/scaffold/Product.txt" && c.Kind == FileChangeKind.OrphanedOwned);
        Assert.True(f.Repo.Exists("out/scaffold/Product.txt"));
        Assert.False(f.Repo.Exists("out/regions/Product.txt"));
        Assert.False(f.Repo.Exists("gen/pair/Product.g.txt"));
        Assert.True(f.Repo.Exists("gen/pair/Product.txt"));
    }

    [Fact]
    public async Task Incremental_skip_keeps_owned_outputs_that_differ()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "modes");
        await f.RunAsync();
        f.Repo.WriteFile("out/scaffold/Customer.txt", "the team's version\n");
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Renderer.Rendered);
        Assert.Equal("the team's version\n", f.Repo.ReadFile("out/scaffold/Customer.txt"));
    }
}
