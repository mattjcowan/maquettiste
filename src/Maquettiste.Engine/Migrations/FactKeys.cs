using System.Collections.Frozen;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Migrations;

/// <summary>
/// The applied order of a database's migration facts and the keys their objects have today (next-phase-1-design.md section 3.2).
/// A fact names objects by the physical keys they had when it was recorded; a later <c>rekey</c> fact (a materialize, or its undo)
/// moves those keys, so every reader moves a fact's keys through the <c>rekey</c> facts after it in applied order. The resolved
/// lists, <c>migration list</c>, <c>list_migrations</c> and the classifier share <see cref="Order"/>.
/// </summary>
internal static class FactKeys
{
    /// <summary>The object kinds a table holds as members: a member names its table's key and its own.</summary>
    public static readonly FrozenSet<string> MemberKinds =
        new[] { "column", "index", "unique", "foreign-key", "check" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The member kinds whose <c>rekey</c> entries move a constraint's element id (a fold into a stereotype's, next-phase-2-design.md
    /// section 3.1): each names its table as a column entry does, and none moves a capture key, since the capture keys of keys and
    /// indexes follow their columns. <see cref="KeyMaps"/> and <see cref="RekeyMap"/> skip them.
    /// </summary>
    public static readonly FrozenSet<string> ConstraintKinds =
        new[] { "index", "unique", "foreign-key", "check" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The applied order of one database's facts: a topological order over their <c>after</c> links that takes the ready fact with
    /// the ordinally smallest id. Links to facts outside <paramref name="facts"/> (missing, MQ4111, or of another database, MQ4105)
    /// are ignored; when the links form a cycle (MQ4104) and no fact is ready, the unplaced fact with the smallest id is placed next,
    /// so the order is total and the same on every run.
    /// </summary>
    /// <param name="facts">The facts of one database.</param>
    /// <returns>The facts in applied order.</returns>
    public static IReadOnlyList<Migration> Order(IEnumerable<Migration> facts)
    {
        var byId = new SortedDictionary<string, Migration>(StringComparer.Ordinal);
        foreach (var fact in facts)
            byId.TryAdd(fact.Id, fact);
        if (byId.Count <= 1)
            return [.. byId.Values];

        var remaining = new Dictionary<string, int>(byId.Count, StringComparer.Ordinal);
        var followers = new Dictionary<string, List<string>>(byId.Count, StringComparer.Ordinal);
        foreach (var (id, fact) in byId)
        {
            var count = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in fact.After)
            {
                if (string.Equals(target, id, StringComparison.Ordinal) || !byId.ContainsKey(target) || !seen.Add(target))
                    continue;
                count++;
                if (!followers.TryGetValue(target, out var list))
                    followers[target] = list = [];
                list.Add(id);
            }

            remaining[id] = count;
        }

        var ready = new SortedSet<string>(remaining.Where(p => p.Value == 0).Select(p => p.Key), StringComparer.Ordinal);
        var unplaced = new SortedSet<string>(byId.Keys, StringComparer.Ordinal);
        var order = new List<Migration>(byId.Count);
        while (unplaced.Count > 0)
        {
            var next = ready.Count > 0 ? ready.Min! : unplaced.Min!; // a cycle: the smallest unplaced id breaks it
            ready.Remove(next);
            unplaced.Remove(next);
            order.Add(byId[next]);
            if (!followers.TryGetValue(next, out var list))
                continue;
            foreach (var follower in list)
            {
                if (unplaced.Contains(follower) && --remaining[follower] == 0)
                    ready.Add(follower);
            }
        }

        return order;
    }

    /// <summary>The key moves of facts in applied order: what <see cref="KeyMoves.Current"/> reads.</summary>
    /// <param name="ordered">One database's facts in applied order (<see cref="Order"/>).</param>
    /// <returns>The moves.</returns>
    public static KeyMoves Moves(IReadOnlyList<Migration> ordered)
    {
        var rekeys = new List<(int Position, RekeyMap Map)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Fact == MigrationFact.Rekey)
                rekeys.Add((i, RekeyMap.Of(ordered[i])));
        }

