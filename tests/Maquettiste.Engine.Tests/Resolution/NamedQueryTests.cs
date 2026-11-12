using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Named queries in built queries (sql-queries-design.md decisions 10 to 14 and 16, section 21): a <c>with</c> entry resolved as a
/// select over the database's tables and earlier named queries, read by name from the query, its nested queries and later named
/// queries; a recursive one's repeating part; the <c>WITH</c> clause per dialect, in collections and in a binding over the query; the
/// rules MQ3001, MQ4021, MQ4023, MQ4099 and MQ4099; inferred types and merged nullability; the records.
/// </summary>
public sealed class NamedQueryTests
{
    /// <summary>A database <c>main</c> with a table <c>categories</c> (id, parent_id, name, note) and queries added from JSON.</summary>
    private sealed class Tree
    {
        public Tree(Dialect dialect = Dialect.PostgreSql)
        {
            Builder = new ModelBuilder(seed: 5599);
            Database = Builder.Database("main", dialect);
            Categories = Builder.Table("categories", Database).Column("id", "int64", nullable: false).Column("parent_id", "int64", nullable: true)
                .Column("name", "string", nullable: false, length: 100).Column("note", "string", nullable: true, length: 200).PrimaryKey("id").Id;
        }

        public ModelBuilder Builder { get; }

        public DatabaseBuilder Database { get; }

        public string Categories { get; }

        /// <summary>Adds a query from its JSON (without kind, id and database, which are filled in) and returns its id.</summary>
        public string Query(string json)
        {
            var node = JsonNode.Parse(json)!.AsObject();
            var id = Builder.NewId();
            node["id"] = id;
            node["database"] ??= Database.Id;
            Builder.Add(node.Deserialize<Query>(EngineJson.Options)!);
            return id;
        }

        public ResolvedModel Resolve() => ResolutionKit.Resolve(Builder);
    }

    /// <summary>The design's example (section 8): the ancestors of a category, through a recursive named query.</summary>
    private static string Ancestors(Tree t, string union = "all", string? extraWith = null) => t.Query($$"""
        {
          "name": "CategoryAncestors",
          "parameters": [ { "name": "categoryId", "type": "int64" } ],
          "with": [ {{extraWith}}{
            "name": "chain",
            "from": { "source": "{{t.Categories}}", "alias": "c" },
            "select": [
              { "name": "id", "expression": { "column": "c.id" } },
              { "name": "parentId", "expression": { "column": "c.parent_id" } },
              { "name": "depth", "expression": { "value": 0 } } ],
            "where": { "op": "eq", "left": { "column": "c.id" }, "right": { "param": "categoryId" } },
            "recursive": {
              "union": "{{union}}",
              "from": { "source": "chain", "alias": "ch" },
              "joins": [ { "source": "{{t.Categories}}", "alias": "p",
                           "on": { "op": "eq", "left": { "column": "p.id" }, "right": { "column": "ch.parentId" } } } ],
              "select": [
                { "name": "id", "expression": { "column": "p.id" } },
                { "name": "parentId", "expression": { "column": "p.parent_id" } },
                { "name": "depth", "expression": { "op": "+", "args": [ { "column": "ch.depth" }, { "value": 1 } ] } } ] } } ],
          "from": { "source": "chain", "alias": "a" },
          "joins": [ { "source": "{{t.Categories}}", "alias": "c",
                       "on": { "op": "eq", "left": { "column": "c.id" }, "right": { "column": "a.id" } } } ],
          "select": [
            { "name": "id", "expression": { "column": "a.id" } },
            { "name": "name", "expression": { "column": "c.name" } },
            { "name": "depth", "expression": { "column": "a.depth" } } ],
          "orderBy": [ { "expression": { "column": "a.depth" } } ]
        }
        """);

    private static RQuery Single(ResolvedModel model) => model.Db("main").Queries.Single();

    [Fact]
    public void A_recursive_named_query_renders_as_the_design_shows_it_and_resolves_its_columns()
    {
        var t = new Tree();
        var id = Ancestors(t);
        var model = t.Resolve();
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == id);
        var query = Single(model);

