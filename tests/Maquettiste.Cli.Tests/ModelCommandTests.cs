using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary><c>maquettiste model export|stats</c>: the model as data, the same pages the editor API and the MCP server serve.</summary>
public sealed class ModelCommandTests
{
    [Fact]
    public async Task Export_writes_documents_as_a_json_array_or_one_per_line_with_fields_trimmed()
    {
        using var repo = CliRepo.Billing();

        var json = await repo.RunAsync("model", "export", "--kind", "entity", "--fields", "name,attributes");
        Assert.True(json.ExitCode == 0, json.Error);
        var array = JsonNode.Parse(json.Out)!.AsArray();
        Assert.Equal(["Customer", "Invoice", "InvoiceLine", "Payment", "Product"], array.Select(d => (string)d!["name"]!));
        Assert.All(array, d => Assert.Equal(["attributes", "id", "kind", "name"], d!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)));
        Assert.Contains("Exported 5 documents.", json.Error, StringComparison.Ordinal);

        var ndjson = await repo.RunAsync("model", "export", "--package", "Billing", "--format", "ndjson");
        Assert.True(ndjson.ExitCode == 0, ndjson.Error);
        var lines = Text.Lines(ndjson.Out);
        Assert.Equal(13, lines.Length);
        // A child package names its parent in parent; every other member names the package in package.
        Assert.All(lines.Select(l => JsonNode.Parse(l)!), d => Assert.Equal("01J92P0V01KDRN8GX5PGYCNKSX", (string)d[(string)d["kind"]! == "package" ? "parent" : "package"]!));
        Assert.Contains(lines, l => (string)JsonNode.Parse(l)!["name"]! == "Invoice");

        var output = Path.Combine(repo.Temp.Root, "out", "entities.ndjson");
        var toFile = await repo.RunAsync("model", "export", "--kind", "entity", "--format", "ndjson", "--out", output);
        Assert.True(toFile.ExitCode == 0, toFile.Error);
        Assert.Equal(5, Text.Lines(File.ReadAllText(output)).Length);

        var missing = await repo.RunAsync("model", "export", "--ids", "01J92P0V0FJ23CGSNKM7P1W5V9");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("no element has the id 01J92P0V0FJ23CGSNKM7P1W5V9", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_resolved_writes_the_records_of_a_scope()
    {
        using var repo = CliRepo.Billing();

        var entities = await repo.RunAsync("model", "export", "--resolved", "--scope", "entities", "--format", "ndjson");
        Assert.True(entities.ExitCode == 0, entities.Error);
        var records = Text.Lines(entities.Out).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(5, records.Count);
        Assert.All(records, r => Assert.Equal("entity", (string)r["kind"]!));
        Assert.Contains(records[0]["attributes"]!.AsArray(), a => (bool)a!["isKey"]!);

        var databases = await repo.RunAsync("model", "export", "--resolved", "--scope", "databases", "--database", "main");
        Assert.True(databases.ExitCode == 0, databases.Error);
        var database = Assert.Single(JsonNode.Parse(databases.Out)!.AsArray())!;
        Assert.Equal("main", (string)database["name"]!);
        Assert.NotEmpty(database["tables"]!.AsArray());

        Assert.Equal(4, (await repo.RunAsync("model", "export", "--resolved", "--scope", "widgets")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "export", "--resolved", "--kind", "entity")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "export", "--scope", "entities")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "export", "--resolved", "--database", "nowhere")).ExitCode);
    }

    [Fact]
    public async Task Export_resolved_on_a_model_with_errors_reports_them_and_exits_1()
    {
        using var repo = CliRepo.Billing();
        var path = repo.PathOf(".maquettiste/model/entities/payment.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("key");
        File.WriteAllText(path, node.ToJsonString());

        var result = await repo.RunAsync("model", "export", "--resolved");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("the model has errors", result.Error, StringComparison.Ordinal);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public async Task Stats_counts_the_kinds_and_per_package()
    {
        using var repo = CliRepo.Billing();

        var text = await repo.RunAsync("model", "stats");
        Assert.True(text.ExitCode == 0, text.Error);
        Assert.Contains(Text.Lines(text.Out), l => l.StartsWith("entity ", StringComparison.Ordinal) && l.EndsWith(" 5", StringComparison.Ordinal));
        Assert.StartsWith("total ", Text.Lines(text.Out)[^1], StringComparison.Ordinal);

        var json = await repo.RunAsync("model", "stats", "--by", "package", "--format", "json");
        Assert.True(json.ExitCode == 0, json.Error);
        var result = JsonNode.Parse(json.Out)!;
        Assert.Equal((int)result["total"]!, result["packages"]!.AsArray().Sum(p => (int)p!["count"]!));
        Assert.Contains(result["packages"]!.AsArray(), p => (string?)p!["name"] == "Billing");

        Assert.Equal(4, (await repo.RunAsync("model", "stats", "--by", "tag")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "count")).ExitCode);
    }
}
