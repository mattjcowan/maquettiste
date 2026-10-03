using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>The conceptual layer: packages, types, entities (flattened attributes, section 7.2), relations and navigations.</summary>
internal sealed partial class ResolveRun
{
    private readonly Dictionary<string, RPackage> _packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, REntity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entity> _entitySources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RValueObject> _valueObjects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, REnum> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumType> _enumSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RScalarType> _scalars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RRelation> _relations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Relation> _relationSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RCategory> _categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RNavigation>> _navigations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RRelation>> _relationsByEntity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<string, REntityMapping>> _entityMappings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<string, REntityBinding>> _entityBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<string, RRelationMapping>> _relationMappings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<string, RSequence>> _keySequences = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Database, string Element), Mapping> _mappings = [];
    private readonly Dictionary<string, REntity> _promoted = new(StringComparer.Ordinal);
    private readonly List<RRelation> _promotedRelations = [];
    private readonly Dictionary<string, List<(RAttribute Attribute, string ColumnKey)>> _promotedKeys = new(StringComparer.Ordinal);
    private List<RPackage> _packageOrder = [];
    private List<RValueObject> _valueObjectOrder = [];
    private List<REnum> _enumOrder = [];
    private List<RScalarType> _scalarOrder = [];
    private List<REntity> _entityOrder = [];
    private List<RRelation> _relationOrder = [];
    private IReadOnlyList<string> _entityMembership = [];
    private readonly Dictionary<string, List<string>> _mappingKeysByTarget = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _categoryTreeOf = new(StringComparer.Ordinal); // category id -> its tree's id
    private IReadOnlyList<string>? _conceptualKeys;

    /// <summary><c>e:</c> keys of every database file.</summary>
    public IReadOnlyList<string> DatabaseKeys { get; private set; } = [];

    /// <summary>
    /// <c>e:</c> keys of every conceptual file and mapping: which tables a database holds, and in which order, can change with any
    /// of them (a new collection attribute, a package move, an ignored mapping), so database table lists depend on all of them.
    /// </summary>
    public IReadOnlyList<string> ConceptualElementKeys => _conceptualKeys ??= [.. Model.Documents
        .Where(d => d.Element.Kind is ElementKind.Entity or ElementKind.Relation or ElementKind.Enum or ElementKind.ValueObject
            or ElementKind.ScalarType or ElementKind.Mapping or ElementKind.Stereotype or ElementKind.Package)
        .Select(d => Keys.Element(d.Element.Id))];

    private IReadOnlyList<string> MappingKeysOf(string id) => _mappingKeysByTarget.TryGetValue(id, out var keys) ? keys : [];

    /// <summary>Resolved entities (promoted ones excluded) in section 7.1 order.</summary>
    public IReadOnlyList<REntity> EntityOrder => _entityOrder;

    /// <summary>Resolved relations (promotion relations excluded) in section 7.1 order.</summary>
    public IReadOnlyList<RRelation> RelationOrder => _relationOrder;

    /// <summary>The source entity of a resolved entity id.</summary>
    public Entity? EntitySource(string id) => _entitySources.GetValueOrDefault(id);

    /// <summary>The source relation of a resolved relation id.</summary>
    public Relation? RelationSource(string id) => _relationSources.GetValueOrDefault(id);

    /// <summary>A resolved entity by id.</summary>
    public REntity? EntityById(string id) => _entities.GetValueOrDefault(id);

    /// <summary>A resolved enum by id.</summary>
    public REnum? EnumById(string id) => _enums.GetValueOrDefault(id);

    /// <summary>The source enum by id.</summary>
    public EnumType? EnumSource(string id) => _enumSources.GetValueOrDefault(id);

    /// <summary>The mapping of an element in a database: the ordinally first by id when there are several (MQ4004).</summary>
    public Mapping? MappingOf(string databaseId, string elementId) => _mappings.GetValueOrDefault((databaseId, elementId));

    /// <summary>The navigations generated on an entity.</summary>
    public IReadOnlyList<RNavigation> NavigationsOf(string entityId) => _navigations.TryGetValue(entityId, out var list) ? list : [];

    /// <summary>
    /// The navigations of an entity with those of its ancestors (nearest first; a name declared closer hides an inherited one),
    /// as a derived entity's instances carry them too.
    /// </summary>
    /// <remarks>
    /// Memoized: every database and the finishing pass ask for every entity. Adding a navigation (promotion does, during the
    /// database runs) clears the memo, so a list is never stale. Safe from the parallel finishing pass, which adds none.
    /// </remarks>
    public IReadOnlyList<RNavigation> InheritedNavigations(string entityId)
    {
        if (_inheritedNavigations.TryGetValue(entityId, out var memo))
            return memo;
        IReadOnlyList<RNavigation> list = !_entitySources.TryGetValue(entityId, out var source)
            ? NavigationsOf(entityId)
            : Chain(source).SelectMany(e => NavigationsOf(e.Id)).DistinctBy(n => n.Name, StringComparer.Ordinal).ToList();
        _inheritedNavigations.TryAdd(entityId, list);
        return list;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<RNavigation>> _inheritedNavigations = new(StringComparer.Ordinal);

    /// <summary>The membership keys of every entity and relation list (promotion can add entities and relations).</summary>
    public IReadOnlyList<string> EntityMembership => _entityMembership;

    /// <summary>Records an entity's mapping in a database.</summary>
    public void SetEntityMapping(REntity entity, string database, REntityMapping mapping)
    {
        mapping.TemplateDefined = TemplateDefinedOf(entity.Attributes, database, entity.Id);
        Map(_entityMappings, entity.Id)[database] = mapping;
    }

    /// <summary>Records an entity's binding to a database (erratum E43).</summary>
    public void SetEntityBinding(REntity entity, string database, REntityBinding binding) => Map(_entityBindings, entity.Id)[database] = binding;

    /// <summary>Records a relation's mapping in a database.</summary>
    public void SetRelationMapping(RRelation relation, string database, RRelationMapping mapping)
    {
        mapping.TemplateDefined = TemplateDefinedOf(relation.Attributes, database, relation.Id);
        Map(_relationMappings, relation.Id)[database] = mapping;
    }

    /// <summary>Records a navigation's join path in a database.</summary>
    public void SetJoin(RNavigation navigation, string database, RJoinPath path) =>
        (navigation.PendingJoins ??= new SortedDictionary<string, RJoinPath>(StringComparer.Ordinal))[database] = path;

    /// <summary>Records a key sequence of an entity in a database.</summary>
    public void SetKeySequence(REntity entity, string database, RSequence sequence) => Map(_keySequences, entity.Id)[database] = sequence;

    private static SortedDictionary<string, T> Map<T>(Dictionary<string, SortedDictionary<string, T>> maps, string id)
    {
        if (!maps.TryGetValue(id, out var map))
        {
            map = new SortedDictionary<string, T>(StringComparer.Ordinal);
            maps[id] = map;
        }

        return map;
    }

    private void ResolveConceptual()
    {
        foreach (var mapping in Model.All<Mapping>().OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var target = mapping.Entity ?? mapping.Relation;
            if (target is null)
                continue;
            _mappings.TryAdd((mapping.Database, target), mapping);
            ListOf(_mappingKeysByTarget, target).Add(Keys.Element(mapping.Id));
        }

        DatabaseKeys = [.. Model.All<Database>().Select(d => Keys.Element(d.Id))];
        ResolveCategories();
        ResolvePackages();
        foreach (var scalar in Model.All<ScalarType>())
            ResolveScalar(scalar);
        foreach (var enumType in Model.All<EnumType>())
            ResolveEnum(enumType);
        ResolveReferenceTypes();
        var valueObjects = Model.All<ValueObject>();
        foreach (var valueObject in valueObjects)
            _valueObjects[valueObject.Id] = new RValueObject();
        foreach (var valueObject in valueObjects)
            ResolveValueObject(valueObject);
        var entities = Model.All<Entity>();
        foreach (var entity in entities)
        {
            _entitySources[entity.Id] = entity;
            _entities[entity.Id] = new REntity();
        }

        // Entities and relations are resolved in parallel (each builds only its own objects and reads the finished lookups above);
        // what they register or add to shared lists is committed afterwards in model order, so the result is the sequential one.
        ForEach(entities.Count, i => ResolveEntityShell(entities[i]));
        var entityRegistrations = new List<IResolvedObject>[entities.Count];
        ForEach(entities.Count, i => entityRegistrations[i] = ResolveEntityAttributes(entities[i]));
        foreach (var registrations in entityRegistrations)
        {
            foreach (var value in registrations)
                Register(value);
            Report();
        }

        var relations = Model.All<Relation>();
        var built = new ResolvedRelation?[relations.Count];
        ForEach(relations.Count, i => built[i] = BuildRelation(relations[i]));
        foreach (var relation in built)
        {
            if (relation is not null)
                CommitRelation(relation);
            Report();
        }

        _packageOrder = [.. _packages.Values.OrderBy(p => p.QualifiedName, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)];
        _scalarOrder = Sorted(_scalars.Values);
        _enumOrder = Sorted(_enums.Values);
        _valueObjectOrder = Sorted(_valueObjects.Values.Where(v => v.Id.Length > 0));
        _entityOrder = Sorted(_entities.Values.Where(e => e.Id.Length > 0));
        _relationOrder = Sorted(_relations.Values);
        _entityMembership = ComputeEntityMembership();

        // Navigations read the frozen keys of their relation and end entities: freeze those first, then build in parallel and
        // commit in relation order.
        foreach (var relation in _relationOrder)
        {
            DepsOf(relation).ToList();
            foreach (var end in relation.Ends)
                DepsOf(end.Entity).ToList();
        }

        var navigations = new List<RNavigation>[_relationOrder.Count];
        ForEach(_relationOrder.Count, i => navigations[i] = BuildNavigations(_relationOrder[i]));
        foreach (var list in navigations)
        {
            foreach (var navigation in list)
            {
                ListOf(_navigations, navigation.From.Entity.Id).Add(navigation);
                Register(navigation);
            }
        }
    }

    private IReadOnlyList<string> ComputeEntityMembership()
    {
        var keys = new DependencySet(Keys).Add("k:entity").Add("k:relation").Add("k:mapping").Add(Conventions);
        foreach (var mapping in Model.All<Mapping>())
        {
            if (mapping.Relation is not null)
                keys.Element(mapping.Id);
        }

        var promotesByConvention = EffectiveConventions.AnyPromotes(Settings);
        if (promotesByConvention)
        {
            foreach (var relation in _relationSources.Keys)
                keys.Element(relation);
        }

        // A promoted entity exists only while its relation resolves in some database, which needs both ends placed there: every
        // entity mapping (ignore) and database file (packages) can add or remove it.
        if (promotesByConvention || Model.All<Mapping>().Any(m => m.Relation is not null && m.Shape == RelationShape.Promoted))
        {
            foreach (var mapping in Model.All<Mapping>())
                keys.Element(mapping.Id);
            keys.AddRange(DatabaseKeys);
        }

        return keys.ToList();
    }

    private void ResolveCategories()
    {
        foreach (var tree in Model.CategoryTrees)
            ResolveCategories(tree);
    }

    private void ResolveCategories(CategoryTree tree)
    {
        var byId = tree.Categories.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var category in tree.Categories)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = category; current is not null && seen.Add(current.Id); current = current.Parent is null ? null : byId.GetValueOrDefault(current.Parent))
                names.Add(current.Name);
            names.Reverse();
            if (_categories.TryAdd(category.Id, new RCategory { Id = category.Id, Name = category.Name, Path = string.Join('/', names) }))
                _categoryTreeOf[category.Id] = tree.Id;
        }
    }

    private void ResolvePackages()
    {
        var sources = Model.All<Package>();
        foreach (var package in sources)
            _packages[package.Id] = new RPackage();
        foreach (var package in sources)
        {
            var r = _packages[package.Id];
            var deps = DepsOf(r);
            FillCommon(r, package, deps);
            r.Parent = package.Parent is null ? null : _packages.GetValueOrDefault(package.Parent);
            r.Package = r.Parent;
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = package; current is not null && seen.Add(current.Id); current = current.Parent is null ? null : Model.Get<Package>(current.Parent))
            {
                names.Add(current.Name);
                deps.Element(current.Id);
            }

            names.Reverse();
            r.QualifiedName = string.Join('.', names);
            Register(r);
            Report();
        }
    }

    private void ResolveScalar(ScalarType scalar)
    {
        var r = new RScalarType();
        FillCommon(r, scalar, DepsOf(r));
        r.Package = PackageOf(scalar.Package);
        r.Base = scalar.Base;
        r.Length = scalar.Length;
        r.Precision = scalar.Precision;
        r.Scale = scalar.Scale;
        r.Validation = Validation(scalar.Validation, null);
        r.NativeTypes = scalar.NativeTypes.ToImmutableSortedDictionary(StringComparer.Ordinal);
        _scalars[scalar.Id] = r;
        Register(r);
        Report();
    }

    private void ResolveEnum(EnumType enumType)
    {
        var r = new REnum();
        var deps = DepsOf(r);
        FillCommon(r, enumType, deps);
        r.Package = PackageOf(enumType.Package);
        r.Flags = enumType.Flags;
        var members = new List<REnumMember>();
        foreach (var member in enumType.Members)
        {
            var m = new REnumMember
            {
                Id = member.Id,
                Name = member.Name,
                DisplayName = member.DisplayName ?? member.Name,
                Description = member.Description?.Text,
                Value = member.Value,
                Code = member.Code,
                Properties = MergedProperties(member),
            };
            m.Dependencies = [Keys.Element(enumType.Id)];
            members.Add(m);
            Register(m);
        }

        r.Members = new RList<REnumMember>(members, [Keys.Element(enumType.Id)]);
        _enums[enumType.Id] = r;
        _enumSources[enumType.Id] = enumType;
        Register(r);
        Report();
    }

    private void ResolveValueObject(ValueObject valueObject)
    {
        var r = _valueObjects[valueObject.Id];
        var deps = DepsOf(r);
        FillCommon(r, valueObject, deps);
        r.Package = PackageOf(valueObject.Package);
        var raw = valueObject.Attributes.Select(a => (Attribute: a, Stereotype: (Stereotype?)null)).ToList();
        foreach (var stereotype in StereotypesOf(valueObject))
            raw.AddRange(stereotype.Attributes.Select(a => (Attribute: a, Stereotype: (Stereotype?)stereotype)));
        var attributes = StableByOrder(raw.DistinctBy(x => x.Attribute.Id, StringComparer.Ordinal), x => x.Attribute.Order)
            .Select(x => MakeAttribute(x.Attribute, r, r.Package, null, false, x.Stereotype, x.Stereotype?.Id ?? valueObject.Id))
            .ToList();
        SetOrder(attributes);
        r.Attributes = new RList<RAttribute>(attributes, deps.ToList());
        foreach (var attribute in attributes)
            Register(attribute);
        Register(r);
        Report();
    }

    private void ResolveEntityShell(Entity entity)
    {
        var r = _entities[entity.Id];
        var deps = DepsOf(r);
        FillCommon(r, entity, deps);
        r.Package = PackageOf(entity.Package);
        r.IsAbstract = entity.Abstract;
        r.Base = entity.Base is null ? null : _entities.GetValueOrDefault(entity.Base);
        deps.Referrers(entity.Id).Add(Conventions).Add("k:database");
        foreach (var ancestor in Chain(entity))
            deps.Element(ancestor.Id).Referrers(ancestor.Id);
        deps.AddRange(MappingKeysOf(entity.Id)).AddRange(DatabaseKeys);
    }

    /// <summary>The entity followed by its ancestors, nearest first; stops at a cycle (MQ3002) or a dangling base.</summary>
    public List<Entity> Chain(Entity entity)
    {
        var chain = new List<Entity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null && seen.Add(current.Id); current = current.Base is null ? null : _entitySources.GetValueOrDefault(current.Base))
            chain.Add(current);
        return chain;
    }

    /// <summary>Resolves an entity's attributes and keys; returns what to register, in order (the caller commits it).</summary>
    private List<IResolvedObject> ResolveEntityAttributes(Entity entity)
    {
        var registrations = new List<IResolvedObject>();
        var r = _entities[entity.Id];
        var deps = DepsOf(r);
        var chain = Chain(entity);
        chain.Reverse(); // root first

        // Section 7.2: base attributes (root first), then own attributes, then stereotype virtual attributes, per level; one
        // stable sort by order at the end. A stereotype applied at two levels contributes its attributes once.
        var raw = new List<(ModelAttribute Attribute, Entity Declaring, Stereotype? Stereotype)>();
        foreach (var level in chain)
        {
            raw.AddRange(level.Attributes.Select(a => (a, level, (Stereotype?)null)));
            foreach (var stereotype in StereotypesOf(level))
            {
                deps.Element(stereotype.Id);
                raw.AddRange(stereotype.Attributes.Select(a => (a, level, (Stereotype?)stereotype)));
            }
        }

        var flattened = StableByOrder(raw.DistinctBy(x => x.Attribute.Id, StringComparer.Ordinal), x => x.Attribute.Order).ToList();
        var attributes = new List<RAttribute>();
        foreach (var (attribute, declaring, stereotype) in flattened)
        {
            var inherited = !string.Equals(declaring.Id, entity.Id, StringComparison.Ordinal);
            var a = MakeAttribute(attribute, r, r.Package, _entities[declaring.Id], inherited, stereotype, stereotype?.Id ?? declaring.Id);
            DepsOf(a).Element(entity.Id);
            attributes.Add(a);
            if (!inherited)
                registrations.Add(a);
        }

        SetOrder(attributes);
        r.Attributes = new RList<RAttribute>(attributes, deps.ToList());
        var own = entity.Attributes.Select(a => attributes.First(x => string.Equals(x.Id, a.Id, StringComparison.Ordinal))).ToList();
        r.OwnAttributes = new RList<RAttribute>(own, [Keys.Element(entity.Id)]);

        // The key and alternate keys come from the hierarchy root (the key) and every level (alternate keys).
        var root = chain[0];
        if (root.Key is { } key)
        {
            var keyAttributes = key.Attributes.Select(id => attributes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal)))
                .OfType<RAttribute>().ToList();
            r.Key = new RKey
            {
                Attributes = new RList<RAttribute>(keyAttributes, deps.ToList()),
                Strategy = ResolutionValues.Kebab(key.Strategy),
            };
        }

        r.AlternateKeys = [.. chain.SelectMany(level => level.AlternateKeys).Select(k => new RAlternateKey
        {
            Id = k.Id,
            Name = k.Name,
            Attributes = new RList<RAttribute>(
                k.Attributes.Select(id => attributes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))).OfType<RAttribute>(),
                deps.ToList()),
        })];
        registrations.Add(r);
        return registrations;
    }

    /// <summary>A relation built by <see cref="BuildRelation"/>, waiting for <see cref="CommitRelation"/>.</summary>
    private sealed record ResolvedRelation(Relation Source, RRelation Relation, List<REnd> Ends, List<RAttribute> Attributes);

    /// <summary>Builds a relation's resolved objects; <see langword="null"/> for a dangling end (MQ2001 leaves the relation out).</summary>
    private ResolvedRelation? BuildRelation(Relation relation)
    {
        Ct.ThrowIfCancellationRequested();
        var dangling = false;
        foreach (var end in relation.Ends)
            dangling |= !_entities.ContainsKey(end.Entity);
        if (dangling)
            return null;

        var r = new RRelation();
        var deps = DepsOf(r);
        FillCommon(r, relation, deps);
        r.Package = PackageOf(relation.Package);
        r.InverseName = relation.InverseName;
        r.RelationKind = ResolutionValues.Kebab(relation.RelationKind);
        r.AllowDuplicates = relation.AllowDuplicates;
        deps.Referrers(relation.Id).Add(Conventions).Add("k:database").AddRange(MappingKeysOf(relation.Id)).AddRange(DatabaseKeys);

        var ends = new List<REnd>();
        IReadOnlyList<string> endDeps = [Keys.Element(relation.Id)];
        foreach (var end in relation.Ends)
        {
            var e = new REnd
            {
                Id = end.Id,
                Entity = _entities[end.Entity],
                Role = end.Role,
                Navigation = end.Navigation.Length == 0 ? null : end.Navigation,
                Min = end.Min,
                Max = end.Max == MaxCardinality.One ? "1" : "*",
                IsMany = end.Max == MaxCardinality.Many,
                OnDelete = ResolutionValues.Kebab(end.OnDelete),
                Ordered = end.Ordered,
            };
            e.Dependencies = endDeps;
            deps.Element(end.Entity).Referrers(end.Entity);
            ends.Add(e);
        }

        if (ends.Count == 2)
        {
            ends[0].Opposite = ends[1];
            ends[1].Opposite = ends[0];
        }

        r.Ends = new RList<REnd>(ends, endDeps);
        r.Cardinality = CardinalityOf(relation);
        var raw = relation.Attributes.Select(a => (Attribute: a, Stereotype: (Stereotype?)null)).ToList();
        foreach (var stereotype in StereotypesOf(relation))
            raw.AddRange(stereotype.Attributes.Select(a => (Attribute: a, Stereotype: (Stereotype?)stereotype)));
        var attributes = raw.Count == 0 ? [] : StableByOrder(raw.DistinctBy(x => x.Attribute.Id, StringComparer.Ordinal), x => x.Attribute.Order)
            .Select(x => MakeAttribute(x.Attribute, r, r.Package, null, false, x.Stereotype, x.Stereotype?.Id ?? relation.Id))
            .ToList();
        SetOrder(attributes);
        r.Attributes = new RList<RAttribute>(attributes, deps.ToList());
        return new ResolvedRelation(relation, r, ends, attributes);
    }

    /// <summary>Registers a built relation and indexes it, in model order.</summary>
    private void CommitRelation(ResolvedRelation built)
    {
        var (relation, r, ends, attributes) = built;
        _relationSources[relation.Id] = relation;
        foreach (var e in ends)
            Register(e);
        foreach (var attribute in attributes)
            Register(attribute);
        _relations[relation.Id] = r;
        for (var i = 0; i < relation.Ends.Count; i++)
        {
            var entityId = relation.Ends[i].Entity;
            var repeated = false;
            for (var j = 0; j < i && !repeated; j++)
                repeated = string.Equals(relation.Ends[j].Entity, entityId, StringComparison.Ordinal);
            if (!repeated)
                ListOf(_relationsByEntity, entityId).Add(r);
        }
        Register(r);
    }

    /// <summary><c>one-to-one</c>, <c>one-to-many</c>, <c>many-to-many</c> or <c>n-ary</c>.</summary>
    public static string CardinalityOf(Relation relation)
    {
        if (relation.Ends.Count != 2 || relation.RelationKind == RelationKind.NAry)
            return "n-ary";
        var ones = relation.Ends.Count(e => e.Max == MaxCardinality.One);
        return ones switch { 2 => "one-to-one", 1 => "one-to-many", _ => "many-to-many" };
    }

    /// <summary>Builds the navigations a relation generates (the caller lists and registers them, in relation order).</summary>
    private List<RNavigation> BuildNavigations(RRelation relation)
    {
        var built = new List<RNavigation>(2);
        var ends = relation.Ends;
        foreach (var to in ends)
        {
            if (to.Navigation is null)
                continue;
            foreach (var from in ends)
            {
                if (ReferenceEquals(from, to) || (ends.Count == 2 && !ReferenceEquals(from, to.Opposite)))
                    continue;
                var navigation = new RNavigation
                {
                    Id = relation.Id + "." + from.Id + "." + to.Id,
                    Name = to.Navigation,
                    Relation = relation,
                    From = from,
                    To = to,
                    Target = to.Entity,
                    IsCollection = to.IsMany,
                };
                DepsOf(navigation).AddRange(DepsOf(relation).ToList()).AddRange(DepsOf(from.Entity).ToList()).AddRange(DepsOf(to.Entity).ToList());
                built.Add(navigation);
            }
        }

        return built;
    }

    private void FinishConceptual()
    {
        var derivedByBase = new Dictionary<REntity, List<REntity>>(ReferenceEqualityComparer.Instance);
        foreach (var e in _entityOrder)
        {
            if (e.Base is { } baseEntity)
            {
                if (!derivedByBase.TryGetValue(baseEntity, out var derived))
                {
                    derived = [];
                    derivedByBase[baseEntity] = derived;
                }

                derived.Add(e);
            }
        }

        // Every entity is finished on its own (in parallel: each writes only its own members and dependency set). The join paths of
        // the navigations they list are published once afterwards (a navigation can be inherited by several entities; each would
        // publish the same paths).
        var targets = _entities.Values.Concat(_promoted.Values).Where(r => r.Id.Length > 0).ToList();
        var listed = new List<RNavigation>[targets.Count];
        ForEach(targets.Count, i => listed[i] = FinishEntity(targets[i], derivedByBase));
        foreach (var navigations in listed)
        {
            foreach (var navigation in navigations)
            {
                if (navigation.PendingJoins is { } joins)
                {
                    navigation.Joins = SortedMap(joins);
                    navigation.PendingJoins = null;
                }
            }
        }

        foreach (var navigations in _navigations.Values)
        {
            foreach (var navigation in navigations)
                navigation.PendingJoins = null; // not listed by any entity: nothing publishes them
        }

        var relations = _relations.Values.Concat(_promotedRelations).ToList();
        ForEach(relations.Count, i =>
        {
            var r = relations[i];
            r.Mappings = _relationMappings.TryGetValue(r.Id, out var mappings) ? SortedMap(mappings) : ImmutableSortedDictionary<string, RRelationMapping>.Empty;
        });

        // Package lists, gathered in one pass each (the order within a package is the order of the whole sorted list).
        var entitiesByPackage = ByPackage(SortedEntities());
        var relationsByPackage = ByPackage(SortedRelations());
        var valueObjectsByPackage = ByPackage(_valueObjectOrder);
        var enumsByPackage = ByPackage(_enumOrder);
        var childrenByParent = new Dictionary<RPackage, List<RPackage>>(ReferenceEqualityComparer.Instance);
        foreach (var p in _packageOrder)
        {
            if (p.Parent is { } parent)
                GroupOf(childrenByParent, parent).Add(p);
        }

        foreach (var package in _packageOrder)
        {
            var membership = new DependencySet(Keys).Referrers(package.Id).AddRange(_entityMembership).ToList();
            package.Children = new RList<RPackage>(childrenByParent.GetValueOrDefault(package) ?? [], [Keys.Referrers(package.Id)]);
            package.Entities = new RList<REntity>(entitiesByPackage.GetValueOrDefault(package) ?? [], membership);
            package.ValueObjects = new RList<RValueObject>(valueObjectsByPackage.GetValueOrDefault(package) ?? [], [Keys.Referrers(package.Id)]);
            package.Enums = new RList<REnum>(enumsByPackage.GetValueOrDefault(package) ?? [], [Keys.Referrers(package.Id)]);
            package.Relations = new RList<RRelation>(relationsByPackage.GetValueOrDefault(package) ?? [], membership);
        }
    }

    /// <summary>Groups elements by their package (by reference), keeping their order.</summary>
    private static Dictionary<RPackage, List<T>> ByPackage<T>(IEnumerable<T> elements) where T : RElement
    {
        var groups = new Dictionary<RPackage, List<T>>(ReferenceEqualityComparer.Instance);
        foreach (var element in elements)
        {
            if (element.Package is { } package)
                GroupOf(groups, package).Add(element);
        }

        return groups;
    }

    private static List<T> GroupOf<T>(Dictionary<RPackage, List<T>> groups, RPackage package)
    {
        if (!groups.TryGetValue(package, out var list))
        {
            list = [];
            groups[package] = list;
        }

        return list;
    }

    /// <summary>Finishes one entity's navigations, relations, derived entities, mappings and key sequences; returns its navigations.</summary>
    private List<RNavigation> FinishEntity(REntity r, Dictionary<REntity, List<REntity>> derivedByBase)
    {
        var deps = DepsOf(r);
        var navigations = InheritedNavigations(r.Id).OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        r.Navigations = new RList<RNavigation>(navigations, deps.ToList());
        var relations = _relationsByEntity.TryGetValue(r.Id, out var list) ? Sorted(list) : [];

        // Promotion adds relations (<relationId>.<endId>) to each end entity, so the list also depends on what decides promotion.
        r.Relations = new RList<RRelation>(relations, relations.Count == 0 ? deps.ToList() : new DependencySet(Keys).AddRange(deps.ToList()).AddRange(_entityMembership).ToList());
        r.Derived = new RList<REntity>(derivedByBase.TryGetValue(r, out var derivedList) ? derivedList : [], [Keys.Referrers(r.Id)]);
        r.Mappings = _entityMappings.TryGetValue(r.Id, out var mappings) ? SortedMap(mappings) : ImmutableSortedDictionary<string, REntityMapping>.Empty;
        r.Bindings = _entityBindings.TryGetValue(r.Id, out var bindings) ? SortedMap(bindings) : ImmutableSortedDictionary<string, REntityBinding>.Empty;
        if (r.Key is { } key && _keySequences.TryGetValue(r.Id, out var sequences))
            key.Sequences = SortedMap(sequences);
        return navigations;
    }

    private List<REntity> SortedEntities() => Sorted(_entityOrder.Concat(_promoted.Values));

    private List<RRelation> SortedRelations() => Sorted(_relationOrder.Concat(_promotedRelations));

    private static List<T> ListOf<T>(Dictionary<string, List<T>> lists, string id)
    {
        if (!lists.TryGetValue(id, out var list))
        {
            list = [];
            lists[id] = list;
        }

        return list;
    }

    internal RPackage? PackageOf(string? id) => id is null ? null : _packages.GetValueOrDefault(id);

    private IEnumerable<Stereotype> StereotypesOf(ElementBase element)
    {
        foreach (var key in element.Stereotypes)
        {
            if (Model.GetStereotype(key) is { } stereotype)
                yield return stereotype;
        }
    }

    private static IEnumerable<T> StableByOrder<T>(IEnumerable<T> items, Func<T, int?> order) => items.OrderBy(x => order(x) ?? 0);

    private static void SetOrder(List<RAttribute> attributes)
    {
        for (var i = 0; i < attributes.Count; i++)
            attributes[i].Order = i;
    }

    /// <summary>Fills the members common to every conceptual resolved object.</summary>
    private void FillCommon(RElement r, Element element, DependencySet deps)
    {
        FillCommon(r, element, Model.GetDocument(element.Id)?.SidecarText, deps);
        deps.Element(element.Id);
    }

    private void FillCommon(RElement r, ElementBase element, string? sidecar, DependencySet deps)
    {
        r.Id = element.Id;
        r.Name = element.Name;
        r.DisplayName = element.DisplayName ?? element.Name;
        if (element.PluralName is { } plural)
        {
            r.PluralName = plural;
        }
        else
        {
            r.PluralName = Inflector.Pluralize(element.Name);
            deps.Add(InflectionKey);
        }

        FillAnnotations(r, element, sidecar, deps);
    }

    /// <summary>
    /// Fills the annotations of a physical object (database, table, view, sequence) from its own file: display and plural names as
    /// written (empty when unset, with no fallback), then what <see cref="FillAnnotations(RAnnotated, ElementBase, string?, DependencySet)"/>
    /// fills. Without a file (a synthesized table with no overlay, a key sequence) the annotations stay empty. The caller adds the
    /// file's own <c>e:</c> key.
    /// </summary>
    /// <param name="r">The resolved object.</param>
    /// <param name="file">The object's file, or <see langword="null"/>.</param>
    /// <param name="deps">The object's dependency keys: the category tree and the applied stereotypes are added.</param>
    internal void FillPhysicalAnnotations(RAnnotated r, Element? file, DependencySet deps)
    {
        if (file is null)
            return;
        FillPhysicalAnnotations(r, file, Model.GetDocument(file.Id)?.SidecarText, deps);
    }

    /// <summary>
    /// Fills the annotations of a physical part (a column, a database schema) from its own entry in a file, as
    /// <see cref="FillPhysicalAnnotations(RAnnotated, Element?, DependencySet)"/> does for a file. A part has no sidecar description.
    /// </summary>
    /// <param name="r">The resolved object.</param>
    /// <param name="part">The part's entry, or <see langword="null"/> (a synthesized column without an overlay entry).</param>
    /// <param name="sidecar">The sidecar text of the entry's description, when it is a file's own.</param>
    /// <param name="deps">The dependency keys of the object (for a column, its table's).</param>
    internal void FillPhysicalAnnotations(RAnnotated r, ElementBase? part, string? sidecar, DependencySet deps)
    {
        if (part is null)
            return;
        r.DisplayName = part.DisplayName ?? "";
        r.PluralName = part.PluralName ?? "";
        FillAnnotations(r, part, sidecar, deps);
    }

    /// <summary>Fills description, tags, category, stereotypes, merged properties and generation hints from a file.</summary>
    private void FillAnnotations(RAnnotated r, ElementBase element, string? sidecar, DependencySet deps)
    {
        r.Description = element.Description is { } description ? description.Text ?? (description.File is null ? null : sidecar) : null;
        r.Tags = [.. element.Tags];
        if (element.Category is { } category && _categories.TryGetValue(category, out var rc))
        {
            r.Category = rc;
            if (_categoryTreeOf.TryGetValue(category, out var treeId))
                deps.Element(treeId);
        }

        var stereotypes = new List<RStereotype>();
        foreach (var stereotype in StereotypesOf(element))
        {
            stereotypes.Add(new RStereotype
            {
                Key = stereotype.Key,
                Name = stereotype.DisplayName ?? (stereotype.Name.Length > 0 ? stereotype.Name : stereotype.Key),
                Icon = stereotype.Icon,
                Color = stereotype.Color,
            });
            deps.Element(stereotype.Id);
        }

        r.Stereotypes = stereotypes;
        r.Properties = MergedProperties(element);
        r.Generation = element.Generation.Count == 0 ? ImmutableSortedDictionary<string, GenerationHints>.Empty : SortedMap(element.Generation);
    }

    /// <summary>Stereotype default properties (stereotype order, later wins) under the element's own properties.</summary>
    private IReadOnlyDictionary<string, object?> MergedProperties(ElementBase element)
    {
        if (element.Properties.Count == 0 && element.Stereotypes.Count == 0)
            return ImmutableSortedDictionary<string, object?>.Empty;
        var merged = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        foreach (var stereotype in StereotypesOf(element))
        {
            foreach (var pair in stereotype.DefaultProperties)
                merged[pair.Key] = pair.Value;
        }

        foreach (var pair in element.Properties)
            merged[pair.Key] = pair.Value;
        return ResolutionValues.PlainMap(merged);
    }

    /// <summary>Creates the resolved view of an attribute on an owner.</summary>
    private RAttribute MakeAttribute(ModelAttribute attribute, IResolvedObject owner, RPackage? package, REntity? declaring, bool inherited,
        Stereotype? stereotype, string declaringFileId)
    {
        var a = new RAttribute();
        var deps = DepsOf(a);
        FillCommon(a, attribute, null, deps);
        deps.Element(declaringFileId);
        a.Package = package;
        a.Owner = owner;
        a.DeclaringEntity = declaring;
        a.Type = TypeOf(attribute.Type, deps, out var scalar);
        a.Required = attribute.Required;
        a.Default = ResolutionValues.Plain(attribute.Default);
        a.DefaultExpression = attribute.DefaultExpression;
        a.Length = attribute.Length ?? scalar?.Length ?? a.Type.ReferenceType?.Code.Length;
        a.Precision = attribute.Precision ?? scalar?.Precision;
        a.Scale = attribute.Scale ?? scalar?.Scale;
        a.Collection = attribute.Collection;
        a.Unique = attribute.Unique;
        a.Indexed = attribute.Indexed;
        a.ReadOnly = attribute.ReadOnly;
        a.Immutable = attribute.Immutable;
        a.Derived = attribute.Derived is { } derived ? new RDerived { Expression = derived.Expression, Stored = derived.Stored } : null;
        a.Sensitive = attribute.Sensitive is { } sensitive ? ResolutionValues.Kebab(sensitive) : null;
        a.Validation = Validation(attribute.Validation, scalar is null ? null : Model.Get<ScalarType>(scalar.Id)?.Validation);
        a.IsInherited = inherited;
        a.IsVirtual = stereotype is not null;
        a.FromStereotype = stereotype is null ? null : new RStereotype
        {
            Key = stereotype.Key,
            Name = stereotype.DisplayName ?? (stereotype.Name.Length > 0 ? stereotype.Name : stereotype.Key),
            Icon = stereotype.Icon,
            Color = stereotype.Color,
        };
        return a;
    }

    private RType TypeOf(TypeRef type, DependencySet deps, out RScalarType? scalar)
    {
        scalar = null;
        if (type.Ref is { } id)
        {
            deps.Element(id);
            if (_enums.TryGetValue(id, out var e))
                return new RType { Kind = "enum", Name = e.Name, Enum = e };
            if (_valueObjects.TryGetValue(id, out var v))
                return new RType { Kind = "value-object", Name = Model.Get<ValueObject>(id)?.Name ?? v.Name, ValueObject = v };
            if (_referenceTypes.TryGetValue(id, out var rt))
                return new RType { Kind = "reference", Name = rt.Name, Builtin = rt.Code.Type, ReferenceType = rt };
            if (_scalars.TryGetValue(id, out var s))
            {
                scalar = s;
                return new RType { Kind = "scalar", Name = s.Name, Builtin = s.Base, Scalar = s };
            }

            return new RType { Kind = "builtin", Name = "" }; // dangling (MQ2001)
        }

        var keyword = type.Builtin ?? "";
        return new RType { Kind = "builtin", Name = keyword, Builtin = BuiltinTypes.IsBuiltin(keyword) ? keyword : null };
    }

    private static RValidation? Validation(AttributeValidation? own, AttributeValidation? scalar)
    {
        if (own is null && scalar is null)
            return null;
        return new RValidation
        {
            Min = ResolutionValues.Plain(own?.Min ?? scalar?.Min),
            Max = ResolutionValues.Plain(own?.Max ?? scalar?.Max),
            Pattern = own?.Pattern ?? scalar?.Pattern,
            AllowedValues = [.. (own is { AllowedValues.Count: > 0 } ? own.AllowedValues : scalar?.AllowedValues ?? []).Select(ResolutionValues.Plain)],
            Rules = [.. (scalar?.Rules ?? []).Concat(own?.Rules ?? []).Distinct(StringComparer.Ordinal)],
        };
    }

    /// <summary>Creates the promoted entity of a relation (once, shared by every database that promotes it).</summary>
    public REntity Promote(RRelation relation, string? promotedName)
    {
        if (_promoted.TryGetValue(relation.Id, out var existing))
            return existing;
        var name = promotedName ?? Casing.Pascal(string.Join('_', relation.Ends.Select(e => e.Entity.Name)));
        var entity = new REntity
        {
            Id = relation.Id,
            Name = name,
            DisplayName = name,
            PluralName = Inflector.Pluralize(name),
            Description = relation.Description,
            Tags = relation.Tags,
            Category = relation.Category,
            Stereotypes = relation.Stereotypes,
            Properties = relation.Properties,
            Generation = relation.Generation,
            Package = relation.Package,
            IsPromoted = true,
            PromotedFrom = relation,
        };
        var deps = DepsOf(entity).AddRange(DepsOf(relation).ToList()).Add(InflectionKey);
        var source = _relationSources[relation.Id];

        // The key (S6 "an entity has identity and a key"): a surrogate id when duplicates are allowed or an end is ordered (the
        // junction's `id` column), else one attribute per end key attribute (the foreign key columns of the composite key).
        var keyAttributes = new List<(RAttribute Attribute, string ColumnKey)>();
        var surrogate = source.AllowDuplicates || source.Ends.Any(e => e.Ordered);
        if (surrogate)
        {
            keyAttributes.Add((KeyAttribute(entity, relation.Id + "/id", "id", new RType { Kind = "builtin", Name = "int64", Builtin = "int64" }, null, null, null, deps), "id"));
        }
        else
        {
            foreach (var end in relation.Ends)
            {
                foreach (var ka in end.Entity.Key?.Attributes ?? RList<RAttribute>.Empty)
                {
                    var keyName = Casing.Camel(end.Role + "_" + ka.Name);
                    var attribute = KeyAttribute(entity, relation.Id + "/" + end.Id + "/" + ka.Id, keyName, ka.Type, ka.Length, ka.Precision, ka.Scale, deps);
                    DepsOf(attribute).AddRange(DepsOf(end.Entity).ToList());
                    keyAttributes.Add((attribute, end.Id + "." + ka.Id));
                }
            }
        }

        _promotedKeys[entity.Id] = keyAttributes;
        var relationAttributes = relation.Attributes.Select(a =>
        {
            var copy = MakeAttribute(source.Attributes.FirstOrDefault(x => string.Equals(x.Id, a.Id, StringComparison.Ordinal))
                    ?? StereotypesOf(source).SelectMany(s => s.Attributes).First(x => string.Equals(x.Id, a.Id, StringComparison.Ordinal)),
                entity, entity.Package, entity, false, null, source.Id);
            copy.IsVirtual = a.IsVirtual;
            copy.FromStereotype = a.FromStereotype;
            return copy;
        }).ToList();
        var attributes = keyAttributes.Select(k => k.Attribute).Concat(relationAttributes).ToList();
        SetOrder(attributes);
        entity.Attributes = new RList<RAttribute>(attributes, deps.ToList());
        entity.OwnAttributes = new RList<RAttribute>(attributes, deps.ToList());
        entity.Key = new RKey
        {
            Attributes = new RList<RAttribute>(keyAttributes.Select(k => k.Attribute), deps.ToList()),
            Strategy = surrogate ? "database-identity" : "application",
        };
        foreach (var (attribute, _) in keyAttributes)
            Register(attribute);

        // Two many-to-one relations whose ids are <relationId>.<endId> (section 7.4), navigable from the promoted entity.
        foreach (var end in relation.Ends)
        {
            var id = relation.Id + "." + end.Id;
            var r = new RRelation
            {
                Id = id,
                Name = end.Role,
                DisplayName = end.Role,
                PluralName = Inflector.Pluralize(end.Role),
                Package = relation.Package,
                RelationKind = "association",
                Cardinality = "one-to-many",
            };
            DepsOf(r).AddRange(deps.ToList());
            var principal = new REnd
            {
                Id = id + ".principal", Entity = end.Entity, Role = end.Role, Navigation = end.Role, Min = 1, Max = "1", IsMany = false,
                OnDelete = string.Equals(end.OnDelete, "restrict", StringComparison.Ordinal) ? "restrict" : "cascade",
            };
            var dependent = new REnd
            {
                Id = id + ".dependent", Entity = entity, Role = Casing.Camel(name), Min = 0, Max = "*", IsMany = true, OnDelete = "none",
            };
            principal.Opposite = dependent;
            dependent.Opposite = principal;
            DepsOf(principal).AddRange(deps.ToList());
            DepsOf(dependent).AddRange(deps.ToList());
            r.Ends = new RList<REnd>([principal, dependent], deps.ToList());
            var navigation = new RNavigation
            {
                Id = id + "." + dependent.Id + "." + principal.Id,
                Name = end.Role,
                Relation = r,
                From = dependent,
                To = principal,
                Target = end.Entity,
                IsCollection = false,
            };
            DepsOf(navigation).AddRange(deps.ToList());
            ListOf(_navigations, entity.Id).Add(navigation);
            _inheritedNavigations.Clear();
            ListOf(_relationsByEntity, entity.Id).Add(r);
            if (!ReferenceEquals(end.Entity, entity))
                ListOf(_relationsByEntity, end.Entity.Id).Add(r);
            _promotedRelations.Add(r);
            Register(r);
            Register(principal);
            Register(dependent);
            Register(navigation);
        }

        _promoted[relation.Id] = entity;
        Register(entity);
        return entity;
    }

    /// <summary>A key attribute synthesized for a promoted entity.</summary>
    private RAttribute KeyAttribute(REntity entity, string id, string name, RType type, int? length, int? precision, int? scale, DependencySet deps)
    {
        var a = new RAttribute
        {
            Id = id,
            Name = name,
            DisplayName = name,
            PluralName = Inflector.Pluralize(name),
            Package = entity.Package,
            Owner = entity,
            DeclaringEntity = entity,
            Type = type,
            Required = true,
            Immutable = true,
            Length = length,
            Precision = precision,
            Scale = scale,
        };
        DepsOf(a).AddRange(deps.ToList());
        return a;
    }

    /// <summary>
    /// The key attributes of a promoted entity with the key of the column each one maps to in the promoted table (<c>id</c>, or
    /// <c>&lt;endId&gt;.&lt;keyAttrId&gt;</c>); empty for any other entity.
    /// </summary>
    public IReadOnlyList<(RAttribute Attribute, string ColumnKey)> PromotedKeys(string entityId) =>
        _promotedKeys.TryGetValue(entityId, out var keys) ? keys : [];

    /// <summary>The promotion relation of an end of a promoted relation.</summary>
    public RRelation PromotionRelation(RRelation relation, REnd end) =>
        _promotedRelations.First(r => string.Equals(r.Id, relation.Id + "." + end.Id, StringComparison.Ordinal));

    /// <summary>The navigation of a promoted entity towards an end entity.</summary>
    public RNavigation PromotionNavigation(RRelation promotionRelation) =>
        _navigations[promotionRelation.Ends[1].Entity.Id].First(n => ReferenceEquals(n.Relation, promotionRelation));
}
