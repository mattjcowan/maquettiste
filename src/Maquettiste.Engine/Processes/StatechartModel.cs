using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>A state of a resolved chart (phase-3-design.md section 4.1), or the implicit root.</summary>
public sealed class ChartState
{
    internal ChartState(ProcessState? state, string id, string name, StateType type, ChartState? parent, int order, string path)
    {
        State = state;
        Id = id;
        Name = name;
        Type = type;
        Parent = parent;
        Order = order;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Path = path;
        Ancestors = parent is null ? [] : [parent, .. parent.Ancestors];
    }

    /// <summary>The state record; <see langword="null"/> for the root.</summary>
    public ProcessState? State { get; }

    /// <summary>The state id; the process id for the root.</summary>
    public string Id { get; }

    /// <summary>The state name; the process name for the root.</summary>
    public string Name { get; }

    /// <summary>The state type; the root is compound.</summary>
    public StateType Type { get; }

    /// <summary>The parent; <see langword="null"/> for the root.</summary>
    public ChartState? Parent { get; }

    /// <summary>The position in document order (pre-order); -1 for the root.</summary>
    public int Order { get; }

    /// <summary>The depth: 0 for the root, 1 for its children.</summary>
    public int Depth { get; }

    /// <summary>The dotted name path from the root.</summary>
    public string Path { get; }

    /// <summary>The proper ancestors, nearest first, ending with the root.</summary>
    public ImmutableArray<ChartState> Ancestors { get; }

    /// <summary>The children in document order (the regions of a parallel state).</summary>
    public ImmutableArray<ChartState> Children { get; internal set; } = [];

    /// <summary>The initial child of a compound state or of the root: <c>initial</c>, or the first child.</summary>
    public ChartState? InitialChild { get; internal set; }

    /// <summary>A history state's default target: <c>defaultTarget</c>, or the parent's initial child.</summary>
    public ChartState? DefaultTarget { get; internal set; }

    /// <summary>The outgoing transitions in priority order.</summary>
    public ImmutableArray<ChartTransition> Transitions { get; internal set; } = [];

    /// <summary>The <c>after</c> transitions whose timers start on entry.</summary>
    public ImmutableArray<ChartTransition> Timers { get; internal set; } = [];

    /// <summary>The invokes started on entry.</summary>
    public IReadOnlyList<ProcessInvoke> Invokes => State?.Invoke ?? [];

    /// <summary>Whether this is the implicit root.</summary>
    public bool IsRoot => Parent is null;

    /// <summary>Whether the state has no children to enter (atomic, final, choice, or an empty container).</summary>
    public bool IsLeaf => Children.IsEmpty && Type != StateType.History;

    /// <summary>Whether <paramref name="ancestor"/> is a proper ancestor of this state.</summary>
    /// <param name="ancestor">The candidate ancestor.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool IsDescendantOf(ChartState ancestor)
    {
        for (var p = Parent; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, ancestor))
                return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => Path;
}

/// <summary>A transition of a resolved chart with its references resolved.</summary>
public sealed class ChartTransition
{
    internal ChartTransition(ProcessTransition transition, int index, ChartState source, ImmutableArray<ChartState> targets, TimeSpan? delay,
        ProcessEvent? @event, ProcessGuard? guard, ImmutableArray<ProcessAction> actions)
    {
        Transition = transition;
        Index = index;
        Source = source;
        Targets = targets;
        Delay = delay;
        Event = @event;
        Guard = guard;
        Actions = actions;
    }

    /// <summary>The transition record.</summary>
    public ProcessTransition Transition { get; }

    /// <summary>The transition id.</summary>
    public string Id => Transition.Id;

    /// <summary>The position in the process's transitions (priority).</summary>
    public int Index { get; }

    /// <summary>The source state.</summary>
    public ChartState Source { get; }

    /// <summary>The targets that resolve to states of this process.</summary>
    public ImmutableArray<ChartState> Targets { get; }

    /// <summary>The delay of an <c>after</c> transition.</summary>
    public TimeSpan? Delay { get; }

    /// <summary>The event of an <c>event</c> transition.</summary>
    public ProcessEvent? Event { get; }

    /// <summary>The guard, when set and declared.</summary>
    public ProcessGuard? Guard { get; }

    /// <summary>The declared actions, in list order.</summary>
    public ImmutableArray<ProcessAction> Actions { get; }