        Assert.Equal("""
            WITH RECURSIVE chain (id, parentId, depth) AS (
                SELECT c.id AS id, c.parent_id AS parentId, 0 AS depth
                FROM public.categories c
                WHERE c.id = @categoryId
                UNION ALL
                SELECT p.id AS id, p.parent_id AS parentId, (ch.depth + 1) AS depth
                FROM chain ch
                INNER JOIN public.categories p ON p.id = ch.parentId
            )
            SELECT a.id AS id, c.name AS name, a.depth AS depth
            FROM chain a
            INNER JOIN public.categories c ON c.id = a.id
            ORDER BY a.depth ASC
            """.ReplaceLineEndings("\n"), query.Sql);

        var chain = Assert.Single(query.With);
        Assert.Equal(("chain", 0, "all"), (chain.Name, chain.Index, chain.Union));
        Assert.Same(query, chain.Query.Parent);
        Assert.Same(query, chain.Recursive!.Parent);
        Assert.Equal([("id", "int64", false), ("parentId", "int64", true), ("depth", "int32", false)],
            chain.Columns.Select(c => (c.Name, c.Type!, c.Nullable)));
        Assert.Same(chain, query.From!.Named);
        Assert.Equal(("chain", null), (query.From.Name, query.From.Schema));
        Assert.Same(chain.Columns[2], query.Select[2].Expression!.NamedField);
        Assert.Equal(("int32", false), (query.Select[2].Type, query.Select[2].Nullable));
        Assert.Same(chain, chain.Recursive.From!.Named);

