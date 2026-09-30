using System.Text.Json.Nodes;
using Maquettiste.Functions.Tests.Support;

namespace Maquettiste.Functions.Tests;

/// <summary>The process operations of phase-3-design.md section 4.4 over the gate 3 fixture, in contract shape.</summary>
public sealed class ProcessEndpointTests
{
    private const string Sales = "01JQPRC0000000000000000001";
    private const string Purchase = "01JQPRC0000000000000000002";
    private const string BudgetRejected = "01JQSCN0000000000000000010";
    private const string PurchaseFile = ".maquettiste/model/processes/purchase-approval.json";

    [Fact]
    public async Task Simulate_replays_a_scenario_deterministically_and_refuses_an_invalid_draft_with_422()
    {
        await using var host = EditorHost.CreateModel("processes");
        var body = new { scenario = BudgetRejected };
        var first = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/simulate", body);
        Assert.Equal(200, first.Status);
        Contract.AssertResponse(first, "/api/processes/{id}/simulate");
        Assert.Equal(["01JQSTA0000000000000000106", "01JQSTA0000000000000000109"], first.Json["configuration"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal([-1, 0, 1, 2], first.Json["trace"]!.AsArray().Select(t => t!["index"]!.GetValue<int>()));
        Assert.False(first.Json["final"]!.GetValue<bool>());
        var again = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/simulate", body);
        Assert.Equal(first.Text, again.Text);

        var tail = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/simulate", new { scenario = BudgetRejected, from = 2 });
        Assert.Equal([2], tail.Json["trace"]!.AsArray().Select(t => t!["index"]!.GetValue<int>()));

        var fresh = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/simulate", new { });
        Assert.Equal(200, fresh.Status);
        Contract.AssertResponse(fresh, "/api/processes/{id}/simulate");
        Assert.NotEmpty(fresh.Json["enabled"]!.AsArray());

        var document = JsonNode.Parse(File.ReadAllText(host.PathOf(PurchaseFile)))!.AsObject();
        document["displayName"] = "Purchase approval (draft)";
        var draft = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/simulate", new JsonObject { ["document"] = document.DeepClone(), ["scenario"] = BudgetRejected });
        Assert.Equal(200, draft.Status);
        Assert.NotEqual(first.Json["processHash"]!.GetValue<string>(), draft.Json["processHash"]!.GetValue<string>());
        Assert.Equal(first.Json["configuration"]!.ToJsonString(), draft.Json["configuration"]!.ToJsonString());

        document["transitions"]![0]!["source"] = "01JQSTA0000000000000009999";
        var broken = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/simulate", new JsonObject { ["document"] = document });
        Assert.Equal(422, broken.Status);
        Contract.AssertResponse(broken, "/api/processes/{id}/simulate");
        Assert.NotEmpty(broken.Json["diagnostics"]!.AsArray());

        var missing = await host.SendJsonAsync("POST", "/api/processes/01JQPRC0000000000000009999/simulate", new { });
        Assert.Equal(404, missing.Status);
        Contract.AssertResponse(missing, "/api/processes/{id}/simulate");
        var bad = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/simulate", new { steps = new[] { 1 } });
        Assert.Equal(400, bad.Status);
    }

    [Fact]
    public async Task Verify_passes_all_13_fixture_scenarios_and_names_a_failure()
    {
        await using var host = EditorHost.CreateModel("processes");
        var total = 0;
        foreach (var process in new[] { Sales, Purchase })
        {
            var verify = await host.SendJsonAsync("POST", $"/api/processes/{process}/verify", new { });
            Assert.Equal(200, verify.Status);
            Contract.AssertResponse(verify, "/api/processes/{id}/verify");
            Assert.True(verify.Json["passed"]!.GetValue<bool>(), verify.Text);
            total += verify.Json["results"]!.AsArray().Count;
        }

        Assert.Equal(13, total);
        var one = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/verify", new { scenarios = new[] { "BudgetRejected" } });
        Assert.Equal(BudgetRejected, one.Json["results"]![0]!["scenario"]!.GetValue<string>());
        Assert.Equal(404, (await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/verify", new { scenarios = new[] { "SmallOrder" } })).Status);

        // A wrong expectation fails with MQ9302 at its step.
        var path = host.PathOf(".maquettiste/model/scenarios/purchase-approval/budget-rejected.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"01JQSTA0000000000000000109\"", "\"01JQSTA0000000000000000108\"", StringComparison.Ordinal));
        await host.Store.RescanAsync(false, EditorHost.Ct);
        var failed = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/verify", new { scenarios = new[] { BudgetRejected } });
        Contract.AssertResponse(failed, "/api/processes/{id}/verify");
        var failure = failed.Json["results"]![0]!["failure"]!;
        Assert.Equal(("MQ9302", 2), (failure["rule"]!.GetValue<string>(), failure["step"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Record_fills_expectations_from_the_replay_and_the_recorded_scenario_verifies()
    {
        await using var host = EditorHost.CreateModel("processes");
        var request = new JsonObject
        {
            ["name"] = "RecordedBudget",
            ["start"] = new JsonObject { ["context"] = new JsonObject { ["01JQATT0000000000000000201"] = 2500 } },
            ["steps"] = new JsonArray(
                new JsonObject { ["event"] = "01JQPRX0000000000000000014", ["actor"] = "01JQACT0000000000000000008" },
                new JsonObject { ["input"] = "invoke-error", ["invoke"] = "01JQPRX0000000000000000019" }),
        };
        var preview = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/scenarios?dryRun=true", request.DeepClone());
        Assert.Equal(200, preview.Status);
        Contract.AssertResponse(preview, "/api/processes/{id}/scenarios");
        Assert.False(preview.Json["applied"]!.GetValue<bool>());
        Assert.False(File.Exists(host.PathOf(".maquettiste/model/scenarios/purchase-approval/recorded-budget.json")));

        var created = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/scenarios", request);
        Assert.Equal(201, created.Status);
        Contract.AssertResponse(created, "/api/processes/{id}/scenarios");
        var steps = created.Json["element"]!["steps"]!.AsArray();
        Assert.Equal(2, steps.Count);
        Assert.Equal("[\"01JQSTA0000000000000000106\",\"01JQSTA0000000000000000108\"]", steps[1]!["expect"]!["states"]!.ToJsonString());
        Assert.True(File.Exists(host.PathOf(".maquettiste/model/scenarios/purchase-approval/recorded-budget.json")));
        var verify = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/verify", new { scenarios = new[] { created.Json["id"]!.GetValue<string>() } });
        Assert.True(verify.Json["passed"]!.GetValue<bool>(), verify.Text);

        var unnamed = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/scenarios", new { steps = Array.Empty<object>() });
        Assert.Equal(400, unnamed.Status);
    }

    [Fact]
    public async Task Export_then_import_into_the_process_round_trips_bytes_and_a_stale_hash_is_409()
    {
        await using var host = EditorHost.CreateModel("processes");
        var before = File.ReadAllBytes(host.PathOf(PurchaseFile));
        var export = await host.GetAsync($"/api/processes/{Purchase}/export?format=xstate");
        Assert.Equal(200, export.Status);
        Contract.AssertResponse(export, "/api/processes/{id}/export");
        Assert.Equal("1", export.Headers["X-Maquettiste-Diagnostics"].ToString());
        Assert.Equal("0", export.Headers["X-Maquettiste-Warnings"].ToString());
        Assert.EndsWith("}\n", export.Text, StringComparison.Ordinal);
        Assert.Equal(export.Text, (await host.GetAsync($"/api/processes/{Purchase}/export")).Text);
        Assert.Equal(400, (await host.GetAsync($"/api/processes/{Purchase}/export?format=scxml")).Status);

        var hash = (await host.Store.GetElementAsync(Purchase, EditorHost.Ct))!.Hash;
        var request = new JsonObject { ["config"] = JsonNode.Parse(export.Text), ["into"] = Purchase, ["expectedHash"] = hash };
        var dry = await host.SendJsonAsync("POST", "/api/processes/import?format=xstate", request.DeepClone());
        Assert.Equal(200, dry.Status);
        Contract.AssertResponse(dry, "/api/processes/import");
        Assert.False(dry.Json["applied"]!.GetValue<bool>());
        Assert.Empty(dry.Json["created"]!.AsArray());
        Assert.Empty(dry.Json["removed"]!.AsArray());

        var applied = await host.SendJsonAsync("POST", "/api/processes/import?dryRun=false", request.DeepClone());
        Assert.Equal(200, applied.Status);
        Contract.AssertResponse(applied, "/api/processes/import");
        Assert.True(applied.Json["applied"]!.GetValue<bool>());
        Assert.Equal(before, File.ReadAllBytes(host.PathOf(PurchaseFile)));

        request["expectedHash"] = new string('0', 64);
        var stale = await host.SendJsonAsync("POST", "/api/processes/import?dryRun=false", request);
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/processes/import");
    }

    [Fact]
    public async Task Import_creates_a_new_process_only_when_applied()
    {
        await using var host = EditorHost.CreateModel("processes");
        var config = JsonNode.Parse("""{ "id": "Door", "initial": "Closed", "states": { "Closed": { "on": { "open": "Opened" } }, "Opened": { "on": { "close": "Closed" } } } }""");
        var request = new JsonObject { ["config"] = config, ["package"] = "purchasing" };
        var dry = await host.SendJsonAsync("POST", "/api/processes/import", request.DeepClone());
        Assert.Equal(200, dry.Status);
        Contract.AssertResponse(dry, "/api/processes/import");
        Assert.Equal("Door", dry.Json["document"]!["name"]!.GetValue<string>());
        Assert.NotEmpty(dry.Json["created"]!.AsArray());
        Assert.False(File.Exists(host.PathOf(".maquettiste/model/processes/door.json")));

        var applied = await host.SendJsonAsync("POST", "/api/processes/import?dryRun=false", request);
        Assert.Equal(200, applied.Status);
        Contract.AssertResponse(applied, "/api/processes/import");
        Assert.True(File.Exists(host.PathOf(".maquettiste/model/processes/door.json")));
        Assert.Equal(404, (await host.SendJsonAsync("POST", "/api/processes/import", new { config, package = "nowhere" })).Status);
        Assert.Equal(400, (await host.SendJsonAsync("POST", "/api/processes/import", new { package = "purchasing" })).Status);
    }

    [Fact]
    public async Task Sync_enum_plans_a_drifted_enum_applies_it_and_refuses_an_orchestration()
    {
        await using var host = EditorHost.CreateModel("processes");
        var inSync = await host.SendJsonAsync("POST", $"/api/processes/{Sales}/sync-enum", new { dryRun = true });
        Assert.Equal(200, inSync.Status);
        Contract.AssertResponse(inSync, "/api/processes/{id}/sync-enum");
        Assert.False(inSync.Json["reordered"]!.GetValue<bool>());

        Drift(host);
        await host.Store.RescanAsync(false, EditorHost.Ct);
        var dry = await host.SendJsonAsync("POST", $"/api/processes/{Sales}/sync-enum", new { dryRun = true });
        Assert.True(dry.Json["reordered"]!.GetValue<bool>());
        Assert.False(dry.Json["applied"]!.GetValue<bool>());
        var stale = await host.SendJsonAsync("POST", $"/api/processes/{Sales}/sync-enum", new { expectedHash = new string('0', 64) });
        Assert.Equal(409, stale.Status);
        Contract.AssertResponse(stale, "/api/processes/{id}/sync-enum");

        var applied = await host.SendJsonAsync("POST", $"/api/processes/{Sales}/sync-enum", new { });
        Assert.Equal(200, applied.Status);
        Contract.AssertResponse(applied, "/api/processes/{id}/sync-enum");
        Assert.True(applied.Json["applied"]!.GetValue<bool>());
        Assert.False((await host.SendJsonAsync("POST", $"/api/processes/{Sales}/sync-enum", new { dryRun = true })).Json["reordered"]!.GetValue<bool>());

        var orchestration = await host.SendJsonAsync("POST", $"/api/processes/{Purchase}/sync-enum", new { dryRun = true });
        Assert.Equal(422, orchestration.Status);
        Contract.AssertResponse(orchestration, "/api/processes/{id}/sync-enum");
        Assert.Equal("MQ9019", orchestration.Json["diagnostics"]![0]!["rule"]!.GetValue<string>());
    }

    /// <summary>Swaps the last two members of the bound enum, so the lifecycle's states and the enum's members disagree on order.</summary>
    /// <param name="host">The host.</param>
    internal static void Drift(EditorHost host)
    {
        var path = host.PathOf(".maquettiste/model/enums/sales-order-status.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var members = document["members"]!.AsArray();
        var last = members[^1]!.DeepClone();
        members.RemoveAt(members.Count - 1);
        members.Insert(members.Count - 1, last);
        File.WriteAllText(path, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
