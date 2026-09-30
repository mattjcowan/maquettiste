using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Processes, actors and scenarios (phase-3-design.md section 4.3). The state tree, document order, paths, depth, initial children,
/// history defaults and transition priority come from the interpreter's <see cref="StatechartModel"/>, so templates see the chart
/// the interpreter runs. Runs after the conceptual layer is finished (a lifecycle reads its subject's flattened attributes).
/// </summary>
internal sealed partial class ResolveRun
{
    private const string ProcessKind = "k:process";
    private const string ActorKind = "k:actor";
    private const string ScenarioKind = "k:scenario";

    private readonly Dictionary<string, RProcess> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RActor> _actors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcessLookup> _processLookups = new(StringComparer.Ordinal);
    private List<RProcess> _processOrder = [];
    private List<RActor> _actorOrder = [];
    private List<RScenario> _scenarioOrder = [];

    /// <summary>A process's nodes by id, for its scenarios' references.</summary>
    private sealed class ProcessLookup
    {
        public Dictionary<string, RState> States { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, REvent> Events { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RGuard> Guards { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RInvoke> Invokes { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RMeaning> Meanings { get; } = new(StringComparer.Ordinal);

        /// <summary>Context attribute names by id.</summary>
        public Dictionary<string, string> Context { get; } = new(StringComparer.Ordinal);

        /// <summary>Payload attribute names by id (every event's).</summary>
        public Dictionary<string, string> Payload { get; } = new(StringComparer.Ordinal);

        /// <summary>Gate audit attribute names by id (every gate's): a gated event's step carries them in its payload.</summary>
        public Dictionary<string, string> Audit { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>What actors are referenced by, gathered in process order.</summary>
    private sealed class ActorUses
    {
        public List<RProcess> Processes { get; } = [];

        public List<REvent> Events { get; } = [];

        public List<RGate> Gates { get; } = [];
    }

    /// <summary>Resolves actors, processes and scenarios, the actors' uses and the entities' lifecycles.</summary>
    private void ResolveProcesses()
    {
        foreach (var actor in Model.All<Actor>())
        {
            Ct.ThrowIfCancellationRequested();
            var r = new RActor();
            FillCommon(r, actor, DepsOf(r)); // an actor is in no package
            r.Type = ResolutionValues.Kebab(actor.Type);
            _actors.TryAdd(actor.Id, r);
            Register(r);
        }

        _actorOrder = [.. _actors.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal)];

        // Shells first: an invoke names another process.
        var sources = new Dictionary<string, Process>(StringComparer.Ordinal);
        foreach (var process in Model.All<Process>())
        {
            var r = new RProcess();
            FillCommon(r, process, DepsOf(r));
            r.Package = PackageOf(process.Package);
            r.Use = ResolutionValues.Kebab(process.Use);
            if (_processes.TryAdd(process.Id, r))
                sources[process.Id] = process;
            Register(r);
        }

        _processOrder = Sorted(_processes.Values);
        var uses = new Dictionary<string, ActorUses>(StringComparer.Ordinal);
        foreach (var r in _processOrder)
        {
            Ct.ThrowIfCancellationRequested();
            BuildProcess(sources[r.Id], r, uses);
        }

        ResolveScenarios();

        foreach (var actor in _actorOrder)
        {
            var own = uses.GetValueOrDefault(actor.Id);
            // A process or scenario that starts or stops naming the actor changes its referrers.
            IReadOnlyList<string> keys = [Keys.Referrers(actor.Id)];
            actor.Processes = new RList<RProcess>(own?.Processes ?? [], keys);
            actor.Events = new RList<REvent>(own?.Events ?? [], keys);
            actor.Gates = new RList<RGate>(own?.Gates ?? [], keys);
        }

        foreach (var (id, entity) in _entitySources)
        {
            if (entity.Lifecycle is not { } lifecycle || !_entities.TryGetValue(id, out var r) || r.Id.Length == 0)
                continue;
            r.Lifecycle = _processes.GetValueOrDefault(lifecycle);
            DepsOf(r).Add(ProcessKind); // the named process appearing or disappearing
        }
    }

    private void BuildProcess(Process process, RProcess r, Dictionary<string, ActorUses> uses)
    {
        var chart = StatechartModel.Get(process, null);
        var lookup = new ProcessLookup();
        _processLookups[process.Id] = lookup;
        var deps = DepsOf(r);

        if (process.Subject is { } subjectId && _entities.TryGetValue(subjectId, out var subject) && subject.Id.Length > 0)
        {
            r.Subject = subject;
            deps.Element(subjectId);
            if (process.BoundAttribute is { } boundId)
            {
                r.BoundAttribute = subject.Attributes.FirstOrDefault(a => string.Equals(a.Id, boundId, StringComparison.Ordinal));
                r.BoundEnum = r.BoundAttribute?.Type.Enum;
                if (r.BoundEnum is { } enumType)
                    deps.Element(enumType.Id);
            }
        }

        // Every node of the process reads its file, and the subject and bound enum (a bound state's member).
        var baseKeys = new DependencySet(Keys).Element(process.Id);
        if (r.Subject is { } s)
            baseKeys.Element(s.Id);
        if (r.BoundEnum is { } e)
            baseKeys.Element(e.Id);
        var nodeKeys = baseKeys.ToList();
        var actorListKeys = new DependencySet(Keys).AddRange(nodeKeys).Add(ActorKind).ToList();

        T Node<T>(T node, string id) where T : RProcessNode
        {
            node.Id = id;
            DepsOf(node).AddRange(nodeKeys);
            Register(node);
            return node;
        }

        RList<RActor> ActorsOf(IEnumerable<string> ids, DependencySet owner)
        {
            var list = new List<RActor>();
            foreach (var id in ids)
            {
                owner.Element(id);
                if (_actors.TryGetValue(id, out var actor) && !list.Contains(actor))
                    list.Add(actor);
            }

            return new RList<RActor>(list, actorListKeys);
        }

        List<RAttribute> Attributes(IEnumerable<ModelAttribute> attributes, IResolvedObject owner, Dictionary<string, string>? names)
        {
            var list = StableByOrder(attributes.DistinctBy(a => a.Id, StringComparer.Ordinal), a => a.Order)
                .Select(a => MakeAttribute(a, owner, r.Package, null, false, null, process.Id)).ToList();
            SetOrder(list);
            foreach (var attribute in list)
            {
                Register(attribute);
                names?.TryAdd(attribute.Id, attribute.Name);
            }

            return list;
        }

        r.Context = new RList<RAttribute>(Attributes(process.Context, r, lookup.Context), nodeKeys);

        var events = new List<REvent>();
        foreach (var source in process.Events)
        {
            var ev = Node(new REvent { Name = source.Name }, source.Id);
            FillNode(ev, source.DisplayName ?? source.Name, source.Description, source.Stereotypes, source.Properties);
            ev.Payload = new RList<RAttribute>(Attributes(source.Payload, ev, lookup.Payload), nodeKeys);
            ev.Actors = ActorsOf(source.Actors, DepsOf(ev));
            if (lookup.Events.TryAdd(source.Id, ev))
                events.Add(ev);
        }

        var guards = new List<RGuard>();
        foreach (var source in process.Guards)
        {
            var guard = Node(new RGuard { Name = source.Name, Expression = source.Expression, IsStub = source.Expression is null }, source.Id);
            FillNode(guard, source.DisplayName ?? source.Name, source.Description, [], ImmutableDictionary<string, JsonElement>.Empty);
            if (lookup.Guards.TryAdd(source.Id, guard))
                guards.Add(guard);
        }

        var actions = new List<RAction>();
        var actionsById = new Dictionary<string, RAction>(StringComparer.Ordinal);
        foreach (var source in process.Actions)
        {
            var action = Node(new RAction { Name = source.Name, Expression = source.Expression, IsStub = source.Expression is null }, source.Id);
            FillNode(action, source.DisplayName ?? source.Name, source.Description, [], ImmutableDictionary<string, JsonElement>.Empty);
            action.Raises = new RList<REvent>(source.Raises.Select(id => lookup.Events.GetValueOrDefault(id)).OfType<REvent>(), nodeKeys);
            if (actionsById.TryAdd(source.Id, action))
                actions.Add(action);
        }

        RList<RAction> ActionsOf(IEnumerable<string> ids) => new(ids.Select(id => actionsById.GetValueOrDefault(id)).OfType<RAction>(), nodeKeys);

        // States in document order, from the interpreter's chart.
        var states = new Dictionary<ChartState, RState>(ReferenceEqualityComparer.Instance);
        foreach (var cs in chart.States)
        {
            var state = Node(new RState { Name = cs.Name, Path = cs.Path, Type = ResolutionValues.Kebab(cs.Type), Depth = cs.Depth }, cs.Id);
            var source = cs.State!;
            FillNode(state, source.DisplayName ?? source.Name, source.Description, source.Stereotypes, source.Properties);
            states[cs] = state;
            lookup.States.TryAdd(cs.Id, state);
        }

        RState? StateOf(ChartState? cs) => cs is not null && states.TryGetValue(cs, out var state) ? state : null;

        var invokes = new List<RInvoke>();
        foreach (var cs in chart.States)
        {
            var state = states[cs];
            var source = cs.State!;
            state.Parent = cs.Parent is { IsRoot: false } parent ? states[parent] : null;
            state.Children = new RList<RState>(cs.Children.Select(c => states[c]), nodeKeys);
            state.Initial = cs.Type == StateType.Compound ? StateOf(cs.InitialChild) : null;
            state.History = cs.Type == StateType.History ? ResolutionValues.Kebab(source.History) : null;
            state.DefaultTarget = cs.Type == StateType.History ? StateOf(cs.DefaultTarget) : null;
            state.Entry = ActionsOf(source.Entry);
            state.Exit = ActionsOf(source.Exit);
            state.IsFinal = cs.Type == StateType.Final;
            state.IsAtomic = cs.IsLeaf && cs.Type != StateType.Choice;
            state.RegionIndex = cs.Parent is { Type: StateType.Parallel } region ? region.Children.IndexOf(cs) : null;
            var own = new List<RInvoke>();
            foreach (var source2 in source.Invoke)
            {
                var invoke = Node(new RInvoke { Name = source2.Name, Type = ResolutionValues.Kebab(source2.Type), State = state }, source2.Id);
                FillNode(invoke, source2.DisplayName ?? source2.Name, source2.Description, [], ImmutableDictionary<string, JsonElement>.Empty);
                var invokeDeps = DepsOf(invoke);
                if (source2.Process is { } sub)
                {
                    invokeDeps.Element(sub);
                    invoke.Process = _processes.GetValueOrDefault(sub);
                }

                invoke.Actors = ActorsOf(source2.Actors, invokeDeps);
                own.Add(invoke);
                if (lookup.Invokes.TryAdd(source2.Id, invoke))
                    invokes.Add(invoke);
            }

            state.Invoke = new RList<RInvoke>(own, nodeKeys);
        }

        // Transitions in priority order, with the editor's edge label.
        var names = new LabelNames(process);
        var transitions = new List<RTransition>();
        var bySource = new Dictionary<RState, List<RTransition>>(ReferenceEqualityComparer.Instance);
        var gates = new List<RGate>();
        foreach (var ct in chart.Transitions)
        {
            var t = ct.Transition;
            var label = ProcessText.Label(t, names);
            var transition = Node(new RTransition
            {
                Source = states[ct.Source],
                Targets = new RList<RState>(ct.Targets.Select(x => states[x]), nodeKeys),
                Trigger = ResolutionValues.Kebab(t.Trigger),
                Event = t.Event is null ? null : lookup.Events.GetValueOrDefault(t.Event),
                After = t.After,
                AfterMs = ProcessText.Milliseconds(t.After),
                AfterTicks = ProcessText.Ticks(t.After),
                Invoke = t.Invoke is null ? null : lookup.Invokes.GetValueOrDefault(t.Invoke),
                Guard = t.Guard is null ? null : lookup.Guards.GetValueOrDefault(t.Guard),
                GuardMissing = t.Guard is not null && !lookup.Guards.ContainsKey(t.Guard),
                Actions = ActionsOf(t.Actions),
                External = t.External,
                Label = label,
                IsTargetless = t.Targets.Count == 0,
            }, t.Id);
            FillNode(transition, t.DisplayName ?? label, t.Description, t.Stereotypes, t.Properties);
            if (t.Gate is { } g)
            {
                var gate = Node(new RGate
                {
                    Name = g.Name,
                    Transition = transition,
                    Required = g.Required,
                    AllowRepeatSigner = g.AllowRepeatSigner,
                    ReasonRequired = g.ReasonRequired,
                }, g.Id);
                FillNode(gate, g.DisplayName ?? g.Name, g.Description, [], ImmutableDictionary<string, JsonElement>.Empty);
                var gateDeps = DepsOf(gate);
                gate.Signers = ActorsOf(g.Signers, gateDeps);
                gate.RequiredActors = ActorsOf(g.RequiredActors, gateDeps);
                var meanings = new List<RMeaning>();
                foreach (var m in g.Meanings)
                {
                    var meaning = Node(new RMeaning { Name = m.Name }, m.Id);
                    FillNode(meaning, m.DisplayName ?? m.Name, m.Description, [], ImmutableDictionary<string, JsonElement>.Empty);
                    meanings.Add(meaning);
                    lookup.Meanings.TryAdd(m.Id, meaning);
                }

                gate.Meanings = new RList<RMeaning>(meanings, nodeKeys);
                var audit = Attributes(g.AuditAttributes, gate, lookup.Audit);
                gate.AuditAttributes = new RList<RAttribute>(audit, nodeKeys);
                gate.Audit = new RList<RAuditField>(AuditFields(gate, audit, gateDeps.ToList()), nodeKeys);
                transition.Gate = gate;
                gates.Add(gate);
            }

            transitions.Add(transition);
            if (!bySource.TryGetValue(transition.Source, out var list))
                bySource[transition.Source] = list = [];
            list.Add(transition);
        }

        foreach (var state in states.Values)
            state.TransitionsOut = new RList<RTransition>(bySource.GetValueOrDefault(state) ?? [], nodeKeys);
        foreach (var ev in events)
            ev.Transitions = new RList<RTransition>(transitions.Where(t => ReferenceEquals(t.Event, ev) && t.Trigger == "event"), nodeKeys);
        foreach (var guard in guards)
            guard.UsedBy = new RList<RTransition>(transitions.Where(t => ReferenceEquals(t.Guard, guard)), nodeKeys);
        var ordered = chart.States.Select(cs => states[cs]).ToList();
        foreach (var action in actions)
        {
            IEnumerable<RProcessNode> users = transitions.Where(t => t.Actions.Contains(action));
            users = users.Concat(ordered.Where(st => st.Entry.Contains(action) || st.Exit.Contains(action)));
            action.UsedBy = new RList<RProcessNode>(users, nodeKeys);
        }

        r.Events = new RList<REvent>(events, nodeKeys);
        r.Guards = new RList<RGuard>(guards, nodeKeys);
        r.Actions = new RList<RAction>(actions, nodeKeys);
        r.States = new RList<RState>(chart.Root.Children.Select(cs => states[cs]), nodeKeys);
        r.AllStates = new RList<RState>(ordered, nodeKeys);
        r.AtomicStates = new RList<RState>(ordered.Where(st => st.IsAtomic), nodeKeys);
        var bound = chart.Root.Children.Where(cs => cs.Type is not (StateType.History or StateType.Choice)).Select(cs => states[cs]).ToList();
        r.BoundStates = new RList<RState>(bound, nodeKeys);
        if (process.Use == ProcessUse.Lifecycle && r.BoundEnum is { } boundEnum)
        {
            foreach (var state in bound)
                state.BoundMember = boundEnum.Members.FirstOrDefault(m => string.Equals(m.Name, state.Name, StringComparison.Ordinal));
        }

        r.Transitions = new RList<RTransition>(transitions, nodeKeys);
        r.Gates = new RList<RGate>(gates, nodeKeys);
        r.Invokes = new RList<RInvoke>(invokes, nodeKeys);
        r.Initial = StateOf(chart.Root.InitialChild);

        // Every actor the process names, and what names each one.
        var referenced = new List<RActor>();
        void Use(RActor actor, Action<ActorUses>? add = null)
        {
            if (!uses.TryGetValue(actor.Id, out var own))
                uses[actor.Id] = own = new ActorUses();
            if (!referenced.Contains(actor))
            {
                referenced.Add(actor);
                own.Processes.Add(r);
            }

            add?.Invoke(own);
        }

        foreach (var ev in events)
        {
            foreach (var actor in ev.Actors)
                Use(actor, u => u.Events.Add(ev));
        }

        foreach (var invoke in invokes)
        {
            foreach (var actor in invoke.Actors)
                Use(actor);
        }

        foreach (var gate in gates)
        {
            foreach (var actor in gate.Signers.Concat(gate.RequiredActors).Distinct())
                Use(actor, u => u.Gates.Add(gate));
        }

        r.Actors = new RList<RActor>(referenced.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal), actorListKeys);
    }

    /// <summary>The audit record shape of a gate (phase-3-design.md section 2.3), then one field per audit attribute.</summary>
    private static List<RAuditField> AuditFields(RGate gate, List<RAttribute> attributes, IReadOnlyList<string> keys)
    {
        var fields = new List<RAuditField>();
        void Add(string name, string type, bool required, string description, IReadOnlyList<string>? values = null, RAttribute? attribute = null) =>
            fields.Add(new RAuditField
            {
                Id = gate.Id + "/audit/" + name, Name = name, Type = type, Required = required, Description = description,
                Values = values ?? [], Attribute = attribute, Dependencies = keys,
            });

        Add("instance", "string", true, "The process instance's identity, supplied by the host.");
        Add("process", "id", true, "The process.");
        Add("gate", "id", true, "The gate.");
        Add("transition", "id", true, "The gated transition.");
        Add("sequence", "int64", true, "The record's order within the instance.");
        Add("signer", "string", false, "The signing person's identity, supplied by the host (never an actor id); none on a discarded record.");
        Add("actor", "id", false, "The actor the signer signs as (one of the gate's signers); none on a discarded record.");
        Add("meaning", "id", false, "The meaning of the signature; none on a discarded record.");
        Add("reason", "text", false, gate.ReasonRequired ? "The signature's reason: required on every signature." : "The signature's reason, when given.");
        Add("at", "datetimeoffset", true, "When the record was made, from the host's clock.");
        Add("outcome", "string", true, "signed, completed, refused (an attempt the gate did not count) or discarded (the source state was exited).",
            ["signed", "completed", "refused", "discarded"]);
        foreach (var attribute in attributes)
            Add(attribute.Name, attribute.Type.Builtin ?? attribute.Type.Name, attribute.Required, attribute.Description ?? "", null, attribute);
        return fields;
    }

    /// <summary>Fills the members common to process nodes.</summary>
    private void FillNode(RProcessNode node, string displayName, Description? description, IReadOnlyList<string> stereotypeKeys,
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        node.DisplayName = displayName;
        node.Description = description?.Text;
        if (stereotypeKeys.Count == 0 && properties.Count == 0)
            return;
        var deps = DepsOf(node);
        var stereotypes = new List<RStereotype>();
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in stereotypeKeys)
        {
            if (Model.GetStereotype(key) is not { } stereotype)
                continue;
            stereotypes.Add(new RStereotype
            {
                Key = stereotype.Key,
                Name = stereotype.DisplayName ?? (stereotype.Name.Length > 0 ? stereotype.Name : stereotype.Key),
                Icon = stereotype.Icon,
                Color = stereotype.Color,
            });
            deps.Element(stereotype.Id);
            foreach (var pair in stereotype.DefaultProperties)
                merged[pair.Key] = pair.Value;
        }

        foreach (var pair in properties)
            merged[pair.Key] = pair.Value;
        node.Stereotypes = stereotypes;
        node.Properties = ResolutionValues.PlainMap(merged);
    }

    private void ResolveScenarios()
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _processOrder.Count; i++)
            order[_processOrder[i].Id] = i;
        var scenarios = new List<RScenario>();
        foreach (var scenario in Model.All<Scenario>())
        {
            Ct.ThrowIfCancellationRequested();
            if (!_processes.TryGetValue(scenario.Process, out var process))
                continue; // a dangling process (MQ2001): the scenario has nothing to run
            var lookup = _processLookups[process.Id];
            var s = new RScenario();
            var deps = DepsOf(s);
            FillCommon(s, scenario, deps);
            deps.Element(process.Id);
            s.Package = process.Package; // a scenario's package is its process's
            s.Process = process;
            s.Outcome = ResolutionValues.Kebab(scenario.Outcome);
            s.Start = new RScenarioStart
            {
                Context = ByName(scenario.Start?.Context ?? ImmutableDictionary<string, JsonElement>.Empty, lookup.Context),
                At = scenario.Start?.At ?? ScenarioStart.DefaultStart,
            };
            var keys = deps.ToList();
            var steps = new List<RStep>();
            for (var i = 0; i < scenario.Steps.Count; i++)
            {
                var step = scenario.Steps[i];
                var r = new RStep
                {
                    Id = step.Id,
                    Index = i,
                    Input = ResolutionValues.Kebab(step.Input),
                    Event = step.Event is null ? null : lookup.Events.GetValueOrDefault(step.Event),
                    Invoke = step.Invoke is null ? null : lookup.Invokes.GetValueOrDefault(step.Invoke),
                    After = step.After,
                    AfterMs = ProcessText.Milliseconds(step.After),
                    AfterTicks = ProcessText.Ticks(step.After),
                    Actor = step.Actor is null ? null : _actors.GetValueOrDefault(step.Actor),
                    Signer = step.Signer,
                    Meaning = step.Meaning is null ? null : lookup.Meanings.GetValueOrDefault(step.Meaning),
                    Reason = step.Reason,
                    Payload = ByName(step.Payload, lookup.Payload, lookup.Audit, lookup.Context),
                    Assume = Assumptions(step.Assume, lookup.Guards),
                    Description = step.Description?.Text,
                };
                var stepDeps = DepsOf(r).AddRange(keys);
                if (step.Actor is { } actor)
                    stepDeps.Element(actor);
                if (step.Expect is { } expect)
                {
                    var expected = expect.States.Select(id => lookup.States.GetValueOrDefault(id)).OfType<RState>().ToList();
                    r.Expect = new RExpectation
                    {
                        Accepted = expect.Accepted,
                        States = new RList<RState>(expected, keys),
                        StatePaths = [.. expected.Select(st => st.Path)],
                        Context = ByName(expect.Context, lookup.Context),
                    };
                }

                steps.Add(r);
                Register(r);
            }

            s.Steps = new RList<RStep>(steps, keys);

            // The engine's replay, for templates that assert what the interpreter did (a refusal's reason, the audit outcomes): run
            // once per scenario on first read, so a run whose templates never read it pays nothing.
            var model = Model;
            var replay = new Lazy<IReadOnlyList<RStepTrace>>(() => Replay(model, scenario, lookup), LazyThreadSafetyMode.ExecutionAndPublication);
            foreach (var step in steps)
                step.Replay = replay;
            scenarios.Add(s);
            Register(s);
        }

        _scenarioOrder = [.. scenarios.OrderBy(s => order[s.Process.Id]).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal)];
        foreach (var process in _processOrder)
        {
            // Which scenarios a process has changes when a scenario file names it or stops naming it (its referrers).
            process.Scenarios = new RList<RScenario>(_scenarioOrder.Where(s => ReferenceEquals(s.Process, process)),
                [ScenarioKind, Keys.Referrers(process.Id)]);
        }
    }

