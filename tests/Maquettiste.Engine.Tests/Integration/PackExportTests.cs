using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>Acceptance for scope 5.3: a services pack reads the class names a records pack exports (next-phase-2-design.md 5.3).</summary>
public sealed class PackExportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static E2ERepo Create(bool cycle = false)
    {
        var repo = E2ERepo.Create(demo: false, settings: settings =>
        {
            E2ERepo.RecordOff(settings);
            settings["properties"] = new JsonObject { ["baseNamespace"] = "App" };
            var packs = settings["packs"]!.AsObject();
            packs["e2e"]!["enabled"] = false;
            packs["records"] = new JsonObject { ["output"] = "src/Generated" };
            packs["services"] = new JsonObject { ["output"] = "src/Generated" };
        });
        var source = Fixtures.Path("integration", "exports");
        foreach (var pack in new[] { "records", "services" })
            E2ERepo.CopyTree(Path.Combine(source, pack), Path.Combine(repo.Repo.ModelRoot, "templates", pack));
        if (cycle)
        {
            var path = Path.Combine(repo.Repo.ModelRoot, "templates", "records", "pack.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"engine\": \">=1.0\",", "\"engine\": \">=1.0\", \"uses\": [{ \"pack\": \"services\", \"version\": \"^1.0.0\" }],", StringComparison.Ordinal));
        }

        return repo;
    }

    private static Task<GenerationPlan> PlanAsync(E2ERepo repo, IReadOnlyList<string>? packs = null) =>
        repo.Service.PlanAsync(new GenerationRequest { Mode = GenerationMode.DryRun, Packs = packs }, null, Ct)
            .ContinueWith(t => t.Result.Plan!, Ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static string InvoiceId(E2ERepo repo) => System.Text.RegularExpressions.Regex.Match(
        Directory.EnumerateFiles(repo.Repo.ModelRoot, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText)
            .First(t => t.Contains("\"kind\": \"entity\"", StringComparison.Ordinal) && t.Contains("\"name\": \"Invoice\"", StringComparison.Ordinal)),
        "\"id\": \"([0-9A-Z]{26})\"").Groups[1].Value;

    [Fact]
    public async Task A_services_pack_takes_class_names_from_records_and_a_rename_flows_through_in_one_plan()
    {
        await using var repo = Create();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.ApplyAsync());
        var service = repo.Outputs()["src/Generated/services/Billing/InvoiceService.cs"];
        Assert.Contains("public App.Billing.InvoiceRecord Load(Guid id)", service, StringComparison.Ordinal);
        foreach (var member in new[] { "Number", "IssuedOn", "Total", "CreatedAt", "UpdatedAt", "DeletedAt" })
            Assert.Contains("    // " + member + "\n", service, StringComparison.Ordinal);

        var pack = Path.Combine(repo.Repo.ModelRoot, "templates", "records", "pack.json");
        File.WriteAllText(pack, File.ReadAllText(pack).Replace("}}Record\"", "}}Row\"", StringComparison.Ordinal));
        var plan = await PlanAsync(repo);

        Assert.Contains(plan.Changes, c => c.Path == "src/Generated/records/Billing/InvoiceRow.cs" && c.Kind == FileChangeKind.Added);
        Assert.Contains(plan.Changes, c => c.Path == "src/Generated/records/Billing/InvoiceRecord.cs" && c.Kind == FileChangeKind.Deleted);
        Assert.Contains(plan.Changes, c => c.Path == "src/Generated/records/Catalog/ProductRow.cs" && c.Kind == FileChangeKind.Added);
        Assert.Equal(8, plan.Changes.Count(c => c.Path.StartsWith("src/Generated/records/", StringComparison.Ordinal) && c.Kind == FileChangeKind.Added));
        Assert.Equal(8, plan.Changes.Count(c => c.Path.StartsWith("src/Generated/services/", StringComparison.Ordinal) && c.Path.EndsWith("Service.cs", StringComparison.Ordinal)
            && c.Kind == FileChangeKind.Modified));
        var status = plan.Units.Single(u => u.Unit == "status" && repo.Outputs().ContainsKey("src/Generated/services/InvoiceStatus.txt") && u.Outputs.Any(o => o.Path.EndsWith("InvoiceStatus.txt", StringComparison.Ordinal)));
        Assert.Equal((true, "unchanged"), (status.Skipped, status.Reason));
        var invoice = InvoiceId(repo);
        Assert.Contains(plan.Units.Single(u => u.Key == "services/service:" + invoice).Causes, c => c.Kind == "exports");
        Assert.Equal("InvoiceRow", plan.Units.Single(u => u.Key == "records/record:" + invoice).Exports!.Values.GetProperty("class_name").GetString());

        Assert.Equal(RunOutcome.Succeeded, (await repo.Service.ApplyAsync(plan.Id, null, Ct)).Outcome);
        Assert.Empty((await PlanAsync(repo)).UnitsRendered);
    }

    [Fact]
    public async Task Renaming_an_entity_renders_only_its_two_units()
    {
        await using var repo = Create();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.ApplyAsync());
        var invoice = InvoiceId(repo);

        await repo.EditAsync(invoice, n => n["name"] = "Bill");
        var plan = await PlanAsync(repo);

        Assert.Equal(["records/record:" + invoice, "services/service:" + invoice], plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal));
        Assert.Contains(plan.Changes, c => c.Path == "src/Generated/records/Billing/BillRecord.cs" && c.Kind == FileChangeKind.Added);
    }

    [Fact]
    public async Task Generating_services_alone_uses_records_exports()
    {
        await using var repo = Create();

        var plan = await PlanAsync(repo, ["services"]);
        Assert.Equal(RunOutcome.Succeeded, (await repo.Service.ApplyAsync(plan.Id, null, Ct)).Outcome);

        Assert.Equal(8, repo.Outputs().Keys.Count(p => p.StartsWith("src/Generated/services/", StringComparison.Ordinal) && p.EndsWith("Service.cs", StringComparison.Ordinal)));
        Assert.Contains("App.Billing.InvoiceRecord", repo.Outputs()["src/Generated/services/Billing/InvoiceService.cs"], StringComparison.Ordinal);
        Assert.DoesNotContain(repo.Outputs().Keys, p => p.StartsWith("src/Generated/records/", StringComparison.Ordinal));
        Assert.False(repo.Repo.Exists(".maquettiste/manifest/records.json"));
        Assert.Equal(8, plan.Exports.Count(e => e.Key.StartsWith("records/record:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_uses_cycle_between_records_and_services_is_MQ6044()
    {
        await using var repo = Create(cycle: true);

        var result = await repo.RunAsync();

        Assert.Equal(RunOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6044" && d.FilePath!.EndsWith("records/pack.json", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6044" && d.FilePath!.EndsWith("services/pack.json", StringComparison.Ordinal));
        Assert.DoesNotContain(repo.Outputs().Keys, p => p.StartsWith("src/Generated/", StringComparison.Ordinal));
    }
}
