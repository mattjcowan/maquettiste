using System.Collections.Frozen;
using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Text;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// One resolution of one snapshot (engine-design.md section 7). Holds the working state of a run; nothing survives it. The
/// conceptual layer is resolved first (<c>ResolveRun.Conceptual.cs</c>), then each database (<c>DatabaseRun</c>) with its migration
/// facts (<c>ResolveRun.Migrations.cs</c>), then the dependency keys and list memberships are frozen.
/// </summary>
internal sealed partial class ResolveRun
{
    private const string Conventions = "s:conventions";
    private const string TypeMaps = "s:typeMaps";
    private const string InflectionKey = "s:inflection";

    private readonly Dictionary<string, IResolvedObject> _byId;
    /// <summary>
    /// The objects that have a dependency set, frozen together at the end. Added to from the parallel phases once per object (about
    /// 330,000 on the benchmark), so a bag of per-thread lists rather than a locked list; freezing is per object, so order is free.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentBag<RObject> _withDependencies;
    private readonly IProgress<ProgressUpdate>? _progress;
    private readonly int _parallelism;
    private int _done;
    private int _total;

    public ResolveRun(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct, int parallelism = 1, Inflector? inflector = null)
    {
        _parallelism = Math.Max(1, parallelism);
        Model = model;
        Settings = model.Settings;
        Inflector = inflector ?? new Inflector(model.Settings.Inflection);
        Ct = ct;
        _progress = progress;
        // Sized from the model up front: a resolution registers several objects per element (attributes, columns, tables, ends), and
        // growing a dictionary of a few hundred thousand entries step by step costs more than the estimate.
        var estimate = Math.Max(1024, model.Documents.Count * 16);
        _byId = new Dictionary<string, IResolvedObject>(estimate, StringComparer.Ordinal);
        _withDependencies = [];
    }

    public ModelSnapshot Model { get; }

    public ProjectSettings Settings { get; }

    /// <summary>The sandbox factory that runs seeds' rows scripts when their rows are first read (next-phase-2-design.md section 3.4).</summary>
    public Scripting.IScriptSandboxFactory Scripts { get; init; } = DefaultScripts;

    /// <summary>The rows cache those runs go through.</summary>
    public SeedRowsCache SeedRows { get; init; } = new();

    /// <summary>
    /// The host of the run's calculated values (next-phase-2-design.md section 5.5), or <see langword="null"/> when the project declares
    /// none; a full run binds every annotated object it builds to it (<see cref="CalculationHost.Build{T}"/>) and attaches it to the model.
    /// </summary>
    public CalculationHost? Calculations { get; init; }

    private static readonly Scripting.IScriptSandboxFactory DefaultScripts = new Scripting.ScriptSandboxFactory();

    public Inflector Inflector { get; }

    public CancellationToken Ct { get; }

    public List<Diagnostic> Diagnostics { get; } = [];

    /// <summary>Shared <c>e:</c> and <c>r:</c> key strings.</summary>
    public DependencyKeyCache Keys { get; } = new();

    /// <summary>Runs the resolution.</summary>
    public ResolvedModel Run()
    {
        try
        {
            return RunCore();
        }
        finally
        {
            // The bag's per-thread lists live in thread statics of the pool threads that added to it: emptying them here (also after a
            // cancelled or failed run) keeps them from holding the resolved objects until the bag's finalizer runs.
            _withDependencies.Clear();
        }
    }

