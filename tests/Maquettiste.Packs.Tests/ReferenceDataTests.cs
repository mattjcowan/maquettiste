using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Reference types in the example packs (reference-types-seeds-localization.md sections 1.6 and 2): the sql-ddl realizations
/// (lookup table, check, native) selected by the project's strategy through the pack's <c>strategyMap</c>, reconciled on every
/// run by seed.sql, and the csharp-dapper records, row classes and resource files. Goldens per strategy cover the three dialects;
/// the scripts run on SQLite (through <c>sqlite3</c> when it is on the PATH) and on PostgreSQL (in the Docker container named by
/// <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c>): schema then seed twice, the first migration then seed, then seed again after a
/// code is added and after it is retired.
/// </summary>
public sealed class ReferenceDataTests
{
    private const string UnitSeed = ".maquettiste/model/seeds/unit-of-measure/unit-of-measure.json";

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("check")]
    [InlineData("native")]
    public async Task Sql_ddl_matches_the_golden_tree_of_each_strategy(string strategy)
    {
        using var repo = PackRepo.ReferenceData(strategy);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "reference-data-" + strategy), repo.PathOf("db"));
    }

    [Fact]
    public async Task Csharp_dapper_emits_records_row_classes_and_resource_files()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "reference-data"), repo.PathOf("src/Generated"));
        var unit = repo.Read("src/Generated/ReferenceData/UnitOfMeasure.g.cs");
        Assert.Contains("public sealed record UnitOfMeasure(string Code, string Label, decimal Factor, string? Symbol);", unit, StringComparison.Ordinal);
        Assert.Contains("public static readonly UnitOfMeasure Pinch = new(\"pinch\", \"Pinch\", 0.36m, null);", unit, StringComparison.Ordinal);
        Assert.Contains("<value>Unité de mesure</value>", repo.Read("src/Generated/ReferenceData/ReferenceData.fr.resx"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_strategy_key_the_strategy_map_does_not_know_stops_the_unit()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["referenceData"]!["strategies"]!["table"] = JsonNode.Parse("""{ "collections": true }""");
            settings["conventions"]!["referenceStorage"]!["strategy"] = "table";
        });
        var result = await repo.GenerateAsync(packs: ["sql-ddl"]);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("'table' is not in the pack's strategyMap", StringComparison.Ordinal));

        repo.EditJson(".maquettiste/maquettiste.json", settings =>
            settings["packs"]!["sql-ddl"]!["parameters"] = JsonNode.Parse("""{ "strategyMap": { "table": "lookup-table", "check": "check", "native": "native", "lookup-table": "lookup-table" } }"""));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.Contains("FOREIGN KEY (default_unit) REFERENCES public.units_of_measure (code)", repo.Read("db/main/seed.sql"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("check")]
    [InlineData("native")]
    public async Task Sqlite_runs_the_scripts_twice_and_after_a_code_is_added_and_retired(string strategy)
    {
        var scripts = await ScriptsAsync(strategy, "local");
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
        {
            Assert.Skip("sqlite3 is not on the PATH.");
            return;
        }

        var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1), scripts.Chain("\nSELECT 'rows=' || count(*) FROM recipe_ingredient;\n"));
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("rows=2", run.Output, StringComparison.Ordinal);
        var migrations = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1), scripts.Migration + scripts.Seed + scripts.Seed);
        Assert.True(migrations.ExitCode == 0, migrations.Output);
        var bad = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1), scripts.Schema + scripts.Seed + "\nUPDATE ingredients SET default_unit = 'nope' WHERE sku = 'salt';\n");
        Assert.NotEqual(0, bad.ExitCode);
        Assert.Contains("default_unit_ref", bad.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("check")]
    [InlineData("native")]
    public async Task PostgreSQL_runs_the_scripts_twice_and_after_a_code_is_added_and_retired(string strategy)
    {
        var container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_POSTGRES_CONTAINER");
        var docker = ProcessRunner.FindOnPath("docker");
        if (string.IsNullOrEmpty(container) || docker is null)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_POSTGRES_CONTAINER to run the reference-data scripts on PostgreSQL.");
            return;
        }

        var scripts = await ScriptsAsync(strategy, "main");
        var (code, output) = await PsqlAsync(docker, container, scripts.Chain("\nSELECT 'rows=' || count(*) FROM recipe_ingredient;\n"));
        Assert.True(code == 0 || strategy == "native", output);
        if (strategy == "native")
        {
            // PostgreSQL cannot drop an enum value: retiring a code stops the seed with the codes named.
            Assert.NotEqual(0, code);
            Assert.Contains("unit_of_measure_t holds codes no longer in the seeds of UnitOfMeasure: cup", output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("rows=2", output, StringComparison.Ordinal);
        }

        (code, output) = await PsqlAsync(docker, container, scripts.Migration + scripts.Seed + scripts.Seed);
        Assert.True(code == 0, output);
        (code, output) = await PsqlAsync(docker, container, scripts.Schema + scripts.Seed + "\nUPDATE ingredients SET default_unit = 'nope' WHERE sku = 'salt';\n");
        Assert.NotEqual(0, code);
    }

    /// <summary>The scripts of one database: schema, first migration, seed, then the seed after adding the code "cup" and after
    /// retiring it again.</summary>
    private sealed record Scripts(string Schema, string Migration, string Seed, string SeedAdded, string SeedRetired)
    {
        /// <summary>Schema, seed twice, the seed with the new code (a row uses it, then stops using it), the seed without it.</summary>
        public string Chain(string query) =>
            Schema + Seed + Seed + SeedAdded
            + "\nUPDATE ingredients SET default_unit = 'cup' WHERE sku = 'salt';\nUPDATE ingredients SET default_unit = 'g' WHERE sku = 'salt';\n"
            + query + SeedRetired + query;
    }

    private static async Task<Scripts> ScriptsAsync(string strategy, string database)
    {
        using var repo = PackRepo.ReferenceData(strategy);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var folder = "db/" + database + "/";
        var (schema, migration, seed) = (repo.Read(folder + "schema.sql"), repo.Read(folder + "migrations/0001.sql"), repo.Read(folder + "seed.sql"));
        repo.EditJson(UnitSeed, s => s["rows"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01JRDX00000000000000000001", "values": ["cup", "Cup", 240] }""")));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var added = repo.Read(folder + "seed.sql");
        Assert.Contains("'cup'", added, StringComparison.Ordinal);
        repo.EditJson(UnitSeed, s => s["rows"]!.AsArray().RemoveAt(3));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        return new Scripts(schema, migration, seed, added, repo.Read(folder + "seed.sql"));
    }

    private static async Task<(int Code, string Output)> PsqlAsync(string docker, string container, string script)
    {
        var name = "mq_ref_" + Guid.NewGuid().ToString("N")[..8];
        Task<ProcessRunner.Result> Exec(string db, string sql) =>
            ProcessRunner.RunAsync(docker, ["exec", "-i", container, "psql", "-U", "postgres", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-d", db], Environment.CurrentDirectory, TimeSpan.FromMinutes(2), sql);
        await Exec("postgres", $"CREATE DATABASE {name};");
        try
        {
            var run = await Exec(name, script);
            return (run.ExitCode, run.Output);
        }
        finally
        {
            await Exec("postgres", $"DROP DATABASE IF EXISTS {name};");
        }
    }
}
