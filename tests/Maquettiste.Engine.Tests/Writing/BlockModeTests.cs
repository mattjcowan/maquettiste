using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;
using static Maquettiste.Engine.Tests.Writing.WritingFixture;

namespace Maquettiste.Engine.Tests.Writing;

/// <summary>The writer's <c>block</c> mode (engine-design.md section 12.3b): every branch of the decision and of the orphan.</summary>
public sealed class BlockModeTests
{
    private const string Begin = "# maquettiste: begin p/ignore\n";
    private const string End = "# maquettiste: end p/ignore\n";
    private const string Two = "node_modules/\n*.log\n";

    private static readonly DateTime Old = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ProcessedUnit Ignore(string lines = "/Generated/\n", bool createFile = false, string? comment = null) =>
        BlockUnit("p/ignore", ".gitignore", lines, createFile, comment);

    private static string Bytes(WritingFixture f, string path) => Encoding.UTF8.GetString(File.ReadAllBytes(f.Repo.PathOf(path)));

    [Fact]
    public async Task A_block_is_inserted_after_a_blank_line_and_a_second_run_changes_nothing()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);

        var first = await f.RunAsync([Ignore()]);

        Assert.Equal([(".gitignore", FileChangeKind.Added)], first.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Empty(first.Diagnostics);
        Assert.Equal(Two + "\n" + Begin + "/Generated/\n" + End, Bytes(f, ".gitignore"));
        Assert.Contains($"[\".gitignore\", \"b:{ContentHash.Of("/Generated/\n")}\", \"ignore\"]", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.Equal("b:" + ContentHash.Of("/Generated/\n"), Assert.Single(f.State.Packs["p"]["p/ignore"].Outputs).ManifestHash);

        var bytes = File.ReadAllBytes(f.Repo.PathOf(".gitignore"));
        File.SetLastWriteTimeUtc(f.Repo.PathOf(".gitignore"), Old);
        var second = await f.RunAsync([Ignore()]);

        Assert.Empty(second.Changes);
        Assert.Equal(0, second.Written);
        Assert.Equal(bytes, File.ReadAllBytes(f.Repo.PathOf(".gitignore")));
        Assert.Equal(Old, File.GetLastWriteTimeUtc(f.Repo.PathOf(".gitignore")));
    }

    [Theory]
    [InlineData("a", "a\n\n")]
    [InlineData("a\n\n", "a\n\n")]
    [InlineData("", "")]
    [InlineData("\r\nx\r\n", "\r\nx\r\n\n")]
    public async Task Insert_adds_a_line_end_and_one_blank_line_only_when_needed(string before, string head)
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", before);

        await f.RunAsync([Ignore()]);

        Assert.Equal(head + Begin + "/Generated/\n" + End, Bytes(f, ".gitignore"));
    }

    [Fact]
    public async Task A_missing_file_is_created_only_with_create_file_and_is_marked_created()
    {
        using var f = new WritingFixture();

        var skipped = await f.RunAsync([Ignore()]);

        Assert.Empty(skipped.Changes);
        var info = Assert.Single(skipped.Diagnostics);
        Assert.Equal(("MQ6028", DiagnosticSeverity.Info), (info.Rule, info.Severity));
        Assert.Contains("target-missing", info.Message, StringComparison.Ordinal);
        Assert.False(f.Repo.Exists(".gitignore"));
        Assert.Equal("", f.ManifestText("p"));
        // No state: the unit renders again next run, so a file created later gets its block.
        Assert.False(f.State.Packs.TryGetValue("p", out var none) && none.ContainsKey("p/ignore"));

        var created = await f.RunAsync([Ignore(createFile: true)]);

        Assert.Equal([(".gitignore", FileChangeKind.Added)], created.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal(Begin + "/Generated/\n" + End, Bytes(f, ".gitignore"));
        Assert.Contains($"\"bc:{ContentHash.Of("/Generated/\n")}\"", f.ManifestText("p"), StringComparison.Ordinal);

        // An update keeps the "created" mark.
        await f.RunAsync([Ignore("/Generated/\n/bin/\n", createFile: true)]);
        Assert.Contains($"\"bc:{ContentHash.Of("/Generated/\n/bin/\n")}\"", f.ManifestText("p"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_replaces_only_the_block_and_edits_outside_it_are_never_reported()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);
        f.Repo.WriteFile(".gitignore", "# mine\r\n" + Bytes(f, ".gitignore") + "trailing  \n");

        var same = await f.RunAsync([Ignore()]);
        Assert.Empty(same.Changes);
        Assert.Empty(same.Diagnostics);

        var changed = await f.RunAsync([Ignore("/Generated/\n/obj/\n")], diffs: true);

        Assert.Equal([(".gitignore", FileChangeKind.Modified)], changed.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("# mine\r\n" + Two + "\n" + Begin + "/Generated/\n/obj/\n" + End + "trailing  \n", Bytes(f, ".gitignore"));
    }

    [Fact]
    public async Task A_new_comment_rewrites_the_delimiter_lines()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);

        var summary = await f.RunAsync([Ignore(comment: "//")]);

        Assert.Equal(FileChangeKind.Modified, Assert.Single(summary.Changes).Kind);
        Assert.Equal(Two + "\n// maquettiste: begin p/ignore\n/Generated/\n// maquettiste: end p/ignore\n", Bytes(f, ".gitignore"));
        Assert.Empty((await f.RunAsync([Ignore(comment: "//")])).Changes);
    }

    [Theory]
    [InlineData(HandEditPolicy.Fail, FileChangeKind.Conflict, "/Edited/\n")]
    [InlineData(HandEditPolicy.Skip, FileChangeKind.HandEdited, "/Edited/\n")]
    [InlineData(HandEditPolicy.Overwrite, FileChangeKind.HandEdited, "/Generated/\n/obj/\n")]
    public async Task A_hand_edit_inside_the_block_follows_the_policy(HandEditPolicy policy, FileChangeKind kind, string body)
    {
        using var f = new WritingFixture(policy);
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);
        f.Repo.WriteFile(".gitignore", Bytes(f, ".gitignore").Replace("/Generated/\n", "/Edited/\n", StringComparison.Ordinal));
        var manifest = f.ManifestText("p");

        var summary = await f.RunAsync([Ignore("/Generated/\n/obj/\n")]);

        Assert.Equal(kind, Assert.Single(summary.Changes).Kind);
        Assert.Equal("MQ6009", Assert.Single(summary.Diagnostics).Rule);
        Assert.Equal(Two + "\n" + Begin + body + End, Bytes(f, ".gitignore"));
        if (policy != HandEditPolicy.Overwrite)
            Assert.Equal(manifest, f.ManifestText("p"));
    }

    [Fact]
    public async Task A_block_removed_by_hand_is_a_hand_edit_and_an_untracked_block_is_adopted_when_identical()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);
        f.Repo.WriteFile(".gitignore", Two);

        var removed = await f.RunAsync([Ignore()]);
        Assert.Equal(FileChangeKind.Conflict, Assert.Single(removed.Changes).Kind);
        Assert.Contains("removed by hand", Assert.Single(removed.Diagnostics).Message, StringComparison.Ordinal);
        Assert.Equal(Two, Bytes(f, ".gitignore"));

        using var g = new WritingFixture();
        g.Repo.WriteFile(".gitignore", Two + Begin + "/Generated/\n" + End);
        var adopted = await g.RunAsync([Ignore()]);
        Assert.Empty(adopted.Changes);
        Assert.Contains("\".gitignore\"", g.ManifestText("p"), StringComparison.Ordinal);

        using var h = new WritingFixture();
        h.Repo.WriteFile(".gitignore", Two + Begin + "/Other/\n" + End);
        var untracked = await h.RunAsync([Ignore()]);
        Assert.Equal(FileChangeKind.Conflict, Assert.Single(untracked.Changes).Kind);
        Assert.Contains("untracked block", Assert.Single(untracked.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Two + Begin + "a\n" + End + Begin + "b\n" + End)]
    [InlineData(Two + Begin + "a\n")]
    [InlineData(Two + End)]
    public async Task A_file_with_the_block_twice_or_unclosed_is_left_alone(string text)
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", text);

        var summary = await f.RunAsync([Ignore()]);

        Assert.Equal(FileChangeKind.Conflict, Assert.Single(summary.Changes).Kind);
        Assert.Equal(("MQ6027", DiagnosticSeverity.Error), (Assert.Single(summary.Diagnostics).Rule, summary.Diagnostics[0].Severity));
        Assert.Equal(text, Bytes(f, ".gitignore"));
    }

    [Fact]
    public async Task Removing_the_unit_removes_the_block_and_gives_the_file_back()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);

        var summary = await f.RunAsync([], diffs: false);

        Assert.Equal([(".gitignore", FileChangeKind.Deleted)], summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal(Two, Bytes(f, ".gitignore"));
        Assert.Equal("", f.ManifestText("p"));
        Assert.Equal((1, 0), (summary.Written, summary.Deleted));
    }

    [Fact]
    public async Task Removing_a_block_keeps_the_lines_around_it()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);
        f.Repo.WriteFile(".gitignore", "top\n" + Bytes(f, ".gitignore") + "after\n");

        await f.RunAsync([]);

        Assert.Equal("top\n" + Two + "\nafter\n", Bytes(f, ".gitignore"));
    }

    [Fact]
    public async Task A_created_file_is_deleted_with_its_block_only_when_nothing_else_is_left()
    {
        using var f = new WritingFixture();
        await f.RunAsync([Ignore(createFile: true)]);
        var gone = await f.RunAsync([]);
        Assert.Equal((0, 1), (gone.Written, gone.Deleted));
        Assert.False(f.Repo.Exists(".gitignore"));

        await f.RunAsync([Ignore(createFile: true)]);
        f.Repo.WriteFile(".gitignore", Bytes(f, ".gitignore") + "mine/\n");
        await f.RunAsync([]);
        Assert.Equal("mine/\n", Bytes(f, ".gitignore"));

        // A file the engine did not create stays, even when only whitespace is left.
        using var g = new WritingFixture();
        g.Repo.WriteFile(".gitignore", "\n");
        await g.RunAsync([Ignore()]);
        await g.RunAsync([]);
        Assert.True(g.Repo.Exists(".gitignore"));
        Assert.Equal("\n", Bytes(g, ".gitignore"));
    }

    [Theory]
    [InlineData(HandEditPolicy.Fail, FileChangeKind.Conflict, true)]
    [InlineData(HandEditPolicy.Skip, FileChangeKind.HandEdited, true)]
    [InlineData(HandEditPolicy.Overwrite, FileChangeKind.HandEdited, false)]
    public async Task An_edited_orphan_block_follows_the_policy(HandEditPolicy policy, FileChangeKind kind, bool kept)
    {
        using var f = new WritingFixture(policy);
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);
        f.Repo.WriteFile(".gitignore", Bytes(f, ".gitignore").Replace("/Generated/\n", "/Edited/\n", StringComparison.Ordinal));

        var summary = await f.RunAsync([]);

        Assert.Equal(kind, Assert.Single(summary.Changes).Kind);
        Assert.Equal(kept, Bytes(f, ".gitignore").Contains("/Edited/", StringComparison.Ordinal));
        Assert.Equal(kept, f.ManifestText("p").Contains(".gitignore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_orphan_whose_block_is_gone_drops_its_entry_and_a_doubled_one_is_kept()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);
        f.Repo.WriteFile(".gitignore", Two + "x\n");

        var gone = await f.RunAsync([]);
        Assert.Equal(FileChangeKind.Deleted, Assert.Single(gone.Changes).Kind);
        Assert.Equal(Two + "x\n", Bytes(f, ".gitignore"));
        Assert.Equal("", f.ManifestText("p"));

        using var g = new WritingFixture();
        g.Repo.WriteFile(".gitignore", Two);
        await g.RunAsync([Ignore()]);
        var doubled = Bytes(g, ".gitignore") + Begin + "/Generated/\n" + End;
        g.Repo.WriteFile(".gitignore", doubled);
        var broken = await g.RunAsync([]);
        Assert.Equal("MQ6027", Assert.Single(broken.Diagnostics).Rule);
        Assert.Equal(doubled, Bytes(g, ".gitignore"));
        Assert.Contains(".gitignore", g.ManifestText("p"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_and_check_compare_the_block_and_write_nothing()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore()]);
        var before = Bytes(f, ".gitignore");

        var dry = await f.RunAsync([Ignore("/Generated/\n/obj/\n")], mode: GenerationMode.DryRun, diffs: true);
        Assert.Equal(FileChangeKind.Modified, Assert.Single(dry.Changes).Kind);
        Assert.Contains("+/obj/", dry.Changes[0].Diff, StringComparison.Ordinal);

        var check = await f.RunAsync([Ignore("/Generated/\n/obj/\n")], mode: GenerationMode.Check);
        Assert.Equal(FileChangeKind.Modified, Assert.Single(check.Changes).Kind);
        Assert.Empty((await f.RunAsync([Ignore()], mode: GenerationMode.Check)).Changes);

        var orphan = await f.RunAsync([], mode: GenerationMode.Check);
        Assert.Equal(FileChangeKind.Deleted, Assert.Single(orphan.Changes).Kind);
        Assert.Equal(before, Bytes(f, ".gitignore"));
    }

    [Fact]
    public async Task Bytes_the_engine_wrote_here_are_not_a_hand_edit_when_the_manifest_moved_on()
    {
        using var f = new WritingFixture();
        f.Repo.WriteFile(".gitignore", Two);
        await f.RunAsync([Ignore(), Unit("p/u", Out("src/Generated/A.cs", "A1\n"))]);

        // A pull brings a manifest that records newer outputs than this checkout generated (the outputs are not committed).
        var manifest = f.ManifestText("p")
            .Replace(ContentHash.Of("A1\n"), ContentHash.Of("A2\n"), StringComparison.Ordinal)
            .Replace(ContentHash.Of("/Generated/\n"), ContentHash.Of("/Pulled/\n"), StringComparison.Ordinal);
        File.WriteAllText(f.Manifests.FileOf("p"), manifest);

        var summary = await f.RunAsync([Ignore("/Pulled/\n"), Unit("p/u", Out("src/Generated/A.cs", "A2\n"))]);

        Assert.Empty(summary.Diagnostics);
        Assert.Equal([(".gitignore", FileChangeKind.Modified), ("src/Generated/A.cs", FileChangeKind.Modified)], summary.Changes.Select(c => (c.Path, c.Kind)));
        Assert.Equal("A2\n", f.Repo.ReadFile("src/Generated/A.cs"));

        // A real hand edit is still one.
        f.Repo.WriteFile("src/Generated/A.cs", "mine\n");
        var edited = await f.RunAsync([Ignore("/Pulled/\n"), Unit("p/u", Out("src/Generated/A.cs", "A3\n"))]);
        Assert.Equal("MQ6009", Assert.Single(edited.Diagnostics).Rule);
    }

    [Fact]
    public async Task The_scanner_and_the_edits_are_byte_exact()
    {
        var text = Encoding.UTF8.GetBytes("a\r\n  // maquettiste: begin p/x  \r\nold\r\n\t// maquettiste: end p/x\r\nz");
        var scan = ManagedBlock.Find(text, "p/x");
        Assert.True(scan.Found);
        Assert.Equal("old\r\n", Encoding.UTF8.GetString(text.AsSpan(scan.BodyStart, scan.BodyEnd - scan.BodyStart)));
        Assert.Equal("a\r\n# maquettiste: begin p/x\nnew\n# maquettiste: end p/x\nz", Encoding.UTF8.GetString(ManagedBlock.Replace(text, scan, "#", "p/x", "new\n")));
        Assert.Equal("a\r\nz", Encoding.UTF8.GetString(ManagedBlock.Remove(text, scan)));
        Assert.False(ManagedBlock.Find(Encoding.UTF8.GetBytes("# maquettiste: begin p/xy\n# maquettiste: end p/xy\n"), "p/x").Found);
        Assert.True(ManagedBlock.HoldsDelimiter("a\n; maquettiste: end p/x\n", "p/x"));
        Assert.Equal("p/ignore", ManagedBlock.Marker("p", "ignore:01ABC#companion"));
        Assert.Equal("p/ignore", ManagedBlock.MarkerOfKey("p/ignore:01ABC"));
    }
}
