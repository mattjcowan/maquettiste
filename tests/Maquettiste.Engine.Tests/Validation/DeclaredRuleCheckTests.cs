using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>The static checks of rule declaration files (next-phase-2-design.md section 5.6, F61): MQ5015 and MQ5016.</summary>
public sealed class DeclaredRuleCheckTests
{
    private const string File = ".maquettiste/extensions/rules/rules.json";

    private static List<(string? Pointer, string Message)> Mq5016(ValidationReport report) =>
        [.. report.Diagnostics.Where(d => d.Rule == "MQ5016").Select(d => (d.JsonPointer, d.Message))];

    [Fact]
    public async Task An_id_taken_by_a_rule_script_is_MQ5015_on_the_later_file()
    {
        await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string>
        {
            ["extensions/rules/a.js"] = """maquettiste.rule({ id: "taken", kinds: ["entity"], check(e, model, report) {} });""",
            ["extensions/rules/b.json"] = DeclaredRuleHarness.File(
                """{ "id": "taken", "select": { "kind": "entity" }, "require": { "has": "name" }, "message": "m" }""",
                """{ "id": "twice", "select": { "kind": "entity" }, "require": { "has": "name" }, "message": "m" }"""),
            ["extensions/rules/c.json"] = DeclaredRuleHarness.File(
                """{ "id": "twice", "select": { "kind": "entity" }, "require": { "has": "name" }, "message": "m" }"""),
        });

        var report = await harness.ValidateAsync();

        var found = report.Diagnostics.Where(d => d.Rule == "MQ5015").Select(d => (d.FilePath, d.JsonPointer, d.Severity)).ToList();
        Assert.Equal(
        [
            (".maquettiste/extensions/rules/b.json", "/rules/0/id", DiagnosticSeverity.Error),
            (".maquettiste/extensions/rules/c.json", "/rules/0/id", DiagnosticSeverity.Error),
        ], found);
        Assert.Contains("rule script .maquettiste/extensions/rules/a.js", report.Diagnostics.First(d => d.Rule == "MQ5015").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_kind_stereotype_or_package_is_MQ5016()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """
            { "id": "unknown", "select": { "kind": ["entity", "entities"], "stereotype": "audit", "package": "01J92P0V01KDRN8GX5PGYCNKSZ" },
              "require": { "has": "name" }, "message": "m" }
            """);

        var report = await harness.ValidateAsync();

        Assert.Equal(["/rules/0/select/kind/1", "/rules/0/select/package", "/rules/0/select/stereotype"],
            Mq5016(report).Select(f => f.Pointer).Order(StringComparer.Ordinal));
        Assert.All(report.Diagnostics.Where(d => d.Rule == "MQ5016"), d => Assert.Equal((File, DiagnosticSeverity.Error), (d.FilePath, d.Severity)));
    }

    [Fact]
    public async Task A_path_no_selected_kind_has_is_MQ5016()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """
            { "id": "paths", "select": { "kind": ["entity", "enum"] },
              "require": { "all": [ { "has": "colour" }, { "has": "name.length" }, { "has": "members" }, { "has": "properties.retentionDays" },
                { "has": "package.parent.name" }, { "has": "package.parent.nmae" } ] },
              "message": "m" }
            """);

        var report = await harness.ValidateAsync();

        var found = Mq5016(report);
        Assert.Equal(["/rules/0/require/all/0/has", "/rules/0/require/all/1/has", "/rules/0/require/all/5/has"], found.Select(f => f.Pointer));
        Assert.Contains("no selected kind has", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("neither a map nor an object", found[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_undeclared_calculated_value_in_a_path_is_MQ5016()
    {
        await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string>
        {
            ["extensions/calculated/names.js"] =
                """maquettiste.calculated({ name: "publicName", kinds: ["entity"], schema: { type: "string" }, value(e) { return e.name.toLowerCase(); } });""",
            ["extensions/rules/rules.json"] = DeclaredRuleHarness.File(
                """
                { "id": "calculated", "select": { "kind": "entity" },
                  "require": { "any": [ { "has": "calculated.publicName" }, { "has": "calculated.secretName" } ] }, "message": "{calculated.publicName}" }
                """),
        });

        var report = await harness.ValidateAsync();

        var found = Assert.Single(Mq5016(report));
        Assert.Equal("/rules/0/require/any/1/has", found.Pointer);
        Assert.Contains("'secretName', which entity does not declare", found.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ5012");
    }

    [Fact]
    public async Task A_count_of_a_list_of_values_is_MQ5016()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """
            { "id": "counts", "select": { "kind": "entity" },
              "require": { "all": [ { "count": { "of": "tags", "min": 1 } }, { "count": { "of": "attributes", "where": { "has": "type" } } },
                { "count": { "of": "name" } }, { "count": { "of": "attributes", "min": 3, "max": 2 } } ] },
              "message": "m" }
            """);

        var report = await harness.ValidateAsync();

        Assert.Equal(["/rules/0/require/all/0/count/of", "/rules/0/require/all/2/count/of", "/rules/0/require/all/3/count/max"],
            Mq5016(report).Select(f => f.Pointer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_unknown_placeholder_or_count_without_a_count_is_MQ5016()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """{ "id": "a", "select": { "kind": "entity" }, "require": { "has": "name" }, "message": "{nmae} is wrong" }""",
            """{ "id": "b", "select": { "kind": "entity" }, "require": { "has": "name" }, "message": "{name} has {$count}" }""",
            """{ "id": "c", "select": { "kind": "entity" }, "require": { "has": "name" }, "message": "{ name } {{fine}}" }""",
            """{ "id": "d", "select": { "kind": "entity" }, "require": { "has": "name" }, "message": "open { brace" }""",
            """{ "id": "e", "select": { "kind": "entity" }, "require": { "count": { "of": "attributes" } }, "message": "{name}: {$count}, {{literal}}" }""",
            """{ "id": "f", "select": { "kind": "entity" }, "require": { "references": { "kind": "nothing" } }, "message": "{$count}" }""");

        var report = await harness.ValidateAsync();

        Assert.Equal(["/rules/0/message", "/rules/1/message", "/rules/2/message", "/rules/3/message", "/rules/5/require/references/kind"],
            Mq5016(report).Select(f => f.Pointer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_broken_declaration_stops_resolution()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """{ "id": "broken", "select": { "kind": "tabel" }, "require": { "has": "name" }, "message": "m" }""",
            """{ "id": "fires", "select": { "kind": "entity" }, "require": { "has": "properties.nothing" }, "message": "{name}" }""");

        var report = await harness.ValidateAsync();

        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ5016");
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule.StartsWith("x/", StringComparison.Ordinal));
        Assert.Equal(0, harness.Checks.DeclaredRuleRuns);
    }
}
