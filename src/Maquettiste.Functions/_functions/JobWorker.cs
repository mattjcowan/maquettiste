using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using StaticSiteHost.Functions;

namespace Maquettiste.Functions;

/// <summary>Runs plan and apply jobs one at a time and publishes their progress and completion (phase2-design.md section 3.6).</summary>
public static class JobWorker
{
    /// <summary>The least time between two <c>job.progress</c> events of one job (at most four per second).</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Subscribes progress (per job: published when 250 ms passed since the last, the stage changed, or <c>done == total</c>) and
    /// completion (the bounded <see cref="JobCompletedEvent"/>), then runs the queue until stopped. On a redeploy a running job stays
    /// <c>running</c> and the next build resumes it through the run journal.
    /// </summary>
    /// <param name="stoppingToken">Stops the worker.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="events">The publisher.</param>
    /// <returns>A task that completes when stopped.</returns>
    [BackgroundService]
    public static async Task Run(CancellationToken stoppingToken, JobQueue queue, EditorEvents events)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(events);
        var throttle = new ProgressThrottle(ProgressInterval, TimeProvider.System);
        using var progress = queue.OnProgress(async (job, update) =>
        {
            if (throttle.ShouldPublish(job.Id, update))
                await events.PublishJobProgressAsync(job with { Progress = update }, stoppingToken).ConfigureAwait(false);
        });
        using var completed = queue.OnCompleted(async job =>
        {
            throttle.Forget(job.Id);
            await events.PublishJobCompletedAsync(job, CancellationToken.None).ConfigureAwait(false);
        });
        events.Worker = "running";
        try
        {
            await queue.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            events.Worker = "stopped";
        }
    }
}

/// <summary>Decides which progress updates of a job are published: the first, a new stage, the last of a stage, or one 250 ms after the last.</summary>
/// <param name="interval">The least time between two published updates of one job within a stage.</param>
/// <param name="time">The clock.</param>
public sealed class ProgressThrottle(TimeSpan interval, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long Timestamp, PipelineStage Stage)> _last = new(StringComparer.Ordinal);

    /// <summary>Whether to publish an update, recording it when so.</summary>
    /// <param name="jobId">The job.</param>
    /// <param name="update">The update.</param>
    /// <returns><see langword="true"/> to publish.</returns>
    public bool ShouldPublish(string jobId, ProgressUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var now = time.GetTimestamp();
        lock (_gate)
        {
            if (_last.TryGetValue(jobId, out var last) && last.Stage == update.Stage && update.Done != update.Total
                && time.GetElapsedTime(last.Timestamp, now) < interval)
                return false;
            _last[jobId] = (now, update.Stage);
            return true;
        }
    }

    /// <summary>Forgets a finished job.</summary>
    /// <param name="jobId">The job.</param>
    public void Forget(string jobId)
    {
        lock (_gate)
            _last.Remove(jobId);
    }
}
