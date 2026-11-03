using Maquettiste.Engine.Generation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// A project whose <c>records</c> pack (one <c>each entity</c> unit writing <c>&lt;Package&gt;/&lt;Name&gt;Record.cs</c> through a
/// <c>record_name</c> helper) holds the <c>billing-small</c> case and a helper test file (next-phase-2-design.md section 5.4).
/// </summary>
internal sealed class PackTestKit : IAsyncDisposable
{
    public const string Case = ".maquettiste/templates/records/tests/billing-small/";
    public const string Pack = ".maquettiste/templates/records/";

    public const string Helpers = """
        function pascal(text) { return String(text).charAt(0).toUpperCase() + String(text).slice(1); }
        maquettiste.helper("record_name", (element) => pascal(element.name) + maquettiste.params.suffix);

        """;

    public const string HelperTests = """
        maquettiste.test("record_name adds the suffix", (assert, helpers) => {
          assert.equal(helpers.record_name({ name: "invoice" }), "InvoiceRecord");
        });
        maquettiste.test("record_name keeps acronyms", (assert, helpers) => {
          assert.equal(helpers.record_name({ name: "VATRate" }), "VATRateRecord");
        });

        """;

    private PackTestKit()
    {
        Repo = new TempRepo();
        Services = EngineServices.Create(Repo.Options);
        Store = new ModelStore(Repo.Options, Services);
        Service = new GenerationService(Store, Repo.Options, Services);
    }

    public TempRepo Repo { get; }

    public EngineServices Services { get; }

    public ModelStore Store { get; }

    public GenerationService Service { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The project, the pack and the case with its expected tree.</summary>
    public static PackTestKit Create(bool expected = true, bool helperTests = true)
    {
        var kit = new PackTestKit();
        kit.Repo.WriteFile(".maquettiste/maquettiste.json", """{ "formatVersion": 1, "name": "shop", "outputs": { "allow": [ { "path": "gen" } ] }, "packs": { "records": { "output": "gen" } } }""");
        kit.Repo.WriteFile(Pack + "pack.json", """
            { "name": "records", "version": "1.0.0", "engine": ">=0.1", "parameters": { "suffix": "Record" },
              "units": [ { "id": "record", "template": "record.scriban", "for": "each entity",
                "output": "{{ entity.package.name }}/{{ record_name entity }}.cs" } ] }
            """);
        kit.Repo.WriteFile(Pack + "record.scriban", "public sealed record {{ record_name entity }};\n");
        kit.Repo.WriteFile(Pack + "helpers.js", Helpers);
        if (helperTests)
            kit.Repo.WriteFile(Pack + "tests/helpers.test.js", HelperTests);
        kit.WriteCase("billing-small", expected);
        return kit;
    }

    /// <summary>A case with a package, two entities and an enum; with <paramref name="expected"/>, its expected tree.</summary>
    public void WriteCase(string name, bool expected = true)
    {
        var folder = Pack + "tests/" + name + "/";
        Repo.WriteFile(folder + "maquettiste.json", $$"""{ "formatVersion": 1, "name": "{{name}}", "packs": { "records": { "output": "out" } } }""");
        Repo.WriteFile(folder + "model/packages/billing.json", """{ "kind": "package", "id": "01JA0000000000000000000P01", "name": "Billing" }""");
        Repo.WriteFile(folder + "model/entities/invoice.json", Entity("01JA0000000000000000000E01", "Invoice", "01JA0000000000000000000A01"));
        Repo.WriteFile(folder + "model/entities/customer.json", Entity("01JA0000000000000000000E02", "Customer", "01JA0000000000000000000A02"));
        Repo.WriteFile(folder + "model/enums/invoice-status.json", """
            { "kind": "enum", "id": "01JA0000000000000000000N01", "name": "InvoiceStatus", "package": "01JA0000000000000000000P01",
              "members": [ { "id": "01JA0000000000000000000V01", "name": "Draft", "value": 0 }, { "id": "01JA0000000000000000000V02", "name": "Sent", "value": 1 } ] }
            """);
        if (expected)
        {
            Repo.WriteFile(folder + "expected/Billing/InvoiceRecord.cs", "public sealed record InvoiceRecord;\n");
            Repo.WriteFile(folder + "expected/Billing/CustomerRecord.cs", "public sealed record CustomerRecord;\n");
        }
    }

    public static string Entity(string id, string name, string attribute) => $$"""
        { "kind": "entity", "id": "{{id}}", "name": "{{name}}", "package": "01JA0000000000000000000P01", "key": { "attributes": [ "{{attribute}}" ] },
          "attributes": [ { "id": "{{attribute}}", "name": "id", "type": "uuid", "required": true } ] }
        """;

    public Task<PackTestReport> TestAsync(PackTestOptions? options = null, params string[] packs) =>
        Service.TestPackAsync(packs, options ?? new PackTestOptions(), null, Ct);

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Repo.Dispose();
    }
}

