using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>
/// The expression rules (next-phase-3-design.md section 3.1, "Rules", task H6), one test each, over the bindings fixture: the
/// invented entity Product, bound to its table products, whose columns and attributes are id, sku, name and price.
/// </summary>
public sealed class ExpressionRuleTests
{
    internal const string ProductId = "01K6BFX0000000000000000027";
    internal const string BindingId = "01K6BFX0000000000000000026";
    internal const string PriceId = "01K6BFX0000000000000000025";
    internal const string SkuAttributeId = "01K6BFX0000000000000000023";
    internal const string ProductsId = "01K6BFX0000000000000000007";
    internal const string SkuColumnId = "01K6BFX0000000000000000004";
    internal const string PriceColumnId = "01K6BFX0000000000000000006";
    internal const string EntityPath = ".maquettiste/model/entities/product.json";
    internal const string TablePath = ".maquettiste/model/databases/main/tables/products.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A copy of the bindings fixture.</summary>
    internal static TempRepo Repo()
    {
        var repo = new TempRepo();
        var from = Fixtures.Path("models", "bindings");
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            repo.WriteBytes(Path.GetRelativePath(from, file).Replace('\\', '/'), File.ReadAllBytes(file));
        return repo;
    }

    /// <summary>Edits one JSON file of the repo.</summary>
    internal static void Edit(TempRepo repo, string path, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(repo.ReadFile(path))!.AsObject();
        edit(node);
        repo.WriteFile(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Adds a table check holding a condition.</summary>
    internal static void TableCheck(TempRepo repo, string condition, string? column = null, string? expression = null) => Edit(repo, TablePath, t =>
    {
        var check = new JsonObject { ["id"] = "01K9EXR0000000000000000C01", ["name"] = "ck_products_rule" };
        if (column is not null)
            check["column"] = column;
        if (expression is not null)
            check["expression"] = new JsonObject { ["*"] = expression };
        check["condition"] = JsonNode.Parse(condition);
        t["checks"] = new JsonArray(check);
    });

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(TempRepo repo)
    {
        var store = new ModelStore(repo.Options);
        return (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
    }

    private static async Task AssertRuleAsync(TempRepo repo, string rule, string? pointer = null)
    {
        var diagnostics = await ValidateAsync(repo);
        Assert.True(diagnostics.Any(d => d.Rule == rule && (pointer is null || d.JsonPointer == pointer)),
            $"No {rule}{(pointer is null ? "" : " at " + pointer)}:\n" + string.Join("\n", diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
    }

    private static string Price => $$"""{ "path": ["{{PriceColumnId}}"] }""";

    private static string Sku => $$"""{ "path": ["{{SkuColumnId}}"] }""";

    [Fact]
    public async Task MQ3101_a_wrong_number_of_operands()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "op": "not", "args": [{{Price}}, { "value": 0 }] }""");
        await AssertRuleAsync(repo, "MQ3101");
    }

    [Fact]
    public async Task MQ3102_a_path_the_site_cannot_read()
    {
        using var repo = Repo();
        TableCheck(repo, """{ "op": "gt", "args": [{ "path": ["01K9EXR0000000000000000X99"] }, { "value": 0 }] }""");
        await AssertRuleAsync(repo, "MQ3102");
    }

    [Fact]
    public async Task MQ3103_operands_of_different_types()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "op": "gt", "args": [{{Sku}}, { "value": 0 }] }""");
        await AssertRuleAsync(repo, "MQ3103");
    }

    [Fact]
    public async Task MQ3103_a_column_without_a_type()
    {
        // An overlay column whose attribute resolves to none states no type; read as a string, 'x' would lower to SQL PostgreSQL refuses.
        using var repo = Repo();
        const string Overlay = "01K9EXR0000000000000000C02";
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(new JsonObject { ["id"] = Overlay, ["attribute"] = "01K9EXR0000000000000000X96" }));
        TableCheck(repo, $$"""{ "op": "eq", "args": [{ "path": ["{{Overlay}}"] }, { "value": "x" }] }""");
        await AssertRuleAsync(repo, "MQ3103", "/checks/0/condition/args/0");
    }

    [Fact]
    public async Task MQ3104_now_at_a_check()
    {
        using var repo = Repo();
        TableCheck(repo, """{ "op": "isNotNull", "args": [{ "now": true }] }""");
        await AssertRuleAsync(repo, "MQ3104");
    }

    [Fact]
    public async Task MQ3105_a_pattern_outside_the_portable_subset()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "call": "matches", "args": [{{Sku}}, { "value": "\\d+" }] }""");
        await AssertRuleAsync(repo, "MQ3105");
    }

    [Fact]
    public async Task MQ3106_a_pattern_that_is_not_a_literal()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "call": "matches", "args": [{{Sku}}, {{Sku}}] }""");
        await AssertRuleAsync(repo, "MQ3106");
    }

    [Fact]
    public async Task MQ3107_a_check_with_both_forms()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "op": "gt", "args": [{{Price}}, { "value": 0 }] }""", expression: "price > 0");
        await AssertRuleAsync(repo, "MQ3107", "/checks/0/condition");
    }

    [Fact]
    public async Task MQ3107_is_silent_for_a_stub_guard()
    {
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/processes/product-review.json", """
            {
              "kind": "process",
              "id": "01K9EXR0000000000000000P01",
              "name": "ProductReview",
              "guards": [ { "id": "01K9EXR0000000000000000P02", "name": "approved" } ],
              "states": [ { "id": "01K9EXR0000000000000000P03", "name": "open" } ]
            }
            """);
        var diagnostics = await ValidateAsync(repo);
        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ3107");
    }

    [Fact]
    public async Task MQ3108_a_file_that_holds_text()
    {
        using var repo = Repo();
        TableCheck(repo, """{ "text": "price > 0" }""");
        await AssertRuleAsync(repo, "MQ3108");
    }

    [Fact]
    public async Task MQ3108_alone_for_text_that_names_an_enum_member_and_a_constraint()
    {
        // Text can name any enum, reference type or constraint, so its site reads them all, as text-to-trees does.
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/enums/product-status.json", """
            {
              "kind": "enum",
              "id": "01K9EXR0000000000000000N01",
              "name": "ProductStatus",
              "members": [ { "id": "01K9EXR0000000000000000N02", "name": "Active" }, { "id": "01K9EXR0000000000000000N03", "name": "Retired" } ]
            }
            """);
        repo.WriteFile(".maquettiste/model/constraints/positive-amount.json", """
            {
              "kind": "constraint",
              "id": "01K9EXR0000000000000000K01",
              "name": "PositiveAmount",
              "parameters": [ { "id": "01K9EXR0000000000000000K02", "name": "amount", "type": "decimal" } ],
              "condition": { "op": "gt", "args": [{ "path": ["01K9EXR0000000000000000K02"] }, { "value": 0 }] }
            }
            """);
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K9EXR0000000000000000C03",
            ["name"] = "status",
            ["type"] = "string",
            ["typeRef"] = new JsonObject { ["ref"] = "01K9EXR0000000000000000N01" },
        }));
        TableCheck(repo, """{ "text": "status = ProductStatus.Active and satisfies PositiveAmount(amount: price)" }""");
        var diagnostics = await ValidateAsync(repo);
        Assert.Equal(["MQ3108"], diagnostics.Where(d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal)).Select(d => d.Rule));
    }

    [Fact]
    public async Task MQ3109_a_satisfies_that_names_no_constraint()
    {
        using var repo = Repo();
        TableCheck(repo, """{ "satisfies": "01K9EXR0000000000000000X98", "with": [] }""");
        await AssertRuleAsync(repo, "MQ3109");
    }

    [Fact]
    public async Task MQ3110_a_binding_that_lists_a_check_of_no_one()
    {
        using var repo = Repo();
        Edit(repo, EntityPath, e => ((JsonObject)e["bindings"]![0]!)["checks"] = new JsonArray("01K9EXR0000000000000000X97"));
        await AssertRuleAsync(repo, "MQ3110", "/bindings/0/checks/0");
    }

    [Fact]
    public async Task MQ3111_a_guard_error_that_is_not_the_operations()
    {
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/operations/reprice.json", $$"""
            {
              "kind": "operation",
              "id": "01K9EXR0000000000000000Q01",
              "name": "reprice",
              "subject": "{{ProductId}}",
              "effect": "change",
              "outcomes": [ { "id": "01K9EXR0000000000000000Q02", "name": "applied" } ],
              "errors": [ { "id": "01K9EXR0000000000000000Q03", "name": "tooLow" } ],
              "guards": [
                {
                  "id": "01K9EXR0000000000000000Q04",
                  "name": "positive",
                  "condition": { "value": true },
                  "error": "01K9EXR0000000000000000Q02"
                }
              ]
            }
            """);
        await AssertRuleAsync(repo, "MQ3111", "/guards/0/error");
    }

    [Fact]
    public async Task MQ3113_a_tree_of_too_many_nodes()
    {
        using var repo = Repo();
        var one = $$"""{ "op": "gt", "args": [{{Price}}, { "value": 0 }] }""";
        TableCheck(repo, """{ "op": "and", "args": [""" + string.Join(", ", Enumerable.Repeat(one, 200)) + "] }");
        await AssertRuleAsync(repo, "MQ3113");
    }

    [Fact]
    public async Task MQ3114_a_column_check_that_does_not_read_its_column()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "op": "gt", "args": [{{Price}}, { "value": 0 }] }""", column: SkuColumnId);
        await AssertRuleAsync(repo, "MQ3114", "/checks/0/condition");
    }

    [Fact]
    public async Task MQ3115_an_entity_check_no_binding_lists()
    {
        using var repo = Repo();
        Edit(repo, EntityPath, e => e["checks"] = JsonNode.Parse($$"""
            [ { "id": "01K9EXR0000000000000000E01", "name": "pricePositive", "attribute": "{{PriceId}}",
                "condition": { "op": "gt", "args": [{ "path": ["{{PriceId}}"] }, { "value": 0 }] } } ]
            """));
        await AssertRuleAsync(repo, "MQ3115", "/checks/0");
        Edit(repo, EntityPath, e => ((JsonObject)e["bindings"]![0]!)["checks"] = new JsonArray("01K9EXR0000000000000000E01"));
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule is "MQ3115" or "MQ3110");
    }

    [Fact]
    public async Task A_model_without_expressions_reports_none_of_them()
    {
        using var repo = Repo();
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));
    }
}
