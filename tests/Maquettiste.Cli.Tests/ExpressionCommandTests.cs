using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// <c>maquettiste expression preview</c> (next-phase-3-design.md section 3.1, "CLI and MCP"): a stored expression or text at a site,
/// printed as its text, type, nullability, JavaScript and PostgreSQL text (or why there is none), and its answer with <c>--values</c>.
/// </summary>
public sealed class ExpressionCommandTests
{
    internal const string ProductId = "01K6BFX0000000000000000027";
    internal const string CheckId = "01K9EXP0000000000000000C01";
    private const string PriceId = "01K6BFX0000000000000000025";

    /// <summary>
    /// The bindings fixture, where the invented Product is bound to its table products, with the check pricePositive (<c>price &gt; 0</c>,
    /// pointing at price) that its binding lists, and an entity whose attribute names a type that does not exist, so the model does
    /// not validate.
    /// </summary>
    internal static CliRepo Bindings()
    {
        var repo = CliRepo.Empty();
        CliRepo.CopyTree(Fixtures.Path("models", "bindings", ".maquettiste"), repo.ModelRoot);
        const string product = ".maquettiste/model/entities/product.json";
        var entity = JsonNode.Parse(repo.Read(product))!.AsObject();
        entity["checks"] = JsonNode.Parse($$"""
            [ { "id": "{{CheckId}}", "name": "pricePositive", "attribute": "{{PriceId}}",
                "condition": { "op": "gt", "args": [{ "path": ["{{PriceId}}"] }, { "value": 0 }] } } ]
            """);
        entity["bindings"]![0]!["checks"] = new JsonArray(CheckId);
        repo.Write(product, entity.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        repo.Write(".maquettiste/model/entities/broken.json", """
            { "kind": "entity", "id": "01K9EXP0000000000000000B01", "name": "Broken",
              "attributes": [ { "id": "01K9EXP0000000000000000B02", "name": "x", "type": { "ref": "01K9EXP0000000000000000B99" } } ] }
            """);
        return repo;
    }

    [Fact]
    public async Task Preview_prints_a_stored_check_through_its_binding_with_its_answer()
    {
        using var repo = Bindings();

        var result = await repo.RunAsync("expression", "preview", "Product", "--at", "/checks/0", "--values", """{ "price": "12.50" }""");

        Assert.True(result.ExitCode == 0, result.ToString());
        Assert.Equal("""
            text: price > 0
            type: bool
            nullable: false
            javascript: (row) => mq.passes(mq.gt(mq.dec(mq.at(row, "price"), 2), mq.dec("0")))
            postgresql: price > 0
            answer: true

            """.Replace("\r\n", "\n", StringComparison.Ordinal), result.Out);
        Assert.Equal("", result.Error);

        // The check by its own id, as JSON; without --values there is no answer, and nulls are left out.
        var json = await repo.RunAsync("expression", "preview", CheckId, "--format", "json");
        Assert.True(json.ExitCode == 0, json.ToString());
        var preview = JsonNode.Parse(json.Out)!["preview"]!.AsObject();
        Assert.Equal(("price > 0", "price > 0"), ((string)preview["text"]!, (string)preview["sql"]!));
        Assert.False(preview.ContainsKey("answer"));
    }

    [Fact]
    public async Task Preview_of_text_at_a_process_guard_runs_as_JavaScript_only_and_fails_on_unknown()
    {
        // The guard exceedsCreditLimit is JavaScript today; text at its site takes its scope, the context and the payloads of the events
        // that use it, and its mode: only true holds.
        using var repo = CliRepo.Processes();
        string[] args = ["expression", "preview", "--text", "total > creditLimit", "--on", "SalesOrderLifecycle", "--site", "/guards/0/condition"];

        var holds = await repo.RunAsync([.. args, "--values", """{ "total": "1500.00", "creditLimit": "1000.00" }"""]);
        var unknown = await repo.RunAsync([.. args, "--values", """{ "total": "1500.00", "creditLimit": null }"""]);

        Assert.True(holds.ExitCode == 0, holds.ToString());
        var lines = holds.Out.Split('\n');
        Assert.Equal(["text: total > creditLimit", "type: bool", "nullable: true"], lines[..3]);
        Assert.StartsWith("javascript: (row) => mq.holds(mq.gt(", lines[3], StringComparison.Ordinal);
        Assert.Equal("postgresql: none: A process guard reads the process's context and the event, not a table: it runs as JavaScript only.", lines[4]);
        Assert.Equal("answer: true", lines[5]);
        Assert.Contains("answer: false\n", unknown.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_that_does_not_parse_exits_1_and_a_request_that_names_nothing_exits_4()
    {
        using var repo = Bindings();

        var bad = await repo.RunAsync("expression", "preview", "--text", "price >", "--on", ProductId, "--site", "check");
        Assert.Equal(1, bad.ExitCode);
        Assert.Equal("", bad.Out);
        Assert.Contains("MQ3101", bad.Error, StringComparison.Ordinal);

        foreach (var args in new[]
        {
            new[] { "expression", "preview", "--text", "price > 0" },
            ["expression", "preview", "Product", "--site", "check"],
            ["expression", "preview", "Nobody"],
            ["expression", "preview", "Product", "--at", "/attributes/0"],
            ["expression", "preview", "--text", "price > 0", "--on", "Product", "--site", "guard"],
            ["expression", "preview", "Product", "--values", "[1]"],
            ["expression", "frob"],
        })
        {
            var refused = await repo.RunAsync(args);
            Assert.True(refused.ExitCode == 4, string.Join(' ', args) + "\n" + refused);
        }
    }
}
