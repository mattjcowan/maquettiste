using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

public sealed partial class ModelStore
{
    private readonly Lock _validationGate = new();

    /// <summary>The whole-model validation of the current snapshot, per variant: 0 judges seeds with a rows script, 1 leaves them out.</summary>
    private readonly (ModelSnapshot Snapshot, Task<WholeValidation> Task)?[] _validations = new (ModelSnapshot, Task<WholeValidation>)?[2];

    /// <summary>How the current snapshot derives from an earlier validated one, or <see langword="null"/> (guarded by the validation gate).</summary>
    private Lineage? _lineage;

    /// <summary>
    /// How a published snapshot (<see cref="Tip"/>) derives from the one before it by plain element changes (change-driven-edits-design.md
    /// section 4, steps 1 and 2): that snapshot's whole-model validations, the ids of the element documents created, changed or deleted,
    /// and the documents the change reached in it (<see cref="ModelValidator.PrepareIncremental"/>), worked out on publish so the earlier
    /// snapshot itself is not kept. Each validation is let go once it served the tip.
    /// </summary>
    private sealed class Lineage(ModelSnapshot tip, Task<WholeValidation>?[] reports, IncrementalChange change)
    {
        public ModelSnapshot Tip { get; } = tip;

        public Task<WholeValidation>?[] Reports { get; } = reports;

        public IncrementalChange Change { get; } = change;

    }

    /// <summary>Why the last published change needs a whole-model validation (<see cref="ModelValidator.IncrementalFallback"/>), for tests.</summary>
    internal string? LastIncrementalFallback { get; private set; }

    /// <summary>The ids a published snapshot changed since the validated one before it, for tests.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The changed ids, or <see langword="null"/> when its next validation covers the whole model.</returns>
    internal IReadOnlySet<string>? LineageOf(ModelSnapshot snapshot)
    {
        lock (_validationGate)
            return _lineage is { } l && ReferenceEquals(l.Tip, snapshot) && l.Reports.Any(r => r is not null) ? l.Change.Changed : null;
    }

    /// <summary>
    /// Records how a newly published snapshot derives from the one before it (call under the write gate, before the results of other
    /// snapshots are released): when the previous snapshot has a whole-model validation (done or under way) and the change between
    /// them is a plain element change (<see cref="ModelValidator.PrepareIncremental"/>), its validations become the base of the next
    /// incremental one, with the documents the change reached in it. Anything else (no validation to build on, settings, extension files,
    /// scripts, a kind that validates the whole model, a large change) clears it, and the next validation covers the whole model.
    /// </summary>
    private void DeriveLineage(ModelSnapshot? previous, ModelSnapshot current, ChangeSet changes)
    {
        Task<WholeValidation>?[]? reports = null;
        lock (_validationGate)
        {
            _lineage = null;
            if (previous is null || changes.Truncated || _services.Validator is not ModelValidator)
                return;
            for (var i = 0; i < _validations.Length; i++)
            {
                if (_validations[i] is { } held && ReferenceEquals(held.Snapshot, previous) && !held.Task.IsFaulted && !held.Task.IsCanceled)
                    (reports ??= new Task<WholeValidation>?[_validations.Length])[i] = held.Task;
            }
        }

        if (reports is null)
            return;
        var ids = new HashSet<string>(changes.Changed.Count + changes.Deleted.Count, StringComparer.Ordinal);
        foreach (var element in changes.Changed)
            ids.Add(element.Id);
        ids.UnionWith(changes.Deleted);
        if (ModelValidator.PrepareIncremental(previous, current, ids, out var reason) is not { } change)
        {
            LastIncrementalFallback = reason;
            return;
        }

        LastIncrementalFallback = null;
        lock (_validationGate)
            _lineage = new Lineage(current, reports, change);
    }

    /// <summary>Drops the validations kept for any snapshot other than <paramref name="current"/> (on publish, after <see cref="DeriveLineage"/>).</summary>
    private void ReleaseValidationsOfOtherSnapshots(ModelSnapshot current)
    {
        lock (_validationGate)
        {
            for (var i = 0; i < _validations.Length; i++)
            {
                if (_validations[i] is { } held && !ReferenceEquals(held.Snapshot, current))
                    _validations[i] = null;
            }

            if (_lineage is { } lineage && !ReferenceEquals(lineage.Tip, current))
                _lineage = null;
        }
    }

