using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The body of <c>POST /api/generate/apply</c>.</summary>
/// <param name="PlanId">The plan to apply.</param>
public sealed record ApplyRequest(string? PlanId);

/// <summary>The body of <c>POST /api/generate/explain</c>.</summary>
/// <param name="Pack">The pack.</param>
/// <param name="Unit">The unit id.</param>
/// <param name="ElementId">The element, absent for model scope.</param>
/// <param name="PlanId">A plan to take the reason and causes from; a new dry-run plan of the pack otherwise.</param>
/// <param name="Packs">The run's pack selection; a pack outside it answers <c>not-selected</c>.</param>
public sealed record ExplainRequest(string? Pack, string? Unit, string? ElementId, string? PlanId, IReadOnlyList<string>? Packs = null);

/// <summary>Plan, per-file diff and apply, as jobs (phase2-design.md section 3.7).</summary>
public static class GenerateEndpoints
{
    /// <summary>
    /// Queues a plan job and returns it at once. The request is forced to <c>includeDiffs: false</c>, <c>stageBarriers: false</c> and
    /// <c>lock: wait</c>; its mode is ignored (a plan is always a dry run, applied later in apply mode).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>202 with the job, 503 when sixteen jobs are queued, or 400.</returns>
    [HttpPost("/api/generate/plan")]
    public static Task<IResult> Plan(HttpContext context, JobQueue queue, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queue);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        var (request, error) = await Api.ReadJsonAsync(context.Request, new GenerationRequest(), ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Jobs is < 1)
            return Api.BadRequest("jobs must be at least 1.");
        var plan = request with { Mode = GenerationMode.Apply, IncludeDiffs = false, StageBarriers = false, Lock = LockMode.Wait };
        return Enqueue(context, queue, new JobRequest(JobKind.Plan, plan, null));
    });

    /// <param name="context">The request.</param>
    /// <summary>The finished plan; <c>units</c> (every unit's read keys and outputs) is emptied unless <paramref name="units"/> is true.</summary>
    /// <param name="id">The plan id.</param>
    /// <param name="units">Whether to keep the units.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpGet("/api/generate/plan/{id}")]
    public static Task<IResult> GetPlan(HttpContext context, string id, bool units, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(generation);
        var plan = Api.IsUlid(id) ? await generation.GetPlanAsync(id, ct).ConfigureAwait(false) : null;
        if (plan is null)
            return Api.NotFound("plan", id);
        return Api.Json(units ? plan : plan with { Units = [] });
    });

    /// <summary>One unit of a stored plan: reason, causes and, for a skipped unit, its read keys grouped (generation-ui.md section 4.3).</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The plan id.</param>
    /// <param name="key">The unit key (query, since keys hold <c>/</c> and <c>:</c>).</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the unit, or 404.</returns>
    [HttpGet("/api/generate/plan/{id}/unit")]
    public static Task<IResult> GetPlanUnit(HttpContext context, string id, string? key, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (!Api.IsUlid(id) || string.IsNullOrEmpty(key))
            return Api.NotFound("plan", id);
        var unit = await generation.GetPlanUnitAsync(id, key, ct).ConfigureAwait(false);
        return unit is null ? Api.Problem("not-found", $"Plan {id} has no unit {key}.", StatusCodes.Status404NotFound) : Api.Json(unit);
    });

    /// <summary>Why a unit does or does not render an element (generation-ui.md section 4.3).</summary>
    /// <param name="context">The request.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 400 or 404.</returns>
    [HttpPost("/api/generate/explain")]
    public static Task<IResult> Explain(HttpContext context, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generation);
        var (request, error) = await Api.ReadJsonAsync<ExplainRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (string.IsNullOrEmpty(request!.Pack) || string.IsNullOrEmpty(request.Unit))
            return Api.BadRequest("pack and unit are required.");
        if (request.PlanId is { } planId && !Api.IsUlid(planId))
            return Api.BadRequest("planId is not a plan id.");
        try
        {
            var result = await generation.ExplainAsync(request.Pack, request.Unit, request.ElementId, request.PlanId, ct,
                request.Packs).ConfigureAwait(false);
            return result is null ? Api.NotFound("pack", request.Pack) : Api.Json(result);
        }
        catch (ArgumentException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>The unified diff of one file of a plan, from the file on disk now to the planned bytes (<c>text/x-diff</c>).</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The plan id.</param>
    /// <param name="path">A repo-relative path from the plan.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpGet("/api/generate/plan/{id}/diff")]
    public static Task<IResult> Diff(HttpContext context, string id, string? path, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (!Api.IsUlid(id) || string.IsNullOrEmpty(path))
            return Api.NotFound("plan", id);
        var diff = await generation.GetPlanDiffAsync(id, path, ct).ConfigureAwait(false);
        if (diff is null)
            return Api.Problem("not-found", $"Plan {id} has no file {path}.", StatusCodes.Status404NotFound);
        return Results.Text(diff, "text/x-diff; charset=utf-8");
    });

    /// <summary>Queues an apply job for a plan. A <c>stale</c>, <c>invalid</c> or <c>conflicts</c> apply is a job in state <c>succeeded</c>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="queue">The job queue.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>202 with the job, 503 or 400.</returns>
    [HttpPost("/api/generate/apply")]
    public static Task<IResult> Apply(HttpContext context, JobQueue queue, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queue);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        var (request, error) = await Api.ReadJsonAsync<ApplyRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (!Api.IsUlid(request!.PlanId))
            return Api.BadRequest("planId must be a plan id (an uppercase ULID).");
        return Enqueue(context, queue, new JobRequest(JobKind.Apply, null, request.PlanId));
    });

    private static IResult Enqueue(HttpContext context, JobQueue queue, JobRequest request)
    {
        if (!queue.TryEnqueue(request, out var job))
        {
            context.Response.Headers.RetryAfter = "5";
            return Api.Problem("queue-full", "The job queue is full.", StatusCodes.Status503ServiceUnavailable);
        }

        context.Response.Headers.Location = "/api/jobs/" + job.Id;
        return Api.Json(job, StatusCodes.Status202Accepted);
    }
}
