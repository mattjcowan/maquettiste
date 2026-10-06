using System.Globalization;
using System.Security.Claims;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The body of <c>POST /api/snapshots</c>.</summary>
/// <param name="Name">The name.</param>
/// <param name="Description">What it is for.</param>
/// <param name="IncludePacks">Whether to hold the template packs too.</param>
public sealed record SnapshotCreateBody(string? Name, string? Description, bool? IncludePacks);

/// <summary>The body of <c>PATCH /api/snapshots/{id}</c>; an absent member keeps its value.</summary>
/// <param name="Name">The new name.</param>
/// <param name="Description">The new description.</param>
/// <param name="Published">Whether it is ready for review.</param>
public sealed record SnapshotPatchBody(string? Name, string? Description, bool? Published);

/// <summary>The body of <c>POST /api/snapshots/{id}/restore</c> (optional).</summary>
/// <param name="IncludePacks">Whether to restore the packs too (only when the snapshot holds them).</param>
public sealed record SnapshotRestoreBody(bool? IncludePacks);

/// <summary>A model or generation service to read: the working model's, or a snapshot's opened read-only (<c>?snapshot=</c>).</summary>
/// <param name="Store">The snapshot's store, or <see langword="null"/> for the working model.</param>
/// <param name="Generation">The snapshot's generation service, or <see langword="null"/>.</param>
/// <param name="Problem">The 404 when the snapshot does not exist.</param>
public sealed record SnapshotAsOf(ModelStore? Store, GenerationService? Generation, IResult? Problem);

/// <summary>
/// Model snapshots (docs/engineering/snapshots.md): list, take, read, rename and publish, delete, restore, compare, export and import
/// (<c>/api/snapshots</c>), and the <c>?snapshot=&lt;id&gt;</c> query parameter that makes the editor's model reads answer "as of" a
/// snapshot, read-only. Viewers list, open and compare; editors take, rename, publish and export; restore, delete and import need
/// maintainer. Nothing here runs in the background: each call does its work and answers.
/// </summary>
public static class SnapshotEndpoints
{
    /// <summary>The query parameter that reads a snapshot instead of the working model.</summary>
    public const string Query = "snapshot";

    /// <summary>The reads that take <c>?snapshot=</c> (method and route); anything else with it is refused by the sign-in gate.</summary>
    private static readonly (string Method, string[] Segments)[] AsOfReads =
    [
        ("GET", ["api", "model", "index"]), ("GET", ["api", "model", "elements"]), ("POST", ["api", "model", "elements", "read"]),
        ("GET", ["api", "model", "elements", "*"]), ("GET", ["api", "model", "kinds"]), ("GET", ["api", "model", "resolved"]),
        ("GET", ["api", "model", "references", "*"]), ("POST", ["api", "validate"]), ("GET", ["api", "diagrams", "*"]),
        ("GET", ["api", "databases", "*", "view"]), ("GET", ["api", "databases", "*", "tables"]), ("GET", ["api", "databases", "*", "tables", "*"]),
        ("GET", ["api", "project", "settings"]), ("POST", ["api", "templates", "preview"]),
    ];

    /// <summary>
    /// What the sign-in gate answers for a request with <c>?snapshot=</c> that is not one of the reads that take it: 409
    /// <c>snapshot-read-only</c> for a write, 400 <c>snapshot-unsupported</c> for another read; <see langword="null"/> to go on.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The problem, or <see langword="null"/>.</returns>
    public static IResult? Refusal(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Query.ContainsKey(Query) || !request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return null;
        var segments = (request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (method, route) in AsOfReads)
        {
            if (string.Equals(method, request.Method, StringComparison.OrdinalIgnoreCase) && route.Length == segments.Length
                && route.Select((s, i) => s == "*" || string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).All(m => m))
                return null;
        }

        return HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            ? Api.Problem("snapshot-unsupported", "This read does not take a snapshot; it answers for the working model only.", StatusCodes.Status400BadRequest)
            : Api.Problem("snapshot-read-only", "A snapshot is read-only; restore it to change it.", StatusCodes.Status409Conflict);
    }

    /// <summary>The snapshot a read asks for with <c>?snapshot=&lt;id&gt;</c>, opened read-only; none for the working model.</summary>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The snapshot's store and generation service, or the 404.</returns>
    public static async Task<SnapshotAsOf> AsOfAsync(HttpContext context, SnapshotLibrary snapshots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (context.Request.Query[Query].ToString() is not { Length: > 0 } id)
            return new SnapshotAsOf(null, null, null);
        var store = await snapshots.OpenAsync(id, ct).ConfigureAwait(false);
        var generation = store is null ? null : await snapshots.OpenGenerationAsync(id, ct).ConfigureAwait(false);
        if (store is null || generation is null)
            return new SnapshotAsOf(null, null, Api.NotFound("snapshot", id));
        context.Response.Headers["X-Maquettiste-Snapshot"] = id;
        return new SnapshotAsOf(store, generation, null);
    }

