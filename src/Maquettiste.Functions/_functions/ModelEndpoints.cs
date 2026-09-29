using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary>The model: the index, elements with <c>If-Match</c>, atomic batches and references (phase2-design.md section 3.7).</summary>
public static class ModelEndpoints
{
    /// <summary>
    /// Summaries of every element, with the index's hash as ETag and <c>Cache-Control: no-cache</c> (E5e): a request whose
    /// <c>If-None-Match</c> names the current tag gets 304 with no body.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the summaries, or 304.</returns>
    [HttpGet("/api/model/index")]
    public static Task<IResult> Index(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var index = await store.GetIndexAsync(ct).ConfigureAwait(false);
        var tag = ModelReads.IndexTag(index);
        Api.SetETag(context, tag);
        context.Response.Headers.CacheControl = "no-cache";
        if (Api.TryReadTag(context.Request.Headers.IfNoneMatch.ToString(), out var seen) && seen == tag)
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Api.Json(index);
    });

    /// <summary>The documents of up to 200 element or sub-element ids, from one snapshot (E5b).</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the documents and the ids that matched nothing, or 400.</returns>
    [HttpPost("/api/model/elements/read")]
    public static Task<IResult> Read(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var (request, error) = await Api.ReadJsonAsync<ElementReadRequest>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (request!.Ids is not { } ids)
            return Api.BadRequest("ids is required.");
        if (ids.Count > ModelReads.MaxReadIds)
            return Api.BadRequest($"At most {ModelReads.MaxReadIds} ids can be read at once; {ids.Count} were given.");
        if (ids.Any(id => !Api.IsUlid(id)))
            return Api.BadRequest("ids must be element or sub-element ids (uppercase ULIDs).");
        return Api.Json(await store.ReadElementsAsync(ids, ct).ConfigureAwait(false));
    });

    /// <summary>Creates an element; a missing top-level id is assigned by the engine.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>201 with the save result, 422 or 400.</returns>
    [HttpPost("/api/model/elements")]
    public static Task<IResult> Create(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        var (body, error) = await Api.ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        var result = await store.CreateAsync(body, ChangeSource.Editor, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
        {
            Api.SetETag(context, result.Hash);
            context.Response.Headers.Location = "/api/model/elements/" + result.Id;
        }

        return Api.Json(result, Api.StatusOf(result.Outcome, created: true));
    });

    /// <summary>One element with its hash as ETag; a sub-element id returns the document that holds it.</summary>
    /// <param name="id">The element or sub-element id.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 304 (the hash in <c>If-None-Match</c>) or 404.</returns>
    [HttpGet("/api/model/elements/{id}")]
    public static Task<IResult> Get(string id, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var document = await store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return Api.NotFound("element", id);
        Api.SetETag(context, document.Hash);
        context.Response.Headers.CacheControl = "no-store";
        if (Api.TryReadTag(context.Request.Headers.IfNoneMatch.ToString(), out var seen) && seen == document.Hash)
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Api.Json(document);
    });

    /// <summary>Saves an element with <c>If-Match</c>; a rename moves the file in the same save.</summary>
    /// <param name="id">The element id (the body's id must be the same).</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 422, 404, 428 or 400.</returns>
    [HttpPut("/api/model/elements/{id}")]
    public static Task<IResult> Save(string id, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, () => SaveAsync(id, context, store, ct));

    /// <summary>
    /// Deletes an element with <c>If-Match</c>. <paramref name="resolution"/> is bound as a string and mapped here: absent or <c>refuse</c>
    /// refuses while others reference it, <c>remove-references</c> clears optional references (the host binds enum parameters by
    /// member name, not by the engine's JSON names).
    /// </summary>
    /// <param name="id">The element id.</param>
    /// <param name="resolution"><c>refuse</c> or <c>remove-references</c>.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 422, 404, 428 or 400.</returns>
    [HttpDelete("/api/model/elements/{id}")]
    public static Task<IResult> Delete(string id, string? resolution, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        DeleteResolution mode;
        switch (resolution)
        {
            case null or "" or "refuse":
                mode = DeleteResolution.Refuse;
                break;
            case "remove-references":
                mode = DeleteResolution.RemoveReferences;
                break;
            default:
                return Api.BadRequest($"resolution must be 'refuse' or 'remove-references', not '{resolution}'.");
        }

        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var result = await store.DeleteAsync(id, expected, mode, ChangeSource.Editor, ct).ConfigureAwait(false);
        return Api.Json(result, Api.StatusOf(result.Outcome));
    });

    /// <summary>An atomic multi-element change: parsed against <c>batch.json</c> before any disk access, then applied all or nothing.</summary>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 404, 422 or 400.</returns>
    [HttpPost("/api/model/batch")]
    public static Task<IResult> Batch(HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        var (body, error) = await Api.ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        var parsed = store.ParseBatch(body);
        if (parsed.Batch is null)
            return Api.Json(parsed, StatusCodes.Status422UnprocessableEntity);
        var result = await store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, ct).ConfigureAwait(false);
        return Api.Json(result, Api.StatusOf(result.Outcome));
    });

    /// <summary>Every reference to the element or one of its sub-elements from another element.</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The element or sub-element id.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 or 404.</returns>
    [HttpGet("/api/model/references/{id}")]
    public static Task<IResult> References(HttpContext context, string id, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        if (await store.GetElementAsync(id, ct).ConfigureAwait(false) is null)
            return Api.NotFound("element", id);
        return Api.Json(await store.GetReferencesAsync(id, ct).ConfigureAwait(false));
    });

    /// <summary>The save both <c>PUT /api/model/elements/{id}</c> and <c>PUT /api/diagrams/{id}</c> run.</summary>
    /// <param name="id">The element id.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    public static async Task<IResult> SaveAsync(string id, HttpContext context, ModelStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (Api.Require(context, "editor") is { } forbidden)
            return forbidden;
        if (!Api.TryGetIfMatch(context.Request, out var expected))
            return Api.PreconditionRequired();
        var (body, error) = await Api.ReadBodyAsync(context.Request, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        var result = await store.SaveAsync(id, body, expected, ChangeSource.Editor, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved)
            Api.SetETag(context, result.Hash);
        return Api.Json(result, Api.StatusOf(result.Outcome));
    }
}

/// <summary>The body of <c>POST /api/model/elements/read</c> (E5b).</summary>
/// <param name="Ids">Element or sub-element ids, at most 200.</param>
public sealed record ElementReadRequest(IReadOnlyList<string>? Ids);
