using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance test of next-phase-1-design.md section 4.3 (scope 3.4, third line): in the billing model Catalog is a child of
/// Billing, <c>Money</c> is in Billing and Product (in Catalog) types <c>listPrice</c> as <c>Money</c>. A child does not reach its
/// parent by default, so once Catalog declares <c>dependsOn</c> without Billing, that reference is a layering error.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class PackageLayeringTests
{
    private const string CatalogPackageId = "01J92P0V025DRFTXKMS240G2NG";
    private const string BillingPackageId = "01J92P0V01KDRN8GX5PGYCNKSX";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reference_from_catalog_to_billing_without_dependsOn_is_an_error()
    {
        await using var repo = E2ERepo.Create(demo: false);
        Assert.Equal(0, (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);

        // The save of Catalog is refused on Product, which is not its referrer, with the store's whole scope.
        var refused = await SaveAsync(repo, CatalogPackageId, n => n["dependsOn"] = new JsonArray());
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        Assert.Equal(E2ERepo.ProductListPriceAttributeId, Assert.Single(refused.Diagnostics, d => d.Rule == "MQ2020").ElementId);

        // On disk, the whole model has exactly one finding.
        const string path = "model/packages/catalog.json";
        var node = JsonNode.Parse(repo.Repo.ReadFile(".maquettiste/" + path))!;
        node["dependsOn"] = new JsonArray();
        repo.Repo.WriteFile(".maquettiste/" + path, Encoding.UTF8.GetString(TestServices.Json.Write(node, "package.json", path)));
        await repo.RestartAsync();

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);

        var finding = Assert.Single(report.Diagnostics, d => d.Rule is "MQ2020" or "MQ2021");
        Assert.Equal(("MQ2020", DiagnosticSeverity.Error, E2ERepo.ProductListPriceAttributeId, "/attributes/3/type/ref"),
            (finding.Rule, finding.Severity, finding.ElementId, finding.JsonPointer));
        Assert.Equal(".maquettiste/model/entities/product.json", finding.FilePath);
        Assert.Contains("in package 'Billing', which package 'Billing.Catalog' does not depend on", finding.Message, StringComparison.Ordinal);

        // Listing Billing in Catalog's dependsOn clears it.
        var saved = await repo.EditAsync(CatalogPackageId, n => n["dependsOn"] = new JsonArray(BillingPackageId));
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(0, (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Errors);
    }

    private static async Task<SaveResult> SaveAsync(E2ERepo repo, string id, Action<JsonObject> edit)
    {
        var document = (await repo.Store.GetElementAsync(id, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        edit(node);
        return await repo.Store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
    }
}
