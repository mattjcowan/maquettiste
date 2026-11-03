using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Text;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Maquettiste.Engine.Rendering;

/// <summary>
/// The state of one <see cref="Renderer.RenderAsync"/> or <see cref="Renderer.RenderOneAsync"/> call: the context, the inflector over
/// the project's inflection settings, and one <see cref="PackRuntime"/> per pack (created on first use, disposed with the run).
/// </summary>
internal sealed class RenderRun : IDisposable
{
    private readonly Renderer _renderer;
    private readonly int _poolSize;
    private readonly ConcurrentDictionary<string, Lazy<PackRuntime>> _packs = new(StringComparer.Ordinal);
    private readonly Lazy<ExportTable> _ownTable;

    /// <summary>Creates the run state.</summary>
    /// <param name="renderer">The renderer.</param>
    /// <param name="context">The render context.</param>
    /// <param name="poolSize">Sandbox engines per pack (the worker count).</param>
    /// <param name="ct">The run token (also the sandbox pools' run token).</param>
    public RenderRun(Renderer renderer, RenderContext context, int poolSize, CancellationToken ct)
    {
        _renderer = renderer;
        _poolSize = poolSize;
        Context = context;
        Token = ct;
        Limits = context.Model.Settings?.Limits ?? new SandboxLimits();
        Inflector = new Inflector(context.Model.Settings?.Inflection);
        _ownTable = new Lazy<ExportTable>(() => ExportTable.Own(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The render context.</summary>
    public RenderContext Context { get; }

    /// <summary>The run token.</summary>
    public CancellationToken Token { get; }

    /// <summary>The sandbox and template limits.</summary>
    public SandboxLimits Limits { get; }

    /// <summary>The inflector (project <c>inflection</c> overrides applied; memoized for the run).</summary>
    public Inflector Inflector { get; }

    /// <summary>The template cache.</summary>
    public ITemplateCache Templates => _renderer.Templates;

    /// <summary>The reflection catalog.</summary>
    public TemplateMemberCatalog Catalog => _renderer.Catalog;

    /// <summary>The shared builtin object.</summary>
    public ScriptObject Builtins => _renderer.Builtins;

    /// <summary>The export table the run's units read: the context's, or one that evaluates each unit's own exports here.</summary>
    public ExportTable Exports => Context.Exports ?? _ownTable.Value;

    /// <summary>Evaluates a unit's export templates on the calling thread (<see cref="ExportTable.Evaluate"/>).</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The values, or the errors that stopped them.</returns>
    public UnitExportsResult EvaluateExports(PlannedUnit unit)
    {
        Token.ThrowIfCancellationRequested();
        var runtime = Pack(unit.Pack);
        using var lease = runtime.Errors.Count == 0 ? runtime.Pool?.Rent() : null;
        var work = new UnitRun(this, runtime, unit, lease, Token) { EvaluatingExports = true };
        return work.EvaluateExports();
    }

    /// <summary>The runtime of a pack (created once per run).</summary>
    /// <param name="pack">The pack.</param>
    /// <returns>The runtime.</returns>
    public PackRuntime Pack(LoadedPack pack) =>
        _packs.GetOrAdd(pack.Name, _ => new Lazy<PackRuntime>(() => PackRuntime.Create(pack, this, _poolSize))).Value;

    /// <summary>Renders one unit on the calling thread.</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The rendered unit.</returns>
    public RenderedUnit Render(PlannedUnit unit)
    {
        Token.ThrowIfCancellationRequested();
        var runtime = Pack(unit.Pack);
        using var lease = runtime.Errors.Count == 0 ? runtime.Pool?.Rent() : null;
        var work = new UnitRun(this, runtime, unit, lease, Token);
        return work.Render();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var pack in _packs.Values)
        {
            if (pack.IsValueCreated)
                pack.Value.Pool?.Dispose();
        }
    }
}

/// <summary>
/// A pack's per-run runtime: its sandbox pool (when it has scripts), the read-only object holding its JavaScript helpers, its
/// parameters as plain values, and the pack-level errors (a script that fails to load, a helper that collides with a builtin,
/// MQ6013) that fail every unit of the pack.
/// </summary>
internal sealed class PackRuntime
{
    private PackRuntime(LoadedPack pack, IScriptSandboxPool? pool, ScriptObject helpers, IReadOnlyList<Diagnostic> errors,
        IReadOnlyDictionary<string, object?> parameters, ScriptObject namespaces, IReadOnlyList<Diagnostic> scriptErrors,
        IReadOnlyList<string> helperNames)
    {
        Pack = pack;
        Pool = pool;
        Helpers = helpers;
        Errors = errors;
        Parameters = parameters;
        Namespaces = namespaces;
        ScriptErrors = scriptErrors;
        HelperNames = helperNames;
    }

    /// <summary>
    /// The helper namespaces the pack's units see (library packs, next-phase-2-design.md section 5.2): one read-only object per
    /// pack of its uses closure that has scripts, and its own when it is used and has scripts, keyed by the pack name with
    /// <c>-</c> replaced by <c>_</c>.
    /// </summary>
    public ScriptObject Namespaces { get; }

    /// <summary>The errors of loading the pack's own scripts (MQ6016, MQ6007), which fail every unit that sees the pack too.</summary>
    public IReadOnlyList<Diagnostic> ScriptErrors { get; }

    /// <summary>The helpers the pack's scripts registered, ordinal.</summary>
    public IReadOnlyList<string> HelperNames { get; }

    /// <summary>The helper namespace of a pack: its name with <c>-</c> replaced by <c>_</c> (pack names hold no <c>_</c>).</summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The namespace.</returns>
    public static string Namespace(string pack) => pack.Replace('-', '_');

    /// <summary>The pack.</summary>
    public LoadedPack Pack { get; }

    /// <summary>The sandbox pool, or <see langword="null"/> when the pack has no scripts.</summary>
    public IScriptSandboxPool? Pool { get; }

    /// <summary>The pack's JavaScript helpers (read-only).</summary>
    public ScriptObject Helpers { get; }

    /// <summary>Errors that fail every unit of the pack.</summary>
    public IReadOnlyList<Diagnostic> Errors { get; }

    /// <summary>The effective parameters as plain values, keys ordinal.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>The repo-relative path of the pack's <c>pack.json</c>.</summary>
    public string PackJsonPath => (Pack.RelativePath.TrimEnd('/').Length == 0 ? "" : Pack.RelativePath.TrimEnd('/') + "/") + "pack.json";

    /// <summary>Creates the runtime: loads the scripts into a pool and registers the helpers.</summary>
    /// <param name="pack">The pack.</param>
    /// <param name="run">The run.</param>
    /// <param name="poolSize">Engines in the pool.</param>
    /// <returns>The runtime.</returns>
    public static PackRuntime Create(LoadedPack pack, RenderRun run, int poolSize)
    {
        var parameters = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in pack.Parameters)
            parameters[name] = TemplateValues.Plain(value);
        var helpers = new ScriptObject(0, false);
        var errors = new List<Diagnostic>();
        var packJson = (pack.RelativePath.TrimEnd('/').Length == 0 ? "" : pack.RelativePath.TrimEnd('/') + "/") + "pack.json";

        // The used packs' runtimes first (uses is acyclic, so this recursion ends); a used pack whose scripts fail to load fails
        // every unit that sees it with that pack's own diagnostics.
        var used = new List<PackRuntime>();
        foreach (var name in pack.Sees)
        {
            if (run.Context.Packs.Find(name) is not { } usedPack)
            {
                errors.Add(new Diagnostic("MQ6042", DiagnosticSeverity.Error,
                    $"Pack '{pack.Name}' uses '{name}', which this run did not load.", null, packJson, null, null, null));
                continue;
            }

            var runtime = run.Pack(usedPack);
            errors.AddRange(runtime.ScriptErrors);
            used.Add(runtime);
        }

        var scriptErrors = new List<Diagnostic>();
        IScriptSandboxPool? pool = null;
        if (pack.Scripts.Count > 0)
        {
            IReadOnlyList<ScriptLibrary> libraries = [.. used.Where(u => u.Pool is not null).Select(u => new ScriptLibrary(u.Pack.Name, u.HelperNames))];
            try
            {
                pool = libraries.Count == 0 ? run.Context.Scripts.CreatePool(pack.Scripts, run.Limits, poolSize, run.Token)
                    : run.Context.Scripts.CreatePool(pack.Scripts, libraries, run.Limits, poolSize, run.Token);
            }
            catch (ScriptErrorException ex)
            {
                scriptErrors.Add(ex.Diagnostic);
            }
            catch (ScriptLimitException ex)
            {
                scriptErrors.Add(ex.Diagnostic);
            }
        }

        errors.AddRange(scriptErrors);
        var registered = pool is null ? [] : pool.Registrations.Where(r => r.Kind == ScriptRegistrationKind.Helper).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();

        var reserved = run.Builtins.GetMembers().ToHashSet(StringComparer.Ordinal);
        if (pool is not null)
        {
            foreach (var registration in registered)
            {
                if (reserved.Contains(registration.Name) || BuiltinHelpers.Variables.Contains(registration.Name))
                {
                    errors.Add(new Diagnostic("MQ6013", DiagnosticSeverity.Error,
                        $"The pack helper '{registration.Name}' collides with a builtin helper or template variable of the same name; rename it.",
                        null, registration.DeclaredIn, null, null, null));
                    continue;
                }

                var name = registration.Name;
                helpers.SetValue(name, new HelperFunction(name, 0, 63, (c, args) => c.Unit.CallHelper(c, name, args)), true);
            }
        }

        helpers.IsReadOnly = true;

        // Helper namespaces: each used pack with scripts, and the pack itself when another pack uses it, so its partials render in
        // its own units too. A namespace equal to a root helper of this pack, a builtin, a keyword or a variable is MQ6045.
        var namespaces = new ScriptObject(0, false);
        var sources = used.Where(u => u.Pool is not null).Select(u => (Runtime: u, Own: false)).ToList();
        if (pool is not null && pack.UsedBy.Any(u => !string.Equals(u, "maquettiste.json", StringComparison.Ordinal)))
            sources.Add((null!, true));
        foreach (var (runtime, own) in sources)
        {
            var target = own ? pack.Name : runtime.Pack.Name;
            var names = own ? (IReadOnlyList<string>)[.. registered.Select(r => r.Name)] : runtime.HelperNames;
            var space = Namespace(target);
            if (registered.FirstOrDefault(r => string.Equals(r.Name, space, StringComparison.Ordinal)) is { } clash)
            {
                errors.Add(new Diagnostic("MQ6045", DiagnosticSeverity.Error,
                    $"The helper namespace '{space}' of pack '{target}' collides with the helper '{space}' of pack '{pack.Name}'; rename the helper or the pack.",
                    null, clash.DeclaredIn, null, null, null));
                continue;
            }

            if (reserved.Contains(space) || BuiltinHelpers.Variables.Contains(space) || Planning.PackUses.Keywords.Contains(space))
            {
                errors.Add(new Diagnostic("MQ6045", DiagnosticSeverity.Error,
                    $"The helper namespace '{space}' of pack '{target}' collides with a builtin, a template keyword or a variable; rename the pack.",
                    null, packJson, null, null, null));
                continue;
            }

            var members = new ScriptObject(0, false);
            foreach (var helper in names)
            {
                var name = helper;
                members.SetValue(name, own
                    ? new HelperFunction(name, 0, 63, (c, args) => c.Unit.CallHelper(c, name, args))
                    : new HelperFunction(name, 0, 63, (c, args) => c.Unit.CallLibraryHelper(c, target, name, args)), true);
            }

            members.IsReadOnly = true;
            namespaces.SetValue(space, members, true);
        }

        namespaces.IsReadOnly = true;
        return new PackRuntime(pack, pool, helpers, errors, parameters, namespaces, scriptErrors, [.. registered.Select(r => r.Name)]);
    }
}

/// <summary>One unit being rendered: its recorder, lease, variables, templates used and outputs.</summary>
internal sealed class UnitRun : IScriptLibraryCalls
{
    private readonly PackRuntime _pack;
    private readonly IScriptSandboxLease? _lease;
    private Dictionary<string, IScriptSandboxLease>? _usedLeases;
    private readonly Dictionary<string, Template> _templatesUsed = new(StringComparer.Ordinal);
    private string? _mainTemplatePath;

