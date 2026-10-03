using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The DDL coverage model: one database per dialect (<c>postgres</c>, <c>sqlserver</c>, <c>mysql</c>, <c>sqlite</c>, <c>oracle</c>),
/// each with the same designed tables, a view and sequences, written in five steps so the sql-ddl pack writes a create script and a
/// migration for every kind of change: step 1 creates; step 2 renames, alters and adds (columns, keys, constraints, indexes, a table,
/// a view, a sequence); step 3 drops; step 4 renames the schema <c>sales</c> to <c>shop</c>, adds the schema <c>archive</c> and moves
/// a table into it; step 5 moves it back and drops <c>archive</c> (steps 4 and 5 change only the PostgreSQL and SQL Server databases,
/// the dialects with schemas here). Each database uses only what its dialect has (no MQ4056), so the scripts run as they are.
/// </summary>
internal static class DdlCoverage
{
    /// <summary>The last step.</summary>
    public const int Steps = 5;

    /// <summary>One database of the model.</summary>
    /// <param name="Name">The database name (its output folder).</param>
    /// <param name="Dialect">The dialect.</param>
    /// <param name="Index">The digit that tells its ids apart.</param>
    public sealed record Db(string Name, string Dialect, int Index)
    {
        /// <summary>Whether the database declares schemas (PostgreSQL and SQL Server here).</summary>
        public bool Schemas => Dialect is "postgresql" or "sqlserver";

        /// <summary>Whether the dialect has sequences.</summary>
        public bool Sequences => Dialect is "postgresql" or "sqlserver" or "oracle";

        /// <summary>Whether the dialect has deferrable foreign keys.</summary>
        public bool Deferrable => Dialect is "postgresql" or "sqlite" or "oracle";

        /// <summary>Whether the dialect has partial indexes.</summary>
        public bool Partial => Dialect is "postgresql" or "sqlserver" or "sqlite";

        /// <summary>Whether the dialect has index include columns.</summary>
        public bool Include => Dialect is "postgresql" or "sqlserver";

        /// <summary>The id of object <paramref name="n"/> of this database.</summary>
        public string Id(int n) => "01DD" + Index.ToString(CultureInfo.InvariantCulture) + n.ToString("D21", CultureInfo.InvariantCulture);
    }

    /// <summary>The databases, one per dialect.</summary>
    public static readonly IReadOnlyList<Db> Databases =
    [
        new("postgres", "postgresql", 1),
        new("sqlserver", "sqlserver", 2),
        new("mysql", "mysql", 3),
        new("sqlite", "sqlite", 4),
        new("oracle", "oracle", 5),
    ];

    /// <summary>A repo with the sql-ddl pack writing to <c>db</c>, its <c>idempotent</c> parameter as given, and the step 1 model.</summary>
    public static PackRepo Create(bool idempotent)
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Maquettiste.Testing.Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        var settings = new JsonObject
        {
            ["$schema"] = ".schema/v1/maquettiste.json",
            ["formatVersion"] = 1,
            ["name"] = "ddl",
            ["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "db" }) },
            ["packs"] = new JsonObject
            {
                ["sql-ddl"] = new JsonObject
                {
                    ["output"] = "db",
                    ["parameters"] = new JsonObject { ["idempotent"] = idempotent },
                },
            },
        };
        repo.Write(".maquettiste/maquettiste.json", Json(settings));
        Write(repo, 1);
        return repo;
    }

