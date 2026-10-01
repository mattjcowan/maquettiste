using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

public sealed partial class GenerationService
{
    private readonly Lock _resolvedGate = new();
    private (ModelSnapshot Snapshot, Task<ResolvedState> Task)? _resolved;

    /// <summary>
    /// One page of the resolved model as flat records (<see cref="ResolvedRecords"/>), by (kind, name, id): what templates read, as data
    /// for external systems. The model is validated and resolved once per snapshot (the last snapshot's resolved model is kept, so paging
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
        Task<ResolvedState> task;
        lock (_resolvedGate)
        {
            if (_resolved is { } cached && ReferenceEquals(cached.Snapshot, snapshot) && !cached.Task.IsFaulted && !cached.Task.IsCanceled)
            {
                task = cached.Task;
            }
            else
            {
                // The shared work runs without the caller's token, so one cancelled request does not fail the others waiting on it.
                task = Task.Run(() => ResolveForReadAsync(snapshot), CancellationToken.None);
                _resolved = (snapshot, task);
            }
        }

        var state = await task.WaitAsync(ct).ConfigureAwait(false);
        if (state.Model is null)
            return new ResolvedPage([], null, state.Errors);
        var entries = ResolvedRecords.Entries(state.Model, query.Scope, string.IsNullOrEmpty(query.Database) ? null : query.Database);
        var (page, next) = ModelPages.Page(entries, e => (e.Kind, e.Name, e.Id), query.Cursor, query.Limit);
        return new ResolvedPage([.. page.Select(e => e.Project())], next, []);
    }

    private async Task<ResolvedState> ResolveForReadAsync(ModelSnapshot snapshot)
    {
        var report = await _services.Validator.ValidateAsync(snapshot, new ValidationScope(), null, CancellationToken.None).ConfigureAwait(false);
        if (report.HasErrors)
            return new ResolvedState(null, Outcomes.Sort(report.Diagnostics.Where(Outcomes.IsInvalid)));
        var resolved = await _services.Resolver.ResolveAsync(snapshot, null, CancellationToken.None).ConfigureAwait(false);
        if (resolved.Diagnostics.Any(Outcomes.IsInvalid))
            return new ResolvedState(null, Outcomes.Sort(resolved.Diagnostics.Where(Outcomes.IsInvalid)));
        return new ResolvedState(resolved, []);
    }

    /// <summary>A snapshot's resolved model, or the errors that prevented it.</summary>
    private sealed record ResolvedState(ResolvedModel? Model, IReadOnlyList<Diagnostic> Errors);
}
