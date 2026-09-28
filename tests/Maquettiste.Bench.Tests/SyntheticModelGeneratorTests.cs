using Maquettiste.Bench.Synthetic;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Bench.Tests;

public sealed class SyntheticModelGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_seed_gives_byte_identical_files()
    {
        using var temp = new TempFolder();
        var a = Path.Combine(temp.Path, "a");
        var b = Path.Combine(temp.Path, "b");

        await SyntheticModelGenerator.WriteAsync(a, BenchTestModels.Small(), Ct);
        await SyntheticModelGenerator.WriteAsync(b, BenchTestModels.Small(), Ct);

        var files = BenchTestModels.Files(a);
        Assert.Equal(files, BenchTestModels.Files(b));
        Assert.Contains(".maquettiste/maquettiste.json", files);
        Assert.Contains(".maquettiste/templates/fanout/pack.json", files);
        foreach (var file in files)
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(a, file), Ct), await File.ReadAllBytesAsync(Path.Combine(b, file), Ct));
    }

    [Fact]
    public void Same_seed_gives_identical_records_and_another_seed_does_not()
    {
        var first = SyntheticModel.Build(BenchTestModels.Small(3));
        var again = SyntheticModel.Build(BenchTestModels.Small(3));
        var other = SyntheticModel.Build(BenchTestModels.Small(4));

        Assert.Equal(first.Elements.Select(e => e.Id), again.Elements.Select(e => e.Id));
        Assert.Equal(RepoWriter.PathsOf(first.Elements), RepoWriter.PathsOf(again.Elements));
        Assert.NotEqual(first.Elements.Select(e => e.Id), other.Elements.Select(e => e.Id));
    }

    [Fact]
    public async Task Generated_model_is_canonical_and_valid()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await SyntheticModelGenerator.WriteAsync(repo, BenchTestModels.Small(), Ct);

        await using var store = new ModelStore(temp.Options(repo));
        var snapshot = await store.GetSnapshotAsync(Ct);
        var report = await store.ValidateAsync(new ValidationScope(), Ct);

        Assert.Empty(snapshot.LoadDiagnostics);                 // no MQ1003 (non-canonical), MQ1005 (wrong folder) or schema errors
        Assert.Equal(0, report.Errors);
        Assert.Equal(60, snapshot.All<Entity>().Count);
        Assert.Equal(150, snapshot.All<Relation>().Count);
        Assert.Equal(3, snapshot.All<Database>().Count);
        Assert.NotEmpty(snapshot.All<Mapping>());
    }

    [Fact]
    public void Default_model_has_the_design_section_17_shape()
    {
        var options = new SyntheticModelOptions();
        var model = SyntheticModel.Build(options);
        var entities = model.Elements.OfType<Entity>().ToList();
        var relations = model.Elements.OfType<Relation>().ToList();

        Assert.Equal(50, model.Elements.OfType<Package>().Count());
        Assert.Equal(5_000, entities.Count);
        Assert.Equal(20_000, relations.Count);
        Assert.Equal(500, model.Elements.OfType<EnumType>().Count());
        Assert.Equal(250, model.Elements.OfType<ValueObject>().Count());
        Assert.Equal(50, model.Elements.OfType<ScalarType>().Count());
        Assert.Equal(["main", "reporting", "edge"], model.Elements.OfType<Database>().Select(d => d.Name));
        Assert.Empty(model.Elements.OfType<Diagram>());      // no processes or other phase 3+ content

        Assert.All(entities, e => Assert.InRange(e.Attributes.Count, 8, 20));
        var inHierarchies = entities.Count(e => e.Base is not null) + entities.Count(e => entities.Any(d => d.Base == e.Id));
        Assert.InRange(inHierarchies, 400, 600);
        Assert.InRange(entities.Count(e => e.Stereotypes.Count > 0), 1_200, 1_800);
        Assert.InRange(relations.Count(r => r.Attributes.Count > 0), 1_600, 2_400);
        Assert.InRange(relations.Count(r => r.Ends.All(e => e.Max == MaxCardinality.Many) && r.Attributes.Count == 0), 2_600, 3_400);
        Assert.InRange(relations.Count(r => r.Ends.All(e => e.Max == MaxCardinality.One)), 700, 1_300);
        Assert.Equal(20, FanoutPack.EffectiveFanout(options));
        Assert.True(FanoutPack.EffectiveFanout(options) * options.Entities >= FanoutPack.TargetFiles);
        Assert.Equal(entities.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), entities.Count);
    }

    [Fact]
    public async Task Checked_in_fanout_manifest_is_the_default_one()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await SyntheticModelGenerator.WriteAsync(repo, BenchTestModels.Small() with { Fanout = FanoutPack.DefaultFanout }, Ct);

        var generated = await File.ReadAllBytesAsync(Path.Combine(repo, ".maquettiste", "templates", "fanout", "pack.json"), Ct);
        var checkedIn = await File.ReadAllBytesAsync(Path.Combine(Maquettiste.Testing.Fixtures.RepoRoot, "bench", "packs", "fanout", "pack.json"), Ct);

        Assert.Equal(checkedIn, generated);
        Assert.Equal(checkedIn, await EmbeddedPacks.ReadAsync(FanoutPack.Name, "pack.json", Ct));
    }

    [Fact]
    public async Task Editing_one_entity_rewrites_exactly_one_model_file()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        var options = BenchTestModels.Small();
        await SyntheticModelGenerator.WriteAsync(repo, options, Ct);
        var before = BenchTestModels.Files(repo).ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(repo, f)), StringComparer.Ordinal);

        var id = await SyntheticModelGenerator.EditOneEntityAsync(repo, options, Ct);

        var changed = BenchTestModels.Files(repo).Where(f => !before[f].AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(repo, f)))).ToList();
        var file = Assert.Single(changed);
        Assert.StartsWith(".maquettiste/model/entities/", file, StringComparison.Ordinal);
        Assert.Contains(id, await File.ReadAllTextAsync(Path.Combine(repo, file), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_impossible_options()
    {
        using var temp = new TempFolder();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SyntheticModelGenerator.WriteAsync(temp.Path, BenchTestModels.Small() with { Entities = 10, Relations = 1_000 }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => SyntheticModelGenerator.WriteAsync(temp.Path, BenchTestModels.Small() with { Fanout = 0 }, Ct));
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(12, 66)]
    public void Dense_models_use_every_entity_pair_at_most_once(int entities, int relations)
    {
        var options = BenchTestModels.Small() with { Packages = 1, Entities = entities, Relations = relations };

        var model = SyntheticModel.Build(options);
        var again = SyntheticModel.Build(options);

        var pairs = model.Elements.OfType<Relation>()
            .Select(r => string.CompareOrdinal(r.Ends[0].Entity, r.Ends[1].Entity) < 0 ? (r.Ends[0].Entity, r.Ends[1].Entity) : (r.Ends[1].Entity, r.Ends[0].Entity))
            .ToList();
        Assert.Equal(relations, pairs.Count);
        Assert.Equal(relations, pairs.Distinct().Count());
        Assert.All(pairs, p => Assert.NotEqual(p.Item1, p.Item2));
        Assert.Equal(model.Elements.Select(e => e.Id), again.Elements.Select(e => e.Id));
    }

    [Fact]
    public void More_relations_than_entity_pairs_are_refused() =>
        Assert.Throws<ArgumentException>(() => SyntheticModel.Build(BenchTestModels.Small() with { Entities = 3, Relations = 4 }));
}
