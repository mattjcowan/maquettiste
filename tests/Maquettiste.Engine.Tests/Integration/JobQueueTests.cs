using System.Collections.Concurrent;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The job queue over the real pipeline (integration task 14; host-contracts 23 to 28): a plan job and an apply job run with
/// progress, and their records persist for a new queue (a new process) to answer from.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class JobQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plan_job_and_an_apply_job_run_with_progress_and_persist()
    {
        await using var repo = await CheckTests.AppliedAsync();
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var progress = new ConcurrentQueue<(string Job, ProgressUpdate Update)>();
        var completed = new ConcurrentDictionary<string, TaskCompletionSource<JobInfo>>(StringComparer.Ordinal);
        TaskCompletionSource<JobInfo> Completion(string id) => completed.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        string planJobId, applyJobId, planId;
        await using (var queue = new JobQueue(repo.Service, repo.Options))
        {
            using var onProgress = queue.OnProgress((job, update) =>
            {
                progress.Enqueue((job.Id, update));
                return ValueTask.CompletedTask;
            });
            using var onCompleted = queue.OnCompleted(job =>
            {
                Completion(job.Id).TrySetResult(job);
                return ValueTask.CompletedTask;
            });
            var loop = queue.RunAsync(stop.Token);

            Assert.True(queue.TryEnqueue(new JobRequest(JobKind.Plan, new GenerationRequest { Jobs = 2 }, null), out var planJob));
            planJobId = planJob.Id;
            var planned = await Completion(planJob.Id).Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(JobState.Succeeded, planned.State);
            Assert.Equal(RunOutcome.Succeeded, planned.PlanResult!.Outcome);
            planId = planned.PlanResult.Plan!.Id;
            Assert.Contains(planned.PlanResult.Plan.Changes, c => c.Path == "db/e2e/entities/customer.txt" && c.Kind == FileChangeKind.Modified);
            Assert.Contains("length 120", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal); // a plan writes nothing

            Assert.True(queue.TryEnqueue(new JobRequest(JobKind.Apply, null, planId), out var applyJob));
            applyJobId = applyJob.Id;
            var applied = await Completion(applyJob.Id).Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal(JobState.Succeeded, applied.State);
            Assert.Equal(RunOutcome.Succeeded, applied.ApplyResult!.Outcome);
            Assert.Contains("length 150", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal);
            Assert.NotNull(applied.StartedUtc);
            Assert.NotNull(applied.FinishedUtc);

            await stop.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        // Progress reached the handlers for both jobs, with render and write updates.
        Assert.Contains(progress, p => p.Job == planJobId && p.Update.Stage == PipelineStage.Render);
        Assert.Contains(progress, p => p.Job == applyJobId && p.Update.Stage == PipelineStage.Write);

        // A new queue over the same cache folder (a new process) answers from the persisted records.
        await repo.RestartAsync();
        await using var again = new JobQueue(repo.Service, repo.Options);
        var storedPlan = await again.GetAsync(planJobId, Ct);
        var storedApply = await again.GetAsync(applyJobId, Ct);
        Assert.Equal(JobState.Succeeded, storedPlan!.State);
        Assert.Equal(planId, storedPlan.PlanResult!.Plan!.Id);
        Assert.Equal(JobState.Succeeded, storedApply!.State);
        Assert.Equal(RunOutcome.Succeeded, storedApply.ApplyResult!.Outcome);
        var list = await again.ListAsync(Ct);
        Assert.Contains(list, j => j.Id == planJobId);
        Assert.Contains(list, j => j.Id == applyJobId);
        Assert.NotNull(await repo.Service.GetPlanAsync(planId, Ct));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
    }
}
