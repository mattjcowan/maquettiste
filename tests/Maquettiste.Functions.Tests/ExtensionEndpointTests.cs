using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>The extensions folder over HTTP (the editor's Extensions screen), against the contract.</summary>
public sealed class ExtensionEndpointTests
{
    private const string Rule = """
        maquettiste.rule({
          id: "short-names",
          severity: "warning",
          kinds: ["entity"],
          check(element, model, report) {
            if (element.name.length > 7) report("Entity names should be at most 7 characters.", { pointer: "/name" });
          },
        });
        """;

    private static string Hash(TestResponse response) => response.Json["hash"]!.GetValue<string>();

    [Fact]
    public async Task Extension_files_list_read_write_move_and_delete_with_etags()
    {
        await using var host = EditorHost.Create();

        var list = await host.GetAsync("/api/extensions/files");
        Assert.Equal(200, list.Status);
        Contract.AssertResponse(list, "/api/extensions/files");
        Assert.Equal(".maquettiste/extensions", list.Json["folder"]!.GetValue<string>());
        Assert.Equal(["retention.json"], list.Json["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));

        var read = await host.GetAsync("/api/extensions/file?path=retention.json");
        Assert.Equal(200, read.Status);
        Contract.AssertResponse(read, "/api/extensions/file");
        Assert.Equal("\"" + Hash(read) + "\"", read.Headers.ETag!.ToString());
        Assert.Equal(404, (await host.GetAsync("/api/extensions/file?path=missing.json")).Status);
        Assert.Equal(400, (await host.GetAsync("/api/extensions/file?path=../maquettiste.json")).Status);

        // A new rule: 201, project.changed, and the next validation runs it.
        Assert.Equal(428, (await host.SendJsonAsync("PUT", "/api/extensions/file?path=rules/naming.js", new { text = Rule })).Status);
        var created = await host.SendJsonAsync("PUT", "/api/extensions/file?path=rules/naming.js", new { text = Rule }, r => r.Header("If-None-Match", "*"));
        Assert.Equal(201, created.Status);
        Contract.AssertResponse(created, "/api/extensions/file");
        Assert.Empty(created.Json["diagnostics"]!.AsArray());
        Assert.NotEmpty(host.Published("project.changed"));
        var validation = await host.SendJsonAsync("POST", "/api/validate", new { });
        Assert.Contains(validation.Json["diagnostics"]!.AsArray(), d => d!["rule"]!.GetValue<string>() == "x/short-names");

        // A syntax error is saved and reported at once with its line.
        var broken = await host.SendJsonAsync("PUT", "/api/extensions/file?path=rules/naming.js", new { text = "maquettiste.rule({\n  id: \"x\",,\n});\n" },
            r => r.IfMatch(Hash(created)));
        Assert.Equal(200, broken.Status);
        Contract.AssertResponse(broken, "/api/extensions/file");
        var syntax = Assert.Single(broken.Json["diagnostics"]!.AsArray())!;
        Assert.Equal(("MQ5002", 2), (syntax["rule"]!.GetValue<string>(), syntax["line"]!.GetValue<int>()));

        // A stale write is 409 with the disk version; an invalid schema is 422.
        var stale = await host.SendJsonAsync("PUT", "/api/extensions/file?path=rules/naming.js", new { text = "//" }, r => r.IfMatch(Hash(created)));
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/extensions/file");
        Assert.Equal(Hash(broken), Hash(stale));
        var invalid = await host.SendJsonAsync("PUT", "/api/extensions/file?path=audit.json", new { text = "{ \"name\": \"audit\" }" }, r => r.Header("If-None-Match", "*"));
        Assert.Equal(422, invalid.Status);
        Contract.AssertResponse(invalid, "/api/extensions/file");

        // Rename within its kind, then delete.
        var moved = await host.SendJsonAsync("POST", "/api/extensions/file/move", new { from = "rules/naming.js", to = "rules/names.js" }, r => r.IfMatch(Hash(broken)));
        Assert.Equal(200, moved.Status);
        Contract.AssertResponse(moved, "/api/extensions/file/move");
        Assert.True(File.Exists(host.PathOf(".maquettiste/extensions/rules/names.js")));
        var wrongKind = await host.SendJsonAsync("POST", "/api/extensions/file/move", new { from = "rules/names.js", to = "names.json" }, r => r.IfMatch(Hash(moved)));
        Assert.Equal(422, wrongKind.Status);
        var deleted = await host.SendAsync(TestRequest.Local("DELETE", "/api/extensions/file?path=rules/names.js").With(r => r.IfMatch(Hash(moved))));
        Assert.Equal(200, deleted.Status);
        Contract.AssertResponse(deleted, "/api/extensions/file");
        Assert.False(File.Exists(host.PathOf(".maquettiste/extensions/rules/names.js")));
    }
}
