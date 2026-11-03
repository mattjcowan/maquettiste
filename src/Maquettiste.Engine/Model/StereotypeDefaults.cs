using System.Collections.Immutable;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Model;

/// <summary>
/// What an element's effective stereotype set is built from (next-phase-2-design.md section 3.1): its own and excluded keys, the
/// package whose chain gives package defaults, and the database schema entry whose defaults reach it.
/// <see cref="ModelSnapshot.HolderOf"/> returns it; <see cref="ModelSnapshot.EffectiveStereotypes(StereotypeHolder)"/> builds the set.
/// </summary>
/// <param name="Kind">The element's kind: a default skips an element whose kind it does not apply to.</param>
/// <param name="Own">The element's own stereotype keys, as its file lists them.</param>
/// <param name="Excludes">The element's <c>excludeStereotypes</c> keys.</param>
/// <param name="Package">
/// The package whose chain gives package defaults: the element's own (a package's parent), else the fallback of
/// next-phase-1-design.md section 4.3 (a table overlay's entity's or relation's, a seed's target's, a scenario's process's);
/// <see langword="null"/> outside the packages and for a migration fact.
/// </param>
/// <param name="Schema">
/// The id of the database schema entry whose defaults reach the element: set for tables, views, sequences, routines, database types
/// and SQL objects when their database has such an entry (<c>StereotypeDefaults.SchemaOf</c>), else <see langword="null"/>.
/// </param>
public sealed record StereotypeHolder(ElementKind Kind, IReadOnlyList<string> Own, IReadOnlyList<string> Excludes, string? Package, string? Schema);

/// <summary>
/// Holders and the layered effective set of default stereotypes (next-phase-2-design.md section 3.1). Built from the element documents
/// alone, before the index, so the index's walks can use it; its lookups are built on first use. Thread-safe after construction.
/// </summary>
internal sealed class StereotypeDefaults
{
    private readonly ImmutableArray<ElementDocument> _documents;
    private readonly Lazy<Lookups> _lookups;

