using System.Collections.Concurrent;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// Records a plan while its dry run streams (engine-design.md section 15, D39): for every processed unit, its input hash, read keys
/// and every output file with the disk hash seen at plan time; the post-processed bytes of each file the apply would write go to
/// the plan's blobs as they pass, so memory stays flat. Skipped units are recorded from their stored state.
/// </summary>
/// <param name="plans">The plan store.</param>
/// <param name="planId">The plan id.</param>
/// <param name="repoRoot">The absolute repo root.</param>
internal sealed class PlanCapture(PlanStore plans, string planId, string repoRoot)
{
    private readonly ConcurrentDictionary<string, PlanUnit> _units = new(StringComparer.Ordinal);

    /// <summary>The prepared run, once stages 1 to 4 succeeded.</summary>
    public PreparedRun? Prepared { get; set; }

    /// <summary>Whether a file's bytes must be stored: the apply writes it (it differs from disk and is not an existing owned file).</summary>
    /// <param name="file">The planned file.</param>
    /// <returns><see langword="true"/> when a blob is needed.</returns>
    public static bool NeedsBlob(PlanFile file) =>
        !string.Equals(file.DiskHashAtPlan, file.ContentHash, StringComparison.Ordinal) && !(IsOwned(file.Mode, file.Role, file.ManifestHash) && file.DiskHashAtPlan is not null);

    /// <summary>Whether a file is owned (<c>once</c>, companion, or an <c>o:</c> manifest hash).</summary>
    /// <param name="mode">The mode.</param>
    /// <param name="role">The role.</param>
    /// <param name="manifestHash">The manifest hash.</param>
    /// <returns><see langword="true"/> when owned.</returns>
    public static bool IsOwned(OutputMode mode, FileRole role, string manifestHash) =>
        mode == OutputMode.Once || role == FileRole.Companion || ManifestHashes.IsOwned(manifestHash);

    /// <summary>Returns the content hash of a repo-relative file, or <see langword="null"/> when it does not exist.</summary>
    /// <param name="repoRoot">The absolute repo root.</param>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The hash.</returns>
    public static async Task<string?> DiskHashAsync(string repoRoot, string path, CancellationToken ct)
    {
        var full = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
            return null;
        try
        {
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous);
            return await ContentHash.OfAsync(stream, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Records a processed unit (called from the post-processing workers).</summary>
    /// <param name="processed">The unit.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task HookAsync(ProcessedUnit processed, CancellationToken ct)
    {
        var files = new List<PlanFile>(processed.Files.Count);
        foreach (var file in processed.Files)
        {
            var disk = await DiskHashAsync(repoRoot, file.Path, ct).ConfigureAwait(false);
            var planned = new PlanFile(file.Path, file.ContentHash, file.ManifestHash, file.Mode, file.Role, file.Root, disk);
            if (NeedsBlob(planned))
                await plans.WriteBlobAsync(planId, file.ContentHash, file.Content, ct).ConfigureAwait(false);
            files.Add(planned);
        }

        var rendered = processed.Rendered;
        var keys = rendered.ReadKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        _units[rendered.Unit.Key] = new PlanUnit(rendered.Unit.Key, rendered.InputHash, keys, false, files);
    }

    /// <summary>Records the skipped units from their stored state.</summary>
    /// <param name="skipped">The skipped units.</param>
    /// <param name="paths">The run's path policy (roots).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task AddSkippedAsync(IReadOnlyList<SkippedUnit> skipped, IOutputPathPolicy paths, CancellationToken ct)
    {
        foreach (var unit in skipped)
        {
            var files = new List<PlanFile>(unit.Previous.Outputs.Count);
            foreach (var output in unit.Previous.Outputs)
            {
                // A plain content hash whose file still has the recorded stat was just verified by stage 5: no need to read it again.
                var info = new FileInfo(Path.Combine(repoRoot, output.Path.Replace('/', Path.DirectorySeparatorChar)));
                var verified = !ManifestHashes.IsOwned(output.ManifestHash) && !ManifestHashes.IsRegions(output.ManifestHash)
                    && info.Exists && info.Length == output.Length && info.LastWriteTimeUtc.Ticks == output.LastWriteUtcTicks;
                var disk = verified ? output.ManifestHash : await DiskHashAsync(repoRoot, output.Path, ct).ConfigureAwait(false);
                var owned = ManifestHashes.IsOwned(output.ManifestHash);
                var mode = unit.Unit.Unit.Mode;
                var role = owned && mode == OutputMode.Pair ? FileRole.Companion : FileRole.Main;
                var root = paths.Check(output.Path).Root ?? new OutputRootInfo("", false);
                files.Add(new PlanFile(output.Path, disk ?? "", output.ManifestHash, mode, role, root, disk));
            }

            _units[unit.Unit.Key] = new PlanUnit(unit.Unit.Key, unit.Previous.InputHash, unit.Previous.ReadKeys, true, files);
        }
    }

    /// <summary>The recorded units in plan order.</summary>
    /// <param name="order">The planned units.</param>
    /// <returns>The units.</returns>
    public IReadOnlyList<PlanUnit> Units(IReadOnlyList<PlannedUnit> order) =>
        [.. order.Select(u => _units.TryGetValue(u.Key, out var unit) ? unit : null).OfType<PlanUnit>()];
}
