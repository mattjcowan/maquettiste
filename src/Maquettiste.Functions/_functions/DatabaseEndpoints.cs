using Maquettiste.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maquettiste.Functions;

/// <summary><c>GET /api/databases/{id}/view</c>: the resolved tables of one database (E1); <c>/tables</c>: their list without columns (E5c); <c>/tables/{key}</c>: one table (E5f);
/// <c>GET /api/model/queries/{id}/sql</c>: the SQL of one query (2026-10-02); <c>GET /api/model/entities/{id}/bindings/{bindingId}/sql</c>: the
/// statements of one binding, and <c>GET /api/model/databases/{id}/materialize</c> with its <c>preview</c> (erratum E43).</summary>
public static class DatabaseEndpoints
{
    /// <summary>The physical model after conventions, mappings and overlays; <c>view</c> is null (with the errors) when the model has errors.</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The database element's id.</param>
    /// <param name="store">The model store.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, or 404 (<c>not-found</c>, <c>not-a-database</c>).</returns>
    /// <param name="snapshots">The snapshots: <c>?snapshot=&lt;id&gt;</c> reads one, read-only, instead of the working model.</param>
    [HttpGet("/api/databases/{id}/view")]
    public static Task<IResult> View(HttpContext context, string id, ModelStore store, GenerationService generation, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        var asOf = await SnapshotEndpoints.AsOfAsync(context, snapshots, ct).ConfigureAwait(false);
        if (asOf.Problem is { } missing)
            return missing;
        store = asOf.Store ?? store;
        generation = asOf.Generation ?? generation;
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
    /// <param name="snapshots">The snapshots: <c>?snapshot=&lt;id&gt;</c> reads one, read-only, instead of the working model.</param>
    [HttpGet("/api/databases/{id}/tables")]
    public static Task<IResult> Tables(HttpContext context, string id, ModelStore store, DatabaseTables tables, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        var asOf = await SnapshotEndpoints.AsOfAsync(context, snapshots, ct).ConfigureAwait(false);
        if (asOf.Problem is { } missing)
            return missing;
        store = asOf.Store ?? store;
        if (asOf.Generation is { } version)
            tables = new DatabaseTables(version);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tables);
        var document = await store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return Api.NotFound("database", id);
        if (document.Element.Id != id || document.Element.KindName != "database")
            return Api.NotFound("database", id, "not-a-database");
        return Api.Json(await tables.GetAsync(id, ct).ConfigureAwait(false));
    });

    /// <summary>One table of a database with its columns, keys and indexes; <c>table</c> is null when absent or left out on a model with errors.</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The database element's id.</param>
    /// <param name="key">The table's key, as <see cref="TableSummary.Key"/>.</param>
    /// <param name="store">The model store.</param>
    /// <param name="tables">The table reader.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, or 404 (<c>not-found</c>, <c>not-a-database</c>).</returns>
    /// <param name="snapshots">The snapshots: <c>?snapshot=&lt;id&gt;</c> reads one, read-only, instead of the working model.</param>
    [HttpGet("/api/databases/{id}/tables/{key}")]
    public static Task<IResult> Table(HttpContext context, string id, string key, ModelStore store, DatabaseTables tables, SnapshotLibrary snapshots, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        var asOf = await SnapshotEndpoints.AsOfAsync(context, snapshots, ct).ConfigureAwait(false);
        if (asOf.Problem is { } missing)
            return missing;
        store = asOf.Store ?? store;
        if (asOf.Generation is { } version)
            tables = new DatabaseTables(version);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tables);
        var document = await store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return Api.NotFound("database", id);
        if (document.Element.Id != id || document.Element.KindName != "database")
            return Api.NotFound("database", id, "not-a-database");
        return Api.Json(await tables.GetTableAsync(id, key, ct).ConfigureAwait(false));
    });

    /// <summary>
    /// The SQL of one query for a dialect (the database's when none is given): the query's statement and one per collection, with the
    /// parameters each names; <c>preview</c> is null (with the errors) when the model has errors.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The query element's id.</param>
    /// <param name="dialect">postgresql, sqlserver, mysql, sqlite or oracle; absent for the query's database's.</param>
    /// <param name="placeholder">The placeholder style: <c>@</c> (default), <c>:</c> or <c>$</c>.</param>
    /// <param name="lists">How list parameters are written: <c>expand</c> (default) or <c>any</c>.</param>
    /// <param name="store">The model store.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 400 for an unknown dialect or style, or 404 (<c>not-found</c>, <c>not-a-query</c>).</returns>
    [HttpGet("/api/model/queries/{id}/sql")]
    public static Task<IResult> QuerySql(HttpContext context, string id, string? dialect, string? placeholder, string? lists, ModelStore store,
        GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(generation);
        var document = await store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return Api.NotFound("query", id);
        if (document.Element.Id != id || document.Element.KindName != "query")
            return Api.NotFound("query", id, "not-a-query");
        var options = new QuerySqlOptions
        {
            Placeholder = string.IsNullOrEmpty(placeholder) ? "@" : placeholder,
            Lists = string.IsNullOrEmpty(lists) ? "expand" : lists,
        };
        if (options.Placeholder is not ("@" or ":" or "$"))
            return Api.BadRequest($"placeholder must be @, : or $, not '{placeholder}'.");
        if (options.Lists is not ("expand" or "any"))
            return Api.BadRequest($"lists must be expand or any, not '{lists}'.");
        try
        {
            return Api.Json(await generation.GetQuerySqlAsync(id, string.IsNullOrEmpty(dialect) ? null : dialect, options, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>The five statements of an entity's binding for a dialect (the database's when none is given); <c>preview</c> is null (with the errors) when the model has errors.</summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The entity's id.</param>
    /// <param name="bindingId">The binding's id.</param>
    /// <param name="dialect">postgresql, sqlserver, mysql, sqlite or oracle; absent for the binding's database's.</param>
    /// <param name="placeholder">The placeholder style: <c>@</c> (default), <c>:</c> or <c>$</c>.</param>
    /// <param name="store">The model store.</param>
    /// <param name="generation">The generation service.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, 400 for an unknown dialect or style, or 404 (<c>not-found</c>, <c>not-an-entity</c>, <c>not-a-binding</c>).</returns>
    [HttpGet("/api/model/entities/{id}/bindings/{bindingId}/sql")]
    public static Task<IResult> BindingSql(HttpContext context, string id, string bindingId, string? dialect, string? placeholder, ModelStore store,
        GenerationService generation, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(generation);
        var document = await store.GetElementAsync(id, ct).ConfigureAwait(false);
        if (document is null)
            return Api.NotFound("entity", id);
        if (document.Element is not Maquettiste.Engine.Model.Entity entity || entity.Id != id)
            return Api.NotFound("entity", id, "not-an-entity");
        if (!entity.Bindings.Any(b => b.Id == bindingId))
            return Api.NotFound("binding", bindingId, "not-a-binding");
        var options = new QuerySqlOptions { Placeholder = string.IsNullOrEmpty(placeholder) ? "@" : placeholder };
        if (options.Placeholder is not ("@" or ":" or "$"))
            return Api.BadRequest($"placeholder must be @, : or $, not '{placeholder}'.");
        try
        {
            return Api.Json(await generation.GetBindingSqlAsync(id, bindingId, string.IsNullOrEmpty(dialect) ? null : dialect, options, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return Api.BadRequest(ex.Message);
        }
    });

    /// <summary>
    /// What the database's materialize screens list: the entities with no binding to it (each flagged when the database projects it
    /// today), and its designed and imported tables and views with the entities bound to each.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The database element's id.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200, or 404 (<c>not-found</c>, <c>not-a-database</c>).</returns>
    [HttpGet("/api/model/databases/{id}/materialize")]
    public static Task<IResult> MaterializeStatus(HttpContext context, string id, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(store);
        var status = await store.GetMaterializeStatusAsync(id, ct).ConfigureAwait(false);
        return status is null ? Api.NotFound("database", id, "not-a-database") : Api.Json(status);
    });

    /// <summary>
    /// What a materialize operation would do, without writing: the body is the batch operation (<c>{ "op": "materialize-tables",
    /// "entities": [...], "schema"? }</c>, <c>{ "op": "materialize-entities", "tables": [...], "package": ... }</c> or <c>{ "op":
    /// "materialize-attributes", "entities": [...], "columns"?: [...] }</c> or <c>{ "op": "materialize-columns", "entities": [...],
    /// "attributes"?: [...] }</c>); the database is the route's.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="id">The database element's id.</param>
    /// <param name="store">The model store.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>200 with the plan, 400 for a body that is not a materialize operation, or 404 (<c>not-a-database</c>).</returns>
    [HttpPost("/api/model/databases/{id}/materialize/preview")]
    public static Task<IResult> MaterializePreview(HttpContext context, string id, ModelStore store, CancellationToken ct) => Api.GuardAsync(context, async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        var (request, error) = await Api.ReadJsonAsync<MaterializeBody>(context.Request, null, ct).ConfigureAwait(false);
        if (error is not null)
            return error;
        if (await store.GetElementAsync(id, ct).ConfigureAwait(false) is not { } document || document.Element.Id != id || document.Element.KindName != "database")
            return Api.NotFound("database", id, "not-a-database");
        var plan = request?.Op switch
        {
            "materialize-tables" when request.Entities is { Count: > 0 } entities =>
                new MaterializeRequest("materialize-tables", id, entities, request.Schema),
            "materialize-entities" when request.Tables is { Count: > 0 } tables && !string.IsNullOrEmpty(request.Package) =>
                new MaterializeRequest("materialize-entities", id, tables, null, request.Package),
            "materialize-attributes" when request.Entities is { Count: > 0 } entities =>
                new MaterializeRequest("materialize-attributes", id, entities, Columns: request.Columns is { Count: > 0 } columns ? columns : null),
            "materialize-columns" when request.Entities is { Count: > 0 } entities =>
                new MaterializeRequest("materialize-columns", id, entities, Attributes: request.Attributes is { Count: > 0 } refs ? refs : null),
            _ => null,
        };
        if (plan is null)
            return Api.BadRequest("The body is { \"op\": \"materialize-tables\", \"entities\": [ids], \"schema\"?: id } { \"op\": \"materialize-entities\", \"tables\": [ids], \"package\": id } { \"op\": \"materialize-attributes\", \"entities\": [ids], \"columns\"?: [keys or names] } or { \"op\": \"materialize-columns\", \"entities\": [ids], \"attributes\"?: [field references] }.");
        return Api.Json(await store.PlanMaterializeAsync(plan, ct).ConfigureAwait(false));
    });
}

/// <summary>The body of <c>POST /api/model/databases/{id}/materialize/preview</c>: a materialize batch operation without its database.</summary>
/// <param name="Op"><c>materialize-tables</c>, <c>materialize-entities</c>, <c>materialize-attributes</c> or <c>materialize-columns</c>.</param>
/// <param name="Entities">The entities (materialize-tables, materialize-attributes, materialize-columns).</param>
/// <param name="Tables">The tables and views (materialize-entities).</param>
/// <param name="Schema">The schema the tables go to (materialize-tables).</param>
/// <param name="Package">The package the entities go to (materialize-entities).</param>
/// <param name="Columns">The source columns (materialize-attributes, one entity); absent means every column nothing in the binding names.</param>
/// <param name="Attributes">The binding field references (materialize-columns, one entity); absent means every unmapped attribute.</param>
public sealed record MaterializeBody(string? Op, IReadOnlyList<string>? Entities, IReadOnlyList<string>? Tables, string? Schema, string? Package,
    IReadOnlyList<string>? Columns = null, IReadOnlyList<string>? Attributes = null);
