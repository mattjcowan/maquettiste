using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Model;

/// <summary>
/// One part of a whole record (parts-and-copy-design.md): a composition whose owner end covers the record's entity (or the entity of the part
/// above it), reached at <see cref="Depth"/> under <see cref="Parent"/>.
/// </summary>
/// <param name="Relation">The composition.</param>
/// <param name="OwnerEnd">The index of the owner end, the one with max 1 (<c>ValidationContext.CompositionEnds</c>).</param>
/// <param name="PartEnd">The index of the part end, whose entity is the part's.</param>
/// <param name="EntityId">The part's entity: the part end's one entity.</param>
/// <param name="OwnerId">The entity that owns the part here: the record's entity, or the entity of the part above.</param>
/// <param name="Parent">The index of the part above in <see cref="PartTree.Nodes"/>, or -1 for a part of the record itself.</param>
/// <param name="Depth">1 for a part of the record itself, 2 for a part of a part, and so on.</param>
/// <param name="Recursive">
/// Whether the part's entity is already on the way down from the record (a part of its own kind): its parts are not listed again, and its rows
/// point at rows of the same kind.
/// </param>
internal sealed record PartNode(Relation Relation, int OwnerEnd, int PartEnd, string EntityId, string OwnerId, int Parent, int Depth, bool Recursive)
{
    /// <summary>The part end.</summary>
    public RelationEnd End => Relation.Ends[PartEnd];

    /// <summary>The owner end.</summary>
    public RelationEnd Owner => Relation.Ends[OwnerEnd];
}

/// <summary>
/// The parts of a whole record of one entity at every level down (parts-and-copy-design.md), the one place that works them out, so the resolver
/// and the rules read the same tree.
/// <para>
/// A part is a composition whose owner end covers the entity: it names the entity or a base of it, or it has several types and covers it. A
/// composition whose part end has several types is left out, with what lies below it. The parts of each owner come in relation name, then id,
/// order; the tree is walked depth first, and a part whose entity is already on the way down is marked <see cref="PartNode.Recursive"/> and not
/// walked again.
/// </para>
/// <para>
/// <see cref="Order"/> is safe for foreign keys: every part after its owner, and a part after the parts its to-one links point at (for a
/// two-ended relation between two parts' entities, both ends of one type: the end with max 1, else the one with min 1, else the first end, is
/// the one pointed at; a many-to-many link points at neither; a composition is never a link, since the walk already orders it). When links go
/// round in a circle, the parts of the circle go together, in walk order, once nothing outside the circle holds them back, which never puts a
/// part before its owner nor a part that only points at a circle before it. Among the parts or circles free to go, the one with the earliest part
/// in walk order goes first. Each part on a circle is marked in <see cref="InCycle"/>, a circle through the record itself included. Deleting
/// deepest first is the order reversed. The work is linear in the parts and the links between their entities.
/// </para>
/// <para>
/// A tree stops at <see cref="MaxNodes"/> parts (<see cref="Truncated"/>); only a model with composition errors (MQ3016) or one that size gets there.
/// </para>
/// </summary>
internal sealed class PartTree
{
    /// <summary>The most parts a tree lists; a model past it has composition errors (MQ3016) that multiply the ways down.</summary>
    public const int MaxNodes = 10_000;

    private static readonly ConditionalWeakTable<ModelSnapshot, Index> Indexes = new();

    private static readonly ConditionalWeakTable<ModelSnapshot, ConcurrentDictionary<string, PartTree>> Trees = new();

    private static readonly PartTree None = new([], [], [], false);

    private PartTree(IReadOnlyList<PartNode> nodes, IReadOnlyList<int> order, IReadOnlyList<bool> inCycle, bool truncated)
    {
        Nodes = nodes;
        Order = order;
        InCycle = inCycle;
        Truncated = truncated;
    }

    /// <summary>Whether the walk stopped at <see cref="MaxNodes"/> parts with more to list, so some parts are left out.</summary>
    public bool Truncated { get; }

    /// <summary>The parts in walk order: depth first, each owner's parts by relation name, then id.</summary>
    public IReadOnlyList<PartNode> Nodes { get; }

    /// <summary>The indexes of <see cref="Nodes"/> in an order safe for foreign keys.</summary>
    public IReadOnlyList<int> Order { get; }

    /// <summary>By index of <see cref="Nodes"/>: whether the part's to-one links go round in a circle with other parts or the record.</summary>
    public IReadOnlyList<bool> InCycle { get; }

    /// <summary>Whether the model has a composition whose part end has one type.</summary>
    /// <param name="model">The model.</param>
    /// <returns>Whether any record can have parts.</returns>
    public static bool Any(ModelSnapshot model) => IndexOf(model).Any;

