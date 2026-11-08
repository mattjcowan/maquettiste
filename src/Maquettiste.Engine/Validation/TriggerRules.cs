using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The trigger point rules (trigger-points-design.md section 6): MQ8101 to MQ8107 on a process's trigger points, and MQ3001 for a trigger
/// point named like another of its lifecycle or like a guard of its process. A process without trigger points gets no finding.
/// </summary>
internal static class TriggerRules
{
    /// <summary>The verbs a replace may take the place of (decision 17).</summary>
    private static readonly HashSet<string> Replaceable = new(StringComparer.Ordinal) { "delete", "disconnect", "copy", "revise" };

    /// <summary>The verbs <c>ends</c> may stand beside: those that can act on a link (decision 10).</summary>
    private static readonly HashSet<string> LinkVerbs = new(StringComparer.Ordinal) { "connect", "disconnect", "read", "modify", "freeze", "thaw" };

    /// <summary>The verbs no input can do yet (MQ8107), unless an event names one.</summary>
    private static readonly HashSet<string> NotYet = new(StringComparer.Ordinal) { "override", "set-state", "claim", "reject", "ignore", "withdraw", "delegate", "route" };

    public static void Check(ValidationContext context, Element element, Report report)
    {
        if (element is not Process { TriggerPoints.Count: > 0 } process)
            return;
        var model = context.Model;
        CheckNames(model, process, report);
        if (process.Use != ProcessUse.Lifecycle)
        {
            for (var i = 0; i < process.TriggerPoints.Count; i++)
                report.Add("MQ8101", $"Trigger point '{process.TriggerPoints[i].Name}' is on {(process.Use == ProcessUse.Library ? "a library" : "an orchestration")}; only lifecycles hold trigger points. Remove it.",
                    Ptr.At("/triggerPoints", i), process.TriggerPoints[i].Id);
            return;
        }

        var governed = Governed(model, process);
        var states = new HashSet<string>(StringComparer.Ordinal);
        AddStates(process.States, states);
        var transitions = process.Transitions.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < process.TriggerPoints.Count; i++)
        {
            var point = process.TriggerPoints[i];
            var at = Ptr.At("/triggerPoints", i);
            CheckShape(model, process, point, at, governed, report);
            CheckPlaces(model, process, point, at, governed, states, transitions, report);
            CheckRuns(process, point, at, report);
        }

