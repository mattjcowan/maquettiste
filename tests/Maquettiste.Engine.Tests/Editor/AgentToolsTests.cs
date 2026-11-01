using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// The read tools <c>maquettiste mcp</c> and the editor's assistant share (erratum E44), the batch dry run behind
/// <c>propose_changes</c>, and the assistant's prompt pieces.
/// </summary>
public sealed partial class AgentToolsTests
{
    private static CancellationToken Ct => EditorRepo.Ct;

    private static AgentTools Catalog(EditorRepo r) => new(r.Store, r.Service, new AgentToolsOptions { RepoRoot = r.Repo.RepoRoot });

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex HostToolName();

    [Fact]
    public async Task Every_definition_has_a_handler_an_object_schema_and_a_name_the_host_accepts()
    {
        await using var r = EditorRepo.Create(packs: false);
        var catalog = Catalog(r);
        Assert.Equal(27, AgentTools.All.Count);
        Assert.Equal(AgentTools.All.Select(t => t.Name).Order(StringComparer.Ordinal), AgentTools.All.Select(t => t.Name));
        foreach (var tool in AgentTools.All.Append(AssistantProposals.Tool))
        {
            Assert.Matches(HostToolName(), tool.Name);
            Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }

        foreach (var tool in AgentTools.All)
        {
            var answer = await catalog.CallAsync(tool.Name, null, Ct);
            Assert.DoesNotContain("\"code\":\"not-found\",\"status\":404,\"title\":\"No tool", answer.Text, StringComparison.Ordinal);
        }

        Assert.True((await catalog.CallAsync("save_element", null, Ct)).IsError);
    }

    [Fact]
    public async Task Calls_answer_the_api_bodies_and_problems()
    {
        await using var r = EditorRepo.Create(packs: false);
        var catalog = Catalog(r);
        var element = await catalog.CallAsync("get_element", JsonSerializer.SerializeToElement(new { id = EditorRepo.InvoiceId }), Ct);
        Assert.False(element.IsError);
        Assert.Equal("Invoice", (string?)JsonNode.Parse(element.Text)!["element"]!["name"]);

        var wrongType = await catalog.CallAsync("get_model_index", JsonSerializer.SerializeToElement(new { kind = 5 }), Ct);
        Assert.True(wrongType.IsError);
        Assert.Equal("kind must be a string, not a number.", (string?)JsonNode.Parse(wrongType.Text)!["detail"]);
        var missing = await catalog.CallAsync("get_element", JsonSerializer.SerializeToElement(new { }), Ct);
        Assert.Equal("bad-request", (string?)JsonNode.Parse(missing.Text)!["code"]);
        var notObject = await catalog.CallAsync("get_element", JsonSerializer.SerializeToElement("x"), Ct);
        Assert.True(notObject.IsError);
    }

