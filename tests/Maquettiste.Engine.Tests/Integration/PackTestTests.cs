using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Acceptance for the tests of packs (next-phase-2-design.md section 5.4), over a billing copy whose <c>records</c> pack, from
/// <c>tests/fixtures/integration/pack-tests/</c>, holds the <c>billing-small</c> case and <c>tests/helpers.test.js</c>: the case passes,
/// fails with a diff when the template changes and is updated, a parameter variant shares its model, and a case of a pack that uses a
/// library runs with the library. The project itself never changes.
/// </summary>
public sealed class PackTestTests
{
    private const string Records = ".maquettiste/templates/records/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Source(string name) => Fixtures.Path("integration", "pack-tests", name);

    /// <summary>Billing with the fixture's <c>records</c> pack, which the project generates under <c>src/Generated</c>.</summary>
    private static E2ERepo Create()
    {
        var repo = E2ERepo.Create(demo: false, settings: settings =>
        {
            E2ERepo.RecordOff(settings);
            var packs = settings["packs"]!.AsObject();
            packs["e2e"]!["enabled"] = false;
            packs["records"] = new JsonObject { ["output"] = "src/Generated" };
        });
        E2ERepo.CopyTree(Source("records"), repo.Repo.PathOf(Records));
        return repo;
    }

    private static Task<PackTestReport> TestAsync(E2ERepo repo, bool update = false, params string[] cases) =>
        repo.Service.TestPackAsync(["records"], new PackTestOptions { Cases = cases, Update = update }, null, Ct);

