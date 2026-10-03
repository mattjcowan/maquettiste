using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>Full and repeated runs over the billing fixture with real packs (integration tasks 1 and 2).</summary>
[Collection(IntegrationCollection.Name)]
public sealed class FullRunTests
{
    [Fact]
    public async Task A_full_run_writes_files_manifests_and_snapshots_and_reports_stage_timings()
    {
        await using var repo = E2ERepo.Create(migrations: true);
        var progress = new ProgressLog();

        var result = await repo.RunAsync(progress: progress);

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        Assert.Empty(result.Diagnostics);
        var outputs = repo.Outputs();
        Assert.Equal(outputs.Keys, result.Changes.Select(c => c.Path));
        Assert.All(result.Changes, c => Assert.Equal(FileChangeKind.Added, c.Kind));
        Assert.Equal(outputs.Count, result.FilesWritten);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.UnitsSkipped);
        Assert.Equal(86, result.UnitsRendered); // e2e 52, billing-demo 33, migrations 1

        // Every mode and every pack produced its files.
        foreach (var path in new[]
        {
            "db/e2e/entities/customer.txt", "db/e2e/tables/invoices.sql", "db/e2e/tables/payment_invoice.sql", "db/e2e/index.txt",
            "db/e2e/types/Product.cs", "db/e2e/audited/customer.txt", "db/e2e/scaffold/customer.txt", "db/e2e/pair/Customer.g.cs",
            "db/e2e/pair/Customer.cs", "db/e2e/regions/customer.txt", "db/e2e/blocks/billing.txt", "db/e2e/blocks/catalog.txt",
            "db/migrations/1.sql", "src/Generated/demo/docs/helpers.txt", "src/Generated/demo/src/Billing/Invoice.g.cs",
            "src/Generated/demo/web/components/InvoiceCard.jsx",
        })
        {
            Assert.True(outputs.ContainsKey(path), "missing " + path);
        }

        Assert.DoesNotContain("db/e2e/audited/product.txt", outputs.Keys); // the JavaScript selector picks audited entities only
        Assert.Contains("hello from CUSTOMER!", outputs["db/e2e/entities/customer.txt"], StringComparison.Ordinal); // pack helper
        Assert.Contains("Guid Id,", outputs["db/e2e/types/Product.cs"], StringComparison.Ordinal); // pack type map
        Assert.StartsWith("-- migration 0 -> 1 of main", outputs["db/migrations/1.sql"], StringComparison.Ordinal);

        // Every pack has one manifest under .maquettiste/manifest, whatever its roots; each entry is the hash of the bytes on disk
        // (owned files o:, regions r: with emptied bodies). Nothing goes to the cache folder.
        var e2e = ManifestEntries(repo.Manifest("e2e"));
        var demo = ManifestEntries(repo.Manifest("billing-demo"));
        var migrations = ManifestEntries(repo.Manifest("migrations"));
        Assert.Equal(outputs.Keys.Where(p => p.StartsWith("db/e2e/", StringComparison.Ordinal)), e2e.Keys);
        Assert.Equal(outputs.Keys.Where(p => p.StartsWith("src/Generated/demo/", StringComparison.Ordinal)), demo.Keys);
        Assert.Equal(["db/migrations/1.sql"], migrations.Keys);
        Assert.False(repo.Repo.Exists(".maquettiste/.cache/manifest/billing-demo.json"));
        foreach (var (path, hash) in e2e.Concat(demo).Concat(migrations))
        {
            var sha = E2ERepo.Sha(File.ReadAllBytes(repo.Repo.PathOf(path)));
            if (IsOwned(path))
                Assert.Equal("o:" + sha, hash);
            else if (path.Contains("/regions/", StringComparison.Ordinal))
                Assert.StartsWith("r:", hash, StringComparison.Ordinal);
            else
                Assert.Equal(sha, hash);
        }

