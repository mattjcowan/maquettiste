using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>Unit paths, template context, file move, pack outputs, pack settings and explain (generation-ui.md sections 4.3 and 5), against the contract.</summary>
public sealed class PackAuthoringEndpointTests
{
    private static string Hash(TestResponse response) => response.Json["hash"]!.GetValue<string>();

    private static string TableKey => EditorHost.CustomerId + "@" + EditorHost.MainDatabaseId;

    [Fact]
    public async Task Paths_list_the_scope_render_only_the_elements_asked_for_and_a_constant_pattern_is_MQ6020()
    {
        await using var host = EditorHost.Create();

        // The scope is counted and listed with names and kinds; nothing is rendered (generation-ui.md section 5.2, "Bounds").
        var paths = await host.SendJsonAsync("POST", "/api/templates/paths", new { pack = "sql-ddl", unit = "table" });
        Assert.Equal(200, paths.Status);
        Contract.AssertResponse(paths, "/api/templates/paths");
        var count = paths.Json["count"]!.GetValue<int>();
        Assert.True(count > 1);
        Assert.Equal(0, paths.Json["rendered"]!.GetValue<int>());
        Assert.Empty(paths.Json["paths"]!.AsArray());
        Assert.False(paths.Json["wide"]!.GetValue<bool>());
        var elements = paths.Json["elements"]!.AsArray();
        Assert.Equal(count, elements.Count);
        Assert.All(elements, e => Assert.Equal("table", e!["kind"]!.GetValue<string>()));

        var one = await host.SendJsonAsync("POST", "/api/templates/paths", new { pack = "sql-ddl", unit = "table", elementIds = new[] { TableKey } });
        Assert.Equal((1, 1), (one.Json["count"]!.GetValue<int>(), one.Json["rendered"]!.GetValue<int>()));
        Assert.Equal(TableKey, one.Json["paths"]![0]!["elementId"]!.GetValue<string>());
        Assert.EndsWith(".sql", one.Json["paths"]![0]!["path"]!.GetValue<string>(), StringComparison.Ordinal);

        var body = new JsonObject
        {
            ["pack"] = "sql-ddl",
            ["unit"] = "table",
            ["elementIds"] = new JsonArray(elements[0]!["id"]!.GetValue<string>(), elements[1]!["id"]!.GetValue<string>()),
            ["unitOverride"] = new JsonObject { ["id"] = "table", ["template"] = "table.scriban", ["for"] = "each table", ["output"] = "same.sql" },
        };
        var same = await host.SendJsonAsync("POST", "/api/templates/paths", body.ToJsonString());
        Assert.Equal(200, same.Status);
        Contract.AssertResponse(same, "/api/templates/paths");
        Assert.Contains(same.Json["diagnostics"]!.AsArray(), d => d!["rule"]!.GetValue<string>() == "MQ6020");

        var tooMany = new JsonObject { ["pack"] = "sql-ddl", ["unit"] = "table", ["elementIds"] = new JsonArray([.. Enumerable.Range(0, 21).Select(i => (JsonNode)JsonValue.Create("e" + i)!)]) };
        Assert.Equal(400, (await host.SendJsonAsync("POST", "/api/templates/paths", tooMany.ToJsonString())).Status);
        Assert.Equal(400, (await host.SendJsonAsync("POST", "/api/templates/paths", new { pack = "sql-ddl" })).Status);
        var wrongId = new JsonObject { ["pack"] = "sql-ddl", ["unit"] = "table", ["unitOverride"] = new JsonObject { ["id"] = "other", ["template"] = "table.scriban", ["for"] = "each table" } };
        Assert.Equal(400, (await host.SendJsonAsync("POST", "/api/templates/paths", wrongId.ToJsonString())).Status);
    }

