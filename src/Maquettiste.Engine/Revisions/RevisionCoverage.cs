using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Revisions;

/// <summary>
/// Each entity's <c>revisions</c> block merged through its bases (revisions-design.md decision 9, section 8), the one place that merges it, so
/// the resolver, the rules and the run read the same result and no reader merges bases itself.
/// <para>
/// Built once per snapshot. A model where no entity writes a block costs one pass over its entities: <see cref="Any"/> is false and
/// <see cref="For(Entity)"/> answers <see langword="null"/> for every entity. Otherwise the merged block is worked out, when the snapshot's
/// coverage is first asked for, for each entity that has a block on itself or a base, and only for those.
/// </para>
/// </summary>
internal sealed class RevisionCoverage
{
    private static readonly ConditionalWeakTable<ModelSnapshot, RevisionCoverage> ByModel = new();

    private static readonly RevisionCoverage Empty = new(FrozenDictionary<string, MergedRevisions>.Empty);

    private readonly FrozenDictionary<string, MergedRevisions> _merged;

    private RevisionCoverage(FrozenDictionary<string, MergedRevisions> merged) => _merged = merged;

    /// <summary>Whether some entity has a revisions block on itself or a base; without one every lookup is <see langword="null"/>.</summary>
    public bool Any => _merged.Count > 0;

