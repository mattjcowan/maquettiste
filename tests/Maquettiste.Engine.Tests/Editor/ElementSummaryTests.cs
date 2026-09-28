using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>E4: <see cref="Maquettiste.Engine.Model.ElementSummary"/> carries the element's category and stereotypes (phase2-design.md section 3.8).</summary>
public sealed class ElementSummaryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Index_summaries_carry_category_and_stereotypes_from_the_element()
    {
        await using var repo = EditorRepo.Create(packs: false);

        var index = await repo.Store.GetIndexAsync(EditorRepo.Ct);

        var invoice = index.Single(s => s.Id == EditorRepo.InvoiceId);
        Assert.Equal(EditorRepo.BillingCategoryId, invoice.Category);
        Assert.Equal(["aggregate-root", "audited", "soft-delete"], invoice.Stereotypes);
        var package = index.Single(s => s.Id == EditorRepo.BillingPackageId);
        Assert.Null(package.Category);
        Assert.Empty(package.Stereotypes);
        // Every summary matches its element: the whole index, not just the two above.
        foreach (var summary in index)
        {
            var element = (await repo.Store.GetElementAsync(summary.Id, EditorRepo.Ct))!.Element;
            Assert.Equal(element.Category, summary.Category);
            Assert.Equal(element.Stereotypes, summary.Stereotypes);
        }
    }

    [Fact]
    public async Task A_save_updates_the_summary_through_the_patched_index()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var document = (await repo.Store.GetElementAsync(EditorRepo.InvoiceId, EditorRepo.Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["stereotypes"] = new JsonArray("audited");
        node.Remove("category");

        var result = await repo.Store.SaveAsync(EditorRepo.InvoiceId, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, EditorRepo.Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var summary = (await repo.Store.GetIndexAsync(EditorRepo.Ct)).Single(s => s.Id == EditorRepo.InvoiceId);
        Assert.Null(summary.Category);
        Assert.Equal(["audited"], summary.Stereotypes);
        Assert.Equal(result.Hash, summary.Hash);
    }

    [Fact]
    public async Task Summaries_serialize_category_and_stereotypes_with_web_defaults()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var index = await repo.Store.GetIndexAsync(EditorRepo.Ct);

        var json = JsonNode.Parse(JsonSerializer.Serialize(index, Web))!.AsArray();

        var invoice = json.Single(n => n!["id"]!.GetValue<string>() == EditorRepo.InvoiceId)!;
        Assert.Equal(EditorRepo.BillingCategoryId, invoice["category"]!.GetValue<string>());
        Assert.Equal(3, invoice["stereotypes"]!.AsArray().Count);
        var package = json.Single(n => n!["id"]!.GetValue<string>() == EditorRepo.BillingPackageId)!.AsObject();
        Assert.True(package.ContainsKey("category"));
        Assert.Null(package["category"]);
        Assert.Empty(package["stereotypes"]!.AsArray());
    }
}
