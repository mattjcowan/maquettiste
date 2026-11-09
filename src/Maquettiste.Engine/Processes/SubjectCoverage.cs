using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// What a process's subject entries cover (stereotype-sets-design.md decisions 3 and 4). An entity entry covers its entity and the entities
/// derived from it. A stereotype entry covers each entity that carries the stereotype (it is in the entity's effective set: package
/// defaults, implied stereotypes, its own list, less what it excludes) and every entity derived from one, even one that excludes the
/// stereotype as a default. When several entries of one process cover an entity, an entry naming the entity or a base wins, nearest first;
/// then stereotype entries, in entry order.
/// <para>
/// Built once per snapshot. It does no work when no entry names a stereotype: <see cref="Any"/> is false and every map is empty; the
/// precedence then is today's rule for a base and a derived entity.
/// </para>
/// </summary>
internal sealed class SubjectCoverage
{
    private static readonly ConditionalWeakTable<ModelSnapshot, SubjectCoverage> ByModel = new();

    private static readonly SubjectCoverage Empty = new();

    private readonly FrozenDictionary<string, ImmutableArray<Entity>> _carriers;
    private readonly FrozenDictionary<string, FrozenSet<string>> _covered;
    private readonly FrozenDictionary<string, ImmutableArray<Process>> _listing;

    private SubjectCoverage()
    {
        _carriers = FrozenDictionary<string, ImmutableArray<Entity>>.Empty;
        _covered = FrozenDictionary<string, FrozenSet<string>>.Empty;
        _listing = FrozenDictionary<string, ImmutableArray<Process>>.Empty;
        Keys = [];
    }

    private SubjectCoverage(ModelSnapshot model, Dictionary<string, List<Process>> listing)
    {
        _listing = listing.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToImmutableArray(), StringComparer.Ordinal);
        Keys = [.. listing.Keys.Order(StringComparer.Ordinal)];
        var direct = listing.Keys.ToDictionary(k => k, _ => new List<Entity>(), StringComparer.Ordinal);
        var entities = model.All<Entity>().ToList();
        foreach (var entity in entities)
        {
            foreach (var applied in model.EffectiveStereotypes(entity))
            {
                if (direct.TryGetValue(applied.Stereotype.Key, out var list) && !list.Contains(entity))
                    list.Add(entity);
            }
        }