    /// <summary>The engine interpreter's result of each step of a scenario that ran (empty when the replay could not start).</summary>
    private static IReadOnlyList<RStepTrace> Replay(ModelSnapshot model, Scenario scenario, ProcessLookup lookup)
    {
        using var runtime = new ProcessRuntime(model, 1, CancellationToken.None);
        if (ScenarioReplayer.Replay(scenario, runtime) is not { } replay)
            return [];
        return [.. replay.Steps.Select(t => new RStepTrace
        {
            Accepted = t.Accepted,
            Refusal = t.Refusal,
            Audit = [.. t.Audit.Select(a => ResolutionValues.Kebab(a.Outcome))],
            StatePaths = [.. t.Configuration.Select(id => lookup.States.TryGetValue(id, out var state) ? state.Path : id)],
            Final = t.Final,
        })];
    }

    /// <summary>Guard results keyed by guard name (the id when no guard has it), ordinal.</summary>
    private static IReadOnlyDictionary<string, object?> Assumptions(IReadOnlyDictionary<string, bool> values, Dictionary<string, RGuard> guards)
    {
        if (values.Count == 0)
            return ImmutableSortedDictionary<string, object?>.Empty;
        var map = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, value) in values)
            map.TryAdd(guards.TryGetValue(id, out var guard) ? guard.Name : id, value);
        return map.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    /// <summary>A map keyed by attribute id as plain values keyed by attribute name (the id when no attribute has it), ordinal.</summary>
    /// <remarks>The name maps are tried in order; a step's payload looks in the event payloads, then the gates' audit attributes, then
    /// the context.</remarks>
    private static IReadOnlyDictionary<string, object?> ByName(IReadOnlyDictionary<string, JsonElement> values, params Dictionary<string, string>[] names)
    {
        if (values.Count == 0)
            return ImmutableSortedDictionary<string, object?>.Empty;
        var map = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, value) in values)
        {
            var name = id;
            foreach (var n in names)
            {
                if (n.TryGetValue(id, out var found))
                {
                    name = found;
                    break;
                }
            }

            map.TryAdd(name, ResolutionValues.Plain(value));
        }

        return map.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }
}

