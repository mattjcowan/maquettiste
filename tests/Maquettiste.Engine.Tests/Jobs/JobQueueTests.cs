using System.Collections.Concurrent;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Generation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Jobs;

/// <summary>The job queue (host-contracts requirements 23 to 28).</summary>
public sealed class JobQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plan_job_runs_and_its_record_persists_with_timestamps()
    {
        await using var h = await Harness.CreateAsync();
        var completed = new TaskCompletionSource<JobInfo>();
        using var _ = h.Queue.OnCompleted(job =>
        {
            completed.TrySetResult(job);
            return ValueTask.CompletedTask;
        });

        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var queued));
        Assert.Equal(JobState.Queued, queued.State);
        Assert.Equal(0, queued.QueuePosition);
        Assert.Equal(Harness.Start, queued.QueuedUtc);

        var loop = h.StartAsync();
        var done = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(queued.Id, done.Id);
        Assert.Equal(RunOutcome.Succeeded, done.PlanResult!.Outcome);
        Assert.NotNull(done.StartedUtc);
        Assert.NotNull(done.FinishedUtc);
        Assert.True(done.StartedUtc >= done.QueuedUtc && done.FinishedUtc >= done.StartedUtc);
        Assert.Null(done.QueuePosition);

        await h.StopAsync(loop);
        var file = Path.Combine(h.Fixture.Repo.CacheDirectory, "jobs", done.Id + ".json");
        Assert.True(File.Exists(file));

        // A new queue (a new process) answers from the record.
        await using var again = new JobQueue(h.Fixture.Service, h.Options);
        var stored = await again.GetAsync(done.Id, Ct);
        Assert.NotNull(stored);
        Assert.Equal(JobState.Succeeded, stored.State);
        Assert.Equal(done.FinishedUtc, stored.FinishedUtc);
        Assert.Equal(done.PlanResult!.Plan!.Id, stored.PlanResult!.Plan!.Id);
        Assert.Empty(stored.PlanResult.Plan.Units);
        Assert.Empty(stored.PlanResult.Plan.Changes);
        Assert.NotNull(await h.Fixture.Service.GetPlanAsync(done.PlanResult.Plan.Id, Ct));
    }

    [Fact]
    public async Task An_apply_job_applies_a_plan()
    {
        await using var h = await Harness.CreateAsync();
        var plan = (await h.Fixture.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!;
        var loop = h.StartAsync();
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Apply, null, plan.Id), out var job));
        var done = await h.WaitAsync(job.Id);
        await h.StopAsync(loop);

        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(RunOutcome.Succeeded, done.ApplyResult!.Outcome);
        Assert.Equal(3, h.Fixture.Outputs().Count);
    }

    [Fact]
    public async Task An_apply_job_for_an_unknown_plan_fails_with_an_error()
    {
        await using var h = await Harness.CreateAsync();
        var loop = h.StartAsync();
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Apply, null, "01ARZ3NDEKTSV4RRFFQ69G5FAV"), out var job));
        var done = await h.WaitAsync(job.Id);
        await h.StopAsync(loop);
        Assert.Equal(JobState.Failed, done.State);
        Assert.NotNull(done.Error);
    }

    [Fact]
    public async Task The_queue_is_bounded_and_shows_positions()
    {
        await using var h = await Harness.CreateAsync(capacity: 2);
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var first));
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var second));
        Assert.False(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var third));
        Assert.Null(third);
        Assert.Equal(0, first.QueuePosition);
        Assert.Equal(1, second.QueuePosition);

        var list = await h.Queue.ListAsync(Ct);
        Assert.Equal([second.Id, first.Id], list.Select(j => j.Id));

        Assert.True(h.Queue.Cancel(first.Id));
        Assert.Equal(JobState.Cancelled, (await h.Queue.GetAsync(first.Id, Ct))!.State);
        Assert.Equal(0, (await h.Queue.GetAsync(second.Id, Ct))!.QueuePosition);
        Assert.False(h.Queue.Cancel(first.Id));
        Assert.False(h.Queue.Cancel("01ARZ3NDEKTSV4RRFFQ69G5FAV"));
        Assert.Throws<ArgumentException>(() => h.Queue.TryEnqueue(new JobRequest(JobKind.Apply, null, null), out _));
    }

    [Fact]
    public async Task One_job_runs_at_a_time_and_a_running_job_can_be_cancelled()
    {
        await using var h = await Harness.CreateAsync();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        h.Fixture.Renderer.BeforeUnit = async (_, ct) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
        };
        var loop = h.StartAsync();
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var running));
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var waiting));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(JobState.Running, (await h.Queue.GetAsync(running.Id, Ct))!.State);
        var queued = (await h.Queue.GetAsync(waiting.Id, Ct))!;
        Assert.Equal(JobState.Queued, queued.State);
        Assert.Equal(0, queued.QueuePosition);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(h.Queue.Cancel(running.Id));
        var cancelled = await h.WaitAsync(running.Id);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());
        Assert.Equal(JobState.Cancelled, cancelled.State);

        gate.TrySetResult();
        var next = await h.WaitAsync(waiting.Id);
        Assert.Equal(JobState.Succeeded, next.State);
        await h.StopAsync(loop);
    }

    [Fact]
    public async Task Progress_reaches_subscribers()
    {
        await using var h = await Harness.CreateAsync();
        var updates = new ConcurrentQueue<(string Id, ProgressUpdate Update)>();
        using var subscription = h.Queue.OnProgress((job, update) =>
        {
            updates.Enqueue((job.Id, update));
            return ValueTask.CompletedTask;
        });
        var loop = h.StartAsync();
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var job));
        await h.WaitAsync(job.Id);
        await Poll(() => updates.Any(u => u.Update.Stage == PipelineStage.Write));
        await h.StopAsync(loop);
        Assert.All(updates, u => Assert.Equal(job.Id, u.Id));
    }

    [Fact]
    public async Task Jobs_left_queued_or_running_resume_after_a_restart()
    {
        await using var h = await Harness.CreateAsync();
        var entered = new TaskCompletionSource();
        h.Fixture.Renderer.BeforeUnit = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = h.Queue.RunAsync(stop.Token);
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var first));
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var second));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The host stops the old build: the running job is interrupted, not cancelled.
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var store = h.Fixture.Services.Jobs;
        Assert.Equal(JobState.Running, (await store.LoadAsync(first.Id, Ct))!.Job.State);
        Assert.Equal(JobState.Queued, (await store.LoadAsync(second.Id, Ct))!.Job.State);

        // The new build resumes both, in order.
        h.Fixture.Renderer = new FakeRenderer();
        await using var restarted = new JobQueue(h.Fixture.Service, h.Options);
        var order = new ConcurrentQueue<string>();
        using var _ = restarted.OnCompleted(job =>
        {
            order.Enqueue(job.Id);
            return ValueTask.CompletedTask;
        });
        using var stop2 = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop2 = restarted.RunAsync(stop2.Token);
        await Poll(() => order.Count == 2);
        Assert.Equal([first.Id, second.Id], order);
        Assert.Equal(JobState.Succeeded, (await restarted.GetAsync(first.Id, Ct))!.State);
        await stop2.CancelAsync();
        await loop2;
    }

    [Fact]
    public async Task An_apply_job_left_running_resumes_after_a_restart()
    {
        await using var f = await GenerationFixture.CreateAsync(b =>
        {
            for (var i = 0; i < 40; i++)
                b.Entity("Thing" + i).Key("id", "uuid");
        }, "basic");
        var plan = (await f.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!;
        var paused = new TaskCompletionSource();
        var services = f.Services with { WriterFactory = paths => new PausingWriter(f.Services.CreateWriter(paths), 10, paused) };
        var pausing = new GenerationService(f.Store, f.Repo.Options, services);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using (var queue = new JobQueue(pausing, f.Repo.Options))
        {
            var loop = queue.RunAsync(stop.Token);
            Assert.True(queue.TryEnqueue(new JobRequest(JobKind.Apply, null, plan.Id), out var job));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await Poll(() => Written(f) >= 5);

            // The host stops the old build mid-write: the job stays "running" and the journal unfinished.
            await stop.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal(JobState.Running, (await f.Services.Jobs.LoadAsync(job.Id, Ct))!.Job.State);
            Assert.InRange(Written(f), 5, 40);
            Assert.True(File.Exists(Path.Combine(f.Repo.ModelRoot, ".cache", "journal.jsonl")));

            // The new build resumes the apply of the same plan.
            await using var restarted = new JobQueue(f.Service, f.Repo.Options);
            var completed = new TaskCompletionSource<JobInfo>();
            using var _ = restarted.OnCompleted(done =>
            {
                completed.TrySetResult(done);
                return ValueTask.CompletedTask;
            });
            using var stop2 = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var loop2 = restarted.RunAsync(stop2.Token);
            var finished = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await stop2.CancelAsync();
            await loop2;

            Assert.Equal(job.Id, finished.Id);
            Assert.Equal(JobState.Succeeded, finished.State);
            Assert.Equal(RunOutcome.Succeeded, finished.ApplyResult!.Outcome);
            Assert.DoesNotContain(finished.ApplyResult.Result!.Changes, c => c.Kind is FileChangeKind.HandEdited or FileChangeKind.Conflict);
            Assert.Equal(41, f.Outputs().Count);
            Assert.False(File.Exists(Path.Combine(f.Repo.ModelRoot, ".cache", "journal.jsonl")));
        }
    }

    [Fact]
    public async Task Listing_merges_memory_and_records_newest_first()
    {
        await using var h = await Harness.CreateAsync();
        var loop = h.StartAsync();
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var a));
        await h.WaitAsync(a.Id);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(h.Queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest(), null), out var b));
        await h.WaitAsync(b.Id);
        await h.StopAsync(loop);

        var list = await h.Queue.ListAsync(Ct);
        Assert.Equal([b.Id, a.Id], list.Select(j => j.Id));
    }

    private static async Task Poll(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++)
            await Task.Delay(50, Ct);
        Assert.True(condition());
    }

    /// <summary>Output files written so far, counted without reading them (staged <c>.*.tmp</c> files are left out).</summary>
    private static int Written(GenerationFixture f)
    {
        var root = Path.Combine(f.Repo.RepoRoot, "out");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(p => !Path.GetFileName(p).StartsWith('.'))
            : 0;
    }

    /// <summary>A writer that passes <paramref name="after"/> units to the real writer, then waits until the run is cancelled.</summary>
    private sealed class PausingWriter(IOutputWriter inner, int after, TaskCompletionSource paused) : IOutputWriter
    {
        public Task<WriteSummary> WriteAsync(IAsyncEnumerable<ProcessedUnit> units, WriteContext context, IProgress<ProgressUpdate>? progress,
            CancellationToken ct) => inner.WriteAsync(Pause(units, ct), context, progress, ct);

        private async IAsyncEnumerable<ProcessedUnit> Pause(IAsyncEnumerable<ProcessedUnit> units, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var passed = 0;
            await foreach (var unit in units.WithCancellation(ct))
            {
                if (passed++ == after)
                {
                    paused.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }

                yield return unit;
            }
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        private Harness(GenerationFixture fixture, int capacity)
        {
            Fixture = fixture;
            Options = fixture.Repo.Options with { TimeProvider = Time };
            Queue = new JobQueue(fixture.Service, Options, capacity);
        }

        public GenerationFixture Fixture { get; }

        public ManualTime Time { get; } = new(Start);

        public EngineOptions Options { get; }

        public JobQueue Queue { get; }

        private CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        public static async Task<Harness> CreateAsync(int capacity = 16) =>
            new(await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic"), capacity);

        public Task StartAsync() => Queue.RunAsync(Stop.Token);

        public async Task StopAsync(Task loop)
        {
            await Stop.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        public async Task<JobInfo> WaitAsync(string id)
        {
            for (var i = 0; i < 600; i++)
            {
                var job = await Queue.GetAsync(id, Ct);
                if (job is { State: JobState.Succeeded or JobState.Failed or JobState.Cancelled })
                    return job;
                await Task.Delay(50, Ct);
            }

            throw new TimeoutException("Job " + id + " did not finish.");
        }

        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync();
            await Queue.DisposeAsync();
            Stop.Dispose();
            await Fixture.DisposeAsync();
        }
    }
}
