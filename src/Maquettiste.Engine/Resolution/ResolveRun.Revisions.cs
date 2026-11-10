using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Revisions;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Revisions (revisions-design.md section 8): each entity's <c>revisions</c>, merged through its bases by <see cref="RevisionCoverage"/>, and
/// the keys it and a scenario whose subject's type has revisions add (decision 42). A model where no entity writes a block skips all of it:
/// every <c>entity.revisions</c> stays null and nothing new is hashed.
/// </summary>
internal sealed partial class ResolveRun
{
    // What entity.revisions keys on, by entity id; filled only for entities with a block in their family.
    private readonly Dictionary<string, IReadOnlyList<string>> _revisionKeys = new(StringComparer.Ordinal);

    /// <summary>After every process, entity and relation is built: each entity's <c>revisions</c>, for the entities with a block in their family.</summary>
    private void FinishRevisions()
    {
        var coverage = RevisionCoverage.Of(Model);
        if (!coverage.Any)
            return;
        foreach (var (id, source) in _entitySources)
        {
            if (!_entities.TryGetValue(id, out var r) || r.Id.Length == 0 || coverage.For(source) is not { } merged)
                continue;
            r.Revisions = Revisions(r, source, merged);
        }
    }

    private RRevisions Revisions(REntity entity, Entity source, MergedRevisions merged)
    {
        RevisionSequence.TryCreate(merged.Levels, merged.Separator, out var sequence, out _);
        var levels = new List<RRevisionLevel>(merged.Levels.Count);
        for (var i = 0; i < merged.Levels.Count; i++)
            levels.Add(Level(merged.Levels[i], i + 1, merged.Separator, sequence));

        var lifecycles = new List<RRevisionLifecycle>(merged.Lifecycles.Count);
        foreach (var rule in merged.Lifecycles)
        {
            if (!_processes.TryGetValue(rule.Lifecycle.Id, out var lifecycle))
                continue;
            lifecycles.Add(new RRevisionLifecycle
            {
                Lifecycle = lifecycle,
                OnRevise = rule.OnRevise switch { LifecycleOnRevise.Keep => "keep", LifecycleOnRevise.State => "state", _ => "restart" },
                State = rule.State is { } state ? _byId.GetValueOrDefault(state) as RState : null,
                From = rule.From is { } from ? _entities.GetValueOrDefault(from.Id) : null,
            });
        }

        var attributes = new List<RRevisionAttribute>(merged.Attributes.Count);
        foreach (var rule in merged.Attributes)
        {
            if (HeldAttribute(entity, rule.Attribute) is { } attribute && _entities.GetValueOrDefault(rule.From.Id) is { } from)
                attributes.Add(new RRevisionAttribute { Attribute = attribute, OnRevise = ResolutionValues.Kebab(rule.OnRevise), From = from });
        }

        var links = new List<RRevisionLink>(merged.Links.Count);
        foreach (var rule in merged.Links)
        {
            if (_byId.GetValueOrDefault(rule.End) is REnd end && _entities.GetValueOrDefault(rule.From.Id) is { } from)
                links.Add(new RRevisionLink { End = end, OnRevise = ResolutionValues.Kebab(rule.OnRevise), From = from });
        }

        return new RRevisions
        {
            Levels = levels,
            Separator = merged.Separator,
            FirstRevision = sequence?.Start ?? FirstRevisionOf(levels, merged.Separator),
            BoundAttribute = merged.BoundAttribute is { } bound ? HeldAttribute(entity, bound) : null,
            Lifecycles = lifecycles,
            Attributes = attributes,
            Links = links,
            Keys = RevisionKeys(source, merged),
        };
    }

    /// <summary>
    /// One level as templates read it. Its first value and examples come from the sequence; when the levels don't make one (MQ8301 or MQ8303
    /// says why), from the level on its own, and failing that from what the file wrote.
    /// </summary>
    private static RRevisionLevel Level(RevisionLevel level, int number, string separator, RevisionSequence? sequence)
    {
        var format = level.Format switch { RevisionFormat.Letters => "letters", RevisionFormat.Numbers => "numbers", _ => "list" };
        var own = sequence ?? (RevisionSequence.TryCreate([level], separator, out var alone, out _) ? alone : null);
        var at = sequence is null ? 1 : number;
        return new RRevisionLevel
        {
            Number = number,
            Name = level.Name,
            Format = format,
            Values = level.Format is null ? level.Values ?? [] : [],
            First = own?.FirstValue(at) ?? level.FirstText ?? level.Values?.FirstOrDefault() ?? "",
            Skip = level.Format == RevisionFormat.Letters ? level.Skip ?? [] : [],
            Width = level.Format == RevisionFormat.Numbers ? level.Width : null,
            StartEmpty = level.StartEmpty == true,
            Examples = own?.Examples(at) ?? [],
        };
    }

