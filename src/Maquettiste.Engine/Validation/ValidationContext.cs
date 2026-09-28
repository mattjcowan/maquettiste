using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>Where an attribute of an entity's flattened list comes from.</summary>
internal enum AttributeSource { Own, Inherited, Virtual }

/// <summary>One attribute of an entity's flattened list (engine-design.md section 7.2, before the <c>order</c> sort).</summary>
/// <param name="Attribute">The attribute.</param>
/// <param name="Source">Where it comes from.</param>
/// <param name="From">The declaring entity id (own, inherited) or the stereotype key (virtual).</param>
/// <param name="OwnIndex">The index in the element's own <c>attributes</c> array, for own attributes; else -1.</param>
internal sealed record FlatAttribute(ModelAttribute Attribute, AttributeSource Source, string From, int OwnIndex);

/// <summary>A navigation generated on an entity by a relation end.</summary>
/// <param name="Document">The relation's document.</param>
/// <param name="EndIndex">The end declaring the navigation.</param>
/// <param name="Name">The navigation name.</param>
internal sealed record NavigationSite(ElementDocument Document, int EndIndex, string Name);

/// <summary>
/// Everything built-in rules share for one validation run: the active documents and lazily built, thread-safe indexes over the
/// whole model (names per scope, flattened attributes, navigations, mappings, overlays). Every index is built in document path
/// order, so "the first" of duplicates is always the ordinally first file.
/// </summary>
internal sealed class ValidationContext
{
    private readonly ConcurrentDictionary<string, ImmutableArray<FlatAttribute>> _flat = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _valueObjectReach = new(StringComparer.Ordinal);
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<ElementDocument>>> _names;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<NavigationSite>>> _navigations;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<ElementDocument>>> _compositions;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<ElementDocument>>> _mappings;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<ElementDocument>>> _overlays;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<ElementDocument>>> _physicalNames;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<string>>> _derived;

    /// <summary>Creates a context.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="documents">The active documents (duplicates and ignored singletons removed), ordinal by path.</param>
    /// <param name="walker">The reference walker.</param>
    /// <param name="ruleNames">The registered JavaScript rule names (without <c>x/</c>), or <see langword="null"/> when unknown.</param>
    public ValidationContext(ModelSnapshot model, IReadOnlyList<ElementDocument> documents, ReferenceWalker walker, FrozenSet<string>? ruleNames)
    {
        Model = model;
        Documents = documents;
        Walker = walker;
        RuleNames = ruleNames;
        _names = new(BuildNames);
        _navigations = new(BuildNavigations);
        _compositions = new(BuildCompositions);
        _mappings = new(BuildMappings);
        _overlays = new(BuildOverlays);
        _physicalNames = new(BuildPhysicalNames);
        _derived = new(BuildDerived);
    }

    /// <summary>A context over the same model and documents, sharing every index already built, with other rule names.</summary>
    private ValidationContext(ValidationContext other, FrozenSet<string>? ruleNames)
    {
        Model = other.Model;
        Documents = other.Documents;
        Walker = other.Walker;
        RuleNames = ruleNames;
        _flat = other._flat;
        _valueObjectReach = other._valueObjectReach;
        _names = other._names;
        _navigations = other._navigations;
        _compositions = other._compositions;
        _mappings = other._mappings;
        _overlays = other._overlays;
        _physicalNames = other._physicalNames;
        _derived = other._derived;
    }

    /// <summary>Returns a context that shares this one's indexes but knows the registered JavaScript rule names.</summary>
    /// <param name="ruleNames">The registered rule names (without <c>x/</c>), or <see langword="null"/> when unknown.</param>
    /// <returns>The context.</returns>
    public ValidationContext WithRuleNames(FrozenSet<string>? ruleNames) => new(this, ruleNames);

    /// <summary>The snapshot.</summary>
    public ModelSnapshot Model { get; }

    /// <summary>The active documents, ordinal by path.</summary>
    public IReadOnlyList<ElementDocument> Documents { get; }

    /// <summary>The reference walker.</summary>
    public ReferenceWalker Walker { get; }

