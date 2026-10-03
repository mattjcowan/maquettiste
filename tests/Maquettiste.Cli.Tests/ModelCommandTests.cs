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
        Assert.Equal(["Customer", "CustomerNote", "Invoice", "InvoiceLine", "InvoiceNote", "Payment", "Product", "RevenueMonth"], array.Select(d => (string)d!["name"]!));
        Assert.All(array, d => Assert.Equal(["attributes", "id", "kind", "name"], d!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)));
        Assert.Contains("Exported 8 documents.", json.Error, StringComparison.Ordinal);

        var ndjson = await repo.RunAsync("model", "export", "--package", "Billing", "--format", "ndjson");
        Assert.True(ndjson.ExitCode == 0, ndjson.Error);
        var lines = Text.Lines(ndjson.Out);
        Assert.Equal(16, lines.Length);
        // A child package names its parent in parent; every other member names the package in package.
        Assert.All(lines.Select(l => JsonNode.Parse(l)!), d => Assert.Equal("01J92P0V01KDRN8GX5PGYCNKSX", (string)d[(string)d["kind"]! == "package" ? "parent" : "package"]!));
        Assert.Contains(lines, l => (string)JsonNode.Parse(l)!["name"]! == "Invoice");

        var output = Path.Combine(repo.Temp.Root, "out", "entities.ndjson");
        var toFile = await repo.RunAsync("model", "export", "--kind", "entity", "--format", "ndjson", "--out", output);
        Assert.True(toFile.ExitCode == 0, toFile.Error);
        Assert.Equal(8, Text.Lines(File.ReadAllText(output)).Length);

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
        Assert.Equal(8, records.Count);
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
    public async Task Materialize_previews_with_dry_run_applies_and_refuses_what_is_bound()
    {
        using var repo = CliRepo.Billing();
        var invoice = repo.PathOf(".maquettiste/model/entities/invoice.json");
        var before = File.ReadAllText(invoice);

        var dry = await repo.RunAsync("model", "materialize", "tables", "--database", "main", "Invoice", "--dry-run");
        Assert.True(dry.ExitCode == 0, dry.Error);
        Assert.StartsWith("materialize-tables would:", dry.Out, StringComparison.Ordinal);
        Assert.Contains("change table invoices", dry.Out, StringComparison.Ordinal);
        Assert.Contains("delete mapping Invoice in main", dry.Out, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(invoice));

        var applied = await repo.RunAsync("model", "materialize", "tables", "--database", "main", "Invoice", "--format", "json");
        Assert.True(applied.ExitCode == 0, applied.Error);
        Assert.Equal("saved", (string)JsonNode.Parse(applied.Out)!["result"]!["outcome"]!);
        Assert.Contains("\"bindings\"", File.ReadAllText(invoice), StringComparison.Ordinal);

        var again = await repo.RunAsync("model", "materialize", "tables", "--database", "main", "Invoice");
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("MQ4055", again.Error, StringComparison.Ordinal);

        var entities = await repo.RunAsync("model", "materialize", "entities", "--database", "main", "--package", "Billing", "notes", "--dry-run");
        Assert.Equal(1, entities.ExitCode);
        Assert.Contains("already bound by entity", entities.Error, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("model", "materialize", "tables", "--database", "main")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "materialize", "views", "--database", "main", "x")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "materialize", "entities", "--database", "main", "--schema", "x", "notes")).ExitCode);
    }

    [Fact]
    public async Task Stats_counts_the_kinds_and_per_package()
    {
        using var repo = CliRepo.Billing();

        var text = await repo.RunAsync("model", "stats");
        Assert.True(text.ExitCode == 0, text.Error);
        Assert.Contains(Text.Lines(text.Out), l => l.StartsWith("entity ", StringComparison.Ordinal) && l.EndsWith(" 8", StringComparison.Ordinal));
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

    [Fact]
    public async Task Delete_prints_the_plan_with_dry_run_and_deletes_with_dependents_in_one_change()
    {
        using var repo = CliRepo.Billing();
        var product = ".maquettiste/model/entities/product.json";
        var relation = ".maquettiste/model/relations/refers-to.json";

        var preview = await repo.RunAsync("model", "delete", "Product", "--resolution", "delete-dependents", "--dry-run");
        Assert.True(preview.ExitCode == 0, preview.Error);
        Assert.Contains("Deleting entity Product (delete-dependents) would:", preview.Out, StringComparison.Ordinal);
        Assert.Contains("delete relation refers to (needs entity Product)", preview.Out, StringComparison.Ordinal);
        Assert.Contains("remove member Product from diagram Billing overview", preview.Out, StringComparison.Ordinal);
        Assert.True(File.Exists(repo.PathOf(product)));

        var json = await repo.RunAsync("model", "delete", "Product", "--resolution", "remove-references", "--dry-run", "--format", "json");
        Assert.Equal(1, json.ExitCode);
        var plan = JsonNode.Parse(json.Out)!;
        Assert.Equal("invalid", (string)plan["outcome"]!);
        Assert.Equal("MQ2001", (string)plan["refused"]![0]!["rule"]!);

        var refused = await repo.RunAsync("model", "delete", "Product");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("--resolution remove-references or delete-dependents", refused.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(repo.PathOf(product)));

        var deleted = await repo.RunAsync("model", "delete", "01J92P0V0JR8BE8253SKT29ZG7", "--resolution", "delete-dependents");
        Assert.True(deleted.ExitCode == 0, deleted.Error);
        Assert.Contains("Deleted 2 elements and changed 1.", deleted.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.PathOf(product)));
        Assert.False(File.Exists(repo.PathOf(relation)));

        Assert.Equal(1, (await repo.RunAsync("model", "delete", "Nothing")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "delete", "Invoice", "--resolution", "cascade")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "delete")).ExitCode);
    }
}
