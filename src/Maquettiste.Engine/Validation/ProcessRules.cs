using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The process rules (phase-3-design.md section 3): MQ90xx structure, MQ91xx actors and gates, MQ92xx lifecycles. Each diagnostic is
/// reported in the file where the fix belongs: a process's own findings on the process, MQ9016 on the diagram, MQ9106 on the actor
/// and the entity side of MQ9201 on the entity. Pure functions of the context; reachability ignores guards.
/// </summary>
internal static partial class ProcessRules
{
    private static readonly string[] SubElementKinds = ["state", "event", "guard", "action", "invoke", "gate", "meaning"];

    /// <summary>Runs the process rules that belong to one element file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the document to validate.</param>
    public static void Validate(ValidationContext context, Report report)
    {
        switch (report.Document.Element)
        {
            case Process process:
                CheckProcess(context, process, report);
                break;
            case Actor actor:
                CheckActor(context, actor, report);
                break;
            case Entity entity:
                CheckEntityLifecycle(context, entity, report);
                break;
            case Diagram diagram:
                CheckDiagram(context, diagram, report);
                break;
        }
    }

    /// <summary>Whether an ISO 8601 duration is well formed and positive (MQ9008).</summary>
    /// <param name="value">The duration text.</param>
    /// <returns><see langword="true"/> for a positive duration such as <c>P2D</c> or <c>PT1H30M</c>.</returns>
    public static bool IsPositiveDuration(string? value)
    {
        if (value is null || !DurationPattern().IsMatch(value))
            return false;
        return value.Any(c => c is >= '1' and <= '9');
    }