    /// <summary>Creates the unit run.</summary>
    /// <param name="run">The run.</param>
    /// <param name="pack">The pack runtime.</param>
    /// <param name="planned">The planned unit.</param>
    /// <param name="lease">The sandbox lease, when the pack has scripts.</param>
    /// <param name="ct">The run token.</param>
    public UnitRun(RenderRun run, PackRuntime pack, PlannedUnit planned, IScriptSandboxLease? lease, CancellationToken ct)
    {
        Run = run;
        _pack = pack;
        Planned = planned;
        _lease = lease;
        CancellationToken = ct;
        ScriptContext = new ScriptCallContext(Recorder, planned.Key, pack.Parameters, ct) { Libraries = this };
    }

    /// <summary>The run.</summary>
    public RenderRun Run { get; }

    /// <summary>The planned unit.</summary>
    public PlannedUnit Planned { get; }

    /// <summary>The unit's read recorder.</summary>
    public UnitRecorder Recorder { get; } = new();

    /// <summary>Whether this run evaluates the unit's export templates rather than rendering it: <c>data</c>, <c>schema_diff</c> and
    /// <c>exports</c> are not set, and <c>exported</c> reads only the packs the unit's pack sees.</summary>
    public bool EvaluatingExports { get; init; }

    private UnitExports? _ownExports;

