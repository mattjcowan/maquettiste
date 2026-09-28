using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Scripting;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Scripting;

public sealed class RegistrationTests
{
    [Fact]
    public void Registrations_are_listed_in_script_then_call_order()
    {
        using var pool = Scripts.Pool(
        [
            Scripts.Source("maquettiste.helper('money', v => v.toFixed(2));\nmaquettiste.selector('audited', m => []);", "templates/demo/a.js"),
            Scripts.Source("""
                maquettiste.filter('isCore', e => e.hasTag('core'));
                maquettiste.transform('extra', e => ({ n: 1 }));
                maquettiste.rule({ id: 'no-empty', check() {} });
                maquettiste.helper('other', () => 2);
                """, "templates/demo/b.js"),
        ]);

        Assert.Equal(
        [
            new ScriptRegistration(ScriptRegistrationKind.Helper, "money", "templates/demo/a.js"),
            new ScriptRegistration(ScriptRegistrationKind.Selector, "audited", "templates/demo/a.js"),
            new ScriptRegistration(ScriptRegistrationKind.Filter, "isCore", "templates/demo/b.js"),
            new ScriptRegistration(ScriptRegistrationKind.Transform, "extra", "templates/demo/b.js"),
            new ScriptRegistration(ScriptRegistrationKind.Rule, "no-empty", "templates/demo/b.js"),
            new ScriptRegistration(ScriptRegistrationKind.Helper, "other", "templates/demo/b.js"),
        ], pool.Registrations);
        Assert.Equal("12.50", pool.Helper("money", 12.5));
    }

    [Fact]
    public void Helpers_see_pack_parameters_through_maquettiste_params()
    {
        using var pool = Scripts.Pool("maquettiste.helper('ns', () => maquettiste.params.namespace + '.' + Object.isFrozen(maquettiste.params));");
        using var lease = pool.Rent();

        var result = lease.Sandbox.CallHelper("ns", [], Scripts.Ctx(parameters: new Dictionary<string, object?> { ["namespace"] = "Acme" }));

        Assert.Equal("Acme.true", result);
    }

    [Fact]
    public void Filters_and_transforms_run_against_an_element()
    {
        var f = new ResolvedFixture();
        using var pool = Scripts.Pool("""
            maquettiste.filter('isCore', e => e.hasTag('core'));
            maquettiste.transform('names', (e, model) => ({ upper: e.name.toUpperCase(), count: e.attributes.length, first: e.attributes[0] }));
            maquettiste.transform('nothing', () => undefined);
            maquettiste.transform('array', () => [1]);
            """);
        using var lease = pool.Rent();

        Assert.True(lease.Sandbox.Filter("isCore", f.Customer, f.Model, Scripts.Ctx()));
        Assert.False(lease.Sandbox.Filter("isCore", f.Invoice, f.Model, Scripts.Ctx()));
        var data = lease.Sandbox.Transform("names", f.Customer, f.Model, Scripts.Ctx());
        Assert.Equal(["upper", "count", "first"], data.Keys);
        Assert.Equal("CUSTOMER", data["upper"]);
        Assert.Equal(2L, data["count"]);
        Assert.Same(f.CustomerName, data["first"]);
        Assert.Empty(lease.Sandbox.Transform("nothing", f.Customer, f.Model, Scripts.Ctx()));
        Assert.Throws<ScriptErrorException>(() => lease.Sandbox.Transform("array", f.Customer, f.Model, Scripts.Ctx()));
    }

    [Theory]
    [InlineData("maquettiste.helper('', () => 1);", "non-empty string")]
    [InlineData("maquettiste.helper('x', 1);", "must be a function")]
    [InlineData("maquettiste.helper('x', () => 1);\nmaquettiste.helper('x', () => 2);", "already registered by templates/demo/helpers.js")]
    [InlineData("maquettiste.rule({ id: 'a b', check() {} });", "without whitespace")]
    [InlineData("maquettiste.rule({ id: 'r', severity: 'fatal', check() {} });", "severity")]
    [InlineData("maquettiste.rule({ id: 'r', kinds: ['widget'], check() {} });", "unknown kind 'widget'")]
    [InlineData("maquettiste.rule({ id: 'r' });", "check must be a function")]
    [InlineData("maquettiste.rule({ id: 'r', check() {} });\nmaquettiste.rule({ id: 'r', check() {} });", "already registered")]
    public void Invalid_registrations_fail_the_pool_with_file_and_line(string code, string expected)
    {
        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool(code));

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Contains(expected, error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
        Assert.Equal(code.Split('\n').Length, error.Diagnostic.Line);
    }

