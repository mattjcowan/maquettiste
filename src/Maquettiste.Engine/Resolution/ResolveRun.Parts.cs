using System.Collections.Frozen;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The parts of a whole record and what a copy carries over (parts-and-copy-design.md): each entity's <c>parts</c>, from <see cref="PartTree"/>,
/// with each composition's foreign key per database, and each entity's <c>copy</c>, its block merged through its bases. Runs after the databases
/// and the processes, whose foreign keys and states it reads. An entity with no parts gets an empty list that still keys on what could give it
/// one; an entity with no copy block in its family keeps a null <c>copy</c>.
/// </summary>
internal sealed partial class ResolveRun
{
    /// <summary>After every database and process is built: each entity's <c>parts</c> and <c>copy</c>.</summary>
    /// <param name="databases">The resolved databases, whose tables hold the compositions' foreign keys.</param>
    private void FinishPartsAndCopy(IReadOnlyList<RDatabase> databases)
    {
        var anyCopy = Model.All<Entity>().Any(e => e.Copy is not null);
        var anyParts = PartTree.Any(Model);
        var keysByRelation = anyParts ? CompositionKeys(databases) : [];
        foreach (var (id, source) in _entitySources)
        {
            if (!_entities.TryGetValue(id, out var r) || r.Id.Length == 0)
                continue;
            var tree = anyParts ? PartTree.Of(Model, source) : null;
            var keys = PartKeys(source, tree);
            var parts = tree is null || tree.Nodes.Count == 0 ? [] : Parts(r, tree, keysByRelation);
            r.Parts = new KeyedList<RPart>(parts, keys);
            if (anyCopy && SubjectCoverage.Chain(Model, source).Any(b => Model.Get<Entity>(b)?.Copy is not null))
                r.Copy = Copy(r, source, parts, keys);
        }
    }

