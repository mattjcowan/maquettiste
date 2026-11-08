using System.Runtime.CompilerServices;
using System.Text.Json;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>One lifecycle an <c>in_state</c> reads on an object: the navigation names from the subject to the object, and the lifecycle's id.</summary>
/// <param name="Path">The navigation names, empty for the subject itself.</param>
/// <param name="Lifecycle">The lifecycle's (process's) id.</param>
internal sealed record StateRead(IReadOnlyList<string> Path, string Lifecycle)
{
    /// <summary>The read's key among a run's notes.</summary>
    public string Key => ObjectStates.Key(Path, Lifecycle);
}

/// <summary>
/// The states of a run's objects in the lifecycles <c>in_state</c> reads (object-effects-design.md decision 10 and section 7): each object of
/// the subject's row holds, under <see cref="Member"/>, the ids of its active states in each lifecycle a condition or set reads at its path.
/// The states come from the object's <c>states</c>, else from the state its bound value maps to, else from its bound attribute's default,
/// then from the lifecycle's initial state; the last two are notes in the trace.
/// </summary>
internal static class ObjectStates
{
    /// <summary>The row member that holds an object's states by lifecycle id; no attribute can be named so.</summary>
    internal const string Member = "$states";

    private static readonly ConditionalWeakTable<ModelSnapshot, ConditionalWeakTable<Process, IReadOnlyList<StateRead>>> ReadsByModel = new();

    /// <summary>The key of a read among a run's notes.</summary>
    /// <param name="path">The navigation names.</param>
    /// <param name="lifecycle">The lifecycle id.</param>
    /// <returns>The key.</returns>
    internal static string Key(IEnumerable<string> path, string lifecycle) => string.Join(".", path) + "|" + lifecycle;

    /// <summary>
    /// The lifecycles a process's guards and sets read on an object through <c>in_state</c> (not the running chart), each once, in document
    /// order; empty when none does, so the rows of such a process stay as they were. Typed once per model.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>The reads.</returns>
    internal static IReadOnlyList<StateRead> Reads(ModelSnapshot model, Process process)
    {
        if (process.Guards.All(g => g.Condition is null) && !ExprSites.SetsData(process) && !ExprSites.TriggerData(process) && !ExprSites.ApprovalData(process))
            return [];
        return ReadsByModel.GetValue(model, static _ => new()).GetValue(process, p =>
        {
            var reads = new List<StateRead>();
            foreach (var id in ExprSites.ConditionIds(p))
            {
                if (ExprResolution.Type(model, p, id) is not { } typed)
                    continue;
                foreach (var read in ReadsOf(typed.Typed))
                {
                    if (!reads.Any(r => r.Key == read.Key))
                        reads.Add(read);
                }
            }

            return reads;
        });
    }

    /// <summary>The lifecycles one typed tree reads on an object through <c>in_state</c>, in tree order.</summary>
    /// <param name="typed">The typed tree.</param>
    /// <returns>The reads.</returns>
    internal static IReadOnlyList<StateRead> ReadsOf(TypedExpr typed)
    {
        var reads = new List<StateRead>();
        Walk(typed);
        return reads;

        void Walk(TypedExpr t)
        {
            if (t.Node.InState is not null && !t.Running && t.Lifecycle is { } lifecycle)
                reads.Add(new StateRead([.. t.Through.Select(n => n.Name)], lifecycle));
            if (t.Body is { } body)
                Walk(body);
            foreach (var arg in t.Args)
                Walk(arg);
        }
    }

    /// <summary>
    /// An object's active states in a lifecycle (decision 10), with the note the trace gives when they were assumed: its <c>states</c> of the
    /// lifecycle; else the state its value of the lifecycle's bound attribute maps to; else the state the bound attribute's default maps to,
    /// then the lifecycle's initial state, each with a note.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="entity">The object's entity, or <see langword="null"/> when unknown.</param>
    /// <param name="obj">The object, or <see langword="null"/> when the run has none.</param>
    /// <returns>The active state ids in document order, and the note.</returns>
    internal static (IReadOnlyList<string> States, string? Note) Of(ModelSnapshot model, Process lifecycle, Entity? entity, ScenarioObject? obj)
    {
        var chart = ProcessLibraries.Chart(model, lifecycle);
        var given = obj?.States.Where(chart.ById.ContainsKey).ToList() ?? [];
        if (given.Count > 0)
            return (Configuration(chart, given), null);
        var who = obj is null ? "The run has no object, so it" : $"Object '{obj.Name}'";
        if (entity is not null && BoundAttribute(model, lifecycle, entity) is { } bound)
        {
            if (obj is not null && obj.Values.TryGetValue(bound.Id, out var value) && Mapped(model, lifecycle, bound, value) is { } state)
                return (Configuration(chart, [state.Id]), null);
            if (bound.Default is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } fallback && Mapped(model, lifecycle, bound, fallback) is { } byDefault)
            {
                var states = Configuration(chart, [byDefault.Id]);
                return (states, $"{who} gives no state in '{lifecycle.Name}'; in_state reads {Paths(chart, states)}, the state the default of '{bound.Name}' maps to.");
            }
        }