    /// <summary>
    /// Resolves the conceptual layer and one database only, for the explorer's table summaries and table detail (E5c, E5f;
    /// explorer-redesign.md section 4.1). The database's tables are those of <see cref="Run"/>: a database run reads the conceptual
    /// layer and its own files only; what an earlier database run adds (relation dependency keys, <c>FinishRelationMappings</c>) and
    /// what runs after the databases (the conceptual finish, the sensitivity bound attributes show, seeds, usages, the dependency freeze) do not shape tables and are
    /// skipped, so dependency lists are not set; the reference type bindings into its tables are linked, since a reference column's
    /// bound table is part of its shape. The diagnostics are the conceptual layer's and this database's.
    /// </summary>
    /// <param name="databaseId">The database element's id.</param>
    /// <returns>The resolved database, or <see langword="null"/> when no database has the id, and the sorted diagnostics.</returns>
    public (RDatabase? Database, IReadOnlyList<Diagnostic> Diagnostics) RunOneDatabase(string databaseId)
    {
        var (databases, diagnostics) = RunDatabases([databaseId], freeze: false);
        return (databases[0], diagnostics);
    }

    /// <summary>
    /// Resolves the conceptual layer once and then each of the given databases, as <see cref="RunOneDatabase"/> does for one (the
    /// recording of migration facts resolves the databases a write can reshape this way, next-phase-1-design.md section 3.2). With
    /// <paramref name="freeze"/>, the dependency keys of every object resolved are frozen into <see cref="RObject.Dependencies"/>,
    /// so a database's footprint (the element ids of its objects' <c>e:</c> keys) can be read; the keys are those of this partial run.
    /// </summary>
    /// <param name="databaseIds">The database element ids, in the order to resolve them.</param>
    /// <param name="freeze">Whether to freeze the dependency keys.</param>
    /// <returns>Per id, the resolved database or <see langword="null"/> when no database has it, and the sorted diagnostics.</returns>
    public (IReadOnlyList<RDatabase?> Databases, IReadOnlyList<Diagnostic> Diagnostics) RunDatabases(IReadOnlyList<string> databaseIds, bool freeze)
    {
        ArgumentNullException.ThrowIfNull(databaseIds);
        try
        {
            ResolveConceptual();
            var results = new RDatabase?[databaseIds.Count];
            for (var i = 0; i < databaseIds.Count; i++)
            {
                var database = Model.Get<Database>(databaseIds[i]);
                if (database is null)
                    continue;
                Ct.ThrowIfCancellationRequested();
                var resolved = new DatabaseRun(this, database).Run();
                ResolveMigrations(resolved, database);
                results[i] = resolved;
            }

            LinkReferenceBindings(results);
            if (freeze)
                FreezeDependencies();
            return (results, [.. Diagnostics.Order(Diagnostic.Order)]);
        }
        finally
        {
            _withDependencies.Clear();
        }
    }

    /// <summary>
    /// Links the reference type bindings into the given databases' tables, as a full run does after every database: a reference
    /// column's bound table (<see cref="RColumn.ReferenceBinding"/>) is part of its physical snapshot, so the databases a write records
    /// facts for capture it as validate and generate do (next-phase-2-design.md section 3.2, decision 8).
    /// </summary>
    private void LinkReferenceBindings(IReadOnlyList<RDatabase?> databases)
    {
        var tablesByKey = new Dictionary<string, RTable>(StringComparer.Ordinal);
        foreach (var database in databases)
        {
            foreach (var table in database?.Tables.Where(t => t.Origin != "synthesized") ?? [])
                tablesByKey.TryAdd(table.Key, table);
        }

        ResolveReferenceBindings(tablesByKey);
    }

