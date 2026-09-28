using System.Collections.Immutable;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// One resolution of one snapshot (engine-design.md section 7). Holds the working state of a run; nothing survives it. The
/// conceptual layer is resolved first (<c>ResolveRun.Conceptual.cs</c>), then each database (<c>DatabaseRun</c>), then the
/// dependency keys and list memberships are frozen.
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
            resolvedDatabases.Add(run.Run());
            Report();
        }

        FinishConceptual();
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
            Databases = new RList<RDatabase>(resolvedDatabases, ["k:database"]),
            Diagnostics = [.. Diagnostics.Order(Diagnostic.Order)],
            ById = _byId, // lookups only, never enumerated; freezing a few hundred thousand ids costs more than it saves
        };
        FreezeDependencies();
        return result;
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
    public void AddDiagnostic(string rule, string message, string? elementId)
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

        var path = elementId is null ? null : Model.GetDocument(elementId)?.Path;
        Diagnostics.Add(new Diagnostic(rule, severity, message, elementId, path, null, null, null));
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
