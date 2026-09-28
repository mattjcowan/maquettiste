using System.Diagnostics;
using System.Runtime.CompilerServices;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Interrupted runs (integration tasks 8 and 13; engine-design.md section 12.4): a run cancelled or crashed in the middle of writing
/// leaves an unfinished journal and a released lock, and the next run resumes it without reporting the files it wrote as hand edits,
/// ending where an uninterrupted run would.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ResumeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cancellation_mid_write_leaves_a_resumable_journal_and_releases_the_lock()
    {
        await using var repo = E2ERepo.Create();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelledAt = new Stopwatch();
        var progress = new ProgressLog(u =>
        {
            if (u.Stage == PipelineStage.Write && u.Done >= 8 && !cts.IsCancellationRequested)
            {
                cancelledAt.Start();
                cts.Cancel();
            }
        });

        var cancelled = await repo.RunAsync(progress: progress, ct: cts.Token);
        cancelledAt.Stop();

        Assert.Equal(RunOutcome.Cancelled, cancelled.Outcome);
        Assert.True(cancelledAt.Elapsed < TimeSpan.FromSeconds(5), "returned " + cancelledAt.Elapsed + " after cancellation");
        Assert.True(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"), "the journal is left unfinished");
        var journal = repo.Repo.ReadFile(".maquettiste/.cache/journal.jsonl");
        Assert.Contains("\"t\":\"begin\"", journal, StringComparison.Ordinal);
        Assert.DoesNotContain("\"t\":\"end\"", journal, StringComparison.Ordinal);
        using (CheckTests.HoldRunLock(repo))
        {
            // The lock was released: another holder can take it.
        }

        var written = WrittenOutputs(repo);
        Assert.NotEmpty(written);
        Assert.True(written.Count < 70, "cancelled before the end: " + written.Count);

        await AssertResumesWithoutHandEditsAsync(repo, written);
    }

    [Fact]
    public async Task An_apply_that_crashes_mid_write_resumes_on_the_next_run_without_hand_edits()
    {
        await using var repo = E2ERepo.Create();
        var crashing = new GenerationService(repo.Store, repo.Options, CrashingServices(repo, afterUnits: 12));

        await Assert.ThrowsAsync<IOException>(() => crashing.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct));

        Assert.True(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"));
        using (CheckTests.HoldRunLock(repo))
        {
        }

        var written = WrittenOutputs(repo);
        Assert.NotEmpty(written);
        await AssertResumesWithoutHandEditsAsync(repo, written);
    }

    [Fact]
    public async Task A_plan_apply_that_crashes_mid_write_resumes_when_the_plan_is_applied_again()
    {
        await using var repo = await CheckTests.AppliedAsync();
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        await repo.EditAsync(E2ERepo.ProductId, n => n["attributes"]![1]!["length"] = 50);
        var plan = (await repo.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct)).Plan!;
        var crashing = new GenerationService(repo.Store, repo.Options, CrashingServices(repo, afterUnits: 3));

        await Assert.ThrowsAsync<IOException>(() => crashing.ApplyAsync(plan.Id, null, Ct));
        Assert.True(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"));

        // Some planned files were written before the crash; applying the same plan again finishes it (not Stale, no hand edits).
        var resumed = await repo.Service.ApplyAsync(plan.Id, null, Ct);

        Assert.Equal(RunOutcome.Succeeded, resumed.Outcome);
        Assert.DoesNotContain(resumed.Result!.Changes, c => c.Kind is FileChangeKind.HandEdited or FileChangeKind.Conflict);
        Assert.False(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"));
        Assert.Contains("length 150", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal);
        Assert.Contains("length 50", repo.Repo.ReadFile("db/e2e/entities/product.txt"), StringComparison.Ordinal);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }

    /// <summary>
    /// Changes the model so the files the interrupted run wrote are rendered differently, then runs again: those files have no
    /// manifest entry (or an old one), so without the journal overlay they would be hand edits.
    /// </summary>
    private static async Task AssertResumesWithoutHandEditsAsync(E2ERepo repo, IReadOnlyDictionary<string, string> written)
    {
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        await repo.EditAsync(E2ERepo.InvoiceId, n => n["displayName"] = "Bill");

        var resumed = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, resumed);
        Assert.DoesNotContain(resumed.Changes, c => c.Kind is FileChangeKind.HandEdited or FileChangeKind.Conflict);
        Assert.DoesNotContain(resumed.Diagnostics, d => d.Rule == "MQ6009");
        Assert.False(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"));
        var rewritten = written.Keys.Where(p => E2ERepo.Sha(File.ReadAllBytes(repo.Repo.PathOf(p))) != written[p]).ToList();
        Assert.True(rewritten.Count > 0, "no file written by the interrupted run changed; the resume path was not exercised");
        Assert.All(rewritten, p => Assert.Contains(resumed.Changes, c => c.Path == p && c.Kind == FileChangeKind.Modified));

        // The resumed run ends where an uninterrupted one would.
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }

    private static Dictionary<string, string> WrittenOutputs(E2ERepo repo) =>
        repo.Repo.ListFiles().Where(E2ERepo.IsOutput).ToDictionary(p => p, p => E2ERepo.Sha(File.ReadAllBytes(repo.Repo.PathOf(p))), StringComparer.Ordinal);

    /// <summary>The real services with a writer whose input stream fails after some units (a crash in the middle of writing).</summary>
    private static EngineServices CrashingServices(E2ERepo repo, int afterUnits)
    {
        var services = EngineServices.Create(repo.Options);
        return services with { WriterFactory = paths => new CrashingWriter(services.CreateWriter(paths), afterUnits) };
    }

    private sealed class CrashingWriter(IOutputWriter inner, int afterUnits) : IOutputWriter
    {
        public Task<WriteSummary> WriteAsync(IAsyncEnumerable<ProcessedUnit> units, WriteContext context, IProgress<ProgressUpdate>? progress, CancellationToken ct) =>
            inner.WriteAsync(Crash(units, ct), context, progress, ct);

        private async IAsyncEnumerable<ProcessedUnit> Crash(IAsyncEnumerable<ProcessedUnit> units, [EnumeratorCancellation] CancellationToken ct)
        {
            var count = 0;
            await foreach (var unit in units.WithCancellation(ct))
            {
                if (++count > afterUnits)
                {
                    await Task.Delay(50, ct); // let the writer finish the files already queued
                    throw new IOException("Simulated crash in the middle of writing.");
                }

                yield return unit;
            }
        }
    }
}