        // An entity is covered when it or a base carries the stereotype (a parent cycle stops at its first repeat).
        var covered = listing.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var carrierIds = direct.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => e.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            foreach (var id in Chain(model, entity))
            {
                foreach (var (key, ids) in carrierIds)
                {
                    if (ids.Contains(id))
                        covered[key].Add(entity.Id);
                }
            }
        }

        _carriers = direct.ToFrozenDictionary(kv => kv.Key, kv => Ordered(model, kv.Value), StringComparer.Ordinal);
        _covered = covered.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>Whether some process lists a stereotype; without one every lookup is empty and costs nothing.</summary>
    public bool Any => Keys.Length > 0;

    /// <summary>The stereotype keys the processes' subject entries name, known or not, ordinal.</summary>
    public ImmutableArray<string> Keys { get; }

    /// <summary>The coverage of a model, built once per snapshot.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The coverage.</returns>
    public static SubjectCoverage Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ByModel.GetValue(model, static m =>
        {
            Dictionary<string, List<Process>>? listing = null;
            foreach (var process in m.All<Process>())
            {
                foreach (var entry in process.Subjects)
                {
                    if (entry.Stereotype is not { } key)
                        continue;
                    listing ??= new Dictionary<string, List<Process>>(StringComparer.Ordinal);
                    if (!listing.TryGetValue(key, out var list))
                        listing[key] = list = [];
                    if (!list.Contains(process))
                        list.Add(process);
                }
            }

            return listing is null ? Empty : new SubjectCoverage(m, listing);
        });
    }

    /// <summary>Whether a process has an entry that names a stereotype.</summary>
    /// <param name="process">The process.</param>
    /// <returns>Whether it has one.</returns>
    public static bool NamesStereotype(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        foreach (var entry in process.Subjects)
        {
            if (entry.Stereotype is not null)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The entities that carry a listed stereotype (it is in their effective set), by qualified name then id; empty for a key no entry names,
    /// an unknown key, or a stereotype nothing carries yet. Derived entities that do not carry it themselves are not listed.
    /// </summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns>The carriers.</returns>
    public ImmutableArray<Entity> CarriersOf(string key) => _carriers.GetValueOrDefault(key, []);

    /// <summary>Whether an entity carries a listed stereotype, or derives from an entity that does.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <param name="entityId">The entity id.</param>
    /// <returns>Whether the stereotype covers it.</returns>
    public bool Covers(string key, string entityId) => _covered.TryGetValue(key, out var set) && set.Contains(entityId);

    /// <summary>The processes whose subject entries name a stereotype key, in model order.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns>The processes.</returns>
    public ImmutableArray<Process> ProcessesListing(string key) => _listing.GetValueOrDefault(key, []);

    /// <summary>
    /// The entry of a process that governs an entity (decision 4): an entry naming the entity or a base, nearest first; else the first
    /// stereotype entry, in entry order, that covers it; <see langword="null"/> when none does.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The entry.</returns>
    public static ProcessSubject? EntryOf(ModelSnapshot model, Process process, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(entity);
        var chain = Chain(model, entity);
        ProcessSubject? best = null;
        var bestAt = int.MaxValue;
        foreach (var entry in process.Subjects)
        {
            if (entry.Entity is not { } id)
                continue;
            var at = chain.IndexOf(id);
            if (at >= 0 && at < bestAt)
            {
                best = entry;
                bestAt = at;
            }
        }

        if (best is not null || !NamesStereotype(process))
            return best;
        var coverage = Of(model);
        foreach (var entry in process.Subjects)
        {
            if (entry.Stereotype is { } key && coverage.Covers(key, entity.Id))
                return entry;
        }

        return null;
    }

    /// <summary>
    /// The carriers a stereotype entry governs (decision 4's <c>covers</c>), by qualified name then id: each carrier of its stereotype that
    /// no other entry wins. Empty for an entity entry.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <param name="entry">One of its entries.</param>
    /// <returns>The carriers.</returns>
    public static IReadOnlyList<Entity> Governed(ModelSnapshot model, Process process, ProcessSubject entry)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Stereotype is not { } key)
            return [];
        var carriers = Of(model).CarriersOf(key);
        if (carriers.IsEmpty)
            return [];
        var governed = new List<Entity>(carriers.Length);
        foreach (var carrier in carriers)
        {
            if (ReferenceEquals(EntryOf(model, process, carrier), entry))
                governed.Add(carrier);
        }

        return governed;
    }

    /// <summary>
    /// Whether a process governs an entity (an entry covers it, decision 3): it, a base, or a stereotype it or a base carries is listed.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether an entry covers it.</returns>
    public static bool Governs(ModelSnapshot model, Process process, Entity entity) => EntryOf(model, process, entity) is not null;

    /// <summary>The prefix of a stereotype's carrier-set dependency key, <c>cs:&lt;stereotype key&gt;</c> (stereotype-sets-design.md section 6).</summary>
    public const string CarrierSetPrefix = "cs:";

    /// <summary>The dependency key of which stereotypes the processes' subject entries name (stereotype-sets-design.md section 6).</summary>
    public const string NamingKey = "sn:subjects";

    private static readonly ConditionalWeakTable<ModelSnapshot, System.Collections.Concurrent.ConcurrentDictionary<string, string>> CarrierSetHashes = new();

    private static readonly ConditionalWeakTable<ModelSnapshot, string> NamingHashes = new();

    /// <summary>The carrier-set key of a stereotype key.</summary>
    /// <param name="key">The stereotype key.</param>
    /// <returns><c>cs:&lt;key&gt;</c>.</returns>
    public static string CarrierSetKey(string key) => CarrierSetPrefix + key;

    /// <summary>
    /// The current hash of <c>cs:&lt;stereotype key&gt;</c>: the id of the stereotype that has the key (if any), the entities that carry it
    /// in carrier order (qualified name, then id), then every entity it covers (carriers and the entities derived from them), ordinal. It
    /// changes when an entity starts or stops carrying the stereotype, by its own list, a package default or an implied stereotype; when a
    /// base change brings an entity under a carrier or out from under one; when a carrier's qualified name moves it in the order; and when
    /// the key starts or stops naming a stereotype. Computed for any key, named or not, once per snapshot.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="key">The stereotype key.</param>
    /// <returns>The hash.</returns>
    public static string CarrierSetHash(ModelSnapshot model, string key)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(key);
        return CarrierSetHashes.GetValue(model, static _ => new(StringComparer.Ordinal)).GetOrAdd(key, static (k, m) => ComputeCarrierSetHash(m, k), model);
    }

    private static string ComputeCarrierSetHash(ModelSnapshot model, string key)
    {
        var coverage = Of(model);
        ImmutableArray<Entity> carriers;
        IEnumerable<string> covered;
        if (coverage._covered.TryGetValue(key, out var known))
        {
            carriers = coverage.CarriersOf(key);
            covered = known;
        }
        else
        {
            // A key no entry names now (a stored read key from an earlier run): worked out the same way, for this key alone.
            var entities = model.All<Entity>().ToList();
            var direct = entities.Where(e => model.EffectiveStereotypes(e).Any(a => string.Equals(a.Stereotype.Key, key, StringComparison.Ordinal))).ToList();
            var ids = direct.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            carriers = Ordered(model, direct);
            covered = ids.Count == 0 ? [] : entities.Where(e => Chain(model, e).Any(ids.Contains)).Select(e => e.Id);
        }

        using var hash = new Hashing.HashBuilder();
        hash.Add("cs").Add(key).Add(model.GetStereotype(key)?.Id ?? "");
        foreach (var carrier in carriers)
            hash.Add(carrier.Id);
        hash.Add("covered");
        foreach (var id in covered.Order(StringComparer.Ordinal))
            hash.Add(id);
        return hash.Finish();
    }

    /// <summary>
    /// The current hash of <see cref="NamingKey"/>: for each process with a stereotype entry, by id, its id, its use and the stereotype keys
    /// its entries name, in entry order. It changes when a process starts or stops naming a stereotype, names another one, or changes its
    /// use; not when an entity entry changes (those reach the entity's referrers).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <returns>The hash.</returns>
    public static string NamingHash(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return NamingHashes.GetValue(model, static m =>
        {
            using var hash = new Hashing.HashBuilder();
            hash.Add("sn");
            foreach (var process in m.All<Process>().Where(NamesStereotype).OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                hash.Add(process.Id).Add(process.Use.ToString());
                foreach (var entry in process.Subjects)
                {
                    if (entry.Stereotype is { } key)
                        hash.Add(key);
                }

                hash.Add("/");
            }

            return hash.Finish();
        });
    }

    /// <summary>The ids of an entity and its bases, nearest first; a base cycle stops at its first repeat.</summary>
    /// <param name="model">The model.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The chain.</returns>
    public static List<string> Chain(ModelSnapshot model, Entity entity)
    {
        var chain = new List<string>();
        for (var current = entity; current is not null && !chain.Contains(current.Id, StringComparer.Ordinal);
             current = current.Base is { } b ? model.Get<Entity>(b) : null)
        {
            chain.Add(current.Id);
        }

        return chain;
    }

    internal static ImmutableArray<Entity> Ordered(ModelSnapshot model, List<Entity> entities)
    {
        if (entities.Count == 0)
            return [];
        var names = model.Names("entity");
        return [.. entities.OrderBy(e => names.Get(e.Id)?.Longest ?? e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)];
    }
}
