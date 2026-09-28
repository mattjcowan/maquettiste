using System.Text.Json;
using Maquettiste.Bench;
using Maquettiste.Testing;

namespace Maquettiste.Cli.Tests;

public sealed class MigrateTests
{
    [Fact]
    public async Task Format_1_is_current()
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync("migrate");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Model format 1 is current.\n", result.Out);
    }

    [Fact]
    public async Task A_newer_format_exits_4_and_a_missing_or_invalid_one_exits_1()
    {
        using var repo = CliRepo.Billing();
        repo.Replace(".maquettiste/maquettiste.json", "\"formatVersion\": 1", "\"formatVersion\": 2");
        var newer = await repo.RunAsync("migrate");
        Assert.Equal(4, newer.ExitCode);
        Assert.Contains("newer", newer.Error, StringComparison.Ordinal);

        repo.Replace(".maquettiste/maquettiste.json", "\"formatVersion\": 2", "\"formatVersion\": \"one\"");
        Assert.Equal(1, (await repo.RunAsync("migrate")).ExitCode);

        repo.Write(".maquettiste/maquettiste.json", "{");
        Assert.Equal(1, (await repo.RunAsync("migrate")).ExitCode);

        File.Delete(repo.PathOf(".maquettiste/maquettiste.json"));
        var missing = await repo.RunAsync("migrate");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("no model found", missing.Error, StringComparison.Ordinal);
    }
}

public sealed class PackNewTests
{
    [Fact]
    public async Task Pack_new_scaffolds_a_pack_that_loads_and_renders()
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync("pack", "new", "docs");
        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal([".maquettiste/templates/docs/entity.scriban", ".maquettiste/templates/docs/helpers.js", ".maquettiste/templates/docs/pack.json"], Text.Lines(result.Out));
        var manifest = File.ReadAllBytes(repo.PathOf(".maquettiste/templates/docs/pack.json"));
        Assert.True(TestServices.Json.IsCanonical(manifest, "pack.json", "templates/docs/pack.json"));
        using (var doc = JsonDocument.Parse(manifest))
            Assert.Equal("docs", doc.RootElement.GetProperty("name").GetString());

