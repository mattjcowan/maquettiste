using System.Text.Json.Nodes;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Migration facts written as files of the billing fixture (next-phase-1-design.md section 3.2's layout): a complete rename, a held
/// drop, a raw step and an adopt, in the PostgreSQL database <c>main</c> unless another database id is given.
/// </summary>
internal static class LedgerFacts
{
    public const string MainId = "01J92P0V1QRN2181XM2ZWE02W4";
    public const string Rename = "01JFCT00000000000000000001";
    public const string HeldDrop = "01JFCT00000000000000000002";
    public const string Raw = "01JFCT00000000000000000003";
    public const string Adopt = "01JFCT00000000000000000004";
    public const string Pending = "01JFCT00000000000000000005";

    private const string Invoices = "01J92P0V0FJ23CGSNKM7P1W5V7@" + MainId;
    private const string Customers = "01J92P0V0ETQKXXP951CMMNHH3@" + MainId;

    /// <summary>The rename of <c>invoices.invoice_no</c> to <c>number</c>: complete, so a build reaches it.</summary>
    public static void AddRename(PackRepo repo) => Add(repo, "main", Rename, $$"""
          "database": "{{MainId}}",
          "fact": "rename",
          "object": {
            "kind": "column",
            "table": "{{Invoices}}",
            "key": "01J92P0V0R3VSP7D5238DTNZX1",
            "schema": "billing",
            "tableName": "invoices",
            "name": "number"
          },
          "from": {
            "name": "invoice_no"
          },
          "to": {
            "name": "number"
          }
        """);

    /// <summary>A held drop of <c>customers.customer_since</c>: the model keeps the column, so a build reaches its end state.</summary>
    public static void AddHeldDrop(PackRepo repo) => Add(repo, "main", HeldDrop, $$"""
          "database": "{{MainId}}",
          "fact": "drop",
          "object": {
            "kind": "column",
            "table": "{{Customers}}",
            "key": "01J92P0V0P4MBJRM6QN6H0RSVP",
            "schema": "billing",
            "tableName": "customers",
            "name": "customer_since"
          },
          "held": true
        """);

    /// <summary>A raw step behind its guard: no build runs its SQL, so none records it.</summary>
    public static void AddRaw(PackRepo repo, string database = "main", string databaseId = MainId) => Add(repo, database, Raw, $$"""
          "database": "{{databaseId}}",
          "fact": "raw",
          "sql": {
            "*": "UPDATE billing.invoices SET notes = NULL WHERE notes = '';"
          },
          "skipWhen": {
            "*": "SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM billing.invoices WHERE notes = '');"
          }
        """);

    /// <summary>An adopt: its end state is object rows, which the pack does not write, so no fact row either.</summary>
    public static void AddAdopt(PackRepo repo) => Add(repo, "main", Adopt, $$"""
          "database": "{{MainId}}",
          "fact": "adopt",
          "objects": [
            {
              "kind": "table",
              "key": "{{Customers}}",
              "schema": "billing",
              "name": "customers"
            }
          ],
          "environment": "production"
        """);

    /// <summary>A type change (a conversion, not a widening) without its conversion: pending (MQ4101), so no build records it.</summary>
    public static void AddPending(PackRepo repo) => Add(repo, "main", Pending, $$"""
          "database": "{{MainId}}",
          "fact": "change-type",
          "object": {
            "kind": "column",
            "table": "{{Invoices}}",
            "key": "01J92P0V0WKRGKH7YBKA2V30NC",
            "schema": "billing",
            "tableName": "invoices",
            "name": "notes"
          },
          "from": {
            "type": "int64",
            "nativeType": "bigint"
          },
          "to": {
            "type": "text",
            "nativeType": "text"
          }
        """);

    /// <summary>A plain fact (a rename of a table that no build has reached) in another database of the dialects variant.</summary>
    public static void AddTableRename(PackRepo repo, string database, string databaseId, string id) => Add(repo, database, id, $$"""
          "database": "{{databaseId}}",
          "fact": "rename",
          "object": {
            "kind": "table",
            "key": "01J92P0V0ETQKXXP951CMMNHH3@{{databaseId}}",
            "name": "customers"
          },
          "from": {
            "name": "clients"
          },
          "to": {
            "name": "customers"
          }
        """);

    private static void Add(PackRepo repo, string database, string id, string fields) =>
        repo.Write($".maquettiste/model/databases/{database}/migrations/open/{id.ToLowerInvariant()}.json",
            "{\n  \"$schema\": \"../../../../../.schema/v1/migration.json\",\n  \"kind\": \"migration\",\n  \"id\": \"" + id + "\",\n"
            + fields.TrimEnd() + "\n}\n");
}

/// <summary>
/// The migration ledger in sql-ddl's schema script (next-phase-1-design.md section 3.3): on PostgreSQL a script that reaches facts
/// runs in one transaction with the ledger DDL and one row per fact it reaches; a model without facts writes what it always wrote.
/// </summary>
public sealed class LedgerGoldenTests
{
    [Fact]
    public async Task Schema_with_facts_writes_one_transaction_with_the_prelude_and_fact_rows()
    {
        using var repo = PackRepo.Billing();
        LedgerFacts.AddRename(repo);
        LedgerFacts.AddHeldDrop(repo);
        LedgerFacts.AddRaw(repo);
        LedgerFacts.AddAdopt(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var schema = repo.Read("db/main/schema.sql");
        var prelude = LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema).TrimEnd('\n');
        Assert.Contains("\n\nBEGIN;\n\nCREATE SCHEMA IF NOT EXISTS billing;\n\n" + prelude + "\n\n", schema, StringComparison.Ordinal);
        Assert.EndsWith("\n\nCOMMIT;\n", schema, StringComparison.Ordinal);
        Assert.Contains($"VALUES ('{LedgerFacts.Rename}', ", schema, StringComparison.Ordinal);
        Assert.Contains($"VALUES ('{LedgerFacts.HeldDrop}', ", schema, StringComparison.Ordinal);
        Assert.DoesNotContain(LedgerFacts.Raw, schema, StringComparison.Ordinal);
        Assert.DoesNotContain(LedgerFacts.Adopt, schema, StringComparison.Ordinal);
        Assert.Equal(1, Count(schema, "BEGIN;"));
        Assert.Equal(1, Count(schema, "COMMIT;"));

        // Between the header and the transaction, and inside it, the script is the one a model without facts writes.
        var plain = File.ReadAllText(Fixtures.Path("golden", "sql-ddl", "billing", "main", "schema.sql"));
        var body = plain[(plain.IndexOf("\n\nCREATE SCHEMA", StringComparison.Ordinal) + 2)..].TrimEnd('\n');
        Assert.Contains(body.Replace("CREATE SCHEMA IF NOT EXISTS billing;", "CREATE SCHEMA IF NOT EXISTS billing;\n\n" + prelude, StringComparison.Ordinal), schema, StringComparison.Ordinal);

        // The migration from nothing builds the database at the model's shape too: no fact step but the raw one behind its guard,
        // followed by its row, then a row per fact a build reaches, as the schema script writes them.
        var path = MigrationFiles.Of(repo, "main", 1);
        var migration = repo.Read(path);
        Assert.Contains("\n\nBEGIN;\n\n" + prelude + "\n\n", migration, StringComparison.Ordinal);
        Assert.Contains($"-- Migration fact {LedgerFacts.Raw} (raw, phase after): runs unless its guard finds a row.\n", migration, StringComparison.Ordinal);
        var rawRow = migration.IndexOf($"VALUES ('{LedgerFacts.Raw}', ", StringComparison.Ordinal);
        var renameRow = migration.IndexOf($"VALUES ('{LedgerFacts.Rename}', ", StringComparison.Ordinal);
        var heldRow = migration.IndexOf($"VALUES ('{LedgerFacts.HeldDrop}', ", StringComparison.Ordinal);
        Assert.True(rawRow > 0 && rawRow < renameRow && renameRow < heldRow, migration);
        Assert.EndsWith("ON CONFLICT (fact_id) DO NOTHING;\n\nCOMMIT;\n", migration, StringComparison.Ordinal);
        Assert.DoesNotContain(LedgerFacts.Adopt + "', ", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO: migration fact", migration, StringComparison.Ordinal);

        // The files that write ledger rows, staged as the golden lays them out.
        using var staged = new TempRepo();
        staged.WriteFile("ledger-facts/main/schema.sql", schema);
        staged.WriteFile("ledger-facts/main/migrations/" + Path.GetFileName(path), migration);
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "ledger-facts"), staged.PathOf("ledger-facts"));
    }

    [Fact]
    public async Task Schema_sql_with_MQ4101_lowered_writes_no_row_for_a_pending_fact()
    {
        using var repo = PackRepo.Billing();
        repo.EditJson(".maquettiste/maquettiste.json", s => s["validation"] = new JsonObject { ["rules"] = new JsonObject { ["MQ4101"] = "warning" } });
        LedgerFacts.AddPending(repo);
        var result = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ4101");

        // Its only fact pending, the script writes no ledger at all: the bytes of a model without facts.
        var plain = File.ReadAllText(Fixtures.Path("golden", "sql-ddl", "billing", "main", "schema.sql"));
        Assert.Equal(plain, repo.Read("db/main/schema.sql"));

        // With a complete fact beside it, only that fact gets a row.
        LedgerFacts.AddRename(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var schema = repo.Read("db/main/schema.sql");
        Assert.Contains($"VALUES ('{LedgerFacts.Rename}', ", schema, StringComparison.Ordinal);
        Assert.DoesNotContain(LedgerFacts.Pending, schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_without_facts_writes_no_ledger()
    {
        // The billing goldens (GoldenTests) are unchanged; facts outside PostgreSQL write no ledger either (MQ4056 reports them).
        using var repo = PackRepo.BillingDialects();
        LedgerFacts.AddTableRename(repo, "reporting", "01J92P0V2A0000000000000001", "01JFCT00000000000000000011");
        LedgerFacts.AddTableRename(repo, "local", "01J92P0V2A0000000000000002", "01JFCT00000000000000000012");
        var result = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ4056");
        foreach (var database in new[] { "main", "reporting", "local" })
        {
            var schema = repo.Read($"db/{database}/schema.sql");
            Assert.Equal(File.ReadAllText(Fixtures.Path("golden", "sql-ddl", "billing-dialects", database, "schema.sql")), schema);
            Assert.DoesNotContain(LedgerContract.DefaultSchema, schema, StringComparison.Ordinal);
        }

        // Their migrations name the facts in the header and write what the dialect always wrote, without a ledger.
        foreach (var (database, fact) in new[] { ("reporting", "01JFCT00000000000000000011"), ("local", "01JFCT00000000000000000012") })
        {
            var path = MigrationFiles.Of(repo, database, 1);
            var migration = repo.Read(path);
            Assert.Contains("\n-- Facts: " + fact + "\n", migration, StringComparison.Ordinal);
            Assert.DoesNotContain(LedgerContract.DefaultSchema, migration, StringComparison.Ordinal);
            var golden = File.ReadAllText(Directory.GetFiles(Fixtures.Path("golden", "sql-ddl", "billing-dialects", database, "migrations")).Single());
            Assert.Equal(golden.Replace("\n-- Facts: none\n", "\n-- Facts: " + fact + "\n", StringComparison.Ordinal), migration);
        }
    }

    [Fact]
    public async Task Changing_the_ledger_schema_re_renders_the_scripts_that_write_the_ledger()
    {
        using var repo = PackRepo.Billing();
        LedgerFacts.AddRename(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.Contains("INSERT INTO migration_ledger.applied_fact ", repo.Read("db/main/schema.sql"), StringComparison.Ordinal);

        repo.EditJson(".maquettiste/maquettiste.json", s => s["migrations"] = new JsonObject { ["ledgerSchema"] = "bookkeeping" });
        var plan = await repo.GenerateCleanlyAsync(GenerationMode.DryRun, packs: ["sql-ddl"]);
        Assert.Contains(plan.Changes, c => c.Path == "db/main/schema.sql" && c.Kind == FileChangeKind.Modified);

        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var schema = repo.Read("db/main/schema.sql");
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS bookkeeping;\n", schema, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO bookkeeping.applied_fact ", schema, StringComparison.Ordinal);
        Assert.DoesNotContain(LedgerContract.DefaultSchema, schema, StringComparison.Ordinal);

        // A schema with a leading underscore is plain to the ledger contract, so the rows name it as the ledger DDL does: unquoted
        // (sql_quote's reserved mode would quote it).
        repo.EditJson(".maquettiste/maquettiste.json", s => s["migrations"]!["ledgerSchema"] = "_ledger");
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        schema = repo.Read("db/main/schema.sql");
        Assert.Equal("_ledger", LedgerContract.QuoteIdentifier(Dialect.PostgreSql, "_ledger", "schema"));
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS _ledger;\n", schema, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO _ledger.applied_fact ", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"_ledger\"", schema, StringComparison.Ordinal);

        // migrations.record changes no rendered byte.
        repo.EditJson(".maquettiste/maquettiste.json", s => s["migrations"]!["record"] = "off");
        var unchanged = await repo.GenerateCleanlyAsync(GenerationMode.DryRun, packs: ["sql-ddl"]);
        Assert.DoesNotContain(unchanged.Changes, c => c.Path == "db/main/schema.sql" && c.Kind == FileChangeKind.Modified);
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
