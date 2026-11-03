using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Acceptance for library packs (next-phase-2-design.md section 5.2): two packs share a <c>naming</c> library's partial and helper,
/// a project rule script reaches the same helper through <c>maquettiste.json</c>'s uses, a library parameter change renders every
/// unit that sees it again and nothing else, and a range the library misses stops generation with MQ6043.
/// </summary>
public sealed class LibraryPackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Entities = ["Customer", "CustomerNote", "Invoice", "InvoiceLine", "InvoiceNote", "Payment", "Product", "RevenueMonth"];

    /// <summary>Billing with the fixture's naming, records and services packs, the project's uses and the class-names rule.</summary>
    private static E2ERepo Create(string recordsRange = "^1.2.0")
    {
        var repo = E2ERepo.Create(demo: false, settings: settings =>
        {
            E2ERepo.RecordOff(settings);
            settings["uses"] = new JsonArray(new JsonObject { ["pack"] = "naming", ["version"] = "^1.0.0" });
            var packs = settings["packs"]!.AsObject();
            packs["e2e"]!["enabled"] = false;
            packs["records"] = new JsonObject { ["output"] = "src/Generated" };
            packs["services"] = new JsonObject { ["output"] = "src/Generated" };
        });
        var source = Fixtures.Path("integration", "library");
        foreach (var pack in new[] { "naming", "records", "services" })
            E2ERepo.CopyTree(Path.Combine(source, pack), Path.Combine(repo.Repo.ModelRoot, "templates", pack));
        E2ERepo.CopyTree(Path.Combine(source, "rules"), Path.Combine(repo.Repo.ModelRoot, "extensions", "rules"));
        if (recordsRange != "^1.2.0")
        {
            var path = Path.Combine(repo.Repo.ModelRoot, "templates", "records", "pack.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"^1.2.0\"", "\"" + recordsRange + "\"", StringComparison.Ordinal));
        }

        return repo;
    }

    private static string Folder(string entity) => entity == "Product" ? "Catalog" : "Billing";

    [Fact]
    public async Task Two_packs_share_a_naming_library_and_a_rule_uses_it()
    {
        await using var repo = Create();
        await repo.Store.GetSnapshotAsync(Ct);

        var first = await repo.ApplyAsync();
        var outputs = repo.Outputs();
        foreach (var entity in Entities)
        {
            var record = outputs[$"src/Generated/records/{Folder(entity)}/{entity}Record.cs"];
            var service = outputs[$"src/Generated/services/{Folder(entity)}/{entity}RecordService.cs"];
            var header = $"// {entity}Record\n// Generated from the billing model; do not edit.\n";
            Assert.StartsWith(header, record, StringComparison.Ordinal);
            Assert.StartsWith(header, service, StringComparison.Ordinal);
        }

        Assert.Equal(2 * Entities.Length, first.UnitsRendered);
        var clean = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(clean.Diagnostics, d => d.Rule == "x/class-name-length" || d.Severity == DiagnosticSeverity.Error);

        // The library's parameter changes: every entity unit of both packs renders again, and nothing else. The rule's findings are
        // errors, which stop generation's validate stage, so the project lowers them to warnings to see the plan.
        var settingsPath = Path.Combine(repo.Repo.ModelRoot, "maquettiste.json");
        var node = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
        node["packs"]!.AsObject()["naming"] = new JsonObject { ["parameters"] = new JsonObject { ["suffix"] = "RecordEntry" } };
        var validation = node["validation"] as JsonObject ?? [];
        node["validation"] = validation;
        validation["rules"] = new JsonObject { ["x/class-name-length"] = "warning" };
        File.WriteAllBytes(settingsPath, TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));
        await repo.Store.GetSnapshotAsync(Ct);

        var plan = await IncrementalTests.PlanAsync(repo);
        var rendered = plan.Units.Where(u => !u.Skipped).Select(u => u.Key).ToList();
        Assert.Equal(2 * Entities.Length, rendered.Count);
        Assert.All(rendered, key => Assert.True(key.StartsWith("records/record:", StringComparison.Ordinal) || key.StartsWith("services/service:", StringComparison.Ordinal), key));

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        var flagged = report.Diagnostics.Where(d => d.Rule == "x/class-name-length")
            .Select(d => d.Message[("The class name ".Length)..d.Message.IndexOf(' ', "The class name ".Length)]).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["CustomerNoteRecordEntry", "InvoiceLineRecordEntry", "InvoiceNoteRecordEntry", "RevenueMonthRecordEntry"], flagged);
    }

    [Fact]
    public async Task A_range_the_library_does_not_satisfy_is_MQ6043()
    {
        await using var repo = Create(recordsRange: "^2.0.0");
        await repo.Store.GetSnapshotAsync(Ct);

        var result = await repo.RunAsync();

        Assert.Equal(RunOutcome.Invalid, result.Outcome);
        var error = Assert.Single(result.Diagnostics, d => d.Rule == "MQ6043");
        Assert.Equal((".maquettiste/templates/records/pack.json", "/uses/0/version"), (error.FilePath, error.JsonPointer));
        Assert.DoesNotContain(repo.Outputs().Keys, path => path.StartsWith("src/Generated/records/", StringComparison.Ordinal)
            || path.StartsWith("src/Generated/services/", StringComparison.Ordinal));
    }
}