    /// <summary>
    /// A snapshot's whole-model validation, once per snapshot and variant (single flight, without any caller's token; only the current
    /// snapshot's is kept). <paramref name="deferScriptedRows"/> leaves seeds with a rows script unjudged, as previews want
    /// (<see cref="ValidationScope.Previews"/>); without such seeds both variants are the same report and share one validation. When
    /// the snapshot derives from a validated one by plain element changes (<see cref="DeriveLineage"/>), the validation is worked out
    /// from that one's over the documents the change reaches (<see cref="ModelValidator.ValidateWholeAsync"/>), with the same report.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="deferScriptedRows">Whether seeds with a rows script are left unjudged.</param>
    /// <param name="ct">Cancellation of the wait only.</param>
    /// <returns>The validation.</returns>
    internal Task<WholeValidation> WholeValidationAsync(ModelSnapshot snapshot, bool deferScriptedRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // The engine's validator gives one report for both variants when no seed has a rows script; another validator is asked as asked.
        var variant = deferScriptedRows && (_services.Validator is not ModelValidator || snapshot.All<Seed>().Any(static s => s.RowsFrom?.Script is not null)) ? 1 : 0;
        Task<WholeValidation> task;
        lock (_validationGate)
        {
            if (_validations[variant] is { } held && ReferenceEquals(held.Snapshot, snapshot) && !held.Task.IsFaulted && !held.Task.IsCanceled)
                return held.Task.WaitAsync(ct);
            var lineage = _lineage is { } l && ReferenceEquals(l.Tip, snapshot) && l.Reports[variant] is not null ? l : null;
            task = Task.Run(() => ValidateWholeAsync(snapshot, variant == 1, lineage, variant), CancellationToken.None);
            if (IsCurrent(snapshot))
                _validations[variant] = (snapshot, task);
        }

        return task.WaitAsync(ct);
    }

    private async Task<WholeValidation> ValidateWholeAsync(ModelSnapshot snapshot, bool defer, Lineage? lineage, int variant)
    {
        if (_services.Validator is not ModelValidator validator)
        {
            var scope = defer ? ValidationScope.Previews : ValidationScope.All;
            var report = await _services.Validator.ValidateAsync(snapshot, scope, null, CancellationToken.None).ConfigureAwait(false);
            return new WholeValidation(report, new Dictionary<string, IReadOnlyList<Diagnostic>>(), new HashSet<string>(), defer, snapshot.Documents.Count, false);
        }

        IncrementalInput? input = null;
        Task<WholeValidation>? previous;
        lock (_validationGate)
            previous = lineage?.Reports[variant];
        if (lineage is not null && previous is not null)
        {
            try
            {
                input = new IncrementalInput(await previous.ConfigureAwait(false), lineage.Change);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                input = null; // the earlier validation failed: this one validates the whole model
            }
        }

        try
        {
            if (input is not null)
            {
                try
                {
                    return await validator.ValidateWholeAsync(snapshot, defer, input, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // An incremental validation that fails for any reason is never the answer: the whole model is validated instead.
                }
            }

            return await validator.ValidateWholeAsync(snapshot, defer, null, null, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // The earlier validation served this variant: it is let go.
            if (lineage is not null)
            {
                lock (_validationGate)
                    lineage.Reports[variant] = null;
            }
        }
    }

    /// <summary>
    /// The validation stage of a generation run: the store's shared whole-model validation (<see cref="WholeValidationAsync"/>) when the
    /// run validates the snapshot the store serves with the engine's own validator over the same model root, so an edit costs what it
    /// reaches and a validate after the run validates nothing again (unless a seed has a rows script and the run's services keep their own
    /// rows cache); otherwise the run's validator over the whole model.
    /// </summary>
    /// <param name="services">The run's services.</param>
    /// <param name="snapshot">The run's snapshot.</param>
    /// <param name="progress">The run's progress.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report.</returns>
    internal async Task<ValidationReport> ValidateForRunAsync(Generation.EngineServices services, ModelSnapshot snapshot, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        // A seed with a rows script is evaluated through the rows cache of the services that validate it: a run whose services are not the
        // store's keeps validating with its own, so the script runs once for validation and the units that read the rows.
        var sharedRows = ReferenceEquals(services.SeedRows, _services.SeedRows) || !snapshot.All<Seed>().Any(static s => s.RowsFrom?.Script is not null);
        // The store's report answers for the run only when the run would validate the same way: the same services, or the engine's own
        // validator, script sandbox and schema registry on both sides (substitutes in tests validate with their own).
        var sameValidation = ReferenceEquals(services, _services)
            || (services.Validator is ModelValidator && _services.Validator is ModelValidator
                && services.Scripts.GetType() == _services.Scripts.GetType() && services.Schemas.GetType() == _services.Schemas.GetType());
        if (sharedRows && sameValidation && services.Validator is ModelValidator && _services.Validator is ModelValidator && IsCurrent(snapshot)
            && string.Equals(Path.GetFullPath(services.Options.EffectiveModelRoot), Path.GetFullPath(_options.EffectiveModelRoot), StringComparison.Ordinal))
        {
            progress?.Report(new ProgressUpdate(PipelineStage.Validate, 0, snapshot.Documents.Count, null, null));
            var shared = await WholeValidationAsync(snapshot, deferScriptedRows: false, ct).ConfigureAwait(false);
            progress?.Report(new ProgressUpdate(PipelineStage.Validate, snapshot.Documents.Count, snapshot.Documents.Count, null, null));
            return shared.Report;
        }

        return await services.Validator.ValidateAsync(snapshot, ValidationScope.All, progress, ct).ConfigureAwait(false);
    }
}
