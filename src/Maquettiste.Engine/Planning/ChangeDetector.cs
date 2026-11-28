using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// <remarks>
/// <para>Skip by key (change-driven-edits-design.md section 4, step 3): the detector keeps, between runs, what the previous run saw
/// (<see cref="SkipBaseline"/>): each read key's hash and the input hashes that were current. A unit whose stored state carries one of
/// those input hashes with the same static hash, and none of whose read keys changed hash since, gets the answer the input hash check
/// would give without computing it; every other unit (a key changed, a new unit, states read from a file another process wrote, other
/// settings, the first run of a host) takes the full check. Forced and check runs check nothing, as before.</para>
/// <para>Outputs (ruling R1 is open): every unit whose inputs are unchanged still has each recorded output checked, whether the edit
/// reached it or not, so a deleted or hand-edited output is still written again. The two halves are separate
/// (<see cref="InputsSame"/>, <see cref="OutputsIntact"/>), and <see cref="SkipStats.OutputCheckTime"/> measures the second.</para>
/// </remarks>
/// <param name="options">The engine options (parallelism of the output checks).</param>
internal sealed class ChangeDetector(EngineOptions options) : IChangeDetector
{
    private readonly SkipBaseline _baseline = new();
    private int _run;
    private int _busy;

    /// <summary>Whether the previous run's record may accept a unit's inputs by key (tests compare with <see langword="false"/>, the full
    /// check of every unit).</summary>
    internal bool KeyDriven { get; init; } = true;

    /// <summary>Tests: every unit accepted by key is checked again in full, with a hasher of its own (so the run's cache, which the next
    /// run reads, stays as it would be), and a disagreement is added to <see cref="CrossCheckFailures"/>.</summary>
    internal bool CrossCheck { get; init; }

    /// <summary>The units accepted by key whose full check disagreed (with <see cref="CrossCheck"/>).</summary>
    internal ConcurrentQueue<string> CrossCheckFailures { get; } = new();

    /// <summary>Tests: runs once a skip check that checked units has decided, before anything renders.</summary>
    internal Action? AfterSelect { get; set; }

