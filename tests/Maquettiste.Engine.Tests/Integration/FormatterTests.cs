using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// A formatter configured in maquettiste.json as an external command (a Python script the test writes), run by the real
/// formatter runner (integration task 12; engine-design.md section 13): over the files of rendered units only, and in memory in check mode.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class FormatterTests
{
    private const string Script = """
        import sys
        args = sys.argv[1:]
        if args[:1] == ["--version"]:
            print("sqlfmt 1.0.0")
            sys.exit(0)
        path, log = args[0], args[1]
        data = sys.stdin.buffer.read()
        with open(log, "a", encoding="utf-8") as f:
            f.write(path + "\n")
        sys.stdout.buffer.write(data.replace(b"CREATE TABLE", b"create table") + b"-- formatted\n")
        """;

    [Fact]
    public async Task An_external_formatter_runs_over_changed_files_only_and_check_formats_in_memory()
    {
        var python = Python();
        var folder = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "sqlfmt.py");
        var log = Path.Combine(folder, "calls.log");
        File.WriteAllText(script, Script.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        try
        {
            await using var repo = E2ERepo.Create(settings: s => s["formatters"] = new JsonArray(new JsonObject
            {
                ["name"] = "sqlfmt",
                ["extensions"] = new JsonArray(".sql"),
                ["command"] = python,
                ["args"] = new JsonArray(script, "{path}", log),
                ["version"] = "1.0.0",
                ["versionArgs"] = new JsonArray(script, "--version"),
            }));

            await repo.ApplyAsync();

            var sql = repo.Outputs().Where(f => f.Key.EndsWith(".sql", StringComparison.Ordinal)).ToList();
            Assert.Equal(12, sql.Count); // 6 e2e tables (committed) and 6 billing-demo tables (built)
            // A table whose entity has a description ends with its COMMENT ON statement (the comments convention), the others with ");".
            Assert.All(sql, f => Assert.EndsWith(";\n-- formatted\n", f.Value, StringComparison.Ordinal));
            Assert.All(sql, f => Assert.Contains("create table", f.Value, StringComparison.Ordinal));
            Assert.DoesNotContain("-- formatted", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal);
            Assert.Equal(sql.Select(f => f.Key).Order(StringComparer.Ordinal), Calls(log).Order(StringComparer.Ordinal));

            // Nothing changed: nothing is rendered, so the formatter does not run.
            File.Delete(log);
            var idle = await repo.ApplyAsync();
            Assert.Equal(0, idle.UnitsRendered);
            Assert.Empty(Calls(log));

            // One entity changed: only the files of the re-rendered units are formatted.
            await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
            var result = await repo.ApplyAsync();
            var calls = Calls(log);
            Assert.Contains("db/e2e/tables/customers.sql", calls);
            Assert.Contains("src/Generated/demo/db/billing/customers.sql", calls);
            Assert.DoesNotContain("db/e2e/tables/products.sql", calls);
            Assert.Equal(calls.Count, calls.Distinct().Count());
            Assert.True(calls.Count < 12, "formatted: " + string.Join(", ", calls));
            Assert.Contains(result.Changes, c => c.Path == "db/e2e/tables/customers.sql" && c.Kind == FileChangeKind.Modified);
            Assert.EndsWith("-- formatted\n", repo.Repo.ReadFile("db/e2e/tables/customers.sql"), StringComparison.Ordinal);

            // Check renders and formats every committed unit in memory, and agrees with what apply wrote.
            File.Delete(log);
            repo.AgeFiles();
            var times = repo.WriteTimes();
            var check = await repo.RunAsync(GenerationMode.Check);
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, check);
            Assert.Contains("db/e2e/tables/products.sql", Calls(log));
            Assert.Equal(times, repo.WriteTimes());
            Assert.Empty(Directory.EnumerateFiles(repo.Repo.RepoRoot, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_formatter_version_mismatch_stops_the_run_before_anything_is_written()
    {
        var python = Python();
        var folder = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "sqlfmt.py");
        File.WriteAllText(script, Script.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        try
        {
            await using var repo = E2ERepo.Create(demo: false, settings: s => s["formatters"] = new JsonArray(new JsonObject
            {
                ["name"] = "sqlfmt",
                ["extensions"] = new JsonArray(".sql"),
                ["command"] = python,
                ["args"] = new JsonArray(script, "{path}", Path.Combine(folder, "calls.log")),
                ["version"] = "9.9.9",
                ["versionArgs"] = new JsonArray(script, "--version"),
            }));

            var result = await repo.RunAsync();

            // The e2e units name no formatter (chosen by extension), yet the pinned version is checked before rendering: one
            // MQ6008 on the settings, and no file of any extension is written (engine-design.md section 13).
            E2ERepo.AssertOutcome(RunOutcome.Invalid, result);
            var mismatch = Assert.Single(result.Diagnostics);
            Assert.Equal("MQ6008", mismatch.Rule);
            Assert.Equal("/formatters/0", mismatch.JsonPointer);
            Assert.Equal(0, result.UnitsRendered);
            Assert.Empty(result.Changes);
            Assert.Empty(repo.Outputs());
            Assert.False(repo.Repo.Exists(".maquettiste/manifest/e2e.json"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static List<string> Calls(string log) =>
        File.Exists(log) ? [.. File.ReadAllLines(log).Where(l => l.Length > 0)] : [];

    private static string Python()
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe", "py.exe" } : ["python3", "python"])
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        Assert.Skip("No python on PATH for the external formatter.");
        return "";
    }
}
