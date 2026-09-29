using System.Collections.Frozen;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
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
/// <param name="IsLookup">Always <see langword="false"/>: the enum lookup-table option is retired (MQ7012); kept for the contract.</param>
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

/// <summary>The result of <see cref="DatabaseTables.GetTableAsync"/> (E5f).</summary>
/// <param name="Table">The table with its columns, keys and indexes, or <see langword="null"/> when no table of the database has the key,
/// or the table was left out because an owning element has errors.</param>
/// <param name="Diagnostics">As <see cref="DatabaseTablesResult.Diagnostics"/>.</param>
/// <param name="Partial">As <see cref="DatabaseTablesResult.Partial"/>.</param>
public sealed record DatabaseTableResult(TableView? Table, IReadOnlyList<Diagnostic> Diagnostics, bool Partial);

/// <summary>
/// The table list of each database, without columns (E5c), and one table's detail (E5f), for the explorer. Unlike
/// <see cref="GenerationService.GetDatabaseViewAsync"/> it answers on a model with errors: it resolves anyway (the resolver ends on
/// invalid snapshots, Resolution/README.md) and leaves out the tables whose owning elements have errors. Per snapshot, the model is
/// validated once, and each database asked for is resolved on its own (the conceptual layer and that database only,
/// <c>ModelResolver.ResolveDatabaseAsync</c>), so a change costs one database's resolve rather than the whole model's; a resolver
/// without that entry point falls back to one whole-model resolve that answers every database. One instance per host keeps the cache
/// (the last snapshot's databases only; the resolved objects are projected and dropped).
/// </summary>
public sealed class DatabaseTables
{
    private readonly GenerationService _generation;
    private readonly Lock _gate = new();
    private ModelSnapshot? _snapshot;
    private Task<ValidationReport>? _validation;
    private Task<FrozenDictionary<string, Prepared>>? _whole;
    private readonly Dictionary<string, Task<Prepared>> _databases = new(StringComparer.Ordinal);

    /// <summary>Creates the reader.</summary>
    /// <param name="generation">The generation service whose store and services are used.</param>
    public DatabaseTables(GenerationService generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        _generation = generation;
    }

    /// <summary>
    /// The tables of one database, from the store's current snapshot (no rescan, as <see cref="ModelStore.GetIndexAsync"/>: a host's
    /// watcher keeps it current), answered from the cached resolve of that database for that snapshot, or after validating the snapshot
    /// and resolving the database side by side. Takes no run lock and writes nothing.
    /// </summary>
    /// <param name="databaseId">The database element's id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The tables, the diagnostics and whether the list is partial. A database that did not resolve has no tables; on a model
    /// without errors that adds MQ6017.</returns>
    public async Task<DatabaseTablesResult> GetAsync(string databaseId, CancellationToken ct)
    {
        var prepared = await PrepareAsync(databaseId, ct).ConfigureAwait(false);
        return prepared.Result;
    }

    /// <summary>One table of a database with its columns, keys, foreign keys and indexes (E5f), from the same cache as <see cref="GetAsync"/>.</summary>
    /// <param name="databaseId">The database element's id.</param>
    /// <param name="key">The table's key, as <see cref="TableSummary.Key"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The table (null when absent or left out), the diagnostics and whether the model has errors.</returns>
    public async Task<DatabaseTableResult> GetTableAsync(string databaseId, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        var prepared = await PrepareAsync(databaseId, ct).ConfigureAwait(false);
        return new DatabaseTableResult(prepared.Views.GetValueOrDefault(key), prepared.Result.Diagnostics, prepared.Result.Partial);
    }

