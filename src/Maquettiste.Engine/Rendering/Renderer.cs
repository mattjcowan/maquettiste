using System.Runtime.CompilerServices;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Scriban.Runtime;

namespace Maquettiste.Engine.Rendering;

/// <summary>
/// Stage 6: renders units with Scriban and the tracking context (W5; engine-design.md section 9). One renderer per run
/// (<c>EngineServices.CreateRenderer()</c>), over a fresh <see cref="ITemplateCache"/>.
/// <para><see cref="RenderAsync"/> renders units on <see cref="RenderContext.MaxDegreeOfParallelism"/> workers (0: the engine
/// options' parallelism) and streams them in input order through a bounded reorder window, so memory stays flat. Every unit renders
/// synchronously on one worker thread with its own <see cref="TrackingTemplateContext"/> and, when its pack has scripts, one
/// sandbox leased from the pack's pool (one pool per pack and call, sized to the workers, disposed when the enumeration ends).
/// A unit that fails returns no files, <see cref="RenderedUnit.Failed"/> and its diagnostics; cancellation throws
/// <see cref="OperationCanceledException"/>.</para>
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="templates">The template cache.</param>
internal sealed class Renderer(EngineOptions options, ITemplateCache templates) : IRenderer
{
    private readonly TemplateMemberCatalog _catalog = new();
    private readonly Lazy<ScriptObject> _builtins = new(BuiltinHelpers.CreateBuiltins, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The template cache.</summary>
    internal ITemplateCache Templates => templates;

    /// <summary>The reflection catalog shared by the run's units.</summary>
    internal TemplateMemberCatalog Catalog => _catalog;

    /// <summary>The shared, read-only builtin object.</summary>
    internal ScriptObject Builtins => _builtins.Value;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RenderedUnit> RenderAsync(IReadOnlyList<PlannedUnit> units, RenderContext context, IProgress<ProgressUpdate>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (units.Count == 0)
            yield break;

        var parallelism = context.MaxDegreeOfParallelism > 0 ? context.MaxDegreeOfParallelism : options.EffectiveParallelism;
        var workers = Math.Clamp(parallelism, 1, units.Count);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var run = new RenderRun(this, context, workers, cts.Token);
        using var window = new SemaphoreSlim(workers * 4);
        var results = new TaskCompletionSource<RenderedUnit>[units.Count];
        for (var i = 0; i < results.Length; i++)
            results[i] = new TaskCompletionSource<RenderedUnit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = -1;
        var tasks = new Task[workers];
        for (var w = 0; w < workers; w++)
            tasks[w] = Task.Run(() => WorkAsync(units, run, results, window, () => Interlocked.Increment(ref next), cts.Token), CancellationToken.None);

        try
        {
            for (var i = 0; i < units.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var rendered = await results[i].Task.WaitAsync(cts.Token).ConfigureAwait(false);
                // WaitAsync returns an already-completed result even when the token was cancelled meanwhile; a unit rendered while
                // the cancel was arriving may hold failures the cancel caused, so it is never yielded.
                cts.Token.ThrowIfCancellationRequested();
                results[i] = null!; // the consumer owns the unit now; keep memory flat over large runs
                window.Release();
                progress?.Report(new ProgressUpdate(PipelineStage.Render, i + 1, units.Count,
                    rendered.Files.Count > 0 ? rendered.Files[0].Path : rendered.Unit.Key, rendered.Unit.Pack.Name));
                yield return rendered;
            }
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Workers stop on the linked token; their units were never consumed.
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <inheritdoc/>
    public async Task<RenderedUnit> RenderOneAsync(PlannedUnit unit, RenderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        using var run = new RenderRun(this, context, 1, ct);
        return await Task.Run(() => run.Render(unit), ct).ConfigureAwait(false);
    }

    /// <summary>Normalizes CRLF and CR to LF.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with LF line endings.</returns>
    public static string NormalizeLineEndings(string text) =>
        text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;

    private static async Task WorkAsync(IReadOnlyList<PlannedUnit> units, RenderRun run, TaskCompletionSource<RenderedUnit>[] results,
        SemaphoreSlim window, Func<int> take, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await window.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var index = take();
            if (index >= units.Count)
            {
                window.Release();
                return;
            }

            try
            {
                results[index].TrySetResult(run.Render(units[index]));
            }
            catch (OperationCanceledException ex)
            {
                results[index].TrySetCanceled(ex.CancellationToken);
                return;
            }
            catch (Exception ex)
            {
                results[index].TrySetException(ex);
            }
        }
    }
}
