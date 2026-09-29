using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>Incremental generation (engine-design.md section 11): what re-renders, and that incremental output equals a forced run.</summary>
public sealed class IncrementalTests
{
    [Fact]
    public async Task Editing_one_entity_re_renders_only_the_units_that_read_it()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b, customerName: "text"));
        f.Renderer = new FakeRenderer();

        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        var rendered = f.Renderer.Rendered.ToList();
        Assert.Equal(2, rendered.Count);
        Assert.Contains("basic/index", rendered);
        Assert.Contains(rendered, k => k.StartsWith("basic/entity:", StringComparison.Ordinal) && Customer(f, k));
        Assert.Equal(1, result.UnitsSkipped);
        await AssertForcedRunChangesNothing(f);
    }

    [Fact]
    public async Task Creating_a_mapping_re_renders_the_entity_that_it_maps()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        Assert.Contains("table: customers", f.Outputs()["out/entities/Customer.txt"], StringComparison.Ordinal);

        // The mapping file is new: no e: key of the last render names it; the entity's r: key sees it.
        await f.WriteModelAsync(b => Models.Shop(b, extra: (m, customer, main) => m.Mapping(main, customer).Ignore()));
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Contains(f.Renderer.Rendered, k => k.StartsWith("basic/entity:", StringComparison.Ordinal) && Customer(f, k));
        Assert.Contains("table: -", f.Outputs()["out/entities/Customer.txt"], StringComparison.Ordinal);
        await AssertForcedRunChangesNothing(f);
    }

    [Fact]
    public async Task Creating_a_mapping_that_changes_nothing_still_re_renders_and_writes_nothing()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b, extra: (m, customer, main) => m.Mapping(main, customer)));
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Contains(f.Renderer.Rendered, k => k.StartsWith("basic/entity:", StringComparison.Ordinal) && Customer(f, k));
        Assert.Equal(0, result.FilesWritten);
        await AssertForcedRunChangesNothing(f);
    }

    [Fact]
    public async Task Creating_a_table_overlay_re_renders_the_entity()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b, extra: (m, customer, main) => m.Table("clients", main).OverlayFor(customer)));
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Contains(f.Renderer.Rendered, k => k.StartsWith("basic/entity:", StringComparison.Ordinal) && Customer(f, k));
        Assert.Contains("table: clients", f.Outputs()["out/entities/Customer.txt"], StringComparison.Ordinal);
        await AssertForcedRunChangesNothing(f);
    }

    [Fact]
    public async Task Creating_a_relation_re_renders_both_ends()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b =>
        {
            var customer = b.Entity("Customer").Key("id", "uuid").Attr("name", "string");
            var product = b.Entity("Product").Key("id", "uuid").Attr("title", "string");
            b.Database("main", Dialect.PostgreSql);
            b.Relation("favours", customer, product, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "fan", toNavigation: "favourites");
        });
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, f.Renderer.Rendered.Count);
        await AssertForcedRunChangesNothing(f);
    }

    [Fact]
    public async Task Editing_a_template_re_renders_its_units_only()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        f.WritePackFile("basic", "index.tpl", "{{param:greeting}} world\n{{entities}}\n");
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Equal(["basic/index"], f.Renderer.Rendered);
        Assert.Equal(1, result.FilesWritten);
        Assert.Equal("hello world\nCustomer Product\n", f.Outputs()["out/index.txt"]);
    }

    [Fact]
    public async Task Changing_a_pack_parameter_in_settings_re_renders_the_pack()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b => Models.Shop(b), s => s with
        {
            Packs = new Dictionary<string, PackSettings> { ["basic"] = new() { Parameters = new Dictionary<string, System.Text.Json.JsonElement> { ["greeting"] = System.Text.Json.JsonSerializer.SerializeToElement("salut") } } },
        });
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Equal(3, result.UnitsRendered);
        Assert.StartsWith("salut\n", f.Outputs()["out/index.txt"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deleted_output_is_rendered_again()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        File.Delete(f.Repo.PathOf("out/index.txt"));
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Equal(["basic/index"], f.Renderer.Rendered);
        Assert.Equal(1, result.FilesWritten);
        Assert.True(f.Repo.Exists("out/index.txt"));
    }

    [Fact]
    public async Task A_touched_but_identical_output_is_still_skipped()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        File.SetLastWriteTimeUtc(f.Repo.PathOf("out/index.txt"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Empty(f.Renderer.Rendered);
        Assert.Equal(3, result.UnitsSkipped);
    }

    [Fact]
    public async Task Removing_an_entity_deletes_its_orphaned_output()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        await f.WriteModelAsync(b =>
        {
            b.Entity("Customer").Key("id", "uuid").Attr("name", "string");
            b.Database("main", Dialect.PostgreSql);
        });
        File.Delete(Path.Combine(f.Repo.ModelRoot, "model", "entities", "product.json"));
        f.Renderer = new FakeRenderer();
        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Contains(result.Changes, c => c.Path == "out/entities/Product.txt" && c.Kind == FileChangeKind.Deleted);
        Assert.False(f.Repo.Exists("out/entities/Product.txt"));
        Assert.DoesNotContain("Product", f.CommittedManifest("basic"), StringComparison.Ordinal);
        Assert.Equal(1, result.FilesDeleted);
    }

    [Fact]
    public async Task Removing_a_pack_orphans_its_whole_manifest()
    {
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), "basic");
        await f.RunAsync();
        Directory.Delete(Path.Combine(f.Repo.ModelRoot, "templates", "basic"), recursive: true);
        var result = await f.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.FilesDeleted);
        Assert.Empty(f.Outputs());
        Assert.Equal("", f.CommittedManifest("basic"));
    }

    [Fact]
    public async Task Output_is_the_same_with_one_and_many_jobs()
    {
        await using var one = await GenerationFixture.CreateAsync(Many, "basic");
        await using var many = await GenerationFixture.CreateAsync(Many, "basic");
        await one.Service.RunAsync(new GenerationRequest { Jobs = 1 }, null, GenerationFixture.Ct);
        await many.Service.RunAsync(new GenerationRequest { Jobs = 8 }, null, GenerationFixture.Ct);
        Assert.Equal(one.Outputs(), many.Outputs());
        Assert.Equal(one.CommittedManifest("basic"), many.CommittedManifest("basic"));
    }

    [Fact]
    public async Task Output_manifest_and_unit_state_are_the_same_under_any_culture()
    {
        await using var invariant = await GenerationFixture.CreateAsync(Many, "basic");
        await using var turkish = await GenerationFixture.CreateAsync(Many, "basic");
        var saved = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture);
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            await invariant.RunAsync();
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("tr-TR");
            await turkish.RunAsync();
        }
        finally
        {
            (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture) = saved;
        }

        Assert.Equal(invariant.Outputs(), turkish.Outputs());
        Assert.Equal(invariant.CommittedManifest("basic"), turkish.CommittedManifest("basic"));
        // Unit states hold input hashes and read keys (culture-free); only the recorded mtimes differ between the two repos.
        static string States(GenerationFixture f) => string.Join("\n", Engine.Planning.UnitStateStore
            .Decode(File.ReadAllBytes(Path.Combine(f.Repo.CacheDirectory, "units", "basic.v3.bin")))!.Values.OrderBy(v => v.Key, StringComparer.Ordinal)
            .Select(v => v.Key + " " + v.InputHash + " " + string.Join(",", v.ReadKeys) + " " + string.Join(",", v.Outputs.Select(o => o.Path + "=" + o.ManifestHash))));
        Assert.Equal(States(invariant), States(turkish));
    }

    private static void Many(ModelBuilder b)
    {
        for (var i = 0; i < 40; i++)
            b.Entity("Thing" + i).Key("id", "uuid").Attr("label", "string");
        b.Database("main", Dialect.PostgreSql);
    }

    private static bool Customer(GenerationFixture f, string key)
    {
        var id = key[(key.IndexOf(':', StringComparison.Ordinal) + 1)..];
        return f.Store.Current?.Get<Entity>(id)?.Name == "Customer";
    }

    /// <summary>A forced run over the incremental result renders everything and finds nothing to write: incremental equals forced.</summary>
    private static async Task AssertForcedRunChangesNothing(GenerationFixture f)
    {
        var before = f.Outputs();
        var forced = await f.RunAsync(force: true);
        Assert.Equal(RunOutcome.Succeeded, forced.Outcome);
        Assert.Empty(forced.Changes);
        Assert.Equal(0, forced.FilesWritten);
        Assert.Equal(before, f.Outputs());
    }
}
