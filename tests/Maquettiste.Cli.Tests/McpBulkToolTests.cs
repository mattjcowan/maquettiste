using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// The bulk read tools (docs/mcp.md "Reading a large model"): <c>get_model_kinds</c>, <c>get_elements</c>, <c>get_resolved_model</c>
/// and the paged <c>get_model_index</c>, with the same bodies and paging as the editor API.
/// </summary>
public sealed class McpBulkToolTests
{
    private const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string InvoiceNotes = "01J92P0V0WKRGKH7YBKA2V30NC";
    private const string MainDatabase = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string Unknown = "01J92P0V0FJ23CGSNKM7P1W5V9";

    [Fact]
    public async Task Kinds_then_every_entity_document_in_pages_with_fields_trimmed()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);

        var kinds = await session.OkAsync("get_model_kinds");
        var entityCount = (int)kinds["kinds"]!.AsArray().Single(k => (string)k!["kind"]! == "entity")!["count"]!;
        Assert.Equal(5, entityCount);
        Assert.Null(kinds["packages"]);
        var byPackage = await session.OkAsync("get_model_kinds", new { by = "package" });
        Assert.Equal((int)kinds["total"]!, byPackage["packages"]!.AsArray().Sum(p => (int)p!["count"]!));

        var names = new List<string>();
        string? cursor = null;
        do
        {
            var page = await session.OkAsync("get_elements", new { kind = "entity", fields = new[] { "name", "attributes" }, limit = 2, cursor });
            foreach (var item in page["items"]!.AsArray())
            {
                Assert.Equal(["attributes", "id", "kind", "name"], item!["json"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
                Assert.Matches("^[0-9a-f]{64}$", (string)item["hash"]!);
                names.Add((string)item["json"]!["name"]!);
            }

            cursor = (string?)page["next"];
        }
        while (cursor is not null);

        Assert.Equal(["Customer", "Invoice", "InvoiceLine", "Payment", "Product"], names);

        var read = await session.OkAsync("get_elements", new { ids = new[] { InvoiceNotes, Unknown, McpSession.Customer } });
        Assert.Equal([McpSession.Customer, Invoice], read["items"]!.AsArray().Select(i => (string)i!["id"]!));
        Assert.Equal([Unknown], read["missing"]!.AsArray().Select(m => (string)m!));
        var whole = await session.OkAsync("get_element", new { id = McpSession.Customer });
        Assert.True(JsonNode.DeepEquals(whole["json"], read["items"]![0]!["json"]));
        Assert.Equal((string)whole["hash"]!, (string)read["items"]![0]!["hash"]!);

        var billing = await session.OkAsync("get_elements", new { package = "Billing", fields = new[] { "name" }, limit = 1000 });
        Assert.All(billing["items"]!.AsArray(), i => Assert.NotEqual("Product", (string)i!["json"]!["name"]!));
        Assert.Contains("Invoice", billing["items"]!.AsArray().Select(i => (string)i!["json"]!["name"]!));
    }

    [Fact]
    public async Task The_resolved_model_pages_entities_and_databases()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);

        var entities = await session.OkAsync("get_resolved_model", new { scope = "entities", limit = 3 });
        Assert.Equal(["Customer", "Invoice", "InvoiceLine"], entities["items"]!.AsArray().Select(i => (string)i!["name"]!));
        Assert.Empty(entities["diagnostics"]!.AsArray());
        var rest = await session.OkAsync("get_resolved_model", new { scope = "entities", limit = 3, cursor = (string)entities["next"]! });
        Assert.Equal(["Payment", "Product"], rest["items"]!.AsArray().Select(i => (string)i!["name"]!));
        Assert.Null(rest["next"]);
        var customer = entities["items"]![0]!;
        Assert.True((bool)customer["attributes"]!.AsArray().Single(a => (string)a!["name"]! == "id")!["isKey"]!);
        Assert.Equal(MainDatabase, (string)customer["mappings"]![0]!["database"]!);

        var databases = await session.OkAsync("get_resolved_model", new { scope = "databases", database = MainDatabase });
        var database = Assert.Single(databases["items"]!.AsArray())!;
        Assert.Equal("database", (string)database["kind"]!);
        var view = await session.OkAsync("get_database_view", new { id = MainDatabase });
        Assert.True(JsonNode.DeepEquals(view["view"]!["tables"], database["tables"]));
        var tables = await session.OkAsync("get_resolved_model", new { scope = "tables", limit = 1000 });
        Assert.Equal(database["tables"]!.AsArray().Count, tables["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task Queries_read_as_records_and_their_sql_previews_for_any_dialect()
    {
        const string invoicesByCustomer = "01K6QRY0000000000000000001";
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);

        var records = await session.OkAsync("get_resolved_model", new { scope = "queries" });
        Assert.Equal(["FindCustomersWithIssuedInvoices", "InvoicesByCustomer", "RevenueByMonth"], records["items"]!.AsArray().Select(i => (string)i!["name"]!));
        var record = records["items"]!.AsArray().Single(i => (string)i!["id"]! == invoicesByCustomer)!;
        var view = await session.OkAsync("get_database_view", new { id = MainDatabase });
        Assert.True(JsonNode.DeepEquals(view["view"]!["queries"]!.AsArray().Single(q => (string)q!["id"]! == invoicesByCustomer), record["query"]));
        Assert.Equal(3, (await session.OkAsync("get_elements", new { kind = "query" }))["items"]!.AsArray().Count);

        var preview = (await session.OkAsync("preview_query_sql", new { id = invoicesByCustomer }))["preview"]!;
        Assert.Equal("postgresql", (string)preview["dialect"]!);
        Assert.Equal((string)record["query"]!["sql"]!, (string)preview["sql"]!);
        Assert.Equal(["customerId", "statuses", "offset", "limit"], preview["parameters"]!.AsArray().Select(p => (string)p!));
        var lines = Assert.Single(preview["collections"]!.AsArray())!;
        Assert.Equal(["mq_keys0"], lines["parameters"]!.AsArray().Select(p => (string)p!));

        var server = (await session.OkAsync("preview_query_sql", new { id = invoicesByCustomer, dialect = "sqlserver", placeholder = "$" }))["preview"]!;
        Assert.EndsWith("OFFSET $3 ROWS FETCH NEXT $4 ROWS ONLY", (string)server["sql"]!, StringComparison.Ordinal);
        var mysql = await session.OkAsync("preview_query_sql", new { id = "01K6QRY0000000000000000002", dialect = "mysql" });
        Assert.Equal("MQ4029", (string)Assert.Single(mysql["diagnostics"]!.AsArray(), d => (string)d!["rule"]! == "MQ4029")!["rule"]!);

        Assert.Equal("not-a-query", (await session.ErrorAsync("preview_query_sql", new { id = Invoice })).Code);
        Assert.Equal("not-found", (await session.ErrorAsync("preview_query_sql", new { id = Unknown })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("preview_query_sql", new { id = invoicesByCustomer, dialect = "cobol" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("preview_query_sql", new { id = invoicesByCustomer, lists = "all" })).Code);
    }

    [Fact]
    public async Task The_index_pages_on_request_and_bad_paging_arguments_are_bad_requests()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);

        var whole = (await session.OkAsync("get_model_index")).AsArray();
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await session.OkAsync("get_model_index", new { limit = 10, cursor });
            ids.AddRange(page["items"]!.AsArray().Select(i => (string)i!["id"]!));
            cursor = (string?)page["next"];
        }
        while (cursor is not null);

        Assert.Equal(whole.Select(r => (string)r!["id"]!).Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
        Assert.Equal(ids.Count, ids.Distinct().Count());

        Assert.Equal("bad-request", (await session.ErrorAsync("get_model_index", new { limit = 0 })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_elements", new { limit = 1001 })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_elements", new { cursor = "nope" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_elements", new { fields = "name" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_resolved_model", new { scope = "widgets" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_resolved_model", new { limit = "ten" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_model_kinds", new { by = "tag" })).Code);
        Assert.Equal("not-a-database", (await session.ErrorAsync("get_resolved_model", new { database = Invoice })).Code);
        Assert.Equal("not-found", (await session.ErrorAsync("get_resolved_model", new { database = Unknown })).Code);
    }
}