public sealed class PackTestRunnerTests
{
    private const string Case = PackTestKit.Case;

    private static PackCaseResult OnlyCase(PackTestReport report) => Assert.Single(Assert.Single(report.Packs).Cases);

    [Fact]
    public async Task A_case_whose_output_matches_passes()
    {
        await using var kit = PackTestKit.Create(helperTests: false);

        var report = await kit.TestAsync();

        var result = OnlyCase(report);
        Assert.True(result.Diagnostics.Count == 0, string.Join("\n", result.Diagnostics.Select(d => d.Rule + " " + d.FilePath + " " + d.JsonPointer + ": " + d.Message)));
        Assert.Equal(("billing-small", PackTestOutcome.Passed), (result.Name, result.Outcome));
        Assert.Empty(result.Files);
        Assert.Equal((1, 0, 0), (report.Passed, report.Failed, report.Updated));
    }

    [Fact]
    public async Task A_changed_template_fails_with_the_differing_path_and_a_diff()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(PackTestKit.Pack + "record.scriban", "// generated\npublic sealed record {{ record_name entity }};\n");

        var result = OnlyCase(await kit.TestAsync());

        Assert.Equal(PackTestOutcome.Failed, result.Outcome);
        Assert.Equal(["Billing/CustomerRecord.cs", "Billing/InvoiceRecord.cs"], result.Files.Select(f => f.Path));
        Assert.All(result.Files, f => Assert.Equal(PackCaseFileChange.Differs, f.Change));
        Assert.Contains("+// generated", result.Files[1].Diff, StringComparison.Ordinal);
        Assert.StartsWith("--- a/Billing/InvoiceRecord.cs\n", result.Files[1].Diff, StringComparison.Ordinal);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("MQ6051", ".maquettiste/templates/records/tests/billing-small"), (error.Rule, error.FilePath));
        Assert.Contains("Billing/InvoiceRecord.cs (differs)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_produced_but_not_expected_is_added()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        File.Delete(kit.Repo.PathOf(Case + "expected/Billing/CustomerRecord.cs"));

        var result = OnlyCase(await kit.TestAsync());

        var file = Assert.Single(result.Files);
        Assert.Equal(("Billing/CustomerRecord.cs", PackCaseFileChange.Added, (string?)null), (file.Path, file.Change, file.Diff));
        Assert.Equal("MQ6051", Assert.Single(result.Diagnostics).Rule);
    }

    [Fact]
    public async Task A_file_expected_but_not_produced_is_missing()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(Case + "expected/Billing/PaymentRecord.cs", "public sealed record PaymentRecord;\n");

        var result = OnlyCase(await kit.TestAsync());

        var file = Assert.Single(result.Files);
        Assert.Equal(("Billing/PaymentRecord.cs", PackCaseFileChange.Missing), (file.Path, file.Change));
        Assert.Equal(PackTestOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Update_rewrites_expected_and_deletes_stale_files()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(Case + "expected/Billing/InvoiceRecord.cs", "stale\n");
        kit.Repo.WriteFile(Case + "expected/Old/PaymentRecord.cs", "gone\n");

        var report = await kit.TestAsync(new PackTestOptions { Update = true });

        var result = OnlyCase(report);
        Assert.Equal(PackTestOutcome.Updated, result.Outcome);
        Assert.Equal((0, 0, 1), (report.Passed, report.Failed, report.Updated));
        Assert.Equal("public sealed record InvoiceRecord;\n", kit.Repo.ReadFile(Case + "expected/Billing/InvoiceRecord.cs"));
        Assert.False(Directory.Exists(kit.Repo.PathOf(Case + "expected/Old")));
        Assert.Equal(PackTestOutcome.Passed, OnlyCase(await kit.TestAsync()).Outcome);
        Assert.Equal(PackTestOutcome.Passed, OnlyCase(await kit.TestAsync(new PackTestOptions { Update = true })).Outcome);
    }

    [Fact]
    public async Task A_case_json_reuses_another_cases_model_with_parameters()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        var rows = PackTestKit.Pack + "tests/billing-small-rows/";
        kit.Repo.WriteFile(rows + "case.json", """{ "description": "Row suffix", "model": "billing-small", "parameters": { "suffix": "Row" } }""");
        kit.Repo.WriteFile(rows + "expected/Billing/InvoiceRow.cs", "public sealed record InvoiceRow;\n");
        kit.Repo.WriteFile(rows + "expected/Billing/CustomerRow.cs", "public sealed record CustomerRow;\n");

        var report = await kit.TestAsync();

        var cases = Assert.Single(report.Packs).Cases;
        Assert.Equal(["billing-small", "billing-small-rows"], cases.Select(c => c.Name));
        Assert.All(cases, c => Assert.Equal(PackTestOutcome.Passed, c.Outcome));
        Assert.Equal("Row suffix", cases[1].Description);
        Assert.Equal(PackTestOutcome.Passed, OnlyCase(await kit.TestAsync(new PackTestOptions { Cases = ["billing-small-rows"] })).Outcome);

        kit.Repo.WriteFile(rows + "case.json", """{ "model": "billing-none" }""");
        var missing = (await kit.TestAsync()).Packs[0].Cases[1];
        Assert.Equal(PackTestOutcome.Error, missing.Outcome);
        Assert.Equal(("MQ6053", "/model"), (missing.Diagnostics[0].Rule, missing.Diagnostics[0].JsonPointer));

        kit.Repo.WriteFile(rows + "case.json", """{ "model": "billing-small", "extra": 1 }""");
        Assert.Equal("MQ6053", (await kit.TestAsync()).Packs[0].Cases[1].Diagnostics[0].Rule);
    }

    [Fact]
    public async Task A_case_without_a_project_is_MQ6053()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        File.Delete(kit.Repo.PathOf(Case + "maquettiste.json"));

        var result = OnlyCase(await kit.TestAsync());

        Assert.Equal(PackTestOutcome.Error, result.Outcome);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("MQ6053", error.Rule);
        Assert.Contains("has no project", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_case_without_expected_is_MQ6053_unless_updating()
    {
        await using var kit = PackTestKit.Create(expected: false, helperTests: false);

        var result = OnlyCase(await kit.TestAsync());
        Assert.Equal(PackTestOutcome.Error, result.Outcome);
        Assert.Contains("no expected/", Assert.Single(result.Diagnostics).Message, StringComparison.Ordinal);

        var updated = OnlyCase(await kit.TestAsync(new PackTestOptions { Update = true }));
        Assert.Equal(PackTestOutcome.Updated, updated.Outcome);
        Assert.All(updated.Files, f => Assert.Equal(PackCaseFileChange.Added, f.Change));
        Assert.Equal("public sealed record CustomerRecord;\n", kit.Repo.ReadFile(Case + "expected/Billing/CustomerRecord.cs"));
    }

    [Fact]
    public async Task A_case_holding_templates_is_MQ6053()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(Case + "templates/other/pack.json", "{}");

        var result = OnlyCase(await kit.TestAsync());

        Assert.Equal(PackTestOutcome.Error, result.Outcome);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("MQ6053", Case + "templates"), (error.Rule, error.FilePath));
    }

    [Fact]
    public async Task A_case_whose_model_is_invalid_errors_with_the_runs_diagnostics()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(Case + "model/entities/invoice.json", """{ "kind": "entity", "id": "01JA0000000000000000000E01", "name": "Invoice", "package": "01JA000000000000000000NONE" }""");

        var result = OnlyCase(await kit.TestAsync());

        Assert.Equal(PackTestOutcome.Error, result.Outcome);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(result.Diagnostics, d => d.FilePath == Case + "model/entities/invoice.json");
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule == "MQ6051");
    }

    [Fact]
    public async Task A_case_runs_the_used_packs_of_the_project()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(".maquettiste/templates/naming/pack.json", """{ "name": "naming", "version": "1.0.0", "engine": ">=0.1", "parameters": { "suffix": "Model" } }""");
        kit.Repo.WriteFile(".maquettiste/templates/naming/helpers.js", """maquettiste.helper("class_name", (name) => String(name) + maquettiste.params.suffix);""" + "\n");
        kit.Repo.WriteFile(PackTestKit.Pack + "pack.json", """
            { "name": "records", "version": "1.0.0", "engine": ">=0.1", "uses": [ { "pack": "naming", "version": ">=1.0" } ],
              "units": [ { "id": "record", "template": "record.scriban", "for": "each entity",
                "output": "{{ entity.package.name }}/{{ naming.class_name entity.name }}.cs" } ] }
            """);
        kit.Repo.WriteFile(PackTestKit.Pack + "record.scriban", "public sealed record {{ naming.class_name entity.name }};\n");
        File.Delete(kit.Repo.PathOf(PackTestKit.Pack + "helpers.js"));
        Directory.Delete(kit.Repo.PathOf(Case + "expected"), recursive: true);
        kit.Repo.WriteFile(Case + "expected/Billing/InvoiceModel.cs", "public sealed record InvoiceModel;\n");
        kit.Repo.WriteFile(Case + "expected/Billing/CustomerModel.cs", "public sealed record CustomerModel;\n");

        Assert.Equal(PackTestOutcome.Passed, OnlyCase(await kit.TestAsync(null, "records")).Outcome);

        kit.Repo.WriteFile(".maquettiste/templates/naming/helpers.js", """maquettiste.helper("class_name", (name) => String(name) + "Dto");""" + "\n");
        var failed = OnlyCase(await kit.TestAsync(null, "records"));
        Assert.Equal(PackTestOutcome.Failed, failed.Outcome);
        Assert.Equal("MQ6051", Assert.Single(failed.Diagnostics).Rule);
    }

    [Fact]
    public async Task A_case_leaves_the_project_untouched()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(".maquettiste/model/packages/shop.json", """{ "kind": "package", "id": "01JA0000000000000000000P09", "name": "Shop" }""");
        kit.Repo.WriteFile(".maquettiste/model/entities/order.json", PackTestKit.Entity("01JA0000000000000000000E09", "Order", "01JA0000000000000000000A09").Replace("01JA0000000000000000000P01", "01JA0000000000000000000P09", StringComparison.Ordinal));
        var run = await kit.Service.RunAsync(new GenerationRequest(), null, PackTestKit.Ct);
        Assert.Equal(RunOutcome.Succeeded, run.Outcome);
        kit.Service.ReuseLastRun = true;
        Assert.Equal(RunOutcome.Succeeded, (await kit.Service.RunAsync(new GenerationRequest(), null, PackTestKit.Ct)).Outcome);
        var before = Snapshot(kit);

        Assert.Equal(PackTestOutcome.Passed, OnlyCase(await kit.TestAsync()).Outcome);
        kit.Repo.WriteFile(PackTestKit.Pack + "record.scriban", "// v2\npublic sealed record {{ record_name entity }};\n");
        Assert.Equal(PackTestOutcome.Failed, OnlyCase(await kit.TestAsync()).Outcome);
        kit.Repo.WriteFile(PackTestKit.Pack + "record.scriban", "public sealed record {{ record_name entity }};\n");

        Assert.Equal(before, Snapshot(kit));
    }

    private static string Snapshot(PackTestKit kit) => string.Join("\n", kit.Repo.ListFiles()
        .Concat(Directory.EnumerateFiles(kit.Repo.CacheDirectory, "*", SearchOption.AllDirectories).Select(f => "cache:" + Path.GetRelativePath(kit.Repo.CacheDirectory, f)))
        .Order(StringComparer.Ordinal)
        .Select(p => p + "=" + (p.StartsWith("cache:", StringComparison.Ordinal)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(kit.Repo.CacheDirectory, p[6..]))))
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(kit.Repo.PathOf(p)))))));

    [Fact]
    public async Task A_pack_without_units_holding_cases_is_MQ6053()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(PackTestKit.Pack + "pack.json", """{ "name": "records", "version": "1.0.0", "engine": ">=0.1", "parameters": { "suffix": "Record" } }""");

        var result = OnlyCase(await kit.TestAsync(null, "records"));

        Assert.Equal(PackTestOutcome.Error, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6053" && d.Message.Contains("no units", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Results_are_identical_with_one_and_eight_jobs()
    {
        await using var kit = PackTestKit.Create();
        kit.Repo.WriteFile(PackTestKit.Pack + "record.scriban", "// v2\npublic sealed record {{ record_name entity }};\n");

        var one = await kit.TestAsync(new PackTestOptions { Jobs = 1 });
        var eight = await kit.TestAsync(new PackTestOptions { Jobs = 8 });

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(one), System.Text.Json.JsonSerializer.Serialize(eight));
        Assert.Equal(PackTestOutcome.Failed, OnlyCase(one).Outcome);
    }

    [Fact]
    public async Task Helper_tests_pass_and_a_failing_one_is_MQ6052_at_its_statement()
    {
        await using var kit = PackTestKit.Create();

        var report = await kit.TestAsync();
        var pack = Assert.Single(report.Packs);
        Assert.Equal(["record_name adds the suffix", "record_name keeps acronyms"], pack.Tests.Select(t => t.Name));
        Assert.All(pack.Tests, t => Assert.Equal((PackTestOutcome.Passed, "tests/helpers.test.js"), (t.Outcome, t.File)));
        Assert.Equal((3, 0), (report.Passed, report.Failed));

        kit.Repo.WriteFile(PackTestKit.Pack + "helpers.js", PackTestKit.Helpers.Replace(" + maquettiste.params.suffix", "", StringComparison.Ordinal));
        var failed = (await kit.TestAsync(new PackTestOptions())).Packs[0];
        Assert.Equal([2, 5], failed.Tests.Select(t => t.Line ?? 0));
        Assert.All(failed.Tests, t => Assert.Equal(PackTestOutcome.Failed, t.Outcome));
        Assert.Equal("expected \"InvoiceRecord\", got \"Invoice\"", failed.Tests[0].Message);
        Assert.Equal(2, failed.Diagnostics.Count(d => d.Rule == "MQ6052" && d.FilePath == PackTestKit.Pack + "tests/helpers.test.js"));

        // Selecting cases runs no helper tests.
        Assert.Empty((await kit.TestAsync(new PackTestOptions { Cases = ["billing-small"] })).Packs[0].Tests);
    }

    [Fact]
    public async Task A_malformed_test_file_is_MQ6053_and_the_other_files_still_run()
    {
        await using var kit = PackTestKit.Create();
        kit.Repo.WriteFile(PackTestKit.Pack + "tests/broken.test.js", "maquettiste.test(\"a\", () => {});\nmaquettiste.helper(\"x\", () => 1);\n");
        kit.Repo.WriteFile(PackTestKit.Pack + "tests/syntax.test.js", "maquettiste.test(\"a\", () => {\n");

        var report = await kit.TestAsync();

        var pack = Assert.Single(report.Packs);
        Assert.Equal(2, pack.Tests.Count);
        Assert.Equal([PackTestKit.Pack + "tests/broken.test.js", PackTestKit.Pack + "tests/syntax.test.js"],
            pack.Diagnostics.Where(d => d.Rule == "MQ6053").Select(d => d.FilePath));
        Assert.Equal(2, pack.Diagnostics.Single(d => d.FilePath!.EndsWith("broken.test.js", StringComparison.Ordinal)).Line);
        Assert.Equal((3, 2), (report.Passed, report.Failed));
    }

    /// <summary>A symbolic link at <paramref name="link"/>; skips the test where links cannot be made.</summary>
    private static void Link(string link, string target, bool folder)
    {
        try
        {
            if (folder)
                Directory.CreateSymbolicLink(link, target);
            else
                File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic links cannot be made here: " + e.Message);
        }
    }

    /// <summary>Every file under a folder, relative with '/', and its text, ordinal.</summary>
    private static string Tree(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/') + ": " + File.ReadAllText(f)).Order(StringComparer.Ordinal));

    [Fact]
    public async Task A_link_under_tests_is_MQ6053_and_what_it_points_at_is_never_read()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile("ledger-secret.txt", "ledger secret\n");
        var expected = kit.Repo.PathOf(Case + "expected/Billing/InvoiceRecord.cs");
        File.Delete(expected);
        Link(expected, kit.Repo.PathOf("ledger-secret.txt"), folder: false);

        var report = await kit.TestAsync();

        var pack = Assert.Single(report.Packs);
        Assert.Empty(pack.Cases);
        var error = Assert.Single(pack.Diagnostics);
        Assert.Equal(("MQ6053", Case + "expected/Billing/InvoiceRecord.cs"), (error.Rule, error.FilePath));
        Assert.DoesNotContain("ledger secret", System.Text.Json.JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.Equal(1, report.Failed);
    }

    [Fact]
    public async Task Update_refuses_a_link_under_tests_and_writes_nothing_through_it()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(PackTestKit.Pack + "partials/billing.scriban", "billing\n");
        Directory.Delete(kit.Repo.PathOf(Case + "expected/Billing"), recursive: true);
        Link(kit.Repo.PathOf(Case + "expected/Billing"), Path.Combine("..", "..", "..", "partials"), folder: true);

        var report = await kit.TestAsync(new PackTestOptions { Update = true });

        var pack = Assert.Single(report.Packs);
        Assert.Empty(pack.Cases);
        Assert.Equal(("MQ6053", Case + "expected/Billing"), (Assert.Single(pack.Diagnostics).Rule, pack.Diagnostics[0].FilePath));
        Assert.Equal("billing.scriban: billing\n", Tree(kit.Repo.PathOf(PackTestKit.Pack + "partials")));
        Assert.NotNull(new DirectoryInfo(kit.Repo.PathOf(Case + "expected/Billing")).LinkTarget);
        await Assert.ThrowsAsync<PackPathException>(() => PackAuthoring.ReplaceTreeAsync(kit.Services, "records", "tests/billing-small/expected",
            new Dictionary<string, byte[]>(), PackTestKit.Ct));
        Assert.Equal("billing.scriban: billing\n", Tree(kit.Repo.PathOf(PackTestKit.Pack + "partials")));
    }

    [Fact]
    public async Task Update_checks_every_path_before_it_changes_anything()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(Case + "expected/Old/PaymentRecord.cs", "gone\n");
        var expected = kit.Repo.PathOf(Case + "expected");
        var before = Tree(expected);
        var produced = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Billing/InvoiceRecord.cs"] = "public sealed record InvoiceLine;\n"u8.ToArray(),
            ["Billing/LateFeeRecord.cs"] = "public sealed record LateFeeRecord;\n"u8.ToArray(),
            ["Billing\\RefundRecord.cs"] = "public sealed record RefundRecord;\n"u8.ToArray(),
        };

        await Assert.ThrowsAsync<PackPathException>(() => PackAuthoring.ReplaceTreeAsync(kit.Services, "records", "tests/billing-small/expected", produced,
            PackTestKit.Ct));

        Assert.Equal(before, Tree(expected));
    }

    [Fact]
    public async Task A_case_folder_whose_name_is_not_a_case_name_is_MQ6053_and_does_not_run()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.WriteCase("Billing_Large");

        var report = await kit.TestAsync();

        var cases = Assert.Single(report.Packs).Cases;
        Assert.Equal(["Billing_Large", "billing-small"], cases.Select(c => c.Name));
        Assert.Equal(PackTestOutcome.Error, cases[0].Outcome);
        Assert.Empty(cases[0].Files);
        var error = Assert.Single(cases[0].Diagnostics);
        Assert.Equal(("MQ6053", PackTestKit.Pack + "tests/Billing_Large"), (error.Rule, error.FilePath));
        Assert.Equal(PackTestOutcome.Passed, cases[1].Outcome);
        Assert.Equal((1, 1), (report.Passed, report.Failed));
    }
}
