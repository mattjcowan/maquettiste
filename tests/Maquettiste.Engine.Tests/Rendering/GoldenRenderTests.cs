using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>
/// Golden renders of the billing-demo pack (tests/fixtures/templates/billing-demo) over the billing fixture: every built-in helper,
/// pack helpers and a transform through the real sandbox, a partial, pair mode, file blocks and both custom-delimiter templates.
/// The golden tree is tests/fixtures/templates/golden/billing-demo (rewrite with MAQUETTISTE_UPDATE_GOLDEN=1).
/// </summary>
public sealed class GoldenRenderTests
{
    [Fact]
    public async Task Billing_demo_pack_matches_the_golden_tree()
    {
        var model = await BillingModel.GetAsync();
        var pack = RenderKit.LoadPack(RenderKit.FixturePackRoot(RenderKit.DemoPack), RenderKit.DemoPack, new PackSettings { Output = "out" });
        var units = RenderKit.Plan(model, pack);
        var rendered = await RenderKit.RenderAllAsync(RenderKit.NewRenderer(), units, RenderKit.Context(model, [pack]));

        Assert.Empty(rendered.Where(u => u.Failed).Select(u => u.Unit.Key + ": " + string.Join("; ", u.Diagnostics.Select(d => $"{d.Rule} {d.FilePath}:{d.Line}:{d.Column} {d.Message}"))));
        Assert.Equal(units.Select(u => u.Key), rendered.Select(u => u.Unit.Key));

        var output = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "golden-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenderKit.WriteTree(output, rendered);
            Golden.AssertMatches(Fixtures.Path("templates", "golden", RenderKit.DemoPack), output);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Output_paths_take_the_pack_output_prefix_and_roles_follow_the_mode()
    {
        var model = await BillingModel.GetAsync();
        var pack = RenderKit.LoadPack(RenderKit.FixturePackRoot(RenderKit.DemoPack), RenderKit.DemoPack, new PackSettings { Output = "gen/" });
        var units = RenderKit.Plan(model, pack, "entity", "registrations");
        var rendered = await RenderKit.RenderAllAsync(RenderKit.NewRenderer(), units, RenderKit.Context(model, [pack]));

        var invoice = rendered.Single(u => u.Unit.Element is Engine.Resolution.REntity { Name: "Invoice" });
        Assert.Equal(["gen/src/Billing/Invoice.g.cs", "gen/src/Billing/Invoice.cs"], invoice.Files.Select(f => f.Path));
        Assert.Equal([FileRole.Main, FileRole.Companion], invoice.Files.Select(f => f.Role));

        var registrations = rendered.Single(u => u.Unit.Unit.Id == "registrations");
        Assert.All(registrations.Files, f => Assert.Equal(FileRole.Block, f.Role));
        Assert.Equal(model.Packages.Select(p => "gen/src/" + p.Name + "/Registrations.g.cs"), registrations.Files.Select(f => f.Path));
        Assert.Empty(registrations.Diagnostics);
        Assert.All(rendered.SelectMany(u => u.Files), f => Assert.DoesNotContain('\r', f.Text));
    }
}
