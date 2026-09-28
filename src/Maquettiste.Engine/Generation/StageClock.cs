using System.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// Collects per-stage timings for one run (<see cref="StageTiming"/>): wall time from a stage's first start to its last stop, busy
/// time as the sum of measured work (wall time for stages that are not measured per item), and items. Thread-safe. Timings are
/// diagnostics for the caller; they never reach generated output or a hash.
/// </summary>
internal sealed class StageClock
{
    private readonly Lock _gate = new();
    private readonly Dictionary<PipelineStage, Entry> _entries = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>The elapsed time since the run started.</summary>
    public TimeSpan Now => _clock.Elapsed;

    /// <summary>Records a stage span.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="start">When it started (<see cref="Now"/>).</param>
    /// <param name="items">Items processed.</param>
    /// <param name="busy">Busy time; <see langword="null"/> counts the span.</param>
    public void Record(PipelineStage stage, TimeSpan start, int items, TimeSpan? busy = null)
    {
        var end = Now;
        lock (_gate)
        {
            var entry = _entries.TryGetValue(stage, out var e) ? e : new Entry(start, end, TimeSpan.Zero, 0);
            _entries[stage] = new Entry(Min(entry.Start, start), Max(entry.End, end), entry.Busy + (busy ?? end - start), entry.Items + items);
        }
    }

    /// <summary>Adds busy time and items to a stage without moving its span.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="start">When the item started.</param>
    /// <param name="busy">The item's busy time.</param>
    public void Item(PipelineStage stage, TimeSpan start, TimeSpan busy) => Record(stage, start, 1, busy);

    /// <summary>Returns the timings in stage order.</summary>
    /// <returns>The timings.</returns>
    public IReadOnlyList<StageTiming> Timings()
    {
        lock (_gate)
            return [.. _entries.OrderBy(e => e.Key).Select(e => new StageTiming(e.Key, e.Value.End - e.Value.Start, e.Value.Busy, e.Value.Items))];
    }

    /// <summary>Times an awaited stage.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="stage">The stage.</param>
    /// <param name="progress">Progress; the stage's start and end are reported.</param>
    /// <param name="work">The work.</param>
    /// <param name="items">Items processed, from the result.</param>
    /// <returns>The result.</returns>
    public async Task<T> TimeAsync<T>(PipelineStage stage, IProgress<ProgressUpdate>? progress, Func<Task<T>> work, Func<T, int> items)
    {
        progress?.Report(new ProgressUpdate(stage, 0, 0, null, null));
        var start = Now;
        var result = await work().ConfigureAwait(false);
        var count = items(result);
        Record(stage, start, count);
        progress?.Report(new ProgressUpdate(stage, count, count, null, null));
        return result;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private readonly record struct Entry(TimeSpan Start, TimeSpan End, TimeSpan Busy, int Items);
}
