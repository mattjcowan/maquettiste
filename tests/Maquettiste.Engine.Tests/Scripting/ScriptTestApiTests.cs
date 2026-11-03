using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Generation;

namespace Maquettiste.Engine.Tests.Scripting;

/// <summary>Helper tests in the sandbox (next-phase-2-design.md section 5.4): the test pool, <c>maquettiste.test</c> and the assert API.</summary>
public sealed class ScriptTestApiTests
{
    private const string TestsFile = PackTestKit.Pack + "tests/helpers.test.js";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptSandboxPool Pool(string pack, params string[] tests) => ScriptSandboxFactory.CreateTestPool(
        [Scripts.Source(pack)], [.. tests.Select((t, i) => Scripts.Source(t, $"templates/demo/tests/t{i}.test.js"))], [], Scripts.Limits, Ct);

    private static List<ScriptTestResult> RunAll(ScriptSandboxPool pool) => [.. pool.Tests.Select((_, i) => pool.RunTest(i, Scripts.Ctx()))];

    [Fact]
    public void Equal_passes_and_fails_with_expected_and_got()
    {
        using var pool = Pool("", """
            maquettiste.test("same", (assert) => { assert.equal(1, 1); assert.equal(NaN, NaN); assert.equal("a", "a"); });
            maquettiste.test("differs", (assert) => assert.equal(1, 2, "numbers"));
            maquettiste.test("strict", (assert) => assert.equal("1", 1));
            """);

        var results = RunAll(pool);

        Assert.True(results[0].Passed);
        Assert.Equal((false, "numbers: expected 2, got 1"), (results[1].Passed, results[1].Message));
        Assert.Equal("expected 1, got \"1\"", results[2].Message);
    }

    [Fact]
    public void Deep_equal_ignores_key_order()
    {
        using var pool = Pool("", """
            maquettiste.test("keys", (assert) => assert.deepEqual({ a: 1, b: [1, { c: null }] }, { b: [1, { c: null }], a: 1 }));
            maquettiste.test("positions", (assert) => assert.deepEqual([1, 2], [2, 1]));
            maquettiste.test("extra key", (assert) => assert.deepEqual({ a: 1, b: 2 }, { a: 1 }));
            """);

        var results = RunAll(pool);

        Assert.True(results[0].Passed);
        Assert.Equal("expected [2,1], got [1,2]", results[1].Message);
        Assert.Equal("expected {\"a\":1}, got {\"a\":1,\"b\":2}", results[2].Message);
    }

    [Fact]
    public void Throws_returns_the_message()
    {
        using var pool = Pool("""function boom() { throw new Error("boom"); }""", """
            maquettiste.test("throws", (assert) => assert.equal(assert.throws(() => boom()), "boom"));
            maquettiste.test("does not throw", (assert) => assert.throws(() => 1, "boom expected"));
            maquettiste.test("ok and fail", (assert) => { assert.ok(1); assert.fail("stop"); });
            """);

        var results = RunAll(pool);

        Assert.True(results[0].Passed);
        Assert.Equal("boom expected: expected the function to throw", results[1].Message);
        Assert.Equal("stop", results[2].Message);
    }

    [Fact]
    public void A_test_reads_the_packs_top_level_functions()
    {
        using var pool = Pool("function suffix() { return 'Row'; }\nconst prefix = 'Q';", """
            maquettiste.test("reads", (assert) => assert.equal(prefix + suffix(), "QRow"));
            """);

        Assert.True(Assert.Single(RunAll(pool)).Passed);
    }

    [Fact]
    public void A_test_cannot_change_the_packs_globals()
    {
        using var pool = Pool("function suffix() { return 'Row'; }", """
            maquettiste.test("assigns", () => { suffix = null; });
            maquettiste.test("redefines", () => { Object.defineProperty(globalThis, "suffix", { value: () => "X" }); });
            maquettiste.test("still there", (assert) => assert.equal(suffix(), "Row"));
            """, """
            globalThis.leak = 1;
            maquettiste.test("never", () => {});
            """);

        var results = RunAll(pool);

        Assert.Equal([false, false, true], results.Select(r => r.Passed));
        Assert.Contains("TypeError", results[0].Message, StringComparison.Ordinal);
        Assert.Equal(3, pool.Tests.Count);
        Assert.Equal("templates/demo/tests/t1.test.js", Assert.Single(pool.TestFailures).File);
    }

    [Fact]
    public void Helpers_holds_the_registered_helpers()
    {
        using var pool = Pool("""
            maquettiste.helper("twice", (n) => n * 2);
            maquettiste.helper("abs", (n) => Math.abs(n));
            maquettiste.filter("never", () => false);
            """, """
            maquettiste.test("helpers", (assert, helpers) => {
              assert.deepEqual(Object.keys(helpers), ["abs", "twice"]);
              assert.equal(helpers.twice(helpers.abs(-2)), 4);
              assert.ok(Object.isFrozen(helpers));
            });
            """);

        Assert.True(Assert.Single(RunAll(pool)).Passed);
        Assert.Empty(pool.TestFailures);
    }

