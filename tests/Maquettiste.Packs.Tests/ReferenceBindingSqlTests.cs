using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// sql-ddl with a reference type bound to a table file of a database (2026-10-11): the pack realizes the type as "table" there (no
/// lookup table, CHECK or enum type), writes the type's rows into the bound table with its other seed rows (by the seed's apply),
/// and points every column and junction table holding the type's codes at the bound table's code column with a foreign key, or
/// with none when the code column alone is not a key of the table. A binding added to a type stored as "native" on PostgreSQL
/// retypes its columns in the next migration. On SQLite the scripts run, twice, when sqlite3 is on the PATH.
/// </summary>
public sealed class ReferenceBindingSqlTests
{
    private const string UnitType = ".maquettiste/model/reference-types/unit-of-measure.json";
    private const string Factor = "01JBNPZX45HY43KWJRP1XPA7Z3", Symbol = "01JBDJ8FSSZ5AWSH8VHTPRE95B";

    private static readonly (string Folder, string Id, string IdBase)[] Databases =
    [
        ("main", PackRepo.MainDatabaseId, "01K7RBS000000000000000001"),
        ("reporting", "01JRDD00000000000000000002", "01K7RBS000000000000000002"),
        ("local", "01JRDD00000000000000000003", "01K7RBS000000000000000003"),
    ];

    /// <summary>A units table in each database, bound to UnitOfMeasure: code, name, factor and symbol columns, keyed by code.</summary>
    private static void Bind(PackRepo repo)
    {
        var bindings = new JsonArray();
        foreach (var (folder, database, idBase) in Databases)
        {
            string Id(int n) => idBase + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            repo.Write($".maquettiste/model/databases/{folder}/tables/units.json", $$"""
                {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{Id(1)}}","name":"units","database":"{{database}}","columns":[
                  {"id":"{{Id(2)}}","name":"code","type":"string","length":8,"nullable":false},
                  {"id":"{{Id(3)}}","name":"name","type":"string","length":64,"nullable":false},
                  {"id":"{{Id(4)}}","name":"factor","type":"decimal","precision":18,"scale":6,"nullable":false},
                  {"id":"{{Id(5)}}","name":"symbol","type":"string","length":8}],
                 "primaryKey":{"columns":["{{Id(2)}}"]} }
                """);
            bindings.Add(JsonNode.Parse($$"""
                {"id":"{{Id(6)}}","database":"{{database}}","table":"{{Id(1)}}",
                 "fields":[{"attribute":"code","column":"code"},{"attribute":"label","column":"name"},{"attribute":"{{Factor}}","column":"factor"},{"attribute":"{{Symbol}}","column":"symbol"}]}
                """));
        }

        repo.EditJson(UnitType, type => type["bindings"] = bindings);
    }

