using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using static Maquettiste.Engine.Tests.Revisions.RevisionRunTests;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// The paths of a revise in a run that <see cref="RevisionRunTests"/> leaves out (revisions-design.md decisions 16, 18, 19, 22, 28, 34 and 38,
/// section 9), over an invented drawing whose type holds the sequence, with a derived detail that holds none, a sheet and a tag it links to,
/// and small lifecycles that each show one path: row 7b's rank in selection, in a time step and at a finished task; before actions and a
/// replace on a revise; other lifecycles' rights and trigger points after a revise; parallel moves at different levels; a sub-process; the notes;
/// and MQ9305.
/// </summary>
public sealed class RevisionRunPathTests
{
    private const string Package = "01JPKG00000000000000000001";

    private const string Drawing = "01JRWE00000000000000000001";
    private const string Detail = "01JRWE00000000000000000002";
    private const string Sheet = "01JRWE00000000000000000003";
    private const string Tag = "01JRWE00000000000000000004";
    private const string Rev = "01JRWA00000000000000000002";
    private const string ApprovedBy = "01JRWA00000000000000000003";
    private const string Trail = "01JRWA00000000000000000004";
    private const string CheckedOn = "01JRWA00000000000000000005";
    private const string Owner = "01JRWA00000000000000000006";
    private const string Status = "01JRWA00000000000000000007";
    private const string SheetsEnd = "01JRWD00000000000000000002";
    private const string TagsEnd = "01JRWD00000000000000000004";
    private const string Approver = "01JRWR00000000000000000001";

    private const string Main = "01JRWP00000000000000000001";
    private const string Delivery = "01JRWP00000000000000000002";
    private const string Rank = "01JRWP00000000000000000003";
    private const string Para = "01JRWP00000000000000000004";
    private const string Host = "01JRWP00000000000000000005";
    private const string Child = "01JRWP00000000000000000006";
    private const string Timed = "01JRWP00000000000000000007";
    private const string Tasked = "01JRWP00000000000000000008";

    private const string Edit = "01JRWX00000000000000000001"; // modify, InWork to InWork
    private const string Duplicate = "01JRWX00000000000000000002"; // copy, InWork to InWork
    private const string ReviseEvent = "01JRWX00000000000000000003"; // revise, InWork to InWork at the minor level
    private const string Waiting = "01JRWS00000000000000000011";
    private const string Shipped = "01JRWS00000000000000000012";
    private const string NoPromote = "01JRWQ00000000000000000041";
    private const string NoComplete = "01JRWQ00000000000000000061";
    private const string Carry = "01JRWQ00000000000000000003";

    private const string Letters = """
        "levels":[{"name":"major","format":"letters","skip":["I","O","Q","S","X","Z"]},{"name":"minor","format":"numbers","startEmpty":true}]
        """;