    /// <summary>The run token.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The script call context (recorder, seed = unit key, pack parameters).</summary>
    public ScriptCallContext ScriptContext { get; }

    /// <summary>The database of the unit's element (a table, view, column or database), for <c>sql_quote</c>'s default quoting.</summary>
    public RDatabase? ElementDatabase => Planned.Element switch
    {
        RTable table => table.Database,
        RView view => view.Database,
        RRoutine routine => routine.Database,
        RDatabaseType type => type.Database,
        RSqlObject obj => obj.Database,
        RQuery query => query.Database,
        RColumn column => column.Table?.Database,
        RDatabase database => database,
        _ => null,
    };

    /// <summary>The dependency key of a pack template or partial.</summary>
    /// <param name="path">The normalized pack-relative path.</param>
    /// <returns>The key.</returns>
    public string TemplateKey(string path) => "t:" + Planned.Pack.Name + "/" + path;

    /// <summary>The dependency key of a template or partial of a pack (its own, or a used pack's).</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="path">The normalized pack-relative path.</param>
    /// <returns>The key.</returns>
    public string TemplateKey(string pack, string path) => "t:" + pack + "/" + path;

    /// <summary>The unit's pack or a pack of its uses closure by name; <see langword="null"/> for any other name.</summary>
    /// <param name="name">The pack name.</param>
    /// <returns>The pack, or <see langword="null"/>.</returns>
    public LoadedPack? Seen(string name) =>
        string.Equals(name, Planned.Pack.Name, StringComparison.Ordinal) ? Planned.Pack
            : Planned.Pack.Sees.Contains(name, StringComparer.Ordinal) ? Run.Context.Packs.Find(name) : null;

    /// <summary>
    /// The pack whose folder holds a template source (the repo-relative path the template cache gives a file, the part before
    /// <c>#</c> for an inline expression): the unit's pack, or a pack of its closure; the unit's pack when none matches.
    /// </summary>
    /// <param name="source">The caller's source path.</param>
    /// <returns>The pack.</returns>
    public LoadedPack PackOfSource(string? source)
    {
        var path = source ?? "";
        var hash = path.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
            path = path[..hash];
        foreach (var name in Planned.Pack.Sees)
        {
            if (Run.Context.Packs.Find(name) is { } pack && path.StartsWith(pack.RelativePath.TrimEnd('/') + "/", StringComparison.Ordinal))
                return pack;
        }

        return Planned.Pack;
    }

    /// <summary>Calls a used pack's helper from a template (a namespace member) with the unit's recorder and seed.</summary>
    /// <param name="context">The unit's context.</param>
    /// <param name="pack">The used pack.</param>
    /// <param name="helper">The helper name.</param>
    /// <param name="args">The template arguments.</param>
    /// <returns>The result, wrapped for templates.</returns>
    public object? CallLibraryHelper(TrackingTemplateContext context, string pack, string helper, IReadOnlyList<object?> args)
    {
        var plain = args.Select(TrackingTemplateContext.Unwrap).ToArray();
        return context.Wrap(Call(pack, helper, plain, ScriptContext));
    }