    /// <summary>The registered JavaScript rule names (without <c>x/</c>); <see langword="null"/> when the scripts could not be loaded.</summary>
    public FrozenSet<string>? RuleNames { get; }

    /// <summary>Returns the document of an element id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The document, or <see langword="null"/>.</returns>
    public ElementDocument? DocumentOf(string id) => Model.GetDocument(id);

    /// <summary>Whether an entity is the base of another entity.</summary>
    /// <param name="entityId">The entity id.</param>
    /// <returns><see langword="true"/> when some entity derives from it.</returns>
    public bool HasDerived(string entityId) => _derived.Value.ContainsKey(entityId);

    /// <summary>Returns the entities that name an entity as their <c>base</c>, in path order.</summary>
    /// <param name="entityId">The entity id.</param>
    /// <returns>The derived entity ids.</returns>
    public ImmutableArray<string> DerivedOf(string entityId) => _derived.Value.TryGetValue(entityId, out var list) ? list : [];

    /// <summary>Returns an entity's ancestors through <c>base</c>, nearest first; an inheritance cycle is cut at the first repeat.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The ancestors (the entity itself excluded).</returns>
    public IEnumerable<Entity> Ancestors(Entity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
        for (var current = entity.Base; current is not null && seen.Add(current) && Model.Get<Entity>(current) is { } ancestor; current = ancestor.Base)
            yield return ancestor;
    }

    /// <summary>Returns an entity's descendants (derived entities, recursively), breadth first in path order; cycles are cut.</summary>
    /// <param name="entityId">The entity id.</param>
    /// <returns>The descendant entities (the entity itself excluded).</returns>
    public IEnumerable<Entity> Descendants(string entityId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { entityId };
        var queue = new Queue<string>();
        queue.Enqueue(entityId);
        while (queue.Count > 0)
        {
            foreach (var derivedId in DerivedOf(queue.Dequeue()))
            {
                if (!seen.Add(derivedId) || Model.Get<Entity>(derivedId) is not { } derived)
                    continue;
                yield return derived;
                queue.Enqueue(derivedId);
            }
        }
    }

