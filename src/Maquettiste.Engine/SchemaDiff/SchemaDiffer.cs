using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.SchemaDiff;

/// <summary>
/// Captures a resolved database as a <see cref="PhysicalSnapshot"/> and diffs two snapshots (W8; SPEC section 9 "Schema diff for
/// migrations", engine-design.md section 14). Objects are matched by their stable keys (section 7.3), never by name, so a changed
/// name with the same key is <see cref="ChangeKind.Renamed"/> while a new key is a drop plus an add. The differ is pure: no I/O,
/// no clock, and every list it returns is in a fixed order.
/// </summary>
internal sealed class SchemaDiffer : ISchemaDiffer
{
    /// <inheritdoc/>
    public PhysicalSnapshot Capture(RDatabase database, int revision) => SnapshotCapture.Capture(database, revision);

    /// <inheritdoc/>
    public SchemaDiffResult Diff(PhysicalSnapshot? previous, RDatabase current) => DiffAndCapture(previous, current, 1, CancellationToken.None).Diff;

    /// <summary>
    /// <see cref="Diff"/>, also returning the current snapshot it captured (stamped with <paramref name="previous"/>'s revision), so
    /// a run that saves the snapshot after applying does not capture the database a second time: the snapshot to save is
    /// <c>Current with { Revision = Diff.ToRevision }</c>, which is what <see cref="Capture"/> would return for that revision.
    /// </summary>
    /// <param name="previous">The previous snapshot, or <see langword="null"/>.</param>
    /// <param name="current">The current resolved database.</param>
    /// <returns>The diff and the captured snapshot.</returns>
    /// <param name="parallelism">Threads for capturing and for finding the unchanged tables (the result does not depend on it).</param>
    /// <param name="ct">Cancellation, observed between tables of the parallel capture and compare and between the two.</param>
    internal (SchemaDiffResult Diff, PhysicalSnapshot Current) DiffAndCapture(PhysicalSnapshot? previous, RDatabase current, int parallelism,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        var after = SnapshotCapture.Capture(current, previous?.Revision ?? 0, parallelism, ct);
        ct.ThrowIfCancellationRequested();
        return (Compare(previous, after, current, parallelism, ct), after);
    }

