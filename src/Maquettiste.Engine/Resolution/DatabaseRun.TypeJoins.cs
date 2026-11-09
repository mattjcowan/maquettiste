using System.Collections.Immutable;
using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The join paths of a relation stored as <see cref="TypeLinks"/> (several-types-design.md decision 48): one path per pair of targets, built from
/// the pointers step 2 resolved, with the conditions a step's rows must also meet. Pure: the run supplies what picks a target's rows in a table.
/// </summary>
internal static class TypeJoinPaths
{
    /// <summary>One pair's path for a navigation: the target at the navigation's from end, the one at its to end, and the path.</summary>
    internal readonly record struct PairPath(Placement From, Placement To, RJoinPath Path);

    /// <summary>
    /// The path of every pair whose link resolved, for a navigation from end <paramref name="from"/> to end <paramref name="to"/>, ordered by
    /// the target at the from end, then at the to end, each in target order (decision 39).
    /// </summary>
    /// <param name="links">The relation's storage.</param>
    /// <param name="from">The navigation's from end.</param>
    /// <param name="to">The navigation's to end.</param>
    /// <param name="constants">The link table's constants (decision 44): conditions on the step that enters the link table.</param>
    /// <param name="rows">What picks a target's rows in a table it enters, when the table holds rows of other entities; empty otherwise.</param>
    /// <returns>The paths.</returns>
    public static List<PairPath> Of(TypeLinks links, REnd from, REnd to, IReadOnlyList<RColumnCondition> constants,
        Func<Placement, RTable, IReadOnlyList<RColumnCondition>> rows)
    {
        var fromEnd = links.Ends.FirstOrDefault(e => string.Equals(e.End.Id, from.Id, StringComparison.Ordinal));
        var toEnd = links.Ends.FirstOrDefault(e => string.Equals(e.End.Id, to.Id, StringComparison.Ordinal));
        var found = new List<(int From, int To, PairPath Path)>();
        if (fromEnd is null || toEnd is null)
            return [];
        foreach (var pair in links.Pairs)
        {
            if (pair.Result is null)
                continue;
            var pf = pair.Ends.FirstOrDefault(p => string.Equals(p.End.Id, from.Id, StringComparison.Ordinal));
            var pt = pair.Ends.FirstOrDefault(p => string.Equals(p.End.Id, to.Id, StringComparison.Ordinal));
            if (pf is null || pt is null || Path(pair, pf, pt, constants, rows) is not { } path)
                continue;
            path.FromType = from.HasSeveralTypes ? pf.Target.Entity : null;
            path.ToType = to.HasSeveralTypes ? pt.Target.Entity : null;
            found.Add((fromEnd.Targets.IndexOf(pf.Target), toEnd.Targets.IndexOf(pt.Target), new PairPath(pf.Target, pt.Target, path)));
        }

        // A stable sort: two pairs of the same targets (none today) keep their pair order.
        return [.. found.OrderBy(x => x.From).ThenBy(x => x.To).Select(x => x.Path)];
    }

    /// <summary>
    /// Way 5's one path (decision 48): when an end with several types is pointed at by one key to a table that holds the rows of every
    /// target (its entry's <c>foreignKey</c>), the pairs' paths differ only in what picks each target's rows in that table and in
    /// <c>from_type</c> or <c>to_type</c> at that end. Without those, they are one path, which <see cref="RNavigation.Joins"/> holds.
    /// <see langword="null"/> when no end is stored that way, no pair resolved, or the paths still differ (the other end's targets live in
    /// several tables).
    /// </summary>
    /// <param name="links">The relation's storage.</param>
    /// <param name="from">The navigation's from end.</param>
    /// <param name="to">The navigation's to end.</param>
    /// <param name="constants">The link table's constants.</param>
    /// <param name="rows">What picks a target's rows in a table it enters.</param>
    /// <returns>The one path, or <see langword="null"/>.</returns>
    public static RJoinPath? Shared(TypeLinks links, REnd from, REnd to, IReadOnlyList<RColumnCondition> constants,
        Func<Placement, RTable, IReadOnlyList<RColumnCondition>> rows)
    {
        var fromShared = IsShared(links, from);
        var toShared = IsShared(links, to);
        if (!fromShared && !toShared)
            return null;
        // Rows are picked only on the step that enters the to end's target table: dropped when that end shares one table.
        var paths = Of(links, from, to, constants, toShared ? (_, _) => [] : rows);
        if (paths.Count == 0)
            return null;
        var first = paths[0].Path;
        if (!paths.TrueForAll(p => SameSteps(p.Path, first)))
            return null;
        return new RJoinPath
        {
            Steps = first.Steps,
            FromType = fromShared ? null : first.FromType,
            ToType = toShared ? null : first.ToType,
        };
    }

