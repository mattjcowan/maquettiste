using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>Realtime events (phase2-design.md section 3.6): what is published, how often, and within the hub's 256 KB.</summary>
public sealed class EventTests
{
    private const int HubLimit = 256 * 1024;

    private static async Task<TestResponse> RenameNotesAsync(EditorHost host, string name)
    {
        var invoice = await host.GetAsync("/api/model/elements/" + EditorHost.InvoiceId);
        var json = invoice.Json["json"]!.AsObject();
        json["attributes"]![5]!["name"] = name;
        return await host.SendJsonAsync("PUT", "/api/model/elements/" + EditorHost.InvoiceId, json, r => r.IfMatch(invoice.Json["hash"]!.GetValue<string>()));
    }

    private static async Task WaitLoadedAsync(EditorHost host) =>
        await EditorHost.WaitForAsync(() => host.Store.Current is not null && host.Events.Watcher == "watching" && host.Events.Worker == "running", "the background services");

    [Fact]
    public async Task A_save_publishes_one_model_changed_and_then_one_validation_completed()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        await WaitLoadedAsync(host);

        var saved = await RenameNotesAsync(host, "remarks");
        await EditorHost.WaitForAsync(() => host.Published("validation.completed").Count > 0, "validation.completed");
        // The watcher reports the save's own write too; the engine recognises it by hash, so nothing more may follow.
        await Task.Delay(ModelWatcher.Quiet * 4 + EditorEvents.ValidationQuiet, EditorHost.Ct);