    /// <summary>Diffs two snapshots. <paramref name="current"/>, when given, supplies the <see cref="RTable"/> and <see cref="RColumn"/> of each change.</summary>
    /// <param name="before">The committed snapshot, or <see langword="null"/> when there is none (everything is added).</param>
    /// <param name="after">The current snapshot.</param>
    /// <param name="current">The current resolved database, or <see langword="null"/>.</param>
    /// <param name="parallelism">Threads for finding the unchanged tables (the result does not depend on it).</param>
    /// <param name="ct">Cancellation, observed by the parallel unchanged-table check.</param>
    /// <returns>The diff; <see cref="SchemaDiffResult.FromRevision"/> is <paramref name="before"/>'s revision (0 without one).</returns>
    internal static SchemaDiffResult Compare(PhysicalSnapshot? before, PhysicalSnapshot after, RDatabase? current, int parallelism = 1,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(after);
        var tablesByKey = new Dictionary<string, RTable>(StringComparer.Ordinal);
        if (current is not null)
        {
            foreach (var table in current.Tables)
                tablesByKey.TryAdd(table.Key, table);
        }

        var oldTables = ByKey(before?.Tables ?? [], t => t.Key);
        var newTables = ByKey(after.Tables, t => t.Key);

        var added = new List<TableChange>();
        var renamed = new List<TableChange>();
        var altered = new List<TableChange>();
        var dropped = new List<TableChange>();
        // Which tables are unchanged is decided first (in parallel on a large database; Unchanged is pure), then the tables are
        // walked in key order as before.
        var pairs = newTables.ToArray();
        var unchanged = new bool[pairs.Length];
        var body = (int i) => unchanged[i] = oldTables.TryGetValue(pairs[i].Key, out var old) && Unchanged(old, pairs[i].Value);
        if (parallelism > 1 && pairs.Length >= 512)
            Parallel.For(0, pairs.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i => body(i));
        else
        {
            for (var i = 0; i < pairs.Length; i++)
                body(i);
        }

        for (var i = 0; i < pairs.Length; i++)
        {
            if (unchanged[i])
                continue;
            var (key, table) = pairs[i];
            var rTable = tablesByKey.GetValueOrDefault(key);
            if (!oldTables.TryGetValue(key, out var old))
            {
                added.Add(new TableChange(ChangeKind.Added, key, null, table.Name, rTable, [], [], [], [], [], []));
                continue;
            }

            var change = CompareTable(old, table, rTable);
            if (change is null)
                continue;
            (change.Kind == ChangeKind.Renamed ? renamed : altered).Add(change);
        }

        foreach (var (key, table) in oldTables)
        {
            if (!newTables.ContainsKey(key))
                dropped.Add(new TableChange(ChangeKind.Dropped, key, table.Name, null, null, [], [], [], [], [], []) { OldSchema = table.Schema, OldComment = table.Comment });
        }

        var tables = new List<TableChange>(added.Count + renamed.Count + altered.Count + dropped.Count);
        tables.AddRange(DependencyOrder(added, newTables));
        tables.AddRange(renamed);
        tables.AddRange(altered);
        var dropOrder = DependencyOrder(dropped, oldTables);
        dropOrder.Reverse();
        tables.AddRange(dropOrder);

        var views = WithOldViews(WithOldSchemas(CompareObjects(before?.Views ?? [], after.Views, v => v.Key, v => v.Name, ViewProperties), before?.Views, v => v.Key, v => v.Schema),
            before?.Views);
        var sequences = WithOldSchemas(CompareObjects(before?.Sequences ?? [], after.Sequences, s => s.Key, s => s.Name, SequenceProperties), before?.Sequences,
            s => s.Key, s => s.Schema);
        // Schemas are compared only when both snapshots record them (a snapshot written by 0.5.5 or earlier has none, and a first migration
        // creates every schema with its tables).
        var schemas = before?.Schemas is { } oldSchemas && after.Schemas is { } newSchemas
            ? CompareObjects(oldSchemas, newSchemas, s => s.Key, s => s.Name, (_, _) => [])
            : [];

        // The database's own properties have no slot on SchemaDiffResult; a change to them still makes the diff non-empty
        // (the committed snapshot is stale) and reaches the hash.
        var databaseChanged = before is not null
            && (before.Dialect != after.Dialect || !string.Equals(before.Name, after.Name, StringComparison.Ordinal));
        var types = CompareDefinitions(before?.Types ?? [], after.Types);
        var routines = CompareDefinitions(before?.Routines ?? [], after.Routines);
        var objects = CompareDefinitions(before?.Objects ?? [], after.Objects);
        var isEmpty = !databaseChanged && tables.Count == 0 && views.Count == 0 && sequences.Count == 0 && types.Count == 0 && routines.Count == 0
            && objects.Count == 0 && schemas.Count == 0;
        var from = before?.Revision ?? 0;
        var to = isEmpty ? from : from + 1;
        var hash = Hash(before, after, from, to, tables, oldTables, newTables, views, sequences, types, routines, objects, schemas);
        return new SchemaDiffResult(current?.Name ?? after.Name, from, to, isEmpty, hash, tables, views, sequences)
        {
            Schemas = schemas,
            Types = types,
            Routines = routines,
            Objects = objects,
        };
    }

    private static TableChange? CompareTable(SnapshotTable old, SnapshotTable table, RTable? rTable)
    {
        if (Unchanged(old, table))
            return null;
        var columnsByKey = new Dictionary<string, RColumn>(StringComparer.Ordinal);
        if (rTable is not null)
        {
            foreach (var column in rTable.Columns)
                columnsByKey.TryAdd(column.Key, column);
        }

        var columns = CompareColumns(old.Columns, table.Columns, columnsByKey);
        var primaryKey = CompareObjects<SnapshotConstraint>(
            old.PrimaryKey is null ? [] : [old.PrimaryKey], table.PrimaryKey is null ? [] : [table.PrimaryKey],
            c => c.Key, c => c.Name, ConstraintProperties);
        var uniques = WithOldKeys(CompareObjects(old.Uniques, table.Uniques, c => c.Key, c => c.Name, ConstraintProperties), old.Uniques, c => c.Key,
            c => [.. c.Columns], _ => true);
        var foreignKeys = CompareObjects(old.ForeignKeys, table.ForeignKeys, f => f.Key, f => f.Name, ForeignKeyProperties);
        var checks = CompareObjects(old.Checks, table.Checks, c => c.Key, c => c.Name, CheckProperties);
        var exclusions = CompareObjects(old.Exclusions, table.Exclusions, x => x.Key, x => x.Name, ExclusionProperties);
        var partitions = CompareObjects(old.Partitions, table.Partitions, p => p.Key, p => p.Name, PartitionProperties);
        var indexes = WithOldKeys(CompareObjects(old.Indexes, table.Indexes, i => i.Key, i => i.Name, IndexProperties), old.Indexes, i => i.Key,
            i => [.. i.Columns.Select(c => c.Column)], i => i.Unique && i.Where is null);
        if (indexes.Count > 0)
        {
            var oldIndexes = ByKey(old.Indexes, i => i.Key);
            indexes = [.. indexes.Select(c => c.Kind != ChangeKind.Added && oldIndexes.TryGetValue(c.Key, out var i) ? c with { OldStorage = OldStorage(i.Storage) } : c)];
        }

        var isRenamed = !string.Equals(old.Name, table.Name, StringComparison.Ordinal);
        var properties = TableProperties(old, table);
        // Schema and comment changes have no list of their own on TableChange; they still make the table Altered.
        var ownChanged = !string.Equals(old.Schema, table.Schema, StringComparison.Ordinal)
            || !string.Equals(old.Comment, table.Comment, StringComparison.Ordinal) || properties.Count > 0;
        var anyChild = columns.Count > 0 || primaryKey.Count > 0 || uniques.Count > 0 || foreignKeys.Count > 0 || checks.Count > 0 || indexes.Count > 0
            || exclusions.Count > 0 || partitions.Count > 0;
        if (!isRenamed && !ownChanged && !anyChild)
            return null;
        return new TableChange(isRenamed ? ChangeKind.Renamed : ChangeKind.Altered, table.Key, old.Name, table.Name, rTable,
            columns, primaryKey, uniques, foreignKeys, checks, indexes) { OldSchema = old.Schema, OldComment = old.Comment, Changes = properties, OldStorage = OldStorage(old.Storage), Exclusions = exclusions, Partitions = partitions };
    }

