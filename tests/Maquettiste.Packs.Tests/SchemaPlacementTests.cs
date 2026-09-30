namespace Maquettiste.Packs.Tests;

/// <summary>
/// Database schemas in sql-ddl (erratum E26): a conventional table goes to its entity mapping's schema, else the schema of the
/// nearest convention package entry, else the default; the schema script creates every schema the database declares.
/// </summary>
public sealed class SchemaPlacementTests
{
    [Fact]
    public async Task Tables_land_in_their_schemas_and_the_schema_script_creates_each_schema()
    {
        using var repo = PackRepo.Schemas();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        Assert.Contains("CREATE TABLE sales.orders", repo.Read("db/main/sales/tables/orders.sql"), StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE ops.logs", repo.Read("db/main/ops/tables/logs.sql"), StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE audit.audits", repo.Read("db/main/audit/tables/audits.sql"), StringComparison.Ordinal);
        var schema = repo.Read("db/main/schema.sql");
        foreach (var name in (string[])["sales", "ops", "audit"])
            Assert.Contains("CREATE SCHEMA IF NOT EXISTS " + name + ";", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE ops.logs", schema, StringComparison.Ordinal);
    }
}
