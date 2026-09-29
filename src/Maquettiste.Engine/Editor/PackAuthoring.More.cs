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

/// <summary>One generated file a pack's manifest records (<c>GET /api/packs/{pack}/outputs</c>).</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Unit">The unit id.</param>
/// <param name="ElementId">The element, or <see langword="null"/> for model and locale scope.</param>
/// <param name="Companion">Whether the file is the unit's companion output.</param>
/// <param name="Root">The output root that holds it, or <see langword="null"/> when no root does now.</param>
/// <param name="Commit">Whether the root is committed.</param>
/// <param name="Mode"><c>overwrite</c>, <c>regions</c> or <c>once</c> (owned), from the manifest hash.</param>
/// <param name="State"><c>intact</c>, <c>edited</c> (the disk bytes differ from the manifest) or <c>missing</c>.</param>
public sealed record PackOutput(string Path, string Unit, string? ElementId, bool Companion, string? Root, bool Commit, string Mode, string State);

/// <summary>The outputs of one pack.</summary>
/// <param name="Pack">The pack name.</param>
/// <param name="Outputs">Every manifest entry, ordinal by path.</param>
/// <param name="LastWritten">When a manifest file was last written (UTC, ISO 8601), or <see langword="null"/> before the first run.</param>
public sealed record PackOutputs(string Pack, IReadOnlyList<PackOutput> Outputs, string? LastWritten);

/// <summary>The body of <c>POST /api/packs/{pack}/file/move</c>.</summary>
/// <param name="From">The pack-relative source path.</param>
/// <param name="To">The pack-relative target path (must not exist).</param>
/// <param name="UpdateUnits">Whether to rewrite the units and scripts of <c>pack.json</c> that name <paramref name="From"/>.</param>
/// <param name="ExpectedPackHash">The <c>pack.json</c> hash the caller read; required with <paramref name="UpdateUnits"/>.</param>
public sealed record PackFileMove(string? From, string? To, bool UpdateUnits, string? ExpectedPackHash);

internal static partial class PackAuthoring
{
    /// <summary>
    /// Moves one pack file (generation-ui.md section 5.1): the source hash must still be <paramref name="expectedHash"/>, the target must
    /// not exist, and a file a template includes is refused. With <see cref="PackFileMove.UpdateUnits"/> the units and scripts that name the
    /// source are rewritten in <c>pack.json</c> (canonical) in the same write, checked against <see cref="PackFileMove.ExpectedPackHash"/>.
    /// Without it, a file a unit names is refused.
    /// </summary>
    public static async Task<PackWriteResult> MoveFileAsync(EngineServices services, ModelSnapshot snapshot, string name, PackFileMove move,
        string expectedHash, CancellationToken ct)
    {
        var root = PackRoot(services.Options, name);
        var from = move.From ?? throw new PackPathException("from is required.");
        var to = move.To ?? throw new PackPathException("to is required.");
        RefuseManifest(from);
        RefuseManifest(to);
        var source = Resolve(root, from);
        var target = Resolve(root, to);
        if (!File.Exists(source))
            return new PackWriteResult(SaveOutcome.NotFound, null, null, [], []);
        if (File.Exists(target) || Directory.Exists(target))
            return Invalid($"'{to}' already exists.", to);
        if (move.UpdateUnits && move.ExpectedPackHash is null)
            return Invalid("expectedPackHash is required when updateUnits is set.", "pack.json");
        var manifest = TryManifest(root);
        var files = await DescribeFilesAsync(root, manifest, ct).ConfigureAwait(false);
        var used = files.FirstOrDefault(f => string.Equals(f.Path, from, StringComparison.Ordinal))?.UsedBy ?? [];
        var includedBy = used.Where(u => u.StartsWith("include:", StringComparison.Ordinal)).ToList();
        if (includedBy.Count > 0 || (!move.UpdateUnits && used.Count > 0))
        {
            return new PackWriteResult(SaveOutcome.Referenced, null, null, [RuleCatalog.Create("MQ6022",
                $"'{from}' is used by {string.Join(", ", used)}; {(includedBy.Count > 0 ? "change the includes first" : "set updateUnits to rewrite the units")}.", filePath: from)], []);
        }

        var packJson = Path.Combine(root, "pack.json");
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disk = await AtomicFile.ReadIfExistsAsync(source, ct).ConfigureAwait(false);
            if (Conflict(disk, expectedHash) is { } conflict)
                return conflict;
            byte[]? rewritten = null;
            if (move.UpdateUnits)
            {
                var packBytes = await AtomicFile.ReadIfExistsAsync(packJson, ct).ConfigureAwait(false);
                if (Conflict(packBytes, move.ExpectedPackHash) is { } packConflict)
                    return packConflict with { Files = ["pack.json"] };
                var node = JsonNode.Parse(packBytes!) as JsonObject ?? throw new JsonException("pack.json must be a JSON object.");
                if (Rename(node, from, to))
                    rewritten = services.Json.Write(node, "pack.json", "templates/" + name + "/pack.json");
            }

            var check = services.EnginePaths.CheckEngineWrite(WriteTarget.Model, source);
            if (!check.Allowed)
                throw new PackPathException($"'{from}' may not be moved: {check.Reason}");
            await WriteGuardedAsync(services, target, disk!, ct).ConfigureAwait(false);
            if (rewritten is not null)
                await WriteGuardedAsync(services, packJson, rewritten, ct).ConfigureAwait(false);
            File.Delete(source);
        }
        finally
        {
            WriteGate.Release();
        }