    /// <summary>
    /// Sets <see cref="ObjectChange.OldColumns"/> and <see cref="ObjectChange.OldUnique"/> on every change of a unique constraint or index
    /// that existed before, so a migration drops the foreign keys that rely on a key before it drops the key.
    /// </summary>
    private static List<ObjectChange> WithOldKeys<T>(List<ObjectChange> changes, IReadOnlyList<T> oldItems, Func<T, string> key, Func<T, IReadOnlyList<string?>> columns,
        Func<T, bool> unique)
    {
        if (changes.Count == 0)
            return changes;
        var old = ByKey(oldItems, key);
        return [.. changes.Select(c => c.Kind != ChangeKind.Added && old.TryGetValue(c.Key, out var item) ? c with { OldColumns = columns(item), OldUnique = unique(item) } : c)];
    }

    /// <summary>Sets <see cref="ObjectChange.OldSchema"/> on every change of a view or sequence that existed before.</summary>
    private static List<ObjectChange> WithOldSchemas<T>(List<ObjectChange> changes, IReadOnlyList<T>? oldItems, Func<T, string> key, Func<T, string?> schema)
    {
        if (changes.Count == 0 || oldItems is null)
            return changes;
        var old = ByKey(oldItems, key);
        return [.. changes.Select(c => c.Kind != ChangeKind.Added && old.TryGetValue(c.Key, out var item) ? c with { OldSchema = schema(item) } : c)];
    }

    /// <summary>
    /// Sets <see cref="ObjectChange.OldDependsOn"/> and <see cref="ObjectChange.OldMaterialized"/> on every change of a view that existed
    /// before, so a migration drops views that read others first and drops a materialized view as one.
    /// </summary>
    private static List<ObjectChange> WithOldViews(List<ObjectChange> changes, IReadOnlyList<SnapshotView>? oldViews)
    {
        if (changes.Count == 0 || oldViews is null)
            return changes;
        var old = ByKey(oldViews, v => v.Key);
        return [.. changes.Select(c => c.Kind != ChangeKind.Added && old.TryGetValue(c.Key, out var view)
            ? c with { OldDependsOn = view.DependsOn, OldMaterialized = view.Materialized }
            : c)];
    }

