using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// The named trigger points of a snapshot (trigger-points-design.md decisions 33 and 34): the entities each one covers, the ones that cover an
/// entity in run order, and each one's body as a lifecycle's trigger point reads it, so the rules, the evaluator and the resolver share one
/// answer. A subject entity covers itself and its derived entities; a subject stereotype covers its carriers and their derived entities; an
/// entry of <c>exceptEntities</c> takes itself and its derived entities out. Built once per snapshot; a project without named trigger points
/// costs one pass over its elements of that kind.
/// </summary>
internal sealed class NamedTriggerPoints
{
    private static readonly ConditionalWeakTable<ModelSnapshot, NamedTriggerPoints> ByModel = new();

    private readonly ModelSnapshot _model;
    // Per point id: the entities its subjects cover, before exceptEntities.
    private readonly FrozenDictionary<string, FrozenSet<string>> _subjectsCover = FrozenDictionary<string, FrozenSet<string>>.Empty;
    // Per point id: the entities it covers, less exceptEntities.
    private readonly FrozenDictionary<string, FrozenSet<string>> _covered = FrozenDictionary<string, FrozenSet<string>>.Empty;
    private readonly FrozenDictionary<string, ProcessTriggerPoint> _bodies = FrozenDictionary<string, ProcessTriggerPoint>.Empty;
    // Per entity id: the chain of the entity and its bases, nearest first, for every entity the points read.
    private readonly FrozenDictionary<string, List<string>> _chains = FrozenDictionary<string, List<string>>.Empty;
    // Per stereotype key a subject names: the entities that carry it.
    private readonly FrozenDictionary<string, FrozenSet<string>> _carriers = FrozenDictionary<string, FrozenSet<string>>.Empty;
    private readonly ConcurrentDictionary<string, IReadOnlyList<TriggerPoint>> _forEntity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<Entity>> _covers = new(StringComparer.Ordinal);

    private NamedTriggerPoints(ModelSnapshot model)
    {
        _model = model;
        var points = model.All<TriggerPoint>().ToList();
        points.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name) is var c and not 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        All = points;
        if (points.Count == 0)
            return;
        var entities = model.All<Entity>().ToList();
        var chains = entities.ToDictionary(e => e.Id, e => SubjectCoverage.Chain(model, e), StringComparer.Ordinal);
        var keys = points.SelectMany(p => p.Subjects).Select(s => s.Stereotype).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var carriers = keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        if (keys.Count > 0)
        {
            foreach (var entity in entities)
            {
                foreach (var applied in model.EffectiveStereotypes(entity))
                {
                    if (carriers.TryGetValue(applied.Stereotype.Key, out var set))
                        set.Add(entity.Id);
                }
            }
        }

