using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// sql-ddl with a reference type bound to a table file of a database (2026-10-11): the pack realizes the type as "table" there (no
/// lookup table or enum type) and writes the type's rows into the bound table with its other seed rows (by the seed's apply). A
/// column that holds the codes declares its own foreign key. On SQLite the scripts run, twice, when sqlite3 is on the PATH.
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
    public async Task A_bound_type_gets_no_lookup_table_and_its_rows_go_into_the_bound_table()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        Bind(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var main = repo.Read("db/main/seed.sql");
        Assert.DoesNotContain("units_of_measure", main, StringComparison.Ordinal);
        Assert.Contains("-- 3 rows into units, inserted when missing.\nINSERT INTO public.units (code, name, factor, symbol) VALUES\n    ('kg', 'Kilogram', 1000, 'kg'),", main, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (code) DO NOTHING;", main, StringComparison.Ordinal);
        // Allergen keeps its lookup table.
        Assert.Contains("allergens", main, StringComparison.Ordinal);

        var reporting = repo.Read("db/reporting/seed.sql");
        Assert.DoesNotContain("units_of_measure", reporting, StringComparison.Ordinal);
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
    public async Task A_table_keyed_by_the_code_and_a_constant_column_gets_the_rows()
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
        Assert.DoesNotContain("REFERENCES public.codes", main, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO public.codes (list, code, label) VALUES\n    ('unit-of-measure', 'kg', 'Kilogram'),", main, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (list, code) DO NOTHING;", main, StringComparison.Ordinal);
    }


    private const string PantryItems = "01K7RBS0000000000000000051", PantryUnit = "01K7RBS0000000000000000053";

    /// <summary>
    /// A designed table pantry_items in main whose unit column (string(8), not null) the entity PantryItem's binding maps to its unit
    /// attribute of UnitOfMeasure, so the column holds its codes, with the given foreign keys (next-phase-2-design.md section 3.2).
    /// </summary>
    private static void PantryItemsTable(PackRepo repo, string foreignKeys = "[]")
    {
        repo.Write(".maquettiste/model/databases/main/tables/pantry-items.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{PantryItems}}","name":"pantry_items","database":"{{PackRepo.MainDatabaseId}}","columns":[
              {"id":"01K7RBS0000000000000000052","name":"id","type":"int32","nullable":false},
              {"id":"{{PantryUnit}}","name":"unit","type":"string","length":8,"nullable":false}],
             "primaryKey":{"columns":["01K7RBS0000000000000000052"]},"foreignKeys":{{foreignKeys}} }
            """);
        repo.Write(".maquettiste/model/entities/pantry-item.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"01K7RBS0000000000000000055","name":"PantryItem",
             "key":{"attributes":["01K7RBS0000000000000000056"]},
             "attributes":[{"id":"01K7RBS0000000000000000056","name":"id","type":"int32","required":true},
               {"id":"01K7RBS0000000000000000057","name":"unit","type":{"ref":"01JBM9S346Q3D25VT4F5V37E3S"},"required":true}],
             "bindings":[{"id":"01K7RBS0000000000000000058","database":"{{PackRepo.MainDatabaseId}}","source":"{{PantryItems}}",
               "fields":[{"attribute":"01K7RBS0000000000000000056","column":"01K7RBS0000000000000000052"},{"attribute":"01K7RBS0000000000000000057","column":"{{PantryUnit}}"}]}]}
            """);
    }

    [Fact]
    public async Task A_designed_column_bound_to_a_reference_type_attribute_gets_no_key_from_the_binding()
    {
        // 0.28.0: binding an entity never changes storage. A column of a table file that a binding maps to an attribute of a bound
        // reference type keeps the type it states and gets nothing the table does not declare: no fk_<table>_<column>_ref, no CHECK.
        using var repo = PackRepo.ReferenceData("lookup-table");
        Bind(repo);
        PantryItemsTable(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        Assert.Contains("unit varchar(8) NOT NULL", repo.Read("db/main/schema.sql"), StringComparison.Ordinal);
        var main = repo.Read("db/main/seed.sql");
        Assert.DoesNotContain("pantry_items", main, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bound_column_in_a_declared_key_gets_no_key_of_its_own()
    {
        // When pantry_items declares a foreign key from unit into the bound table, that key is the link: schema.sql holds it, and
        // seed.sql writes no fk_pantry_items_unit_ref for the column.
        using var repo = PackRepo.ReferenceData("lookup-table");
        Bind(repo);
        PantryItemsTable(repo, $$"""
            [{"id":"01K7RBS0000000000000000054","name":"fk_pantry_unit","columns":["{{PantryUnit}}"],"referencesTable":"01K7RBS0000000000000000011"}]
            """);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        Assert.Contains("fk_pantry_unit", repo.Read("db/main/schema.sql"), StringComparison.Ordinal);
        var main = repo.Read("db/main/seed.sql");
        Assert.DoesNotContain("pantry_items", main, StringComparison.Ordinal);
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
    public async Task PostgreSQL_runs_the_scripts_of_a_bound_type_twice()
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
    }
}

/// <summary>The scripts of a bound reference type on SQL Server (see <see cref="ReferenceBindingSqlTests"/>).</summary>
public sealed class ReferenceBindingSqlServerTests(SqlServerContainer server) : IClassFixture<SqlServerContainer>
{
    [Fact]
    public async Task Sql_server_runs_the_scripts_of_a_bound_type_twice()
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
    }
}
