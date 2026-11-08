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
        if (process.Use == ProcessUse.Library)
        {
            CheckLibrary(context, process, report);
            return;
        }

        if (process.States.Count == 0)
            report.Add("MQ9020", $"Process '{process.Name}' has no states; add one, or set use to library if it only shares guards, actions and gates.", "/states");
        if (process.Gates.Count > 0)
            report.Add("MQ9020", $"Process '{process.Name}' lists gates of its own, which only a library does; put each gate on its transition, or set use to library.", "/gates");
        var analysis = new ProcessAnalysis(process);
        var locals = Locals.Of(process);
        var codes = ProcessCodes.Of(process);
        CheckStates(context, analysis, locals, report);
        CheckCodes(process, codes, report);
        CheckTransitions(context, analysis, locals, report);
        CheckReachability(analysis, report);
        CheckUnused(process, locals, report);
        CheckInvokes(context, process, analysis, report);
        CheckLifecycle(context, process, analysis, codes, report);
        CheckSubjectOwners(context, process, report);
        CheckRelativeUses(context, process, analysis, report);
        CheckNodeNames(context, process, report);
        CheckNodeStereotypes(context, process, report);
    }

    // MQ9020, MQ9013 and the gate rules that judge a gate by itself: a library holds nothing that runs (next-phase-2-design.md section 4.4).
    private static void CheckLibrary(ValidationContext context, Process process, Report report)
    {
        void Holds(string what, string pointer, string? id = null) =>
            report.Add("MQ9020", $"Library '{process.Name}' holds {what}, which only a process that runs has; move it there, or change use.", pointer, id);
        if (process.States.Count > 0)
            Holds("states", "/states");
        if (process.Initial is not null)
            Holds("an initial state", "/initial");
        if (process.Transitions.Count > 0)
            Holds("transitions", "/transitions");
        if (process.Context.Count > 0)
            Holds("context", "/context");
        if (process.Events.Count > 0)
            Holds("events", "/events");
        if (process.Subject is not null)
            Holds("a subject", "/subject");
        if (process.BoundAttribute is not null)
            Holds("a bound attribute", "/boundAttribute");
        if (process.SubjectList is { Count: > 0 })
            Holds("subjects", "/subjects");
        for (var i = 0; i < process.Actions.Count; i++)
        {
            if (process.Actions[i].Raises.Count > 0)
                Holds($"action '{process.Actions[i].Name}' that raises events", "/actions/" + N(i) + "/raises", process.Actions[i].Id);
            // A set names an attribute of one process's subjects by id, as a condition does (object-effects-design.md section 5).
            if (process.Actions[i].Sets.Count > 0)
            {
                report.Add("MQ9020", $"Action '{process.Actions[i].Name}' of library '{process.Name}' sets the object's data, which names the attributes of one process's subjects; "
                    + "move the action to the process that runs it.", "/actions/" + N(i) + "/sets", process.Actions[i].Id);
            }
        }

        for (var i = 0; i < process.Guards.Count; i++)
        {
            if (process.Guards[i].Condition is not null)
                report.Add("MQ9020", $"Guard '{process.Guards[i].Name}' of library '{process.Name}' has a condition, which names the attributes of one process's context; write it as an expression, which reads the using process's context by name, or move the guard.",
                    "/guards/" + N(i) + "/condition", process.Guards[i].Id);
        }

        for (var i = 0; i < process.Gates.Count; i++)
        {
            CheckGateItself(context, $"Gate '{process.Gates[i].Name}' of library '{process.Name}'", process.Gates[i], "/gates/" + N(i), report);
        }

        bool Used(string id) => context.Model.ReferencesTo(id).Any(r => context.Model.Get<Process>(r.FromElementId) is not null
            && !r.JsonPointer.Contains("/properties/", StringComparison.Ordinal));
        void Unused(string what, string name, string pointer, string id)
        {
            if (!Used(id))
                report.Add("MQ9013", $"{what} '{name}' of library '{process.Name}' is used by no process; use it or remove it.", pointer, id);
        }

        for (var i = 0; i < process.Guards.Count; i++)
            Unused("Guard", process.Guards[i].Name, "/guards/" + N(i), process.Guards[i].Id);
        for (var i = 0; i < process.Actions.Count; i++)
            Unused("Action", process.Actions[i].Name, "/actions/" + N(i), process.Actions[i].Id);
        for (var i = 0; i < process.Gates.Count; i++)
            Unused("Gate", process.Gates[i].Name, "/gates/" + N(i), process.Gates[i].Id);
        CheckNodeNames(context, process, report);
        CheckNodeStereotypes(context, process, report);
    }

    // MQ3001 and MQ3018 on the process's sub-elements (section 2.6): states unique among siblings; events, guards, actions,
    // invokes and gates unique within the process, one namespace per kind; gate meanings unique within their gate.
    private static void CheckNodeNames(ValidationContext context, Process process, Report report)
    {
        var uses = ProcessLibraries.UsesOf(context.Model, process);
        CheckSiblings(process.States, "", null);
        Unique("event", process.Events.Select((e, i) => (e.Name, "/events/" + N(i), e.Id)));
        Unique("guard", process.Guards.Select((g, i) => (g.Name, "/guards/" + N(i), g.Id)));
        Unique("action", process.Actions.Select((a, i) => (a.Name, "/actions/" + N(i), a.Id)));
        UsedNames("guard", process.Guards.Select(g => g.Name), uses.Where(u => u.Guard is not null).Select(u => (u.Guard!.Name, u.Pointer, u.Guard.Id)));
        UsedNames("action", process.Actions.Select(a => a.Name), uses.Where(u => u.Action is not null).Select(u => (u.Action!.Name, u.Pointer, u.Action.Id)));
        Unique("invoke", Invokes(process.States, ""));
        var gates = new List<(string, string, string)>();
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            if (process.Transitions[i].Gate is not { } gate)
                continue;
            var at = "/transitions/" + N(i) + "/gate";
            gates.Add((gate.Name, at, gate.Id));
            Unique("meaning", gate.Meanings.Select((m, j) => (m.Name, at + "/meanings/" + N(j), m.Id)), "gate '" + gate.Name + "'");
            ApprovalNames(gate, at);
        }

        for (var i = 0; i < process.Gates.Count; i++)
        {
            var gate = process.Gates[i];
            var at = "/gates/" + N(i);
            gates.Add((gate.Name, at, gate.Id));
            Unique("meaning", gate.Meanings.Select((m, j) => (m.Name, at + "/meanings/" + N(j), m.Id)), "gate '" + gate.Name + "'");
            ApprovalNames(gate, at);
        }

        Unique("gate", gates);
        UsedNames("gate", gates.Select(g => g.Item1), uses.Where(u => u.Gate is not null).Select(u => (u.Gate!.Name, u.Pointer, u.Gate.Id)));
        // An inline right's name is unique among the lifecycle's inline rights (rights-design.md decision 3); a use has no name of its own.
        Unique("right", process.Rights.Select((r, i) => (r.Right is null ? r.Name ?? "" : "", "/rights/" + N(i), r.Id)));

        // An approval's stages each have their own name, and so do its signatures, across its stages (signoff-design.md section 6).
        void ApprovalNames(ProcessGate gate, string at)
        {
            if (gate.Stages is not { } stages)
                return;
            Unique("stage", stages.Select((s, j) => (s.Name, at + "/stages/" + N(j), s.Id)), "gate '" + gate.Name + "'");
            var signatures = new List<(string, string, string)>();
            ApprovalRules.ForEachSignature(gate, at, (signature, pointer) => signatures.Add((signature.Name, pointer, signature.Id)));
            Unique("signature", signatures, "gate '" + gate.Name + "'");
        }

        // A library member's name must not clash with an own node's or another member's where code meets them: a pack generates one method per name.
        void UsedNames(string what, IEnumerable<string> own, IEnumerable<(string Name, string Pointer, string Id)> used)
        {
            var seen = new HashSet<string>(own, StringComparer.Ordinal);
            foreach (var (name, pointer, id) in used)
            {
                if (!seen.Add(name))
                    report.Add("MQ3001", $"The {what} name '{name}' from a library is already used in process '{process.Name}'; rename one of them.", pointer, id);
            }
        }

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

    // MQ2003 and MQ2004 on the marks of states, transitions, events, guards, actions, invokes and gates (section 2.6); the process's own
    // marks are checked as every element's are.
    private static void CheckNodeStereotypes(ValidationContext context, Process process, Report report)
    {
        Walk(process.States, "");
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var t = process.Transitions[i];
            BuiltinRules.CheckStereotypes(context, t.Id, t.Stereotypes, "/transitions/" + N(i), "transition", report);
            if (t.Gate is { } gate)
                BuiltinRules.CheckStereotypes(context, gate.Id, gate.Stereotypes, "/transitions/" + N(i) + "/gate", "gate", report);
        }

        for (var i = 0; i < process.Events.Count; i++)
            BuiltinRules.CheckStereotypes(context, process.Events[i].Id, process.Events[i].Stereotypes, "/events/" + N(i), "event", report);
        for (var i = 0; i < process.Guards.Count; i++)
            BuiltinRules.CheckStereotypes(context, process.Guards[i].Id, process.Guards[i].Stereotypes, "/guards/" + N(i), "guard", report);
        for (var i = 0; i < process.Actions.Count; i++)
            BuiltinRules.CheckStereotypes(context, process.Actions[i].Id, process.Actions[i].Stereotypes, "/actions/" + N(i), "action", report);
        for (var i = 0; i < process.Gates.Count; i++)
            BuiltinRules.CheckStereotypes(context, process.Gates[i].Id, process.Gates[i].Stereotypes, "/gates/" + N(i), "gate", report);

        void Walk(IReadOnlyList<ProcessState> states, string pointer)
        {
            for (var i = 0; i < states.Count; i++)
            {
                var at = pointer + "/states/" + N(i);
                BuiltinRules.CheckStereotypes(context, states[i].Id, states[i].Stereotypes, at, "state", report);
                for (var j = 0; j < states[i].Invoke.Count; j++)
                    BuiltinRules.CheckStereotypes(context, states[i].Invoke[j].Id, states[i].Invoke[j].Stereotypes, at + "/invoke/" + N(j), "invoke", report);
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
            if (t.GatePolicy is { } policy)
                CheckGatePolicy(context, process, locals, t, policy, pointer, report);
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
        CheckGateItself(context, name, gate, pointer, report);
        CheckGateEvent(context, name, locals, t, gate, pointer, report);
    }

    // MQ9021, then what a gate's use on a transition can get wrong; a policy's own rules are judged on its library.
    private static void CheckGatePolicy(ValidationContext context, Process process, Locals locals, ProcessTransition t, string policy, string pointer, Report report)
    {
        if (t.Gate is not null)
            report.Add("MQ9021", $"Transition {t.Id} of process '{process.Name}' has both a gate and a gatePolicy; keep one.", pointer + "/gatePolicy", t.Id);
        if (ProcessLibraries.Owner(context.Model, policy, "gate") is not { } library)
        {
            if (context.Model.TryGetEntry(policy, out _))
                report.Add("MQ9021", $"The gatePolicy of transition {t.Id} of process '{process.Name}' names a gate that no library holds; point at a gate of a library, or put the gate on the transition.", pointer + "/gatePolicy", t.Id);
            return;
        }

        var gate = library.Gates.First(g => string.Equals(g.Id, policy, StringComparison.Ordinal));
        var name = $"Gate '{gate.Name}' of library '{library.Name}', used by process '{process.Name}',";
        if (t.Trigger != TransitionTrigger.Event)
            report.Add("MQ9103", $"{name} is on a transition whose trigger is '{TriggerName(t.Trigger)}'; signatures are given on an event. Move the gate to an event transition.", pointer + "/gatePolicy", t.Id);
        CheckGateEvent(context, name, locals, t, gate, pointer + "/gatePolicy", report);
    }

    // MQ9104, MQ9102, MQ9101: what a gate gets wrong wherever it is used.
    private static void CheckGateItself(ValidationContext context, string name, ProcessGate gate, string pointer, Report report)
    {
        if (gate.Meanings.Count == 0)
            report.Add("MQ9104", $"{name} has no meanings; add at least one, saying what a signature means (\"Reviewed as finance controller\").", pointer + "/meanings", gate.Id);

        // A required actor that is a member of a signer, at any depth, counts as among them (actors-design.md decision 6).
        var membership = ActorMembership.Of(context.Model);
        for (var i = 0; i < gate.RequiredActors.Count; i++)
        {
            if (!membership.Allows(gate.Signers, gate.RequiredActors[i]))
                report.Add("MQ9102", $"{name} requires a signature from {ActorName(context, gate.RequiredActors[i])}, which is not one of its signers; add it to signers.", pointer + "/requiredActors/" + N(i), gate.Id);
        }

        var signers = gate.Signers.Distinct(StringComparer.Ordinal).Select(id => context.Model.Get<Actor>(id)).ToList();
        if (!gate.AllowRepeatSigner && signers.Count > 0 && signers.All(a => a is { Type: ActorType.Person }) && gate.Required > signers.Count)
            report.Add("MQ9101", $"{name} needs {N(gate.Required)} signatures, but its signers are {N(signers.Count)} person actor(s) and a signer may not sign twice; lower required, add signers, or allow repeat signers.", pointer + "/required", gate.Id);
        if (gate.UsesStages)
            CheckSignatures(context, membership, name, gate, pointer, report);
    }

    // MQ9102 and MQ9101 on each named signature of an approval, as on a gate (signoff-design.md section 6), then MQ9101 on the approval: without
    // allowSignerAcrossSignatures each person gives one response that counts on it (decision 16), so it needs as many different people as its
    // signatures need together. Only person actors are counted, as for a gate, and only the signatures always needed: one with a when may not
    // be needed, so the count is known only without it.
    private static void CheckSignatures(ValidationContext context, ActorMembership membership, string name, ProcessGate gate, string pointer, Report report)
    {
        var people = new HashSet<string>(StringComparer.Ordinal);
        var allPeople = true;
        var needed = 0;
        var conditional = false;
        ApprovalRules.ForEachSignature(gate, pointer, (signature, at) =>
        {
            var what = $"Signature '{signature.Name}' of {char.ToLowerInvariant(name[0])}{name[1..]}";
            for (var i = 0; i < signature.RequiredActors.Count; i++)
            {
                if (!membership.Allows(signature.Signers, signature.RequiredActors[i]))
                    report.Add("MQ9102", $"{what} requires a signature from {ActorName(context, signature.RequiredActors[i])}, which is not one of its signers; add it to the signature's signers.", at + "/requiredActors/" + N(i), signature.Id);
            }

            var signers = signature.Signers.Distinct(StringComparer.Ordinal).Select(id => context.Model.Get<Actor>(id)).ToList();
            var persons = signers.Count > 0 && signers.All(a => a is { Type: ActorType.Person });
            if (!signature.AllowRepeatSigner && persons && signature.Required > signers.Count)
                report.Add("MQ9101", $"{what} needs {N(signature.Required)} signatures, but its signers are {N(signers.Count)} person actor(s) and a signer may not sign twice; lower required, add signers, or allow repeat signers.", at + "/required", signature.Id);
            if (signature.When is not null)
            {
                conditional = true;
                return;
            }

            allPeople &= persons;
            people.UnionWith(signature.Signers);
            // With repeat signing one person may give all of a signature's count, but each required actor still signs it.
            needed += signature.AllowRepeatSigner ? Math.Max(1, signature.RequiredActors.Distinct(StringComparer.Ordinal).Count()) : signature.Required;
        });

        if (!gate.AllowSignerAcrossSignatures && allPeople && people.Count > 0 && needed > people.Count)
            report.Add("MQ9101", $"{name} needs {N(needed)} different people across {(conditional ? "the signatures it always needs (those without when)" : "its signatures")}, but their signers are {N(people.Count)} person actor(s) and one person gives one signature of an approval; lower required, add signers, or allow one person several signatures (allowSignerAcrossSignatures).", pointer + "/stages", gate.Id);
    }

    // MQ9105.
    private static void CheckGateEvent(ValidationContext context, string name, Locals locals, ProcessTransition t, ProcessGate gate, string pointer, Report report)
    {
        // An actor from the object among the event's actors or as a signer is decided by the object, never by the model.
        if (t.Trigger == TransitionTrigger.Event && t.Event is not null && locals.Events.TryGetValue(t.Event, out var e) && e.Actors.Count > 0
            && !e.Actors.Any(a => IsRelative(context.Model, a)))
        {
            // A signer that is a member of one of the event's actors, at any depth, may raise it (actors-design.md decision 6).
            var membership = ActorMembership.Of(context.Model);
            for (var i = 0; i < gate.Signers.Count; i++)
            {
                if (!membership.Allows(e.Actors, gate.Signers[i]) && !IsRelative(context.Model, gate.Signers[i]))
                    report.Add("MQ9105", $"{name} lists signer {ActorName(context, gate.Signers[i])}, but event '{e.Name}' may only be raised by {Names(e.Actors.Select(a => ActorName(context, a)))}; add the signer to the event's actors.", pointer + "/signers/" + N(i), gate.Id);
            }

            // An approval's signers are its signatures' (signoff-design.md section 6).
            ApprovalRules.ForEachSignature(gate, pointer, (signature, at) =>
            {
                for (var i = 0; i < signature.Signers.Count; i++)
                {
                    if (!membership.Allows(e.Actors, signature.Signers[i]) && !IsRelative(context.Model, signature.Signers[i]))
                        report.Add("MQ9105", $"{name} lists signer {ActorName(context, signature.Signers[i])} in signature '{signature.Name}', but event '{e.Name}' may only be raised by {Names(e.Actors.Select(a => ActorName(context, a)))}; add the signer to the event's actors.", at + "/signers/" + N(i), signature.Id);
                }
            });
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
            if ((t.Gate is null && t.GatePolicy is null) || t.Trigger != TransitionTrigger.Event || t.Event is null)
                continue;
            if (seen.TryGetValue((t.Source, t.Event), out var first))
                report.Add("MQ9103", $"Transitions {N(first)} and {N(i)} of process '{process.Name}' both carry a gate on one source and event; the signatures could not tell which gate they count for. Keep one gate.", "/transitions/" + N(i) + (t.Gate is null ? "/gatePolicy" : "/gate"), t.Gate?.Id ?? t.GatePolicy);
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
                    case InvokeType.Process when context.Model.Get<Process>(invoke.Process) is { Use: ProcessUse.Library } library:
                        report.Add("MQ9022", $"{name} names library '{library.Name}', which has no states to run; name a running process.", pointer + "/process", invoke.Id);
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

    // MQ9209, MQ9201 (process side), MQ9202 to MQ9208, MQ9210, and MQ9206's clause under a binding.
    private static void CheckLifecycle(ValidationContext context, Process process, ProcessAnalysis analysis, ProcessCodes codes, Report report)
    {
        var model = context.Model;
        // The one-subject form is deprecated on lifecycles and orchestrations alike (lifecycle-design.md decision 6). It warns only where
        // there is a subject, since that is what use-subjects converts; a boundAttribute with no subject keeps its own findings (MQ9201, MQ9202).
        if (process.Subject is not null)
        {
            report.Add("MQ9209", $"Process '{process.Name}' names its subject in the one-subject form (subject and boundAttribute), which is deprecated; list it in subjects instead.",
                "/subject");
        }

        var entries = process.Subject is null ? process.SubjectList : null; // the subjects form
        CheckSubjectStereotypes(context, process, entries ?? [], report);
        if (process.Use == ProcessUse.Orchestration)
        {
            if (process.BoundAttribute is not null)
                report.Add("MQ9202", $"Process '{process.Name}' is an orchestration but binds an attribute; only a lifecycle binds its states to an attribute. Remove boundAttribute or set use to lifecycle.", "/boundAttribute");
            for (var i = 0; i < (entries?.Count ?? 0); i++)
            {
                if (entries![i].BoundAttribute is not null)
                {
                    report.Add("MQ9202", $"Process '{process.Name}' is an orchestration but its subject {SubjectLabel(model, entries[i])} binds an attribute; only a lifecycle binds its states to an attribute. Remove the subject's boundAttribute or set use to lifecycle.",
                        "/subjects/" + N(i) + "/boundAttribute", entries[i].Id);
                }
            }

            return;
        }

        var bindings = new List<(BindingType Type, SubjectRef Subject, ModelAttribute Attribute, string Pointer, string? EntryId)>();
        if (entries is not null)
        {
            CheckSubjects(context, process, analysis, codes, entries, bindings, report);
        }
        else if (process.Subject is null)
        {
            report.Add("MQ9201", $"Process '{process.Name}' is a lifecycle without a subject; set subject to the entity it describes.", "/use");
            return;
        }
        else
        {
            if (model.Get<Entity>(process.Subject) is not { } subject)
                return; // MQ2001 or MQ2002
            if (!string.Equals(subject.Lifecycle, process.Id, StringComparison.Ordinal))
            {
                var current = subject.Lifecycle is not null && model.Get<Process>(subject.Lifecycle) is { } other ? $"names process '{other.Name}'" : "is not set";
                report.Add("MQ9201", $"Process '{process.Name}' is the lifecycle of entity '{subject.Name}', but the entity's lifecycle {current}; set the entity's lifecycle to this process.", "/subject");
            }

            if (process.BoundAttribute is not null)
                CheckBinding(context, process, analysis, codes, SubjectRef.Of(context, subject), process.BoundAttribute, "/boundAttribute", null, bindings, report);
        }

        CheckBoundCodes(process, analysis, codes, bindings, report);
    }

    // The subjects form (lifecycle-design.md decisions 5 and 8): a lifecycle lists at least one entity or stereotype, each once. An entity
    // need not name the lifecycle back, since several may govern it. Each entry's binding is judged as the one-subject form's is; a
    // stereotype entry's binds one of the stereotype's own attributes (stereotype-sets-design.md decision 6).
    private static void CheckSubjects(ValidationContext context, Process process, ProcessAnalysis analysis, ProcessCodes codes, IReadOnlyList<ProcessSubject> entries,
        List<(BindingType Type, SubjectRef Subject, ModelAttribute Attribute, string Pointer, string? EntryId)> bindings, Report report)
    {
        var model = context.Model;
        if (entries.Count == 0)
        {
            report.Add("MQ9201", $"Process '{process.Name}' is a lifecycle with no subjects; add an entry to subjects for each entity it governs.", "/subjects");
            return;
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Stereotype is { } key)
            {
                if (!listed.Add("s:" + key))
                {
                    report.Add("MQ9201", $"Process '{process.Name}' lists {SubjectLabel(model, entry)} more than once in subjects; remove the repeated entry.",
                        "/subjects/" + N(i) + "/stereotype", entry.Id);
                    continue;
                }

                // An unknown key is MQ2003, one that does not apply to entities MQ9211 (CheckSubjectStereotypes).
                if (entry.BoundAttribute is not null && model.GetStereotype(key) is { } stereotype && AppliesToEntities(stereotype))
                    CheckBinding(context, process, analysis, codes, SubjectRef.Of(stereotype), entry.BoundAttribute, "/subjects/" + N(i) + "/boundAttribute", entry.Id, bindings, report);
                continue;
            }

            if (entry.Entity is not { } entityId)
                continue; // the schema asks for one of entity and stereotype
            if (!listed.Add("e:" + entityId))
            {
                report.Add("MQ9201", $"Process '{process.Name}' lists {SubjectLabel(model, entry)} more than once in subjects; remove the repeated entry.",
                    "/subjects/" + N(i) + "/entity", entry.Id);
                continue;
            }

            if (entry.BoundAttribute is not null && model.Get<Entity>(entityId) is { } entity) // else MQ2001 or MQ2002
                CheckBinding(context, process, analysis, codes, SubjectRef.Of(context, entity), entry.BoundAttribute, "/subjects/" + N(i) + "/boundAttribute", entry.Id, bindings, report);
        }
    }

    // MQ2003 and MQ9211 (stereotype-sets-design.md decision 5): a subject entry's stereotype is a known key of a stereotype that applies to
    // entities, on lifecycles and orchestrations alike.
    private static void CheckSubjectStereotypes(ValidationContext context, Process process, IReadOnlyList<ProcessSubject> entries, Report report)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Stereotype is not { } key)
                continue;
            var pointer = "/subjects/" + N(i) + "/stereotype";
            if (context.Model.GetStereotype(key) is not { } stereotype)
                report.Add("MQ2003", $"Stereotype '{key}' is not defined in model/vocabularies/stereotypes.", pointer, entries[i].Id);
            else if (!AppliesToEntities(stereotype))
                report.Add("MQ9211", $"Subject {N(i + 1)} of process '{process.Name}' names stereotype '{key}', which applies to {string.Join(", ", stereotype.AppliesTo)}, not to entities, so no entity can carry it; name a stereotype entities carry, or an entity.", pointer, entries[i].Id);
        }
    }

    /// <summary>Whether entities may carry a stereotype: its <c>appliesTo</c> is empty or names <c>entity</c>.</summary>
    /// <param name="stereotype">The stereotype.</param>
    /// <returns>Whether it applies to entities.</returns>
    internal static bool AppliesToEntities(Stereotype stereotype) =>
        stereotype.AppliesTo.Count == 0 || stereotype.AppliesTo.Contains(KindInfo.Get(ElementKind.Entity).Name, StringComparer.Ordinal);

    /// <summary>
    /// The subject a binding is judged for: an entity, whose attributes are its own, inherited and those of the stereotypes it carries; or a
    /// stereotype entry's stereotype, whose attributes are its own (stereotype-sets-design.md decision 6).
    /// </summary>
    /// <param name="Id">The entity's id, or the stereotype's key.</param>
    /// <param name="Name">The entity's name, or the stereotype's key.</param>
    /// <param name="Label">How findings name it: <c>entity 'Order'</c> or <c>stereotype 'approvable'</c>.</param>
    /// <param name="Stereotype">The stereotype key for a stereotype entry, else <see langword="null"/>.</param>
    /// <param name="Find">Finds one of the subject's attributes by id.</param>
    private sealed record SubjectRef(string Id, string Name, string Label, string? Stereotype, Func<string, ModelAttribute?> Find)
    {
        public static SubjectRef Of(ValidationContext context, Entity entity) =>
            new(entity.Id, entity.Name, "entity '" + entity.Name + "'", null,
                id => context.Flatten(entity).FirstOrDefault(f => string.Equals(f.Attribute.Id, id, StringComparison.Ordinal))?.Attribute);

        public static SubjectRef Of(Stereotype stereotype) =>
            new(stereotype.Key, stereotype.Key, "stereotype '" + stereotype.Key + "'", stereotype.Key,
                id => stereotype.Attributes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal)));
    }

    // What every binding of a lifecycle asks of its codes together (lifecycle-design.md decisions 4, 12 and 13): under any binding the codes
    // form a map (MQ9206), and under a binding that stores codes each state a configuration rests in has one (MQ9210, one warning per
    // binding, so an edit that adds a state before its code is never refused). An enum binding needs none: its member is the value.
    private static void CheckBoundCodes(Process process, ProcessAnalysis analysis, ProcessCodes codes, List<(BindingType Type, SubjectRef Subject, ModelAttribute Attribute, string Pointer, string? EntryId)> bindings,
        Report report)
    {
        if (bindings.Count == 0)
            return;
        var settled = codes.Coded.Where(c => c.State.Type is not (StateType.History or StateType.Choice)).ToList();
        if (settled.Count > 0 && !codes.IsMap)
        {
            foreach (var coded in settled)
            {
                if (coded.CodedAncestor is { } ancestor)
                {
                    report.Add("MQ9206", $"State '{PathOf(analysis, coded.State.Id)}' of process '{process.Name}' has a code, and so does '{PathOf(analysis, ancestor.Id)}' above it, but the process binds an attribute, which holds one code per configuration; remove one of the two codes.",
                        coded.Pointer + "/code", coded.State.Id);
                }
                else if (coded.UnderParallel)
                {
                    report.Add("MQ9206", $"State '{PathOf(analysis, coded.State.Id)}' of process '{process.Name}' has a code inside a region of a parallel state, but the process binds an attribute, which holds one code per configuration; give the parallel state, or a state above it, the code instead.",
                        coded.Pointer + "/code", coded.State.Id);
                }
            }
        }

        // Under an enum binding the member is the stored value, so a code is optional (MQ9207 still checks one that is given). Under any other
        // binding one finding per binding names the states without a code, at the binding where the other binding findings are.
        var needing = bindings.Where(b => b.Type.NeedsCodes).ToList();
        if (needing.Count == 0)
            return;
        var missing = LifecycleBindings.Uncoded(analysis, codes).Where(n => analysis.Reachable.Contains(n.Id)).Select(n => n.Path).ToList(); // else MQ9003
        if (missing.Count == 0)
            return;
        const int Shown = 5;
        var names = missing.Count <= Shown ? string.Join(", ", missing) : string.Join(", ", missing.Take(Shown)) + " and " + N(missing.Count - Shown) + " more";
        var count = missing.Count == 1 ? "1 state of process '" + process.Name + "' has" : N(missing.Count) + " states of process '" + process.Name + "' have";
        foreach (var binding in needing)
        {
            report.Add("MQ9210", $"{count} no code, which binding '{binding.Subject.Name}.{binding.Attribute.Name}' needs: {names}; give each a code, or run Sync codes.",
                binding.Pointer, binding.EntryId ?? process.Id);
        }
    }

    private static string PathOf(ProcessAnalysis analysis, string stateId) => analysis.Find(stateId)?.Path ?? stateId;

    private static string SubjectLabel(ModelSnapshot model, ProcessSubject entry) =>
        entry.Stereotype is { } key ? "stereotype '" + key + "'"
        : model.Get<Entity>(entry.Entity ?? "") is { } entity ? "entity '" + entity.Name + "'" : "'" + entry.Entity + "'";

    // MQ9202 to MQ9205, MQ9207 and MQ9208 for one subject's binding, at its pointer (and, for a subjects entry, under the entry's id); MQ9207 at
    // each code that does not fit. A binding that can hold a state joins bindings, for what the lifecycle's bindings ask together.
    private static void CheckBinding(ValidationContext context, Process process, ProcessAnalysis analysis, ProcessCodes codes, SubjectRef subject, string boundAttribute,
        string pointer, string? entryId, List<(BindingType Type, SubjectRef Subject, ModelAttribute Attribute, string Pointer, string? EntryId)> bindings, Report report)
    {
        var model = context.Model;
        if (subject.Find(boundAttribute) is not { } attribute)
        {
            report.Add("MQ9202", subject.Stereotype is null
                ? $"The bound attribute of process '{process.Name}' is not an attribute of entity '{subject.Name}' (own, inherited or from a stereotype); bind one of the subject's attributes that can hold the state."
                : $"The bound attribute of process '{process.Name}' is not an attribute of stereotype '{subject.Name}'; a stereotype subject binds one of the stereotype's own attributes that can hold the state.", pointer, entryId);
            return;
        }

        var type = LifecycleBindings.Classify(model, attribute);
        if (type.Kind == BindingKind.Unsupported)
        {
            report.Add("MQ9202", $"The bound attribute '{attribute.Name}' of process '{process.Name}' {type.Why}; bind a single-valued attribute of '{subject.Name}' typed by an enum, a reference type, or a string or integer type.", pointer, entryId);
            return;
        }

        // Two lifecycles may not bind one attribute for an entity both list by name, or a stereotype both list (decision 11,
        // stereotype-sets-design.md decision 8); a base or derived entity, or covering through a stereotype, does not count. Two lifecycles in
        // the one-subject form already disagree on the entity's one lifecycle (MQ9201), and stay as they were.
        foreach (var other in LifecycleBindings.Of(model).ByAttribute.GetValueOrDefault(attribute.Id, []))
        {
            if (ReferenceEquals(other.Process, process) || string.Equals(other.Process.Id, process.Id, StringComparison.Ordinal)
                || !(subject.Stereotype is { } key ? string.Equals(other.Stereotype, key, StringComparison.Ordinal) : string.Equals(other.Entity, subject.Id, StringComparison.Ordinal))
                || (process.UsesOneSubjectForm && other.Process.UsesOneSubjectForm))
            {
                continue;
            }

            report.Add("MQ9202", $"Processes '{process.Name}' and '{other.Process.Name}' both bind attribute '{attribute.Name}' for {subject.Label}, which both list, and one attribute holds one lifecycle's state; bind another attribute in one of them.", pointer, entryId);
        }

        bindings.Add((type, subject, attribute, pointer, entryId));
        switch (type.Kind)
        {
            case BindingKind.Enum:
                CheckEnumBinding(context, process, analysis, codes, subject, attribute, type.Enum!, pointer, entryId, report);
                return;
            case BindingKind.Reference:
                CheckReferenceCodes(context, process, codes, subject, attribute, type.Reference!, report);
                break;
            default:
                CheckPlainCodes(process, codes, subject, attribute, type.Plain!, report);
                break;
        }

        CheckInitialCode(process, analysis, codes, attribute, pointer, entryId, report);
        CheckUncoveredValues(context, process, codes, subject, attribute, type, pointer, entryId, report);
    }

    // MQ9203 to MQ9205 and MQ9207 for an enum binding (decision 14): states match the enum's members by name, as before codes, and a root
    // state's code names its member by one of the member's own identifiers.
    private static void CheckEnumBinding(ValidationContext context, Process process, ProcessAnalysis analysis, ProcessCodes codes, SubjectRef subject, ModelAttribute attribute,
        EnumType enumType, string pointer, string? entryId, Report report)
    {
        var model = context.Model;
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
            report.Add("MQ9203", $"The members of enum '{enumType.Name}' differ from the states of process '{process.Name}' ({Names(states)}): {string.Join("; ", parts)}. Sync the enum from the process.", pointer, entryId);
        }

        // Subjects of one lifecycle may share the enum without warning about each other (decision 15).
        var own = process.Subjects.Select(s => s.BoundAttribute).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var others = model.ReferencesTo(enumType.Id)
            .Where(r => r.JsonPointer.EndsWith("/type/ref", StringComparison.Ordinal) && !own.Contains(r.FromId))
            .Select(r => AttributeLabel(model, r)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (others.Count > 0)
            report.Add("MQ9204", $"Enum '{enumType.Name}', bound by process '{process.Name}', is also used by {Names(others.Select(o => "'" + o + "'"))}: syncing it from the process changes them too. Give the lifecycle its own enum if they should not follow.", pointer, entryId);

        if (analysis.InitialOf(null) is { Type: not (StateType.History or StateType.Choice) } initial
            && !(attribute.Default is { ValueKind: JsonValueKind.String } value && string.Equals(value.GetString(), initial.State.Name, StringComparison.Ordinal)))
        {
            var current = attribute.Default is { } d ? $"is {d.GetRawText()}" : "is not set";
            report.Add("MQ9205", $"The default of bound attribute '{attribute.Name}' {current}, but process '{process.Name}' starts in '{initial.State.Name}'; set the default to '{initial.State.Name}'.", pointer, entryId);
        }

        // A code names the same member as the state's name: the member's key, its integer value or its name, compared as codes are. The
        // enum maps root states only, so a nested state's code has no member to name.
        foreach (var coded in codes.Coded)
        {
            if (coded.State.Type is StateType.History or StateType.Choice || analysis.Find(coded.State.Id) is not { } node)
                continue; // MQ9206
            var code = ProcessCodes.Text(coded.State.Code!.Value);
            if (node.Parent is not null)
            {
                report.Add("MQ9207", $"State '{node.Path}' of process '{process.Name}' has code {code}, but enum '{enumType.Name}', which attribute '{attribute.Name}' of {subject.Label} binds, holds root states only, by name; move the code to root state '{node.Path.Split('.')[0]}', or remove it.",
                    coded.Pointer + "/code", coded.State.Id);
                continue;
            }

            if (enumType.Members.FirstOrDefault(m => string.Equals(m.Name, coded.State.Name, StringComparison.Ordinal)) is not { } member)
                continue; // MQ9203
            if (string.Equals(member.Code, coded.Key, StringComparison.Ordinal) || string.Equals(member.Name, coded.Key, StringComparison.Ordinal)
                || (member.Value is { } number && string.Equals(number.ToString(CultureInfo.InvariantCulture), coded.Key, StringComparison.Ordinal)))
            {
                continue;
            }

            var identifiers = new List<string>();
            if (member.Code is { } key)
                identifiers.Add("'" + key + "'");
            if (member.Value is { } v)
                identifiers.Add(v.ToString(CultureInfo.InvariantCulture));
            identifiers.Add("'" + member.Name + "'");
            report.Add("MQ9207", $"State '{node.Path}' of process '{process.Name}' has code {code}, but member '{member.Name}' of enum '{enumType.Name}', which attribute '{attribute.Name}' of {subject.Label} binds, is {Names(identifiers)}; write one of those as the code.",
                coded.Pointer + "/code", coded.State.Id);
        }
    }

    // MQ9207 for a reference binding (decision 16): every code is a live code of the type; a former code is reported with its replacement.
    private static void CheckReferenceCodes(ValidationContext context, Process process, ProcessCodes codes, SubjectRef subject, ModelAttribute attribute, ReferenceType type,
        Report report)
    {
        foreach (var coded in codes.Coded)
        {
            if (coded.State.Type is StateType.History or StateType.Choice)
                continue; // MQ9206
            var value = coded.State.Code!.Value;
            if (!context.CodeMissing(type.Id, value))
                continue;
            var where = $"State '{coded.State.Name}' of process '{process.Name}' has code {ProcessCodes.Text(value)}";
            var binding = $"reference type '{type.Name}', which attribute '{attribute.Name}' of {subject.Label} binds";
            if (context.ReferenceData.ReplacedByOf(type.Id, value) is { } rowId)
            {
                var current = ReferenceDataRules.CodeOfRow(context, rowId);
                report.Add("MQ9207", current is not null
                        ? $"{where}, a former code of {binding}; write '{current}', the code of the row that replaced it."
                        : $"{where}, a former code of {binding}; write the code of row {rowId}, which replaced it.",
                    coded.Pointer + "/code", coded.State.Id);
            }
            else
            {
                report.Add("MQ9207", $"{where}, which is not a code of {binding}; write one of its codes, or add a row with it.", coded.Pointer + "/code", coded.State.Id);
            }
        }
    }

    // MQ9207 for a plain status (decision 16): every code fits the attribute's type, length, pattern and allowedValues, as a default would.
    private static void CheckPlainCodes(Process process, ProcessCodes codes, SubjectRef subject, ModelAttribute attribute, EffectiveType type,
        Report report)
    {
        foreach (var coded in codes.Coded)
        {
            if (coded.State.Type is StateType.History or StateType.Choice)
                continue; // MQ9206
            var value = coded.State.Code!.Value;
            if (AttributeRules.DefaultMismatch(type, attribute, value) is not { } reason)
                continue;
            report.Add("MQ9207", $"State '{coded.State.Name}' of process '{process.Name}' has code {ProcessCodes.Text(value)}, which does not fit attribute '{attribute.Name}' of {subject.Label} ({type.Describe()}), which the process binds: {reason}; write a code that fits it.",
                coded.Pointer + "/code", coded.State.Id);
        }
    }

    // MQ9205 for a binding by code: the bound attribute's default is the code of the configuration the process starts in, when it has one
    // (MQ9210 reports a missing one).
    private static void CheckInitialCode(Process process, ProcessAnalysis analysis, ProcessCodes codes, ModelAttribute attribute, string pointer, string? entryId, Report report)
    {
        var node = analysis.InitialOf(null);
        while (node is { Type: StateType.Compound } && codes.Find(node.Id) is null)
            node = analysis.InitialOf(node);
        if (node is null or { Type: StateType.History or StateType.Choice } || codes.CodedStateOf(node.Id) is not { Code: { } code } coded)
            return;
        if (attribute.Default is { } d && string.Equals(ReferenceDataIndex.CodeKey(d), ReferenceDataIndex.CodeKey(code), StringComparison.Ordinal))
            return;
        var current = attribute.Default is { } given ? $"is {given.GetRawText()}" : "is not set";
        report.Add("MQ9205", $"The default of bound attribute '{attribute.Name}' {current}, but process '{process.Name}' starts in '{PathOf(analysis, coded.Id)}', whose code is {ProcessCodes.Text(code)}; set the default to {ProcessCodes.Text(code)}.",
            pointer, entryId);
    }

    // MQ9208 (decision 17): the values a binding can hold that no state's code covers, once per binding: the attribute's allowedValues when
    // it has them (else a scalar type's), else a reference type's stored rows. Rows a script fills are not judged, and a lifecycle with no
    // codes yet is left to MQ9210.
    private static void CheckUncoveredValues(ValidationContext context, Process process, ProcessCodes codes, SubjectRef subject, ModelAttribute attribute, BindingType type,
        string pointer, string? entryId, Report report)
    {
        // While the lifecycle has no codes, MQ9210 already asks one of each state it rests in; listing every value as well adds nothing.
        var covered = codes.Coded.Where(c => c.State.Type is not (StateType.History or StateType.Choice)).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        if (covered.Count == 0)
            return;
        var values = new List<string>();
        var allowed = attribute.Validation?.AllowedValues is { Count: > 0 } own ? own : type.Plain?.Scalar?.Validation?.AllowedValues ?? [];
        if (allowed.Count > 0)
        {
            foreach (var value in allowed)
            {
                if (ReferenceDataIndex.CodeKey(value) is { } key && !values.Contains(key, StringComparer.Ordinal))
                    values.Add(key);
            }
        }
        else if (type.Reference is { } reference && context.ReferenceData.Codes.TryGetValue(reference.Id, out var rows))
        {
            values.AddRange(rows.Keys.Order(StringComparer.Ordinal));
        }

        var uncovered = values.Where(v => !covered.Contains(v)).ToList();
        if (uncovered.Count == 0)
            return;
        var shown = Names(uncovered.Take(10).Select(v => "'" + v + "'")) + (uncovered.Count > 10 ? $" and {N(uncovered.Count - 10)} more" : "");
        var from = allowed.Count > 0 ? "allowedValues of attribute '" + attribute.Name + "'" : "the rows of reference type '" + type.Reference!.Name + "'";
        report.Add("MQ9208", $"No state of process '{process.Name}' has {(uncovered.Count == 1 ? "the value" : "the values")} {shown} from {from} of {subject.Label}, which the process binds; add a state with each code, or narrow the attribute's allowedValues.",
            pointer, entryId);
    }

    // MQ9206 (lifecycle-design.md decision 1): codes unique within the process, of one kind, and none on a history or choice state. Codes
    // need no binding, so every process that runs is judged.
    private static void CheckCodes(Process process, ProcessCodes codes, Report report)
    {
        if (codes.Coded.Count == 0)
            return;
        foreach (var coded in codes.OnHistoryOrChoice)
        {
            report.Add("MQ9206", $"State '{coded.State.Name}' of process '{process.Name}' is a {(coded.State.Type == StateType.History ? "history" : "choice")} state, which nothing rests in, so it has no code; remove its code.",
                coded.Pointer + "/code", coded.State.Id);
        }

        foreach (var (first, second) in codes.Duplicates)
        {
            report.Add("MQ9206", $"States '{first.State.Name}' and '{second.State.Name}' of process '{process.Name}' share code {ProcessCodes.Text(second.State.Code!.Value)} (1 and \"1\" are one code); give each state its own code.",
                second.Pointer + "/code", second.State.Id);
        }

        foreach (var coded in codes.OtherKind)
        {
            report.Add("MQ9206", $"State '{coded.State.Name}' of process '{process.Name}' has {(coded.IsInteger ? "an integer" : "a string")} code, but the process's first code is {(coded.IsInteger ? "a string" : "an integer")}; write every code of a process as strings, or every one as integers.",
                coded.Pointer + "/code", coded.State.Id);
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
            report.Add("MQ9201", $"Entity '{entity.Name}' names process '{process.Name}' as its lifecycle, but that process is {(process.Use == ProcessUse.Library ? "a library" : "an orchestration")}; name a lifecycle process, or set its use to lifecycle.", "/lifecycle");
        else if (process.Subject is null && process.SubjectList is { } entries)
        {
            // The subjects form (lifecycle-design.md decision 8): the named lifecycle lists the entity, or a base of it, or a stereotype it or
            // a base carries (stereotype-sets-design.md section 5).
            var chain = context.Ancestors(entity).Select(a => a.Id).Prepend(entity.Id).ToHashSet(StringComparer.Ordinal);
            if (!entries.Any(e => e.Entity is { } listed && chain.Contains(listed)) && !SubjectCoverage.Governs(context.Model, process, entity))
                report.Add("MQ9201", $"Entity '{entity.Name}' names process '{process.Name}' as its lifecycle, but that process does not list it, or an entity it derives from, in its subjects; add the entity to the process's subjects, or name another lifecycle.", "/lifecycle");
        }
        else if (!string.Equals(process.Subject, entity.Id, StringComparison.Ordinal))
            report.Add("MQ9201", $"Entity '{entity.Name}' names process '{process.Name}' as its lifecycle, but that process's subject is {(process.Subject is not null && context.Model.Get<Entity>(process.Subject) is { } other ? "entity '" + other.Name + "'" : "not this entity")}; set the process's subject to this entity, or name its own lifecycle.", "/lifecycle");
    }

    // MQ9106, MQ9601, MQ9602, MQ9605.
    private static void CheckActor(ValidationContext context, Actor actor, Report report)
    {
        var model = context.Model;
        var membership = ActorMembership.Of(model);
        // A process names it, an actor from the object is the members of it, or a scenario's participant stands for it or holds it
        // (actors-design.md decision 6).
        // A right that names it in its actors or except counts too, a named right included (rights-design.md section 5).
        bool Named(string id) => model.ReferencesTo(id).Any(r => model.Get<Process>(r.FromElementId) is not null || model.Get<Right>(r.FromElementId) is not null
            || (model.Get<Actor>(r.FromElementId) is { Relative.MembersOf: { } holder } && string.Equals(holder, id, StringComparison.Ordinal))
            || (model.Get<Scenario>(r.FromElementId) is not null && r.JsonPointer.StartsWith("/participants/", StringComparison.Ordinal)));
        // An actor counts as used when a process names it, one of its members at any depth, or an actor it is a member of at any depth
        // (actors-design.md decision 6). The message is today's unless the actor has memberships or members.
        if (!Named(actor.Id) && !membership.AllMembers(actor.Id).Any(Named) && !membership.AllMemberOf(actor.Id).Any(Named))
        {
            report.Add("MQ9106", membership.MemberOf(actor.Id).Count > 0 || membership.Members(actor.Id).Count > 0
                ? $"Actor '{actor.Name}' is not referenced by any process (event actors, gate signers or human tasks), directly, through its members or through the actors it is a member of; use it in a process or remove it."
                : $"Actor '{actor.Name}' is not referenced by any process (event actors, gate signers or human tasks); use it in a process or remove it.", "");
        }

        if (actor.Relative is null)
            CheckMemberships(model, membership, actor, report);
        CheckRelative(model, actor, report);
        if (actor.Type == ActorType.System)
        {
            var others = model.All<Actor>().Where(a => a.Type == ActorType.System && !string.Equals(a.Id, actor.Id, StringComparison.Ordinal))
                .Select(a => "'" + a.Name + "'").Order(StringComparer.Ordinal).ToList();
            if (others.Count > 0)
                report.Add("MQ9605", $"Actor '{actor.Name}' is the Automatic actor, as {(others.Count == 1 ? "is" : "are")} {Names(others)}; a project has at most one. Change the type of all but one, for example to external-system.", "/type");
        }
    }

    // MQ9601 and MQ9602 (actors-design.md decisions 4 and 5): what an actor's memberOf gets wrong.
    private static void CheckMemberships(ModelSnapshot model, ActorMembership membership, Actor actor, Report report)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cycle = null;
        for (var i = 0; i < actor.MemberOf.Count; i++)
        {
            var target = actor.MemberOf[i];
            var pointer = "/memberOf/" + N(i);
            if (!seen.Add(target))
            {
                report.Add("MQ9601", $"Actor '{actor.Name}' lists {ActorName(model, target)} twice in memberOf; remove the repeated entry.", pointer);
                continue;
            }

            if (string.Equals(target, actor.Id, StringComparison.Ordinal))
            {
                report.Add("MQ9601", $"Actor '{actor.Name}' names itself in memberOf; an actor already stands for itself. Remove the entry.", pointer);
                continue;
            }

            if (model.Get<Actor>(target) is not { } holder)
                continue; // MQ2001, MQ2002
            if (holder.Relative is not null)
            {
                report.Add("MQ9601", $"Actor '{actor.Name}' is a member of '{holder.Name}', which is worked out from the object and so has no members; remove the entry, or name a role, group or organization that stands for itself.", pointer);
                continue;
            }

            if (!ActorTypes.HoldsMembers(holder.Type))
            {
                report.Add("MQ9601", $"Actor '{actor.Name}' is a member of '{holder.Name}', which is {TypeText(holder.Type)}; only a role, a group or an organization has members. Remove the entry, or name a role, group or organization.", pointer);
                continue;
            }

            if (cycle is null && membership.AllMemberOf(target).Contains(actor.Id) && CyclePath(model, membership, actor.Id, target) is { } path)
            {
                cycle = path;
                report.Add("MQ9602", $"Memberships form a cycle: {path}; an actor cannot be a member of itself. Remove one of these memberships.", pointer);
            }
        }
    }

    // The shortest way back from target to the actor along counted memberships, taken in file order: "A -> B -> A" by name.
    private static string? CyclePath(ModelSnapshot model, ActorMembership membership, string actorId, string target)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal) { [target] = null };
        var queue = new Queue<string>([target]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in membership.MemberOf(current))
            {
                if (string.Equals(next, actorId, StringComparison.Ordinal))
                {
                    var chain = new List<string> { actorId };
                    for (string? step = current; step is not null; step = previous[step])
                        chain.Add(step);
                    chain.Add(actorId);
                    chain.Reverse(1, chain.Count - 2);
                    return string.Join(" -> ", chain.Select(id => model.Get<Actor>(id)?.Name ?? id));
                }

                if (previous.TryAdd(next, current))
                    queue.Enqueue(next);
            }
        }

        return null;
    }

    private static string TypeText(ActorType type) => type switch
    {
        ActorType.Person => "a person",
        ActorType.ExternalSystem => "an external system",
        ActorType.System => "the system actor",
        ActorType.Role => "a role",
        ActorType.Group => "a group",
        _ => "an organization",
    };

    private static string ActorName(ModelSnapshot model, string id) =>
        model.Get<Actor>(id) is { } actor ? "'" + actor.Name + "'" : "'" + id + "'";

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
        report.Add("MQ9014", $"Process '{process.Name}' refers to {kind} '{NameOf(other, id)}' of process '{other.Name}'; a process refers only to its own states, events, guards, actions and invokes, and a library's guards and actions. Declare it in this process, or move it to a library.", pointer, elementId);
    }

    private static Process? OtherProcessOwner(ValidationContext context, Process process, string id, string kind)
    {
        if (!context.Model.TryGetEntry(id, out var entry) || !SubElementKinds.Contains(entry.Kind) || !string.Equals(entry.Kind, kind, StringComparison.Ordinal)
            || string.Equals(entry.OwnerId, process.Id, StringComparison.Ordinal))
            return null;
        // A library's guards and actions are there to be used; gatePolicy has its own rule (MQ9021).
        return context.Model.Get<Process>(entry.OwnerId) is { } owner && !(owner.Use == ProcessUse.Library && kind is "guard" or "action") ? owner : null;
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
