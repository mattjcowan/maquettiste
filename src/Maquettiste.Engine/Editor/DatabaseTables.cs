using System.Collections.Frozen;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>One table of a database without its columns (E5c, explorer-redesign.md section 4.1).</summary>
/// <param name="Key">As <see cref="TableView.Key"/>: a table file's id, or a synthesized key such as <c>&lt;entityId&gt;@&lt;databaseId&gt;</c>.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Schema">The schema name, or <see langword="null"/>.</param>
/// <param name="Origin"><c>synthesized</c>, <c>designed</c> or <c>imported</c>.</param>
/// <param name="EntityId">The mapped entity's id, for an entity table.</param>
/// <param name="RelationId">The relation's id, for a junction table.</param>
/// <param name="IsJunction">Whether the table is a relation's junction table.</param>
/// <param name="IsLookup">Whether the table is an enum lookup table.</param>
/// <param name="ColumnCount">The number of columns.</param>
public sealed record TableSummary(
    string Key,
    string Name,
    string? Schema,
    string Origin,
    string? EntityId,
    string? RelationId,
    bool IsJunction,
    bool IsLookup,
    int ColumnCount);

/// <summary>The result of <see cref="DatabaseTables.GetAsync"/> (E5c).</summary>
/// <param name="Tables">The tables that resolve, in the resolver's order.</param>
/// <param name="Diagnostics">Every validation and resolution diagnostic, sorted.</param>
/// <param name="Partial">
/// Whether the model has errors: tables whose owning elements have errors are then left out, and the rest may change once the errors are
/// fixed.
/// </param>
public sealed record DatabaseTablesResult(IReadOnlyList<TableSummary> Tables, IReadOnlyList<Diagnostic> Diagnostics, bool Partial);

/// <summary>
/// The table list of each database, without columns, for the explorer (E5c). Unlike <see cref="GenerationService.GetDatabaseViewAsync"/>
/// it answers on a model with errors: it resolves anyway (the resolver ends on invalid snapshots, Resolution/README.md) and leaves out
/// the tables whose owning elements have errors. The model is validated and resolved once per snapshot, and every database is answered
/// from that one resolve; one instance per host keeps that cache (it holds the last snapshot's result only).
/// </summary>
public sealed class DatabaseTables
{
    private readonly GenerationService _generation;
    private readonly Lock _gate = new();
    private (ModelSnapshot Snapshot, Task<Prepared> Task)? _last;

    /// <summary>Creates the reader.</summary>
    /// <param name="generation">The generation service whose store and services are used.</param>
    public DatabaseTables(GenerationService generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        _generation = generation;
    }

    /// <summary>
    /// The tables of one database, from the store's current snapshot (no rescan, as <see cref="ModelStore.GetIndexAsync"/>: a host's
    /// watcher keeps it current), answered from the cached resolve of that snapshot, or after validating and resolving it side by side.
    /// Takes no run lock and writes nothing.
    /// </summary>
    /// <param name="databaseId">The database element's id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The tables, the diagnostics and whether the list is partial. A database that did not resolve has no tables; on a model
    /// without errors that adds MQ6017.</returns>
    public async Task<DatabaseTablesResult> GetAsync(string databaseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(databaseId);
        var store = _generation.Store;
        if (store.Current is null)
            await store.LoadAsync(ct).ConfigureAwait(false);
        var snapshot = store.Current!;
        var prepared = await PrepareAsync(snapshot, ct).ConfigureAwait(false);
        if (prepared.Tables.TryGetValue(databaseId, out var tables))
            return new DatabaseTablesResult(tables, prepared.Diagnostics, prepared.Partial);
        if (prepared.Partial)
            return new DatabaseTablesResult([], prepared.Diagnostics, true);
        return new DatabaseTablesResult([], [.. prepared.Diagnostics, RuleCatalog.Create("MQ6017", $"No database has the id '{databaseId}'.", databaseId)], false);
    }

    private Task<Prepared> PrepareAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        Task<Prepared> task;
        lock (_gate)
        {
            if (_last is { } last && ReferenceEquals(last.Snapshot, snapshot) && !last.Task.IsFaulted && !last.Task.IsCanceled)
            {
                task = last.Task;
            }
            else
            {
                // The shared work runs without the caller's token, so one cancelled request does not fail the others waiting on it;
                // each caller stops waiting on its own token.
                task = Task.Run(() => PrepareCoreAsync(snapshot, CancellationToken.None), CancellationToken.None);
                _last = (snapshot, task);
            }
        }

