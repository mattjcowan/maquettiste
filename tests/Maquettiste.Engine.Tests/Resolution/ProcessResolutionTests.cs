using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The resolved processes, actors and scenarios of the gate 3 fixture (phase-3-design.md section 4.3) against a golden text rendering
/// (set MAQUETTISTE_UPDATE_GOLDEN=1 to rewrite it), and the members and dependency keys packs rely on.
/// </summary>
public sealed class ProcessResolutionTests
{
    private const string HappyPath = "01JQSCN0000000000000000008";
    private const string FirstApprove = "01JQSTP0000000000000000028";
    private const string CostCentre = "01JQATT0000000000000000204";
    private const string Reject = "01JQTRN0000000000000000022";

    private static string Expected => Path.Combine(Fixtures.RepoRoot, "tests", "Maquettiste.Engine.Tests", "Resolution", "Golden", "processes");

    /// <summary>The gate 3 fixture as a snapshot.</summary>
    internal static ModelSnapshot Snapshot() => ValidationFixture.LoadRepo(Fixtures.Path("models", "processes"));

    [Fact]
    public void The_gate_3_fixture_resolves_to_the_golden_model()
    {
        var text = ResolutionKit.Dump(ResolutionKit.Resolve(Snapshot()));
        var actual = Directory.CreateTempSubdirectory("mq-resolve-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(actual, "resolved.txt"), text);
            Golden.AssertMatches(Expected, actual);
        }
        finally
        {
            Directory.Delete(actual, recursive: true);
        }
    }

    [Fact]
    public void Processes_actors_and_scenarios_are_ordered_and_their_lists_carry_membership_keys()
    {
        var model = ResolutionKit.Resolve(Snapshot());

        Assert.Equal(["PurchaseApproval", "SalesOrderLifecycle"], model.Processes.Select(p => p.Name)); // Purchasing before Sales
        Assert.Equal(model.Actors.Select(a => a.Name).Order(StringComparer.Ordinal), model.Actors.Select(a => a.Name));
        Assert.Equal(14, model.Scenarios.Count);
        Assert.Equal(6, model.Scenarios.TakeWhile(s => s.Process.Name == "PurchaseApproval").Count());
        Assert.Equal(["k:process"], model.Processes.MembershipKeys);
        Assert.Equal(["k:actor"], model.Actors.MembershipKeys);
        Assert.Equal(["k:scenario"], model.Scenarios.MembershipKeys);

        var sales = model.Processes.Single(p => p.Name == "SalesOrderLifecycle");
        Assert.Same(sales, model.Entities.Single(e => e.Name == "SalesOrder").Lifecycle);
        Assert.Contains("k:process", model.Entities.Single(e => e.Name == "SalesOrder").Dependencies);
        Assert.Null(model.Entities.Single(e => e.Name == "PurchaseRequest").Lifecycle);
        Assert.Contains("e:" + sales.Id, sales.Dependencies);
        Assert.Contains("e:" + sales.Subject!.Id, sales.Dependencies);
        Assert.Contains("e:" + sales.BoundEnum!.Id, sales.Dependencies);
        Assert.DoesNotContain("r:" + sales.Id, sales.Dependencies);
        Assert.Equal(["k:scenario", "r:" + sales.Id], sales.Scenarios.MembershipKeys);
        Assert.Contains("k:actor", sales.Actors.MembershipKeys);
        Assert.All(sales.AllStates, s => Assert.Contains("e:" + sales.Id, s.Dependencies));
        Assert.All(sales.BoundStates, s => Assert.Equal(s.Name, s.BoundMember?.Name));

        var scenario = model.Scenarios.First(s => s.Process == sales);
        Assert.Contains("e:" + scenario.Id, scenario.Dependencies);
        Assert.Contains("e:" + sales.Id, scenario.Dependencies);
        Assert.Equal(sales.Package, scenario.Package);
        Assert.All(scenario.Steps, step => Assert.Contains("e:" + sales.Id, step.Dependencies));

        var approver = model.Actors.Single(a => a.Name == "Approver");
        Assert.Equal(["r:" + approver.Id], approver.Processes.MembershipKeys);
        Assert.Null(approver.Package);

        // Every new object is found by id.
        Assert.Same(sales.AllStates[3], model.Find(sales.AllStates[3].Id));
        Assert.Same(sales.Transitions[0], model.Find(sales.Transitions[0].Id));
        Assert.Same(scenario.Steps[0], model.Find(scenario.Steps[0].Id));
    }

