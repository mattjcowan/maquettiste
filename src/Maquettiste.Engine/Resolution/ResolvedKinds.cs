using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The kinds of the resolved model's annotated objects (next-phase-2-design.md section 5.5): the <see cref="IResolvedObject.Kind"/> of
/// every concrete <see cref="RAnnotated"/> type, found by reflection, so a type added later is a kind a calculated value can name without
/// a list to keep. Kinds without resolved objects (<c>diagram</c>, <c>mapping</c>) are not among them.
/// </summary>
internal static class ResolvedKinds
{
    private static readonly FrozenDictionary<string, Type> Types = Discover();

    /// <summary>Every resolved kind, ordinal.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Types.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The resolved type of a kind, or <see langword="null"/> when no annotated object has it.</summary>
    /// <param name="kind">The kind name, such as <c>entity</c>.</param>
    /// <returns>The type.</returns>
    public static Type? TypeOf(string kind) => kind is not null && Types.TryGetValue(kind, out var type) ? type : null;

    /// <summary>Whether a name is a resolved kind.</summary>
    /// <param name="kind">The kind name.</param>
    /// <returns><see langword="true"/> for a kind of <see cref="All"/>.</returns>
    public static bool Contains(string kind) => kind is not null && Types.ContainsKey(kind);

    /// <summary>
    /// The site of an object: the key that tells its calculated values apart from those of every other object. It is the object's id,
    /// except for an attribute whose holder (<see cref="RAttribute.Owner"/>) is not the element that owns the attribute's id in the index
    /// (an inherited attribute, a stereotype's virtual attribute, an attribute copied onto a relation's entity): then it is
    /// <c>&lt;holder id&gt;/&lt;attribute id&gt;</c>, so each holder gets its own value.
    /// </summary>
    /// <param name="value">The object.</param>
    /// <param name="snapshot">The snapshot it was resolved from (its index names the owner of an id).</param>
    /// <returns>The site.</returns>
    public static string SiteOf(IResolvedObject value, ModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (value is not RAttribute { Owner: { } holder } attribute)
            return value.Id;
        var owner = snapshot.TryGetEntry(attribute.Id, out var entry) ? entry.OwnerId : null;
        return string.Equals(owner, holder.Id, StringComparison.Ordinal) ? attribute.Id : holder.Id + "/" + attribute.Id;
    }

