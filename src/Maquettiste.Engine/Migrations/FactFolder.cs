using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Migrations;

/// <summary>
/// <c>migration fold</c> (next-phase-1-design.md section 3.2, "Fold"; task A14): what folding one database's open facts writes. The
/// candidates are the open facts the baseline lacks (the caller's predicate: the <c>--against</c> model's facts, else the facts the
/// committed snapshot lists as rendered, else pending facts only). Stale chains go first (<see cref="StaleFacts.Chains"/>): they are
/// removed, and a link to one names what it followed instead. Then two candidates on one object (by its key today) join when they are
/// consecutive (no other open fact of the same table lies between them in applied order), in one database, and both held or both not
/// held:
/// <list type="bullet">
/// <item><description>renames: <c>a</c> to <c>b</c> and <c>b</c> to <c>c</c> become <c>a</c> to <c>c</c> with <c>via: ["b"]</c>,
/// keeping every intermediate name;</description></item>
/// <item><description>a <c>change-type</c> whose own step is a widening joins its neighbour, which keeps its conversion and data
/// check;</description></item>
/// <item><description>two <c>change-type</c> facts that are both still pending (neither carries a conversion) join;</description></item>
/// <item><description>two <c>change-type</c> facts that each carry a conversion are refused and stay in order;</description></item>
/// <item><description>a rename then a drop of the object join into the drop, the names in <c>object.formerNames</c>;</description></item>
/// <item><description><c>rekey</c> facts with no other open fact between them chain; <c>raw</c>, <c>adopt</c>, <c>baseline</c>,
/// <c>backfill</c> and the maps never join.</description></item>
/// </list>
/// The joined fact keeps the last fact's id and file; its <c>after</c> is the union of the members' <c>after</c>, minus the members.
/// The others are deleted, and links to them name the kept fact, unless that would order facts in a cycle: then the join is refused.
/// </summary>
internal static class FactFolder
{
    /// <summary>The reason a join is refused when it would order facts in a cycle.</summary>
    internal const string CycleReason = "the join would order facts in a cycle";

    /// <summary>What folding one database writes.</summary>
    /// <param name="Joined">The joins, in applied order of the kept fact.</param>
    /// <param name="RemovedStale">The stale facts removed, in applied order.</param>
    /// <param name="Refused">The joins refused.</param>
    /// <param name="Changed">The facts whose document changes (kept facts, and facts whose <c>after</c> links were rewired), by id.</param>
    /// <param name="Deleted">The facts deleted (stale ones and joined members), in applied order.</param>
    internal sealed record Plan(IReadOnlyList<FoldJoin> Joined, IReadOnlyList<string> RemovedStale, IReadOnlyList<FoldRefusal> Refused,
        IReadOnlyList<Migration> Changed, IReadOnlyList<string> Deleted);

    /// <summary>Folds one database's open facts.</summary>
    /// <param name="ordered">The database's open facts in applied order (<see cref="FactKeys.Order"/>).</param>
    /// <param name="isCandidate">Whether a fact is a candidate (the baseline lacks it).</param>
    /// <param name="capture">The database's capture in the working model, for the stale chains.</param>
    /// <param name="dialect">The database's dialect (the widening table).</param>
    /// <returns>What to write.</returns>
    public static Plan Fold(IReadOnlyList<Migration> ordered, Func<Migration, bool> isCandidate, PhysicalSnapshot capture, Dialect dialect)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(isCandidate);
        ArgumentNullException.ThrowIfNull(capture);
        var original = ordered.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var current = new Dictionary<string, Migration>(original, StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);

        // Stale chains go first. A link to a removed fact is replaced by what that fact followed, so the order it gave stays.
        var stale = StaleFacts.Chains(ordered, isCandidate, capture, dialect).Select(s => s.Fact.Id).ToList();
        var staleSet = stale.ToHashSet(StringComparer.Ordinal);
        removed.UnionWith(stale);
        foreach (var id in stale)
        {
            var through = current[id].After;
            foreach (var other in current.Values.ToList())
            {
                if (!string.Equals(other.Id, id, StringComparison.Ordinal) && other.After.Contains(id, StringComparer.Ordinal))
                    current[other.Id] = other with { After = Links(other.After.Where(a => !string.Equals(a, id, StringComparison.Ordinal)).Concat(through), other.Id) };
            }
        }

        // Joins: per object, the candidates in applied order, each joined to the open group of its object when consecutive.
        var moves = FactKeys.Moves(ordered);
        var tables = new HashSet<string>?[ordered.Count];
        var staleAt = new bool[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            tables[i] = TablesOf(ordered[i], i, moves);
            staleAt[i] = staleSet.Contains(ordered[i].Id);
        }

