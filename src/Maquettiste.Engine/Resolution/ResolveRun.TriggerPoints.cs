using System.Collections.Frozen;
using System.Collections.Immutable;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Trigger points (trigger-points-design.md section 7): <c>model.trigger_lifecycles</c>, each lifecycle's <c>trigger_points</c> and
/// <c>trigger_verbs</c>, each point's <c>covers</c>, <c>entity.trigger_lifecycles</c> and <c>verb.trigger_points</c>. Which points run where is
/// the one evaluator's (<see cref="TriggerSequences"/>); this only resolves what it answers. A project that uses none of this resolves empty
/// lists, whose keys never change there.
/// </summary>
internal sealed partial class ResolveRun
{
    /// <summary>Every lifecycle's trigger points by id, for scenarios (assumptions, expectations and traces) and verbs.</summary>
    private readonly Dictionary<string, RTriggerPoint> _triggerPoints = new(StringComparer.Ordinal);

    /// <summary>The verb keys each resolved trigger point names, by its id, for <c>verb.trigger_points</c>.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _triggerPointVerbs = new(StringComparer.Ordinal);

    // The lifecycles with trigger points, by name then id (model.trigger_lifecycles); empty in a project without trigger points.
    private List<RProcess> _triggerLifecycles = [];

    /// <summary>
    /// A lifecycle's trigger points in file order (decisions 1 to 3 and 10), each with its places, its typed <c>when</c>, condition and sets,
    /// and its verbs, which wait for <c>model.verbs</c>; then its points per verb, as the evaluator groups them. <paramref name="condition"/>
    /// types a site of the point (its <c>when</c> under <see cref="ExprSites.WhenKey"/>, its condition under its id, a set's value under the
    /// set's id), as a guard's is typed. The lists key on the process's file.
    /// </summary>
    private void BuildTriggerPoints(Process process, RProcess r, IReadOnlyList<string> nodeKeys, IReadOnlyDictionary<string, RState> states,
        IReadOnlyDictionary<string, RTransition> transitions, Func<string, ExprSiteKind, DependencySet, RExpression?> condition)
    {
        var points = new List<RTriggerPoint>(process.TriggerPoints.Count);
        var byId = new Dictionary<string, RTriggerPoint>(StringComparer.Ordinal);
        foreach (var source in process.TriggerPoints)
        {
            var displayName = source.DisplayName ?? source.Name;
            var description = source.Description?.Text;
            var point = new RTriggerPoint
            {
                Id = source.Id,
                Name = source.Name,
                DisplayName = displayName,
                Description = description,
                Message = description is { Length: > 0 } ? description : displayName.Length > 0 ? displayName : source.Name,
                Type = ResolutionValues.Kebab(source.Type),
                Timing = source.Type == TriggerPointType.Action && source.Timing is { } timing ? ResolutionValues.Kebab(timing) : null,
                Severity = source.Type == TriggerPointType.Check ? ResolutionValues.Kebab(source.Severity ?? TriggerSeverity.Error) : null,
                Outside = source.Outside,
                TeamCode = source.TeamCode,
                Process = r,
            };
            var deps = DepsOf(point).AddRange(nodeKeys);
            point.States = new RList<RState>(source.States.Select(id => states.GetValueOrDefault(id)).OfType<RState>().Distinct(), nodeKeys);
            point.Targets = new RList<RState>(source.Targets.Select(id => states.GetValueOrDefault(id)).OfType<RState>().Distinct(), nodeKeys);
            point.Transitions = new RList<RTransition>(source.Transitions.Select(id => transitions.GetValueOrDefault(id)).OfType<RTransition>().Distinct(), nodeKeys);

            // Whether an entity resolves is its own file; what a point narrowed by entities reads and sets is what those entities hold
            // (decision 12), so its expressions read them and their bases too.
            var entityKeys = new DependencySet(Keys).AddRange(nodeKeys);
            var scopeKeys = new DependencySet(Keys);
            foreach (var id in source.Entities)
            {
                entityKeys.Element(id);
                if (Model.Get<Entity>(id) is { } entity)
                {
                    foreach (var level in SubjectCoverage.Chain(Model, entity))
                        scopeKeys.Element(level);
                }
            }

            point.Entities = new RList<REntity>(source.Entities.Select(id => _entities.GetValueOrDefault(id)).OfType<REntity>().Where(e => e.Id.Length > 0).Distinct(),
                entityKeys.ToList());

            // Whether an attribute resolves is its holder's file; whether an end resolves is its relation's file.
            var attributeKeys = new DependencySet(Keys).AddRange(nodeKeys);
            foreach (var id in source.Attributes)
            {
                if (Model.TryGetEntry(id, out var holder))
                    attributeKeys.Element(holder.OwnerId);
            }

            point.Attributes = new RList<RAttribute>(source.Attributes.Select(id => _byId.GetValueOrDefault(id)).OfType<RAttribute>().Distinct(), attributeKeys.ToList());
            var endKeys = new DependencySet(Keys).AddRange(nodeKeys);
            foreach (var id in source.Ends)
            {
                if (Model.TryGetEntry(id, out var relation))
                    endKeys.Element(relation.OwnerId);
            }

            point.Ends = new RList<REnd>(source.Ends.Select(id => _byId.GetValueOrDefault(id)).OfType<REnd>().Distinct(), endKeys.ToList());

            if (source.When is not null)
            {
                point.When = condition(ExprSites.WhenKey(source.Id), ExprSiteKind.Trigger, deps);
                if (point.When is not null)
                    deps.AddRange(scopeKeys.ToList());
            }

            if (source.Condition is not null)
            {
                point.Condition = condition(source.Id, ExprSiteKind.Trigger, deps);
                if (point.Condition is not null)
                    deps.AddRange(scopeKeys.ToList());
            }

            if (source.Sets.Count > 0)
                point.Sets = TriggerSetsOf(source, nodeKeys, scopeKeys.ToList(), condition);

            var verbs = source.Verbs;
            _verbKeysNamed.UnionWith(verbs);
            _verbFills.Add((verbOf, vocabularyKeys) =>
                point.Verbs = new RList<RVerb>(verbs.Distinct(StringComparer.Ordinal).Select(verbOf).OfType<RVerb>(), [.. nodeKeys, .. vocabularyKeys]));
            if (byId.TryAdd(source.Id, point))
            {
                points.Add(point);
                _triggerPoints.TryAdd(source.Id, point);
                _triggerPointVerbs.TryAdd(source.Id, verbs);
                Register(point);
            }
        }

        r.TriggerPoints = new RList<RTriggerPoint>(points, nodeKeys);

        // The points per verb, as the evaluator groups them: own verbs follow the vocabulary's order, so the list also keys on it.
        var grouped = TriggerSequences.Of(Model).VerbsOf(process);
        if (grouped.Count == 0)
            return;
        _verbFills.Add((verbOf, vocabularyKeys) =>
        {
            IReadOnlyList<string> keys = [.. nodeKeys, .. vocabularyKeys];
            RList<RTriggerPoint> Points(IReadOnlyList<ProcessTriggerPoint> list) =>
                new(list.Select(p => byId.GetValueOrDefault(p.Id)).OfType<RTriggerPoint>(), keys);
            var entries = new List<RTriggerVerb>(grouped.Count);
            foreach (var verb in grouped)
            {
                if (verbOf(verb.Verb) is not { } resolved)
                    continue; // a key that names no verb (MQ9701)
                entries.Add(new RTriggerVerb
                {
                    Id = r.Id + "/trigger-verb/" + verb.Verb,
                    Dependencies = keys,
                    Verb = resolved,
                    Before = Points(verb.Before),
                    Checks = Points(verb.Checks),
                    Replace = Points(verb.Replace),
                    WithChange = Points(verb.WithChange),
                    AfterCommit = Points(verb.AfterCommit),
                });
            }

            r.TriggerVerbs = new RList<RTriggerVerb>(entries, keys);
        });
    }

