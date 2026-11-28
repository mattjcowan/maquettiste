using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Reference types in the example packs (reference-types-seeds-localization.md sections 1.6 and 2): the sql-ddl realizations
/// (lookup table, native) selected by the project's strategy through the pack's <c>strategyMap</c>, reconciled on every run by
/// seed.sql, and the csharp-dapper records, row classes and resource files. Goldens per strategy cover the three dialects. Since
/// 0.29.0 no column of a table the engine lays out holds codes: a designed column declares its own key or check.
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

    private const string FirstBatch = "0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b";

    /// <summary>Adds the reference type Batch, coded by uuid, with two rows, and the optional attribute Ingredient.batch.</summary>
    private static void AddUuidCodedType(PackRepo repo)
    {
        const string model = ".maquettiste/model/";
        repo.Write(model + "reference-types/batch.json",
            """{ "$schema": "../../.schema/v1/reference-type.json", "kind": "reference-type", "id": "01JRDW00000000000000000001", "name": "Batch", "code": { "id": "01JRDW00000000000000000002", "type": "uuid" }, "label": { "id": "01JRDW00000000000000000003", "length": 40 } }""" + "\n");
        repo.Write(model + "seeds/batch/batch.json",
            """{ "$schema": "../../../.schema/v1/seed.json", "kind": "seed", "id": "01JRDW00000000000000000010", "name": "Batch", "target": "01JRDW00000000000000000001", "columns": ["code", "label"], "rows": [ { "id": "01JRDW00000000000000000011", "values": ["0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b", "First"] }, { "id": "01JRDW00000000000000000012", "values": ["0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6c", "Second"] } ] }""" + "\n");
        repo.EditJson(model + "entities/ingredient.json", entity => entity["attributes"]!.AsArray().Add(
            JsonNode.Parse("""{ "id": "01JRDW00000000000000000020", "name": "batch", "type": { "ref": "01JRDW00000000000000000001" } }""")));
    }

    [Fact]
    public async Task A_uuid_code_keys_the_lookup_table_with_each_dialects_uuid_type()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        AddUuidCodedType(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        Assert.Contains("code uuid NOT NULL", repo.Read("db/main/seed.sql"), StringComparison.Ordinal);
        Assert.Contains("code uniqueidentifier NOT NULL", repo.Read("db/reporting/seed.sql"), StringComparison.Ordinal);
        Assert.Contains("code TEXT NOT NULL", repo.Read("db/local/seed.sql"), StringComparison.Ordinal);
        Assert.Contains("'" + FirstBatch + "'", repo.Read("db/main/seed.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_uuid_code_is_a_guid_in_csharp()
    {
        using var repo = PackRepo.ReferenceData("check");
        AddUuidCodedType(repo);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        var batch = repo.Read("src/Generated/ReferenceData/Batch.g.cs");
        Assert.Contains("public sealed record Batch(Guid Code, string Label);", batch, StringComparison.Ordinal);
        Assert.Contains("= new(new Guid(\"" + FirstBatch + "\"), \"First\");", batch, StringComparison.Ordinal);
        Assert.Contains("public static Batch? Find(Guid code)", batch, StringComparison.Ordinal);
        Assert.Contains("public Guid? Batch { get; set; }", repo.Read("src/Generated/Ingredient.g.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_typescript_sample_keeps_a_uuid_code_a_string_and_says_so()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        AddUuidCodedType(repo);
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "samples", "typescript-pack"), Path.Combine(repo.Repo.ModelRoot, "templates", "typescript"));
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"]!["allow"]!.AsArray().Add(JsonNode.Parse("""{ "path": "web" }"""));
            settings["packs"]!["typescript"] = JsonNode.Parse("""{ "output": "web" }""");
        });
        await repo.GenerateCleanlyAsync(packs: ["typescript"]);

        var batch = repo.Read("web/batch.ts");
        Assert.Contains("// The codes are UUIDs in canonical form (lowercase, hyphenated), held as strings.", batch, StringComparison.Ordinal);
        Assert.Contains("export type Batch = '" + FirstBatch + "' | ", batch, StringComparison.Ordinal);
        Assert.DoesNotContain("UUIDs", repo.Read("web/unit-of-measure.ts"), StringComparison.Ordinal);
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
        Assert.Contains("lookup table units_of_measure", repo.Read("db/main/seed.sql"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A designed table pantry_items in main whose unit column (string(8), not null) the entity PantryItem's binding maps to its attribute
    /// of UnitOfMeasure, with one row that holds the code kg (next-phase-2-design.md section 3.2).
    /// </summary>
    private static void PantryItems(PackRepo repo)
    {
        repo.Write(".maquettiste/model/databases/main/tables/pantry-items.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"01K9X3E0000000000000000001","name":"pantry_items","database":"{{PackRepo.MainDatabaseId}}","columns":[
              {"id":"01K9X3E0000000000000000002","name":"id","type":"int32","nullable":false},
              {"id":"01K9X3E0000000000000000003","name":"unit","type":"string","length":8,"nullable":false}],
             "primaryKey":{"columns":["01K9X3E0000000000000000002"]} }
            """);
        repo.Write(".maquettiste/model/entities/pantry-item.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"01K9X3E0000000000000000020","name":"PantryItem",
             "key":{"attributes":["01K9X3E0000000000000000021"]},
             "attributes":[{"id":"01K9X3E0000000000000000021","name":"id","type":"int32","required":true},
               {"id":"01K9X3E0000000000000000022","name":"unit","type":{"ref":"01JBM9S346Q3D25VT4F5V37E3S"},"required":true}],
             "bindings":[{"id":"01K9X3E0000000000000000023","database":"{{PackRepo.MainDatabaseId}}","source":"01K9X3E0000000000000000001",
               "fields":[{"attribute":"01K9X3E0000000000000000021","column":"01K9X3E0000000000000000002"},{"attribute":"01K9X3E0000000000000000022","column":"01K9X3E0000000000000000003"}]}]}
            """);
        repo.Write(".maquettiste/model/seeds/pantry-items/pantry-items.json", """
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01K9X3E0000000000000000010","name":"pantryItems","target":"01K9X3E0000000000000000001",
             "columns":["01K9X3E0000000000000000002","01K9X3E0000000000000000003"],"rows":[{"id":"01K9X3E0000000000000000011","values":[1,"kg"]}]}
            """);
    }

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("native")]
    public async Task A_designed_column_bound_to_a_reference_type_attribute_gets_nothing_from_the_binding(string strategy)
    {
        // 0.28.0: binding an entity never changes storage. Whatever the type's storage, pantry_items keeps the type its column states and
        // gets no key, CHECK or enum type the table does not declare.
        using var repo = PackRepo.ReferenceData(strategy);
        PantryItems(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var schema = repo.Read("db/main/schema.sql");
        Assert.Contains("unit varchar(8) NOT NULL", schema, StringComparison.Ordinal);
        var seed = repo.Read("db/main/seed.sql");
        Assert.DoesNotContain("pantry_items_unit_ref", seed, StringComparison.Ordinal);
        Assert.DoesNotContain("pantry_items.unit", seed, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- reference-type refactors (next-phase-2-design.md section 3.5)

    /// <summary>
    /// A refactor of the payment terms setup: the scripts that build main as the model was before it (the first migration, then
    /// seed.sql), the migration and seed.sql generated after it, and the id of the value map it recorded in main (null for none).
    /// </summary>
    private sealed record Refactor(string Before, string Migration, string Seed, string? FactId);

    /// <summary>
    /// Generates main with the setup (and <paramref name="setup"/>'s additions), applies the operation (or the edit) through a model
    /// store and generates again.
    /// </summary>
    private static async Task<Refactor> RefactorAsync(PackRepo repo, string? operation, bool collection = false, Func<PackRepo, Task>? edit = null,
        Func<PackRepo, Task>? setup = null)
    {
        await PaymentTerms.AddAsync(repo, collection);
        if (setup is not null)
            await setup(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var before = MigrationFiles.Read(repo, "main", 1) + repo.Read("db/main/seed.sql");
        string? fact = null;
        if (operation is not null)
            fact = (await PaymentTerms.ApplyAsync(repo, operation)).Facts!.SingleOrDefault(f => f.Database == PaymentTerms.MainId && f.Fact == "value-map")?.Id;
        if (edit is not null)
            await edit(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        return new Refactor(before, MigrationFiles.Read(repo, "main", 2), repo.Read("db/main/seed.sql"), fact);
    }

    [Fact]
    public async Task A_renamed_type_renames_its_lookup_table_and_enum_type()
    {
        foreach (var strategy in new[] { "lookup-table", "native" })
        {
            using var repo = PaymentTerms.Create(strategy);
            var refactor = await RefactorAsync(repo, $$"""{ "op": "rename-reference-type", "id": "{{PaymentTerms.PaymentTerm}}", "name": "PaymentCondition" }""");
            Assert.Contains("(value-map): renames reference type PaymentTerm to PaymentCondition.", refactor.Migration, StringComparison.Ordinal);
            Assert.Contains(strategy == "native" ? "ALTER TYPE payment_term_t RENAME TO payment_condition_t;" : "ALTER TABLE billing.payment_terms RENAME TO payment_conditions;",
                refactor.Migration, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Former_codes_become_obsolete_aliases_in_csharp()
    {
        // kg is renamed to kilogram and pinch retired into g: each former code is an obsolete field holding the row that replaced it.
        // g renamed to g_ leaves the former code g, whose member G the row g_ has already, so it gets no field.
        using var repo = PackRepo.ReferenceData("lookup-table");
        const string unit = "01JBM9S346Q3D25VT4F5V37E3S";
        await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "rename-code", "id": "{{unit}}", "code": "kg", "to": "kilogram" }""");
        await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "retire-code", "id": "{{unit}}", "code": "pinch", "to": "g" }""");
        await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "rename-code", "id": "{{unit}}", "code": "g", "to": "g_" }""");
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        var text = repo.Read("src/Generated/ReferenceData/UnitOfMeasure.g.cs");
        Assert.Contains("""
                /// <summary>The former code kg, now the row kilogram.</summary>
                [Obsolete("kg is a former code of UnitOfMeasure; use Kilogram.")]
                public static readonly UnitOfMeasure Kg = Kilogram;
            """, text, StringComparison.Ordinal);
        Assert.Contains("""
                [Obsolete("pinch is a former code of UnitOfMeasure; use G.")]
                public static readonly UnitOfMeasure Pinch = G;
            """, text, StringComparison.Ordinal);
        Assert.DoesNotContain("former code g,", text, StringComparison.Ordinal);
        Assert.Contains("public static IReadOnlyList<UnitOfMeasure> All { get; } = [Kilogram, G];", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kg\" =>", text, StringComparison.Ordinal);
    }

    /// <summary>The scripts of one database: schema, first migration, seed, then the seed after adding the code "cup" and after
    /// retiring it again.</summary>
    internal sealed record Scripts(string Schema, string Migration, string Seed, string SeedAdded, string SeedRetired)
    {
        /// <summary>Schema, seed twice, the seed with the new code (a row uses it, then stops using it), the seed without it.</summary>
        public string Chain(string query) =>
            Schema + Seed + Seed + SeedAdded
            + "\nUPDATE ingredients SET default_unit = 'cup' WHERE sku = 'salt';\nUPDATE ingredients SET default_unit = 'g' WHERE sku = 'salt';\n"
            + query + SeedRetired + query;
    }

    internal static async Task<Scripts> ScriptsAsync(string strategy, string database)
    {
        using var repo = PackRepo.ReferenceData(strategy);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var folder = "db/" + database + "/";
        var (schema, migration, seed) = (repo.Read(folder + "schema.sql"), MigrationFiles.Read(repo, database, 1), repo.Read(folder + "seed.sql"));
        repo.EditJson(UnitSeed, s => s["rows"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01JRDX00000000000000000001", "values": ["cup", "Cup", 240] }""")));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var added = repo.Read(folder + "seed.sql");
        Assert.Contains("'cup'", added, StringComparison.Ordinal);
        repo.EditJson(UnitSeed, s => s["rows"]!.AsArray().RemoveAt(3));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        return new Scripts(schema, migration, seed, added, repo.Read(folder + "seed.sql"));
    }

    internal static async Task<(int Code, string Output)> PsqlAsync(string docker, string container, string script)
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
