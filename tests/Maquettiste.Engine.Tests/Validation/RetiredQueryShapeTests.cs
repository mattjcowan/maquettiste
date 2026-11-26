using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// MQ4127: a query that names an entity as the shape of its rows, or fills collections, is a warning once per file (at <c>/entity</c>,
/// else at <c>/collections</c>), which <c>validation.rules</c> may change or turn off. The billing fixture keeps its two older queries in
/// that form on purpose, with the rule off; these tests turn it back on.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RetiredQueryShapeTests
{
    private const string InvoicesByCustomer = ".maquettiste/model/databases/main/queries/invoices-by-customer.json";
    private const string FindCustomers = ".maquettiste/model/databases/main/queries/find-customers-with-issued-invoices.json";
    private const string Settings = ".maquettiste/maquettiste.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A billing copy whose <c>validation.rules</c> gives MQ4127 the severity given, or leaves it at its default.</summary>
    private static TempRepo Billing(string? severity)
    {
        var repo = new TempRepo();
        E2ERepo.CopyTree(Fixtures.Path("models", "billing"), repo.RepoRoot);
        var settings = JsonNode.Parse(repo.ReadFile(Settings))!.AsObject();
        settings.Remove("validation");
        if (severity is not null)
            settings["validation"] = new JsonObject { ["rules"] = new JsonObject { ["MQ4127"] = severity } };
        repo.WriteFile(Settings, settings.ToJsonString(Indented) + "\n");
        return repo;
    }

    private static async Task<List<Diagnostic>> Findings(TempRepo repo)
    {
        var model = ValidationFixture.LoadRepo(repo.RepoRoot);
        var report = await ValidationFixture.Validator(repo.RepoRoot).ValidateAsync(model, ValidationScope.All, null, Ct);
        return [.. report.Diagnostics.Where(d => d.Rule == "MQ4127").OrderBy(d => d.FilePath, StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_query_that_names_an_entity_is_one_warning_at_entity_and_the_billing_fixture_turns_it_off()
    {
        using (var asIs = new TempRepo())
        {
            E2ERepo.CopyTree(Fixtures.Path("models", "billing"), asIs.RepoRoot);
            Assert.Empty(await Findings(asIs));
        }

        using var repo = Billing(null);
        var found = await Findings(repo);

        Assert.Collection(found,
            d =>
            {
                Assert.Equal((DiagnosticSeverity.Warning, FindCustomers, "/entity"), (d.Severity, d.FilePath, d.JsonPointer));
                Assert.Contains("'FindCustomersWithIssuedInvoices' names an entity as the shape of its rows,", d.Message, StringComparison.Ordinal);
            },
            d =>
            {
                Assert.Equal((DiagnosticSeverity.Warning, InvoicesByCustomer, "/entity"), (d.Severity, d.FilePath, d.JsonPointer));
                Assert.Contains("names an entity as the shape of its rows and fills collections", d.Message, StringComparison.Ordinal);
                Assert.Contains("bind the entity to it with its binding's source", d.Message, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task Collections_alone_are_the_warning_at_collections_and_validation_rules_set_its_severity()
    {
        using var repo = Billing("error");
        var query = JsonNode.Parse(repo.ReadFile(InvoicesByCustomer))!.AsObject();
        query.Remove("entity");
        repo.WriteFile(InvoicesByCustomer, query.ToJsonString(Indented) + "\n");

        var found = await Findings(repo);

        var collections = Assert.Single(found, d => d.FilePath == InvoicesByCustomer);
        Assert.Equal((DiagnosticSeverity.Error, "/collections"), (collections.Severity, collections.JsonPointer));
        Assert.Contains("'InvoicesByCustomer' fills collections,", collections.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(found, d => d.FilePath == FindCustomers).Severity);
    }

    [Fact]
    public async Task A_query_with_neither_has_no_warning()
    {
        using var repo = Billing(null);
        foreach (var path in new[] { InvoicesByCustomer, FindCustomers })
        {
            var query = JsonNode.Parse(repo.ReadFile(path))!.AsObject();
            query.Remove("entity");
            query.Remove("collections");
            repo.WriteFile(path, query.ToJsonString(Indented) + "\n");
        }

        Assert.Empty(await Findings(repo));
    }
}