    /// <summary>
    /// Calls a used pack's helper (a namespace member, or <c>maquettiste.use</c> in a script): leases a sandbox of that pack's own
    /// pool on first use in the unit, held to the end of the unit, and calls it with the caller's recorder and seed and the used
    /// pack's parameters.
    /// </summary>
    /// <inheritdoc/>
    public object? Call(string pack, string helper, IReadOnlyList<object?> args, ScriptCallContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (Seen(pack) is not { } used || string.Equals(pack, Planned.Pack.Name, StringComparison.Ordinal))
            throw new RenderHelperException("MQ6016", $"Pack '{pack}' is not in the uses of pack '{Planned.Pack.Name}'.");
        var runtime = Run.Pack(used);
        _usedLeases ??= new Dictionary<string, IScriptSandboxLease>(StringComparer.Ordinal);
        if (!_usedLeases.TryGetValue(pack, out var lease))
        {
            lease = (runtime.Pool ?? throw new RenderHelperException("MQ6016", $"The pack '{pack}' has no helper '{helper}'.")).Rent();
            _usedLeases[pack] = lease;
        }

        return lease.Sandbox.CallHelper(helper, args, caller with { Parameters = runtime.Parameters });
    }

    /// <summary>A repo-relative output path: the pack's <c>output</c> folder, then the rendered path.</summary>
    /// <param name="relative">The rendered path.</param>
    /// <returns>The output path.</returns>
    public string OutputPath(string relative)
    {
        var prefix = Planned.Pack.Settings.Output.Trim().TrimEnd('/');
        return prefix.Length == 0 ? relative : prefix + "/" + relative;
    }

    /// <summary>Calls a pack JavaScript helper with the unit's lease and recorder.</summary>
    /// <param name="context">The unit's context.</param>
    /// <param name="name">The helper name.</param>
    /// <param name="args">The template arguments.</param>
    /// <returns>The result, wrapped for templates.</returns>
    public object? CallHelper(TrackingTemplateContext context, string name, IReadOnlyList<object?> args)
    {
        var sandbox = _lease?.Sandbox ?? throw new RenderHelperException("MQ6016", $"The pack helper '{name}' has no sandbox.");
        var plain = args.Select(TrackingTemplateContext.Unwrap).ToArray();
        return context.Wrap(sandbox.CallHelper(name, plain, ScriptContext));
    }

    /// <summary>Renders the unit.</summary>
    /// <returns>The rendered unit.</returns>
    public RenderedUnit Render() => ReleasingUsedLeases(RenderCore);

    /// <summary>Runs a unit's render or export evaluation; the used packs' leases it took end with it (library packs).</summary>
    private T ReleasingUsedLeases<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        finally
        {
            if (_usedLeases is not null)
            {
                foreach (var lease in _usedLeases.Values)
                    lease.Dispose();
            }

            _usedLeases = null;
        }
    }

    private RenderedUnit RenderCore()
    {
        var diagnostics = new List<Diagnostic>();
        var files = new List<RenderedFile>();
        var failed = false;
        if (_pack.Errors.Count > 0)
        {
            failed = true;
            diagnostics.AddRange(_pack.Errors.Select(WithElement));
        }
        else
        {
            try
            {
                RenderFiles(files, diagnostics);
            }
            catch (Exception ex) when (CancellationToken.IsCancellationRequested || IsCancellation(ex))
            {
                // Scriban reports its cancellation as ScriptAbortException (not an OperationCanceledException), and any other
                // failure raised while the token is set is a consequence of the cancel: both surface as cancellation, never as MQ6006.
                throw new OperationCanceledException("Rendering was cancelled.", ex, CancellationToken);
            }
            catch (Exception ex)
            {
                failed = true;
                diagnostics.AddRange(Diagnose(ex));
            }
        }

        var keys = Recorder.SortedKeys();
        // A run limited by unit or element never advances schema snapshots, so a unit that read a schema diff fails and keeps its
        // outputs (MQ6033, next-phase-1-design.md section 5.6): a later full run then renders it from the snapshot it diffs from.
        if (!failed && Run.Context.Scoped && keys.Any(k => k.StartsWith("d:", StringComparison.Ordinal)))
        {
            failed = true;
            diagnostics.Add(RuleCatalog.Create("MQ6033",
                $"The unit '{Planned.Key}' reads the schema diff, so a run limited by unit or element does not render it: schema snapshots advance only in whole-pack runs. Generate without unit or element filters to render it.",
                Planned.Element?.Id, _mainTemplatePath ?? _pack.PackJsonPath));
        }

        var inputHash = Run.Context.Hasher.InputHash(Planned.StaticHash, keys);
        diagnostics.Sort(Diagnostic.Order);
        return new RenderedUnit(Planned, failed ? [] : files, keys, inputHash, diagnostics, failed)
        {
            KeyHashes = Planning.KeyHashes.Of(keys, Run.Context.Hasher.CurrentHash),
            Names = Generation.PlanExplainer.NamesOf(keys, Run.Context.Model.Find),
            Exports = failed ? null : _ownExports,
        };
    }

    /// <summary>
    /// Evaluates the unit's export templates (next-phase-2-design.md section 5.3): each value is rendered with the unit's variables,
    /// or once per item of an <c>each</c> member with the item as <c>item</c>, keyed by its id; values are trimmed, with LF line
    /// endings. The keys read go to this run's recorder only, so they are the export keys.
    /// </summary>
    /// <returns>The values, or the errors that stopped them.</returns>
    public UnitExportsResult EvaluateExports() => ReleasingUsedLeases(EvaluateExportsCore);