    /// <summary>
    /// Whether a table is unchanged, checked pairwise in list order without building the keyed maps <see cref="CompareTable"/> uses
    /// (almost every table of an incremental run is unchanged: 10,004 of 10,005 in the benchmark). Two tables whose names, schemas
    /// and comments match and whose columns, keys, constraints and indexes match pairwise (same keys and names, no property
    /// changes by the same property comparisons) are exactly the tables <see cref="CompareTable"/> finds no change in; any other
    /// pair (a reordering included) takes the full comparison.
    /// </summary>
    private static bool Unchanged(SnapshotTable a, SnapshotTable b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal) || !string.Equals(a.Schema, b.Schema, StringComparison.Ordinal)
            || !string.Equals(a.Comment, b.Comment, StringComparison.Ordinal) || TableProperties(a, b).Count > 0)
            return false;
        if (a.PrimaryKey is null != b.PrimaryKey is null
            || (a.PrimaryKey is not null && !SameObject(a.PrimaryKey, b.PrimaryKey!, c => c.Key, c => c.Name, ConstraintProperties)))
            return false;
        return SameList(a.Columns, b.Columns, c => c.Key, c => c.Name, ColumnProperties)
            && SameList(a.Uniques, b.Uniques, c => c.Key, c => c.Name, ConstraintProperties)
            && SameList(a.ForeignKeys, b.ForeignKeys, f => f.Key, f => f.Name, ForeignKeyProperties)
            && SameList(a.Checks, b.Checks, c => c.Key, c => c.Name, CheckProperties)
            && SameList(a.Exclusions, b.Exclusions, x => x.Key, x => x.Name, ExclusionProperties)
            && SameList(a.Partitions, b.Partitions, p => p.Key, p => p.Name, PartitionProperties)
            && SameList(a.Indexes, b.Indexes, i => i.Key, i => i.Name, IndexProperties);
    }

    private static bool SameList<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, string> key, Func<T, string> name,
        Func<T, T, List<PropertyChange>> properties)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!SameObject(a[i], b[i], key, name, properties))
                return false;
        }

        return true;
    }

    private static bool SameObject<T>(T a, T b, Func<T, string> key, Func<T, string> name, Func<T, T, List<PropertyChange>> properties) =>
        string.Equals(key(a), key(b), StringComparison.Ordinal) && string.Equals(name(a), name(b), StringComparison.Ordinal)
        && properties(a, b).Count == 0;

    /// <summary>Column changes: added (current position order), renamed and altered (current position order), then dropped (old position order).</summary>
    private static List<ColumnChange> CompareColumns(IReadOnlyList<SnapshotColumn> oldColumns, IReadOnlyList<SnapshotColumn> newColumns,
        Dictionary<string, RColumn> columnsByKey)
    {
        var oldByKey = ByKey(oldColumns, c => c.Key);
        var newByKey = ByKey(newColumns, c => c.Key);
        var added = new List<ColumnChange>();
        var changed = new List<ColumnChange>();
        var dropped = new List<ColumnChange>();
        foreach (var column in newColumns)
        {
            if (!ReferenceEquals(newByKey[column.Key], column))
                continue;
            var rColumn = columnsByKey.GetValueOrDefault(column.Key);
            if (!oldByKey.TryGetValue(column.Key, out var old))
            {
                added.Add(new ColumnChange(ChangeKind.Added, column.Key, null, column.Name, [], rColumn));
                continue;
            }

            var properties = ColumnProperties(old, column);
            var isRenamed = !string.Equals(old.Name, column.Name, StringComparison.Ordinal);
            if (isRenamed || properties.Count > 0)
                changed.Add(new ColumnChange(isRenamed ? ChangeKind.Renamed : ChangeKind.Altered, column.Key, old.Name, column.Name, properties, rColumn) { OldDefaultName = old.DefaultName });
        }

        foreach (var column in oldColumns)
        {
            if (ReferenceEquals(oldByKey[column.Key], column) && !newByKey.ContainsKey(column.Key))
                dropped.Add(new ColumnChange(ChangeKind.Dropped, column.Key, column.Name, null, [], null) { OldDefaultName = column.DefaultName });
        }

        return [.. added, .. changed, .. dropped];
    }

    /// <summary>Object changes grouped as added, renamed, altered, dropped, each ordinal by key.</summary>
    private static List<ObjectChange> CompareObjects<T>(IReadOnlyList<T> oldItems, IReadOnlyList<T> newItems, Func<T, string> key, Func<T, string> name,
        Func<T, T, List<PropertyChange>> properties)
    {
        var oldByKey = ByKey(oldItems, key);
        var newByKey = ByKey(newItems, key);
        var added = new List<ObjectChange>();
        var renamed = new List<ObjectChange>();
        var altered = new List<ObjectChange>();
        var dropped = new List<ObjectChange>();
        foreach (var (k, item) in newByKey)
        {
            if (!oldByKey.TryGetValue(k, out var old))
            {
                added.Add(new ObjectChange(ChangeKind.Added, k, null, name(item), []));
                continue;
            }

            var changes = properties(old, item);
            if (!string.Equals(name(old), name(item), StringComparison.Ordinal))
                renamed.Add(new ObjectChange(ChangeKind.Renamed, k, name(old), name(item), changes));
            else if (changes.Count > 0)
                altered.Add(new ObjectChange(ChangeKind.Altered, k, name(old), name(item), changes));
        }

        foreach (var (k, item) in oldByKey)
        {
            if (!newByKey.ContainsKey(k))
                dropped.Add(new ObjectChange(ChangeKind.Dropped, k, name(item), null, []));
        }

        return [.. added, .. renamed, .. altered, .. dropped];
    }

    /// <summary>Routine, database type or SQL object changes: added, renamed, altered, then dropped, each by key.</summary>
    private static List<DefinitionChange> CompareDefinitions(IReadOnlyList<SnapshotDefinition> oldItems, IReadOnlyList<SnapshotDefinition> newItems)
    {
        var oldByKey = ByKey(oldItems, d => d.Key);
        var newByKey = ByKey(newItems, d => d.Key);
        var added = new List<DefinitionChange>();
        var renamed = new List<DefinitionChange>();
        var altered = new List<DefinitionChange>();
        var dropped = new List<DefinitionChange>();
        foreach (var (k, item) in newByKey)
        {
            if (!oldByKey.TryGetValue(k, out var old))
            {
                added.Add(new DefinitionChange(ChangeKind.Added, k, null, item.Name, null, item.Kind, null, []));
                continue;
            }

            var changes = new List<PropertyChange>();
            Property(changes, "schema", old.Schema, item.Schema);
            Property(changes, "kind", old.Kind, item.Kind);
            Property(changes, "definition", old.Definition, item.Definition);
            if (!string.Equals(old.Name, item.Name, StringComparison.Ordinal))
                renamed.Add(new DefinitionChange(ChangeKind.Renamed, k, old.Name, item.Name, old.Kind, item.Kind, old.Schema, changes));
            else if (changes.Count > 0)
                altered.Add(new DefinitionChange(ChangeKind.Altered, k, old.Name, item.Name, old.Kind, item.Kind, old.Schema, changes));
        }

        foreach (var (k, item) in oldByKey)
        {
            if (!newByKey.ContainsKey(k))
                dropped.Add(new DefinitionChange(ChangeKind.Dropped, k, item.Name, null, item.Kind, null, item.Schema, []));
        }

        return [.. added, .. renamed, .. altered, .. dropped];
    }

    /// <summary>Items by key, ordinal; the first item wins when a key repeats.</summary>
    private static SortedDictionary<string, T> ByKey<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        var result = new SortedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
            result.TryAdd(key(item), item);
        return result;
    }

    /// <summary>
    /// Orders table changes so that a table comes after every table it references by foreign key (within the set); ties and
    /// cycles break by key, ordinal.
    /// </summary>
    private static List<TableChange> DependencyOrder(List<TableChange> changes, SortedDictionary<string, SnapshotTable> tables)
    {
        if (changes.Count <= 1)
            return [.. changes];
        var inSet = changes.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var dependsOn = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            dependsOn[change.Key] = new SortedSet<string>(StringComparer.Ordinal);
            dependents.TryAdd(change.Key, new SortedSet<string>(StringComparer.Ordinal));
        }

        foreach (var change in changes)
        {
            if (!tables.TryGetValue(change.Key, out var table))
                continue;
            foreach (var fk in table.ForeignKeys)
            {
                var target = fk.ReferencedTable;
                if (string.Equals(target, change.Key, StringComparison.Ordinal) || !inSet.ContainsKey(target))
                    continue;
                if (dependsOn[change.Key].Add(target))
                    dependents[target].Add(change.Key);
            }
        }

        var ready = new SortedSet<string>(dependsOn.Where(p => p.Value.Count == 0).Select(p => p.Key), StringComparer.Ordinal);
        var remaining = new SortedSet<string>(inSet.Keys, StringComparer.Ordinal);
        var result = new List<TableChange>(changes.Count);
        while (remaining.Count > 0)
        {
            // A cycle leaves nothing ready: take the smallest remaining key.
            var next = ready.Count > 0 ? ready.Min! : remaining.Min!;
            ready.Remove(next);
            remaining.Remove(next);
            result.Add(inSet[next]);
            foreach (var dependent in dependents[next])
            {
                var pending = dependsOn[dependent];
                pending.Remove(next);
                if (pending.Count == 0 && remaining.Contains(dependent))
                    ready.Add(dependent);
            }
        }

        return result;
    }

    private static List<PropertyChange> ColumnProperties(SnapshotColumn a, SnapshotColumn b)
    {
        var list = new List<PropertyChange>();
        Property(list, "type", a.Type, b.Type);
        Property(list, "length", a.Length, b.Length);
        Property(list, "precision", a.Precision, b.Precision);
        Property(list, "scale", a.Scale, b.Scale);
        Property(list, "nativeType", a.NativeType, b.NativeType);
        Property(list, "nullable", a.Nullable, b.Nullable);
        if (!PlainValues.JsonEquals(a.Default, b.Default))
            list.Add(new PropertyChange("default", PlainValues.From(a.Default), PlainValues.From(b.Default)));
        Property(list, "defaultSql", a.DefaultSql, b.DefaultSql);
        Property(list, "defaultName", a.DefaultName, b.DefaultName);
        Property(list, "identity", a.Identity, b.Identity);
        Property(list, "identitySeed", a.IdentitySeed, b.IdentitySeed);
        Property(list, "identityIncrement", a.IdentityIncrement, b.IdentityIncrement);
        Property(list, "identityAlways", a.IdentityAlways, b.IdentityAlways);
        Property(list, "sequence", a.Sequence, b.Sequence);
        Property(list, "computed", a.Computed, b.Computed);
        Property(list, "computedStored", a.ComputedStored, b.ComputedStored);
        Property(list, "collation", a.Collation, b.Collation);
        Property(list, "comment", a.Comment, b.Comment);
        Property(list, "referenceType", a.ReferenceType, b.ReferenceType);
        Property(list, "strategy", a.Strategy, b.Strategy);
        return list;
    }

    private static List<PropertyChange> ConstraintProperties(SnapshotConstraint a, SnapshotConstraint b)
    {
        var list = new List<PropertyChange>();
        Property(list, "columns", a.Columns, b.Columns);
        Property(list, "clustered", a.Clustered, b.Clustered);
        Property(list, "nullsNotDistinct", a.NullsNotDistinct, b.NullsNotDistinct);
        Property(list, "withoutOverlaps", a.WithoutOverlaps, b.WithoutOverlaps);
        return list;
    }

    private static List<PropertyChange> ForeignKeyProperties(SnapshotForeignKey a, SnapshotForeignKey b)
    {
        var list = new List<PropertyChange>();
        Property(list, "columns", a.Columns, b.Columns);
        Property(list, "referencedTable", a.ReferencedTable, b.ReferencedTable);
        Property(list, "referencedColumns", a.ReferencedColumns, b.ReferencedColumns);
        Property(list, "period", a.Period, b.Period);
        Property(list, "onDelete", PlainValues.Kebab(a.OnDelete), PlainValues.Kebab(b.OnDelete));
        Property(list, "onDeleteColumns", a.OnDeleteColumns, b.OnDeleteColumns);
        Property(list, "onUpdate", PlainValues.Kebab(a.OnUpdate), PlainValues.Kebab(b.OnUpdate));
        if (a.Deferrable != b.Deferrable)
            list.Add(new PropertyChange("deferrable", PlainValues.Kebab(a.Deferrable), PlainValues.Kebab(b.Deferrable)));
        return list;
    }

    /// <summary>An exclusion constraint's properties outside its key (its definition forms the key): when it is checked.</summary>
    private static List<PropertyChange> ExclusionProperties(SnapshotExclusion a, SnapshotExclusion b)
    {
        var list = new List<PropertyChange>();
        if (a.Deferrable != b.Deferrable)
            list.Add(new PropertyChange("deferrable", PlainValues.Kebab(a.Deferrable), PlainValues.Kebab(b.Deferrable)));
        return list;
    }

    private static List<PropertyChange> CheckProperties(SnapshotCheck a, SnapshotCheck b)
    {
        var list = new List<PropertyChange>();
        Property(list, "expression", a.Expression, b.Expression);
        return list;
    }

    private static List<PropertyChange> IndexProperties(SnapshotIndex a, SnapshotIndex b)
    {
        var list = new List<PropertyChange>();
        Property(list, "columns", IndexColumns(a.Columns), IndexColumns(b.Columns));
        Property(list, "include", a.Include, b.Include);
        Property(list, "where", a.Where, b.Where);
        Property(list, "unique", a.Unique, b.Unique);
        Property(list, "method", PlainValues.Kebab(a.Method), PlainValues.Kebab(b.Method));
        Property(list, "storage", StorageText(a.Storage), StorageText(b.Storage));
        return list;
    }

    /// <summary>A snapshot's storage parameters as templates read them.</summary>
    private static IReadOnlyList<RStorageParameter> OldStorage(IReadOnlyList<SnapshotStorageParameter> storage) =>
        [.. storage.Select(p => new RStorageParameter { Name = p.Name, Value = p.Value })];

    /// <summary>Storage parameters as <c>name = value</c> texts, for a property change.</summary>
    internal static IReadOnlyList<string> StorageText(IReadOnlyList<SnapshotStorageParameter> storage) => [.. storage.Select(p => p.Name + " = " + p.Value)];

    /// <summary>A table's own properties that have no list of their own (storage), compared as a property list.</summary>
    private static List<PropertyChange> TableProperties(SnapshotTable a, SnapshotTable b)
    {
        var list = new List<PropertyChange>();
        Property(list, "storage", StorageText(a.Storage), StorageText(b.Storage));
        Property(list, "partitionBy", PartitionText(a.PartitionBy), PartitionText(b.PartitionBy));
        return list;
    }

    /// <summary>A table's partitioning as text (<c>range(key, key)</c>), or <see langword="null"/>.</summary>
    private static string? PartitionText(SnapshotPartitionBy? by) => by is null ? null : PlainValues.Kebab(by.Strategy) + "(" + string.Join(", ", by.Columns) + ")";

    private static List<PropertyChange> PartitionProperties(SnapshotPartition a, SnapshotPartition b)
    {
        var list = new List<PropertyChange>();
        Property(list, "bounds", a.Default ? "DEFAULT" : a.Bounds, b.Default ? "DEFAULT" : b.Bounds);
        return list;
    }

    private static List<PropertyChange> ViewProperties(SnapshotView a, SnapshotView b)
    {
        var list = new List<PropertyChange>();
        Property(list, "schema", a.Schema, b.Schema);
        Property(list, "body", a.Body, b.Body);
        Property(list, "columns", a.Columns, b.Columns);
        Property(list, "withCheckOption", a.WithCheckOption, b.WithCheckOption);
        Property(list, "securityInvoker", a.SecurityInvoker, b.SecurityInvoker);
        Property(list, "securityBarrier", a.SecurityBarrier, b.SecurityBarrier);
        Property(list, "materialized", a.Materialized, b.Materialized);
        Property(list, "comment", a.Comment, b.Comment);
        return list;
    }

    private static List<PropertyChange> SequenceProperties(SnapshotSequence a, SnapshotSequence b)
    {
        var list = new List<PropertyChange>();
        Property(list, "schema", a.Schema, b.Schema);
        Property(list, "type", a.Type, b.Type);
        Property(list, "start", a.Start, b.Start);
        Property(list, "increment", a.Increment, b.Increment);
        Property(list, "min", a.Min, b.Min);
        Property(list, "max", a.Max, b.Max);
        Property(list, "cycle", a.Cycle, b.Cycle);
        Property(list, "cache", a.Cache, b.Cache);
        return list;
    }

    /// <summary>
    /// Index columns as plain strings: the column key (an expression in parentheses), its prefix length in parentheses, then
    /// <c> desc</c> when descending.
    /// </summary>
    internal static IReadOnlyList<string> IndexColumns(IReadOnlyList<SnapshotIndexColumn> columns) =>
        [.. columns.Select(c => (c.Column ?? "(" + c.Expression + ")")
            + (c.OperatorClass is { } opclass ? " " + opclass : "")
            + (c.Length is { } length ? "(" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : "")
            + (c.Descending ? " desc" : ""))];

    private static void Property<T>(List<PropertyChange> list, string name, T a, T b)
    {
        if (!EqualityComparer<T>.Default.Equals(a, b))
            list.Add(new PropertyChange(name, a, b));
    }

    private static void Property(List<PropertyChange> list, string name, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (!a.SequenceEqual(b, StringComparer.Ordinal))
            list.Add(new PropertyChange(name, a, b));
    }

    /// <summary>
    /// The diff's hash (the <c>d:&lt;databaseId&gt;</c> dependency key, engine-design.md section 11): <c>H</c> over the database
    /// (id, old and new name and dialect), the revisions, every table's old and new schema and comment, and every change with its
    /// property values as JSON. Resolved objects are not hashed; units that read them
    /// record their own dependencies.
    /// </summary>
    private static string Hash(PhysicalSnapshot? before, PhysicalSnapshot after, int from, int to, List<TableChange> tables,
        SortedDictionary<string, SnapshotTable> oldTables, SortedDictionary<string, SnapshotTable> newTables, List<ObjectChange> views,
        List<ObjectChange> sequences, List<DefinitionChange> types, List<DefinitionChange> routines, List<DefinitionChange> objects,
        List<ObjectChange> schemas)
    {
        using var h = new HashBuilder();
        h.Add("mq-schema-diff-2").Add(after.Database).Add(after.Name).Add(PlainValues.Kebab(after.Dialect)).Add(from).Add(to);
        h.Add(before?.Name).Add(before is null ? null : PlainValues.Kebab(before.Dialect));
        h.Add(tables.Count);
        foreach (var table in tables)
        {
            AddHead(h, table.Kind, table.Key, table.OldName, table.NewName);
            // The table's own schema and comment (TableChange has no property list for them).
            var old = oldTables.GetValueOrDefault(table.Key);
            var current = newTables.GetValueOrDefault(table.Key);
            h.Add(old?.Schema).Add(old?.Comment).Add(current?.Schema).Add(current?.Comment);
            h.Add(table.Columns.Count);
            foreach (var column in table.Columns)
            {
                AddHead(h, column.Kind, column.Key, column.OldName, column.NewName);
                AddProperties(h, column.Changes);
            }

            AddObjects(h, table.PrimaryKey);
            AddObjects(h, table.Uniques);
            AddObjects(h, table.ForeignKeys);
            AddObjects(h, table.Checks);
            AddObjects(h, table.Indexes);
            // The table's own property changes reach the hash only when there are some, so every diff without them keeps its hash.
            if (table.Changes.Count > 0)
            {
                h.Add("table-properties");
                AddProperties(h, table.Changes);
            }

            if (table.Exclusions.Count > 0)
            {
                h.Add("exclusions");
                AddObjects(h, table.Exclusions);
            }

            if (table.Partitions.Count > 0)
            {
                h.Add("partitions");
                AddObjects(h, table.Partitions);
            }
        }

        AddObjects(h, views);
        AddObjects(h, sequences);
        // Routines, database types and SQL objects reach the hash only when one changed, so the hash of every diff without them
        // stays what it was before they existed.
        if (types.Count + routines.Count + objects.Count > 0)
        {
            foreach (var list in new[] { types, routines, objects })
            {
                h.Add(list.Count);
                foreach (var change in list)
                {
                    AddHead(h, change.Kind, change.Key, change.OldName, change.NewName);
                    h.Add(change.OldKind).Add(change.NewKind).Add(change.OldSchema);
                    AddProperties(h, change.Changes);
                }
            }
        }

        // Schema changes reach the hash only when there are some, so every diff without them keeps the hash it had before.
        if (schemas.Count > 0)
        {
            h.Add("schemas");
            AddObjects(h, schemas);
        }

        return h.Finish();
    }

    private static void AddObjects(HashBuilder h, IReadOnlyList<ObjectChange> changes)
    {
        h.Add(changes.Count);
        foreach (var change in changes)
        {
            AddHead(h, change.Kind, change.Key, change.OldName, change.NewName);
            AddProperties(h, change.Changes);
        }
    }

    private static void AddHead(HashBuilder h, ChangeKind kind, string key, string? oldName, string? newName) =>
        h.Add(PlainValues.Kebab(kind)).Add(key).Add(oldName).Add(newName);

    private static void AddProperties(HashBuilder h, IReadOnlyList<PropertyChange> changes)
    {
        h.Add(changes.Count);
        foreach (var change in changes)
            h.Add(change.Property).Add(PlainValues.Json(change.Old)).Add(PlainValues.Json(change.New));
    }
}

