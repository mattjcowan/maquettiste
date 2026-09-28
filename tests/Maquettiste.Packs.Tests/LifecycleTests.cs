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
    public async Task A_model_change_writes_the_next_migration_once_and_keeps_the_earlier_one()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync();
        var first = repo.Read("db/main/migrations/0001.sql");
        Assert.Contains("CREATE TABLE billing.invoices (", first, StringComparison.Ordinal);

        ModelChanges.ChangeCustomer(repo);
        ModelChanges.AddFeaturedInvoice(repo);
        ModelChanges.AddDisputedStatus(repo);
        await repo.GenerateCleanlyAsync();

        Assert.Equal(first, repo.Read("db/main/migrations/0001.sql"));
        var pg = repo.Read("db/main/migrations/0002.sql");
        Assert.Contains("schema revision 1 to 2", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.customers ADD COLUMN phone varchar(30) NULL;", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.customers RENAME COLUMN name TO full_name;", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.customers ALTER COLUMN full_name TYPE varchar(200) USING full_name::varchar(200);", pg, StringComparison.Ordinal);
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
        Assert.Contains("EXEC sp_rename N'dbo.customers.name', N'full_name', N'COLUMN';", sqlServer, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE dbo.customers ADD phone nvarchar(30) NULL;", sqlServer, StringComparison.Ordinal);

        var sqlite = repo.Read("db/local/migrations/0002.sql");
        Assert.Contains("ALTER TABLE customers RENAME COLUMN name TO full_name;", sqlite, StringComparison.Ordinal);
        Assert.Contains("-- TODO (SQLite cannot change constraints in place; rebuild invoice_lines)", sqlite, StringComparison.Ordinal);
        AssertBefore(sqlite, "ALTER TABLE customers ADD COLUMN region text NULL;", "CREATE INDEX ix_customers_region ON customers (region);");
        // The new enum member reaches the lookup table through an idempotent upsert, not a "review by hand" note.
        Assert.Contains("(4, 'X', 'Disputed')\nON CONFLICT (id) DO UPDATE SET code = excluded.code, name = excluded.name;", sqlite, StringComparison.Ordinal);
        Assert.DoesNotContain("review by hand", sqlite, StringComparison.Ordinal);

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
                + "\nSELECT 'statuses=' || count(*) FROM invoice_statuses;\nSELECT 'region=' || count(*) FROM pragma_table_info('customers') WHERE name = 'region';\n";
            var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], repo.PathOf("db/local"), TimeSpan.FromMinutes(1), chain);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("statuses=5", run.Output, StringComparison.Ordinal);
            Assert.Contains("region=1", run.Output, StringComparison.Ordinal);
        }

        var check = await repo.GenerateAsync(GenerationMode.Check);
        Assert.True(check.Outcome == RunOutcome.Succeeded, PackRepo.Describe(check));
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
            ["\\ir billing/tables/customers.sql", "\\ir billing/tables/invoices.sql", "\\ir billing/tables/products.sql", "\\ir billing/tables/invoice_lines.sql", "\\ir billing/tables/payments.sql", "\\ir billing/tables/payment_invoice.sql"],
            pg);
        foreach (var line in pg)
            Assert.True(File.Exists(repo.PathOf("db/main/" + line[4..])), line);
        // sqlcmd on Linux and macOS rejects "\\" in :r paths, and a GO after each include keeps later batches (views) first.
        Assert.Contains(":r dbo/tables/invoices.sql\nGO\n", repo.Read("db/reporting/schema.sql"), StringComparison.Ordinal);
        Assert.Contains(".read tables/invoice_statuses.sql", repo.Read("db/local/schema.sql"), StringComparison.Ordinal);
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

    private static void EditJson(PackRepo repo, string path, Action<JsonNode> edit) => repo.EditJson(path, edit);

    private static void AssertBefore(string text, string first, string second)
    {
        var a = text.IndexOf(first, StringComparison.Ordinal);
        var b = text.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0, "Missing: " + first + "\n" + text);
        Assert.True(b >= 0, "Missing: " + second + "\n" + text);
        Assert.True(a < b, "Expected\n  " + first + "\nbefore\n  " + second + "\nin\n" + text);
    }
}
