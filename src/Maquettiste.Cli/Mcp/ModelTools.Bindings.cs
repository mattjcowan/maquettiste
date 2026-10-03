using System.ComponentModel;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// Entity bindings and materialize (erratum E43): a binding's statements per dialect, what a database's materialize screens list, and
/// the preview of a materialize operation. The operations themselves are batch operations (<c>apply_batch</c>).
/// </summary>
internal sealed partial class ModelTools
{
    /// <summary>The statements of one binding for a dialect.</summary>
    /// <param name="entity">The entity id.</param>
    /// <param name="binding">The binding id.</param>
    /// <param name="dialect">The dialect, or null for the binding's database's.</param>
    /// <param name="placeholder">The placeholder style.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The statements and diagnostics.</returns>
    [McpServerTool(Name = "preview_binding_sql", Title = "Preview binding SQL", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The SQL an entity's binding renders to, for its database's dialect or another: {preview: {entityId, bindingId, database, dialect, select, selectByKey, insert, update, delete}, diagnostics}, each statement {sql, parameters} or null when the binding does not have it (a read-only binding has no insert, update or delete). select reads every row (the constants filter it; a query source is wrapped as a derived table), selectByKey adds the key fields, insert writes the fields and constants without the columns the database fills and returns generated keys, update sets the updatable fields by key with the constants in the where clause, delete deletes by key or runs the soft-delete update. Parameters are named after the fields (@name, or : / $ with placeholder). preview is null when the model has errors.")]
    public Task<CallToolResult> PreviewBindingSql(
        [Description("The entity id; required.")] string? entity = null,
        [Description("The binding id (entity.bindings[i].id); required.")] string? binding = null,
        [Description("postgresql, sqlserver, mysql, sqlite or oracle; omit for the binding's database's dialect.")] string? dialect = null,
        [Description("The placeholder style: @ (default), : or $.")] string? placeholder = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(entity) || string.IsNullOrEmpty(binding))
            return BadRequest("entity and binding are required.");
        var options = new QuerySqlOptions { Placeholder = string.IsNullOrEmpty(placeholder) ? "@" : placeholder };
        if (options.Placeholder is not ("@" or ":" or "$"))
            return BadRequest($"placeholder must be @, : or $, not '{placeholder}'.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var document = await _store.GetElementAsync(entity, ct).ConfigureAwait(false);
        if (document is null)
            return NotFound("entity", entity);
        if (document.Element is not Entity e || e.Id != entity)
            return NotFound("entity", entity, "not-an-entity");
        if (!e.Bindings.Any(b => b.Id == binding))
            return NotFound("binding", binding, "not-a-binding");
        try
        {
            return Ok(await _generation.GetBindingSqlAsync(entity, binding, string.IsNullOrEmpty(dialect) ? null : dialect, options, ct).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }, ct);

    /// <summary>What a database's materialize screens list.</summary>
    /// <param name="database">The database id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status.</returns>
    [McpServerTool(Name = "get_materialize_status", Title = "Materialize status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What can be materialized in one database: {database, databaseName, entities: [{id, name, package, projected, table}] (the entities with no binding to the database; projected says the database still projects a table for it by convention or a mapping element), sources: [{id, kind (table or view), name, schema, origin, boundBy: [{entity, entityName, binding, constants}]}] (its designed and imported tables and its views; boundBy empty means no entity is bound to it, several entries mean constant columns tell the entities apart)}. Then preview_materialize, and apply_batch with a materialize-tables or materialize-entities operation.")]
    public Task<CallToolResult> GetMaterializeStatus([Description("The database element id; required.")] string? database = null, CancellationToken ct = default) =>
        GuardAsync(async () =>
        {
            if (string.IsNullOrEmpty(database))
                return BadRequest("database is required.");
            await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var status = await _store.GetMaterializeStatusAsync(database, ct).ConfigureAwait(false);
            return status is null ? NotFound("database", database, "not-a-database") : Ok(status);
        }, ct);

    /// <summary>The preview of a materialize operation.</summary>
    /// <param name="database">The database id.</param>
    /// <param name="op">The operation.</param>
    /// <param name="entities">The entities (materialize-tables).</param>
    /// <param name="tables">The tables and views (materialize-entities).</param>
    /// <param name="schema">The schema the tables go to.</param>
    /// <param name="package">The package the entities go to.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    [McpServerTool(Name = "preview_materialize", Title = "Preview materialize", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What a materialize operation would do, without writing: {operation, database, valid, creates, updates, deletes (each {id, kind, name, path, because, element}: element is the document that would be written), notes, diagnostics}. op materialize-tables with entities (and an optional schema id) writes, per entity, a designed table with exactly the shape its projection has (columns, keys, uniques, checks, indexes, foreign keys, comments; its overlay folds into the table, which keeps the overlay's id), binds the entity to it, deletes its mapping to the database and points relation mappings at the foreign keys; op materialize-entities with tables (designed or imported tables and views) and package writes, per table, an entity (one attribute per column, the key from the primary key) bound to it, and a many-to-one relation per foreign key between them. An entity already bound, or a table an entity is bound to, is refused (MQ4055). Apply it with apply_batch: {\"op\":\"materialize-tables\",\"database\":...,\"entities\":[...]} or {\"op\":\"materialize-entities\",\"database\":...,\"tables\":[...],\"package\":...}.")]
    public Task<CallToolResult> PreviewMaterialize(
        [Description("The database element id; required.")] string? database = null,
        [Description("materialize-tables or materialize-entities; required.")] string? op = null,
        [Description("The entity ids (materialize-tables).")] string[]? entities = null,
        [Description("The table and view ids (materialize-entities).")] string[]? tables = null,
        [Description("The schema id of the database the tables go to (materialize-tables); omit to keep each projected table's schema.")] string? schema = null,
        [Description("The package id the entities go to (materialize-entities).")] string? package = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(database))
            return BadRequest("database is required.");
        var request = op switch
        {
            "materialize-tables" when entities is { Length: > 0 } => new MaterializeRequest(op, database, entities, schema),
            "materialize-entities" when tables is { Length: > 0 } && !string.IsNullOrEmpty(package) => new MaterializeRequest(op, database, tables, null, package),
            _ => null,
        };
        if (request is null)
            return BadRequest("op is materialize-tables (with entities) or materialize-entities (with tables and package).");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (await _store.GetElementAsync(database, ct).ConfigureAwait(false) is not { } document || document.Element is not Database)
            return NotFound("database", database, "not-a-database");
        return Ok(await _store.PlanMaterializeAsync(request, ct).ConfigureAwait(false));
    }, ct);
}