    /// <summary>The coverage of a model, built once per snapshot.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The coverage.</returns>
    public static RevisionCoverage Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ByModel.GetValue(model, static m => Build(m));
    }

    /// <summary>
    /// The merged block of an entity: <see langword="null"/> when neither it nor any base has a <c>revisions</c> block (decision 1: the type
    /// has no revisions).
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The merged block, or <see langword="null"/>.</returns>
    public MergedRevisions? For(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return For(entity.Id);
    }

    /// <summary>The merged block of an entity by id; <see langword="null"/> for an entity with no block in its family, or an unknown id.</summary>
    /// <param name="entityId">The entity id.</param>
    /// <returns>The merged block, or <see langword="null"/>.</returns>
    public MergedRevisions? For(string entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        return _merged.Count == 0 ? null : _merged.GetValueOrDefault(entityId);
    }

    /// <summary>
    /// The lifecycles that govern an entity, by name then id, ordinal: every process used as a lifecycle one of whose subject entries covers
    /// the entity (<see cref="SubjectCoverage.Governs"/>), as <c>entity.trigger_lifecycles</c> decides it, whether or not it has trigger points.
    /// </summary>
    /// <param name="lifecycles">The model's lifecycles, by name then id.</param>
    /// <param name="model">The model.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The governing lifecycles.</returns>
    private static List<Process> Governing(IReadOnlyList<Process> lifecycles, ModelSnapshot model, Entity entity)
    {
        var governing = new List<Process>();
        foreach (var lifecycle in lifecycles)
        {
            if (SubjectCoverage.Governs(model, lifecycle, entity))
                governing.Add(lifecycle);
        }

        return governing;
    }

    private static RevisionCoverage Build(ModelSnapshot model)
    {
        var entities = model.All<Entity>();
        var any = false;
        foreach (var entity in entities)
        {
            if (entity.Revisions is not null)
            {
                any = true;
                break;
            }
        }

        if (!any)
            return Empty;

        var lifecycles = model.All<Process>().Where(p => p.Use == ProcessUse.Lifecycle).ToList();
        lifecycles.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name) is var c and not 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        var merged = new Dictionary<string, MergedRevisions>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            // The family, farthest base first, so a nearer statement overrides a farther one.
            var family = SubjectCoverage.Chain(model, entity);
            var blocks = new List<(Entity Entity, EntityRevisions Block)>();
            for (var i = family.Count - 1; i >= 0; i--)
            {
                if (model.Get<Entity>(family[i]) is { Revisions: { } block } level)
                    blocks.Add((level, block));
            }

            if (blocks.Count > 0)
                merged[entity.Id] = Merge(model, entity, blocks, Governing(lifecycles, model, entity));
        }

        return new RevisionCoverage(merged.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static MergedRevisions Merge(ModelSnapshot model, Entity entity, List<(Entity Entity, EntityRevisions Block)> blocks, List<Process> governing)
    {
        IReadOnlyList<RevisionLevel> levels = [];
        Entity? levelsFrom = null, separatorFrom = null, boundFrom = null;
        string? separator = null, bound = null;
        var lifecycleEntries = new Dictionary<string, (RevisionLifecycleEntry Entry, Entity From)>(StringComparer.Ordinal);
        var attributes = new List<(string Id, AttributeOnRevise OnRevise, Entity From)>();
        var links = new List<(string Id, LinkOnRevise OnRevise, Entity From)>();
        foreach (var (from, block) in blocks)
        {
            if (block.Levels is { } ownLevels)
                (levels, levelsFrom) = (ownLevels, from);
            if (block.Separator is { } ownSeparator)
                (separator, separatorFrom) = (ownSeparator, from);
            if (block.BoundAttribute is { } ownBound)
                (bound, boundFrom) = (ownBound, from);

            // Within one block the first entry for an item counts; a second one is MQ8312's. A nearer block's entry replaces a farther one's.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in block.Lifecycles ?? [])
            {
                if (seen.Add(entry.Lifecycle))
                    lifecycleEntries[entry.Lifecycle] = (entry, from);
            }

            seen.Clear();
            foreach (var entry in block.Attributes ?? [])
            {
                if (seen.Add(entry.Attribute))
                    Place(attributes, entry.Attribute, entry.OnRevise, from);
            }

            seen.Clear();
            foreach (var entry in block.Links ?? [])
            {
                if (seen.Add(entry.End))
                    Place(links, entry.End, entry.OnRevise, from);
            }
        }

        // Every governing lifecycle, with the fallback (restart, said by no one) for one no entry names. Entries for lifecycles that don't
        // govern the entity (a base's entry for a lifecycle of a derived entity) don't apply to it.
        var lifecycleRules = new List<RevisionLifecycleRule>(governing.Count);
        foreach (var lifecycle in governing)
        {
            if (!lifecycleEntries.TryGetValue(lifecycle.Id, out var said))
            {
                lifecycleRules.Add(new RevisionLifecycleRule(lifecycle, LifecycleOnRevise.Restart, null, null));
                continue;
            }

            lifecycleRules.Add(said.Entry.OnRevise switch
            {
                RevisionLifecycleEntry.Restart => new RevisionLifecycleRule(lifecycle, LifecycleOnRevise.Restart, null, said.From),
                RevisionLifecycleEntry.Keep => new RevisionLifecycleRule(lifecycle, LifecycleOnRevise.Keep, null, said.From),
                var state => new RevisionLifecycleRule(lifecycle, LifecycleOnRevise.State, state, said.From),
            });
        }

        return new MergedRevisions
        {
            Entity = entity,
            Levels = levels,
            LevelsFrom = levelsFrom,
            Separator = separator ?? EntityRevisions.DefaultSeparator,
            SeparatorFrom = separatorFrom,
            BoundAttribute = bound,
            BoundAttributeFrom = boundFrom,
            Lifecycles = lifecycleRules,
            // copy is what an attribute not listed gets: such an entry only undoes a base's, so it is left out.
            Attributes = [.. attributes.Where(a => a.OnRevise != AttributeOnRevise.Copy).Select(a => new RevisionAttributeRule(a.Id, a.OnRevise, a.From))],
            // A base's entry for an end only a derived entity reaches doesn't apply to it.
            Links = [.. links.Where(l => Reaches(model, entity, l.Id)).Select(l => new RevisionLinkRule(l.Id, l.OnRevise, l.From))],
        };
    }

    /// <summary>Puts an entry in its list: in the place of the entry a farther block wrote for the same item, else at the end.</summary>
    private static void Place<T>(List<(string Id, T OnRevise, Entity From)> list, string id, T onRevise, Entity from)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, id, StringComparison.Ordinal))
            {
                list[i] = (id, onRevise, from);
                return;
            }
        }

        list.Add((id, onRevise, from));
    }

    /// <summary>
    /// Whether an entity reaches a relation end (decision 35): the end is of a relation with two ends, and the other end covers the entity
    /// (<see cref="EndCoverage.Covers"/>: by naming it or a base, a stereotype it carries, several types or any entity).
    /// </summary>
    private static bool Reaches(ModelSnapshot model, Entity entity, string endId)
    {
        if (!model.TryGetEntry(endId, out var entry) || entry.Kind != "end" || model.Get<Relation>(entry.OwnerId) is not { Ends.Count: 2 } relation)
            return false;
        var other = string.Equals(relation.Ends[0].Id, endId, StringComparison.Ordinal) ? relation.Ends[1] : relation.Ends[0];
        return EndCoverage.Covers(model, other, entity.Id);
    }
}

/// <summary>
/// An entity's <c>revisions</c> block merged through its bases (revisions-design.md decision 9, section 8). <see cref="Levels"/>,
/// <see cref="Separator"/> and <see cref="BoundAttribute"/> come from the nearest statement: the entity's, then its base's, and on up. The
/// three lists add up through the family, and a nearer entry for the same lifecycle, attribute or end replaces a farther one; each rule says
/// which entity's block said it.
/// </summary>
internal sealed record MergedRevisions
{
    /// <summary>The entity the block is merged for.</summary>
    public required Entity Entity { get; init; }

