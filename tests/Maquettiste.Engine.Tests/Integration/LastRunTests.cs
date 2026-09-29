using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The last-run record of one-shot hosts (Generation/README.md, "Last-run record"): a fresh process whose inputs, engine files and
/// outputs are all as the last apply left them is answered from the record, with exactly the result a full run gives; any
/// difference falls back to the full run; a run that could leave work for the next one writes no record.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class LastRunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_process_with_nothing_changed_is_answered_from_the_record_with_the_full_runs_result()
    {
        await using var repo = E2ERepo.Create();
        var (cold, coldReplayed) = await OneShotAsync(repo);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, cold);
        Assert.False(coldReplayed);
        Assert.True(cold.UnitsRendered > 0);
        Assert.True(File.Exists(RecordPath(repo)), "the cold run records");

        var tree = repo.Tree();
        var times = repo.WriteTimes();
        var cacheTimes = CacheWriteTimes(repo);
        var (replayed, hit) = await OneShotAsync(repo);
        Assert.True(hit, "answered without loading the model");
        AssertSameTree(tree, repo.Tree());
        Assert.Equal(times, repo.WriteTimes());
        Assert.Equal(cacheTimes, CacheWriteTimes(repo));

        // A full run (a long-lived host, which neither reads nor writes the record) gives the same result and leaves the record valid.
        var full = await repo.RunAsync();
        Assert.Equal(0, full.UnitsRendered);
        AssertSameResult(full, replayed);
        Assert.Contains(replayed.Timings, t => t.Stage == PipelineStage.Skip && t.Items == full.UnitsSkipped);
        var (again, stillHit) = await OneShotAsync(repo);
        Assert.True(stillHit);
        AssertSameResult(full, again);
    }

    [Fact]
    public async Task Every_kind_of_change_falls_back_to_the_full_run_and_the_next_run_is_answered_again()
    {
        await using var repo = E2ERepo.Create();
        await OneShotAsync(repo);
        await AssertReplayedAsync(repo);

        // An element saved by an editor: the one-shot run renders its units, records, and the run after it is answered.
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var edited = await AssertFullAsync(repo);
        Assert.True(edited.UnitsRendered > 0);
        await AssertReplayedAsync(repo);

        // A model file whose time stamp alone changed.
        var customer = Directory.EnumerateFiles(Path.Combine(repo.Repo.ModelRoot, "model"), "customer.json", SearchOption.AllDirectories).Single();
        Touch(customer);
        Assert.Equal(0, (await AssertFullAsync(repo)).UnitsRendered);
        await AssertReplayedAsync(repo);

        // A new file in the model (an invalid one: the full run reports it) and its removal.
        repo.Repo.WriteFile(".maquettiste/model/enums/broken.json", "{");
        var broken = await OneShotAsync(repo);
        Assert.False(broken.Replayed);
        Assert.Equal(RunOutcome.Invalid, broken.Result.Outcome);
        File.Delete(repo.Repo.PathOf(".maquettiste/model/enums/broken.json"));
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);

        // A template edited.
        var template = Directory.EnumerateFiles(Path.Combine(repo.Repo.ModelRoot, "templates", "e2e"), "*.scriban", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
        File.AppendAllText(template, "\n");
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);

        // A generated file deleted: re-rendered and restored.
        var customerText = repo.Repo.ReadFile("db/e2e/entities/customer.txt");
        File.Delete(repo.Repo.PathOf("db/e2e/entities/customer.txt"));
        Assert.True((await AssertFullAsync(repo)).UnitsRendered > 0);
        Assert.Equal(customerText, repo.Repo.ReadFile("db/e2e/entities/customer.txt"));
        await AssertReplayedAsync(repo);

        // A generated file whose time stamp alone changed: the full run finds it intact by its bytes and records the stat it checked,
        // so the run after it is answered although the unit's state still holds the old stat.
        Touch(repo.Repo.PathOf("db/e2e/entities/customer.txt"));
        Assert.Equal(0, (await AssertFullAsync(repo)).UnitsRendered);
        await AssertReplayedAsync(repo);

        // A committed manifest touched (compared by content: answered), a built root's manifest touched (compared by stat), a unit
        // state file removed, the record damaged.
        Touch(repo.Repo.PathOf(".maquettiste/manifest/e2e.json"));
        await AssertReplayedAsync(repo);
        Touch(repo.Repo.PathOf(".maquettiste/.cache/manifest/billing-demo.json"));
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);
        File.Delete(Path.Combine(repo.Repo.CacheDirectory, "units", "e2e.v4.bin"));
        Assert.True((await AssertFullAsync(repo)).UnitsRendered > 0);
        await AssertReplayedAsync(repo);
        var record = await File.ReadAllBytesAsync(RecordPath(repo), Ct);
        record[record.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(RecordPath(repo), record, Ct);
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);

        // An unfinished journal: the full run resumes it.
        repo.Repo.WriteFile(".maquettiste/.cache/journal.jsonl", "{\"t\":\"begin\",\"run\":\"interrupted\",\"plan\":null,\"packs\":[\"e2e\"]}\n");
        await AssertFullAsync(repo);
        Assert.False(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"));
        await AssertReplayedAsync(repo);

        // Another request shape, and a forced run.
        var one = await OneShotAsync(repo, new GenerationRequest { Jobs = 2, Packs = ["e2e"] });
        Assert.False(one.Replayed);
        Assert.True((await OneShotAsync(repo, new GenerationRequest { Jobs = 2, Packs = ["e2e"] })).Replayed);
        await AssertFullAsync(repo);
        var forced = await OneShotAsync(repo, new GenerationRequest { Jobs = 2, Force = true });
        Assert.False(forced.Replayed);
        Assert.Equal(0, forced.Result.UnitsSkipped);
        await AssertReplayedAsync(repo);
    }

    [Fact]
    public async Task Sidecars_extensions_rule_scripts_and_partials_fall_back_to_the_full_run()
    {
        await using var repo = E2ERepo.Create();
        await OneShotAsync(repo);
        await AssertReplayedAsync(repo);
        var model = Path.Combine(repo.Repo.ModelRoot, "model");
        var customer = Directory.EnumerateFiles(model, "customer.json", SearchOption.AllDirectories).Single();
        var sidecar = Path.Combine(Path.GetDirectoryName(customer)!, "customer.md");

        // A description moved to a sidecar that does not exist yet: the missing sidecar is recorded and the run after is answered.
        await repo.EditAsync(E2ERepo.CustomerId, n => n["description"] = new System.Text.Json.Nodes.JsonObject { ["file"] = "customer.md" });
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);
        Assert.DoesNotContain("Pays on time", CustomerSource(repo));

        // The sidecar created, then edited: each renders the new description.
        await File.WriteAllTextAsync(sidecar, "Pays on time.\n", Ct);
        Assert.True((await AssertFullAsync(repo)).UnitsRendered > 0);
        Assert.Contains("Pays on time.", CustomerSource(repo));
        await AssertReplayedAsync(repo);
        await File.WriteAllTextAsync(sidecar, "Pays late, always.\n", Ct);
        Assert.True((await AssertFullAsync(repo)).UnitsRendered > 0);
        Assert.Contains("Pays late, always.", CustomerSource(repo));
        await AssertReplayedAsync(repo);

        // An extension changed.
        var extension = Path.Combine(repo.Repo.ModelRoot, "extensions", "retention.json");
        await File.WriteAllTextAsync(extension, (await File.ReadAllTextAsync(extension, Ct)).Replace("How long", "For how long", StringComparison.Ordinal), Ct);
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);

        // A rule script added (its diagnostic is replayed with the rest), then changed (the new message is reported).
        const string Rule = "maquettiste.rule({ id: \"customer-note\", severity: \"info\", kinds: [\"entity\"], check(element, model, report) { if (element.name === \"Customer\") report(\"MESSAGE\"); } });\n";
        repo.Repo.WriteFile(".maquettiste/extensions/rules/customer-note.js", Rule.Replace("MESSAGE", "first note", StringComparison.Ordinal));
        var added = await AssertFullAsync(repo);
        Assert.Contains(added.Diagnostics, d => d.Message.Contains("first note", StringComparison.Ordinal));
        await AssertReplayedAsync(repo);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/customer-note.js", Rule.Replace("MESSAGE", "second note", StringComparison.Ordinal));
        var changed = await AssertFullAsync(repo);
        Assert.Contains(changed.Diagnostics, d => d.Message.Contains("second note", StringComparison.Ordinal));
        Assert.DoesNotContain(changed.Diagnostics, d => d.Message.Contains("first note", StringComparison.Ordinal));
        await AssertReplayedAsync(repo);

        // A new partial under a pack's templates folder, although no template includes it.
        repo.Repo.WriteFile(".maquettiste/templates/e2e/_unused.scriban", "{{ 1 }}\n");
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);
    }

    [Fact]
    public async Task Templates_and_committed_manifests_are_compared_by_content_not_by_stat()
    {
        await using var repo = E2ERepo.Create();
        await OneShotAsync(repo);
        await AssertReplayedAsync(repo);

        // A template touched: same bytes, answered. A template rewritten with the same length and time stamp: not answered.
        var template = Directory.EnumerateFiles(Path.Combine(repo.Repo.ModelRoot, "templates", "e2e"), "*.scriban", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First();
        Touch(template);
        await AssertReplayedAsync(repo);
        var original = await File.ReadAllBytesAsync(template, Ct);
        RewriteKeepingStat(template, (byte)' ', (byte)'\t');
        Assert.False((await OneShotAsync(repo)).Replayed);
        RewriteKeepingStat(template, (byte)'\t', (byte)' ');
        Assert.Equal(original, await File.ReadAllBytesAsync(template, Ct));
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);

        // The committed manifest touched: answered. Rewritten with the same length and time stamp (a checkout within the same tick):
        // not answered; the full run writes the manifest it describes again.
        var manifest = repo.Repo.PathOf(".maquettiste/manifest/e2e.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifest, Ct);
        Touch(manifest);
        await AssertReplayedAsync(repo);
        RewriteKeepingStat(manifest, (byte)'a', (byte)'b');
        var rewritten = await OneShotAsync(repo);
        Assert.False(rewritten.Replayed);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, rewritten.Result);
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifest, Ct));
        await AssertReplayedAsync(repo);
    }

    [Fact]
    public async Task A_schema_snapshot_is_compared_by_content()
    {
        await using var repo = E2ERepo.Create(migrations: true);
        for (var i = 0; i < 4 && !File.Exists(RecordPath(repo)); i++)
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, (await OneShotAsync(repo)).Result);
        await AssertReplayedAsync(repo);
        var snapshot = Directory.EnumerateFiles(Path.Combine(repo.Repo.ModelRoot, "snapshots")).Order(StringComparer.Ordinal).First();

        Touch(snapshot);
        await AssertReplayedAsync(repo);

        // A snapshot rewritten with the same length and time stamp (another branch's, checked out within the same tick).
        RewriteKeepingStat(snapshot, (byte)'a', (byte)'b');
        var changed = await OneShotAsync(repo);
        Assert.False(changed.Replayed);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, changed.Result);
        for (var i = 0; i < 4 && !File.Exists(RecordPath(repo)); i++)
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, (await OneShotAsync(repo)).Result);
        await AssertReplayedAsync(repo);
    }

    [Fact]
    public async Task A_record_with_a_path_the_file_system_refuses_falls_back_to_the_full_run()
    {
        await using var repo = E2ERepo.Create();
        await OneShotAsync(repo);
        await AssertReplayedAsync(repo);

        // A well-formed, intact record whose model path holds a NUL (FileInfo throws ArgumentException, wrapped by the parallel check).
        var record = RunRecord.Decode(await File.ReadAllBytesAsync(RecordPath(repo), Ct));
        Assert.NotNull(record);
        var files = record.ModelFiles.ToList();
        files[files.Count / 2] = files[files.Count / 2] with { Path = "model/entities/bad\0name.json" };
        await File.WriteAllBytesAsync(RecordPath(repo), (record with { ModelFiles = files }).Encode(), Ct);
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);

        // The same with a sidecar path (checked after the listing, also in parallel).
        record = RunRecord.Decode(await File.ReadAllBytesAsync(RecordPath(repo), Ct));
        Assert.NotNull(record);
        await File.WriteAllBytesAsync(RecordPath(repo), (record with { Sidecars = [.. record.Sidecars, new Maquettiste.Engine.Loading.FileStamp("model/a\0.md", 1, 1)] }).Encode(), Ct);
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);
    }

    [Fact]
    public async Task A_hand_edit_is_never_answered_from_the_record()
    {
        await using var repo = E2ERepo.Create();
        await OneShotAsync(repo);
        var original = repo.Repo.ReadFile("db/e2e/entities/customer.txt");

        repo.Repo.WriteFile("db/e2e/entities/customer.txt", original + "hand edit\n");
        var edited = await OneShotAsync(repo);
        Assert.False(edited.Replayed);
        Assert.Equal(RunOutcome.Conflicts, edited.Result.Outcome);
        Assert.False(File.Exists(RecordPath(repo)), "a run with conflicts leaves no record");
        Assert.Equal(RunOutcome.Conflicts, (await OneShotAsync(repo)).Result.Outcome);

        // Under the skip policy the run succeeds with a warning, but the next run would report the edit again: no record either.
        var skip = new GenerationRequest { Jobs = 2, HandEdits = HandEditPolicy.Skip };
        var skipped = await OneShotAsync(repo, skip);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, skipped.Result);
        Assert.Contains(skipped.Result.Diagnostics, d => d.Rule == "MQ6009");
        Assert.False(File.Exists(RecordPath(repo)));
        var again = await OneShotAsync(repo, skip);
        Assert.False(again.Replayed);
        Assert.Contains(again.Result.Diagnostics, d => d.Rule == "MQ6009");

        repo.Repo.WriteFile("db/e2e/entities/customer.txt", original);
        await AssertFullAsync(repo);
        await AssertReplayedAsync(repo);
    }

    [Fact]
    public async Task A_run_that_advances_a_schema_snapshot_writes_no_record_so_the_next_run_renders_what_the_snapshot_changes()
    {
        await using var repo = E2ERepo.Create(migrations: true);
        var first = await OneShotAsync(repo);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, first.Result);
        Assert.False(File.Exists(RecordPath(repo)), "the snapshot moved after the units read the diff");

        // The runs that follow converge; once one records, the answer from the record equals a full run.
        for (var i = 0; i < 3 && !File.Exists(RecordPath(repo)); i++)
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, (await OneShotAsync(repo)).Result);
        Assert.True(File.Exists(RecordPath(repo)));
        var replayed = await OneShotAsync(repo);
        Assert.True(replayed.Replayed);
        AssertSameResult(await repo.RunAsync(), replayed.Result);
    }

    [Fact]
    public async Task A_long_lived_host_neither_writes_nor_reads_the_record()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();
        await repo.ApplyAsync();
        Assert.False(File.Exists(RecordPath(repo)));

        await OneShotAsync(repo);
        Assert.True(File.Exists(RecordPath(repo)));
        await repo.RestartAsync();
        var fresh = await repo.RunAsync();
        Assert.NotNull(repo.Store.Current); // loaded: the record is not used without ReuseLastRun
        Assert.Equal(0, fresh.UnitsRendered);
    }

    private static async Task<(GenerationResult Result, bool Replayed)> OneShotAsync(E2ERepo repo, GenerationRequest? request = null)
    {
        var store = new ModelStore(repo.Options);
        await using (store)
        {
            var service = new GenerationService(store, repo.Options) { ReuseLastRun = true };
            var result = await service.RunAsync(request ?? new GenerationRequest { Jobs = 2 }, null, Ct);
            return (result, store.Current is null);
        }
    }

    /// <summary>A one-shot run that is not answered from the record, gives the full run's outcome, and succeeds.</summary>
    private static async Task<GenerationResult> AssertFullAsync(E2ERepo repo)
    {
        var (result, replayed) = await OneShotAsync(repo);
        Assert.False(replayed, "expected a full run");
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        return result;
    }

    /// <summary>A one-shot run that is answered from the record, with the result of a full run of a fresh host.</summary>
    private static async Task AssertReplayedAsync(E2ERepo repo)
    {
        var (result, replayed) = await OneShotAsync(repo);
        Assert.True(replayed, "expected an answer from the record");
        await repo.RestartAsync();
        AssertSameResult(await repo.RunAsync(), result);
    }

    private static void AssertSameResult(GenerationResult expected, GenerationResult actual)
    {
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.Changes, actual.Changes);
        Assert.Equal(expected.UnitsRendered, actual.UnitsRendered);
        Assert.Equal(expected.UnitsSkipped, actual.UnitsSkipped);
        Assert.Equal(expected.FilesWritten, actual.FilesWritten);
        Assert.Equal(expected.FilesDeleted, actual.FilesDeleted);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private static void AssertSameTree(SortedDictionary<string, byte[]> expected, SortedDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (path, bytes) in expected)
            Assert.True(bytes.AsSpan().SequenceEqual(actual[path]), path);
    }

    private static string RecordPath(E2ERepo repo) => Path.Combine(repo.Repo.CacheDirectory, RunRecord.FileName);

    private static SortedDictionary<string, DateTime> CacheWriteTimes(E2ERepo repo) =>
        new(Directory.EnumerateFiles(repo.Repo.CacheDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.Ordinal), StringComparer.Ordinal);

    private static void Touch(string path) => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

    /// <summary>
    /// Replaces the last <paramref name="from"/> byte of a file with <paramref name="to"/> and restores its time stamp: other bytes,
    /// the same length and the same stat (a rewrite within one time-stamp tick).
    /// </summary>
    private static void RewriteKeepingStat(string path, byte from, byte to)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        var at = Array.LastIndexOf(bytes, from);
        Assert.True(at >= 0, "no byte to replace in " + path);
        bytes[at] = to;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);
    }

    private static string CustomerSource(E2ERepo repo) =>
        repo.Outputs().Single(o => o.Key.StartsWith("src/Generated/demo/", StringComparison.Ordinal) && o.Key.EndsWith("/Customer.g.cs", StringComparison.Ordinal)).Value;
}
