using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Declared rule conditions written in the expression language (next-phase-3-design.md section 3.1, "Declared rule conditions", task
/// H10), over the billing copy of <see cref="DeclaredRuleHarness"/>: an <c>expression</c> reads the selected object's members by
/// <c>member</c> paths, typed from the resolved record of the selected kind and, for <c>properties.x</c>, from the property schemas
/// (the billing extension retention declares <c>retentionDays</c> an integer for audited entities: Invoice states 3650, Customer and
/// Payment take the stereotype's 2555). It is judged as a guard, so only true holds and a null member fails it.
/// </summary>
public sealed class DeclaredRuleExpressionTests
{
    private static string Rule(string id, string select, string expression) =>
        $$"""{ "id": "{{id}}", "select": {{select}}, "require": { "expression": {{expression}} }, "message": "{name}" }""";

    private static string Member(string path) => $$"""{ "member": "{{path}}" }""";

    private const string Audited = """{ "kind": "entity", "stereotype": "audited" }""";

    private const string Entities = """{ "kind": "entity" }""";

    [Fact]
    public async Task An_expression_reads_members_typed_from_the_record_and_the_property_schemas()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            Rule("long-retention", Audited, $$"""{ "op": "ge", "args": [{{Member("properties.retentionDays")}}, { "value": 3000 }] }"""),
            Rule("short-name", Audited, $$"""{ "op": "le", "args": [{ "call": "length", "args": [{{Member("name")}}] }, { "value": 7 }] }"""),
            Rule("concrete", Audited, $$"""{ "op": "not", "args": [{{Member("isAbstract")}}] }"""));

        var report = await harness.ValidateAsync();

        Assert.DoesNotContain(report.Diagnostics, d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal) || d.Rule is "MQ5016" or "MQ5017");
        Assert.Equal(["Customer", "Payment"], await harness.FoundOnAsync(report, "x/long-retention"));
        Assert.Equal(["Customer"], await harness.FoundOnAsync(report, "x/short-name"));
        Assert.Empty(await harness.FoundOnAsync(report, "x/concrete"));
        var finding = report.Diagnostics.First(d => d.Rule == "x/long-retention");
        Assert.Equal((DiagnosticSeverity.Error, "Customer", ".maquettiste/model/entities/customer.json"), (finding.Severity, finding.Message, finding.FilePath));
    }

    [Fact]
    public async Task A_null_member_fails_the_condition_as_a_guard_does()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            Rule("kept", Entities, $$"""{ "op": "gt", "args": [{{Member("properties.retentionDays")}}, { "value": 0 }] }"""),
            Rule("unknown", Entities, $$"""{ "op": "or", "args": [{ "op": "eq", "args": [{{Member("rationale")}}, { "value": "x" }] }, { "value": true }] }"""),
            Rule("kept-or-none", Entities,
                $$"""{ "op": "or", "args": [{ "op": "gt", "args": [{{Member("properties.retentionDays")}}, { "value": 0 }] }, { "op": "isNull", "args": [{{Member("properties.retentionDays")}}] }] }"""));

        var report = await harness.ValidateAsync();

        // An entity without the property reads null: the comparison is unknown, and unknown fails.
        var kept = await harness.FoundOnAsync(report, "x/kept");
        Assert.DoesNotContain("Invoice", kept);
        Assert.DoesNotContain("Customer", kept);
        Assert.Contains("Product", kept);
        Assert.Empty(await harness.FoundOnAsync(report, "x/unknown"));
        Assert.Empty(await harness.FoundOnAsync(report, "x/kept-or-none"));
    }

    [Fact]
    public async Task The_static_pass_types_members_and_a_rule_that_does_not_type_reports_no_finding()
    {
        await using var harness = DeclaredRuleHarness.WithRules(
            Rule("wrong-type", Audited, $$"""{ "op": "eq", "args": [{{Member("properties.retentionDays")}}, { "value": "long" }] }"""),
            Rule("a-list", Audited, $$"""{ "op": "eq", "args": [{{Member("ownAttributes")}}, { "value": "x" }] }"""),
            Rule("an-object", Audited, $$"""{ "op": "eq", "args": [{{Member("package")}}, { "value": "x" }] }"""),
            Rule("clock", Audited, """{ "op": "isNotNull", "args": [{ "now": true }] }"""));

        var report = await harness.ValidateAsync();

        string RuleAt(string pointer) => report.Diagnostics.Single(d => d.FilePath == ".maquettiste/extensions/rules/rules.json" && d.JsonPointer == pointer).Rule;
        Assert.Equal("MQ3103", RuleAt("/rules/0/require/expression"));
        Assert.Equal("MQ3102", RuleAt("/rules/1/require/expression/args/0"));
        Assert.Equal("MQ3103", RuleAt("/rules/2/require/expression/args/0"));
        Assert.Equal("MQ3104", RuleAt("/rules/3/require/expression/args/0"));
        foreach (var id in new[] { "wrong-type", "a-list", "an-object", "clock" })
            Assert.Empty(await harness.FoundOnAsync(report, "x/" + id));
    }

    [Fact]
    public async Task Expressions_in_two_files_and_inside_count_each_judge_their_own_objects()
    {
        await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string>
        {
            ["extensions/rules/a.json"] = DeclaredRuleHarness.File(
                Rule("long-retention", Audited, $$"""{ "op": "ge", "args": [{{Member("properties.retentionDays")}}, { "value": 3000 }] }""")),
            ["extensions/rules/b.json"] = DeclaredRuleHarness.File(
                """
                { "id": "has-total", "select": { "kind": "entity", "tag": "pii" }, "message": "{name}",
                  "require": { "count": { "of": "ownAttributes", "where": { "expression": { "op": "eq", "args": [{ "member": "name" }, { "value": "email" }] } }, "min": 1 } } }
                """),
        });

        var report = await harness.ValidateAsync();

        Assert.DoesNotContain(report.Diagnostics, d => d.Rule.StartsWith("MQ31", StringComparison.Ordinal) || d.Rule == "MQ5016");
        Assert.Equal(["Customer", "Payment"], await harness.FoundOnAsync(report, "x/long-retention"));
        Assert.Empty(await harness.FoundOnAsync(report, "x/has-total"));
    }

    [Fact]
    public async Task A_condition_that_stops_at_a_limit_is_one_MQ5003_for_its_rule_and_no_finding()
    {
        // length counts code points one at a time, so Customer's long rationale runs the condition past the statement limit. The other
        // entities fail it, but a rule that could not judge every object reports why, once, rather than findings that hide it.
        await using var harness = DeclaredRuleHarness.Create(
            new Dictionary<string, string>
            {
                ["extensions/rules/rules.json"] = DeclaredRuleHarness.File(
                    Rule("long-rationale", Entities, $$"""{ "op": "gt", "args": [{ "call": "length", "args": [{{Member("rationale")}}] }, { "value": 200000 }] }"""),
                    Rule("not-customer", Entities, $$"""{ "op": "ne", "args": [{{Member("name")}}, { "value": "Customer" }] }""")),
            },
            edit: root =>
            {
                var path = Path.Combine(root, "model", "entities", "customer.json");
                var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                node["rationale"] = new string('x', 150_000);
                File.WriteAllBytes(path, TestServices.Json.Write(node, "entity.json", ".maquettiste/model/entities/customer.json"));
            });

        var report = await harness.ValidateAsync();

        var stopped = Assert.Single(report.Diagnostics, d => d.Rule == "MQ5003");
        Assert.Equal((".maquettiste/extensions/rules/rules.json", "/rules/0/require/expression"), (stopped.FilePath, stopped.JsonPointer));
        Assert.Contains("rule 'long-rationale' stopped on entity 'Customer'", stopped.Message, StringComparison.Ordinal);
        Assert.Contains("statement limit", stopped.Message, StringComparison.Ordinal);
        Assert.Empty(await harness.FoundOnAsync(report, "x/long-rationale"));
        Assert.Equal(["Customer"], await harness.FoundOnAsync(report, "x/not-customer"));
    }
}
