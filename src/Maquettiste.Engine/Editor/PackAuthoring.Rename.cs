using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine;

/// <summary>The result of renaming a pack (<c>POST /api/packs/{pack}/rename</c>).</summary>
/// <param name="Outcome"><c>saved</c> (renamed, or with a dry run: would be); <c>conflict</c> (409: <c>pack.json</c> changed since it was read, and
/// <paramref name="Hash"/> and <paramref name="Current"/> are the disk version, or <c>maquettiste.json</c> kept changing during the rename, and both
/// are null); <c>invalid</c> (422: the new name is refused or the settings save was refused; nothing changed) or <c>not-found</c>.</param>
/// <param name="From">The old name.</param>
/// <param name="To">The new name.</param>
/// <param name="Hash">The new <c>pack.json</c> hash when saved (<see langword="null"/> for a dry run); the disk hash on conflict.</param>
/// <param name="Current">The disk text of <c>pack.json</c> on conflict, else <see langword="null"/>.</param>
/// <param name="SettingsHash">The hash of <c>maquettiste.json</c> after its <c>packs.&lt;from&gt;</c> entry became <c>packs.&lt;to&gt;</c>, or
/// <see langword="null"/> when it had no entry (or for a dry run).</param>
/// <param name="Files">The pack-relative files that moved with the folder, ordinal (hidden ones included).</param>
/// <param name="Tracked">The repo-relative generated files the pack's manifests record, now recorded under the new name, ordinal.</param>
/// <param name="Hints">The ids of the elements whose generation hints are keyed by the old name, ordinal: the ones a hint update rewrites.</param>
/// <param name="Diagnostics">Why nothing was renamed; after a rename, a warning when the hint update was refused.</param>
public sealed record PackRenameResult(SaveOutcome Outcome, string From, string To, string? Hash, string? Current, string? SettingsHash,
    IReadOnlyList<string> Files, IReadOnlyList<string> Tracked, IReadOnlyList<string> Hints, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>
    /// The ids of the elements whose hints were moved to the new name in one model batch (<c>updateHints</c>), ordinal; empty when the
    /// caller did not ask, or when the batch was refused (a warning in <see cref="Diagnostics"/> then says why; the rename stands).
    /// </summary>
    public IReadOnlyList<string> HintsUpdated { get; init; } = [];
}

