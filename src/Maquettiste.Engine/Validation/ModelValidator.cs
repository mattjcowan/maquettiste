using System.Collections.Frozen;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Stage 2: built-in rules, extension schemas and JavaScript rules (W2; engine-design.md section 6).
/// <para>
/// The report holds the snapshot's load diagnostics (MQ1xxx, filtered to the scope), the built-in rules of every file in scope,
/// extension schema failures, extension file problems (whole-model scope only) and JavaScript rule results: element rules on each file
/// in scope, their results reused from <see cref="RuleResultCache"/> while their inputs are unchanged, and whole-model rules
/// (<c>scope: "model"</c>) once per whole-model validation (next-phase-1-design.md section 5.7). Rule severities
/// follow <c>validation.rules</c> in <c>maquettiste.json</c> (<c>off</c> drops a rule; MQ1xxx cannot be turned off, and the MQ1xxx
/// errors that leave a file out of the snapshot keep their severity). Line and
/// column are filled for every diagnostic with a file and pointer. Files are validated in parallel
/// (<see cref="EngineOptions.MaxDegreeOfParallelism"/>); the result does not depend on the degree.
/// </para>
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="schemas">The schema registry (kept for the design's constructor; extension schemas are compiled here, see README).</param>
/// <param name="scripts">The sandbox factory (JavaScript rules).</param>
/// <param name="resolver">The resolver that judges pending migration facts stale (MQ4103, next-phase-1-design.md section 3.2); without
/// one every pending fact is MQ4101.</param>
/// <param name="seedRows">The engine's cache of rows script runs (next-phase-2-design.md section 3.4); a cache of its own when absent.</param>
/// <param name="calculations">The service that loads the calculation files once for validation and resolution, and that the checks after
/// resolution read the rule registrations from; the resolver's when absent, else one of its own.</param>
internal sealed class ModelValidator(EngineOptions options, ISchemaRegistry schemas, IScriptSandboxFactory scripts, IModelResolver? resolver = null,
    SeedRowsCache? seedRows = null, CalculationService? calculations = null)
    : IModelValidator
{
    private readonly CalculationService _calculations = calculations ?? (resolver as ModelResolver)?.Calculations
        ?? new CalculationService(scripts, Math.Max(1, options.EffectiveParallelism));

    private static readonly IReadOnlyDictionary<string, object?> NoParameters = FrozenDictionary<string, object?>.Empty;

    /// <summary>
    /// The MQ1xxx rules whose file is left out of the snapshot (or ignored), so <c>validation.rules</c> cannot change their severity:
    /// lowering one would let the file vanish from generation without an error.
    /// </summary>
    private static readonly FrozenSet<string> FixedSeverityRules =
        new[] { "MQ1001", "MQ1002", "MQ1004", "MQ1006", "MQ1007", "MQ1009", "MQ7012" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The walker of <see cref="TargetIds"/> (thread-safe; it caches metadata).</summary>
    private static readonly ReferenceWalker SelectionWalker = new();

    private readonly ReferenceWalker _walker = new();

    /// <summary>The engine's cache of rows script runs (next-phase-2-design.md section 3.4), shared with the resolver.</summary>
    private readonly SeedRowsCache _seedRows = seedRows ?? new SeedRowsCache();

    /// <summary>The per-element results of script rules, reused while their inputs are unchanged (next-phase-1-design.md section 5.7).</summary>
    private readonly RuleResultCache _ruleResults = new();

    /// <summary>The rule result cache, for tests.</summary>
    internal RuleResultCache RuleResults => _ruleResults;

    private int _declaredRuleChecks;

    /// <summary>How many validations checked rule declaration files (tests: a project without them checks none).</summary>
    internal int DeclaredRuleChecksRun => Volatile.Read(ref _declaredRuleChecks);

    /// <summary>The schema registry.</summary>
    internal ISchemaRegistry Schemas => schemas;

    /// <inheritdoc/>
    public async Task<ValidationReport> ValidateAsync(ModelSnapshot model, ValidationScope scope, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        scope ??= ValidationScope.All;
        ct.ThrowIfCancellationRequested();

        var active = ActiveDocuments(model);
        var wholeModel = scope.ElementIds is null;
        var shared = new ValidationContext(model, active, _walker, null, wholeModel, scripts, _seedRows)
        {
            DeferScriptedRows = wholeModel && scope.DeferScriptedRows,
        };
        var targets = SelectTargets(shared, active, scope);
        if (!wholeModel)
            shared.Scope = targets.Select(d => d.Element.Id).ToHashSet(StringComparer.Ordinal);
        var parallelism = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;
        var diagnostics = new List<Diagnostic>();

        // Load diagnostics, filtered to the scope.
        var targetIds = targets.Select(d => d.Element.Id).ToHashSet(StringComparer.Ordinal);
        var targetPaths = targets.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var d in model.LoadDiagnostics)
        {
            if (wholeModel || (d.FilePath is not null && targetPaths.Contains(d.FilePath))
                || (d.ElementId is not null && model.GetDocument(d.ElementId) is { } owner && targetIds.Contains(owner.Element.Id)))
            {
                diagnostics.Add(d);
            }
        }

        var extensions = new ExtensionSet(model, wholeModel ? diagnostics : null);
        if (wholeModel)
        {
            diagnostics.AddRange(ReferenceDataRules.CheckSettings(model));
            diagnostics.AddRange(LocalizationRules.Check(model));
            diagnostics.AddRange(BrandingRules.Check(model, options.EffectiveModelRoot));
        }

        // JavaScript rules: one pool per validation (engine-design.md section 10).
        var needScripts = model.RuleScripts.Count > 0 && (scope.IncludeScriptRules || NamesRules(targets));
        IScriptSandboxPool? pool = null;
        FrozenSet<string>? ruleNames = model.RuleScripts.Count == 0 ? FrozenSet<string>.Empty : null;
        // The rule ids the rule scripts register, with the first file that registers each: what MQ5015 compares declared rules with.
        Dictionary<string, string>? scriptRules = model.RuleScripts.Count == 0 ? [] : null;
        IReadOnlyList<string> rules = [];
        IReadOnlyList<ScriptRegistration> modelRules = [];
        var libraryPools = new Dictionary<string, RuleLibrary>(StringComparer.Ordinal);
        var failed = false;
        if (needScripts)
        {
            // The project's library packs (next-phase-2-design.md section 5.2): one pool each, in dependency order, so maquettiste.use
            // in a rule reaches their helpers. A library that does not load is reported on its script and left out.
            var poolSize = Math.Max(1, Math.Min(parallelism, targets.Count));
            libraryPools = LibraryPools(model, scripts, poolSize, diagnostics, ct);
            IReadOnlyList<ScriptLibrary> libraries = [.. model.ScriptLibraries.Where(l => libraryPools.ContainsKey(l.Pack))
                .Select(l => new ScriptLibrary(l.Pack, libraryPools[l.Pack].Helpers))];

            // A rule file that does not load (a syntax error, a registration without an id, a limit) is reported on that file with its
            // line and column, and the other rule files still run without it. While one is broken the set of rule ids is unknown, so
            // MQ2007 (an unknown rule) is not reported against the ids it would have registered.
            var loadable = model.RuleScripts.ToList();
            while (loadable.Count > 0)
            {
                try
                {
                    pool = libraries.Count == 0 ? scripts.CreatePool(loadable, model.Settings.Limits, poolSize, ct)
                        : scripts.CreatePool(loadable, libraries, model.Settings.Limits, poolSize, ct);
                    var registered = pool.Registrations.Where(r => r.Kind == ScriptRegistrationKind.Rule).ToList();
                    // Element rules run on each document; whole-model rules (scope "model") once, after them, on whole-model runs only.
                    // Resolved rules (scope "resolved") run in the checks after resolution (ResolvedChecks), never here.
                    rules = [.. registered.Where(r => !r.WholeModel && !r.Resolved).Select(r => Bare(r.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                    modelRules = [.. registered.Where(r => r.WholeModel).DistinctBy(r => Bare(r.Name), StringComparer.Ordinal)
                        .OrderBy(r => Bare(r.Name), StringComparer.Ordinal)];
                    ruleNames = failed ? null : registered.Select(r => Bare(r.Name)).ToFrozenSet(StringComparer.Ordinal);
                    scriptRules = failed ? null : registered.GroupBy(r => Bare(r.Name), StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Select(r => r.DeclaredIn).Min(StringComparer.Ordinal)!, StringComparer.Ordinal);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e) when (e is ScriptErrorException or ScriptLimitException)
                {
                    var (rule, located) = e is ScriptLimitException limit ? ("MQ5003", limit.Diagnostic) : ("MQ5002", ((ScriptErrorException)e).Diagnostic);
                    var broken = loadable.FindIndex(s => string.Equals(s.Path, located.FilePath, StringComparison.Ordinal));
                    diagnostics.Add(LoadFailure(model, rule, located, broken >= 0 ? loadable[broken].Path : null));
                    failed = true;
                    if (broken < 0)
                        break;
                    loadable.RemoveAt(broken);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    diagnostics.Add(PoolFailure(model, "MQ5002", "Loading the validation rule scripts failed: " + e.Message, null));
                    break;
                }
            }

            // The rule files' load warnings (MQ5005: top-level let or var) are about the files, so whole-model reports carry them.
            if (wholeModel && pool is not null)
                diagnostics.AddRange(pool.Warnings);
        }

        // Calculation files (next-phase-2-design.md section 5.5): a whole-model validation reports a file that does not load (MQ5002,
        // MQ5003) and top-level let or var (MQ5005) on it, from the service's one load that the resolved model's host then evaluates with.
        // Without calculation files nothing is loaded.
        Func<string, string, bool>? declaresCalculated = model.CalculationScripts.Count == 0 ? static (_, _) => false : null;
        if (wholeModel && scope.IncludeScriptRules && model.CalculationScripts.Count > 0)
        {
            var loaded = _calculations.Load(model);
            diagnostics.AddRange(loaded.Diagnostics);
            if (!loaded.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                declaresCalculated = (kind, name) => loaded.ByKind.TryGetValue(kind, out var declared) && declared.ByName.ContainsKey(name);
        }

        // Rule declaration files (next-phase-2-design.md section 5.6): whole-model validations check every declaration, so a broken one
        // stops resolution as a broken rule script does. Ids are compared with the rule scripts' only when every rule script loaded, and
        // calculated values in paths only when the calculation files did. A project without declaration files does none of this.
        if (wholeModel && model.DeclaredRules.Count > 0)
        {
            Interlocked.Increment(ref _declaredRuleChecks);
            diagnostics.AddRange(DeclaredRuleChecks.Check(model, scriptRules, declaresCalculated));
        }

        var runtime = targets.Any(d => d.Element is Scenario) ? new ProcessRuntime(model, parallelism, ct) : null;
        var facts = targets.Any(d => d.Element is Migration) ? MigrationRules.Analyze(model) : null;
        if (facts is not null && resolver is not null)
        {
            // Pending facts whose change the model no longer shows are stale (MQ4103), never pending: with a resolver, the databases
            // that hold pending facts are resolved to judge them; without one (the fixture harness), every pending fact is MQ4101.
            var pending = targets.Select(d => d.Element).OfType<Migration>()
                .Where(f => model.Get<Database>(f.Database) is { } database && MigrationRules.IntentFindings(f, database.Dialect).Any()).ToList();
            if (pending.Count > 0)
            {
                foreach (var (id, message) in await Migrations.StaleFacts.PendingAsync(model, pending, resolver, parallelism, ct).ConfigureAwait(false))
                    facts.MarkStale(id, message);
            }
        }

        var ticket = _ruleResults.Begin();
        var runScripts = scope.IncludeScriptRules && pool is not null && rules.Count > 0;
        var kept = false;
        try
        {
            var context = shared.WithRuleNames(ruleNames);
            var calls = new RuleLibraryCalls(libraryPools);
            var ruleRun = runScripts ? new RuleRun(pool!, rules, RuleKinds(pool!), CachedRuleFiles(model, pool!), _ruleResults, ticket, calls) : null;
            var perDocument = new IReadOnlyList<Diagnostic>[targets.Count];
            var done = 0;
            await Parallel.ForEachAsync(
                Enumerable.Range(0, targets.Count),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                (i, token) =>
                {
                    var report = new Report(targets[i]);
                    BuiltinRules.Validate(context, report);
                    MemberRules.Validate(context, report);
                    StatusRules.Validate(context, report);
                    PackageRules.Validate(context, report);
                    ProcessRules.Validate(context, report);
                    RevisionRules.Validate(context, report);
                    OperationRules.Validate(context, report);
                    ExpressionRules.Validate(context, report);
                    CustomRules.Validate(context, report);
                    ScenarioRules.Validate(context, report, runtime);
                    if (facts is not null)
                        MigrationRules.Validate(context, report, facts);
                    extensions.Check(context, report);
                    SuppressionRules.Validate(context, report);
                    if (ruleRun is not null)
                        RunScripts(ruleRun, model, report, token);
                    perDocument[i] = report.Items;
                    progress?.Report(new ProgressUpdate(PipelineStage.Validate, Interlocked.Increment(ref done), targets.Count, targets[i].Path, null));
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            foreach (var items in perDocument)
                diagnostics.AddRange(items);

            // Whole-model rules run once per whole-model validation, on one lease, after the per-document loop (section 5.7); scoped
            // validations (element saves) skip them, so they never refuse a save over elements outside the change.
            if (wholeModel && scope.IncludeScriptRules && pool is not null && modelRules.Count > 0)
                RunModelRules(pool, modelRules, model, diagnostics, calls, ct);
            kept = PublishRules(model, failed ? null : pool, libraryPools, ct);
        }
        finally
        {
            if (!kept)
            {
                pool?.Dispose();
                foreach (var library in libraryPools.Values)
                    library.Pool?.Dispose();
            }

            runtime?.Dispose();
        }

        // A whole-model validation with script rules used every entry that is still current, so the others go (bounding memory). That
        // holds when no element rule ran too (every rule file deleted or failing to load, or only whole-model rules left): all go.
        if (wholeModel && scope.IncludeScriptRules)
            _ruleResults.Sweep(ticket);

        ct.ThrowIfCancellationRequested();
        var configured = ApplySettings(model.Settings, diagnostics).Distinct().ToList(); // one diagnostic per distinct finding
        // Suppressions (next-phase-2-design.md section 5.6), after severities and before tags collapse, so a save sees them removed.
        var suppressions = Suppressions.Of(model);
        IReadOnlyList<SuppressedFinding> suppressed = [];
        if (!suppressions.IsEmpty)
            configured = [.. suppressions.Apply(configured, out suppressed)];
        if (wholeModel)
            configured = CollapseUndeclaredTags(model, configured);
        var positioned = await AddPositionsAsync(model, configured, ct).ConfigureAwait(false);
        if (suppressed.Count == 0)
            return ValidationReport.From(positioned);
        var findings = await AddPositionsAsync(model, [.. suppressed.Select(s => s.Finding)], ct).ConfigureAwait(false);
        return ValidationReport.From(positioned, suppressed.Select((s, i) => s with { Finding = findings[i] }));
    }

    /// <summary>
    /// The whole model's MQ2006 findings, one per tag and severity instead of one per use (2026-10-07: a vocabulary created on a model
    /// that already uses tags turned thousands of uses into as many notes): the first use in path and pointer order carries the count
    /// of uses and elements and a few element names. A validation of some elements (a save) still reports each of their uses, and so
    /// does a strict vocabulary's error, which marks every element that has one.
    /// </summary>
    internal static List<Diagnostic> CollapseUndeclaredTags(ModelSnapshot model, List<Diagnostic> diagnostics)
    {
        // Errors (a strict vocabulary) stay one per use: the explorer marks each element that has one.
        var groups = diagnostics.Where(d => d.Rule == "MQ2006" && d.Severity != DiagnosticSeverity.Error).GroupBy(d => (d.Severity, d.Message))
            .Where(g => g.Count() > 1).ToList();
        if (groups.Count == 0)
            return diagnostics;
        var dropped = new HashSet<Diagnostic>(groups.SelectMany(g => g));
        var result = diagnostics.Where(d => !dropped.Contains(d)).ToList();
        foreach (var group in groups)
        {
            var uses = group.OrderBy(d => d.FilePath, StringComparer.Ordinal).ThenBy(d => d.JsonPointer, StringComparer.Ordinal).ToList();
            var elements = uses.Select(d => d.ElementId is { } id ? model.GetDocument(id)?.Element.Id ?? id : d.FilePath).OfType<string>()
                .Distinct(StringComparer.Ordinal).ToList();
            var names = elements.Select(id => model.GetDocument(id)?.Element.Name is { Length: > 0 } n ? n : id).Order(StringComparer.Ordinal).ToList();
            var shown = string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");
            var message = group.Key.Message.TrimEnd('.') +
                $": {uses.Count} uses in {elements.Count} {(elements.Count == 1 ? "element" : "elements")} ({shown}). Declare it in the tag vocabulary, or remove it everywhere.";
            result.Add(uses[0] with { Message = message });
        }

        return result;
    }

    /// <summary>
    /// Tells the checks after resolution what the rule files register (<see cref="ResolvedChecks"/>, next-phase-2-design.md section 5.5):
    /// whether a rule has scope "resolved", so they never build a rule pool to find none, and with one, this pool and its libraries for
    /// the snapshot, so they run on it rather than load the files again. A pool wired to a token that can be cancelled is not kept (a later
    /// cancellation would stop its engines), and nothing is said while a rule file does not load (validation then has an error).
    /// </summary>
    /// <returns>Whether the pool and its libraries are kept (and so not disposed here).</returns>
    private bool PublishRules(ModelSnapshot model, IScriptSandboxPool? pool, Dictionary<string, RuleLibrary> libraries, CancellationToken ct)
    {
        if (pool is null)
            return false;
        var registers = pool.Registrations.Any(r => r is { Kind: ScriptRegistrationKind.Rule, Resolved: true, Kinds: not null });
        var keep = registers && !ct.CanBeCanceled;
        _calculations.PublishRules(model, registers, keep ? new ResolvedRulePool(pool, libraries) : null);
        return keep;
    }

    internal static string Bare(string ruleName) => ruleName.StartsWith("x/", StringComparison.Ordinal) ? ruleName[2..] : ruleName;

    private static Diagnostic PoolFailure(ModelSnapshot model, string rule, string message, string? path) =>
        RuleCatalog.Create(rule, message, null, path ?? (model.RuleScripts.Count == 1 ? model.RuleScripts[0].Path : null));

    /// <summary>A rule script that failed to load, on its own file with the sandbox's line and column.</summary>
    private static Diagnostic LoadFailure(ModelSnapshot model, string rule, Diagnostic located, string? path) =>
        RuleCatalog.Create(rule, located.Message, null, path ?? located.FilePath ?? (model.RuleScripts.Count == 1 ? model.RuleScripts[0].Path : null))
            with { Line = located.Line, Column = located.Column };

    /// <summary>The documents validation looks at: the first document of each id, and the tag vocabulary and category tree in use.</summary>
    internal static List<ElementDocument> ActiveDocuments(ModelSnapshot model)
    {
        var list = new List<ElementDocument>(model.Documents.Count);
        foreach (var doc in model.Documents)
        {
            if (!ReferenceEquals(model.GetDocument(doc.Element.Id), doc))
                continue; // a duplicate id (MQ1004)
            if (doc.Element is TagVocabulary tags && model.TagVocabularyOf(tags.Package)?.Id != doc.Element.Id)
                continue; // a second vocabulary in its scope (MQ1009)
            if (doc.Element is CategoryTree tree && model.CategoryTreeOf(tree.Package)?.Id != doc.Element.Id)
                continue;
            if (doc.Element is VerbVocabulary && model.Verbs?.Id != doc.Element.Id)
                continue; // a second verb vocabulary (MQ1009)
            list.Add(doc);
        }

        return list;
    }

    /// <summary>
    /// The documents in scope: every active document for the whole model; else the owners of the listed ids, their conflict peers
    /// (see <see cref="ConflictPeers"/>) and, with <see cref="ValidationScope.IncludeReferrers"/>, every file that references them
    /// or any of their sub-elements.
    /// </summary>
    private static List<ElementDocument> SelectTargets(ValidationContext context, List<ElementDocument> active, ValidationScope scope)
    {
        if (scope.ElementIds is null)
            return active;
        var model = context.Model;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var owners = new List<ElementDocument>();
        foreach (var id in scope.ElementIds)
        {
            if (model.GetDocument(id) is { } doc && selected.Add(doc.Element.Id))
                owners.Add(doc);
        }

        foreach (var owner in owners)
        {
            if (scope.IncludeReferrers)
            {
                foreach (var id in SubElementIds(owner.Element, model).Prepend(owner.Element.Id))
                {
                    foreach (var reference in model.ReferencesTo(id))
                        selected.Add(reference.FromElementId);
                }
            }

            foreach (var peer in ConflictPeers(context, owner))
                selected.Add(peer.Element.Id);
        }

        return active.Where(d => selected.Contains(d.Element.Id)).ToList();
    }

    /// <summary>
    /// The ids of the documents a validation of <paramref name="scope"/> looks at (<see cref="SelectTargets"/>: the listed elements'
    /// owners, their conflict peers and, with <see cref="ValidationScope.IncludeReferrers"/>, their referrers), worked out on
    /// <paramref name="model"/>. A dry run compares two models over the same documents with it.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="scope">What would be validated; it must list elements.</param>
    /// <returns>The document ids, in path order.</returns>
    internal static IReadOnlyList<string> TargetIds(ModelSnapshot model, ValidationScope scope)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ElementIds is not { Count: > 0 })
            return [];
        var active = ActiveDocuments(model);
        return [.. SelectTargets(new ValidationContext(model, active, SelectionWalker, null), active, scope).Select(d => d.Element.Id)];
    }

    /// <summary>Every vocabulary, and every document whose vocabulary chain contains <paramref name="scope"/>.</summary>
    /// <param name="model">The model.</param>
    /// <param name="scope">A package id, or <see langword="null"/> for the global scope (every document).</param>
    /// <returns>The documents.</returns>
    private static IEnumerable<ElementDocument> VocabularyDependents(ModelSnapshot model, string? scope)
    {
        foreach (var doc in model.Documents)
        {
            if (doc.Element is TagVocabulary or CategoryTree || scope is null
                || model.VocabularyChain(BuiltinRules.VocabularyScope(doc.Element)).Contains(scope))
                yield return doc;
        }
    }

    /// <summary>
    /// The files whose row checks read the rows or keys of a table that a seed, a reference type's binding or the table itself shapes:
    /// the table's own seeds and the types bound to it with their seeds (MQ7111, MQ7112, MQ7119), and the seeds of the tables whose
    /// foreign keys point to it, with the types bound to those tables and their seeds (MQ7111). A reference type's own seeds follow
    /// its bindings. The tables a change named before it are added by the store (a binding removed or retargeted, a seed deleted).
    /// </summary>
    private static IEnumerable<ElementDocument> RowPeers(ValidationContext context, Element element)
    {
        var model = context.Model;
        IEnumerable<string> tables = element switch
        {
            Table table => [table.Id],
            Seed seed when model.GetDocument(seed.Target)?.Element is Table => [seed.Target],
            Seed seed when model.GetDocument(seed.Target)?.Element is ReferenceType type => type.Bindings.Select(b => b.Table),
            ReferenceType type => type.Bindings.Select(b => b.Table),
            _ => [],
        };
        var ids = new List<string>();
        if (element is ReferenceType { Bindings.Count: > 0 } bound)
            ids.AddRange(context.SeedsOf(bound.Id).Select(s => s.Id));
        foreach (var tableId in tables.Distinct(StringComparer.Ordinal))
        {
            Holders(tableId);
            foreach (var referencing in model.All<Table>().Where(t => t.ForeignKeys.Any(fk => string.Equals(fk.ReferencesTable, tableId, StringComparison.Ordinal))))
                Holders(referencing.Id);
        }

        foreach (var id in ids)
        {
            if (model.GetDocument(id) is { } peer)
                yield return peer;
        }

        void Holders(string tableId)
        {
            ids.AddRange(context.SeedsOf(tableId).Select(s => s.Id));
            foreach (var (type, _) in context.BoundTypesOf(tableId))
            {
                ids.Add(type.Id);
                ids.AddRange(context.SeedsOf(type.Id).Select(s => s.Id));
            }
        }
    }

    /// <summary>The database of a routine, database type or SQL object; <see langword="null"/> for any other element.</summary>
    private static string? DatabaseOfObject(Element element) => element switch
    {
        Routine r => r.Database,
        DatabaseType t => t.Database,
        SqlObject o => o.Database,
        _ => null,
    };

    private static bool HasDomainVocabularies(ModelSnapshot model) =>
        model.TagVocabularies.Any(v => v.Package is not null) || model.CategoryTrees.Any(t => t.Package is not null);

    /// <summary>
    /// The files whose diagnostics can change with a document although they do not reference it: cross-file conflicts are reported
    /// on every file after the ordinally first, so the other participants must be validated with it. They are the files sharing a
    /// scoped name (MQ3001) or a physical name (MQ4002), the other mappings of the same target and database and the other overlays of
    /// the same synthesized table (MQ4004), for a table the first holder of each user-defined native type it uses and for a reference
    /// type or enum the tables whose native types carry its name (MQ4006, MQ4016), the other compositions of the same child (MQ3016),
    /// the relations whose navigations land in the same inheritance hierarchy (MQ3009), an entity's descendants (MQ3007), and for a
    /// mapping the relations and relation mappings whose foreign key binding depends on it (MQ4009, MQ4011); for a tag vocabulary or category tree every vocabulary and
    /// every element whose chain sees its scope (MQ2005, MQ2006, MQ2008, MQ3021), and for a package, when a domain vocabulary exists,
    /// every element under it (a move changes their chains); for a migration fact the facts of its database and the facts whose
    /// <c>after</c> links name it, in any database (MQ4104, MQ4105, MQ4107, MQ4111). Peers come from the document in scope: a deleted
    /// fact has none, so the store adds the facts whose links named it (their MQ4111); the facts of a moved fact's former database
    /// are not revisited, since leaving a database only removes findings there. For a stereotype, the elements that carry a
    /// stereotype whose <c>implies</c> names it (MQ2004, MQ2013 to MQ2015, MQ2018 on its effective carriers). For a package, when
    /// some package is checked, every element whose file states a package of its subtree and every referrer of those elements
    /// (MQ2020: its <c>dependsOn</c> and <c>parent</c> decide what its subtree may reference and what reaches it). The other packages
    /// of a <c>dependsOn</c> cycle (MQ2021; the saved package reports its own edge) and the seeds a rename or move reorders (it only
    /// moves MQ7112, MQ7121, MQ7001 and MQ7102 between seeds that already disagree) are not revisited (next-phase-1-design.md
    /// section 4.3). Default stereotypes (next-phase-2-design.md section 3.1, <see cref="DefaultPeers"/>): a package, while the model
    /// declares any default, brings the files whose holder's package chain holds it (its members, its descendants' members and the
    /// members that fall back to its package); a database whose schema entries declare defaults brings the files those schemas reach;
    /// a stereotype brings the files that carry it, or a stereotype implying it, through a schema or package default. Typed columns
    /// (next-phase-2-design.md section 3.2, <see cref="TypedColumnPeers"/>): an enum, a reference type or a seed of a reference type brings
    /// the tables whose columns the type types, the table seeds of those tables and the entities and reference types whose bindings give
    /// those tables' columns constants (MQ7014, MQ4083 and MQ4084 read the type's values, codes and bindings, which no reference reaches).
    /// Relations with an end of several types (several-types-design.md decision 12, <see cref="SeveralTypesPeers"/>): an entity, package or
    /// stereotype brings those whose ends name a stereotype (an entity also those with an <c>anyEntity</c> end or an end covering it), with
    /// their children's other compositions and the scenarios linking through them; a mapping or database brings all of them (MQ4115).
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="document">The document in scope.</param>
    /// <returns>The peers (the document itself may be among them; duplicates are possible).</returns>
    internal static IEnumerable<ElementDocument> ConflictPeers(ValidationContext context, ElementDocument document)
    {
        var model = context.Model;
        var element = document.Element;
        if (element is Seed seed)
        {
            // Codes and keys are unique across a target's seeds (MQ7001, MQ7002, MQ7102); a relation seed makes entity seeds that
            // state the same links invalid (MQ7106).
            foreach (var peer in context.SeedsOf(seed.Target))
            {
                if (model.GetDocument(peer.Id) is { } peerDocument)
                    yield return peerDocument;
            }

            // A former code is no live code of its type (MQ7017), and an open value map maps away no live code (MQ4113): a row's code is
            // judged on the type and on its value maps (next-phase-2-design.md section 3.5).
            if (model.Get<ReferenceType>(seed.Target) is { } codeType)
            {
                if (codeType.FormerCodes.Count > 0 && model.GetDocument(codeType.Id) is { } typeDocument)
                    yield return typeDocument;
                foreach (var map in model.All<Migration>())
                {
                    if (map.Fact == MigrationFact.ValueMap && string.Equals(map.ReferenceType, codeType.Id, StringComparison.Ordinal)
                        && model.GetDocument(map.Id) is { } mapDocument)
                        yield return mapDocument;
                }
            }

            if (model.Get<Relation>(seed.Target) is { } seededRelation)
            {
                foreach (var end in seededRelation.Ends)
                {
                    foreach (var reference in model.ReferencesTo(end.Id))
                    {
                        if (reference.Field == "columns" && model.GetDocument(reference.FromElementId) is { } holder)
                            yield return holder;
                    }
                }
            }
        }

        foreach (var peer in RowPeers(context, element))
            yield return peer;

        foreach (var peer in TypedColumnPeers(context, element))
            yield return peer;

        foreach (var (key, _) in ValidationContext.NameKeys(element))
        {
            foreach (var peer in context.WithName(key))
                yield return peer;
        }

        if (element is Table or View or Sequence && context.PhysicalNameKey(element) is { } physicalKey)
        {
            foreach (var peer in context.WithPhysicalName(physicalKey))
                yield return peer;
        }

        // MQ8104 across lifecycles is reported on the lifecycle that sorts later, so a lifecycle is judged with every other one that holds a replace.
        if (element is Process { Use: ProcessUse.Lifecycle } lifecycle)
        {
            foreach (var other in model.All<Process>())
            {
                if (!ReferenceEquals(other, lifecycle) && other.Use == ProcessUse.Lifecycle && other.TriggerPoints.Any(t => t.Type == TriggerPointType.Replace)
                    && model.GetDocument(other.Id) is { } peer)
                    yield return peer;
            }
        }

        // MQ8107 on a named trigger point on reject, ignore or withdraw depends on whether a lifecycle that governs what it covers has an
        // approval (signoff-design.md decision 17), inline or through a library's gate policy, and on delegate whether one of its named
        // signatures has delegateTo (decision 20); neither names the other.
        if (element is Process { Use: ProcessUse.Lifecycle or ProcessUse.Library })
        {
            foreach (var point in model.All<TriggerPoint>())
            {
                if (point.Verbs.Any(v => v is "reject" or "ignore" or "withdraw" or "delegate") && model.GetDocument(point.Id) is { } peer)
                    yield return peer;
            }
        }

        // MQ4014 on a mapping depends on its database's schemas.
        if (element is Database schemaOwner)
        {
            foreach (var mapping in model.All<Mapping>())
            {
                if (mapping.Schema is not null && string.Equals(mapping.Database, schemaOwner.Id, StringComparison.Ordinal) && model.GetDocument(mapping.Id) is { } peer)
                    yield return peer;
            }
        }

        // MQ4012 on an entity depends on every database's convention, on the entity's mappings and on the package chain.
        if (element is Database || (element is Package && model.All<Database>().Any(d => d.ByConvention is null ? d.Packages.Count > 0 : d.ByConvention == ConventionMapping.Packages)))
        {
            foreach (var entity in model.All<Entity>())
            {
                if (model.GetDocument(entity.Id) is { } peer)
                    yield return peer;
            }
        }
        else if (element is Mapping { Entity: { } mappedEntity } && model.GetDocument(mappedEntity) is { Element: Entity } mappedDocument)
        {
            yield return mappedDocument;
        }

        // MQ4016 is reported on the first column of a user-defined native type with a count of every use; a reference type or
        // enum name turns matching native types into known ones (MQ4006, MQ4016).
        foreach (var peer in context.NativeTypePeers(element))
            yield return peer;

        // MQ4020 on routines, database types and SQL objects depends on the others of the database (a cycle); a database type's name
        // decides MQ4006 and MQ4019 on the columns whose native type writes it.
        if (DatabaseOfObject(element) is { } objectDatabase)
        {
            foreach (var doc in context.Documents)
            {
                if (DatabaseOfObject(doc.Element) == objectDatabase)
                    yield return doc;
                else if (element is DatabaseType type && doc.Element is Table table
                    && table.Columns.Any(c => c.NativeType is { } n && (n == type.Id || n == type.Name || n.EndsWith("." + type.Name, StringComparison.Ordinal))))
                    yield return doc;
            }
        }

        foreach (var peer in ProcessPeers(model, element))
            yield return peer;

        foreach (var id in OperationLinkPeers(model, element))
        {
            if (model.GetDocument(id) is { } peer)
                yield return peer;
        }

        foreach (var peer in SeveralTypesPeers(context, element))
            yield return peer;

        foreach (var id in RevisionPeers(model, element))
        {
            if (model.GetDocument(id) is { } peer)
                yield return peer;
        }

        // The files a default list reaches are judged on the effective set it gives (MQ2004, MQ2013 to MQ2015, MQ2018 and every rule
        // that reads stereotypes), and they are not the referrers of the package, database or stereotype (next-phase-2-design.md
        // section 3.1).
        foreach (var peer in DefaultPeers(context, element))
            yield return peer;

        // MQ2004, MQ2013 to MQ2015 and MQ2018 on the carriers of a stereotype that implies this one depend on it (its applicability,
        // group, conflicts, requirements and parameters), and they are not its referrers (next-phase-1-design.md section 4.2).
        if (element is Stereotype)
        {
            foreach (var implying in model.ReferencesTo(element.Id))
            {
                if (implying.Field != "implies" || model.GetDocument(implying.FromElementId)?.Element is not Stereotype)
                    continue;
                foreach (var carrier in model.ReferencesTo(implying.FromElementId))
                {
                    if (carrier.Field == "stereotypes" && model.GetDocument(carrier.FromElementId) is { } peer)
                        yield return peer;
                }
            }
        }

        // MQ4104, MQ4105, MQ4107 and MQ4111 on a migration fact depend on the other facts of its database and on the facts that
        // name it in their after links (which are not references); the store adds the facts that named a deleted fact.
        if (element is Migration fact)
        {
            foreach (var other in model.All<Migration>())
            {
                if ((string.Equals(other.Database, fact.Database, StringComparison.Ordinal) || other.After.Contains(fact.Id, StringComparer.Ordinal))
                    && model.GetDocument(other.Id) is { } peer)
                    yield return peer;
            }
        }

        switch (element)
        {
            case VerbVocabulary:
                // The verb vocabulary decides MQ9701 and MQ9704 at every process and operation that names a verb, and MQ1009 on another one.
                foreach (var peer in model.Documents)
                {
                    if (peer.Element is Process or Operation or VerbVocabulary)
                        yield return peer;
                }

                break;
            case TagVocabulary or CategoryTree:
                // A vocabulary decides MQ2005, MQ2006 and MQ2008 on every element whose chain sees its scope, and MQ3021 and MQ1009
                // on the other vocabularies; tags are not references, so none of them is a referrer.
                foreach (var peer in VocabularyDependents(model, ModelIndexer.PackageOf(element)))
                    yield return peer;
                break;
            case Package:
                // Moving a domain changes the chain of every element under it (MQ2006, MQ2008) and the enclosing domains of its
                // vocabularies (MQ3021).
                if (HasDomainVocabularies(model))
                {
                    foreach (var peer in VocabularyDependents(model, element.Id))
                        yield return peer;
                }

                foreach (var peer in LayerPeers(context, element.Id))
                    yield return peer;
                break;
            case Table table when ValidationContext.OverlayTarget(table) is { } overlayTarget:
                foreach (var peer in context.OverlaysOf(overlayTarget))
                    yield return peer;
                break;
            case Mapping mapping:
                if ((mapping.Entity ?? mapping.Relation) is { } target)
                {
                    foreach (var peer in context.MappingsOf(mapping.Database, target))
                        yield return peer;
                }

                if (mapping.Relation is { } relationId && model.GetDocument(relationId) is { Element: Relation } relationDocument)
                    yield return relationDocument; // MQ4011 on the relation file depends on whether a mapping exists
                if (mapping.Entity is { } entityId)
                {
                    foreach (var reference in model.ReferencesTo(entityId))
                    {
                        if (model.GetDocument(reference.FromElementId) is not { Element: Relation relation } referrer)
                            continue;
                        yield return referrer;
                        foreach (var peer in context.MappingsOf(mapping.Database, relation.Id))
                            yield return peer;
                    }
                }

                break;
            case Relation relation:
                // A child end with several types reaches the compositions of every entity it covers (several-types-design.md decision 9).
                foreach (var peer in CompositionPeers(context, relation))
                    yield return peer;
                // An end with several types allows discriminatorValue on the mappings and bindings of the entities it covers (decision 42).
                foreach (var peer in TypeCodePeers(context, relation))
                    yield return peer;

                for (var i = 0; i < relation.Ends.Count; i++)
                {
                    if (relation.Ends[i].Navigation.Length == 0)
                        continue;
                    foreach (var holderId in ValidationContext.NavigationHolders(model, relation, i))
                    {
                        if (model.Get<Entity>(holderId) is { } holder)
                        {
                            foreach (var peer in HierarchyNavigationDocuments(context, holder))
                                yield return peer;
                        }
                    }
                }

                break;
            case Entity entity:
                foreach (var peer in HierarchyNavigationDocuments(context, entity))
                    yield return peer;
                foreach (var descendant in context.Descendants(entity.Id))
                {
                    if (model.GetDocument(descendant.Id) is { } peer)
                        yield return peer;
                }

                break;
        }
    }

    /// <summary>
    /// The files whose MQ4009 on <c>discriminatorValue</c> an end with several types of the relation decides (several-types-design.md
    /// section 19.7): see <see cref="TypeCodeFiles"/>. Nothing for a relation whose ends each have one type.
    /// </summary>
    private static IEnumerable<ElementDocument> TypeCodePeers(ValidationContext context, Relation relation)
    {
        var model = context.Model;
        foreach (var id in TypeCodeFiles(model, r => string.Equals(r.Id, relation.Id, StringComparison.Ordinal)))
        {
            if (model.GetDocument(id) is { } peer)
                yield return peer;
        }
    }

    /// <summary>
    /// The files whose MQ4009 on <c>discriminatorValue</c> depends on what the ends with several types of the relations
    /// <paramref name="relevant"/> picks cover (several-types-design.md decision 42 and section 19.7): for each entity such an end covers, its
    /// file when a binding sets <c>discriminatorValue</c>, and its mappings that set one. Worked out on one model; the store asks it of the
    /// model before a change too, since an entity that loses coverage has its finding back. Nothing while no end has several types or no
    /// file sets <c>discriminatorValue</c>.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="relevant">Which relations with an end of several types count.</param>
    /// <returns>The element ids (an entity's or a mapping's); duplicates are possible.</returns>
    internal static IEnumerable<string> TypeCodeFiles(ModelSnapshot model, Func<Relation, bool> relevant)
    {
        if (!EndCoverage.Of(model).Any)
            yield break;
        var setters = new List<(string Entity, string File)>();
        foreach (var mapping in model.All<Mapping>())
        {
            if (mapping is { Entity: { } entityId, DiscriminatorValue: not null })
                setters.Add((entityId, mapping.Id));
        }

        foreach (var entity in model.All<Entity>())
        {
            if (entity.Bindings.Any(b => b.DiscriminatorValue is not null))
                setters.Add((entity.Id, entity.Id));
        }

        if (setters.Count == 0)
            yield break;
        foreach (var relation in model.All<Relation>())
        {
            if (!EndCoverage.HasSeveralTypes(relation) || !relevant(relation))
                continue;
            foreach (var end in relation.Ends)
            {
                if (!end.HasSeveralTypes)
                    continue;
                foreach (var (entityId, file) in setters)
                {
                    if (EndCoverage.Covers(model, end, entityId))
                        yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Whether a change of <paramref name="element"/> may move what an end with several types of <paramref name="relation"/> covers without
    /// a save of the relation: a stereotype entry's carriers (an entity, package or stereotype save), any entity (an entity save), or an end
    /// that covers the entity saved. The relations <see cref="SeveralTypesPeers"/> brings for carriers, shared with the store's former peers.
    /// </summary>
    internal static bool MovesCoverage(ModelSnapshot model, Element element, Relation relation) =>
        element switch
        {
            Entity => relation.Ends.Any(e => EndCoverage.NamesStereotypeOrAny(e) || (e.HasSeveralTypes && EndCoverage.Covers(model, e, element.Id))),
            Package or Stereotype => relation.Ends.Any(e => e.Types?.Any(t => t.Stereotype is not null) == true),
            _ => false,
        };

    /// <summary>
    /// The other compositions of a composition's children (MQ3016): of its one-type child end's entity, or of each entity a child end with
    /// several types covers (several-types-design.md decision 9). Nothing for any other relation.
    /// </summary>
    private static IEnumerable<ElementDocument> CompositionPeers(ValidationContext context, Relation relation)
    {
        if (ValidationContext.CompositionEnds(relation) is not { } ends)
            yield break;
        var childEnd = relation.Ends[ends.Child];
        IEnumerable<string> children = childEnd.HasSeveralTypes
            ? childEnd.AnyEntity == true ? [] : EndCoverage.CoveredBy(context.Model, childEnd)
            : childEnd.SingleEntity is { } single ? [single] : [];
        foreach (var child in children)
        {
            foreach (var peer in context.CompositionsOwning(child))
                yield return peer;
        }
    }

    /// <summary>
    /// The relations with an end of several types whose findings a document can move without referencing them (several-types-design.md
    /// decision 12 and section 6), and only while such an end exists: an entity, package or stereotype change may move a stereotype's carriers,
    /// so it brings the relations whose ends name a stereotype (MQ3009, MQ3016, MQ3043, MQ4115), the other compositions of their children
    /// and the scenarios that link through them (MQ9305); an entity change also brings the relations with an <c>anyEntity</c> end (MQ3009);
    /// a mapping or a database, or a package while a database places entities by package, changes where the types are placed, so it brings
    /// every relation with an end of several types (MQ4115). A settings save validates the whole model.
    /// </summary>
    private static IEnumerable<ElementDocument> SeveralTypesPeers(ValidationContext context, Element element)
    {
        var model = context.Model;
        if (element is not (Entity or Package or Stereotype or Mapping or Database) || !EndCoverage.Of(model).Any)
            yield break;
        var placement = element is Mapping or Database
            || (element is Package && model.All<Database>().Any(d => d.ByConvention is null ? d.Packages.Count > 0 : d.ByConvention == ConventionMapping.Packages));
        var carriers = element is Entity or Package or Stereotype;
        var linked = new List<Relation>();
        foreach (var relation in model.All<Relation>())
        {
            if (!EndCoverage.HasSeveralTypes(relation))
                continue;
            // An entity also brings the relations whose end with several types covers it: a derived entity of a listed one is no referrer.
            var names = carriers && MovesCoverage(model, element, relation);
            if (!names && !placement)
                continue;
            if (model.GetDocument(relation.Id) is { } peer && !ReferenceEquals(peer.Element, element))
                yield return peer;
            if (!names)
                continue;
            foreach (var composition in CompositionPeers(context, relation))
                yield return composition;
            // Carriers that moved move the relaxed MQ4009 on discriminatorValue too (decision 42, section 19.7); the store adds the files
            // of entities that were covered before the change.
            foreach (var typeCode in TypeCodePeers(context, relation))
                yield return typeCode;
            if (relation.Ends.Any(e => e.Types?.Any(t => t.Stereotype is not null) == true))
                linked.Add(relation);
        }

        if (linked.Count == 0)
            yield break;
        foreach (var scenario in model.All<Scenario>())
        {
            if (linked.Exists(r => ObjectsReach(model, scenario, r)) && model.GetDocument(scenario.Id) is { } peer)
                yield return peer;
        }
    }

    /// <summary>
    /// The files whose revision findings a change can move without referencing it (revisions-design.md section 7, "Incremental validation"),
    /// and only while the model has a <c>revisions</c> block or a <c>nextRevision</c>. An entity brings the entities of its family with a
    /// merged block (its bases, whose entries may name what only a derived entity holds, reaches or is governed by, and its derived entities,
    /// whose merged blocks inherit from it), and the processes that govern it or a derived entity and name a <c>nextRevision</c> (MQ8306) or
    /// a set (MQ9802), with the named trigger points with sets that cover them; the scenarios with an object of it or of a derived entity
    /// (MQ9305) are already <see cref="ProcessPeers"/>'. A process brings the entities with a merged block it governs (MQ8305 and MQ8312 read
    /// which lifecycles govern them and their bound state attributes). A relation brings the entities with a merged block that its ends cover
    /// (MQ8308, MQ8312). A scalar type brings the entities whose merged bound attribute it types (MQ8305). The store adds the same peers worked
    /// out on the model before the change, so an entity a process stopped governing, or one whose block went, is checked again too.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="element">The changed element.</param>
    /// <returns>The ids of the peers (duplicates possible).</returns>
    internal static IEnumerable<string> RevisionPeers(ModelSnapshot model, Element element)
    {
        if (element is not (Entity or Process or Relation or ScalarType))
            yield break;
        var coverage = Revisions.RevisionCoverage.Of(model);
        if (!coverage.Any && !RevisionRules.AnyNextRevision(model))
            yield break;
        switch (element)
        {
            case Entity entity:
            {
                var derived = RevisionRules.DerivedFrom(model, entity);
                var bases = Processes.SubjectCoverage.Chain(model, entity).Skip(1).Select(id => model.Get<Entity>(id)).OfType<Entity>();
                foreach (var member in derived.Concat(bases))
                {
                    if (coverage.For(member) is not null)
                        yield return member.Id;
                }

                var reached = derived.Prepend(entity).ToList();
                foreach (var process in model.All<Process>())
                {
                    if ((process.Transitions.Any(t => t.NextRevision is not null) || process.Actions.Any(a => a.Sets.Count > 0) || process.TriggerPoints.Any(t => t.Sets.Count > 0))
                        && reached.Exists(e => Processes.SubjectCoverage.Governs(model, process, e)))
                        yield return process.Id;
                }

                if (coverage.Any && Processes.NamedTriggerPoints.Of(model) is { Any: true } named)
                {
                    foreach (var point in named.All)
                    {
                        if (point.Sets.Count > 0 && named.Covers(point).Any(c => reached.Exists(e => string.Equals(e.Id, c.Id, StringComparison.Ordinal))))
                            yield return point.Id;
                    }
                }

                break;
            }

            case Process process when coverage.Any:
                foreach (var entity in model.All<Entity>())
                {
                    if (coverage.For(entity) is not null && Processes.SubjectCoverage.Governs(model, process, entity))
                        yield return entity.Id;
                }

                break;
            case Relation relation when coverage.Any:
                foreach (var entity in model.All<Entity>())
                {
                    if (coverage.For(entity) is not null && relation.Ends.Any(e => EndCoverage.Covers(model, e, entity.Id)))
                        yield return entity.Id;
                }

                break;
            case ScalarType scalar when coverage.Any:
                foreach (var entity in model.All<Entity>())
                {
                    if (coverage.For(entity) is { BoundAttribute: { } bound } && model.TryGetEntry(bound, out var entry)
                        && model.GetDocument(entry.OwnerId)?.Element is { } owner
                        && ReferenceDataIndex.AttributesOf(owner).Any(a => string.Equals(a.Id, bound, StringComparison.Ordinal) && string.Equals(a.Type.Ref, scalar.Id, StringComparison.Ordinal)))
                        yield return entity.Id;
                }

                break;
        }
    }

    /// <summary>
    /// For an enum, a reference type or a seed of a reference type: the table files with a column whose <c>typeRef</c> names the type (own,
    /// or through a stereotype the table lists or a stereotype it lists implies), their table seeds, and the entities and reference types bound to them, whose constants
    /// fill their columns (next-phase-2-design.md section 3.2). Nothing for any other document.
    /// </summary>
    private static IEnumerable<ElementDocument> TypedColumnPeers(ValidationContext context, Element element)
    {
        var model = context.Model;
        var typeId = element switch
        {
            EnumType or ReferenceType => element.Id,
            Seed seed when model.Get<ReferenceType>(seed.Target) is not null => seed.Target,
            _ => null,
        };
        if (typeId is null)
            yield break;
        var tables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in model.ReferencesTo(typeId))
        {
            switch (model.GetDocument(reference.FromElementId)?.Element)
            {
                case Table table when table.Columns.Any(c => string.Equals(c.TypeRef?.Ref, typeId, StringComparison.Ordinal)):
                    tables.Add(table.Id);
                    break;
                case Stereotype stereotype when stereotype.Columns.Any(c => string.Equals(c.TypeRef?.Ref, typeId, StringComparison.Ordinal)):
                {
                    // A table carries the stereotype by listing it or a stereotype that implies it (as DefaultPeers follows implies).
                    var carried = new List<string> { stereotype.Id };
                    foreach (var implying in model.ReferencesTo(stereotype.Id))
                    {
                        if (implying.Field == "implies" && model.GetDocument(implying.FromElementId)?.Element is Stereotype)
                            carried.Add(implying.FromElementId);
                    }

                    foreach (var key in carried)
                    {
                        foreach (var carrier in model.ReferencesTo(key))
                        {
                            if (carrier.Field == "stereotypes" && model.Get<Table>(carrier.FromElementId) is not null)
                                tables.Add(carrier.FromElementId);
                        }
                    }

                    break;
                }
            }
        }

        if (tables.Count == 0)
            yield break;
        foreach (var tableId in tables)
        {
            if (model.GetDocument(tableId) is { } tableDocument)
                yield return tableDocument;
            foreach (var seed in context.SeedsOf(tableId))
            {
                if (model.GetDocument(seed.Id) is { } seedDocument)
                    yield return seedDocument;
            }

            foreach (var (type, _) in context.BoundTypesOf(tableId))
            {
                if (model.GetDocument(type.Id) is { } typeDocument)
                    yield return typeDocument;
            }
        }

        foreach (var entity in model.All<Entity>())
        {
            if (entity.Bindings.Any(b => tables.Contains(b.Source) || (b.Write?.Table is { } written && tables.Contains(written)))
                && model.GetDocument(entity.Id) is { } entityDocument)
                yield return entityDocument;
        }
    }

    /// <summary>
    /// The files whose effective stereotype set a document's default lists decide (next-phase-2-design.md section 3.1). The validator
    /// sees only the candidate model, so a package brings its reach while the model declares any default (a save that removes the
    /// model's last default list leaves its descendants' and fallback members to the next validate; its own members are its
    /// referrers); a database brings the reach of its schema entries that declare defaults (its physical files are its referrers
    /// too); a stereotype brings the reach of every schema entry or package whose default list names it or a stereotype implying it.
    /// The reach of schema entries and packages is every document whose holder (<see cref="ModelSnapshot.HolderOf"/>) is in one of
    /// the schemas or has one of the packages on its package chain, and, for schemas, the entities and relations whose tables without a
    /// file would be in one (findings about such a table go on its entity or relation).
    /// </summary>
    private static IEnumerable<ElementDocument> DefaultPeers(ValidationContext context, Element element)
    {
        var model = context.Model;
        var schemas = new HashSet<string>(StringComparer.Ordinal);
        var packages = new HashSet<string>(StringComparer.Ordinal);
        switch (element)
        {
            case Package package when model.StereotypeDefaults.HasDefaults || package.DefaultStereotypes.Count > 0:
                packages.Add(package.Id);
                break;
            case Database database:
                foreach (var schema in database.Schemas)
                {
                    if (schema.DefaultStereotypes.Count > 0)
                        schemas.Add(schema.Id);
                }

                break;
            case Stereotype stereotype when model.StereotypeDefaults.HasDefaults:
                var keys = new List<string> { stereotype.Id };
                foreach (var reference in model.ReferencesTo(stereotype.Id))
                {
                    if (reference.Field == "implies" && model.GetDocument(reference.FromElementId)?.Element is Stereotype)
                        keys.Add(reference.FromElementId);
                }

                foreach (var key in keys)
                {
                    foreach (var reference in model.ReferencesTo(key))
                    {
                        if (reference.Field != "defaultStereotypes")
                            continue;
                        switch (model.GetDocument(reference.FromElementId)?.Element)
                        {
                            case Database:
                                schemas.Add(reference.FromId);
                                break;
                            case Package:
                                packages.Add(reference.FromElementId);
                                break;
                        }
                    }
                }

                break;
        }

        if (schemas.Count == 0 && packages.Count == 0)
            yield break;
        var routed = schemas.Count == 0 ? [] : model.All<Database>().Where(d => d.Schemas.Any(s => schemas.Contains(s.Id))).ToList();
        foreach (var doc in context.Documents)
        {
            var holder = model.HolderOf(doc.Element);
            if ((holder.Schema is { } schema && schemas.Contains(schema)) || (packages.Count > 0 && OnChain(model, holder.Package, packages)))
            {
                yield return doc;
                continue;
            }

            foreach (var database in routed)
            {
                var tableSchema = doc.Element switch
                {
                    Entity entity => model.StereotypeDefaults.SchemaOf(database.Id, null, entity.Id),
                    Relation relation => model.StereotypeDefaults.SchemaOf(database.Id, null, relation: relation.Id),
                    _ => null,
                };
                if (tableSchema is not null && schemas.Contains(tableSchema))
                {
                    yield return doc;
                    break;
                }
            }
        }

        static bool OnChain(ModelSnapshot model, string? packageId, HashSet<string> packages)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var id = packageId; id is not null && seen.Add(id); id = model.Get<Package>(id)?.Parent)
            {
                if (packages.Contains(id))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// MQ2020 depends on a package's <c>dependsOn</c> and <c>parent</c> beyond its referrers (next-phase-1-design.md section 4.3): a
    /// changed <c>dependsOn</c> changes what every element of its subtree may reference, and the members of child packages do not
    /// reference the changed package; a moved subtree changes what may reference its elements. So, only when some package is
    /// checked: every element whose file states a package of the subtree, and every referrer of those elements.
    /// </summary>
    private static IEnumerable<ElementDocument> LayerPeers(ValidationContext context, string packageId)
    {
        if (!context.Layers.AnyChecked)
            yield break;
        var model = context.Model;
        var members = new List<ElementDocument>();
        foreach (var doc in context.Documents)
        {
            if (ModelIndexer.PackageOf(doc.Element) is { } memberOf && context.Layers.ChainOf(memberOf).Contains(packageId, StringComparer.Ordinal))
                members.Add(doc);
        }

        foreach (var member in members)
        {
            yield return member;
            foreach (var id in SubElementIds(member.Element, model).Prepend(member.Element.Id))
            {
                foreach (var reference in model.ReferencesTo(id))
                {
                    if (model.GetDocument(reference.FromElementId) is { } referrer)
                        yield return referrer;
                }
            }
        }
    }

    /// <summary>The relation files that generate navigations on an entity, its ancestors or its descendants.</summary>
    private static IEnumerable<ElementDocument> HierarchyNavigationDocuments(ValidationContext context, Entity entity)
    {
        foreach (var member in context.Ancestors(entity).Prepend(entity).Concat(context.Descendants(entity.Id)))
        {
            foreach (var site in context.NavigationsOn(member.Id))
                yield return site.Document;
        }
    }

    /// <summary>
    /// Every sub-element id an element holds, a custom element's members included (next-phase-2-design.md section 4.2): those of its
    /// members the index registers as owned by it, through its custom kind's member tree, so a save that changes a member widens to its
    /// referrers.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="model">The snapshot whose index and custom kinds are read.</param>
    /// <returns>The ids.</returns>
    internal static IEnumerable<string> SubElementIds(Element element, ModelSnapshot model) =>
        element is Custom custom
            ? ModelIndexer.MembersOf(custom, model.PropertySchemas)
                .Where(m => m.Id is { } id && model.TryGetEntry(id, out var entry) && entry.OwnerId == custom.Id && entry.Kind == "member")
                .Select(m => m.Id!)
            : SubElementIds(element);

    /// <summary>Every sub-element id an element holds (attributes, members, ends, columns, keys, categories, schemas, an operation's
    /// members, outcomes and errors); a custom element's members need the snapshot (<see cref="SubElementIds(Element, ModelSnapshot)"/>).</summary>
    internal static IEnumerable<string> SubElementIds(Element element)
    {
        switch (element)
        {
            case Entity e:
                foreach (var a in e.Attributes) yield return a.Id;
                foreach (var k in e.AlternateKeys) yield return k.Id;
                break;
            case ValueObject v:
                foreach (var a in v.Attributes) yield return a.Id;
                break;
            case Relation r:
                foreach (var end in r.Ends) yield return end.Id;
                foreach (var a in r.Attributes) yield return a.Id;
                break;
            case Stereotype s:
                foreach (var a in s.Attributes) yield return a.Id;
                foreach (var e in s.Errors) yield return e.Id;
                break;
            case EnumType n:
                foreach (var m in n.Members) yield return m.Id;
                break;
            case Database d:
                foreach (var s in d.Schemas) yield return s.Id;
                break;
            case CategoryTree c:
                foreach (var category in c.Categories) yield return category.Id;
                break;
            case Process p:
                foreach (var id in ProcessNodeIds(p)) yield return id;
                break;
            case Scenario sc:
                foreach (var step in sc.Steps) yield return step.Id;
                break;
            case Operation o:
                foreach (var m in o.Input) yield return m.Id;
                foreach (var outcome in o.Outcomes)
                {
                    yield return outcome.Id;
                    foreach (var m in outcome.Returns?.Members ?? []) yield return m.Id;
                }

                foreach (var e in o.Errors) yield return e.Id;
                break;
            case Table t:
                foreach (var c in t.Columns) yield return c.Id;
                foreach (var u in t.Uniques) yield return u.Id;
                foreach (var f in t.ForeignKeys) yield return f.Id;
                foreach (var c in t.Checks) yield return c.Id;
                foreach (var x in t.Indexes) yield return x.Id;
                break;
        }
    }

    private static IEnumerable<string> ProcessNodeIds(Process process)
    {
        foreach (var a in process.Context) yield return a.Id;
        foreach (var e in process.Events)
        {
            yield return e.Id;
            foreach (var a in e.Payload) yield return a.Id;
        }

        foreach (var g in process.Guards) yield return g.Id;
        foreach (var a in process.Actions) yield return a.Id;
        var stack = new Stack<ProcessState>(process.States.Reverse());
        while (stack.Count > 0)
        {
            var state = stack.Pop();
            yield return state.Id;
            foreach (var invoke in state.Invoke) yield return invoke.Id;
            for (var i = state.States.Count - 1; i >= 0; i--)
                stack.Push(state.States[i]);
        }

        foreach (var t in process.Transitions)
            yield return t.Id;
        foreach (var gate in process.Transitions.Select(t => t.Gate).OfType<ProcessGate>().Concat(process.Gates))
        {
            yield return gate.Id;
            foreach (var m in gate.Meanings) yield return m.Id;
            foreach (var a in gate.AuditAttributes) yield return a.Id;
        }
    }

    /// <summary>
    /// The files whose links between processes and operations (processes-operations-design.md section 8) a change may alter beyond its
    /// referrers. A process change reaches the operations that fire its events (they reference them), and an operation change the processes
    /// whose calls or service steps name it, as referrers. Beyond them: while MQ9024 is on, an operation brings the processes whose events it
    /// fires (the store adds those it fired before, <see cref="ModelStore"/>'s former operation peers); nothing while it is off. An entity, or a
    /// package or stereotype that moves the carriers of a stereotype entry, changes which entities a process governs and which entities derive
    /// from an operation's subject (MQ3032): it brings the processes about it or an entity derived from it and the operations that fire their
    /// events, and, for each operation on it or an entity derived from it or with a record key member of one, the processes that call or run
    /// the operation and the operation itself when it fires; nothing while no operation fires an event and no call or service step names one.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="element">The element changed.</param>
    /// <returns>The ids of the peers, possibly repeated.</returns>
    internal static IEnumerable<string> OperationLinkPeers(ModelSnapshot model, Element element)
    {
        if (element is Operation fired && fired.Fires.Count > 0 && OperationLinks.UnfiredEventsOn(model))
        {
            foreach (var id in fired.Fires)
            {
                if (model.TryGetEntry(id, out var entry) && entry.Kind == "event")
                    yield return entry.OwnerId;
            }
        }

        if (element is not (Entity or Package or Stereotype) || !HasOperationLinks(model))
            yield break;
        HashSet<string> lineage;
        if (element is Entity changed)
        {
            lineage = model.All<Entity>().Where(e => Processes.SubjectCoverage.Chain(model, e).Contains(changed.Id, StringComparer.Ordinal)).Select(e => e.Id)
                .ToHashSet(StringComparer.Ordinal);
        }
        else if (Processes.SubjectCoverage.Of(model).Any)
        {
            lineage = [];
        }
        else
        {
            yield break;
        }

        foreach (var process in model.All<Process>())
        {
            if (process.Use == ProcessUse.Library || !process.Subjects.Any(s => (s.Entity is { } id && lineage.Contains(id)) || s.Stereotype is not null))
                continue;
            if (CallsOperations(process))
                yield return process.Id;
            foreach (var e in process.Events)
            {
                foreach (var reference in model.ReferencesTo(e.Id))
                {
                    if (model.Get<Operation>(reference.FromElementId) is not null)
                        yield return reference.FromElementId;
                }
            }
        }

        // An operation on an entity of the lineage, or with a record key member of one (a key attribute's type, a key that turns composite
        // or goes, or a base the key comes from: MQ3044, MQ3103, MQ9025): the processes that call it or run it, and itself when it fires,
        // since MQ3032 and MQ3044 on its fires read the lineage and it references only the entity it names.
        foreach (var operation in model.All<Operation>())
        {
            if (!(operation.Subject is { } subject && lineage.Contains(subject)) && !operation.Input.Any(m => m.KeyOf is { } keyOf && lineage.Contains(keyOf)))
                continue;
            if (operation.Fires.Count > 0)
                yield return operation.Id;
            foreach (var reference in model.ReferencesTo(operation.Id))
            {
                if (model.Get<Process>(reference.FromElementId) is not null)
                    yield return reference.FromElementId;
            }
        }
    }

    /// <summary>Whether some operation fires an event, or some call or service step names an operation (processes-operations-design.md section 8).</summary>
    private static bool HasOperationLinks(ModelSnapshot model) => model.All<Operation>().Any(o => o.Fires.Count > 0) || model.All<Process>().Any(CallsOperations);

    /// <summary>Whether an action of a process calls an operation, or one of its service steps names one.</summary>
    private static bool CallsOperations(Process process) => process.Actions.Any(a => a.Call is not null) || process.States.Any(HasStepOperation);

    private static bool HasStepOperation(ProcessState state) => state.Invoke.Any(i => i.Operation is not null) || state.States.Any(HasStepOperation);

    // The process rules that read other files: MQ9106 on actors depends on every process, MQ9015 on the sub-processes a process
    // reaches, MQ9202 to MQ9208 and MQ9210 on the subject's attributes, the bound enum and every other use of it, the other lifecycles that
    // bind one attribute, and the bound reference or scalar type with its seeds.
    internal static IEnumerable<ElementDocument> ProcessPeers(ModelSnapshot model, Element element)
    {
        // An actor's type and memberships decide MQ9106, MQ9602 and MQ9605 on other actors (a membership that goes leaves no reference in
        // the candidate), and, once the model has memberships, MQ9102 and MQ9105 on the processes that name actors and their scenarios'
        // replays (actors-design.md section 14).
        var membershipsMatter = element is Actor && Processes.ActorMembership.Of(model).Any;
        // A scenario names actors only through its participants, which MQ9106 counts as used (actors-design.md decision 6): a scenario
        // without them reaches no actor. One that had participants before the change is followed by the store (FormerActorPeers).
        // A named right that names an actor counts it as used too (rights-design.md section 5).
        if (element is Process or Actor or Right || (element is Scenario scenarioSaved && scenarioSaved.Participants.Count > 0))
        {
            foreach (var actor in model.All<Actor>())
            {
                if (!ReferenceEquals(actor, element) && model.GetDocument(actor.Id) is { } peer)
                    yield return peer;
            }
        }

        if (membershipsMatter)
        {
            foreach (var named in model.All<Process>())
            {
                if (NamesActor(model, named) && model.GetDocument(named.Id) is { } peer)
                    yield return peer;
            }
        }

        if (element is Actor gateActor)
        {
            // MQ9604 on a process that uses the actor only through a library's gate (gatePolicy): the process names the gate, not the
            // actor, so it is not among the actor's referrers (actors-design.md decision 11).
            foreach (var user in model.All<Process>())
            {
                if (!(membershipsMatter && NamesActor(model, user)) && UsesThroughLibraryGate(model, user, gateActor.Id) && model.GetDocument(user.Id) is { } peer)
                    yield return peer;
            }

            // MQ9604 at each use of a named right that names the actor: the lifecycle names the right, not the actor (rights-design.md decision 7).
            if (gateActor.Relative is not null)
            {
                foreach (var reference in model.ReferencesTo(gateActor.Id))
                {
                    if (model.Get<Right>(reference.FromElementId) is not { } naming)
                        continue;
                    foreach (var use in ExprSites.RightUses(model, naming))
                    {
                        if (model.GetDocument(use.Id) is { } peer)
                            yield return peer;
                    }
                }
            }
        }

        // A named right's condition, ends and actors from the object are judged at each use (rights-design.md decision 7).
        if (element is Right changedRight)
        {
            foreach (var use in ExprSites.RightUses(model, changedRight))
            {
                if (model.GetDocument(use.Id) is { } peer)
                    yield return peer;
            }
        }

        if (element is Process process)
        {

            var seen = new HashSet<string>(StringComparer.Ordinal) { process.Id };
            var queue = new Queue<Process>([process]);
            while (queue.Count > 0)
            {
                foreach (var callee in ProcessRules.Callees(queue.Dequeue()))
                {
                    if (seen.Add(callee) && model.Get<Process>(callee) is { } next && model.GetDocument(next.Id) is { } peer)
                    {
                        queue.Enqueue(next);
                        yield return peer;
                    }
                }
            }
        }

        if (element is Process or Entity or ValueObject or Relation or Stereotype or EnumType)
        {
            foreach (var bound in model.All<Process>())
            {
                if (bound.Subjects.Any(s => s.BoundAttribute is not null) && model.GetDocument(bound.Id) is { } peer) // either form
                    yield return peer;
            }
        }

        if (element is Entity or Relation or ValueObject or Stereotype)
        {
            // MQ9604 and MQ9606 read the subjects' attributes and navigations (actors-design.md section 5): every process that runs once
            // the model has an actor from the object, else only those whose subjects name an owner; none without either.
            var relatives = model.All<Actor>().Any(a => a.Relative is not null);
            foreach (var owned in model.All<Process>())
            {
                if ((relatives ? owned.Use != ProcessUse.Library : owned.Subjects.Any(s => s.Owner is not null)) && model.GetDocument(owned.Id) is { } peer)
                    yield return peer;
            }

            // A named right's condition and ends are typed at each use against the subjects (rights-design.md decision 7), and the
            // lifecycle names the right, not what the condition reads. None without a named right that has either.
            if (!relatives)
            {
                foreach (var named in model.All<Right>())
                {
                    if (named.Condition is null && named.Ends.Count == 0)
                        continue;
                    foreach (var use in ExprSites.RightUses(model, named))
                    {
                        if (model.GetDocument(use.Id) is { } peer)
                            yield return peer;
                    }
                }
            }
        }

        if (element is ReferenceType or ScalarType or Seed)
        {
            // MQ9205, MQ9207 and MQ9208 under a reference or plain binding read the type's codes, rows and facets (lifecycle-design.md
            // decision 18): only the lifecycles that bind an attribute of that type, none when nothing binds.
            var typeId = element is Seed seed ? seed.Target : element.Id;
            foreach (var id in Processes.LifecycleBindings.Of(model).ByType.GetValueOrDefault(typeId, []))
            {
                if (model.GetDocument(id) is { } peer)
                    yield return peer;
            }
        }

        // A stereotype that stands for a set of types (stereotype-sets-design.md section 5): an entity's stereotypes, base or package, a
        // package's defaults or parent, or a stereotype's implies or appliesTo may change which entities a stereotype entry covers. While an
        // entry names a stereotype, such a change re-checks the processes that have one (MQ9201, MQ9202, MQ9211, the guard scope), the
        // entities whose lifecycle names one (MQ9201) and, below, those processes' scenarios. None without a stereotype entry.
        var coverage = Processes.SubjectCoverage.Of(model);
        var coverageMatters = coverage.Any && element is Entity or Package or Stereotype;
        if (coverageMatters)
        {
            foreach (var listing in model.All<Process>())
            {
                if (!ReferenceEquals(listing, element) && Processes.SubjectCoverage.NamesStereotype(listing) && model.GetDocument(listing.Id) is { } peer)
                    yield return peer;
            }

            foreach (var governed in model.All<Entity>())
            {
                if (!ReferenceEquals(governed, element) && governed.Lifecycle is { } named && model.Get<Process>(named) is { } lifecycle
                    && Processes.SubjectCoverage.NamesStereotype(lifecycle) && model.GetDocument(governed.Id) is { } peer)
                    yield return peer;
            }
        }

        // Without a stereotype entry too: a guard, owner field or actor path that reads a stereotype's attribute, and a scenario object's
        // value for one, depend on which stereotypes an entity carries. Only the processes, actors' processes and scenarios that name an
        // attribute declared on a stereotype are picked, so it costs nothing when nothing reads one.
        var stereotypeReaders = new HashSet<string>(StringComparer.Ordinal);
        if (element is Entity or Package or Stereotype)
        {
            foreach (var declaring in model.All<Stereotype>())
            {
                foreach (var attribute in declaring.Attributes)
                {
                    foreach (var reference in model.ReferencesTo(attribute.Id))
                    {
                        switch (model.GetDocument(reference.FromElementId)?.Element)
                        {
                            case Process reader when reader.Use != ProcessUse.Library:
                                stereotypeReaders.Add(reader.Id);
                                break;
                            case Scenario scenario:
                                stereotypeReaders.Add(scenario.Id);
                                break;
                            case Actor { Relative: not null } actor:
                                foreach (var use in model.ReferencesTo(actor.Id))
                                {
                                    if (model.Get<Process>(use.FromElementId) is not null)
                                        stereotypeReaders.Add(use.FromElementId);
                                }

                                break;
                        }
                    }
                }
            }

            foreach (var id in stereotypeReaders.Order(StringComparer.Ordinal))
            {
                if (model.GetDocument(id) is { } peer && !ReferenceEquals(peer.Element, element))
                    yield return peer;
            }
        }

        // MQ93xx and MQ95xx on scenarios replay their process, the sub-processes it invokes and the types of its attributes: a process
        // change re-checks its own scenarios and those of the processes that invoke it; an enum or subject change re-checks the
        // scenarios of the processes that use it.
        var replayed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in model.All<Process>())
        {
            var affected = element switch
            {
                Process changed => string.Equals(candidate.Id, changed.Id, StringComparison.Ordinal) || ProcessRules.Callees(candidate).Contains(changed.Id),
                // While an entry names a stereotype, the change may move the carriers (stereotype-sets-design.md section 5); a process
                // whose guard reads a stereotype's attribute replays too.
                Entity or Package or Stereotype when (coverageMatters && Processes.SubjectCoverage.NamesStereotype(candidate)) || stereotypeReaders.Contains(candidate.Id) => true,
                // A sub-process's guard that reads subject sees its own subjects' defaults (lifecycle-design.md decision 22).
                Entity entity => Governs(model, candidate, entity.Id)
                    || ExprSites.InvokedProcesses(model, candidate).Any(p => Governs(model, p, entity.Id) && ExprSites.SubjectDepth(model, p) is not null),
                EnumType or ValueObject or ScalarType => UsesType(candidate, element.Id),
                // Replay accepts members of listed actors (actors-design.md decision 5).
                Actor => membershipsMatter && NamesActor(model, candidate),
                _ => false,
            };
            if (affected)
                replayed.Add(candidate.Id);
        }

        // A scenario's objects (lifecycle-design.md decision 22) read their entities and the bases that declare their attributes and
        // defaults, the types (reference types too) their values must fit, and the relations whose ends name their links' navigations.
        // A scenario's participants (actors-design.md decisions 14 to 17) read every actor's type, memberships and relative form: an actor
        // change re-checks the scenarios that have participants, and none when no scenario has them.
        var objectsReached = element is Entity or Relation or EnumType or ValueObject or ScalarType or ReferenceType;
        var participantsReached = element is Actor;
        if (replayed.Count == 0 && !objectsReached && !participantsReached)
            yield break;
        foreach (var scenario in model.All<Scenario>())
        {
            if ((replayed.Contains(scenario.Process) || (objectsReached && ObjectsReach(model, scenario, element))
                    || (participantsReached && scenario.Participants.Count > 0)) && model.GetDocument(scenario.Id) is { } peer)
                yield return peer;
        }
    }

    // Whether a process's transitions use a library's gate whose signers or required actors name the actor, or a named signature's delegateTo
    // or escalateTo.
    private static bool UsesThroughLibraryGate(ModelSnapshot model, Process process, string actorId) =>
        process.Transitions.Any(t => t.GatePolicy is { } policy && Processes.ProcessLibraries.Owner(model, policy, "gate") is { } library
            && library.Gates.Any(g => string.Equals(g.Id, policy, StringComparison.Ordinal)
                && (g.Signers.Contains(actorId, StringComparer.Ordinal) || g.RequiredActors.Contains(actorId, StringComparer.Ordinal)
                    || g.AllSignatures.Any(s => s.Signers.Contains(actorId, StringComparer.Ordinal) || s.RequiredActors.Contains(actorId, StringComparer.Ordinal)
                        || s.DelegateTo.Contains(actorId, StringComparer.Ordinal) || s.EscalateTo.Contains(actorId, StringComparer.Ordinal)))));

    // Whether a process document names an actor anywhere (the index row's actors).
    private static bool NamesActor(ModelSnapshot model, Process process) =>
        model.ReferencesFrom(process.Id).Any(r => model.Get<Actor>(r.ToId) is not null);

    // Whether a scenario's objects read the element: an entity they are or derive from, a type an attribute of those entities has, or a
    // relation one of their links goes through.
    private static bool ObjectsReach(ModelSnapshot model, Scenario scenario, Element element)
    {
        foreach (var obj in scenario.Objects)
        {
            if (element is Relation relation)
            {
                if (obj.Links.Any(l => relation.Ends.Any(e => string.Equals(e.Id, l.End, StringComparison.Ordinal))))
                    return true;
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = model.Get<Entity>(obj.Entity); current is not null && seen.Add(current.Id); current = current.Base is { } b ? model.Get<Entity>(b) : null)
            {
                if (element is Entity ? string.Equals(current.Id, element.Id, StringComparison.Ordinal)
                    : current.Attributes.Any(a => string.Equals(a.Type.Ref, element.Id, StringComparison.Ordinal)))
                    return true;
            }

            // A dangling entity id still names the entity it waits for.
            if (element is Entity && string.Equals(obj.Entity, element.Id, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // Whether a process lists the entity as a subject, in either form, or lists an entity derived from it (whose inherited attributes
    // the entity's change reaches).
    private static bool Governs(ModelSnapshot model, Process process, string entityId)
    {
        foreach (var subject in process.Subjects)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = subject.Entity; current is not null && seen.Add(current); current = model.Get<Entity>(current)?.Base)
            {
                if (string.Equals(current, entityId, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static bool UsesType(Process process, string typeId) =>
        process.Context.Concat(process.Events.SelectMany(e => e.Payload))
            .Concat(process.Transitions.Where(t => t.Gate is not null).SelectMany(t => t.Gate!.AuditAttributes))
            .Concat(process.Gates.SelectMany(g => g.AuditAttributes))
            .Any(a => string.Equals(a.Type.Ref, typeId, StringComparison.Ordinal));

    private static bool NamesRules(IEnumerable<ElementDocument> documents)
    {
        foreach (var doc in documents)
        {
            // An x/ id in suppressions is judged against the loaded rules (MQ5019), as validation rule ids are (MQ2007).
            if (doc.Element.Suppressions.Any(s => s.Rule.StartsWith("x/", StringComparison.Ordinal)))
                return true;
            IEnumerable<ModelAttribute> attributes = doc.Element switch
            {
                Entity e => e.Attributes,
                ValueObject v => v.Attributes,
                Relation r => r.Attributes,
                Stereotype s => s.Attributes,
                _ => [],
            };
            if (attributes.Any(a => a.Validation?.Rules.Count > 0) || doc.Element is ScalarType { Validation.Rules.Count: > 0 })
                return true;
        }

        return false;
    }

    /// <summary>
    /// The script rules of one validation: the pool, the element rules and their kinds, the rule files whose results may be cached, the
    /// cache.
    /// </summary>
    /// <param name="Pool">The pool.</param>
    /// <param name="Rules">The element rules (without <c>x/</c>), ordinal.</param>
    /// <param name="Kinds">Rule id → the kinds it checks, for the element rules that name kinds (<see cref="RuleKinds"/>).</param>
    /// <param name="Cached">Rule id → its rule file, for the rules whose results are cached (<see cref="CachedRuleFiles"/>).</param>
    /// <param name="Cache">The validator's result cache.</param>
    /// <param name="Ticket">The validation's cache ticket.</param>
    /// <param name="Libraries">Where the rules' <c>maquettiste.use</c> calls go (library packs).</param>
    private sealed record RuleRun(IScriptSandboxPool Pool, IReadOnlyList<string> Rules, IReadOnlyDictionary<string, IReadOnlySet<string>> Kinds,
        IReadOnlyDictionary<string, ScriptSource> Cached, RuleResultCache Cache, long Ticket, RuleLibraryCalls Libraries) : IScriptLibraryCalls
    {
        /// <inheritdoc/>
        public object? Call(string pack, string helper, IReadOnlyList<object?> args, ScriptCallContext caller) => Libraries.Call(pack, helper, args, caller);
    }

    /// <summary>A project library of one validation: its pool (none without scripts), its helper names, its parameters as plain values.</summary>
    internal sealed record RuleLibrary(IScriptSandboxPool? Pool, IReadOnlyList<string> Helpers, IReadOnlyDictionary<string, object?> Parameters);

    /// <summary>
    /// The calls of the project's rules into its library packs: each call rents an engine of the library's pool, runs the helper with
    /// the library's parameters and the caller's recorder, and returns it. A library's failure is MQ5002 (a limit MQ5003) at the
    /// library's script, naming the library.
    /// </summary>
    internal sealed class RuleLibraryCalls(IReadOnlyDictionary<string, RuleLibrary> libraries) : IScriptLibraryCalls
    {
        /// <inheritdoc/>
        public object? Call(string pack, string helper, IReadOnlyList<object?> args, ScriptCallContext caller)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var label = $"maquettiste.use('{pack}').{helper}";
            if (!libraries.TryGetValue(pack, out var library) || library.Pool is null)
            {
                throw new ScriptErrorException(new Diagnostic("MQ5002", DiagnosticSeverity.Error,
                    $"{label}: the library pack '{pack}' has no helper '{helper}'.", null, null, null, null, null));
            }

            using var lease = library.Pool.Rent();
            try
            {
                return lease.Sandbox.CallHelper(helper, args, caller with { Parameters = library.Parameters });
            }
            catch (ScriptErrorException e)
            {
                throw new ScriptErrorException(e.Diagnostic with { Rule = "MQ5002", Message = $"The library pack '{pack}' failed in {label}: {e.Diagnostic.Message}" }, e);
            }
            catch (ScriptLimitException e)
            {
                throw new ScriptLimitException(e.Diagnostic with { Rule = "MQ5003", Message = $"The library pack '{pack}' exceeded a limit in {label}: {e.Diagnostic.Message}" }, e);
            }
        }
    }

    /// <summary>
    /// One pool per project library (<see cref="ModelSnapshot.ScriptLibraries"/>), in dependency order, each reaching the libraries of
    /// its own closure. A library whose scripts fail to load is MQ5002 (a limit MQ5003) at its script and left out, and so is a library
    /// whose closure holds one.
    /// </summary>
    internal static Dictionary<string, RuleLibrary> LibraryPools(ModelSnapshot model, IScriptSandboxFactory scripts, int size, List<Diagnostic> diagnostics,
        CancellationToken ct)
    {
        var pools = new Dictionary<string, RuleLibrary>(StringComparer.Ordinal);
        var byName = model.ScriptLibraries.ToDictionary(l => l.Pack, StringComparer.Ordinal);
        foreach (var library in model.ScriptLibraries)
        {
            var reach = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(library.Uses);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (reach.Add(next) && byName.TryGetValue(next, out var used))
                {
                    foreach (var name in used.Uses)
                        stack.Push(name);
                }
            }

            if (!reach.All(pools.ContainsKey))
                continue;
            var parameters = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in library.Parameters)
                parameters[name] = Rendering.TemplateValues.Plain(value);
            if (library.Scripts.Count == 0)
            {
                pools[library.Pack] = new RuleLibrary(null, [], parameters);
                continue;
            }

            IReadOnlyList<ScriptLibrary> reached = [.. model.ScriptLibraries.Where(l => reach.Contains(l.Pack)).Select(l => new ScriptLibrary(l.Pack, pools[l.Pack].Helpers))];
            try
            {
                var pool = reached.Count == 0 ? scripts.CreatePool(library.Scripts, model.Settings.Limits, size, ct)
                    : scripts.CreatePool(library.Scripts, reached, model.Settings.Limits, size, ct);
                IReadOnlyList<string> helpers = [.. pool.Registrations.Where(r => r.Kind == ScriptRegistrationKind.Helper).Select(r => r.Name)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                pools[library.Pack] = new RuleLibrary(pool, helpers, parameters);
            }
            catch (Exception e) when (e is ScriptErrorException or ScriptLimitException)
            {
                var (rule, located) = e is ScriptLimitException limit ? ("MQ5003", limit.Diagnostic) : ("MQ5002", ((ScriptErrorException)e).Diagnostic);
                diagnostics.Add(located with
                {
                    Rule = rule,
                    Message = $"The library pack '{library.Pack}' that maquettiste.json uses failed to load, so rules cannot call it: {located.Message}",
                    FilePath = located.FilePath ?? library.Scripts[0].Path,
                });
            }
        }

        return pools;
    }

    /// <summary>The kinds of each element rule that names kinds; a rule missing here checks every kind.</summary>
    private static Dictionary<string, IReadOnlySet<string>> RuleKinds(IScriptSandboxPool pool)
    {
        var kinds = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var registration in pool.Registrations)
        {
            if (registration is { Kind: ScriptRegistrationKind.Rule, WholeModel: false, Resolved: false, Kinds: { } set })
                kinds.TryAdd(Bare(registration.Name), set);
        }

        return kinds;
    }

    /// <summary>
    /// The rules whose per-element results may be cached, with their rule file: a rule declared in one of the snapshot's rule scripts
    /// whose file has no MQ5005 warning (top-level <c>let</c> or <c>var</c>, state the sandbox cannot freeze). One scope per file and
    /// the globals frozen before the files run make a file's hash, with the shared sandbox setup, its behaviour (section 5.7).
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="pool">The rule pool.</param>
    /// <param name="resolved">Whether to give the resolved rules' files (scope "resolved", next-phase-2-design.md section 5.5) rather
    /// than the element rules'.</param>
    internal static Dictionary<string, ScriptSource> CachedRuleFiles(ModelSnapshot model, IScriptSandboxPool pool, bool resolved = false)
    {
        var stateful = pool.Warnings.Where(w => w.Rule == "MQ5005" && w.FilePath is not null).Select(w => w.FilePath!).ToHashSet(StringComparer.Ordinal);
        var files = new Dictionary<string, ScriptSource>(StringComparer.Ordinal);
        // The project's libraries fold into every rule file's key (library packs): a cached result is never served after one changed.
        // Without uses the keys are the file hashes, as before.
        var libraries = model.ScriptLibraries.Count == 0 ? null
            : Hashing.HashBuilder.Of(["mq-rule-libraries-1", .. model.ScriptLibraries.Select(l => l.Hash)]);
        foreach (var script in model.RuleScripts)
            files.TryAdd(script.Path, libraries is null ? script : script with { Hash = Hashing.HashBuilder.Of(script.Hash, libraries) });
        var cached = new Dictionary<string, ScriptSource>(StringComparer.Ordinal);
        foreach (var registration in pool.Registrations)
        {
            if (registration.Kind == ScriptRegistrationKind.Rule && !registration.WholeModel && registration.Resolved == resolved && !stateful.Contains(registration.DeclaredIn)
                && files.TryGetValue(registration.DeclaredIn, out var file))
                cached.TryAdd(Bare(registration.Name), file);
        }

        return cached;
    }

    /// <summary>The keys one rule call records.</summary>
    private sealed class RuleReads : IReadRecorder
    {
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

        public void Record(string dependencyKey) => Keys.Add(dependencyKey);
    }

    /// <summary>
    /// Runs every element rule on one document. A rule whose <c>validation.rules</c> setting (<c>x/&lt;rule&gt;</c>) is <c>off</c>
    /// is not run at all, so its own failures are off too; a severity setting also applies to the rule's failures (MQ5002, MQ5003),
    /// which keep their catalog id. An explicit MQ5002 or MQ5003 setting still applies on top, in <see cref="ApplySettings"/>. A rule's
    /// raw diagnostics come from the cache when its inputs are unchanged (<see cref="RuleResultCache"/>), and a clean run is stored. A
    /// rule whose kinds leave the document out is skipped first: no lease, no call, no cache entry.
    /// </summary>
    private static void RunScripts(RuleRun run, ModelSnapshot model, Report report, CancellationToken ct)
    {
        var document = report.Document;
        var kind = KindInfo.Get(document.Element.Kind).Name;
        var settings = model.Settings.Validation.Rules;
        var limits = model.Settings.Limits;
        var hasher = RuleKeyHasher.Of(model);
        IScriptSandboxLease? lease = null;
        try
        {
            foreach (var rule in run.Rules)
            {
                ct.ThrowIfCancellationRequested();
                if (run.Kinds.TryGetValue(rule, out var kinds) && !kinds.Contains(kind))
                    continue;
                var ruleId = "x/" + rule;
                var setting = settings.TryGetValue(ruleId, out var configured) ? configured : null;
                if (string.Equals(setting, "off", StringComparison.Ordinal))
                    continue;
                var failureSeverity = SeverityOf(setting);
                var ruleFile = run.Cached.GetValueOrDefault(rule);
                if (ruleFile is not null && run.Cache.TryGet(rule, document, ruleFile, limits, hasher, run.Ticket, out var cached))
                {
                    AddRuleDiagnostics(report, ruleId, failureSeverity, cached);
                    continue;
                }

                try
                {
                    var reads = ruleFile is null ? null : new RuleReads();
                    var context = new ScriptCallContext(reads, document.Element.Id, NoParameters, ct) { Libraries = run };
                    lease ??= run.Pool.Rent();
                    var diagnostics = lease.Sandbox.RunRule(rule, document, model, context);
                    // Only a clean run is kept: a rule that threw ends with its MQ5002 (a catalog id) after its reports.
                    if (reads is not null && !diagnostics.Any(d => RuleCatalog.TryGet(d.Rule, out _)))
                        run.Cache.Store(rule, document, ruleFile!, limits, reads.Keys, hasher, diagnostics, run.Ticket);
                    AddRuleDiagnostics(report, ruleId, failureSeverity, diagnostics);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (ScriptLimitException e)
                {
                    Fail("MQ5003", $"Rule {ruleId} exceeded a sandbox limit on this element: {e.Diagnostic.Message}");
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Fail("MQ5002", $"Rule {ruleId} failed on this element: {e.Message}");
                }

                void Fail(string id, string message)
                {
                    if (failureSeverity is { } severity)
                        report.Add(id, severity, message, "");
                    else
                        report.Add(id, message, "");
                }
            }
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>Adds a rule's raw diagnostics on one document, as they come from the sandbox or from the cache.</summary>
    private static void AddRuleDiagnostics(Report report, string ruleId, DiagnosticSeverity? failureSeverity, IEnumerable<Diagnostic> diagnostics)
    {
        var document = report.Document;
        foreach (var d in diagnostics)
        {
            // The sandbox returns a rule that throws as its own MQ5002 diagnostic (after the rule's reports), located at the failing
            // line of the rule script: it keeps its catalog id and its script position instead of becoming x/<id>.
            if (RuleCatalog.TryGet(d.Rule, out _))
            {
                report.Add(d with
                {
                    Severity = failureSeverity ?? d.Severity,
                    ElementId = d.ElementId ?? document.Element.Id,
                    FilePath = d.FilePath ?? document.Path,
                    JsonPointer = d.FilePath is null ? "" : d.JsonPointer,
                });
                continue;
            }

            report.Add(d with
            {
                Rule = d.Rule.StartsWith("x/", StringComparison.Ordinal) ? d.Rule : ruleId,
                ElementId = d.ElementId ?? document.Element.Id,
                FilePath = d.FilePath ?? document.Path,
                JsonPointer = d.JsonPointer ?? "",
            });
        }
    }

    /// <summary>
    /// Runs the whole-model rules (<c>scope: "model"</c>) once each, on one lease, in id order, except those <c>validation.rules</c>
    /// sets <c>off</c> (next-phase-1-design.md section 5.7). Each finding is on the file of the element it names. A rule that throws is
    /// MQ5002 and one that breaks a limit MQ5003, on the rule file at the failing line, with the rule's severity setting applied as for
    /// element rules. Their results are never cached.
    /// </summary>
    private static void RunModelRules(IScriptSandboxPool pool, IReadOnlyList<ScriptRegistration> modelRules, ModelSnapshot model, List<Diagnostic> diagnostics,
        IScriptLibraryCalls libraries, CancellationToken ct)
    {
        var settings = model.Settings.Validation.Rules;
        using var lease = pool.Rent();
        foreach (var registration in modelRules)
        {
            ct.ThrowIfCancellationRequested();
            var rule = Bare(registration.Name);
            var ruleId = "x/" + rule;
            var setting = settings.TryGetValue(ruleId, out var configured) ? configured : null;
            if (string.Equals(setting, "off", StringComparison.Ordinal))
                continue;
            var failureSeverity = SeverityOf(setting);
            try
            {
                var context = new ScriptCallContext(null, ruleId, NoParameters, ct) { Libraries = libraries };
                foreach (var d in lease.Sandbox.RunModelRule(rule, model, context))
                {
                    if (RuleCatalog.TryGet(d.Rule, out _))
                    {
                        diagnostics.Add(d with { Severity = failureSeverity ?? d.Severity, FilePath = d.FilePath ?? registration.DeclaredIn });
                        continue;
                    }

                    diagnostics.Add(d with { Rule = d.Rule.StartsWith("x/", StringComparison.Ordinal) ? d.Rule : ruleId, JsonPointer = d.JsonPointer ?? "" });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ScriptLimitException e)
            {
                Fail("MQ5003", $"Rule {ruleId} exceeded a sandbox limit: {e.Diagnostic.Message}", e.Diagnostic);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Fail("MQ5002", $"Rule {ruleId} failed: {e.Message}", null);
            }

            void Fail(string id, string message, Diagnostic? located)
            {
                var failure = RuleCatalog.Create(id, message, null, located?.FilePath ?? registration.DeclaredIn)
                    with { Line = located?.Line, Column = located?.Column };
                diagnostics.Add(failureSeverity is { } severity ? failure with { Severity = severity } : failure);
            }
        }
    }

    /// <summary>The severity a <c>validation.rules</c> setting sets, or <see langword="null"/> (none, <c>off</c> or unknown).</summary>
    internal static DiagnosticSeverity? SeverityOf(string? setting) => setting switch
    {
        "error" => DiagnosticSeverity.Error,
        "warning" => DiagnosticSeverity.Warning,
        "info" => DiagnosticSeverity.Info,
        _ => null,
    };

    /// <summary>
    /// Applies <c>validation.rules</c>: a severity override, or <c>off</c>. MQ1xxx ignore <c>off</c>; the MQ1xxx errors that leave a
    /// file out of the snapshot (MQ1001, MQ1002, MQ1004, MQ1006, MQ1007, MQ1009, and MQ7012, a retired option refused at load) ignore every setting.
    /// </summary>
    internal static List<Diagnostic> ApplySettings(ProjectSettings settings, List<Diagnostic> diagnostics)
    {
        var rules = settings.Validation.Rules;
        if (rules.Count == 0)
            return diagnostics;
        var result = new List<Diagnostic>(diagnostics.Count);
        foreach (var d in diagnostics)
        {
            if (!rules.TryGetValue(d.Rule, out var setting) || FixedSeverityRules.Contains(d.Rule))
            {
                result.Add(d);
                continue;
            }

            switch (setting)
            {
                case "off" when !d.Rule.StartsWith("MQ1", StringComparison.Ordinal):
                    break;
                case "error":
                    result.Add(d with { Severity = DiagnosticSeverity.Error });
                    break;
                case "warning":
                    result.Add(d with { Severity = DiagnosticSeverity.Warning });
                    break;
                case "info":
                    result.Add(d with { Severity = DiagnosticSeverity.Info });
                    break;
                default:
                    result.Add(d);
                    break;
            }
        }

        return result;
    }

    private async Task<List<Diagnostic>> AddPositionsAsync(ModelSnapshot model, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var positions = new Positions(options, model);
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            if (d.Line is not null || d.FilePath is null || d.JsonPointer is null)
                continue;
            ct.ThrowIfCancellationRequested();
            if (await positions.LocateAsync(d.FilePath, d.JsonPointer, ct).ConfigureAwait(false) is { } position)
                diagnostics[i] = d with { Line = position.Line, Column = position.Column };
        }

        return diagnostics;
    }
}
