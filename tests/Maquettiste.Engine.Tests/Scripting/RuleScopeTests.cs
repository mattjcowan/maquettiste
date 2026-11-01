using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Scripting;

/// <summary>
/// Each rule file runs in its own function scope, and the globals are frozen before the rule files run (next-phase-1-design.md
/// section 5.7): a file cannot publish anything for another, and top-level <c>let</c> or <c>var</c> is MQ5005.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RuleScopeTests
{
    private const string RuleA = ".maquettiste/extensions/rules/a.js";
    private const string RuleB = ".maquettiste/extensions/rules/b.js";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ModelSnapshot Model, ElementDocument Invoice) Model()
    {
        var builder = new ModelBuilder(seed: 9);
        var invoice = builder.Entity("Invoice").Key("id", "uuid");
        var model = builder.Build();
        return (model, model.GetDocument(invoice.Id)!);
    }

    private static IReadOnlyList<Diagnostic> Run(IScriptSandboxPool pool, string rule)
    {
        var (model, invoice) = Model();
        using var lease = pool.Rent();
        return lease.Sandbox.RunRule(rule, invoice, model, Scripts.Ctx());
    }

    [Fact]
    public void Each_rule_file_has_its_own_scope()
    {
        // The same top-level names in two files: a const declared twice in one scope would not even load.
        using var pool = Scripts.Pool(
        [
            Scripts.Source("const label = 'a';\nfunction describe() { return 'from ' + label; }\nmaquettiste.rule({ id: 'a', check(e, m, report) { report(describe()); } });", RuleA),
            Scripts.Source("const label = 'b';\nfunction describe() { return 'from ' + label; }\nmaquettiste.rule({ id: 'b', check(e, m, report) { report(describe() + ', ' + typeof globalThis.describe + ', ' + typeof label); } });", RuleB),
        ]);

        Assert.Equal("from a", Assert.Single(Run(pool, "a")).Message);
        Assert.Equal("from b, undefined, string", Assert.Single(Run(pool, "b")).Message);
        Assert.Empty(pool.Warnings);
    }

    [Fact]
    public void A_rule_cannot_call_a_function_of_another_rule_file()
    {
        using var pool = Scripts.Pool(
        [
            Scripts.Source("function shared() { return 1; }\nmaquettiste.rule({ id: 'a', check(e, m, report) { report('a' + shared()); } });", RuleA),
            Scripts.Source("maquettiste.rule({ id: 'b', check(e, m, report) {\n  report('b' + shared());\n} });", RuleB),
        ]);

        Assert.Equal("a1", Assert.Single(Run(pool, "a")).Message);
        var failure = Assert.Single(Run(pool, "b"));
        Assert.Equal("MQ5002", failure.Rule);
        Assert.Contains("ReferenceError", failure.Message, StringComparison.Ordinal);
        Assert.Equal((RuleB, 2), (failure.FilePath, failure.Line));

        // Calling it while the file loads fails the file.
        var load = Assert.Throws<ScriptErrorException>(() => Scripts.Pool(
        [
            Scripts.Source("function shared() { return 1; }", RuleA),
            Scripts.Source("\nconst one = shared();", RuleB),
        ]));
        Assert.Equal(("MQ5002", RuleB, 2), (load.Diagnostic.Rule, load.Diagnostic.FilePath, load.Diagnostic.Line));
    }

    [Theory]
    [InlineData("globalThis.helper = () => 1;", "TypeError")]
    [InlineData("Object.prototype.extra = 1;", "TypeError")]
    [InlineData("Array.prototype.sum = function () { return 0; };", "TypeError")]
    [InlineData("Object.defineProperty(globalThis, 'helper', { value: 1 });", "TypeError")]
    [InlineData("})(); let leaked = 1; (function () {", "closes the function scope")]
    public void A_rule_file_cannot_publish_a_global_for_another_file(string statement, string expected)
    {
        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool(
        [
            Scripts.Source("maquettiste.rule({ id: 'a', check() {} });\n" + statement, RuleA),
            Scripts.Source("maquettiste.rule({ id: 'b', check(e, m, report) { report(typeof helper); } });", RuleB),
        ]));

        Assert.Equal(("MQ5002", RuleA), (error.Diagnostic.Rule, error.Diagnostic.FilePath));
        Assert.Contains(expected, error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(2, error.Diagnostic.Line);

        // Pack scripts keep one scope per pack: a function of one script serves the next, and the globals freeze after loading.
        using var pack = Scripts.Pool(
        [
            Scripts.Source("function money(v) { return v.toFixed(2); }", "templates/demo/a.js"),
            Scripts.Source("maquettiste.helper('money', v => money(v));", "templates/demo/b.js"),
        ]);
        Assert.Equal("1.50", pack.Helper("money", 1.5));
    }

    public static TheoryData<string> BrokenCode() =>
    [
        "maquettiste.rule({ id: 'x' check() {} });",
        "// a rule in progress\nmaquettiste.rule({\n  id: 'x',\n  check() { return 1 +; },\n});",
        "maquettiste.rule({ id: 'x', check() {",
        "maquettiste.rule({ id: 'x', check() {\n",
        "null.x;",
        "\n\nnotDefined();",
        "maquettiste.rule({ id: 'x', check() {} });\nmaquettiste.rule({ severity: 'error' });",
    ];

    [Theory]
    [MemberData(nameof(BrokenCode))]
    public void Syntax_errors_keep_their_line_and_column(string code)
    {
        // A pack script runs as written; a rule script runs inside its scope, and its positions are those of the same code.
        var asPack = Assert.Throws<ScriptErrorException>(() => Scripts.Pool(code, path: Scripts.PackScript)).Diagnostic;
        var asRule = Assert.Throws<ScriptErrorException>(() => Scripts.Pool(code, path: Scripts.RuleScript)).Diagnostic;

        Assert.Equal(("MQ6016", "MQ5002"), (asPack.Rule, asRule.Rule));
        Assert.NotNull(asRule.Line);
        Assert.NotNull(asRule.Column);
        Assert.Equal((asPack.Line, asPack.Column), (asRule.Line, asRule.Column));
    }

    [Fact]
    public async Task Top_level_let_or_var_is_MQ5005_on_validate_and_on_the_extension_file()
    {
        const string Counter = """
            // Counts the entities it sees.
            const limit = 3;
            let seen = 0;
            maquettiste.rule({
              id: "counter",
              severity: "info",
              kinds: ["entity"],
              check(element, model, report) {
                let local = 0;
                seen++;
                if (seen > limit + local) report("Seen more than three.");
              },
            });
            """;

        // The pool reports the first top-level let or var, by file, and none for const or a let inside a function.
        using (var pool = Scripts.Pool([Scripts.Source(Counter, RuleB), Scripts.Source("{\n  var flag = true;\n}\nvar other = 1;", RuleA),
            Scripts.Source("const fine = () => { let x = 1; var y = 2; return x + y; };\nfor (const k of [1]) {}", ".maquettiste/extensions/rules/c.js")]))
        {
            Assert.Equal([("MQ5005", RuleA, 2, 3), ("MQ5005", RuleB, 3, 1)],
                pool.Warnings.Select(w => (w.Rule, w.FilePath, w.Line ?? 0, w.Column ?? 0)));
            Assert.All(pool.Warnings, w => Assert.Equal(DiagnosticSeverity.Warning, w.Severity));
            Assert.Contains("'seen'", pool.Warnings[1].Message, StringComparison.Ordinal);
        }

        await using var repo = E2ERepo.Create(demo: false);
        var written = await repo.Store.WriteExtensionFileAsync("rules/counter.js", Counter, null, Ct);
        Assert.Equal(SaveOutcome.Saved, written.Outcome);
        const string Path = ".maquettiste/extensions/rules/counter.js";
        var saved = Assert.Single(written.Diagnostics);
        Assert.Equal(("MQ5005", Path, 3), (saved.Rule, saved.FilePath, saved.Line));

        var listed = Assert.Single((await repo.Store.ListExtensionFilesAsync(Ct)).Files, f => f.Kind == "rule");
        Assert.Equal("MQ5005", Assert.Single(listed.Diagnostics).Rule);
        Assert.Equal([new ExtensionRuleInfo("x/counter", DiagnosticSeverity.Info)], listed.Rules);

        var whole = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        var warning = Assert.Single(whole.Diagnostics, d => d.Rule == "MQ5005");
        Assert.Equal((Path, 3, 1, (string?)null), (warning.FilePath, warning.Line, warning.Column, warning.ElementId));

        // A validation of some elements (a save) does not report the files.
        var scoped = await repo.Store.ValidateAsync(new ValidationScope([E2ERepo.ProductId]), Ct);
        Assert.DoesNotContain(scoped.Diagnostics, d => d.Rule == "MQ5005");
    }
}