/// <summary>The names a transition label uses, by id: events, guards, actions and invokes of the process.</summary>
internal sealed class LabelNames
{
    /// <summary>Collects the names of a process's nodes (a later duplicate id wins, as the editor's maps do).</summary>
    /// <param name="process">The process.</param>
    public LabelNames(Process process)
    {
        foreach (var e in process.Events)
            Events[e.Id] = e.Name;
        foreach (var g in process.Guards)
            Guards[g.Id] = g.Name;
        foreach (var a in process.Actions)
            Actions[a.Id] = a.Name;
        void Walk(IReadOnlyList<ProcessState> states)
        {
            foreach (var s in states)
            {
                foreach (var i in s.Invoke)
                    Invokes[i.Id] = i.Name;
                Walk(s.States);
            }
        }

        Walk(process.States);
    }

    /// <summary>Event names.</summary>
    public Dictionary<string, string> Events { get; } = new(StringComparer.Ordinal);

    /// <summary>Guard names.</summary>
    public Dictionary<string, string> Guards { get; } = new(StringComparer.Ordinal);

    /// <summary>Action names.</summary>
    public Dictionary<string, string> Actions { get; } = new(StringComparer.Ordinal);

    /// <summary>Invoke names.</summary>
    public Dictionary<string, string> Invokes { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Process text the templates and the editor share: the edge label and the short duration (the rules of the editor's
/// <c>canvas/statechart/chartModel.ts</c>, <c>edgeLabel</c> and <c>shortDuration</c>, which must give the same text), and the
/// milliseconds of a duration (the interpreter's fixed spans).
/// </summary>
internal static partial class ProcessText
{
    // The short unit of each group of the pattern, in order: years, months, weeks, days, hours, minutes, seconds.
    private static readonly string[] Units = ["y", "mo", "w", "d", "h", "m", "s"];

    /// <summary>A transition's label: <c>event [guard] / actions</c>, <c>after 30d</c>, <c>done</c>, <c>always</c>, <c>done: invoke</c> or <c>error: invoke</c>.</summary>
    /// <param name="t">The transition.</param>
    /// <param name="names">The process's names by id.</param>
    /// <returns>The label.</returns>
    public static string Label(ProcessTransition t, LabelNames names)
    {
        static string Name(Dictionary<string, string> map, string? id) => id is not null && map.TryGetValue(id, out var name) ? name : "?";
        var on = t.Trigger switch
        {
            TransitionTrigger.Event => Name(names.Events, t.Event),
            TransitionTrigger.After => "after " + ShortDuration(t.After),
            TransitionTrigger.InvokeDone => "done: " + Name(names.Invokes, t.Invoke),
            TransitionTrigger.InvokeError => "error: " + Name(names.Invokes, t.Invoke),
            _ => ResolutionValues.Kebab(t.Trigger),
        };
        var text = new StringBuilder(on);
        if (t.Guard is not null)
            text.Append(" [").Append(Name(names.Guards, t.Guard)).Append(']');
        if (t.Actions.Count > 0)
            text.Append(" / ").Append(string.Join(", ", t.Actions.Select(a => Name(names.Actions, a))));
        return text.ToString();
    }

    /// <summary>An ISO 8601 duration written short: <c>P30D</c> gives <c>30d</c>, <c>PT1H30M</c> <c>1h 30m</c>, <c>P1M</c> <c>1mo</c>; anything else as given.</summary>
    /// <param name="iso">The duration text.</param>
    /// <returns>The short text.</returns>
    public static string ShortDuration(string? iso)
    {
        if (string.IsNullOrEmpty(iso))
            return "";
        var trimmed = iso.Trim();
        var m = ShortPattern().Match(trimmed);
        if (!m.Success || trimmed == "P" || trimmed.EndsWith('T'))
            return iso;
        var parts = new List<string>();
        for (var i = 0; i < Units.Length; i++)
        {
            if (m.Groups[i + 1].Success && m.Groups[i + 1].Value.Length > 0)
                parts.Add(m.Groups[i + 1].Value + Units[i]);
        }

        return parts.Count > 0 ? string.Join(' ', parts) : iso;
    }

    /// <summary>The milliseconds of an ISO 8601 duration with the interpreter's fixed spans (a month 30 days, a year 365).</summary>
    /// <param name="iso">The duration text.</param>
    /// <returns>The milliseconds, or <see langword="null"/> when the text is not a duration.</returns>
    public static long? Milliseconds(string? iso) =>
        StatechartModel.ParseDuration(iso) is { } span ? span.Ticks / TimeSpan.TicksPerMillisecond : null;

    /// <summary>The exact length of an ISO 8601 duration in ticks (100 ns), as the interpreter's clock moves by it.</summary>
    /// <param name="iso">The duration text.</param>
    /// <returns>The ticks, or <see langword="null"/> when the text is not a duration.</returns>
    public static long? Ticks(string? iso) => StatechartModel.ParseDuration(iso) is { } span ? span.Ticks : null;

    // The editor's pattern, with ASCII digits (a script's \d is ASCII only).
    [GeneratedRegex(@"^P(?:([0-9]+(?:\.[0-9]+)?)Y)?(?:([0-9]+(?:\.[0-9]+)?)M)?(?:([0-9]+(?:\.[0-9]+)?)W)?(?:([0-9]+(?:\.[0-9]+)?)D)?(?:T(?:([0-9]+(?:\.[0-9]+)?)H)?(?:([0-9]+(?:\.[0-9]+)?)M)?(?:([0-9]+(?:\.[0-9]+)?)S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortPattern();
}
