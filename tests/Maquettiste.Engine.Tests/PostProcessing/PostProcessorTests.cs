using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.PostProcessing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.PostProcessing;

public sealed class PostProcessorTests : IDisposable
{
    private static readonly OutputRootInfo Committed = new("db", true);
    private static readonly OutputRootInfo Built = new("src/Generated", false);

    private readonly TempRepo _repo = new();
    private readonly FakeFormatterRunner _runner = new();
    private readonly FakePathPolicy _paths = new(null, Committed, Built);

    public void Dispose() => _repo.Dispose();

    private PostProcessor Processor() => new(_repo.Options, _runner);

    private PostProcessContext Context(bool verified = true, params FormatterSettings[] formatters) =>
        new(_repo.RepoRoot, _paths, formatters, verified);

    private static string Text(OutputFile file) => Encoding.UTF8.GetString(file.Content.Span);

    [Fact]
    public async Task Overwrite_file_is_normalized_hashed_and_classified()
    {
        var unit = Units.Rendered(OutputMode.Overwrite, null, Units.File("db/t.sql", "\uFEFFa\r\nb\rc"));

        var result = await Processor().ProcessAsync(unit, Context(), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Empty(result.Diagnostics);
        var file = Assert.Single(result.Files);
        Assert.Equal("a\nb\nc", Text(file));
        Assert.Equal(ContentHash.Of("a\nb\nc"), file.ContentHash);
        Assert.Equal(file.ContentHash, file.ManifestHash);
        Assert.Equal(Committed, file.Root);
        Assert.Equal(OutputMode.Overwrite, file.Mode);
        Assert.Equal(FileRole.Main, file.Role);
        Assert.False(file.ContentOmitted);
    }

    [Fact]
    public async Task Owned_files_get_the_o_prefix_and_pair_blocks_are_overwrite()
    {
        var once = Units.Rendered(OutputMode.Once, null, Units.File("db/once.sql", "x"));
        var pair = Units.Rendered(OutputMode.Pair, null,
            Units.File("src/Generated/A.g.cs", "g"),
            new RenderedFile("src/Generated/A.cs", "c", FileRole.Companion),
            new RenderedFile("src/Generated/Reg.cs", "r", FileRole.Block));

        var onceResult = await Processor().ProcessAsync(once, Context(), TestContext.Current.CancellationToken);
        var pairResult = await Processor().ProcessAsync(pair, Context(), TestContext.Current.CancellationToken);

        Assert.Equal("o:" + ContentHash.Of("x"), Assert.Single(onceResult.Files).ManifestHash);
        Assert.Equal([OutputMode.Pair, OutputMode.Pair, OutputMode.Overwrite], pairResult.Files.Select(f => f.Mode));
        Assert.Equal(ContentHash.Of("g"), pairResult.Files[0].ManifestHash);
        Assert.Equal("o:" + ContentHash.Of("c"), pairResult.Files[1].ManifestHash);
        Assert.Equal(ContentHash.Of("r"), pairResult.Files[2].ManifestHash);
        Assert.All(pairResult.Files, f => Assert.Equal(Built, f.Root));
    }

    [Fact]
    public async Task Refused_path_fails_the_unit_with_MQ6004()
    {
        var unit = Units.Rendered(OutputMode.Overwrite, null, Units.File("db/ok.sql", "a"), Units.File("elsewhere/x.sql", "b"));

        var result = await Processor().ProcessAsync(unit, Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Empty(result.Files);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MQ6004", diagnostic.Rule);
        Assert.Equal("elsewhere/x.sql", diagnostic.FilePath);
    }

    [Fact]
    public async Task Regions_on_a_built_root_fail_with_MQ6015()
    {
        var unit = Units.Rendered(OutputMode.Regions, null, Units.File("src/Generated/x.cs", "a"));

        var result = await Processor().ProcessAsync(unit, Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Equal("MQ6015", Assert.Single(result.Diagnostics).Rule);
    }

    [Fact]
    public async Task Failed_render_passes_through_without_files()
    {
        var unit = Units.Rendered("u", OutputMode.Overwrite, null, true, Units.File("db/a.sql", "a"));

        var result = await Processor().ProcessAsync(unit, Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Empty(result.Files);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Regions_round_trip_keeps_hand_written_bodies()
    {
        const string template = "-- generated v1\n-- maquettiste:keep id=grants\n-- add grants here\n-- maquettiste:end-keep\nCREATE TABLE t (id int);\n";
        var first = await Processor().ProcessAsync(Units.Rendered(OutputMode.Regions, null, Units.File("db/t.sql", template)), Context(),
            TestContext.Current.CancellationToken);
        var written = Assert.Single(first.Files);
        Assert.Equal(template, Text(written));
        _repo.WriteBytes("db/t.sql", written.Content.ToArray());

        // The team edits inside the region (and the file picks up CRLF from a checkout).
        var edited = template.Replace("-- add grants here", "GRANT SELECT ON t TO app;", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        _repo.WriteFile("db/t.sql", edited);

        // The model changes and the unit renders again.
        var template2 = template.Replace("v1", "v2", StringComparison.Ordinal).Replace("(id int)", "(id int, name text)", StringComparison.Ordinal);
        var second = await Processor().ProcessAsync(Units.Rendered(OutputMode.Regions, null, Units.File("db/t.sql", template2)), Context(),
            TestContext.Current.CancellationToken);

        Assert.False(second.Failed);
        var merged = Assert.Single(second.Files);
        Assert.Equal("-- generated v2\n-- maquettiste:keep id=grants\nGRANT SELECT ON t TO app;\n-- maquettiste:end-keep\nCREATE TABLE t (id int, name text);\n", Text(merged));
        Assert.Equal(ContentHash.Of(Text(merged)), merged.ContentHash);
        Assert.Equal(ProtectedRegions.SkeletonHash(template2), merged.ManifestHash);
        // The disk file's skeleton equals the first run's manifest hash: an edit inside a region is not a hand edit.
        Assert.Equal(written.ManifestHash, ProtectedRegions.SkeletonHash(TextNormalizer.Normalize(edited)));

        // Rendering the same template again over the merged file is stable.
        _repo.WriteBytes("db/t.sql", merged.Content.ToArray());
        var third = await Processor().ProcessAsync(Units.Rendered(OutputMode.Regions, null, Units.File("db/t.sql", template2)), Context(),
            TestContext.Current.CancellationToken);
        Assert.Equal(Text(merged), Text(Assert.Single(third.Files)));
    }

    [Fact]
    public async Task Disk_region_without_counterpart_is_MQ6010()
    {
        _repo.WriteFile("db/t.sql", "-- maquettiste:keep id=old\nmine\n-- maquettiste:end-keep\n");

        var result = await Processor().ProcessAsync(
            Units.Rendered(OutputMode.Regions, null, Units.File("db/t.sql", "-- maquettiste:keep id=new\n-- maquettiste:end-keep\n")),
            Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MQ6010", diagnostic.Rule);
        Assert.Contains("'old'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("db/t.sql", diagnostic.FilePath);
    }

    [Fact]
    public async Task Malformed_regions_on_disk_are_MQ6010_and_in_output_MQ6006()
    {
        _repo.WriteFile("db/a.sql", "-- maquettiste:keep id=x\nunterminated\n");
        var disk = await Processor().ProcessAsync(
            Units.Rendered(OutputMode.Regions, null, Units.File("db/a.sql", "-- maquettiste:keep id=x\n-- maquettiste:end-keep\n")),
            Context(), TestContext.Current.CancellationToken);
        var generated = await Processor().ProcessAsync(
            Units.Rendered(OutputMode.Regions, null, Units.File("db/b.sql", "-- maquettiste:keep id=x\n-- maquettiste:end-keep\n-- maquettiste:keep id=x\n-- maquettiste:end-keep\n")),
            Context(), TestContext.Current.CancellationToken);

        Assert.True(disk.Failed);
        Assert.Equal("MQ6010", Assert.Single(disk.Diagnostics).Rule);
        Assert.True(generated.Failed);
        var diagnostic = Assert.Single(generated.Diagnostics);
        Assert.Equal("MQ6006", diagnostic.Rule);
        Assert.Contains("duplicate region id 'x'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Formatter_by_extension_by_name_none_and_unknown()
    {
        var sql = Units.Formatter("sql", ".sql");
        var md = Units.Formatter("md", ".md", ".g.md");
        var context = Context(true, sql, md);

        var byExtension = await Processor().ProcessAsync(
            Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "select 1\r\n"), Units.File("db/b.g.md", "# b"), Units.File("db/c.txt", "c")),
            context, TestContext.Current.CancellationToken);
        Assert.Equal(["SELECT 1\n", "# B", "c"], byExtension.Files.Select(Text));
        Assert.Equal([("sql", "db/a.sql", "select 1\n"), ("md", "db/b.g.md", "# b")], _runner.Calls);

        _runner.Calls.Clear();
        var byName = await Processor().ProcessAsync(Units.Rendered(OutputMode.Overwrite, "md", Units.File("db/a.sql", "x")), context,
            TestContext.Current.CancellationToken);
        Assert.Equal("X", Text(Assert.Single(byName.Files)));
        Assert.Equal("md", Assert.Single(_runner.Calls).Formatter);

        _runner.Calls.Clear();
        var none = await Processor().ProcessAsync(Units.Rendered(OutputMode.Overwrite, "none", Units.File("db/a.sql", "x")), context,
            TestContext.Current.CancellationToken);
        Assert.Equal("x", Text(Assert.Single(none.Files)));
        Assert.Empty(_runner.Calls);

        var processor = Processor();
        var unknown = await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, "prettier", Units.File("db/a.sql", "x")), context,
            TestContext.Current.CancellationToken);
        Assert.False(unknown.Failed);
        Assert.Equal("x", Text(Assert.Single(unknown.Files)));
        var warning = Assert.Single(unknown.Diagnostics);
        Assert.Equal(("MQ6014", DiagnosticSeverity.Warning), (warning.Rule, warning.Severity));
        // It names the pack unit, not whichever element or file got there first.
        Assert.Equal(((string?)null, "templates/pack/pack.json", "/units/0/formatter"), (warning.ElementId, warning.FilePath, warning.JsonPointer));
        var again = await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, "prettier", Units.File("db/b.sql", "y")), context,
            TestContext.Current.CancellationToken);
        Assert.Empty(again.Diagnostics);
    }

    [Fact]
    public async Task Formatter_output_is_normalized_and_runs_before_the_region_merge()
    {
        _runner.Transform = (_, text) => "\uFEFF" + text.Replace("\n", "\r\n", StringComparison.Ordinal).ToUpperInvariant();
        _repo.WriteFile("db/t.sql", "-- MAQUETTISTE:KEEP-less header\n-- maquettiste:keep id=body\nhand written, lower case\n-- maquettiste:end-keep\n");

        var result = await Processor().ProcessAsync(
            Units.Rendered(OutputMode.Regions, "sql", Units.File("db/t.sql", "create\n-- maquettiste:keep id=body\ndefault\n-- maquettiste:end-keep\n")),
            Context(true, Units.Formatter("sql", ".sql")), TestContext.Current.CancellationToken);

        // The formatter upper-cases the generated text (markers included, so ids must survive it: here they would not).
        Assert.True(result.Failed);
        Assert.Equal("MQ6010", Assert.Single(result.Diagnostics).Rule);

        _runner.Transform = (_, text) => text.Replace("create", "CREATE", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        var ok = await Processor().ProcessAsync(
            Units.Rendered(OutputMode.Regions, "sql", Units.File("db/t.sql", "create\n-- maquettiste:keep id=body\ndefault\n-- maquettiste:end-keep\n")),
            Context(true, Units.Formatter("sql", ".sql")), TestContext.Current.CancellationToken);
        Assert.False(ok.Failed);
        Assert.Equal("CREATE\n-- maquettiste:keep id=body\nhand written, lower case\n-- maquettiste:end-keep\n", Text(Assert.Single(ok.Files)));
    }

    [Fact]
    public async Task Formatter_failure_fails_the_unit()
    {
        _runner.FormatError = new Diagnostic("MQ6008", DiagnosticSeverity.Error, "Formatter 'sql' exited with code 2.", null, "db/a.sql", null, null, null);

        var result = await Processor().ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "x")),
            Context(true, Units.Formatter("sql", ".sql")), TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Empty(result.Files);
        Assert.Equal("MQ6008", Assert.Single(result.Diagnostics).Rule);
    }

    [Fact]
    public async Task Unverified_formatter_is_checked_once_per_run_before_first_use()
    {
        var sql = Units.Formatter("sql", ".sql");
        var processor = Processor();
        var run = Context(false, sql);

        await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "a")), run, TestContext.Current.CancellationToken);
        await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/b.sql", "b")), run, TestContext.Current.CancellationToken);
        Assert.Equal(1, _runner.VersionChecks);

        await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/c.sql", "c")), Context(false, sql),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, _runner.VersionChecks);

        await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/d.sql", "d")), Context(true, sql),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, _runner.VersionChecks);
    }

