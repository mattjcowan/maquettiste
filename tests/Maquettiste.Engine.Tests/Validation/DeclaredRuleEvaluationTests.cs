using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Testing;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Declared rules judge the resolved model in the checks after resolution (next-phase-2-design.md section 5.6, F62).</summary>
public sealed class DeclaredRuleEvaluationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";
    private const string Catalog = "01J92P0V025DRFTXKMS240G2NG";

    /// <summary>A rule on Customer alone (the one entity tagged pii) that requires a condition.</summary>
    private static string OnCustomer(string id, string require) =>
        $$"""{ "id": "{{id}}", "select": { "kind": "entity", "tag": "pii" }, "require": {{require}}, "message": "{name}" }""";

    [Fact]
    public async Task Each_condition_holds_on_its_passing_case_and_fails_on_its_failing_case()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            OnCustomer("all-pass", """{ "all": [ { "has": "name" }, { "equals": { "path": "name", "value": "Customer" } } ] }"""),
            OnCustomer("all-fail", """{ "all": [ { "has": "name" }, { "equals": { "path": "name", "value": "Invoice" } } ] }"""),
            OnCustomer("any-pass", """{ "any": [ { "equals": { "path": "name", "value": "Invoice" } }, { "equals": { "path": "name", "value": "Customer" } } ] }"""),
            OnCustomer("any-fail", """{ "any": [ { "equals": { "path": "name", "value": "Invoice" } }, { "equals": { "path": "name", "value": "Payment" } } ] }"""),
            OnCustomer("not-pass", """{ "not": { "equals": { "path": "name", "value": "Invoice" } } }"""),
            OnCustomer("not-fail", """{ "not": { "equals": { "path": "name", "value": "Customer" } } }"""),
            OnCustomer("has-pass", """{ "has": "tags" }"""),
            OnCustomer("has-fail", """{ "has": "properties.nothing" }"""),
            OnCustomer("equals-pass", """{ "all": [ { "equals": { "path": "tags", "value": ["billing", "pii"] } }, { "equals": { "path": "package", "value": "01J92P0V01KDRN8GX5PGYCNKSX" } }, { "equals": { "path": "rationale", "value": null } } ] }"""),
            OnCustomer("equals-fail", """{ "equals": { "path": "name", "value": "customer" } }"""),
            OnCustomer("count-pass", """{ "count": { "of": "ownAttributes", "where": { "equals": { "path": "name", "value": "email" } }, "min": 1, "max": 1 } }"""),
            OnCustomer("count-fail", """{ "count": { "of": "ownAttributes", "where": { "equals": { "path": "name", "value": "phone" } } } }"""),
            OnCustomer("references-pass", """{ "references": { "kind": "package", "field": "package" } }"""),
            OnCustomer("references-fail", """{ "references": { "kind": "enum" } }"""),
            """{ "id": "referenced-by-pass", "select": { "kind": "enum" }, "require": { "referencedBy": { "kind": "attribute", "field": "type" } }, "message": "{name}" }""",
            """{ "id": "referenced-by-fail", "select": { "kind": "enum" }, "require": { "referencedBy": { "kind": "attribute", "max": 0 } }, "message": "{name}" }""");

        var report = await harness.ValidateAsync();

        foreach (var condition in new[] { "all", "any", "not", "has", "equals", "count", "references", "referenced-by" })
        {
            Assert.Empty(await harness.FoundOnAsync(report, $"x/{condition}-pass"));
            var name = condition == "referenced-by" ? "InvoiceStatus" : "Customer";
            Assert.Equal([name], await harness.FoundOnAsync(report, $"x/{condition}-fail"));
        }

        var finding = report.Diagnostics.First(d => d.Rule == "x/all-fail");
        Assert.Equal((DiagnosticSeverity.Error, "Customer", ".maquettiste/model/entities/customer.json", ""),
            (finding.Severity, finding.Message, finding.FilePath, finding.JsonPointer));
    }

    [Fact]
    public async Task Selector_keys_combine_and_lists_mean_any_of()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """
            { "id": "combined", "select": { "kind": ["entity", "enum"], "stereotype": ["soft-delete", "aggregate-root"], "tag": "billing" },
              "require": { "has": "properties.nothing" }, "message": "{name}" }
            """,
            """
            { "id": "property", "select": { "kind": "entity", "property": ["retentionDays", "nothing"] },
              "require": { "has": "properties.nothing" }, "message": "{name}" }
            """);

        var report = await harness.ValidateAsync();

        Assert.Equal(["Customer", "Invoice"], await harness.FoundOnAsync(report, "x/combined"));
        Assert.Contains("Invoice", await harness.FoundOnAsync(report, "x/property"));
        Assert.DoesNotContain("Product", await harness.FoundOnAsync(report, "x/property"));
    }

    [Fact]
    public async Task Package_selects_nested_packages()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            $$"""{ "id": "billing", "select": { "kind": "entity", "package": "{{Billing}}" }, "require": { "has": "properties.nothing" }, "message": "{name}" }""",
            $$"""{ "id": "catalog", "select": { "kind": "entity", "package": "{{Catalog}}" }, "require": { "has": "properties.nothing" }, "message": "{name}" }""");

        var report = await harness.ValidateAsync();

        Assert.Equal(["Customer", "CustomerNote", "Invoice", "InvoiceLine", "InvoiceNote", "Payment", "Product", "RevenueMonth"],
            await harness.FoundOnAsync(report, "x/billing"));
        Assert.Equal(["Product"], await harness.FoundOnAsync(report, "x/catalog"));
    }

    [Fact]
    public async Task Stereotype_reads_the_effective_set()
    {
        await using var harness = DeclaredRuleHarness.Create(
            new Dictionary<string, string>
            {
                ["extensions/rules/rules.json"] = DeclaredRuleHarness.File(
                    """{ "id": "audited", "select": { "kind": "entity", "stereotype": "audited" }, "require": { "has": "properties.nothing" }, "message": "{name}" }"""),
            },
            edit: root =>
            {
                // The catalog package makes audited a default of its elements (3.1), so Product carries it without naming it.
                var path = Path.Combine(root, "model", "packages", "catalog.json");
                var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                node["defaultStereotypes"] = new JsonArray("audited");
                File.WriteAllBytes(path, TestServices.Json.Write(node, "package.json", ".maquettiste/model/packages/catalog.json"));
            });

        var report = await harness.ValidateAsync();

        Assert.Equal(["Customer", "Invoice", "Payment", "Product"], await harness.FoundOnAsync(report, "x/audited"));
    }

    [Fact]
    public async Task Messages_render_paths_and_count()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """
            { "id": "message", "select": { "kind": "entity", "tag": "pii" }, "require": { "count": { "of": "ownAttributes", "max": 0 } },
              "message": "{name} has {$count} attributes; tags {tags}; {{braces}}; package {package}; none [{rationale}]; first {ownAttributes}" }
            """,
            """
            { "id": "enum", "select": { "kind": "attribute" }, "require": { "not": { "equals": { "path": "name", "value": "status" } } },
              "message": "{name} is {type} with {required}" }
            """);

        var report = await harness.ValidateAsync();

        var message = Assert.Single(report.Diagnostics, d => d.Rule == "x/message").Message;
        Assert.Equal("""Customer has 4 attributes; tags ["billing","pii"]; {braces}; package Billing; none []; first ["id","name","email","customerSince"]""",
            message);
        var status = Assert.Single(report.Diagnostics, d => d.Rule == "x/enum").Message;
        Assert.StartsWith("status is InvoiceStatus with ", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rule_that_selects_nothing_is_MQ5017()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """{ "id": "nothing", "select": { "kind": "entity", "tag": "no-such-tag" }, "require": { "has": "name" }, "message": "m" }""");

        var report = await harness.ValidateAsync();

        var found = Assert.Single(report.Diagnostics, d => d.Rule == "MQ5017");
        Assert.Equal((DiagnosticSeverity.Warning, ".maquettiste/extensions/rules/rules.json", "/rules/0/select"), (found.Severity, found.FilePath, found.JsonPointer));
        Assert.NotNull(found.Line);
    }

    [Fact]
    public async Task Validation_rules_overrides_and_off_apply_to_x_ids()
    {
        await using var harness = DeclaredRuleHarness.Create(
            new Dictionary<string, string>
            {
                ["extensions/rules/rules.json"] = DeclaredRuleHarness.File(
                    OnCustomer("lowered", """{ "has": "properties.nothing" }"""),
                    OnCustomer("silenced", """{ "has": "properties.nothing" }"""),
                    """{ "id": "silenced-nothing", "select": { "kind": "entity", "tag": "no-such-tag" }, "require": { "has": "name" }, "message": "m" }"""),
            },
            settings: s => s["validation"] = new JsonObject
            {
                ["rules"] = new JsonObject { ["x/lowered"] = "info", ["x/silenced"] = "off", ["x/silenced-nothing"] = "off" },
            });

        var report = await harness.ValidateAsync();

        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(report.Diagnostics, d => d.Rule == "x/lowered").Severity);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "x/silenced" or "MQ5017");
    }

    [Fact]
    public async Task Diagram_references_do_not_count()
    {
        // The billing overview diagram shows Customer; that is not a use.
        await using var harness = DeclaredRuleHarness.WithRules(OnCustomer("on-a-diagram", """{ "referencedBy": { "kind": ["diagram", "member"] } }"""));

        var report = await harness.ValidateAsync();

        Assert.Equal(["Customer"], await harness.FoundOnAsync(report, "x/on-a-diagram"));
    }

    [Fact]
    public async Task A_path_through_calculated_values_reads_the_cached_value()
    {
        await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string>
        {
            ["extensions/calculated/names.js"] =
                """maquettiste.calculated({ name: "publicName", kinds: ["entity"], schema: { type: "string" }, value(e) { return e.name.toLowerCase(); } });""",
            ["extensions/rules/rules.json"] = DeclaredRuleHarness.File(
                """{ "id": "public", "select": { "kind": "entity" }, "require": { "not": { "equals": { "path": "calculated.publicName", "value": "customer" } } }, "message": "{calculated.publicName}" }""",
                """{ "id": "again", "select": { "kind": "entity" }, "require": { "has": "calculated.publicName" }, "message": "m" }"""),
        });

        var report = await harness.ValidateAsync();

        Assert.Equal("customer", Assert.Single(report.Diagnostics, d => d.Rule == "x/public").Message);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "x/again");
        Assert.Equal(8, harness.Factory.CalculationCalls); // one per entity: the rules read the values the checks evaluated
    }

    [Fact]
    public async Task Count_reads_lists_of_records_such_as_indexes()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            """{ "id": "one-index", "select": { "kind": "table" }, "require": { "count": { "of": "indexes", "min": 1 } }, "message": "{name}" }""",
            """
            { "id": "wide-index", "select": { "kind": "table" }, "require": { "count": { "of": "indexes", "where": { "count": { "of": "columns", "min": 3 } } } },
              "message": "{name} has {$count} wide indexes" }
            """);

        var report = await harness.ValidateAsync();

        const string notes = "01K6BND0000000000000000001";
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "x/one-index" && d.ElementId == notes);
        var wide = Assert.Single(report.Diagnostics, d => d.Rule == "x/wide-index" && d.ElementId == notes);
        Assert.Equal("notes has 0 wide indexes", wide.Message);
    }

    [Fact]
    public async Task Findings_are_byte_identical_across_parallelism()
    {
        string[] rules =
        [
            """{ "id": "columns", "select": { "kind": "column" }, "require": { "equals": { "path": "nullable", "value": true } }, "message": "{table.name}.{name}" }""",
            """{ "id": "attributes", "select": { "kind": "attribute" }, "require": { "has": "description" }, "message": "{name} of {owner}" }""",
        ];
        var reports = new List<string>();
        foreach (var parallelism in new[] { 1, 8 })
        {
            await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string> { ["extensions/rules/rules.json"] = DeclaredRuleHarness.File(rules) },
                parallelism: parallelism);
            var report = await harness.ValidateAsync();
            Assert.Contains(report.Diagnostics, d => d.Rule == "x/columns");
            reports.Add(JsonSerializer.Serialize(report.Diagnostics.Where(d => d.Rule.StartsWith("x/", StringComparison.Ordinal))));
        }

        Assert.Equal(reports[0], reports[1]);
    }

    [Fact]
    public async Task A_project_without_declaration_files_does_no_declared_rule_work()
    {
        // The owner's constraint: no cost for a project without rule declaration files. Counted, not timed.
        await using (var without = DeclaredRuleHarness.Create(new Dictionary<string, string>()))
        {
            var report = await without.ValidateAsync();
            Assert.Empty((await without.Store.GetSnapshotAsync(Ct)).DeclaredRules);
            Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
            Assert.Equal((0, 0, 0L), (without.Validator.DeclaredRuleChecksRun, without.Checks.DeclaredRuleRuns, without.Checks.DeclaredRuleObjectsJudged));
        }

        await using var with = DeclaredRuleHarness.WithRules(OnCustomer("one", """{ "has": "name" }"""));
        await with.ValidateAsync();
        Assert.Equal((1, 1, 1L), (with.Validator.DeclaredRuleChecksRun, with.Checks.DeclaredRuleRuns, with.Checks.DeclaredRuleObjectsJudged));
    }
}
