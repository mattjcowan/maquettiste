using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine;

/// <summary>
/// What runs, in order, when a verb happens in a lifecycle (trigger-points-design.md decision 36 and section 9): one engine call, shared by the
/// editor API (<c>GET /api/processes/{id}/trigger-sequence</c>), the MCP tool <c>get_trigger_sequence</c> and the CLI verb
/// <c>process trigger-sequence</c>.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>
    /// The steps of the fixed order for one verb in one lifecycle, from the one evaluator (<see cref="TriggerSequences"/>). It reads the lifecycle
    /// and the lifecycles that govern its subjects from the snapshot; it never resolves the model, and writes nothing. A verb's sequence is short,
    /// so the answer is one page.
    /// </summary>
    /// <param name="processId">The lifecycle's id.</param>
    /// <param name="verb">The verb, by key.</param>
    /// <param name="filter">The place to narrow the answer to.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The sequence; <see cref="ProcessCallStatus.NotFound"/> for no such process, <see cref="ProcessCallStatus.BadRequest"/> for a
    /// process that is no lifecycle, a verb missing or unknown, or a state, entity or transition the lifecycle doesn't have.</returns>
    public async Task<ProcessCall<TriggerSequence>> GetTriggerSequenceAsync(string processId, string? verb, TriggerSequenceFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(filter);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Process>(processId) is not { } process)
            return ProcessCall<TriggerSequence>.Fail(ProcessCallStatus.NotFound, $"No process has the id {processId}.");
        if (process.Use != ProcessUse.Lifecycle)
            return Bad($"Process '{process.Name}' is no lifecycle: only lifecycles hold trigger points. Name a lifecycle.");
        if (string.IsNullOrEmpty(verb))
            return Bad("verb is required: a verb key, such as promote or modify.");
        if (BuiltInVerbs.Get(verb) is null && snapshot.Verbs?.Verbs.Any(v => string.Equals(v.Key, verb, StringComparison.Ordinal)) != true)
            return Bad($"verb '{verb}' is no built-in verb and no verb of the project's vocabulary.");
        var chart = ProcessLibraries.Chart(snapshot, process);
        if (filter.State is { } state && !chart.ById.ContainsKey(state))
            return Bad($"state {state} is not a state of lifecycle '{process.Name}'.");
        if (filter.Transition is { } transition && !chart.Transitions.Any(t => string.Equals(t.Transition.Id, transition, StringComparison.Ordinal)))
            return Bad($"transition {transition} is not a transition of lifecycle '{process.Name}'.");
        if (filter.Entity is { } entityId && (snapshot.Get<Entity>(entityId) is not { } entity || !SubjectCoverage.Governs(snapshot, process, entity)))
            return Bad($"entity {entityId} is not an entity lifecycle '{process.Name}' governs.");
        return ProcessCall<TriggerSequence>.Ok(TriggerSequences.Of(snapshot).Describe(process, verb, filter));

        static ProcessCall<TriggerSequence> Bad(string detail) => ProcessCall<TriggerSequence>.Fail(ProcessCallStatus.BadRequest, detail);
    }
}
