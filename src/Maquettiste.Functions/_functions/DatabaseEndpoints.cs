using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary><c>GET /api/databases/{id}/view</c>: the resolved tables of one database (E1).</summary>
public static class DatabaseEndpoints
{
    /// <summary>The physical model after conventions, mappings and overlays; <c>view</c> is null (with the errors) when the model has errors.</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The database element's id.</param>
    /// <param name="store">The model store.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, or 404 (<c>not-found</c>, <c>not-a-database</c>).</returns>
    [HttpGet("/api/databases/{id}/view")]
    public static Task<IResult> View(HttpContext context, string id, ModelStore store, GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(generation);
        var document = await store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return Api.NotFound("database", id);
        if (document.Element.Id != id || document.Element.KindName != "database")
            return Api.NotFound("database", id, "not-a-database");
        return Api.Json(await generation.GetDatabaseViewAsync(id, ct).ConfigureAwait(false));
    });
}
