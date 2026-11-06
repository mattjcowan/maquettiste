using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Object effects in simulation and replay (object-effects-design.md decisions 4, 5 and 10, section 7), over an invented model beside the
/// <c>process-basics</c> fixture: a purchase request whose actions set its data on leaving a state, on a move and on entering a state; an
/// employee whose employment lifecycle is held in a coded status; and an assignment whose start reads the employee's state.
/// </summary>
public sealed class ObjectEffectsReplayTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Request = "01JENT00000000000000000071";
    private const string Employee = "01JENT00000000000000000072";
    private const string Assignment = "01JENT00000000000000000073";
    private const string Trail = "01JATT00000000000000000702";
    private const string SubmittedBy = "01JATT00000000000000000703";
    private const string ApproverNo = "01JATT00000000000000000704";
    private const string OwnerId = "01JATT00000000000000000705";
    private const string A = "01JATT00000000000000000706";
    private const string B = "01JATT00000000000000000707";
    private const string Count = "01JATT00000000000000000708";
    private const string OwnerLogin = "01JATT00000000000000000709";
    private const string EStatus = "01JATT00000000000000000712";
    private const string N = "01JATT00000000000000000731";
    private const string EmployeeEnd = "01JEND00000000000000000072";
    private const string Owner = "01JACT00000000000000000071";
    private const string Purchase = "01JPRC00000000000000000071";
    private const string Employment = "01JPRC00000000000000000072";
    private const string Staffing = "01JPRC00000000000000000073";
    private const string Submit = "01JPRX00000000000000000701";
    private const string Approve = "01JPRX00000000000000000702";
    private const string Swap = "01JPRX00000000000000000703";
    private const string Bump = "01JPRX00000000000000000704";
    private const string Claim = "01JPRX00000000000000000705";
    private const string Own = "01JPRX00000000000000000706";
    private const string Take = "01JPRX00000000000000000707";
    private const string Start = "01JPRX00000000000000000709";
    private const string LeaveDraft = "01JPRX00000000000000000711";
    private const string Move = "01JPRX00000000000000000712";
    private const string EnterReview = "01JPRX00000000000000000713";
    private const string Draft = "01JSTA00000000000000000701";
    private const string Review = "01JSTA00000000000000000702";
    private const string Approved = "01JSTA00000000000000000703";
    private const string Applicant = "01JSTA00000000000000000711";
    private const string Active = "01JSTA00000000000000000712";
    private const string OnLeave = "01JSTA00000000000000000713";
    private const string Planned = "01JSTA00000000000000000721";
    private const string Running = "01JSTA00000000000000000722";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ActorValue = "{\"actor\":true}";

    private static string Path(string id) => $$"""{"path":["{{id}}"]}""";

    private static string Append(string id, string text) => $$"""{"op":"concat","args":[{{Path(id)}},{"value":"{{text}}"}]}""";

    private static string Set(int n, string attribute, string value) =>
        $$"""{"id":"01JSET000000000000000007{{n:D2}}","attribute":"{{attribute}}","value":{{value}} }""";

    private static string Attribute(string id, string name, string type, string extra = "") =>
        $$"""{"id":"{{id}}","name":"{{name}}","type":"{{type}}"{{extra}}}""";

    // The invented model. statusDefault gives Employee.status a default.
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? statusDefault = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/request.json", $$"""
            {"kind":"entity","id":"{{Request}}","name":"Request","package":"{{Package}}","key":{"attributes":["01JATT00000000000000000701"]},
             "attributes":[{{Attribute("01JATT00000000000000000701", "id", "uuid")}},{{Attribute(Trail, "trail", "string", ",\"length\":40,\"default\":\"\"")}},
               {{Attribute(SubmittedBy, "submittedBy", "string", ",\"length\":40")}},{{Attribute(ApproverNo, "approverNo", "int32")}},{{Attribute(OwnerId, "ownerId", "uuid")}},
               {{Attribute(A, "a", "string", ",\"length\":40,\"default\":\"A\"")}},{{Attribute(B, "b", "string", ",\"length\":40,\"default\":\"B\"")}},
               {{Attribute(Count, "count", "int32", ",\"default\":0")}},{{Attribute(OwnerLogin, "ownerLogin", "string", ",\"length\":40")}}]}
            """);
        var status = statusDefault is null ? ",\"length\":20" : $",\"length\":20,\"default\":\"{statusDefault}\"";
        harness.Write("model/entities/employee.json", $$"""
            {"kind":"entity","id":"{{Employee}}","name":"Employee","package":"{{Package}}","key":{"attributes":["01JATT00000000000000000711"]},
             "attributes":[{{Attribute("01JATT00000000000000000711", "id", "uuid")}},{{Attribute(EStatus, "status", "string", status)}}]}
            """);
        harness.Write("model/entities/assignment.json", $$"""
            {"kind":"entity","id":"{{Assignment}}","name":"Assignment","package":"{{Package}}","key":{"attributes":["01JATT00000000000000000721"]},
             "attributes":[{{Attribute("01JATT00000000000000000721", "id", "uuid")}}]}
            """);
        harness.Write("model/relations/assignment-employee.json", $$"""
            {"kind":"relation","id":"01JRXN00000000000000000071","name":"AssignmentEmployee",
             "ends":[{"id":"01JEND00000000000000000071","entity":"{{Assignment}}","role":"assignments"},{"id":"{{EmployeeEnd}}","entity":"{{Employee}}","role":"employee","navigation":"employee","max":1}]}
            """);
        harness.Write("model/actors/owner.json", $$"""{"kind":"actor","id":"{{Owner}}","name":"Owner","type":"person","relative":{"owner":true} }""");
        harness.Write("model/processes/purchase.json", $$"""
            {"kind":"process","id":"{{Purchase}}","name":"Purchase","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000071","entity":"{{Request}}","owner":"{{OwnerLogin}}"}],
             "context":[{{Attribute(N, "n", "int32", ",\"default\":5")}}],
             "events":[{"id":"{{Submit}}","name":"submit"},{"id":"{{Approve}}","name":"approve"},{"id":"{{Swap}}","name":"swap"},{"id":"{{Bump}}","name":"bump"},
               {"id":"{{Claim}}","name":"claim"},{"id":"{{Own}}","name":"own"},{"id":"{{Take}}","name":"take","actors":["{{Owner}}"]}],
             "guards":[{"id":"01JPRX00000000000000000721","name":"notSubmitter",
               "condition":{"op":"and","args":[{"op":"ne","args":[{{Path(SubmittedBy)}},{"actor":true}]},{"inState":["{{Review}}"]}]} }],
             "actions":[
               {"id":"{{LeaveDraft}}","name":"leaveDraft","sets":[{{Set(1, Trail, Append(Trail, "x"))}}]},
               {"id":"{{Move}}","name":"move","sets":[{{Set(2, Trail, Append(Trail, "t"))}},{{Set(3, SubmittedBy, ActorValue)}}]},
               {"id":"{{EnterReview}}","name":"enterReview","sets":[{{Set(4, Trail, Append(Trail, "e"))}}]},
               {"id":"01JPRX00000000000000000714","name":"swapAB","sets":[{{Set(5, A, Path(B))}},{{Set(6, B, Path(A))}}]},
               {"id":"01JPRX00000000000000000715","name":"bump","expression":"({ n: context.n + 1 })","sets":[{{Set(7, Count, Path(N))}}]},
               {"id":"01JPRX00000000000000000716","name":"claim","sets":[{{Set(8, ApproverNo, ActorValue)}},{{Set(9, OwnerId, ActorValue)}}]},
               {"id":"01JPRX00000000000000000717","name":"own","sets":[{{Set(10, OwnerLogin, ActorValue)}}]}],
             "states":[{"id":"{{Draft}}","name":"Draft","exit":["{{LeaveDraft}}"]},{"id":"{{Review}}","name":"Review","entry":["{{EnterReview}}"]},
               {"id":"{{Approved}}","name":"Approved","type":"final"}],
             "initial":"{{Draft}}",
             "transitions":[
               {"id":"01JTRN00000000000000000701","source":"{{Draft}}","event":"{{Submit}}","targets":["{{Review}}"],"actions":["{{Move}}"]},
               {"id":"01JTRN00000000000000000702","source":"{{Review}}","event":"{{Approve}}","guard":"01JPRX00000000000000000721","targets":["{{Approved}}"]},
               {"id":"01JTRN00000000000000000703","source":"{{Review}}","event":"{{Swap}}","actions":["01JPRX00000000000000000714"]},
               {"id":"01JTRN00000000000000000704","source":"{{Review}}","event":"{{Bump}}","actions":["01JPRX00000000000000000715"]},
               {"id":"01JTRN00000000000000000705","source":"{{Review}}","event":"{{Claim}}","actions":["01JPRX00000000000000000716"]},
               {"id":"01JTRN00000000000000000706","source":"{{Draft}}","event":"{{Own}}","actions":["01JPRX00000000000000000717"]},
               {"id":"01JTRN00000000000000000707","source":"{{Draft}}","event":"{{Take}}","targets":["{{Review}}"]}]}
            """);
        harness.Write("model/processes/employment.json", $$"""
            {"kind":"process","id":"{{Employment}}","name":"Employment","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000072","entity":"{{Employee}}","boundAttribute":"{{EStatus}}"}],
             "events":[{"id":"01JPRX00000000000000000708","name":"hire"}],
             "states":[{"id":"{{Applicant}}","name":"Applicant","code":"applicant"},{"id":"{{Active}}","name":"Active","code":"active"},
               {"id":"{{OnLeave}}","name":"OnLeave","code":"onleave"}],
             "initial":"{{Applicant}}",
             "transitions":[{"id":"01JTRN00000000000000000711","source":"{{Applicant}}","event":"01JPRX00000000000000000708","targets":["{{Active}}"]}]}
            """);
        harness.Write("model/processes/staffing.json", $$"""
            {"kind":"process","id":"{{Staffing}}","name":"Staffing","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000073","entity":"{{Assignment}}"}],
             "events":[{"id":"{{Start}}","name":"start"}],
             "guards":[{"id":"01JPRX00000000000000000722","name":"employeeActive","condition":{"inState":["{{Active}}"],"of":["{{EmployeeEnd}}"]} }],
             "states":[{"id":"{{Planned}}","name":"Planned"},{"id":"{{Running}}","name":"Running"}],
             "initial":"{{Planned}}",
             "transitions":[{"id":"01JTRN00000000000000000721","source":"{{Planned}}","event":"{{Start}}","guard":"01JPRX00000000000000000722","targets":["{{Running}}"]}]}
            """);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement RequestObject(string values = "") =>
        Json($$"""{"id":"01JXBJ00000000000000000071","name":"req","entity":"{{Request}}","values":{ {{values}} } }""");

    private static JsonElement Step(string ev, string extra = "") => Json($$"""{"event":"{{ev}}"{{extra}}}""");

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, string process, JsonElement[] steps, JsonElement[]? objects = null, string? subject = null,
        JsonElement[]? participants = null)
    {
        var call = await store.SimulateProcessAsync(process, new SimulateRequest { Steps = steps, Objects = objects, Subject = subject, Participants = participants }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!;
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement>? values, string id) => values![id].GetString()!;

    [Fact]
    public async Task The_model_validates_cleanly()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("model/", StringComparison.Ordinal) && !(d.FilePath ?? "").Contains("order", StringComparison.Ordinal))
            .Select(d => d.Rule + " " + d.Message));
    }

    [Fact]
    public async Task Sets_run_in_order_exit_then_transition_then_entry_and_read_the_object_as_the_action_started()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await SimulateAsync(store, Purchase, [Step(Submit, ",\"signer\":\"bob\""), Step(Swap), Step(Bump), Step(Bump)],
            [RequestObject()], "01JXBJ00000000000000000071");
        var submit = result.Trace[1];
        // Each action appends to the trail as the one before left it: exit, then the move, then entry.
        Assert.Equal([LeaveDraft, Move, EnterReview], submit.Microsteps.SelectMany(m => m.Actions).Select(a => a.Action));
        Assert.Equal(["x", "xt", "xte"], submit.Microsteps.SelectMany(m => m.Actions).Select(a => Text(a.Sets, Trail)));
        Assert.All(submit.Microsteps.SelectMany(m => m.Actions), a => Assert.Equal(ActionSource.Sets, a.Source));
        Assert.Equal("xte", Text(submit.ValuesChanged, Trail));
        Assert.Equal("bob", Text(submit.ValuesChanged, SubmittedBy));
        Assert.Equal([Trail, SubmittedBy], submit.ValuesChanged!.Keys.Order(StringComparer.Ordinal));

        // Within one action, every value reads the object as it was: a and b swap.
        var swap = result.Trace[2];
        Assert.Equal(("B", "A"), (Text(swap.ValuesChanged, A), Text(swap.ValuesChanged, B)));

        // A set reads the context from before the action's expression: count takes n as it was, and the next bump sees the new n.
        Assert.Equal(5, result.Trace[3].ValuesChanged![Count].GetInt32());
        Assert.Equal(ActionSource.Expression, result.Trace[3].Microsteps.Single().Actions.Single().Source);
        Assert.Equal(6, result.Trace[4].ValuesChanged![Count].GetInt32());

        // Nothing set at the initial entry: the members are left out of the JSON.
        var start = JsonSerializer.Serialize(result.Trace[0], EngineJson.Options);
        Assert.DoesNotContain("valuesChanged", start, StringComparison.Ordinal);
        Assert.DoesNotContain("\"notes\"", start, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"sets\"", JsonSerializer.Serialize(submit, EngineJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_guard_sees_the_new_values_and_the_running_chart()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // Bob submits and is refused approval; Carol approves. With no objects, the set writes into the row of defaults.
        foreach (var objects in new[] { [RequestObject()], Array.Empty<JsonElement>() })
        {
            var result = await SimulateAsync(store, Purchase,
                [Step(Submit, ",\"signer\":\"bob\""), Step(Approve, ",\"signer\":\"bob\""), Step(Approve, ",\"signer\":\"carol\"")],
                objects, objects.Length == 0 ? null : "01JXBJ00000000000000000071");
            Assert.Equal([true, false, true], result.Trace.Skip(1).Select(t => t.Accepted));
            Assert.Equal(Refusals.Guard, result.Trace[2].Refusal);
            Assert.True(result.Final);
        }
    }

    [Fact]
    public async Task Actor_reads_through_the_type_it_meets_with_a_note_when_the_identity_does_not_convert()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await SimulateAsync(store, Purchase,
            [Step(Submit, ",\"signer\":\"bob\""), Step(Claim, ",\"signer\":\"42\""), Step(Claim, ",\"signer\":\"6f9619ff-8b86-d011-b42d-00cf4fc964ff\"")],
            [RequestObject()], "01JXBJ00000000000000000071");
        var number = result.Trace[2];
        Assert.Equal(42, number.ValuesChanged![ApproverNo].GetInt32());
        Assert.Equal(JsonValueKind.Null, number.Microsteps.Single().Actions.Single().Sets![OwnerId].ValueKind);
        Assert.False(number.ValuesChanged.ContainsKey(OwnerId)); // null before, null after
        Assert.Equal(["The identity '42' is not a uuid, so actor() reads null where it meets one."], number.Notes);

        var uuid = result.Trace[3];
        Assert.Equal("6f9619ff-8b86-d011-b42d-00cf4fc964ff", Text(uuid.ValuesChanged, OwnerId));
        Assert.Equal(JsonValueKind.Null, uuid.ValuesChanged![ApproverNo].ValueKind);
        Assert.Equal(["The identity '6f9619ff-8b86-d011-b42d-00cf4fc964ff' is not an integer, so actor() reads null where it meets one."], uuid.Notes);
    }

    [Fact]
    public async Task Actor_reads_the_participant_identity_and_an_owner_follows_a_set()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var people = new[] { Json("""{"id":"01JPTC00000000000000000071","name":"bob","identity":"bob@example.com"}"""), Json("""{"id":"01JPTC00000000000000000072","name":"ann"}""") };

        // take is the owner's: refused before own sets ownerLogin to the participant's identity, accepted after, for the same participant.
        var result = await SimulateAsync(store, Purchase,
            [Step(Take, ",\"participant\":\"bob\""), Step(Own, ",\"participant\":\"bob\""), Step(Take, ",\"participant\":\"ann\""), Step(Take, ",\"participant\":\"bob\"")],
            [RequestObject()], "01JXBJ00000000000000000071", people);
        Assert.Equal([false, true, false, true], result.Trace.Skip(1).Select(t => t.Accepted));
        Assert.Equal("bob@example.com", Text(result.Trace[2].ValuesChanged, OwnerLogin));
        Assert.Equal([Review], result.Configuration);

        // A participant with no identity acts as its name.
        var named = await SimulateAsync(store, Purchase, [Step(Own, ",\"participant\":\"ann\"")], [RequestObject()], "01JXBJ00000000000000000071", people);
        Assert.Equal("ann", Text(named.Trace[1].ValuesChanged, OwnerLogin));
    }

    [Fact]
    public async Task In_state_reads_given_states_a_bound_value_a_default_and_else_the_initial_state_with_a_note()
    {
        JsonElement[] Objects(string employee) =>
        [
            Json($$"""{"id":"01JXBJ00000000000000000072","name":"a1","entity":"{{Assignment}}","links":[{"end":"{{EmployeeEnd}}","object":"01JXBJ00000000000000000073"}]}"""),
            Json($$"""{"id":"01JXBJ00000000000000000073","name":"ann","entity":"{{Employee}}"{{employee}}}"""),
        ];
        async Task<StepTrace> StartAsync(ModelStore store, string employee)
        {
            var result = await SimulateAsync(store, Staffing, [Step(Start)], Objects(employee), "01JXBJ00000000000000000072");
            return result.Trace[1];
        }

        var (h, store) = await OpenAsync();
        using (h)
        await using (store)
        {
            var given = await StartAsync(store, $$""","states":["{{Active}}"]""");
            Assert.True(given.Accepted, JsonSerializer.Serialize(given, EngineJson.Options));
            Assert.Null(given.Notes);
            Assert.False((await StartAsync(store, $$""","states":["{{OnLeave}}"]""")).Accepted);

            var bound = await StartAsync(store, $$""","values":{"{{EStatus}}":"active"}""");
            Assert.True(bound.Accepted);
            Assert.Null(bound.Notes);

            var initial = await StartAsync(store, "");
            Assert.False(initial.Accepted);
            Assert.Equal(["Object 'ann' gives no state in 'Employment'; in_state reads its initial state 'Applicant'."], initial.Notes);

            // A link not given: no object at the end of the path, so the guard fails, with no note.
            var unlinked = await SimulateAsync(store, Staffing, [Step(Start)],
                [Json($$"""{"id":"01JXBJ00000000000000000072","name":"a1","entity":"{{Assignment}}"}""")], "01JXBJ00000000000000000072");
            Assert.False(unlinked.Trace[1].Accepted);
            Assert.Null(unlinked.Trace[1].Notes);

            // A state that disagrees with the bound value the object gives is MQ9305.
            var call = await store.RecordScenarioAsync(Staffing, new RecordScenarioRequest
            {
                Name = "disagrees", Steps = [Step(Start)], Objects = Objects($$""","states":["{{OnLeave}}"],"values":{"{{EStatus}}":"active"}"""),
                Subject = "01JXBJ00000000000000000072",
            }, true, ChangeSource.Editor, Ct);
            Assert.Equal(ProcessCallStatus.Invalid, call.Status);
            Assert.Contains(call.Diagnostics, d => d.Rule == "MQ9305" && d.Message.Contains("maps to state 'Active'", StringComparison.Ordinal));
        }

        // The bound attribute's default maps to a state, with a note.
        var (h2, store2) = await OpenAsync(statusDefault: "active");
        using var _ = h2;
        await using var __ = store2;
        var byDefault = await StartAsync(store2, "");
        Assert.True(byDefault.Accepted);
        Assert.Equal(["Object 'ann' gives no state in 'Employment'; in_state reads 'Active', the state the default of 'status' maps to."], byDefault.Notes);
    }

    [Fact]
    public async Task Record_writes_expected_values_and_verify_compares_them()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var call = await store.RecordScenarioAsync(Purchase, new RecordScenarioRequest
        {
            Name = "bobSubmits", Objects = [RequestObject()], Subject = "01JXBJ00000000000000000071",
            Steps = [Step(Submit, ",\"signer\":\"bob\""), Step(Approve, ",\"signer\":\"bob\""), Step(Swap)],
        }, false, ChangeSource.Editor, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        var saved = store.Current!.Get<Scenario>(call.Value!.Id)!;
        Assert.Equal("bob", saved.Steps[0].Expect!.Values[SubmittedBy].GetString());
        Assert.Equal("xte", saved.Steps[0].Expect!.Values[Trail].GetString());
        Assert.Empty(saved.Steps[1].Expect!.Values);
        Assert.Equal(["a", "b"], saved.Steps[2].Expect!.Values.Keys.Select(k => k == A ? "a" : "b").Order(StringComparer.Ordinal));
        var file = JsonNode.Parse(call.Value.Element.GetRawText())!;
        Assert.Null(file["steps"]![1]!["expect"]!["values"]); // written only when a step set something

        var verified = await store.VerifyScenariosAsync(Purchase, [saved.Id], Ct);
        Assert.True(verified.Value!.Passed, string.Join(" | ", verified.Value.Results.Select(r => r.Failure?.Message)));

        // An expected value that differs is MQ9307.
        var node = JsonNode.Parse(call.Value.Element.GetRawText())!;
        node["steps"]![0]!["expect"]!["values"]![SubmittedBy] = "carol";
        var resaved = await store.SaveAsync(saved.Id, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), store.Current!.GetDocument(saved.Id)!.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, resaved.Outcome);
        var failed = await store.VerifyScenariosAsync(Purchase, [saved.Id], Ct);
        var failure = Assert.Single(failed.Value!.Results).Failure!;
        Assert.Equal("MQ9307", failure.Rule);
        Assert.Contains("sets trail = \"xte\", submittedBy = \"bob\" on the object, but expect.values lists trail = \"xte\", submittedBy = \"carol\"", failure.Message, StringComparison.Ordinal);

        // Updating the expectations from a replay (refresh-scenario) rewrites expect.values by attribute id, as it does the context and states.
        var refreshed = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.RefreshScenario, saved.Id, null, null)]), ChangeSource.Editor, Ct);
        Assert.True(refreshed.Outcome == SaveOutcome.Saved, string.Join(" | ", refreshed.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var values = JsonNode.Parse((await store.GetElementAsync(saved.Id, Ct))!.Json.GetRawText())!["steps"]![0]!["expect"]!["values"]!.AsObject();
        Assert.Equal([Trail, SubmittedBy], values.Select(p => p.Key));
        Assert.Equal("bob", (string)values[SubmittedBy]!);
        Assert.True((await store.VerifyScenariosAsync(Purchase, [saved.Id], Ct)).Value!.Passed);
    }
}
