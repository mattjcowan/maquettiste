using System.IO.Compression;
using System.Text.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Snapshots;

namespace Maquettiste.Engine;

public sealed partial class SnapshotLibrary
{
    /// <summary>The largest page of a comparison's elements.</summary>
    public const int MaxCompareLimit = 5000;

    /// <summary>The page of a comparison's elements when none is asked for.</summary>
    public const int DefaultCompareLimit = 500;

    private const int MaxFiles = 1000;
    private const int MaxFields = 500;

    /// <summary>
    /// Compares two models, each a snapshot or the working model (<see cref="Working"/>). A snapshot's side comes from its index (no
    /// document is decompressed); the working model's from the store's documents and their hashes (only documents that are not
    /// elements, or did not load, are read). Elements are matched by id: added, removed, or changed when their documents' hashes
    /// differ (renamed and moved say how). Other documents are matched by path. The packs are compared when both sides hold them.
    /// </summary>
    /// <param name="from">A snapshot id or <c>working</c>.</param>
    /// <param name="to">A snapshot id or <c>working</c>.</param>
    /// <param name="offset">The first element of the page.</param>
    /// <param name="limit">The page size, 1 to <see cref="MaxCompareLimit"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The comparison, or <see langword="null"/> when a side names no snapshot.</returns>
    public async Task<SnapshotComparison?> CompareAsync(string from, string to, int offset, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxCompareLimit);
        if (await SideAsync(from, ct).ConfigureAwait(false) is not { } a || await SideAsync(to, ct).ConfigureAwait(false) is not { } b)
            return null;
        var packs = a.IncludesPacks && b.IncludesPacks;
        var (aElements, aFiles) = Split(a.Rows, packs);
        var (bElements, bFiles) = Split(b.Rows, packs);

        var changes = new List<SnapshotElementChange>();
        foreach (var (id, after) in bElements)
        {
            if (!aElements.TryGetValue(id, out var before))
            {
                changes.Add(new SnapshotElementChange(id, after.Kind!, after.Name ?? "", "added", after.Path));
            }
            else if (before.Hash != after.Hash)
            {
                changes.Add(new SnapshotElementChange(id, after.Kind!, after.Name ?? "", "changed", after.Path,
                    (before.Name ?? "") != (after.Name ?? "") ? before.Name ?? "" : null, before.Path != after.Path ? before.Path : null));
            }
        }

        foreach (var (id, before) in aElements)
        {
            if (!bElements.ContainsKey(id))
                changes.Add(new SnapshotElementChange(id, before.Kind!, before.Name ?? "", "removed", before.Path));
        }

        changes.Sort((x, y) =>
        {
            var c = string.CompareOrdinal(x.Kind, y.Kind);
            if (c == 0)
                c = string.CompareOrdinal(x.Name, y.Name);
            return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
        });

        var files = new List<SnapshotFileChange>();
        foreach (var (path, after) in bFiles)
        {
            if (!aFiles.TryGetValue(path, out var before))
                files.Add(new SnapshotFileChange(path, "added"));
            else if (before.Hash != after.Hash)
                files.Add(new SnapshotFileChange(path, "changed"));
        }

