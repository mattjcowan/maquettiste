using System.Collections.Concurrent;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The engine's long-lived side of calculated values (next-phase-2-design.md section 5.5): the sandbox factory, the parallelism and the
/// cache of values by the keys they read, shared by the hosts of every resolved model. A snapshot without calculation files gets no
/// host, so a project that declares no calculated value creates no pool and evaluates nothing. The calculation files load once per
/// (files, limits) into one pool that validation and every host share (<see cref="Load"/>), and validation tells the checks after
/// resolution what the rule files register (<see cref="PublishRules"/>), so they never build a rule pool to find no resolved rule.
/// </summary>
/// <param name="scripts">The sandbox factory.</param>
/// <param name="parallelism">The engines a host's pool keeps idle.</param>
internal sealed class CalculationService(IScriptSandboxFactory scripts, int parallelism)
{
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, bool> _registersResolved = new(StringComparer.Ordinal);
    private (string Files, SandboxLimits Limits, Lazy<CalculationHost.Loaded> Loaded)? _files;
    private (ModelSnapshot Snapshot, ResolvedRulePool Rules)? _rules;
    private long _pools;
    private long _evaluations;

    /// <summary>The sandbox factory.</summary>
    public IScriptSandboxFactory Scripts { get; } = scripts ?? throw new ArgumentNullException(nameof(scripts));

    /// <summary>The engines a host's pool keeps idle.</summary>
    public int Parallelism { get; } = Math.Max(1, parallelism);

    /// <summary>The values of every host, by (name, kind, site) and the keys they read.</summary>
    public ResolvedResultCache Cache { get; } = new();

    /// <summary>The calculation pools created so far (a pool whose files failed to load included).</summary>
    public long PoolsCreated => Interlocked.Read(ref _pools);

    /// <summary>The calculated values evaluated so far: calls into a sandbox, not reuses from a memo or the cache.</summary>
    public long Evaluations => Interlocked.Read(ref _evaluations);

    /// <summary>The host of a resolution of the snapshot, or <see langword="null"/> when it has no calculation file.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The host.</returns>
    public CalculationHost? CreateHost(ModelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.CalculationScripts.Count == 0 ? null : new CalculationHost(this, snapshot);
    }

    /// <summary>
    /// The calculation files of a snapshot loaded into one pool, with their declarations and load diagnostics (a file that does not load
    /// is MQ5002, a limit MQ5003, at its line and column, and the others load without it; the pool's MQ5005 warnings follow). Loaded once
    /// per (files and their hashes, limits) and kept until they change, so a whole-model validation and every host of a resolved model
    /// read one load; nothing is evaluated.
    /// </summary>
    /// <param name="snapshot">The snapshot (it has calculation files).</param>
    /// <returns>The load.</returns>
    internal CalculationHost.Loaded Load(ModelSnapshot snapshot)
    {
        var files = HashBuilder.Of(["mq-calculation-files-1", .. snapshot.CalculationScripts.Select(s => s.Path + "\u0000" + s.Hash)]);
        var limits = snapshot.Settings.Limits;
        Lazy<CalculationHost.Loaded> loaded;
        lock (_gate)
        {
            if (_files is { } cached && string.Equals(cached.Files, files, StringComparison.Ordinal) && cached.Limits.Equals(limits))
            {
                loaded = cached.Loaded;
            }
            else
            {
                loaded = new Lazy<CalculationHost.Loaded>(() => CalculationHost.Load(this, snapshot), LazyThreadSafetyMode.ExecutionAndPublication);
                _files = (files, limits, loaded);
            }
        }

        return loaded.Value;
    }

    /// <summary>The hash of the rule scripts and libraries a rule pool loads.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The hash.</returns>
    internal static string RulesKey(ModelSnapshot snapshot) => HashBuilder.Of(
        ["mq-resolved-rules-1", .. snapshot.RuleScripts.Select(s => s.Path + "\u0000" + s.Hash), .. snapshot.ScriptLibraries.Select(l => l.Pack + "\u0000" + l.Hash)]);

    /// <summary>Whether the rule scripts of a <see cref="RulesKey"/> register a rule with scope "resolved", or <see langword="null"/>
    /// when no validation or check has loaded them yet.</summary>
    /// <param name="rulesKey">The key.</param>
    /// <returns>The answer.</returns>
    internal bool? RegistersResolved(string rulesKey) => _registersResolved.TryGetValue(rulesKey, out var registers) ? registers : null;

    /// <summary>Notes whether the rule scripts of a <see cref="RulesKey"/> register a resolved rule (at most 64 keys are kept).</summary>
    /// <param name="rulesKey">The key.</param>
    /// <param name="registers">Whether they do.</param>
    internal void NoteRules(string rulesKey, bool registers)
    {
        if (_registersResolved.Count >= 64)
            _registersResolved.Clear();
        _registersResolved[rulesKey] = registers;
    }

    /// <summary>
    /// What a validation's rule pool registers: whether a rule has scope "resolved" and, with one, the pool and its libraries for the
    /// checks of that snapshot (the last such snapshot only). A validation of another snapshot without a pool to keep drops the one kept.
    /// </summary>
    /// <param name="snapshot">The validated snapshot.</param>
    /// <param name="registers">Whether a rule has scope "resolved".</param>
    /// <param name="rules">The pool to keep for the snapshot's checks, or <see langword="null"/>.</param>
    internal void PublishRules(ModelSnapshot snapshot, bool registers, ResolvedRulePool? rules)
    {
        NoteRules(RulesKey(snapshot), registers);
        lock (_gate)
        {
            if (rules is not null)
                _rules = (snapshot, rules);
            else if (_rules is { } kept && !ReferenceEquals(kept.Snapshot, snapshot))
                _rules = null;
        }
    }

    /// <summary>The rule pool a validation of the snapshot kept for its checks, or <see langword="null"/>.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The pool and its libraries.</returns>
    internal ResolvedRulePool? RulesOf(ModelSnapshot snapshot)
    {
        lock (_gate)
            return _rules is { } kept && ReferenceEquals(kept.Snapshot, snapshot) ? kept.Rules : null;
    }

    internal void CountPool() => Interlocked.Increment(ref _pools);

    internal void CountEvaluation() => Interlocked.Increment(ref _evaluations);
}

/// <summary>A validation's rule pool with its library pools, kept for the checks after resolution (<see cref="CalculationService.PublishRules"/>);
/// never disposed (its engines are managed), so a check that still uses it when another replaces it is unaffected.</summary>
/// <param name="Pool">The rule pool.</param>
/// <param name="Libraries">The library pools by pack.</param>
internal sealed record ResolvedRulePool(IScriptSandboxPool Pool, IReadOnlyDictionary<string, ModelValidator.RuleLibrary> Libraries);