    private UnitExportsResult EvaluateExportsCore()
    {
        var diagnostics = new List<Diagnostic>();
        JsonElement? values = null;
        if (_pack.Errors.Count > 0)
        {
            diagnostics.AddRange(_pack.Errors.Select(WithElement));
        }
        else
        {
            try
            {
                values = ExportValues(diagnostics);
            }
            catch (Exception ex) when (CancellationToken.IsCancellationRequested || IsCancellation(ex))
            {
                throw new OperationCanceledException("Rendering was cancelled.", ex, CancellationToken);
            }
            catch (Exception ex)
            {
                diagnostics.AddRange(Diagnose(ex));
            }
        }

        diagnostics.Sort(Diagnostic.Order);
        if (values is null || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new UnitExportsResult(null, diagnostics);
        var keys = Recorder.SortedKeys();
        return new UnitExportsResult(new UnitExports(values.Value, keys, Run.Context.Hasher.InputHash(Planned.StaticHash, keys)), diagnostics);
    }

    private JsonElement? ExportValues(List<Diagnostic> diagnostics)
    {
        var context = new TrackingTemplateContext(Run.Builtins, this);
        if (_pack.Helpers.Count > 0)
            context.PushGlobal(_pack.Helpers);
        if (_pack.Namespaces.Count > 0)
            context.PushGlobal(_pack.Namespaces);
        context.PushGlobal(CreateGlobals(context));
        var index = UnitIndex().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var results = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, export) in Planned.Unit.Exports.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var pointer = "/units/" + index + "/exports/" + name + (export.Each is null ? "" : "/value");
            var template = Run.Templates.GetInline(export.Value, _pack.PackJsonPath, pointer);
            _templatesUsed[_pack.PackJsonPath + "#" + pointer] = template;
            if (export.Each is null)
            {
                results[name] = Renderer.NormalizeLineEndings(template.Render(context)).Trim();
                continue;
            }

            var items = EachItems(context, export.Each, "/units/" + index + "/exports/" + name + "/each", diagnostics);
            if (items is null)
                return null;
            var byId = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, item) in items)
            {
                var scope = new Scriban.Runtime.ScriptObject { ["item"] = item };
                context.PushGlobal(scope);
                try
                {
                    byId[id] = Renderer.NormalizeLineEndings(template.Render(context)).Trim();
                }
                finally
                {
                    context.PopGlobal();
                }
            }

            results[name] = byId;
        }

        return ExportTable.Write(w =>
        {
            foreach (var (name, value) in results)
            {
                if (value is string text)
                {
                    w.WriteString(name, text);
                    continue;
                }

                w.WriteStartObject(name);
                foreach (var (id, text2) in (SortedDictionary<string, string>)value)
                    w.WriteString(id, text2);
                w.WriteEndObject();
            }
        });
    }

    /// <summary>The items of an <c>each</c> member with their ids, or <see langword="null"/> after MQ6049.</summary>
    private List<(string Id, object? Item)>? EachItems(TrackingTemplateContext context, string member, string pointer, List<Diagnostic> diagnostics)
    {
        var element = Planned.Element;
        object? value = null;
        var found = false;
        if (element is not null)
        {
            Recorder.RecordObject(element);
            var accessor = context.GetMemberAccessor(element);
            found = accessor.TryGetValue(context, default, element, member, out value);
        }

        var wrapped = context.Wrap(value);
        var items = new List<(string, object?)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? problem = !found || wrapped is string || wrapped is not System.Collections.IEnumerable
            ? $"The export each '{member}' of unit '{Planned.Unit.Id}' names no list member of {(element is null ? "the model scope" : element.Kind)}."
            : null;
        if (problem is null)
        {
            foreach (var item in (System.Collections.IEnumerable)wrapped!)
            {
                var id = item switch
                {
                    IResolvedObject resolved => resolved.Id,
                    IReadOnlyDictionary<string, object?> map when map.TryGetValue("id", out var v) && v is string s => s,
                    _ => TrackingTemplateContext.Unwrap(item) is IResolvedObject inner ? inner.Id : null,
                };
                if (id is null)
                {
                    problem = $"The export each '{member}' of unit '{Planned.Unit.Id}' lists an item without a string id.";
                    break;
                }

                if (!ids.Add(id))
                {
                    problem = $"The export each '{member}' of unit '{Planned.Unit.Id}' lists two items with the id '{id}'.";
                    break;
                }

                items.Add((id, item));
            }
        }

        if (problem is null)
            return items;
        diagnostics.Add(new Diagnostic("MQ6049", DiagnosticSeverity.Error, problem, element?.Id, _pack.PackJsonPath, pointer, null, null));
        return null;
    }

    private void RenderFiles(List<RenderedFile> files, List<Diagnostic> diagnostics)
    {
        var unit = Planned.Unit;
        var context = new TrackingTemplateContext(Run.Builtins, this);
        if (_pack.Helpers.Count > 0)
            context.PushGlobal(_pack.Helpers);
        if (_pack.Namespaces.Count > 0)
            context.PushGlobal(_pack.Namespaces);
        context.PushGlobal(CreateGlobals(context));

        if (unit.Mode == OutputMode.Copy)
        {
            CopyFiles(context, files);
            return;
        }

        var main = Template(unit.Template ?? throw new RenderHelperException("MQ6006", $"The unit '{unit.Id}' names no template."));
        _mainTemplatePath = Run.Templates.Describe(main)?.RepoPath;
        var body = Renderer.NormalizeLineEndings(main.Render(context));
        if (unit.Output is not null)
        {
            files.Add(new RenderedFile(RenderPath(context, unit.Output, "output"), body, FileRole.Main));
        }
        else if (!string.IsNullOrWhiteSpace(body) && Severity("MQ6011", DiagnosticSeverity.Warning) is { } severity)
        {
            diagnostics.Add(new Diagnostic("MQ6011", severity,
                $"The unit '{unit.Id}' has no output, so the text its template writes outside file blocks is discarded.",
                Planned.Element?.Id, Run.Templates.Describe(main)?.RepoPath, null, null, null));
        }

        if (unit.Mode == OutputMode.Pair)
        {
            var companion = unit.Companion
                ?? throw new RenderHelperException("MQ6006", $"The unit '{unit.Id}' uses mode pair but names no companion.");
            var companionTemplate = Template(companion.Template);
            var text = Renderer.NormalizeLineEndings(companionTemplate.Render(context));
            files.Add(new RenderedFile(RenderPath(context, companion.Output, "companion/output"), text, FileRole.Companion));
        }

        files.AddRange(context.Blocks);
    }

    /// <summary>
    /// A copy unit (next-phase-1-design.md section 5.4). Each file its sources match comes from the shared matcher
    /// (<see cref="Planning.CopySources"/>), never from disk, and lands at the unit's output folder (rendered; the pack's output base
    /// when absent) plus its path after the literal base of the first pattern that matched it. Tokens are replaced in that relative
    /// path and in a text file's bytes in one left-to-right pass, longest token first; a file that is not text (not strict UTF-8, or
    /// holding a NUL) is copied byte for byte. Nothing else changes: no line-ending normalization, formatter or regions. The
    /// matcher's MQ6030 or MQ6031 fails the unit (only a preview gets here with one: a run's planner skips such a unit), and two
    /// sources on one path are MQ6005. Sources record no read keys: the static hash covers them.
    /// </summary>
    private void CopyFiles(TrackingTemplateContext context, List<RenderedFile> files)
    {
        var unit = Planned.Unit;
        _mainTemplatePath = _pack.PackJsonPath;
        var sources = Planning.CopySources.Of(Planned.Pack, unit);
        if (sources.Diagnostics.Count > 0)
            throw new TemplateException(sources.Diagnostics);

        var folder = unit.Output is null ? null : RenderPath(context, unit.Output, "output").TrimEnd('/');
        var tokens = new CopyTokens(unit.Tokens, Planned.Pack.Parameters);
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources.Files)
        {
            var relative = source.Path.StartsWith(source.Base, StringComparison.Ordinal) ? source.Path[source.Base.Length..] : source.Path;
            relative = tokens.Replace(relative);
            var path = folder is null ? OutputPath(relative) : folder.Length == 0 ? relative : folder + "/" + relative;
            if (!claimed.TryAdd(path, source.Path))
            {
                var pointer = "/units/" + UnitIndex().ToString(System.Globalization.CultureInfo.InvariantCulture) + "/sources";
                throw new TemplateException([RuleCatalog.Create("MQ6005",
                    $"Copy unit '{unit.Id}' copies '{claimed[path]}' and '{source.Path}' to the same path, {path}.", Planned.Element?.Id,
                    _pack.PackJsonPath, pointer)]);
            }

            if (Writing.DiffGenerator.IsText(source.Bytes))
            {
                var bytes = tokens.Replace(source.Bytes);
                files.Add(new RenderedFile(path, Encoding.UTF8.GetString(bytes), FileRole.Main) { Bytes = bytes });
            }
            else
            {
                files.Add(new RenderedFile(path, "", FileRole.Main) { Bytes = source.Bytes, Binary = true });
            }
        }
    }

    /// <summary>
    /// The severity of a non-failing diagnostic after <c>validation.rules</c> in <c>maquettiste.json</c>, or <see langword="null"/>
    /// when the rule is turned off. Diagnostics that fail the unit keep their catalog severity.
    /// </summary>
    private DiagnosticSeverity? Severity(string rule, DiagnosticSeverity fallback)
    {
        var rules = Run.Context.Model.Settings?.Validation.Rules;
        if (rules is null || !rules.TryGetValue(rule, out var value))
            return fallback;
        return value switch
        {
            "off" => null,
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            "info" => DiagnosticSeverity.Info,
            _ => fallback,
        };
    }

    private Template Template(string path)
    {
        if (PackPaths.TryNormalize(path, out var normalized, out _))
            Recorder.Record(TemplateKey(normalized));
        var template = Run.Templates.Get(Planned.Pack, path, Planned.Unit.Delimiters);
        var info = Run.Templates.Describe(template);
        if (info is not null)
            _templatesUsed[info.RepoPath] = template;
        return template;
    }

    private string RenderPath(TrackingTemplateContext context, string expression, string member)
    {
        var pointer = "/units/" + UnitIndex().ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + member;
        var template = Run.Templates.GetInline(expression, _pack.PackJsonPath, pointer);
        _templatesUsed[_pack.PackJsonPath + "#" + pointer] = template;
        var path = Renderer.NormalizeLineEndings(template.Render(context)).Trim();
        if (path.Length == 0)
            throw new RenderHelperException("MQ6006", $"The {member.Replace('/', ' ')} expression of unit '{Planned.Unit.Id}' rendered an empty path.");
        return OutputPath(path);
    }

    private int UnitIndex()
    {
        var units = Planned.Pack.Manifest.Units;
        for (var i = 0; i < units.Count; i++)
        {
            if (ReferenceEquals(units[i], Planned.Unit) || string.Equals(units[i].Id, Planned.Unit.Id, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    /// <summary>Records a template the context included, so its error positions can be mapped.</summary>
    /// <param name="template">The template.</param>
    public void Used(Template template)
    {
        if (Run.Templates.Describe(template) is { } info)
            _templatesUsed[info.RepoPath] = template;
    }

    private UnitGlobals CreateGlobals(TrackingTemplateContext context)
    {
        var element = Planned.Element;
        var model = Run.Context.Model;
        var globals = new UnitGlobals(() =>
        {
            if (element is not null)
                Recorder.RecordObject(element);
        }, () => Recorder.Record("s:project"));
        globals.SetValue("model", model, true);
        globals.SetValue("element", element, true);
        if (element is not null)
        {
            var alias = element.Kind.Replace('-', '_');
            if (!BuiltinHelpers.Variables.Contains(alias) || alias is "package" or "entity" or "relation" or "enum" or "value_object" or "table" or "view" or "sequence" or "routine" or "database_type" or "sql_object" or "query" or "reference_type" or "seed" or "locale"
                or "process" or "actor" or "scenario" or "operation" or "database" or "attribute" or "custom")
                globals.SetValue(alias, element, true);
        }

        var pack = Planned.Pack;
        globals.SetValue("pack", new PackView(pack.Name, pack.Manifest.Version,
            new MapView(_pack.Parameters, [.. _pack.Parameters], context)), true);
        globals.SetValue("unit", new UnitView(Planned.Unit.Id, Planned.Key), true);
        var settings = model.Settings;
        var properties = settings.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (object?)p.Value)).ToList();
        globals.SetValue("project", new ProjectView(settings.Name, new MapView(settings.Properties, properties, context)), true);

        var (mapping, mappings) = Mappings(context, element);
        globals.SetValue("mapping", mapping, true);
        globals.SetValue("mappings", mappings, true);
        globals.SetValue("hints", Hints(context, element), true);
        if (EvaluatingExports)
            return globals; // export templates see neither data, schema_diff nor exports (next-phase-2-design.md section 5.3)

        globals.SetValue("exports", OwnExports(context), true);
        var diffs = Run.Context.SchemaDiffs;
        // A preview's diffs are computed per database on first read (Generation.LazySchemaDiffs); a run's are all computed already.
        var diffEntries = diffs is Generation.LazySchemaDiffs lazy ? lazy.LazyEntries()
            : diffs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, (object?)p.Value)).ToList();
        globals.SetValue("schema_diff", new MapView(diffs, diffEntries, context, name =>
        {
            // The map's key set follows the project's databases: any read (a name lookup, a miss, an enumeration, a size or a key
            // listing) records the database kind set, so adding, removing or renaming a database re-renders the unit.
            Recorder.Record("k:database");
            var database = model.Databases.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
            if (database is null)
            {
                // A miss depends on every database's name: renaming one to this name must re-render the unit too.
                foreach (var other in model.Databases)
                    Recorder.RecordObject(other);
                return;
            }

            Recorder.RecordObject(database);
            Recorder.Record("d:" + database.Id);
        }, () => Recorder.Record("k:database")), true);

        globals.SetValue("data", Transforms(context), true);
        return globals;
    }

    /// <summary>The unit's own export values for the variable <c>exports</c> (null when it declares none); their keys join its reads.</summary>
    private object? OwnExports(TrackingTemplateContext context)
    {
        if (Planned.Unit.Exports.Count == 0)
            return null;
        var own = Run.Exports.Evaluate(Planned);
        if (own.Exports is null)
            throw new TemplateException(own.Diagnostics.Count > 0 ? own.Diagnostics
                : [RuleCatalog.Create("MQ6006", $"The exports of unit '{Planned.Unit.Id}' failed to evaluate.", Planned.Element?.Id, _pack.PackJsonPath)]);
        foreach (var key in own.Exports.ReadKeys)
            Recorder.Record(key);
        _ownExports = own.Exports;
        return context.Wrap(own.Exports.Values);
    }

    private (object? Mapping, MapView Mappings) Mappings(TrackingTemplateContext context, IResolvedObject? element)
    {
        IReadOnlyDictionary<string, object?> all = element switch
        {
            REntity entity => entity.Mappings.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal),
            RRelation relation => relation.Mappings.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal),
            _ => new Dictionary<string, object?>(StringComparer.Ordinal),
        };
        var entries = all.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        object? mapping = null;
        if (Planned.Unit.Where?.Database is { } database)
            all.TryGetValue(database, out mapping);
        else if (entries.Count == 1)
            mapping = entries[0].Value;
        return (mapping, new MapView(all, entries, context));
    }

    private static HintsView Hints(TrackingTemplateContext context, IResolvedObject? element)
    {
        GenerationHints? star = null, own = null;
        if (element is RAnnotated e)
        {
            e.Generation.TryGetValue("*", out star);
            e.Generation.TryGetValue(context.Unit.Planned.Pack.Name, out own);
        }

        var variables = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var hints in new[] { star, own })
        {
            if (hints is null)
                continue;
            foreach (var (name, value) in hints.Variables)
                variables[name] = TemplateValues.Plain(value);
        }

        return new HintsView(star?.Skip == true || own?.Skip == true, own?.Rename ?? star?.Rename, new MapView(variables, [.. variables], context));
    }

    private MapView Transforms(TrackingTemplateContext context)
    {
        var merged = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var transforms = Planned.Unit.Transforms;
        for (var i = 0; i < transforms.Count; i++)
        {
            var name = transforms[i];
            if (_lease is null)
            {
                throw new ScriptErrorException(new Diagnostic("MQ6016", DiagnosticSeverity.Error,
                    $"The unit '{Planned.Unit.Id}' names the transform '{name}' but the pack has no scripts.",
                    Planned.Element?.Id, _pack.PackJsonPath, "/units/" + UnitIndex().ToString(System.Globalization.CultureInfo.InvariantCulture) + "/transforms/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null));
            }

            var result = _lease.Sandbox.Transform(name, Planned.Element!, Run.Context.Model, ScriptContext);
            foreach (var (key, value) in result)
                merged[key] = value;
        }

        return new MapView(merged, [.. merged], context);
    }

    private static bool IsCancellation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException or ScriptAbortException)
                return true;
        }

        return false;
    }

    private Diagnostic WithElement(Diagnostic diagnostic) =>
        diagnostic.ElementId is null && Planned.Element is not null ? diagnostic with { ElementId = Planned.Element.Id } : diagnostic;

    /// <summary>Maps a render failure to diagnostics with the template file, line and column.</summary>
    private IReadOnlyList<Diagnostic> Diagnose(Exception ex)
    {
        Scriban.Syntax.ScriptRuntimeException? located = null;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case ScriptLimitException limit:
                    return [WithElement(limit.Diagnostic)];
                case ScriptErrorException script:
                    return [WithElement(script.Diagnostic)];
                case TemplateException template:
                    return [.. template.Diagnostics.Select(WithElement)];
                case ExportReadException read:
                    return [Located("MQ6050", read.Message, located), .. read.Record];
                case RenderHelperException helper:
                    return [Located(helper.Rule, helper.Message, located)];
                case System.Text.RegularExpressions.RegexMatchTimeoutException timeout:
                    return [Located("MQ6007", $"A regular expression ran longer than its {timeout.MatchTimeout.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms limit (pattern `{timeout.Pattern}`).", located)];
                case Scriban.Syntax.ScriptRuntimeException runtime when located is null:
                    located = runtime;
                    break;
            }
        }

        if (located is not null)
        {
            var message = located.OriginalMessage;
            return [Located(IsLimit(message) ? "MQ6007" : "MQ6006", message, located)];
        }

        return [WithElement(new Diagnostic("MQ6006", DiagnosticSeverity.Error, ex.Message, null,
            _mainTemplatePath, null, null, null))];
    }

    private Diagnostic Located(string rule, string message, Scriban.Syntax.ScriptRuntimeException? at)
    {
        var severity = RuleCatalog.TryGet(rule, out var info) ? info.DefaultSeverity : DiagnosticSeverity.Error;
        string? file = null;
        string? pointer = null;
        int? line = null, column = null;
        if (at is not null && at.Span.FileName is { } sourcePath)
        {
            if (_templatesUsed.TryGetValue(sourcePath, out var template) && Run.Templates.Describe(template) is { } info2)
            {
                file = info2.RepoPath;
                pointer = info2.JsonPointer;
                (line, column) = info2.Position(at.Span.Start.Line, at.Span.Start.Column);
            }
            else
            {
                var hash = sourcePath.IndexOf('#', StringComparison.Ordinal);
                file = hash >= 0 ? sourcePath[..hash] : sourcePath;
                pointer = hash >= 0 ? sourcePath[(hash + 1)..] : null;
                if (hash < 0 && at.Span.Start.Line >= 0)
                    (line, column) = (at.Span.Start.Line + 1, Math.Max(at.Span.Start.Column, 0) + 1);
            }
        }

        return WithElement(new Diagnostic(rule, severity, message, null, file, pointer, line, column));
    }

    private static bool IsLimit(string message) =>
        message.StartsWith("Exceeding ", StringComparison.Ordinal)
        || message.Contains("exceeds LoopLimit", StringComparison.Ordinal)
        || message.Contains("LimitToString", StringComparison.Ordinal)
        || message.Contains("OutputLimit", StringComparison.Ordinal);
}