        foreach (var path in aFiles.Keys.Where(p => !bFiles.ContainsKey(p)))
            files.Add(new SnapshotFileChange(path, "removed"));
        files.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));

        var kinds = changes.GroupBy(c => c.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SnapshotKindChanges(g.Key, g.Count(c => c.Change == "added"), g.Count(c => c.Change == "removed"), g.Count(c => c.Change == "changed")))
            .ToList();
        var page = changes.Skip(offset).Take(limit).ToList();
        int? next = offset + page.Count < changes.Count ? offset + page.Count : null;
        return new SnapshotComparison(a.Name, b.Name, kinds.Sum(k => k.Added), kinds.Sum(k => k.Removed), kinds.Sum(k => k.Changed), kinds, page, next,
            [.. files.Take(MaxFiles)], files.Count > MaxFiles, packs);
    }

    /// <summary>
    /// One element on both sides of a comparison: the two documents, in the form the editor's conflict view renders, and the fields
    /// that differ (objects by key, arrays of objects with ids by id, other arrays by position when their lengths match, else whole).
    /// </summary>
    /// <param name="from">A snapshot id or <c>working</c>.</param>
    /// <param name="to">A snapshot id or <c>working</c>.</param>
    /// <param name="id">The element id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The detail, or <see langword="null"/> when a side names no snapshot or neither side has the element.</returns>
    public async Task<SnapshotElementDiff?> CompareElementAsync(string from, string to, string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (await SideAsync(from, ct).ConfigureAwait(false) is not { } a || await SideAsync(to, ct).ConfigureAwait(false) is not { } b)
            return null;
        var before = Split(a.Rows, false).Elements.GetValueOrDefault(id);
        var after = Split(b.Rows, false).Elements.GetValueOrDefault(id);
        if (before is null && after is null)
            return null;
        var beforeJson = before is null ? null : Parse(await a.Read(before.Path, ct).ConfigureAwait(false));
        var afterJson = after is null ? null : Parse(await b.Read(after.Path, ct).ConfigureAwait(false));
        var fields = new List<SnapshotFieldChange>();
        var change = before is null ? "added" : after is null ? "removed" : before.Hash == after.Hash ? "unchanged" : "changed";
        if (change == "changed" && beforeJson is { } x && afterJson is { } y)
            Diff(x, y, "", fields);
        var row = after ?? before!;
        return new SnapshotElementDiff(id, row.Kind!, row.Name ?? "", change, before?.Path, after?.Path, before?.Hash, after?.Hash, beforeJson, afterJson,
            [.. fields.Take(MaxFields)], fields.Count > MaxFields);
    }

    private static JsonElement? Parse(byte[]? bytes)
    {
        if (bytes is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Adds the differences of two JSON values under a pointer; stops once more than <see cref="MaxFields"/> are found.</summary>
    private static void Diff(JsonElement before, JsonElement after, string pointer, List<SnapshotFieldChange> fields)
    {
        if (fields.Count > MaxFields)
            return;
        if (before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var p in before.EnumerateObject())
                keys.Add(p.Name);
            foreach (var p in after.EnumerateObject())
                keys.Add(p.Name);
            foreach (var key in keys)
            {
                var child = pointer + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                var had = before.TryGetProperty(key, out var b);
                var has = after.TryGetProperty(key, out var a);
                if (!has)
                    fields.Add(new SnapshotFieldChange(child, "removed", b.Clone(), null));
                else if (!had)
                    fields.Add(new SnapshotFieldChange(child, "added", null, a.Clone()));
                else
                    Diff(b, a, child, fields);
            }

            return;
        }

        if (before.ValueKind == JsonValueKind.Array && after.ValueKind == JsonValueKind.Array && !JsonElement.DeepEquals(before, after))
        {
            var beforeItems = before.EnumerateArray().ToArray(); // an index into a JsonElement array of objects walks it
            var afterItems = after.EnumerateArray().ToArray();
            if (IdsOf(before) is { } beforeIds && IdsOf(after) is { } afterIdIndex)
            {
                var afterIds = afterIdIndex.OrderBy(p => p.Value).Select(p => p.Key).ToList();
                var count = fields.Count;
                for (var j = 0; j < afterIds.Count && fields.Count <= MaxFields; j++)
                {
                    var child = pointer + "/" + j.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (beforeIds.TryGetValue(afterIds[j], out var i))
                        Diff(beforeItems[i], afterItems[j], child, fields);
                    else
                        fields.Add(new SnapshotFieldChange(child, "added", null, afterItems[j].Clone()));
                }

                var kept = afterIds.ToHashSet(StringComparer.Ordinal);
                foreach (var (beforeId, i) in beforeIds.OrderBy(p => p.Value))
                {
                    if (!kept.Contains(beforeId))
                        fields.Add(new SnapshotFieldChange(pointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "removed", beforeItems[i].Clone(), null));
                    if (fields.Count > MaxFields)
                        break;
                }

                if (fields.Count == count)
                    fields.Add(new SnapshotFieldChange(pointer, "changed", before.Clone(), after.Clone())); // the same items, reordered
                return;
            }

            if (beforeItems.Length == afterItems.Length)
            {
                for (var i = 0; i < beforeItems.Length; i++)
                    Diff(beforeItems[i], afterItems[i], pointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), fields);
                return;
            }
        }

        if (!JsonElement.DeepEquals(before, after))
            fields.Add(new SnapshotFieldChange(pointer.Length == 0 ? "" : pointer, "changed", before.Clone(), after.Clone()));
    }

    /// <summary>The ids of an array whose items are all objects with a distinct string <c>id</c>, else <see langword="null"/>.</summary>
    private static Dictionary<string, int>? IdsOf(JsonElement array)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !ids.TryAdd(id.GetString()!, i++))
                return null;
        }

        return ids;
    }

    /// <summary>
    /// Element rows by id (the ordinally first path wins, as the loader decides a duplicate id) and the other rows by path; the packs
    /// only when they are compared.
    /// </summary>
    private static (Dictionary<string, SnapshotIndexRow> Elements, Dictionary<string, SnapshotIndexRow> Files) Split(IReadOnlyList<SnapshotIndexRow> rows, bool packs)
    {
        var elements = new Dictionary<string, SnapshotIndexRow>(StringComparer.Ordinal);
        var files = new Dictionary<string, SnapshotIndexRow>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            if (!packs && SnapshotLayout.IsTemplatePath(row.Path))
                continue;
            if (row is { Id: { } id, Kind: not null } && elements.TryAdd(id, row))
                continue;
            files[row.Path] = row;
        }

        return (elements, files);
    }

    private async Task<Side?> SideAsync(string name, CancellationToken ct)
    {
        if (name == Working)
            return await WorkingSideAsync(ct).ConfigureAwait(false);
        if (!IsValidId(name) || TryInfo(name) is not { } info)
            return null;
        var file = FileOf(name);
        List<SnapshotIndexRow> rows;
        using (var zip = ZipFile.OpenRead(file))
        {
            var entry = zip.GetEntry(SnapshotLayout.IndexEntry) ?? throw new InvalidDataException($"{file} has no {SnapshotLayout.IndexEntry}.");
            await using var read = entry.Open();
            rows = SnapshotIndexRow.Read(ReadBounded(read, Limits.MaxEntryBytes));
        }

        return new Side(name, info.IncludesPacks, rows, (path, token) => Task.FromResult(ReadEntry(file, path)));
    }

    /// <summary>The working model's rows: the store's documents and their hashes (after a stat rescan); every other document is read.</summary>
    private async Task<Side> WorkingSideAsync(CancellationToken ct)
    {
        var model = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var live = _store.Documents;
        var paths = ScopePaths(live, includePacks: true);
        var known = new Dictionary<string, Model.ElementDocument>(StringComparer.Ordinal);
        foreach (var document in model.Documents)
            known[_paths.FromRepoPath(document.Path)] = document;
        var rows = new SnapshotIndexRow?[paths.Count];
        var unknown = new List<int>();
        for (var i = 0; i < paths.Count; i++)
        {
            if (known.TryGetValue(paths[i], out var d))
                rows[i] = new SnapshotIndexRow(paths[i], d.Hash, d.Element.Id, d.Element.KindName, d.Element.Name);
            else
                unknown.Add(i);
        }

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.EffectiveParallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(unknown, parallel, async (i, token) =>
        {
            if (await live.ReadAsync(paths[i], token).ConfigureAwait(false) is { } bytes)
                rows[i] = SnapshotIndexRow.Of(paths[i], bytes);
        }).ConfigureAwait(false);
        return new Side(Working, true, [.. rows.OfType<SnapshotIndexRow>()], live.ReadAsync);
    }

    private byte[]? ReadEntry(string file, string path)
    {
        using var zip = ZipFile.OpenRead(file);
        if (zip.GetEntry(path) is not { } entry)
            return null;
        using var read = entry.Open();
        return ReadBounded(read, Limits.MaxEntryBytes);
    }

    private sealed record Side(string Name, bool IncludesPacks, List<SnapshotIndexRow> Rows, Func<string, CancellationToken, Task<byte[]?>> Read);
}