    /// <summary>Wraps the documents of a snapshot, sorted by path (the first document of a duplicated id wins, as in the index).</summary>
    /// <param name="documents">The documents.</param>
    public StereotypeDefaults(ImmutableArray<ElementDocument> documents)
    {
        _documents = documents;
        _lookups = new Lazy<Lookups>(() => new Lookups(_documents), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Whether any package or database schema entry declares a default stereotype; without one every set is phase 1's.</summary>
    public bool HasDefaults => _lookups.Value.HasDefaults;

    /// <summary>The holder of an element (see <see cref="StereotypeHolder"/>), computed from the documents.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The holder.</returns>
    public StereotypeHolder HolderOf(Element element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new StereotypeHolder(element.Kind, element.Stereotypes, element.ExcludeStereotypes ?? [], PackageOf(element), SchemaOfElement(element));
    }

    /// <summary>
    /// The package whose chain gives an element its package defaults: its own (a package's parent), else the phase 1 fallback: a
    /// synthesized table's entity's (a child table's owner entity's) or relation's, a seed's target's, a scenario's process's, an
    /// operation's subject's (next-phase-2-design.md section 4.1).
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The package id, or <see langword="null"/>.</returns>
    public string? PackageOf(Element element)
    {
        if (ModelIndexer.PackageOf(element) is { } own)
            return own;
        var lookups = _lookups.Value;
        return element switch
        {
            Table { Origin: TableOrigin.Synthesized } overlay => overlay.Entity is { } entity ? OwnPackage(lookups.ElementById(entity))
                : overlay.Relation is { } relation ? OwnPackage(lookups.ElementById(relation)) : null,
            Seed seed => lookups.ElementById(seed.Target) is { } target and not Seed ? PackageOf(target) : null,
            Scenario scenario => OwnPackage(lookups.ElementById(scenario.Process)),
            Operation { Subject: { } subject } => OwnPackage(lookups.ElementById(subject)),
            _ => null,
        };

        static string? OwnPackage(Element? element) => element is null ? null : ModelIndexer.PackageOf(element);
    }

    /// <summary>
    /// The schema entry whose defaults reach a physical object (tables, views, sequences, routines, database types and SQL objects),
    /// by ids: the stated schema; else, for an entity's table, the schema the database routes it to
    /// (<see cref="DatabaseScope.SchemaFor(Func{string, string?}, Database, string?, Mapping?)"/>); for a child table, its owner's
    /// table's (an entity's table as above; a relation's table, junction or promoted, the schema its relation overlay states); else the
    /// entry the database's default schema names. A stated or routed id that names no entry of the database falls back to that entry
    /// too, as the resolver's schema name does. The resolver takes a table without a file through the same rule.
    /// </summary>
    /// <param name="databaseId">The object's database.</param>
    /// <param name="schema">The schema id the object's file states, or <see langword="null"/>.</param>
    /// <param name="entity">For a synthesized table: its entity (a child table's owner); else <see langword="null"/>.</param>
    /// <param name="attribute">For a synthesized child table: the owner's attribute; else <see langword="null"/>.</param>
    /// <param name="relation">
    /// For a relation's table (junction or promoted), or a child table without a file that it owns: the relation; else
    /// <see langword="null"/>. (A table file cannot state an attribute without an entity, so such a child table never has a file.)
    /// </param>
    /// <returns>The schema entry id, or <see langword="null"/> when the database has no such entry (or is unknown).</returns>
    public string? SchemaOf(string databaseId, string? schema, string? entity = null, string? attribute = null, string? relation = null)
    {
        var lookups = _lookups.Value;
        if (lookups.ElementById(databaseId) is not Database database)
            return null;
        if (schema is null && entity is not null)
        {
            if (attribute is not null)
                return SchemaOf(databaseId, lookups.EntityOverlayOf(databaseId, entity)?.Schema, entity);
            schema = DatabaseScope.SchemaFor(id => (lookups.ElementById(id) as Package)?.Parent, database, OwnPackageOf(lookups.ElementById(entity)),
                lookups.MappingOf(databaseId, entity));
        }
        else if (schema is null && relation is not null)
        {
            // A relation's table is in the schema its overlay states (the database does not route relations), its child tables with it.
            schema = lookups.RelationOverlayOf(databaseId, relation)?.Schema;
        }

        if (schema is not null && Contains(database, schema))
            return schema;
        var defaultName = database.DefaultSchema ?? DialectInfo.DefaultSchema(database.Dialect);
        foreach (var entry in database.Schemas)
        {
            if (string.Equals(entry.Name, defaultName, StringComparison.Ordinal))
                return entry.Id;
        }

        return null;

        static string? OwnPackageOf(Element? element) => element is null ? null : ModelIndexer.PackageOf(element);

        static bool Contains(Database database, string schemaId)
        {
            foreach (var entry in database.Schemas)
            {
                if (string.Equals(entry.Id, schemaId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

    private string? SchemaOfElement(Element element) => element switch
    {
        Table t => SchemaOf(t.Database, t.Schema, t.Origin == TableOrigin.Synthesized ? t.Entity : null, t.Origin == TableOrigin.Synthesized ? t.Attribute : null),
        View v => SchemaOf(v.Database, v.Schema),
        Sequence s => SchemaOf(s.Database, s.Schema),
        Routine r => SchemaOf(r.Database, r.Schema),
        DatabaseType d => SchemaOf(d.Database, d.Schema),
        SqlObject o => SchemaOf(o.Database, o.Schema),
        _ => null,
    };

    /// <summary>The packages on a package's chain that declare default stereotypes, outermost first; a parent cycle stops at its first repeat.</summary>
    /// <param name="packageId">The innermost package, or <see langword="null"/>.</param>
    /// <returns>The packages.</returns>
    private List<Package> DefaultChain(string? packageId)
    {
        var lookups = _lookups.Value;
        var chain = new List<Package>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = packageId; id is not null && seen.Add(id);)
        {
            if (lookups.ElementById(id) is not Package package)
                break;
            if (package.DefaultStereotypes.Count > 0)
                chain.Add(package);
            id = package.Parent;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// The effective set of a holder (next-phase-2-design.md section 3.1, decision 5), in layers: the schema's defaults, then the package
    /// defaults from the outermost package to the holder's own, then the own list. Within a layer the stereotypes its entries imply come
    /// first, then the entries, as phase 1 orders the own list. A default entry that <see cref="StereotypeHolder.Excludes"/> names, or
    /// that does not apply to the holder's kind, leaves with the copies it implies; an implied copy that is excluded or does not apply
    /// leaves too. A key listed twice then keeps its last place: a later layer beats an earlier one. Without defaults the set is phase 1's.
    /// </summary>
    /// <param name="holder">The holder.</param>
    /// <param name="stereotypeOf">Finds a stereotype by key (unknown keys are skipped).</param>
    /// <returns>The effective set.</returns>
    public IReadOnlyList<AppliedStereotype> Effective(StereotypeHolder holder, Func<string, Stereotype?> stereotypeOf)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (!HasDefaults)
            return Layer(holder.Own, stereotypeOf);
        var lookups = _lookups.Value;
        List<AppliedStereotype>? result = null;
        var kindName = KindInfo.Get(holder.Kind).Name;
        bool Keep(Stereotype stereotype) =>
            !holder.Excludes.Contains(stereotype.Key, StringComparer.Ordinal)
            && (stereotype.AppliesTo.Count == 0 || stereotype.AppliesTo.Contains(kindName, StringComparer.Ordinal));
        if (holder.Schema is { } schemaId && lookups.SchemaById(schemaId) is { DefaultStereotypes.Count: > 0 } schema)
            (result ??= []).AddRange(DefaultLayer(schema.DefaultStereotypes, stereotypeOf, Keep, StereotypeSource.SchemaDefault, schemaId));
        foreach (var package in DefaultChain(holder.Package))
            (result ??= []).AddRange(DefaultLayer(package.DefaultStereotypes, stereotypeOf, Keep, StereotypeSource.PackageDefault, package.Id));
        var own = Layer(holder.Own, stereotypeOf);
        if (result is null || result.Count == 0)
            return own;
        result.AddRange(own);
        return LastPlaces(result);
    }

    /// <summary>
    /// The phase 1 effective set of an own key list (next-phase-1-design.md section 4.2): implied first, then own; an own entry replaces
    /// an implied copy of the same key; unknown keys skipped. Implication is one level, in entry then <c>implies</c> order, each key once.
    /// </summary>
    /// <param name="keys">The keys, as a file lists them.</param>
    /// <param name="stereotypeOf">Finds a stereotype by key.</param>
    /// <returns>The set: own entries with <see cref="StereotypeSource.Own"/>, implied ones with <see cref="StereotypeSource.Implied"/>.</returns>
    public static IReadOnlyList<AppliedStereotype> Layer(IReadOnlyList<string> keys, Func<string, Stereotype?> stereotypeOf)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            return [];
        var own = new List<AppliedStereotype>(keys.Count);
        var implies = false;
        for (var i = 0; i < keys.Count; i++)
        {
            if (stereotypeOf(keys[i]) is not { } stereotype)
                continue;
            own.Add(new AppliedStereotype(stereotype, i, null));
            implies |= stereotype.Implies.Count > 0;
        }

        if (!implies)
            return own;
        var result = new List<AppliedStereotype>(own.Count * 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in own)
            seen.Add(entry.Stereotype.Key);
        foreach (var entry in own)
        {
            foreach (var key in entry.Stereotype.Implies)
            {
                if (seen.Add(key) && stereotypeOf(key) is { } implied)
                    result.Add(new AppliedStereotype(implied, entry.Entry, entry.Stereotype.Key) { Source = StereotypeSource.Implied });
            }
        }

        result.AddRange(own);
        return result;
    }

    /// <summary>One default layer: the kept entries' kept implied copies (each key once, entries' keys excluded), then the kept entries.</summary>
    private static List<AppliedStereotype> DefaultLayer(IReadOnlyList<string> keys, Func<string, Stereotype?> stereotypeOf, Func<Stereotype, bool> keep,
        StereotypeSource source, string holderId)
    {
        var entries = new List<Stereotype>(keys.Count);
        foreach (var key in keys)
        {
            if (stereotypeOf(key) is { } stereotype && keep(stereotype))
                entries.Add(stereotype);
        }

        var layer = new List<AppliedStereotype>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            seen.Add(entry.Key);
        foreach (var entry in entries)
        {
            foreach (var key in entry.Implies)
            {
                if (!seen.Contains(key) && stereotypeOf(key) is { } implied && keep(implied))
                {
                    seen.Add(key);
                    layer.Add(new AppliedStereotype(implied, -1, entry.Key) { Source = source, DefaultHolder = holderId });
                }
            }
        }

        foreach (var entry in entries)
            layer.Add(new AppliedStereotype(entry, -1, null) { Source = source, DefaultHolder = holderId });
        return layer;
    }

    /// <summary>Keeps each key's last occurrence, in order.</summary>
    private static List<AppliedStereotype> LastPlaces(List<AppliedStereotype> list)
    {
        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
            last[list[i].Stereotype.Key] = i;
        if (last.Count == list.Count)
            return list;
        var result = new List<AppliedStereotype>(last.Count);
        for (var i = 0; i < list.Count; i++)
        {
            if (last[list[i].Stereotype.Key] == i)
                result.Add(list[i]);
        }

        return result;
    }

    /// <summary>The few lookups holders need, built once from the documents.</summary>
    private sealed class Lookups
    {
        private readonly Dictionary<string, Element> _elements = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DbSchema> _schemas = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Database, string Entity), Mapping> _mappings = [];
        private readonly Dictionary<(string Database, string Entity), Table> _entityOverlays = [];
        private readonly Dictionary<(string Database, string Relation), Table> _relationOverlays = [];

        public Lookups(ImmutableArray<ElementDocument> documents)
        {
            var mappings = new List<Mapping>();
            var overlays = new List<Table>();
            foreach (var document in documents)
            {
                var element = document.Element;
                if (element is not (Package or Entity or Relation or Database or Process or Table or ReferenceType or Mapping)
                    || !_elements.TryAdd(element.Id, element))
                {
                    continue;
                }

                switch (element)
                {
                    case Package { DefaultStereotypes.Count: > 0 }:
                        HasDefaults = true;
                        break;
                    case Database database:
                        foreach (var schema in database.Schemas)
                        {
                            _schemas.TryAdd(schema.Id, schema);
                            HasDefaults |= schema.DefaultStereotypes.Count > 0;
                        }

                        break;
                    case Mapping { Entity: not null } mapping:
                        mappings.Add(mapping);
                        break;
                    case Table { Origin: TableOrigin.Synthesized, Attribute: null } overlay when overlay.Entity is not null || overlay.Relation is not null:
                        overlays.Add(overlay);
                        break;
                }
            }

            // As the resolver: the mapping and the overlay with the ordinally first id win.
            foreach (var mapping in mappings.OrderBy(m => m.Id, StringComparer.Ordinal))
                _mappings.TryAdd((mapping.Database, mapping.Entity!), mapping);
            foreach (var overlay in overlays.OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                if (overlay.Entity is { } entity)
                    _entityOverlays.TryAdd((overlay.Database, entity), overlay);
                else
                    _relationOverlays.TryAdd((overlay.Database, overlay.Relation!), overlay);
            }
        }

        public bool HasDefaults { get; }

        public Element? ElementById(string id) => _elements.GetValueOrDefault(id);

        public DbSchema? SchemaById(string id) => _schemas.GetValueOrDefault(id);

        public Mapping? MappingOf(string databaseId, string entityId) => _mappings.GetValueOrDefault((databaseId, entityId));

        public Table? EntityOverlayOf(string databaseId, string entityId) => _entityOverlays.GetValueOrDefault((databaseId, entityId));

        public Table? RelationOverlayOf(string databaseId, string relationId) => _relationOverlays.GetValueOrDefault((databaseId, relationId));
    }
}