    [Fact]
    public async Task A_bound_type_gets_no_lookup_table_its_rows_go_into_the_bound_table_and_its_columns_point_there()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        Bind(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var main = repo.Read("db/main/seed.sql");
        Assert.DoesNotContain("units_of_measure", main, StringComparison.Ordinal);
        Assert.Contains("-- ingredients.default_unit (UnitOfMeasure, rows in table units).", main, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (default_unit) REFERENCES public.units (code);", main, StringComparison.Ordinal);
        Assert.Contains("REFERENCES public.units (code)", main[main.IndexOf("ingredients_pack_units", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("-- 3 rows into units, inserted when missing.\nINSERT INTO public.units (code, name, factor, symbol) VALUES\n    ('kg', 'Kilogram', 1000, 'kg'),", main, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (code) DO NOTHING;", main, StringComparison.Ordinal);
        // The rows go in, then the foreign keys that need them (a column may already hold codes), then the entity seeds.
        var (rows, key, entities) = (main.IndexOf("INSERT INTO public.units", StringComparison.Ordinal),
            main.IndexOf("ADD CONSTRAINT fk_ingredients_default_unit_ref", StringComparison.Ordinal), main.IndexOf("INSERT INTO public.ingredients", StringComparison.Ordinal));
        Assert.True(rows < key && key < entities, main);
        // Allergen keeps its lookup table.
        Assert.Contains("allergens", main, StringComparison.Ordinal);

        var reporting = repo.Read("db/reporting/seed.sql");
        Assert.DoesNotContain("units_of_measure", reporting, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (default_unit) REFERENCES dbo.units (code);", reporting, StringComparison.Ordinal);
        Assert.Contains("MERGE INTO dbo.units AS x", reporting, StringComparison.Ordinal);

        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
            return;
        var folder = repo.PathOf("db/local");
        var (schema, seed) = (File.ReadAllText(Path.Combine(folder, "schema.sql")), File.ReadAllText(Path.Combine(folder, "seed.sql")));
        Assert.DoesNotContain("units_of_measure", seed, StringComparison.Ordinal);
        var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1),
            schema + seed + seed + "\nSELECT 'units=' || count(*) FROM units;\n");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("units=3", run.Output, StringComparison.Ordinal);
        var bad = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1),
            schema + seed + "\nUPDATE ingredients SET default_unit = 'nope' WHERE sku = 'salt';\n");
        Assert.NotEqual(0, bad.ExitCode);
        Assert.Contains("fk_ingredients_default_unit_ref", bad.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_converging_seed_of_a_bound_type_upserts_its_rows()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        Bind(repo);
        repo.EditJson(".maquettiste/model/seeds/unit-of-measure/unit-of-measure.json", seed => seed["apply"] = "converge");
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var main = repo.Read("db/main/seed.sql");
        Assert.Contains("-- 3 rows into units, kept as the model has them.", main, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, factor = EXCLUDED.factor, symbol = EXCLUDED.symbol;", main, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_keyed_by_the_code_and_a_constant_column_gets_the_rows_but_no_foreign_key_from_a_code_column()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        repo.Write(".maquettiste/model/databases/main/tables/codes.json", """
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"01K7RBS0000000000000000041","name":"codes","database":"01JRDD00000000000000000001","columns":[
              {"id":"01K7RBS0000000000000000042","name":"list","type":"string","length":20,"nullable":false},
              {"id":"01K7RBS0000000000000000043","name":"code","type":"string","length":8,"nullable":false},
              {"id":"01K7RBS0000000000000000044","name":"label","type":"string","length":64,"nullable":false}],
             "primaryKey":{"columns":["01K7RBS0000000000000000042","01K7RBS0000000000000000043"]} }
            """);
        repo.EditJson(UnitType, type => type["bindings"] = JsonNode.Parse("""
            [{"id":"01K7RBS0000000000000000045","database":"01JRDD00000000000000000001","table":"01K7RBS0000000000000000041",
              "fields":[{"attribute":"code","column":"code"},{"attribute":"label","column":"label"}],
              "constants":[{"column":"list","value":"unit-of-measure"}]}]
            """));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var main = repo.Read("db/main/seed.sql");
        Assert.Contains("-- The key of codes is not its code column alone, so a foreign key from ingredients.default_unit cannot name a row of UnitOfMeasure; it gets none.", main, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES public.codes", main, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO public.codes (list, code, label) VALUES\n    ('unit-of-measure', 'kg', 'Kilogram'),", main, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (list, code) DO NOTHING;", main, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binding_a_native_type_on_postgresql_retypes_its_columns_and_drops_the_enum_type_from_the_scripts()
    {
        using var repo = PackRepo.ReferenceData("native");
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.Contains("default_unit unit_of_measure_t", repo.Read("db/main/schema.sql"), StringComparison.Ordinal);
        var (nativeSchema, nativeSeed) = (repo.Read("db/main/schema.sql"), repo.Read("db/main/seed.sql"));

        Bind(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var schema = repo.Read("db/main/schema.sql");
        Assert.DoesNotContain("unit_of_measure_t", schema, StringComparison.Ordinal);
        Assert.Contains("default_unit varchar(8)", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("unit_of_measure_t", repo.Read("db/main/seed.sql"), StringComparison.Ordinal);
        var migration = MigrationFiles.Read(repo, "main", 2);
        Assert.Contains("ALTER COLUMN default_unit TYPE varchar(8) USING default_unit::varchar(8);", migration, StringComparison.Ordinal);

        // On PostgreSQL: the database as the native scripts left it, then the migration and the bound seed, twice.
        var container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_POSTGRES_CONTAINER");
        var docker = ProcessRunner.FindOnPath("docker");
        if (string.IsNullOrEmpty(container) || docker is null)
            return;
        var seed = repo.Read("db/main/seed.sql");
        var (code, output) = await ReferenceDataTests.PsqlAsync(docker, container,
            nativeSchema + nativeSeed + migration + seed + seed + "\nSELECT 'units=' || count(*) FROM units;\nSELECT 'salt=' || default_unit FROM ingredients WHERE sku = 'salt';\n");
        Assert.True(code == 0, output);
        Assert.Contains("units=3", output, StringComparison.Ordinal);
        Assert.Contains("salt=g", output, StringComparison.Ordinal);
    }

    /// <summary>A bound repo's scripts of one database: schema, first migration, seed.</summary>
    internal static async Task<(string Schema, string Migration, string Seed)> ScriptsAsync(string database)
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        Bind(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var folder = "db/" + database + "/";
        return (repo.Read(folder + "schema.sql"), MigrationFiles.Read(repo, database, 1), repo.Read(folder + "seed.sql"));
    }

    [Fact]
    public async Task PostgreSQL_runs_the_scripts_of_a_bound_type_twice_and_refuses_a_code_the_table_does_not_hold()
    {
        var container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_POSTGRES_CONTAINER");
        var docker = ProcessRunner.FindOnPath("docker");
        if (string.IsNullOrEmpty(container) || docker is null)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_POSTGRES_CONTAINER to run the scripts on PostgreSQL.");
            return;
        }

        var (schema, migration, seed) = await ScriptsAsync("main");
        var (code, output) = await ReferenceDataTests.PsqlAsync(docker, container, schema + seed + seed + "\nSELECT 'units=' || count(*) FROM units;\n");
        Assert.True(code == 0, output);
        Assert.Contains("units=3", output, StringComparison.Ordinal);
        (code, output) = await ReferenceDataTests.PsqlAsync(docker, container, migration + seed + seed);
        Assert.True(code == 0, output);
        (code, output) = await ReferenceDataTests.PsqlAsync(docker, container, schema + seed + "\nUPDATE ingredients SET default_unit = 'nope' WHERE sku = 'salt';\n");
        Assert.NotEqual(0, code);
        Assert.Contains("fk_ingredients_default_unit_ref", output, StringComparison.Ordinal);
    }
}

/// <summary>The scripts of a bound reference type on SQL Server (see <see cref="ReferenceBindingSqlTests"/>).</summary>
public sealed class ReferenceBindingSqlServerTests(SqlServerContainer server) : IClassFixture<SqlServerContainer>
{
    [Fact]
    public async Task Sql_server_runs_the_scripts_of_a_bound_type_twice_and_refuses_a_code_the_table_does_not_hold()
    {
        if (server.SkipReason is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        var (schema, migration, seed) = await ReferenceBindingSqlTests.ScriptsAsync("reporting");
        var (code, output) = await server.RunAsync(schema, seed, seed, "SELECT 'units=' + CAST(COUNT(*) AS varchar(10)) FROM dbo.units;");
        Assert.True(code == 0, output);
        Assert.Contains("units=3", output, StringComparison.Ordinal);
        (code, output) = await server.RunAsync(migration, seed, seed);
        Assert.True(code == 0, output);
        (code, output) = await server.RunAsync(schema, seed, "UPDATE dbo.ingredients SET default_unit = N'nope' WHERE sku = N'salt';");
        Assert.NotEqual(0, code);
        Assert.Contains("fk_ingredients_default_unit_ref", output, StringComparison.Ordinal);
    }
}
