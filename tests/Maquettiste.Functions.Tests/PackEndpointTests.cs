using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>Pack authoring, the extended preview and the plan unit (generation-ui.md section 5), against the contract.</summary>
public sealed class PackEndpointTests
{
    private static string Hash(TestResponse response) => response.Json["hash"]!.GetValue<string>();

    [Fact]
    public async Task Packs_list_and_read_with_their_files_parameters_and_etag()
    {
        await using var host = EditorHost.Create();

        var list = await host.GetAsync("/api/packs");
        Assert.Equal(200, list.Status);
        Contract.AssertResponse(list, "/api/packs");
        Assert.Equal(["csharp-dapper", "sql-ddl"], list.Json["packs"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Recorder.Json("packs.json", list);

        var pack = await host.GetAsync("/api/packs/sql-ddl");
        Assert.Equal(200, pack.Status);
        Contract.AssertResponse(pack, "/api/packs/{pack}");
        Assert.Equal("\"" + Hash(pack) + "\"", pack.Headers.ETag!.ToString());
        Assert.Contains(pack.Json["files"]!.AsArray(), f => f!["path"]!.GetValue<string>() == "table.scriban" && f["role"]!.GetValue<string>() == "template");
        Recorder.Json("pack-sql-ddl.json", pack);

        Assert.Equal(404, (await host.GetAsync("/api/packs/ghost")).Status);
        Assert.Equal(400, (await host.GetAsync("/api/packs/Bad%20Name")).Status);
    }

    [Fact]
    public async Task A_file_round_trips_with_etags_and_a_stale_write_is_409_with_the_disk_text()
    {
        await using var host = EditorHost.Create();
        var read = await host.GetAsync("/api/packs/sql-ddl/file?path=seed.scriban");
        Assert.Equal(200, read.Status);
        Contract.AssertResponse(read, "/api/packs/{pack}/file");
        var hash = Hash(read);
        var text = read.Json["text"]!.GetValue<string>();

        Assert.Equal(428, (await host.SendJsonAsync("PUT", "/api/packs/sql-ddl/file?path=seed.scriban", new { text = "x" })).Status);
        var saved = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl/file?path=seed.scriban", new { text = text + "\n" }, r => r.IfMatch(hash));
        Assert.Equal(200, saved.Status);
        Contract.AssertResponse(saved, "/api/packs/{pack}/file");
        Assert.NotEqual(hash, Hash(saved));
        // The write tells every editor which file changed and its hash now (generation-ui.md section 5.1).
        var templates = Assert.Single(host.Published("templates.changed"));
        Assert.Equal("sql-ddl", templates.Payload["pack"]!.GetValue<string>());
        Assert.Equal(("seed.scriban", Hash(saved)), (templates.Payload["files"]![0]!["path"]!.GetValue<string>(), templates.Payload["files"]![0]!["hash"]!.GetValue<string>()));
        Assert.Equal("sql-ddl", Assert.Single(host.Published("packs.changed")).Payload["packs"]![0]!.GetValue<string>());

        var stale = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl/file?path=seed.scriban", new { text = "mine" }, r => r.IfMatch(hash));
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/packs/{pack}/file");
        Assert.Equal(("conflict", text + "\n", Hash(saved)), (stale.Json["outcome"]!.GetValue<string>(), stale.Json["current"]!.GetValue<string>(), Hash(stale)));

        var created = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl/file?path=extra/notes.scriban", new { text = "{{ if }" }, r => r.Header("If-None-Match", "*"));
        Assert.Equal(201, created.Status);
        Contract.AssertResponse(created, "/api/packs/{pack}/file");
        var again = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl/file?path=extra/notes.scriban", new { text = "x" }, r => r.Header("If-None-Match", "*"));
        Assert.Equal(409, again.Status);

        var raw = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl/file?path=pack.json", new { text = "{}" }, r => r.Header("If-None-Match", "*"));
        Assert.Equal(400, raw.Status);
        Assert.Equal(400, (await host.SendJsonAsync("PUT", "/api/packs/sql-ddl/file?path=../csharp-dapper/x.scriban", new { text = "x" }, r => r.Header("If-None-Match", "*"))).Status);

        var inUse = await host.SendAsync(TestRequest.Local("DELETE", "/api/packs/sql-ddl/file?path=_table.scriban").With(r => r.IfMatch(new string('0', 64))));
        Assert.Equal(409, inUse.Status);
        Contract.AssertResponse(inUse, "/api/packs/{pack}/file");
        Assert.Equal("referenced", inUse.Json["outcome"]!.GetValue<string>());
        var deleted = await host.SendAsync(TestRequest.Local("DELETE", "/api/packs/sql-ddl/file?path=extra/notes.scriban").With(r => r.IfMatch(Hash(created))));
        Assert.Equal(200, deleted.Status);
        Contract.AssertResponse(deleted, "/api/packs/{pack}/file");
        Assert.False(File.Exists(host.PathOf(".maquettiste/templates/sql-ddl/extra/notes.scriban")));
    }

    [Fact]
    public async Task Pack_json_saves_canonical_with_if_match_and_new_packs_are_created()
    {
        await using var host = EditorHost.Create();
        var pack = await host.GetAsync("/api/packs/sql-ddl");
        var document = pack.Json["document"]!.AsObject().DeepClone().AsObject();
        document["description"] = "Edited.";

        var saved = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl", document.ToJsonString(), r => r.IfMatch(Hash(pack)));
        Assert.Equal(200, saved.Status);
        Contract.AssertResponse(saved, "/api/packs/{pack}");
        Assert.Contains("\n  \"description\": \"Edited.\"", File.ReadAllText(host.PathOf(".maquettiste/templates/sql-ddl/pack.json")), StringComparison.Ordinal);

        var stale = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl", document.ToJsonString(), r => r.IfMatch(Hash(pack)));
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/packs/{pack}");
        document["units"]![0]!["for"] = "each tables";
        var invalid = await host.SendJsonAsync("PUT", "/api/packs/sql-ddl", document.ToJsonString(), r => r.IfMatch(Hash(saved)));
        Assert.Equal(422, invalid.Status);
        Contract.AssertResponse(invalid, "/api/packs/{pack}");
        Assert.Equal("MQ6021", invalid.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());
        Assert.Equal(428, (await host.SendJsonAsync("PUT", "/api/packs/sql-ddl", document.ToJsonString())).Status);

        var created = await host.SendJsonAsync("POST", "/api/packs", new { name = "docs", from = "empty" });
        Assert.Equal(201, created.Status);
        Contract.AssertResponse(created, "/api/packs");
        var exists = await host.SendJsonAsync("POST", "/api/packs", new { name = "docs" });
        Assert.Equal(409, exists.Status);
        Contract.AssertResponse(exists, "/api/packs");
        Assert.Equal(422, (await host.SendJsonAsync("POST", "/api/packs", new { name = "x", from = "nothing" })).Status);
        Assert.Equal(400, (await host.SendJsonAsync("POST", "/api/packs", new { name = "Bad" })).Status);
    }

    [Fact]
    public async Task The_preview_renders_unsaved_text_and_reports_read_keys()
    {
        await using var host = EditorHost.Create();
        var body = new JsonObject
        {
            ["pack"] = "sql-ddl",
            ["unit"] = "schema",
            ["elementId"] = EditorHost.MainDatabaseId,
            ["overlay"] = new JsonObject { ["schema.scriban"] = "UNSAVED {{ database.name }}" },
        };

        var preview = await host.SendJsonAsync("POST", "/api/templates/preview", body.ToJsonString());

        Assert.Equal(200, preview.Status);
        Contract.AssertResponse(preview, "/api/templates/preview");
        Assert.StartsWith("UNSAVED ", preview.Json["files"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEmpty(preview.Json["readKeys"]!.AsArray());

        body["overlay"] = new JsonObject { ["pack.json"] = "{}" };
        Assert.Equal(400, (await host.SendJsonAsync("POST", "/api/templates/preview", body.ToJsonString())).Status);
    }

    [Fact]
    public async Task A_plan_unit_carries_its_reason_and_why_not()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        var queued = await host.SendJsonAsync("POST", "/api/generate/plan", "{}");
        var jobId = queued.Json["id"]!.GetValue<string>();
        await EditorHost.WaitForAsync(() => host.Published("job.completed").Any(e => e.Target == "group:job:" + jobId), "plan", 60);
        var planId = host.Published("job.completed").Single(e => e.Target == "group:job:" + jobId).Payload["planId"]!.GetValue<string>();

        var plan = await host.GetAsync("/api/generate/plan/" + planId + "?units=true");
        Contract.AssertResponse(plan, "/api/generate/plan/{id}");
        var unit = plan.Json["units"]![0]!;
        Assert.Equal("new", unit["reason"]!.GetValue<string>());
        Assert.NotNull(unit["pack"]);

        var key = unit["key"]!.GetValue<string>();
        var detail = await host.GetAsync("/api/generate/plan/" + planId + "/unit?key=" + Uri.EscapeDataString(key));
        Assert.Equal(200, detail.Status);
        Contract.AssertResponse(detail, "/api/generate/plan/{id}/unit");
        Assert.Equal(key, detail.Json["unit"]!["key"]!.GetValue<string>());
        Recorder.Json("plan-unit.json", detail);
        Assert.Equal(404, (await host.GetAsync("/api/generate/plan/" + planId + "/unit?key=nope")).Status);
    }
}
