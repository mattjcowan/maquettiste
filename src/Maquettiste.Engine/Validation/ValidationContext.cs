using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

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
    private readonly Lazy<ReferenceDataIndex> _referenceData;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<Seed>>> _seeds;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<(ReferenceType Type, ReferenceTypeBinding Binding)>>> _boundTypes;
    private readonly Lazy<FrozenDictionary<string, Seed>> _rowSeeds;
    private readonly Lazy<FrozenSet<string>> _cyclicRows;
    private readonly ConcurrentDictionary<Dialect, FrozenSet<string>> _knownNativeTypes;
    private readonly Lazy<FrozenSet<string>> _modelTypeNames;
    private readonly Lazy<FrozenDictionary<string, ImmutableArray<NativeTypeUse>>> _userDefinedTypes;
    private readonly Lazy<DatabaseObjectIndex> _databaseObjects;
    private readonly Lazy<PackageLayers> _layers;
    private readonly Lazy<SeedOrder> _seedOrder;

    /// <summary>Creates a context.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="documents">The active documents (duplicates and ignored singletons removed), ordinal by path.</param>
    /// <param name="walker">The reference walker.</param>
    /// <param name="ruleNames">The registered JavaScript rule names (without <c>x/</c>), or <see langword="null"/> when unknown.</param>
    /// <param name="wholeModel">Whether the validation judges the whole model (no element scope), which decides the scoped code checks of
    /// seeds with a rows script (next-phase-2-design.md section 3.4, decision 17).</param>
    /// <param name="scripts">The sandbox factory that runs rows scripts; a factory of its own when absent.</param>
    /// <param name="seedRows">The engine's rows cache; a cache of its own when absent.</param>
    public ValidationContext(ModelSnapshot model, IReadOnlyList<ElementDocument> documents, ReferenceWalker walker, FrozenSet<string>? ruleNames,
        bool wholeModel = true, Scripting.IScriptSandboxFactory? scripts = null, SeedRowsCache? seedRows = null)
    {
        Model = model;
        Documents = documents;
        Walker = walker;
        RuleNames = ruleNames;
        WholeModel = wholeModel;
        _scripts = scripts;
        _seedRows = seedRows;
        _scripted = new(StringComparer.Ordinal);
        _names = new(BuildNames);
        _navigations = new(BuildNavigations);
        _compositions = new(BuildCompositions);
        _mappings = new(BuildMappings);
        _overlays = new(BuildOverlays);
        _physicalNames = new(BuildPhysicalNames);
        _derived = new(BuildDerived);
        _referenceData = new(() => ReferenceDataIndex.Build(Documents.Select(d => d.Element)));
        _seeds = new(BuildSeeds);
        _boundTypes = new(BuildBoundTypes);
        _rowSeeds = new(BuildRowSeeds);
        _cyclicRows = new(() => ReferenceDataRules.CyclicRows(this));
        _knownNativeTypes = new();
        _modelTypeNames = new(BuildModelTypeNames);
        _userDefinedTypes = new(BuildUserDefinedTypes);
        _databaseObjects = new(() => DatabaseObjectIndex.Build(this));
        _layers = new(() => PackageLayers.Build(this));
        _seedOrder = new(() => new SeedOrder(model));
    }

    /// <summary>A context over the same model and documents, sharing every index already built, with other rule names.</summary>
    private ValidationContext(ValidationContext other, FrozenSet<string>? ruleNames)
    {
        Model = other.Model;
        Documents = other.Documents;
        Walker = other.Walker;
        RuleNames = ruleNames;
        WholeModel = other.WholeModel;
        Scope = other.Scope;
        DeferScriptedRows = other.DeferScriptedRows;
        _scripts = other._scripts;
        _seedRows = other._seedRows;
        _scripted = other._scripted;
        _flat = other._flat;
        _valueObjectReach = other._valueObjectReach;
        _names = other._names;
        _navigations = other._navigations;
        _compositions = other._compositions;
        _mappings = other._mappings;
        _overlays = other._overlays;
        _physicalNames = other._physicalNames;
        _derived = other._derived;
        _referenceData = other._referenceData;
        _seeds = other._seeds;
        _boundTypes = other._boundTypes;
        _rowSeeds = other._rowSeeds;
        _cyclicRows = other._cyclicRows;
        _knownNativeTypes = other._knownNativeTypes;
        _modelTypeNames = other._modelTypeNames;
        _userDefinedTypes = other._userDefinedTypes;
        _databaseObjects = other._databaseObjects;
        _layers = other._layers;
        _seedOrder = other._seedOrder;
    }

    private readonly Scripting.IScriptSandboxFactory? _scripts;
    private readonly SeedRowsCache? _seedRows;
    private readonly ConcurrentDictionary<string, Lazy<ScriptedRows>> _scripted;

    /// <summary>Whether the validation judges the whole model (no element scope).</summary>
    public bool WholeModel { get; }

    /// <summary>
    /// The element ids a scoped validation (a save) judges, or <see langword="null"/> for a whole-model one: a seed's rows script runs
    /// only when the seed is among them (next-phase-2-design.md section 3.4, decision 15).
    /// </summary>
    internal IReadOnlySet<string>? Scope { get; set; }

    /// <summary>
    /// Whether a whole-model validation leaves every seed with a rows script unjudged, as a save leaves one outside its scope: the
    /// validation previews wait on (<see cref="Diagnostics.ValidationScope.Previews"/>; next-phase-2-design.md section 3.4, decision 31).
    /// </summary>
    internal bool DeferScriptedRows { get; set; }

    /// <summary>Whether every seed with a rows script is judged: a whole-model validation that does not defer them.</summary>
    private bool JudgesEveryScript => WholeModel && !DeferScriptedRows;

    /// <summary>
    /// Whether this validation evaluates a seed's rows: a seed without a rows script always; one with a script in a whole-model
    /// validation that does not defer scripted seeds (<see cref="DeferScriptedRows"/>), or in a save whose scope holds it (the seed or
    /// its target changed).
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <returns><see langword="true"/> when its rows are judged.</returns>
    public bool Judges(Seed seed) => seed.RowsFrom?.Script is null || JudgesEveryScript || Scope?.Contains(seed.Id) == true;

    /// <summary>
    /// A seed's rows: its stored rows, or for a seed with a rows script (<c>rowsFrom.script</c>) its scripted rows, evaluated once per
    /// validation through the engine's rows cache (next-phase-2-design.md section 3.4); none for a scripted seed outside a save's scope
    /// (<see cref="Judges"/>), whose script the save does not run. The seed rules read rows through it.
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <returns>The rows.</returns>
    public IReadOnlyList<SeedRow> RowsOf(Seed seed) => seed.RowsFrom?.Script is null ? seed.Rows : Judges(seed) ? Scripted(seed).Rows : [];

    /// <summary>
    /// Whether a code is missing from a reference type's rows, so a default, an <c>allowedValues</c> entry or a cell that names it is
    /// reported (decision 17). A stored row's code is in the index. When the type has a seed with a rows script, a save (and the
    /// validation previews wait on, <see cref="DeferScriptedRows"/>) does not judge a code no stored row holds (scripted rows are not in
    /// the index), and whole-model validation judges it against the stored and scripted rows.
    /// </summary>
    /// <param name="typeId">The reference type id.</param>
    /// <param name="code">The code.</param>
    /// <returns><see langword="true"/> when the finding is due.</returns>
    public bool CodeMissing(string typeId, JsonElement code)
    {
        if (ReferenceData.RowOf(typeId, code) is not null)
            return false;
        if (!SeedsOf(typeId).Any(s => s.RowsFrom?.Script is not null))
            return true;
        return JudgesEveryScript && !ScriptedHolds(typeId, code);
    }

    /// <summary>
    /// Whether a former code of a reference type is one of its live codes (MQ7017, next-phase-2-design.md section 3.5): a stored row holds
    /// it, or, when this validation judges every rows script, a scripted row does. A save that runs no script leaves the scripted rows to
    /// whole-model validation.
    /// </summary>
    /// <param name="typeId">The reference type id.</param>
    /// <param name="code">The code.</param>
    /// <returns><see langword="true"/> when a row holds it.</returns>
    public bool HasLiveCode(string typeId, JsonElement code) =>
        ReferenceData.RowOf(typeId, code) is not null || (JudgesEveryScript && ScriptedHolds(typeId, code));

    /// <summary>Whether a scripted row of a seed of the reference type holds the code.</summary>
    private bool ScriptedHolds(string typeId, JsonElement code)
    {
        var key = ReferenceDataIndex.CodeKey(code);
        foreach (var seed in SeedsOf(typeId).Where(s => s.RowsFrom?.Script is not null))
        {
            var at = -1;
            for (var k = 0; k < seed.Columns.Count && at < 0; k++)
            {
                if (string.Equals(seed.Columns[k], "code", StringComparison.Ordinal))
                    at = k;
            }

            if (at >= 0 && Scripted(seed).Rows.Any(r => at < r.Values.Count && string.Equals(ReferenceDataIndex.CodeKey(r.Values[at]), key, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary>A seed with a rows script: its scripted rows and their findings, evaluated once per validation.</summary>
    /// <param name="seed">The seed (its <c>rowsFrom.script</c> is set).</param>
    /// <returns>The scripted rows.</returns>
    public ScriptedRows Scripted(Seed seed) =>
        _scripted.GetOrAdd(seed.Id, _ => new Lazy<ScriptedRows>(
            () => SeedScripts.Evaluate(Model, seed, _scripts ?? DefaultScripts, _seedRows ?? DefaultSeedRows, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Whether a reference type has a seed with a rows script (its codes are then not all in the index: decision 17).</summary>
    /// <param name="typeId">The reference type id.</param>
    /// <returns><see langword="true"/> when it has one.</returns>
    public bool HasScriptedSeed(string typeId) => SeedsOf(typeId).Any(s => s.RowsFrom?.Script is not null);

    private static readonly Scripting.IScriptSandboxFactory DefaultScripts = new Scripting.ScriptSandboxFactory();
    private static readonly SeedRowsCache DefaultSeedRows = new();

    /// <summary>The database types by name and the dependency cycles among routines, database types and SQL objects (MQ4018 to MQ4020).</summary>
    public DatabaseObjectIndex DatabaseObjects => _databaseObjects.Value;

    /// <summary>The layers packages declare: which are checked and what each may reference (MQ2020, MQ2021).</summary>
    public PackageLayers Layers => _layers.Value;

    /// <summary>The order seeds of one target merge in (<see cref="Resolution.SeedOrder"/>), and their packages' names for messages.</summary>
    public SeedOrder SeedOrder => _seedOrder.Value;

    /// <summary>The reference-data facts: reference-typed attributes, ends and each type's codes.</summary>
    public ReferenceDataIndex ReferenceData => _referenceData.Value;

    /// <summary>The seeds of a target in merge order (<see cref="Resolution.SeedOrder"/>: package qualified name with none first, then name, then id).</summary>
    /// <param name="targetId">The entity, relation, reference type or table id.</param>
    /// <returns>The seeds.</returns>
    public ImmutableArray<Seed> SeedsOf(string targetId) => _seeds.Value.TryGetValue(targetId, out var list) ? list : [];

    /// <summary>The seeds of every target, keyed by target id.</summary>
    public FrozenDictionary<string, ImmutableArray<Seed>> AllSeeds => _seeds.Value;

    /// <summary>The seed that holds a row id, or <see langword="null"/>.</summary>
    /// <param name="rowId">The row id.</param>
    /// <returns>The seed.</returns>
    public Seed? SeedOfRow(string rowId) => _rowSeeds.Value.TryGetValue(rowId, out var seed) ? seed : null;

    /// <summary>Whether a row is part of a cycle of required row references (MQ7103).</summary>
    /// <param name="rowId">The row id.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool IsCyclicRow(string rowId) => _cyclicRows.Value.Contains(rowId);

    private FrozenDictionary<string, ImmutableArray<Seed>> BuildSeeds() =>
        Documents.Select(d => d.Element).OfType<Seed>()
            .GroupBy(s => s.Target, StringComparer.Ordinal)
            .ToFrozenDictionary(
                g => g.Key,
                g => g.Order(SeedOrder).ToImmutableArray(),
                StringComparer.Ordinal);

    /// <summary>The reference types bound to a table file, with their binding, ordered by (type name, type id, binding index).</summary>
    /// <param name="tableId">The table id.</param>
    /// <returns>The bindings.</returns>
    public ImmutableArray<(ReferenceType Type, ReferenceTypeBinding Binding)> BoundTypesOf(string tableId) =>
        _boundTypes.Value.TryGetValue(tableId, out var list) ? list : [];

    private FrozenDictionary<string, ImmutableArray<(ReferenceType Type, ReferenceTypeBinding Binding)>> BuildBoundTypes() =>
        Documents.Select(d => d.Element).OfType<ReferenceType>()
            .OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal)
            .SelectMany(t => t.Bindings.Select(b => (Type: t, Binding: b)))
            .GroupBy(x => x.Binding.Table, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.Ordinal);

    private FrozenDictionary<string, Seed> BuildRowSeeds()
    {
        var map = new Dictionary<string, Seed>(StringComparer.Ordinal);
        foreach (var seed in Documents.Select(d => d.Element).OfType<Seed>())
        {
            foreach (var row in seed.Rows)
                map.TryAdd(row.Id, seed);
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
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

    /// <summary>Returns the virtual attributes an element's stereotypes add (its effective set), in stereotype then array order.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The attributes.</returns>
    public IEnumerable<FlatAttribute> VirtualAttributes(ElementBase element)
    {
        foreach (var stereotype in StereotypesOf(element))
        {
            foreach (var attribute in stereotype.Attributes)
                yield return new FlatAttribute(attribute, AttributeSource.Virtual, stereotype.Key, -1);
        }
    }

    /// <summary>
    /// The stereotypes an element carries that exist, its effective set: for a top-level element
    /// <see cref="ModelSnapshot.EffectiveStereotypes(Element)"/> (schema and package defaults, implied, then own; next-phase-2-design.md
    /// section 3.1), for a sub-element <see cref="ModelSnapshot.EffectiveStereotypes(IReadOnlyList{string})"/> (implied first, then own):
    /// the set its virtual attributes come from, and the set the <c>x-ref</c> scope <c>of: "$element"</c> reaches through
    /// (next-phase-1-design.md sections 4.1 and 4.2).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The stereotypes.</returns>
    public IEnumerable<Stereotype> StereotypesOf(ElementBase element) =>
        (element is Element top ? Model.EffectiveStereotypes(top) : Model.EffectiveStereotypes(element.Stereotypes)).Select(s => s.Stereotype);

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
            case Process process when process.Name.Length > 0:
                yield return ("process|" + process.Package + "|" + process.Name, "processes of the same package");
                break;
            case Actor actor when actor.Name.Length > 0:
                yield return ("actor|" + actor.Name, "actors");
                break;
            case Scenario scenario when scenario.Name.Length > 0:
                yield return ("scenario|" + scenario.Process + "|" + scenario.Name, "scenarios of the same process");
                break;
            // Routines, database types and SQL objects: one name per kind and schema of a database (a routine is not overloaded,
            // and each becomes one script); names compare case-insensitively, as the dialects compare unquoted identifiers.
            case Routine routine when routine.Name.Length > 0:
                yield return ("routine|" + routine.Database + "|" + routine.Schema + "|" + routine.Name.ToUpperInvariant(), "routines of the same database schema");
                break;
            case DatabaseType databaseType when databaseType.Name.Length > 0:
                yield return ("database-type|" + databaseType.Database + "|" + databaseType.Schema + "|" + databaseType.Name.ToUpperInvariant(),
                    "database types of the same database schema");
                break;
            case SqlObject sqlObject when sqlObject.Name.Length > 0:
                yield return ("sql-object|" + sqlObject.Database + "|" + sqlObject.Schema + "|" + sqlObject.Name.ToUpperInvariant(), "SQL objects of the same database schema");
                break;
            // A query becomes a repository class named after it: one name per database, compared case-insensitively.
            case Query query when query.Name.Length > 0:
                yield return ("query|" + query.Database + "|" + query.Name.ToUpperInvariant(), "queries of the same database");
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
        return table.Relation is { } target ? target + "@" + table.Database : null;
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

    /// <summary>
    /// Classifies a column's native type for MQ4006 and MQ4016: known when its base name is a native type of the dialect, the base of
    /// a value of the dialect's effective type map, the base of a native type a custom type declares for the dialect, or the snake or
    /// kebab name (with or without a <c>_t</c> suffix) of one of the model's reference types or enums; user-defined when it is
    /// otherwise written with identifier quotes or a prefix such as a schema; else unknown. SQLite accepts any type name.
    /// </summary>
    /// <param name="dialect">The database's dialect.</param>
    /// <param name="nativeType">The native type as written.</param>
    /// <param name="parsed">The parsed native type.</param>
    /// <returns>The classification.</returns>
    public NativeTypeStatus ClassifyNativeType(Dialect dialect, string nativeType, out NativeTypeName parsed)
    {
        parsed = DialectInfo.Parse(nativeType);
        if (dialect == Dialect.Sqlite)
            return NativeTypeStatus.Known;
        var name = parsed.BaseName;
        if (name.Length > 0)
        {
            var known = _knownNativeTypes.GetOrAdd(dialect, d => DialectInfo.KnownBaseNames(d, Resolution.DialectTypeMaps.Effective(d, Model.Settings),
                ScalarNativeTypes(d)));
            if (known.Contains(name) || _modelTypeNames.Value.Contains(name))
                return NativeTypeStatus.Known;
        }

        return parsed.IsQuotedOrQualified ? NativeTypeStatus.UserDefined : NativeTypeStatus.Unknown;
    }

    /// <summary>The native types the active custom types declare for a dialect (<c>nativeTypes</c>), as written.</summary>
    /// <param name="dialect">The dialect.</param>
    /// <returns>The native types.</returns>
    private IEnumerable<string> ScalarNativeTypes(Dialect dialect)
    {
        var name = DialectInfo.Name(dialect);
        foreach (var doc in Documents)
        {
            if (doc.Element is ScalarType scalar && scalar.NativeTypes.TryGetValue(name, out var native))
                yield return native;
        }
    }

    /// <summary>
    /// The columns of file-backed tables in a database whose native type names one user-defined type (MQ4016), in document path and
    /// column order.
    /// </summary>
    /// <param name="databaseId">The database id.</param>
    /// <param name="typeKey">The type's key (see <see cref="UserDefinedTypeKey"/>).</param>
    /// <returns>The uses.</returns>
    public ImmutableArray<NativeTypeUse> UserDefinedTypeUses(string databaseId, string typeKey) =>
        _userDefinedTypes.Value.TryGetValue(databaseId + "|" + typeKey, out var list) ? list : [];

    /// <summary>
    /// The MQ4016 key of a user-defined native type: its unquoted lower-cased parts joined with dots, without a leading schema equal
    /// to the database's default schema, so <c>"public"."unit_of_measure"</c> and <c>"unit_of_measure"</c> are one type on a
    /// PostgreSQL database whose default schema is <c>public</c>.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="parsed">The parsed native type.</param>
    /// <returns>The key.</returns>
    public static string UserDefinedTypeKey(Database database, NativeTypeName parsed)
    {
        var parts = parsed.Parts;
        var defaultSchema = (database.DefaultSchema ?? DialectInfo.DefaultSchema(database.Dialect)).ToLowerInvariant();
        if (parts.Length == 2 && defaultSchema.Length > 0 && parts[0] == defaultSchema)
            return parts[1];
        return string.Join('.', parts);
    }

    /// <summary>
    /// The tables whose MQ4006 or MQ4016 findings can change with a document they do not reference: for a table, the first holder of
    /// each user-defined native type it uses (that holder reports the column count); for a reference type or an enum, the tables with
    /// a column whose native type carries one of its names; for a custom type, the tables with a column whose native type has the base
    /// name of one of the native types it declares.
    /// </summary>
    /// <param name="element">The changed element.</param>
    /// <returns>The documents (duplicates are possible).</returns>
    public IEnumerable<ElementDocument> NativeTypePeers(Element element)
    {
        if (element is Table table && Model.Get<Database>(table.Database) is { } database)
        {
            foreach (var column in table.Columns)
            {
                if (column.NativeType is { } native && ClassifyNativeType(database.Dialect, native, out var parsed) == NativeTypeStatus.UserDefined
                    && UserDefinedTypeUses(database.Id, UserDefinedTypeKey(database, parsed)) is { Length: > 0 } uses)
                {
                    yield return uses[0].Document;
                }
            }
        }
        else if (element is ReferenceType or EnumType)
        {
            var names = ModelTypeNames(element.Name);
            foreach (var doc in Documents)
            {
                if (doc.Element is Table other && other.Columns.Any(c => c.NativeType is { } n && names.Contains(DialectInfo.Parse(n).BaseName)))
                    yield return doc;
            }
        }
        else if (element is ScalarType { NativeTypes.Count: > 0 } scalar)
        {
            var names = scalar.NativeTypes.Values.Select(v => DialectInfo.Parse(v).BaseName).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            foreach (var doc in Documents)
            {
                if (doc.Element is Table other && other.Columns.Any(c => c.NativeType is { } n && names.Contains(DialectInfo.Parse(n).BaseName)))
                    yield return doc;
            }
        }
    }

    /// <summary>The native type names that stand for a reference type or enum: lower-cased, snake and kebab, each with and without <c>_t</c>.</summary>
    /// <param name="name">The element's name.</param>
    /// <returns>The names.</returns>
    public static IEnumerable<string> ModelTypeNames(string name)
    {
        if (name.Length == 0)
            yield break;
        foreach (var form in new[] { name.ToLowerInvariant(), Text.Casing.Snake(name), Text.Casing.Kebab(name) })
        {
            yield return form;
            yield return form + "_t";
        }
    }

    private FrozenSet<string> BuildModelTypeNames() =>
        Documents.Select(d => d.Element).Where(e => e is ReferenceType or EnumType)
            .SelectMany(e => ModelTypeNames(e.Name)).ToFrozenSet(StringComparer.Ordinal);

    private FrozenDictionary<string, ImmutableArray<NativeTypeUse>> BuildUserDefinedTypes()
    {
        var map = new Dictionary<string, ImmutableArray<NativeTypeUse>.Builder>(StringComparer.Ordinal);
        foreach (var doc in Documents)
        {
            if (doc.Element is not Table table || Model.Get<Database>(table.Database) is not { } database)
                continue;
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (table.Columns[i].NativeType is { } native && ClassifyNativeType(database.Dialect, native, out var parsed) == NativeTypeStatus.UserDefined)
                    Append(map, database.Id + "|" + UserDefinedTypeKey(database, parsed), new NativeTypeUse(doc, i));
            }
        }

        return Freeze(map);
    }
}

/// <summary>How the validator reads a column's native type (MQ4006, MQ4016).</summary>
internal enum NativeTypeStatus
{
    /// <summary>A native type of the dialect, of its type map, or a name of the model's reference types and enums.</summary>
    Known,

    /// <summary>A quoted or qualified name the validator cannot check: a type the database defines (MQ4016).</summary>
    UserDefined,

    /// <summary>A plain name the dialect does not know (MQ4006).</summary>
    Unknown,
}

/// <summary>A table column whose native type is a user-defined type (MQ4016).</summary>
/// <param name="Document">The table's document.</param>
/// <param name="Column">The column's index.</param>
internal sealed record NativeTypeUse(ElementDocument Document, int Column);