    /// <summary>
    /// A trigger point's sets (decision 3): each attribute as its declaring entity resolves it and each value typed at a trigger set site. The
    /// list keys on the process's file and on the holders of the attributes set, whose names a template prints.
    /// </summary>
    private RList<RSet> TriggerSetsOf(ProcessTriggerPoint source, IReadOnlyList<string> nodeKeys, IReadOnlyList<string> scopeKeys,
        Func<string, ExprSiteKind, DependencySet, RExpression?> condition)
    {
        var sets = new List<RSet>(source.Sets.Count);
        var listKeys = new DependencySet(Keys).AddRange(nodeKeys);
        foreach (var entry in source.Sets)
        {
            var set = new RSet { Id = entry.Id };
            var setDeps = DepsOf(set).AddRange(nodeKeys).AddRange(scopeKeys);
            if (Model.TryGetEntry(entry.Attribute, out var holder))
            {
                listKeys.Element(holder.OwnerId);
                setDeps.Element(holder.OwnerId);
            }

            if (DeclaredAttribute(entry.Attribute) is not { } attribute || condition(entry.Id, ExprSiteKind.TriggerSet, setDeps) is not { } value)
                continue;
            set.Attribute = attribute;
            set.Value = value;
            sets.Add(set);
            Register(set);
        }

        return new RList<RSet>(sets, listKeys.ToList());
    }

