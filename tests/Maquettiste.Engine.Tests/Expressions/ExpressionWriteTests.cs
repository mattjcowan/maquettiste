using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Expressions.ExpressionRuleTests;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>
/// Expressions on the write path (next-phase-3-design.md section 3.1, task H6): text is stored as a tree on every write surface, bad
/// text refuses the write, <c>text-to-trees</c> converts files, and deletes treat a tree as a whole.
/// </summary>
public sealed class ExpressionWriteTests
{
    private const string ConstraintId = "01K9EXW0000000000000000K01";
    private const string AmountId = "01K9EXW0000000000000000K02";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Tree(TempRepo repo) =>
        JsonNode.Parse(repo.ReadFile(TablePath))!["checks"]![0]!["condition"]!.ToJsonString();

    private static string Expected => $$"""{"op":"gt","args":[{"path":["{{PriceColumnId}}"]},{"value":0}]}""";

    /// <summary>The products table's file with one check whose condition is <paramref name="condition"/>.</summary>
    private static string TableWith(TempRepo repo, string condition)
    {
        var node = JsonNode.Parse(repo.ReadFile(TablePath))!.AsObject();
        node["checks"] = new JsonArray(new JsonObject
        {
            ["id"] = "01K9EXW0000000000000000C01",
            ["name"] = "ck_products_price",
            ["condition"] = JsonNode.Parse(condition),
        });
        return node.ToJsonString();
    }

    [Fact]
    public async Task Text_is_stored_as_a_tree_on_save()
    {
        using var repo = Repo();
        var store = new ModelStore(repo.Options);
        var table = (await store.GetElementAsync(ProductsId, Ct))!;
        var saved = await store.SaveAsync(ProductsId, Encoding.UTF8.GetBytes(TableWith(repo, """{ "text": "price > 0" }""")), table.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(Expected, Tree(repo));
    }

    [Fact]
    public async Task Text_inside_a_tree_is_stored_as_a_tree_by_a_batch()
    {
        using var repo = Repo();
        var store = new ModelStore(repo.Options);
        var table = (await store.GetElementAsync(ProductsId, Ct))!;
        var body = TableWith(repo, """{ "op": "and", "args": [{ "text": "price > 0" }, { "text": "sku <> ''" }] }""");
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes($$"""{ "operations": [ { "op": "update", "id": "{{ProductsId}}", "expectedHash": "{{table.Hash}}", "element": {{body}} } ] }"""));
        Assert.NotNull(parsed.Batch);
        var applied = await store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal($$"""{"op":"and","args":[{{Expected}},{"op":"ne","args":[{"path":["{{SkuColumnId}}"]},{"value":""}]}]}""", Tree(repo));
    }

