using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The calculated values of one resolved model (next-phase-2-design.md section 5.5). On first use it takes the sandbox pool of the
/// calculation files from its service (<see cref="CalculationService.Load"/>), which loads them once per (files, limits) for validation
/// and every host; a pool holds managed engines only, so nothing leaks when it is dropped. A read looks in the memo (object reference and name), then in the service's cache (name, kind, site and the keys the value
/// read), and only then evaluates. The memo publishes the first finished result: two threads may evaluate one value at once and get equal
/// results, no thread waits on another, and a cancelled call publishes nothing.
/// </summary>
internal sealed class CalculationHost
{
    private static readonly AsyncLocal<CalculationHost?> Current = new();
    private static int s_building;

    [ThreadStatic]
    private static List<(RAnnotated Object, string Name)>? t_stack;

    private static readonly IReadOnlyDictionary<string, object?> NoParameters = FrozenDictionary<string, object?>.Empty;
    private static readonly global::Json.Schema.EvaluationOptions SchemaCheck = new()
    {
        OutputFormat = global::Json.Schema.OutputFormat.List, Culture = CultureInfo.InvariantCulture,
    };

    private readonly CalculationService _service;
    private readonly Lazy<Loaded> _loaded;
    private readonly ConcurrentDictionary<(RAnnotated Object, string Name), Result> _memo = new(MemoComparer.Instance);
    private ResolvedModel? _model;
    private DependencyHasher? _hasher;
    private IReadOnlyList<RAnnotated>? _objects;
    private long _ticket;

