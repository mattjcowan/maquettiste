using System.IO.Compression;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Functions.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Maquettiste.Functions.Tests;

/// <summary>Model snapshots over the API (docs/engineering/snapshots.md): every response checked against the contract.</summary>
public sealed class SnapshotEndpointTests
{
    [Fact]
    public async Task Snapshots_are_taken_listed_read_renamed_published_and_deleted()
    {
        await using var host = EditorHost.Create();

        var created = await host.SendJsonAsync("POST", "/api/snapshots", new { name = "Release 1", description = "For review" });
        Assert.Equal(201, created.Status);
        Contract.AssertResponse(created, "/api/snapshots");
        var id = created.Json["id"]!.GetValue<string>();
        Assert.Equal("/api/snapshots/" + id, created.Headers.Location.ToString());
        Assert.StartsWith("release-1-", id, StringComparison.Ordinal);
        Assert.Equal(("local", false, false), (created.Json["author"]!.GetValue<string>(), created.Json["includesPacks"]!.GetValue<bool>(), created.Json["published"]!.GetValue<bool>()));
        Assert.True(File.Exists(Path.Combine(host.ModelRoot, "model-snapshots", id + ".zip")));

        var withPacks = await host.SendJsonAsync("POST", "/api/snapshots", new { name = "With packs", includePacks = true });
        Assert.True(withPacks.Json["includesPacks"]!.GetValue<bool>());

        var list = await host.GetAsync("/api/snapshots");
        Contract.AssertResponse(list, "/api/snapshots");
        Assert.Equal(2, list.Json.AsArray().Count);

        var patched = await host.SendJsonAsync("PATCH", "/api/snapshots/" + id, new { name = "Release 1 (reviewed)", published = true });
        Assert.Equal(200, patched.Status);
        Contract.AssertResponse(patched, "/api/snapshots/{id}");
        Assert.Equal((id, "Release 1 (reviewed)", "For review", true), (patched.Json["id"]!.GetValue<string>(), patched.Json["name"]!.GetValue<string>(),
            patched.Json["description"]!.GetValue<string>(), patched.Json["published"]!.GetValue<bool>()));
        var read = await host.GetAsync("/api/snapshots/" + id);
        Contract.AssertResponse(read, "/api/snapshots/{id}");
        Assert.True(JsonNode.DeepEquals(patched.Json, read.Json));

        var bad = await host.SendJsonAsync("POST", "/api/snapshots", new { name = "" });
        Assert.Equal(400, bad.Status);
        Contract.AssertResponse(bad, "/api/snapshots");
        var missing = await host.GetAsync("/api/snapshots/missing-20260101-000000");
        Assert.Equal(404, missing.Status);
        Contract.AssertResponse(missing, "/api/snapshots/{id}");

        var deleted = await host.SendAsync(TestRequest.Local("DELETE", "/api/snapshots/" + id));
        Assert.Equal(204, deleted.Status);
        Contract.AssertResponse(deleted, "/api/snapshots/{id}");
        Assert.Equal(404, (await host.SendAsync(TestRequest.Local("DELETE", "/api/snapshots/" + id))).Status);
        Assert.Single((await host.GetAsync("/api/snapshots")).Json.AsArray());
    }

    [Fact]
    public async Task The_model_reads_answer_as_of_a_snapshot_and_writes_against_it_are_refused()
    {
        await using var host = EditorHost.Create();
        var id = (await host.SendJsonAsync("POST", "/api/snapshots", new { name = "Before" })).Json["id"]!.GetValue<string>();
        var customer = await host.GetAsync("/api/model/elements/" + EditorHost.CustomerId);
        var json = customer.Json["json"]!.AsObject();
        json["name"] = "Client";
        var saved = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.CustomerId, json, r => r.IfMatch(customer.Json["hash"]!.GetValue<string>()));
        Assert.Equal(200, saved.Status);
        var q = "?snapshot=" + id;

        var index = await host.GetAsync("/api/model/index" + q);
        Assert.Equal(200, index.Status);
        Contract.AssertResponse(index, "/api/model/index");
        Assert.Equal(id, index.Headers["X-Maquettiste-Snapshot"].ToString());
        Assert.Contains(index.Json.AsArray(), r => r!["name"]!.GetValue<string>() == "Customer");
        Assert.Contains((await host.GetAsync("/api/model/index")).Json.AsArray(), r => r!["name"]!.GetValue<string>() == "Client");

