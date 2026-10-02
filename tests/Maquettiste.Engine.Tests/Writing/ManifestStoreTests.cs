using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Writing;

public sealed class ManifestStoreTests
{
    private static readonly string H1 = new('a', 64);
    private static readonly string H2 = new('b', 64);
    private static readonly string H3 = new('c', 64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_writes_one_sorted_entry_per_line_in_the_model_folder()
    {
        using var f = new WritingFixture();

        await f.Manifests.SavePackAsync("sql-ddl",
        [
            new ManifestEntry("db/main/tables/team.sql", "r:" + H2, "table:01JB2Q0N"),
            new ManifestEntry("db/main/tables/invoice.sql", H1, "table:01JB2Q0M"),
            new ManifestEntry("db/main/migrations/0001.sql", "o:" + H3, "migration"),
            new ManifestEntry(".gitignore", "bc:" + H1, "ignore"),
        ], Ct);

        var expected =
            "{\n" +
            "  \"$schema\": \"../.schema/v1/manifest.json\",\n" +
            "  \"pack\": \"sql-ddl\",\n" +
            "  \"files\": [\n" +
            $"    [\".gitignore\", \"bc:{H1}\", \"ignore\"],\n" +
            $"    [\"db/main/migrations/0001.sql\", \"o:{H3}\", \"migration\"],\n" +
            $"    [\"db/main/tables/invoice.sql\", \"{H1}\", \"table:01JB2Q0M\"],\n" +
            $"    [\"db/main/tables/team.sql\", \"r:{H2}\", \"table:01JB2Q0N\"]\n" +
            "  ]\n" +
            "}\n";
        Assert.Equal(expected, f.ManifestText("sql-ddl"));
        Assert.Equal(Path.Combine(f.Repo.ModelRoot, "manifest", "sql-ddl.json"), f.Manifests.FileOf("sql-ddl"));
    }

    [Fact]
    public async Task Saved_manifest_is_valid_against_the_manifest_schema()
    {
        using var f = new WritingFixture();
        await f.Manifests.SavePackAsync("p", [new ManifestEntry("db/a.sql", H1, "u"), new ManifestEntry("db/b.sql", "o:" + H2, "u#companion"),
            new ManifestEntry(".gitignore", "b:" + H3, "ignore")], Ct);

        using var document = JsonDocument.Parse(f.ManifestText("p"));
        var diagnostics = TestServices.Schemas.Evaluate("manifest.json", document.RootElement, ".maquettiste/manifest/p.json");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Save_is_byte_stable_whatever_the_input_order()
    {
        using var f = new WritingFixture();
        var entries = new[]
        {
            new ManifestEntry("db/b.sql", H2, "u:2"),
            new ManifestEntry("db/a.sql", H1, "u:1"),
            new ManifestEntry("db/é.sql", H3, "u:3"),
            new ManifestEntry("db/\U0001F600.sql", H3, "u:4"),
            new ManifestEntry("db/Ａ.sql", H3, "u:5"),
        };

        await f.Manifests.SavePackAsync("p", entries, Ct);
        var first = File.ReadAllBytes(f.Manifests.FileOf("p"));
        var firstWrite = File.GetLastWriteTimeUtc(f.Manifests.FileOf("p"));
        File.SetLastWriteTimeUtc(f.Manifests.FileOf("p"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await f.Manifests.SavePackAsync("p", [.. entries.Reverse()], Ct);
        var second = File.ReadAllBytes(f.Manifests.FileOf("p"));

        Assert.Equal(first, second);
        Assert.NotEqual(firstWrite, File.GetLastWriteTimeUtc(f.Manifests.FileOf("p")));
        Assert.Equal(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(f.Manifests.FileOf("p")));
        // Ordinal UTF-8 order: U+FF21 (EF BC A1) sorts before U+1F600 (F0 9F 98 80), unlike UTF-16 ordinal order.
        var text = Encoding.UTF8.GetString(first);
        Assert.Contains("\"db/\uFF21.sql\"", text, StringComparison.Ordinal);
        // The canonical encoder writes supplementary characters as escaped surrogate pairs.
        Assert.True(text.IndexOf("Ａ", StringComparison.Ordinal) < text.IndexOf("\\uD83D\\uDE00", StringComparison.OrdinalIgnoreCase), text);
        Assert.True(text.IndexOf("db/a.sql", StringComparison.Ordinal) < text.IndexOf("db/b.sql", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', text);
        Assert.Empty(f.TempFiles());
    }

    [Fact]
    public async Task A_cache_copy_an_earlier_release_left_is_read_once_then_moved_into_the_model_folder()
    {
        using var f = new WritingFixture();
        await f.Manifests.SavePackAsync("p", [new ManifestEntry("db/a.sql", H1, "u")], Ct);
        // What a release before 0.5.5 wrote for roots not marked commit.
        var legacy = Path.Combine(f.Repo.ModelRoot, ".cache", "manifest", "p.json");
        Assert.Equal(legacy, f.Manifests.LegacyFileOf("p"));
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy,
            "{\n  \"$schema\": \"../../.schema/v1/manifest.json\",\n  \"pack\": \"p\",\n  \"files\": [\n" +
            $"    [\"db/a.sql\", \"{H3}\", \"u\"],\n    [\"src/Generated/a.cs\", \"{H2}\", \"u\"]\n  ]\n}}\n");
        // A pack known only by its cache copy is listed too.
        Directory.CreateDirectory(f.Manifests.LegacyFolder);
        File.Copy(legacy, f.Manifests.LegacyFileOf("q"));

        var set = await f.Manifests.LoadAsync([], Ct);

        Assert.Equal(["p", "q"], set.Packs);
        // The model folder's entry wins a path both list.
        Assert.Equal([new ManifestEntry("db/a.sql", H1, "u"), new ManifestEntry("src/Generated/a.cs", H2, "u")], set.Entries("p"));

        await f.Manifests.SavePackAsync("p", set.Entries("p"), Ct);

        Assert.False(File.Exists(legacy));
        Assert.Contains("src/Generated/a.cs", f.ManifestText("p"), StringComparison.Ordinal);
        Assert.Equal(set.Entries("p"), (await f.Manifests.LoadAsync(["p"], Ct)).Entries("p"));
        Assert.True(File.Exists(f.Manifests.LegacyFileOf("q")));
    }

    [Fact]
    public async Task Empty_manifest_deletes_the_file()
    {
        using var f = new WritingFixture();
        await f.Manifests.SavePackAsync("p", [new ManifestEntry("db/a.sql", H1, "u")], Ct);

        await f.Manifests.SavePackAsync("p", [], Ct);

        Assert.False(File.Exists(f.Manifests.FileOf("p")));
    }

    [Fact]
    public async Task Duplicate_paths_are_refused()
    {
        using var f = new WritingFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            f.Manifests.SavePackAsync("p", [new ManifestEntry("db/a.sql", H1, "u"), new ManifestEntry("db/a.sql", H2, "v")], Ct));
    }

    [Fact]
    public async Task A_pack_name_that_escapes_the_folder_is_refused()
    {
        using var f = new WritingFixture();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            f.Manifests.SavePackAsync("../../escape", [new ManifestEntry("db/a.sql", H1, "u")], Ct));
    }

    [Fact]
    public async Task Load_round_trips_every_pack()
    {
        using var f = new WritingFixture();
        await f.Manifests.SavePackAsync("p", [new ManifestEntry("db/a.sql", H1, "u:1"), new ManifestEntry("src/Generated/a.cs", H2, "u:1#companion")], Ct);
        await f.Manifests.SavePackAsync("q", [new ManifestEntry("db/q.sql", H3, "v")], Ct);

        var all = await f.Manifests.LoadAsync([], Ct);
        var onlyP = await f.Manifests.LoadAsync(["p", "missing"], Ct);

        Assert.Equal(["p", "q"], all.Packs);
        Assert.Equal([new ManifestEntry("db/a.sql", H1, "u:1"), new ManifestEntry("src/Generated/a.cs", H2, "u:1#companion")], all.Entries("p"));
        Assert.True(all.TryGet("db/q.sql", out var entry, out var pack));
        Assert.Equal(("q", H3), (pack, entry.Hash));
        Assert.False(all.TryGet("db/none.sql", out _, out _));
        Assert.Equal(["p"], onlyP.Packs);
        Assert.Empty(onlyP.Entries("q"));
    }

    [Fact]
    public async Task Load_salvages_a_manifest_with_merge_conflict_markers()
    {
        using var f = new WritingFixture();
        Directory.CreateDirectory(Path.Combine(f.Repo.ModelRoot, "manifest"));
        var text =
            "{\n  \"$schema\": \"../.schema/v1/manifest.json\",\n  \"pack\": \"p\",\n  \"files\": [\n" +
            $"    [\"db/a.sql\", \"{H1}\", \"u:1\"],\n" +
            "<<<<<<< HEAD\n" +
            $"    [\"db/b.sql\", \"{H2}\", \"u:2\"],\n" +
            "=======\n" +
            $"    [\"db/b.sql\", \"{H3}\", \"u:2\"],\n" +
            $"    [\"db/c.sql\", \"{H3}\", \"u:3\"],\n" +
            ">>>>>>> theirs\n" +
            $"    [\"db/d.sql\", \"{H1}\", \"u:4\"]\n  ]\n}}\n";
        File.WriteAllText(f.Manifests.FileOf("p"), text);

        var set = await f.Manifests.LoadAsync(["p"], Ct);

        Assert.Equal(["db/a.sql", "db/b.sql", "db/c.sql", "db/d.sql"], set.Entries("p").Select(e => e.Path));
        Assert.Equal(H2, set.Entries("p")[1].Hash);
    }

    [Fact]
    public void Empty_manifest_set_has_nothing()
    {
        var set = new ManifestSet();

        Assert.Empty(set.Packs);
        Assert.Empty(set.Entries("p"));
        Assert.False(set.TryGet("x", out _, out _));
    }

    [Fact]
    public async Task Journal_overlay_applies_packs_without_a_pack_line()
    {
        using var f = new WritingFixture();
        await f.Manifests.SavePackAsync("p", [new ManifestEntry("db/a.sql", H1, "u"), new ManifestEntry("db/old.sql", H1, "u")], Ct);
        await f.Manifests.SavePackAsync("q", [new ManifestEntry("db/q.sql", H1, "v")], Ct);
        var set = await f.Manifests.LoadAsync([], Ct);

        var overlaid = set.WithJournalOverlay(
        [
            new JournalRecord("begin", null, null, null, null),
            new JournalRecord("write", "p", "db/a.sql", H2, "u"),
            new JournalRecord("write", "p", "db/new.sql", H3, "u2"),
            new JournalRecord("delete", "p", "db/old.sql", null, null),
            new JournalRecord("write", "q", "db/q.sql", H3, "v"),
            new JournalRecord("pack", "q", null, null, null),
        ]);

        Assert.True(overlaid.TryGet("db/a.sql", out var a, out _));
        Assert.Equal(H2, a.Hash);
        Assert.True(overlaid.TryGet("db/new.sql", out var created, out var pack));
        Assert.Equal(("p", H3), (pack, created.Hash));
        Assert.False(overlaid.TryGet("db/old.sql", out _, out _));
        // q finished: its saved manifest already holds its writes, so the journal line is not applied again.
        Assert.True(overlaid.TryGet("db/q.sql", out var q, out _));
        Assert.Equal(H1, q.Hash);
        // A path only the journal knows is not listed until a manifest holds it.
        Assert.Equal(["db/a.sql"], overlaid.Entries("p").Select(e => e.Path));
        // The original set is unchanged.
        Assert.True(set.TryGet("db/old.sql", out _, out _));
    }

    [Fact]
    public void Utf8_ordinal_order_is_code_point_order_for_any_strings()
    {
        // The comparer compares UTF-16 units up to the first difference and runes only when a surrogate is there; the result must
        // be the rune order for every pair, including supplementary characters against units at or above U+E000.
        string[] samples =
        [
            "", "a", "ab", "b", "a/b", "a\uE000", "a\uFFFF", "a\U0001F600", "a\U0001F600b", "a\U00010000", "\uD800", "\uDC00x", "a\uD800",
            "é", "e\u0301", "\u00FF", "\u0100", "Z", "z", "\uFB01",
        ];
        foreach (var x in samples)
        {
            foreach (var y in samples)
                Assert.Equal(Math.Sign(RuneOrder(x, y)), Math.Sign(Utf8OrdinalComparer.Instance.Compare(x, y)));
        }
    }

    [Fact]
    public async Task Save_escapes_what_needs_escaping_and_writes_plain_ascii_as_is()
    {
        using var f = new WritingFixture();

        await f.Manifests.SavePackAsync("p",
        [
            new ManifestEntry("db/plain-name_1.sql", H1, "u:1"),
            new ManifestEntry("db/quote\"back\\slash.sql", H2, "u:2"),
            new ManifestEntry("db/é\U0001F600.sql", H3, "u:<3>&'"),
        ], Ct);

        var text = f.ManifestText("p");
        Assert.Contains($"    [\"db/plain-name_1.sql\", \"{H1}\", \"u:1\"],\n", text, StringComparison.Ordinal);
        Assert.Contains("\"db/quote\\\"back\\\\slash.sql\"", text, StringComparison.Ordinal);
        Assert.Contains("\"u:<3>&'\"", text, StringComparison.Ordinal);
        var loaded = await f.LoadManifestsAsync();
        Assert.True(loaded.TryGet("db/quote\"back\\slash.sql", out var entry, out _));
        Assert.Equal(H2, entry.Hash);
        Assert.True(loaded.TryGet("db/é\U0001F600.sql", out _, out _));
    }

    private static int RuneOrder(string x, string y)
    {
        var ex = x.EnumerateRunes();
        var ey = y.EnumerateRunes();
        while (true)
        {
            var hx = ex.MoveNext();
            var hy = ey.MoveNext();
            if (!hx || !hy)
                return hx == hy ? 0 : hx ? 1 : -1;
            var c = ex.Current.Value.CompareTo(ey.Current.Value);
            if (c != 0)
                return c;
        }
    }
}