        CheckReplaces(model, process, governed, report);
    }

    // ---- MQ3001 ----

    private static void CheckNames(ModelSnapshot model, Process process, Report report)
    {
        var guards = ProcessLibraries.Effective(model, process).Guards.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < process.TriggerPoints.Count; i++)
        {
            var point = process.TriggerPoints[i];
            var at = Ptr.At("/triggerPoints", i);
            if (point.Name.Length == 0)
                continue;
            if (!seen.TryAdd(point.Name, at))
                report.Add("MQ3001", $"The trigger point name '{point.Name}' is already used at {seen[point.Name]} in process '{process.Name}'; rename one of them.", at + "/name", point.Id);
            else if (guards.Contains(point.Name))
                report.Add("MQ3001", $"The trigger point name '{point.Name}' is also a guard's of process '{process.Name}', so a step's assume could name either; rename one of them.",
                    at + "/name", point.Id);
        }
    }

    // ---- MQ8101, MQ8105, MQ8106 ----

    private static void CheckShape(ModelSnapshot model, Process process, ProcessTriggerPoint point, string at, IReadOnlyList<Entity> governed, Report report)
    {
        var name = point.Name;
        if (point.Type == TriggerPointType.Action && point.Timing is null)
            report.Add("MQ8101", $"Action '{name}' has no timing; say before, with-change or after-commit.", at, point.Id);
        if (point.Type != TriggerPointType.Action && point.Timing is not null)
            report.Add("MQ8101", $"Trigger point '{name}' is a {Word(point.Type)}, and only an action has a timing; remove the key.", at + "/timing", point.Id);
        if (point.Type != TriggerPointType.Check && point.Severity is not null)
            report.Add("MQ8101", $"Trigger point '{name}' is a {Word(point.Type)}, and only a check has a severity; remove the key.", at + "/severity", point.Id);
        if (point.Type != TriggerPointType.Check && point.Condition is not null)
            report.Add("MQ8101", $"Trigger point '{name}' is a {Word(point.Type)}, and only a check has a condition; remove the key.", at + "/condition", point.Id);
        if (point.Type == TriggerPointType.Check && point.Sets.Count > 0)
            report.Add("MQ8101", $"Check '{name}' has sets, and a check sets nothing; remove the key.", at + "/sets", point.Id);
        if (point.Outside && !point.TeamCode)
            report.Add("MQ8101", $"Trigger point '{name}' says it reaches outside systems, but its body is not the team's code; remove outside.", at + "/outside", point.Id);
        if (point.Type == TriggerPointType.Action && point.Timing == TriggerTiming.AfterCommit && point.Sets.Count > 0)
            report.Add("MQ8105", $"After-commit action '{name}' has sets: once the change is saved, setting the object would be a new change. Make it a with-change action.",
                at + "/sets", point.Id);
        if (point.Type == TriggerPointType.Action && point.Timing == TriggerTiming.WithChange && point.Outside)
            report.Add("MQ8106", $"With-change action '{name}' reaches outside systems: a rollback cannot undo the outside call. Make it after commit.", at + "/timing", point.Id);
        if (point.Type == TriggerPointType.Action && point.Timing == TriggerTiming.WithChange && point.Sets.Count > 0 && point.Verbs.Contains("delete", StringComparer.Ordinal))
        {
            var replaced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lifecycle in model.All<Process>().Where(p => p.Use == ProcessUse.Lifecycle))
            {
                var theirs = ReferenceEquals(lifecycle, process) ? governed : Governed(model, lifecycle);
                foreach (var replace in lifecycle.TriggerPoints.Where(t => t.Type == TriggerPointType.Replace && t.Verbs.Contains("delete", StringComparer.Ordinal)))
                    replaced.UnionWith(Covers(model, replace, theirs).Select(e => e.Id));
            }

            if (Covers(model, point, governed).Any(e => !replaced.Contains(e.Id)))
                report.Add("MQ8101", $"With-change action '{name}' sets values on delete, but after a built-in delete there is no object to set; add a replace on delete that "
                    + "covers its entities, or remove the sets.", at + "/sets", point.Id);
        }
    }

    // ---- MQ8102, MQ8103 ----

    private static void CheckPlaces(ModelSnapshot model, Process process, ProcessTriggerPoint point, string at, IReadOnlyList<Entity> governed,
        HashSet<string> states, HashSet<string> transitions, Report report)
    {
        var name = point.Name;
        for (var k = 0; k < point.States.Count; k++)
        {
            if (!states.Contains(point.States[k]))
                report.Add("MQ8102", $"Trigger point '{name}' names '{point.States[k]}' in states, which is not a state of lifecycle '{process.Name}'; remove the entry.", Ptr.At(at + "/states", k), point.Id);
        }

        for (var k = 0; k < point.Targets.Count; k++)
        {
            if (!states.Contains(point.Targets[k]))
                report.Add("MQ8102", $"Trigger point '{name}' names '{point.Targets[k]}' in targets, which is not a state of lifecycle '{process.Name}'; remove the entry.", Ptr.At(at + "/targets", k), point.Id);
        }

        for (var k = 0; k < point.Transitions.Count; k++)
        {
            if (!transitions.Contains(point.Transitions[k]))
                report.Add("MQ8102", $"Trigger point '{name}' names '{point.Transitions[k]}' in transitions, which is not a transition of lifecycle '{process.Name}'; remove the entry.",
                    Ptr.At(at + "/transitions", k), point.Id);
        }

        var moves = point.Verbs.All(v => ExprSites.IsMoveVerb(v) || v == "sign" || (!BuiltInVerbs.IsBuiltIn(v) && process.Events.Any(e => e.Verb == v)));
        if (!moves && point.Targets.Count > 0)
            report.Add("MQ8102", $"Trigger point '{name}' names targets beside a verb that makes no move of this lifecycle; targets fit move verbs, sign, and own verbs its events send. Remove them.",
                at + "/targets", point.Id);
        if (!moves && point.Transitions.Count > 0)
            report.Add("MQ8102", $"Trigger point '{name}' names transitions beside a verb that makes no move of this lifecycle; transitions fit move verbs, sign, and own verbs its events send. Remove them.",
                at + "/transitions", point.Id);
        if (point.Attributes.Count > 0 && (point.Ends.Count > 0 || !point.Verbs.All(v => v is "read" or "modify")))
            report.Add("MQ8102", $"Trigger point '{name}' names attributes beside {(point.Ends.Count > 0 ? "ends" : "a verb other than read and modify")}; remove them.", at + "/attributes", point.Id);
        if (point.Ends.Count > 0 && !point.Verbs.All(LinkVerbs.Contains))
            report.Add("MQ8102", $"Trigger point '{name}' names ends beside a verb that cannot act on a link (any but connect, disconnect, read, modify, freeze and thaw); remove them.",
                at + "/ends", point.Id);

        var governedIds = governed.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        for (var k = 0; k < point.Entities.Count; k++)
        {
            if (!governedIds.Contains(point.Entities[k]))
                report.Add("MQ8102", $"Trigger point '{name}' names '{point.Entities[k]}' in entities, which lifecycle '{process.Name}' does not govern; remove the entry.",
                    Ptr.At(at + "/entities", k), point.Id);
        }

        var (attributes, navigations) = ExprSites.TriggerMembers(model, process, point, withBound: true);
        var held = attributes.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var reached = navigations.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        for (var k = 0; k < point.Attributes.Count; k++)
        {
            if (!held.Contains(point.Attributes[k]))
                report.Add("MQ8102", $"Trigger point '{name}' names '{point.Attributes[k]}' in attributes, which not every entity it covers holds under one id; remove the entry.",
                    Ptr.At(at + "/attributes", k), point.Id);
        }

        for (var k = 0; k < point.Ends.Count; k++)
        {
            if (!reached.Contains(point.Ends[k]))
                report.Add("MQ8102", $"Trigger point '{name}' names '{point.Ends[k]}' in ends, which is not an end every entity it covers holds; remove the entry.",
                    Ptr.At(at + "/ends", k), point.Id);
        }

        for (var j = 0; j < point.Sets.Count; j++)
        {
            if (!held.Contains(point.Sets[j].Attribute))
                report.Add("MQ8102", $"Trigger point '{name}' sets '{point.Sets[j].Attribute}', which not every entity it covers holds under one id; remove the set.",
                    Ptr.At(at + "/sets", j) + "/attribute", point.Sets[j].Id);
        }

        if (point.Type == TriggerPointType.Replace)
        {
            for (var k = 0; k < point.Verbs.Count; k++)
            {
                if (!Replaceable.Contains(point.Verbs[k]))
                    report.Add("MQ8103", $"Replace '{name}' names verb '{point.Verbs[k]}', whose effect cannot be replaced; only delete, disconnect, copy and revise allow it. Make it an action.",
                        Ptr.At(at + "/verbs", k), point.Id);
            }
        }
    }

    // ---- MQ8104 ----

    private static void CheckReplaces(ModelSnapshot model, Process process, IReadOnlyList<Entity> governed, Report report)
    {
        if (!process.TriggerPoints.Any(t => t.Type == TriggerPointType.Replace))
            return;
        // A clash across lifecycles is reported once, by the lifecycle that sorts later by name then id, as one within a lifecycle is
        // reported by its later point.
        var others = model.All<Process>().Where(p => p.Use == ProcessUse.Lifecycle && !ReferenceEquals(p, process) && SortsBefore(p, process)
                && p.TriggerPoints.Any(t => t.Type == TriggerPointType.Replace))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
            .SelectMany(p => p.TriggerPoints.Where(t => t.Type == TriggerPointType.Replace).Select(t => (Lifecycle: p, Point: t, Covers: Covers(model, t, Governed(model, p)))))
            .ToList();
        var mine = new List<(ProcessTriggerPoint Point, IReadOnlyList<Entity> Covers)>();
        for (var i = 0; i < process.TriggerPoints.Count; i++)
        {
            var point = process.TriggerPoints[i];
            if (point.Type != TriggerPointType.Replace)
                continue;
            var covers = Covers(model, point, governed);
            var clash = mine.Select(m => (Where: "trigger point '" + m.Point.Name + "' of this lifecycle", m.Point, m.Covers))
                .Concat(others.Select(o => (Where: "trigger point '" + o.Point.Name + "' of lifecycle '" + o.Lifecycle.Name + "'", o.Point, o.Covers)))
                .Select(c => (c.Where, Verb: point.Verbs.FirstOrDefault(v => c.Point.Verbs.Contains(v, StringComparer.Ordinal)),
                    Entity: covers.FirstOrDefault(e => c.Covers.Any(x => string.Equals(x.Id, e.Id, StringComparison.Ordinal)))))
                .FirstOrDefault(c => c.Verb is not null && c.Entity is not null);
            if (clash.Verb is not null)
                report.Add("MQ8104", $"Replace '{point.Name}' and {clash.Where} can both apply to {clash.Verb} on '{clash.Entity!.Name}'; at most one replace applies to a verb on "
                    + "an entity, whatever their states and when. Narrow one with entities.", Ptr.At("/triggerPoints", i), point.Id);
            mine.Add((point, covers));
        }

        static bool SortsBefore(Process a, Process b)
        {
            var byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName < 0 : string.CompareOrdinal(a.Id, b.Id) < 0;
        }
    }

    // ---- MQ8107 ----

    private static void CheckRuns(Process process, ProcessTriggerPoint point, string at, Report report)
    {
        if (point.Verbs.Count == 0 || point.Verbs.Any(CanRun))
            return;
        report.Add("MQ8107", $"Trigger point '{point.Name}' can never run: no event or transition of lifecycle '{process.Name}' does its verbs, or no input can do them yet. Remove it.",
            at + "/verbs", point.Id);

        bool CanRun(string verb)
        {
            if (process.Events.Any(e => e.Verb == verb && (point.Transitions.Count == 0 || process.Transitions.Any(t => t.Event == e.Id && point.Transitions.Contains(t.Id, StringComparer.Ordinal)))))
                return true;
            if (ExprSites.IsMoveVerb(verb))
                return process.Transitions.Any(t => t.Verb == verb && (point.Transitions.Count == 0 || point.Transitions.Contains(t.Id, StringComparer.Ordinal)));
            return !NotYet.Contains(verb);
        }
    }

    // ---- helpers ----

    /// <summary>The entities a lifecycle governs (its subjects, their derived entities and a stereotype's carriers), by name.</summary>
    private static IReadOnlyList<Entity> Governed(ModelSnapshot model, Process lifecycle) =>
        [.. model.All<Entity>().Where(e => SubjectCoverage.Governs(model, lifecycle, e)).OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)];

    /// <summary>The entities a trigger point runs for (decision 12): those its lifecycle governs, narrowed by its entities with their derived entities.</summary>
    internal static IReadOnlyList<Entity> Covers(ModelSnapshot model, ProcessTriggerPoint point, IReadOnlyList<Entity> governed)
    {
        if (point.Entities.Count == 0)
            return governed;
        var named = point.Entities.ToHashSet(StringComparer.Ordinal);
        return [.. governed.Where(e => IsOrDerives(model, e, named))];
    }

    private static bool IsOrDerives(ModelSnapshot model, Entity entity, HashSet<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Entity? e = entity; e is not null && seen.Add(e.Id); e = e.Base is { } b ? model.Get<Entity>(b) : null)
        {
            if (ids.Contains(e.Id))
                return true;
        }

        return false;
    }

    private static void AddStates(IReadOnlyList<ProcessState> states, HashSet<string> ids)
    {
        foreach (var state in states)
        {
            ids.Add(state.Id);
            AddStates(state.States, ids);
        }
    }

    private static string Word(TriggerPointType type) => type switch
    {
        TriggerPointType.Check => "check",
        TriggerPointType.Action => "action",
        _ => "replace",
    };
}
