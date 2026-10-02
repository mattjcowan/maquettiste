using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using static Maquettiste.Engine.Tests.Writing.WritingFixture;

namespace Maquettiste.Engine.Tests.Writing;

public sealed class OutputWriterTests
{
    private static readonly DateTime Old = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task First_run_adds_files_and_records_manifests_state_and_journal()
    {
        using var f = new WritingFixture();

        var summary = await f.RunAsync(
        [
            Unit("p/table:01A", Out("db/tables/a.sql", "create table a;\n")),
            Unit("p/entity:01B", Out("src/Generated/B.g.cs", "class B {}\n")),
        ]);

        Assert.Equal(2, summary.Written);
        Assert.Empty(summary.Diagnostics);
        Assert.Equal([("db/tables/a.sql", FileChangeKind.Added), ("src/Generated/B.g.cs", FileChangeKind.Added)],
            summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("create table a;\n", f.Repo.ReadFile("db/tables/a.sql"));
        // One manifest for every root, in the model folder.
        Assert.Contains($"[\"db/tables/a.sql\", \"{ContentHash.Of("create table a;\n")}\", \"table:01A\"]", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.Contains("\"src/Generated/B.g.cs\"", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(f.Manifests.LegacyFileOf("p")));
        var state = f.State.Packs["p"]["p/table:01A"];
        Assert.Equal("input-p/table:01A", state.InputHash);
        Assert.Equal(["e:a", "e:b"], state.ReadKeys);
        var output = Assert.Single(state.Outputs);
        Assert.Equal(("db/tables/a.sql", 16L), (output.Path, output.Length));
        Assert.Equal(new FileInfo(f.Repo.PathOf("db/tables/a.sql")).LastWriteTimeUtc.Ticks, output.LastWriteUtcTicks);
        Assert.False(System.IO.File.Exists(f.Journal.FilePath));
        Assert.Empty(f.TempFiles());
    }

    [Fact]
    public async Task Identical_bytes_are_not_rewritten_and_keep_their_mtime()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"))]);
        var manifest = f.ManifestText("p");
        System.IO.File.SetLastWriteTimeUtc(f.Repo.PathOf("db/a.sql"), Old);

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"))]);

        Assert.Equal(0, summary.Written);
        Assert.Empty(summary.Changes);
        Assert.Equal(Old, System.IO.File.GetLastWriteTimeUtc(f.Repo.PathOf("db/a.sql")));
        Assert.Equal(manifest, f.ManifestText("p"));
    }

    [Fact]
    public async Task Changed_output_is_modified_atomically()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"))]);

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "b\n"))], diffs: true);

        var change = Assert.Single(summary.Changes);
        Assert.Equal(FileChangeKind.Modified, change.Kind);
        Assert.Equal(ContentHash.Of("a\n"), change.OldHash);
        Assert.Equal(ContentHash.Of("b\n"), change.NewHash);
        Assert.Equal("--- a/db/a.sql\n+++ b/db/a.sql\n@@ -1 +1 @@\n-a\n+b\n", change.Diff);
        Assert.Equal("b\n", f.Repo.ReadFile("db/a.sql"));
        Assert.Contains(ContentHash.Of("b\n"), f.ManifestText("p"), StringComparison.Ordinal);
        Assert.Empty(f.TempFiles());
    }

    [Fact]
    public async Task Hand_edit_under_fail_is_a_conflict_and_nothing_is_written()
    {
        using var f = new WritingFixture(HandEditPolicy.Fail);
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"))]);
        var manifest = f.ManifestText("p");
        f.Repo.WriteFile("db/a.sql", "edited\n");

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "b\n"))]);

        var change = Assert.Single(summary.Changes);
        Assert.Equal(FileChangeKind.Conflict, change.Kind);
        Assert.Equal("edited\n", f.Repo.ReadFile("db/a.sql"));
        Assert.Equal(manifest, f.ManifestText("p"));
        var diagnostic = Assert.Single(summary.Diagnostics);
        Assert.Equal(("MQ6009", Diagnostics.DiagnosticSeverity.Error, "db/a.sql"), (diagnostic.Rule, diagnostic.Severity, diagnostic.FilePath));
        // The unit renders again next time: its state is not replaced.
        Assert.Equal(ContentHash.Of("a\n"), f.State.Packs["p"]["p/u"].Outputs[0].ManifestHash);
    }

    [Fact]
    public async Task Hand_edit_under_overwrite_is_replaced()
    {
        using var f = new WritingFixture(HandEditPolicy.Overwrite);
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"))]);
        f.Repo.WriteFile("db/a.sql", "edited\n");

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "b\n"))]);

        Assert.Equal(FileChangeKind.HandEdited, Assert.Single(summary.Changes).Kind);
        Assert.Equal("b\n", f.Repo.ReadFile("db/a.sql"));
        Assert.Equal(1, summary.Written);
        Assert.Equal(Diagnostics.DiagnosticSeverity.Warning, Assert.Single(summary.Diagnostics).Severity);
        Assert.Contains(ContentHash.Of("b\n"), f.ManifestText("p"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hand_edit_under_skip_keeps_the_file_and_the_old_entry()
    {
        using var f = new WritingFixture(HandEditPolicy.Skip);
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"))]);
        f.Repo.WriteFile("db/a.sql", "edited\n");

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "b\n"))]);

        Assert.Equal(FileChangeKind.HandEdited, Assert.Single(summary.Changes).Kind);
        Assert.Equal("edited\n", f.Repo.ReadFile("db/a.sql"));
        Assert.Equal(0, summary.Written);
        Assert.Contains(ContentHash.Of("a\n"), f.ManifestText("p"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untracked_file_in_the_way_is_a_hand_edit_unless_identical()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile("db/a.sql", "someone else's\n");
        f.Repo.WriteFile("db/b.sql", "same\n");

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"), Out("db/b.sql", "same\n"))]);

        Assert.Equal([("db/a.sql", FileChangeKind.Conflict)], summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("someone else's\n", f.Repo.ReadFile("db/a.sql"));
        // The identical file is adopted into the manifest without a write.
        Assert.Contains("\"db/b.sql\"", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.DoesNotContain("\"db/a.sql\"", f.ManifestText("p"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Once_mode_writes_only_when_missing_and_never_deletes()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/scaffold", Out("db/seed.sql", "v1\n", OutputMode.Once))]);
        Assert.Contains($"\"o:{ContentHash.Of("v1\n")}\"", f.ManifestText("p"), StringComparison.Ordinal);
        f.Repo.WriteFile("db/seed.sql", "team owns this\n");

        var second = await f.RunAsync([Unit("p/scaffold", Out("db/seed.sql", "v2\n", OutputMode.Once))]);
        var third = await f.RunAsync([]);

        Assert.Equal(FileChangeKind.Kept, Assert.Single(second.Changes).Kind);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(FileChangeKind.OrphanedOwned, Assert.Single(third.Changes).Kind);
        Assert.Equal("team owns this\n", f.Repo.ReadFile("db/seed.sql"));
        Assert.Equal("", f.ManifestText("p"));
    }

    [Fact]
    public async Task Pair_mode_regenerates_the_main_file_and_writes_the_companion_once()
    {
        using var f = new WritingFixture();
        ProcessedUnit Pair(string generated, string companion) => Unit("p/entity:01E",
            Out("src/Generated/E.g.cs", generated, OutputMode.Pair),
            Out("src/Generated/E.cs", companion, OutputMode.Pair, FileRole.Companion));
        await f.RunAsync([Pair("partial class E { int A; }\n", "partial class E {}\n")]);
        f.Repo.WriteFile("src/Generated/E.cs", "partial class E { void Mine() {} }\n");

        var summary = await f.RunAsync([Pair("partial class E { int B; }\n", "partial class E {}\n")]);

        Assert.Equal([("src/Generated/E.cs", FileChangeKind.Kept), ("src/Generated/E.g.cs", FileChangeKind.Modified)],
            summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("partial class E { void Mine() {} }\n", f.Repo.ReadFile("src/Generated/E.cs"));
        Assert.Equal("partial class E { int B; }\n", f.Repo.ReadFile("src/Generated/E.g.cs"));
        Assert.Contains("\"entity:01E#companion\"", f.ManifestText("p"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regions_mode_ignores_edits_inside_regions_only()
    {
        using var f = new WritingFixture();
        const string v1 = "create table a (\n  id int\n  -- maquettiste:keep id=extra\n  -- maquettiste:end-keep\n);\n";
        await f.RunAsync([Unit("p/u", Out("db/a.sql", v1, OutputMode.Regions))]);
        Assert.Contains("\"r:", f.ManifestText("p"), StringComparison.Ordinal);

        // A hand edit inside the region: the post-processor carries it into the new output, which the writer accepts.
        const string inside = "create table a (\n  id int\n  -- maquettiste:keep id=extra\n  , note text\n  -- maquettiste:end-keep\n);\n";
        f.Repo.WriteFile("db/a.sql", inside);
        const string v2 = "create table a (\n  id bigint\n  -- maquettiste:keep id=extra\n  , note text\n  -- maquettiste:end-keep\n);\n";
        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", v2, OutputMode.Regions))]);
        Assert.Equal(FileChangeKind.Modified, Assert.Single(summary.Changes).Kind);
        Assert.Equal(v2, f.Repo.ReadFile("db/a.sql"));

        // A hand edit outside the regions is a hand edit.
        f.Repo.WriteFile("db/a.sql", v2.Replace("bigint", "smallint", StringComparison.Ordinal));
        var conflict = await f.RunAsync([Unit("p/u", Out("db/a.sql", v2.Replace("bigint", "numeric", StringComparison.Ordinal), OutputMode.Regions))]);
        Assert.Equal(FileChangeKind.Conflict, Assert.Single(conflict.Changes).Kind);
    }

    [Fact]
    public async Task Orphans_are_deleted_and_emptied_folders_removed_but_not_the_root()
    {
        using var f = new WritingFixture();
        await f.RunAsync(
        [
            Unit("p/keep", Out("db/keep.sql", "k\n")),
            Unit("p/gone", Out("db/deep/nested/gone.sql", "g\n"), Out("src/Generated/Only.cs", "o\n")),
        ]);

        var summary = await f.RunAsync([Unit("p/keep", Out("db/keep.sql", "k\n"))], diffs: true);

        Assert.Equal([("db/deep/nested/gone.sql", FileChangeKind.Deleted), ("src/Generated/Only.cs", FileChangeKind.Deleted)],
            summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal(2, summary.Deleted);
        Assert.Equal("--- a/db/deep/nested/gone.sql\n+++ b/db/deep/nested/gone.sql\n@@ -1 +0,0 @@\n-g\n", summary.Changes[0].Diff);
        Assert.False(Directory.Exists(f.Repo.PathOf("db/deep")));
        Assert.True(Directory.Exists(f.Repo.PathOf("src/Generated")));
        Assert.False(Directory.Exists(f.Repo.PathOf("src/Generated/x")));
        Assert.DoesNotContain("gone", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.DoesNotContain("Only.cs", f.ManifestText("p"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hand_edited_orphan_follows_the_policy()
    {
        using var f = new WritingFixture(HandEditPolicy.Fail);
        await f.RunAsync([Unit("p/gone", Out("db/gone.sql", "g\n"))]);
        f.Repo.WriteFile("db/gone.sql", "edited\n");

        var fail = await f.RunAsync([]);
        Assert.Equal(FileChangeKind.Conflict, Assert.Single(fail.Changes).Kind);
        Assert.True(f.Repo.Exists("db/gone.sql"));
        Assert.Contains("gone.sql", f.ManifestText("p"), StringComparison.Ordinal);

        f.Policy = HandEditPolicy.Skip;
        var skip = await f.RunAsync([]);
        Assert.Equal(FileChangeKind.HandEdited, Assert.Single(skip.Changes).Kind);
        Assert.True(f.Repo.Exists("db/gone.sql"));

        f.Policy = HandEditPolicy.Overwrite;
        var overwrite = await f.RunAsync([]);
        Assert.Equal(FileChangeKind.HandEdited, Assert.Single(overwrite.Changes).Kind);
        Assert.False(f.Repo.Exists("db/gone.sql"));
        Assert.Equal("", f.ManifestText("p"));
    }

    [Fact]
    public async Task Skipped_and_failed_units_keep_their_files()
    {
        using var f = new WritingFixture();
        await f.RunAsync(
        [
            Unit("p/skipped", Out("db/s.sql", "s\n")),
            Unit("p/failing", Out("db/f.sql", "f\n")),
        ]);
        var skippedState = f.State.Packs["p"]["p/skipped"];
        var failedState = f.State.Packs["p"]["p/failing"];

        var summary = await f.RunAsync([Unit("p/failing", failed: true)], skipped: [new SkippedUnit(Planned("p/skipped"), skippedState)]);

        Assert.Empty(summary.Changes);
        Assert.True(f.Repo.Exists("db/s.sql"));
        Assert.True(f.Repo.Exists("db/f.sql"));
        Assert.Contains("db/s.sql", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.Contains("db/f.sql", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.Same(skippedState, f.State.Packs["p"]["p/skipped"]);
        Assert.Same(failedState, f.State.Packs["p"]["p/failing"]);
    }

    [Fact]
    public async Task Removed_packs_are_orphaned_in_full_only_without_a_pack_filter()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("q/u", Out("db/q.sql", "q\n")), Unit("p/u", Out("db/p.sql", "p\n"))]);

        var filtered = await f.RunAsync([Unit("p/u", Out("db/p.sql", "p\n"))], counts: new Dictionary<string, int> { ["p"] = 1 });
        Assert.Empty(filtered.Changes);
        Assert.True(f.Repo.Exists("db/q.sql"));

        var all = await f.RunAsync([Unit("p/u", Out("db/p.sql", "p\n"))], allPacks: true, counts: new Dictionary<string, int> { ["p"] = 1 });
        Assert.Equal([("db/q.sql", FileChangeKind.Deleted)], all.Changes.Select(c => (c.Path, c.Kind)));
        Assert.False(f.Repo.Exists("db/q.sql"));
        Assert.Equal("", f.ManifestText("q"));
    }

    [Fact]
    public async Task A_file_moving_to_another_pack_is_not_deleted()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("q/u", Out("db/shared.sql", "x\n"))]);

        var summary = await f.RunAsync([Unit("p/u", Out("db/shared.sql", "x\n"))], allPacks: true);

        Assert.Empty(summary.Changes);
        Assert.True(f.Repo.Exists("db/shared.sql"));
        Assert.Contains("db/shared.sql", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.Equal("", f.ManifestText("q"));
    }

    [Fact]
    public async Task Regions_mode_works_under_any_root()
    {
        using var f = new WritingFixture();
        const string v1 = "a\n// maquettiste:keep id=x\nbody\n// maquettiste:end-keep\n";
        var summary = await f.RunAsync([Unit("p/u", Out("src/Generated/a.cs", v1, OutputMode.Regions))]);

        Assert.Empty(summary.Diagnostics);
        Assert.Equal(v1, f.Repo.ReadFile("src/Generated/a.cs"));
        Assert.Contains("\"r:", f.ManifestText("p"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_writes_nothing_and_returns_diffs()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n")), Unit("p/gone", Out("db/gone.sql", "g\n"))]);
        var manifest = f.ManifestText("p");
        var state = f.State.Packs["p"];

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "b\n")), Unit("p/new", Out("db/new.sql", "n\n"))],
            mode: GenerationMode.DryRun, diffs: true);

        Assert.Equal([("db/a.sql", FileChangeKind.Modified), ("db/gone.sql", FileChangeKind.Deleted), ("db/new.sql", FileChangeKind.Added)],
            summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.All(summary.Changes, c => Assert.NotNull(c.Diff));
        Assert.Equal((0, 0), (summary.Written, summary.Deleted));
        Assert.Equal("a\n", f.Repo.ReadFile("db/a.sql"));
        Assert.True(f.Repo.Exists("db/gone.sql"));
        Assert.False(f.Repo.Exists("db/new.sql"));
        Assert.Equal(manifest, f.ManifestText("p"));
        Assert.Same(state, f.State.Packs["p"]);
        Assert.False(System.IO.File.Exists(f.Journal.FilePath));
    }

    [Fact]
    public async Task Check_covers_every_root_and_writes_nothing()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"), Out("src/Generated/A.cs", "A\n"), Out("src/Generated/B.cs", "B\n"))]);
        f.Repo.WriteFile("src/Generated/A.cs", "edited\n");

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "a2\n"), Out("src/Generated/A.cs", "A\n"), Out("src/Generated/C.cs", "C\n"))],
            mode: GenerationMode.Check);

        Assert.Equal(
            [("db/a.sql", FileChangeKind.Modified), ("src/Generated/A.cs", FileChangeKind.Conflict), ("src/Generated/B.cs", FileChangeKind.Deleted),
                ("src/Generated/C.cs", FileChangeKind.Added)],
            summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("a\n", f.Repo.ReadFile("db/a.sql"));
        Assert.True(f.Repo.Exists("src/Generated/B.cs"));
        Assert.False(f.Repo.Exists("src/Generated/C.cs"));
    }

    [Fact]
    public async Task Refused_and_colliding_paths_are_reported_and_not_written()
    {
        using var f = new WritingFixture(deny: ["db/secret/**"]);

        var summary = await f.RunAsync(
        [
            Unit("p/a", Out("db/Invoice.sql", "1\n"), Out("db/../escape.sql", "x\n"), Out("db/secret/k.sql", "k\n")),
            Unit("p/b", Out("db/invoice.sql", "2\n"), Out("db/Invoice.sql", "3\n")),
        ]);

        Assert.Equal(
            [("MQ6004", "db/../escape.sql"), ("MQ6004", "db/secret/k.sql"), ("MQ6005", "db/Invoice.sql"), ("MQ6005", "db/invoice.sql")],
            summary.Diagnostics.Select(d => (d.Rule, d.FilePath!)).OrderBy(x => x.Item1, StringComparer.Ordinal).ThenBy(x => x.Item2, StringComparer.Ordinal));
        Assert.Equal("1\n", f.Repo.ReadFile("db/Invoice.sql"));
        Assert.Equal(["db/Invoice.sql"], f.Repo.ListFiles().Where(p => !p.StartsWith(".maquettiste", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Plan_apply_writes_nothing_outside_the_plan()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n")), Unit("p/gone", Out("db/gone.sql", "g\n"))]);
        var planned = new HashSet<string>(StringComparer.Ordinal) { "db/a.sql" };

        var summary = await f.RunAsync([Unit("p/u", Out("db/a.sql", "b\n"), Out("db/extra.sql", "x\n"))], planned: planned);

        Assert.Equal("b\n", f.Repo.ReadFile("db/a.sql"));
        Assert.False(f.Repo.Exists("db/extra.sql"));
        Assert.True(f.Repo.Exists("db/gone.sql"));
        Assert.Equal(["db/extra.sql", "db/gone.sql"], summary.Diagnostics.Where(d => d.Rule == "MQ6004").Select(d => d.FilePath));
    }

    [Fact]
    public async Task Content_omitted_files_are_never_written_and_conflict_when_changed()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Unit("p/u", Out("db/a.sql", "a\n"), Out("db/b.sql", "b\n"))]);
        f.Repo.WriteFile("db/b.sql", "edited\n");
        var a = Out("db/a.sql", "a\n") with { Content = ReadOnlyMemory<byte>.Empty, ContentOmitted = true };
        var b = Out("db/b.sql", "b\n") with { Content = ReadOnlyMemory<byte>.Empty, ContentOmitted = true };

        var summary = await f.RunAsync([Unit("p/u", a, b)]);

        Assert.Equal([("db/b.sql", FileChangeKind.Conflict)], summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("a\n", f.Repo.ReadFile("db/a.sql"));
        Assert.Equal("edited\n", f.Repo.ReadFile("db/b.sql"));
        Assert.Equal(0, summary.Written);
    }

    [Fact]
    public async Task Pack_closes_when_its_last_unit_arrives()
    {
        using var f = new WritingFixture();
        var manifests = await f.LoadManifestsAsync();
        await f.Journal.BeginAsync("RUN", null, ["p", "q"], Ct);

        async IAsyncEnumerable<ProcessedUnit> Units()
        {
            yield return Unit("p/u", Out("db/p.sql", "p\n"));
            await Task.Yield();
            // p has closed: its manifest is on disk and the journal holds its pack line before q starts.
            Assert.Contains("db/p.sql", f.ManifestText("p"), StringComparison.Ordinal);
            Assert.Contains("{\"t\":\"pack\",\"pack\":\"p\"}", System.IO.File.ReadAllText(f.Journal.FilePath), StringComparison.Ordinal);
            yield return Unit("q/u", Out("db/q.sql", "q\n"));
        }

        var summary = await f.Writer.WriteAsync(Units(), f.Context(manifests, counts: new Dictionary<string, int> { ["p"] = 1, ["q"] = 1 }), null, Ct);
        await f.Journal.EndAsync(Ct);

        Assert.Equal(2, summary.Written);
    }

    [Fact]
    public async Task Output_is_identical_with_one_or_many_writer_tasks()
    {
        async Task<(string Manifest, string Changes)> RunWith(int jobs)
        {
            using var f = new WritingFixture();
            f.Writer = new Engine.Writing.OutputWriter(f.Repo.Options with { MaxDegreeOfParallelism = jobs }, f.Paths, f.Manifests, new Engine.Writing.DiffGenerator());
            var units = Enumerable.Range(0, 300)
                .Select(i => Unit($"p/u:{i:D3}", Out($"src/Generated/F{i % 7}/file{i}.cs", $"// {i}\n")))
                .ToList();
            var summary = await f.RunAsync(units, diffs: true);
            return (f.ManifestText("p"), string.Join("\n", summary.Changes.Select(c => $"{c.Path} {c.Kind} {c.NewHash} {c.Diff}")));
        }

        var one = await RunWith(1);
        var many = await RunWith(8);

        Assert.Equal(one, many);
    }

    [Fact]
    public async Task Progress_is_reported_per_file()
    {
        using var f = new WritingFixture();
        var updates = new List<ProgressUpdate>();
        var progress = new SyncProgress(updates);
        var manifests = await f.LoadManifestsAsync();

        await f.Writer.WriteAsync(Stream([Unit("p/u", Out("db/a.sql", "a\n"), Out("db/b.sql", "b\n"))], Ct),
            f.Context(manifests, GenerationMode.DryRun), progress, Ct);

        Assert.Equal(2, updates.Count);
        Assert.All(updates, u => Assert.Equal(PipelineStage.Write, u.Stage));
        Assert.Contains(updates, u => u.Done == 2);
    }

    private sealed class SyncProgress(List<ProgressUpdate> updates) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value)
        {
            lock (updates)
                updates.Add(value);
        }
    }
}