        // The named query's reads are the query's uses and dependencies.
        Assert.Contains(query.Uses, u => u is RTable { Name: "categories" });
    }

    public static TheoryData<Dialect, string, string> Dialects() => new()
    {
        { Dialect.PostgreSql, "WITH RECURSIVE chain (id, parentId, depth) AS (", "    UNION\n" },
        { Dialect.MySql, "WITH RECURSIVE chain (id, parentId, depth) AS (", "    UNION\n" },
        { Dialect.Sqlite, "WITH RECURSIVE chain (id, parentId, depth) AS (", "    UNION\n" },
        { Dialect.SqlServer, "WITH chain (id, parentId, depth) AS (", "    UNION ALL\n" },
        { Dialect.Oracle, "WITH chain (id, parentId, depth) AS (", "    UNION ALL\n" },
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Each_dialect_has_its_keyword_the_column_list_and_its_union(Dialect dialect, string first, string union)
    {
        var t = new Tree(dialect);
        var id = Ancestors(t, union: dialect is Dialect.SqlServer or Dialect.Oracle ? "all" : "distinct");
        var model = t.Resolve();
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == id);
        var sql = Single(model).Sql!;

        Assert.StartsWith(first + "\n", sql, StringComparison.Ordinal);
        Assert.Contains(union, sql, StringComparison.Ordinal);
        Assert.Contains("\n)\nSELECT ", sql, StringComparison.Ordinal);

        // Another dialect's rendering of the same query follows that dialect.
        var other = QuerySql.Render(Single(model), dialect == Dialect.SqlServer ? "postgresql" : "sqlserver").Sql;
        Assert.StartsWith(dialect == Dialect.SqlServer ? "WITH RECURSIVE chain" : "WITH chain", other, StringComparison.Ordinal);
    }

    [Fact]
    public void Placeholders_are_numbered_in_text_order_and_a_named_query_reads_an_earlier_one()
    {
        var t = new Tree();
        var id = t.Query($$"""
            {
              "name": "Children",
              "parameters": [ { "name": "rootName", "type": "string" }, { "name": "minId", "type": "int64" } ],
              "with": [
                { "name": "roots", "from": { "source": "{{t.Categories}}", "alias": "c" },
                  "select": [ { "name": "id", "expression": { "column": "c.id" } }, { "name": "label", "expression": { "column": "c.note" } } ],
                  "where": { "op": "eq", "left": { "column": "c.name" }, "right": { "param": "rootName" } } },
                { "name": "kids", "from": { "source": "roots", "alias": "r" },
                  "joins": [ { "source": "{{t.Categories}}", "alias": "k", "on": { "op": "eq", "left": { "column": "k.parent_id" }, "right": { "column": "r.id" } } } ],
                  "select": [ { "name": "id", "expression": { "column": "k.id" } }, { "name": "rootLabel", "expression": { "column": "r.label" } } ],
                  "distinct": true }
              ],
              "from": { "source": "kids", "alias": "x" },
              "select": [ { "name": "id", "expression": { "column": "x.id" } }, { "name": "rootLabel", "expression": { "column": "x.rootLabel" } } ],
              "where": { "op": "ge", "left": { "column": "x.id" }, "right": { "param": "minId" } }
            }
            """);
        var model = t.Resolve();
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == id);
        var query = Single(model);

        var numbered = QuerySql.Render(query, null, new QuerySqlOptions { Placeholder = "$" });
        Assert.Equal("""
            WITH roots (id, label) AS (
                SELECT c.id AS id, c.note AS label
                FROM public.categories c
                WHERE c.name = $1
            ),
            kids (id, rootLabel) AS (
                SELECT DISTINCT k.id AS id, r.label AS rootLabel
                FROM roots r
                INNER JOIN public.categories k ON k.parent_id = r.id
            )
            SELECT x.id AS id, x.rootLabel AS rootLabel
            FROM kids x
            WHERE x.id >= $2
            """.ReplaceLineEndings("\n"), numbered.Sql);
        Assert.Equal(["rootName", "minId"], numbered.Parameters);
        Assert.Equal(("string", true), (query.Select[1].Type, query.Select[1].Nullable));
        Assert.Same(query.With[0], query.With[1].Query.From!.Named);
    }

    /// <summary>A query over categories with a collection of each one's children read through named queries, and an exists over one.</summary>
    private static string WithCollection(Tree t) => t.Query($$"""
        {
          "name": "Parents",
          "with": [
            { "name": "base", "from": { "source": "{{t.Categories}}", "alias": "c" },
              "select": [ { "name": "id", "expression": { "column": "c.id" } }, { "name": "parentId", "expression": { "column": "c.parent_id" } } ] },
            { "name": "unused", "from": { "source": "{{t.Categories}}", "alias": "c" }, "select": [ { "name": "id", "expression": { "column": "c.id" } } ] },
            { "name": "kids", "from": { "source": "base", "alias": "b" }, "select": [ { "name": "id", "expression": { "column": "b.id" } }, { "name": "parentId", "expression": { "column": "b.parentId" } } ] },
            { "name": "roots", "from": { "source": "{{t.Categories}}", "alias": "c" }, "select": [ { "name": "id", "expression": { "column": "c.id" } } ],
              "where": { "op": "isNull", "left": { "column": "c.parent_id" } } }
          ],
          "from": { "source": "{{t.Categories}}", "alias": "p" },
          "select": [ { "name": "id", "expression": { "column": "p.id" } } ],
          "where": { "exists": { "from": { "source": "roots", "alias": "r" }, "where": { "op": "eq", "left": { "column": "r.id" }, "right": { "column": "p.id" } } } },
          "collections": [ {
            "attribute": "children",
            "query": {
              "from": { "source": "kids", "alias": "k" },
              "select": [ { "name": "id", "expression": { "column": "k.id" } } ],
              "where": { "op": "eq", "left": { "column": "k.parentId" }, "right": { "column": "p.id" } }
            } } ]
        }
        """);

    [Fact]
    public void A_collection_repeats_the_named_queries_it_reads_and_an_exists_reads_one()
    {
        var t = new Tree();
        var id = WithCollection(t);
        var model = t.Resolve();
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == id);
        var query = Single(model);

        Assert.Equal("""
            WITH base (id, parentId) AS (
                SELECT c.id AS id, c.parent_id AS parentId
                FROM public.categories c
            ),
            unused (id) AS (
                SELECT c.id AS id
                FROM public.categories c
            ),
            kids (id, parentId) AS (
                SELECT b.id AS id, b.parentId AS parentId
                FROM base b
            ),
            roots (id) AS (
                SELECT c.id AS id
                FROM public.categories c
                WHERE c.parent_id IS NULL
            )
            SELECT p.id AS id
            FROM public.categories p
            WHERE EXISTS (SELECT 1
                FROM roots r
                WHERE r.id = p.id)
            """.ReplaceLineEndings("\n"), query.Sql);

        // The collection's statement runs on its own: it repeats kids and base, which kids reads, in list order, and nothing else.
        Assert.Equal("""
            WITH base (id, parentId) AS (
                SELECT c.id AS id, c.parent_id AS parentId
                FROM public.categories c
            ),
            kids (id, parentId) AS (
                SELECT b.id AS id, b.parentId AS parentId
                FROM base b
            )
            SELECT k.id AS id, k.parentId AS mq_key0
            FROM kids k
            WHERE k.parentId IN @mq_keys0
            """.ReplaceLineEndings("\n"), QuerySql.RenderCollection(Assert.Single(query.Collections)).Sql);
        Assert.Equal(("int64", true), (query.Collections[0].Keys[0].Inner.Type, query.Collections[0].Keys[0].Inner.Nullable));
    }

    [Fact]
    public void A_binding_over_a_query_with_named_queries_puts_the_with_first()
    {
        var t = new Tree();
        var queryId = Ancestors(t);
        var ancestor = t.Builder.Entity("Ancestor").Key("id", "int64").Attr("depth", "int32", a => a.Required());
        ancestor.Bind(new EntityBinding
        {
            Id = "", Database = t.Database.Id, Source = queryId,
            Fields = [new BindingField { Attribute = ancestor.AttrId("id"), Column = "id" }, new BindingField { Attribute = ancestor.AttrId("depth"), Column = "depth" }],
        });
        var model = t.Resolve();
        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == Diagnostics.DiagnosticSeverity.Error);
        var binding = model.Entity("Ancestor").Bindings["main"];

        var numbered = BindingSql.Render(binding, "select-by-key", null, new QuerySqlOptions { Placeholder = "$" });
        Assert.Equal("""
            WITH RECURSIVE chain (id, parentId, depth) AS (
                SELECT c.id AS id, c.parent_id AS parentId, 0 AS depth
                FROM public.categories c
                WHERE c.id = $1
                UNION ALL
                SELECT p.id AS id, p.parent_id AS parentId, (ch.depth + 1) AS depth
                FROM chain ch
                INNER JOIN public.categories p ON p.id = ch.parentId
            )
            SELECT q.id AS id, q.depth AS depth
            FROM (
            SELECT a.id AS id, c.name AS name, a.depth AS depth
            FROM chain a
            INNER JOIN public.categories c ON c.id = a.id
            ) q
            WHERE q.id = $2
            """.ReplaceLineEndings("\n"), numbered.Sql);
        Assert.Equal(["categoryId", "id"], numbered.Parameters);

        var (with, statement) = QuerySql.RenderParts(Single(model), null, null);
        Assert.Equal(Single(model).Sql, with + "\n" + statement.Sql);
    }

    /// <summary>A named query <c>chain</c> whose repeating part the edit changes, on a dialect; the MQ4099 findings' pointers.</summary>
    private static List<string> Mq4100(Dialect dialect, string recursive, string anchorSelect = """
        [ { "name": "id", "expression": { "column": "c.id" } }, { "name": "depth", "expression": { "value": 0 } } ]
        """)
    {
        var t = new Tree(dialect);
        var id = t.Query($$"""
            {
              "name": "Walk",
              "with": [ { "name": "chain", "from": { "source": "{{t.Categories}}", "alias": "c" }, "select": {{anchorSelect}},
                "recursive": {{recursive.Replace("$T", t.Categories, StringComparison.Ordinal)}} } ],
              "from": { "source": "chain", "alias": "a" },
              "select": [ { "name": "id", "expression": { "column": "a.id" } } ]
            }
            """);
        var model = t.Resolve();
        var query = Single(model);
        var found = model.Diagnostics.Where(d => d.ElementId == id).ToList();
        Assert.All(found, d => Assert.Equal("MQ4099", d.Rule));
        if (found.Count > 0)
            Assert.Null(query.Sql);
        return [.. found.Select(d => d.JsonPointer!)];
    }

    private const string Fits = """
        { "from": { "source": "chain", "alias": "ch" },
          "joins": [ { "source": "$T", "alias": "p", "on": { "op": "eq", "left": { "column": "p.parent_id" }, "right": { "column": "ch.id" } } } ],
          "select": [ { "name": "id", "expression": { "column": "p.id" } }, { "name": "depth", "expression": { "op": "+", "args": [ { "column": "ch.depth" }, { "value": 1 } ] } } ] }
        """;

    [Theory]
    [InlineData(Dialect.PostgreSql)]
    [InlineData(Dialect.SqlServer)]
    [InlineData(Dialect.MySql)]
    [InlineData(Dialect.Sqlite)]
    [InlineData(Dialect.Oracle)]
    public void A_repeating_part_that_fits_has_no_finding_and_union_distinct_is_refused_on_sql_server_and_oracle(Dialect dialect)
    {
        Assert.Empty(Mq4100(dialect, Fits));
        var distinct = Mq4100(dialect, Fits.Replace("{ \"from\"", "{ \"union\": \"distinct\", \"from\"", StringComparison.Ordinal));
        Assert.Equal(dialect is Dialect.SqlServer or Dialect.Oracle ? ["/with/0/recursive/union"] : [], distinct);
    }

    [Fact]
    public void A_repeating_part_that_does_not_fit_its_anchor_is_MQ4099()
    {
        var pg = Dialect.PostgreSql;
        // Other names, or another number of fields.
        Assert.Equal(["/with/0/recursive/select"], Mq4100(pg, Fits.Replace("\"name\": \"depth\"", "\"name\": \"level\"", StringComparison.Ordinal)));
        Assert.Equal(["/with/0/recursive/select"], Mq4100(pg, """
            { "from": { "source": "chain", "alias": "ch" }, "select": [ { "name": "id", "expression": { "column": "ch.id" } } ] }
            """));
        // A type that differs from the anchor's; an unknown type on either side passes.
        Assert.Equal(["/with/0/recursive/select/1"], Mq4100(pg, Fits.Replace("{ \"value\": 1 }", "{ \"value\": 1.5 }", StringComparison.Ordinal)));
        Assert.Empty(Mq4100(pg, Fits, """
            [ { "name": "id", "expression": { "column": "c.id" } }, { "name": "depth", "expression": { "null": true } } ]
            """));
        // Its own name not read, read twice, on the outer side of a join, or in a nested query.
        Assert.Equal(["/with/0/recursive/from"], Mq4100(pg, """
            { "from": { "source": "$T", "alias": "p" },
              "select": [ { "name": "id", "expression": { "column": "p.id" } }, { "name": "depth", "expression": { "value": 1 } } ] }
            """));
        Assert.Equal(["/with/0/recursive/joins/0/source"], Mq4100(pg, Fits.Replace("\"source\": \"$T\", \"alias\": \"p\"", "\"source\": \"chain\", \"alias\": \"p\"", StringComparison.Ordinal)
            .Replace("p.parent_id", "p.id", StringComparison.Ordinal)));
        Assert.Equal(["/with/0/recursive/joins/0/source"], Mq4100(pg, """
            { "from": { "source": "$T", "alias": "p" },
              "joins": [ { "source": "chain", "alias": "ch", "kind": "left", "on": { "op": "eq", "left": { "column": "p.parent_id" }, "right": { "column": "ch.id" } } } ],
              "select": [ { "name": "id", "expression": { "column": "p.id" } }, { "name": "depth", "expression": { "value": 1 } } ] }
            """));
        Assert.Equal(["/with/0/recursive/where/exists/from/source"], Mq4100(pg, """
            { "from": { "source": "$T", "alias": "p" },
              "select": [ { "name": "id", "expression": { "column": "p.id" } }, { "name": "depth", "expression": { "value": 1 } } ],
              "where": { "exists": { "from": { "source": "chain", "alias": "ch" }, "where": { "op": "eq", "left": { "column": "ch.id" }, "right": { "column": "p.parent_id" } } } } }
            """));
        // An aggregate call.
        Assert.Equal(["/with/0/recursive/select"], Mq4100(pg, Fits.Replace("{ \"column\": \"p.id\" }", "{ \"call\": \"max\", \"args\": [ { \"column\": \"p.id\" } ] }", StringComparison.Ordinal)));
    }

    /// <summary>A repeating part that reads chain in from and categories in a join of the given kind, or the other way round.</summary>
    private static string Joined(string kind, bool chainFirst)
    {
        var (first, second) = chainFirst ? ("chain", "$T") : ("$T", "chain");
        var (firstAlias, secondAlias) = chainFirst ? ("ch", "p") : ("p", "ch");
        return $$"""
            { "from": { "source": "{{first}}", "alias": "{{firstAlias}}" },
              "joins": [ { "source": "{{second}}", "alias": "{{secondAlias}}", "kind": "{{kind}}", "on": { "op": "eq", "left": { "column": "p.parent_id" }, "right": { "column": "ch.id" } } } ],
              "select": [ { "name": "id", "expression": { "column": "p.id" } }, { "name": "depth", "expression": { "op": "+", "args": [ { "column": "ch.depth" }, { "value": 1 } ] } } ] }
            """;
    }

    [Theory]
    [InlineData(Dialect.PostgreSql)]
    [InlineData(Dialect.MySql)]
    [InlineData(Dialect.Oracle)]
    public void A_repeating_part_may_not_read_its_own_name_on_the_null_padded_side_of_an_outer_join(Dialect dialect)
    {
        // The side that keeps its rows is allowed: the left side of a left join, the right side of a right join.
        Assert.Empty(Mq4100(dialect, Joined("left", chainFirst: true)));
        Assert.Empty(Mq4100(dialect, Joined("right", chainFirst: false)));
        // The null-padded side is not: the right side of a left join, the left side of a right join.
        Assert.Equal(["/with/0/recursive/joins/0/source"], Mq4100(dialect, Joined("left", chainFirst: false)));
        Assert.Equal(["/with/0/recursive/from/source"], Mq4100(dialect, Joined("right", chainFirst: true)));
        // Either side of a full join is null-padded (MySQL has no full join: MQ4036).
        if (dialect != Dialect.MySql)
        {
            Assert.Equal(["/with/0/recursive/from/source"], Mq4100(dialect, Joined("full", chainFirst: true)));
            Assert.Equal(["/with/0/recursive/joins/0/source"], Mq4100(dialect, Joined("full", chainFirst: false)));
        }
    }

    [Fact]
    public void Sql_server_refuses_any_outer_join_or_nested_query_in_a_repeating_part()
    {
        const string outer = """
            { "from": { "source": "chain", "alias": "ch" },
              "joins": [ { "source": "$T", "alias": "p", "kind": "left", "on": { "op": "eq", "left": { "column": "p.parent_id" }, "right": { "column": "ch.id" } } } ],
              "select": [ { "name": "id", "expression": { "column": "ch.id" } }, { "name": "depth", "expression": { "op": "+", "args": [ { "column": "ch.depth" }, { "value": 1 } ] } } ] }
            """;
        const string nested = """
            { "from": { "source": "chain", "alias": "ch" },
              "joins": [ { "source": "$T", "alias": "p", "on": { "op": "eq", "left": { "column": "p.parent_id" }, "right": { "column": "ch.id" } } } ],
              "select": [ { "name": "id", "expression": { "column": "p.id" } }, { "name": "depth", "expression": { "op": "+", "args": [ { "column": "ch.depth" }, { "value": 1 } ] } } ],
              "where": { "exists": { "from": { "source": "$T", "alias": "x" }, "where": { "op": "eq", "left": { "column": "x.id" }, "right": { "column": "p.id" } } } } }
            """;
        Assert.Empty(Mq4100(Dialect.PostgreSql, outer));
        Assert.Empty(Mq4100(Dialect.Oracle, nested));
        Assert.Equal(["/with/0/recursive/joins/0/kind"], Mq4100(Dialect.SqlServer, outer));
        Assert.Equal(["/with/0/recursive"], Mq4100(Dialect.SqlServer, nested));
        // Its own name on the side that keeps its rows does not help: any outer join is refused there.
        Assert.Equal(["/with/0/recursive/joins/0/kind"], Mq4100(Dialect.SqlServer, Joined("right", chainFirst: false)));
        Assert.Equal(["/with/0/recursive/from/source", "/with/0/recursive/joins/0/kind"], Mq4100(Dialect.SqlServer, Joined("right", chainFirst: true)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Misplaced_named_queries_are_MQ4099_and_names_follow_MQ3001_MQ4021_and_MQ4023()
    {
        var t = new Tree();
        var id = t.Query($$"""
            {
              "name": "Misplaced",
              "with": [
                { "name": "early", "from": { "source": "late", "alias": "l" }, "select": [ { "name": "id", "expression": { "column": "l.id" } } ] },
                { "name": "self", "from": { "source": "self", "alias": "s" }, "select": [ { "name": "id", "expression": { "value": 1 } } ] },
                { "name": "Categories", "from": { "source": "{{t.Categories}}", "alias": "c" }, "select": [ { "name": "id", "expression": { "column": "c.id" } } ] },
                { "name": "late", "from": { "source": "{{t.Categories}}", "alias": "c" }, "select": [ { "name": "id", "expression": { "column": "c.id" } } ] },
                { "name": "LATE", "from": { "source": "{{t.Categories}}", "alias": "c" }, "select": [ { "name": "id", "expression": { "column": "c.id" } } ] }
              ],
              "from": { "source": "late", "alias": "a" },
              "joins": [ { "source": "nowhere", "alias": "n", "kind": "cross" } ],
              "select": [ { "name": "id", "expression": { "column": "a.id" } }, { "name": "missing", "expression": { "column": "a.missing" } } ]
            }
            """);
        var model = t.Resolve();
        var found = model.Diagnostics.Where(d => d.ElementId == id).Select(d => (d.Rule, d.JsonPointer)).OrderBy(d => d.Rule, StringComparer.Ordinal).ThenBy(d => d.JsonPointer, StringComparer.Ordinal).ToList();

        Assert.Equal(
        [
            ("MQ3001", "/with/4/name"),
            ("MQ4021", "/joins/0/source"),
            ("MQ4023", "/select/1/expression/column"),
            ("MQ4099", "/with/0/from/source"),
            ("MQ4099", "/with/1/from/source"),
            ("MQ4099", "/with/2/name"),
        ], found);
        var messages = model.Diagnostics.Where(d => d.ElementId == id).ToDictionary(d => d.JsonPointer!, d => d.Message);
        Assert.Equal("Named query 'early' of query 'Misplaced' reads named query 'late', which is listed after it: move 'late' before it.", messages["/with/0/from/source"]);
        Assert.Equal("Named query 'self' of query 'Misplaced' reads itself outside recursive: only its repeating part may read its own name.", messages["/with/1/from/source"]);
        Assert.Equal("Named query 'Categories' of query 'Misplaced' has the name of table 'categories', which the query reads, so the SQL would read one for the other: rename the named query.", messages["/with/2/name"]);
        Assert.Equal("Named query name 'LATE' is already used in query 'Misplaced' (ignoring case).", messages["/with/4/name"]);
        Assert.Equal("Query 'Misplaced' reads 'nowhere', which is no table or view of database 'main' and no named query of the query.", messages["/joins/0/source"]);
        Assert.Equal("Query 'Misplaced' names column 'missing' of 'a' (named query 'late'), which has no such column.", messages["/select/1/expression/column"]);
        Assert.Null(Single(model).Sql);
    }

    [Fact]
    public void Types_are_inferred_and_nullability_merged_from_both_parts()
    {
        var t = new Tree();
        var id = t.Query($$"""
            {
              "name": "Merged",
              "with": [ {
                "name": "chain",
                "from": { "source": "{{t.Categories}}", "alias": "c" },
                "select": [
                  { "name": "id", "expression": { "column": "c.id" } },
                  { "name": "label", "expression": { "column": "c.name" } },
                  { "name": "extra", "expression": { "null": true } },
                  { "name": "total", "expression": { "op": "+", "args": [ { "column": "c.id" }, { "value": 1.5 } ] } } ],
                "recursive": {
                  "from": { "source": "chain", "alias": "ch" },
                  "joins": [ { "source": "{{t.Categories}}", "alias": "p", "on": { "op": "eq", "left": { "column": "p.parent_id" }, "right": { "column": "ch.id" } } } ],
                  "select": [
                    { "name": "id", "expression": { "column": "p.id" } },
                    { "name": "label", "expression": { "column": "p.note" } },
                    { "name": "extra", "expression": { "column": "p.id" } },
                    { "name": "total", "expression": { "column": "ch.total" } } ] } } ],
              "from": { "source": "chain", "alias": "a" },
              "select": [
                { "name": "id", "expression": { "column": "a.id" } },
                { "name": "label", "expression": { "column": "a.label" } },
                { "name": "extra", "expression": { "column": "a.extra" } },
                { "name": "total", "expression": { "column": "a.total" } } ]
            }
            """);
        var model = t.Resolve();
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == id);
        var query = Single(model);

        // label: not null in the anchor, nullable in the repeating part; extra: the anchor's type is unknown, the repeating part's int64.
        Assert.Equal([("id", "int64", false), ("label", "string", true), ("extra", "int64", true), ("total", "decimal", false)],
            query.With[0].Columns.Select(c => (c.Name, c.Type!, c.Nullable)));
        Assert.Equal([("id", "int64", false), ("label", "string", true), ("extra", "int64", true), ("total", "decimal", false)],
            query.Select.Select(f => (f.Name, f.Type!, f.Nullable)));
        Assert.Equal(query.Select[2].NativeType, query.With[0].Recursive!.Select[2].NativeType);
    }

    [Fact]
    public void Records_carry_the_named_queries_and_the_sources_that_read_them()
    {
        var t = new Tree();
        Ancestors(t);
        var model = t.Resolve();
        var view = JsonSerializer.SerializeToNode(QueryViews.Project(Single(model)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();

        var with = Assert.Single(view["with"]!.AsArray())!.AsObject();
        Assert.Equal(["name", "query", "columns", "recursive", "union"], with.Select(p => p.Key));
        Assert.Equal(("chain", "all"), (with["name"]!.GetValue<string>(), with["union"]!.GetValue<string>()));
        Assert.False(with["query"]!.AsObject().ContainsKey("recursive"));
        Assert.Equal("chain", with["query"]!["name"]!.GetValue<string>());
        Assert.Equal("chain", with["recursive"]!["from"]!["source"]!.GetValue<string>());
        Assert.Equal(["id", "parentId", "depth"], with["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal("chain", view["from"]!["namedQuery"]!.GetValue<string>());
        Assert.False(view["joins"]![0]!.AsObject().ContainsKey("namedQuery"));

        Editor.ContractCheck.Validate(model);

        // A query without named queries leaves both out.
        var plain = new Tree();
        plain.Query($$"""{ "name": "Plain", "from": { "source": "{{plain.Categories}}", "alias": "c" }, "select": [ { "name": "id", "expression": { "column": "c.id" } } ] }""");
        var plainView = JsonSerializer.SerializeToNode(QueryViews.Project(Single(plain.Resolve())), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        Assert.False(plainView.ContainsKey("with"));
        Assert.False(plainView["from"]!.AsObject().ContainsKey("namedQuery"));
    }
}
