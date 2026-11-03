using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Rendering;

/// <summary>One pack's merged export record for one element, or for model scope (next-phase-2-design.md section 5.3).</summary>
/// <param name="Values">The merged values (a JSON object, keys ordinal), or <see langword="null"/> when no unit exports for the element
/// or the record failed.</param>
/// <param name="Units">The keys of the units whose exports it merges, ordinal.</param>
/// <param name="Diagnostics">Why it failed: the exporting units' evaluation errors, or MQ6048; empty when it did not.</param>
/// <param name="FailedUnit">The first exporting unit that failed, or <see langword="null"/>.</param>
internal sealed record ExportRecord(JsonElement? Values, IReadOnlyList<string> Units, IReadOnlyList<Diagnostic> Diagnostics, string? FailedUnit)
{
    /// <summary>Whether the record failed to evaluate.</summary>
    public bool Failed => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>One unit's evaluated exports, or the errors that stopped them.</summary>
/// <param name="Exports">The values, keys and input hash; <see langword="null"/> when the evaluation failed.</param>
/// <param name="Diagnostics">The evaluation's diagnostics.</param>
internal sealed record UnitExportsResult(UnitExports? Exports, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>A read of exports that failed to evaluate (MQ6050), with the failed record's own diagnostics.</summary>
/// <param name="message">The MQ6050 message.</param>
/// <param name="record">The record's diagnostics.</param>
internal sealed class ExportReadException(string message, IReadOnlyList<Diagnostic> record) : Exception(message)
{
    /// <summary>The failed record's diagnostics, reported beside MQ6050.</summary>
    public IReadOnlyList<Diagnostic> Record { get; } = record;
}

/// <summary>
/// The export records of one run, preview session or apply (next-phase-2-design.md section 5.3). Records are computed from the model
/// on demand, never read back from rendered files: <see cref="Record"/> merges the exports of the units of a pack that plan an
/// element, <see cref="Unit"/> gives one unit's values to readers (its stored state's while their inputs are unchanged), and
/// <see cref="Evaluate"/> renders a unit's export templates. Each is computed once, whatever the number of readers or threads.
/// The table is created before the hasher it serves <c>px:</c> keys to, and bound to the run's render context once that exists.
/// </summary>
internal sealed class ExportTable : IDisposable
{
    private readonly Func<string, string?, IReadOnlyList<PlannedUnit>> _unitsFor;
    private readonly IReadOnlyDictionary<string, PlannedUnit> _byKey;
    private readonly ConcurrentDictionary<string, bool> _unseeded = new(StringComparer.Ordinal);
    private readonly Func<string, string, UnitState?>? _stored;
    private readonly IReadOnlyDictionary<string, UnitExports>? _seeded;
    private readonly Renderer? _renderer;
    private readonly RenderRun? _borrowed;
    private readonly int _poolSize;
    private readonly CancellationToken _ct;
    private readonly Lock _gate = new();
    private RenderRun? _run;
    private bool _disposed;
    private RenderContext? _context;
    private readonly ConcurrentDictionary<(string Pack, string? Id), Lazy<ExportRecord>> _records = new();
    private readonly ConcurrentDictionary<string, Lazy<UnitExportsResult>> _units = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<UnitExportsResult>> _evaluated = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UnitExports> _served = new(StringComparer.Ordinal);
    private int _evaluations;

    private ExportTable(Func<string, string?, IReadOnlyList<PlannedUnit>> unitsFor, Func<string, string, UnitState?>? stored,
        IReadOnlyDictionary<string, UnitExports>? seeded, Renderer? renderer, RenderRun? borrowed, int poolSize, CancellationToken ct,
        IReadOnlyDictionary<string, PlannedUnit>? byKey = null)
    {
        _unitsFor = unitsFor;
        _byKey = byKey ?? new Dictionary<string, PlannedUnit>(StringComparer.Ordinal);
        _stored = stored;
        _seeded = seeded;
        _renderer = renderer;
        _borrowed = borrowed;
        _poolSize = Math.Max(1, poolSize);
        _ct = ct;
    }

    /// <summary>
    /// A run's table over its unfiltered plan: the planned units and <see cref="UnitPlan.ExportOnly"/>, indexed once by pack and
    /// element; stored states serve while <see cref="ReuseStates"/> is set and their export inputs are unchanged.
    /// </summary>
    /// <param name="units">The planned units (those without exports are ignored).</param>
    /// <param name="renderer">The renderer whose cache and builtins evaluate export templates.</param>
    /// <param name="poolSize">The sandbox engines per pack of the evaluator (the run's parallelism).</param>
    /// <param name="stored">A unit's stored state by pack and key, or <see langword="null"/> for none.</param>
    /// <param name="ct">The run token.</param>
    /// <returns>The table.</returns>
    public static ExportTable ForPlan(IEnumerable<PlannedUnit> units, Renderer? renderer, int poolSize, Func<string, string, UnitState?>? stored,
        CancellationToken ct)
    {
        var index = Index(units);
        return new ExportTable((pack, id) => index.TryGetValue((pack, id), out var list) ? list : [], stored, null, renderer, null, poolSize, ct,
            ByKey(index))
        {
            ReuseStates = stored is not null,
        };
    }

    /// <summary>
    /// An apply's table: the units come from the plan the apply prepares again, the values from the stored plan; it never evaluates,
    /// and a record whose units the plan did not seed sets <see cref="Unseeded"/>.
    /// </summary>
    /// <param name="units">The units of the prepared plan, export-only ones included.</param>
    /// <param name="seeded">The stored values by unit key.</param>
    /// <returns>The table.</returns>
    public static ExportTable ForApply(IEnumerable<PlannedUnit> units, IReadOnlyDictionary<string, UnitExports> seeded)
    {
        var index = Index(units);
        return new ExportTable((pack, id) => index.TryGetValue((pack, id), out var list) ? list : [], null, seeded, null, null, 1, default, ByKey(index));
    }

    /// <summary>
    /// A preview session's table: a record's units are the pack's (run or used) units with exports whose scope, filter and hints plan
    /// the element (<see cref="UnitPlanner.WhyNot"/>), found when the record is first read; no stored states.
    /// </summary>
    /// <param name="model">The resolved model.</param>
    /// <param name="packs">The session's packs.</param>
    /// <param name="scripts">The sandbox factory.</param>
    /// <param name="renderer">The renderer.</param>
    /// <param name="ct">The session token.</param>
    /// <returns>The table.</returns>
    public static ExportTable ForPreview(ResolvedModel model, PackSet packs, IScriptSandboxFactory scripts, Renderer? renderer, CancellationToken ct) =>
        new((pack, id) => Scope(model, packs, scripts, pack, id, ct), null, null, renderer, null, 1, ct);

    /// <summary>A table that holds no records and evaluates a unit's own exports in the run that renders it (a render context built
    /// without one).</summary>
    /// <param name="run">The render run.</param>
    /// <returns>The table.</returns>
    public static ExportTable Own(RenderRun run) => new((_, _) => [], null, null, null, run, 1, run.Token);

    /// <summary>Whether stored states may serve (off for forced and check runs).</summary>
    public bool ReuseStates { get; set; }

    /// <summary>Apply only: the units a record needed whose values the plan did not record, so the plan is stale; ordinal.</summary>
    public IReadOnlyList<string> Unseeded => [.. _unseeded.Keys.Order(StringComparer.Ordinal)];

    /// <summary>An exporting unit of the unfiltered plan by key, or <see langword="null"/>.</summary>
    /// <param name="key">The unit key.</param>
    /// <returns>The unit.</returns>
    public PlannedUnit? Find(string key) => _byKey.TryGetValue(key, out var unit) ? unit : null;

    /// <summary>How many units this table evaluated (tests).</summary>
    public int Evaluations => Volatile.Read(ref _evaluations);

    /// <summary>The values of every unit this table served or evaluated, by unit key.</summary>
    public IReadOnlyDictionary<string, UnitExports> Served => _served;

    /// <summary>Binds the render context whose model, packs, hasher and limits evaluate the export templates.</summary>
    /// <param name="context">The context; its <see cref="RenderContext.Exports"/> is this table.</param>
    public void Bind(RenderContext context) => _context = context;

    /// <summary>The <c>px:</c> key hash: <c>H("mq-exports-1", canonical record)</c>, absent without a record, or the hash of a failed
    /// record's sorted messages.</summary>
    /// <param name="rest">The key after <c>px:</c>: <c>&lt;pack&gt;/&lt;element id&gt;</c> or <c>&lt;pack&gt;</c>.</param>
    /// <returns>The hash.</returns>
    public string Hash(string rest)
    {
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var record = slash < 0 ? Record(rest, null) : Record(rest[..slash], rest[(slash + 1)..]);
        if (record.Failed)
            return HashBuilder.Of(["mq-exports-failed-1", .. record.Diagnostics.Select(d => d.Message).Order(StringComparer.Ordinal)]);
        return record.Values is { } values ? HashBuilder.Of("mq-exports-1", CanonicalForm.Json(values)) : DependencyHasher.Absent;
    }

    /// <summary>The merged record of a pack for an element.</summary>
    /// <param name="pack">The pack name.</param>
    /// <param name="elementId">The element id, or <see langword="null"/> for model scope.</param>
    /// <returns>The record.</returns>
    public ExportRecord Record(string pack, string? elementId) =>
        _records.GetOrAdd((pack, elementId), k => new Lazy<ExportRecord>(() => Merge(k.Pack, k.Id))).Value;

    /// <summary>One unit's values for readers: its stored state's while their inputs are unchanged, otherwise evaluated.</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The result.</returns>
    public UnitExportsResult Unit(PlannedUnit unit) =>
        _units.GetOrAdd(unit.Key, _ => new Lazy<UnitExportsResult>(() =>
        {
            UnitExportsResult result;
            if (_seeded is not null)
            {
                if (_seeded.TryGetValue(unit.Key, out var seeded))
                {
                    result = new UnitExportsResult(seeded, []);
                }
                else
                {
                    _unseeded[unit.Key] = true;
                    result = new UnitExportsResult(null, [RuleCatalog.Create("MQ6050",
                        $"The plan does not record the exports of '{unit.Key}'; plan again.", unit.Element?.Id, PackJson(unit.Pack))]);
                }
            }
            else if (ReuseStates && _context is { } context && _stored?.Invoke(unit.Pack.Name, unit.Key)?.Exports is { } stored
                && string.Equals(context.Hasher.InputHash(unit.StaticHash, stored.ReadKeys), stored.InputHash, StringComparison.Ordinal))
            {
                result = new UnitExportsResult(stored, []);
            }
            else
            {
                result = Evaluate(unit);
            }

            if (result.Exports is { } exports)
                _served[unit.Key] = exports;
            return result;
        })).Value;

    /// <summary>Renders a unit's export templates (once per table); the unit's own render takes its values from here.</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The result.</returns>
    public UnitExportsResult Evaluate(PlannedUnit unit) =>
        _evaluated.GetOrAdd(unit.Key, _ => new Lazy<UnitExportsResult>(() =>
        {
            if (_seeded is not null)
                throw new InvalidOperationException("An apply never evaluates exports.");
            Interlocked.Increment(ref _evaluations);
            var result = Runner().EvaluateExports(unit);
            if (result.Exports is { } exports)
                _served[unit.Key] = exports;
            return result;
        })).Value;

    /// <summary>Disposes the evaluator this table created (its script pools); a table that borrows a run leaves it to its owner.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _run?.Dispose();
            _run = null;
        }
    }