        var groups = new List<Group>();
        var open = new Dictionary<(string Kind, string? Table, string Key), Group>();
        var refused = new List<FoldRefusal>();
        for (var position = 0; position < ordered.Count; position++)
        {
            var fact = ordered[position];
            if (staleSet.Contains(fact.Id) || !isCandidate(fact) || IdentityOf(fact, position, moves) is not { } identity)
                continue;
            if (open.TryGetValue(identity, out var group))
            {
                var rekey = fact.Fact == MigrationFact.Rekey;
                if (group.Current.Held == fact.Held && Consecutive(group.Last, position, rekey ? null : tables[position], rekey, tables, staleAt))
                {
                    var (joined, refusal) = TryJoin(group.Current, fact, dialect);
                    if (joined is not null)
                    {
                        group.Members.Add(fact);
                        group.Current = joined;
                        group.Last = position;
                        continue;
                    }

                    if (refusal is not null)
                        refused.Add(new FoldRefusal([group.Members[^1].Id, fact.Id], refusal));
                }
            }

            var started = new Group(fact, position);
            groups.Add(started);
            open[identity] = started;
        }

        // Each join, in applied order of its kept fact: the kept fact takes the members' links, the links to the others name it.
        var joins = new List<FoldJoin>();
        foreach (var group in groups.Where(g => g.Members.Count > 1).OrderBy(g => g.Last))
        {
            var members = group.Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var keptId = group.Members[^1].Id;
            var trial = new Dictionary<string, Migration>(current, StringComparer.Ordinal)
            {
                [keptId] = group.Current with
                {
                    After = Links(group.Members.SelectMany(m => current[m.Id].After).Where(a => !members.Contains(a)), keptId),
                },
            };
            foreach (var other in current.Values)
            {
                if (removed.Contains(other.Id) || members.Contains(other.Id) || !other.After.Any(members.Contains))
                    continue;
                trial[other.Id] = other with { After = Links(other.After.Select(a => members.Contains(a) ? keptId : a), other.Id) };
            }

            var gone = new HashSet<string>(removed, StringComparer.Ordinal);
            gone.UnionWith(members.Where(m => !string.Equals(m, keptId, StringComparison.Ordinal)));
            if (InCycle(trial, keptId, gone))
            {
                refused.Add(new FoldRefusal([.. group.Members.Select(m => m.Id)], CycleReason));
                continue;
            }

            current = trial;
            removed.UnionWith(gone);
            joins.Add(new FoldJoin(keptId, [.. group.Members.Take(group.Members.Count - 1).Select(m => m.Id)], ResolutionValues.Kebab(group.Current.Fact)));
        }

