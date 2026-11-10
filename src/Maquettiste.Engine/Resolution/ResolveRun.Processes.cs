using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Validation;

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
    private readonly Dictionary<string, IReadOnlyList<TypedExpr>> _conditionTrees = new(StringComparer.Ordinal);
    private List<RProcess> _processOrder = [];

    /// <summary>The library processes, by (package qualified name, name, id); <see cref="_processOrder"/> holds those that run.</summary>
    private List<RProcess> _libraryOrder = [];

    private readonly Dictionary<string, RGuard> _libraryGuards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RAction> _libraryActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RGate> _libraryGates = new(StringComparer.Ordinal);
    private readonly Dictionary<RProcessNode, List<RProcessNode>> _libraryUsers = new(ReferenceEqualityComparer.Instance);
    private List<RActor> _actorOrder = [];
    private List<RScenario> _scenarioOrder = [];

    // The processes that use an approval, by name then id (model.approval_lifecycles); empty in a project without approvals.
    private List<RProcess> _approvalLifecycles = [];

    /// <summary>The part 2 groups some lifecycle uses (signoff-design.md decision 32), for the model's <c>approvals_use_*</c> flags.</summary>
    private ApprovalGroups _approvalGroups;

    /// <summary>The part 2 groups of approvals (signoff-design.md decision 32).</summary>
    [Flags]
    private enum ApprovalGroups
    {
        None = 0,

        /// <summary>A gate with more than one stage, or a stage with a when.</summary>
        Rounds = 1,

        /// <summary>A named signature with delegateTo.</summary>
        Delegation = 2,

        /// <summary>A named signature with remindAfter or escalateAfter.</summary>
        Notices = 4,

        /// <summary>A named signature with reauthenticate.</summary>
        Password = 8,
    }

    /// <summary>The part 2 groups a lifecycle's approvals use, inline or through a gate policy (signoff-design.md decision 32).</summary>
    private ApprovalGroups ApprovalGroupsOf(Process process)
    {
        var groups = ApprovalGroups.None;
        foreach (var transition in process.Transitions)
        {
            if (ApprovalRules.GateOf(Model, transition) is not { Stages: { Count: > 0 } stages })
                continue;
            if (stages.Count > 1 || stages.Any(st => st.When is not null))
                groups |= ApprovalGroups.Rounds;
            foreach (var signature in stages.SelectMany(st => st.Signatures))
            {
                if (signature.DelegateTo.Count > 0)
                    groups |= ApprovalGroups.Delegation;
                if (signature.RemindAfter is not null || signature.EscalateAfter is not null)
                    groups |= ApprovalGroups.Notices;
                if (signature.Reauthenticate)
                    groups |= ApprovalGroups.Password;
            }
        }

        return groups;
    }

    /// <summary>A process's nodes by id, for its scenarios' references.</summary>
    private sealed class ProcessLookup
    {
        public Dictionary<string, RState> States { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, REvent> Events { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RGuard> Guards { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RInvoke> Invokes { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RMeaning> Meanings { get; } = new(StringComparer.Ordinal);

        /// <summary>The named signatures of the process's approvals and of the gate policies it uses, by id.</summary>
        public Dictionary<string, RNamedSignature> Signatures { get; } = new(StringComparer.Ordinal);

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

        public List<RNamedSignature> Signatures { get; } = [];

        /// <summary>The referrers keys of the library gates the actor signs: a process that starts naming one changes the actor's lists.</summary>
        public List<string> LibraryGateKeys { get; } = [];
    }

    /// <summary>
    /// The actors' memberships (actors-design.md section 6). Every actor gets its lists, empty without memberships, so a template that
    /// reads one renders again once a membership appears: <c>member_of</c> keys on the actor's file and the files it names,
    /// <c>all_member_of</c> on each file along the chain and the files they name, <c>members</c> on the actor's file and its referrers
    /// (a file that starts naming it in memberOf), and <c>all_members</c> on the actor's file and the referrers of it and each member.
    /// </summary>
    private void ResolveMemberships()
    {
        var membership = Maquettiste.Engine.Processes.ActorMembership.Of(Model);
        IReadOnlyList<string> Named(string id) => Model.Get<Actor>(id)?.MemberOf ?? [];
        List<RActor> ByName(IEnumerable<string> ids) =>
            [.. ids.Select(id => _actors.GetValueOrDefault(id)).OfType<RActor>().OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal)];
        foreach (var actor in _actorOrder)
        {
            var id = actor.Id;
            actor.MemberOf = new RList<RActor>([.. membership.MemberOf(id).Select(t => _actors.GetValueOrDefault(t)).OfType<RActor>()],
                [Keys.Element(id), .. Named(id).Select(Keys.Element)]);
            actor.Members = new RList<RActor>(ByName(membership.Members(id)), [Keys.Element(id), Keys.Referrers(id)]);
            var chain = membership.AllMemberOf(id).Prepend(id).ToList();
            actor.AllMemberOf = new RList<RActor>(ByName(membership.AllMemberOf(id)),
                new DependencySet(Keys).AddRange(chain.Select(Keys.Element)).AddRange(chain.SelectMany(Named).Select(Keys.Element)).ToList());
            actor.AllMembers = new RList<RActor>(ByName(membership.AllMembers(id)),
                new DependencySet(Keys).Add(Keys.Element(id)).AddRange(membership.AllMembers(id).Prepend(id).Select(Keys.Referrers)).ToList());
        }
    }

    /// <summary>
    /// Actors worked out from the object (actors-design.md section 6): each with <c>relative</c> gets its form, the ids of its path as the
    /// model resolves them (ends, then the declaring attribute, a base's for an inherited one), and the role, group or organization it is
    /// the members of. A relative path keys on the actor's file (its own keys) and on the relations and entities it passes through.
    /// </summary>
    private void ResolveRelatives()
    {
        foreach (var actor in Model.All<Actor>())
        {
            if (actor.Relative is not { } relative || !_actors.TryGetValue(actor.Id, out var r))
                continue;
            var deps = DepsOf(r);
            var path = new List<RObject>();
            foreach (var id in relative.Steps)
            {
                if (_byId.GetValueOrDefault(id) is REnd end)
                {
                    path.Add(end);
                    var owner = Model.TryGetEntry(id, out var entry) ? entry.OwnerId : null;
                    if (owner is not null)
                        deps.Element(owner);
                    // Through an end with several types the path depends on every covered entity (several-types-design.md decision 20).
                    if (end.Entity is { } far)
                        deps.Element(far.Id);
                    else if (owner is not null && RelationSource(owner)?.Ends.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)) is { } source)
                        AddCoverageKeys(source, deps);
                }
                else if (DeclaredAttribute(id) is { } attribute)
                {
                    path.Add(attribute);
                    if (Model.TryGetEntry(id, out var entry))
                        deps.Element(entry.OwnerId);
                }
            }

            r.Relative = new RRelative
            {
                Form = relative.Form,
                Path = path,
                MembersOf = relative.MembersOf is { } holder ? _actors.GetValueOrDefault(holder) : null,
            };
            if (relative.MembersOf is { } named)
                deps.Element(named);
        }
    }

    /// <summary>Resolves actors, processes and scenarios, the actors' uses and the entities' lifecycles.</summary>
    private void ResolveProcesses()
    {
        foreach (var actor in Model.All<Actor>())
        {
            Ct.ThrowIfCancellationRequested();
            var r = new RActor();
            FillCommon(r, actor, DepsOf(r));
            r.Package = PackageOf(actor.Package);
            r.Type = ResolutionValues.Kebab(actor.Type);
            _actors.TryAdd(actor.Id, r);
            Register(r);
        }

        _actorOrder = [.. _actors.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal)];
        ResolveMemberships();
        ResolveRelatives();
        ResolveNamedRights(); // before processes: a lifecycle's rights use them

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

        _processOrder = Sorted(_processes.Values.Where(p => sources[p.Id].Use != ProcessUse.Library));
        _libraryOrder = Sorted(_processes.Values.Where(p => sources[p.Id].Use == ProcessUse.Library));
        var uses = new Dictionary<string, ActorUses>(StringComparer.Ordinal);

        // Libraries first: a using process's transitions point at their guards, actions and gates.
        foreach (var r in _libraryOrder.Concat(_processOrder))
        {
            Ct.ThrowIfCancellationRequested();
            BuildProcess(sources[r.Id], r, uses);
        }

        // The processes that use an approval, inline or through a gate policy, by name then id (model.approval_lifecycles).
        _approvalLifecycles = [.. _processOrder.Where(p => ApprovalRules.UsesApproval(Model, sources[p.Id]))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)];
        foreach (var lifecycle in _approvalLifecycles)
            _approvalGroups |= ApprovalGroupsOf(sources[lifecycle.Id]);

        // Named trigger points (part 2) resolve once every process is built and before the scenarios, whose expectations and traces name them.
        if (NamedTriggerPoints.Of(Model).Any)
        {
            ResolveNamedTriggerPoints();
            _namedTriggerPointsResolved = true;
        }

        // An in_state names another process's states, which exist once every process is built.
        DrainInStateFills();
        FinishRights(sources);

        // Which transitions and states use a library's member changes when another process starts or stops naming it.
        foreach (var guard in _libraryGuards.Values)
            guard.UsedBy = new RList<RTransition>(LibraryUsers(guard).OfType<RTransition>(), [Keys.Referrers(guard.Id)]);
        foreach (var action in _libraryActions.Values)
            action.UsedBy = new RList<RProcessNode>(LibraryUsers(action), [Keys.Referrers(action.Id)]);
        foreach (var gate in _libraryGates.Values)
            gate.UsedBy = new RList<RTransition>(LibraryUsers(gate).OfType<RTransition>(), [Keys.Referrers(gate.Id)]);

        ResolveScenarios();

        foreach (var actor in _actorOrder)
        {
            var own = uses.GetValueOrDefault(actor.Id);
            // A process or scenario that starts or stops naming the actor changes its referrers.
            IReadOnlyList<string> keys = [Keys.Referrers(actor.Id), .. own?.LibraryGateKeys ?? []];
            actor.Processes = new RList<RProcess>(own?.Processes ?? [], keys);
            actor.Events = new RList<REvent>(own?.Events ?? [], keys);
            actor.Gates = new RList<RGate>(own?.Gates ?? [], keys);
            actor.Signatures = new RList<RNamedSignature>(own?.Signatures ?? [], keys);
            actor.Rights = RightsOf(actor);
        }

        foreach (var (id, entity) in _entitySources)
        {
            if (entity.Lifecycle is not { } lifecycle || !_entities.TryGetValue(id, out var r) || r.Id.Length == 0)
                continue;
            r.Lifecycle = _processes.GetValueOrDefault(lifecycle);
            DepsOf(r).Add(ProcessKind); // the named process appearing or disappearing
        }

        ResolveEntityLifecycles(sources);
        FinishTriggerPoints();
        FinishRevisions();
    }

    /// <summary>
    /// <c>entity.lifecycles</c> (lifecycle-design.md section 7): the lifecycles that list the entity or a base of it, in either form, by
    /// name then id. The list is keyed on the process kind and on the referrers of the entity and of each base, since a lifecycle may
    /// list a base; only a template that reads it pays for those keys. While some entry names a stereotype it also keys on which
    /// stereotypes the processes name and on the carrier set of each named stereotype (stereotype-sets-design.md section 6).
    /// </summary>
    private void ResolveEntityLifecycles(Dictionary<string, Process> sources)
    {
        var governing = new Dictionary<string, List<RProcess>>(StringComparer.Ordinal);
        foreach (var r in _processOrder)
        {
            if (r.Use != "lifecycle")
                continue;
            foreach (var entity in sources[r.Id].Subjects.Select(s => s.Entity).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                if (!governing.TryGetValue(entity, out var list))
                    governing[entity] = list = [];
                list.Add(r);
            }
        }

        // A lifecycle that covers an entity through a stereotype (stereotype-sets-design.md section 6), after decision 4's precedence; only
        // while some entry names a stereotype, so a model without one keeps its keys.
        var coverage = SubjectCoverage.Of(Model);
        List<(RProcess Resolved, Process Source)> through = coverage.Any
            ? [.. _processOrder.Where(p => p.Use == "lifecycle" && SubjectCoverage.NamesStereotype(sources[p.Id])).Select(p => (p, sources[p.Id]))]
            : [];
        // A process that starts or stops naming a stereotype, or names another, changes the naming key; an entity (or a base) that starts
        // or stops carrying a named stereotype changes that stereotype's carrier-set key. The naming key is kept even in a model with no
        // stereotype entry: its hash counts only processes that name one, so it never changes there, and the model's first stereotype
        // entry still reaches every entity's lifecycles.
        List<string> throughKeys = coverage.Any ? [SubjectCoverage.NamingKey, .. coverage.Keys.Select(SubjectCoverage.CarrierSetKey)] : [SubjectCoverage.NamingKey];

        foreach (var id in _entitySources.Keys)
        {
            if (!_entities.TryGetValue(id, out var r) || r.Id.Length == 0)
                continue;
            var keys = new List<string> { ProcessKind };
            var found = new List<RProcess>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = r; current is not null && seen.Add(current.Id); current = current.Base)
            {
                keys.Add(Keys.Referrers(current.Id));
                if (governing.TryGetValue(current.Id, out var list))
                    found.AddRange(list);
            }

            if (throughKeys.Count > 0)
            {
                if (_entitySources.TryGetValue(id, out var source))
                {
                    foreach (var (resolved, process) in through)
                    {
                        if (SubjectCoverage.EntryOf(Model, process, source) is { Stereotype: not null })
                            found.Add(resolved);
                    }
                }

                keys = [.. new DependencySet(Keys).AddRange(keys).AddRange(throughKeys).ToList()];
            }

            r.Lifecycles = new RList<RProcess>(found.Distinct().OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal), keys);
        }
    }

    /// <summary>
    /// What decides the carriers a process's stereotype entries govern (stereotype-sets-design.md section 6): the carrier set of each
    /// stereotype it names (which entities carry it, in which order, and whether the key names a stereotype), and the file of each carrier
    /// and of its bases, since an entry naming a carrier or a base wins over the stereotype entry.
    /// </summary>
    private IReadOnlyList<string> CarrierKeys(Process process)
    {
        var keys = new DependencySet(Keys);
        var coverage = SubjectCoverage.Of(Model);
        foreach (var entry in process.Subjects)
        {
            if (entry.Stereotype is not { } key)
                continue;
            keys.Add(SubjectCoverage.CarrierSetKey(key));
            foreach (var carrier in coverage.CarriersOf(key))
            {
                foreach (var id in SubjectCoverage.Chain(Model, carrier))
                    keys.Element(id);
            }
        }

        return keys.ToList();
    }

    /// <summary>
    /// What decides the stereotypes one entity carries itself, besides its own file: its package and their parents (their defaults), and
    /// every stereotype (implies).
    /// </summary>
    private IEnumerable<string> CarryingIds(Entity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = entity.Package; id is not null && seen.Add(id); id = Model.Get<Package>(id)?.Parent)
            yield return id;
        foreach (var stereotype in Model.All<Stereotype>())
            yield return stereotype.Id;
    }

    /// <summary><see cref="CarryingIds"/> as <c>e:</c> keys.</summary>
    private IEnumerable<string> CarryingKeys(Entity entity) => CarryingIds(entity).Select(Keys.Element);

    /// <summary>
    /// A subject entry that names a stereotype (stereotype-sets-design.md decision 12): the entry as written, with the stereotype's own bound
    /// and owner attributes and the carriers it governs; and, added to <paramref name="subjects"/> at the entry's place, one subject per
    /// governed carrier, by qualified name, with the id <c>&lt;entry id&gt;/&lt;entity id&gt;</c> and the carrier's copies of those attributes.
    /// </summary>
    private RStereotypeSubject ExpandStereotypeEntry(Process process, ProcessSubject entry, string key, List<RSubject> subjects, DependencySet deps)
    {
        var definition = _stereotypeDefinitions.GetValueOrDefault(key);
        var r = new RStereotypeSubject { Id = entry.Id, Stereotype = definition };
        if (definition is not null)
            deps.Element(definition.Id);
        RAttribute? Own(string? id) =>
            id is null ? null : definition?.Attributes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
        r.BoundAttribute = Own(entry.BoundAttribute);
        // The stereotype's own single-valued attribute (MQ9606); a stereotype has no relation ends.
        r.OwnerAttribute = Own(entry.Owner) is { Collection: false, Type.ValueObject: null } owner ? owner : null;
        var carriers = new List<REntity>();
        foreach (var carrier in SubjectCoverage.Governed(Model, process, entry))
        {
            if (!_entities.TryGetValue(carrier.Id, out var entity) || entity.Id.Length == 0)
                continue;
            carriers.Add(entity);
            deps.Element(entity.Id);
            var subject = new RSubject { Id = entry.Id + "/" + entity.Id, Entity = entity, Stereotype = definition };
            if (r.BoundAttribute is { } bound)
            {
                subject.BoundAttribute = entity.Attributes.FirstOrDefault(a => string.Equals(a.Id, bound.Id, StringComparison.Ordinal));
                subject.BoundEnum = subject.BoundAttribute?.Type.Enum;
                if (subject.BoundEnum is { } enumType)
                    deps.Element(enumType.Id);
                subject.BoundReferenceType = subject.BoundAttribute?.Type.ReferenceType;
                if (subject.BoundReferenceType is { } referenceType)
                    deps.Element(referenceType.Id);
            }

            if (r.OwnerAttribute is { } ownerAttribute)
                subject.OwnerAttribute = entity.Attributes.FirstOrDefault(a => string.Equals(a.Id, ownerAttribute.Id, StringComparison.Ordinal));
            subjects.Add(subject);
        }

        r.Entities = new RList<REntity>(carriers, []);
        return r;
    }

    private IReadOnlyList<RProcessNode> LibraryUsers(RProcessNode member) => _libraryUsers.TryGetValue(member, out var users) ? users : [];

    /// <summary>The <c>in-state</c> nodes waiting for every process to be built, each with the work that fills it.</summary>
    private readonly List<Action> _inStateFills = [];

    /// <summary>
    /// What fills an <c>in-state</c> node of a process's guard or set, or of an operation's guard (object-effects-design.md section 6):
    /// queued, since the node may name the states of a process not built yet. <paramref name="holderDeps"/> takes the binding's keys.
    /// </summary>
    private Action<TypedExpr, RExpression> InStateFill(Process? process, Operation? operation, DependencySet holderDeps) =>
        (typed, node) => _inStateFills.Add(() => FillInState(typed, node, process, operation, holderDeps));

    /// <summary>Fills the queued <c>in-state</c> nodes.</summary>
    private void DrainInStateFills()
    {
        foreach (var fill in _inStateFills)
            fill();
        _inStateFills.Clear();
    }

    /// <summary>
    /// An <c>in-state</c> node (object-effects-design.md decision 11): its lifecycle, the states it names and the ends of its path, each list
    /// keyed on the file that decides it; and, when the lifecycle binds one attribute for every entity the node reaches, that attribute
    /// as its declaring entity (or stereotype) resolves it, its enum, and the stored values for which the node holds. The holder then also
    /// keys on that entity and that enum.
    /// </summary>
    private void FillInState(TypedExpr typed, RExpression node, Process? process, Operation? operation, DependencySet holderDeps)
    {
        if (typed.Lifecycle is not { } lifecycleId || Model.Get<Process>(lifecycleId) is not { } lifecycle)
            return;
        node.Process = _processes.GetValueOrDefault(lifecycleId);
        var states = _processLookups.GetValueOrDefault(lifecycleId)?.States;
        node.States = new RList<RState>(typed.States.Select(s => states?.GetValueOrDefault(s.Id)).OfType<RState>(), [Keys.Element(lifecycleId)]);
        var endKeys = new DependencySet(Keys);
        var ends = new List<REnd>();
        foreach (var navigation in typed.Through)
        {
            if (Model.TryGetEntry(navigation.Id, out var relation))
                endKeys.Element(relation.OwnerId);
            if (_byId.GetValueOrDefault(navigation.Id) is REnd end)
                ends.Add(end);
        }

        node.Through = new RList<REnd>(ends, endKeys.ToList());

        // One binding for every entity the node can reach: the entity at the end of the path, else each subject the site reads.
        string? boundId = null;
        foreach (var entity in ReachedEntities(typed, process, operation, lifecycle))
        {
            var id = SubjectCoverage.EntryOf(Model, lifecycle, entity)?.BoundAttribute;
            if (id is null || (boundId is not null && !string.Equals(boundId, id, StringComparison.Ordinal)))
                return;
            boundId = id;
        }

        if (boundId is null || DeclaredAttribute(boundId) is not { } bound)
            return;
        if (Model.TryGetEntry(boundId, out var holder))
            holderDeps.Element(holder.OwnerId);
        node.BoundAttribute = bound;
        node.Enum = bound.Type.Enum;
        if (node.Enum is { } enumType)
            holderDeps.Element(enumType.Id);
        node.Codes = InStateCodes(lifecycle, typed, node.Enum);
    }

    /// <summary>
    /// The entities an <c>in_state</c> can reach: the one at the end of its path (through an end with several types, each of its types the
    /// lifecycle governs); else the operation's subject; else each subject of the process, a stereotype entry's carriers included.
    /// </summary>
    private List<Entity> ReachedEntities(TypedExpr typed, Process? process, Operation? operation, Process lifecycle)
    {
        if (typed.Through.Count > 0)
        {
            // Through an end with several types: each of its types the lifecycle governs (several-types-design.md decision 15).
            var last = typed.Through[^1];
            if (Model.TryGetEntry(last.Id, out var owner)
                && _relationSources.GetValueOrDefault(owner.OwnerId)?.Ends.FirstOrDefault(e => string.Equals(e.Id, last.Id, StringComparison.Ordinal)) is { HasSeveralTypes: true } end)
                return [.. EndCoverage.TypesOf(Model, end).Where(t => SubjectCoverage.Governs(Model, lifecycle, t))];
            return last.Entity is { } id && Model.Get<Entity>(id) is { } reached ? [reached] : [];
        }
        if (operation is not null)
            return operation.Subject is { } subject && Model.Get<Entity>(subject) is { } entity ? [entity] : [];
        var entities = new List<Entity>();
        foreach (var entry in process?.Subjects ?? [])
        {
            if (entry.Entity is { } id)
            {
                if (Model.Get<Entity>(id) is { } entity)
                    entities.Add(entity);
            }
            else
            {
                entities.AddRange(SubjectCoverage.Governed(Model, process!, entry));
            }
        }

        return entities;
    }

    /// <summary>
    /// The stored values for which an <c>in_state</c> holds (object-effects-design.md decision 11), when the stored value decides it: under an
    /// enum binding, the members of the root-level states named, by name (an enum holds a root-level state); under a code binding whose
    /// codes form a map, each named state's code, or the codes of the coded states inside it when it has none and every state inside it is
    /// covered by one, in document order. Empty when a named state's presence cannot be read from the stored value.
    /// </summary>
    private static IReadOnlyList<object> InStateCodes(Process lifecycle, TypedExpr typed, REnum? enumType)
    {
        var values = new List<object>();
        if (enumType is not null)
        {
            foreach (var state in typed.States)
            {
                if (state.Path.Count != 1 || enumType.Members.FirstOrDefault(m => string.Equals(m.Name, state.Path[0], StringComparison.Ordinal)) is not { } member)
                    return [];
                if (!values.Contains(member))
                    values.Add(member);
            }

            return values;
        }

        var codes = ProcessCodes.Of(lifecycle);
        if (!codes.IsMap || !codes.IsSound)
            return [];
        var byId = new Dictionary<string, ProcessState>(StringComparer.Ordinal);
        Index(lifecycle.States);
        foreach (var state in typed.States)
        {
            // A coded state decides itself (a map has no coded state above it); one with no code needs no coded state above it and a code
            // on every path down from it.
            if (codes.Find(state.Id) is null && (codes.CodedStateOf(state.Id) is not null || !byId.TryGetValue(state.Id, out var source) || !Covered(source)))
                return [];
        }

        foreach (var id in typed.Inside)
        {
            if (codes.Find(id) is { } coded && ProcessCodes.Value(coded.State.Code) is { } value && !values.Contains(value))
                values.Add(value);
        }

        return values;

        void Index(IReadOnlyList<ProcessState> level)
        {
            foreach (var state in level)
            {
                byId[state.Id] = state;
                Index(state.States);
            }
        }

        bool Covered(ProcessState state)
        {
            var inside = state.States.Where(s => s.Type is not (StateType.History or StateType.Choice)).ToList();
            return inside.Count > 0 && inside.All(s => codes.Find(s.Id) is not null || Covered(s));
        }
    }

    private void BuildProcess(Process process, RProcess r, Dictionary<string, ActorUses> uses)
    {
        var chart = StatechartModel.Get(process, null);
        var libraryUses = ProcessLibraries.UsesOf(Model, process);
        var lookup = new ProcessLookup();
        _processLookups[process.Id] = lookup;
        var deps = DepsOf(r);

        // Every subject in one list, whatever the file's form (lifecycle-design.md decision 6); the one-subject form's entry has the
        // process's id. Subject, BoundAttribute and BoundEnum are set only when there is exactly one subject.
        var listed = process.Subjects;
        var subjects = new List<RSubject>();
        var stereotypeSubjects = new List<RStereotypeSubject>();
        foreach (var entry in listed)
        {
            if (entry.Stereotype is { } key)
            {
                stereotypeSubjects.Add(ExpandStereotypeEntry(process, entry, key, subjects, deps));
                continue;
            }

            if (entry.Entity is null || !_entities.TryGetValue(entry.Entity, out var entity) || entity.Id.Length == 0)
                continue;
            deps.Element(entry.Entity);
            var subject = new RSubject { Id = entry.Id, Entity = entity };
            if (entry.BoundAttribute is { } boundId)
            {
                subject.BoundAttribute = entity.Attributes.FirstOrDefault(a => string.Equals(a.Id, boundId, StringComparison.Ordinal));
                subject.BoundEnum = subject.BoundAttribute?.Type.Enum;
                if (subject.BoundEnum is { } enumType)
                    deps.Element(enumType.Id);
                subject.BoundReferenceType = subject.BoundAttribute?.Type.ReferenceType;
                if (subject.BoundReferenceType is { } referenceType)
                    deps.Element(referenceType.Id);
            }

            // The owner field (actors-design.md decision 10), only when MQ9606 has nothing to say about it.
            if (entry.Owner is { } ownerId && Model.Get<Entity>(entry.Entity) is { } modelEntity)
            {
                if (entity.Attributes.FirstOrDefault(a => string.Equals(a.Id, ownerId, StringComparison.Ordinal)) is { } owner)
                {
                    // Own, inherited, or from a stereotype the entity or a base carries (stereotype-sets-design.md decision 7).
                    var declared = owner.FromStereotype is not null
                        || ExprSites.AttributesOf(Model, modelEntity).Any(a => string.Equals(a.Id, ownerId, StringComparison.Ordinal));
                    subject.OwnerAttribute = !declared || owner.Collection || owner.Type.ValueObject is not null ? null : owner;
                }
                else if (Processes.RelativeActors.OwnerEnd(Model, modelEntity, ownerId) is { Max: MaxCardinality.One } && _byId.GetValueOrDefault(ownerId) is REnd ownerEnd)
                    subject.OwnerEnd = ownerEnd;
                if (subject.OwnerEnd is not null && Model.TryGetEntry(ownerId, out var ownerEntry))
                    deps.Element(ownerEntry.OwnerId);
            }

            subjects.Add(subject);
        }

        // With a stereotype entry, from the expanded count (stereotype-sets-design.md decision 12): one carrier gives them, as one entity does.
        if (stereotypeSubjects.Count > 0 ? subjects.Count == 1 : listed.Count == 1 && subjects.Count == 1)
        {
            r.Subject = subjects[0].Entity;
            r.BoundAttribute = subjects[0].BoundAttribute;
            r.BoundEnum = subjects[0].BoundEnum;
        }

        r.ListsSubjects = process.SubjectList is { Count: > 0 } && process.Subject is null;

        // Every node of the process reads its file, the subjects and bound enums (a bound state's member), and the libraries it names (a
        // transition's label holds a library guard's name).
        var baseKeys = new DependencySet(Keys).Element(process.Id);
        foreach (var use in libraryUses)
            baseKeys.Element(use.Library.Id);
        foreach (var subject in subjects)
        {
            baseKeys.Element(subject.Entity.Id);
            if (subject.BoundEnum is { } e)
                baseKeys.Element(e.Id);
            if (subject.BoundReferenceType is { } referenceType) // a reference binding's nodes read the type's file (section 7)
                baseKeys.Element(referenceType.Id);
        }

        foreach (var entry in stereotypeSubjects)
        {
            if (entry.Stereotype is { } definition)
                baseKeys.Element(definition.Id);
        }

        var nodeKeys = baseKeys.ToList();
        var subjectKeys = nodeKeys;
        if (stereotypeSubjects.Count > 0)
        {
            // Which entities carry a named stereotype (stereotype-sets-design.md section 6) decides the subjects and the one-subject fields.
            var carrierKeys = CarrierKeys(process);
            subjectKeys = new DependencySet(Keys).AddRange(nodeKeys).AddRange(carrierKeys).ToList();
            deps.AddRange(carrierKeys);
            foreach (var entry in stereotypeSubjects)
            {
                DepsOf(entry).AddRange(subjectKeys);
                entry.Entities = new RList<REntity>(entry.Entities, subjectKeys);
            }

            r.StereotypeSubjects = new RList<RStereotypeSubject>(stereotypeSubjects, subjectKeys);
        }

        foreach (var subject in subjects)
            DepsOf(subject).AddRange(subjectKeys);
        r.Subjects = new RList<RSubject>(subjects, subjectKeys);
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
        Dictionary<string, RAttribute>? readable = null;
        foreach (var source in process.Guards)
        {
            var guard = Node(new RGuard { Name = source.Name, Expression = source.Expression }, source.Id);
            FillNode(guard, source.DisplayName ?? source.Name, source.Description, source.Stereotypes, source.Properties);
            if (source.Condition is not null)
                guard.Condition = BuildCondition(source.Id, ExprSiteKind.Guard, DepsOf(guard));

            // A condition that does not type runs no more than a missing one does (simulation asks for an assume value, MQ9306).
            guard.IsStub = guard.Expression is null && guard.Condition is null;
            if (lookup.Guards.TryAdd(source.Id, guard))
                guards.Add(guard);
            if (process.Use == ProcessUse.Library)
                _libraryGuards.TryAdd(source.Id, guard);
        }

        // The members this process names from libraries follow its own, so a pack that walks a process's guards, actions and gates meets
        // every one its transitions name. Their own files are in the keys through each member; the process depends on them too.
        var libraries = new List<RProcess>();
        var ownGuards = guards.Count;
        foreach (var use in libraryUses)
        {
            if (_processes.TryGetValue(use.Library.Id, out var library) && !libraries.Contains(library))
                libraries.Add(library);
            deps.Element(use.Library.Id);
            if (use.Guard is { } shared && _libraryGuards.TryGetValue(shared.Id, out var guard) && lookup.Guards.TryAdd(shared.Id, guard))
                guards.Add(guard);
        }


        // A guard's condition or a set's value (next-phase-3-design.md section 3.1, object-effects-design.md section 6): typed in the
        // process's scope, its paths ending at the context's and payloads' attributes; what it names beyond the process (an enum, a seed, a
        // constraint, an in_state's lifecycle) joins the holder's keys. A tree that reads the object (lifecycle-design.md decision 20) ends
        // its paths there at the declaring entity's own attribute, a base's for an inherited one, so it has one target whichever subject
        // runs; it depends on every subject's entity and its bases, and, through Names, on the relations it follows and the entities they
        // lead to. An in_state node is filled once every process is built, and keys its holder on its binding.
        RExpression? BuildCondition(string siteId, ExprSiteKind kind, DependencySet holderDeps)
        {
            if (ExprResolution.Type(Model, process, siteId) is not { } typed)
                return null;
            readable ??= r.Context.Concat(events.SelectMany(ev => ev.Payload)).DistinctBy(a => a.Id, StringComparer.Ordinal)
                .ToDictionary(a => a.Id, StringComparer.Ordinal);
            var root = new ExprRoot(typed.Tree, typed.Typed, typed.Scope, kind,
                ExprResolution.ApiColumns(Model, siteId, Model.GetDocument(process.Id)?.Path));
            // A trigger point's site also reads the object through old. (trigger-points-design.md decision 14).
            var readsSubject = ExprSites.ReadsSubject(process, typed.Typed) || (kind is ExprSiteKind.Trigger or ExprSiteKind.TriggerSet && ReadsOld(typed.Typed));
            var readsStereotype = false;
            var built = ExprResolution.Build(root, m =>
            {
                if (AttributeTarget(readable, m.Id) is { } target)
                    return target;
                var declared = readsSubject ? DeclaredAttribute(m.Id) : null;
                readsStereotype |= declared?.Owner is RStereotypeDefinition;
                return declared;
            }, EnumMemberOf, InStateFill(process, null, holderDeps));
            var guardDeps = holderDeps;
            ExprResolution.AddNames(Model, typed.Typed, guardDeps);
            if (!readsSubject)
                return built;
            foreach (var entry in process.Subjects)
            {
                if (entry.Stereotype is { } key && Model.GetStereotype(key) is { } stereotype)
                    guardDeps.Element(stereotype.Id);
                if (entry.Entity is null || EntitySource(entry.Entity) is not { } entity)
                    continue;
                foreach (var level in Chain(entity))
                {
                    guardDeps.Element(level.Id);
                    // A stereotype's attribute is in scope while the entity or a base carries it: its packages' defaults and the stereotypes'
                    // implies decide that (stereotype-sets-design.md decision 7).
                    if (readsStereotype)
                        guardDeps.AddRange(CarryingKeys(level));
                }
            }

            return built;
        }

        // An action's sets (object-effects-design.md section 6): each attribute as its declaring entity resolves it and each value typed at a
        // set site. The list keys on the process's file and on the holders of the attributes set, whose names a template prints.
        RList<RSet> SetsOf(RAction action, ProcessAction source)
        {
            var sets = new List<RSet>(source.Sets.Count);
            var listKeys = new DependencySet(Keys).AddRange(nodeKeys);
            foreach (var entry in source.Sets)
            {
                var holderId = Model.TryGetEntry(entry.Attribute, out var holder) ? holder.OwnerId : null;
                var set = new RSet { Id = entry.Id, Action = action };
                var setDeps = DepsOf(set).AddRange(nodeKeys);
                if (holderId is not null)
                {
                    listKeys.Element(holderId);
                    setDeps.Element(holderId);
                }
                if (DeclaredAttribute(entry.Attribute) is not { } attribute || BuildCondition(entry.Id, ExprSiteKind.Set, setDeps) is not { } value)
                    continue;
                set.Attribute = attribute;
                set.Value = value;
                sets.Add(set);
                Register(set);
            }

            return new RList<RSet>(sets, listKeys.ToList());
        }

        var actions = new List<RAction>();
        var actionsById = new Dictionary<string, RAction>(StringComparer.Ordinal);
        foreach (var source in process.Actions)
        {
            var action = Node(new RAction { Name = source.Name, Expression = source.Expression, IsStub = source.Expression is null && source.Sets.Count == 0 }, source.Id);
            FillNode(action, source.DisplayName ?? source.Name, source.Description, source.Stereotypes, source.Properties);
            action.Raises = new RList<REvent>(source.Raises.Select(id => lookup.Events.GetValueOrDefault(id)).OfType<REvent>(), nodeKeys);
            if (source.Sets.Count > 0)
                action.Sets = SetsOf(action, source);
            if (actionsById.TryAdd(source.Id, action))
                actions.Add(action);
            if (process.Use == ProcessUse.Library)
                _libraryActions.TryAdd(source.Id, action);
        }

        var ownActions = actions.Count;
        foreach (var use in libraryUses)
        {
            if (use.Action is { } shared && _libraryActions.TryGetValue(shared.Id, out var action) && actionsById.TryAdd(shared.Id, action))
                actions.Add(action);
        }

        RList<RAction> ActionsOf(IEnumerable<string> ids) => new(ids.Select(id => actionsById.GetValueOrDefault(id)).OfType<RAction>(), nodeKeys);

        // States in document order, from the interpreter's chart.
        var states = new Dictionary<ChartState, RState>(ReferenceEqualityComparer.Instance);
        foreach (var cs in chart.States)
        {
            var state = Node(new RState { Name = cs.Name, Path = cs.Path, Type = ResolutionValues.Kebab(cs.Type), Depth = cs.Depth }, cs.Id);
            var source = cs.State!;
            state.Code = ProcessCodes.Value(source.Code);
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
                FillNode(invoke, source2.DisplayName ?? source2.Name, source2.Description, source2.Stereotypes, source2.Properties);
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

        RGate BuildGate(ProcessGate g)
        {
            var gate = Node(new RGate
            {
                Name = g.Name,
                Required = g.Required,
                AllowRepeatSigner = g.AllowRepeatSigner,
                ReasonRequired = g.ReasonRequired,
            }, g.Id);
            FillNode(gate, g.DisplayName ?? g.Name, g.Description, g.Stereotypes, g.Properties);
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
            // Empty on a gate without stages, keyed on the file all the same, so its first stage renders again what read them.
            gate.Stages = new RList<RGateStage>([], nodeKeys);
            gate.AllSignatures = new RList<RNamedSignature>([], nodeKeys);
            if (g.Stages is { Count: > 0 } stages)
                BuildStages(gate, g, stages);
            var audit = Attributes(g.AuditAttributes, gate, lookup.Audit);
            gate.AuditAttributes = new RList<RAttribute>(audit, nodeKeys);
            gate.Audit = new RList<RAuditField>(AuditFields(gate, audit, gateDeps.ToList()), nodeKeys);
            return gate;
        }

        // An approval's stages and named signatures (signoff-design.md section 7), keyed on the process's file, or the library's for a gate
        // policy, as the gate's other members are. A signature's when is typed at the approval site; a gate policy's has none (MQ8202). A
        // notBy transition waits for the process's transitions, and a notBy verb for model.verbs.
        var notByTransitions = new List<(RNotBy Entry, string Transition)>();
        void BuildStages(RGate gate, ProcessGate g, IReadOnlyList<GateStage> stages)
        {
            var built = new List<RGateStage>();
            var all = new List<RNamedSignature>();
            foreach (var st in stages)
            {
                var stage = Node(new RGateStage { Name = st.Name, Gate = gate, Round = st.Round }, st.Id);
                FillNode(stage, st.DisplayName ?? st.Name, st.Description, [], ImmutableDictionary<string, JsonElement>.Empty);
                // A stage's when is typed at the approval site, as a signature's is (part 2, decision 6); a gate policy's stage has none (MQ8202).
                if (st.When is not null && process.Use != ProcessUse.Library)
                    stage.When = BuildCondition(st.Id, ExprSiteKind.Approval, DepsOf(stage));
                var own = new List<RNamedSignature>();
                foreach (var ns in st.Signatures)
                {
                    var signature = Node(new RNamedSignature
                    {
                        Name = ns.Name, Gate = gate, Stage = stage, Required = ns.Required, AllowRepeatSigner = ns.AllowRepeatSigner,
                    }, ns.Id);
                    FillNode(signature, ns.DisplayName ?? ns.Name, ns.Description, [], ImmutableDictionary<string, JsonElement>.Empty);
                    var signatureDeps = DepsOf(signature);
                    signature.Signers = ActorsOf(ns.Signers, signatureDeps);
                    signature.RequiredActors = ActorsOf(ns.RequiredActors, signatureDeps);
                    // Part 2 (decisions 20 to 22): delegation, reminders, escalation and signing again with a password.
                    signature.DelegateTo = ActorsOf(ns.DelegateTo, signatureDeps);
                    signature.EscalateTo = ActorsOf(ns.EscalateTo, signatureDeps);
                    signature.RemindAfter = ns.RemindAfter;
                    signature.RemindAfterMs = ProcessText.Milliseconds(ns.RemindAfter);
                    signature.RemindAfterTicks = ProcessText.Ticks(ns.RemindAfter);
                    signature.EscalateAfter = ns.EscalateAfter;
                    signature.EscalateAfterMs = ProcessText.Milliseconds(ns.EscalateAfter);
                    signature.EscalateAfterTicks = ProcessText.Ticks(ns.EscalateAfter);
                    signature.Reauthenticate = ns.Reauthenticate;
                    if (ns.When is not null && process.Use != ProcessUse.Library)
                        signature.When = BuildCondition(ns.Id, ExprSiteKind.Approval, signatureDeps);
                    var entries = new List<RNotBy>(ns.NotBy.Count);
                    for (var k = 0; k < ns.NotBy.Count; k++)
                    {
                        var entry = new RNotBy { Id = ns.Id + "/not-by/" + k.ToString(System.Globalization.CultureInfo.InvariantCulture), Dependencies = nodeKeys, VerbKey = ns.NotBy[k].Verb, TransitionId = ns.NotBy[k].Verb is null ? ns.NotBy[k].Transition : null };
                        if (ns.NotBy[k].Verb is { } verbKey)
                        {
                            var known = entry;
                            _verbFills.Add((verbOf, vocabularyKeys) =>
                            {
                                known.Verb = verbOf(verbKey);
                                known.Dependencies = [.. nodeKeys, .. vocabularyKeys];
                            });
                        }
                        else if (ns.NotBy[k].Transition is { } transitionId)
                            notByTransitions.Add((entry, transitionId));
                        entries.Add(entry);
                    }

                    signature.NotBy = new RList<RNotBy>(entries, nodeKeys);
                    own.Add(signature);
                    lookup.Signatures.TryAdd(ns.Id, signature);
                }

                stage.Signatures = new RList<RNamedSignature>(own, nodeKeys);
                built.Add(stage);
                all.AddRange(own);
            }

            gate.Stages = new RList<RGateStage>(built, nodeKeys);
            gate.AllSignatures = new RList<RNamedSignature>(all, nodeKeys);
            gate.AllowSignerAcrossSignatures = g.AllowSignerAcrossSignatures;
        }

        // Transitions in priority order, with the editor's edge label.
        var names = new LabelNames(ProcessLibraries.Effective(Model, process));
        var transitions = new List<RTransition>();
        var bySource = new Dictionary<RState, List<RTransition>>(ReferenceEqualityComparer.Instance);
        var gates = new List<RGate>();
        foreach (var g in process.Gates)
        {
            var gate = BuildGate(g);
            gates.Add(gate);
            if (process.Use == ProcessUse.Library)
                _libraryGates.TryAdd(g.Id, gate);
        }

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
                NextRevision = t.NextRevision,
            }, t.Id);
            FillNode(transition, t.DisplayName ?? label, t.Description, t.Stereotypes, t.Properties);
            if (t.Gate is { } g)
            {
                var gate = BuildGate(g);
                gate.Transition = transition;
                gate.UsedBy = new RList<RTransition>([transition], nodeKeys);
                transition.Gate = gate;
                gates.Add(gate);
            }
            else if (t.GatePolicy is { } policy && _libraryGates.TryGetValue(policy, out var shared))
            {
                transition.Gate = shared;
                if (!gates.Contains(shared))
                {
                    gates.Add(shared);
                    foreach (var m in shared.Meanings)
                        lookup.Meanings.TryAdd(m.Id, m);
                    foreach (var a in shared.AuditAttributes)
                        lookup.Audit.TryAdd(a.Id, a.Name);
                    foreach (var signature in shared.AllSignatures)
                        lookup.Signatures.TryAdd(signature.Id, signature);
                }
            }

            transitions.Add(transition);
            if (!bySource.TryGetValue(transition.Source, out var list))
                bySource[transition.Source] = list = [];
            list.Add(transition);
        }

        foreach (var (entry, transitionId) in notByTransitions)
            entry.Transition = transitions.FirstOrDefault(t => string.Equals(t.Id, transitionId, StringComparison.Ordinal));
        foreach (var state in states.Values)
            state.TransitionsOut = new RList<RTransition>(bySource.GetValueOrDefault(state) ?? [], nodeKeys);
        foreach (var ev in events)
            ev.Transitions = new RList<RTransition>(transitions.Where(t => ReferenceEquals(t.Event, ev) && t.Trigger == "event"), nodeKeys);
        void UsedAcross(RProcessNode member, IEnumerable<RProcessNode> users)
        {
            if (!_libraryUsers.TryGetValue(member, out var all))
                _libraryUsers[member] = all = [];
            all.AddRange(users);
        }

        for (var i = 0; i < guards.Count; i++)
        {
            var guard = guards[i];
            var users = transitions.Where(t => ReferenceEquals(t.Guard, guard));
            if (i < ownGuards)
                guard.UsedBy = new RList<RTransition>(users, nodeKeys);
            else
                UsedAcross(guard, users);
        }

        var ordered = chart.States.Select(cs => states[cs]).ToList();
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            IEnumerable<RProcessNode> users = transitions.Where(t => t.Actions.Contains(action));
            users = users.Concat(ordered.Where(st => st.Entry.Contains(action) || st.Exit.Contains(action)));
            if (i < ownActions)
                action.UsedBy = new RList<RProcessNode>(users, nodeKeys);
            else
                UsedAcross(action, users);
        }

        foreach (var gate in gates.Where(g => g.Transition is null && process.Use != ProcessUse.Library))
            UsedAcross(gate, transitions.Where(t => ReferenceEquals(t.Gate, gate)));

        r.Events = new RList<REvent>(events, nodeKeys);
        r.Libraries = new RList<RProcess>(libraries.OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Id, StringComparer.Ordinal), nodeKeys);
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
        var codes = ProcessCodes.Of(process);
        r.HasCodes = codes.Coded.Count > 0;
        r.CodesMap = codes.IsMap && codes.IsSound;

        // Rights (rights-design.md section 6): only lifecycles hold them (MQ9901), and only lifecycles have a default of their own.
        if (process.Use == ProcessUse.Lifecycle)
        {
            r.RightsDefault = process.RightsDefault == RightAccess.Deny ? "deny" : "allow";
            if (process.Rights.Count > 0)
                r.Rights = BuildRights(process, r, nodeKeys, lookup.States, BuildCondition);

            // Trigger points (trigger-points-design.md section 7): only lifecycles hold them (MQ8101). Without any, both lists stay empty,
            // keyed on the process's file, so its first point renders again what read them.
            r.TriggerPoints = new RList<RTriggerPoint>([], nodeKeys);
            r.TriggerVerbs = new RList<RTriggerVerb>([], nodeKeys);
            if (process.TriggerPoints.Count > 0)
            {
                var transitionsById = new Dictionary<string, RTransition>(StringComparer.Ordinal);
                foreach (var t in transitions)
                    transitionsById.TryAdd(t.Id, t);
                BuildTriggerPoints(process, r, nodeKeys, lookup.States, transitionsById, BuildCondition);
            }
        }

        // Every actor the process names, and what names each one.
        var referenced = new List<RActor>();
        void Use(RActor actor, Action<ActorUses>? add = null)
        {
            if (!uses.TryGetValue(actor.Id, out var own))
                uses[actor.Id] = own = new ActorUses();
            if (!referenced.Contains(actor))
            {
                referenced.Add(actor);
                if (process.Use != ProcessUse.Library)
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
                Use(actor, u =>
                {
                    if (!u.Gates.Contains(gate))
                        u.Gates.Add(gate);
                    if (process.Use == ProcessUse.Library && gate.Transition is null)
                        u.LibraryGateKeys.Add(Keys.Referrers(gate.Id));
                });

            // An approval's signatures name their own signers and required actors (signoff-design.md section 7).
            foreach (var signature in gate.AllSignatures)
            {
                foreach (var actor in signature.Signers.Concat(signature.RequiredActors).Distinct())
                    Use(actor, u =>
                    {
                        if (!u.Gates.Contains(gate))
                            u.Gates.Add(gate);
                        if (!u.Signatures.Contains(signature))
                            u.Signatures.Add(signature);
                        if (process.Use == ProcessUse.Library && gate.Transition is null && !u.LibraryGateKeys.Contains(Keys.Referrers(gate.Id)))
                            u.LibraryGateKeys.Add(Keys.Referrers(gate.Id));
                    });
            }
        }

        r.Actors = new RList<RActor>(referenced.OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal), actorListKeys);
    }

    /// <summary>The audit record shape of a gate (phase-3-design.md section 2.3), then one field per audit attribute.</summary>
    private static List<RAuditField> AuditFields(RGate gate, List<RAttribute> attributes, IReadOnlyList<string> keys)
    {
        var fields = new List<RAuditField>();
        void Add(string name, string type, bool required, string? description, IReadOnlyList<string>? values = null, RAttribute? attribute = null) =>
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
        Add("actor", "id", false, gate.UsesStages
            ? "The actor the signer answers as (one of the named signature's signers); none on a discarded record."
            : "The actor the signer signs as (one of the gate's signers); none on a discarded record.");
        Add("meaning", "id", false, "The meaning of the signature; none on a discarded record.");
        Add("reason", "text", false, gate.ReasonRequired ? "The signature's reason: required on every signature." : "The signature's reason, when given.");
        Add("at", "datetimeoffset", true, "When the record was made, from the host's clock.");
        if (gate.UsesStages)
        {
            // An approval's records name the signature and stage, and gain the responses' outcomes (signoff-design.md decision 19).
            if (gate.AllSignatures.Any(s => s.DelegateTo.Count > 0))
            {
                // A signature that may be delegated adds the delegated outcome (part 2, decision 34); other approvals read as in part 1.
                Add("outcome", "string", true, "signed, completed, refused (an attempt the approval did not count), discarded (the source state was exited), "
                    + "rejected (a veto cleared the approval), ignored (a person abstained on a signature), withdrawn (a person took back a signature) "
                    + "or delegated (a person handed their part of a signature to someone else).",
                    ["signed", "completed", "refused", "discarded", "rejected", "ignored", "withdrawn", "delegated"]);
            }
            else
            {
                Add("outcome", "string", true, "signed, completed, refused (an attempt the approval did not count), discarded (the source state was exited), "
                    + "rejected (a veto cleared the approval), ignored (a person abstained on a signature) or withdrawn (a person took back a signature).",
                    ["signed", "completed", "refused", "discarded", "rejected", "ignored", "withdrawn"]);
            }
            Add("signature", "id", false, "The named signature the record is about; none on a record about the whole approval.");
            Add("stage", "id", false, "The stage of the signature; none when the record names no signature.");
            if (gate.AllSignatures.Any(s => s.DelegateTo.Count > 0))
            {
                // Where a signature may be delegated, the records name who answered on whose behalf and who a delegation went to (decision 34).
                Add("onBehalfOf", "string", false, "For an answer on behalf of someone who delegated the signature, the delegator's identity; none otherwise.");
                Add("delegateTo", "string", false, "For a delegation, the identity it went to; none otherwise.");
            }
        }
        else
        {
            Add("outcome", "string", true, "signed, completed, refused (an attempt the gate did not count) or discarded (the source state was exited).",
                ["signed", "completed", "refused", "discarded"]);
        }

        foreach (var attribute in attributes)
            Add(attribute.Name, attribute.Type.Builtin ?? attribute.Type.Name, attribute.Required, attribute.Description, null, attribute);
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
        // The effective set, as for every annotated object: implied stereotypes first, own ones after (a later default wins).
        foreach (var applied in StereotypesOf(stereotypeKeys))
        {
            stereotypes.Add(Summary(applied));
            deps.Element(applied.Stereotype.Id);
            foreach (var pair in applied.Stereotype.DefaultProperties)
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
            if (!order.ContainsKey(scenario.Process) || !_processes.TryGetValue(scenario.Process, out var process))
                continue; // a dangling process (MQ2001): the scenario has nothing to run
            var lookup = _processLookups[process.Id];
            var s = new RScenario();
            var deps = DepsOf(s);
            FillCommon(s, scenario, deps);
            deps.Element(process.Id);
            s.Package = PackageOf(scenario.Package) ?? process.Package; // its own file's package, else its process's
            s.Process = process;
            s.Outcome = ResolutionValues.Kebab(scenario.Outcome);
            s.Start = new RScenarioStart
            {
                Context = ByName(scenario.Start?.Context ?? ImmutableDictionary<string, JsonElement>.Empty, lookup.Context),
                At = scenario.Start?.At ?? ScenarioStart.DefaultStart,
            };
            var keys = deps.ToList();
            ResolveScenarioObjects(s, scenario, keys);
            var participants = ResolveParticipants(s, scenario, keys);
            var traced = TraceInputs(scenario);
            var rightsKeys = RightsReplayKeys(scenario);
            var triggerKeys = TriggerReplayKeys(scenario);
            var revisionKeys = RevisionReplayKeys(scenario);
            // A check the team writes, assumed by a word (trigger-points-design.md decision 26): its key, by trigger point id. Only a scenario
            // with such a word pays for it.
            var checkKeys = scenario.Steps.Any(st => st.Assume.Values.Any(v => v.Check is not null)) ? CheckKeys(scenario) : null;
            var runs = scenario.Participants.Count > 0 ? Model.Get<Process>(scenario.Process) : null;
            // A process that reads current_actor() (object-effects-design.md section 7): the identity comes from each step's signer, participant and
            // actor, and an automatic move's from the system actor, so every step reads the actors as a participant's step does.
            var readsActor = Model.Get<Process>(scenario.Process) is { } running && ReadsActor(running);
            Dictionary<string, string>? valueNames = null;
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
                    Response = ResolutionValues.Kebab(step.Response),
                    Signature = step.Signature is null ? null : lookup.Signatures.GetValueOrDefault(step.Signature),
                    DelegateTo = step.DelegateTo is null ? null : participants.GetValueOrDefault(step.DelegateTo),
                    OnBehalfOf = step.OnBehalfOf is null ? null : participants.GetValueOrDefault(step.OnBehalfOf),
                    Meaning = step.Meaning is null ? null : lookup.Meanings.GetValueOrDefault(step.Meaning),
                    Reason = step.Reason,
                    Payload = ByName(step.Payload, lookup.Payload, lookup.Audit, lookup.Context),
                    Assume = Assumptions(step.Assume, lookup.Guards, checkKeys?.Names),
                    Description = step.Description?.Text,
                    End = step.End is null ? null : _byId.GetValueOrDefault(step.End) as REnd,
                    Object = step.Object is null ? null : s.Objects.FirstOrDefault(o => string.Equals(o.Id, step.Object, StringComparison.Ordinal)),
                };
                var stepDeps = DepsOf(r).AddRange(keys);
                if (step.Verb is { } verbKey)
                {
                    // A verb step (rights-design.md decision 25): its verb waits for model.verbs.
                    var known = r;
                    _verbKeysNamed.Add(verbKey);
                    _verbFills.Add((verbOf, vocabularyKeys) =>
                    {
                        known.Verb = verbOf(verbKey);
                        DepsOf(known).AddRange(vocabularyKeys);
                    });
                }

                if (step.End is { } endId && Model.TryGetEntry(endId, out var endEntry))
                    stepDeps.Element(endEntry.OwnerId); // whether the end resolves is its relation's file
                if (step.Attributes is { Count: > 0 } stepAttributes)
                {
                    // A read step's attributes (rights-design.md decision 25): whether each resolves is its holder's file.
                    var attributeKeys = new DependencySet(Keys).AddRange(keys);
                    foreach (var id in stepAttributes)
                    {
                        if (Model.TryGetEntry(id, out var holder))
                            attributeKeys.Element(holder.OwnerId);
                    }

                    r.Attributes = new RList<RAttribute>(stepAttributes.Select(id => _byId.GetValueOrDefault(id)).OfType<RAttribute>().Distinct(),
                        attributeKeys.ToList());
                }

                if (step.Values is { Count: > 0 } stepValues)
                {
                    // A modify step's values by name, as an expectation's (object-effects-design.md decision 4).
                    valueNames ??= ExprSites.HeldAttributes(Model, Model.Get<Process>(scenario.Process)!).Values
                        .ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
                    r.Values = ByName(stepValues, valueNames);
                    foreach (var id in stepValues.Keys)
                    {
                        if (Model.TryGetEntry(id, out var holder))
                            stepDeps.Element(holder.OwnerId);
                    }
                }
                if (step.Actor is { } actor)
                    stepDeps.Element(actor);
                if (step.Participant is { } participantId && runs is not null)
                {
                    // The actor a participant acts as (actors-design.md decision 15) is the one the engine's replay ran the step as. It
                    // reads the actors' files (memberships and the actors from the object) and which actors exist, the processes it
                    // invokes, the entities of every subject of the process with their bases (a path must mean the same on each), and the
                    // scenario's objects (traced below).
                    r.Participant = participants.GetValueOrDefault(participantId);
                    var known = r;
                    var actors = _actors;
                    r.ReplayedActsAs = new Lazy<RActor?>(() => known.Trace?.ActsAs is { } acting ? actors.GetValueOrDefault(acting) : null,
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    stepDeps.Add(ActorKind);
                    foreach (var listed in _actorOrder)
                        stepDeps.Element(listed.Id);
                    foreach (var invoked in ExprSites.InvokedProcesses(Model, runs))
                        stepDeps.Element(invoked.Id);
                    foreach (var subject in runs.Subjects)
                    {
                        var chain = new HashSet<string>(StringComparer.Ordinal);
                        for (var id = subject.Entity; id is not null && chain.Add(id); id = Model.Get<Entity>(id)?.Base)
                            stepDeps.Element(id);
                        if (subject.Stereotype is not { } key || Model.GetStereotype(key) is not { } stereotype)
                            continue;
                        // A stereotype entry: its attributes and the carriers it governs, with their bases.
                        stepDeps.Element(stereotype.Id);
                        foreach (var carrier in SubjectCoverage.Governed(Model, runs, subject))
                        {
                            for (var id = carrier.Id; id is not null && chain.Add(id); id = Model.Get<Entity>(id)?.Base)
                                stepDeps.Element(id);
                        }
                    }
                }

                if (readsActor)
                {
                    stepDeps.Add(ActorKind);
                    foreach (var listed in _actorOrder)
                        stepDeps.Element(listed.Id);
                }

                foreach (var id in traced)
                    stepDeps.Element(id);
                stepDeps.AddRange(rightsKeys);
                stepDeps.AddRange(triggerKeys);
                stepDeps.AddRange(revisionKeys);
                // Another lifecycle's check is keyed by that lifecycle's name, or its id when two lifecycles the scenario reaches share one.
                if (checkKeys is not null && step.Assume.Any(a => a.Value.Check is not null && _triggerPoints.TryGetValue(a.Key, out var check)
                        && !string.Equals(check.Process?.Id, scenario.Process, StringComparison.Ordinal)))
                    stepDeps.AddRange(checkKeys.Keys);
                if (step.Expect is { } expect)
                {
                    var expected = expect.States.Select(id => lookup.States.GetValueOrDefault(id)).OfType<RState>().ToList();
                    r.Expect = new RExpectation
                    {
                        Accepted = expect.Accepted,
                        States = new RList<RState>(expected, keys),
                        StatePaths = [.. expected.Select(st => st.Path)],
                        Context = ByName(expect.Context, lookup.Context),
                        Refusal = expect.Refusal,
                        RefusedBy = expect.RefusedBy,
                    };
                    if (expect.CheckWarnings is { Count: > 0 } warnings)
                        r.Expect.CheckWarnings = ExpectedTriggerPoints(warnings, keys);
                    if (expect.TriggerPoints is { Count: > 0 } ran)
                        r.Expect.TriggerPoints = ExpectedTriggerPoints(ran, keys);
                    if (expect.Signatures is { Count: > 0 } counted)
                    {
                        // In the gate's file order (signoff-design.md decision 27); an id of no signature of the process is left out.
                        var signatureOrder = lookup.Signatures.Values.ToList();
                        r.Expect.Signatures = [.. counted
                            .Select(kv => (Signature: lookup.Signatures.GetValueOrDefault(kv.Key), kv.Value))
                            .Where(x => x.Signature is not null)
                            .OrderBy(x => signatureOrder.IndexOf(x.Signature!))
                            .Select(x => new RSignatureCount { Signature = x.Signature!, Count = x.Value })];
                    }
                    if (expect.Notices is { } notices)
                    {
                        // As written (decision 35): compared as a set; an id of no signature of the process is left out.
                        r.Expect.Notices = [.. notices
                            .Select(n => (Signature: lookup.Signatures.GetValueOrDefault(n.Signature), n.Kind))
                            .Where(x => x.Signature is not null)
                            .Select(x => new RExpectedNotice { Signature = x.Signature!, Kind = x.Kind == NoticeKind.Escalate ? "escalate" : "remind" })];
                    }
                    if (expect.Values.Count > 0)
                    {
                        // The object's values by name (object-effects-design.md decision 4): the names its attributes' holders give.
                        valueNames ??= ExprSites.HeldAttributes(Model, Model.Get<Process>(scenario.Process)!).Values
                            .ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
                        r.Expect.Values = ByName(expect.Values, valueNames);
                        foreach (var id in expect.Values.Keys)
                        {
                            if (Model.TryGetEntry(id, out var holder))
                                stepDeps.Element(holder.OwnerId);
                        }
                    }
                }

                steps.Add(r);
                Register(r);
            }

            s.Steps = new RList<RStep>(steps, keys);

            // The engine's replay, for templates that assert what the interpreter did (a refusal's reason, the audit outcomes): run
            // once per scenario on first read, so a run whose templates never read it pays nothing.
            var model = Model;
            var actorsById = _actors;
            var triggerPoints = _triggerPoints;
            var replay = new Lazy<IReadOnlyList<RStepTrace>>(() => Replay(model, scenario, lookup, actorsById, triggerPoints), LazyThreadSafetyMode.ExecutionAndPublication);
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

    /// <summary>
    /// A scenario's objects and subject (lifecycle-design.md decision 22). An object whose entity does not resolve is left out, and so is a
    /// link whose end or object does not resolve. An object's values are keyed by the names of its entity's attributes, so it also reads
    /// its entity and that entity's bases.
    /// </summary>
    private void ResolveScenarioObjects(RScenario s, Scenario scenario, IReadOnlyList<string> keys)
    {
        if (scenario.Objects.Count == 0)
            return;
        var objects = new List<RScenarioObject>();
        var byId = new Dictionary<string, RScenarioObject>(StringComparer.Ordinal);
        var listKeys = new DependencySet(Keys).AddRange(keys);
        foreach (var obj in scenario.Objects)
        {
            listKeys.Element(obj.Entity);
            if (!_entities.TryGetValue(obj.Entity, out var entity) || entity.Id.Length == 0)
                continue;
            var r = new RScenarioObject { Id = obj.Id, Name = obj.Name, Entity = entity, Frozen = obj.Frozen };
            var deps = DepsOf(r).AddRange(keys);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var e = entity; e is not null && seen.Add(e.Id); e = e.Base)
                deps.Element(e.Id);
            foreach (var attribute in entity.Attributes)
                names.TryAdd(attribute.Id, attribute.Name);
            r.Values = ByName(obj.Values, names);
            if (obj.States.Count > 0)
                r.States = ObjectStates(obj.States, keys);
            objects.Add(r);
            byId.TryAdd(obj.Id, r);
            Register(r);
        }

        foreach (var obj in scenario.Objects)
        {
            if (!byId.TryGetValue(obj.Id, out var r))
                continue;
            var links = new List<RScenarioLink>();
            foreach (var link in obj.Links)
            {
                // Whether the end resolves is its relation's file.
                if (Model.TryGetEntry(link.End, out var entry))
                    DepsOf(r).Element(entry.OwnerId);
                if (_byId.GetValueOrDefault(link.End) is REnd end && byId.TryGetValue(link.Object, out var target))
                    links.Add(new RScenarioLink { End = end, Object = target, Frozen = link.Frozen });
            }

            r.Links = links;
        }

        s.Objects = new RList<RScenarioObject>(objects, listKeys.ToList());
        s.Subject = scenario.Subject is { } subject ? byId.GetValueOrDefault(subject) : null;
    }

    /// <summary>
    /// A scenario object's states in other lifecycles, as given (object-effects-design.md decision 10): a state that does not resolve is left
    /// out, so the list also keys on each lifecycle a state id names.
    /// </summary>
    private RList<RState> ObjectStates(IReadOnlyList<string> ids, IReadOnlyList<string> keys)
    {
        var listKeys = new DependencySet(Keys).AddRange(keys);
        var states = new List<RState>(ids.Count);
        foreach (var id in ids)
        {
            if (!Model.TryGetEntry(id, out var entry))
                continue;
            listKeys.Element(entry.OwnerId);
            if (_processLookups.GetValueOrDefault(entry.OwnerId)?.States.GetValueOrDefault(id) is { } state && !states.Contains(state))
                states.Add(state);
        }

        return new RList<RState>(states, listKeys.ToList());
    }

    /// <summary>
    /// A scenario's participants (actors-design.md decision 14), by id: each with its actor, its holdings (a holding whose actor does not
    /// resolve is left out) and its object; and each object's owner. They are parts of the scenario's own file, and read the actors they
    /// name.
    /// </summary>
    private Dictionary<string, RParticipant> ResolveParticipants(RScenario s, Scenario scenario, IReadOnlyList<string> keys)
    {
        var byId = new Dictionary<string, RParticipant>(StringComparer.Ordinal);
        if (scenario.Participants.Count == 0)
            return byId;
        var objects = new Dictionary<string, RScenarioObject>(StringComparer.Ordinal);
        foreach (var obj in s.Objects)
            objects.TryAdd(obj.Id, obj);
        var list = new List<RParticipant>();
        var listKeys = new DependencySet(Keys).AddRange(keys);
        foreach (var participant in scenario.Participants)
        {
            var r = new RParticipant
            {
                Id = participant.Id,
                Name = participant.Name,
                DisplayName = participant.DisplayName,
                Identity = participant.Identity is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var integer) ? integer : participant.IdentityText,
                Actor = participant.Actor is { } actor ? _actors.GetValueOrDefault(actor) : null,
                Object = participant.Object is { } record ? objects.GetValueOrDefault(record) : null,
                Holds = [.. participant.Holds.Where(h => _actors.ContainsKey(h.Actor)).Select(h => new RHolding
                {
                    Id = h.Id,
                    Actor = _actors[h.Actor],
                    Within = h.Within is { } within ? objects.GetValueOrDefault(within) : null,
                })],
            };
            var deps = DepsOf(r).AddRange(keys);
            foreach (var named in participant.Holds.Select(h => h.Actor).Prepend(participant.Actor).OfType<string>())
            {
                deps.Element(named);
                listKeys.Element(named);
            }

            list.Add(r);
            byId.TryAdd(r.Id, r);
            Register(r);
        }

        s.Participants = new RList<RParticipant>(list, listKeys.ToList());
        foreach (var obj in scenario.Objects)
        {
            if (obj.Owner is { } owner && objects.TryGetValue(obj.Id, out var r))
                r.Owner = byId.GetValueOrDefault(owner);
        }

        return byId;
    }

    /// <summary>
    /// What a step's replay (<c>step.trace</c>) reads beyond the scenario and its process (lifecycle-design.md decision 22): the subjects'
    /// entities of the process and of each process it invokes whose guard conditions read <c>subject</c> (their defaults are what such a
    /// condition sees for a value the object lacks), the entities of the scenario's objects, all with their bases, which declare the
    /// attributes and their defaults, and the relations the objects' links go through. A process that sets the object's data counts as one
    /// that reads it (object-effects-design.md section 7). Then the lifecycles an <c>in_state</c> of these processes reads, with each one's
    /// binding: the holder of the bound attribute, whose default stands in for a state not given, and the enum of an enum binding. In
    /// first-seen order, each once; none for a scenario without objects whose processes neither read nor set the object nor read a lifecycle.
    /// </summary>
    private List<string> TraceInputs(Scenario scenario)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var running = Model.Get<Process>(scenario.Process);
        var processes = running is not null
            ? ExprSites.InvokedProcesses(Model, running).Prepend(running).Where(p => ExprSites.SubjectDepth(Model, p) is not null || ExprSites.SetsData(p)).ToList()
            : [];
        var entities = processes.SelectMany(p => p.Subjects.Select(x => x.Entity)).Concat(scenario.Objects.Select(o => o.Entity));
        foreach (var start in entities)
        {
            for (var id = start; id is not null && seen.Add(id); id = Model.Get<Entity>(id)?.Base)
                ids.Add(id);
        }

        // A stereotype entry (stereotype-sets-design.md section 5): the stereotype declares the attributes, its carriers' defaults are what a
        // condition sees, and which entry governs an object depends on what it carries (its packages, the stereotypes' implies). A guard
        // that reads a stereotype's attribute on the object reads the same, with entity entries only.
        var readsStereotype = processes.Any(p => ExprSites.StereotypeAttributesRead(Model, p).Count > 0);
        if (readsStereotype || processes.Any(SubjectCoverage.NamesStereotype)
            || (Model.Get<Process>(scenario.Process) is { } runs && SubjectCoverage.NamesStereotype(runs)))
        {
            if (readsStereotype)
            {
                foreach (var entity in processes.SelectMany(p => p.Subjects).Select(x => x.Entity).OfType<string>().Select(Model.Get<Entity>).OfType<Entity>())
                {
                    foreach (var id in Chain(entity).SelectMany(CarryingIds))
                    {
                        if (seen.Add(id))
                            ids.Add(id);
                    }
                }
            }

            foreach (var p in processes.Append(Model.Get<Process>(scenario.Process)).OfType<Process>())
            {
                foreach (var entry in p.Subjects)
                {
                    if (entry.Stereotype is not { } key || Model.GetStereotype(key) is not { } stereotype)
                        continue;
                    if (seen.Add(stereotype.Id))
                        ids.Add(stereotype.Id);
                    foreach (var carrier in SubjectCoverage.Governed(Model, p, entry))
                    {
                        for (var id = carrier.Id; id is not null && seen.Add(id); id = Model.Get<Entity>(id)?.Base)
                            ids.Add(id);
                    }
                }
            }

            foreach (var obj in scenario.Objects)
            {
                if (Model.Get<Entity>(obj.Entity) is not { } entity)
                    continue;
                foreach (var id in Chain(entity).SelectMany(CarryingIds))
                {
                    if (seen.Add(id))
                        ids.Add(id);
                }
            }
        }

        foreach (var link in scenario.Objects.SelectMany(o => o.Links))
        {
            if (Model.TryGetEntry(link.End, out var entry) && seen.Add(entry.OwnerId))
                ids.Add(entry.OwnerId);
        }

        foreach (var lifecycle in running is null ? Enumerable.Empty<Process>() : ExprSites.InvokedProcesses(Model, running).Prepend(running).SelectMany(InStateLifecycles))
        {
            if (seen.Add(lifecycle.Id))
                ids.Add(lifecycle.Id);
            foreach (var bound in lifecycle.Subjects.Select(s => s.BoundAttribute).OfType<string>())
            {
                if (Model.TryGetEntry(bound, out var holder) && seen.Add(holder.OwnerId))
                    ids.Add(holder.OwnerId);
                if (DeclaredAttribute(bound)?.Type.Enum is { } enumType && seen.Add(enumType.Id))
                    ids.Add(enumType.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// The nodes of the typed trees of a process's guard conditions and set values, in document order: what reads <c>current_actor()</c> and
    /// <c>in_state</c>. A tree stored as text holds its nodes only once parsed, so the typed form is walked, as the run reads it
    /// (<see cref="ObjectStates.Reads"/>); a tree that does not type reads nothing. Typed once per process and run.
    /// </summary>
    private IReadOnlyList<TypedExpr> ConditionNodes(Process process)
    {
        if (_conditionTrees.TryGetValue(process.Id, out var known))
            return known;
        var nodes = new List<TypedExpr>();
        foreach (var id in ExprSites.ConditionIds(process))
        {
            if (ExprResolution.Type(Model, process, id) is { } typed)
                Walk(typed.Typed);
        }

        _conditionTrees[process.Id] = nodes;
        return nodes;

        void Walk(TypedExpr t)
        {
            nodes.Add(t);
            if (t.Body is { } body)
                Walk(body);
            foreach (var arg in t.Args)
                Walk(arg);
        }
    }

    /// <summary>Whether a guard condition or set value of the process, or of a process it invokes, reads <c>current_actor()</c>.</summary>
    private bool ReadsActor(Process process) =>
        ExprSites.InvokedProcesses(Model, process).Prepend(process).SelectMany(ConditionNodes).Any(n => n.Node.CurrentActor == true);

    /// <summary>The lifecycles the <c>in_state</c> calls of a process's guard conditions and set values name, each once, in document order.</summary>
    private IEnumerable<Process> InStateLifecycles(Process process)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in ConditionNodes(process))
        {
            if (node.Node.InState is not null && node.Lifecycle is { } id && seen.Add(id) && Model.Get<Process>(id) is { } lifecycle)
                yield return lifecycle;
        }
    }

    /// <summary>The engine interpreter's result of each step of a scenario that ran (empty when the replay could not start).</summary>
    private static IReadOnlyList<RStepTrace> Replay(ModelSnapshot model, Scenario scenario, ProcessLookup lookup, Dictionary<string, RActor> actors,
        Dictionary<string, RTriggerPoint> triggerPoints)
    {
        using var runtime = new ProcessRuntime(model, 1, CancellationToken.None);
        if (ScenarioReplayer.Replay(scenario, runtime) is not { } replay)
            return [];

        // The actors from the object each participant's step satisfied, read from the cast as that step saw it: the values the run set
        // before it (Participants.Current), as the run's actor lookup reads them.
        var process = model.Get<Process>(scenario.Process);
        var fromObject = scenario.Participants.Count == 0 || process is null ? []
            : actors.Values.Where(a => a.Relative is not null).OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        var cast = ScenarioCast.Of(scenario);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var traces = new List<RStepTrace>(replay.Steps.Count);
        foreach (var t in replay.Steps)
        {
            IReadOnlyList<RActor> satisfied = [];
            if (fromObject.Count > 0 && t.Input?.Participant is { } participantId && cast.Participant(participantId) is { } participant)
            {
                var current = Participants.Current(cast, values);
                satisfied = [.. fromObject.Where(a => Participants.Satisfies(model, process!, current, participant, a.Id))];
            }

            foreach (var (id, value) in t.ValuesChanged ?? ImmutableDictionary<string, JsonElement>.Empty)
                values[id] = value;
            traces.Add(new RStepTrace
            {
                Accepted = t.Accepted,
                Refusal = t.Refusal,
                Audit = [.. t.Audit.Select(a => ResolutionValues.Kebab(a.Outcome))],
                StatePaths = [.. t.Configuration.Select(id => lookup.States.TryGetValue(id, out var state) ? state.Path : id)],
                Final = t.Final,
                ActsAs = t.ActsAs,
                ObjectActors = satisfied,
                TriggerPoints = t.TriggerPoints is { Count: > 0 } runs ? TriggerRuns(runs, triggerPoints) : [],
                CheckWarnings = t.CheckWarnings is { Count: > 0 } warned ? [.. warned.Select(id => triggerPoints.GetValueOrDefault(id)).OfType<RTriggerPoint>()] : [],
            });
        }

        return traces;
    }

    /// <summary>
    /// Guard results keyed by guard name (the id when no guard has it), and check outcomes keyed as <paramref name="checks"/> names them
    /// (trigger-points-design.md decision 26), ordinal. A boolean entry is keyed as before, whatever its key names.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> Assumptions(IReadOnlyDictionary<string, StepAssumption> values, Dictionary<string, RGuard> guards,
        IReadOnlyDictionary<string, string>? checks)
    {
        if (values.Count == 0)
            return ImmutableSortedDictionary<string, object?>.Empty;
        var map = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, value) in values)
        {
            var key = value.Check is not null && checks is not null && checks.TryGetValue(id, out var check) ? check
                : guards.TryGetValue(id, out var guard) ? guard.Name : id;
            map.TryAdd(key, value.ToPlain());
        }

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
