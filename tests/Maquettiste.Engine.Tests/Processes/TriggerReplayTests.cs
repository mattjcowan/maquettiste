using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Trigger points in simulation, recording and replay (trigger-points-design.md decisions 16 to 19 and 24 to 28, section 8; section 15's
/// <c>TriggerReplayTests</c>), over an invented model beside the <c>process-basics</c> fixture: a document lifecycle with the design's section 5
/// points, an employee governed by HR and Projects, and a contract with a human task. It covers the owner's four behaviours: an event that names
/// delete still moves, a check that refuses an automatic move leaves it untaken, what a point sets is part of the verb, and a check that refuses
/// the completing signature leaves it uncounted.
/// </summary>
public sealed class TriggerReplayTests
{
    private const string Package = "01JPKG00000000000000000001";

    // Entities and attributes.
    private const string Document = "01JTRE00000000000000000001";
    private const string Employee = "01JTRE00000000000000000002";
    private const string Contract = "01JTRE00000000000000000003";
    private const string Title = "01JTRA00000000000000000002";
    private const string Price = "01JTRA00000000000000000003";
    private const string Number = "01JTRA00000000000000000004";
    private const string ArchivedOn = "01JTRA00000000000000000005";
    private const string Stamp = "01JTRA00000000000000000006";
    private const string LastSigner = "01JTRA00000000000000000007";
    private const string Submitted = "01JTRA00000000000000000008";
    private const string Salary = "01JTRA00000000000000000012";
    private const string Label = "01JTRA00000000000000000022";
    private const string Approver = "01JTRR00000000000000000001";

    // Processes, states, events and transitions.
    private const string Documents = "01JTRP00000000000000000001";
    private const string Hr = "01JTRP00000000000000000002";
    private const string Projects = "01JTRP00000000000000000003";
    private const string Approvals = "01JTRP00000000000000000004";
    private const string Draft = "01JTRS00000000000000000001";
    private const string Review = "01JTRS00000000000000000002";
    private const string Parked = "01JTRS00000000000000000003";
    private const string Released = "01JTRS00000000000000000004";
    private const string Drafting = "01JTRS00000000000000000011";
    private const string Done = "01JTRS00000000000000000012";
    private const string Failed = "01JTRS00000000000000000013";
    private const string SubmitEvent = "01JTRX00000000000000000001";
    private const string ReviewEvent = "01JTRX00000000000000000002";
    private const string ApproveEvent = "01JTRX00000000000000000003";
    private const string WithdrawEvent = "01JTRX00000000000000000004";
    private const string Prepare = "01JTRX00000000000000000011";
    private const string ToReview = "01JTRK00000000000000000001";
    private const string ToParked = "01JTRK00000000000000000002";
    private const string Approve = "01JTRK00000000000000000003";
    private const string Withdraw = "01JTRK00000000000000000004";
    private const string Unpark = "01JTRK00000000000000000005"; // always
    private const string Expire = "01JTRK00000000000000000006"; // after P2D
    private const string Gate = "01JTRG00000000000000000001";

    // Trigger points.
    private const string AssignNumber = "01JTRQ00000000000000000001";
    private const string TitleBeforeReview = "01JTRQ00000000000000000002";
    private const string LargePriceDrop = "01JTRQ00000000000000000003";
    private const string StampRelease = "01JTRQ00000000000000000004";
    private const string PublishToPortal = "01JTRQ00000000000000000005";
    private const string Archive = "01JTRQ00000000000000000006";
    private const string FreezeApproval = "01JTRQ00000000000000000007";
    private const string ReleasePrice = "01JTRQ00000000000000000008";
    private const string SignNote = "01JTRQ00000000000000000009";
    private const string DeleteStamp = "01JTRQ00000000000000000010";
    private const string TouchPrice = "01JTRQ00000000000000000011";
    private const string SubmittedStamp = "01JTRQ00000000000000000012";
    private const string DeleteAudit = "01JTRQ00000000000000000013";
    private const string KeepParked = "01JTRQ00000000000000000014";
    private const string SalaryWarning = "01JTRQ00000000000000000021";
    private const string NeedsLabel = "01JTRQ00000000000000000031";

    // A part lifecycle with a parallel state and a compound one, for the states place of a move (decision 10).
    private const string Part = "01JTRE00000000000000000004";
    private const string Parts = "01JTRP00000000000000000005";
    private const string Welding = "01JTRS00000000000000000044";
    private const string Pending = "01JTRS00000000000000000046";
    private const string Cleared = "01JTRS00000000000000000047";
    private const string ClearEvent = "01JTRX00000000000000000021"; // Pending -> Cleared, no verb
    private const string WeldEvent = "01JTRX00000000000000000022"; // Cutting -> Welding, promote
    private const string ParkEvent = "01JTRX00000000000000000023"; // Working -> Hold, no verb
    private const string ReadyEvent = "01JTRX00000000000000000024"; // Waiting -> Ready, no verb
    private const string ResumeEvent = "01JTRX00000000000000000025"; // Hold -> Working, demote
    private const string OnlyPending = "01JTRQ00000000000000000041"; // refuses promote in Pending, another region than the move's
    private const string InBuild = "01JTRQ00000000000000000042"; // warns on promote in Build, the compound parent of Cutting
    private const string InReady = "01JTRQ00000000000000000043"; // warns on demote in Ready, a state inside the compound Hold

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string T(string text) => JsonSerializer.Serialize(new { text });

    private static string Attribute(string id, string name, string type) => $$"""{"id":"{{id}}","name":"{{name}}","type":"{{type}}"}""";

    private static string Entity(string id, string name, string key, params string[] attributes) => $$"""
        {"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Package}}","key":{"attributes":["{{key}}"]},
         "attributes":[{{string.Join(",", [Attribute(key, "id", "uuid"), .. attributes])}}]}
        """;

    private static string Set(string id, string attribute, string value) => $$"""{"id":"{{id}}","attribute":"{{attribute}}","value":{{T(value)}} }""";

    // The invented model; rights adds a deny on modifying the price, which only the actor's own change answers to.
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(bool rights = false, string? archiveWhen = null, bool releaseAudit = false)
    {
        var archiveWhenJson = archiveWhen is null ? "" : $$""","when":{{T(archiveWhen)}}""";
        // A team-code check on the gated move Approve, which only the signature that completes the gate runs.
        var releaseAuditJson = releaseAudit
            ? $$""",{"id":"01JTRQ00000000000000000015","name":"ReleaseAudit","type":"check","verbs":["promote"],"transitions":["{{Approve}}"]}"""
            : "";
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/document.json", Entity(Document, "Document", "01JTRA00000000000000000001",
            Attribute(Title, "title", "string"), Attribute(Price, "price", "int32"), Attribute(Number, "number", "string"),
            Attribute(ArchivedOn, "archivedOn", "string"), Attribute(Stamp, "stamp", "string"), Attribute(LastSigner, "lastSigner", "string"),
            Attribute(Submitted, "submitted", "string")));
        harness.Write("model/entities/employee.json", Entity(Employee, "Employee", "01JTRA00000000000000000011", Attribute(Salary, "salary", "int32")));
        harness.Write("model/entities/contract.json", Entity(Contract, "Contract", "01JTRA00000000000000000021", Attribute(Label, "label", "string")));
        harness.Write("model/actors/approver.json", $$"""{"kind":"actor","id":"{{Approver}}","name":"Approver","type":"role"}""");
        var rightsJson = rights
            ? $$""","rights":[{"id":"01JTRZ00000000000000000001","name":"PriceFixed","access":"deny","verbs":["modify"],"attributes":["{{Price}}"]}]"""
            : "";
        harness.Write("model/processes/documents.json", $$"""
            {"kind":"process","id":"{{Documents}}","name":"Documents","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JTRB00000000000000000001","entity":"{{Document}}"}],
             "events":[{"id":"{{SubmitEvent}}","name":"submit","verb":"promote"},{"id":"{{ReviewEvent}}","name":"review","verb":"promote"},
               {"id":"{{ApproveEvent}}","name":"approve","verb":"promote"},{"id":"{{WithdrawEvent}}","name":"withdraw","verb":"delete"}],
             "guards":[{"id":"01JTRW00000000000000000001","name":"priced","condition":{{T("subject.price >= 0")}} }],
             "states":[{"id":"{{Draft}}","name":"Draft"},{"id":"{{Review}}","name":"Review"},{"id":"{{Parked}}","name":"Parked"},
               {"id":"{{Released}}","name":"Released"}],
             "initial":"{{Draft}}",
             "transitions":[
               {"id":"{{ToReview}}","source":"{{Draft}}","event":"{{SubmitEvent}}","guard":"01JTRW00000000000000000001","targets":["{{Review}}"]},
               {"id":"{{ToParked}}","source":"{{Draft}}","event":"{{SubmitEvent}}","targets":["{{Parked}}"]},
               {"id":"01JTRK00000000000000000007","source":"{{Draft}}","event":"{{ReviewEvent}}","targets":["{{Review}}"]},
               {"id":"{{Approve}}","source":"{{Review}}","event":"{{ApproveEvent}}","targets":["{{Released}}"],
                "gate":{"id":"{{Gate}}","name":"release","required":2,"signers":["{{Approver}}"],"meanings":[{"id":"01JTRG00000000000000000002","name":"approved"}]} },
               {"id":"{{Withdraw}}","source":"{{Released}}","event":"{{WithdrawEvent}}","targets":["{{Draft}}"]},
               {"id":"{{Unpark}}","source":"{{Parked}}","trigger":"always","verb":"promote","targets":["{{Review}}"]},
               {"id":"{{Expire}}","source":"{{Parked}}","trigger":"after","after":"P2D","verb":"demote","targets":["{{Draft}}"]}]{{rightsJson}},
             "triggerPoints":[
               {"id":"{{AssignNumber}}","name":"AssignNumber","type":"action","timing":"before","verbs":["create"],
                "sets":[{{Set("01JTRV00000000000000000001", Number, "'DOC-1'")}}]},
               {"id":"{{TitleBeforeReview}}","name":"TitleBeforeReview","type":"check","verbs":["promote"],"targets":["{{Review}}"],
                "condition":{{T("coalesce(subject.title, '') <> ''")}},"description":"A document needs a title before it goes to review."},
               {"id":"{{LargePriceDrop}}","name":"LargePriceDrop","type":"check","severity":"warning","verbs":["modify"],"attributes":["{{Price}}"],
                "condition":{{T("coalesce(old.price, 0) = 0 or subject.price >= old.price * 0.9")}} },
               {"id":"{{StampRelease}}","name":"StampRelease","type":"action","timing":"with-change","verbs":["promote"],"targets":["{{Released}}"],
                "sets":[{{Set("01JTRV00000000000000000002", Stamp, "'released'")}}]},
               {"id":"{{PublishToPortal}}","name":"PublishToPortal","type":"action","timing":"after-commit","verbs":["promote"],"targets":["{{Released}}"],"outside":true},
               {"id":"{{Archive}}","name":"Archive","type":"replace","verbs":["delete"]{{archiveWhenJson}},"sets":[{{Set("01JTRV00000000000000000003", ArchivedOn, "'yes'")}}]},
               {"id":"{{FreezeApproval}}","name":"FreezeApproval","type":"check","verbs":["freeze"]},
               {"id":"{{ReleasePrice}}","name":"ReleasePrice","type":"check","verbs":["promote"],"targets":["{{Released}}"],"condition":{{T("subject.price <= 1000")}} },
               {"id":"{{SignNote}}","name":"SignNote","type":"action","timing":"with-change","verbs":["sign"],"sets":[{{Set("01JTRV00000000000000000004", LastSigner, "'signed'")}}]},
               {"id":"{{DeleteStamp}}","name":"DeleteStamp","type":"action","timing":"with-change","verbs":["delete"],"sets":[{{Set("01JTRV00000000000000000005", Stamp, "'gone'")}}]},
               {"id":"{{TouchPrice}}","name":"TouchPrice","type":"action","timing":"before","verbs":["modify"],"attributes":["{{Title}}"],
                "sets":[{{Set("01JTRV00000000000000000006", Price, "50")}}]},
               {"id":"{{SubmittedStamp}}","name":"SubmittedStamp","type":"action","timing":"with-change","verbs":["promote"],"targets":["{{Review}}"],
                "sets":[{{Set("01JTRV00000000000000000007", Submitted, "'yes'")}}]},
               {"id":"{{DeleteAudit}}","name":"DeleteAudit","type":"check","verbs":["delete"]},
               {"id":"{{KeepParked}}","name":"KeepParked","type":"check","verbs":["demote"],"transitions":["{{Expire}}"],"condition":{{T("subject.price < 0")}} }{{releaseAuditJson}}]}
            """);
        harness.Write("model/processes/hr.json", $$"""
            {"kind":"process","id":"{{Hr}}","name":"HR","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JTRB00000000000000000002","entity":"{{Employee}}"}],
             "states":[{"id":"01JTRS00000000000000000021","name":"Active"}],"initial":"01JTRS00000000000000000021",
             "triggerPoints":[{"id":"{{SalaryWarning}}","name":"SalaryWarning","type":"check","severity":"warning","verbs":["modify"],
               "attributes":["{{Salary}}"],"condition":{{T("subject.salary <= 100000")}} }]}
            """);
        harness.Write("model/processes/projects.json", $$"""
            {"kind":"process","id":"{{Projects}}","name":"Projects","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JTRB00000000000000000003","entity":"{{Employee}}"}],
             "states":[{"id":"01JTRS00000000000000000031","name":"Open"}],"initial":"01JTRS00000000000000000031"}
            """);
        harness.Write("model/processes/approvals.json", $$"""
            {"kind":"process","id":"{{Approvals}}","name":"Approvals","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JTRB00000000000000000004","entity":"{{Contract}}"}],
             "states":[{"id":"{{Drafting}}","name":"Drafting","invoke":[{"id":"{{Prepare}}","name":"prepare","type":"human-task","actors":["{{Approver}}"]}]},
               {"id":"{{Done}}","name":"Done","type":"final"},{"id":"{{Failed}}","name":"Failed","type":"final"}],
             "initial":"{{Drafting}}",
             "transitions":[{"id":"01JTRK00000000000000000011","source":"{{Drafting}}","trigger":"invoke-done","invoke":"{{Prepare}}","targets":["{{Done}}"]},
               {"id":"01JTRK00000000000000000012","source":"{{Drafting}}","trigger":"invoke-error","invoke":"{{Prepare}}","targets":["{{Failed}}"]}],
             "triggerPoints":[{"id":"{{NeedsLabel}}","name":"NeedsLabel","type":"check","verbs":["complete"],"condition":{{T("coalesce(subject.label, '') <> ''")}} }]}
            """);
        harness.Write("model/entities/part.json", Entity(Part, "Part", "01JTRA00000000000000000031"));
        harness.Write("model/processes/parts.json", $$"""
            {"kind":"process","id":"{{Parts}}","name":"Parts","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JTRB00000000000000000005","entity":"{{Part}}"}],
             "events":[{"id":"{{ClearEvent}}","name":"clear"},{"id":"{{WeldEvent}}","name":"weld","verb":"promote"},{"id":"{{ParkEvent}}","name":"park"},
               {"id":"{{ReadyEvent}}","name":"ready"},{"id":"{{ResumeEvent}}","name":"resume","verb":"demote"}],
             "states":[{"id":"01JTRS00000000000000000041","name":"Working","type":"parallel","states":[
                 {"id":"01JTRS00000000000000000042","name":"Build","type":"compound","initial":"01JTRS00000000000000000043","states":[
                   {"id":"01JTRS00000000000000000043","name":"Cutting"},{"id":"{{Welding}}","name":"Welding"}]},
                 {"id":"01JTRS00000000000000000045","name":"Paper","type":"compound","initial":"{{Pending}}","states":[
                   {"id":"{{Pending}}","name":"Pending"},{"id":"{{Cleared}}","name":"Cleared"}]}]},
               {"id":"01JTRS00000000000000000048","name":"Hold","type":"compound","initial":"01JTRS00000000000000000049","states":[
                 {"id":"01JTRS00000000000000000049","name":"Waiting"},{"id":"01JTRS00000000000000000050","name":"Ready"}]}],
             "initial":"01JTRS00000000000000000041",
             "transitions":[
               {"id":"01JTRK00000000000000000021","source":"{{Pending}}","event":"{{ClearEvent}}","targets":["{{Cleared}}"]},
               {"id":"01JTRK00000000000000000022","source":"01JTRS00000000000000000043","event":"{{WeldEvent}}","targets":["{{Welding}}"]},
               {"id":"01JTRK00000000000000000023","source":"01JTRS00000000000000000041","event":"{{ParkEvent}}","targets":["01JTRS00000000000000000048"]},
               {"id":"01JTRK00000000000000000024","source":"01JTRS00000000000000000049","event":"{{ReadyEvent}}","targets":["01JTRS00000000000000000050"]},
               {"id":"01JTRK00000000000000000025","source":"01JTRS00000000000000000048","event":"{{ResumeEvent}}","targets":["01JTRS00000000000000000041"]}],
             "triggerPoints":[
               {"id":"{{OnlyPending}}","name":"OnlyPending","type":"check","verbs":["promote"],"states":["{{Pending}}"],"condition":{{T("false")}} },
               {"id":"{{InBuild}}","name":"InBuild","type":"check","severity":"warning","verbs":["promote"],"states":["01JTRS00000000000000000042"],
                "condition":{{T("false")}} },
               {"id":"{{InReady}}","name":"InReady","type":"check","severity":"warning","verbs":["demote"],"states":["01JTRS00000000000000000050"],
                "condition":{{T("false")}} }]}
            """);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement[] Doc(string title = "", int price = 100, bool frozen = false) =>
        [Json($$"""{"id":"o1","name":"doc","entity":"{{Document}}","values":{"{{Title}}":"{{title}}","{{Price}}":{{price}}}{{(frozen ? ",\"frozen\":true" : "")}} }""")];

    private static JsonElement Event(string ev, string extra = "") => Json($$"""{"event":"{{ev}}"{{extra}} }""");

    private static JsonElement Verb(string verb, string extra = "") => Json($$"""{"input":"verb","verb":"{{verb}}"{{extra}} }""");

    private static JsonElement Sign(string signer) => Event(ApproveEvent, $$""","actor":"{{Approver}}","signer":"{{signer}}","meaning":"01JTRG00000000000000000002" """);

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, string process, JsonElement[] steps, JsonElement[]? objects = null)
    {
        var call = await store.SimulateProcessAsync(process, new SimulateRequest { Steps = steps, Objects = objects, Subject = objects is null ? null : "o1" }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!;
    }

    private static IReadOnlyList<(string, TriggerOutcome)> Ran(StepTrace trace) =>
        trace.TriggerPoints?.Select(r => (r.TriggerPoint, r.Outcome)).ToList() ?? [];

    private static string Value(StepTrace trace, string attribute) => trace.ValuesChanged![attribute].ToString();

    [Fact]
    public async Task The_model_validates_without_errors()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var diagnostics = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.Empty(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("model/", StringComparison.Ordinal)
            && !(d.FilePath ?? "").Contains("order", StringComparison.Ordinal)).Select(d => d.Rule + " " + d.Message));
    }

    [Fact]
    public async Task A_refusing_check_skips_its_candidate_and_leaves_an_automatic_move_untaken_without_looping()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        // An empty title: the move to Review is refused by its check, so submit takes the next candidate to Parked; Parked's always move to
        // Review is refused too, so it isn't taken, the step stays accepted, the trace names the check and the run doesn't loop (MQ9507).
        var result = await SimulateAsync(store, Documents, [Event(SubmitEvent), Json("""{"input":"time","after":"P3D"}""")], Doc());
        var submit = result.Trace[1];
        Assert.True(submit.Accepted);
        Assert.Equal([Parked], submit.Configuration);
        Assert.Equal([ToParked], submit.Microsteps.Select(m => m.Transitions.Single()));
        Assert.Null(submit.TriggerPoints);
        Assert.Contains(submit.Notes!, n => n.Contains("'TitleBeforeReview' refused the automatic move", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule == "MQ9507");

        // A timer whose check refuses: its move isn't taken, and time only moves the clock.
        var time = result.Trace[2];
        Assert.True(time.Accepted);
        Assert.Equal([Parked], time.Configuration);
        Assert.Contains(time.Notes!, n => n.Contains("'KeepParked' refused the automatic move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_event_refused_by_a_check_names_it_without_rights()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Documents, [Event(ReviewEvent), Json($$"""{"input":"verb","verb":"modify","values":{"{{Title}}":"Pump"} }"""), Event(ReviewEvent)], Doc());
        var refused = result.Trace[1];
        Assert.False(refused.Accepted);
        Assert.Equal((Refusals.Check, TitleBeforeReview), (refused.Refusal, refused.RefusedBy));
        Assert.Equal([(TitleBeforeReview, TriggerOutcome.Refused)], Ran(refused));

        // Fixed: the move is taken and its with-change point sets the object after the move.
        var taken = result.Trace[3];
        Assert.True(taken.Accepted);
        Assert.Equal([(TitleBeforeReview, TriggerOutcome.Passed), (SubmittedStamp, TriggerOutcome.Ran)], Ran(taken));
        Assert.Equal("yes", Value(taken, Submitted));
    }

    [Fact]
    public async Task Before_actions_fill_the_proposed_values_and_an_accepted_create_writes_them()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Documents,
            [Json($$"""{"input":"verb","verb":"create","values":{"{{Title}}":"Pump"} }"""), Verb("create")], Doc(title: "", price: 0));
        var create = result.Trace[1];
        Assert.True(create.Accepted);
        Assert.Equal([(AssignNumber, TriggerOutcome.Ran)], Ran(create));
        Assert.Equal(("Pump", "DOC-1"), (Value(create, Title), Value(create, Number)));
        Assert.Equal("DOC-1", create.TriggerPoints![0].Sets![Number].GetString());
    }

    [Fact]
    public async Task What_a_point_sets_is_part_of_the_verb_and_never_makes_other_points_match()
    {
        var (h, store) = await OpenAsync(rights: true);
        using var _ = h;
        await using var __ = store;
        // A drop of more than a tenth warns; old. reads the price before the verb.
        var result = await SimulateAsync(store, Documents,
            [Json($$"""{"input":"verb","verb":"modify","values":{"{{Title}}":"Pump"} }""")], Doc(title: "", price: 100));
        // The actor changes the title only: TouchPrice fills the price before the checks, which a deny on modifying the price doesn't
        // answer to, and which doesn't make LargePriceDrop (narrowed to the price) match.
        var modify = result.Trace[1];
        Assert.True(modify.Accepted, modify.Refusal);
        Assert.Equal([(TouchPrice, TriggerOutcome.Ran)], Ran(modify));
        Assert.Null(modify.CheckWarnings);
        Assert.Equal("50", Value(modify, Price));

        var (h2, plain) = await OpenAsync();
        using var ___ = h2;
        await using var ____ = plain;
        var drop = (await SimulateAsync(plain, Documents, [Json($$"""{"input":"verb","verb":"modify","values":{"{{Price}}":80} }""")], Doc(price: 100))).Trace[1];
        Assert.True(drop.Accepted);
        Assert.Equal([LargePriceDrop], drop.CheckWarnings);
        Assert.Equal([(LargePriceDrop, TriggerOutcome.Warned)], Ran(drop));
    }

    [Fact]
    public async Task A_frozen_object_takes_a_moves_trigger_point_sets_with_a_note()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var submit = (await SimulateAsync(store, Documents, [Event(SubmitEvent)], Doc(title: "Pump", frozen: true))).Trace[1];
        Assert.True(submit.Accepted);
        Assert.Equal("yes", Value(submit, Submitted));
        Assert.Contains(submit.Notes!, n => n.Contains("freezing does not stop it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_replace_stands_in_for_a_verb_steps_delete_and_an_event_that_names_delete_still_moves()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Documents,
            [Verb("delete"), Json($$"""{"input":"verb","verb":"modify","values":{"{{Title}}":"Kept"} }""")], Doc(title: "Pump"));
        var delete = result.Trace[1];
        Assert.True(delete.Accepted);
        Assert.Equal([(DeleteAudit, TriggerOutcome.Passed), (Archive, TriggerOutcome.Replaced), (DeleteStamp, TriggerOutcome.Ran)], Ran(delete));
        Assert.True(delete.TriggerPoints![0].Assumed);
        Assert.Equal(("yes", "gone"), (Value(delete, ArchivedOn), Value(delete, Stamp)));
        Assert.True(result.Trace[2].Accepted); // the archived document stays in the run

        // The event withdraw names delete: it still does its move, and the object stays. The replace stands in only for a verb step's effect,
        // so it doesn't run; delete's checks and other actions do, its with-change sets included (the owner's answer to decision 16).
        var withdraw = (await SimulateAsync(store, Documents,
            [Event(SubmitEvent), Sign("ann"), Sign("bob"), Event(WithdrawEvent)], Doc(title: "Pump", price: 500))).Trace[4];
        Assert.True(withdraw.Accepted);
        Assert.Equal([Draft], withdraw.Configuration);
        Assert.Equal([(DeleteAudit, TriggerOutcome.Passed), (DeleteStamp, TriggerOutcome.Ran)], Ran(withdraw));
        Assert.Equal("gone", Value(withdraw, Stamp));
        Assert.Contains(withdraw.Notes!, n => n.Contains("still does its move", StringComparison.Ordinal));
        Assert.DoesNotContain(withdraw.Notes!, n => n.Contains("are skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_verb_steps_delete_whose_replace_does_not_hold_skips_the_with_change_sets_with_one_note()
    {
        // The replace's when doesn't hold, so the built-in delete runs and DeleteStamp has no object to set.
        var (h, store) = await OpenAsync(archiveWhen: "false");
        using var _ = h;
        await using var __ = store;
        var delete = (await SimulateAsync(store, Documents, [Verb("delete")], Doc(title: "Pump"))).Trace[1];
        Assert.True(delete.Accepted);
        Assert.Equal([(DeleteAudit, TriggerOutcome.Passed), (Archive, TriggerOutcome.Skipped), (DeleteStamp, TriggerOutcome.Skipped)], Ran(delete));
        Assert.Single(delete.Notes!, n => n.Contains("are skipped", StringComparison.Ordinal));
        Assert.Contains(delete.Notes!, n => n.Contains("Replace 'Archive' did not run, so the sets of 'DeleteStamp' are skipped: after a built-in delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_moves_states_place_reads_its_source_the_states_inside_it_and_those_holding_it_but_never_another_region()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        JsonElement[] part = [Json($$"""{"id":"o1","name":"part","entity":"{{Part}}","values":{} }""")];

        // weld moves Cutting to Welding in Build. OnlyPending is narrowed to Pending, a state of the other region Paper: it never runs on the
        // move, whether Paper is in Pending or in Cleared. InBuild is narrowed to Build, which holds the source, so it runs.
        foreach (var (steps, at) in new[] { (new[] { Event(WeldEvent) }, 1), (new[] { Event(ClearEvent), Event(WeldEvent) }, 2) })
        {
            var weld = (await SimulateAsync(store, Parts, steps, part)).Trace[at];
            Assert.True(weld.Accepted, weld.Refusal + " " + weld.RefusedBy);
            Assert.Contains(Welding, weld.Configuration);
            Assert.Contains(at == 1 ? Pending : Cleared, weld.Configuration);
            Assert.Equal([(InBuild, TriggerOutcome.Warned)], Ran(weld));
        }

        // resume leaves the compound Hold: InReady, narrowed to Ready, runs when Ready is the active state inside it, and not from Waiting.
        var fromReady = (await SimulateAsync(store, Parts, [Event(ParkEvent), Event(ReadyEvent), Event(ResumeEvent)], part)).Trace[3];
        Assert.True(fromReady.Accepted);
        Assert.Equal([(InReady, TriggerOutcome.Warned)], Ran(fromReady));
        var fromWaiting = (await SimulateAsync(store, Parts, [Event(ParkEvent), Event(ResumeEvent)], part)).Trace[2];
        Assert.True(fromWaiting.Accepted);
        Assert.Null(fromWaiting.TriggerPoints);
    }

    [Fact]
    public async Task A_check_that_refuses_the_completing_signature_leaves_it_uncounted_and_the_next_one_completes()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Documents,
            [Event(SubmitEvent), Sign("ann"), Sign("bob"), Json($$"""{"input":"verb","verb":"modify","values":{"{{Price}}":500} }"""), Sign("bob")],
            Doc(title: "Pump", price: 5000));
        // The first signature doesn't complete the gate: sign's with-change point runs on it.
        var first = result.Trace[2];
        Assert.True(first.Accepted);
        Assert.Equal([(SignNote, TriggerOutcome.Ran)], Ran(first));
        Assert.Equal([GateOutcome.Signed], first.Audit.Select(a => a.Outcome));

        // The second would complete it: the move's check refuses, so it isn't counted, and the attempt is audited as refused.
        var refused = result.Trace[3];
        Assert.False(refused.Accepted);
        Assert.Equal((Refusals.Check, ReleasePrice), (refused.Refusal, refused.RefusedBy));
        Assert.Equal([GateOutcome.Refused], refused.Audit.Select(a => a.Outcome));
        Assert.Equal([(ReleasePrice, TriggerOutcome.Refused)], Ran(refused));

        // Once the price is fixed, the next valid signature completes it: sign's points, the move's checks, then the microstep's points,
        // and the after-commit point last, queued and marked outside.
        var completed = result.Trace[5];
        Assert.True(completed.Accepted);
        Assert.Equal([Released], completed.Configuration);
        Assert.Equal([GateOutcome.Signed, GateOutcome.Completed], completed.Audit.Select(a => a.Outcome));
        Assert.Equal([(ReleasePrice, TriggerOutcome.Passed), (SignNote, TriggerOutcome.Ran), (StampRelease, TriggerOutcome.Ran), (PublishToPortal, TriggerOutcome.Queued)],
            Ran(completed));
        Assert.True(completed.TriggerPoints![^1].Outside);
        Assert.Equal("released", Value(completed, Stamp));
    }

    [Fact]
    public async Task A_team_code_check_takes_the_assumed_word_and_a_word_for_a_guard_is_MQ9305()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Documents,
            [Verb("freeze", $$""","assume":{"{{FreezeApproval}}":"error"}"""), Verb("freeze", $$""","assume":{"FreezeApproval":"warning"}"""), Verb("thaw")], Doc());
        Assert.Equal((Refusals.Check, FreezeApproval), (result.Trace[1].Refusal, result.Trace[1].RefusedBy));
        Assert.True(result.Trace[1].TriggerPoints!.Single().Assumed);
        var warned = result.Trace[2];
        Assert.True(warned.Accepted);
        Assert.Equal([FreezeApproval], warned.CheckWarnings);
        Assert.True(warned.TriggerPoints!.Single() is { Outcome: TriggerOutcome.Warned, Assumed: true });

        var bad = await SimulateAsync(store, Documents, [Verb("freeze", ""","assume":{"01JTRW00000000000000000001":"pass"}""")], Doc());
        Assert.Contains(bad.Diagnostics, d => d.Rule == "MQ9305" && d.Message.Contains("not a check the team writes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_human_tasks_complete_check_refuses_the_step_on_done_and_on_error()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        JsonElement[] contract = [Json($$"""{"id":"o1","name":"deal","entity":"{{Contract}}","values":{"{{Label}}":""} }""")];
        foreach (var input in new[] { "invoke-done", "invoke-error" })
        {
            var refused = (await SimulateAsync(store, Approvals, [Json($$"""{"input":"{{input}}","invoke":"{{Prepare}}","actor":"{{Approver}}"}""")], contract)).Trace[1];
            Assert.Equal((false, Refusals.Check, NeedsLabel), (refused.Accepted, refused.Refusal, refused.RefusedBy));
            Assert.Equal([Drafting], refused.Configuration);
        }

        var done = (await SimulateAsync(store, Approvals, [Json($$"""{"input":"invoke-done","invoke":"{{Prepare}}","actor":"{{Approver}}"}""")],
            [Json($$"""{"id":"o1","name":"deal","entity":"{{Contract}}","values":{"{{Label}}":"Supply"} }""")])).Trace[1];
        Assert.True(done.Accepted);
        Assert.Equal([(NeedsLabel, TriggerOutcome.Passed)], Ran(done));
    }

    [Fact]
    public async Task Another_lifecycles_warning_runs_during_a_verb_step()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Projects, [Json($$"""{"input":"verb","verb":"modify","values":{"{{Salary}}":200000} }""")],
            [Json($$"""{"id":"o1","name":"eve","entity":"{{Employee}}","values":{"{{Salary}}":90000} }""")]);
        var modify = result.Trace[1];
        Assert.True(modify.Accepted);
        Assert.Equal([SalaryWarning], modify.CheckWarnings);
        Assert.Equal(Hr, modify.TriggerPoints!.Single().Lifecycle);
    }

    [Fact]
    public async Task Recording_writes_refusals_and_warnings_and_trigger_points_only_on_request_and_replay_compares_them()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        JsonElement[] steps = [Event(ReviewEvent), Json($$"""{"input":"verb","verb":"modify","values":{"{{Price}}":80} }""")];
        var plain = await store.RecordScenarioAsync(Documents, new RecordScenarioRequest { Name = "plain", Steps = steps, Objects = Doc(), Subject = "o1" },
            dryRun: true, ChangeSource.Editor, Ct);
        Assert.True(plain.Status == ProcessCallStatus.Ok, plain.Detail + " " + string.Join(" | ", plain.Diagnostics.Select(d => d.Message)));
        var recorded = plain.Value!.Element.GetProperty("steps");
        Assert.Equal("check", recorded[0].GetProperty("expect").GetProperty("refusal").GetString());
        Assert.Equal(TitleBeforeReview, recorded[0].GetProperty("expect").GetProperty("refusedBy").GetString());
        Assert.Equal(LargePriceDrop, recorded[1].GetProperty("expect").GetProperty("checkWarnings")[0].GetString());
        Assert.False(recorded[1].GetProperty("expect").TryGetProperty("triggerPoints", out var unexpected), unexpected.ToString());

        var asked = await store.RecordScenarioAsync(Documents,
            new RecordScenarioRequest { Name = "asked", Steps = steps, Objects = Doc(), Subject = "o1", TriggerPoints = true }, dryRun: true, ChangeSource.Editor, Ct);
        Assert.Equal(LargePriceDrop, asked.Value!.Element.GetProperty("steps")[1].GetProperty("expect").GetProperty("triggerPoints")[0].GetString());

        // A scenario whose expectations differ is MQ9310.
        var scenario = asked.Value.Element.Deserialize<Scenario>(EngineJson.Options)!;
        var wrong = scenario with
        {
            Steps = [scenario.Steps[0], scenario.Steps[1] with { Expect = scenario.Steps[1].Expect! with { CheckWarnings = [] } }],
        };
        using var runtime = new ProcessRuntime(store.Current!, 1, Ct);
        Assert.True(ScenarioReplayer.Replay(scenario, runtime)!.Passed);
        Assert.Equal("MQ9310", ScenarioReplayer.Replay(wrong, runtime)!.Failure?.Rule);
    }

    [Fact]
    public async Task A_simulation_lists_the_team_code_checks_a_trigger_reaches()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var result = await SimulateAsync(store, Documents, [], Doc());
        Assert.All(result.Enabled, e => Assert.Null(e.ChecksUnknown));
        Assert.DoesNotContain("checksUnknown", JsonSerializer.Serialize(result.Enabled, EngineJson.Options), StringComparison.Ordinal);
        // In Released, withdraw names delete, whose check DeleteAudit is the team's code.
        var released = await SimulateAsync(store, Documents, [Event(SubmitEvent), Sign("ann"), Sign("bob")], Doc(title: "Pump", price: 500));
        Assert.Equal([Released], released.Configuration);
        Assert.Equal([DeleteAudit], released.Enabled.Single(e => e.Event == WithdrawEvent).ChecksUnknown);
        Assert.Contains("\"checksUnknown\"", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(released.Enabled, EngineJson.Options)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_trigger_sequence_refuses_an_attribute_or_end_the_model_does_not_have()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        Assert.Equal(ProcessCallStatus.Ok, (await store.GetTriggerSequenceAsync(Documents, "modify", new TriggerSequenceFilter { Attributes = [Price] }, Ct)).Status);
        var unknown = await store.GetTriggerSequenceAsync(Documents, "modify", new TriggerSequenceFilter { Attributes = [Price, "01JTRA00000000000000000099"] }, Ct);
        Assert.Equal(ProcessCallStatus.BadRequest, unknown.Status);
        Assert.Contains("01JTRA00000000000000000099", unknown.Detail, StringComparison.Ordinal);
        // An attribute id given as the end is no relation end.
        Assert.Equal(ProcessCallStatus.BadRequest, (await store.GetTriggerSequenceAsync(Documents, "connect", new TriggerSequenceFilter { End = Price }, Ct)).Status);
    }

    [Fact]
    public async Task A_gated_moves_own_checks_are_listed_only_for_a_signature_that_can_complete_the_gate()
    {
        var (h, store) = await OpenAsync(releaseAudit: true);
        using var _ = h;
        await using var __ = store;
        // The gate takes two signatures: the first can't complete it, so the move's check ReleaseAudit won't run on it.
        var first = await SimulateAsync(store, Documents, [Event(SubmitEvent)], Doc(title: "Pump", price: 500));
        Assert.Null(first.Enabled.Single(e => e.Event == ApproveEvent).ChecksUnknown);
        var second = await SimulateAsync(store, Documents, [Event(SubmitEvent), Sign("ann")], Doc(title: "Pump", price: 500));
        Assert.Equal(["01JTRQ00000000000000000015"], second.Enabled.Single(e => e.Event == ApproveEvent).ChecksUnknown);
    }
}
