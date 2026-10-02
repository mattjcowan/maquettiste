using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>One rendered output path of a unit (<c>POST /api/templates/paths</c>).</summary>
/// <param name="ElementId">The element, or <see langword="null"/> for model and locale scope.</param>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Role"><c>main</c> or <c>companion</c>.</param>
/// <param name="Root">The output root that holds it, or <see langword="null"/> when none does.</param>
/// <param name="Allowed">Whether the writer would write it.</param>
/// <param name="Rule">The refusal's rule (MQ6004), or <see langword="null"/>.</param>
public sealed record UnitPath(string? ElementId, string Path, string Role, string? Root, bool Allowed, string? Rule)
{
    /// <summary>
    /// The resolved element's name for people: an element's name; a table, view or sequence as <c>name (database)</c>, the name
    /// qualified by its schema when that is not the database's default (<c>sales.orders (billing)</c>); a column as
    /// <c>table.column (database)</c>; a locale's tag. <see langword="null"/> for model scope and for an object with no name.
    /// </summary>
    public string? ElementName { get; init; }

    /// <summary>The resolved element's kind (<c>entity</c>, <c>table</c>, <c>locale</c>, ...), or <see langword="null"/> for model scope.</summary>
    public string? ElementKind { get; init; }
}

/// <summary>A unit's output paths over its scope (generation-ui.md section 5.2).</summary>
/// <param name="Count">How many elements the unit plans (after the filter and the skip hints), or the listed elements it plans.</param>
/// <param name="Rendered">How many of them were rendered here.</param>
/// <param name="Paths">The rendered paths, by element then path, ordinal.</param>
/// <param name="Diagnostics">MQ6019, MQ6020, MQ6005, MQ6007 and the render errors.</param>
/// <param name="ElapsedMs">Time spent.</param>
public sealed record UnitPathsResult(int Count, int Rendered, IReadOnlyList<UnitPath> Paths, IReadOnlyList<Diagnostic> Diagnostics, long ElapsedMs);