    private FrozenDictionary<string, ImmutableArray<string>> BuildDerived()
    {
        var map = new Dictionary<string, ImmutableArray<string>.Builder>(StringComparer.Ordinal);
        foreach (var doc in Documents)
        {
            if (doc.Element is Entity { Base: { } baseId } entity)
            {
                if (!map.TryGetValue(baseId, out var list))
                    map[baseId] = list = ImmutableArray.CreateBuilder<string>();
                list.Add(entity.Id);
            }
        }

        return map.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns an entity's flattened attributes: base attributes (root first, recursively flattened), own attributes, then the
    /// virtual attributes of its stereotypes in stereotype order. Inheritance cycles are cut at the first repeated entity.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The attributes.</returns>
    public ImmutableArray<FlatAttribute> Flatten(Entity entity) =>
        _flat.GetOrAdd(entity.Id, _ => Flatten(entity, new HashSet<string>(StringComparer.Ordinal)));

    private ImmutableArray<FlatAttribute> Flatten(Entity entity, HashSet<string> visiting)
    {
        visiting.Add(entity.Id);
        var list = ImmutableArray.CreateBuilder<FlatAttribute>();
        if (entity.Base is { } baseId && Model.Get<Entity>(baseId) is { } baseEntity && !visiting.Contains(baseEntity.Id))
        {
            foreach (var inherited in Flatten(baseEntity, visiting))
                list.Add(inherited with { Source = AttributeSource.Inherited, OwnIndex = -1 });
        }

        for (var i = 0; i < entity.Attributes.Count; i++)
            list.Add(new FlatAttribute(entity.Attributes[i], AttributeSource.Own, entity.Id, i));
        list.AddRange(VirtualAttributes(entity));
        return list.ToImmutable();
    }

    /// <summary>Returns the virtual attributes an element's stereotypes add, in stereotype then array order.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The attributes.</returns>
    public IEnumerable<FlatAttribute> VirtualAttributes(ElementBase element)
    {
        foreach (var key in element.Stereotypes)
        {
            if (Model.GetStereotype(key) is not { } stereotype)
                continue;
            foreach (var attribute in stereotype.Attributes)
                yield return new FlatAttribute(attribute, AttributeSource.Virtual, key, -1);
        }
    }

    /// <summary>Returns the first document that holds a name key (see <see cref="NameKeys"/>).</summary>
    /// <param name="key">The scope key.</param>
    /// <returns>The first document with that key.</returns>
    public ElementDocument FirstWithName(string key) => _names.Value[key][0];

    /// <summary>Returns every document that holds a name key (see <see cref="NameKeys"/>), in path order.</summary>
    /// <param name="key">The scope key.</param>
    /// <returns>The documents.</returns>
    public ImmutableArray<ElementDocument> WithName(string key) => _names.Value.TryGetValue(key, out var list) ? list : [];

    /// <summary>The scoped name keys of an element for MQ3001: scope and name, per namespace the element takes part in.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The keys with a description of the namespace.</returns>
    public static IEnumerable<(string Key, string Namespace)> NameKeys(Element element)
    {
        switch (element)
        {
            case Package p when p.Name.Length > 0:
                yield return ("package|" + p.Parent + "|" + p.Name, p.Parent is null ? "root packages" : "packages under the same parent");
                break;
            case Entity or ValueObject or ScalarType or EnumType when element.Name.Length > 0:
                var package = element switch { Entity e => e.Package, ValueObject v => v.Package, ScalarType s => s.Package, EnumType n => n.Package, _ => null };
                yield return ("type|" + package + "|" + element.Name, "types of the same package (entities, value objects, scalar types and enums)");
                break;
            case Database d when d.Name.Length > 0:
                yield return ("database|" + d.Name, "databases");
                break;
            case Stereotype s:
                yield return ("stereotype|" + s.Key, "stereotype keys");
                break;
        }
    }

    private FrozenDictionary<string, ImmutableArray<ElementDocument>> BuildNames()
    {
        var names = new Dictionary<string, ImmutableArray<ElementDocument>.Builder>(StringComparer.Ordinal);
        foreach (var doc in Documents)
        {
            foreach (var (key, _) in NameKeys(doc.Element))
                Append(names, key, doc);
        }

        return Freeze(names);
    }

    private static void Append<T>(Dictionary<string, ImmutableArray<T>.Builder> map, string key, T item)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = ImmutableArray.CreateBuilder<T>();
        list.Add(item);
    }