    [Fact]
    public async Task Params_hold_the_pack_defaults()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(".maquettiste/maquettiste.json", """{ "formatVersion": 1, "name": "shop", "outputs": { "allow": [ { "path": "gen" } ] }, "packs": { "records": { "output": "gen", "parameters": { "suffix": "Row" } } } }""");
        kit.Repo.WriteFile(TestsFile, """
            maquettiste.test("defaults", (assert, helpers) => {
              assert.equal(maquettiste.params.suffix, "Record");
              assert.equal(helpers.record_name({ name: "invoice" }), "InvoiceRecord");
            });
            """);

        var report = await kit.TestAsync(null, "records");

        Assert.Equal(PackTestOutcome.Passed, Assert.Single(report.Packs[0].Tests).Outcome);
    }

    [Fact]
    public async Task A_test_file_registering_a_helper_is_MQ6053()
    {
        await using var kit = PackTestKit.Create();
        kit.Repo.WriteFile(PackTestKit.Pack + "tests/extra.test.js", "maquettiste.test(\"x\", () => {});\nmaquettiste.helper(\"extra\", () => 1);\n");

        var pack = (await kit.TestAsync(null, "records")).Packs[0];

        var error = Assert.Single(pack.Diagnostics);
        Assert.Equal(("MQ6053", PackTestKit.Pack + "tests/extra.test.js", 2), (error.Rule, error.FilePath, error.Line));
        Assert.Contains("registers tests only", error.Message, StringComparison.Ordinal);
        Assert.Equal(["tests/helpers.test.js", "tests/helpers.test.js"], pack.Tests.Select(t => t.File));
    }

    [Fact]
    public async Task Two_tests_with_one_name_are_MQ6053()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(TestsFile, "maquettiste.test(\"a\", () => {});\nmaquettiste.test(\"b\", () => {});\nmaquettiste.test(\"a\", () => {});\n");

        var report = await kit.TestAsync(null, "records");

        var error = Assert.Single(report.Packs[0].Diagnostics);
        Assert.Equal(("MQ6053", 3), (error.Rule, error.Line));
        Assert.Empty(report.Packs[0].Tests);
        Assert.Equal(1, report.Failed);
    }

    [Fact]
    public async Task A_limit_fails_the_test_with_MQ6052()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(".maquettiste/maquettiste.json", """{ "formatVersion": 1, "name": "shop", "limits": { "scriptStatements": 5000 }, "packs": { "records": { "output": "gen" } } }""");
        kit.Repo.WriteFile(TestsFile, "maquettiste.test(\"spins\", () => { for (;;) {} });\nmaquettiste.test(\"after\", (assert) => assert.ok(true));\n");

        var pack = (await kit.TestAsync(null, "records")).Packs[0];

        Assert.Equal([PackTestOutcome.Failed, PackTestOutcome.Passed], pack.Tests.Select(t => t.Outcome));
        Assert.Contains("statement limit", pack.Tests[0].Message, StringComparison.Ordinal);
        Assert.Equal("MQ6052", Assert.Single(pack.Diagnostics).Rule);
    }

    [Fact]
    public void A_failure_points_at_the_failing_statement()
    {
        using var pool = Pool("", "maquettiste.test(\"a\", (assert) => {\n  const x = 1;\n  assert.equal(x, 2);\n});\nmaquettiste.test(\"b\", () => { null.x; });\n");

        var results = RunAll(pool);

        Assert.Equal(("templates/demo/tests/t0.test.js", 3), (results[0].File, results[0].Line));
        Assert.Equal(5, results[1].Line);
        Assert.Contains("TypeError", results[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_reaches_the_closure()
    {
        await using var kit = PackTestKit.Create(helperTests: false);
        kit.Repo.WriteFile(".maquettiste/maquettiste.json", """{ "formatVersion": 1, "name": "shop", "packs": { "records": { "output": "gen" }, "naming": { "parameters": { "suffix": "Project" } } } }""");
        kit.Repo.WriteFile(".maquettiste/templates/naming/pack.json", """{ "name": "naming", "version": "1.0.0", "engine": ">=0.1", "parameters": { "suffix": "Model" } }""");
        kit.Repo.WriteFile(".maquettiste/templates/naming/helpers.js", """maquettiste.helper("class_name", (name) => String(name) + maquettiste.params.suffix);""" + "\n");
        kit.Repo.WriteFile(PackTestKit.Pack + "pack.json", """
            { "name": "records", "version": "1.0.0", "engine": ">=0.1", "uses": [ { "pack": "naming", "version": ">=1.0" } ], "parameters": { "suffix": "Record" },
              "units": [ { "id": "record", "template": "record.scriban", "for": "each entity", "output": "{{ entity.package.name }}/{{ record_name entity }}.cs" } ] }
            """);
        kit.Repo.WriteFile(PackTestKit.Pack + "helpers.js", PackTestKit.Helpers + "maquettiste.helper(\"model_name\", (name) => maquettiste.use(\"naming\").class_name(name));\n");
        kit.Repo.WriteFile(TestsFile, """
            maquettiste.test("direct", (assert) => assert.equal(maquettiste.use("naming").class_name("Invoice"), "InvoiceModel"));
            maquettiste.test("through a helper", (assert, helpers) => assert.equal(helpers.model_name("Invoice"), "InvoiceModel"));
            """);

        var pack = (await kit.TestAsync(null, "records")).Packs[0];

        Assert.All(pack.Tests, t => Assert.Equal(PackTestOutcome.Passed, t.Outcome));
        Assert.Equal(2, pack.Tests.Count);
    }
}
