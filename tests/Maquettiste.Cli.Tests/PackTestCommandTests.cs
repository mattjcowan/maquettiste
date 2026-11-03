using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary><c>maquettiste pack test</c> (next-phase-2-design.md section 5.4).</summary>
public sealed class PackTestCommandTests
{
    internal const string Pack = ".maquettiste/templates/records/";
    internal const string Case = Pack + "tests/billing-small/";

    /// <summary>
    /// Adds a <c>records</c> pack to the billing project: one <c>each entity</c> unit through a <c>record_name</c> helper, the
    /// <c>billing-small</c> case (a package and two entities) with its expected tree, and a helper test file.
    /// </summary>
    internal static void Records(CliRepo repo)
    {
        repo.Write(Pack + "pack.json", """
            { "name": "records", "version": "1.0.0", "engine": ">=0.1", "parameters": { "suffix": "Record" },
              "units": [ { "id": "record", "template": "record.scriban", "for": "each entity",
                "output": "{{ entity.package.name }}/{{ record_name entity }}.cs" } ] }
            """);
        repo.Write(Pack + "record.scriban", "public sealed record {{ record_name entity }};\n");
        repo.Write(Pack + "helpers.js", "maquettiste.helper(\"record_name\", (element) => String(element.name).charAt(0).toUpperCase() + String(element.name).slice(1) + maquettiste.params.suffix);\n");
        repo.Write(Pack + "tests/helpers.test.js", """
            maquettiste.test("record_name adds the suffix", (assert, helpers) => {
              assert.equal(helpers.record_name({ name: "invoice" }), "InvoiceRecord");
            });

            """);
        repo.Write(Case + "maquettiste.json", """{ "formatVersion": 1, "name": "billing-small", "packs": { "records": { "output": "out" } } }""");
        repo.Write(Case + "model/packages/billing.json", """{ "kind": "package", "id": "01JA0000000000000000000P01", "name": "Billing" }""");
        foreach (var (id, name, attribute) in new[] { ("01JA0000000000000000000E01", "Invoice", "01JA0000000000000000000A01"), ("01JA0000000000000000000E02", "Customer", "01JA0000000000000000000A02") })
        {
            repo.Write(Case + "model/entities/" + name.ToLowerInvariant() + ".json", $$"""
                { "kind": "entity", "id": "{{id}}", "name": "{{name}}", "package": "01JA0000000000000000000P01", "key": { "attributes": [ "{{attribute}}" ] },
                  "attributes": [ { "id": "{{attribute}}", "name": "id", "type": "uuid", "required": true } ] }
                """);
            repo.Write(Case + "expected/Billing/" + name + "Record.cs", "public sealed record " + name + "Record;\n");
        }
    }

    [Fact]
    public async Task Pack_test_exits_0_when_every_case_and_test_passes()
    {
        using var repo = CliRepo.Billing();
        Records(repo);

        var result = await repo.RunAsync("pack", "test");

        Assert.True(result.ExitCode == 0, result.Error + result.Out);
        Assert.Equal("records\n  PASS case billing-small\n  PASS test tests/helpers.test.js: record_name adds the suffix\n2 passed, 0 failed\n", result.Out);
        var json = JsonNode.Parse((await repo.RunAsync("pack", "test", "records", "--format", "json")).Out)!;
        Assert.Equal((2, 0), ((int)json["passed"]!, (int)json["failed"]!));
        Assert.Equal("passed", (string)json["packs"]![0]!["cases"]![0]!["outcome"]!);
    }

    [Fact]
    public async Task Pack_test_exits_1_on_a_failing_case_and_prints_the_diff()
    {
        using var repo = CliRepo.Billing();
        Records(repo);
        repo.Write(Pack + "record.scriban", "// generated\npublic sealed record {{ record_name entity }};\n");

        var result = await repo.RunAsync("pack", "test", "records");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("  FAIL case billing-small\n    differs Billing/CustomerRecord.cs\n      --- a/Billing/CustomerRecord.cs\n", result.Out, StringComparison.Ordinal);
        Assert.Contains("      +// generated\n", result.Out, StringComparison.Ordinal);
        Assert.EndsWith("1 passed, 1 failed\n", result.Out, StringComparison.Ordinal);

        repo.Write(Pack + "record.scriban", "public sealed record {{ record_name entity }};\n");
        repo.Write(Pack + "helpers.js", "maquettiste.helper(\"record_name\", (element) => String(element.name));\n");
        var helper = await repo.RunAsync("pack", "test", "records");
        Assert.Equal(1, helper.ExitCode);
        Assert.Contains("  FAIL test tests/helpers.test.js: record_name adds the suffix\n    tests/helpers.test.js:2:", helper.Out, StringComparison.Ordinal);
        Assert.Contains("expected \"InvoiceRecord\", got \"invoice\"", helper.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_rewrites_and_exits_0()
    {
        using var repo = CliRepo.Billing();
        Records(repo);
        repo.Write(Pack + "record.scriban", "// v2\npublic sealed record {{ record_name entity }};\n");

        var result = await repo.RunAsync("pack", "test", "--update");

        Assert.True(result.ExitCode == 0, result.Error + result.Out);
        Assert.Contains("  UPDATED case billing-small\n    differs Billing/CustomerRecord.cs\n    differs Billing/InvoiceRecord.cs\n", result.Out, StringComparison.Ordinal);
        Assert.EndsWith("1 passed, 0 failed, 1 updated\n", result.Out, StringComparison.Ordinal);
        Assert.Equal("// v2\npublic sealed record InvoiceRecord;\n", repo.Read(Case + "expected/Billing/InvoiceRecord.cs"));
        Assert.Equal(0, (await repo.RunAsync("pack", "test")).ExitCode);
    }

    [Fact]
    public async Task Case_selects_one_case()
    {
        using var repo = CliRepo.Billing();
        Records(repo);
        repo.Write(Pack + "tests/other/case.json", """{ "model": "billing-small", "parameters": { "suffix": "Row" } }""");

        var result = await repo.RunAsync("pack", "test", "--case", "billing-small");

        Assert.True(result.ExitCode == 0, result.Error + result.Out);
        Assert.Equal("records\n  PASS case billing-small\n1 passed, 0 failed\n", result.Out);
        var all = await repo.RunAsync("pack", "test");
        Assert.Equal(1, all.ExitCode);
        Assert.Contains("  ERROR case other\n    ", all.Out, StringComparison.Ordinal);
        Assert.Contains("MQ6053", all.Out, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("pack", "test", "--case", "ghost")).ExitCode);
    }

    [Fact]
    public async Task An_unknown_pack_exits_4()
    {
        using var repo = CliRepo.Billing();
        Records(repo);

        var result = await repo.RunAsync("pack", "test", "records", "ghost");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("Pack 'ghost' does not exist", result.Error, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("pack", "test", "--format", "yaml")).ExitCode);
    }
}