    /// <summary>The parts of a record of an entity, worked out once per snapshot and entity.</summary>
    /// <param name="model">The model.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The tree; empty when the entity has no parts.</returns>
    public static PartTree Of(ModelSnapshot model, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(entity);
        var index = IndexOf(model);
        if (!index.Any)
            return None;
        return Trees.GetValue(model, static _ => new ConcurrentDictionary<string, PartTree>(StringComparer.Ordinal))
            .GetOrAdd(entity.Id, static (_, state) => Build(state.model, state.index, state.entity), (model, index, entity));
    }

    /// <summary>Whether a composition's part end with one type is reached among the parts.</summary>
    /// <param name="endId">The end id.</param>
    /// <returns>Whether a part is reached through it.</returns>
    public bool Reaches(string endId) => Nodes.Any(n => string.Equals(n.End.Id, endId, StringComparison.Ordinal));

    /// <summary>The ids of the relations whose ends a part tree of an entity reads: those with an end at the entity or a base, by entity id.</summary>
    /// <param name="model">The model.</param>
    /// <param name="entityId">The entity id.</param>
    /// <returns>The relations, in model order.</returns>
    public static IReadOnlyList<Relation> RelationsAt(ModelSnapshot model, string entityId) =>
        IndexOf(model).Touching.GetValueOrDefault(entityId) ?? [];

    /// <summary>A relation end by id, with its relation; <see langword="null"/> when the id names no relation end.</summary>
    /// <param name="model">The model.</param>
    /// <param name="endId">The end id.</param>
    /// <returns>The relation and the end, or <see langword="null"/>.</returns>
    public static (Relation Relation, RelationEnd End)? EndOf(ModelSnapshot model, string endId)
    {
        if (!model.TryGetEntry(endId, out var entry) || entry.Kind != "end" || model.Get<Relation>(entry.OwnerId) is not { } relation)
            return null;
        foreach (var end in relation.Ends)
        {
            if (string.Equals(end.Id, endId, StringComparison.Ordinal))
                return (relation, end);
        }

        return null;
    }

    /// <summary>The key a copy part entry's choice condition is found by among the entity's expression sites.</summary>
    /// <param name="endId">The entry's end id.</param>
    /// <returns>The key.</returns>
    public static string ChoiceKey(string endId) => "copy:" + endId;

    private static Index IndexOf(ModelSnapshot model) => Indexes.GetValue(model, static m => new Index(m));

    private static PartTree Build(ModelSnapshot model, Index index, Entity root)
    {
        var nodes = new List<PartNode>();
        var path = new List<string> { root.Id };
        var truncated = false;
        Walk(model, index, root, -1, 1, path, nodes, ref truncated);
        if (nodes.Count == 0)
            return None;

        // The graph: parts 0..n-1, the record n, and one hub per link relation: a part a to-one link points at reaches the hub, and the hub the
        // parts that hold the link. A hub stands for every pair of those parts, so the graph grows with the parts and links, not their product.
        var n = nodes.Count;
        var chains = new List<string>[n + 1];
        for (var i = 0; i < n; i++)
            chains[i] = model.Get<Entity>(nodes[i].EntityId) is { } e ? SubjectCoverage.Chain(model, e) : [nodes[i].EntityId];
        chains[n] = SubjectCoverage.Chain(model, root);

        // The nodes whose entity is, or derives from, each entity: the nodes a one-type end at that entity covers.
        var covered = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i <= n; i++)
        {
            foreach (var id in chains[i])
            {
                if (!covered.TryGetValue(id, out var list))
                    covered[id] = list = [];
                list.Add(i);
            }
        }