        // The schema snapshot of the database the migrations pack diffs.
        using (var snapshot = JsonDocument.Parse(repo.Repo.ReadFile(".maquettiste/snapshots/main.json")))
        {
            Assert.Equal(1, snapshot.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal(E2ERepo.MainDatabaseId, snapshot.RootElement.GetProperty("database").GetString());
            Assert.True(snapshot.RootElement.GetProperty("tables").GetArrayLength() >= 6);
        }

        // No run journal is left behind, and the unit state is in the cache folder, outside the repo.
        Assert.False(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"));
        Assert.True(File.Exists(Path.Combine(repo.Options.CacheDirectory, "units", "e2e.v4.bin")));
        Assert.True(File.Exists(Path.Combine(repo.Options.CacheDirectory, "units", "billing-demo.v4.bin")));

        // Per-stage timings and progress: every stage reports, and the render and write stages count units and files.
        var stages = Enum.GetValues<PipelineStage>();
        Assert.Equal(stages, result.Timings.Select(t => t.Stage));
        Assert.All(result.Timings, t => Assert.True(t.Wall >= TimeSpan.Zero && t.Busy >= TimeSpan.Zero));
        Assert.Equal(result.UnitsRendered, result.Timings.Single(t => t.Stage == PipelineStage.Render).Items);
        Assert.True(result.Timings.Single(t => t.Stage == PipelineStage.Write).Items >= outputs.Count);
        var reported = progress.Updates.Select(u => u.Stage).ToHashSet();
        Assert.Superset(new HashSet<PipelineStage>([PipelineStage.Load, PipelineStage.Plan, PipelineStage.Render, PipelineStage.Write]), reported);
        Assert.Contains(progress.Updates, u => u.Stage == PipelineStage.Write && u.CurrentPath == "db/e2e/index.txt");
    }

    [Fact]
    public async Task A_second_identical_run_writes_nothing_and_touches_no_mtime()
    {
        await using var repo = E2ERepo.Create();
        var first = await repo.ApplyAsync();
        repo.AgeFiles();
        var before = repo.Tree();
        var times = repo.WriteTimes();

        var second = await repo.ApplyAsync();

        Assert.Empty(second.Changes);
        Assert.Equal(0, second.FilesWritten);
        Assert.Equal(0, second.FilesDeleted);
        Assert.Equal(0, second.UnitsRendered);
        Assert.Equal(first.UnitsRendered, second.UnitsSkipped);
        AssertSameBytes(before, repo.Tree());
        Assert.Equal(times, repo.WriteTimes());

        // A forced run renders every unit but still writes nothing: every file is byte-identical.
        var forced = await repo.ApplyAsync(force: true);
        Assert.Equal(first.UnitsRendered, forced.UnitsRendered);
        Assert.All(forced.Changes, c => Assert.Equal(FileChangeKind.Kept, c.Kind)); // once files and companions: rendered, kept
        Assert.Equal(24, forced.Changes.Count); // 8 scaffold files, 8 e2e companions, 8 billing-demo companions
        Assert.Equal(0, forced.FilesWritten);
        Assert.Equal(times, repo.WriteTimes());

        // A new process (store, service and caches rebuilt from disk) agrees.
        await repo.RestartAsync();
        var restarted = await repo.ApplyAsync();
        Assert.Equal(0, restarted.UnitsRendered);
        Assert.Empty(restarted.Changes);
        Assert.Equal(times, repo.WriteTimes());

        // And check mode finds no drift.
        var check = await repo.RunAsync(GenerationMode.Check);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, check);
        Assert.All(check.Changes, c => Assert.Equal(FileChangeKind.Kept, c.Kind)); // once files and companions, under every root
        Assert.Equal(24, check.Changes.Count);
        Assert.Equal(times, repo.WriteTimes());
    }

