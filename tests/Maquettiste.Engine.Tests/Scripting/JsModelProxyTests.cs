using System.Text.Json;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Tests.Scripting;

public sealed class JsModelProxyTests
{
    private static (IScriptSandboxPool Pool, ResolvedFixture Fixture) Setup(string code) => (Scripts.Pool(code), new ResolvedFixture());

    private static object? Filter(IScriptSandboxPool pool, string name, IResolvedObject element, ResolvedModel model, ListRecorder reads)
    {
        using var lease = pool.Rent();
        return lease.Sandbox.Filter(name, element, model, Scripts.Ctx(reads));
    }

    [Fact]
    public void Properties_appear_in_camel_case_and_nested_objects_are_proxies()
    {
        var (pool, f) = Setup("""
            maquettiste.transform('view', (e, model) => ({
              name: e.name, plural: e.pluralName, pkg: e.package.qualifiedName, tags: Array.from(e.tags),
              stereo: e.stereotypes[0].key, audited: e.hasStereotype('audited'), core: e.hasTag('core'), other: e.hasTag('x'),
              attrs: e.attributes.map(a => a.name + ':' + a.type.builtin + (a.required ? '!' : '')),
              length: e.attributes[0].length, keys: Object.keys(e.properties), props: e.properties,
              owner: e.attributes[0].owner === e, same: model.find(e.id) === e, entityCount: model.entities.length,
            }));
            """);
        using (pool)
        {
            using var lease = pool.Rent();
            var data = lease.Sandbox.Transform("view", f.Customer, f.Model, Scripts.Ctx());

            Assert.Equal("Customer", data["name"]);
            Assert.Equal("Customers", data["plural"]);
            Assert.Equal("Billing", data["pkg"]);
            Assert.Equal(["core"], (IReadOnlyList<object?>)data["tags"]!);
            Assert.Equal("audited", data["stereo"]);
            Assert.Equal(true, data["audited"]);
            Assert.Equal(true, data["core"]);
            Assert.Equal(false, data["other"]);
            Assert.Equal(["name:string!", "email:string"], (IReadOnlyList<object?>)data["attrs"]!);
            Assert.Equal(120L, data["length"]);
            Assert.Equal(["alpha", "zeta"], (IReadOnlyList<object?>)data["keys"]!);
            var props = (IReadOnlyDictionary<string, object?>)data["props"]!;
            Assert.Equal("a", props["alpha"]);
            Assert.Equal(true, data["owner"]);
            Assert.Equal(true, data["same"]);
            Assert.Equal(2L, data["entityCount"]);
        }
    }

    [Fact]
    public void Every_member_read_records_the_object_dependencies()
    {
        var (pool, f) = Setup("maquettiste.filter('named', e => e.name === 'Customer');");
        using (pool)
        {
            var reads = new ListRecorder();

            Assert.Equal(true, Filter(pool, "named", f.Customer, f.Model, reads));

            Assert.Equal(["e:CUSTOMER", "r:CUSTOMER"], reads.Keys.Distinct());
        }
    }

    [Fact]
    public void Enumerating_a_list_records_its_membership_keys()
    {
        var (pool, f) = Setup("maquettiste.filter('count', (e, model) => model.entities.length === 2);");
        using (pool)
        {
            var reads = new ListRecorder();

            Assert.Equal(true, Filter(pool, "count", f.Invoice, f.Model, reads));

            Assert.Equal(["k:entity"], reads.Keys.Distinct());
        }
    }

    [Fact]
    public void Reads_are_recorded_into_the_current_call_even_for_cached_proxies()
    {
        var (pool, f) = Setup("maquettiste.filter('attr', e => e.attributes[0].name === 'name');");
        using (pool)
        {
            using var lease = pool.Rent();
            var first = new ListRecorder();
            var second = new ListRecorder();

            lease.Sandbox.Filter("attr", f.Customer, f.Model, Scripts.Ctx(first));
            lease.Sandbox.Filter("attr", f.Customer, f.Model, Scripts.Ctx(second));

            Assert.Equal(first.Keys.Distinct(), second.Keys.Distinct());
            Assert.Contains("e:CUSTOMER", second.Keys);
            Assert.Contains("m:customer-attributes", second.Keys);
        }
    }