    /// <summary>
    /// Replaces the databases of the model with their state at a step; <paramref name="edit"/>, when given, changes an element (by
    /// database and path below the database folder, such as <c>tables/customers</c>) before it is written.
    /// </summary>
    public static void Write(PackRepo repo, int step, Action<Db, string, JsonObject>? edit = null)
    {
        var folder = repo.PathOf(".maquettiste/model/databases");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        foreach (var db in Databases)
        {
            var root = $".maquettiste/model/databases/{db.Name}";
            foreach (var (path, node) in Elements(db, step))
            {
                edit?.Invoke(db, path, node);
                // Each file is named after its element (MQ1005), so a renamed element moves to a new file.
                var folderName = path.Contains('/', StringComparison.Ordinal) ? path[..(path.IndexOf('/', StringComparison.Ordinal) + 1)] : "";
                var name = folderName.Length == 0 ? path : node["name"]!.GetValue<string>().Replace('_', '-');
                repo.Write($"{root}/{folderName}{name}.json", Json(node));
            }
        }
    }

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>The element files of one database at a step, by path below the database folder (without extension).</summary>
    private static IEnumerable<(string Path, JsonObject Node)> Elements(Db db, int step)
    {
        var d = db.Dialect;
        var database = new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/database.json",
            ["kind"] = "database",
            ["id"] = db.Id(1),
            ["name"] = db.Name,
            ["dialect"] = d,
            ["byConvention"] = "none",
        };
        if (db.Schemas)
        {
            var schemas = new JsonArray(new JsonObject { ["id"] = db.Id(2), ["name"] = step >= 4 ? "shop" : "sales" });
            if (step == 4)
                schemas.Add(new JsonObject { ["id"] = db.Id(3), ["name"] = "archive" });
            database["schemas"] = schemas;
        }

        yield return ("database", database);
        var sales = db.Schemas ? db.Id(2) : null;

        if (db.Sequences)
        {
            var orderNumbers = Element(db, "sequence", 10, step >= 2 ? "order_number_seq" : "order_numbers", sales);
            orderNumbers["type"] = "int64";
            orderNumbers["start"] = 1000;
            orderNumbers["increment"] = step >= 2 ? 5 : 1;
            if (step >= 2)
            {
                orderNumbers["max"] = 999999999;
                orderNumbers["cycle"] = true;
            }

            orderNumbers["cache"] = 20;
            yield return ("sequences/order-numbers", orderNumbers);
            if (step <= 2)
            {
                var audit = Element(db, "sequence", 11, "audit_numbers", sales);
                audit["type"] = "int32";
                yield return ("sequences/audit-numbers", audit);
            }
        }

        yield return ("tables/customers", Customers(db, step, sales));
        yield return ("tables/orders", Orders(db, step, sales));
        yield return ("tables/order-lines", OrderLines(db, step, step == 4 && db.Schemas ? db.Id(3) : sales));
        if (step == 2)
            yield return ("tables/notes", Notes(db, sales));
        // SQLite drops a table's triggers when it rebuilds the table: a trigger the model holds is created again.
        if (db.Dialect == "sqlite")
        {
            var trigger = Element(db, "sql-object", 120, "customers_touch", null);
            trigger["objectKind"] = "trigger";
            trigger["dependsOn"] = Ids(db, 20);
            trigger["body"] = new JsonObject { ["sqlite"] = "CREATE TRIGGER IF NOT EXISTS customers_touch AFTER UPDATE OF name ON customers BEGIN UPDATE customers SET status = status WHERE id = NEW.id; END;" };
            yield return ("objects/customers-touch", trigger);
        }

