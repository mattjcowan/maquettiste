using System.Text;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Tests.Writing;

public sealed class RunJournalAndLockTests
{
    private static readonly string H1 = new('a', 64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Journal_writes_one_line_per_record_and_is_deleted_at_end()
    {
        using var f = new WritingFixture();
        await f.Journal.BeginAsync("RUN1", "PLAN1", ["p", "q"], Ct);
        await f.Journal.RecordWriteAsync("p", new ManifestEntry("db/a.sql", H1, "u:1"), Ct);
        await f.Journal.RecordDeleteAsync("p", "db/old.sql", Ct);
        await f.Journal.RecordPackCompleteAsync("p", Ct);

        var text = File.ReadAllText(f.Journal.FilePath, Encoding.UTF8);
        Assert.Equal(
            "{\"t\":\"begin\",\"run\":\"RUN1\",\"plan\":\"PLAN1\",\"packs\":[\"p\",\"q\"]}\n" +
            $"{{\"t\":\"write\",\"pack\":\"p\",\"path\":\"db/a.sql\",\"hash\":\"{H1}\",\"unit\":\"u:1\"}}\n" +
            "{\"t\":\"delete\",\"pack\":\"p\",\"path\":\"db/old.sql\"}\n" +
            "{\"t\":\"pack\",\"pack\":\"p\"}\n",
            text);
        Assert.Equal(Path.Combine(f.Repo.ModelRoot, ".cache", "journal.jsonl"), f.Journal.FilePath);

        await f.Journal.EndAsync(Ct);

        Assert.False(File.Exists(f.Journal.FilePath));
        Assert.Null(await f.Journal.ReadUnfinishedAsync(Ct));
    }

    [Fact]
    public async Task Unfinished_journal_is_read_back_and_a_torn_last_line_is_ignored()
    {
        using var f = new WritingFixture();
        await f.Journal.BeginAsync("RUN1", null, ["p"], Ct);
        await f.Journal.RecordWriteAsync("p", new ManifestEntry("db/a.sql", H1, "u"), Ct);
        await f.Journal.DisposeAsync();
        File.AppendAllText(f.Journal.FilePath, "{\"t\":\"write\",\"pack\":\"p\",\"pa");

        var records = await f.Journal.ReadUnfinishedAsync(Ct);

        Assert.NotNull(records);
        Assert.Equal(
            [new JournalRecord("begin", null, null, null, null), new JournalRecord("write", "p", "db/a.sql", H1, "u")],
            records);
    }

    [Fact]
    public async Task Begin_carries_unfinished_packs_forward()
    {
        using var f = new WritingFixture();
        await f.Journal.BeginAsync("RUN1", null, ["p", "q"], Ct);
        await f.Journal.RecordWriteAsync("p", new ManifestEntry("db/a.sql", H1, "u"), Ct);
        await f.Journal.RecordWriteAsync("q", new ManifestEntry("db/q.sql", H1, "v"), Ct);
        await f.Journal.RecordPackCompleteAsync("q", Ct);
        await f.Journal.DisposeAsync();

        await f.Journal.BeginAsync("RUN2", null, ["p", "q"], Ct);
        await f.Journal.DisposeAsync();
        var records = await f.Journal.ReadUnfinishedAsync(Ct);

        Assert.Equal(
            [new JournalRecord("begin", null, null, null, null), new JournalRecord("write", "p", "db/a.sql", H1, "u")],
            records);
        Assert.StartsWith("{\"t\":\"begin\",\"run\":\"RUN2\",\"plan\":null", File.ReadAllText(f.Journal.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_keeps_the_lines_of_carried_packs_this_run_did_not_complete()
    {
        using var f = new WritingFixture();
        await f.Journal.BeginAsync("RUN1", null, ["p", "q"], Ct);
        await f.Journal.RecordWriteAsync("p", new ManifestEntry("db/a.sql", H1, "u"), Ct);
        await f.Journal.RecordWriteAsync("q", new ManifestEntry("db/q.sql", H1, "v"), Ct);
        await f.Journal.RecordDeleteAsync("q", "db/old.sql", Ct);
        await f.Journal.DisposeAsync();

        // A resumed run that covers only pack p.
        await f.Journal.BeginAsync("RUN2", null, ["p"], Ct);
        await f.Journal.RecordWriteAsync("p", new ManifestEntry("db/a.sql", H1, "u"), Ct);
        await f.Journal.RecordPackCompleteAsync("p", Ct);
        await f.Journal.EndAsync(Ct);

        Assert.True(File.Exists(f.Journal.FilePath));
        Assert.Equal(
            [
                new JournalRecord("begin", null, null, null, null),
                new JournalRecord("write", "q", "db/q.sql", H1, "v"),
                new JournalRecord("delete", "q", "db/old.sql", null, null),
            ],
            await f.Journal.ReadUnfinishedAsync(Ct));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(f.Journal.FilePath)!, "*.tmp"));

        // Once a run completes pack q, the journal ends and is deleted.
        await f.Journal.BeginAsync("RUN3", null, ["q"], Ct);
        await f.Journal.RecordPackCompleteAsync("q", Ct);
        await f.Journal.EndAsync(Ct);

        Assert.False(File.Exists(f.Journal.FilePath));
    }

    [Fact]
    public async Task A_cancelled_begin_leaves_the_unfinished_journal_intact()
    {
        using var f = new WritingFixture();
        await f.Journal.BeginAsync("RUN1", null, ["p"], Ct);
        await f.Journal.RecordWriteAsync("p", new ManifestEntry("db/a.sql", H1, "u"), Ct);
        await f.Journal.DisposeAsync();
        var before = File.ReadAllBytes(f.Journal.FilePath);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Journal.BeginAsync("RUN2", null, ["p"], cts.Token));

        Assert.Equal(before, File.ReadAllBytes(f.Journal.FilePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(f.Journal.FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task Journal_refuses_records_when_not_open()
    {
        using var f = new WritingFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Journal.RecordWriteAsync("p", new ManifestEntry("a", H1, "u"), Ct).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Journal.EndAsync(Ct));
    }

    [Fact]
    public async Task Journal_outside_the_cache_folders_is_refused()
    {
        using var repo = new Testing.TempRepo();
        var options = repo.Options with { JournalDirectory = Path.Combine(repo.Root, "journal") };
        // A guard built for other folders refuses the journal's folder.
        var journal = new RunJournal(options, new OutputPathPolicy(repo.Options, null));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => journal.BeginAsync("RUN", null, [], Ct));
    }

    [Fact]
    public async Task Lock_is_exclusive_until_released()
    {
        using var f = new WritingFixture();
        var runLock = new RunLock(f.Repo.Options, f.EnginePaths);
        var other = new RunLock(f.Repo.Options, f.EnginePaths);

        var held = await runLock.AcquireAsync(wait: false, Ct);
        Assert.NotNull(held);
        Assert.Null(await other.AcquireAsync(wait: false, Ct));
        Assert.True(File.Exists(Path.Combine(f.Repo.ModelRoot, ".cache", "run.lock")));

        await held.DisposeAsync();
        await held.DisposeAsync();
        var again = await other.AcquireAsync(wait: false, Ct);
        Assert.NotNull(again);
        await again.DisposeAsync();
    }

    [Fact]
    public async Task Waiting_lock_is_acquired_once_released()
    {
        using var f = new WritingFixture();
        var runLock = new RunLock(f.Repo.Options, f.EnginePaths);
        var held = await runLock.AcquireAsync(wait: false, Ct);
        Assert.NotNull(held);

        var waiting = runLock.AcquireAsync(wait: true, Ct);
        await Task.Delay(250, Ct);
        Assert.False(waiting.IsCompleted);
        await held.DisposeAsync();
        var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.NotNull(acquired);
        await acquired.DisposeAsync();
    }

    [Fact]
    public async Task Waiting_lock_observes_cancellation()
    {
        using var f = new WritingFixture();
        var runLock = new RunLock(f.Repo.Options, f.EnginePaths);
        var held = await runLock.AcquireAsync(wait: false, Ct);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runLock.AcquireAsync(wait: true, cts.Token));
        await held!.DisposeAsync();
    }
}
