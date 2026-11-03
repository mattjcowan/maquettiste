using System.Collections.Frozen;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// The filters of a scoped run (next-phase-1-design.md section 5.6): <see cref="GenerationRequest.Units"/> and
/// <see cref="GenerationRequest.Elements"/>. Values of one filter OR, the two filters AND. A unit value is <c>&lt;pack&gt;/&lt;unit&gt;</c>
/// or a bare unit id that exactly one run pack has; an element value is an element id (a synthesized table's id is its key,
/// <c>&lt;entityId&gt;@&lt;dbId&gt;</c>), and model-scope units, which have no element, never match one.
/// </summary>
/// <remarks>
/// <see cref="Of"/> takes the request's values; <see cref="Resolve"/> binds the unit values to the run's packs (MQ6032) and returns
/// the selection the run matches with, immutable from then on, so the writer's tasks may share it. A run matches planned units by
/// pack, unit id and element, unit keys (<c>&lt;pack&gt;/&lt;unit&gt;[:&lt;element&gt;]</c>, unit states) and manifest unit names
/// (<c>&lt;unit&gt;[:&lt;element&gt;]</c> under a pack, companion mark removed) the same way.
/// </remarks>
internal sealed class UnitSelection
{
    private const string UnknownRule = "MQ6032";
    private const string NothingRule = "MQ6034";

    private readonly IReadOnlyList<string>? _units;
    private readonly FrozenSet<string>? _elements;

    /// <summary>The <c>&lt;pack&gt;/&lt;unit&gt;</c> pairs the unit values name, once resolved; <see langword="null"/> before, or with no unit filter.</summary>
    private readonly FrozenSet<string>? _pairs;

    private UnitSelection(IReadOnlyList<string>? units, FrozenSet<string>? elements, FrozenSet<string>? pairs, bool resolved)
    {
        _units = units;
        _elements = elements;
        _pairs = pairs;
        IsResolved = resolved;
    }

    /// <summary>Whether <see cref="Resolve"/> made this selection, so it can match.</summary>
    public bool IsResolved { get; }

    /// <summary>
    /// The selection of a request, or <see langword="null"/> when it sets neither filter (an empty list sets none, as
    /// <see langword="null"/> does): the run is not scoped.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The unresolved selection, or <see langword="null"/>.</returns>
    public static UnitSelection? Of(GenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var units = request.Units is { Count: > 0 } u ? u.Distinct(StringComparer.Ordinal).ToList() : null;
        var elements = request.Elements is { Count: > 0 } e ? e.ToFrozenSet(StringComparer.Ordinal) : null;
        return units is null && elements is null ? null : new UnitSelection(units, elements, null, false);
    }

    /// <summary>Whether a request sets a unit or element filter (<see cref="Of"/> returns a selection).</summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> for a scoped run.</returns>
    public static bool IsScoped(GenerationRequest request) => request.Units is { Count: > 0 } || request.Elements is { Count: > 0 };

    /// <summary>
    /// The packs a scoped run loads: when every unit value names its pack, those packs (intersected with the requested ones), so the
    /// others are not loaded; else the requested packs. A pack only a unit value names is left out when its folder holds no
    /// <c>pack.json</c>, so the value is MQ6032 (a pack the run lacks) rather than the loader's missing pack.
    /// </summary>
    /// <param name="requested">The request's packs, or <see langword="null"/> for every enabled pack.</param>
    /// <param name="templatesFolder">The absolute <c>templates</c> folder.</param>
    /// <returns>The pack names to load, or <see langword="null"/> for every enabled pack.</returns>
    public IReadOnlyList<string>? PackNames(IReadOnlyList<string>? requested, string templatesFolder)
    {
        if (_units is null || _units.Any(u => !u.Contains('/', StringComparison.Ordinal)))
            return requested;
        var named = _units.Select(u => u[..u.IndexOf('/', StringComparison.Ordinal)]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        if (requested is not null)
            return [.. named.Where(n => requested.Contains(n, StringComparer.Ordinal))];
        return [.. named.Where(n => n.Length > 0 && !n.StartsWith('.') && n.IndexOfAny(['/', '\\']) < 0
            && File.Exists(Path.Combine(templatesFolder, n, "pack.json")))];
    }

    /// <summary>
    /// Binds the unit values to the run's packs. A value naming a pack the run lacks, a unit no run pack has, or a bare unit id
    /// several run packs have is MQ6032 (in <paramref name="diagnostics"/>; it matches nothing).
    /// </summary>
    /// <param name="packs">The run's packs.</param>
    /// <param name="diagnostics">Receives the MQ6032 errors.</param>
    /// <returns>The resolved selection.</returns>
    public UnitSelection Resolve(PackSet packs, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(packs);
        if (_units is null)
            return new UnitSelection(null, _elements, null, true);
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        var byName = packs.Packs.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var value in _units)
        {
            var slash = value.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                var pack = value[..slash];
                var unit = value[(slash + 1)..];
                if (!byName.TryGetValue(pack, out var loaded))
                    diagnostics.Add(RuleCatalog.Create(UnknownRule, $"The unit filter '{value}' names pack '{pack}', which this run does not have (it does not exist, is disabled or is not among the run's packs)."));
                else if (!loaded.Manifest.Units.Any(u => string.Equals(u.Id, unit, StringComparison.Ordinal)))
                    diagnostics.Add(RuleCatalog.Create(UnknownRule, $"The unit filter '{value}' names unit '{unit}', which pack '{pack}' does not have."));
                else
                    pairs.Add(pack + "/" + unit);
                continue;
            }

            var owners = packs.Packs.Where(p => p.Manifest.Units.Any(u => string.Equals(u.Id, value, StringComparison.Ordinal)))
                .Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
            if (owners.Count == 0)
                diagnostics.Add(RuleCatalog.Create(UnknownRule, $"The unit filter '{value}' names a unit that no pack of this run has."));
            else if (owners.Count > 1)
                diagnostics.Add(RuleCatalog.Create(UnknownRule, $"The unit filter '{value}' is ambiguous: packs {string.Join(", ", owners.Select(o => "'" + o + "'"))} all have a unit '{value}'; write {string.Join(" or ", owners.Select(o => "'" + o + "/" + value + "'"))}."));
            else
                pairs.Add(owners[0] + "/" + value);
        }