    [Fact]
    public void Looking_up_a_missing_id_records_its_element_key()
    {
        var (pool, f) = Setup("maquettiste.filter('lookup', (e, model) => model.find('NOT_YET') === null);");
        using (pool)
        {
            var reads = new ListRecorder();

            Assert.Equal(true, Filter(pool, "lookup", f.Customer, f.Model, reads));

            Assert.Equal(["e:NOT_YET"], reads.Keys.Distinct());
        }
    }

    [Fact]
    public void A_selector_returns_ids_from_elements_and_strings()
    {
        var (pool, f) = Setup("maquettiste.selector('pick', model => [...model.entities.filter(e => e.hasTag('core')), 'INVOICE']);");
        using (pool)
        {
            using var lease = pool.Rent();
            var reads = new ListRecorder();

            var ids = lease.Sandbox.Select("pick", f.Model, Scripts.Ctx(reads));

            Assert.Equal(["CUSTOMER", "INVOICE"], ids);
            Assert.Contains("k:entity", reads.Keys);
            Assert.Contains("e:CUSTOMER", reads.Keys);
        }
    }

    [Theory]
    [InlineData("e.name = 'Other'")]
    [InlineData("e.brandNew = 1")]
    [InlineData("delete e.name")]
    [InlineData("Object.defineProperty(e, 'name', { value: 'x' })")]
    [InlineData("e.attributes.push(1)")]
    [InlineData("e.attributes[0] = null")]
    [InlineData("e.properties.alpha = 'b'")]
    [InlineData("e.tags.length = 0")]
    [InlineData("Object.setPrototypeOf(e, null)")]
    public void Model_objects_refuse_writes(string statement)
    {
        var (pool, f) = Setup($"maquettiste.filter('write', e => {{ {statement}; return true; }});");
        using (pool)
        {
            var error = Assert.Throws<ScriptErrorException>(() => Filter(pool, "write", f.Customer, f.Model, new ListRecorder()));

            Assert.Equal("MQ6016", error.Diagnostic.Rule);
            Assert.Contains("TypeError", error.Diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal("CUSTOMER", error.Diagnostic.ElementId);
            Assert.Equal("Customer", f.Customer.Name);
        }
    }

    [Fact]
    public void Model_objects_report_as_frozen()
    {
        var (pool, f) = Setup("maquettiste.filter('frozen', e => Object.isFrozen(e) && !Object.isExtensible(e.attributes));");
        using (pool)
            Assert.Equal(true, Filter(pool, "frozen", f.Customer, f.Model, new ListRecorder()));
    }

    [Fact]
    public void Values_cross_as_json_like_data_both_ways()
    {
        using var pool = Scripts.Pool("maquettiste.helper('echo', (...args) => args);");
        using var json = JsonDocument.Parse("""{"b":[1,2.5,"x"],"a":null}""");
        var f = new ResolvedFixture();

        using var lease = pool.Rent();
        var result = (IReadOnlyList<object?>)lease.Sandbox.CallHelper("echo",
            ["s", 3, 2.5, true, null, new[] { 1, 2 }, new Dictionary<string, object?> { ["z"] = 1, ["a"] = "x" }, json.RootElement, f.Customer],
            Scripts.Ctx())!;

        Assert.Equal("s", result[0]);
        Assert.Equal(3L, result[1]);
        Assert.Equal(2.5, result[2]);
        Assert.Equal(true, result[3]);
        Assert.Null(result[4]);
        Assert.Equal([1, 2], (int[])result[5]!);
        var map = (IReadOnlyDictionary<string, object?>)result[6]!;
        Assert.Equal(["a", "z"], map.Keys);
        var fromJson = (IReadOnlyDictionary<string, object?>)result[7]!;
        Assert.Equal(["b", "a"], fromJson.Keys);
        Assert.Equal([1L, 2.5, "x"], (IReadOnlyList<object?>)fromJson["b"]!);
        Assert.Same(f.Customer, result[8]);
    }

    [Fact]
    public void Mutable_clr_arguments_are_read_afresh_on_every_call()
    {
        using var pool = Scripts.Pool("""
            maquettiste.helper('list', xs => Array.from(xs).join(','));
            maquettiste.helper('map', m => Object.keys(m).join(','));
            maquettiste.helper('param', () => String(maquettiste.params.mode));
            """);
        var list = new List<object?> { 1 };
        var map = new Dictionary<string, object?> { ["a"] = 1 };
        var parameters = new Dictionary<string, object?> { ["mode"] = "first" };
        using var lease = pool.Rent();

        Assert.Equal("1", lease.Sandbox.CallHelper("list", [list], Scripts.Ctx()));
        Assert.Equal("a", lease.Sandbox.CallHelper("map", [map], Scripts.Ctx()));
        Assert.Equal("first", lease.Sandbox.CallHelper("param", [], Scripts.Ctx(parameters: parameters)));
        list.Add(2);
        map["b"] = 2;
        parameters["mode"] = "second";

        Assert.Equal("1,2", lease.Sandbox.CallHelper("list", [list], Scripts.Ctx()));
        Assert.Equal("a,b", lease.Sandbox.CallHelper("map", [map], Scripts.Ctx()));
        Assert.Equal("second", lease.Sandbox.CallHelper("param", [], Scripts.Ctx(parameters: parameters)));
    }

    [Fact]
    public void Resolved_lists_keep_their_identity()
    {
        var (pool, f) = Setup("maquettiste.filter('same', (e, model) => model.entities === model.entities && e.attributes === model.find(e.id).attributes);");
        using (pool)
            Assert.Equal(true, Filter(pool, "same", f.Customer, f.Model, new ListRecorder()));
    }

    [Fact]
    public void A_huge_sparse_array_result_is_a_limit_error_before_the_host_allocates_it()
    {
        var (pool, f) = Setup("""
            maquettiste.helper('sparse', n => { const a = []; a.length = n; return a; });
            maquettiste.selector('sparse', () => { const a = []; a.length = 2 ** 31; return a; });
            """);
        using (pool)
        {
            using var lease = pool.Rent();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var helper = Assert.Throws<ScriptLimitException>(() => lease.Sandbox.CallHelper("sparse", [100_000_000L], Scripts.Ctx()));
            var selector = Assert.Throws<ScriptLimitException>(() => lease.Sandbox.Select("sparse", f.Model, Scripts.Ctx()));

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms");
            Assert.Equal("MQ6007", helper.Diagnostic.Rule);
            Assert.Contains("memory limit", helper.Diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal("MQ6007", selector.Diagnostic.Rule);
        }
    }

    [Fact]
    public void Objects_with_a_null_prototype_leave_as_plain_objects()
    {
        using var pool = Scripts.Pool("maquettiste.helper('bare', () => Object.assign(Object.create(null), { a: 1 }));");

        var map = (IReadOnlyDictionary<string, object?>)pool.Helper("bare")!;

        Assert.Equal(1L, map["a"]);
    }

    [Theory]
    [InlineData("() => () => 1", "function")]
    [InlineData("() => Symbol('x')", "symbol")]
    [InlineData("() => Promise.resolve(1)", "promise")]
    [InlineData("async () => 1", "promise")]
    [InlineData("() => new Date()", "Date")]
    [InlineData("() => { const a = {}; a.self = a; return a; }", "nests deeper")]
    [InlineData("() => new Map([['a', 1]])", "is a Map")]
    [InlineData("() => ({ nested: new Set([1, 2]) })", "is a Set")]
    [InlineData("() => [/x/g]", "instance of RegExp")]
    [InlineData("() => new Error('boom')", "instance of Error")]
    [InlineData("() => { class Money { constructor(v) { this.v = v; } } return new Money(1); }", "instance of Money")]
    public void Unsupported_results_are_script_errors(string helper, string expected)
    {
        using var pool = Scripts.Pool($"maquettiste.helper('bad', {helper});");

        var error = Assert.Throws<ScriptErrorException>(() => pool.Helper("bad"));

        Assert.Equal("MQ6016", error.Diagnostic.Rule);
        Assert.Contains(expected, error.Diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(Scripts.PackScript, error.Diagnostic.FilePath);
    }

    [Fact]
    public void Integral_numbers_leave_as_long_and_fractions_as_double()
    {
        using var pool = Scripts.Pool("maquettiste.helper('nums', () => [1, -0, 2 ** 53, 2 ** 60, 0.1 + 0.2, NaN, 1 / 0]);");

        var result = (IReadOnlyList<object?>)pool.Helper("nums")!;

        Assert.Equal(1L, result[0]);
        Assert.Equal(0L, result[1]);
        Assert.IsType<double>(result[2]);
        Assert.IsType<double>(result[3]);
        Assert.Equal(0.1 + 0.2, result[4]);
        Assert.True(double.IsNaN((double)result[5]!));
        Assert.True(double.IsPositiveInfinity((double)result[6]!));
    }
}
