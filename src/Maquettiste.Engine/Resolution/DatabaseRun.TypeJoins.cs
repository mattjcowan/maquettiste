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
    /// One pair's path from <paramref name="pf"/>'s target to <paramref name="pt"/>'s: one step when either end's rows hold the link, else two
    /// through the link table; <see langword="null"/> when a pointer didn't resolve.
    /// </summary>
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
    /// <see cref="RNavigation.Joins"/> when there is exactly one), and per entity with an entity mapping, the paths from the target that stands
    /// for it (<see cref="REntityMapping.Joins"/> when there is one, <see cref="REntityMapping.TypeJoins"/> when the navigation leads to an end
    /// with several types or there are several). Does nothing in a database with no such relation.
    /// </summary>
    private void BuildTypeJoins()
    {
        var stored = _relationPhysical.Where(p => p.Value is { TypeLinks: not null, Relation: not null })
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToList();
        if (stored.Count == 0)
            return;

        // Paths read finished tables and keys only: computed per relation in parallel, committed in relation order.
        RowsByTable(); // built once here, read by the parallel loop
        var computed = new List<(RNavigation Navigation, List<TypeJoinPaths.PairPath> Paths, List<string> Keys)>[stored.Count];
        _run.ForEach(stored.Count, i => computed[i] = RelationTypeJoins(stored[i]));

        for (var i = 0; i < stored.Count; i++)
        {
            var relationKeys = _run.DepsOf(stored[i].Relation!).ToList();
            foreach (var (navigation, pairs, keys) in computed[i])
            {
                var paths = pairs.Select(p => p.Path).ToList();
                _run.SetTypeJoins(navigation, _db.Name, paths);
                if (paths.Count == 1)
                    _run.SetJoin(navigation, _db.Name, paths[0]);
                // The paths follow what the relation's storage keys on (the storage run adds those to the relation), and the files that pick
                // a target's rows (section 19.8, "Dependency keys").
                _run.DepsOf(navigation).AddRange(relationKeys).AddRange(keys);
                AddEntityTypeJoins(stored[i].TypeLinks!, navigation, pairs, relationKeys, keys);
            }
        }
    }

    private List<(RNavigation Navigation, List<TypeJoinPaths.PairPath> Paths, List<string> Keys)> RelationTypeJoins(RelationPhysical phys)
    {
        var rel = phys.Relation!;
        var links = phys.TypeLinks!;
        var constants = phys.Mapping?.Constants ?? [];
        var result = new List<(RNavigation, List<TypeJoinPaths.PairPath>, List<string>)>();
        foreach (var from in rel.Ends)
        {
            foreach (var to in rel.Ends)
            {
                if (ReferenceEquals(from, to) || NavigationOf(rel, from, to) is not { } navigation)
                    continue;
                var keys = new DependencySet(_run.Keys);
                var paths = TypeJoinPaths.Of(links, from, to, constants, (target, table) => RowsOf(target, table, keys));
                result.Add((navigation, paths, keys.ToList().ToList()));
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
    private void AddEntityTypeJoins(TypeLinks links, RNavigation navigation, List<TypeJoinPaths.PairPath> pairs, IReadOnlyList<string> relationKeys,
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
            if (own.Count == 1)
                Map(_joinsByEntity, entityId).TryAdd(navigation.Name, own[0]);
            if (navigation.To.HasSeveralTypes || own.Count > 1)
                Map(_typeJoinsByEntity, entityId).TryAdd(navigation.Name, own);
            if (_run.EntityById(entityId) is { } entity)
                _run.DepsOf(entity).AddRange(relationKeys).AddRange(keys);
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
    /// doesn't stand for (itself and its derived entities): its TPH discriminator, else its binding's constants read from its file and resolved
    /// against the table by name or key, as a binding does. Empty when the table holds the target's rows only, or when nothing picks them
    /// (MQ4118 then reports <c>rows-not-told-apart</c>).
    /// </summary>
    private IReadOnlyList<RColumnCondition> RowsOf(Placement target, RTable table, DependencySet keys)
    {
        var rowsByTable = RowsByTable();
        if (!rowsByTable.TryGetValue(table, out var placed))
            return [];
        var stood = new List<Placement>();
        var others = false;
        foreach (var p in placed)
        {
            if (StandsFor(target, p))
                stood.Add(p);
            else if (!p.Entity.IsAbstract)
                others = true;
        }

        if (!others)
            return [];
        if (!target.Bound && _discriminators.TryGetValue(table.Key, out var discriminator))
        {
            var values = new List<object?>();
            foreach (var p in stood.Where(p => !p.Entity.IsAbstract))
            {
                values.Add(ResolutionValues.Plain(p.Mapping?.DiscriminatorValue) ?? p.Entity.Name);
                keys.Element(p.Entity.Id);
                if (p.Mapping is { } mapping)
                    keys.Element(mapping.Id);
            }

            return values.Count == 0 ? [] : [new RColumnCondition { Column = discriminator, Values = [.. values.Distinct().OrderBy(Text, StringComparer.Ordinal)] }];
        }

        if (target.ViaBinding && _bindingByEntity.TryGetValue(target.Entity.Id, out var bound) && bound.Binding.Constants.Count > 0)
        {
            keys.Element(target.Entity.Id);
            var conditions = new List<RColumnCondition>();
            foreach (var constant in bound.Binding.Constants)
            {
                // A constant whose column isn't in the table gives no condition: MQ4048 or MQ4049 reports it.
                if (BindingBuilder.FindTableColumn(table, constant.Column) is { } column)
                    conditions.Add(new RColumnCondition { Column = column, Values = [ResolutionValues.Plain(constant.Value)] });
            }

            return conditions;
        }

        return [];

        static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Whether <paramref name="target"/> stands for <paramref name="p"/>: it is the target or derives from it.</summary>
    private static bool StandsFor(Placement target, Placement p)
    {
        for (var x = p; x is not null; x = x.Base)
        {
            if (ReferenceEquals(x, target))
                return true;
        }

        return false;
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
