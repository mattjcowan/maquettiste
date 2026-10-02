using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// Fills the plan's explanation members (generation-ui.md section 4): each unit's pack, unit, template, element, reason and causes. The
/// reason is the first that applies: <c>new</c> (no recorded render), <c>forced</c>, <c>check</c> (a check run renders every unit),
/// <c>inputs</c> (the input hash differs from the
/// recorded one), <c>outputs</c> (inputs unchanged, an output missing or edited on disk), <c>unchanged</c> (skipped), and
/// <c>target-missing</c> for a <c>block</c> unit without <c>createFile</c> whose target files do not exist (it rendered, and writes
/// nothing; one <c>target-missing</c> cause per file). Causes come from the
/// recorded read keys: keys that no longer resolve (<c>absent</c>), keys whose hash differs from the one recorded at the unit's last
/// render (unit state format 3's per-key hashes, section 4.2), keys read for the first time or no longer read, the static parts
/// (pack version, unit definition, each parameter, scripts, output base, formatter, templates) and the outputs. A pack whose state file
/// was of another engine version or format gives every unit reason <c>new</c> with the single cause <c>state-reset</c>.
/// Element causes name the element ("Customer (entity) changed") when the model still has it, and its id otherwise.
/// Deterministic: ordinal by kind then key, the same at any parallelism.
/// </summary>
internal static class PlanExplainer
{
    /// <summary>The most causes a unit carries; <see cref="PlanUnit.CauseCount"/> gives the total.</summary>
    public const int MaxCauses = 20;

    /// <summary>Explains every unit of a plan against the recorded unit state (a plan writes none, so it is the state before the plan).</summary>
    public static async Task<IReadOnlyList<PlanUnit>> ExplainAsync(IReadOnlyList<PlanUnit> units, IReadOnlyList<PlannedUnit> planned, IUnitStateStore state,
        bool force, string repoRoot, Func<string, bool> resolves, CancellationToken ct, Func<string, string>? currentHash = null,
        Func<string, string?>? nameOf = null, bool check = false)
    {
        var byKey = planned.ToDictionary(u => u.Key, StringComparer.Ordinal);
        var states = new Dictionary<string, IReadOnlyDictionary<string, UnitState>>(StringComparer.Ordinal);
        var result = new List<PlanUnit>(units.Count);
        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            if (!byKey.TryGetValue(unit.Key, out var p))
            {
                result.Add(unit);
                continue;
            }

            if (!states.TryGetValue(p.Pack.Name, out var packState))
                states[p.Pack.Name] = packState = await state.LoadAsync(p.Pack.Name, ct).ConfigureAwait(false);
            packState.TryGetValue(unit.Key, out var stored);
            var (reason, causes) = TargetMissing(unit, p) is { Count: > 0 } missing
                ? ("target-missing", missing)
                : Explain(unit, stored, force, repoRoot, resolves, currentHash, p.StaticParts, state.WasReset(p.Pack.Name), check, nameOf);
            result.Add(unit with
            {
                Pack = p.Pack.Name,
                Unit = p.Unit.Id,
                Template = p.Unit.Template,
                ElementId = p.Element?.Id,
                Reason = reason,
                Causes = [.. causes.Take(MaxCauses)],
                CauseCount = causes.Count,
            });
        }

