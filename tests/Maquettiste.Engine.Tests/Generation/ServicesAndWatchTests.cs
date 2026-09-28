using System.Collections.Concurrent;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

public sealed class ServicesAndWatchTests
{
    [Fact]
    public void The_composition_root_builds_without_io_and_guards_engine_writes()
    {
        var root = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "no-io-" + Environment.ProcessId);
        var options = new EngineOptions { RepoRoot = Path.Combine(root, "repo"), CacheDirectory = Path.Combine(root, "cache") };
        var services = EngineServices.Create(options);
        var store = new ModelStore(options);
        _ = new GenerationService(store, options);
        _ = new JobQueue(new GenerationService(store, options), options);

        Assert.False(Directory.Exists(root));
        Assert.False(services.EnginePaths.Check("out/x.txt").Allowed);
        Assert.True(services.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(options.CacheDirectory, "plans", "x")).Allowed);
        Assert.False(services.EnginePaths.CheckEngineWrite(WriteTarget.Cache, Path.Combine(root, "elsewhere")).Allowed);
        Assert.NotSame(services.CreateRenderer(), services.CreateRenderer());
    }

    [Fact]
    public async Task Plan_and_job_stores_refuse_writes_the_guard_refuses()
    {
        using var repo = new TempRepo();
        using var other = new TempRepo();
        var guard = new Engine.Writing.OutputPathPolicy(other.Options, null);
        var plans = new PlanStore(repo.Options, guard);
        var id = repo.Options.EffectiveIdGenerator.NewId();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => plans.WriteBlobAsync(id, Engine.Hashing.ContentHash.Of("x"), new byte[] { 1 }, TestContext.Current.CancellationToken));
        var jobs = new Engine.Jobs.JobStore(repo.Options, guard);
        var job = new JobInfo(id, JobKind.Plan, JobState.Queued, 0, null, null, null, null, DateTimeOffset.UnixEpoch, null, null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => jobs.SaveAsync(new Engine.Jobs.JobRecord(job, new JobRequest(JobKind.Plan, new GenerationRequest(), null)),
            TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(repo.CacheDirectory));
    }

    [Fact]
    public async Task The_watch_hook_debounces_changes_into_one_incremental_run()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        var results = new ConcurrentQueue<GenerationResult>();
        // The CLI's real debounce (250 ms): a burst of five notifications 20 ms apart must land inside one window even on a loaded
        // CI runner, where a 100 ms window let a late notification start a second run.
        var watcher = new GenerationWatcher(f.Service, new GenerationRequest(), TimeSpan.FromMilliseconds(250), (result, _) =>
        {
            results.Enqueue(result);
            return ValueTask.CompletedTask;
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(GenerationFixture.Ct);
        var loop = watcher.RunAsync(stop.Token);

        watcher.Notify([Path.Combine(f.Repo.ModelRoot, "manifest", "basic.json"), ".maquettiste/.cache/journal.jsonl", "out/.index.txt.mq-1.tmp"]);
        await Task.Delay(300, GenerationFixture.Ct);
        Assert.Empty(results);

        await f.WriteModelAsync(b => Models.Shop(b, customerName: "text"));
        for (var i = 0; i < 5; i++)
        {
            watcher.Notify([".maquettiste/model/entities/customer.json"]);
            await Task.Delay(20, GenerationFixture.Ct);
        }

        for (var i = 0; i < 200 && results.IsEmpty; i++)
            await Task.Delay(50, GenerationFixture.Ct);
        await Task.Delay(300, GenerationFixture.Ct);
        await stop.CancelAsync();
        await loop;

        var run = Assert.Single(results);
        Assert.Equal(RunOutcome.Succeeded, run.Outcome);
        Assert.Equal(2, run.UnitsRendered);
        Assert.Equal(1, watcher.Runs);
    }

    [Fact]
    public async Task The_watch_hook_runs_after_an_editor_save()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.Store.LoadAsync(GenerationFixture.Ct);
        var runs = new TaskCompletionSource<GenerationResult>();
        var watcher = new GenerationWatcher(f.Service, new GenerationRequest(), TimeSpan.FromMilliseconds(50), (result, _) =>
        {
            runs.TrySetResult(result);
            return ValueTask.CompletedTask;
        });
        using var subscription = watcher.Attach(f.Store);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(GenerationFixture.Ct);
        var loop = watcher.RunAsync(stop.Token);

        var json = """{ "kind": "entity", "name": "Invoice", "key": { "attributes": ["01ARZ3NDEKTSV4RRFFQ69G5FAV"] }, "attributes": [ { "id": "01ARZ3NDEKTSV4RRFFQ69G5FAV", "name": "id", "type": "uuid" } ] }""";
        var saved = await f.Store.CreateAsync(System.Text.Encoding.UTF8.GetBytes(json), ChangeSource.Editor, GenerationFixture.Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);

        var result = await runs.Task.WaitAsync(TimeSpan.FromSeconds(30), GenerationFixture.Ct);
        await stop.CancelAsync();
        await loop;
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.True(f.Repo.Exists("out/entities/Invoice.txt"));
    }
}
