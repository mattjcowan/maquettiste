using Maquettiste.Bench.Synthetic;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
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
             "--sequences", "0", "--lookups", "3", "--lookup-attributes", "12", "--no-example-packs"],
            out _, out options, out var error), error);
        Assert.Equal((3, 300, 600, 10, 2, 5), (options.Seed, options.Entities, options.Relations, options.Packages, options.DomainDepth, options.DomainWidth));
        Assert.Equal((20, 4, 9, 1, 2, 1, 0, 3, 12), (options.Diagrams, options.DiagramMinSize, options.DiagramMaxSize, options.Schemas,
            options.DesignedTables, options.Views, options.Sequences, options.Lookups, options.LookupAttributes));
        Assert.False(options.IncludeExamplePacks);
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
}