    /// <summary>The project's outputs, manifests and unit states (the cache folder), path to SHA-256.</summary>
    private static SortedDictionary<string, string> Project(E2ERepo repo)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, bytes) in repo.Tree())
            files[path] = E2ERepo.Sha(bytes);
        foreach (var path in Directory.EnumerateFiles(repo.Repo.CacheDirectory, "*", SearchOption.AllDirectories))
            files["cache:" + Path.GetRelativePath(repo.Repo.CacheDirectory, path).Replace('\\', '/')] = E2ERepo.Sha(File.ReadAllBytes(path));
        return files;
    }

    private static string Describe(PackCaseResult result) =>
        result.Outcome + ": " + string.Join("\n", result.Diagnostics.Select(d => d.Rule + " " + d.FilePath + ": " + d.Message));

    [Fact]
    public async Task A_case_passes_fails_with_a_diff_and_updates()
    {
        await using var repo = Create();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.ApplyAsync());
        Assert.Equal("public sealed record InvoiceRecord;\n", repo.Repo.ReadFile("src/Generated/Billing/InvoiceRecord.cs"));
        var before = Project(repo);

        // One case passed and two tests passed.
        var report = await TestAsync(repo);
        var pack = Assert.Single(report.Packs);
        var passed = Assert.Single(pack.Cases);
        Assert.True(passed.Outcome == PackTestOutcome.Passed, Describe(passed));
        Assert.Equal("billing-small", passed.Name);
        Assert.Equal(["record_name adds the suffix", "record_name keeps acronyms"], pack.Tests.Select(t => t.Name));
        Assert.All(pack.Tests, t => Assert.Equal(PackTestOutcome.Passed, t.Outcome));
        Assert.Equal((3, 0, 0), (report.Passed, report.Failed, report.Updated));

        // A line added to the unit's template fails the case with MQ6051, each diff showing the line.
        var template = repo.Repo.PathOf(Records + "record.scriban");
        await File.WriteAllTextAsync(template, "// A billing record.\npublic sealed record {{ record_name entity }};\n", Ct);
        var failed = Assert.Single(Assert.Single((await TestAsync(repo)).Packs).Cases);
        Assert.Equal(PackTestOutcome.Failed, failed.Outcome);
        Assert.Equal(["Billing/CustomerRecord.cs", "Billing/InvoiceRecord.cs"], failed.Files.Select(f => f.Path));
        Assert.All(failed.Files, f =>
        {
            Assert.Equal(PackCaseFileChange.Differs, f.Change);
            Assert.Contains("\n+// A billing record.\n", f.Diff, StringComparison.Ordinal);
        });
        Assert.Equal("MQ6051", Assert.Single(failed.Diagnostics).Rule);

        // Update rewrites both files, and a rerun passes.
        var updated = Assert.Single(Assert.Single((await TestAsync(repo, update: true)).Packs).Cases);
        Assert.Equal(PackTestOutcome.Updated, updated.Outcome);
        Assert.Equal("// A billing record.\npublic sealed record InvoiceRecord;\n", repo.Repo.ReadFile(Records + "tests/billing-small/expected/Billing/InvoiceRecord.cs"));
        Assert.Equal("// A billing record.\npublic sealed record CustomerRecord;\n", repo.Repo.ReadFile(Records + "tests/billing-small/expected/Billing/CustomerRecord.cs"));
        var rerun = Assert.Single(Assert.Single((await TestAsync(repo)).Packs).Cases);
        Assert.True(rerun.Outcome == PackTestOutcome.Passed, Describe(rerun));

        // The project's own records output, manifest and unit states are byte-identical to before.
        Assert.Equal(before, Project(repo));

        // record_name without the suffix fails both helper tests with MQ6052 at their statements.
        var helpers = repo.Repo.PathOf(Records + "helpers.js");
        await File.WriteAllTextAsync(helpers, (await File.ReadAllTextAsync(helpers, Ct)).Replace(" + maquettiste.params.suffix", "", StringComparison.Ordinal), Ct);
        var broken = Assert.Single((await TestAsync(repo)).Packs);
        Assert.All(broken.Tests, t => Assert.Equal((PackTestOutcome.Failed, "tests/helpers.test.js"), (t.Outcome, t.File)));
        Assert.Equal([2, 5], broken.Tests.Select(t => t.Line ?? 0));
        Assert.Equal(2, broken.Diagnostics.Count(d => d.Rule == "MQ6052" && d.FilePath == Records + "tests/helpers.test.js"));
    }

    [Fact]
    public async Task A_parameter_variant_shares_its_model()
    {
        await using var repo = Create();
        var rows = Records + "tests/billing-small-rows/";
        E2ERepo.CopyTree(Source("billing-small-rows"), repo.Repo.PathOf(rows));

        var result = Assert.Single(Assert.Single((await TestAsync(repo, cases: "billing-small-rows")).Packs).Cases);

        Assert.True(result.Outcome == PackTestOutcome.Passed, Describe(result));
        Assert.Equal(("billing-small-rows", "The billing-small model with the Row suffix."), (result.Name, result.Description));
        var folder = repo.Repo.PathOf(rows);
        Assert.Equal(["case.json", "expected/Billing/CustomerRow.cs", "expected/Billing/InvoiceRow.cs"],
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_case_of_a_pack_that_uses_a_library_runs_with_the_library()
    {
        await using var repo = Create();
        E2ERepo.CopyTree(Source("naming"), repo.Repo.PathOf(".maquettiste/templates/naming"));
        E2ERepo.CopyTree(Source("named-records"), repo.Repo.PathOf(Records));

        var result = Assert.Single(Assert.Single((await TestAsync(repo, cases: "billing-small")).Packs).Cases);
        Assert.True(result.Outcome == PackTestOutcome.Passed, Describe(result));

        // The library's helper changes: the case fails with MQ6051.
        var helpers = repo.Repo.PathOf(".maquettiste/templates/naming/helpers.js");
        await File.WriteAllTextAsync(helpers, (await File.ReadAllTextAsync(helpers, Ct)).Replace("+ maquettiste.params.suffix", "+ \"Model\"", StringComparison.Ordinal), Ct);
        var failed = Assert.Single(Assert.Single((await TestAsync(repo, cases: "billing-small")).Packs).Cases);
        Assert.Equal(PackTestOutcome.Failed, failed.Outcome);
        Assert.Equal("MQ6051", Assert.Single(failed.Diagnostics).Rule);
        Assert.Equal(
            [("Billing/CustomerModel.cs", PackCaseFileChange.Added), ("Billing/CustomerRecord.cs", PackCaseFileChange.Missing),
             ("Billing/InvoiceModel.cs", PackCaseFileChange.Added), ("Billing/InvoiceRecord.cs", PackCaseFileChange.Missing)],
            failed.Files.Select(f => (f.Path, f.Change)));
    }
}