    [Fact]
    public async Task The_template_context_lists_the_scope_alias_the_members_and_the_helpers()
    {
        await using var host = EditorHost.Create();
        var context = await host.GetAsync("/api/templates/context?pack=sql-ddl&unit=table");
        Assert.Equal(200, context.Status);
        Contract.AssertResponse(context, "/api/templates/context");
        Assert.Contains(context.Json["variables"]!.AsArray(), v => v!["name"]!.GetValue<string>() == "table");
        Assert.Contains(context.Json["members"]!["element"]!.AsArray(), m => m!["name"]!.GetValue<string>() == "name");
        Assert.Contains(context.Json["members"]!["model"]!.AsArray(), m => m!["name"]!.GetValue<string>() == "entities");
        Assert.NotEmpty(context.Json["helpers"]!.AsArray());

        Assert.Equal(404, (await host.GetAsync("/api/templates/context?pack=sql-ddl&unit=ghost")).Status);
        Assert.Equal(400, (await host.GetAsync("/api/templates/context?pack=sql-ddl")).Status);
    }

    [Fact]
    public async Task A_move_checks_both_hashes_and_can_rewrite_the_units()
    {
        await using var host = EditorHost.Create();
        var created = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl/file?path=extra/a.scriban", new { text = "A" }, r => r.Header("If-None-Match", "*"));
        Assert.Equal(201, created.Status);
        var hash = Hash(created);

        Assert.Equal(428, (await host.SendJsonAsync("POST", "/api/packs/sql-ddl/file/move", new { from = "extra/a.scriban", to = "extra/b.scriban" })).Status);
        var stale = await host.SendJsonAsync("POST", "/api/packs/sql-ddl/file/move", new { from = "extra/a.scriban", to = "extra/b.scriban" }, r => r.IfMatch(new string('0', 64)));
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/packs/{pack}/file/move");

        var moved = await host.SendJsonAsync("POST", "/api/packs/sql-ddl/file/move", new { from = "extra/a.scriban", to = "extra/b.scriban" }, r => r.IfMatch(hash));
        Assert.Equal(200, moved.Status);
        Contract.AssertResponse(moved, "/api/packs/{pack}/file/move");
        Assert.Equal(404, (await host.GetAsync("/api/packs/sql-ddl/file?path=extra/a.scriban")).Status);
        Assert.Equal(200, (await host.GetAsync("/api/packs/sql-ddl/file?path=extra/b.scriban")).Status);

        var template = await host.GetAsync("/api/packs/sql-ddl/file?path=seed.scriban");
        var referenced = await host.SendJsonAsync("POST", "/api/packs/sql-ddl/file/move", new { from = "seed.scriban", to = "seeds.scriban" }, r => r.IfMatch(Hash(template)));
        Assert.Equal(409, referenced.Status);
        Assert.Equal("referenced", referenced.Json["outcome"]!.GetValue<string>());

        var pack = await host.GetAsync("/api/packs/sql-ddl");
        var stalePack = await host.SendJsonAsync("POST", "/api/packs/sql-ddl/file/move",
            new { from = "seed.scriban", to = "seeds.scriban", updateUnits = true, expectedPackHash = new string('0', 64) }, r => r.IfMatch(Hash(template)));
        Assert.Equal(409, stalePack.Status);
        var rewritten = await host.SendJsonAsync("POST", "/api/packs/sql-ddl/file/move",
            new { from = "seed.scriban", to = "seeds.scriban", updateUnits = true, expectedPackHash = Hash(pack) }, r => r.IfMatch(Hash(template)));
        Assert.Equal(200, rewritten.Status);
        var after = await host.GetAsync("/api/packs/sql-ddl");
        Assert.Contains(after.Json["document"]!["units"]!.AsArray(), u => u!["template"]!.GetValue<string>() == "seeds.scriban");
        Assert.Equal(422, (await host.SendJsonAsync("POST", "/api/packs/sql-ddl/file/move", new { from = "extra/b.scriban", to = "table.scriban" },
            r => r.IfMatch(Hash(moved)))).Status);
    }

    [Fact]
    public async Task Outputs_list_the_manifest_entries_with_their_state()
    {
        await using var host = EditorHost.Create();
        var outputs = await host.GetAsync("/api/packs/sql-ddl/outputs");
        Assert.Equal(200, outputs.Status);
        Contract.AssertResponse(outputs, "/api/packs/{pack}/outputs");
        Assert.Equal("sql-ddl", outputs.Json["pack"]!.GetValue<string>());
        Assert.Equal(404, (await host.GetAsync("/api/packs/ghost/outputs")).Status);
        Assert.Equal(400, (await host.GetAsync("/api/packs/Bad%20Name/outputs")).Status);
    }

