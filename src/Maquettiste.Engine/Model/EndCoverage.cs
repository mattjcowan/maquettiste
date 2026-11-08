using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Model;

/// <summary>
/// Which entities a relation end covers (several-types-design.md decisions 4 and 5): the one place that answers it. An entity entry, or a
/// one-type end's <see cref="RelationEnd.Entity"/>, covers the entity and every entity derived from it. A stereotype entry covers each entity
/// that carries the stereotype (it is in the entity's effective set: package defaults, implied stereotypes, its own list, less what it excludes)
/// and every entity derived from one, worked out by the rule <see cref="SubjectCoverage"/> uses, without adding relation ends to that class's
/// process index (so <c>sn:subjects</c> never changes). <c>anyEntity</c> covers every entity of the model. Abstract entities are covered;
/// entities the resolver makes when it promotes a relation are not model entities, and no end covers them.
/// <para>
/// Built once per snapshot, and only for ends with several types (<see cref="RelationEnd.HasSeveralTypes"/>): a model without such ends
/// costs one pass over its relations, and every lookup on a one-type end walks the entity's base chain, as the readers do today.
/// </para>
/// </summary>
internal sealed class EndCoverage
{
    /// <summary>
    /// The naming key (several-types-design.md decision 35): which relation ends name a stereotype or any entity. A model without such ends
    /// never records it.
    /// </summary>
    public const string NamingKey = "et:ends";

    private static readonly ConditionalWeakTable<ModelSnapshot, EndCoverage> ByModel = new();

    private static readonly ConditionalWeakTable<ModelSnapshot, string> NamingHashes = new();

    private static readonly EndCoverage Empty = new();

    private readonly FrozenDictionary<string, Covered> _ends;

    private readonly FrozenDictionary<string, ImmutableArray<Entity>> _carriers;

    private EndCoverage()
    {
        _ends = FrozenDictionary<string, Covered>.Empty;
        _carriers = FrozenDictionary<string, ImmutableArray<Entity>>.Empty;
        Ends = [];
    }

    private EndCoverage(ModelSnapshot model, List<(Relation Relation, RelationEnd End)> ends)
    {
        Ends = [.. ends.Select(e => e.End.Id)];
        var entities = model.All<Entity>().ToList();
        var chains = new Dictionary<string, List<string>>(entities.Count, StringComparer.Ordinal);
        foreach (var entity in entities)
            chains.TryAdd(entity.Id, SubjectCoverage.Chain(model, entity));

        // The carriers of every stereotype key an end names: the entities whose effective set holds it.
        var keys = ends.SelectMany(e => e.End.Types ?? []).Select(t => t.Stereotype).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var carriers = keys.ToDictionary(k => k, _ => new List<Entity>(), StringComparer.Ordinal);
        if (keys.Count > 0)
        {
            foreach (var entity in entities)
            {
                foreach (var applied in model.EffectiveStereotypes(entity))
                {
                    if (carriers.TryGetValue(applied.Stereotype.Key, out var list) && !list.Contains(entity))
                        list.Add(entity);
                }
            }
        }

        var orderedCarriers = carriers.ToDictionary(kv => kv.Key, kv => SubjectCoverage.Ordered(model, kv.Value), StringComparer.Ordinal);
        _carriers = orderedCarriers.ToFrozenDictionary(StringComparer.Ordinal);
        ImmutableArray<Entity>? roots = null;
        var byEnd = new Dictionary<string, Covered>(StringComparer.Ordinal);
        foreach (var (_, end) in ends)
        {
            if (byEnd.ContainsKey(end.Id))
                continue;

            // The entities the entries name directly: listed entities, carriers, or every entity.
            var named = new HashSet<string>(StringComparer.Ordinal);
            if (end.AnyEntity == true)
            {
                foreach (var entity in entities)
                    named.Add(entity.Id);
            }
            else
            {
                foreach (var entry in end.Types ?? [])
                {
                    if (entry.Entity is { } id)
                        named.Add(id);
                    else if (entry.Stereotype is { } key)
                    {
                        foreach (var carrier in orderedCarriers[key])
                            named.Add(carrier.Id);
                    }
                }
            }

            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, chain) in chains)
            {
                if (chain.Exists(named.Contains))
                    covered.Add(id);
            }

