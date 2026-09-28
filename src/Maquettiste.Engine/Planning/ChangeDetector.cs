using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// Stage 5: skips unchanged units (W6; engine-design.md section 11). A unit is skipped when the run is not forced and not a check,
/// its stored <see cref="UnitState"/> exists, the input hash recomputed from its recorded read keys with the current hashes equals
/// the stored one, and every recorded output is intact: an owned output (<c>o:</c>) only needs to exist; any other must still be
/// in the manifest with the same manifest hash and, on disk, have the recorded length and last-write time or, when the stat
/// differs, bytes that hash to it again (the skeleton for <c>r:</c> files). The checks of different units run in parallel; the
/// result keeps the plan's order.
/// </summary>
/// <param name="options">The engine options (parallelism of the output checks).</param>
internal sealed class ChangeDetector(EngineOptions options) : IChangeDetector
{
    /// <inheritdoc/>
    public Task<SkipResult> SelectAsync(UnitPlan plan, IDependencyHasher hasher, IUnitStateStore state, ManifestSet manifests,
        GenerationMode mode, bool force, IProgress<ProgressUpdate>? progress, CancellationToken ct) =>
        SelectAsync(plan, hasher, state, manifests, mode, force, progress, ct, null);

    /// <summary>
    /// <see cref="SelectAsync(UnitPlan, IDependencyHasher, IUnitStateStore, ManifestSet, GenerationMode, bool, IProgress{ProgressUpdate}?, CancellationToken)"/>
    /// with the recorded outputs' stats taken earlier in the same run (<see cref="OutputStats"/>), so the check needs no stat of its own
    /// for them; an output without one is stat'ed here.
    /// </summary>
    internal async Task<SkipResult> SelectAsync(UnitPlan plan, IDependencyHasher hasher, IUnitStateStore state, ManifestSet manifests,
        GenerationMode mode, bool force, IProgress<ProgressUpdate>? progress, CancellationToken ct, OutputStats? stats)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(manifests);
        var units = plan.Units;
        if (force || mode == GenerationMode.Check)
        {
            progress?.Report(new ProgressUpdate(PipelineStage.Skip, units.Count, units.Count, null, null));
            return new SkipResult(units, []);
        }

        var states = new Dictionary<string, IReadOnlyDictionary<string, UnitState>>(StringComparer.Ordinal);
        foreach (var pack in units.Select(u => u.Pack.Name).Distinct(StringComparer.Ordinal))
            states[pack] = await state.LoadAsync(pack, ct).ConfigureAwait(false);

