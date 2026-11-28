using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Expressions.ExpressionResolutionTests;

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

    internal const string UnitAttributeId = "01K9EXR0000000000000000R06";
    internal const string UnitColumnId = "01K9EXR0000000000000000R07";
    internal const string KgId = "01K9EXR0000000000000000R05";

    /// <summary>
    /// The reference type Unit, whose codes are text (one seed row, <c>kg</c>), and a unit column of products typed int32 that Product's
    /// binding maps to its unit attribute of Unit, so the column's bound type is Unit.
    /// </summary>
    internal static void IntegerUnitColumn(TempRepo repo)
    {
        repo.WriteFile(".maquettiste/model/reference-types/unit.json", """
            {
              "kind": "reference-type",
              "id": "01K9EXR0000000000000000R01",
              "name": "Unit",
              "package": "01K6BFX0000000000000000001",
              "code": { "id": "01K9EXR0000000000000000R02", "length": 8 },
              "label": { "id": "01K9EXR0000000000000000R03", "length": 64 }
            }
            """);
        repo.WriteFile(".maquettiste/model/seeds/unit/unit.json", $$"""
            {
              "kind": "seed",
              "id": "01K9EXR0000000000000000R04",
              "name": "Unit",
              "target": "01K9EXR0000000000000000R01",
              "columns": ["code", "label"],
              "rows": [ { "id": "{{KgId}}", "values": ["kg", "Kilogram"] } ]
            }
            """);
        BoundColumn(repo, new JsonObject { ["id"] = UnitColumnId, ["name"] = "unit", ["type"] = "int32" }, UnitAttributeId, "01K9EXR0000000000000000R01");
    }

    /// <summary>
    /// Adds a column to products and an attribute of the same name, typed by an enum, a reference type or a scalar type, to Product,
    /// whose binding maps it to the column: the binding way to give a column a type (0.28.0).
    /// </summary>
    internal static void BoundColumn(TempRepo repo, JsonObject column, string attributeId, string typeId)
    {
        var columnId = (string)column["id"]!;
        var name = (string)column["name"]!;
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(column));
        Edit(repo, EntityPath, e =>
        {
            e["attributes"]!.AsArray().Add(new JsonObject { ["id"] = attributeId, ["name"] = name, ["type"] = new JsonObject { ["ref"] = typeId } });
            ((JsonObject)e["bindings"]![0]!)["fields"]!.AsArray().Add(new JsonObject { ["attribute"] = attributeId, ["column"] = columnId });
        });
    }

    /// <summary>The entity check kgOnly (<c>unit = Unit.kg</c>), listed by Product's binding.</summary>
    internal static void KgOnly(TempRepo repo) => Edit(repo, EntityPath, e =>
    {
        e["checks"] = JsonNode.Parse($$"""
            [ { "id": "01K9EXR0000000000000000E02", "name": "kgOnly", "attribute": "{{UnitAttributeId}}",
                "condition": { "op": "eq", "args": [{ "path": ["{{UnitAttributeId}}"] }, { "ref": "{{KgId}}" }] } } ]
            """);
        ((JsonObject)e["bindings"]![0]!)["checks"] = new JsonArray("01K9EXR0000000000000000E02");
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
    public async Task MQ3103_a_code_against_a_column_of_another_physical_type()
    {
        // A table's check reads its columns' physical types only (0.28.0): unit is an int32 column, whatever the binding maps to it, so
        // comparing it with the code kg does not type.
        using var repo = Repo();
        IntegerUnitColumn(repo);
        TableCheck(repo, $$"""{ "op": "eq", "args": [{ "path": ["{{UnitColumnId}}"] }, { "ref": "{{KgId}}" }] }""");
        await AssertRuleAsync(repo, "MQ3103", "/checks/0/condition");
        Assert.Contains(await ValidateAsync(repo), d => d.Rule == "MQ3103" && d.Message.Contains("Cannot compare int32 with code", StringComparison.Ordinal));
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
    public async Task MQ3108_alone_for_text_that_names_a_constraint()
    {
        // Text can name any enum, reference type or constraint, so its site looks up each name the text writes, as text-to-trees does.
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/constraints/positive-amount.json", """
            {
              "kind": "constraint",
              "id": "01K9EXR0000000000000000K01",
              "name": "PositiveAmount",
              "parameters": [ { "id": "01K9EXR0000000000000000K02", "name": "amount", "type": "decimal" } ],
              "condition": { "op": "gt", "args": [{ "path": ["01K9EXR0000000000000000K02"] }, { "value": 0 }] }
            }
            """);
        TableCheck(repo, """{ "text": "price > 0 and satisfies PositiveAmount(amount: price)" }""");
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
    public async Task MQ3110_a_binding_check_whose_code_the_mapped_column_cannot_store()
    {
        // The binding maps unit, typed by Unit (text codes), to an int32 column: the check's kg cannot be written there.
        using var repo = Repo();
        IntegerUnitColumn(repo);
        KgOnly(repo);
        await AssertRuleAsync(repo, "MQ3110", "/bindings/0/checks/0");
        Assert.Contains(await ValidateAsync(repo), d => d.Rule == "MQ3110" && d.Message.Contains("the code's type does not match the column", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MQ3110_a_binding_check_on_a_value_object_member_the_binding_does_not_map()
    {
        // The binding maps total.amount, not total.currency, so the check on total.currency cannot reach the table.
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/types/money.json", """
            {
              "kind": "value-object",
              "id": "01K9EXR0000000000000000V01",
              "name": "Money",
              "package": "01K6BFX0000000000000000001",
              "attributes": [
                { "id": "01K9EXR0000000000000000V02", "name": "amount", "type": "decimal", "precision": 18, "scale": 2, "required": true },
                { "id": "01K9EXR0000000000000000V03", "name": "currency", "type": "string", "length": 3, "required": true }
              ]
            }
            """);
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "01K9EXR0000000000000000V05", "name": "total_amount", "type": "decimal", "precision": 18, "scale": 2 }
            """)));
        Edit(repo, EntityPath, e =>
        {
            e["attributes"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01K9EXR0000000000000000V04", "name": "total", "type": { "ref": "01K9EXR0000000000000000V01" } }"""));
            var binding = (JsonObject)e["bindings"]![0]!;
            binding["fields"]!.AsArray().Add(JsonNode.Parse("""{ "attribute": "01K9EXR0000000000000000V04.01K9EXR0000000000000000V02", "column": "01K9EXR0000000000000000V05" }"""));
            e["checks"] = JsonNode.Parse("""
                [ { "id": "01K9EXR0000000000000000E03", "name": "usdOnly", "attribute": "01K9EXR0000000000000000V04.01K9EXR0000000000000000V03",
                    "condition": { "op": "eq", "args": [{ "path": ["01K9EXR0000000000000000V04.01K9EXR0000000000000000V03"] }, { "value": "USD" }] } } ]
                """);
            binding["checks"] = new JsonArray("01K9EXR0000000000000000E03");
        });
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
    public async Task MQ3114_and_MQ3115_read_the_paths_of_a_check_held_as_text()
    {
        // A check held as text names no ids until it is parsed, so the rules read its typed form: an entity check whose text reads its
        // attribute is not MQ3114, and one whose text reads an attribute the binding does not map is no MQ3115, since no binding
        // could enforce it.
        using var repo = Repo();
        Edit(repo, EntityPath, e =>
        {
            e["attributes"]!.AsArray().Add(new JsonObject { ["id"] = "01K9EXR0000000000000000E09", ["name"] = "notes", ["type"] = "string" });
            e["checks"] = JsonNode.Parse($$"""
                [ { "id": "01K9EXR0000000000000000E01", "name": "pricePositive", "attribute": "{{PriceId}}", "condition": { "text": "price > 0" } },
                  { "id": "01K9EXR0000000000000000E02", "name": "notesGiven", "attribute": "01K9EXR0000000000000000E09", "condition": { "text": "notes <> ''" } } ]
                """);
            ((JsonObject)e["bindings"]![0]!)["checks"] = new JsonArray("01K9EXR0000000000000000E01");
        });
        var diagnostics = await ValidateAsync(repo);
        Assert.Equal(["MQ3108", "MQ3108"], diagnostics.Where(d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal)).Select(d => d.Rule));

        Edit(repo, EntityPath, e => e["checks"]![0]!["attribute"] = SkuAttributeId);
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