        // Give it an output folder under the committed root and generate it.
        repo.Replace(".maquettiste/maquettiste.json", "\"packs\": {", "\"packs\": {\n    \"docs\": {\n      \"output\": \"db/docs\"\n    },");
        Assert.Equal(0, (await repo.RunAsync("validate")).ExitCode);
        var generate = await repo.RunAsync("generate", "--pack", "docs");
        Assert.True(generate.ExitCode == 0, generate.ToString());
        Assert.Contains("CUSTOMER", repo.Read("db/docs/customer.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pack_new_from_a_starter_renames_it()
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync("pack", "new", "my-sql", "--from", "sql-ddl");
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(repo.PathOf(".maquettiste/templates/my-sql/pack.json")));
        Assert.Equal("my-sql", doc.RootElement.GetProperty("name").GetString());
        Assert.True(doc.RootElement.GetProperty("units").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Pack_new_refuses_an_existing_folder()
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync("pack", "new", "ddl");
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
        Assert.Equal("ddl", JsonDocument.Parse(repo.Read(".maquettiste/templates/ddl/pack.json")).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Pack_new_needs_a_model()
    {
        using var repo = CliRepo.Empty();
        Assert.Equal(1, (await repo.RunAsync("pack", "new", "x")).ExitCode);
    }
}

public sealed class BenchTests
{
    [Fact]
    public async Task Bench_runs_the_harness_and_fails_the_file_budget_on_a_tiny_model()
    {
        using var repo = CliRepo.Empty();
        var result = await repo.RunAsync("bench", "--entities", "4", "--relations", "1", "--enums", "1", "--fanout", "1", "--out", repo.PathOf("bench"), "--format", "json");

        // A tiny model cannot reach the 100,000-file budget, so the report fails with exit 2.
        Assert.True(result.ExitCode == 2, result.ToString());
        Assert.DoesNotContain("internal error", result.Error, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(result.Out);
        Assert.False(doc.RootElement.GetProperty("passed").GetBoolean());
        var budgets = doc.RootElement.GetProperty("budgets").EnumerateArray().ToList();
        Assert.NotEmpty(budgets);
        Assert.Contains(budgets, b => b.GetProperty("name").GetString() == BenchmarkHarness.Files && !b.GetProperty("pass").GetBoolean());

        // The CLI writes the bench's own report format, so its JSON is a valid --baseline.
        Assert.NotEmpty(BenchmarkReportJson.ReadBudgets(System.Text.Encoding.UTF8.GetBytes(result.Out)));
        // The work folders are removed without --keep; the marker stays, so the folder can be reused.
        Assert.True(File.Exists(repo.PathOf("bench/" + Commands.BenchCommand.Marker)));
        Assert.False(Directory.Exists(repo.PathOf("bench/staged")));

        var again = await repo.RunAsync("bench", "--entities", "4", "--relations", "1", "--out", repo.PathOf("bench"));
        Assert.True(again.ExitCode == 2, again.ToString());
        Assert.Contains("Result: FAIL", again.Out, StringComparison.Ordinal);

        // Inside a test host the CLI is not its own process, so it measures no one-shot figure (a real maquettiste runs itself).
        Assert.Null(Commands.BenchCommand.OneShotSelf());
        Assert.False(doc.RootElement.GetProperty("incremental").TryGetProperty("oneShot", out _));

        // --no-example-packs measures the fanout pack alone, as the bench app's option does.
        var fanoutOnly = await repo.RunAsync("bench", "--entities", "4", "--relations", "1", "--out", repo.PathOf("bench"), "--no-example-packs", "--format", "json");
        Assert.True(fanoutOnly.ExitCode == 2, fanoutOnly.ToString());
        using var fanoutDoc = JsonDocument.Parse(fanoutOnly.Out);
        Assert.Equal(["fanout"], fanoutDoc.RootElement.GetProperty("model").GetProperty("packs").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Bench_rejects_bad_numbers()
    {
        using var repo = CliRepo.Empty();
        Assert.Equal(4, (await repo.RunAsync("bench", "--entities", "lots")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("bench", "--max-regression", "-1")).ExitCode);
    }

    [Theory]
    [InlineData("--entities", "1")]
    [InlineData("--enums", "0")]
    [InlineData("--fanout", "0")]
    public async Task Bench_rejects_values_below_the_generator_minimums(string option, string value)
    {
        using var repo = CliRepo.Empty();
        var result = await repo.RunAsync("bench", option, value, "--out", repo.PathOf("bench"));
        Assert.Equal(4, result.ExitCode);
        Assert.DoesNotContain("internal error", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.PathOf("bench")));
    }

    [Fact]
    public async Task Bench_rejects_more_relations_than_the_generator_accepts_without_a_stack_trace()
    {
        using var repo = CliRepo.Empty();
        var result = await repo.RunAsync("bench", "--entities", "3", "--relations", "2", "--out", repo.PathOf("bench"));
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--relations 2 is too many for 3 entities (at most 1", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("internal error", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bench_refuses_an_out_folder_it_did_not_make()
    {
        using var repo = CliRepo.Empty();
        repo.Write("work/staged/keep.txt", "mine\n");
        var result = await repo.RunAsync("bench", "--entities", "4", "--relations", "1", "--out", repo.PathOf("work"));
        Assert.Equal(4, result.ExitCode);
        Assert.Contains("not empty and was not made by maquettiste bench", result.Error, StringComparison.Ordinal);
        Assert.Equal("mine\n", repo.Read("work/staged/keep.txt"));
        Assert.False(File.Exists(repo.PathOf("work/" + Commands.BenchCommand.Marker)));
    }

    [Fact]
    public async Task A_regression_against_the_baseline_exits_2_and_is_printed()
    {
        using var temp = CliRepo.Empty();
        var budgets = new List<BudgetResult>
        {
            new(BenchmarkHarness.ColdTotal, 60, 10, true),
            new(BenchmarkHarness.Files, 100_000, 120_000, true) { Unit = "files", AtLeast = true },
        };
        var run = new BenchmarkReport([], TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 120_000, 8, 8, "test", budgets, [])
        {
            CheckOutcome = "Succeeded",
            OutputsMatch = true,
        };
        Assert.Equal(0, Commands.BenchCommand.ExitCode(run));

        // Non-identical outputs between the staged and pipelined cold runs fail the report too.
        Assert.Equal(2, Commands.BenchCommand.ExitCode(run with { OutputsMatch = false }));

        // The baseline ran the cold total in 5 s; this run's 10 s is a 100% slowdown, past the default 10%.
        var faster = run with { Budgets = [budgets[0] with { Actual = 5 }, budgets[1]] };
        var baseline = temp.PathOf("baseline.json");
        await File.WriteAllBytesAsync(baseline, BenchmarkReportJson.Write(faster), TestContext.Current.CancellationToken);
        var compared = await BenchmarkHarness.CompareAsync(run, baseline, new BenchmarkOptions().MaxRegressionPercent, TestContext.Current.CancellationToken);

        Assert.Contains(compared.Regressions, r => r.Name == BenchmarkHarness.ColdTotal && !r.Pass);
        Assert.True(compared.Budgets.All(b => b.Pass));
        Assert.Equal(2, Commands.BenchCommand.ExitCode(compared));
        Assert.Contains("REGRESSION", BenchmarkReportJson.ToText(compared), StringComparison.Ordinal);

        // A failed check run fails the report too.
        Assert.Equal(2, Commands.BenchCommand.ExitCode(run with { CheckOutcome = "Drift" }));
    }
}
