using System.ComponentModel;
using Maquettiste.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The bulk read tools (docs/mcp.md "Reading a large model"): the kinds and their counts, documents in pages and the resolved model as
/// flat records in pages. Every page is ordered by (kind, name, id); a cursor encodes the last position, so a change between pages never
/// fails the next one.
/// </summary>
internal sealed partial class ModelTools
{
    /// <summary>The kinds present with their counts (getModelKinds).</summary>
    /// <param name="by"><c>kind</c> or <c>package</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts.</returns>
    [McpServerTool(Name = "get_model_kinds", Title = "Model kinds", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The kinds of top-level elements present with their counts ({total, kinds: [{kind, count}]}), and with by=package the same per package (packages: [{package, name, count, kinds}], the elements in no package first), so you know what to iterate before reading in bulk.")]
    public Task<CallToolResult> GetModelKinds([Description("kind (default) or package.")] string? by = null, CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (by is not (null or "" or "kind" or "package"))
            return BadRequest($"by must be kind or package, not '{by}'.");
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return Ok(ModelPages.Kinds(snapshot.Summaries(), by == "package"));
    }, ct);

    /// <summary>Element documents in pages (getElements).</summary>
    /// <param name="ids">Element or sub-element ids.</param>
    /// <param name="kind">A kind name.</param>
    /// <param name="package">A package id or name.</param>
    /// <param name="tag">A tag.</param>
    /// <param name="category">A category.</param>
    /// <param name="stereotype">A stereotype.</param>
    /// <param name="query">A name fragment.</param>
    /// <param name="fields">Top-level members to keep.</param>
    /// <param name="cursor">The previous page's next.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "get_elements", Title = "Get elements", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Element documents in pages: {items: [{id, kind, path, hash, json}], next, missing}, json being the canonical document as get_element returns it. Select by ids, or by the index filters (kind, package, tag, category, stereotype, query; AND), or both; fields keeps only those top-level members of each json (id and kind are always kept), for example [\"name\", \"attributes\"]. Ordered by kind, name and id; pass next as cursor until it is null. A change between pages is fine: the next page starts after the last element read.")]
    public Task<CallToolResult> GetElements(
        [Description("Element or sub-element ids (a sub-element id reads the element that holds it), at most 1000; ids that match nothing are listed in missing.")] string[]? ids = null,
        [Description("Only this kind, for example entity, relation, enum, process.")] string? kind = null,
        [Description("Only elements directly in this package, by package id or package name.")] string? package = null,
        [Description("Only elements with this tag.")] string? tag = null,
        [Description("Only elements in this category.")] string? category = null,
        [Description("Only elements with this stereotype.")] string? stereotype = null,
        [Description("Only elements whose name contains this text, ignoring case.")] string? query = null,
        [Description("The top-level members to keep in each document, for example [\"name\", \"package\", \"attributes\"]; id and kind are always kept. Omit for whole documents.")] string[]? fields = null,
        [Description("The next value of the previous page.")] string? cursor = null,
        [Description("The page size, 1 to 1000 (default 100).")] int? limit = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        if (ids is { Length: > ModelPages.MaxLimit })
            return BadRequest($"At most {ModelPages.MaxLimit} ids can be read at once; {ids.Length} were given.");
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        try
        {
            return Ok(ModelPages.ReadElements(snapshot, ids, new ElementFilter(kind, package, tag, category, stereotype, query), fields, cursor, limit ?? ModelPages.DefaultLimit));
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
    }, ct);

    /// <summary>The resolved model as flat records in pages (getResolvedModel).</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="database">A database id.</param>
    /// <param name="cursor">The previous page's next.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    [McpServerTool(Name = "get_resolved_model", Title = "Resolved model", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The resolved model, what templates read, as flat records in pages: {items, next, diagnostics}. Each record has id, kind and name; other objects appear by id. entities: attributes resolved (type, default, isKey, isInherited for the base's), key, alternate keys, base and derived, navigations, relations, mappings per database (table and columns), annotations. relations: ends, attributes, mappings. processes: every state, transition, event, guard, action, gate and invoke, actors by id. databases: the database view (tables with columns, keys, foreign keys and indexes, views, sequences); tables: one table per record. Also packages, enums, value-objects, scalar-types, reference-types (with rows), seeds, actors, scenarios; all is every scope but tables. A model with errors returns no items and the errors in diagnostics.")]
    public Task<CallToolResult> GetResolvedModel(
        [Description("all (default), packages, entities, relations, enums, value-objects, scalar-types, reference-types, seeds, processes, actors, scenarios, databases or tables.")] string? scope = null,
        [Description("A database id: only the entities and relations mapped to it, that database and its tables.")] string? database = null,
        [Description("The next value of the previous page.")] string? cursor = null,
        [Description("The page size, 1 to 1000 (default 100).")] int? limit = null,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (limit is < 1 or > ModelPages.MaxLimit)
            return BadRequest($"limit must be from 1 to {ModelPages.MaxLimit}.");
        var name = string.IsNullOrEmpty(scope) ? "all" : scope;
        if (!ResolvedRecords.ScopeNames.Contains(name, StringComparer.Ordinal))
            return BadRequest($"scope must be one of {string.Join(", ", ResolvedRecords.ScopeNames)}, not '{scope}'.");
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(database))
        {
            var document = await _store.GetElementAsync(database, ct).ConfigureAwait(false);
            if (document is null)
                return NotFound("database", database);
            if (document.Element.Id != database || document.Element.KindName != "database")
                return NotFound("database", database, "not-a-database");
        }

        try
        {
            return Ok(await _generation.GetResolvedAsync(new ResolvedQuery(name, database, cursor, limit ?? ModelPages.DefaultLimit), ct).ConfigureAwait(false));
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }
    }, ct);
}
