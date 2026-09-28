using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>Schema snapshots around a run (engine-design.md section 14): saved after a successful apply, drift in check mode.</summary>
public sealed class SchemaSnapshotTests
{
    [Fact]
    public async Task Apply_saves_the_snapshot_and_check_reports_a_stale_one()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var packFile = f.Repo.PathOf(".maquettiste/templates/basic/pack.json");
        f.WritePackFile("basic", "pack.json", File.ReadAllText(packFile).Replace("\"units\"", "\"usesSchemaDiff\": true,\n  \"units\"", StringComparison.Ordinal));

        var dry = await f.RunAsync(GenerationMode.DryRun);
        Assert.Equal(RunOutcome.Succeeded, dry.Outcome);
        Assert.False(f.Repo.Exists(".maquettiste/snapshots/main.json"));

        var applied = await f.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.True(f.Repo.Exists(".maquettiste/snapshots/main.json"));
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync(GenerationMode.Check)).Outcome);

        await f.WriteModelAsync(b => Models.Shop(b, extra: (m, _, _) => m.Entity("Order").Key("id", "uuid")));
        var snapshot = f.Repo.ReadFile(".maquettiste/snapshots/main.json");
        var check = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Drift, check.Outcome);
        Assert.Contains(check.Diagnostics, d => d.Rule == "MQ6018" && d.FilePath == ".maquettiste/snapshots/main.json");
        Assert.Equal(snapshot, f.Repo.ReadFile(".maquettiste/snapshots/main.json"));
    }

    [Fact]
    public async Task A_run_over_part_of_the_roots_does_not_move_the_snapshot()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var packFile = f.Repo.PathOf(".maquettiste/templates/basic/pack.json");
        f.WritePackFile("basic", "pack.json", File.ReadAllText(packFile).Replace("\"units\"", "\"usesSchemaDiff\": true,\n  \"units\"", StringComparison.Ordinal));
        var result = await f.Service.RunAsync(new GenerationRequest { Roots = RootSelection.Built }, null, GenerationFixture.Ct);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.False(f.Repo.Exists(".maquettiste/snapshots/main.json"));
    }
}
