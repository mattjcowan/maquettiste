using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>JavaScript helpers, filters-by-pipe and pre-render transforms through the real sandbox pool (W4).</summary>
public sealed class ScriptHelperTests
{
    private static Dictionary<string, string> Js(string code) => new() { ["helpers.js"] = code };

    [Fact]
    public async Task A_javascript_helper_is_callable_and_pipeable()
    {
        var unit = await Adhoc.RenderAsync("{{ shout 'hi' }} {{ 'there' | shout }} {{ add 2 3 }}",
            files: Js("maquettiste.helper('shout', (s) => s.toUpperCase() + '!'); maquettiste.helper('add', (a, b) => a + b);"));
        Assert.Equal("HI! THERE! 5", unit.Text());
    }

    [Fact]
    public async Task Helper_results_become_template_values()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync(
            "{{ r = info entity }}{{ for kv in r }}{{ kv.key }}={{ kv.value }};{{ end }}|{{ r.names[1] }}|{{ r.nested.b }}|{{ (same entity).name }}",
            invoice,
            files: Js("maquettiste.helper('info', (e) => ({ z: e.attributes.length, a: e.name, names: e.attributes.map((x) => x.name), nested: { b: true } }));" +
                      "maquettiste.helper('same', (e) => e);"));
        Assert.Equal($"a=Invoice;names=[{string.Join(", ", invoice.Attributes.Select(a => "\"" + a.Name + "\""))}];nested={{b: true}};z={invoice.Attributes.Count};|{invoice.Attributes[1].Name}|true|Invoice", unit.Text());
    }

    [Fact]
    public async Task Transforms_fill_data_and_see_pack_parameters()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync("{{ data.count }} {{ data.label }} {{ data.first }}", invoice,
            unit: u => u with { Transforms = ["one", "two"] },
            files: Js("maquettiste.transform('one', (e) => ({ count: e.attributes.length, label: 'one', first: 'kept' }));" +
                      "maquettiste.transform('two', (e) => ({ label: maquettiste.params.prefix + e.name }));"),
            settings: new PackSettings { Parameters = new Dictionary<string, System.Text.Json.JsonElement> { ["prefix"] = System.Text.Json.JsonDocument.Parse("\"P:\"").RootElement } });
        Assert.Equal($"{invoice.Attributes.Count} P:Invoice kept", unit.Text());
        Assert.Contains(invoice.Attributes.MembershipKeys[0], unit.ReadKeys);
    }

    [Fact]
    public async Task Script_errors_fail_the_unit_with_the_script_location()
    {
        var error = (await Adhoc.RenderAsync("{{ boom }}", files: Js("maquettiste.helper('boom', () => {\n  throw new Error('kaput');\n});"))).Error();
        Assert.Equal("MQ6016", error.Rule);
        Assert.Equal(".maquettiste/templates/adhoc/helpers.js", error.FilePath);
        Assert.Contains("kaput", error.Message, StringComparison.Ordinal);

        var load = (await Adhoc.RenderAsync("x", files: Js("this is not javascript"))).Error();
        Assert.Equal("MQ6016", load.Rule);

        var noScripts = (await Adhoc.RenderAsync("x", unit: u => u with { Transforms = ["missing"] })).Error();
        Assert.Equal("MQ6016", noScripts.Rule);
        Assert.Equal("/units/0/transforms/0", noScripts.JsonPointer);

        var unknownTransform = (await Adhoc.RenderAsync("x", unit: u => u with { Transforms = ["missing"] }, files: Js("maquettiste.helper('h', () => 1);"))).Error();
        Assert.Equal("MQ6016", unknownTransform.Rule);
    }

    [Fact]
    public async Task Script_limits_fail_the_unit_with_MQ6007()
    {
        var model = Adhoc.WithSettings(await BillingModel.GetAsync(), s => s with { Limits = s.Limits with { ScriptStatements = 1000 } });
        var error = (await Adhoc.RenderAsync("{{ spin }}", files: Js("maquettiste.helper('spin', () => { for (;;) {} });"), model: model)).Error();
        Assert.Equal("MQ6007", error.Rule);
    }

    [Theory]
    [InlineData("pascal")]
    [InlineData("file")]
    [InlineData("string")]
    [InlineData("model")]
    [InlineData("include")]
    public async Task A_helper_named_like_a_builtin_is_MQ6013(string name)
    {
        var error = (await Adhoc.RenderAsync("x", files: Js($"maquettiste.helper('{name}', () => 1);"))).Error();
        Assert.Equal("MQ6013", error.Rule);
        Assert.Equal(".maquettiste/templates/adhoc/helpers.js", error.FilePath);
    }

    [Fact]
    public async Task Script_randomness_is_seeded_by_the_unit_key()
    {
        var files = Js("maquettiste.helper('r', () => Math.random()); maquettiste.helper('now', () => Date.now());");
        var first = await Adhoc.RenderAsync("{{ r }} {{ now }}", files: files);
        var second = await Adhoc.RenderAsync("{{ r }} {{ now }}", files: files);
        Assert.Equal(first.Text(), second.Text());
    }
}
