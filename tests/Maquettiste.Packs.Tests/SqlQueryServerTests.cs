using Maquettiste.Engine;
using Maquettiste.Engine.Rendering;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The sql-queries fixture on real PostgreSQL and SQL Server servers (sql-queries-design.md section 21): the sql-ddl schema, a few
/// rows, then the statements the engine renders for the recursive named query, the query written as SQL text (its PostgreSQL text,
/// and its <c>"*"</c> text on SQL Server), the command and the binding's select over the SQL text. PostgreSQL runs them with numbered
/// placeholders (<c>PREPARE … AS … $1</c>, then <c>EXECUTE</c>), SQL Server with <c>@name</c> variables, as the libraries that take
/// each style do. Each test skips unless its container is named in the environment (<see cref="DbServer"/>).
/// </summary>
public sealed class SqlQueryServerTests
{
    private const string CategoryAncestors = "01K9Q550000000000000000040";
    private const string TopCustomersByRegion = "01K9Q550000000000000000041";
    private const string MarkOverdueInvoices = "01K9Q550000000000000000042";
    private const string RegionLeader = "01K9Q550000000000000000050";
    private const string RegionLeaderBinding = "01K9Q550000000000000000055";

    private const string Rows = """
        INSERT INTO categories (id, parent_id, name) VALUES (1, NULL, 'All'), (2, 1, 'Bikes'), (3, 2, 'E-bikes');
        INSERT INTO customers (id, name, region) VALUES (1, 'Ada', 'north'), (2, 'Bob', 'north'), (3, 'Cy', 'south');
        INSERT INTO invoices (id, customer_id, total, status, issued_on, due_on) VALUES
            (1, 1, 100.25, 'issued', '2026-01-10', '2026-02-10'),
            (2, 2, 50.00, 'issued', '2026-01-15', '2026-03-15'),
            (3, 3, 70.00, 'paid', '2026-02-01', '2026-03-01'),
            (4, 1, 20.00, 'issued', '2026-05-01', '2026-06-01');

        """;

    /// <summary>Each parameter's value as a literal, and its SQL Server type.</summary>
    private static readonly Dictionary<string, (string Literal, string Type)> Values = new(StringComparer.Ordinal)
    {
        ["categoryId"] = ("3", "bigint"),
        ["fromDate"] = ("'2026-01-01'", "date"),
        ["toDate"] = ("'2026-03-31'", "date"),
        ["perRegion"] = ("1", "int"),
        ["asOf"] = ("'2026-03-01'", "date"),
    };

    [Fact]
    public Task PostgreSQL_runs_the_sql_queries_fixture() => RunAsync(DbServer.Postgres());

    [Fact]
    public Task Sql_server_runs_the_sql_queries_fixture() => RunAsync(DbServer.SqlServer());

    private static async Task RunAsync(DbServer? server)
    {
        if (server is null)
        {
            Assert.Skip("Set MAQUETTISTE_TEST_POSTGRES_CONTAINER, or MAQUETTISTE_TEST_SQLSERVER_CONTAINER and MAQUETTISTE_TEST_SQLSERVER_PASSWORD, to run the queries on a real server.");
            return;
        }

        var postgres = server.Dialect == "postgresql";
        using var repo = PackRepo.SqlQueries(postgres ? null : "sqlserver");
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var schema = repo.Read("db/main/schema.sql") + (postgres ? "" : "\nGO\n");
        var options = new QuerySqlOptions { Placeholder = postgres ? "$" : "@" };

        await using var store = new ModelStore(repo.Repo.Options);
        var service = new GenerationService(store, repo.Repo.Options);
        var ct = TestContext.Current.CancellationToken;

        async Task<IReadOnlyList<string>> Run(string label, string sql, IReadOnlyList<string> parameters, string after = "")
        {
            var script = postgres
                ? $"PREPARE q AS {sql};\nEXECUTE q({string.Join(", ", parameters.Select(p => Values[p].Literal))});\n{after}"
                : "SET NOCOUNT ON;\n" + (parameters.Count == 0 ? "" : "DECLARE " + string.Join(", ", parameters.Select(p => $"@{p} {Values[p].Type} = {Values[p].Literal}")) + ";\n")
                  + sql + ";\n" + after + "GO\n";
            var output = await server.RunAsync("sq_" + label, schema, Rows + (postgres ? "" : "GO\n"), script);
            return [.. output.Split('\n').Select(l => string.Join(' ', l.Replace('|', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))).Where(l => l.Length > 0)];
        }

        async Task<IReadOnlyList<string>> RunQuery(string label, string id, string after = "")
        {
            var result = await service.GetQuerySqlAsync(id, null, options, ct);
            Assert.True(result.Preview is not null, string.Join('\n', result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
            return await Run(label, result.Preview.Sql, result.Preview.Parameters, after);
        }

        // The recursive named query, in order.
        Assert.Equal(["3 E-bikes 0", "2 Bikes 1", "1 All 2"], await RunQuery("ancestors", CategoryAncestors));

        // The SQL text with a window function: each region's best customer.
        Assert.Equal(["north 1 100.25 1", "south 3 70.00 1"], (await RunQuery("top", TopCustomersByRegion)).Order(StringComparer.Ordinal));

        // The command: one issued invoice is due before the date.
        Assert.Equal(["1"], await RunQuery("overdue", MarkOverdueInvoices, "SELECT COUNT(*) FROM invoices WHERE status = 'overdue';\n"));

        // The binding's select reads the SQL text as a derived table, its parameters first.
        var binding = await service.GetBindingSqlAsync(RegionLeader, RegionLeaderBinding, null, options, ct);
        Assert.True(binding.Preview?.Select is not null, string.Join('\n', binding.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(["1 north 100.25 1", "3 south 70.00 1"],
            (await Run("leaders", binding.Preview.Select.Sql, binding.Preview.Select.Parameters)).Order(StringComparer.Ordinal));
    }
}
