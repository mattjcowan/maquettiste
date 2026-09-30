using Maquettiste.Bench.Synthetic;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Bench.Tests;

public sealed class WriteModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A small model with every scale option on.</summary>
    private static SyntheticModelOptions Scaled(int seed = 7) => BenchTestModels.Small(seed) with
    {
        Packages = 13,
        DomainDepth = 3,
        DomainWidth = 2,
        Diagrams = 20,
        DiagramMinSize = 5,
        DiagramMaxSize = 25,
        Schemas = 2,
        DesignedTables = 7,
        Views = 5,
        Sequences = 3,
        Lookups = 4,
        LookupAttributes = 70,
    };

    [Fact]
    public async Task Scaled_model_is_canonical_and_valid()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        var files = await WriteModel.WriteAsync(repo, Scaled(), Ct);

        await using var store = new ModelStore(temp.Options(repo));
        var snapshot = await store.GetSnapshotAsync(Ct);
        var report = await store.ValidateAsync(new ValidationScope(), Ct);

        Assert.Empty(snapshot.LoadDiagnostics);
        Assert.Equal(0, report.Errors);
        Assert.Equal(0, report.Warnings);
        Assert.Equal(64, snapshot.All<Entity>().Count);                     // 60 + 4 lookups
        Assert.Equal(20, snapshot.All<Diagram>().Count);
        Assert.Equal(7, snapshot.All<Table>().Count);
        Assert.Equal(5, snapshot.All<View>().Count);
        Assert.Equal(3, snapshot.All<Sequence>().Count);
        Assert.True(files > 60 + 150 + 20);
    }

    [Fact]
    public void Scale_shape_has_the_asked_for_nesting_diagrams_schemas_and_lookups()
    {
        var model = SyntheticModel.Build(Scaled());
        var packages = model.Elements.OfType<Package>().ToDictionary(p => p.Id, StringComparer.Ordinal);
        int Depth(Package p) => p.Parent is null ? 1 : 1 + Depth(packages[p.Parent]);

        Assert.Equal(3, packages.Values.Max(Depth));
        Assert.Contains(packages.Values, p => p.Parent is null);
        Assert.All(packages.Values.GroupBy(p => p.Parent).Where(g => g.Key is not null), g => Assert.InRange(g.Count(), 1, 2));

        var entities = model.Elements.OfType<Entity>().ToDictionary(e => e.Id, StringComparer.Ordinal);
        var relations = model.Elements.OfType<Relation>().ToDictionary(r => r.Id, StringComparer.Ordinal);
        var diagrams = model.Elements.OfType<Diagram>().ToList();
        Assert.All(diagrams, d =>
        {
            var onDiagram = d.Members.Where(m => entities.ContainsKey(m.Element)).Select(m => m.Element).ToHashSet(StringComparer.Ordinal);
            Assert.InRange(onDiagram.Count, 5, 25);
            Assert.NotNull(d.Package);
            Assert.All(d.Members.Where(m => !onDiagram.Contains(m.Element)), m =>
                Assert.All(relations[m.Element].Ends, end => Assert.Contains(end.Entity, onDiagram)));
        });
        Assert.Contains(diagrams, d => d.Members.Any(m => relations.ContainsKey(m.Element)));
        Assert.Equal(packages.Count, diagrams.Select(d => d.Package).Distinct(StringComparer.Ordinal).Count());

        var databases = model.Elements.OfType<Database>().ToList();
        Assert.All(databases.Where(d => d.Dialect != Dialect.Sqlite), d => Assert.Equal(2, d.Schemas.Count));
        Assert.Empty(databases.Single(d => d.Dialect == Dialect.Sqlite).Schemas);
        Assert.Contains(model.Elements.OfType<Table>(), t => t.ForeignKeys.Count > 0);

        var lookups = entities.Values.Where(e => e.Tags.Contains("lookup")).ToList();
        Assert.Equal(4, lookups.Count);
        Assert.All(lookups, l => Assert.Equal(70, l.Attributes.Count));
        Assert.All(lookups, l => Assert.Equal(l.Attributes.Count, l.Attributes.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count()));
        Assert.Equal(entities.Count, entities.Values.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Scale_shape_leaves_the_benchmark_entities_and_relations_alone()
    {
        var plain = SyntheticModel.Build(BenchTestModels.Small());
        var scaled = SyntheticModel.Build(Scaled() with { Packages = 5 });

        static string Json(IEnumerable<Element> elements) =>
            string.Join('\n', elements.Select(e => System.Text.Json.JsonSerializer.Serialize(e, e.GetType())));
        Assert.True(Json(plain.Elements.OfType<Entity>()) == Json(scaled.Elements.OfType<Entity>().Where(e => !e.Tags.Contains("lookup"))));
        Assert.True(Json(plain.Elements.OfType<Relation>()) == Json(scaled.Elements.OfType<Relation>()));
        Assert.Empty(plain.Elements.OfType<Diagram>());
        Assert.All(plain.Elements.OfType<Package>(), p => Assert.Null(p.Parent));
        Assert.All(plain.Elements.OfType<Database>(), d => Assert.Empty(d.Schemas));
    }

    [Fact]
    public async Task Domain_vocabularies_are_valid_and_leave_the_model_byte_identical_when_off()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await WriteModel.WriteAsync(repo, Scaled() with { DomainVocabularies = true }, Ct);

        await using var store = new ModelStore(temp.Options(repo));
        var snapshot = await store.GetSnapshotAsync(Ct);
        var report = await store.ValidateAsync(new ValidationScope(), Ct);

        Assert.Empty(snapshot.LoadDiagnostics);
        Assert.Empty(report.Diagnostics);
        Assert.Equal(14, snapshot.TagVocabularies.Count());                 // global + 13 domains
        Assert.Equal(14, snapshot.CategoryTrees.Count());
        Assert.Contains(snapshot.All<Entity>(), e => e.Category is not null && e.Tags.Any(t => t != "lookup"));

        // Off, nothing is drawn: the same files as a model written without the option.
        var plain = SyntheticModel.Build(Scaled());
        var on = SyntheticModel.Build(Scaled() with { DomainVocabularies = true });
        static string Json(IEnumerable<Element> elements) =>
            string.Join('\n', elements.Select(e => System.Text.Json.JsonSerializer.Serialize(e, e.GetType())));
        Assert.True(Json(plain.Elements.Where(e => e is not Entity)) == Json(on.Elements.Where(e => e is not (Entity or TagVocabulary or CategoryTree))));
        Assert.True(Json(plain.Elements) == Json(SyntheticModel.Build(Scaled() with { DomainVocabularies = false }).Elements));
    }

    [Fact]
    public async Task Same_options_give_byte_identical_files()
    {
        using var temp = new TempFolder();
        var a = Path.Combine(temp.Path, "a");
        var b = Path.Combine(temp.Path, "b");
        await WriteModel.WriteAsync(a, Scaled(), Ct);
        await WriteModel.WriteAsync(b, Scaled(), Ct);

        var files = BenchTestModels.Files(a);
        Assert.Equal(files, BenchTestModels.Files(b));
        foreach (var file in files)
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(a, file), Ct), await File.ReadAllBytesAsync(Path.Combine(b, file), Ct));
    }

    [Fact]
    public async Task A_second_run_replaces_its_own_model_and_refuses_a_foreign_one()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await WriteModel.WriteAsync(repo, Scaled(), Ct);
        await WriteModel.WriteAsync(repo, Scaled() with { Diagrams = 2 }, Ct);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(repo, ".maquettiste", "model", "diagrams")).Length);

        var foreign = Path.Combine(temp.Path, "foreign");
        Directory.CreateDirectory(Path.Combine(foreign, ".maquettiste"));
        await File.WriteAllTextAsync(Path.Combine(foreign, ".maquettiste", "maquettiste.json"), "{}", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WriteModel.WriteAsync(foreign, Scaled(), Ct));
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(foreign, ".maquettiste", "maquettiste.json"), Ct));
    }

    [Fact]
    public void Parses_the_explorer_defaults_and_overrides()
    {
        Assert.True(WriteModel.TryParse(["--out", "x"], out var dir, out var options, out _));
        Assert.Equal("x", dir);
        Assert.Equal(WriteModel.ExplorerDefaults, options);
        Assert.Equal(5_000, options.Entities);
        Assert.Equal(150, options.Diagrams);

        Assert.True(WriteModel.TryParse(
            ["--out", "y", "--seed", "3", "--entities", "300", "--relations", "600", "--domains", "10", "--domain-depth", "2", "--domain-width", "5",
             "--diagrams-per-domain", "2", "--diagram-size", "4..9", "--schemas", "1", "--designed-tables", "2", "--views", "1",
             "--sequences", "0", "--lookups", "3", "--lookup-attributes", "12", "--no-example-packs", "--domain-vocabularies", "--locales", "2"],
            out _, out options, out var error), error);
        Assert.Equal((3, 300, 600, 10, 2, 5), (options.Seed, options.Entities, options.Relations, options.Packages, options.DomainDepth, options.DomainWidth));
        Assert.Equal((20, 4, 9, 1, 2, 1, 0, 3, 12), (options.Diagrams, options.DiagramMinSize, options.DiagramMaxSize, options.Schemas,
            options.DesignedTables, options.Views, options.Sequences, options.Lookups, options.LookupAttributes));
        Assert.False(options.IncludeExamplePacks);
        Assert.True(options.DomainVocabularies);
        Assert.False(WriteModel.ExplorerDefaults.DomainVocabularies);
        Assert.Equal(2, options.Locales);
        Assert.Equal(0, WriteModel.ExplorerDefaults.Locales);
        Assert.False(WriteModel.TryParse(["--out", "y", "--locales", "9"], out _, out _, out error));
    }

    [Theory]
    [InlineData(new string[0], "--out is required")]
    [InlineData(new[] { "--out", "x", "--diagram-size", "9..4" }, "--diagram-size")]
    [InlineData(new[] { "--out", "x", "--domain-depth", "0" }, "--domain-depth")]
    [InlineData(new[] { "--out", "x", "--lookup-attributes", "4" }, "--lookup-attributes")]
    [InlineData(new[] { "--out", "x", "--entities", "10", "--relations", "100" }, "too many")]
    [InlineData(new[] { "--out", "x", "--frobnicate" }, "unknown option")]
    public void Refuses_bad_arguments(string[] args, string message)
    {
        Assert.False(WriteModel.TryParse(args, out _, out _, out var error));
        Assert.Contains(message, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_writes_and_reports_through_the_program()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await WriteModel.RunAsync(["--out", repo, "--entities", "40", "--relations", "60", "--domains", "6", "--diagrams", "3",
            "--diagram-size", "5..10", "--lookups", "2", "--lookup-attributes", "8", "--designed-tables", "2", "--views", "1", "--sequences", "1"],
            output, error, Ct);

        Assert.Equal(0, code);
        Assert.StartsWith("wrote ", output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(repo, ".maquettiste", ".cache", WriteModel.MarkerFile)));
        Assert.Equal(2, await WriteModel.RunAsync(["--views"], output, error, Ct));
    }

    [Fact]
    public async Task Each_database_resolved_on_its_own_gives_the_tables_of_the_whole_model_resolve()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await WriteModel.WriteAsync(repo, Scaled() with { DomainVocabularies = true }, Ct);
        var options = temp.Options(repo);
        await using var store = new ModelStore(options);
        var generation = new GenerationService(store, options);
        var tables = new DatabaseTables(generation);
        var snapshot = await store.GetSnapshotAsync(Ct);
        static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

        foreach (var database in snapshot.All<Database>())
        {
            var view = (await generation.GetDatabaseViewAsync(database.Id, Ct)).View!;
            var result = await tables.GetAsync(database.Id, Ct);

            Assert.False(result.Partial);
            Assert.Equal(view.Tables.Count, result.Tables.Count);
            Assert.True(view.Tables.Count > 0);
            Assert.Equal(
                Json(view.Tables.Select(t => new TableSummary(t.Key, t.Name, t.Schema, t.Origin, t.EntityId, t.RelationId, t.IsJunction, t.IsLookup, t.Columns.Count))),
                Json(result.Tables));
            foreach (var table in view.Tables)
                Assert.Equal(Json(table), Json((await tables.GetTableAsync(database.Id, table.Key, Ct)).Table));
        }
    }

    [Fact]
    public async Task Time_tables_times_every_database_cold_and_after_an_edit()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await WriteModel.WriteAsync(repo, Scaled(), Ct);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, await TimeTables.RunAsync(["--model", repo, "--rounds", "2", "--jobs", "2"], output, error, Ct));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.StartsWith("round 1 cold: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("round 2 after an edit: ", StringComparison.Ordinal) && l.Contains("partial False", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("parts 2: validate ", StringComparison.Ordinal));
        Assert.Equal(4, await TimeTables.RunAsync(["--rounds"], output, error, Ct));
    }

    [Fact]
    public async Task Locales_write_complete_shards_and_leave_the_model_byte_identical_when_off()
    {
        using var temp = new TempFolder();
        var on = Path.Combine(temp.Path, "on");
        var again = Path.Combine(temp.Path, "again");
        var off = Path.Combine(temp.Path, "off");
        var plain = Path.Combine(temp.Path, "plain");
        await WriteModel.WriteAsync(on, Scaled() with { Locales = 2 }, Ct);
        await WriteModel.WriteAsync(again, Scaled() with { Locales = 2 }, Ct);
        await WriteModel.WriteAsync(off, Scaled() with { Locales = 0 }, Ct);
        await WriteModel.WriteAsync(plain, Scaled(), Ct);

        await using var store = new ModelStore(temp.Options(on));
        var snapshot = await store.GetSnapshotAsync(Ct);
        var report = await store.ValidateAsync(new ValidationScope(), Ct);
        Assert.Empty(snapshot.LoadDiagnostics);
        Assert.Empty(report.Diagnostics);
        Assert.Equal(["en", "de", "fr"], snapshot.Localization.Locales);                 // default first, then ordinal
        var completeness = snapshot.Localization.Completeness();
        Assert.Equal(["de", "fr"], completeness.Select(c => c.Locale).Distinct().Order(StringComparer.Ordinal));
        Assert.All(completeness, c => Assert.Equal((c.Expected, 0, 0), (c.Translated, c.Missing, c.Stale)));
        Assert.True(completeness.Sum(c => c.Expected) > 1_000);

        // The same options give the same shards; off, the model is the one written without the option.
        static Dictionary<string, byte[]> Files(string repo)
        {
            var model = Path.Combine(repo, ".maquettiste", "model");
            return Directory.EnumerateFiles(model, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(model, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
        }

        var a = Files(on);
        var b = Files(again);
        Assert.Equal(a.Keys.Order(StringComparer.Ordinal), b.Keys.Order(StringComparer.Ordinal));
        Assert.All(a, p => Assert.True(p.Value.AsSpan().SequenceEqual(b[p.Key]), p.Key));
        Assert.Contains(a.Keys, k => k.StartsWith("locales/fr/", StringComparison.Ordinal));
        var c0 = Files(off);
        var p0 = Files(plain);
        Assert.Equal(p0.Keys.Order(StringComparer.Ordinal), c0.Keys.Order(StringComparer.Ordinal));
        Assert.All(p0, p => Assert.True(p.Value.AsSpan().SequenceEqual(c0[p.Key]), p.Key));
        Assert.True(File.ReadAllBytes(Path.Combine(plain, ".maquettiste", "maquettiste.json")).AsSpan()
            .SequenceEqual(File.ReadAllBytes(Path.Combine(off, ".maquettiste", "maquettiste.json"))));
    }

    [Fact]
    public async Task Time_load_times_the_cold_load_and_the_completeness_pass()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await WriteModel.WriteAsync(repo, Scaled() with { Locales = 1 }, Ct);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, await TimeLoad.RunAsync(["--model", repo, "--rounds", "2"], output, error, Ct));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Contains("2 locales", l, StringComparison.Ordinal));
        Assert.All(lines, l => Assert.Contains("(0 missing)", l, StringComparison.Ordinal));
        Assert.Equal(4, await TimeLoad.RunAsync(["--rounds"], output, error, Ct));
    }

    [Fact]
    public async Task A_restart_reads_parsed_shards_from_the_shard_cache_keyed_by_hash()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await WriteModel.WriteAsync(repo, Scaled() with { Locales = 1 }, Ct);
        var cache = Path.Combine(temp.Path, "cache");

        IReadOnlyList<LocaleShardDocument> first;
        await using (var store = new ModelStore(new EngineOptions { RepoRoot = repo, CacheDirectory = cache }))
            first = (await store.GetSnapshotAsync(Ct)).LocaleShards;
        Assert.NotEmpty(first);
        var cached = Directory.GetFiles(Path.Combine(cache, "shards"), "*.bin").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal);
        Assert.Equal(first.Select(s => s.Hash).Distinct().Order(StringComparer.Ordinal), cached);

        // A restart over the cache folder gives the same entries; a cache file that is not a shard is ignored (the JSON is read).
        File.WriteAllText(Path.Combine(cache, "shards", first[0].Hash + ".bin"), "not a shard");
        await using (var store = new ModelStore(new EngineOptions { RepoRoot = repo, CacheDirectory = cache }))
        {
            var again = (await store.GetSnapshotAsync(Ct)).LocaleShards.ToDictionary(s => s.Path, StringComparer.Ordinal);
            Assert.Equal(first.Count, again.Count);
            foreach (var shard in first)
            {
                var other = again[shard.Path].Shard;
                Assert.Equal(shard.Shard.Entries.Keys.Order(StringComparer.Ordinal), other.Entries.Keys.Order(StringComparer.Ordinal));
                Assert.All(shard.Shard.Entries, e => Assert.Equal(e.Value.Label, other.Entries[e.Key].Label));
                Assert.All(shard.Shard.Entries, e => Assert.Equal(e.Value.Description?.Text, other.Entries[e.Key].Description?.Text));
                Assert.All(shard.Shard.Entries, e => Assert.Equal(e.Value.Src.OrderBy(p => p.Key, StringComparer.Ordinal), other.Entries[e.Key].Src.OrderBy(p => p.Key, StringComparer.Ordinal)));
            }
        }

        // A shard that changes gets a new cache file, and the old one is pruned.
        var edited = first.First(s => s.Shard.Entries.Values.Any(e => e.DisplayName is not null));
        var path = Path.Combine(repo, edited.Path);
        var text = File.ReadAllText(path);
        var at = text.IndexOf("\"displayName\": \"", StringComparison.Ordinal) + "\"displayName\": \"".Length;
        File.WriteAllText(path, text[..at] + "x" + text[at..]);
        await using (var store = new ModelStore(new EngineOptions { RepoRoot = repo, CacheDirectory = cache }))
            Assert.NotEqual(edited.Hash, (await store.GetSnapshotAsync(Ct)).LocaleShards.Single(s => s.Path == edited.Path).Hash);
        Assert.False(File.Exists(Path.Combine(cache, "shards", edited.Hash + ".bin")));
        Assert.Equal(first.Select(s => s.Hash).Distinct().Count(), Directory.GetFiles(Path.Combine(cache, "shards"), "*.bin").Length);

        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await TimeLoad.RunAsync(["--model", repo, "--rounds", "1", "--warm-cache"], output, error, Ct));
        Assert.Contains("shard cache " + first.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " files", output.ToString(), StringComparison.Ordinal);
    }
}
