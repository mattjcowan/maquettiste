using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>Model edits the migration tests apply to the billing fixture between generate runs.</summary>
internal static class ModelChanges
{
    private const string CustomerPath = ".maquettiste/model/entities/customer.json";

    /// <summary>
    /// Customer changes: drop <c>customerSince</c>, add <c>phone</c>, rename <c>name</c> to <c>fullName</c> and widen it, add an
    /// indexed <c>region</c> and a required <c>priority</c> with a default; and <c>refers to</c> cascades on delete.
    /// </summary>
    /// <param name="repo">The repo.</param>
    public static void ChangeCustomer(PackRepo repo)
    {
        repo.EditJson(CustomerPath, customer =>
        {
            var attributes = customer["attributes"]!.AsArray();
            attributes.Remove(attributes.First(a => (string?)a!["name"] == "customerSince"));
            attributes.Add(new JsonObject { ["id"] = "01J92P0V2B0000000000000001", ["name"] = "phone", ["type"] = "string", ["length"] = 30 });
            attributes.Add(new JsonObject { ["id"] = "01J92P0V2B0000000000000002", ["name"] = "region", ["type"] = "string", ["length"] = 40, ["indexed"] = true });
            attributes.Add(new JsonObject { ["id"] = "01J92P0V2B0000000000000003", ["name"] = "priority", ["type"] = "int32", ["required"] = true, ["default"] = 0 });
            var name = attributes.First(a => (string?)a!["name"] == "name")!;
            name["name"] = "fullName";
            name["length"] = 200;
        });
        repo.EditJson(".maquettiste/model/relations/refers-to.json", relation => relation["ends"]![1]!["onDelete"] = "cascade");
    }

    /// <summary>Removes the <c>priority</c> attribute <see cref="ChangeCustomer"/> added (a column with a default).</summary>
    /// <param name="repo">The repo.</param>
    public static void RemovePriority(PackRepo repo) =>
        repo.EditJson(CustomerPath, customer =>
        {
            var attributes = customer["attributes"]!.AsArray();
            attributes.Remove(attributes.First(a => (string?)a!["name"] == "priority"));
        });

    /// <summary>
    /// An optional relation from Customer to Invoice (a featured invoice). Invoice already references Customer, so the two tables
    /// form a foreign-key cycle.
    /// </summary>
    /// <param name="repo">The repo.</param>
    public static void AddFeaturedInvoice(PackRepo repo) => repo.Write(".maquettiste/model/relations/features.json", """
        {
          "$schema": "../../.schema/v1/relation.json",
          "kind": "relation",
          "id": "01J92P0V2D0000000000000001",
          "name": "features",
          "package": "01J92P0V01KDRN8GX5PGYCNKSX",
          "ends": [
            {
              "id": "01J92P0V2D0000000000000002",
              "entity": "01J92P0V0ETQKXXP951CMMNHH3",
              "role": "featuringCustomers"
            },
            {
              "id": "01J92P0V2D0000000000000003",
              "entity": "01J92P0V0FJ23CGSNKM7P1W5V7",
              "role": "featuredInvoice",
              "navigation": "featuredInvoice",
              "min": 0,
              "max": 1
            }
          ]
        }

        """);

    /// <summary>A new InvoiceStatus member, <c>Disputed</c> (4, code X), of an enum stored as its codes.</summary>
    /// <param name="repo">The repo.</param>
    public static void AddDisputedStatus(PackRepo repo) =>
        repo.EditJson(".maquettiste/model/enums/invoice-status.json", status =>
            status["members"]!.AsArray().Add(new JsonObject { ["id"] = "01J92P0V2E0000000000000001", ["name"] = "Disputed", ["value"] = 4, ["code"] = "X" }));

    /// <summary>A new PaymentMethod row, <c>voucher</c>: a new row of the lookup table and a new code in the CHECKs.</summary>
    /// <param name="repo">The repo.</param>
    public static void AddVoucherMethod(PackRepo repo) =>
        repo.EditJson(".maquettiste/model/seeds/payment-method/payment-method.json", seed =>
            seed["rows"]!.AsArray().Add(new JsonObject { ["id"] = "01J92P0V2F0000000000000014", ["values"] = new JsonArray("voucher", "Voucher") }));
}
