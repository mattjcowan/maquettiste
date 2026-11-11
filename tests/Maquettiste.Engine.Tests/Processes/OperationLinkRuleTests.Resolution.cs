using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The resolved side of processes linked to operations, on the same invoice model (processes-operations-design.md sections 9, 10 and 18):
/// <c>action.call</c>, <c>invoke.operation</c>, <c>operation.called_by</c> and <c>invoked_by</c> in their order, the records that carry them and
/// leave them out when unset, <c>event.fired_by</c>'s narrowed keys, and the simulator recording a call without running it.
/// </summary>
public sealed partial class OperationLinkRuleTests
{
    private const string Run = "01JPRC00000000000000000104"; // AInvoiceRun, an orchestration on Invoice that also calls issue
    private const string RunAsk = "01JPRX00000000000000000141";
    private const string Terms = "01JQPN00000000000000000105"; // issue.terms, int32, optional
    private const string Scan = "01JQPN00000000000000000106"; // issue.scan, a file
    private const string DueDaysPayload = "01JATT00000000000000000181";
    private const string IssuedOnPayload = "01JATT00000000000000000182";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<ResolvedModel> ResolveAsync(World w)
    {
        var (h, store) = await OpenAsync(w);
        using var _ = h;
        await using var __ = store;
        return await new ModelResolver(h.Options).ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);
    }

    private static void AddRun(World w) => w.Files["model/processes/invoice-run.json"] = Obj($$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{Run}}","name":"AInvoiceRun","package":"{{Package}}","use":"orchestration",
         "subjects":[{"id":"01JSBJ00000000000000000141","entity":"{{Invoice}}"}],
         "states":[{"id":"01JSTA00000000000000000141","name":"Start","entry":["{{RunAsk}}"]}],"initial":"01JSTA00000000000000000141",
         "actions":[{"id":"{{RunAsk}}","name":"Ask","call":{"operation":"{{IssueOp}}"} }]}
        """);

    [Fact]
    public async Task Calls_steps_and_both_directions_resolve_in_order_and_their_records_leave_out_what_is_unset()
    {
        var w = Model();
        AddRun(w);
        var model = await ResolveAsync(w);
        var process = model.Processes.Single(p => p.Id == Lifecycle);
        var request = process.Actions.Single(a => a.Id == RequestIssue);
        var call = Assert.IsType<RCall>(request.Call);
        Assert.Equal(IssueOp, call.Operation.Id);
        Assert.Same(request, call.Action);
        Assert.Equal([DueDays, IssuedOn], call.Inputs.Select(i => i.Member.Id));
        Assert.Equal(["30", "event.issuedOn"], call.Inputs.Select(i => i.Value.Text));
        Assert.True(request.IsStub, "a call-only action is still a stub");
        Assert.Null(process.Actions.Single(a => a.Id == Nudge).Call);
        Assert.Equal(Charge, process.Invokes.Single().Operation?.Id);

        var issue = model.Operations.Single(o => o.Id == IssueOp);
        // Processes order (AInvoiceRun before InvoiceLifecycle), then file order.
        Assert.Equal([RunAsk, RequestIssue], issue.CalledBy.Select(a => a.Id));
        Assert.Empty(issue.InvokedBy);
        Assert.Equal([ChargeCard], model.Operations.Single(o => o.Id == Charge).InvokedBy.Select(i => i.Id));
        Assert.Equal([IssueOp], process.Events.Single(e => e.Id == Issue).FiredBy.Select(o => o.Id));

        // event.fired_by keys on r:<event> alone when empty; a list that is not empty keeps k:operation.
        Assert.Equal(["r:" + Pay], process.Events.Single(e => e.Id == Pay).FiredBy.MembershipKeys);
        Assert.Contains("k:operation", process.Events.Single(e => e.Id == Issue).FiredBy.MembershipKeys);
        Assert.Equal(["r:" + IssueOp], issue.CalledBy.MembershipKeys);
        Assert.Equal(["r:" + IssueOp], issue.InvokedBy.MembershipKeys);

        var processJson = Record(model, "processes", Lifecycle);
        var actions = processJson["actions"]!.AsArray();
        var requestJson = actions.Single(a => (string?)a!["id"] == RequestIssue)!;
        Assert.Equal(IssueOp, (string?)requestJson["call"]!["operation"]);
        Assert.Equal([InputA, InputB], requestJson["call"]!["inputs"]!.AsArray().Select(i => (string?)i!["id"]));
        Assert.Equal(DueDays, (string?)requestJson["call"]!["inputs"]![0]!["member"]);
        Assert.Equal("30", (string?)requestJson["call"]!["inputs"]![0]!["value"]!["text"]);
        Assert.False(actions.Single(a => (string?)a!["id"] == Nudge)!.AsObject().ContainsKey("call"));
        Assert.Equal(Charge, (string?)processJson["invokes"]![0]!["operation"]);
        var events = processJson["events"]!.AsArray();
        Assert.Equal([IssueOp], events.Single(e => (string?)e!["id"] == Issue)!["firedBy"]!.AsArray().Select(x => (string?)x));
        Assert.False(events.Single(e => (string?)e!["id"] == Pay)!.AsObject().ContainsKey("firedBy"));

        Assert.Equal([RunAsk, RequestIssue], Record(model, "operations", IssueOp)["calledBy"]!.AsArray().Select(x => (string?)x));
        Assert.False(Record(model, "operations", IssueOp).ContainsKey("invokedBy"));
        Assert.Equal([ChargeCard], Record(model, "operations", Charge)["invokedBy"]!.AsArray().Select(x => (string?)x));
        var settle = Record(model, "operations", Settle);
        Assert.False(settle.ContainsKey("calledBy") || settle.ContainsKey("invokedBy"), settle.ToJsonString());
    }

    [Fact]
    public async Task An_input_that_cannot_be_given_or_whose_operation_is_gone_stays_out_of_the_resolved_model()
    {
        var w = Model();
        // A file member can't be given (MQ9025): the input is left out, the others stay.
        w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"{{Scan}}","name":"scan","file":true}"""));
        w.Inputs.Add(Input(InputC, Scan, "{\"value\":\"x\"}"));
        // A step's operation on a human task (MQ9025) does not resolve.
        w.Step["type"] = "human-task";
        w.Step["actors"] = new JsonArray("01JACT00000000000000000002");
        var model = await ResolveAsync(w);
        var process = model.Processes.Single(p => p.Id == Lifecycle);
        Assert.Equal([DueDays, IssuedOn], process.Actions.Single(a => a.Id == RequestIssue).Call!.Inputs.Select(i => i.Member.Id));
        Assert.Null(process.Invokes.Single().Operation);
        Assert.Empty(model.Operations.Single(o => o.Id == Charge).InvokedBy);
    }

    [Fact]
    public async Task A_simulation_records_a_call_with_its_values_and_runs_nothing()
    {
        var (h, store) = await OpenAsync(Model());
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, [Json($$"""{"event":"{{Issue}}","payload":{"{{DueDaysPayload}}":10,"{{IssuedOnPayload}}":"2026-01-02"} }""")]);
        var step = result.Trace[^1];
        Assert.True(step.Accepted);
        var run = step.Microsteps.SelectMany(m => m.Actions).Single(a => a.Action == RequestIssue);
        Assert.Equal(ActionSource.Stub, run.Source);
        Assert.Empty(run.Changed);
        Assert.Null(run.Sets);
        var call = Assert.IsType<CallRun>(run.Call);
        Assert.Equal(IssueOp, call.Operation);
        Assert.Equal("30", call.Inputs[DueDays].GetRawText());
        Assert.Equal("\"2026-01-02\"", call.Inputs[IssuedOn].GetRawText());
        Assert.Null(step.Microsteps.SelectMany(m => m.Actions).Single(a => a.Action == Nudge).Call);
        Assert.Contains(step.Notes ?? [], n => n.Contains("'RequestIssue' called an operation: recorded, not run", StringComparison.Ordinal));
        // Nothing moved because of the call: the move went to Issued, and the call's operation fired nothing.
        Assert.Equal([Issued], step.Configuration);
        var json = JsonSerializer.SerializeToNode(run, Web)!.AsObject();
        Assert.Equal("stub", (string?)json["source"]);
        Assert.Equal(IssueOp, (string?)json["call"]!["operation"]);
        Assert.False(JsonSerializer.SerializeToNode(step.Microsteps.SelectMany(m => m.Actions).Single(a => a.Action == Nudge), Web)!.AsObject().ContainsKey("call"));
    }

    [Fact]
    public async Task A_call_reads_the_object_as_the_action_started_and_a_value_that_cannot_be_worked_out_is_null_with_a_note()
    {
        var w = Model();
        w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"{{Terms}}","name":"terms","type":"int32"}"""));
        w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"{{Scan}}","name":"scan","file":true}"""));
        w.Inputs.Add(Input(InputC, Terms, $$"""{"path":["{{TermsDays}}"]}"""));
        w.Inputs.Add(Input("01JCXN00000000000000000104", Scan, "{\"value\":\"x\"}"));
        w.Action(RequestIssue)["sets"] = JsonNode.Parse($$"""[{"id":"01JCXN00000000000000000105","attribute":"{{TermsDays}}","value":{"value":7} }]""");
        var (h, store) = await OpenAsync(w);
        using var _ = h;
        await using var __ = store;
        JsonElement[] objects = [Json($$"""{"id":"o1","name":"invoice","entity":"{{Invoice}}","values":{"{{TermsDays}}":45} }""")];
        var result = await SimulateAsync(store, [Json($$"""{"event":"{{Issue}}","payload":{"{{DueDaysPayload}}":10,"{{IssuedOnPayload}}":"2026-01-02"} }""")], objects);
        var step = result.Trace[^1];
        var run = step.Microsteps.SelectMany(m => m.Actions).Single(a => a.Action == RequestIssue);
        Assert.Equal(ActionSource.Sets, run.Source);
        Assert.Equal("7", run.Sets![TermsDays].GetRawText());
        // The value from before the action's sets applied.
        Assert.Equal("45", run.Call!.Inputs[Terms].GetRawText());
        Assert.Equal(JsonValueKind.Null, run.Call.Inputs[Scan].ValueKind);
        Assert.Contains(step.Notes ?? [], n => n.Contains("could not be worked out", StringComparison.Ordinal));
        Assert.True(step.Accepted);
    }

    [Fact]
    public async Task A_call_in_an_invoked_sub_process_is_recorded_and_a_value_that_reads_the_object_is_null_with_a_note()
    {
        // Issued also invokes Child, an orchestration on Invoice whose entry action calls issue with dueDays = subject.termsDays and issuedOn = null.
        const string Child = "01JPRC00000000000000000105";
        const string ChildAsk = "01JPRX00000000000000000151";
        var w = Model();
        w.Files["model/processes/child.json"] = Obj($$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{Child}}","name":"Child","package":"{{Package}}","use":"orchestration",
             "subjects":[{"id":"01JSBJ00000000000000000151","entity":"{{Invoice}}"}],
             "states":[{"id":"01JSTA00000000000000000151","name":"C1","entry":["{{ChildAsk}}"]}],"initial":"01JSTA00000000000000000151",
             "actions":[{"id":"{{ChildAsk}}","name":"ChildAsk","call":{"operation":"{{IssueOp}}","inputs":[
               {"id":"01JCXN00000000000000000151","member":"{{DueDays}}","value":{"path":["{{TermsDays}}"]} },
               {"id":"01JCXN00000000000000000152","member":"{{IssuedOn}}","value":{"null":true} }]} }]}
            """);
        w.Step.Parent!.AsArray().Add(Obj($$"""{"id":"01JPRX00000000000000000152","name":"Sub","type":"process","process":"{{Child}}"}"""));
        Assert.DoesNotContain(await FindingsAsync(w), d => Path.GetFileName(d.FilePath) == "child.json");
        var (h, store) = await OpenAsync(w);
        using var _ = h;
        await using var __ = store;
        JsonElement[] objects = [Json($$"""{"id":"o1","name":"invoice","entity":"{{Invoice}}","values":{"{{TermsDays}}":45} }""")];
        var result = await SimulateAsync(store, [Json($$"""{"event":"{{Issue}}","payload":{"{{DueDaysPayload}}":10,"{{IssuedOnPayload}}":"2026-01-02"} }""")], objects);
        var step = result.Trace[^1];
        Assert.True(step.Accepted);
        var run = step.Microsteps.SelectMany(m => m.Actions).Single(a => a.Action == ChildAsk);
        Assert.Equal(ActionSource.Stub, run.Source);
        var call = Assert.IsType<CallRun>(run.Call);
        Assert.Equal(IssueOp, call.Operation);
        // The object is not the sub-process's: the read gives null, and the trace says why. A null given is recorded as null, with no note.
        Assert.Equal(JsonValueKind.Null, call.Inputs[DueDays].ValueKind);
        Assert.Equal(JsonValueKind.Null, call.Inputs[IssuedOn].ValueKind);
        Assert.DoesNotContain(step.Notes ?? [], n => n.Contains("could not be worked out", StringComparison.Ordinal));
        Assert.Contains(step.Notes ?? [], n => n.Contains("of invoked process 'Child' gives its operation an input that reads the object", StringComparison.Ordinal));
        Assert.Contains(step.Notes ?? [], n => n.Contains("'ChildAsk' called an operation: recorded, not run", StringComparison.Ordinal));
        Assert.Empty(run.Changed);
    }

    private static JsonObject Record(ResolvedModel model, string scope, string id) =>
        JsonSerializer.SerializeToNode(ResolvedRecords.Entries(model, scope, null).Single(e => e.Id == id).Project(), Web)!.AsObject();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, JsonElement[] steps, JsonElement[]? objects = null)
    {
        var call = await store.SimulateProcessAsync(Lifecycle, new SimulateRequest { Steps = steps, Objects = objects, Subject = objects is null ? null : "o1" }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!;
    }
}
