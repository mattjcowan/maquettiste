using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Packs.Tests;

/// <summary>The packs across runs: incremental runs, <c>--check</c>, migrations, protected regions and owned files.</summary>
public sealed class LifecycleTests
{
    private static readonly FileChangeKind[] Writes = [FileChangeKind.Added, FileChangeKind.Modified, FileChangeKind.Deleted, FileChangeKind.HandEdited, FileChangeKind.Conflict, FileChangeKind.OrphanedOwned];

    [Fact]
    public async Task A_second_run_writes_nothing_and_check_finds_no_drift()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync();

        var again = await repo.GenerateCleanlyAsync();
        Assert.DoesNotContain(again.Changes, c => Writes.Contains(c.Kind));

        var forced = await repo.GenerateCleanlyAsync(force: true);
        Assert.DoesNotContain(forced.Changes, c => Writes.Contains(c.Kind));

        // Check mode renders every unit against the saved snapshots: the migration unit re-emits revision 1 as a placeholder,
        // which the owned-file rule keeps, so the committed migration is neither drift nor an orphan.
        var check = await repo.GenerateAsync(GenerationMode.Check);
        Assert.True(check.Outcome == RunOutcome.Succeeded, PackRepo.Describe(check));
    }

    [Fact]
    public async Task A_plan_lists_a_migration_written_once_as_kept_with_no_diff()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync();
        // The snapshot is at revision 1 now: the migration unit emits a short placeholder for 0001, never written over the file.
        await repo.GenerateCleanlyAsync();
        var real = repo.Read("db/main/migrations/0001.sql");
        await using var store = new ModelStore(repo.Repo.Options);
        var service = new GenerationService(store, repo.Repo.Options);

        foreach (var force in new[] { false, true })
        {
            var plan = (await service.PlanAsync(new GenerationRequest { Force = force }, null, TestContext.Current.CancellationToken)).Plan!;

            var migration = Assert.Single(plan.Changes, c => c.Path == "db/main/migrations/0001.sql");
            Assert.Equal(FileChangeKind.Kept, migration.Kind);
            Assert.Equal("", await service.GetPlanDiffAsync(plan.Id, migration.Path, TestContext.Current.CancellationToken));
            Assert.Equal(0, plan.Counts["added"] + plan.Counts["modified"] + plan.Counts["deleted"] + plan.Counts["conflict"]);
            Assert.Equal(plan.Units.SelectMany(u => u.Outputs).Count(), plan.Changes.Count);
            if (force)
            {
                // The forced plan rendered the placeholder: other bytes than the migration on disk, still nothing to do.
                var output = plan.Units.SelectMany(u => u.Outputs).Single(o => o.Path == migration.Path);
                Assert.NotEqual(output.DiskHashAtPlan, output.ContentHash);
                Assert.Equal(0, plan.Counts["not-rendered"]);
            }
            else
            {
                Assert.True(plan.Counts["not-rendered"] > 0);
            }

            var applied = await service.ApplyAsync(plan.Id, null, TestContext.Current.CancellationToken);
            Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
            Assert.Equal(0, applied.Result!.FilesWritten);
            Assert.Equal(real, repo.Read("db/main/migrations/0001.sql"));
        }
    }

    [Fact]
    public async Task A_change_of_logical_type_asks_the_reviewer_to_convert_the_existing_values()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync();

        repo.EditJson(".maquettiste/model/entities/customer.json", customer =>
        {
            var since = customer["attributes"]!.AsArray().First(a => (string?)a!["name"] == "customerSince")!;
            since["type"] = "string";
            since["length"] = 10;
        });
        await repo.GenerateCleanlyAsync();

        var pg = repo.Read("db/main/migrations/0002.sql");
        AssertBefore(pg, "ALTER TABLE billing.customers ALTER COLUMN customer_since TYPE varchar(10)", "-- TODO: convert existing values of customers.customer_since (type: ");
        Assert.Contains("-- TODO: convert existing values of customers.customer_since (type: ", repo.Read("db/reporting/migrations/0002.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_column_replaced_by_one_of_the_same_name_is_dropped_before_it_is_added_again()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync();

        // A new id for the same attribute (as when a generator derives ids from names that changed): the diff sees a dropped
        // column and an added column with the same name, and the migration must drop first or the ADD fails.
        repo.EditJson(".maquettiste/model/entities/customer.json", customer =>
        {
            var since = customer["attributes"]!.AsArray().First(a => (string?)a!["name"] == "customerSince")!;
            since["id"] = "01KZZZZZZZZZZZZZZZZZZZZZZZ";
        });
        await repo.GenerateCleanlyAsync();

        var pg = repo.Read("db/main/migrations/0002.sql");
        AssertBefore(pg, "ALTER TABLE billing.customers DROP COLUMN customer_since;", "ALTER TABLE billing.customers ADD COLUMN customer_since ");
    }

    [Fact]
    public async Task A_model_change_writes_the_next_migration_once_and_keeps_the_earlier_one()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync();
        var first = repo.Read("db/main/migrations/0001.sql");
        Assert.Contains("CREATE TABLE billing.invoices (", first, StringComparison.Ordinal);

        ModelChanges.ChangeCustomer(repo);
        ModelChanges.AddFeaturedInvoice(repo);
        ModelChanges.AddDisputedStatus(repo);
        ModelChanges.AddVoucherMethod(repo);
        await repo.GenerateCleanlyAsync();

        Assert.Equal(first, repo.Read("db/main/migrations/0001.sql"));
        var pg = repo.Read("db/main/migrations/0002.sql");
        Assert.Contains("schema revision 1 to 2", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.customers ADD COLUMN phone varchar(30) NULL;", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.customers RENAME COLUMN name TO full_name;", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.customers ALTER COLUMN full_name TYPE varchar(200);", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.customers DROP COLUMN customer_since;", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.invoice_lines ADD CONSTRAINT fk_invoice_lines_product_id FOREIGN KEY (product_id) REFERENCES billing.products (id) ON DELETE CASCADE;", pg, StringComparison.Ordinal);

        // Every column exists before an index or constraint uses it, and a changed constraint is dropped before it is added again.
        AssertBefore(pg, "ALTER TABLE billing.customers ADD COLUMN region varchar(40) NULL;", "CREATE INDEX ix_customers_region ON billing.customers (region);");
        AssertBefore(pg, "ALTER TABLE billing.customers ADD COLUMN featured_invoice_id uuid NULL;", "ALTER TABLE billing.customers ADD CONSTRAINT fk_customers_featured_invoice_id FOREIGN KEY (featured_invoice_id) REFERENCES billing.invoices (id);");
        AssertBefore(pg, "ALTER TABLE billing.invoice_lines DROP CONSTRAINT fk_invoice_lines_product_id;", "ALTER TABLE billing.customers ADD COLUMN phone");
        AssertBefore(pg, "ALTER TABLE billing.customers DROP COLUMN customer_since;", "ALTER TABLE billing.invoice_lines ADD CONSTRAINT fk_invoice_lines_product_id");

        var sqlServer = repo.Read("db/reporting/migrations/0002.sql");
        AssertBefore(sqlServer, "ALTER TABLE dbo.customers ADD region nvarchar(40) NULL;", "CREATE INDEX ix_customers_region ON dbo.customers (region);");
        AssertBefore(sqlServer, "ALTER TABLE dbo.customers ADD featured_invoice_id uniqueidentifier NULL;", "ADD CONSTRAINT fk_customers_featured_invoice_id");
        Assert.Contains("ALTER TABLE dbo.customers ADD priority int NOT NULL CONSTRAINT df_customers_priority DEFAULT 0;", sqlServer, StringComparison.Ordinal);
        // sp_rename reports a failure by an error XACT_ABORT does not act on: its return code is checked and THROW aborts the migration.
        Assert.Matches(@"BEGIN DECLARE (@mq_rc\d+) int; EXEC \1 = sp_rename N'dbo\.customers\.name', N'full_name', N'COLUMN'; IF \1 <> 0 THROW 50000, N'sp_rename failed: the migration stops here\.', 1; END;", sqlServer);
        Assert.Contains("ALTER TABLE dbo.customers ADD phone nvarchar(30) NULL;", sqlServer, StringComparison.Ordinal);

        var sqlite = repo.Read("db/local/migrations/0002.sql");
        // customers gains a foreign key, so it is rebuilt too; its renamed column is copied from its old name.
        Assert.Contains("INSERT INTO \"_mq_new_customers\" (id, full_name, email, created_at, updated_at)\nSELECT id, name, email, created_at, updated_at FROM customers;", sqlite, StringComparison.Ordinal);
        // SQLite cannot change a foreign key in place: invoice_lines is rebuilt (new table, rows copied, old dropped, new renamed).
        Assert.Contains("-- Rebuild invoice_lines (SQLite cannot change it in place: keys and constraints).", sqlite, StringComparison.Ordinal);
        AssertBefore(sqlite, "PRAGMA foreign_keys = OFF;", "BEGIN;");
        AssertBefore(sqlite, "INSERT INTO \"_mq_new_invoice_lines\" (", "DROP TABLE invoice_lines;");
        AssertBefore(sqlite, "ALTER TABLE \"_mq_new_invoice_lines\" RENAME TO invoice_lines;", "COMMIT;");
        AssertBefore(sqlite, "ALTER TABLE \"_mq_new_customers\" RENAME TO customers;", "CREATE INDEX ix_customers_region ON customers (region);");
        Assert.DoesNotContain("review by hand", sqlite, StringComparison.Ordinal);
        // The new reference row reaches the lookup table through the seed's idempotent upsert, and the CHECKs elsewhere.
        var localSeed = repo.Read("db/local/seed.sql");
        Assert.Contains("('voucher', 'Voucher')\nON CONFLICT (code) DO UPDATE SET label = excluded.label;", localSeed, StringComparison.Ordinal);
        Assert.Contains("CHECK (method IN ('card', 'transfer', 'cash', 'voucher'));", repo.Read("db/main/seed.sql"), StringComparison.Ordinal);

        // Dropping a column with a default: SQL Server needs its default constraint dropped first.
        ModelChanges.RemovePriority(repo);
        await repo.GenerateCleanlyAsync();
        AssertBefore(repo.Read("db/reporting/migrations/0003.sql"),
            "IF OBJECT_ID(N'dbo.df_customers_priority', 'D') IS NOT NULL ALTER TABLE dbo.customers DROP CONSTRAINT df_customers_priority;",
            "ALTER TABLE dbo.customers DROP COLUMN priority;");
        Assert.Contains("ALTER TABLE billing.customers DROP COLUMN priority;", repo.Read("db/main/migrations/0003.sql"), StringComparison.Ordinal);

        // The SQLite chain runs: every migration, then the seed twice (its lookup rows are upserts).
        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is not null)
        {
            var chain = string.Concat(new[] { "0001", "0002", "0003" }.Select(r => repo.Read($"db/local/migrations/{r}.sql")))
                + repo.Read("db/local/seed.sql") + repo.Read("db/local/seed.sql")
                + "\nSELECT 'methods=' || count(*) FROM payment_methods;\nSELECT 'region=' || count(*) FROM pragma_table_info('customers') WHERE name = 'region';\n";
            var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], repo.PathOf("db/local"), TimeSpan.FromMinutes(1), chain);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("methods=4", run.Output, StringComparison.Ordinal);
            Assert.Contains("region=1", run.Output, StringComparison.Ordinal);
        }

        var check = await repo.GenerateAsync(GenerationMode.Check);
        Assert.True(check.Outcome == RunOutcome.Succeeded, PackRepo.Describe(check));
    }

    [Fact]
    public async Task A_migration_that_adds_one_table_creates_it()
    {
        // One added table gives an empty foreign-key spec, which ddl_order reads as no tables: the migration (and a schema script
        // of a one-table database) must still create it.
        using var repo = PackRepo.Billing();
        var notes = repo.Read(".maquettiste/model/databases/main/tables/notes.json");
        File.Delete(repo.PathOf(".maquettiste/model/databases/main/tables/notes.json"));
        foreach (var entity in new[] { "invoice-note", "customer-note" })
            File.Delete(repo.PathOf(".maquettiste/model/entities/" + entity + ".json"));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        repo.Repo.WriteFile(".maquettiste/model/databases/main/tables/notes.json", notes);

        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var migration = repo.Read("db/main/migrations/0002.sql");
        Assert.Contains("CREATE TABLE billing.notes (", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ix_notes_entity_type_entity_id ON billing.notes (entity_type, entity_id);", migration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owned_files_keep_their_edits_and_regions_keep_their_bodies()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync();

        // once: the migration belongs to the team after the first write.
        var migration = repo.Read("db/main/migrations/0001.sql") + "-- reviewed\n";
        repo.Write("db/main/migrations/0001.sql", migration);
        // pair: the companion half of an entity likewise.
        var companion = repo.Read("src/Generated/Billing/Customer.cs").Replace("{\n}", "{\n    public override string ToString() => Name;\n}", StringComparison.Ordinal);
        repo.Write("src/Generated/Billing/Customer.cs", companion);
        // regions: text inside the keep region survives regeneration.
        var seed = repo.Read("db/main/seed.sql").Replace("-- INSERT INTO ... VALUES (...);", "INSERT INTO billing.customers (id) VALUES ('x');", StringComparison.Ordinal);
        repo.Write("db/main/seed.sql", seed);

        await repo.GenerateCleanlyAsync(force: true);
        Assert.Equal(migration, repo.Read("db/main/migrations/0001.sql"));
        Assert.Equal(companion, repo.Read("src/Generated/Billing/Customer.cs"));
        Assert.Equal(seed, repo.Read("db/main/seed.sql"));
        var check = await repo.GenerateAsync(GenerationMode.Check);
        Assert.True(check.Outcome == RunOutcome.Succeeded, PackRepo.Describe(check));

        // An edit outside the region is a hand edit.
        repo.Write("db/main/seed.sql", seed.Replace("Seed data of database", "Seed of database", StringComparison.Ordinal));
        var drift = await repo.GenerateAsync(GenerationMode.Check);
        Assert.Equal(RunOutcome.Conflicts, drift.Outcome);
    }

    [Fact]
    public async Task Schema_script_include_mode_points_at_the_table_scripts_in_dependency_order()
    {
        using var repo = PackRepo.BillingDialects();
        EditJson(repo, ".maquettiste/maquettiste.json", settings =>
            settings["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["schemaScript"] = "include" });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var pg = repo.Read("db/main/schema.sql").Split('\n').Where(l => l.StartsWith("\\ir ", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["\\ir billing/tables/customers.sql", "\\ir billing/tables/invoices.sql", "\\ir billing/tables/products.sql", "\\ir billing/tables/invoice_lines.sql", "\\ir billing/tables/notes.sql",
                "\\ir billing/tables/payments.sql", "\\ir billing/tables/payment_invoice.sql"],
            pg);
        foreach (var line in pg)
            Assert.True(File.Exists(repo.PathOf("db/main/" + line[4..])), line);
        // sqlcmd on Linux and macOS rejects "\\" in :r paths, and a GO after each include keeps later batches (views) first.
        Assert.Contains(":r dbo/tables/invoices.sql\nGO\n", repo.Read("db/reporting/schema.sql"), StringComparison.Ordinal);
        Assert.Contains(".read tables/invoices.sql", repo.Read("db/local/schema.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pack_parameters_move_the_csharp_output_and_pick_the_database()
    {
        using var repo = PackRepo.BillingDialects();
        EditJson(repo, ".maquettiste/maquettiste.json", settings =>
            settings["packs"]!["csharp-dapper"]!["parameters"] = new JsonObject
            {
                ["namespace"] = "Acme.Billing",
                ["database"] = "reporting",
                ["generatedFolder"] = "Model",
                ["partialFolder"] = "Partials",
            });
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        var repository = repo.Read("src/Generated/Model/Billing/InvoiceRepository.g.cs");
        Assert.Contains("namespace Acme.Billing.Billing;", repository, StringComparison.Ordinal);
        Assert.Contains("OUTPUT INSERTED", repo.Read("src/Generated/Model/Billing/PaymentRepository.g.cs"), StringComparison.Ordinal);
        Assert.Contains("OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY", repository, StringComparison.Ordinal);
        Assert.True(File.Exists(repo.PathOf("src/Generated/Partials/Billing/Invoice.cs")));
        Assert.True(File.Exists(repo.PathOf("src/Generated/Model/Billing/Invoice.g.cs")));
        Assert.True(File.Exists(repo.PathOf("src/Generated/Model/DapperTypeHandlers.g.cs")));
    }

    [Fact]
    public async Task Routines_database_types_and_sql_objects_migrate_by_drop_rename_and_todo()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        // A new body for the SQL Server function, a new name for the PostgreSQL domain, and the SQLite trigger removed.
        EditJson(repo, ".maquettiste/model/databases/reporting/routines/invoice-total.json", routine =>
            routine["body"]!["sqlserver"] = "BEGIN\n    RETURN 0;\nEND");
        EditJson(repo, ".maquettiste/model/databases/main/types/email-address.json", type => type["name"] = "contact_email");
        File.Delete(repo.PathOf(".maquettiste/model/databases/local/objects/invoices-keep-number.json"));
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var sqlServer = repo.Read("db/reporting/migrations/0002.sql");
        AssertBefore(sqlServer, "DROP FUNCTION IF EXISTS dbo.invoice_total;", "CREATE FUNCTION dbo.invoice_total(@invoice_id uniqueidentifier)");
        Assert.Contains("    RETURN 0;", sqlServer, StringComparison.Ordinal);
        var pg = repo.Read("db/main/migrations/0002.sql");
        Assert.Contains("ALTER DOMAIN billing.email_address RENAME TO contact_email;", pg, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO: database type", pg, StringComparison.Ordinal); // a rename is not a change of definition
        Assert.Contains("billing.contact_email", repo.Read("db/main/schema.sql"), StringComparison.Ordinal);
        Assert.Contains("-- TODO: trigger invoices_keep_number left the model; drop it by hand.", repo.Read("db/local/migrations/0002.sql"), StringComparison.Ordinal);
    }

    private static void EditJson(PackRepo repo, string path, Action<JsonNode> edit) => repo.EditJson(path, edit);

    [Fact]
    public async Task A_foreign_key_sets_only_the_columns_it_lists_on_delete_and_a_changed_list_replaces_the_key()
    {
        using var repo = PackRepo.Billing();
        const string tables = ".maquettiste/model/databases/main/tables/";
        repo.Write(tables + "tenant-folders.json", """
            {
              "$schema": "../../../../.schema/v1/table.json",
              "kind": "table",
              "id": "01K6FKS0000000000000000001",
              "name": "tenant_folders",
              "database": "01J92P0V1QRN2181XM2ZWE02W4",
              "schema": "01J92P0V1RC04SKQ5353EAKHG2",
              "columns": [
                { "id": "01K6FKS0000000000000000002", "name": "tenant_id", "type": "uuid", "nullable": false },
                { "id": "01K6FKS0000000000000000003", "name": "id", "type": "uuid", "nullable": false }
              ],
              "primaryKey": { "columns": ["01K6FKS0000000000000000002", "01K6FKS0000000000000000003"] }
            }
            """);
        string Documents(string onDeleteColumns) => $$"""
            {
              "$schema": "../../../../.schema/v1/table.json",
              "kind": "table",
              "id": "01K6FKS0000000000000000011",
              "name": "tenant_documents",
              "database": "01J92P0V1QRN2181XM2ZWE02W4",
              "schema": "01J92P0V1RC04SKQ5353EAKHG2",
              "columns": [
                { "id": "01K6FKS0000000000000000012", "name": "tenant_id", "type": "uuid", "nullable": false },
                { "id": "01K6FKS0000000000000000013", "name": "id", "type": "uuid", "nullable": false },
                { "id": "01K6FKS0000000000000000014", "name": "folder_id", "type": "uuid" }
              ],
              "primaryKey": { "columns": ["01K6FKS0000000000000000012", "01K6FKS0000000000000000013"] },
              "foreignKeys": [
                {
                  "id": "01K6FKS0000000000000000015",
                  "name": "fk_tenant_documents_folder",
                  "columns": ["01K6FKS0000000000000000012", "01K6FKS0000000000000000014"],
                  "referencesTable": "01K6FKS0000000000000000001",
                  "onDelete": "set-null"{{onDeleteColumns}}
                }
              ]
            }
            """;
        repo.Write(tables + "tenant-documents.json", Documents(", \"onDeleteColumns\": [\"01K6FKS0000000000000000014\"]"));
        await repo.GenerateCleanlyAsync();

        // Deleting a folder clears folder_id and keeps tenant_id, which is not nullable.
        Assert.Contains("FOREIGN KEY (tenant_id, folder_id) REFERENCES billing.tenant_folders (tenant_id, id) ON DELETE SET NULL (folder_id)",
            repo.Read("db/main/schema.sql"), StringComparison.Ordinal);

        repo.Write(tables + "tenant-documents.json", Documents(""));
        await repo.GenerateCleanlyAsync();
        var pg = repo.Read("db/main/migrations/0002.sql");
        AssertBefore(pg, "ALTER TABLE billing.tenant_documents DROP CONSTRAINT fk_tenant_documents_folder;",
            "ALTER TABLE billing.tenant_documents ADD CONSTRAINT fk_tenant_documents_folder FOREIGN KEY (tenant_id, folder_id) REFERENCES billing.tenant_folders (tenant_id, id) ON DELETE SET NULL;");
    }

    private static void AssertBefore(string text, string first, string second)
    {
        var a = text.IndexOf(first, StringComparison.Ordinal);
        var b = text.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0, "Missing: " + first + "\n" + text);
        Assert.True(b >= 0, "Missing: " + second + "\n" + text);
        Assert.True(a < b, "Expected\n  " + first + "\nbefore\n  " + second + "\nin\n" + text);
    }
}
