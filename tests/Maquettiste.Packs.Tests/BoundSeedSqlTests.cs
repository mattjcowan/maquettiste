using System.Text.RegularExpressions;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// sql-ddl's seed script for entities bound to designed tables (retire-projection-design.md section 6), on <see cref="BoundSeedModel"/>:
/// an entity seed writes its binding's write table (value object members at any depth), with table per type the base's table first with
/// the entity's type code, and a row per list item in each child table (owner key, position from 0); a relation seed writes its junction
/// table (each end's key, the attribute column its mapping names) or, stored on a foreign key, updates the dependent's row. The scripts
/// run twice on SQLite when sqlite3 is on the PATH, and on PostgreSQL and SQL Server when their containers are set. Binding an entity
/// that seeds through an entity mapping today to the same table writes the same rows.
/// </summary>
public sealed partial class BoundSeedSqlTests
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
        Assert.DoesNotContain("-- Seed rows,", seed, StringComparison.Ordinal); // nothing goes through a projected mapping

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

    // ---- the same rows through a mapping and through a binding --------------------------------------------------------------------

    private static string Id(int n) => "01KBSEQ" + n.ToString("D19", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Categories, parts and tools in designed tables of a PostgreSQL database: <c>categories (id, name)</c>, <c>parts (id, name, status,
    /// category_id)</c> (status an enum stored as its code, category a to-one end through <c>fk_parts_category</c>), <c>tools (id, name)</c>
    /// and the junction table <c>part_tools (part_id, tool_id)</c>, with seeds of each and of the relation Uses. With
    /// <paramref name="bound"/> each entity binds its table; without, an entity mapping maps it (the way that seeds through projection).
    /// </summary>
    private static PackRepo EquivalenceRepo(bool bound)
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        repo.Write(".maquettiste/maquettiste.json", """
            {
              "formatVersion": 1,
              "name": "seed-equivalence",
              "outputs": { "allow": [ { "path": "db" } ] },
              "packs": { "sql-ddl": { "output": "db" } }
            }

            """);
        var m = ".maquettiste/model/";
        var t = m + "databases/main/tables/";
        repo.Write(m + "packages/shop.json", $$"""{"kind":"package","id":"{{Id(1)}}","name":"Shop"}""");
        repo.Write(m + "databases/main/database.json", $$"""{"kind":"database","id":"{{Id(2)}}","name":"main","dialect":"postgresql","byConvention":"none"}""");
        repo.Write(m + "enums/part-status.json", $$"""
            {"kind":"enum","id":"{{Id(3)}}","name":"PartStatus","package":"{{Id(1)}}","members":[{"id":"{{Id(4)}}","name":"Active","code":"A"},{"id":"{{Id(5)}}","name":"Retired","code":"R"}]}
            """);
        repo.Write(t + "categories.json", $$"""
            {"kind":"table","id":"{{Id(10)}}","name":"categories","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(11)}}","name":"id","type":"int64","nullable":false},{"id":"{{Id(12)}}","name":"name","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{Id(11)}}"]} }
            """);
        repo.Write(t + "parts.json", $$"""
            {"kind":"table","id":"{{Id(20)}}","name":"parts","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(21)}}","name":"id","type":"uuid","nullable":false},{"id":"{{Id(22)}}","name":"name","type":"string","length":40,"nullable":false},
              {"id":"{{Id(23)}}","name":"status","type":"string","length":1,"nullable":false},{"id":"{{Id(24)}}","name":"category_id","type":"int64"}],
             "primaryKey":{"columns":["{{Id(21)}}"]},
             "foreignKeys":[{"id":"{{Id(25)}}","name":"fk_parts_category","columns":["{{Id(24)}}"],"referencesTable":"{{Id(10)}}"}]}
            """);
        repo.Write(t + "tools.json", $$"""
            {"kind":"table","id":"{{Id(30)}}","name":"tools","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(31)}}","name":"id","type":"int64","nullable":false},{"id":"{{Id(32)}}","name":"name","type":"string","length":40,"nullable":false}],
             "primaryKey":{"columns":["{{Id(31)}}"]} }
            """);
        repo.Write(t + "part-tools.json", $$"""
            {"kind":"table","id":"{{Id(40)}}","name":"part_tools","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(41)}}","name":"part_id","type":"uuid","nullable":false},{"id":"{{Id(42)}}","name":"tool_id","type":"int64","nullable":false}],
             "primaryKey":{"columns":["{{Id(41)}}","{{Id(42)}}"]},
             "foreignKeys":[{"id":"{{Id(43)}}","name":"fk_part_tools_part","columns":["{{Id(41)}}"],"referencesTable":"{{Id(20)}}"},
               {"id":"{{Id(44)}}","name":"fk_part_tools_tool","columns":["{{Id(42)}}"],"referencesTable":"{{Id(30)}}"}]}
            """);

        string Binding(int id, int table, params (int Attribute, int Column)[] fields) => bound
            ? $$""","bindings":[{"id":"{{Id(id)}}","database":"{{Id(2)}}","source":"{{Id(table)}}","fields":[{{string.Join(",", fields.Select(f => $$"""{"attribute":"{{Id(f.Attribute)}}","column":"{{Id(f.Column)}}"}"""))}}]}]"""
            : "";
        repo.Write(m + "entities/category.json", $$"""
            {"kind":"entity","id":"{{Id(100)}}","name":"Category","package":"{{Id(1)}}","key":{"attributes":["{{Id(101)}}"]},"attributes":[
              {"id":"{{Id(101)}}","name":"id","type":"int64","required":true},{"id":"{{Id(102)}}","name":"name","type":"string","length":40,"required":true}]{{Binding(103, 10, (101, 11), (102, 12))}}}
            """);
        repo.Write(m + "entities/part.json", $$"""
            {"kind":"entity","id":"{{Id(110)}}","name":"Part","package":"{{Id(1)}}","key":{"attributes":["{{Id(111)}}"]},"attributes":[
              {"id":"{{Id(111)}}","name":"id","type":"uuid","required":true},{"id":"{{Id(112)}}","name":"name","type":"string","length":40,"required":true},
              {"id":"{{Id(113)}}","name":"status","type":{"ref":"{{Id(3)}}"},"required":true}]{{Binding(114, 20, (111, 21), (112, 22), (113, 23), (141, 24))}}}
            """);
        repo.Write(m + "entities/tool.json", $$"""
            {"kind":"entity","id":"{{Id(120)}}","name":"Tool","package":"{{Id(1)}}","key":{"attributes":["{{Id(121)}}"]},"attributes":[
              {"id":"{{Id(121)}}","name":"id","type":"int64","required":true},{"id":"{{Id(122)}}","name":"name","type":"string","length":40,"required":true}]{{Binding(123, 30, (121, 31), (122, 32))}}}
            """);
        if (!bound)
        {
            foreach (var (name, entity, table, id) in new[] { ("category", 100, 10, 104), ("part", 110, 20, 115), ("tool", 120, 30, 124) })
                repo.Write(m + $"mappings/{name}-in-main.json", $$"""{"kind":"mapping","id":"{{Id(id)}}","name":"{{name}} in main","database":"{{Id(2)}}","entity":"{{Id(entity)}}","table":"{{Id(table)}}"}""");
        }

        repo.Write(m + "relations/classifies.json", $$"""
            {"kind":"relation","id":"{{Id(140)}}","name":"Classifies","package":"{{Id(1)}}","ends":[
              {"id":"{{Id(141)}}","entity":"{{Id(100)}}","role":"category","max":1},{"id":"{{Id(142)}}","entity":"{{Id(110)}}","role":"parts","navigation":"category","max":"*"}]}
            """);
        repo.Write(m + "mappings/classifies-in-main.json", $$"""{"kind":"mapping","id":"{{Id(143)}}","name":"classifies in main","database":"{{Id(2)}}","relation":"{{Id(140)}}","foreignKey":"{{Id(25)}}"}""");
        repo.Write(m + "relations/uses.json", $$"""
            {"kind":"relation","id":"{{Id(150)}}","name":"Uses","package":"{{Id(1)}}","ends":[
              {"id":"{{Id(151)}}","entity":"{{Id(110)}}","role":"part","navigation":"tools","max":"*"},{"id":"{{Id(152)}}","entity":"{{Id(120)}}","role":"tool","navigation":"parts","max":"*"}]}
            """);
        repo.Write(m + "mappings/uses-in-main.json", $$"""
            {"kind":"mapping","id":"{{Id(153)}}","name":"uses in main","database":"{{Id(2)}}","relation":"{{Id(150)}}","junctionTable":"{{Id(40)}}",
             "ends":[{"end":"{{Id(151)}}","foreignKey":"{{Id(43)}}"},{"end":"{{Id(152)}}","foreignKey":"{{Id(44)}}"}]}
            """);

        repo.Write(m + "seeds/category/categories.json", $$"""
            {"kind":"seed","id":"{{Id(200)}}","name":"categories","target":"{{Id(100)}}","columns":["{{Id(101)}}","{{Id(102)}}"],
             "rows":[{"id":"{{Id(201)}}","values":[1,"Fasteners"]},{"id":"{{Id(202)}}","values":[2,"Hinges"]}]}
            """);
        repo.Write(m + "seeds/part/parts.json", $$"""
            {"kind":"seed","id":"{{Id(210)}}","name":"parts","target":"{{Id(110)}}","columns":["{{Id(111)}}","{{Id(112)}}","{{Id(113)}}","{{Id(141)}}"],
             "rows":[{"id":"{{Id(211)}}","values":["0193a3c2-0000-7000-8000-000000000001","Bolt","Active","{{Id(201)}}"]},
               {"id":"{{Id(212)}}","values":["0193a3c2-0000-7000-8000-000000000002","Old hinge","Retired","{{Id(202)}}"]},
               {"id":"{{Id(213)}}","values":["0193a3c2-0000-7000-8000-000000000003","It's loose","A",null]}]}
            """);
        repo.Write(m + "seeds/tool/tools.json", $$"""
            {"kind":"seed","id":"{{Id(220)}}","name":"tools","target":"{{Id(120)}}","columns":["{{Id(121)}}","{{Id(122)}}"],
             "rows":[{"id":"{{Id(221)}}","values":[7,"Wrench"]},{"id":"{{Id(222)}}","values":[8,"Driver"]}]}
            """);
        repo.Write(m + "seeds/uses/uses.json", $$"""
            {"kind":"seed","id":"{{Id(230)}}","name":"uses","target":"{{Id(150)}}","columns":["{{Id(151)}}","{{Id(152)}}"],
             "rows":[{"id":"{{Id(231)}}","values":["{{Id(211)}}","{{Id(221)}}"]},{"id":"{{Id(232)}}","values":["{{Id(211)}}","{{Id(222)}}"]},
               {"id":"{{Id(233)}}","values":["{{Id(212)}}","{{Id(222)}}"]}]}
            """);
        return repo;
    }

    /// <summary>Each insert's table, column list and rows (its VALUES lines), by table.</summary>
    private static SortedDictionary<string, string> Inserts(string seed)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Insert().Matches(seed))
            result.Add(match.Groups["table"].Value, match.Groups["columns"].Value + "\n" + match.Groups["rows"].Value);
        return result;
    }

    [GeneratedRegex(@"INSERT INTO public\.(?<table>\w+) \((?<columns>[^)]*)\) VALUES\n(?<rows>(?:    \(.*\n)+)")]
    private static partial Regex Insert();

    [Fact]
    public async Task Binding_an_entity_that_seeds_through_a_mapping_to_the_same_table_writes_the_same_rows()
    {
        using var mapped = EquivalenceRepo(bound: false);
        using var bound = EquivalenceRepo(bound: true);
        await mapped.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        await bound.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var before = Inserts(mapped.Read("db/main/seed.sql"));
        var after = Inserts(bound.Read("db/main/seed.sql"));

        Assert.Equal(["categories", "part_tools", "parts", "tools"], before.Keys);
        Assert.Contains("id, name, status, category_id\n    ('0193a3c2-0000-7000-8000-000000000001', 'Bolt', 'A', 1),\n", before["parts"], StringComparison.Ordinal);
        Assert.Contains("    ('0193a3c2-0000-7000-8000-000000000003', 'It''s loose', 'A', NULL)\n", before["parts"], StringComparison.Ordinal);
        Assert.Equal(before, after);

        // The statements differ only in how a row already there is found: the mapping path's ON CONFLICT names no columns, the binding's
        // its row key; and the binding's rows go with the table seeds, by table in foreign key order.
        Assert.Contains("-- 3 rows into parts.\nINSERT INTO public.parts", mapped.Read("db/main/seed.sql"), StringComparison.Ordinal);
        Assert.Contains("-- 3 rows into parts, inserted when missing.\nINSERT INTO public.parts", bound.Read("db/main/seed.sql"), StringComparison.Ordinal);
    }
}
