using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// The packs of a dry run's impact (next-phase-2-design.md section 6.1, decisions 3 to 5): per enabled pack of the model before the
/// change, the units whose stored read keys the change touches, the units of the elements it deletes and the units its created or
/// changed elements enter, from each pack's stored unit states alone. Nothing is resolved, planned or rendered, no run lock is taken and no
/// file is written: the impact names the files whose units would render again, not whether their bytes change.
/// </summary>
internal static class OutputImpact
{
    /// <summary>The change of a unit a touched read key lists.</summary>
    public const string Inputs = "inputs";

    /// <summary>The change of a unit whose element the change deletes.</summary>
    public const string Deleted = "deleted";

    /// <summary>The change of a unit a created element enters.</summary>
    public const string Added = "added";

    private const int MaxKeys = 5;

    /// <summary>The impact's packs, in pack order: the loaded enabled packs, then those that do not load, by name.</summary>
    /// <param name="services">The engine services (pack loader and unit state store).</param>
    /// <param name="options">The engine options.</param>
    /// <param name="run">The store's dry run, which would save.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The packs.</returns>
    public static async Task<IReadOnlyList<ImpactPack>> PacksAsync(EngineServices services, EngineOptions options, BatchPreviewRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(run);
        var (before, candidate, preview) = (run.Before!, run.Candidate!, run.Preview);
        var set = await services.Packs.LoadAsync(before, null, null, ct).ConfigureAwait(false);
        var states = new Dictionary<string, IReadOnlyDictionary<string, UnitState>>(StringComparer.Ordinal);
        foreach (var pack in set.Packs.Concat(set.Used))
            states[pack.Name] = await services.UnitState.LoadAsync(pack.Name, ct).ConfigureAwait(false);

        var entering = Entering(before, candidate, run.Changed);
        var created = entering.Where(e => e.Before is null).ToList();
        var exporters = new Dictionary<string, ILookup<string, UnitState>>(StringComparer.Ordinal);
        ImpactKeys? keys = null;
        keys = new ImpactKeys(before, candidate, run.Changed, preview.Files, preview.Impact?.Databases.Select(d => d.Id) ?? [], preview.Facts)
        {
            Exports = rest => ExportsTouched(rest, states, exporters, created, keys!),
        };

        var touchedByTable = new Dictionary<string[], bool?[]>(ReferenceEqualityComparer.Instance);
        Func<string, bool> heldBefore = id => before.TryGetEntry(id, out _), heldAfter = id => candidate.TryGetEntry(id, out _);
        var packs = new List<ImpactPack>();
        foreach (var pack in set.Packs)
        {
            ct.ThrowIfCancellationRequested();
            var own = states[pack.Name];
            if (own.Count == 0)
            {
                packs.Add(new ImpactPack(pack.Name, pack.Manifest.Units.Count == 0 ? "known" : "none", [], 0));
                continue;
            }

            var units = new SortedDictionary<string, ImpactUnit>(StringComparer.Ordinal);
            foreach (var (key, state) in own)
            {
                var touched = TouchedKeys(state.ReadKeys, keys, touchedByTable);
                var deleted = ElementDeleted(key, heldBefore, heldAfter);
                if (touched.Count == 0 && !deleted)
                    continue;
                units[key] = new ImpactUnit(key, deleted ? Deleted : Inputs, touched,
                    [.. state.Outputs.Select(o => o.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
            }

            // A unit of the element's scope with no state at its key did not plan it before: the element is created, or a where filter
            // or a skip hint left it out, and the change may let it in. Filtered when plan decides: a where filter, or hints that
            // skipped the pack before the change.
            foreach (var element in entering)
            {
                var scope = ScopeOf(element.Element);
                if (scope is null || UnitPlanner.SkippedByHints(element.Hints, pack.Name))
                    continue;
                var unskipped = element.Before is { } previous && UnitPlanner.SkippedByHints(previous, pack.Name);
                foreach (var unit in pack.Manifest.Units)
                {
                    if (!string.Equals(unit.For, scope, StringComparison.Ordinal))
                        continue;
                    var key = UnitPlanner.KeyOf(pack.Name, unit.Id, element.Id);
                    if (!units.ContainsKey(key) && !own.ContainsKey(key))
                        units[key] = new ImpactUnit(key, Added, [], []) { Filtered = unit.Where is not null || unskipped };
                }
            }

            packs.Add(new ImpactPack(pack.Name, "known", [.. units.Values.Take(ImpactPack.MaxUnits)], units.Count));
        }

        var loaded = set.Packs.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in EnabledPacks(options, before).Where(n => !loaded.Contains(n)))
            packs.Add(new ImpactPack(name, "invalid", [], 0));
        return packs;
    }

    /// <summary>The touched read keys of a state, ordinal, at most <see cref="MaxKeys"/>: one test per key of the state file's table.</summary>
    private static IReadOnlyList<string> TouchedKeys(IReadOnlyList<string> readKeys, ImpactKeys keys, Dictionary<string[], bool?[]> byTable)
    {
        List<string>? touched = null;
        if (readKeys is UnitStateStore.TableKeys table)
        {
            if (!byTable.TryGetValue(table.Table, out var answers))
                byTable[table.Table] = answers = new bool?[table.Table.Length];
            foreach (var index in table.Indexes)
            {
                var answer = answers[index] ??= keys.IsTouched(table.Table[index]);
                if (answer && (touched ??= []).Count < MaxKeys)
                    touched.Add(table.Table[index]);
            }
        }
        else
        {
            foreach (var key in readKeys)
            {
                if (keys.IsTouched(key) && (touched ??= []).Count < MaxKeys)
                    touched.Add(key);
            }
        }

        return touched is null ? [] : [.. touched.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether the element of a unit key is deleted: its element id is an id the model before held and the candidate does not. An
    /// id neither model holds as a whole (a resolved id such as <c>&lt;entity&gt;@&lt;database&gt;</c>, or an inherited copy
    /// <c>&lt;tableId&gt;.&lt;memberId&gt;</c>) is tried by its halves around <c>@</c>, and a half neither holds by its parts around
    /// <c>.</c>: a part that goes deletes it. An id or half held as a whole answers for itself, whatever its parts do.
    /// </summary>
    /// <param name="key">The unit key, <c>&lt;pack&gt;/&lt;unit&gt;:&lt;id&gt;</c>.</param>
    /// <param name="heldBefore">Whether the model before holds an id.</param>
    /// <param name="heldAfter">Whether the candidate holds an id.</param>
    /// <returns><see langword="true"/> when the element goes.</returns>
    internal static bool ElementDeleted(string key, Func<string, bool> heldBefore, Func<string, bool> heldAfter)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
            return false;
        var id = key[(colon + 1)..];
        if (heldBefore(id))
            return !heldAfter(id);
        foreach (var half in id.Split('@', StringSplitOptions.RemoveEmptyEntries))
        {
            if (heldBefore(half))
            {
                if (!heldAfter(half))
                    return true;
                continue;
            }

            foreach (var part in half.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (heldBefore(part) && !heldAfter(part))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>px:&lt;pack&gt;/&lt;id&gt;</c> or <c>px:&lt;pack&gt;</c>: a key the pack's exporting units recorded with their stored exports is
    /// touched, or the element is created (its record appears). Without states of that pack every such key counts as touched.
    /// </summary>
    private static bool ExportsTouched(string rest, Dictionary<string, IReadOnlyDictionary<string, UnitState>> states,
        Dictionary<string, ILookup<string, UnitState>> exporters, IReadOnlyList<ScopeElement> created, ImpactKeys keys)
    {
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var (pack, id) = slash < 0 ? (rest, "") : (rest[..slash], rest[(slash + 1)..]);
        if (!states.TryGetValue(pack, out var own) || own.Count == 0)
            return true;
        if (id.Length > 0 && created.Any(c => string.Equals(c.Id, id, StringComparison.Ordinal)))
            return true;
        if (!exporters.TryGetValue(pack, out var byElement))
        {
            exporters[pack] = byElement = own.Values.Where(s => s.Exports is not null).ToLookup(s =>
            {
                var colon = s.Key.IndexOf(':', StringComparison.Ordinal);
                return colon < 0 ? "" : s.Key[(colon + 1)..];
            }, StringComparer.Ordinal);
        }

        return byElement[id].Any(s => s.Exports!.ReadKeys.Any(keys.IsTouched));
    }

    /// <summary>
    /// An element or attribute that may enter a unit's scope: created (<paramref name="Before"/> null), or held by both models, with the
    /// generation hints it had before.
    /// </summary>
    private sealed record ScopeElement(string Id, Element? Element, IReadOnlyDictionary<string, GenerationHints> Hints,
        IReadOnlyDictionary<string, GenerationHints>? Before);

    /// <summary>
    /// The changed elements, created or held by both models, and the attributes they declare (for <c>each attribute</c>: an entity's,
    /// a relation's, a value object's or a reference type's, never a stereotype's), ordinal by id.
    /// </summary>
    private static List<ScopeElement> Entering(ModelSnapshot before, ModelSnapshot candidate, IReadOnlyList<string> changed)
    {
        var entering = new List<ScopeElement>();
        foreach (var id in changed)
        {
            if (candidate.Get<Element>(id) is not { } element)
                continue;
            var previous = before.Get<Element>(id);
            entering.Add(new ScopeElement(id, element, element.Generation, previous?.Generation));
            if (element is Stereotype)
                continue;
            Dictionary<string, ModelAttribute>? old = null;
            foreach (var attribute in ReferenceDataIndex.AttributesOf(element))
            {
                if (!before.TryGetEntry(attribute.Id, out _))
                {
                    entering.Add(new ScopeElement(attribute.Id, null, attribute.Generation, null));
                    continue;
                }

                if (old is null)
                {
                    old = new Dictionary<string, ModelAttribute>(StringComparer.Ordinal);
                    if (previous is not null)
                    {
                        foreach (var was in ReferenceDataIndex.AttributesOf(previous))
                            old.TryAdd(was.Id, was);
                    }
                }

                entering.Add(new ScopeElement(attribute.Id, null, attribute.Generation, old.GetValueOrDefault(attribute.Id)?.Generation ?? attribute.Generation));
            }
        }

        entering.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return entering;
    }

    /// <summary>
    /// The <c>for</c> scope an element enters: <c>each &lt;kind&gt;</c> by its kind name, <c>each custom:&lt;type&gt;</c> for a
    /// custom element, <c>each attribute</c> for an attribute. A table that binds an entity or a relation makes no table of its own
    /// (resolution names the table), so it enters none.
    /// </summary>
    private static string? ScopeOf(Element? element) => element switch
    {
        null => "each attribute",
        Custom custom => UnitRules.CustomScopePrefix + custom.Type,
        Table { Entity: not null } or Table { Relation: not null } => null,
        _ => "each " + element.KindName.Replace('-', ' '),
    };

    /// <summary>The enabled pack folders of a model (every folder of <c>templates/</c> with a <c>pack.json</c>), by name.</summary>
    private static IEnumerable<string> EnabledPacks(EngineOptions options, ModelSnapshot model)
    {
        var templates = Path.Combine(new ModelPaths(options).ModelRoot, "templates");
        if (!Directory.Exists(templates))
            return [];
        return Directory.EnumerateDirectories(templates).Select(Path.GetFileName).OfType<string>()
            .Where(n => !n.StartsWith('.') && File.Exists(Path.Combine(templates, n, "pack.json")))
            .Where(n => !model.Settings.Packs.TryGetValue(n, out var settings) || settings.Enabled)
            .Order(StringComparer.Ordinal);
    }
}
