using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Expressions.ExpressionRuleTests;

namespace Maquettiste.Engine.Tests.Expressions;

/// <summary>
/// Checks, entity checks, derived values and constraints in the resolved model (next-phase-3-design.md section 3.1, "Where lowering
/// runs", task H7), over the bindings fixture: the invented entity Product, bound to its table products, whose columns and attributes
/// are id, sku, name and price. A tree check is lowered during resolution for the database that holds its table, into
/// <see cref="RCheck.Expression"/>, so the sql-ddl pack prints it unchanged.
/// </summary>
public sealed class ExpressionResolutionTests
{
    private const string CheckId = "01K9EXR0000000000000000C01";
    private const string EntityCheckId = "01K9EXR0000000000000000E01";
    private const string ConstraintId = "01K9EXR0000000000000000K01";
    private const string ParameterId = "01K9EXR0000000000000000K02";
    private const string DatabasePath = ".maquettiste/model/databases/main/database.json";
    private const string DerivedId = "01K9EXR0000000000000000B01";
    private const string DiscountId = "01K9EXR0000000000000000B02";
    private const string DerivedTableId = "01K9EXR0000000000000000T01";
    private const string KeyAttributeId = "01K6BFX0000000000000000022";
    private const string KeyColumnId = "01K6BFX0000000000000000003";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Price => $$"""{ "path": ["{{PriceColumnId}}"] }""";

    private static string Sku => $$"""{ "path": ["{{SkuColumnId}}"] }""";

    private static async Task<ResolvedModel> ResolveAsync(TempRepo repo)
    {
        var store = new ModelStore(repo.Options);
        return await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
    }

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(TempRepo repo) =>
        (await new ModelStore(repo.Options).ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    private static RTable Products(ResolvedModel model) => model.Db("main").Table("products");

    /// <summary>The entity check pricePositive (<c>price &gt; 0</c>, pointing at price), listed by the binding or not.</summary>
    private static void EntityCheck(TempRepo repo, bool listed)
    {
        Edit(repo, EntityPath, e =>
        {
            e["checks"] = JsonNode.Parse($$"""
                [ { "id": "{{EntityCheckId}}", "name": "pricePositive", "attribute": "{{PriceId}}", "message": "The price must be positive.",
                    "condition": { "op": "gt", "args": [{ "path": ["{{PriceId}}"] }, { "value": 0 }] } } ]
                """);
            if (listed)
                ((JsonObject)e["bindings"]![0]!)["checks"] = new JsonArray(EntityCheckId);
        });
    }

    /// <summary>
    /// The entity DiscountedProduct, derived from Product with a discount attribute, bound to <paramref name="source"/> with id, price and
    /// (when given) discount mapped, its binding listing the inherited check pricePositive.
    /// </summary>
    private static void DiscountedProduct(TempRepo repo, string source, string idColumn, string priceColumn, string? discountColumn)
    {
        var fields = new JsonArray(
            new JsonObject { ["attribute"] = KeyAttributeId, ["column"] = idColumn },
            new JsonObject { ["attribute"] = PriceId, ["column"] = priceColumn });
        if (discountColumn is not null)
            fields.Add(new JsonObject { ["attribute"] = DiscountId, ["column"] = discountColumn });
        repo.WriteFile(".maquettiste/model/entities/discounted-product.json", $$"""
            {
              "kind": "entity",
              "id": "{{DerivedId}}",
              "name": "DiscountedProduct",
              "package": "01K6BFX0000000000000000001",
              "base": "{{ProductId}}",
              "attributes": [ { "id": "{{DiscountId}}", "name": "discount", "type": "decimal", "precision": 5, "scale": 2 } ],
              "bindings": [ { "id": "01K9EXR0000000000000000B10", "database": "01K6BFX0000000000000000002", "source": "{{source}}",
                "fields": {{fields.ToJsonString()}}, "checks": ["{{EntityCheckId}}"] } ]
            }
            """);
    }

    /// <summary>The stereotype audited: created_at, updated_at and the check ck_updated_after_created (<c>updated_at &gt;= created_at</c>).</summary>
    private static void Audited(TempRepo repo) => repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/audited.json", """
        {
          "kind": "stereotype",
          "id": "01K9EXR0000000000000000S01",
          "key": "audited",
          "name": "Audited",
          "appliesTo": ["table"],
          "columns": [
            { "id": "01K9EXR0000000000000000S02", "name": "created_at", "type": "datetimeoffset", "nullable": false, "defaultSql": { "*": "now()" } },
            { "id": "01K9EXR0000000000000000S03", "name": "updated_at", "type": "datetimeoffset" }
          ],
          "checks": [
            { "id": "01K9EXR0000000000000000S04", "name": "ck_updated_after_created",
              "condition": { "op": "ge", "args": [{ "path": ["01K9EXR0000000000000000S03"] }, { "path": ["01K9EXR0000000000000000S02"] }] } }
          ]
        }
        """);

    /// <summary>The constraint PositiveAmount(amount: decimal): <c>amount &gt; 0</c>.</summary>
    private static void PositiveAmount(TempRepo repo) => repo.WriteFile(".maquettiste/model/constraints/positive-amount.json", $$"""
        {
          "kind": "constraint",
          "id": "{{ConstraintId}}",
          "name": "PositiveAmount",
          "parameters": [ { "id": "{{ParameterId}}", "name": "amount", "type": "decimal" } ],
          "condition": { "op": "gt", "args": [{ "path": ["{{ParameterId}}"] }, { "value": 0 }] },
          "message": "The amount must be positive."
        }
        """);

    [Fact]
    public async Task A_table_check_resolves_to_SQL_with_its_condition()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "op": "gt", "args": [{{Price}}, { "value": 0 }] }""");
        var model = await ResolveAsync(repo);

