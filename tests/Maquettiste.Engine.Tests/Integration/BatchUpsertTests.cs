using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>The batch operation <c>upsert</c>: a script that re-runs against another source sends one batch, on a copy of the billing fixture.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class BatchUpsertTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<E2ERepo> CopyAsync()
    {
        var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/kinds/policy.json", """{ "type": "policy", "properties": { "text": { "type": "string" } } }""");
        await repo.RestartAsync();
        return repo;
    }

    private static string Upsert(string text, string id = "@custom:policy.keeps_lines", string name = "keeps_lines", string extra = "") => $$"""
        { "operations": [ { "op": "upsert", "id": "{{id}}"{{extra}},
            "element": { "kind": "custom", "type": "policy", "name": "{{name}}", "content": { "text": "{{text}}" } } } ] }
        """;

    private static async Task<BatchResult> ApplyAsync(E2ERepo repo, string json)
    {
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes(json));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
        return await repo.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
    }

    private static string Describe(BatchResult result) =>
        string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message));

    private static async Task<string> TextAsync(E2ERepo repo, string id) =>
        (await repo.Store.GetElementAsync(id, Ct))!.Json.GetProperty("content").GetProperty("text").GetString()!;

    [Fact]
    public async Task A_new_name_creates_and_the_same_name_replaces_keeping_the_id()
    {
        await using var repo = await CopyAsync();

        var first = await ApplyAsync(repo, Upsert("One."));
        Assert.True(first.Outcome == SaveOutcome.Saved, Describe(first));
        var id = Assert.Single(first.Items).Id!;
        Assert.Equal("One.", await TextAsync(repo, id));

        var second = await ApplyAsync(repo, Upsert("Two."));
        Assert.True(second.Outcome == SaveOutcome.Saved, Describe(second));
        Assert.Equal(id, Assert.Single(second.Items).Id);
        Assert.Equal("Two.", await TextAsync(repo, id));
        Assert.Single((await repo.Store.GetSnapshotAsync(Ct)).Names("custom").Find("policy.keeps_lines"));
    }

    [Fact]
    public async Task A_run_that_changes_nothing_writes_nothing()
    {
        await using var repo = await CopyAsync();
        var first = await ApplyAsync(repo, Upsert("One."));
        var id = Assert.Single(first.Items).Id!;
        var hash = (await repo.Store.GetElementAsync(id, Ct))!.Hash;
        repo.AgeFiles();
        var times = repo.WriteTimes();

        var again = await ApplyAsync(repo, Upsert("One."));

        Assert.True(again.Outcome == SaveOutcome.Saved, Describe(again));
        Assert.Equal(hash, (await repo.Store.GetElementAsync(id, Ct))!.Hash);
        Assert.Equal(times, repo.WriteTimes());
    }

    [Fact]
    public async Task A_stale_hash_given_on_purpose_is_a_conflict()
    {
        await using var repo = await CopyAsync();
        await ApplyAsync(repo, Upsert("One."));

        var stale = await ApplyAsync(repo, Upsert("Two.", extra: """, "expectedHash": "0000000000000000000000000000000000000000000000000000000000000000" """));

        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
    }

    [Theory]
    [InlineData("01JRVP00000000000000000001", "MQ1015", "must be a qualified name")]
    [InlineData("$new", "MQ1015", "must be a qualified name")]
    [InlineData("@entity:keeps_lines", "MQ1015", "names a entity")]
    [InlineData("@attribute:Customer.name", "MQ1015", "writes a whole element")]
    [InlineData("@custom:policy.another_name", "MQ1015", "does not rename")]
    public async Task An_id_that_cannot_find_this_element_again_is_refused(string id, string rule, string says)
    {
        await using var repo = await CopyAsync();

        var refused = await ApplyAsync(repo, Upsert("One.", id));

        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var diagnostic = Assert.Single(refused.Items.SelectMany(i => i.Diagnostics));
        Assert.Equal((rule, "/operations/0/id"), (diagnostic.Rule, diagnostic.JsonPointer));
        Assert.Contains(says, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_two_elements_share_is_refused_until_a_longer_form_tells_them_apart()
    {
        await using var repo = await CopyAsync();
        await repo.CreateElementAsync("""{ "kind": "package", "id": "01JRVP00000000000000000002", "name": "Sales" }""");
        await repo.CreateElementAsync("""{ "kind": "package", "id": "01JRVP00000000000000000003", "name": "Support" }""");
        await repo.CreateElementAsync("""{ "kind": "custom", "id": "01JRVP00000000000000000004", "name": "keeps_lines", "type": "policy", "package": "01JRVP00000000000000000002", "content": { "text": "A" } }""");
        await repo.CreateElementAsync("""{ "kind": "custom", "id": "01JRVP00000000000000000005", "name": "keeps_lines", "type": "policy", "package": "01JRVP00000000000000000003", "content": { "text": "B" } }""");

        var shared = await ApplyAsync(repo, Upsert("New."));
        Assert.Equal("MQ1013", Assert.Single(shared.Items.SelectMany(i => i.Diagnostics)).Rule);

        var named = await ApplyAsync(repo, $$"""
            { "operations": [ { "op": "upsert", "id": "@custom:policy.Sales.keeps_lines",
                "element": { "kind": "custom", "type": "policy", "name": "keeps_lines", "package": "01JRVP00000000000000000002", "content": { "text": "New." } } } ] }
            """);
        Assert.True(named.Outcome == SaveOutcome.Saved, Describe(named));
        Assert.Equal("New.", await TextAsync(repo, "01JRVP00000000000000000004"));
        Assert.Equal("B", await TextAsync(repo, "01JRVP00000000000000000005"));
    }

    [Fact]
    public async Task A_part_with_no_id_is_refused_as_in_any_update()
    {
        await using var repo = await CopyAsync();
        var customer = (await repo.Store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var node = JsonNode.Parse(customer.Json.GetRawText())!.AsObject();
        foreach (var attribute in node["attributes"]!.AsArray())
            attribute!.AsObject().Remove("id");

        var refused = await ApplyAsync(repo, new JsonObject
        {
            ["operations"] = new JsonArray(new JsonObject { ["op"] = "upsert", ["id"] = "@entity:Customer", ["element"] = node }),
        }.ToJsonString());

        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Contains(refused.Items.SelectMany(i => i.Diagnostics), d => d.Rule == "MQ1014");
    }

    [Fact]
    public async Task A_dry_run_answers_what_an_apply_would_do()
    {
        await using var repo = await CopyAsync();
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes(Upsert("One.")));

        var preview = await repo.Store.PreviewBatchAsync(parsed.Batch!, Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Equal("created", Assert.Single(preview.Files, f => f.Path.Contains("keeps", StringComparison.Ordinal)).Action);
        Assert.Empty((await repo.Store.GetSnapshotAsync(Ct)).Names("custom").Find("policy.keeps_lines"));
    }
}
