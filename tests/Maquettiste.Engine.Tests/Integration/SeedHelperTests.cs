using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;
using static Maquettiste.Engine.Tests.Integration.ReferenceDataRepo;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The <c>seed_named</c> and <c>seed_row</c> helpers (next-phase-1-design.md section 5.3) over the reference-data model with a small
/// <c>names</c> pack of one model unit: rows found by code, by a table seed's key and by an entity's key; a name two seeds share; the
/// <c>n:seed:</c> key that renders the unit again when a seed is renamed into the name; and the package form.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class SeedHelperTests
{
    private const string Names = "names";
    private const string Output = "gen/names/names.txt";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The reference-data repo with the <c>names</c> pack, whose model unit renders the template.</summary>
    private static ReferenceDataRepo Create(string template)
    {
        var repo = ReferenceDataRepo.Create(s => s["packs"]!["names"] = new JsonObject { ["output"] = "gen/names" });
        var folder = Path.Combine(repo.Repo.ModelRoot, "templates", Names);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), """
            {"name": "names", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "names", "template": "names.scriban", "for": "model", "output": "names.txt"}]}
            """);
        File.WriteAllText(Path.Combine(folder, "names.scriban"), template);
        return repo;
    }

    private static Task<GenerationResult> RunAsync(ReferenceDataRepo repo) =>
        repo.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2 }, null, Ct);

    private static PlanUnit NamesUnit(GenerationPlan plan) => plan.Units.Single(u => u.Key == Names + "/names");

    [Fact]
    public async Task Seed_row_finds_rows_by_code_by_table_key_and_by_entity_key()
    {
        await using var repo = Create("""
            type {{ (seed_row "UnitOfMeasure" "kg").values.label }}
            entity {{ (seed_row (seed_named "Recipe") "soup").values.name }}
            table {{ (seed_row "settings" "grace_days").values.value }} {{ (seed_row "settings" ["late_fee"]).values.id }}
            composite {{ (seed_row "rates" ["EUR", 2027]).values.rate }}
            missing {{ (seed_row "UnitOfMeasure" "lb") == null }} {{ (seed_row "rates" ["EUR", 2025]) == null }} {{ (seed_row "Nothing" "x") == null }}

            """);
        // A table keyed by a unique constraint the seed names, and one keyed by a composite primary key.
        await repo.CreateAsync($$"""
            {"kind":"table","id":"01K7SHT0000000000000000001","name":"settings","database":"{{MainDatabaseId}}","columns":[
              {"id":"01K7SHT0000000000000000002","name":"id","type":"int32","nullable":false},
              {"id":"01K7SHT0000000000000000003","name":"key","type":"string","length":40,"nullable":false},
              {"id":"01K7SHT0000000000000000004","name":"value","type":"string","length":40}],
             "primaryKey":{"columns":["01K7SHT0000000000000000002"]},
             "uniques":[{"id":"01K7SHT0000000000000000005","name":"uq_settings_key","columns":["01K7SHT0000000000000000003"]}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"01K7SHT0000000000000000010","name":"settings","target":"01K7SHT0000000000000000001","key":"01K7SHT0000000000000000005",
             "columns":["01K7SHT0000000000000000002","01K7SHT0000000000000000003","01K7SHT0000000000000000004"],
             "rows":[{"id":"01K7SHT0000000000000000011","values":[1,"late_fee","2.50"]},{"id":"01K7SHT0000000000000000012","values":[2,"grace_days","3"]}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"table","id":"01K7SHT0000000000000000020","name":"rates","database":"{{MainDatabaseId}}","columns":[
              {"id":"01K7SHT0000000000000000021","name":"currency","type":"string","length":3,"nullable":false},
              {"id":"01K7SHT0000000000000000022","name":"year","type":"int32","nullable":false},
              {"id":"01K7SHT0000000000000000023","name":"rate","type":"string","length":10}],
             "primaryKey":{"columns":["01K7SHT0000000000000000021","01K7SHT0000000000000000022"]} }
            """);
        await repo.CreateAsync("""
            {"kind":"seed","id":"01K7SHT0000000000000000030","name":"rates","target":"01K7SHT0000000000000000020",
             "columns":["01K7SHT0000000000000000021","01K7SHT0000000000000000022","01K7SHT0000000000000000023"],
             "rows":[{"id":"01K7SHT0000000000000000031","values":["EUR",2026,"1.10"]},{"id":"01K7SHT0000000000000000032","values":["EUR",2027,"1.12"]}]}
            """);

        await repo.ApplyAsync();

        Assert.Equal("type Kilogram\nentity Soup\ntable 3 1\ncomposite 1.12\nmissing true true true\n", repo.Repo.ReadFile(Output));

        // A relation's seed has no row key, and a composite key wants every part.
        File.WriteAllText(Path.Combine(repo.Repo.ModelRoot, "templates", Names, "names.scriban"), "{{ seed_row \"contains\" \"x\" }}\n");
        var relation = await RunAsync(repo);
        Assert.Contains(relation.Diagnostics, d => d.Rule == "MQ6006" && d.Message.Contains("relation seed 'contains' have no key", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(repo.Repo.ModelRoot, "templates", Names, "names.scriban"), "{{ seed_row \"rates\" \"EUR\" }}\n");
        var partial = await RunAsync(repo);
        Assert.Contains(partial.Diagnostics, d => d.Rule == "MQ6006" && d.Message.Contains("by 2 value(s) (currency, year), not 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seed_named_fails_the_unit_when_two_seeds_share_the_name()
    {
        await using var repo = Create("{{ (seed_named \"UnitOfMeasure\").name }}\n");
        await repo.ApplyAsync();
        Assert.Equal("UnitOfMeasure\n", repo.Repo.ReadFile(Output));

        const string Second = "01K7SHT0000000000000000040";
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"{{Second}}","name":"UnitOfMeasure","target":"{{UnitOfMeasure}}",
             "columns":["code","label","01JBNPZX45HY43KWJRP1XPA7Z3","01JBDJ8FSSZ5AWSH8VHTPRE95B"],
             "rows":[{"id":"01K7SHT0000000000000000041","values":["lb","Pound",453.59237,"lb"]}]}
            """);
        var result = await RunAsync(repo);

        E2ERepo.AssertOutcome(RunOutcome.Invalid, result);
        // The fixture pack's seed unit writes one file per seed name, so it reports the second seed too (MQ6005).
        var error = Assert.Single(result.Diagnostics, d => d.Rule == "MQ6006");
        Assert.Equal((DiagnosticSeverity.Error, ".maquettiste/templates/names/names.scriban"), (error.Severity, error.FilePath));
        Assert.Contains("names 2 seeds: UnitOfMeasure (" + UnitSeed + "), UnitOfMeasure (" + Second + ")", error.Message, StringComparison.Ordinal);
        Assert.Equal("UnitOfMeasure\n", repo.Repo.ReadFile(Output)); // a failed unit keeps its files
    }

    [Fact]
    public async Task A_seed_renamed_into_the_name_renders_the_unit_again()
    {
        await using var repo = Create("{{ s = seed_named \"Units\" }}{{ if s }}{{ s.rows.size }} rows{{ else }}none{{ end }}\n");
        await repo.ApplyAsync();
        Assert.Equal("none\n", repo.Repo.ReadFile(Output));
        Assert.True(NamesUnit(await repo.PlanAsync()).Skipped);

        // The unit read no seed: only the n:seed:Units key tells it that a seed now has the name.
        await repo.EditAsync(UnitSeed, n => n["name"] = "Units");
        var unit = NamesUnit(await repo.PlanAsync());
        Assert.False(unit.Skipped);
        var cause = Assert.Single(unit.Causes, c => c.Key.StartsWith("n:", StringComparison.Ordinal));
        Assert.Equal(("inputs", "n:seed:Units", "The seeds named Units changed"), (cause.Kind, cause.Key, cause.Detail));
        Assert.DoesNotContain(unit.Causes, c => c.Detail.EndsWith(" changed", StringComparison.Ordinal) && c != cause); // the rest is read for the first time
        await repo.ApplyAsync();
        Assert.Equal("3 rows\n", repo.Repo.ReadFile(Output));

        // Renamed out of the name: the seed's own key and the name's both change.
        await repo.EditAsync(UnitSeed, n => n["name"] = "UnitOfMeasure");
        Assert.False(NamesUnit(await repo.PlanAsync()).Skipped);
        await repo.ApplyAsync();
        Assert.Equal("none\n", repo.Repo.ReadFile(Output));
        var forced = await repo.ApplyAsync(force: true);
        Assert.All(forced.Changes, c => Assert.Equal(FileChangeKind.Kept, c.Kind));
    }

    [Fact]
    public async Task Seed_named_takes_the_package_form_of_a_seed_with_a_package()
    {
        await using var repo = Create("""
            {{ s = seed_named "Billing.Catalog.UnitOfMeasure" }}{{ if s }}{{ s.qualified_name }}{{ else }}none{{ end }}
            {{ (seed_named "Catalog.UnitOfMeasure").id }}
            {{ (seed_named "UnitOfMeasure").id }}
            {{ (seed_named "Allergen").qualified_name }}
            {{ (seed_named "Ledger.UnitOfMeasure") == null }}

            """);
        const string Billing = "01K7SHT0000000000000000050", Catalog = "01K7SHT0000000000000000051";
        await repo.CreateAsync($$"""{"kind":"package","id":"{{Billing}}","name":"Billing"}""");
        await repo.CreateAsync($$"""{"kind":"package","id":"{{Catalog}}","name":"Catalog","parent":"{{Billing}}"}""");
        await repo.EditAsync(UnitSeed, n => n["package"] = Catalog);
        await repo.ApplyAsync();
        Assert.Equal($"Billing.Catalog.UnitOfMeasure\n{UnitSeed}\n{UnitSeed}\nAllergen\ntrue\n", repo.Repo.ReadFile(Output));

        // Renaming a package of the chain renames the form: the unit renders again and the old form finds nothing.
        await repo.EditAsync(Billing, n => n["name"] = "Ledger");
        Assert.False(NamesUnit(await repo.PlanAsync()).Skipped);
        await repo.ApplyAsync();
        Assert.Equal($"none\n{UnitSeed}\n{UnitSeed}\nAllergen\ntrue\n", repo.Repo.ReadFile(Output));
        Assert.Equal("Ledger.Catalog.UnitOfMeasure", (await repo.ResolveAsync()).Seeds.Single(x => x.Id == UnitSeed).QualifiedName);
    }

    [Fact]
    public async Task Stable_id_matches_the_resolved_value()
    {
        // stable_id (next-phase-2-design.md section 3.4) gives what the resolver puts in a stable id column of the table's rows.
        const string Customers = "01K7SHT0000000000000000060";
        await using var repo = Create("""
            {{ customers = table_named "customers" -}}
            helper {{ stable_id customers "ada@example.com" }} {{ stable_id customers ["grace@example.com"] }}
            {{ for db in model.databases }}{{ for seeded in db.seed_tables }}{{ if seeded.table.name == "customers" }}{{ for r in seeded.rows -}}
            resolved {{ r.values.email }} {{ r.values.id }}
            {{ end }}{{ end }}{{ end }}{{ end -}}

            """);
        await repo.CreateAsync($$"""
            {"kind":"table","id":"{{Customers}}","name":"customers","database":"{{MainDatabaseId}}","columns":[
              {"id":"01K7SHT0000000000000000061","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"01K7SHT0000000000000000062","name":"email","type":"string","length":254,"nullable":false}],
             "primaryKey":{"columns":["01K7SHT0000000000000000061"]},
             "uniques":[{"id":"01K7SHT0000000000000000063","name":"uq_customers_email","columns":["01K7SHT0000000000000000062"]}]}
            """);
        await repo.CreateAsync($$"""
            {"kind":"seed","id":"01K7SHT0000000000000000064","name":"customers","target":"{{Customers}}","key":"01K7SHT0000000000000000063",
             "columns":["01K7SHT0000000000000000062"],
             "rows":[{"id":"01K7SHT0000000000000000065","values":["ada@example.com"]},{"id":"01K7SHT0000000000000000066","values":["grace@example.com"]}]}
            """);

        await repo.ApplyAsync();

        var lines = repo.Repo.ReadFile(Output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string ada = Maquettiste.Engine.Model.StableIds.Of(Customers, ["ada@example.com"]), grace = Maquettiste.Engine.Model.StableIds.Of(Customers, ["grace@example.com"]);
        Assert.Equal([$"helper {ada} {grace}", $"resolved ada@example.com {ada}", $"resolved grace@example.com {grace}"], lines);

        // A value that is not a table, and a table without a stable id column, are render errors.
        File.WriteAllText(Path.Combine(repo.Repo.ModelRoot, "templates", Names, "names.scriban"), "{{ stable_id \"customers\" \"x\" }}\n");
        var text = await RunAsync(repo);
        Assert.Contains(text.Diagnostics, d => d.Rule == "MQ6006" && d.Message.Contains("`stable_id` takes a table", StringComparison.Ordinal));
        await repo.CreateAsync($$"""
            {"kind":"table","id":"01K7SHT0000000000000000067","name":"plain","database":"{{MainDatabaseId}}","columns":[
              {"id":"01K7SHT0000000000000000068","name":"id","type":"uuid","nullable":false}],
             "primaryKey":{"columns":["01K7SHT0000000000000000068"]} }
            """);
        File.WriteAllText(Path.Combine(repo.Repo.ModelRoot, "templates", Names, "names.scriban"), "{{ stable_id (table_named \"plain\") \"x\" }}\n");
        var plain = await RunAsync(repo);
        Assert.Contains(plain.Diagnostics, d => d.Rule == "MQ6006" && d.Message.Contains("table 'plain' has no stable id column", StringComparison.Ordinal));
    }
}