    [Fact]
    public async Task Get_project_answers_a_summary_and_sections_on_request()
    {
        await using var r = EditorRepo.Create();
        var catalog = Catalog(r);

        var summary = JsonNode.Parse((await catalog.CallAsync("get_project", null, Ct)).Text)!.AsObject();

        Assert.Equal("billing", (string?)summary["name"]);
        Assert.Equal(["extensions", "packs", "settings"], summary["sections"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["csharp-dapper", "sql-ddl"], summary["packs"]!.AsArray().Select(p => (string?)p!["name"]).Order(StringComparer.Ordinal));
        var ddl = summary["packs"]!.AsArray().Single(p => (string?)p!["name"] == "sql-ddl")!;
        Assert.True((int)ddl["units"]! > 0);
        Assert.False(string.IsNullOrEmpty((string?)ddl["version"]));
        Assert.Equal(0, (int)summary["packDiagnosticCounts"]!["errors"]!);
        Assert.Equal(["retention"], summary["extensions"]!.AsArray().Select(n => (string?)n));
        Assert.False(string.IsNullOrEmpty((string?)summary["settingsHash"]));
        Assert.Single(summary["databases"]!.AsArray());
        foreach (var full in new[] { "settings", "packManifests", "packDiagnostics", "extensionSchemas", "git" })
            Assert.False(summary.ContainsKey(full), full);

        var settings = JsonNode.Parse((await catalog.CallAsync("get_project", JsonSerializer.SerializeToElement(new { sections = new[] { "settings" } }), Ct)).Text)!.AsObject();
        Assert.Equal("billing", (string?)settings["settings"]!["name"]);
        Assert.False(settings.ContainsKey("packManifests"));

        var all = JsonNode.Parse((await catalog.CallAsync("get_project", JsonSerializer.SerializeToElement(new { sections = new[] { "packs", "extensions", "settings" } }), Ct)).Text)!;
        Assert.Equal(2, all["packManifests"]!.AsArray().Count);
        Assert.NotNull(all["packManifests"]![0]!["units"]);
        Assert.NotNull(all["packDiagnostics"]);
        Assert.Equal("retention", (string?)Assert.Single(all["extensionSchemas"]!.AsArray())!["name"]);
        Assert.NotNull(all["settings"]);

        var unknown = await catalog.CallAsync("get_project", JsonSerializer.SerializeToElement(new { sections = new[] { "git" } }), Ct);
        Assert.True(unknown.IsError);
        Assert.Equal("bad-request", (string?)JsonNode.Parse(unknown.Text)!["code"]);
    }

    [Fact]
    public async Task List_validation_rules_summarizes_families_and_pages_rules()
    {
        await using var r = EditorRepo.Create(packs: false);
        var catalog = Catalog(r);
        async Task<JsonNode> CallAsync(object? args)
        {
            var answer = await catalog.CallAsync("list_validation_rules", args is null ? null : JsonSerializer.SerializeToElement(args), Ct);
            Assert.False(answer.IsError, answer.Text);
            return JsonNode.Parse(answer.Text)!;
        }

        // No argument: the families, in id order, with their counts and ranges; no description.
        var summary = await CallAsync(null);
        Assert.Equal(Diagnostics.RuleCatalog.All.Count, (int)summary["total"]!);
        var families = summary["families"]!.AsArray();
        Assert.Equal(Diagnostics.RuleCatalog.All.Count, families.Sum(f => (int)f!["rules"]!));
        var models = families[0]!;
        Assert.Equal(("MQ10xx", "Model files", "MQ1001"), ((string)models["family"]!, (string)models["familyLabel"]!, (string)models["first"]!));
        Assert.Equal("MQ1016", (string)models["last"]!);
        Assert.Contains(families, f => (string)f!["family"]! == "MQ41xx" && (string)f["familyLabel"]! == "Migrations");
        Assert.DoesNotContain("description", summary.ToJsonString(), StringComparison.Ordinal);

        // A family: its full entries, one page.
        var migrations = await CallAsync(new { family = "MQ41xx" });
        var items = migrations["items"]!.AsArray();
        Assert.All(items, i => Assert.Equal("MQ41xx", (string)i!["family"]!));
        Assert.Equal(items.Count, (int)families.Single(f => (string)f!["family"]! == "MQ41xx")!["rules"]!);
        Assert.Null(migrations["next"]);
        Assert.False(string.IsNullOrEmpty((string?)items[0]!["description"]));

        // Ids exactly, query by id or description ignoring case, AND together.
        var two = await CallAsync(new { ids = new[] { "MQ4101", "MQ2001", "MQ99" } });
        Assert.Equal(["MQ2001", "MQ4101"], two["items"]!.AsArray().Select(i => (string?)i!["id"]));
        var text = await CallAsync(new { query = "DANGLING" });
        Assert.Contains(text["items"]!.AsArray(), i => (string)i!["id"]! == "MQ2001");
        var both = await CallAsync(new { family = "MQ10xx", query = "json patch" });
        Assert.Equal(["MQ1016"], both["items"]!.AsArray().Select(i => (string?)i!["id"]));
        Assert.Empty((await CallAsync(new { family = "MQ20xx", ids = new[] { "MQ4101" } }))["items"]!.AsArray());

        // Pages in id order, keyed by rule id, until next is null.
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await CallAsync(new { family = "MQ10xx", limit = 5, cursor });
            seen.AddRange(page["items"]!.AsArray().Select(i => (string)i!["id"]!));
            cursor = (string?)page["next"];
        }
        while (cursor is not null);
        Assert.Equal(Diagnostics.RuleCatalog.All.Where(x => x.Id.StartsWith("MQ10", StringComparison.Ordinal)).Select(x => x.Id), seen);
        var first = await CallAsync(new { limit = 3 });
        Assert.Equal(["MQ1001", "MQ1002", "MQ1003"], first["items"]!.AsArray().Select(i => (string?)i!["id"]));

        var bad = await catalog.CallAsync("list_validation_rules", JsonSerializer.SerializeToElement(new { cursor = "nope" }), Ct);
        Assert.Equal("bad-request", (string?)JsonNode.Parse(bad.Text)!["code"]);
        var big = await catalog.CallAsync("list_validation_rules", JsonSerializer.SerializeToElement(new { limit = 1001 }), Ct);
        Assert.Equal("bad-request", (string?)JsonNode.Parse(big.Text)!["code"]);
    }

