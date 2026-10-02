using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Planning;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Queries after the 2026-10-02 review: grouped collections and parents, MySQL casts, fields typed from their attributes, foreign
/// key and value object member fields, the rules MQ4032 to MQ4043, hidden key names, exists indentation, typed decimal literals,
/// blank sql texts and the <c>query_sql_parameters</c> helper.
/// </summary>
public sealed class QueryRulesTests
{
    /// <summary>
    /// Customer (id, required name, email, visits) placing Invoices (id, required number, total, an optional Money price), the
    /// navigations <c>customer</c> on Invoice and <c>invoices</c> on Customer, an abstract Party, in a database of the given dialect.
    /// </summary>
    private sealed class Store
    {
        public Store(Dialect dialect = Dialect.PostgreSql)
        {
            Builder = new ModelBuilder(seed: 96);
            Money = Builder.ValueObject("Money").Attr("amount", "decimal", a => a.Precision(12).Scale(2).Required()).Attr("currency", "string", a => a.Length(3).Required());
            Customer = Builder.Entity("Customer").Key("id", "uuid").Attr("name", "string", a => a.Length(100).Required()).Attr("email", "string", a => a.Length(200))
                .Attr("visits", "int32");
            Invoice = Builder.Entity("Invoice").Key("id", "uuid").Attr("number", "string", a => a.Length(20).Required()).Attr("total", "decimal", a => a.Precision(12).Scale(2))
                .Attr("price", Money);
            Party = Builder.Entity("Party").Abstract().Key("id", "uuid");
            Places = Builder.Relation("places", Customer, Invoice, fromMax: MaxCardinality.One, fromMin: 1, fromRole: "customer", toRole: "invoices",
                fromNavigation: "customer", toNavigation: "invoices");
            Main = Builder.Database("main", dialect).Id;
        }

        public ModelBuilder Builder { get; }

        public ValueObjectBuilder Money { get; }

        public EntityBuilder Customer { get; }

        public EntityBuilder Invoice { get; }

        public EntityBuilder Party { get; }

        public RelationBuilder Places { get; }

        public string Main { get; }

        public string Invoices => Invoice.Id + "@" + Main;

        public string Customers => Customer.Id + "@" + Main;

        public string Query(string json)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            var id = Builder.NewId();
            node["id"] = id;
            node["database"] ??= Main;
            Builder.Add(node.Deserialize<Query>(EngineJson.Options)!);
            return id;
        }

        public ResolvedModel Resolve() => ResolutionKit.Resolve(Builder);

