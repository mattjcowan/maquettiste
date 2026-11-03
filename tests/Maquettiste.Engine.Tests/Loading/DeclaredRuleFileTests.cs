using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Tests.Validation;

namespace Maquettiste.Engine.Tests.Loading;

/// <summary>Rule declaration files load into the snapshot (next-phase-2-design.md section 5.6, F60).</summary>
public sealed class DeclaredRuleFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Audit = """
        {
          "$schema": "../../.schema/v1/declared-rules.json",
          "rules": [
            {
              "id": "audited-tables-have-updated-at",
              "description": "Audited tables record when a row last changed.",
              "select": { "kind": "table", "stereotype": "audited" },
              "require": {
                "count": { "of": "columns", "where": { "equals": { "path": "name", "value": "updated_at" } }, "min": 1 }
              },
              "message": "Table {name} carries audited but has no updated_at column."
            }
          ]
        }
        """;

    [Fact]
    public async Task A_declaration_file_loads_into_the_snapshot()
    {
        await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string> { ["extensions/rules/audit.json"] = Audit });

        var snapshot = await harness.Store.GetSnapshotAsync(Ct);

        var file = Assert.Single(snapshot.DeclaredRules);
        Assert.Equal(".maquettiste/extensions/rules/audit.json", file.Path);
        Assert.NotEmpty(file.Hash);
        var rule = Assert.Single(file.Rules.Rules);
        Assert.Equal("audited-tables-have-updated-at", rule.Id);
        Assert.Equal(DiagnosticSeverity.Error, rule.Severity);
        Assert.Equal("Audited tables record when a row last changed.", rule.Description);
        Assert.Equal(["table"], rule.Select.Kind);
        Assert.Equal(["audited"], rule.Select.Stereotype);
        Assert.Empty(rule.Select.Tag);
        var count = rule.Require.Count!;
        Assert.Equal(("columns", 1, (int?)null), (count.Of, count.Min!.Value, count.Max));
        Assert.Equal("name", count.Where!.Equal!.Path);
        Assert.Equal("updated_at", count.Where.Equal.Value.GetString());
        Assert.Null(rule.Require.Has);
        Assert.Equal("Table {name} carries audited but has no updated_at column.", rule.Message);
        Assert.DoesNotContain(snapshot.LoadDiagnostics, d => d.Rule == "MQ5014");
    }

    [Fact]
    public async Task A_file_that_fails_its_schema_is_MQ5014_and_loads_no_rules()
    {
        await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string>
        {
            ["extensions/rules/broken.json"] = """{ "rules": [ { "id": "no-message", "select": { "kind": "entity" }, "require": { "has": "name" } } ] }""",
            ["extensions/rules/syntax.json"] = """{ "rules": [ """,
            ["extensions/rules/fine.json"] = DeclaredRuleHarness.File("""{ "id": "fine", "severity": "info", "select": { "kind": "entity" }, "require": { "has": "name" }, "message": "m" }"""),
        });

        var snapshot = await harness.Store.GetSnapshotAsync(Ct);

        Assert.Equal(["fine"], snapshot.DeclaredRules.SelectMany(d => d.Rules.Rules).Select(r => r.Id));
        Assert.Equal(DiagnosticSeverity.Info, snapshot.DeclaredRules[0].Rules.Rules[0].Severity);
        var broken = Assert.Single(snapshot.LoadDiagnostics, d => d.FilePath == ".maquettiste/extensions/rules/broken.json");
        Assert.Equal(("MQ5014", DiagnosticSeverity.Error), (broken.Rule, broken.Severity));
        Assert.Equal("/rules/0", broken.JsonPointer);
        var syntax = Assert.Single(snapshot.LoadDiagnostics, d => d.FilePath == ".maquettiste/extensions/rules/syntax.json");
        Assert.Equal("MQ5014", syntax.Rule);
        Assert.Contains((await harness.ValidateAsync()).Diagnostics, d => d.Rule == "MQ5014" && d.FilePath == broken.FilePath);
    }

    [Fact]
    public async Task A_condition_takes_exactly_one_operator()
    {
        await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string>
        {
            ["extensions/rules/two.json"] = DeclaredRuleHarness.File(
                """{ "id": "two", "select": { "kind": "entity" }, "require": { "has": "name", "not": { "has": "rationale" } }, "message": "m" }"""),
            ["extensions/rules/none.json"] = DeclaredRuleHarness.File(
                """{ "id": "none", "select": { "kind": "entity" }, "require": { "all": [ {} ] }, "message": "m" }"""),
        });

        var snapshot = await harness.Store.GetSnapshotAsync(Ct);

        Assert.Empty(snapshot.DeclaredRules);
        Assert.Contains(snapshot.LoadDiagnostics, d => d.Rule == "MQ5014" && d.FilePath!.EndsWith("two.json", StringComparison.Ordinal) && d.JsonPointer == "/rules/0/require");
        Assert.Contains(snapshot.LoadDiagnostics, d => d.Rule == "MQ5014" && d.FilePath!.EndsWith("none.json", StringComparison.Ordinal)
            && d.JsonPointer == "/rules/0/require/all/0");
    }

    [Fact]
    public async Task Selector_values_read_one_value_or_a_list()
    {
        await using var harness = DeclaredRuleHarness.Create(new Dictionary<string, string>
        {
            ["extensions/rules/values.json"] = DeclaredRuleHarness.File(
                """{ "id": "one", "select": { "kind": "entity", "tag": "billing" }, "require": { "references": { "kind": "package" } }, "message": "m" }""",
                """
                { "id": "list", "select": { "kind": ["entity", "table"], "tag": ["billing", "pii"], "package": ["01J92P0V01KDRN8GX5PGYCNKSX"] },
                  "require": { "referencedBy": { "kind": ["attribute", "column"], "field": "type", "max": 3 } }, "message": "m" }
                """),
        });

        var snapshot = await harness.Store.GetSnapshotAsync(Ct);

        var rules = Assert.Single(snapshot.DeclaredRules).Rules.Rules;
        Assert.Equal(["entity"], rules[0].Select.Kind);
        Assert.Equal(["billing"], rules[0].Select.Tag);
        Assert.Equal(["package"], rules[0].Require.References!.Kind);
        Assert.Equal(["entity", "table"], rules[1].Select.Kind);
        Assert.Equal(["billing", "pii"], rules[1].Select.Tag);
        Assert.Equal(["01J92P0V01KDRN8GX5PGYCNKSX"], rules[1].Select.Package);
        var links = rules[1].Require.ReferencedBy!;
        Assert.Equal(["attribute", "column"], links.Kind);
        Assert.Equal(("type", (int?)null, (int?)3), (links.Field, links.Min, links.Max));
    }
}
