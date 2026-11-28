using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// SQL object environments and run-once in sql-ddl (next-phase-1-design.md section 3.4): an object with environments runs only in
/// <c>&lt;db&gt;/objects.&lt;env&gt;.sql</c> (unit <c>object-environments</c>), one per environment the pack's <c>environments</c>
/// parameter names, on every deploy; a run-once object runs inside a ledger guard and records itself, in every script that holds it.
/// One PostgreSQL database <c>billing</c> (schema <c>billing</c>) with a table <c>invoices</c>; the server case skips unless
/// <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c> names a running container.
/// </summary>
public sealed class SqlObjectEnvironmentTests
{
    internal const string DevTools = "01SE0000000000000000000020";
    internal const string DevInvoices = "01SE0000000000000000000021";
    internal const string TotalIndex = "01SE0000000000000000000022";
    internal const string AuditOnce = "01SE0000000000000000000023";
    internal const string DevOnce = "01SE0000000000000000000024";
    internal const string Grants = "01SE0000000000000000000025";

    private const string Skip = "Name a PostgreSQL container in the environment to run the ledger on it (see DbServer).";

    private static string Id(int n) => "01SE" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>
    /// A repo with the sql-ddl pack writing to <c>db</c> (environments dev, test and staging), the database <c>billing</c>
    /// (PostgreSQL 16 unless another dialect is given) and its table <c>invoices</c>.
    /// </summary>
    internal static PackRepo Create(bool objectScripts = false, string dialect = "postgresql")
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        var parameters = new JsonObject { ["environments"] = new JsonArray("dev", "test", "staging") };
        if (objectScripts)
            parameters["objectScripts"] = true;
        repo.Write(".maquettiste/maquettiste.json", Json(new JsonObject
        {
            ["$schema"] = ".schema/v1/maquettiste.json",
            ["formatVersion"] = 1,
            ["name"] = "object-environments",
            ["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "db" }) },
            ["packs"] = new JsonObject { ["sql-ddl"] = new JsonObject { ["output"] = "db", ["parameters"] = parameters } },
        }));
        var database = new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/database.json",
            ["kind"] = "database",
            ["id"] = Id(1),
            ["name"] = "billing",
            ["dialect"] = dialect,
        };
        if (dialect == "postgresql")
            database["version"] = "16";
        if (dialect != "sqlite")
            database["schemas"] = new JsonArray(new JsonObject { ["id"] = Id(2), ["name"] = "billing" });
        database["byConvention"] = "none";
        repo.Write(".maquettiste/model/databases/billing/database.json", Json(database));
        var table = new JsonObject
        {
            ["$schema"] = "../../../../.schema/v1/table.json",
            ["kind"] = "table",
            ["id"] = Id(10),
            ["name"] = "invoices",
            ["database"] = Id(1),
        };
        if (dialect != "sqlite")
            table["schema"] = Id(2);
        table["columns"] = new JsonArray(
            new JsonObject { ["id"] = Id(11), ["name"] = "id", ["type"] = "int64", ["nullable"] = false },
            new JsonObject { ["id"] = Id(12), ["name"] = "total", ["type"] = "decimal", ["precision"] = 12, ["scale"] = 2 });
        table["primaryKey"] = new JsonObject { ["name"] = "pk_invoices", ["columns"] = new JsonArray(Id(11)) };
        repo.Write(".maquettiste/model/databases/billing/tables/invoices.json", Json(table));
        return repo;
    }

    /// <summary>Writes a SQL object of <c>billing</c> (schema <c>billing</c> unless the database has none).</summary>
    internal static void Object(PackRepo repo, string id, string name, string body, string[]? environments = null, bool once = false, string? phase = null,
        string[]? dependsOn = null, string dialect = "postgresql")
    {
        var o = new JsonObject
        {
            ["$schema"] = "../../../../.schema/v1/sql-object.json",
            ["kind"] = "sql-object",
            ["id"] = id,
            ["name"] = name,
            ["database"] = Id(1),
        };
        if (dialect != "sqlite")
            o["schema"] = Id(2);
        o["objectKind"] = "script";
        if (phase is not null)
            o["phase"] = phase;
        if (environments is not null)
            o["environments"] = new JsonArray([.. environments.Select(e => JsonValue.Create(e))]);
        if (once)
            o["apply"] = "once";
        if (dependsOn is not null)
            o["dependsOn"] = new JsonArray([.. dependsOn.Select(e => JsonValue.Create(e))]);
        o["body"] = new JsonObject { ["*"] = body };
        repo.Write($".maquettiste/model/databases/billing/objects/{name.Replace('_', '-')}.json", Json(o));
    }

    /// <summary>
    /// The golden model's objects: grants that run everywhere; a dev schema (phase before) and a dev view on the table, an index for
    /// dev and test; a run-once audit table everywhere and a run-once dev table, each made by two statements.
    /// </summary>
    internal static void Objects(PackRepo repo, string dialect = "postgresql")
    {
        Object(repo, Grants, "invoice_grants", "GRANT SELECT ON billing.invoices TO PUBLIC;", dependsOn: [Id(10)], dialect: dialect);
        Object(repo, DevTools, "dev_tools", "CREATE SCHEMA IF NOT EXISTS dev_tools;", ["dev"], phase: "before", dialect: dialect);
        Object(repo, DevInvoices, "dev_invoices", "CREATE OR REPLACE VIEW dev_tools.invoices AS SELECT id, total FROM billing.invoices;", ["dev"],
            dependsOn: [Id(10), DevTools], dialect: dialect);
        Object(repo, TotalIndex, "invoice_total_index", "CREATE INDEX IF NOT EXISTS ix_invoices_total ON billing.invoices (total);", ["dev", "test"],
            dependsOn: [Id(10)], dialect: dialect);
        Object(repo, AuditOnce, "invoice_audit", "CREATE TABLE billing.invoice_audit (id bigint NOT NULL);\nINSERT INTO billing.invoice_audit (id) VALUES (1);",
            once: true, dependsOn: [Id(10)], dialect: dialect);
        Object(repo, DevOnce, "dev_runs", "CREATE TABLE dev_tools.runs (n integer NOT NULL);\nINSERT INTO dev_tools.runs (n) VALUES (1);", ["dev"], once: true,
            dependsOn: [DevTools], dialect: dialect);
    }

    /// <summary>The ledger guard sql-ddl writes for a run-once object, up to its statements.</summary>
    internal static string Guard(string id) =>
        "DO $once$ BEGIN\n    IF NOT EXISTS (SELECT 1 FROM migration_ledger.applied_object WHERE element_id = '" + id + "') THEN\n        EXECUTE '";

    [Fact]
    public async Task Environment_objects_go_only_to_their_environment_script()
    {
        using var repo = Create();
        Objects(repo);
        var result = await repo.GenerateCleanlyAsync();
        Assert.Equal(["/phase"], result.Diagnostics.Where(d => d.Rule == "MQ4070").Select(d => d.JsonPointer));
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "object-environments"), repo.PathOf("db"));

        var schema = repo.Read("db/billing/schema.sql");
        Assert.Contains("GRANT SELECT ON billing.invoices TO PUBLIC;", schema, StringComparison.Ordinal);
        Assert.Contains(", SQL objects 2.\n", schema, StringComparison.Ordinal);
        foreach (var text in new[] { "dev_tools", "ix_invoices_total" })
            Assert.DoesNotContain(text, schema, StringComparison.Ordinal);

        var dev = repo.Read("db/billing/objects.dev.sql");
        var test = repo.Read("db/billing/objects.test.sql");
        Assert.False(File.Exists(repo.PathOf("db/billing/objects.staging.sql")), "a script for an environment no object names");
        Assert.Contains("-- Run it after schema.sql or the migrations on every deploy; it can run again, so every statement in it must be safe to run again.\n",
            dev, StringComparison.Ordinal);
        // Phase before first; then the others in dependency order, which follows the names where nothing orders them.
        var order = new[] { "CREATE SCHEMA IF NOT EXISTS dev_tools;", "CREATE OR REPLACE VIEW dev_tools.invoices", Guard(DevOnce), "ix_invoices_total" }
            .Select(s => dev.IndexOf(s, StringComparison.Ordinal)).ToList();
        Assert.True(order[0] > 0 && order.SequenceEqual(order.Order()), dev);
        Assert.Contains("ix_invoices_total", test, StringComparison.Ordinal);
        Assert.DoesNotContain("dev_tools", test, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT", dev + test, StringComparison.Ordinal);

        // The first migration builds the same database: no environment object either.
        var migration = MigrationFiles.Read(repo, "billing", 1);
        Assert.Contains("GRANT SELECT ON billing.invoices TO PUBLIC;", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("dev_tools", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("ix_invoices_total", migration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_once_objects_are_guarded_by_the_ledger_everywhere()
    {
        using var repo = Create(objectScripts: true);
        Objects(repo);
        await repo.GenerateCleanlyAsync();
        var prelude = LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema).TrimEnd('\n');

        // schema.sql, the first migration, objects.dev.sql and each per-object file write the ledger DDL, then the guard.
        foreach (var (path, id) in new[]
        {
            ("db/billing/schema.sql", AuditOnce), (MigrationFiles.Of(repo, "billing", 1), AuditOnce), ("db/billing/objects.dev.sql", DevOnce),
            ("db/billing/billing/objects/invoice_audit.sql", AuditOnce), ("db/billing/billing/objects/dev_runs.sql", DevOnce),
        })
        {
            var text = repo.Read(path);
            var ddl = text.IndexOf(prelude, StringComparison.Ordinal);
            var guard = text.IndexOf(Guard(id), StringComparison.Ordinal);
            Assert.True(ddl > 0 && guard > ddl, path + ":\n" + text);
            Assert.Contains("VALUES ('" + id + "', 'sql-object', 'billing', ", text, StringComparison.Ordinal);
            Assert.Contains("\nEND $once$;", text, StringComparison.Ordinal);
        }

        // The statements are one literal with their quotes doubled; the schema script and the migration run in one transaction.
        var schema = repo.Read("db/billing/schema.sql");
        Assert.Contains("EXECUTE 'CREATE TABLE billing.invoice_audit (id bigint NOT NULL);\nINSERT INTO billing.invoice_audit (id) VALUES (1);';", schema,
            StringComparison.Ordinal);
        Assert.Contains("\n-- One transaction: the DDL and a migration ledger row for each run-once SQL object, which runs only where the ledger does not "
            + "record it yet (1).\n", schema, StringComparison.Ordinal);
        Assert.Contains("\n\nBEGIN;\n", schema, StringComparison.Ordinal);
        Assert.EndsWith("\n\nCOMMIT;\n", schema, StringComparison.Ordinal);
        Assert.Contains("\n\nBEGIN;\n\n" + prelude + "\n\n", MigrationFiles.Read(repo, "billing", 1), StringComparison.Ordinal);
        Assert.DoesNotContain(prelude, repo.Read("db/billing/objects.test.sql"), StringComparison.Ordinal);
        Assert.DoesNotContain(prelude, repo.Read("db/billing/billing/objects/invoice_grants.sql"), StringComparison.Ordinal);

        // A body that holds the tag gets the next one.
        Object(repo, AuditOnce, "invoice_audit", "DO $once$ BEGIN PERFORM 1; END $once$;", once: true);
        await repo.GenerateCleanlyAsync();
        Assert.Contains("DO $once1$ BEGIN\n", repo.Read("db/billing/schema.sql"), StringComparison.Ordinal);
        Assert.Contains("\nEND $once1$;", repo.Read("db/billing/schema.sql"), StringComparison.Ordinal);

        // Outside PostgreSQL there is no ledger: a run-once object is left out with MQ4056.
        using var sqlite = Create(objectScripts: true, dialect: "sqlite");
        Object(sqlite, AuditOnce, "invoice_audit", "CREATE TABLE invoice_audit (id bigint NOT NULL);", once: true, dialect: "sqlite");
        var result = await sqlite.GenerateCleanlyAsync();
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ4056" && d.JsonPointer == "/apply");
        Assert.DoesNotContain("invoice_audit", sqlite.Read("db/billing/schema.sql"), StringComparison.Ordinal);
        Assert.DoesNotContain("invoice_audit", MigrationFiles.Read(sqlite, "billing", 1), StringComparison.Ordinal);
        Assert.Contains("-- script invoice_audit runs once, which needs the migration ledger; sqlite has none, so it is not created.",
            sqlite.Read("db/billing/objects/invoice_audit.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_environments_and_apply_keep_goldens_byte_identical()
    {
        // The dialects fixture's SQL objects with environments [] and apply converge written out: the bytes of the goldens.
        using var repo = PackRepo.BillingDialects();
        foreach (var database in new[] { "main", "reporting", "local" })
        {
            repo.EditJson($".maquettiste/model/databases/{database}/objects/invoices-keep-number.json", o =>
            {
                o["environments"] = new JsonArray();
                o["apply"] = "converge";
            });
        }

        repo.EditJson(".maquettiste/maquettiste.json", settings => settings["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["objectScripts"] = true });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        foreach (var database in new[] { "main", "reporting", "local" })
        {
            Assert.Equal(File.ReadAllText(Fixtures.Path("golden", "sql-ddl", "billing-dialects", database, "schema.sql")), repo.Read($"db/{database}/schema.sql"));
            var golden = Directory.GetFiles(Fixtures.Path("golden", "sql-ddl", "billing-dialects", database, "migrations")).Single();
            Assert.Equal(File.ReadAllText(golden), repo.Read($"db/{database}/migrations/{Path.GetFileName(golden)}"));
            Assert.Empty(Directory.GetFiles(repo.PathOf($"db/{database}"), "objects.*.sql"));
        }

        foreach (var folder in new[] { "main/billing/objects", "reporting/dbo/objects", "local/objects" })
            Golden.AssertMatches(Fixtures.Path(["golden", "sql-ddl", "objects-dialects", .. folder.Split('/')]), repo.PathOf("db/" + folder));
    }

    [Fact]
    public async Task The_environment_script_runs_twice_with_a_rerunnable_body_and_a_run_once_object()
    {
        using var repo = Create();
        Objects(repo);
        await repo.GenerateCleanlyAsync();
        var schema = repo.Read("db/billing/schema.sql");
        var dev = repo.Read("db/billing/objects.dev.sql");
        Assert.Contains(Guard(DevOnce), dev, StringComparison.Ordinal);

        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(Skip);
            return;
        }

        // The second run succeeds (a CREATE TABLE outside the guard would fail) and the run-once statements ran once.
        var output = await db.RunAsync("object_environments", schema, dev, dev,
            "SELECT 'runs=' || count(*) FROM dev_tools.runs;\n"
            + "SELECT 'audit=' || count(*) FROM billing.invoice_audit;\n"
            + "SELECT 'objects=' || string_agg(element_id || ' ' || kind || ' ' || coalesce(schema_name, '-') || ' ' || name, ',' ORDER BY element_id) FROM migration_ledger.applied_object;\n"
            + "SELECT 'view=' || count(*) FROM information_schema.views WHERE table_schema = 'dev_tools';\n");
        Assert.Contains("runs=1", output, StringComparison.Ordinal);
        Assert.Contains("audit=1", output, StringComparison.Ordinal);
        Assert.Contains($"objects={AuditOnce} sql-object billing invoice_audit,{DevOnce} sql-object billing dev_runs", output, StringComparison.Ordinal);
        Assert.Contains("view=1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_changed_environment_object_gets_its_todo_and_a_dropped_one_keeps_todays()
    {
        using var repo = Create();
        Objects(repo);
        await repo.GenerateCleanlyAsync();

        // The index moves to test only, the run-once audit table changes, the dev view leaves the model.
        Object(repo, TotalIndex, "invoice_total_index", "CREATE INDEX IF NOT EXISTS ix_invoices_total ON billing.invoices (total DESC);", ["test"],
            dependsOn: [Id(10)]);
        Object(repo, AuditOnce, "invoice_audit", "CREATE TABLE billing.invoice_audit (id bigint NOT NULL, at timestamptz);", once: true, dependsOn: [Id(10)]);
        File.Delete(repo.PathOf(".maquettiste/model/databases/billing/objects/dev-invoices.json"));
        await repo.GenerateCleanlyAsync();

        var migration = MigrationFiles.Read(repo, "billing", 2);
        Assert.Contains("-- TODO: SQL object invoice_total_index changed and runs in environments test (objects.<env>.sql applies it there); drop it by hand "
            + "where it should no longer run.\n", migration, StringComparison.Ordinal);
        Assert.Contains("-- Run-once SQL object invoice_audit changed; it does not run again where the ledger records it.\n", migration, StringComparison.Ordinal);
        Assert.Contains("-- TODO: script dev_invoices left the model; drop it by hand.\n", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("ix_invoices_total ON", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("drop it by hand before its new statements below run", migration, StringComparison.Ordinal);
        Assert.Contains(Guard(AuditOnce), migration, StringComparison.Ordinal);
        Assert.Contains("ix_invoices_total ON billing.invoices (total DESC)", repo.Read("db/billing/objects.test.sql"), StringComparison.Ordinal);
        Assert.DoesNotContain("ix_invoices_total", repo.Read("db/billing/objects.dev.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_object_switched_from_converge_to_run_once_gets_a_todo_that_it_runs_one_more_time()
    {
        // The ledger has no row for an object that converged, so its guard runs it once more: the reviewer gets a TODO, not the
        // run-once comment, whether the migration or objects.<env>.sql runs it.
        const string audit = "CREATE TABLE billing.invoice_audit (id bigint NOT NULL);";
        const string runs = "CREATE TABLE billing.runs (n integer NOT NULL);";
        using var repo = Create();
        Object(repo, AuditOnce, "invoice_audit", audit, dependsOn: [Id(10)]);
        Object(repo, DevOnce, "dev_runs", runs, ["dev"]);
        await repo.GenerateCleanlyAsync();
        Assert.DoesNotContain("applied_object", repo.Read("db/billing/schema.sql"), StringComparison.Ordinal);

        Object(repo, AuditOnce, "invoice_audit", audit, once: true, dependsOn: [Id(10)]);
        Object(repo, DevOnce, "dev_runs", runs, ["dev"], once: true);
        await repo.GenerateCleanlyAsync();
        var migration = MigrationFiles.Read(repo, "billing", 2);
        foreach (var name in new[] { "invoice_audit", "dev_runs" })
        {
            Assert.Contains("-- TODO: SQL object " + name + " changed to run once and the ledger has no row for it yet, so it runs one more time in every "
                + "database, also where it exists already; drop it by hand before it runs, or make its statements safe to run again.\n", migration,
                StringComparison.Ordinal);
        }

        Assert.Contains("-- TODO: SQL object dev_runs changed and runs in environments dev (objects.<env>.sql applies it there); drop it by hand where "
            + "it should no longer run.\n", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("-- Run-once SQL object", migration, StringComparison.Ordinal);
        Assert.Contains(Guard(AuditOnce), migration, StringComparison.Ordinal);
        Assert.Contains(Guard(DevOnce), repo.Read("db/billing/objects.dev.sql"), StringComparison.Ordinal);

        // Once it runs once, a change keeps the run-once comment.
        Object(repo, AuditOnce, "invoice_audit", audit + "\nCOMMENT ON TABLE billing.invoice_audit IS 'audit';", once: true, dependsOn: [Id(10)]);
        await repo.GenerateCleanlyAsync();
        migration = MigrationFiles.Read(repo, "billing", 3);
        Assert.Contains("-- Run-once SQL object invoice_audit changed; it does not run again where the ledger records it.\n", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("changed to run once", migration, StringComparison.Ordinal);
    }
}
