using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// sql-ddl's seed script for entities bound to designed tables (retire-projection-design.md section 6), on <see cref="BoundSeedModel"/>:
/// an entity seed writes its binding's write table (value object members at any depth), with table per type the base's table first with
/// the entity's type code, and a row per list item in each child table (owner key, position from 0); a relation seed writes its junction
/// table (each end's key, the attribute column its mapping names) or, stored on a foreign key, updates the dependent's row. The scripts
/// run twice on SQLite when sqlite3 is on the PATH, and on PostgreSQL and SQL Server when their containers are set.
/// </summary>
public sealed class BoundSeedSqlTests
{
    private const string Rows = """
        customers=1:Ada:London,2:Grace:Arlington
        contacts=1.0:Bob:555-0100,1.1:Cy:-
        labels=1:early,1:vip,2:navy
        payments=100:payment:1:-,101:bank:-:-,102:card:2:card on file
        cards=102:4242
        favorites=1-10:1,1-11:2,2-10:1
        """;

    [Fact]
    public async Task Bound_entities_and_relations_seed_every_table_their_bindings_and_mappings_name()
    {
        using var repo = BoundSeedModel.Repo("sqlite");
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");

        // Value object members, two levels down; a missing value object leaves its columns NULL.
        Assert.Contains("-- 2 rows into customers, inserted when missing.\nINSERT INTO customers (id, name, city, geo_lat, geo_lng) VALUES\n"
            + "    (1, 'Ada', 'London', 51.5, -0.12),\n    (2, 'Grace', 'Arlington', NULL, NULL)\nON CONFLICT (id) DO NOTHING;", seed, StringComparison.Ordinal);
        // A list of a value object, with its position; a list of a string; an empty list has no rows.
        Assert.Contains("-- 2 rows into customer_contacts, inserted when missing.\nINSERT INTO customer_contacts (customer_id, position, name, phone) VALUES\n"
            + "    (1, 0, 'Bob', '555-0100'),\n    (1, 1, 'Cy', NULL)\nON CONFLICT (customer_id, position) DO NOTHING;", seed, StringComparison.Ordinal);
        Assert.Contains("-- 3 rows into customer_labels, inserted when missing.\nINSERT INTO customer_labels (customer_id, label) VALUES\n"
            + "    (1, 'vip'),\n    (1, 'early'),\n    (2, 'navy')\nON CONFLICT (customer_id, label) DO NOTHING;", seed, StringComparison.Ordinal);
        // One table for a base and a subtype, told apart by the type column; a table per type: the base's row first, with the card code.
        Assert.Contains("INSERT INTO payments (id, kind, amount, reference) VALUES\n    (101, 'bank', 40, 'REF-1')\nON CONFLICT (id) DO NOTHING;", seed, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO payments (id, kind, amount) VALUES\n    (102, 'card', 99.99)\nON CONFLICT (id) DO NOTHING;", seed, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO payments (id, kind, amount) VALUES\n    (100, 'payment', 12.5)\nON CONFLICT (id) DO NOTHING;", seed, StringComparison.Ordinal);
        Assert.Contains("-- 1 row into card_payments, inserted when missing.\nINSERT INTO card_payments (payment_id, last4) VALUES\n    (102, '4242')\n"
            + "ON CONFLICT (payment_id) DO NOTHING;", seed, StringComparison.Ordinal);
        // Links on a foreign key update the payments' rows, with the relation attribute's column; links through a junction table insert.
        Assert.Contains("-- 2 links set on rows of payments.\nUPDATE payments SET customer_id = 2, payer_note = 'card on file'\nWHERE id = 102;\n"
            + "UPDATE payments SET customer_id = 1, payer_note = NULL\nWHERE id = 100;", seed, StringComparison.Ordinal);
        Assert.Contains("-- 3 rows into customer_products, inserted when missing.\nINSERT INTO customer_products (customer_id, product_id, rank) VALUES\n"
            + "    (1, 10, 1),\n    (1, 11, 2),\n    (2, 10, 1)\nON CONFLICT (customer_id, product_id) DO NOTHING;", seed, StringComparison.Ordinal);

        // Referenced tables first: the base before its subtype's table, an owner before its child tables, inserts before the links' updates.
        Assert.True(seed.IndexOf("INTO payments", StringComparison.Ordinal) < seed.IndexOf("INTO card_payments", StringComparison.Ordinal));
        Assert.True(seed.IndexOf("INTO customers", StringComparison.Ordinal) < seed.IndexOf("INTO customer_contacts", StringComparison.Ordinal));
        Assert.True(seed.LastIndexOf("INTO payments", StringComparison.Ordinal) < seed.IndexOf("UPDATE payments", StringComparison.Ordinal));

        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
            return;
        var folder = repo.PathOf("db/main");
        var script = File.ReadAllText(Path.Combine(folder, "schema.sql")) + seed + seed + Queries("sqlite");
        var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1), script);
        Assert.True(run.ExitCode == 0, run.Output);
        AssertRows(run.Output);
    }

    [Fact]
    public async Task Sql_server_merges_the_rows_and_updates_the_links()
    {
        using var repo = BoundSeedModel.Repo("sqlserver");
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("MERGE INTO dbo.customer_contacts AS x\nUSING (VALUES\n    (1, 0, N'Bob', N'555-0100'),\n    (1, 1, N'Cy', NULL)) AS v (customer_id, position, name, phone)\n"
            + "ON x.customer_id = v.customer_id AND x.position = v.position\nWHEN NOT MATCHED THEN INSERT (customer_id, position, name, phone) VALUES (v.customer_id, v.position, v.name, v.phone);",
            seed, StringComparison.Ordinal);
        Assert.Contains("UPDATE dbo.payments SET customer_id = 2, payer_note = N'card on file'\nWHERE id = 102;", seed, StringComparison.Ordinal);

        if (DbServer.SqlServer() is not { } server)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_SQLSERVER_CONTAINER and MAQUETTISTE_TEST_SQLSERVER_PASSWORD to run the seed script on SQL Server.");
            return;
        }

        AssertRows(await server.RunAsync("bound_seeds", repo.Read("db/main/schema.sql"), seed, seed + Queries("sqlserver")));
    }

    [Fact]
    public async Task PostgreSQL_runs_the_seed_script_twice()
    {
        using var repo = BoundSeedModel.Repo("postgresql");
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("INSERT INTO public.card_payments (payment_id, last4) VALUES\n    (102, '4242')\nON CONFLICT (payment_id) DO NOTHING;", seed, StringComparison.Ordinal);

        if (DbServer.Postgres() is not { } server)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_POSTGRES_CONTAINER to run the seed script on PostgreSQL.");
            return;
        }

        AssertRows(await server.RunAsync("bound_seeds", repo.Read("db/main/schema.sql"), seed, seed + Queries("postgresql")));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("sqlserver")]
    public async Task A_child_table_whose_own_id_the_database_fills_is_found_again_by_a_unique_constraint_the_rows_give(string dialect)
    {
        using var repo = BoundSeedModel.Repo(dialect);
        // customer_labels gets its own identity id as its primary key, and a unique constraint on (customer_id, label).
        repo.EditJson(".maquettiste/model/databases/main/tables/customer-labels.json", t =>
        {
            t["columns"]!.AsArray().Insert(0, System.Text.Json.Nodes.JsonNode.Parse($$"""{"id":"{{BoundSeedModel.Id(44)}}","name":"id","type":"int64","nullable":false,"generated":"identity"}"""));
            t["primaryKey"] = System.Text.Json.Nodes.JsonNode.Parse($$"""{"columns":["{{BoundSeedModel.Id(44)}}"]}""");
            t["uniques"] = System.Text.Json.Nodes.JsonNode.Parse($$"""[{"id":"{{BoundSeedModel.Id(45)}}","name":"uq_customer_labels","columns":["{{BoundSeedModel.Id(41)}}","{{BoundSeedModel.Id(42)}}"]}]""");
        });
        repo.EditJson(".maquettiste/model/entities/customer.json", e =>
            e["bindings"]![0]!["childTables"]![1]!["columns"] = System.Text.Json.Nodes.JsonNode.Parse($$"""[{"column":"{{BoundSeedModel.Id(44)}}","status":"database"}]"""));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");

        if (dialect == "sqlserver")
        {
            Assert.Contains("ON x.customer_id = v.customer_id AND x.label = v.label\nWHEN NOT MATCHED THEN INSERT (customer_id, label) VALUES (v.customer_id, v.label);", seed, StringComparison.Ordinal);
            return;
        }

        Assert.Contains("INSERT INTO customer_labels (customer_id, label) VALUES\n    (1, 'vip'),\n    (1, 'early'),\n    (2, 'navy')\nON CONFLICT (customer_id, label) DO NOTHING;", seed, StringComparison.Ordinal);
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
            return;
        var folder = repo.PathOf("db/main");
        var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1),
            File.ReadAllText(Path.Combine(folder, "schema.sql")) + seed + seed + "\nSELECT 'labels=' || count(*) FROM customer_labels;\n");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("labels=3", run.Output, StringComparison.Ordinal);
    }

    /// <summary>The queries that read the rows back, one line per table, in the dialect's own words.</summary>
    private static string Queries(string dialect)
    {
        string Agg(string label, string expression, string table, string order) => dialect switch
        {
            "sqlite" => $"SELECT '{label}=' || group_concat({expression}, ',') FROM (SELECT * FROM {table} ORDER BY {order});",
            "postgresql" => $"SELECT '{label}=' || string_agg({expression}, ',' ORDER BY {order}) FROM {table};",
            _ => $"SELECT '{label}=' + STRING_AGG({expression}, ',') WITHIN GROUP (ORDER BY {order}) FROM {table};",
        };
        string Text(string column) => dialect == "sqlite" ? column : dialect == "postgresql" ? column + "::text" : "CAST(" + column + " AS varchar(40))";
        string Concat(params string[] parts) => dialect == "sqlite" ? string.Join(" || ", parts) : "CONCAT(" + string.Join(", ", parts) + ")";
        var go = dialect == "sqlserver" ? "\nGO\n" : "\n";
        return "\n" + string.Join("\n", new[]
        {
            Agg("customers", Concat("id", "':'", "name", "':'", "coalesce(city, '-')"), "customers", "id"),
            Agg("contacts", Concat("customer_id", "'.'", "position", "':'", "name", "':'", "coalesce(phone, '-')"), "customer_contacts", "customer_id, position"),
            Agg("labels", Concat("customer_id", "':'", "label"), "customer_labels", "customer_id, label"),
            Agg("payments", Concat("id", "':'", "kind", "':'", $"coalesce({Text("customer_id")}, '-')", "':'", "coalesce(payer_note, '-')"), "payments", "id"),
            Agg("cards", Concat("payment_id", "':'", "last4"), "card_payments", "payment_id"),
            Agg("favorites", Concat("customer_id", "'-'", "product_id", "':'", "rank"), "customer_products", "customer_id, product_id"),
        }) + go;
    }

    private static void AssertRows(string output)
    {
        foreach (var line in Rows.Split('\n'))
            Assert.Contains(line, output, StringComparison.Ordinal);
    }
}