        var element = await host.GetAsync("/api/model/elements/" + EditorHost.CustomerId + q);
        Contract.AssertResponse(element, "/api/model/elements/{id}");
        Assert.Equal("Customer", element.Json["json"]!["name"]!.GetValue<string>());
        Assert.Equal(customer.Json["hash"]!.GetValue<string>(), element.Json["hash"]!.GetValue<string>());
        Contract.AssertResponse(await host.GetAsync("/api/model/elements?kind=entity" + q.Replace('?', '&')), "/api/model/elements");
        Contract.AssertResponse(await host.GetAsync("/api/model/kinds" + q), "/api/model/kinds");
        Contract.AssertResponse(await host.GetAsync("/api/model/references/" + EditorHost.CustomerId + q), "/api/model/references/{id}");
        Contract.AssertResponse(await host.SendJsonAsync("POST", "/api/model/elements/read" + q, new { ids = new[] { EditorHost.CustomerId } }), "/api/model/elements/read");
        var validation = await host.SendJsonAsync("POST", "/api/validate" + q, new { });
        Assert.Equal(200, validation.Status);
        Contract.AssertResponse(validation, "/api/validate");
        var diagram = await host.GetAsync("/api/diagrams/" + EditorHost.OverviewDiagramId + q);
        Contract.AssertResponse(diagram, "/api/diagrams/{id}");
        var view = await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/view" + q);
        Assert.Equal(200, view.Status);
        Contract.AssertResponse(view, "/api/databases/{id}/view");
        Assert.NotNull(view.Json["view"]);
        Contract.AssertResponse(await host.GetAsync("/api/databases/" + EditorHost.MainDatabaseId + "/tables" + q), "/api/databases/{id}/tables");
        Contract.AssertResponse(await host.GetAsync("/api/model/resolved?scope=entities&snapshot=" + id), "/api/model/resolved");
        Contract.AssertResponse(await host.GetAsync("/api/project/settings" + q), "/api/project/settings");
        var preview = await host.SendJsonAsync("POST", "/api/templates/preview" + q, new { pack = "csharp-dapper", unit = "entity", elementId = EditorHost.CustomerId });
        Assert.Equal(200, preview.Status);
        Contract.AssertResponse(preview, "/api/templates/preview");
        Assert.Contains("Customer", preview.Text, StringComparison.Ordinal);

