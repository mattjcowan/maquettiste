using Maquettiste.Engine.Hashing;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>The result of <see cref="ModelReads.ReadElementsAsync"/> (E5b, explorer-redesign.md section 4.1).</summary>
/// <param name="Elements">The documents, in the order their first id was asked for, each once.</param>
/// <param name="Missing">The ids no element or sub-element has, in request order.</param>
public sealed record ElementReadResult(IReadOnlyList<ElementDocument> Elements, IReadOnlyList<string> Missing);

/// <summary>Reads for the explorer at scale: many elements from one snapshot (E5b) and the index's ETag (E5e).</summary>
public static class ModelReads
{
    /// <summary>The most ids <see cref="ReadElementsAsync"/> takes in one call.</summary>
    public const int MaxReadIds = 200;

    /// <summary>
    /// The format of the index rows, hashed into <see cref="IndexTag"/>: changed whenever <see cref="ElementSummary"/> gains or loses a
    /// member, so a client holding an index of an older shape never gets 304 for it.
    /// </summary>
    internal const string IndexFormat = "maquettiste-index/e7";

    /// <summary>
    /// Reads the documents of up to <see cref="MaxReadIds"/> element or sub-element ids from one snapshot (no rescan): a sub-element id
    /// reads the document that holds it, and each document comes back once.
    /// </summary>
    /// <param name="store">The model store.</param>
    /// <param name="ids">The ids.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The documents and the ids that matched nothing.</returns>
    /// <exception cref="ArgumentException">More than <see cref="MaxReadIds"/> ids, or a null id.</exception>
    public static async Task<ElementReadResult> ReadElementsAsync(this ModelStore store, IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > MaxReadIds)
            throw new ArgumentException($"At most {MaxReadIds} ids can be read at once; {ids.Count} were given.", nameof(ids));
        if (store.Current is null)
            await store.LoadAsync(ct).ConfigureAwait(false);
        var snapshot = store.Current!;
        var documents = new List<ElementDocument>(ids.Count);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (id is null)
                throw new ArgumentException("An id is null.", nameof(ids));
            var document = snapshot.GetDocument(id);
            if (document is null)
                missing.Add(id);
            else if (seen.Add(document.Element.Id))
                documents.Add(document);
        }

        return new ElementReadResult(documents, missing);
    }

    /// <summary>
    /// The index's ETag (E5e): a hash of the index format and of every row's id, file hash and path, in index order. A row is a function
    /// of its file's bytes and path, so the tag changes exactly when a row does, and is the same across processes for the same files.
    /// </summary>
    /// <param name="summaries">The index.</param>
    /// <returns>A lowercase hex SHA-256.</returns>
    public static string IndexTag(IReadOnlyList<ElementSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        using var hash = new HashBuilder();
        hash.Add(IndexFormat).Add(summaries.Count);
        foreach (var summary in summaries)
            hash.Add(summary.Id).Add(summary.Hash).Add(summary.Path);
        return hash.Finish();
    }

    private static readonly ConditionalWeakTable<ModelSnapshot, ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>> Tables = new();

    /// <summary>
    /// The per-locale display-name table (reference-types-seeds-localization.md section 3.8): for every index row, the display name the
    /// locale's fallback chain gives (a translation, else the default display name, else the name). Built once per snapshot and locale
    /// and kept beside it, so an index request joins it and parses no shard.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="locale">A declared locale.</param>
    /// <returns>Element id to display name.</returns>
    public static IReadOnlyDictionary<string, string> DisplayNames(ModelSnapshot snapshot, string locale)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(locale);
        return Tables.GetValue(snapshot, _ => new(StringComparer.Ordinal)).GetOrAdd(locale, l =>
        {
            var l10n = snapshot.Localization;
            var chain = l10n.ChainOf(l);
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var summary in snapshot.Summaries())
            {
                string? text = null;
                foreach (var consulted in chain)
                {
                    if (!l10n.IsTranslated(consulted))
                        break;
                    if ((text = l10n.Text(consulted, summary.Id, Localization.LocalizationIndex.DisplayNameField)) is not null)
                        break;
                }

                table[summary.Id] = text ?? summary.DisplayName ?? summary.Name;
            }

            return table;
        });
    }

    /// <summary>The index with <c>displayName</c> filled from <paramref name="locale"/>'s chain (rows a translation answers change).</summary>
    /// <param name="snapshot">The snapshot the index comes from.</param>
    /// <param name="summaries">The index.</param>
    /// <param name="locale">A declared locale.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<ElementSummary> Localize(ModelSnapshot snapshot, IReadOnlyList<ElementSummary> summaries, string locale)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var table = DisplayNames(snapshot, locale);
        return [.. summaries.Select(s => table.TryGetValue(s.Id, out var text) && text != (s.DisplayName ?? s.Name) ? s with { DisplayName = text } : s)];
    }

    /// <summary>
    /// The ETag of the index in a locale: <see cref="IndexTag(IReadOnlyList{ElementSummary})"/>'s inputs plus the locale, its chain and
    /// the path and dependency hash of every shard of a chain locale.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="summaries">The index (unlocalized).</param>
    /// <param name="locale">The locale.</param>
    /// <returns>A lowercase hex SHA-256.</returns>
    public static string LocalizedIndexTag(ModelSnapshot snapshot, IReadOnlyList<ElementSummary> summaries, string locale)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(summaries);
        var chain = snapshot.Localization.ChainOf(locale);
        using var hash = new HashBuilder();
        hash.Add(IndexTag(summaries)).Add(locale).Add(string.Join(",", chain));
        foreach (var shard in snapshot.LocaleShards.Where(s => chain.Contains(s.FolderLocale, StringComparer.Ordinal)).OrderBy(s => s.Path, StringComparer.Ordinal))
            hash.Add(shard.Path).Add(shard.DependencyHash);
        return hash.Finish();
    }
}