    /// <summary>The trigger.</summary>
    public TransitionTrigger Trigger => Transition.Trigger;

    /// <summary>The signature gate.</summary>
    public ProcessGate? Gate => Transition.Gate;

    /// <summary>Whether the transition exits and re-enters its source when a target is its descendant.</summary>
    public bool External => Transition.External;
}

/// <summary>
/// A process's statechart resolved once for the interpreter (phase-3-design.md section 4.1): states with document order, depth,
/// ancestors, regions, initial children and history defaults; transitions grouped by source, trigger and key; timers, invokes and
/// gates. Built once per process and cached by the process hash. Dangling references are skipped (validation reports them).
/// </summary>
public sealed partial class StatechartModel
{
    private const int CacheLimit = 512;
    private static readonly ConcurrentDictionary<string, StatechartModel> Cache = new(StringComparer.Ordinal);

    private readonly FrozenDictionary<(string Source, TransitionTrigger Trigger, string Key), ImmutableArray<ChartTransition>> _groups;

    private StatechartModel(Process process)
    {
        Process = process;
        var root = new ChartState(null, process.Id, process.Name, StateType.Compound, null, -1, "");
        var all = new List<ChartState>();
        var byId = new Dictionary<string, ChartState>(StringComparer.Ordinal);
        root.Children = Build(process.States, root, all, byId);
        Root = root;
        States = [.. all];
        ById = byId.ToFrozenDictionary(StringComparer.Ordinal);
        root.InitialChild = Initial(process.Initial, root);
        foreach (var s in all)
        {
            if (s.Type == StateType.Compound)
                s.InitialChild = Initial(s.State!.Initial, s);
        }

        foreach (var s in all)
        {
            if (s.Type == StateType.History && s.Parent is { } parent)
                s.DefaultTarget = s.State!.DefaultTarget is { } d && ById.TryGetValue(d, out var target) ? target : parent.InitialChild;
        }

        Events = Distinct(process.Events.Select(e => (e.Id, e)));
        Guards = Distinct(process.Guards.Select(g => (g.Id, g)));
        Actions = Distinct(process.Actions.Select(a => (a.Id, a)));
        Invokes = Distinct(all.SelectMany(s => s.Invokes.Select(i => (i.Id, (i, s)))));
        Attributes = Distinct(process.Context.Select(a => (a.Id, a)));

        var transitions = new List<ChartTransition>();
        var bySource = new Dictionary<ChartState, List<ChartTransition>>();
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var t = process.Transitions[i];
            if (!ById.TryGetValue(t.Source, out var source))
                continue;
            var targets = t.Targets.Select(id => ById.GetValueOrDefault(id)).OfType<ChartState>().ToImmutableArray();
            var delay = t.Trigger == TransitionTrigger.After ? ParseDuration(t.After) : null;
            var chart = new ChartTransition(t, i, source, targets, delay, t.Event is null ? null : Events.GetValueOrDefault(t.Event),
                t.Guard is null ? null : Guards.GetValueOrDefault(t.Guard), [.. t.Actions.Select(a => Actions.GetValueOrDefault(a)).OfType<ProcessAction>()]);
            transitions.Add(chart);
            if (!bySource.TryGetValue(source, out var list))
                bySource[source] = list = [];
            list.Add(chart);
        }

        Transitions = [.. transitions];
        foreach (var (source, list) in bySource)
        {
            source.Transitions = [.. list];
            source.Timers = [.. list.Where(t => t.Trigger == TransitionTrigger.After && t.Delay is not null)];
        }