/// <summary>
/// A copy unit's tokens (next-phase-1-design.md section 5.4): each literal text and the string value of the pack parameter it names,
/// as UTF-8 bytes, longest token first (ties ordinal). <see cref="Replace(byte[])"/> makes one left-to-right pass: at each position the
/// longest token that starts there is replaced by its value and the pass resumes after it, so a value is never scanned again; every
/// other byte stays.
/// </summary>
internal sealed class CopyTokens
{
    private readonly (byte[] Token, byte[] Value)[] _tokens;
    private readonly SearchValues<byte> _firsts;

    /// <summary>Builds the tokens of a unit.</summary>
    /// <param name="tokens">Literal text → parameter name.</param>
    /// <param name="parameters">The pack's effective parameters (the planner checked each named one is a string, MQ6031).</param>
    public CopyTokens(IReadOnlyDictionary<string, string> tokens, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        _tokens = [.. tokens
            .Where(t => t.Key.Length > 0)
            .Select(t => (Key: t.Key, Token: Encoding.UTF8.GetBytes(t.Key),
                Value: Encoding.UTF8.GetBytes(parameters.TryGetValue(t.Value, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : t.Key)))
            .OrderByDescending(t => t.Token.Length)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => (t.Token, t.Value))];
        _firsts = SearchValues.Create([.. _tokens.Select(t => t.Token[0]).Distinct()]);
    }

