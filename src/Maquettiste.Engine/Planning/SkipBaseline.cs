using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// A unit's static hash (or any 64-character lowercase hexadecimal hash) as its 32 bytes, so the skip check's record of the previous
/// run keeps one per unit without keeping the run's strings alive.
/// </summary>
/// <param name="A">Bytes 0 to 7.</param>
/// <param name="B">Bytes 8 to 15.</param>
/// <param name="C">Bytes 16 to 23.</param>
/// <param name="D">Bytes 24 to 31.</param>
internal readonly record struct UnitHash(ulong A, ulong B, ulong C, ulong D)
{
    /// <summary>Parses a lowercase hexadecimal SHA-256 hash; any other text (an upper-case or shorter hash, a test's made-up one) fails,
    /// so two different texts never map to one value.</summary>
    /// <param name="hex">The text.</param>
    /// <param name="hash">The hash.</param>
    /// <returns>Whether the text is such a hash.</returns>
    public static bool TryParse(string hex, out UnitHash hash)
    {
        hash = default;
        if (hex.Length != 64 || hex.AsSpan().ContainsAnyInRange('A', 'F'))
            return false;
        Span<byte> bytes = stackalloc byte[32];
        if (Convert.FromHexString(hex, bytes, out _, out var written) != System.Buffers.OperationStatus.Done || written != 32)
            return false;
        var words = MemoryMarshal.Cast<byte, ulong>(bytes);
        hash = new UnitHash(words[0], words[1], words[2], words[3]);
        return true;
    }
}

/// <summary>
/// What the skip check keeps from one run for the next (change-driven-edits-design.md section 4, step 3): the hash each dependency key
/// had, and which input hashes were current under those hashes. A stored unit state whose input hash is one of them, whose static hash
/// is the one recorded with it and none of whose read keys changed hash since, has the input hash the full check would compute again,
/// so the check accepts its inputs without computing it.
/// </summary>
/// <remarks>
/// <para>Why that is the full check's answer: an input hash is <c>H(static hash, key₁, hash₁, ...)</c> over the state's own read keys.
/// One recorded as current was computed (or recomputed) by the previous run from that static hash, those keys and that run's key
/// hashes; a state holding it with the same static hash and keys whose hashes did not change therefore hashes to it again now. States
/// are trusted to hold the input hash of their own read keys only when this process computed them: the states this store saved
/// (<see cref="UnitStateStore.Saved"/>), or the very states the previous run checked. States read from a file someone else wrote take
/// the full check, once.</para>
/// <para>Every key a candidate unit reads is still hashed each run (the hasher has no list of what changed): an <c>e:</c> key is a
/// lookup, and the rest of each key is kept here so hashing it allocates nothing; <c>r:</c> keys pay what
/// <see cref="Model.ModelSnapshot.ReferrersHash"/> pays. What the record saves is the per-unit work: the input hash of every unit,
/// and the per-key fields a unit state file's key table built for it.</para>
/// </remarks>
internal sealed class SkipBaseline
{
    /// <summary>
    /// One key as the record keeps it: the rest of the key, so hashing it again allocates nothing, and the hash it had in run
    /// <see cref="Run"/>. A skip check works on these in place: each key is claimed once per run (<see cref="Claim"/>), hashed into
    /// <see cref="Now"/> and compared (<see cref="Changed"/>) in parallel, and only once every comparison is done do <see cref="Hash"/>
    /// and <see cref="Run"/> move to this run, so keys shared by several packs' tables compare with the previous run's hash.
    /// </summary>
    /// <param name="key">The key.</param>
    internal sealed class KeyMemo(string key)
    {
        /// <summary>The key.</summary>
        public string Key { get; } = key;

        /// <summary>The key after its kind (<see cref="DependencyHasher.RestOf"/>).</summary>
        public string? Rest { get; set; } = DependencyHasher.RestOf(key);

        /// <summary>The hash the key had in run <see cref="Run"/>, or <see langword="null"/> before the first.</summary>
        public string? Hash { get; set; }

        /// <summary>The run that hashed it.</summary>
        public int Run { get; set; }

        /// <summary>The run that last claimed it for hashing.</summary>
        public int Claim { get; set; }

