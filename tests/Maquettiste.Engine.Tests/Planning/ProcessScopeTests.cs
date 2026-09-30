using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;
using static Maquettiste.Engine.Tests.Planning.PlanningKit;

namespace Maquettiste.Engine.Tests.Planning;

/// <summary>
/// The <c>each process</c>, <c>each actor</c> and <c>each scenario</c> scopes (phase-3-design.md section 7.1) over the gate 3 fixture:
/// one unit per element keyed by its id, <c>where</c> by tags, stereotypes, categories and packages (a scenario's package is its
/// process's; an actor has none), and <c>generation.skip</c>.
/// </summary>
public sealed class ProcessScopeTests
{
    private const string HappyPath = "01JQSCN0000000000000000008";

    private static readonly ResolvedModel Model = Resolve(ValidationFixture.LoadRepo(Fixtures.Path("models", "processes"), Change));

    // The fixture has no tags or hints: the purchase process gets a tag, one of its scenarios a skip hint for pack "p" only.
    private static Element Change(Element element) => element switch
    {
        Process { Name: "PurchaseApproval" } p => p with { Tags = ["approval"] },
        Scenario { Name: "ChangesRequested" } s => s with { Generation = new Dictionary<string, GenerationHints> { ["p"] = new() { Skip = true } } },
        _ => element,
    };

    [Fact]
    public async Task Each_scope_plans_one_unit_per_element_keyed_by_its_id()
    {
        var plan = await Plan(Pack("p", [Unit("process", "each process"), Unit("actor", "each actor"), Unit("scenario", "each scenario")]));

        Assert.Empty(plan.Diagnostics);
        Assert.Equal(Model.Processes.Select(p => "p/process:" + p.Id).Order(StringComparer.Ordinal), plan.Units.Where(u => u.Unit.Id == "process").Select(u => u.Key));
        Assert.Equal(8, plan.Units.Count(u => u.Unit.Id == "actor"));
        Assert.All(plan.Units.Where(u => u.Unit.Id == "actor"), u => Assert.IsType<RActor>(u.Element));
        // Fourteen scenarios, one skipped by its hint for this pack.
        var scenarios = plan.Units.Where(u => u.Unit.Id == "scenario").ToList();
        Assert.Equal(13, scenarios.Count);
        Assert.DoesNotContain(scenarios, u => ((RScenario)u.Element!).Name == "ChangesRequested");
        Assert.Contains(scenarios, u => u.Key == "p/scenario:" + HappyPath);

        // The hint names pack "p" only: another pack still plans it.
        var other = await Plan(Pack("q", [Unit("scenario", "each scenario")]));
        Assert.Equal(14, other.Units.Count);
    }

    [Theory]
    [InlineData("each process", "packages", "Sales", "SalesOrderLifecycle")]
    [InlineData("each process", "tags", "approval", "PurchaseApproval")]
    [InlineData("each process", "notTags", "approval", "SalesOrderLifecycle")]
    [InlineData("each actor", "stereotypes", "persona", "BudgetHolder")]
    [InlineData("each actor", "packages", "Sales", "")]
    [InlineData("each actor", "notPackages", "Sales", "Approver,BudgetHolder,ComplianceOfficer,CreditManager,FinanceController,FinanceDirector,ProcurementSystem,Requester")]
    [InlineData("each scenario", "packages", "Purchasing", "BudgetRejected,ComplianceFirstThenBudget,HappyPath,RejectionWithReason,ReminderAfterFiveDays")]
    [InlineData("each scenario", "notPackages", "Purchasing", "CancelBeforeShipping,CancelRefusedAfterShipping,CreditApproved,CreditRejectedAndResubmitted,HoldAndRelease,PaymentOverdue,RepeatSignerRefused,SmallOrder")]
    public async Task Where_filters_processes_actors_and_scenarios(string scope, string filter, string value, string expected)
    {
        var where = filter switch
        {
            "packages" => new UnitWhere { Packages = [value] },
            "notPackages" => new UnitWhere { NotPackages = [value] },
            "tags" => new UnitWhere { Tags = [value] },
            "notTags" => new UnitWhere { NotTags = [value] },
            _ => new UnitWhere { Stereotypes = [value] },
        };
        var plan = await Plan(Pack("p", [Unit("u", scope, where)]));

        Assert.Empty(plan.Diagnostics);
        Assert.Equal(expected, string.Join(",", plan.Units.Select(u => ((RElement)u.Element!).Name).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Explain_and_preview_name_the_new_scopes()
    {
        var pack = Pack("p", [Unit("process", "each process"), Unit("scenario", "each scenario")]);
        var sales = Model.Processes.Single(p => p.Name == "SalesOrderLifecycle");
        var scenario = Model.Scenarios.Single(s => s.Id == HappyPath);

        Assert.Null(UnitPlanner.OutOfScope(Model, pack, pack.Manifest.Units[0], sales, new ScriptSandboxFactory(), Ct));
        Assert.Contains("expects a process", UnitPlanner.OutOfScope(Model, pack, pack.Manifest.Units[0], scenario, new ScriptSandboxFactory(), Ct), StringComparison.Ordinal);
        Assert.Contains("expects a scenario", UnitPlanner.OutOfScope(Model, pack, pack.Manifest.Units[1], null, new ScriptSandboxFactory(), Ct), StringComparison.Ordinal);
        var skipped = Model.Scenarios.Single(s => s.Name == "ChangesRequested");
        Assert.Equal("skip-hint", UnitPlanner.WhyNot(Model, pack, pack.Manifest.Units[1], skipped, new ScriptSandboxFactory(), Ct)?.Kind);
    }

    [Fact]
    public void The_scope_list_names_the_new_scopes()
    {
        Assert.Contains("each process", UnitRules.Scopes);
        Assert.Contains("each actor", UnitRules.Scopes);
        Assert.Contains("each scenario", UnitRules.Scopes);
        Assert.Contains("'each actor'", UnitRules.UnknownScope("u", "each actors"), StringComparison.Ordinal);
    }

    // The fixture's settings allow db/ and the C# pack's src/ folders (phase 3 round P5b), so the test units write under db/.
    private static PackUnit Unit(string id, string @for, UnitWhere? where = null) => PlanningKit.Unit(id, @for, where) with { Output = "db/x" };

    private static Task<UnitPlan> Plan(LoadedPack pack) => new UnitPlanner(Options).PlanAsync(Model, new PackSet([pack], []), new ScriptSandboxFactory(), null, Ct);
}