        var read = await ReadAsync(services.Options, services.Schemas, snapshot, name, ct).ConfigureAwait(false);
        return new PackWriteResult(SaveOutcome.Saved, read?.Files.FirstOrDefault(f => string.Equals(f.Path, to, StringComparison.Ordinal))?.Hash,
            null, read?.Diagnostics ?? [], move.UpdateUnits ? [from, "pack.json", to] : [from, to]);
    }

    /// <summary>Rewrites the unit templates, companion templates and scripts of a <c>pack.json</c> node that name <paramref name="from"/>.</summary>
    private static bool Rename(JsonObject node, string from, string to)
    {
        var changed = false;
        void Swap(JsonObject? owner, string member)
        {
            if (owner?[member] is JsonValue value && value.TryGetValue<string>(out var text) && string.Equals(text, from, StringComparison.Ordinal))
            {
                owner[member] = to;
                changed = true;
            }
        }

        if (node["units"] is JsonArray units)
        {
            foreach (var unit in units.OfType<JsonObject>())
            {
                Swap(unit, "template");
                Swap(unit["companion"] as JsonObject, "template");
            }
        }

        if (node["scripts"] is JsonArray scripts)
        {
            for (var i = 0; i < scripts.Count; i++)
            {
                if (scripts[i] is JsonValue value && value.TryGetValue<string>(out var text) && string.Equals(text, from, StringComparison.Ordinal))
                {
                    scripts[i] = to;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>The manifest entries of one pack with their root, mode and disk state (<c>GET /api/packs/{pack}/outputs</c>).</summary>
    public static async Task<PackOutputs?> OutputsAsync(EngineServices services, ModelSnapshot snapshot, string name, CancellationToken ct)
    {
        var root = PackRoot(services.Options, name);
        if (!File.Exists(Path.Combine(root, "pack.json")))
            return null;
        var manifests = await services.Manifests.LoadAsync([name], ct).ConfigureAwait(false);
        var policy = services.CreatePathPolicy(snapshot.Settings);
        var repoRoot = new ModelPaths(services.Options).RepoRoot;
        var outputs = new List<PackOutput>();
        foreach (var committed in new[] { true, false })
        {
            foreach (var entry in manifests.Entries(name, committed))
            {
                ct.ThrowIfCancellationRequested();
                var companion = entry.Unit.EndsWith("#companion", StringComparison.Ordinal);
                var unit = companion ? entry.Unit[..^"#companion".Length] : entry.Unit;
                var colon = unit.IndexOf(':', StringComparison.Ordinal);
                var check = policy.Check(entry.Path);
                var full = Path.Combine(repoRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                var bytes = await AtomicFile.ReadIfExistsAsync(full, ct).ConfigureAwait(false);
                var state = bytes is null ? "missing"
                    : string.Equals(ManifestHashes.Comparable(entry.Hash, bytes, ContentHash.Of(bytes)), entry.Hash, StringComparison.Ordinal) ? "intact" : "edited";
                var mode = ManifestHashes.IsRegions(entry.Hash) ? "regions" : ManifestHashes.IsOwned(entry.Hash) ? "once" : "overwrite";
                outputs.Add(new PackOutput(entry.Path, colon < 0 ? unit : unit[..colon], colon < 0 ? null : unit[(colon + 1)..], companion,
                    check.Root?.Path, check.Root?.Commit ?? committed, mode, state));
            }
        }

        outputs.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new PackOutputs(name, outputs, LastWritten(services.Options, name));
    }

    private static string? LastWritten(EngineOptions options, string name)
    {
        var times = new[] { Path.Combine(options.EffectiveModelRoot, "manifest", name + ".json"), Path.Combine(options.EffectiveJournalDirectory, "manifest", name + ".json") }
            .Where(File.Exists).Select(File.GetLastWriteTimeUtc).ToList();
        return times.Count == 0 ? null : times.Max().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Replaces <c>packs.&lt;pack&gt;</c> of a settings document with <paramref name="section"/> (<c>PUT /api/project/settings/packs/{pack}</c>),
    /// leaving every other member as it is; an empty object removes the entry.
    /// </summary>
    public static JsonObject WithPackSettings(JsonElement settings, string name, JsonElement section)
    {
        _ = NamePattern().IsMatch(name) ? name : throw new PackPathException($"'{name}' is not a pack name.");
        if (section.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The pack settings must be a JSON object (enabled, output, parameters).", nameof(section));
        var node = (settings.ValueKind == JsonValueKind.Object ? JsonNode.Parse(settings.GetRawText()) as JsonObject : null) ?? [];
        var packs = node["packs"] as JsonObject;
        if (packs is null)
        {
            packs = [];
            node["packs"] = packs;
        }

        if (section.EnumerateObject().Any())
            packs[name] = JsonNode.Parse(section.GetRawText());
        else
            packs.Remove(name);
        return node;
    }
}
