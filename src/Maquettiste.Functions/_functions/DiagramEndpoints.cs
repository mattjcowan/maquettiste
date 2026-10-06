using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary><c>GET</c> and <c>PUT /api/diagrams/{id}</c>: the element endpoints restricted to diagrams.</summary>
public static class DiagramEndpoints
{
    /// <summary>A diagram's membership, positions and viewport, with its hash as ETag.</summary>
    /// <param name="id">The diagram id.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, or 404 (<c>not-found</c>, <c>not-a-diagram</c>).</returns>
    /// <param name="snapshots">The snapshots: <c>?snapshot=&lt;id&gt;</c> reads one, read-only, instead of the working model.</param>
    [HttpGet("/api/diagrams/{id}")]
    public static Task<IResult> Get(string id, HttpContext context, ModelStore store, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        var asOf = await SnapshotEndpoints.AsOfAsync(context, snapshots, ct).ConfigureAwait(false);
        if (asOf.Problem is { } missing)
            return missing;
        store = asOf.Store ?? store;
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var document = await store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (NotADiagram(id, document) is { } problem)
            return problem;
        Api.SetETag(context, document!.Hash);
        context.Response.Headers.CacheControl = "no-store";
        return Api.Json(document);
    });

    /// <summary>Saves a diagram with <c>If-Match</c> (the editor debounces position changes and retries once on 409).</summary>
    /// <param name="id">The diagram id.</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 409, 422, 404, 428 or 400.</returns>
    [HttpPut("/api/diagrams/{id}")]
    public static Task<IResult> Save(string id, HttpContext context, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (NotADiagram(id, await store.GetElementAsync(id, ct).ConfigureAwait(false)) is { } problem)
            return problem;
        return await ModelEndpoints.SaveAsync(id, context, store, ct).ConfigureAwait(false);
    });

    private static IResult? NotADiagram(string id, Engine.Model.ElementDocument? document) =>
        document is null ? Api.NotFound("diagram", id)
        : document.Element.Id != id || document.Element.KindName != "diagram" ? Api.NotFound("diagram", id, "not-a-diagram")
        : null;
}
