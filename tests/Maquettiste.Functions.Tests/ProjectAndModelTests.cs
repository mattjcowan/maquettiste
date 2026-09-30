using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>Health, session, project, settings, the index and element reads (phase2-design.md section 3.7), each response checked against the contract.</summary>
public sealed class ProjectAndModelTests
{
    [Fact]
    public async Task Health_answers_anonymously_and_reports_the_model_and_services()
    {
        await using var host = EditorHost.Create();

        var before = await host.SendAsync(TestRequest.FromElsewhere("GET", "/api/health"));
        await host.Store.LoadAsync(EditorHost.Ct);
        host.Events.Worker = "running";
        host.Events.Watcher = "watching";
        var after = await host.GetAsync("/api/health");

        Assert.Equal(200, before.Status);
        Contract.AssertResponse(before, "/api/health");
        Assert.Equal("starting", before.Json["status"]!.GetValue<string>());
        Assert.False(before.Json["modelLoaded"]!.GetValue<bool>());
        Contract.AssertResponse(after, "/api/health");
        Assert.Equal("ok", after.Json["status"]!.GetValue<string>());
        Assert.Equal(host.Store.Current!.Documents.Count, after.Json["elements"]!.GetValue<int>());
        Assert.Equal("1.0.0", after.Json["engineVersion"]!.GetValue<string>());
        Assert.Equal(HealthEndpoints.EngineBuild(), after.Json["engineBuild"]!.GetValue<string>());
        Assert.DoesNotContain("+", HealthEndpoints.EngineBuild(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_is_degraded_when_a_background_service_is_not_running()
    {
        await using var host = EditorHost.Create();
        await host.Store.LoadAsync(EditorHost.Ct);

        var response = await host.GetAsync("/api/health");

        Contract.AssertResponse(response, "/api/health");
        Assert.Equal("degraded", response.Json["status"]!.GetValue<string>());
        Assert.Equal("stopped", response.Json["worker"]!.GetValue<string>());
    }

    [Fact]
    public async Task Validation_rules_lists_the_catalog_with_families_and_which_rules_can_be_off()
    {
        await using var host = EditorHost.Create();

        var response = await host.GetAsync("/api/validation/rules");

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/validation/rules");
        var rules = response.Json.AsArray();
        Assert.Equal(Maquettiste.Engine.Diagnostics.RuleCatalog.All.Count, rules.Count);
        var ids = rules.Select(r => r!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        var first = rules[0]!;
        Assert.Equal("MQ1001", first["id"]!.GetValue<string>());
        Assert.Equal("error", first["defaultSeverity"]!.GetValue<string>());
        Assert.Equal("MQ10xx", first["family"]!.GetValue<string>());
        Assert.Equal("Model files", first["familyLabel"]!.GetValue<string>());
        Assert.False(first["canBeOff"]!.GetValue<bool>());
        var locale = rules.Single(r => r!["id"]!.GetValue<string>() == "MQ7204")!;
        Assert.Equal("info", locale["defaultSeverity"]!.GetValue<string>());
        Assert.Equal("MQ72xx", locale["family"]!.GetValue<string>());
        Assert.Equal("Localization", locale["familyLabel"]!.GetValue<string>());
        Assert.True(locale["canBeOff"]!.GetValue<bool>());
        Assert.All(rules, r => Assert.NotEqual(r!["family"]!.GetValue<string>(), r["familyLabel"]!.GetValue<string>()));

        // The editor's mock serves this recording; a new or changed rule needs MAQUETTISTE_RECORD=1 once.
        Recorder.Json("validation-rules.json", response);
        var recorded = File.ReadAllText(Path.Combine(Recorder.Folder, "validation-rules.json"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(recorded), response.Json),
            "src/editor/src/mocks/recorded/validation-rules.json is out of date: run this test with MAQUETTISTE_RECORD=1.");
    }

    [Fact]
    public async Task Session_says_who_the_caller_is_and_sign_out_clears_the_cookie()
    {
        await using var host = EditorHost.Create();

        var local = await host.GetAsync("/api/session");
        var anonymous = await host.SendAsync(TestRequest.FromElsewhere("GET", "/api/session"));
        var signOut = await host.SendAsync(TestRequest.Local("DELETE", "/api/session"));

        Contract.AssertResponse(local, "/api/session");
        Assert.Equal("local", local.Json["via"]!.GetValue<string>());
        Assert.Equal("admin", local.Json["user"]!["role"]!.GetValue<string>());
        Assert.Equal("local", local.Json["mode"]!.GetValue<string>());
        Assert.Equal(401, anonymous.Status);
        Assert.True(anonymous.Routed, "GET /api/session is anonymous in the gate; the handler answers 401.");
        Contract.AssertResponse(anonymous, "/api/session");
        Assert.Equal(204, signOut.Status);
        Contract.AssertResponse(signOut, "/api/session");
        Assert.Contains("mq_session=;", signOut.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_composes_settings_databases_packs_extensions_and_git()
    {
        await using var host = EditorHost.Create();

        var response = await host.GetAsync("/api/project");

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/project");
        var project = response.Json;
        Assert.Equal("billing", project["name"]!.GetValue<string>());
        Assert.Equal(1, project["formatVersion"]!.GetValue<int>());
        Assert.Equal(host.Store.Current!.SettingsHash, project["settingsHash"]!.GetValue<string>());
        Assert.Equal(EditorHost.MainDatabaseId, project["databases"]!.AsArray().Single()!["id"]!.GetValue<string>());
        Assert.Equal(["csharp-dapper", "sql-ddl"], project["packs"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal("retention", project["extensions"]!.AsArray().Single()!["name"]!.GetValue<string>());
        Assert.Null(project["git"]); // the temp copy is not a git checkout
        // The page-state key: stable for one checkout, apart for another checkout with the same name.
        var key = project["projectKey"]!.GetValue<string>();
        Assert.Matches("^[0-9a-f]{16}$", key);
        Assert.Equal(key, ProjectEndpoints.KeyOf(host.RepoRoot + Path.DirectorySeparatorChar));
        Assert.NotEqual(key, ProjectEndpoints.KeyOf(host.RepoRoot + "-clone"));
        Recorder.Json("project.json", response);
    }

    [Fact]
    public async Task Settings_read_with_its_hash_as_etag_and_save_with_if_match()
    {
        await using var host = EditorHost.Create();
        var read = await host.GetAsync("/api/project/settings");
        var hash = read.Json["hash"]!.GetValue<string>();
        var json = read.Json["json"]!.AsObject();
        json["conventions"] = new JsonObject { ["pluralTables"] = false };

        var missing = await host.SendJsonAsync("PUT", "/api/project/settings", json);
        var saved = await host.SendJsonAsync("PUT", "/api/project/settings", json, r => r.IfMatch(hash));
        var stale = await host.SendJsonAsync("PUT", "/api/project/settings", json, r => r.IfMatch(hash));
        var invalid = await host.SendJsonAsync("PUT", "/api/project/settings", new JsonObject { ["formatVersion"] = 1, ["handEdits"] = "never" },
            r => r.IfMatch(saved.Json["hash"]!.GetValue<string>()));

        Contract.AssertResponse(read, "/api/project/settings");
        Assert.Equal("\"" + hash + "\"", read.ETag);
        Assert.Equal(428, missing.Status);
        Assert.Equal("precondition-required", missing.ProblemCode);
        Contract.AssertResponse(missing, "/api/project/settings");
        Assert.Equal(200, saved.Status);
        Contract.AssertResponse(saved, "/api/project/settings");
        Assert.Equal("\"" + saved.Json["hash"]!.GetValue<string>() + "\"", saved.ETag);
        Assert.False(host.Store.Current!.Settings.Conventions.PluralTables);
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/project/settings");
        Assert.Equal("conflict", stale.Json["outcome"]!.GetValue<string>());
        Assert.Equal(422, invalid.Status);
        Contract.AssertResponse(invalid, "/api/project/settings");
        Assert.Equal("invalid", invalid.Json["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Index_lists_every_element_with_category_and_stereotypes()
    {
        await using var host = EditorHost.Create();

        var response = await host.GetAsync("/api/model/index");

        Assert.Equal(200, response.Status);
        Contract.AssertResponse(response, "/api/model/index");
        Assert.Equal("no-cache", response.Headers.CacheControl.ToString()); // E5e: kept, revalidated with the ETag
        var invoice = response.Json.AsArray().Single(e => e!["id"]!.GetValue<string>() == EditorHost.InvoiceId)!;
        Assert.Equal(["aggregate-root", "audited", "soft-delete"], invoice["stereotypes"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(host.Store.Current!.Documents.Count, response.Json.AsArray().Count);
        Recorder.Json("index.json", response);
    }

    [Fact]
    public async Task An_element_comes_with_its_hash_as_etag_and_304_when_unchanged()
    {
        await using var host = EditorHost.Create();

        var invoice = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId);
        var hash = invoice.Json["hash"]!.GetValue<string>();
        var unchanged = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId, r => r.Header("If-None-Match", "\"" + hash + "\""));
        var weak = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId, r => r.Header("If-None-Match", "W/\"" + hash + "\""));
        var other = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId, r => r.Header("If-None-Match", "\"" + new string('0', 64) + "\""));

        Assert.Equal(200, invoice.Status);
        Contract.AssertResponse(invoice, "/api/model/elements/{id}");
        Assert.Equal("\"" + hash + "\"", invoice.ETag);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(host.PathOf(invoice.Json["path"]!.GetValue<string>())))), hash);
        Assert.Equal("no-store", invoice.Headers.CacheControl.ToString());
        Assert.Equal(304, unchanged.Status);
        Assert.Empty(unchanged.Body);
        Contract.AssertResponse(unchanged, "/api/model/elements/{id}");
        Assert.Equal(304, weak.Status);
        Assert.Equal(200, other.Status);
        Recorder.Json("element-invoice.json", invoice);
    }

    [Fact]
    public async Task A_sub_element_id_returns_the_document_that_holds_it_and_an_unknown_id_is_404()
    {
        await using var host = EditorHost.Create();

        var attribute = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceNotesAttributeId);
        var unknown = await host.GetAsync("/api/model/elements/01J92P0V0FJ23CGSNKM7P1W5V9");
        var notUlid = await host.GetAsync("/api/model/elements/not-an-id");

        Assert.Equal(200, attribute.Status);
        Assert.Equal(EditorHost.InvoiceId, attribute.Json["element"]!["id"]!.GetValue<string>());
        Assert.Equal(404, unknown.Status);
        Assert.Equal("not-found", unknown.ProblemCode);
        Contract.AssertResponse(unknown, "/api/model/elements/{id}");
        Assert.Equal(404, notUlid.Status);
    }

    [Fact]
    public async Task References_list_who_uses_an_element_and_404_for_an_unknown_one()
    {
        await using var host = EditorHost.Create();

        var references = await host.GetAsync("/api/model/references/" + EditorHost.InvoiceId);
        var unknown = await host.GetAsync("/api/model/references/01J92P0V0FJ23CGSNKM7P1W5V9");

        Assert.Equal(200, references.Status);
        Contract.AssertResponse(references, "/api/model/references/{id}");
        Assert.Contains(references.Json.AsArray(), r => r!["fromElementId"]!.GetValue<string>() == EditorHost.OverviewDiagramId);
        Assert.Equal(404, unknown.Status);
        Contract.AssertResponse(unknown, "/api/model/references/{id}");
    }

    [Fact]
    public async Task Relations_and_diagrams_record_for_the_mocks()
    {
        await using var host = EditorHost.Create();

        var relation = await host.GetAsync("/api/model/elements/" + EditorHost.ContainsRelationId);
        var diagram = await host.GetAsync("/api/diagrams/" + EditorHost.OverviewDiagramId);

        Contract.AssertResponse(relation, "/api/model/elements/{id}");
        Contract.AssertResponse(diagram, "/api/diagrams/{id}");
        Assert.Equal("relation", relation.Json["element"]!["kind"]!.GetValue<string>());
        Assert.Equal("diagram", diagram.Json["element"]!["kind"]!.GetValue<string>());
        Recorder.Json("element-contains.json", relation);
        Recorder.Json("diagram-billing-overview.json", diagram);
    }
}
