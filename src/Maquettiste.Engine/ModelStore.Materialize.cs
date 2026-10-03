using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>
/// Materialize, both directions (erratum E43; engine-design.md section 7, "Bindings and materialize"): the batch operations
/// <c>materialize-tables</c> and <c>materialize-entities</c>, which expand into creates, updates and deletes applied with the batch's
/// other operations, all or nothing; the preview of either (<see cref="PlanMaterializeAsync"/>, nothing written); and the status of a
/// database (<see cref="GetMaterializeStatusAsync"/>): which entities have no binding to it, and which of its tables and views the
/// entities bind.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>Whether an operation is a materialize operation.</summary>
    /// <param name="op">The operation kind.</param>
    public static bool IsMaterializeOperation(BatchOp op) => op is BatchOp.MaterializeTables or BatchOp.MaterializeEntities;

    private async Task<string?> MaterializeAsync(ModelSnapshot snapshot, BatchOperation o, List<PlannedChange> changes, List<MaterializeResult> results, CancellationToken ct)
    {
        var result = await RunMaterializerAsync(snapshot, ToRequest(o), ct).ConfigureAwait(false);
        if (result.Refusal is not null)
            return result.Refusal;
        changes.AddRange(ToChanges(result, o.ExpectedHashes));
        results.Add(result);
        return null;
    }

    /// <summary>
    /// After a saved materialize-tables, records in each database's committed snapshot the keys of the tables (and key sequences) it
    /// stored as files: the projected key, the file's id and each column's key before and after (<see cref="Generation.SnapshotAliases"/>).
    /// Nothing is rekeyed: the schema diff reads the snapshot through the aliases, so the next migration sees each projected table and
    /// its stored successor as one table, and so does the one after an undo puts the projection back, or a redo stores it again. A
    /// database without a snapshot gets an alias-only one, which the diff reads as none.
    /// </summary>
    private async Task RecordAliasesAsync(IReadOnlyList<MaterializeResult> results, CancellationToken ct)
    {
        foreach (var group in results.Where(r => r.Database is not null && (r.Rekeys?.Count > 0 || r.Sequences?.Count > 0)).GroupBy(r => r.Database!, StringComparer.Ordinal))
        {
            if (_current?.All<Database>().FirstOrDefault(d => d.Name == group.Key) is not { } database)
                continue;
            var before = await _services.Snapshots.LoadAsync(group.Key, ct).ConfigureAwait(false);
            var tables = group.SelectMany(r => r.Rekeys ?? []).ToList();
            var sequences = group.SelectMany(r => r.Sequences ?? new Dictionary<string, string>()).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (Generation.SnapshotAliases.Record(before, database, tables, sequences) is { } after)
                await _services.Snapshots.SaveAsync(after, ct).ConfigureAwait(false);
        }
    }

    private static MaterializeRequest ToRequest(BatchOperation o) => o.Op == BatchOp.MaterializeTables
        ? new MaterializeRequest("materialize-tables", o.Database ?? o.Id ?? "", o.Entities ?? [], o.Schema)
        : new MaterializeRequest("materialize-entities", o.Database ?? o.Id ?? "", o.Tables ?? [], null, o.Package);

    /// <summary>
    /// Runs the materializer. Storing tables reads the database's committed snapshot first: its aliases give back the ids a table, its
    /// columns and its key sequences had when they were stored before (and undone), so storing again changes neither ids nor aliases.
    /// </summary>
    private async Task<MaterializeResult> RunMaterializerAsync(ModelSnapshot snapshot, MaterializeRequest request, CancellationToken ct)
    {
        IReadOnlyList<SnapshotAlias>? aliases = null;
        if (request.Operation == "materialize-tables" && snapshot.Get<Database>(request.Database) is { } database)
            aliases = (await _services.Snapshots.LoadAsync(database.Name, ct).ConfigureAwait(false))?.Aliases;
        var materializer = new Materializer(snapshot, _services.Resolver, _options.EffectiveIdGenerator, _options.EffectiveParallelism, aliases);
        return request.Operation switch
        {
            "materialize-tables" => await materializer.TablesAsync(request.Database, request.Ids, request.Schema, ct).ConfigureAwait(false),
            "materialize-entities" => await materializer.EntitiesAsync(request.Database, request.Ids, request.Package, ct).ConfigureAwait(false),
            _ => new MaterializeResult($"'{request.Operation}' is not materialize-tables or materialize-entities.", [], []),
        };
    }

    /// <summary>
    /// The planned changes. With the caller's expected hashes, each update and delete expects the hash the caller read (none read: a
    /// hash no file has), so a document that changed since the caller read it, or one the caller did not expect the operation to
    /// touch, is a conflict and nothing is written: an undo built from what the caller read never puts back a stale version.
    /// </summary>
    private static IEnumerable<PlannedChange> ToChanges(MaterializeResult result, IReadOnlyDictionary<string, string>? expected = null) =>
        result.Ops.Select(op => new PlannedChange(op.Op, op.Op == BatchOp.Create ? null : op.Id,
            op.Op == BatchOp.Create || expected is null ? op.Hash : expected.GetValueOrDefault(op.Id) ?? "unread",
            op.Node?.DeepClone(), DeleteResolution.Refuse));

    /// <summary>
    /// What a materialize operation would do, planned and validated like the batch operation without writing anything: the elements it
    /// creates, updates and deletes (with the documents it would write), what it leaves as it is, and the diagnostics the change would
    /// have (a refusal is MQ4055; the binding rules MQ4044 to MQ4054 come from resolving the model after the change).
    /// </summary>
    /// <param name="request">The operation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public async Task<MaterializePlan> PlanMaterializeAsync(MaterializeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var result = await RunMaterializerAsync(snapshot, request, ct).ConfigureAwait(false);
        if (result.Refusal is not null)
            return new MaterializePlan(request.Operation, request.Database, false, [], [], [], [], [RuleCatalog.Create("MQ4055", result.Refusal, null, null, null)]);

        var plan = Plan(snapshot, [.. ToChanges(result)], ct);
        if (!plan.Failed)
            await ValidateAsync(plan, snapshot, ct).ConfigureAwait(false);
        var diagnostics = plan.Outcomes.SelectMany(o => o.Diagnostics).ToList();
        if (!plan.Failed && plan.Candidate is { } candidate)
        {
            var touched = result.Ops.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            var resolved = await _services.Resolver.ResolveAsync(candidate, null, ct).ConfigureAwait(false);
            diagnostics.AddRange(resolved.Diagnostics.Where(d => d.ElementId is { } id && touched.Contains(id) && string.CompareOrdinal(d.Rule, "MQ4044") >= 0
                && string.CompareOrdinal(d.Rule, "MQ4054") <= 0));
        }

        MaterializeChange Change(MaterializeOp op)
        {
            var path = snapshot.GetDocument(op.Id)?.Path;
            JsonElement? element = op.Node is null ? null : JsonSerializer.SerializeToElement(op.Node);
            return new MaterializeChange(op.Id, op.Kind, op.Name, path, op.Because, element);
        }

        return new MaterializePlan(request.Operation, request.Database, !plan.Failed,
            [.. result.Ops.Where(o => o.Op == BatchOp.Create).Select(Change)],
            [.. result.Ops.Where(o => o.Op == BatchOp.Update).Select(Change)],
            [.. result.Ops.Where(o => o.Op == BatchOp.Delete).Select(Change)],
            result.Notes,
            [.. diagnostics.Distinct().Order(Diagnostic.Order)]);
    }

    /// <summary>
    /// What a database's materialize screens list: the entities with no binding to it (and whether each is projected there today), and
    /// its designed and imported tables and views with the entities bound to each (none for an unbound one).
    /// </summary>
    /// <param name="databaseId">The database id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status, or <see langword="null"/> when no database has the id.</returns>
    public async Task<MaterializeStatus?> GetMaterializeStatusAsync(string databaseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(databaseId);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Database>(databaseId) is not { } db)
            return null;
        var resolved = await _services.Resolver.ResolveAsync(snapshot, null, ct).ConfigureAwait(false);
        var rdb = resolved.Databases.FirstOrDefault(d => d.Id == db.Id);
        var entities = new List<MaterializeEntityStatus>();
        foreach (var entity in snapshot.All<Entity>().OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            if (entity.Bindings.Any(b => b.Database == db.Id))
                continue;
            var table = rdb?.Tables.FirstOrDefault(t => t.Origin == "synthesized" && t.Entity?.Id == entity.Id && t.Attribute is null && !t.IsJunction);
            entities.Add(new MaterializeEntityStatus(entity.Id, entity.Name, entity.Package, table is not null, table?.Name));
        }

        var sources = new List<MaterializeSourceStatus>();
        foreach (var table in rdb?.Tables.Where(t => t.Origin != "synthesized") ?? [])
            sources.Add(new MaterializeSourceStatus(table.Key, "table", table.Name, table.Schema, table.Origin, [.. table.BoundBy.Select(Binder)]));
        foreach (var view in rdb?.Views ?? Resolution.RList<Resolution.RView>.Empty)
            sources.Add(new MaterializeSourceStatus(view.Id, "view", view.Name, view.Schema, "designed", [.. view.BoundBy.Select(Binder)]));
        return new MaterializeStatus(db.Id, db.Name, entities, sources);

        static MaterializeBinder Binder(Resolution.REntityBinding b) => new(b.Entity.Id, b.Entity.Name, b.Id,
            [.. b.Constants.Select(c => new MaterializeConstant(c.ColumnName, c.Value is null ? null : JsonSerializer.SerializeToElement(c.Value)))]);
    }
}

