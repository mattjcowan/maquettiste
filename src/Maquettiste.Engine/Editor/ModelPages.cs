using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>The index filters every bulk read shares; each one that is set must match (AND).</summary>
/// <param name="Kind">Only this kind (an index kind name such as <c>entity</c>).</param>
/// <param name="Package">Only elements directly in this package, by package id or package name (ignoring case).</param>
/// <param name="Tag">Only elements with this tag key.</param>
/// <param name="Category">Only elements in this category (its id).</param>
/// <param name="Stereotype">Only elements with this stereotype key.</param>
/// <param name="Query">Only elements whose name contains this text, ignoring case.</param>
public sealed record ElementFilter(string? Kind = null, string? Package = null, string? Tag = null, string? Category = null, string? Stereotype = null, string? Query = null)
{
    /// <summary>Whether no filter is set.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Kind) && string.IsNullOrEmpty(Package) && string.IsNullOrEmpty(Tag) && string.IsNullOrEmpty(Category)
        && string.IsNullOrEmpty(Stereotype) && string.IsNullOrEmpty(Query);
}

/// <summary>One page of index rows (<see cref="ModelPages.PageIndex"/>).</summary>
/// <param name="Items">The rows, by (kind, name, id).</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> on the last one.</param>
public sealed record IndexPage(IReadOnlyList<ElementSummary> Items, string? Next);

/// <summary>One document of a bulk read.</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Hash">The file hash (the element's ETag; pass it as the expected hash of a save).</param>
/// <param name="Json">The canonical document, as <c>getElement</c> returns it, trimmed to the requested fields (<c>id</c> and <c>kind</c>
/// always kept).</param>
public sealed record ElementPageItem(string Id, string Kind, string Path, string Hash, JsonElement Json);

/// <summary>One page of a bulk document read (<see cref="ModelPages.ReadElements"/>).</summary>
/// <param name="Items">The documents, by (kind, name, id).</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> on the last one.</param>
/// <param name="Missing">The requested ids that match no element or sub-element, in request order (empty without ids).</param>
public sealed record ElementPage(IReadOnlyList<ElementPageItem> Items, string? Next, IReadOnlyList<string> Missing);

/// <summary>How many top-level elements of one kind the model holds.</summary>
/// <param name="Kind">The kind name.</param>
/// <param name="Count">The number of elements.</param>
public sealed record KindCount(string Kind, int Count);

/// <summary>The kinds directly in one package.</summary>
/// <param name="Package">The package id, or <see langword="null"/> for the elements in no package.</param>
/// <param name="Name">The package name, or <see langword="null"/> for the elements in no package.</param>
/// <param name="Count">The number of elements directly in the package.</param>
/// <param name="Kinds">The count of each kind present, by kind name.</param>
public sealed record PackageKindCounts(string? Package, string? Name, int Count, IReadOnlyList<KindCount> Kinds);

/// <summary>The kinds present in the model with their counts (<see cref="ModelPages.Kinds"/>).</summary>
/// <param name="Total">The number of top-level elements.</param>
/// <param name="Kinds">The count of each kind present, by kind name.</param>
/// <param name="Packages">With <c>by=package</c>: the counts per package, the elements in no package first, then by (name, id);
/// otherwise <see langword="null"/>.</param>
public sealed record ModelKindsResult(int Total, IReadOnlyList<KindCount> Kinds, IReadOnlyList<PackageKindCounts>? Packages);

/// <summary>
/// Bulk reads for external systems: index filters, paging with an opaque cursor, documents in pages and kind counts. Every paged read
/// orders its rows by (kind, name, id), ordinal, so the order is stable across pages; a cursor encodes the last row's position, so a
/// change between pages never fails a read: the next page starts after that position in the current model.
/// </summary>
public static class ModelPages
{
    /// <summary>The page size when none is given.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The largest page size (and the most ids one bulk read takes).</summary>
    public const int MaxLimit = 1000;

    private const string CursorVersion = "p1";

