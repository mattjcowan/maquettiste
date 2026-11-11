using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Queries written as SQL text (sql-queries-design.md decisions 3 to 9, 15 and 18, sections 9 and 10): resolution, rendering per
/// placeholder style, the rules MQ4024, MQ4029, MQ4096, MQ4097 and MQ4098 and the cases of MQ3001, MQ4018, MQ4021, MQ4040 and MQ4044,
/// a binding over a query written as SQL text, the dependency keys and the records.
/// </summary>
public sealed class SqlQueryTests
{
    /// <summary>Customer and Invoice (the relation <c>places</c> gives invoices a customer_id column), an abstract Party, in <c>main</c>.</summary>
    private sealed class Store
    {
        public Store(Dialect dialect = Dialect.PostgreSql)
        {
            Builder = new ModelBuilder(seed: 55);
            Customer = Builder.Entity("Customer").Key("id", "uuid").Attr("name", "string", a => a.Length(100).Required());
            Invoice = Builder.Entity("Invoice").Key("id", "uuid").Attr("total", "decimal", a => a.Precision(12).Scale(2));
            Party = Builder.Entity("Party").Abstract().Key("id", "uuid");
            Places = Builder.Relation("places", Customer, Invoice, fromMax: MaxCardinality.One, fromMin: 1, fromRole: "customer", toRole: "invoices",
                fromNavigation: "customer", toNavigation: "invoices");
            Main = Builder.Database("main", dialect).Id;
        }

        public ModelBuilder Builder { get; }

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
            node["kind"] = "query";
            node["id"] = id;
            node["database"] ??= Main;
            Builder.Add(node.Deserialize<Query>(EngineJson.Options)!);
            return id;
        }

