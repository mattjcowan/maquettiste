using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using static Maquettiste.Engine.Tests.Integration.ReferenceDataRepo;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Generation over reference data with the real renderer: the <c>each reference type</c> and <c>each seed</c> scopes, the <c>row</c>
/// and <c>row_uuid</c> helpers, and incremental runs driven by the keys templates record (a seed's <c>e:</c> key for its rows,
/// <c>s:referenceData</c> for storage choices).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ReferenceDataGenerationTests
{
    private static List<string> Rendered(GenerationPlan plan) =>
        [.. plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Reference_types_and_seeds_render_through_their_scopes()
    {
        await using var repo = Create();
        var result = await repo.ApplyAsync();
        var outputs = repo.Outputs().ToDictionary(p => p.Key, p => System.Text.Encoding.UTF8.GetString(p.Value), StringComparer.Ordinal);

        // 2 types + 5 seeds + 2 entities + 1 model unit.
        Assert.Equal(10, result.UnitsRendered);
        var unit = outputs["gen/types/unit-of-measure.txt"];
        Assert.StartsWith("UnitOfMeasure code=string(8)\n0 kg Kilogram {\"factor\":1000,\"symbol\":\"kg\"}\n", unit, StringComparison.Ordinal);
        Assert.Contains("2 pinch Pinch {\"factor\":0.36,\"symbol\":null}", unit, StringComparison.Ordinal);
        Assert.Contains("storage main check type\nstorage reporting check type", unit, StringComparison.Ordinal);
        Assert.Contains("used by defaultUnit preferredUnit packUnits unit_of_measure", unit, StringComparison.Ordinal);
        Assert.Contains("2 tree_nut Tree nuts {\"parent\":\"nut\"} parent=nut", outputs["gen/types/allergen.txt"], StringComparison.Ordinal);
        Assert.Contains("storage main lookup-table project\nstorage reporting check database", outputs["gen/types/allergen.txt"], StringComparison.Ordinal);

        var contains = outputs["gen/seeds/contains.txt"];
        Assert.StartsWith("contains -> contains\n", contains, StringComparison.Ordinal);
        Assert.Contains("\"quantity\":0.01", contains, StringComparison.Ordinal);
        Assert.Contains("allergens: reference Allergen[]", outputs["gen/entities/ingredient.txt"], StringComparison.Ordinal);

        // row_uuid writes the ULID's 128 bits big-endian as a UUID.
        var order = outputs["gen/order.txt"];
        Assert.StartsWith("Allergen Recipe UnitOfMeasure Ingredient contains \nKilogram ", order, StringComparison.Ordinal);
        var uuid = order.Split('\n')[1].Split(' ')[1];
        Assert.Equal(Convert.ToHexStringLower(Ulid.Parse(KgRow).ToByteArray()), uuid.Replace("-", "", StringComparison.Ordinal));
        Assert.Equal([8, 4, 4, 4, 12], uuid.Split('-').Select(p => p.Length));
    }

    [Fact]
    public async Task Editing_one_seed_row_re_renders_only_the_units_that_read_that_seed()
    {
        await using var repo = Create();
        await repo.ApplyAsync();
        Assert.Empty(Rendered(await repo.PlanAsync()));

        await repo.EditAsync(UnitSeed, n => n["rows"]![0]!["values"]![1] = "Kilogramme");

        var rendered = Rendered(await repo.PlanAsync());
        Assert.Equal(
            [Pack + "/order", Pack + "/seed:" + UnitSeed, Pack + "/type:" + UnitOfMeasure],
            rendered);
        var result = await repo.ApplyAsync();
        Assert.Equal(3, result.UnitsRendered);
        Assert.Contains("0 kg Kilogramme", repo.Repo.ReadFile("gen/types/unit-of-measure.txt"), StringComparison.Ordinal);
        await AssertIncrementalEqualsForcedAsync(repo);

        // A row of another seed: only that seed's unit and its type's unit (the model unit reads UnitOfMeasure rows only).
        await repo.EditAsync(AllergenSeed, n => n["rows"]![0]!["values"]![1] = "Gluten (wheat)");
        Assert.Equal([Pack + "/seed:" + AllergenSeed, Pack + "/type:" + Allergen], Rendered(await repo.PlanAsync()).Where(k => !k.EndsWith("/order", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Renaming_a_package_that_orders_a_types_seeds_re_renders_the_type()
    {
        // Seeds merge by package qualified name (next-phase-1-design.md section 4.3), which no seed file holds: each seed records the
        // packages of its chain, so renaming one of them re-renders what read the rows, here a type unit that reads no package.
        const string Billing = "01JRDP00000000000000000001", Catalog = "01JRDP00000000000000000002", Ledger = "01JRDP00000000000000000003";
        const string PoundSeed = "01JRDS00000000000000000004", Type = "gen/types/unit-of-measure.txt";
        await using var repo = Create();
        await repo.CreateAsync($$"""{"kind":"package","id":"{{Billing}}","name":"Billing"}""");
        await repo.CreateAsync($$"""{"kind":"package","id":"{{Catalog}}","name":"Catalog","parent":"{{Billing}}"}""");
        await repo.CreateAsync($$"""{"kind":"package","id":"{{Ledger}}","name":"Ledger"}""");
        await repo.EditAsync(UnitSeed, n => n["package"] = Catalog);
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"{{PoundSeed}}","name":"Pounds","package":"{{Ledger}}","target":"{{UnitOfMeasure}}",
             "columns":["code","label","01JBNPZX45HY43KWJRP1XPA7Z3","01JBDJ8FSSZ5AWSH8VHTPRE95B"],
             "rows":[{"id":"01JRDS00000000000000000005","values":["lb","Pound",453.59237,"lb"]}]}
            """);
        await repo.ApplyAsync();
        Assert.StartsWith("UnitOfMeasure code=string(8)\n0 kg Kilogram ", repo.Repo.ReadFile(Type), StringComparison.Ordinal); // Billing.Catalog, Ledger
        Assert.DoesNotContain(Pack + "/type:" + UnitOfMeasure, Rendered(await repo.PlanAsync()));

        // The seed's own package: Alpha now sorts before Billing.Catalog.
        await repo.EditAsync(Ledger, n => n["name"] = "Alpha");
        Assert.Contains(Pack + "/type:" + UnitOfMeasure, Rendered(await repo.PlanAsync()));
        await repo.ApplyAsync();
        Assert.StartsWith("UnitOfMeasure code=string(8)\n0 lb Pound ", repo.Repo.ReadFile(Type), StringComparison.Ordinal);
        await AssertIncrementalEqualsForcedAsync(repo);

        // A parent of the other seed's package: Aaa.Catalog now sorts before Alpha.
        await repo.EditAsync(Billing, n => n["name"] = "Aaa");
        Assert.Contains(Pack + "/type:" + UnitOfMeasure, Rendered(await repo.PlanAsync()));
        await repo.ApplyAsync();
        Assert.StartsWith("UnitOfMeasure code=string(8)\n0 kg Kilogram ", repo.Repo.ReadFile(Type), StringComparison.Ordinal);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Changing_a_strategy_declaration_re_renders_the_units_that_read_storage_choices()
    {
        await using var repo = Create();
        await repo.ApplyAsync();

        await repo.EditSettingsAsync(s => s["referenceData"]!["strategies"]!["check"]!["description"] = "CHECK over the codes");
        Assert.Equal([Pack + "/type:" + UnitOfMeasure, Pack + "/type:" + Allergen], Rendered(await repo.PlanAsync())); // ordinal keys

        // A database's choice is also a convention: units that read conventions re-render too, the storage readers among them.
        await repo.EditSettingsAsync(s => s["databases"]!["reporting"]!["referenceStorage"]!["strategy"] = "lookup-table");
        var rendered = Rendered(await repo.PlanAsync());
        Assert.Contains(Pack + "/type:" + Allergen, rendered);
        Assert.Contains(Pack + "/type:" + UnitOfMeasure, rendered);
        await repo.ApplyAsync();
        Assert.Contains("storage reporting lookup-table database", repo.Repo.ReadFile("gen/types/allergen.txt"), StringComparison.Ordinal);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Two_cold_runs_are_byte_identical()
    {
        await using var a = Create();
        await using var b = Create();
        await a.ApplyAsync();
        await b.ApplyAsync();
        FullRunTests.AssertSameBytes(a.Outputs(), b.Outputs());
        Assert.Equal(10, a.Outputs().Count);
    }

    private static async Task AssertIncrementalEqualsForcedAsync(ReferenceDataRepo repo)
    {
        var tree = repo.Outputs();
        var forced = await repo.ApplyAsync(force: true);
        Assert.All(forced.Changes, c => Assert.True(c.Kind == FileChangeKind.Kept, "forced run changed " + c.Kind + " " + c.Path));
        FullRunTests.AssertSameBytes(tree, repo.Outputs());
    }
}