    [Fact]
    public async Task Text_is_stored_as_a_tree_on_create()
    {
        using var repo = Repo();
        var store = new ModelStore(repo.Options);
        var created = await store.CreateAsync(Encoding.UTF8.GetBytes($$"""
            { "kind": "constraint", "id": "{{ConstraintId}}", "name": "PositiveAmount",
              "parameters": [ { "id": "{{AmountId}}", "name": "amount", "type": "decimal" } ],
              "condition": { "text": "amount > 0" } }
            """), ChangeSource.Editor, Ct);
        Assert.True(created.Outcome == SaveOutcome.Saved, string.Join("; ", created.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var document = (await store.GetElementAsync(ConstraintId, Ct))!;
        Assert.Contains($$"""{"op":"gt","args":[{"path":["{{AmountId}}"]},{"value":0}]}""", JsonNode.Parse(document.Json.GetRawText())!["condition"]!.ToJsonString(),
            StringComparison.Ordinal);
    }

    /// <summary>A new table of the main database whose columns and check have no ids yet; its check's condition is the text.</summary>
    private static string NewTable(string name, string text) => $$"""
        { "kind": "table", "name": "{{name}}", "database": "01K6BFX0000000000000000002",
          "columns": [ { "name": "id", "type": "int64", "nullable": false }, { "name": "amount", "type": "decimal", "precision": 12, "scale": 2, "nullable": false } ],
          "checks": [ { "name": "ck_{{name}}_amount", "condition": { "text": "{{text}}" } } ] }
        """;

    /// <summary>Asserts that a new table's check holds <c>amount > 0</c> as a tree that names the amount column's minted id.</summary>
    private static void AssertMintedTree(string json)
    {
        var node = JsonNode.Parse(json)!;
        var amount = node["columns"]![1]!["id"]!.GetValue<string>();
        Assert.Equal($$"""{"op":"gt","args":[{"path":["{{amount}}"]},{"value":0}]}""", node["checks"]![0]!["condition"]!.ToJsonString());
    }

    [Fact]
    public async Task Text_is_stored_as_a_tree_on_a_create_whose_sub_elements_have_no_ids()
    {
        using var repo = Repo();
        var store = new ModelStore(repo.Options);
        var created = await store.CreateAsync(Encoding.UTF8.GetBytes(NewTable("invoices", "amount > 0")), ChangeSource.Editor, Ct);
        Assert.True(created.Outcome == SaveOutcome.Saved, string.Join("; ", created.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        AssertMintedTree((await store.GetElementAsync(created.Id!, Ct))!.Json.GetRawText());

        var refused = await store.CreateAsync(Encoding.UTF8.GetBytes(NewTable("refunds", "amount >")), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Contains(refused.Diagnostics, d => d.Rule == "MQ3101");
    }

    [Fact]
    public async Task Text_is_stored_as_a_tree_by_a_batch_create_whose_sub_elements_have_no_ids()
    {
        using var repo = Repo();
        var store = new ModelStore(repo.Options);
        async Task<BatchResult> ApplyAsync(string element)
        {
            var parsed = store.ParseBatch(Encoding.UTF8.GetBytes($$"""{ "operations": [ { "op": "create", "element": {{element}} } ] }"""));
            Assert.NotNull(parsed.Batch);
            return await store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        }

        var applied = await ApplyAsync(NewTable("invoices", "amount > 0"));
        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        AssertMintedTree(repo.ReadFile(".maquettiste/model/databases/main/tables/invoices.json"));

        var refused = await ApplyAsync(NewTable("refunds", "amount >"));
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Contains(refused.Items[0].Diagnostics, d => d.Rule == "MQ3101" && d.JsonPointer == "/operations/0/element/checks/0/condition");
    }

    [Fact]
    public async Task Text_is_stored_as_a_tree_on_a_save_that_adds_a_column_without_an_id()
    {
        using var repo = Repo();
        var store = new ModelStore(repo.Options);
        var table = (await store.GetElementAsync(ProductsId, Ct))!;
        var node = JsonNode.Parse(repo.ReadFile(TablePath))!.AsObject();
        node["columns"]!.AsArray().Add(new JsonObject { ["name"] = "discount", ["type"] = "decimal", ["precision"] = 12, ["scale"] = 2, ["nullable"] = true });
        node["checks"] = new JsonArray(new JsonObject { ["name"] = "ck_products_discount", ["condition"] = new JsonObject { ["text"] = "discount < price" } });
        var saved = await store.SaveAsync(ProductsId, Encoding.UTF8.GetBytes(node.ToJsonString()), table.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var stored = JsonNode.Parse(repo.ReadFile(TablePath))!;
        var discount = stored["columns"]![4]!["id"]!.GetValue<string>();
        Assert.Equal($$"""{"op":"lt","args":[{"path":["{{discount}}"]},{"path":["{{PriceColumnId}}"]}]}""", stored["checks"]![0]!["condition"]!.ToJsonString());
    }

    [Fact]
    public async Task Bad_text_refuses_the_save()
    {
        using var repo = Repo();
        var store = new ModelStore(repo.Options);
        var table = (await store.GetElementAsync(ProductsId, Ct))!;
        var saved = await store.SaveAsync(ProductsId, Encoding.UTF8.GetBytes(TableWith(repo, """{ "text": "price >" }""")), table.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, saved.Outcome);
        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ3101");
        Assert.DoesNotContain("checks", repo.ReadFile(TablePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_to_trees_converts_a_file_and_clears_MQ3108()
    {
        using var repo = Repo();
        TableCheck(repo, """{ "text": "price > 0" }""");
        var store = new ModelStore(repo.Options);
        Assert.Contains((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ3108");
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("""{ "operations": [ { "op": "text-to-trees" } ] }"""));
        Assert.NotNull(parsed.Batch);
        var applied = await store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(Expected, Tree(repo));
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ3108");
    }

    [Fact]
    public async Task A_delete_is_refused_and_delete_dependents_removes_the_check_that_names_it()
    {
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/constraints/positive-amount.json", $$"""
            {
              "kind": "constraint",
              "id": "{{ConstraintId}}",
              "name": "PositiveAmount",
              "parameters": [ { "id": "{{AmountId}}", "name": "amount", "type": "decimal" } ],
              "condition": { "op": "gt", "args": [{ "path": ["{{AmountId}}"] }, { "value": 0 }] }
            }
            """);
        TableCheck(repo, $$"""{ "satisfies": "{{ConstraintId}}", "with": [{ "parameter": "{{AmountId}}", "value": { "path": ["{{PriceColumnId}}"] } }] }""");
        var store = new ModelStore(repo.Options);
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));
        var constraint = (await store.GetElementAsync(ConstraintId, Ct))!;

        var refused = await store.DeleteAsync(ConstraintId, constraint.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        var cleared = await store.DeleteAsync(ConstraintId, constraint.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.NotEqual(SaveOutcome.Saved, cleared.Outcome);

        var deleted = await store.DeleteAsync(ConstraintId, constraint.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct);
        Assert.True(deleted.Outcome == SaveOutcome.Saved, deleted.Outcome + ": " + string.Join("; ", deleted.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Null(await store.GetElementAsync(ConstraintId, Ct));
        var checks = JsonNode.Parse(repo.ReadFile(TablePath))!["checks"];
        Assert.True(checks is null || checks.AsArray().Count == 0, repo.ReadFile(TablePath));
    }
}
