using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// Keeps the locale shards in step with a save (reference-types-seeds-localization.md sections 2.7, 3.3 and 3.4): a package rename
/// or move renames its shards (and its sub-packages'), an element that changes domain takes its entries to the new shard, an element
/// file that moves takes its description sidecars along, and a delete removes its entries. Shards are read from disk (or the pending
/// write) and recorded as touched, so a concurrent edit is a conflict; a shard left with no entries is deleted.
/// </summary>
internal sealed partial class ChangePlanner
{
    private const string LocalesRoot = "model/locales";

    private void ReconcileTranslations()
    {
        var root = _paths.FullPath(LocalesRoot);
        if (!Directory.Exists(root))
            return;
        var locales = Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>()
            .Where(n => n.Length > 0 && n[0] != '.').Order(StringComparer.Ordinal).ToList();
        if (locales.Count == 0)
            return;

        // 1. Package renames and moves: every package whose derived shard name changes renames its shard in every locale.
        var oldFiles = LocalizationIndex.PackageFiles(_snapshot.All<Package>());
        var newFiles = LocalizationIndex.PackageFiles(_snapshot.All<Package>()
            .Where(p => !_deleted.Contains(p.Id) && !_working.ContainsKey(p.Id))
            .Concat(_working.Where(w => !_deleted.Contains(w.Key)).Select(w => w.Value.Element).OfType<Package>()));
        foreach (var (id, oldFile) in oldFiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!newFiles.TryGetValue(id, out var newFile) || newFile == oldFile)
                continue;
            foreach (var locale in locales)
                MoveEntries(LocalizationIndex.PathOf(locale, oldFile), LocalizationIndex.PathOf(locale, newFile), null, id);
        }

        string FileOf(string scope) => scope switch
        {
            LocalizationIndex.RootScope => "_root",
            LocalizationIndex.ReferenceDataScope => "_reference-data",
            _ => newFiles.GetValueOrDefault(scope) ?? "_root",
        };
        Element? Find(string id) => _deleted.Contains(id) ? null : _working.TryGetValue(id, out var w) ? w.Element : _snapshot.Get<Element>(id);

        // 2. Elements that change domain, and 3. element files that move (their description sidecars follow).
        foreach (var (id, working) in _working.Where(w => !_deleted.Contains(w.Key)).OrderBy(w => w.Key, StringComparer.Ordinal))
        {
            if (_snapshot.GetDocument(id) is not { } before || before.Element.Id != id)
                continue;
            var owned = OwnedIds(id);
            var oldScope = LocalizationIndex.ScopeOf(before.Element, _snapshot.Get<Element>);
            var newScope = LocalizationIndex.ScopeOf(working.Element, Find);
            var target = FileOf(newScope);
            if (oldScope != newScope)
            {
                foreach (var locale in locales)
                    MoveEntries(LocalizationIndex.PathOf(locale, FileOf(oldScope)), LocalizationIndex.PathOf(locale, target), owned, newScope);
            }

            var oldPath = _paths.FromRepoPath(before.Path);
            if (oldPath != working.ModelPath)
            {
                foreach (var locale in locales)
                    MoveSidecars(LocalizationIndex.PathOf(locale, target), owned, Mirror(oldPath), Mirror(working.ModelPath));
            }
        }