    /// <summary>Whether an end with several types is pointed at by one key to a shared table (way 5).</summary>
    private static bool IsShared(TypeLinks links, REnd end) =>
        end.HasSeveralTypes && links.Ends.Any(e => ReferenceEquals(e.End, end) && e.Entry?.ForeignKey is not null);

    private static bool SameSteps(RJoinPath a, RJoinPath b)
    {
        if (a.Steps.Count != b.Steps.Count)
            return false;
        for (var i = 0; i < a.Steps.Count; i++)
        {
            var (x, y) = (a.Steps[i], b.Steps[i]);
            if (!ReferenceEquals(x.FromTable, y.FromTable) || !ReferenceEquals(x.ToTable, y.ToTable) || x.ViaJunction != y.ViaJunction
                || !x.FromColumns.SequenceEqual(y.FromColumns) || !x.ToColumns.SequenceEqual(y.ToColumns) || x.Conditions.Count != y.Conditions.Count)
                return false;
            for (var k = 0; k < x.Conditions.Count; k++)
            {
                if (!ReferenceEquals(x.Conditions[k].Column, y.Conditions[k].Column) || !x.Conditions[k].Values.SequenceEqual(y.Conditions[k].Values))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One pair's path from <paramref name="pf"/>'s target to <paramref name="pt"/>'s: one step when either end's rows hold the link, else two
    /// through the link table; <see langword="null"/> when a pointer didn't resolve.
    /// </summary>
    /// <remarks>
    /// A step ends in the table the pointer refers to and doesn't step on from there (decision 48: paths between the pointer's columns and the
    /// columns they refer to). When that is a base's table holding the rows of every target, as one key at an end with several types over TPT
    /// (way 5), the step's condition is what picks the target's rows there: a TPT base table has no discriminator, so with no binding
    /// constants a pair's path to one type also reaches the rows of the end's other types, as way 5's single path in <c>joins</c> does. MQ4118's
    /// <c>shared-table</c> gap says the key can't tell the types apart. A reader that needs one type's rows joins on into that type's own table,
    /// from <c>to_type</c>'s entity mapping.
    /// </remarks>
    private static RJoinPath? Path(TypeLink pair, TypeLinkPointer pf, TypeLinkPointer pt, IReadOnlyList<RColumnCondition> constants,
        Func<Placement, RTable, IReadOnlyList<RColumnCondition>> rows)
    {
        var holding = pair.Table.Table;
        if (pt.IsHolder)
        {
            // The to end's rows hold the link: from the from target's table into the holding table, which is the to target's.
            if (pf.Result is not { ReferencedTable: { } referenced } f)
                return null;
            return new RJoinPath
            {
                Steps =
                [
                    new RJoinStep
                    {
                        FromTable = referenced, FromColumns = f.ReferencedColumns, ToTable = holding, ToColumns = f.Columns,
                        Conditions = Concat(TypeCondition(f), rows(pt.Target, holding)),
                    },
                ],
            };
        }

        if (pt.Result is not { ReferencedTable: { } toTable } t)
            return null;
        if (pf.IsHolder)
        {
            // The from end's rows hold the link: from the holding table, which is the from target's, into the to target's table.
            return new RJoinPath
            {
                Steps =
                [
                    new RJoinStep
                    {
                        FromTable = holding, FromColumns = t.Columns, ToTable = toTable, ToColumns = t.ReferencedColumns,
                        Conditions = Concat(TypeCondition(t), rows(pt.Target, toTable)),
                    },
                ],
            };
        }

        // A link table: into it through the from end's pointer (its type column and the mapping's constants), then out through the to end's.
        if (pf.Result is not { ReferencedTable: { } fromTable } fr)
            return null;
        return new RJoinPath
        {
            Steps =
            [
                new RJoinStep
                {
                    FromTable = fromTable, FromColumns = fr.ReferencedColumns, ToTable = holding, ToColumns = fr.Columns, ViaJunction = true,
                    Conditions = Concat(TypeCondition(fr), constants),
                },
                new RJoinStep
                {
                    FromTable = holding, FromColumns = t.Columns, ToTable = toTable, ToColumns = t.ReferencedColumns, ViaJunction = true,
                    Conditions = Concat(TypeCondition(t), rows(pt.Target, toTable)),
                },
            ],
        };
    }

    /// <summary>"The type column holds the target's codes", when the pointer has a type column.</summary>
    private static IReadOnlyList<RColumnCondition> TypeCondition(RRelationLinkEnd pointer) =>
        pointer.TypeColumn is { } column && pointer.Codes.Count > 0
            ? [new RColumnCondition { Column = column, Values = [.. pointer.Codes] }]
            : [];

    private static IReadOnlyList<RColumnCondition> Concat(IReadOnlyList<RColumnCondition> first, IReadOnlyList<RColumnCondition> second) =>
        first.Count == 0 ? second : second.Count == 0 ? first : [.. first, .. second];
}

/// <summary>Join paths of relations with an end of several types (several-types-design.md decision 48, section 19.8 step 3).</summary>
internal sealed partial class DatabaseRun
{
    private readonly Dictionary<string, SortedDictionary<string, IReadOnlyList<RJoinPath>>> _typeJoinsByEntity = new(StringComparer.Ordinal);

    /// <summary>The placements with rows in each table (a TPT entity in each table of its chain), built on first use.</summary>
    private Dictionary<RTable, List<Placement>>? _rowsByTable;

    /// <summary>
    /// Step 3 for every relation stored as <see cref="TypeLinks"/>: each navigation's paths per pair (<see cref="RNavigation.TypeJoins"/>, and
    /// <see cref="RNavigation.Joins"/> when there is exactly one: way 5's path to the shared table, or a single pair's), and per entity with an
    /// entity mapping, the paths from the target that stands for it (<see cref="REntityMapping.Joins"/> when there is one, or way 5's;
    /// <see cref="REntityMapping.TypeJoins"/> when the navigation leads to an end with several types, or there are several, as from an end
    /// over an abstract TPC root). Does nothing in a database with no such relation.
    /// </summary>
    private void BuildTypeJoins()
    {
        var stored = _relationPhysical.Where(p => p.Value is { TypeLinks: not null, Relation: not null })
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToList();
        if (stored.Count == 0)
            return;

        // Paths read finished tables and keys only: computed per relation in parallel, committed in relation order.
        RowsByTable(); // built once here, read by the parallel loop
        var computed = new List<(RNavigation Navigation, List<TypeJoinPaths.PairPath> Paths, RJoinPath? Shared, IReadOnlyList<string> Keys)>[stored.Count];
        _run.ForEach(stored.Count, i => computed[i] = RelationTypeJoins(stored[i]));

        for (var i = 0; i < stored.Count; i++)
        {
            var storageKeys = _storageKeys.GetValueOrDefault(stored[i].Relation!) ?? _run.DepsOf(stored[i].Relation!).ToList();
            foreach (var (navigation, pairs, shared, rowKeys) in computed[i])
            {
                // The paths carry what the relation's storage keys on and the files that pick a target's rows (section 19.8, "Dependency
                // keys"), so only a unit that reads them records those keys: the navigation and the entities that hold it keep their own.
                var keys = rowKeys.Count == 0 ? storageKeys : new DependencySet(_run.Keys).AddRange(storageKeys).AddRange(rowKeys).ToList();
                foreach (var pair in pairs)
                    pair.Path.Keys = keys;
                if (shared is not null)
                    shared.Keys = keys;
                var paths = new KeyedList<RJoinPath>([.. pairs.Select(p => p.Path)], keys);
                _run.SetTypeJoins(navigation, _db.Name, paths);
                // Exactly one path: way 5's, or a single pair's (decision 48).
                if ((shared ?? (paths.Count == 1 ? paths[0] : null)) is { } one)
                    _run.SetJoin(navigation, _db.Name, one);
                AddEntityTypeJoins(stored[i].TypeLinks!, navigation, pairs, shared, keys);
            }
        }
    }

    private List<(RNavigation Navigation, List<TypeJoinPaths.PairPath> Paths, RJoinPath? Shared, IReadOnlyList<string> Keys)> RelationTypeJoins(RelationPhysical phys)
    {
        var rel = phys.Relation!;
        var links = phys.TypeLinks!;
        var constants = phys.Mapping?.Constants ?? [];
        var result = new List<(RNavigation, List<TypeJoinPaths.PairPath>, RJoinPath?, IReadOnlyList<string>)>();
        foreach (var from in rel.Ends)
        {
            foreach (var to in rel.Ends)
            {
                if (ReferenceEquals(from, to) || NavigationOf(rel, from, to) is not { } navigation)
                    continue;
                var keys = new DependencySet(_run.Keys);
                // Rows are picked at the to end only: its targets, with what that end covers.
                var entry = _typeStorage.First(x => ReferenceEquals(x.Relation, rel));
                var index = entry.Ends.ToList().FindIndex(e => ReferenceEquals(e.End, to));
                var (at, covered) = (entry.Ends[index], entry.Covered[index]);
                var paths = TypeJoinPaths.Of(links, from, to, constants, (target, table) => RowsOf(at, covered, target, table, keys));
                var shared = TypeJoinPaths.Shared(links, from, to, constants, (target, table) => RowsOf(at, covered, target, table, keys));
                result.Add((navigation, paths, shared, keys.ToList()));
            }
        }

        return result;
    }

    /// <summary>The navigation of a relation from one end to another: listed on the from end's entity, or on each of its types.</summary>
    private RNavigation? NavigationOf(RRelation rel, REnd from, REnd to)
    {
        var holder = from.Entity ?? (from.Types.Count > 0 ? from.Types[0] : null);
        return holder is null ? null
            : _run.NavigationsOf(holder.Id).FirstOrDefault(n => ReferenceEquals(n.Relation, rel) && ReferenceEquals(n.From, from) && ReferenceEquals(n.To, to));
    }

    /// <summary>
    /// For each entity with an entity mapping that holds the navigation: the paths from the target that stands for it (itself, else its nearest
    /// base among the from end's targets).
    /// </summary>
    private void AddEntityTypeJoins(TypeLinks links, RNavigation navigation, List<TypeJoinPaths.PairPath> pairs, RJoinPath? shared,
        IReadOnlyList<string> keys)
    {
        var fromEnd = links.Ends.FirstOrDefault(e => ReferenceEquals(e.End, navigation.From));
        if (fromEnd is null || pairs.Count == 0)
            return;
        foreach (var (entityId, _) in _mappingByEntity.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!_placements.TryGetValue(entityId, out var placement)
                || !_run.InheritedNavigations(entityId).Any(n => ReferenceEquals(n, navigation)))
                continue;
            var target = placement;
            while (target is not null && !fromEnd.Targets.Contains(target))
                target = target.Base;
            if (target is null)
                continue;
            var own = pairs.Where(p => ReferenceEquals(p.From, target)).Select(p => p.Path).ToList();
            if (own.Count == 0)
                continue;
            // The entity's own path when there is one: its single pair's, or way 5's one path to the shared table.
            if ((own.Count == 1 ? own[0] : shared) is { } one)
                Map(_joinsByEntity, entityId).TryAdd(navigation.Name, one);
            if (navigation.To.HasSeveralTypes || own.Count > 1)
                Map(_typeJoinsByEntity, entityId).TryAdd(navigation.Name, new KeyedList<RJoinPath>(own, keys));
        }

        static SortedDictionary<string, TValue> Map<TValue>(Dictionary<string, SortedDictionary<string, TValue>> byEntity, string entityId)
        {
            if (!byEntity.TryGetValue(entityId, out var map))
            {
                map = new SortedDictionary<string, TValue>(StringComparer.Ordinal);
                byEntity[entityId] = map;
            }

            return map;
        }
    }

    /// <summary>
    /// What picks <paramref name="target"/>'s rows in <paramref name="table"/> (decision 48), when the table holds rows of an entity the target
    /// doesn't stand for (<see cref="StandsFor(TypeLinkEnd, HashSet{string}, Placement)"/>: itself and its covered derived entities whose rows
    /// are in its table): see <see cref="PickRows"/>. Empty when the table holds the rows of those entities only, or when nothing picks them
    /// (MQ4118 then reports <c>rows-not-told-apart</c>).
    /// </summary>
    private IReadOnlyList<RColumnCondition> RowsOf(TypeLinkEnd end, HashSet<string> covered, Placement target, RTable table, DependencySet keys)
    {
        if (!RowsByTable().TryGetValue(table, out var placed) || !placed.Exists(p => !ReferenceEquals(p, target) && !p.Entity.IsAbstract))
            return [];
        var stands = StandsFor(end, covered, target);
        if (!placed.Exists(p => !p.Entity.IsAbstract && !stands.Contains(p)))
            return [];
        return PickRows(target, table, stands, keys) ?? [];
    }

    /// <summary>
    /// The conditions that pick the rows of the entities a target stands for in a table that also holds other rows (decision 48): the TPH
    /// discriminator holding their codes; else their bindings' constants, read from their files and resolved against the table by name or
    /// key, as a binding does. One entity's constants are each a condition (a constant whose column isn't in the table gives none: MQ4048 or
    /// MQ4049 reports it). Several entities each set one constant, on the same column, which then holds the union of their values. Otherwise
    /// <see langword="null"/>: nothing picks the rows (MQ4118 <c>rows-not-told-apart</c>).
    /// </summary>
    private List<RColumnCondition>? PickRows(Placement target, RTable table, List<Placement> stands, DependencySet? keys)
    {
        var rows = stands.Where(p => !p.Entity.IsAbstract).ToList();
        if (!target.Bound && _discriminators.TryGetValue(table.Key, out var discriminator))
        {
            var values = new List<object?>();
            foreach (var p in rows)
            {
                values.Add(ResolutionValues.Plain(p.Mapping?.DiscriminatorValue) ?? p.Entity.Name);
                keys?.Element(p.Entity.Id);
                if (p.Mapping is { } mapping)
                    keys?.Element(mapping.Id);
            }

            return values.Count == 0 ? [] : [new RColumnCondition { Column = discriminator, Values = [.. values.Distinct().OrderBy(Text, StringComparer.Ordinal)] }];
        }

        if (rows.Count == 0)
            return [];
        var each = new List<List<RColumnCondition>>(rows.Count);
        foreach (var p in rows)
        {
            if (!p.ViaBinding || !_bindingByEntity.TryGetValue(p.Entity.Id, out var bound) || bound.Binding.Constants.Count == 0)
                return null;
            keys?.Element(p.Entity.Id);
            var conditions = new List<RColumnCondition>();
            foreach (var constant in bound.Binding.Constants)
            {
                if (BindingBuilder.FindTableColumn(table, constant.Column) is { } column)
                    conditions.Add(new RColumnCondition { Column = column, Values = [ResolutionValues.Plain(constant.Value)] });
            }

            if (rows.Count > 1 && bound.Binding.Constants.Count != 1)
                return null;
            each.Add(conditions);
        }

        if (each.Count == 1)
            return each[0];
        if (each[0] is not [var first] || !each.TrueForAll(c => c is [var only] && ReferenceEquals(only.Column, first.Column)))
            return null;
        return [new RColumnCondition { Column = first.Column, Values = [.. each.SelectMany(c => c[0].Values).Distinct().OrderBy(Text, StringComparer.Ordinal)] }];

        static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private Dictionary<RTable, List<Placement>> RowsByTable()
    {
        if (_rowsByTable is { } built)
            return built;
        var map = new Dictionary<RTable, List<Placement>>(ReferenceEqualityComparer.Instance);
        foreach (var p in _placementOrder)
        {
            if (p.Table is null)
                continue;
            foreach (var t in TablesOf(p))
            {
                if (!map.TryGetValue(t.Table, out var list))
                {
                    list = [];
                    map[t.Table] = list;
                }

                list.Add(p);
            }
        }

        _rowsByTable = map;
        return map;
    }

    /// <summary>Publishes each entity mapping's <see cref="REntityMapping.TypeJoins"/>.</summary>
    private void PublishEntityTypeJoins()
    {
        foreach (var (entityId, joins) in _typeJoinsByEntity)
        {
            if (_mappingByEntity.TryGetValue(entityId, out var mapping))
                mapping.TypeJoins = joins.ToImmutableSortedDictionary(StringComparer.Ordinal);
        }
    }
}