    /// <summary>Replaces the tokens in a relative path.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The path with each token replaced.</returns>
    public string Replace(string path)
    {
        if (_tokens.Length == 0)
            return path;
        var bytes = Encoding.UTF8.GetBytes(path);
        var replaced = Replace(bytes);
        return ReferenceEquals(bytes, replaced) ? path : Encoding.UTF8.GetString(replaced);
    }

    /// <summary>Replaces the tokens in bytes.</summary>
    /// <param name="input">The bytes (not changed).</param>
    /// <returns>New bytes, or <paramref name="input"/> itself when no token occurs in it.</returns>
    public byte[] Replace(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_tokens.Length == 0)
            return input;
        ReadOnlySpan<byte> span = input;
        ArrayBufferWriter<byte>? output = null;
        var start = 0; // the first byte not yet written
        var at = 0; // where the search resumes
        while (at < span.Length)
        {
            var offset = span[at..].IndexOfAny(_firsts);
            if (offset < 0)
                break;
            var i = at + offset;
            var hit = -1;
            for (var k = 0; k < _tokens.Length; k++)
            {
                if (span[i..].StartsWith(_tokens[k].Token))
                {
                    hit = k;
                    break;
                }
            }

            if (hit < 0)
            {
                at = i + 1;
                continue;
            }

            output ??= new ArrayBufferWriter<byte>(input.Length + 64);
            output.Write(span[start..i]);
            output.Write(_tokens[hit].Value);
            at = start = i + _tokens[hit].Token.Length;
        }

        if (output is null)
            return input;
        output.Write(span[start..]);
        return output.WrittenSpan.ToArray();
    }
}