            // Decision 5: the covered entities with no covered base, each where the walk over the entries first reaches it.
            var types = new List<Entity>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Offer(Entity entity)
            {
                if (chains.TryGetValue(entity.Id, out var chain) && covered.Contains(entity.Id) && !chain.Skip(1).Any(covered.Contains) && seen.Add(entity.Id))
                    types.Add(entity);
            }

            if (end.AnyEntity == true)
            {
                roots ??= SubjectCoverage.Ordered(model, [.. entities.Where(e => chains[e.Id].Count == 1)]);
                foreach (var root in roots.Value)
                    Offer(root);
            }
            else
            {
                foreach (var entry in end.Types ?? [])
                {
                    if (entry.Entity is { } id)
                    {
                        if (model.Get<Entity>(id) is { } entity)
                            Offer(entity);
                    }
                    else if (entry.Stereotype is { } key)
                    {
                        foreach (var carrier in orderedCarriers[key])
                            Offer(carrier);
                    }
                }
            }

            byEnd[end.Id] = new Covered(covered.ToFrozenSet(StringComparer.Ordinal), [.. types], CommonBase(model, chains, types));
        }

        _ends = byEnd.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Whether some relation end has several types; without one every lookup on this instance is empty and costs nothing.</summary>
    public bool Any => Ends.Length > 0;

    /// <summary>The ids of the ends with several types, in model order of their relations, then end order.</summary>
    public ImmutableArray<string> Ends { get; }

    /// <summary>The coverage of a model, built once per snapshot.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The coverage.</returns>
    public static EndCoverage Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ByModel.GetValue(model, static m =>
        {
            List<(Relation, RelationEnd)>? ends = null;
            foreach (var relation in m.All<Relation>())
            {
                foreach (var end in relation.Ends)
                {
                    if (end.HasSeveralTypes)
                        (ends ??= []).Add((relation, end));
                }
            }

            return ends is null ? Empty : new EndCoverage(m, ends);
        });
    }

    /// <summary>Whether a relation has an end with several types (decision 2), read from the file alone.</summary>
    /// <param name="relation">The relation.</param>
    /// <returns>Whether one of its ends has several types.</returns>
    public static bool HasSeveralTypes(Relation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        foreach (var end in relation.Ends)
        {
            if (end.HasSeveralTypes)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether an end covers an entity: on a one-type end, the entity is the end's entity or derives from it; on an end with several types,
    /// decision 4. An unknown entity id is covered by no end with several types, and by a one-type end only when it is the end's entity.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="end">The end.</param>
    /// <param name="entityId">The entity id.</param>
    /// <returns>Whether the end covers it.</returns>
    public static bool Covers(ModelSnapshot model, RelationEnd end, string entityId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(end);
        ArgumentNullException.ThrowIfNull(entityId);
        if (end.HasSeveralTypes)
            return Of(model)._ends.TryGetValue(end.Id, out var covered) && covered.Set.Contains(entityId);
        if (end.SingleEntity is not { } single)
            return false;
        if (string.Equals(single, entityId, StringComparison.Ordinal))
            return true;
        return model.Get<Entity>(entityId) is { } entity && SubjectCoverage.Chain(model, entity).Contains(single, StringComparer.Ordinal);
    }

    /// <summary>
    /// The entities an end covers, ordinal by id: on a one-type end, the end's entity and every entity derived from it (empty when the entity
    /// is unknown); on an end with several types, decision 4's set. These are also the entities that hold the other end's navigation
    /// (decision 10: it is generated on each of the end's types, and so on their derived entities by inheritance).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="end">The end.</param>
    /// <returns>The covered entity ids.</returns>
    public static IReadOnlyCollection<string> CoveredBy(ModelSnapshot model, RelationEnd end)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(end);
        if (end.HasSeveralTypes)
            return Of(model)._ends.TryGetValue(end.Id, out var covered) ? [.. covered.Set.Order(StringComparer.Ordinal)] : [];
        if (end.SingleEntity is not { } single || model.Get<Entity>(single) is null)
            return [];
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entity in model.All<Entity>())
        {
            if (SubjectCoverage.Chain(model, entity).Contains(single, StringComparer.Ordinal))
                set.Add(entity.Id);
        }

        return set;
    }

    /// <summary>
    /// The end's types (decision 5): on a one-type end, its entity alone (empty when unknown); on an end with several types, the covered
    /// entities with no covered base, in the order the entries first reach them (an entity entry its entity; a stereotype entry its carriers by
    /// qualified name, then id; <c>anyEntity</c> every entity with no base by qualified name, then id).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="end">The end.</param>
    /// <returns>The types.</returns>
    public static ImmutableArray<Entity> TypesOf(ModelSnapshot model, RelationEnd end)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(end);
        if (end.HasSeveralTypes)
            return Of(model)._ends.TryGetValue(end.Id, out var covered) ? covered.Types : [];
        return end.SingleEntity is { } single && model.Get<Entity>(single) is { } entity ? [entity] : [];
    }

    /// <summary>
    /// The end's common base (decision 19): the nearest entity every type is or derives from. The entity itself on a one-type end; a shared
    /// base when there is one; else <see langword="null"/> (also when the end has no types yet).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="end">The end.</param>
    /// <returns>The common base, or <see langword="null"/>.</returns>
    public static Entity? CommonBaseOf(ModelSnapshot model, RelationEnd end)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(end);
        if (end.HasSeveralTypes)
            return Of(model)._ends.TryGetValue(end.Id, out var covered) ? covered.CommonBase : null;
        return end.SingleEntity is { } single ? model.Get<Entity>(single) : null;
    }

    /// <summary>
    /// The entities that carry a stereotype a relation end names (decision 4: it is in their effective set), by qualified name, then id; empty
    /// for a key no end names.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="key">The stereotype key.</param>
    /// <returns>The carriers.</returns>
    public static ImmutableArray<Entity> CarriersOf(ModelSnapshot model, string key)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(key);
        return Of(model)._carriers.TryGetValue(key, out var carriers) ? carriers : [];
    }

    /// <summary>Whether an end names a stereotype or any entity: what the naming key <see cref="NamingKey"/> records.</summary>
    /// <param name="end">The end.</param>
    /// <returns>Whether it does.</returns>
    public static bool NamesStereotypeOrAny(RelationEnd end)
    {
        ArgumentNullException.ThrowIfNull(end);
        return end.AnyEntity == true || end.Types is { } types && types.Any(t => t.Stereotype is not null);
    }

    /// <summary>
    /// The current hash of <see cref="NamingKey"/> (several-types-design.md section 7): for each relation with an end that names a stereotype or
    /// any entity, by id, the relation's id, then for each such end its id, its stereotype keys in entry order and its <c>anyEntity</c> flag.
    /// It changes when an end starts or stops naming a stereotype or any entity, or names another stereotype; not when an entity entry changes
    /// (those reach the entity's referrers). Worked out once per snapshot.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <returns>The hash.</returns>
    public static string NamingHash(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return NamingHashes.GetValue(model, static m =>
        {
            using var hash = new Hashing.HashBuilder();
            hash.Add("et");
            foreach (var relation in m.All<Relation>().Where(r => r.Ends.Any(NamesStereotypeOrAny)).OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                hash.Add(relation.Id);
                foreach (var end in relation.Ends.Where(NamesStereotypeOrAny))
                {
                    hash.Add(end.Id);
                    foreach (var entry in end.Types ?? [])
                    {
                        if (entry.Stereotype is { } key)
                            hash.Add(key);
                    }

                    hash.Add(end.AnyEntity == true ? "any" : "-").Add("/");
                }

                hash.Add("//");
            }

            return hash.Finish();
        });
    }

    private static Entity? CommonBase(ModelSnapshot model, Dictionary<string, List<string>> chains, List<Entity> types)
    {
        if (types.Count == 0)
            return null;
        var first = chains[types[0].Id];
        foreach (var candidate in first)
        {
            if (types.TrueForAll(t => chains[t.Id].Contains(candidate, StringComparer.Ordinal)))
                return model.Get<Entity>(candidate);
        }

        return null;
    }

    private sealed record Covered(FrozenSet<string> Set, ImmutableArray<Entity> Types, Entity? CommonBase);
}
