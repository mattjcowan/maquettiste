using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// Stage 8: decisions, atomic writes, orphans, manifests, unit state and journal (W7; engine-design.md sections 12.3 and 12.4). Each
/// call runs one <see cref="WriteRun"/>; the writer itself keeps no state between runs.
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="paths">The path policy of the run (built from the project settings).</param>
/// <param name="manifests">The manifest store.</param>
/// <param name="diffs">The diff generator.</param>
internal sealed class OutputWriter(EngineOptions options, IOutputPathPolicy paths, IManifestStore manifests, IDiffGenerator diffs) : IOutputWriter
{
    /// <summary>The bounded write queue's capacity, in files (engine-design.md section 4.3).</summary>
    internal const int QueueCapacity = 256;

    /// <summary>The number of tasks draining the queue: <c>min(jobs, 8)</c>.</summary>
    internal int WorkerCount => Math.Clamp(options.EffectiveParallelism, 1, 8);

    /// <inheritdoc/>
    public Task<WriteSummary> WriteAsync(IAsyncEnumerable<ProcessedUnit> units, WriteContext context, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(context);
        var run = new WriteRun(options, paths, manifests, diffs, context, progress, WorkerCount, QueueCapacity);
        return run.RunAsync(units, ct);
    }
}