        return result;
    }

    /// <summary>
    /// The <c>target-missing</c> causes of a rendered <c>block</c> unit without <c>createFile</c> whose every target file was missing
    /// when planned (the writer writes nothing for it, MQ6028); empty otherwise.
    /// </summary>
    /// <param name="unit">The plan's unit.</param>
    /// <param name="planned">The planned unit.</param>
    /// <returns>The causes.</returns>
    internal static List<PlanCause> TargetMissing(PlanUnit unit, PlannedUnit planned)
    {
        if (unit.Skipped || planned.Unit.Mode != Model.OutputMode.Block || planned.Unit.CreateFile || unit.Outputs.Count == 0
            || unit.Outputs.Any(o => o.DiskHashAtPlan is not null))
            return [];
        return [.. unit.Outputs.Select(o => new PlanCause("target-missing", o.Path, $"{o.Path} does not exist; set createFile to create it", null, o.Path))];
    }

    /// <summary>The reason and every cause of one unit.</summary>
    /// <param name="unit">The plan's unit.</param>
    /// <param name="stored">The state recorded at the unit's last render, or <see langword="null"/>.</param>
    /// <param name="force">Whether the run is forced.</param>
    /// <param name="repoRoot">The repository root (outputs).</param>
    /// <param name="resolves">Whether an element id still resolves.</param>
    /// <param name="currentHash">A key's current hash, compared with the recorded per-key hashes; <see langword="null"/> skips that.</param>
    /// <param name="currentParts">The unit's current static parts (<see cref="PlannedUnit.StaticParts"/>), or <see langword="null"/>.</param>
    /// <param name="reset">Whether the pack's state file was of another engine version or format.</param>
    /// <param name="check">Whether the run is a check run, which renders every unit to compare.</param>
    /// <param name="nameOf">An element id's display label, <c>Name (kind)</c>, or <see langword="null"/> when the model no longer has it.</param>
    internal static (string Reason, List<PlanCause> Causes) Explain(PlanUnit unit, UnitState? stored, bool force, string repoRoot, Func<string, bool> resolves,
        Func<string, string>? currentHash = null, string? currentParts = null, bool reset = false, bool check = false, Func<string, string?>? nameOf = null)
    {
        var causes = new List<PlanCause>();
        if (unit.Skipped)
            return ("unchanged", causes);
        if (stored is null)
        {
            if (reset)
                causes.Add(new PlanCause("state-reset", "", "No state from this engine version or format; every unit renders", null, null));
            return ("new", causes);
        }
        if (force)
            return ("forced", causes);
        if (check)
            return ("check", causes);
        string Describe(string key) => PlanExplainer.Describe(key, nameOf);

        if (!string.Equals(stored.InputHash, unit.InputHash, StringComparison.Ordinal))
        {
            var now = new HashSet<string>(unit.ReadKeys, StringComparer.Ordinal);
            var before = new HashSet<string>(stored.ReadKeys, StringComparer.Ordinal);
            var hashes = currentHash is not null && stored.KeyHashes.Length == stored.ReadKeys.Count * KeyHashes.Size ? stored.KeyHashes : default;
            for (var i = 0; i < stored.ReadKeys.Count; i++)
            {
                var key = stored.ReadKeys[i];
                // With the hasher, an element key is absent when it no longer hashes (it covers every element id a key can name);
                // without, when the resolved model no longer finds it.
                if (key.StartsWith("e:", StringComparison.Ordinal)
                    && (currentHash is not null ? string.Equals(currentHash(key), DependencyHasher.Absent, StringComparison.Ordinal) : !resolves(key[2..])))
                    causes.Add(new PlanCause("absent", key, $"{Label(key[2..], nameOf) ?? LastName(stored, key) ?? key[2..]} was deleted", key[2..], null));
                else if (!now.Contains(key))
                    causes.Add(new PlanCause(KindOf(key), key, $"{Describe(key)} is no longer read", ElementOf(key), null));
                else if (!hashes.IsEmpty && KeyHashes.Differs(hashes.Span, i, currentHash!(key)))
                    causes.Add(new PlanCause(KindOf(key), key, $"{Describe(key)} changed", ElementOf(key), null));
            }

            if (stored.StaticParts is not null && currentParts is not null && !string.Equals(stored.StaticParts, currentParts, StringComparison.Ordinal))
                causes.AddRange(StaticCauses(unit.Key, KeyHashes.Parts(stored.StaticParts), KeyHashes.Parts(currentParts)));

            foreach (var key in unit.ReadKeys.Where(k => !before.Contains(k)).Order(StringComparer.Ordinal))
                causes.Add(new PlanCause(KindOf(key), key, $"{Describe(key)} is read for the first time", ElementOf(key), null));
            if (causes.Count == 0)
            {
                causes.Add(new PlanCause("inputs", "", $"One of its {stored.ReadKeys.Count} recorded inputs, or its unit definition, parameters, scripts or templates, changed",
                    null, null));
            }

            return ("inputs", Sort(causes));
        }

        foreach (var output in stored.Outputs)
        {
            var full = Path.Combine(repoRoot, output.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                causes.Add(new PlanCause("output-missing", output.Path, $"{output.Path} is missing", null, output.Path));
                continue;
            }

            if (ManifestHashes.IsOwned(output.ManifestHash) || ManifestHashes.IsRegions(output.ManifestHash) || ManagedBlock.IsBlock(output.ManifestHash))
                continue;
            try
            {
                if (!string.Equals(ContentHash.Of(File.ReadAllBytes(full)), output.ManifestHash, StringComparison.Ordinal))
                    causes.Add(new PlanCause("output-edited", output.Path, $"{output.Path} was edited on disk", null, output.Path));
            }
            catch (IOException)
            {
                causes.Add(new PlanCause("output-edited", output.Path, $"{output.Path} cannot be read", null, output.Path));
            }
        }

        if (causes.Count == 0)
            causes.Add(new PlanCause("output-edited", "", "Its outputs' manifest entries differ from the recorded ones", null, null));
        return ("outputs", Sort(causes));
    }

    /// <summary>The causes from the static parts that differ, by part name.</summary>
    private static IEnumerable<PlanCause> StaticCauses(string unitKey, SortedDictionary<string, string> before, SortedDictionary<string, string> now)
    {
        var pack = unitKey.Split('/')[0];
        var unitId = unitKey.Split('/', 3) is { Length: > 1 } segments ? segments[1].Split(':')[0] : unitKey;
        foreach (var name in before.Keys.Union(now.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(name, out var old);
            now.TryGetValue(name, out var value);
            if (string.Equals(old, value, StringComparison.Ordinal))
                continue;
            if (name.StartsWith("parameter:", StringComparison.Ordinal))
            {
                var parameter = name["parameter:".Length..];
                var what = old is null ? "was added" : value is null ? "was removed" : "changed";
                yield return new PlanCause("parameter", parameter, $"Parameter {parameter} {what}", null, null);
            }
            else if (name.StartsWith("template:", StringComparison.Ordinal))
            {
                var path = name["template:".Length..];
                yield return new PlanCause("template", "t:" + pack + "/" + path, $"Template {path} changed", null, null);
            }
            else
            {
                yield return name switch
                {
                    "pack-version" => new PlanCause("pack-version", pack, $"{pack} went from {old ?? "?"} to {value ?? "?"}", null, null),
                    "unit" => new PlanCause("unit", unitId, $"Unit {unitId}'s definition changed", null, null),
                    "scripts" => new PlanCause("scripts", pack, $"The scripts of {pack} changed", null, null),
                    "output-base" => new PlanCause("output-base", pack, $"The output base of {pack} changed", null, null),
                    "formatter" => new PlanCause("formatter", unitId, "The formatter settings changed", null, null),
                    _ => new PlanCause("inputs", name, $"{name} changed", null, null),
                };
            }
        }
    }

    /// <summary>The cause kind of a read key (engine-design.md section 11's key prefixes).</summary>
    public static string KindOf(string key) => key.Length < 2 || key[1] != ':' ? "inputs" : key[0] switch
    {
        'e' => "element",
        'k' => "kind-set",
        'r' => "referrers",
        's' => key.StartsWith("s:localization", StringComparison.Ordinal) ? "localization" : "setting",
        't' => "template",
        'd' => "schema-diff",
        'l' => "translation",
        _ => "inputs",
    };

    private static string? ElementOf(string key) => key.StartsWith("e:", StringComparison.Ordinal) || key.StartsWith("r:", StringComparison.Ordinal) ? key[2..] : null;

    private static string? Label(string id, Func<string, string?>? nameOf) => nameOf?.Invoke(id);

    /// <summary>The label an element key had when the unit last rendered (unit state format 4), or <see langword="null"/>.</summary>
    private static string? LastName(UnitState stored, string key) => stored.Names is { } names && names.TryGetValue(key, out var name) ? name : null;

    /// <summary>
    /// The labels of the element keys (<c>e:&lt;id&gt;</c>) among <paramref name="keys"/> that <paramref name="find"/> resolves to a named
    /// object, for the unit state; <see langword="null"/> when there are none.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? NamesOf(IReadOnlyList<string> keys, Func<string, Maquettiste.Engine.Resolution.IResolvedObject?> find)
    {
        Dictionary<string, string>? names = null;
        foreach (var key in keys)
        {
            if (key.StartsWith("e:", StringComparison.Ordinal) && LabelOf(find(key[2..])) is { } label)
                (names ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = label;
        }

        return names;
    }

    private static string Describe(string key, Func<string, string?>? nameOf) => KindOf(key) switch
    {
        "element" => Label(key[2..], nameOf) ?? "Element " + key[2..],
        "kind-set" => "The set of " + key[2..] + " elements",
        "referrers" => "Something that references " + (Label(key[2..], nameOf) ?? key[2..]),
        "setting" => "Setting " + key[2..],
        "template" => "Template " + key[2..],
        "schema-diff" => "The schema diff of " + key[2..],
        "translation" => "Texts " + key[2..],
        _ => "Input " + key,
    };

    /// <summary>The display label of a resolved object: <c>Name (kind)</c>, or <see langword="null"/> when it has no name.</summary>
    public static string? LabelOf(Maquettiste.Engine.Resolution.IResolvedObject? obj)
    {
        var name = obj switch
        {
            null => null,
            Maquettiste.Engine.Resolution.RElement e => e.Name,
            _ => obj.GetType().GetProperty("Name")?.GetValue(obj) as string,
        };
        return string.IsNullOrEmpty(name) ? null : $"{name} ({obj!.Kind})";
    }

    private static List<PlanCause> Sort(List<PlanCause> causes) =>
        [.. causes.DistinctBy(c => (c.Kind, c.Key)).OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal)];
}
