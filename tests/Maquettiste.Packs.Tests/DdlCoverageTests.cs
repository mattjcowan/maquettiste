using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The sql-ddl pack over the DDL coverage model (<see cref="DdlCoverage"/>): byte-exact goldens of the create scripts (step 1) and of
/// the tree after the last step (the final create scripts and every migration), plain and with the <c>idempotent</c> parameter, under
/// <c>tests/fixtures/golden/sql-ddl/ddl-coverage</c>; and the scripts run on SQLite (sqlite3 on the PATH) and on the servers
/// <see cref="DbServer"/> finds: the step 1 schema, the migration chain over rows inserted after the first migration, and the final
/// schema; when idempotent, every script twice (SQLite's migrations once: SQLite has no guard for renames and rebuilds, and its
/// idempotent migrations say so).
/// </summary>
public sealed class DdlCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_scripts_and_every_migration_match_the_golden_trees(bool idempotent)
    {
        using var repo = DdlCoverage.Create(idempotent);
        var golden = Fixtures.Path("golden", "sql-ddl", idempotent ? "ddl-coverage-idempotent" : "ddl-coverage");
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(golden, "step1"), repo.PathOf("db"));
        for (var step = 2; step <= DdlCoverage.Steps; step++)
        {
            DdlCoverage.Write(repo, step);
            await repo.GenerateCleanlyAsync();
        }

        Golden.AssertMatches(Path.Combine(golden, "final"), repo.PathOf("db"));
        // Steps 4 and 5 change schemas only, which the MySQL, SQLite and Oracle databases do not declare.
        Assert.True(File.Exists(repo.PathOf("db/postgres/migrations/0005.sql")));
        Assert.False(File.Exists(repo.PathOf("db/mysql/migrations/0004.sql")));
    }

    [Fact]
    public async Task Validation_reports_no_dialect_feature_the_ddl_would_leave_out()
    {
        using var repo = DdlCoverage.Create(idempotent: false);
        for (var step = 1; step <= DdlCoverage.Steps; step++)
        {
            DdlCoverage.Write(repo, step);
            var result = await repo.GenerateCleanlyAsync();
            Assert.DoesNotContain(result.Diagnostics, d => d.Rule is "MQ4056" or "MQ4057");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_runs_the_create_scripts_and_the_migration_chain(bool idempotent)
    {
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
        {
            Assert.Skip("sqlite3 is not on the PATH.");
            return;
        }

        var scripts = await ScriptsAsync("sqlite", idempotent);
        async Task<string> Run(string script)
        {
            var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1), script);
            Assert.True(run.ExitCode == 0, run.Output + "\n--- script ---\n" + script);
            return run.Output;
        }

        await Run(scripts.First + (idempotent ? scripts.First : ""));
        await Run(scripts.Final + (idempotent ? scripts.Final : ""));
        var output = await Run(string.Concat(scripts.Migrations.Take(1)) + Rows(null, "sqlite") + string.Concat(scripts.Migrations.Skip(1)) + Counts(null, "sqlite"));
        Assert.Contains("customers=1|orders=1|order_items=1", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgresql", false)]
    [InlineData("postgresql", true)]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    [InlineData("mysql", false)]
    [InlineData("mysql", true)]
    [InlineData("mariadb", false)]
    [InlineData("mariadb", true)]
    public async Task Servers_run_the_create_scripts_and_the_migration_chain(string server, bool idempotent)
    {
        var db = server switch
        {
            "postgresql" => DbServer.Postgres(),
            "sqlserver" => DbServer.SqlServer(),
            "mysql" => DbServer.MySql(),
            _ => DbServer.MariaDb(),
        };
        if (db is null)
        {
            Assert.Skip($"Name a {server} container in the environment to run the DDL on it (see DbServer).");
            return;
        }

        var folder = db.Dialect switch { "postgresql" => "postgres", "sqlserver" => "sqlserver", _ => "mysql" };
        var scripts = await ScriptsAsync(folder, idempotent);
        await db.RunAsync("create", idempotent ? [scripts.First, scripts.First] : [scripts.First]);
        await db.RunAsync("final", idempotent ? [scripts.Final, scripts.Final] : [scripts.Final]);
        var schema = db.Dialect is "postgresql" or "sqlserver" ? "sales." : null;
        var final = db.Dialect is "postgresql" or "sqlserver" ? "shop." : null;
        // Idempotent: each migration runs twice in a row, the second time over what the first one did.
        var twice = idempotent ? 2 : 1;
        string[] chain = [.. Enumerable.Repeat(scripts.Migrations[0], twice), Rows(schema, db.Dialect),
            .. scripts.Migrations.Skip(1).SelectMany(m => Enumerable.Repeat(m, twice)), Counts(final, db.Dialect)];
        var output = await db.RunAsync("chain", chain);
        Assert.Contains("customers=1|orders=1|order_items=1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sqlite_rebuild_gives_a_new_not_null_column_a_placeholder_that_fails_on_rows()
    {
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        using var repo = DdlCoverage.Create(idempotent: false);
        await repo.GenerateCleanlyAsync();
        var first = repo.Read("db/sqlite/migrations/0001.sql");
        // Step 1 again, with a NOT NULL column without a default added to the SQLite customers: a rebuild.
        DdlCoverage.Write(repo, 1, (db, path, node) =>
        {
            if (db.Dialect == "sqlite" && path == "tables/customers")
                node["columns"]!.AsArray().Add(DdlCoverage.NewColumn(db, 33, "rating", "int32", c => c["nullable"] = false));
        });
        await repo.GenerateCleanlyAsync();
        var second = repo.Read("db/sqlite/migrations/0002.sql");

        Assert.Contains("-- TODO: rating is NOT NULL without a default: replace the NULL placeholder below with each row's value", second, StringComparison.Ordinal);
        Assert.Contains(", NULL /* TODO: rating */ FROM customers;", second, StringComparison.Ordinal);
        Assert.Contains("PRAGMA legacy_alter_table = ON;\nALTER TABLE \"_mq_new_customers\" RENAME TO customers;\nPRAGMA legacy_alter_table = OFF;", second, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER IF NOT EXISTS customers_touch", second, StringComparison.Ordinal);
        if (sqlite3 is null)
            return;
        // Over an empty table the placeholder is never read; over a row it fails the migration (sqlite3 -bail stops there).
        Assert.Equal(0, (await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1), first + second)).ExitCode);
        var failed = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1), first + Rows(null, "sqlite") + second);
        Assert.NotEqual(0, failed.ExitCode);
        Assert.Contains("NOT NULL constraint failed", failed.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sqlite_migration_that_rebuilds_tables_fails_when_a_foreign_key_is_broken()
    {
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
        {
            Assert.Skip("sqlite3 is not on the PATH.");
            return;
        }

        var scripts = await ScriptsAsync("sqlite", idempotent: false);
        Assert.Contains("FROM pragma_foreign_key_check;", scripts.Migrations[1], StringComparison.Ordinal);
        // An order whose customer is gone (foreign keys off, as an import might leave it): migration 0002 rebuilds the tables and refuses
        // to commit them.
        const string orphan = "PRAGMA foreign_keys = OFF;\nDELETE FROM customers;\n";
        var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1),
            scripts.Migrations[0] + Rows(null, "sqlite") + orphan + scripts.Migrations[1]);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("foreign keys broken by the rebuilds", run.Output, StringComparison.Ordinal);

        // Without -bail too: the trigger rolls the whole migration back, so the commit after it finds no transaction and nothing of the
        // rebuild stays (the old order_lines is still there under its old name).
        var lenient = await ProcessRunner.RunAsync(sqlite3, [":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1),
            scripts.Migrations[0] + Rows(null, "sqlite") + orphan + scripts.Migrations[1] + "\nSELECT 'tables=' || group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name);\n");
        Assert.Contains("foreign keys broken by the rebuilds", lenient.Output, StringComparison.Ordinal);
        Assert.Contains("tables=customers,order_lines,orders", lenient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sqlite_bail_parameter_writes_bail_on_at_the_top_of_sqlite_migrations_only()
    {
        using var repo = DdlCoverage.Create(idempotent: false);
        var settings = System.Text.Json.Nodes.JsonNode.Parse(repo.Read(".maquettiste/maquettiste.json"))!;
        settings["packs"]!["sql-ddl"]!["parameters"]!["sqliteBail"] = true;
        repo.Write(".maquettiste/maquettiste.json", settings.ToJsonString());
        await repo.GenerateCleanlyAsync();
        DdlCoverage.Write(repo, 2);
        await repo.GenerateCleanlyAsync();

        Assert.StartsWith(".bail on\n-- Migration 0002", repo.Read("db/sqlite/migrations/0002.sql"), StringComparison.Ordinal);
        Assert.DoesNotContain(".bail", repo.Read("db/postgres/migrations/0002.sql"), StringComparison.Ordinal);
        Assert.DoesNotContain(".bail", repo.Read("db/sqlite/schema.sql"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The view-body step (<see cref="DdlCoverage.ViewBody"/>): only customer_orders' body changes, and customer_order_totals reads it.
    /// PostgreSQL drops the reader first and creates it again after; every dialect's migration runs over rows, twice when idempotent.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_view_whose_body_changes_is_created_again_with_the_views_that_read_it(bool idempotent)
    {
        var scripts = await ViewBodyScriptsAsync(idempotent);
        var pg = scripts["postgres"][1];
        var ifExists = idempotent ? "IF EXISTS " : "";
        var dropReader = pg.IndexOf($"DROP VIEW {ifExists}sales.customer_order_totals;", StringComparison.Ordinal);
        var dropRead = pg.IndexOf($"DROP VIEW {ifExists}sales.customer_orders;", StringComparison.Ordinal);
        Assert.True(dropReader >= 0 && dropRead > dropReader, pg);
        var createRead = pg.IndexOf("VIEW sales.customer_orders AS", dropRead, StringComparison.Ordinal);
        Assert.True(createRead > 0 && pg.IndexOf("VIEW sales.customer_order_totals", createRead, StringComparison.Ordinal) > createRead, pg);
        Assert.DoesNotContain("TABLE", pg.Replace("Written once", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("active_customers", pg, StringComparison.Ordinal);

        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is not null)
        {
            var sqlite = scripts["sqlite"];
            var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1),
                sqlite[0] + Rows(null, "sqlite") + sqlite[1] + (idempotent ? sqlite[1] : ""));
            Assert.True(run.ExitCode == 0, run.Output);
        }

        foreach (var db in new[] { DbServer.Postgres(), DbServer.SqlServer(), DbServer.MySql(), DbServer.MariaDb() })
        {
            if (db is null)
                continue;
            var folder = db.Dialect switch { "postgresql" => "postgres", "sqlserver" => "sqlserver", _ => "mysql" };
            var schema = db.Dialect is "postgresql" or "sqlserver" ? "sales." : null;
            var twice = idempotent ? 2 : 1;
            await db.RunAsync("viewbody", [scripts[folder][0], Rows(schema, db.Dialect), .. Enumerable.Repeat(scripts[folder][1], twice)]);
        }
    }

    /// <summary>The first two migrations of every database: step 1, then the view-body step.</summary>
    private static async Task<Dictionary<string, string[]>> ViewBodyScriptsAsync(bool idempotent)
    {
        using var repo = DdlCoverage.Create(idempotent);
        await repo.GenerateCleanlyAsync();
        DdlCoverage.Write(repo, 1, DdlCoverage.ViewBody);
        await repo.GenerateCleanlyAsync();
        return DdlCoverage.Databases.ToDictionary(d => d.Name,
            d => new[] { repo.Read($"db/{d.Name}/migrations/0001.sql"), repo.Read($"db/{d.Name}/migrations/0002.sql") }, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Mysql_runs_an_expression_index_and_a_text_default()
    {
        var db = DbServer.MySql();
        if (db is null)
        {
            Assert.Skip("Name a MySQL container in the environment to run the DDL on it (see DbServer); MariaDB has no expression indexes.");
            return;
        }

        using var repo = DdlCoverage.Create(idempotent: false);
        DdlCoverage.Write(repo, 1, (d, path, node) =>
        {
            if (d.Dialect != "mysql" || path != "tables/customers")
                return;
            node["indexes"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = d.Id(47),
                ["name"] = "ix_customers_email_lower",
                ["columns"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["expression"] = new System.Text.Json.Nodes.JsonObject { ["*"] = "lower(email)" }, ["descending"] = true }),
            });
            foreach (var column in node["columns"]!.AsArray())
            {
                if (column!["name"]!.GetValue<string>() == "notes")
                    column["default"] = "none";
            }
        });
        await repo.GenerateCleanlyAsync();
        var schema = repo.Read("db/mysql/schema.sql");

        Assert.Contains("INDEX ix_customers_email_lower ((lower(email)) DESC)", schema, StringComparison.Ordinal);
        Assert.Contains("notes longtext NULL DEFAULT ('none')", schema, StringComparison.Ordinal);
        await db.RunAsync("expr", schema);
    }

    private sealed record Scripts(string First, string Final, IReadOnlyList<string> Migrations);

    /// <summary>The step 1 and final schema scripts of a database and its migrations, in order.</summary>
    private static async Task<Scripts> ScriptsAsync(string database, bool idempotent)
    {
        using var repo = DdlCoverage.Create(idempotent);
        await repo.GenerateCleanlyAsync();
        var first = repo.Read($"db/{database}/schema.sql");
        for (var step = 2; step <= DdlCoverage.Steps; step++)
        {
            DdlCoverage.Write(repo, step);
            await repo.GenerateCleanlyAsync();
        }

        var migrations = Directory.GetFiles(repo.PathOf($"db/{database}/migrations"), "*.sql").Order(StringComparer.Ordinal).Select(File.ReadAllText).ToList();
        return new Scripts(first, repo.Read($"db/{database}/schema.sql"), migrations);
    }

    /// <summary>A customer, an order and an order line, inserted after the first migration so every later one runs over rows.</summary>
    private static string Rows(string? schema, string dialect)
    {
        var s = schema ?? "";
        var go = dialect == "sqlserver" ? "GO\n" : "";
        // sqlcmd starts with QUOTED_IDENTIFIER off, which SQL Server refuses for writes to a table with a persisted computed column.
        var set = dialect == "sqlserver" ? "SET QUOTED_IDENTIFIER ON;" : "";
        return $"""
            {set}
            INSERT INTO {s}customers (code, name, email) VALUES ('C1', 'Ann', 'ann@example.com');
            INSERT INTO {s}orders (number, customer_id) SELECT 'N1', id FROM {s}customers WHERE code = 'C1';
            INSERT INTO {s}order_lines (order_id, line_no, product_code, quantity) SELECT id, 1, 'P1', 2 FROM {s}orders;
            {go}
            """;
    }

    /// <summary>A query printing the row counts as customers=n|orders=n|order_items=n.</summary>
    private static string Counts(string? schema, string dialect)
    {
        var s = schema ?? "";
        string Count(string table) => dialect switch
        {
            "sqlserver" => $"CAST((SELECT count(*) FROM {s}{table}) AS varchar(10))",
            _ => $"(SELECT count(*) FROM {s}{table})",
        };

        var parts = new[] { "'customers='", Count("customers"), "'|orders='", Count("orders"), "'|order_items='", Count("order_items") };
        var expression = dialect switch
        {
            "sqlserver" => string.Join(" + ", parts),
            "mysql" => "CONCAT(" + string.Join(", ", parts) + ")",
            _ => string.Join(" || ", parts),
        };
        return "\nSELECT " + expression + ";\n" + (dialect == "sqlserver" ? "GO\n" : "");
    }
}
