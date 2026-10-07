using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>Conversions from model values to the plain CLR values and kebab strings templates see.</summary>
internal static class ResolutionValues
{
    /// <summary>A JSON value as a plain CLR value: string, <see cref="long"/> (integral) or <see cref="double"/>, bool, null, list, map.</summary>
    public static object? Plain(JsonElement? value) => value is { } v ? Plain(v) : null;

    /// <summary>A JSON value as a plain CLR value.</summary>
    public static object? Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => value.EnumerateArray().Select(Plain).ToImmutableArray(),
        JsonValueKind.Object => value.EnumerateObject()
            .ToImmutableSortedDictionary(p => p.Name, p => Plain(p.Value), StringComparer.Ordinal),
        _ => null,
    };

    /// <summary>
    /// A storage parameter's value as a dialect writes it: a number as written, a boolean as <c>ON</c>/<c>OFF</c> on SQL Server,
    /// <c>1</c>/<c>0</c> on MySQL and <c>true</c>/<c>false</c> elsewhere, a string as it is.
    /// </summary>
    public static string StorageValue(JsonElement value, string dialect) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => (value.ValueKind == JsonValueKind.True, dialect) switch
        {
            (var on, "sqlserver") => on ? "ON" : "OFF",
            (var on, "mysql") => on ? "1" : "0",
            (var on, _) => on ? "true" : "false",
        },
        JsonValueKind.String => value.GetString() ?? "",
        _ => value.GetRawText(),
    };

    /// <summary>A map of JSON values as plain values, ordinal by key.</summary>
    public static IReadOnlyDictionary<string, object?> PlainMap(IEnumerable<KeyValuePair<string, JsonElement>> values) =>
        values.ToImmutableSortedDictionary(p => p.Key, p => Plain(p.Value), StringComparer.Ordinal);

    /// <summary>The kebab JSON name of an enum value (its <c>JsonStringEnumMemberName</c>).</summary>
    public static string Kebab<T>(T value) where T : struct, Enum =>
        KebabNames<T>.Names.TryGetValue(value, out var name) ? name : Serialize(value);

    private static string Serialize<T>(T value) where T : struct, Enum => JsonSerializer.Serialize(value, EngineJson.Options).Trim('"');

    /// <summary>The kebab names of an enum's declared values, computed once per enum type (an immutable table, like a constant).</summary>
    private static class KebabNames<T> where T : struct, Enum
    {
        public static readonly System.Collections.Frozen.FrozenDictionary<T, string> Names =
            System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(Enum.GetValues<T>().Distinct(), v => v, Serialize);
    }
}

/// <summary>
/// A sorted, distinct set of dependency keys (engine-design.md section 11). Keys are appended cheaply and sorted (ordinal) and
/// de-duplicated when read; the result is cached until the next change.
/// </summary>
/// <remarks>
/// Most sets are unions of other sets' frozen lists (an attribute's keys include its entity's, a table's its entity's, a navigation
/// its relation's and both end entities'). Those arrive as <see cref="FrozenKeys"/>, already sorted and distinct, and are kept as
/// runs that <see cref="ToList"/> merges linearly; only loose keys are sorted. A set whose content is exactly one run returns that
/// run itself, so equal lists are shared rather than copied. The result is the same ordinal, distinct list either way.
/// </remarks>
internal sealed class DependencySet(DependencyKeyCache? cache = null)
{
    private List<string>? _loose;
    private List<string[]>? _runs;
    private FrozenKeys? _frozen;

    /// <summary>Adds a key.</summary>
    public DependencySet Add(string key)
    {
        (_loose ??= new List<string>(8)).Add(key);
        _frozen = null;
        return this;
    }

    /// <summary>Adds keys.</summary>
    public DependencySet AddRange(IEnumerable<string> keys)
    {
        switch (keys)
        {
            case FrozenKeys frozen:
                if (frozen.Items.Length > 0)
                    (_runs ??= new List<string[]>(4)).Add(frozen.Items);
                break;
            case IReadOnlyList<string> list:
                if (list.Count == 0)
                    return this;
                var loose = _loose ??= new List<string>(Math.Max(8, list.Count));
                for (var i = 0; i < list.Count; i++)
                    loose.Add(list[i]);
                break;
            default:
                (_loose ??= new List<string>(8)).AddRange(keys);
                break;
        }

        _frozen = null;
        return this;
    }

    /// <summary>Adds <c>e:&lt;id&gt;</c>.</summary>
    public DependencySet Element(string id) => Add(cache is null ? "e:" + id : cache.Element(id));

    /// <summary>Adds <c>r:&lt;id&gt;</c>.</summary>
    public DependencySet Referrers(string id) => Add(cache is null ? "r:" + id : cache.Referrers(id));

