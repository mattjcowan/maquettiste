using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary><c>maquettiste pack exports</c> (next-phase-2-design.md section 5.3).</summary>
public sealed class PackExportsCommandTests
{
    [Fact]
    public async Task Pack_exports_prints_one_block_per_element()
    {
        using var repo = CliRepo.Billing();
        repo.Temp.WriteFile(".maquettiste/templates/records/pack.json", """
            { "name": "records", "version": "1.0.0", "engine": ">=1.0",
              "units": [ { "id": "record", "template": "record.scriban", "for": "each entity", "output": "src/{{ exports.class_name }}.cs",
                "exports": { "class_name": "{{ pascal entity.name }}Record", "members": { "each": "attributes", "value": "{{ pascal item.name }}" } } } ] }
            """);
        repo.Temp.WriteFile(".maquettiste/templates/records/record.scriban", "class {{ exports.class_name }}");
        var first = await repo.RunAsync("pack", "exports", "records", "--format", "json");
        Assert.True(first.ExitCode == 0, first.Error + first.Out);
        var all = JsonNode.Parse(first.Out)!;
        Assert.Null(all["elements"]![0]!["values"]);

        var file = Directory.EnumerateFiles(Path.Combine(repo.RepoRoot, ".maquettiste"), "*.json", SearchOption.AllDirectories)
            .Select(File.ReadAllText).First(t => t.Contains("\"kind\": \"entity\"", StringComparison.Ordinal) && t.Contains("\"name\": \"Invoice\"", StringComparison.Ordinal));
        var invoice = System.Text.RegularExpressions.Regex.Match(file, "\"id\": \"([0-9A-Z]{26})\"").Groups[1].Value;
        Assert.NotEmpty(invoice);

        var text = await repo.RunAsync("pack", "exports", "records", "--element", invoice);

        Assert.Equal(0, text.ExitCode);
        var lines = text.Out.Split('\n');
        Assert.Equal("Invoice (entity)", lines[0]);
        Assert.Equal("  class_name = InvoiceRecord", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("  members/", StringComparison.Ordinal) && l.EndsWith(" = Number", StringComparison.Ordinal));
        Assert.Equal(1, (await repo.RunAsync("pack", "exports", "nope")).ExitCode);
    }
}