        if (step <= 2)
        {
            var listName = step == 2 ? "customer_order_list" : "customer_orders";
            var view = Element(db, "view", 90, listName, sales);
            var prefix = db.Schemas ? "sales." : "";
            view["body"] = new JsonObject { ["*"] = $"SELECT c.id, c.name, o.number FROM {prefix}customers c JOIN {prefix}orders o ON o.customer_id = c.id" };
            view["columns"] = new JsonArray(
                new JsonObject { ["name"] = "id", ["type"] = "int64", ["nullable"] = false },
                new JsonObject { ["name"] = "name", ["type"] = "string", ["nullable"] = false },
                new JsonObject { ["name"] = "number", ["type"] = "string", ["nullable"] = false });
            view["comment"] = step == 2 ? "Every customer with each order." : "Customers with their orders.";
            yield return ("views/customer-orders", view);

            // A view that reads the one above (a dependency found in its body), with a column list: created after it and dropped
            // before it, by name order the other way round.
            var totals = Element(db, "view", 91, "customer_order_totals", sales);
            totals["body"] = new JsonObject { ["*"] = $"SELECT id, count(*) FROM {prefix}{listName} GROUP BY id" };
            totals["columns"] = new JsonArray(
                new JsonObject { ["name"] = "customer_id", ["type"] = "int64", ["nullable"] = false },
                new JsonObject { ["name"] = "order_count", ["type"] = "int64", ["nullable"] = false });
            totals["columnList"] = true;
            yield return ("views/customer-order-totals", totals);

            // An updatable view with WITH CHECK OPTION (SQLite has none).
            var active = Element(db, "view", 92, "active_customers", sales);
            active["body"] = new JsonObject { ["*"] = $"SELECT id, code, status FROM {prefix}customers WHERE status = 'A'" };
            if (db.Dialect != "sqlite")
                active["withCheckOption"] = true;
            yield return ("views/active-customers", active);

            // A materialized view (PostgreSQL and Oracle), over a column step 2 retypes.
            if (db.Dialect is "postgresql" or "oracle")
            {
                var counts = Element(db, "view", 93, "order_counts", sales);
                counts["body"] = new JsonObject { ["*"] = $"SELECT customer_id, count(*) AS order_count FROM {prefix}orders GROUP BY customer_id" };
                counts["materialized"] = true;
                counts["comment"] = "Orders per customer, stored.";
                yield return ("views/order-counts", counts);
            }
        }
    }

    /// <summary>
    /// The view-body step, an edit for <see cref="Write"/> over step 1: the body of <c>customer_orders</c> changes (its columns do not),
    /// while <c>customer_order_totals</c>, which reads it, stays as it is; PostgreSQL refuses to drop the first while the second reads it.
    /// </summary>
    public static void ViewBody(Db db, string path, JsonObject node)
    {
        if (path != "views/customer-orders")
            return;
        var prefix = db.Schemas ? "sales." : "";
        node["body"] = new JsonObject { ["*"] = $"SELECT c.id, c.name, o.number FROM {prefix}customers c JOIN {prefix}orders o ON o.customer_id = c.id WHERE o.total >= 0" };
    }

    /// <summary>A column of the coverage model, for tests that edit a table (<see cref="Write"/>).</summary>
    public static JsonObject NewColumn(Db db, int n, string name, string type, Action<JsonObject>? more = null) => Column(db, n, name, type, more);

    private static JsonObject Element(Db db, string kind, int n, string name, string? schema)
    {
        var node = new JsonObject
        {
            ["$schema"] = $"../../../../.schema/v1/{kind}.json",
            ["kind"] = kind,
            ["id"] = db.Id(n),
            ["name"] = name,
            ["database"] = db.Id(1),
        };
        if (schema is not null)
            node["schema"] = schema;
        return node;
    }

    private static JsonObject Column(Db db, int n, string name, string type, Action<JsonObject>? more = null)
    {
        var column = new JsonObject { ["id"] = db.Id(n), ["name"] = name, ["type"] = type };
        more?.Invoke(column);
        return column;
    }

    private static JsonArray Ids(Db db, params int[] ns) => new([.. ns.Select(n => (JsonNode)db.Id(n))]);

    private static JsonObject Table(Db db, int n, string name, string? schema, JsonArray columns)
    {
        var table = Element(db, "table", n, name, schema);
        table["origin"] = "designed";
        table["columns"] = columns;
        return table;
    }

    private static string Now(Db db) => db.Dialect switch
    {
        "sqlserver" => "SYSUTCDATETIME()",
        "mysql" => "CURRENT_TIMESTAMP(6)",
        _ => "CURRENT_TIMESTAMP",
    };

    private static string? Collation(Db db) => db.Dialect switch
    {
        "postgresql" => "C",
        "sqlserver" => "Latin1_General_100_CI_AS",
        "mysql" => "utf8mb4_bin",
        "sqlite" => "NOCASE",
        _ => null,
    };

    /// <summary>
    /// customers: an identity key (clustered on SQL Server, with a seed where the dialect has one; step 2 widens it from int32 to int64,
    /// and on PostgreSQL and Oracle makes it generated always with a step of 2), a fixed-length single-byte code, a Unicode name with a
    /// collation (single-byte from step 3), a nullable text with a comment, a decimal with a named default (two fewer digits from step
    /// 3, read by a table check and a computed column), a literal default with a column check, a SQL default, a stored computed column;
    /// a unique constraint (nulls not distinct from step 2 on PostgreSQL), a table check, an index with a descending column (and include
    /// columns where the dialect has them), a partial index, an index with a method, an expression index (PostgreSQL, SQLite, Oracle),
    /// and a table comment. On SQL Server step 4 makes the primary key nonclustered, which orders' foreign key references.
    /// </summary>
    private static JsonObject Customers(Db db, int step, string? schema)
    {
        var columns = new JsonArray(
            Column(db, 21, "id", step >= 2 ? "int64" : "int32", c =>
            {
                c["nullable"] = false;
                c["generated"] = "identity";
                if (db.Dialect != "sqlite")
                {
                    var identity = new JsonObject { ["seed"] = 100 };
                    if (step >= 2 && db.Dialect is "postgresql" or "oracle")
                    {
                        identity["increment"] = 2;
                        identity["always"] = true;
                    }

                    c["identity"] = identity;
                }
            }),
            Column(db, 22, "code", "string", c => { c["length"] = 10; c["fixedLength"] = true; c["unicode"] = false; c["nullable"] = false; }),
            Column(db, 23, "name", "string", c =>
            {
                c["length"] = step >= 2 ? 150 : 100;
                c["unicode"] = step < 3;
                c["nullable"] = false;
                if (Collation(db) is { } collation)
                    c["collation"] = collation;
            }),
            Column(db, 24, "email", "string", c => { c["length"] = 200; c["nullable"] = step >= 2; }),
            Column(db, 25, step >= 2 ? "remarks" : "notes", "text", c => c["comment"] = step >= 2 ? "Remarks about the customer." : "Free-form notes."),
            Column(db, 26, "balance", "decimal", c =>
            {
                c["precision"] = step >= 3 ? 10 : 12;
                c["scale"] = 2;
                c["nullable"] = false;
                c["default"] = step >= 2 ? 10 : 0;
                c["defaultName"] = step >= 2 ? "customers_balance_dflt" : "customers_balance_default";
            }),
            Column(db, 27, "status", "string", c => { c["length"] = 1; c["nullable"] = false; c["default"] = "A"; }),
            Column(db, 28, "created_at", "datetime", c => { c["nullable"] = false; c["defaultSql"] = new JsonObject { [db.Dialect] = Now(db) }; }),
            Column(db, 29, "balance_twice", "decimal", c => { c["precision"] = 14; c["scale"] = 2; c["computed"] = "balance * 2"; c["computedStored"] = db.Dialect != "oracle"; }));
        if (step == 2)
        {
            columns.Add(Column(db, 30, "phone", "string", c => { c["length"] = 30; c["comment"] = "Contact phone."; }));
            columns.Add(Column(db, 32, "loyalty", "int32"));
        }

        if (step >= 2)
            columns.Add(Column(db, 31, "vip", "bool", c => { c["nullable"] = false; c["default"] = false; }));
        var table = Table(db, 20, "customers", schema, columns);
        var pk = new JsonObject { ["name"] = "pk_customers", ["columns"] = Ids(db, 21) };
        if (db.Dialect == "sqlserver")
            pk["clustered"] = step < 4;
        table["primaryKey"] = pk;
        var uniques = new JsonArray();
        if (step <= 2)
        {
            var unique = new JsonObject { ["id"] = db.Id(40), ["name"] = step >= 2 ? "uq_customers_mail" : "uq_customers_email", ["columns"] = Ids(db, 24) };
            if (step == 2 && db.Dialect == "postgresql")
                unique["nullsNotDistinct"] = true;
            uniques.Add(unique);
        }

        table["uniques"] = uniques;
        var checks = new JsonArray();
        if (step <= 2)
            checks.Add(new JsonObject { ["id"] = db.Id(41), ["name"] = "ck_customers_status", ["column"] = db.Id(27), ["expression"] = new JsonObject { ["*"] = "status IN ('A', 'I')" } });
        checks.Add(new JsonObject { ["id"] = db.Id(42), ["name"] = "ck_customers_balance", ["expression"] = new JsonObject { ["*"] = step >= 2 ? "balance >= -100" : "balance >= 0" } });
        table["checks"] = checks;
        var indexes = new JsonArray();
        var byName = new JsonObject
        {
            ["id"] = db.Id(43),
            ["name"] = "ix_customers_name",
            ["columns"] = new JsonArray(new JsonObject { ["column"] = db.Id(23) }, new JsonObject { ["column"] = db.Id(28), ["descending"] = true }),
        };
        if (db.Include)
            byName["include"] = Ids(db, 24);
        indexes.Add(byName);
        if (db.Partial && step <= 2)
            indexes.Add(new JsonObject { ["id"] = db.Id(44), ["name"] = "ix_customers_active", ["columns"] = new JsonArray(new JsonObject { ["column"] = db.Id(27) }), ["where"] = "status = 'A'" });
        var code = new JsonObject { ["id"] = db.Id(45), ["name"] = step >= 2 ? "ix_customers_code_lookup" : "ix_customers_code", ["columns"] = new JsonArray(new JsonObject { ["column"] = db.Id(22) }), ["unique"] = true };
        if (db.Dialect is "postgresql" or "mysql")
            code["method"] = "btree";
        indexes.Add(code);
        if (db.Dialect is "postgresql" or "sqlite" or "oracle")
        {
            indexes.Add(new JsonObject
            {
                ["id"] = db.Id(46),
                ["name"] = "ix_customers_email_lower",
                ["columns"] = new JsonArray(new JsonObject { ["expression"] = new JsonObject { ["*"] = "lower(email)" } }),
            });
        }

        table["indexes"] = indexes;
        table["comment"] = step >= 2 ? "Customers who place orders, with their balance." : "Customers who place orders.";
        return table;
    }

    /// <summary>
    /// orders: a key from a sequence (identity where the dialect has no sequences), a composite unique constraint, a foreign key with
    /// actions and (where the dialect has it) a deferral; step 2 renames the key, widens customer_id with customers.id from int32 to
    /// int64 (the foreign key itself unchanged, except its deferral where the dialect has one), changes an index's columns and adds a
    /// check; step 3 changes the foreign key's actions, shortens number and drops the index.
    /// </summary>
    private static JsonObject Orders(Db db, int step, string? schema)
    {
        var columns = new JsonArray(
            Column(db, 51, "id", "int64", c =>
            {
                c["nullable"] = false;
                if (db.Sequences)
                {
                    c["generated"] = "sequence";
                    c["sequence"] = db.Id(10);
                }
                else
                {
                    c["generated"] = "identity";
                }
            }),
            Column(db, 52, "number", "string", c => { c["length"] = step >= 3 ? 16 : 20; c["nullable"] = false; }),
            Column(db, 53, "customer_id", step >= 2 ? "int64" : "int32", c => c["nullable"] = false),
            Column(db, 54, "total", "decimal", c => { c["precision"] = 12; c["scale"] = 2; c["nullable"] = false; c["default"] = 0; }),
            Column(db, 55, "placed_on", "date"));
        var table = Table(db, 50, "orders", schema, columns);
        table["primaryKey"] = new JsonObject { ["name"] = step >= 2 ? "pk_orders_id" : "pk_orders", ["columns"] = Ids(db, 51) };
        table["uniques"] = new JsonArray(new JsonObject { ["id"] = db.Id(60), ["name"] = "uq_orders_customer_number", ["columns"] = Ids(db, 53, 52) });
        var fk = new JsonObject
        {
            ["id"] = db.Id(61),
            ["name"] = "fk_orders_customer",
            ["columns"] = Ids(db, 53),
            ["referencesTable"] = db.Id(20),
            ["onDelete"] = step >= 3 ? "no-action" : "cascade",
        };
        if (db.Deferrable)
            fk["deferrable"] = step >= 2 ? "initially-immediate" : "initially-deferred";
        table["foreignKeys"] = new JsonArray(fk);
        if (step >= 2)
            table["checks"] = new JsonArray(new JsonObject { ["id"] = db.Id(63), ["name"] = "ck_orders_total", ["expression"] = new JsonObject { ["*"] = "total >= 0" } });
        if (step <= 2)
        {
            var placed = new JsonArray(new JsonObject { ["column"] = db.Id(55), ["descending"] = true });
            if (step == 2)
                placed.Add(new JsonObject { ["column"] = db.Id(52) });
            table["indexes"] = new JsonArray(new JsonObject { ["id"] = db.Id(62), ["name"] = "ix_orders_placed_on", ["columns"] = placed });
        }

        return table;
    }

    /// <summary>
    /// order_lines: a composite primary key, a fixed-length code, a column check and a foreign key that cascades on delete and update
    /// (Oracle has no ON UPDATE); step 2 renames the table to order_items, renames a column, retypes another and, on PostgreSQL and
    /// Oracle, makes line_no an identity over the rows it holds.
    /// </summary>
    private static JsonObject OrderLines(Db db, int step, string? schema)
    {
        var columns = new JsonArray(
            Column(db, 71, "order_id", "int64", c => c["nullable"] = false),
            Column(db, 72, "line_no", "int32", c =>
            {
                c["nullable"] = false;
                if (step >= 2 && db.Dialect is "postgresql" or "oracle")
                    c["generated"] = "identity";
            }),
            Column(db, 73, step >= 2 ? "sku" : "product_code", "string", c => { c["length"] = 20; c["fixedLength"] = true; c["nullable"] = false; }),
            Column(db, 74, "quantity", step >= 2 ? "int64" : "int32", c => c["nullable"] = false));
        var table = Table(db, 70, step >= 2 ? "order_items" : "order_lines", schema, columns);
        table["primaryKey"] = new JsonObject { ["name"] = "pk_order_lines", ["columns"] = Ids(db, 71, 72) };
        table["foreignKeys"] = new JsonArray(new JsonObject
        {
            ["id"] = db.Id(80),
            ["name"] = "fk_order_lines_order",
            ["columns"] = Ids(db, 71),
            ["referencesTable"] = db.Id(50),
            ["onDelete"] = "cascade",
            ["onUpdate"] = db.Dialect == "oracle" ? "no-action" : "cascade",
        });
        table["checks"] = new JsonArray(new JsonObject { ["id"] = db.Id(81), ["name"] = "ck_order_lines_quantity", ["column"] = db.Id(74), ["expression"] = new JsonObject { ["*"] = "quantity > 0" } });
        table["indexes"] = new JsonArray(new JsonObject { ["id"] = db.Id(82), ["name"] = "ix_order_lines_product", ["columns"] = new JsonArray(new JsonObject { ["column"] = db.Id(73) }) });
        return table;
    }

    /// <summary>
    /// notes (step 2 only): a table added and dropped again, with a foreign key that sets null, and a text with a literal default (in
    /// parentheses on MySQL) and, on MySQL, an index with a key prefix length on it.
    /// </summary>
    private static JsonObject Notes(Db db, string? schema)
    {
        var table = Table(db, 100, "notes", schema, new JsonArray(
            Column(db, 101, "id", "int64", c => { c["nullable"] = false; c["generated"] = "identity"; }),
            Column(db, 102, "customer_id", "int64"),
            Column(db, 103, "body", "text", c => { c["nullable"] = false; c["default"] = "-"; })));
        table["primaryKey"] = new JsonObject { ["name"] = "pk_notes", ["columns"] = Ids(db, 101) };
        table["foreignKeys"] = new JsonArray(new JsonObject
        {
            ["id"] = db.Id(110),
            ["name"] = "fk_notes_customer",
            ["columns"] = Ids(db, 102),
            ["referencesTable"] = db.Id(20),
            ["onDelete"] = "set-null",
        });
        var indexes = new JsonArray(new JsonObject { ["id"] = db.Id(111), ["name"] = "ix_notes_customer", ["columns"] = new JsonArray(new JsonObject { ["column"] = db.Id(102) }) });
        if (db.Dialect == "mysql")
            indexes.Add(new JsonObject { ["id"] = db.Id(112), ["name"] = "ix_notes_body", ["columns"] = new JsonArray(new JsonObject { ["column"] = db.Id(103), ["length"] = 50 }) });
        table["indexes"] = indexes;
        return table;
    }
}
