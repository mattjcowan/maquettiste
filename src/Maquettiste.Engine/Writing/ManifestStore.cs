using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// Per-pack manifests (W7; engine-design.md section 12.2): one file per pack at <c>&lt;ModelRoot&gt;/manifest/&lt;pack&gt;.json</c>, for
/// every output root. The file is canonical JSON except that each <c>[path, hash, unit]</c> entry sits on one line, sorted by path
/// in ordinal UTF-8 order. Releases before 0.5.5 kept the manifests of roots not marked <c>commit</c> under
/// <c>&lt;JournalDirectory&gt;/manifest/</c>: such a copy is read beside the pack's manifest (the manifest's entry wins a path both
/// list), and the pack's next save writes the merged entries to the model folder and deletes the copy.
/// </summary>
/// <remarks>
/// The store keeps, per pack, what it last read or wrote: the files' stats (length and last write time), the entries and whether the
/// manifest's bytes are known to be exactly what <see cref="Format"/> gives for them. A load whose files still have those stats returns
/// the kept entries without reading or parsing (a watch or editor host runs one store for many runs); another process, a hand edit or a
/// branch switch changes the stat, and the file is read again (the loader trusts model files' stats the same way). A save compares the
/// new entries with the kept ones: equal entries over a manifest known to be canonical mean equal bytes, so nothing is formatted or
/// written; otherwise the kept entries are patched with the changes. Only when the store holds nothing it can trust does a save read
/// the file to compare, as before.
/// </remarks>
/// <param name="options">The engine options.</param>
/// <param name="json">The canonical writer (for the document head and its <c>$schema</c>).</param>
/// <param name="paths">The engine-write guard (<see cref="WriteTarget.Model"/> for manifests, <see cref="WriteTarget.Cache"/> to delete a cache copy).</param>
internal sealed class ManifestStore(EngineOptions options, ICanonicalJson json, IOutputPathPolicy paths) : IManifestStore
{
    private const string SchemaFile = "manifest.json";

    private static readonly JsonWriterOptions TokenOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    private int _tempCounter;
    private int _fileReads;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Kept> _kept = new(StringComparer.Ordinal);
    private readonly Lock _lastSetGate = new();
    private (string[] Names, ManifestSet Set)? _lastSet;

    /// <summary>How many manifest files this store has read from disk (tests: an unchanged file is not read again).</summary>
    internal int FileReads => Volatile.Read(ref _fileReads);

    /// <summary>A file's stat: whether it exists, its length and its last write time.</summary>
    private readonly record struct Stamp(bool Exists, long Length, long Ticks)
    {
        public static Stamp Of(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? new Stamp(true, info.Length, info.LastWriteTimeUtc.Ticks) : default;
        }
    }

    /// <summary>What the store last read or wrote for a pack.</summary>
    /// <param name="Main">The manifest's stat then.</param>
    /// <param name="Legacy">The cache copy's stat then.</param>
    /// <param name="Data">The merged entries, or <see langword="null"/> for none.</param>
    /// <param name="Canonical">Whether the manifest's bytes are exactly <see cref="Format"/> of <paramref name="Data"/> (the store wrote or
    /// confirmed them) and no cache copy exists.</param>
    private sealed record Kept(Stamp Main, Stamp Legacy, PackManifestData? Data, bool Canonical);

    /// <summary>The folder of the manifests.</summary>
    internal string Folder => Path.Combine(options.EffectiveModelRoot, "manifest");

    /// <summary>The folder where releases before 0.5.5 kept the manifests of roots not marked <c>commit</c>.</summary>
    internal string LegacyFolder => Path.Combine(options.EffectiveJournalDirectory, "manifest");

    /// <summary>The file of a pack's manifest.</summary>
    /// <param name="pack">The pack.</param>
    /// <returns>The absolute path.</returns>
    internal string FileOf(string pack) => Path.Combine(Folder, pack + ".json");

