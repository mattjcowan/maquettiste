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
/// <param name="options">The engine options.</param>
/// <param name="json">The canonical writer (for the document head and its <c>$schema</c>).</param>
/// <param name="paths">The engine-write guard (<see cref="WriteTarget.Model"/> for manifests, <see cref="WriteTarget.Cache"/> to delete a cache copy).</param>
internal sealed class ManifestStore(EngineOptions options, ICanonicalJson json, IOutputPathPolicy paths) : IManifestStore
{
    private const string SchemaFile = "manifest.json";

    private static readonly JsonWriterOptions TokenOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    private int _tempCounter;

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
            // Collected in a hash map (the manifest first, so its entry wins a path a cache copy lists too), then turned into the
            // sorted map in one step, which builds the tree from sorted input instead of rebalancing it once per entry.
            var entries = new Dictionary<string, (ManifestEntry, ManifestBucket)>(StringComparer.Ordinal);
            foreach (var file in new[] { FileOf(pack), LegacyFileOf(pack) })
            {
                var bytes = await AtomicFile.ReadIfExistsAsync(file, ct).ConfigureAwait(false);
                if (bytes is null)
                    continue;
                foreach (var entry in Parse(bytes))
                    entries.TryAdd(entry.Path, (entry, ManifestBucket.Manifest));
            }

            if (entries.Count > 0)
                result[pack] = new PackManifestData(ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, entries));
        }

        return new ManifestSet(new ManifestSetData(result.ToImmutable()));
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

        if (entries.Count == 0)
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        else
        {
            var bytes = Format(pack, entries);
            var existing = await AtomicFile.ReadIfExistsAsync(file, ct).ConfigureAwait(false);
            if (existing is null || !existing.AsSpan().SequenceEqual(bytes))
            {
                var tag = "manifest-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-"
                    + Interlocked.Increment(ref _tempCounter).ToString(CultureInfo.InvariantCulture);
                await AtomicFile.WriteAsync(file, bytes, tag, ct).ConfigureAwait(false);
            }
        }

        // The cache copy of an earlier release was read with the manifest, so its entries are in this save: it goes now.
        var legacy = LegacyFileOf(pack);
        if (File.Exists(legacy) && paths.CheckEngineWrite(WriteTarget.Cache, legacy).Allowed)
            File.Delete(legacy);
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
        var head = Encoding.UTF8.GetString(json.Write(new JsonObject { ["pack"] = pack }, SchemaFile, documentPath));
        var close = head.LastIndexOf("\n}", StringComparison.Ordinal);
        var sb = new StringBuilder(head.Length + (sorted.Count * 128));
        sb.Append(head, 0, close).Append(",\n  \"files\": [\n");
        for (var i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            sb.Append("    [").Append(Token(e.Path)).Append(", ").Append(Token(e.Hash)).Append(", ").Append(Token(e.Unit)).Append(']');
            sb.Append(i + 1 < sorted.Count ? ",\n" : "\n");
        }

        sb.Append("  ]\n}\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
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
