using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The process-docs pages with a relation that has several types at both ends (several-types-design.md sections 19.9 and 19.12): no template
/// change. The sales order lifecycle's subject finds its owner through the relation's to-one end, which SalesOrder holds since the other end
/// covers it; the pages are pinned under <c>tests/fixtures/golden/process-docs/several-types-both-ends</c>.
/// </summary>
public sealed partial class ProcessDocsTests
{
    [Fact]
    public async Task A_relation_with_several_types_at_both_ends_reads_on_the_pages_as_any_relation()
    {
        const string SalesOrder = "01JQENT0000000000000000001", PurchaseRequest = "01JQENT0000000000000000003";
        using var repo = new DocsRepo("processes");
        repo.Write(".maquettiste/model/relations/related.json", $$"""
            {"kind":"relation","id":"01M6Q0ST000000000000000001","name":"Related","package":"01JQPKG0000000000000000001","ends":[
              {"id":"01M6Q0ST000000000000000002","types":[{"entity":"{{SalesOrder}}"},{"entity":"{{PurchaseRequest}}"}],"role":"source","navigation":"relatedFrom","max":"*"},
              {"id":"01M6Q0ST000000000000000003","types":[{"entity":"{{PurchaseRequest}}"},{"entity":"{{SalesOrder}}"}],"role":"target","navigation":"relatedTo","max":1}]}

            """);
        // The lifecycle's subject finds its owner through the to-one end (each end covers SalesOrder, so it holds both).
        repo.EditJson(LifecycleFile, process => process["subjects"]![0]!["owner"] = "01M6Q0ST000000000000000003");
        await repo.GenerateCleanlyAsync();
        var page = repo.Read("docs/processes/sales-order-lifecycle.md");
        Assert.Contains("`SalesOrder.relatedTo`", page, StringComparison.Ordinal);
        Golden.AssertMatches(Fixtures.Path("golden", "process-docs", "several-types-both-ends"), repo.PathOf("docs"));
    }
}