    [Fact]
    public async Task Tag_usage_summarizes_tags_and_pages_one_tags_elements()
    {
        await using var r = EditorRepo.Create(packs: false);
        var catalog = Catalog(r);
        async Task<JsonNode> CallAsync(object args)
        {
            var answer = await catalog.CallAsync("tag_usage", JsonSerializer.SerializeToElement(args), Ct);
            Assert.False(answer.IsError, answer.Text);
            return JsonNode.Parse(answer.Text)!;
        }

        var usage = (await r.Store.GetTagUsageAsync(null, Ct))!;

        // Without a tag: every tag in key order, without element ids, the whole list.
        var summary = await CallAsync(new { });
        Assert.Equal(usage.Vocabulary, (string?)summary["vocabulary"]);
        Assert.Equal(usage.Strict, (bool)summary["strict"]!);
        Assert.Null(summary["next"]);
        var tags = summary["tags"]!.AsArray();
        Assert.Equal(["billing", "pii"], tags.Select(t => (string?)t!["tag"]));
        Assert.All(tags, t => Assert.Null(t!["elements"]));
        var billing = usage.Tags.Single(t => t.Tag == "billing");
        Assert.Equal((billing.Declared, billing.Uses), ((bool)tags[0]!["declared"]!, (int)tags[0]!["uses"]!));
        Assert.Equal(billing.Examples, tags[0]!["examples"]!.AsArray().Select(e => (string)e!));

        // limit or cursor pages the tags.
        var firstTag = await CallAsync(new { limit = 1 });
        Assert.Equal("billing", (string?)Assert.Single(firstTag["tags"]!.AsArray())!["tag"]);
        var secondTag = await CallAsync(new { cursor = (string)firstTag["next"]! });
        Assert.Equal("pii", (string?)Assert.Single(secondTag["tags"]!.AsArray())!["tag"]);
        Assert.Null(secondTag["next"]);

        // With a tag: its element ids, paged in id order.
        var elements = new List<string>();
        var page = await CallAsync(new { tag = "billing", limit = 2 });
        Assert.Equal(("billing", true, billing.Uses), ((string)page["tag"]!, (bool)page["declared"]!, (int)page["uses"]!));
        Assert.Null(page["package"]);
        elements.AddRange(page["elements"]!.AsArray().Select(e => (string)e!));
        Assert.Equal(2, elements.Count);
        var rest = await CallAsync(new { tag = "billing", limit = 2, cursor = (string)page["next"]! });
        elements.AddRange(rest["elements"]!.AsArray().Select(e => (string)e!));
        Assert.Null(rest["next"]);
        Assert.Equal(billing.Elements.Order(StringComparer.Ordinal), elements);
        Assert.Contains(EditorRepo.InvoiceId, elements);

        var unknown = await catalog.CallAsync("tag_usage", JsonSerializer.SerializeToElement(new { tag = "nope" }), Ct);
        Assert.Equal("not-found", (string?)JsonNode.Parse(unknown.Text)!["code"]);
        var notDomain = await catalog.CallAsync("tag_usage", JsonSerializer.SerializeToElement(new { package = EditorRepo.InvoiceId }), Ct);
        Assert.Equal("not-found", (string?)JsonNode.Parse(notDomain.Text)!["code"]);
        var badCursor = await catalog.CallAsync("tag_usage", JsonSerializer.SerializeToElement(new { tag = "billing", cursor = "nope" }), Ct);
        Assert.Equal("bad-request", (string?)JsonNode.Parse(badCursor.Text)!["code"]);
    }