/// <summary>Why a unit does or does not render an element (generation-ui.md section 4.3; <c>POST /api/generate/explain</c>).</summary>
/// <param name="Pack">The pack.</param>
/// <param name="Unit">The unit id.</param>
/// <param name="ElementId">The element asked about.</param>
/// <param name="Planned">Whether the unit plans the element.</param>
/// <param name="Reason">The first reason that applies: <c>pack-disabled</c>, <c>pack-invalid</c>, <c>unknown-unit</c>, <c>unknown-element</c>,
/// <c>scope</c>, <c>selector</c>, <c>skip-hint</c>, <c>filter</c>, or, when planned, the plan's reason (<c>new</c>, <c>forced</c>, <c>inputs</c>,
/// <c>outputs</c>, <c>unchanged</c>, <c>target-missing</c>).</param>
/// <param name="Detail">One sentence.</param>
/// <param name="Key">The unit key.</param>
/// <param name="PlanId">The plan the planned answer comes from.</param>
/// <param name="PlanUnit">The planned unit with its causes.</param>
/// <param name="Diagnostics">Load or plan errors behind the answer.</param>
public sealed record ExplainResult(string Pack, string Unit, string? ElementId, bool Planned, string Reason, string Detail, string Key,
    string? PlanId, PlanUnit? PlanUnit, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>A template variable for completion.</summary>
/// <param name="Name">The variable, or a dotted path such as <c>pack.params.quoting</c>.</param>
/// <param name="Detail">What it holds.</param>
public sealed record TemplateVariable(string Name, string Detail);

/// <summary>A member of a resolved record as templates see it (snake_case).</summary>
/// <param name="Name">The member name.</param>
/// <param name="Type"><c>string</c>, <c>number</c>, <c>boolean</c>, <c>list</c>, <c>map</c> or <c>object</c>.</param>
public sealed record TemplateMember(string Name, string Type);

/// <summary>Completion data for one unit's templates (<c>GET /api/templates/context</c>).</summary>
/// <param name="Pack">The pack.</param>
/// <param name="Unit">The unit id.</param>
/// <param name="Scope">The unit's <c>for</c>.</param>
/// <param name="Variables">The globals, ordinal.</param>
/// <param name="Members">Member lists by variable (<c>model</c>, <c>element</c>), ordinal.</param>
/// <param name="Helpers">The built-in helpers, ordinal.</param>
public sealed record TemplateContextResult(string Pack, string Unit, string Scope, IReadOnlyList<TemplateVariable> Variables,
    IReadOnlyDictionary<string, IReadOnlyList<TemplateMember>> Members, IReadOnlyList<string> Helpers)
{
    /// <summary>What the pack's own scripts register, by kind then name; its helpers are also in <see cref="Helpers"/>.</summary>
    public IReadOnlyList<ScriptRegistration> Registrations { get; init; } = [];
}

public sealed partial class GenerationService
{
    private const int MaxPathElements = 2000;

    /// <summary>
    /// Renders a unit's output paths over its scope, after its filter and the skip hints as the planner counts them, or over the listed
    /// elements (generation-ui.md section 5.2). Each planned element is rendered fully (so a pattern that reads a variable the template
    /// assigned gets its real value), up to <paramref name="limit"/>, under the preview deadline. Nothing touches disk.
    /// </summary>
    public async Task<UnitPathsResult> PathsAsync(string pack, string unitId, IReadOnlyList<string>? elementIds, PreviewOptions? options, int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(unitId);
        using var turn = TakeTurn(options?.Connection, ct);
        ct = turn.Token;
        var clock = Stopwatch.StartNew();
        // The deadline covers the whole request: waiting for a preview slot, preparing, planning and rendering.
        var deadline = PreviewDeadlineMs();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(deadline);
        UnitSession? session;
        IReadOnlyList<Diagnostic> failure;
        IDisposable? slot = null;
        try
        {
            slot = await EnterPreviewAsync(timeout.Token).ConfigureAwait(false);
            (session, failure) = await OpenUnitAsync(pack, unitId, null, options, timeout.Token, checkScope: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            slot?.Dispose();
            return new UnitPathsResult(0, 0, [], [RuleCatalog.Create("MQ6007", $"The paths of '{pack}/{unitId}' did not finish within {deadline} ms (limits.scriptTimeoutMs x 4).")],
                clock.ElapsedMilliseconds);
        }
        using var held = slot;
        if (session is null)
            return new UnitPathsResult(0, 0, [], failure, clock.ElapsedMilliseconds);
        var (prepared, loaded, unit, _, _, _, _, renderer, context) = session;
        var single = loaded with { Manifest = loaded.Manifest with { Units = [unit] } };
        var diagnostics = new List<Diagnostic>(UnitRules.OutputRoots(single.Manifest, loaded.Settings, prepared.Snapshot.Settings.Outputs.Allow,
            loaded.RelativePath + "/pack.json"));
        UnitPlan plan;
        try
        {
            plan = await _services.Planner.PlanAsync(prepared.Resolved, new PackSet([single], []) { CheckOutputRoots = false }, _services.Scripts, null, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            diagnostics.Add(RuleCatalog.Create("MQ6007", $"The paths of '{pack}/{unit.Id}' did not finish within {deadline} ms (limits.scriptTimeoutMs x 4).",
                filePath: loaded.RelativePath + "/pack.json"));
            return new UnitPathsResult(0, 0, [], diagnostics, clock.ElapsedMilliseconds);
        }
        diagnostics.AddRange(plan.Diagnostics);
        var units = plan.Units;
        if (elementIds is { Count: > 0 })
        {
            var wanted = elementIds.ToHashSet(StringComparer.Ordinal);
            units = [.. units.Where(u => u.Element is not null && wanted.Contains(u.Element.Id))];
        }

        var take = Math.Clamp(limit <= 0 ? 200 : limit, 1, MaxPathElements);
        var paths = new List<UnitPath>();
        var rendered = 0;
        try
        {
            foreach (var planned in units.Take(take))
            {
                var result = await renderer.RenderOneAsync(planned, context, timeout.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                timeout.Token.ThrowIfCancellationRequested();
                rendered++;
                diagnostics.AddRange(result.Diagnostics.Where(Outcomes.IsInvalid));
                foreach (var file in result.Files)
                {
                    var check = prepared.Paths.Check(file.Path);
                    paths.Add(new UnitPath(planned.Element?.Id, check.Allowed ? check.NormalizedPath : file.Path,
                        file.Role == FileRole.Companion ? "companion" : "main", check.Root?.Path, check.Allowed, check.Allowed ? null : check.RuleId ?? "MQ6004")
                    {
                        ElementName = planned.Element is { } named ? NameOf(named) : null,
                        ElementKind = planned.Element?.Kind,
                    });
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            diagnostics.Add(RuleCatalog.Create("MQ6007", $"The paths of '{pack}/{unit.Id}' did not finish within {deadline} ms (limits.scriptTimeoutMs x 4); {rendered} of {units.Count} were rendered.",
                filePath: loaded.RelativePath + "/pack.json"));
        }

        foreach (var group in paths.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var exact = group.GroupBy(p => p.Path, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            var elements = group.Select(p => p.ElementId ?? "(model)").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            diagnostics.Add(exact is not null
                ? RuleCatalog.Create("MQ6020", $"Unit '{pack}/{unit.Id}' renders {exact.Key} for {string.Join(" and ", elements.Select(e => "'" + e + "'"))}: its output pattern is not unique per element.",
                    filePath: exact.Key)
                : RuleCatalog.Create("MQ6005", $"Case-colliding output paths in unit '{pack}/{unit.Id}': {string.Join(", ", group.Select(p => p.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}.",
                    filePath: group.Key));
        }

        paths.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.ElementId, b.ElementId);
            return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
        });
        return new UnitPathsResult(units.Count, rendered, paths, Outcomes.Sort(diagnostics), clock.ElapsedMilliseconds);
    }

    /// <summary>The name <see cref="UnitPath.ElementName"/> shows for a resolved object, or <see langword="null"/> when it has none.</summary>
    internal static string? NameOf(IResolvedObject element)
    {
        static string Physical(string name, string? schema, RDatabase database) =>
            (schema is { Length: > 0 } && !string.Equals(schema, database.DefaultSchema, StringComparison.Ordinal) ? schema + "." + name : name)
            + (database.Name.Length > 0 ? " (" + database.Name + ")" : "");

        return element switch
        {
            RElement named => named.Name,
            RTable table => Physical(table.Name, table.Schema, table.Database),
            RView view => Physical(view.Name, view.Schema, view.Database),
            RSequence sequence => Physical(sequence.Name, sequence.Schema, sequence.Database),
            RRoutine routine => Physical(routine.Name, routine.Schema, routine.Database),
            RDatabaseType type => Physical(type.Name, type.Schema, type.Database),
            RSqlObject obj => Physical(obj.Name, obj.Schema, obj.Database),
            RQuery query => Physical(query.Name, null, query.Database),
            RColumn column => Physical(column.Table.Name + "." + column.Name, column.Table.Schema, column.Table.Database),
            RDatabase database => database.Name,
            RSchema schema => schema.Name,
            RLocale locale => locale.Tag,
            RAnnotated { DisplayName.Length: > 0 } annotated => annotated.DisplayName,
            RProcessNode { DisplayName.Length: > 0 } node => node.DisplayName,
            _ => null,
        };
    }

    /// <summary>
    /// Why a unit does or does not render an element (generation-ui.md section 4.3), with the first reason that applies. When the unit plans
    /// the element, the answer carries the planned unit's reason and causes from <paramref name="planId"/>, or from a new dry-run plan of
    /// the pack when no plan is given or the plan does not hold the unit. <see langword="null"/> when there is no such pack.
    /// </summary>
    /// <exception cref="PackPathException">The name is not a pack key.</exception>
    /// <param name="pack">The pack.</param>
    /// <param name="unitId">The unit id.</param>
    /// <param name="elementId">The element id, or <see langword="null"/> for a model-scope unit.</param>
    /// <param name="planId">A plan to read the answer from, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="selectedPacks">The run's pack selection (<see langword="null"/>: every enabled pack).</param>
    public async Task<ExplainResult?> ExplainAsync(string pack, string unitId, string? elementId, string? planId, CancellationToken ct,
        IReadOnlyList<string>? selectedPacks = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(unitId);
        if (!File.Exists(Path.Combine(PackAuthoring.PackRoot(_options, pack), "pack.json")))
            return null;
        var key = UnitPlanner.KeyOf(pack, unitId, elementId);
        ExplainResult Answer(string reason, string detail, IReadOnlyList<Diagnostic>? diagnostics = null) =>
            new(pack, unitId, elementId, false, reason, detail, key, null, null, diagnostics ?? []);

        var run = new GenerationRun(_services, _store, null);
        var prepared = await run.PrepareAsync([pack], GenerationMode.DryRun, ct, locked: false).ConfigureAwait(false);
        if (prepared is null)
            return Answer("pack-invalid", "The model or the pack has errors, so nothing is planned.", [.. run.Diagnostics.Where(Outcomes.IsInvalid)]);
        var loaded = prepared.Packs.Packs.FirstOrDefault(p => string.Equals(p.Name, pack, StringComparison.Ordinal));
        var disabled = false;
        if (loaded is null)
        {
            var (named, errors) = await new PackLoader(_options, _services.Schemas).LoadNamedAsync(prepared.Snapshot, pack, ct).ConfigureAwait(false);
            if (named is null)
                return Answer("pack-invalid", $"Pack '{pack}' does not load, so none of its units run.", errors);
            loaded = named;
            disabled = true;
        }

        var unit = loaded.Manifest.Units.FirstOrDefault(u => string.Equals(u.Id, unitId, StringComparison.Ordinal));
        if (unit is null)
            return Answer("unknown-unit", $"Pack '{pack}' has no unit '{unitId}'.");
        if (disabled)
            return Answer("pack-disabled", $"Pack '{pack}' is disabled (packs.{pack}.enabled is false in the project settings), so none of its units run.");
        if (selectedPacks is { Count: > 0 } && !selectedPacks.Contains(pack, StringComparer.Ordinal))
            return Answer("not-selected", $"Pack '{pack}' is not in this run's selection ({string.Join(", ", selectedPacks.Order(StringComparer.Ordinal))}).");
        var element = elementId is null ? null : prepared.Resolved.Find(elementId);
        if (elementId is not null && element is null)
            return Answer("unknown-element", $"'{elementId}' is not in the resolved model (deleted, renamed or never defined).");
        if (UnitPlanner.WhyNot(prepared.Resolved, loaded, unit, element, _services.Scripts, ct) is { } why)
            return Answer(why.Kind, why.Detail);

        var plan = planId is null ? null : await GetPlanAsync(planId, ct).ConfigureAwait(false);
        if (plan?.Units.Any(u => string.Equals(u.Key, key, StringComparison.Ordinal)) != true)
        {
            var result = await PlanAsync(new GenerationRequest { Mode = GenerationMode.DryRun, Packs = [pack] }, null, ct).ConfigureAwait(false);
            plan = result.Plan;
        }

        var detail = plan is null ? null : await GetPlanUnitAsync(plan.Id, key, ct).ConfigureAwait(false);
        if (detail is null)
            return Answer("pack-invalid", "The unit is in scope but the plan did not hold it; see the diagnostics.", plan?.Diagnostics.Where(Outcomes.IsInvalid).ToList());
        return new ExplainResult(pack, unitId, elementId, true, detail.Unit.Reason ?? (detail.Unit.Skipped ? "unchanged" : "inputs"), detail.Summary, key,
            plan!.Id, detail.Unit, []);
    }

    /// <summary>The manifest entries of one pack (<c>GET /api/packs/{pack}/outputs</c>); <see langword="null"/> when there is no such pack.</summary>
    /// <exception cref="PackPathException">The name is not a pack key.</exception>
    public async Task<PackOutputs?> GetPackOutputsAsync(string pack, CancellationToken ct)
    {
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return await PackAuthoring.OutputsAsync(_services, snapshot, pack, ct).ConfigureAwait(false);
    }

    /// <summary>Moves one pack file, optionally rewriting the units that name it (<c>POST /api/packs/{pack}/file/move</c>).</summary>
    /// <exception cref="PackPathException">A path is refused.</exception>
    public async Task<PackWriteResult> MovePackFileAsync(string pack, PackFileMove move, string expectedHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(move);
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        return await PackAuthoring.MoveFileAsync(_services, snapshot, pack, move, expectedHash, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves <c>packs.&lt;pack&gt;</c> of <c>maquettiste.json</c> (enabled, output base, parameter values) and nothing else in the file, with the
    /// settings ETag (<c>PUT /api/project/settings/packs/{pack}</c>). The save goes through the settings save, so it is validated the same way.
    /// </summary>
    public async Task<SettingsSaveResult> SavePackSettingsAsync(string pack, JsonElement section, string expectedHash, CancellationToken ct)
    {
        var current = await _store.GetSettingsAsync(ct).ConfigureAwait(false);
        var node = PackAuthoring.WithPackSettings(current.Json, pack, section);
        var bytes = _services.Json.Write(node, "maquettiste.json", "maquettiste.json");
        return await _store.SaveSettingsAsync(bytes, expectedHash, ChangeSource.Editor, ct).ConfigureAwait(false);
    }

    /// <summary>Completion data for a unit's templates: variables, the members of the model and of the scope's records, and helpers.</summary>
    /// <returns><see langword="null"/> when there is no such pack or unit.</returns>
    /// <exception cref="PackPathException">The name is not a pack key.</exception>
    public async Task<TemplateContextResult?> GetTemplateContextAsync(string pack, string unitId, CancellationToken ct)
    {
        var document = await GetPackAsync(pack, ct).ConfigureAwait(false);
        var summary = document is null ? null : (await ListPacksAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => string.Equals(p.Name, pack, StringComparison.Ordinal));
        var unit = summary?.Units.FirstOrDefault(u => string.Equals(u.Id, unitId, StringComparison.Ordinal));
        if (document is null || unit is null)
            return null;
        var variables = new List<TemplateVariable>
        {
            new("model", "The resolved model."),
            new("pack", "The pack: name, version and params."),
            new("unit", "The unit: id and key."),
            new("mapping", "The element's mapping to a database, when it has one."),
            new("mappings", "Every mapping of the element."),
            new("hints", "The element's generation hints for this pack."),
            new("schema_diff", "The schema diffs by database name."),
            new("data", "The pack's transforms' results."),
        };
        var elementType = ScopeType(unit.For);
        if (unit.For != "model")
        {
            variables.Add(new("element", "The element this unit renders for."));
            if (unit.For.StartsWith("each ", StringComparison.Ordinal))
                variables.Add(new(unit.For["each ".Length..].Replace(' ', '_'), "The element, by its kind."));
        }

        variables.AddRange(document.Parameters.Select(p => new TemplateVariable("pack.params." + p.Name, "Parameter " + p.Name + ".")));
        variables.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var members = new SortedDictionary<string, IReadOnlyList<TemplateMember>>(StringComparer.Ordinal) { ["model"] = MembersOf(typeof(ResolvedModel)) };
        if (elementType is not null)
            members["element"] = MembersOf(elementType);
        var registrations = document.Registrations;
        var helpers = BuiltinHelpers.Names.Concat(registrations.Where(r => r.Kind == ScriptRegistrationKind.Helper).Select(r => r.Name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return new TemplateContextResult(pack, unitId, unit.For, variables, members, [.. helpers]) { Registrations = registrations };
    }

    private static Type? ScopeType(string scope) => scope switch
    {
        "each package" => typeof(RPackage),
        "each entity" => typeof(REntity),
        "each relation" => typeof(RRelation),
        "each enum" => typeof(REnum),
        "each value object" => typeof(RValueObject),
        "each table" => typeof(RTable),
        "each view" => typeof(RView),
        "each sequence" => typeof(RSequence),
        "each routine" => typeof(RRoutine),
        "each database type" => typeof(RDatabaseType),
        "each sql object" => typeof(RSqlObject),
        "each query" => typeof(RQuery),
        "each reference type" => typeof(RReferenceType),
        "each seed" => typeof(RSeed),
        "each locale" => typeof(RLocale),
        "each process" => typeof(RProcess),
        "each actor" => typeof(RActor),
        "each scenario" => typeof(RScenario),
        _ => null,
    };

    private static IReadOnlyList<TemplateMember> MembersOf(Type type) =>
    [
        .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod?.IsPublic == true)
            .Select(p => new TemplateMember(Scriban.Runtime.StandardMemberRenamer.Rename(p.Name), KindOf(p.PropertyType)))
            .DistinctBy(m => m.Name, StringComparer.Ordinal)
            .OrderBy(m => m.Name, StringComparer.Ordinal),
    ];

    private static string KindOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsEnum)
            return "string";
        if (type == typeof(bool))
            return "boolean";
        if (type.IsPrimitive || type == typeof(decimal))
            return "number";
        // A property typed as the interface itself (IReadOnlyDictionary<,>) does not list it among its own interfaces.
        if (type.GetInterfaces().Prepend(type).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
            return "map";
        return type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) ? "list" : "object";
    }
}
