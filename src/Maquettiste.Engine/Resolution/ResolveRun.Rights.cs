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

        var evaluator = RightsEvaluator.Of(Model);
        if (!evaluator.Uses)
        {
            foreach (var r in _processOrder)
                r.MayRefuse = new RList<RMayRefuse>([], [Keys.Element(r.Id), RightsNaming.Key]);
            return;
        }

        // The lifecycles that state rights or a default, by name then id: the others always say yes.
        var stating = _processOrder.Where(p => sources[p.Id] is { Use: ProcessUse.Lifecycle } l && (l.Rights.Count > 0 || l.RightsDefault is not null))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
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
                foreach (var other in stating)
                {
                    if (ReferenceEquals(other, r))
                        continue;
                    var lifecycle = sources[other.Id];
                    if (!entities.Any(e => SubjectCoverage.Governs(Model, lifecycle, e)))
                        continue;
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
