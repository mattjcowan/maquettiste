using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Current hashes of the read keys a script rule records (next-phase-1-design.md section 5.7), for one snapshot (<see cref="Of"/>);
/// computed on first use and cached, so it is safe to use from the parallel workers of a validation. Two calls of a rule that read
/// keys with equal hashes saw the same values:
/// <list type="bullet">
/// <item><c>e:&lt;id&gt;</c> (<c>get</c>, and every document a list returns): the hash of the file holding the id, or absent.</item>
/// <item><c>k:&lt;kind&gt;</c> (<c>all</c>): the hash of the kind's sorted ids.</item>
/// <item><c>ix:&lt;index&gt;:&lt;value&gt;</c> (<c>withStereotype</c>, <c>withTag</c>, <c>withProperty</c>, and <c>ix:qn:&lt;canonical
/// name&gt;</c> for <c>byQualifiedName</c>): the hash of the sorted ids the lookup returns.</item>
/// <item><c>rt:&lt;id&gt;</c> (<c>referencesTo</c>): the hash of every reference entry to the id, in order, with the fields the rule sees;
/// unlike <c>r:</c> it covers the id's own file and diagrams.</item>
/// </list>
/// </summary>
internal sealed class RuleKeyHasher
{
    /// <summary>The hash of a key that does not resolve.</summary>
    public const string Absent = "absent";

    private static readonly ConditionalWeakTable<ModelSnapshot, RuleKeyHasher> Hashers = new();

    private readonly ModelSnapshot _model;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    private RuleKeyHasher(ModelSnapshot model) => _model = model;

    /// <summary>The hasher of a snapshot, created on first use and kept as long as the snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The hasher.</returns>
    public static RuleKeyHasher Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Hashers.GetValue(model, static m => new RuleKeyHasher(m));
    }

    /// <summary>The current hash of a read key; <see cref="Absent"/> for a key this hasher does not know.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The hash.</returns>
    public string CurrentHash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _cache.TryGetValue(key, out var hash) ? hash : _cache.GetOrAdd(key, static (k, self) => self.Compute(k), this);
    }

    private string Compute(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return Absent;
        var rest = key[(colon + 1)..];
        return key[..colon] switch
        {
            "e" => _model.GetDocument(rest)?.Hash ?? Absent,
            "k" => KindInfo.TryGet(rest, out var info) ? _model.KindSetHash(info.Kind) : Absent,
            "ix" => RuleModelIndex.Of(_model).SortedIds(rest) is { } ids ? HashBuilder.Of(["ix", rest, .. ids]) : Absent,
            "rt" => ReferencesHash(rest),
            _ => Absent,
        };
    }

    private string ReferencesHash(string id)
    {
        using var hash = new HashBuilder();
        hash.Add("rt").Add(id);
        foreach (var r in _model.ReferencesTo(id))
            hash.Add(r.FromElementId).Add(r.FromId).Add(r.JsonPointer).Add(r.Field).Add(r.ToId);
        return hash.Finish();
    }
}
