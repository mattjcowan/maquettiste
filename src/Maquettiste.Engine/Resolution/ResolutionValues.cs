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
    /// <summary>The ordinal comparison as one shared delegate (a method group would allocate a delegate per sort).</summary>
    private static readonly Comparison<string> OrdinalOrder = string.CompareOrdinal;

    // Loose keys in a plain array (most sets hold a handful; sorted in place, the array becomes the run when it is full). The first
    // run is held on its own: most sets copy one other set's keys, and a list of runs is made only for the second.
    private string[]? _loose;
    private int _looseCount;
    private string[]? _firstRun;
    private List<string[]>? _runs;
    private FrozenKeys? _frozen;

    /// <summary>Adds a key.</summary>
    public DependencySet Add(string key)
    {
        if (_loose is null)
            _loose = new string[4];
        else if (_looseCount == _loose.Length)
            Array.Resize(ref _loose, _loose.Length * 2);
        _loose[_looseCount++] = key;
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
                    AddRun(frozen.Items);
                break;
            case IReadOnlyList<string> list:
                if (list.Count == 0)
                    return this;
                var needed = _looseCount + list.Count;
                if (_loose is null)
                    _loose = new string[Math.Max(4, list.Count)];
                else if (needed > _loose.Length)
                    Array.Resize(ref _loose, Math.Max(needed, _loose.Length * 2));
                for (var i = 0; i < list.Count; i++)
                    _loose[_looseCount++] = list[i];
                break;
            default:
                foreach (var key in keys)
                    Add(key);
                break;
        }

        _frozen = null;
        return this;
    }

    private void AddRun(string[] run)
    {
        if (_firstRun is null && _runs is null)
        {
            _firstRun = run;
            return;
        }

        if (_runs is null)
        {
            _runs = new List<string[]>(4) { _firstRun! };
            _firstRun = null;
        }

        _runs.Add(run);
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

        string[]? looseRun = null;
        if (_looseCount > 0)
        {
            // The loose keys, sorted and distinct in place; a full array becomes the run itself.
            var loose = _loose.AsSpan(0, _looseCount);
            loose.Sort(OrdinalOrder);
            var write = 0;
            for (var read = 0; read < loose.Length; read++)
            {
                if (write == 0 || !string.Equals(loose[write - 1], loose[read], StringComparison.Ordinal))
                    loose[write++] = loose[read];
            }

            looseRun = write == _loose!.Length ? _loose : loose[..write].ToArray();
            _loose = null;
            _looseCount = 0;
        }

        if (_runs is null)
        {
            // At most two runs: the loose keys and one copied set.
            var single = _firstRun is null ? looseRun ?? [] : looseRun is null ? _firstRun : Merge(_firstRun, looseRun);
            _firstRun = single.Length > 0 ? single : null;
            _frozen = new FrozenKeys(single);
            return _frozen;
        }

        var runs = _runs;
        if (looseRun is not null)
            runs.Add(looseRun);
        string[] merged;
        if (runs.Count > 32)
        {
            // Very many runs (a database's membership gathers every table's keys): the largest run stays as it is, sorted, and only the
            // distinct keys outside it are collected, sorted and merged in (sorting every key again cost tens of milliseconds a database
            // when the largest run holds every conceptual file's key).
            var largest = runs[0];
            foreach (var run in runs)
            {
                if (run.Length > largest.Length)
                    largest = run;
            }

            var inLargest = new HashSet<string>(largest, StringComparer.Ordinal);
            var extra = new HashSet<string>(StringComparer.Ordinal);
            foreach (var run in runs)
            {
                if (ReferenceEquals(run, largest))
                    continue;
                foreach (var key in run)
                {
                    if (!inLargest.Contains(key))
                        extra.Add(key);
                }
            }

            var extras = extra.ToArray();
            Array.Sort(extras, OrdinalOrder);
            merged = Merge(largest, extras);
        }
        else
        {
            // The same run added twice is merged once.
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
            merged = runs.Count switch
            {
                0 => [],
                1 => runs[0],
                2 => Merge(runs[0], runs[1]),
                _ => MergeAll(runs),
            };
        }

        // Later additions merge onto the result, which stays as the only run.
        _runs = null;
        _firstRun = merged.Length > 0 ? merged : null;
        _frozen = new FrozenKeys(merged);
        return _frozen;
    }

    /// <summary>
    /// Merges two sorted, distinct arrays into one; returns an input unchanged when the other adds nothing. The union is counted first, so
    /// the result is allocated once at its size, and not at all when one input holds the other.
    /// </summary>
    private static string[] Merge(string[] a, string[] b)
    {
        if (ReferenceEquals(a, b) || b.Length == 0)
            return a;
        if (a.Length == 0)
            return b;
        int i = 0, j = 0, n = 0;
        while (i < a.Length && j < b.Length)
        {
            var c = string.CompareOrdinal(a[i], b[j]);
            if (c <= 0)
                i++;
            if (c >= 0)
                j++;
            n++;
        }

        n += a.Length - i + (b.Length - j);
        if (n == a.Length)
            return a; // b was a subset of a
        if (n == b.Length)
            return b;
        var result = new string[n];
        i = j = n = 0;
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
        return result;
    }

    /// <summary>
    /// Merges three to 32 sorted, distinct, different arrays into one, all at once (merging pairwise made an array per pair and level).
    /// The union is gathered in a pooled buffer; when it is as long as one of the inputs, it is that input, which is returned.
    /// </summary>
    private static string[] MergeAll(List<string[]> runs)
    {
        var total = 0;
        foreach (var run in runs)
            total += run.Length;
        var buffer = System.Buffers.ArrayPool<string>.Shared.Rent(total);
        Span<int> heads = stackalloc int[runs.Count];
        var n = 0;
        while (true)
        {
            string? min = null;
            for (var k = 0; k < runs.Count; k++)
            {
                var run = runs[k];
                if (heads[k] < run.Length && (min is null || string.CompareOrdinal(run[heads[k]], min) < 0))
                    min = run[heads[k]];
            }

            if (min is null)
                break;
            buffer[n++] = min;
            for (var k = 0; k < runs.Count; k++)
            {
                var run = runs[k];
                if (heads[k] < run.Length && string.Equals(run[heads[k]], min, StringComparison.Ordinal))
                    heads[k]++;
            }
        }

        string[]? result = null;
        foreach (var run in runs)
        {
            if (run.Length == n)
            {
                result = run;
                break;
            }
        }

        result ??= buffer.AsSpan(0, n).ToArray();
        System.Buffers.ArrayPool<string>.Shared.Return(buffer, clearArray: true);
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

    /// <summary>The number of keys held.</summary>
    public int Count => _elements.Count + _referrers.Count;
}
