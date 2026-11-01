using System.Text.RegularExpressions;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Model;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The migration ledger on a PostgreSQL server (next-phase-1-design.md section 3.3): the contract's DDL and grant, the rows
/// sql-ddl's schema script records, and the object rows of run-once SQL objects (section 3.4). Skipped unless
/// <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c> names a running container.
/// </summary>
public sealed partial class LedgerSqlTests
{
    private const string Skip = "Name a PostgreSQL container in the environment to run the ledger on it (see DbServer).";

    [Fact]
    public async Task Ledger_ddl_runs_twice_in_one_transaction_and_a_newer_version_stops_it()
    {
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(Skip);
            return;
        }

        var ddl = LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema);
        var output = await db.RunAsync("ledger_ddl",
            "BEGIN;\n" + ddl + ddl + "COMMIT;\n" + ddl + "UPDATE migration_ledger.contract SET version = 2;\n",
            "\\set ON_ERROR_STOP off\nBEGIN;\n" + ddl
            + "INSERT INTO migration_ledger.applied_fact (fact_id, content_hash) VALUES ('01JFCT00000000000000000001', 'h');\nCOMMIT;\n"
            + "\\set ON_ERROR_STOP on\n"
            + "SELECT 'contract=' || count(*) || ',' || max(version) FROM migration_ledger.contract;\n"
            + "SELECT 'facts=' || count(*) FROM migration_ledger.applied_fact;\n");

        Assert.Contains("ledger contract version is newer than 1; use a newer writer", output, StringComparison.Ordinal);
        Assert.Contains("contract=1,2", output, StringComparison.Ordinal);
        Assert.Contains("facts=0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_read_grant_lets_a_role_select_and_not_insert()
    {
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(Skip);
            return;
        }

        // A reserved schema name, quoted in the DDL and the grant alike.
        var role = "mq_reader_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var output = await db.RunAsync("ledger_grant",
                LedgerContract.Ddl(Dialect.PostgreSql, "order")
                + "INSERT INTO \"order\".applied_fact (fact_id, content_hash) VALUES ('01JFCT00000000000000000001', 'h');\n"
                + $"CREATE ROLE {role};\n"
                + LedgerContract.ReadGrant(Dialect.PostgreSql, "order", role)
                + $"SET ROLE {role};\n"
                + "SELECT 'facts=' || count(*) FROM \"order\".applied_fact;\n"
                + "SELECT 'objects=' || count(*) FROM \"order\".applied_object;\n"
                + "SELECT 'version=' || version FROM \"order\".contract;\n"
                + "DO $t$ BEGIN\n"
                + "    INSERT INTO \"order\".applied_fact (fact_id, content_hash) VALUES ('01JFCT00000000000000000002', 'h');\n"
                + "    RAISE EXCEPTION 'the reader inserted a fact row';\n"
                + "EXCEPTION WHEN insufficient_privilege THEN NULL;\n"
                + "END $t$;\n"
                + "SELECT 'insert=denied';\n"
                + "RESET ROLE;\n");

            Assert.Contains("facts=1", output, StringComparison.Ordinal);
            Assert.Contains("objects=0", output, StringComparison.Ordinal);
            Assert.Contains("version=1", output, StringComparison.Ordinal);
            Assert.Contains("insert=denied", output, StringComparison.Ordinal);
        }
        finally
        {
            await db.RunAsync("ledger_grant_cleanup", $"DROP ROLE IF EXISTS {role};");
        }
    }

    [Fact]
    public async Task Schema_sql_records_its_facts_except_raw_ones_and_a_failed_run_records_none()
    {
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(Skip);
            return;
        }

        using var repo = PackRepo.Billing();
        LedgerFacts.AddRename(repo);
        LedgerFacts.AddHeldDrop(repo);
        LedgerFacts.AddRaw(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var schema = repo.Read("db/main/schema.sql");
        var rows = FactRow().Matches(schema).Select(m => m.Groups["id"].Value + " " + m.Groups["hash"].Value).ToList();
        Assert.Equal(2, rows.Count);

        // The rename and the held drop are recorded with the hashes the script wrote; the raw fact is not.
        var output = await db.RunAsync("ledger_schema", schema,
            "SELECT 'row=' || fact_id || ' ' || content_hash || ' ' || applied_by || ' ' || (applied_at IS NOT NULL) FROM migration_ledger.applied_fact ORDER BY fact_id;\n");
        Assert.Equal(rows.Select(r => "row=" + r + " sql-ddl true"), Lines(output).Where(l => l.StartsWith("row=", StringComparison.Ordinal)));
        Assert.Equal([LedgerFacts.Rename, LedgerFacts.HeldDrop], rows.Select(r => r[..26]));
        Assert.DoesNotContain(LedgerFacts.Raw, output, StringComparison.Ordinal);

        // A statement that fails inside the script's transaction takes the DDL and the rows with it.
        Assert.EndsWith("\n\nCOMMIT;\n", schema, StringComparison.Ordinal);
        var failing = "\\set ON_ERROR_STOP off\n" + schema[..^"COMMIT;\n".Length] + "SELECT 1 / 0;\n\nCOMMIT;\n\\set ON_ERROR_STOP on\n";
        output = await db.RunAsync("ledger_failed", LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema),
            failing + "SELECT 'facts=' || count(*) FROM migration_ledger.applied_fact;\n"
            + "SELECT 'tables=' || count(*) FROM information_schema.tables WHERE table_schema = 'billing';\n");
        Assert.Contains("division by zero", output, StringComparison.Ordinal);
        Assert.Contains("facts=0", output, StringComparison.Ordinal);
        Assert.Contains("tables=0", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_once_object_runs_once_and_records_itself()
    {
        using var repo = SqlObjectEnvironmentTests.Create(objectScripts: true);
        SqlObjectEnvironmentTests.Objects(repo);
        await repo.GenerateCleanlyAsync();
        var schema = repo.Read("db/billing/schema.sql");
        var script = repo.Read("db/billing/billing/objects/invoice_audit.sql");
        var hash = Maquettiste.Engine.Hashing.ContentHash.Of(
            "CREATE TABLE billing.invoice_audit (id bigint NOT NULL);\nINSERT INTO billing.invoice_audit (id) VALUES (1);");
        Assert.Contains("'" + hash + "');", schema, StringComparison.Ordinal);

        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(Skip);
            return;
        }

        // The two-statement body ran in schema.sql; its own script, run twice more, finds the row and runs nothing (a second CREATE
        // TABLE would fail).
        var output = await db.RunAsync("run_once", schema, script, script,
            "SELECT 'audit=' || count(*) FROM billing.invoice_audit;\n"
            + "SELECT 'row=' || element_id || ' ' || kind || ' ' || schema_name || ' ' || name || ' ' || content_hash || ' ' || (revert_text IS NULL) || ' ' || (applied_at IS NOT NULL) "
            + "FROM migration_ledger.applied_object ORDER BY element_id;\n");
        Assert.Contains("audit=1", output, StringComparison.Ordinal);
        Assert.Equal([$"row={SqlObjectEnvironmentTests.AuditOnce} sql-object billing invoice_audit {hash} true true"],
            Lines(output).Where(l => l.StartsWith("row=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_rekey_fact_moves_the_row_of_a_run_once_object()
    {
        const string Moved = "01SE0000000000000000000030";
        const string Rekey = "01JFCT00000000000000000031";
        const string Body = "CREATE TABLE billing.invoice_audit (id bigint NOT NULL);\nINSERT INTO billing.invoice_audit (id) VALUES (1);";
        using var repo = SqlObjectEnvironmentTests.Create(objectScripts: true);
        SqlObjectEnvironmentTests.Object(repo, SqlObjectEnvironmentTests.AuditOnce, "invoice_audit", Body, once: true);
        await repo.GenerateCleanlyAsync();
        var schema = repo.Read("db/billing/schema.sql");

        // The object's id moves (its file written again under a new id) and a rekey fact says so.
        SqlObjectEnvironmentTests.Object(repo, Moved, "invoice_audit", Body, once: true);
        repo.Write($".maquettiste/model/databases/billing/migrations/open/{Rekey.ToLowerInvariant()}.json", $$"""
            {
              "$schema": "../../../../../.schema/v1/migration.json",
              "kind": "migration",
              "id": "{{Rekey}}",
              "database": "01SE0000000000000000000001",
              "fact": "rekey",
              "keys": [
                {
                  "kind": "sql-object",
                  "from": "{{SqlObjectEnvironmentTests.AuditOnce}}",
                  "to": "{{Moved}}",
                  "name": "invoice_audit"
                }
              ]
            }

            """);
        await repo.GenerateCleanlyAsync();
        var migration = MigrationFiles.Read(repo, "billing", 2);
        var update = $"UPDATE migration_ledger.applied_object SET element_id = '{Moved}' WHERE element_id = '{SqlObjectEnvironmentTests.AuditOnce}' AND NOT EXISTS "
            + $"(SELECT 1 FROM migration_ledger.applied_object WHERE element_id = '{Moved}');";
        Assert.Contains("\n" + update + "\n", migration, StringComparison.Ordinal);
        Assert.Contains($"VALUES ('{Rekey}', ", migration, StringComparison.Ordinal);

        // Read under today's keys, the baseline holds the object already: no TODO, no guard, nothing runs again.
        Assert.DoesNotContain("-- TODO", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("DO $once$", migration, StringComparison.Ordinal);

        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(Skip);
            return;
        }

        // After the migration the object's own script, under its new id, finds its row and runs nothing (a second CREATE TABLE would
        // fail).
        var script = repo.Read("db/billing/billing/objects/invoice_audit.sql");
        Assert.Contains($"WHERE element_id = '{Moved}') THEN", script, StringComparison.Ordinal);
        var output = await db.RunAsync("run_once_rekey", schema, migration, script,
            "SELECT 'audit=' || count(*) FROM billing.invoice_audit;\n"
            + "SELECT 'objects=' || string_agg(element_id, ',' ORDER BY element_id) FROM migration_ledger.applied_object;\n");
        Assert.Contains("audit=1", output, StringComparison.Ordinal);
        Assert.Contains($"objects={Moved}", output, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^INSERT INTO migration_ledger\.applied_fact \(fact_id, content_hash, applied_by\) VALUES \('(?<id>[0-9A-Z]{26})', '(?<hash>[0-9a-f]{64})', 'sql-ddl'\) ON CONFLICT \(fact_id\) DO NOTHING;$", RegexOptions.Multiline)]
    private static partial Regex FactRow();
}