    /// <summary>Whether a typed tree reads the object's values before the verb (<c>old.</c>, decision 14).</summary>
    private static bool ReadsOld(TypedExpr typed)
    {
        if (typed.Node.Old == true)
            return true;
        if (typed.Body is { } body && ReadsOld(body))
            return true;
        foreach (var arg in typed.Args)
        {
            if (ReadsOld(arg))
                return true;
        }

        return false;
    }

    /// <summary>
    /// After every process and entity is built: <c>model.trigger_lifecycles</c>, each point's <c>covers</c> and each entity's
    /// <c>trigger_lifecycles</c> (section 7). In a project without trigger points the lists stay empty, keyed on the naming key alone.
    /// </summary>
    private void FinishTriggerPoints()
    {
        var sequences = TriggerSequences.Of(Model);
        if (!sequences.Uses)
        {
            IReadOnlyList<string> naming = [TriggerNaming.Key];
            foreach (var entity in _entities.Values)
            {
                if (entity.Id.Length > 0)
                    entity.TriggerLifecycles = new RList<RProcess>([], naming);
            }

            return;
        }

        _triggerLifecycles = [.. sequences.Lifecycles.Select(l => _processes.GetValueOrDefault(l.Id)).OfType<RProcess>()];

        // Each point's covers: every entity its lifecycle governs, narrowed by entities. Which entities a lifecycle governs is decided by its
        // file (its subject entries), the referrers of each governed entity (a new derived entity names its base), and for a stereotype entry
        // the carrier sets and the carriers' files.
        foreach (var lifecycle in sequences.Lifecycles)
        {
            if (!_processes.TryGetValue(lifecycle.Id, out var r) || r.TriggerPoints.Count == 0)
                continue;
            var keys = new DependencySet(Keys).Element(lifecycle.Id);
            if (SubjectCoverage.NamesStereotype(lifecycle))
                keys.Add(SubjectCoverage.NamingKey).AddRange(CarrierKeys(lifecycle));
            foreach (var entry in lifecycle.Subjects)
            {
                if (entry.Entity is { } id && Model.Get<Entity>(id) is { } subject)
                {
                    foreach (var level in SubjectCoverage.Chain(Model, subject))
                        keys.Referrers(level);
                }
            }

            var all = new ProcessTriggerPoint { Id = lifecycle.Id, Name = "", Type = TriggerPointType.Check, Verbs = [] };
            foreach (var governed in sequences.Covers(lifecycle, all))
                keys.Referrers(governed.Id);
            var coverKeys = keys.ToList();
            foreach (var point in lifecycle.TriggerPoints)
            {
                if (!_triggerPoints.TryGetValue(point.Id, out var resolved) || !ReferenceEquals(resolved.Process, r))
                    continue;
                resolved.Covers = new RList<REntity>(sequences.Covers(lifecycle, point).Select(e => _entities.GetValueOrDefault(e.Id)).OfType<REntity>()
                    .Where(e => e.Id.Length > 0), coverKeys);
            }
        }

        // Each entity's lifecycles with points on object, link or own verbs, whatever those points' entities: the naming key (which lifecycles
        // have points on which verbs), what decides which lifecycles govern the entity (the governing keys), and each lifecycle listed.
        var coverage = SubjectCoverage.Of(Model);
        List<string> throughKeys = [ProcessKind, SubjectCoverage.NamingKey, .. coverage.Keys.Select(SubjectCoverage.CarrierSetKey)];
        foreach (var (id, source) in _entitySources)
        {
            if (!_entities.TryGetValue(id, out var r) || r.Id.Length == 0)
                continue;
            var keys = new DependencySet(Keys).Add(TriggerNaming.Key).AddRange(throughKeys);
            foreach (var level in SubjectCoverage.Chain(Model, source))
                keys.Referrers(level);
            var listed = sequences.LifecyclesFor(source).Select(l => _processes.GetValueOrDefault(l.Id)).OfType<RProcess>().ToList();
            foreach (var lifecycle in listed)
                keys.Element(lifecycle.Id);
            r.TriggerLifecycles = new RList<RProcess>(listed, keys.ToList());
        }
    }

