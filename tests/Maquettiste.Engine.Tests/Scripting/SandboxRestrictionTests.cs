using Maquettiste.Engine.Model;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Tests.Scripting;

public sealed class SandboxRestrictionTests
{
    private static ScriptErrorException ScriptError(IScriptSandboxPool pool, string helper, params object?[] args) =>
        Assert.Throws<ScriptErrorException>(() => pool.Helper(helper, args));

    private static ScriptLimitException LimitError(IScriptSandboxPool pool, string helper, params object?[] args) =>
        Assert.Throws<ScriptLimitException>(() => pool.Helper(helper, args));

    [Fact]
    public void No_clr_host_or_module_globals_are_reachable()
    {
        using var pool = Scripts.Pool("""
            maquettiste.helper('probe', () => ['System', 'importNamespace', 'clrHelper', 'require', 'process', 'fetch', 'XMLHttpRequest',
              'setTimeout', 'Deno', 'Bun', 'host', 'ArrayBuffer', 'Uint8Array', 'SharedArrayBuffer', 'WeakRef', 'FinalizationRegistry', 'ShadowRealm']
              .map(n => n + ':' + typeof globalThis[n]).join(','));
            """);

        var result = (string)pool.Helper("probe")!;

        Assert.All(result.Split(','), entry => Assert.EndsWith(":undefined", entry, StringComparison.Ordinal));
    }

    [Fact]
    public void Model_objects_expose_no_clr_members()
    {
        var fixture = new ResolvedFixture();
        using var pool = Scripts.Pool("""
            maquettiste.helper('clr', e => [typeof e.GetType, typeof e.getType, typeof e.Dependencies, typeof e.dependencies, typeof e.Target, typeof e.constructor.constructor].join(','));
            """);

        Assert.Equal("undefined,undefined,undefined,undefined,undefined,function", pool.Helper("clr", fixture.Customer));
    }

