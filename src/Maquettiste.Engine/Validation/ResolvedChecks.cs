using System.Collections.Frozen;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Validation;

/// <summary>What the checks after resolution found.</summary>
/// <param name="Findings">Failures of calculated values (and, later, the findings of declared rules).</param>
/// <param name="ScriptFindings">The findings of rules registered with scope "resolved", which a validation without script rules leaves
/// out.</param>
internal sealed record ResolvedCheckResult(IReadOnlyList<Diagnostic> Findings, IReadOnlyList<Diagnostic> ScriptFindings)
{
    /// <summary>Nothing found.</summary>
    public static ResolvedCheckResult Empty { get; } = new([], []);

    /// <summary>Both lists, findings first.</summary>
    public IEnumerable<Diagnostic> All => Findings.Concat(ScriptFindings);
}

/// <summary>
/// The checks after resolution (next-phase-2-design.md section 5.5): on a resolved model whose validation has no error but MQ4101, they
/// evaluate every calculated value and run the resolved rules (<c>scope: "resolved"</c>) once per object of their kinds. Whole-model
/// runs call them (every validate path, through the model store, once per snapshot; and a generation run before it plans); no preview
/// and no element save does. A later stage adds its own findings to <see cref="ResolvedCheckResult.Findings"/>.
/// </summary>
internal sealed class ResolvedChecks(EngineOptions options, IScriptSandboxFactory scripts, CalculationService calculations)
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = FrozenDictionary<string, object?>.Empty;

    private int _poolsCreated;

    /// <summary>The rule pools the checks built themselves, each with its library pools (tests: a validation that loaded the rule files
    /// leaves none to build).</summary>
    internal int PoolsCreated => Volatile.Read(ref _poolsCreated);

    /// <summary>
    /// Evaluates every calculated value and runs the resolved rules. Work runs in parallel over objects; the result does not depend on
    /// the degree. Findings are located (<see cref="ResolvedLocations"/>) and positioned, <c>validation.rules</c> applied, in diagnostic
    /// order.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="resolved">Its resolved model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The findings.</returns>
    internal async Task<ResolvedCheckResult> RunAsync(ModelSnapshot snapshot, ResolvedModel resolved, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolved);
        var host = resolved.Calculated;
        // Whether the rule files register a resolved rule is what the validation of the snapshot found (CalculationService.PublishRules),
        // or what an earlier check found; unknown, the rule files are loaded here once to learn it.
        var rulesKey = snapshot.RuleScripts.Count == 0 ? null : CalculationService.RulesKey(snapshot);
        var mayHaveRules = rulesKey is not null && calculations.RegistersResolved(rulesKey) != false;
        if (host is null && !mayHaveRules)
            return ResolvedCheckResult.Empty;

        var cache = host?.Cache ?? calculations.Cache;
        var ticket = cache.Begin();
        IReadOnlyList<Diagnostic> failures = [];
        List<Diagnostic> found = [];
        try
        {
            if (host is not null)
                failures = await Task.Run(() => host.EvaluateAll(ticket, ct), ct).ConfigureAwait(false);
            if (mayHaveRules)
                found = await Task.Run(() => RunRules(snapshot, resolved, rulesKey!, cache, ticket, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            cache.Sweep(ticket);
        }

        return new ResolvedCheckResult(await FinishAsync(snapshot, [.. failures], ct).ConfigureAwait(false),
            await FinishAsync(snapshot, found, ct).ConfigureAwait(false));
    }

    /// <summary><c>validation.rules</c> applied, line and column found from the pointer, distinct, in diagnostic order.</summary>
    private async Task<IReadOnlyList<Diagnostic>> FinishAsync(ModelSnapshot snapshot, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        if (diagnostics.Count == 0)
            return [];
        var positions = new Positions(options, snapshot);
        var placed = new List<Diagnostic>(diagnostics.Count);
        foreach (var d in ModelValidator.ApplySettings(snapshot.Settings, diagnostics))
        {
            if (d.Line is null && d.FilePath is not null && d.JsonPointer is not null
                && await positions.LocateAsync(d.FilePath, d.JsonPointer, ct).ConfigureAwait(false) is { } at)
                placed.Add(d with { Line = at.Line, Column = at.Column });
            else
                placed.Add(d);
        }

        return [.. placed.Distinct().Order(Diagnostic.Order)];
    }

    /// <summary>
    /// Runs the resolved rules on the rule pool the snapshot's validation kept (<see cref="CalculationService.RulesOf"/>), else on one
    /// built here with the project's libraries, as the validator creates its own: each rule on each object
    /// of its kinds, except a rule <c>validation.rules</c> sets <c>off</c>. Raw results are cached per (rule, kind, site) in the
    /// calculated values' cache, with the rule file's hash folded with the libraries'. A rule that throws is MQ5002 and one that breaks
    /// a limit MQ5003, on the rule file, with the object's id; a rule that read a failed calculated value reports nothing, since that
    /// value's failure is reported once already. A rule file that does not load is the validator's to report (validation then has an
    /// error, and no check runs).
    /// </summary>
    private List<Diagnostic> RunRules(ModelSnapshot snapshot, ResolvedModel resolved, string rulesKey, ResolvedResultCache cache, long ticket,
        CancellationToken ct)
    {
        var size = Math.Max(1, options.EffectiveParallelism);
        var limits = snapshot.Settings.Limits;
        var kept = calculations.RulesOf(snapshot);
        IReadOnlyDictionary<string, ModelValidator.RuleLibrary> libraryPools = kept?.Libraries ?? ModelValidator.LibraryPools(snapshot, scripts, size, [], ct);
        try
        {
            IScriptSandboxPool pool;
            if (kept is not null)
            {
                pool = kept.Pool;
            }
            else
            {
                Interlocked.Increment(ref _poolsCreated);
                IReadOnlyList<ScriptLibrary> libraries = [.. snapshot.ScriptLibraries.Where(l => libraryPools.ContainsKey(l.Pack))
                    .Select(l => new ScriptLibrary(l.Pack, libraryPools[l.Pack].Helpers))];
                try
                {
                    pool = libraries.Count == 0 ? scripts.CreatePool(snapshot.RuleScripts, limits, size, ct)
                        : scripts.CreatePool(snapshot.RuleScripts, libraries, limits, size, ct);
                }
                catch (Exception e) when (e is ScriptErrorException or ScriptLimitException)
                {
                    return [];
                }
            }

            using (kept is null ? pool : null)
            {
                var rules = pool.Registrations.Where(r => r is { Kind: ScriptRegistrationKind.Rule, Resolved: true, Kinds: not null })
                    .DistinctBy(r => ModelValidator.Bare(r.Name), StringComparer.Ordinal)
                    .OrderBy(r => ModelValidator.Bare(r.Name), StringComparer.Ordinal).ToList();
                calculations.NoteRules(rulesKey, rules.Count > 0);
                var settings = snapshot.Settings.Validation.Rules;
                rules.RemoveAll(r => settings.TryGetValue("x/" + ModelValidator.Bare(r.Name), out var setting) && string.Equals(setting, "off", StringComparison.Ordinal));
                if (rules.Count == 0)
                    return [];

                var files = ModelValidator.CachedRuleFiles(snapshot, pool, resolved: true);
                var hasher = resolved.Calculated?.Hasher
                    ?? new DependencyHasher(snapshot, resolved, new PackSet([], []), FrozenDictionary<string, SchemaDiff.SchemaDiffResult>.Empty);
                var calls = new ModelValidator.RuleLibraryCalls(libraryPools);
                var objects = ResolvedKinds.ObjectsOf(resolved, rules.SelectMany(r => r.Kinds!));
                var results = new List<Diagnostic>?[objects.Count];
                Parallel.For(0, objects.Count, new ParallelOptions { MaxDegreeOfParallelism = size, CancellationToken = ct },
                    () => new Lease(pool),
                    (i, _, lease) =>
                    {
                        results[i] = Check(objects[i], rules, files, snapshot, resolved, cache, ticket, hasher, calls, lease, ct);
                        return lease;
                    },
                    lease => lease.Dispose());
                return [.. results.Where(r => r is not null).SelectMany(r => r!)];
            }
        }
        finally
        {
            if (kept is null)
            {
                foreach (var library in libraryPools.Values)
                    library.Pool?.Dispose();
            }
        }
    }

    private static List<Diagnostic>? Check(RAnnotated value, List<ScriptRegistration> rules, Dictionary<string, ScriptSource> files, ModelSnapshot snapshot,
        ResolvedModel resolved, ResolvedResultCache cache, long ticket, DependencyHasher hasher, IScriptLibraryCalls calls, Lease lease, CancellationToken ct)
    {
        List<Diagnostic>? found = null;
        var settings = snapshot.Settings.Validation.Rules;
        var limits = snapshot.Settings.Limits;
        string? site = null;
        foreach (var registration in rules)
        {
            if (!registration.Kinds!.Contains(value.Kind))
                continue;
            ct.ThrowIfCancellationRequested();
            var rule = ModelValidator.Bare(registration.Name);
            var ruleId = "x/" + rule;
            var failureSeverity = ModelValidator.SeverityOf(settings.GetValueOrDefault(ruleId));
            var file = files.GetValueOrDefault(rule);
            site ??= ResolvedKinds.SiteOf(value, snapshot);
            IReadOnlyList<Diagnostic> raw;
            if (file is not null && cache.TryGet(ruleId, value.Kind, site, file, limits, hasher, ticket, out var cached, out _))
            {
                raw = (IReadOnlyList<Diagnostic>)cached!;
            }
            else
            {
                var reads = new Reads();
                try
                {
                    var context = new ScriptCallContext(reads, value.Id, NoParameters, ct) { Libraries = calls };
                    raw = lease.Sandbox.RunResolvedRule(rule, value, resolved, context);
                }
                catch (CalculatedValueException)
                {
                    continue; // the value that failed is reported once, on itself
                }
                catch (ScriptLimitException e)
                {
                    raw = [e.Diagnostic with { Rule = "MQ5003", ElementId = value.Id, FilePath = e.Diagnostic.FilePath ?? registration.DeclaredIn }];
                    file = null;
                }

                // Only a clean run is kept: a rule that threw ends with its MQ5002 (a catalog id) after its reports.
                if (file is not null && !raw.Any(d => RuleCatalog.TryGet(d.Rule, out _)))
                    cache.Store(ruleId, value.Kind, site, file, limits, reads.Sorted(), hasher, raw, ticket);
            }

            foreach (var d in raw)
            {
                (found ??= []).Add(RuleCatalog.TryGet(d.Rule, out _)
                    ? d with { Severity = failureSeverity ?? d.Severity, ElementId = d.ElementId ?? value.Id, FilePath = d.FilePath ?? registration.DeclaredIn }
                    : ResolvedLocations.Place(d with { Rule = d.Rule.StartsWith("x/", StringComparison.Ordinal) ? d.Rule : ruleId }, value, snapshot));
            }
        }

        return found;
    }

    /// <summary>One worker's lease, rented on first use.</summary>
    private sealed class Lease(IScriptSandboxPool pool) : IDisposable
    {
        private IScriptSandboxLease? _lease;

        public IScriptSandbox Sandbox => (_lease ??= pool.Rent()).Sandbox;

        public void Dispose() => _lease?.Dispose();
    }

    /// <summary>The keys one rule call records.</summary>
    private sealed class Reads : IReadRecorder
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public void Record(string dependencyKey) => _keys.Add(dependencyKey);

        public string[] Sorted() => [.. _keys.Order(StringComparer.Ordinal)];
    }
}
