using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>A state of a process with its place in the state tree.</summary>
/// <param name="State">The state.</param>
/// <param name="Parent">The parent state, or <see langword="null"/> for a child of the (implicit) root.</param>
/// <param name="Index">The position in document order (pre-order over the tree).</param>
/// <param name="Pointer">The JSON pointer of the state in the process file.</param>
/// <param name="Path">The dotted name path from the root (<c>Fulfilment.Shipping.Packed</c>).</param>
internal sealed record StateNode(ProcessState State, StateNode? Parent, int Index, string Pointer, string Path)
{
    /// <summary>The state id.</summary>
    public string Id => State.Id;

    /// <summary>The state type.</summary>
    public StateType Type => State.Type;
}

/// <summary>The transitions of one source, trigger and event (or duration, or invoke), in priority order.</summary>
/// <param name="Source">The source state id.</param>
/// <param name="Trigger">The trigger.</param>
/// <param name="Key">The event id, the duration, the invoke id, or the empty string for <c>done</c> and <c>always</c>.</param>
/// <param name="Transitions">The indexes of the transitions in <see cref="Process.Transitions"/>, in array order.</param>
internal sealed record TransitionGroup(string Source, TransitionTrigger Trigger, string Key, ImmutableArray<int> Transitions);

/// <summary>
/// The static analysis of one process's statechart (phase-3-design.md section 3): the state tree, reachability from the initial
/// configuration (entry through initial children and parallel regions, transitions, history default targets, <c>done</c>,
/// <c>always</c> and invoke completion triggers), completion (which states can reach a final child), the transition groups the overlap
/// rule reads, and the eventless cycles. Guards are ignored: a guard can only remove paths. No interpreter runs; the result is a
/// deterministic function of the process record.
/// </summary>
internal sealed class ProcessAnalysis
{
    private readonly Dictionary<string, StateNode> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableArray<StateNode>> _children = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reached = new(StringComparer.Ordinal);
    private readonly Lazy<ImmutableArray<TransitionGroup>> _groups;
    private const string RootKey = "";

    /// <summary>Analyses a process.</summary>
    /// <param name="process">The process.</param>
    public ProcessAnalysis(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        Process = process;
        var all = new List<StateNode>();
        _children[RootKey] = Build(process.States, null, "", all);
        States = [.. all];
        ComputeReachability();
        Reachable = _reached.ToFrozenSet(StringComparer.Ordinal);
        _groups = new(ComputeGroups);
    }

    /// <summary>The analysed process.</summary>
    public Process Process { get; }

    /// <summary>Every state, in document order (pre-order).</summary>
    public ImmutableArray<StateNode> States { get; }

    /// <summary>The ids of the states some path from the initial configuration enters.</summary>
    public FrozenSet<string> Reachable { get; }

    /// <summary>The transition groups, ordered by their first transition.</summary>
    public ImmutableArray<TransitionGroup> Groups => _groups.Value;

    /// <summary>Returns a state of this process by id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The state, or <see langword="null"/> when the id is not a state of this process.</returns>
    public StateNode? Find(string? id) => id is not null && _byId.TryGetValue(id, out var node) ? node : null;

    /// <summary>Returns the children of a state, or of the root for <see langword="null"/>.</summary>
    /// <param name="node">The state, or <see langword="null"/> for the root.</param>
    /// <returns>The children in document order.</returns>
    public ImmutableArray<StateNode> ChildrenOf(StateNode? node) => _children[node?.Id ?? RootKey];

    /// <summary>The declared initial child of a compound state or of the root (<see langword="null"/>).</summary>
    /// <param name="node">The state, or <see langword="null"/> for the root.</param>
    /// <returns>The declared id, or <see langword="null"/> when absent.</returns>
    public string? DeclaredInitial(StateNode? node) => node is null ? Process.Initial : node.State.Initial;

