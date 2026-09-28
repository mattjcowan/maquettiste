using System.Text.Json;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Cli.Tests;

public sealed class GenerateTests
{
    private const string CustomerFile = ".maquettiste/model/entities/customer.json";
    private const string CustomersSql = "db/main/customers.sql";

    [Fact]
    public async Task Apply_writes_both_roots_lists_changes_and_a_rerun_skips_everything()
    {
        using var repo = CliRepo.Billing();
        var first = await repo.RunAsync("generate");
        Assert.True(first.ExitCode == 0, first.ToString());
        Assert.Contains("A " + CustomersSql, Text.Lines(first.Out));
        Assert.Contains("A src/Generated/Customer.g.cs", Text.Lines(first.Out));
        Assert.Contains("varchar(120)", repo.Read(CustomersSql), StringComparison.Ordinal);
        Assert.True(File.Exists(repo.PathOf(".maquettiste/manifest/ddl.json")));
        Assert.Contains("Generated: ", first.Error, StringComparison.Ordinal);
        Assert.Contains("[1/8 load] started", first.Error, StringComparison.Ordinal);
        Assert.Contains("[8/8 write] done", first.Error, StringComparison.Ordinal);

        var second = await repo.RunAsync("generate", "--format", "json");
        Assert.Equal(0, second.ExitCode);
        using var doc = JsonDocument.Parse(second.Out);
        Assert.Equal(0, doc.RootElement.GetProperty("unitsRendered").GetInt32());
        Assert.True(doc.RootElement.GetProperty("unitsSkipped").GetInt32() > 0);
        Assert.Empty(doc.RootElement.GetProperty("changes").EnumerateArray());

        var forced = await repo.RunAsync("generate", "--force", "--format", "json");
        using var forcedDoc = JsonDocument.Parse(forced.Out);
        Assert.Equal(0, forcedDoc.RootElement.GetProperty("unitsSkipped").GetInt32());
        Assert.Empty(forcedDoc.RootElement.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task A_rerun_with_nothing_changed_is_answered_from_the_last_run_record_with_the_same_result()
    {
        using var repo = CliRepo.Billing();
        Assert.Equal(0, (await repo.RunAsync("generate", "--quiet")).ExitCode);
        var record = Path.Combine(repo.CacheDirectory, "last-run.v1.bin");
        Assert.True(File.Exists(record), "a one-shot generate keeps the engine's last-run record");

        var answered = await repo.RunAsync("generate", "--format", "json");
        var answeredText = await repo.RunAsync("generate");
        File.Delete(record);
        var full = await repo.RunAsync("generate", "--format", "json");
        File.Delete(record);
        var fullText = await repo.RunAsync("generate");
        Assert.Equal(0, answered.ExitCode);
        Assert.Equal(full.Out, answered.Out);
        Assert.Equal(fullText.Out, answeredText.Out);
        string Summary(CliResult r) => Text.Lines(r.Error).Single(l => l.StartsWith("Generated: ", StringComparison.Ordinal));
        Assert.Equal(Summary(fullText), Summary(answeredText));
        Assert.DoesNotContain("[3/8 resolve]", answeredText.Error, StringComparison.Ordinal); // nothing was resolved
        Assert.Contains("[3/8 resolve]", fullText.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_passes_on_a_generated_repo_and_exits_2_on_drift()
    {
        using var repo = CliRepo.Billing();
        Assert.Equal(0, (await repo.RunAsync("generate")).ExitCode);
        var clean = await repo.RunAsync("generate", "--check");
        Assert.True(clean.ExitCode == 0, clean.ToString());
        Assert.Equal("", clean.Out);

        // Model drift: a stale committed file.
        repo.Replace(CustomerFile, "\"length\": 120", "\"length\": 150");
        var stale = await repo.RunAsync("generate", "--check");
        Assert.Equal(2, stale.ExitCode);
        Assert.Equal(["M " + CustomersSql], Text.Lines(stale.Out));
        Assert.Contains("Drift", stale.Error, StringComparison.Ordinal);
        Assert.Contains("varchar(120)", repo.Read(CustomersSql), StringComparison.Ordinal);
        var withDiff = await repo.RunAsync("generate", "--check", "--diff");
        Assert.Equal(2, withDiff.ExitCode);
        Assert.Contains("+    name varchar(150) NOT NULL,", Text.Lines(withDiff.Out));

        // Built roots are not checked: deleting a built file is no drift once the model matches again.
        repo.Replace(CustomerFile, "\"length\": 150", "\"length\": 120");
        File.Delete(repo.PathOf("src/Generated/Customer.g.cs"));
        Assert.Equal(0, (await repo.RunAsync("generate", "--check")).ExitCode);

        // A missing committed file is drift.
        File.Delete(repo.PathOf(CustomersSql));
        var missing = await repo.RunAsync("generate", "--check", "--format", "json");
        Assert.Equal(2, missing.ExitCode);
        using var doc = JsonDocument.Parse(missing.Out);
        Assert.Equal("drift", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("exitCode").GetInt32());
        var change = Assert.Single(doc.RootElement.GetProperty("changes").EnumerateArray());
        Assert.Equal(CustomersSql, change.GetProperty("path").GetString());
        Assert.Equal("added", change.GetProperty("kind").GetString());
        Assert.False(File.Exists(repo.PathOf(CustomersSql)));
    }

    [Fact]
    public async Task Check_reports_orphaned_committed_files_as_drift_and_apply_deletes_them()
    {
        using var repo = CliRepo.Billing();
        Assert.Equal(0, (await repo.RunAsync("generate")).ExitCode);
        // The pack moves its output: every old file is an orphan, every new one is missing.
        repo.Replace(".maquettiste/templates/ddl/pack.json", "}}/{{ table.name }}.sql", "}}/tables/{{ table.name }}.sql");

        var check = await repo.RunAsync("generate", "--check");
        Assert.Equal(2, check.ExitCode);
        var lines = Text.Lines(check.Out);
        Assert.Contains("D " + CustomersSql, lines);
        Assert.Contains("A db/main/tables/customers.sql", lines);
        Assert.True(File.Exists(repo.PathOf(CustomersSql)));

        var apply = await repo.RunAsync("generate");
        Assert.Equal(0, apply.ExitCode);
        Assert.False(File.Exists(repo.PathOf(CustomersSql)));
        Assert.True(File.Exists(repo.PathOf("db/main/tables/customers.sql")));
        Assert.Equal(0, (await repo.RunAsync("generate", "--check")).ExitCode);
    }

    [Fact]
    public async Task Hand_edits_exit_3_in_check_and_apply_and_the_file_is_kept()
    {
        using var repo = CliRepo.Billing();
        Assert.Equal(0, (await repo.RunAsync("generate")).ExitCode);
        repo.Write(CustomersSql, repo.Read(CustomersSql) + "-- mine\n");

        var check = await repo.RunAsync("generate", "--check");
        Assert.Equal(3, check.ExitCode);
        Assert.Contains("MQ6009", check.Error, StringComparison.Ordinal);

        var apply = await repo.RunAsync("generate", "--force");
        Assert.Equal(3, apply.ExitCode);
        Assert.Contains("C " + CustomersSql, Text.Lines(apply.Out));
        Assert.EndsWith("-- mine\n", repo.Read(CustomersSql), StringComparison.Ordinal);

        var overwrite = await repo.RunAsync("generate", "--force", "--hand-edits", "overwrite");
        Assert.Equal(0, overwrite.ExitCode);
        Assert.DoesNotContain("-- mine", repo.Read(CustomersSql), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_model_exits_1_before_anything_is_written()
    {
        using var repo = CliRepo.Billing();
        repo.Replace(CustomerFile, "\"ref\": \"01J92P0V04TDYE2C73WMNXVDBV\"", "\"ref\": \"01J92P0V04TDYE2C73WMNXVDBZ\"");
        foreach (var args in new[] { new[] { "generate" }, ["generate", "--check"], ["generate", "--dry-run"] })
        {
            var result = await repo.RunAsync(args);
            Assert.Equal(1, result.ExitCode);
            Assert.Contains(": error MQ", result.Error, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(repo.PathOf("db")));
    }

    [Fact]
    public async Task A_template_error_exits_1()
    {
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/templates/ddl/table.scriban", "{{ for c in table.columns }}\n");
        var result = await repo.RunAsync("generate");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("templates/ddl/table.scriban", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_diff_prints_the_plan_and_unified_diffs_without_writing()
    {
        using var repo = CliRepo.Billing();
        Assert.Equal(0, (await repo.RunAsync("generate")).ExitCode);
        var manifest = File.ReadAllBytes(repo.PathOf(".maquettiste/manifest/ddl.json"));
        repo.Replace(CustomerFile, "\"length\": 120", "\"length\": 150");

        var result = await repo.RunAsync("generate", "--dry-run", "--diff");
        Assert.True(result.ExitCode == 0, result.ToString());
        var lines = Text.Lines(result.Out);
        Assert.Equal("M " + CustomersSql, lines[0]);
        Assert.Contains("--- a/" + CustomersSql, lines);
        Assert.Contains("+++ b/" + CustomersSql, lines);
        Assert.Contains(lines, l => l.StartsWith("@@ ", StringComparison.Ordinal));
        Assert.Contains("-    name varchar(120) NOT NULL,", lines);
        Assert.Contains("+    name varchar(150) NOT NULL,", lines);
        Assert.Contains("Dry run: ", result.Error, StringComparison.Ordinal);

        Assert.Contains("varchar(120)", repo.Read(CustomersSql), StringComparison.Ordinal);
        Assert.Equal(manifest, File.ReadAllBytes(repo.PathOf(".maquettiste/manifest/ddl.json")));

        // Without --diff: the list only. JSON carries the diff when asked.
        var plain = await repo.RunAsync("generate", "--dry-run");
        Assert.DoesNotContain("--- a/", plain.Out, StringComparison.Ordinal);
        var json = await repo.RunAsync("generate", "--dry-run", "--diff", "--format", "json");
        using var doc = JsonDocument.Parse(json.Out);
        var change = doc.RootElement.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("path").GetString() == CustomersSql);
        Assert.Contains("+    name varchar(150) NOT NULL,", change.GetProperty("diff").GetString(), StringComparison.Ordinal);
        Assert.Equal("dry-run", doc.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Json_result_has_the_documented_shape()
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync("generate", "--format", "json");
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Out);
        var root = doc.RootElement;
        Assert.Equal(["mode", "outcome", "exitCode", "unitsRendered", "unitsSkipped", "filesWritten", "filesDeleted", "changes", "diagnostics"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("apply", root.GetProperty("mode").GetString());
        Assert.Equal("succeeded", root.GetProperty("outcome").GetString());
        var changes = root.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(root.GetProperty("filesWritten").GetInt32(), changes.Count);
        Assert.All(changes, c =>
        {
            Assert.Equal("added", c.GetProperty("kind").GetString());
            Assert.Matches("^[0-9a-f]{64}$", c.GetProperty("newHash").GetString());
            Assert.Contains(c.GetProperty("pack").GetString(), new[] { "ddl", "classes" });
        });
        var paths = changes.Select(c => c.GetProperty("path").GetString()!).ToList();
        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
    }

    [Fact]
    public async Task Pack_and_roots_filters_limit_the_run()
    {
        using var repo = CliRepo.Billing();
        var packOnly = await repo.RunAsync("generate", "--pack", "classes");
        Assert.Equal(0, packOnly.ExitCode);
        Assert.False(Directory.Exists(repo.PathOf("db")));
        Assert.NotEmpty(repo.Tree("src/Generated"));

        using var built = CliRepo.Billing();
        var builtOnly = await built.RunAsync("generate", "--roots", "built");
        Assert.Equal(0, builtOnly.ExitCode);
        Assert.False(Directory.Exists(built.PathOf("db")));
        Assert.NotEmpty(built.Tree("src/Generated"));
    }

    [Fact]
    public async Task Output_is_byte_identical_with_one_job_and_eight()
    {
        using var one = CliRepo.Billing();
        using var eight = CliRepo.Billing();
        Assert.Equal(0, (await one.RunAsync("generate", "--jobs", "1")).ExitCode);
        Assert.Equal(0, (await eight.RunAsync("generate", "--jobs", "8")).ExitCode);
        foreach (var folder in new[] { "db", "src/Generated", ".maquettiste/manifest" })
        {
            var a = one.Tree(folder);
            var b = eight.Tree(folder);
            Assert.Equal(a.Keys.Order(StringComparer.Ordinal), b.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, bytes) in a)
                Assert.Equal(bytes, b[path]);
        }
    }

    [Fact]
    public async Task A_busy_lock_exits_4_with_no_wait()
    {
        using var repo = CliRepo.Billing();
        Assert.Equal(0, (await repo.RunAsync("migrate")).ExitCode);
        var options = repo.Temp.Options with { CacheDirectory = repo.CacheDirectory };
        var runLock = new RunLock(options, new OutputPathPolicy(options, null));
        var held = await runLock.AcquireAsync(wait: false, TestContext.Current.CancellationToken);
        Assert.NotNull(held);
        await using (held)
        {
            var result = await repo.RunAsync("generate", "--no-wait");
            Assert.Equal(4, result.ExitCode);
            Assert.Contains("run lock", result.Error, StringComparison.Ordinal);
        }

        Assert.Equal(0, (await repo.RunAsync("generate", "--no-wait")).ExitCode);
    }

    [Fact]
    public async Task Cancellation_exits_4()
    {
        using var repo = CliRepo.Billing();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var output = new SharedWriter();
        var error = new SharedWriter();
        var code = await CliRepo.RunWithAsync(repo.RepoRoot, output, error, cts.Token, _ => null,
            "generate", "--repo", repo.RepoRoot, "--cache-dir", repo.CacheDirectory);
        Assert.Equal(4, code);
        Assert.Contains("ancel", error.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quiet_writes_nothing_on_success_and_json_progress_is_one_object_per_line()
    {
        using var repo = CliRepo.Billing();
        var quiet = await repo.RunAsync("generate", "--quiet");
        Assert.True(quiet.ExitCode == 0, quiet.ToString());
        Assert.Equal("", quiet.Out);
        Assert.Equal("", quiet.Error);

        using var other = CliRepo.Billing();
        var json = await other.RunAsync("generate", "--progress", "json", "--format", "json");
        Assert.True(json.ExitCode == 0, json.ToString());
        var progressLines = Text.Lines(json.Error).Where(l => l.StartsWith('{')).ToList();
        Assert.NotEmpty(progressLines);
        var stages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in progressLines)
        {
            using var doc = JsonDocument.Parse(line);
            stages.Add(doc.RootElement.GetProperty("stage").GetString()!);
            Assert.True(doc.RootElement.GetProperty("done").GetInt32() >= 0);
        }

        Assert.Contains("load", stages);
        Assert.Contains("write", stages);
        using (JsonDocument.Parse(json.Out))
        {
        }
    }

    [Fact]
    public async Task Detailed_verbosity_adds_stage_timings()
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync("generate", "--verbosity", "detailed", "--progress", "none");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[6/8 render] wall ", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("started", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Kind_names_cover_every_change_kind() =>
        Assert.All(Enum.GetValues<FileChangeKind>(), k => Assert.Matches("^[a-z-]+$", Commands.GenerateCommand.KindName(k)));
}
