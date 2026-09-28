using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The model store through the public API, next to generation (integration tasks 9 and 10): JavaScript validation rules through the
/// real sandbox on load and on save, optimistic concurrency with the disk version on conflict, and all-or-nothing batches.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ModelStoreTests
{
    private const string Rule = """
        maquettiste.rule({
          id: "short-names",
          severity: "error",
          kinds: ["entity"],
          check(element, model, report) {
            if (element.name.length > 12) report("Entity names must be at most 12 characters.", { pointer: "/name" });
          },
        });
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_javascript_rule_runs_through_the_sandbox_on_load_and_on_save()
    {
        await using var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/naming.js", Rule);

        // On load: the billing model passes, so validation and generation succeed with the rule registered.
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.False(report.HasErrors, string.Join("; ", report.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Contains(".maquettiste/extensions/rules/naming.js", (await repo.Store.GetSnapshotAsync(Ct)).RuleScripts.Select(s => s.Path));
        await repo.ApplyAsync();

        // On save: a rename that breaks the rule is refused, and the file on disk is untouched.
        var customerPath = repo.Repo.PathOf(".maquettiste/model/entities/customer.json");
        var before = File.ReadAllBytes(customerPath);
        var document = (await repo.Store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!;
        node["name"] = "CustomerAccount";
        var saved = await repo.Store.SaveAsync(E2ERepo.CustomerId, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, saved.Outcome);
        var diagnostic = Assert.Single(saved.Diagnostics, d => d.Rule == "x/short-names");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(E2ERepo.CustomerId, diagnostic.ElementId);
        Assert.Equal("/name", diagnostic.JsonPointer);
        Assert.Equal(before, File.ReadAllBytes(customerPath));

        // On load again: the same edit made on disk behind the store's back stops generation with the rule's diagnostic.
        File.WriteAllText(customerPath, Encoding.UTF8.GetString(before).Replace("\"name\": \"Customer\"", "\"name\": \"CustomerAccount\"", StringComparison.Ordinal));
        var tree = repo.Tree();
        var run = await repo.RunAsync();
        E2ERepo.AssertOutcome(RunOutcome.Invalid, run);
        var fromRun = Assert.Single(run.Diagnostics, d => d.Rule == "x/short-names");
        Assert.Equal(".maquettiste/model/entities/customer.json", fromRun.FilePath);
        Assert.NotNull(fromRun.Line);
        FullRunTests.AssertSameBytes(tree, repo.Tree());
        Assert.Contains((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "x/short-names");
    }

    [Fact]
    public async Task A_rule_script_that_throws_is_reported_and_stops_generation()
    {
        await using var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/broken.js", """
            maquettiste.rule({ id: "broken", severity: "error", kinds: ["entity"], check(element) { if (element.name === "Product") throw new Error("boom"); } });
            """);

        var run = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Invalid, run);
        var failure = Assert.Single(run.Diagnostics, d => d.Rule == "MQ5002");
        Assert.Equal(E2ERepo.ProductId, failure.ElementId);
        Assert.Contains("boom", failure.Message, StringComparison.Ordinal);
        Assert.Equal(".maquettiste/extensions/rules/broken.js", failure.FilePath); // the failing line of the rule script
        Assert.Equal(1, failure.Line);
        Assert.DoesNotContain(run.Diagnostics, d => d.Rule == "x/broken");
        Assert.Empty(repo.Outputs());
    }

    [Fact]
    public async Task A_throwing_rule_turned_off_in_settings_does_not_stop_generation()
    {
        await using var repo = BrokenRuleRepo("off");

        var run = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, run);
        Assert.DoesNotContain(run.Diagnostics, d => d.Rule is "MQ5002" or "MQ5003" or "x/broken");
        Assert.NotEmpty(repo.Outputs());
    }

    [Fact]
    public async Task A_throwing_rule_set_to_warning_reports_its_failure_as_a_warning()
    {
        await using var repo = BrokenRuleRepo("warning");

        var run = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, run);
        var failure = Assert.Single(run.Diagnostics, d => d.Rule == "MQ5002");
        Assert.Equal(DiagnosticSeverity.Warning, failure.Severity);
        Assert.Equal(E2ERepo.ProductId, failure.ElementId);
        Assert.Equal(".maquettiste/extensions/rules/broken.js", failure.FilePath); // catalog id and script position are kept
        Assert.Equal(1, failure.Line);
        Assert.NotEmpty(repo.Outputs());
    }

    private static E2ERepo BrokenRuleRepo(string setting)
    {
        var repo = E2ERepo.Create(demo: false, settings: s => s["validation"] = new JsonObject
        {
            ["rules"] = new JsonObject { ["x/broken"] = setting },
        });
        repo.Repo.WriteFile(".maquettiste/extensions/rules/broken.js", """
            maquettiste.rule({ id: "broken", severity: "error", kinds: ["entity"], check(element) { if (element.name === "Product") throw new Error("boom"); } });
            """);
        return repo;
    }

    [Fact]
    public async Task Saving_with_an_outdated_hash_returns_a_conflict_with_the_disk_version()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var original = (await repo.Store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var first = await repo.EditAsync(E2ERepo.CustomerId, n => n["displayName"] = "Client");
        var customerPath = repo.Repo.PathOf(".maquettiste/model/entities/customer.json");
        var onDisk = File.ReadAllBytes(customerPath);

        // A second editor still holds the original hash.
        var node = JsonNode.Parse(original.Json.GetRawText())!;
        node["displayName"] = "Patron";
        var conflict = await repo.Store.SaveAsync(E2ERepo.CustomerId, Encoding.UTF8.GetBytes(node.ToJsonString()), original.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Conflict, conflict.Outcome);
        Assert.Equal(first.Hash, conflict.Hash);
        Assert.Equal(E2ERepo.Sha(onDisk), conflict.Hash);
        Assert.NotNull(conflict.Current);
        Assert.Equal("Client", conflict.Current.Json.GetProperty("displayName").GetString());
        Assert.Equal(onDisk, File.ReadAllBytes(customerPath));

        // An edit made on disk by another tool is a conflict too, judged against the disk rather than the index.
        File.WriteAllText(customerPath, Encoding.UTF8.GetString(onDisk).Replace("\"Client\"", "\"Disk\"", StringComparison.Ordinal));
        node["displayName"] = "Late";
        var late = await repo.Store.SaveAsync(E2ERepo.CustomerId, Encoding.UTF8.GetBytes(node.ToJsonString()), first.Hash!, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Conflict, late.Outcome);
        Assert.Equal("Disk", late.Current!.Json.GetProperty("displayName").GetString());
        Assert.Equal(E2ERepo.Sha(File.ReadAllBytes(customerPath)), late.Hash);
    }

    [Fact]
    public async Task A_failing_batch_leaves_the_repo_unchanged_and_a_passing_one_is_generated()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await repo.ApplyAsync();
        var customer = (await repo.Store.GetElementAsync(E2ERepo.CustomerId, Ct))!;
        var product = (await repo.Store.GetElementAsync(E2ERepo.ProductId, Ct))!;
        var modelBefore = ModelFiles(repo);
        var outputsBefore = repo.Tree();

        // Update Customer (valid), create Refund (valid), delete Product (still referenced by 'refers to'): nothing is written.
        var failing = repo.Store.ParseBatch(Encoding.UTF8.GetBytes(Batch(customer, product, deleteProduct: true)));
        Assert.Empty(failing.Diagnostics);
        var refused = await repo.Store.ApplyBatchAsync(failing.Batch!, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Equal(3, refused.Items.Count);
        Assert.NotEmpty(refused.Items[2].Referrers);
        FullRunTests.AssertSameBytes(modelBefore, ModelFiles(repo));
        var noop = await repo.ApplyAsync();
        Assert.Equal(0, noop.UnitsRendered);
        FullRunTests.AssertSameBytes(outputsBefore, repo.Tree());

        // The same batch without the delete applies atomically and generation follows it.
        var passing = repo.Store.ParseBatch(Encoding.UTF8.GetBytes(Batch(customer, product, deleteProduct: false)));
        var applied = await repo.Store.ApplyBatchAsync(passing.Batch!, ChangeSource.Editor, Ct);
        Assert.True(applied.Outcome == SaveOutcome.Saved,
            string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
        Assert.NotNull(applied.Changes);
        Assert.Equal(2, applied.Changes.Changed.Count);

        var result = await repo.ApplyAsync();
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/entities/refund.txt" && c.Kind == FileChangeKind.Added);
        Assert.Contains("length 99", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal);
        Assert.Contains("- Refund", repo.Repo.ReadFile("db/e2e/index.txt"), StringComparison.Ordinal);
    }

    private static string Batch(Engine.Model.ElementDocument customer, Engine.Model.ElementDocument product, bool deleteProduct)
    {
        var updated = JsonNode.Parse(customer.Json.GetRawText())!;
        updated["attributes"]![1]!["length"] = 99;
        var operations = new JsonArray
        {
            new JsonObject { ["op"] = "update", ["id"] = E2ERepo.CustomerId, ["expectedHash"] = customer.Hash, ["element"] = updated },
            new JsonObject
            {
                ["op"] = "create",
                ["element"] = JsonNode.Parse("""
                    {"kind": "entity", "name": "Refund", "package": "01J92P0V01KDRN8GX5PGYCNKSX",
                     "key": {"attributes": ["01J92P0VZZ0000000000000010"]},
                     "attributes": [{"id": "01J92P0VZZ0000000000000010", "name": "id", "type": "uuid", "required": true}]}
                    """),
            },
        };
        if (deleteProduct)
            operations.Add(new JsonObject { ["op"] = "delete", ["id"] = E2ERepo.ProductId, ["expectedHash"] = product.Hash });
        return new JsonObject { ["operations"] = operations }.ToJsonString();
    }

    private static SortedDictionary<string, byte[]> ModelFiles(E2ERepo repo)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in repo.Repo.ListFiles().Where(p => p.StartsWith(".maquettiste/model/", StringComparison.Ordinal)))
            files[path] = File.ReadAllBytes(repo.Repo.PathOf(path));
        return files;
    }
}