    [Fact]
    public async Task Migrations_are_written_once_per_revision_and_the_snapshot_advances_only_on_apply()
    {
        await using var repo = E2ERepo.Create(demo: false, migrations: true);
        await repo.ApplyAsync();
        Assert.Equal(1, Revision(repo));

        // Nothing changed, yet the repository is not stable after the apply that wrote migration 1: the migration unit renders
        // nothing against the advanced (empty) diff, so migration 1 is an owned orphan. Check reports it as drift (design section 16,
        // SPEC section 17: any orphaned file), since the next apply drops its `o:` line from the committed manifest (D29). Known gap,
        // raised as a design request (keep an owned orphan's entry while its file exists); this test records today's behaviour.
        var manifestAfterWrite = repo.Manifest("migrations");
        Assert.StartsWith("o:", ManifestEntries(manifestAfterWrite)["db/migrations/1.sql"], StringComparison.Ordinal);
        var check = await repo.RunAsync(GenerationMode.Check);
        E2ERepo.AssertOutcome(RunOutcome.Drift, check);
        Assert.Equal(("db/migrations/1.sql", FileChangeKind.OrphanedOwned), check.Changes
            .Where(c => c.Kind is not (FileChangeKind.Unchanged or FileChangeKind.Kept)).Select(c => (c.Path, c.Kind)).Single());
        Assert.Equal(manifestAfterWrite, repo.Manifest("migrations")); // check writes nothing
        var again = await repo.ApplyAsync();
        Assert.Equal(("db/migrations/1.sql", FileChangeKind.OrphanedOwned), again.Changes.Select(c => (c.Path, c.Kind)).Single());
        Assert.Equal(0, again.FilesWritten + again.FilesDeleted);
        Assert.True(repo.Repo.Exists("db/migrations/1.sql"));
        Assert.False(repo.Repo.Exists(".maquettiste/manifest/migrations.json")); // the only entry is gone, so the manifest is deleted

        // From here on the repository is stable: check is clean and a further apply leaves the committed tree byte-identical.
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
        var stable = repo.Tree();
        Assert.Empty((await repo.ApplyAsync()).Changes);
        AssertSameBytes(stable, repo.Tree());

        // A physical change: check reports the pending diff as drift (MQ6018) and saves nothing; apply writes migration 2.
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var drift = await repo.RunAsync(GenerationMode.Check);
        E2ERepo.AssertOutcome(RunOutcome.Drift, drift);
        Assert.Contains(drift.Diagnostics, d => d.Rule == "MQ6018");
        Assert.Equal(1, Revision(repo));
        var dry = await repo.RunAsync(GenerationMode.DryRun);
        Assert.Contains(dry.Changes, c => c.Path == "db/migrations/2.sql" && c.Kind == FileChangeKind.Added);
        Assert.False(repo.Repo.Exists("db/migrations/2.sql"));
        Assert.Equal(1, Revision(repo));

        var applied = await repo.ApplyAsync();
        Assert.Contains(applied.Changes, c => c.Path == "db/migrations/2.sql" && c.Kind == FileChangeKind.Added);
        Assert.Equal(2, Revision(repo));
        Assert.StartsWith("-- migration 1 -> 2 of main\n-- altered", repo.Repo.ReadFile("db/migrations/2.sql"), StringComparison.Ordinal);
        Assert.True(repo.Repo.Exists("db/migrations/1.sql"));
        // Same gap for migration 2: drift until a second apply drops its entry.
        E2ERepo.AssertOutcome(RunOutcome.Drift, await repo.RunAsync(GenerationMode.Check));
        await repo.ApplyAsync();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
    }

    /// <summary>Owned outputs: once files, pair companions and migrations.</summary>
    internal static bool IsOwned(string path) =>
        path.Contains("/scaffold/", StringComparison.Ordinal) || path.StartsWith("db/migrations/", StringComparison.Ordinal)
        || (path.EndsWith(".cs", StringComparison.Ordinal) && !path.EndsWith(".g.cs", StringComparison.Ordinal)
            && (path.StartsWith("db/e2e/pair/", StringComparison.Ordinal) || path.StartsWith("src/Generated/demo/src/", StringComparison.Ordinal)));

    private static int Revision(E2ERepo repo)
    {
        using var snapshot = JsonDocument.Parse(repo.Repo.ReadFile(".maquettiste/snapshots/main.json"));
        return snapshot.RootElement.GetProperty("revision").GetInt32();
    }

    internal static SortedDictionary<string, string> ManifestEntries(string manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
            entries[file[0].GetString()!] = file[1].GetString()!;
        return entries;
    }

    internal static void AssertSameBytes(SortedDictionary<string, byte[]> expected, SortedDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (path, bytes) in expected)
            Assert.True(bytes.AsSpan().SequenceEqual(actual[path]), "bytes differ: " + path);
    }
}
