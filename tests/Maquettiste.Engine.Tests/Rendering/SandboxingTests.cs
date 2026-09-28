using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>
/// What templates cannot do (SPEC S19): read .NET members beyond the documented ones, write to the model, the variables or the
/// builtins, use non-deterministic builtins (MQ6012), or include files outside the pack; and how errors are located.
/// </summary>
public sealed class SandboxingTests
{
    [Fact]
    public async Task Only_template_visible_properties_are_readable()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync(
            "[{{ entity.dependencies }}][{{ entity.get_type }}][{{ entity.to_string }}][{{ entity.has_stereotype }}][{{ model.source }}][{{ model.settings }}]" +
            "[{{ model.find }}][{{ model.entities.membership_keys }}][{{ entity.name.length }}][{{ unit.equality_contract }}]", invoice);
        Assert.Equal("[][][][][][][][][][]", unit.Text());
    }

    [Fact]
    public async Task Object_keys_list_only_the_visible_members()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var unit = await Adhoc.RenderAsync("{{ entity | object.keys | array.join ',' }}", invoice);
        var keys = unit.Text().Split(',');
        Assert.Contains("attributes", keys);
        Assert.Contains("plural_name", keys);
        Assert.DoesNotContain("dependencies", keys);
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    [Theory]
    [InlineData("{{ entity.name = 'x' }}")]
    [InlineData("{{ model = 1 }}")]
    [InlineData("{{ element = 1 }}")]
    [InlineData("{{ string.foo = 1 }}")]
    [InlineData("{{ date.format = '%Y' }}")]
    [InlineData("{{ pack.params.namespace = 'x' }}")]
    public async Task Nothing_shared_can_be_written(string template)
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var error = (await Adhoc.RenderAsync(template, invoice)).Error();
        Assert.Equal("MQ6006", error.Rule);
    }

    [Fact]
    public async Task Assignments_are_per_unit()
    {
        var model = await BillingModel.GetAsync();
        using var pack = new TempPack("adhoc", new Dictionary<string, string> { ["main.scriban"] = "{{ if element.name == 'Invoice' }}{{ x = 1 }}{{ pascal = 'shadowed' }}{{ end }}{{ pascal }}|{{ x ?? 'unset' }}" });
        var packUnit = new PackUnit { Id = "main", Template = "main.scriban", For = "each entity", Output = "{{ element.name }}.txt" };
        var loaded = pack.Load(RenderKit.Manifest("adhoc", packUnit));
        var units = RenderKit.Plan(model, loaded);
        var rendered = await RenderKit.RenderAllAsync(RenderKit.NewRenderer(), units, RenderKit.Context(model, [loaded], 1));
        foreach (var unit in rendered)
        {
            var name = ((Engine.Resolution.REntity)unit.Unit.Element!).Name;
            Assert.Equal(name == "Invoice" ? "shadowed|1" : "MQ6006", name == "Invoice" ? unit.Text() : unit.Error().Rule);
        }
    }

    [Theory]
    [InlineData("date.now")]
    [InlineData("date.utc_now")]
    [InlineData("math.random 1 10")]
    [InlineData("math.uuid")]
    [InlineData("object.eval '1 + 1'")]
    [InlineData("object.eval_template '{{ 1 }}'")]
    public async Task Non_deterministic_builtins_fail_with_MQ6012(string call)
    {
        var error = (await Adhoc.RenderAsync("ok\n  {{ " + call + " }}")).Error();
        Assert.Equal("MQ6012", error.Rule);
        Assert.Equal(".maquettiste/templates/adhoc/main.scriban", error.FilePath);
        Assert.Equal(2, error.Line);
        Assert.Equal(6, error.Column);
    }

    [Theory]
    [InlineData("../outside.scriban")]
    [InlineData("/etc/hosts")]
    [InlineData("sub/../../outside.scriban")]
    [InlineData("C:/windows/win.ini")]
    [InlineData("")]
    public async Task Includes_are_confined_to_the_pack(string path)
    {
        var error = (await Adhoc.RenderAsync("{{ include '" + path + "' }}")).Error();
        Assert.Equal("MQ6006", error.Rule);
        Assert.Contains("include", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_symbolic_link_out_of_the_pack_is_refused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "outside-" + Guid.NewGuid().ToString("N") + ".scriban");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllTextAsync(outside, "secret", TestContext.Current.CancellationToken);
        try
        {
            using var pack = new TempPack("adhoc", new Dictionary<string, string> { ["main.scriban"] = "{{ include 'link.scriban' }}", ["inside.scriban"] = "fine" });
            try
            {
                File.CreateSymbolicLink(Path.Combine(pack.Root, "link.scriban"), outside);
                File.CreateSymbolicLink(Path.Combine(pack.Root, "ok.scriban"), Path.Combine(pack.Root, "inside.scriban"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Skip("Symbolic links are not available: " + ex.Message);
            }

            var model = await BillingModel.GetAsync();
            var packUnit = new PackUnit { Id = "main", Template = "main.scriban", For = "model", Output = "out.txt" };
            var loaded = pack.Load(RenderKit.Manifest("adhoc", packUnit));
            var refused = await RenderKit.NewRenderer().RenderOneAsync(new PlannedUnit("adhoc/main", loaded, packUnit, null, "s"),
                RenderKit.Context(model, [loaded], 1), TestContext.Current.CancellationToken);
            var error = refused.Error();
            Assert.Equal("MQ6003", error.Rule);
            Assert.Contains("outside the pack", error.Message, StringComparison.Ordinal);

            pack.Write("main.scriban", "{{ include 'ok.scriban' }}");
            var allowed = await RenderKit.NewRenderer().RenderOneAsync(new PlannedUnit("adhoc/main", loaded, packUnit, null, "s"),
                RenderKit.Context(model, [loaded], 1), TestContext.Current.CancellationToken);
            Assert.Equal("fine", allowed.Text());
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task A_unit_template_outside_the_pack_is_refused()
    {
        var error = (await Adhoc.RenderAsync("x", unit: u => u with { Template = "../main.scriban" })).Error();
        Assert.Equal("MQ6003", error.Rule);
        var missing = (await Adhoc.RenderAsync("x", unit: u => u with { Template = "missing.scriban" })).Error();
        Assert.Equal("MQ6003", missing.Rule);
    }

    [Fact]
    public async Task Parse_errors_carry_the_template_line_and_column()
    {
        var unit = await Adhoc.RenderAsync("first line\nsecond {{ for x in }}\n");
        Assert.True(unit.Failed);
        Assert.Empty(unit.Files);
        var error = unit.Diagnostics[0];
        Assert.Equal("MQ6003", error.Rule);
        Assert.Equal(".maquettiste/templates/adhoc/main.scriban", error.FilePath);
        Assert.Equal(2, error.Line);
        Assert.True(error.Column > 8, "column " + error.Column);
    }

    [Fact]
    public async Task Runtime_errors_carry_the_template_the_element_and_the_position()
    {
        var model = await BillingModel.GetAsync();
        var invoice = model.Entities.Single(e => e.Name == "Invoice");
        var files = new Dictionary<string, string> { ["_bad.scriban"] = "ok\n  {{ entity.base.name }}" };
        var error = (await Adhoc.RenderAsync("{{ include '_bad.scriban' }}", invoice, files: files)).Error();
        Assert.Equal("MQ6006", error.Rule);
        Assert.Equal(invoice.Id, error.ElementId);
        Assert.Equal(".maquettiste/templates/adhoc/_bad.scriban", error.FilePath);
        Assert.Equal(2, error.Line);

        var helper = (await Adhoc.RenderAsync("\n\n{{ sql_quote 'x' 'db2' }}")).Error();
        Assert.Equal("MQ6006", helper.Rule);
        Assert.Equal(3, helper.Line);
        Assert.Contains("Unknown SQL dialect 'db2'", helper.Message, StringComparison.Ordinal);

        var unknown = (await Adhoc.RenderAsync("{{ not_a_variable }}")).Error();
        Assert.Equal("MQ6006", unknown.Rule);
        Assert.Equal((1, 4), (unknown.Line, unknown.Column));
    }

    [Fact]
    public async Task Output_is_normalized_to_lf()
    {
        var unit = await Adhoc.RenderAsync("a\r\nb\rc{{ \"\\r\\n\" }}d");
        Assert.Equal("a\nb\nc\nd", unit.Text());
    }
}
