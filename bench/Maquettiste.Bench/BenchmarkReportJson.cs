using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Bench;

/// <summary>
/// The benchmark report as JSON (the <c>--format json</c> output, the <c>--report</c> file and <c>bench/baseline.json</c>) and as
/// text. Times are seconds rounded to milliseconds; keys are written in a fixed order, numbers in the invariant culture.
/// </summary>
public static class BenchmarkReportJson
{
    /// <summary>The report format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>Writes the report as indented JSON (LF line endings, trailing newline).</summary>
    /// <param name="report">The report.</param>
    /// <returns>The UTF-8 bytes.</returns>
    public static byte[] Write(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteNumber("formatVersion", FormatVersion);
            w.WriteBoolean("passed", report.Passed);
            w.WriteStartObject("model");
            w.WriteNumber("seed", report.Seed);
            w.WriteNumber("entities", report.Entities);
            w.WriteNumber("relations", report.Relations);
            w.WriteNumber("fanout", report.Fanout);
            WriteStrings(w, "packs", report.Packs);
            w.WriteNumber("writeSeconds", Seconds(report.ModelWrite));
            w.WriteEndObject();
            w.WriteStartObject("machine");
            w.WriteNumber("cores", report.MachineCores);
            w.WriteNumber("jobs", report.Jobs);
            w.WriteString("os", report.OperatingSystem);
            w.WriteString("framework", report.Framework);
            w.WriteString("architecture", report.Architecture);
            w.WriteBoolean("serverGc", report.ServerGc);
            w.WriteNumber("gcHeapCount", report.GcHeapCount);
            w.WriteNumber("gcDynamicAdaptation", report.GcDynamicAdaptation);
            WriteStrings(w, "runtimeEnvironment", report.RuntimeEnvironment);
            w.WriteNumber("peakWorkingSetMiB", Math.Round(report.PeakWorkingSetBytes / 1048576.0, 1));
            w.WriteEndObject();
            w.WriteStartArray("budgets");
            foreach (var b in report.Budgets)
            {
                w.WriteStartObject();
                w.WriteString("name", b.Name);
                w.WriteNumber("limit", b.Limit);
                w.WriteNumber("actual", b.Actual);
                w.WriteString("unit", b.Unit);
                w.WriteBoolean("atLeast", b.AtLeast);
                w.WriteBoolean("pass", b.Pass);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("cold");
            w.WriteNumber("files", report.Files);
            w.WriteNumber("unitsRendered", report.ColdUnitsRendered);
            w.WriteNumber("pipelinedTotalSeconds", Seconds(report.ColdTotal));
            w.WriteBoolean("outputsMatch", report.OutputsMatch);
            WriteStages(w, "stages", report.ColdStages);
            WriteStages(w, "pipelinedStages", report.ColdPipelinedStages);
            w.WriteEndObject();
            w.WriteStartObject("incremental");
            w.WriteNumber("totalSeconds", Seconds(report.Incremental));
            w.WriteNumber("unitsRendered", report.IncrementalUnitsRendered);
            w.WriteNumber("unitsSkipped", report.IncrementalUnitsSkipped);
            w.WriteNumber("filesWritten", report.IncrementalFilesWritten);
            w.WriteNumber("gcPauseSeconds", Seconds(report.IncrementalGcPause));
            WriteStages(w, "stages", report.IncrementalStages);
            w.WriteStartObject("freshStore");
            w.WriteNumber("totalSeconds", Seconds(report.IncrementalFreshStore));
            w.WriteNumber("unitsRendered", report.IncrementalFreshStoreUnitsRendered);
            w.WriteNumber("gcPauseSeconds", Seconds(report.IncrementalFreshStoreGcPause));
            WriteStages(w, "stages", report.IncrementalFreshStoreStages);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("check");
            w.WriteNumber("totalSeconds", Seconds(report.CheckTotal));
            w.WriteString("outcome", report.CheckOutcome);
            w.WriteNumber("unitsRendered", report.CheckUnitsRendered);
            w.WriteEndObject();
            if (report.Baseline is not null)
                w.WriteString("baseline", report.Baseline);
            if (report.RegressionGateSkipped is not null)
                w.WriteString("regressionGateSkipped", report.RegressionGateSkipped);
            w.WriteStartArray("regressions");
            foreach (var r in report.Regressions)
            {
                w.WriteStartObject();
                w.WriteString("name", r.Name);
                w.WriteNumber("baseline", r.Baseline);
                w.WriteNumber("actual", r.Actual);
                w.WriteNumber("changePercent", r.ChangePercent);
                w.WriteBoolean("pass", r.Pass);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            WriteStrings(w, "notes", report.Notes);
            w.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Reads the budgets' actual values from a report file (a baseline): budget name → actual.</summary>
    /// <param name="json">The report JSON.</param>
    /// <returns>The values, ordinal by name.</returns>
    /// <exception cref="BenchmarkException">The file is not a report.</exception>
    public static IReadOnlyDictionary<string, double> ReadBudgets(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var values = new SortedDictionary<string, double>(StringComparer.Ordinal);
            foreach (var budget in document.RootElement.GetProperty("budgets").EnumerateArray())
                values[budget.GetProperty("name").GetString()!] = budget.GetProperty("actual").GetDouble();
            return values;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new BenchmarkException("The baseline is not a benchmark report: " + ex.Message, ex);
        }
    }

    /// <summary>Reads the machine section of a report file (a baseline).</summary>
    /// <param name="json">The report JSON.</param>
    /// <returns>The machine; <see cref="BenchmarkMachine.GcHeapCount"/> and <see cref="BenchmarkMachine.RuntimeEnvironment"/> are
    /// <see langword="null"/> in a report written before they were recorded.</returns>
    /// <exception cref="BenchmarkException">The file is not a report.</exception>
    public static BenchmarkMachine ReadMachine(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var machine = document.RootElement.GetProperty("machine");
            return new BenchmarkMachine(
                machine.GetProperty("cores").GetInt32(),
                machine.GetProperty("jobs").GetInt32(),
                machine.GetProperty("os").GetString()!,
                machine.GetProperty("architecture").GetString()!,
                machine.GetProperty("serverGc").GetBoolean(),
                machine.TryGetProperty("gcHeapCount", out var heaps) ? heaps.GetInt32() : null,
                machine.TryGetProperty("runtimeEnvironment", out var environment) ? [.. environment.EnumerateArray().Select(e => e.GetString()!)] : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new BenchmarkException("The baseline is not a benchmark report: " + ex.Message, ex);
        }
    }

    /// <summary>Formats the report as text for a terminal.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The text, LF line endings.</returns>
    public static string ToText(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var s = new StringBuilder();
        void Line(FormattableString text) => s.Append(text.ToString(CultureInfo.InvariantCulture)).Append('\n');

        Line($"Maquettiste benchmark: {report.Entities:N0} entities, {report.Relations:N0} relations, fanout {report.Fanout}, packs {string.Join(", ", report.Packs)}");
        Line($"Machine: {report.MachineCores} cores, --jobs {report.Jobs}, {report.OperatingSystem}, {report.Framework} {report.Architecture}, server GC {report.ServerGc} (heaps {(report.GcHeapCount == 0 ? "per core" : report.GcHeapCount.ToString(CultureInfo.InvariantCulture))}, DATAS {report.GcDynamicAdaptation})");
        if (report.RuntimeEnvironment.Count > 0)
            Line($"Runtime overrides: {string.Join(" ", report.RuntimeEnvironment)}");
        Line($"Files: {report.Files:N0} (cold units rendered {report.ColdUnitsRendered:N0}); peak working set {report.PeakWorkingSetBytes / 1048576.0:F0} MiB");
        Line($"");
        Line($"Cold run with stage barriers:");
        foreach (var t in report.ColdStages)
            Line($"  {Stage(t.Stage),-12} wall {t.Wall.TotalSeconds,8:F3} s  busy {t.Busy.TotalSeconds,8:F3} s  items {t.Items,8:N0}");
        Line($"");
        Line($"Budgets:");
        foreach (var b in report.Budgets)
            Line($"  {b.Name,-22} {(b.AtLeast ? ">=" : "<="),2} {b.Limit,9:0.###} {b.Unit,-5}  actual {b.Actual,10:0.###}  {(b.Pass ? "pass" : "FAIL")}");
        Line($"");
        Line($"Incremental: {report.Incremental.TotalSeconds:F3} s, {report.IncrementalUnitsRendered:N0} units rendered, {report.IncrementalUnitsSkipped:N0} skipped, {report.IncrementalFilesWritten:N0} files written; GC pauses {report.IncrementalGcPause.TotalSeconds:F3} s");
        Line($"Incremental, new store (not a budget): {report.IncrementalFreshStore.TotalSeconds:F3} s, {report.IncrementalFreshStoreUnitsRendered:N0} units rendered; GC pauses {report.IncrementalFreshStoreGcPause.TotalSeconds:F3} s");
        Line($"Check: {report.CheckOutcome} in {report.CheckTotal.TotalSeconds:F3} s ({report.CheckUnitsRendered:N0} units rendered); cold outputs match: {report.OutputsMatch}");
        if (report.RegressionGateSkipped is not null)
        {
            Line($"");
            Line($"Regression gate skipped: {report.RegressionGateSkipped}");
        }

        if (report.Regressions.Count > 0)
        {
            Line($"");
            Line($"Against {report.Baseline}:");
            foreach (var r in report.Regressions)
                Line($"  {r.Name,-22} baseline {r.Baseline,9:0.###}  actual {r.Actual,9:0.###}  {r.ChangePercent,7:+0.0;-0.0;0.0}%  {(r.Pass ? "pass" : "REGRESSION")}");
        }

        Line($"");
        foreach (var note in report.Notes)
            Line($"Note: {note}");
        Line($"Result: {(report.Passed ? "PASS" : "FAIL")}");
        return s.ToString();
    }

    private static string Stage(PipelineStage stage) => stage switch
    {
        PipelineStage.PostProcess => "post-process",
        _ => stage.ToString().ToLowerInvariant(),
    };

    private static double Seconds(TimeSpan span) => Math.Round(span.TotalSeconds, 3);

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values)
            w.WriteStringValue(value);
        w.WriteEndArray();
    }

    private static void WriteStages(Utf8JsonWriter w, string name, IReadOnlyList<StageTiming> stages)
    {
        w.WriteStartArray(name);
        foreach (var t in stages)
        {
            w.WriteStartObject();
            w.WriteString("stage", Stage(t.Stage));
            w.WriteNumber("wallSeconds", Seconds(t.Wall));
            w.WriteNumber("busySeconds", Seconds(t.Busy));
            w.WriteNumber("items", t.Items);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}
