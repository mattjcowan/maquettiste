using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Migrations over the DDL coverage model (<see cref="DdlCoverage"/>, step 1 with an edit, then the same with a second edit) that a
/// database would refuse without the pack's care: a unique key or unique index that foreign keys rely on, recreated (the foreign
/// keys go first and come back after); a sequence moved to another schema by an idempotent migration run twice; a scale that rises
/// at the same precision (a TODO); a view whose snapshot predates recorded dependencies (dropped in the model's dependency order).
/// The scripts run on SQLite (sqlite3 on the PATH) and on the servers <see cref="DbServer"/> finds.
/// </summary>
public sealed class MigrationSafetyTests
{
    /// <summary>The first and second migration of every database, after step 1 with <paramref name="first"/>, then with <paramref name="second"/>.</summary>
    private static async Task<Dictionary<string, string[]>> MigrationsAsync(bool idempotent, Action<DdlCoverage.Db, string, JsonObject> first,
        Action<DdlCoverage.Db, string, JsonObject> second)
    {
        using var repo = DdlCoverage.Create(idempotent);
        DdlCoverage.Write(repo, 1, first);
        await repo.GenerateCleanlyAsync();
        DdlCoverage.Write(repo, 1, second);
        await repo.GenerateCleanlyAsync();
        return DdlCoverage.Databases.ToDictionary(d => d.Name, d => new[] { repo.Read($"db/{d.Name}/migrations/0001.sql"), Second(repo, d.Name) },
            StringComparer.Ordinal);

        static string Second(PackRepo repo, string name)
        {
            var path = repo.PathOf($"db/{name}/migrations/0002.sql");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
    }

    /// <summary>Runs the first migration, a customer row, and the second migration (twice when idempotent) wherever it can.</summary>
    private static async Task RunEverywhereAsync(Dictionary<string, string[]> scripts, bool idempotent, string label)
    {
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is not null)
        {
            var sqlite = scripts["sqlite"];
            var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], Environment.CurrentDirectory, TimeSpan.FromMinutes(1),
                sqlite[0] + Customer(null) + sqlite[1]);
            Assert.True(run.ExitCode == 0, run.Output + "\n--- script ---\n" + sqlite[1]);
        }

        foreach (var db in new[] { DbServer.Postgres(), DbServer.SqlServer(), DbServer.MySql(), DbServer.MariaDb() })
        {
            if (db is null)
                continue;
            var folder = db.Dialect switch { "postgresql" => "postgres", "sqlserver" => "sqlserver", _ => "mysql" };
            var schema = db.Dialect is "postgresql" or "sqlserver" ? "sales." : null;
            var customer = Customer(schema) + (db.Dialect == "sqlserver" ? "GO\n" : "");
            if (db.Dialect == "sqlserver")
                customer = "SET QUOTED_IDENTIFIER ON;\n" + customer;
            await db.RunAsync(label, [scripts[folder][0], customer, .. Enumerable.Repeat(scripts[folder][1], idempotent ? 2 : 1)]);
        }
    }

    private static string Customer(string? schema) => $"\nINSERT INTO {schema}customers (code, name, email) VALUES ('C1', 'Ann', 'ann@example.com');\n";

    /// <summary>orders references customers' unique index on code and (except SQLite) its unique constraint on email.</summary>
    private static void ReferencesUniqueKeys(DdlCoverage.Db db, string path, JsonObject node)
    {
        if (path != "tables/orders" || db.Dialect == "oracle")
            return;
        node["columns"]!.AsArray().Add(DdlCoverage.NewColumn(db, 56, "customer_code", "string", c => { c["length"] = 10; c["fixedLength"] = true; c["unicode"] = false; }));
        node["columns"]!.AsArray().Add(DdlCoverage.NewColumn(db, 57, "customer_email", "string", c => c["length"] = 200));
        var fks = node["foreignKeys"]!.AsArray();
        fks.Add(new JsonObject { ["id"] = db.Id(64), ["name"] = "fk_orders_customer_code", ["columns"] = new JsonArray(db.Id(56)), ["referencesTable"] = db.Id(20), ["referencesColumns"] = new JsonArray(db.Id(22)) });
        fks.Add(new JsonObject { ["id"] = db.Id(65), ["name"] = "fk_orders_customer_email", ["columns"] = new JsonArray(db.Id(57)), ["referencesTable"] = db.Id(20), ["referencesColumns"] = new JsonArray(db.Id(24)) });
    }

    /// <summary>
    /// The unique index on code becomes descending (a new index), and on PostgreSQL the unique constraint on email treats nulls as
    /// equal (the same key, recreated): the foreign keys that rely on them are dropped before and added back after.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recreating_a_unique_key_drops_the_foreign_keys_that_rely_on_it_first_and_adds_them_back(bool idempotent)
    {
        var scripts = await MigrationsAsync(idempotent, ReferencesUniqueKeys, (db, path, node) =>
        {
            ReferencesUniqueKeys(db, path, node);
            if (path != "tables/customers" || db.Dialect == "oracle")
                return;
            foreach (var index in node["indexes"]!.AsArray())
            {
                if (index!["id"]!.GetValue<string>() == db.Id(45))
                    index["columns"]![0]!["descending"] = true;
            }

            if (db.Dialect == "postgresql")
                node["uniques"]![0]!["nullsNotDistinct"] = true;
        });

        foreach (var (name, dropKey) in new[] { ("postgres", "DROP INDEX"), ("sqlserver", "DROP INDEX"), ("mysql", "DROP INDEX ix_customers_code") })
        {
            var migration = scripts[name][1];
            var dropFk = migration.IndexOf("fk_orders_customer_code", StringComparison.Ordinal);
            var drop = migration.IndexOf(dropKey, StringComparison.Ordinal);
            var add = migration.LastIndexOf("fk_orders_customer_code", StringComparison.Ordinal);
            var create = migration.IndexOf("ix_customers_code", drop, StringComparison.Ordinal);
            Assert.True(dropFk >= 0 && drop > dropFk && add > create && create > drop, name + ":\n" + migration);
        }

        var pg = scripts["postgres"][1];
        Assert.True(pg.IndexOf("DROP CONSTRAINT " + (idempotent ? "IF EXISTS " : "") + "fk_orders_customer_email", StringComparison.Ordinal)
            < pg.IndexOf("DROP CONSTRAINT " + (idempotent ? "IF EXISTS " : "") + "uq_customers_email", StringComparison.Ordinal), pg);
        Assert.Contains("ADD CONSTRAINT fk_orders_customer_email FOREIGN KEY", pg, StringComparison.Ordinal);
        await RunEverywhereAsync(scripts, idempotent, "uniquefk");
    }

    /// <summary>A sequence moves to another schema: an idempotent migration finds it moved on its second run and moves nothing.</summary>
    [Fact]
    public async Task An_idempotent_migration_moving_a_sequence_runs_twice()
    {
        static void Archive(DdlCoverage.Db db, string path, JsonObject node)
        {
            if (path == "database" && db.Schemas)
                node["schemas"]!.AsArray().Add(new JsonObject { ["id"] = db.Id(3), ["name"] = "archive" });
        }

        var scripts = await MigrationsAsync(true, Archive, (db, path, node) =>
        {
            Archive(db, path, node);
            if (path == "sequences/audit-numbers" && db.Schemas)
                node["schema"] = db.Id(3);
        });

        Assert.Contains("ALTER SEQUENCE IF EXISTS sales.audit_numbers SET SCHEMA archive;", scripts["postgres"][1], StringComparison.Ordinal);
        Assert.Contains("ALTER SCHEMA archive TRANSFER sales.audit_numbers;", scripts["sqlserver"][1], StringComparison.Ordinal);
        await RunEverywhereAsync(scripts, idempotent: true, "seqmove");
    }

    /// <summary>
    /// A scale that rises at the same precision leaves fewer digits before the point: (12,2) to (12,4) gets the narrowing TODO with its
    /// check; a precision that rises as much does not.
    /// </summary>
    [Theory]
    [InlineData(12, true)]
    [InlineData(14, false)]
    public async Task A_rising_scale_without_as_much_more_precision_gets_a_narrowing_todo(int precision, bool narrows)
    {
        var scripts = await MigrationsAsync(false, (_, _, _) => { }, (db, path, node) =>
        {
            if (path != "tables/customers")
                return;
            foreach (var column in node["columns"]!.AsArray())
            {
                if (column!["name"]!.GetValue<string>() == "balance")
                {
                    column["precision"] = precision;
                    column["scale"] = 4;
                }
            }
        });

        var pg = scripts["postgres"][1];
        const string Todo = "-- TODO: customers.balance holds fewer digits before the point (precision 12 -> 12, scale 2 -> 4); larger values are refused. Check first: SELECT count(*) FROM sales.customers WHERE abs(balance) >= 100000000;";
        if (narrows)
            Assert.Contains(Todo, pg, StringComparison.Ordinal);
        else
            Assert.DoesNotContain("fewer digits", pg, StringComparison.Ordinal);
        Assert.Equal(narrows, scripts["sqlserver"][1].Contains("holds fewer digits before the point", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first migration after an upgrade reads a snapshot written before views recorded what they read: customer_orders and the two
    /// views that read it change (one, early_orders, before it by key, so the key order alone would drop it last), and PostgreSQL still
    /// drops both readers before it (the model's dependencies).
    /// </summary>
    [Fact]
    public async Task Views_of_a_snapshot_without_recorded_dependencies_drop_in_the_models_dependency_order()
    {
        static JsonObject EarlyOrders(DdlCoverage.Db db, string where) => new()
        {
            ["$schema"] = "../../../../.schema/v1/view.json",
            ["kind"] = "view",
            ["id"] = db.Id(89),
            ["name"] = "early_orders",
            ["database"] = db.Id(1),
            ["schema"] = db.Id(2),
            ["body"] = new JsonObject { ["*"] = "SELECT id, number FROM sales.customer_orders" + where },
        };

        static void Reader(PackRepo repo, string where)
        {
            var db = DdlCoverage.Databases[0];
            repo.Write(".maquettiste/model/databases/postgres/views/early-orders.json", EarlyOrders(db, where).ToJsonString());
        }

        static void Bodies(DdlCoverage.Db db, string path, JsonObject node)
        {
            DdlCoverage.ViewBody(db, path, node);
            if (path == "views/customer-order-totals")
                node["body"] = new JsonObject { ["*"] = $"SELECT id, count(*) FROM {(db.Schemas ? "sales." : "")}customer_orders GROUP BY id HAVING count(*) > 0" };
        }

        using var repo = DdlCoverage.Create(idempotent: false);
        Reader(repo, "");
        await repo.GenerateCleanlyAsync();
        var path = ".maquettiste/snapshots/postgres.json";
        var snapshot = JsonNode.Parse(repo.Read(path))!;
        var stripped = 0;
        foreach (var view in snapshot["views"]!.AsArray())
            stripped += view!.AsObject().Remove("dependsOn") ? 1 : 0;
        repo.Write(path, snapshot.ToJsonString());
        DdlCoverage.Write(repo, 1, Bodies);
        Reader(repo, " WHERE id > 0");
        await repo.GenerateCleanlyAsync();

        Assert.Equal(2, stripped);
        var pg = repo.Read("db/postgres/migrations/0002.sql");
        var read = pg.IndexOf("DROP VIEW sales.customer_orders;", StringComparison.Ordinal);
        Assert.True(read > pg.IndexOf("DROP VIEW sales.customer_order_totals;", StringComparison.Ordinal) && read > pg.IndexOf("DROP VIEW sales.early_orders;", StringComparison.Ordinal)
            && pg.IndexOf("DROP VIEW sales.early_orders;", StringComparison.Ordinal) >= 0, pg);
        var db = DbServer.Postgres();
        if (db is not null)
            await db.RunAsync("olddeps", repo.Read("db/postgres/migrations/0001.sql"), Customer("sales."), pg);
    }

    /// <summary>SQL Server: a migration with GO batches ends by rolling back what a compile error in its last batch left open.</summary>
    [Fact]
    public async Task A_sql_server_migration_with_batches_rolls_back_a_transaction_its_last_batch_left_open()
    {
        var scripts = await MigrationsAsync(false, (_, _, _) => { }, DdlCoverage.ViewBody);

        Assert.EndsWith("COMMIT TRANSACTION;\nGO\n-- A compile error in the last batch skips its COMMIT and leaves the transaction open: roll it back.\nIF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\nSET NOEXEC OFF;\n",
            scripts["sqlserver"][1], StringComparison.Ordinal);
    }
}