internal static partial class PackAuthoring
{
    /// <summary>
    /// Renames a pack (<c>POST /api/packs/{pack}/rename</c>) when <c>pack.json</c> still has <paramref name="expectedHash"/>: the
    /// <c>packs.&lt;name&gt;</c> entry of <c>maquettiste.json</c> becomes <c>packs.&lt;newName&gt;</c> with its values kept (through the settings
    /// save, so a refused save changes nothing), then the folder <c>.maquettiste/templates/&lt;name&gt;/</c> is renamed (never copied),
    /// <c>pack.json</c> names the new folder, and the manifest and the unit states move to the new name, so every
    /// generated file stays tracked and no later run treats it as an orphan (engine-design.md, pack rename). The caller holds the run lock.
    /// </summary>
    /// <exception cref="PackPathException">The old name is not a pack key, the folder is a link, or a path may not be written.</exception>
    public static async Task<PackRenameResult> RenameAsync(EngineServices services, ModelStore store, string name, string newName, string expectedHash,
        bool dryRun, ChangeSource source, CancellationToken ct)
    {
        var root = PackRoot(services.Options, name);
        var manifestPath = Path.Combine(root, "pack.json");
        PackRenameResult Result(SaveOutcome outcome, IReadOnlyList<Diagnostic>? diagnostics = null, string? hash = null, string? current = null) =>
            new(outcome, name, newName, hash, current, null, [], [], [], diagnostics ?? []);
        if (!File.Exists(manifestPath))
            return Result(SaveOutcome.NotFound);
        var packFile = new ModelPaths(services.Options).ToRepoPath("templates/" + newName + "/pack.json");
        PackRenameResult Refused(string message) => Result(SaveOutcome.Invalid, [RuleCatalog.Create("MQ6001", message, filePath: packFile, jsonPointer: "/name")]);
        if (string.IsNullOrEmpty(newName) || !NamePattern().IsMatch(newName))
            return Refused($"'{newName}' is not a pack name: lowercase letters and digits separated by single hyphens, starting with a letter.");
        if (string.Equals(name, newName, StringComparison.Ordinal))
            return Refused($"The pack is already named '{name}'.");
        var target = PackRoot(services.Options, newName);

        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disk = await AtomicFile.ReadIfExistsAsync(manifestPath, ct).ConfigureAwait(false);
            if (Conflict(disk, expectedHash) is { } conflict)
                return Result(SaveOutcome.Conflict, hash: conflict.Hash, current: conflict.Current);
            if (Directory.Exists(target) || File.Exists(target))
                return Refused($"templates/{newName}/ already exists; choose another name.");
            var settings = await store.GetSettingsAsync(ct).ConfigureAwait(false);
            if (Section(settings.Json, newName) is not null)
                return Refused($"maquettiste.json already has a packs.{newName} entry; remove it or choose another name.");
            var manifests = await services.Manifests.LoadAsync([name, newName], ct).ConfigureAwait(false);
            if (manifests.Packs.Contains(newName))
                return Refused($"Manifests of a former pack '{newName}' remain under .maquettiste/manifest/; run generation over every pack first, or choose another name.");
            if (await services.Journal.ReadUnfinishedAsync(ct).ConfigureAwait(false) is { } unfinished
                && unfinished.Any(r => string.Equals(r.Pack, name, StringComparison.Ordinal)))
                return Refused($"An interrupted run of '{name}' has not finished; run generation once to finish it, then rename the pack.");

            var moves = PlanMove(services, root, target);
            var entries = manifests.Entries(name);
            var tracked = entries.Select(e => e.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var snapshot = await store.GetSnapshotAsync(ct).ConfigureAwait(false);
            var hints = HintOwners(snapshot, name);
            if (dryRun)
                return new PackRenameResult(SaveOutcome.Saved, name, newName, null, null, null, moves, tracked, hints, []);

            var node = JsonNode.Parse(disk!) as JsonObject ?? throw new PackPathException("pack.json is not a JSON object; fix it before renaming the pack.");
            node["name"] = newName;
            var packBytes = services.Json.Write(node, "pack.json", "templates/" + newName + "/pack.json");

            string? settingsHash = null;
            var before = settings.Json;
            var settled = false;
            for (var attempt = 0; attempt < 3 && !settled; attempt++)
            {
                var current = attempt == 0 ? settings : await store.GetSettingsAsync(ct).ConfigureAwait(false);
                if (Section(current.Json, name) is not { } section)
                {
                    settled = true;
                    break;
                }

                using var empty = JsonDocument.Parse("{}");
                var moved = WithPackSettings(current.Json, name, empty.RootElement);
                if (moved["packs"] is JsonObject packs)
                    packs[newName] = JsonNode.Parse(section.GetRawText());
                var bytes = services.Json.Write(moved, "maquettiste.json", "maquettiste.json");
                var saved = await store.SaveSettingsAsync(bytes, current.Hash, source, ct).ConfigureAwait(false);
                if (saved.Outcome == SaveOutcome.Conflict)
                    continue; // written between the read and the save: read again
                if (saved.Outcome != SaveOutcome.Saved)
                    return Result(SaveOutcome.Invalid, saved.Diagnostics);
                settingsHash = saved.Hash;
                before = current.Json;
                settled = true;
            }

            // maquettiste.json changed under every attempt: nothing was renamed, the caller may try again.
            if (!settled)
                return Result(SaveOutcome.Conflict);

            try
            {
                Directory.Move(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The folder stayed where it was (a file in it is open elsewhere, say): put the settings entry back under the old name.
                if (settingsHash is not null)
                {
                    var back = services.Json.Write(JsonNode.Parse(before.GetRawText())!, "maquettiste.json", "maquettiste.json");
                    await store.SaveSettingsAsync(back, settingsHash, source, CancellationToken.None).ConfigureAwait(false);
                }

                return Refused($"The folder templates/{name}/ could not be renamed ({ex.Message}); nothing was renamed.");
            }

            await WriteGuardedAsync(services, Path.Combine(target, "pack.json"), packBytes, CancellationToken.None).ConfigureAwait(false);

            // From here on nothing is cancelled: the folder has moved, so its manifests and states follow it.
            await services.Manifests.SavePackAsync(newName, entries, CancellationToken.None).ConfigureAwait(false);
            await services.Manifests.SavePackAsync(name, [], CancellationToken.None).ConfigureAwait(false);
            var states = await services.UnitState.LoadAsync(name, CancellationToken.None).ConfigureAwait(false);
            var prefix = name + "/";
            await services.UnitState.SaveAsync(newName, [.. states.Values.Select(s => s.Key.StartsWith(prefix, StringComparison.Ordinal)
                ? s with { Key = newName + "/" + s.Key[prefix.Length..] } : s)], CancellationToken.None).ConfigureAwait(false);
            await services.UnitState.SaveAsync(name, [], CancellationToken.None).ConfigureAwait(false);
            return new PackRenameResult(SaveOutcome.Saved, name, newName, ContentHash.Of(packBytes), null, settingsHash, moves, tracked, hints, []);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>The <c>packs.&lt;name&gt;</c> object of a settings document, or <see langword="null"/>.</summary>
    private static JsonElement? Section(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("packs", out var packs) && packs.ValueKind == JsonValueKind.Object
            && packs.TryGetProperty(name, out var section) ? section : null;

    /// <summary>
    /// Checks a folder rename against the engine-write guard: the folder and every entry under it, at its old and its new path. A link
    /// inside moves as an entry and is never followed; a pack folder that is itself a link is refused, as removal refuses it.
    /// </summary>
    /// <returns>The pack-relative files that move, ordinal.</returns>
    private static List<string> PlanMove(EngineServices services, string root, string target)
    {
        void Check(string full)
        {
            var check = services.EnginePaths.CheckEngineWrite(WriteTarget.Model, full);
            if (!check.Allowed)
                throw new PackPathException($"'{full}' may not be moved: {check.Reason}");
        }

        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (new DirectoryInfo(top).LinkTarget is not null)
            throw new PackPathException($"The pack folder '{top}' is a link; rename it by hand.");
        Check(top);
        Check(to);
        var files = new List<string>();
        void Walk(string folder)
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(top, entry.FullName);
                Check(entry.FullName);
                Check(Path.Combine(to, relative));
                if (entry is DirectoryInfo && entry.LinkTarget is null)
                    Walk(entry.FullName);
                else
                    files.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        Walk(top);
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary>The ids of the elements whose generation hints, at any depth, are keyed by the pack name; ordinal.</summary>
    private static List<string> HintOwners(ModelSnapshot snapshot, string pack) =>
        [.. snapshot.Documents.Where(d => PackHints.Names(d.Json, pack)).Select(d => d.Element.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>
/// The per-pack generation hints of model documents (<c>generation</c>, keyed by a pack name or <c>*</c>, on an element and on its parts:
/// enum members, table columns, category nodes, ...), as a pack rename sees them.
/// </summary>
public static class PackHints
{
    /// <summary>Whether a document holds, at any depth, a <c>generation</c> map with a key equal to <paramref name="pack"/>.</summary>
    /// <param name="document">The document as read.</param>
    /// <param name="pack">The pack name.</param>
    /// <returns>Whether a hint names the pack.</returns>
    public static bool Names(JsonElement document, string pack)
    {
        switch (document.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in document.EnumerateObject())
                {
                    if (property.NameEquals("generation") && property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty(pack, out _))
                        return true;
                    if (Names(property.Value, pack))
                        return true;
                }

                return false;
            case JsonValueKind.Array:
                return document.EnumerateArray().Any(item => Names(item, pack));
            default:
                return false;
        }
    }

    /// <summary>
    /// Moves every <c>generation</c> hint keyed by <paramref name="from"/> to <paramref name="to"/>, in place and at the same position in its
    /// map; a map that already has a <paramref name="to"/> key is left as it is (both keys stay).
    /// </summary>
    /// <param name="document">The document, edited in place.</param>
    /// <param name="from">The old pack name.</param>
    /// <param name="to">The new pack name.</param>
    /// <returns>How many maps changed.</returns>
    public static int Rename(JsonNode? document, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var changed = 0;
        switch (document)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "generation" && value is JsonObject map && map.ContainsKey(from) && !map.ContainsKey(to))
                    {
                        var entries = map.ToList();
                        map.Clear();
                        foreach (var (name, hint) in entries)
                            map[string.Equals(name, from, StringComparison.Ordinal) ? to : name] = hint;
                        changed++;
                    }
                    else
                    {
                        changed += Rename(value, from, to);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                    changed += Rename(item, from, to);
                break;
        }

        return changed;
    }
}
