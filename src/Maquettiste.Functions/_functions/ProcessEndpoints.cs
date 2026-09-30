using System.Globalization;
using System.Text;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Processes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>
/// The process operations (phase-3-design.md section 4.4): simulate, record a scenario, verify, export and import XState configs,
/// and sync a lifecycle's bound enum. Simulation keeps no server state: every call replays its inputs from the start.
/// </summary>
public static class ProcessEndpoints
{
    /// <summary>The response header that counts the export's MQ9406 notes.</summary>
    public const string DiagnosticsHeader = "X-Maquettiste-Diagnostics";

    /// <summary>The export's count of warnings (MQ9404: an opaque pointer that was kept but has no place in the config).</summary>
    public const string WarningsHeader = "X-Maquettiste-Warnings";

    /// <summary>Runs a process (or an unsaved draft of it) from the start through a list of inputs.</summary>
    /// <param name="id">The process id.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the simulation, 422 for a draft that does not validate, 404 or 400.</returns>
    [HttpPost("/api/processes/{id}/simulate")]
    public static Task<IResult> Simulate(string id, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        var (request, error) = await Api.ReadJsonAsync(context.Request, new SimulateRequest(), ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        return Answer(await store.SimulateProcessAsync(id, request!, ct).ConfigureAwait(false), "process", id);
    });

    /// <summary>Records a scenario whose expectations and outcome are filled from the engine's replay of the inputs.</summary>
    /// <param name="id">The process id.</param>
    /// <param name="dryRun">Returns the scenario without writing it (default false).</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>201 with the scenario, 200 for a dry run, 422, 404 or 400.</returns>
    [HttpPost("/api/processes/{id}/scenarios")]
    public static Task<IResult> Record(string id, string? dryRun, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        var (request, error) = await Api.ReadJsonAsync<RecordScenarioRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        var call = await store.RecordScenarioAsync(id, request!, dryRun == "true", ChangeSource.Editor, ct).ConfigureAwait(false);
        if (call is { Status: ProcessCallStatus.Ok, Value.Applied: true } saved)
        {
            Api.SetETag(context, saved.Value.Hash);
            context.Response.Headers.Location = "/api/model/elements/" + saved.Value.Id;
            return Api.Json(saved.Value, StatusCodes.Status201Created);
        }

        return Answer(call, "process", id);
    });

    /// <summary>Replays the scenarios of a process (all, or the ones named) and reports each one's first failure.</summary>
    /// <param name="id">The process id.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the results, 404 or 400.</returns>
    [HttpPost("/api/processes/{id}/verify")]
    public static Task<IResult> Verify(string id, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        var (request, error) = await Api.ReadJsonAsync(context.Request, new VerifyRequest(null), ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        return Answer(await store.VerifyScenariosAsync(id, request!.Scenarios, ct).ConfigureAwait(false), "process", id);
    });

    /// <summary>A process as an XState machine config, in canonical key order; the header counts the MQ9406 notes.</summary>
    /// <param name="id">The process id.</param>
    /// <param name="format">The format: <c>xstate</c> (the default and only one).</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the config, 404 or 400.</returns>
    [HttpGet("/api/processes/{id}/export")]
    public static Task<IResult> Export(string id, string? format, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        if (format is not (null or "" or XStateProjection.Format))
            return Api.BadRequest($"format must be '{XStateProjection.Format}', not '{format}'.");
        var call = await store.ExportProcessAsync(id, ct).ConfigureAwait(false);
        if (call.Status != ProcessCallStatus.Ok)
            return Answer(call, "process", id);
        context.Response.Headers[DiagnosticsHeader] = call.Value!.Diagnostics.Count(d => d.Rule == "MQ9406").ToString(CultureInfo.InvariantCulture);
        context.Response.Headers[WarningsHeader] = call.Value.Diagnostics.Count(d => d.Rule != "MQ9406").ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Text(call.Value.Json, "application/json", Encoding.UTF8);
    });

    /// <summary>Imports an XState machine config as a new process or over an existing one; a dry run unless <c>dryRun=false</c>.</summary>
    /// <param name="format">The format: <c>xstate</c>.</param>
    /// <param name="dryRun">Previews without writing (default true).</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the document (and the save when applied), 409, 422, 404 or 400.</returns>
    [HttpPost("/api/processes/import")]
    public static Task<IResult> Import(string? format, string? dryRun, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        if (format is not (null or "" or XStateProjection.Format))
            return Api.BadRequest($"format must be '{XStateProjection.Format}', not '{format}'.");
        var (request, error) = await Api.ReadJsonAsync<ProcessImportRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        return Answer(await store.ImportProcessAsync(request!, dryRun != "false", ChangeSource.Editor, ct).ConfigureAwait(false), "process", request!.Into ?? "");
    });

    /// <summary>Syncs a lifecycle's bound enum with its root-level states; a dry run returns the plan.</summary>
    /// <param name="id">The process id.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the plan, 409, 422 (refused), 404 or 400.</returns>
    [HttpPost("/api/processes/{id}/sync-enum")]
    public static Task<IResult> SyncEnum(string id, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        var (request, error) = await Api.ReadJsonAsync(context.Request, new SyncEnumRequest(null, null), ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        return Answer(await store.SyncEnumAsync(id, request!.DryRun == true, request.ExpectedHash, ChangeSource.Editor, ct).ConfigureAwait(false), "process", id);
    });

    // Ok -> 200 with the value; Invalid -> 422 with the value, else a validation report of the diagnostics; Conflict -> 409 with the
    // value, else a problem; NotFound -> 404; BadRequest -> 400.
    private static IResult Answer<T>(ProcessCall<T> call, string what, string id) => call.Status switch
    {
        ProcessCallStatus.Ok => Api.Json(call.Value),
        ProcessCallStatus.Invalid => call.Value is not null
            ? Api.Json(call.Value, StatusCodes.Status422UnprocessableEntity)
            : Api.Json(ValidationReport.From(call.Diagnostics), StatusCodes.Status422UnprocessableEntity),
        ProcessCallStatus.Conflict => call.Value is not null
            ? Api.Json(call.Value, StatusCodes.Status409Conflict)
            : Api.Problem("conflict", call.Detail ?? "The element changed since it was read.", StatusCodes.Status409Conflict),
        ProcessCallStatus.NotFound => Api.Problem("not-found", call.Detail ?? $"No {what} has the id {id}.", StatusCodes.Status404NotFound),
        _ => Api.BadRequest(call.Detail ?? "The request is not valid."),
    };
}

/// <summary>The body of <c>POST /api/processes/{id}/verify</c>.</summary>
/// <param name="Scenarios">Scenario ids (or names); every scenario of the process when absent.</param>
public sealed record VerifyRequest(IReadOnlyList<string>? Scenarios);

/// <summary>The body of <c>POST /api/processes/{id}/sync-enum</c>.</summary>
/// <param name="DryRun">Returns the plan without writing (default false).</param>
/// <param name="ExpectedHash">The process hash as read (409 when it changed).</param>
public sealed record SyncEnumRequest(bool? DryRun, string? ExpectedHash);