    /// <summary>
    /// The trigger points that name a verb (section 7, <c>verb.trigger_points</c>): keyed on the trigger points naming key alone in a project
    /// without trigger points, whose hash never changes there; otherwise also on the file of every lifecycle with trigger points.
    /// </summary>
    private RList<RTriggerPoint> TriggerPointsOf(RVerb verb)
    {
        if (_triggerLifecycles.Count == 0)
            return new RList<RTriggerPoint>([], [TriggerNaming.Key]);
        var keys = new DependencySet(Keys).Add(TriggerNaming.Key);
        foreach (var lifecycle in _triggerLifecycles)
            keys.Element(lifecycle.Id);
        var points = _triggerLifecycles.SelectMany(l => l.TriggerPoints)
            .Where(p => _triggerPointVerbs.TryGetValue(p.Id, out var verbs) && verbs.Contains(verb.Key, StringComparer.Ordinal));
        return new RList<RTriggerPoint>(points, keys.ToList());
    }
    /// <summary>
    /// The keys a scenario's replay adds for trigger points (section 8, "Replay keys"), as <see cref="RightsReplayKeys"/> does for rights: what the
    /// running lifecycle's points read (<see cref="TriggerConditionKeys"/>); and when a step does an object, link or own verb (a verb step, or an
    /// event that names one), the trigger points naming key, and in a project that uses trigger points the governing keys of the subject's entity
    /// (of every subject entity of the process when the scenario has no subject) and each other lifecycle that governs it with points on a verb a
    /// step does, with that lifecycle's file and what its points read. So it replays when another lifecycle starts governing the subject or gains
    /// a check on <c>modify</c>. A project without trigger points adds only the naming key, whose hash never changes there, and only to such
    /// scenarios.
    /// </summary>
    private IReadOnlyList<string> TriggerReplayKeys(Scenario scenario)
    {
        var keys = new DependencySet(Keys);
        if (Model.Get<Process>(scenario.Process) is not { } running)
            return keys.ToList();

        // The running lifecycle's own points run on every input a step makes, so what they read is read whatever the steps do.
        if (running.Use == ProcessUse.Lifecycle && running.TriggerPoints.Count > 0)
            TriggerConditionKeys(keys, running, scenario, running);

        var done = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var step in scenario.Steps)
        {
            var verb = step.Verb ?? (step.Event is { } eventId ? running.Events.FirstOrDefault(e => string.Equals(e.Id, eventId, StringComparison.Ordinal))?.Verb : null);
            if (TriggerSequences.SpeaksOfObject(verb))
                done.Add(verb!);
        }

