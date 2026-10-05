using System.Collections;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// A preview's schema diffs (generation-ui.md section 5.2, "Bounds"): one entry per database of the resolved model, each diffed against
/// its committed snapshot on first read and kept. A template that never reads <c>schema_diff</c> (a table's DDL) costs no diff; one
/// that reads one database's diff costs that database's only. A generation run diffs every database up front instead
/// (<see cref="GenerationRun"/>), since it renders every unit.
/// </summary>
internal sealed class LazySchemaDiffs : IReadOnlyDictionary<string, SchemaDiffResult>
{
    private readonly SortedDictionary<string, Lazy<SchemaDiffResult>> _diffs = new(StringComparer.Ordinal);

    /// <summary>Creates the diffs of every database of <paramref name="resolved"/>; nothing is read until an entry is.</summary>
    /// <param name="resolved">The resolved model.</param>
    /// <param name="services">The services (the snapshot store and the differ).</param>
    public LazySchemaDiffs(ResolvedModel resolved, EngineServices services)
    {
        foreach (var database in resolved.Databases)
            _diffs[database.Name] = new Lazy<SchemaDiffResult>(() => Diff(database, services), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>How many databases have been diffed (tests).</summary>
    internal int Computed => _diffs.Values.Count(d => d.IsValueCreated);

    /// <summary>The entries in key order with values computed on first read, for the template's <c>schema_diff</c> map.</summary>
    internal IReadOnlyList<KeyValuePair<string, object?>> LazyEntries() =>
        [.. _diffs.Select(p => KeyValuePair.Create(p.Key, (object?)new Lazy<object?>(() => p.Value.Value, LazyThreadSafetyMode.ExecutionAndPublication)))];

    private static SchemaDiffResult Diff(RDatabase database, EngineServices services)
    {
        // The diff a run computes (GenerationRun.SchemaDiffsAsync), without the capture a run keeps for saving; the read is synchronous
        // because a template asks for the value synchronously, on the render's own thread.
        var previous = services.Snapshots.LoadAsync(database.Name, CancellationToken.None).GetAwaiter().GetResult();
        previous = SnapshotAliases.Normalize(previous, database);
        return services.SchemaDiffer.Diff(previous, database);
    }

    /// <inheritdoc/>
    public SchemaDiffResult this[string key] => _diffs[key].Value;

    /// <inheritdoc/>
    public IEnumerable<string> Keys => _diffs.Keys;

    /// <inheritdoc/>
    public IEnumerable<SchemaDiffResult> Values => _diffs.Values.Select(d => d.Value);

    /// <inheritdoc/>
    public int Count => _diffs.Count;

    /// <inheritdoc/>
    public bool ContainsKey(string key) => _diffs.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out SchemaDiffResult value)
    {
        if (_diffs.TryGetValue(key, out var lazy))
        {
            value = lazy.Value;
            return true;
        }

        value = null!;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, SchemaDiffResult>> GetEnumerator() =>
        _diffs.Select(p => KeyValuePair.Create(p.Key, p.Value.Value)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The dependency hasher of a preview: a preview reports files, diagnostics and read keys, never an input hash, so no key is hashed
/// (a run's hasher hashes each key a unit reads, which for a kind-wide key reads every element of the kind).
/// </summary>
internal sealed class PreviewHasher : IDependencyHasher
{
    /// <summary>The one instance.</summary>
    public static PreviewHasher Instance { get; } = new();

    /// <inheritdoc/>
    public string CurrentHash(string dependencyKey) => "";

    /// <inheritdoc/>
    public string InputHash(string staticHash, IReadOnlyList<string> sortedReadKeys) => "";
}
