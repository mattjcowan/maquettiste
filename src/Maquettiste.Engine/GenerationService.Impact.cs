using Maquettiste.Engine.Generation;

namespace Maquettiste.Engine;

/// <summary>Impact before apply (next-phase-2-design.md section 6.1): a batch's dry run with the generated files it touches.</summary>
public sealed partial class GenerationService
{
    /// <summary>
    /// A dry run of the batch (<see cref="ModelStore.PreviewBatchAsync"/>) with the impact's packs filled: per enabled pack, the units
    /// whose stored inputs the change touches, with their output files. The store runs the dry run once; the packs come from the
    /// unit states alone, so nothing is resolved, planned or rendered and no file is written.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The dry run; when it would save, its <see cref="BatchPreview.Impact"/> has <see cref="BatchImpact.Packs"/>.</returns>
    public async Task<BatchPreview> PreviewBatchAsync(ModelBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await _store.GetSnapshotAsync(ct).ConfigureAwait(false);
        var run = await _store.PreviewBatchCoreAsync(batch, ct).ConfigureAwait(false);
        if (run.Preview.Impact is not { } impact || run.Before is null || run.Candidate is null)
            return run.Preview;
        var packs = await OutputImpact.PacksAsync(_services, _options, run, ct).ConfigureAwait(false);
        return run.Preview with { Impact = impact with { Packs = packs } };
    }
}