        if (done.Count == 0)
            return keys.ToList();
        keys.Add(TriggerNaming.Key);
        var sequences = TriggerSequences.Of(Model);
        if (!sequences.Uses)
            return keys.ToList();

        var subjectObject = scenario.Objects.FirstOrDefault(o => string.Equals(o.Id, scenario.Subject, StringComparison.Ordinal));
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

        foreach (var other in sequences.Lifecycles)
        {
            if (string.Equals(other.Id, running.Id, StringComparison.Ordinal) || !other.TriggerPoints.Any(point => point.Verbs.Any(done.Contains))
                || !entities.Any(e => SubjectCoverage.Governs(Model, other, e)))
                continue;
            keys.Element(other.Id);
            TriggerConditionKeys(keys, other, scenario, running);
        }

        return keys.ToList();
    }

    /// <summary>
    /// What a lifecycle's trigger points read in a replay, as <see cref="RightConditionKeys"/> keys a right's condition: the lifecycles an
    /// <c>in_state</c> of a <c>when</c>, a condition or a set reads, each with its binding (the holder of the bound attribute and the enum of an
    /// enum binding); and the object's row, which <c>subject.</c> and <c>old.</c> read and sets write: the entities of the scenario's objects and
    /// of the running process's subjects with their bases, with what decides the stereotypes they carry when a point reads or sets a stereotype's
    /// attribute. Nothing for a lifecycle whose points have no expression and no set.
    /// </summary>
    private void TriggerConditionKeys(DependencySet keys, Process lifecycle, Scenario scenario, Process running)
    {
        var readsObject = false;
        var readsStereotype = false;
        foreach (var point in lifecycle.TriggerPoints)
        {
            foreach (var set in point.Sets)
            {
                readsObject = true;
                readsStereotype |= ExprSites.IsStereotypeAttribute(Model, set.Attribute);
            }

            IEnumerable<string> sites = [.. point.When is null ? [] : new[] { ExprSites.WhenKey(point.Id) }, .. point.Condition is null ? [] : new[] { point.Id },
                .. point.Sets.Select(s => s.Id)];
            foreach (var site in sites)
            {
                if (ExprResolution.Type(Model, lifecycle, site) is not { } typed)
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

    /// <summary>The keys a scenario's assumed checks take in <c>step.assume</c>, by trigger point id, and what decides them.</summary>
    /// <param name="Names">The key of each check of a lifecycle the scenario reaches, by trigger point id.</param>
    /// <param name="Keys">What decides the keys of another lifecycle's checks: which lifecycles the scenario reaches, and their files.</param>
    private sealed record CheckKeyNames(IReadOnlyDictionary<string, string> Names, IReadOnlyList<string> Keys);

    /// <summary>
    /// The keys of the checks a scenario's steps may assume (decision 26): the running lifecycle's by name; another lifecycle's as
    /// <c>Lifecycle.Name</c>, or with the lifecycle's id in place of its name when two lifecycles the scenario reaches share a name. The
    /// lifecycles reached are the running one, those with trigger points that govern the subject's entity (every subject entity of the process
    /// when the scenario has no subject), and the lifecycle of every check a word names.
    /// </summary>
    private CheckKeyNames CheckKeys(Scenario scenario)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Model.Get<Process>(scenario.Process) is not { } running)
            return new CheckKeyNames(names, []);
        var reached = new List<Process> { running };
        var keys = new DependencySet(Keys).Add(TriggerNaming.Key);
        var subjectObject = scenario.Objects.FirstOrDefault(o => string.Equals(o.Id, scenario.Subject, StringComparison.Ordinal));
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

        foreach (var lifecycle in TriggerSequences.Of(Model).Lifecycles)
        {
            if (!reached.Contains(lifecycle) && entities.Any(e => SubjectCoverage.Governs(Model, lifecycle, e)))
                reached.Add(lifecycle);
        }

        foreach (var (id, value) in scenario.Steps.SelectMany(st => st.Assume))
        {
            if (value.Check is not null && _triggerPoints.TryGetValue(id, out var point) && point.Process is { } owner
                && Model.Get<Process>(owner.Id) is { } lifecycle && !reached.Contains(lifecycle))
                reached.Add(lifecycle);
        }

        var shared = reached.GroupBy(l => l.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g).Select(l => l.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var lifecycle in reached)
        {
            var own = ReferenceEquals(lifecycle, running);
            if (!own)
                keys.Element(lifecycle.Id);
            var prefix = own ? "" : (shared.Contains(lifecycle.Id) ? lifecycle.Id : lifecycle.Name) + ".";
            foreach (var point in lifecycle.TriggerPoints)
                names.TryAdd(point.Id, prefix + point.Name);
        }

        return new CheckKeyNames(names, keys.ToList());
    }

    /// <summary>
    /// The trigger points an expectation lists (decision 28), in its order, repeats kept: each id that names a lifecycle's trigger point. The
    /// list keys on the scenario's keys and on the file of each lifecycle that holds one.
    /// </summary>
    private RList<RTriggerPoint> ExpectedTriggerPoints(IReadOnlyList<string> ids, IReadOnlyList<string> keys)
    {
        var listKeys = new DependencySet(Keys).AddRange(keys);
        var points = new List<RTriggerPoint>(ids.Count);
        foreach (var id in ids)
        {
            if (Model.TryGetEntry(id, out var entry))
                listKeys.Element(entry.OwnerId);
            if (_triggerPoints.TryGetValue(id, out var point))
                points.Add(point);
        }

        return new RList<RTriggerPoint>(points, listKeys.ToList());
    }

    /// <summary>A replay's trigger point entries (decision 28) as templates read them: each whose trigger point resolves, in run order.</summary>
    private static List<RTriggerPointRun> TriggerRuns(IReadOnlyList<TriggerPointRun> runs, Dictionary<string, RTriggerPoint> points)
    {
        var list = new List<RTriggerPointRun>(runs.Count);
        foreach (var run in runs)
        {
            if (!points.TryGetValue(run.TriggerPoint, out var point))
                continue;
            IReadOnlyDictionary<string, object?> sets = FrozenDictionary<string, object?>.Empty;
            if (run.Sets is { Count: > 0 } given)
            {
                var map = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (id, value) in given)
                    map.TryAdd(point.Sets.FirstOrDefault(x => string.Equals(x.Attribute.Id, id, StringComparison.Ordinal))?.Attribute.Name ?? id, ResolutionValues.Plain(value));
                sets = map.ToImmutableSortedDictionary(StringComparer.Ordinal);
            }

            list.Add(new RTriggerPointRun
            {
                TriggerPoint = point,
                Lifecycle = point.Process,
                Verb = point.Verbs.FirstOrDefault(v => string.Equals(v.Key, run.Verb, StringComparison.Ordinal)),
                Type = ResolutionValues.Kebab(run.Type),
                Timing = run.Timing is { } timing ? ResolutionValues.Kebab(timing) : null,
                Outcome = ResolutionValues.Kebab(run.Outcome),
                Assumed = run.Assumed,
                Outside = run.Outside,
                Sets = sets,
            });
        }

        return list;
    }
}