    internal CalculationHost(CalculationService service, ModelSnapshot snapshot)
    {
        _service = service;
        Snapshot = snapshot;
        _ticket = service.Cache.Current;
        _loaded = new Lazy<Loaded>(() => service.Load(snapshot), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The snapshot the model was resolved from.</summary>
    public ModelSnapshot Snapshot { get; }

    /// <summary>
    /// The host of the resolution running on this flow, read by every <see cref="RAnnotated"/> as it is built; <see langword="null"/>
    /// outside one, and at the cost of one static read while no resolution with calculation files runs.
    /// </summary>
    internal static CalculationHost? Building => Volatile.Read(ref s_building) == 0 ? null : Current.Value;

    /// <summary>Runs a resolution whose annotated objects are bound to the host (none when it is <see langword="null"/>).</summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="host">The host.</param>
    /// <param name="run">The resolution.</param>
    /// <returns>Its result.</returns>
    internal static T Build<T>(CalculationHost? host, Func<T> run)
    {
        if (host is null)
            return run();
        Interlocked.Increment(ref s_building);
        Current.Value = host;
        try
        {
            return run();
        }
        finally
        {
            Current.Value = null;
            Interlocked.Decrement(ref s_building);
        }
    }

    /// <summary>The dependency key of what a kind declares: <c>cv:&lt;kind&gt;</c>.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The key.</returns>
    public static string DeclarationKey(string kind) => "cv:" + kind;

    /// <summary>Binds the host to the resolved model it serves.</summary>
    /// <param name="model">The resolved model.</param>
    internal void Attach(ResolvedModel model) => _model = model;

    /// <summary>What the calculation files declare, by file and call order.</summary>
    public IReadOnlyList<CalculatedValueDeclaration> Declarations => _loaded.Value.Declarations;

    /// <summary>The names a kind declares, ordinal.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The names.</returns>
    public IReadOnlyList<string> NamesOf(string kind) => _loaded.Value.ByKind.TryGetValue(kind, out var declared) ? declared.Names : [];

    /// <summary>Whether a kind declares a name.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when declared.</returns>
    public bool Declares(string kind, string name) => _loaded.Value.ByKind.TryGetValue(kind, out var declared) && declared.ByName.ContainsKey(name);

    /// <summary>
    /// The hash of <c>cv:&lt;kind&gt;</c>: the kind's (name, declaring file) pairs in ordinal order with the declaring files' hashes, or
    /// <see cref="DependencyHasher.Absent"/> when the kind declares none.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The hash.</returns>
    public string DeclarationHash(string kind) =>
        _loaded.Value.ByKind.TryGetValue(kind, out var declared) ? declared.Hash : DependencyHasher.Absent;

    /// <summary>Reads a calculated value of an object: memo, cache, else evaluation; replays its keys into the reader's recorder.</summary>
    /// <param name="value">The object.</param>
    /// <param name="name">The name.</param>
    /// <param name="reads">The reader's recorder, or <see langword="null"/>.</param>
    /// <param name="ct">The reader's cancellation.</param>
    /// <returns>The value.</returns>
    /// <exception cref="CalculatedValueException">The value failed, or the kind does not declare the name (MQ5012).</exception>
    public object? Read(RAnnotated value, string name, IReadRecorder? reads, CancellationToken ct)
    {
        var loaded = _loaded.Value;
        if (!loaded.ByKind.TryGetValue(value.Kind, out var declared) || !declared.ByName.TryGetValue(name, out var declaration))
        {
            reads?.Record(DeclarationKey(value.Kind));
            var names = NamesOf(value.Kind);
            throw new CalculatedValueException(RuleCatalog.Create("MQ5012",
                $"The calculated value '{name}' is not declared for {value.Kind} ({(names.Count == 0 ? value.Kind + " declares none" : "it declares " + string.Join(", ", names))}).",
                value.Id, null));
        }

        var key = (value, name);
        if (!_memo.TryGetValue(key, out var result))
        {
            var stack = t_stack ??= [];
            var at = stack.FindIndex(f => ReferenceEquals(f.Object, value) && string.Equals(f.Name, name, StringComparison.Ordinal));
            if (at >= 0)
                throw new CalculatedValueException(Cycle(stack.GetRange(at, stack.Count - at), loaded));
            ct.ThrowIfCancellationRequested();
            stack.Add(key);
            try
            {
                result = Evaluate(value, declaration, loaded, ct);
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }

            result = _memo.GetOrAdd(key, result);
        }

        if (reads is not null)
        {
            foreach (var read in result.Keys)
                reads.Record(read);
            reads.Record(declaration.FileKey);
        }

        return result.Failure is { } failure ? throw new CalculatedValueException(failure) : result.Value;
    }

    /// <summary>
    /// Evaluates every calculated value on every object of its kinds (whole-model runs: validate, plan, generate), in parallel, and
    /// sweeps the cache entries the run did not use. Each failure is reported once, on the value and object that failed.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The failures, distinct, in (kind, site, name) order.</returns>
    public IReadOnlyList<Diagnostic> EvaluateAll(CancellationToken ct)
    {
        var ticket = _service.Cache.Begin();
        var failures = EvaluateAll(ticket, ct);
        _service.Cache.Sweep(ticket);
        return failures;
    }

    /// <summary>
    /// <see cref="EvaluateAll(CancellationToken)"/> under a cache ticket the caller took from <see cref="Cache"/> and sweeps with after
    /// its other work (the checks after resolution run the resolved rules under the same ticket).
    /// </summary>
    /// <param name="ticket">The run's ticket.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The failures, distinct, in (kind, site, name) order.</returns>
    internal IReadOnlyList<Diagnostic> EvaluateAll(long ticket, CancellationToken ct)
    {
        var loaded = _loaded.Value;
        Volatile.Write(ref _ticket, ticket);
        var work = new List<(RAnnotated Object, string Name)>();
        if (loaded.ByKind.Count > 0)
        {
            foreach (var value in ObjectsOf(loaded))
            {
                foreach (var name in loaded.ByKind[value.Kind].Names)
                    work.Add((value, name));
            }
        }

        var failures = new Diagnostic?[work.Count];
        Parallel.For(0, work.Count, new ParallelOptions { MaxDegreeOfParallelism = _service.Parallelism, CancellationToken = ct }, i =>
        {
            var (value, name) = work[i];
            if (_memo.TryGetValue(work[i], out var known))
            {
                // A value an earlier run on this model already has (a validate, then a plan): its cache entry is touched with this run's
                // ticket, so the sweep after the run keeps it for the next snapshot.
                if (known.Cached)
                    _service.Cache.Touch(name, value.Kind, known.Site, ticket);
                failures[i] = known.Failure;
                return;
            }

            try
            {
                Read(value, name, null, ct);
            }
            catch (CalculatedValueException e)
            {
                failures[i] = e.Diagnostic;
            }
        });
        // Each object's results are written to its own slot (the memo is keyed by object and name), so the order never depends on
        // which thread finished first.
        return [.. failures.OfType<Diagnostic>().Distinct()];
    }

    /// <summary>Every object of a declared kind, once, in (kind, site) order; walked once per resolved model, evaluating nothing.</summary>
    internal IReadOnlyList<RAnnotated> Objects => ObjectsOf(_loaded.Value);

    /// <summary>The kinds that declare a calculated value, ordinal.</summary>
    internal IReadOnlyList<string> DeclaredKinds => [.. _loaded.Value.ByKind.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Every object of a declared kind, once, in (kind, site) order; walked once per resolved model.</summary>
    private IReadOnlyList<RAnnotated> ObjectsOf(Loaded loaded)
    {
        var objects = Volatile.Read(ref _objects);
        if (objects is null)
        {
            objects = ResolvedKinds.ObjectsOf(Model, loaded.ByKind.Keys);
            Volatile.Write(ref _objects, objects);
        }

        return objects;
    }

    private ResolvedModel Model => _model ?? throw new InvalidOperationException("The calculation host is not attached to a resolved model.");

    /// <summary>The cache the host's values go to (its service's, shared with resolved rules).</summary>
    internal Validation.ResolvedResultCache Cache => _service.Cache;

    /// <summary>The hasher of the keys calculation calls and resolved rules record (no packs, no schema diffs).</summary>
    internal DependencyHasher Hasher
    {
        get
        {
            var hasher = Volatile.Read(ref _hasher);
            if (hasher is null)
            {
                // Calculation calls never record t: or d: keys, so no pack and no schema diff takes part.
                hasher = new DependencyHasher(Snapshot, Model, new PackSet([], []), FrozenDictionary<string, SchemaDiff.SchemaDiffResult>.Empty);
                Volatile.Write(ref _hasher, hasher);
            }

            return hasher;
        }
    }

    private Result Evaluate(RAnnotated value, Declared declaration, Loaded loaded, CancellationToken ct)
    {
        var site = ResolvedKinds.SiteOf(value, Snapshot);
        var script = loaded.Files[declaration.DeclaredIn];
        var limits = Snapshot.Settings.Limits;
        var cacheable = !loaded.Stateful.Contains(declaration.DeclaredIn);
        var ticket = Volatile.Read(ref _ticket);
        if (cacheable && _service.Cache.TryGet(declaration.Name, value.Kind, site, script, limits, Hasher, ticket, out var cached, out var cachedKeys))
            return new Result(cached, null, cachedKeys, site, true);

        var recorder = new Recorder();
        Diagnostic? failure = null;
        object? result = null;
        var readFailure = false;
        _service.CountEvaluation();
        using (var lease = loaded.Pool!.Rent())
        {
            try
            {
                var context = new ScriptCallContext(recorder, "calculated:" + declaration.Name + ":" + site, NoParameters, ct);
                result = lease.Sandbox.RunCalculation(declaration.Name, value, Model, context);
                failure = SchemaFailure(value, site, declaration, result);
            }
            catch (CalculatedValueException e)
            {
                failure = e.Diagnostic;
                readFailure = true;
            }
            catch (ScriptErrorException e)
            {
                failure = string.Equals(e.Diagnostic.Rule, "MQ5012", StringComparison.Ordinal) ? e.Diagnostic : Failed(value, site, declaration, e.Diagnostic);
            }
            catch (ScriptLimitException e)
            {
                failure = Failed(value, site, declaration, e.Diagnostic);
            }
        }

        var keys = recorder.Sorted();
        var stored = failure is null && cacheable && !readFailure;
        if (stored)
            _service.Cache.Store(declaration.Name, value.Kind, site, script, limits, keys, Hasher, result, ticket);
        return new Result(failure is null ? result : null, failure, keys, site, stored);
    }

    /// <summary>MQ5011 when the value does not fit the declared schema, at the declaring call, with the object's id.</summary>
    private static Diagnostic? SchemaFailure(RAnnotated value, string site, Declared declaration, object? result)
    {
        if (declaration.Schema is null)
            return null;
        var json = ScriptSandbox.CalculationJson(result);
        var evaluation = declaration.Schema.Evaluate(json, SchemaCheck);
        if (evaluation.IsValid)
            return null;
        var detail = (evaluation.Details ?? []).Where(d => d.Errors is { Count: > 0 }).Select(d => (d.InstanceLocation.ToString(), d.Errors!.OrderBy(e => e.Key, StringComparer.Ordinal).First()))
            .FirstOrDefault();
        var why = detail.Item2.Key is null ? "" : $": {(detail.Item1.Length == 0 ? "" : detail.Item1 + " ")}{detail.Item2.Key}: {detail.Item2.Value}";
        return RuleCatalog.Create("MQ5011",
            $"Calculated value '{declaration.Name}' of {Label(value, site)} does not fit its schema{why}. The value was {Shorten(json.GetRawText())}.",
            value.Id, declaration.DeclaredIn) with { Line = declaration.Line, Column = declaration.Column };
    }

    /// <summary>MQ5010: the function threw, broke a limit or returned a value that is not JSON; at the failing line, with the object's id.</summary>
    private static Diagnostic Failed(RAnnotated value, string site, Declared declaration, Diagnostic cause) =>
        RuleCatalog.Create("MQ5010", $"Calculated value '{declaration.Name}' failed on {Label(value, site)}: {cause.Message}", value.Id,
            cause.FilePath ?? declaration.DeclaredIn) with { Line = cause.Line, Column = cause.Column };

    /// <summary>
    /// MQ5013 for a cycle found on this thread's stack of reads: the chain starts at its least (kind, site, name) in ordinal order, so the
    /// text does not depend on which value was read first or which thread found it; at that value's declaring call.
    /// </summary>
    private Diagnostic Cycle(List<(RAnnotated Object, string Name)> chain, Loaded loaded)
    {
        var links = chain.Select(f => (f.Object, f.Name, Site: ResolvedKinds.SiteOf(f.Object, Snapshot))).ToList();
        var start = 0;
        for (var i = 1; i < links.Count; i++)
        {
            var c = string.CompareOrdinal(links[i].Object.Kind, links[start].Object.Kind);
            if (c == 0)
                c = string.CompareOrdinal(links[i].Site, links[start].Site);
            if (c == 0)
                c = string.CompareOrdinal(links[i].Name, links[start].Name);
            if (c < 0)
                start = i;
        }

        var ordered = links.Skip(start).Concat(links.Take(start)).ToList();
        var text = string.Join(" -> ", ordered.Append(ordered[0]).Select(l => $"{l.Name} of {Label(l.Object, l.Site)}"));
        var first = loaded.ByKind[ordered[0].Object.Kind].ByName[ordered[0].Name];
        return RuleCatalog.Create("MQ5013", $"Calculated values read each other in a cycle: {text}. Break the cycle in one of them.",
            ordered[0].Object.Id, first.DeclaredIn) with { Line = first.Line, Column = first.Column };
    }

    private static string Label(RAnnotated value, string site) =>
        value is RElement { Name.Length: > 0 } element ? $"{value.Kind} {element.Name} ({site})" : $"{value.Kind} {site}";

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..117] + "...";

    /// <summary>Loads the calculation files into one pool (<see cref="CalculationService.Load"/>, which keeps the result).</summary>
    /// <param name="service">The service.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The load.</returns>
    internal static Loaded Load(CalculationService service, ModelSnapshot snapshot)
    {
        var diagnostics = new List<Diagnostic>();
        var loadable = snapshot.CalculationScripts.ToList();
        IScriptSandboxPool? pool = null;
        while (loadable.Count > 0)
        {
            try
            {
                service.CountPool();
                pool = service.Scripts.CreatePool(loadable, snapshot.Settings.Limits, service.Parallelism, CancellationToken.None);
                break;
            }
            catch (Exception e) when (e is ScriptErrorException or ScriptLimitException)
            {
                // A file that does not load is reported on its own line and left out; the other files still declare their values.
                var (rule, located) = e is ScriptLimitException limit ? ("MQ5003", limit.Diagnostic) : ("MQ5002", ((ScriptErrorException)e).Diagnostic);
                var broken = loadable.FindIndex(s => string.Equals(s.Path, located.FilePath, StringComparison.Ordinal));
                diagnostics.Add(RuleCatalog.Create(rule, located.Message, null, broken >= 0 ? loadable[broken].Path : located.FilePath)
                    with { Line = located.Line, Column = located.Column });
                if (broken < 0)
                    break;
                loadable.RemoveAt(broken);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                diagnostics.Add(RuleCatalog.Create("MQ5002", "Loading the calculation files failed: " + e.Message, null,
                    snapshot.CalculationScripts.Count == 1 ? snapshot.CalculationScripts[0].Path : null));
                break;
            }
        }

        if (pool is not null)
            diagnostics.AddRange(pool.Warnings);
        var stateful = (pool?.Warnings ?? []).Where(w => w.Rule == "MQ5005" && w.FilePath is not null).Select(w => w.FilePath!).ToFrozenSet(StringComparer.Ordinal);
        var files = snapshot.CalculationScripts.ToFrozenDictionary(s => s.Path, StringComparer.Ordinal);
        var declarations = new List<CalculatedValueDeclaration>();
        var byKind = new Dictionary<string, Dictionary<string, Declared>>(StringComparer.Ordinal);
        foreach (var registration in pool?.Registrations ?? [])
        {
            if (registration is not { Kind: ScriptRegistrationKind.CalculatedValue, CalculatedValue: { } declaration })
                continue;
            declarations.Add(declaration);
            var schema = ScriptSandbox.CompileCalculationSchema(declaration.Schema).Schema;
            foreach (var kind in declaration.Kinds)
            {
                if (!byKind.TryGetValue(kind, out var names))
                    byKind[kind] = names = new Dictionary<string, Declared>(StringComparer.Ordinal);
                names.TryAdd(declaration.Name, new Declared(declaration.Name, registration.DeclaredIn, FileKey(registration.DeclaredIn), schema,
                    registration.Line, registration.Column));
            }
        }

        var kinds = byKind.ToFrozenDictionary(p => p.Key, p =>
        {
            var names = p.Value.Keys.Order(StringComparer.Ordinal).ToArray();
            var fields = new List<string?> { DeclarationKey(p.Key) };
            foreach (var name in names)
                fields.AddRange([name, p.Value[name].DeclaredIn, files.TryGetValue(p.Value[name].DeclaredIn, out var file) ? file.Hash : null]);
            return new KindDeclarations(names, p.Value.ToFrozenDictionary(StringComparer.Ordinal), HashBuilder.Of([.. fields]));
        }, StringComparer.Ordinal);
        return new Loaded(pool, kinds, stateful, files, [.. diagnostics], declarations);
    }

    /// <summary>The <c>x:</c> key of a calculation file: <c>x:calculated/&lt;name&gt;.js</c>.</summary>
    private static string FileKey(string path)
    {
        var at = path.LastIndexOf("extensions/calculated/", StringComparison.Ordinal);
        return SeedScripts.Key(at < 0 ? "calculated/" + path[(path.LastIndexOf('/') + 1)..] : path[(at + "extensions/".Length)..]);
    }

    internal sealed record Declared(string Name, string DeclaredIn, string FileKey, global::Json.Schema.JsonSchema? Schema, int? Line, int? Column);

    internal sealed record KindDeclarations(string[] Names, FrozenDictionary<string, Declared> ByName, string Hash);

    /// <summary>The calculation files loaded (<see cref="CalculationService.Load"/>): the pool, the declarations by kind, the files with
    /// top-level state, the files, the load diagnostics and the declarations in file and call order.</summary>
    internal sealed record Loaded(IScriptSandboxPool? Pool, FrozenDictionary<string, KindDeclarations> ByKind, FrozenSet<string> Stateful,
        FrozenDictionary<string, ScriptSource> Files, ImmutableArray<Diagnostic> Diagnostics, IReadOnlyList<CalculatedValueDeclaration> Declarations);

    /// <summary>A finished evaluation: the value or the failure, the keys the call read (ordinal), the object's site and whether the value
    /// is in the service's cache.</summary>
    private sealed record Result(object? Value, Diagnostic? Failure, string[] Keys, string Site, bool Cached);

    private sealed class Recorder : IReadRecorder
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public void Record(string dependencyKey) => _keys.Add(dependencyKey);

        public string[] Sorted() => [.. _keys.Order(StringComparer.Ordinal)];
    }

    private sealed class MemoComparer : IEqualityComparer<(RAnnotated Object, string Name)>
    {
        public static readonly MemoComparer Instance = new();

        public bool Equals((RAnnotated Object, string Name) x, (RAnnotated Object, string Name) y) =>
            ReferenceEquals(x.Object, y.Object) && string.Equals(x.Name, y.Name, StringComparison.Ordinal);

        public int GetHashCode((RAnnotated Object, string Name) obj) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Object), StringComparer.Ordinal.GetHashCode(obj.Name));
    }
}
