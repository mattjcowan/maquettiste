using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine;

/// <summary>What <see cref="GenerationService.GetCalculatedAsync"/> reads (next-phase-2-design.md section 5.5).</summary>
/// <param name="Ids">The ids of the objects, 1 to <see cref="GenerationService.MaxCalculatedIds"/>; an attribute id names each object that
/// holds the attribute.</param>
/// <param name="Names">The calculated value names to evaluate, or <see langword="null"/> for every name each object's kind declares.</param>
public sealed record CalculatedValueQuery(IReadOnlyList<string> Ids, IReadOnlyList<string>? Names = null);

/// <summary>The calculated values of one resolved object.</summary>
/// <param name="Id">The object's id.</param>
/// <param name="Kind">The object's resolved kind (<c>entity</c>, <c>attribute</c>, ...).</param>
/// <param name="Holder">The holder's id, for an attribute held by an element other than its declaring one; otherwise
/// <see langword="null"/>.</param>
/// <param name="Calculated">The values by name, ordinal; a value that failed is left out (its diagnostic is in the answer).</param>
public sealed record CalculatedValueItem(string Id, string Kind, string? Holder, IReadOnlyDictionary<string, object?> Calculated)
{
    /// <summary>The object's name as a unit path listing shows it (<c>table (database)</c> for a physical object), or
    /// <see langword="null"/> when it has none.</summary>
    public string? Name { get; init; }
}

/// <summary>The answer of <see cref="GenerationService.GetCalculatedAsync"/>.</summary>
/// <param name="Items">One item per resolved object an id names, by id in query order, then by site.</param>
/// <param name="Missing">The ids that name no resolved object, in query order.</param>
/// <param name="Declarations">What the calculation files declare for the items' kinds (the names asked for only), in file and call
/// order.</param>
/// <param name="Diagnostics">The failures (MQ5010, MQ5011, MQ5013), or the errors that prevented resolution (then there are no items).</param>
public sealed record CalculatedValueAnswer(IReadOnlyList<CalculatedValueItem> Items, IReadOnlyList<string> Missing,
    IReadOnlyList<CalculatedValueDeclaration> Declarations, IReadOnlyList<Diagnostic> Diagnostics);

public sealed partial class GenerationService
{
    /// <summary>
    /// One page of the resolved model as flat records (<see cref="ResolvedRecords"/>), by (kind, name, id): what templates read, as data
    /// for external systems. The model is validated and resolved once per snapshot (the store keeps the last snapshot's resolved model, so paging
    /// through it resolves once); a model with errors returns no records and the errors, unless its only errors are pending migration facts
    /// (MQ4101), which return records. Takes no run lock and writes nothing.
    /// </summary>
    /// <param name="query">The scope, the database, the cursor and the page size.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ArgumentException">An unknown scope.</exception>
    /// <exception cref="FormatException">The cursor is not one a paged read returned.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is out of range.</exception>
    public async Task<ResolvedPage> GetResolvedAsync(ResolvedQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Scope != "all" && !ResolvedRecords.Scopes.ContainsKey(query.Scope))
            throw new ArgumentException($"scope must be one of {string.Join(", ", ResolvedRecords.ScopeNames)}, not '{query.Scope}'.", nameof(query));
        ModelPages.CheckLimit(query.Limit);
        if (!string.IsNullOrEmpty(query.Cursor))
            ModelPages.ReadCursor(query.Cursor);
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var state = await ReadableAsync(snapshot, ct).ConfigureAwait(false);
        if (state.Model is null)
            return new ResolvedPage([], null, state.Errors);
        var entries = ResolvedRecords.Entries(state.Model, query.Scope, string.IsNullOrEmpty(query.Database) ? null : query.Database);
        var (page, next) = ModelPages.Page(entries, e => (e.Kind, e.Name, e.Id), query.Cursor, query.Limit);
        var items = page.Select(e => e.Project()).ToList();
        if (state.Model.Calculated is not { } host || host.DeclaredKinds is not { Count: > 0 } kinds)
            return new ResolvedPage(items, next, []);
        // The page's objects and the sub-objects their records list, evaluated for them only: a page never evaluates the whole model.
        var values = new SortedDictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        var failures = new List<Diagnostic>();
        foreach (var entry in page)
        {
            foreach (var value in ResolvedKinds.ObjectsUnder(state.Model, entry.Source, kinds))
            {
                var site = ResolvedKinds.SiteOf(value, snapshot);
                if (!values.ContainsKey(site) && Evaluate(host, value, null, failures, ct) is { Count: > 0 } found)
                    values[site] = found;
            }
        }

