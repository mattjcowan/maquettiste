using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// More object effects in a run (object-effects-design.md decisions 4, 5, 8 and 10), over an invented ticket beside the <c>process-basics</c>
/// fixture. Gate runs two parallel regions: B's moves wait on A's state through the running chart, by a guard stored as a tree and one stored
/// as text; B1's exit and B2's entry set values that read the running chart; B2 invokes Child, whose entry sets data it can't; A's first move
/// is automatic and stamps the system actor; sign stamps the acting actor. Phases holds the ticket's state in an enum whose members name a
/// root-level state and a nested state alike.
/// </summary>
public sealed class ObjectEffectsRunTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Ticket = "01JENT00000000000000000081";
    private const string Note = "01JATT00000000000000000802";
    private const string Who = "01JATT00000000000000000803";
    private const string By = "01JATT00000000000000000804";
    private const string Phase = "01JATT00000000000000000805";
    private const string PhaseEnum = "01JENM00000000000000000081";
    private const string Phases = "01JPRC00000000000000000081";
    private const string Gate = "01JPRC00000000000000000082";
    private const string Child = "01JPRC00000000000000000083";
    private const string Signer = "01JACT00000000000000000081";
    private const string Open = "01JSTA00000000000000000801";
    private const string NestedHold = "01JSTA00000000000000000802";
    private const string Work = "01JSTA00000000000000000803";
    private const string Hold = "01JSTA00000000000000000804";
    private const string DoneA = "01JPRX00000000000000000801";
    private const string Finish = "01JPRX00000000000000000802";
    private const string FinishText = "01JPRX00000000000000000803";
    private const string Held = "01JPRX00000000000000000804";
    private const string Sign = "01JPRX00000000000000000805";
    private const string ObjectId = "01JXBJ00000000000000000081";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Set(int n, string attribute, string value) =>
        $$"""{"id":"01JSET000000000000000008{{n:D2}}","attribute":"{{attribute}}","value":{{value}} }""";

    // case when in_state(subject, <state>) then 'yes' else 'no' end, as a tree.
    private static string When(string state, string yes, string no) =>
        $$"""{"case":[{"when":{"inState":["{{state}}"]},"then":{"value":"{{yes}}"} }],"else":{"value":"{{no}}"} }""";

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync()
    {
        const string ActorValue = "{\"actor\":true}";
        var leave = Set(1, Note, When("01JSTA00000000000000000815", "exit-in-B1", "exit-out"));
        var enter = Set(2, Who, When("01JSTA00000000000000000816", "entry-in-B2", "entry-out"));
        var system = Set(3, By, ActorValue);
        var acting = Set(4, By, ActorValue);
        var child = Set(9, Note, "{\"value\":\"child\"}");
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/enums/ticket-phase.json", $$"""
            {"kind":"enum","id":"{{PhaseEnum}}","name":"TicketPhase","package":"{{Package}}",
             "members":[{"id":"01JMEM00000000000000000081","name":"Open"},{"id":"01JMEM00000000000000000082","name":"Hold"}]}
            """);
        harness.Write("model/entities/ticket.json", $$"""
            {"kind":"entity","id":"{{Ticket}}","name":"Ticket","package":"{{Package}}","key":{"attributes":["01JATT00000000000000000801"]},
             "attributes":[{"id":"01JATT00000000000000000801","name":"id","type":"uuid"},{"id":"{{Note}}","name":"note","type":"string","length":40},
               {"id":"{{Who}}","name":"who","type":"string","length":40},{"id":"{{By}}","name":"by","type":"string","length":40},
               {"id":"{{Phase}}","name":"phase","type":{"ref":"{{PhaseEnum}}"},"default":"Open"}]}
            """);
        harness.Write("model/actors/robot.json", """{"kind":"actor","id":"01JACT00000000000000000082","name":"Robot","type":"system"}""");
        harness.Write("model/actors/signer.json", $$"""{"kind":"actor","id":"{{Signer}}","name":"Signer","type":"person"}""");
        harness.Write("model/processes/phases.json", $$"""
            {"kind":"process","id":"{{Phases}}","name":"Phases","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000081","entity":"{{Ticket}}","boundAttribute":"{{Phase}}"}],
             "states":[{"id":"{{Open}}","name":"Open","type":"compound","initial":"{{NestedHold}}",
                 "states":[{"id":"{{NestedHold}}","name":"Hold"},{"id":"{{Work}}","name":"Work"}]},
               {"id":"{{Hold}}","name":"Hold"}],
             "initial":"{{Open}}"}
            """);
        harness.Write("model/processes/child.json", $$"""
            {"kind":"process","id":"{{Child}}","name":"Child","package":"{{Package}}",
             "subjects":[{"id":"01JSBJ00000000000000000083","entity":"{{Ticket}}"}],
             "actions":[{"id":"01JPRX00000000000000000831","name":"childStamp","sets":[{{child}}]}],
             "states":[{"id":"01JSTA00000000000000000831","name":"C1","entry":["01JPRX00000000000000000831"]}],
             "initial":"01JSTA00000000000000000831"}
            """);
        harness.Write("model/processes/gate.json", $$"""
            {"kind":"process","id":"{{Gate}}","name":"Gate","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000082","entity":"{{Ticket}}"}],
             "events":[{"id":"{{DoneA}}","name":"doneA"},{"id":"{{Finish}}","name":"finish"},{"id":"{{FinishText}}","name":"finishText"},
               {"id":"{{Held}}","name":"held"},{"id":"{{Sign}}","name":"sign","actors":["{{Signer}}"]}],
             "guards":[{"id":"01JPRX00000000000000000811","name":"aDone","condition":{"inState":["01JSTA00000000000000000813"]} },
               {"id":"01JPRX00000000000000000812","name":"aDoneText","condition":{"text":"in_state(subject, Gate.Main.A.A2)"} },
               {"id":"01JPRX00000000000000000813","name":"onHold","condition":{"inState":["{{Hold}}"]} }],
             "actions":[{"id":"01JPRX00000000000000000821","name":"leaveB1","sets":[{{leave}}]},
               {"id":"01JPRX00000000000000000822","name":"enterB2","sets":[{{enter}}]},
               {"id":"01JPRX00000000000000000823","name":"stampSystem","sets":[{{system}}]},
               {"id":"01JPRX00000000000000000824","name":"stampActor","sets":[{{acting}}]}],
             "states":[{"id":"01JSTA00000000000000000810","name":"Main","type":"parallel","states":[
                 {"id":"01JSTA00000000000000000811","name":"A","type":"compound","initial":"01JSTA00000000000000000812","states":[
                   {"id":"01JSTA00000000000000000812","name":"A0"},{"id":"01JSTA00000000000000000817","name":"A1"},{"id":"01JSTA00000000000000000813","name":"A2"}]},
                 {"id":"01JSTA00000000000000000814","name":"B","type":"compound","initial":"01JSTA00000000000000000815","states":[
                   {"id":"01JSTA00000000000000000815","name":"B1","exit":["01JPRX00000000000000000821"]},
                   {"id":"01JSTA00000000000000000816","name":"B2","entry":["01JPRX00000000000000000822"],
                    "invoke":[{"id":"01JPRX00000000000000000832","name":"child","type":"process","process":"{{Child}}"}]} ]} ]} ],
             "initial":"01JSTA00000000000000000810",
             "transitions":[
               {"id":"01JTRN00000000000000000801","source":"01JSTA00000000000000000812","trigger":"always","targets":["01JSTA00000000000000000817"],"actions":["01JPRX00000000000000000823"]},
               {"id":"01JTRN00000000000000000802","source":"01JSTA00000000000000000817","event":"{{DoneA}}","targets":["01JSTA00000000000000000813"]},
               {"id":"01JTRN00000000000000000803","source":"01JSTA00000000000000000815","event":"{{Finish}}","guard":"01JPRX00000000000000000811","targets":["01JSTA00000000000000000816"]},
               {"id":"01JTRN00000000000000000804","source":"01JSTA00000000000000000815","event":"{{FinishText}}","guard":"01JPRX00000000000000000812","targets":["01JSTA00000000000000000816"]},
               {"id":"01JTRN00000000000000000805","source":"01JSTA00000000000000000815","event":"{{Held}}","guard":"01JPRX00000000000000000813","targets":["01JSTA00000000000000000816"]},
               {"id":"01JTRN00000000000000000806","source":"01JSTA00000000000000000813","event":"{{Sign}}","targets":["01JSTA00000000000000000813"],"actions":["01JPRX00000000000000000824"]} ]}
            """);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement TicketObject(string values = "") =>
        Json($$"""{"id":"{{ObjectId}}","name":"t1","entity":"{{Ticket}}","values":{ {{values}} } }""");

    private static JsonElement Step(string ev, string extra = "") => Json($$"""{"event":"{{ev}}"{{extra}}}""");

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, JsonElement[] steps, string values = "")
    {
        var call = await store.SimulateProcessAsync(Gate, new SimulateRequest { Steps = steps, Objects = [TicketObject(values)], Subject = ObjectId }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(call.Value!.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return call.Value!;
    }

    private static string? Value(StepTrace trace, string id) => trace.ValuesChanged is { } values && values.TryGetValue(id, out var v) ? v.GetString() : null;

    [Theory]
    [InlineData(Finish)]
    [InlineData(FinishText)]
    public async Task A_region_waits_on_another_through_the_running_chart_and_entry_and_exit_sets_read_it(string finish)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // in_state on the running chart answers false while A is in A1, then true once A moves to A2; a guard stored as text runs as a tree.
        var result = await SimulateAsync(store, [Step(finish), Step(DoneA), Step(finish)]);
        Assert.Equal([false, true, true], result.Trace.Skip(1).Select(t => t.Accepted));
        Assert.Equal(Refusals.Guard, result.Trace[1].Refusal);

        // B1's exit set reads B1 as still active; B2's entry set reads B2 as just entered.
        var moved = result.Trace[3];
        Assert.Equal("exit-in-B1", Value(moved, Note));
        Assert.Equal("entry-in-B2", Value(moved, Who));

        // B2 invokes Child, whose entry sets the note: an invoked process sees no object, so nothing changes, and the trace says so.
        Assert.Contains("invoked process 'Child' sets the object's data", JsonSerializer.Serialize(moved, EngineJson.Options), StringComparison.Ordinal);

        // A recorded scenario replays the same, with no MQ9502 from either guard, and validation finds no replay error.
        var call = await store.RecordScenarioAsync(Gate, new RecordScenarioRequest
        {
            Name = "waits" + finish[^1], Objects = [TicketObject()], Subject = ObjectId, Steps = [Step(finish), Step(DoneA), Step(finish)],
        }, false, ChangeSource.Editor, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        var verified = await store.VerifyScenariosAsync(Gate, [call.Value!.Id], Ct);
        Assert.True(verified.Value!.Passed, string.Join(" | ", verified.Value.Results.Select(r => r.Failure?.Message)));
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.DoesNotContain(diagnostics, d => d.Rule is "MQ9502" or "MQ9301" or "MQ9302");
    }

    [Fact]
    public async Task Actor_is_the_system_actor_on_an_automatic_move_and_the_acting_actor_s_name_on_a_step()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await SimulateAsync(store, [Step(DoneA), Step(Sign, $",\"actor\":\"{Signer}\"")]);
        Assert.Equal("Robot", Value(result.Trace[0], By));
        Assert.Equal([true, true], result.Trace.Skip(1).Select(t => t.Accepted));
        Assert.Equal("Signer", Value(result.Trace[2], By));
    }

    [Fact]
    public async Task An_enum_bound_value_maps_to_the_root_level_state_of_its_name_in_a_replay()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // Phases nests a Hold under Open before its root-level Hold: the member Hold names the root-level one.
        var held = await SimulateAsync(store, [Step(Held)], $"\"{Phase}\":\"Hold\"");
        Assert.True(held.Trace[1].Accepted);
        var open = await SimulateAsync(store, [Step(Held)], $"\"{Phase}\":\"Open\"");
        Assert.False(open.Trace[1].Accepted);

        var call = await store.RecordScenarioAsync(Gate, new RecordScenarioRequest
        {
            Name = "heldTicket", Objects = [TicketObject($"\"{Phase}\":\"Hold\"")], Subject = ObjectId, Steps = [Step(Held)],
        }, false, ChangeSource.Editor, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        var saved = store.Current!.Get<Scenario>(call.Value!.Id)!;
        Assert.True(saved.Steps[0].Expect!.Accepted);
        var verified = await store.VerifyScenariosAsync(Gate, [saved.Id], Ct);
        Assert.True(verified.Value!.Passed, string.Join(" | ", verified.Value.Results.Select(r => r.Failure?.Message)));
    }
}
