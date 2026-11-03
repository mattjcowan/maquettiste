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
    public async Task A_uuid_code_is_checked_as_quoted_literals_and_is_a_guid_in_csharp()
    {
        using var repo = PackRepo.ReferenceData("check");
        AddUuidCodedType(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl", "csharp-dapper"]);

        var main = repo.Read("db/main/schema.sql") + repo.Read("db/main/seed.sql");
        Assert.Contains("batch uuid", main, StringComparison.Ordinal);
        Assert.Contains("CHECK (batch IN ('0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b', '0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6c'))", main, StringComparison.Ordinal);
        var reporting = repo.Read("db/reporting/schema.sql") + repo.Read("db/reporting/seed.sql");
        Assert.Contains("batch uniqueidentifier", reporting, StringComparison.Ordinal);
        Assert.Contains("N'" + FirstBatch + "'", reporting, StringComparison.Ordinal);
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

    /// <summary>
    /// A designed table pantry_items in main whose unit column (string(8), not null) is typed by UnitOfMeasure, with one row that
    /// holds the code kg (next-phase-2-design.md section 3.2).
    /// </summary>
    private static void PantryItems(PackRepo repo)
    {
        repo.Write(".maquettiste/model/databases/main/tables/pantry-items.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"01K9X3E0000000000000000001","name":"pantry_items","database":"{{PackRepo.MainDatabaseId}}","columns":[
              {"id":"01K9X3E0000000000000000002","name":"id","type":"int32","nullable":false},
              {"id":"01K9X3E0000000000000000003","name":"unit","type":"string","typeRef":{"ref":"01JBM9S346Q3D25VT4F5V37E3S"},"length":8,"nullable":false}],
             "primaryKey":{"columns":["01K9X3E0000000000000000002"]} }
            """);
        repo.Write(".maquettiste/model/seeds/pantry-items/pantry-items.json", """
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01K9X3E0000000000000000010","name":"pantryItems","target":"01K9X3E0000000000000000001",
             "columns":["01K9X3E0000000000000000002","01K9X3E0000000000000000003"],"rows":[{"id":"01K9X3E0000000000000000011","values":[1,"kg"]}]}
            """);
    }

    /// <summary>
    /// Runs schema.sql, then seed.sql twice, on PostgreSQL, then shows that pantry_items.unit refuses a value that is not a code;
    /// skips the run (after the caller's text checks) without a PostgreSQL container.
    /// </summary>
    private static async Task RunPantryItemsOnPostgresAsync(PackRepo repo, string constraint)
    {
        var container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_POSTGRES_CONTAINER");
        var docker = ProcessRunner.FindOnPath("docker");
        if (string.IsNullOrEmpty(container) || docker is null)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_POSTGRES_CONTAINER to run the typed column's scripts on PostgreSQL.");
            return;
        }

        var (schema, seed) = (repo.Read("db/main/schema.sql"), repo.Read("db/main/seed.sql"));
        var (code, output) = await PsqlAsync(docker, container, schema + seed + seed + "\nSELECT 'pantry=' || count(*) FROM pantry_items WHERE unit = 'kg';\n");
        Assert.True(code == 0, output);
        Assert.Contains("pantry=1", output, StringComparison.Ordinal);
        (code, output) = await PsqlAsync(docker, container, schema + seed + "\nUPDATE pantry_items SET unit = 'nope' WHERE id = 1;\n");
        Assert.NotEqual(0, code);
        Assert.Contains(constraint, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typed_designed_column_on_a_lookup_type_gets_its_key()
    {
        // A column of a table file typed by a reference type stored as a lookup table gets the key a projected column gets, into the
        // lookup table's code, and PostgreSQL refuses a value that is not a code.
        using var repo = PackRepo.ReferenceData("lookup-table");
        PantryItems(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        Assert.Contains("unit varchar(8) NOT NULL", repo.Read("db/main/schema.sql"), StringComparison.Ordinal);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("-- pantry_items.unit (UnitOfMeasure, lookup-table).", seed, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE public.pantry_items ADD CONSTRAINT fk_pantry_items_unit_ref FOREIGN KEY (unit) REFERENCES public.units_of_measure (code);", seed, StringComparison.Ordinal);
        await RunPantryItemsOnPostgresAsync(repo, "fk_pantry_items_unit_ref");
    }

    [Fact]
    public async Task A_typed_designed_column_on_a_native_type_keeps_its_type_and_gets_a_check()
    {
        // On PostgreSQL a type stored as "native" types projected columns by its enum type; a column of a table file states its own
        // type, so it keeps varchar(8) and gets the CHECK the other dialects get, while the enum type still serves the projected columns.
        using var repo = PackRepo.ReferenceData("native");
        PantryItems(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var schema = repo.Read("db/main/schema.sql");
        Assert.Contains("unit varchar(8) NOT NULL", schema, StringComparison.Ordinal);
        Assert.Contains("default_unit unit_of_measure_t", schema, StringComparison.Ordinal);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("-- UnitOfMeasure is stored as \"native\"; pantry_items.unit states its own type, so it gets a CHECK.", seed, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE public.pantry_items ADD CONSTRAINT ck_pantry_items_unit_ref CHECK (unit IN ('kg', 'g', 'pinch'));", seed, StringComparison.Ordinal);
        Assert.DoesNotContain("fk_pantry_items_unit_ref FOREIGN KEY", seed, StringComparison.Ordinal);
        await RunPantryItemsOnPostgresAsync(repo, "ck_pantry_items_unit_ref");
    }

    // ---------------------------------------------------------------- reference-type refactors (next-phase-2-design.md section 3.5)

    private const string RefactorSkip = "Set MAQUETTISTE_TEST_POSTGRES_CONTAINER to run the value-map steps on PostgreSQL.";

    private static string RenameNet30 => $$"""{ "op": "rename-code", "id": "{{PaymentTerms.PaymentTerm}}", "code": "net30", "to": "net-30" }""";

    private static string RetireDue => $$"""{ "op": "retire-code", "id": "{{PaymentTerms.PaymentTerm}}", "code": "due", "to": "net60" }""";

    private static string MergeCreditTerms(string codes) =>
        $$"""{ "op": "merge-reference-types", "id": "{{PaymentTerms.CreditTerm}}", "target": "{{PaymentTerms.PaymentTerm}}"{{codes}} }""";

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

    /// <summary>
    /// Rows that store codes: customers Ada (credit term n90) and Bob (net30), invoices INV-1 (net30), INV-2 (due) and INV-3 (net60),
    /// and a note on Ada (term n90); with a collection, Ada accepts net30, due and net60 and Bob accepts due.
    /// </summary>
    private static string TermRows(string strategy, bool collection)
    {
        var rows = """
            INSERT INTO billing.customers (id, name, email, created_at, credit_term) VALUES
                ('00000000-0000-0000-0000-000000000001', 'Ada', 'ada@example.com', now(), 'n90'),
                ('00000000-0000-0000-0000-000000000002', 'Bob', 'bob@example.com', now(), 'net30');
            INSERT INTO billing.invoices (id, number, issued_on, created_at, customer_id, payment_term) VALUES
                ('00000000-0000-0000-0000-000000000011', 'INV-1', DATE '2026-01-31', now(), '00000000-0000-0000-0000-000000000001', 'net30'),
                ('00000000-0000-0000-0000-000000000012', 'INV-2', DATE '2026-01-31', now(), '00000000-0000-0000-0000-000000000001', 'due'),
                ('00000000-0000-0000-0000-000000000013', 'INV-3', DATE '2026-01-31', now(), '00000000-0000-0000-0000-000000000002', 'net60');
            INSERT INTO billing.notes (id, entity_type, entity_id, body, term) VALUES
                ('00000000-0000-0000-0000-000000000021', 'customer', '00000000-0000-0000-0000-000000000001', 'Pays late', 'n90');

            """;
        if (!collection)
            return rows;
        return rows + (strategy == "native"
            ? """
              UPDATE billing.customers SET accepted_terms = ARRAY['net30', 'due', 'net60']::payment_term_t[] WHERE name = 'Ada';
              UPDATE billing.customers SET accepted_terms = ARRAY['due']::payment_term_t[] WHERE name = 'Bob';

              """
            : """
              INSERT INTO billing.customers_accepted_terms (customer_id, accepted_term_code) VALUES
                  ('00000000-0000-0000-0000-000000000001', 'net30'), ('00000000-0000-0000-0000-000000000001', 'due'),
                  ('00000000-0000-0000-0000-000000000001', 'net60'), ('00000000-0000-0000-0000-000000000002', 'due');

              """);
    }

    /// <summary>What the database holds: invoice terms, customer credit terms, the note's term and the fact's ledger rows.</summary>
    private static string TermQuery(string? factId) =>
        "SELECT 'invoice ' || number || ' ' || coalesce(payment_term::text, '-') FROM billing.invoices ORDER BY number;\n"
        + "SELECT 'customer ' || name || ' ' || coalesce(credit_term::text, '-') FROM billing.customers ORDER BY name;\n"
        + "SELECT 'note ' || coalesce(term, '-') FROM billing.notes;\n"
        + (factId is null ? "" : $"SELECT 'ledger ' || count(*) FROM migration_ledger.applied_fact WHERE fact_id = '{factId}';\n");

    /// <summary>
    /// Builds main as the model was before the refactor and stores the rows, then runs the migration and seed.sql twice, the query
    /// after each run; returns the query's output after the first run and asserts that the second run changed nothing.
    /// </summary>
    private static async Task<string> RunTwiceAsync(DbServer db, string label, Refactor refactor, string rows, string query)
    {
        var output = await db.RunAsync(label, refactor.Before + rows,
            "SET client_min_messages = warning;\n" + refactor.Migration + refactor.Seed + "SELECT 'first run';\n" + query + refactor.Migration + refactor.Seed + "SELECT 'second run';\n" + query);
        var first = output[(output.IndexOf("first run", StringComparison.Ordinal) + "first run".Length)..output.IndexOf("second run", StringComparison.Ordinal)].Trim();
        var second = output[(output.IndexOf("second run", StringComparison.Ordinal) + "second run".Length)..].Trim();
        Assert.Equal(first, second);
        return first;
    }

    [Fact]
    public async Task A_renamed_code_moves_stored_values_under_lookup_table()
    {
        using var repo = PaymentTerms.Create("lookup-table");
        var refactor = await RefactorAsync(repo, RenameNet30);

        // The data step comes before the schema statements: the new default follows it.
        AssertBefore(refactor.Migration, "UPDATE billing.invoices SET payment_term = CASE payment_term WHEN 'net30' THEN 'net-30' ELSE payment_term END WHERE payment_term IN ('net30');",
            "ALTER TABLE billing.invoices ALTER COLUMN payment_term SET DEFAULT 'net-30';");
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await RunTwiceAsync(db, "rename_lookup", refactor, TermRows("lookup-table", false),
            TermQuery(refactor.FactId) + "SELECT 'codes ' || string_agg(code, ',' ORDER BY code) FROM billing.payment_terms;\n");
        Assert.Contains("invoice INV-1 net-30", output, StringComparison.Ordinal);
        Assert.DoesNotContain("net30", output.Replace("customer Bob net30", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("codes due,net-30,net60", output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_renamed_code_moves_stored_values_under_check()
    {
        using var repo = PaymentTerms.Create("check");
        var refactor = await RefactorAsync(repo, RenameNet30);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await RunTwiceAsync(db, "rename_check", refactor, TermRows("check", false),
            TermQuery(refactor.FactId) + "SELECT 'check ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_invoices_payment_term_ref';\n");
        Assert.Contains("invoice INV-1 net-30", output, StringComparison.Ordinal);
        Assert.Contains("check CHECK (((payment_term)::text = ANY ((ARRAY['net-30'::character varying, 'net60'::character varying, 'due'::character varying])::text[])))",
            output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_renamed_code_is_renamed_in_place_under_native()
    {
        using var repo = PaymentTerms.Create("native");
        var refactor = await RefactorAsync(repo, RenameNet30);

        Assert.Contains("THEN ALTER TYPE payment_term_t RENAME VALUE 'net30' TO 'net-30'; END IF;", refactor.Migration, StringComparison.Ordinal);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        // The label keeps its place, so the stored values follow it without an UPDATE; seed.sql adds nothing and stops at nothing.
        var output = await RunTwiceAsync(db, "rename_native", refactor, TermRows("native", false),
            TermQuery(refactor.FactId) + "SELECT 'labels ' || array_to_string(enum_range(NULL::payment_term_t), ',');\n");
        Assert.Contains("invoice INV-1 net-30", output, StringComparison.Ordinal);
        Assert.Contains("labels net-30,net60,due", output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retired_code_moves_into_an_existing_code()
    {
        using var repo = PaymentTerms.Create("lookup-table");
        var refactor = await RefactorAsync(repo, RetireDue);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        // INV-2 stored due: it holds net60 now, and seed.sql's retire step deletes the due row no column stores any more.
        var output = await RunTwiceAsync(db, "retire_lookup", refactor, TermRows("lookup-table", false),
            TermQuery(refactor.FactId) + "SELECT 'codes ' || string_agg(code, ',' ORDER BY code) FROM billing.payment_terms;\n");
        Assert.Contains("invoice INV-2 net60", output, StringComparison.Ordinal);
        Assert.Contains("codes net30,net60", output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("native")]
    public async Task A_merge_maps_stored_codes_under_lookup_table_and_native(string strategy)
    {
        using var repo = PaymentTerms.Create(strategy);
        var refactor = await RefactorAsync(repo, MergeCreditTerms(""", "codes": { "n90": "net60" }"""));
        Assert.Contains("(value-map): maps CreditTerm codes n90 -> net60 into PaymentTerm.", refactor.Migration, StringComparison.Ordinal);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        // Ada stored n90, which maps to net60; Bob's net30 is a code of both types. Under native the column takes PaymentTerm's enum
        // type; under lookup-table its foreign key points at PaymentTerm's lookup table.
        var output = await RunTwiceAsync(db, "merge_" + strategy.Replace('-', '_'), refactor, TermRows(strategy, false), TermQuery(refactor.FactId)
            + "SELECT 'type ' || format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'billing.customers'::regclass AND attname = 'credit_term';\n"
            + "SELECT 'references ' || confrelid::regclass FROM pg_constraint WHERE conname = 'fk_customers_credit_term_ref';\n");
        Assert.Contains("customer Ada net60", output, StringComparison.Ordinal);
        Assert.Contains("customer Bob net30", output, StringComparison.Ordinal);
        Assert.Contains("note net60", output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
        if (strategy == "native")
            Assert.Contains("type payment_term_t", output, StringComparison.Ordinal);
        else
            Assert.Contains("references billing.payment_terms", output, StringComparison.Ordinal);
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
            var db = DbServer.Postgres();
            if (db is null)
                continue;

            var output = await RunTwiceAsync(db, "rename_type_" + strategy.Replace('-', '_'), refactor, TermRows(strategy, false), TermQuery(refactor.FactId)
                + "SELECT 'old ' || coalesce(to_regclass('billing.payment_terms')::text, '-') || ' ' || coalesce(to_regtype('payment_term_t')::text, '-');\n"
                + (strategy == "native"
                    ? "SELECT 'type ' || format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'billing.invoices'::regclass AND attname = 'payment_term';\n"
                    : "SELECT 'codes ' || string_agg(code, ',' ORDER BY code) FROM billing.payment_conditions;\n"
                      + "SELECT 'references ' || confrelid::regclass FROM pg_constraint WHERE conname = 'fk_invoices_payment_term_ref';\n"));
            Assert.Contains("invoice INV-2 due", output, StringComparison.Ordinal);
            Assert.Contains("old - -", output, StringComparison.Ordinal);
            Assert.Contains("ledger 1", output, StringComparison.Ordinal);
            if (strategy == "native")
            {
                Assert.Contains("type payment_condition_t", output, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("codes due,net30,net60", output, StringComparison.Ordinal);
                Assert.Contains("references billing.payment_conditions", output, StringComparison.Ordinal);
            }
        }

        if (DbServer.Postgres() is null)
            Assert.Skip(RefactorSkip);
    }

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("check")]
    [InlineData("native")]
    public async Task A_value_map_step_runs_twice_with_no_change(string strategy)
    {
        // A retire in each realization, with a collection where the realization stores one: the second run of the migration and
        // seed.sql finds nothing to map and leaves every row as the first run left it (RunTwiceAsync compares them).
        var collection = strategy != "check";
        using var repo = PaymentTerms.Create(strategy);
        var refactor = await RefactorAsync(repo, RetireDue, collection);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await RunTwiceAsync(db, "twice_" + strategy.Replace('-', '_'), refactor, TermRows(strategy, collection), TermQuery(refactor.FactId));
        Assert.Contains("invoice INV-2 net60", output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_native_map_to_a_value_the_enum_lacks_stops_with_the_codes_named()
    {
        // Merging CreditTerm into PaymentTerm with no codes moves n90 into PaymentTerm as a row; payment_term_t exists and lacks it, and
        // PostgreSQL cannot use a value added in the migration's own transaction, so the step stops and names the code.
        using var repo = PaymentTerms.Create("native");
        var refactor = await RefactorAsync(repo, MergeCreditTerms(""));
        Assert.Contains("(value-map): merges CreditTerm into PaymentTerm.", refactor.Migration, StringComparison.Ordinal);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await db.RunAsync("native_lacks", refactor.Before + TermRows("native", false),
            "\\set ON_ERROR_STOP off\n" + refactor.Migration + "\\set ON_ERROR_STOP on\n"
            + "SELECT 'type ' || format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'billing.customers'::regclass AND attname = 'credit_term';\n");
        Assert.Contains($"migration fact {refactor.FactId}: the enum type payment_term_t has no value for the codes n90; add them in an earlier migration", output, StringComparison.Ordinal);
        Assert.Contains("type credit_term_t", output, StringComparison.Ordinal);
    }

    private static string View(string id, string name, string body, string dependsOn) => $$"""
        { "kind": "view", "id": "{{id}}", "name": "{{name}}", "database": "{{PaymentTerms.MainId}}", "schema": "01J92P0V1RC04SKQ5353EAKHG2",
          "body": { "*": "{{body}}" }, "dependsOn": [{{dependsOn}}],
          "columns": [ { "name": "name", "type": "string", "nullable": false }, { "name": "credit_term", "type": "string", "nullable": true } ] }
        """;

    [Fact]
    public async Task A_merge_retypes_an_enum_column_that_modeled_views_read()
    {
        // Under native the merge retypes customers.credit_term from credit_term_t to payment_term_t, which PostgreSQL refuses while a
        // view reads the column: the step drops the model's views first (ada_terms before customer_terms, which it reads), and the
        // migration creates them again at its end.
        const string CustomerTerms = "01KACT00000000000000000041";
        using var repo = PaymentTerms.Create("native");
        var refactor = await RefactorAsync(repo, MergeCreditTerms(""", "codes": { "n90": "net60" }"""), setup: async r =>
        {
            await PaymentTerms.CreateAsync(r, View(CustomerTerms, "customer_terms", "select name, credit_term from billing.customers", ""));
            await PaymentTerms.CreateAsync(r, View("01KACT00000000000000000042", "ada_terms", "select name, credit_term from billing.customer_terms where name = 'Ada'",
                "\"" + CustomerTerms + "\""));
        });

        AssertBefore(refactor.Migration, "DROP VIEW IF EXISTS billing.ada_terms;", "DROP VIEW IF EXISTS billing.customer_terms;");
        AssertBefore(refactor.Migration, "DROP VIEW IF EXISTS billing.customer_terms;", "ALTER COLUMN credit_term TYPE payment_term_t");
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await RunTwiceAsync(db, "merge_views", refactor, TermRows("native", false), TermQuery(refactor.FactId)
            + "SELECT 'view ' || name || ' ' || credit_term::text FROM billing.ada_terms;\n"
            + "SELECT 'view type ' || format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'billing.customer_terms'::regclass AND attname = 'credit_term';\n");
        Assert.Contains("customer Ada net60", output, StringComparison.Ordinal);
        Assert.Contains("view Ada net60", output, StringComparison.Ordinal);
        Assert.Contains("view type payment_term_t", output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merge_retypes_an_enum_column_that_the_model_s_last_view_read_as_the_view_leaves()
    {
        // customer_terms is the model's only view and leaves it in the merge's generation: the model has no view now, but the step still
        // drops it before the retype, and the migration's own drop of it then finds none.
        const string OutstandingInvoices = "01J92P0V1YZ4YP352KD1A50FXS";
        const string CustomerTerms = "01KACT00000000000000000041";
        using var repo = PaymentTerms.Create("native");
        var refactor = await RefactorAsync(repo, MergeCreditTerms(""", "codes": { "n90": "net60" }"""), setup: async r =>
        {
            await PaymentTerms.DeleteAsync(r, OutstandingInvoices);
            await PaymentTerms.CreateAsync(r, View(CustomerTerms, "customer_terms", "select name, credit_term from billing.customers", ""));
        }, edit: r => PaymentTerms.DeleteAsync(r, CustomerTerms));

        AssertBefore(refactor.Migration, "DROP VIEW IF EXISTS billing.customer_terms;", "ALTER COLUMN credit_term TYPE payment_term_t");
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await RunTwiceAsync(db, "merge_last_view", refactor, TermRows("native", false), TermQuery(refactor.FactId)
            + "SELECT 'views ' || count(*) FROM pg_views WHERE schemaname = 'billing';\n");
        Assert.Contains("customer Ada net60", output, StringComparison.Ordinal);
        Assert.Contains("views 0", output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_map_upserts_the_rows_it_needs_after_the_rows_their_fields_name()
    {
        // PaymentTerm gets a field typed by TermCategory. The rename moves net30 to net-30, and the same generation gives TermCategory a
        // new code that net-30 names: the step upserts that category row first and then the net-30 row alone, so the foreign key between
        // the lookup tables holds although seed.sql adds the category's other rows later.
        const string Category = "01KATC00000000000000000001";
        const string CategorySeed = "01KATC00000000000000000010";
        const string CategoryField = "01KATC00000000000000000004";
        using var repo = PaymentTerms.Create("lookup-table");
        var refactor = await RefactorAsync(repo, RenameNet30, setup: async r =>
        {
            await PaymentTerms.CreateAsync(r, $$"""
                { "kind": "reference-type", "id": "{{Category}}", "name": "TermCategory",
                  "code": { "id": "01KATC00000000000000000002", "length": 8 }, "label": { "id": "01KATC00000000000000000003", "length": 64 } }
                """);
            await PaymentTerms.CreateAsync(r, $$"""
                { "kind": "seed", "id": "{{CategorySeed}}", "name": "TermCategories", "target": "{{Category}}", "columns": ["code", "label"],
                  "rows": [ { "id": "01KATC00000000000000000011", "values": ["std", "Standard"] } ] }
                """);
            await PaymentTerms.EditAsync(r, PaymentTerms.PaymentTerm,
                e => e["attributes"] = JsonNode.Parse($$"""[ { "id": "{{CategoryField}}", "name": "category", "type": { "ref": "{{Category}}" } } ]"""));
            await PaymentTerms.EditAsync(r, PaymentTerms.PaymentTermSeed, e =>
            {
                e["columns"]!.AsArray().Add(CategoryField);
                foreach (var row in e["rows"]!.AsArray())
                    row!["values"]!.AsArray().Add("std");
            });
        }, edit: async r =>
        {
            await PaymentTerms.EditAsync(r, CategorySeed, e => e["rows"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01KATC00000000000000000012", "values": ["early", "Early"] }""")));
            await PaymentTerms.EditAsync(r, PaymentTerms.PaymentTermSeed, e =>
            {
                foreach (var row in e["rows"]!.AsArray())
                {
                    if (row!["values"]![0]!.GetValue<string>() == "net-30")
                        row["values"]![2] = "early";
                }
            });
        });

        var migration = refactor.Migration;
        AssertBefore(migration, "INSERT INTO billing.term_categories (code, label) VALUES\n    ('early', 'Early')\nON CONFLICT",
            "INSERT INTO billing.payment_terms (code, label, category) VALUES\n    ('net-30', 'Net 30', 'early')\nON CONFLICT");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(migration, "INSERT INTO billing.payment_terms "));
        Assert.DoesNotContain("('std', 'Standard')", migration, StringComparison.Ordinal);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await RunTwiceAsync(db, "map_field_rows", refactor, TermRows("lookup-table", false), TermQuery(refactor.FactId)
            + "SELECT 'term ' || code || ' ' || coalesce(category, '-') FROM billing.payment_terms ORDER BY code;\n");
        Assert.Contains("invoice INV-1 net-30", output, StringComparison.Ordinal);
        Assert.Contains("term net-30 early", output, StringComparison.Ordinal);
        Assert.Contains("term net60 std", output, StringComparison.Ordinal);
        Assert.Contains("ledger 1", output, StringComparison.Ordinal);
    }

    private const string SettlementTerm = "01KAST00000000000000000001";
    private const string SettlementCategory = "01KAST00000000000000000005";

    /// <summary>
    /// Adds TermCategory (std) and SettlementTerm, whose field category is typed by it, with the rows net30 and n90: their category
    /// std when <paramref name="categories"/>, else none.
    /// </summary>
    private static async Task AddSettlementTermsAsync(PackRepo r, bool categories)
    {
        await PaymentTerms.CreateAsync(r, $$"""
            { "kind": "reference-type", "id": "{{SettlementCategory}}", "name": "TermCategory",
              "code": { "id": "01KAST00000000000000000006", "length": 8 }, "label": { "id": "01KAST00000000000000000007", "length": 64 } }
            """);
        await PaymentTerms.CreateAsync(r, $$"""
            { "kind": "seed", "id": "01KAST00000000000000000008", "name": "TermCategories", "target": "{{SettlementCategory}}", "columns": ["code", "label"],
              "rows": [ { "id": "01KAST00000000000000000009", "values": ["std", "Standard"] } ] }
            """);
        await PaymentTerms.CreateAsync(r, $$"""
            { "kind": "reference-type", "id": "{{SettlementTerm}}", "name": "SettlementTerm",
              "code": { "id": "01KAST00000000000000000002", "length": 8 }, "label": { "id": "01KAST00000000000000000003", "length": 64 },
              "attributes": [ { "id": "01KAST00000000000000000004", "name": "category", "type": { "ref": "{{SettlementCategory}}" } } ] }
            """);
        var columns = categories ? """["code", "label", "01KAST00000000000000000004"]""" : """["code", "label"]""";
        var category = categories ? ", \"std\"" : "";
        await PaymentTerms.CreateAsync(r, $$"""
            { "kind": "seed", "id": "01KAST00000000000000000010", "name": "SettlementTerms", "target": "{{SettlementTerm}}", "columns": {{columns}},
              "rows": [ { "id": "01KAST00000000000000000011", "values": ["net30", "Net 30"{{category}}] },
                        { "id": "01KAST00000000000000000012", "values": ["n90", "Net 90"{{category}}] } ] }
            """);
    }

    private static Task MoveCreditTermAsync(PackRepo r, string type) => PaymentTerms.EditAsync(r, PaymentTerms.CustomerId, e =>
        e["attributes"]!.AsArray().Single(a => (string?)a!["name"] == "creditTerm")!["type"] = JsonNode.Parse($$"""{ "ref": "{{type}}" }"""));

    [Fact]
    public async Task A_column_moved_to_a_new_type_gets_every_lookup_table_its_fields_reference()
    {
        // Customer.creditTerm moves to SettlementTerm in a plain edit, and the same generation adds SettlementTerm with its field category
        // typed by the new TermCategory. No SettlementTerm row names a category, but its lookup table has a foreign key to TermCategory's,
        // so the column step creates TermCategory's table first (and writes none of its rows).
        using var repo = PaymentTerms.Create("lookup-table");
        var refactor = await RefactorAsync(repo, null, edit: async r =>
        {
            await AddSettlementTermsAsync(r, categories: false);
            await MoveCreditTermAsync(r, SettlementTerm);
        });

        AssertBefore(refactor.Migration, "CREATE TABLE IF NOT EXISTS billing.term_categories ", "CREATE TABLE IF NOT EXISTS billing.settlement_terms ");
        Assert.DoesNotContain("INSERT INTO billing.term_categories ", refactor.Migration, StringComparison.Ordinal);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await RunTwiceAsync(db, "moved_new_types", refactor, TermRows("lookup-table", false), TermQuery(null)
            + "SELECT 'references ' || confrelid::regclass FROM pg_constraint WHERE conname = 'fk_customers_credit_term_ref';\n");
        Assert.Contains("customer Ada n90", output, StringComparison.Ordinal);
        Assert.Contains("customer Bob net30", output, StringComparison.Ordinal);
        Assert.Contains("references billing.settlement_terms", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_columns_moved_to_one_type_write_each_type_s_rows_once()
    {
        // Customer.creditTerm and the notes' term move to SettlementTerm, whose rows name the category std: the first column step creates
        // both lookup tables and upserts their rows, the second finds them written.
        using var repo = PaymentTerms.Create("lookup-table");
        var refactor = await RefactorAsync(repo, null, edit: async r =>
        {
            await AddSettlementTermsAsync(r, categories: true);
            await MoveCreditTermAsync(r, SettlementTerm);
            await PaymentTerms.EditAsync(r, PaymentTerms.NotesTable, e =>
                e["columns"]!.AsArray().Single(c => (string?)c!["name"] == "term")!["typeRef"] = JsonNode.Parse($$"""{ "ref": "{{SettlementTerm}}" }"""));
        });

        var migration = refactor.Migration;
        Assert.Contains("-- customers.credit_term now holds codes of SettlementTerm", migration, StringComparison.Ordinal);
        Assert.Contains("-- notes.term now holds codes of SettlementTerm", migration, StringComparison.Ordinal);
        foreach (var once in new[] { "CREATE TABLE IF NOT EXISTS billing.term_categories ", "CREATE TABLE IF NOT EXISTS billing.settlement_terms ",
            "INSERT INTO billing.term_categories ", "INSERT INTO billing.settlement_terms " })
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(migration, System.Text.RegularExpressions.Regex.Escape(once)));
        AssertBefore(migration, "INSERT INTO billing.term_categories ", "INSERT INTO billing.settlement_terms ");
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var output = await RunTwiceAsync(db, "moved_two_columns", refactor, TermRows("lookup-table", false), TermQuery(null)
            + "SELECT 'term ' || code || ' ' || coalesce(category, '-') FROM billing.settlement_terms ORDER BY code;\n");
        Assert.Contains("customer Ada n90", output, StringComparison.Ordinal);
        Assert.Contains("note n90", output, StringComparison.Ordinal);
        Assert.Contains("term n90 std", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("native")]
    public async Task Collections_lose_the_duplicates_a_map_makes(string strategy)
    {
        // Ada accepts net30, due and net60: due maps to net60, which she holds already, so one net60 stays, in due's place for the array.
        using var repo = PaymentTerms.Create(strategy);
        var refactor = await RefactorAsync(repo, RetireDue, collection: true);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        var query = strategy == "native"
            ? "SELECT 'accepted ' || name || ' ' || array_to_string(accepted_terms, ',') FROM billing.customers ORDER BY name;\n"
            : "SELECT 'accepted ' || c.name || ' ' || string_agg(a.accepted_term_code, ',' ORDER BY a.accepted_term_code) FROM billing.customers_accepted_terms a "
              + "JOIN billing.customers c ON c.id = a.customer_id GROUP BY c.name ORDER BY c.name;\n";
        var output = await RunTwiceAsync(db, "collections_" + strategy.Replace('-', '_'), refactor, TermRows(strategy, true), query);
        Assert.Contains("accepted Ada net30,net60", output, StringComparison.Ordinal);
        Assert.Contains("accepted Bob net60", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("native")]
    public async Task A_reference_type_change_without_a_map_reconciles_the_column(string strategy)
    {
        // Customer.creditTerm is typed by PaymentTerm in a plain edit: no value map, so the column step drops the column's reference
        // objects and creates them for PaymentTerm (a foreign key to its lookup table), or retypes it to PaymentTerm's enum type.
        using var repo = PaymentTerms.Create(strategy);
        var refactor = await RefactorAsync(repo, null, edit: r => PaymentTerms.EditAsync(r, PaymentTerms.CustomerId, e =>
            e["attributes"]!.AsArray().Single(a => (string?)a!["name"] == "creditTerm")!["type"] = JsonNode.Parse($$"""{ "ref": "{{PaymentTerms.PaymentTerm}}" }""")));
        Assert.Contains("-- customers.credit_term now holds codes of PaymentTerm (" + strategy + "): its reference objects are dropped and created again.",
            refactor.Migration, StringComparison.Ordinal);
        Assert.DoesNotContain("-- TODO", refactor.Migration, StringComparison.Ordinal);
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(RefactorSkip);
            return;
        }

        // Ada's n90 is no PaymentTerm code, so she stores none before; Bob's net30 is a code of both.
        var rows = TermRows(strategy, false).Replace("now(), 'n90'),", "now(), NULL),", StringComparison.Ordinal);
        var output = await RunTwiceAsync(db, "retyped_" + strategy.Replace('-', '_'), refactor, rows, TermQuery(null)
            + "SELECT 'type ' || format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'billing.customers'::regclass AND attname = 'credit_term';\n"
            + "SELECT 'references ' || confrelid::regclass FROM pg_constraint WHERE conname = 'fk_customers_credit_term_ref';\n");
        Assert.Contains("customer Bob net30", output, StringComparison.Ordinal);
        Assert.Contains(strategy == "native" ? "type payment_term_t" : "references billing.payment_terms", output, StringComparison.Ordinal);
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

    private static void AssertBefore(string text, string first, string second)
    {
        var a = text.IndexOf(first, StringComparison.Ordinal);
        var b = text.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, $"Expected\n  {first}\nbefore\n  {second}\nin\n{text}");
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