    private async Task<Prepared> PrepareAsync(string databaseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(databaseId);
        var store = _generation.Store;
        if (store.Current is null)
            await store.LoadAsync(ct).ConfigureAwait(false);
        var snapshot = store.Current!;
        Task<Prepared> task;
        lock (_gate)
        {
            if (!ReferenceEquals(_snapshot, snapshot))
            {
                _snapshot = snapshot;
                _validation = null;
                _whole = null;
                _databases.Clear();
            }

            if (_databases.TryGetValue(databaseId, out var cached) && !cached.IsFaulted && !cached.IsCanceled)
            {
                task = cached;
            }
            else
            {
                if (_validation is null || _validation.IsFaulted || _validation.IsCanceled)
                    _validation = Shared(() => _generation.Services.Validator.ValidateAsync(snapshot, new ValidationScope(), null, CancellationToken.None));
                var validation = _validation;
                var resolver = _generation.Services.Resolver;
                if (resolver is ModelResolver perDatabase)
                {
                    task = Shared(() => PrepareOneAsync(snapshot, databaseId, validation, perDatabase));
                }
                else
                {
                    if (_whole is null || _whole.IsFaulted || _whole.IsCanceled)
                        _whole = Shared(() => PrepareWholeAsync(snapshot, validation, resolver));
                    var whole = _whole;
                    task = Shared(async () => (await whole.ConfigureAwait(false)).GetValueOrDefault(databaseId) ?? Missing(databaseId, await validation.ConfigureAwait(false), []));
                }

                _databases[databaseId] = task;
            }
        }

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared work runs without the caller's token, so one cancelled request does not fail the others waiting on it; each caller
    /// stops waiting on its own token.
    /// </summary>
    private static Task<T> Shared<T>(Func<Task<T>> work) => Task.Run(work, CancellationToken.None);

    private static async Task<Prepared> PrepareOneAsync(ModelSnapshot snapshot, string databaseId, Task<ValidationReport> validating, ModelResolver resolver)
    {
        // Validation and resolution run side by side, as a pipelined generation run does (the resolver ends on invalid snapshots).
        var resolving = resolver.ResolveDatabaseAsync(snapshot, databaseId, CancellationToken.None);
        var report = await validating.ConfigureAwait(false);
        (RDatabase? Database, IReadOnlyList<Diagnostic> Diagnostics) resolved;
        try
        {
            resolved = await resolving.ConfigureAwait(false);
        }
        catch (Exception ex) when (report.HasErrors && ex is not OperationCanceledException)
        {
            // A shape validation rejects that the resolver still cannot take: nothing resolves, and the errors say why.
            return Unresolved(report);
        }

        var diagnostics = Outcomes.Sort(report.Diagnostics.Concat(resolved.Diagnostics));
        return resolved.Database is { } database ? Project(snapshot, database, diagnostics) : Missing(databaseId, report, diagnostics);
    }

    private static async Task<FrozenDictionary<string, Prepared>> PrepareWholeAsync(ModelSnapshot snapshot, Task<ValidationReport> validating, IModelResolver resolver)
    {
        // The fallback: one whole-model resolve answers every database of the snapshot.
        var resolving = resolver.ResolveAsync(snapshot, null, CancellationToken.None);
        var report = await validating.ConfigureAwait(false);
        ResolvedModel resolved;
        try
        {
            resolved = await resolving.ConfigureAwait(false);
        }
        catch (Exception ex) when (report.HasErrors && ex is not OperationCanceledException)
        {
            return FrozenDictionary<string, Prepared>.Empty;
        }

        var diagnostics = Outcomes.Sort(report.Diagnostics.Concat(resolved.Diagnostics));
        var result = new Dictionary<string, Prepared>(StringComparer.Ordinal);
        foreach (var database in resolved.Databases)
            result.TryAdd(database.Id, Project(snapshot, database, diagnostics));
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static Prepared Unresolved(ValidationReport report) =>
        new(new DatabaseTablesResult([], Outcomes.Sort(report.Diagnostics), true), FrozenDictionary<string, TableView>.Empty);

    private static Prepared Missing(string databaseId, ValidationReport report, IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            diagnostics = Outcomes.Sort(report.Diagnostics);
        if (diagnostics.Any(Outcomes.IsInvalid))
            return new Prepared(new DatabaseTablesResult([], diagnostics, true), FrozenDictionary<string, TableView>.Empty);
        return new Prepared(
            new DatabaseTablesResult([], [.. diagnostics, RuleCatalog.Create("MQ6017", $"No database has the id '{databaseId}'.", databaseId)], false),
            FrozenDictionary<string, TableView>.Empty);
    }

    /// <summary>Projects one resolved database: the summaries and the table views of the tables whose owning elements have no errors.</summary>
    private static Prepared Project(ModelSnapshot snapshot, RDatabase database, IReadOnlyList<Diagnostic> diagnostics)
    {
        var invalid = diagnostics.Where(Outcomes.IsInvalid).ToList();
        var failed = FailedOwners(snapshot, invalid);
        var partial = invalid.Count > 0;
        if (failed.Contains(database.Id))
            return new Prepared(new DatabaseTablesResult([], diagnostics, partial), FrozenDictionary<string, TableView>.Empty);
        // Each table is projected on its own (pure reads of the resolved objects), in parallel on large databases, then gathered in order.
        var tables = database.Tables;
        var projected = new (TableSummary Summary, TableView View)?[tables.Count];
        void ProjectAt(int i)
        {
            var table = tables[i];
            if (!Fails(table, failed))
                projected[i] = (Summarize(table), DatabaseViews.Table(table));
        }

        if (tables.Count < 256)
        {
            for (var i = 0; i < tables.Count; i++)
                ProjectAt(i);
        }
        else
        {
            Parallel.For(0, tables.Count, ProjectAt);
        }

        var list = new List<TableSummary>(tables.Count);
        var views = new Dictionary<string, TableView>(tables.Count, StringComparer.Ordinal);
        foreach (var item in projected)
        {
            if (item is not { } p)
                continue;
            list.Add(p.Summary);
            views.TryAdd(p.Summary.Key, p.View);
        }

        return new Prepared(new DatabaseTablesResult(list, diagnostics, partial), views.ToFrozenDictionary(StringComparer.Ordinal));
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
        false, // the enum lookup-table option is retired (MQ7012)
        table.Columns.Count);

    private sealed record Prepared(DatabaseTablesResult Result, FrozenDictionary<string, TableView> Views);
}
