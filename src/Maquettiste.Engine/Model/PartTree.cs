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
/// round in a circle, a part on the circle goes next: the earliest in walk order of a circle that waits on nothing else, which never puts a part
/// before its owner nor a part that only points at a circle before it. Each part on a circle is marked in <see cref="InCycle"/>, a circle through
/// the record itself included. Deleting deepest first is the order reversed.
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

    /// <summary>Whether the walk stopped at <see cref="MaxNodes"/> parts, so parts further down are not listed.</summary>
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
        Walk(model, index, root, -1, 1, path, nodes);
        if (nodes.Count == 0)
            return None;

        // The edges: an owner before its part (the record is node n), and the part a to-one link points at before the part that holds it.
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

        var successors = new HashSet<int>[n + 1];
        for (var i = 0; i <= n; i++)
            successors[i] = [];
        for (var i = 0; i < n; i++)
            successors[nodes[i].Parent < 0 ? n : nodes[i].Parent].Add(i);
        foreach (var (id, holders) in covered)
        {
            foreach (var relation in index.Touching.GetValueOrDefault(id) ?? [])
            {
                // A composition is a tree edge, never a link: a part of its own kind gets no edge back to its owner.
                if (relation.RelationKind == RelationKind.Composition || Pointed(relation) is not { } pointed)
                    continue;
                var (principal, dependent) = pointed;
                if (!string.Equals(dependent.SingleEntity, id, StringComparison.Ordinal) || !covered.TryGetValue(principal.SingleEntity!, out var targets))
                    continue;
                foreach (var j in holders)
                {
                    foreach (var i in targets)
                    {
                        if (i != j)
                            successors[i].Add(j);
                    }
                }
            }
        }

        var (group, sizes) = Groups(n + 1, static _ => true, successors);
        var inCycle = new bool[n];
        for (var i = 0; i < n; i++)
            inCycle[i] = sizes[group[i]] > 1;
        return new PartTree(nodes, SortedOrder(n, successors), inCycle, nodes.Count >= MaxNodes);
    }

    private static void Walk(ModelSnapshot model, Index index, Entity owner, int parent, int depth, List<string> path, List<PartNode> nodes)
    {
        foreach (var (relation, ownerEnd, partEnd) in index.PartsOf(model, owner))
        {
            if (nodes.Count >= MaxNodes)
                return;
            var entityId = relation.Ends[partEnd].SingleEntity!;
            var recursive = path.Contains(entityId, StringComparer.Ordinal);
            nodes.Add(new PartNode(relation, ownerEnd, partEnd, entityId, owner.Id, parent, depth, recursive));
            if (recursive || model.Get<Entity>(entityId) is not { } part)
                continue;
            path.Add(entityId);
            Walk(model, index, part, nodes.Count - 1, depth + 1, path, nodes);
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
    /// A stable Kahn sort of the parts; the record, node <paramref name="n"/>, is placed before them all, so its edges count for nothing. When
    /// every part left waits, the parts left hold a circle that waits on nothing else: a group with no edge in from the other parts left, which
    /// <see cref="Released"/> finds.
    /// </summary>
    private static int[] SortedOrder(int n, HashSet<int>[] successors)
    {
        var waiting = new int[n];
        for (var i = 0; i < n; i++)
        {
            foreach (var s in successors[i])
            {
                if (s < n)
                    waiting[s]++;
            }
        }

        var ready = new SortedSet<int>();
        var left = new SortedSet<int>(Enumerable.Range(0, n));
        for (var i = 0; i < n; i++)
        {
            if (waiting[i] == 0)
                ready.Add(i);
        }

        var order = new int[n];
        for (var k = 0; k < n; k++)
        {
            var next = ready.Count > 0 ? ready.Min : Released(n, successors, left);
            ready.Remove(next);
            left.Remove(next);
            order[k] = next;
            foreach (var s in successors[next])
            {
                if (s < n && left.Contains(s) && --waiting[s] == 0)
                    ready.Add(s);
            }
        }

        return order;
    }

    /// <summary>
    /// The part a circle releases when every part left waits: of the groups of parts left with no edge in from another group left, the earliest
    /// part in walk order. Every part left waits, so such a group is a circle; its earliest part's owner comes earlier in walk order and so is
    /// placed already or in the group, where it would be earlier still. A part that only points at a circle is never released before it.
    /// </summary>
    private static int Released(int n, HashSet<int>[] successors, SortedSet<int> left)
    {
        var (group, _) = Groups(n, left.Contains, successors);
        var fed = new HashSet<int>();
        foreach (var i in left)
        {
            foreach (var s in successors[i])
            {
                if (s < n && left.Contains(s) && group[s] != group[i])
                    fed.Add(group[s]);
            }
        }

        foreach (var i in left)
        {
            if (!fed.Contains(group[i]))
                return i;
        }

        return left.Min;
    }

    /// <summary>
    /// The strongly connected groups of the nodes below <paramref name="count"/> that <paramref name="included"/> keeps, over the edges between
    /// them, by an iterative Kosaraju walk: each node's group (-1 when left out) and each group's size.
    /// </summary>
    private static (int[] Group, List<int> Sizes) Groups(int count, Func<int, bool> included, HashSet<int>[] successors)
    {
        var predecessors = new List<int>[count];
        var next = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            predecessors[i] = [];
            next[i] = [];
        }

        for (var i = 0; i < count; i++)
        {
            if (!included(i))
                continue;
            foreach (var s in successors[i].Order())
            {
                if (s < count && included(s))
                {
                    next[i].Add(s);
                    predecessors[s].Add(i);
                }
            }
        }

        // First pass: finishing order.
        var visited = new bool[count];
        var finished = new List<int>(count);
        for (var start = 0; start < count; start++)
        {
            if (visited[start] || !included(start))
                continue;
            var stack = new Stack<(int Node, int At)>();
            visited[start] = true;
            stack.Push((start, 0));
            while (stack.Count > 0)
            {
                var (node, at) = stack.Pop();
                if (at < next[node].Count)
                {
                    stack.Push((node, at + 1));
                    var s = next[node][at];
                    if (!visited[s])
                    {
                        visited[s] = true;
                        stack.Push((s, 0));
                    }
                }
                else
                {
                    finished.Add(node);
                }
            }
        }

        // Second pass: groups on the reversed edges, in reverse finishing order.
        var group = new int[count];
        Array.Fill(group, -1);
        var sizes = new List<int>();
        for (var k = finished.Count - 1; k >= 0; k--)
        {
            var start = finished[k];
            if (group[start] >= 0)
                continue;
            var id = sizes.Count;
            sizes.Add(0);
            var pending = new Stack<int>();
            pending.Push(start);
            group[start] = id;
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                sizes[id]++;
                foreach (var p in predecessors[node])
                {
                    if (group[p] < 0)
                    {
                        group[p] = id;
                        pending.Push(p);
                    }
                }
            }
        }

        return (group, sizes);
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
