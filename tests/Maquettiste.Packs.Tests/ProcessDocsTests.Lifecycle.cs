using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The process-docs pages of a lifecycle that uses state codes, several subjects or a binding beyond an enum (lifecycle-design.md
/// section 9): the page lists the subjects, their bindings and the codes, and the index lists every subject. The golden test keeps
/// the pages of a lifecycle that uses none of this.
/// </summary>
public sealed partial class ProcessDocsTests
{
    [Fact]
    public async Task A_lifecycle_page_lists_its_subjects_their_bindings_and_its_codes()
    {
        using var repo = new DocsRepo("processes");
        repo.EditJson(".maquettiste/model/entities/sales-order.json", entity =>
        {
            var status = entity["attributes"]!.AsArray().Single(a => (string?)a!["name"] == "status")!;
            status["type"] = "string";
            status["length"] = 32;
            status["default"] = "draft";
        });
        var codes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Draft"] = "draft", ["CreditReview"] = "review", ["Fulfilment"] = "fulfilment", ["OnHold"] = "hold", ["Completed"] = "done", ["Cancelled"] = "cancelled",
        };
        repo.EditJson(".maquettiste/model/processes/sales-order-lifecycle.json", process =>
        {
            foreach (var state in process["states"]!.AsArray())
            {
                if (codes.TryGetValue((string)state!["name"]!, out var code))
                    state["code"] = code;
            }

            // PurchaseRequest follows the lifecycle too, with no attribute holding its state.
            process["subjects"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01M6Q0KV191MQ003ZG6KPZSM75", "entity": "01JQENT0000000000000000003" }"""));
        });
        await repo.GenerateCleanlyAsync();

        var page = repo.Read("docs/processes/sales-order-lifecycle.md");
        Assert.Contains("| Use | lifecycle |\n| Subjects | `SalesOrder`, `PurchaseRequest` |\n", page, StringComparison.Ordinal);
        Assert.Contains("""

            ## Lifecycle

            | Subject | Attribute | Type |
            | --- | --- | --- |
            | `SalesOrder` | `SalesOrder.status` | `string(32)` |
            | `PurchaseRequest` | none |  |

            The codes form a map: an attribute bound to a reference type or a plain status holds the code of the state's nearest coded ancestor, and a state with none has no code.

            | State | Code |
            | --- | --- |
            | `Draft` | `"draft"` |
            | `CreditReview` | `"review"` |
            | `Fulfilment` | `"fulfilment"` |
            | `OnHold` | `"hold"` |
            | `Completed` | `"done"` |
            | `Cancelled` | `"cancelled"` |

            ## Diagram
            """.ReplaceLineEndings("\n"), page, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum member", page, StringComparison.Ordinal);
        Assert.Contains("| lifecycle | `SalesOrder`, `PurchaseRequest` |", repo.Read("docs/index.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_enum_binding_with_codes_keeps_its_members_beside_the_codes()
    {
        using var repo = new DocsRepo("processes");
        repo.EditJson(".maquettiste/model/processes/sales-order-lifecycle.json", process =>
        {
            foreach (var state in process["states"]!.AsArray())
            {
                if ((string?)state!["name"] is "Draft" or "Completed")
                    state["code"] = (string)state["name"]!;
            }
        });
        await repo.GenerateCleanlyAsync();

        var page = repo.Read("docs/processes/sales-order-lifecycle.md");
        Assert.Contains("| Use | lifecycle |\n| Subject | `SalesOrder` |\n", page, StringComparison.Ordinal);
        Assert.Contains("| `SalesOrder` | `SalesOrder.status` | enum `SalesOrderStatus` |\n", page, StringComparison.Ordinal);
        Assert.Contains("| State | Code |\n| --- | --- |\n| `Draft` | `\"Draft\"` |\n| `Completed` | `\"Completed\"` |\n", page, StringComparison.Ordinal);
        Assert.Contains("The attribute `SalesOrder.status` holds the top-level state as a member of the enum `SalesOrderStatus`.\n\n| State | Enum member |", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_orchestration_page_has_no_lifecycle_section_even_with_codes_or_several_subjects()
    {
        using var repo = new DocsRepo("processes");
        repo.EditJson(".maquettiste/model/processes/purchase-approval.json", process =>
        {
            process["states"]!.AsArray()[0]!["code"] = "start";
            process["subjects"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01M6Q0KV191MQ003ZG6KPZSM76", "entity": "01JQENT0000000000000000001" }"""));
        });
        await repo.GenerateCleanlyAsync();

        var page = repo.Read("docs/processes/purchase-approval.md");
        Assert.Contains("| Subjects | `PurchaseRequest`, `SalesOrder` |\n", page, StringComparison.Ordinal);
        Assert.DoesNotContain("## Lifecycle", page, StringComparison.Ordinal);
        Assert.DoesNotContain("The codes form a map", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subjects_that_share_an_entity_name_are_qualified_by_package()
    {
        using var repo = new DocsRepo("processes");
        // A second SalesOrder, in the Purchasing package.
        repo.Write(".maquettiste/model/entities/purchasing-sales-order.json", """
            {
              "$schema": "../../.schema/v1/entity.json",
              "kind": "entity",
              "id": "01M6Q0KV191MQ003ZG6KPZSM80",
              "name": "SalesOrder",
              "package": "01JQPKG0000000000000000002",
              "key": { "attributes": [ "01M6Q0KV191MQ003ZG6KPZSM81" ], "strategy": "uuid-v7" },
              "attributes": [ { "id": "01M6Q0KV191MQ003ZG6KPZSM81", "name": "id", "type": "uuid", "required": true } ]
            }

            """);
        repo.EditJson(".maquettiste/model/processes/sales-order-lifecycle.json", process =>
            process["subjects"]!.AsArray().Add(JsonNode.Parse("""{ "id": "01M6Q0KV191MQ003ZG6KPZSM82", "entity": "01M6Q0KV191MQ003ZG6KPZSM80" }""")));
        await repo.GenerateCleanlyAsync();

        var page = repo.Read("docs/processes/sales-order-lifecycle.md");
        Assert.Contains("| Subjects | `Sales.SalesOrder`, `Purchasing.SalesOrder` |\n", page, StringComparison.Ordinal);
        Assert.Contains("| `Sales.SalesOrder` | `Sales.SalesOrder.status` | enum `SalesOrderStatus` |\n| `Purchasing.SalesOrder` | none |  |\n", page, StringComparison.Ordinal);
    }
}