        return task.WaitAsync(ct);
    }

    private async Task<Prepared> PrepareCoreAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        // Validation and resolution run side by side, as a pipelined generation run does (the resolver ends on invalid snapshots).
        var services = _generation.Services;
        var validating = services.Validator.ValidateAsync(snapshot, new ValidationScope(), null, ct);
        var resolving = services.Resolver.ResolveAsync(snapshot, null, ct);
        var report = await validating.ConfigureAwait(false);
        ResolvedModel resolved;
        try
        {
            resolved = await resolving.ConfigureAwait(false);
        }
        catch (Exception ex) when (report.HasErrors && ex is not OperationCanceledException)
        {
            // A shape validation rejects that the resolver still cannot take: nothing resolves, and the errors say why.
            return new Prepared(FrozenDictionary<string, IReadOnlyList<TableSummary>>.Empty, Outcomes.Sort(report.Diagnostics), true);
        }

        var diagnostics = Outcomes.Sort(report.Diagnostics.Concat(resolved.Diagnostics));
        var invalid = diagnostics.Where(Outcomes.IsInvalid).ToList();
        var failed = FailedOwners(snapshot, invalid);
        var tables = new Dictionary<string, IReadOnlyList<TableSummary>>(StringComparer.Ordinal);
        foreach (var database in resolved.Databases)
        {
            if (failed.Contains(database.Id))
            {
                tables.TryAdd(database.Id, []);
                continue;
            }

            var list = new List<TableSummary>(database.Tables.Count);
            foreach (var table in database.Tables)
            {
                if (!Fails(table, failed))
                    list.Add(Summarize(table));
            }

            tables.TryAdd(database.Id, list);
        }

        return new Prepared(tables.ToFrozenDictionary(StringComparer.Ordinal), diagnostics, invalid.Count > 0);
    }

    /// <summary>
    /// The top-level ids of the elements with errors, plus, for a mapping or table file with errors, the entity, relation and table it
    /// applies to (a broken mapping or overlay makes its target's table untrustworthy).
    /// </summary>
    private static HashSet<string> FailedOwners(ModelSnapshot snapshot, List<Diagnostic> invalid)
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in invalid)
        {
            if (diagnostic.ElementId is not { } id)
                continue;
            var owner = snapshot.TryGetEntry(id, out var entry) ? entry.OwnerId : id;
            if (!failed.Add(owner))
                continue;
            switch (snapshot.GetDocument(owner)?.Element)
            {
                case Mapping mapping:
                    AddIfSet(failed, mapping.Entity);
                    AddIfSet(failed, mapping.Relation);
                    AddIfSet(failed, mapping.Table);
                    break;
                case Table table:
                    AddIfSet(failed, table.Entity);
                    AddIfSet(failed, table.Relation);
                    break;
            }
        }

        return failed;
    }

    private static void AddIfSet(HashSet<string> set, string? id)
    {
        if (id is not null)
            set.Add(id);
    }

    /// <summary>Whether a table has an owning element with errors: its entity, relation, table file, or the element its key names.</summary>
    private static bool Fails(RTable table, HashSet<string> failed)
    {
        if (failed.Count == 0)
            return false;
        if (table.Entity is { } entity && failed.Contains(entity.Id))
            return true;
        if (table.Relation is { } relation && failed.Contains(relation.Id))
            return true;
        if (failed.Contains(table.Key))
            return true;
        var at = table.Key.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && failed.Contains(table.Key[..at]);
    }

    private static TableSummary Summarize(RTable table) => new(
        table.Key,
        table.Name,
        table.Schema,
        table.Origin,
        table.Entity?.Id,
        table.Relation?.Id,
        table.IsJunction,
        table.IsLookup,
        table.Columns.Count);

    private sealed record Prepared(FrozenDictionary<string, IReadOnlyList<TableSummary>> Tables, IReadOnlyList<Diagnostic> Diagnostics, bool Partial);
}
