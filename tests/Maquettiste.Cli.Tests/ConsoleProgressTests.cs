using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli.Tests;

public sealed class ConsoleProgressTests
{
    [Fact]
    public void Terminal_style_rewrites_one_line_with_stage_counts_and_path_and_ends_with_a_newline()
    {
        var error = new SharedWriter();
        var progress = new ConsoleProgress(ProgressStyle.Terminal, error);
        progress.Report(new ProgressUpdate(PipelineStage.Render, 41_200, 100_480, "src/Generated/Billing/Invoice.g.cs", "csharp-dapper"));
        progress.Report(new ProgressUpdate(PipelineStage.Write, 3, 3, "db/x.sql", "sql-ddl"));
        progress.Complete();
        var text = error.Text();
        Assert.StartsWith("\r[6/8 render] 41,200/100,480 src/Generated/Billing/Invoice.g.cs", text, StringComparison.Ordinal);
        Assert.Contains("\r[8/8 write] 3/3 db/x.sql", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Count(c => c == '\n'));
        Assert.Equal(3, progress.FilesCompared);
    }

    [Fact]
    public void Plain_style_writes_a_start_and_an_end_line_per_stage()
    {
        var error = new SharedWriter();
        var progress = new ConsoleProgress(ProgressStyle.Plain, error);
        progress.Report(new ProgressUpdate(PipelineStage.Load, 0, 0, null, null));
        progress.Report(new ProgressUpdate(PipelineStage.Load, 12, 12, null, null));
        progress.Report(new ProgressUpdate(PipelineStage.Validate, 12, 12, null, null));
        progress.Report(new ProgressUpdate(PipelineStage.Render, 1, 2, "a", null));
        progress.Report(new ProgressUpdate(PipelineStage.Write, 1, 1, "a", null));
        progress.Report(new ProgressUpdate(PipelineStage.Render, 2, 2, "b", null));
        progress.Complete();
        var lines = Text.Lines(error.Text());
        Assert.Equal("[1/8 load] started", lines[0]);
        Assert.StartsWith("[1/8 load] done: 12 in ", lines[1], StringComparison.Ordinal);
        Assert.Equal("[2/8 validate] started", lines[2]);
        Assert.StartsWith("[2/8 validate] done: 12", lines[3], StringComparison.Ordinal);
        Assert.Equal("[6/8 render] started", lines[4]);
        Assert.Equal("[8/8 write] started", lines[5]);
        Assert.StartsWith("[6/8 render] done: 2 in ", lines[6], StringComparison.Ordinal);
        Assert.StartsWith("[8/8 write] done: 1 in ", lines[7], StringComparison.Ordinal);
        Assert.Equal(8, lines.Length);
    }

    [Fact]
    public void Plain_style_under_concurrent_updates_still_writes_one_start_and_one_end_line_per_stage()
    {
        var error = new SharedWriter();
        var progress = new ConsoleProgress(ProgressStyle.Plain, error);
        progress.Report(new ProgressUpdate(PipelineStage.Load, 0, 0, null, null));
        Parallel.For(0, 20_000, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => progress.Report(new ProgressUpdate(PipelineStage.Load, i + 1, 20_001, null, null)));
        progress.Report(new ProgressUpdate(PipelineStage.Load, 20_001, 20_001, null, null));
        Parallel.For(0, 5_000, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => progress.Report(new ProgressUpdate(PipelineStage.Write, i + 1, 5_000, "f", null)));
        progress.Complete();
        var lines = Text.Lines(error.Text());
        Assert.Equal("[1/8 load] started", lines[0]);
        Assert.StartsWith("[1/8 load] done: 20,001 in ", lines[1], StringComparison.Ordinal);
        Assert.Equal("[8/8 write] started", lines[2]);
        Assert.StartsWith("[8/8 write] done: 5,000 in ", lines[3], StringComparison.Ordinal);
        Assert.Equal(4, lines.Length);
        Assert.Equal(5_000, progress.FilesCompared);
    }

    [Fact]
    public void None_style_counts_files_from_concurrent_updates()
    {
        var progress = new ConsoleProgress(ProgressStyle.None, new SharedWriter());
        Parallel.For(0, 10_000, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => progress.Report(new ProgressUpdate(PipelineStage.Write, i + 1, 10_000, null, null)));
        Assert.Equal(10_000, progress.TakeFilesCompared());
        Assert.Equal(0, progress.FilesCompared);
    }

    [Fact]
    public void Json_style_throttles_but_keeps_first_and_last_updates()
    {
        var error = new SharedWriter();
        var progress = new ConsoleProgress(ProgressStyle.Json, error);
        for (var i = 1; i <= 1000; i++)
            progress.Report(new ProgressUpdate(PipelineStage.Render, i, 1000, "p" + i, "pack"));
        var lines = Text.Lines(error.Text());
        Assert.InRange(lines.Length, 2, 20);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("render", first.RootElement.GetProperty("stage").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("done").GetInt32());
        Assert.Equal("p1", first.RootElement.GetProperty("currentPath").GetString());
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal(1000, last.RootElement.GetProperty("done").GetInt32());
    }

    [Fact]
    public void None_style_writes_nothing_but_still_counts_files()
    {
        var error = new SharedWriter();
        var progress = new ConsoleProgress(ProgressStyle.None, error);
        progress.Report(new ProgressUpdate(PipelineStage.Write, 7, 9, "x", null));
        progress.Complete();
        Assert.Equal("", error.Text());
        Assert.Equal(7, progress.FilesCompared);
    }
}