    /// <summary>The cache copy an earlier release may have left for a pack.</summary>
    /// <param name="pack">The pack.</param>
    /// <returns>The absolute path.</returns>
    internal string LegacyFileOf(string pack) => Path.Combine(LegacyFolder, pack + ".json");

    /// <inheritdoc/>
    public async Task<ManifestSet> LoadAsync(IReadOnlyCollection<string> packs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(packs);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (packs.Count == 0)
        {
            foreach (var folder in new[] { Folder, LegacyFolder })
            {
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
                    names.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        else
        {
            foreach (var pack in packs)
                names.Add(pack);
        }

        var result = ImmutableSortedDictionary.CreateBuilder<string, PackManifestData>(StringComparer.Ordinal);
        foreach (var pack in names)
        {
            ct.ThrowIfCancellationRequested();
            if (await PackDataAsync(pack, ct).ConfigureAwait(false) is { } data)
                result[pack] = data;
        }

        // The same packs with the same entries as the last load: the same set, without indexing every path again.
        var nameList = names.ToArray();
        lock (_lastSetGate)
        {
            if (_lastSet is { } last && last.Names.AsSpan().SequenceEqual(nameList) && SameData(last.Set.Data, result))
                return last.Set;
        }

        var set = new ManifestSet(new ManifestSetData(result.ToImmutable()));
        lock (_lastSetGate)
            _lastSet = (nameList, set);
        return set;
    }

    /// <summary>Whether a set holds exactly these packs' entries (the same objects).</summary>
    private static bool SameData(ManifestSetData data, ImmutableSortedDictionary<string, PackManifestData>.Builder packs)
    {
        if (data.PackData.Count != packs.Count)
            return false;
        foreach (var (pack, entries) in packs)
        {
            if (!data.PackData.TryGetValue(pack, out var kept) || !ReferenceEquals(kept, entries))
                return false;
        }

        return true;
    }

    /// <summary>A pack's entries: the kept ones while both files keep their stats, else read and parsed (and kept).</summary>
    private async Task<PackManifestData?> PackDataAsync(string pack, CancellationToken ct)
    {
        var main = FileOf(pack);
        var legacy = LegacyFileOf(pack);
        var mainStamp = Stamp.Of(main);
        var legacyStamp = Stamp.Of(legacy);
        // While a cache copy of an earlier release exists, every load reads and merges both files, exactly as a cold load does.
        if (!legacyStamp.Exists && _kept.TryGetValue(pack, out var kept) && kept.Main == mainStamp && kept.Legacy == legacyStamp)
            return kept.Data;

        // Collected in a hash map (the manifest first, so its entry wins a path a cache copy lists too), then turned into the
        // sorted map in one step, which builds the tree from sorted input instead of rebalancing it once per entry.
        var entries = new Dictionary<string, (ManifestEntry, ManifestBucket)>(StringComparer.Ordinal);
        foreach (var file in new[] { main, legacy })
        {
            var bytes = await AtomicFile.ReadIfExistsAsync(file, ct).ConfigureAwait(false);
            if (bytes is null)
                continue;
            Interlocked.Increment(ref _fileReads);
            foreach (var entry in Parse(bytes))
                entries.TryAdd(entry.Path, (entry, ManifestBucket.Manifest));
        }

        var data = entries.Count > 0 ? new PackManifestData(ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, entries)) : null;

        // Kept only when the stats did not move during the read (a write racing it would leave a stat that does not match the bytes),
        // and only without a cache copy.
        if (!legacyStamp.Exists && Stamp.Of(main) == mainStamp && Stamp.Of(legacy) == legacyStamp)
            _kept[pack] = new Kept(mainStamp, legacyStamp, data, Canonical: false);
        else
            _kept.TryRemove(pack, out _);
        return data;
    }

    /// <inheritdoc/>
    public async Task SavePackAsync(string pack, IReadOnlyList<ManifestEntry> entries, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(pack);
        ArgumentNullException.ThrowIfNull(entries);
        ct.ThrowIfCancellationRequested();
        var file = FileOf(pack);
        var check = paths.CheckEngineWrite(WriteTarget.Model, file);
        if (!check.Allowed)
            throw new UnauthorizedAccessException($"{check.RuleId}: manifest write refused for {file}: {check.Reason}");

        var legacy = LegacyFileOf(pack);
        var trusted = _kept.TryRemove(pack, out var kept) && !File.Exists(legacy) && kept.Main == Stamp.Of(file) && kept.Legacy == Stamp.Of(legacy)
            ? kept : null;
        PackManifestData? data = null;
        if (entries.Count == 0)
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        else
        {
            // The kept entries patched with the changes (a new map when the store holds nothing it can trust); an unchanged set over
            // a manifest whose bytes are known to be canonical needs no formatting and no write. Duplicate paths are refused first,
            // as Format refuses them, since the patch counts on distinct paths.
            RefuseDuplicatePaths(entries);
            data = Patched(trusted?.Data, entries, out var unchanged);
            if (!(unchanged && trusted is { Canonical: true }))
            {
                var bytes = Format(pack, entries);
                var existing = trusted is { Canonical: true } ? null : await AtomicFile.ReadIfExistsAsync(file, ct).ConfigureAwait(false);
                if (existing is null || !existing.AsSpan().SequenceEqual(bytes))
                {
                    var tag = "manifest-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-"
                        + Interlocked.Increment(ref _tempCounter).ToString(CultureInfo.InvariantCulture);
                    await AtomicFile.WriteAsync(file, bytes, tag, ct).ConfigureAwait(false);
                }
            }
        }

        // The cache copy of an earlier release was read with the manifest, so its entries are in this save: it goes now.
        if (File.Exists(legacy) && paths.CheckEngineWrite(WriteTarget.Cache, legacy).Allowed)
            File.Delete(legacy);

        // What is on disk now: the manifest is the canonical form of these entries (written, confirmed, or deleted with none).
        var legacyStamp = Stamp.Of(legacy);
        _kept[pack] = new Kept(Stamp.Of(file), legacyStamp, data, Canonical: !legacyStamp.Exists);
    }

    /// <summary>
    /// Throws when two entries share a path (the writer's entries come from a map keyed by path, and a renamed pack's from a loaded
    /// manifest, so neither has any; this keeps the patch below honest for any caller). Sorted entries, as the writer passes them, are
    /// checked pairwise; others through a set.
    /// </summary>
    /// <exception cref="ArgumentException">Two entries share a path.</exception>
    private static void RefuseDuplicatePaths(IReadOnlyList<ManifestEntry> entries)
    {
        var sorted = true;
        for (var i = 1; i < entries.Count && sorted; i++)
            sorted = string.CompareOrdinal(entries[i - 1].Path, entries[i].Path) < 0;
        if (sorted)
            return;
        var seen = new HashSet<string>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Path))
                throw new ArgumentException($"Duplicate manifest path '{entry.Path}'.", nameof(entries));
        }
    }

    /// <summary>
    /// The entries as a pack's data: <paramref name="kept"/> itself when it holds exactly them (<paramref name="unchanged"/>), else
    /// <paramref name="kept"/> with only the added, changed and removed paths applied, or a new map when nothing is kept.
    /// </summary>
    private static PackManifestData Patched(PackManifestData? kept, IReadOnlyList<ManifestEntry> entries, out bool unchanged)
    {
        unchanged = false;
        if (kept is null)
        {
            var all = new Dictionary<string, (ManifestEntry, ManifestBucket)>(entries.Count, StringComparer.Ordinal);
            foreach (var entry in entries)
                all[entry.Path] = (entry, ManifestBucket.Manifest);
            return new PackManifestData(ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, all));
        }

        List<KeyValuePair<string, (ManifestEntry, ManifestBucket)>>? changed = null;
        var matched = 0;
        foreach (var entry in entries)
        {
            var known = kept.Entries.TryGetValue(entry.Path, out var old);
            if (known)
                matched++;
            if (known && old.Bucket == ManifestBucket.Manifest && old.Entry == entry)
                continue;
            (changed ??= []).Add(new(entry.Path, (entry, ManifestBucket.Manifest)));
        }

        // Every kept path is still listed when the entries matched as many kept paths as there are (the writer's paths are distinct).
        List<string>? removed = null;
        if (matched != kept.Entries.Count)
        {
            var listed = new HashSet<string>(entries.Count, StringComparer.Ordinal);
            foreach (var entry in entries)
                listed.Add(entry.Path);
            removed = [.. kept.Entries.Keys.Where(path => !listed.Contains(path))];
        }

        if (changed is null && removed is null)
        {
            unchanged = true;
            return kept;
        }

        var patched = kept.Entries;
        if (removed is not null)
            patched = patched.RemoveRange(removed);
        if (changed is not null)
            patched = patched.SetItems(changed);
        return new PackManifestData(patched);
    }

    /// <summary>Formats a manifest: the canonical head, then one entry per line sorted by path in ordinal UTF-8 order.</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="entries">The entries, in any order.</param>
    /// <returns>The file bytes.</returns>
    /// <exception cref="ArgumentException">Two entries share a path.</exception>
    internal byte[] Format(string pack, IReadOnlyList<ManifestEntry> entries)
    {
        var sorted = IsSorted(entries) ? entries : entries.OrderBy(e => e.Path, Utf8OrdinalComparer.Instance).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            if (string.Equals(sorted[i - 1].Path, sorted[i].Path, StringComparison.Ordinal))
                throw new ArgumentException($"Duplicate manifest path '{sorted[i].Path}'.", nameof(entries));
        }

        // The head goes through the canonical writer, so "$schema" and "pack" are exactly what it would write.
        var documentPath = $".maquettiste/manifest/{pack}.json";
        var head = json.Write(new JsonObject { ["pack"] = pack }, SchemaFile, documentPath);
        var close = head.AsSpan().LastIndexOf("\n}"u8);
        ReadOnlySpan<byte> open = ",\n  \"files\": [\n"u8, lineStart = "    ["u8, separator = ", "u8, end = "  ]\n}\n"u8;

        // Written straight into one array of the exact size (two passes over the entries; only a value that needs escaping is
        // formatted twice): no text builder, no string per line or value, no copy.
        var size = (long)close + open.Length + end.Length;
        foreach (var e in sorted)
            size += lineStart.Length + TokenLength(e.Path) + separator.Length + TokenLength(e.Hash) + separator.Length + TokenLength(e.Unit) + 3; // "],\n"
        if (sorted.Count > 0)
            size--; // the last line ends "]\n"
        var bytes = new byte[checked((int)size)];
        var at = 0;
        void Put(ReadOnlySpan<byte> part)
        {
            part.CopyTo(bytes.AsSpan(at));
            at += part.Length;
        }

        Put(head.AsSpan(0, close));
        Put(open);
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            Put(lineStart);
            at += WriteToken(e.Path, bytes.AsSpan(at));
            Put(separator);
            at += WriteToken(e.Hash, bytes.AsSpan(at));
            Put(separator);
            at += WriteToken(e.Unit, bytes.AsSpan(at));
            Put(i + 1 < sorted.Count ? "],\n"u8 : "]\n"u8);
        }

        Put(end);
        return at == bytes.Length ? bytes : throw new InvalidOperationException("The manifest's size was miscounted.");
    }

    /// <summary>Whether a value is written as is between quotes: printable ASCII other than '"' and '\\' (the relaxed encoder leaves it unchanged).</summary>
    private static bool IsPlain(string value) => !value.AsSpan().ContainsAnyExceptInRange(' ', '~') && !value.AsSpan().ContainsAny('"', '\\');

    /// <summary>The UTF-8 length of a value's JSON string token.</summary>
    private static int TokenLength(string value) => IsPlain(value) ? value.Length + 2 : Encoding.UTF8.GetByteCount(Token(value));

    /// <summary>Writes a value's JSON string token; returns its length.</summary>
    private static int WriteToken(string value, Span<byte> target)
    {
        if (!IsPlain(value))
            return Encoding.UTF8.GetBytes(Token(value), target);
        target[0] = (byte)'"';
        System.Text.Ascii.FromUtf16(value, target[1..], out var written);
        target[written + 1] = (byte)'"';
        return written + 2;
    }

    /// <summary>Whether entries are already in ordinal UTF-8 path order (the writer passes them so), which saves sorting them.</summary>
    private static bool IsSorted(IReadOnlyList<ManifestEntry> entries)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            if (Utf8OrdinalComparer.Instance.Compare(entries[i - 1].Path, entries[i].Path) > 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Parses a manifest. A file that is not valid JSON (for example one left with git conflict markers) is read line by line: every
    /// line that holds one <c>[path, hash, unit]</c> entry counts, the first occurrence of a path wins, and anything else is ignored.
    /// </summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns>The entries, in file order.</returns>
    internal static IReadOnlyList<ManifestEntry> Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ManifestEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in files.EnumerateArray())
                {
                    if (ToEntry(item) is { } entry && seen.Add(entry.Path))
                        list.Add(entry);
                }

                return list;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return [];
        }
        catch (JsonException)
        {
        }

        return Salvage(bytes);
    }

    private static List<ManifestEntry> Salvage(byte[] bytes)
    {
        var list = new List<ManifestEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var line = raw.Trim().TrimEnd(',');
            if (!line.StartsWith('[') || !line.EndsWith(']'))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (ToEntry(document.RootElement) is { } entry && seen.Add(entry.Path))
                    list.Add(entry);
            }
            catch (JsonException)
            {
            }
        }

        return list;
    }

    private static ManifestEntry? ToEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3)
            return null;
        var path = item[0];
        var hash = item[1];
        var unit = item[2];
        if (path.ValueKind != JsonValueKind.String || hash.ValueKind != JsonValueKind.String || unit.ValueKind != JsonValueKind.String)
            return null;
        var p = path.GetString()!;
        var h = hash.GetString()!;
        var u = unit.GetString()!;
        return p.Length == 0 || h.Length == 0 ? null : new ManifestEntry(p, h, u);
    }

    private static string Token(string value)
    {
        // Printable ASCII other than '"' and '\\' is written as is by the relaxed encoder: skip the writer for it.
        if (!value.AsSpan().ContainsAnyExceptInRange(' ', '~') && !value.AsSpan().ContainsAny('"', '\\'))
            return string.Concat("\"", value, "\"");
        var buffer = new ArrayBufferWriter<byte>(value.Length + 8);
        using (var writer = new Utf8JsonWriter(buffer, TokenOptions))
            writer.WriteStringValue(value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>Orders strings as their UTF-8 bytes would (code point order), unlike ordinal UTF-16 order for supplementary characters.</summary>
internal sealed class Utf8OrdinalComparer : IComparer<string>
{
    /// <summary>The instance.</summary>
    public static Utf8OrdinalComparer Instance { get; } = new();

    /// <inheritdoc/>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        // Past a common prefix, two UTF-16 units that are not surrogates compare as their code points do; only a surrogate at the
        // first difference needs the rune comparison. A string that is a prefix of the other sorts first either way.
        var common = x.AsSpan().CommonPrefixLength(y);
        if (common == x.Length || common == y.Length)
            return x.Length.CompareTo(y.Length);
        var cx = x[common];
        var cy = y[common];
        return char.IsSurrogate(cx) || char.IsSurrogate(cy) ? CompareRunes(x, y) : cx.CompareTo(cy);
    }

    private static int CompareRunes(string x, string y)
    {
        var ex = x.EnumerateRunes();
        var ey = y.EnumerateRunes();
        while (true)
        {
            var hx = ex.MoveNext();
            var hy = ey.MoveNext();
            if (!hx || !hy)
                return hx == hy ? 0 : hx ? 1 : -1;
            var c = ex.Current.Value.CompareTo(ey.Current.Value);
            if (c != 0)
                return c;
        }
    }
}
