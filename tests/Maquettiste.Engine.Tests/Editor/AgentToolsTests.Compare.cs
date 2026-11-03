using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Tests.Snapshots;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary><c>compare_snapshots</c> (next-phase-2-design.md section 6.2): grouped by package, field changes, and the markdown review.</summary>
public sealed partial class AgentToolsTests
{
    [Fact]
    public async Task Compare_snapshots_groups_by_package_and_answers_markdown()
    {
        await using var f = await SnapshotFixture.CreateAsync();
        var snapshot = await f.Library.CreateAsync(new SnapshotCreateRequest("Before review"), Ct);
        ModelBaselineTests.CopyBilling(Path.Combine(f.Repo.RepoRoot, "base"));
        await ModelDiffTests.ReviewChangeAsync(f.Store);
        var catalog = new AgentTools(f.Store, new GenerationService(f.Store, f.Store.Options), new AgentToolsOptions { RepoRoot = f.Repo.RepoRoot });
        async Task<AgentToolResult> CallAsync(object args) =>
            await catalog.CallAsync("compare_snapshots", JsonSerializer.SerializeToElement(args), Ct);
        async Task<JsonNode> OkAsync(object args)
        {
            var answer = await CallAsync(args);
            Assert.False(answer.IsError, answer.Text);
            return JsonNode.Parse(answer.Text)!;
        }

        // Always by package; the facts of main in their own group, labelled; no field changes unless asked.
        var plain = await OkAsync(new { from = snapshot.Id });
        Assert.Equal(["Billing", "migrations of main"], plain["packages"]!.AsArray().Select(p => (string)p!["name"]!));
        Assert.Equal(["entity", "table", "migration"], plain["elements"]!.AsArray().Select(e => (string)e!["kind"]!));
        Assert.Equal(ModelDiffTests.Billing, (string?)plain["elements"]![0]!["package"]);
        Assert.Equal("rename column billing.invoices.number", (string?)plain["elements"]![2]!["label"]);
        Assert.Null(plain["elements"]![0]!["fields"]);

        var fields = await OkAsync(new { from = snapshot.Id, fieldChanges = true });
        var field = fields["elements"]![0]!["fields"]![0]!;
        Assert.Equal(("attribute", "code", "/name"), ((string?)field["subKind"], (string?)field["subName"], (string?)field["subPointer"]));

        // markdown answers the page in the review form, the bytes ModelDiffFormat renders.
        var markdown = await OkAsync(new { from = snapshot.Id, format = "markdown" });
        var expected = (await f.Library.CompareAsync(snapshot.Id, SnapshotLibrary.Working, 0, SnapshotCompareOptions.MaxElementFields,
            new SnapshotCompareOptions { ByPackage = true, FieldChanges = true }, Ct))!;
        Assert.Equal(ModelDiffFormat.Markdown(expected), (string?)markdown["markdown"]);
        Assert.Null(markdown["next"]);
        Assert.Contains("  - attribute `code`, `/name` from `\"number\"` to `\"code\"`\n", (string)markdown["markdown"]!, StringComparison.Ordinal);
        var page = await OkAsync(new { from = snapshot.Id, format = "markdown", limit = 1 });
        Assert.Equal(1, (int?)page["next"]);

        // The detail names the sub-element; the editor's host refuses a folder; a bad format is bad-request.
        var detail = await OkAsync(new { from = snapshot.Id, id = ModelDiffTests.Invoice });
        Assert.Equal(ModelDiffTests.InvoiceNumber, (string?)detail["fields"]![0]!["subId"]);
        // The detail in markdown answers { markdown, next } too, next always null.
        var detailMarkdown = (await OkAsync(new { from = snapshot.Id, id = ModelDiffTests.Invoice, format = "markdown" })).AsObject();
        Assert.Equal(["markdown", "next"], detailMarkdown.Select(p => p.Key));
        Assert.Null(detailMarkdown["next"]);
        Assert.False(string.IsNullOrEmpty((string?)detailMarkdown["markdown"]));
        Assert.Equal("bad-request", (string?)JsonNode.Parse((await CallAsync(new { from = "./base" })).Text)!["code"]);
        Assert.Equal("bad-request", (string?)JsonNode.Parse((await CallAsync(new { from = snapshot.Id, format = "yaml" })).Text)!["code"]);
        Assert.Equal("not-found", (string?)JsonNode.Parse((await CallAsync(new { from = "missing-20260101-000000" })).Text)!["code"]);
    }
}
