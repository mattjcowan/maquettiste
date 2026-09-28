using System.Text.RegularExpressions;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// A foreign-key cycle (Customer references a featured Invoice, Invoice references its Customer): PostgreSQL and SQL Server
/// reject a CREATE TABLE whose foreign key names a table that does not exist yet, so the schema script and the first migration
/// leave the cycle-closing key out and add it with ALTER TABLE after the tables exist. SQLite keeps it inline.
/// </summary>
public sealed partial class CycleTests
{
    [Theory]
    [InlineData("db/main/schema.sql", "ALTER TABLE billing.invoices ADD CONSTRAINT fk_invoices_customer_id FOREIGN KEY (customer_id) REFERENCES billing.customers (id)")]
    [InlineData("db/main/migrations/0001.sql", "ALTER TABLE billing.invoices ADD CONSTRAINT fk_invoices_customer_id FOREIGN KEY (customer_id) REFERENCES billing.customers (id)")]
    [InlineData("db/reporting/schema.sql", "ALTER TABLE dbo.invoices ADD CONSTRAINT fk_invoices_customer_id FOREIGN KEY (customer_id) REFERENCES dbo.customers (id)")]
    [InlineData("db/reporting/migrations/0001.sql", "ALTER TABLE dbo.invoices ADD CONSTRAINT fk_invoices_customer_id FOREIGN KEY (customer_id) REFERENCES dbo.customers (id)")]
    public async Task Every_referenced_table_exists_before_the_foreign_key_that_names_it(string path, string deferred)
    {
        using var repo = PackRepo.BillingDialects();
        ModelChanges.AddFeaturedInvoice(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var script = repo.Read(path);

        Assert.Contains(deferred, script, StringComparison.Ordinal);
        AssertReferencesResolve(script);
    }

    [Fact]
    public async Task Include_mode_writes_a_table_in_a_cycle_inline_and_adds_its_key_afterwards()
    {
        using var repo = PackRepo.BillingDialects();
        ModelChanges.AddFeaturedInvoice(repo);
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
            settings["packs"]!["sql-ddl"]!["parameters"] = new System.Text.Json.Nodes.JsonObject { ["schemaScript"] = "include" });
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        var pg = repo.Read("db/main/schema.sql");
        Assert.DoesNotContain("\\ir billing/tables/invoices.sql", pg, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE billing.invoices (", pg, StringComparison.Ordinal);
        Assert.Contains("\\ir billing/tables/customers.sql", pg, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE billing.invoices ADD CONSTRAINT fk_invoices_customer_id", pg, StringComparison.Ordinal);
        Assert.Contains(".read tables/invoices.sql", repo.Read("db/local/schema.sql"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sqlite_keeps_the_cycle_inline_and_the_schema_runs()
    {
        using var repo = PackRepo.BillingDialects();
        ModelChanges.AddFeaturedInvoice(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var schema = repo.Read("db/local/schema.sql");
        Assert.Contains("CONSTRAINT fk_invoices_customer_id FOREIGN KEY (customer_id) REFERENCES customers (id)", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", schema, StringComparison.Ordinal);

        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
            return;
        foreach (var script in new[] { schema, repo.Read("db/local/migrations/0001.sql") })
        {
            var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], repo.PathOf("db/local"), TimeSpan.FromMinutes(1), script);
            Assert.True(run.ExitCode == 0, run.Output);
        }
    }

    /// <summary>Walks the statements in order: a table named after REFERENCES must have been created by an earlier statement.</summary>
    private static void AssertReferencesResolve(string script)
    {
        var created = new HashSet<string>(StringComparer.Ordinal);
        var body = GoLine().Replace(LineComment().Replace(script, ""), "");
        foreach (var statement in body.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var create = CreateTable().Match(statement);
            foreach (Match reference in References().Matches(statement))
            {
                var target = reference.Groups[1].Value;
                Assert.True(created.Contains(target) || (create.Success && create.Groups[1].Value == target),
                    $"{target} is referenced before it is created:\n{statement}");
            }

            if (create.Success)
                created.Add(create.Groups[1].Value);
        }

        Assert.NotEmpty(created);
    }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"^GO$", RegexOptions.Multiline)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"^CREATE TABLE (\S+) \(")]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"REFERENCES (\S+) \(")]
    private static partial Regex References();
}