    private ResolvedModel RunCore()
    {
        var databases = Model.All<Database>().OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        // One progress step per conceptual element resolved and per database.
        _total = Model.All<Package>().Count + Model.All<ScalarType>().Count + Model.All<EnumType>().Count + Model.All<ValueObject>().Count
            + Model.All<Entity>().Count + Model.All<Relation>().Count + databases.Count;
        ResolveConceptual();

        var resolvedDatabases = new List<RDatabase>();
        foreach (var database in databases)
        {
            Ct.ThrowIfCancellationRequested();
            var run = new DatabaseRun(this, database);
            var resolved = run.Run();
            ResolveMigrations(resolved, database);
            resolvedDatabases.Add(resolved);
            Report();
        }

        FinishConceptual();
        ResolveShared();
        ResolveSeedsAndUsages(resolvedDatabases);
        var stereotypes = ResolveStereotypeDefinitions(); // before processes: a subject entry may name a stereotype
        ResolveEndStereotypes();
        ResolveProcesses();
        ResolveOperations();
        ResolveCustomElements();
        var verbs = ResolveVerbs();
        var (vocabularies, trees) = ResolveDefinitions(stereotypes);
        LinkOperationRequires(); // last: an id of any kind resolves, whatever registered it

        var result = new ResolvedModel
        {
            Source = Model,
            Settings = Settings,
            Packages = new RList<RPackage>(_packageOrder, ["k:package"]),
            Entities = new RList<REntity>(SortedEntities(), EntityMembership),
            ValueObjects = new RList<RValueObject>(_valueObjectOrder, ["k:value-object"]),
            Enums = new RList<REnum>(_enumOrder, ["k:enum"]),
            ScalarTypes = new RList<RScalarType>(_scalarOrder, ["k:scalar-type"]),
            Relations = new RList<RRelation>(SortedRelations(), EntityMembership),
            ReferenceTypes = new RList<RReferenceType>(_referenceTypeOrder, ["k:reference-type"]),
            Seeds = new RList<RSeed>(_seedOrder, ["k:seed"]),
            SeedsInOrderFactory = _seedsInOrder,
            Processes = new RList<RProcess>(_processOrder, [ProcessKind]),
            ProcessLibraries = new RList<RProcess>(_libraryOrder, [ProcessKind]),
            Actors = new RList<RActor>(_actorOrder, [ActorKind]),
            Scenarios = new RList<RScenario>(_scenarioOrder, [ScenarioKind]),
            Rights = new RList<RRight>(_rightOrder, [RightKind]),
            RightsLifecycles = new RList<RProcess>(_rightsLifecycles, [RightsNaming.Key]),
            TriggerLifecycles = new RList<RProcess>(_triggerLifecycles, [TriggerNaming.Key]),
            ApprovalLifecycles = new RList<RProcess>(_approvalLifecycles, [ApprovalNaming.Key]),
            ApprovalsUseRounds = _approvalGroups.HasFlag(ApprovalGroups.Rounds),
            ApprovalsUseDelegation = _approvalGroups.HasFlag(ApprovalGroups.Delegation),
            ApprovalsUseNotices = _approvalGroups.HasFlag(ApprovalGroups.Notices),
            ApprovalsUseReauthenticate = _approvalGroups.HasFlag(ApprovalGroups.Password),
            // Keyed on the named trigger points' files alone. Their kind set's hash never changes in a project without one, so what reads this
            // list in a part 1 project never renders again when a lifecycle's trigger points change; the first named point changes it.
            TriggerPoints = new RList<RTriggerPoint>(_namedTriggerPoints, [TriggerPointKind]),
            Freezing = RFreezing.Of(Model),
            Operations = new RList<ROperation>(_operationOrder, [OperationKind]),
            CustomElements = new RList<RCustom>(_customOrder, [CustomKind]),
            Constraints = new RList<RConstraint>(_constraintOrder, ["k:constraint"]),
            Databases = new RList<RDatabase>(resolvedDatabases, ["k:database"]),
            Migrations = new RList<RMigration>(_migrationOrder, MigrationMembership()),
            Locales = new RList<RLocale>(RLocale.Of(Settings.Localization), [RLocale.SettingsKey]),
            Stereotypes = new RList<RStereotypeDefinition>(stereotypes, ["k:stereotype"]),
            TagVocabularies = new RList<RTagVocabulary>(vocabularies, ["k:tag-vocabulary"]),
            CategoryTrees = new RList<RCategoryTree>(trees, ["k:category-tree"]),
            Verbs = verbs,
            Diagnostics = [.. Suppressions.Of(Model).Apply(Diagnostics, out var suppressed).Order(Diagnostic.Order)],
            Suppressed = suppressed,
            ById = _byId, // lookups only, never enumerated; freezing a few hundred thousand ids costs more than it saves
            Calculated = Calculations,
        };
        FreezeDependencies();
        Calculations?.Attach(result);
        return result;
    }

