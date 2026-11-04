using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Expressions.ExpressionRuleTests;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>
/// <see cref="ModelStore.PreviewExpressionAsync"/> (next-phase-3-design.md section 3.1, "CLI and MCP") over the bindings fixture's Product:
/// text reads only what it names, the SQL it shows for a binding check is the DDL's, and its messages name the element's kind.
/// </summary>
public sealed class ExpressionPreviewTests
{
    private const string ColorId = "01K9EXV0000000000000000E01";
    private const string RedId = "01K9EXV0000000000000000E02";
    private const string BlueId = "01K9EXV0000000000000000E03";
    private const string SizeId = "01K9EXV0000000000000000E04";
    private const string SmallId = "01K9EXV0000000000000000E05";
    private const string ColorAttributeId = "01K9EXV0000000000000000A01";
    private const string ColorColumnId = "01K9EXV0000000000000000K01";
    private const string PositiveId = "01K9EXV0000000000000000C01";
    private const string ShortId = "01K9EXV0000000000000000C02";
    private const string CurrencyRowId = "01K9EXV0000000000000000R13";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Product gains a color attribute of the enum Color (Red 10, Blue 20), which its binding maps to an integer column, beside an enum
    /// Size, the reference types Unit (with Product's unit attribute, <see cref="ExpressionRuleTests.IntegerUnitColumn"/>) and Currency,
    /// and the constraints PositiveAmount and ShortText.
    /// </summary>
    private static TempRepo Catalog()
    {
        var repo = Repo();
        IntegerUnitColumn(repo, typeRef: false);
        repo.WriteFile(".maquettiste/model/enums/color.json", $$"""
            { "kind": "enum", "id": "{{ColorId}}", "name": "Color", "package": "01K6BFX0000000000000000001",
              "members": [ { "id": "{{RedId}}", "name": "Red", "value": 10 }, { "id": "{{BlueId}}", "name": "Blue", "value": 20 } ] }
            """);
        repo.WriteFile(".maquettiste/model/enums/size.json", $$"""
            { "kind": "enum", "id": "{{SizeId}}", "name": "Size", "members": [ { "id": "{{SmallId}}", "name": "Small" } ] }
            """);
        repo.WriteFile(".maquettiste/model/reference-types/currency.json", """
            { "kind": "reference-type", "id": "01K9EXV0000000000000000R10", "name": "Currency", "package": "01K6BFX0000000000000000001",
              "code": { "id": "01K9EXV0000000000000000R11", "length": 3 }, "label": { "id": "01K9EXV0000000000000000R12", "length": 40 } }
            """);
        repo.WriteFile(".maquettiste/model/seeds/currency/currency.json", $$"""
            { "kind": "seed", "id": "01K9EXV0000000000000000R14", "name": "Currency", "target": "01K9EXV0000000000000000R10", "columns": ["code", "label"],
              "rows": [ { "id": "{{CurrencyRowId}}", "values": ["EUR", "Euro"] } ] }
            """);
        repo.WriteFile(".maquettiste/model/constraints/positive-amount.json", $$"""
            { "kind": "constraint", "id": "{{PositiveId}}", "name": "PositiveAmount",
              "parameters": [ { "id": "01K9EXV0000000000000000C03", "name": "amount", "type": "decimal" } ],
              "condition": { "op": "gt", "args": [{ "path": ["01K9EXV0000000000000000C03"] }, { "value": 0 }] } }
            """);
        repo.WriteFile(".maquettiste/model/constraints/short-text.json", $$"""
            { "kind": "constraint", "id": "{{ShortId}}", "name": "ShortText",
              "parameters": [ { "id": "01K9EXV0000000000000000C04", "name": "s", "type": "string" } ],
              "condition": { "op": "le", "args": [{ "call": "length", "args": [{ "path": ["01K9EXV0000000000000000C04"] }] }, { "value": 10 }] } }
            """);
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(new JsonObject { ["id"] = ColorColumnId, ["name"] = "color_code", ["type"] = "int32", ["nullable"] = true }));
        Edit(repo, EntityPath, e =>
        {
            e["attributes"]!.AsArray().Add(new JsonObject { ["id"] = ColorAttributeId, ["name"] = "color", ["type"] = new JsonObject { ["ref"] = ColorId } });
            ((JsonObject)e["bindings"]![0]!)["fields"]!.AsArray().Add(new JsonObject { ["attribute"] = ColorAttributeId, ["column"] = ColorColumnId });
        });
        return repo;
    }

    [Fact]
    public async Task Text_reads_only_the_enums_reference_types_and_constraints_it_names()
    {
        // Models hold hundreds of lookup types: a scope for text looks up each name the text writes, and nothing else, where it once
        // held every enum, every reference type with the codes of every seed, and every constraint.
        using var repo = Catalog();
        var snapshot = await new ModelStore(repo.Options).GetSnapshotAsync(Ct);
        var product = snapshot.Get<Maquettiste.Engine.Model.Entity>(ProductId)!;
        var scope = ExprSites.Scope(snapshot, product, snapshot.GetDocument(ProductId), ExprSites.Free(snapshot, product, ExprSiteKind.Check)!, text: true);

        var parsed = ExprText.Parse("color = Color.Red and unit = Unit.kg and satisfies PositiveAmount(amount: price)", scope);

        Assert.NotNull(parsed.Tree);
        Assert.NotNull(scope.RefNames(RedId));
        Assert.NotNull(scope.RefNames(KgId));
        Assert.NotNull(scope.Constraint(PositiveId));
        Assert.Null(scope.RefNames(SmallId));
        Assert.Null(scope.RefNames(CurrencyRowId));
        Assert.Null(scope.Constraint(ShortId));
        Assert.Null(ExprText.Parse("Size.Large = Size.Large", scope).Tree);
    }

    [Fact]
    public async Task Text_previews_with_what_it_names_and_saves_as_a_tree()
    {
        // The preview's text and the write path's both find the enum member, the code and the constraint the text names.
        using var repo = Catalog();
        var store = new ModelStore(repo.Options);
        const string text = "color = Color.Red and unit = Unit.kg and satisfies PositiveAmount(amount: price)";
        var values = JsonSerializer.SerializeToElement(new JsonObject { ["color"] = "Red", ["unit"] = "kg", ["price"] = "1.50" });

        var preview = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = ProductId, Text = text, Site = "check", Values = values }, Ct);

        Assert.True(preview.Preview is { Answer: true }, string.Join("; ", preview.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(text, preview.Preview.Text);
        Edit(repo, EntityPath, e => e["checks"] = JsonNode.Parse($$"""
            [ { "id": "01K9EXV0000000000000000X01", "name": "redKg", "attribute": "{{ColorAttributeId}}", "condition": { "text": "{{text}}" } } ]
            """));
        var writer = new ModelStore(repo.Options);
        var applied = await writer.ApplyBatchAsync(writer.ParseBatch("""{ "operations": [ { "op": "text-to-trees" } ] }"""u8).Batch!, ChangeSource.Cli, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var saved = JsonNode.Parse(repo.ReadFile(EntityPath))!["checks"]![0]!["condition"]!.ToJsonString();
        Assert.Contains(RedId, saved, StringComparison.Ordinal);
        Assert.Contains(KgId, saved, StringComparison.Ordinal);
        Assert.Contains(PositiveId, saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binding_check_previews_and_renders_the_sql_the_ddl_holds()
    {
        // The CHECK resolution writes into the table, the preview's SQL and expression_sql through the binding are one text: an enum
        // member as its stored value, string order under COLLATE "C", and division as PostgreSQL rounds it.
        using var repo = Catalog();
        (string Text, string Attribute)[] checks =
        [
            ("price > 0", PriceId), ("color = Color.Red or color is null", ColorAttributeId), ("price * 2 > 1", PriceId),
            ("name between 'A' and 'Z'", "01K6BFX0000000000000000024"), ("price / 3 > 0", PriceId), ("color in (Color.Red, Color.Blue)", ColorAttributeId),
        ];
        var ids = checks.Select((_, i) => "01K9EXV0000000000000000X" + (i + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Edit(repo, EntityPath, e =>
        {
            e["checks"] = new JsonArray([.. checks.Select((c, i) => (JsonNode)new JsonObject
            {
                ["id"] = ids[i], ["name"] = "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), ["attribute"] = c.Attribute,
                ["condition"] = new JsonObject { ["text"] = c.Text },
            })]);
            ((JsonObject)e["bindings"]![0]!)["checks"] = new JsonArray([.. ids.Select(id => (JsonNode)id)]);
        });
        var store = new ModelStore(repo.Options);
        var applied = await store.ApplyBatchAsync(store.ParseBatch("""{ "operations": [ { "op": "text-to-trees" } ] }"""u8).Batch!, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, applied.Outcome);
        var model = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
        var ddl = model.Db("main").Table("products").Checks.Where(c => c.Id is not null).ToDictionary(c => c.Id!, c => c.Expression, StringComparer.Ordinal);

        for (var i = 0; i < checks.Length; i++)
        {
            var preview = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = ids[i] }, Ct);
            var rendered = await Adhoc.RenderAsync("{{ expression_sql entity.checks[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "].condition entity.bindings.main }}",
                model.Entity("Product"), model: model);
            Assert.Equal(ddl[ids[i]], preview.Preview!.Sql);
            Assert.Equal(ddl[ids[i]], rendered.Text());
        }

        Assert.Equal("color_code = 10 OR color_code IS NULL", ddl[ids[1]]);
    }

    [Fact]
    public async Task A_site_the_element_does_not_have_is_named_with_its_kinds_article()
    {
        using var repo = Catalog();
        var store = new ModelStore(repo.Options);

        var entity = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = ProductId, Text = "price > 0", Site = "guard" }, Ct));
        var table = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = ProductsId, Text = "price > 0", Site = "guard" }, Ct));

        Assert.StartsWith("An entity has no guard site: ", entity.Message, StringComparison.Ordinal);
        Assert.StartsWith("A table has no guard site: ", table.Message, StringComparison.Ordinal);
    }
}