        public ResolvedModel Resolve() => ResolutionKit.Resolve(Builder);
    }

    private static RQuery QueryOf(ResolvedModel model, string id) => model.Databases.SelectMany(d => d.Queries).Single(q => q.Id == id);

    private static List<(string Rule, string? Pointer)> Findings(ResolvedModel model, string id) =>
        [.. model.Diagnostics.Where(d => d.ElementId == id).Select(d => (d.Rule, d.JsonPointer))];

    private static string Text(string text) => JsonSerializer.Serialize(text);

    private string TopInvoices(Store s, string sql, string extra = "") => s.Query($$"""
        { "name": "TopInvoices",
          "parameters": [ { "name": "min", "type": "decimal" }, { "name": "customer", "type": "uuid" } ],
          "sql": {{sql}},
          "columns": [ { "name": "id", "type": "uuid", "nullable": false }, { "name": "total", "type": "decimal", "description": "The invoice's total." } ],
          "reads": [ "{{s.Invoices}}" ]{{extra}} }
        """);

    [Fact]
    public void A_sql_text_resolves_its_declarations_and_renders_its_text_trimmed_with_its_final_semicolon_and_its_placeholders_rewritten()
    {
        var s = new Store();
        // A byte order mark and U+0085 count as white space at the ends; the final ; stays (decision 5).
        const string written = "\uFEFF\n  SELECT i.id, i.total::text AS t, total FROM public.invoices i\n  WHERE i.total > :min AND i.customer_id = :customer OR i.total < :min ;\n\u0085";
        var id = TopInvoices(s, $$"""{ "postgresql": {{Text(written)}} }""");
        var model = s.Resolve();
        Assert.Empty(Findings(model, id));
        var query = QueryOf(model, id);

        Assert.Equal(("sql", written), (query.Form, query.SqlText));
        Assert.Equal(written, Assert.Single(query.Texts).Value);
        Assert.Null(query.From);
        Assert.Equal([("id", "uuid", false, (string?)null), ("total", "decimal", true, "The invoice's total.")],
            query.Select.Select(f => (f.Name, f.Type!, f.Nullable, f.Description)));
        Assert.All(query.Select, f => Assert.Null(f.Expression));
        Assert.Equal("numeric(18,2)", query.Select[1].NativeType);
        Assert.Same(model.Db("main").Table("invoices"), Assert.Single(query.Uses));
        Assert.Empty(query.Writes);

        const string body = "SELECT i.id, i.total::text AS t, total FROM public.invoices i\n  WHERE i.total > {0}min AND i.customer_id = {0}customer OR i.total < {0}min ;";
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, body, "@"), query.Sql);
        var colons = QuerySql.Render(query, null, new QuerySqlOptions { Placeholder = ":" });
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, body, ":"), colons.Sql);

        // $n: numbered by first appearance, one number per name; lists does not apply.
        var numbered = QuerySql.Render(query, null, new QuerySqlOptions { Placeholder = "$", Lists = "any" });
        Assert.Equal("SELECT i.id, i.total::text AS t, total FROM public.invoices i\n  WHERE i.total > $1 AND i.customer_id = $2 OR i.total < $1 ;", numbered.Sql);
        Assert.Equal(["min", "customer"], numbered.Parameters);
        Assert.Equal(["min", "customer"], QuerySql.Parameters(query));
        Assert.Empty(numbered.Diagnostics);
    }

    [Fact]
    public void A_dialect_takes_its_own_text_else_the_star_text_and_neither_is_MQ4029()
    {
        var s = new Store(Dialect.SqlServer);
        var id = TopInvoices(s, """{ "*": "SELECT id, total FROM invoices WHERE total > :min AND customer_id = :customer", "sqlserver": "SELECT TOP 5 id, total FROM [invoices] WHERE total > :min AND customer_id = :customer;" }""");
        var model = s.Resolve();
        Assert.Empty(Findings(model, id));
        var query = QueryOf(model, id);
        Assert.Equal("SELECT TOP 5 id, total FROM [invoices] WHERE total > @min AND customer_id = @customer;", query.Sql);
        Assert.Equal("SELECT id, total FROM invoices WHERE total > $1 AND customer_id = $2", QuerySql.Render(query, "postgresql", new QuerySqlOptions { Placeholder = "$" }).Sql);

        // Only an Oracle text: the database's dialect has none (MQ4029), and the query has no SQL.
        var o = new Store(Dialect.SqlServer);
        var only = TopInvoices(o, """{ "oracle": "SELECT id, total FROM invoices WHERE total > :min AND customer_id = :customer" }""");
        var resolved = o.Resolve();
        Assert.Equal([("MQ4029", "/sql")], Findings(resolved, only));
        var none = QueryOf(resolved, only);
        Assert.Null(none.Sql);
        Assert.Null(none.SqlText);
        Assert.Equal("SELECT id, total FROM invoices WHERE total > :min AND customer_id = :customer", QuerySql.Render(none, "oracle", new QuerySqlOptions { Placeholder = ":" }).Sql);
        var missing = QuerySql.Render(none, "mysql");
        Assert.Equal(("", "MQ4029"), (missing.Sql, Assert.Single(missing.Diagnostics).Rule));
    }

    [Fact]
    public void MQ4024_names_a_placeholder_without_a_parameter_with_its_line_and_column_and_leaves_the_query_without_sql()
    {
        var s = new Store();
        var id = TopInvoices(s, """{ "postgresql": "SELECT id, total\nFROM invoices\nWHERE total > :mni AND customer_id = :customer AND total > :min" }""");
        var model = s.Resolve();
        var finding = Assert.Single(model.Diagnostics, d => d.ElementId == id);
        Assert.Equal(("MQ4024", "/sql/postgresql"), (finding.Rule, finding.JsonPointer));
        Assert.Contains("':mni' (line 3, column 15 of the postgresql text) names no parameter", finding.Message, StringComparison.Ordinal);
        Assert.Null(QueryOf(model, id).Sql);
    }

    [Fact]
    public void MQ4096_reports_a_parameter_missing_from_every_text_or_from_some_and_keeps_the_sql()
    {
        var s = new Store();
        var id = TopInvoices(s, """{ "postgresql": "SELECT id, total FROM invoices WHERE total > :min", "*": "SELECT id, total FROM invoices WHERE total > :min AND customer_id = :customer" }""");
        var model = s.Resolve();
        var finding = Assert.Single(model.Diagnostics, d => d.ElementId == id);
        Assert.Equal(("MQ4096", "/parameters/1"), (finding.Rule, finding.JsonPointer));
        Assert.Contains("does not appear as :customer in its postgresql text", finding.Message, StringComparison.Ordinal);
        Assert.NotNull(QueryOf(model, id).Sql);

        var t = new Store();
        var unused = TopInvoices(t, """{ "postgresql": "SELECT id, total FROM invoices WHERE customer_id = :customer" }""");
        var none = Assert.Single(t.Resolve().Diagnostics, d => d.ElementId == unused);
        Assert.Equal(("MQ4096", "/parameters/0"), (none.Rule, none.JsonPointer));
        Assert.Contains("appears as :min in none of its texts", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MQ4098_compares_declared_names_ignoring_case_and_reads_the_verb_behind_a_leading_with()
    {
        // A declared table named in another case passes; one the text never names (renamed, or declared by mistake) warns.
        var s = new Store();
        var passes = TopInvoices(s, """{ "postgresql": "SELECT id, total FROM \"INVOICES\" WHERE total > :min AND customer_id = :customer" }""");
        var renamed = TopInvoices(s, """{ "postgresql": "SELECT id, total FROM bills WHERE total > :min AND customer_id = :customer" }""");
        // A command behind a leading WITH, with no writes; then the same with writes; a select with no columns.
        var command = s.Query($$"""
            { "name": "MarkPaid", "parameters": [ { "name": "asOf", "type": "date" } ],
              "sql": { "postgresql": "WITH due AS (SELECT id FROM invoices WHERE total > 0)\nUPDATE invoices SET total = 0 WHERE id IN (SELECT id FROM due) AND :asOf IS NOT NULL" },
              "reads": [ "{{s.Invoices}}" ] }
            """);
        var declared = s.Query($$"""
            { "name": "MarkPaidDeclared", "parameters": [ { "name": "asOf", "type": "date" } ],
              "sql": { "postgresql": "WITH due AS (SELECT id FROM invoices WHERE total > 0)\nUPDATE invoices SET total = 0 WHERE id IN (SELECT id FROM due) AND :asOf IS NOT NULL" },
              "writes": [ "{{s.Invoices}}" ] }
            """);
        var rows = s.Query("""{ "name": "CountInvoices", "sql": { "*": "with x as (select 1 as n) select count(*) from invoices" } }""");
        var model = s.Resolve();

        Assert.Empty(Findings(model, passes));
        Assert.Equal([("MQ4098", "/reads/0")], Findings(model, renamed));
        Assert.Equal([("MQ4098", "/sql/postgresql")], Findings(model, command));
        Assert.Contains("is an UPDATE statement (after its WITH)", Assert.Single(model.Diagnostics, d => d.ElementId == command).Message, StringComparison.Ordinal);
        Assert.Empty(Findings(model, declared));
        Assert.Equal([("MQ4098", "/sql/*")], Findings(model, rows));
        // Warnings: each query still has its SQL.
        Assert.All(new[] { renamed, command, rows }, q => Assert.NotNull(QueryOf(model, q).Sql));
        var written = QueryOf(model, declared);
        Assert.Same(model.Db("main").Table("invoices"), Assert.Single(written.Writes));
        Assert.Empty(written.Select);
    }

    [Fact]
    public void The_declarations_take_the_cases_of_MQ4021_MQ4018_MQ3001_and_MQ4040()
    {
        var s = new Store();
        var reads = s.Query($$"""
            { "name": "BadReads", "sql": { "*": "SELECT 1 AS n FROM invoices, customers" }, "columns": [ { "name": "n", "type": "int32" } ],
              "reads": [ "{{s.Builder.NewId()}}" ], "writes": [ "{{s.Customer.Id}}", "{{s.Invoice.Id}}" ] }
            """);
        var other = s.Query($$"""{ "name": "Other", "sql": { "*": "SELECT 1 AS n" }, "columns": [ { "name": "n", "type": "int32" } ], "writes": [ "{{reads}}" ] }""");
        var columns = s.Query("""
            { "name": "BadColumns", "sql": { "*": "SELECT 1" },
              "columns": [ { "name": "a", "type": "nope" }, { "name": "A", "type": "int32" }, { "name": "mq_x", "type": "int32" },
                           { "name": "total_due", "type": "int32" }, { "name": "totalDue", "type": "int32" } ] }
            """);
        var model = s.Resolve();

        Assert.Equal([("MQ4021", "/reads/0")], Findings(model, reads));
        Assert.Contains("is no table, view, routine, sequence or database type of database 'main'", Assert.Single(model.Diagnostics, d => d.ElementId == reads).Message, StringComparison.Ordinal);
        Assert.Equal([("MQ4021", "/writes/0")], Findings(model, other));
        Assert.Contains("is query 'BadReads', not a table", Assert.Single(model.Diagnostics, d => d.ElementId == other).Message, StringComparison.Ordinal);
        Assert.Null(QueryOf(model, reads).Sql);
        Assert.Equal(2, QueryOf(model, reads).Writes.Count); // an entity id names its table, as from.source does
        Assert.Equal(
            [("MQ3001", "/columns/1/name"), ("MQ4018", "/columns/0/type"), ("MQ4040", "/columns/2/name"), ("MQ4040", "/columns/4/name")],
            Findings(model, columns).Order());
        Assert.Null(QueryOf(model, columns).Sql); // MQ4018 leaves it without SQL; MQ3001 and MQ4040 alone would not
    }

    [Fact]
    public void A_sql_text_keys_on_the_elements_it_declares_not_on_what_they_depend_on()
    {
        var s = new Store();
        var text = TopInvoices(s, """{ "postgresql": "SELECT id, total FROM invoices WHERE total > :min AND customer_id = :customer" }""");
        var built = s.Query($$"""
            { "name": "Built", "from": { "source": "{{s.Invoices}}", "alias": "i" },
              "select": [ { "name": "id", "expression": { "column": "i.id" } } ] }
            """);
        var model = s.Resolve();
        var keys = QueryOf(model, text).Dependencies;

        Assert.Contains("e:" + text, keys);
        Assert.Contains("r:" + text, keys);
        Assert.Contains("e:" + s.Main, keys);
        Assert.Contains("e:" + s.Invoice.Id, keys);
        // The relation that gives invoices its customer_id column is a dependency of the table, so of a built query over it; a
        // change to it renders no unit of the SQL text again.
        Assert.Contains("e:" + s.Places.Id, QueryOf(model, built).Dependencies);
        Assert.DoesNotContain("e:" + s.Places.Id, keys);
        Assert.DoesNotContain("e:" + s.Customer.Id, keys);
    }

    private sealed class Bound
    {
        public Bound(Dialect dialect, string sql, string extra = "")
        {
            S = new Store(dialect);
            Query = S.Query($$"""
                { "name": "BigInvoices", "parameters": [ { "name": "min", "type": "decimal" } ], "sql": {{sql}},
                  "columns": [ { "name": "id", "type": "uuid", "nullable": false }, { "name": "total", "type": "decimal" } ],
                  "reads": [ "{{S.Invoices}}" ]{{extra}} }
                """);
            var big = S.Builder.Entity("BigInvoice").Key("id", "uuid").Attr("total", "decimal", a => a.Precision(12).Scale(2));
            big.Bind(new EntityBinding
            {
                Id = "", Database = S.Main, Source = Query,
                Fields = [new BindingField { Attribute = big.AttrId("id"), Column = "id" }, new BindingField { Attribute = big.AttrId("total"), Column = "total" }],
            });
            Entity = big.Id;
            Model = S.Resolve();
        }

        public Store S { get; }

        public string Query { get; }

        public string Entity { get; }

        public ResolvedModel Model { get; }

        public REntityBinding Binding => Model.Entity("BigInvoice").Bindings["main"];
    }

    [Fact]
    public void A_binding_reads_a_sql_text_as_a_derived_table()
    {
        var b = new Bound(Dialect.PostgreSql, """{ "postgresql": "SELECT id, total FROM invoices WHERE total > :min ORDER BY total DESC;" }""");
        Assert.DoesNotContain(b.Model.Diagnostics, d => d.ElementId == b.Entity || d.ElementId == b.Query);
        var binding = b.Binding;
        Assert.Equal(("query", false), (binding.SourceKind, binding.Writes));
        Assert.Equal([b.Entity], QueryOf(b.Model, b.Query).BoundBy.Select(x => x.Entity.Id));
        var select = BindingSql.Render(binding, "select-by-key", null, new QuerySqlOptions { Placeholder = "$" });
        Assert.Equal("SELECT q.id AS id, q.total AS total\nFROM (\nSELECT id, total FROM invoices WHERE total > $1 ORDER BY total DESC\n) q\nWHERE q.id = $2", select.Sql);
        Assert.Equal(["min", "id"], select.Parameters);
        // The query's own statement keeps its final ;.
        Assert.EndsWith("ORDER BY total DESC;", QueryOf(b.Model, b.Query).Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT id, total FROM invoices WHERE total > :min; -- the big ones", "SELECT id, total FROM invoices WHERE total > $1")]
    [InlineData("SELECT id, total FROM invoices WHERE total > :min /* why */ ;\n/* trailing */\n", "SELECT id, total FROM invoices WHERE total > $1 /* why */")]
    [InlineData("SELECT id, total FROM invoices WHERE total > :min -- a note\n", "SELECT id, total FROM invoices WHERE total > $1 -- a note")]
    [InlineData("SELECT id, total FROM invoices WHERE total > :min AND ';' <> 'x'", "SELECT id, total FROM invoices WHERE total > $1 AND ';' <> 'x'")]
    [InlineData("SELECT id, total FROM invoices WHERE total > :min AND \"a;\" IS NULL", "SELECT id, total FROM invoices WHERE total > $1 AND \"a;\" IS NULL")]
    [InlineData("SELECT id, total FROM invoices WHERE total > :min;;", "SELECT id, total FROM invoices WHERE total > $1;")]
    public void A_binding_wraps_a_sql_text_without_its_final_semicolon_and_what_follows_it(string text, string body)
    {
        var b = new Bound(Dialect.PostgreSql, "{ \"postgresql\": " + Text(text) + " }");
        Assert.DoesNotContain(b.Model.Diagnostics, d => d.ElementId == b.Entity || d.ElementId == b.Query);
        var select = BindingSql.Render(b.Binding, "select", null, new QuerySqlOptions { Placeholder = "$" });
        Assert.Equal("SELECT q.id AS id, q.total AS total\nFROM (\n" + body + "\n) q", select.Sql);
        // The statement itself is the text as written, trimmed.
        Assert.Equal(QuerySql.Statement(text).Replace(":min", "@min", StringComparison.Ordinal), QueryOf(b.Model, b.Query).Sql);
    }

    [Fact]
    public void MQ4044_refuses_a_sql_text_that_writes_or_declares_no_columns_as_a_binding_source()
    {
        var s = new Store();
        var command = s.Query($$"""{ "name": "Zero", "parameters": [ { "name": "min", "type": "decimal" } ], "sql": { "*": "UPDATE invoices SET total = :min RETURNING id, total" }, "columns": [ { "name": "id", "type": "uuid" }, { "name": "total", "type": "decimal" } ], "writes": [ "{{s.Invoices}}" ] }""");
        var noRows = s.Query($$"""{ "name": "Touch", "sql": { "*": "DELETE FROM invoices" }, "writes": [ "{{s.Invoices}}" ] }""");
        var nothing = s.Query("""{ "name": "Ping", "sql": { "*": "CALL ping()" } }""");
        foreach (var (name, source) in new[] { ("A", command), ("B", noRows), ("C", nothing) })
        {
            var e = s.Builder.Entity(name).Key("id", "uuid");
            e.Bind(new EntityBinding { Id = "", Database = s.Main, Source = source, Fields = [new BindingField { Attribute = e.AttrId("id"), Column = "id" }] });
        }

        var model = s.Resolve();
        var findings = model.Diagnostics.Where(d => d.Rule == "MQ4044").OrderBy(d => d.Message, StringComparer.Ordinal).ToList();
        Assert.Equal(3, findings.Count);
        Assert.All(findings, d => Assert.Equal("/bindings/0/source", d.JsonPointer));
        Assert.Contains("reads query 'Zero', which is written as SQL text and writes tables", findings[0].Message, StringComparison.Ordinal);
        Assert.Contains("reads query 'Touch', which is written as SQL text and writes tables", findings[1].Message, StringComparison.Ordinal);
        Assert.Contains("reads query 'Ping', which is written as SQL text and declares no columns", findings[2].Message, StringComparison.Ordinal);
        // The binding reads nothing, so its fields report nothing more.
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule == "MQ4045");
    }

    [Theory]
    [InlineData("WITH big AS (SELECT id, total FROM invoices WHERE total > :min) SELECT id, total FROM big", "starts with WITH")]
    [InlineData("SELECT id, total FROM invoices WHERE total > :min ORDER BY total", "has an ORDER BY outside parentheses with no OFFSET, FETCH or TOP")]
    [InlineData("SELECT id, total FROM invoices WHERE total > :min ORDER BY total OFFSET 0 ROWS", null)]
    [InlineData("SELECT TOP 10 id, total FROM invoices WHERE total > :min ORDER BY total", null)]
    [InlineData("SELECT id, total FROM (SELECT TOP 10 * FROM invoices ORDER BY total) x WHERE total > :min", null)]
    public void MQ4097_warns_when_sql_server_cannot_nest_the_text(string text, string? problem)
    {
        var b = new Bound(Dialect.SqlServer, $$"""{ "*": {{Text(text)}} }""");
        var findings = b.Model.Diagnostics.Where(d => d.Rule == "MQ4097").ToList();
        if (problem is null)
        {
            Assert.Empty(findings);
        }
        else
        {
            var finding = Assert.Single(findings);
            Assert.Equal((b.Entity, "/bindings/0/source"), (finding.ElementId, finding.JsonPointer));
            Assert.Contains("whose SQL text " + problem, finding.Message, StringComparison.Ordinal);
        }

        Assert.Equal("query", b.Binding.SourceKind);

        // Only SQL Server: the other dialects take both.
        Assert.DoesNotContain(new Bound(Dialect.PostgreSql, $$"""{ "*": {{Text(text)}} }""").Model.Diagnostics, d => d.Rule == "MQ4097");
    }

    [Fact]
    public void The_record_has_the_form_the_texts_and_the_writes_and_leaves_them_out_for_a_built_query()
    {
        var s = new Store();
        var text = s.Query($$"""
            { "name": "Bump", "parameters": [ { "name": "min", "type": "decimal" } ],
              "sql": { "postgresql": "UPDATE invoices SET total = total + 1 WHERE total > :min RETURNING id" },
              "columns": [ { "name": "id", "type": "uuid", "nullable": false, "description": "The invoice." } ],
              "reads": [ "{{s.Invoices}}" ], "writes": [ "{{s.Invoices}}" ] }
            """);
        var built = s.Query($$"""{ "name": "Built", "from": { "source": "{{s.Invoices}}", "alias": "i" }, "select": [ { "name": "id", "expression": { "column": "i.id" } } ] }""");
        var model = s.Resolve();

        var view = JsonSerializer.SerializeToElement(QueryViews.Project(QueryOf(model, text)), EngineJson.Options);
        Assert.Equal("sql", view.GetProperty("form").GetString());
        Assert.Equal("UPDATE invoices SET total = total + 1 WHERE total > :min RETURNING id", view.GetProperty("texts").GetProperty("postgresql").GetString());
        Assert.Equal([s.Invoices], view.GetProperty("writes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([s.Invoices], view.GetProperty("uses").EnumerateArray().Select(e => e.GetString()));
        Assert.False(view.TryGetProperty("from", out _));
        Assert.Equal("UPDATE invoices SET total = total + 1 WHERE total > @min RETURNING id", view.GetProperty("sql").GetString());
        var column = Assert.Single(view.GetProperty("select").EnumerateArray());
        Assert.False(column.TryGetProperty("expression", out _));
        Assert.Equal(("The invoice.", false), (column.GetProperty("description").GetString(), column.GetProperty("nullable").GetBoolean()));

        var plain = JsonSerializer.SerializeToElement(QueryViews.Project(QueryOf(model, built)), EngineJson.Options);
        foreach (var name in new[] { "form", "texts", "writes" })
            Assert.False(plain.TryGetProperty(name, out _), name);
        Assert.True(plain.TryGetProperty("from", out _));
        var field = Assert.Single(plain.GetProperty("select").EnumerateArray());
        Assert.True(field.TryGetProperty("expression", out _));
        Assert.False(field.TryGetProperty("description", out _));
    }
}
