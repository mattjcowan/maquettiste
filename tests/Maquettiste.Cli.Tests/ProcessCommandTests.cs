using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// The process operations over MCP and the command line (phase-3-design.md section 4.4), on the gate 3 fixture: each has a tool and a
/// verb with the API's semantics (parity with the Functions tests of the same operations).
/// </summary>
public sealed class ProcessCommandTests
{
    private const string Purchase = "01JQPRC0000000000000000002";
    private const string PurchaseFile = ".maquettiste/model/processes/purchase-approval.json";
    private const string EnumFile = ".maquettiste/model/enums/sales-order-status.json";

    private const string Inputs = """
        { "start": { "context": { "01JQATT0000000000000000201": 2500 } },
          "steps": [ { "event": "01JQPRX0000000000000000014", "actor": "01JQACT0000000000000000008" },
                     { "input": "invoke-error", "invoke": "01JQPRX0000000000000000019" } ] }
        """;

    [Fact]
    public async Task Mcp_simulate_verify_and_record_match_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Processes();
        await using var session = await McpSession.StartAsync(repo, ct);

        var simulated = await session.OkAsync("simulate_process", new { process = "PurchaseApproval", scenario = "01JQSCN0000000000000000010" });
        Assert.Equal("[\"01JQSTA0000000000000000106\",\"01JQSTA0000000000000000109\"]", simulated["configuration"]!.ToJsonString());
        var again = await session.CallAsync("simulate_process", new { process = Purchase, scenario = "01JQSCN0000000000000000010" });
        Assert.Equal(simulated.ToJsonString(), again.Json.ToJsonString());

        var verified = 0;
        foreach (var process in new[] { "SalesOrderLifecycle", "PurchaseApproval" })
        {
            var verify = await session.OkAsync("verify_scenarios", new { process });
            Assert.True(verify["passed"]!.GetValue<bool>(), verify.ToJsonString());
            verified += verify["results"]!.AsArray().Count;
        }

        Assert.Equal(14, verified);
        var inputs = JsonNode.Parse(Inputs)!;
        var preview = await session.OkAsync("record_scenario", new { process = Purchase, name = "Recorded", steps = inputs["steps"], start = inputs["start"], dryRun = true });
        Assert.False(preview["applied"]!.GetValue<bool>());
        var recorded = await session.OkAsync("record_scenario", new { process = Purchase, name = "Recorded", steps = inputs["steps"], start = inputs["start"] });
        Assert.True(recorded["applied"]!.GetValue<bool>());
        Assert.True(File.Exists(repo.PathOf(".maquettiste/model/scenarios/purchase-approval/recorded.json")));

        Assert.Equal("not-found", (await session.ErrorAsync("simulate_process", new { process = "Nope" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("verify_scenarios", new { })).Code);
    }

    [Fact]
    public async Task Simulation_inputs_accept_names_and_refuse_an_unparsable_start_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Processes();
        await using var session = await McpSession.StartAsync(repo, ct);
        var start = JsonNode.Parse(Inputs)!["start"];

        var byId = await session.OkAsync("simulate_process", new { process = Purchase, start, steps = new[] { new { @event = "01JQPRX0000000000000000014", actor = "01JQACT0000000000000000008" } } });
        var byName = await session.OkAsync("simulate_process", new { process = Purchase, start, steps = new[] { new { @event = "submit", actor = "BudgetHolder" } } });

        Assert.True(byName["trace"]!.AsArray()[1]!["accepted"]!.GetValue<bool>(), byName.ToJsonString());
        Assert.Equal(byId["configuration"]!.ToJsonString(), byName["configuration"]!.ToJsonString());
        var bad = await session.ErrorAsync("simulate_process", new { process = Purchase, start = new { at = "not-a-date" } });
        Assert.Equal("bad-request", bad.Code);
    }

    [Fact]
    public async Task Mcp_export_then_import_into_round_trips_and_sync_enum_is_a_dry_run_unless_applied()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Processes();
        await using var session = await McpSession.StartAsync(repo, ct);
        var before = repo.Read(PurchaseFile);

        var export = await session.CallAsync("export_process", new { process = "PurchaseApproval" });
        Assert.False(export.IsError, export.ToString());
        var notes = JsonNode.Parse(export.Blocks[1])!["diagnostics"]!.AsArray();     // the export's diagnostics travel in a second block
        Assert.Contains(notes, d => (string)d!["rule"]! == "MQ9406");
        export = export with { Text = export.Blocks[0] };
        var dry = await session.OkAsync("import_process", new { config = export.Text, into = "PurchaseApproval" });
        Assert.False(dry["applied"]!.GetValue<bool>());
        Assert.Empty(dry["created"]!.AsArray());
        var applied = await session.OkAsync("import_process", new { config = export.Text, into = Purchase, apply = true });
        Assert.True(applied["applied"]!.GetValue<bool>());
        Assert.Equal(before, repo.Read(PurchaseFile));
        Assert.Equal("conflict", (await session.ErrorAsync("import_process", new { config = export.Text, into = Purchase, expectedHash = new string('0', 64), apply = true })).Code);

