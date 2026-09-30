using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The gate 3 fixture <c>tests/fixtures/models/processes</c> (phase-3-design.md section 8.1): it loads with no diagnostic, validates
/// with no error and no warning, and holds the processes, actors, entities, mappings and fourteen scenarios the design lists.
/// </summary>
public sealed class ProcessFixtureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int CountStates(IEnumerable<ProcessState> states) => states.Sum(s => 1 + CountStates(s.States));

    [Fact]
    public async Task The_gate_3_fixture_loads_validates_and_holds_what_the_design_lists()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "processes");
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var model = store.Current!;

        Assert.Empty(model.LoadDiagnostics);
        var report = await EngineServices.Create(h.Options).Validator.ValidateAsync(model, ValidationScope.All, null, Ct);
        Assert.All(report.Diagnostics, d => Assert.True(d.Severity == DiagnosticSeverity.Info && d.Rule == "MQ9005", d.Rule + " " + d.Message));

        Assert.Equal(["Purchasing", "Sales"], model.All<Package>().Select(p => p.Name));
        Assert.Equal(["GateSignature", "ProcessInstance", "PurchaseRequest", "SalesOrder", "SalesOrderHistory"], model.All<Entity>().Select(e => e.Name));
        Assert.Equal(5, model.All<Mapping>().Count);
        Assert.Equal(ConventionMapping.None, Assert.Single(model.All<Database>()).ByConvention);
        Assert.Equal(8, model.All<Actor>().Count);
        Assert.Equal(["persona"], model.All<Actor>().Single(a => a.Name == "BudgetHolder").Stereotypes);

        var sales = model.All<Process>().Single(p => p.Name == "SalesOrderLifecycle");
        var purchase = model.All<Process>().Single(p => p.Name == "PurchaseApproval");
        Assert.Equal((ProcessUse.Lifecycle, ProcessUse.Orchestration), (sales.Use, purchase.Use));
        Assert.Equal(sales.Id, model.All<Entity>().Single(e => e.Name == "SalesOrder").Lifecycle);
        Assert.Equal((17, 14, 9, 2), (CountStates(sales.States), sales.Transitions.Count, sales.Events.Count, sales.Guards.Count));
        Assert.Equal((13, 10, 4, 1), (CountStates(purchase.States), purchase.Transitions.Count, purchase.Events.Count, purchase.Actions.Count));
        Assert.Single(sales.Guards, g => g.Expression is null); // the stub
        Assert.Equal([2, 2], new[] { sales, purchase }.Select(p => p.Transitions.Single(t => t.Gate is not null).Gate!.Required));
        Assert.Equal(["Draft", "CreditReview", "Fulfilment", "OnHold", "Completed", "Cancelled"],
            model.All<EnumType>().Single(e => e.Name == "SalesOrderStatus").Members.Select(m => m.Name));

        var scenarios = model.All<Scenario>();
        Assert.Equal(14, scenarios.Count);
        Assert.Equal(8, scenarios.Count(s => s.Process == sales.Id));
        Assert.Equal(6, scenarios.Count(s => s.Process == purchase.Id));
        Assert.Equal(50, scenarios.Sum(s => s.Steps.Count));
    }
}