/// <summary>Conversions of snapshot values into the plain CLR values templates and hashes see.</summary>
internal static class PlainValues
{
    /// <summary>The kebab-case JSON name of an engine enum value (<c>no-action</c>, <c>btree</c>, <c>renamed</c>).</summary>
    /// <typeparam name="T">The enum type, which carries a string enum converter.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The name.</returns>
    public static string Kebab<T>(T value) where T : struct, Enum =>
        JsonSerializer.Deserialize<string>(JsonSerializer.SerializeToUtf8Bytes(value, EngineJson.Options), EngineJson.Options)!;

    /// <summary>Parses a kebab-case JSON name into an engine enum value.</summary>
    /// <typeparam name="T">The enum type, which carries a string enum converter.</typeparam>
    /// <param name="name">The name.</param>
    /// <param name="fallback">The value for an unknown or missing name.</param>
    /// <returns>The value.</returns>
    public static T Parse<T>(string? name, T fallback) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(name))
            return fallback;
        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(name, EngineJson.Options), EngineJson.Options);
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>A plain CLR value as a JSON element, or <see langword="null"/>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The element.</returns>
    public static JsonElement? ToElement(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null } => null,
        JsonElement element => element.Clone(),
        _ => JsonSerializer.SerializeToElement(value, value.GetType(), EngineJson.Options),
    };

    /// <summary>Whether two optional JSON values are equal (deep; <see langword="null"/> equals JSON null).</summary>
    /// <param name="a">The first value.</param>
    /// <param name="b">The second value.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool JsonEquals(JsonElement? a, JsonElement? b)
    {
        var aNull = a is null || a.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        var bNull = b is null || b.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        if (aNull || bNull)
            return aNull == bNull;
        return JsonElement.DeepEquals(a!.Value, b!.Value);
    }

    /// <summary>
    /// A JSON value as a plain CLR value: string, <see cref="long"/>, <see cref="decimal"/> or <see cref="double"/>, bool,
    /// <see langword="null"/>, a list, or a dictionary sorted by key (ordinal), so enumeration order is fixed.
    /// </summary>
    /// <param name="element">The value.</param>
    /// <returns>The plain value.</returns>
    public static object? From(JsonElement? element)
    {
        if (element is not { } e)
            return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                if (e.TryGetInt64(out var l))
                    return l;
                if (e.TryGetDecimal(out var d))
                    return d;
                return e.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Array:
                return e.EnumerateArray().Select(item => From(item)).ToList();
            case JsonValueKind.Object:
                var map = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in e.EnumerateObject())
                    map[property.Name] = From(property.Value);
                return map;
            default:
                return null;
        }
    }

    /// <summary>A plain value as compact JSON, for hashing; <see langword="null"/> stays <see langword="null"/>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The JSON text.</returns>
    public static string? Json(object? value) => value is null ? null : JsonSerializer.Serialize(value, value.GetType(), EngineJson.Options);
}
