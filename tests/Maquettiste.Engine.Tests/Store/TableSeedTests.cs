using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// Table seeds (2026-10-07): a seed whose target is a table file, its rows in column terms, inline or in a CSV rows file that a save
/// writes and a load reads back; every row a database receives (<see cref="RDatabase.SeedTables"/>) in foreign key order, entity
/// seeds through their binding; and the rules MQ7107 to MQ7116.
/// </summary>
public sealed class TableSeedTests
{
    private const string Currencies = "01K6TSD0000000000000000001", CurrencyCode = "01K6TSD0000000000000000002", CurrencyName = "01K6TSD0000000000000000003";
    private const string PriceLists = "01K6TSD0000000000000000010", PriceListId = "01K6TSD0000000000000000011", PriceListCurrency = "01K6TSD0000000000000000012";
    private const string PriceListLabel = "01K6TSD0000000000000000013", PriceListKey = "01K6TSD0000000000000000014";
    private const string CurrencySeed = "01K6TSD0000000000000000020", PriceListSeed = "01K6TSD0000000000000000030";
    private const string InvoiceNoteId = "01K6BND0000000000000000010";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static async Task<SaveResult> CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return result;
    }

    private static async Task<EditorRepo> RepoAsync(bool packs = false)
    {
        var r = EditorRepo.Create(packs);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Currencies}}","name":"currencies","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{CurrencyCode}}","name":"code","type":"string","length":3,"nullable":false},
              {"id":"{{CurrencyName}}","name":"name","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{CurrencyCode}}"]} }
            """);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{PriceLists}}","name":"price_lists","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{PriceListId}}","name":"id","type":"int32","nullable":false},
              {"id":"{{PriceListCurrency}}","name":"currency","type":"string","length":3,"nullable":false},
              {"id":"{{PriceListLabel}}","name":"label","type":"string","length":20}],
             "primaryKey":{"columns":["{{PriceListId}}"]},
             "foreignKeys":[{"id":"{{PriceListKey}}","name":"fk_price_lists_currency","columns":["{{PriceListCurrency}}"],"referencesTable":"{{Currencies}}"}]}
            """);
        return r;
    }

    private static async Task<ResolvedModel> ResolvedAsync(EditorRepo r) =>
        (await r.Store.ResolvedAsync(await r.Store.GetSnapshotAsync(Ct), Ct)).Model!;

    [Fact]
    public async Task A_table_seed_keeps_its_rows_in_a_csv_file_that_a_save_writes_and_a_load_reads_back()
    {
        await using var r = await RepoAsync();
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{CurrencySeed}}","name":"currencies","target":"{{Currencies}}","columns":["{{CurrencyCode}}","{{CurrencyName}}"],
             "apply":"converge","rowsFrom":{"file":"currencies.csv"},
             "rows":[{"id":"01K6TSD0000000000000000021","values":["EUR","Euro"]},{"id":"01K6TSD0000000000000000022","values":["USD","US dollar, \"the\" buck"]},
               {"id":"01K6TSD0000000000000000023","values":["007","007"]}]}
            """);

        var document = (await r.Store.GetElementAsync(CurrencySeed, Ct))!;
        var folder = Path.GetDirectoryName(r.Repo.PathOf(document.Path))!;
        var file = File.ReadAllText(r.Repo.PathOf(document.Path));
        Assert.DoesNotContain("\"rows\"", file, StringComparison.Ordinal);
        Assert.Contains("\"rowsFrom\"", file, StringComparison.Ordinal);
        var csv = File.ReadAllText(Path.Combine(folder, "currencies.csv"));
        Assert.Equal(
            $"@id,{CurrencyCode},{CurrencyName}\n01K6TSD0000000000000000021,EUR,Euro\n01K6TSD0000000000000000022,USD,\"US dollar, \"\"the\"\" buck\"\n01K6TSD0000000000000000023,007,007\n",
            csv);

        // The document's JSON and the element carry the rows, as if the seed file held them.
        Assert.Equal(3, document.Json.GetProperty("rows").GetArrayLength());
        var seed = (Seed)document.Element;
        Assert.Equal(SeedApply.Converge, seed.Apply);
        Assert.Equal("US dollar, \"the\" buck", seed.Rows[1].Values[1].GetString());
        Assert.Equal("007", seed.Rows[2].Values[0].GetString());

        // A hand edit of the CSV (a spreadsheet) reloads into the rows; a number stays a number, quoted text stays text.
        File.WriteAllText(Path.Combine(folder, "currencies.csv"), $"@id,{CurrencyName},{CurrencyCode}\n01K6TSD0000000000000000021,Euro,EUR\n01K6TSD0000000000000000024,\"12\",CHF\n");
        await r.Store.RefreshAsync([Path.Combine(folder, "currencies.csv")], Ct);
        seed = (Seed)(await r.Store.GetElementAsync(CurrencySeed, Ct))!.Element;
        Assert.Equal(["01K6TSD0000000000000000021", "01K6TSD0000000000000000024"], seed.Rows.Select(x => x.Id));
        Assert.Equal(["CHF", "12"], seed.Rows[1].Values.Select(v => v.GetString()));

        // A save writes the CSV back in canonical form.
        var node = JsonNode.Parse(seed is null ? "{}" : (await r.Store.GetElementAsync(CurrencySeed, Ct))!.Json.GetRawText())!.AsObject();
        node["rows"]!.AsArray().Add(new JsonObject { ["id"] = "01K6TSD0000000000000000025", ["values"] = new JsonArray("GBP", "Pound") });
        var current = (await r.Store.GetElementAsync(CurrencySeed, Ct))!;
        var saved = await r.Store.SaveAsync(CurrencySeed, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(
            $"@id,{CurrencyCode},{CurrencyName}\n01K6TSD0000000000000000021,EUR,Euro\n01K6TSD0000000000000000024,CHF,\"12\"\n01K6TSD0000000000000000025,GBP,Pound\n",
            File.ReadAllText(Path.Combine(folder, "currencies.csv")));

        // A broken rows file is MQ7116.
        File.WriteAllText(Path.Combine(folder, "currencies.csv"), "code,name\nEUR,Euro\n");
        await r.Store.RefreshAsync([Path.Combine(folder, "currencies.csv")], Ct);
        var validation = await r.Store.ValidateAsync(new ValidationScope([CurrencySeed]), Ct);
        Assert.Contains(validation.Diagnostics, d => d.Rule == "MQ7116");
    }

    [Fact]
    public async Task A_database_receives_its_table_seeds_and_bound_entity_seeds_in_foreign_key_order_in_column_terms()
    {
        await using var r = await RepoAsync();
        // The referencing table's seed is created first: the order still puts the referenced table first.
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{PriceListSeed}}","name":"demoPriceLists","target":"{{PriceLists}}","columns":["{{PriceListId}}","{{PriceListCurrency}}","{{PriceListLabel}}"],
             "environments":["dev","test"],
             "rows":[{"id":"01K6TSD0000000000000000031","values":[1,"EUR","Standard"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{CurrencySeed}}","name":"currencies","target":"{{Currencies}}","columns":["{{CurrencyCode}}","{{CurrencyName}}"],"apply":"converge",
             "rows":[{"id":"01K6TSD0000000000000000021","values":["EUR","Euro"]}]}
            """);
        // An entity bound to the shared notes table: its rows reach the table through the binding, with its constant.
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"01K6TSD0000000000000000040","name":"notes","target":"{{InvoiceNoteId}}",
             "columns":["01K6BND0000000000000000011","01K6BND0000000000000000012","01K6BND0000000000000000013"],
             "rows":[{"id":"01K6TSD0000000000000000041","values":["0193a3c2-0000-7000-8000-000000000001","0193a3c2-0000-7000-8000-000000000002","Paid by card"]}]}
            """);

        var model = await ResolvedAsync(r);
        var main = model.Databases.Single(d => d.Id == EditorRepo.MainDatabaseId);
        var tables = main.SeedTables.Select(t => t.Table.Name).ToList();
        Assert.True(tables.IndexOf("currencies") < tables.IndexOf("price_lists"), string.Join(", ", tables));
        var currencies = main.SeedTables.Single(t => t.Table.Name == "currencies");
        Assert.Equal(["code"], currencies.KeyColumns.Select(c => c.Name));
        var euro = Assert.Single(currencies.Rows);
        Assert.Equal(("table", "converge", "EUR", "Euro"), (euro.Source, euro.Apply, euro.Values["code"], euro.Values["name"]));
        Assert.Equal(["EUR"], euro.Key);
        var priceList = Assert.Single(main.SeedTables.Single(t => t.Table.Name == "price_lists").Rows);
        Assert.Equal(["dev", "test"], priceList.Environments);
        Assert.Equal("once", priceList.Apply);
        Assert.Equal(1L, priceList.Values["id"]);

        var notes = main.SeedTables.Single(t => t.Table.Name == "notes");
        var note = Assert.Single(notes.Rows);
        Assert.Equal("entity", note.Source);
        Assert.Equal("invoice", note.Values["entity_type"]);
        Assert.Equal("Paid by card", note.Values["body"]);
        Assert.Equal("0193a3c2-0000-7000-8000-000000000002", note.Values["entity_id"]);
        Assert.Matches("^[0-9a-f]{64}$", main.SeedHash);
        Assert.Equal([CurrencySeed], model.Databases.SelectMany(d => d.Tables).Single(t => t.Name == "currencies").Seeds.Select(s => s.Id));
    }

    private const string Categories = "01K6TSD0000000000000000050", CategoryId = "01K6TSD0000000000000000051", CategoryParent = "01K6TSD0000000000000000052";
    private const string CategoryParentKey = "01K6TSD0000000000000000053", RootSeed = "01K6TSD0000000000000000060", ChildSeed = "01K6TSD0000000000000000070";

    /// <summary>A table whose rows name a parent row of the same table, with a seed of every environment holding the root.</summary>
    private static async Task<EditorRepo> CategoriesAsync()
    {
        var r = await RepoAsync();
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Categories}}","name":"categories","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{CategoryId}}","name":"id","type":"int32","nullable":false},
              {"id":"{{CategoryParent}}","name":"parent_id","type":"int32"}],
             "primaryKey":{"columns":["{{CategoryId}}"]},
             "foreignKeys":[{"id":"{{CategoryParentKey}}","name":"fk_categories_parent","columns":["{{CategoryParent}}"],"referencesTable":"{{Categories}}"}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{RootSeed}}","name":"root","target":"{{Categories}}","columns":["{{CategoryId}}","{{CategoryParent}}"],
             "rows":[{"id":"01K6TSD0000000000000000061","values":[1,null]}]}
            """);
        return r;
    }

    [Fact]
    public async Task Seeds_in_order_put_a_table_seed_after_the_seeds_of_the_rows_its_foreign_keys_name()
    {
        await using var r = await RepoAsync();
        // By name the price lists come first; their currency names a row of the currencies seed.
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{CurrencySeed}}","name":"currencies","target":"{{Currencies}}","columns":["{{CurrencyCode}}","{{CurrencyName}}"],
             "rows":[{"id":"01K6TSD0000000000000000021","values":["EUR","Euro"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{PriceListSeed}}","name":"aPriceLists","target":"{{PriceLists}}","columns":["{{PriceListId}}","{{PriceListCurrency}}"],
             "rows":[{"id":"01K6TSD0000000000000000031","values":[1,"EUR"]}]}
            """);

        var model = await ResolvedAsync(r);
        Assert.Equal(["aPriceLists", "currencies"], model.Seeds.Select(s => s.Name));
        Assert.Equal(["currencies", "aPriceLists"], model.SeedsInOrder.Select(s => s.Name));
    }

    [Fact]
    public async Task A_tables_rows_that_name_rows_of_the_same_table_come_after_them_seed_by_seed()
    {
        await using var r = await CategoriesAsync();
        // By name this seed comes before the root; its first row names its second.
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{ChildSeed}}","name":"children","target":"{{Categories}}","columns":["{{CategoryId}}","{{CategoryParent}}"],
             "environments":["demo"],
             "rows":[{"id":"01K6TSD0000000000000000071","values":[3,2]},{"id":"01K6TSD0000000000000000072","values":[2,1]}]}
            """);

        var model = await ResolvedAsync(r);
        Assert.Equal(["root", "children"], model.SeedsInOrder.Where(s => s.Target is RTable { Name: "categories" }).Select(s => s.Name));
        Assert.Equal(["01K6TSD0000000000000000072", "01K6TSD0000000000000000071"], model.Seeds.Single(s => s.Name == "children").OrderedRows.Select(x => x.Id));

        var categories = model.Databases.Single(d => d.Id == EditorRepo.MainDatabaseId).SeedTables.Single(t => t.Table.Name == "categories");
        Assert.Equal(["root", "children"], categories.Seeds.Select(s => s.Name));
        Assert.Equal([1L, 2L, 3L], categories.Rows.Select(x => x.Values["id"]));
    }

    [Fact]
    public async Task A_row_comes_after_the_holder_of_its_parent_key_that_loads_with_it_when_seeds_of_other_environments_share_the_key()
    {
        await using var r = await CategoriesAsync();
        // Category 2 is held by a prod seed (a top row) and by a demo seed (under the root); the demo child names it, so it follows
        // the demo holder, not the first holder by name.
        string Seed(string id, string name, string environment, string rowId, int key, string parent) => $$"""
            {"kind":"seed","id":"{{id}}","name":"{{name}}","target":"{{Categories}}","columns":["{{CategoryId}}","{{CategoryParent}}"],
             "environments":["{{environment}}"],"rows":[{"id":"{{rowId}}","values":[{{key}},{{parent}}]}]}
            """;
        await CreateAsync(r, Seed("01K6TSD0000000000000000092", "bProd", "prod", "01K6TSD0000000000000000093", 2, "null"));
        await CreateAsync(r, Seed("01K6TSD0000000000000000094", "cDemo", "demo", "01K6TSD0000000000000000095", 2, "1"));
        await CreateAsync(r, Seed("01K6TSD0000000000000000090", "aDemoChild", "demo", "01K6TSD0000000000000000091", 3, "2"));
        Assert.DoesNotContain((await r.Store.ValidateAsync(new ValidationScope(), Ct)).Diagnostics, d => d.Rule is "MQ7111" or "MQ7112");

        var model = await ResolvedAsync(r);
        var order = model.SeedsInOrder.Where(s => s.Target is RTable { Name: "categories" }).Select(s => s.Name).ToList();
        Assert.True(order.IndexOf("cDemo") < order.IndexOf("aDemoChild"), string.Join(", ", order));
        var categories = model.Databases.Single(d => d.Id == EditorRepo.MainDatabaseId).SeedTables.Single(t => t.Table.Name == "categories");
        Assert.Equal(["bProd", "root", "cDemo", "aDemoChild"], categories.Seeds.Select(s => s.Name));
    }

    [Fact]
    public async Task A_foreign_key_must_name_a_row_held_in_every_environment_its_row_is_loaded_in()
    {
        await using var r = await CategoriesAsync();
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{ChildSeed}}","name":"children","target":"{{Categories}}","columns":["{{CategoryId}}","{{CategoryParent}}"],
             "environments":["demo"],
             "rows":[{"id":"01K6TSD0000000000000000071","values":[2,1]}]}
            """);
        // Row 2 is there in demo only: a seed of every environment and a seed of demo and test cannot name it; one of demo can.
        string Seed(string id, string name, string environments, string rowId, int key) => $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{id}}","name":"{{name}}","target":"{{Categories}}",
             "columns":["{{CategoryId}}","{{CategoryParent}}"],"environments":{{environments}},
             "rows":[{"id":"{{rowId}}","values":[{{key}},2]}]}
            """;
        var seeds = new[]
        {
            ("01K6TSD0000000000000000080", Seed("01K6TSD0000000000000000080", "everywhere", "[]", "01K6TSD0000000000000000081", 4)),
            ("01K6TSD0000000000000000082", Seed("01K6TSD0000000000000000082", "demoAndTest", "[\"demo\",\"test\"]", "01K6TSD0000000000000000083", 5)),
            ("01K6TSD0000000000000000084", Seed("01K6TSD0000000000000000084", "demoOnly", "[\"demo\"]", "01K6TSD0000000000000000085", 6)),
        };
        var paths = new List<string>();
        foreach (var (id, json) in seeds)
        {
            var path = r.Repo.PathOf($".maquettiste/model/seeds/{id}/{id}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonNode.Parse(json)!.ToJsonString());
            paths.Add(path);
        }

        await r.Store.RefreshAsync(paths, Ct);
        var rules = (await r.Store.ValidateAsync(new ValidationScope([.. seeds.Select(s => s.Item1)]), Ct)).Diagnostics.Where(d => d.Rule == "MQ7111").ToList();
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, d => d.Message.StartsWith("Row 01K6TSD0000000000000000081:", StringComparison.Ordinal) && d.Message.Contains("do not hold in every environment", StringComparison.Ordinal));
        Assert.Contains(rules, d => d.Message.StartsWith("Row 01K6TSD0000000000000000083:", StringComparison.Ordinal) && d.Message.Contains("in the environment 'test'", StringComparison.Ordinal));
    }

    private const string CatalogPackageId = "01J92P0V025DRFTXKMS240G2NG";

    private static string CurrencySeedJson(string id, string name, string? package, string rowId, string code, string? key = null) =>
        $$"""
        {"kind":"seed","id":"{{id}}","name":"{{name}}",{{(package is null ? "" : $"\"package\":\"{package}\",")}}"target":"{{Currencies}}",
         {{(key is null ? "" : $"\"key\":\"{key}\",")}}"columns":["{{CurrencyCode}}","{{CurrencyName}}"],
         "rows":[{"id":"{{rowId}}","values":["{{code}}","{{code}} name"]}]}
        """;

    [Fact]
    public async Task Seeds_in_two_packages_merge_by_package_then_name()
    {
        // next-phase-1-design.md section 4.3: by package qualified name (none first), then name, then id. Catalog is Billing.Catalog.
        await using var r = await RepoAsync();
        await CreateAsync(r, CurrencySeedJson("01K6TSD0000000000000000101", "catalogRates", CatalogPackageId, "01K6TSD0000000000000000111", "GBP"));
        await CreateAsync(r, CurrencySeedJson("01K6TSD0000000000000000102", "zzBase", null, "01K6TSD0000000000000000112", "EUR"));
        await CreateAsync(r, CurrencySeedJson("01K6TSD0000000000000000103", "billingRates", EditorRepo.BillingPackageId, "01K6TSD0000000000000000113", "USD"));
        await CreateAsync(r, CurrencySeedJson("01K6TSD0000000000000000104", "billingExtra", EditorRepo.BillingPackageId, "01K6TSD0000000000000000114", "CHF"));

        var model = await ResolvedAsync(r);

        string[] expected = ["zzBase", "billingExtra", "billingRates", "catalogRates"];
        var currencies = model.Databases.Single(d => d.Id == EditorRepo.MainDatabaseId).SeedTables.Single(t => t.Table.Name == "currencies");
        Assert.Equal(expected, currencies.Seeds.Select(seed => seed.Name));
        Assert.Equal(["EUR", "CHF", "USD", "GBP"], currencies.Rows.Select(row => row.Values["code"]));
        Assert.Equal(expected, currencies.Table.Seeds.Select(seed => seed.Name));
        Assert.Equal(expected, model.Seeds.Where(seed => seed.Target == currencies.Table).Select(seed => seed.Name));
        Assert.Equal([null, "Billing", "Billing", "Billing.Catalog"], currencies.Seeds.Select(seed => seed.Package?.QualifiedName));
    }

    [Fact]
    public async Task Seeds_keying_rows_differently_are_MQ7121()
    {
        const string Settings = "01K6TSD0000000000000000120", SettingId = "01K6TSD0000000000000000121", SettingKey = "01K6TSD0000000000000000122";
        const string ByKey = "01K6TSD0000000000000000123";
        await using var r = await RepoAsync();
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Settings}}","name":"app_settings","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{SettingId}}","name":"id","type":"int32","nullable":false},
              {"id":"{{SettingKey}}","name":"key","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{SettingId}}"]},"uniques":[{"id":"{{ByKey}}","name":"uq_app_settings_key","columns":["{{SettingKey}}"]}]}
            """);
        string Seed(string id, string name, string? package, string? key, string rowId, int value) => $$"""
            {"kind":"seed","id":"{{id}}","name":"{{name}}",{{(package is null ? "" : $"\"package\":\"{package}\",")}}"target":"{{Settings}}",
             {{(key is null ? "" : $"\"key\":\"{key}\",")}}"columns":["{{SettingId}}","{{SettingKey}}"],"rows":[{"id":"{{rowId}}","values":[{{value}},"k{{value}}"]}]}
            """;
        await CreateAsync(r, Seed("01K6TSD0000000000000000130", "base", null, ByKey, "01K6TSD0000000000000000131", 1));

        // A later seed (by package) that finds its rows by the primary key is refused at its target.
        var byPrimaryKey = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(Seed("01K6TSD0000000000000000140", "aaDemo", EditorRepo.BillingPackageId, null, "01K6TSD0000000000000000141", 2))!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, byPrimaryKey.Outcome);
        var d = Assert.Single(byPrimaryKey.Diagnostics, d => d.Rule == "MQ7121");
        Assert.Equal((DiagnosticSeverity.Error, "01K6TSD0000000000000000140", "/target"), (d.Severity, d.ElementId, d.JsonPointer));
        Assert.Contains("Seed 'aaDemo' finds its rows by the primary key, but seed 'base' of table 'app_settings', which comes first, finds them by the unique constraint 'uq_app_settings_key'", d.Message, StringComparison.Ordinal);
        Assert.Contains("(seed 'base' is in no package, this seed in package 'Billing')", d.Message, StringComparison.Ordinal);

        // The same key is fine, and the table's row key is that constraint.
        await CreateAsync(r, Seed("01K6TSD0000000000000000140", "aaDemo", EditorRepo.BillingPackageId, ByKey, "01K6TSD0000000000000000141", 2));
        var table = (await ResolvedAsync(r)).Databases.Single(x => x.Id == EditorRepo.MainDatabaseId).SeedTables.Single(t => t.Table.Name == "app_settings");
        Assert.Equal(["key"], table.KeyColumns.Select(c => c.Name));

        // A new first seed (by name, in no package) that names no key makes the others the later ones: its save is refused on them,
        // at their key.
        var first = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(Seed("01K6TSD0000000000000000150", "aaa", null, null, "01K6TSD0000000000000000151", 3))!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, first.Outcome);
        Assert.Equal(
            [("01K6TSD0000000000000000130", "/key"), ("01K6TSD0000000000000000140", "/key")],
            first.Diagnostics.Where(x => x.Rule == "MQ7121").Select(x => (x.ElementId!, x.JsonPointer!)).Order());
    }

    [Fact]
    public async Task A_row_key_claimed_in_two_packages_is_MQ7112_naming_both()
    {
        await using var r = await RepoAsync();
        await CreateAsync(r, CurrencySeedJson("01K6TSD0000000000000000170", "zzBase", null, "01K6TSD0000000000000000171", "EUR"));

        // By name the new seed comes first; by package it comes later, so the finding goes on it.
        var claimed = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(CurrencySeedJson("01K6TSD0000000000000000180", "aaCatalog", CatalogPackageId, "01K6TSD0000000000000000181", "EUR"))!.ToJsonString()), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, claimed.Outcome);
        var d = Assert.Single(claimed.Diagnostics, d => d.Rule == "MQ7112");
        Assert.Equal(("01K6TSD0000000000000000181", "/rows/0"), (d.ElementId, d.JsonPointer));
        Assert.Equal(".maquettiste/model/seeds/currencies/aa-catalog.json", d.FilePath);
        Assert.Contains("repeats a row key of seed 'zzBase' of the same table (seed 'zzBase' is in no package, this seed in package 'Billing.Catalog')", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renaming_a_package_on_a_seeds_chain_reorders_its_rows_and_re_renders_the_seed_script()
    {
        // The merge order reads package qualified names, which no seed file holds: each seed records the packages of its chain, so an
        // incremental run re-renders the seed script when one of them is renamed and writes what a full run writes.
        const string Ledger = "01K6TSD0000000000000000190";
        const string SeedUnit = "sql-ddl/seed:" + EditorRepo.MainDatabaseId, SeedScript = "db/main/seed.sql";
        await using var r = await RepoAsync(packs: true);
        await CreateAsync(r, $$"""{"kind":"package","id":"{{Ledger}}","name":"Ledger"}""");
        await CreateAsync(r, CurrencySeedJson("01K6TSD0000000000000000191", "catalogRates", CatalogPackageId, "01K6TSD0000000000000000192", "GBP"));
        await CreateAsync(r, CurrencySeedJson("01K6TSD0000000000000000193", "ledgerRates", Ledger, "01K6TSD0000000000000000194", "USD"));
        async Task<GenerationResult> RunAsync(bool force = false)
        {
            var run = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"], Force = force }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, run.Outcome);
            return run;
        }

        async Task RenameAsync(string id, string name)
        {
            var document = await r.Store.GetElementAsync(id, Ct);
            var node = JsonNode.Parse(document!.Json.GetRawText())!.AsObject();
            node["name"] = name;
            var saved = await r.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
            Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        async Task AssertRerenderedAsync(string first, string second)
        {
            var plan = (await r.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct)).Plan!;
            Assert.Contains(plan.Units, u => u.Key == SeedUnit && !u.Skipped);
            await RunAsync();
            var script = r.Repo.ReadFile(SeedScript);
            Assert.True(script.IndexOf($"'{first}'", StringComparison.Ordinal) < script.IndexOf($"'{second}'", StringComparison.Ordinal), script);
            Assert.All((await RunAsync(force: true)).Changes, c => Assert.True(c.Kind == FileChangeKind.Kept, "forced run changed " + c.Kind + " " + c.Path));
        }

        // Each seed, and so the table's seed rows, records its file's package and that package's parents.
        var currencies = (await ResolvedAsync(r)).Databases.Single(d => d.Id == EditorRepo.MainDatabaseId).SeedTables.Single(t => t.Table.Name == "currencies");
        Assert.Equal(
            [["e:" + EditorRepo.BillingPackageId, "e:" + CatalogPackageId], ["e:" + Ledger]],
            currencies.Seeds.Select(seed => seed.Dependencies.Where(k => k == "e:" + CatalogPackageId || k == "e:" + EditorRepo.BillingPackageId || k == "e:" + Ledger).Order(StringComparer.Ordinal).ToArray()));
        Assert.Contains("e:" + Ledger, currencies.Dependencies);
        Assert.Contains("e:" + EditorRepo.BillingPackageId, currencies.Table.Seeds.MembershipKeys);

        await RunAsync();
        Assert.Contains((await r.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct)).Plan!.Units, u => u.Key == SeedUnit && u.Skipped);
        var initial = r.Repo.ReadFile(SeedScript);
        Assert.True(initial.IndexOf("'GBP'", StringComparison.Ordinal) < initial.IndexOf("'USD'", StringComparison.Ordinal), initial); // Billing.Catalog, Ledger

        // The seed's own package: Alpha now sorts before Billing.Catalog.
        await RenameAsync(Ledger, "Alpha");
        await AssertRerenderedAsync("USD", "GBP");

        // A parent of the seed's package: Aaa.Catalog now sorts before Alpha.
        await RenameAsync(EditorRepo.BillingPackageId, "Aaa");
        await AssertRerenderedAsync("GBP", "USD");
    }

    [Fact]
    public async Task Table_seed_rules_check_columns_cells_keys_and_foreign_keys()
    {
        await using var r = await RepoAsync();
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{CurrencySeed}}","name":"currencies","target":"{{Currencies}}","columns":["{{CurrencyCode}}","{{CurrencyName}}"],
             "rows":[{"id":"01K6TSD0000000000000000021","values":["EUR","Euro"]}]}
            """);
        // A save is refused while it adds errors: write the bad seed straight to disk and load.
        var bad = $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{PriceListSeed}}","name":"priceLists","target":"{{PriceLists}}",
             "columns":["{{PriceListId}}","{{PriceListLabel}}","{{PriceListCurrency}}","{{CurrencyName}}"],
             "rows":[{"id":"01K6TSD0000000000000000031","values":[1,"Far too long for twenty","GBP"]},
               {"id":"01K6TSD0000000000000000032","values":[1,"Again","EUR"]},
               {"id":"01K6TSD0000000000000000033","values":["two",null,"EUR"]}]}
            """;
        var path = r.Repo.PathOf(".maquettiste/model/seeds/price-lists/price-lists.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonNode.Parse(bad)!.ToJsonString());
        await r.Store.RefreshAsync([path], Ct);

        var rules = (await r.Store.ValidateAsync(new ValidationScope([PriceListSeed]), Ct)).Diagnostics.Where(d => d.ElementId is not null).ToList();
        string Messages(string rule) => string.Join(" | ", rules.Where(d => d.Rule == rule).Select(d => d.Message));
        Assert.Contains("is not a column of table 'price_lists'", Messages("MQ7107"), StringComparison.Ordinal); // the currency name column
        Assert.Contains("more than its length 20", Messages("MQ7109"), StringComparison.Ordinal);
        Assert.Contains("not an integer", Messages("MQ7109"), StringComparison.Ordinal);
        Assert.Contains("repeats the row key", Messages("MQ7112"), StringComparison.Ordinal);
        Assert.Contains("names no row of the seeds of table 'currencies'", Messages("MQ7111"), StringComparison.Ordinal);
        Assert.DoesNotContain(rules, d => d.Rule == "MQ7111" && d.Message.Contains("01K6TSD0000000000000000032", StringComparison.Ordinal));
    }
}