    /// <summary>The rows that match every filter that is set, in the order given.</summary>
    /// <param name="index">The index rows.</param>
    /// <param name="filter">The filters.</param>
    /// <returns>The matching rows.</returns>
    public static IReadOnlyList<ElementSummary> Filter(IReadOnlyList<ElementSummary> index, ElementFilter filter)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.IsEmpty)
            return index;
        HashSet<string>? packages = null;
        if (!string.IsNullOrEmpty(filter.Package))
        {
            packages = new HashSet<string>(StringComparer.Ordinal) { filter.Package };
            foreach (var summary in index)
            {
                if (summary.Kind == "package" && string.Equals(summary.Name, filter.Package, StringComparison.OrdinalIgnoreCase))
                    packages.Add(summary.Id);
            }
        }

        IEnumerable<ElementSummary> result = index;
        if (!string.IsNullOrEmpty(filter.Kind))
            result = result.Where(s => string.Equals(s.Kind, filter.Kind, StringComparison.Ordinal));
        if (packages is not null)
            result = result.Where(s => s.Package is not null && packages.Contains(s.Package));
        if (!string.IsNullOrEmpty(filter.Tag))
            result = result.Where(s => s.Tags.Contains(filter.Tag, StringComparer.Ordinal));
        if (!string.IsNullOrEmpty(filter.Category))
            result = result.Where(s => string.Equals(s.Category, filter.Category, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(filter.Stereotype))
            result = result.Where(s => s.Stereotypes.Contains(filter.Stereotype, StringComparer.Ordinal));
        if (!string.IsNullOrEmpty(filter.Query))
            result = result.Where(s => s.Name.Contains(filter.Query, StringComparison.OrdinalIgnoreCase));
        return [.. result];
    }

    /// <summary>One page of index rows, by (kind, name, id).</summary>
    /// <param name="rows">The (filtered) rows.</param>
    /// <param name="cursor">The previous page's <see cref="IndexPage.Next"/>, or <see langword="null"/> for the first page.</param>
    /// <param name="limit">The page size, 1 to <see cref="MaxLimit"/>.</param>
    /// <returns>The page.</returns>
    /// <exception cref="FormatException">The cursor is not one a paged read returned.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is out of range.</exception>
    public static IndexPage PageIndex(IReadOnlyList<ElementSummary> rows, string? cursor, int limit)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var (items, next) = Page(rows, s => (s.Kind, s.Name, s.Id), cursor, limit);
        return new IndexPage(items, next);
    }

    /// <summary>
    /// One page of documents: the elements the ids name (a sub-element id names the element that holds it), or every element, narrowed
    /// by the filters, by (kind, name, id), each trimmed to <paramref name="fields"/> when given.
    /// </summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <param name="ids">Element or sub-element ids, at most <see cref="MaxLimit"/>; <see langword="null"/> or empty for every element.</param>
    /// <param name="filter">The index filters.</param>
    /// <param name="fields">The top-level members to keep in each document (<c>id</c> and <c>kind</c> are always kept); <see langword="null"/>
    /// or empty for the whole document.</param>
    /// <param name="cursor">The previous page's <see cref="ElementPage.Next"/>, or <see langword="null"/>.</param>
    /// <param name="limit">The page size, 1 to <see cref="MaxLimit"/>.</param>
    /// <returns>The page.</returns>
    /// <exception cref="FormatException">The cursor is not one a paged read returned.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is out of range.</exception>
    /// <exception cref="ArgumentException">More than <see cref="MaxLimit"/> ids.</exception>
    public static ElementPage ReadElements(ModelSnapshot snapshot, IReadOnlyList<string>? ids, ElementFilter filter, IReadOnlyList<string>? fields, string? cursor, int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(filter);
        CheckLimit(limit);
        IReadOnlyList<ElementSummary> rows = snapshot.Summaries();
        var missing = new List<string>();
        if (ids is { Count: > 0 })
        {
            if (ids.Count > MaxLimit)
                throw new ArgumentException($"At most {MaxLimit} ids can be read at once; {ids.Count} were given.", nameof(ids));
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (snapshot.GetDocument(id) is { } document)
                    wanted.Add(document.Element.Id);
                else
                    missing.Add(id);
            }

            rows = [.. rows.Where(s => wanted.Contains(s.Id))];
        }

        rows = Filter(rows, filter);
        var keep = fields is { Count: > 0 } ? new HashSet<string>(fields.Where(f => f.Length > 0), StringComparer.Ordinal) { "id", "kind" } : null;
        var (page, next) = Page(rows, s => (s.Kind, s.Name, s.Id), cursor, limit);
        var items = new List<ElementPageItem>(page.Count);
        foreach (var summary in page)
        {
            if (snapshot.GetDocument(summary.Id) is not { } document)
                continue;
            items.Add(new ElementPageItem(summary.Id, summary.Kind, document.Path, document.Hash, keep is null ? document.Json : Trim(document.Json, keep)));
        }

        return new ElementPage(items, next, missing);
    }

    /// <summary>The kinds present with their counts, and with <paramref name="byPackage"/> the same per package.</summary>
    /// <param name="index">The index rows.</param>
    /// <param name="byPackage">Whether to add the counts per package.</param>
    /// <returns>The counts.</returns>
    public static ModelKindsResult Kinds(IReadOnlyList<ElementSummary> index, bool byPackage)
    {
        ArgumentNullException.ThrowIfNull(index);
        var kinds = Count(index);
        if (!byPackage)
            return new ModelKindsResult(index.Count, kinds, null);
        var names = index.Where(s => s.Kind == "package").ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        var packages = index.GroupBy(s => s.Package ?? "", StringComparer.Ordinal)
            .Select(g => new PackageKindCounts(g.Key.Length == 0 ? null : g.Key, g.Key.Length == 0 ? null : names.GetValueOrDefault(g.Key) ?? g.Key, g.Count(), Count([.. g])))
            .OrderBy(p => p.Package is null ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Package, StringComparer.Ordinal)
            .ToList();
        return new ModelKindsResult(index.Count, kinds, packages);
    }

    /// <summary>Parses a page size: absent is <see cref="DefaultLimit"/>; otherwise a whole number from 1 to <see cref="MaxLimit"/>.</summary>
    /// <param name="text">The text, or <see langword="null"/>.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="error">Why the text is not a page size.</param>
    /// <returns>Whether the text is a page size.</returns>
    public static bool TryParseLimit(string? text, out int limit, out string? error)
    {
        error = null;
        limit = DefaultLimit;
        if (string.IsNullOrEmpty(text))
            return true;
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > MaxLimit)
        {
            error = $"limit must be a whole number from 1 to {MaxLimit}, not '{text}'.";
            return false;
        }

        return true;
    }

    /// <summary>Splits a comma-separated list (empty items dropped), or <see langword="null"/> when there is none.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The items.</returns>
    public static IReadOnlyList<string>? SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>One page of rows by an ordinal (kind, name, id) key, after the cursor's position.</summary>
    internal static (IReadOnlyList<T> Items, string? Next) Page<T>(IEnumerable<T> rows, Func<T, (string Kind, string Name, string Id)> key, string? cursor, int limit)
    {
        CheckLimit(limit);
        var ordered = rows.Select(r => (Row: r, Key: key(r))).OrderBy(r => r.Key.Kind, StringComparer.Ordinal).ThenBy(r => r.Key.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Key.Id, StringComparer.Ordinal);
        IEnumerable<(T Row, (string Kind, string Name, string Id) Key)> rest = ordered;
        if (!string.IsNullOrEmpty(cursor))
        {
            var after = ReadCursor(cursor);
            rest = ordered.Where(r => Compare(r.Key, after) > 0);
        }

        var page = rest.Take(limit + 1).ToList();
        if (page.Count <= limit)
            return ([.. page.Select(r => r.Row)], null);
        var last = page[limit - 1].Key;
        return ([.. page.Take(limit).Select(r => r.Row)], WriteCursor(last));
    }

    /// <summary>Throws when a page size is out of range.</summary>
    internal static void CheckLimit(int limit)
    {
        if (limit is < 1 or > MaxLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"limit must be from 1 to {MaxLimit}.");
    }

    /// <summary>The cursor of a position: an opaque base64url text.</summary>
    internal static string WriteCursor((string Kind, string Name, string Id) key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(CursorVersion + "\0" + key.Kind + "\0" + key.Name + "\0" + key.Id));

    /// <summary>The position a cursor encodes.</summary>
    /// <exception cref="FormatException">The text is not a cursor a paged read returned.</exception>
    internal static (string Kind, string Name, string Id) ReadCursor(string cursor)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (FormatException)
        {
            throw new FormatException("cursor is not a cursor a paged read returned; pass next from the previous page as it is.");
        }

        var parts = text.Split('\0');
        if (parts.Length != 4 || parts[0] != CursorVersion)
            throw new FormatException("cursor is not a cursor a paged read returned; pass next from the previous page as it is.");
        return (parts[1], parts[2], parts[3]);
    }

    private static int Compare((string Kind, string Name, string Id) a, (string Kind, string Name, string Id) b)
    {
        var c = string.CompareOrdinal(a.Kind, b.Kind);
        if (c == 0)
            c = string.CompareOrdinal(a.Name, b.Name);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    private static KindCount[] Count(IReadOnlyList<ElementSummary> rows) =>
        [.. rows.GroupBy(s => s.Kind, StringComparer.Ordinal).Select(g => new KindCount(g.Key, g.Count())).OrderBy(k => k.Kind, StringComparer.Ordinal)];

    private static JsonElement Trim(JsonElement json, HashSet<string> keep)
    {
        if (json.ValueKind != JsonValueKind.Object)
            return json;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in json.EnumerateObject())
            {
                if (keep.Contains(property.Name))
                    property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