    /// <summary>
    /// The initial child a compound state (or the root) enters: the declared one when it is a direct child, the first child otherwise.
    /// </summary>
    /// <param name="node">The state, or <see langword="null"/> for the root.</param>
    /// <returns>The child, or <see langword="null"/> when there are no children.</returns>
    public StateNode? InitialOf(StateNode? node)
    {
        var children = ChildrenOf(node);
        if (children.Length == 0)
            return null;
        var declared = Find(DeclaredInitial(node));
        return declared is not null && ReferenceEquals(declared.Parent, node) ? declared : children[0];
    }

    /// <summary>Whether <paramref name="node"/> is a strict descendant of <paramref name="ancestor"/> (every state descends from the root).</summary>
    /// <param name="node">The state.</param>
    /// <param name="ancestor">The ancestor, or <see langword="null"/> for the root.</param>
    /// <returns><see langword="true"/> when it descends from it.</returns>
    public static bool IsDescendant(StateNode node, StateNode? ancestor)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (ancestor is null)
            return true;
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, ancestor))
                return true;
        }

        return false;
    }

    /// <summary>The ancestors of a state, nearest first (the root is not included).</summary>
    /// <param name="node">The state.</param>
    /// <returns>The ancestors.</returns>
    public static IEnumerable<StateNode> Ancestors(StateNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        for (var p = node.Parent; p is not null; p = p.Parent)
            yield return p;
    }

    /// <summary>The nearest common strict ancestor of two distinct states that is not one of them, or <see langword="null"/> for the root.</summary>
    /// <param name="a">A state.</param>
    /// <param name="b">Another state.</param>
    /// <returns>The common ancestor.</returns>
    public static StateNode? CommonAncestor(StateNode a, StateNode b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        foreach (var candidate in Ancestors(a))
        {
            if (IsDescendant(b, candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>Every final descendant of a state, or of the root for <see langword="null"/>, in document order.</summary>
    /// <param name="node">The state, or <see langword="null"/> for the root.</param>
    /// <returns>The final states.</returns>
    public IEnumerable<StateNode> FinalDescendants(StateNode? node) =>
        States.Where(s => s.Type == StateType.Final && !ReferenceEquals(s, node) && IsDescendant(s, node));

    /// <summary>
    /// Whether a state can complete (so a <c>done</c> transition on it can fire): a compound state (or the root) when a final direct
    /// child is reachable, a parallel state when every region can complete, a final state when it is reachable.
    /// </summary>
    /// <param name="node">The state, or <see langword="null"/> for the root.</param>
    /// <returns><see langword="true"/> when it can complete.</returns>
    public bool Completes(StateNode? node) => Completes(node, _reached);

    /// <summary>
    /// The states entering <paramref name="target"/> activates, statically: the target, its ancestors, the other regions of each
    /// parallel ancestor (at their initial), and below the target its initial children, every region of a parallel state and a
    /// history state's default target.
    /// </summary>
    /// <param name="target">The entered state.</param>
    /// <returns>The entered states, in visiting order.</returns>
    public IReadOnlyList<StateNode> EntryClosure(StateNode target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<StateNode>();
        Enter(target, seen, list);
        return list;
    }

    /// <summary>
    /// The cycles of eventless (<c>always</c>) unguarded transitions: each is a set of states, in document order, from which the
    /// macrostep never ends, with the first transition (in priority order) that closes it.
    /// </summary>
    /// <returns>The cycles, ordered by their first state.</returns>
    public IReadOnlyList<(ImmutableArray<StateNode> States, int Transition)> EventlessCycles()
    {
        var transitions = Process.Transitions;
        var eventless = new List<int>();
        for (var i = 0; i < transitions.Count; i++)
        {
            if (transitions[i].Trigger == TransitionTrigger.Always && transitions[i].Guard is null && Find(transitions[i].Source) is not null)
                eventless.Add(i);
        }

        if (eventless.Count == 0)
            return [];

        // An active state enables the eventless transitions of itself and of its ancestors.
        var edges = new Dictionary<string, List<(string To, int Transition)>>(StringComparer.Ordinal);
        foreach (var state in States)
        {
            var list = new List<(string, int)>();
            foreach (var i in eventless)
            {
                var source = Find(transitions[i].Source)!;
                if (!ReferenceEquals(source, state) && !IsDescendant(state, source))
                    continue;
                var targets = transitions[i].Targets.Select(Find).OfType<StateNode>().ToList();
                if (transitions[i].Targets.Count == 0)
                    list.Add((state.Id, i)); // targetless: stays enabled in the same configuration
                foreach (var target in targets)
                {
                    foreach (var entered in EntryClosure(target))
                        list.Add((entered.Id, i));
                }
            }

            edges[state.Id] = list;
        }

        var cycles = new List<(ImmutableArray<StateNode>, int)>();
        foreach (var component in StronglyConnected(edges))
        {
            var members = component.ToHashSet(StringComparer.Ordinal);
            var closing = int.MaxValue;
            foreach (var id in component)
            {
                foreach (var (to, t) in edges[id])
                {
                    if (members.Contains(to))
                        closing = Math.Min(closing, t);
                }
            }

            if (closing == int.MaxValue)
                continue; // a single state without a self edge
            cycles.Add(([.. component.Select(id => _byId[id]).OrderBy(n => n.Index)], closing));
        }

        return [.. cycles.OrderBy(c => c.Item1[0].Index)];
    }

    private ImmutableArray<StateNode> Build(IReadOnlyList<ProcessState> states, StateNode? parent, string pointer, List<StateNode> all)
    {
        var builder = ImmutableArray.CreateBuilder<StateNode>(states.Count);
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var node = new StateNode(state, parent, all.Count, pointer + "/states/" + i.ToString(CultureInfo.InvariantCulture),
                parent is null ? state.Name : parent.Path + "." + state.Name);
            all.Add(node);
            builder.Add(node);
            _byId.TryAdd(state.Id, node); // a duplicate id is MQ1004's
            _children[state.Id] = Build(state.States, node, node.Pointer, all);
        }

        return builder.MoveToImmutable();
    }

    private void Enter(StateNode target, HashSet<string> seen, List<StateNode> entered)
    {
        // The ancestors become active; a parallel ancestor also enters its other regions.
        var below = target;
        foreach (var ancestor in Ancestors(target))
        {
            if (seen.Add(ancestor.Id))
                entered.Add(ancestor);
            if (ancestor.Type == StateType.Parallel)
            {
                foreach (var region in ChildrenOf(ancestor))
                {
                    if (!ReferenceEquals(region, below))
                        EnterDown(region, seen, entered);
                }
            }

            below = ancestor;
        }

        EnterDown(target, seen, entered);
    }

    private void EnterDown(StateNode node, HashSet<string> seen, List<StateNode> entered)
    {
        if (!seen.Add(node.Id))
            return;
        entered.Add(node);
        switch (node.Type)
        {
            case StateType.Compound when InitialOf(node) is { } initial:
                EnterDown(initial, seen, entered);
                break;
            case StateType.Parallel:
                foreach (var region in ChildrenOf(node))
                    EnterDown(region, seen, entered);
                break;
            case StateType.History:
                // Without a recorded history the default target (or the parent's initial) is entered; a recorded configuration
                // only re-enters states that were reached before.
                var fallback = Find(node.State.DefaultTarget) ?? InitialOf(node.Parent);
                if (fallback is not null && !ReferenceEquals(fallback, node))
                    Enter(fallback, seen, entered);
                break;
        }
    }

    private void ComputeReachability()
    {
        if (InitialOf(null) is not { } initial)
            return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scratch = new List<StateNode>();
        Enter(initial, seen, scratch);
        _reached.UnionWith(seen);

        var transitions = Process.Transitions;
        var fired = new bool[transitions.Count];
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < transitions.Count; i++)
            {
                var t = transitions[i];
                if (fired[i] || Find(t.Source) is not { } source || !_reached.Contains(source.Id))
                    continue;
                if (t.Trigger == TransitionTrigger.Done && !Completes(source, _reached))
                    continue;
                fired[i] = true;
                changed = true;
                foreach (var target in t.Targets.Select(Find).OfType<StateNode>())
                {
                    var closure = new HashSet<string>(StringComparer.Ordinal);
                    Enter(target, closure, scratch);
                    _reached.UnionWith(closure);
                }
            }
        }
        while (changed);
    }

    private bool Completes(StateNode? node, HashSet<string> reached)
    {
        if (node is null)
            return ChildrenOf(null).Any(c => c.Type == StateType.Final && reached.Contains(c.Id));
        return node.Type switch
        {
            StateType.Compound => ChildrenOf(node).Any(c => c.Type == StateType.Final && reached.Contains(c.Id)),
            StateType.Parallel => ChildrenOf(node).Length > 0 && ChildrenOf(node).All(r => Completes(r, reached)),
            StateType.Final => reached.Contains(node.Id),
            _ => false,
        };
    }

    private ImmutableArray<TransitionGroup> ComputeGroups()
    {
        var order = new List<(string Source, TransitionTrigger Trigger, string Key)>();
        var members = new Dictionary<(string, TransitionTrigger, string), ImmutableArray<int>.Builder>();
        for (var i = 0; i < Process.Transitions.Count; i++)
        {
            var t = Process.Transitions[i];
            var key = (t.Source, t.Trigger, KeyOf(t));
            if (!members.TryGetValue(key, out var list))
            {
                members[key] = list = ImmutableArray.CreateBuilder<int>();
                order.Add(key);
            }

            list.Add(i);
        }

        return [.. order.Select(k => new TransitionGroup(k.Source, k.Trigger, k.Key, members[k].ToImmutable()))];
    }

    /// <summary>The group key of a transition: its event, duration or invoke, or the empty string.</summary>
    /// <param name="transition">The transition.</param>
    /// <returns>The key.</returns>
    public static string KeyOf(ProcessTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return transition.Trigger switch
        {
            TransitionTrigger.Event => transition.Event ?? "",
            TransitionTrigger.After => transition.After ?? "",
            TransitionTrigger.InvokeDone or TransitionTrigger.InvokeError => transition.Invoke ?? "",
            _ => "",
        };
    }

    // Tarjan's algorithm, iterative over the states in document order so the result is deterministic.
    private List<List<string>> StronglyConnected(Dictionary<string, List<(string To, int Transition)>> edges)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var next = 0;
        foreach (var start in States)
        {
            if (index.ContainsKey(start.Id))
                continue;
            var work = new Stack<(string Node, int Edge)>();
            work.Push((start.Id, 0));
            index[start.Id] = low[start.Id] = next++;
            stack.Push(start.Id);
            onStack.Add(start.Id);
            while (work.Count > 0)
            {
                var (node, edge) = work.Pop();
                var outgoing = edges[node];
                if (edge < outgoing.Count)
                {
                    work.Push((node, edge + 1));
                    var to = outgoing[edge].To;
                    if (!index.ContainsKey(to))
                    {
                        index[to] = low[to] = next++;
                        stack.Push(to);
                        onStack.Add(to);
                        work.Push((to, 0));
                    }
                    else if (onStack.Contains(to))
                    {
                        low[node] = Math.Min(low[node], index[to]);
                    }

                    continue;
                }

                if (low[node] == index[node])
                {
                    var component = new List<string>();
                    string popped;
                    do
                    {
                        popped = stack.Pop();
                        onStack.Remove(popped);
                        component.Add(popped);
                    }
                    while (popped != node);
                    result.Add(component);
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
            }
        }

        return result;
    }
}
