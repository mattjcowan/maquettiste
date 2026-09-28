using System.Text.RegularExpressions;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The sql-ddl output for SQLite runs. The test project has no SQLite provider (its references are fixed by the scaffold), so the
/// scripts go through the <c>sqlite3</c> command-line shell when it is on the PATH; without it the test checks the scripts' shape.
/// <see cref="CompileTests"/> also executes the schema and seed scripts through Microsoft.Data.Sqlite when NuGet is reachable.
/// </summary>
public sealed partial class SqliteTests
{
    [Fact]
    public async Task Sqlite_scripts_execute_against_an_in_memory_database()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var folder = repo.PathOf("db/local");
        var tables = Directory.GetFiles(Path.Combine(folder, "tables"), "*.sql").Order(StringComparer.Ordinal).ToList();
        var schema = File.ReadAllText(Path.Combine(folder, "schema.sql"));
        var seed = File.ReadAllText(Path.Combine(folder, "seed.sql"));
        var migration = File.ReadAllText(Path.Combine(folder, "migrations", "0001.sql"));

        AssertShape(schema, tables.Count);
        AssertShape(migration, tables.Count);

        var sqlite3 = ProcessRunner.FindOnPath("sqlite3");
        if (sqlite3 is null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("sqlite3 is not on the PATH: checked the script shape only.");
            return;
        }

        // The schema script, then the seed (twice); the first migration, then the seed; every table script in isolation (SQLite
        // accepts forward foreign-key references, so any order works).
        var query = "\nSELECT 'tables=' || count(*) FROM sqlite_master WHERE type = 'table';\nSELECT 'statuses=' || count(*) FROM invoice_statuses;\n";
        // The seed runs twice: its lookup rows are upserts, so a second run on the same database succeeds.
        foreach (var script in new[] { schema + seed + seed + query, migration + seed + query })
        {
            var run = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1), script);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("tables=" + tables.Count, run.Output, StringComparison.Ordinal);
            Assert.Contains("statuses=4", run.Output, StringComparison.Ordinal);
        }

        var all = string.Concat(tables.Select(File.ReadAllText));
        var each = await ProcessRunner.RunAsync(sqlite3, ["-bail", ":memory:"], folder, TimeSpan.FromMinutes(1), all);
        Assert.True(each.ExitCode == 0, each.Output);
    }

    private static void AssertShape(string script, int tableCount)
    {
        var body = LineComment().Replace(script, "");
        Assert.Equal(tableCount, CreateTable().Matches(body).Count);
        Assert.Equal(body.Count(c => c == '('), body.Count(c => c == ')'));
        Assert.Equal(0, body.Count(c => c == '\'') % 2);
        Assert.DoesNotContain("[", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\nGO", body, StringComparison.Ordinal);
        Assert.DoesNotContain("IDENTITY", body, StringComparison.Ordinal);
        Assert.EndsWith(";", body.TrimEnd(), StringComparison.Ordinal);
        foreach (var statement in body.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
            Assert.Matches("^(CREATE TABLE|CREATE INDEX|CREATE UNIQUE INDEX|PRAGMA|BEGIN|COMMIT|INSERT INTO)", statement);
    }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"^CREATE TABLE ", RegexOptions.Multiline)]
    private static partial Regex CreateTable();
}