    /// <summary>The sequence's start from the levels' first values, for levels that make no sequence: those above the first that starts empty.</summary>
    private static string FirstRevisionOf(List<RRevisionLevel> levels, string separator) =>
        string.Join(separator, levels.TakeWhile(l => !l.StartEmpty).Select(l => l.First));

    /// <summary>An attribute as the entity holds it (its own, inherited or from a stereotype it carries), else as resolved anywhere.</summary>
    private RAttribute? HeldAttribute(REntity entity, string id)
    {
        foreach (var attribute in entity.Attributes)
        {
            if (string.Equals(attribute.Id, id, StringComparison.Ordinal))
                return attribute;
        }

        return _byId.GetValueOrDefault(id) as RAttribute;
    }

    /// <summary>
    /// What <c>entity.revisions</c> keys on (decision 42, section 8), worked out once per entity: the files of the entity and its bases; what
    /// decides which lifecycles govern it, as <c>entity.trigger_lifecycles</c> keys it (the process kind, the referrers of the entity and each
    /// base, the stereotype naming key and the carrier sets) and each governing lifecycle's file; the holder of each attribute the block names
    /// (a stereotype's file for a carried attribute); and the relations with an end the entity reaches: those with an end at the entity or a
    /// base, those an entry names, and while some end has several types, the end keys <c>entity.relations</c> adds. A new relation at a base
    /// changes that base's referrers.
    /// </summary>
    /// <param name="source">The entity.</param>
    /// <param name="merged">Its merged block.</param>
    /// <returns>The keys, ordinal and distinct.</returns>
    private IReadOnlyList<string> RevisionKeys(Entity source, MergedRevisions merged)
    {
        if (_revisionKeys.TryGetValue(source.Id, out var memo))
            return memo;
        var keys = new DependencySet(Keys).Add(ProcessKind).Add(SubjectCoverage.NamingKey);
        foreach (var key in SubjectCoverage.Of(Model).Keys)
            keys.Add(SubjectCoverage.CarrierSetKey(key));
        foreach (var id in SubjectCoverage.Chain(Model, source))
        {
            keys.Element(id).Referrers(id);
            foreach (var relation in _relationsByEntity.GetValueOrDefault(id) ?? [])
                keys.Element(relation.Id);
            if (Model.Get<Entity>(id)?.Revisions?.Links is { } entries)
            {
                foreach (var entry in entries)
                {
                    if (Model.TryGetEntry(entry.End, out var end))
                        keys.Element(end.OwnerId);
                }
            }
        }

        foreach (var rule in merged.Lifecycles)
            keys.Element(rule.Lifecycle.Id);
        foreach (var attribute in merged.Attributes.Select(a => a.Attribute).Prepend(merged.BoundAttribute).OfType<string>())
        {
            if (Model.TryGetEntry(attribute, out var holder))
                keys.Element(holder.OwnerId);
        }

        keys.AddRange(EndNamingKeys());
        IReadOnlyList<string> list = keys.ToList();
        _revisionKeys[source.Id] = list;
        return list;
    }

    /// <summary>
    /// The replay keys of a scenario whose subject's type has revisions (section 8): what that type's <c>entity.revisions</c> keys on, beside
    /// the rights keys. Its steps already key on the entities of its objects and their bases, so a type gaining a block replays them. None
    /// for a scenario whose subject's type has no block, and none in a project without one.
    /// </summary>
    private IReadOnlyList<string> RevisionReplayKeys(Scenario scenario)
    {
        var coverage = RevisionCoverage.Of(Model);
        if (!coverage.Any)
            return [];
        var subject = scenario.Objects.FirstOrDefault(o => string.Equals(o.Id, scenario.Subject, StringComparison.Ordinal));
        return subject is not null && Model.Get<Entity>(subject.Entity) is { } entity && coverage.For(entity) is { } merged
            ? RevisionKeys(entity, merged)
            : [];
    }
}
