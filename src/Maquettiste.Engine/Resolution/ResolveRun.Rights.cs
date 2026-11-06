using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Rights (rights-design.md section 6): <c>model.rights</c>, each lifecycle's <c>rights</c>, <c>rights_default</c> and <c>may_refuse</c>. A
/// project that uses none of this resolves empty lists, whose keys never change there.
/// </summary>
internal sealed partial class ResolveRun
{
    private const string RightKind = "k:right";

    private readonly Dictionary<string, RRight> _namedRights = new(StringComparer.Ordinal);
    private readonly Dictionary<RRight, List<RRightUse>> _namedRightUses = new(ReferenceEqualityComparer.Instance);
    private List<RRight> _rightOrder = [];

    /// <summary>Every right: the named rights in <c>model.rights</c> order, then the inline rights in process order and file order.</summary>
    private List<RRight> _allRights = [];
    // The lifecycles that state rights or a default, by name then id (model.rights_lifecycles); empty in a project without rights.
    private List<RProcess> _rightsLifecycles = [];

    /// <summary>
    /// Verb lists waiting for <c>model.verbs</c>, which is resolved after the processes: each takes the verb of a key and the keys of the
    /// vocabulary that decides what a key names.
    /// </summary>
    private readonly List<Action<Func<string, RVerb?>, IReadOnlyList<string>>> _verbFills = [];

    /// <summary>The verb keys the rights and verb steps name, which keep a hidden built-in verb in <c>model.verbs</c>.</summary>
    private readonly HashSet<string> _verbKeysNamed = new(StringComparer.Ordinal);