        var check = Assert.Single(Products(model).Checks);
        Assert.Equal(("ck_products_rule", "price > 0", "own"), (check.Name, check.Expression, check.MemberOrigin.Source));
        var condition = Assert.IsType<RExpression>(check.Condition);
        Assert.Equal(("op", "gt", "bool", false), (condition.Node, condition.Op, condition.Type.Kind, condition.Nullable));
        Assert.Same(Products(model).Column("price"), condition.Args[0].Target);
        Assert.Equal(("value", 0L), (condition.Args[1].Node, condition.Args[1].Value));
        Assert.Equal("price > 0", condition.Text);
        Assert.Null(condition.Args[0].JavaScript);
        Assert.Equal("(row) => mq.passes(mq.gt(mq.dec(mq.at(row, \"price\"), 2), mq.dec(\"0\")))", condition.JavaScript);
        Assert.NotNull(check.Shape);
    }

    [Fact]
    public async Task A_column_check_resolves_to_SQL_on_its_column()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "op": "ge", "args": [{ "call": "length", "args": [{{Sku}}] }, { "value": 3 }] }""", column: SkuColumnId);
        var model = await ResolveAsync(repo);

        var check = Assert.Single(Products(model).Checks);
        Assert.Equal("char_length(sku) >= 3", check.Expression);
        Assert.Same(Products(model).Column("sku"), check.Column);
    }

    [Fact]
    public async Task A_stereotype_check_resolves_on_each_table_that_carries_it()
    {
        using var repo = Repo();
        Audited(repo);
        Edit(repo, TablePath, t => t["stereotypes"] = new JsonArray("audited"));
        var model = await ResolveAsync(repo);

        var products = Products(model);
        var check = Assert.Single(products.Checks);
        Assert.Equal(("ck_updated_after_created", "updated_at >= created_at", "stereotype"), (check.Name, check.Expression, check.MemberOrigin.Source));
        Assert.Same(products.Column("updated_at"), check.Condition!.Args[0].Target);
        Assert.Equal(ProductsId + ".01K9EXR0000000000000000S04", check.Id);
    }

    [Fact]
    public async Task A_table_check_reads_a_column_its_stereotype_contributes_by_its_keyed_id()
    {
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/priced.json", """
            {
              "kind": "stereotype",
              "id": "01K9EXR0000000000000000S11",
              "key": "priced",
              "name": "Priced",
              "appliesTo": ["table"],
              "columns": [ { "id": "01K9EXR0000000000000000S12", "name": "list_price", "type": "decimal", "precision": 12, "scale": 2 } ]
            }
            """);
        Edit(repo, TablePath, t => t["stereotypes"] = new JsonArray("priced"));
        TableCheck(repo, $$"""{ "op": "le", "args": [{{Price}}, { "path": ["{{ProductsId}}.01K9EXR0000000000000000S12"] }] }""");
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));
        var model = await ResolveAsync(repo);

        Assert.Equal("price <= list_price", Assert.Single(Products(model).Checks).Expression);
    }

    [Fact]
    public async Task A_binding_check_resolves_on_its_write_table_through_the_field_map()
    {
        using var repo = Repo();
        EntityCheck(repo, listed: true);
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));
        var model = await ResolveAsync(repo);

        var products = Products(model);
        var check = Assert.Single(products.Checks);
        Assert.Equal((EntityCheckId, "ck_products_price_positive", "price > 0", "binding"), (check.Id, check.Name, check.Expression, check.MemberOrigin.Source));
        Assert.Same(products.Column("price"), check.Condition!.Args[0].Target);
        Assert.Equal("(row) => mq.passes(mq.gt(mq.dec(mq.at(row, \"price\"), 2), mq.dec(\"0\")))", check.Condition.JavaScript);

        // The entity's own check reads the attribute, by its API name, and lists the binding.
        var product = model.Entity("Product");
        var entityCheck = Assert.Single(product.Checks);
        Assert.Equal(("pricePositive", "The price must be positive.", false), (entityCheck.Name, entityCheck.Message, entityCheck.IsInherited));
        Assert.Same(product.Attributes.Single(a => a.Name == "price"), entityCheck.Attribute);
        Assert.Same(entityCheck.Attribute, entityCheck.Condition!.Args[0].Target);
        Assert.Equal("price > 0", entityCheck.Condition.Text);
        Assert.Equal("(row) => mq.passes(mq.gt(mq.dec(mq.at(row, \"price\"), 2), mq.dec(\"0\")))", entityCheck.Condition.JavaScript);
        Assert.Same(product.Bindings["main"], Assert.Single(entityCheck.Bindings));
    }

    [Fact]
    public async Task A_binding_that_lists_nothing_adds_nothing()
    {
        using var repo = Repo();
        EntityCheck(repo, listed: false);
        var model = await ResolveAsync(repo);

        Assert.Empty(Products(model).Checks);
        var entityCheck = Assert.Single(model.Entity("Product").Checks);
        Assert.NotNull(entityCheck.Condition);
        Assert.Empty(entityCheck.Bindings);
    }

    [Fact]
    public async Task A_derived_entity_lists_its_bases_checks_first_by_id()
    {
        using var repo = Repo();
        EntityCheck(repo, listed: false);
        repo.WriteFile(".maquettiste/model/entities/discounted-product.json", $$"""
            {
              "kind": "entity",
              "id": "01K9EXR0000000000000000B01",
              "name": "DiscountedProduct",
              "package": "01K6BFX0000000000000000001",
              "base": "{{ProductId}}",
              "attributes": [ { "id": "01K9EXR0000000000000000B02", "name": "discount", "type": "decimal", "precision": 5, "scale": 2 } ],
              "checks": [ { "id": "01K9EXR0000000000000000B03", "name": "discountBelowPrice", "attribute": "01K9EXR0000000000000000B02",
                "condition": { "op": "lt", "args": [{ "path": ["01K9EXR0000000000000000B02"] }, { "path": ["{{PriceId}}"] }] } } ]
            }
            """);
        var model = await ResolveAsync(repo);

        var discounted = model.Entity("DiscountedProduct");
        Assert.Equal([(EntityCheckId, true), ("01K9EXR0000000000000000B03", false)], discounted.Checks.Select(c => (c.Id, c.IsInherited)));
        var inherited = discounted.Checks[0];
        Assert.Same(discounted.Attributes.Single(a => a.Name == "price"), inherited.Attribute);
        Assert.Same(inherited.Attribute, inherited.Condition!.Args[0].Target);
        Assert.Equal("discount < price", discounted.Checks[1].Condition!.Text);
        Assert.Same(model.Entity("Product").Checks[0], model.Find(EntityCheckId));
    }

    [Fact]
    public async Task Constraints_inline_and_resolve_on_their_own()
    {
        using var repo = Repo();
        PositiveAmount(repo);
        TableCheck(repo, $$"""{ "satisfies": "{{ConstraintId}}", "with": [{ "parameter": "{{ParameterId}}", "value": {{Price}} }] }""");
        var model = await ResolveAsync(repo);

        var products = Products(model);
        var check = Assert.Single(products.Checks);
        Assert.Equal("price > 0", check.Expression);
        // The condition is the constraint's, with the argument in place of the parameter; its text is the citation.
        var condition = check.Condition!;
        Assert.Equal(("op", "gt"), (condition.Node, condition.Op));
        Assert.Same(products.Column("price"), condition.Args[0].Target);
        Assert.Equal("satisfies PositiveAmount(amount: price)", condition.Text);
        Assert.Equal("0", condition.Args[1].Text);
        Assert.Contains("e:" + ConstraintId, products.Dependencies);

        var constraint = Assert.Single(model.Constraints);
        Assert.Equal(("PositiveAmount", "The amount must be positive."), (constraint.Name, constraint.Message));
        var parameter = Assert.Single(constraint.Parameters);
        Assert.Equal(("amount", "decimal"), (parameter.Name, parameter.Type.Builtin));
        Assert.Same(parameter, constraint.Condition!.Args[0].Target);
        Assert.Equal("amount > 0", constraint.Condition.Text);
        Assert.Same(constraint, model.Find(ConstraintId));
    }

    [Fact]
    public async Task A_derived_value_resolves_and_lowers_to_JavaScript_on_read()
    {
        using var repo = Repo();
        Edit(repo, EntityPath, e => e["attributes"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "01K9EXR0000000000000000D01", "name": "doubled", "type": "decimal",
              "derived": { "value": { "op": "mul", "args": [{ "path": ["01K6BFX0000000000000000025"] }, { "value": 2 }] } } }
            """)));
        var model = await ResolveAsync(repo);

        var doubled = model.Entity("Product").Attributes.Single(a => a.Name == "doubled");
        var value = doubled.Derived!.Value!;
        Assert.Equal(("op", "mul", "decimal"), (value.Node, value.Op, value.Type.Kind));
        Assert.Equal("price * 2", value.Text);
        Assert.Equal("", doubled.Derived.Expression);
        Assert.StartsWith("(row) => mq.out(", value.JavaScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tree_check_on_a_sqlserver_database_is_MQ4056_and_left_out_of_the_DDL()
    {
        using var repo = Repo();
        Edit(repo, DatabasePath, d => d["dialect"] = "sqlserver");
        TableCheck(repo, $$"""{ "op": "gt", "args": [{{Price}}, { "value": 0 }] }""");
        EntityCheck(repo, listed: true);

        var diagnostics = await ValidateAsync(repo);
        Assert.Contains(diagnostics, d => d.Rule == "MQ4056" && d.ElementId == ProductsId && d.JsonPointer == "/checks/0/condition");
        Assert.Contains(diagnostics, d => d.Rule == "MQ4056" && d.ElementId == ProductId && d.JsonPointer == "/bindings/0/checks/0");
        Assert.Empty(Products(await ResolveAsync(repo)).Checks);
    }

    [Fact]
    public async Task A_check_that_does_not_type_is_left_out_and_reported_by_validation_only()
    {
        using var repo = Repo();
        TableCheck(repo, $$"""{ "op": "gt", "args": [{{Sku}}, { "value": 0 }] }""");
        var model = await ResolveAsync(repo);

        Assert.Empty(Products(model).Checks);
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_code_its_column_cannot_store_leaves_the_check_out_and_the_model_resolves()
    {
        // Unit's codes are text. The binding maps unit to an int32 column, and a table check meets an int32 column typed by Unit: neither
        // check can hold kg, so each is reported where validation reports it and left out, and the rest of the model resolves.
        using var repo = Repo();
        IntegerUnitColumn(repo, typeRef: false);
        KgOnly(repo);
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "01K9EXR0000000000000000R08", "name": "pack_unit", "type": "int32", "typeRef": { "ref": "01K9EXR0000000000000000R01" } }
            """)));
        TableCheck(repo, $$"""{ "op": "eq", "args": [{ "path": ["01K9EXR0000000000000000R08"] }, { "ref": "{{KgId}}" }] }""");
        var model = await ResolveAsync(repo);

        Assert.Empty(Products(model).Checks);
        Assert.Contains(model.Diagnostics, d => d.Rule == "MQ3110" && d.ElementId == ProductId && d.JsonPointer == "/bindings/0/checks/0");
        Assert.Contains(model.Diagnostics, d => d.Rule == "MQ3103" && d.ElementId == ProductsId && d.JsonPointer == "/checks/0/condition");
        // The entity's own check reads the API's form, the code's text.
        Assert.Contains("\"kg\"", Assert.Single(model.Entity("Product").Checks).Condition!.JavaScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_check_lowers_a_code_to_the_text_its_column_stores()
    {
        // The seed holds Unit's codes in its code column; a string column typed by Unit stores them as they are.
        using var repo = Repo();
        IntegerUnitColumn(repo, typeRef: true);
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "01K9EXR0000000000000000R09", "name": "unit_code", "type": "string", "length": 8, "typeRef": { "ref": "01K9EXR0000000000000000R01" } }
            """)));
        TableCheck(repo, $$"""{ "op": "eq", "args": [{ "path": ["01K9EXR0000000000000000R09"] }, { "ref": "{{KgId}}" }] }""");
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal));
        var check = Assert.Single(Products(await ResolveAsync(repo)).Checks);

        Assert.Equal("unit_code COLLATE \"C\" = 'kg' COLLATE \"C\"", check.Expression);
        Assert.Equal(("ref", "kg"), (check.Condition!.Args[1].Node, check.Condition.Args[1].Value));
    }

    [Fact]
    public async Task A_binding_check_lowers_an_enum_member_to_the_value_its_column_stores()
    {
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/enums/status.json", """
            {
              "kind": "enum",
              "id": "01K9EXR0000000000000000N01",
              "name": "Status",
              "package": "01K6BFX0000000000000000001",
              "members": [ { "id": "01K9EXR0000000000000000N02", "name": "Active", "value": 10 },
                           { "id": "01K9EXR0000000000000000N03", "name": "Discontinued", "code": "GONE", "value": 20 } ]
            }
            """);
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01K9EXR0000000000000000N05", "name": "status", "type": "int32" }""")));
        Edit(repo, EntityPath, e =>
        {
            e["attributes"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01K9EXR0000000000000000N04", "name": "status", "type": { "ref": "01K9EXR0000000000000000N01" } }"""));
            ((JsonObject)e["bindings"]![0]!)["fields"]!.AsArray().Add(JsonNode.Parse("""{ "attribute": "01K9EXR0000000000000000N04", "column": "01K9EXR0000000000000000N05" }"""));
            e["checks"] = JsonNode.Parse($$"""
                [ { "id": "{{EntityCheckId}}", "name": "notGone", "attribute": "01K9EXR0000000000000000N04",
                    "condition": { "op": "ne", "args": [{ "path": ["01K9EXR0000000000000000N04"] }, { "ref": "01K9EXR0000000000000000N03" }] } } ]
                """);
            ((JsonObject)e["bindings"]![0]!)["checks"] = new JsonArray(EntityCheckId);
        });
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error);
        var model = await ResolveAsync(repo);

        // The table stores the member's value; the entity's own check reads the API's form, its code.
        var check = Assert.Single(Products(model).Checks);
        Assert.Equal("status <> 20", check.Expression);
        Assert.Contains("20", check.Condition!.JavaScript, StringComparison.Ordinal);
        Assert.Contains("\"GONE\"", Assert.Single(model.Entity("Product").Checks).Condition!.JavaScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binding_check_keeps_its_name_and_shape_across_a_column_and_an_attribute_rename()
    {
        using var repo = Repo();
        EntityCheck(repo, listed: true);
        var before = Assert.Single(Products(await ResolveAsync(repo)).Checks);

        Edit(repo, TablePath, t => t["columns"]![3]!["name"] = "unit_price");
        Edit(repo, EntityPath, e => e["attributes"]![3]!["name"] = "unitPrice");
        var model = await ResolveAsync(repo);

        var after = Assert.Single(Products(model).Checks);
        Assert.Equal(("unit_price > 0", before.Name, before.Shape), (after.Expression, after.Name, after.Shape));
        Assert.Equal("unitPrice > 0", Assert.Single(model.Entity("Product").Checks).Condition!.Text);
    }

    [Fact]
    public async Task A_check_two_bindings_write_into_one_table_is_reported_by_its_name()
    {
        // Product and DiscountedProduct both bind to products and list pricePositive, so the table would hold two checks of one name.
        using var repo = Repo();
        EntityCheck(repo, listed: true);
        DiscountedProduct(repo, ProductsId, KeyColumnId, PriceColumnId, null);
        var model = await ResolveAsync(repo);

        Assert.Equal(2, Products(model).Checks.Count);
        Assert.Contains(model.Diagnostics, d => d.Message.Contains("ck_products_price_positive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_binding_check_makes_its_table_depend_on_the_base_that_declares_it()
    {
        // DiscountedProduct inherits pricePositive and writes it into discounted_products: editing the condition in Product's file
        // changes that table's DDL, so the table depends on Product.
        using var repo = Repo();
        EntityCheck(repo, listed: false);
        repo.WriteFile(".maquettiste/model/databases/main/tables/discounted-products.json", $$"""
            {
              "kind": "table",
              "id": "{{DerivedTableId}}",
              "name": "discounted_products",
              "database": "01K6BFX0000000000000000002",
              "columns": [
                { "id": "01K9EXR0000000000000000T02", "name": "id", "type": "int64", "nullable": false, "generated": "identity" },
                { "id": "01K9EXR0000000000000000T03", "name": "price", "type": "decimal", "precision": 12, "scale": 2, "nullable": false },
                { "id": "01K9EXR0000000000000000T04", "name": "discount", "type": "decimal", "precision": 5, "scale": 2 }
              ],
              "primaryKey": { "columns": ["01K9EXR0000000000000000T02"] }
            }
            """);
        DiscountedProduct(repo, DerivedTableId, "01K9EXR0000000000000000T02", "01K9EXR0000000000000000T03", "01K9EXR0000000000000000T04");
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error);
        var table = (await ResolveAsync(repo)).Db("main").Table("discounted_products");

        Assert.Equal("price > 0", Assert.Single(table.Checks).Expression);
        Assert.Contains("e:" + DerivedId, table.Dependencies);
        Assert.Contains("e:" + ProductId, table.Dependencies);
    }

    [Fact]
    public async Task A_binding_check_makes_its_table_depend_on_the_value_object_it_reads()
    {
        using var repo = Repo();
        repo.WriteFile(".maquettiste/model/types/money.json", """
            {
              "kind": "value-object",
              "id": "01K9EXR0000000000000000V01",
              "name": "Money",
              "package": "01K6BFX0000000000000000001",
              "attributes": [ { "id": "01K9EXR0000000000000000V02", "name": "amount", "type": "decimal", "precision": 18, "scale": 2, "required": true } ]
            }
            """);
        Edit(repo, TablePath, t => t["columns"]!.AsArray().Add(JsonNode.Parse("""
            { "id": "01K9EXR0000000000000000V05", "name": "total_amount", "type": "decimal", "precision": 18, "scale": 2 }
            """)));
        Edit(repo, EntityPath, e =>
        {
            e["attributes"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01K9EXR0000000000000000V04", "name": "total", "type": { "ref": "01K9EXR0000000000000000V01" } }"""));
            ((JsonObject)e["bindings"]![0]!)["fields"]!.AsArray().Add(JsonNode.Parse("""
                { "attribute": "01K9EXR0000000000000000V04.01K9EXR0000000000000000V02", "column": "01K9EXR0000000000000000V05" }
                """));
            e["checks"] = JsonNode.Parse($$"""
                [ { "id": "{{EntityCheckId}}", "name": "totalNotNegative", "attribute": "01K9EXR0000000000000000V04.01K9EXR0000000000000000V02",
                    "condition": { "op": "ge", "args": [{ "path": ["01K9EXR0000000000000000V04.01K9EXR0000000000000000V02"] }, { "value": 0 }] } } ]
                """);
            ((JsonObject)e["bindings"]![0]!)["checks"] = new JsonArray(EntityCheckId);
        });
        var products = Products(await ResolveAsync(repo));

        Assert.Equal("total_amount >= 0", Assert.Single(products.Checks).Expression);
        Assert.Contains("e:01K9EXR0000000000000000V01", products.Dependencies);
    }

    [Fact]
    public async Task Excluding_a_column_a_stereotype_check_reads_is_MQ2028_on_the_exclusion()
    {
        // products excludes created_at but keeps ck_updated_after_created, which reads it: the DDL cannot hold the check, so the
        // exclusion is reported as for a check whose column it excludes.
        using var repo = Repo();
        Audited(repo);
        Edit(repo, TablePath, t =>
        {
            t["stereotypes"] = new JsonArray("audited");
            t["excludes"] = new JsonArray("01K9EXR0000000000000000S02");
        });
        var diagnostics = await ValidateAsync(repo);

        Assert.Contains(diagnostics, d => d.Rule == "MQ2028" && d.ElementId == ProductsId && d.JsonPointer == "/excludes/0"
            && d.Message.Contains("check '01K9EXR0000000000000000S04'", StringComparison.Ordinal));
        Assert.Empty(Products(await ResolveAsync(repo)).Checks);
    }

    [Fact]
    public async Task An_overlay_check_on_a_column_of_a_stereotype_its_table_skips_is_MQ4008_at_the_check()
    {
        // Invoice carries audited, so its table stores audited's attributes as columns and skips audited's members: an overlay check on
        // audited's created_at, which the overlay's candidate members still hold, is reported rather than left out of the DDL silently.
        await using var r = EditorRepo.Create(packs: false);
        const string CreatedAt = "01K9EXR0000000000000000S12";
        EditOnDisk(r, ".maquettiste/model/vocabularies/stereotypes/audited.json", s => s["columns"] = JsonNode.Parse($$"""
            [ { "id": "{{CreatedAt}}", "name": "created_at", "type": "datetimeoffset" } ]
            """));
        EditOnDisk(r, ".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json", t => t["checks"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "01K9EXR0000000000000000S13", "name": "ck_invoices_created",
              "condition": { "op": "isNotNull", "args": [{ "path": ["{{EditorRepo.InvoiceId}}@{{EditorRepo.MainDatabaseId}}.{{CreatedAt}}"] }] } }
            """)));
        var model = await new ModelResolver(r.Repo.Options).ResolveAsync(await r.Store.GetSnapshotAsync(Ct), null, Ct);

        Assert.Contains(model.Diagnostics, d => d.Rule == "MQ4008" && d.ElementId == EditorRepo.OverlayTableId && d.JsonPointer == "/checks/1/condition");
        var invoices = model.Db("main").Tables.Single(t => t.Entity?.Id == EditorRepo.InvoiceId && t.Attribute is null);
        Assert.DoesNotContain(invoices.Checks, c => c.Id == "01K9EXR0000000000000000S13");
    }

    internal const string OverlayPath = ".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json";
    internal const string InvoicePath = ".maquettiste/model/entities/invoice.json";
    internal const string IssuedOnId = "01J92P0V0SNXS6PZ42VDK42HP9";
    internal const string NumberId = "01J92P0V0R3VSP7D5238DTNZX1";
    internal const string NumberEntryId = "01J92P0V1VQAA9FW71PKYWWHKN";
    internal const string TotalAmount = "01J92P0V0T6EA0XM025XQX8GBW.01J92P0V08P9BVJAPQ793XYNTQ";
    internal const string DueOnId = "01K9EXR0000000000000000W01";
    private const string NotesId = "01J92P0V0WKRGKH7YBKA2V30NC";

    /// <summary>
    /// The billing fixture with dueOn, an optional date, on Invoice and these checks on Invoice's overlay, after its SQL text check
    /// ck_invoice_status: each a JSON object with an id, a name and a condition.
    /// </summary>
    internal static void OverlayChecks(EditorRepo r, params string[] checks)
    {
        EditOnDisk(r, InvoicePath, e => e["attributes"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "{{DueOnId}}", "name": "dueOn", "type": "date" }""")));
        EditOnDisk(r, OverlayPath, t =>
        {
            foreach (var check in checks)
                t["checks"]!.AsArray().Add(JsonNode.Parse(check));
        });
    }

    /// <summary>The check ck_invoices_due_after_issue: <c>issuedOn &lt;= dueOn</c>, by the attributes' column keys.</summary>
    internal static string DueAfterIssue => $$"""
        { "id": "01K9EXR0000000000000000W11", "name": "ck_invoices_due_after_issue",
          "condition": { "op": "le", "args": [{ "path": ["{{IssuedOnId}}"] }, { "path": ["{{DueOnId}}"] }] } }
        """;

    private static RTable Invoices(ResolvedModel model) => model.Db("main").Tables.Single(t => t.Entity?.Id == EditorRepo.InvoiceId && t.Attribute is null);

    private static async Task<ResolvedModel> ResolveAsync(EditorRepo r) =>
        await new ModelResolver(r.Repo.Options).ResolveAsync(await r.Store.GetSnapshotAsync(Ct), null, Ct);

    [Fact]
    public async Task An_overlay_check_reads_its_entitys_attributes_by_name_and_lowers_to_their_columns()
    {
        // Invoice's overlay checks read issuedOn, dueOn, the value object member total.amount and number, each by its column key, and
        // number also through its override entry, which has no name: validation types them as attributes, their text names the
        // attributes, and the DDL names the columns the conventions give them.
        await using var r = EditorRepo.Create(packs: false);
        OverlayChecks(r, DueAfterIssue,
            $$"""
            { "id": "01K9EXR0000000000000000W12", "name": "ck_invoices_total",
              "condition": { "op": "ge", "args": [{ "path": ["{{TotalAmount}}"] }, { "value": 0 }] } }
            """,
            $$"""
            { "id": "01K9EXR0000000000000000W13", "name": "ck_invoices_number",
              "condition": { "op": "and", "args": [
                { "op": "ge", "args": [{ "call": "length", "args": [{ "path": ["{{NumberEntryId}}"] }] }, { "value": 3 }] },
                { "op": "le", "args": [{ "call": "length", "args": [{ "path": ["{{NumberId}}"] }] }, { "value": 32 }] }] } }
            """);
        Assert.DoesNotContain((await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics,
            d => d.FilePath == OverlayPath && d.Severity == DiagnosticSeverity.Error);
        var invoices = Invoices(await ResolveAsync(r));

        Assert.Equal(
            [("ck_invoice_status", "status in ('D', 'I', 'P', 'V')", null),
             ("ck_invoices_due_after_issue", "issued_on <= due_on", "issuedOn <= dueOn"),
             ("ck_invoices_total", "total_amount >= 0", "total.amount >= 0"),
             ("ck_invoices_number", "char_length(number) >= 3 AND char_length(number) <= 32", "length(number) >= 3 and length(number) <= 32")],
            invoices.Checks.Select(c => (c.Name, c.Expression, c.Condition?.Text)));
        var due = invoices.Checks[1].Condition!;
        Assert.Same(invoices.Column("issued_on"), due.Args[0].Target);
        Assert.Same(invoices.Column("due_on"), due.Args[1].Target);
        Assert.Contains("mq.at(row, \"issued_on\")", due.JavaScript, StringComparison.Ordinal);
        Assert.Contains("mq.at(row, \"due_on\")", due.JavaScript, StringComparison.Ordinal);
        var number = invoices.Checks[3].Condition!;
        Assert.Same(number.Args[0].Args[0].Args[0].Target, number.Args[1].Args[0].Args[0].Target);
    }

    [Fact]
    public async Task An_overlay_check_on_attributes_keeps_its_shape_when_their_columns_are_renamed()
    {
        // The snapshot's shape is the check lowered with column keys for names: an overlay entry that renames issued_on changes the
        // DDL's text, not the shape, so the migration renames the column and leaves the check alone.
        await using var r = EditorRepo.Create(packs: false);
        OverlayChecks(r, DueAfterIssue);
        var before = Invoices(await ResolveAsync(r)).Checks.Single(c => c.Name == "ck_invoices_due_after_issue");

        EditOnDisk(r, OverlayPath, t => t["columns"]!.AsArray().Add(JsonNode.Parse($$"""
            { "id": "01K9EXR0000000000000000W21", "attribute": "{{IssuedOnId}}", "name": "issue_date" }
            """)));
        var after = Invoices(await ResolveAsync(r)).Checks.Single(c => c.Name == "ck_invoices_due_after_issue");

        Assert.Equal(("issued_on <= due_on", "issue_date <= due_on"), (before.Expression, after.Expression));
        Assert.Equal(("issuedOn <= dueOn", before.Shape), (after.Condition!.Text, after.Shape));
        Assert.NotNull(after.Shape);
    }

    [Fact]
    public async Task An_overlay_check_on_an_attribute_its_table_does_not_store_is_MQ4008_and_left_out()
    {
        // The mapping ignores notes, so the projected table has no column for it: the check types (notes is an attribute of Invoice),
        // and resolution reports it at its condition rather than leaving it out of the DDL silently.
        await using var r = EditorRepo.Create(packs: false);
        EditOnDisk(r, ".maquettiste/model/mappings/invoice-in-main.json", m => m["attributes"]!.AsArray().Add(JsonNode.Parse($$"""
            { "attribute": "{{NotesId}}", "ignore": true }
            """)));
        OverlayChecks(r, DueAfterIssue, $$"""
            { "id": "01K9EXR0000000000000000W14", "name": "ck_invoices_notes",
              "condition": { "op": "isNotNull", "args": [{ "path": ["{{NotesId}}"] }] } }
            """);
        var diagnostics = (await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
        Assert.DoesNotContain(diagnostics, d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal) && d.FilePath == OverlayPath);
        var model = await ResolveAsync(r);

        var finding = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4008" && d.ElementId == EditorRepo.OverlayTableId);
        Assert.Equal("/checks/2/condition", finding.JsonPointer);
        Assert.Contains("reads 'notes', which the table does not store", finding.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => d.Rule == "MQ4008" && d.ElementId == EditorRepo.OverlayTableId && d.JsonPointer == "/checks/2/condition");
        Assert.Equal(["ck_invoice_status", "ck_invoices_due_after_issue"], Invoices(model).Checks.Select(c => c.Name));
    }

    [Fact]
    public async Task An_overlay_check_reads_an_inherited_attribute_of_its_entity()
    {
        // With table per concrete type, RecurringInvoice's own table holds the attributes it inherits from Invoice: its overlay's check
        // reads issuedOn, Invoice's, beside its own nextIssueOn.
        await using var r = EditorRepo.Create(packs: false);
        const string RecurringId = "01K9EXR0000000000000000W31", NextIssueOnId = "01K9EXR0000000000000000W32";
        const string RecurringPath = ".maquettiste/model/databases/main/tables/recurring-invoices.json";
        EditOnDisk(r, ".maquettiste/model/mappings/invoice-in-main.json", m => m["inheritance"] = "tpc");
        File.WriteAllText(r.Repo.PathOf(".maquettiste/model/entities/recurring-invoice.json"), $$"""
            { "kind": "entity", "id": "{{RecurringId}}", "name": "RecurringInvoice", "package": "{{EditorRepo.BillingPackageId}}",
              "base": "{{EditorRepo.InvoiceId}}", "attributes": [ { "id": "{{NextIssueOnId}}", "name": "nextIssueOn", "type": "date" } ] }
            """, new UTF8Encoding(false));
        File.WriteAllText(r.Repo.PathOf(RecurringPath), $$"""
            { "kind": "table", "id": "01K9EXR0000000000000000W33", "database": "{{EditorRepo.MainDatabaseId}}", "origin": "synthesized",
              "entity": "{{RecurringId}}",
              "checks": [ { "id": "01K9EXR0000000000000000W34", "name": "ck_recurring_next_after_issue",
                "condition": { "op": "lt", "args": [{ "path": ["{{IssuedOnId}}"] }, { "path": ["{{NextIssueOnId}}"] }] } } ] }
            """, new UTF8Encoding(false));
        Assert.DoesNotContain((await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics,
            d => d.FilePath == RecurringPath && d.Severity == DiagnosticSeverity.Error);
        var model = await ResolveAsync(r);

        var check = Assert.Single(model.Db("main").Tables.Single(t => t.Entity?.Id == RecurringId && t.Attribute is null).Checks);
        Assert.Equal(("issued_on < next_issue_on", "issuedOn < nextIssueOn"), (check.Expression, check.Condition!.Text));
    }

    /// <summary>Rewrites a model file of an editor repo on disk before the store reads it.</summary>
    internal static void EditOnDisk(EditorRepo r, string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(r.Repo.PathOf(path)))!.AsObject();
        change(node);
        File.WriteAllText(r.Repo.PathOf(path), node.ToJsonString(), new UTF8Encoding(false));
    }
}
