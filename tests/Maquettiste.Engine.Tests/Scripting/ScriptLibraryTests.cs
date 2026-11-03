using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Tests.Scripting;

/// <summary>
/// <c>maquettiste.use(name)</c> (library packs, next-phase-2-design.md section 5.2): a script reaches a used pack's helpers, which run
/// in a sandbox of that pack's own pool, with the caller's recorder and seed and the used pack's parameters.
/// </summary>
public sealed class ScriptLibraryTests
{
    private const string NamingScript = "templates/naming/helpers.js";
    private const string NamingCode = """
        function pascal(s) { return String(s).replace(/(^|[-_ ])([a-z])/g, (_, __, c) => c.toUpperCase()); }
        maquettiste.helper('class_name', e => pascal(e.name) + maquettiste.params.suffix);
        maquettiste.helper('echo', v => v);
        maquettiste.helper('spin', () => { for (;;) {} });
        """;

    private static IReadOnlyDictionary<string, object?> NamingParameters => new Dictionary<string, object?> { ["suffix"] = "Record" };

    /// <summary>Routes library calls to the used packs' own pools, as a render's unit does.</summary>
    private sealed class Calls : IScriptLibraryCalls
    {
        public Dictionary<string, (IScriptSandboxPool Pool, IReadOnlyDictionary<string, object?> Parameters)> Packs { get; } = new(StringComparer.Ordinal);

        public object? Call(string pack, string helper, IReadOnlyList<object?> args, ScriptCallContext caller)
        {
            var (pool, parameters) = Packs[pack];
            using var lease = pool.Rent();
            return lease.Sandbox.CallHelper(helper, args, caller with { Parameters = parameters });
        }
    }

    private static ScriptLibrary Library(string pack, IScriptSandboxPool pool) =>
        new(pack, [.. pool.Registrations.Where(r => r.Kind == ScriptRegistrationKind.Helper).Select(r => r.Name).Order(StringComparer.Ordinal)]);

    private static IScriptSandboxPool Pool(string code, string path, IReadOnlyList<ScriptLibrary> libraries, SandboxLimits? limits = null) =>
        new ScriptSandboxFactory().CreatePool([Scripts.Source(code, path)], libraries, limits ?? Scripts.Limits, 1, CancellationToken.None);

    private static (IScriptSandboxPool Records, Calls Calls) Records(string code, SandboxLimits? limits = null)
    {
        var naming = Pool(NamingCode, NamingScript, [], limits);
        var calls = new Calls();
        calls.Packs["naming"] = (naming, NamingParameters);
        return (Pool(code, Scripts.PackScript, [Library("naming", naming)], limits), calls);
    }

    private static object? Call(IScriptSandboxPool pool, Calls calls, string helper, IReadRecorder? reads = null, params object?[] args)
    {
        using var lease = pool.Rent();
        return lease.Sandbox.CallHelper(helper, args, Scripts.Ctx(reads) with { Libraries = calls });
    }

    [Fact]
    public void Use_returns_the_library_helpers_frozen()
    {
        var (records, calls) = Records("""
            maquettiste.helper('probe', () => {
              const n = maquettiste.use('naming');
              let threw = false;
              try { n.extra = 1; } catch (e) { threw = e instanceof TypeError; }
              return [Object.isFrozen(n), Object.keys(n).join(','), n === maquettiste.use('naming'), threw, typeof n.class_name];
            });
            """);

        var result = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Call(records, calls, "probe"));