        Drift(repo);
        var plan = await session.OkAsync("sync_enum_from_process", new { process = "SalesOrderLifecycle" });
        Assert.True(plan["reordered"]!.GetValue<bool>());
        Assert.False(plan["applied"]!.GetValue<bool>());
        var drifted = repo.Read(EnumFile);
        var synced = await session.OkAsync("sync_enum_from_process", new { process = "SalesOrderLifecycle", apply = true });
        Assert.True(synced["applied"]!.GetValue<bool>());
        Assert.NotEqual(drifted, repo.Read(EnumFile));
        Assert.Equal("invalid", (await session.ErrorAsync("sync_enum_from_process", new { process = "PurchaseApproval" })).Code);
    }

    [Fact]
    public async Task Cli_verify_simulate_and_record()
    {
        using var repo = CliRepo.Processes();
        var verify = await repo.RunAsync("process", "verify");
        Assert.Equal(0, verify.ExitCode);
        Assert.Contains("14 scenarios, 14 passed, 0 failed", verify.Out, StringComparison.Ordinal);
        var json = await repo.RunAsync("process", "verify", "PurchaseApproval", "--format", "json");
        Assert.Equal(6, JsonNode.Parse(json.Out)!["scenarios"]!.GetValue<int>());

        repo.Write("inputs.json", Inputs);
        var simulate = await repo.RunAsync("process", "simulate", "PurchaseApproval", "--inputs", repo.PathOf("inputs.json"));
        Assert.Equal(0, simulate.ExitCode);
        Assert.Contains("[1] invoke-error checkBudget -> Review.Budget.BudgetRejected, Review.Compliance.Pending", simulate.Out, StringComparison.Ordinal);
        var simulateJson = await repo.RunAsync("process", "simulate", Purchase, "--inputs", repo.PathOf("inputs.json"), "--format", "json", "--from", "1");
        Assert.Equal([1], JsonNode.Parse(simulateJson.Out)!["trace"]!.AsArray().Select(t => t!["index"]!.GetValue<int>()));

        var preview = await repo.RunAsync("process", "record", "PurchaseApproval", "Recorded", "--inputs", repo.PathOf("inputs.json"));
        Assert.Equal(0, preview.ExitCode);
        Assert.Contains("--apply", preview.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.PathOf(".maquettiste/model/scenarios/purchase-approval/recorded.json")));
        var record = await repo.RunAsync("process", "record", "PurchaseApproval", "Recorded", "--inputs", repo.PathOf("inputs.json"), "--apply");
        Assert.Equal(0, record.ExitCode);
        Assert.True(File.Exists(repo.PathOf(".maquettiste/model/scenarios/purchase-approval/recorded.json")));
        Assert.Contains("15 scenarios, 15 passed", (await repo.RunAsync("process", "verify")).Out, StringComparison.Ordinal);

        repo.Replace(".maquettiste/model/scenarios/purchase-approval/budget-rejected.json", "\"01JQSTA0000000000000000109\"", "\"01JQSTA0000000000000000108\"");
        var failing = await repo.RunAsync("process", "verify", "PurchaseApproval");
        Assert.Equal(1, failing.ExitCode);
        Assert.Contains("FAIL  PurchaseApproval/BudgetRejected (3 steps): MQ9302 at step 2", failing.Out, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("process", "simulate", "Nope")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("process", "fly", "PurchaseApproval")).ExitCode);
    }

    [Fact]
    public async Task Cli_export_import_round_trip_and_sync_enum_check_and_apply()
    {
        using var repo = CliRepo.Processes();
        var before = repo.Read(PurchaseFile);
        var export = await repo.RunAsync("process", "export", "PurchaseApproval", "--out", repo.PathOf("purchase.xstate.json"));
        Assert.Equal(0, export.ExitCode);
        var dry = await repo.RunAsync("process", "import", repo.PathOf("purchase.xstate.json"), "--into", "PurchaseApproval");
        Assert.Equal(0, dry.ExitCode);
        Assert.Contains("0 created, 0 removed; run with --apply", dry.Error, StringComparison.Ordinal);
        var applied = await repo.RunAsync("process", "import", repo.PathOf("purchase.xstate.json"), "--into", "PurchaseApproval", "--apply");
        Assert.Equal(0, applied.ExitCode);
        Assert.Equal(before, repo.Read(PurchaseFile));

        repo.Write("door.json", """{ "id": "Door", "initial": "Closed", "states": { "Closed": { "on": { "open": "Opened" } }, "Opened": { "on": { "close": "Closed" } } } }""");
        Assert.Equal(4, (await repo.RunAsync("process", "import", repo.PathOf("door.json"))).ExitCode);
        var created = await repo.RunAsync("process", "import", repo.PathOf("door.json"), "--domain", "purchasing", "--apply", "--format", "json");
        Assert.Equal(0, created.ExitCode);
        Assert.True(JsonNode.Parse(created.Out)!["applied"]!.GetValue<bool>());
        Assert.True(File.Exists(repo.PathOf(".maquettiste/model/processes/door.json")));

        Assert.Equal(0, (await repo.RunAsync("process", "sync-enum", "SalesOrderLifecycle", "--check")).ExitCode);
        Drift(repo);
        var check = await repo.RunAsync("process", "sync-enum", "SalesOrderLifecycle", "--check");
        Assert.Equal(2, check.ExitCode);
        Assert.Contains("reordered", check.Out, StringComparison.Ordinal);
        Assert.Equal(0, (await repo.RunAsync("process", "sync-enum", "SalesOrderLifecycle", "--apply")).ExitCode);
        Assert.Equal(0, (await repo.RunAsync("process", "sync-enum", "SalesOrderLifecycle", "--check")).ExitCode);
        Assert.Equal(1, (await repo.RunAsync("process", "sync-enum", "PurchaseApproval")).ExitCode);
    }

    /// <summary>Swaps the last two members of the bound enum.</summary>
    private static void Drift(CliRepo repo)
    {
        var document = JsonNode.Parse(repo.Read(EnumFile))!.AsObject();
        var members = document["members"]!.AsArray();
        var last = members[^1]!.DeepClone();
        members.RemoveAt(members.Count - 1);
        members.Insert(members.Count - 1, last);
        repo.Write(EnumFile, document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
