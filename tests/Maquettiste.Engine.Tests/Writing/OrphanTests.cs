using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;
using static Maquettiste.Engine.Tests.Writing.WritingFixture;

namespace Maquettiste.Engine.Tests.Writing;

/// <summary>Orphans across packs and outputs renamed only by case (engine-design.md sections 12.1 and 12.3).</summary>
public sealed class OrphanTests
{
    private static readonly DateTime Old = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(GenerationMode.DryRun)]
    [InlineData(GenerationMode.Apply)]
    public async Task A_file_moving_to_a_later_pack_is_neither_deleted_nor_rewritten(GenerationMode mode)
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/u:1", Out("db/shared.sql", "x\n")), Unit("p/u:2", Out("db/other.sql", "o\n"))]);
        System.IO.File.SetLastWriteTimeUtc(f.Repo.PathOf("db/shared.sql"), Old);

        // Pack p closes (its last unit has arrived) before pack q produces the file.
        var summary = await f.RunAsync(
            [Unit("p/u:2", Out("db/other.sql", "o\n")), Unit("q/u", Out("db/shared.sql", "x\n"))],
            mode,
            counts: new Dictionary<string, int> { ["p"] = 1, ["q"] = 1 });

        Assert.Empty(summary.Changes);
        Assert.Empty(summary.Diagnostics);
        Assert.Equal(0, summary.Deleted);
        Assert.Equal(0, summary.Written);
        Assert.Equal("x\n", f.Repo.ReadFile("db/shared.sql"));
        Assert.Equal(Old, System.IO.File.GetLastWriteTimeUtc(f.Repo.PathOf("db/shared.sql")));
        if (mode == GenerationMode.Apply)
        {
            Assert.DoesNotContain("db/shared.sql", f.ManifestText("p"), StringComparison.Ordinal);
            Assert.Contains("db/other.sql", f.ManifestText("p"), StringComparison.Ordinal);
            Assert.Contains("db/shared.sql", f.ManifestText("q"), StringComparison.Ordinal);
            Assert.False(System.IO.File.Exists(f.Journal.FilePath));
        }
    }

    [Fact]
    public async Task An_orphan_of_an_earlier_pack_is_deleted_once_the_stream_ends()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/u:1", Out("db/gone.sql", "g\n")), Unit("p/u:2", Out("db/other.sql", "o\n"))]);

        var summary = await f.RunAsync(
            [Unit("p/u:2", Out("db/other.sql", "o\n")), Unit("q/u", Out("db/q.sql", "q\n"))],
            counts: new Dictionary<string, int> { ["p"] = 1, ["q"] = 1 });

        Assert.Contains(summary.Changes, c => c is { Path: "db/gone.sql", Kind: FileChangeKind.Deleted, Pack: "p" });
        Assert.False(f.Repo.Exists("db/gone.sql"));
        Assert.Equal(1, summary.Deleted);
        Assert.DoesNotContain("db/gone.sql", f.ManifestText("p"), StringComparison.Ordinal);
        // The pack's second save gets its own journal line, so the journal ends cleanly.
        Assert.False(System.IO.File.Exists(f.Journal.FilePath));
    }

    [Theory]
    [InlineData("v2\n")]
    [InlineData("v1\n")]
    public async Task An_output_renamed_only_by_case_is_neither_a_hand_edit_nor_deleted(string content)
    {
        // Overwrite: a file wrongly taken for a hand-edited orphan would be deleted.
        using var f = new WritingFixture(HandEditPolicy.Overwrite);
        await f.RunAsync([Unit("p/u", Out("db/Invoice.sql", "v1\n"))]);
        var caseInsensitive = System.IO.File.Exists(f.Repo.PathOf("db/invoice.sql"));

        var summary = await f.RunAsync([Unit("p/u", Out("db/invoice.sql", content))]);

        Assert.DoesNotContain(summary.Diagnostics, d => d.Rule == "MQ6009");
        Assert.Equal(content, f.Repo.ReadFile("db/invoice.sql"));
        Assert.Equal(["invoice.sql"], Directory.EnumerateFiles(f.Repo.PathOf("db")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var manifest = f.ManifestText("p");
        Assert.Contains("\"db/invoice.sql\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("\"db/Invoice.sql\"", manifest, StringComparison.Ordinal);
        var changes = summary.Changes.Select(c => (c.Path, c.Kind)).ToList();
        if (caseInsensitive)
        {
            // One file on disk: an edit of the tracked file (or nothing when the bytes are identical), never a delete.
            Assert.Equal(content == "v1\n" ? [] : [("db/invoice.sql", FileChangeKind.Modified)], changes);
        }
        else
        {
            Assert.Equal([("db/Invoice.sql", FileChangeKind.Deleted), ("db/invoice.sql", FileChangeKind.Added)], changes);
        }
    }

    [Fact]
    public async Task A_conflict_on_an_output_renamed_only_by_case_keeps_the_old_entry_once()
    {
        using var f = new WritingFixture(HandEditPolicy.Fail);
        await f.RunAsync([Unit("p/u", Out("db/Invoice.sql", "v1\n"))]);
        f.Repo.WriteFile("db/Invoice.sql", "hand edit\n");
        var caseInsensitive = System.IO.File.Exists(f.Repo.PathOf("db/invoice.sql"));

        var summary = await f.RunAsync([Unit("p/u", Out("db/invoice.sql", "v2\n"))]);

        var manifest = f.ManifestText("p");
        if (caseInsensitive)
        {
            Assert.Equal([("db/invoice.sql", FileChangeKind.Conflict)], summary.Changes.Select(c => (c.Path, c.Kind)));
            Assert.Equal("hand edit\n", f.Repo.ReadFile("db/Invoice.sql"));
            Assert.Single(manifest.Split('\n'), l => l.Contains("nvoice.sql", StringComparison.Ordinal));
        }
        else
        {
            // Two different files: the new one is added; the hand-edited old one is an orphan in conflict and stays.
            Assert.Contains(summary.Changes, c => c is { Path: "db/Invoice.sql", Kind: FileChangeKind.Conflict });
            Assert.Contains(summary.Changes, c => c is { Path: "db/invoice.sql", Kind: FileChangeKind.Added });
            Assert.Equal("hand edit\n", f.Repo.ReadFile("db/Invoice.sql"));
            Assert.Equal("v2\n", f.Repo.ReadFile("db/invoice.sql"));
        }
    }

    [Fact]
    public void On_disk_spelling_keeps_exact_names_and_reports_missing_paths()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile("db/Tables/A.sql", "a\n");

        Assert.Equal("db/Tables/A.sql", FileSystemPaths.OnDiskSpelling(f.Repo.RepoRoot, "db/Tables/A.sql"));
        Assert.Null(FileSystemPaths.OnDiskSpelling(f.Repo.RepoRoot, "db/Tables/B.sql"));
        if (System.IO.File.Exists(f.Repo.PathOf("db/tables/a.sql")))
            Assert.Equal("db/Tables/A.sql", FileSystemPaths.OnDiskSpelling(f.Repo.RepoRoot, "db/tables/a.sql"));
        else
            Assert.Null(FileSystemPaths.OnDiskSpelling(f.Repo.RepoRoot, "db/tables/a.sql"));
    }
}
