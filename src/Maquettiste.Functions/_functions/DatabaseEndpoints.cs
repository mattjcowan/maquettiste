using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary><c>GET /api/databases/{id}/view</c>: the resolved tables of one database (E1); <c>/tables</c>: their list without columns (E5c).</summary>
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

    /// <summary>The table list of one database without columns; on a model with errors, the tables that resolve with <c>partial: true</c>.</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The database element's id.</param>
    /// <param name="store">The model store.</param>
    /// <param name="tables">The table reader.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, or 404 (<c>not-found</c>, <c>not-a-database</c>).</returns>
    [HttpGet("/api/databases/{id}/tables")]
    public static Task<IResult> Tables(HttpContext context, string id, ModelStore store, DatabaseTables tables, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tables);
        var document = await store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return Api.NotFound("database", id);
        if (document.Element.Id != id || document.Element.KindName != "database")
            return Api.NotFound("database", id, "not-a-database");
        return Api.Json(await tables.GetAsync(id, ct).ConfigureAwait(false));
    });
}
