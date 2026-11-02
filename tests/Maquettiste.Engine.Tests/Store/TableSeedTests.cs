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
    public async Task A_seed_names_an_inherited_column_by_its_keyed_id()
    {
        // A stereotype gives the table two columns and a unique (next-phase-2-design.md section 3.1); the seed names them by keyed id.
        const string Stamped = "01K6TSD0000000000000000080", StampedOn = "01K6TSD0000000000000000081", StampedBy = "01K6TSD0000000000000000082";
        const string StampedKey = "01K6TSD0000000000000000083", Rates = "01K6TSD0000000000000000090", RateCode = "01K6TSD0000000000000000091";
        const string RateSeed = "01K6TSD0000000000000000095";
        await using var r = await RepoAsync();
        await CreateAsync(r, $$"""
            {"kind":"stereotype","id":"{{Stamped}}","key":"stamped","name":"Stamped","appliesTo":["table"],
             "columns":[{"id":"{{StampedOn}}","name":"stamped_on","type":"date","nullable":false},
               {"id":"{{StampedBy}}","name":"stamped_by","type":"string","length":10,"nullable":false}],
             "uniques":[{"id":"{{StampedKey}}","columns":["{{StampedBy}}","{{StampedOn}}"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Rates}}","name":"rates","database":"{{EditorRepo.MainDatabaseId}}","stereotypes":["stamped"],
             "columns":[{"id":"{{RateCode}}","name":"code","type":"string","length":3,"nullable":false}],
             "primaryKey":{"columns":["{{RateCode}}"]} }
            """);
        string on = Rates + "." + StampedOn, by = Rates + "." + StampedBy;
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{RateSeed}}","name":"rates","target":"{{Rates}}","columns":["{{RateCode}}","{{on}}","{{by}}"],"key":"{{Rates}}.{{StampedKey}}",
             "rows":[{"id":"01K6TSD0000000000000000096","values":["STD","2026-11-01","setup"]}]}
            """);

        var main = (await ResolvedAsync(r)).Databases.Single(d => d.Id == EditorRepo.MainDatabaseId);
        var rates = main.SeedTables.Single(t => t.Table.Name == "rates");
        Assert.Equal(["stamped_by", "stamped_on"], rates.KeyColumns.Select(c => c.Name));
        var row = Assert.Single(rates.Rows);
        Assert.Equal(("STD", "2026-11-01", "setup"), (row.Values["code"], row.Values["stamped_on"], row.Values["stamped_by"]));

        // A bare member id is told the keyed id; leaving the NOT NULL inherited column out is MQ7109; a bare unique id is MQ7114.
        var bad = $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{RateSeed}}","name":"rates","target":"{{Rates}}",
             "columns":["{{RateCode}}","{{StampedBy}}"],"key":"{{StampedKey}}",
             "rows":[{"id":"01K6TSD0000000000000000096","values":["STD","setup"]}]}
            """;
        var document = (await r.Store.GetElementAsync(RateSeed, Ct))!;
        var path = r.Repo.PathOf(document.Path);
        File.WriteAllText(path, JsonNode.Parse(bad)!.ToJsonString());
        await r.Store.RefreshAsync([path], Ct);

        var rules = (await r.Store.ValidateAsync(new ValidationScope([RateSeed]), Ct)).Diagnostics.Where(d => d.ElementId is not null).ToList();
        string Messages(string rule) => string.Join(" | ", rules.Where(d => d.Rule == rule).Select(d => d.Message));
        Assert.Contains($"It is the column 'stamped_by' the table inherits from stereotype 'stamped': write '{by}'.", Messages("MQ7107"), StringComparison.Ordinal);
        Assert.Contains("has no column for 'stamped_on', which is NOT NULL without a default", Messages("MQ7109"), StringComparison.Ordinal);
        Assert.Contains($"It is the unique '{StampedKey}' the table inherits from stereotype 'stamped': write '{Rates}.{StampedKey}'.", Messages("MQ7114"),
            StringComparison.Ordinal);
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
        Assert.Contains("is not a column or foreign key of table 'price_lists'", Messages("MQ7107"), StringComparison.Ordinal); // the currency name column
        Assert.Contains("more than its length 20", Messages("MQ7109"), StringComparison.Ordinal);
        Assert.Contains("not an integer", Messages("MQ7109"), StringComparison.Ordinal);
        Assert.Contains("repeats the row key", Messages("MQ7112"), StringComparison.Ordinal);
        Assert.Contains("names no row of the seeds of table 'currencies'", Messages("MQ7111"), StringComparison.Ordinal);
        Assert.DoesNotContain(rules, d => d.Rule == "MQ7111" && d.Message.Contains("01K6TSD0000000000000000032", StringComparison.Ordinal));
    }

    // Stable ids (next-phase-2-design.md section 3.4).
    private const string Labels = "01K6TSS0000000000000000010", LabelId = "01K6TSS0000000000000000011", LabelCode = "01K6TSS0000000000000000012";
    private const string LabelName = "01K6TSS0000000000000000013", LabelsByCode = "01K6TSS0000000000000000014", LabelSeed = "01K6TSS0000000000000000015";
    private const string LabelKind = "01K6TSS0000000000000000020", LabelKindSeed = "01K6TSS0000000000000000025";
    private const string Notes = "01K6BND0000000000000000001", NoteSeed = "01K6TSS0000000000000000030";
    private const string NoteId = "0193A3C2-0000-7000-8000-000000000001", NoteInvoice = "0193a3c2-0000-7000-8000-000000000002";

    /// <summary>A labels table whose id takes a stable id, keyed for seeds by its unique code.</summary>
    private static string LabelsJson(string idColumn = "\"stableId\":true") => $$"""
        {"kind":"table","id":"{{Labels}}","name":"labels","database":"{{EditorRepo.MainDatabaseId}}","columns":[
          {"id":"{{LabelId}}","name":"id","type":"uuid","nullable":false,{{idColumn}}},
          {"id":"{{LabelCode}}","name":"code","type":"string","length":10,"nullable":false},
          {"id":"{{LabelName}}","name":"name","type":"string","length":40}],
         "primaryKey":{"columns":["{{LabelId}}"]},"uniques":[{"id":"{{LabelsByCode}}","name":"uq_labels_code","columns":["{{LabelCode}}"]}]}
        """;

    /// <summary>Edits an element's document and saves it.</summary>
    private static async Task<SaveResult> EditAsync(EditorRepo r, string id, Action<JsonObject> edit)
    {
        var current = (await r.Store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        edit(node);
        return await r.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
    }

    /// <summary>Sets a property of the notes table's column at an index (the billing fixture binds entity InvoiceNote to it).</summary>
    private static Task<SaveResult> EditNotesColumnAsync(EditorRepo r, int index, string property, JsonNode? value) =>
        EditAsync(r, Notes, node => node["columns"]![index]![property] = value);

    private static Task<SaveResult> CreateNoteSeedAsync(EditorRepo r) => CreateAsync(r, $$"""
        {"kind":"seed","id":"{{NoteSeed}}","name":"notes","target":"01K6BND0000000000000000010",
         "columns":["01K6BND0000000000000000011","01K6BND0000000000000000012","01K6BND0000000000000000013"],
         "rows":[{"id":"01K6TSS0000000000000000031","values":["{{NoteId}}","{{NoteInvoice}}","Paid by card"]}]}
        """);

    [Fact]
    public async Task A_stable_id_column_is_filled_for_every_row_the_table_receives()
    {
        await using var r = await RepoAsync();

        // A table nothing fills yet takes a stable id before its seeds exist.
        await CreateAsync(r, LabelsJson());
        await CreateAsync(r, $$"""
            {"kind":"reference-type","id":"{{LabelKind}}","name":"LabelKind",
             "code":{"id":"01K6TSS0000000000000000021","length":10},"label":{"id":"01K6TSS0000000000000000022","length":40},
             "bindings":[{"id":"01K6TSS0000000000000000023","database":"{{EditorRepo.MainDatabaseId}}","table":"{{Labels}}",
               "fields":[{"attribute":"code","column":"{{LabelCode}}"},{"attribute":"label","column":"{{LabelName}}"}]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{LabelKindSeed}}","name":"labelKinds","target":"{{LabelKind}}","columns":["code","label"],
             "rows":[{"id":"01K6TSS0000000000000000026","values":["alpha","Alpha"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{LabelSeed}}","name":"labels","target":"{{Labels}}","key":"{{LabelsByCode}}","columns":["{{LabelCode}}","{{LabelName}}"],
             "rows":[{"id":"01K6TSS0000000000000000016","values":["beta","Beta"]}]}
            """);

        // Entity rows that reach the notes table through the binding fill a stable id column the binding does not write.
        Assert.Equal(SaveOutcome.Saved, (await EditAsync(r, Notes, node => node["columns"]!.AsArray().Add(
            new JsonObject { ["id"] = "01K6TSS0000000000000000032", ["name"] = "ref", ["type"] = "uuid", ["stableId"] = true }))).Outcome);
        await CreateNoteSeedAsync(r);

        var model = await ResolvedAsync(r);
        var main = model.Databases.Single(d => d.Id == EditorRepo.MainDatabaseId);
        Assert.True(main.Tables.Single(t => t.Name == "labels").Columns.Single(c => c.Name == "id").StableId);
        var labels = main.SeedTables.Single(t => t.Table.Name == "labels");
        Assert.Equal(["code"], labels.KeyColumns.Select(c => c.Name));
        Assert.Equal(
            [("list", "alpha", StableIds.Of(Labels, ["alpha"])), ("table", "beta", StableIds.Of(Labels, ["beta"]))],
            labels.Rows.Select(x => (x.Source, (string)x.Values["code"]!, (string)x.Values["id"]!)));
        Assert.Contains(labels.Columns, c => c.Name == "id");

        // The note's key is its id, a uuid, which names the stable id in lowercase with hyphens.
        var note = Assert.Single(main.SeedTables.Single(t => t.Table.Name == "notes").Rows);
        Assert.Equal(StableIds.Of(Notes, [NoteId.ToLowerInvariant()]), note.Values["ref"]);
        Assert.Equal(NoteId, note.Values["id"]);

        // Validation has nothing to say about a stable id the engine fills.
        var report = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ7108" or "MQ7109" or "MQ7120" or "MQ7124" or "MQ7125");
    }

    [Fact]
    public async Task A_value_a_binding_writes_is_kept()
    {
        await using var r = await RepoAsync();

        // entity_id is written by the binding's invoiceId field; ref is not written, so the engine fills it.
        Assert.Equal(SaveOutcome.Saved, (await EditNotesColumnAsync(r, 2, "stableId", true)).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await EditAsync(r, Notes, node => node["columns"]!.AsArray().Add(
            new JsonObject { ["id"] = "01K6TSS0000000000000000032", ["name"] = "ref", ["type"] = "uuid", ["stableId"] = true }))).Outcome);
        await CreateNoteSeedAsync(r);

        var main = (await ResolvedAsync(r)).Databases.Single(d => d.Id == EditorRepo.MainDatabaseId);
        var note = Assert.Single(main.SeedTables.Single(t => t.Table.Name == "notes").Rows);
        Assert.Equal(NoteInvoice, note.Values["entity_id"]);
        Assert.Equal(StableIds.Of(Notes, [NoteId.ToLowerInvariant()]), note.Values["ref"]);
    }

    [Fact]
    public async Task A_stable_id_on_a_text_column_is_MQ7124()
    {
        await using var r = await RepoAsync();
        var refused = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse($$"""
            {"kind":"table","id":"{{Labels}}","name":"labels","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{LabelId}}","name":"id","type":"uuid","nullable":false},
              {"id":"{{LabelCode}}","name":"code","type":"string","length":10,"nullable":false,"stableId":true}],
             "primaryKey":{"columns":["{{LabelId}}"]} }
            """)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var d = Assert.Single(refused.Diagnostics, d => d.Rule == "MQ7124");
        Assert.Equal((DiagnosticSeverity.Error, LabelCode, "/columns/1/stableId"), (d.Severity, d.ElementId, d.JsonPointer));
        Assert.Contains("is a string column: a stable id is a uuid", d.Message, StringComparison.Ordinal);

        // A uuid column the database fills (computed) cannot take one either.
        refused = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(LabelsJson("\"stableId\":true,\"computed\":\"gen_random_uuid()\""))!.ToJsonString()),
            ChangeSource.Editor, Ct);
        Assert.Contains(refused.Diagnostics, x => x.Rule == "MQ7124" && x.Message.Contains("the database fills its value (computed)", StringComparison.Ordinal));

        // So does a stereotype's column.
        var stereotype = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse($$"""
            {"kind":"stereotype","id":"01K6TSS0000000000000000040","key":"keyed","name":"Keyed","appliesTo":["table"],
             "columns":[{"id":"01K6TSS0000000000000000041","name":"ref","type":"int64","stableId":true}]}
            """)!.ToJsonString()), ChangeSource.Editor, Ct);
        d = Assert.Single(stereotype.Diagnostics, d => d.Rule == "MQ7124");
        Assert.Equal("/columns/0/stableId", d.JsonPointer);
    }

    [Fact]
    public async Task A_row_key_holding_the_stable_id_column_is_MQ7125()
    {
        await using var r = await RepoAsync();
        await CreateAsync(r, LabelsJson());

        // Without a key, the seed finds its rows by the primary key: the stable id column itself.
        var seed = $$"""
            {"kind":"seed","id":"{{LabelSeed}}","name":"labels","target":"{{Labels}}","columns":["{{LabelCode}}","{{LabelName}}"],
             "rows":[{"id":"01K6TSS0000000000000000016","values":["beta","Beta"]}]}
            """;
        var refused = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(seed)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var d = Assert.Single(refused.Diagnostics, d => d.Rule == "MQ7125");
        Assert.Equal((DiagnosticSeverity.Error, LabelSeed, "/target"), (d.Severity, d.ElementId, d.JsonPointer));
        Assert.Contains("its row key holds the stable id column 'id' itself", d.Message, StringComparison.Ordinal);

        // Keyed by the unique code, it derives the ids from the code.
        await CreateAsync(r, seed.Replace("\"columns\":", $"\"key\":\"{LabelsByCode}\",\"columns\":", StringComparison.Ordinal));
        var row = Assert.Single((await ResolvedAsync(r)).Databases.Single(x => x.Id == EditorRepo.MainDatabaseId).SeedTables.Single(t => t.Table.Name == "labels").Rows);
        Assert.Equal(StableIds.Of(Labels, ["beta"]), row.Values["id"]);

        // A key that holds a value the database fills (an identity) is MQ7125 too, at the seed's key. A save is refused while it adds
        // errors: write the table straight to disk and load.
        var path = r.Repo.PathOf((await r.Store.GetElementAsync(Labels, Ct))!.Path);
        var table = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        table["columns"]!.AsArray().Add(new JsonObject { ["id"] = "01K6TSS0000000000000000017", ["name"] = "serial", ["type"] = "int64", ["generated"] = "identity" });
        table["uniques"]![0]!["columns"]!.AsArray().Add("01K6TSS0000000000000000017");
        File.WriteAllText(path, table.ToJsonString());
        await r.Store.RefreshAsync([path], Ct);
        var rules = (await r.Store.ValidateAsync(new ValidationScope([LabelSeed]), Ct)).Diagnostics;
        Assert.Contains(rules, x => x.Rule == "MQ7125" && x.ElementId == LabelSeed && x.JsonPointer == "/key"
            && x.Message.Contains("column 'serial', whose value the database fills (identity)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_table_only_a_bound_entity_fills_reports_MQ7125_on_the_table()
    {
        await using var r = await RepoAsync();

        // The notes table receives InvoiceNote's rows through its binding and has no seed of its own: its key is the primary key.
        var refused = await EditNotesColumnAsync(r, 0, "stableId", true);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var d = Assert.Single(refused.Diagnostics, d => d.Rule == "MQ7125");
        Assert.Equal((DiagnosticSeverity.Error, "01K6BND0000000000000000002", "/columns/0/stableId"), (d.Severity, d.ElementId, d.JsonPointer));
        Assert.Contains("Table 'notes' takes stable ids and receives the rows of a bound entity", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_a_binding_only_reads_does_not_meet_MQ7125()
    {
        await using var r = await RepoAsync();

        // Both bindings over notes read it and write nothing, so no row reaches the table and its primary key keys nothing.
        foreach (var entity in new[] { "01K6BND0000000000000000010", "01K6BND0000000000000000020" })
            Assert.Equal(SaveOutcome.Saved, (await EditAsync(r, entity, node => node["bindings"]![0]!["write"] = "none")).Outcome);

        var saved = await EditNotesColumnAsync(r, 0, "stableId", true);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.DoesNotContain(saved.Diagnostics, d => d.Rule == "MQ7125");
    }

    [Fact]
    public async Task A_seed_cell_for_a_stable_id_column_is_MQ7108()
    {
        await using var r = await RepoAsync();
        await CreateAsync(r, LabelsJson());
        var refused = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse($$"""
            {"kind":"seed","id":"{{LabelSeed}}","name":"labels","target":"{{Labels}}","key":"{{LabelsByCode}}","columns":["{{LabelId}}","{{LabelCode}}"],
             "rows":[{"id":"01K6TSS0000000000000000016","values":["0193a3c2-0000-7000-8000-000000000009","beta"]}]}
            """)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var d = Assert.Single(refused.Diagnostics, d => d.Rule == "MQ7108");
        Assert.Equal("/columns/0", d.JsonPointer);
        Assert.Contains("takes a stable id, which the engine derives from the row key", d.Message, StringComparison.Ordinal);
    }

    // Foreign keys by natural key (next-phase-2-design.md section 3.4).
    private const string Natural = "01K6TSN00000000000000000", OnSqlite = "01K6TSP00000000000000000";

    private static string N(int n, string prefix = Natural) => prefix + n.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// customers (id, name, email unique as uq_customers_email) with a seed of Ada and Grace keyed by email, and invoices (id, number
    /// unique as uq_invoices_number, issued_on, customer_id) whose fk_invoices_customer references customers(id). The ids take stable
    /// ids, or with <paramref name="identity"/> customers.id is a bigint the database fills.
    /// </summary>
    private static async Task NaturalKeyTablesAsync(EditorRepo r, bool identity = false, string database = EditorRepo.MainDatabaseId,
        string prefix = Natural, bool nullable = false)
    {
        // Billing projects its Customer and Invoice entities into billing.customers and billing.invoices: these go in schema sales.
        var schema = "";
        if (database == EditorRepo.MainDatabaseId)
        {
            await SalesSchemaAsync(r);
            schema = $$""","schema":"{{SalesSchema}}" """;
        }

        var customerId = identity ? "\"type\":\"int64\",\"nullable\":false,\"generated\":\"identity\"" : "\"type\":\"uuid\",\"nullable\":false,\"stableId\":true";
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{N(1, prefix)}}","name":"customers","database":"{{database}}"{{schema}},"columns":[
              {"id":"{{N(2, prefix)}}","name":"id",{{customerId}}},
              {"id":"{{N(3, prefix)}}","name":"name","type":"string","nullable":false},
              {"id":"{{N(4, prefix)}}","name":"email","type":"string","length":254,"nullable":false}],
             "primaryKey":{"columns":["{{N(2, prefix)}}"]},"uniques":[{"id":"{{N(5, prefix)}}","name":"uq_customers_email","columns":["{{N(4, prefix)}}"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{N(10, prefix)}}","name":"invoices","database":"{{database}}"{{schema}},"columns":[
              {"id":"{{N(11, prefix)}}","name":"id","type":"uuid","nullable":false,"stableId":true},
              {"id":"{{N(12, prefix)}}","name":"number","type":"string","length":32,"nullable":false},
              {"id":"{{N(13, prefix)}}","name":"issued_on","type":"date"},
              {"id":"{{N(14, prefix)}}","name":"customer_id","type":"{{(identity ? "int64" : "uuid")}}","nullable":{{(nullable ? "true" : "false")}}}],
             "primaryKey":{"columns":["{{N(11, prefix)}}"]},"uniques":[{"id":"{{N(15, prefix)}}","name":"uq_invoices_number","columns":["{{N(12, prefix)}}"]}],
             "foreignKeys":[{"id":"{{N(16, prefix)}}","name":"fk_invoices_customer","columns":["{{N(14, prefix)}}"],"referencesTable":"{{N(1, prefix)}}"}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{N(6, prefix)}}","name":"customers","target":"{{N(1, prefix)}}","key":"{{N(5, prefix)}}","columns":["{{N(3, prefix)}}","{{N(4, prefix)}}"],
             "rows":[{"id":"{{N(7, prefix)}}","values":["Ada","ada@example.com"]},{"id":"{{N(8, prefix)}}","values":["Grace","grace@example.com"]}]}
            """);
    }

    private const string SalesSchema = "01K6TSN0000000000000000098";

    /// <summary>Declares schema sales in billing's main database, once.</summary>
    private static async Task SalesSchemaAsync(EditorRepo r)
    {
        if (((Database)(await r.Store.GetElementAsync(EditorRepo.MainDatabaseId, Ct))!.Element).Schemas.Any(x => x.Id == SalesSchema))
            return;
        Assert.Equal(SaveOutcome.Saved, (await EditAsync(r, EditorRepo.MainDatabaseId,
            node => node["schemas"]!.AsArray().Add(new JsonObject { ["id"] = SalesSchema, ["name"] = "sales" }))).Outcome);
    }

    /// <summary>An invoices seed keyed by number whose third column is the foreign-key entry; each row is (number, the cell's JSON).</summary>
    private static string InvoiceSeedJson(string prefix, params (string Number, string Customer)[] rows) => $$"""
        {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{N(17, prefix)}}","name":"invoices","target":"{{N(10, prefix)}}","key":"{{N(15, prefix)}}",
         "columns":["{{N(12, prefix)}}","{{N(13, prefix)}}","{{N(16, prefix)}}"],
         "rows":[{{string.Join(",", rows.Select((x, i) => $$"""{"id":"{{N(20 + i, prefix)}}","values":["{{x.Number}}","2026-01-05",{{x.Customer}}]}"""))}}]}
        """;

    /// <summary>Writes a seed straight to disk and loads it (a save is refused while it adds errors).</summary>
    private static async Task WriteSeedAsync(EditorRepo r, string id, string json)
    {
        var path = r.Repo.PathOf($".maquettiste/model/seeds/{id}/{id}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonNode.Parse(json)!.ToJsonString());
        await r.Store.RefreshAsync([path], Ct);
    }

    /// <summary>The findings of some rules on a seed and its rows.</summary>
    private static async Task<List<Diagnostic>> RulesOfAsync(EditorRepo r, string seedId, params string[] rules)
    {
        var ids = new HashSet<string>(((Seed)(await r.Store.GetElementAsync(seedId, Ct))!.Element).Rows.Select(x => x.Id), StringComparer.Ordinal) { seedId };
        return [.. (await r.Store.ValidateAsync(new ValidationScope([seedId]), Ct)).Diagnostics.Where(d => d.ElementId is { } id && ids.Contains(id) && rules.Contains(d.Rule))];
    }

    [Fact]
    public async Task A_foreign_key_entry_fills_its_columns_from_the_referenced_row()
    {
        await using var r = await RepoAsync();
        await NaturalKeyTablesAsync(r, nullable: true);
        await CreateAsync(r, InvoiceSeedJson(Natural, ("INV-1", "\"ada@example.com\""), ("INV-2", "\"grace@example.com\"")));
        var report = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.True(!report.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error),
            string.Join("; ", report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message)));

        var model = await ResolvedAsync(r);
        var seed = model.Seeds.Single(x => x.Id == N(17));
        var entry = seed.Columns[2];
        Assert.Equal(("fk_invoices_customer", "foreign-key", "customer_id"), (entry.Name, entry.Kind, entry.ForeignKey!.Columns.Single().Name));
        Assert.Equal("ada@example.com", seed.Rows[0].Values["fk_invoices_customer"]); // the cell as written

        var main = model.Databases.Single(d => d.Id == EditorRepo.MainDatabaseId);
        var tables = main.SeedTables.Select(t => t.Table.Name).ToList();
        Assert.True(tables.IndexOf("customers") < tables.IndexOf("invoices"), string.Join(", ", tables));
        var customers = main.SeedTables.Single(t => t.Table.Name == "customers").Rows;
        var invoices = main.SeedTables.Single(t => t.Table.Name == "invoices");
        Assert.Equal([StableIds.Of(N(1), ["ada@example.com"]), StableIds.Of(N(1), ["grace@example.com"])], customers.Select(x => x.Values["id"]));
        Assert.Equal(customers.Select(x => x.Values["id"]), invoices.Rows.Select(x => x.Values["customer_id"]));
        Assert.Equal(StableIds.Of(N(10), ["INV-1"]), invoices.Rows[0].Values["id"]);
        Assert.Contains(invoices.Columns, c => c.Name == "customer_id");
        Assert.DoesNotContain("fk_invoices_customer", invoices.Rows[0].Values.Keys);

        var link = invoices.Rows[0].Refs["fk_invoices_customer"];
        Assert.Same(customers[0], link.Row);
        Assert.Equal(("customers", "fk_invoices_customer"), (link.Table.Name, link.ForeignKey.Name));
        Assert.Equal(["email"], link.KeyColumns.Select(c => c.Name));
        Assert.Equal(["ada@example.com"], link.Key);
        Assert.Empty(link.Unknown);

        // Deleting the foreign key drops its entry and cells from the seed (the planner's step for a removed sub-element).
        Assert.Equal(SaveOutcome.Saved, (await EditAsync(r, N(10), node => node.Remove("foreignKeys"))).Outcome);
        var kept = (Seed)(await r.Store.GetElementAsync(N(17), Ct))!.Element;
        Assert.Equal([N(12), N(13)], kept.Columns);
        Assert.All(kept.Rows, x => Assert.Equal(2, x.Values.Count));
    }

    [Fact]
    public async Task A_foreign_key_entry_beside_its_own_column_is_MQ7126()
    {
        await using var r = await RepoAsync();
        await NaturalKeyTablesAsync(r);

        // The entry beside its own column, a foreign key of another table, and one whose table holds no rows (currencies has no seed).
        Assert.Equal(SaveOutcome.Saved, (await EditAsync(r, N(10), node =>
        {
            node["columns"]!.AsArray().Add(new JsonObject { ["id"] = N(30), ["name"] = "currency", ["type"] = "string", ["length"] = 3 });
            node["foreignKeys"]!.AsArray().Add(new JsonObject { ["id"] = N(31), ["name"] = "fk_invoices_currency", ["columns"] = new JsonArray(N(30)), ["referencesTable"] = Currencies });
        })).Outcome);
        await WriteSeedAsync(r, N(17), $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{N(17)}}","name":"invoices","target":"{{N(10)}}","key":"{{N(15)}}",
             "columns":["{{N(12)}}","{{N(14)}}","{{N(16)}}","{{PriceListKey}}","{{N(31)}}"],
             "rows":[{"id":"{{N(20)}}","values":["INV-1","{{StableIds.Of(N(1), ["ada@example.com"])}}","ada@example.com",null,"EUR"]}]}
            """);

        var rules = await RulesOfAsync(r, N(17), "MQ7126", "MQ7107");
        Assert.Equal(["/columns/2", "/columns/3", "/columns/4"], rules.Where(d => d.Rule == "MQ7126").Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(rules, d => d.Rule == "MQ7107");
        string At(int k) => rules.Single(d => d.JsonPointer == "/columns/" + k).Message;
        Assert.Contains("lists foreign key 'fk_invoices_customer' beside its own column 'customer_id'", At(2), StringComparison.Ordinal);
        Assert.Contains("is foreign key 'fk_price_lists_currency' of table 'price_lists', not of table 'invoices'", At(3), StringComparison.Ordinal);
        Assert.Contains("references table 'currencies', which has no rows in the model", At(4), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_natural_key_that_finds_no_row_is_MQ7111()
    {
        await using var r = await RepoAsync();
        await NaturalKeyTablesAsync(r);
        // A customer seeded in dev only.
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{N(40)}}","name":"devCustomers","target":"{{N(1)}}","key":"{{N(5)}}","columns":["{{N(3)}}","{{N(4)}}"],"environments":["dev"],
             "rows":[{"id":"{{N(41)}}","values":["Dev","dev@example.com"]}]}
            """);
        await WriteSeedAsync(r, N(17), InvoiceSeedJson(Natural, ("INV-1", "\"nobody@example.com\""), ("INV-2", "\"dev@example.com\""), ("INV-3", "\"ada@example.com\"")));

        var rules = await RulesOfAsync(r, N(17), "MQ7111");
        Assert.Equal(["/rows/0/values/2", "/rows/1/values/2"], rules.Select(d => d.JsonPointer));
        Assert.Contains("foreign key 'fk_invoices_customer' names no row of table 'customers' by its column 'email' \"nobody@example.com\"", rules[0].Message, StringComparison.Ordinal);
        Assert.Contains("that its rows do not hold in every environment", rules[1].Message, StringComparison.Ordinal);

        // A row with no match keeps a ref without a row; its column stays null (the model has errors, so resolve it directly).
        var main = Maquettiste.Engine.Tests.Resolution.ResolutionKit.Resolve(await r.Store.GetSnapshotAsync(Ct)).Databases.Single(d => d.Id == EditorRepo.MainDatabaseId);
        var nobody = main.SeedTables.Single(t => t.Table.Name == "invoices").Rows[0];
        Assert.Null(nobody.Refs["fk_invoices_customer"].Row);
        Assert.Null(nobody.Values["customer_id"]);
    }

    private const string Branches = "01K6TSN0000000000000000050", Staff = "01K6TSN0000000000000000060";

    [Fact]
    public async Task A_composite_natural_key_must_be_an_array_in_key_order()
    {
        await using var r = await RepoAsync();
        // branches are found by (company, region); staff's fk_staff_branch references those two columns.
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Branches}}","name":"branches","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{N(51)}}","name":"id","type":"int32","nullable":false},
              {"id":"{{N(52)}}","name":"company","type":"string","length":8,"nullable":false},
              {"id":"{{N(53)}}","name":"region","type":"string","length":8,"nullable":false}],
             "primaryKey":{"columns":["{{N(51)}}"]},"uniques":[{"id":"{{N(54)}}","name":"uq_branches_place","columns":["{{N(52)}}","{{N(53)}}"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{N(55)}}","name":"branches","target":"{{Branches}}","key":"{{N(54)}}","columns":["{{N(51)}}","{{N(52)}}","{{N(53)}}"],
             "rows":[{"id":"{{N(56)}}","values":[1,"ACME","EU"]},{"id":"{{N(57)}}","values":[2,"ACME","US"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{Staff}}","name":"staff","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{N(61)}}","name":"id","type":"int32","nullable":false},
              {"id":"{{N(62)}}","name":"company","type":"string","length":8},
              {"id":"{{N(63)}}","name":"region","type":"string","length":8}],
             "primaryKey":{"columns":["{{N(61)}}"]},"uniques":[{"id":"{{N(64)}}","name":"uq_staff_company","columns":["{{N(61)}}","{{N(62)}}"]}],
             "foreignKeys":[{"id":"{{N(65)}}","name":"fk_staff_branch","columns":["{{N(62)}}","{{N(63)}}"],"referencesTable":"{{Branches}}","referencesColumns":["{{N(52)}}","{{N(53)}}"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{N(66)}}","name":"staff","target":"{{Staff}}","columns":["{{N(61)}}","{{N(65)}}"],
             "rows":[{"id":"{{N(67)}}","values":[10,["ACME","EU"]]}]}
            """);
        var staff = Assert.Single((await ResolvedAsync(r)).Databases.Single(d => d.Id == EditorRepo.MainDatabaseId).SeedTables.Single(t => t.Table.Name == "staff").Rows);
        Assert.Equal(("ACME", "EU"), (staff.Values["company"], staff.Values["region"]));
        Assert.Equal(["ACME", "EU"], staff.Refs["fk_staff_branch"].Key);
        Assert.Equal(1L, staff.Refs["fk_staff_branch"].Row!.Values["id"]);

        // A value, the parts in the wrong order and a list of the wrong length; a row key that holds one column of the entry.
        await WriteSeedAsync(r, N(68), $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{N(68)}}","name":"moreStaff","target":"{{Staff}}","columns":["{{N(61)}}","{{N(65)}}"],
             "rows":[{"id":"{{N(70)}}","values":[11,"ACME"]},{"id":"{{N(71)}}","values":[12,["EU","ACME"]]},{"id":"{{N(72)}}","values":[13,["ACME","EU","X"]]}]}
            """);
        var rules = await RulesOfAsync(r, N(68), "MQ7109", "MQ7111");
        Assert.Equal([("MQ7109", "/rows/0/values/1"), ("MQ7111", "/rows/1/values/1"), ("MQ7109", "/rows/2/values/1")],
            rules.OrderBy(d => d.JsonPointer, StringComparer.Ordinal).Select(d => (d.Rule, d.JsonPointer!)));
        Assert.Contains("a text, but table 'branches' finds its rows by its columns ('company', 'region'): give a list of 2 values in that order",
            rules.Single(d => d.JsonPointer == "/rows/0/values/1").Message, StringComparison.Ordinal);
        await WriteSeedAsync(r, N(73), $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{N(73)}}","name":"keyedStaff","target":"{{Staff}}","key":"{{N(64)}}","columns":["{{N(61)}}","{{N(65)}}"],
             "rows":[{"id":"{{N(74)}}","values":[14,["ACME","US"]]}]}
            """);
        var part = Assert.Single(await RulesOfAsync(r, N(73), "MQ7126"));
        Assert.Equal("/columns/1", part.JsonPointer);
        Assert.Contains("holds some but not all columns of foreign key 'fk_staff_branch'", part.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_natural_key_finds_the_rows_of_a_bound_reference_type()
    {
        await using var r = await RepoAsync();
        // Labels only a reference type fills: its binding keys them by the unique code, which holds the code's column.
        await CreateAsync(r, LabelsJson());
        await CreateAsync(r, $$"""
            {"kind":"reference-type","id":"{{LabelKind}}","name":"LabelKind",
             "code":{"id":"01K6TSS0000000000000000021","length":10},"label":{"id":"01K6TSS0000000000000000022","length":40},
             "bindings":[{"id":"01K6TSS0000000000000000023","database":"{{EditorRepo.MainDatabaseId}}","table":"{{Labels}}",
               "fields":[{"attribute":"code","column":"{{LabelCode}}"},{"attribute":"label","column":"{{LabelName}}"}]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{LabelKindSeed}}","name":"labelKinds","target":"{{LabelKind}}","columns":["code","label"],
             "rows":[{"id":"01K6TSS0000000000000000026","values":["alpha","Alpha"]}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"table","id":"{{N(80)}}","name":"tagged","database":"{{EditorRepo.MainDatabaseId}}","columns":[
              {"id":"{{N(81)}}","name":"id","type":"int32","nullable":false},
              {"id":"{{N(82)}}","name":"label_id","type":"uuid","nullable":false}],
             "primaryKey":{"columns":["{{N(81)}}"]},
             "foreignKeys":[{"id":"{{N(83)}}","name":"fk_tagged_label","columns":["{{N(82)}}"],"referencesTable":"{{Labels}}"}]}
            """);
        await CreateAsync(r, $$"""
            {"kind":"seed","id":"{{N(84)}}","name":"tagged","target":"{{N(80)}}","columns":["{{N(81)}}","{{N(83)}}"],
             "rows":[{"id":"{{N(85)}}","values":[1,"alpha"]}]}
            """);
        Assert.Empty(await RulesOfAsync(r, N(84), "MQ7111", "MQ7109", "MQ7126"));

        var main = (await ResolvedAsync(r)).Databases.Single(d => d.Id == EditorRepo.MainDatabaseId);
        var tagged = Assert.Single(main.SeedTables.Single(t => t.Table.Name == "tagged").Rows);
        Assert.Equal(StableIds.Of(Labels, ["alpha"]), tagged.Values["label_id"]);
        var link = tagged.Refs["fk_tagged_label"];
        Assert.Equal(("list", "alpha"), (link.Row!.Source, link.Row.Values["code"]));
        Assert.Equal(["code"], link.KeyColumns.Select(c => c.Name));
    }

    [Fact]
    public async Task A_value_the_database_fills_stays_null_with_a_ref()
    {
        await using var r = await RepoAsync();
        await NaturalKeyTablesAsync(r, identity: true);
        await CreateAsync(r, InvoiceSeedJson(Natural, ("INV-1", "\"ada@example.com\""), ("INV-2", "\"grace@example.com\"")));
        Assert.Empty(await RulesOfAsync(r, N(17), "MQ7109", "MQ7111", "MQ7126", "MQ4056"));

        var main = (await ResolvedAsync(r)).Databases.Single(d => d.Id == EditorRepo.MainDatabaseId);
        var customers = main.SeedTables.Single(t => t.Table.Name == "customers").Rows;
        Assert.DoesNotContain("id", customers[0].Values.Keys);
        var invoice = main.SeedTables.Single(t => t.Table.Name == "invoices").Rows[0];
        Assert.True(invoice.Values.ContainsKey("customer_id"));
        Assert.Null(invoice.Values["customer_id"]);
        Assert.Equal(StableIds.Of(N(10), ["INV-1"]), invoice.Values["id"]); // its own key is known
        var link = invoice.Refs["fk_invoices_customer"];
        Assert.Same(customers[0], link.Row);
        Assert.Equal(["customer_id"], link.Unknown.Select(c => c.Name));
        Assert.Equal(["email"], link.KeyColumns.Select(c => c.Name));
        Assert.Equal(["ada@example.com"], link.Key);
    }

    [Fact]
    public async Task Outside_PostgreSQL_a_lookup_cell_is_MQ4056()
    {
        await using var r = await RepoAsync();
        const string Edge = "01K6TSP0000000000000000099";
        await CreateAsync(r, $$"""{"kind":"database","id":"{{Edge}}","name":"edge","dialect":"sqlite"}""");
        await NaturalKeyTablesAsync(r, identity: true, database: Edge, prefix: OnSqlite);
        var saved = await CreateAsync(r, InvoiceSeedJson(OnSqlite, ("INV-1", "\"ada@example.com\"")));

        var d = Assert.Single(saved.Diagnostics, x => x.Rule == "MQ4056");
        Assert.Equal((DiagnosticSeverity.Warning, "/columns/2"), (d.Severity, d.JsonPointer));
        Assert.Contains("names rows of table 'customers' whose column 'id' the database fills (identity), which sqlite (database 'edge') cannot look up", d.Message, StringComparison.Ordinal);

        // The same seed on PostgreSQL has nothing to say.
        await NaturalKeyTablesAsync(r, identity: true);
        Assert.DoesNotContain((await CreateAsync(r, InvoiceSeedJson(Natural, ("INV-1", "\"ada@example.com\"")))).Diagnostics, x => x.Rule == "MQ4056");
    }
}
