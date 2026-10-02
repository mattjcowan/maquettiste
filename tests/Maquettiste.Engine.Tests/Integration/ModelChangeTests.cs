using System.Diagnostics;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// More input changes end to end: files edited on disk behind the store (a sidecar, maquettiste.json), pack parameters, deleting an
/// element, pack filters and roots, preview, and cancelling a running pack script.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ModelChangeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Editing_a_description_sidecar_on_disk_rerenders_the_element_units()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();
        Assert.Contains("/// Invoice: # Invoice\n", repo.Repo.ReadFile("src/Generated/demo/src/Billing/Invoice.g.cs"), StringComparison.Ordinal);

        var sidecar = repo.Repo.PathOf(".maquettiste/model/entities/invoice.md");
        File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("# Invoice", "# Invoice (sidecar edited)", StringComparison.Ordinal));
        var result = await repo.ApplyAsync();

        Assert.Contains("/// Invoice: # Invoice (sidecar edited)\n", repo.Repo.ReadFile("src/Generated/demo/src/Billing/Invoice.g.cs"), StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => c.Path == "src/Generated/demo/src/Billing/Invoice.g.cs" && c.Kind == FileChangeKind.Modified);
        Assert.DoesNotContain(result.Changes, c => c.Path.Contains("Customer", StringComparison.Ordinal) && c.Kind != FileChangeKind.Kept);
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Changing_conventions_in_maquettiste_json_renames_tables_and_deletes_the_old_files()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();
        Assert.True(repo.Repo.Exists("db/e2e/tables/customers.sql"));

        var settingsPath = repo.Repo.PathOf(".maquettiste/maquettiste.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["conventions"] = new JsonObject { ["pluralTables"] = false };
        File.WriteAllBytes(settingsPath, TestServices.Json.Write(settings, "maquettiste.json", "maquettiste.json"));
        var result = await repo.ApplyAsync();

        Assert.False(repo.Repo.Exists("db/e2e/tables/customers.sql"));
        Assert.True(repo.Repo.Exists("db/e2e/tables/customer.sql"));
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/tables/customers.sql" && c.Kind == FileChangeKind.Deleted);
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/tables/customer.sql" && c.Kind == FileChangeKind.Added);
        Assert.Contains("stored in customer\n", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal);
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
    }

    [Fact]
    public async Task Changing_a_pack_parameter_rerenders_that_pack_only()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();

        var settingsPath = repo.Repo.PathOf(".maquettiste/maquettiste.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["packs"]!["e2e"]!["parameters"] = new JsonObject { ["greeting"] = "bonjour" };
        File.WriteAllBytes(settingsPath, TestServices.Json.Write(settings, "maquettiste.json", "maquettiste.json"));
        var plan = await IncrementalTests.PlanAsync(repo);
        var result = await repo.ApplyAsync();

        Assert.All(plan.Units.Where(u => u.Key.StartsWith("billing-demo/", StringComparison.Ordinal)), u => Assert.True(u.Skipped, u.Key));
        Assert.Contains("bonjour from CUSTOMER!", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal);
        Assert.StartsWith("bonjour\n", repo.Repo.ReadFile("db/e2e/index.txt"), StringComparison.Ordinal);
        Assert.All(result.Changes.Where(c => c.Kind == FileChangeKind.Modified), c => Assert.StartsWith("db/e2e/", c.Path, StringComparison.Ordinal));
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Deleting_an_element_deletes_its_outputs_and_keeps_its_owned_files()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var created = await repo.CreateElementAsync("""
            {"kind": "entity", "name": "Refund", "package": "01J92P0V01KDRN8GX5PGYCNKSX",
             "key": {"attributes": ["01J92P0VZZ0000000000000010"]},
             "attributes": [{"id": "01J92P0VZZ0000000000000010", "name": "id", "type": "uuid", "required": true}]}
            """);
        await repo.ApplyAsync();
        Assert.True(repo.Repo.Exists("db/e2e/entities/refund.txt"));
        Assert.Contains("- Refund", repo.Repo.ReadFile("db/e2e/index.txt"), StringComparison.Ordinal);

        var deleted = await repo.Store.DeleteAsync(created.Id!, created.Hash!, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, deleted.Outcome);
        var result = await repo.ApplyAsync();

        foreach (var path in new[] { "db/e2e/entities/refund.txt", "db/e2e/tables/refunds.sql", "db/e2e/types/Refund.cs", "db/e2e/pair/Refund.g.cs", "db/e2e/regions/refund.txt" })
        {
            Assert.False(repo.Repo.Exists(path), path);
            Assert.Contains(result.Changes, c => c.Path == path && c.Kind == FileChangeKind.Deleted);
        }

        foreach (var path in new[] { "db/e2e/scaffold/refund.txt", "db/e2e/pair/Refund.cs" })
        {
            Assert.True(repo.Repo.Exists(path), path); // owned files belong to the team (D29)
            Assert.Contains(result.Changes, c => c.Path == path && c.Kind == FileChangeKind.OrphanedOwned);
        }

        Assert.DoesNotContain("Refund", repo.Repo.ReadFile("db/e2e/index.txt"), StringComparison.Ordinal);
        Assert.DoesNotContain("Refund", repo.Manifest("e2e"), StringComparison.Ordinal);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
    }

    [Fact]
    public async Task A_pack_filter_and_a_root_selection_leave_the_other_outputs_alone()
    {
        await using var repo = E2ERepo.Create();
        var onlyE2e = await repo.RunAsync(packs: ["e2e"]);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, onlyE2e);
        Assert.All(repo.Outputs().Keys, p => Assert.StartsWith("db/e2e/", p, StringComparison.Ordinal));

        var all = await repo.ApplyAsync();
        Assert.All(all.Changes.Where(c => c.Kind != FileChangeKind.Kept), c => Assert.StartsWith("src/Generated/demo/", c.Path, StringComparison.Ordinal));

        // A filtered run does not orphan the other pack's files.
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var filtered = await repo.RunAsync(packs: ["e2e"]);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, filtered);
        Assert.All(filtered.Changes, c => Assert.Equal("e2e", c.Pack));
        Assert.True(repo.Repo.Exists("src/Generated/demo/docs/helpers.txt"));

        // Every root is covered by a run.
        await repo.EditAsync(E2ERepo.ProductId, n => n["attributes"]![1]!["length"] = 50);
        var rest = await repo.ApplyAsync();
        Assert.Contains(rest.Changes, c => c.Path == "db/e2e/entities/product.txt" && c.Kind == FileChangeKind.Modified);
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Preview_renders_one_unit_with_the_real_renderer_and_writes_nothing()
    {
        await using var repo = E2ERepo.Create();

        var preview = await repo.Service.PreviewAsync("e2e", "entity", E2ERepo.CustomerId, Ct);
        var model = await repo.Service.PreviewAsync("billing-demo", "registrations", null, Ct);
        var unknown = await repo.Service.PreviewAsync("e2e", "entity", "01J00000000000000000000000", Ct);

        Assert.Empty(preview.Diagnostics);
        var file = Assert.Single(preview.Files);
        Assert.Equal("db/e2e/entities/customer.txt", file.Path);
        Assert.Contains("hello from CUSTOMER!", file.Text, StringComparison.Ordinal);
        Assert.Equal(["src/Generated/demo/src/Billing/Registrations.g.cs", "src/Generated/demo/src/Catalog/Registrations.g.cs"], model.Files.Select(f => f.Path));
        Assert.Equal("MQ6017", Assert.Single(unknown.Diagnostics).Rule);
        Assert.Empty(repo.Outputs());
        Assert.False(repo.Repo.Exists(".maquettiste/manifest/e2e.json"));
    }

    [Fact]
    public async Task Cancelling_a_run_stops_a_running_pack_script()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["limits"] = new JsonObject
        {
            ["scriptTimeoutMs"] = 120000,
            ["scriptStatements"] = 2000000000,
        });
        var e2e = Path.Combine(repo.Repo.ModelRoot, "templates", "e2e");
        File.AppendAllText(Path.Combine(e2e, "helpers.js"), "maquettiste.helper(\"spin\", (x) => { let i = 0; for (;;) { i++; } });\n");
        File.WriteAllText(Path.Combine(e2e, "audited.scriban"), "{{ spin 1 }}\n"); // the first unit in plan order
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var stopwatch = new Stopwatch();
        using var registration = cts.Token.Register(stopwatch.Start);
        var progress = new ProgressLog(u =>
        {
            if (u.Stage == PipelineStage.Skip)
                cts.CancelAfter(TimeSpan.FromMilliseconds(500)); // the script is spinning by then
        });

        var result = await repo.RunAsync(progress: progress, ct: cts.Token, jobs: 1);
        stopwatch.Stop();

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "returned " + stopwatch.Elapsed + " after cancellation"); // budget 1 s; loose for CI
        Assert.Empty(repo.Outputs());
        using (CheckTests.HoldRunLock(repo))
        {
        }
    }

    [Fact]
    public async Task Renaming_an_entity_moves_its_outputs()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();

        await repo.EditAsync(E2ERepo.ProductId, n => n["name"] = "Article");
        var result = await repo.ApplyAsync();

        Assert.Contains(result.Changes, c => c.Path == "db/e2e/entities/product.txt" && c.Kind == FileChangeKind.Deleted);
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/entities/article.txt" && c.Kind == FileChangeKind.Added);
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/tables/articles.sql" && c.Kind == FileChangeKind.Added);
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/scaffold/product.txt" && c.Kind == FileChangeKind.OrphanedOwned);
        Assert.False(repo.Repo.Exists("db/e2e/tables/products.sql"));
        Assert.Contains("- Article", repo.Repo.ReadFile("db/e2e/index.txt"), StringComparison.Ordinal);
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
    }

    [Fact]
    public async Task The_skip_policy_leaves_a_hand_edited_file_and_updates_the_rest()
    {
        await using var repo = await CheckTests.AppliedAsync();
        repo.Repo.WriteFile("db/e2e/entities/customer.txt", "edited by hand\n");
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);

        var skipped = await repo.RunAsync(handEdits: HandEditPolicy.Skip);

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, skipped);
        Assert.Contains(skipped.Changes, c => c.Path == "db/e2e/entities/customer.txt" && c.Kind == FileChangeKind.HandEdited);
        Assert.Contains(skipped.Diagnostics, d => d.Rule == "MQ6009" && d.Severity == Diagnostics.DiagnosticSeverity.Warning);
        Assert.Equal("edited by hand\n", repo.Repo.ReadFile("db/e2e/entities/customer.txt"));
        Assert.Contains("varchar(150)", repo.Repo.ReadFile("db/e2e/tables/customers.sql"), StringComparison.Ordinal);

        // The default policy (fail) still refuses it, and check reports it.
        E2ERepo.AssertOutcome(RunOutcome.Conflicts, await repo.RunAsync(force: true));
        E2ERepo.AssertOutcome(RunOutcome.Conflicts, await repo.RunAsync(GenerationMode.Check));
    }

    [Fact]
    public async Task An_output_outside_the_allowed_roots_is_refused_and_nothing_is_written_there()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["packs"]!["e2e"]!["output"] = "elsewhere");

        var result = await repo.RunAsync();

        // The pattern's literal prefix is under no allowed root: MQ6019 at planning skips those units (generation-ui.md section 5.3).
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6019" && d.JsonPointer!.StartsWith("/units/", StringComparison.Ordinal));
        // A unit whose pattern starts with code is only known once rendered: its paths stay MQ6004 at write.
        E2ERepo.AssertOutcome(RunOutcome.Invalid, result);
        Assert.False(Directory.Exists(repo.Repo.PathOf("elsewhere")));
    }
}
