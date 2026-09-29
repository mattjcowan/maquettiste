using System.Globalization;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine;

/// <summary>One translated field as the store serves it (reference-types-seeds-localization.md section 3.9).</summary>
/// <param name="Id">The node id.</param>
/// <param name="OwnerId">The element whose file holds the node.</param>
/// <param name="Field">displayName, pluralName, label or description.</param>
/// <param name="Source">The default-locale text.</param>
/// <param name="Value">The locale's own translation, or <see langword="null"/>.</param>
/// <param name="Effective">The text the chain gives (the translation, a fallback locale's, or the default).</param>
/// <param name="State"><c>translated</c>, <c>stale</c>, <c>fallback</c> (missing here, answered by a fallback locale) or <c>missing</c>.</param>
/// <param name="ShardPath">The repo path of the shard the entry belongs in.</param>
public sealed record TranslationItem(string Id, string OwnerId, string Field, string? Source, string? Value, string? Effective, string State, string ShardPath);

/// <summary>Translations of one locale with the ETags (content hashes) of the shards they come from.</summary>
/// <param name="Locale">The locale.</param>
/// <param name="Items">The items, ordinal by (id, field order).</param>
/// <param name="ShardHashes">Repo path to content hash of each shard concerned (absent shards are omitted).</param>
public sealed record TranslationPage(string Locale, IReadOnlyList<TranslationItem> Items, IReadOnlyDictionary<string, string> ShardHashes);

/// <summary>One edit: set (<paramref name="Value"/>), remove (null) or confirm (<paramref name="Confirm"/>: rewrites the field's
/// source fingerprint, leaving the text).</summary>
/// <param name="Id">The node id.</param>
/// <param name="Field">The field.</param>
/// <param name="Value">The text, or <see langword="null"/> to remove it.</param>
/// <param name="Confirm">Confirms a stale translation.</param>
public sealed record TranslationEdit(string Id, string Field, string? Value, bool Confirm = false);

/// <summary>The result of a translation save.</summary>
/// <param name="Outcome">Saved, Conflict (a shard changed since its ETag) or Invalid.</param>
/// <param name="ShardHashes">The new hashes of the shards written (Saved) or their current hashes (Conflict).</param>
/// <param name="Diagnostics">Why it was refused.</param>
/// <param name="Changes">What changed when saved.</param>
public sealed record TranslationSaveResult(SaveOutcome Outcome, IReadOnlyDictionary<string, string> ShardHashes, IReadOnlyList<Diagnostic> Diagnostics, ChangeSet? Changes);

