using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Expressions.ExpressionResolutionTests;
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
    public async Task Text_on_an_overlay_check_names_the_entitys_attributes_and_previews_by_their_names()
    {
        // On Invoice's overlay, text names Invoice's attributes as an entity check does, so a save stores their column keys; the preview
        // prints that check, and one stored through number's override entry, which has no name, by the attributes' names.
        await using var r = EditorRepo.Create(packs: false);
        OverlayChecks(r);
        var overlay = (await r.Store.GetElementAsync(EditorRepo.OverlayTableId, Ct))!;
        var node = JsonNode.Parse(overlay.Json.GetRawText())!.AsObject();
        node["checks"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "01K9EXW0000000000000000W01", "name": "ck_invoices_due_after_issue", "condition": { "text": "issuedOn <= dueOn and total.amount >= 0" } }
            """));
        node["checks"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "01K9EXW0000000000000000W02", "name": "ck_invoices_number",
              "condition": { "op": "ge", "args": [{ "call": "length", "args": [{ "path": ["{{NumberEntryId}}"] }] }, { "value": 3 }] } }
            """));
        var saved = await r.Store.SaveAsync(EditorRepo.OverlayTableId, Encoding.UTF8.GetBytes(node.ToJsonString()), overlay.Hash, ChangeSource.Editor, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var stored = JsonNode.Parse(File.ReadAllText(r.Repo.PathOf(OverlayPath)))!["checks"]![1]!["condition"]!.ToJsonString();
        Assert.Equal($$"""
            {"op":"and","args":[{"op":"le","args":[{"path":["{{IssuedOnId}}"]},{"path":["{{DueOnId}}"]}]},{"op":"ge","args":[{"path":["{{TotalAmount}}"]},{"value":0}]}]}
            """, stored);

        var due = (await r.Store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = "01K9EXW0000000000000000W01" }, Ct)).Preview!;
        Assert.Equal(("issuedOn <= dueOn and total.amount >= 0", "bool", null), (due.Text, due.Type, due.Sql));
        Assert.Contains("synthesized", due.SqlNote, StringComparison.Ordinal);
        using var values = JsonDocument.Parse("""{ "number": "INV" }""");
        var number = (await r.Store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = "01K9EXW0000000000000000W02", Values = values.RootElement }, Ct)).Preview!;
        Assert.Equal(("length(number) >= 3", true), (number.Text, number.Answer));
        var text = (await r.Store.PreviewExpressionAsync(new ExpressionPreviewRequest
        {
            Element = EditorRepo.OverlayTableId, Text = "length(number) < 3", Site = "check", Values = values.RootElement,
        }, Ct)).Preview!;
        Assert.Equal(("length(number) < 3", false), (text.Text, text.Answer));
    }

    [Fact]
    public async Task Preview_of_an_overlay_check_reads_values_by_the_names_its_text_prints()
    {
        // The check reads issuedOn through an override entry that renames its column issue_date, and total.amount: the preview prints
        // them by the attributes' names, so its JavaScript reads values by those names (a value object's members nested), as an entity
        // check's does, and answers by them.
        await using var r = EditorRepo.Create(packs: false);
        const string EntryId = "01K9EXR0000000000000000W21";
        OverlayChecks(r, $$"""
            { "id": "01K9EXR0000000000000000W22", "name": "ck_entry", "condition": { "op": "le", "args": [{ "path": ["{{EntryId}}"] }, { "path": ["{{DueOnId}}"] }] } }
            """, $$"""
            { "id": "01K9EXR0000000000000000W12", "name": "ck_invoices_total", "condition": { "op": "ge", "args": [{ "path": ["{{TotalAmount}}"] }, { "value": 0 }] } }
            """);
        EditOnDisk(r, OverlayPath, t => t["columns"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "{{EntryId}}", "attribute": "{{IssuedOnId}}", "name": "issue_date" }""")));
        async Task<ExpressionPreviewDetail> PreviewAsync(string element, string values)
        {
            using var row = JsonDocument.Parse(values);
            return (await r.Store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = element, Values = row.RootElement }, Ct)).Preview!;
        }

        var late = await PreviewAsync("01K9EXR0000000000000000W22", """{ "issuedOn": "2024-03-01", "dueOn": "2024-02-01" }""");
        var early = await PreviewAsync("01K9EXR0000000000000000W22", """{ "issuedOn": "2024-01-01", "dueOn": "2024-02-01" }""");
        var negative = await PreviewAsync("01K9EXR0000000000000000W12", """{ "total": { "amount": -1 } }""");

        Assert.Equal(("issuedOn <= dueOn", "(row) => mq.passes(mq.le(mq.date(mq.at(row, \"issuedOn\")), mq.date(mq.at(row, \"dueOn\"))))", false),
            (late.Text, late.JavaScript, late.Answer));
        Assert.Equal(true, early.Answer);
        Assert.Equal(("total.amount >= 0", false), (negative.Text, negative.Answer));
        Assert.Contains("mq.at(row, \"total\", \"amount\")", negative.JavaScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_of_an_overlay_check_reads_an_attribute_as_its_override_entry_makes_its_column()
    {
        // dueOn's entry makes its column not null and number's makes it char(32), so a check on them is not nullable and reads number
        // without its trailing spaces.
        await using var r = EditorRepo.Create(packs: false);
        OverlayChecks(r, DueAfterIssue, $$"""
            { "id": "01K9EXR0000000000000000W13", "name": "ck_invoices_number",
              "condition": { "op": "ge", "args": [{ "call": "length", "args": [{ "path": ["{{NumberId}}"] }] }, { "value": 3 }] } }
            """);
        EditOnDisk(r, OverlayPath, t =>
        {
            t["columns"]![0]!["nativeType"] = "char(32)";
            t["columns"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "01K9EXR0000000000000000W24", "attribute": "{{DueOnId}}", "nullable": false }"""));
        });

        var due = (await r.Store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = "01K9EXR0000000000000000W11" }, Ct)).Preview!;
        var number = (await r.Store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = "01K9EXR0000000000000000W13" }, Ct)).Preview!;

        Assert.False(due.Nullable);
        Assert.Contains("mq.fixed(mq.at(row, \"number\"))", number.JavaScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_on_an_overlay_check_may_name_an_override_entry_by_its_own_name_unless_another_member_has_it()
    {
        // issuedOn's entry renames its column issue_date, which text may write for issuedOn, and prints as issuedOn; number's entry renames
        // its column dueOn, the name of Invoice's attribute dueOn, which text keeps reading.
        await using var r = EditorRepo.Create(packs: false);
        OverlayChecks(r);
        EditOnDisk(r, OverlayPath, t =>
        {
            t["columns"]![0]!["name"] = "dueOn";
            t["columns"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "01K9EXR0000000000000000W21", "attribute": "{{IssuedOnId}}", "name": "issue_date" }"""));
        });
        var overlay = (await r.Store.GetElementAsync(EditorRepo.OverlayTableId, Ct))!;
        var node = JsonNode.Parse(overlay.Json.GetRawText())!.AsObject();
        node["checks"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "01K9EXW0000000000000000W03", "name": "ck_invoices_due_after_issue", "condition": { "text": "issue_date <= dueOn and length(number) > 0" } }
            """));
        var saved = await r.Store.SaveAsync(EditorRepo.OverlayTableId, Encoding.UTF8.GetBytes(node.ToJsonString()), overlay.Hash, ChangeSource.Editor, Ct);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var stored = JsonNode.Parse(File.ReadAllText(r.Repo.PathOf(OverlayPath)))!["checks"]![1]!["condition"]!.ToJsonString();
        Assert.Equal($$"""
            {"op":"and","args":[{"op":"le","args":[{"path":["{{IssuedOnId}}"]},{"path":["{{DueOnId}}"]}]},{"op":"gt","args":[{"call":"length","args":[{"path":["{{NumberId}}"]}]},{"value":0}]}]}
            """, stored);
        var preview = (await r.Store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = "01K9EXW0000000000000000W03" }, Ct)).Preview!;
        Assert.Equal("issuedOn <= dueOn and length(number) > 0", preview.Text);
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