    /// <summary>What the last skip check that checked units did.</summary>
    internal SkipStats? LastStats { get; private set; }

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
        var own = hasher as DependencyHasher;
        if (force || mode == GenerationMode.Check)
        {
            progress?.Report(new ProgressUpdate(PipelineStage.Skip, units.Count, units.Count, null, null));
            // A check saves no state, so the record of the run before it stays true. A forced run renders every unit with its hashes:
            // the next run starts from what it rendered.
            if (force && mode != GenerationMode.Check && own is not null && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
            {
                try
                {
                    Remember(own, Interlocked.Increment(ref _run), new Dictionary<string, IReadOnlyDictionary<string, UnitState>>(StringComparer.Ordinal), 0);
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                }
            }

            return new SkipResult(units, []);
        }

        var states = new Dictionary<string, IReadOnlyDictionary<string, UnitState>>(StringComparer.Ordinal);
        foreach (var pack in units.Select(u => u.Pack.Name).Distinct(StringComparer.Ordinal))
            states[pack] = await state.LoadAsync(pack, ct).ConfigureAwait(false);

        // One skip check at a time keeps the record (runs hold the run lock, so a second one is rare): it checks every unit in full.
        var exclusive = own is not null && Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
        try
        {
            var run = exclusive ? Interlocked.Increment(ref _run) : 0;
            var previous = exclusive && KeyDriven && state is UnitStateStore && _baseline.Previous is { } last
                && string.Equals(last.SettingsHash, own!.ModelSettingsHash, StringComparison.Ordinal) ? last : null;
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.EffectiveParallelism, CancellationToken = ct };

            // Each unit's stored state and, when the previous run's record trusts it, whether it is a candidate: its input hash was
            // current then, with this static hash. The keys candidates read are marked in their state file's key table.
            var tables = previous is null ? null : KeyTables(states, (UnitStateStore)state, previous);
            var stored = new UnitState?[units.Count];
            var candidates = new KeyTable?[units.Count];
            var entries = new SkipBaseline.Current?[units.Count];
            Parallel.For(0, units.Count, parallel, i =>
            {
                var unit = units[i];
                if (!states[unit.Pack.Name].TryGetValue(unit.Key, out var found))
                    return;
                stored[i] = found;
                if (tables is not null && tables.TryGetValue(unit.Pack.Name, out var table) && found.ReadKeys is UnitStateStore.TableKeys keys
                    && ReferenceEquals(keys.Table, table.Keys) && previous!.WasCurrent(found.InputHash, unit.StaticHash, _baseline.Skipped, out entries[i]))
                {
                    candidates[i] = table;
                    foreach (var index in keys.Indexes)
                        table.Needed[index] = true; // every writer writes true
                }
            });

            var (keysHashed, keysChanged, keysFromRecord, tableKeys) = tables is null ? (0, 0, 0, 0) : CompareKeys(tables.Values, own!, previous!, run, parallel);
            var full = 0;
            for (var i = 0; i < units.Count; i++)
            {
                if (stored[i] is not null && candidates[i] is null)
                    full++;
            }

            // Keys decoded from a state file hash fastest through the file's key table when most units take the full check; for a few,
            // the table's one sort per run costs more than it saves.
            var viaTable = tables is null || full * 4 > tableKeys;
            // The cross-check hashes with a hasher of its own: through this run's, it would put every key in the cache the next run reads.
            IDependencyHasher? checker = CrossCheck ? own?.Fresh() ?? hasher : null;
            var repoRoot = Path.GetFullPath(options.RepoRoot);
            var skippedState = new UnitState?[units.Count];
            var done = 0;
            var byKey = 0;
            var outputsChecked = 0;
            long outputTicks = 0;
            await Parallel.ForAsync(0, units.Count, parallel, (i, token) =>
            {
                var unit = units[i];
                if (stored[i] is { } previousState)
                {
                    bool inputs;
                    if (candidates[i] is { } table && !table.AnyChanged((UnitStateStore.TableKeys)previousState.ReadKeys))
                    {
                        inputs = true;
                        Interlocked.Increment(ref byKey);
                        if (checker is not null && !InputsSame(unit, previousState, checker, viaTable: false))
                            CrossCheckFailures.Enqueue(unit.Key);
                    }
                    else
                    {
                        inputs = InputsSame(unit, previousState, hasher, viaTable);
                        entries[i] = null; // a full check: Remember records its input hash afresh
                    }

                    // Every unit whose inputs are unchanged has its outputs checked, reached by the edit or not (ruling R1).
                    if (inputs)
                    {
                        var start = Stopwatch.GetTimestamp();
                        if (OutputsIntact(repoRoot, unit, previousState, manifests, token, stats))
                            skippedState[i] = previousState;
                        Interlocked.Add(ref outputTicks, Stopwatch.GetTimestamp() - start);
                        Interlocked.Add(ref outputsChecked, previousState.Outputs.Count);
                    }

                    // An input hash the record already holds, still current: stamped with this run in place (each unit has its own).
                    if (exclusive && skippedState[i] is not null && entries[i] is { } entry)
                        entry.Run = run;
                }

                var count = Interlocked.Increment(ref done);
                if (progress is not null && (count % 256 == 0 || count == units.Count))
                    progress.Report(new ProgressUpdate(PipelineStage.Skip, count, units.Count, null, unit.Pack.Name));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            var toRender = new List<PlannedUnit>();
            var skipped = new List<SkippedUnit>();
            if (exclusive)
                _baseline.Skipped.EnsureCapacity(units.Count);
            for (var i = 0; i < units.Count; i++)
            {
                if (skippedState[i] is { } kept)
                {
                    skipped.Add(new SkippedUnit(units[i], kept));
                    if (exclusive && entries[i] is null)
                        RecordSkipped(units[i], kept, run);
                }
                else
                {
                    toRender.Add(units[i]);
                }
            }

            if (exclusive)
                Remember(own!, run, states, tableKeys);
            var withState = stored.Count(s => s is not null);
            LastStats = new SkipStats(units.Count, withState, byKey, withState - byKey, keysHashed, keysChanged, keysFromRecord, outputsChecked,
                Stopwatch.GetElapsedTime(0, outputTicks));
            AfterSelect?.Invoke();
            return new SkipResult(toRender, skipped);
        }
        finally
        {
            if (exclusive)
                Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>
    /// The key table of each pack whose states the previous run's record trusts: states this store saved, or the very states the previous
    /// run loaded and looked at (passed or not). The states of one file share one key table; a state with another (a store other than the engine's) is no candidate.
    /// </summary>
    private static Dictionary<string, KeyTable> KeyTables(Dictionary<string, IReadOnlyDictionary<string, UnitState>> states, UnitStateStore store,
        SkipBaseline.RunRecord previous)
    {
        var tables = new Dictionary<string, KeyTable>(StringComparer.Ordinal);
        foreach (var (pack, packStates) in states)
        {
            if (packStates.Count == 0 || !(store.Saved(pack, packStates) || previous.Checked(pack, packStates)))
                continue;
            if (packStates.Values.First().ReadKeys is UnitStateStore.TableKeys keys)
                tables[pack] = new KeyTable(keys.Table);
        }

        return tables;
    }

    /// <summary>
    /// Hashes every key a candidate reads, once even when several packs' tables hold it, compares each with the previous run's hash, then
    /// records this run's: the comparisons all finish before any hash is replaced.
    /// </summary>
    /// <returns>The keys hashed, those whose hash changed (or was unknown), those whose previous hash came from the record's own
    /// comparison rather than the previous run's hasher cache, and the size of the key tables.</returns>
    private (int Hashed, int Changed, int FromRecord, int TableKeys) CompareKeys(IEnumerable<KeyTable> tables, DependencyHasher hasher, SkipBaseline.RunRecord previous, int run,
        ParallelOptions parallel)
    {
        var distinct = new List<SkipBaseline.KeyMemo>();
        var size = 0;
        _baseline.Keys.EnsureCapacity(tables.Select(t => t.Keys.Length).DefaultIfEmpty().Max());
        foreach (var table in tables)
        {
            size += table.Keys.Length;
            for (var k = 0; k < table.Keys.Length; k++)
            {
                if (!table.Needed[k])
                    continue;
                ref var memo = ref CollectionsMarshal.GetValueRefOrAddDefault(_baseline.Keys, table.Keys[k], out _);
                memo ??= new SkipBaseline.KeyMemo(table.Keys[k]);
                table.Memos[k] = memo;
                if (memo.Claim != run)
                {
                    memo.Claim = run;
                    distinct.Add(memo);
                }
            }
        }

        var fromRecord = 0;
        Parallel.For(0, distinct.Count, parallel, n =>
        {
            var memo = distinct[n];
            var now = hasher.Uncached(memo.Key, memo.Rest);
            var before = previous.HashOf(memo);
            if (memo.Run == previous.Run && !previous.Cache.ContainsKey(memo.Key))
                Interlocked.Increment(ref fromRecord);
            memo.Now = now;
            memo.Changed = before is null || !string.Equals(before, now, StringComparison.Ordinal);
        });

        var changed = 0;
        foreach (var memo in distinct)
        {
            if (memo.Changed)
                changed++;
            memo.Hash = memo.Now;
            memo.Now = null;
            memo.Run = run;
        }

        return (distinct.Count, changed, fromRecord, size);
    }

    /// <summary>One state file's key table: per key, whether a candidate reads it, then its record entry (whose
    /// <see cref="SkipBaseline.KeyMemo.Changed"/> says whether its hash changed).</summary>
    private sealed class KeyTable(string[] keys)
    {
        public string[] Keys { get; } = keys;

        public bool[] Needed { get; } = new bool[keys.Length];

        public SkipBaseline.KeyMemo?[] Memos { get; } = new SkipBaseline.KeyMemo?[keys.Length];

        /// <summary>Whether any of a candidate's keys changed hash.</summary>
        public bool AnyChanged(UnitStateStore.TableKeys keys)
        {
            foreach (var index in keys.Indexes)
            {
                if (Memos[index]!.Changed)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Records a skipped unit's input hash as current in this run, when the record does not hold it yet.</summary>
    private void RecordSkipped(PlannedUnit unit, UnitState kept, int run)
    {
        if (!UnitHash.TryParse(unit.StaticHash, out var parsed))
            return;
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_baseline.Skipped, kept.InputHash, out _);
        entry ??= new SkipBaseline.Current();
        entry.Static = parsed;
        entry.Run = run;
    }

    /// <summary>
    /// Records this run for the next: its hasher's cache, the states it checked, and, through <see cref="DependencyHasher.InputHashes"/>,
    /// the input hashes of the units it goes on to render (the skipped units' input hashes are recorded as they are found).
    /// </summary>
    private void Remember(DependencyHasher hasher, int run, Dictionary<string, IReadOnlyDictionary<string, UnitState>> states, int tableKeys)
    {
        // Entries no run refreshed only take room: dropped once they clearly outnumber what the loaded state files hold.
        var stored = 0;
        var keys = 0;
        foreach (var packStates in states.Values)
        {
            stored += packStates.Count;
            if (packStates.Count > 0 && packStates.Values.First().ReadKeys is UnitStateStore.TableKeys table)
                keys += table.Table.Length;
        }

        if (_baseline.Skipped.Count > (2 * stored) + 1024)
            Prune(_baseline.Skipped, e => e.Run != run);
        if (_baseline.Keys.Count > (2 * Math.Max(keys, tableKeys)) + 4096)
            Prune(_baseline.Keys, e => e.Run != run);

        var rendered = new ConcurrentDictionary<string, UnitHash>(StringComparer.Ordinal);
        hasher.InputHashes = rendered;
        var seen = new Dictionary<string, WeakReference<IReadOnlyDictionary<string, UnitState>>>(StringComparer.Ordinal);
        foreach (var (pack, packStates) in states)
            seen[pack] = new WeakReference<IReadOnlyDictionary<string, UnitState>>(packStates);
        _baseline.Previous = new SkipBaseline.RunRecord(run, hasher.ModelSettingsHash, hasher.Cache, rendered, seen);
    }

    private static void Prune<T>(Dictionary<string, T> entries, Func<T, bool> stale)
    {
        foreach (var (key, value) in entries)
        {
            if (stale(value))
                entries.Remove(key);
        }
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
        OutputStats? stats = null) =>
        InputsSame(unit, stored, hasher, viaTable: true) && OutputsIntact(repoRoot, unit, stored, manifests, ct, stats);

    /// <summary>Whether the input hash recomputed from a stored state's read keys with the current hashes equals the stored one.</summary>
    /// <param name="unit">The planned unit.</param>
    /// <param name="stored">Its stored state.</param>
    /// <param name="hasher">Current hashes.</param>
    /// <param name="viaTable">Whether keys decoded from a state file go through the file's key table (<see cref="DependencyHasher.InputHashEquals"/>).</param>
    /// <returns><see langword="true"/> when equal.</returns>
    internal static bool InputsSame(PlannedUnit unit, UnitState stored, IDependencyHasher hasher, bool viaTable) =>
        hasher is DependencyHasher own
            ? own.InputHashEquals(unit.StaticHash, stored.ReadKeys, stored.InputHash, viaTable)
            : string.Equals(hasher.InputHash(unit.StaticHash, stored.ReadKeys), stored.InputHash, StringComparison.Ordinal);

    /// <summary>Whether every output a stored state records is intact (<see cref="OutputIntact"/>).</summary>
    /// <param name="repoRoot">The absolute repo root.</param>
    /// <param name="unit">The planned unit.</param>
    /// <param name="stored">Its stored state.</param>
    /// <param name="manifests">The manifests.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="stats">Output stats taken earlier in the run, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when all are intact.</returns>
    internal static bool OutputsIntact(string repoRoot, PlannedUnit unit, UnitState stored, ManifestSet manifests, CancellationToken ct, OutputStats? stats)
    {
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
        // The absolute path only when the stat was not taken beside resolve or the bytes must be read: no string per output otherwise.
        var stat = stats is not null && stats.TryGet(output.Path, out var known) ? known : OutputStats.Stat(OutputStats.FullPath(repoRoot, output.Path));
        if (ManifestHashes.IsOwned(output.ManifestHash))
            return stat.Exists;
        if (!stat.Exists || !manifests.TryGet(output.Path, out var entry, out var owner)
            || !string.Equals(owner, pack, StringComparison.Ordinal) || !string.Equals(entry.Hash, output.ManifestHash, StringComparison.Ordinal))
            return false;
        if (stat.Length == output.Length && stat.LastWriteUtcTicks == output.LastWriteUtcTicks)
            return true;
        try
        {
            var bytes = File.ReadAllBytes(OutputStats.FullPath(repoRoot, output.Path));
            return string.Equals(ManifestHashes.Comparable(output.ManifestHash, bytes, ContentHash.Of(bytes), ManagedBlock.Marker(pack, entry.Unit)),
                output.ManifestHash, StringComparison.Ordinal);
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
