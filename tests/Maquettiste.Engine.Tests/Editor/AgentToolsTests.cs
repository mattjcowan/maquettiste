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
        Assert.Equal(25, AgentTools.All.Count);
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
