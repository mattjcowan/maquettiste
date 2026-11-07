using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Freezing and attribute rights in simulation, recording and replay (rights-design.md decisions 16 to 18 and 21, section 7; section 14's
/// part 2 freezing and attribute cases), over an invented item lifecycle beside the <c>process-basics</c> fixture. Its events name freeze
/// and thaw (or, as a 0.15.0 project would read them, promote and demote), and with rights it denies salary to all but HR staff and
/// changes of owner while frozen.
/// </summary>
public sealed class FreezingReplayTests
{
    private const string Package = "01JPKG00000000000000000001";

    private const string Item = "01JFZT00000000000000000010";
    private const string Part = "01JFZT00000000000000000020";
    private const string Title = "01JFZT00000000000000000012";
    private const string Salary = "01JFZT00000000000000000013";
    private const string Trail = "01JFZT00000000000000000014";
    private const string Parts = "01JFZN00000000000000000003";

    private const string Editor = "01JFZA00000000000000000001";
    private const string HrStaff = "01JFZA00000000000000000002";

    private const string Items = "01JFZP00000000000000000001";
    private const string Draft = "01JFZS00000000000000000001";
    private const string Released = "01JFZS00000000000000000002";
    private const string Obsolete = "01JFZS00000000000000000003";

    private const string Edit = "01JFZX00000000000000000001";
    private const string Release = "01JFZX00000000000000000002";
    private const string Attach = "01JFZX00000000000000000003";
    private const string Detach = "01JFZX00000000000000000004";
    private const string Remove = "01JFZX00000000000000000005";
    private const string Retire = "01JFZX00000000000000000006";
    private const string Reopen = "01JFZX00000000000000000007";

    private const string HrOnlySalary = "01JFZR00000000000000000001";
    private const string NoReadSalary = "01JFZR00000000000000000002";
    private const string NoOwnerChangeWhileFrozen = "01JFZR00000000000000000004";
    private const string NoPeekAtFrozenParts = "01JFZR00000000000000000005";

    private const string Box = "01JFZJ00000000000000000001";
    private const string Bolt = "01JFZJ00000000000000000002";
    private const string Nut = "01JFZJ00000000000000000003";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Attribute(string id, string name, string type, string extra = "") =>
        $$"""{"id":"{{id}}","name":"{{name}}","type":"{{type}}"{{extra}}}""";

    private static string Mark(string set, string text) =>
        $$"""{"id":"{{set}}","attribute":"{{Trail}}","value":{"op":"concat","args":[{"path":["{{Trail}}"]},{"value":"{{text}}"}]} }""";

    private static string Transition(int n, string source, string ev, string? target = null, string? action = null) =>
        $$"""{"id":"01JFZK000000000000000000{{n:D2}}","source":"{{source}}","event":"{{ev}}"{{(target is null ? "" : $",\"targets\":[\"{target}\"]")}}{{(action is null ? "" : $",\"actions\":[\"{action}\"]")}}}""";

    // The item lifecycle. freezes: release names freeze and reopen thaw (else promote and demote, as a project read them before freezing
    // took effect). rights: salary only for HR staff, no change of owner while frozen, and no read of a frozen link to a part.
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(bool freezes = true, bool rights = false, string? scenario = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/item.json", $$"""
            {"kind":"entity","id":"{{Item}}","name":"Item","package":"{{Package}}","key":{"attributes":["01JFZT00000000000000000011"]},
             "attributes":[{{Attribute("01JFZT00000000000000000011", "id", "uuid")}},{{Attribute(Title, "title", "string", ",\"length\":40")}},
               {{Attribute(Salary, "salary", "string", ",\"length\":20")}},{{Attribute(Trail, "trail", "string", ",\"length\":40")}}]}
            """);
        harness.Write("model/entities/part.json", $$"""
            {"kind":"entity","id":"{{Part}}","name":"Part","package":"{{Package}}","key":{"attributes":["01JFZT00000000000000000021"]},
             "attributes":[{{Attribute("01JFZT00000000000000000021", "id", "uuid")}}]}
            """);
        harness.Write("model/relations/item-parts.json", $$"""
            {"kind":"relation","id":"01JFZN00000000000000000001","name":"ItemParts",
             "ends":[{"id":"01JFZN00000000000000000002","entity":"{{Item}}","role":"item"},
               {"id":"{{Parts}}","entity":"{{Part}}","role":"parts","navigation":"parts"}]}
            """);
        harness.Write("model/actors/editor.json", $$"""{"kind":"actor","id":"{{Editor}}","name":"Editor","type":"role"}""");
        harness.Write("model/actors/hr-staff.json", $$"""{"kind":"actor","id":"{{HrStaff}}","name":"HrStaff","type":"role"}""");
        var rightsJson = !rights ? "" : $$"""
            ,"rights":[{"id":"{{HrOnlySalary}}","name":"HrOnlySalary","access":"deny","verbs":["modify"],"attributes":["{{Salary}}"],"except":["{{HrStaff}}"]},
              {"id":"{{NoReadSalary}}","name":"NoReadSalary","access":"deny","verbs":["read"],"attributes":["{{Salary}}"],"except":["{{HrStaff}}"]},
              {"id":"01JFZR00000000000000000003","name":"EditorsEdit","access":"allow","verbs":["modify"],"actors":["{{Editor}}"]},
              {"id":"{{NoOwnerChangeWhileFrozen}}","name":"NoOwnerChangeWhileFrozen","access":"deny","verbs":["change-owner"],"whileFrozen":true},
              {"id":"{{NoPeekAtFrozenParts}}","name":"NoPeekAtFrozenParts","access":"deny","verbs":["read"],"ends":["{{Parts}}"],"whileFrozen":true}]
            """;
        harness.Write("model/processes/items.json", $$"""
            {"kind":"process","id":"{{Items}}","name":"Items","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JFZB00000000000000000001","entity":"{{Item}}"}],
             "events":[{"id":"{{Edit}}","name":"edit","verb":"modify"},{"id":"{{Release}}","name":"release","verb":"{{(freezes ? "freeze" : "promote")}}"},
               {"id":"{{Attach}}","name":"attach","verb":"connect"},{"id":"{{Detach}}","name":"detach","verb":"disconnect"},
               {"id":"{{Remove}}","name":"remove","verb":"delete"},{"id":"{{Retire}}","name":"retire","verb":"promote"},
               {"id":"{{Reopen}}","name":"reopen","verb":"{{(freezes ? "thaw" : "demote")}}"}],
             "actions":[{"id":"01JFZC00000000000000000001","name":"stamp","sets":[{{Mark("01JFZE00000000000000000001", "r")}}]},
               {"id":"01JFZC00000000000000000002","name":"mark","sets":[{{Mark("01JFZE00000000000000000002", "o")}}]}],
             "states":[{"id":"{{Draft}}","name":"Draft"},{"id":"{{Released}}","name":"Released"},{"id":"{{Obsolete}}","name":"Obsolete"}],
             "initial":"{{Draft}}",
             "transitions":[{{Transition(1, Draft, Edit)}},{{Transition(2, Released, Edit)}},{{Transition(3, Obsolete, Edit)}},
               {{Transition(4, Draft, Attach)}},{{Transition(5, Released, Attach)}},{{Transition(6, Draft, Detach)}},{{Transition(7, Released, Detach)}},
               {{Transition(8, Draft, Remove)}},{{Transition(9, Released, Remove)}},{{Transition(10, Draft, Release, Released, "01JFZC00000000000000000001")}},
               {{Transition(11, Released, Retire, Obsolete, "01JFZC00000000000000000002")}},{{Transition(12, Released, Reopen, Draft, "01JFZC00000000000000000002")}},
               {{Transition(13, Obsolete, Reopen, Draft)}}]{{rightsJson}}}
            """);
        if (scenario is not null)
            harness.Write("model/scenarios/items/freezing.json", scenario);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // The item box with the part bolt linked through parts, and the part nut; frozen marks the box, frozenLink its link to bolt.
    private static JsonElement[] Objects(bool frozen = false, bool frozenLink = false) =>
    [
        Json($$"""{"id":"{{Box}}","name":"box","entity":"{{Item}}","values":{"{{Trail}}":"-"},{{(frozen ? "\"frozen\":true," : "")}}"links":[{"end":"{{Parts}}","object":"{{Bolt}}"{{(frozenLink ? ",\"frozen\":true" : "")}}}]}"""),
        Json($$"""{"id":"{{Bolt}}","name":"bolt","entity":"{{Part}}"}"""),
        Json($$"""{"id":"{{Nut}}","name":"nut","entity":"{{Part}}"}"""),
    ];

    private static JsonElement[] People() =>
    [
        Json($$"""{"id":"p1","name":"ed","holds":[{"id":"h1","actor":"{{Editor}}"}]}"""),
        Json($$"""{"id":"p2","name":"hal","holds":[{"id":"h2","actor":"{{HrStaff}}"}]}"""),
    ];

    private static JsonElement Verb(string verb, string participant = "p1", string extra = "") =>
        Json($$"""{"input":"verb","verb":"{{verb}}","participant":"{{participant}}"{{extra}}}""");

    private static JsonElement Event(string ev, string participant = "p1") => Json($$"""{"event":"{{ev}}","participant":"{{participant}}"}""");

    private static string Link(string obj) => $",\"end\":\"{Parts}\",\"object\":\"{obj}\"";

    private static string Values(string attribute, string value) => $",\"values\":{{\"{attribute}\":\"{value}\"}}";

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, JsonElement[] steps, JsonElement[]? objects = null)
    {
        var call = await store.SimulateProcessAsync(Items, new SimulateRequest { Steps = steps, Objects = objects ?? Objects(), Subject = Box, Participants = People() }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!;
    }

    private static void Frozen(StepTrace trace)
    {
        Assert.False(trace.Accepted, $"step {trace.Index} was accepted");
        Assert.Equal(Refusals.Frozen, trace.Refusal);
        Assert.Null(trace.RefusedBy);
        Assert.Null(trace.Rights);
    }

    private static void Refused(StepTrace trace, string right)
    {
        Assert.False(trace.Accepted, $"step {trace.Index} was accepted");
        Assert.Equal((Refusals.Right, right), (trace.Refusal, trace.RefusedBy));
    }

    private static string Notes(StepTrace trace) => string.Join(" ", trace.Notes ?? []);

    [Fact]
    public async Task The_models_validate_without_errors()
    {
        foreach (var rights in new[] { false, true })
        {
            var (h, store) = await OpenAsync(rights: rights);
            using var _ = h;
            await using var __ = store;
            var report = await store.ValidateAsync(ValidationScope.All, Ct);
            Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }
    }

    // A 0.15.0 project whose events name freeze and thaw: its runs now freeze, and only the steps that then modify, connect, disconnect or
    // delete change; a move's sets on the frozen object apply with a note, and a thaw's own move runs thawed.
    [Fact]
    public async Task A_project_whose_events_name_freeze_now_freezes_and_only_later_modify_connect_disconnect_and_delete_change()
    {
        JsonElement[] steps =
        [
            Event(Edit), Event(Release), Event(Edit), Event(Attach), Event(Detach), Event(Remove), Event(Retire), Event(Edit), Event(Reopen), Event(Edit),
            Event(Release), Event(Reopen),
        ];
        var (oldH, oldStore) = await OpenAsync(freezes: false);
        using var _ = oldH;
        await using var __ = oldStore;
        var before = await SimulateAsync(oldStore, steps);
        var (h, store) = await OpenAsync();
        using var ___ = h;
        await using var ____ = store;
        var after = await SimulateAsync(store, steps);

        int[] changed = [3, 4, 5, 6, 8]; // trace indexes: the start is 0
        for (var i = 0; i < after.Trace.Count; i++)
        {
            Assert.True(before.Trace[i].Accepted, $"trace {i} was refused before freezing took effect");
            Assert.False(before.Trace[i].Frozen);
            if (changed.Contains(i))
            {
                Frozen(after.Trace[i]);
                continue;
            }

            Assert.Equal(Comparable(before.Trace[i]), Comparable(after.Trace[i]));
        }

        Assert.Equal([false, false, true, true, true, true, true, true, true, false, false, true, false], after.Trace.Select(t => t.Frozen));
        Assert.Equal("-ro", after.Trace[7].ValuesChanged![Trail].GetString());
        Assert.Contains("the move's sets applied", Notes(after.Trace[7]), StringComparison.Ordinal);
        Assert.DoesNotContain("sets applied", Notes(after.Trace[2]) + Notes(after.Trace[9]) + Notes(after.Trace[12]), StringComparison.Ordinal);
        Assert.Contains("\"frozen\":true", JsonSerializer.Serialize(after.Trace[2], EngineJson.Options), StringComparison.Ordinal);
        Assert.DoesNotContain("frozen", JsonSerializer.Serialize(after.Trace[1], EngineJson.Options), StringComparison.Ordinal);

        // The trace without frozen and notes, which freezing adds.
        static string Comparable(StepTrace trace)
        {
            var node = JsonSerializer.SerializeToNode(trace, EngineJson.Options)!.AsObject();
            node.Remove("frozen");
            node.Remove("notes");
            return node.ToJsonString();
        }
    }

    [Fact]
    public async Task Freeze_and_thaw_steps_change_the_flags_and_frozen_refuses_before_any_right()
    {
        var (h, store) = await OpenAsync(rights: true);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store,
        [
            Verb("change-owner"), // 1: not frozen, so the whileFrozen deny says nothing
            Verb("freeze"), Verb("freeze"), // 2, 3: the second changes nothing
            Verb("modify", "p1", Values(Title, "Box")), // 4: EditorsEdit would allow it
            Verb("change-owner"), // 5: the whileFrozen deny holds
            Verb("connect", "p1", Link(Nut)), Verb("disconnect", "p1", Link(Bolt)), Verb("delete"), // 6 to 8
            Verb("read", "p2"), Verb("revise"), // 9, 10: freezing stops nothing else
            Verb("thaw"), Verb("thaw"), // 11, 12
            Verb("freeze", "p1", Link(Bolt)), // 13: the link only
            Verb("disconnect", "p1", Link(Bolt)), Verb("read", "p1", Link(Bolt)), // 14, 15
            Verb("modify", "p1", Values(Title, "Box")), Verb("connect", "p1", Link(Nut)), // 16, 17: the object is not frozen by its link
            Event(Detach), // 18: with no end known, a frozen link refuses disconnect
            Verb("thaw", "p1", Link(Bolt)), Verb("disconnect", "p1", Link(Bolt)), // 19, 20
        ]);
        var t = result.Trace;
        Assert.True(t[1].Accepted && !t[1].Frozen);
        Assert.True(t[2].Accepted && t[2].Frozen && t[2].Notes is null);
        Assert.True(t[3].Accepted && t[3].Frozen);
        Assert.Contains("already frozen", Notes(t[3]), StringComparison.Ordinal);
        Frozen(t[4]);
        Assert.Null(t[4].ValuesChanged);
        Refused(t[5], NoOwnerChangeWhileFrozen);
        Frozen(t[6]);
        Frozen(t[7]);
        Frozen(t[8]);
        Assert.True(t[9].Accepted && t[10].Accepted);
        Assert.True(t[11].Accepted && !t[11].Frozen && t[11].Notes is null);
        Assert.Contains("was not frozen", Notes(t[12]), StringComparison.Ordinal);
        Assert.True(t[13].Accepted && !t[13].Frozen);
        Frozen(t[14]);
        Refused(t[15], NoPeekAtFrozenParts);
        Assert.True(t[16].Accepted && t[17].Accepted);
        Assert.Equal("Box", t[16].ValuesChanged![Title].GetString());
        Frozen(t[18]);
        Assert.True(t[19].Accepted && t[20].Accepted, t[19].Refusal + " " + t[20].Refusal + " " + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public async Task A_modify_step_is_checked_per_attribute_and_a_read_step_by_its_attributes()
    {
        var (h, store) = await OpenAsync(rights: true);
        using var _ = h;
        await using var __ = store;
        var both = $",\"values\":{{\"{Title}\":\"Box\",\"{Salary}\":\"10\"}}";
        var result = await SimulateAsync(store,
        [
            Verb("modify", "p1", Values(Salary, "10")), // 1: the attribute right denies salary to all but HR staff
            Verb("modify", "p1", Values(Title, "Box")), // 2: it names only salary, and EditorsEdit allows
            Verb("modify", "p1", both), // 3: one refused attribute refuses the step
            Verb("modify", "p2", both), // 4: HR staff are excepted
            Verb("read", "p1", $",\"attributes\":[\"{Title}\"]"), // 5
            Verb("read", "p1", $",\"attributes\":[\"{Salary}\"]"), // 6
            Verb("read"), // 7: attributes not known: the deny that names attributes holds
            Verb("read", "p2"), // 8
        ]);
        var t = result.Trace;
        Refused(t[1], HrOnlySalary);
        Assert.True(t[2].Accepted);
        Refused(t[3], HrOnlySalary);
        Assert.Null(t[3].ValuesChanged);
        Assert.True(t[4].Accepted);
        Assert.Equal("10", t[4].ValuesChanged![Salary].GetString());
        Assert.True(t[5].Accepted);
        Refused(t[6], NoReadSalary);
        Refused(t[7], NoReadSalary);
        Assert.True(t[8].Accepted);
    }

    [Fact]
    public async Task In_a_project_with_rights_an_event_that_names_freeze_freezes_and_a_later_modify_event_is_refused()
    {
        var (h, store) = await OpenAsync(rights: true);
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, [Event(Release), Event(Edit), Event(Retire), Verb("change-owner"), Event(Reopen), Event(Edit, "p2")]);
        var t = result.Trace;
        Assert.True(t[1].Accepted && t[1].Frozen);
        Frozen(t[2]);
        Assert.True(t[3].Accepted && t[3].Frozen);
        Assert.Contains("the move's sets applied", Notes(t[3]), StringComparison.Ordinal);
        Refused(t[4], NoOwnerChangeWhileFrozen);
        Assert.True(t[5].Accepted && !t[5].Frozen);
        Assert.True(t[6].Accepted);
    }

    [Fact]
    public async Task Objects_and_links_may_start_frozen()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, [Event(Edit), Event(Release), Event(Reopen), Event(Edit), Event(Detach)], Objects(frozen: true, frozenLink: true));
        var t = result.Trace;
        Assert.True(t[0].Frozen);
        Frozen(t[1]);
        // release freezes what is frozen: its move runs, and the freeze changes nothing. reopen thaws the object, not its link.
        Assert.True(t[2].Accepted && t[2].Frozen);
        Assert.Contains("already frozen", Notes(t[2]), StringComparison.Ordinal);
        Assert.True(t[3].Accepted && !t[3].Frozen);
        Assert.True(t[4].Accepted);
        Frozen(t[5]);
    }

    [Fact]
    public async Task Recording_writes_refusal_frozen_and_verify_names_the_step_that_froze_it()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var recorded = await store.RecordScenarioAsync(Items, new RecordScenarioRequest
        {
            Name = "freezing", Objects = Objects(), Subject = Box, Participants = People(),
            Steps = [Event(Release), Event(Edit), Event(Reopen), Event(Edit), Event(Retire)],
        }, false, ChangeSource.Editor, Ct);
        Assert.True(recorded.Status is ProcessCallStatus.Ok, recorded.Detail + string.Join(" | ", recorded.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(recorded.Value!.Diagnostics, d => d.Rule == "MQ9309");
        var steps = recorded.Value.Element.GetProperty("steps");
        Assert.Equal("{\"accepted\":false,\"refusal\":\"frozen\",\"states\":[\"" + Released + "\"]}", JsonSerializer.Serialize(steps[1].GetProperty("expect")));
        // A project without rights records no other refusal (decision 26): retire has no transition from Draft.
        Assert.Equal("{\"accepted\":false,\"states\":[\"" + Draft + "\"]}", JsonSerializer.Serialize(steps[4].GetProperty("expect")));
        var id = recorded.Value.Id;
        var verified = await store.VerifyScenariosAsync(Items, [id], Ct);
        Assert.True(verified.Value!.Results.Single().Passed, verified.Value.Results.Single().Failure?.Message);
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        var scenario = store.Current!.Get<Scenario>(id)!;
        Assert.DoesNotContain(ScenarioReplayer.Replay(scenario, runtime)!.Diagnostics, d => d.Rule == "MQ9309");

        // As 0.15.0 recorded it, the edit after release was accepted: verify fails on it and names the step that froze it, and MQ9309 warns.
        var old = With(scenario, 1, new StepExpectation { Accepted = true, States = [Released] });
        var (oldH, oldStore) = await OpenAsync(scenario: JsonSerializer.Serialize(old, EngineJson.Options));
        using var ___ = oldH;
        await using var ____ = oldStore;
        var failure = (await oldStore.VerifyScenariosAsync(Items, [id], Ct)).Value!.Results.Single().Failure!;
        Assert.Equal(("MQ9301", 1), (failure.Rule, failure.Step));
        Assert.Contains("was refused (frozen) after step 1 (event 'release') froze it, but expect.accepted is true", failure.Message, StringComparison.Ordinal);
        var warning = ScenarioReplayer.Replay(old, runtime)!.Diagnostics.Single(d => d.Rule == "MQ9309");
        Assert.Equal((DiagnosticSeverity.Warning, "/steps/1/expect"), (warning.Severity, warning.JsonPointer));
        Assert.StartsWith("Step 2: event 'edit' was refused as frozen after step 1 (event 'release') froze it;", warning.Message, StringComparison.Ordinal);
        Assert.Equal("refresh-scenario", RuleCatalog.Get("MQ9309")!.QuickFix);
        Assert.Contains((await oldStore.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ9309");

        // Refused without a refusal expected, it still passes, and MQ9309 still names the step. A step the scenario started frozen is quiet.
        var refused = ScenarioReplayer.Replay(With(scenario, 1, new StepExpectation { Accepted = false, States = [Released] }), runtime)!;
        Assert.True(refused.Passed);
        Assert.Contains(refused.Diagnostics, d => d.Rule == "MQ9309");
        var startsFrozen = With(scenario with { Objects = [scenario.Objects.Single(o => o.Id == scenario.Subject) with { Frozen = true }, .. scenario.Objects.Where(o => o.Id != scenario.Subject)] },
            1, new StepExpectation { Accepted = true, States = [Released] });
        var replay = ScenarioReplayer.Replay(startsFrozen, runtime)!;
        Assert.DoesNotContain(replay.Diagnostics, d => d.Rule == "MQ9309");
        Assert.True(replay.Steps[0].Frozen && replay.Steps[0].Accepted);
        Assert.Equal("MQ9301", replay.Failure!.Rule);
        Assert.DoesNotContain("after step", replay.Failure.Message, StringComparison.Ordinal);

        static Scenario With(Scenario s, int index, StepExpectation expect) =>
            s with { Steps = [.. s.Steps.Select((step, i) => i == index ? step with { Expect = expect } : step)] };
    }

    // verify_scenarios names the step that froze it in warnings, also for a scenario that passes; without warnings the member is left out.
    [Fact]
    public async Task Verify_lists_MQ9309_in_warnings_of_a_scenario_that_passes_and_leaves_warnings_out_otherwise()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var recorded = await store.RecordScenarioAsync(Items, new RecordScenarioRequest
        {
            Name = "freezing", Objects = Objects(), Subject = Box, Participants = People(), Steps = [Event(Release), Event(Edit)],
        }, false, ChangeSource.Editor, Ct);
        var id = recorded.Value!.Id;
        var clean = (await store.VerifyScenariosAsync(Items, [id], Ct)).Value!;
        Assert.Null(clean.Results.Single().Warnings);
        Assert.DoesNotContain("warnings", JsonSerializer.Serialize(clean, EngineJson.Options), StringComparison.Ordinal);

        // Refused without a refusal expected: the scenario passes, and its warnings name the step that froze it.
        var scenario = store.Current!.Get<Scenario>(id)!;
        var quiet = scenario with { Steps = [scenario.Steps[0], scenario.Steps[1] with { Expect = new StepExpectation { Accepted = false, States = [Released] } }] };
        var (warnH, warnStore) = await OpenAsync(scenario: JsonSerializer.Serialize(quiet, EngineJson.Options));
        using var ___ = warnH;
        await using var ____ = warnStore;
        var verified = (await warnStore.VerifyScenariosAsync(Items, [id], Ct)).Value!;
        var result = verified.Results.Single();
        Assert.True(result.Passed && verified.Passed);
        Assert.Null(result.Failure);
        var warning = Assert.Single(result.Warnings!);
        Assert.Equal(("MQ9309", DiagnosticSeverity.Warning, "/steps/1/expect"), (warning.Rule, warning.Severity, warning.JsonPointer));
        Assert.Contains("after step 1 (event 'release') froze it", warning.Message, StringComparison.Ordinal);
        Assert.Contains("\"warnings\":[{\"rule\":\"MQ9309\"", JsonSerializer.Serialize(verified, EngineJson.Options), StringComparison.Ordinal);
    }

    // A step with no expect doesn't expect frozen either: refused as frozen after a freeze, it is named by MQ9309 on the step itself, in
    // the replay, in verify's warnings and in validate.
    [Fact]
    public async Task A_step_with_no_expect_refused_as_frozen_is_named_by_MQ9309()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var recorded = await store.RecordScenarioAsync(Items, new RecordScenarioRequest
        {
            Name = "freezing", Objects = Objects(), Subject = Box, Participants = People(), Steps = [Event(Release), Event(Edit)],
        }, false, ChangeSource.Editor, Ct);
        var id = recorded.Value!.Id;
        var scenario = store.Current!.Get<Scenario>(id)!;
        var bare = scenario with { Steps = [scenario.Steps[0], scenario.Steps[1] with { Expect = null }] };
        var (bareH, bareStore) = await OpenAsync(scenario: JsonSerializer.Serialize(bare, EngineJson.Options));
        using var ___ = bareH;
        await using var ____ = bareStore;
        var result = (await bareStore.VerifyScenariosAsync(Items, [id], Ct)).Value!.Results.Single();
        Assert.True(result.Passed, result.Failure?.Message);
        var warning = Assert.Single(result.Warnings!);
        Assert.Equal(("MQ9309", "/steps/1"), (warning.Rule, warning.JsonPointer));
        Assert.StartsWith("Step 2: event 'edit' was refused as frozen after step 1 (event 'release') froze it;", warning.Message, StringComparison.Ordinal);
        Assert.Contains((await bareStore.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ9309");
    }

    // Verify's warnings hold MQ9309 alone: a project that doesn't freeze answers as 0.15.0 did. The process-basics fixture's guard returns 1,
    // which the replay warns of (MQ9504), and verify's answer still has no warnings member.
    [Fact]
    public async Task Verify_answers_of_a_project_that_does_not_freeze_carry_no_other_warning()
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        var text = h.Read("model/processes/order-lifecycle.json");
        Assert.Contains("\"expression\": \"context.amount > 1000\"", text, StringComparison.Ordinal);
        h.Write("model/processes/order-lifecycle.json", text.Replace("\"expression\": \"context.amount > 1000\"", "\"expression\": \"1\"", StringComparison.Ordinal));
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        const string Lifecycle = "01JPRC00000000000000000001";
        var ids = store.Current!.All<Scenario>().Where(s => s.Process == Lifecycle).Select(s => s.Id).Order(StringComparer.Ordinal).ToList();
        using (var runtime = new ProcessRuntime(store.Current!, 1, Ct))
            Assert.Contains(ids, i => ScenarioReplayer.Replay(store.Current!.Get<Scenario>(i)!, runtime)!.Diagnostics.Any(d => d.Rule == "MQ9504"));
        var verified = (await store.VerifyScenariosAsync(Lifecycle, ids, Ct)).Value!;
        Assert.All(verified.Results, r => Assert.Null(r.Warnings));
        Assert.DoesNotContain("warnings", JsonSerializer.Serialize(verified, EngineJson.Options), StringComparison.Ordinal);
    }
}