        Assert.Equal([true, "class_name,echo,spin", true, true, "function"], result);
    }

    [Fact]
    public void Use_of_a_pack_outside_the_libraries_is_a_type_error()
    {
        var (records, calls) = Records("maquettiste.helper('probe', () => maquettiste.use('currency').code());");
        var ex = Assert.Throws<ScriptErrorException>(() => Call(records, calls, "probe"));
        Assert.Equal(("MQ6016", Scripts.PackScript), (ex.Diagnostic.Rule, ex.Diagnostic.FilePath));
        Assert.Contains("TypeError", ex.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("'currency'", ex.Diagnostic.Message, StringComparison.Ordinal);

        var rule = Assert.Throws<ScriptErrorException>(() => new ScriptSandboxFactory().CreatePool(
            [Scripts.Source("const c = maquettiste.use('currency');", Scripts.RuleScript)], [], Scripts.Limits, 1, CancellationToken.None));
        Assert.Equal(("MQ5002", Scripts.RuleScript), (rule.Diagnostic.Rule, rule.Diagnostic.FilePath));
    }

    [Fact]
    public void Use_at_load_time_returns_the_object()
    {
        var (records, calls) = Records("""
            const naming = maquettiste.use('naming');
            maquettiste.helper('file', e => naming.class_name(e) + '.cs');
            """);

        Assert.Equal("CustomerRecord.cs", Call(records, calls, "file", null, new ResolvedFixture().Customer));
    }

    [Fact]
    public void Calling_a_library_helper_while_loading_is_a_type_error()
    {
        var naming = Pool(NamingCode, NamingScript, []);
        var ex = Assert.Throws<ScriptErrorException>(() => Pool("const early = maquettiste.use('naming').echo(1);", Scripts.PackScript, [Library("naming", naming)]));

        Assert.Equal(("MQ6016", Scripts.PackScript, 1), (ex.Diagnostic.Rule, ex.Diagnostic.FilePath, ex.Diagnostic.Line));
        Assert.Contains("while scripts load", ex.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_call_records_on_the_callers_recorder()
    {
        var (records, calls) = Records("maquettiste.helper('file', e => maquettiste.use('naming').class_name(e));");
        var reads = new ListRecorder();

        Call(records, calls, "file", reads, new ResolvedFixture().Customer);

        Assert.Contains("e:CUSTOMER", reads.Keys);

        // A selector or filter has no libraries: the call says why it cannot run.
        using var lease = records.Rent();
        var ex = Assert.Throws<ScriptErrorException>(() => lease.Sandbox.CallHelper("file", [new ResolvedFixture().Customer], Scripts.Ctx()));
        Assert.Contains("selectors and script filters run while planning", ex.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_helper_sees_its_own_parameters()
    {
        var (records, calls) = Records("maquettiste.helper('file', e => maquettiste.params.suffix + ':' + maquettiste.use('naming').class_name(e));");
        using var lease = records.Rent();

        var result = lease.Sandbox.CallHelper("file", [new ResolvedFixture().Invoice],
            Scripts.Ctx(parameters: new Dictionary<string, object?> { ["suffix"] = "Service" }) with { Libraries = calls });

        Assert.Equal("Service:InvoiceRecord", result);
    }

    [Fact]
    public void Values_cross_engines_as_they_cross_into_templates()
    {
        var (records, calls) = Records("""
            maquettiste.helper('round', e => {
              const back = maquettiste.use('naming').echo({ list: [1, 'two', null], flag: true, element: e });
              return [back.list.length, back.list[1], back.flag, back.element.name, back.element === e];
            });
            maquettiste.helper('fn', () => maquettiste.use('naming').echo(() => 1));
            """);

        var result = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Call(records, calls, "round", null, new ResolvedFixture().Customer));
        Assert.Equal([3L, "two", true, "Customer", true], result);

        var ex = Assert.Throws<ScriptErrorException>(() => Call(records, calls, "fn"));
        Assert.Contains("function", ex.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_limit_is_MQ6007_at_the_library_script()
    {
        var limits = new SandboxLimits { ScriptStatements = 50_000, ScriptTimeoutMs = 0 };
        var (records, calls) = Records("""
            maquettiste.helper('probe', () => maquettiste.use('naming').spin());
            maquettiste.helper('file', e => maquettiste.use('naming').class_name(e));
            """, limits);

        var ex = Assert.Throws<ScriptLimitException>(() => Call(records, calls, "probe"));

        Assert.Equal(("MQ6007", NamingScript), (ex.Diagnostic.Rule, ex.Diagnostic.FilePath));
        // The calling engine only unwound: its next call, and the library's next engine, still work.
        Assert.Equal("CustomerRecord", Call(records, calls, "file", null, new ResolvedFixture().Customer));
    }

    [Fact]
    public void A_library_reaches_its_own_closure()
    {
        const string CurrencyScript = "templates/currency/helpers.js";
        var currency = Pool("maquettiste.helper('code', c => String(c).toUpperCase());", CurrencyScript, []);
        var naming = Pool("""
            const currency = maquettiste.use('currency');
            maquettiste.helper('price_name', (e, c) => e.name + currency.code(c));
            """, NamingScript, [Library("currency", currency)]);
        var calls = new Calls();
        calls.Packs["currency"] = (currency, new Dictionary<string, object?>());
        calls.Packs["naming"] = (naming, NamingParameters);
        var records = Pool("maquettiste.helper('file', e => maquettiste.use('naming').price_name(e, 'eur'));", Scripts.PackScript,
            [Library("naming", naming)]);

        Assert.Equal("InvoiceEUR", Call(records, calls, "file", null, new ResolvedFixture().Invoice));
    }
}