    [Fact]
    public async Task Get_schema_lists_stereotype_parameters()
    {
        await using var r = EditorRepo.Create(packs: false);
        var audited = (await r.Store.GetElementAsync("01J92P0V25BX5TMQ8N9F6AJE61", Ct))!;
        var json = JsonNode.Parse(audited.Json.GetRawText())!.AsObject();
        json["parameters"] = JsonNode.Parse("""{ "properties": { "retentionDays": { "type": "integer", "minimum": 1 } }, "required": ["retentionDays"] }""");
        var saved = await r.Store.SaveAsync(audited.Element.Id, Encoding.UTF8.GetBytes(json.ToJsonString()), audited.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var catalog = Catalog(r);

        var table = JsonNode.Parse((await catalog.CallAsync("get_schema", JsonSerializer.SerializeToElement(new { kind = "table" }), Ct)).Text)!;
        var package = JsonNode.Parse((await catalog.CallAsync("get_schema", JsonSerializer.SerializeToElement(new { kind = "package" }), Ct)).Text)!;
        var document = JsonNode.Parse((await catalog.CallAsync("get_schema", JsonSerializer.SerializeToElement(new { kind = "batch" }), Ct)).Text)!;

        // audited applies to entities, tables and columns: listed for a table, not for a package; a document has no stereotypes.
        var entry = Assert.Single(table["stereotypeParameters"]!.AsArray());
        Assert.Equal("audited", (string?)entry!["stereotype"]);
        Assert.Equal("integer", (string?)entry["parameters"]!["properties"]!["retentionDays"]!["type"]);
        Assert.Equal(["retentionDays"], entry["parameters"]!["required"]!.AsArray().Select(n => (string?)n));
        Assert.Empty(package["stereotypeParameters"]!.AsArray());
        Assert.Null(document["stereotypeParameters"]);
    }

    [Fact]
    public async Task A_batch_preview_writes_nothing_and_lists_the_files_with_their_text_before_and_after()
    {
        await using var r = EditorRepo.Create(packs: false);
        var before = r.Repo.ListFiles();
        var invoice = (await r.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!;
        var json = JsonNode.Parse(invoice.Json.GetRawText())!.AsObject();
        json["description"] = "Previewed.";
        var batch = r.Store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["operations"] = new JsonArray(
                new JsonObject { ["op"] = "update", ["id"] = EditorRepo.InvoiceId, ["expectedHash"] = invoice.Hash, ["element"] = json },
                new JsonObject
                {
                    ["op"] = "create",
                    ["element"] = new JsonObject { ["kind"] = "enum", ["name"] = "Previewed", ["package"] = EditorRepo.BillingPackageId, ["members"] = new JsonArray(new JsonObject { ["id"] = "01J92P0V0000000000000000B1", ["name"] = "One", ["value"] = 0 }) },
                }),
        }.ToJsonString())).Batch!;

