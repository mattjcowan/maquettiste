using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>
/// One page of a seed's rows (next-phase-2-design.md section 3.4; MCP <c>preview_seed_rows</c>, <c>maquettiste seed preview</c>): the rows
/// as the seed writes them or its rows script returns them, or, with a database, as that database receives them.
/// </summary>
/// <param name="Seed">The seed id.</param>
/// <param name="Script">The seed's rows script (<c>rowsFrom.script</c>, a path under <c>extensions/</c>), or <see langword="null"/>.</param>
/// <param name="Database">The id of the database the rows are given for, or <see langword="null"/> for the rows as written.</param>
/// <param name="Rows">The page's rows, in the seed's order.</param>
/// <param name="Total">The number of rows in every page.</param>
/// <param name="Next">The cursor of the next page, or <see langword="null"/> on the last one.</param>
/// <param name="Diagnostics">
/// What kept rows out: the script's findings (MQ7116, MQ7127, MQ7128, MQ5005), a failed script of a seed the rows name, or the errors
/// that keep the model from resolving (with a database).
/// </param>
public sealed record SeedRowsPreview(string Seed, string? Script, string? Database, IReadOnlyList<SeedRowsPreviewRow> Rows, int Total, string? Next,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One row of a <see cref="SeedRowsPreview"/>.</summary>
/// <param name="Id">The row id: the stored one, or the one derived from the seed and the key for a scripted row.</param>
/// <param name="Key">
/// The row key: in seed terms without a database (empty when the seed's target has no row key), else in database terms
/// (<see cref="RDataRow.Key"/>, null where the database fills a part).
/// </param>
/// <param name="Values">
/// The cells: by the seed's column names, in column order, as written, without a database; with one, by the table's column names in
/// database terms (stable ids, the values of foreign-key entries), ordinal.
/// </param>
/// <param name="Refs">With a database, the row each foreign-key entry names (<see cref="RDataRow.Refs"/>), by foreign key name; else empty.</param>
public sealed record SeedRowsPreviewRow(string Id, IReadOnlyList<object?> Key, IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<SeedRowsPreviewRef> Refs);

/// <summary>The row a foreign-key entry of a table seed names, in a <see cref="SeedRowsPreviewRow"/>.</summary>
/// <param name="ForeignKey">The foreign key's name.</param>
/// <param name="Table">The referenced table, schema-qualified when it has a schema.</param>
/// <param name="Key">The entry's cell in seed terms: the referenced row's key, one value per key column.</param>
/// <param name="Found">Whether a row of the referenced table has the key (MQ7111 when not).</param>
/// <param name="Unknown">The foreign key's columns whose referenced value the database fills, which a pack looks up by the key.</param>
public sealed record SeedRowsPreviewRef(string ForeignKey, string Table, IReadOnlyList<object?> Key, bool Found, IReadOnlyList<string> Unknown);

public sealed partial class ModelStore
{
    /// <summary>
    /// One page of a seed's rows (next-phase-2-design.md section 3.4). Without <paramref name="database"/>: the rows as the seed writes them
    /// (inline or in its CSV rows file) or as its rows script returns them, with their ids and their keys in seed terms; only that seed's
    /// script runs (through the engine's rows cache). With <paramref name="database"/>: the rows that database receives from the seed, in
    /// database terms (stable ids, the values foreign-key entries fill, their refs), from the per-seed builder the resolved model's seed
    /// tables share, never from the seed tables themselves, so only the seed's script and the scripts of the seeds its foreign-key entries
    /// name, transitively, run. A model whose errors keep it from resolving answers no rows and those errors. Never the whole model's rows.
    /// </summary>
    /// <param name="seedId">The seed id.</param>
    /// <param name="database">A database id or name, or <see langword="null"/> for the rows as written.</param>
    /// <param name="cursor">The previous page's <see cref="SeedRowsPreview.Next"/>, or <see langword="null"/> for the first page.</param>
    /// <param name="limit">The page size, 1 to <see cref="ModelPages.MaxLimit"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page, or <see langword="null"/> when no seed has the id.</returns>
    /// <exception cref="FormatException">The cursor is not one a page returned.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is out of range.</exception>
    /// <exception cref="ArgumentException">The database names no database of the model.</exception>
    public async Task<SeedRowsPreview?> PreviewSeedRowsAsync(string seedId, string? database, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedId);
        ModelPages.CheckLimit(limit);
        if (!string.IsNullOrEmpty(cursor))
            ModelPages.ReadCursor(cursor);
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Element>(seedId) is not Seed seed)
            return null;
        var script = seed.RowsFrom?.Script;
        if (string.IsNullOrEmpty(database))
            return WrittenRows(snapshot, seed, cursor, limit, ct);

        var target = snapshot.ElementsOf(ElementKind.Database).OfType<Database>().Where(d => string.Equals(d.Id, database, StringComparison.Ordinal))
            .Concat(snapshot.ElementsOf(ElementKind.Database).OfType<Database>().Where(d => string.Equals(d.Name, database, StringComparison.Ordinal))
                .OrderBy(d => d.Id, StringComparer.Ordinal))
            .FirstOrDefault() ?? throw new ArgumentException($"No database has the id or name '{database}'.", nameof(database));
        var shared = await ResolvedAsync(snapshot, ct).ConfigureAwait(false);
        if (shared.Model is not { } resolved)
            return new SeedRowsPreview(seed.Id, script, target.Id, [], 0, null, [.. shared.Report.Diagnostics.Where(Generation.Outcomes.BlocksResolution)]);

        var diagnostics = new List<Diagnostic>();
        if (script is not null)
        {
            // The seed's own findings (MQ7128, MQ5005); the resolver's read of its rows below reuses this run from the rows cache.
            var scripted = SeedScripts.Evaluate(snapshot, seed, _services.Scripts, _services.SeedRows, ct);
            diagnostics.AddRange(scripted.Diagnostics);
            if (scripted.Failure is not null)
                return new SeedRowsPreview(seed.Id, script, target.Id, [], 0, null, diagnostics);
        }

        IReadOnlyList<RDataRow> rows = [];
        if (resolved.Databases.FirstOrDefault(d => string.Equals(d.Id, target.Id, StringComparison.Ordinal)) is { } rdb && resolved.Find(seed.Id) is RSeed rseed)
        {
            try
            {
                rows = rdb.DataRowsOf(rseed);
            }
            catch (Scripting.ScriptErrorException ex)
            {
                // A seed the rows name through a foreign-key entry takes its rows from a script that fails.
                diagnostics.Add(ex.Diagnostic);
            }
        }

        var (page, next) = RowPage(rows, r => r.Id, cursor, limit);
        return new SeedRowsPreview(seed.Id, script, target.Id, [.. page.Select(DatabaseRow)], rows.Count, next, diagnostics);
    }

    /// <summary>The rows as the seed writes them or its script returns them, with keys in seed terms.</summary>
    private SeedRowsPreview WrittenRows(ModelSnapshot snapshot, Seed seed, string? cursor, int limit, CancellationToken ct)
    {
        var script = seed.RowsFrom?.Script;
        IReadOnlyList<SeedRow> rows = seed.Rows;
        IReadOnlyList<Diagnostic> diagnostics = [];
        if (script is not null)
        {
            var scripted = SeedScripts.Evaluate(snapshot, seed, _services.Scripts, _services.SeedRows, ct);
            rows = scripted.Rows;
            diagnostics = scripted.Diagnostics;
        }

        var names = SeedScripts.ColumnNames(snapshot, seed);
        var key = SeedScripts.KeyPositions(snapshot, seed) ?? [];
        var (page, next) = RowPage(rows, r => r.Id, cursor, limit);
        var result = new List<SeedRowsPreviewRow>(page.Count);
        foreach (var row in page)
        {
            object? Cell(int at) => at < row.Values.Count ? PreviewValue(row.Values[at]) : null;
            var values = new OrderedDictionary<string, object?>(names.Count, StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++)
                values.TryAdd(names[i], Cell(i));
            result.Add(new SeedRowsPreviewRow(row.Id, [.. key.Select(Cell)], values, []));
        }

        return new SeedRowsPreview(seed.Id, script, null, result, rows.Count, next, diagnostics);
    }

    /// <summary>One page of rows in their own order: the cursor holds the position of the page's last row.</summary>
    private static (IReadOnlyList<T> Items, string? Next) RowPage<T>(IReadOnlyList<T> rows, Func<T, string> id, string? cursor, int limit)
    {
        var (items, next) = ModelPages.Page(rows.Select((row, at) => (Row: row, At: at)),
            r => ("row", r.At.ToString("D10", CultureInfo.InvariantCulture), id(r.Row)), cursor, limit);
        return ([.. items.Select(r => r.Row)], next);
    }

    private static SeedRowsPreviewRow DatabaseRow(RDataRow row) => new(
        row.Id,
        [.. row.Key.Select(Plainly)],
        row.Values.ToImmutableSortedDictionary(p => p.Key, p => Plainly(p.Value), StringComparer.Ordinal),
        [.. row.Refs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new SeedRowsPreviewRef(
            p.Value.ForeignKey.Name,
            p.Value.Table.Schema is { Length: > 0 } schema ? schema + "." + p.Value.Table.Name : p.Value.Table.Name,
            [.. p.Value.Key.Select(Plainly)],
            p.Value.Row is not null,
            [.. p.Value.Unknown.Select(c => c.Name)]))]);

    /// <summary>A cell as written, as a plain value: text, a whole number as <see cref="long"/>, else <see cref="decimal"/> when it fits, else
    /// <see cref="double"/>, a boolean, null, a list or an ordinal map.</summary>
    private static object? PreviewValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? whole : value.TryGetDecimal(out var exact) ? exact : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => value.EnumerateArray().Select(PreviewValue).ToImmutableArray(),
        JsonValueKind.Object => value.EnumerateObject().ToImmutableSortedDictionary(p => p.Name, p => PreviewValue(p.Value), StringComparer.Ordinal),
        _ => null,
    };

    /// <summary>A value in database terms as plain JSON-ready data: a resolved object becomes its id, lists and maps are walked.</summary>
    private static object? Plainly(object? value) => value switch
    {
        null or string or bool or long or int or decimal or double => value,
        JsonElement json => PreviewValue(json),
        Resolution.IResolvedObject resolved => resolved.Id,
        IReadOnlyDictionary<string, object?> map => map.ToImmutableSortedDictionary(p => p.Key, p => Plainly(p.Value), StringComparer.Ordinal),
        IEnumerable list => list.Cast<object?>().Select(Plainly).ToImmutableArray(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
