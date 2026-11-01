using System.Text.Json.Nodes;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Model;

namespace Maquettiste.Cli.Tests;

/// <summary><c>maquettiste migration ledger</c> (next-phase-1-design.md section 3.3, task A21).</summary>
public sealed class MigrationLedgerCommandTests
{
    private const string MainDatabaseId = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string Settings = ".maquettiste/maquettiste.json";
    private const string MainDatabase = ".maquettiste/model/databases/main/database.json";

    [Fact]
    public async Task Prints_the_ddl_and_the_read_grant_for_the_configured_schema()
    {
        using var repo = CliRepo.Billing();

        // The default schema, and only the DDL without --grant-read.
        var plain = await repo.RunAsync("migration", "ledger");
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.Equal(LedgerContract.Ddl(Dialect.PostgreSql, "migration_ledger"), plain.Out);
        Assert.StartsWith("CREATE SCHEMA IF NOT EXISTS migration_ledger;\n", plain.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT", plain.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN;", plain.Out, StringComparison.Ordinal); // no transaction control: the caller's transaction holds it
        Assert.DoesNotContain("COMMIT", plain.Out, StringComparison.Ordinal);
        Assert.Contains("contract version 1 for database main (postgresql), schema migration_ledger", plain.Error, StringComparison.Ordinal);

        // The configured schema reaches the DDL and the grant; the database by name or id.
        repo.Replace(Settings, "\"formatVersion\": 1,", "\"formatVersion\": 1,\n  \"migrations\": {\n    \"ledgerSchema\": \"billing_ledger\"\n  },");
        foreach (var database in new[] { "main", MainDatabaseId })
        {
            var text = await repo.RunAsync("migration", "ledger", "--database", database, "--grant-read", "reporting");
            Assert.True(text.ExitCode == 0, text.ToString());
            Assert.Equal(LedgerContract.Ddl(Dialect.PostgreSql, "billing_ledger") + "\n" + LedgerContract.ReadGrant(Dialect.PostgreSql, "billing_ledger", "reporting"), text.Out);
            Assert.Contains("CREATE TABLE IF NOT EXISTS billing_ledger.applied_fact (", text.Out, StringComparison.Ordinal);
            Assert.EndsWith(
                "GRANT USAGE ON SCHEMA billing_ledger TO reporting;\n" +
                "GRANT SELECT ON billing_ledger.contract, billing_ledger.applied_fact, billing_ledger.applied_object TO reporting;\n",
                text.Out, StringComparison.Ordinal);
        }

        // JSON is the answer record: grant is null unless asked, and present either way.
        var json = JsonNode.Parse((await repo.RunAsync("migration", "ledger", "--format", "json")).Out)!.AsObject();
        Assert.Equal(1, (int)json["contractVersion"]!);
        Assert.Equal(("main", "postgresql", "billing_ledger"), ((string)json["database"]!, (string)json["dialect"]!, (string)json["schema"]!));
        Assert.Equal(LedgerContract.Ddl(Dialect.PostgreSql, "billing_ledger"), (string)json["ddl"]!);
        Assert.True(json.ContainsKey("grant"));
        Assert.Null(json["grant"]);
        var granted = JsonNode.Parse((await repo.RunAsync("migration", "ledger", "--format", "json", "--grant-read", "Report Readers")).Out)!;
        Assert.StartsWith("GRANT USAGE ON SCHEMA billing_ledger TO \"Report Readers\";\n", (string)granted["grant"]!, StringComparison.Ordinal);

        // Usage errors exit 4.
        Assert.Equal(4, (await repo.RunAsync("migration", "ledger", "--grant-read", "")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("migration", "ledger", "--against", "x")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("migration", "ledger", "extra")).ExitCode);
    }

    [Fact]
    public async Task Exits_1_without_a_postgresql_database()
    {
        using var repo = CliRepo.Billing();
        var unknown = await repo.RunAsync("migration", "ledger", "--database", "nowhere");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("nowhere", unknown.Error, StringComparison.Ordinal);
        Assert.Equal("", unknown.Out);

        // A second PostgreSQL database: one must be named.
        repo.Write(".maquettiste/model/databases/reporting/database.json",
            "{\n  \"$schema\": \"../../../.schema/v1/database.json\",\n  \"kind\": \"database\",\n  \"id\": \"01K9QDB0000000000000000001\",\n" +
            "  \"name\": \"reporting\",\n  \"dialect\": \"postgresql\",\n  \"version\": \"16\"\n}\n");
        var several = await repo.RunAsync("migration", "ledger");
        Assert.Equal(1, several.ExitCode);
        Assert.Contains("main, reporting", several.Error, StringComparison.Ordinal);
        var named = await repo.RunAsync("migration", "ledger", "--database", "reporting", "--format", "json");
        Assert.True(named.ExitCode == 0, named.ToString());
        Assert.Equal("reporting", (string)JsonNode.Parse(named.Out)!["database"]!);
        File.Delete(repo.PathOf(".maquettiste/model/databases/reporting/database.json"));

        // No PostgreSQL database: the ledger exists on PostgreSQL only, named or not.
        repo.Replace(MainDatabase, "\"dialect\": \"postgresql\"", "\"dialect\": \"sqlite\"");
        var none = await repo.RunAsync("migration", "ledger");
        Assert.Equal(1, none.ExitCode);
        Assert.Contains("no PostgreSQL database", none.Error, StringComparison.Ordinal);
        var other = await repo.RunAsync("migration", "ledger", "--database", "main");
        Assert.Equal(1, other.ExitCode);
        Assert.Contains("is sqlite", other.Error, StringComparison.Ordinal);
        Assert.Equal("", other.Out);
    }

    [Fact]
    public async Task Writes_output_through_the_path_policy()
    {
        using var repo = CliRepo.Billing();
        var target = repo.PathOf("db/ledger/ledger.sql");
        var ok = await repo.RunAsync("migration", "ledger", "--grant-read", "reporting", "--output", target);
        Assert.True(ok.ExitCode == 0, ok.ToString());
        Assert.Equal("", ok.Out);
        Assert.Equal(LedgerContract.Ddl(Dialect.PostgreSql, "migration_ledger") + "\n" + LedgerContract.ReadGrant(Dialect.PostgreSql, "migration_ledger", "reporting"),
            File.ReadAllText(target));

        // Outside every outputs.allow root (and inside .maquettiste): refused with MQ6004, exit 4, nothing written.
        foreach (var refused in new[] { repo.PathOf("ledger.sql"), repo.PathOf(".maquettiste/ledger.sql"), Path.Combine(repo.Temp.Root, "ledger.sql") })
        {
            var result = await repo.RunAsync("migration", "ledger", "--output", refused);
            Assert.Equal(4, result.ExitCode);
            Assert.Contains("MQ6004", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(refused));
        }
    }
}