        _groups = transitions.GroupBy(t => (t.Source.Id, t.Trigger, Key(t)))
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray());
    }

    /// <summary>The process.</summary>
    public Process Process { get; }

    /// <summary>The implicit root compound state.</summary>
    public ChartState Root { get; }

    /// <summary>Every state except the root, in document order.</summary>
    public ImmutableArray<ChartState> States { get; }

    /// <summary>The states by id.</summary>
    public FrozenDictionary<string, ChartState> ById { get; }

    /// <summary>The transitions whose source resolves, in priority order.</summary>
    public ImmutableArray<ChartTransition> Transitions { get; }

    /// <summary>The process's events by id.</summary>
    public FrozenDictionary<string, ProcessEvent> Events { get; }

    /// <summary>The process's guards by id.</summary>
    public FrozenDictionary<string, ProcessGuard> Guards { get; }

    /// <summary>The process's actions by id.</summary>
    public FrozenDictionary<string, ProcessAction> Actions { get; }

    /// <summary>The invokes by id, with the state that starts them.</summary>
    public FrozenDictionary<string, (ProcessInvoke Invoke, ChartState State)> Invokes { get; }

    /// <summary>The context attributes by id.</summary>
    public FrozenDictionary<string, ModelAttribute> Attributes { get; }

    /// <summary>The transitions of one source, trigger and key (event id, invoke id, or empty), in priority order.</summary>
    /// <param name="source">The source state.</param>
    /// <param name="trigger">The trigger.</param>
    /// <param name="key">The event or invoke id; empty for the other triggers.</param>
    /// <returns>The candidates.</returns>
    public ImmutableArray<ChartTransition> Candidates(ChartState source, TransitionTrigger trigger, string key) =>
        _groups.TryGetValue((source.Id, trigger, key), out var list) ? list : [];

    /// <summary>The resolved chart of a process, cached by the process hash when one is given.</summary>
    /// <param name="process">The process.</param>
    /// <param name="hash">The process document's hash, or <see langword="null"/> to build without caching (a draft).</param>
    /// <returns>The chart.</returns>
    public static StatechartModel Get(Process process, string? hash)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (hash is null)
            return new StatechartModel(process);
        var key = process.Id + "|" + hash;
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        if (Cache.Count >= CacheLimit)
            Cache.Clear();
        return Cache.GetOrAdd(key, _ => new StatechartModel(process));
    }

    /// <summary>Converts an ISO 8601 duration to a fixed span: a year is 365 days and a month 30 days, as delays in milliseconds are.</summary>
    /// <param name="value">The duration text.</param>
    /// <returns>The span, or <see langword="null"/> when the text is not a duration.</returns>
    public static TimeSpan? ParseDuration(string? value)
    {
        if (value is null || DurationPattern().Match(value) is not { Success: true } m)
            return null;
        static long Part(Group g) => g.Success ? long.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        var days = Part(m.Groups["y"]) * 365 + Part(m.Groups["mo"]) * 30 + Part(m.Groups["w"]) * 7 + Part(m.Groups["d"]);
        var seconds = m.Groups["s"].Success ? decimal.Parse(m.Groups["s"].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 0m;
        var ticks = (decimal)TimeSpan.FromDays(days).Ticks + TimeSpan.FromHours(Part(m.Groups["h"])).Ticks + TimeSpan.FromMinutes(Part(m.Groups["mi"])).Ticks
            + seconds * TimeSpan.TicksPerSecond;
        return TimeSpan.FromTicks((long)ticks);
    }

    [GeneratedRegex(@"^P(?=\d|T\d)(?:(?<y>\d+)Y)?(?:(?<mo>\d+)M)?(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?(?:T(?=\d)(?:(?<h>\d+)H)?(?:(?<mi>\d+)M)?(?:(?<s>\d+(?:[.,]\d+)?)S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    private static string Key(ChartTransition t) => t.Trigger switch
    {
        TransitionTrigger.Event => t.Transition.Event ?? "",
        TransitionTrigger.InvokeDone or TransitionTrigger.InvokeError => t.Transition.Invoke ?? "",
        _ => "",
    };

    private static FrozenDictionary<string, T> Distinct<T>(IEnumerable<(string Id, T Value)> items)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (id, value) in items)
            map.TryAdd(id, value);
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private ChartState? Initial(string? initial, ChartState holder)
    {
        if (initial is not null && ById.TryGetValue(initial, out var named) && ReferenceEquals(named.Parent, holder))
            return named;
        return holder.Children.IsEmpty ? null : holder.Children[0];
    }

    private static ImmutableArray<ChartState> Build(IReadOnlyList<ProcessState> states, ChartState parent, List<ChartState> all, Dictionary<string, ChartState> byId)
    {
        var children = ImmutableArray.CreateBuilder<ChartState>(states.Count);
        foreach (var s in states)
        {
            var node = new ChartState(s, s.Id, s.Name, s.Type, parent, all.Count, parent.IsRoot ? s.Name : parent.Path + "." + s.Name);
            all.Add(node);
            byId.TryAdd(s.Id, node);
            node.Children = Build(s.States, node, all, byId);
            children.Add(node);
        }

        return children.MoveToImmutable();
    }
}