    [Fact]
    public void States_come_from_the_interpreters_chart()
    {
        var snapshot = Snapshot();
        var model = ResolutionKit.Resolve(snapshot);
        foreach (var process in model.Processes)
        {
            var chart = Maquettiste.Engine.Processes.StatechartModel.Get(snapshot.Get<Process>(process.Id)!, null);
            Assert.Equal(chart.States.Select(s => s.Path), process.AllStates.Select(s => s.Path));
            Assert.Equal(chart.States.Select(s => s.Depth), process.AllStates.Select(s => s.Depth));
            Assert.Equal(chart.Transitions.Select(t => t.Id), process.Transitions.Select(t => t.Id));
            Assert.Equal(chart.Root.InitialChild!.Id, process.Initial!.Id);
        }
    }

    [Theory]
    [InlineData("P30D", "30d", 2_592_000_000L, 25_920_000_000_000L)]
    [InlineData("PT1H30M", "1h 30m", 5_400_000L, 54_000_000_000L)]
    [InlineData("P1M", "1mo", 2_592_000_000L, 25_920_000_000_000L)]
    [InlineData("P1Y2M3W4DT5H6M7S", "1y 2mo 3w 4d 5h 6m 7s", 38_898_367_000L, 388_983_670_000_000L)]
    [InlineData("PT0.5S", "0.5s", 500L, 5_000_000L)]
    [InlineData("PT0.0005S", "0.0005s", 0L, 5_000L)]
    [InlineData("P5D", "5d", 432_000_000L, 4_320_000_000_000L)]
    public void Durations_are_written_short_as_the_editor_does_and_counted_with_fixed_spans(string iso, string text, long ms, long ticks)
    {
        Assert.Equal(text, ProcessText.ShortDuration(iso));
        Assert.Equal(ms, ProcessText.Milliseconds(iso));
        Assert.Equal(ticks, ProcessText.Ticks(iso)); // below a millisecond too: the interpreter's clock moves by ticks
    }

    [Fact]
    public void A_steps_gate_audit_attributes_resolve_by_name_and_an_undeclared_guard_is_marked_missing()
    {
        var snapshot = ValidationFixture.LoadRepo(Fixtures.Path("models", "processes"), element => element switch
        {
            // The first approve of HappyPath records its cost centre; its process's reject gets a guard that is not declared.
            Scenario { Id: HappyPath } scenario => scenario with
            {
                Steps = [.. scenario.Steps.Select(step => step.Id == FirstApprove
                    ? step with { Payload = ImmutableDictionary<string, JsonElement>.Empty.Add(CostCentre, JsonDocument.Parse("\"CC-7\"").RootElement.Clone()) }
                    : step)],
            },
            Process { Name: "PurchaseApproval" } process => process with
            {
                Transitions = [.. process.Transitions.Select(t => t.Id == Reject ? t with { Guard = "01JQPRX00000000000000000ZZ" } : t)],
            },
            _ => element,
        });
        var model = ResolutionKit.Resolve(snapshot);

        var step = model.Scenarios.Single(s => s.Id == HappyPath).Steps.Single(s => s.Id == FirstApprove);
        Assert.Equal("CC-7", Assert.Single(step.Payload, p => p.Key == "costCentre").Value);
        Assert.DoesNotContain(CostCentre, step.Payload.Keys);

        var purchase = model.Processes.Single(p => p.Name == "PurchaseApproval");
        var reject = purchase.Transitions.Single(t => t.Id == Reject);
        Assert.True(reject.GuardMissing);
        Assert.Null(reject.Guard);
        Assert.All(purchase.Transitions.Where(t => t.Id != Reject), t => Assert.False(t.GuardMissing));
    }

    [Fact]
    public void A_steps_trace_is_the_engines_replay()
    {
        var model = ResolutionKit.Resolve(Snapshot());
        var happy = model.Scenarios.Single(s => s.Id == HappyPath);
        Assert.Equal([[], [], [], ["signed"], ["signed", "completed"], []], happy.Steps.Select(s => s.Trace!.Audit.ToArray()));
        Assert.All(happy.Steps, s => Assert.True(s.Trace!.Accepted && s.Trace.Refusal is null));
        Assert.Equal(["Ordered"], happy.Steps[^1].Trace!.StatePaths);
        Assert.True(happy.Steps[^1].Trace!.Final);

        var repeat = model.Scenarios.Single(s => s.Name == "RepeatSignerRefused").Steps[^1].Trace!;
        Assert.False(repeat.Accepted);
        Assert.Equal("gate-repeat", repeat.Refusal);
        Assert.Equal(["refused"], repeat.Audit);
        var cancel = model.Scenarios.Single(s => s.Name == "CancelRefusedAfterShipping").Steps[^1].Trace!;
        Assert.Equal(("guard", 0), (cancel.Refusal, cancel.Audit.Count));
    }

    [Theory]
    [InlineData("")]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("5 days")]
    public void Text_that_is_not_a_duration_is_kept_and_has_no_milliseconds(string iso)
    {
        Assert.Equal(iso, ProcessText.ShortDuration(iso));
        Assert.Null(ProcessText.Milliseconds(iso));
    }
}