    /// <summary>
    /// By composition id, then database name: every foreign key of the composition in the database's tables, in table order, where the
    /// composition is stored with foreign keys there. With one table per derived type, each table that holds parts has a key of its own.
    /// </summary>
    private static Dictionary<string, SortedDictionary<string, List<RForeignKey>>> CompositionKeys(IReadOnlyList<RDatabase> databases)
    {
        var keys = new Dictionary<string, SortedDictionary<string, List<RForeignKey>>>(StringComparer.Ordinal);
        foreach (var database in databases)
        {
            foreach (var table in database.Tables)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    if (fk.Relation is not { RelationKind: "composition" } relation
                        || relation.Mappings.GetValueOrDefault(database.Name) is not { Shape: "foreign-key" })
                        continue;
                    if (!keys.TryGetValue(relation.Id, out var byDatabase))
                        keys[relation.Id] = byDatabase = new SortedDictionary<string, List<RForeignKey>>(StringComparer.Ordinal);
                    if (!byDatabase.TryGetValue(database.Name, out var list))
                        byDatabase[database.Name] = list = [];
                    list.Add(fk);
                }
            }
        }

        return keys;
    }

    /// <summary>The parts as templates read them, in the tree's order safe for foreign keys.</summary>
    private List<RPart> Parts(REntity record, PartTree tree, Dictionary<string, SortedDictionary<string, List<RForeignKey>>> keysByRelation)
    {
        var built = new RPart?[tree.Nodes.Count];
        for (var i = 0; i < tree.Nodes.Count; i++)
        {
            var node = tree.Nodes[i];
            if (!_relations.TryGetValue(node.Relation.Id, out var relation) || _byId.GetValueOrDefault(node.End.Id) is not REnd end
                || _byId.GetValueOrDefault(node.Owner.Id) is not REnd ownerEnd || !_entities.TryGetValue(node.EntityId, out var entity))
                continue;
            var parent = node.Parent >= 0 ? built[node.Parent] : null;
            if (node.Parent >= 0 && parent is null)
                continue;
            var owner = parent?.Entity ?? record;
            var keys = node.Owner.SingleEntity is not null ? keysByRelation.GetValueOrDefault(relation.Id) : null;

            built[i] = new RPart
            {
                Id = parent is null ? end.Id : parent.Id + "/" + end.Id,
                Relation = relation,
                OwnerEnd = ownerEnd,
                End = end,
                Entity = entity,
                Owner = owner,
                Parent = parent,
                Depth = node.Depth,
                IsRecursive = node.Recursive,
                InCycle = tree.InCycle[i],
                ForeignKeys = keys is null ? FrozenDictionary<string, IReadOnlyList<RForeignKey>>.Empty
                    : keys.ToFrozenDictionary(k => k.Key, IReadOnlyList<RForeignKey> (k) => k.Value, StringComparer.Ordinal),
            };
        }

        var parts = new List<RPart>(tree.Nodes.Count);
        foreach (var i in tree.Order)
        {
            if (built[i] is { } part)
                parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// What <c>entity.parts</c> keys on: for the entity and each part's entity, the files of it and its bases, their referrers (a relation that
    /// starts or stops naming one), and the files of the relations at them (a relation that becomes a composition, or a link that orders two
    /// parts). While some end has several types, also the end keys <c>entity.relations</c> adds.
    /// </summary>
    private IReadOnlyList<string> PartKeys(Entity source, PartTree? tree)
    {
        var keys = new DependencySet(Keys);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(Entity entity)
        {
            foreach (var id in SubjectCoverage.Chain(Model, entity))
            {
                if (!seen.Add(id))
                    continue;
                keys.Element(id).Referrers(id);
                foreach (var relation in _relationsByEntity.GetValueOrDefault(id) ?? [])
                    keys.Element(relation.Id);
            }
        }

        Add(source);
        foreach (var node in tree?.Nodes ?? [])
        {
            keys.Element(node.Relation.Id);
            if (Model.Get<Entity>(node.EntityId) is { } part)
                Add(part);
        }

        keys.AddRange(EndNamingKeys());
        return keys.ToList();
    }

    /// <summary>An entity's <c>copy</c>: its family's blocks merged, farthest base first, each nearer entry replacing a farther one.</summary>
    private RCopy Copy(REntity entity, Entity source, List<RPart> parts, IReadOnlyList<string> partKeys)
    {
        var chain = SubjectCoverage.Chain(Model, source);
        var attributes = new Dictionary<string, (AttributeOnCopy OnCopy, Entity From)>(StringComparer.Ordinal);
        var links = new List<(string End, LinkOnCopy OnCopy, Entity From)>();
        var kinds = new Dictionary<string, (CopyPartEntry Entry, Entity From)>(StringComparer.Ordinal);
        var keys = new DependencySet(Keys).AddRange(partKeys);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (Model.Get<Entity>(chain[i]) is not { Copy: { } block } level)
                continue;
            keys.Element(level.Id);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in block.Attributes ?? [])
            {
                if (seen.Add(entry.Attribute))
                    attributes[entry.Attribute] = (entry.OnCopy, level);
                AddHolder(keys, entry.Attribute);
            }

            seen.Clear();
            foreach (var entry in block.Links ?? [])
            {
                if (!seen.Add(entry.End))
                    continue;
                links.RemoveAll(l => string.Equals(l.End, entry.End, StringComparison.Ordinal));
                links.Add((entry.End, entry.OnCopy, level));
                AddHolder(keys, entry.End);
            }

            seen.Clear();
            foreach (var entry in block.Parts ?? [])
            {
                if (seen.Add(entry.End))
                    kinds[entry.End] = (entry, level);
                foreach (var named in (entry.Attributes ?? []).Select(a => a.Attribute).Append(entry.Choose?.State).Append(entry.Choose?.LatestBy).OfType<string>())
                    AddHolder(keys, named);
            }
        }

        var copy = new RCopy
        {
            Attributes = [.. entity.Attributes.Where(a => attributes.TryGetValue(a.Id, out var rule) && rule.OnCopy != AttributeOnCopy.Copy)
                .Select(a => CopyAttribute(a, attributes[a.Id].OnCopy, attributes[a.Id].From)).OfType<RCopyAttribute>()],
            Links = [.. links.Where(l => l.OnCopy != LinkOnCopy.Copy && _byId.GetValueOrDefault(l.End) is REnd && _entities.ContainsKey(l.From.Id))
                .Select(l => new RCopyLink { End = (REnd)_byId[l.End], OnCopy = ResolutionValues.Kebab(l.OnCopy), From = _entities[l.From.Id] })],
        };

        var left = new HashSet<RPart>();
        var copied = new List<RCopyPart>();
        foreach (var part in parts)
        {
            if (part.Parent is { } parent && left.Contains(parent))
            {
                left.Add(part);
                continue;
            }

            if (!kinds.TryGetValue(part.End.Id, out var kind))
            {
                copied.Add(new RCopyPart { Part = part });
                continue;
            }

            if (kind.Entry.OnCopy == PartOnCopy.None)
            {
                left.Add(part);
                continue;
            }

            copied.Add(new RCopyPart
            {
                Part = part,
                OnCopy = ResolutionValues.Kebab(kind.Entry.OnCopy),
                Choose = kind.Entry.OnCopy == PartOnCopy.One && kind.Entry.Choose is { } choose ? Choice(part, choose, kind.From, keys) : null,
                Attributes = PartAttributes(part.Entity, kind.Entry.Attributes ?? [], kind.From),
                From = _entities.GetValueOrDefault(kind.From.Id),
            });
        }

        copy.Parts = copied;
        copy.Keys = keys.ToList();
        return copy;
    }

    /// <summary>A part kind's attribute entries, in the part entity's attribute order, the first entry for an attribute counting.</summary>
    private List<RCopyAttribute> PartAttributes(REntity part, IReadOnlyList<CopyAttributeEntry> entries, Entity from)
    {
        if (entries.Count == 0)
            return [];
        var rules = new Dictionary<string, AttributeOnCopy>(StringComparer.Ordinal);
        foreach (var entry in entries)
            rules.TryAdd(entry.Attribute, entry.OnCopy);
        return [.. part.Attributes.Where(a => rules.TryGetValue(a.Id, out var rule) && rule != AttributeOnCopy.Copy)
            .Select(a => CopyAttribute(a, rules[a.Id], from)).OfType<RCopyAttribute>()];
    }

    private RCopyAttribute? CopyAttribute(RAttribute attribute, AttributeOnCopy onCopy, Entity from) =>
        _entities.TryGetValue(from.Id, out var by) ? new RCopyAttribute { Attribute = attribute, OnCopy = ResolutionValues.Kebab(onCopy), From = by } : null;

    /// <summary>A part kind's choice: its condition typed against the part's attributes, its state and its latest-by attribute.</summary>
    private RCopyChoice Choice(RPart part, CopyChoice choose, Entity from, DependencySet keys)
    {
        var choice = new RCopyChoice
        {
            State = choose.State is { } state ? _byId.GetValueOrDefault(state) as RState : null,
            LatestBy = choose.LatestBy is { } latest ? part.Entity.Attributes.FirstOrDefault(a => string.Equals(a.Id, latest, StringComparison.Ordinal)) : null,
        };
        if (choose.Condition is null || ExprResolution.Type(Model, from, PartTree.ChoiceKey(part.End.Id)) is not { } typed)
            return choice;
        var byId = part.Entity.Attributes.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var root = new ExprRoot(typed.Tree, typed.Typed, typed.Scope, ExprSiteKind.PartChoice,
            ExprResolution.ApiColumns(Model, from.Id, Model.GetDocument(from.Id)?.Path));
        choice.Condition = ExprResolution.Build(root, member => AttributeTarget(byId, member.Id), EnumMemberOf);
        foreach (var id in ExprResolution.Holders(Model, typed.Typed).Concat(ExprResolution.Names(Model, typed.Typed)))
            keys.Element(id);
        keys.AddRange(ExprResolution.CoverageKeys(Model, typed.Typed));
        return choice;
    }

    /// <summary>Adds the file that holds a sub-element an entry names: an attribute's entity or stereotype, an end's relation, a state's process.</summary>
    private void AddHolder(DependencySet keys, string id)
    {
        if (Model.TryGetEntry(id, out var entry))
            keys.Element(entry.OwnerId);
    }
}
