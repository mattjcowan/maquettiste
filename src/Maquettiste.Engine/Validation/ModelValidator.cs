using System.Collections.Frozen;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Stage 2: built-in rules, extension schemas and JavaScript rules (W2; engine-design.md section 6).
/// <para>
/// The report holds the snapshot's load diagnostics (MQ1xxx, filtered to the scope), the built-in rules of every file in scope,
/// extension schema failures, extension file problems (whole-model scope only) and JavaScript rule results. Rule severities
/// follow <c>validation.rules</c> in <c>maquettiste.json</c> (<c>off</c> drops a rule; MQ1xxx cannot be turned off, and the MQ1xxx
/// errors that leave a file out of the snapshot keep their severity). Line and
/// column are filled for every diagnostic with a file and pointer. Files are validated in parallel
/// (<see cref="EngineOptions.MaxDegreeOfParallelism"/>); the result does not depend on the degree.
/// </para>
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="schemas">The schema registry (kept for the design's constructor; extension schemas are compiled here, see README).</param>
/// <param name="scripts">The sandbox factory (JavaScript rules).</param>
internal sealed class ModelValidator(EngineOptions options, ISchemaRegistry schemas, IScriptSandboxFactory scripts) : IModelValidator
{
    private static readonly IReadOnlyDictionary<string, object?> NoParameters = FrozenDictionary<string, object?>.Empty;

    /// <summary>
    /// The MQ1xxx rules whose file is left out of the snapshot (or ignored), so <c>validation.rules</c> cannot change their severity:
    /// lowering one would let the file vanish from generation without an error.
    /// </summary>
    private static readonly FrozenSet<string> FixedSeverityRules =
        new[] { "MQ1001", "MQ1002", "MQ1004", "MQ1006", "MQ1007", "MQ1009", "MQ7012" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ReferenceWalker _walker = new();

    /// <summary>The schema registry.</summary>
    internal ISchemaRegistry Schemas => schemas;

    /// <inheritdoc/>
    public async Task<ValidationReport> ValidateAsync(ModelSnapshot model, ValidationScope scope, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        scope ??= ValidationScope.All;
        ct.ThrowIfCancellationRequested();

        var active = ActiveDocuments(model);
        var shared = new ValidationContext(model, active, _walker, null);
        var targets = SelectTargets(shared, active, scope);
        var wholeModel = scope.ElementIds is null;
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
        IReadOnlyList<string> rules = [];
        if (needScripts)
        {
            // A rule file that does not load (a syntax error, a registration without an id, a limit) is reported on that file with its
            // line and column, and the other rule files still run without it. While one is broken the set of rule ids is unknown, so
            // MQ2007 (an unknown rule) is not reported against the ids it would have registered.
            var loadable = model.RuleScripts.ToList();
            var failed = false;
            while (loadable.Count > 0)
            {
                try
                {
                    pool = scripts.CreatePool(loadable, model.Settings.Limits, Math.Max(1, Math.Min(parallelism, targets.Count)), ct);
                    rules = [.. pool.Registrations.Where(r => r.Kind == ScriptRegistrationKind.Rule).Select(r => Bare(r.Name))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                    ruleNames = failed ? null : rules.ToFrozenSet(StringComparer.Ordinal);
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
        }

        var runtime = targets.Any(d => d.Element is Scenario) ? new ProcessRuntime(model, parallelism, ct) : null;
        try
        {
            var context = shared.WithRuleNames(ruleNames);
            var runScripts = scope.IncludeScriptRules && pool is not null && rules.Count > 0;
            var perDocument = new IReadOnlyList<Diagnostic>[targets.Count];
            var done = 0;
            await Parallel.ForEachAsync(
                Enumerable.Range(0, targets.Count),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                (i, token) =>
                {
                    var report = new Report(targets[i]);
                    BuiltinRules.Validate(context, report);
                    ProcessRules.Validate(context, report);
                    ScenarioRules.Validate(context, report, runtime);
                    extensions.Check(context, report);
                    if (runScripts)
                        RunScripts(pool!, rules, model, report, token);
                    perDocument[i] = report.Items;
                    progress?.Report(new ProgressUpdate(PipelineStage.Validate, Interlocked.Increment(ref done), targets.Count, targets[i].Path, null));
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

            foreach (var items in perDocument)
                diagnostics.AddRange(items);
        }
        finally
        {
            pool?.Dispose();
            runtime?.Dispose();
        }

        ct.ThrowIfCancellationRequested();
        var configured = ApplySettings(model.Settings, diagnostics).Distinct().ToList(); // one diagnostic per distinct finding
        var positioned = await AddPositionsAsync(model, configured, ct).ConfigureAwait(false);
        return ValidationReport.From(positioned);
    }

    private static string Bare(string ruleName) => ruleName.StartsWith("x/", StringComparison.Ordinal) ? ruleName[2..] : ruleName;

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
                foreach (var id in SubElementIds(owner.Element).Prepend(owner.Element.Id))
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
    /// every element under it (a move changes their chains).
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

        switch (element)
        {
            case TagVocabulary or CategoryTree:
                // A vocabulary decides MQ2005, MQ2006 and MQ2008 on every element whose chain sees its scope, and MQ3021 and MQ1009
                // on the other vocabularies; tags are not references, so none of them is a referrer.
                foreach (var peer in VocabularyDependents(model, ModelIndexer.PackageOf(element)))
                    yield return peer;
                break;
            case Package when HasDomainVocabularies(model):
                // Moving a domain changes the chain of every element under it (MQ2006, MQ2008) and the enclosing domains of its
                // vocabularies (MQ3021).
                foreach (var peer in VocabularyDependents(model, element.Id))
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
                if (ValidationContext.CompositionEnds(relation) is { } ends)
                {
                    foreach (var peer in context.CompositionsOwning(relation.Ends[ends.Child].Entity))
                        yield return peer;
                }

                for (var i = 0; i < relation.Ends.Count; i++)
                {
                    if (relation.Ends[i].Navigation.Length == 0)
                        continue;
                    foreach (var holderId in ValidationContext.NavigationHolders(relation, i))
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

    /// <summary>The relation files that generate navigations on an entity, its ancestors or its descendants.</summary>
    private static IEnumerable<ElementDocument> HierarchyNavigationDocuments(ValidationContext context, Entity entity)
    {
        foreach (var member in context.Ancestors(entity).Prepend(entity).Concat(context.Descendants(entity.Id)))
        {
            foreach (var site in context.NavigationsOn(member.Id))
                yield return site.Document;
        }
    }

    /// <summary>Every sub-element id an element holds (attributes, members, ends, columns, keys, categories, schemas).</summary>
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
        {
            yield return t.Id;
            if (t.Gate is not { } gate)
                continue;
            yield return gate.Id;
            foreach (var m in gate.Meanings) yield return m.Id;
            foreach (var a in gate.AuditAttributes) yield return a.Id;
        }
    }

    // The process rules that read other files: MQ9106 on actors depends on every process, MQ9015 on the sub-processes a process
    // reaches, and MQ9203 to MQ9205 on the bound enum, the subject's attributes and every other use of the enum.
    private static IEnumerable<ElementDocument> ProcessPeers(ModelSnapshot model, Element element)
    {
        if (element is Process process)
        {
            foreach (var actor in model.All<Actor>())
            {
                if (model.GetDocument(actor.Id) is { } peer)
                    yield return peer;
            }

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
                if (bound.BoundAttribute is not null && model.GetDocument(bound.Id) is { } peer)
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
                Entity entity => string.Equals(candidate.Subject, entity.Id, StringComparison.Ordinal),
                EnumType or ValueObject or ScalarType => UsesType(candidate, element.Id),
                _ => false,
            };
            if (affected)
                replayed.Add(candidate.Id);
        }

        if (replayed.Count == 0)
            yield break;
        foreach (var scenario in model.All<Scenario>())
        {
            if (replayed.Contains(scenario.Process) && model.GetDocument(scenario.Id) is { } peer)
                yield return peer;
        }
    }

    private static bool UsesType(Process process, string typeId) =>
        process.Context.Concat(process.Events.SelectMany(e => e.Payload))
            .Concat(process.Transitions.Where(t => t.Gate is not null).SelectMany(t => t.Gate!.AuditAttributes))
            .Any(a => string.Equals(a.Type.Ref, typeId, StringComparison.Ordinal));

    private static bool NamesRules(IEnumerable<ElementDocument> documents)
    {
        foreach (var doc in documents)
        {
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
    /// Runs every registered rule on one document. A rule whose <c>validation.rules</c> setting (<c>x/&lt;rule&gt;</c>) is <c>off</c>
    /// is not run at all, so its own failures are off too; a severity setting also applies to the rule's failures (MQ5002, MQ5003),
    /// which keep their catalog id. An explicit MQ5002 or MQ5003 setting still applies on top, in <see cref="ApplySettings"/>.
    /// </summary>
    private static void RunScripts(IScriptSandboxPool pool, IReadOnlyList<string> rules, ModelSnapshot model, Report report, CancellationToken ct)
    {
        var document = report.Document;
        var settings = model.Settings.Validation.Rules;
        using var lease = pool.Rent();
        foreach (var rule in rules)
        {
            ct.ThrowIfCancellationRequested();
            var ruleId = "x/" + rule;
            var setting = settings.TryGetValue(ruleId, out var configured) ? configured : null;
            if (string.Equals(setting, "off", StringComparison.Ordinal))
                continue;
            var failureSeverity = SeverityOf(setting);
            try
            {
                var context = new ScriptCallContext(null, document.Element.Id, NoParameters, ct);
                foreach (var d in lease.Sandbox.RunRule(rule, document, model, context))
                {
                    // The sandbox returns a rule that throws as its own MQ5002 diagnostic (after the rule's reports), located at the
                    // failing line of the rule script: it keeps its catalog id and its script position instead of becoming x/<id>.
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

    /// <summary>The severity a <c>validation.rules</c> setting sets, or <see langword="null"/> (none, <c>off</c> or unknown).</summary>
    private static DiagnosticSeverity? SeverityOf(string? setting) => setting switch
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
