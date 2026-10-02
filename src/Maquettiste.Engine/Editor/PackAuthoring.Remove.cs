using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine;

/// <summary>The result of removing a pack (<c>DELETE /api/packs/{pack}</c>).</summary>
/// <param name="Outcome"><c>saved</c> (removed); <c>conflict</c> (409: <c>pack.json</c> changed since it was read, and <paramref name="Hash"/>
/// and <paramref name="Current"/> are the disk version, or <c>maquettiste.json</c> kept changing during the removal, and both are null);
/// <c>invalid</c> (422: the settings save was refused; nothing was removed) or <c>not-found</c>.</param>
/// <param name="Hash">The disk hash of <c>pack.json</c> on conflict, else <see langword="null"/>.</param>
/// <param name="Current">The disk text of <c>pack.json</c> on conflict, else <see langword="null"/>.</param>
/// <param name="Files">The pack-relative files deleted, ordinal (hidden ones included).</param>
/// <param name="Untracked">The repo-relative generated files the pack's manifests recorded: left on disk and no longer tracked, ordinal.</param>
/// <param name="SettingsHash">The hash of <c>maquettiste.json</c> after its <c>packs.&lt;pack&gt;</c> entry was removed, or <see langword="null"/>
/// when it had no entry.</param>
/// <param name="Diagnostics">Why nothing was removed.</param>
public sealed record PackRemoveResult(SaveOutcome Outcome, string? Hash, string? Current, IReadOnlyList<string> Files, IReadOnlyList<string> Untracked,
    string? SettingsHash, IReadOnlyList<Diagnostic> Diagnostics);

internal static partial class PackAuthoring
{
    /// <summary>
    /// Removes a pack (<c>DELETE /api/packs/{pack}</c>) when <c>pack.json</c> still has <paramref name="expectedHash"/>: the
    /// <c>packs.&lt;pack&gt;</c> entry of <c>maquettiste.json</c> first (through the settings save, so a refused save removes nothing), then
    /// the folder <c>.maquettiste/templates/&lt;pack&gt;/</c>, then its manifest and its unit states. Generated files
    /// stay on disk, untracked: with no manifest left, no later run treats them as orphans (engine-design.md, pack removal). Every
    /// delete is checked against the engine-write guard before the first one; a refused path refuses the whole removal. The caller holds
    /// the run lock, so no run writes the manifests meanwhile.
    /// </summary>
    /// <exception cref="PackPathException">The name is not a pack key, or a path of the folder may not be deleted.</exception>
    public static async Task<PackRemoveResult> RemoveAsync(EngineServices services, ModelStore store, string name, string expectedHash, ChangeSource source,
        CancellationToken ct)
    {
        var root = PackRoot(services.Options, name);
        var manifestPath = Path.Combine(root, "pack.json");
        if (!File.Exists(manifestPath))
            return new PackRemoveResult(SaveOutcome.NotFound, null, null, [], [], null, []);
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disk = await AtomicFile.ReadIfExistsAsync(manifestPath, ct).ConfigureAwait(false);
            if (Conflict(disk, expectedHash) is { } conflict)
                return new PackRemoveResult(SaveOutcome.Conflict, conflict.Hash, conflict.Current, [], [], null, []);

            var removals = PlanRemoval(services, root);
            var manifests = await services.Manifests.LoadAsync([name], ct).ConfigureAwait(false);
            var untracked = manifests.Entries(name).Select(e => e.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            string? settingsHash = null;
            var settled = false;
            for (var attempt = 0; attempt < 3 && !settled; attempt++)
            {
                var current = await store.GetSettingsAsync(ct).ConfigureAwait(false);
                if (!(current.Json.ValueKind == JsonValueKind.Object && current.Json.TryGetProperty("packs", out var packs)
                    && packs.ValueKind == JsonValueKind.Object && packs.TryGetProperty(name, out _)))
                {
                    settled = true;
                    break;
                }

                using var empty = JsonDocument.Parse("{}");
                var bytes = services.Json.Write(WithPackSettings(current.Json, name, empty.RootElement), "maquettiste.json", "maquettiste.json");
                var saved = await store.SaveSettingsAsync(bytes, current.Hash, source, ct).ConfigureAwait(false);
                if (saved.Outcome == SaveOutcome.Conflict)
                    continue; // written between the read and the save: read again
                if (saved.Outcome != SaveOutcome.Saved)
                    return new PackRemoveResult(SaveOutcome.Invalid, null, null, [], [], null, saved.Diagnostics);
                settingsHash = saved.Hash;
                settled = true;
            }

            // maquettiste.json changed under every attempt: nothing was removed, the caller may try again.
            if (!settled)
                return new PackRemoveResult(SaveOutcome.Conflict, null, null, [], [], null, []);

            var files = new List<string>();
            foreach (var (full, directory) in removals)
            {
                ct.ThrowIfCancellationRequested();
                if (directory)
                {
                    Directory.Delete(full, recursive: false);
                }
                else
                {
                    File.Delete(full);
                    files.Add(Path.GetRelativePath(Path.GetFullPath(root), full).Replace(Path.DirectorySeparatorChar, '/'));
                }
            }

            await services.Manifests.SavePackAsync(name, [], ct).ConfigureAwait(false);
            await services.UnitState.SaveAsync(name, [], ct).ConfigureAwait(false);
            files.Sort(StringComparer.Ordinal);
            return new PackRemoveResult(SaveOutcome.Saved, null, null, files, untracked, settingsHash, []);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>
    /// The deletes that remove a pack folder, children before their folder, each checked against the engine-write guard first. A symbolic
    /// link is removed as an entry and never followed (the guard checks the folder that holds it, which the walk already checked).
    /// </summary>
    /// <returns>Absolute paths, with whether each is a folder to remove once empty.</returns>
    private static List<(string Path, bool Directory)> PlanRemoval(EngineServices services, string root)
    {
        void Check(string full)
        {
            var check = services.EnginePaths.CheckEngineWrite(WriteTarget.Model, full);
            if (!check.Allowed)
                throw new PackPathException($"'{full}' may not be deleted: {check.Reason}");
        }

        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (new DirectoryInfo(top).LinkTarget is not null)
            throw new PackPathException($"The pack folder '{top}' is a link; remove it by hand.");
        Check(top);
        var removals = new List<(string, bool)>();
        void Walk(string folder)
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry.LinkTarget is not null)
                {
                    // Directory.Delete removes a link to a folder without touching its target; File.Delete removes any other link.
                    removals.Add((entry.FullName, entry is DirectoryInfo));
                    continue;
                }

                Check(entry.FullName);
                if (entry is DirectoryInfo)
                {
                    Walk(entry.FullName);
                    removals.Add((entry.FullName, true));
                }
                else
                {
                    removals.Add((entry.FullName, false));
                }
            }
        }

        Walk(top);
        removals.Add((top, true));
        return removals;
    }
}