    [Fact]
    public async Task Version_mismatch_fails_the_unit_without_formatting()
    {
        _runner.VersionError = new Diagnostic("MQ6008", DiagnosticSeverity.Error, "Formatter 'sql' version does not match.", null,
            ".maquettiste/maquettiste.json", "/formatters/0", null, null);

        var result = await Processor().ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "a")),
            Context(false, Units.Formatter("other", ".md"), Units.Formatter("sql", ".sql")), TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MQ6008", diagnostic.Rule);
        Assert.Equal("/formatters/1", diagnostic.JsonPointer);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_the_first_file()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Processor().ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "a")), Context(), cts.Token));
    }

    [Fact]
    public async Task Cancellation_is_observed_between_files()
    {
        using var cts = new CancellationTokenSource();
        _runner.Transform = (_, text) =>
        {
            cts.Cancel();
            return text;
        };
        var unit = Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "a"), Units.File("db/b.sql", "b"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Processor().ProcessAsync(unit, Context(true, Units.Formatter("sql", ".sql")), cts.Token));

        Assert.Equal(["db/a.sql"], _runner.Calls.Select(c => c.Path));
    }

    [Fact]
    public async Task Text_with_an_unpaired_surrogate_fails_only_its_unit()
    {
        var processor = Processor();
        var context = Context(true, Units.Formatter("sql", ".sql"));

        var broken = await processor.ProcessAsync(
            Units.Rendered(OutputMode.Overwrite, null, Units.File("db/a.sql", "emoji \uD83D cut"), Units.File("db/b.sql", "fine")), context,
            TestContext.Current.CancellationToken);
        var next = await processor.ProcessAsync(Units.Rendered(OutputMode.Overwrite, null, Units.File("db/c.sql", "ok \uD83D\uDE00")), context,
            TestContext.Current.CancellationToken);

        Assert.True(broken.Failed);
        Assert.Empty(broken.Files);
        var error = Assert.Single(broken.Diagnostics);
        Assert.Equal(("MQ6006", DiagnosticSeverity.Error, "db/a.sql"), (error.Rule, error.Severity, error.FilePath));
        Assert.Contains("unpaired UTF-16 surrogate", error.Message, StringComparison.Ordinal);
        Assert.False(next.Failed);
        Assert.Equal("OK \uD83D\uDE00", Text(Assert.Single(next.Files)));
        // The broken file never reached the formatter; the valid one in the same unit still did.
        Assert.Equal(["db/b.sql", "db/c.sql"], _runner.Calls.Select(c => c.Path));
    }

    [Fact]
    public void ByExtension_prefers_the_longest_extension_then_the_first_formatter()
    {
        var cs = Units.Formatter("cs", ".cs");
        var generated = Units.Formatter("gen", ".g.cs");
        var cs2 = Units.Formatter("cs2", ".cs");

        Assert.Same(generated, PostProcessor.ByExtension("src/A.g.cs", [cs, generated, cs2]));
        Assert.Same(cs, PostProcessor.ByExtension("src/A.cs", [cs, generated, cs2]));
        Assert.Null(PostProcessor.ByExtension("src/A.CS", [cs]));
    }
}
