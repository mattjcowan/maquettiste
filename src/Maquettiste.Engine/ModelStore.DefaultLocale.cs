using System.Globalization;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>One text swapped by <see cref="ModelStore.ChangeDefaultLocaleAsync"/>.</summary>
/// <param name="Id">The node id.</param>
/// <param name="Field">displayName, pluralName, label or description.</param>
/// <param name="Before">The old default text, which becomes the old default locale's translation (<see langword="null"/>: it had none).</param>
/// <param name="After">The new locale's translation, which becomes the text in the element file.</param>
public sealed record DefaultLocaleMove(string Id, string Field, string? Before, string After);

/// <summary>The result of a default-locale change.</summary>
/// <param name="Outcome">Saved, Conflict (a file changed behind the loaded model; nothing written) or Invalid (the write was refused).</param>
/// <param name="From">The old default locale.</param>
/// <param name="To">The new default locale.</param>
/// <param name="Moves">The swapped texts, ordinal by id and field.</param>
/// <param name="Skipped">Translations left where they are (<c>id/field: reason</c>): orphans and sidecar descriptions.</param>
/// <param name="Applied">Whether the files were written.</param>
/// <param name="Diagnostics">Why the write was refused.</param>
public sealed record DefaultLocaleResult(SaveOutcome Outcome, string From, string To, IReadOnlyList<DefaultLocaleMove> Moves, IReadOnlyList<string> Skipped,
    bool Applied, IReadOnlyList<Diagnostic> Diagnostics);

