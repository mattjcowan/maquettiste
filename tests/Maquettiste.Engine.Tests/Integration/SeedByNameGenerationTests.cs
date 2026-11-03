using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Seeds by name in templates, end to end (next-phase-1-design.md section 5.3): a table seed created through the store with its columns
/// by name, and a model unit that finds it with <c>seed_named</c> and emits one constant per row.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class SeedByNameGenerationTests
{
    private const string Currency = "01K7SBN0000000000000000001";
    private const string CurrencyCode = "01K7SBN0000000000000000002";
    private const string CurrencyLabel = "01K7SBN0000000000000000003";
    private const string Output = "db/constants/currencies.txt";

    [Fact]
    public async Task A_template_emits_one_constant_per_row_of_a_table_seed_found_by_name()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["packs"]!["constants"] = new JsonObject { ["output"] = "db/constants" });
        var folder = Path.Combine(repo.Repo.ModelRoot, "templates", "constants");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), """
            {"name": "constants", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "currencies", "template": "currencies.scriban", "for": "model", "output": "currencies.txt"}]}
            """);
        File.WriteAllText(Path.Combine(folder, "currencies.scriban"), """
            // currencies
            {{~ currencies = seed_named "currencies" ~}}
            {{~ if currencies; for r in currencies.rows ~}}
            const {{ r.values.code }} = "{{ r.values.label }}";
            {{~ end; end ~}}

            """);

        // A designed table billing.currency, and its seed created with the columns by name.
        await repo.CreateElementAsync($$"""
            {"kind":"table","id":"{{Currency}}","name":"currency","database":"{{E2ERepo.MainDatabaseId}}","columns":[
              {"id":"{{CurrencyCode}}","name":"code","type":"string","length":3,"nullable":false},
              {"id":"{{CurrencyLabel}}","name":"label","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{CurrencyCode}}"]} }
            """);
        var seed = await repo.CreateElementAsync($$"""
            {"kind":"seed","name":"currencies","target":"{{Currency}}","columns":["code","label"],
             "rows":[{"values":{"code":"EUR","label":"Euro"} },{"values":{"code":"USD","label":"US dollar"} },{"values":{"code":"GBP","label":"Pound sterling"} }]}
            """);
        var stored = (Seed)(await repo.Store.GetElementAsync(seed.Id!, E2ERepo.Ct))!.Element;
        Assert.Equal([CurrencyCode, CurrencyLabel], stored.Columns);

        await repo.ApplyAsync();
        Assert.Equal("// currencies\nconst EUR = \"Euro\";\nconst USD = \"US dollar\";\nconst GBP = \"Pound sterling\";\n", repo.Repo.ReadFile(Output));

        // Renamed, the seed no longer has the name: the unit renders again and emits none.
        await repo.EditAsync(seed.Id!, n => n["name"] = "moneys");
        await repo.ApplyAsync();
        Assert.Equal("// currencies\n", repo.Repo.ReadFile(Output));
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }
}
