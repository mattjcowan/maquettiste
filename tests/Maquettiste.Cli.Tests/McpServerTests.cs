using System.Text.Json.Nodes;
using Maquettiste.Bench;
using Maquettiste.Testing;
using ModelContextProtocol.Protocol;

namespace Maquettiste.Cli.Tests;

/// <summary>The protocol surface: tools, the conventions resource and prompt, the server's identity.</summary>
public sealed class McpSurfaceTests
{
    private static readonly string[] Tools =
    [
        "apply_batch", "apply_plan", "create_element", "create_seed", "delete_element", "delete_extension_file", "delete_pack", "delete_pack_file", "explain_unit",
        "export_process", "export_seed_csv", "get_database_view", "get_element", "get_elements", "get_model_index", "get_model_kinds", "get_pack",
        "get_pack_outputs", "get_plan", "get_plan_diff", "get_project", "get_references", "get_resolved_model", "get_schema", "get_settings", "get_template_context",
        "get_translations", "import_process", "import_seed_csv", "list_extension_files", "list_pack_files", "list_packs", "list_validation_rules",
        "localization_status", "move_extension_file", "move_pack_file", "new_pack", "plan", "preview_query_sql", "preview_unit", "read_extension_file", "read_pack_file", "record_scenario",
        "reference_type_usage", "rename_pack", "save_element", "save_pack", "save_pack_settings", "save_settings", "set_translations", "simulate_process",
        "sync_enum_from_process", "unit_paths", "validate", "verify_scenarios", "write_extension_file", "write_pack_file",
    ];