    /// <summary>The keys, ordinal and distinct; the same list until the set changes.</summary>
    public IReadOnlyList<string> ToList()
    {
        if (_frozen is not null)
            return _frozen;

        var runs = _runs ??= new List<string[]>(4);
        if (runs.Count > 32)
        {
            // Very many runs (a database's membership gathers every table's keys): collect the distinct keys and sort them once.
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var run in runs)
                distinct.UnionWith(run);
            if (_loose is not null)
            {
                distinct.UnionWith(_loose);
                _loose.Clear();
            }

            var all = distinct.ToArray();
            Array.Sort(all, StringComparer.Ordinal);
            runs.Clear();
            if (all.Length > 0)
                runs.Add(all);
            _frozen = new FrozenKeys(all);
            return _frozen;
        }

        if (_loose is { Count: > 0 } loose)
        {
            loose.Sort(StringComparer.Ordinal);
            var write = 0;
            for (var read = 0; read < loose.Count; read++)
            {
                if (write == 0 || !string.Equals(loose[write - 1], loose[read], StringComparison.Ordinal))
                    loose[write++] = loose[read];
            }

            loose.RemoveRange(write, loose.Count - write);
            runs.Add([.. loose]);
            loose.Clear();
        }

        // Merge the sorted runs pairwise, level by level (a set can gather dozens: a table with many foreign keys takes each relation's
        // keys), so each key is compared about log2(runs) times; the same run added twice is merged once.
        if (runs.Count > 2)
        {
            var kept = 0;
            for (var i = 0; i < runs.Count; i++)
            {
                var seen = false;
                for (var j = 0; j < kept && !seen; j++)
                    seen = ReferenceEquals(runs[j], runs[i]);
                if (!seen)
                    runs[kept++] = runs[i];
            }

            runs.RemoveRange(kept, runs.Count - kept);
        }

        while (runs.Count > 1)
        {
            var next = 0;
            for (var i = 0; i < runs.Count; i += 2)
                runs[next++] = i + 1 < runs.Count ? Merge(runs[i], runs[i + 1]) : runs[i];
            runs.RemoveRange(next, runs.Count - next);
        }

        // Later additions merge onto the result, which stays as the only run.
        var merged = runs.Count == 1 ? runs[0] : [];
        _frozen = new FrozenKeys(merged);
        return _frozen;
    }

    /// <summary>Merges two sorted, distinct arrays into one; returns an input unchanged when the other adds nothing.</summary>
    private static string[] Merge(string[] a, string[] b)
    {
        if (ReferenceEquals(a, b) || b.Length == 0)
            return a;
        if (a.Length == 0)
            return b;
        var result = new string[a.Length + b.Length];
        int i = 0, j = 0, n = 0;
        while (i < a.Length && j < b.Length)
        {
            var c = string.CompareOrdinal(a[i], b[j]);
            if (c < 0)
                result[n++] = a[i++];
            else if (c > 0)
                result[n++] = b[j++];
            else
            {
                result[n++] = a[i++];
                j++;
            }
        }

        while (i < a.Length)
            result[n++] = a[i++];
        while (j < b.Length)
            result[n++] = b[j++];
        if (n == a.Length)
            return a; // b was a subset of a
        if (n == b.Length)
            return b;
        Array.Resize(ref result, n);
        return result;
    }
}

/// <summary>
/// A frozen dependency key list: sorted (ordinal), distinct and never changed, so sets and lists can share it instead of copying.
/// </summary>
internal sealed class FrozenKeys : System.Collections.ObjectModel.ReadOnlyCollection<string>
{
    /// <summary>Wraps a sorted, distinct array that nothing will change.</summary>
    /// <param name="items">The keys.</param>
    public FrozenKeys(string[] items)
        : base(items) => Items = items;

    /// <summary>The keys.</summary>
    public new string[] Items { get; }
}

/// <summary>
/// One string instance per <c>e:</c> and <c>r:</c> key for a run, so dependency lists share strings instead of copying them.
/// Thread-safe (the resolver's parallel phases share it); which instance wins a race does not matter, only the text does.
/// </summary>
internal sealed class DependencyKeyCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _elements = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _referrers = new(StringComparer.Ordinal);

    /// <summary><c>e:&lt;id&gt;</c>.</summary>
    public string Element(string id) => _elements.TryGetValue(id, out var key) ? key : _elements.GetOrAdd(id, static id => "e:" + id);

    /// <summary><c>r:&lt;id&gt;</c>.</summary>
    public string Referrers(string id) => _referrers.TryGetValue(id, out var key) ? key : _referrers.GetOrAdd(id, static id => "r:" + id);
}