    /// <summary>The levels, from the most significant; empty when no block of the family names any (MQ8301).</summary>
    public required IReadOnlyList<RevisionLevel> Levels { get; init; }

    /// <summary>The entity whose block gave <see cref="Levels"/>; <see langword="null"/> when none did.</summary>
    public Entity? LevelsFrom { get; init; }

    /// <summary>The separator: the nearest one written, else <see cref="EntityRevisions.DefaultSeparator"/>.</summary>
    public required string Separator { get; init; }

    /// <summary>The entity whose block wrote <see cref="Separator"/>; <see langword="null"/> for the default.</summary>
    public Entity? SeparatorFrom { get; init; }

    /// <summary>The id of the attribute that holds the current revision; <see langword="null"/> when no block of the family names one.</summary>
    public string? BoundAttribute { get; init; }

    /// <summary>The entity whose block named <see cref="BoundAttribute"/>; <see langword="null"/> when none did.</summary>
    public Entity? BoundAttributeFrom { get; init; }

    /// <summary>
    /// What a revise does to every lifecycle that governs the entity, by lifecycle name then id (ordinal), as <c>entity.trigger_lifecycles</c>
    /// orders and decides them: the nearest entry for the lifecycle, else the fallback, a restart said by no one. An entry for a lifecycle
    /// that doesn't govern the entity is left out.
    /// </summary>
    public required IReadOnlyList<RevisionLifecycleRule> Lifecycles { get; init; }

    /// <summary>
    /// What a revise does to each attribute that isn't copied (<c>reset</c> or <c>clear</c>). Order: the order the family first writes an
    /// entry for each attribute, from the farthest base down to the entity, each block in its written order; a nearer entry for the same
    /// attribute takes the farther one's place. An attribute whose nearest entry is <c>copy</c> is left out, since that is what any attribute
    /// not listed gets.
    /// </summary>
    public required IReadOnlyList<RevisionAttributeRule> Attributes { get; init; }

    /// <summary>
    /// What a revise does to the links through each end with a rule that the entity reaches. Order: as <see cref="Attributes"/>, the order
    /// the family first writes an entry for each end, from the farthest base down, a nearer entry taking the farther one's place. An entry
    /// for an end the entity doesn't reach (a base's entry for an end only a derived entity reaches, or an end of a relation with more than
    /// two ends) is left out.
    /// </summary>
    public required IReadOnlyList<RevisionLinkRule> Links { get; init; }
}

/// <summary>What a revise does to one lifecycle (revisions-design.md decision 33), in a <see cref="MergedRevisions"/>.</summary>
/// <param name="Lifecycle">The lifecycle.</param>
/// <param name="OnRevise">Restart, keep, or go to <paramref name="State"/>.</param>
/// <param name="State">The id of the state it goes to, for <see cref="LifecycleOnRevise.State"/>; else <see langword="null"/>.</param>
/// <param name="From">The entity whose block said it; <see langword="null"/> for the fallback.</param>
internal sealed record RevisionLifecycleRule(Process Lifecycle, LifecycleOnRevise OnRevise, string? State, Entity? From);

/// <summary>What a revise does to one attribute (revisions-design.md decision 34), in a <see cref="MergedRevisions"/>.</summary>
/// <param name="Attribute">The attribute's id.</param>
/// <param name="OnRevise"><see cref="AttributeOnRevise.Reset"/> or <see cref="AttributeOnRevise.Clear"/>.</param>
/// <param name="From">The entity whose block said it.</param>
internal sealed record RevisionAttributeRule(string Attribute, AttributeOnRevise OnRevise, Entity From);

/// <summary>What a revise does to the links through one relation end (revisions-design.md decision 35), in a <see cref="MergedRevisions"/>.</summary>
/// <param name="End">The end's id.</param>
/// <param name="OnRevise">Stay, move or duplicate.</param>
/// <param name="From">The entity whose block said it.</param>
internal sealed record RevisionLinkRule(string End, LinkOnRevise OnRevise, Entity From);

/// <summary>What a revise does to a lifecycle (revisions-design.md decision 33), as the resolved <c>on_revise</c> words it.</summary>
internal enum LifecycleOnRevise
{
    /// <summary>Back to the initial state (<c>restart</c>), also the fallback.</summary>
    Restart,

    /// <summary>The states stay as they are, with what is collected in them (<c>keep</c>).</summary>
    Keep,

    /// <summary>A named state, entered as a restart enters the initial one (<c>state</c>).</summary>
    State,
}
