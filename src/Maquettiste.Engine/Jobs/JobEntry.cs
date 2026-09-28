using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Jobs;

/// <summary>The mutable state of one job inside <see cref="JobQueue"/>; every member is read and written under the queue's lock.</summary>
/// <param name="id">The job id.</param>
/// <param name="request">The request.</param>
/// <param name="queuedUtc">When it was queued.</param>
internal sealed class JobEntry(string id, JobRequest request, DateTimeOffset queuedUtc)
{
    /// <summary>The job id.</summary>
    public string Id { get; } = id;

    /// <summary>The request.</summary>
    public JobRequest Request { get; } = request;

    /// <summary>When it was queued.</summary>
    public DateTimeOffset QueuedUtc { get; } = queuedUtc;

    /// <summary>The state.</summary>
    public JobState State { get; set; } = JobState.Queued;

    /// <summary>When it started running.</summary>
    public DateTimeOffset? StartedUtc { get; set; }

    /// <summary>When it finished.</summary>
    public DateTimeOffset? FinishedUtc { get; set; }

    /// <summary>The latest progress.</summary>
    public ProgressUpdate? Progress { get; set; }

    /// <summary>The plan result.</summary>
    public PlanResult? PlanResult { get; set; }

    /// <summary>The apply result.</summary>
    public ApplyResult? ApplyResult { get; set; }

    /// <summary>The error of a failed job.</summary>
    public string? Error { get; set; }

    /// <summary>Cancels the running job.</summary>
    public CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Whether <see cref="JobQueue.Cancel"/> asked for the cancellation (as opposed to a shutdown).</summary>
    public bool CancelRequested { get; set; }

    /// <summary>A snapshot.</summary>
    /// <param name="position">The queue position, while queued.</param>
    /// <returns>The job info.</returns>
    public JobInfo ToInfo(int? position) =>
        new(Id, Request.Kind, State, State == JobState.Queued ? position : null, Progress, PlanResult, ApplyResult, Error, QueuedUtc, StartedUtc, FinishedUtc);

    /// <summary>Restores an entry from a persisted record (resume after a restart).</summary>
    /// <param name="job">The record.</param>
    /// <param name="request">The request, rebuilt from the record.</param>
    /// <returns>The entry, queued again.</returns>
    public static JobEntry Resume(JobInfo job, JobRequest request) => new(job.Id, request, job.QueuedUtc);
}
