using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Model;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>E5 to E5e over HTTP and realtime (explorer-redesign.md section 4.1), each response checked against the contract.</summary>
public sealed class ExplorerEndpointTests
{
    private const string Unknown = "01J92P0V0FJ23CGSNKM7P1W5V9";

    [Fact]
    public async Task The_index_has_an_etag_answers_304_for_it_and_changes_it_after_a_save()
    {
        await using var host = EditorHost.Create();

        var first = await host.GetAsync("/api/model/index");
        var tag = first.Headers.ETag.ToString();
        var again = await host.GetAsync("/api/model/index", r => r.Header("If-None-Match", tag));
        var other = await host.GetAsync("/api/model/index", r => r.Header("If-None-Match", "\"" + new string('0', 64) + "\""));

        Assert.Equal(200, first.Status);
        Assert.Matches("^\"[0-9a-f]{64}\"$", tag);
        Assert.Equal("no-cache", first.Headers.CacheControl.ToString());
        Assert.Equal(304, again.Status);
        Assert.Empty(again.Body);
        Assert.Equal(tag, again.Headers.ETag.ToString());
        Contract.AssertResponse(again, "/api/model/index");
        Assert.Equal(200, other.Status);

        var invoice = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId);
        var json = invoice.Json["json"]!.AsObject();
        json["attributes"]![5]!["name"] = "remarks";
        var saved = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.InvoiceId, json, r => r.IfMatch(invoice.Json["hash"]!.GetValue<string>()));
        Assert.Equal(200, saved.Status);
        var after = await host.GetAsync("/api/model/index", r => r.Header("If-None-Match", tag));

        Assert.Equal(200, after.Status);
        Assert.NotEqual(tag, after.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Index_rows_carry_relation_ends_diagram_member_counts_and_physical_owners()
    {
        await using var host = EditorHost.Create();

        var response = await host.GetAsync("/api/model/index");

        Contract.AssertResponse(response, "/api/model/index");
        var rows = response.Json.AsArray().Select(r => r!.AsObject()).ToList();
        var contains = rows.Single(r => r["id"]!.GetValue<string>() == EditorHost.ContainsRelationId);
        Assert.All(contains["ends"]!.AsArray(), end => Assert.False(string.IsNullOrEmpty(end!["entity"]!.GetValue<string>())));
        Assert.Equal(2, contains["ends"]!.AsArray().Count);
        var diagram = rows.Single(r => r["id"]!.GetValue<string>() == EditorHost.OverviewDiagramId);
        Assert.True(diagram["memberCount"]!.GetValue<int>() > 0);
        Assert.All(rows.Where(r => r["kind"]!.GetValue<string>() is "table" or "view" or "sequence" or "mapping"),
            r => Assert.Equal(EditorHost.MainDatabaseId, r["database"]!.GetValue<string>()));
        Assert.All(rows.Where(r => r["kind"]!.GetValue<string>() == "entity"), r => Assert.False(r.ContainsKey("ends") || r.ContainsKey("database")));
    }

    [Fact]
    public async Task Reading_many_elements_returns_each_document_once_and_the_missing_ids()
    {
        await using var host = EditorHost.Create();

        var response = await host.SendJsonAsync("POST", "/api/model/elements/read",
            new JsonObject { ["ids"] = new JsonArray(EditorHost.PaymentId, EditorHost.InvoiceNotesAttributeId, Unknown, EditorHost.InvoiceId) });

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/model/elements/read");
        Assert.Equal([EditorHost.PaymentId, EditorHost.InvoiceId],
            response.Json["elements"]!.AsArray().Select(e => e!["element"]!["id"]!.GetValue<string>()));
        Assert.Equal([Unknown], response.Json["missing"]!.AsArray().Select(e => e!.GetValue<string>()));
        var single = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId);
        Assert.Equal(single.Json["hash"]!.GetValue<string>(), response.Json["elements"]![1]!["hash"]!.GetValue<string>());
        Recorder.Json("elements-read.json", response);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":[\"not-an-id\"]}")]
    [InlineData("")]
    public async Task A_read_without_valid_ids_is_a_bad_request(string body)
    {
        await using var host = EditorHost.Create();

        var response = await host.SendJsonAsync("POST", "/api/model/elements/read", body.Length == 0 ? System.Array.Empty<byte>() : body);

        Assert.Equal(400, response.Status);
        Contract.AssertResponse(response, "/api/model/elements/read");
    }

    [Fact]
    public async Task A_read_of_more_than_200_ids_is_a_bad_request()
    {
        await using var host = EditorHost.Create();
        var ids = new JsonArray([.. Enumerable.Repeat(EditorHost.InvoiceId, ModelReads.MaxReadIds + 1).Select(id => (JsonNode)JsonValue.Create(id))]);

        var response = await host.SendJsonAsync("POST", "/api/model/elements/read", new JsonObject { ["ids"] = ids });

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-request", response.ProblemCode);
    }

    [Fact]
    public async Task Table_summaries_answer_for_a_database_and_404_otherwise()
    {
        await using var host = EditorHost.Create();

        var tables = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/tables");
        var notADatabase = await host.GetAsync("/api/databases/" + EditorHost.InvoiceId + "/tables");
        var unknown = await host.GetAsync("/api/databases/" + Unknown + "/tables");

        Assert.Equal(200, tables.Status);
        Contract.AssertResponse(tables, "/api/databases/{id}/tables");
        Assert.False(tables.Json["partial"]!.GetValue<bool>());
        var customers = tables.Json["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "customers")!;
        Assert.Equal(6, customers["columnCount"]!.GetValue<int>());
        Assert.False(customers.AsObject().ContainsKey("columns"));
        Assert.Equal(404, notADatabase.Status);
        Assert.Equal("not-a-database", notADatabase.ProblemCode);
        Contract.AssertResponse(notADatabase, "/api/databases/{id}/tables");
        Assert.Equal(404, unknown.Status);
        Recorder.Json("database-tables-main.json", tables);
    }

    [Fact]
    public async Task One_table_answers_with_its_columns_and_null_for_an_unknown_key()
    {
        await using var host = EditorHost.Create();
        var tables = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/tables");
        var key = tables.Json["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "customers")!["key"]!.GetValue<string>();

        var table = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/tables/" + Uri.EscapeDataString(key));
        var missing = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/tables/no-such-table");
        var notADatabase = await host.GetAsync("/api/databases/" + EditorHost.InvoiceId + "/tables/" + Uri.EscapeDataString(key));

        Assert.Equal(200, table.Status);
        Contract.AssertResponse(table, "/api/databases/{id}/tables/{key}");
        Assert.False(table.Json["partial"]!.GetValue<bool>());
        Assert.Equal("customers", table.Json["table"]!["name"]!.GetValue<string>());
        Assert.Equal(6, table.Json["table"]!["columns"]!.AsArray().Count);
        Assert.Equal(200, missing.Status);
        Contract.AssertResponse(missing, "/api/databases/{id}/tables/{key}");
        Assert.Null(missing.Json["table"]);
        Assert.Equal(404, notADatabase.Status);
        Assert.Equal("not-a-database", notADatabase.ProblemCode);
        Recorder.Json("database-table-customers.json", table);
    }

    [Fact]
    public async Task Table_summaries_on_a_model_with_errors_are_partial()
    {
        await using var host = EditorHost.Create();
        var payment = (await host.Store.GetElementAsync(EditorHost.PaymentId, EditorHost.Ct))!;
        var node = JsonNode.Parse(payment.Json.GetRawText())!.AsObject();
        node.Remove("key");
        await File.WriteAllTextAsync(host.PathOf(payment.Path), node.ToJsonString(), EditorHost.Ct);
        await host.Store.RescanAsync(false, EditorHost.Ct); // what the watcher does

        var tables = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/tables");

        Assert.Equal(200, tables.Status);
        Contract.AssertResponse(tables, "/api/databases/{id}/tables");
        Assert.True(tables.Json["partial"]!.GetValue<bool>());
        Assert.NotEmpty(tables.Json["diagnostics"]!.AsArray());
        var entities = tables.Json["tables"]!.AsArray().Select(t => t!["entityId"]?.GetValue<string>()).ToList();
        Assert.DoesNotContain(EditorHost.PaymentId, entities);
        Assert.Contains(EditorHost.CustomerId, entities);
    }

    [Fact]
    public async Task Model_changed_carries_each_changed_elements_summary_and_validates()
    {
        await using var host = EditorHost.Create();
        await host.Store.LoadAsync(EditorHost.Ct);
        var invoice = (await host.Store.GetElementAsync(EditorHost.InvoiceId, EditorHost.Ct))!;

        await host.Events.OnModelChangedAsync(
            new ChangeSet([new ElementChange(EditorHost.InvoiceId, "entity", invoice.Path, invoice.Hash), new ElementChange(Unknown, "entity", "gone.json", invoice.Hash)],
                [EditorHost.PaymentId], ChangeSource.Disk, false), EditorHost.Ct);

        var payload = Assert.Single(host.Published("model.changed")).Payload;
        Contract.AssertEvent("model.changed", payload);
        var changed = payload["changed"]!.AsArray();
        Assert.Equal("Invoice", changed[0]!["summary"]!["name"]!.GetValue<string>());
        Assert.Equal(invoice.Hash, changed[0]!["summary"]!["hash"]!.GetValue<string>());
        Assert.False(changed[1]!.AsObject().ContainsKey("summary"));
        Assert.False(payload["truncated"]!.GetValue<bool>());
        Assert.False(payload["isEmpty"]!.GetValue<bool>());
    }

    [Fact]
    public void A_model_changed_payload_over_the_limit_drops_trailing_items_and_says_truncated()
    {
        var rows = Enumerable.Range(0, 400).Select(i => new ElementSummary(Id(i), "entity", "Entity" + i, null, [], new string('a', 64), $".maquettiste/model/entities/e{i}.json", null, [])).ToList();
        var set = new ChangeSet([.. rows.Select(r => new ElementChange(r.Id, r.Kind, r.Path, r.Hash))], [Unknown], ChangeSource.Disk, false);

        var full = ModelChangedEvent.Create(set, rows, Api.MaxEventBytes);
        var cut = ModelChangedEvent.Create(set, rows, 20_000);

        Assert.False(full.Truncated);
        Assert.Equal(400, full.Changed.Count);
        Assert.True(cut.Truncated);
        Assert.InRange(cut.Changed.Count, 1, 399);
        Assert.Empty(cut.Deleted);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cut, Api.JsonOptions).Length <= 20_000);
        Assert.All(cut.Changed, c => Assert.Equal(c.Id, c.Summary!.Id));
    }

    private static string Id(int i) => "01J92P0V0FJ23CGSNKM7P1" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}