    [Theory]
    [InlineData("eval('1 + 1')")]
    [InlineData("Function('return 1')()")]
    [InlineData("(() => {}).constructor('return 1')()")]
    [InlineData("Object.getPrototypeOf(async function () {}).constructor('return 1')")]
    [InlineData("Object.getPrototypeOf(function* () {}).constructor('return 1')")]
    public void String_compilation_is_refused(string expression)
    {
        using var pool = Scripts.Pool($"maquettiste.helper('compile', () => {expression});");

        var error = ScriptError(pool, "compile");

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Contains("String compilation has been disabled", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void File_access_through_dynamic_import_is_refused()
    {
        using var pool = Scripts.Pool("maquettiste.helper('read', () => import('/etc/passwd'));");

        var error = ScriptError(pool, "read");

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Contains("Module loading has been disabled", error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("promise", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_syntax_is_refused()
    {
        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool("import fs from 'fs';\nmaquettiste.helper('x', () => 1);"));

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
        Assert.Equal(1, error.Diagnostic.Line);
    }

    [Fact]
    public void An_infinite_loop_hits_the_statement_limit_and_cannot_catch_it()
    {
        using var pool = Scripts.Pool("maquettiste.helper('spin', () => { try { while (true) {} } catch (e) { return 'caught'; } });",
            Scripts.Limits with { ScriptStatements = 10_000 });

        var error = LimitError(pool, "spin");

        Assert.Equal("MQ6007", error.Diagnostic.Rule);
        Assert.Contains("statement limit (10000)", error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
    }

    [Fact]
    public void An_infinite_loop_hits_the_time_limit()
    {
        using var pool = Scripts.Pool("maquettiste.helper('spin', () => { while (true) {} });",
            Scripts.Limits with { ScriptStatements = long.MaxValue, ScriptTimeoutMs = 200 });

        var error = LimitError(pool, "spin");

        Assert.Equal("MQ6007", error.Diagnostic.Rule);
        Assert.Contains("time limit (200 ms)", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_recursion_hits_the_recursion_limit_with_a_location()
    {
        using var pool = Scripts.Pool("function down(n) {\n  return down(n + 1);\n}\nmaquettiste.helper('deep', () => down(0));",
            Scripts.Limits with { ScriptRecursion = 64 });

        var error = LimitError(pool, "deep");

        Assert.Equal("MQ6007", error.Diagnostic.Rule);
        Assert.Contains("recursion limit (64)", error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
        Assert.NotNull(error.Diagnostic.Line);
    }

    [Theory]
    [InlineData("const a = []; while (true) a.push({ x: [1, 2, 3] });")]
    [InlineData("let s = 'x'; while (true) s += s;")]
    [InlineData("return 'x'.repeat(2 ** 28).length;")]
    [InlineData("return 'x'.padEnd(2 ** 28).length;")]
    [InlineData("return new Array(1e8).fill(0).length;")]
    public void Memory_growth_hits_the_memory_limit(string body)
    {
        using var pool = Scripts.Pool($"maquettiste.helper('grow', () => {{ {body} }});",
            Scripts.Limits with { ScriptMemoryBytes = 8_000_000, ScriptStatements = long.MaxValue, ScriptTimeoutMs = 30_000 });

        var error = LimitError(pool, "grow");

        Assert.Equal("MQ6007", error.Diagnostic.Rule);
        Assert.Contains("memory limit (8000000 bytes)", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> CatastrophicRegexes() =>
    [
        "/(a+)+$/.test(input)",
        "new RegExp('(a+)+$').test(input)",
        "new RegExp('(a+)+$').exec(input)",
        "input.match('(a+)+$')",
        "input.search('(a+)+$')",
        "input.replace(new RegExp('(a+)+$', 'g'), 'x')",
        "input.split(new RegExp('(a+)+$'))",
        "[...input.matchAll(new RegExp('(a+)+$', 'g'))]",
        "new RegExp('(a+)+$', 'v').test(input)",
        "/(\\p{L}+)+$/u.test(input)",
    ];

    [Theory]
    [MemberData(nameof(CatastrophicRegexes))]
    public void A_catastrophic_regular_expression_stops_within_one_second_under_the_default_limits(string expression)
    {
        using var pool = Scripts.Pool($"maquettiste.helper('regex', () => {{ const input = 'a'.repeat(40) + '!'; return {expression}; }});");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = LimitError(pool, "regex");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"stopped after {clock.ElapsedMilliseconds} ms");
        Assert.Equal("MQ6007", error.Diagnostic.Rule);
        Assert.Contains("regular expression", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catastrophic_regular_expression_stops_within_one_second_of_call_cancellation()
    {
        using var pool = Scripts.Pool("maquettiste.helper('regex', () => { for (;;) new RegExp('(a+)+$').test('a'.repeat(40) + '!'); });",
            Scripts.Limits with { ScriptTimeoutMs = 120_000, ScriptStatements = long.MaxValue });
        using var cts = new CancellationTokenSource();
        var started = new ManualResetEventSlim();
        var run = Task.Run(() =>
        {
            using var lease = pool.Rent();
            started.Set();
            return lease.Sandbox.CallHelper("regex", [], Scripts.CtxWithToken(cts.Token));
        }, TestContext.Current.CancellationToken);

        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await cts.CancelAsync();

        // The script stops within a second either way: as a cancellation, or, when the regular expression's own time limit trips
        // before the cancellation is observed on a slow machine, as a limit. Only the deadline is the contract here.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.True(error is OperationCanceledException or ScriptLimitException, $"unexpected {error.GetType().Name}: {error.Message}");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"stopped after {clock.ElapsedMilliseconds} ms");
        if (error is OperationCanceledException cancelled)
            Assert.Equal(cts.Token, cancelled.CancellationToken);
    }

    [Theory]
    [InlineData("/\\?$/.test('int?')", true)]
    [InlineData("new RegExp('b').exec('abc').index", 1L)]
    [InlineData("'Invoice42'.replace(/(\\d+)/g, '<$1>')", "Invoice<42>")]
    [InlineData("'a,b;c'.split(/[,;]/).join('|')", "a|b|c")]
    [InlineData("'xAy'.search(/A/)", 1L)]
    [InlineData("(() => { const r = /a/g; r.lastIndex = 1; const hit = r.test('aa'); return hit + ':' + r.lastIndex; })()", "true:2")]
    public void A_match_that_times_out_once_from_a_stalled_thread_is_tried_again_with_the_same_result(string expression, object expected)
    {
        // A busy machine can deschedule the thread past the wall-clock match bound even on a trivial pattern; one simulated
        // stall stands for that.
        using var pool = Scripts.Pool($"maquettiste.helper('regex', () => {expression});");
        try
        {
            RegexRetry.SimulatedStalls = 1;
            Assert.Equal(expected, pool.Helper("regex"));
            Assert.Equal(0, RegexRetry.SimulatedStalls);
        }
        finally
        {
            RegexRetry.SimulatedStalls = 0;
        }
    }

    [Theory]
    [InlineData("/\\?$/.test('int?')", 2)]
    [InlineData("/x/.test({ toString() { return 'x'; } })", 1)]
    [InlineData("'ab'.replace(/a/, () => 'x')", 1)]
    [InlineData("(() => { const r = /x/; Object.defineProperty(r, 'flags', { value: '' }); return r.test('x'); })()", 1)]
    public void A_match_is_retried_at_most_once_and_never_when_it_may_have_run_script_code(string expression, int stalls)
    {
        using var pool = Scripts.Pool($"maquettiste.helper('regex', () => {expression});");
        try
        {
            RegexRetry.SimulatedStalls = stalls;
            var error = LimitError(pool, "regex");
            Assert.Equal("MQ6007", error.Diagnostic.Rule);
            Assert.Contains("regular expression", error.Diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal(0, RegexRetry.SimulatedStalls);
        }
        finally
        {
            RegexRetry.SimulatedStalls = 0;
        }
    }

    [Fact]
    public void A_timeout_an_inner_match_already_retried_is_not_retried_by_the_call_around_it()
    {
        var inner = 0;
        var outerRetries = 0;
        Assert.Throws<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => RegexRetry.Run(
            () => RegexRetry.Run<int>(() =>
            {
                inner++;
                throw new System.Text.RegularExpressions.RegexMatchTimeoutException("a", "(a+)+$", TimeSpan.FromMilliseconds(250));
            }, () => true),
            () => { outerRetries++; return true; }));

        Assert.Equal(2, inner);
        Assert.Equal(0, outerRetries);
    }

    [Fact]
    public void Ordinary_regular_expressions_still_work_after_the_guard()
    {
        using var pool = Scripts.Pool("""
            maquettiste.helper('regex', () => [
              'Invoice42'.replace(/(\d+)/g, '<$1>'), 'a,b;c'.split(/[,;]/).join('|'), [...'a1b2'.matchAll(/\d/g)].map(m => m[0] + m.index).join(''),
              'xAy'.search(/A/), /a/y.test('ba'), new RegExp('b').exec('abc').index, 'abc'.match(/(?<n>b)/).groups.n,
              'aXbXc'.replaceAll(new RegExp('x', 'gi'), '-'), RegExp.prototype.exec.name, RegExp.prototype.test.length,
            ].join(','));
            """);

        Assert.Equal("Invoice<42>,a|b|c,1123,1,false,1,b,a-b-c,exec,1", pool.Helper("regex"));
    }

    [Fact]
    public void A_sandbox_that_hit_a_limit_is_replaced_and_the_pool_keeps_working()
    {
        using var pool = Scripts.Pool("maquettiste.helper('spin', () => { while (true) {} }); maquettiste.helper('one', () => 1);",
            Scripts.Limits with { ScriptStatements = 1_000 });

        LimitError(pool, "spin");

        Assert.Equal(1L, pool.Helper("one"));
    }

    [Fact]
    public void A_limit_while_loading_fails_the_pool()
    {
        var error = Assert.Throws<ScriptLimitException>(() => Scripts.Pool("while (true) {}", Scripts.Limits with { ScriptStatements = 1_000 }));

        Assert.Equal("MQ6007", error.Diagnostic.Rule);
        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
    }

    [Theory]
    [InlineData("globalThis.leak = 1")]
    [InlineData("Math.random = () => 0")]
    [InlineData("Array.prototype.push = null")]
    [InlineData("Object.prototype.polluted = true")]
    [InlineData("maquettiste.helper = null")]
    [InlineData("delete globalThis.JSON")]
    public void Globals_are_frozen_after_loading(string statement)
    {
        using var pool = Scripts.Pool($"maquettiste.helper('mutate', () => {{ {statement}; return 'mutated'; }});");

        var error = ScriptError(pool, "mutate");

        Assert.Contains("TypeError", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_is_closed_after_loading()
    {
        using var pool = Scripts.Pool("maquettiste.helper('late', () => maquettiste.helper('other', () => 1));");

        var error = ScriptError(pool, "late");

        Assert.Contains("only be called while scripts load", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_javascript_still_works_after_freezing()
    {
        using var pool = Scripts.Pool("""
            class Money { constructor(v) { this.v = v; } format() { return `$${this.v.toFixed(2)}`; } }
            function* count(n) { for (let i = 0; i < n; i++) yield i; }
            maquettiste.helper('mix', () => {
              const m = new Map([['b', 2], ['a', 1]]);
              const matched = 'Invoice42'.match(/([A-Za-z]+)(\d+)/);
              let caught = '';
              try { null.x; } catch (e) { caught = e.constructor.name; }
              return [new Money(3.5).format(), [...count(3)].join(''), [...m.keys()].sort().join(''), matched[1] + '-' + matched[2],
                JSON.stringify({ a: [1, { b: true }] }), caught, [3, 1, 2].sort((x, y) => x - y).join(''), 'ÉTÉ'.toLowerCase(),
                (1234.5).toLocaleString(), typeof Temporal].join('|');
            });
            """);

        Assert.Equal("$3.50|012|ab|Invoice-42|{\"a\":[1,{\"b\":true}]}|TypeError|123|été|1,234.5|object", pool.Helper("mix"));
    }
}
