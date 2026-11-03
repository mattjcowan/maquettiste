using System.Collections.Concurrent;
using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The per-element results of script rules, kept in memory by one <see cref="ModelValidator"/> (next-phase-1-design.md section 5.7),
/// so a long-lived host (the MCP server, the editor host, watch mode) runs a rule again only on the elements whose inputs changed.
/// Nothing is written to disk. An entry, keyed by (rule id, element id), holds the rule's raw diagnostics (before
/// <c>validation.rules</c> applies) and is reused only while the element's file (hash and path), the rule file's hash, the sandbox
/// limits and the current hash of every key the call recorded (<see cref="RuleKeyHasher"/>) are unchanged. The validator stores only
/// clean runs: no failure (MQ5002, MQ5003, cancellation) and no rule of a file with MQ5005. A whole-model validation drops the entries
/// it did not use (<see cref="Sweep"/>), so memory stays bounded by the rules and elements of the model. Thread-safe.
/// </summary>
internal sealed class RuleResultCache
{
    private readonly ConcurrentDictionary<(string Rule, string Element), Entry> _entries = new();
    private long _clock;

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Starts a validation: the returned ticket marks the entries it uses or stores, and a whole-model validation passes it to
    /// <see cref="Sweep"/>. A later validation running at the same time takes a larger ticket, so the sweep keeps what it uses.
    /// </summary>
    /// <returns>The ticket.</returns>
    public long Begin() => Interlocked.Increment(ref _clock);

    /// <summary>The diagnostics a rule gave on an element, when every input of that run is unchanged.</summary>
    /// <param name="rule">The rule id (without <c>x/</c>).</param>
    /// <param name="document">The element's document now.</param>
    /// <param name="ruleFile">The rule file and its hash now.</param>
    /// <param name="limits">The sandbox limits now.</param>
    /// <param name="hasher">The current hashes of read keys.</param>
    /// <param name="ticket">The validation's ticket.</param>
    /// <param name="diagnostics">The cached raw diagnostics when found.</param>
    /// <returns><see langword="true"/> when the entry is current.</returns>
    public bool TryGet(string rule, ElementDocument document, ScriptSource ruleFile, SandboxLimits limits, RuleKeyHasher hasher, long ticket,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        diagnostics = default;
        if (!_entries.TryGetValue((rule, document.Element.Id), out var entry)
            || !string.Equals(entry.ElementHash, document.Hash, StringComparison.Ordinal)
            || !string.Equals(entry.Path, document.Path, StringComparison.Ordinal)
            || !string.Equals(entry.RuleFile, ruleFile.Path, StringComparison.Ordinal)
            || !string.Equals(entry.RuleFileHash, ruleFile.Hash, StringComparison.Ordinal)
            || !Equals(entry.Limits, limits))
            return false;
        for (var i = 0; i < entry.Keys.Length; i++)
        {
            if (!string.Equals(entry.Hashes[i], hasher.CurrentHash(entry.Keys[i]), StringComparison.Ordinal))
                return false;
        }

        entry.Touch(ticket);
        diagnostics = entry.Diagnostics;
        return true;
    }

    /// <summary>Stores the raw diagnostics of a clean run, with the current hashes of the keys it recorded.</summary>
    /// <param name="rule">The rule id (without <c>x/</c>).</param>
    /// <param name="document">The element's document.</param>
    /// <param name="ruleFile">The rule file.</param>
    /// <param name="limits">The sandbox limits of the run.</param>
    /// <param name="keys">The keys the call recorded.</param>
    /// <param name="hasher">The current hashes of read keys, over the snapshot the call read.</param>
    /// <param name="diagnostics">The raw diagnostics.</param>
    /// <param name="ticket">The validation's ticket.</param>
    public void Store(string rule, ElementDocument document, ScriptSource ruleFile, SandboxLimits limits, IReadOnlyCollection<string> keys,
        RuleKeyHasher hasher, IReadOnlyList<Diagnostic> diagnostics, long ticket)
    {
        var sorted = keys.Order(StringComparer.Ordinal).ToArray();
        var hashes = new string[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
            hashes[i] = hasher.CurrentHash(sorted[i]);
        _entries[(rule, document.Element.Id)] = new Entry(document.Hash, document.Path, ruleFile.Path, ruleFile.Hash, limits, sorted, hashes,
            [.. diagnostics], ticket);
    }

    /// <summary>Drops the entries no validation used or stored since <paramref name="ticket"/> was taken.</summary>
    /// <param name="ticket">The ticket of the whole-model validation that just ended.</param>
    public void Sweep(long ticket)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.LastUsed < ticket)
                _entries.TryRemove(pair);
        }
    }

    /// <summary>One cached run.</summary>
    private sealed class Entry(string elementHash, string path, string ruleFile, string ruleFileHash, SandboxLimits limits, string[] keys,
        string[] hashes, ImmutableArray<Diagnostic> diagnostics, long ticket)
    {
        private long _lastUsed = ticket;

        public string ElementHash { get; } = elementHash;

        public string Path { get; } = path;

        public string RuleFile { get; } = ruleFile;

        public string RuleFileHash { get; } = ruleFileHash;

        public SandboxLimits Limits { get; } = limits;

        /// <summary>The recorded keys, ordinal.</summary>
        public string[] Keys { get; } = keys;

        /// <summary>The hash of each key when the run was stored.</summary>
        public string[] Hashes { get; } = hashes;

        public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics;

        /// <summary>The largest ticket of a validation that used or stored the entry.</summary>
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