        /// <summary>The (rule, pointer) findings of a query.</summary>
        public static List<(string Rule, string? Pointer)> Findings(ResolvedModel model, string id) =>
            [.. model.Diagnostics.Where(d => d.ElementId == id).Select(d => (d.Rule, d.JsonPointer))];
    }

    private static readonly string[] Dialects = ["postgresql", "sqlserver", "mysql", "sqlite", "oracle"];

    [Fact]
    public void A_grouped_collection_groups_by_its_keys_and_a_grouped_parent_by_its_hidden_keys()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "CustomerTotals",
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [
                { "name": "name", "expression": { "column": "c.name" } },
                { "name": "customers", "expression": { "call": "count" } }
              ],
              "groupBy": [{ "column": "c.name" }],
              "collections": [
                {
                  "attribute": "byNumber",
                  "query": {
                    "from": { "source": "{{s.Invoices}}", "alias": "i" },
                    "select": [
                      { "name": "number", "expression": { "column": "i.number" } },
                      { "name": "total", "expression": { "call": "sum", "args": [{ "column": "i.total" }] } }
                    ],
                    "where": { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } },
                    "groupBy": [{ "column": "i.number" }]
                  }
                },
                {
                  "attribute": "invoiceCount",
                  "query": {
                    "from": { "source": "{{s.Invoices}}", "alias": "i" },
                    "select": [{ "name": "count", "expression": { "call": "COUNT" } }],
                    "where": { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } }
                  }
                }
              ]
            }
            """);
        var model = s.Resolve();
        Assert.Empty(Store.Findings(model, id));
        var query = model.Db("main").Queries.Single();

        Assert.Equal("""
            SELECT c.name AS name, COUNT(*) AS customers, c.id AS mq_key0_0, c.id AS mq_key1_0
            FROM public.customers c
            GROUP BY c.name, c.id
            """, query.Sql);
        Assert.Equal("""
            SELECT i.number AS number, SUM(i.total) AS total, i.customer_id AS mq_key0
            FROM public.invoices i
            WHERE i.customer_id IN @mq_keys0
            GROUP BY i.number, i.customer_id
            """, QuerySql.RenderCollection(query.Collections[0]).Sql);
        Assert.Equal("""
            SELECT COUNT(*) AS count, i.customer_id AS mq_key0
            FROM public.invoices i
            WHERE i.customer_id IN @mq_keys0
            GROUP BY i.customer_id
            """, QuerySql.RenderCollection(query.Collections[1]).Sql);

        // Every dialect family groups the same way (quoting aside).
        foreach (var dialect in Dialects)
        {
            static string Unquoted(string sql) => sql.Replace("\"", "", StringComparison.Ordinal).Replace("[", "", StringComparison.Ordinal)
                .Replace("]", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
            Assert.EndsWith("GROUP BY c.name, c.id", Unquoted(QuerySql.Render(query, dialect).Sql), StringComparison.Ordinal);
            Assert.EndsWith("GROUP BY i.number, i.customer_id", Unquoted(QuerySql.RenderCollection(query.Collections[0], dialect).Sql), StringComparison.Ordinal);
            Assert.EndsWith("GROUP BY i.customer_id", Unquoted(QuerySql.RenderCollection(query.Collections[1], dialect).Sql), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_distinct_parent_must_select_or_group_its_collection_keys_MQ4032()
    {
        var s = new Store();
        string Query(string name, string select, string groupBy) => s.Query($$"""
            {
              "name": "{{name}}",
              "distinct": true,
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [{{select}}],
              "groupBy": [{{groupBy}}],
              "collections": [{
                "attribute": "numbers",
                "query": {
                  "from": { "source": "{{s.Invoices}}", "alias": "i" },
                  "select": [{ "name": "number", "expression": { "column": "i.number" } }],
                  "where": { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } }
                }
              }]
            }
            """);
        var hidden = Query("Hidden", """{ "name": "name", "expression": { "column": "c.name" } }""", "");
        var selected = Query("Selected", """{ "name": "name", "expression": { "column": "c.name" } }, { "name": "id", "expression": { "column": "c.id" } }""", "");
        var grouped = Query("Grouped", """{ "name": "name", "expression": { "column": "c.name" } }""", """{ "column": "c.name" }, { "column": "c.id" }""");
        var model = s.Resolve();

        Assert.Equal([("MQ4032", "/collections/0/query/where")], Store.Findings(model, hidden));
        Assert.Equal(DiagnosticSeverity.Error, model.Diagnostics.Single(d => d.ElementId == hidden).Severity);
        Assert.Empty(model.Db("main").Queries.Single(q => q.Id == hidden).Sql);
        Assert.Empty(Store.Findings(model, selected));
        Assert.Empty(Store.Findings(model, grouped));
    }

    [Fact]
    public void A_cast_on_MySQL_takes_a_type_its_cast_accepts()
    {
        var s = new Store(Dialect.MySql);
        var id = s.Query($$"""
            {
              "name": "Casts",
              "from": { "source": "{{s.Invoices}}", "alias": "i" },
              "select": [
                { "name": "a", "expression": { "cast": { "column": "i.number" }, "type": "string" } },
                { "name": "b", "expression": { "cast": { "column": "i.total" }, "type": "int64" } },
                { "name": "c", "expression": { "cast": { "column": "i.number" }, "type": "decimal" } },
                { "name": "d", "expression": { "cast": { "column": "i.number" }, "type": "datetimeOffset" } },
                { "name": "e", "expression": { "cast": { "column": "i.total" }, "type": "float" } },
                { "name": "f", "expression": { "cast": { "column": "i.id" }, "type": "uuid" } },
                { "name": "g", "expression": { "cast": { "column": "i.number" }, "type": "bool" } },
                { "name": "h", "expression": { "cast": { "column": "i.number" }, "type": "json" } }
              ]
            }
            """);
        var model = s.Resolve();
        Assert.Empty(Store.Findings(model, id));
        var query = model.Db("main").Queries.Single();
        var decimalType = query.Select[2].NativeType!;

        Assert.StartsWith("DECIMAL(", decimalType, StringComparison.Ordinal);
        Assert.Equal(
            $"SELECT CAST(i.number AS CHAR) AS a, CAST(i.total AS SIGNED) AS b, CAST(i.number AS {decimalType}) AS c, CAST(i.number AS DATETIME) AS d, "
            + "CAST(i.total AS DOUBLE) AS e, CAST(i.id AS CHAR) AS f, CAST(i.number AS SIGNED) AS g, CAST(i.number AS JSON) AS h\nFROM invoices i",
            query.Sql.Replace("main.", "", StringComparison.Ordinal));
        Assert.Equal(["CHAR", "SIGNED", decimalType, "DATETIME", "DOUBLE", "CHAR", "SIGNED", "JSON"], query.Select.Select(f => f.NativeType));

        // The same query rendered for MySQL from another dialect's database takes the table too.
        var pg = new Store();
        pg.Query($$"""{ "name": "Cast", "from": { "source": "{{pg.Invoices}}", "alias": "i" }, "select": [{ "name": "a", "expression": { "cast": { "column": "i.total" }, "type": "int32" } }] }""");
        var other = pg.Resolve().Db("main").Queries.Single();
        Assert.StartsWith("SELECT CAST(i.total AS SIGNED) AS a", QuerySql.Render(other, "mysql").Sql, StringComparison.Ordinal);
        Assert.StartsWith("SELECT CAST(i.total AS integer) AS a", other.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_that_fills_an_attribute_takes_its_type_and_warns_when_it_cannot_convert_MQ4033()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "Typed",
              "entity": "{{s.Customer.Id}}",
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [
                { "attribute": "{{s.Customer.AttrId("id")}}", "expression": { "column": "c.id" } },
                { "attribute": "{{s.Customer.AttrId("name")}}", "expression": { "call": "initcap", "args": [{ "column": "c.name" }] } },
                { "attribute": "{{s.Customer.AttrId("visits")}}", "expression": { "call": "count" } },
                { "attribute": "{{s.Customer.AttrId("email")}}", "expression": { "sql": { "*": "c.email" } } }
              ],
              "groupBy": [{ "column": "c.id" }]
            }
            """);
        var wrong = s.Query($$"""
            {
              "name": "Wrong",
              "entity": "{{s.Customer.Id}}",
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [
                { "attribute": "{{s.Customer.AttrId("id")}}", "expression": { "column": "c.id" } },
                { "attribute": "{{s.Customer.AttrId("name")}}", "expression": { "column": "c.name" } },
                { "attribute": "{{s.Customer.AttrId("visits")}}", "expression": { "column": "c.name" } }
              ]
            }
            """);
        var model = s.Resolve();
        var typed = model.Db("main").Queries.Single(q => q.Id == id);

        Assert.Empty(Store.Findings(model, id));
        Assert.Equal(("string", "string"), (typed.Select[1].Type, typed.Select[1].CodeType)); // an unknown function: the attribute's type
        Assert.NotNull(typed.Select[1].NativeType);
        Assert.Equal("int64", typed.Select[2].CodeType); // count into an int32: converted in code
        Assert.Equal("string", typed.Select[3].CodeType); // an sql text: the attribute's type
        Assert.Equal([("MQ4033", "/select/2/expression")], Store.Findings(model, wrong));
        Assert.Equal(DiagnosticSeverity.Warning, model.Diagnostics.Single(d => d.ElementId == wrong).Severity);
        Assert.NotEmpty(model.Db("main").Queries.Single(q => q.Id == wrong).Sql);
    }

    [Fact]
    public void A_field_fills_a_foreign_key_by_its_relation_end_and_a_value_object_member_by_its_path()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "Invoices",
              "entity": "{{s.Invoice.Id}}",
              "from": { "source": "{{s.Invoices}}", "alias": "i" },
              "select": [
                { "attribute": "{{s.Invoice.AttrId("id")}}", "expression": { "column": "i.id" } },
                { "attribute": "{{s.Invoice.AttrId("number")}}", "expression": { "column": "i.number" } },
                { "attribute": "{{s.Places.EndIds[0]}}", "expression": { "column": "i.customer_id" } },
                { "attribute": "{{s.Invoice.AttrId("price")}}.{{s.Money.AttrId("amount")}}", "expression": { "column": "i.{{s.Invoice.AttrId("price")}}.{{s.Money.AttrId("amount")}}" } },
                { "attribute": "{{s.Invoice.AttrId("price")}}.{{s.Money.AttrId("currency")}}", "expression": { "column": "i.price_currency" } }
              ]
            }
            """);
        var bad = s.Query($$"""
            {
              "name": "Bad",
              "entity": "{{s.Invoice.Id}}",
              "from": { "source": "{{s.Invoices}}", "alias": "i" },
              "select": [
                { "attribute": "{{s.Invoice.AttrId("id")}}", "expression": { "column": "i.id" } },
                { "attribute": "{{s.Invoice.AttrId("number")}}", "expression": { "column": "i.number" } },
                { "attribute": "{{s.Invoice.AttrId("number")}}.{{s.Money.AttrId("amount")}}", "expression": { "column": "i.number" } },
                { "attribute": "{{s.Places.EndIds[1]}}", "expression": { "column": "i.id" } }
              ]
            }
            """);
        var model = s.Resolve();
        Assert.Empty(Store.Findings(model, id));
        var fields = model.Db("main").Queries.Single(q => q.Id == id).Select;

        Assert.Equal(["id", "number", "customerId", "priceAmount", "priceCurrency"], fields.Select(f => f.Name));
        Assert.Equal(("customer", "customer_id"), (fields[2].Navigation!.Name, fields[2].ForeignKeyColumn!.Name));
        Assert.Null(fields[2].Attribute);
        Assert.Equal(("price", "amount"), (fields[3].Attribute!.Name, fields[3].Member!.Name));
        Assert.Equal("decimal", fields[3].Type);
        var view = DatabaseViews.From(model.Db("main")).Queries.Single(q => q.Id == id);
        Assert.Equal(s.Places.EndIds[0], view.Select[2].AttributeId);
        Assert.Equal(s.Invoice.AttrId("price") + "." + s.Money.AttrId("amount"), view.Select[3].AttributeId);

        Assert.Equal([("MQ4025", "/select/2/attribute"), ("MQ4025", "/select/3/attribute")], Store.Findings(model, bad).OrderBy(f => f.Pointer, StringComparer.Ordinal));
    }

    [Fact]
    public void A_call_names_a_function_or_a_routine_MQ4034()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "Calls",
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [
                { "name": "a", "expression": { "call": "lower(c.name)); DROP TABLE customers; --", "args": [{ "column": "c.name" }] } },
                { "name": "b", "expression": { "call": "pg_catalog.lower", "args": [{ "column": "c.name" }] } },
                { "name": "c", "expression": { "call": "a.b.c" } }
              ]
            }
            """);
        var model = s.Resolve();

        Assert.Equal([("MQ4034", "/select/0/expression/call"), ("MQ4034", "/select/2/expression/call")], Store.Findings(model, id).OrderBy(f => f.Pointer, StringComparer.Ordinal));
        Assert.Empty(model.Db("main").Queries.Single().Sql);
    }

    [Fact]
    public void A_join_fits_its_kind_and_the_dialect_MQ4035_MQ4036_MQ4037()
    {
        string Joins(Store s) => s.Query($$"""
            {
              "name": "Joins",
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "joins": [
                { "source": "{{s.Invoices}}", "alias": "a" },
                { "source": "{{s.Invoices}}", "alias": "b", "kind": "cross", "on": { "op": "eq", "left": { "column": "b.id" }, "right": { "column": "c.id" } } },
                { "source": "{{s.Invoices}}", "alias": "f", "kind": "full", "on": { "op": "eq", "left": { "column": "f.customer_id" }, "right": { "column": "c.id" } } },
                { "source": "{{s.Invoices}}", "alias": "r", "kind": "right", "on": { "op": "eq", "left": { "column": "r.customer_id" }, "right": { "column": "c.id" } } }
              ],
              "select": [{ "name": "name", "expression": { "column": "c.name" } }]
            }
            """);
        var pg = new Store();
        var id = Joins(pg);
        Assert.Equal([("MQ4035", "/joins/0"), ("MQ4035", "/joins/1/on")], Store.Findings(pg.Resolve(), id).OrderBy(f => f.Pointer, StringComparer.Ordinal));

        var mysql = new Store(Dialect.MySql);
        id = Joins(mysql);
        Assert.Contains(("MQ4036", "/joins/2/kind"), Store.Findings(mysql.Resolve(), id));
        Assert.DoesNotContain(Store.Findings(mysql.Resolve(), id), f => f is ("MQ4036", "/joins/3/kind"));

        // On SQLite a right or full join is a warning (SQLite 3.39 runs them), and the query still renders.
        var sqlite = new Store(Dialect.Sqlite);
        var ok = sqlite.Query($$"""
            {
              "name": "Right",
              "from": { "source": "{{sqlite.Customers}}", "alias": "c" },
              "joins": [{ "source": "{{sqlite.Invoices}}", "alias": "r", "kind": "right", "on": { "op": "eq", "left": { "column": "r.customer_id" }, "right": { "column": "c.id" } } }],
              "select": [{ "name": "name", "expression": { "column": "c.name" } }]
            }
            """);
        var model = sqlite.Resolve();
        Assert.Equal([("MQ4037", "/joins/0/kind")], Store.Findings(model, ok));
        Assert.Equal(DiagnosticSeverity.Warning, model.Diagnostics.Single(d => d.ElementId == ok).Severity);
        Assert.Contains("RIGHT JOIN invoices r ON", model.Db("main").Queries.Single().Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_distinct_query_orders_by_selected_expressions_only_MQ4038()
    {
        string Distinct(Store s, string order) => s.Query($$"""
            {
              "name": "Distinct",
              "distinct": true,
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [{ "name": "name", "expression": { "column": "c.name" } }],
              "orderBy": [{{order}}]
            }
            """);
        var pg = new Store();
        var bad = Distinct(pg, """{ "expression": { "column": "c.email" } }""");
        var model = pg.Resolve();
        Assert.Equal([("MQ4038", "/orderBy/0/expression")], Store.Findings(model, bad));

        var good = new Store();
        var id = Distinct(good, """{ "expression": { "column": "c.name" }, "nulls": "last" }""");
        Assert.Empty(Store.Findings(good.Resolve(), id));

        var sqlServer = new Store(Dialect.SqlServer);
        id = Distinct(sqlServer, """{ "expression": { "column": "c.name" }, "nulls": "last" }""");
        Assert.Equal([("MQ4038", "/orderBy/0/nulls")], Store.Findings(sqlServer.Resolve(), id));
    }

    [Fact]
    public void A_list_parameter_is_only_the_whole_right_side_of_in_MQ4039()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "Lists",
              "parameters": [{ "name": "ids", "type": "uuid", "collection": true }, { "name": "one", "type": "uuid" }],
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [
                { "name": "name", "expression": { "column": "c.name" } },
                { "name": "ids", "expression": { "call": "coalesce", "args": [{ "param": "ids" }] } }
              ],
              "where": { "and": [
                { "op": "in", "left": { "column": "c.id" }, "right": { "param": "ids" } },
                { "op": "eq", "left": { "column": "c.id" }, "right": { "param": "ids" } },
                { "op": "notIn", "left": { "column": "c.id" }, "right": [{ "param": "one" }, { "param": "ids" }] },
                { "op": "between", "left": { "column": "c.id" }, "right": [{ "param": "ids" }, { "param": "one" }] }
              ] }
            }
            """);
        var findings = Store.Findings(s.Resolve(), id);

        Assert.Equal(
            [
                ("MQ4039", "/select/1/expression/args/0/param"), ("MQ4039", "/where/and/1/right/param"), ("MQ4039", "/where/and/2/right/1/param"),
                ("MQ4039", "/where/and/3/right/0/param"),
            ],
            findings.OrderBy(f => f.Pointer, StringComparer.Ordinal));
    }

    [Fact]
    public void Names_that_collide_in_generated_code_or_start_with_mq_are_MQ4040_and_parameters_are_unique_ignoring_case()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "Names",
              "parameters": [{ "name": "word", "type": "string" }, { "name": "Word", "type": "string" }, { "name": "mq_keys0", "type": "string" }],
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [
                { "name": "issued_on", "expression": { "column": "c.name" } },
                { "name": "issuedOn", "expression": { "column": "c.email" } },
                { "name": "mq_key0_0", "expression": { "column": "c.id" } }
              ],
              "collections": [
                { "attribute": "item", "query": { "from": { "source": "{{s.Invoices}}", "alias": "i" }, "select": [{ "name": "n", "expression": { "column": "i.number" } }], "where": { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } } } },
                { "attribute": "lines", "query": { "from": { "source": "{{s.Invoices}}", "alias": "i" }, "select": [{ "name": "n", "expression": { "column": "i.number" } }], "where": { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } } } },
                { "attribute": "Lines", "query": { "from": { "source": "{{s.Invoices}}", "alias": "i" }, "select": [{ "name": "n", "expression": { "column": "i.number" } }], "where": { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } } } }
              ]
            }
            """);
        var first = s.Query($$"""{ "name": "open invoices", "from": { "source": "{{s.Invoices}}" }, "select": [{ "name": "n", "expression": { "value": 1 } }] }""");
        var second = s.Query($$"""{ "name": "OpenInvoices", "from": { "source": "{{s.Invoices}}" }, "select": [{ "name": "n", "expression": { "value": 1 } }] }""");
        var digits = s.Query($$"""{ "name": "2024 report", "from": { "source": "{{s.Invoices}}" }, "select": [{ "name": "n", "expression": { "value": 1 } }] }""");
        var model = s.Resolve();

        Assert.Equal(
            [
                ("MQ3001", "/parameters/1/name"), ("MQ4040", "/collections/0/attribute"), ("MQ4040", "/collections/2/attribute"),
                ("MQ4040", "/parameters/2/name"), ("MQ4040", "/select/1/name"), ("MQ4040", "/select/2/name"),
            ],
            Store.Findings(model, id).OrderBy(f => f.Rule, StringComparer.Ordinal).ThenBy(f => f.Pointer, StringComparer.Ordinal));
        Assert.NotEmpty(model.Db("main").Queries.Single(q => q.Id == id).Sql); // names do not stop the SQL
        Assert.Equal([("MQ4040", "/name")], Store.Findings(model, first).Concat(Store.Findings(model, second)));
        Assert.Empty(Store.Findings(model, digits));
        Assert.Equal("Q2024Report", DatabaseRun.QueryClassName("2024 report"));
    }

    [Fact]
    public void An_abstract_result_or_element_entity_is_MQ4041()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "Parties",
              "entity": "{{s.Party.Id}}",
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [{ "attribute": "{{s.Party.AttrId("id")}}", "expression": { "column": "c.id" } }],
              "collections": [{ "attribute": "others", "entity": "{{s.Party.Id}}", "query": { "from": { "source": "{{s.Invoices}}", "alias": "i" }, "select": [{ "attribute": "{{s.Party.AttrId("id")}}", "expression": { "column": "i.id" } }], "where": { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } } } }]
            }
            """);
        var findings = Store.Findings(s.Resolve(), id);

        Assert.Contains(("MQ4041", "/entity"), findings);
        Assert.Contains(("MQ4041", "/collections/0/entity"), findings);
    }

    [Fact]
    public void Operations_literals_and_defaults_are_checked_MQ4042_MQ4043()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "Values",
              "parameters": [
                { "name": "count", "type": "int32", "default": "ten" },
                { "name": "small", "type": "int16", "default": 70000 },
                { "name": "big", "type": "double", "default": 1e400 },
                { "name": "rate", "type": "decimal", "default": 0.5 },
                { "name": "key", "type": "uuid", "default": "not-a-uuid" }
              ],
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [
                { "name": "a", "expression": { "op": "+", "args": [] } },
                { "name": "b", "expression": { "op": "*", "args": [{ "value": 2 }] } },
                { "name": "c", "expression": { "op": "-", "args": [{ "column": "c.visits" }] } },
                { "name": "d", "expression": { "value": 1e400 } },
                { "name": "e", "expression": { "value": "2.x", "type": "decimal" } },
                { "name": "f", "expression": { "value": "2.0", "type": "int32" } }
              ]
            }
            """);
        var model = s.Resolve();

        Assert.Equal(
            [
                ("MQ4042", "/select/0/expression/args"), ("MQ4042", "/select/1/expression/args"),
                ("MQ4043", "/parameters/0/default"), ("MQ4043", "/parameters/1/default"), ("MQ4043", "/parameters/2/default"),
                ("MQ4043", "/parameters/4/default"), ("MQ4043", "/select/3/expression/value"), ("MQ4043", "/select/4/expression/value"),
                ("MQ4043", "/select/5/expression/type"),
            ],
            Store.Findings(model, id).OrderBy(f => f.Rule, StringComparer.Ordinal).ThenBy(f => f.Pointer, StringComparer.Ordinal));
        Assert.Equal(0.5m, model.Db("main").Queries.Single().Parameters[3].Default);
    }

    [Fact]
    public void A_typed_decimal_literal_keeps_its_digits_and_a_blank_sql_text_is_none()
    {
        var s = new Store();
        var id = s.Query($$"""
            {
              "name": "Literals",
              "from": { "source": "{{s.Invoices}}", "alias": "i" },
              "select": [
                { "name": "a", "expression": { "op": "*", "args": [{ "column": "i.total" }, { "value": "2.0", "type": "decimal" }] } },
                { "name": "b", "expression": { "value": "-007.50", "type": "decimal" } },
                { "name": "c", "expression": { "value": "-0.00", "type": "decimal" } },
                { "name": "d", "expression": { "sql": { "postgresql": "  ", "*": "1" } } }
              ]
            }
            """);
        var blank = s.Query($$"""{ "name": "Blank", "from": { "source": "{{s.Invoices}}", "alias": "i" }, "select": [{ "name": "a", "expression": { "sql": { "postgresql": " " } } }] }""");
        var model = s.Resolve();

        Assert.Empty(Store.Findings(model, id));
        var query = model.Db("main").Queries.Single(q => q.Id == id);
        Assert.Equal("SELECT (i.total * 2.0) AS a, -7.50 AS b, 0.00 AS c, 1 AS d\nFROM public.invoices i", query.Sql);
        Assert.Equal(("decimal", 2.0m), (query.Select[0].Expression.Args[1].Type, (decimal)query.Select[0].Expression.Args[1].Value!));
        Assert.Equal([("MQ4029", "/select/0/expression/sql")], Store.Findings(model, blank));
    }

    [Fact]
    public void An_exists_indents_its_clauses_and_leaves_literal_text_alone_and_hidden_names_are_quoted()
    {
        var s = new Store(Dialect.Oracle);
        var id = s.Query($$"""
            {
              "name": "Exists",
              "from": { "source": "{{s.Customers}}", "alias": "c" },
              "select": [{ "name": "_name", "expression": { "column": "c.name" } }],
              "where": { "exists": {
                "from": { "source": "{{s.Invoices}}", "alias": "i" },
                "where": { "and": [
                  { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } },
                  { "op": "ne", "left": { "column": "i.number" }, "right": { "value": "two\nlines" } },
                  { "exists": { "from": { "source": "{{s.Invoices}}", "alias": "j" }, "where": { "op": "eq", "left": { "column": "j.id" }, "right": { "column": "i.id" } } } }
                ] }
              } }
            }
            """);
        var model = s.Resolve();
        Assert.Empty(Store.Findings(model, id));

        Assert.Equal(
            "SELECT c.name AS \"_name\"\nFROM customers c\nWHERE EXISTS (SELECT 1\n    FROM invoices i\n"
            + "    WHERE i.customer_id = c.id AND i.\"number\" <> 'two\nlines' AND EXISTS (SELECT 1\n        FROM invoices j\n        WHERE j.id = i.id))",
            model.Db("main").Queries.Single().Sql);
    }

    [Fact]
    public async Task Query_sql_parameters_lists_the_names_in_placeholder_order()
    {
        var model = await BillingModel.GetAsync();
        var byCustomer = model.Databases.Single().Queries.Single(q => q.Name == "InvoicesByCustomer");

        var text = (await Adhoc.RenderAsync(
            "{{ query_sql_parameters query | array.join ',' }}|{{ query_sql_parameters query 'sqlserver' | array.join ',' }}|{{ query_sql_parameters query { placeholder: '$' } | array.join ',' }}|{{ query_sql_parameters query.collections[0] | array.join ',' }}",
            byCustomer, u => u with { For = "each query" })).Text();

        Assert.Equal("customerId,statuses,offset,limit|customerId,statuses,offset,limit|customerId,statuses,offset,limit|mq_keys0", text);
        Assert.Equal(["customerId", "statuses", "offset", "limit"], QuerySql.Parameters(byCustomer));
        Assert.Equal("MQ6006", (await Adhoc.RenderAsync("{{ query_sql_parameters model }}")).Error().Rule);
    }
}
