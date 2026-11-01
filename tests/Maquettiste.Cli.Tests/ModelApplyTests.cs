using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Cli.Tests;

/// <summary><c>maquettiste model apply</c>: a batch file applied all or nothing, by name, with ids minted (next-phase-1-design.md 5.1).</summary>
public sealed class ModelApplyTests
{
    private const string Main = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string BillingSchema = "01J92P0V1RC04SKQ5353EAKHG2";

    /// <summary>The acceptance batch's operations: a table, a reference type bound to it and its seed, by name and placeholder only.</summary>
    internal const string PaymentMethods = """
        [
          { "op": "create", "as": "$pm", "element": { "kind": "table", "name": "payment_method", "database": "@database:main", "schema": "@schema:billing",
              "columns": [ { "name": "code", "type": "string", "length": 16, "nullable": false }, { "name": "label", "type": "string", "length": 64, "nullable": false } ],
              "primaryKey": { "columns": [ "@column:billing.payment_method.code" ] } } },
          { "op": "create", "as": "$type", "element": { "kind": "reference-type", "name": "PaymentMethod", "code": { "length": 16 }, "label": { "length": 64 },
              "bindings": [ { "database": "@database:main", "table": "$pm", "fields": [ { "attribute": "code", "column": "code" }, { "attribute": "label", "column": "label" } ] } ] } },
          { "op": "create", "element": { "kind": "seed", "name": "PaymentMethods", "target": "$type", "columns": [ "code", "label" ],
              "rows": [ { "values": [ "card", "Card" ] }, { "values": [ "transfer", "Bank transfer" ] } ] } }
        ]
        """;

    /// <summary>The billing invoice's id, which the typed property of <see cref="TypeTheCustomersOwner"/> names.</summary>
    internal const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";