    /// <summary>
    /// The named rights (decision 7), before the processes that use them: each with its actors, exceptions, ends and condition text. Its
    /// verbs wait for <c>model.verbs</c>, and its uses for every process.
    /// </summary>
    private void ResolveNamedRights()
    {
        foreach (var right in Model.All<Right>())
        {
            Ct.ThrowIfCancellationRequested();
            var r = new RRight
            {
                Id = right.Id,
                Named = true,
                Name = right.Name,
                DisplayName = right.DisplayName ?? right.Name,
                PluralName = right.PluralName ?? Inflector.Pluralize(right.Name),
                Description = right.Description is { } d ? d.Text ?? (d.File is null ? null : Model.GetDocument(right.Id)?.SidecarText) : null,
                Access = AccessName(right.Access),
                Everyone = right.Actors.Count == 0,
                Package = PackageOf(right.Package),
            };
            var deps = DepsOf(r).Element(right.Id);
            if (right.Package is { } package)
                deps.Element(package);
            IReadOnlyList<string> own = [Keys.Element(right.Id)];
            FillRight(r, right.Actors, right.Except, right.Verbs, right.Ends, own);
            if (right.Condition is { } condition)
            {
                // Printed in the scope of every use (decision 7): which lifecycles use it, and what their subjects hold.
                var site = ExprSites.NamedRightSite(Model, right, null);
                r.ConditionText = ExprText.Print(condition, ExprSites.Scope(Model, right, Model.GetDocument(right.Id), site, text: false));
                deps.Referrers(right.Id);
                foreach (var lifecycle in ExprSites.RightUses(Model, right))
                    deps.Element(lifecycle.Id).AddRange(SubjectChainKeys(lifecycle));
            }

            if (_namedRights.TryAdd(right.Id, r))
            {
                _namedRightUses[r] = [];
                Register(r);
            }
        }

        _rightOrder = [.. _namedRights.Values.OrderBy(r => r.Package?.QualifiedName ?? "", StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// A lifecycle's rights in file order (decisions 1, 3 and 7): each inline right, built here with its one use, and each use of a named
    /// right. <paramref name="condition"/> types a condition at its use, against the lifecycle's subjects, as a guard's is typed.
    /// </summary>
    private RList<RRightUse> BuildRights(Process process, RProcess r, IReadOnlyList<string> nodeKeys, IReadOnlyDictionary<string, RState> states,
        Func<string, ExprSiteKind, DependencySet, RExpression?> condition)
    {
        var uses = new List<RRightUse>(process.Rights.Count);
        var listKeys = new DependencySet(Keys).AddRange(nodeKeys);
        Dictionary<string, ExprSite>? sites = null;
        foreach (var entry in process.Rights)
        {
            RRight right;
            var hasCondition = false;
            if (entry.Right is { } namedId)
            {
                listKeys.Element(namedId);
                if (!_namedRights.TryGetValue(namedId, out right!))
                    continue; // names nothing, or no named right (MQ2001, MQ9901)
                hasCondition = Model.Get<Right>(namedId)?.Condition is not null;
            }
            else if (entry.Access is { } access)
            {
                right = new RRight
                {
                    Id = entry.Id,
                    Name = entry.Name ?? "",
                    DisplayName = entry.DisplayName ?? entry.Name ?? "",
                    Description = entry.Description?.Text,
                    Access = AccessName(access),
                    Everyone = entry.Actors.Count == 0,
                };
                DepsOf(right).AddRange(nodeKeys);
                FillRight(right, entry.Actors, entry.Except, entry.Verbs, entry.Ends, nodeKeys);
                if (entry.Condition is { } tree)
                {
                    hasCondition = true;
                    sites ??= ExprSites.Of(Model, process).Where(s => s.Kind == ExprSiteKind.Right)
                        .GroupBy(s => s.SubId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                    right.ConditionText = sites.TryGetValue(entry.Id, out var site)
                        ? ExprText.Print(tree, ExprSites.Scope(Model, process, Model.GetDocument(process.Id), site, text: false))
                        : tree.Text;
                }

                Register(right);
            }
            else
            {
                continue; // an entry in neither form says nothing (MQ9901)
            }

            var use = new RRightUse { Id = entry.Id, Right = right, Process = r };
            var useDeps = DepsOf(use).AddRange(nodeKeys);
            if (right.Named)
                useDeps.Element(right.Id);
            use.States = new RList<RState>(entry.States.Select(id => states.GetValueOrDefault(id)).OfType<RState>().Distinct(), nodeKeys);
            if (hasCondition)
            {
                use.Condition = condition(entry.Id, ExprSiteKind.Right, useDeps);
                if (!right.Named)
                    DepsOf(right).AddRange(useDeps.ToList()); // the printed text reads the names the condition reads
            }

            if (right.Named)
            {
                _namedRightUses[right].Add(use);
                Register(use);
            }
            else
            {
                right.Uses = new RList<RRightUse>([use], nodeKeys);
            }

            uses.Add(use);
        }

        return new RList<RRightUse>(uses, listKeys.ToList());
    }

    /// <summary>A right's actors, exceptions and ends, each list keyed on <paramref name="ownerKeys"/> and what decides it; its verbs later.</summary>
    private void FillRight(RRight r, IReadOnlyList<string> actors, IReadOnlyList<string> except, IReadOnlyList<string> verbs, IReadOnlyList<string> ends,
        IReadOnlyList<string> ownerKeys)
    {
        var actorKeys = new DependencySet(Keys).AddRange(ownerKeys).Add(ActorKind).ToList();
        r.Actors = new RList<RActor>(ActorsByIds(actors), actorKeys);
        r.Except = new RList<RActor>(ActorsByIds(except), actorKeys);

        // Whether an end resolves is its relation's file.
        var endKeys = new DependencySet(Keys).AddRange(ownerKeys);
        foreach (var id in ends)
        {
            if (Model.TryGetEntry(id, out var entry))
                endKeys.Element(entry.OwnerId);
        }

        r.Ends = new RList<REnd>(ends.Select(id => _byId.GetValueOrDefault(id)).OfType<REnd>().Distinct(), endKeys.ToList());
        _verbKeysNamed.UnionWith(verbs);
        _verbFills.Add((verbOf, vocabularyKeys) =>
            r.Verbs = new RList<RVerb>(verbs.Distinct(StringComparer.Ordinal).Select(verbOf).OfType<RVerb>(), [.. ownerKeys, .. vocabularyKeys]));
    }

    private List<RActor> ActorsByIds(IEnumerable<string> ids)
    {
        var list = new List<RActor>();
        foreach (var id in ids)
        {
            if (_actors.TryGetValue(id, out var actor) && !list.Contains(actor))
                list.Add(actor);
        }

        return list;
    }

    /// <summary>
    /// After every process is built: each named right's uses, and each process's <c>may_refuse</c> (section 6). Both change only in a
    /// project that uses rights; otherwise the lists stay empty with their keys.
    /// </summary>
    private void FinishRights(Dictionary<string, Process> sources)
    {
        foreach (var (right, uses) in _namedRightUses)
            right.Uses = new RList<RRightUse>(uses, [Keys.Referrers(right.Id)]); // a lifecycle that starts or stops using it
        _allRights = [.. _rightOrder, .. _processOrder.SelectMany(p => p.Rights).Select(u => u.Right).Where(r => !r.Named)];

        var evaluator = RightsEvaluator.Of(Model);
        if (!evaluator.Uses)
        {
            foreach (var r in _processOrder)
            {
                r.MayRefuse = new RList<RMayRefuse>([], [Keys.Element(r.Id), RightsNaming.Key]);
                r.AccessGrid = new RList<RAccessCell>([], [Keys.Element(r.Id), RightsNaming.Key]);
            }

            return;
        }

        // The lifecycles that state rights or a default, by name then id: the others always say yes.
        var stating = _processOrder.Where(p => sources[p.Id] is { Use: ProcessUse.Lifecycle } l && (l.Rights.Count > 0 || l.RightsDefault is not null))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        _rightsLifecycles = stating;
        var coverage = SubjectCoverage.Of(Model);
        List<string> throughKeys = coverage.Any ? [SubjectCoverage.NamingKey, .. coverage.Keys.Select(SubjectCoverage.CarrierSetKey)] : [SubjectCoverage.NamingKey];
        foreach (var r in _processOrder)
        {
            var process = sources[r.Id];
            var keys = new DependencySet(Keys).Element(r.Id).Add(RightsNaming.Key);
            foreach (var use in process.Rights)
            {
                if (use.Right is { } namedRight)
                    keys.Element(namedRight);
            }

            // What decides which lifecycles govern each subject's entity: the process kind, the referrers of the entity and its bases, and
            // the stereotype keys (the governing keys).
            var entities = new List<Entity>();
            foreach (var subject in r.Subjects)
            {
                if (Model.Get<Entity>(subject.Entity.Id) is not { } entity || entities.Contains(entity))
                    continue;
                entities.Add(entity);
                keys.Add(ProcessKind).AddRange(throughKeys);
                foreach (var id in SubjectCoverage.Chain(Model, entity))
                    keys.Referrers(id);
            }

            var found = new List<(RProcess Lifecycle, List<string> Verbs, bool ByEvents)>();
            var eventVerbs = process.Events.Select(e => e.Verb).OfType<string>().Where(RightsEvaluator.SpeaksOfObject).ToHashSet(StringComparer.Ordinal);
            var named = new SortedSet<string>(eventVerbs, StringComparer.Ordinal);
            foreach (var rule in evaluator.RulesOf(process))
                named.UnionWith(rule.Verbs.Where(RightsEvaluator.SpeaksOfObject));
            if (entities.Count > 0 && named.Count > 0 && process.Use != ProcessUse.Library)
            {
                var governing = entities.SelectMany(evaluator.Governing).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var other in stating)
                {
                    if (ReferenceEquals(other, r) || !governing.Contains(other.Id))
                        continue;
                    var lifecycle = sources[other.Id];
                    var verbs = RightsEvaluator.DefaultOf(lifecycle) == RightAccess.Deny
                        ? [.. named]
                        : named.Where(v => evaluator.RulesOf(lifecycle).Any(rule => rule.Access == RightAccess.Deny && rule.Verbs.Contains(v, StringComparer.Ordinal))).ToList();
                    if (verbs.Count > 0)
                        found.Add((other, verbs, verbs.Any(eventVerbs.Contains)));
                }
            }

            var listKeys = keys.ToList();
            var entries = new List<RMayRefuse>(found.Count);
            foreach (var (lifecycle, verbs, byEvents) in found)
            {
                var entry = new RMayRefuse { Id = r.Id + "/may-refuse/" + lifecycle.Id, Dependencies = listKeys, Lifecycle = lifecycle, ByEvents = byEvents };
                _verbFills.Add((verbOf, vocabularyKeys) =>
                    entry.Verbs = new RList<RVerb>(verbs.Select(verbOf).OfType<RVerb>(), [.. listKeys, .. vocabularyKeys]));
                _verbKeysNamed.UnionWith(verbs);
                entries.Add(entry);
            }

            r.MayRefuse = new RList<RMayRefuse>(entries, listKeys);
            r.AccessGrid = new RList<RAccessCell>([], listKeys);
            if (process.Use == ProcessUse.Lifecycle)
                BuildAccessGrid(process, r, keys, found.Select(f => f.Lifecycle));
        }
    }

    /// <summary>
    /// A lifecycle's "who can do what" (decision 31), from <see cref="RightsGrid"/>, the rows <c>get_rights</c> answers. Its keys are
    /// <c>may_refuse</c>'s (<paramref name="keys"/>), every actor file (memberships), the libraries the lifecycle uses, the verb vocabulary,
    /// and each lifecycle that could say no with the named rights it uses: its states and conditions decide a cell that depends on it. The
    /// keys are known here; the rows are worked out on first read, as a scenario's replay is, so a run whose templates never read the grid
    /// pays nothing for it.
    /// </summary>
    private void BuildAccessGrid(Process process, RProcess r, DependencySet keys, IEnumerable<RProcess> mayRefuse)
    {
        keys = new DependencySet(Keys).AddRange(keys.ToList()).Add(ActorKind);
        foreach (var actor in _actorOrder)
            keys.Element(actor.Id);
        foreach (var use in ProcessLibraries.UsesOf(Model, process))
            keys.Element(use.Library.Id);
        foreach (var other in mayRefuse)
        {
            keys.Element(other.Id);
            foreach (var entry in Model.Get<Process>(other.Id)?.Rights ?? [])
            {
                if (entry.Right is { } named)
                    keys.Element(named);
            }
        }

        // The verbs a row can name (the rights' and the events' verbs) keep a hidden built-in verb in model.verbs, so they are known now.
        foreach (var rule in RightsEvaluator.Of(Model).RulesOf(process))
            _verbKeysNamed.UnionWith(rule.Verbs);
        foreach (var ev in ProcessLibraries.Chart(Model, process).Events.Values)
        {
            if (ev.Verb is { Length: > 0 } key)
                _verbKeysNamed.Add(key);
        }

        var listKeys = keys.ToList();
        var (model, actors, entities, byId, processes) = (Model, _actors, _entities, _byId, _processes);
        _verbFills.Add((verbOf, vocabularyKeys) =>
        {
            IReadOnlyList<string> cellKeys = [.. listKeys, .. vocabularyKeys];
            r.AccessGridSource = new Lazy<RList<RAccessCell>>(() =>
            {
                var states = r.AllStates.ToDictionary(st => st.Id, StringComparer.Ordinal);
                var cells = new List<RAccessCell>();
                foreach (var row in RightsGrid.Rows(model, process))
                {
                    if (!states.TryGetValue(row.State, out var state) || verbOf(row.Verb) is not { } verb)
                        continue;
                    cells.Add(new RAccessCell
                    {
                        Id = r.Id + "/access/" + row.State + "/" + (row.Actor ?? "*") + "/" + row.Verb + (row.Entity is { } e ? "/" + e : ""),
                        Dependencies = cellKeys,
                        State = state,
                        Actor = row.Actor is null ? null : actors.GetValueOrDefault(row.Actor),
                        Verb = verb,
                        Entity = row.Entity is null ? null : entities.GetValueOrDefault(row.Entity),
                        Answer = row.Answer,
                        ByDefault = row.ByDefault,
                        By = [.. row.By.Select(id => byId.GetValueOrDefault(id)).OfType<RObject>()],
                        DependsOn = new RList<RProcess>(row.DependsOn.Select(id => processes.GetValueOrDefault(id)).OfType<RProcess>(), listKeys),
                    });
                }

                return new RList<RAccessCell>(cells, cellKeys);
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        });
    }

    /// <summary>
    /// The rights that name an actor directly (section 6, <c>actor.rights</c>), keyed on its referrers: every right that names it refers to it.
    /// </summary>
    private RList<RRight> RightsOf(RActor actor) =>
        new(_allRights.Where(r => r.Actors.Contains(actor) || r.Except.Contains(actor)), [Keys.Referrers(actor.Id)]);

    /// <summary>
    /// The rights that name a verb (section 6, <c>verb.rights</c>): keyed on the rights naming key alone in a project without rights, whose
    /// hash never changes there; otherwise also on the named right kind, every named right and every lifecycle with rights.
    /// </summary>
    private RList<RRight> RightsOf(RVerb verb)
    {
        if (!RightsEvaluator.Of(Model).Uses)
            return new RList<RRight>([], [RightsNaming.Key]);
        var keys = new DependencySet(Keys).Add(RightsNaming.Key).Add(RightKind);
        foreach (var right in _rightOrder)
            keys.Element(right.Id);
        foreach (var process in Model.All<Process>())
        {
            if (process.Rights.Count > 0)
                keys.Element(process.Id);
        }

        return new RList<RRight>(_allRights.Where(r => r.Verbs.Contains(verb)), keys.ToList());
    }

    /// <summary>
    /// The keys a scenario's replay adds for rights (section 7): the named rights its process uses; and when a step checks an object, link
    /// or own verb (a verb step, or an event that names one), the rights naming key, and in a project that uses rights the governing keys of
    /// the subject's entity (of every subject entity of the process when the scenario has no subject) and each other lifecycle that governs
    /// it and could say no to a verb a step checks, with that lifecycle's file and the named rights it uses. And what the conditions of the
    /// rights of those lifecycles read (<see cref="RightConditionKeys"/>), as a guard's are traced. Empty for a process with no rights whose
    /// scenario checks none of those verbs.
    /// </summary>
    private IReadOnlyList<string> RightsReplayKeys(Scenario scenario)
    {
        var keys = new DependencySet(Keys);
        if (Model.Get<Process>(scenario.Process) is not { } running)
            return keys.ToList();
        foreach (var entry in running.Rights)
        {
            if (entry.Right is { } named)
                keys.Element(named);
        }

        // The running lifecycle answers for every input a step makes, so its rights' conditions are read whatever the steps check.
        var subjectObject = scenario.Objects.FirstOrDefault(o => string.Equals(o.Id, scenario.Subject, StringComparison.Ordinal));
        if (running.Use == ProcessUse.Lifecycle)
            RightConditionKeys(keys, running, scenario, running);

        var checkedVerbs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var step in scenario.Steps)
        {
            var verb = step.Verb ?? (step.Event is { } eventId ? running.Events.FirstOrDefault(e => string.Equals(e.Id, eventId, StringComparison.Ordinal))?.Verb : null);
            if (RightsEvaluator.SpeaksOfObject(verb))
                checkedVerbs.Add(verb!);
        }

        if (checkedVerbs.Count == 0)
            return keys.ToList();
        keys.Add(RightsNaming.Key);
        var evaluator = RightsEvaluator.Of(Model);
        if (!evaluator.Uses)
            return keys.ToList();

        IReadOnlyList<Entity> entities = subjectObject is not null
            ? Model.Get<Entity>(subjectObject.Entity) is { } subjectEntity ? [subjectEntity] : []
            : RightsGrid.Subjects(Model, running);
        var coverage = SubjectCoverage.Of(Model);
        keys.Add(ProcessKind).Add(SubjectCoverage.NamingKey);
        foreach (var key in coverage.Keys)
            keys.Add(SubjectCoverage.CarrierSetKey(key));
        foreach (var entity in entities)
        {
            foreach (var id in SubjectCoverage.Chain(Model, entity))
                keys.Referrers(id);
        }

        foreach (var other in Model.All<Process>())
        {
            if (other.Use != ProcessUse.Lifecycle || string.Equals(other.Id, running.Id, StringComparison.Ordinal)
                || (other.Rights.Count == 0 && other.RightsDefault is null) || !entities.Any(e => SubjectCoverage.Governs(Model, other, e)))
                continue;
            var denies = RightsEvaluator.DefaultOf(other) == RightAccess.Deny
                || evaluator.RulesOf(other).Any(rule => rule.Access == RightAccess.Deny && rule.Verbs.Any(checkedVerbs.Contains));
            if (!denies)
                continue;
            keys.Element(other.Id);
            foreach (var entry in other.Rights)
            {
                if (entry.Right is { } named)
                    keys.Element(named);
            }

            RightConditionKeys(keys, other, scenario, running);
        }

        return keys.ToList();
    }

    /// <summary>
    /// What the conditions of a lifecycle's rights read in a replay: the lifecycles an <c>in_state</c> reads, each with its binding (the holder
    /// of the bound attribute, whose default stands in for a state not given, and the enum of an enum binding); and the object's row: the
    /// entities of the scenario's objects and of the running process's subjects with their bases, whose defaults a row holds, with what
    /// decides the stereotypes they carry when a condition reads a stereotype's attribute. Nothing for a lifecycle whose rights have no condition.
    /// </summary>
    private void RightConditionKeys(DependencySet keys, Process lifecycle, Scenario scenario, Process running)
    {
        var readsObject = false;
        var readsStereotype = false;
        foreach (var rule in RightsEvaluator.Of(Model).RulesOf(lifecycle))
        {
            if (rule.Condition is null || ExprResolution.Type(Model, lifecycle, rule.Entry.Id) is not { } typed)
                continue;
            readsObject = true;
            readsStereotype |= ExprResolution.Paths(typed.Typed).Any(id => ExprSites.IsStereotypeAttribute(Model, id));
            foreach (var read in Processes.ObjectStates.ReadsOf(typed.Typed))
            {
                if (Model.Get<Process>(read.Lifecycle) is not { } reads)
                    continue;
                keys.Element(reads.Id);
                foreach (var bound in reads.Subjects.Select(s => s.BoundAttribute).OfType<string>())
                {
                    if (Model.TryGetEntry(bound, out var holder))
                        keys.Element(holder.OwnerId);
                    if (DeclaredAttribute(bound)?.Type.Enum is { } enumType)
                        keys.Element(enumType.Id);
                }
            }
        }

        if (!readsObject)
            return;
        var entities = scenario.Objects.Select(o => o.Entity).Concat(running.Subjects.Select(x => x.Entity).OfType<string>()).Select(Model.Get<Entity>).OfType<Entity>();
        foreach (var entity in entities)
        {
            foreach (var id in SubjectCoverage.Chain(Model, entity))
            {
                keys.Element(id);
                if (readsStereotype && Model.Get<Entity>(id) is { } level)
                {
                    foreach (var carrying in CarryingIds(level))
                        keys.Element(carrying);
                }
            }
        }
    }

    /// <summary>The element keys of each subject entity of a process and of its bases.</summary>
    private IEnumerable<string> SubjectChainKeys(Process process)
    {
        foreach (var entry in process.Subjects)
        {
            if (entry.Entity is { } id && Model.Get<Entity>(id) is { } entity)
            {
                foreach (var level in SubjectCoverage.Chain(Model, entity))
                    yield return Keys.Element(level);
            }
        }
    }

    private static string AccessName(RightAccess access) => access == RightAccess.Deny ? "deny" : "allow";
}
