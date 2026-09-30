using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Resolution;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>
/// Templates and JavaScript helpers over processes, actors and scenarios (phase-3-design.md sections 4.3 and 7.1): the scope aliases,
/// the <c>state_path</c> and <c>iso_duration_ms</c> helpers, and the dependency keys the tracking context records.
/// </summary>
public sealed class ProcessRenderTests
{
    private static readonly Lazy<ResolvedModel> Model = new(() => ResolutionKit.Resolve(ProcessResolutionTests.Snapshot()));

    private static RProcess Sales => Model.Value.Processes.Single(p => p.Name == "SalesOrderLifecycle");

    [Fact]
    public async Task A_process_unit_reads_the_state_tree_paths_and_durations_and_records_the_process_keys()
    {
        var unit = await Adhoc.RenderAsync(
            "{{ for s in process.states }}{{ s.name }}={{ state_path s }};{{ end }}|{{ state_path process.all_states[6] }}"
            + "|{{ iso_duration_ms process.transitions[6].after }}|{{ process.transitions[6].label }}|{{ iso_duration_ms 'PT1H30M' }}",
            Sales, unit: u => u with { For = "each process" }, model: Model.Value);

        Assert.Equal("Draft=Draft;CreditCheck=CreditCheck;CreditReview=CreditReview;Fulfilment=Fulfilment;OnHold=OnHold;Completed=Completed;Cancelled=Cancelled;"
            + "|Fulfilment.Processing.Payment.AwaitingPayment|2592000000|after 30d|5400000", unit.Text());
        Assert.Contains("e:" + Sales.Id, unit.ReadKeys);
        Assert.Contains("e:" + Sales.Subject!.Id, unit.ReadKeys);
        Assert.Contains("e:" + Sales.BoundEnum!.Id, unit.ReadKeys);
        // Reading the process does not depend on who refers to it (its scenarios): editing a scenario leaves this unit alone.
        Assert.DoesNotContain("r:" + Sales.Id, unit.ReadKeys);
        Assert.DoesNotContain(unit.ReadKeys, k => k.StartsWith("k:scenario", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reading_a_processs_scenarios_and_actors_records_their_membership()
    {
        var unit = await Adhoc.RenderAsync("{{ process.scenarios.size }} {{ for a in process.actors }}{{ a.name }} {{ end }}", Sales,
            unit: u => u with { For = "each process" }, model: Model.Value);

        Assert.Equal("8 CreditManager FinanceDirector ", unit.Text());
        Assert.Contains("k:scenario", unit.ReadKeys);
        Assert.Contains("r:" + Sales.Id, unit.ReadKeys);
        Assert.Contains("k:actor", unit.ReadKeys);
    }

    [Fact]
    public async Task A_scenario_unit_reads_its_steps_expected_state_paths_and_records_its_process()
    {
        var scenario = Model.Value.Scenarios.Single(s => s.Name == "HoldAndRelease");
        var unit = await Adhoc.RenderAsync(
            "{{ scenario.process.name }}:{{ scenario.steps[0].expect.state_paths | array.join ',' }}|{{ scenario.steps[0].event.name }}"
            + "|{{ scenario.start.context.total }}|{{ for s in scenario.steps }}{{ s.index }}{{ if s.expect.accepted }}+{{ end }}{{ end }}",
            scenario, unit: u => u with { For = "each scenario" }, model: Model.Value);

        Assert.Equal("SalesOrderLifecycle:Fulfilment.Processing.Payment.AwaitingPayment,Fulfilment.Processing.Shipping.Picking|submit|100|0+1+2+3+", unit.Text());
        Assert.Contains("e:" + scenario.Id, unit.ReadKeys);
        Assert.Contains("e:" + Sales.Id, unit.ReadKeys);
    }

    [Fact]
    public async Task An_actor_unit_reads_what_references_it_through_its_referrers()
    {
        var actor = Model.Value.Actors.Single(a => a.Name == "CreditManager");
        var unit = await Adhoc.RenderAsync("{{ actor.type }} {{ for e in actor.events }}{{ e.name }} {{ end }}{{ actor.gates[0].name }} {{ actor.processes[0].name }}",
            actor, unit: u => u with { For = "each actor" }, model: Model.Value);

        Assert.Equal("role approveCredit rejectCredit creditApproval SalesOrderLifecycle", unit.Text());
        Assert.Contains("e:" + actor.Id, unit.ReadKeys);
        Assert.Contains("r:" + actor.Id, unit.ReadKeys);
    }

    [Theory]
    [InlineData("{{ iso_duration_ms 'soon' }}", "'soon'")]
    [InlineData("{{ state_path 'nothing' }}", "'nothing'")]
    [InlineData("{{ state_path 3 }}", "state_path")]
    public async Task Bad_helper_arguments_fail_the_unit_with_MQ6006(string template, string named)
    {
        var error = (await Adhoc.RenderAsync(template, Sales, unit: u => u with { For = "each process" }, model: Model.Value)).Error();

        Assert.Equal("MQ6006", error.Rule);
        Assert.Contains(named, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_state_id_works_for_state_path_and_localization_helpers_read_process_nodes()
    {
        var state = Sales.AllStates.Single(s => s.Name == "Paid");
        var gate = Sales.Gates[0];
        var unit = await Adhoc.RenderAsync($"{{{{ state_path '{state.Id}' }}}}|{{{{ display_name process.gates[0] }}}}|{{{{ has_stereotype process.states[0] 'x' }}}}",
            Sales, unit: u => u with { For = "each process" }, model: Model.Value);

        Assert.Equal("Fulfilment.Processing.Payment.Paid|" + gate.DisplayName + "|false", unit.Text());
    }

    [Fact]
    public async Task A_javascript_helper_receives_a_process_proxy()
    {
        var unit = await Adhoc.RenderAsync("{{ describe process }}|{{ (same process).name }}|{{ first_after process }}", Sales,
            unit: u => u with { For = "each process" }, model: Model.Value,
            files: new Dictionary<string, string>
            {
                ["helpers.js"] = "maquettiste.helper('describe', (p) => p.name + ':' + p.use + ':' + p.allStates.length + ':' + p.states[3].children[0].path"
                    + " + ':' + p.boundStates.map((s) => s.boundMember.name).join('/') + ':' + p.states[0].hasStereotype('x'));"
                    + "maquettiste.helper('same', (p) => p);"
                    + "maquettiste.helper('first_after', (p) => p.transitions.filter((t) => t.trigger === 'after')[0].afterMs);",
            });

        Assert.Equal("SalesOrderLifecycle:lifecycle:17:Fulfilment.Processing:Draft/CreditReview/Fulfilment/OnHold/Completed/Cancelled:false|SalesOrderLifecycle|2592000000",
            unit.Text());
        Assert.Contains("e:" + Sales.Id, unit.ReadKeys);
    }
}