    /// <summary>
    /// The definitions <c>model</c> lists (next-phase-1-design.md section 5.5): the stereotype of each key (the one
    /// <see cref="ModelSnapshot.GetStereotype"/> gives, by key), and the tag vocabularies and category trees validation uses, one per
    /// scope, the global one first, then by (package qualified name, id). Each depends on its own file; a tree also completes the
    /// shared <see cref="RCategory"/> instances (parent, children, order, description, rationale, decision, status) the conceptual pass created.
    /// </summary>
    private (List<RTagVocabulary> Vocabularies, List<RCategoryTree> Trees) ResolveDefinitions(List<RStereotypeDefinition> stereotypes)
    {
        // A stereotype's errors last, after the operations that carry it, as before the definitions moved ahead of the processes.
        foreach (var r in stereotypes)
        {
            if (Model.GetStereotype(r.Key) is { } stereotype)
                r.Errors = StereotypeErrors(stereotype, r);
        }

        var vocabularies = new List<RTagVocabulary>();
        foreach (var vocabulary in Model.TagVocabularies)
        {
            var r = new RTagVocabulary();
            FillCommon(r, vocabulary, DepsOf(r));
            r.Package = PackageOf(vocabulary.Package);
            r.Strict = vocabulary.Strict;
            r.Definitions = [.. vocabulary.Definitions.Select(d => new RTagDefinition { Key = d.Key, Description = d.Description, Color = d.Color })];
            Register(r);
            vocabularies.Add(r);
        }

        var trees = new List<RCategoryTree>();
        foreach (var tree in Model.CategoryTrees)
        {
            var r = new RCategoryTree();
            FillCommon(r, tree, DepsOf(r));
            r.Package = PackageOf(tree.Package);
            (r.Roots, r.Categories) = CategoryTreeOf(tree);
            Register(r);
            trees.Add(r);
        }

        return (ScopeOrder(vocabularies), ScopeOrder(trees));
    }

    /// <summary>The stereotype definitions by key (<see cref="ResolveStereotypeDefinitions"/>), for the subject entries that name one.</summary>
    private readonly Dictionary<string, RStereotypeDefinition> _stereotypeDefinitions = new(StringComparer.Ordinal);

