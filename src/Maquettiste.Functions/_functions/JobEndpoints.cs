using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The job queue: one running job at a time; finished jobs are read from the cache folder.</summary>
public static class JobEndpoints
{
    /// <summary>Queued, running and recent jobs, newest first.</summary>
    /// <param name="context">The request.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200.</returns>
    [HttpGet("/api/jobs")]
    public static Task<IResult> List(HttpContext context, JobQueue queue, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(queue);
        return Api.Json(await queue.ListAsync(ct).ConfigureAwait(false));
    });

    /// <summary>
    /// Clears the run history: every finished job record and every stored plan no queued or running job names; then publishes
    /// <c>jobs.cleared</c>.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="events">The realtime events.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the counts removed.</returns>
    [HttpDelete("/api/jobs")]
    public static Task<IResult> Clear(HttpContext context, JobQueue queue, EditorEvents events, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(events);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        var cleared = await queue.ClearHistoryAsync(ct).ConfigureAwait(false);
        await events.PublishJobsClearedAsync(cleared, CancellationToken.None).ConfigureAwait(false);
        return Api.Json(cleared);
    });

    /// <summary>A job's state, progress, result or error. Judge a finished job by its run outcome, not its state.</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The job id.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpGet("/api/jobs/{id}")]
    public static Task<IResult> Get(HttpContext context, string id, JobQueue queue, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(queue);
        var job = Api.IsUlid(id) ? await queue.GetAsync(id, ct).ConfigureAwait(false) : null;
        return job is null ? Api.NotFound("job", id) : Api.Json(job);
    });

    /// <summary>Cancels a queued job (recorded <c>cancelled</c> at once) or a running one (stops within a second).</summary>
    /// <param name="id">The job id.</param>
    /// <param name="context">The request.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>202 with the job, 404, or 409 when it already finished.</returns>
    [HttpDelete("/api/jobs/{id}")]
    public static Task<IResult> Cancel(string id, HttpContext context, JobQueue queue, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queue);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        var job = Api.IsUlid(id) ? await queue.GetAsync(id, ct).ConfigureAwait(false) : null;
        if (job is null)
            return Api.NotFound("job", id);
        if (job.State is JobState.Succeeded or JobState.Failed or JobState.Cancelled || !queue.Cancel(id))
            return Api.Problem("job-finished", $"Job {id} already finished.", StatusCodes.Status409Conflict);
        return Api.Json(await queue.GetAsync(id, ct).ConfigureAwait(false) ?? job, StatusCodes.Status202Accepted);
    });
}