        return new KeyMoves(rekeys);
    }

    /// <summary>
    /// The key maps of a <c>rekey</c> fact for <see cref="SnapshotRekey.Apply"/>: one table entry per table key it moves or whose
    /// columns it moves (a column entry's table is its table's key before the fact), and its sequence keys; constraint entries
    /// (<see cref="ConstraintKinds"/>) move no capture key and are skipped. With
    /// <paramref name="reverse"/>, the maps that undo the fact: each key back from <c>to</c> to <c>from</c>.
    /// </summary>
    /// <param name="rekey">A <c>rekey</c> fact.</param>
    /// <param name="reverse">Whether to undo the fact.</param>
    /// <returns>The table maps, by old key, and the sequence map.</returns>
    public static (IReadOnlyList<TableRekey> Tables, IReadOnlyDictionary<string, string> Sequences) KeyMaps(Migration rekey, bool reverse = false)
    {
        var tables = new SortedDictionary<string, string>(StringComparer.Ordinal); // old key to new key, as the fact reads
        var columns = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal); // old table key to its column moves
        var sequences = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in rekey.Keys)
        {
            switch (key.Kind)
            {
                case "table":
                    tables.TryAdd(key.From, key.To);
                    break;
                case "sequence":
                    sequences.TryAdd(key.From, key.To);
                    break;
                case "column" when key.Table is { } table:
                    if (!columns.TryGetValue(table, out var moves))
                        columns[table] = moves = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    moves.TryAdd(key.From, key.To);
                    break;
            }
        }

        var result = new List<TableRekey>();
        foreach (var oldKey in tables.Keys.Union(columns.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var newKey = tables.GetValueOrDefault(oldKey) ?? oldKey;
            var moves = columns.GetValueOrDefault(oldKey);
            if (reverse)
            {
                var back = new Dictionary<string, string>(StringComparer.Ordinal);
                if (moves is not null)
                {
                    foreach (var (from, to) in moves)
                        back.TryAdd(to, from);
                }

                result.Add(new TableRekey(newKey, oldKey, back));
            }
            else
            {
                result.Add(new TableRekey(oldKey, newKey, moves is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(moves, StringComparer.Ordinal)));
            }
        }

        var sequenceMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in sequences)
        {
            if (reverse)
                sequenceMap.TryAdd(to, from);
            else
                sequenceMap.TryAdd(from, to);
        }

        return ([.. result.OrderBy(t => t.OldKey, StringComparer.Ordinal)], sequenceMap);
    }

    /// <summary>
    /// The SQL object keys a <c>rekey</c> fact moves (next-phase-1-design.md section 3.4: a run-once SQL object's ledger row follows
    /// its id), from <c>from</c> to <c>to</c>; the first entry for a key wins.
    /// </summary>
    /// <param name="rekey">A <c>rekey</c> fact.</param>
    /// <returns>The moves, by old key.</returns>
    public static IReadOnlyDictionary<string, string> SqlObjectMoves(Migration rekey)
    {
        var moves = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in rekey.Keys)
        {
            if (key.Kind == "sql-object")
                moves.TryAdd(key.From, key.To);
        }

        return moves;
    }

    /// <summary>The key moves of the <c>rekey</c> facts of one applied order.</summary>
    internal sealed class KeyMoves
    {
        private readonly List<(int Position, RekeyMap Map)> _rekeys;

        internal KeyMoves(List<(int Position, RekeyMap Map)> rekeys) => _rekeys = rekeys;

        /// <summary>Whether any fact of the order is a <c>rekey</c>.</summary>
        public bool Any => _rekeys.Count > 0;

        /// <summary>
        /// An object's keys moved through every <c>rekey</c> fact after position <paramref name="position"/>, in applied order. A
        /// table, a sequence and a SQL object move by their own entries; a member moves its table key by the table's entry, and a
        /// column also its own key by the column entry under its table's key before that fact. Other keys (views, routines, the
        /// derived keys of indexes and constraints) do not move.
        /// </summary>
        /// <param name="position">The fact's place in the order (0-based).</param>
        /// <param name="kind">The object kind.</param>
        /// <param name="table">The member's table key, as recorded.</param>
        /// <param name="key">The object's key, as recorded.</param>
        /// <returns>The keys today.</returns>
        public (string? Table, string Key) Current(int position, string kind, string? table, string key)
        {
            foreach (var (at, map) in _rekeys)
            {
                if (at > position)
                    (table, key) = map.Move(kind, table, key);
            }

            return (table, key);
        }
    }

    /// <summary>The moves one <c>rekey</c> fact records.</summary>
    internal sealed class RekeyMap
    {
        private readonly Dictionary<string, string> _tables = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Table, string From), string> _columns = [];
        private readonly Dictionary<string, string> _sequences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sqlObjects = new(StringComparer.Ordinal);

        /// <summary>The moves of a <c>rekey</c> fact; the first entry for a key wins (MQ4106 reports a key listed twice).</summary>
        public static RekeyMap Of(Migration rekey)
        {
            var map = new RekeyMap();
            foreach (var key in rekey.Keys)
            {
                switch (key.Kind)
                {
                    case "table":
                        map._tables.TryAdd(key.From, key.To);
                        break;
                    case "sequence":
                        map._sequences.TryAdd(key.From, key.To);
                        break;
                    case "sql-object":
                        map._sqlObjects.TryAdd(key.From, key.To);
                        break;
                    case "column" when key.Table is { } table:
                        map._columns.TryAdd((table, key.From), key.To);
                        break;
                }
            }

            return map;
        }

        /// <summary>An object's keys after the fact.</summary>
        public (string? Table, string Key) Move(string kind, string? table, string key)
        {
            if (kind == "table")
                return (table, _tables.GetValueOrDefault(key) ?? key);
            if (kind == "sequence")
                return (table, _sequences.GetValueOrDefault(key) ?? key);
            if (kind == "sql-object")
                return (table, _sqlObjects.GetValueOrDefault(key) ?? key);
            if (!MemberKinds.Contains(kind) || table is null)
                return (table, key);
            var moved = kind == "column" && _columns.TryGetValue((table, key), out var column) ? column : key;
            return (_tables.GetValueOrDefault(table) ?? table, moved);
        }
    }
}
