using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>
/// The bulk reads for external systems: documents in pages (<c>GET /api/model/elements</c>), the kinds and their counts
/// (<c>GET /api/model/kinds</c>), the resolved model as flat records (<c>GET /api/model/resolved</c>) and the paged index, each response
/// checked against the contract.
/// </summary>
public sealed class BulkReadTests
{
    private const string Unknown = "01J92P0V0FJ23CGSNKM7P1W5V9";

    [Fact]
    public async Task Element_pages_cover_every_document_once_in_kind_name_id_order()
    {
        await using var host = EditorHost.Create();
        var index = (await host.Store.GetSnapshotAsync(EditorHost.Ct)).Summaries();

        var items = new List<JsonNode>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var response = await host.GetAsync("/api/model/elements?limit=7" + (cursor is null ? "" : "&cursor=" + cursor));
            Assert.Equal(200, response.Status);
            Contract.AssertResponse(response, "/api/model/elements");
            Assert.True(response.Json["items"]!.AsArray().Count <= 7);
            items.AddRange(response.Json["items"]!.AsArray().Select(i => i!));
            cursor = response.Json["next"]?.GetValue<string>();
            pages++;
        }
        while (cursor is not null);

        var expected = index.OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).Select(s => s.Id);
        Assert.Equal(expected, items.Select(i => i["id"]!.GetValue<string>()));
        Assert.Equal((index.Count + 6) / 7, pages);
        var invoice = items.Single(i => i["id"]!.GetValue<string>() == EditorHost.InvoiceId);
        var single = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId);
        Assert.Equal(single.Json["hash"]!.GetValue<string>(), invoice["hash"]!.GetValue<string>());
        Assert.Equal(single.Json["path"]!.GetValue<string>(), invoice["path"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(single.Json["json"], invoice["json"]));

        // Without a limit, a page holds 100.
        var all = await host.GetAsync("/api/model/elements");
        Assert.Equal(Math.Min(100, index.Count), all.Json["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task Elements_take_the_index_filters_ids_and_fields()
    {
        await using var host = EditorHost.Create();

        var entities = await host.GetAsync("/api/model/elements?kind=entity&fields=name,attributes");
        Contract.AssertResponse(entities, "/api/model/elements");
        var rows = entities.Json["items"]!.AsArray();
        Assert.Equal(["Customer", "CustomerNote", "Invoice", "InvoiceLine", "InvoiceNote", "Payment", "Product", "RevenueMonth"], rows.Select(r => r!["json"]!["name"]!.GetValue<string>()));
        Assert.All(rows, r => Assert.Equal(["attributes", "id", "kind", "name"], r!["json"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)));
        Assert.Null(entities.Json["next"]);

        var billing = await host.GetAsync("/api/model/elements?kind=entity&package=billing");
        Assert.DoesNotContain("Product", billing.Json["items"]!.AsArray().Select(r => r!["json"]!["name"]!.GetValue<string>()));
        Assert.Equal(7, billing.Json["items"]!.AsArray().Count);
        var byId = await host.GetAsync("/api/model/elements?kind=entity&package=" + EditorHost.BillingPackageId);
        Assert.True(JsonNode.DeepEquals(billing.Json["items"], byId.Json["items"]));
        // The name filter matches Customer, CustomerNote and the two queries whose names say customer (FindCustomersWithIssuedInvoices, InvoicesByCustomer).
        Assert.Equal([EditorHost.CustomerId, "01K6BND0000000000000000020", "01K6QRY0000000000000000003", "01K6QRY0000000000000000001"],
            (await host.GetAsync("/api/model/elements?query=CUSTOM")).Json["items"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));
        Assert.Contains(EditorHost.CustomerId, (await host.GetAsync("/api/model/elements?tag=pii")).Json["items"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));
        Assert.Contains(EditorHost.InvoiceId, (await host.GetAsync("/api/model/elements?stereotype=audited")).Json["items"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));

        // ids: a sub-element id reads the element that holds it, each element once; unknown ids are listed in missing.
        var read = await host.GetAsync($"/api/model/elements?ids={EditorHost.InvoiceNotesAttributeId},{Unknown},{EditorHost.InvoiceId},{EditorHost.CustomerId}&fields=name");
        Contract.AssertResponse(read, "/api/model/elements");
        Assert.Equal([EditorHost.CustomerId, EditorHost.InvoiceId], read.Json["items"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));
        Assert.Equal([Unknown], read.Json["missing"]!.AsArray().Select(m => m!.GetValue<string>()));
        // ids and filters combine.
        var narrowed = await host.GetAsync($"/api/model/elements?ids={EditorHost.InvoiceId},{EditorHost.ContainsRelationId}&kind=relation");
        Assert.Equal([EditorHost.ContainsRelationId], narrowed.Json["items"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Bad_limits_cursors_and_ids_are_400()
    {
        await using var host = EditorHost.Create();
        var tooMany = string.Join(",", Enumerable.Repeat(EditorHost.InvoiceId, 1001));
        foreach (var path in new[]
        {
            "/api/model/elements?limit=0", "/api/model/elements?limit=1001", "/api/model/elements?limit=ten", "/api/model/elements?cursor=not-a-cursor",
            "/api/model/elements?cursor=" + Convert.ToBase64String("hello"u8.ToArray()).TrimEnd('='), "/api/model/elements?ids=invoice", "/api/model/elements?ids=" + tooMany,
            "/api/model/index?limit=0", "/api/model/index?cursor=***", "/api/model/kinds?by=tag", "/api/model/resolved?scope=widgets",
            "/api/model/resolved?limit=5000", "/api/model/resolved?cursor=nope",
        })
        {
            var response = await host.GetAsync(path);
            Assert.True(response.Status == 400, response.ToString());
            Assert.Equal("bad-request", response.ProblemCode);
            Contract.AssertResponse(response, path[..path.IndexOf('?', StringComparison.Ordinal)]);
        }

        var maximum = await host.GetAsync("/api/model/elements?limit=1000");
        Assert.Equal(200, maximum.Status);
    }

    [Fact]
    public async Task A_change_between_pages_is_tolerated_without_duplicates()
    {
        await using var host = EditorHost.Create();
        var first = await host.GetAsync("/api/model/elements?kind=entity&limit=2&fields=name");
        Assert.Equal(["Customer", "CustomerNote"], Names(first));
        var cursor = first.Json["next"]!.GetValue<string>();

        // One entity sorts before the cursor and one after it.
        foreach (var (name, attribute) in new[] { ("Aardvark", "01M3P0000000000000000000B1"), ("Zebra", "01M3P0000000000000000000B2") })
        {
            var created = await host.SendJsonAsync("POST", "/api/model/elements", new JsonObject
            {
                ["kind"] = "entity",
                ["name"] = name,
                ["package"] = EditorHost.BillingPackageId,
                ["key"] = new JsonObject { ["attributes"] = new JsonArray(attribute) },
                ["attributes"] = new JsonArray(new JsonObject { ["id"] = attribute, ["name"] = "id", ["type"] = "uuid", ["required"] = true }),
            });
            Assert.True(created.Status == 201, created.ToString());
        }

        var second = await host.GetAsync("/api/model/elements?kind=entity&limit=2&fields=name&cursor=" + cursor);
        Assert.Equal(200, second.Status);
        Assert.Equal(["Invoice", "InvoiceLine"], Names(second));
        var rest = new List<string>();
        for (var page = second; page.Json["next"] is { } next;)
        {
            page = await host.GetAsync("/api/model/elements?kind=entity&limit=2&fields=name&cursor=" + next.GetValue<string>());
            rest.AddRange(Names(page));
        }

        Assert.Equal(["InvoiceNote", "Payment", "Product", "RevenueMonth", "Zebra"], rest);
    }

    [Fact]
    public async Task Kinds_count_the_index_and_per_package()
    {
        await using var host = EditorHost.Create();
        var index = (await host.Store.GetSnapshotAsync(EditorHost.Ct)).Summaries();

        var kinds = await host.GetAsync("/api/model/kinds");
        Assert.Equal(200, kinds.Status);
        Contract.AssertResponse(kinds, "/api/model/kinds");
        Assert.Equal(index.Count, kinds.Json["total"]!.GetValue<int>());
        Assert.Null(kinds.Json["packages"]);
        var counts = kinds.Json["kinds"]!.AsArray().ToDictionary(k => k!["kind"]!.GetValue<string>(), k => k!["count"]!.GetValue<int>());
        Assert.Equal(8, counts["entity"]);
        Assert.Equal(index.GroupBy(s => s.Kind).Select(g => g.Key).Order(StringComparer.Ordinal), counts.Keys);
        Assert.Equal(index.Count, counts.Values.Sum());

        var byPackage = await host.GetAsync("/api/model/kinds?by=package");
        Contract.AssertResponse(byPackage, "/api/model/kinds");
        var packages = byPackage.Json["packages"]!.AsArray();
        Assert.Null(packages[0]!["package"]);
        Assert.Equal(index.Count, packages.Sum(p => p!["count"]!.GetValue<int>()));
        var billing = packages.Single(p => p!["package"]?.GetValue<string>() == EditorHost.BillingPackageId)!;
        Assert.Equal("Billing", billing["name"]!.GetValue<string>());
        Assert.Equal(7, billing["kinds"]!.AsArray().Single(k => k!["kind"]!.GetValue<string>() == "entity")!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_index_pages_and_filters_on_request_and_stays_whole_otherwise()
    {
        await using var host = EditorHost.Create();
        var index = (await host.Store.GetSnapshotAsync(EditorHost.Ct)).Summaries();

        var rows = new List<string>();
        var path = "/api/model/index?limit=10";
        var pages = 0;
        while (path is not null)
        {
            var response = await host.GetAsync(path);
            Assert.Equal(200, response.Status);
            Contract.AssertResponse(response, "/api/model/index");
            Assert.Null(response.ETag);
            Assert.Equal("no-store", response.Headers.CacheControl.ToString());
            rows.AddRange(response.Json.AsArray().Select(r => r!["id"]!.GetValue<string>()));
            var link = response.Headers.Link.ToString();
            path = link.Length == 0 ? null : link[1..link.IndexOf('>', StringComparison.Ordinal)];
            if (path is not null)
                Assert.EndsWith(">; rel=\"next\"", link, StringComparison.Ordinal);
            pages++;
        }

        Assert.Equal(index.OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).Select(s => s.Id), rows);
        Assert.Equal((index.Count + 9) / 10, pages);

        var entities = await host.GetAsync("/api/model/index?kind=entity&package=Billing");
        Contract.AssertResponse(entities, "/api/model/index");
        Assert.Equal(7, entities.Json.AsArray().Count);
        Assert.Null(entities.ETag);

        var whole = await host.GetAsync("/api/model/index");
        Assert.NotNull(whole.ETag);
        Assert.Equal(index.Count, whole.Json.AsArray().Count);
    }

    [Fact]
    public async Task The_resolved_entities_carry_resolved_attributes_keys_relations_and_mappings()
    {
        await using var host = EditorHost.Create();

        var response = await host.GetAsync("/api/model/resolved?scope=entities");

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/model/resolved");
        Assert.Empty(response.Json["diagnostics"]!.AsArray());
        var items = response.Json["items"]!.AsArray();
        Assert.Equal(["Customer", "CustomerNote", "Invoice", "InvoiceLine", "InvoiceNote", "Payment", "Product", "RevenueMonth"], items.Select(i => i!["name"]!.GetValue<string>()));
        var customer = items[0]!;
        Assert.Equal("entity", customer["kind"]!.GetValue<string>());
        var email = customer["attributes"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "email")!;
        Assert.Equal("scalar", email["type"]!["kind"]!.GetValue<string>());
        Assert.Equal("string", email["type"]!["builtin"]!.GetValue<string>());
        Assert.Equal(254, email["length"]!.GetValue<int>());
        Assert.True(customer["attributes"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "id")!["isKey"]!.GetValue<bool>());
        Assert.False(email["isKey"]!.GetValue<bool>());
        Assert.NotEmpty(customer["relations"]!.AsArray());
        var mapping = Assert.Single(customer["mappings"]!.AsArray())!;
        Assert.Equal(EditorHost.MainDatabaseId, mapping["database"]!.GetValue<string>());
        Assert.Contains(customer["key"]!["attributes"]![0]!.GetValue<string>(), mapping["columns"]!.AsArray().Select(c => c!["attributePath"]!.GetValue<string>()));
        Assert.Contains("aggregate-root", customer["stereotypes"]!.AsArray().Select(s => s!.GetValue<string>()));

        // The page records the engine's answer for the editor's mock layer (all scopes, one page).
        var all = await host.GetAsync("/api/model/resolved?limit=1000");
        Contract.AssertResponse(all, "/api/model/resolved");
        Recorder.Json("getResolvedModel.json", all);
    }

    [Fact]
    public async Task Resolved_scopes_page_and_narrow_to_a_database()
    {
        await using var host = EditorHost.Create();

        var records = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var response = await host.GetAsync("/api/model/resolved?limit=4" + (cursor is null ? "" : "&cursor=" + cursor));
            Assert.Equal(200, response.Status);
            Contract.AssertResponse(response, "/api/model/resolved");
            records.AddRange(response.Json["items"]!.AsArray().Select(i => i!));
            cursor = response.Json["next"]?.GetValue<string>();
        }
        while (cursor is not null);

        var keys = records.Select(r => (Kind: r["kind"]!.GetValue<string>(), Name: r["name"]!.GetValue<string>(), Id: r["id"]!.GetValue<string>())).ToList();
        Assert.Equal(keys.OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Name, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal), keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(["database", "entity", "enum", "package", "relation", "scalar-type", "value-object"], keys.Select(k => k.Kind).Distinct());
        Assert.DoesNotContain("table", keys.Select(k => k.Kind));

        var view = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/view");
        var database = records.Single(r => r["kind"]!.GetValue<string>() == "database");
        Assert.True(JsonNode.DeepEquals(view.Json["view"]!["tables"], database["tables"]));

        var tables = await host.GetAsync("/api/model/resolved?scope=tables&database=" + EditorHost.MainDatabaseId + "&limit=1000");
        Contract.AssertResponse(tables, "/api/model/resolved");
        var tableRecords = tables.Json["items"]!.AsArray();
        Assert.Equal(view.Json["view"]!["tables"]!.AsArray().Count, tableRecords.Count);
        Assert.All(tableRecords, t => Assert.Equal(EditorHost.MainDatabaseId, t!["database"]!.GetValue<string>()));
        Assert.All(tableRecords, t => Assert.Equal(t!["id"]!.GetValue<string>(), t["table"]!["key"]!.GetValue<string>()));

        var mapped = await host.GetAsync("/api/model/resolved?scope=entities&database=" + EditorHost.MainDatabaseId);
        Assert.Equal(8, mapped.Json["items"]!.AsArray().Count); // the five mapped entities and the three bound ones

        var notADatabase = await host.GetAsync("/api/model/resolved?database=" + EditorHost.InvoiceId);
        Assert.Equal(404, notADatabase.Status);
        Assert.Equal("not-a-database", notADatabase.ProblemCode);
        Contract.AssertResponse(notADatabase, "/api/model/resolved");
        var missing = await host.GetAsync("/api/model/resolved?database=" + Unknown);
        Assert.Equal("not-found", missing.ProblemCode);
    }

    [Fact]
    public async Task Queries_read_as_records_and_their_sql_comes_back_per_dialect()
    {
        const string invoicesByCustomer = "01K6QRY0000000000000000001";
        await using var host = EditorHost.Create();

        var records = await host.GetAsync("/api/model/resolved?scope=queries");
        Contract.AssertResponse(records, "/api/model/resolved");
        Assert.Equal(["FindCustomersWithIssuedInvoices", "InvoicesByCustomer", "RevenueByMonth"], records.Json["items"]!.AsArray().Select(i => i!["name"]!.GetValue<string>()));
        var view = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/view");
        Contract.AssertResponse(view, "/api/databases/{id}/view");
        Assert.Equal(3, view.Json["view"]!["queries"]!.AsArray().Count);

        var sql = await host.GetAsync("/api/model/queries/" + invoicesByCustomer + "/sql");
        Assert.Equal(200, sql.Status);
        Contract.AssertResponse(sql, "/api/model/queries/{id}/sql");
        Assert.Equal("postgresql", sql.Json["preview"]!["dialect"]!.GetValue<string>());
        Assert.Contains("LIMIT @limit OFFSET @offset", sql.Json["preview"]!["sql"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Single(sql.Json["preview"]!["collections"]!.AsArray());

        var oracle = await host.GetAsync("/api/model/queries/" + invoicesByCustomer + "/sql?dialect=oracle&placeholder=:");
        Contract.AssertResponse(oracle, "/api/model/queries/{id}/sql");
        Assert.EndsWith("FETCH NEXT :limit ROWS ONLY", oracle.Json["preview"]!["sql"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal("not-a-query", (await host.GetAsync("/api/model/queries/" + EditorHost.InvoiceId + "/sql")).ProblemCode);
        Assert.Equal("not-found", (await host.GetAsync("/api/model/queries/" + Unknown + "/sql")).ProblemCode);
        Assert.Equal("bad-request", (await host.GetAsync("/api/model/queries/" + invoicesByCustomer + "/sql?dialect=cobol")).ProblemCode);
        Assert.Equal("bad-request", (await host.GetAsync("/api/model/queries/" + invoicesByCustomer + "/sql?placeholder=%3F")).ProblemCode);
    }

    [Fact]
    public async Task Bindings_show_on_the_tables_their_sql_comes_back_and_materialize_previews_without_writing()
    {
        const string invoiceNote = "01K6BND0000000000000000010", binding = "01K6BND0000000000000000015", notes = "01K6BND0000000000000000001";
        await using var host = EditorHost.Create();

        var view = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/view");
        Contract.AssertResponse(view, "/api/databases/{id}/view");
        var table = view.Json["view"]!["tables"]!.AsArray().Single(t => t!["key"]!.GetValue<string>() == notes)!;
        Assert.Equal(["CustomerNote", "InvoiceNote"], table["boundBy"]!.AsArray().Select(b => b!["entityName"]!.GetValue<string>()));
        Assert.Equal("invoice", table["boundBy"]![1]!["constants"]![0]!["value"]!.GetValue<string>());
        Assert.Single(view.Json["view"]!["queries"]!.AsArray().Single(q => q!["name"]!.GetValue<string>() == "RevenueByMonth")!["boundBy"]!.AsArray());

        var sql = await host.GetAsync("/api/model/entities/" + invoiceNote + "/bindings/" + binding + "/sql?dialect=sqlite");
        Assert.Equal(200, sql.Status);
        Contract.AssertResponse(sql, "/api/model/entities/{id}/bindings/{bindingId}/sql");
        Assert.Equal("DELETE FROM notes\nWHERE id = @id AND entity_type = 'invoice'", sql.Json["preview"]!["delete"]!["sql"]!.GetValue<string>());
        Assert.Equal("not-a-binding", (await host.GetAsync("/api/model/entities/" + invoiceNote + "/bindings/" + Unknown + "/sql")).ProblemCode);
        Assert.Equal("not-an-entity", (await host.GetAsync("/api/model/entities/" + notes + "/bindings/" + binding + "/sql")).ProblemCode);
        Assert.Equal("bad-request", (await host.GetAsync("/api/model/entities/" + invoiceNote + "/bindings/" + binding + "/sql?dialect=cobol")).ProblemCode);

        var status = await host.GetAsync("/api/model/databases/" + EditorHost.MainDatabaseId + "/materialize");
        Assert.Equal(200, status.Status);
        Contract.AssertResponse(status, "/api/model/databases/{id}/materialize");
        Assert.Contains(status.Json["entities"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "Invoice" && e["projected"]!.GetValue<bool>());
        Assert.Equal("not-a-database", (await host.GetAsync("/api/model/databases/" + EditorHost.InvoiceId + "/materialize")).ProblemCode);

        var preview = await host.SendJsonAsync("POST", "/api/model/databases/" + EditorHost.MainDatabaseId + "/materialize/preview",
            new JsonObject { ["op"] = "materialize-tables", ["entities"] = new JsonArray(EditorHost.InvoiceId) });
        Assert.Equal(200, preview.Status);
        Contract.AssertResponse(preview, "/api/model/databases/{id}/materialize/preview");
        Assert.True(preview.Json["valid"]!.GetValue<bool>());
        Assert.Equal("table", preview.Json["updates"]![0]!["kind"]!.GetValue<string>());
        var refused = await host.SendJsonAsync("POST", "/api/model/databases/" + EditorHost.MainDatabaseId + "/materialize/preview",
            new JsonObject { ["op"] = "materialize-tables", ["entities"] = new JsonArray(invoiceNote) });
        Assert.Equal("MQ4055", refused.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());
        Assert.Equal("bad-request", (await host.SendJsonAsync("POST", "/api/model/databases/" + EditorHost.MainDatabaseId + "/materialize/preview",
            new JsonObject { ["op"] = "materialize-entities" })).ProblemCode);
    }

    [Fact]
    public async Task Resolved_processes_list_every_state_transition_and_event_by_id()
    {
        await using var host = EditorHost.CreateModel("processes");

        var response = await host.GetAsync("/api/model/resolved?scope=processes");

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/model/resolved");
        var processes = response.Json["items"]!.AsArray();
        Assert.NotEmpty(processes);
        foreach (var process in processes)
        {
            var states = process!["states"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(states);
            Assert.Contains(process["initial"]!.GetValue<string>(), states);
            foreach (var transition in process["transitions"]!.AsArray())
            {
                Assert.Contains(transition!["source"]!.GetValue<string>(), states);
                Assert.All(transition["targets"]!.AsArray(), t => Assert.Contains(t!.GetValue<string>(), states));
            }

            var events = process["events"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            Assert.All(process["transitions"]!.AsArray().Where(t => t!["event"] is not null), t => Assert.Contains(t!["event"]!.GetValue<string>(), events));
        }

        var all = await host.GetAsync("/api/model/resolved?limit=1000");
        Contract.AssertResponse(all, "/api/model/resolved");
        Assert.Contains("actor", all.Json["items"]!.AsArray().Select(i => i!["kind"]!.GetValue<string>()));
        Assert.Contains("scenario", all.Json["items"]!.AsArray().Select(i => i!["kind"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_model_with_errors_resolves_to_no_records_and_the_errors()
    {
        await using var host = EditorHost.Create();
        var payment = (await host.Store.GetElementAsync(EditorHost.PaymentId, EditorHost.Ct))!;
        var node = JsonNode.Parse(payment.Json.GetRawText())!.AsObject();
        node.Remove("key");
        await File.WriteAllTextAsync(host.PathOf(payment.Path), node.ToJsonString(), EditorHost.Ct);

        var response = await host.GetAsync("/api/model/resolved?scope=entities");

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/model/resolved");
        Assert.Empty(response.Json["items"]!.AsArray());
        Assert.Null(response.Json["next"]);
        Assert.NotEmpty(response.Json["diagnostics"]!.AsArray());
        Assert.All(response.Json["diagnostics"]!.AsArray(), d => Assert.Equal("error", d!["severity"]!.GetValue<string>()));
        // The documents are still there to read.
        Assert.Equal(8, (await host.GetAsync("/api/model/elements?kind=entity")).Json["items"]!.AsArray().Count);
    }

    private static string[] Names(TestResponse response) => [.. response.Json["items"]!.AsArray().Select(r => r!["json"]!["name"]!.GetValue<string>())];
}