    [Fact]
    public async Task Lists_every_tool_and_serves_the_conventions_as_a_resource_and_a_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);
        Assert.Equal("maquettiste", session.Client.ServerInfo.Name);
        Assert.Equal(Engine.EngineVersion.Product, session.Client.ServerInfo.Version); // the release, not the engine contract
        Assert.Contains("expectedHash", session.Client.ServerInstructions, StringComparison.Ordinal);

        var tools = await session.Client.ListToolsAsync(cancellationToken: ct);
        Assert.Equal(Tools, tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Name));
        var save = tools.Single(t => t.Name == "save_element").ProtocolTool;
        Assert.Equal(["element", "expectedHash", "id"], save.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));
        Assert.True(tools.Single(t => t.Name == "get_element").ProtocolTool.Annotations!.ReadOnlyHint);

        var resources = await session.Client.ListResourcesAsync(cancellationToken: ct);
        var conventions = Assert.Single(resources);
        Assert.Equal("maquettiste://conventions", conventions.Uri);
        var read = await session.Client.ReadResourceAsync(new Uri("maquettiste://conventions"), cancellationToken: ct);
        var text = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text;
        Assert.StartsWith("# Modeling with Maquettiste", text, StringComparison.Ordinal);
        Assert.DoesNotContain("name: maquettiste-modeling", text, StringComparison.Ordinal);

        var prompts = await session.Client.ListPromptsAsync(cancellationToken: ct);
        Assert.Equal("modeling-conventions", Assert.Single(prompts).Name);
        var prompt = await session.Client.GetPromptAsync("modeling-conventions", cancellationToken: ct);
        Assert.Equal(text, Assert.IsType<TextContentBlock>(Assert.Single(prompt.Messages).Content).Text);
    }

    [Fact]
    public void Docs_list_every_tool_and_the_current_count()
    {
        var root = Maquettiste.Testing.Fixtures.RepoRoot;
        var docs = File.ReadAllText(Path.Combine(root, "docs", "mcp.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = docs.IndexOf("\n## Tools\n", StringComparison.Ordinal);
        var table = docs[start..docs.IndexOf("\n### Semantics\n", start, StringComparison.Ordinal)];
        var rows = System.Text.RegularExpressions.Regex.Matches(table, @"^\| `([a-z_]+)` \|", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value);
        Assert.Equal(Tools, rows.Order(StringComparer.Ordinal));
        var count = Tools.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("the current source " + count + ")", docs, StringComparison.Ordinal);
        Assert.Contains("connected with " + count + " tools", docs, StringComparison.Ordinal);
        Assert.Contains(count + " today", File.ReadAllText(Path.Combine(root, "README.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_refreshes_stale_schema_copies_before_it_serves()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/.schema/v1/entity.json", "{}\n");
        repo.Write(".maquettiste/.schema/v1/retired.json", "{}\n");
        await using var session = await McpSession.StartAsync(repo, ct);
        foreach (var name in Maquettiste.Testing.TestServices.Schemas.FileNames)
            Assert.Equal(Maquettiste.Testing.TestServices.Schemas.GetFileBytes(name).ToArray(), File.ReadAllBytes(repo.PathOf(".maquettiste/.schema/v1/" + name)));
        Assert.False(File.Exists(repo.PathOf(".maquettiste/.schema/v1/retired.json")));
        var validate = await session.OkAsync("validate");
        Assert.DoesNotContain("MQ1008", validate.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_arguments_and_missing_ids_are_tool_errors_with_problem_codes_not_crashes()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal("not-found", (await session.ErrorAsync("get_element", new { id = "01J92P0V0000000000000000ZZ" })).Code);
        Assert.Equal("not-found", (await session.ErrorAsync("get_references", new { id = "nope" })).Code);
        Assert.Equal("not-a-database", (await session.ErrorAsync("get_database_view", new { id = McpSession.Customer })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("delete_element", new { id = McpSession.Customer, expectedHash = "x", resolution = "cascade" })).Code);
        Assert.Equal("precondition-required", (await session.ErrorAsync("save_element", new { id = McpSession.Customer, element = new { kind = "entity" }, expectedHash = "" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("create_element", new { element = 42 })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("plan", new { handEdits = "sometimes" })).Code);
        Assert.Equal("not-found", (await session.ErrorAsync("get_plan", new { planId = "01J92P0V0000000000000000ZZ" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("apply_plan", new { planId = "not-a-plan" })).Code);
        // Missing or wrongly typed arguments get the documented problem naming the argument, not the SDK's generic error.
        var missing = await session.ErrorAsync("save_element", new { id = McpSession.Customer, expectedHash = "x" });
        Assert.Equal("bad-request", missing.Code);
        Assert.StartsWith("element is required", (string)missing.Json["detail"]!, StringComparison.Ordinal);
        Assert.Equal("precondition-required", (await session.ErrorAsync("save_element", new { id = McpSession.Customer })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_element", new { })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("apply_batch", new { })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_schema", new { })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_plan_diff", new { planId = "01J92P0V0000000000000000ZZ" })).Code);
        var wrongType = await session.ErrorAsync("get_model_index", new { kind = 5 });
        Assert.Equal("bad-request", wrongType.Code);
        Assert.Equal("kind must be a string, not a number.", (string)wrongType.Json["detail"]!);
        Assert.Equal("elementIds[0] must be a string, not a number.", (string)(await session.ErrorAsync("validate", new { elementIds = new[] { 1 } })).Json["detail"]!);
        Assert.Equal("bad-request", (await session.ErrorAsync("plan", new { jobs = "many" })).Code);

        var schema = await session.ErrorAsync("get_schema", new { kind = "widget" });
        Assert.Equal("not-found", schema.Code);
        Assert.Contains("value-object", (string)schema.Json["detail"]!, StringComparison.Ordinal);

        // The server is still up.
        Assert.Equal("billing", (string)(await session.OkAsync("get_project"))["name"]!);
    }
}

/// <summary>The command itself, in-process.</summary>
public sealed class McpCommandTests
{
    [Fact]
    public async Task Exits_1_without_a_model_4_on_misuse_and_0_when_the_client_closes_stdin()
    {
        var ct = TestContext.Current.CancellationToken;
        using var empty = CliRepo.Empty();
        using var billing = CliRepo.Billing();
        var error = new SharedWriter();
        var stdout = new MemoryStream();
        var environment = new CliEnvironment
        {
            Out = TextWriter.Null,
            Error = error,
            CurrentDirectory = empty.RepoRoot,
            OpenStandardInput = () => new MemoryStream(),
            OpenStandardOutput = () => stdout,
        };

        Assert.Equal(1, await new CliApp(environment).RunAsync(["--cache-dir", empty.CacheDirectory, "mcp"], ct));
        Assert.Contains("no model found", error.Text(), StringComparison.Ordinal);
        Assert.Equal(4, await new CliApp(environment).RunAsync(["--repo", billing.RepoRoot, "mcp", "--force"], ct));
        Assert.Equal(4, await new CliApp(environment with { OpenStandardInput = null }).RunAsync(["--repo", billing.RepoRoot, "mcp"], ct));
        Assert.Contains("standard input and output", error.Text(), StringComparison.Ordinal);

        // Stdin at its end: the server starts, sees the client gone and stops; nothing but protocol output reaches stdout.
        Assert.Equal(0, await new CliApp(environment).RunAsync(["--repo", billing.RepoRoot, "--cache-dir", billing.CacheDirectory, "mcp"], ct));
        Assert.Contains("over MCP (stdio)", error.Text(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToArray());
    }
}

/// <summary>The end of stdin right after the requests (<c>echo request | maquettiste mcp</c>).</summary>
public sealed class McpEndOfInputTests
{
    [Fact]
    public async Task Answers_every_request_read_before_stdin_ended_then_exits_0()
    {
        var ct = TestContext.Current.CancellationToken;
        using var billing = CliRepo.Billing();
        var requests = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"echo","version":"1"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":"two","method":"tools/call","params":{"name":"get_project","arguments":{}}}
            """;
        var stdout = new MemoryStream();
        var environment = new CliEnvironment
        {
            Out = TextWriter.Null,
            Error = new SharedWriter(),
            CurrentDirectory = billing.RepoRoot,
            OpenStandardInput = () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(requests)),
            OpenStandardOutput = () => stdout,
        };

        // The last request has no newline (echo -n): the end of input completes it.
        Assert.Equal(0, await new CliApp(environment).RunAsync(["--repo", billing.RepoRoot, "--cache-dir", billing.CacheDirectory, "mcp"], ct));
        var answers = System.Text.Encoding.UTF8.GetString(stdout.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.Nodes.JsonNode.Parse(line)!).ToList();
        Assert.Equal(2, answers.Count);
        var initialize = Assert.Single(answers, a => a["id"]?.ToJsonString() == "1");
        Assert.NotNull(initialize["result"]?["serverInfo"]);
        var project = Assert.Single(answers, a => a["id"]?.ToJsonString() == "\"two\"");
        Assert.Contains("billing", (string)project["result"]!["content"]![0]!["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_request_does_not_hold_the_end_of_input()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = """
            {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}
            {"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}
            """;
        var stdio = new Maquettiste.Cli.Mcp.DrainingStdio(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input)), new MemoryStream());
        var buffer = new byte[4096];
        while (await stdio.Input.ReadAsync(buffer, ct) > 0)
        {
        }

        Assert.Equal(0, stdio.PendingCount);

        // A response written in chunks answers its request; the end of input then comes at once.
        var pending = new Maquettiste.Cli.Mcp.DrainingStdio(new MemoryStream("{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"method\":\"ping\"}\n"u8.ToArray()), new MemoryStream());
        Assert.True(await pending.Input.ReadAsync(buffer, ct) > 0);
        Assert.Equal(1, pending.PendingCount);
        var end = pending.Input.ReadAsync(buffer, ct).AsTask();
        await Task.Delay(50, ct);
        Assert.False(end.IsCompleted);
        await pending.Output.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":\"x\","u8.ToArray(), ct);
        await pending.Output.WriteAsync("\"result\":{}}\n"u8.ToArray(), ct);
        Assert.Equal(0, await end.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }
}

/// <summary>The read tools return the editor API's bodies.</summary>
public sealed class McpReadTests
{
    [Fact]
    public async Task Project_names_the_release_and_the_workspace_from_the_variable_else_from_git()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        await using (var plain = await McpSession.StartAsync(repo, ct, environment: new Dictionary<string, string?> { [Engine.WorkspaceInfo.Variable] = null }))
        {
            var project = await plain.OkAsync("get_project");
            Assert.Equal(Engine.EngineVersion.Product, (string)project["productVersion"]!);
            Assert.Equal(Engine.EngineVersion.Build, (string)project["build"]!);
            Assert.Null(project["workspace"]); // no variable, no .git
            Assert.Null(project["branch"]);
            Assert.Null(project["worktree"]);
        }

        // A linked worktree whose git folder is not in reach (a container): its name; the variable wins over it, trimmed.
        File.WriteAllText(Path.Combine(repo.RepoRoot, ".git"), "gitdir: /nowhere/maquettiste/.git/worktrees/modeling-and-codegen\n");
        await using (var linked = await McpSession.StartAsync(repo, ct, environment: new Dictionary<string, string?> { [Engine.WorkspaceInfo.Variable] = " " }))
        {
            var project = await linked.OkAsync("get_project");
            Assert.Equal("modeling-and-codegen", (string)project["workspace"]!);
            Assert.Equal("modeling-and-codegen", (string)project["worktree"]!);
            Assert.Equal("maquettiste", (string)project["repository"]!);
            Assert.Null(project["branch"]);
        }

        await using (var named = await McpSession.StartAsync(repo, ct, environment: new Dictionary<string, string?> { [Engine.WorkspaceInfo.Variable] = "  billing-demo " }))
            Assert.Equal("billing-demo", (string)(await named.OkAsync("get_project"))["workspace"]!);
    }

    [Fact]
    public async Task Project_index_element_references_database_packs_settings_and_schema()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);

        var project = await session.OkAsync("get_project");
        Assert.Equal("billing", (string)project["name"]!);
        Assert.Equal(1, (int)project["formatVersion"]!);
        Assert.Equal(Engine.EngineVersion.Value, (string)project["engineVersion"]!);
        Assert.Equal(Engine.EngineVersion.Product, (string)project["productVersion"]!);
        Assert.Equal(Engine.EngineVersion.Build, (string)project["build"]!);
        Assert.Equal("local", (string)project["mode"]!);
        Assert.False(string.IsNullOrEmpty((string?)project["settingsHash"]));
        var database = Assert.Single(project["databases"]!.AsArray())!;
        Assert.Equal(["classes", "ddl"], project["packs"]!.AsArray().Select(p => (string)p!["name"]!).Order(StringComparer.Ordinal));

        var index = (await session.OkAsync("get_model_index")).AsArray();
        Assert.True(index.Count > 20, index.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(5, (await session.OkAsync("get_model_index", new { kind = "entity" })).AsArray().Count);
        // The name filter matches Customer and the two queries whose names say customer.
        Assert.Equal(new[] { McpSession.Customer, "01K6QRY0000000000000000003", "01K6QRY0000000000000000001" }.Order(StringComparer.Ordinal),
            Ids(await session.OkAsync("get_model_index", new { query = "CUSTOM" })).Order(StringComparer.Ordinal));
        Assert.Contains(McpSession.Customer, Ids(await session.OkAsync("get_model_index", new { package = "Billing", kind = "entity" })));
        Assert.Equal(
            Ids(await session.OkAsync("get_model_index", new { package = "Billing" })),
            Ids(await session.OkAsync("get_model_index", new { package = McpSession.BillingPackage })));
        Assert.Contains(McpSession.Customer, Ids(await session.OkAsync("get_model_index", new { tag = "pii" })));
        Assert.Contains(McpSession.Customer, Ids(await session.OkAsync("get_model_index", new { stereotype = "aggregate-root" })));
        Assert.Contains(McpSession.Customer, Ids(await session.OkAsync("get_model_index", new { category = "01J92P0V06TYE8P8990AV35A8K" })));
        Assert.Empty(Ids(await session.OkAsync("get_model_index", new { kind = "enum", tag = "pii" })));

        var element = await session.OkAsync("get_element", new { id = McpSession.Customer });
        Assert.Equal(McpSession.CustomerPath, (string)element["path"]!);
        Assert.Equal("Customer", (string)element["json"]!["name"]!);
        var summary = index.Single(s => (string)s!["id"]! == McpSession.Customer)!;
        Assert.Equal((string)summary["hash"]!, (string)element["hash"]!);

        var references = (await session.OkAsync("get_references", new { id = McpSession.Customer })).AsArray();
        Assert.NotEmpty(references);
        Assert.All(references, r => Assert.Equal(McpSession.Customer, (string)r!["toId"]!));

        var view = await session.OkAsync("get_database_view", new { id = (string)database["id"]! });
        Assert.NotNull(view["view"]);
        Assert.Contains("customers", view["view"]!.ToJsonString(), StringComparison.Ordinal);

        var packs = await session.OkAsync("list_packs");
        Assert.Equal(2, packs["packs"]!.AsArray().Count);

        var settings = await session.OkAsync("get_settings");
        Assert.Equal((string)project["settingsHash"]!, (string)settings["hash"]!);
        Assert.Equal("billing", (string)settings["json"]!["name"]!);

        var rules = (await session.OkAsync("list_validation_rules")).AsArray();
        Assert.Equal("MQ1001", (string)rules[0]!["id"]!);
        Assert.False((bool)rules[0]!["canBeOff"]!);
        Assert.Equal("MQ72xx", (string)rules.Single(r => (string)r!["id"]! == "MQ7204")!["family"]!);

        var schema = await session.OkAsync("get_schema", new { kind = "entity" });
        Assert.Equal("entity.json", (string)schema["file"]!);
        Assert.NotNull(schema["schema"]!["properties"]!["attributes"]);
        Assert.NotNull(schema["references"]!["common.json"]);
        var retention = schema["extensions"]!.AsArray().Single(e => (string)e!["name"]! == "retention")!;
        Assert.Equal(1, (int)retention["properties"]!["retentionDays"]!["minimum"]!);
        Assert.Equal("audited", (string)retention["appliesTo"]!["stereotypes"]![0]!);
        Assert.Empty((await session.OkAsync("get_schema", new { kind = "enum" }))["extensions"]!.AsArray());
        Assert.Equal("maquettiste.json", (string)(await session.OkAsync("get_schema", new { kind = "maquettiste" }))["file"]!);
        Assert.Null((await session.OkAsync("get_schema", new { kind = "maquettiste" }))["extensions"]);
    }

    private static List<string> Ids(JsonNode index) => [.. index.AsArray().Select(s => (string)s!["id"]!)];
}

/// <summary>Saves, conflicts, creates, deletes and batches.</summary>
public sealed class McpWriteTests
{
    [Fact]
    public async Task A_save_needs_the_hash_and_a_conflict_returns_both_versions_and_never_overwrites()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var loaded = await session.OkAsync("get_element", new { id = McpSession.Customer });
        var staleHash = (string)loaded["hash"]!;

        var mine = loaded["json"]!.DeepClone();
        mine["description"] = "Someone we bill, by agent.";
        var saved = await session.OkAsync("save_element", new { id = McpSession.Customer, element = mine, expectedHash = staleHash });
        Assert.Equal("saved", (string)saved["outcome"]!);
        var newHash = (string)saved["hash"]!;
        Assert.NotEqual(staleHash, newHash);
        Assert.Contains("\"description\": \"Someone we bill, by agent.\"", session.Repo.Read(McpSession.CustomerPath), StringComparison.Ordinal);
        var onDisk = session.Repo.Read(McpSession.CustomerPath);

        var theirs = loaded["json"]!.DeepClone();
        theirs["description"] = "A competing edit.";
        var conflict = await session.ErrorAsync("save_element", new { id = McpSession.Customer, element = theirs, expectedHash = staleHash });
        Assert.Equal("conflict", conflict.Code);
        Assert.Equal(409, (int)conflict.Json["status"]!);
        Assert.Equal(newHash, (string)conflict.Json["hash"]!);
        Assert.Equal("Someone we bill, by agent.", (string)conflict.Json["current"]!["json"]!["description"]!);
        Assert.Equal("A competing edit.", (string)conflict.Json["submitted"]!["description"]!);
        Assert.Equal(onDisk, session.Repo.Read(McpSession.CustomerPath));

        // A save the file was changed behind: the server sees the disk version.
        session.Repo.Replace(McpSession.CustomerPath, "Someone we bill, by agent.", "Edited in a text editor.");
        var behind = await session.ErrorAsync("save_element", new { id = McpSession.Customer, element = mine, expectedHash = newHash });
        Assert.Equal("conflict", behind.Code);
        Assert.Equal("Edited in a text editor.", (string)behind.Json["current"]!["json"]!["description"]!);

        // An invalid document is refused with diagnostics and nothing is written.
        var reloaded = await session.OkAsync("get_element", new { id = McpSession.Customer });
        var broken = reloaded["json"]!.DeepClone();
        broken["package"] = "01J92P0V0000000000000000ZZ";
        var invalid = await session.ErrorAsync("save_element", new { id = McpSession.Customer, element = broken, expectedHash = (string)reloaded["hash"]! });
        Assert.Equal("invalid", invalid.Code);
        Assert.NotEmpty(invalid.Json["diagnostics"]!.AsArray());
        Assert.Contains("Edited in a text editor.", session.Repo.Read(McpSession.CustomerPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_then_delete_and_a_referenced_delete_is_refused_unless_resolved()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var created = await session.OkAsync("create_element", new
        {
            element = new { kind = "entity", name = "Coupon", package = McpSession.BillingPackage, description = "A discount code.", @abstract = true },
        });
        Assert.Equal("saved", (string)created["outcome"]!);
        var id = (string)created["id"]!;
        var coupon = await session.OkAsync("get_element", new { id });
        Assert.Equal("Coupon", (string)coupon["json"]!["name"]!);
        Assert.True(File.Exists(session.Repo.PathOf((string)coupon["path"]!)));

        var customer = await session.OkAsync("get_element", new { id = McpSession.Customer });
        var refused = await session.ErrorAsync("delete_element", new { id = McpSession.Customer, expectedHash = (string)customer["hash"]! });
        Assert.Equal("referenced", refused.Code);
        Assert.NotEmpty(refused.Json["referrers"]!.AsArray());
        Assert.True(File.Exists(session.Repo.PathOf(McpSession.CustomerPath)));

        // The relations' ends are required references, so clearing them makes the delete invalid; still nothing is written.
        var resolved = await session.CallAsync("delete_element", new { id = McpSession.Customer, expectedHash = (string)customer["hash"]!, resolution = "remove-references" });
        Assert.True(resolved.IsError, resolved.ToString());
        Assert.Contains(resolved.Code, new[] { "invalid", "referenced" });
        Assert.True(File.Exists(session.Repo.PathOf(McpSession.CustomerPath)));

        var stale = await session.ErrorAsync("delete_element", new { id, expectedHash = (string)customer["hash"]! });
        Assert.Equal("conflict", stale.Code);
        var deleted = await session.OkAsync("delete_element", new { id, expectedHash = (string)coupon["hash"]! });
        Assert.Equal("saved", (string)deleted["outcome"]!);
        Assert.False(File.Exists(session.Repo.PathOf((string)coupon["path"]!)));
        Assert.Equal("not-found", (await session.ErrorAsync("get_element", new { id })).Code);
    }

    [Fact]
    public async Task Delete_element_dry_run_returns_the_plan_and_delete_dependents_takes_the_dependents()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var customer = await session.OkAsync("get_element", new { id = McpSession.Customer });

        var plan = await session.OkAsync("delete_element", new { id = McpSession.Customer, dryRun = true });
        var clearing = await session.OkAsync("delete_element", new { id = McpSession.Customer, dryRun = true, resolution = "remove-references" });
        var bad = await session.ErrorAsync("delete_element", new { id = McpSession.Customer, dryRun = true, resolution = "cascade" });
        Assert.Equal("delete-dependents", (string)plan["resolution"]!);
        Assert.Equal("saved", (string)plan["outcome"]!);
        var deletes = plan["deletes"]!.AsArray();
        Assert.Contains(deletes, d => (string)d!["kind"]! == "relation" && ((string)d["because"]!).Contains("Customer", StringComparison.Ordinal));
        Assert.Equal("invalid", (string)clearing["outcome"]!);
        Assert.Equal("bad-request", bad.Code);
        Assert.True(File.Exists(session.Repo.PathOf(McpSession.CustomerPath)));

        var deleted = await session.OkAsync("delete_element", new { id = McpSession.Customer, expectedHash = (string)customer["hash"]!, resolution = "delete-dependents" });
        Assert.Equal("saved", (string)deleted["outcome"]!);
        Assert.Equal(deletes.Count + 1, deleted["changes"]!["deleted"]!.AsArray().Count);
        Assert.False(File.Exists(session.Repo.PathOf(McpSession.CustomerPath)));
    }

    [Fact]
    public async Task A_batch_is_all_or_nothing()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var customer = await session.OkAsync("get_element", new { id = McpSession.Customer });
        var edited = customer["json"]!.DeepClone();
        edited["description"] = "Batched.";

        var failed = await session.ErrorAsync("apply_batch", new
        {
            operations = new object[]
            {
                new { op = "create", element = new { kind = "entity", name = "Voucher", package = McpSession.BillingPackage, @abstract = true } },
                new { op = "update", id = McpSession.Customer, expectedHash = new string('0', 64), element = edited },
            },
        });
        Assert.True(failed.Code == "conflict", failed.ToString());
        Assert.Equal(2, failed.Json["items"]!.AsArray().Count);
        Assert.Empty((await session.OkAsync("get_model_index", new { query = "Voucher" })).AsArray());

        var ok = await session.OkAsync("apply_batch", new
        {
            operations = new object[]
            {
                new { op = "create", element = new { kind = "entity", name = "Voucher", package = McpSession.BillingPackage, @abstract = true } },
                new { op = "update", id = McpSession.Customer, expectedHash = (string)customer["hash"]!, element = edited },
            },
        });
        Assert.Equal("saved", (string)ok["outcome"]!);
        Assert.Single((await session.OkAsync("get_model_index", new { query = "Voucher" })).AsArray());
        Assert.Contains("Batched.", session.Repo.Read(McpSession.CustomerPath), StringComparison.Ordinal);

        var malformed = await session.ErrorAsync("apply_batch", new { operations = new object[] { new { op = "rename" } } });
        Assert.Equal("invalid", malformed.Code);
        Assert.NotEmpty(malformed.Json["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task Settings_save_with_the_hash_and_conflict_without_it()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var settings = await session.OkAsync("get_settings");
        var json = settings["json"]!.DeepClone();
        json["name"] = "billing-mcp";
        var saved = await session.OkAsync("save_settings", new { settings = json, expectedHash = (string)settings["hash"]! });
        Assert.Equal("saved", (string)saved["outcome"]!);
        Assert.Contains("\"name\": \"billing-mcp\"", session.Repo.Read(".maquettiste/maquettiste.json"), StringComparison.Ordinal);

        var conflict = await session.ErrorAsync("save_settings", new { settings = json, expectedHash = (string)settings["hash"]! });
        Assert.Equal("conflict", conflict.Code);
        Assert.Equal((string)saved["hash"]!, (string)conflict.Json["hash"]!);
    }
}

/// <summary>Validation, plan then apply, and cancellation.</summary>
public sealed class McpGenerationTests
{
    [Fact]
    public async Task Validate_reports_rule_ids_and_positions_for_a_model_broken_behind_the_server()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var clean = await session.OkAsync("validate");
        Assert.Equal(0, (int)clean["errors"]!);

        // Break the model on disk, as an agent's own file edit would: the server rescans before it answers.
        session.Repo.Replace(McpSession.CustomerPath, "\"package\": \"" + McpSession.BillingPackage + "\"", "\"package\": \"01J92P0V0000000000000000ZZ\"");
        var report = await session.OkAsync("validate");
        Assert.True((int)report["errors"]! > 0, report.ToJsonString());
        var diagnostic = report["diagnostics"]!.AsArray().First(d => (string)d!["severity"]! == "error" && (string?)d["filePath"] == McpSession.CustomerPath)!;
        Assert.Matches("^MQ[0-9]{4}$", (string)diagnostic["rule"]!);
        Assert.Equal(McpSession.Customer, (string)diagnostic["elementId"]!);
        Assert.True((int)diagnostic["line"]! > 0);
        Assert.StartsWith("/", (string)diagnostic["jsonPointer"]!, StringComparison.Ordinal);

        var scoped = await session.OkAsync("validate", new { elementIds = new[] { McpSession.Customer } });
        Assert.True((int)scoped["errors"]! > 0);
        Assert.Equal("bad-request", (await session.ErrorAsync("validate", new { elementIds = new[] { "customer" } })).Code);
    }

    [Fact]
    public async Task Validate_carries_the_resolvers_MQ4005_for_a_foreign_key_an_overlay_pins()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        session.Repo.Write(BillingEdits.InvoiceOverlayPath, BillingEdits.PinCustomerForeignKey(session.Repo.Read(BillingEdits.InvoiceOverlayPath)));
        var report = await session.OkAsync("validate");
        Assert.Equal(1, (int)report["errors"]!);
        var diagnostic = report["diagnostics"]!.AsArray().Single(d => (string)d!["rule"]! == "MQ4005")!;
        Assert.Equal(BillingEdits.InvoiceOverlayId, (string)diagnostic["elementId"]!);
        Assert.Equal(BillingEdits.InvoiceOverlayPath, (string)diagnostic["filePath"]!);
        Assert.Equal(BillingEdits.PinnedPointer, (string)diagnostic["jsonPointer"]!);
        Assert.Equal(BillingEdits.ForeignKeyMismatch, (string)diagnostic["message"]!);
        Assert.True((int)diagnostic["line"]! > 0);
        var scoped = await session.OkAsync("validate", new { elementIds = new[] { BillingEdits.InvoiceOverlayId } });
        Assert.Contains("MQ4005", scoped.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_then_diff_then_apply_by_id_and_a_stale_plan_is_refused()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var planned = await session.OkAsync("plan");
        Assert.Equal("succeeded", (string)planned["outcome"]!);
        var plan = planned["plan"]!;
        var planId = (string)plan["id"]!;
        Assert.Empty(plan["units"]!.AsArray());
        Assert.Contains("db/main/customers.sql", plan["changes"]!.AsArray().Select(c => (string)c!["path"]!));
        Assert.False(File.Exists(session.Repo.PathOf("db/main/customers.sql")));

        var diff = await session.CallAsync("get_plan_diff", new { planId, path = "db/main/customers.sql" });
        Assert.False(diff.IsError, diff.ToString());
        Assert.Contains("+CREATE TABLE customers", diff.Text, StringComparison.Ordinal);
        Assert.Equal("not-found", (await session.ErrorAsync("get_plan_diff", new { planId, path = "db/main/nothing.sql" })).Code);
        Assert.NotEmpty((await session.OkAsync("get_plan", new { planId, units = true }))["units"]!.AsArray());

        var applied = await session.OkAsync("apply_plan", new { planId });
        Assert.Equal("succeeded", (string)applied["outcome"]!);
        Assert.Contains("CREATE TABLE customers", session.Repo.Read("db/main/customers.sql"), StringComparison.Ordinal);
        Assert.True(File.Exists(session.Repo.PathOf("src/Generated/Customer.g.cs")));

        // A plan whose inputs change before it is applied is refused as stale and writes nothing.
        var customer = await session.OkAsync("get_element", new { id = McpSession.Customer });
        var edited = customer["json"]!.DeepClone();
        edited["attributes"]![1]!["length"] = 200;
        var second = await session.OkAsync("plan");
        var secondId = (string)second["plan"]!["id"]!;
        await session.OkAsync("save_element", new { id = McpSession.Customer, element = edited, expectedHash = (string)customer["hash"]! });
        var before = session.Repo.Read("db/main/customers.sql");
        var stale = await session.ErrorAsync("apply_plan", new { planId = secondId });
        Assert.Equal("stale", stale.Code);
        Assert.NotEmpty(stale.Json["staleUnits"]!.AsArray());
        Assert.Equal(before, session.Repo.Read("db/main/customers.sql"));

        var third = await session.OkAsync("plan");
        await session.OkAsync("apply_plan", new { planId = (string)third["plan"]!["id"]! });
        Assert.Contains("200", session.Repo.Read("db/main/customers.sql"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A client cancels a call with <c>notifications/cancelled</c> naming the request. The SDK's client (2.2.0) does not send it when a
    /// call's token is cancelled (only its own wait stops), so the test sends the request and the notification itself, as a
    /// conforming MCP client does. The cancelled plan stops and stores nothing, the run lock is released and the server
    /// keeps serving.
    /// </summary>
    [Fact]
    public async Task A_cancelled_plan_stops_and_the_server_keeps_serving()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Empty();
        var model = new SyntheticModelOptions { Packages = 4, Entities = 400, Relations = 800, Enums = 20, ValueObjects = 10, ScalarTypes = 4, Fanout = 10, IncludeExamplePacks = false };
        await SyntheticModelGenerator.WriteAsync(repo.RepoRoot, model, ct);
        await using var session = await McpSession.StartAsync(repo, ct, "2025-11-25");
        await session.OkAsync("get_project");

        var id = new RequestId("plan-to-cancel");
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var call = session.Client.SendRequestAsync(
            new JsonRpcRequest { Id = id, Method = RequestMethods.ToolsCall, Params = JsonNode.Parse("""{"name":"plan","arguments":{"force":true}}""") },
            stopWaiting.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        Assert.False(call.IsCompleted, "The plan finished before it could be cancelled.");
        await session.Client.SendNotificationAsync(NotificationMethods.CancelledNotification, new CancelledNotificationParams { RequestId = id, Reason = "test" }, cancellationToken: ct);
        await session.OkAsync("get_project");
        stopWaiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        // The server is still up, the run lock was released, and a later plan completes.
        Assert.False(string.IsNullOrEmpty((string?)(await session.OkAsync("get_project"))["name"]));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var planned = await session.OkAsync("plan", new { force = true });
        Assert.Equal("succeeded", (string)planned["outcome"]!);
        Assert.True(timer.Elapsed > TimeSpan.FromMilliseconds(600), "The model is too small to cancel a plan reliably: " + timer.Elapsed);

        // Only the second plan was stored: the cancelled one stopped before it finished.
        var plans = Directory.GetFiles(Path.Combine(repo.CacheDirectory, "plans"), "plan.json", SearchOption.AllDirectories);
        Assert.Equal((string)planned["plan"]!["id"]!, Path.GetFileName(Path.GetDirectoryName(Assert.Single(plans))));
    }
}
