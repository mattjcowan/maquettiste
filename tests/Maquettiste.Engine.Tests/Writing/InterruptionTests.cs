using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using static Maquettiste.Engine.Tests.Writing.WritingFixture;

namespace Maquettiste.Engine.Tests.Writing;

/// <summary>Failures mid-batch, cancellation, and resuming from the journal (engine-design.md section 12.4; host-contracts 26, 32, 33).</summary>
public sealed class InterruptionTests
{
    private static List<ProcessedUnit> Batch(string version) =>
    [
        Unit("p/u:1", Out("db/one.sql", "one " + version + "\n")),
        Unit("p/u:2", Out("db/two.sql", "two " + version + "\n")),
        Unit("p/u:3", Out("db/three.sql", "three " + version + "\n")),
    ];

    [Fact]
    public async Task A_failure_mid_batch_leaves_no_partial_files_and_the_run_resumes_without_hand_edits()
    {
        using var f = new WritingFixture(HandEditPolicy.Fail);
        await f.RunAsync(Batch("v1"));
        var manifest = f.ManifestText("p");
        // A folder where a file must go makes the rename fail after staging.
        Directory.CreateDirectory(f.Repo.PathOf("db/blocker.sql"));
        var failing = Batch("v2");
        failing.Insert(2, Unit("p/u:blocker", Out("db/blocker.sql", "blocked\n")));

        var manifests = await f.LoadManifestsAsync();
        await f.Journal.BeginAsync("RUN2", null, ["p"], Ct);
        f.Writer = new Engine.Writing.OutputWriter(f.Repo.Options with { MaxDegreeOfParallelism = 1 }, f.Paths, f.Manifests, new Engine.Writing.DiffGenerator());
        await Assert.ThrowsAnyAsync<IOException>(() => f.Writer.WriteAsync(Stream(failing, Ct), f.Context(manifests), null, Ct));
        await f.Journal.DisposeAsync();

        // No temporary file is left, every file is either old or new, and the manifest was not saved for the unfinished pack.
        Assert.Empty(f.TempFiles());
        Assert.Equal("one v2\n", f.Repo.ReadFile("db/one.sql"));
        Assert.Equal("two v2\n", f.Repo.ReadFile("db/two.sql"));
        Assert.Equal("three v1\n", f.Repo.ReadFile("db/three.sql"));
        Assert.Equal(manifest, f.ManifestText("p"));
        var journal = await f.Journal.ReadUnfinishedAsync(Ct);
        Assert.NotNull(journal);
        Assert.Equal(["db/one.sql", "db/two.sql"], journal.Where(r => r.Type == "write").Select(r => r.Path));

        // Without the journal the new files would look like hand edits.
        var withoutJournal = await f.Writer.WriteAsync(Stream(Batch("v3"), Ct), f.Context(await f.LoadManifestsAsync(), GenerationMode.DryRun), null, Ct);
        Assert.Equal(["db/one.sql", "db/two.sql"], withoutJournal.Changes.Where(c => c.Kind == FileChangeKind.Conflict).Select(c => c.Path));

        // The resumed run overlays the journal: nothing is a hand edit.
        Directory.Delete(f.Repo.PathOf("db/blocker.sql"));
        var resumed = await f.RunAsync(Batch("v3"));
        Assert.Empty(resumed.Diagnostics);
        Assert.All(resumed.Changes, c => Assert.Equal(FileChangeKind.Modified, c.Kind));
        Assert.Equal("three v3\n", f.Repo.ReadFile("db/three.sql"));
        Assert.False(System.IO.File.Exists(f.Journal.FilePath));
    }

