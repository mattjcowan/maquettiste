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
    /// process that is no lifecycle, a verb missing or unknown, a state, entity or transition the lifecycle doesn't have, or an attribute or end
    /// the model doesn't have.</returns>
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
        if (filter.Attributes.FirstOrDefault(a => !snapshot.TryGetEntry(a, out var found) || found.Kind != "attribute") is { } attribute)
            return Bad($"attribute {attribute} is not an attribute of the model.");
        if (filter.End is { } end && (!snapshot.TryGetEntry(end, out var endEntry) || endEntry.Kind != "end"))
            return Bad($"end {end} is not a relation end of the model.");
        return ProcessCall<TriggerSequence>.Ok(TriggerSequences.Of(snapshot).Describe(process, verb, filter));

        static ProcessCall<TriggerSequence> Bad(string detail) => ProcessCall<TriggerSequence>.Fail(ProcessCallStatus.BadRequest, detail);
    }

    /// <summary>
    /// What runs, in order, when an object, link or own verb happens to an object of an entity with no lifecycle acting (trigger-points-design.md
    /// decisions 33, 34 and 36; <c>GET /api/model/entities/{id}/trigger-sequence</c>, and <c>get_trigger_sequence</c> given an entity and no
    /// process): the named trigger points that cover it first at each moment, then every lifecycle that governs it. It reads the snapshot; it
    /// never resolves the model, and writes nothing. One page.
    /// </summary>
    /// <param name="entityId">The entity's id.</param>
    /// <param name="verb">The verb, by key: an object, link or own verb.</param>
    /// <param name="filter">The place: its attributes and end; its state, entity and transition are not read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The sequence, with no lifecycle; <see cref="ProcessCallStatus.NotFound"/> for no such entity, <see cref="ProcessCallStatus.BadRequest"/>
    /// for a verb missing or unknown, a move, signature or task verb, or an attribute or end the model doesn't have.</returns>
    public async Task<ProcessCall<TriggerSequence>> GetEntityTriggerSequenceAsync(string entityId, string? verb, TriggerSequenceFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        ArgumentNullException.ThrowIfNull(filter);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<Entity>(entityId) is not { } entity)
            return ProcessCall<TriggerSequence>.Fail(ProcessCallStatus.NotFound, $"No entity has the id {entityId}.");
        if (string.IsNullOrEmpty(verb))
            return Bad("verb is required: an object, link or own verb key, such as create or modify.");
        if (BuiltInVerbs.Get(verb) is null && snapshot.Verbs?.Verbs.Any(v => string.Equals(v.Key, verb, StringComparison.Ordinal)) != true)
            return Bad($"verb '{verb}' is no built-in verb and no verb of the project's vocabulary.");
        if (filter.Attributes.FirstOrDefault(a => !snapshot.TryGetEntry(a, out var found) || found.Kind != "attribute") is { } attribute)
            return Bad($"attribute {attribute} is not an attribute of the model.");
        if (filter.End is { } end && (!snapshot.TryGetEntry(end, out var endEntry) || endEntry.Kind != "end"))
            return Bad($"end {end} is not a relation end of the model.");
        return TriggerSequences.Of(snapshot).Describe(entity, verb, filter) is { } sequence
            ? ProcessCall<TriggerSequence>.Ok(sequence)
            : Bad($"verb '{verb}' is a move, signature or task verb, which happens in one lifecycle; name the lifecycle as process.");

        static ProcessCall<TriggerSequence> Bad(string detail) => ProcessCall<TriggerSequence>.Fail(ProcessCallStatus.BadRequest, detail);
    }

    /// <summary>
    /// The entities a named trigger point runs for (trigger-points-design.md decision 33; <c>GET /api/model/trigger-points/{id}/covers</c>): every
    /// entity a subject covers (an entity with its derived entities; a stereotype's carriers, those a package gives it by default and those an
    /// implied stereotype brings included, with their derived entities), less <c>exceptEntities</c> and theirs, narrowed by <c>entities</c>, in
    /// name order: the same list as the record's <c>covers</c>. It reads the snapshot; it never resolves the model, and writes nothing.
    /// </summary>
    /// <param name="id">The named trigger point's id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entity ids; <see cref="ProcessCallStatus.NotFound"/> when the id is not a named trigger point.</returns>
    public async Task<ProcessCall<IReadOnlyList<string>>> GetTriggerPointCoversAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (snapshot.Get<TriggerPoint>(id) is not { } point)
            return ProcessCall<IReadOnlyList<string>>.Fail(ProcessCallStatus.NotFound, $"No named trigger point has the id {id}.");
        return ProcessCall<IReadOnlyList<string>>.Ok([.. TriggerSequences.Of(snapshot).Covers(point).Select(e => e.Id)]);
    }
}
