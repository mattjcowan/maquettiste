using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine;

/// <summary>
/// Who can do what in a lifecycle (rights-design.md decision 31 and section 8): one engine call, shared by the editor API
/// (<c>GET /api/processes/{id}/rights</c>), the MCP tool <c>get_rights</c> and the CLI verb <c>process rights</c>.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>
    /// One page of a lifecycle's "who can do what". It reads the lifecycle, the named rights it uses, the actors and their memberships, and
    /// the lifecycles that govern its subjects from the snapshot; it never resolves the model, and writes nothing.
    /// </summary>
    /// <param name="processId">The lifecycle's id.</param>
    /// <param name="filter">What to narrow the rows to.</param>
    /// <param name="cursor">The cursor a previous page gave, or <see langword="null"/> for the first page.</param>
    /// <param name="limit">The page size, 1 to <see cref="ModelPages.MaxLimit"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page; <see cref="ProcessCallStatus.NotFound"/> for no such process, <see cref="ProcessCallStatus.BadRequest"/> for a process
    /// that is no lifecycle, an entity that is not one of its subjects, or a cursor no page gave.</returns>
    public async Task<ProcessCall<RightsPage>> GetRightsAsync(string processId, RightsFilter filter, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(filter);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Process>(processId) is not { } process)
            return ProcessCall<RightsPage>.Fail(ProcessCallStatus.NotFound, $"No process has the id {processId}.");
        if (process.Use != ProcessUse.Lifecycle)
            return ProcessCall<RightsPage>.Fail(ProcessCallStatus.BadRequest, $"Process '{process.Name}' is no lifecycle: only lifecycles hold rights. Name a lifecycle.");
        if (limit is < 1 or > ModelPages.MaxLimit)
            return ProcessCall<RightsPage>.Fail(ProcessCallStatus.BadRequest, $"limit must be a whole number from 1 to {ModelPages.MaxLimit}.");
        if (filter.Entity is { } entity && !RightsGrid.Subjects(snapshot, process).Any(e => string.Equals(e.Id, entity, StringComparison.Ordinal)))
            return ProcessCall<RightsPage>.Fail(ProcessCallStatus.BadRequest, $"entity {entity} is not one of the subjects of lifecycle '{process.Name}'.");
        return RightsGrid.Page(snapshot, process, filter, cursor, limit, ct) is { } page
            ? ProcessCall<RightsPage>.Ok(page)
            : ProcessCall<RightsPage>.Fail(ProcessCallStatus.BadRequest, "The cursor is not one this answer gave.");
    }
}
