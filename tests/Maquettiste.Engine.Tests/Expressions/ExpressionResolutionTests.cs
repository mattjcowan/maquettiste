using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;
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
        repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/audited.json", """
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
}