        var changed = current.Values.Where(f => !removed.Contains(f.Id) && !ReferenceEquals(f, original[f.Id]) && !Same(f, original[f.Id]))
            .OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        var deleted = ordered.Where(f => removed.Contains(f.Id)).Select(f => f.Id).ToList();
        return new Plan(joins, stale, refused, changed, deleted);
    }

    /// <summary>Whether two versions of a fact are the same, <c>after</c> compared as a set.</summary>
    private static bool Same(Migration a, Migration b) =>
        a with { After = [] } == b with { After = [] } && a.After.Order(StringComparer.Ordinal).SequenceEqual(b.After.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>
    /// The object a fact joins on, by its keys today: a rename, a <c>change-type</c> or a drop of one object (kind, table key of a member,
    /// key); every <c>rekey</c> of the database shares one; other facts never join.
    /// </summary>
    private static (string Kind, string? Table, string Key)? IdentityOf(Migration fact, int position, FactKeys.KeyMoves moves)
    {
        if (fact.Fact == MigrationFact.Rekey)
            return ("rekey", null, "");
        if (fact.Fact is not (MigrationFact.Rename or MigrationFact.ChangeType or MigrationFact.Drop) || fact.Object is not { } o)
            return null;
        var (table, key) = moves.Current(position, o.Kind, o.Table, o.Key);
        return (o.Kind, FactKeys.MemberKinds.Contains(o.Kind) ? table : null, key);
    }

    /// <summary>
    /// The tables (by key today) a fact changes: a member's table, a table, view, sequence or schema itself, every key a <c>rekey</c>
    /// moves (a SQL object's as itself); <see langword="null"/> for a fact that may change any (a <c>raw</c> fact with no objects, a <c>baseline</c>).
    /// </summary>
    private static HashSet<string>? TablesOf(Migration fact, int position, FactKeys.KeyMoves moves)
    {
        if (fact.Fact == MigrationFact.Baseline || (fact.Fact == MigrationFact.Raw && fact.Objects.Count == 0 && fact.Object is null))
            return null;
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (fact.Fact == MigrationFact.Rekey)
        {
            var moved = fact.Keys.Where(k => k.Kind == "table").GroupBy(k => k.From, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().To, StringComparer.Ordinal);
            foreach (var key in fact.Keys)
            {
                var (kind, now) = key.Kind switch
                {
                    "sequence" => ("sequence", key.To),
                    "sql-object" => ("sql-object", key.To),
                    "column" when key.Table is { } table => ("table", moved.GetValueOrDefault(table) ?? table),
                    _ => ("table", key.To),
                };
                result.Add(moves.Current(position, kind, null, now).Key);
            }

            return result;
        }

        foreach (var o in fact.Objects.Prepend(fact.Object).OfType<MigrationObject>())
        {
            var (table, key) = moves.Current(position, o.Kind, o.Table, o.Key);
            result.Add(FactKeys.MemberKinds.Contains(o.Kind) ? table ?? key : key);
        }

        return result;
    }

    /// <summary>
    /// Whether two facts at <paramref name="from"/> and <paramref name="to"/> are consecutive: no open fact between them (stale ones
    /// aside) changes one of <paramref name="on"/>; for <c>rekey</c> facts, no open fact lies between them at all.
    /// </summary>
    private static bool Consecutive(int from, int to, HashSet<string>? on, bool any, HashSet<string>?[] tables, bool[] staleAt)
    {
        for (var k = from + 1; k < to; k++)
        {
            if (staleAt[k])
                continue;
            if (any || tables[k] is not { } between || on is null || between.Overlaps(on))
                return false;
        }

        return true;
    }

    /// <summary>A join of two consecutive facts on one object, or why it is refused, or neither (they stay as they are).</summary>
    private static (Migration? Joined, string? Refusal) TryJoin(Migration a, Migration b, Dialect dialect)
    {
        switch (a.Fact, b.Fact)
        {
            case (MigrationFact.Rename, MigrationFact.Rename):
                if (a.To?.Name is not { } middle || !string.Equals(b.From?.Name, middle, StringComparison.Ordinal))
                    return (null, null);
                return (b with { Object = a.Object, From = a.From, Via = [.. a.Via, middle, .. b.Via] }, null);

            case (MigrationFact.Rename, MigrationFact.Drop) when b.Object is { } dropped:
                var former = (a.Object?.FormerNames ?? []).Append(a.From?.Name).Concat(a.Via).Concat(dropped.FormerNames)
                    .OfType<string>().Where(n => n.Length > 0 && !string.Equals(n, dropped.Name, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal).ToList();
                return (b with { Object = dropped with { FormerNames = former } }, null);

            case (MigrationFact.ChangeType, MigrationFact.ChangeType):
                if (a is not { From: { } aFrom, To: { } aTo } || b is not { From: not null, To: { } bTo })
                    return (null, null);
                if (Widening.Classify(dialect, aFrom, aTo) is TypeChange.Same or TypeChange.Widening)
                    return (b with { Object = a.Object, From = aFrom }, null); // the neighbour keeps its conversion and data check
                if (Widening.Classify(dialect, b.From, bTo) is TypeChange.Same or TypeChange.Widening)
                    return (b with { Object = a.Object, From = aFrom, Conversion = a.Conversion, DataCheck = a.DataCheck }, null);
                if (a.Conversion is null && b.Conversion is null)
                    return (b with { Object = a.Object, From = aFrom, DataCheck = b.DataCheck ?? a.DataCheck }, null);
                if (a.Conversion is not null && b.Conversion is not null)
                    return (null, "both carry a conversion; they stay in order (join them by hand: one conversion from the first type to the last)");
                return (null, null);

            case (MigrationFact.Rekey, MigrationFact.Rekey):
                var keys = Compose(a, b);
                return keys.Count == 0 ? (null, null) : (b with { Keys = keys }, null);

            default:
                return (null, null);
        }
    }

    /// <summary>
    /// The keys of two <c>rekey</c> facts applied one after the other: each key from its value before the first to its value after
    /// the second (a column under its table's key before the first); keys that end where they began are left out.
    /// </summary>
    private static List<MigrationRekey> Compose(Migration a, Migration b)
    {
        var (aTables, aSequences) = FactKeys.KeyMaps(a);
        var (bTables, bSequences) = FactKeys.KeyMaps(b);
        var tables = new SortedDictionary<string, (string To, Dictionary<string, string> Columns)>(StringComparer.Ordinal);
        foreach (var t in aTables)
            tables[t.OldKey] = (t.NewKey, new Dictionary<string, string>(t.Columns, StringComparer.Ordinal));
        var aBack = aTables.GroupBy(t => t.NewKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().OldKey, StringComparer.Ordinal);
        foreach (var t in bTables)
        {
            var origin = aBack.GetValueOrDefault(t.OldKey) ?? t.OldKey;
            if (!tables.TryGetValue(origin, out var entry))
            {
                tables[origin] = (t.NewKey, new Dictionary<string, string>(t.Columns, StringComparer.Ordinal));
                continue;
            }

            var back = entry.Columns.GroupBy(c => c.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
            var columns = new Dictionary<string, string>(entry.Columns, StringComparer.Ordinal);
            foreach (var (from, to) in t.Columns)
                columns[back.GetValueOrDefault(from) ?? from] = to;
            tables[origin] = (t.NewKey, columns);
        }

        var sequences = new SortedDictionary<string, string>(aSequences.ToDictionary(StringComparer.Ordinal), StringComparer.Ordinal);
        var sequenceBack = aSequences.GroupBy(s => s.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
        foreach (var (from, to) in bSequences)
            sequences[sequenceBack.GetValueOrDefault(from) ?? from] = to;

        var aObjects = FactKeys.SqlObjectMoves(a);
        var sqlObjects = new SortedDictionary<string, string>(aObjects.ToDictionary(StringComparer.Ordinal), StringComparer.Ordinal);
        var objectBack = aObjects.GroupBy(o => o.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
        foreach (var (from, to) in FactKeys.SqlObjectMoves(b))
            sqlObjects[objectBack.GetValueOrDefault(from) ?? from] = to;

        // A composed key keeps the name the later fact gave it, else the earlier one's.
        string? NameOf(string kind, string? table, string to) =>
            b.Keys.FirstOrDefault(k => k.Kind == kind && string.Equals(k.To, to, StringComparison.Ordinal))?.Name
            ?? a.Keys.FirstOrDefault(k => k.Kind == kind && string.Equals(k.To, to, StringComparison.Ordinal))?.Name;

        var keys = new List<MigrationRekey>();
        foreach (var (from, (to, columns)) in tables)
        {
            if (!string.Equals(from, to, StringComparison.Ordinal))
                keys.Add(new MigrationRekey { Kind = "table", From = from, To = to, Name = NameOf("table", null, to) });
            foreach (var (columnFrom, columnTo) in columns.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (!string.Equals(columnFrom, columnTo, StringComparison.Ordinal))
                    keys.Add(new MigrationRekey { Kind = "column", Table = from, From = columnFrom, To = columnTo, Name = NameOf("column", from, columnTo) });
            }
        }

        foreach (var (from, to) in sequences)
        {
            if (!string.Equals(from, to, StringComparison.Ordinal))
                keys.Add(new MigrationRekey { Kind = "sequence", From = from, To = to, Name = NameOf("sequence", null, to) });
        }

        foreach (var (from, to) in sqlObjects)
        {
            if (!string.Equals(from, to, StringComparison.Ordinal))
                keys.Add(new MigrationRekey { Kind = "sql-object", From = from, To = to, Name = NameOf("sql-object", null, to) });
        }

        return keys;
    }

    /// <summary>Whether <paramref name="start"/> follows itself through <c>after</c> links (facts in <paramref name="gone"/> left out).</summary>
    private static bool InCycle(Dictionary<string, Migration> facts, string start, HashSet<string> gone)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(facts[start].After);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (string.Equals(id, start, StringComparison.Ordinal))
                return true;
            if (gone.Contains(id) || !seen.Add(id) || !facts.TryGetValue(id, out var fact))
                continue;
            foreach (var next in fact.After)
                stack.Push(next);
        }

        return false;
    }

    /// <summary>A fact's <c>after</c> links: distinct, ordinal, without itself.</summary>
    private static IReadOnlyList<string> Links(IEnumerable<string> links, string self) =>
        [.. links.Where(l => !string.Equals(l, self, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The facts joined on one object so far.</summary>
    private sealed class Group(Migration first, int position)
    {
        public List<Migration> Members { get; } = [first];

        public Migration Current { get; set; } = first;

        public int Last { get; set; } = position;
    }
}
