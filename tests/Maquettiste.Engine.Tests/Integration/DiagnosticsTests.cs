using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Diagnostics that cross workstreams, checked end to end: a corrupt schema snapshot (W8 → W6), load warnings reported once (W1 and
/// W2), resolver identifier-limit errors surfaced by the orchestrator (W3 → W6), and the stereotype-key save rule (W2 → W1).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class DiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_corrupt_schema_snapshot_is_an_error_on_that_file_not_an_exception()
    {
        await using var repo = E2ERepo.Create(demo: false, migrations: true);
        await repo.ApplyAsync();
        repo.Repo.WriteFile(".maquettiste/snapshots/main.json", "{ not json");
        var tree = repo.Tree();

        var result = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Invalid, result);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("MQ1001", ".maquettiste/snapshots/main.json", E2ERepo.MainDatabaseId), (error.Rule, error.FilePath, error.ElementId));
        FullRunTests.AssertSameBytes(tree, repo.Tree());
        E2ERepo.AssertOutcome(RunOutcome.Invalid, await repo.RunAsync(GenerationMode.Check));
    }

    [Fact]
    public async Task A_non_canonical_model_file_is_one_warning_and_does_not_stop_generation()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var customer = repo.Repo.PathOf(".maquettiste/model/entities/customer.json");
        File.WriteAllText(customer, JsonNode.Parse(File.ReadAllText(customer))!.ToJsonString());

        var result = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(("MQ1003", DiagnosticSeverity.Warning, ".maquettiste/model/entities/customer.json"), (warning.Rule, warning.Severity, warning.FilePath));
    }

    [Fact]
    public async Task Resolver_identifier_limit_errors_stop_the_run()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["conventions"] = new JsonObject
        {
            ["tableName"] = "{entity}_with_an_extremely_long_suffix_that_goes_well_past_the_identifier_limit",
        });

        var result = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Invalid, result);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ4001" && d.FilePath == ".maquettiste/model/entities/customer.json"
            && d.Message.Contains("Table name 'customers_with_an_extremely_long", StringComparison.Ordinal));
        Assert.All(result.Diagnostics, d => Assert.Equal("MQ4001", d.Rule));
        Assert.Empty(repo.Outputs());
    }

    [Fact]
    public async Task Changing_a_stereotype_key_is_refused_on_save()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        var audited = snapshot.Documents.Single(d => d.Path.EndsWith("/audited.json", StringComparison.Ordinal));
        var node = JsonNode.Parse(audited.Json.GetRawText())!;
        node["key"] = "audited-renamed";

        var saved = await repo.Store.SaveAsync(audited.Element.Id, Encoding.UTF8.GetBytes(node.ToJsonString()), audited.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, saved.Outcome);
        Assert.Equal(("MQ3020", "/key"), (Assert.Single(saved.Diagnostics).Rule, saved.Diagnostics[0].JsonPointer));
    }
}
