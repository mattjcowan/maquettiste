using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The process-docs pages of a lifecycle whose subject entry names a stereotype (stereotype-sets-design.md decision 12): templates see one
/// subject per carrier, so the pages render with zero, one or two carriers.
/// </summary>
public sealed partial class ProcessDocsTests
{
    private const string LifecycleFile = ".maquettiste/model/processes/sales-order-lifecycle.json";

    private static void Approvable(DocsRepo repo, params string[] carriers)
    {
        repo.Write(".maquettiste/model/vocabularies/stereotypes/approvable.json", """
            {
              "$schema": "../../../.schema/v1/stereotype.json",
              "kind": "stereotype",
              "id": "01M6Q0KV191MQ003ZG6KPZSM90",
              "key": "approvable",
              "name": "Approvable",
              "appliesTo": [ "entity" ]
            }

            """);
        repo.EditJson(LifecycleFile, process => process["subjects"] = JsonNode.Parse("""[ { "id": "01M6Q0KV191MQ003ZG6KPZSM91", "stereotype": "approvable" } ]"""));
        // SalesOrder stops naming the lifecycle, so the case holds with no carrier too (MQ9201).
        repo.EditJson(".maquettiste/model/entities/sales-order.json", entity => entity.AsObject().Remove("lifecycle"));
        foreach (var carrier in carriers)
            repo.EditJson(".maquettiste/model/entities/" + carrier + ".json", entity => entity["stereotypes"] = new JsonArray("approvable"));
    }

    [Fact]
    public async Task A_stereotype_nothing_carries_yet_gives_a_page_with_no_subject()
    {
        using var repo = new DocsRepo("processes");
        Approvable(repo);
        await repo.GenerateCleanlyAsync();

        var page = repo.Read("docs/processes/sales-order-lifecycle.md");
        Assert.Contains("| Use | lifecycle |\n", page, StringComparison.Ordinal);
        Assert.DoesNotContain("| Subject", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_carrier_reads_as_the_one_subject()
    {
        using var repo = new DocsRepo("processes");
        Approvable(repo, "sales-order");
        await repo.GenerateCleanlyAsync();

        Assert.Contains("| Use | lifecycle |\n| Subject | `SalesOrder` |\n", repo.Read("docs/processes/sales-order-lifecycle.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_carriers_read_as_two_subjects_by_qualified_name()
    {
        using var repo = new DocsRepo("processes");
        Approvable(repo, "sales-order", "purchase-request");
        await repo.GenerateCleanlyAsync();

        Assert.Contains("| Subjects | `PurchaseRequest`, `SalesOrder` |\n", repo.Read("docs/processes/sales-order-lifecycle.md"), StringComparison.Ordinal);
    }
}
