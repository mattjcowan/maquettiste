using System.Collections.Concurrent;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// What a call on one resolved object computed, kept between runs of a long-lived host (next-phase-2-design.md section 5.5): keyed by
/// (name, kind, site), an entry holds the result, the script's path and hash, the limits, and the sorted keys the call read with their
/// hashes. It is reused while every one of them is unchanged. Nothing is written to disk. Calculated values use it today; resolved
/// rules (section 5.5) share it. Runs take tickets as <see cref="RuleResultCache"/> runs do: an entry a run uses is touched with its
/// ticket, and a whole-model run sweeps the entries it did not use, so a sweep keeps what a concurrent, later run used.
/// </summary>
internal sealed class ResolvedResultCache
{
    private readonly ConcurrentDictionary<(string Name, string Kind, string Site), Entry> _entries = new();
    private long _clock;

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>Takes a ticket for a run.</summary>
    /// <returns>The ticket, greater than every earlier one.</returns>
    public long Begin() => Interlocked.Increment(ref _clock);

    /// <summary>The ticket of the latest run.</summary>
    public long Current => Interlocked.Read(ref _clock);

    /// <summary>The cached result of a call, when the script, the limits and every key it read are unchanged.</summary>
    /// <param name="name">The calculated value's name (or rule id).</param>
    /// <param name="kind">The object's kind.</param>
    /// <param name="site">The object's site.</param>
    /// <param name="script">The script that computed it.</param>
    /// <param name="limits">The sandbox limits.</param>
    /// <param name="hasher">Hashes the keys as they are now.</param>
    /// <param name="ticket">The run's ticket.</param>
    /// <param name="value">The cached result.</param>
    /// <param name="keys">The keys the call read, ordinal.</param>
    /// <returns><see langword="true"/> when the entry is current.</returns>
    public bool TryGet(string name, string kind, string site, ScriptSource script, SandboxLimits limits, IDependencyHasher hasher, long ticket,
        out object? value, out string[] keys)
    {
        value = null;
        keys = [];
        if (!_entries.TryGetValue((name, kind, site), out var entry)
            || !string.Equals(entry.Script, script.Path, StringComparison.Ordinal)
            || !string.Equals(entry.ScriptHash, script.Hash, StringComparison.Ordinal)
            || !Equals(entry.Limits, limits))
            return false;
        for (var i = 0; i < entry.Keys.Length; i++)
        {
            if (!string.Equals(entry.Hashes[i], hasher.CurrentHash(entry.Keys[i]), StringComparison.Ordinal))
                return false;
        }

        entry.Touch(ticket);
        value = entry.Value;
        keys = entry.Keys;
        return true;
    }

    /// <summary>Stores a result with the current hashes of the keys its call read.</summary>
    /// <param name="name">The calculated value's name (or rule id).</param>
    /// <param name="kind">The object's kind.</param>
    /// <param name="site">The object's site.</param>
    /// <param name="script">The script that computed it.</param>
    /// <param name="limits">The sandbox limits.</param>
    /// <param name="keys">The keys the call read, ordinal and distinct.</param>
    /// <param name="hasher">Hashes the keys as they are now.</param>
    /// <param name="value">The result.</param>
    /// <param name="ticket">The run's ticket.</param>
    public void Store(string name, string kind, string site, ScriptSource script, SandboxLimits limits, string[] keys, IDependencyHasher hasher,
        object? value, long ticket)
    {
        var hashes = new string[keys.Length];
        for (var i = 0; i < keys.Length; i++)
            hashes[i] = hasher.CurrentHash(keys[i]);
        _entries[(name, kind, site)] = new Entry(script.Path, script.Hash, limits, keys, hashes, value, ticket);
    }

    /// <summary>Marks an entry used by a run that reuses its value without reading it again (a host's memo), so the run's sweep keeps it.</summary>
    /// <param name="name">The value's name.</param>
    /// <param name="kind">The object's kind.</param>
    /// <param name="site">The object's site.</param>
    /// <param name="ticket">The run's ticket.</param>
    public void Touch(string name, string kind, string site, long ticket)
    {
        if (_entries.TryGetValue((name, kind, site), out var entry))
            entry.Touch(ticket);
    }

    /// <summary>Removes the entries no run used since the ticket was taken.</summary>
    /// <param name="ticket">The ticket of the whole-model run that ends.</param>
    public void Sweep(long ticket)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.LastUsed < ticket)
                _entries.TryRemove(pair);
        }
    }

    private sealed class Entry(string script, string scriptHash, SandboxLimits limits, string[] keys, string[] hashes, object? value, long ticket)
    {
        private long _lastUsed = ticket;

        public string Script { get; } = script;

        public string ScriptHash { get; } = scriptHash;

        public SandboxLimits Limits { get; } = limits;

        public string[] Keys { get; } = keys;

        public string[] Hashes { get; } = hashes;

        public object? Value { get; } = value;

        public long LastUsed => Interlocked.Read(ref _lastUsed);

        public void Touch(long ticket)
        {
            var seen = Interlocked.Read(ref _lastUsed);
            while (seen < ticket)
            {
                var previous = Interlocked.CompareExchange(ref _lastUsed, ticket, seen);
                if (previous == seen)
                    return;
                seen = previous;
            }
        }
    }
}