/// <summary>A materialize operation to plan (<see cref="ModelStore.PlanMaterializeAsync"/>).</summary>
/// <param name="Operation"><c>materialize-tables</c> or <c>materialize-entities</c>.</param>
/// <param name="Database">The database id.</param>
/// <param name="Ids">The entities (tables) or the tables and views (entities).</param>
/// <param name="Schema">For tables: the schema id of the database the tables go to; absent keeps the projected tables' schema.</param>
/// <param name="Package">For entities: the package id the entities go to.</param>
public sealed record MaterializeRequest(string Operation, string Database, IReadOnlyList<string> Ids, string? Schema = null, string? Package = null);

/// <summary>What a materialize operation would do.</summary>
/// <param name="Operation">The operation.</param>
/// <param name="Database">The database id.</param>
/// <param name="Valid">Whether the change would be saved (no refusal and no error).</param>
/// <param name="Creates">The elements it creates.</param>
/// <param name="Updates">The elements it changes.</param>
/// <param name="Deletes">The elements it deletes.</param>
/// <param name="Notes">What it leaves as it is, in words.</param>
/// <param name="Diagnostics">The refusal (MQ4055), the errors that would refuse the change, and the binding findings after it.</param>
public sealed record MaterializePlan(string Operation, string Database, bool Valid, IReadOnlyList<MaterializeChange> Creates,
    IReadOnlyList<MaterializeChange> Updates, IReadOnlyList<MaterializeChange> Deletes, IReadOnlyList<string> Notes, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One element a materialize operation creates, updates or deletes.</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Name">A readable name.</param>
/// <param name="Path">The file's repo-relative path today (absent for a create).</param>
/// <param name="Because">Why.</param>
/// <param name="Element">The document it would write (absent for a delete).</param>
public sealed record MaterializeChange(string Id, string Kind, string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path, string Because,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Element);

/// <summary>What a database's materialize screens list.</summary>
/// <param name="Database">The database id.</param>
/// <param name="DatabaseName">The database name.</param>
/// <param name="Entities">The entities with no binding to the database, by name.</param>
/// <param name="Sources">The database's designed and imported tables and its views, with the entities bound to each.</param>
public sealed record MaterializeStatus(string Database, string DatabaseName, IReadOnlyList<MaterializeEntityStatus> Entities, IReadOnlyList<MaterializeSourceStatus> Sources);

/// <summary>An entity with no binding to the database.</summary>
/// <param name="Id">The entity id.</param>
/// <param name="Name">The entity name.</param>
/// <param name="Package">The package id.</param>
/// <param name="Projected">Whether the database projects the entity into a table today (by convention or a mapping element).</param>
/// <param name="Table">The projected table's name, when it is projected.</param>
public sealed record MaterializeEntityStatus(string Id, string Name, string? Package, bool Projected, string? Table);

/// <summary>A table or view of the database and the entities bound to it.</summary>
/// <param name="Id">The table or view id.</param>
/// <param name="Kind"><c>table</c> or <c>view</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Schema">The schema name.</param>
/// <param name="Origin"><c>designed</c> or <c>imported</c> (a view: <c>designed</c>).</param>
/// <param name="BoundBy">The bindings that read or write it; empty when no entity is bound to it.</param>
public sealed record MaterializeSourceStatus(string Id, string Kind, string Name, string? Schema, string Origin, IReadOnlyList<MaterializeBinder> BoundBy);

/// <summary>An entity bound to a table or view.</summary>
/// <param name="Entity">The entity id.</param>
/// <param name="EntityName">The entity name.</param>
/// <param name="Binding">The binding id.</param>
/// <param name="Constants">The binding's constants (what tells several entities of one table apart).</param>
public sealed record MaterializeBinder(string Entity, string EntityName, string Binding, IReadOnlyList<MaterializeConstant> Constants);

/// <summary>A constant column of a binding.</summary>
/// <param name="Column">The column name.</param>
/// <param name="Value">The value (absent for NULL).</param>
public sealed record MaterializeConstant(string Column, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Value);