        var write = await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.CustomerId + q, json, r => r.IfMatch(customer.Json["hash"]!.GetValue<string>()));
        Assert.Equal((409, "snapshot-read-only"), (write.Status, write.ProblemCode));
        Assert.Equal("snapshot-read-only", (await host.SendJsonAsync("POST", "/api/model/batch" + q, new { operations = Array.Empty<object>() })).ProblemCode);
        Assert.Equal((400, "snapshot-unsupported"), ((await host.GetAsync("/api/packs" + q)).Status, (await host.GetAsync("/api/packs" + q)).ProblemCode));
        var unknown = await host.GetAsync("/api/model/index?snapshot=missing-20260101-000000");
        Assert.Equal((404, "not-found"), (unknown.Status, unknown.ProblemCode));
        Contract.AssertResponse(unknown, "/api/model/index");
        Assert.Equal("Client", (await host.GetAsync("/api/model/elements/" + EditorHost.CustomerId)).Json["json"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Compare_lists_what_changed_and_the_fields_of_one_element()
    {
        await using var host = EditorHost.Create();
        var id = (await host.SendJsonAsync("POST", "/api/snapshots", new { name = "Before" })).Json["id"]!.GetValue<string>();
        var customer = await host.GetAsync("/api/model/elements/" + EditorHost.CustomerId);
        var json = customer.Json["json"]!.AsObject();
        json["name"] = "Client";
        Assert.Equal(200, (await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.CustomerId, json, r => r.IfMatch(customer.Json["hash"]!.GetValue<string>()))).Status);

        var compare = await host.GetAsync("/api/snapshots/compare?from=" + id);
        Assert.Equal(200, compare.Status);
        Contract.AssertResponse(compare, "/api/snapshots/compare");
        Assert.Equal("working", compare.Json["to"]!.GetValue<string>());
        var row = Assert.Single(compare.Json["elements"]!.AsArray());
        Assert.Equal(("changed", "Client", "Customer"), (row!["change"]!.GetValue<string>(), row["name"]!.GetValue<string>(), row["previousName"]!.GetValue<string>()));

        var detail = await host.GetAsync($"/api/snapshots/compare/element?from={id}&to=working&id={EditorHost.CustomerId}");
        Assert.Equal(200, detail.Status);
        Contract.AssertResponse(detail, "/api/snapshots/compare/element");
        Assert.Equal("Customer", detail.Json["before"]!["name"]!.GetValue<string>());
        Assert.Contains(detail.Json["fields"]!.AsArray(), f => f!["pointer"]!.GetValue<string>() == "/name");

        Contract.AssertResponse(await host.GetAsync("/api/snapshots/compare?from=" + id + "&limit=0"), "/api/snapshots/compare");
        Assert.Equal(400, (await host.GetAsync("/api/snapshots/compare")).Status);
        var missing = await host.GetAsync("/api/snapshots/compare?from=missing-20260101-000000");
        Assert.Equal(404, missing.Status);
        Contract.AssertResponse(missing, "/api/snapshots/compare");
        Assert.Equal(404, (await host.GetAsync($"/api/snapshots/compare/element?from={id}&id=01J92P0V0FJ23CGSNKM7P1W5V9")).Status);
    }

    [Fact]
    public async Task Restore_replaces_the_working_model_publishes_model_changed_and_is_refused_under_the_run_lock()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        await EditorHost.WaitForAsync(() => host.Store.Current is not null && host.Events.Watcher == "watching", "the background services");
        var id = (await host.SendJsonAsync("POST", "/api/snapshots", new { name = "Before" })).Json["id"]!.GetValue<string>();
        var customer = await host.GetAsync("/api/model/elements/" + EditorHost.CustomerId);
        var json = customer.Json["json"]!.AsObject();
        json["name"] = "Client";
        Assert.Equal(200, (await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.CustomerId, json, r => r.IfMatch(customer.Json["hash"]!.GetValue<string>()))).Status);
        await EditorHost.WaitForAsync(() => host.Published("model.changed").Count == 1, "the save's model.changed");

        // What a generation run holds: the run lock file, opened exclusively.
        Directory.CreateDirectory(Path.Combine(host.ModelRoot, ".cache"));
        await using (var held = new FileStream(Path.Combine(host.ModelRoot, ".cache", "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await host.SendJsonAsync("POST", $"/api/snapshots/{id}/restore", new { });
            Assert.Equal((409, "run-locked"), (locked.Status, locked.ProblemCode));
            Contract.AssertResponse(locked, "/api/snapshots/{id}/restore");
        }

        var restored = await host.SendAsync(TestRequest.Local("POST", $"/api/snapshots/{id}/restore").WithJson("{}"));
        Assert.Equal(200, restored.Status);
        Contract.AssertResponse(restored, "/api/snapshots/{id}/restore");
        Assert.Equal("restored", restored.Json["outcome"]!.GetValue<string>());
        var safety = restored.Json["safety"]!["id"]!.GetValue<string>();
        Assert.StartsWith("before-restore-", safety, StringComparison.Ordinal);
        Assert.Contains(safety, restored.Json["undo"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Customer", (await host.GetAsync("/api/model/elements/" + EditorHost.CustomerId)).Json["json"]!["name"]!.GetValue<string>());
        await EditorHost.WaitForAsync(() => host.Published("model.changed").Count == 2, "the restore's model.changed");
        var changed = host.Published("model.changed")[1].Payload;
        Contract.AssertEvent("model.changed", changed);
        Assert.Equal(2, (await host.GetAsync("/api/snapshots")).Json.AsArray().Count);
        Assert.Equal(404, (await host.SendJsonAsync("POST", "/api/snapshots/missing-20260101-000000/restore", new { })).Status);
    }

    [Fact]
    public async Task Export_downloads_the_archive_and_import_takes_it_back_under_a_new_id()
    {
        await using var host = EditorHost.Create();
        var id = (await host.SendJsonAsync("POST", "/api/snapshots", new { name = "Shared" })).Json["id"]!.GetValue<string>();

        var export = await host.GetAsync($"/api/snapshots/{id}/export");
        Assert.Equal(200, export.Status);
        Contract.AssertResponse(export, "/api/snapshots/{id}/export");
        Assert.Equal("application/zip", export.ContentType);
        Assert.Contains(id + ".zip", export.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
        Assert.Equal(File.ReadAllBytes(Path.Combine(host.ModelRoot, "model-snapshots", id + ".zip")), export.Body);

        var imported = await host.SendAsync(TestRequest.Local("POST", "/api/snapshots/import").With(r => { r.Body = export.Body; r.ContentType = "application/zip"; }));
        Assert.Equal(201, imported.Status);
        Contract.AssertResponse(imported, "/api/snapshots/import");
        Assert.Equal(id + "-2", imported.Json["snapshot"]!["id"]!.GetValue<string>());

        var refused = await host.SendAsync(TestRequest.Local("POST", "/api/snapshots/import").With(r => { r.Body = BadArchive(); r.ContentType = "application/zip"; }));
        Assert.Equal(422, refused.Status);
        Contract.AssertResponse(refused, "/api/snapshots/import");
        Assert.Equal("MQ1011", refused.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());
        var notZip = await host.SendAsync(TestRequest.Local("POST", "/api/snapshots/import").With(r => { r.Body = "x"u8.ToArray(); r.ContentType = "application/octet-stream"; }));
        Assert.Equal(415, notZip.Status);
        Assert.Equal(404, (await host.GetAsync("/api/snapshots/missing-20260101-000000/export")).Status);
    }

    [Fact]
    public async Task A_viewer_lists_and_compares_but_may_not_take_restore_delete_or_import()
    {
        await using var host = EditorHost.Create();
        var library = host.Services.GetRequiredService<SnapshotLibrary>();
        var id = (await host.SendJsonAsync("POST", "/api/snapshots", new { name = "S" })).Json["id"]!.GetValue<string>();

        async Task<int> AsViewer(string method, string path, Func<HttpContext, Task<IResult>> call)
        {
            var context = TestRequest.Local(method, path).WithJson("{}").ToContext(host.Site, host.Services);
            context.User = new EditorUser("viewer-1", "Viewer", "viewer", "cookie").ToPrincipal();
            context.Response.Body = new MemoryStream();
            await (await call(context)).ExecuteAsync(context);
            return context.Response.StatusCode;
        }

        Assert.Equal(200, await AsViewer("GET", "/api/snapshots", c => SnapshotEndpoints.List(c, library, EditorHost.Ct)));
        Assert.Equal(200, await AsViewer("GET", "/api/snapshots/compare", c => SnapshotEndpoints.Compare(id, null, null, null, c, library, EditorHost.Ct)));
        Assert.Equal(403, await AsViewer("POST", "/api/snapshots", c => SnapshotEndpoints.Create(c, library, EditorHost.Ct)));
        Assert.Equal(403, await AsViewer("PATCH", "/api/snapshots/" + id, c => SnapshotEndpoints.Update(id, c, library, EditorHost.Ct)));
        Assert.Equal(403, await AsViewer("POST", $"/api/snapshots/{id}/restore", c => SnapshotEndpoints.Restore(id, c, library, EditorHost.Ct)));
        Assert.Equal(403, await AsViewer("DELETE", "/api/snapshots/" + id, c => SnapshotEndpoints.Delete(id, c, library, EditorHost.Ct)));
        Assert.Equal(403, await AsViewer("POST", "/api/snapshots/import", c => SnapshotEndpoints.Import(c, library, EditorHost.Ct)));
        Assert.Equal(403, await AsViewer("GET", $"/api/snapshots/{id}/export", c => Task.FromResult(SnapshotEndpoints.Export(id, c, library))));
    }

    [Fact]
    public async Task An_import_over_the_hosts_body_bound_is_refused_as_too_large_not_as_an_unreadable_model()
    {
        await using var host = EditorHost.Create();
        var library = host.Services.GetRequiredService<SnapshotLibrary>();

        async Task<(int Status, JsonNode Body)> Import(Action<HttpContext> arrange)
        {
            var context = TestRequest.Local("POST", "/api/snapshots/import").ToContext(host.Site, host.Services);
            context.Request.ContentType = "application/zip";
            context.User = new EditorUser("maintainer-1", "Maintainer", "maintainer", "cookie").ToPrincipal();
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyBound(1024));
            arrange(context);
            context.Response.Body = new MemoryStream();
            await (await SnapshotEndpoints.Import(context, library, EditorHost.Ct)).ExecuteAsync(context);
            context.Response.Body.Position = 0;
            return (context.Response.StatusCode, JsonNode.Parse(context.Response.Body)!);
        }

        // Announced by Content-Length: refused before a byte is read.
        var announced = await Import(c =>
        {
            c.Request.ContentLength = 4096;
            c.Request.Body = new MemoryStream(new byte[4096]);
        });
        Assert.Equal(413, announced.Status);
        Assert.True(announced.Body["tooLarge"]!.GetValue<bool>());
        Assert.Equal("MQ1011", announced.Body["diagnostics"]![0]!["rule"]!.GetValue<string>());
        Assert.Contains("1024 bytes", announced.Body["diagnostics"]![0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // Chunked: the host's reader stops at its bound with a 413 BadHttpRequestException, which is the same refusal.
        var chunked = await Import(c => c.Request.Body = new OverBoundStream());
        Assert.Equal(413, chunked.Status);
        Assert.True(chunked.Body["tooLarge"]!.GetValue<bool>());
        var stored = Path.Combine(host.ModelRoot, "model-snapshots");
        Assert.True(!Directory.Exists(stored) || !Directory.EnumerateFiles(stored, "*.zip").Any());
    }

    private sealed class BodyBound(long max) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; } = max;
    }

    private sealed class OverBoundStream : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
    }

    private static byte[] BadArchive()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry("../outside.json").Open();
            stream.Write("{}"u8);
        }

        return output.ToArray();
    }
}
