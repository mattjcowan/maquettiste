using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;
using Maquettiste.Testing;

namespace Maquettiste.Functions.Tests;

/// <summary>Create, save, delete, batch, validation, diagrams and the database view (phase2-design.md section 3.7), against the contract.</summary>
public sealed class ModelWriteTests
{
    private static async Task<(JsonObject Json, string Hash)> LoadAsync(EditorHost host, string id)
    {
        var response = await host.GetAsync("/api/model/elements/" + id);
        Assert.Equal(200, response.Status);
        return (response.Json["json"]!.AsObject(), response.Json["hash"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_answers_201_with_location_etag_and_the_new_document()
    {
        await using var host = EditorHost.Create();
        var body = new JsonObject
        {
            ["kind"] = "entity",
            ["name"] = "Refund",
            ["package"] = EditorHost.BillingPackageId,
            ["key"] = new JsonObject { ["attributes"] = new JsonArray("01M3P0000000000000000000A1") },
            ["attributes"] = new JsonArray(new JsonObject { ["id"] = "01M3P0000000000000000000A1", ["name"] = "id", ["type"] = "uuid", ["required"] = true }),
        };

        var response = await host.SendJsonAsync("POST", "/api/model/elements", body);

        Assert.Equal(201, response.Status);
        Contract.AssertResponse(response, "/api/model/elements");
        var id = response.Json["id"]!.GetValue<string>();
        Assert.Equal("/api/model/elements/" + id, response.Headers.Location.ToString());
        Assert.Equal("\"" + response.Json["hash"]!.GetValue<string>() + "\"", response.ETag);
        Assert.True(File.Exists(host.PathOf(".maquettiste/model/entities/refund.json")));
        Recorder.Json("create-entity.json", response);
    }

    [Fact]
    public async Task Create_of_an_invalid_document_is_422_and_a_non_ulid_id_is_echoed()
    {
        await using var host = EditorHost.Create();

        var missingSubId = await host.SendJsonAsync("POST", "/api/model/elements", """{ "kind": "entity", "name": "X", "attributes": [ { "name": "a", "type": "string" } ] }""");
        var badId = await host.SendJsonAsync("POST", "/api/model/elements", """{ "kind": "entity", "id": "not-a-ulid", "name": "X" }""");
        var notJson = await host.SendJsonAsync("POST", "/api/model/elements", "{ \"kind\": ");

        Assert.Equal(422, missingSubId.Status);
        Contract.AssertResponse(missingSubId, "/api/model/elements");
        Assert.Equal(422, badId.Status);
        Contract.AssertResponse(badId, "/api/model/elements");
        Assert.Equal("not-a-ulid", badId.Json["id"]!.GetValue<string>());
        Assert.Equal(422, notJson.Status);
        Assert.Equal("MQ1001", notJson.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_body_over_4_mb_is_400_too_large()
    {
        await using var host = EditorHost.Create();
        var huge = "{\"kind\":\"entity\",\"name\":\"" + new string('a', Api.MaxBodyBytes) + "\"}";

        var response = await host.SendJsonAsync("POST", "/api/model/elements", huge);

        Assert.Equal(400, response.Status);
        Assert.Equal("too-large", response.ProblemCode);
        Contract.AssertResponse(response, "/api/model/elements");
    }

    [Theory]
    [InlineData("quoted")]
    [InlineData("weak")]
    [InlineData("bare")]
    public async Task Save_accepts_every_if_match_form(string form)
    {
        await using var host = EditorHost.Create();
        var (json, hash) = await LoadAsync(host, EditorHost.InvoiceId);
        json["attributes"]![5]!["name"] = "remarks";
        var header = form switch { "quoted" => "\"" + hash + "\"", "weak" => "W/\"" + hash + "\"", _ => hash };

        var response = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.InvoiceId, json, r => r.Header("If-Match", header));

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/model/elements/{id}");
        Assert.Equal("saved", response.Json["outcome"]!.GetValue<string>());
        Assert.Equal("\"" + response.Json["hash"]!.GetValue<string>() + "\"", response.ETag);
        Assert.Equal("remarks", response.Json["current"]!["json"]!["attributes"]![5]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Save_answers_409_with_the_disk_version_422_404_and_428()
    {
        await using var host = EditorHost.Create();
        var (json, hash) = await LoadAsync(host, EditorHost.InvoiceId);
        json["name"] = "Bill";
        var first = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.InvoiceId, json, r => r.IfMatch(hash));

        var stale = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.InvoiceId, json, r => r.IfMatch(hash));
        var invalidJson = json.DeepClone();
        invalidJson["attributes"]![0]!["type"] = "no-such-type";
        var invalid = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.InvoiceId, invalidJson,
            r => r.IfMatch(first.Json["hash"]!.GetValue<string>()));
        var unknownBody = json.DeepClone();
        unknownBody["id"] = "01J92P0V0FJ23CGSNKM7P1W5V9";
        var unknown = await host.SendJsonAsync("PUT", "/api/model/elements/01J92P0V0FJ23CGSNKM7P1W5V9", unknownBody, r => r.IfMatch(hash));
        var missing = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.InvoiceId, json);

        Assert.Equal(200, first.Status);
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/model/elements/{id}");
        Assert.Equal("conflict", stale.Json["outcome"]!.GetValue<string>());
        Assert.Equal(first.Json["hash"]!.GetValue<string>(), stale.Json["hash"]!.GetValue<string>());
        Assert.Equal("Bill", stale.Json["current"]!["json"]!["name"]!.GetValue<string>());
        Assert.Null(stale.ETag);
        Assert.Equal(422, invalid.Status);
        Contract.AssertResponse(invalid, "/api/model/elements/{id}");
        Assert.Equal(404, unknown.Status);
        Contract.AssertResponse(unknown, "/api/model/elements/{id}");
        Assert.Equal("not-found", unknown.Json["outcome"]!.GetValue<string>());
        Assert.Equal(428, missing.Status);
        Contract.AssertResponse(missing, "/api/model/elements/{id}");
        Recorder.Json("save-conflict.json", stale);
    }

    [Fact]
    public async Task Delete_refuses_while_referenced_and_maps_the_resolution_itself()
    {
        await using var host = EditorHost.Create();
        var (_, hash) = await LoadAsync(host, EditorHost.InvoiceId);

        var refused = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + EditorHost.InvoiceId).IfMatch(hash));
        var explicitRefuse = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + EditorHost.InvoiceId + "?resolution=refuse").IfMatch(hash));
        var memberName = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + EditorHost.InvoiceId + "?resolution=RemoveReferences").IfMatch(hash));
        var required = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + EditorHost.InvoiceId + "?resolution=remove-references").IfMatch(hash));
        var missing = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + EditorHost.InvoiceId));
        var unknown = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/01J92P0V0FJ23CGSNKM7P1W5V9").IfMatch(hash));

        Assert.Equal(409, refused.Status);
        Contract.AssertResponse(refused, "/api/model/elements/{id}");
        Assert.Equal("referenced", refused.Json["outcome"]!.GetValue<string>());
        Assert.NotEmpty(refused.Json["referrers"]!.AsArray());
        Assert.Equal(409, explicitRefuse.Status);
        Assert.Equal(400, memberName.Status);
        Assert.Equal("bad-request", memberName.ProblemCode);
        Contract.AssertResponse(memberName, "/api/model/elements/{id}");
        // Invoice is the required end of relations (min 1), so clearing references would leave them empty: invalid.
        Assert.Equal(422, required.Status);
        Contract.AssertResponse(required, "/api/model/elements/{id}");
        Assert.Equal(428, missing.Status);
        Assert.Equal(404, unknown.Status);
        Contract.AssertResponse(unknown, "/api/model/elements/{id}");
        Recorder.Json("delete-referenced.json", refused);
    }

    [Fact]
    public async Task Delete_with_remove_references_clears_optional_references()
    {
        await using var host = EditorHost.Create();
        var package = await host.SendJsonAsync("POST", "/api/model/elements", """{ "kind": "package", "name": "Scratch" }""");
        var packageId = package.Json["id"]!.GetValue<string>();
        var entity = await host.SendJsonAsync("POST", "/api/model/elements", new JsonObject
        {
            ["kind"] = "entity",
            ["name"] = "Note",
            ["package"] = packageId,
            ["key"] = new JsonObject { ["attributes"] = new JsonArray("01M3P0000000000000000000B1") },
            ["attributes"] = new JsonArray(new JsonObject { ["id"] = "01M3P0000000000000000000B1", ["name"] = "id", ["type"] = "uuid", ["required"] = true }),
        });
        var entityId = entity.Json["id"]!.GetValue<string>();
        var hash = package.Json["hash"]!.GetValue<string>();

        var refused = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + packageId).IfMatch(hash));
        var removed = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + packageId + "?resolution=remove-references").IfMatch(hash));

        Assert.Equal(201, entity.Status);
        Assert.Equal(409, refused.Status);
        Assert.Equal(200, removed.Status);
        Contract.AssertResponse(removed, "/api/model/elements/{id}");
        Assert.Equal([packageId], removed.Json["changes"]!["deleted"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Contains(entityId, removed.Json["changes"]!["changed"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()));
        var after = await host.GetAsync("/api/model/elements/" + entityId);
        Assert.Null(after.Json["element"]!["package"]);
    }

    [Fact]
    public async Task The_delete_plan_names_the_dependents_and_delete_dependents_removes_them_in_one_change()
    {
        await using var host = EditorHost.Create();
        var (_, hash) = await LoadAsync(host, EditorHost.InvoiceId);

        var plan = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId + "/delete-plan");
        var clearing = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId + "/delete-plan?resolution=remove-references");
        var badResolution = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId + "/delete-plan?resolution=cascade");
        var unknown = await host.GetAsync("/api/model/elements/01J92P0V0FJ23CGSNKM7P1W5V9/delete-plan");
        var bulk = await host.SendJsonAsync("POST", "/api/model/delete-plan", new JsonObject
        {
            ["ids"] = new JsonArray(EditorHost.InvoiceId, "01J92P0V0FJ23CGSNKM7P1W5V9"),
            ["resolution"] = "delete-dependents",
        });
        var badBulk = await host.SendJsonAsync("POST", "/api/model/delete-plan", new JsonObject { ["ids"] = new JsonArray() });
        var deleted = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + EditorHost.InvoiceId + "?resolution=delete-dependents").IfMatch(hash));

        Assert.Equal(200, plan.Status);
        Contract.AssertResponse(plan, "/api/model/elements/{id}/delete-plan");
        Assert.Equal("saved", plan.Json["outcome"]!.GetValue<string>());
        var deletes = plan.Json["deletes"]!.AsArray();
        Assert.Contains(deletes, d => d!["kind"]!.GetValue<string>() == "relation" && d["name"]!.GetValue<string>() == "places" && d["because"]!.GetValue<string>() == "needs entity Invoice");
        Assert.Equal(200, clearing.Status);
        Contract.AssertResponse(clearing, "/api/model/elements/{id}/delete-plan");
        Assert.Equal("invalid", clearing.Json["outcome"]!.GetValue<string>());
        Assert.All(clearing.Json["refused"]!.AsArray(), r => Assert.Equal("MQ2001", r!["rule"]!.GetValue<string>()));
        Assert.Equal(400, badResolution.Status);
        Assert.Equal(404, unknown.Status);
        Assert.Equal(200, bulk.Status);
        Contract.AssertResponse(bulk, "/api/model/delete-plan");
        Assert.Equal("not-found", bulk.Json["outcome"]!.GetValue<string>());
        Assert.Equal(400, badBulk.Status);
        Assert.Equal(200, deleted.Status);
        Contract.AssertResponse(deleted, "/api/model/elements/{id}");
        Assert.Equal(deletes.Count + 1, deleted.Json["changes"]!["deleted"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_new_element_with_no_referrers_deletes_with_200()
    {
        await using var host = EditorHost.Create();
        var created = await host.SendJsonAsync("POST", "/api/model/elements", """{ "kind": "package", "name": "Scratch" }""");
        var id = created.Json["id"]!.GetValue<string>();

        var deleted = await host.SendAsync(TestRequest.Local("DELETE", "/api/model/elements/" + id).IfMatch(created.Json["hash"]!.GetValue<string>()));

        Assert.Equal(200, deleted.Status);
        Contract.AssertResponse(deleted, "/api/model/elements/{id}");
        Assert.Equal([id], deleted.Json["changes"]!["deleted"]!.AsArray().Select(d => d!.GetValue<string>()));
    }

    [Fact]
    public async Task Batch_applies_all_or_nothing_with_each_outcome_status()
    {
        await using var host = EditorHost.Create();
        var (invoice, hash) = await LoadAsync(host, EditorHost.InvoiceId);
        invoice["name"] = "Bill";
        var ok = new JsonObject
        {
            ["operations"] = new JsonArray(
                new JsonObject { ["op"] = "update", ["id"] = EditorHost.InvoiceId, ["expectedHash"] = hash, ["element"] = invoice.DeepClone() },
                new JsonObject { ["op"] = "create", ["element"] = new JsonObject { ["kind"] = "package", ["name"] = "Shipping" } }),
        };

        var saved = await host.SendJsonAsync("POST", "/api/model/batch", ok);
        var conflict = await host.SendJsonAsync("POST", "/api/model/batch", ok);
        var notFound = await host.SendJsonAsync("POST", "/api/model/batch", new JsonObject
        {
            ["operations"] = new JsonArray(new JsonObject { ["op"] = "delete", ["id"] = "01J92P0V0FJ23CGSNKM7P1W5V9", ["expectedHash"] = hash }),
        });
        var unparsable = await host.SendJsonAsync("POST", "/api/model/batch", """{ "operations": [ { "op": "rename" } ] }""");
        var invalid = await host.SendJsonAsync("POST", "/api/model/batch", """{ "operations": [ { "op": "create", "element": { "kind": "entity", "name": "Shipment" } } ] }""");

        Assert.Equal(200, saved.Status);
        Contract.AssertResponse(saved, "/api/model/batch");
        Assert.Equal(2, saved.Json["changes"]!["changed"]!.AsArray().Count);
        Assert.Equal(409, conflict.Status);
        Contract.AssertResponse(conflict, "/api/model/batch");
        Assert.Equal(404, notFound.Status);
        Contract.AssertResponse(notFound, "/api/model/batch");
        Assert.Equal(422, unparsable.Status);
        Contract.AssertResponse(unparsable, "/api/model/batch");
        Assert.Null(unparsable.Json["batch"]);
        Assert.Equal(422, invalid.Status);
        Contract.AssertResponse(invalid, "/api/model/batch");
        Assert.Equal("invalid", invalid.Json["outcome"]!.GetValue<string>());
        Assert.False(Directory.EnumerateFiles(host.PathOf(".maquettiste/model/entities"), "shipment*").Any());
    }

    [Fact]
    public async Task Batch_schema_operations_add_and_refuse_a_remove_that_strands_objects()
    {
        await using var host = EditorHost.Create();
        const string Database = "01J92P0V1QRN2181XM2ZWE02W4";

        var added = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "add-schema", "id": "{{Database}}", "schema": "01J92P0V1RC04SKQ5353EAKHG3", "name": "archive" } ] }""");
        var refused = await host.SendJsonAsync("POST", "/api/model/batch", $$"""{ "operations": [ { "op": "remove-schema", "id": "{{Database}}", "schema": "01J92P0V1RC04SKQ5353EAKHG2", "default": "01J92P0V1RC04SKQ5353EAKHG3" } ] }""");

        Assert.Equal(200, added.Status);
        Contract.AssertResponse(added, "/api/model/batch");
        Assert.Contains("\"archive\"", File.ReadAllText(host.PathOf(".maquettiste/model/databases/main/database.json")), StringComparison.Ordinal);
        Assert.Equal(422, refused.Status);
        Contract.AssertResponse(refused, "/api/model/batch");
        Assert.Contains("MQ4015", refused.Json.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_takes_no_body_a_scope_or_answers_400()
    {
        await using var host = EditorHost.Create();

        var all = await host.SendAsync(TestRequest.Local("POST", "/api/validate").With(r => r.ContentType = "application/json"));
        var scoped = await host.SendJsonAsync("POST", "/api/validate", """{ "elementIds": ["01J92P0V0HEGSC6MW92CST5KA6"], "includeReferrers": false }""");
        var empty = await host.SendJsonAsync("POST", "/api/validate", "{}");
        var bad = await host.SendJsonAsync("POST", "/api/validate", "{ \"elementIds\": 3 }");
        var badId = await host.SendJsonAsync("POST", "/api/validate", """{ "elementIds": ["payment"] }""");

        Assert.Equal(200, all.Status);
        Contract.AssertResponse(all, "/api/validate");
        Assert.False(all.Json["hasErrors"]!.GetValue<bool>());
        Assert.Equal(200, scoped.Status);
        Contract.AssertResponse(scoped, "/api/validate");
        Assert.Equal(200, empty.Status);
        Assert.Equal(400, bad.Status);
        Contract.AssertResponse(bad, "/api/validate");
        Assert.Equal(400, badId.Status);
    }

    [Fact]
    public async Task Validate_carries_the_resolvers_MQ4005_with_its_pointer_as_the_problems_panel_shows_it()
    {
        await using var host = EditorHost.Create();
        var path = host.PathOf(BillingEdits.InvoiceOverlayPath);
        await File.WriteAllTextAsync(path, BillingEdits.PinCustomerForeignKey(await File.ReadAllTextAsync(path, EditorHost.Ct)), EditorHost.Ct);
        await host.Store.RescanAsync(false, EditorHost.Ct);

        var report = await host.SendJsonAsync("POST", "/api/validate", "{}");
        Assert.Equal(200, report.Status);
        Contract.AssertResponse(report, "/api/validate");
        Assert.True(report.Json["hasErrors"]!.GetValue<bool>());
        var d = report.Json["diagnostics"]!.AsArray().Single(x => x!["rule"]!.GetValue<string>() == "MQ4005")!;
        Assert.Equal(BillingEdits.InvoiceOverlayId, d["elementId"]!.GetValue<string>());
        Assert.Equal(BillingEdits.InvoiceOverlayPath, d["filePath"]!.GetValue<string>());
        Assert.Equal(BillingEdits.PinnedPointer, d["jsonPointer"]!.GetValue<string>());
        Assert.Equal(BillingEdits.ForeignKeyMismatch, d["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Diagrams_read_and_save_only_diagrams()
    {
        await using var host = EditorHost.Create();
        var diagram = await host.GetAsync("/api/diagrams/" + EditorHost.OverviewDiagramId);
        var json = diagram.Json["json"]!.AsObject();
        json["viewport"] = new JsonObject { ["zoom"] = 1.25 };

        var saved = await host.SendJsonAsync("PUT", "/api/diagrams/" + EditorHost.OverviewDiagramId, json, r => r.IfMatch(diagram.Json["hash"]!.GetValue<string>()));
        var stale = await host.SendJsonAsync("PUT", "/api/diagrams/" + EditorHost.OverviewDiagramId, json, r => r.IfMatch(diagram.Json["hash"]!.GetValue<string>()));
        var notADiagram = await host.GetAsync("/api/diagrams/" + EditorHost.InvoiceId);
        var saveNotADiagram = await host.SendJsonAsync("PUT", "/api/diagrams/" + EditorHost.InvoiceId, json, r => r.IfMatch(diagram.Json["hash"]!.GetValue<string>()));
        var unknown = await host.GetAsync("/api/diagrams/01J92P0V0FJ23CGSNKM7P1W5V9");
        var missing = await host.SendJsonAsync("PUT", "/api/diagrams/" + EditorHost.OverviewDiagramId, json);

        Assert.Equal(200, saved.Status);
        Contract.AssertResponse(saved, "/api/diagrams/{id}");
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/diagrams/{id}");
        Assert.Equal(404, notADiagram.Status);
        Assert.Equal("not-a-diagram", notADiagram.ProblemCode);
        Contract.AssertResponse(notADiagram, "/api/diagrams/{id}");
        Assert.Equal(404, saveNotADiagram.Status);
        Assert.Equal("not-a-diagram", saveNotADiagram.ProblemCode);
        Assert.Equal(404, unknown.Status);
        Assert.Equal("not-found", unknown.ProblemCode);
        Assert.Equal(428, missing.Status);
        Contract.AssertResponse(missing, "/api/diagrams/{id}");
    }

    [Fact]
    public async Task Database_view_answers_for_a_database_and_404_otherwise()
    {
        await using var host = EditorHost.Create();

        var view = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/view");
        var notADatabase = await host.GetAsync("/api/databases/" + EditorHost.InvoiceId + "/view");
        var unknown = await host.GetAsync("/api/databases/01J92P0V0FJ23CGSNKM7P1W5V9/view");

        Assert.Equal(200, view.Status);
        Contract.AssertResponse(view, "/api/databases/{id}/view");
        Assert.Equal("main", view.Json["view"]!["name"]!.GetValue<string>());
        Assert.Contains(view.Json["view"]!["tables"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "customers");
        // The invoice table's overlay carries annotations: the view shows them as written, merged with the stereotype's defaults.
        var invoices = view.Json["view"]!["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "invoices")!;
        Assert.Equal("Invoice register", invoices["displayName"]!.GetValue<string>());
        Assert.Null(invoices["pluralName"]);
        Assert.Equal("One row per issued invoice; finance reconciles it monthly.", invoices["description"]!.GetValue<string>());
        Assert.Equal(["audited"], invoices["stereotypes"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(["billing"], invoices["tags"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal("01J92P0V06TYE8P8990AV35A8K", invoices["category"]!.GetValue<string>());
        Assert.Equal("billing_data", invoices["properties"]!["tablespace"]!.GetValue<string>());
        Assert.Equal(2555, invoices["properties"]!["retentionDays"]!.GetValue<int>());
        Assert.Empty(invoices["generation"]!.AsObject());
        // A synthesized table without an overlay has none, whatever its entity carries.
        var customers = view.Json["view"]!["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "customers")!;
        Assert.Null(customers["displayName"]);
        Assert.Empty(customers["stereotypes"]!.AsArray());
        Assert.Empty(customers["properties"]!.AsObject());
        // Views and sequences are listed with their own annotations.
        Assert.Equal("outstanding_invoices", Assert.Single(view.Json["view"]!["views"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Contains(view.Json["view"]!["sequences"]!.AsArray(), s => s!["name"]!.GetValue<string>() == "invoice_number_seq" && s["start"]!.GetValue<long>() == 1000);
        Assert.Equal(404, notADatabase.Status);
        Assert.Equal("not-a-database", notADatabase.ProblemCode);
        Contract.AssertResponse(notADatabase, "/api/databases/{id}/view");
        Assert.Equal(404, unknown.Status);
        Assert.Equal("not-found", unknown.ProblemCode);
        Recorder.Json("database-view-main.json", view);
    }

    [Fact]
    public async Task A_new_database_holds_only_what_is_mapped_to_it()
    {
        await using var host = EditorHost.Create();
        var body = new JsonObject { ["kind"] = "database", ["name"] = "archive", ["dialect"] = "sqlite", ["byConvention"] = "none" };

        var created = await host.SendJsonAsync("POST", "/api/model/elements", body);

        Assert.Equal(201, created.Status);
        Contract.AssertResponse(created, "/api/model/elements");
        var id = created.Json["id"]!.GetValue<string>();
        var empty = await host.GetAsync("/api/databases/" + id + "/view");
        Assert.Equal(200, empty.Status);
        Assert.Empty(empty.Json["view"]!["tables"]!.AsArray());
        var main = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/view");
        Assert.Contains(main.Json["view"]!["tables"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "customers");

        var (json, hash) = await LoadAsync(host, id);
        Assert.Equal("none", json["byConvention"]!.GetValue<string>());
        json["byConvention"] = "packages";
        json["packages"] = new JsonArray(EditorHost.BillingPackageId);
        var saved = await host.SendJsonAsync("PUT", "/api/model/elements/" + id, json, r => r.IfMatch(hash));
        Assert.True(saved.Status is 200, saved.Json.ToJsonString());
        var mapped = await host.GetAsync("/api/databases/" + id + "/view");
        Assert.Contains(mapped.Json["view"]!["tables"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "customers");
    }

    [Fact]
    public async Task A_bad_byConvention_value_is_422()
    {
        await using var host = EditorHost.Create();

        var response = await host.SendJsonAsync("POST", "/api/model/elements", """{ "kind": "database", "name": "x", "dialect": "sqlite", "byConvention": "some" }""");

        Assert.Equal(422, response.Status);
        Assert.Equal("MQ1002", response.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_database_view_of_a_model_with_errors_is_null_with_the_errors()
    {
        await using var host = EditorHost.Create();
        var payment = host.PathOf(".maquettiste/model/entities/payment.json");
        var node = JsonNode.Parse(File.ReadAllText(payment))!.AsObject();
        node.Remove("key");
        File.WriteAllText(payment, node.ToJsonString(), new UTF8Encoding(false));

        var view = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/view");

        Assert.Equal(200, view.Status);
        Contract.AssertResponse(view, "/api/databases/{id}/view");
        Assert.Null(view.Json["view"]);
        Assert.Contains(view.Json["diagnostics"]!.AsArray(), d => d!["rule"]!.GetValue<string>() == "MQ3005");
    }
}
