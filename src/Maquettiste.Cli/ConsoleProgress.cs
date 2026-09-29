using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli;

/// <summary>
/// Console progress on stderr (engine-design.md section 16). <see cref="ProgressStyle.Terminal"/> rewrites one line at most 10 times
/// a second (<c>[6/8 render] 41,200/100,480 src/Generated/Billing/Invoice.g.cs</c>); <see cref="ProgressStyle.Plain"/> writes a line
/// when each stage starts and when it ends (count, elapsed); <see cref="ProgressStyle.Json"/> writes one <see cref="ProgressUpdate"/>
/// JSON line per update, at most 10 a second (the last update of a stage always). Thread-safe: stages report from several threads.
/// Elapsed times go to stderr only; they never reach output or a hash.
/// </summary>
internal sealed class ConsoleProgress : IProgress<ProgressUpdate>
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private static readonly int StageSlots = Enum.GetValues<PipelineStage>().Max(s => (int)s) + 1;

    private readonly ProgressStyle _style;
    private readonly bool _hideWrite;

    /// <summary>Plain style: the stages whose start line was written, by stage value (under the lock).</summary>
    private readonly bool[] _shown = new bool[StageSlots];
    private readonly TextWriter _error;
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Stage states by stage value; a slot is set once, under the lock, and read without it.</summary>
    private readonly StageState?[] _stages = new StageState?[StageSlots];

    /// <summary>When a line was last written (<see cref="TimeSpan.Ticks"/> of the clock), or -1.</summary>
    private long _lastWrite = -1;

    /// <summary>The current stage's value (plain style), or 0.</summary>
    private int _current;
    private int _lineLength;
    private int _filesCompared;

    /// <summary>Creates the reporter.</summary>
    /// <param name="style">The style.</param>
    /// <param name="error">stderr.</param>
    /// <param name="hideWrite">Leaves the write stage off the display (<c>generate --check</c> compares files and writes none); its file
    /// count still reaches <see cref="FilesCompared"/>.</param>
    public ConsoleProgress(ProgressStyle style, TextWriter error, bool hideWrite = false)
    {
        _style = style;
        _error = error;
        _hideWrite = hideWrite;
    }

    /// <summary>The largest number of files the writer reported (files compared with disk), for the summary.</summary>
    public int FilesCompared => Volatile.Read(ref _filesCompared);

    /// <summary>Returns <see cref="FilesCompared"/> and resets it to 0, so each run of a watch session counts only its own files.</summary>
    /// <returns>The files compared since the last call.</returns>
    public int TakeFilesCompared() => Interlocked.Exchange(ref _filesCompared, 0);

    /// <inheritdoc/>
    /// <remarks>
    /// Stages report from several threads, often once per file. An update that changes nothing on screen (the stage already
    /// started and is still current, it is not the stage's last update, and no line is due) only folds its counts into the stage
    /// state, without the lock; everything else takes the lock as before, so the output is the same.
    /// </remarks>
    public void Report(ProgressUpdate value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Stage == PipelineStage.Write)
            Max(ref _filesCompared, value.Done);
        if (_style == ProgressStyle.None || (_hideWrite && value.Stage == PipelineStage.Write))
            return;

        var now = _clock.Elapsed;
        var slot = (int)value.Stage;
        if (slot >= 0 && slot < StageSlots && Volatile.Read(ref _stages[slot]) is { } known && !known.Ended && value.Done != value.Total
            && (_style == ProgressStyle.Plain ? Volatile.Read(ref _current) == slot : !Due(now)))
        {
            known.Fold(now, value.Done, value.Total);
            return;
        }

        lock (_gate)
        {
            var first = slot < 0 || slot >= StageSlots || _stages[slot] is null;
            var state = first ? new StageState(now) : _stages[slot]!;
            if (first && slot >= 0 && slot < StageSlots)
                Volatile.Write(ref _stages[slot], state);
            state.Fold(now, value.Done, value.Total);

            switch (_style)
            {
                case ProgressStyle.Plain:
                    Plain(value.Stage);
                    break;
                case ProgressStyle.Terminal:
                    if (Due(now) || value.Done == value.Total)
                    {
                        Volatile.Write(ref _lastWrite, now.Ticks);
                        var text = string.Create(CultureInfo.InvariantCulture,
                            $"[{(int)value.Stage}/8 {Name(value.Stage)}] {value.Done:N0}/{value.Total:N0} {value.CurrentPath ?? value.Pack ?? ""}");
                        var pad = Math.Max(0, _lineLength - text.Length);
                        _error.Write("\r" + text + new string(' ', pad));
                        _lineLength = text.Length;
                    }

                    break;
                case ProgressStyle.Json:
                    if (Due(now) || value.Done == value.Total || first)
                    {
                        Volatile.Write(ref _lastWrite, now.Ticks);
                        _error.WriteLine(Json(value));
                    }

                    break;
            }
        }
    }

    /// <summary>Ends the progress display: the pending stage end lines (plain) or a newline after the rewritten line (terminal).</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_style == ProgressStyle.Plain)
            {
                for (var slot = 0; slot < StageSlots; slot++)
                {
                    if (_stages[slot] is { } state)
                    {
                        StartLine(slot);
                        EndLine((PipelineStage)slot, state);
                    }
                }

                Volatile.Write(ref _current, 0);
                Array.Clear(_shown);
            }
            else if (_style == ProgressStyle.Terminal && _lineLength > 0)
            {
                _error.WriteLine();
                _lineLength = 0;
            }

            for (var slot = 0; slot < StageSlots; slot++)
                Volatile.Write(ref _stages[slot], null);
        }
    }

    /// <summary>The stage names used in progress lines.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The lowercase name.</returns>
    public static string Name(PipelineStage stage) => stage switch
    {
        PipelineStage.Load => "load",
        PipelineStage.Validate => "validate",
        PipelineStage.Resolve => "resolve",
        PipelineStage.Plan => "plan",
        PipelineStage.Skip => "skip",
        PipelineStage.Render => "render",
        PipelineStage.PostProcess => "post-process",
        PipelineStage.Write => "write",
        _ => stage.ToString().ToLowerInvariant(),
    };

    private bool Due(TimeSpan now) => Volatile.Read(ref _lastWrite) is var last && (last < 0 || now.Ticks - last >= Interval.Ticks);

    private static void Max(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen)
        {
            var found = Interlocked.CompareExchange(ref target, value, seen);
            if (found == seen)
                return;
            seen = found;
        }
    }

    private void Plain(PipelineStage stage)
    {
        // Every stage gets one start line and one end line, in stage order. Stages 1 to 5 run one after another, but the pack
        // loader reports as plan before resolve starts: a stage 2 to 5 whose predecessor has not reported yet is held (its counts
        // fold in, nothing is written) until it reports again after its predecessor, or until the run ends. Stages 6 to 8 stream
        // together, so their end lines wait for the end of the run.
        var slot = (int)stage;
        if (slot > 1 && stage < PipelineStage.Render && _stages[slot - 1] is null)
            return;
        var current = Volatile.Read(ref _current);
        if (current != 0 && current != slot && (PipelineStage)current < PipelineStage.Render && _stages[current] is { } previous)
            EndLine((PipelineStage)current, previous);

        // A held stage below this one that never got its lines gets them now, in order.
        for (var lower = 1; lower < slot && lower < (int)PipelineStage.Render; lower++)
        {
            if (lower != current && _stages[lower] is { } held && !_shown[lower])
            {
                StartLine(lower);
                EndLine((PipelineStage)lower, held);
            }
        }

        StartLine(slot);
        Volatile.Write(ref _current, slot);
    }

    private void StartLine(int slot)
    {
        if (_shown[slot])
            return;
        _shown[slot] = true;
        _error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{slot}/8 {Name((PipelineStage)slot)}] started"));
    }

    private void EndLine(PipelineStage stage, StageState state)
    {
        if (state.Ended)
            return;
        state.Ended = true;
        var elapsed = state.Last - state.Start;
        _error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{(int)stage}/8 {Name(stage)}] done: {state.Done:N0} in {elapsed.TotalSeconds:0.000} s"));
    }

    private static string Json(ProgressUpdate value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("stage", Name(value.Stage));
            writer.WriteNumber("done", value.Done);
            writer.WriteNumber("total", value.Total);
            if (value.CurrentPath is not null)
                writer.WriteString("currentPath", value.CurrentPath);
            if (value.Pack is not null)
                writer.WriteString("pack", value.Pack);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>One stage's counts; <see cref="Fold"/> is safe without the lock (maxima only grow).</summary>
    private sealed class StageState(TimeSpan start)
    {
        private long _last = start.Ticks;
        private int _done;
        private int _total;
        private volatile bool _ended;

        public TimeSpan Start { get; } = start;

        public TimeSpan Last => TimeSpan.FromTicks(Volatile.Read(ref _last));

        public int Done => Volatile.Read(ref _done);

        public int Total => Volatile.Read(ref _total);

        public bool Ended
        {
            get => _ended;
            set => _ended = value;
        }

        public void Fold(TimeSpan now, int done, int total)
        {
            MaxLong(ref _last, now.Ticks);
            Max(ref _done, done);
            Max(ref _total, total);
        }

        private static void MaxLong(ref long target, long value)
        {
            var seen = Volatile.Read(ref target);
            while (value > seen)
            {
                var found = Interlocked.CompareExchange(ref target, value, seen);
                if (found == seen)
                    return;
                seen = found;
            }
        }
    }
}
