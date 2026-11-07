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
        "apply_batch", "apply_plan", "compare_snapshots", "create_element", "create_seed", "create_snapshot", "delete_element", "delete_extension_file", "delete_pack", "delete_pack_file", "explain_unit",
        "export_process", "export_seed_csv", "fold_migrations", "format_model", "generate", "get_binding_drift", "get_calculated", "get_database_view", "get_element", "get_elements", "get_ledger_ddl", "get_materialize_status", "get_model_index", "get_model_kinds", "get_pack",
        "get_pack_exports", "get_pack_outputs", "get_plan", "get_plan_diff", "get_project", "get_references", "get_resolved_model", "get_rights", "get_schema", "get_settings", "get_template_context",
        "get_translations", "import_process", "import_seed_csv", "list_extension_files", "list_migrations", "list_pack_files", "list_packs", "list_snapshots", "list_validation_rules",
        "localization_status", "move_extension_file", "move_pack_file", "new_pack", "patch_element", "plan", "preview_binding_sql", "preview_expression", "preview_materialize", "preview_query_sql", "preview_seed_rows", "preview_unit", "read_extension_file", "read_pack_file", "record_scenario",
        "reference_type_usage", "rename_pack", "restore_snapshot", "save_element", "save_pack", "save_pack_settings", "save_settings", "set_translations", "simulate_process",
        "sync_enum_from_process", "tag_usage", "test_pack", "unit_paths", "validate", "verify_scenarios", "write_extension_file", "write_pack_file",
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
    public async Task The_read_tools_are_the_catalog_the_editor_assistant_shares()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);
        var tools = await session.Client.ListToolsAsync(cancellationToken: ct);
        Assert.Equal(32, Engine.AgentTools.All.Count);
        foreach (var tool in Engine.AgentTools.All)
        {
            var listed = tools.Single(t => t.Name == tool.Name).ProtocolTool;
            Assert.Equal(tool.Title, listed.Title);
            Assert.Equal(tool.Description, listed.Description);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(tool.InputSchema.GetRawText()), JsonNode.Parse(listed.InputSchema.GetRawText())), tool.Name);
            Assert.True(listed.Annotations!.ReadOnlyHint);
            Assert.True(listed.Annotations.IdempotentHint);
            Assert.False(listed.Annotations.OpenWorldHint);
        }
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
    public async Task Apply_batch_by_name_previews_then_applies()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var operations = JsonNode.Parse(ModelApplyTests.PaymentMethods)!;
        var before = session.Repo.Tree(".maquettiste");

        var preview = await session.OkAsync("apply_batch", new { operations, dryRun = true });

        Assert.Equal("saved", (string)preview["outcome"]!);
        var files = preview["files"]!.AsArray();
        Assert.Equal(3, files.Count);
        Assert.All(files, f =>
        {
            Assert.Null(f!["after"]);
            Assert.StartsWith("--- a/" + (string)f["path"]!, (string)f["diff"]!, StringComparison.Ordinal);
        });
        Assert.Equal(10, preview["ids"]!["minted"]!.AsArray().Count);
        Assert.Equal(["$pm", "$type", "@column:billing.payment_method.code", "@database:main", "@schema:billing"],
            preview["ids"]!["names"]!.AsObject().Select(p => p.Key));
        Assert.NotNull(preview["diagnosticsAdded"]);
        Assert.NotNull(preview["diagnosticsRemoved"]);
        Assert.Equal(before, session.Repo.Tree(".maquettiste"));

        var applied = await session.OkAsync("apply_batch", new { operations });

        Assert.Equal("saved", (string)applied["outcome"]!);
        var table = (string)applied["ids"]!["names"]!["$pm"]!;
        Assert.Equal(table, (string)applied["items"]![0]!["id"]!);
        Assert.Equal(10, applied["ids"]!["minted"]!.AsArray().Count);
        Assert.NotNull(applied["facts"]);
        var written = await session.OkAsync("get_element", new { id = table });
        Assert.Equal("payment_method", (string)written["json"]!["name"]!);
        Assert.DoesNotContain("\"@", written["json"]!.ToJsonString(), StringComparison.Ordinal);

        // A name that finds nothing fails the batch with MQ1012 and writes nothing, in a dry run as in an apply.
        var unknown = new[] { new { op = "update", id = "@entity:Nobody", expectedHash = new string('0', 64), element = new { kind = "entity", name = "Nobody" } } };
        var failed = await session.ErrorAsync("apply_batch", new { operations = unknown, dryRun = true });
        Assert.Equal("invalid", failed.Code);
        Assert.Contains("MQ1012", failed.Json["items"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(failed.Json["files"]!.AsArray());
    }

    [Fact]
    public async Task Create_element_takes_a_table_seed_with_column_names()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        const string Table = "01K7SMC0000000000000000001", Code = "01K7SMC0000000000000000002", Label = "01K7SMC0000000000000000003";
        var table = await session.OkAsync("create_element", new
        {
            element = new
            {
                kind = "table", id = Table, name = "currency", database = "01J92P0V1QRN2181XM2ZWE02W4",
                columns = new object[]
                {
                    new { id = Code, name = "code", type = "string", length = 3, nullable = false },
                    new { id = Label, name = "label", type = "string", length = 40, nullable = false },
                },
                primaryKey = new { columns = new[] { Code } },
            },
        });
        Assert.Equal("saved", (string)table["outcome"]!);

        // Columns by name, cells keyed by column name: stored as ids and positional cells.
        var seed = await session.OkAsync("create_element", new
        {
            element = JsonNode.Parse("""
                {"kind":"seed","name":"currencies","target":"01K7SMC0000000000000000001","columns":["label","code"],
                 "rows":[{"values":{"code":"EUR","label":"Euro"}},{"values":["US dollar","USD"]}]}
                """),
        });
        Assert.Equal("saved", (string)seed["outcome"]!);
        var written = (await session.OkAsync("get_element", new { id = (string)seed["id"]! }))["json"]!;
        Assert.Equal([Label, Code], written["columns"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(["Euro", "EUR"], written["rows"]![0]!["values"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(["US dollar", "USD"], written["rows"]![1]!["values"]!.AsArray().Select(c => (string)c!));

        // A name the table does not have is invalid (MQ1002 at the column), and nothing is written.
        var before = session.Repo.Tree(".maquettiste");
        var refused = await session.ErrorAsync("create_element", new
        {
            element = JsonNode.Parse("""{"kind":"seed","name":"more","target":"01K7SMC0000000000000000001","columns":["code","symbol"],"rows":[]}"""),
        });
        Assert.Equal("invalid", refused.Code);
        var diagnostic = refused.Json["diagnostics"]!.AsArray().Single()!;
        Assert.Equal(("MQ1002", "/columns/1"), ((string)diagnostic["rule"]!, (string)diagnostic["jsonPointer"]!));
        Assert.Equal(before, session.Repo.Tree(".maquettiste"));
    }

    [Fact]
    public async Task Patch_element_needs_the_hash_and_a_conflict_returns_the_disk_version()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var loaded = await session.OkAsync("get_element", new { id = McpSession.Customer });
        var staleHash = (string)loaded["hash"]!;
        var patch = new object[] { new { op = "replace", path = "/description", value = "Someone we bill, patched." } };

        Assert.Equal("precondition-required", (await session.ErrorAsync("patch_element", new { id = McpSession.Customer, patch })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("patch_element", new { id = McpSession.Customer, expectedHash = staleHash })).Code);
        // A patch that is not an array of operations reaches the engine: invalid with one MQ1016 at "", and nothing is written.
        foreach (var notAnArray in new object[] { 5, new { op = "replace", path = "/description", value = "Not in an array." } })
        {
            var refused = await session.ErrorAsync("patch_element", new { id = McpSession.Customer, expectedHash = staleHash, patch = notAnArray });
            Assert.Equal("invalid", refused.Code);
            var error = Assert.Single(refused.Json["diagnostics"]!.AsArray())!;
            Assert.Equal(("MQ1016", ""), ((string)error["rule"]!, (string)error["jsonPointer"]!));
        }

        // The target is an id, never a name.
        Assert.Equal("not-found", (await session.ErrorAsync("patch_element", new { id = "@entity:Customer", patch, expectedHash = staleHash })).Code);

        var saved = await session.OkAsync("patch_element", new { id = McpSession.Customer, patch, expectedHash = staleHash });

        Assert.Equal("saved", (string)saved["outcome"]!);
        var newHash = (string)saved["hash"]!;
        Assert.NotEqual(staleHash, newHash);
        Assert.Empty(saved["ids"]!["minted"]!.AsArray());
        Assert.Empty(saved["facts"]!.AsArray());
        var onDisk = session.Repo.Read(McpSession.CustomerPath);
        Assert.Contains("\"description\": \"Someone we bill, patched.\"", onDisk, StringComparison.Ordinal);

        // A patch sent as a string holding the array, with the hash read before the save: a conflict with the disk version and the patch.
        var theirs = """[ { "op": "replace", "path": "/description", "value": "A competing edit." } ]""";
        var conflict = await session.ErrorAsync("patch_element", new { id = McpSession.Customer, patch = theirs, expectedHash = staleHash });
        Assert.Equal("conflict", conflict.Code);
        Assert.Equal(409, (int)conflict.Json["status"]!);
        Assert.Equal(newHash, (string)conflict.Json["hash"]!);
        Assert.Equal("Someone we bill, patched.", (string)conflict.Json["current"]!["json"]!["description"]!);
        Assert.Equal("A competing edit.", (string)conflict.Json["submitted"]![0]!["value"]!);
        Assert.Equal(onDisk, session.Repo.Read(McpSession.CustomerPath));

        // A patch that cannot apply is invalid with MQ1016 at the operation, and nothing is written.
        var failed = await session.ErrorAsync("patch_element", new
        {
            id = McpSession.Customer,
            expectedHash = newHash,
            patch = new object[] { new { op = "replace", path = "/name", value = "Client" }, new { op = "remove", path = "/attributes/9" } },
        });
        Assert.Equal("invalid", failed.Code);
        var diagnostic = Assert.Single(failed.Json["diagnostics"]!.AsArray())!;
        Assert.Equal(("MQ1016", "/1"), ((string)diagnostic["rule"]!, (string)diagnostic["jsonPointer"]!));
        Assert.Equal("invalid", (await session.ErrorAsync("patch_element", new { id = McpSession.Customer, expectedHash = newHash, patch = new[] { new { op = "replace", path = "/kind", value = "enum" } } })).Code);
        Assert.Equal(onDisk, session.Repo.Read(McpSession.CustomerPath));
    }

    [Fact]
    public async Task Patch_element_answers_the_facts_its_save_recorded()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var loaded = await session.OkAsync("get_element", new { id = McpSession.Customer });

        // A rename by patch records its fact like save_element; a new attribute sent without an id gets one minted.
        var saved = await session.OkAsync("patch_element", new
        {
            id = McpSession.Customer,
            expectedHash = (string)loaded["hash"]!,
            patch = new object[]
            {
                new { op = "test", path = "/attributes/3/name", value = "customerSince" },
                new { op = "replace", path = "/attributes/3/name", value = "memberSince" },
                new { op = "add", path = "/attributes/-", value = new { name = "nickname", type = "string", length = 40 } },
            },
        });

        Assert.Equal("saved", (string)saved["outcome"]!);
        Assert.Empty(saved["diagnostics"]!.AsArray());
        var fact = Assert.Single(saved["facts"]!.AsArray())!;
        Assert.Equal(("rename", "complete", "rename column billing.customers.customer_since to member_since"),
            ((string)fact["fact"]!, (string)fact["state"]!, (string)fact["summary"]!));
        Assert.Contains(saved["changes"]!["changed"]!.AsArray(), c => (string)c!["id"]! == (string)fact["id"]!);
        var minted = Assert.Single(saved["ids"]!["minted"]!.AsArray())!;
        Assert.Equal(("/attributes/4", "attribute"), ((string)minted["pointer"]!, (string)minted["kind"]!));
        var customer = await session.OkAsync("get_element", new { id = McpSession.Customer });
        Assert.Equal((string)minted["id"]!, (string)customer["json"]!["attributes"]![4]!["id"]!);
        Assert.Equal((string)saved["hash"]!, (string)customer["hash"]!);
    }

    private const string CustomersSql = "db/main/customers.sql";
    private const string CustomerClass = "src/Generated/Customer.g.cs";
    private const string CustomerTable = McpSession.Customer + "@01J92P0V1QRN2181XM2ZWE02W4"; // a synthesized table's id is its key

    private static List<string> Paths(JsonNode? changes) => [.. changes!.AsArray().Select(c => (string)c!["path"]!)];

    private static List<(string Path, string Kind)> Files(JsonNode summary) =>
        [.. summary["packs"]!.AsArray().SelectMany(p => p!["files"]!.AsArray()).Select(f => ((string)f!["path"]!, (string)f["kind"]!))];

    private static void WidenCustomerNameAndDropItsClass(CliRepo repo)
    {
        repo.Replace(McpSession.CustomerPath, "\"length\": 120", "\"length\": 150");
        File.Delete(repo.PathOf(CustomerClass));
    }

    [Fact]
    public async Task Plan_takes_units_and_elements_and_applies_only_them()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var first = await session.OkAsync("plan");
        Assert.Equal("ddl/table:" + CustomerTable, (string)first["plan"]!["changes"]!.AsArray().Single(c => (string)c!["path"]! == CustomersSql)!["unitKey"]!);
        await session.OkAsync("apply_plan", new { planId = (string)first["plan"]!["id"]! });
        WidenCustomerNameAndDropItsClass(session.Repo);

        // The table unit of the customers table: the plan holds its script only and stores both filters.
        var planned = await session.OkAsync("plan", new { units = new[] { "ddl/table" }, elements = new[] { CustomerTable } });
        Assert.Equal("succeeded", (string)planned["outcome"]!);
        var plan = planned["plan"]!;
        Assert.Equal([CustomersSql], Paths(plan["changes"]));
        Assert.Equal(["ddl/table"], plan["request"]!["units"]!.AsArray().Select(v => (string)v!));
        Assert.Equal([CustomerTable], plan["request"]!["elements"]!.AsArray().Select(v => (string)v!));

        // Its apply plans again with the filters: the class, which a full run would write, is neither written nor stale.
        var applied = await session.OkAsync("apply_plan", new { planId = (string)plan["id"]! });
        Assert.Equal("succeeded", (string)applied["outcome"]!);
        Assert.Contains("varchar(150)", session.Repo.Read(CustomersSql), StringComparison.Ordinal);
        Assert.False(File.Exists(session.Repo.PathOf(CustomerClass)));
        var full = Paths((await session.OkAsync("plan"))["plan"]!["changes"]);
        Assert.Contains(CustomerClass, full);
        Assert.DoesNotContain(CustomersSql, full);

        // An unknown unit makes the plan invalid (MQ6032), with nothing in it; an empty value is a bad request.
        var unknown = await session.OkAsync("plan", new { units = new[] { "nosuch/table" } });
        Assert.Equal("invalid", (string)unknown["outcome"]!);
        Assert.Equal("MQ6032", (string)Assert.Single(unknown["plan"]!["diagnostics"]!.AsArray())!["rule"]!);
        Assert.Empty(unknown["plan"]!["changes"]!.AsArray());
        Assert.Equal("bad-request", (await session.ErrorAsync("plan", new { units = new[] { "" } })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("plan", new { elements = new[] { " " } })).Code);
    }

    [Fact]
    public async Task Generate_plans_and_applies_in_one_call_and_stores_no_plan()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var generated = await session.OkAsync("generate");
        Assert.Equal(["outcome", "unitsRendered", "unitsSkipped", "filesWritten", "filesDeleted", "packs", "diagnostics", "moreDiagnostics"],
            generated.AsObject().Select(p => p.Key));
        Assert.Equal("succeeded", (string)generated["outcome"]!);
        Assert.True((int)generated["unitsRendered"]! > 0);
        var packs = generated["packs"]!.AsArray();
        Assert.Equal(["classes", "ddl"], packs.Select(p => (string)p!["pack"]!));
        foreach (var pack in packs)
        {
            Assert.Equal(["added"], pack!["counts"]!.AsObject().Select(c => c.Key));
            var paths = pack["files"]!.AsArray().Select(f => (string)f!["path"]!).ToList();
            Assert.Equal((int)pack["counts"]!["added"]!, paths.Count);
            Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
            Assert.Equal(0, (int)pack["more"]!);
        }

        Assert.Equal((int)generated["filesWritten"]!, Files(generated).Count);
        Assert.Contains((CustomersSql, "added"), Files(generated));
        Assert.Contains("CREATE TABLE customers", session.Repo.Read(CustomersSql), StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(Path.Combine(session.Repo.CacheDirectory, "plans"))
            ? Directory.GetFiles(Path.Combine(session.Repo.CacheDirectory, "plans"), "plan.json", SearchOption.AllDirectories)
            : []);

        // Nothing changed: every unit is skipped and no pack is listed. An edit writes its one file.
        var again = await session.OkAsync("generate");
        Assert.Equal(0, (int)again["unitsRendered"]!);
        Assert.True((int)again["unitsSkipped"]! > 0);
        Assert.Empty(again["packs"]!.AsArray());
        session.Repo.Replace(McpSession.CustomerPath, "\"length\": 120", "\"length\": 150");
        var edited = await session.OkAsync("generate", new { noWait = true });
        Assert.Equal([(CustomersSql, "modified")], Files(edited));
        Assert.Contains("varchar(150)", session.Repo.Read(CustomersSql), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_answers_invalid_with_diagnostics_and_writes_nothing()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        session.Repo.Replace(McpSession.CustomerPath, "\"package\": \"" + McpSession.BillingPackage + "\"", "\"package\": \"01J92P0V0000000000000000ZZ\"");
        var failed = await session.ErrorAsync("generate");
        Assert.Equal("invalid", failed.Code);
        var body = failed.Json;
        Assert.Equal(["code", "status", "title", "outcome", "unitsRendered", "unitsSkipped", "filesWritten", "filesDeleted", "packs", "diagnostics", "moreDiagnostics"],
            body.AsObject().Select(p => p.Key));
        Assert.Equal(422, (int)body["status"]!);
        Assert.Equal("invalid", (string)body["outcome"]!);
        Assert.Equal(0, (int)body["filesWritten"]!);
        Assert.Empty(body["packs"]!.AsArray());
        var severities = body["diagnostics"]!.AsArray().Select(d => (string)d!["severity"]!).ToList();
        Assert.Equal("error", severities[0]);
        Assert.Equal(severities.OrderBy(s => s == "error" ? 0 : 1), severities);
        Assert.Contains(body["diagnostics"]!.AsArray(), d => (string?)d!["filePath"] == McpSession.CustomerPath);
        Assert.Equal(0, (int)body["moreDiagnostics"]!);
        Assert.False(Directory.Exists(session.Repo.PathOf("db")));
        Assert.False(Directory.Exists(session.Repo.PathOf("src/Generated")));

        // The caps: 50 paths a pack, 100 diagnostics with the errors first, and the counts of what is left out.
        var warning = Engine.Diagnostics.RuleCatalog.Create("MQ6034", "Nothing selected.");
        var error = Engine.Diagnostics.RuleCatalog.Create("MQ6032", "Unknown unit.");
        var changes = Enumerable.Range(0, 60)
            .Select(i => new Engine.Pipeline.FileChange($"db/{59 - i:00}.sql", Engine.Pipeline.FileChangeKind.Added, "ddl", "ddl/table:t", null, "h", null))
            .Append(new Engine.Pipeline.FileChange("db/same.sql", Engine.Pipeline.FileChangeKind.Unchanged, "ddl", "ddl/table:t", "h", "h", null))
            .ToList();
        var result = new Engine.GenerationResult("run", Engine.Pipeline.GenerationMode.Apply, Engine.RunOutcome.Invalid, changes, 1, 0, 60, 0,
            [.. Enumerable.Repeat(warning, 110), error], []);
        var summary = Mcp.GenerateSummary.Of(result);
        var pack = Assert.Single(summary.Packs);
        Assert.Equal(60, pack.Counts["added"]);
        Assert.Equal(["added"], pack.Counts.Keys);
        Assert.Equal(50, pack.Files.Count);
        Assert.Equal("db/00.sql", pack.Files[0].Path);
        Assert.Equal(10, pack.More);
        Assert.Equal(100, summary.Diagnostics.Count);
        Assert.Equal("MQ6032", summary.Diagnostics[0].Rule);
        Assert.Equal(11, summary.MoreDiagnostics);
    }

    [Fact]
    public async Task Generate_scopes_to_units_and_elements()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        await session.OkAsync("generate");
        WidenCustomerNameAndDropItsClass(session.Repo);

        // The customers table's unit: its script only; the class a full run would write stays missing.
        var table = await session.OkAsync("generate", new { units = new[] { "ddl/table" }, elements = new[] { CustomerTable } });
        Assert.Equal("succeeded", (string)table["outcome"]!);
        Assert.Equal([(CustomersSql, "modified")], Files(table));
        Assert.Contains("varchar(150)", session.Repo.Read(CustomersSql), StringComparison.Ordinal);
        Assert.False(File.Exists(session.Repo.PathOf(CustomerClass)));

        // A bare unit id and the entity's id: the class only.
        var entity = await session.OkAsync("generate", new { units = new[] { "entity" }, elements = new[] { McpSession.Customer } });
        Assert.Equal([(CustomerClass, "added")], Files(entity));

        // An id nothing has or had selects nothing: the warning MQ6034 and no file. An unknown unit is invalid (MQ6032).
        var nothing = await session.OkAsync("generate", new { elements = new[] { "01J92P0VZZ0000000000000099" } });
        Assert.Equal("succeeded", (string)nothing["outcome"]!);
        Assert.Equal(["MQ6034"], nothing["diagnostics"]!.AsArray().Select(d => (string)d!["rule"]!));
        Assert.Empty(nothing["packs"]!.AsArray());
        var unknown = await session.ErrorAsync("generate", new { units = new[] { "nosuch" } });
        Assert.Equal("invalid", unknown.Code);
        Assert.Equal("MQ6032", (string)unknown.Json["diagnostics"]![0]!["rule"]!);
        Assert.Equal("bad-request", (await session.ErrorAsync("generate", new { elements = new[] { "" } })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("generate", new { handEdits = "sometimes" })).Code);

        // A full run finds nothing left to write.
        Assert.Empty((await session.OkAsync("generate"))["packs"]!.AsArray());
    }

    [Fact]
    public async Task Generate_against_a_snapshot_diffs_from_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        GenerateTests.AddDiffPack(repo);
        await using var session = await McpSession.StartAsync(repo, ct);
        Assert.Equal("succeeded", (string)(await session.OkAsync("generate"))["outcome"]!);
        var committed = repo.Read(".maquettiste/snapshots/main.json");
        var baseline = (string)(await session.OkAsync("create_snapshot", new { name = "Base" }))["id"]!;
        repo.Replace(McpSession.CustomerPath, "\"length\": 120", "\"length\": 150");

        // The diff runs from the snapshot (a baseline, and the change since it), and the committed schema snapshot stays as it was.
        var against = await session.OkAsync("generate", new { against = baseline });
        Assert.Equal("succeeded", (string)against["outcome"]!);
        Assert.Contains(("db/changes/main.txt", "modified"), Files(against));
        var diff = repo.Read("db/changes/main.txt");
        Assert.Contains("from_baseline: true", diff, StringComparison.Ordinal);
        Assert.Contains("empty: false", diff, StringComparison.Ordinal);
        Assert.Equal(committed, repo.Read(".maquettiste/snapshots/main.json"));
        Assert.Equal("bad-request", (await session.ErrorAsync("generate", new { against = "no-such-snapshot" })).Code);
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
    public async Task Get_resolved_model_pages_carry_calculated_values()
    {
        // next-phase-2-design.md section 5.5: a page carries the calculated values of its records and the sub-objects they list, by
        // site then name; a value that fails is left out and its diagnostic joins the page's.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/extensions/calculated/names.js", """
            maquettiste.calculated({
              name: "apiName",
              kinds: ["entity"],
              schema: { type: "string" },
              value(entity) { return entity.name.toLowerCase(); },
            });
            maquettiste.calculated({
              name: "broken",
              kinds: ["entity"],
              schema: { type: "string" },
              value(entity) { throw new Error("no value for " + entity.name); },
            });
            maquettiste.calculated({
              name: "label",
              kinds: ["attribute"],
              schema: { type: "string" },
              value(attribute) { return "attribute " + attribute.name; },
            });

            """);
        await using var session = await McpSession.StartAsync(repo, ct);

        var page = await session.OkAsync("get_resolved_model", new { scope = "entities", limit = 1 });
        var entity = Assert.Single(page["items"]!.AsArray())!;
        var calculated = page["calculated"]!.AsObject();
        var own = calculated[(string)entity["id"]!]!.AsObject();
        Assert.Equal([("apiName", ((string)entity["name"]!).ToLowerInvariant())], own.Select(p => (p.Key, (string)p.Value!)));
        Assert.NotEmpty(entity["attributes"]!.AsArray());
        foreach (var attribute in entity["attributes"]!.AsArray())
        {
            // An attribute the entity does not own (inherited, a stereotype's) is keyed <holder id>/<attribute id>.
            var id = (string)attribute!["id"]!;
            Assert.Contains(calculated, p => (p.Key == id || p.Key == (string)entity["id"]! + "/" + id)
                && (string?)p.Value!["label"] == "attribute " + (string)attribute["name"]!);
        }

        var failure = Assert.Single(page["diagnostics"]!.AsArray())!;
        Assert.Equal(("MQ5010", (string)entity["id"]!), ((string)failure["rule"]!, (string)failure["elementId"]!));
        Assert.Contains("no value for " + (string)entity["name"]!, (string)failure["message"]!, StringComparison.Ordinal);

        // A scope without a declaring kind carries none.
        Assert.Empty((await session.OkAsync("get_resolved_model", new { scope = "enums", limit = 5 }))["calculated"]!.AsObject());
    }

    [Fact]
    public async Task Get_model_index_lists_an_operation_row()
    {
        // next-phase-2-design.md section 4.1: an operation's index row carries its subject and its effect.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/model/operations/invoice/send.json", """
            {
              "$schema": "../../../.schema/v1/operation.json",
              "kind": "operation",
              "id": "01K9QPR0000000000000000001",
              "name": "send",
              "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "subject": "01J92P0V0FJ23CGSNKM7P1W5V7",
              "effect": "change",
              "outcomes": [
                {
                  "id": "01K9QPR0000000000000000002",
                  "name": "applied"
                }
              ]
            }

            """);
        await using var session = await McpSession.StartAsync(repo, ct);

        var row = Assert.Single((await session.OkAsync("get_model_index", new { kind = "operation" })).AsArray())!;

        Assert.Equal(("01K9QPR0000000000000000001", "operation", "send", "01J92P0V01KDRN8GX5PGYCNKSX", "01J92P0V0FJ23CGSNKM7P1W5V7", "change",
                ".maquettiste/model/operations/invoice/send.json"),
            ((string)row["id"]!, (string)row["kind"]!, (string)row["name"]!, (string)row["package"]!, (string)row["subject"]!, (string)row["effect"]!,
                (string)row["path"]!));
        Assert.Null((await session.OkAsync("get_model_index", new { kind = "entity" })).AsArray()[0]!["effect"]);
    }

    [Fact]
    public async Task Get_resolved_model_scope_operations_pages_operation_records()
    {
        // next-phase-2-design.md section 4.1: one OperationRecord per operation, its parts inline and other objects by id.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        const string billing = "01J92P0V01KDRN8GX5PGYCNKSX", invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
        repo.Write(".maquettiste/model/operations/invoice/send.json", $$"""
            {
              "$schema": "../../../.schema/v1/operation.json",
              "kind": "operation",
              "id": "01K9QPR0000000000000000001",
              "name": "send",
              "package": "{{billing}}",
              "subject": "{{invoice}}",
              "effect": "change",
              "input": [
                {
                  "id": "01K9QPR0000000000000000005",
                  "name": "recipient",
                  "type": "string",
                  "length": 254,
                  "required": true
                }
              ],
              "outcomes": [
                {
                  "id": "01K9QPR0000000000000000002",
                  "name": "applied",
                  "returns": {
                    "entity": "{{invoice}}"
                  }
                },
                {
                  "id": "01K9QPR0000000000000000006",
                  "name": "waiting",
                  "completes": "later"
                }
              ],
              "errors": [
                {
                  "id": "01K9QPR0000000000000000007",
                  "name": "missing_recipient",
                  "input": "01K9QPR0000000000000000005"
                }
              ]
            }

            """);
        repo.Write(".maquettiste/model/operations/ping.json", """
            {
              "$schema": "../../.schema/v1/operation.json",
              "kind": "operation",
              "id": "01K9QPR0000000000000000003",
              "name": "ping",
              "effect": "read",
              "outcomes": [
                {
                  "id": "01K9QPR0000000000000000004",
                  "name": "pong"
                }
              ]
            }

            """);
        await using var session = await McpSession.StartAsync(repo, ct);

        var first = await session.OkAsync("get_resolved_model", new { scope = "operations", limit = 1 });
        var ping = Assert.Single(first["items"]!.AsArray())!;
        Assert.Equal(("operation", "ping", "read"), ((string)ping["kind"]!, (string)ping["name"]!, (string)ping["effect"]!));
        Assert.Null(ping["subject"]);
        Assert.Null(ping["level"]);
        var second = await session.OkAsync("get_resolved_model", new { scope = "operations", limit = 1, cursor = (string)first["next"]! });
        Assert.Null(second["next"]);
        var send = Assert.Single(second["items"]!.AsArray())!;
        Assert.Equal(("send", billing, invoice, "record", "01K9QPR0000000000000000002"),
            ((string)send["name"]!, (string)send["package"]!, (string)send["subject"]!, (string)send["level"]!, (string)send["defaultOutcome"]!));
        var recipient = Assert.Single(send["input"]!.AsArray())!;
        Assert.Equal(("recipient", "string", 254, true), ((string)recipient["name"]!, (string)recipient["type"]!["builtin"]!, (int)recipient["length"]!, (bool)recipient["required"]!));
        var outcomes = send["outcomes"]!.AsArray();
        Assert.Equal((true, "now", invoice), ((bool)outcomes[0]!["isDefault"]!, (string)outcomes[0]!["completes"]!, (string)outcomes[0]!["returns"]!["entity"]!));
        Assert.Equal((false, "later"), ((bool)outcomes[1]!["isDefault"]!, (string)outcomes[1]!["completes"]!));
        Assert.Null(outcomes[1]!["returns"]);
        var error = Assert.Single(send["errors"]!.AsArray())!;
        Assert.Equal(("missing_recipient", "01K9QPR0000000000000000005"), ((string)error["code"]!, (string)error["input"]!));
        Assert.Empty(send["fires"]!.AsArray());
        Assert.Equal("send", (string)send["displayName"]!);
    }

    [Fact]
    public async Task Get_binding_drift_answers_only_drifted_bindings()
    {
        // next-phase-2-design.md section 3.3 (D35): InvoiceNote records provenance through a re-sync; renaming the notes table's body
        // column behind the server is drift for it alone (CustomerNote records none, and every column of notes is accounted for).
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        await using var session = await McpSession.StartAsync(repo, ct);
        Assert.Empty((await session.OkAsync("get_binding_drift"))["bindings"]!.AsArray());
        var resync = new[] { new { op = "resync-binding", database = "@database:main", entities = new[] { "@entity:InvoiceNote" } } };
        Assert.Equal("saved", (string)(await session.OkAsync("apply_batch", new { operations = resync }))["outcome"]!);

        repo.Replace(".maquettiste/model/databases/main/tables/notes.json", "\"name\": \"body\"", "\"name\": \"note_body\"");
        var drift = await session.OkAsync("get_binding_drift", new { database = "main" });

        var binding = Assert.Single(drift["bindings"]!.AsArray())!;
        Assert.Equal(("InvoiceNote", "notes", true, false), ((string)binding["entityName"]!, (string)binding["sourceName"]!, (bool)binding["hasProvenance"]!, (bool)binding["stale"]!));
        Assert.Equal(
            new JsonObject { ["column"] = "01K6BND0000000000000000005", ["name"] = "note_body", ["attribute"] = "01K6BND0000000000000000013", ["attributeName"] = "body" }.ToJsonString(),
            Assert.Single(binding["renamed"]!.AsArray())!.ToJsonString());
        Assert.Empty(binding["retyped"]!.AsArray());
        Assert.Null(drift["next"]);

        // all lists every binding; an unknown database or entity is bad-request.
        var all = await session.OkAsync("get_binding_drift", new { all = true, limit = 2 });
        Assert.Equal(["CustomerNote", "InvoiceNote"], all["bindings"]!.AsArray().Select(b => (string)b!["entityName"]!));
        var rest = await session.OkAsync("get_binding_drift", new { all = true, cursor = (string)all["next"]! });
        Assert.Equal("RevenueMonth", (string)Assert.Single(rest["bindings"]!.AsArray())!["entityName"]!);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_binding_drift", new { database = "nowhere" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_binding_drift", new { entities = new[] { "01K6BNDNOSUCHENTITY0000000" } })).Code);
    }

    [Fact]
    public async Task Validate_against_a_folder_or_a_snapshot_compares_the_model_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        using var copy = new Maquettiste.Testing.TempRepo();
        CliRepo.CopyTree(repo.ModelRoot, copy.ModelRoot);
        await using var session = await McpSession.StartAsync(repo, ct);
        var snapshot = (string)(await session.OkAsync("create_snapshot", new { name = "Base" }))["id"]!;

        // Renamed by hand, behind the server: no write recorded a fact.
        const string notes = ".maquettiste/model/databases/main/tables/notes.json";
        repo.Replace(notes, "\"name\": \"body\"", "\"name\": \"text\"");
        foreach (var against in new[] { snapshot, copy.RepoRoot })
        {
            var report = await session.OkAsync("validate", new { against });
            var unexplained = report["diagnostics"]!.AsArray().Single(d => (string)d!["rule"]! == "MQ4102")!;
            Assert.Equal((notes, "/columns/3"), ((string)unexplained["filePath"]!, (string)unexplained["jsonPointer"]!));
            Assert.Contains("rename column billing.notes.body to text", (string)unexplained["message"]!, StringComparison.Ordinal);
        }

        Assert.Equal("bad-request", (await session.ErrorAsync("validate", new { against = "no-such-baseline" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("validate", new { against = snapshot, elementIds = new[] { McpSession.Customer } })).Code);
    }

    [Fact]
    public async Task Preview_seed_rows_pages_scripted_rows()
    {
        // next-phase-2-design.md section 3.4 (D44): a reference type takes one row per entity of the billing package from a rows script;
        // the preview pages them with ids derived from the seed and the code, the same on every call, and runs nothing else.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        const string type = "01K9SPM0000000000000000001", seed = "01K9SPM0000000000000000010";
        repo.Write(".maquettiste/model/reference-types/entity-kind.json", $$"""
            {"kind":"reference-type","id":"{{type}}","name":"EntityKind","code":{"id":"01K9SPM0000000000000000002","length":32},
             "label":{"id":"01K9SPM0000000000000000003","length":64} }
            """);
        repo.Write(".maquettiste/model/seeds/entity-kinds.json", $$"""
            {"kind":"seed","id":"{{seed}}","name":"EntityKinds","target":"{{type}}","columns":["code","label"],"rowsFrom":{"script":"seeds/entity-kinds.js"} }
            """);
        repo.Write(".maquettiste/extensions/seeds/entity-kinds.js", """
            maquettiste.rows((model, seed) => model.all("entity")
              .map(d => ({ code: d.name.toLowerCase(), label: d.name }))
              .sort((a, b) => a.code < b.code ? -1 : a.code > b.code ? 1 : 0));
            """);
        await using var session = await McpSession.StartAsync(repo, ct);

        var first = await session.OkAsync("preview_seed_rows", new { seed, limit = 2 });

        Assert.Equal(("seeds/entity-kinds.js", 2), ((string)first["script"]!, first["rows"]!.AsArray().Count));
        var total = (int)first["total"]!;
        Assert.True(total > 2, first.ToJsonString());
        var rows = first["rows"]!.AsArray().ToList();
        var cursor = (string?)first["next"];
        while (cursor is not null)
        {
            var page = await session.OkAsync("preview_seed_rows", new { seed, cursor, limit = 2 });
            rows.AddRange(page["rows"]!.AsArray());
            cursor = (string?)page["next"];
        }

        Assert.Equal(total, rows.Count);
        Assert.Contains(rows, row => (string)row!["values"]!["code"]! == "customer");
        Assert.All(rows, row =>
        {
            var code = (string)row!["key"]![0]!;
            Assert.Equal(Engine.Model.StableIds.RowId(seed, [code]), (string)row["id"]!);
            Assert.Equal(code, (string)row["values"]!["code"]!);
            Assert.Empty(row["refs"]!.AsArray());
        });
        // The same ids on the next call.
        var again = await session.OkAsync("preview_seed_rows", new { seed, limit = 2 });
        Assert.Equal(first["rows"]!.ToJsonString(), again["rows"]!.ToJsonString());
        Assert.Equal("not-found", (await session.ErrorAsync("preview_seed_rows", new { seed = type })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("preview_seed_rows", new { seed, database = "nowhere" })).Code);
    }

    [Fact]
    public async Task Reference_type_usage_answers_columns()
    {
        // next-phase-2-design.md section 3.2 (D24): reference_type_usage answers columns beside usages, the designed column whose
        // typeRef names the type with its table, database, required and no storage choice or bound table (the billing project states
        // none, and the type has no binding).
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        const string state = "01K9X3A0000000000000000001", events = "01K9X3C0000000000000000001", status = "01K9X3C0000000000000000003";
        repo.Write(".maquettiste/model/reference-types/invoice-state.json", $$"""
            { "kind": "reference-type", "id": "{{state}}", "name": "InvoiceState",
              "code": { "id": "01K9X3A0000000000000000002", "length": 1 }, "label": { "id": "01K9X3A0000000000000000003", "length": 40 } }
            """);
        repo.Write(".maquettiste/model/databases/main/tables/invoice-events.json", $$"""
            { "kind": "table", "id": "{{events}}", "name": "invoice_events", "database": "01J92P0V1QRN2181XM2ZWE02W4",
              "columns": [ { "id": "01K9X3C0000000000000000002", "name": "id", "type": "uuid", "nullable": false },
                           { "id": "{{status}}", "name": "status", "type": "string", "typeRef": { "ref": "{{state}}" }, "length": 1 } ],
              "primaryKey": { "columns": ["01K9X3C0000000000000000002"] } }
            """);
        await using var session = await McpSession.StartAsync(repo, ct);

        var usage = await session.OkAsync("reference_type_usage", new { id = state });

        Assert.Empty(usage["usages"]!.AsArray());
        var column = Assert.Single(usage["columns"]!.AsArray())!;
        Assert.Equal(
            new JsonObject
            {
                ["table"] = events, ["column"] = status, ["database"] = "01J92P0V1QRN2181XM2ZWE02W4", ["domain"] = null, ["required"] = false,
                ["storage"] = null, ["boundTable"] = null,
            }.ToJsonString(),
            column.ToJsonString());
        Assert.Equal("not-found", (await session.ErrorAsync("reference_type_usage", new { id = events })).Code);
    }

    [Fact]
    public async Task Get_references_shows_an_xref_with_schema_and_optional()
    {
        // next-phase-1-design.md section 4.1: an id an x-ref types in a custom property is a reference, and its row says which
        // schema types it and whether remove-references may clear it.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/extensions/links.json", """
            {
              "name": "links",
              "appliesTo": {
                "kinds": [
                  "entity"
                ]
              },
              "properties": {
                "billedTo": {
                  "type": "string",
                  "x-ref": {
                    "kind": "entity"
                  }
                },
                "watchers": {
                  "type": "array",
                  "items": {
                    "type": "string",
                    "x-ref": {
                      "kind": "entity",
                      "optional": true
                    }
                  }
                }
              }
            }

            """);
        repo.Replace(".maquettiste/model/entities/invoice.json", "    \"retentionDays\": 3650\n",
            $"    \"billedTo\": \"{McpSession.Customer}\",\n    \"retentionDays\": 3650,\n    \"watchers\": [\"{McpSession.Customer}\", \"Ann\"]\n");
        await using var session = await McpSession.StartAsync(repo, ct);

        var references = (await session.OkAsync("get_references", new { id = McpSession.Customer })).AsArray();

        const string invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
        var billedTo = references.Single(r => (string?)r!["jsonPointer"] == "/properties/billedTo")!;
        Assert.Equal((invoice, invoice, "billedTo", "extension:links"), ((string)billedTo["fromElementId"]!, (string)billedTo["fromId"]!, (string)billedTo["field"]!, (string)billedTo["schema"]!));
        Assert.Null(billedTo["optional"]); // left out when false
        var watcher = references.Single(r => (string?)r!["jsonPointer"] == "/properties/watchers/0")!;
        Assert.True((bool)watcher["optional"]!);
        Assert.Equal("extension:links", (string)watcher["schema"]!);
        Assert.DoesNotContain(references, r => (string?)r!["jsonPointer"] == "/properties/watchers/1");
        // Every other reference carries neither key.
        Assert.All(references.Where(r => !((string)r!["jsonPointer"]!).StartsWith("/properties/", StringComparison.Ordinal)),
            r => Assert.False(r!.AsObject().ContainsKey("schema") || r.AsObject().ContainsKey("optional")));
    }

    [Fact]
    public async Task Get_element_returns_rationale_decision_and_status()
    {
        // next-phase-1-design.md section 4.5: get_element returns the whole document, so the three keys come back as written.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        repo.Replace(McpSession.CustomerPath, "  \"description\": \"Someone we bill.\",\n",
            "  \"description\": \"Someone we bill.\",\n  \"rationale\": \"One row per **billed** party.\",\n  \"decision\": \"https://example.com/adr/0042\",\n  \"status\": \"approved\",\n");
        repo.Replace(McpSession.CustomerPath, "      \"name\": \"customerSince\",\n      \"type\": \"date\"\n",
            "      \"name\": \"customerSince\",\n      \"type\": \"date\",\n      \"status\": \"deprecated\"\n");
        await using var session = await McpSession.StartAsync(repo, ct);

        var element = await session.OkAsync("get_element", new { id = McpSession.Customer });

        var json = element["json"]!;
        Assert.Equal(("One row per **billed** party.", "https://example.com/adr/0042", "approved"),
            ((string)json["rationale"]!, (string)json["decision"]!, (string)json["status"]!));
        Assert.Equal("deprecated", (string)json["attributes"]!.AsArray().Single(a => (string)a!["name"]! == "customerSince")!["status"]!);
        Assert.Empty((await session.OkAsync("validate", new { elementIds = new[] { McpSession.Customer } }))["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task Get_database_view_shows_inherited_members()
    {
        // next-phase-2-design.md section 3.1: audited contributes two columns and an index, and schema billing gives it to every table;
        // an agent reads the keyed ids it must write from memberOrigin, which own members leave null. notes keeps its own created_at.
        var ct = TestContext.Current.CancellationToken;
        const string mainDatabase = "01J92P0V1QRN2181XM2ZWE02W4", notes = "01K6BND0000000000000000001";
        const string createdAt = "01JC7AVD1TC0000000000000C1", updatedAt = "01JC7AVD1TC0000000000000C2", createdIndex = "01JC7AVD1TC0000000000000X1";
        using var repo = CliRepo.Billing();
        repo.Replace(".maquettiste/model/vocabularies/stereotypes/audited.json", "  \"defaultProperties\": {",
            $$"""
              "columns": [
                { "id": "{{createdAt}}", "name": "created_at", "type": "datetimeoffset", "nullable": false, "defaultSql": { "*": "CURRENT_TIMESTAMP" } },
                { "id": "{{updatedAt}}", "name": "updated_at", "type": "datetimeoffset" } ],
              "indexes": [ { "id": "{{createdIndex}}", "columns": [ { "column": "{{createdAt}}" } ] } ],
              "defaultProperties": {
            """);
        repo.Replace(".maquettiste/model/databases/main/database.json", "\"name\": \"billing\"", "\"name\": \"billing\", \"defaultStereotypes\": [\"audited\"]");
        repo.Replace(".maquettiste/model/databases/main/tables/notes.json", "  \"primaryKey\": {",
            $"  \"excludes\": [\"{createdAt}\", \"{createdIndex}\"],\n  \"primaryKey\": {{");
        await using var session = await McpSession.StartAsync(repo, ct);

        var view = (await session.OkAsync("get_database_view", new { id = mainDatabase }))["view"]!;

        JsonNode Table(string name) => view["tables"]!.AsArray().Single(t => (string)t!["name"]! == name)!;
        var products = Table("products");
        var created = products["columns"]!.AsArray().Single(c => (string)c!["name"]! == "created_at")!;
        Assert.Equal((string)products["key"]! + "." + createdAt, (string)created["key"]!);
        Assert.Equal($$"""{"source":"schema-default","stereotype":"audited","memberId":"{{createdAt}}","redefined":[]}""", created["memberOrigin"]!.ToJsonString());
        Assert.All(products["columns"]!.AsArray().Where(c => (string)c!["name"]! is not ("created_at" or "updated_at")), c => Assert.Null(c!["memberOrigin"]));
        Assert.Null(products["primaryKey"]!["memberOrigin"]);
        var index = products["indexes"]!.AsArray().Single(i => i!["memberOrigin"] is not null)!;
        Assert.Equal(createdIndex, (string)index["memberOrigin"]!["memberId"]!);
        // A synthesized table without an overlay carries the stereotype its schema default gives it.
        Assert.Equal(["audited"], products["stereotypes"]!.AsArray().Select(s => (string)s!));
        var notesTable = view["tables"]!.AsArray().Single(t => (string)t!["key"]! == notes)!;
        Assert.Null(notesTable["columns"]!.AsArray().Single(c => (string)c!["name"]! == "created_at")!["memberOrigin"]);
        Assert.Equal(notes + "." + updatedAt, (string)notesTable["columns"]!.AsArray().Single(c => (string)c!["name"]! == "updated_at")!["key"]!);

        // The resolved model's table records carry the same origins.
        var records = await session.OkAsync("get_resolved_model", new { scope = "tables", database = mainDatabase, limit = 1000 });
        var record = records["items"]!.AsArray().Single(r => (string)r!["id"]! == (string)products["key"]!)!;
        Assert.True(JsonNode.DeepEquals(products, record["table"]));
    }

    [Fact]
    public async Task Get_ledger_ddl_returns_the_contract_version_schema_and_ddl()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        await using var session = await McpSession.StartAsync(repo, ct);

        // The only PostgreSQL database and the default schema; grant is null unless a role is asked for.
        var answer = (await session.OkAsync("get_ledger_ddl")).AsObject();
        Assert.Equal(1, (int)answer["contractVersion"]!);
        Assert.Equal(("main", "postgresql", "migration_ledger"), ((string)answer["database"]!, (string)answer["dialect"]!, (string)answer["schema"]!));
        Assert.Equal(Engine.Ledger.LedgerContract.Ddl(Engine.Model.Dialect.PostgreSql, "migration_ledger"), (string)answer["ddl"]!);
        Assert.True(answer.ContainsKey("grant"));
        Assert.Null(answer["grant"]);

        var granted = await session.OkAsync("get_ledger_ddl", new { database = "01J92P0V1QRN2181XM2ZWE02W4", grantRead = "reporting" });
        Assert.Equal(Engine.Ledger.LedgerContract.ReadGrant(Engine.Model.Dialect.PostgreSql, "migration_ledger", "reporting"), (string)granted["grant"]!);

        Assert.Equal("bad-request", (await session.ErrorAsync("get_ledger_ddl", new { database = "nowhere" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("get_ledger_ddl", new { grantRead = "" })).Code);
    }

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
        Assert.Equal(8, (await session.OkAsync("get_model_index", new { kind = "entity" })).AsArray().Count);
        // The name filter matches Customer, CustomerNote and the two queries whose names say customer.
        Assert.Equal(new[] { McpSession.Customer, "01K6BND0000000000000000020", "01K6QRY0000000000000000003", "01K6QRY0000000000000000001" }.Order(StringComparer.Ordinal),
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

        var families = await session.OkAsync("list_validation_rules");
        Assert.Equal("MQ10xx", (string)families["families"]![0]!["family"]!);
        Assert.Equal("MQ1001", (string)families["families"]![0]!["first"]!);
        Assert.True((int)families["total"]! > 200);
        var rules = (await session.OkAsync("list_validation_rules", new { limit = 1 }))["items"]!.AsArray();
        Assert.Equal("MQ1001", (string)rules[0]!["id"]!);
        Assert.False((bool)rules[0]!["canBeOff"]!);
        Assert.Equal("MQ72xx", (string)Assert.Single((await session.OkAsync("list_validation_rules", new { ids = new[] { "MQ7204" } }))["items"]!.AsArray())!["family"]!);

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

    /// <summary>get_schema declared-rules (next-phase-2-design.md section 5.6): the rule declaration file's schema, as schemas/v1 holds it.</summary>
    [Fact]
    public async Task Get_schema_for_declared_rules_returns_the_schema_file()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await McpSession.StartAsync(ct: ct);

        var schema = await session.OkAsync("get_schema", new { kind = "declared-rules" });

        Assert.Equal(("declared-rules", "declared-rules.json"), ((string)schema["name"]!, (string)schema["file"]!));
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "schemas", "v1", "declared-rules.json")));
        Assert.True(JsonNode.DeepEquals(file, schema["schema"]), "get_schema declared-rules differs from schemas/v1/declared-rules.json");
    }

    /// <summary>get_schema custom:&lt;type&gt; (next-phase-2-design.md section 4.2): custom.json with the declaration and the compiled content
    /// schema, member ids injected.</summary>
    [Fact]
    public async Task Get_schema_for_a_custom_kind_returns_the_declaration_and_content_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/extensions/kinds/message.json", """
            { "type": "message", "properties": { "parts": { "type": "array", "x-member": true,
              "items": { "type": "object", "properties": { "name": { "type": "string" }, "text": { "type": "string" } } } } }, "required": ["parts"] }
            """);
        await using var session = await McpSession.StartAsync(repo, ct);

        var schema = await session.OkAsync("get_schema", new { kind = "custom:message" });

        Assert.Equal(("custom:message", "custom.json"), ((string)schema["name"]!, (string)schema["file"]!));
        Assert.Equal(("message", ".maquettiste/extensions/kinds/message.json"), ((string)schema["customKind"]!["type"]!, (string)schema["customKind"]!["path"]!));
        var content = schema["contentSchema"]!;
        Assert.False((bool)content["additionalProperties"]!);
        Assert.Equal(["parts"], content["required"]!.AsArray().Select(n => (string?)n));
        var item = content["properties"]!["parts"]!["items"]!;
        Assert.Contains("id", item["required"]!.AsArray().Select(n => (string?)n));
        Assert.NotNull(item["properties"]!["id"]);
        var missing = await session.ErrorAsync("get_schema", new { kind = "custom:flag" });
        Assert.Equal("not-found", missing.Code);
        Assert.Contains("custom:message", missing.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Custom element rows carry type and memberCount; a kind filter takes custom and custom:&lt;type&gt;, and kind counts list one
    /// custom:&lt;type&gt; row per custom kind (next-phase-2-design.md section 4.2).</summary>
    [Fact]
    public async Task Model_index_and_kinds_present_custom_kinds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/extensions/kinds/setting.json", """{ "type": "setting", "displayName": "Setting", "properties": { "unit": { "type": "string" }, "value": { "type": "integer" } } }""");
        repo.Write(".maquettiste/extensions/kinds/flag.json", """{ "type": "flag", "properties": { "on": { "type": "boolean" } } }""");
        repo.Write(".maquettiste/model/custom/setting/due-days.json", """
            { "kind": "custom", "id": "01JR6CK000000000000000000A", "name": "due_days", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "type": "setting",
              "content": { "unit": "days", "value": 30 } }
            """);
        repo.Write(".maquettiste/model/custom/flag/strict.json", """{ "kind": "custom", "id": "01JR6CK000000000000000000B", "name": "strict", "type": "flag", "content": { "on": true } }""");
        await using var session = await McpSession.StartAsync(repo, ct);

        var row = Assert.Single((await session.OkAsync("get_model_index", new { kind = "custom:setting" })).AsArray())!;
        Assert.Equal(("due_days", "custom", "setting", 0), ((string)row["name"]!, (string)row["kind"]!, (string)row["type"]!, (int)row["memberCount"]!));
        Assert.Equal(2, (await session.OkAsync("get_model_index", new { kind = "custom" })).AsArray().Count);
        var page = await session.OkAsync("get_elements", new { kind = "custom:flag" });
        Assert.Equal("01JR6CK000000000000000000B", (string)Assert.Single(page["items"]!.AsArray())!["id"]!);
        var kinds = (await session.OkAsync("get_model_kinds"))["kinds"]!.AsArray().Select(k => ((string)k!["kind"]!, (int)k["count"]!)).ToList();
        Assert.Contains(("custom:flag", 1), kinds);
        Assert.Contains(("custom:setting", 1), kinds);
        Assert.DoesNotContain(kinds, k => k.Item1 == "custom");
    }

    [Fact]
    public async Task Compare_snapshots_takes_a_folder_and_answers_by_package()
    {
        // next-phase-2-design.md section 6.2: maquettiste mcp allows folders as sides; the answer is always grouped by package.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        using var copy = new TempRepo();
        CliRepo.CopyTree(repo.ModelRoot, copy.ModelRoot);
        await ModelCommandTests.ReviewChangeAsync(repo);
        await using var session = await McpSession.StartAsync(repo, ct);

        var comparison = await session.OkAsync("compare_snapshots", new { from = copy.RepoRoot, fieldChanges = true });

        Assert.Equal(copy.RepoRoot, (string)comparison["from"]!);
        Assert.False((bool)comparison["packsCompared"]!);
        Assert.Equal(["Billing", "migrations of main"], comparison["packages"]!.AsArray().Select(p => (string)p!["name"]!));
        Assert.Equal(["entity", "table", "migration"], comparison["elements"]!.AsArray().Select(e => (string)e!["kind"]!));
        Assert.Equal(McpSession.BillingPackage, (string)comparison["elements"]![1]!["package"]!);
        var field = comparison["elements"]![0]!["fields"]![0]!;
        Assert.Equal(("attribute", "code", "/name"), ((string?)field["subKind"], (string?)field["subName"], (string?)field["subPointer"]));
        Assert.Equal("bad-request", (await session.ErrorAsync("compare_snapshots", new { from = "./no-such-folder" })).Code);
    }

    [Fact]
    public async Task Compare_snapshots_answers_the_markdown_model_diff_prints()
    {
        // The acceptance line of section 6.2: the MCP answer and the command line print the same review, byte for byte.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        var id = await ModelCommandTests.SnapshotAsync(repo, "before-review");
        Assert.StartsWith("before-review-", id, StringComparison.Ordinal);
        await ModelCommandTests.ReviewChangeAsync(repo);
        var printed = await repo.RunAsync("model", "diff", id, "--format", "markdown");
        Assert.True(printed.ExitCode == 0, printed.Error);
        await using var session = await McpSession.StartAsync(repo, ct);

        var answer = await session.OkAsync("compare_snapshots", new { from = id, fieldChanges = true, format = "markdown" });

        Assert.Equal(printed.Out, (string)answer["markdown"]!);
        Assert.Null(answer["next"]); // one page: the diff holds three elements
        Assert.Contains("- added migration `rename column billing.invoices.number`", printed.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_expression_reads_one_element()
    {
        // next-phase-3-design.md section 3.1, decision 24: the bindings fixture's Product holds the check pricePositive, listed by its
        // binding to products. A broken entity elsewhere keeps the model from validating, so a preview that resolves the model answers
        // nothing; the expression preview reads the one element and the binding's table, and answers.
        var ct = TestContext.Current.CancellationToken;
        using var repo = ExpressionCommandTests.Bindings();
        await using var session = await McpSession.StartAsync(repo, ct);
        Assert.NotEqual(0, (int)(await session.OkAsync("validate"))["errors"]!);
        Assert.Null((await session.OkAsync("preview_query_sql", new { id = "01K6BFX0000000000000000021" }))["preview"]);

        // A stored check, named by its own id: printed, typed, lowered through the binding to products, and judged as a CHECK.
        var stored = (await session.OkAsync("preview_expression", new { element = ExpressionCommandTests.CheckId }))["preview"]!;
        Assert.Equal(("price > 0", "bool", false), ((string)stored["text"]!, (string)stored["type"]!, (bool)stored["nullable"]!));
        Assert.Equal("(row) => mq.passes(mq.gt(mq.dec(mq.at(row, \"price\"), 2), mq.dec(\"0\")))", (string)stored["javascript"]!);
        Assert.Equal(("price > 0", null, null), ((string?)stored["sql"], (string?)stored["sqlNote"], (string?)stored["answer"]));
        var atPointer = await session.OkAsync("preview_expression", new { element = ExpressionCommandTests.ProductId, at = "/checks/0", values = new { price = "0.00" } });
        Assert.False((bool)atPointer["preview"]!["answer"]!);
        Assert.Empty(atPointer["diagnostics"]!.AsArray());
        var unknown = await session.OkAsync("preview_expression", new { element = ExpressionCommandTests.ProductId, values = new { price = (string?)null } });
        Assert.True((bool)unknown["preview"]!["answer"]!); // unknown passes a check

        // Text at the table's check site reads the table's own columns, by their physical names.
        var text = (await session.OkAsync("preview_expression", new { element = "01K6BFX0000000000000000007", text = "length(sku) >= 3", site = "check" }))["preview"]!;
        Assert.Equal(("length(sku) >= 3", "char_length(sku) >= 3"), ((string)text["text"]!, (string)text["sql"]!));
        Assert.StartsWith("(row) => mq.passes(mq.ge(mq.length(mq.str(mq.at(row, \"sku\")))", (string)text["javascript"]!, StringComparison.Ordinal);

        // Text that does not parse previews nothing; a request that names nothing is refused.
        var bad = await session.OkAsync("preview_expression", new { element = ExpressionCommandTests.ProductId, text = "price >", site = "check" });
        Assert.Null(bad["preview"]);
        Assert.Equal("MQ3101", (string)Assert.Single(bad["diagnostics"]!.AsArray())!["rule"]!);
        Assert.Equal("not-found", (await session.ErrorAsync("preview_expression", new { element = "01K9EXP0000000000000000X99" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("preview_expression", new { element = ExpressionCommandTests.ProductId, at = "/checks/0", text = "price > 1", site = "check" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("preview_expression", new { element = ExpressionCommandTests.ProductId, text = "price > 1", site = "guard" })).Code);
    }
}

/// <summary>Saves, conflicts, creates, deletes and batches.</summary>
public sealed class McpWriteTests
{
    [Fact]
    public async Task Apply_batch_dry_run_answers_the_impact()
    {
        // next-phase-2-design.md section 6.1: the dry run answers impact beside today's members; a dry run that would not save has none.
        const string invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
        const string number = "01J92P0V0R3VSP7D5238DTNZX1";
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        await using var session = await McpSession.StartAsync(repo, ct);
        await session.OkAsync("generate");
        var loaded = await session.OkAsync("get_element", new { id = invoice });
        var element = loaded["json"]!.DeepClone().AsObject();
        element["attributes"]!.AsArray().Single(a => (string?)a!["id"] == number)!["name"] = "code";
        var operations = new[] { new { op = "update", id = invoice, expectedHash = (string)loaded["hash"]!, element } };
        var before = repo.Tree(".maquettiste");

        var preview = await session.OkAsync("apply_batch", new { operations, dryRun = true });

        Assert.Equal(before, repo.Tree(".maquettiste"));
        var impact = preview["impact"]!;
        var referrers = impact["referrers"]!.AsArray();
        Assert.Contains(referrers, r => (string)r!["fromName"]! == "InvoicesByCustomer" && (string)r["target"]! == number && (string)r["change"]! == "changed");
        Assert.Equal(referrers.Count, (int)impact["referrersTotal"]!);
        Assert.Equal("main", (string)impact["databases"]![0]!["name"]!);
        var packs = impact["packs"]!.AsArray();
        Assert.All(packs, p => Assert.Equal("known", (string)p!["state"]!));
        var unit = packs.SelectMany(p => p!["units"]!.AsArray()).Single(u => (string)u!["key"]! == "classes/entity:" + invoice)!;
        Assert.Equal("inputs", (string)unit["change"]!);
        Assert.False(unit.AsObject().ContainsKey("filtered"));

        var conflict = await session.ErrorAsync("apply_batch", new { operations = new[] { operations[0] with { expectedHash = new string('0', 64) } }, dryRun = true });
        Assert.Equal("conflict", conflict.Code);
        Assert.False(conflict.Json.AsObject().ContainsKey("impact"));
    }

    [Fact]
    public async Task Apply_batch_takes_a_rename_code_operation()
    {
        // next-phase-2-design.md section 3.5 (D52): a refactor is an operation of the batch contract; its dry run and its apply answer the
        // usage on the type's item, and a refusal is MQ7020.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        await using var session = await McpSession.StartAsync(repo, ct);
        var created = await session.OkAsync("apply_batch", new { operations = ModelApplyTests.PaymentTerms });
        Assert.Equal("saved", (string)created["outcome"]!);
        var type = (string)created["ids"]!["names"]!["$term"]!;
        var rename = new[] { new { op = "rename-code", id = "@reference-type:PaymentTerm", code = "net30", to = "net-30" } };
        var before = repo.Tree(".maquettiste");

        var preview = await session.OkAsync("apply_batch", new { operations = rename, dryRun = true });

        Assert.Equal("saved", (string)preview["outcome"]!);
        Assert.Equal(before, repo.Tree(".maquettiste"));
        var item = preview["items"]!.AsArray().Single(i => i!["usage"] is not null)!;
        Assert.Equal((type, 1), ((string)item["id"]!, (int)item["usage"]!["useCount"]!));

        var applied = await session.OkAsync("apply_batch", new { operations = rename });
        Assert.Equal("saved", (string)applied["outcome"]!);
        Assert.Contains(applied["items"]!.AsArray(), i => i!["usage"] is not null);
        var read = await session.OkAsync("get_element", new { id = type });
        var former = Assert.Single(read["json"]!["formerCodes"]!.AsArray())!;
        Assert.Equal("net30", (string)former["code"]!);

        // The old code never comes back: renaming another code to it is refused, and nothing is written.
        before = repo.Tree(".maquettiste");
        var refused = await session.ErrorAsync("apply_batch", new { operations = new[] { new { op = "rename-code", id = type, code = "net60", to = "net30" } } });
        Assert.Equal("invalid", refused.Code);
        Assert.Contains("MQ7020", refused.Json["items"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before, repo.Tree(".maquettiste"));
    }

    [Fact]
    public async Task Apply_batch_resync_binding_is_one_batch()
    {
        // next-phase-2-design.md section 3.3 (D35): a re-sync of several entities is one batch, all or nothing; preview_materialize
        // shows the same plan with keepNames.
        var ct = TestContext.Current.CancellationToken;
        using var repo = CliRepo.Billing();
        await using var session = await McpSession.StartAsync(repo, ct);
        const string main = "01J92P0V1QRN2181XM2ZWE02W4", invoiceNote = "01K6BND0000000000000000010", customerNote = "01K6BND0000000000000000020";
        var both = new[] { new { op = "resync-binding", database = main, entities = new[] { invoiceNote, customerNote } } };
        var recorded = await session.OkAsync("apply_batch", new { operations = both });
        Assert.Equal("saved", (string)recorded["outcome"]!);
        repo.Replace(".maquettiste/model/databases/main/tables/notes.json", "\"name\": \"body\"", "\"name\": \"note_body\"");

        var preview = await session.OkAsync("preview_materialize", new { database = main, op = "resync-binding", entities = new[] { invoiceNote, customerNote }, keepNames = true });
        Assert.True((bool)preview["valid"]!);
        Assert.Equal([invoiceNote, customerNote], preview["updates"]!.AsArray().Select(u => (string)u!["id"]!).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(preview["notes"]!.AsArray(), n => ((string)n!).Contains("is renamed", StringComparison.Ordinal));
        Assert.Equal("bad-request", (await session.ErrorAsync("preview_materialize", new { database = main, op = "resync-binding" })).Code);

        // Both entities follow the column in one write: each attribute that followed it is renamed and keeps its id.
        var applied = await session.OkAsync("apply_batch", new { operations = both });
        Assert.Equal("saved", (string)applied["outcome"]!);
        foreach (var (path, attribute) in new[] { (".maquettiste/model/entities/invoice-note.json", "01K6BND0000000000000000013"), (".maquettiste/model/entities/customer-note.json", "01K6BND0000000000000000023") })
        {
            var entity = JsonNode.Parse(repo.Read(path))!;
            Assert.Equal("noteBody", (string)entity["attributes"]!.AsArray().Single(a => (string)a!["id"]! == attribute)!["name"]!);
        }

        Assert.Empty((await session.OkAsync("get_binding_drift"))["bindings"]!.AsArray());

        // One entity the operation refuses (no binding to the database) refuses the batch: nothing is written.
        var before = repo.Tree(".maquettiste");
        var refused = await session.ErrorAsync("apply_batch", new { operations = new[] { new { op = "resync-binding", database = main, entities = new[] { invoiceNote, McpSession.Customer } } } });
        Assert.Equal("invalid", refused.Code);
        Assert.Contains("MQ4089", refused.Json["items"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(before, repo.Tree(".maquettiste"));
    }

    [Fact]
    public async Task A_rename_through_save_element_writes_one_fact_file_with_no_prompt()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var loaded = await session.OkAsync("get_element", new { id = McpSession.Customer });
        var renamed = loaded["json"]!.DeepClone();
        renamed["attributes"]![3]!["name"] = "memberSince";

        // The save goes through as any save: the answer lists the fact its write recorded, in the same atomic set.
        var saved = await session.OkAsync("save_element", new { id = McpSession.Customer, element = renamed, expectedHash = (string)loaded["hash"]! });

        Assert.Equal("saved", (string)saved["outcome"]!);
        Assert.Empty(saved["diagnostics"]!.AsArray());
        var fact = Assert.Single(saved["facts"]!.AsArray())!;
        Assert.Equal(("rename", "complete", "rename column billing.customers.customer_since to member_since"),
            ((string)fact["fact"]!, (string)fact["state"]!, (string)fact["summary"]!));
        Assert.Empty(fact["missing"]!.AsArray());
        var path = (string)fact["path"]!;
        Assert.StartsWith(".maquettiste/model/databases/main/migrations/open/", path, StringComparison.Ordinal);
        Assert.Equal([Path.GetFileName(path)], Directory.EnumerateFiles(Path.GetDirectoryName(session.Repo.PathOf(path))!).Select(Path.GetFileName));
        Assert.Contains(saved["changes"]!["changed"]!.AsArray(), c => (string)c!["id"]! == (string)fact["id"]!);

        // The fact reads back like any element.
        var read = await session.OkAsync("get_element", new { id = (string)fact["id"]! });
        Assert.Equal(("migration", "rename"), ((string)read["json"]!["kind"]!, (string)read["json"]!["fact"]!));
    }

    [Fact]
    public async Task Names_to_ids_previews_then_applies_through_apply_batch()
    {
        using var repo = CliRepo.Billing();
        ModelApplyTests.TypeTheCustomersOwner(repo);
        await using var session = await McpSession.StartAsync(repo, TestContext.Current.CancellationToken);
        var operations = new[] { new { op = "names-to-ids" } };
        var before = session.Repo.Tree(".maquettiste");

        var preview = await session.OkAsync("apply_batch", new { operations, dryRun = true });

        Assert.Equal("saved", (string)preview["outcome"]!);
        var file = Assert.Single(preview["files"]!.AsArray())!;
        Assert.Equal((McpSession.CustomerPath, "changed"), ((string)file["path"]!, (string)file["action"]!));
        Assert.Contains("+    \"owner\": \"" + ModelApplyTests.Invoice + "\"", (string)file["diff"]!, StringComparison.Ordinal);
        Assert.Contains(preview["diagnosticsRemoved"]!.AsArray(), d => (string)d!["rule"]! == "MQ2010");
        Assert.Equal(before, session.Repo.Tree(".maquettiste"));

        var applied = await session.OkAsync("apply_batch", new { operations });

        Assert.Equal("saved", (string)applied["outcome"]!);
        var customer = await session.OkAsync("get_element", new { id = McpSession.Customer });
        Assert.Equal(ModelApplyTests.Invoice, (string)customer["json"]!["properties"]!["owner"]!);
        var references = (await session.OkAsync("get_references", new { id = ModelApplyTests.Invoice })).AsArray();
        Assert.Contains(references, r => (string)r!["jsonPointer"]! == "/properties/owner" && (string)r["schema"]! == "extension:links");

        // Nothing is left to convert: a second run writes nothing.
        var again = await session.OkAsync("apply_batch", new { operations });
        Assert.Equal("saved", (string)again["outcome"]!);
        Assert.Empty(again["items"]!.AsArray());
    }

    [Fact]
    public async Task Apply_batch_folds_into_a_stereotype()
    {
        // next-phase-2-design.md section 3.1 (D12): fold-into-stereotype through apply_batch. A refusal is MQ4077 per table and writes
        // nothing; the dry run lists the files and the rekey; the apply records that rekey, and the database view shows the inherited copy.
        using var repo = CliRepo.Billing();
        ModelApplyTests.AuditColumns(repo);
        await using var session = await McpSession.StartAsync(repo, TestContext.Current.CancellationToken);
        const string mainDatabase = "01J92P0V1QRN2181XM2ZWE02W4";
        const string notesPath = ".maquettiste/model/databases/main/tables/notes.json";
        object Fold(bool excludeMissing) => new[]
        {
            new { op = "fold-into-stereotype", id = "@stereotype:audited", tables = new[] { ModelApplyTests.Notes }, excludeMissing },
        };
        var before = session.Repo.Tree(".maquettiste");

        var refused = await session.ErrorAsync("apply_batch", new { operations = Fold(excludeMissing: false) });

        Assert.Equal("invalid", refused.Code);
        var refusal = Assert.Single(refused.Json["items"]![0]!["diagnostics"]!.AsArray())!;
        Assert.Equal(("MQ4077", "/operations/0"), ((string)refusal["rule"]!, (string)refusal["jsonPointer"]!));
        Assert.Contains("column 'updated_at'", (string)refusal["message"]!, StringComparison.Ordinal);
        Assert.Equal(before, session.Repo.Tree(".maquettiste"));

        var preview = await session.OkAsync("apply_batch", new { operations = Fold(excludeMissing: true), dryRun = true });

        Assert.Equal("saved", (string)preview["outcome"]!);
        var planned = Assert.Single(preview["facts"]!.AsArray())!;
        Assert.Equal(("rekey", "rekey 1 column, 1 index"), ((string)planned["fact"]!, (string)planned["summary"]!));
        Assert.Equal(
            [(string)planned["path"]!, notesPath, ".maquettiste/model/entities/customer-note.json", ".maquettiste/model/entities/invoice-note.json"],
            preview["files"]!.AsArray().Select(f => (string)f!["path"]!).Order(StringComparer.Ordinal));
        Assert.Equal(before, session.Repo.Tree(".maquettiste"));

        var applied = await session.OkAsync("apply_batch", new { operations = Fold(excludeMissing: true) });

        Assert.Equal("saved", (string)applied["outcome"]!);
        var fact = Assert.Single(applied["facts"]!.AsArray())!;
        Assert.Equal(("rekey", "complete", mainDatabase), ((string)fact["fact"]!, (string)fact["state"]!, (string)fact["database"]!));
        var rekey = (await session.OkAsync("get_element", new { id = (string)fact["id"]! }))["json"]!;
        var keyed = ModelApplyTests.Notes + "." + ModelApplyTests.AuditCreatedAt;
        Assert.Equal(
            [("column", "01K6BND0000000000000000006", keyed), ("index", ModelApplyTests.NotesCreatedIndex, ModelApplyTests.Notes + "." + ModelApplyTests.AuditCreatedIndex)],
            rekey["keys"]!.AsArray().Select(k => ((string)k!["kind"]!, (string)k["from"]!, (string)k["to"]!)));
        var notes = (await session.OkAsync("get_element", new { id = ModelApplyTests.Notes }))["json"]!;
        Assert.DoesNotContain(notes["columns"]!.AsArray(), c => (string)c!["name"]! == "created_at");
        Assert.Equal([ModelApplyTests.AuditUpdatedAt], notes["excludes"]!.AsArray().Select(k => (string)k!));

        // An agent reads the keyed id from the view, and get_references on the stereotype's member lists the inherited copy's uses.
        var view = (await session.OkAsync("get_database_view", new { id = mainDatabase }))["view"]!;
        var table = view["tables"]!.AsArray().Single(t => (string)t!["key"]! == ModelApplyTests.Notes)!;
        var created = table["columns"]!.AsArray().Single(c => (string)c!["name"]! == "created_at")!;
        Assert.Equal((keyed, "stereotype"), ((string)created["key"]!, (string)created["memberOrigin"]!["source"]!));
        var references = (await session.OkAsync("get_references", new { id = ModelApplyTests.AuditCreatedAt })).AsArray();
        Assert.Contains(references, r => (string)r!["fromElementId"]! == "01K6BND0000000000000000010");
        Assert.DoesNotContain((await session.OkAsync("validate"))["diagnostics"]!.AsArray(), d => (string)d!["severity"]! == "error");
    }

    [Fact]
    public async Task Two_renames_fold_into_one_fact_that_list_migrations_shows()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var baseline = (string)(await session.OkAsync("create_snapshot", new { name = "Base" }))["id"]!;
        var facts = new List<string>();
        foreach (var name in new[] { "memberSince", "joinedOn" })
        {
            var loaded = await session.OkAsync("get_element", new { id = McpSession.Customer });
            var renamed = loaded["json"]!.DeepClone();
            renamed["attributes"]![3]!["name"] = name;
            var saved = await session.OkAsync("save_element", new { id = McpSession.Customer, element = renamed, expectedHash = (string)loaded["hash"]! });
            facts.Add((string)Assert.Single(saved["facts"]!.AsArray())!["id"]!);
        }

        // list_migrations pages the facts in applied order.
        var page = await session.OkAsync("list_migrations", new { limit = 1 });
        Assert.Equal(2, (int)page["total"]!);
        Assert.Equal(facts[0], (string)page["facts"]![0]!["id"]!);
        var rest = await session.OkAsync("list_migrations", new { cursor = (string)page["next"]! });
        Assert.Equal(facts[1], (string)Assert.Single(rest["facts"]!.AsArray())!["id"]!);
        Assert.Null(rest["next"]);
        Assert.Equal("bad-request", (await session.ErrorAsync("list_migrations", new { database = "nowhere" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("list_migrations", new { cursor = "nope" })).Code);

        // fold_migrations against the snapshot: a dry run lists the files, then the fold keeps the last fact.
        var dry = await session.OkAsync("fold_migrations", new { against = baseline, dryRun = true });
        Assert.Equal(2, dry["files"]!.AsArray().Count);
        Assert.Equal(2, (int)(await session.OkAsync("list_migrations"))["total"]!);
        var folded = await session.OkAsync("fold_migrations", new { against = baseline });
        var join = Assert.Single(folded["joined"]!.AsArray())!;
        Assert.Equal((facts[1], facts[0]), ((string)join["kept"]!, (string)Assert.Single(join["removed"]!.AsArray())!));
        var after = await session.OkAsync("list_migrations");
        Assert.Equal("rename column billing.customers.customer_since to joined_on", (string)Assert.Single(after["facts"]!.AsArray())!["summary"]!);
        Assert.Equal("bad-request", (await session.ErrorAsync("fold_migrations", new { against = "no-such-snapshot" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("fold_migrations", new { database = "nowhere" })).Code);
    }

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
        // Nothing changed since the apply: the answer lists no file left alone as identical or not rendered, it only counts them;
        // get_plan lists every file.
        var kinds = second["plan"]!["changes"]!.AsArray().Select(c => (string)c!["kind"]!).ToList();
        Assert.DoesNotContain("unchanged", kinds);
        Assert.DoesNotContain("not-rendered", kinds);
        var notRendered = (int)second["plan"]!["counts"]!["not-rendered"]!;
        Assert.True(notRendered > 0);
        var stored = await session.OkAsync("get_plan", new { planId = secondId });
        Assert.Equal(notRendered, stored["changes"]!.AsArray().Count(c => (string)c!["kind"]! == "not-rendered"));
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
    /// A plan against a model snapshot stores the snapshot id, and its apply opens it again; a value that names neither a snapshot nor
    /// a folder is a bad request.
    /// </summary>
    [Fact]
    public async Task Plan_against_a_snapshot_stores_it_and_a_value_that_names_nothing_is_a_bad_request()
    {
        await using var session = await McpSession.StartAsync(ct: TestContext.Current.CancellationToken);
        var baseline = (string)(await session.OkAsync("create_snapshot", new { name = "Base" }))["id"]!;
        var planned = await session.OkAsync("plan", new { against = baseline });
        Assert.Equal(baseline, (string)planned["plan"]!["request"]!["against"]!);
        var applied = await session.OkAsync("apply_plan", new { planId = (string)planned["plan"]!["id"]! });
        Assert.Equal("succeeded", (string)applied["outcome"]!);
        Assert.False(File.Exists(session.Repo.PathOf(".maquettiste/snapshots/main.json")));
        Assert.Equal("bad-request", (await session.ErrorAsync("plan", new { against = "no-such-snapshot" })).Code);
        Assert.Equal("bad-request", (await session.ErrorAsync("plan", new { against = "./no-such-folder" })).Code);
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
