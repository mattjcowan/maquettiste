using System.Text.Json.Nodes;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>One orphan translation (MQ7203) found by <see cref="ModelStore.PruneTranslationsAsync"/>.</summary>
/// <param name="Locale">The locale.</param>
/// <param name="Id">The entry id.</param>
/// <param name="Field">The field, or <see langword="null"/> when the whole entry is an orphan (its id names no localizable node).</param>
/// <param name="Shard">The shard repo path.</param>
public sealed record OrphanTranslation(string Locale, string Id, string? Field, string Shard);

/// <summary>The result of a prune.</summary>
/// <param name="Outcome">Saved (or nothing to do), Conflict (a shard changed while pruning) or Invalid (the write was refused).</param>
/// <param name="Orphans">The orphans found, ordinal by locale, id and field.</param>
/// <param name="Applied">Whether they were removed.</param>
/// <param name="Diagnostics">Why the write was refused.</param>
public sealed record TranslationPruneResult(SaveOutcome Outcome, IReadOnlyList<OrphanTranslation> Orphans, bool Applied, IReadOnlyList<Diagnostics.Diagnostic> Diagnostics);

public sealed partial class ModelStore
{
    /// <summary>
    /// Finds the orphan translations (MQ7203: an entry whose id names no localizable node, or a field its node does not have) of every
    /// translated locale and, unless <paramref name="dryRun"/>, removes them in one atomic save checked against the shards as loaded.
    /// </summary>
    /// <param name="dryRun">Only report.</param>
    /// <param name="source">Who makes the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The orphans and the outcome.</returns>
    public async Task<TranslationPruneResult> PruneTranslationsAsync(bool dryRun, ChangeSource source, CancellationToken ct)
    {
        await LoadedAsync(ct).ConfigureAwait(false);
        var notifications = new List<ChangeSet>();
        TranslationPruneResult result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await PruneLockedAsync(dryRun, source, notifications, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var changeSet in notifications)
            await NotifyAsync(changeSet, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<TranslationPruneResult> PruneLockedAsync(bool dryRun, ChangeSource source, List<ChangeSet> notifications, CancellationToken ct)
    {
        var l10n = _current!.Localization;
        var orphans = new List<(OrphanTranslation Orphan, LocaleShardDocument Shard)>();
        foreach (var locale in l10n.Locales.Where(l10n.IsTranslated).Order(StringComparer.Ordinal))
        {
            foreach (var (id, entry, shard) in l10n.EntriesOf(locale))
            {
                if (!l10n.Nodes.TryGetValue(id, out var node))
                {
                    orphans.Add((new OrphanTranslation(locale, id, null, shard.Path), shard));
                    continue;
                }

                foreach (var field in LocalizationIndex.Fields.Where(f => LocalizationIndex.Has(entry, f) && !node.Allows(f)))
                {
                    if (field == LocalizationIndex.PluralNameField && node.Kind == "end")
                        continue; // MQ7211, not an orphan.
                    orphans.Add((new OrphanTranslation(locale, id, field, shard.Path), shard));
                }
            }
        }

        var found = orphans.Select(o => o.Orphan).ToList();
        if (dryRun || found.Count == 0)
            return new TranslationPruneResult(SaveOutcome.Saved, found, false, []);

        var paths = _paths.Value;
        var shards = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var diskHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (orphan, shard) in orphans)
        {
            if (!shards.TryGetValue(shard.ModelPath, out var json))
            {
                var bytes = await File.ReadAllBytesAsync(paths.FullPath(shard.ModelPath), ct).ConfigureAwait(false);
                diskHashes[shard.ModelPath] = ContentHash.Of(bytes);
                expected[shard.Path] = shard.Hash;
                shards[shard.ModelPath] = json = JsonNode.Parse(bytes) as JsonObject ?? new JsonObject();
            }

            if (json["entries"] is not JsonObject entries)
                continue;
            if (orphan.Field is null)
            {
                entries.Remove(orphan.Id);
                continue;
            }

            if (entries[orphan.Id] is not JsonObject entry)
                continue;
            entry.Remove(orphan.Field);
            if (entry["src"] is JsonObject src)
            {
                src.Remove(orphan.Field);
                if (src.Count == 0)
                    entry.Remove("src");
            }

            if (!entry.Any(kv => kv.Key != "src"))
                entries.Remove(orphan.Id);
        }

        var saved = await CommitShardsLockedAsync(shards, diskHashes, expected, source, notifications, ct).ConfigureAwait(false);
        return new TranslationPruneResult(saved.Outcome, found, saved.Outcome == SaveOutcome.Saved, saved.Diagnostics);
    }
}