        var initial = Configuration(chart, []);
        return (initial, $"{who} gives no state in '{lifecycle.Name}'; in_state reads its initial state {Paths(chart, initial)}.");
    }

    /// <summary>
    /// A lifecycle's active configuration from some states: each with its ancestors, then, down from the root, each compound state's initial
    /// child when none of its children is given and each region of a parallel state. With no state given, the lifecycle's initial
    /// configuration. Ids in document order.
    /// </summary>
    /// <param name="chart">The lifecycle's chart.</param>
    /// <param name="given">The state ids given.</param>
    /// <returns>The active state ids.</returns>
    internal static IReadOnlyList<string> Configuration(StatechartModel chart, IEnumerable<string> given)
    {
        var active = new HashSet<ChartState>(ReferenceEqualityComparer.Instance);
        foreach (var id in given)
        {
            if (!chart.ById.TryGetValue(id, out var state) || state.Type is StateType.History or StateType.Choice)
                continue;
            for (var s = state; s is { IsRoot: false }; s = s.Parent)
                active.Add(s);
        }

        Complete(chart.Root, 0);
        return [.. active.OrderBy(s => s.Order).Select(s => s.Id)];

        void Complete(ChartState s, int depth)
        {
            if (s.Children.IsEmpty || depth > 64)
                return;
            if (s.Type == StateType.Parallel && !s.IsRoot)
            {
                foreach (var region in s.Children.Where(r => r.Type is not (StateType.History or StateType.Choice)))
                {
                    active.Add(region);
                    Complete(region, depth + 1);
                }

                return;
            }

            var child = s.Children.FirstOrDefault(active.Contains);
            if (child is null)
            {
                child = s.InitialChild;
                if (child is null || child.Type is StateType.History or StateType.Choice)
                    return;
                active.Add(child);
            }

            Complete(child, depth + 1);
        }
    }

    /// <summary>The attribute of an entity, its bases or a stereotype it carries, that binds a lifecycle, or <see langword="null"/>.</summary>
    /// <param name="model">The model.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The bound attribute.</returns>
    internal static ModelAttribute? BoundAttribute(ModelSnapshot model, Process lifecycle, Entity entity)
    {
        var held = new Dictionary<string, ModelAttribute>(StringComparer.Ordinal);
        foreach (var attribute in ScenarioObjects.Attributes(model, entity).Concat(ExprSites.ProcessAttributesOf(model, entity)))
            held.TryAdd(attribute.Id, attribute);
        foreach (var entry in lifecycle.Subjects)
        {
            if (entry.BoundAttribute is { } id && held.TryGetValue(id, out var bound))
                return bound;
        }

        return null;
    }

    /// <summary>
    /// The state a bound attribute's value maps to (4.1 decisions 4 and 14): under an enum binding, the root-level state named as the member
    /// the value names (by its code, else its name); under a code binding whose codes form a map, the state with that code. <see langword="null"/>
    /// when it maps to none.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="attribute">The bound attribute.</param>
    /// <param name="value">The value.</param>
    /// <returns>The state.</returns>
    internal static ProcessState? Mapped(ModelSnapshot model, Process lifecycle, ModelAttribute attribute, JsonElement value)
    {
        var binding = LifecycleBindings.Classify(model, attribute);
        if (binding.Kind == BindingKind.Enum && binding.Enum is { } enumType)
        {
            var member = value.ValueKind switch
            {
                JsonValueKind.String => enumType.Members.FirstOrDefault(m => string.Equals(m.Code, value.GetString(), StringComparison.Ordinal))
                    ?? enumType.Members.FirstOrDefault(m => string.Equals(m.Name, value.GetString(), StringComparison.Ordinal)),
                JsonValueKind.Number when value.TryGetInt64(out var n) => enumType.Members.FirstOrDefault(m => m.Value == n),
                _ => null,
            };
            // An enum holds a root-level state, as the resolved in-state codes and the packs read it: a nested state of the same name is
            // never the member's.
            return member is null ? null : lifecycle.States.FirstOrDefault(s => string.Equals(s.Name, member.Name, StringComparison.Ordinal)
                && s.Type is not (StateType.History or StateType.Choice));
        }

        if (!binding.NeedsCodes || ReferenceDataIndex.CodeKey(value) is not { } key)
            return null;
        var codes = ProcessCodes.Of(lifecycle);
        return codes.IsMap
            ? codes.Coded.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.Ordinal) && c.State.Type is not (StateType.History or StateType.Choice))?.State
            : null;
    }

    // The atomic states of a configuration by path, for notes.
    private static string Paths(StatechartModel chart, IReadOnlyList<string> states)
    {
        var leaves = states.Select(id => chart.ById[id]).Where(s => s.IsLeaf).Select(s => "'" + s.Path + "'").ToList();
        return leaves.Count == 0 ? "no state" : string.Join(" and ", leaves);
    }
}