public sealed partial class ModelStore
{
    /// <summary>
    /// Makes a declared locale the default (reference-types-seeds-localization.md section 3.2, <c>maquettiste l10n set-default</c>): each of
    /// its translations becomes the text in the element file, and the text it replaces becomes a translation of the old default locale,
    /// fingerprinted against the new text; <c>defaultLocale</c> changes and the new default's own <c>fallbacks</c> entry is dropped. The
    /// moved entries leave the new default's shards (a shard left empty is deleted). Orphans and sidecar descriptions are not moved and
    /// are reported. Other locales' translations turn stale where their source text changed. Everything is written in one atomic set.
    /// </summary>
    /// <param name="locale">A declared locale other than the default.</param>
    /// <param name="dryRun">Only report.</param>
    /// <param name="source">Who makes the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result, or <see langword="null"/> when <paramref name="locale"/> is not a declared locale other than the default.</returns>
    public async Task<DefaultLocaleResult?> ChangeDefaultLocaleAsync(string locale, bool dryRun, ChangeSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(locale);
        await LoadedAsync(ct).ConfigureAwait(false);
        var notifications = new List<ChangeSet>();
        DefaultLocaleResult? result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await ChangeDefaultLocaleLockedAsync(locale, dryRun, source, notifications, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        foreach (var changeSet in notifications)
            await NotifyAsync(changeSet, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<DefaultLocaleResult?> ChangeDefaultLocaleLockedAsync(string locale, bool dryRun, ChangeSource source, List<ChangeSet> notifications,
        CancellationToken ct)
    {
        var snapshot = _current!;
        var l10n = snapshot.Localization;
        if (!l10n.IsTranslated(locale) || l10n.Settings is not { } settings)
            return null;
        var old = settings.DefaultLocale;
        var paths = _paths.Value;
        var moves = new List<DefaultLocaleMove>();
        var skipped = new List<string>();
        var elements = new SortedDictionary<string, (JsonObject Json, ElementDocument Document)>(StringComparer.Ordinal);
        var shards = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);

        JsonObject ShardJson(string modelPath, string shardLocale, string scope)
        {
            if (shards.TryGetValue(modelPath, out var json))
                return json;
            var full = paths.FullPath(modelPath);
            var bytes = File.Exists(full) ? File.ReadAllBytes(full) : null;
            json = bytes is not null ? JsonNode.Parse(bytes) as JsonObject ?? ChangePlanner.NewShard(modelPath, shardLocale, scope) : ChangePlanner.NewShard(modelPath, shardLocale, scope);
            // The plan uses the loaded entries, so the file must still be the one the model was loaded from; a shard that was not
            // loaded (none there, or one in the default locale's folder, MQ7202) is checked against what is read here.
            var loaded = snapshot.LocaleShards.FirstOrDefault(s => string.Equals(s.ModelPath, modelPath, StringComparison.Ordinal));
            expected[modelPath] = loaded?.Hash ?? (bytes is not null ? ContentHash.Of(bytes) : "");
            shards[modelPath] = json;
            return json;
        }

        foreach (var (id, entry, shard) in l10n.EntriesOf(locale))
        {
            if (!l10n.Nodes.TryGetValue(id, out var node) || snapshot.GetDocument(node.OwnerId) is not { } document)
            {
                skipped.Add(id + ": no localizable node of the model has this id (the entry stays in the new default's locale folder, which is not loaded, MQ7202)");
                continue;
            }

            foreach (var field in LocalizationIndex.Fields.Where(f => LocalizationIndex.Has(entry, f) && node.Allows(f)))
            {
                var text = field switch
                {
                    LocalizationIndex.DisplayNameField => entry.DisplayName,
                    LocalizationIndex.PluralNameField => entry.PluralName,
                    LocalizationIndex.LabelField => entry.Label,
                    _ => entry.Description?.Text,
                };
                var before = node.Source(field);
                if (text is null || before?.StartsWith("file:", StringComparison.Ordinal) == true)
                {
                    skipped.Add(id + "/" + field + ": a description in a sidecar file is not moved (the translation stays in the new default's locale folder, which is not loaded, MQ7202; the element keeps its sidecar text)");
                    continue;
                }

                if (!elements.TryGetValue(document.Path, out var element))
                {
                    elements[document.Path] = element = (JsonNode.Parse(document.Json.GetRawText())!.AsObject(), document);
                    expected[paths.FromRepoPath(document.Path)] = document.Hash;
                }

                if (!SetText(element.Json, snapshot, node, field, text))
                {
                    skipped.Add(id + "/" + field + ": the element file has no place for this text");
                    continue;
                }

                moves.Add(new DefaultLocaleMove(id, field, before, text));
                var from = ShardJson(shard.ModelPath, locale, shard.Shard.Scope);
                if (from["entries"] is JsonObject fromEntries && fromEntries[id] is JsonObject fromEntry)
                {
                    fromEntry.Remove(field);
                    if (fromEntry["src"] is JsonObject fromSrc)
                    {
                        fromSrc.Remove(field);
                        if (fromSrc.Count == 0)
                            fromEntry.Remove("src");
                    }

                    if (!fromEntry.Any(kv => kv.Key != "src"))
                        fromEntries.Remove(id);
                }

                if (before is null)
                    continue;
                var to = ShardJson(l10n.ShardPath(old, node.Scope), old, node.Scope);
                var toEntries = to["entries"] as JsonObject ?? new JsonObject();
                to["entries"] = toEntries;
                var toEntry = toEntries[id] as JsonObject ?? new JsonObject();
                toEntries[id] = toEntry;
                toEntry[field] = before;
                var src = toEntry["src"] as JsonObject ?? new JsonObject();
                toEntry.Remove("src");
                src[field] = LocalizationIndex.SourceHash(text);
                toEntry["src"] = src;
            }
        }

        moves.Sort((a, b) => string.CompareOrdinal(a.Id + "/" + a.Field, b.Id + "/" + b.Field));
        if (dryRun)
            return new DefaultLocaleResult(SaveOutcome.Saved, old, locale, moves, skipped, false, []);

        // Settings: the new default, without a fallback chain of its own.
        var settingsBytes = await File.ReadAllBytesAsync(paths.FullPath(ModelPaths.SettingsFile), ct).ConfigureAwait(false);
        expected[ModelPaths.SettingsFile] = snapshot.SettingsHash;
        var settingsJson = JsonNode.Parse(settingsBytes)!.AsObject();
        if (settingsJson["localization"] is JsonObject localization)
        {
            localization["defaultLocale"] = locale;
            if (localization["fallbacks"] is JsonObject fallbacks)
            {
                fallbacks.Remove(locale);
                if (fallbacks.Count == 0)
                    localization.Remove("fallbacks");
            }
        }

        // Every file must still be what the model was loaded from.
        foreach (var (modelPath, hash) in expected)
        {
            var full = paths.FullPath(modelPath);
            var disk = File.Exists(full) ? ContentHash.Of(await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false)) : "";
            if (!string.Equals(disk, hash, StringComparison.Ordinal))
                return new DefaultLocaleResult(SaveOutcome.Conflict, old, locale, moves, skipped, false, []);
        }

        var writes = new List<(string Path, byte[] Bytes)>
        {
            (paths.FullPath(ModelPaths.SettingsFile), _services.Json.Write(settingsJson, ModelPaths.SettingsFile, ModelPaths.SettingsFile)),
        };
        var deletes = new List<string>();
        foreach (var (repoPath, (json, document)) in elements)
        {
            if (KindInfo.TryGet(document.Element.KindName, out var info))
                writes.Add((paths.FullPath(paths.FromRepoPath(repoPath)), _services.Json.Write(json, info.SchemaFile, repoPath)));
        }

        foreach (var (modelPath, json) in shards)
        {
            if (json["entries"] is JsonObject { Count: > 0 })
                writes.Add((paths.FullPath(modelPath), _services.Json.Write(json, "locale.json", paths.ToRepoPath(modelPath))));
            else if (expected[modelPath].Length > 0)
                deletes.Add(paths.FullPath(modelPath));
        }

        var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
        var failure = await new AtomicFileSet(_services.EnginePaths, paths.ModelRoot).ApplyAsync(writes, deletes, batchId, ct).ConfigureAwait(false);
        if (failure is { Refused: true })
            return new DefaultLocaleResult(SaveOutcome.Invalid, old, locale, moves, skipped, false,
                [RuleCatalog.Create("MQ6004", $"The model write to {failure.Path} was refused: {failure.Reason}", null, null, null)]);
        if (failure is not null)
            throw new IOException($"The default locale could not be changed ({failure.Path}); nothing was changed. {failure.Reason}");

        var changes = await ReloadAsync(null, false, source, CancellationToken.None).ConfigureAwait(false);
        if (!changes.IsEmpty)
            notifications.Add(changes);
        return new DefaultLocaleResult(SaveOutcome.Saved, old, locale, moves, skipped, true, []);
    }

    /// <summary>Sets a node's default text in its owner's file: a property of the element or sub-element, or a seed row's cell.</summary>
    private static bool SetText(JsonObject owner, ModelSnapshot snapshot, LocalizableNode node, string field, string text)
    {
        if (node.Kind == "reference-row")
        {
            if (snapshot.Get<Element>(node.OwnerId) is not Seed seed || owner["rows"] is not JsonArray rows)
                return false;
            var column = seed.Columns.ToList().IndexOf(field);
            var row = rows.OfType<JsonObject>().FirstOrDefault(r => (string?)r["id"] == node.Id);
            if (column < 0 || row?["values"] is not JsonArray values)
                return false;
            while (values.Count <= column)
                values.Add(null);
            values[column] = text;
            return true;
        }

        JsonNode? target = owner;
        if (node.Id != node.OwnerId)
        {
            if (!snapshot.Index.Entries.TryGetValue(node.Id, out var entry))
                return false;
            foreach (var segment in entry.JsonPointer.Split('/').Skip(1).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)))
            {
                target = target switch
                {
                    JsonObject o => o[segment],
                    JsonArray a when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                    _ => null,
                };
            }
        }

        if (target is not JsonObject properties)
            return false;
        properties[field] = text;
        return true;
    }
}