    /// <summary>
    /// Writes an extension file that types an entity's <c>owner</c> as a link to an entity, and gives Customer the owner
    /// <c>Invoice</c> by name (MQ2010 until <c>names-to-ids</c> converts it), on disk before anything loads the model.
    /// </summary>
    internal static void TypeTheCustomersOwner(CliRepo repo)
    {
        const string Links = ".maquettiste/extensions/links.json";
        const string Customer = ".maquettiste/model/entities/customer.json";
        repo.Write(Links, Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse("""
            { "name": "links", "appliesTo": { "kinds": ["entity"] },
              "properties": { "owner": { "type": "string", "x-ref": { "kind": "entity" } } } }
            """)!, "extension.json", Links)));
        var customer = JsonNode.Parse(repo.Read(Customer))!;
        customer["properties"] = new JsonObject { ["owner"] = "Invoice" };
        repo.Write(Customer, Encoding.UTF8.GetString(TestServices.Json.Write(customer, "entity.json", Customer)));
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => Strings(p.Value)),
        JsonArray a => a.SelectMany(Strings),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };

    [Fact]
    public async Task Model_apply_writes_a_batch_by_name_and_prints_the_ids()
    {
        using var repo = CliRepo.Billing();
        var batch = Path.Combine(repo.Temp.Root, "payment-methods.json");
        File.WriteAllText(batch, "{ \"operations\": " + PaymentMethods + " }");

        var result = await repo.RunAsync("model", "apply", batch);

        Assert.True(result.ExitCode == 0, result.ToString());
        var lines = Text.Lines(result.Out);
        var written = lines.Where(l => l.StartsWith("written ", StringComparison.Ordinal)).Select(l => l["written ".Length..]).ToList();
        Assert.Equal(3, written.Count);
        foreach (var path in written)
        {
            var document = JsonNode.Parse(repo.Read(path));
            Assert.DoesNotContain(Strings(document), v => v.StartsWith('@') || v.StartsWith('$'));
        }

        var names = lines.Where(l => l.Contains("  ", StringComparison.Ordinal)).Select(l => l.Split("  ")).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        Assert.Equal(["$pm", "$type", "@column:billing.payment_method.code", "@database:main", "@schema:billing"], names.Keys);
        Assert.Equal(Main, names["@database:main"]);
        Assert.Equal(BillingSchema, names["@schema:billing"]);
        var table = JsonNode.Parse(repo.Read(Assert.Single(written, p => p.Contains("/tables/", StringComparison.Ordinal))))!;
        Assert.Equal(names["$pm"], (string)table["id"]!);
        Assert.Equal(names["@column:billing.payment_method.code"], (string)table["primaryKey"]!["columns"]![0]!);
        Assert.Contains("10 ids minted", result.Error, StringComparison.Ordinal);

        // JSON prints the apply_batch answer: the batch result with its ids and facts.
        var renamed = Path.Combine(repo.Temp.Root, "label.json");
        File.WriteAllText(renamed, """
            [ { "op": "create", "element": { "kind": "table", "name": "payment_note", "database": "@database:main", "schema": "@schema:billing",
                "columns": [ { "name": "method", "type": "string", "length": 16, "nullable": false } ] } } ]
            """);
        var json = await repo.RunAsync("model", "apply", renamed, "--format", "json");
        Assert.True(json.ExitCode == 0, json.ToString());
        var answer = JsonNode.Parse(json.Out)!;
        Assert.Equal("saved", (string)answer["outcome"]!);
        Assert.Equal(2, answer["ids"]!["minted"]!.AsArray().Count);
        Assert.Equal("@table:main.billing.payment_note", (string)answer["ids"]!["minted"]![0]!["name"]!);
        Assert.NotNull(answer["facts"]);
    }

    [Fact]
    public async Task Model_apply_dry_run_prints_diffs_and_writes_nothing()
    {
        using var repo = CliRepo.Billing();
        var batch = Path.Combine(repo.Temp.Root, "payment-methods.json");
        File.WriteAllText(batch, PaymentMethods); // the bare array
        var before = repo.Tree(".maquettiste");

        var text = await repo.RunAsync("model", "apply", batch, "--dry-run");

        Assert.True(text.ExitCode == 0, text.ToString());
        Assert.Equal(3, Text.Lines(text.Out).Count(l => l.StartsWith("--- a/.maquettiste/model/", StringComparison.Ordinal)));
        Assert.Contains("+++ b/.maquettiste/model/", text.Out, StringComparison.Ordinal);
        Assert.Contains("+  \"name\": \"payment_method\",", text.Out, StringComparison.Ordinal);
        Assert.Contains("\n$pm  ", "\n" + text.Out, StringComparison.Ordinal);
        Assert.Contains("\n@database:main  " + Main + "\n", text.Out, StringComparison.Ordinal);
        Assert.Contains("nothing was written", text.Error, StringComparison.Ordinal);

        var json = await repo.RunAsync("model", "apply", batch, "--dry-run", "--format", "json");

        Assert.True(json.ExitCode == 0, json.ToString());
        var answer = JsonNode.Parse(json.Out)!;
        Assert.Equal("saved", (string)answer["outcome"]!);
        var files = answer["files"]!.AsArray();
        Assert.Equal(3, files.Count);
        Assert.All(files, f =>
        {
            Assert.Equal(["action", "beforeHash", "diff", "id", "kind", "name", "path"], f!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.Equal("created", (string)f["action"]!);
            Assert.StartsWith("--- a/" + (string)f["path"]!, (string)f["diff"]!, StringComparison.Ordinal);
        });
        Assert.Equal(10, answer["ids"]!["minted"]!.AsArray().Count);
        Assert.NotNull(answer["diagnosticsAdded"]);
        Assert.NotNull(answer["diagnosticsRemoved"]);
        Assert.True(answer.AsObject().ContainsKey("facts"));
        Assert.Equal(before, repo.Tree(".maquettiste"));
    }

    [Fact]
    public async Task Names_to_ids_dry_run_writes_nothing()
    {
        using var repo = CliRepo.Billing();
        TypeTheCustomersOwner(repo);
        var batch = Path.Combine(repo.Temp.Root, "names-to-ids.json");
        File.WriteAllText(batch, """[ { "op": "names-to-ids", "elementIds": [ "@entity:Customer" ] } ]""");
        var before = repo.Tree(".maquettiste");

        var text = await repo.RunAsync("model", "apply", batch, "--dry-run");

        Assert.True(text.ExitCode == 0, text.ToString());
        Assert.Contains("--- a/.maquettiste/model/entities/customer.json", text.Out, StringComparison.Ordinal);
        Assert.Contains("-    \"owner\": \"Invoice\"", text.Out, StringComparison.Ordinal);
        Assert.Contains("+    \"owner\": \"" + Invoice + "\"", text.Out, StringComparison.Ordinal);
        Assert.Contains("\n@entity:Customer  01J92P0V0ETQKXXP951CMMNHH3\n", "\n" + text.Out, StringComparison.Ordinal);
        Assert.Contains("nothing was written", text.Error, StringComparison.Ordinal);
        Assert.Equal(before, repo.Tree(".maquettiste"));

        // A name that finds nothing fails the dry run with MQ2010 at the value.
        repo.Replace(".maquettiste/model/entities/customer.json", "\"Invoice\"", "\"Nobody\"");
        var unknown = await repo.RunAsync("model", "apply", batch, "--dry-run", "--format", "json");
        Assert.True(unknown.ExitCode == 1, unknown.ToString());
        Assert.Contains("MQ2010", JsonNode.Parse(unknown.Out)!["items"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_apply_exits_3_on_a_conflict_and_1_when_invalid()
    {
        using var repo = CliRepo.Billing();
        var customer = JsonNode.Parse(repo.Read(".maquettiste/model/entities/customer.json"))!;
        customer["description"] = "Changed by a batch.";
        var conflict = Path.Combine(repo.Temp.Root, "conflict.json");
        File.WriteAllText(conflict, new JsonArray(new JsonObject
        {
            ["op"] = "update",
            ["id"] = "@entity:Customer",
            ["expectedHash"] = new string('0', 64),
            ["element"] = customer.DeepClone(),
        }).ToJsonString());
        var before = repo.Tree(".maquettiste");

        var stale = await repo.RunAsync("model", "apply", conflict);
        Assert.True(stale.ExitCode == 3, stale.ToString());
        Assert.Contains("nothing was written", stale.Error, StringComparison.Ordinal);

        var unknown = Path.Combine(repo.Temp.Root, "unknown.json");
        File.WriteAllText(unknown, """
            [ { "op": "create", "element": { "kind": "table", "name": "payment_note", "database": "@database:nowhere",
                "columns": [ { "name": "method", "type": "string", "length": 16 } ] } } ]
            """);
        var invalid = await repo.RunAsync("model", "apply", unknown);
        Assert.True(invalid.ExitCode == 1, invalid.ToString());
        Assert.Contains("MQ1012", invalid.Error, StringComparison.Ordinal);
        var dryInvalid = await repo.RunAsync("model", "apply", unknown, "--dry-run", "--format", "json");
        Assert.True(dryInvalid.ExitCode == 1, dryInvalid.ToString());
        var answer = JsonNode.Parse(dryInvalid.Out)!;
        Assert.Equal("invalid", (string)answer["outcome"]!);
        Assert.Empty(answer["files"]!.AsArray());
        Assert.Empty(answer["diagnosticsAdded"]!.AsArray());
        Assert.Contains("MQ1012", answer["items"]!.ToJsonString(), StringComparison.Ordinal);

        var malformed = Path.Combine(repo.Temp.Root, "malformed.json");
        File.WriteAllText(malformed, """[ { "op": "rename" } ]""");
        var parse = await repo.RunAsync("model", "apply", malformed);
        Assert.True(parse.ExitCode == 1, parse.ToString());
        Assert.Contains("is not a valid batch", parse.Error, StringComparison.Ordinal);
        Assert.Equal(1, (await repo.RunAsync("model", "apply", Path.Combine(repo.Temp.Root, "missing.json"))).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "apply")).ExitCode);
        Assert.Equal(before, repo.Tree(".maquettiste"));
    }
}