        // 4. Deletes: the entries of deleted elements and removed sub-elements, with their sidecars.
        if (_removedIds.Count > 0)
            RemoveEntries();
    }

    /// <summary>The element id and the ids of the sub-elements it held before the change.</summary>
    private HashSet<string> OwnedIds(string id)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { id };
        foreach (var entry in _snapshot.Index.Entries.Values)
        {
            if (entry.OwnerId == id)
                ids.Add(entry.Id);
        }

        return ids;
    }

    /// <summary>A model path relative to <c>model/</c>, without <c>.json</c> (the sidecar mirror of section 3.4).</summary>
    private static string Mirror(string modelPath)
    {
        var relative = modelPath.StartsWith("model/", StringComparison.Ordinal) ? modelPath["model/".Length..] : modelPath;
        return relative.EndsWith(".json", StringComparison.Ordinal) ? relative[..^".json".Length] : relative;
    }

    /// <summary>Moves entries (all, or those in <paramref name="ids"/>) from one shard file to another, merging into an existing one.</summary>
    private void MoveEntries(string from, string to, HashSet<string>? ids, string scope)
    {
        if (from == to || LoadShard(from) is not { } source || source["entries"] is not JsonObject entries)
            return;
        var moving = entries.Where(kv => ids is null || ids.Contains(kv.Key)).Select(kv => kv.Key).ToList();
        if (moving.Count == 0)
            return;
        var destination = LoadShard(to) ?? NewShard(to, source["locale"]?.GetValue<string>() ?? LocaleShardDocument.FolderLocaleOf(to), scope);
        var targetEntries = destination["entries"] as JsonObject ?? new JsonObject();
        destination["entries"] = targetEntries;
        foreach (var key in moving)
        {
            var node = entries[key];
            entries.Remove(key);
            targetEntries.Remove(key);
            targetEntries[key] = node;
        }

        if (ids is null)
            destination["scope"] = scope;
        SaveShard(from, source);
        SaveShard(to, destination);
    }

    /// <summary>Moves the mirrored description sidecars of a moved element file and rewrites the entries' references.</summary>
    private void MoveSidecars(string shardPath, HashSet<string> ids, string oldMirror, string newMirror)
    {
        if (LoadShard(shardPath) is not { } shard || shard["entries"] is not JsonObject entries)
            return;
        var changed = false;
        foreach (var (id, node) in entries.Where(kv => ids.Contains(kv.Key)).ToList())
        {
            if (node?["description"] is not JsonObject description || description["file"] is not JsonValue value || !value.TryGetValue<string>(out var file))
                continue;
            if (!file.StartsWith(oldMirror + ".", StringComparison.Ordinal))
                continue;
            var renamed = newMirror + file[oldMirror.Length..];
            var from = LocaleShardDocument.ResolveSidecar(shardPath, file);
            var to = LocaleShardDocument.ResolveSidecar(shardPath, renamed);
            if (from is null || to is null || !Exists(from))
                continue;
            var bytes = File.ReadAllBytes(_paths.FullPath(from));
            _touched.TryAdd(from, ContentHash.Of(bytes));
            _deletes.Add(from);
            _deletes.Remove(to);
            _writes[to] = bytes;
            description["file"] = renamed;
            changed = true;
        }

        if (changed)
            SaveShard(shardPath, shard);
    }

    private void RemoveEntries()
    {
        var files = Directory.EnumerateFiles(_paths.FullPath(LocalesRoot), "*.json", SearchOption.AllDirectories)
            .Select(f => LocalesRoot + "/" + Path.GetRelativePath(_paths.FullPath(LocalesRoot), f).Replace('\\', '/'))
            .Concat(_writes.Keys.Where(k => k.StartsWith(LocalesRoot + "/", StringComparison.Ordinal)))
            .Where(p => ModelPaths.Classify(p) == ModelFileKind.LocaleShard && !_deletes.Contains(p))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var modelPath in files)
        {
            _ct.ThrowIfCancellationRequested();
            if (LoadShard(modelPath) is not { } node || node["entries"] is not JsonObject entries)
                continue;
            var stale = entries.Where(kv => _removedIds.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            if (stale.Count == 0)
                continue;
            foreach (var key in stale)
            {
                if (entries[key]?["description"] is JsonObject { } description && description["file"] is JsonValue file
                    && file.TryGetValue<string>(out var name) && LocaleShardDocument.ResolveSidecar(modelPath, name) is { } sidecar && Exists(sidecar))
                {
                    _deletes.Add(sidecar);
                    _touched.TryAdd(sidecar, ContentHash.Of(File.ReadAllBytes(_paths.FullPath(sidecar))));
                }

                entries.Remove(key);
            }

            SaveShard(modelPath, node);
        }
    }

    /// <summary>Reads a shard: the pending write, else the file (recorded as touched); <see langword="null"/> when absent or invalid.</summary>
    private JsonObject? LoadShard(string modelPath)
    {
        byte[] bytes;
        if (_writes.TryGetValue(modelPath, out var pending))
            bytes = pending;
        else if (!_deletes.Contains(modelPath) && Exists(modelPath))
        {
            bytes = File.ReadAllBytes(_paths.FullPath(modelPath));
            _touched.TryAdd(modelPath, ContentHash.Of(bytes));
        }
        else
            return null;

        try
        {
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException)
        {
            return null; // an invalid shard is the loader's finding (MQ1001); it is left alone
        }
    }

    /// <summary>Writes a shard canonically, or deletes it when it has no entries left (section 3.5).</summary>
    private void SaveShard(string modelPath, JsonObject node)
    {
        if (node["entries"] is not JsonObject { Count: > 0 })
        {
            _writes.Remove(modelPath);
            if (Exists(modelPath))
                _deletes.Add(modelPath);
            return;
        }

        _deletes.Remove(modelPath);
        _writes[modelPath] = _json.Write(node, "locale.json", _paths.ToRepoPath(modelPath));
    }

    /// <summary>A new, empty shard for a path, with the <c>$schema</c> path its depth needs.</summary>
    internal static JsonObject NewShard(string modelPath, string locale, string scope)
    {
        var depth = modelPath.Count(c => c == '/');
        return new JsonObject
        {
            ["$schema"] = string.Concat(Enumerable.Repeat("../", depth)) + ".schema/v1/locale.json",
            ["kind"] = "locale-shard",
            ["locale"] = locale,
            ["scope"] = scope,
            ["entries"] = new JsonObject(),
        };
    }
}