    /// <summary>
    /// The stereotype of each key (the one <see cref="ModelSnapshot.GetStereotype"/> gives), by key, each depending on its own file; their
    /// errors come after the operations (<see cref="ResolveDefinitions"/>).
    /// </summary>
    private List<RStereotypeDefinition> ResolveStereotypeDefinitions()
    {
        var stereotypes = new List<RStereotypeDefinition>();
        foreach (var key in Model.All<Stereotype>().Select(s => s.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (Model.GetStereotype(key) is not { } stereotype)
                continue;
            var r = new RStereotypeDefinition();
            var deps = DepsOf(r);
            FillCommon(r, stereotype, deps);
            r.Package = PackageOf(stereotype.Package);
            r.Key = stereotype.Key;
            r.AppliesTo = [.. stereotype.AppliesTo];
            var attributes = StableByOrder(stereotype.Attributes.DistinctBy(a => a.Id, StringComparer.Ordinal), a => a.Order)
                .Select(a => MakeAttribute(a, r, r.Package, null, false, stereotype, stereotype.Id))
                .ToList();
            SetOrder(attributes);
            r.Attributes = new RList<RAttribute>(attributes, deps.ToList());
            r.DefaultProperties = stereotype.DefaultProperties.Count == 0
                ? FrozenDictionary<string, object?>.Empty
                : ResolutionValues.PlainMap(stereotype.DefaultProperties);
            r.Icon = stereotype.Icon;
            r.Color = stereotype.Color;
            Register(r);
            stereotypes.Add(r);
            _stereotypeDefinitions[r.Key] = r;
        }

        return stereotypes;
    }

    /// <summary>The global definition first (no <c>package</c>), then by (package qualified name, id), ordinal.</summary>
    private static List<T> ScopeOrder<T>(List<T> values) where T : RElement =>
        [.. values.OrderBy(v => v.Package is null ? 0 : 1).ThenBy(v => v.Package?.QualifiedName ?? "", StringComparer.Ordinal).ThenBy(v => v.Id, StringComparer.Ordinal)];

    /// <summary>
    /// A tree's roots and its categories depth-first, on the <see cref="RCategory"/> instances the conceptual pass registered for this
    /// tree (the first entry of an id). Siblings go by order (none counts as 0), then file order; a parent the tree lacks, or the
    /// category itself, makes a root; a category caught in a longer parent cycle is walked after the roots, in file order.
    /// </summary>
    private (IReadOnlyList<RCategory> Roots, IReadOnlyList<RCategory> Categories) CategoryTreeOf(CategoryTree tree)
    {
        var own = new List<(Category Source, RCategory Category)>();
        var byId = new Dictionary<string, RCategory>(StringComparer.Ordinal);
        foreach (var category in tree.Categories)
        {
            if (byId.ContainsKey(category.Id) || !_categories.TryGetValue(category.Id, out var rc)
                || !string.Equals(_categoryTreeOf.GetValueOrDefault(category.Id), tree.Id, StringComparison.Ordinal))
                continue;
            byId.Add(category.Id, rc);
            own.Add((category, rc));
        }

        var roots = new List<RCategory>();
        var children = new Dictionary<RCategory, List<RCategory>>(ReferenceEqualityComparer.Instance);
        foreach (var (source, rc) in StableByOrder(own, x => x.Source.Order))
        {
            rc.Order = source.Order;
            rc.Description = source.Description?.Text;
            rc.Rationale = source.Rationale;
            rc.Decision = source.Decision;
            rc.Status = StatusName(source.Status);
            rc.Parent = source.Parent is { } parentId && byId.TryGetValue(parentId, out var parent) && !ReferenceEquals(parent, rc) ? parent : null;
            if (rc.Parent is null)
                roots.Add(rc);
            else if (children.TryGetValue(rc.Parent, out var siblings))
                siblings.Add(rc);
            else
                children.Add(rc.Parent, [rc]);
        }

        foreach (var (_, rc) in own)
            rc.Children = children.TryGetValue(rc, out var list) ? list : [];

        var ordered = new List<RCategory>(own.Count);
        var seen = new HashSet<RCategory>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<RCategory>();
        foreach (var start in roots.Concat(own.Select(x => x.Category)))
        {
            stack.Push(start);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current))
                    continue;
                ordered.Add(current);
                for (var i = current.Children.Count - 1; i >= 0; i--)
                    stack.Push(current.Children[i]);
            }
        }

        return (roots, ordered);
    }

    /// <summary>Registers an object for <see cref="ResolvedModel.Find"/>; the first registration of an id wins.</summary>
    public void Register(IResolvedObject value) => _byId.TryAdd(value.Id, value);

    /// <summary>The dependency set of an object, created on first use.</summary>
    /// <remarks>
    /// Safe from the parallel phases: there each object's set is created and written only by the thread that owns the object, and
    /// sets of other objects are only read once frozen; the bag of objects to freeze is shared and thread-safe (no lock).
    /// </remarks>
    public DependencySet DepsOf(RObject value)
    {
        if (value.PendingDependencies is not { } set)
        {
            set = new DependencySet(Keys);
            value.PendingDependencies = set;
            _withDependencies.Add(value);
        }

        return set;
    }

    /// <summary>
    /// Runs <paramref name="body"/> for 0..<paramref name="count"/>-1 on up to the run's parallelism, observing cancellation. The body
    /// must write only objects it creates or owns; the caller commits shared state afterwards in index order.
    /// </summary>
    internal void ForEach(int count, Action<int> body)
    {
        if (_parallelism <= 1 || count < 64)
        {
            for (var i = 0; i < count; i++)
            {
                Ct.ThrowIfCancellationRequested();
                body(i);
            }

            return;
        }

        // Every index runs; if any fails, the failure of the lowest index is rethrown, which is the one a sequential loop would meet.
        var failedAt = int.MaxValue;
        Exception? failure = null;
        var gate = new Lock();
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = Ct }, i =>
        {
            try
            {
                body(i);
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    if (i < failedAt)
                        (failedAt, failure) = (i, e);
                }
            }
        });
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Reports progress for one element, and observes cancellation between elements.</summary>
    public void Report()
    {
        Ct.ThrowIfCancellationRequested();
        _done++;
        _progress?.Report(new ProgressUpdate(PipelineStage.Resolve, _done, Math.Max(_done, _total), null, null));
    }

    /// <summary>Adds a diagnostic whose severity follows <c>validation.rules</c>; nothing when the rule is off.</summary>
    public void AddDiagnostic(string rule, string message, string? elementId) => AddDiagnostic(rule, message, elementId, null);

    /// <summary>Adds a diagnostic at a JSON pointer in the element's file; its severity follows <c>validation.rules</c>; nothing when the rule is off.</summary>
    public void AddDiagnostic(string rule, string message, string? elementId, string? pointer)
    {
        var path = elementId is null ? null : Model.GetDocument(elementId)?.Path;
        Add(rule, message, elementId, path, path is null ? null : pointer);
    }

    /// <summary>
    /// Adds a diagnostic about a member a table inherits (next-phase-2-design.md section 3.1), which no file holds: on the file of
    /// <paramref name="fileElementId"/> without a pointer, with the member's keyed id (<c>&lt;tableKey&gt;.&lt;memberId&gt;</c>) as its element
    /// id, so a suppression on the table names it (section 5.6). Without that file it is on <paramref name="fileElementId"/>, as
    /// <see cref="AddDiagnostic(string, string, string?)"/> reports.
    /// </summary>
    public void AddMemberDiagnostic(string rule, string message, string fileElementId, string memberId)
    {
        var path = Model.GetDocument(fileElementId)?.Path;
        Add(rule, message, path is null ? fileElementId : memberId, path, null);
    }

    private void Add(string rule, string message, string? elementId, string? path, string? pointer)
    {
        var severity = RuleCatalog.TryGet(rule, out var info) ? info.DefaultSeverity : DiagnosticSeverity.Error;
        if (Settings.Validation.Rules.TryGetValue(rule, out var configured))
        {
            switch (configured)
            {
                case "off": return;
                case "error": severity = DiagnosticSeverity.Error; break;
                case "warning": severity = DiagnosticSeverity.Warning; break;
                case "info": severity = DiagnosticSeverity.Info; break;
            }
        }

        Diagnostics.Add(new Diagnostic(rule, severity, message, elementId, path, pointer, null, null));
    }

    private void FreezeDependencies()
    {
        // Each object's set is frozen on its own (sets share only immutable arrays), so this runs in parallel too.
        var objects = _withDependencies.ToArray();
        ForEach(objects.Length, i =>
        {
            var obj = objects[i];
            obj.Dependencies = obj.PendingDependencies!.ToList();
            obj.PendingDependencies = null;
        });
    }

    /// <summary>Compares elements by (package qualified name, name, id), ordinal (section 7.1).</summary>
    private int CompareElements(RElement a, RElement b)
    {
        var c = string.CompareOrdinal(a.Package?.QualifiedName ?? "", b.Package?.QualifiedName ?? "");
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Name, b.Name);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    private List<T> Sorted<T>(IEnumerable<T> values) where T : RElement
    {
        var list = values.ToList();
        list.Sort(CompareElements);
        return list;
    }

    private static ImmutableSortedDictionary<string, TValue> SortedMap<TValue>(IEnumerable<KeyValuePair<string, TValue>> values) =>
        values.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}