        /// <summary>The hash this run computed, until it becomes <see cref="Hash"/>.</summary>
        public string? Now { get; set; }

        /// <summary>Whether this run's hash differs from the previous run's (or the previous run did not hash the key).</summary>
        public bool Changed { get; set; }
    }

    /// <summary>An input hash recorded as current: the static hash it was computed from and the last run that found it current.</summary>
    internal sealed class Current
    {
        /// <summary>The static hash.</summary>
        public UnitHash Static { get; set; }

        /// <summary>The run.</summary>
        public int Run { get; set; }
    }

    /// <summary>Key hashes, by key; an entry hashed by an older run than <see cref="Previous"/> is unknown.</summary>
    public Dictionary<string, KeyMemo> Keys { get; } = new(StringComparer.Ordinal);

    /// <summary>Input hashes found current by a skip check (its skipped units), by input hash; an entry of an older run than
    /// <see cref="Previous"/> is unknown.</summary>
    public Dictionary<string, Current> Skipped { get; } = new(StringComparer.Ordinal);

    /// <summary>The previous run's own record, or <see langword="null"/> before the first.</summary>
    public RunRecord? Previous { get; set; }

    /// <summary>What one run leaves for the next.</summary>
    /// <param name="Run">The run's number.</param>
    /// <param name="SettingsHash">The settings it ran with; other settings make the next run check every unit.</param>
    /// <param name="Cache">The run's hasher cache: every key hash the run computed outside the key comparison (full checks, rendering),
    /// which goes on filling while the run renders.</param>
    /// <param name="Rendered">The input hashes the run's hasher computed after the skip check (<see cref="DependencyHasher.InputHashes"/>):
    /// those of the units it rendered, current under the same key hashes.</param>
    /// <param name="Seen">The states the run checked, per pack, held weakly.</param>
    internal sealed record RunRecord(int Run, string SettingsHash, ConcurrentDictionary<string, string> Cache, ConcurrentDictionary<string, UnitHash> Rendered,
        IReadOnlyDictionary<string, WeakReference<IReadOnlyDictionary<string, UnitState>>> Seen)
    {
        /// <summary>The hash a key had in this run, or <see langword="null"/> when this run did not hash it.</summary>
        public string? HashOf(KeyMemo memo) =>
            Cache.TryGetValue(memo.Key, out var hash) ? hash : memo.Run == Run ? memo.Hash : null;

        /// <summary>
        /// Whether an input hash was current in this run, computed from this static hash, with the record's entry when a skip check
        /// found it (<see langword="null"/> when this run rendered it).
        /// </summary>
        public bool WasCurrent(string inputHash, string staticHash, Dictionary<string, Current> skipped, out Current? entry)
        {
            entry = null;
            if (!UnitHash.TryParse(staticHash, out var parsed))
                return false;
            if (skipped.TryGetValue(inputHash, out var current) && current.Run == Run && current.Static == parsed)
            {
                entry = current;
                return true;
            }

            return Rendered.TryGetValue(inputHash, out var rendered) && rendered == parsed;
        }

        /// <summary>Whether these are the states this run checked for the pack.</summary>
        public bool Checked(string pack, IReadOnlyDictionary<string, UnitState> states) =>
            Seen.TryGetValue(pack, out var seen) && seen.TryGetTarget(out var target) && ReferenceEquals(target, states);
    }
}

/// <summary>What the last skip check did (tests, measurements).</summary>
/// <param name="Units">Planned units.</param>
/// <param name="Stored">Units with a stored state.</param>
/// <param name="ByKey">Units whose inputs were accepted by key (no input hash computed).</param>
/// <param name="Checked">Units whose input hash was computed again (today's check).</param>
/// <param name="KeysHashed">Keys hashed for the comparison.</param>
/// <param name="KeysChanged">Of those, keys whose hash changed or was unknown.</param>
/// <param name="OutputsChecked">Recorded outputs checked on disk and in the manifest.</param>
/// <param name="OutputCheckTime">Time spent checking outputs, summed over threads.</param>
internal sealed record SkipStats(int Units, int Stored, int ByKey, int Checked, int KeysHashed, int KeysChanged, int OutputsChecked, TimeSpan OutputCheckTime);
