using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>Where an entry came from.</summary>
internal enum ManifestBucket
{
    /// <summary>The pack's manifest, <c>&lt;ModelRoot&gt;/manifest/&lt;pack&gt;.json</c> (or, read once, a cache copy of an earlier release).</summary>
    Manifest,

    /// <summary>
    /// An entry that only an unfinished journal knows about: the writer checks it through the path policy when the pack's manifest is
    /// next saved.
    /// </summary>
    Journal,
}

/// <summary>One pack's entries, by path.</summary>
/// <param name="Entries">Path → (entry, bucket).</param>
internal sealed record PackManifestData(ImmutableSortedDictionary<string, (ManifestEntry Entry, ManifestBucket Bucket)> Entries)
{
    /// <summary>An empty pack.</summary>
    public static PackManifestData Empty { get; } =
        new(ImmutableSortedDictionary.Create<string, (ManifestEntry, ManifestBucket)>(StringComparer.Ordinal));
}

/// <summary>
/// The immutable data behind <see cref="ManifestSet"/>: every pack's entries, plus entries overlaid from an unfinished journal
/// (engine-design.md sections 12.2 and 12.4).
/// </summary>
internal sealed class ManifestSetData
{
    private readonly ImmutableSortedDictionary<string, PackManifestData> _packs;
    private readonly Dictionary<string, (ManifestEntry Entry, string Pack)> _byPath;
    private readonly string[] _packNames;

    /// <summary>Creates the data.</summary>
    /// <param name="packs">Pack name → entries.</param>
    public ManifestSetData(ImmutableSortedDictionary<string, PackManifestData> packs)
    {
        _packs = packs.WithComparers(StringComparer.Ordinal);
        _packNames = [.. _packs.Where(p => !p.Value.Entries.IsEmpty).Select(p => p.Key)];
        _byPath = new Dictionary<string, (ManifestEntry, string)>(StringComparer.Ordinal);
        // Packs in ordinal order, so a path listed by two packs resolves to the ordinally first one.
        foreach (var (pack, data) in _packs)
        {
            foreach (var (path, value) in data.Entries)
                _byPath.TryAdd(path, (value.Entry, pack));
        }
    }

    /// <summary>No manifests.</summary>
    public static ManifestSetData Empty { get; } = new(ImmutableSortedDictionary.Create<string, PackManifestData>(StringComparer.Ordinal));

    /// <summary>Packs with any entry, ordinal.</summary>
    public IReadOnlyCollection<string> Packs => _packNames;

    /// <summary>Finds a path's entry.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="pack">The owning pack.</param>
    /// <returns>Whether found.</returns>
    public bool TryGet(string path, [MaybeNullWhen(false)] out ManifestEntry entry, [MaybeNullWhen(false)] out string pack)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_byPath.TryGetValue(path, out var found))
        {
            entry = found.Entry;
            pack = found.Pack;
            return true;
        }

        entry = null;
        pack = null;
        return false;
    }

    /// <summary>A pack's entries, ordinal by path. Journal-only entries are not listed.</summary>
    /// <param name="pack">The pack.</param>
    /// <returns>The entries.</returns>
    public IReadOnlyList<ManifestEntry> Entries(string pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!_packs.TryGetValue(pack, out var data))
            return [];
        return [.. data.Entries.Values.Where(v => v.Bucket == ManifestBucket.Manifest).Select(v => v.Entry)];
    }

    /// <summary>Every entry of a pack with its bucket, ordinal by path.</summary>
    /// <param name="pack">The pack.</param>
    /// <returns>The entries.</returns>
    public PackManifestData Pack(string pack) => _packs.TryGetValue(pack, out var data) ? data : PackManifestData.Empty;

    /// <summary>
    /// Applies the <c>write</c> and <c>delete</c> records of packs that have no <c>pack</c> line: a write replaces the path's entry in
    /// the pack (keeping its bucket, or <see cref="ManifestBucket.Journal"/> for a new path), a delete removes it.
    /// </summary>
    /// <param name="records">The journal records, in file order.</param>
    /// <returns>The new data.</returns>
    public ManifestSetData WithJournalOverlay(IReadOnlyList<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.Type == JournalTypes.Pack && record.Pack is not null)
                completed.Add(record.Pack);
        }

        var packs = _packs.ToBuilder();
        foreach (var record in records)
        {
            if (record.Pack is null || record.Path is null || completed.Contains(record.Pack))
                continue;
            var data = packs.TryGetValue(record.Pack, out var existing) ? existing : PackManifestData.Empty;
            if (record.Type == JournalTypes.Write && record.Hash is not null && record.Unit is not null)
            {
                var bucket = data.Entries.TryGetValue(record.Path, out var old) ? old.Bucket : ManifestBucket.Journal;
                data = new PackManifestData(data.Entries.SetItem(record.Path, (new ManifestEntry(record.Path, record.Hash, record.Unit), bucket)));
            }
            else if (record.Type == JournalTypes.Delete)
            {
                data = new PackManifestData(data.Entries.Remove(record.Path));
            }
            else
            {
                continue;
            }

            packs[record.Pack] = data;
        }

        return new ManifestSetData(packs.ToImmutable());
    }
}

/// <summary>The journal line types (JSON <c>t</c>).</summary>
internal static class JournalTypes
{
    /// <summary>Run start.</summary>
    public const string Begin = "begin";

    /// <summary>A file written.</summary>
    public const string Write = "write";

    /// <summary>A file deleted.</summary>
    public const string Delete = "delete";

    /// <summary>A pack's manifest and unit state saved.</summary>
    public const string Pack = "pack";

    /// <summary>Run end.</summary>
    public const string End = "end";
}
