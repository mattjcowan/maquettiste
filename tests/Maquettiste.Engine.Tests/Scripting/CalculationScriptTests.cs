using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Scripting;

/// <summary>Calculation files load and register (next-phase-2-design.md section 5.5, F50).</summary>
public sealed class CalculationScriptTests
{
    private const string Names = ".maquettiste/extensions/calculated/names.js";

    private static ScriptSource Calc(string code, string path = Names) => Scripts.Source(code, path);

    private static Exception LoadError(params ScriptSource[] sources) =>
        Assert.ThrowsAny<Exception>(() => Scripts.Pool(sources).Dispose());

    [Fact]
    public void A_calculated_value_registers_its_name_kinds_schema_and_description()
    {
        using var pool = Scripts.Pool([Calc("""
            maquettiste.calculated({
              name: "publicName",
              kinds: ["entity", "value-object", "entity"],
              schema: { type: "string", pattern: "^[a-z][a-z0-9-]*$" },
              description: "The name clients see in URLs and payloads.",
              value(entity) { return entity.pluralName.toLowerCase(); },
            });
            """)]);

        var registration = Assert.Single(pool.Registrations);
        Assert.Equal(ScriptRegistrationKind.CalculatedValue, registration.Kind);
        Assert.Equal("publicName", registration.Name);
        Assert.Equal(Names, registration.DeclaredIn);
        var declaration = registration.CalculatedValue!;
        Assert.Equal(["entity", "value-object"], declaration.Kinds);
        Assert.Equal("""{"type":"string","pattern":"^[a-z][a-z0-9-]*$"}""", declaration.Schema.GetRawText());
        Assert.Equal("The name clients see in URLs and payloads.", declaration.Description);
        Assert.Equal(1, registration.Line);
        Assert.Contains("calculated", System.Text.Json.JsonSerializer.Serialize(registration.Kind), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name: 'Public', kinds: ['entity'], schema: {}, value() {}", "must start with a lowercase letter")]
    [InlineData("name: 'a', kinds: ['diagram'], schema: {}, value() {}", "is not a resolved kind")]
    [InlineData("name: 'a', kinds: [], schema: {}, value() {}", "at least one resolved kind")]
    [InlineData("name: 'a', kinds: ['entity'], value() {}", "schema is required")]
    [InlineData("name: 'a', kinds: ['entity'], schema: { type: 12 }, value() {}", "does not compile")]
    [InlineData("name: 'a', kinds: ['entity'], schema: {}, value: 1", "value must be a function")]
    [InlineData("name: 'a', kinds: ['entity'], schema: {}, value() {}, cache: true", "unknown key 'cache'")]
    public void A_bad_name_unknown_kind_or_invalid_schema_is_MQ5002_with_its_line(string body, string message)
    {
        var e = Assert.IsType<ScriptErrorException>(LoadError(Calc("\n\nmaquettiste.calculated({ " + body + " });")));

        Assert.Equal("MQ5002", e.Diagnostic.Rule);
        Assert.Equal(Names, e.Diagnostic.FilePath);
        Assert.Equal(3, e.Diagnostic.Line);
        Assert.Contains(message, e.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_declared_twice_for_one_kind_is_MQ5002_on_the_later_file()
    {
        var declare = "maquettiste.calculated({ name: 'publicName', kinds: ['entity'], schema: {}, value() { return 1; } });";

        var e = Assert.IsType<ScriptErrorException>(LoadError(
            Calc(declare, ".maquettiste/extensions/calculated/a.js"), Calc(declare, ".maquettiste/extensions/calculated/b.js")));

        Assert.Equal("MQ5002", e.Diagnostic.Rule);
        Assert.Equal(".maquettiste/extensions/calculated/b.js", e.Diagnostic.FilePath);
        Assert.Contains("already declared by .maquettiste/extensions/calculated/a.js", e.Diagnostic.Message, StringComparison.Ordinal);

        // One name may serve other kinds through a separate registration.
        using var pool = Scripts.Pool([Calc(declare + "\nmaquettiste.calculated({ name: 'publicName', kinds: ['attribute'], schema: {}, value() {} });")]);
        Assert.Equal(2, pool.Registrations.Count);
    }

    [Fact]
    public void Calculation_files_have_a_scope_each_and_top_level_let_is_MQ5005()
    {
        // A global one file sets is a TypeError while it loads: the globals are frozen before calculation files run.
        var e = Assert.IsType<ScriptErrorException>(LoadError(Calc("globalThis.shared = 1;")));
        Assert.Equal("MQ5002", e.Diagnostic.Rule);

        // A top-level function is the file's own: two files may each declare one of the same name.
        using var pool = Scripts.Pool(
        [
            Calc("function kebab(t) { return t; }\nlet count = 0;\nmaquettiste.calculated({ name: 'a', kinds: ['entity'], schema: {}, value() {} });",
                ".maquettiste/extensions/calculated/a.js"),
            Calc("function kebab(t) { return t; }\nmaquettiste.calculated({ name: 'b', kinds: ['entity'], schema: {}, value() {} });",
                ".maquettiste/extensions/calculated/b.js"),
        ]);

        var warning = Assert.Single(pool.Warnings);
        Assert.Equal("MQ5005", warning.Rule);
        Assert.Equal(".maquettiste/extensions/calculated/a.js", warning.FilePath);
        Assert.Equal(2, warning.Line);
        Assert.StartsWith("Calculation file .maquettiste/extensions/calculated/a.js declares 'count' with let", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".maquettiste/extensions/rules/naming.js", "maquettiste.calculated({ name: 'a', kinds: ['entity'], schema: {}, value() {} });", "MQ5002")]
    [InlineData("templates/demo/helpers.js", "maquettiste.calculated({ name: 'a', kinds: ['entity'], schema: {}, value() {} });", "MQ6016")]
    [InlineData(Names, "maquettiste.helper('h', () => 1);", "MQ5002")]
    [InlineData(Names, "maquettiste.rule({ id: 'r', check() {} });", "MQ5002")]
    [InlineData(Names, "maquettiste.transform('t', () => ({}));", "MQ5002")]
    [InlineData(Names, "maquettiste.rows(() => []);", "MQ5002")]
    public void A_calculated_value_outside_a_calculation_file_or_another_registration_inside_one_is_a_load_error(string path, string code, string rule)
    {
        var e = Assert.IsType<ScriptErrorException>(LoadError(Calc(code, path)));

        Assert.Equal(rule, e.Diagnostic.Rule);
        Assert.Equal(path, e.Diagnostic.FilePath);
        Assert.Contains("TypeError", e.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whole_model_validate_reports_a_calculation_file_that_does_not_load()
    {
        var builder = new ModelBuilder();
        builder.Entity("Invoice").Key("id", "uuid");
        var basic = builder.Build();
        var model = ModelSnapshot.Create(basic.Documents, basic.Settings, basic.SettingsHash, [], [], 1, calculationScripts:
        [
            Calc("maquettiste.calculated({ name: 'a', kinds: ['entity'], schema: {}, value() { return 1; } });", ".maquettiste/extensions/calculated/a.js"),
            Calc("\nmaquettiste.calculated({ name: 'b', kinds: ['nope'], schema: {}, value() {} });", ".maquettiste/extensions/calculated/b.js"),
            Calc("var state = 1;\nmaquettiste.calculated({ name: 'c', kinds: ['entity'], schema: {}, value() { return 1; } });", ".maquettiste/extensions/calculated/c.js"),
        ]);
        var factory = new CountingSandboxFactory();
        var validator = ValidationFixture.Validator(scripts: factory);

        var whole = await validator.ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);
        var scoped = await validator.ValidateAsync(model, new ValidationScope([model.Documents[0].Element.Id]), null,
            TestContext.Current.CancellationToken);

        var loadError = Assert.Single(whole.Diagnostics, d => d.Rule == "MQ5002");
        Assert.Equal((".maquettiste/extensions/calculated/b.js", 2), (loadError.FilePath, loadError.Line));
        var warning = Assert.Single(whole.Diagnostics, d => d.Rule == "MQ5005");
        Assert.Equal(".maquettiste/extensions/calculated/c.js", warning.FilePath);
        Assert.DoesNotContain(scoped.Diagnostics, d => d.Rule is "MQ5002" or "MQ5005");
        Assert.Equal(0, factory.CalculationCalls); // loading evaluates nothing
    }
}
