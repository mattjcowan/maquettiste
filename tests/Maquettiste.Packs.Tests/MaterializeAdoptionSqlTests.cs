using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// "Create tables…" turns the tables the sql-ddl pack made in seed.sql (a lookup table, a list's table, the foreign keys or checks of the
/// columns holding a reference type's codes) into designed tables, which seed.sql no longer creates. The migration that renders the
/// write's rekey fact creates them where they are missing: a database built from the migrations alone then takes the new seed.sql, and
/// a database where the earlier seed.sql made them runs the migration without an error and without a change to its schema.
/// </summary>
[UsesProjectedFixtures]
public sealed class MaterializeAdoptionSqlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The scripts of one database around the conversion.</summary>
    /// <param name="Chain">The migrations before the conversion, in order, as one script.</param>
    /// <param name="Rekey">The migration that renders the rekey fact.</param>
    /// <param name="OldSeed">seed.sql before the conversion (it creates the lookup and list tables).</param>
    /// <param name="NewSeed">seed.sql after it.</param>
    private sealed record Scripts(string Chain, string Rekey, string OldSeed, string NewSeed);

    private static async Task<Scripts> ConvertAsync(PackRepo repo, string database, bool sameDdl = true)
    {
        var proof = await MaterializeProof.RunAsync(repo.Repo.Options, "db", Ct);
        if (sameDdl)
            MaterializeShapeTests.AssertSameDdl(proof);
        var folder = "db/" + database + "/";
        var chain = proof.MigrationsAtStart.Where(p => p.StartsWith(folder, StringComparison.Ordinal))
            .Select(p => File.ReadAllText(Path.Combine(repo.Repo.RepoRoot, p))).ToList();
        Assert.NotEmpty(chain);
        var rekey = Assert.Single(proof.RekeyMigrations, m => m.Key.StartsWith(folder, StringComparison.Ordinal)).Value;
        var adoption = MaterializeAdoptionTests.AdoptionLines(rekey);
        Assert.NotEmpty(adoption);
        // The lookup table's rows go in (inserted when missing) before the foreign key from a column that may already hold its codes.
        var constraint = adoption.FindIndex(l => l.Contains("fk_ingredients_default_unit_ref", StringComparison.Ordinal));
        if (constraint >= 0)
        {
            var rows = adoption.FindIndex(l => (l.StartsWith("INSERT", StringComparison.Ordinal) || l.StartsWith("MERGE", StringComparison.Ordinal))
                && l.Contains("units_of_measure", StringComparison.Ordinal) && l.Contains("'kg'", StringComparison.Ordinal));
            Assert.True(rows >= 0 && rows < constraint, rekey);
        }

        var oldSeed = proof.Before[folder + "seed.sql"];
        var newSeed = proof.After[folder + "seed.sql"];
        Assert.Contains("CREATE TABLE ", oldSeed, StringComparison.Ordinal);
        return new Scripts(string.Join("\n", chain), rekey, oldSeed, newSeed);
    }

    /// <summary>
    /// A script that prints the schema (the dialect's query, <paramref name="schema"/>), runs the migration, prints the schema again and
    /// then runs the new seed.sql; <see cref="Split"/> reads the two prints back.
    /// </summary>
    private static string Compare(Scripts s, string schema, string go = "") =>
        "SELECT '==before==';\n" + go + schema + s.Rekey + "\n" + go + "SELECT '==after==';\n" + go + schema + "SELECT '==end==';\n" + go + s.NewSeed;

    private static (string Before, string After) Split(string output)
    {
        var start = output.IndexOf("==before==", StringComparison.Ordinal);
        var at = output.IndexOf("==after==", StringComparison.Ordinal);
        var end = output.IndexOf("==end==", StringComparison.Ordinal);
        Assert.True(start >= 0 && at > start && end > at, output);
        return (output[(start + "==before==".Length)..at].Trim(), output[(at + "==after==".Length)..end].Trim());
    }

    /// <summary>The statement of a seed script that writes the ingredients' rows (they hold unit codes): rows a database already stores.</summary>
    private static string Ingredients(string seed)
    {
        var at = seed.IndexOf("INSERT INTO ", StringComparison.Ordinal);
        while (at >= 0 && !seed[at..].StartsWith("INSERT INTO ingredients (", StringComparison.Ordinal) && !seed[at..].StartsWith("INSERT INTO public.ingredients (", StringComparison.Ordinal))
            at = seed.IndexOf("INSERT INTO ", at + 1, StringComparison.Ordinal);
        Assert.True(at >= 0, seed);
        return seed[at..(seed.IndexOf(";\n", at, StringComparison.Ordinal) + 1)];
    }

    private const string SqliteSchema = "SELECT type || ' ' || name || ' ' || coalesce(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;\n";

    [Theory]
    [InlineData("lookup-table")]
    [InlineData("check")]
    public async Task Sqlite_builds_from_the_migrations_alone_and_leaves_a_database_seed_sql_made_as_it_is(string strategy)
    {
        using var repo = PackRepo.ReferenceData(strategy);
        var s = await ConvertAsync(repo, "local");
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
        {
            Assert.Skip("sqlite3 is not on the PATH.");
            return;
        }

        // SQLite cannot add a constraint to a table: a column's adopted foreign key or check comes as the triggers seed.sql made.
        Assert.Contains(strategy == "check" ? "CREATE TRIGGER IF NOT EXISTS ck_" : "CREATE TRIGGER IF NOT EXISTS fk_", s.Rekey, StringComparison.Ordinal);
        var folder = repo.PathOf("db/local");
        // A database built from the migrations alone, then the new seed.sql (twice: it reconciles).
        var fresh = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1),
            s.Chain + "\n" + s.Rekey + "\n" + s.NewSeed + "\n" + s.NewSeed + "\nSELECT 'allergens=' || count(*) FROM allergens;\n");
        Assert.True(fresh.ExitCode == 0, fresh.Output);
        Assert.Matches("allergens=[1-9]", fresh.Output);

        // A database built from the migrations alone that already stores codes (an ingredient row): the migration adds the lookup rows
        // before the constraints, so it runs cleanly; run again, it changes nothing.
        var stored = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1),
            s.Chain + "\n" + Ingredients(s.NewSeed) + "\n" + s.Rekey + "\n" + Compare(s, SqliteSchema + "SELECT 'allergens=' || count(*) FROM allergens;\n"));
        Assert.True(stored.ExitCode == 0, stored.Output);
        var (once, twice) = Split(stored.Output);
        Assert.Equal(once, twice);
        Assert.Matches("allergens=[1-9]", once);

        // A database the earlier seed.sql completed: the migration finds everything there and changes nothing; the new seed.sql runs.
        var existing = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1),
            s.Chain + "\n" + s.OldSeed + "\n" + Compare(s, SqliteSchema));
        Assert.True(existing.ExitCode == 0, existing.Output);
        var (before, after) = Split(existing.Output);
        Assert.Equal(before, after);
    }

    private const string PostgresSchema =
        "SELECT 'table ' || table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'migration_ledger')\n"
        + "UNION ALL SELECT 'column ' || table_schema || '.' || table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable FROM information_schema.columns WHERE table_schema NOT IN ('pg_catalog', 'information_schema', 'migration_ledger')\n"
        + "UNION ALL SELECT 'constraint ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE connamespace::regnamespace::text NOT IN ('pg_catalog', 'information_schema', 'migration_ledger')\n"
        + "ORDER BY 1;\n";

    [Fact]
    public async Task Postgres_builds_from_the_migrations_alone_and_leaves_a_database_seed_sql_made_as_it_is()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        var s = await ConvertAsync(repo, "main");
        if (DbServer.Postgres() is not { } server)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_POSTGRES_CONTAINER to run the migrations on PostgreSQL.");
            return;
        }

        var fresh = await server.RunAsync("adopt_fresh", s.Chain, s.Rekey, s.NewSeed, s.NewSeed + "\nSELECT 'allergens=' || count(*) FROM allergens;\n");
        Assert.Matches("allergens=[1-9]", fresh);

        // Codes already stored: the foreign keys are added after the lookup rows, so they validate; run again, nothing changes.
        var (once, twice) = Split(await server.RunAsync("adopt_stored", s.Chain, Ingredients(s.NewSeed), s.Rekey,
            "SET client_min_messages = warning;\n" + Compare(s, PostgresSchema + "SELECT 'units=' || count(*) FROM units_of_measure;\n")));
        Assert.Equal(once, twice);
        Assert.Matches("units=[1-9]", once);

        var (before, after) = Split(await server.RunAsync("adopt_existing", s.Chain, s.OldSeed, "SET client_min_messages = warning;\n" + Compare(s, PostgresSchema)));
        Assert.Contains("fk_ingredients_default_unit_ref", before, StringComparison.Ordinal);
        Assert.Equal(before, after);
    }

    private const string SqlServerSchema =
        "SELECT o.type COLLATE DATABASE_DEFAULT + ' ' + SCHEMA_NAME(o.schema_id) COLLATE DATABASE_DEFAULT + '.' + o.name COLLATE DATABASE_DEFAULT + ' '\n"
        + "    + COALESCE(OBJECT_NAME(o.parent_object_id), '') COLLATE DATABASE_DEFAULT + ' ' + COALESCE(c.definition, '') COLLATE DATABASE_DEFAULT\n"
        + "FROM sys.objects o LEFT JOIN sys.check_constraints c ON c.object_id = o.object_id WHERE o.is_ms_shipped = 0 ORDER BY 1;\nGO\n";

    [Fact]
    public async Task Sql_server_builds_from_the_migrations_alone_and_leaves_a_database_seed_sql_made_as_it_is()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        var s = await ConvertAsync(repo, "reporting");
        if (DbServer.SqlServer() is not { } server)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_SQLSERVER_CONTAINER and MAQUETTISTE_TEST_SQLSERVER_PASSWORD to run the migrations on SQL Server.");
            return;
        }

        var fresh = await server.RunAsync("adopt_fresh", s.Chain, s.Rekey, s.NewSeed, s.NewSeed + "\nGO\nSELECT 'allergens=' + CAST(count(*) AS varchar(10)) FROM allergens;\nGO\n");
        Assert.Matches("allergens=[1-9]", fresh);

        var (before, after) = Split(await server.RunAsync("adopt_existing", s.Chain, s.OldSeed, "SET NOCOUNT ON;\nGO\n" + Compare(s, SqlServerSchema, "GO\n")));
        Assert.Contains("fk_ingredients_default_unit_ref", before, StringComparison.Ordinal);
        Assert.Equal(before, after);
    }

    private const string MySqlSchema =
        "SELECT CONCAT('table ', table_name) FROM information_schema.tables WHERE table_schema = DATABASE()\n"
        + "UNION ALL SELECT CONCAT('constraint ', table_name, ' ', constraint_name, ' ', constraint_type) FROM information_schema.table_constraints WHERE table_schema = DATABASE()\n"
        + "ORDER BY 1;\n";

    /// <summary>
    /// MySQL and Oracle write the same adoption, each statement guarded in the dialect's way (an information_schema test through a
    /// prepared statement; a PL/SQL block that ignores "already exists"); MySQL runs it when a server is configured.
    /// </summary>
    [Theory]
    [InlineData("mysql", "lookup-table", "ADD CONSTRAINT fk_ingredients_default_unit_ref FOREIGN KEY")]
    [InlineData("oracle", "lookup-table", "ADD CONSTRAINT fk_ingredients_default_unit_ref FOREIGN KEY")]
    [InlineData("oracle", "check", "ADD CONSTRAINT ck_ingredients_default_unit_ref CHECK")]
    public async Task Mysql_and_oracle_create_what_the_rekey_fact_adopted_where_it_is_missing(string dialect, string strategy, string constraint)
    {
        using var repo = PackRepo.ReferenceData(strategy);
        repo.EditJson(".maquettiste/model/databases/reporting/database.json", d => d["dialect"] = dialect);
        // Converting a model in these dialects is not proved byte for byte (MaterializeShapeTests): only the migration that renders the
        // rekey fact is read here.
        var s = await ConvertAsync(repo, "reporting", sameDdl: false);
        var adoption = MaterializeAdoptionTests.AdoptionLines(s.Rekey);
        Assert.Contains(adoption, l => l.Contains("allergens (", StringComparison.Ordinal));
        Assert.Contains(adoption, l => l.Contains(constraint, StringComparison.Ordinal));
        if (dialect == "oracle")
        {
            // DDL in PL/SQL blocks that ignore "already exists"; the reference rows in a MERGE that inserts only missing codes.
            Assert.All(adoption.Where(l => !l.StartsWith("MERGE INTO ", StringComparison.Ordinal)),
                l => Assert.Matches(@"^BEGIN\n    EXECUTE IMMEDIATE '[\s\S]*';\nEXCEPTION WHEN OTHERS THEN\n    IF SQLCODE NOT IN \(-[0-9, -]+\) THEN RAISE; END IF;\nEND;\n/$", l));
            Assert.All(adoption.Where(l => l.StartsWith("MERGE INTO ", StringComparison.Ordinal)), l => Assert.True(MaterializeAdoptionTests.IsIdempotent(l), l));
            return;
        }

        if (DbServer.MySql() is not { } server)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_MYSQL_CONTAINER and MAQUETTISTE_TEST_MYSQL_PASSWORD to run the migrations on MySQL.");
            return;
        }

        Assert.Matches("allergens=[1-9]", await server.RunAsync("adopt_fresh", s.Chain, s.Rekey, s.NewSeed, s.NewSeed + "\nSELECT CONCAT('allergens=', count(*)) FROM allergens;\n"));
        var (before, after) = Split(await server.RunAsync("adopt_existing", s.Chain, s.OldSeed, Compare(s, MySqlSchema)));
        Assert.Contains("fk_ingredients_default_unit_ref", before, StringComparison.Ordinal);
        Assert.Equal(before, after);
    }

    /// <summary>
    /// On Oracle a lookup table's code column has the type the columns holding the codes have (<c>varchar2(n char)</c>, the type map's),
    /// in seed.sql before "Create tables…" and in the designed table after it, so the foreign keys from those columns pass MQ4005 and
    /// generation succeeds.
    /// </summary>
    [Fact]
    public async Task Oracle_lookup_tables_take_the_type_of_the_columns_that_hold_their_codes()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        repo.EditJson(".maquettiste/model/databases/reporting/database.json", d => d["dialect"] = "oracle");

        var proof = await MaterializeProof.RunAsync(repo.Repo.Options, "db", Ct);

        Assert.True(proof.Errors.Count == 0 && !proof.After.ContainsKey("(generation)"), proof.Describe());
        Assert.Contains("CREATE TABLE IF NOT EXISTS units_of_measure (\n    code varchar2(8 char) NOT NULL,", proof.Before["db/reporting/seed.sql"], StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE units_of_measure (\n    code varchar2(8 char) NOT NULL,", proof.After["db/reporting/schema.sql"], StringComparison.Ordinal);
        Assert.Contains("    default_unit varchar2(8 char) ", proof.After["db/reporting/schema.sql"], StringComparison.Ordinal);
    }
}
