using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The sql-ddl pack's lifecycle parameters (lifecycle-design.md section 9), both off by default. <c>lifecycleColumns</c> adds the
/// subject's type, its id and, when the codes form a map, the current state's code to a lifecycle's instance table.
/// <c>lifecycleChecks</c> reconciles a check of the codes on the column a plain status binding holds, found through the entity's
/// mapping (SalesOrder in the processes fixture, string codes) or its binding (Customer in the bindings fixture, integer codes).
/// The scripts run twice on SQLite when sqlite3 is on the PATH, and on each database server whose container is set.
/// </summary>
public sealed class ProcessLifecycleSqlTests
{
    private const string Check = "ck_sales_orders_status_states";
    private const string CustomerCheck = "ck_customers_stage_states";

    /// <summary>
    /// The processes fixture with SalesOrder.status a string that holds the sales order lifecycle's top-level state as a code, in a
    /// main database of the given dialect, with the given sql-ddl parameters.
    /// </summary>
    private static PackRepo SalesOrders(string dialect, JsonObject? parameters)
    {
        var repo = PackRepo.Processes();
        repo.EditJson(".maquettiste/model/entities/sales-order.json", entity =>
        {
            var status = entity["attributes"]!.AsArray().Single(a => (string?)a!["name"] == "status")!;
            status["type"] = "string";
            status["length"] = 32;
            status["default"] = "draft";
        });
        var codes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Draft"] = "draft", ["CreditReview"] = "review", ["Fulfilment"] = "fulfilment", ["OnHold"] = "hold", ["Completed"] = "done", ["Cancelled"] = "cancelled",
        };
        repo.EditJson(".maquettiste/model/processes/sales-order-lifecycle.json", process =>
        {
            foreach (var state in process["states"]!.AsArray())
            {
                if (codes.TryGetValue((string)state!["name"]!, out var code))
                    state["code"] = code;
            }
        });
        Configure(repo, dialect, parameters);
        return repo;
    }

    /// <summary>
    /// The bindings fixture with a stage column (int16) in the customers table, bound to Customer.stage, which holds a customer
    /// lifecycle's state as an integer code: Prospect 1, Active 2, Closed 3.
    /// </summary>
    private static PackRepo Customers(string dialect, JsonObject? parameters)
    {
        var repo = PackRepo.Bindings();
        repo.EditJson(".maquettiste/model/databases/main/tables/customers.json", table =>
            table["columns"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01K6BFX0000000000000000090", "name": "stage", "type": "int16", "nullable": false, "default": 1 }""")));
        repo.EditJson(".maquettiste/model/entities/customer.json", entity =>
        {
            entity["attributes"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01K6BFX0000000000000000091", "name": "stage", "type": "int16", "required": true, "default": 1 }"""));
            entity["bindings"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse("""{ "attribute": "01K6BFX0000000000000000091", "column": "01K6BFX0000000000000000090" }"""));
        });
        repo.Write(".maquettiste/model/processes/customer-lifecycle.json", """
            {
              "$schema": "../../.schema/v1/process.json",
              "kind": "process",
              "id": "01K6BFX0000000000000000092",
              "name": "CustomerLifecycle",
              "package": "01K6BFX0000000000000000001",
              "use": "lifecycle",
              "subjects": [ { "id": "01K6BFX0000000000000000093", "entity": "01K6BFX0000000000000000032", "boundAttribute": "01K6BFX0000000000000000091" } ],
              "events": [ { "id": "01K6BFX0000000000000000094", "name": "activate" }, { "id": "01K6BFX0000000000000000095", "name": "close" } ],
              "states": [
                { "id": "01K6BFX0000000000000000096", "name": "Prospect", "code": 1 },
                { "id": "01K6BFX0000000000000000097", "name": "Active", "code": 2 },
                { "id": "01K6BFX0000000000000000098", "name": "Closed", "type": "final", "code": 3 }
              ],
              "initial": "01K6BFX0000000000000000096",
              "transitions": [
                { "id": "01K6BFX0000000000000000099", "source": "01K6BFX0000000000000000096", "event": "01K6BFX0000000000000000094", "targets": [ "01K6BFX0000000000000000097" ] },
                { "id": "01K6BFX00000000000000000A0", "source": "01K6BFX0000000000000000097", "event": "01K6BFX0000000000000000095", "targets": [ "01K6BFX0000000000000000098" ] }
              ]
            }

            """);
        Configure(repo, dialect, parameters);
        return repo;
    }

    private static void Configure(PackRepo repo, string dialect, JsonObject? parameters)
    {
        if (dialect != "postgresql")
        {
            repo.EditJson(".maquettiste/model/databases/main/database.json", database =>
            {
                database["dialect"] = dialect;
                database.AsObject().Remove("version");
            });
        }

        if (parameters is not null)
            repo.EditJson(".maquettiste/maquettiste.json", settings => settings["packs"]!["sql-ddl"]!["parameters"] = parameters);
    }

    [Fact]
    public async Task With_both_parameters_off_a_plain_status_binding_adds_no_column_and_no_check()
    {
        using var repo = SalesOrders("postgresql", new JsonObject { ["processTables"] = true });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.DoesNotContain("_states", repo.Read("db/main/seed.sql"), StringComparison.Ordinal);
        Assert.DoesNotContain("Lifecycle states", repo.Read("db/main/seed.sql"), StringComparison.Ordinal);
        Assert.DoesNotContain("subject_type", repo.Read("db/main/processes/sales-order-lifecycle.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_columns_add_the_subject_and_the_state_code_to_a_lifecycles_instances_table()
    {
        using (var mapped = SalesOrders("postgresql", new JsonObject { ["processTables"] = true, ["lifecycleColumns"] = true }))
        {
            await mapped.GenerateCleanlyAsync(packs: ["sql-ddl"]);
            var script = mapped.Read("db/main/processes/sales-order-lifecycle.sql");
            Assert.Contains("-- The instances table also holds the subject's type and id and the current state's code, because the lifecycleColumns parameter is true.\n\nCREATE TABLE", script, StringComparison.Ordinal);
            Assert.Contains("    updated_at timestamptz NOT NULL,\n    subject_type varchar(255) NULL,\n    subject_id varchar(255) NULL,\n    state_code varchar(255) NULL,\n    CONSTRAINT pk_sales_order_lifecycle_instances PRIMARY KEY (instance)", script, StringComparison.Ordinal);
            // Only the instances table, and only a lifecycle's: PurchaseApproval orchestrates.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "subject_type"));
            Assert.DoesNotContain("subject_type", mapped.Read("db/main/processes/purchase-approval.sql"), StringComparison.Ordinal);
            // The checks are a parameter of their own.
            Assert.DoesNotContain(Check, mapped.Read("db/main/seed.sql"), StringComparison.Ordinal);
        }

        using (var bound = Customers("sqlite", new JsonObject { ["processTables"] = true, ["lifecycleColumns"] = true }))
        {
            await bound.GenerateCleanlyAsync(packs: ["sql-ddl"]);
            Assert.Contains("    updated_at TEXT NOT NULL,\n    subject_type TEXT NULL,\n    subject_id TEXT NULL,\n    state_code INTEGER NULL,\n", bound.Read("db/main/processes/customer-lifecycle.sql"), StringComparison.Ordinal);
        }

        // A lifecycle without codes gets the subject's columns only.
        using var plain = PackRepo.Processes();
        plain.EditJson(".maquettiste/maquettiste.json", settings => settings["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["processTables"] = true, ["lifecycleColumns"] = true });
        await plain.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var noCodes = plain.Read("db/main/processes/sales-order-lifecycle.sql");
        Assert.Contains("-- The instances table also holds the subject's type and id, because the lifecycleColumns parameter is true.\n", noCodes, StringComparison.Ordinal);
        Assert.Contains("    subject_id varchar(255) NULL,\n    CONSTRAINT pk_sales_order_lifecycle_instances", noCodes, StringComparison.Ordinal);
        Assert.DoesNotContain("state_code", noCodes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lifecycle_checks_reconcile_a_check_of_the_codes_on_a_mapped_column()
    {
        using var repo = SalesOrders("postgresql", new JsonObject { ["lifecycleChecks"] = true });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("""
            -- Lifecycle states, reconciled on every run: these checks list the states' codes and are not in the schema snapshot.

            -- sales_orders.status (states of SalesOrderLifecycle).
            ALTER TABLE public.sales_orders DROP CONSTRAINT IF EXISTS ck_sales_orders_status_states;
            ALTER TABLE public.sales_orders ADD CONSTRAINT ck_sales_orders_status_states CHECK (status IN ('draft', 'review', 'fulfilment', 'hold', 'done', 'cancelled'));
            """.ReplaceLineEndings("\n"), seed, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE public.sales_orders", repo.Read("db/main/schema.sql"), StringComparison.Ordinal);
        // No process tables come with it.
        Assert.False(Directory.Exists(repo.PathOf("db/main/processes")));
    }

    [Theory]
    [InlineData("postgresql", "ALTER TABLE public.customers DROP CONSTRAINT IF EXISTS ck_customers_stage_states;\nALTER TABLE public.customers ADD CONSTRAINT ck_customers_stage_states CHECK (stage IN (1, 2, 3));")]
    [InlineData("sqlserver", "IF OBJECT_ID(N'dbo.ck_customers_stage_states', N'C') IS NOT NULL ALTER TABLE dbo.customers DROP CONSTRAINT ck_customers_stage_states;\nALTER TABLE dbo.customers ADD CONSTRAINT ck_customers_stage_states CHECK (stage IN (1, 2, 3));\nGO")]
    [InlineData("mysql", "SET @mq_sql = IF(EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'customers' AND constraint_name = 'ck_customers_stage_states'), 'ALTER TABLE customers DROP CONSTRAINT ck_customers_stage_states', 'DO 0');")]
    [InlineData("sqlite", "DROP TRIGGER IF EXISTS ck_customers_stage_states_i;\nDROP TRIGGER IF EXISTS ck_customers_stage_states_u;\nCREATE TRIGGER ck_customers_stage_states_i BEFORE INSERT ON customers WHEN NEW.stage IS NOT NULL AND NEW.stage NOT IN (1, 2, 3) BEGIN SELECT RAISE(ABORT, 'ck_customers_stage_states: customers.stage must be a state code of CustomerLifecycle'); END;")]
    [InlineData("oracle", "ALTER TABLE customers DROP CONSTRAINT IF EXISTS ck_customers_stage_states;\nALTER TABLE customers ADD CONSTRAINT ck_customers_stage_states CHECK (stage IN (1, 2, 3));")]
    public async Task Lifecycle_checks_reach_a_bound_column_in_every_dialect(string dialect, string expected)
    {
        using var repo = Customers(dialect, new JsonObject { ["lifecycleChecks"] = true });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("-- customers.stage (states of CustomerLifecycle).\n", seed, StringComparison.Ordinal);
        Assert.Contains(expected, seed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sqlite_runs_the_checks_twice_and_refuses_a_value_that_is_no_code()
    {
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        using var mapped = SalesOrders("sqlite", new JsonObject { ["lifecycleChecks"] = true });
        await mapped.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        using var bound = Customers("sqlite", new JsonObject { ["lifecycleChecks"] = true });
        await bound.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        if (sqlite3 is null)
        {
            Assert.Skip("sqlite3 is not on the PATH.");
            return;
        }

        foreach (var (repo, good, bad, check) in new[]
        {
            (mapped, "INSERT INTO sales_orders (id, status, total, credit_limit) VALUES ('a', 'review', 1, 1);\nUPDATE sales_orders SET status = 'done';\n",
                "UPDATE sales_orders SET status = 'Draft';\n", Check),
            (bound, "INSERT INTO customers (id, name, stage) VALUES ('a', 'A', 2);\nUPDATE customers SET stage = 3;\n", "UPDATE customers SET stage = 4;\n", CustomerCheck),
        })
        {
            var folder = repo.PathOf("db/main");
            var (schema, seed) = (File.ReadAllText(Path.Combine(folder, "schema.sql")), File.ReadAllText(Path.Combine(folder, "seed.sql")));
            Assert.Contains(check + "_i", seed, StringComparison.Ordinal);
            var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1), schema + seed + seed + good + "SELECT 'triggers=' || count(*) FROM sqlite_master WHERE type = 'trigger' AND name LIKE '" + check + "%';\n");
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("triggers=2", run.Output, StringComparison.Ordinal);
            var refused = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1), schema + seed + good + bad);
            Assert.NotEqual(0, refused.ExitCode);
            Assert.Contains(check, refused.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Two_lifecycles_that_bind_one_column_share_one_check_listing_both_sets_of_codes()
    {
        using var repo = Customers("postgresql", new JsonObject { ["lifecycleChecks"] = true });
        // VipCustomer is a second entity over the same customers table and stage column, with a lifecycle of its own.
        repo.Write(".maquettiste/model/entities/vip-customer.json", """
            {
              "$schema": "../../.schema/v1/entity.json",
              "kind": "entity",
              "id": "01K6BFX00000000000000000B6",
              "name": "VipCustomer",
              "package": "01K6BFX0000000000000000001",
              "key": { "attributes": [ "01K6BFX00000000000000000B7" ], "strategy": "uuid-v7" },
              "attributes": [
                { "id": "01K6BFX00000000000000000B7", "name": "id", "type": "uuid", "required": true },
                { "id": "01K6BFX00000000000000000B8", "name": "stage", "type": "int16", "required": true, "default": 3 }
              ],
              "bindings": [
                {
                  "id": "01K6BFX00000000000000000B9",
                  "database": "01K6BFX0000000000000000002",
                  "source": "01K6BFX0000000000000000013",
                  "fields": [
                    { "attribute": "01K6BFX00000000000000000B7", "column": "01K6BFX0000000000000000009" },
                    { "attribute": "01K6BFX00000000000000000B8", "column": "01K6BFX0000000000000000090" }
                  ]
                }
              ]
            }

            """);
        repo.Write(".maquettiste/model/processes/customer-support.json", """
            {
              "$schema": "../../.schema/v1/process.json",
              "kind": "process",
              "id": "01K6BFX00000000000000000B0",
              "name": "CustomerSupport",
              "package": "01K6BFX0000000000000000001",
              "use": "lifecycle",
              "subjects": [ { "id": "01K6BFX00000000000000000B1", "entity": "01K6BFX00000000000000000B6", "boundAttribute": "01K6BFX00000000000000000B8" } ],
              "events": [ { "id": "01K6BFX00000000000000000B2", "name": "escalate" } ],
              "states": [
                { "id": "01K6BFX00000000000000000B3", "name": "Open", "code": 3 },
                { "id": "01K6BFX00000000000000000B4", "name": "Escalated", "type": "final", "code": 4 }
              ],
              "initial": "01K6BFX00000000000000000B3",
              "transitions": [
                { "id": "01K6BFX00000000000000000B5", "source": "01K6BFX00000000000000000B3", "event": "01K6BFX00000000000000000B2", "targets": [ "01K6BFX00000000000000000B4" ] }
              ]
            }

            """);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("-- customers.stage (states of CustomerLifecycle, CustomerSupport).\n", seed, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT ck_customers_stage_states CHECK (stage IN (1, 2, 3, 4));", seed, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(seed, "ADD CONSTRAINT ck_customers_stage_states"));
    }

    [Fact]
    public async Task Tables_of_one_name_in_two_schemas_each_get_their_own_check()
    {
        using var repo = Customers("postgresql", new JsonObject { ["lifecycleChecks"] = true });
        // ArchivedCustomer keeps its stage in archive.customers, beside public.customers, and has a lifecycle of its own.
        repo.EditJson(".maquettiste/model/databases/main/database.json", database =>
            database["schemas"] = JsonNode.Parse("""[ { "id": "01K6BFX00000000000000000F0", "name": "archive" } ]"""));
        repo.Write(".maquettiste/model/databases/main/tables/archive-customers.json", """
            {
              "$schema": "../../../../.schema/v1/table.json",
              "kind": "table",
              "id": "01K6BFX00000000000000000C0",
              "name": "customers",
              "schema": "01K6BFX00000000000000000F0",
              "database": "01K6BFX0000000000000000002",
              "columns": [
                { "id": "01K6BFX00000000000000000C1", "name": "id", "type": "uuid", "nullable": false },
                { "id": "01K6BFX00000000000000000C2", "name": "stage", "type": "int16", "nullable": false, "default": 1 }
              ],
              "primaryKey": { "columns": [ "01K6BFX00000000000000000C1" ] }
            }

            """);
        repo.Write(".maquettiste/model/entities/archived-customer.json", """
            {
              "$schema": "../../.schema/v1/entity.json",
              "kind": "entity",
              "id": "01K6BFX00000000000000000C3",
              "name": "ArchivedCustomer",
              "package": "01K6BFX0000000000000000001",
              "key": { "attributes": [ "01K6BFX00000000000000000C4" ], "strategy": "uuid-v7" },
              "attributes": [
                { "id": "01K6BFX00000000000000000C4", "name": "id", "type": "uuid", "required": true },
                { "id": "01K6BFX00000000000000000C5", "name": "stage", "type": "int16", "required": true, "default": 1 }
              ],
              "bindings": [
                {
                  "id": "01K6BFX00000000000000000C6",
                  "database": "01K6BFX0000000000000000002",
                  "source": "01K6BFX00000000000000000C0",
                  "fields": [
                    { "attribute": "01K6BFX00000000000000000C4", "column": "01K6BFX00000000000000000C1" },
                    { "attribute": "01K6BFX00000000000000000C5", "column": "01K6BFX00000000000000000C2" }
                  ]
                }
              ]
            }

            """);
        repo.Write(".maquettiste/model/processes/archive-lifecycle.json", """
            {
              "$schema": "../../.schema/v1/process.json",
              "kind": "process",
              "id": "01K6BFX00000000000000000C7",
              "name": "ArchiveLifecycle",
              "package": "01K6BFX0000000000000000001",
              "use": "lifecycle",
              "subjects": [ { "id": "01K6BFX00000000000000000C8", "entity": "01K6BFX00000000000000000C3", "boundAttribute": "01K6BFX00000000000000000C5" } ],
              "events": [ { "id": "01K6BFX00000000000000000C9", "name": "restore" } ],
              "states": [
                { "id": "01K6BFX00000000000000000D0", "name": "Stored", "code": 7 },
                { "id": "01K6BFX00000000000000000D1", "name": "Restored", "type": "final", "code": 8 }
              ],
              "initial": "01K6BFX00000000000000000D0",
              "transitions": [
                { "id": "01K6BFX00000000000000000D2", "source": "01K6BFX00000000000000000D0", "event": "01K6BFX00000000000000000C9", "targets": [ "01K6BFX00000000000000000D1" ] }
              ]
            }

            """);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("ALTER TABLE public.customers ADD CONSTRAINT ck_customers_stage_states CHECK (stage IN (1, 2, 3));", seed, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE archive.customers ADD CONSTRAINT ck_customers_stage_states CHECK (stage IN (7, 8));", seed, StringComparison.Ordinal);
        Assert.Contains("-- customers.stage (states of CustomerLifecycle).\n", seed, StringComparison.Ordinal);
        Assert.Contains("-- customers.stage (states of ArchiveLifecycle).\n", seed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_column_found_through_a_binding_with_constants_gets_a_drop_and_no_check()
    {
        using var repo = Customers("postgresql", new JsonObject { ["lifecycleChecks"] = true });
        // CustomerNote shares the notes table (entity_type 'customer'), so the codes of one lifecycle need not cover its rows.
        repo.EditJson(".maquettiste/model/databases/main/tables/notes.json", table =>
            table["columns"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01K6BFX00000000000000000E0", "name": "phase", "type": "string", "length": 20, "nullable": true }""")));
        repo.EditJson(".maquettiste/model/entities/customer-note.json", entity =>
        {
            entity["attributes"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01K6BFX00000000000000000E1", "name": "phase", "type": "string", "length": 20 }"""));
            entity["bindings"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse("""{ "attribute": "01K6BFX00000000000000000E1", "column": "01K6BFX00000000000000000E0" }"""));
        });
        repo.Write(".maquettiste/model/processes/note-lifecycle.json", """
            {
              "$schema": "../../.schema/v1/process.json",
              "kind": "process",
              "id": "01K6BFX00000000000000000E2",
              "name": "NoteLifecycle",
              "package": "01K6BFX0000000000000000001",
              "use": "lifecycle",
              "subjects": [ { "id": "01K6BFX00000000000000000E3", "entity": "01K6BFX0000000000000000044", "boundAttribute": "01K6BFX00000000000000000E1" } ],
              "events": [ { "id": "01K6BFX00000000000000000E4", "name": "publish" } ],
              "states": [
                { "id": "01K6BFX00000000000000000E5", "name": "Draft", "code": "draft" },
                { "id": "01K6BFX00000000000000000E6", "name": "Published", "type": "final", "code": "published" }
              ],
              "initial": "01K6BFX00000000000000000E5",
              "transitions": [
                { "id": "01K6BFX00000000000000000E7", "source": "01K6BFX00000000000000000E5", "event": "01K6BFX00000000000000000E4", "targets": [ "01K6BFX00000000000000000E6" ] }
              ]
            }

            """);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var seed = repo.Read("db/main/seed.sql");
        Assert.Contains("-- notes.phase (states of NoteLifecycle): its table also holds rows of other entities, so it gets no check.\nALTER TABLE public.notes DROP CONSTRAINT IF EXISTS ck_notes_phase_states;\n", seed, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD CONSTRAINT ck_notes_phase_states", seed, StringComparison.Ordinal);
        // The customers check, whose table holds only customers, is still added.
        Assert.Contains("ADD CONSTRAINT ck_customers_stage_states", seed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    public async Task Servers_run_the_checks_twice(string server)
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
            Assert.Skip("Set the " + server + " test container to run the lifecycle checks on it.");
            return;
        }

        var customers = Customers(db.Dialect, new JsonObject { ["lifecycleChecks"] = true });
        // The fixture's notes.created_at defaults to CURRENT_TIMESTAMP, which MySQL refuses on a datetime(6) column.
        customers.EditJson(".maquettiste/model/databases/main/tables/notes.json", table =>
            table["columns"]!.AsArray().Single(c => (string?)c!["name"] == "created_at")!.AsObject().Remove("defaultSql"));
        foreach (var (repo, check) in new[] { (SalesOrders(db.Dialect, new JsonObject { ["lifecycleChecks"] = true }), Check), (customers, CustomerCheck) })
        {
            using (repo)
            {
                await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
                var (schema, seed) = (repo.Read("db/main/schema.sql"), repo.Read("db/main/seed.sql"));
                var count = db.Dialect switch
                {
                    "postgresql" => $"SELECT 'checks=' || count(*) FROM information_schema.check_constraints WHERE constraint_name = '{check}';\n",
                    "sqlserver" => $"SELECT 'checks=' + CAST(count(*) AS varchar(10)) FROM sys.check_constraints WHERE name = '{check}';\nGO\n",
                    _ => $"SELECT CONCAT('checks=', count(*)) FROM information_schema.check_constraints WHERE constraint_schema = DATABASE() AND constraint_name = '{check}';\n",
                };
                var output = await db.RunAsync("lifecycle_checks", schema, seed, seed, count);
                Assert.Contains("checks=1", output, StringComparison.Ordinal);
            }
        }
    }
}