    /// <summary>Every snapshot, newest first.</summary>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the snapshots.</returns>
    [HttpGet("/api/snapshots")]
    public static Task<IResult> List(HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(await snapshots.ListAsync(ct).ConfigureAwait(false));
    });

    /// <summary>Takes a snapshot of the working model (packs only when <c>includePacks</c> is true); editor.</summary>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>201 with the snapshot, 400 or 403.</returns>
    [HttpPost("/api/snapshots")]
    public static Task<IResult> Create(HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        var (body, error) = await Api.ReadJsonAsync<SnapshotCreateBody>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        SnapshotInfo created;
        try
        {
            created = await snapshots.CreateAsync(new SnapshotCreateRequest(body!.Name ?? "", body.Description, body.IncludePacks ?? false, Author(context)), ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return Api.BadRequest(ex.Message);
        }

        context.Response.Headers.Location = "/api/snapshots/" + created.Id;
        return Api.Json(created, StatusCodes.Status201Created);
    });

    /// <summary>One snapshot.</summary>
    /// <param name="id">The id.</param>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpGet("/api/snapshots/{id}")]
    public static Task<IResult> Get(string id, HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return await snapshots.GetAsync(id, ct).ConfigureAwait(false) is { } found ? Api.Json(found) : Api.NotFound("snapshot", id);
    });

    /// <summary>Renames, describes or publishes a snapshot (the id stays); editor.</summary>
    /// <param name="id">The id.</param>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 400, 403 or 404.</returns>
    [HttpPatch("/api/snapshots/{id}")]
    public static Task<IResult> Update(string id, HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        var (body, error) = await Api.ReadJsonAsync<SnapshotPatchBody>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        try
        {
            return await snapshots.UpdateAsync(id, new SnapshotUpdate(body!.Name, body.Description, body.Published), ct).ConfigureAwait(false) is { } updated
                ? Api.Json(updated)
                : Api.NotFound("snapshot", id);
        }
        catch (ArgumentException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>Deletes a snapshot; maintainer.</summary>
    /// <param name="id">The id.</param>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>204, 403 or 404.</returns>
    [HttpDelete("/api/snapshots/{id}")]
    public static Task<IResult> Delete(string id, HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        return await snapshots.DeleteAsync(id, ct).ConfigureAwait(false) ? Results.NoContent() : Api.NotFound("snapshot", id);
    });

    /// <summary>
    /// Restores a snapshot into the working model after an automatic safety snapshot (<c>before-restore-…</c>; restoring it undoes this);
    /// realtime clients get one <c>model.changed</c>. Refused (409 <c>run-locked</c>) while generation runs; maintainer.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with what was done, 403, 404, 409 or 422.</returns>
    [HttpPost("/api/snapshots/{id}/restore")]
    public static Task<IResult> Restore(string id, HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;
        var (body, error) = await Api.ReadJsonAsync(context.Request, new SnapshotRestoreBody(null), ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        var result = await snapshots.RestoreAsync(id, new SnapshotRestoreRequest(body!.IncludePacks ?? false, Author(context), ChangeSource.Editor), ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            SnapshotRestoreOutcome.Restored => Api.Json(result),
            SnapshotRestoreOutcome.NotFound => Api.NotFound("snapshot", id),
            SnapshotRestoreOutcome.Locked => Api.Problem("run-locked", "A generation run is writing; restore the snapshot when it ends.", StatusCodes.Status409Conflict),
            _ => Api.Json(result, StatusCodes.Status422UnprocessableEntity),
        };
    });

    /// <summary>
    /// What differs between two models: <c>from</c> and <c>to</c> are snapshot ids or <c>working</c> (<c>to</c> defaults to
    /// <c>working</c>). Elements by kind with names, paged by <c>offset</c> and <c>limit</c> (default 500, at most 5000); other documents
    /// by path; per-element fields come from <c>/api/snapshots/compare/element</c>.
    /// </summary>
    /// <param name="from">The <c>from</c> side.</param>
    /// <param name="to">The <c>to</c> side.</param>
    /// <param name="offset">The first element of the page.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 400 or 404.</returns>
    [HttpGet("/api/snapshots/compare")]
    public static Task<IResult> Compare(string? from, string? to, string? offset, string? limit, HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) =>
        Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (string.IsNullOrEmpty(from))
            return Api.BadRequest("from is required: a snapshot id or working.");
        var start = 0;
        var size = SnapshotLibrary.DefaultCompareLimit;
        if (!string.IsNullOrEmpty(offset) && (!int.TryParse(offset, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0))
            return Api.BadRequest("offset must be a whole number of zero or more.");
        if (!string.IsNullOrEmpty(limit) && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 1 || size > SnapshotLibrary.MaxCompareLimit))
            return Api.BadRequest($"limit must be 1 to {SnapshotLibrary.MaxCompareLimit}.");
        var comparison = await snapshots.CompareAsync(from, string.IsNullOrEmpty(to) ? SnapshotLibrary.Working : to, start, size, ct).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return comparison is null ? Api.NotFound("snapshot", from + " or " + (to ?? SnapshotLibrary.Working)) : Api.Json(comparison);
    });

    /// <summary>One element on both sides of a comparison: the two documents (as the conflict view shows them) and the fields that differ.</summary>
    /// <param name="from">The <c>from</c> side.</param>
    /// <param name="to">The <c>to</c> side.</param>
    /// <param name="id">The element id.</param>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 400 or 404.</returns>
    [HttpGet("/api/snapshots/compare/element")]
    public static Task<IResult> CompareElement(string? from, string? to, string? id, HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) =>
        Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(id) || !Api.IsUlid(id))
            return Api.BadRequest("from and id (an element id) are required.");
        var diff = await snapshots.CompareElementAsync(from, string.IsNullOrEmpty(to) ? SnapshotLibrary.Working : to, id, ct).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return diff is null ? Api.NotFound("element on either side", id) : Api.Json(diff);
    });

    /// <summary>The snapshot's archive (a deterministic zip) as a download; editor.</summary>
    /// <param name="id">The id.</param>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <returns>200 <c>application/zip</c>, 403 or 404.</returns>
    [HttpGet("/api/snapshots/{id}/export")]
    public static IResult Export(string id, HttpContext context, SnapshotLibrary snapshots) => Api.Guard(context, () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        if (snapshots.OpenRead(id) is not { } stream)
            return Api.NotFound("snapshot", id);
        return Results.File(stream, "application/zip", id + ".zip");
    });

    /// <summary>
    /// Imports an exported snapshot sent as the <c>application/zip</c> body: checked (paths, sizes, metadata, every document parses and is
    /// canonical) and stored under a new id; the working model is not touched. Maintainer.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="snapshots">The library.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>201 with the snapshot, 403, 413 or 422 with the diagnostics.</returns>
    [HttpPost("/api/snapshots/import")]
    public static Task<IResult> Import(HttpContext context, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (Api.Require(context, "maintainer") is { } forbidden)
            return forbidden;

        // The host bounds every request body (536,870,912 bytes in static-site-hosting 0.4.0, the engine's own 512 MiB archive
        // limit). A body over the host's bound is refused here as the engine refuses an archive over its own, 413 with tooLarge
        // and MQ1011, never as the IOException the host's body reader throws (which read as 503 model-unavailable).
        var bound = context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
        if (bound is { } max && context.Request.ContentLength > max)
            return TooLarge(max);
        SnapshotImportResult result;
        try
        {
            result = await snapshots.ImportAsync(context.Request.Body, ct).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(bound ?? 0);
        }

        if (result.Snapshot is { } imported)
        {
            context.Response.Headers.Location = "/api/snapshots/" + imported.Id;
            return Api.Json(result, StatusCodes.Status201Created);
        }

        return Api.Json(result, result.TooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status422UnprocessableEntity);
    });

    /// <summary>Whether a request is an import, whose body the sign-in gate lets through as <c>application/zip</c>.</summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> for <c>POST /api/snapshots/import</c> with a zip body.</returns>
    public static bool IsZipImport(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return HttpMethods.IsPost(request.Method) && request.Path.Equals("/api/snapshots/import", StringComparison.OrdinalIgnoreCase)
            && (request.ContentType ?? "").Split(';')[0].Trim().Equals("application/zip", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult TooLarge(long bound) =>
        Api.Json(
            new SnapshotImportResult(null, [RuleCatalog.Create("MQ1011", bound > 0
                ? $"The archive is larger than the {bound.ToString(CultureInfo.InvariantCulture)} bytes this server accepts in one request; import it with the command line (maquettiste snapshot import)."
                : "The archive is larger than this server accepts in one request; import it with the command line (maquettiste snapshot import).")], TooLarge: true),
            StatusCodes.Status413PayloadTooLarge);

    private static string Author(HttpContext context) => context.User.FindFirst(ClaimTypes.Name)?.Value ?? "";
}
