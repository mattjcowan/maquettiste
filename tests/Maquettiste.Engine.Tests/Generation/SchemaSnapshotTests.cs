using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

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
    public async Task The_snapshot_serialized_during_the_run_is_what_a_capture_after_it_saves()
    {
        // A run serializes the snapshot from the diff's own capture while it renders (WA); a store the run does not know takes the
        // plain path, a second capture saved after the apply. Both must write the same bytes, run after run.
        await using var early = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await using var plain = await GenerationFixture.CreateAsync(b => Models.Shop(b), s => s with { Snapshots = new PassThrough(s.Snapshots) }, "basic");
        foreach (var f in new[] { early, plain })
        {
            var packFile = f.Repo.PathOf(".maquettiste/templates/basic/pack.json");
            f.WritePackFile("basic", "pack.json", File.ReadAllText(packFile).Replace("\"units\"", "\"usesSchemaDiff\": true,\n  \"units\"", StringComparison.Ordinal));
        }

        Action<ModelBuilder>[] models =
        [
            b => Models.Shop(b),
            b => Models.Shop(b, extra: (m, _, _) => m.Entity("Order").Key("id", "uuid")),
            b => Models.Shop(b, extra: (m, _, _) => m.Entity("Order").Key("id", "uuid").Attr("note", "string")),
        ];
        var revision = 0;
        foreach (var model in models)
        {
            foreach (var f in new[] { early, plain })
            {
                await f.WriteModelAsync(model);
                Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
                Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync(GenerationMode.Check)).Outcome);
            }

            var bytes = early.Repo.ReadFile(".maquettiste/snapshots/main.json");
            Assert.Equal(plain.Repo.ReadFile(".maquettiste/snapshots/main.json"), bytes);
            Assert.Contains("\"revision\": " + (++revision).ToString(System.Globalization.CultureInfo.InvariantCulture), bytes, StringComparison.Ordinal);
        }

        // An unchanged model leaves the snapshot alone (the store answers the load from what it parsed when it wrote the file).
        var before = early.Repo.ReadFile(".maquettiste/snapshots/main.json");
        Assert.Equal(RunOutcome.Succeeded, (await early.RunAsync()).Outcome);
        Assert.Equal(before, early.Repo.ReadFile(".maquettiste/snapshots/main.json"));
    }

    [Fact]
    public async Task The_store_holds_the_written_snapshot_only_while_runs_diff_it()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        var store = Assert.IsType<Maquettiste.Engine.SchemaDiff.SnapshotStore>(f.Services.Snapshots);
        var packFile = f.Repo.PathOf(".maquettiste/templates/basic/pack.json");
        var plainPack = File.ReadAllText(packFile);
        f.WritePackFile("basic", "pack.json", plainPack.Replace("\"units\"", "\"usesSchemaDiff\": true,\n  \"units\"", StringComparison.Ordinal));

        // The apply writes the snapshot and parses it back for the next run, which reuses that object.
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
        var held = store.Held("main");
        Assert.NotNull(held);
        var parsed = await held;
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
        Assert.Same(parsed, await store.Held("main")!);

        // A run whose packs no longer diff drops it.
        f.WritePackFile("basic", "pack.json", plainPack);
        Assert.Equal(RunOutcome.Succeeded, (await f.RunAsync()).Outcome);
        Assert.Null(store.Held("main"));
    }

    /// <summary>A snapshot store that is not the engine's own, so a run takes the plain save path.</summary>
    private sealed class PassThrough(ISnapshotStore inner) : ISnapshotStore
    {
        public Task<PhysicalSnapshot?> LoadAsync(string databaseName, CancellationToken ct) => inner.LoadAsync(databaseName, ct);

        public Task SaveAsync(PhysicalSnapshot snapshot, CancellationToken ct) => inner.SaveAsync(snapshot, ct);
    }
}