    [Fact]
    public async Task Cancellation_stops_between_files_and_the_run_resumes_without_hand_edits()
    {
        using var f = new WritingFixture(HandEditPolicy.Fail);
        await f.RunAsync(Batch("v1"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var manifests = await f.LoadManifestsAsync();
        await f.Journal.BeginAsync("RUN2", null, ["p"], Ct);

        async IAsyncEnumerable<ProcessedUnit> CancelAfterFirst([EnumeratorCancellation] CancellationToken ct = default)
        {
            var units = Batch("v2");
            yield return units[0];
            // Give the writer time to finish the first file, then cancel as a host stop would.
            for (var i = 0; i < 200 && !System.IO.File.ReadAllText(f.Journal.FilePath).Contains("one.sql", StringComparison.Ordinal); i++)
                await Task.Delay(10, CancellationToken.None);
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            yield return units[1];
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Writer.WriteAsync(CancelAfterFirst(cts.Token), f.Context(manifests), null, cts.Token));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        await f.Journal.DisposeAsync();

        Assert.Equal("one v2\n", f.Repo.ReadFile("db/one.sql"));
        Assert.Equal("two v1\n", f.Repo.ReadFile("db/two.sql"));
        Assert.Empty(f.TempFiles());

        var resumed = await f.RunAsync(Batch("v3"));

        Assert.Empty(resumed.Diagnostics);
        Assert.Equal(3, resumed.Changes.Count(c => c.Kind == FileChangeKind.Modified));
    }

    [Fact]
    public async Task A_resume_interrupted_again_still_resumes()
    {
        using var f = new WritingFixture(HandEditPolicy.Fail);
        await f.RunAsync(Batch("v1"));

        // Run 2 writes one.sql and dies before the pack closes.
        var manifests = await f.LoadManifestsAsync(overlayJournal: true);
        await f.Journal.BeginAsync("RUN2", null, ["p"], Ct);
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            async IAsyncEnumerable<ProcessedUnit> OneThenStop()
            {
                yield return Batch("v2")[0];
                for (var i = 0; i < 200 && !System.IO.File.ReadAllText(f.Journal.FilePath).Contains("one.sql", StringComparison.Ordinal); i++)
                    await Task.Delay(10, CancellationToken.None);
                await cts.CancelAsync();
                cts.Token.ThrowIfCancellationRequested();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Writer.WriteAsync(OneThenStop(), f.Context(manifests), null, cts.Token));
        }

        await f.Journal.DisposeAsync();

        // Run 3 resumes but renders nothing for one.sql yet (say, the unit is still queued) and dies too.
        manifests = await f.LoadManifestsAsync(overlayJournal: true);
        await f.Journal.BeginAsync("RUN3", null, ["p"], Ct);
        await f.Journal.DisposeAsync();

        // Run 4 still knows one.sql's hash from run 2.
        var resumed = await f.RunAsync(Batch("v4"));

        Assert.Empty(resumed.Diagnostics);
        Assert.Equal("one v4\n", f.Repo.ReadFile("db/one.sql"));
    }

    [Fact]
    public async Task A_resume_covering_other_packs_keeps_the_journal_of_the_packs_it_skipped()
    {
        using var f = new WritingFixture(HandEditPolicy.Fail);
        var counts = new Dictionary<string, int> { ["q"] = 1 };
        await f.RunAsync([Unit("q/u", Out("db/q.sql", "v1\n"))], counts: counts);

        // Run 2 writes db/q.sql v2 for pack q and dies before q closes.
        var manifests = await f.LoadManifestsAsync(overlayJournal: true);
        await f.Journal.BeginAsync("RUN2", null, ["q"], Ct);
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            async IAsyncEnumerable<ProcessedUnit> OneThenStop()
            {
                yield return Unit("q/u", Out("db/q.sql", "v2\n"));
                for (var i = 0; i < 200 && !System.IO.File.ReadAllText(f.Journal.FilePath).Contains("q.sql", StringComparison.Ordinal); i++)
                    await Task.Delay(10, CancellationToken.None);
                await cts.CancelAsync();
                cts.Token.ThrowIfCancellationRequested();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                f.Writer.WriteAsync(OneThenStop(), f.Context(manifests, counts: new Dictionary<string, int> { ["q"] = 2 }), null, cts.Token));
        }

        await f.Journal.DisposeAsync();
        Assert.Equal("v2\n", f.Repo.ReadFile("db/q.sql"));

        // Run 3 resumes but covers only pack p, and completes.
        var third = await f.RunAsync([], counts: new Dictionary<string, int> { ["p"] = 0 });
        Assert.Empty(third.Diagnostics);
        Assert.True(System.IO.File.Exists(f.Journal.FilePath));

        // Run 4 of pack q still knows db/q.sql's hash from run 2: no hand edit.
        var fourth = await f.RunAsync([Unit("q/u", Out("db/q.sql", "v3\n"))], counts: counts);

        Assert.Empty(fourth.Diagnostics);
        Assert.Equal([("db/q.sql", FileChangeKind.Modified)], fourth.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("v3\n", f.Repo.ReadFile("db/q.sql"));
        Assert.False(System.IO.File.Exists(f.Journal.FilePath));
    }
}