    [Fact]
    public void The_same_name_may_be_registered_once_per_kind()
    {
        using var pool = Scripts.Pool("maquettiste.helper('x', () => 1); maquettiste.filter('x', () => true);");

        Assert.Equal(2, pool.Registrations.Count);
    }

    [Fact]
    public void Calling_an_unknown_name_is_a_script_error()
    {
        using var pool = Scripts.Pool("maquettiste.helper('x', () => 1);");

        var error = Assert.Throws<ScriptErrorException>(() => pool.Helper("missing"));

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Contains("'missing'", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_syntax_error_reports_file_line_and_column()
    {
        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool("maquettiste.helper('x', () => 1);\nfunction (\n"));

        Assert.Equal(new Diagnostic("MQ6016", DiagnosticSeverity.Error, error.Diagnostic.Message, null, Scripts.PackScript, null, 2, 10), error.Diagnostic);
        Assert.Contains("syntax error", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_error_in_a_helper_reports_file_and_line()
    {
        using var pool = Scripts.Pool("maquettiste.helper('boom', (v) => {\n  const x = null;\n  return x.missing;\n});");

        var error = Assert.Throws<ScriptErrorException>(() => pool.Helper("boom", 1));

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
        Assert.Equal(3, error.Diagnostic.Line);
        Assert.Equal(12, error.Diagnostic.Column);
        Assert.StartsWith("Helper 'boom' threw TypeError:", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_thrown_error_while_loading_reports_file_and_line()
    {
        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool("const a = 1;\nthrow new Error('bad config');"));

        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
        Assert.Equal(2, error.Diagnostic.Line);
        Assert.Contains("Error: bad config", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    private sealed record RuleSample(ModelSnapshot Model, EntityBuilder Customer, EntityBuilder Invoice, EnumBuilder Status);

    private static RuleSample RuleModel()
    {
        var b = new ModelBuilder(seed: 7);
        var billing = b.Package("Billing");
        var status = b.Enum("InvoiceStatus", billing).Member("Draft", 0);
        var customer = b.Entity("customer", billing).Key("id", "uuid").Attr("name", "string", a => a.Length(120));
        var invoice = b.Entity("Invoice", billing).Key("id", "uuid").Attr("status", status);
        b.Relation("places", customer, invoice, fromMax: MaxCardinality.One, toMax: MaxCardinality.Many, fromRole: "customer", toNavigation: "invoices");
        return new RuleSample(b.Build(), customer, invoice, status);
    }

    private const string NamingRule = """
        maquettiste.rule({
          id: 'pascal-names',
          severity: 'warning',
          kinds: ['entity', 'enum'],
          check(element, model, report) {
            if (!/^[A-Z]/.test(element.name)) report(`${element.name} should be PascalCase`, { pointer: '/name' });
            const refs = model.referencesTo(element.id);
            if (refs.length === 0) report('unreferenced', { severity: 'info' });
            const first = model.get(element.attributes[0].id);
            if (first.name !== 'id') report('first attribute is ' + first.name, { severity: 'error' });
            if (model.all('entity').length !== 2) report('expected two entities');
            if (Object.isFrozen(element) && Object.isFrozen(element.attributes[0])) report('frozen', { severity: 'info', pointer: '' });
          },
        });
        """;

    [Fact]
    public void A_rule_reports_diagnostics_with_pointer_and_severity()
    {
        var s = RuleModel();
        using var pool = Scripts.Pool(NamingRule, path: Scripts.RuleScript);
        using var lease = pool.Rent();
        var document = s.Model.GetDocument(s.Customer.Id)!;
        var reads = new ListRecorder();

        var diagnostics = lease.Sandbox.RunRule("pascal-names", document, s.Model, Scripts.Ctx(reads, seed: s.Customer.Id));

        Assert.Equal(
        [
            new Diagnostic("x/pascal-names", DiagnosticSeverity.Warning, "customer should be PascalCase", s.Customer.Id, document.Path, "/name", null, null),
            new Diagnostic("x/pascal-names", DiagnosticSeverity.Info, "frozen", s.Customer.Id, document.Path, "", null, null),
        ], diagnostics);
        Assert.Contains("r:" + s.Customer.Id, reads.Keys);
        Assert.Contains("k:entity", reads.Keys);
    }

    [Fact]
    public void A_rule_skips_elements_of_other_kinds()
    {
        var s = RuleModel();
        using var pool = Scripts.Pool("maquettiste.rule({ id: 'entities', kinds: ['entity'], check(e, m, report) { report('seen'); } });", path: Scripts.RuleScript);
        using var lease = pool.Rent();

        Assert.Empty(lease.Sandbox.RunRule("entities", s.Model.GetDocument(s.Status.Id)!, s.Model, Scripts.Ctx()));
        Assert.Single(lease.Sandbox.RunRule("entities", s.Model.GetDocument(s.Invoice.Id)!, s.Model, Scripts.Ctx()));
    }

    [Fact]
    public void A_rule_script_error_becomes_an_MQ5002_diagnostic_after_earlier_reports()
    {
        var s = RuleModel();
        using var pool = Scripts.Pool("maquettiste.rule({ id: 'crash', check(e, m, report) {\n  report('first');\n  e.attributes[99].name;\n} });", path: Scripts.RuleScript);
        using var lease = pool.Rent();
        var document = s.Model.GetDocument(s.Invoice.Id)!;

        var diagnostics = lease.Sandbox.RunRule("crash", document, s.Model, Scripts.Ctx());

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(new Diagnostic("x/crash", DiagnosticSeverity.Error, "first", s.Invoice.Id, document.Path, null, null, null), diagnostics[0]);
        Assert.Equal("MQ5002", diagnostics[1].Rule);
        Assert.Equal(Scripts.RuleScript, diagnostics[1].FilePath);
        Assert.Equal(3, diagnostics[1].Line);
        Assert.Equal(s.Invoice.Id, diagnostics[1].ElementId);
    }

    [Fact]
    public void A_rule_that_exceeds_a_limit_throws_MQ5003()
    {
        var s = RuleModel();
        using var pool = Scripts.Pool("maquettiste.rule({ id: 'spin', check() { for (;;) {} } });", Scripts.Limits with { ScriptStatements = 5_000 }, path: Scripts.RuleScript);
        using var lease = pool.Rent();

        var error = Assert.Throws<ScriptLimitException>(() => lease.Sandbox.RunRule("spin", s.Model.GetDocument(s.Invoice.Id)!, s.Model, Scripts.Ctx()));

        Assert.Equal("MQ5003", error.Diagnostic.Rule);
        Assert.Equal(s.Invoice.Id, error.Diagnostic.ElementId);
    }

    [Fact]
    public void Rule_script_load_errors_use_MQ5002()
    {
        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool("maquettiste.rule(", path: Scripts.RuleScript));

        Assert.Equal("MQ5002", error.Diagnostic.Rule);
        Assert.Equal(Scripts.RuleScript, error.Diagnostic.FilePath);
    }

    [Theory]
    [InlineData(".maquettiste/extensions/rules/naming.js", true)]
    [InlineData("extensions/rules/naming.js", true)]
    [InlineData("repo/.maquettiste/extensions/rules/naming.js", true)]
    [InlineData(".maquettiste\\extensions\\rules\\naming.js", true)]
    [InlineData(".maquettiste/templates/acme/extensions/rules/helpers.js", false)]
    [InlineData("templates/acme/extensions/rules/x.js", false)]
    [InlineData(".maquettiste/extensions/rules/nested/x.js", false)]
    [InlineData("helpers.js", false)]
    public void Only_scripts_directly_in_the_model_rules_folder_are_rule_scripts(string path, bool expected) =>
        Assert.Equal(expected, ScriptSandbox.IsRuleScript(path));

    [Fact]
    public void A_pack_script_under_a_folder_named_extensions_rules_fails_as_a_pack_script()
    {
        const string path = ".maquettiste/templates/acme/extensions/rules/helpers.js";

        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool("maquettiste.helper(", path: path));

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Equal(path, error.Diagnostic.FilePath);
    }

    [Fact]
    public void Report_validates_its_options()
    {
        var s = RuleModel();
        using var pool = Scripts.Pool("maquettiste.rule({ id: 'bad', check(e, m, report) { report('x', { severity: 'fatal' }); } });", path: Scripts.RuleScript);
        using var lease = pool.Rent();

        var diagnostics = lease.Sandbox.RunRule("bad", s.Model.GetDocument(s.Invoice.Id)!, s.Model, Scripts.Ctx());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MQ5002", diagnostic.Rule);
        Assert.Contains("severity", diagnostic.Message, StringComparison.Ordinal);
    }
}
