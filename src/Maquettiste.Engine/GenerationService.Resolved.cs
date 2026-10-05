using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

public sealed partial class GenerationService
{
    /// <summary>
    /// One page of the resolved model as flat records (<see cref="ResolvedRecords"/>), by (kind, name, id): what templates read, as data
    /// for external systems. The model is validated and resolved once per snapshot (the store keeps the last snapshot's resolved model, so paging
    /// through it resolves once); a model with errors returns no records and the errors. Takes no run lock and writes nothing.
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
        return new ResolvedPage([.. page.Select(e => e.Project())], next, []);
    }

    /// <summary>
    /// The snapshot's resolved model from the store's shared resolution (<see cref="ModelStore.ResolvedAsync"/>: validated and resolved
    /// once per snapshot), or the validation or resolution errors that leave it out.
    /// </summary>
    private async Task<ResolvedState> ReadableAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        var shared = await _store.ResolvedAsync(snapshot, ct).ConfigureAwait(false);
        if (shared.Model is not { } resolved)
            return new ResolvedState(null, Outcomes.Sort(shared.Report.Diagnostics.Where(Outcomes.IsInvalid)));
        if (resolved.Diagnostics.Any(Outcomes.IsInvalid))
            return new ResolvedState(null, Outcomes.Sort(resolved.Diagnostics.Where(Outcomes.IsInvalid)));
        return new ResolvedState(resolved, []);
    }

    /// <summary>A snapshot's resolved model, or the errors that prevented it.</summary>
    private sealed record ResolvedState(ResolvedModel? Model, IReadOnlyList<Diagnostic> Errors);
}
