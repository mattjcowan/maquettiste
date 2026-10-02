using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Planning;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Queries (added 2026-10-02, erratum E41): a query file resolved over the database's tables and views, its trees, types and
/// collections, the SQL renderer per dialect, the resolver's rules MQ4021 to MQ4031 with their pointers, the <c>each query</c> scope
/// and the <c>query_sql</c> helper, the database view and resolved records, and what the schema diff makes of them (nothing).
/// </summary>
public sealed class QueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A PostgreSQL database <c>main</c> over Customer (id, required name, email) and Invoice (id, required number, total), a customer
    /// placing invoices (the navigation <c>invoices</c> on Customer), a view of the invoices and a function <c>tax_of</c>.
    /// </summary>
    private sealed class Shop
    {
        public Shop(Dialect dialect = Dialect.PostgreSql)
        {
            Builder = new ModelBuilder(seed: 95);
            Customer = Builder.Entity("Customer").Key("id", "uuid").Attr("name", "string", a => a.Length(100).Required()).Attr("email", "string", a => a.Length(200));
            Invoice = Builder.Entity("Invoice").Key("id", "uuid").Attr("number", "string", a => a.Length(20).Required()).Attr("total", "decimal", a => a.Precision(12).Scale(2));
            Places = Builder.Relation("places", Customer, Invoice, fromMax: MaxCardinality.One, fromMin: 1, fromRole: "customer", toRole: "invoices",
                fromNavigation: "customer", toNavigation: "invoices");
            Main = Builder.Database("main", dialect).Id;
            View = Builder.NewId();
            Builder.Add(new View
            {
                Id = View, Name = "open_invoices", Database = Main, Body = new Dictionary<string, string> { ["*"] = "select id, number from invoices" },
                Columns = [new ViewColumn { Name = "id", Type = "uuid", Nullable = false }, new ViewColumn { Name = "number", Type = "string" }],
            });
            Routine = Builder.NewId();
            Builder.Add(new Routine
            {
                Id = Routine, Name = "tax_of", Database = Main, Parameters = [new RoutineParameter { Name = "amount", Type = "decimal" }],
                Returns = new RoutineReturns { Type = "decimal", Precision = 12, Scale = 2 }, Body = new Dictionary<string, string> { ["*"] = "select amount * 0.2" },
            });
        }

        public ModelBuilder Builder { get; }

        public EntityBuilder Customer { get; }

        public EntityBuilder Invoice { get; }

        public RelationBuilder Places { get; }

        public string Main { get; }

        public string View { get; }

        public string Routine { get; }

        public string InvoiceTable => Invoice.Id + "@" + Main;

        /// <summary>Adds a query from its JSON (without kind, id and database, which are filled in) and returns its id.</summary>
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
    }

    /// <summary>The customer report the SQL tests render: a left join, functions, an operator, a filter, grouping, ordering and paging.</summary>
    private static string Report(Shop s) => s.Query($$"""
        {
          "name": "CustomerReport",
          "entity": "{{s.Customer.Id}}",
          "parameters": [
            { "name": "pattern", "type": "string", "length": 100 },
            { "name": "ids", "type": "uuid", "collection": true },
            { "name": "take", "type": "int32", "default": 20 }
          ],
          "from": { "source": "{{s.Customer.Id}}", "alias": "c" },
          "joins": [
            { "source": "{{s.InvoiceTable}}", "alias": "order", "kind": "left", "on": { "op": "eq", "left": { "column": "order.customer_id" }, "right": { "column": "c.id" } } }
          ],
          "select": [
            { "attribute": "{{s.Customer.AttrId("id")}}", "expression": { "column": "c.{{s.Customer.AttrId("id")}}" } },
            { "attribute": "{{s.Customer.AttrId("name")}}", "expression": { "call": "upper", "args": [{ "column": "c.name" }] } },
            { "name": "lastNumber", "expression": { "call": "max", "args": [{ "column": "order.number" }] } },
            { "name": "label", "expression": { "op": "concat", "args": [{ "column": "c.name" }, { "value": " <" }, { "column": "c.email" }, { "value": ">" }] } },
            { "name": "nameLength", "expression": { "call": "length", "args": [{ "column": "c.name" }] } }
          ],
          "where": { "and": [
            { "op": "ilike", "left": { "column": "c.name" }, "right": { "param": "pattern" } },
            { "op": "in", "left": { "column": "c.id" }, "right": { "param": "ids" } },
            { "not": { "op": "isNull", "left": { "column": "c.email" } } }
          ] },
          "groupBy": [{ "column": "c.id" }, { "column": "c.name" }, { "column": "c.email" }],
          "having": { "op": "gt", "left": { "call": "count" }, "right": { "value": 0 } },
          "orderBy": [{ "expression": { "column": "c.name" }, "direction": "desc", "nulls": "last" }],
          "paging": { "offset": 10, "limit": "take" }
        }
        """);

    /// <summary>A customer's invoices as a collection, correlated on the invoice's customer column, which the parent does not select.</summary>
    private static string WithInvoices(Shop s) => s.Query($$"""
        {
          "name": "CustomersWithInvoices",
          "entity": "{{s.Customer.Id}}",
          "from": { "source": "{{s.Customer.Id}}", "alias": "c" },
          "select": [
            { "attribute": "{{s.Customer.AttrId("name")}}", "expression": { "column": "c.name" } },
            { "attribute": "{{s.Customer.AttrId("id")}}", "expression": { "column": "c.id" } }
          ],
          "collections": [
            {
              "attribute": "{{s.Places.EndIds[1]}}",
              "query": {
                "from": { "source": "{{s.InvoiceTable}}", "alias": "i" },
                "select": [
                  { "attribute": "{{s.Invoice.AttrId("id")}}", "expression": { "column": "i.id" } },
                  { "attribute": "{{s.Invoice.AttrId("number")}}", "expression": { "column": "i.number" } }
                ],
                "where": { "and": [
                  { "op": "eq", "left": { "column": "c.id" }, "right": { "column": "i.customer_id" } },
                  { "op": "gt", "left": { "column": "i.total" }, "right": { "call": "{{s.Routine}}", "args": [{ "value": 10 }] } }
                ] },
                "orderBy": [{ "expression": { "column": "i.number" } }]
              }
            }
          ]
        }
        """);

    [Fact]
    public void A_query_resolves_its_sources_columns_parameters_types_and_dependencies()
    {
        var s = new Shop();
        var id = Report(s);
        var model = s.Resolve();
        var db = model.Db("main");

        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == id);
        var query = Assert.Single(db.Queries);
        Assert.Same(query, model.Find(id));
        Assert.Equal("query", query.Kind);
        Assert.Same(model.Entity("Customer"), query.Entity);
        Assert.Same(db.Table("customers"), query.From.Table);
        Assert.Equal(("c", "from"), (query.From.Alias, query.From.JoinKind));
        var join = Assert.Single(query.Joins);
        Assert.Equal(("order", "left", "invoices", "public"), (join.Alias, join.JoinKind, join.Name, join.Schema));
        Assert.True(join.Optional);
        Assert.Equal("compare", join.On!.Node);
        Assert.True(join.On.Right!.Node == "column" && join.On.Right.ColumnName == "id" && !join.On.Right.IsOuter);

        // Parameters: native types for the dialect, the keyword a code map maps, the default as a plain value.
        Assert.Equal(["varchar(100)", "uuid", "integer"], query.Parameters.Select(p => p.NativeType));
        Assert.True(query.Parameters[1].Collection);
        Assert.Equal(20L, query.Parameters[2].Default);
        Assert.Same(query.Parameters[2], query.Paging!.LimitParameter);
        Assert.Equal(10L, query.Paging.Offset);

        // The column part may be the column key (the attribute id here) or the physical name.
        var fields = query.Select;
        Assert.Equal(["id", "name", "lastNumber", "label", "nameLength"], fields.Select(f => f.Name));
        Assert.Same(db.Table("customers").Column("id"), fields[0].Expression.TableColumn);
        Assert.Same(model.Entity("Customer").Attributes.Single(a => a.Name == "name"), fields[1].Attribute);
        Assert.Equal(("uuid", "uuid", false), (fields[0].Type, fields[0].NativeType, fields[0].Nullable));
        Assert.Equal(("string", true), (fields[2].Type, fields[2].Nullable)); // max over the outer side of a left join
        Assert.Equal(("string", true), (fields[3].Type, fields[3].Nullable)); // concat of a nullable email
        Assert.Equal(("int32", false), (fields[4].Type, fields[4].Nullable));
        Assert.Equal("call", fields[1].Expression.Node);
        Assert.Equal("ilike", query.Where!.And[0].Op);
        Assert.Same(query.Parameters[1], query.Where.And[1].Right!.Param);
        Assert.Equal("not", query.Where.And[2].Node);

        // Dependencies: the query's file, the tables it reads and what they come from; the database's list counts queries.
        Assert.Equal([db.Table("customers"), db.Table("invoices")], query.Uses);
        Assert.Contains("e:" + id, query.Dependencies);
        Assert.Contains("e:" + s.Invoice.Id, query.Dependencies);
        Assert.Contains("k:query", db.Queries.MembershipKeys);
        Assert.NotEmpty(query.Sql);
    }

    [Fact]
    public void The_sql_follows_each_dialect_with_its_quoting_functions_ilike_nulls_and_paging()
    {
        var s = new Shop();
        Report(s);
        var query = Assert.Single(s.Resolve().Db("main").Queries);

        Assert.Equal("""
            SELECT c.id AS id, UPPER(c.name) AS name, MAX("order".number) AS lastNumber, (c.name || ' <' || c.email || '>') AS label, LENGTH(c.name) AS nameLength
            FROM public.customers c
            LEFT JOIN public.invoices "order" ON "order".customer_id = c.id
            WHERE c.name ILIKE @pattern AND c.id IN @ids AND NOT (c.email IS NULL)
            GROUP BY c.id, c.name, c.email
            HAVING COUNT(*) > 0
            ORDER BY c.name DESC NULLS LAST
            LIMIT @take OFFSET 10
            """, QuerySql.Render(query).Sql);
        Assert.Equal(query.Sql, QuerySql.Render(query, "postgresql").Sql);

        Assert.Equal("""
            SELECT c.id AS id, UPPER(c.name) AS name, MAX([order].number) AS lastNumber, CONCAT(c.name, N' <', c.email, N'>') AS label, LEN(c.name) AS nameLength
            FROM [public].customers c
            LEFT JOIN [public].invoices [order] ON [order].customer_id = c.id
            WHERE LOWER(c.name) LIKE LOWER(@pattern) AND c.id IN @ids AND NOT (c.email IS NULL)
            GROUP BY c.id, c.name, c.email
            HAVING COUNT(*) > 0
            ORDER BY CASE WHEN c.name IS NULL THEN 1 ELSE 0 END, c.name DESC
            OFFSET 10 ROWS FETCH NEXT @take ROWS ONLY
            """, QuerySql.Render(query, "sqlserver").Sql);

        Assert.Equal("""
            SELECT c.id AS id, UPPER(c.name) AS name, MAX(`order`.number) AS lastNumber, CONCAT(c.name, ' <', c.email, '>') AS label, CHAR_LENGTH(c.name) AS nameLength
            FROM public.customers c
            LEFT JOIN public.invoices `order` ON `order`.customer_id = c.id
            WHERE LOWER(c.name) LIKE LOWER(@pattern) AND c.id IN @ids AND NOT (c.email IS NULL)
            GROUP BY c.id, c.name, c.email
            HAVING COUNT(*) > 0
            ORDER BY CASE WHEN c.name IS NULL THEN 1 ELSE 0 END, c.name DESC
            LIMIT @take OFFSET 10
            """, QuerySql.Render(query, "mysql").Sql);

        // SQLite has no schemas; Oracle pages with OFFSET … FETCH and takes :name placeholders.
        var sqlite = QuerySql.Render(query, "sqlite").Sql;
        Assert.Contains("FROM customers c\nLEFT JOIN invoices \"order\" ON", sqlite, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY c.name DESC NULLS LAST\nLIMIT @take OFFSET 10", sqlite, StringComparison.Ordinal);
        var oracle = QuerySql.Render(query, "oracle", new QuerySqlOptions { Placeholder = ":" });
        Assert.EndsWith("OFFSET 10 ROWS\nFETCH NEXT :take ROWS ONLY", oracle.Sql, StringComparison.Ordinal);
        Assert.Contains("LOWER(c.name) LIKE LOWER(:pattern)", oracle.Sql, StringComparison.Ordinal);

        // Positional placeholders are numbered by first appearance; a list may be an array on PostgreSQL.
        var positional = QuerySql.Render(query, null, new QuerySqlOptions { Placeholder = "$", Lists = "any" });
        Assert.Equal(["pattern", "ids", "take"], positional.Parameters);
        Assert.Contains("c.name ILIKE $1 AND c.id = ANY($2)", positional.Sql, StringComparison.Ordinal);
        Assert.EndsWith("LIMIT $3 OFFSET 10", positional.Sql, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => QuerySql.Render(query, "cobol"));
        Assert.Throws<ArgumentException>(() => QuerySql.Render(query, null, new QuerySqlOptions { Placeholder = "?" }));
    }

    [Fact]
    public void A_collection_is_a_second_statement_keyed_by_its_correlation()
    {
        var s = new Shop();
        WithInvoices(s);
        var model = s.Resolve();
        var query = Assert.Single(model.Db("main").Queries);

        var collection = Assert.Single(query.Collections);
        Assert.Equal("invoices", collection.Name);
        Assert.True(collection.Navigation!.IsCollection);
        Assert.Same(model.Entity("Invoice"), collection.Entity); // the navigation's target when the file names none
        Assert.Same(query, collection.Query.Parent);
        var key = Assert.Single(collection.Keys);
        Assert.Equal(("id", false, "__key0", "__keys0", "uuid"), (key.ParentField, key.Hidden, key.ChildField, key.Parameter, key.Type));
        Assert.True(key.Outer.IsOuter);
        Assert.Equal("customer_id", key.Inner.ColumnName);
        Assert.Same(model.Db("main").Routines.Single(), collection.Query.Where!.And[1].Right!.Routine);

        Assert.Equal("""
            SELECT c.name AS name, c.id AS id
            FROM public.customers c
            """, QuerySql.Render(query).Sql);
        var statement = QuerySql.RenderCollection(collection);
        Assert.Equal("""
            SELECT i.id AS id, i.number AS number, i.customer_id AS __key0
            FROM public.invoices i
            WHERE i.total > public.tax_of(10) AND i.customer_id IN @__keys0
            ORDER BY i.number ASC
            """, statement.Sql);
        Assert.Equal(["__keys0"], statement.Parameters);

        // A parent that does not select the key column carries it as a hidden column.
        var hidden = new Shop();
        hidden.Query($$"""
            {
              "name": "Names",
              "from": { "source": "{{hidden.Customer.Id}}", "alias": "c" },
              "select": [{ "name": "name", "expression": { "column": "c.name" } }],
              "collections": [{
                "attribute": "invoiceNumbers",
                "query": {
                  "from": { "source": "{{hidden.InvoiceTable}}", "alias": "i" },
                  "select": [{ "name": "number", "expression": { "column": "i.number" } }],
                  "where": { "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } }
                }
              }]
            }
            """);
        var adHoc = Assert.Single(hidden.Resolve().Db("main").Queries);
        var hiddenKey = Assert.Single(adHoc.Collections.Single().Keys);
        Assert.Equal(("__key0_0", true), (hiddenKey.ParentField, hiddenKey.Hidden));
        Assert.Null(adHoc.Entity);
        Assert.Equal("SELECT c.name AS name, c.id AS __key0_0\nFROM public.customers c", QuerySql.Render(adHoc).Sql);
    }

    [Fact]
    public void Each_rule_reports_its_finding_at_the_offending_node()
    {
        var s = new Shop();
        var other = s.Builder.Database("other", Dialect.PostgreSql).Id;
        var id = s.Query($$"""
            {
              "name": "Broken",
              "entity": "{{s.Customer.Id}}",
              "parameters": [
                { "name": "word", "type": "string" },
                { "name": "word", "type": "int32" },
                { "name": "odd", "type": "not_a_type" }
              ],
              "from": { "source": "{{s.Customer.Id}}", "alias": "c" },
              "joins": [
                { "source": "{{s.Invoice.Id}}@{{other}}", "alias": "x" },
                { "source": "{{s.View}}", "alias": "c" }
              ],
              "select": [
                { "attribute": "{{s.Invoice.AttrId("number")}}", "expression": { "column": "c.nope" } },
                { "name": "b", "expression": { "column": "z.id" } },
                { "name": "c", "expression": { "param": "missing" } },
                { "name": "d", "expression": { "sql": { "sqlserver": "GETDATE()" } } }
              ],
              "where": { "op": "between", "left": { "column": "c.name" }, "right": { "value": 1 } },
              "paging": { "limit": "word" },
              "collections": [
                {
                  "attribute": "{{s.Customer.AttrId("email")}}",
                  "query": {
                    "from": { "source": "{{s.InvoiceTable}}", "alias": "i" },
                    "select": [{ "name": "n", "expression": { "column": "i.number" } }],
                    "where": { "or": [{ "op": "eq", "left": { "column": "i.customer_id" }, "right": { "column": "c.id" } }, { "op": "eq", "left": { "column": "q.id" }, "right": { "value": 1 } }] }
                  }
                }
              ]
            }
            """);

        var findings = s.Resolve().Diagnostics.Where(d => d.ElementId == id).Select(d => (d.Rule, d.JsonPointer)).ToList();

        Assert.Contains(("MQ3001", "/parameters/1/name"), findings);
        Assert.Contains(("MQ4018", "/parameters/2/type"), findings);
        Assert.Contains(("MQ4021", "/joins/0/source"), findings);
        Assert.Contains(("MQ4022", "/joins/1/alias"), findings);
        Assert.Contains(("MQ4025", "/select/0/attribute"), findings);
        Assert.Contains(("MQ4023", "/select/0/expression/column"), findings);
        Assert.Contains(("MQ4022", "/select/1/expression/column"), findings);
        Assert.Contains(("MQ4024", "/select/2/expression/param"), findings);
        Assert.Contains(("MQ4029", "/select/3/expression/sql"), findings);
        Assert.Contains(("MQ4026", "/select"), findings);
        Assert.Contains(("MQ4031", "/where/right"), findings);
        Assert.Contains(("MQ4030", "/paging/limit"), findings);
        Assert.Contains(("MQ4027", "/collections/0/attribute"), findings);
        Assert.Contains(("MQ4028", "/collections/0/query/where/or/1/left/column"), findings);
        Assert.Contains(("MQ4028", "/collections/0/query/where/or/0/right/column"), findings);
        Assert.Contains(("MQ4028", "/collections/0/query"), findings);
        Assert.Equal(DiagnosticSeverity.Warning, s.Resolve().Diagnostics.Single(d => d.ElementId == id && d.Rule == "MQ4026").Severity);

        // The query is not rendered while it has errors, and the database view refuses a model that has them.
        Assert.Empty(s.Resolve().Db("main").Queries.Single().Sql);
    }

    [Fact]
    public void A_view_source_reads_its_columns_and_a_name_matches_ignoring_case_and_underscores()
    {
        var s = new Shop();
        var id = s.Query($$"""
            {
              "name": "OpenNumbers",
              "from": { "source": "{{s.View}}", "alias": "v" },
              "joins": [{ "source": "{{s.InvoiceTable}}", "alias": "i", "on": { "op": "eq", "left": { "column": "i.ID" }, "right": { "column": "v.id" } } }],
              "select": [
                { "name": "number", "expression": { "column": "v.number" } },
                { "name": "customer", "expression": { "column": "CustomerId" } },
                { "name": "flag", "expression": { "case": [{ "when": { "op": "isNull", "left": { "column": "i.total" } }, "then": { "value": "none" } }], "else": { "value": "some" } } },
                { "name": "total", "expression": { "cast": { "column": "i.total" }, "type": "double" } },
                { "name": "nothing", "expression": { "null": true } }
              ]
            }
            """);
        var model = s.Resolve();
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == id);
        var query = model.Db("main").Queries.Single();

        Assert.Same(model.Db("main").Views.Single(), query.From.View);
        Assert.Equal(("string", true), (query.Select[0].Type, query.Select[0].Nullable));
        Assert.Equal("customer_id", query.Select[1].Expression.ColumnName);
        Assert.Equal("""
            SELECT v.number AS number, i.customer_id AS customer, CASE WHEN i.total IS NULL THEN 'none' ELSE 'some' END AS flag, CAST(i.total AS double precision) AS total, NULL AS nothing
            FROM public.open_invoices v
            INNER JOIN public.invoices i ON i.id = v.id
            """, query.Sql);
        Assert.Equal("CAST(i.total AS float)", QuerySql.Render(query, "sqlserver").Sql.Split(", ")[3][..22]);
    }

    [Fact]
    public async Task Each_query_plans_one_unit_and_query_sql_renders_it_for_any_dialect()
    {
        var model = await BillingModel.GetAsync();
        var queries = model.Databases.Single().Queries;
        Assert.Equal(["FindCustomersWithIssuedInvoices", "InvoicesByCustomer", "RevenueByMonth"], queries.Select(q => q.Name));

        var shop = new Shop();
        Report(shop);
        WithInvoices(shop);
        shop.Builder.Database("other", Dialect.Sqlite);
        var pack = PlanningKit.Pack("p", [PlanningKit.Unit("query", "each query"), PlanningKit.Unit("other", "each query", new UnitWhere { Database = "other" })]);
        var plan = await new UnitPlanner(PlanningKit.Options).PlanAsync(shop.Resolve(), new PackSet([pack], []), new ScriptSandboxFactory(), null, Ct);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(["CustomerReport (main)", "CustomersWithInvoices (main)"],
            plan.Units.Where(u => u.Unit.Id == "query").Select(u => GenerationService.NameOf(u.Element!)!).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(plan.Units, u => u.Unit.Id == "other");

        var byCustomer = queries.Single(q => q.Name == "InvoicesByCustomer");
        var text = (await Adhoc.RenderAsync("{{ query_sql query 'sqlserver' }}|{{ query_collection_sql query.collections[0] }}|{{ query_sql query null { placeholder: ':' } }}",
            byCustomer, u => u with { For = "each query" })).Text();
        var parts = text.Split('|');
        Assert.Equal(QuerySql.Render(byCustomer, "sqlserver").Sql, parts[0]);
        Assert.Equal(QuerySql.RenderCollection(byCustomer.Collections[0]).Sql, parts[1]);
        Assert.Contains("i.customer_id = :customerId", parts[2], StringComparison.Ordinal);

        // A dialect the query has no sql text for fails the unit with MQ4029; the wrong argument with MQ6006.
        var revenue = queries.Single(q => q.Name == "RevenueByMonth");
        Assert.Equal("MQ4029", (await Adhoc.RenderAsync("{{ query_sql query 'mysql' }}", revenue, u => u with { For = "each query" })).Error().Rule);
        Assert.Equal("MQ6006", (await Adhoc.RenderAsync("{{ query_sql model }}")).Error().Rule);
    }

    [Fact]
    public async Task The_database_view_and_the_resolved_records_project_queries_with_their_sql()
    {
        var model = await BillingModel.GetAsync();
        var view = DatabaseViews.From(model.Databases.Single());

        var query = view.Queries.Single(q => q.Name == "InvoicesByCustomer");
        Assert.Equal("01J92P0V0FJ23CGSNKM7P1W5V7", query.EntityId);
        Assert.Equal(["customerId", "statuses", "offset", "limit"], query.SqlParameters);
        Assert.StartsWith("SELECT i.id AS id, i.number AS number", query.Sql, StringComparison.Ordinal);
        Assert.Equal(("01J92P0V1T0J6RH4MY9H81NYB4", "01J92P0V0FJ23CGSNKM7P1W5V7@01J92P0V1QRN2181XM2ZWE02W4", "from"), (query.From.Source, query.From.TableKey, query.From.Kind));
        Assert.Equal("and", query.Where!.Value.EnumerateObject().Single().Name);
        Assert.Equal("desc", query.OrderBy[0].GetProperty("direction").GetString());
        var lines = Assert.Single(query.Collections);
        Assert.Equal(("lines", "01J92P0V1H4D2M1HCK82ASJEWT", "01J92P0V0GWFR78HZH0P8Z3GY7"), (lines.Name, lines.Attribute, lines.EntityId));
        Assert.Equal(["__keys0"], lines.SqlParameters);
        Assert.Equal("i.01J92P0V0Q9EK961M5HAQ3C5MY", Assert.Single(lines.Keys).Outer);
        Assert.Contains("01J92P0V0FJ23CGSNKM7P1W5V7@01J92P0V1QRN2181XM2ZWE02W4", query.Uses);
        Assert.Equal(JsonValueKind.Object, query.Select[0].Expression.ValueKind);

        var records = ResolvedRecords.Entries(model, "queries", null).Select(e => (QueryRecord)e.Project()).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(["FindCustomersWithIssuedInvoices", "InvoicesByCustomer", "RevenueByMonth"], records.Select(r => r.Name));
        Assert.All(records, r => Assert.Equal(("query", "01J92P0V1QRN2181XM2ZWE02W4"), (r.Kind, r.Database)));
        Assert.DoesNotContain(ResolvedRecords.Entries(model, "all", null), e => e.Kind == "query");
    }

    [Fact]
    public void The_schema_diff_ignores_queries()
    {
        var differ = new SchemaDiffer();
        var without = new Shop();
        var before = differ.Capture(without.Resolve().Db("main"), 1);

        var with = new Shop();
        Report(with);
        WithInvoices(with);

        Assert.True(differ.Diff(before, with.Resolve().Db("main")).IsEmpty);
    }

    [Fact]
    public async Task Duplicate_query_names_in_a_database_are_MQ3001()
    {
        var s = new Shop();
        s.Query("""{ "name": "Same", "from": { "source": "x" }, "select": [{ "name": "a", "expression": { "value": 1 } }] }""");
        s.Query("""{ "name": "same", "from": { "source": "x" }, "select": [{ "name": "a", "expression": { "value": 1 } }] }""");

        var report = await ValidationFixture.Validator().ValidateAsync(s.Builder.Build(), ValidationScope.All, null, Ct);

        Assert.Single(report.Diagnostics, d => d.Rule == "MQ3001");
    }
}