    /// <summary>
    /// Every annotated object of the given kinds reachable from the model's lists through the members scripts see, once per object by
    /// reference, in (kind, site) ordinal order. Only whole-model runs call it, once per resolved model. The walk is kind-aware
    /// (<see cref="Plan"/>): it enters the model's lists and, below them, only the members whose types can lead to an object of the
    /// kinds, and never goes back up to an element of a model list (an attribute's owner, a relation's ends' entities), which the
    /// lists already hold. Members whose type cannot hold an annotated object (rows, names, JSON values) are not walked, so a walk
    /// never builds lazily resolved rows.
    /// </summary>
    /// <param name="model">The resolved model.</param>
    /// <param name="kinds">The kinds; names that are not resolved kinds are ignored.</param>
    /// <returns>The objects.</returns>
    public static IReadOnlyList<RAnnotated> ObjectsOf(ResolvedModel model, IEnumerable<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(kinds);
        string[] wanted = [.. kinds.Where(Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (wanted.Length == 0)
            return [];
        var plan = Plans.GetOrAdd(string.Join('\n', wanted), static (_, w) => new Plan(w), wanted);
        // Nested is false only for the model's own lists and their items: an element of a model list met below them is not entered.
        var pending = new Stack<(object Value, bool Nested)>();
        foreach (var getter in plan.Follow(typeof(ResolvedModel)))
        {
            if (getter(model) is { } value)
                pending.Push((value, false));
        }

        return Walk(plan, pending, model);
    }

    /// <summary>
    /// The annotated objects of the given kinds at and below one object, through the members scripts see, never entering an element of
    /// a model list other than the root (so an entity's attributes, not its base entity's record); once per object, in (kind, site)
    /// ordinal order. The resolved model's paged reader uses it for the objects a record lists.
    /// </summary>
    /// <param name="model">The resolved model the object belongs to.</param>
    /// <param name="root">The object.</param>
    /// <param name="kinds">The kinds; names that are not resolved kinds are ignored.</param>
    /// <returns>The objects.</returns>
    public static IReadOnlyList<RAnnotated> ObjectsUnder(ResolvedModel model, object root, IEnumerable<string> kinds)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(kinds);
        string[] wanted = [.. kinds.Where(Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (wanted.Length == 0)
            return [];
        var plan = Plans.GetOrAdd(string.Join('\n', wanted), static (_, w) => new Plan(w), wanted);
        var pending = new Stack<(object Value, bool Nested)>();
        pending.Push((root, false));
        return Walk(plan, pending, model);
    }

    private static IReadOnlyList<RAnnotated> Walk(Plan plan, Stack<(object Value, bool Nested)> pending, ResolvedModel model)
    {
        var found = new List<(RAnnotated Object, string Site, int Index)>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var next))
        {
            var (value, nested) = next;
            if (value is string or JsonElement)
                continue;
            var type = value.GetType();
            if (IsWalked(type))
            {
                if ((nested && plan.IsListed(type)) || !seen.Add(value))
                    continue;
                if (value is RAnnotated annotated && plan.Wants(annotated.Kind))
                    found.Add((annotated, SiteOf(annotated, model.Source), found.Count));
                foreach (var getter in plan.Follow(type))
                {
                    if (getter(value) is { } member)
                        pending.Push((member, true));
                }

                continue;
            }

            if (value is IDictionary dictionary)
            {
                foreach (var item in dictionary.Values)
                {
                    if (item is not null)
                        pending.Push((item, nested));
                }
            }
            else if (value is IEnumerable items)
            {
                foreach (var item in items)
                {
                    if (item is null)
                        continue;
                    var itemType = item.GetType();
                    if (itemType.IsGenericType && itemType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                    {
                        if (PairValue(itemType)(item) is { } entry)
                            pending.Push((entry, nested));
                    }
                    else
                    {
                        pending.Push((item, nested));
                    }
                }
            }
        }

        // List.Sort is unstable: two objects with one (kind, site) keep their discovery order, so the order never varies.
        found.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.Object.Kind, b.Object.Kind);
            if (c == 0)
                c = string.CompareOrdinal(a.Site, b.Site);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        return [.. found.Select(f => f.Object)];
    }

    private static readonly ConcurrentDictionary<string, Plan> Plans = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Type, Func<object, object?>> PairValues = new();

    private static Func<object, object?> PairValue(Type pair) => PairValues.GetOrAdd(pair, static t =>
    {
        var parameter = Expression.Parameter(typeof(object), "o");
        var body = Expression.Convert(Expression.Property(Expression.Convert(parameter, t), "Value"), typeof(object));
        return Expression.Lambda<Func<object, object?>>(body, parameter).Compile();
    });

    /// <summary>
    /// The members a walk for a set of kinds follows, decided once per set from the static types of the members scripts see. The
    /// types are the walked resolution types; an edge goes from a type to each concrete walked type one of its members may hold
    /// (through lists, dictionaries and base types), never back to the model. Edges into a listed type (the item type of a model list, such as
    /// <see cref="REntity"/>) leave only the model itself, since the model's list holds every element of that type. A type leads to
    /// the kinds when it is one of their types or an edge reaches a type that does; a walk follows a member only when it reaches such a
    /// type, so a walk for entities reads the entity list alone.
    /// </summary>
    private sealed class Plan
    {
        private static readonly Type[] Concrete =
        [
            .. typeof(RAnnotated).Assembly.GetTypes().Where(t => !t.IsAbstract && IsWalked(t) && t != typeof(ResolvedModel)).OrderBy(t => t.FullName, StringComparer.Ordinal),
        ];

        private static readonly FrozenSet<Type> Listed = GettersOf(typeof(ResolvedModel))
            .SelectMany(m => TargetsOf(m.Property.PropertyType)).ToFrozenSet();

        private readonly FrozenSet<string> _kinds;
        private readonly FrozenDictionary<Type, Func<object, object?>[]> _follow;

        public Plan(string[] kinds)
        {
            _kinds = kinds.ToFrozenSet(StringComparer.Ordinal);
            var edges = new Dictionary<Type, List<(Func<object, object?> Get, Type[] Targets)>>();
            var queue = new Queue<Type>([typeof(ResolvedModel)]);
            while (queue.TryDequeue(out var type))
            {
                if (edges.ContainsKey(type))
                    continue;
                var members = new List<(Func<object, object?>, Type[])>();
                foreach (var (property, get) in GettersOf(type))
                {
                    Type[] targets = [.. TargetsOf(property.PropertyType).Where(t => type == typeof(ResolvedModel) || !Listed.Contains(t))];
                    members.Add((get, targets));
                    foreach (var target in targets)
                        queue.Enqueue(target);
                }

                edges[type] = members;
            }

            var leads = edges.Keys.Where(t => typeof(RAnnotated).IsAssignableFrom(t)
                && _kinds.Contains(((RAnnotated)RuntimeHelpers.GetUninitializedObject(t)).Kind)).ToHashSet();
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var (type, members) in edges)
                {
                    if (!leads.Contains(type) && members.Any(m => m.Targets.Any(leads.Contains)))
                        changed = leads.Add(type);
                }
            }

            _follow = edges.ToFrozenDictionary(e => e.Key, e => e.Value.Where(m => m.Targets.Any(leads.Contains)).Select(m => m.Get).ToArray());
        }

