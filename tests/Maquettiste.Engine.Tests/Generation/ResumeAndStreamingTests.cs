using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>Interruptions (the run journal) and the streaming pipeline's bounded memory.</summary>
public sealed class ResumeAndStreamingTests
{
    [Fact]
    public async Task An_interrupted_apply_resumes_without_hand_edits()
    {
        await using var f = await GenerationFixture.CreateAsync(Many(60), "basic");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(GenerationFixture.Ct);
        var progress = new SyncProgress(update =>
        {
            if (update.Stage == PipelineStage.Write && update.Done >= 10)
                cts.Cancel();
        });

        var interrupted = await f.RunAsync(progress: progress, ct: cts.Token);

        Assert.Equal(RunOutcome.Cancelled, interrupted.Outcome);
        var journal = Path.Combine(f.Repo.ModelRoot, ".cache", "journal.jsonl");
        Assert.True(File.Exists(journal));
        var written = f.Outputs().Count;
        Assert.InRange(written, 1, 60);
        Assert.Equal("", f.CommittedManifest("basic")); // the pack never closed

        f.Renderer = new FakeRenderer();
        var resumed = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, resumed.Outcome);
        Assert.DoesNotContain(resumed.Changes, c => c.Kind is FileChangeKind.HandEdited or FileChangeKind.Conflict);
        Assert.Equal(61, f.Outputs().Count);
        Assert.False(File.Exists(journal));
        Assert.Contains("out/index.txt", f.CommittedManifest("basic"), StringComparison.Ordinal);

        var after = await f.RunAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Succeeded, after.Outcome);
    }

    [Fact]
    public async Task A_dry_run_after_an_interruption_reports_no_hand_edits_and_leaves_the_journal()
    {
        await using var f = await GenerationFixture.CreateAsync(Many(30), "basic");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(GenerationFixture.Ct);
        var progress = new SyncProgress(update =>
        {
            if (update.Stage == PipelineStage.Write && update.Done >= 5)
                cts.Cancel();
        });
        await f.RunAsync(progress: progress, ct: cts.Token);
        var journal = Path.Combine(f.Repo.ModelRoot, ".cache", "journal.jsonl");
        Assert.True(File.Exists(journal));

        var dry = await f.RunAsync(GenerationMode.DryRun);

        Assert.Equal(RunOutcome.Succeeded, dry.Outcome);
        Assert.DoesNotContain(dry.Changes, c => c.Kind is FileChangeKind.HandEdited or FileChangeKind.Conflict);
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public async Task Rendering_streams_into_the_writer_with_a_bounded_number_of_units_in_flight()
    {
        await using var f = await GenerationFixture.CreateAsync(Many(200), "basic");
        var writer = new SlowWriter(() => f.Renderer.Produced);
        var services = f.Services with { RendererFactory = () => f.Renderer, WriterFactory = _ => writer };
        var service = new GenerationService(f.Store, f.Repo.Options, services);

        var result = await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, GenerationFixture.Ct);

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal(201, writer.Consumed);
        Assert.Equal(201, f.Renderer.Produced);
        // Channel of 2 × jobs, jobs post-processing, one unit held by the producer and one by the writer: far below 201.
        Assert.InRange(writer.MaxInFlight, 1, 12);
    }

    [Fact]
    public async Task Stage_barriers_render_everything_before_writing()
    {
        await using var f = await GenerationFixture.CreateAsync(Many(20), "basic");
        var writer = new SlowWriter(() => f.Renderer.Produced);
        var services = f.Services with { RendererFactory = () => f.Renderer, WriterFactory = _ => writer };
        var service = new GenerationService(f.Store, f.Repo.Options, services);

        await service.RunAsync(new GenerationRequest { Jobs = 2, StageBarriers = true }, null, GenerationFixture.Ct);

        Assert.Equal(21, writer.MaxInFlight);
    }

    private static Action<ModelBuilder> Many(int count) => b =>
    {
        for (var i = 0; i < count; i++)
            b.Entity("Thing" + i).Key("id", "uuid");
        b.Database("main", Dialect.PostgreSql);
    };

    /// <summary>A writer that consumes slowly and records how many rendered units existed ahead of it.</summary>
    private sealed class SlowWriter(Func<int> produced) : IOutputWriter
    {
        public int Consumed { get; private set; }

        public int MaxInFlight { get; private set; }

        public async Task<WriteSummary> WriteAsync(IAsyncEnumerable<ProcessedUnit> units, WriteContext context, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            await foreach (var unit in units.WithCancellation(ct))
            {
                MaxInFlight = Math.Max(MaxInFlight, produced() - Consumed);
                Consumed++;
                await Task.Delay(2, ct);
            }

            return new WriteSummary([], [], 0, 0);
        }
    }
}