    [GeneratedRegex(@"^P(?=\d|T\d)(\d+Y)?(\d+M)?(\d+W)?(\d+D)?(T(?=\d)(\d+H)?(\d+M)?(\d+(?:[.,]\d+)?S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    private static string Article(string word) => word.Length > 0 && "aeiou".Contains(word[0], StringComparison.Ordinal) ? "an" : "a";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Names(IEnumerable<string> names) => string.Join(", ", names);

    private static void CheckProcess(ValidationContext context, Process process, Report report)
    {
        var analysis = new ProcessAnalysis(process);
        var locals = Locals.Of(process);
        CheckStates(context, analysis, locals, report);
        CheckTransitions(context, analysis, locals, report);
        CheckReachability(analysis, report);
        CheckUnused(process, locals, report);
        CheckInvokes(context, process, analysis, report);
        CheckLifecycle(context, process, analysis, report);
        CheckNodeNames(process, report);
        CheckNodeStereotypes(context, process, report);
    }

    // MQ3001 and MQ3018 on the process's sub-elements (section 2.6): states unique among siblings; events, guards, actions,
    // invokes and gates unique within the process, one namespace per kind; gate meanings unique within their gate.
    private static void CheckNodeNames(Process process, Report report)
    {
        CheckSiblings(process.States, "", null);
        Unique("event", process.Events.Select((e, i) => (e.Name, "/events/" + N(i), e.Id)));
        Unique("guard", process.Guards.Select((g, i) => (g.Name, "/guards/" + N(i), g.Id)));
        Unique("action", process.Actions.Select((a, i) => (a.Name, "/actions/" + N(i), a.Id)));
        Unique("invoke", Invokes(process.States, ""));
        var gates = new List<(string, string, string)>();
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            if (process.Transitions[i].Gate is not { } gate)
                continue;
            var at = "/transitions/" + N(i) + "/gate";
            gates.Add((gate.Name, at, gate.Id));
            Unique("meaning", gate.Meanings.Select((m, j) => (m.Name, at + "/meanings/" + N(j), m.Id)), "gate '" + gate.Name + "'");
        }

        Unique("gate", gates);

        void CheckSiblings(IReadOnlyList<ProcessState> states, string pointer, string? parent)
        {
            Unique("state", states.Select((s, i) => (s.Name, pointer + "/states/" + N(i), s.Id)), parent is null ? null : "the children of state '" + parent + "'");
            for (var i = 0; i < states.Count; i++)
                CheckSiblings(states[i].States, pointer + "/states/" + N(i), states[i].Name);
        }

        IEnumerable<(string, string, string)> Invokes(IReadOnlyList<ProcessState> states, string pointer)
        {
            for (var i = 0; i < states.Count; i++)
            {
                var at = pointer + "/states/" + N(i);
                for (var j = 0; j < states[i].Invoke.Count; j++)
                    yield return (states[i].Invoke[j].Name, at + "/invoke/" + N(j), states[i].Invoke[j].Id);
                foreach (var nested in Invokes(states[i].States, at))
                    yield return nested;
            }
        }

        void Unique(string what, IEnumerable<(string Name, string Pointer, string Id)> items, string? scope = null)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, pointer, id) in items)
            {
                if (name.Length == 0)
                    continue;
                BuiltinRules.CheckIdentifier(name, what, pointer + "/name", id, report);
                if (!seen.TryAdd(name, pointer))
                {
                    var where = scope ?? (what == "state" ? "the root states of process '" + process.Name + "'" : "process '" + process.Name + "'");
                    report.Add("MQ3001", $"The {what} name '{name}' is already used at {seen[name]} in {where}; rename one of them.", pointer + "/name", id);
                }
            }
        }
    }

    // MQ2003 and MQ2004 on the marks of states, transitions and events (section 2.6); the process's own marks are checked as
    // every element's are.
    private static void CheckNodeStereotypes(ValidationContext context, Process process, Report report)
    {
        Walk(process.States, "");
        for (var i = 0; i < process.Transitions.Count; i++)
            BuiltinRules.CheckStereotypes(context, process.Transitions[i].Id, process.Transitions[i].Stereotypes, "/transitions/" + N(i), "transition", report);
        for (var i = 0; i < process.Events.Count; i++)
            BuiltinRules.CheckStereotypes(context, process.Events[i].Id, process.Events[i].Stereotypes, "/events/" + N(i), "event", report);

        void Walk(IReadOnlyList<ProcessState> states, string pointer)
        {
            for (var i = 0; i < states.Count; i++)
            {
                var at = pointer + "/states/" + N(i);
                BuiltinRules.CheckStereotypes(context, states[i].Id, states[i].Stereotypes, at, "state", report);
                Walk(states[i].States, at);
            }
        }
    }

    // MQ9001, MQ9002, MQ9004, MQ9010, MQ9011, MQ9012, MQ9017, and MQ9014 on state-held references.
    private static void CheckStates(ValidationContext context, ProcessAnalysis analysis, Locals locals, Report report)
    {
        var process = analysis.Process;
        CheckInitial(analysis, null, "", report);
        foreach (var node in analysis.States)
        {
            var state = node.State;
            var children = analysis.ChildrenOf(node).Length;
            var where = $"State '{node.Path}' of process '{process.Name}'";
            switch (state.Type)
            {
                case StateType.Compound or StateType.Parallel when children == 0:
                    report.Add("MQ9002", $"{where} is {state.Type.ToString().ToLowerInvariant()} but has no child states; add children or make it atomic.", node.Pointer + "/type", state.Id);
                    break;
                case StateType.Atomic or StateType.Final or StateType.History or StateType.Choice when children > 0:
                    report.Add("MQ9002", $"{where} is {TypeName(state.Type)} but has {N(children)} child state(s); make it compound (or parallel), or move the children out.", node.Pointer + "/states", state.Id);
                    break;
            }

            if (state.Type == StateType.Compound)
                CheckInitial(analysis, node, node.Pointer, report);
            else if (state.Initial is not null)
                report.Add("MQ9001", $"{where} is {TypeName(state.Type)} but sets initial; only a compound state has an initial child. Remove initial.", node.Pointer + "/initial", state.Id);

            if (state.Type == StateType.Parallel && children == 1)
                report.Add("MQ9017", $"{where} is parallel with a single region; add a second region or make it compound.", node.Pointer + "/states", state.Id);

            if (state.Type == StateType.History)
                CheckHistory(analysis, node, where, report);

            if (state.Type == StateType.Final)
            {
                var outgoing = process.Transitions.Count(t => string.Equals(t.Source, state.Id, StringComparison.Ordinal));
                if (outgoing > 0)
                    report.Add("MQ9012", $"{where} is final but is the source of {N(outgoing)} transition(s); a final state is never left. Remove them or make the state atomic.", node.Pointer, state.Id);
                if (state.Invoke.Count > 0)
                    report.Add("MQ9012", $"{where} is final but invokes work; a final state starts nothing. Move the invoke to an atomic state.", node.Pointer + "/invoke", state.Id);
            }

            if (state.Type == StateType.Choice)
                CheckChoice(process, node, where, report);

            if (state.Type == StateType.Atomic && analysis.Reachable.Contains(state.Id) && state.Invoke.Count == 0
                && !process.Transitions.Any(t => analysis.Find(t.Source) is { } source && (ReferenceEquals(source, node) || ProcessAnalysis.IsDescendant(node, source))))
            {
                report.Add("MQ9004", $"{where} is a dead end: no transition leaves it or an ancestor and it invokes nothing. Add a transition, or make it final.", node.Pointer, state.Id);
            }

            for (var i = 0; i < state.Entry.Count; i++)
                CheckLocal(context, process, locals.Actions, state.Entry[i], "action", node.Pointer + "/entry/" + N(i), state.Id, report);
            for (var i = 0; i < state.Exit.Count; i++)
                CheckLocal(context, process, locals.Actions, state.Exit[i], "action", node.Pointer + "/exit/" + N(i), state.Id, report);
            if (state.Initial is not null)
                CheckLocal(context, process, locals.States, state.Initial, "state", node.Pointer + "/initial", state.Id, report);
            if (state.DefaultTarget is not null)
                CheckLocal(context, process, locals.States, state.DefaultTarget, "state", node.Pointer + "/defaultTarget", state.Id, report);
        }

        if (process.Initial is not null)
            CheckLocal(context, process, locals.States, process.Initial, "state", "/initial", null, report);
        for (var i = 0; i < process.Actions.Count; i++)
        {
            var action = process.Actions[i];
            for (var j = 0; j < action.Raises.Count; j++)
                CheckLocal(context, process, locals.Events, action.Raises[j], "event", "/actions/" + N(i) + "/raises/" + N(j), action.Id, report);
        }
    }

    private static string TypeName(StateType type) => type switch
    {
        StateType.Atomic => "atomic",
        StateType.Compound => "compound",
        StateType.Parallel => "parallel",
        StateType.Final => "final",
        StateType.History => "a history state",
        _ => "a choice state",
    };

    private static void CheckInitial(ProcessAnalysis analysis, StateNode? node, string pointer, Report report)
    {
        var declared = analysis.DeclaredInitial(node);
        if (declared is null)
            return;
        var target = analysis.Find(declared);
        if (target is null)
            return; // MQ2001, MQ2002 or MQ9014
        if (ReferenceEquals(target.Parent, node))
            return;
        var owner = node is null ? $"Process '{analysis.Process.Name}'" : $"State '{node.Path}' of process '{analysis.Process.Name}'";
        var first = analysis.ChildrenOf(node).FirstOrDefault();
        var fix = first is null ? "Remove initial." : $"Set initial to a direct child such as '{first.State.Name}', or remove it to use the first child.";
        report.Add("MQ9001", $"{owner} has initial '{target.Path}', which is not one of its direct children. {fix}", pointer + "/initial", node?.Id);
    }

    private static void CheckHistory(ProcessAnalysis analysis, StateNode node, string where, Report report)
    {
        var parent = node.Parent;
        if (parent is not null && parent.Type != StateType.Compound)
        {
            report.Add("MQ9011", $"{where} is a history state inside {TypeName(parent.Type)} state '{parent.Path}'; a history state belongs to a compound state. Move it into one.", node.Pointer + "/type", node.Id);
            return;
        }

        if (analysis.Find(node.State.DefaultTarget) is { } target && (ReferenceEquals(target, node) || !ProcessAnalysis.IsDescendant(target, parent)))
        {
            var scope = parent is null ? "the process" : $"'{parent.Path}'";
            report.Add("MQ9011", $"{where} has default target '{target.Path}', which is not a descendant of {scope}. Pick a state inside {scope}, or remove defaultTarget to use its initial.", node.Pointer + "/defaultTarget", node.Id);
        }
    }

    private static void CheckChoice(Process process, StateNode node, string where, Report report)
    {
        var outgoing = new List<int>();
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            if (string.Equals(process.Transitions[i].Source, node.Id, StringComparison.Ordinal))
                outgoing.Add(i);
        }

        if (outgoing.Count == 0)
        {
            report.Add("MQ9010", $"{where} is a choice state with no outgoing transition; add always transitions ending with an unguarded one.", node.Pointer, node.Id);
            return;
        }

        foreach (var i in outgoing)
        {
            if (process.Transitions[i].Trigger != TransitionTrigger.Always)
                report.Add("MQ9010", $"Transition {N(i)} leaves choice state '{node.Path}' with trigger '{TriggerName(process.Transitions[i].Trigger)}'; every transition of a choice state is 'always'.", "/transitions/" + N(i) + "/trigger", process.Transitions[i].Id);
        }

        var last = outgoing[^1];
        if (process.Transitions[last].Guard is not null)
            report.Add("MQ9010", $"The last transition of choice state '{node.Path}' is guarded, so no branch may apply; add an unguarded transition last as the default.", "/transitions/" + N(last) + "/guard", process.Transitions[last].Id);
    }

    private static string TriggerName(TransitionTrigger trigger) => trigger switch
    {
        TransitionTrigger.Event => "event",
        TransitionTrigger.After => "after",
        TransitionTrigger.Done => "done",
        TransitionTrigger.Always => "always",
        TransitionTrigger.InvokeDone => "invoke-done",
        _ => "invoke-error",
    };

    // MQ9006, MQ9007, MQ9008, MQ9009, MQ9018, MQ9101 to MQ9105, and MQ9014 on transition-held references.
    private static void CheckTransitions(ValidationContext context, ProcessAnalysis analysis, Locals locals, Report report)
    {
        var process = analysis.Process;
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var t = process.Transitions[i];
            var pointer = "/transitions/" + N(i);
            CheckLocal(context, process, locals.States, t.Source, "state", pointer + "/source", t.Id, report);
            if (t.Event is not null)
                CheckLocal(context, process, locals.Events, t.Event, "event", pointer + "/event", t.Id, report);
            if (t.Guard is not null)
                CheckLocal(context, process, locals.Guards, t.Guard, "guard", pointer + "/guard", t.Id, report);
            if (t.Invoke is not null)
                CheckLocal(context, process, locals.Invokes, t.Invoke, "invoke", pointer + "/invoke", t.Id, report);
            for (var j = 0; j < t.Actions.Count; j++)
                CheckLocal(context, process, locals.Actions, t.Actions[j], "action", pointer + "/actions/" + N(j), t.Id, report);

            var source = analysis.Find(t.Source);
            CheckTargets(context, analysis, t, pointer, report);
            CheckTrigger(analysis, t, source, pointer, report);
            if (t.Gate is { } gate)
                CheckGate(context, process, locals, t, gate, pointer + "/gate", report);
        }

        CheckOverlaps(analysis, report);
        CheckGatePairs(process, report);
        foreach (var (states, transition) in analysis.EventlessCycles())
        {
            report.Add("MQ9009", $"Unguarded eventless transitions of process '{process.Name}' loop through {Names(states.Select(s => "'" + s.Path + "'"))}: the macrostep never ends. Guard one of them, or give one an event.",
                "/transitions/" + N(transition), process.Transitions[transition].Id);
        }
    }

    private static void CheckTargets(ValidationContext context, ProcessAnalysis analysis, ProcessTransition t, string pointer, Report report)
    {
        var process = analysis.Process;
        var found = new List<StateNode>();
        for (var j = 0; j < t.Targets.Count; j++)
        {
            if (analysis.Find(t.Targets[j]) is { } node)
            {
                found.Add(node);
                continue;
            }

            if (OtherProcessOwner(context, process, t.Targets[j], "state") is { } other)
                report.Add("MQ9007", $"Transition {pointer[13..]} of process '{process.Name}' targets a state of process '{other.Name}'; a transition stays in its process. Target one of this process's states, or invoke the other process.", pointer + "/targets/" + N(j), t.Id);
        }

        for (var a = 0; a < found.Count; a++)
        {
            for (var b = a + 1; b < found.Count; b++)
            {
                var x = found[a];
                var y = found[b];
                string? problem = null;
                if (ReferenceEquals(x, y) || ProcessAnalysis.IsDescendant(x, y) || ProcessAnalysis.IsDescendant(y, x))
                    problem = "are in one region";
                else if (ProcessAnalysis.CommonAncestor(x, y) is not { Type: StateType.Parallel })
                    problem = "are not in orthogonal regions of one parallel state";
                if (problem is null)
                    continue;
                report.Add("MQ9007", $"Targets '{x.Path}' and '{y.Path}' of transition {pointer[13..]} in process '{process.Name}' {problem}; several targets name one state in each region of a parallel ancestor. Keep one target per region.", pointer + "/targets/" + N(b), t.Id);
                return;
            }
        }
    }

    private static void CheckTrigger(ProcessAnalysis analysis, ProcessTransition t, StateNode? source, string pointer, Report report)
    {
        var name = $"Transition {pointer[13..]} of process '{analysis.Process.Name}'";
        switch (t.Trigger)
        {
            case TransitionTrigger.Event when t.Event is null:
                report.Add("MQ9008", $"{name} has trigger 'event' but names no event; set event, or pick another trigger.", pointer, t.Id);
                break;
            case TransitionTrigger.After when !IsPositiveDuration(t.After):
                report.Add("MQ9008", t.After is null
                    ? $"{name} has trigger 'after' but no duration; set after to a positive ISO 8601 duration such as P2D or PT30M."
                    : $"{name} has the duration '{t.After}', which is not a positive ISO 8601 duration; use a form such as P2D or PT30M.",
                    t.After is null ? pointer : pointer + "/after", t.Id);
                break;
            case TransitionTrigger.Done when source is not null && source.Type is not (StateType.Compound or StateType.Parallel):
                report.Add("MQ9008", $"{name} has trigger 'done' on {TypeName(source.Type)} state '{source.Path}'; only a compound or parallel state completes. Use another trigger or source.", pointer + "/trigger", t.Id);
                break;
            case TransitionTrigger.Done when source is not null && analysis.Reachable.Contains(source.Id) && !analysis.Completes(source):
                report.Add("MQ9018", $"{name} fires when '{source.Path}' completes, but no final state of it is reachable{(source.Type == StateType.Parallel ? " in every region" : "")}, so it never fires. Add a reachable final child, or change the trigger.", pointer + "/trigger", t.Id);
                break;
            case TransitionTrigger.InvokeDone or TransitionTrigger.InvokeError when source is not null
                && (t.Invoke is null || !source.State.Invoke.Any(v => string.Equals(v.Id, t.Invoke, StringComparison.Ordinal))):
                report.Add("MQ9008", $"{name} has trigger '{TriggerName(t.Trigger)}' but {(t.Invoke is null ? "names no invoke" : "names an invoke that is not on its source")} '{source.Path}'; set invoke to one of the source's invokes.", t.Invoke is null ? pointer : pointer + "/invoke", t.Id);
                break;
        }

        // A field that belongs to another trigger is never read.
        if (t.Event is not null && t.Trigger != TransitionTrigger.Event)
            report.Add("MQ9008", $"{name} sets event but its trigger is '{TriggerName(t.Trigger)}'; remove event or set the trigger to 'event'.", pointer + "/event", t.Id);
        if (t.After is not null && t.Trigger != TransitionTrigger.After)
            report.Add("MQ9008", $"{name} sets after but its trigger is '{TriggerName(t.Trigger)}'; remove after or set the trigger to 'after'.", pointer + "/after", t.Id);
        if (t.Invoke is not null && t.Trigger is not (TransitionTrigger.InvokeDone or TransitionTrigger.InvokeError))
            report.Add("MQ9008", $"{name} sets invoke but its trigger is '{TriggerName(t.Trigger)}'; remove invoke or use 'invoke-done' or 'invoke-error'.", pointer + "/invoke", t.Id);
    }

    private static void CheckOverlaps(ProcessAnalysis analysis, Report report)
    {
        var process = analysis.Process;
        foreach (var group in analysis.Groups)
        {
            if (group.Transitions.Length < 2 || analysis.Find(group.Source) is not { } source)
                continue;
            var unguarded = -1;
            var guards = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in group.Transitions)
            {
                var t = process.Transitions[i];
                var name = $"Transition {N(i)} from '{source.Path}' in process '{process.Name}'";
                if (unguarded >= 0)
                {
                    report.Add("MQ9006", $"{name} never fires: transition {N(unguarded)} has the same source and trigger, comes first and has no guard. Guard the earlier one, or remove this one.", "/transitions/" + N(i), t.Id);
                    continue;
                }

                if (t.Guard is null)
                    unguarded = i;
                else if (!guards.Add(t.Guard))
                    report.Add("MQ9006", $"{name} repeats a guard an earlier transition with the same source and trigger already tests, so it never fires. Use another guard, or merge the transitions.", "/transitions/" + N(i) + "/guard", t.Id);
            }
        }
    }

    private static void CheckGate(ValidationContext context, Process process, Locals locals, ProcessTransition t, ProcessGate gate, string pointer, Report report)
    {
        var name = $"Gate '{gate.Name}' of process '{process.Name}'";
        if (t.Trigger != TransitionTrigger.Event)
            report.Add("MQ9103", $"{name} is on a transition whose trigger is '{TriggerName(t.Trigger)}'; signatures are given on an event. Move the gate to an event transition.", pointer, gate.Id);
        if (gate.Meanings.Count == 0)
            report.Add("MQ9104", $"{name} has no meanings; add at least one, saying what a signature means (\"Reviewed as finance controller\").", pointer + "/meanings", gate.Id);

        for (var i = 0; i < gate.RequiredActors.Count; i++)
        {
            if (!gate.Signers.Contains(gate.RequiredActors[i], StringComparer.Ordinal))
                report.Add("MQ9102", $"{name} requires a signature from {ActorName(context, gate.RequiredActors[i])}, which is not one of its signers; add it to signers.", pointer + "/requiredActors/" + N(i), gate.Id);
        }

        var signers = gate.Signers.Distinct(StringComparer.Ordinal).Select(id => context.Model.Get<Actor>(id)).ToList();
        if (!gate.AllowRepeatSigner && signers.Count > 0 && signers.All(a => a is { Type: ActorType.Person }) && gate.Required > signers.Count)
            report.Add("MQ9101", $"{name} needs {N(gate.Required)} signatures, but its signers are {N(signers.Count)} person actor(s) and a signer may not sign twice; lower required, add signers, or allow repeat signers.", pointer + "/required", gate.Id);

        if (t.Trigger == TransitionTrigger.Event && t.Event is not null && locals.Events.TryGetValue(t.Event, out var e) && e.Actors.Count > 0)
        {
            for (var i = 0; i < gate.Signers.Count; i++)
            {
                if (!e.Actors.Contains(gate.Signers[i], StringComparer.Ordinal))
                    report.Add("MQ9105", $"{name} lists signer {ActorName(context, gate.Signers[i])}, but event '{e.Name}' may only be raised by {Names(e.Actors.Select(a => ActorName(context, a)))}; add the signer to the event's actors.", pointer + "/signers/" + N(i), gate.Id);
            }
        }
    }

    private static string ActorName(ValidationContext context, string id) =>
        context.Model.Get<Actor>(id) is { } actor ? "'" + actor.Name + "'" : "'" + id + "'";

    private static void CheckGatePairs(Process process, Report report)
    {
        var seen = new Dictionary<(string, string), int>();
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var t = process.Transitions[i];
            if (t.Gate is null || t.Trigger != TransitionTrigger.Event || t.Event is null)
                continue;
            if (seen.TryGetValue((t.Source, t.Event), out var first))
                report.Add("MQ9103", $"Transitions {N(first)} and {N(i)} of process '{process.Name}' both carry a gate on one source and event; the signatures could not tell which gate they count for. Keep one gate.", "/transitions/" + N(i) + "/gate", t.Gate.Id);
            else
                seen[(t.Source, t.Event)] = i;
        }
    }

    // MQ9003, MQ9005.
    private static void CheckReachability(ProcessAnalysis analysis, Report report)
    {
        var process = analysis.Process;
        foreach (var node in analysis.States)
        {
            if (analysis.Reachable.Contains(node.Id) || (node.Parent is not null && !analysis.Reachable.Contains(node.Parent.Id)))
                continue; // only the topmost unreachable state is reported
            var below = analysis.States.Count(s => ProcessAnalysis.IsDescendant(s, node));
            var suffix = below > 0 ? $" (nor its {N(below)} descendant state(s))" : "";
            report.Add("MQ9003", $"State '{node.Path}' of process '{process.Name}' is unreachable{suffix}: no path from the initial state enters it. Add a transition to it, or remove it.", node.Pointer, node.Id);
        }

        if (!analysis.Completes(null))
            report.Add("MQ9005", $"No final state of process '{process.Name}' is reachable from its initial state: the process never completes. This is normal for a lifecycle without an end; otherwise add a final state and a transition to it.", "/states");
    }

    // MQ9013.
    private static void CheckUnused(Process process, Locals locals, Report report)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in process.Transitions)
        {
            if (t.Trigger == TransitionTrigger.Event && t.Event is not null)
                used.Add(t.Event);
            if (t.Guard is not null)
                used.Add(t.Guard);
            if (t.Trigger is TransitionTrigger.InvokeDone or TransitionTrigger.InvokeError && t.Invoke is not null)
                used.Add(t.Invoke);
            used.UnionWith(t.Actions);
        }

        foreach (var state in locals.AllStates)
        {
            used.UnionWith(state.Entry);
            used.UnionWith(state.Exit);
        }

        for (var i = 0; i < process.Events.Count; i++)
        {
            if (!used.Contains(process.Events[i].Id))
                report.Add("MQ9013", $"Event '{process.Events[i].Name}' of process '{process.Name}' triggers no transition; use it on a transition or remove it.", "/events/" + N(i), process.Events[i].Id);
        }

        for (var i = 0; i < process.Guards.Count; i++)
        {
            if (!used.Contains(process.Guards[i].Id))
                report.Add("MQ9013", $"Guard '{process.Guards[i].Name}' of process '{process.Name}' guards no transition; use it on a transition or remove it.", "/guards/" + N(i), process.Guards[i].Id);
        }

        for (var i = 0; i < process.Actions.Count; i++)
        {
            if (!used.Contains(process.Actions[i].Id))
                report.Add("MQ9013", $"Action '{process.Actions[i].Name}' of process '{process.Name}' runs nowhere (no transition, entry or exit lists it); use it or remove it.", "/actions/" + N(i), process.Actions[i].Id);
        }

        foreach (var (invoke, pointer) in locals.InvokePointers)
        {
            if (!used.Contains(invoke.Id))
                report.Add("MQ9013", $"Invoke '{invoke.Name}' of process '{process.Name}' has no invoke-done or invoke-error transition, so its outcome is never used; add a transition on its completion or remove it.", pointer, invoke.Id);
        }
    }

    // MQ9015.
    private static void CheckInvokes(ValidationContext context, Process process, ProcessAnalysis analysis, Report report)
    {
        foreach (var node in analysis.States)
        {
            for (var i = 0; i < node.State.Invoke.Count; i++)
            {
                var invoke = node.State.Invoke[i];
                var pointer = node.Pointer + "/invoke/" + N(i);
                var name = $"Invoke '{invoke.Name}' of process '{process.Name}'";
                switch (invoke.Type)
                {
                    case InvokeType.Process when invoke.Process is null:
                        report.Add("MQ9015", $"{name} is a sub-process invoke without a process; set process.", pointer, invoke.Id);
                        break;
                    case InvokeType.Process when context.Model.Get<Process>(invoke.Process) is { } callee && Reaches(context, callee, process.Id) is { } chain:
                        report.Add("MQ9015", $"{name} starts a cycle of sub-processes ({Names(chain.Prepend(process.Name))}): the process would invoke itself. Break the cycle.", pointer + "/process", invoke.Id);
                        break;
                    case InvokeType.HumanTask when invoke.Actors.Count == 0:
                        report.Add("MQ9015", $"{name} is a human task without actors; list the actors who may complete it.", pointer, invoke.Id);
                        break;
                }
            }
        }
    }

    // The names of a sub-process chain from start that invokes target again, or null.
    private static List<string>? Reaches(ValidationContext context, Process start, string targetId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        return Visit(start) ? path : null;

        bool Visit(Process p)
        {
            path.Add(p.Name);
            if (string.Equals(p.Id, targetId, StringComparison.Ordinal))
                return true;
            if (visited.Add(p.Id))
            {
                foreach (var callee in Callees(p))
                {
                    if (context.Model.Get<Process>(callee) is { } next && Visit(next))
                        return true;
                }
            }

            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    /// <summary>The ids of the processes a process invokes as sub-processes, in document order.</summary>
    /// <param name="process">The process.</param>
    /// <returns>The ids.</returns>
    internal static IEnumerable<string> Callees(Process process)
    {
        var stack = new Stack<ProcessState>(process.States.Reverse());
        while (stack.Count > 0)
        {
            var state = stack.Pop();
            foreach (var invoke in state.Invoke)
            {
                if (invoke.Type == InvokeType.Process && invoke.Process is not null)
                    yield return invoke.Process;
            }

            for (var i = state.States.Count - 1; i >= 0; i--)
                stack.Push(state.States[i]);
        }
    }

    // MQ9201 (process side), MQ9202, MQ9203, MQ9204, MQ9205.
    private static void CheckLifecycle(ValidationContext context, Process process, ProcessAnalysis analysis, Report report)
    {
        var model = context.Model;
        if (process.Use == ProcessUse.Orchestration)
        {
            if (process.BoundAttribute is not null)
                report.Add("MQ9202", $"Process '{process.Name}' is an orchestration but binds an attribute; only a lifecycle binds its states to an attribute. Remove boundAttribute or set use to lifecycle.", "/boundAttribute");
            return;
        }

        if (process.Subject is null)
        {
            report.Add("MQ9201", $"Process '{process.Name}' is a lifecycle without a subject; set subject to the entity it describes.", "/use");
            return;
        }

        if (model.Get<Entity>(process.Subject) is not { } subject)
            return; // MQ2001 or MQ2002
        if (!string.Equals(subject.Lifecycle, process.Id, StringComparison.Ordinal))
        {
            var current = subject.Lifecycle is not null && model.Get<Process>(subject.Lifecycle) is { } other ? $"names process '{other.Name}'" : "is not set";
            report.Add("MQ9201", $"Process '{process.Name}' is the lifecycle of entity '{subject.Name}', but the entity's lifecycle {current}; set the entity's lifecycle to this process.", "/subject");
        }

        if (process.BoundAttribute is null)
            return;
        var flat = context.Flatten(subject).FirstOrDefault(f => string.Equals(f.Attribute.Id, process.BoundAttribute, StringComparison.Ordinal));
        if (flat is null)
        {
            report.Add("MQ9202", $"The bound attribute of process '{process.Name}' is not an attribute of entity '{subject.Name}' (own, inherited or from a stereotype); bind one of the subject's enum attributes.", "/boundAttribute");
            return;
        }

        var attribute = flat.Attribute;
        var enumType = attribute.Type.Ref is { } typeId ? model.Get<EnumType>(typeId) : null;
        if (enumType is null || attribute.Collection || enumType.Flags)
        {
            var why = enumType is null ? "is not enum-typed" : attribute.Collection ? "is a collection" : $"is typed by the flags enum '{enumType.Name}', which holds several members at once";
            report.Add("MQ9202", $"The bound attribute '{attribute.Name}' of process '{process.Name}' {why}; bind a single-valued enum attribute of '{subject.Name}'.", "/boundAttribute");
            return;
        }

        var states = BoundStates(process).ToList();
        var members = enumType.Members.Select(m => m.Name).ToList();
        if (!states.SequenceEqual(members, StringComparer.Ordinal))
        {
            var missing = states.Except(members, StringComparer.Ordinal).ToList();
            var extra = members.Except(states, StringComparer.Ordinal).ToList();
            var parts = new List<string>();
            if (missing.Count > 0)
                parts.Add("missing " + Names(missing));
            if (extra.Count > 0)
                parts.Add("extra " + Names(extra));
            if (parts.Count == 0)
                parts.Add("out of order");
            report.Add("MQ9203", $"The members of enum '{enumType.Name}' differ from the states of process '{process.Name}' ({Names(states)}): {string.Join("; ", parts)}. Sync the enum from the process.", "/boundAttribute");
        }

        var others = model.ReferencesTo(enumType.Id)
            .Where(r => r.JsonPointer.EndsWith("/type/ref", StringComparison.Ordinal) && !string.Equals(r.FromId, attribute.Id, StringComparison.Ordinal))
            .Select(r => AttributeLabel(model, r)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (others.Count > 0)
            report.Add("MQ9204", $"Enum '{enumType.Name}', bound by process '{process.Name}', is also used by {Names(others.Select(o => "'" + o + "'"))}: syncing it from the process changes them too. Give the lifecycle its own enum if they should not follow.", "/boundAttribute");

        if (analysis.InitialOf(null) is { Type: not (StateType.History or StateType.Choice) } initial
            && !(attribute.Default is { ValueKind: JsonValueKind.String } value && string.Equals(value.GetString(), initial.State.Name, StringComparison.Ordinal)))
        {
            var current = attribute.Default is { } d ? $"is {d.GetRawText()}" : "is not set";
            report.Add("MQ9205", $"The default of bound attribute '{attribute.Name}' {current}, but process '{process.Name}' starts in '{initial.State.Name}'; set the default to '{initial.State.Name}'.", "/boundAttribute");
        }
    }

    // "Owner.attribute" for a reference held by an attribute's type.
    private static string AttributeLabel(ModelSnapshot model, ReferenceInfo reference)
    {
        if (model.GetDocument(reference.FromElementId) is not { } document)
            return reference.FromElementId;
        var node = document.Json;
        foreach (var segment in Ptr.Split(reference.JsonPointer[..^"/type/ref".Length]))
        {
            if (node.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < node.GetArrayLength())
                node = node[i];
            else if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out var child))
                node = child;
            else
                return document.Element.Name;
        }

        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? document.Element.Name + "." + name.GetString()
            : document.Element.Name;
    }

    /// <summary>The names of a lifecycle's bound states: the root's children that are not history or choice, in document order.</summary>
    /// <param name="process">The process.</param>
    /// <returns>The state names.</returns>
    internal static IEnumerable<string> BoundStates(Process process) =>
        process.States.Where(s => s.Type is not (StateType.History or StateType.Choice)).Select(s => s.Name);

    // MQ9201 (entity side).
    private static void CheckEntityLifecycle(ValidationContext context, Entity entity, Report report)
    {
        if (entity.Lifecycle is null || context.Model.Get<Process>(entity.Lifecycle) is not { } process)
            return;
        if (process.Use != ProcessUse.Lifecycle)
            report.Add("MQ9201", $"Entity '{entity.Name}' names process '{process.Name}' as its lifecycle, but that process is an orchestration; name a lifecycle process, or set its use to lifecycle.", "/lifecycle");
        else if (!string.Equals(process.Subject, entity.Id, StringComparison.Ordinal))
            report.Add("MQ9201", $"Entity '{entity.Name}' names process '{process.Name}' as its lifecycle, but that process's subject is {(process.Subject is not null && context.Model.Get<Entity>(process.Subject) is { } other ? "entity '" + other.Name + "'" : "not this entity")}; set the process's subject to this entity, or name its own lifecycle.", "/lifecycle");
    }

    // MQ9106.
    private static void CheckActor(ValidationContext context, Actor actor, Report report)
    {
        var model = context.Model;
        if (!model.ReferencesTo(actor.Id).Any(r => model.Get<Process>(r.FromElementId) is not null))
            report.Add("MQ9106", $"Actor '{actor.Name}' is not referenced by any process (event actors, gate signers or human tasks); use it in a process or remove it.", "");
    }

    // MQ9016.
    private static void CheckDiagram(ValidationContext context, Diagram diagram, Report report)
    {
        if (diagram.Process is null || context.Model.Get<Process>(diagram.Process) is not { } process)
            return;
        var states = Locals.Of(process).States;
        for (var i = 0; i < diagram.Members.Count; i++)
        {
            var element = diagram.Members[i].Element;
            if (states.ContainsKey(element))
                continue;
            if (!context.Model.TryGetEntry(element, out var entry))
                continue; // MQ2001
            report.Add("MQ9016", $"Member {N(i)} of diagram '{diagram.Name}' is {Article(entry.Kind)} {entry.Kind}, not a state of process '{process.Name}'; a process diagram shows only that process's states. Remove the member.", "/members/" + N(i) + "/element");
        }
    }

    // MQ9014: a reference that is not local but names a sub-element of another process.
    private static void CheckLocal<T>(ValidationContext context, Process process, IReadOnlyDictionary<string, T> locals, string id, string kind, string pointer, string? elementId, Report report)
    {
        if (locals.ContainsKey(id) || OtherProcessOwner(context, process, id, kind) is not { } other)
            return;
        report.Add("MQ9014", $"Process '{process.Name}' refers to {kind} '{NameOf(other, id)}' of process '{other.Name}'; a process refers only to its own states, events, guards, actions and invokes. Declare it in this process.", pointer, elementId);
    }

    private static Process? OtherProcessOwner(ValidationContext context, Process process, string id, string kind)
    {
        if (!context.Model.TryGetEntry(id, out var entry) || !SubElementKinds.Contains(entry.Kind) || !string.Equals(entry.Kind, kind, StringComparison.Ordinal)
            || string.Equals(entry.OwnerId, process.Id, StringComparison.Ordinal))
            return null;
        return context.Model.Get<Process>(entry.OwnerId);
    }

    private static string NameOf(Process owner, string id)
    {
        var locals = Locals.Of(owner);
        return locals.States.TryGetValue(id, out var s) ? s.Name
            : locals.Events.TryGetValue(id, out var e) ? e.Name
            : locals.Guards.TryGetValue(id, out var g) ? g.Name
            : locals.Actions.TryGetValue(id, out var a) ? a.Name
            : locals.Invokes.TryGetValue(id, out var v) ? v.Name
            : id;
    }

    private sealed record Locals(
        Dictionary<string, ProcessState> States,
        List<ProcessState> AllStates,
        Dictionary<string, ProcessEvent> Events,
        Dictionary<string, ProcessGuard> Guards,
        Dictionary<string, ProcessAction> Actions,
        Dictionary<string, ProcessInvoke> Invokes,
        List<(ProcessInvoke Invoke, string Pointer)> InvokePointers)
    {
        public static Locals Of(Process process)
        {
            var locals = new Locals(new(StringComparer.Ordinal), [], new(StringComparer.Ordinal), new(StringComparer.Ordinal),
                new(StringComparer.Ordinal), new(StringComparer.Ordinal), []);
            Walk(process.States, "");
            foreach (var e in process.Events)
                locals.Events.TryAdd(e.Id, e);
            foreach (var g in process.Guards)
                locals.Guards.TryAdd(g.Id, g);
            foreach (var a in process.Actions)
                locals.Actions.TryAdd(a.Id, a);
            return locals;

            void Walk(IReadOnlyList<ProcessState> states, string pointer)
            {
                for (var i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    var at = pointer + "/states/" + N(i);
                    locals.States.TryAdd(state.Id, state);
                    locals.AllStates.Add(state);
                    for (var j = 0; j < state.Invoke.Count; j++)
                    {
                        locals.Invokes.TryAdd(state.Invoke[j].Id, state.Invoke[j]);
                        locals.InvokePointers.Add((state.Invoke[j], at + "/invoke/" + N(j)));
                    }

                    Walk(state.States, at);
                }
            }
        }
    }
}