        return new UnitSelection(_units, _elements, pairs.ToFrozenSet(StringComparer.Ordinal), true);
    }

    /// <summary>
    /// The packs with only the selected units in their manifests, for a run that plans the selected units alone
    /// (<see cref="GenerationRun.PlansSelectedUnitsOnly"/>); without a unit filter, the packs as they are.
    /// </summary>
    /// <param name="packs">The run's packs.</param>
    /// <returns>The packs to plan.</returns>
    public PackSet SelectedUnitsOnly(PackSet packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        if (!IsResolved)
            throw new InvalidOperationException("Resolve the selection against the run's packs before narrowing them.");
        if (_pairs is not { } pairs)
            return packs;
        return packs with
        {
            Packs = [.. packs.Packs.Select(p => p with { Manifest = p.Manifest with { Units = [.. p.Manifest.Units.Where(u => pairs.Contains(p.Name + "/" + u.Id))] } })],
        };
    }

    /// <summary>The MQ6034 warning of a selection that matches no planned unit and no manifest entry.</summary>
    /// <returns>The diagnostic.</returns>
    public Diagnostic NothingSelected()
    {
        var parts = new List<string>(2);
        if (_units is not null)
            parts.Add("units " + string.Join(", ", _units));
        if (_elements is not null)
            parts.Add("elements " + string.Join(", ", _elements.Order(StringComparer.Ordinal)));
        return RuleCatalog.Create(NothingRule, $"The generation filter ({string.Join("; ", parts)}) selects no unit and no output; nothing is generated.");
    }

    /// <summary>Whether a planned unit is selected.</summary>
    /// <param name="unit">The planned unit.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool Matches(PlannedUnit unit) => Matches(unit.Pack.Name, unit.Unit.Id, unit.Element?.Id);

    /// <summary>
    /// Whether a manifest entry's unit name (<c>&lt;unit&gt;[:&lt;element&gt;]</c>, without the companion mark) under a pack is selected.
    /// </summary>
    /// <param name="pack">The pack.</param>
    /// <param name="unitName">The unit name.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool MatchesName(string pack, string unitName)
    {
        var colon = unitName.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? Matches(pack, unitName[..colon], unitName[(colon + 1)..]) : Matches(pack, unitName, null);
    }

    /// <summary>Whether any manifest entry of the given packs is selected.</summary>
    /// <param name="manifests">The manifests.</param>
    /// <param name="packs">The run's pack names.</param>
    /// <returns><see langword="true"/> when one is.</returns>
    public bool MatchesAnyEntry(ManifestSet manifests, IEnumerable<string> packs) =>
        packs.Any(p => manifests.Entries(p).Any(e => MatchesName(p, StripCompanion(e.Unit))));

    /// <summary>A manifest unit name without the companion mark (<c>#companion</c>); the writer uses it too.</summary>
    /// <param name="unit">The manifest entry's unit.</param>
    /// <returns>The unit name.</returns>
    public static string StripCompanion(string unit) =>
        unit.EndsWith("#companion", StringComparison.Ordinal) ? unit[..^"#companion".Length] : unit;

    private bool Matches(string pack, string unitId, string? elementId)
    {
        if (!IsResolved)
            throw new InvalidOperationException("Resolve the selection against the run's packs before matching.");
        if (_elements is not null && (elementId is null || !_elements.Contains(elementId)))
            return false;
        return _pairs is null || _pairs.Contains(pack + "/" + unitId);
    }
}
