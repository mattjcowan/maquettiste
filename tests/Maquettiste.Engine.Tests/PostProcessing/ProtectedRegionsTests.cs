using Maquettiste.Engine.PostProcessing;

namespace Maquettiste.Engine.Tests.PostProcessing;

public sealed class ProtectedRegionsTests
{
    private const string Generated = """
        -- header
        -- maquettiste:keep id=grants
        -- default grants
        -- maquettiste:end-keep
        CREATE TABLE t (id int);
        <!-- maquettiste:keep id=notes-v2 -->
        default notes
        <!-- maquettiste:end-keep -->
        tail

        """;

    [Fact]
    public void Parse_finds_regions_in_any_comment_syntax()
    {
        var parse = ProtectedRegions.Parse(Generated);

        Assert.Null(parse.Error);
        Assert.Equal(["grants", "notes-v2"], parse.Regions.Select(r => r.Id));
        Assert.Equal(1, parse.Regions[0].StartLine);
        Assert.Equal(3, parse.Regions[0].EndLine);
        Assert.Equal(Generated, string.Join('\n', parse.Lines));
    }

    [Theory]
    [InlineData("// maquettiste:keep id=a\nx\n// maquettiste:keep id=a\ny\n// maquettiste:end-keep\n", "starts inside region 'a'")]
    [InlineData("// maquettiste:keep id=a\nx\n// maquettiste:end-keep\n// maquettiste:keep id=a\ny\n// maquettiste:end-keep\n", "duplicate region id 'a'")]
    [InlineData("a\n// maquettiste:keep id=open\nbody\n", "region 'open' is not terminated")]
    [InlineData("a\n// maquettiste:end-keep\n", "without an open region")]
    [InlineData("// maquettiste:keep\nx\n// maquettiste:end-keep\n", "without a valid 'id=<id>'")]
    public void Parse_rejects_duplicate_unterminated_and_stray_markers(string text, string expected)
    {
        var parse = ProtectedRegions.Parse(text);

        Assert.NotNull(parse.Error);
        Assert.Contains(expected, parse.Error, StringComparison.Ordinal);
        Assert.Empty(parse.Regions);
    }

    [Fact]
    public void Merge_keeps_hand_written_bodies_and_takes_markers_from_new_output()
    {
        var disk = """
            -- old header
            -- maquettiste:keep id=grants
            GRANT SELECT ON t TO app;
            GRANT INSERT ON t TO app;
            -- maquettiste:end-keep
            CREATE TABLE t (id int);
            <!-- maquettiste:keep id=notes-v2 -->
            <!-- maquettiste:end-keep -->
            tail

            """;
        var merged = ProtectedRegions.Merge(ProtectedRegions.Parse(Generated), ProtectedRegions.Parse(disk), out var lost);

        Assert.Empty(lost);
        Assert.Equal("""
            -- header
            -- maquettiste:keep id=grants
            GRANT SELECT ON t TO app;
            GRANT INSERT ON t TO app;
            -- maquettiste:end-keep
            CREATE TABLE t (id int);
            <!-- maquettiste:keep id=notes-v2 -->
            <!-- maquettiste:end-keep -->
            tail

            """, merged);
    }

    [Fact]
    public void Merge_keeps_generated_body_for_new_regions_and_reports_lost_ones()
    {
        var disk = "// maquettiste:keep id=gone\nmine\n// maquettiste:end-keep\n// maquettiste:keep id=b-old\nx\n// maquettiste:end-keep\n";
        var merged = ProtectedRegions.Merge(ProtectedRegions.Parse(Generated), ProtectedRegions.Parse(disk), out var lost);

        Assert.Equal(["b-old", "gone"], lost);
        Assert.Equal(Generated, merged);
    }

    [Fact]
    public void Skeleton_empties_bodies_so_region_edits_do_not_change_the_hash()
    {
        var edited = Generated.Replace("-- default grants", "GRANT ALL ON t TO admin;\nGRANT SELECT ON t TO app;", StringComparison.Ordinal);

        var skeleton = ProtectedRegions.Skeleton(ProtectedRegions.Parse(Generated));

        Assert.Equal("-- header\n-- maquettiste:keep id=grants\n-- maquettiste:end-keep\nCREATE TABLE t (id int);\n" +
            "<!-- maquettiste:keep id=notes-v2 -->\n<!-- maquettiste:end-keep -->\ntail\n", skeleton);
        Assert.Equal(ProtectedRegions.SkeletonHash(Generated), ProtectedRegions.SkeletonHash(edited));
        Assert.StartsWith("r:", ProtectedRegions.SkeletonHash(Generated), StringComparison.Ordinal);
        Assert.NotEqual(ProtectedRegions.SkeletonHash(Generated), ProtectedRegions.SkeletonHash(Generated.Replace("tail", "TAIL", StringComparison.Ordinal)));
        Assert.Null(ProtectedRegions.SkeletonHash("// maquettiste:keep id=x\n"));
    }

    [Fact]
    public void Text_without_regions_round_trips()
    {
        const string text = "a\nb\n";
        var parse = ProtectedRegions.Parse(text);

        Assert.Equal(text, ProtectedRegions.Skeleton(parse));
        Assert.Equal(text, ProtectedRegions.Merge(parse, ProtectedRegions.Parse("other\n"), out var lost));
        Assert.Empty(lost);
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\nb\n")]
    [InlineData("a\rb\r", "a\nb\n")]
    [InlineData("\uFEFFa\n", "a\n")]
    [InlineData("a\r\n\rb", "a\n\nb")]
    public void Normalizer_writes_lf_without_bom(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
        Assert.True(TextNormalizer.TryDecode(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(input)).ToArray(), out var decoded));
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Normalizer_rejects_invalid_utf8()
    {
        Assert.False(TextNormalizer.TryDecode([0x61, 0xFF, 0x62], out _));
    }
}