    private RenderRun Runner()
    {
        if (_borrowed is not null)
            return _borrowed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _run ??= new RenderRun(
                _renderer ?? throw new InvalidOperationException("The export table has no renderer."),
                _context ?? throw new InvalidOperationException("The export table is not bound to a render context."), _poolSize, _ct);
        }
    }

    private ExportRecord Merge(string pack, string? elementId)
    {
        var units = _unitsFor(pack, elementId).Where(u => u.Unit.Exports.Count > 0).OrderBy(u => u.Key, StringComparer.Ordinal).ToList();
        if (units.Count == 0)
            return new ExportRecord(null, [], [], null);
        var merged = new SortedDictionary<string, (JsonElement Value, PlannedUnit From)>(StringComparer.Ordinal);
        var diagnostics = new List<Diagnostic>();
        string? failed = null;
        foreach (var unit in units)
        {
            var result = Unit(unit);
            if (result.Exports is null)
            {
                failed ??= unit.Key;
                diagnostics.AddRange(result.Diagnostics);
                continue;
            }

            foreach (var property in result.Exports.Values.EnumerateObject())
            {
                if (!merged.TryGetValue(property.Name, out var earlier))
                {
                    merged[property.Name] = (property.Value.Clone(), unit);
                }
                else if (!JsonElement.DeepEquals(earlier.Value, property.Value))
                {
                    failed ??= unit.Key;
                    diagnostics.Add(new Diagnostic("MQ6048", DiagnosticSeverity.Error,
                        $"The units '{earlier.From.Key}' and '{unit.Key}' export '{property.Name}' for one element with different values.",
                        elementId, PackJson(unit.Pack), "/units/" + IndexOf(unit).ToString(CultureInfo.InvariantCulture) + "/exports/" + property.Name,
                        null, null));
                }
            }
        }

        var keys = units.Select(u => u.Key).ToList();
        if (failed is not null)
        {
            diagnostics.Sort(Diagnostic.Order);
            return new ExportRecord(null, keys, diagnostics, failed);
        }

        return new ExportRecord(Write(w =>
        {
            foreach (var (name, (value, _)) in merged)
            {
                w.WritePropertyName(name);
                value.WriteTo(w);
            }
        }), keys, [], null);
    }

    /// <summary>Writes a JSON object and returns it as a detached element.</summary>
    /// <param name="body">Writes the object's members.</param>
    /// <returns>The element.</returns>
    internal static JsonElement Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>The repo-relative <c>pack.json</c> path of a pack.</summary>
    /// <param name="pack">The pack.</param>
    /// <returns>The path.</returns>
    internal static string PackJson(LoadedPack pack) => (pack.RelativePath.TrimEnd('/').Length == 0 ? "" : pack.RelativePath.TrimEnd('/') + "/") + "pack.json";

    /// <summary>The index of a planned unit's definition in its pack's units.</summary>
    /// <param name="unit">The unit.</param>
    /// <returns>The index (0 when not found).</returns>
    internal static int IndexOf(PlannedUnit unit)
    {
        var units = unit.Pack.Manifest.Units;
        for (var i = 0; i < units.Count; i++)
        {
            if (ReferenceEquals(units[i], unit.Unit) || string.Equals(units[i].Id, unit.Unit.Id, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }

    private static Dictionary<(string, string?), List<PlannedUnit>> Index(IEnumerable<PlannedUnit> units)
    {
        var index = new Dictionary<(string, string?), List<PlannedUnit>>();
        foreach (var unit in units)
        {
            if (unit.Unit.Exports.Count == 0)
                continue;
            var key = (unit.Pack.Name, unit.Element?.Id);
            if (!index.TryGetValue(key, out var list))
                index[key] = list = [];
            list.Add(unit);
        }

        return index;
    }

    private static Dictionary<string, PlannedUnit> ByKey(Dictionary<(string, string?), List<PlannedUnit>> index)
    {
        var byKey = new Dictionary<string, PlannedUnit>(StringComparer.Ordinal);
        foreach (var list in index.Values)
        {
            foreach (var unit in list)
                byKey.TryAdd(unit.Key, unit);
        }

        return byKey;
    }

    private static IReadOnlyList<PlannedUnit> Scope(ResolvedModel model, PackSet packs, IScriptSandboxFactory scripts, string name, string? id,
        CancellationToken ct)
    {
        if (packs.Find(name) is not { } pack)
            return [];
        var element = id is null ? null : model.Find(id);
        if (id is not null && element is null)
            return [];
        var units = new List<PlannedUnit>();
        foreach (var unit in pack.Manifest.Units)
        {
            if (unit.Exports.Count == 0)
                continue;
            if (element is null ? !string.Equals(unit.For.Trim(), "model", StringComparison.Ordinal)
                : UnitPlanner.WhyNot(model, pack, unit, element, scripts, ct, packs) is not null)
                continue;
            var key = UnitPlanner.KeyOf(pack.Name, unit.Id, element?.Id);
            units.Add(new PlannedUnit(key, pack, unit, element, UnitPlanner.StaticHash(pack, unit, model.Settings.Formatters, key, packs)));
        }

        return units;
    }
}
