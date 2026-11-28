using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// A save validates the databases whose MQ4146 it may have changed: a database file with no <c>byConvention</c> gets a warning while an
/// entity is not bound to it (never a refused save), so a save that drops an entity's binding, or creates an entity with none, revisits that database. The
/// harness runs the real validator.
/// </summary>
public sealed class ModelStoreBindingScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool Unbound(SaveResult result, string name) =>
        result.Diagnostics.Any(d => d.Rule == "MQ4146" && d.Message.Contains(name, StringComparison.Ordinal));

    /// <summary>The billing store with main's <c>byConvention</c> removed: every entity is bound, so the save is clean.</summary>
    private static async Task<BillingStore> WithMainWithoutByConvention()
    {
        var s = await BillingStore.OpenAsync();
        var validator = ValidationFixture.Validator();
        s.Harness.Validator.Rule = (model, scope) => validator.ValidateAsync(model, scope, null, Ct).GetAwaiter().GetResult().Diagnostics;
        var main = s.Doc("database", "main");
        var saved = await s.Store.SaveAsync(main.Element.Id, BillingStore.Edit(main, n => n.Remove("byConvention")), main.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.DoesNotContain(saved.Diagnostics, d => d.Rule == "MQ4146");
        return s;
    }

    [Fact]
    public async Task Dropping_an_entitys_binding_reports_MQ4146_on_the_database()
    {
        await using var s = await WithMainWithoutByConvention();
        var invoice = s.Doc("entity", "Invoice");

        var result = await s.Store.SaveAsync(invoice.Element.Id, BillingStore.Edit(invoice, n => n.Remove("bindings")), invoice.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.True(Unbound(result, "Invoice"));
        Assert.Equal(s.Id("database", "main"), result.Diagnostics.Single(d => d.Rule == "MQ4146").ElementId);
    }

    [Fact]
    public async Task Creating_an_entity_with_no_binding_reports_MQ4146_on_the_database()
    {
        await using var s = await WithMainWithoutByConvention();
        var json = $$"""
            {"kind":"entity","name":"Shipment","package":"{{s.Id("package", "Billing")}}",
             "key":{"attributes":["01K6SHP0000000000000000001"]},"attributes":[{"id":"01K6SHP0000000000000000001","name":"id","type":"uuid","required":true}]}
            """;

        var created = await s.Store.CreateAsync(Encoding.UTF8.GetBytes(json), ChangeSource.Editor, Ct);

        // A database converted on 0.28.0 has no byConvention: the save gate takes the new entity, with a warning naming it.
        Assert.True(created.Outcome == SaveOutcome.Saved, string.Join("; ", created.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.True(Unbound(created, "Shipment"));
        var finding = created.Diagnostics.Single(d => d.Rule == "MQ4146");
        Assert.Equal(Maquettiste.Engine.Diagnostics.DiagnosticSeverity.Warning, finding.Severity);
        Assert.Contains("entity 'Shipment' is not bound to it, as a new entity is until it gets a table", finding.Message, StringComparison.Ordinal);
    }
}