public sealed partial class ModelStore
{
    /// <summary>
    /// Reads the translations of a locale for one owner element (its node and sub-elements) or one shard (repo path), or every node
    /// when both are <see langword="null"/>, with the shards' ETags.
    /// </summary>
    /// <param name="locale">The locale.</param>
    /// <param name="ownerId">An owner element id, or <see langword="null"/>.</param>
    /// <param name="shardPath">A shard's repo path, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    public async Task<TranslationPage> GetTranslationsAsync(string locale, string? ownerId, string? shardPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(locale);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var l10n = snapshot.Localization;
        var paths = _paths.Value;
        var items = new List<TranslationItem>();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in l10n.Nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            if (ownerId is not null && node.OwnerId != ownerId)
                continue;
            var shard = paths.ToRepoPath(l10n.ShardPath(locale, node.Scope));
            if (shardPath is not null && shard != shardPath)
                continue;
            if (snapshot.LocaleShards.FirstOrDefault(s => s.Path == shard) is { } loaded)
                hashes[shard] = loaded.Hash;
            foreach (var field in LocalizationIndex.Fields.Where(node.Allows))
            {
                var value = l10n.Text(locale, node.Id, field);
                var source = field == LocalizationIndex.DescriptionField && node.Description is { } d && d.StartsWith("file:", StringComparison.Ordinal)
                    ? snapshot.GetDocument(node.OwnerId)?.SidecarText ?? d
                    : node.Source(field);
                string? effective = null;
                foreach (var consulted in l10n.ChainOf(locale))
                {
                    effective = l10n.IsTranslated(consulted) ? l10n.Text(consulted, node.Id, field) : source;
                    if (effective is not null)
                        break;
                }

                var state = value is null
                    ? effective is not null && effective != source ? "fallback" : "missing"
                    : l10n.StateOf(locale, node, field) == TranslationState.Stale ? "stale" : "translated";
                if (value is null && source is null)
                    continue; // nothing to translate: an optional field the default leaves empty
                items.Add(new TranslationItem(node.Id, node.OwnerId, field, source, value, effective, state, shard));
            }
        }

        return new TranslationPage(locale, items, hashes);
    }

    /// <summary>
    /// Writes translations of one locale in one atomic save: each entry goes to its node's shard (an entry found in another shard
    /// moves there), with the source fingerprint of the default text it was made from. <paramref name="expected"/> maps shard repo
    /// paths to the ETags the caller loaded: a shard that changed since is a conflict and nothing is written.
    /// </summary>
    /// <param name="locale">A declared, non-default locale.</param>
    /// <param name="edits">The edits.</param>
    /// <param name="expected">Shard repo path to expected content hash (an empty string for a shard expected not to exist).</param>
    /// <param name="source">Who made the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    public async Task<TranslationSaveResult> SaveTranslationsAsync(string locale, IReadOnlyList<TranslationEdit> edits,
        IReadOnlyDictionary<string, string> expected, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(expected);
        await LoadedAsync(ct).ConfigureAwait(false);
        var notifications = new List<ChangeSet>();
        TranslationSaveResult result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await SaveTranslationsLockedAsync(locale, edits, expected, source, notifications, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var changeSet in notifications)
            await NotifyAsync(changeSet, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<TranslationSaveResult> SaveTranslationsLockedAsync(string locale, IReadOnlyList<TranslationEdit> edits,
        IReadOnlyDictionary<string, string> expected, ChangeSource source, List<ChangeSet> notifications, CancellationToken ct)
    {
        var paths = _paths.Value;
        var snapshot = _current!;
        var l10n = snapshot.Localization;
        var diagnostics = new List<Diagnostic>();
        if (!l10n.IsTranslated(locale))
            diagnostics.Add(RuleCatalog.Create("MQ7202", $"'{locale}' is not a declared locale other than the default; it has no translations.", null, null, null));

        // Plan: shard model path -> JSON, loaded from disk once.
        var shards = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var diskHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonObject Shard(string modelPath, string scope)
        {
            if (shards.TryGetValue(modelPath, out var existing))
                return existing;
            var full = paths.FullPath(modelPath);
            JsonObject node;
            if (File.Exists(full))
            {
                var bytes = File.ReadAllBytes(full);
                diskHashes[modelPath] = ContentHash.Of(bytes);
                node = JsonNode.Parse(bytes) as JsonObject ?? ChangePlanner.NewShard(modelPath, locale, scope);
            }
            else
            {
                diskHashes[modelPath] = "";
                node = ChangePlanner.NewShard(modelPath, locale, scope);
            }

            shards[modelPath] = node;
            return node;
        }

        for (var i = 0; i < edits.Count && diagnostics.Count == 0; i++)
        {
            var edit = edits[i];
            var pointer = "/entries/" + i.ToString(CultureInfo.InvariantCulture);
            if (!l10n.Nodes.TryGetValue(edit.Id, out var node))
            {
                diagnostics.Add(RuleCatalog.Create("MQ7203", $"{edit.Id} is not a localizable node of the model.", edit.Id, null, pointer + "/id"));
                break;
            }

            if (!LocalizationIndex.Fields.Contains(edit.Field, StringComparer.Ordinal) || !node.Allows(edit.Field))
            {
                var rule = edit.Field == LocalizationIndex.PluralNameField && node.Kind == "end" ? "MQ7211" : "MQ7203";
                diagnostics.Add(RuleCatalog.Create(rule, $"A {node.Kind} has no translatable '{edit.Field}'.", edit.Id, null, pointer + "/field"));
                break;
            }

            var targetPath = l10n.ShardPath(locale, node.Scope);
            var target = Shard(targetPath, node.Scope);
            var entries = target["entries"] as JsonObject ?? new JsonObject();
            target["entries"] = entries;
            if (l10n.TryGetEntry(locale, edit.Id, out _, out var held) && held.ModelPath != targetPath && !entries.ContainsKey(edit.Id))
            {
                // The entry sits in another shard (MQ7207): it moves to its node's shard in this save.
                var from = Shard(held.ModelPath, held.Shard.Scope);
                if (from["entries"] is JsonObject old && old[edit.Id] is { } moving)
                {
                    old.Remove(edit.Id);
                    entries[edit.Id] = moving;
                }
            }

            var entry = entries[edit.Id] as JsonObject ?? new JsonObject();
            entries[edit.Id] = entry;
            var src = entry["src"] as JsonObject ?? new JsonObject();
            var fingerprint = node.Source(edit.Field) is { } text ? LocalizationIndex.SourceHash(text) : null;
            if (edit.Confirm)
            {
                if (entry[edit.Field] is null)
                {
                    diagnostics.Add(RuleCatalog.Create("MQ7203", $"There is no '{locale}' {edit.Field} of {edit.Id} to confirm.", edit.Id, null, pointer + "/confirm"));
                    break;
                }
            }
            else if (edit.Value is null || edit.Value.Length == 0)
            {
                entry.Remove(edit.Field);
                src.Remove(edit.Field);
                fingerprint = null;
            }
            else
            {
                entry[edit.Field] = edit.Value;
            }

            if (fingerprint is not null)
                src[edit.Field] = fingerprint;
            if (src.Count > 0)
                entry["src"] = src;
            else
                entry.Remove("src");
            if (!entry.Any(kv => kv.Key != "src"))
                entries.Remove(edit.Id);
        }

        if (diagnostics.Count > 0)
            return new TranslationSaveResult(SaveOutcome.Invalid, new Dictionary<string, string>(), diagnostics, null);
        return await CommitShardsLockedAsync(shards, diskHashes, expected, source, notifications, ct).ConfigureAwait(false);
    }

    /// <summary>Writes planned shards (a shard left without entries is deleted) in one atomic set, after checking the ETags the caller read.</summary>
    private async Task<TranslationSaveResult> CommitShardsLockedAsync(SortedDictionary<string, JsonObject> shards, Dictionary<string, string> diskHashes,
        IReadOnlyDictionary<string, string> expected, ChangeSource source, List<ChangeSet> notifications, CancellationToken ct)
    {
        var paths = _paths.Value;

        // ETags: every shard the caller names must still have the hash it loaded.
        var conflicts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (modelPath, diskHash) in diskHashes)
        {
            if (expected.TryGetValue(paths.ToRepoPath(modelPath), out var loaded) && !string.Equals(loaded, diskHash, StringComparison.Ordinal))
                conflicts[paths.ToRepoPath(modelPath)] = diskHash;
        }

        if (conflicts.Count > 0)
        {
            var refreshed = await ReloadAsync([.. conflicts.Keys], false, ChangeSource.Disk, ct).ConfigureAwait(false);
            if (!refreshed.IsEmpty)
                notifications.Add(refreshed);
            return new TranslationSaveResult(SaveOutcome.Conflict, conflicts, [], null);
        }

        var writes = new List<(string Path, byte[] Bytes)>();
        var deletes = new List<string>();
        foreach (var (modelPath, node) in shards)
        {
            if (node["entries"] is JsonObject { Count: > 0 })
            {
                var bytes = _services.Json.Write(node, "locale.json", paths.ToRepoPath(modelPath));
                if (diskHashes[modelPath] != ContentHash.Of(bytes))
                    writes.Add((paths.FullPath(modelPath), bytes));
            }
            else if (diskHashes[modelPath].Length > 0)
            {
                deletes.Add(paths.FullPath(modelPath));
            }
        }

        if (writes.Count == 0 && deletes.Count == 0)
            return new TranslationSaveResult(SaveOutcome.Saved, Hashes(_current!, shards.Keys), [], ChangeSet.Empty(source));
        var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
        var failure = await new AtomicFileSet(_services.EnginePaths, paths.ModelRoot).ApplyAsync(writes, deletes, batchId, ct).ConfigureAwait(false);
        if (failure is { Refused: true })
            return new TranslationSaveResult(SaveOutcome.Invalid, new Dictionary<string, string>(),
                [RuleCatalog.Create("MQ6004", $"The model write to {failure.Path} was refused: {failure.Reason}", null, null, null)], null);
        if (failure is not null)
            throw new IOException($"The translations could not be written ({failure.Path}); nothing was changed. {failure.Reason}");

        var changes = await ReloadAsync([.. shards.Keys.Select(paths.ToRepoPath)], false, source, CancellationToken.None).ConfigureAwait(false);
        if (!changes.IsEmpty)
            notifications.Add(changes);
        return new TranslationSaveResult(SaveOutcome.Saved, Hashes(_current!, shards.Keys), [], changes);
    }

    private IReadOnlyDictionary<string, string> Hashes(ModelSnapshot snapshot, IEnumerable<string> modelPaths)
    {
        var wanted = modelPaths.Select(_paths.Value.ToRepoPath).ToHashSet(StringComparer.Ordinal);
        return snapshot.LocaleShards.Where(s => wanted.Contains(s.Path)).ToDictionary(s => s.Path, s => s.Hash, StringComparer.Ordinal);
    }
}