        public bool Wants(string kind) => _kinds.Contains(kind);

        public bool IsListed(Type type) => Listed.Contains(type);

        public Func<object, object?>[] Follow(Type type) => _follow.TryGetValue(type, out var getters) ? getters : [];

        /// <summary>The concrete walked types a value of a static type may be or hold.</summary>
        private static IEnumerable<Type> TargetsOf(Type type)
        {
            if (type == typeof(string) || type == typeof(RCalculated))
                return [];
            if (type.IsValueType)
                return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) ? TargetsOf(type.GetGenericArguments()[1]) : [];
            if (IsWalked(type) || type.IsAssignableFrom(typeof(RAnnotated)))
                return Concrete.Where(type.IsAssignableFrom);
            return type.GetInterfaces().Prepend(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } enumerable
                ? TargetsOf(enumerable.GetGenericArguments()[0])
                : [];
        }
    }

    private static readonly ConcurrentDictionary<Type, (PropertyInfo Property, Func<object, object?> Get)[]> Getters = new();
    private static readonly ConcurrentDictionary<Type, bool> Holders = new();

    /// <summary>The resolution types the walk enters: the model and the objects and helper objects scripts see as proxies.</summary>
    private static bool IsWalked(Type type) =>
        type == typeof(ResolvedModel) || (type.IsClass && !type.IsGenericType && string.Equals(type.Namespace, typeof(RAnnotated).Namespace, StringComparison.Ordinal)
            && !typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(RCalculated));

    /// <summary>The compiled getters of a walked type's public members that may hold an annotated object, with their properties.</summary>
    private static (PropertyInfo Property, Func<object, object?> Get)[] GettersOf(Type type) => Getters.GetOrAdd(type, static t =>
    [
        .. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true }
                && p.Name is not (nameof(IResolvedObject.Dependencies) or nameof(ResolvedModel.Source) or nameof(ResolvedModel.Settings)
                    or nameof(ResolvedModel.Diagnostics) or nameof(ResolvedModel.SeedsInOrder) or nameof(RAnnotated.Calculated))
                && MayHold(p.PropertyType))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p =>
            {
                var parameter = Expression.Parameter(typeof(object), "o");
                var body = Expression.Convert(Expression.Property(Expression.Convert(parameter, t), p), typeof(object));
                return (p, Expression.Lambda<Func<object, object?>>(body, parameter).Compile());
            }),
    ]);

    /// <summary>Whether a value of a static type may be or hold an annotated object; memoized per type.</summary>
    private static bool MayHold(Type type) => Holders.GetOrAdd(type, static t => MayHold(t, []));

    /// <summary>
    /// Whether a value of a static type may be or hold an annotated object, not counting the types on the path being decided (which
    /// their other members decide); only the top-level answer is memoized, since an inner one may depend on the path.
    /// </summary>
    private static bool MayHold(Type type, HashSet<Type> visiting)
    {
        bool result;
        if (type == typeof(string) || type.IsValueType || type == typeof(RCalculated))
            result = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) && MayHold(type.GetGenericArguments()[1], visiting);
        else if (typeof(RAnnotated).IsAssignableFrom(type) || type.IsAssignableFrom(typeof(RAnnotated)))
            result = true;
        else if (type.GetInterfaces().Prepend(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } enumerable)
            result = MayHold(enumerable.GetGenericArguments()[0], visiting);
        else if (IsWalked(type) && visiting.Add(type))
            result = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.GetIndexParameters().Length == 0 && MayHold(p.PropertyType, visiting));
        else
            result = false; // a type on the path being decided: its other members decide
        return result;
    }

    private static FrozenDictionary<string, Type> Discover()
    {
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in typeof(RAnnotated).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsClass || !typeof(RAnnotated).IsAssignableFrom(type))
                continue;
            // Every annotated type's Kind is a constant; reading it needs no constructed object.
            var kind = ((RAnnotated)RuntimeHelpers.GetUninitializedObject(type)).Kind;
            if (!types.TryAdd(kind, type))
                throw new InvalidOperationException($"The resolved types {types[kind].Name} and {type.Name} share the kind '{kind}'.");
        }

        return types.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