        return new ResolvedPage(items, next, Outcomes.Sort(failures.Distinct())) { Calculated = values };
    }

    /// <summary>The most ids <see cref="GetCalculatedAsync"/> takes.</summary>
    public const int MaxCalculatedIds = 100;

    /// <summary>
    /// The calculated values (next-phase-2-design.md section 5.5) of the objects the ids name, evaluated for them and whatever they read
    /// only: one item per resolved object an id names (an attribute held by several entities gives one item per holder), the declarations
    /// for their kinds, the ids that name nothing and the failures. Reads the store's shared resolution of the snapshot (as
    /// <see cref="GetResolvedAsync"/>), so a value evaluated by a preview or an earlier read is reused. Takes no run lock and writes nothing.
    /// </summary>
    /// <param name="query">The ids and the names.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentException">No id, more than <see cref="MaxCalculatedIds"/>, or an empty id or name.</exception>
    public async Task<CalculatedValueAnswer> GetCalculatedAsync(CalculatedValueQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Ids is not { Count: > 0 and <= MaxCalculatedIds })
            throw new ArgumentException($"ids must hold 1 to {MaxCalculatedIds} ids.", nameof(query));
        if (query.Ids.Any(string.IsNullOrEmpty) || query.Names?.Any(string.IsNullOrEmpty) == true)
            throw new ArgumentException("An id or a name is empty.", nameof(query));
        var snapshot = await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var state = await ReadableAsync(snapshot, ct).ConfigureAwait(false);
        if (state.Model is not { } model)
            return new CalculatedValueAnswer([], [], [], state.Errors);
        var host = model.Calculated;
        HashSet<string>? names = query.Names is null ? null : new(query.Names, StringComparer.Ordinal);
        ILookup<string, RAnnotated> declared = (host?.Objects ?? []).ToLookup(o => o.Id, StringComparer.Ordinal);
        var items = new List<CalculatedValueItem>();
        var missing = new List<string>();
        var failures = new List<Diagnostic>();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in query.Ids.Distinct(StringComparer.Ordinal))
        {
            IEnumerable<IResolvedObject> objects = declared.Contains(id) ? declared[id] : model.Find(id) is { } found ? [found] : [];
            var any = false;
            foreach (var value in objects)
            {
                any = true;
                kinds.Add(value.Kind);
                var site = ResolvedKinds.SiteOf(value, snapshot);
                var holder = site.Length > value.Id.Length && site.EndsWith("/" + value.Id, StringComparison.Ordinal) ? site[..^(value.Id.Length + 1)] : null;
                var calculated = host is not null && value is RAnnotated annotated
                    ? Evaluate(host, annotated, names, failures, ct)
                    : (IReadOnlyDictionary<string, object?>)new SortedDictionary<string, object?>(StringComparer.Ordinal);
                items.Add(new CalculatedValueItem(value.Id, value.Kind, holder, calculated) { Name = NameOf(value) });
            }

            if (!any)
                missing.Add(id);
        }

        var declarations = (host?.Declarations ?? [])
            .Where(d => d.Kinds.Any(kinds.Contains) && (names is null || names.Contains(d.Name))).ToList();
        return new CalculatedValueAnswer(items, missing, declarations, Outcomes.Sort(failures.Distinct()));
    }

    /// <summary>The values of one object (the names given only, when given), by name; a failure goes to <paramref name="failures"/>.</summary>
    private static SortedDictionary<string, object?> Evaluate(CalculationHost host, RAnnotated value, HashSet<string>? names, List<Diagnostic> failures,
        CancellationToken ct)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in host.NamesOf(value.Kind))
        {
            if (names is not null && !names.Contains(name))
                continue;
            try
            {
                values[name] = host.Read(value, name, null, ct);
            }
            catch (CalculatedValueException e)
            {
                failures.Add(e.Diagnostic);
            }
        }

        return values;
    }

    /// <summary>
    /// The snapshot's resolved model from the store's shared resolution (<see cref="ModelStore.ResolvedAsync"/>: validated and resolved
    /// once per snapshot), or the validation or resolution errors that leave it out.
    /// </summary>
    private async Task<ResolvedState> ReadableAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        var shared = await _store.ResolvedAsync(snapshot, ct).ConfigureAwait(false);
        if (shared.Model is not { } resolved)
            return new ResolvedState(null, Outcomes.Sort(shared.Report.Diagnostics.Where(Outcomes.BlocksResolution)));
        if (resolved.Diagnostics.Any(Outcomes.IsInvalid))
            return new ResolvedState(null, Outcomes.Sort(resolved.Diagnostics.Where(Outcomes.IsInvalid)));
        return new ResolvedState(resolved, []);
    }

    /// <summary>A snapshot's resolved model, or the errors that prevented it.</summary>
    private sealed record ResolvedState(ResolvedModel? Model, IReadOnlyList<Diagnostic> Errors);
}