    private static FrozenDictionary<string, ImmutableArray<T>> Freeze<T>(Dictionary<string, ImmutableArray<T>.Builder> map) =>
        map.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal);

    /// <summary>Returns the navigations generated on an entity, in (relation path, end) order.</summary>
    /// <param name="entityId">The entity id.</param>
    /// <returns>The navigations.</returns>
    public ImmutableArray<NavigationSite> NavigationsOn(string entityId) =>
        _navigations.Value.TryGetValue(entityId, out var list) ? list : [];

    /// <summary>Returns the entities that carry a navigation of an end: the entities of every other end of the relation.</summary>
    /// <param name="relation">The relation.</param>
    /// <param name="endIndex">The end.</param>
    /// <returns>The entity ids, one per other end.</returns>
    public static IEnumerable<string> NavigationHolders(Relation relation, int endIndex)
    {
        for (var j = 0; j < relation.Ends.Count; j++)
        {
            if (j != endIndex)
                yield return relation.Ends[j].Entity;
        }
    }

    private FrozenDictionary<string, ImmutableArray<NavigationSite>> BuildNavigations()
    {
        var map = new Dictionary<string, ImmutableArray<NavigationSite>.Builder>(StringComparer.Ordinal);
        foreach (var doc in Documents)
        {
            if (doc.Element is not Relation relation)
                continue;
            for (var i = 0; i < relation.Ends.Count; i++)
            {
                var name = relation.Ends[i].Navigation;
                if (name.Length == 0)
                    continue;
                foreach (var holder in NavigationHolders(relation, i).Distinct(StringComparer.Ordinal))
                {
                    if (!map.TryGetValue(holder, out var list))
                        map[holder] = list = ImmutableArray.CreateBuilder<NavigationSite>();
                    list.Add(new NavigationSite(doc, i, name));
                }
            }
        }

        return map.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the owner and child ends of a binary composition: the owner is the end with <c>max</c> 1 (end 0 when both are 1);
    /// <see langword="null"/> when the relation is not a binary composition or has no end with <c>max</c> 1.
    /// </summary>
    /// <param name="relation">The relation.</param>
    /// <returns>The owner and child end indexes.</returns>
    public static (int Owner, int Child)? CompositionEnds(Relation relation)
    {
        if (relation.RelationKind != RelationKind.Composition || relation.Ends.Count != 2)
            return null;
        if (relation.Ends[0].Max == MaxCardinality.One)
            return (0, 1);
        if (relation.Ends[1].Max == MaxCardinality.One)
            return (1, 0);
        return null;
    }

    /// <summary>Returns the composition relations whose child is an entity, in path order.</summary>
    /// <param name="entityId">The child entity id.</param>
    /// <returns>The relation documents.</returns>
    public ImmutableArray<ElementDocument> CompositionsOwning(string entityId) =>
        _compositions.Value.TryGetValue(entityId, out var list) ? list : [];

    private FrozenDictionary<string, ImmutableArray<ElementDocument>> BuildCompositions()
    {
        var map = new Dictionary<string, ImmutableArray<ElementDocument>.Builder>(StringComparer.Ordinal);
        foreach (var doc in Documents)
        {
            if (doc.Element is Relation relation && CompositionEnds(relation) is { } ends)
            {
                var child = relation.Ends[ends.Child].Entity;
                if (!map.TryGetValue(child, out var list))
                    map[child] = list = ImmutableArray.CreateBuilder<ElementDocument>();
                list.Add(doc);
            }
        }

        return map.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal);
    }

    /// <summary>Whether a value object can reach another through attribute types (containment, collections included).</summary>
    /// <param name="fromId">The starting value object.</param>
    /// <param name="toId">The value object to reach.</param>
    /// <returns><see langword="true"/> when <paramref name="toId"/> is reachable from <paramref name="fromId"/> (or equal).</returns>
    public bool ValueObjectReaches(string fromId, string toId) =>
        _valueObjectReach.GetOrAdd(fromId + ">" + toId, _ =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            queue.Enqueue(fromId);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (id == toId)
                    return true;
                if (!seen.Add(id) || Model.Get<ValueObject>(id) is not { } vo)
                    continue;
                foreach (var attribute in vo.Attributes)
                {
                    if (attribute.Type.Ref is { } next && Model.Get<ValueObject>(next) is not null)
                        queue.Enqueue(next);
                }
            }

            return false;
        });

    /// <summary>Returns the mappings of a target (entity or relation) in a database, in path order.</summary>
    /// <param name="databaseId">The database id.</param>
    /// <param name="targetId">The entity or relation id.</param>
    /// <returns>The mapping documents.</returns>
    public ImmutableArray<ElementDocument> MappingsOf(string databaseId, string targetId) =>
        _mappings.Value.TryGetValue(databaseId + "|" + targetId, out var list) ? list : [];

    private FrozenDictionary<string, ImmutableArray<ElementDocument>> BuildMappings()
    {
        var map = new Dictionary<string, ImmutableArray<ElementDocument>.Builder>(StringComparer.Ordinal);
        foreach (var doc in Documents)
        {
            if (doc.Element is Mapping { } mapping && (mapping.Entity ?? mapping.Relation) is { } target)
            {
                var key = mapping.Database + "|" + target;
                if (!map.TryGetValue(key, out var list))
                    map[key] = list = ImmutableArray.CreateBuilder<ElementDocument>();
                list.Add(doc);
            }
        }

        return map.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal);
    }

    /// <summary>The key of the synthesized table a table overlay targets, or <see langword="null"/> for designed and imported tables.</summary>
    /// <param name="table">The table.</param>
    /// <returns>The synthesized table key (engine-design.md section 7.3).</returns>
    public static string? OverlayTarget(Table table)
    {
        if (table.Origin != TableOrigin.Synthesized)
            return null;
        if (table.Entity is { } entity)
            return (table.Attribute is { } attribute ? entity + "." + attribute : entity) + "@" + table.Database;
        return (table.Relation ?? table.Enum) is { } target ? target + "@" + table.Database : null;
    }

    /// <summary>Returns the overlay files targeting one synthesized table, in path order.</summary>
    /// <param name="targetKey">The synthesized table key.</param>
    /// <returns>The overlay documents.</returns>
    public ImmutableArray<ElementDocument> OverlaysOf(string targetKey) =>
        _overlays.Value.TryGetValue(targetKey, out var list) ? list : [];

    private FrozenDictionary<string, ImmutableArray<ElementDocument>> BuildOverlays()
    {
        var map = new Dictionary<string, ImmutableArray<ElementDocument>.Builder>(StringComparer.Ordinal);
        foreach (var doc in Documents)
        {
            if (doc.Element is Table table && OverlayTarget(table) is { } key)
            {
                if (!map.TryGetValue(key, out var list))
                    map[key] = list = ImmutableArray.CreateBuilder<ElementDocument>();
                list.Add(doc);
            }
        }

        return map.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutable(), StringComparer.Ordinal);
    }

    /// <summary>Returns the first file-backed table, view or sequence holding a physical name key (see <see cref="PhysicalNameKey"/>).</summary>
    /// <param name="key">The key.</param>
    /// <returns>The first document.</returns>
    public ElementDocument FirstWithPhysicalName(string key) => _physicalNames.Value[key][0];

    /// <summary>Returns every file-backed table, view or sequence holding a physical name key, in path order.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The documents.</returns>
    public ImmutableArray<ElementDocument> WithPhysicalName(string key) => _physicalNames.Value.TryGetValue(key, out var list) ? list : [];

    /// <summary>
    /// The MQ4002 key of a table, view or sequence with an explicit name: database, effective schema name and upper-cased name
    /// (tables, views and sequences share one namespace per schema; names compare case-insensitively).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The key, or <see langword="null"/> when the element has no explicit name.</returns>
    public string? PhysicalNameKey(Element element)
    {
        var (databaseId, schemaId) = element switch
        {
            Table t => (t.Database, t.Schema),
            View v => (v.Database, v.Schema),
            Sequence s => (s.Database, s.Schema),
            _ => (null, null),
        };
        if (databaseId is null || element.Name.Length == 0)
            return null;
        return databaseId + "|" + SchemaName(databaseId, schemaId) + "|" + element.Name.ToUpperInvariant();
    }

    /// <summary>Returns the effective schema name of a physical element: its schema's name, else the database's default schema.</summary>
    /// <param name="databaseId">The database id.</param>
    /// <param name="schemaId">The schema id, or <see langword="null"/> for the default schema.</param>
    /// <returns>The schema name (upper-cased), or an empty string when the dialect has no schemas.</returns>
    public string SchemaName(string databaseId, string? schemaId)
    {
        var database = Model.Get<Database>(databaseId);
        if (schemaId is not null)
        {
            if (Model.TryGetEntry(schemaId, out var entry) && entry.Kind == "schema" && database is not null)
            {
                foreach (var schema in database.Schemas)
                {
                    if (schema.Id == schemaId)
                        return schema.Name.ToUpperInvariant();
                }
            }

            return schemaId;
        }

        return (database?.DefaultSchema ?? DialectInfo.DefaultSchema(database?.Dialect)).ToUpperInvariant();
    }

    private FrozenDictionary<string, ImmutableArray<ElementDocument>> BuildPhysicalNames()
    {
        var map = new Dictionary<string, ImmutableArray<ElementDocument>.Builder>(StringComparer.Ordinal);
        foreach (var doc in Documents)
        {
            if (doc.Element is Table or View or Sequence && PhysicalNameKey(doc.Element) is { } key)
                Append(map, key, doc);
        }

        return Freeze(map);
    }
}