    // A list of two values, which a run started at Q has at its end.
    private const string List = """{"levels":[{"values":["P","Q"]}]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string T(string text) => JsonSerializer.Serialize(new { text });

    private static string Block(string extra = "") =>
        $$"""{{{Letters}},"boundAttribute":"{{Rev}}","attributes":[{"attribute":"{{ApprovedBy}}","onRevise":"reset"},{"attribute":"{{Status}}","onRevise":"reset"}{{extra}}]}""";

    private static string Process(string id, string name, string subject, string body, string use = "\"use\":\"lifecycle\",") => $$"""
        {"kind":"process","id":"{{id}}","name":"{{name}}","package":"{{Package}}",{{use}}
         "subjects":[{"id":"{{subject}}","entity":"{{Drawing}}"}],{{body}}}
        """;

    // InWork moves to itself on edit (modify), duplicate (copy) and revise (revise, at the minor level).
    private static string MainLife(string points = "") => Process(Main, "Main", "01JRWB00000000000000000001", $$"""
        "events":[{"id":"{{Edit}}","name":"edit","verb":"modify"},{"id":"{{Duplicate}}","name":"duplicate","verb":"copy"},
          {"id":"{{ReviseEvent}}","name":"revise","verb":"revise"}],
        "states":[{"id":"01JRWS00000000000000000001","name":"InWork"}],"initial":"01JRWS00000000000000000001",
        "transitions":[{"id":"01JRWT00000000000000000001","source":"01JRWS00000000000000000001","event":"{{Edit}}","targets":["01JRWS00000000000000000001"]},
          {"id":"01JRWT00000000000000000002","source":"01JRWS00000000000000000001","event":"{{Duplicate}}","targets":["01JRWS00000000000000000001"]},
          {"id":"01JRWT00000000000000000003","source":"01JRWS00000000000000000001","event":"{{ReviseEvent}}","targets":["01JRWS00000000000000000001"],"nextRevision":2}]
        {{points}}
        """);

    // Delivery denies modify and refuses copy while Shipped: verbs on the object, which other lifecycles answer to.
    private static readonly string DeliveryLife = Process(Delivery, "Delivery", "01JRWB00000000000000000002", $$"""
        "states":[{"id":"{{Waiting}}","name":"Waiting"},{"id":"{{Shipped}}","name":"Shipped"}],"initial":"{{Waiting}}",
        "rights":[{"id":"01JRWZ00000000000000000001","name":"NoModifyShipped","access":"deny","verbs":["modify"],"states":["{{Shipped}}"]}],
        "triggerPoints":[{"id":"01JRWQ00000000000000000011","name":"NotWhileShipped","type":"check","verbs":["copy"],"states":["{{Shipped}}"],"condition":{{T("false")}} }]
        """);

    // Each event of Rank has two candidates from R0, each refused for its own reason; the first refusal listed is not the one that should win.
    private static readonly string RankLife = Process(Rank, "Rank", "01JRWB00000000000000000003", $$"""
        "events":[{"id":"01JRWX00000000000000000041","name":"checkFirst"},{"id":"01JRWX00000000000000000042","name":"gateSecond"},
          {"id":"01JRWX00000000000000000043","name":"guardSecond"}],
        "guards":[{"id":"01JRWG00000000000000000041","name":"never","condition":{{T("false")}} }],
        "states":[{"id":"01JRWS00000000000000000041","name":"R0"},{"id":"01JRWS00000000000000000042","name":"R1"},{"id":"01JRWS00000000000000000043","name":"R2"},
          {"id":"01JRWS00000000000000000044","name":"R3"},{"id":"01JRWS00000000000000000045","name":"R4"}],
        "initial":"01JRWS00000000000000000041",
        "transitions":[
          {"id":"01JRWT00000000000000000041","source":"01JRWS00000000000000000041","event":"01JRWX00000000000000000041","verb":"promote","targets":["01JRWS00000000000000000042"]},
          {"id":"01JRWT00000000000000000042","source":"01JRWS00000000000000000041","event":"01JRWX00000000000000000041","verb":"revise","targets":["01JRWS00000000000000000043"]},
          {"id":"01JRWT00000000000000000043","source":"01JRWS00000000000000000041","event":"01JRWX00000000000000000042","targets":["01JRWS00000000000000000043"],"nextRevision":1},
          {"id":"01JRWT00000000000000000044","source":"01JRWS00000000000000000041","event":"01JRWX00000000000000000042","targets":["01JRWS00000000000000000044"],
           "gate":{"id":"01JRWK00000000000000000041","name":"sign","required":1,"signers":["{{Approver}}"],
           "meanings":[{"id":"01JRWK00000000000000000042","name":"approved"}]} },
          {"id":"01JRWT00000000000000000045","source":"01JRWS00000000000000000041","event":"01JRWX00000000000000000043","verb":"revise","targets":["01JRWS00000000000000000043"]},
          {"id":"01JRWT00000000000000000046","source":"01JRWS00000000000000000041","event":"01JRWX00000000000000000043","guard":"01JRWG00000000000000000041","targets":["01JRWS00000000000000000045"]}],
        "triggerPoints":[{"id":"{{NoPromote}}","name":"NoPromote","type":"check","verbs":["promote"],"transitions":["01JRWT00000000000000000041"],"condition":{{T("false")}} }]
        """);

    // A timer that gives the next revision.
    private static readonly string TimedLife = Process(Timed, "Timed", "01JRWB00000000000000000007", """
        "states":[{"id":"01JRWS00000000000000000051","name":"T0"},{"id":"01JRWS00000000000000000052","name":"T1"}],"initial":"01JRWS00000000000000000051",
        "transitions":[{"id":"01JRWT00000000000000000051","source":"01JRWS00000000000000000051","trigger":"after","after":"P1D","targets":["01JRWS00000000000000000052"],"nextRevision":1}]
        """);

    // Two human tasks in K0: prepare's first move is guarded off and its second gives the next revision; review's first is refused by a check
    // and its second revises.
    private static readonly string TaskedLife = Process(Tasked, "Tasked", "01JRWB00000000000000000008", $$"""
        "guards":[{"id":"01JRWG00000000000000000061","name":"never","condition":{{T("false")}} }],
        "states":[{"id":"01JRWS00000000000000000061","name":"K0","invoke":[{"id":"01JRWF00000000000000000061","name":"prepare","type":"human-task"},
            {"id":"01JRWF00000000000000000062","name":"review","type":"human-task"}]},
          {"id":"01JRWS00000000000000000062","name":"K1"},{"id":"01JRWS00000000000000000063","name":"K2"},{"id":"01JRWS00000000000000000064","name":"K3"}],
        "initial":"01JRWS00000000000000000061",
        "transitions":[
          {"id":"01JRWT00000000000000000061","source":"01JRWS00000000000000000061","trigger":"invoke-done","invoke":"01JRWF00000000000000000061","guard":"01JRWG00000000000000000061","targets":["01JRWS00000000000000000062"]},
          {"id":"01JRWT00000000000000000062","source":"01JRWS00000000000000000061","trigger":"invoke-done","invoke":"01JRWF00000000000000000061","targets":["01JRWS00000000000000000063"],"nextRevision":1},
          {"id":"01JRWT00000000000000000063","source":"01JRWS00000000000000000061","trigger":"invoke-done","invoke":"01JRWF00000000000000000062","verb":"promote","targets":["01JRWS00000000000000000064"]},
          {"id":"01JRWT00000000000000000064","source":"01JRWS00000000000000000061","trigger":"invoke-done","invoke":"01JRWF00000000000000000062","verb":"revise","targets":["01JRWS00000000000000000063"]}],
        "triggerPoints":[{"id":"{{NoComplete}}","name":"NoComplete","type":"check","verbs":["promote"],"transitions":["01JRWT00000000000000000063"],"condition":{{T("false")}} }]
        """);

    // Two regions that move together on go (a label at level 2 and one at level 1) and on back (a label at level 2 and a revise).
    private static readonly string ParaLife = Process(Para, "Para", "01JRWB00000000000000000004", """
        "events":[{"id":"01JRWX00000000000000000021","name":"go"},{"id":"01JRWX00000000000000000022","name":"back"}],
        "states":[{"id":"01JRWS00000000000000000021","name":"Split","type":"parallel","states":[
            {"id":"01JRWS00000000000000000022","name":"RegA","type":"compound","initial":"01JRWS00000000000000000023","states":[
              {"id":"01JRWS00000000000000000023","name":"A1"},{"id":"01JRWS00000000000000000024","name":"A2"}]},
            {"id":"01JRWS00000000000000000025","name":"RegB","type":"compound","initial":"01JRWS00000000000000000026","states":[
              {"id":"01JRWS00000000000000000026","name":"B1"},{"id":"01JRWS00000000000000000027","name":"B2"}]}]}],
        "initial":"01JRWS00000000000000000021",
        "transitions":[
          {"id":"01JRWT00000000000000000021","source":"01JRWS00000000000000000023","event":"01JRWX00000000000000000021","targets":["01JRWS00000000000000000024"],"nextRevision":2},
          {"id":"01JRWT00000000000000000022","source":"01JRWS00000000000000000026","event":"01JRWX00000000000000000021","targets":["01JRWS00000000000000000027"],"nextRevision":1},
          {"id":"01JRWT00000000000000000023","source":"01JRWS00000000000000000024","event":"01JRWX00000000000000000022","targets":["01JRWS00000000000000000023"],"nextRevision":2},
          {"id":"01JRWT00000000000000000024","source":"01JRWS00000000000000000027","event":"01JRWX00000000000000000022","verb":"revise","targets":["01JRWS00000000000000000026"]}]
        """);

    // Host invokes Child, whose own event revises.
    private static readonly string HostLife = Process(Host, "Host", "01JRWB00000000000000000005", $$"""
        "states":[{"id":"01JRWS00000000000000000033","name":"H0","invoke":[{"id":"01JRWF00000000000000000031","name":"child","type":"process","process":"{{Child}}"}]}],
        "initial":"01JRWS00000000000000000033"
        """);

    private static readonly string ChildProcess = Process(Child, "Child", "01JRWB00000000000000000006", """
        "events":[{"id":"01JRWX00000000000000000031","name":"childRevise","verb":"revise"}],
        "states":[{"id":"01JRWS00000000000000000031","name":"C1"},{"id":"01JRWS00000000000000000032","name":"C2"}],"initial":"01JRWS00000000000000000031",
        "transitions":[{"id":"01JRWT00000000000000000031","source":"01JRWS00000000000000000031","event":"01JRWX00000000000000000031","targets":["01JRWS00000000000000000032"]}]
        """, use: "");

    // F0 moves to itself on go (no verb), whose transition revises, and on back (verb revise), whose transition promotes; a before action stamps
    // the trail on both verbs.
    private const string Frost = "01JRWP00000000000000000009";
    private static readonly string FrostLife = Process(Frost, "Frost", "01JRWB00000000000000000009", $$"""
        "events":[{"id":"01JRWX00000000000000000071","name":"go"},{"id":"01JRWX00000000000000000072","name":"back","verb":"revise"}],
        "states":[{"id":"01JRWS00000000000000000071","name":"F0"}],"initial":"01JRWS00000000000000000071",
        "transitions":[
          {"id":"01JRWT00000000000000000071","source":"01JRWS00000000000000000071","event":"01JRWX00000000000000000071","verb":"revise","targets":["01JRWS00000000000000000071"]},
          {"id":"01JRWT00000000000000000072","source":"01JRWS00000000000000000071","event":"01JRWX00000000000000000072","verb":"promote","targets":["01JRWS00000000000000000071"]}],
        "triggerPoints":[{"id":"01JRWQ00000000000000000071","name":"Stamp","type":"action","timing":"before","verbs":["revise","promote"],
          "sets":[{"id":"01JRWV00000000000000000071","attribute":"{{Trail}}","value":{{T("'x'")}} }]}]
        """);

    // A revise move behind a gate that needs two signatures.
    private const string Gated = "01JRWP00000000000000000010";
    private static readonly string GatedLife = Process(Gated, "Gated", "01JRWB00000000000000000010", $$"""
        "events":[{"id":"01JRWX00000000000000000081","name":"approve"}],
        "states":[{"id":"01JRWS00000000000000000081","name":"G0"},{"id":"01JRWS00000000000000000082","name":"G1"}],"initial":"01JRWS00000000000000000081",
        "transitions":[{"id":"01JRWT00000000000000000081","source":"01JRWS00000000000000000081","event":"01JRWX00000000000000000081","verb":"revise",
          "targets":["01JRWS00000000000000000082"],"gate":{"id":"01JRWK00000000000000000081","name":"sign","required":2,"signers":["{{Approver}}"],
          "meanings":[{"id":"01JRWK00000000000000000082","name":"approved"}]} }]
        """);

    private static string Attribute(string id, string name, string extra = "") => $$"""{"id":"{{id}}","name":"{{name}}","type":"string","length":40{{extra}}}""";

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? block, params string[] processes)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/entities/drawing.json", $$"""
            {"kind":"entity","id":"{{Drawing}}","name":"Drawing","package":"{{Package}}","key":{"attributes":["01JRWA00000000000000000001"]},
             "attributes":[{"id":"01JRWA00000000000000000001","name":"id","type":"uuid"},{{Attribute(Rev, "revision")}},{{Attribute(ApprovedBy, "approvedBy")}},
               {{Attribute(Trail, "trail")}},{"id":"{{CheckedOn}}","name":"checkedOn","type":"uuid","defaultExpression":"new-uuid"},
               {{Attribute(Owner, "owner", ",\"required\":true")}},{{Attribute(Status, "status", ",\"default\":\"draft\"")}}]
             {{(block is null ? "" : ",\"revisions\":" + block)}}}
            """);
        h.Write("model/entities/detail.json", $$"""
            {"kind":"entity","id":"{{Detail}}","name":"Detail","package":"{{Package}}","base":"{{Drawing}}","attributes":[]
             {{(block is null ? "" : $$""","revisions":{"attributes":[{"attribute":"{{Trail}}","onRevise":"clear"}]}""")}}}
            """);
        h.Write("model/entities/sheet.json", $$"""
            {"kind":"entity","id":"{{Sheet}}","name":"Sheet","package":"{{Package}}","key":{"attributes":["01JRWA00000000000000000008"]},
             "attributes":[{"id":"01JRWA00000000000000000008","name":"id","type":"uuid"}]}
            """);
        h.Write("model/entities/tag.json", $$"""
            {"kind":"entity","id":"{{Tag}}","name":"Tag","package":"{{Package}}","key":{"attributes":["01JRWA00000000000000000009"]},
             "attributes":[{"id":"01JRWA00000000000000000009","name":"id","type":"uuid"}]}
            """);
        h.Write("model/relations/drawing-sheets.json", $$"""
            {"kind":"relation","id":"01JRWN00000000000000000001","name":"DrawingSheets","package":"{{Package}}",
             "ends":[{"id":"01JRWD00000000000000000001","entity":"{{Drawing}}","role":"drawings"},{"id":"{{SheetsEnd}}","entity":"{{Sheet}}","role":"sheets"}]}
            """);
        h.Write("model/relations/drawing-tags.json", $$"""
            {"kind":"relation","id":"01JRWN00000000000000000002","name":"DrawingTags","package":"{{Package}}",
             "ends":[{"id":"01JRWD00000000000000000003","entity":"{{Drawing}}","role":"tagged"},{"id":"{{TagsEnd}}","entity":"{{Tag}}","role":"tags"}]}
            """);
        h.Write("model/actors/approver.json", $$"""{"kind":"actor","id":"{{Approver}}","name":"Approver","type":"role"}""");
        for (var i = 0; i < processes.Length; i++)
            h.Write($"model/processes/p{i}.json", processes[i]);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (h, store);
    }

    private static JsonElement[] Objects(string extra = "", string entity = Drawing, string values = "") =>
        [J($$"""
            {"id":"d1","name":"drawing","entity":"{{entity}}","values":{"{{ApprovedBy}}":"kim","{{Trail}}":"-","{{Owner}}":"lee","{{Status}}":"final"{{values}}}{{extra}}}
            """)];

    private static async Task<IReadOnlyList<StepTrace>> SimulateAsync(ModelStore store, string process, JsonElement[] steps, JsonElement[]? objects = null)
    {
        var call = await store.SimulateProcessAsync(process, new SimulateRequest { Steps = steps, Objects = objects ?? Objects(), Subject = "d1" }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!.Trace;
    }

    private static JsonElement J(string text) => RevisionRunTests.Json(text);

    private static string? Value(StepTrace trace, string id) =>
        trace.ValuesChanged is { } values && values.TryGetValue(id, out var v) ? (v.ValueKind == JsonValueKind.Null ? null : v.GetString()) : "(unchanged)";

    // Decision 22: a check outranks a revision refusal, which outranks a gate and a guard, whatever order the candidates come in; refusedBy is
    // the entity whose block holds the sequence, so a derived type's refusal names its base.
    [Fact]
    public async Task A_revision_refusal_ranks_below_a_check_and_above_a_gate_and_a_guard()
    {
        var (h, store) = await OpenAsync(List, RankLife);
        using var _ = h;
        await using var __ = store;
        var atEnd = Objects(",\"revision\":\"Q\"");
        var t = await SimulateAsync(store, Rank,
            [Event("01JRWX00000000000000000041"), Event("01JRWX00000000000000000042"), Event("01JRWX00000000000000000043")], atEnd);
        Assert.Equal((false, Refusals.Check, NoPromote), (t[1].Accepted, t[1].Refusal, t[1].RefusedBy));
        Assert.Equal((false, Refusals.Revision, Drawing), (t[2].Accepted, t[2].Refusal, t[2].RefusedBy));
        Assert.Equal((false, Refusals.Revision, Drawing), (t[3].Accepted, t[3].Refusal, t[3].RefusedBy));
        Assert.All(t, trace => Assert.Null(trace.Revision));

        var detail = await SimulateAsync(store, Rank, [Event("01JRWX00000000000000000043")], Objects(",\"revision\":\"Q\"", Detail));
        Assert.Equal((false, Refusals.Revision, Drawing), (detail[1].Accepted, detail[1].Refusal, detail[1].RefusedBy));
    }

    // Row 7b in a time step: a timer whose revision the sequence can't give doesn't fire, with a note; one it can give moves the revision.
    [Fact]
    public async Task A_timer_the_sequence_cannot_label_does_not_fire_and_one_it_can_moves_the_revision()
    {
        var (h, store) = await OpenAsync(List, TimedLife);
        using var _ = h;
        await using var __ = store;
        JsonElement[] wait = [J("""{"input":"time","after":"P2D"}""")];
        var stuck = await SimulateAsync(store, Timed, wait, Objects(",\"revision\":\"Q\""));
        Assert.Equal(["01JRWS00000000000000000051"], stuck[1].Configuration);
        Assert.Null(stuck[1].Revision);
        Assert.Contains("can't give the revision of the automatic move", Notes(stuck[1]), StringComparison.Ordinal);

        var moved = await SimulateAsync(store, Timed, wait, Objects(",\"revision\":\"P\""));
        Assert.Equal(["01JRWS00000000000000000052"], moved[1].Configuration);
        Assert.Equal(("P", "Q", 1), Moved(moved[1]));
    }

    // Row 7b in SelectAt, at a finished task: a revision refusal outranks a guard, and a check outranks it.
    [Fact]
    public async Task A_finished_task_ranks_a_revision_refusal_above_a_guard_and_below_a_check()
    {
        var (h, store) = await OpenAsync(List, TaskedLife);
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store, Tasked,
        [
            J("""{"input":"invoke-done","invoke":"01JRWF00000000000000000061"}"""),
            J("""{"input":"invoke-done","invoke":"01JRWF00000000000000000062"}"""),
        ], Objects(",\"revision\":\"Q\""));
        Assert.Equal((false, Refusals.Revision, Drawing), (t[1].Accepted, t[1].Refusal, t[1].RefusedBy));
        Assert.Equal((false, Refusals.Check, NoComplete), (t[2].Accepted, t[2].Refusal, t[2].RefusedBy));
    }

    private static string Points => $$"""
        ,"triggerPoints":[
          {"id":"01JRWQ00000000000000000001","name":"Stamp","type":"action","timing":"before","verbs":["revise"],
           "sets":[{"id":"01JRWV00000000000000000001","attribute":"{{Trail}}",
             "value":{{T("coalesce(old.revision, '-') || '>' || coalesce(subject.revision, '-') || '/' || coalesce(subject.status, '-')")}} }]},
          {"id":"01JRWQ00000000000000000002","name":"AutoApprove","type":"action","timing":"before","verbs":["revise"],
           "sets":[{"id":"01JRWV00000000000000000002","attribute":"{{ApprovedBy}}","value":{{T("'auto'")}} }]},
          {"id":"{{Carry}}","name":"Carry","type":"replace","verbs":["revise"],
           "sets":[{"id":"01JRWV00000000000000000003","attribute":"{{Owner}}","value":{{T("'by ' || coalesce(subject.revision, '-')")}} }]}]
        """;

    // Decision 16 and section 9: the scratch copy of a revise holds the next revision and the reset values, a before action's value wins over a
    // reset, and a replace's sets apply on top of what is carried over; on a revise move too, but for the replace. Without a block, the replace
    // keeps today's handling: the trace shows its sets, and nothing is written.
    [Fact]
    public async Task Before_actions_read_the_new_revision_and_win_over_resets_and_a_replace_applies_on_top()
    {
        var (h, store) = await OpenAsync(Block(), MainLife(Points));
        using var _ = h;
        await using var __ = store;
        var objects = Objects(values: $$""","{{Rev}}":"A" """);
        var step = await SimulateAsync(store, Main, [Verb("revise")], objects);
        Assert.Equal(("A", "B", 1), Moved(step[1]));
        Assert.Equal("A>B/draft", Value(step[1], Trail));
        Assert.Equal("auto", Value(step[1], ApprovedBy));
        Assert.Equal("draft", Value(step[1], Status));
        Assert.Equal("by B", Value(step[1], Owner));
        Assert.False(step[1].ValuesChanged!.ContainsKey(Rev));
        Assert.Contains(step[1].TriggerPoints!, r => r.TriggerPoint == Carry && r.Outcome == TriggerOutcome.Replaced);

        var move = await SimulateAsync(store, Main, [Event(ReviseEvent)], objects);
        Assert.Equal(("A", "A.1", 2), Moved(move[1]));
        Assert.Equal("A>A.1/draft", Value(move[1], Trail));
        Assert.Equal("auto", Value(move[1], ApprovedBy));
        Assert.Equal("draft", Value(move[1], Status));
        Assert.Equal("(unchanged)", Value(move[1], Owner));
        Assert.False(move[1].ValuesChanged!.ContainsKey(Rev));

        var (h2, plain) = await OpenAsync(null, MainLife(Points));
        using var ___ = h2;
        await using var ____ = plain;
        var today = await SimulateAsync(plain, Main, [Verb("revise")], objects);
        Assert.Null(today[1].Revision);
        Assert.Null(today[1].ValuesChanged);
        var replaced = Assert.Single(today[1].TriggerPoints!, r => r.TriggerPoint == Carry);
        Assert.Equal(TriggerOutcome.Replaced, replaced.Outcome);
        Assert.Equal("by A", replaced.Sets![Owner].GetString());
        Assert.Null(today[1].RevisionAfter);
    }

    // Section 9's other lifecycles: Delivery's rights and trigger points read the states a revise gave it, over what the run worked out (and
    // cached) before it: started over, kept, or sent to a named state.
    [Theory]
    [InlineData("", Shipped, new[] { false, false, true, true, true })]
    [InlineData("keep", Shipped, new[] { false, false, true, false, false })]
    [InlineData(Shipped, Waiting, new[] { true, true, true, false, false })]
    public async Task Other_lifecycles_rights_and_trigger_points_read_the_states_a_revise_gave_them(string onRevise, string given, bool[] accepted)
    {
        var lifecycles = onRevise.Length == 0 ? "" : $$""","lifecycles":[{"lifecycle":"{{Delivery}}","onRevise":"{{onRevise}}"}]""";
        var block = $$"""{{{Letters}},"boundAttribute":"{{Rev}}"{{lifecycles}}}""";
        var (h, store) = await OpenAsync(block, MainLife(), DeliveryLife);
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store, Main, [Event(Edit), Event(Duplicate), Verb("revise"), Event(Edit), Event(Duplicate)],
            Objects($$""","states":["{{given}}"]"""));
        Assert.Equal(accepted, t.Skip(1).Select(x => x.Accepted).ToArray());
        Assert.Equal(("A", "B", 1), Moved(t[3]));
        foreach (var refused in t.Skip(1).Where(x => !x.Accepted))
            Assert.Equal(refused.Index is 0 or 3 ? Refusals.Right : Refusals.Check, refused.Refusal);
    }

    // Decision 19: transitions taken together in parallel regions move the revision once, at the most significant level any names, as a revise
    // when any of them is one, with a note that they named different levels.
    [Fact]
    public async Task Parallel_moves_at_different_levels_move_the_revision_once_at_the_most_significant()
    {
        var (h, store) = await OpenAsync(Block(), ParaLife);
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store, Para, [Event("01JRWX00000000000000000021"), Event("01JRWX00000000000000000022")]);
        Assert.Equal(("A", "B", 1), Moved(t[1]));
        Assert.Contains("named different revision levels, so the revision moved once, at level 1", Notes(t[1]), StringComparison.Ordinal);
        Assert.Null(t[1].ValuesChanged); // labels carry nothing over
        Assert.Equal(("B", "C", 1), Moved(t[2]));
        Assert.Contains("named different revision levels", Notes(t[2]), StringComparison.Ordinal);
        Assert.Null(Value(t[2], ApprovedBy)); // a revise resets
        Assert.Contains("01JRWS00000000000000000023", t[2].Configuration);
        Assert.Contains("01JRWS00000000000000000026", t[2].Configuration);
    }

    // Decision 18: a sub-process sees no object, so its revise move moves nothing.
    [Fact]
    public async Task A_sub_process_revise_move_moves_no_revision()
    {
        var (h, store) = await OpenAsync(Block(), HostLife, ChildProcess);
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store, Host, [Event("01JRWX00000000000000000031")]);
        Assert.True(t[1].Accepted);
        Assert.Null(t[1].Revision);
        Assert.Equal("A", t[1].RevisionAfter);
        Assert.Equal("(unchanged)", Value(t[1], ApprovedBy));
        Assert.DoesNotContain("revised", Notes(t[1]), StringComparison.Ordinal);
    }

    // Decisions 28 and 34: the revise's notes say what the type's links would do, that a reset with a default expression is left empty, and
    // that a required value was emptied.
    [Fact]
    public async Task A_revise_notes_the_links_a_default_expression_and_an_emptied_required_value()
    {
        var block = Block($$""",{"attribute":"{{CheckedOn}}","onRevise":"reset"},{"attribute":"{{Owner}}","onRevise":"clear"}""")[..^1]
            + $$""","links":[{"end":"{{SheetsEnd}}","onRevise":"duplicate"}]}""";
        var (h, store) = await OpenAsync(block, MainLife());
        using var _ = h;
        await using var __ = store;
        JsonElement[] objects =
        [
            .. Objects($$""","links":[{"end":"{{SheetsEnd}}","object":"s1"},{"end":"{{TagsEnd}}","object":"t1"}]"""),
            J($$"""{"id":"s1","name":"sheet","entity":"{{Sheet}}"}"""), J($$"""{"id":"t1","name":"tag","entity":"{{Tag}}"}"""),
        ];
        var t = await SimulateAsync(store, Main, [Verb("revise")], objects);
        var notes = Notes(t[1]);
        Assert.Contains("The run leaves the subject's links as they are; the type says its links would 'sheets' be duplicated on the new revision, 'tags' not said.",
            notes, StringComparison.Ordinal);
        Assert.Contains("Attribute 'checkedOn' resets to the value of its default expression, which the team's system fills, so the run leaves it empty.", notes,
            StringComparison.Ordinal);
        Assert.Contains("Required attribute 'owner' is empty on the new revision", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("Required attribute 'status'", notes, StringComparison.Ordinal);
        Assert.Null(Value(t[1], Owner));
        Assert.Equal("draft", Value(t[1], Status));
    }

    // Section 9: a revise move counts as thawing for the frozen notes by the move's verb, not its event's. A move whose transition revises under
    // an event with no verb applies its before sets with no frozen note; a move whose transition promotes under an event that names revise notes
    // that the frozen object's sets applied.
    [Fact]
    public async Task The_frozen_note_follows_the_verb_of_the_move_not_of_its_event()
    {
        var (h, store) = await OpenAsync(Block(), FrostLife);
        using var _ = h;
        await using var __ = store;
        const string Frozen = "The object is frozen, but a trigger point's sets applied";
        var revised = await SimulateAsync(store, Frost, [Event("01JRWX00000000000000000071")], Objects(",\"frozen\":true"));
        Assert.Equal(("A", "B", 1), Moved(revised[1]));
        Assert.Equal("x", Value(revised[1], Trail));
        Assert.DoesNotContain(Frozen, Notes(revised[1]), StringComparison.Ordinal);

        var promoted = await SimulateAsync(store, Frost, [Event("01JRWX00000000000000000072")], Objects(",\"frozen\":true"));
        Assert.True(promoted[1].Accepted);
        Assert.Null(promoted[1].Revision);
        Assert.Equal("x", Value(promoted[1], Trail));
        Assert.Contains(Frozen, Notes(promoted[1]), StringComparison.Ordinal);
    }

    // Decision 40: a signature that leaves a gated revise move waiting takes no move, so the step's chosen name is not said to be ignored; the
    // signature that completes the gate gives it.
    [Fact]
    public async Task A_signature_that_leaves_a_gated_revise_waiting_does_not_say_the_chosen_name_was_ignored()
    {
        var (h, store) = await OpenAsync(Block(), GatedLife);
        using var _ = h;
        await using var __ = store;
        var t = await SimulateAsync(store, Gated,
        [
            Event("01JRWX00000000000000000081", $$""","actor":"{{Approver}}","signer":"kim","revision":"C" """),
            Event("01JRWX00000000000000000081", $$""","actor":"{{Approver}}","signer":"lee","revision":"C" """),
        ]);
        Assert.True(t[1].Accepted);
        Assert.Null(t[1].Revision);
        Assert.DoesNotContain("was ignored", Notes(t[1]), StringComparison.Ordinal);
        Assert.Equal(("A", "C", null), Moved(t[2]));
        Assert.DoesNotContain("was ignored", Notes(t[2]), StringComparison.Ordinal);
    }

    // Decision 38: with no subject object a revise moves no revision; the note's reason fits the process: a lifecycle that governs a type with
    // revisions can't know which type's apply, and an orchestration has none.
    [Fact]
    public async Task The_no_object_note_gives_a_reason_that_fits_the_process()
    {
        var (h, store) = await OpenAsync(Block(), MainLife(), ChildProcess);
        using var _ = h;
        await using var __ = store;
        async Task<string> NoteOf(string process, string ev)
        {
            var call = await store.SimulateProcessAsync(process, new SimulateRequest { Steps = [Event(ev)] }, Ct);
            Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
            Assert.Null(call.Value!.Trace[1].Revision);
            return Notes(call.Value.Trace[1]);
        }

        Assert.Contains("the move gave no revision: one lifecycle may govern types with different revisions", await NoteOf(Main, ReviseEvent), StringComparison.Ordinal);
        var orchestration = await NoteOf(Child, "01JRWX00000000000000000031");
        Assert.Contains("the move gave no revision: only an object whose type has revisions has one.", orchestration, StringComparison.Ordinal);
        Assert.DoesNotContain("one lifecycle may govern", orchestration, StringComparison.Ordinal);
    }

    private static Scenario Scenario(string step) => JsonSerializer.Deserialize<Scenario>($$"""
        {"kind":"scenario","id":"01JRWY00000000000000000001","name":"drawing","process":"{{Main}}","subject":"d1",
         "objects":[{"id":"d1","name":"drawing","entity":"{{Drawing}}","values":{"{{Owner}}":"lee"},"links":[{"end":"{{SheetsEnd}}","object":"s1"}]},
           {"id":"s1","name":"sheet","entity":"{{Sheet}}"}],
         "steps":[{{step[..^1]}},"id":"01JRWY00000000000000000002"}]}
        """, EngineJson.Options)!;

    // MQ9305: revise with end on a subject whose type has revisions; a step's revision and an expect.revision on one whose type has none; and
    // revise with end keeps today's handling there.
    [Fact]
    public async Task Revise_with_end_and_revision_keys_on_the_wrong_subject_are_MQ9305()
    {
        const string ReviseLink = $$"""{"input":"verb","verb":"revise","end":"{{SheetsEnd}}","object":"s1"}""";
        var (h, store) = await OpenAsync(Block(), MainLife());
        using var _ = h;
        await using var __ = store;
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        Assert.Contains("revise acts on objects, and the subject's entity 'Drawing' has revisions",
            ScenarioReplayer.Replay(Scenario(ReviseLink), runtime)!.Diagnostics.FirstOrDefault(d => d.Rule == "MQ9305")?.Message, StringComparison.Ordinal);

        var (h2, plain) = await OpenAsync(null, MainLife());
        using var ___ = h2;
        await using var ____ = plain;
        using var plainRuntime = new ProcessRuntime(plain.Current!, 1, Ct);
        string? Problem(string step) =>
            ScenarioReplayer.Replay(Scenario(step), plainRuntime)!.Diagnostics.FirstOrDefault(d => d.Rule == "MQ9305")?.Message;
        Assert.Contains("the step gives a revision, but the subject's entity 'Drawing' has no revisions", Problem("""{"input":"verb","verb":"revise","revision":1}"""),
            StringComparison.Ordinal);
        Assert.Contains("the step gives a revision, but", Problem($$"""{"event":"{{ReviseEvent}}","revision":"B"}"""), StringComparison.Ordinal);
        Assert.Contains("expect gives a revision, but the subject's entity 'Drawing' has no revisions", Problem($$$"""{"event":"{{{Edit}}}","expect":{"revision":"A"}}"""),
            StringComparison.Ordinal);
        Assert.Null(Problem(ReviseLink));
    }
}
