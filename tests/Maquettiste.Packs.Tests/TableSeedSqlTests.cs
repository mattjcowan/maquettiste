using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// sql-ddl's seed scripts for table seeds (2026-10-07): a converging seed upserts on its row key in each dialect (ON CONFLICT on
/// PostgreSQL and SQLite, MERGE on SQL Server), with delete it removes the rows it does not hold, a seed applied once inserts what is
/// missing, and a seed of one environment goes to that environment's script (seed.dev.sql) when the pack names the environment.
/// On SQLite the scripts run, twice, when sqlite3 is on the PATH.
/// </summary>
public sealed class TableSeedSqlTests
{
    private const string MainId = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string ReportingId = "01J92P0V2A0000000000000001";
    private const string LocalId = "01J92P0V2A0000000000000002";

    private static void AddTables(PackRepo repo, string folder, string database, string prefix, string idBase)
    {
        string Id(int n) => idBase + n.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        repo.Write($".maquettiste/model/databases/{folder}/tables/{prefix}currencies.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{Id(1)}}","name":"{{prefix}}currencies","database":"{{database}}","columns":[
              {"id":"{{Id(2)}}","name":"code","type":"string","length":3,"nullable":false},
              {"id":"{{Id(3)}}","name":"name","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{Id(2)}}"]} }
            """);
        repo.Write($".maquettiste/model/databases/{folder}/tables/{prefix}price-lists.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{Id(10)}}","name":"{{prefix}}price_lists","database":"{{database}}","columns":[
              {"id":"{{Id(11)}}","name":"id","type":"int32","nullable":false},
              {"id":"{{Id(12)}}","name":"currency","type":"string","length":3,"nullable":false},
              {"id":"{{Id(13)}}","name":"label","type":"string","length":20}],
             "primaryKey":{"columns":["{{Id(11)}}"]},
             "foreignKeys":[{"id":"{{Id(14)}}","name":"fk_{{prefix}}price_lists_currency","columns":["{{Id(12)}}"],"referencesTable":"{{Id(1)}}"}]}
            """);
        repo.Write($".maquettiste/model/seeds/{prefix}currencies/currencies.json", $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{Id(20)}}","name":"currencies","target":"{{Id(1)}}","columns":["{{Id(2)}}","{{Id(3)}}"],
             "apply":"converge","delete":true,
             "rows":[{"id":"{{Id(21)}}","values":["EUR","Euro"]},{"id":"{{Id(22)}}","values":["USD","US dollar"]}]}
            """);
        repo.Write($".maquettiste/model/seeds/{prefix}price-lists/demo-lists.json", $$"""
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{Id(30)}}","name":"demoLists","target":"{{Id(10)}}","columns":["{{Id(11)}}","{{Id(12)}}","{{Id(13)}}"],
             "environments":["dev"],
             "rows":[{"id":"{{Id(31)}}","values":[1,"EUR","Standard"]},{"id":"{{Id(32)}}","values":[2,"USD","Export"]}]}
            """);
    }

    private static PackRepo Repo()
    {
        var repo = PackRepo.BillingDialects();
        AddTables(repo, "main", MainId, "", "01K6TSQ00000000000000001");
        AddTables(repo, "reporting", ReportingId, "rep_", "01K6TSQ00000000000000002");
        AddTables(repo, "local", LocalId, "loc_", "01K6TSQ00000000000000003");
        // An entity bound to the shared notes table: its seed reaches the table through the binding, with the binding's constant.
        repo.Write(".maquettiste/model/seeds/invoice-note/notes.json", """
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01K6TSQ0000000000000000040","name":"notes","target":"01K6BND0000000000000000010",
             "columns":["01K6BND0000000000000000011","01K6BND0000000000000000012","01K6BND0000000000000000013"],
             "rows":[{"id":"01K6TSQ0000000000000000041","values":["0193a3c2-0000-7000-8000-000000000001","0193a3c2-0000-7000-8000-000000000002","Paid by card"]}]}
            """);
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
            settings["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["environments"] = new JsonArray("dev", "staging") });
        return repo;
    }

    /// <summary>The repo with the seed-data pack installed, writing under data/ in the given format.</summary>
    internal static PackRepo DataRepo(string format)
    {
        var repo = Repo();
        PackRepo.CopyTree(Path.Combine(Maquettiste.Testing.Fixtures.RepoRoot, "packs", "seed-data"), Path.Combine(repo.Repo.ModelRoot, "templates", "seed-data"));
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"]!["allow"]!.AsArray().Add(new JsonObject { ["path"] = "data" });
            settings["packs"]!["seed-data"] = new JsonObject { ["output"] = "data", ["parameters"] = new JsonObject { ["format"] = format } };
        });
        return repo;
    }

    [Fact]
    public async Task The_seed_data_pack_writes_a_manifest_and_a_csv_per_table_in_foreign_key_order()
    {
        using var repo = DataRepo("csv");
        await repo.GenerateCleanlyAsync(packs: ["seed-data"]);

        var manifest = JsonNode.Parse(repo.Read("data/main/manifest.json"))!;
        Assert.Equal("main", (string)manifest["database"]!);
        Assert.Matches("^[0-9a-f]{64}$", (string)manifest["seedHash"]!);
        var tables = manifest["tables"]!.AsArray().Select(t => (string)t!["table"]!).ToList();
        Assert.True(tables.IndexOf("currencies") < tables.IndexOf("price_lists"), string.Join(", ", tables));
        var currencies = manifest["tables"]!.AsArray().Single(t => (string)t!["table"]! == "currencies")!;
        Assert.Equal("billing.currencies.csv", (string)currencies["file"]!);
        Assert.Equal(["code"], currencies["key"]!.AsArray().Select(k => (string)k!));
        var seed = currencies["seeds"]![0]!;
        Assert.Equal(("currencies", "table", "converge", true, 0, 2), ((string)seed["seed"]!, (string)seed["source"]!, (string)seed["apply"]!, (bool)seed["delete"]!, (int)seed["firstRow"]!, (int)seed["rowCount"]!));
        var lists = manifest["tables"]!.AsArray().Single(t => (string)t!["table"]! == "price_lists")!["seeds"]![0]!;
        Assert.Equal(["dev"], lists["environments"]!.AsArray().Select(e => (string)e!));
        Assert.Equal("once", (string)lists["apply"]!);

        Assert.Equal("code,name\nEUR,Euro\nUSD,US dollar\n", repo.Read("data/main/billing.currencies.csv").ReplaceLineEndings("\n"));
        Assert.StartsWith("id,currency,label\n1,EUR,Standard\n", repo.Read("data/main/billing.price_lists.csv").ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_seed_data_pack_writes_one_json_file_per_database_when_asked()
    {
        using var repo = DataRepo("json");
        await repo.GenerateCleanlyAsync(packs: ["seed-data"]);

        Assert.False(File.Exists(repo.PathOf("data/main/manifest.json")));
        var data = JsonNode.Parse(repo.Read("data/main/seed-data.json"))!;
        var currencies = data["tables"]!.AsArray().Single(t => (string)t!["table"]! == "currencies")!;
        Assert.Null(currencies["file"]);
        var rows = currencies["rows"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Equal(("EUR", "Euro"), ((string)rows[0]!["values"]!["code"]!, (string)rows[0]!["values"]!["name"]!));
        var lists = data["tables"]!.AsArray().Single(t => (string)t!["table"]! == "price_lists")!["rows"]!.AsArray();
        Assert.Equal(1, (int)lists[0]!["values"]!["id"]!);
    }

    [Fact]
    public async Task Table_seeds_upsert_or_insert_once_per_dialect_and_an_environments_rows_go_to_its_own_script()
    {
        using var repo = Repo();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var main = repo.Read("db/main/seed.sql");
        Assert.Contains("-- 2 rows into currencies, kept as the model has them.", main, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO billing.currencies (code, name) VALUES\n    ('EUR', 'Euro'),\n    ('USD', 'US dollar')\nON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name;", main, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM billing.currencies\nWHERE code NOT IN ('EUR', 'USD');", main, StringComparison.Ordinal);
        Assert.DoesNotContain("price_lists", main, StringComparison.Ordinal); // dev rows only
        Assert.Contains("-- 1 row into notes, inserted when missing.\nINSERT INTO billing.notes (id, entity_type, entity_id, body) VALUES\n    ('0193a3c2-0000-7000-8000-000000000001', 'invoice', '0193a3c2-0000-7000-8000-000000000002', 'Paid by card')\nON CONFLICT (id) DO NOTHING;", main, StringComparison.Ordinal);
        var dev = repo.Read("db/main/seed.dev.sql");
        Assert.Contains("-- 2 rows into price_lists, inserted when missing.", dev, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (id) DO NOTHING;", dev, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.PathOf("db/main/seed.staging.sql"))); // no seed names staging

        var reporting = repo.Read("db/reporting/seed.sql");
        Assert.Contains("MERGE INTO dbo.rep_currencies AS x\nUSING (VALUES\n    (N'EUR', N'Euro'),\n    (N'USD', N'US dollar')) AS v (code, name)\nON x.code = v.code", reporting, StringComparison.Ordinal);
        Assert.Contains("WHEN MATCHED THEN UPDATE SET x.name = v.name", reporting, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT (code, name) VALUES (v.code, v.name);", reporting, StringComparison.Ordinal);

        // A second run with nothing changed writes the same bytes.
        var before = (main, dev, reporting);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"], force: true);
        Assert.Equal(before, (repo.Read("db/main/seed.sql"), repo.Read("db/main/seed.dev.sql"), repo.Read("db/reporting/seed.sql")));

        // SQLite: the schema, then the seed and the dev seed twice (upserts and inserts that skip what is there), then a changed name
        // and a stray row that the next seed run puts back and removes.
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
            return;
        var folder = repo.PathOf("db/local");
        var schema = File.ReadAllText(Path.Combine(folder, "schema.sql"));
        var script = File.ReadAllText(Path.Combine(folder, "seed.sql")) + File.ReadAllText(Path.Combine(folder, "seed.dev.sql"));
        var query = "\nUPDATE loc_currencies SET name = 'Changed' WHERE code = 'EUR';\nINSERT INTO loc_currencies (code, name) VALUES ('XXX', 'Stray');\n"
            + File.ReadAllText(Path.Combine(folder, "seed.sql"))
            + "\nSELECT 'currencies=' || group_concat(code || ':' || name, ',') FROM (SELECT * FROM loc_currencies ORDER BY code);\nSELECT 'lists=' || count(*) FROM loc_price_lists;\n";
        var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1), schema + script + script + query);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("currencies=EUR:Euro,USD:US dollar", run.Output, StringComparison.Ordinal); // name restored, stray row deleted
        Assert.Contains("lists=2", run.Output, StringComparison.Ordinal);
    }
}