        Assert.Equal(200, saved.Status);
        var changed = Assert.Single(host.Published("model.changed"));
        Assert.Equal("all", changed.Target);
        Contract.AssertEvent("model.changed", changed.Payload);
        Assert.Equal("editor", changed.Payload["source"]!.GetValue<string>());
        Assert.Equal(saved.Json["hash"]!.GetValue<string>(), changed.Payload["changed"]![0]!["hash"]!.GetValue<string>());
        var validated = Assert.Single(host.Published("validation.completed"));
        Contract.AssertEvent("validation.completed", validated.Payload);
        Assert.Equal(0, validated.Payload["errors"]!.GetValue<int>());
        var names = host.Realtime.Published.Select(e => e.EventName).Where(n => n is "model.changed" or "validation.completed").ToList();
        Assert.Equal(["model.changed", "validation.completed"], names);
    }

    [Fact]
    public async Task A_disk_edit_seen_by_the_watcher_publishes_source_disk()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        await WaitLoadedAsync(host);
        var path = host.PathOf(".maquettiste/model/entities/customer.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["description"] = "Someone we bill, edited in a text editor.";

        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        await EditorHost.WaitForAsync(() => host.Published("model.changed").Count > 0, "model.changed from the watcher");

        var changed = host.Published("model.changed")[0].Payload;
        Contract.AssertEvent("model.changed", changed);
        Assert.Equal("disk", changed["source"]!.GetValue<string>());
        Assert.Equal(EditorHost.CustomerId, changed["changed"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_disk_edit_through_refresh_publishes_source_disk()
    {
        await using var host = EditorHost.Create();
        await host.Store.LoadAsync(EditorHost.Ct);
        using var subscription = host.Store.OnChanged(host.Events.OnModelChangedAsync);
        var path = host.PathOf(".maquettiste/model/entities/product.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["description"] = "Changed on disk.";
        File.WriteAllText(path, node.ToJsonString(), new UTF8Encoding(false));

        await host.Store.RefreshAsync([".maquettiste/model/entities/product.json"], EditorHost.Ct);

        var changed = Assert.Single(host.Published("model.changed")).Payload;
        Contract.AssertEvent("model.changed", changed);
        Assert.Equal("disk", changed["source"]!.GetValue<string>());
        Assert.Equal(EditorHost.ProductId, changed["changed"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_change_set_over_200_kb_is_cut_and_marked_truncated()
    {
        await using var host = EditorHost.Create();
        var changes = Enumerable.Range(0, 5000)
            .Select(i => new ElementChange($"01M3P{i:D21}", "entity", $".maquettiste/model/entities/entity-{i:D5}.json", new string('a', 64)))
            .ToList();
        var set = new ChangeSet(changes, [], ChangeSource.Disk, false);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(set, Api.JsonOptions).Length > HubLimit);

        await host.Events.OnModelChangedAsync(set, EditorHost.Ct);

        var published = Assert.Single(host.Realtime.Published, e => e.EventName == "model.changed");
        Assert.True(published.Payload!.Value.GetRawText().Length <= Api.MaxEventBytes);
        var payload = JsonNode.Parse(published.Payload.Value.GetRawText())!;
        Contract.AssertEvent("model.changed", payload);
        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.InRange(payload["changed"]!.AsArray().Count, 1, 4999);
    }

    [Fact]
    public async Task A_validation_report_over_200_kb_is_cut_and_keeps_its_counts()
    {
        await using var host = EditorHost.Create();
        // 2,500 entities without a key: one MQ3005 error each, far over 200 KB of diagnostics.
        var folder = host.PathOf(".maquettiste/model/entities/generated");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 2500; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"e{i:D4}.json"),
                $"{{\n  \"kind\": \"entity\",\n  \"id\": \"01M3Q{i:D21}\",\n  \"name\": \"Generated{i:D4}\"\n}}\n", new UTF8Encoding(false));
        }

        await host.Store.LoadAsync(EditorHost.Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(EditorHost.Ct);
        var loop = host.Events.RunValidationLoopAsync(cancel.Token);
        await host.Events.OnModelChangedAsync(ChangeSet.Empty(ChangeSource.Disk) with { Deleted = ["01M3Q000000000000000000000"] }, EditorHost.Ct);
        await EditorHost.WaitForAsync(() => host.Published("validation.completed").Count > 0, "validation.completed", 60);
        await cancel.CancelAsync();
        await loop;

        var published = host.Realtime.Published.Single(e => e.EventName == "validation.completed");
        Assert.True(published.Payload!.Value.GetRawText().Length <= Api.MaxEventBytes);
        var payload = JsonNode.Parse(published.Payload.Value.GetRawText())!;
        Contract.AssertEvent("validation.completed", payload);
        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.Equal(2500, payload["errors"]!.GetValue<int>());
        Assert.True(payload["diagnostics"]!.AsArray().Count < 2500);
    }

    [Fact]
    public async Task A_settings_save_publishes_one_project_changed_then_validation_even_with_the_watcher_reporting_it()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        await WaitLoadedAsync(host);
        var read = await host.GetAsync("/api/project/settings");
        var json = read.Json["json"]!.AsObject();
        json["name"] = "billing-renamed";

        var saved = await host.SendJsonAsync("PUT", "/api/project/settings", json, r => r.IfMatch(read.Json["hash"]!.GetValue<string>()));
        await EditorHost.WaitForAsync(() => host.Published("validation.completed").Count > 0, "validation.completed");
        await Task.Delay(ModelWatcher.Quiet * 4 + EditorEvents.ValidationQuiet, EditorHost.Ct);

        Assert.Equal(200, saved.Status);
        var changed = Assert.Single(host.Published("project.changed"));
        Contract.AssertEvent("project.changed", changed.Payload);
        Assert.Equal(saved.Json["hash"]!.GetValue<string>(), changed.Payload["settingsHash"]!.GetValue<string>());
        Assert.Single(host.Published("validation.completed"));
        Assert.Empty(host.Published("model.changed"));
        var order = host.Realtime.Published.Select(e => e.EventName).Where(n => n is "project.changed" or "validation.completed").ToList();
        Assert.Equal(["project.changed", "validation.completed"], order);
    }

    [Fact]
    public async Task A_disk_edit_of_maquettiste_json_publishes_one_project_changed_then_validation()
    {
        await using var host = EditorHost.Create();
        host.StartBackground();
        await WaitLoadedAsync(host);
        var path = host.PathOf(".maquettiste/maquettiste.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["name"] = "edited-by-hand";

        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        await EditorHost.WaitForAsync(() => host.Published("validation.completed").Count > 0, "validation.completed");
        await Task.Delay(ModelWatcher.Quiet * 4, EditorHost.Ct);

        var changed = Assert.Single(host.Published("project.changed"));
        Assert.Equal(host.Store.Current!.SettingsHash, changed.Payload["settingsHash"]!.GetValue<string>());
        Assert.Equal("edited-by-hand", host.Store.Current.Settings.Name);
        Assert.Single(host.Published("validation.completed"));
    }

    [Fact]
    public async Task A_pack_change_publishes_project_changed_though_the_settings_hash_is_the_same()
    {
        await using var host = EditorHost.Create();
        await host.Store.LoadAsync(EditorHost.Ct);
        var hash = host.Store.Current!.SettingsHash;

        await ModelWatcher.ApplyAsync(["maquettiste.json"], host.Store, host.Events, EditorHost.Ct);
        await ModelWatcher.ApplyAsync(["maquettiste.json"], host.Store, host.Events, EditorHost.Ct);
        await ModelWatcher.ApplyAsync(["templates/sql-ddl/table.scriban"], host.Store, host.Events, EditorHost.Ct);

        var events = host.Published("project.changed");
        Assert.Equal(2, events.Count); // the settings report once (deduplicated by hash), the pack edit once more
        Assert.All(events, e => Assert.Equal(hash, e.Payload["settingsHash"]!.GetValue<string>()));
    }

    [Fact]
    public void The_watcher_ignores_the_paths_the_engine_owns()
    {
        var root = Path.Combine(Path.GetTempPath(), "model");
        Assert.Equal("model/entities/invoice.json", ModelWatcher.Relevant(root, Path.Combine(root, "model", "entities", "invoice.json")));
        Assert.Equal("maquettiste.json", ModelWatcher.Relevant(root, Path.Combine(root, "maquettiste.json")));
        Assert.Equal("templates/sql-ddl/table.scriban", ModelWatcher.Relevant(root, Path.Combine(root, "templates", "sql-ddl", "table.scriban")));
        Assert.Null(ModelWatcher.Relevant(root, Path.Combine(root, ".cache", "index", "x.bin")));
        Assert.Null(ModelWatcher.Relevant(root, Path.Combine(root, "manifest", "sql-ddl.json")));
        Assert.Null(ModelWatcher.Relevant(root, Path.Combine(root, "snapshots", "main.json")));
        Assert.Null(ModelWatcher.Relevant(root, Path.Combine(root, ".schema", "v1", "entity.json")));
        Assert.Null(ModelWatcher.Relevant(root, Path.Combine(root, "model", "entities", ".invoice.json.mq-12-3.tmp")));
        Assert.Null(ModelWatcher.Relevant(root, Path.Combine(root, "model", "entities", "invoice.json.bak")));
        Assert.Null(ModelWatcher.Relevant(root, root));
        Assert.Null(ModelWatcher.Relevant(root, Path.Combine(Path.GetTempPath(), "elsewhere.json")));
        // Reported through the real location of a root configured through a symbolic link (macOS: /var is /private/var).
        Assert.Equal("model/entities/invoice.json", ModelWatcher.Relevant("/var/folders/x/model", "/private/var/folders/x/model/model/entities/invoice.json", "/private/var/folders/x/model"));
        Assert.Null(ModelWatcher.Relevant("/var/folders/x/model", "/private/var/folders/x/model/model/entities/invoice.json"));
    }

    [Fact]
    public void Links_along_the_model_root_are_resolved()
    {
        var target = Directory.CreateTempSubdirectory("mq-real-");
        var link = Path.Combine(Path.GetTempPath(), "mq-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(link, target.FullName);
            Directory.CreateDirectory(Path.Combine(target.FullName, "model"));

            Assert.Equal(Path.Combine(ModelWatcher.ResolveLinks(target.FullName), "model"), ModelWatcher.ResolveLinks(Path.Combine(link, "model")));
        }
        finally
        {
            Directory.Delete(link);
            target.Delete(recursive: true);
        }
    }

    [Fact]
    public void Progress_is_published_at_most_every_250_ms_within_a_stage_except_stage_changes_and_ends()
    {
        var clock = new TestClock();
        var throttle = new ProgressThrottle(JobWorker.ProgressInterval, clock);
        static ProgressUpdate At(PipelineStage stage, int done, int total) => new(stage, done, total, null, null);

        Assert.True(throttle.ShouldPublish("a", At(PipelineStage.Load, 0, 10)));
        Assert.False(throttle.ShouldPublish("a", At(PipelineStage.Load, 1, 10)));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(throttle.ShouldPublish("a", At(PipelineStage.Load, 2, 10)));
        Assert.True(throttle.ShouldPublish("a", At(PipelineStage.Load, 10, 10))); // the end of a stage
        Assert.True(throttle.ShouldPublish("a", At(PipelineStage.Validate, 0, 10))); // a new stage
        Assert.True(throttle.ShouldPublish("b", At(PipelineStage.Validate, 1, 10))); // another job
        clock.Advance(TimeSpan.FromMilliseconds(249));
        Assert.False(throttle.ShouldPublish("a", At(PipelineStage.Validate, 3, 10)));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(throttle.ShouldPublish("a", At(PipelineStage.Validate, 4, 10)));

        // Over a simulated second of updates every millisecond, at most four per second go out (plus the stage's end).
        var published = 0;
        for (var i = 0; i < 1000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            if (throttle.ShouldPublish("c", At(PipelineStage.Render, i, 1000)))
                published++;
        }

        Assert.InRange(published, 4, 5);
    }

    [Fact]
    public async Task Job_completed_is_a_bounded_summary_with_exact_counts_for_huge_jobs()
    {
        await using var host = EditorHost.Create();
        var now = new DateTimeOffset(2026, 9, 28, 19, 2, 41, TimeSpan.Zero);
        var stalePaths = Enumerable.Range(0, 100_000).Select(i => $"src/Generated/Module{i / 100:D4}/Entity{i:D6}.g.cs").ToList();
        var staleUnits = Enumerable.Range(0, 1000).Select(i => $"csharp-dapper/entity:01M3R{i:D21}").ToList();
        var apply = new JobInfo("01M3MNY0J2S7Q8C1V4E6T9R3KD", JobKind.Apply, JobState.Succeeded, null, null, null,
            new ApplyResult(RunOutcome.Stale, staleUnits, stalePaths, null), null, now, now, now);
        var warnings = Enumerable.Range(0, 100_000)
            .Select(i => new Diagnostic("MQ6011", DiagnosticSeverity.Warning, $"Text outside file blocks in unit {i}.", null, null, null, null, null))
            .Append(new Diagnostic("MQ6006", DiagnosticSeverity.Error, "An error.", null, null, null, null, null))
            .ToList();
        var request = new GenerationRequest();
        var plan = new GenerationPlan("01M3MNY0HY08AVTKW927JJPSEF", request, 4, ["csharp-dapper"], [],
            [new FileChange("db/a.sql", FileChangeKind.Added, "sql-ddl", "sql-ddl/table", null, new string('b', 64), null)], warnings);
        var planJob = new JobInfo("01M3MNY0HTVJQSH4BX78RWN8F7", JobKind.Plan, JobState.Succeeded, null, null,
            new PlanResult(RunOutcome.Succeeded, plan), null, new string('x', 5000), now, now, now);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(apply, Api.JsonOptions).Length > HubLimit);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(planJob, Api.JsonOptions).Length > HubLimit);

        await host.Events.PublishJobCompletedAsync(apply, EditorHost.Ct);
        await host.Events.PublishJobCompletedAsync(planJob, EditorHost.Ct);

        var published = host.Realtime.Published.Where(e => e.EventName == "job.completed").ToList();
        Assert.Equal(2, published.Count);
        Assert.All(published, e => Assert.True(e.Payload!.Value.GetRawText().Length < 4096));
        var applied = JsonNode.Parse(published[0].Payload!.Value.GetRawText())!;
        Contract.AssertEvent("job.completed", applied);
        Assert.Equal("group:job:01M3MNY0J2S7Q8C1V4E6T9R3KD", published[0].Target);
        Assert.Equal("stale", applied["outcome"]!.GetValue<string>());
        Assert.Equal(100_000, applied["counts"]!["stalePaths"]!.GetValue<int>());
        Assert.Equal(1000, applied["counts"]!["staleUnits"]!.GetValue<int>());
        Assert.Null(applied["planId"]);
        var planned = JsonNode.Parse(published[1].Payload!.Value.GetRawText())!;
        Contract.AssertEvent("job.completed", planned);
        Assert.Equal(100_000, planned["counts"]!["warnings"]!.GetValue<int>());
        Assert.Equal(1, planned["counts"]!["errors"]!.GetValue<int>());
        Assert.Equal(1, planned["counts"]!["changes"]!.GetValue<int>());
        Assert.Equal("01M3MNY0HY08AVTKW927JJPSEF", planned["planId"]!.GetValue<string>());
        Assert.Equal(JobCompletedEvent.MaxErrorLength, planned["error"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task Presence_reports_publish_the_live_selections_to_editors()
    {
        await using var host = EditorHost.Create();
        host.Realtime.Connect("conn-local", user: "local");
        host.Realtime.Connect("conn-other", user: "token");

        var reported = await host.SendJsonAsync("PUT", "/api/presence", new JsonObject
        {
            ["connectionId"] = "conn-local", ["elementId"] = EditorHost.InvoiceId, ["workspace"] = "entities",
        });
        var notMine = await host.SendJsonAsync("PUT", "/api/presence", new JsonObject { ["connectionId"] = "conn-other" });
        var unknown = await host.SendJsonAsync("PUT", "/api/presence", new JsonObject { ["connectionId"] = "conn-gone" });
        var badWorkspace = await host.SendJsonAsync("PUT", "/api/presence", new JsonObject { ["connectionId"] = "conn-local", ["workspace"] = "templates" });

        Assert.Equal(204, reported.Status);
        Contract.AssertResponse(reported, "/api/presence");
        var presence = Assert.Single(host.Published("presence.changed"));
        Assert.Equal("group:editors", presence.Target);
        Contract.AssertEvent("presence.changed", presence.Payload);
        var entry = presence.Payload["editors"]!.AsArray().Single()!;
        Assert.Equal("local", entry["user"]!.GetValue<string>());
        Assert.Equal(EditorHost.InvoiceId, entry["elementId"]!.GetValue<string>());
        Assert.Equal(404, notMine.Status);
        Contract.AssertResponse(notMine, "/api/presence");
        Assert.Equal(404, unknown.Status);
        Assert.Equal(400, badWorkspace.Status);
    }

    [Fact]
    public async Task An_oversize_or_non_ulid_element_id_is_refused_and_later_reports_still_publish()
    {
        await using var host = EditorHost.Create();
        host.Realtime.Connect("conn-a", user: "local");
        host.Realtime.Connect("conn-b", user: "local");

        var huge = await host.SendJsonAsync("PUT", "/api/presence", new JsonObject { ["connectionId"] = "conn-a", ["elementId"] = new string('X', 300 * 1024) });
        var notUlid = await host.SendJsonAsync("PUT", "/api/presence", new JsonObject { ["connectionId"] = "conn-a", ["elementId"] = "customers" });
        var fine = await host.SendJsonAsync("PUT", "/api/presence", new JsonObject
        {
            ["connectionId"] = "conn-b", ["elementId"] = EditorHost.InvoiceId, ["workspace"] = "entities",
        });

        Assert.Equal(400, huge.Status);
        Assert.Equal("bad-request", huge.ProblemCode);
        Assert.Equal(400, notUlid.Status);
        Assert.Equal(204, fine.Status);
        Assert.Empty(host.HostFailures);
        var presence = Assert.Single(host.Published("presence.changed"));
        var entry = presence.Payload["editors"]!.AsArray().Single()!;
        Assert.Equal("conn-b", entry["connectionId"]!.GetValue<string>());
        Assert.All(host.Realtime.Published, e => Assert.True((e.Payload?.GetRawText().Length ?? 0) <= 256 * 1024, e.EventName + " is over 256 KB"));
    }

    [Fact]
    public void The_presence_event_stays_within_the_event_budget()
    {
        var now = DateTimeOffset.UnixEpoch;
        var entries = Enumerable.Range(0, 5000).Select(i => new PresenceEntry($"conn-{i:D5}", new string('u', 100), EditorHost.InvoiceId, "entities", now)).ToList();

        var bounded = PresenceChangedEvent.Bounded(entries, Api.MaxEventBytes);

        Assert.InRange(bounded.Editors.Count, 1, entries.Count - 1);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bounded, Api.JsonOptions).Length <= Api.MaxEventBytes);
    }

    [Fact]
    public async Task The_periodic_rescan_prunes_presence_of_closed_connections_and_publishes_it()
    {
        await using var host = EditorHost.Create();
        host.Realtime.Connect("conn-a", user: "local");
        host.Realtime.Connect("conn-b", user: "local");
        await host.SendJsonAsync("PUT", "/api/presence", new JsonObject { ["connectionId"] = "conn-a", ["workspace"] = "entities" });
        await host.SendJsonAsync("PUT", "/api/presence", new JsonObject { ["connectionId"] = "conn-b", ["workspace"] = "database" });
        host.Realtime.Disconnect("conn-b");
        var presence = (PresenceRegistry)host.Services.GetService(typeof(PresenceRegistry))!;

        await ModelWatcher.Rescan(host.Store, presence, host.Events, host.Realtime, EditorHost.Ct);
        await ModelWatcher.Rescan(host.Store, presence, host.Events, host.Realtime, EditorHost.Ct);

        var events = host.Published("presence.changed");
        Assert.Equal(3, events.Count); // two reports, one prune; the second rescan finds nothing to prune
        Assert.Equal(["conn-a"], events[^1].Payload["editors"]!.AsArray().Select(e => e!["connectionId"]!.GetValue<string>()));
    }
}
