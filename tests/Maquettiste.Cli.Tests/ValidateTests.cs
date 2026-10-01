using System.Text.Json;
using Maquettiste.Testing;

namespace Maquettiste.Cli.Tests;

public sealed class ValidateTests
{
    private const string CustomerFile = ".maquettiste/model/entities/customer.json";

    private static void BreakReference(CliRepo repo) =>
        // The email attribute's value object id no longer exists: a dangling reference (an error).
        repo.Replace(CustomerFile, "\"ref\": \"01J92P0V04TDYE2C73WMNXVDBV\"", "\"ref\": \"01J92P0V04TDYE2C73WMNXVDBZ\"");

    [Fact]
    public async Task A_clean_model_exits_0_with_no_diagnostics_on_stdout()
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync("validate");
        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.DoesNotContain(" error ", result.Out, StringComparison.Ordinal);
        Assert.Contains("Validation passed: 0 errors", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_exit_1_and_are_listed_as_text_lines()
    {
        using var repo = CliRepo.Billing();
        BreakReference(repo);
        var result = await repo.RunAsync("validate");
        Assert.Equal(1, result.ExitCode);
        var line = Assert.Single(Text.Lines(result.Out), l => l.Contains(" error ", StringComparison.Ordinal));
        Assert.StartsWith(CustomerFile, line, StringComparison.Ordinal);
        Assert.Matches(@"^\S+\(\d+,\d+\): error MQ\d{4}: ", line);
        Assert.Contains("Validation failed: 1 error", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_json_is_an_error_too()
    {
        using var repo = CliRepo.Billing();
        repo.Write(CustomerFile, "{ not json");
        var result = await repo.RunAsync("validate", "--quiet");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("MQ1001", result.Out, StringComparison.Ordinal);
        Assert.Equal("", result.Error);
    }

    [Fact]
    public async Task Json_output_matches_the_diagnostics_schema()
    {
        using var repo = CliRepo.Billing();
        BreakReference(repo);
        var result = await repo.RunAsync("validate", "--format", "json");
        Assert.Equal(1, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Out);
        Assert.Empty(TestServices.Schemas.Evaluate("diagnostics.json", doc.RootElement, "validate.json"));
        var root = doc.RootElement;
        Assert.Equal(["errors", "warnings", "infos", "diagnostics"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("errors").GetInt32());
        var error = root.GetProperty("diagnostics").EnumerateArray().Single(d => d.GetProperty("severity").GetString() == "error");
        Assert.Equal(CustomerFile, error.GetProperty("filePath").GetString());
        Assert.StartsWith("MQ", error.GetProperty("rule").GetString(), StringComparison.Ordinal);
        Assert.True(error.GetProperty("line").GetInt32() > 0);
        Assert.EndsWith("\n", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sarif_output_has_the_2_1_0_shape()
    {
        using var repo = CliRepo.Billing();
        BreakReference(repo);
        var result = await repo.RunAsync("validate", "--format", "sarif");
        Assert.Equal(1, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Out);
        var root = doc.RootElement;
        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
        Assert.Equal("maquettiste", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        var sarifResult = Assert.Single(run.GetProperty("results").EnumerateArray(), r => r.GetProperty("level").GetString() == "error");
        var ruleId = sarifResult.GetProperty("ruleId").GetString();
        Assert.Equal(ruleId, rules[sarifResult.GetProperty("ruleIndex").GetInt32()]);
        var location = sarifResult.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.Equal(CustomerFile, location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal("%SRCROOT%", location.GetProperty("artifactLocation").GetProperty("uriBaseId").GetString());
        Assert.True(location.GetProperty("region").GetProperty("startLine").GetInt32() > 0);
    }

    [Fact]
    public async Task Output_file_is_written_through_the_path_policy()
    {
        using var repo = CliRepo.Billing();
        var ok = await repo.RunAsync("validate", "--format", "sarif", "--output", repo.PathOf("db/reports/validation.sarif"));
        Assert.True(ok.ExitCode == 0, ok.ToString());
        Assert.Equal("", ok.Out);
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(repo.PathOf("db/reports/validation.sarif"))))
            Assert.Equal("2.1.0", doc.RootElement.GetProperty("version").GetString());

        // Outside every outputs.allow root (and inside .maquettiste): refused with MQ6004, exit 4.
        foreach (var refused in new[] { repo.PathOf("validation.sarif"), repo.PathOf(".maquettiste/validation.json"), Path.Combine(repo.Temp.Root, "out.json") })
        {
            var result = await repo.RunAsync("validate", "--format", "json", "--output", refused);
            Assert.Equal(4, result.ExitCode);
            Assert.Contains("MQ6004", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(refused));
        }
    }

    [Fact]
    public async Task Pack_errors_are_reported_by_validate()
    {
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/templates/ddl/pack.json", "{ \"name\": \"ddl\" }\n");
        var result = await repo.RunAsync("validate", "--format", "json");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("templates/ddl/pack.json", result.Out, StringComparison.Ordinal);
    }

    private const string SchemaFolder = ".maquettiste/.schema/v1";

    private static void WriteCurrentSchemas(CliRepo repo)
    {
        foreach (var name in TestServices.Schemas.FileNames)
            repo.Temp.WriteBytes(SchemaFolder + "/" + name, TestServices.Schemas.GetFileBytes(name).ToArray());
    }

    private static List<JsonElement> Mq1008(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("diagnostics").EnumerateArray().Where(d => d.GetProperty("rule").GetString() == "MQ1008").Select(d => d.Clone())];
    }

    [Fact]
    public async Task A_stale_schema_copy_is_a_MQ1008_warning_and_validate_leaves_it_as_it_is()
    {
        using var repo = CliRepo.Billing();
        WriteCurrentSchemas(repo);
        repo.Write(SchemaFolder + "/entity.json", "{}\n");
        repo.Write(SchemaFolder + "/retired.json", "{}\n");

        var result = await repo.RunAsync("validate", "--format", "json");
        Assert.True(result.ExitCode == 0, result.ToString());
        var warning = Assert.Single(Mq1008(result.Out));
        Assert.Equal("warning", warning.GetProperty("severity").GetString());
        Assert.Equal(".maquettiste/maquettiste.json", warning.GetProperty("filePath").GetString());
        var message = warning.GetProperty("message").GetString()!;
        Assert.Contains("entity.json (out of date), retired.json (no longer shipped)", message, StringComparison.Ordinal);
        Assert.Contains("maquettiste init", message, StringComparison.Ordinal);

        // Read-only: the stale and the extra copy are still there.
        Assert.Equal("{}\n", repo.Read(SchemaFolder + "/entity.json"));
        Assert.Equal("{}\n", repo.Read(SchemaFolder + "/retired.json"));

        // The text output carries it too, and generate reports it as well (a dry run writes nothing either).
        var text = await repo.RunAsync("validate");
        Assert.Contains(".maquettiste/maquettiste.json: warning MQ1008: ", text.Out, StringComparison.Ordinal);
        var plan = await repo.RunAsync("generate", "--dry-run");
        Assert.Contains("warning MQ1008: ", plan.Error, StringComparison.Ordinal);
        Assert.Equal("{}\n", repo.Read(SchemaFolder + "/entity.json"));
    }

    [Fact]
    public async Task A_current_schema_folder_raises_no_MQ1008_and_init_refreshes_a_stale_one()
    {
        using var repo = CliRepo.Billing();
        WriteCurrentSchemas(repo);
        var current = await repo.RunAsync("validate", "--format", "json");
        Assert.True(current.ExitCode == 0, current.ToString());
        Assert.Empty(Mq1008(current.Out));

        repo.Write(SchemaFolder + "/entity.json", "{}\n");
        var init = await repo.RunAsync("init", "--pack", "none");
        Assert.True(init.ExitCode == 0, init.ToString());
        Assert.DoesNotContain("MQ1008", init.Out + init.Error, StringComparison.Ordinal);
        Assert.Contains("wrote .maquettiste/.schema/v1/", init.Error, StringComparison.Ordinal);
        var after = await repo.RunAsync("validate", "--format", "json");
        Assert.Empty(Mq1008(after.Out));
    }

    [Fact]
    public async Task A_model_without_a_schema_folder_is_told_to_run_init()
    {
        using var repo = CliRepo.Billing();
        var result = await repo.RunAsync("validate", "--format", "json");
        var warning = Assert.Single(Mq1008(result.Out));
        Assert.Contains("actor.json (missing), batch.json (missing)", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains($", and {TestServices.Schemas.FileNames.Count - 5} more.", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(repo.PathOf(SchemaFolder)));
    }
}
