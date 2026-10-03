using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// The aliases of a committed schema snapshot (<see cref="PhysicalSnapshot.Aliases"/>, erratum E43): <c>materialize-tables</c> records,
/// for each projected table it stores as a table file, the table's synthesized key, the file's id and each column's key before and
/// after; the schema diff reads the snapshot through them. Nothing is rewritten when a table is stored, nor when an undo puts the
/// projection back (the overlay and mapping recreated, the table file deleted), nor when a redo stores it again: whichever key the
/// model gives the table, the snapshot is read under that key (<see cref="Normalize"/>), so the next migration sees one table, never a
/// drop and a create. Saved snapshots keep the aliases (<see cref="Carry"/>).
/// </summary>
internal static class SnapshotAliases
{
    /// <summary>
    /// The snapshot with the aliases of a materialize recorded: an alias replaces one with the same key (the same file again keeps
    /// the column ids it recorded before, for columns stored then and not now). Storing a key again after an undo takes back the ids its
    /// alias recorded (<see cref="Loading.Materializer"/>), so that store changes nothing here. Without a snapshot, an alias-only one
    /// (revision 0, nothing else) that <see cref="Normalize"/> reads as no snapshot.
    /// </summary>
    /// <param name="snapshot">The committed snapshot, or <see langword="null"/>.</param>
    /// <param name="database">The database (for an alias-only snapshot).</param>
    /// <param name="tables">The tables stored as files.</param>
    /// <param name="sequences">The key sequences stored as sequence files: synthesized key to file id.</param>
    /// <returns>The snapshot to save, or <see langword="null"/> when nothing changes.</returns>
    public static PhysicalSnapshot? Record(PhysicalSnapshot? snapshot, Database database, IReadOnlyList<TableRekey> tables, IReadOnlyDictionary<string, string> sequences)
    {
        var added = tables.Select(t => new SnapshotAlias { Key = t.OldKey, Alias = t.NewKey, Columns = Sorted(t.Columns) })
            .Concat(sequences.Select(p => new SnapshotAlias { Key = p.Key, Alias = p.Value, Kind = "sequence" }))
            .ToList();
        if (added.Count == 0)
            return null;
        var byKey = new SortedDictionary<string, SnapshotAlias>(StringComparer.Ordinal);
        foreach (var alias in snapshot?.Aliases ?? [])
            byKey[alias.Key] = alias;
        var changed = false;
        foreach (var stored in added)
        {
            var alias = stored;
            if (byKey.TryGetValue(alias.Key, out var known) && known.Alias == alias.Alias && known.Kind == alias.Kind && known.Columns is { Count: > 0 } before)
            {
                // The same file stored again: the columns it had before and has no longer keep their ids, so a later store of them
                // takes them back too.
                var merged = new SortedDictionary<string, string>(before.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
                foreach (var (path, id) in alias.Columns ?? new Dictionary<string, string>())
                    merged[path] = id;
                alias = alias with { Columns = merged };
            }

            if (known is not null && Same(known, alias))
                continue;
            byKey[alias.Key] = alias;
            changed = true;
        }

        if (!changed)
            return null;
        var baseline = snapshot ?? new PhysicalSnapshot { Database = database.Id, Name = database.Name, Dialect = database.Dialect };
        return baseline with { Aliases = [.. byKey.Values] };
    }

    /// <summary>
    /// The snapshot read under the keys the model gives its tables now: a table the snapshot holds under one key of an alias while the
    /// model has it under the other (and not under the first) is renamed to the model's key, with its column keys, the keys derived
    /// from them, and the foreign keys of other tables that reference it (<see cref="SnapshotRekey"/>); a key sequence the same way. An
    /// alias-only snapshot reads as none.
    /// </summary>
    /// <param name="snapshot">The committed snapshot, or <see langword="null"/>.</param>
    /// <param name="current">The resolved database.</param>
    /// <returns>The snapshot to diff against.</returns>
    public static PhysicalSnapshot? Normalize(PhysicalSnapshot? snapshot, RDatabase current)
    {
        if (snapshot is null || IsAliasOnly(snapshot))
            return null;
        if (snapshot.Aliases is not { Count: > 0 } aliases)
            return snapshot;
        var model = current.Tables.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var modelSequences = current.Sequences.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var held = snapshot.Tables.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var heldSequences = snapshot.Sequences.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var tables = new List<TableRekey>();
        var sequences = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            var (inModel, inSnapshot) = alias.Kind == "sequence" ? (modelSequences, heldSequences) : (model, held);
            string from, to;
            if (inSnapshot.Contains(alias.Key) && !inSnapshot.Contains(alias.Alias) && inModel.Contains(alias.Alias) && !inModel.Contains(alias.Key))
                (from, to) = (alias.Key, alias.Alias);
            else if (inSnapshot.Contains(alias.Alias) && !inSnapshot.Contains(alias.Key) && inModel.Contains(alias.Key) && !inModel.Contains(alias.Alias))
                (from, to) = (alias.Alias, alias.Key);
            else
                continue;
            if (alias.Kind == "sequence")
            {
                sequences[from] = to;
                continue;
            }

            var columns = alias.Columns ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var map = from == alias.Key
                ? new Dictionary<string, string>(columns, StringComparer.Ordinal)
                : columns.GroupBy(p => p.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
            tables.Add(new TableRekey(from, to, map));
        }

        return tables.Count == 0 && sequences.Count == 0 ? snapshot : SnapshotRekey.Apply(snapshot, tables, sequences);
    }

    /// <summary>A snapshot to save, with the aliases of the one it replaces.</summary>
    /// <param name="captured">The snapshot captured from the model.</param>
    /// <param name="previous">The committed snapshot it replaces, as loaded (not normalized), or <see langword="null"/>.</param>
    /// <returns>The snapshot.</returns>
    public static PhysicalSnapshot Carry(PhysicalSnapshot captured, PhysicalSnapshot? previous) =>
        previous?.Aliases is { Count: > 0 } aliases ? captured with { Aliases = aliases } : captured;

    /// <summary>Whether a snapshot holds only aliases (recorded before the database had a snapshot).</summary>
    private static bool IsAliasOnly(PhysicalSnapshot snapshot) =>
        snapshot.Revision == 0 && snapshot.Schemas is null && snapshot.Tables.Count == 0 && snapshot.Views.Count == 0 && snapshot.Sequences.Count == 0
        && snapshot.Types.Count == 0 && snapshot.Routines.Count == 0 && snapshot.Objects.Count == 0;

    private static SortedDictionary<string, string> Sorted(IReadOnlyDictionary<string, string> columns) => new(columns.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);

    private static bool Same(SnapshotAlias a, SnapshotAlias b) =>
        a.Alias == b.Alias && a.Kind == b.Kind && (a.Columns ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal)
            .SequenceEqual((b.Columns ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal));
}
