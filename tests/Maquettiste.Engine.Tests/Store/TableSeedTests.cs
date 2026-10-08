using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
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

    private static async Task<EditorRepo> RepoAsync()
    {
        var r = EditorRepo.Create(packs: false);
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
