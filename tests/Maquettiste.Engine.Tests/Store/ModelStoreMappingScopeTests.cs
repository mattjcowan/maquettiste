using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Tests.Validation;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// A save validates the entities whose MQ4012 it may have changed (engine-design 7.2a): every entity when a database changes,
/// the entity a deleted mapping named, and the entity a retargeted mapping named before. The harness runs the real validator.
/// </summary>
public sealed class ModelStoreMappingScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Unplaced(SaveResult result, string name) =>
        result.Diagnostics.Any(d => d.Rule == "MQ4012" && d.Message.Contains($"'{name}'", StringComparison.Ordinal));

    private static async Task<BillingStore> WithMainTakingNothing()
    {
        var s = await BillingStore.OpenAsync();
        var validator = ValidationFixture.Validator();
        s.Harness.Validator.Rule = (model, scope) => validator.ValidateAsync(model, scope, null, Ct).GetAwaiter().GetResult().Diagnostics;
        var main = s.Doc("database", "main");
        var saved = await s.Store.SaveAsync(main.Element.Id, BillingStore.Edit(main, n => n["byConvention"] = "none"), main.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.True(Unplaced(saved, "Customer"));
        Assert.False(Unplaced(saved, "Invoice"));
        return s;
    }

    [Fact]
    public async Task Deleting_a_mapping_reports_MQ4012_on_the_entity_it_placed()
    {
        await using var s = await WithMainTakingNothing();
        var mapping = s.Doc("mapping", "Invoice in main");

        var result = await s.Store.DeleteAsync(mapping.Element.Id, mapping.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.True(Unplaced(result, "Invoice"));
    }

    [Fact]
    public async Task Retargeting_a_mapping_reports_MQ4012_on_the_entity_it_named_before()
    {
        await using var s = await WithMainTakingNothing();
        var main = s.Id("database", "main");
        var json = $"{{\"kind\":\"mapping\",\"name\":\"Customer in main\",\"database\":\"{main}\",\"entity\":\"{s.Id("entity", "Customer")}\"}}";
        var created = await s.Store.CreateAsync(Encoding.UTF8.GetBytes(json), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.False(Unplaced(created, "Customer"));

        var product = s.Id("entity", "Product");
        var result = await s.Store.SaveAsync(created.Id!, BillingStore.Edit(created.Current!, n => n["entity"] = product), created.Hash!, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.True(Unplaced(result, "Customer"));
        Assert.False(Unplaced(result, "Product"));
    }
}