        var edges = new List<List<int>>(n + 1);
        for (var i = 0; i <= n; i++)
            edges.Add([]);
        for (var i = 0; i < n; i++)
            edges[nodes[i].Parent < 0 ? n : nodes[i].Parent].Add(i);
        var links = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in covered.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var relation in index.Touching.GetValueOrDefault(id) ?? [])
            {
                // A composition is a tree edge, never a link: a part of its own kind gets no edge back to its owner.
                if (relation.RelationKind == RelationKind.Composition || !links.Add(relation.Id) || Pointed(relation) is not { } pointed)
                    continue;
                var (principal, dependent) = pointed;
                if (!covered.TryGetValue(principal.SingleEntity!, out var targets) || !covered.TryGetValue(dependent.SingleEntity!, out var holders))
                    continue;
                var hub = edges.Count;
                edges.Add([.. holders]);
                foreach (var t in targets)
                    edges[t].Add(hub);
            }
        }

        // A circle is a group of two or more parts (the record counting as one), a hub aside: a part that points at its own kind meets itself only
        // through the hub. The order works on the parts alone, with the record placed before them.
        var all = Components(edges, _ => true);
        var members = new int[all.Count];
        for (var v = 0; v <= n; v++)
            members[all.Group[v]]++;
        var inCycle = new bool[n];
        for (var i = 0; i < n; i++)
            inCycle[i] = members[all.Group[i]] > 1;
        return new PartTree(nodes, SortedOrder(n, edges), inCycle, truncated);
    }

    private static void Walk(ModelSnapshot model, Index index, Entity owner, int parent, int depth, List<string> path, List<PartNode> nodes, ref bool truncated)
    {
        foreach (var (relation, ownerEnd, partEnd) in index.PartsOf(model, owner))
        {
            if (nodes.Count >= MaxNodes)
            {
                truncated = true; // a part is left out
                return;
            }

            var entityId = relation.Ends[partEnd].SingleEntity!;
            var recursive = path.Contains(entityId, StringComparer.Ordinal);
            nodes.Add(new PartNode(relation, ownerEnd, partEnd, entityId, owner.Id, parent, depth, recursive));
            if (recursive || model.Get<Entity>(entityId) is not { } part)
                continue;
            path.Add(entityId);
            Walk(model, index, part, nodes.Count - 1, depth + 1, path, nodes, ref truncated);
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>
    /// The end pointed at and the end that points, for a two-ended relation whose ends both have one type: the end with max 1 when only one
    /// has it, else the one with min 1 when only one has it, else the first; <see langword="null"/> for a many-to-many relation.
    /// </summary>
    private static (RelationEnd Principal, RelationEnd Dependent)? Pointed(Relation relation)
    {
        var (e0, e1) = (relation.Ends[0], relation.Ends[1]);
        var (many0, many1) = (e0.Max == MaxCardinality.Many, e1.Max == MaxCardinality.Many);
        if (many0 && many1)
            return null;
        if (many0 != many1)
            return many0 ? (e1, e0) : (e0, e1);
        if ((e0.Min == 1) != (e1.Min == 1))
            return e0.Min == 1 ? (e0, e1) : (e1, e0);
        return (e0, e1);
    }

    /// <summary>
    /// The parts in an order safe for foreign keys, in time linear in the graph: the strongly connected groups of the graph without the record,
    /// in an order safe for the edges between groups, the group holding the earliest part in walk order first among those free to go; the parts
    /// of a group together, in walk order. A part's owner is in an earlier group or in its own, where it comes earlier in walk order, so it is
    /// placed first; a part that only points at a circle comes after the whole circle.
    /// </summary>
    private static int[] SortedOrder(int n, List<List<int>> edges)
    {
        var groups = Components(edges, v => v != n);
        var first = new int[groups.Count];
        Array.Fill(first, int.MaxValue);
        var parts = new List<int>[groups.Count];
        for (var g = 0; g < groups.Count; g++)
            parts[g] = [];
        for (var i = 0; i < n; i++)
        {
            var g = groups.Group[i];
            parts[g].Add(i); // in walk order, since i rises
            first[g] = Math.Min(first[g], i);
        }

        // A group of hubs alone places nothing: it goes as soon as it is free, so it never holds back the parts behind it.
        for (var g = 0; g < groups.Count; g++)
        {
            if (parts[g].Count == 0)
                first[g] = -1;
        }

        var waiting = new int[groups.Count];
        var next = new List<int>[groups.Count];
        for (var g = 0; g < groups.Count; g++)
            next[g] = [];
        for (var v = 0; v < edges.Count; v++)
        {
            if (v == n)
                continue;
            foreach (var w in edges[v])
            {
                if (w == n || groups.Group[w] == groups.Group[v])
                    continue;
                next[groups.Group[v]].Add(groups.Group[w]);
                waiting[groups.Group[w]]++;
            }
        }

        // Groups free to go, the one with the earliest part first.
        var ready = new SortedSet<(int First, int Group)>();
        for (var g = 0; g < groups.Count; g++)
        {
            if (waiting[g] == 0)
                ready.Add((first[g], g));
        }

        var order = new List<int>(n);
        while (ready.Count > 0)
        {
            var (_, g) = ready.Min;
            ready.Remove(ready.Min);
            order.AddRange(parts[g]);
            foreach (var h in next[g])
            {
                if (--waiting[h] == 0)
                    ready.Add((first[h], h));
            }
        }

        return [.. order];
    }

    /// <summary>
    /// The strongly connected groups of the nodes <paramref name="included"/> keeps, over the edges between them, by an iterative Tarjan walk in
    /// time linear in the graph: each node's group (-1 when left out) and the number of groups.
    /// </summary>
    private static (int[] Group, int Count) Components(List<List<int>> edges, Func<int, bool> included)
    {
        var count = edges.Count;
        var index = new int[count];
        var low = new int[count];
        var group = new int[count];
        var onStack = new bool[count];
        Array.Fill(index, -1);
        Array.Fill(group, -1);
        var stack = new Stack<int>();
        var walk = new Stack<(int Node, int At)>();
        var counter = 0;
        var groups = 0;
        for (var start = 0; start < count; start++)
        {
            if (index[start] >= 0 || !included(start))
                continue;
            walk.Push((start, 0));
            index[start] = low[start] = counter++;
            stack.Push(start);
            onStack[start] = true;
            while (walk.Count > 0)
            {
                var (v, at) = walk.Pop();
                if (at < edges[v].Count)
                {
                    walk.Push((v, at + 1));
                    var w = edges[v][at];
                    if (!included(w))
                        continue;
                    if (index[w] < 0)
                    {
                        index[w] = low[w] = counter++;
                        stack.Push(w);
                        onStack[w] = true;
                        walk.Push((w, 0));
                    }
                    else if (onStack[w])
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }

                    continue;
                }

                if (low[v] == index[v])
                {
                    int w;
                    do
                    {
                        w = stack.Pop();
                        onStack[w] = false;
                        group[w] = groups;
                    }
                    while (w != v);
                    groups++;
                }

                if (walk.Count > 0)
                {
                    var parent = walk.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[v]);
                }
            }
        }

        return (group, groups);
    }

    /// <summary>
    /// The model's compositions by owner (a one-type owner end by its entity; an owner end with several types in a list of its own), and its
    /// two-ended relations whose ends both have one type, by each end's entity. Built once per snapshot.
    /// </summary>
    private sealed class Index
    {
        private readonly FrozenDictionary<string, List<(Relation Relation, int Owner, int Part)>> _byOwner;

        private readonly List<(Relation Relation, int Owner, int Part)> _severalOwners = [];

        public Index(ModelSnapshot model)
        {
            var byOwner = new Dictionary<string, List<(Relation, int, int)>>(StringComparer.Ordinal);
            var touching = new Dictionary<string, List<Relation>>(StringComparer.Ordinal);
            foreach (var relation in model.All<Relation>())
            {
                if (relation.Ends.Count != 2 || relation.RelationKind == RelationKind.NAry)
                    continue;
                if (relation.Ends.All(e => e.SingleEntity is not null))
                {
                    foreach (var entity in relation.Ends.Select(e => e.SingleEntity!).Distinct(StringComparer.Ordinal))
                    {
                        if (!touching.TryGetValue(entity, out var list))
                            touching[entity] = list = [];
                        list.Add(relation);
                    }
                }

                if (Validation.ValidationContext.CompositionEnds(relation) is not { } ends || relation.Ends[ends.Child].SingleEntity is not { } part
                    || model.Get<Entity>(part) is null)
                    continue;
                Any = true;
                if (relation.Ends[ends.Owner].SingleEntity is { } owner)
                {
                    if (!byOwner.TryGetValue(owner, out var list))
                        byOwner[owner] = list = [];
                    list.Add((relation, ends.Owner, ends.Child));
                }
                else
                {
                    _severalOwners.Add((relation, ends.Owner, ends.Child));
                }
            }

            _byOwner = byOwner.ToFrozenDictionary(StringComparer.Ordinal);
            Touching = touching.ToFrozenDictionary(StringComparer.Ordinal);
        }

        public bool Any { get; }

        /// <summary>Two-ended relations whose ends both have one type, by each end's entity, in model order.</summary>
        public FrozenDictionary<string, List<Relation>> Touching { get; }

        /// <summary>The compositions that make parts of an entity's records, by relation name, then id.</summary>
        public List<(Relation Relation, int Owner, int Part)> PartsOf(ModelSnapshot model, Entity entity)
        {
            var found = new List<(Relation Relation, int Owner, int Part)>();
            foreach (var id in SubjectCoverage.Chain(model, entity))
                found.AddRange(_byOwner.GetValueOrDefault(id) ?? []);
            foreach (var several in _severalOwners)
            {
                if (EndCoverage.Covers(model, several.Relation.Ends[several.Owner], entity.Id))
                    found.Add(several);
            }

            found.Sort(static (a, b) => string.CompareOrdinal(a.Relation.Name, b.Relation.Name) is var c and not 0 ? c : string.CompareOrdinal(a.Relation.Id, b.Relation.Id));
            return found;
        }
    }
}