    [Fact]
    public async Task A_pack_settings_save_touches_only_its_entry_and_checks_the_settings_etag()
    {
        await using var host = EditorHost.Create();
        var settings = await host.GetAsync("/api/project/settings");
        var hash = Hash(settings);

        Assert.Equal(428, (await host.SendJsonAsync("PUT", "/api/project/settings/packs/csharp-dapper", new { enabled = false })).Status);
        Assert.Equal(400, (await host.SendJsonAsync("PUT", "/api/project/settings/packs/csharp-dapper", "[1]", r => r.IfMatch(hash))).Status);
        var saved = await host.SendJsonAsync("PUT", "/api/project/settings/packs/csharp-dapper", new { enabled = false }, r => r.IfMatch(hash));
        Assert.Equal(200, saved.Status);
        Contract.AssertResponse(saved, "/api/project/settings/packs/{pack}");
        Assert.Contains(host.Published("project.changed"), e => e.Payload["settingsHash"]!.GetValue<string>() == Hash(saved));

        var list = await host.GetAsync("/api/packs");
        Assert.False(list.Json["packs"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "csharp-dapper")!["enabled"]!.GetValue<bool>());
        var after = await host.GetAsync("/api/project/settings");
        Assert.Equal(settings.Json["json"]!["outputs"]!.ToJsonString(), after.Json["json"]!["outputs"]!.ToJsonString());

        var stale = await host.SendJsonAsync("PUT", "/api/project/settings/packs/csharp-dapper", new { enabled = true }, r => r.IfMatch(hash));
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/project/settings/packs/{pack}");
    }

    [Fact]
    public async Task Explain_names_the_first_reason_and_the_plan_reason_when_planned()
    {
        await using var host = EditorHost.Create();

        var scope = await host.SendJsonAsync("POST", "/api/generate/explain", new { pack = "sql-ddl", unit = "table", elementId = EditorHost.CustomerId });
        Assert.Equal(200, scope.Status);
        Contract.AssertResponse(scope, "/api/generate/explain");
        Assert.Equal(("scope", false), (scope.Json["reason"]!.GetValue<string>(), scope.Json["planned"]!.GetValue<bool>()));

        var notSelected = await host.SendJsonAsync("POST", "/api/generate/explain", new { pack = "sql-ddl", unit = "schema", packs = new[] { "csharp-dapper" } });
        Contract.AssertResponse(notSelected, "/api/generate/explain");
        Assert.Equal("not-selected", notSelected.Json["reason"]!.GetValue<string>());

        var unknown = await host.SendJsonAsync("POST", "/api/generate/explain", new { pack = "sql-ddl", unit = "ghost" });
        Assert.Equal("unknown-unit", unknown.Json["reason"]!.GetValue<string>());
        var element = await host.SendJsonAsync("POST", "/api/generate/explain", new { pack = "sql-ddl", unit = "table", elementId = "01J92P0V0ZZZZZZZZZZZZZZZZZ" });
        Assert.Equal("unknown-element", element.Json["reason"]!.GetValue<string>());

        var planned = await host.SendJsonAsync("POST", "/api/generate/explain", new { pack = "sql-ddl", unit = "table", elementId = TableKey });
        Assert.Equal(200, planned.Status);
        Contract.AssertResponse(planned, "/api/generate/explain");
        Assert.True(planned.Json["planned"]!.GetValue<bool>());
        Assert.Equal("new", planned.Json["reason"]!.GetValue<string>());
        Assert.Equal("sql-ddl/table:" + TableKey, planned.Json["planUnit"]!["key"]!.GetValue<string>());

        Assert.Equal(404, (await host.SendJsonAsync("POST", "/api/generate/explain", new { pack = "ghost", unit = "table" })).Status);
        Assert.Equal(400, (await host.SendJsonAsync("POST", "/api/generate/explain", new { pack = "sql-ddl" })).Status);
    }
}