        var subjectsCover = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        var covered = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        var bodies = new Dictionary<string, ProcessTriggerPoint>(StringComparer.Ordinal);
        foreach (var point in points)
        {
            var named = point.Subjects.Select(s => s.Entity).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var carried = point.Subjects.Select(s => s.Stereotype).OfType<string>().SelectMany(k => carriers.GetValueOrDefault(k) ?? []).ToHashSet(StringComparer.Ordinal);
            var except = point.ExceptEntities.ToHashSet(StringComparer.Ordinal);
            var all = new HashSet<string>(StringComparer.Ordinal);
            var less = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, chain) in chains)
            {
                if (!chain.Any(c => named.Contains(c) || carried.Contains(c)))
                    continue;
                all.Add(id);
                if (!chain.Any(except.Contains))
                    less.Add(id);
            }

            subjectsCover.TryAdd(point.Id, all.ToFrozenSet(StringComparer.Ordinal));
            covered.TryAdd(point.Id, less.ToFrozenSet(StringComparer.Ordinal));
            bodies.TryAdd(point.Id, BodyOf(point));
        }

        _subjectsCover = subjectsCover.ToFrozenDictionary(StringComparer.Ordinal);
        _covered = covered.ToFrozenDictionary(StringComparer.Ordinal);
        _bodies = bodies.ToFrozenDictionary(StringComparer.Ordinal);
        _chains = chains.ToFrozenDictionary(StringComparer.Ordinal);
        _carriers = carriers.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>The named trigger points, by name then id (<c>model.trigger_points</c>).</summary>
    public IReadOnlyList<TriggerPoint> All { get; }

    /// <summary>Whether the snapshot has a named trigger point.</summary>
    public bool Any => All.Count > 0;

    /// <summary>The named trigger points of a snapshot, worked out once and kept as long as the snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The named trigger points.</returns>
    public static NamedTriggerPoints Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ByModel.GetValue(model, static m => new NamedTriggerPoints(m));
    }

    /// <summary>
    /// A named trigger point's body as a lifecycle's trigger point (decision 33): the same keys, with no states, targets or transitions, under
    /// the point's own id, so the evaluator, the rules and the run read both kinds alike. The same instance for one point and one snapshot.
    /// </summary>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The body.</returns>
    public ProcessTriggerPoint Body(TriggerPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return _bodies.TryGetValue(point.Id, out var body) ? body : BodyOf(point);
    }

    /// <summary>Whether a point's subjects cover an entity, before <c>exceptEntities</c> (MQ8102).</summary>
    /// <param name="point">The named trigger point.</param>
    /// <param name="entityId">The entity's id.</param>
    /// <returns>Whether a subject covers it.</returns>
    public bool SubjectsCover(TriggerPoint point, string entityId) => _subjectsCover.TryGetValue(point.Id, out var set) && set.Contains(entityId);

    /// <summary>Whether a point covers an entity: a subject covers it and no entry of <c>exceptEntities</c> takes it out. Its <c>entities</c> aside.</summary>
    /// <param name="point">The named trigger point.</param>
    /// <param name="entityId">The entity's id.</param>
    /// <returns>Whether it covers it.</returns>
    public bool Covered(TriggerPoint point, string entityId) => _covered.TryGetValue(point.Id, out var set) && set.Contains(entityId);

    /// <summary>The entities a point covers, less <c>exceptEntities</c>, before its <c>entities</c> narrow it, by qualified name then id.</summary>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The entities.</returns>
    public IReadOnlyList<Entity> CoveredEntities(TriggerPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return _covers.GetOrAdd(point.Id, _ =>
        {
            if (!_covered.TryGetValue(point.Id, out var set) || set.Count == 0)
                return [];
            var names = _model.Names("entity");
            return [.. set.Select(id => _model.Get<Entity>(id)).OfType<Entity>()
                .OrderBy(e => names.Get(e.Id)?.Longest ?? e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)];
        });
    }

    /// <summary>
    /// The entities a point runs for (decision 33, <c>trigger_point.covers</c>): those it covers, narrowed by <c>entities</c> to those that are
    /// or derive from one, by qualified name then id.
    /// </summary>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The entities.</returns>
    public IReadOnlyList<Entity> Covers(TriggerPoint point)
    {
        var covered = CoveredEntities(point);
        return point.Entities.Count == 0 ? covered : [.. covered.Where(e => ChainOf(e).Any(id => point.Entities.Contains(id, StringComparer.Ordinal)))];
    }

    /// <summary>
    /// The named trigger points that cover an entity, in run order (decision 34), whatever their verbs and <c>entities</c>: the one whose subject
    /// is the farthest base first; at one depth those an entity subject brings before those a stereotype brings; then by name, then id.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The points; empty when none covers it.</returns>
    public IReadOnlyList<TriggerPoint> For(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!Any)
            return [];
        return _forEntity.GetOrAdd(entity.Id, _ =>
        {
            var chain = ChainOf(entity);
            var found = new List<(TriggerPoint Point, int Depth, int Rank)>();
            foreach (var point in All)
            {
                if (!Covered(point, entity.Id))
                    continue;
                var (depth, rank) = (-1, 1);
                foreach (var subject in point.Subjects)
                {
                    for (var at = chain.Count - 1; at >= 0; at--)
                    {
                        var brings = subject.Entity is { } id ? string.Equals(chain[at], id, StringComparison.Ordinal)
                            : subject.Stereotype is { } key && _carriers.TryGetValue(key, out var set) && set.Contains(chain[at]);
                        if (!brings)
                            continue;
                        var kind = subject.Entity is null ? 1 : 0;
                        if (at > depth || (at == depth && kind < rank))
                            (depth, rank) = (at, kind);
                        break;
                    }
                }

                found.Add((point, depth, rank));
            }

            // All is in name then id order, and the sort is stable.
            return [.. found.OrderByDescending(f => f.Depth).ThenBy(f => f.Rank).Select(f => f.Point)];
        });
    }

    /// <summary>
    /// The key a step's <c>assume</c> gives a named trigger point's check by name (decision 26, part 2): its package's qualified name, a dot
    /// and its name; its name alone when it has no package.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The qualified name.</returns>
    public static string QualifiedName(ModelSnapshot model, TriggerPoint point)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(point);
        var names = new List<string> { point.Name };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var package = point.Package is { } id ? model.Get<Package>(id) : null; package is not null && seen.Add(package.Id);
             package = package.Parent is { } parent ? model.Get<Package>(parent) : null)
            names.Add(package.Name);
        names.Reverse();
        return string.Join('.', names);
    }

    private List<string> ChainOf(Entity entity) => _chains.TryGetValue(entity.Id, out var chain) ? chain : SubjectCoverage.Chain(_model, entity);

    private static ProcessTriggerPoint BodyOf(TriggerPoint point) => new()
    {
        Id = point.Id,
        Name = point.Name,
        DisplayName = point.DisplayName,
        Type = point.Type,
        Timing = point.Timing,
        Severity = point.Severity,
        Verbs = point.Verbs,
        Entities = point.Entities,
        Attributes = point.Attributes,
        Ends = point.Ends,
        When = point.When,
        Condition = point.Condition,
        Sets = point.Sets,
        Outside = point.Outside,
        NoBypass = point.NoBypass,
        Description = point.Description,
    };
}