        var preview = await r.Store.PreviewBatchAsync(batch, Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Equal(before, r.Repo.ListFiles());
        Assert.Equal(invoice.Hash, (await r.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!.Hash);
        var changed = Assert.Single(preview.Files, f => f.Action == "changed");
        Assert.Equal(EditorRepo.InvoiceId, changed.Id);
        Assert.Equal(invoice.Hash, changed.BeforeHash);
        Assert.Contains("Previewed.", changed.After, StringComparison.Ordinal);
        var created = Assert.Single(preview.Files, f => f.Action == "created");
        Assert.Equal(("enum", "Previewed"), (created.Kind, created.Name));
        Assert.Null(created.Before);

        var conflict = await r.Store.PreviewBatchAsync(r.Store.ParseBatch(Encoding.UTF8.GetBytes(
            $$"""{"operations":[{"op":"delete","id":"{{EditorRepo.InvoiceId}}","expectedHash":"{{new string('0', 64)}}"}]}""")).Batch!, Ct);
        Assert.Equal(SaveOutcome.Conflict, conflict.Outcome);
        Assert.Empty(conflict.Files);
    }

    [Fact]
    public async Task A_proposal_gets_ids_and_hashes_pinned_and_an_invalid_one_names_its_diagnostics()
    {
        await using var r = EditorRepo.Create(packs: false);
        var ids = new UlidIdGenerator();
        var args = JsonSerializer.SerializeToElement(new
        {
            summary = "Add an enum",
            operations = new object[]
            {
                new { op = "create", element = new { kind = "enum", name = "Pinned", package = EditorRepo.BillingPackageId, members = new[] { new { name = "One", value = 0 } } } },
            },
        });
        var (draft, error) = await AssistantProposals.PrepareAsync(r.Store, ids, args, Ct);
        Assert.True(error is null, error);
        var element = draft!.Operations[0]!["element"]!;
        Assert.NotNull(element["id"]);
        Assert.NotNull(element["members"]![0]!["id"]); // the sub-element id the dry run assigned is pinned
        Assert.Null(element["$schema"]);
        Assert.Contains("pending-review", AssistantProposals.Accepted("01J92P0V0000000000000000AA", draft), StringComparison.Ordinal);

        var (none, invalid) = await AssistantProposals.PrepareAsync(r.Store, ids, JsonSerializer.SerializeToElement(new { summary = "x", operations = new[] { new { op = "explode" } } }), Ct);
        Assert.Null(none);
        Assert.Contains("MQ1002", invalid, StringComparison.Ordinal);
        var (_, empty) = await AssistantProposals.PrepareAsync(r.Store, ids, JsonSerializer.SerializeToElement(new { summary = "", operations = Array.Empty<object>() }), Ct);
        Assert.Contains("summary is required", empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposal_by_name_is_pinned_to_ids()
    {
        await using var r = EditorRepo.Create(packs: false);
        var invoice = (await r.Store.GetElementAsync(EditorRepo.InvoiceId, Ct))!;
        var json = JsonNode.Parse(invoice.Json.GetRawText())!.AsObject();
        json["description"] = "Pinned by name.";
        json.Remove("$schema");
        var args = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["summary"] = "Add an enum and describe Invoice",
            ["operations"] = new JsonArray(
                new JsonObject
                {
                    ["op"] = "create",
                    ["as"] = "$kinds",
                    ["element"] = new JsonObject
                    {
                        ["kind"] = "enum", ["name"] = "PinnedKinds", ["package"] = "@package:Billing",
                        ["members"] = new JsonArray(new JsonObject { ["name"] = "One", ["value"] = 0 }),
                    },
                },
                new JsonObject { ["op"] = "update", ["id"] = "@entity:Invoice", ["element"] = json }),
        });

        var (draft, error) = await AssistantProposals.PrepareAsync(r.Store, new UlidIdGenerator(), args, Ct);

        Assert.True(error is null, error);
        var create = draft!.Operations[0]!.AsObject();
        Assert.False(create.ContainsKey("as"));
        var enumId = (string)create["id"]!;
        var element = create["element"]!;
        Assert.Equal(enumId, (string)element["id"]!);
        Assert.Equal(EditorRepo.BillingPackageId, (string)element["package"]!);
        Assert.NotNull(element["members"]![0]!["id"]);
        var created = Assert.Single(draft.Files, f => f.Action == "created");
        Assert.Equal(enumId, created.Id);
        Assert.StartsWith("--- a/", created.Diff, StringComparison.Ordinal);
        var update = draft.Operations[1]!.AsObject();
        Assert.Equal(EditorRepo.InvoiceId, (string)update["id"]!);
        Assert.Equal(invoice.Hash, (string)update["expectedHash"]!);
        Assert.Equal("Pinned by name.", (string)update["element"]!["description"]!);

        // Applying the pinned operations writes exactly what was previewed, with the ids the proposal shows.
        var applied = await r.Store.ApplyBatchAsync(r.Store.ParseBatch(Encoding.UTF8.GetBytes(new JsonObject { ["operations"] = draft.Operations.DeepClone() }.ToJsonString())).Batch!,
            ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, applied.Outcome);
        Assert.Empty(applied.Ids.Minted);
        Assert.Equal(created.After, r.Repo.ReadFile(created.Path).Replace("\r\n", "\n", StringComparison.Ordinal));

        // A target name that finds nothing is refused like a parse failure, with its diagnostic.
        var unknown = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["summary"] = "Delete nobody",
            ["operations"] = new JsonArray(new JsonObject { ["op"] = "delete", ["id"] = "@entity:Nobody" }),
        });
        var (none, refused) = await AssistantProposals.PrepareAsync(r.Store, new UlidIdGenerator(), unknown, Ct);
        Assert.Null(none);
        Assert.Contains("MQ1012", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void The_system_prompt_carries_the_conventions_and_the_house_rules_and_the_context_is_a_tagged_block()
    {
        var prompt = AssistantPrompt.System("billing", AgentConventions.Read(), "Name tables in snake_case.");
        Assert.Contains("\"billing\"", prompt, StringComparison.Ordinal);
        Assert.Contains("# Modeling with Maquettiste", prompt, StringComparison.Ordinal);
        Assert.Contains("propose_changes", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Name tables in snake_case.\n", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", prompt, StringComparison.Ordinal);

        Assert.Equal("Hi", AssistantPrompt.UserMessage("Hi", null));
        var message = AssistantPrompt.UserMessage("Hi", new AssistantContext("entities", EditorRepo.InvoiceId, "Invoice", "entity", ["A", "B"],
            new AssistantProblems(1, 0, 0, [new AssistantProblem("MQ3005", "No key.", EditorRepo.InvoiceId)])));
        Assert.Equal($"""
            Hi

            <editor-context>
            Workspace: entities
            Open element: Invoice (entity, {EditorRepo.InvoiceId})
            Selected ids: A, B
            Problems: 1 errors, 0 warnings, 0 infos
            - MQ3005 on {EditorRepo.InvoiceId}: No key.
            </editor-context>
            """.Replace("\r\n", "\n", StringComparison.Ordinal), message);
    }
}
