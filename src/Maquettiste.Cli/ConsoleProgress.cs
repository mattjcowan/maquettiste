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

    private readonly ProgressStyle _style;
    private readonly TextWriter _error;
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<PipelineStage, StageState> _stages = [];
    private TimeSpan? _lastWrite;
    private PipelineStage? _current;
    private int _lineLength;

    /// <summary>Creates the reporter.</summary>
    /// <param name="style">The style.</param>
    /// <param name="error">stderr.</param>
    public ConsoleProgress(ProgressStyle style, TextWriter error)
    {
        _style = style;
        _error = error;
    }

    /// <summary>The largest number of files the writer reported (files compared with disk), for the summary.</summary>
    public int FilesCompared { get; private set; }

    /// <summary>Returns <see cref="FilesCompared"/> and resets it to 0, so each run of a watch session counts only its own files.</summary>
    /// <returns>The files compared since the last call.</returns>
    public int TakeFilesCompared()
    {
        lock (_gate)
        {
            var value = FilesCompared;
            FilesCompared = 0;
            return value;
        }
    }

    /// <inheritdoc/>
    public void Report(ProgressUpdate value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            var now = _clock.Elapsed;
            if (value.Stage == PipelineStage.Write)
                FilesCompared = Math.Max(FilesCompared, value.Done);
            var first = !_stages.TryGetValue(value.Stage, out var state);
            if (first)
                _stages[value.Stage] = state = new StageState(now);
            state!.Last = now;
            state.Done = Math.Max(state.Done, value.Done);
            state.Total = Math.Max(state.Total, value.Total);
            state.Ended = false;

            switch (_style)
            {
                case ProgressStyle.Plain:
                    Plain(value.Stage, first);
                    break;
                case ProgressStyle.Terminal:
                    if (Due(now) || value.Done == value.Total)
                    {
                        _lastWrite = now;
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
                        _lastWrite = now;
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
                foreach (var (stage, state) in _stages.OrderBy(s => s.Key))
                    EndLine(stage, state);
                _current = null;
            }
            else if (_style == ProgressStyle.Terminal && _lineLength > 0)
            {
                _error.WriteLine();
                _lineLength = 0;
            }

            _stages.Clear();
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

    private bool Due(TimeSpan now) => _lastWrite is not { } last || now - last >= Interval;

    private void Plain(PipelineStage stage, bool first)
    {
        // Stages 1 to 5 run one after another (the pack loader reports as plan before resolve); 6 to 8 stream together, so their
        // end lines wait for the end of the run.
        if (_current is { } current && current != stage && current < PipelineStage.Render && _stages.TryGetValue(current, out var previous))
            EndLine(current, previous);
        if (first || _current != stage)
        {
            if (first || stage < PipelineStage.Render)
                _error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{(int)stage}/8 {Name(stage)}] started"));
        }

        _current = stage;
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

    private sealed class StageState(TimeSpan start)
    {
        public TimeSpan Start { get; } = start;

        public TimeSpan Last { get; set; } = start;

        public int Done { get; set; }

        public int Total { get; set; }

        public bool Ended { get; set; }
    }
}
