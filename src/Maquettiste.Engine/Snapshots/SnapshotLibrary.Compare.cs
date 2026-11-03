using System.IO.Compression;
using System.Text.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
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
    public Task<SnapshotComparison?> CompareAsync(string from, string to, int offset, int limit, CancellationToken ct) =>
        CompareAsync(from, to, offset, limit, null, ct);

    /// <summary>
    /// Compares two models as <see cref="CompareAsync(string, string, int, int, CancellationToken)"/> does; with options a side may also
    /// be a folder that holds another copy of the project (read like the working model, without packs, nothing validated or written),
    /// the elements may be grouped by package and carry their field changes (next-phase-2-design.md section 6.2). A value resolves in
    /// one order: <c>working</c>, then a folder when it is path-like, else a snapshot id when the archive exists, else a folder when the
    /// directory exists.
    /// </summary>
    /// <param name="from">A snapshot id, <c>working</c>, or (when allowed) a folder.</param>
    /// <param name="to">A snapshot id, <c>working</c>, or (when allowed) a folder.</param>
    /// <param name="offset">The first element of the page.</param>
    /// <param name="limit">The page size, 1 to <see cref="MaxCompareLimit"/>.</param>
    /// <param name="options">How to compare, or <see langword="null"/> to answer as before.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The comparison, or <see langword="null"/> without options when a side names no snapshot.</returns>
    /// <exception cref="ArgumentException">With options: a side resolves to nothing, names a folder where folders are not allowed or that
    /// is not a project, or holds a newer model format (MQ1007's text).</exception>
    public async Task<SnapshotComparison?> CompareAsync(string from, string to, int offset, int limit, SnapshotCompareOptions? options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxCompareLimit);
        CheckOptions(options);
        var owned = new List<IDisposable>();
        try
        {
            if (await OpenSideAsync(from, options, owned, ct).ConfigureAwait(false) is not { } a
                || await OpenSideAsync(to, options, owned, ct).ConfigureAwait(false) is not { } b)
            {
                return null;
            }

            return await CompareSidesAsync(a, b, offset, limit, options, ct).ConfigureAwait(false);
        }
        finally
        {
            for (var i = owned.Count - 1; i >= 0; i--)
                owned[i].Dispose();
        }
    }

    private async Task<SnapshotComparison> CompareSidesAsync(Side a, Side b, int offset, int limit, SnapshotCompareOptions? options, CancellationToken ct)
    {
        var packs = a.IncludesPacks && b.IncludesPacks;
        var (aElements, aFiles) = Split(a.Rows, packs);
        var (bElements, bFiles) = Split(b.Rows, packs);

        var entries = new List<Entry>();
        foreach (var (id, after) in bElements)
        {
            if (!aElements.TryGetValue(id, out var before))
            {
                entries.Add(new Entry(new SnapshotElementChange(id, after.Kind!, after.Name ?? "", "added", after.Path), null, after));
            }
            else if (before.Hash != after.Hash)
            {
                entries.Add(new Entry(new SnapshotElementChange(id, after.Kind!, after.Name ?? "", "changed", after.Path,
                    (before.Name ?? "") != (after.Name ?? "") ? before.Name ?? "" : null, before.Path != after.Path ? before.Path : null), before, after));
            }
        }

        foreach (var (id, before) in aElements)
        {
            if (!bElements.ContainsKey(id))
                entries.Add(new Entry(new SnapshotElementChange(id, before.Kind!, before.Name ?? "", "removed", before.Path), before, null));
        }

        IReadOnlyList<SnapshotPackageChanges>? groups = null;
        if (options is { ByPackage: true })
            groups = await GroupAsync(a, b, entries, ct).ConfigureAwait(false);
        else
            entries.Sort((x, y) => ByKind(x.Change, y.Change));

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

        var changes = entries.Select(e => e.Change).ToList();
        var kinds = changes.GroupBy(c => c.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SnapshotKindChanges(g.Key, g.Count(c => c.Change == "added"), g.Count(c => c.Change == "removed"), g.Count(c => c.Change == "changed")))
            .ToList();
        var pageEntries = entries.Skip(offset).Take(limit).ToList();
        var page = new List<SnapshotElementChange>(pageEntries.Count);
        foreach (var entry in pageEntries)
        {
            if (options is { FieldChanges: true } && entry is { Before: { } before, After: { } after })
            {
                var fields = await FieldsAsync(a, b, before, after, entry.Document, ct).ConfigureAwait(false);
                page.Add(entry.Change with
                {
                    Fields = [.. fields.Take(SnapshotCompareOptions.MaxElementFields)],
                    FieldsTruncated = fields.Count > SnapshotCompareOptions.MaxElementFields,
                });
            }
            else
            {
                page.Add(entry.Change);
            }
        }

        int? next = offset + page.Count < changes.Count ? offset + page.Count : null;
        return new SnapshotComparison(a.Name, b.Name, kinds.Sum(k => k.Added), kinds.Sum(k => k.Removed), kinds.Sum(k => k.Changed), kinds, page, next,
            [.. files.Take(MaxFiles)], files.Count > MaxFiles, packs) { Packages = groups };
    }

    private static int ByKind(SnapshotElementChange x, SnapshotElementChange y)
    {
        var c = string.CompareOrdinal(x.Kind, y.Kind);
        if (c == 0)
            c = string.CompareOrdinal(x.Name, y.Name);
        return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
    }

    /// <summary>
    /// Fills each row's package members from its document (the <c>to</c> side's, the <c>from</c> side's for a removed element), orders
    /// the rows by group and returns the groups: packages by qualified name, "(no package)", then the facts of each database by its
    /// name; ties by id. Inside a group: kind, name, id.
    /// </summary>
    private async Task<List<SnapshotPackageChanges>> GroupAsync(Side a, Side b, List<Entry> entries, CancellationToken ct)
    {
        var documents = new JsonElement?[entries.Count];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.EffectiveParallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), parallel, async (i, token) =>
        {
            var (side, row) = entries[i].Change.Change == "removed" ? (a, entries[i].Before!) : (b, entries[i].After!);
            documents[i] = Parse(await side.Read(row.Path, token).ConfigureAwait(false));
        }).ConfigureAwait(false);

        for (var i = 0; i < entries.Count; i++)
        {
            var change = entries[i].Change;
            var json = documents[i];
            if (change.Kind == "migration")
                change = change with { Database = Text(json, "database"), Label = FactLabel(json) };
            else
                change = change with { Package = Text(json, change.Kind == "package" ? "parent" : "package") };
            entries[i] = entries[i] with { Change = change, Document = json };
        }

        var keyed = new List<(Entry Entry, int Rank, string Label, string Key)>(entries.Count);
        Dictionary<string, SnapshotIndexRow>? aPackages = null, bPackages = null;
        foreach (var group in entries.GroupBy(e => GroupKey(e.Change)))
        {
            // A group's label comes from the to side, or the from side when every row in it was removed.
            var to = group.Any(e => e.Change.Change != "removed");
            var side = to ? b : a;
            var label = group.Key.Rank switch
            {
                0 => await QualifiedNameAsync(side, to ? bPackages ??= PackagesOf(b) : aPackages ??= PackagesOf(a), group.Key.Key, ct)
                    .ConfigureAwait(false) ?? group.Key.Key,
                1 => NoPackage,
                _ => "migrations of " + (side.Rows.FirstOrDefault(r => r.Kind == "database" && r.Id == group.Key.Key)?.Name ?? group.Key.Key),
            };
            keyed.AddRange(group.Select(e => (e, group.Key.Rank, label, group.Key.Key)));
        }

        keyed.Sort((x, y) =>
        {
            var c = x.Rank.CompareTo(y.Rank);
            if (c == 0)
                c = string.CompareOrdinal(x.Label, y.Label);
            if (c == 0)
                c = string.CompareOrdinal(x.Key, y.Key);
            return c != 0 ? c : ByKind(x.Entry.Change, y.Entry.Change);
        });
        entries.Clear();
        entries.AddRange(keyed.Select(k => k.Entry));
        return [.. keyed.GroupBy(k => (k.Rank, k.Key)).Select(g =>
        {
            var first = g.First();
            return new SnapshotPackageChanges(first.Rank == 0 ? first.Key : null, first.Label, g.Count(k => k.Entry.Change.Change == "added"),
                g.Count(k => k.Entry.Change.Change == "removed"), g.Count(k => k.Entry.Change.Change == "changed"));
        })];
    }

    /// <summary>The group label of the elements no package holds.</summary>
    internal const string NoPackage = "(no package)";

    private static (int Rank, string Key) GroupKey(SnapshotElementChange change) => change switch
    {
        { Kind: "migration", Database: { } database } => (2, database),
        { Package: { } package } => (0, package),
        _ => (1, ""),
    };

    /// <summary>A package's qualified name on a side, from the side's package documents; <see langword="null"/> when it lacks it.</summary>
    private static async Task<string?> QualifiedNameAsync(Side side, Dictionary<string, SnapshotIndexRow> packages, string id, CancellationToken ct)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = id; current is not null && seen.Add(current) && packages.TryGetValue(current, out var row);)
        {
            names.Add(row.Name ?? "");
            current = Text(Parse(await side.Read(row.Path, ct).ConfigureAwait(false)), "parent");
        }

        if (names.Count == 0)
            return null;
        names.Reverse();
        return QualifiedName.FormatPath(names);
    }

    /// <summary>A side's package rows by id (the ordinally first path wins, as the loader decides a duplicate id).</summary>
    private static Dictionary<string, SnapshotIndexRow> PackagesOf(Side side) =>
        side.Rows.Where(r => r is { Kind: "package", Id: not null }).GroupBy(r => r.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Path, StringComparer.Ordinal).First(), StringComparer.Ordinal);

    private static string? Text(JsonElement? json, string property) =>
        json is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>A recorded fact's label: its <c>fact</c>, then its object's kind and names (<c>rename column billing.invoices.number</c>).</summary>
    private static string FactLabel(JsonElement? json)
    {
        var fact = Text(json, "fact") ?? "migration";
        if (json is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("object", out var value) || value.ValueKind != JsonValueKind.Object)
            return fact;
        try
        {
            return JsonSerializer.Deserialize<MigrationObject>(value, EngineJson.Options) is { } target
                ? fact + " " + target.Kind + " " + FactDocuments.Name(target)
                : fact;
        }
        catch (JsonException)
        {
            return fact;
        }
    }

    /// <summary>The field changes of a changed element, each naming its sub-element.</summary>
    private static async Task<List<SnapshotFieldChange>> FieldsAsync(Side a, Side b, SnapshotIndexRow before, SnapshotIndexRow after, JsonElement? read,
        CancellationToken ct)
    {
        var fields = new List<SnapshotFieldChange>();
        if (Parse(await a.Read(before.Path, ct).ConfigureAwait(false)) is { } x
            && (read ?? Parse(await b.Read(after.Path, ct).ConfigureAwait(false))) is { } y)
        {
            Diff(x, y, "", fields, new FieldOwners(after.Kind), null);
        }

        return fields;
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
    public Task<SnapshotElementDiff?> CompareElementAsync(string from, string to, string id, CancellationToken ct) =>
        CompareElementAsync(from, to, id, null, ct);

    /// <summary>
    /// One element on both sides, as <see cref="CompareElementAsync(string, string, string, CancellationToken)"/> answers; with options a
    /// side may be a folder (see <see cref="CompareAsync(string, string, int, int, SnapshotCompareOptions?, CancellationToken)"/>). Each
    /// field change names its sub-element.
    /// </summary>
    /// <param name="from">A snapshot id, <c>working</c>, or (when allowed) a folder.</param>
    /// <param name="to">A snapshot id, <c>working</c>, or (when allowed) a folder.</param>
    /// <param name="id">The element id.</param>
    /// <param name="options">How to resolve the sides, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The detail, or <see langword="null"/> when neither side has the element (or, without options, a side names no
    /// snapshot).</returns>
    /// <exception cref="ArgumentException">With options: a side resolves to nothing, names a folder where folders are not allowed or that
    /// is not a project, or holds a newer model format.</exception>
    public async Task<SnapshotElementDiff?> CompareElementAsync(string from, string to, string id, SnapshotCompareOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(id);
        CheckOptions(options);
        var owned = new List<IDisposable>();
        try
        {
            if (await OpenSideAsync(from, options, owned, ct).ConfigureAwait(false) is not { } a
                || await OpenSideAsync(to, options, owned, ct).ConfigureAwait(false) is not { } b)
            {
                return null;
            }

            var before = Split(a.Rows, false).Elements.GetValueOrDefault(id);
            var after = Split(b.Rows, false).Elements.GetValueOrDefault(id);
            if (before is null && after is null)
                return null;
            var beforeJson = before is null ? null : Parse(await a.Read(before.Path, ct).ConfigureAwait(false));
            var afterJson = after is null ? null : Parse(await b.Read(after.Path, ct).ConfigureAwait(false));
            var fields = new List<SnapshotFieldChange>();
            var change = before is null ? "added" : after is null ? "removed" : before.Hash == after.Hash ? "unchanged" : "changed";
            var row = after ?? before!;
            if (change == "changed" && beforeJson is { } x && afterJson is { } y)
                Diff(x, y, "", fields, new FieldOwners(row.Kind), null);
            return new SnapshotElementDiff(id, row.Kind!, row.Name ?? "", change, before?.Path, after?.Path, before?.Hash, after?.Hash, beforeJson, afterJson,
                [.. fields.Take(MaxFields)], fields.Count > MaxFields);
        }
        finally
        {
            for (var i = owned.Count - 1; i >= 0; i--)
                owned[i].Dispose();
        }
    }

    private static void CheckOptions(SnapshotCompareOptions? options)
    {
        if (options is { AllowFolders: true } && (options.CurrentDirectory is null || !Path.IsPathFullyQualified(options.CurrentDirectory)))
            throw new ArgumentException("A comparison that allows folders needs the absolute current directory.", nameof(options));
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

    /// <summary>
    /// Adds the differences of two JSON values under a pointer, each naming the sub-element it belongs to; stops once more than
    /// <see cref="MaxFields"/> are found.
    /// </summary>
    private static void Diff(JsonElement before, JsonElement after, string pointer, List<SnapshotFieldChange> fields, FieldOwners owners,
        FieldOwners.Owner? owner)
    {
        if (fields.Count > MaxFields)
            return;
        if (before.ValueKind == JsonValueKind.Object && after.ValueKind == JsonValueKind.Object)
        {
            owner = owners.Enter(owner, pointer, before, after);
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
                    fields.Add(owners.Attribute(new SnapshotFieldChange(child, "removed", b.Clone(), null), owner));
                else if (!had)
                    fields.Add(owners.Attribute(new SnapshotFieldChange(child, "added", null, a.Clone()), owner));
                else
                    Diff(b, a, child, fields, owners, owner);
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
                        Diff(beforeItems[i], afterItems[j], child, fields, owners, owner);
                    else
                        fields.Add(owners.Attribute(new SnapshotFieldChange(child, "added", null, afterItems[j].Clone()), owner));
                }

                var kept = afterIds.ToHashSet(StringComparer.Ordinal);
                foreach (var (beforeId, i) in beforeIds.OrderBy(p => p.Value))
                {
                    if (!kept.Contains(beforeId))
                        fields.Add(owners.Attribute(new SnapshotFieldChange(pointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "removed", beforeItems[i].Clone(), null), owner));
                    if (fields.Count > MaxFields)
                        break;
                }

                if (fields.Count == count)
                    fields.Add(owners.Attribute(new SnapshotFieldChange(pointer, "changed", before.Clone(), after.Clone()), owner)); // the same items, reordered
                return;
            }

            if (beforeItems.Length == afterItems.Length)
            {
                for (var i = 0; i < beforeItems.Length; i++)
                    Diff(beforeItems[i], afterItems[i], pointer + "/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), fields, owners, owner);
                return;
            }
        }

        if (!JsonElement.DeepEquals(before, after))
            fields.Add(owners.Attribute(new SnapshotFieldChange(pointer.Length == 0 ? "" : pointer, "changed", before.Clone(), after.Clone()), owner));
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

    /// <summary>
    /// Opens a side. Without options: <c>working</c> or a snapshot id, else <see langword="null"/>. With options: <c>working</c>, then
    /// <see cref="ModelBaseline"/>'s order (path-like means a folder; else a snapshot id when the archive exists; else a folder when
    /// allowed), else an <see cref="ArgumentException"/> naming the value. What the side holds open is added to <paramref name="owned"/>.
    /// </summary>
    private async Task<Side?> OpenSideAsync(string value, SnapshotCompareOptions? options, List<IDisposable> owned, CancellationToken ct)
    {
        if (value == Working)
            return await StoreSideAsync(_store, Working, includePacks: true, ct).ConfigureAwait(false);
        var pathLike = ModelBaseline.IsPathLike(value);
        if (!pathLike && IsValidId(value) && TryInfo(value) is { } info)
        {
            if (options is not null && info.ModelFormat > EngineVersion.FormatVersion)
                throw new ArgumentException(ModelBaseline.NewerFormat("snapshot " + value, info.ModelFormat));
            var side = SnapshotSide(value, info);
            owned.Add(side);
            return side;
        }

        if (options is null)
            return null;
        if (!options.AllowFolders)
        {
            throw new ArgumentException(pathLike
                ? $"'{value}' is a folder; this host compares model snapshots and the working model only."
                : $"'{value}' names no model snapshot (nor working).");
        }

        ModelBaseline baseline;
        try
        {
            baseline = await ModelBaseline.OpenFolderAsync(_store, value, options.CurrentDirectory!, ct).ConfigureAwait(false);
        }
        catch (BadBaselineException ex)
        {
            throw new ArgumentException(ex.Text);
        }

        owned.Add(baseline);
        return await StoreSideAsync(baseline.Store, value, includePacks: false, ct).ConfigureAwait(false);
    }

    /// <summary>A snapshot's side: its index, and its documents read through one open archive.</summary>
    private Side SnapshotSide(string id, SnapshotInfo info)
    {
        var file = FileOf(id);
        var zip = ZipFile.OpenRead(file);
        try
        {
            var entry = zip.GetEntry(SnapshotLayout.IndexEntry) ?? throw new InvalidDataException($"{file} has no {SnapshotLayout.IndexEntry}.");
            List<SnapshotIndexRow> rows;
            using (var read = entry.Open())
                rows = SnapshotIndexRow.Read(ReadBounded(read, Limits.MaxEntryBytes));
            var gate = new Lock();
            return new Side(id, info.IncludesPacks, rows, (path, _) =>
            {
                lock (gate)
                {
                    if (zip.GetEntry(path) is not { } document)
                        return Task.FromResult<byte[]?>(null);
                    using var read = document.Open();
                    return Task.FromResult<byte[]?>(ReadBounded(read, Limits.MaxEntryBytes));
                }
            }, zip);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A loaded model's rows (the working model's, or a folder's): the store's documents and their hashes (after a stat rescan); every
    /// other document is read.
    /// </summary>
    private async Task<Side> StoreSideAsync(ModelStore store, string name, bool includePacks, CancellationToken ct)
    {
        var model = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var live = store.Documents;
        var paths = ScopePaths(live, includePacks);
        var modelPaths = ReferenceEquals(store, _store) ? _paths : new ModelPaths(store.Options);
        var known = new Dictionary<string, Model.ElementDocument>(StringComparer.Ordinal);
        foreach (var document in model.Documents)
            known[modelPaths.FromRepoPath(document.Path)] = document;
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
        return new Side(name, includePacks, [.. rows.OfType<SnapshotIndexRow>()], live.ReadAsync);
    }

    /// <summary>One side of a comparison; disposing it closes what it holds open (a snapshot's archive).</summary>
    private sealed record Side(string Name, bool IncludesPacks, List<SnapshotIndexRow> Rows, Func<string, CancellationToken, Task<byte[]?>> Read,
        IDisposable? Holds = null) : IDisposable
    {
        public void Dispose() => Holds?.Dispose();
    }

    /// <summary>A changed element, its rows on each side, and its document once read (the <c>to</c> side's, the <c>from</c> side's when
    /// removed).</summary>
    private sealed record Entry(SnapshotElementChange Change, SnapshotIndexRow? Before, SnapshotIndexRow? After)
    {
        public JsonElement? Document { get; init; }
    }
}