        var repoRoot = Path.GetFullPath(options.RepoRoot);
        var previous = new UnitState?[units.Count];
        var done = 0;
        await Parallel.ForAsync(0, units.Count, new ParallelOptions { MaxDegreeOfParallelism = options.EffectiveParallelism, CancellationToken = ct },
            (i, token) =>
            {
                var unit = units[i];
                if (states[unit.Pack.Name].TryGetValue(unit.Key, out var stored) && Unchanged(repoRoot, unit, stored, hasher, manifests, token, stats))
                    previous[i] = stored;
                var count = Interlocked.Increment(ref done);
                if (progress is not null && (count % 256 == 0 || count == units.Count))
                    progress.Report(new ProgressUpdate(PipelineStage.Skip, count, units.Count, null, unit.Pack.Name));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var toRender = new List<PlannedUnit>();
        var skipped = new List<SkippedUnit>();
        for (var i = 0; i < units.Count; i++)
        {
            if (previous[i] is { } stored)
                skipped.Add(new SkippedUnit(units[i], stored));
            else
                toRender.Add(units[i]);
        }

        return new SkipResult(toRender, skipped);
    }

    /// <summary>Whether a unit's inputs and outputs are what its stored state says.</summary>
    /// <param name="repoRoot">The absolute repo root.</param>
    /// <param name="unit">The planned unit.</param>
    /// <param name="stored">Its stored state.</param>
    /// <param name="hasher">Current hashes.</param>
    /// <param name="manifests">The manifests.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="true"/> when the unit can be skipped.</returns>
    /// <param name="stats">Output stats taken earlier in the run, or <see langword="null"/>.</param>
    internal static bool Unchanged(string repoRoot, PlannedUnit unit, UnitState stored, IDependencyHasher hasher, ManifestSet manifests, CancellationToken ct,
        OutputStats? stats = null)
    {
        var inputsSame = hasher is DependencyHasher own
            ? own.InputHashEquals(unit.StaticHash, stored.ReadKeys, stored.InputHash)
            : string.Equals(hasher.InputHash(unit.StaticHash, stored.ReadKeys), stored.InputHash, StringComparison.Ordinal);
        if (!inputsSame)
            return false;
        foreach (var output in stored.Outputs)
        {
            ct.ThrowIfCancellationRequested();
            if (!OutputIntact(repoRoot, unit.Pack.Name, output, manifests, stats))
                return false;
        }

        return true;
    }

    /// <summary>Whether one recorded output is intact on disk and in the manifest.</summary>
    /// <param name="repoRoot">The absolute repo root.</param>
    /// <param name="pack">The unit's pack.</param>
    /// <param name="output">The recorded output.</param>
    /// <param name="manifests">The manifests.</param>
    /// <returns><see langword="true"/> when intact.</returns>
    /// <param name="stats">Output stats taken earlier in the run, or <see langword="null"/>.</param>
    internal static bool OutputIntact(string repoRoot, string pack, UnitOutput output, ManifestSet manifests, OutputStats? stats = null)
    {
        var full = OutputStats.FullPath(repoRoot, output.Path);
        var stat = stats is not null && stats.TryGet(output.Path, out var known) ? known : OutputStats.Stat(full);
        if (ManifestHashes.IsOwned(output.ManifestHash))
            return stat.Exists;
        if (!stat.Exists || !manifests.TryGet(output.Path, out var entry, out var owner)
            || !string.Equals(owner, pack, StringComparison.Ordinal) || !string.Equals(entry.Hash, output.ManifestHash, StringComparison.Ordinal))
            return false;
        if (stat.Length == output.Length && stat.LastWriteUtcTicks == output.LastWriteUtcTicks)
            return true;
        try
        {
            var bytes = File.ReadAllBytes(full);
            return string.Equals(ManifestHashes.Comparable(output.ManifestHash, bytes, ContentHash.Of(bytes)), output.ManifestHash, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// The stats (existence, length, last-write time) of the outputs recorded in the loaded unit states, taken in parallel while the
/// model resolves, so the skip stage finds them ready. Taken after the run lock, within the run: the same as checking a moment
/// earlier. Read-only once built.
/// </summary>
internal sealed class OutputStats
{
    private readonly Dictionary<string, FileStat> _byPath;

    private OutputStats(Dictionary<string, FileStat> byPath) => _byPath = byPath;

    /// <summary>One output's stat.</summary>
    /// <param name="Exists">Whether the file exists.</param>
    /// <param name="Length">Its length (0 when missing).</param>
    /// <param name="LastWriteUtcTicks">Its last-write time (0 when missing).</param>
    internal readonly record struct FileStat(bool Exists, long Length, long LastWriteUtcTicks);

    /// <summary>The absolute path of an output.</summary>
    /// <param name="repoRoot">The absolute repo root.</param>
    /// <param name="path">The repo-relative output path.</param>
    /// <returns>The path.</returns>
    public static string FullPath(string repoRoot, string path) => Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Stats one file.</summary>
    /// <param name="fullPath">The absolute path.</param>
    /// <returns>The stat.</returns>
    public static FileStat Stat(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return info.Exists ? new FileStat(true, info.Length, info.LastWriteTimeUtc.Ticks) : new FileStat(false, 0, 0);
    }

    /// <summary>Stats every output recorded in the states, in parallel.</summary>
    /// <param name="repoRoot">The repo root.</param>
    /// <param name="states">The loaded states of the run's packs.</param>
    /// <param name="parallelism">The degree of parallelism.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The stats.</returns>
    public static OutputStats Collect(string repoRoot, IEnumerable<IReadOnlyDictionary<string, UnitState>> states, int parallelism, CancellationToken ct)
    {
        var root = Path.GetFullPath(repoRoot);
        var paths = new List<string>();
        foreach (var pack in states)
        {
            foreach (var state in pack.Values)
            {
                foreach (var output in state.Outputs)
                    paths.Add(output.Path);
            }
        }

        var results = new FileStat[paths.Count];
        Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct },
            i => results[i] = Stat(FullPath(root, paths[i])));
        var byPath = new Dictionary<string, FileStat>(paths.Count, StringComparer.Ordinal);
        for (var i = 0; i < paths.Count; i++)
            byPath[paths[i]] = results[i];
        return new OutputStats(byPath);
    }

    /// <summary>The stat taken for an output path, when there is one.</summary>
    /// <param name="path">The repo-relative output path.</param>
    /// <param name="stat">The stat.</param>
    /// <returns>Whether it was taken.</returns>
    public bool TryGet(string path, out FileStat stat) => _byPath.TryGetValue(path, out stat);
}
