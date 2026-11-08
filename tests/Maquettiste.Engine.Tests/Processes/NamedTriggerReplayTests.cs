using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Named trigger points in simulation, scenarios, replay keys and the entity's sequence (trigger-points-design.md decisions 20, 33, 34 and 35,
/// sections 7 to 9, section 15's part 2), over an invented model: a customer with no lifecycle, derived from a party and carrying a stereotype,
/// whose <c>create</c> runs <c>CustomerNumber</c>; and a ticket lifecycle whose move a named check refuses as a false guard would.
/// </summary>
public sealed class NamedTriggerReplayTests
{
    private const string Package = "01JPKG00000000000000000001"; // Sales
    private const string Party = "01JNTE00000000000000000001";
    private const string Customer = "01JNTE00000000000000000002";
    private const string Ticket = "01JNTE00000000000000000003";
    private const string Number = "01JNTA00000000000000000002";
    private const string Ready = "01JNTA00000000000000000012";
    private const string Approvable = "01JNTT00000000000000000001";
    private const string Onboarding = "01JNTP00000000000000000001"; // an orchestration of the customer, which has no lifecycle
    private const string Tickets = "01JNTP00000000000000000002";
    private const string Open = "01JNTS00000000000000000001";
    private const string Done = "01JNTS00000000000000000002";
    private const string Submit = "01JNTX00000000000000000001";
    private const string PartyAudit = "01JNTQ00000000000000000001"; // Party: the farthest base
    private const string AZero = "01JNTQ00000000000000000002"; // Customer
    private const string CustomerNumber = "01JNTQ00000000000000000003"; // Customer, sets the number, noBypass
    private const string ApprovableStamp = "01JNTQ00000000000000000004"; // the stereotype, at the customer's depth
    private const string TeamCode = "01JNTQ00000000000000000005"; // Customer, a check the team writes
    private const string TicketGate = "01JNTQ00000000000000000006"; // Ticket, a check on promote
    private const string Step = "01JNTZ00000000000000000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string T(string text) => JsonSerializer.Serialize(new { text });

    private static string Point(string id, string name, string body) =>
        $$"""{"kind":"trigger-point","id":"{{id}}","name":"{{name}}","package":"{{Package}}",{{body}}}""";

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(bool named = true, string numberVerbs = "\"create\"", string? assume = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/vocabularies/stereotypes/approvable.json",
            $$"""{"kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"Approvable","appliesTo":["entity"]}""");
        h.Write("model/entities/party.json", $$"""
            {"kind":"entity","id":"{{Party}}","name":"Party","package":"{{Package}}","key":{"attributes":["01JNTA00000000000000000001"]},
             "attributes":[{"id":"01JNTA00000000000000000001","name":"id","type":"uuid","required":true},{"id":"{{Number}}","name":"number","type":"string"}]}
            """);
        h.Write("model/entities/customer.json",
            $$"""{"kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}","base":"{{Party}}","stereotypes":["approvable"]}""");
        h.Write("model/entities/ticket.json", $$"""
            {"kind":"entity","id":"{{Ticket}}","name":"Ticket","package":"{{Package}}","key":{"attributes":["01JNTA00000000000000000011"]},
             "attributes":[{"id":"01JNTA00000000000000000011","name":"id","type":"uuid","required":true},{"id":"{{Ready}}","name":"ready","type":"bool"}]}
            """);
        h.Write("model/processes/onboarding.json", $$"""
            {"kind":"process","id":"{{Onboarding}}","name":"Onboarding","package":"{{Package}}","use":"orchestration",
             "subjects":[{"id":"01JNTB00000000000000000001","entity":"{{Customer}}"}],
             "states":[{"id":"01JNTS00000000000000000011","name":"Welcome"}],"initial":"01JNTS00000000000000000011"}
            """);
        h.Write("model/processes/tickets.json", $$"""
            {"kind":"process","id":"{{Tickets}}","name":"Tickets","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JNTB00000000000000000002","entity":"{{Ticket}}"}],
             "events":[{"id":"{{Submit}}","name":"submit","verb":"promote"}],
             "states":[{"id":"{{Open}}","name":"Open"},{"id":"{{Done}}","name":"Done"}],"initial":"{{Open}}",
             "transitions":[{"id":"01JNTK00000000000000000001","source":"{{Open}}","event":"{{Submit}}","targets":["{{Done}}"]}]}
            """);
        if (named)
        {
            h.Write("model/trigger-points/party-audit.json", Point(PartyAudit, "PartyAudit",
                $$""" "subjects":[{"entity":"{{Party}}"}],"type":"action","timing":"before","verbs":["create"] """));
            h.Write("model/trigger-points/a-zero.json", Point(AZero, "AZero",
                $$""" "subjects":[{"entity":"{{Customer}}"}],"type":"action","timing":"before","verbs":["create"] """));
            var sets = $$""" "sets":[{"id":"01JNTV00000000000000000001","attribute":"{{Number}}","value":{{T("'C-00042'")}} }] """;
            h.Write("model/trigger-points/customer-number.json", Point(CustomerNumber, "CustomerNumber",
                $$""" "subjects":[{"entity":"{{Customer}}"}],"type":"action","timing":"before","verbs":[{{numberVerbs}}],"noBypass":true,{{sets}} """));
            h.Write("model/trigger-points/approvable-stamp.json", Point(ApprovableStamp, "ApprovableStamp",
                """ "subjects":[{"stereotype":"approvable"}],"type":"action","timing":"before","verbs":["create"] """));
            h.Write("model/trigger-points/team-code.json", Point(TeamCode, "TeamCode",
                $$""" "subjects":[{"entity":"{{Customer}}"}],"type":"check","verbs":["create"] """));
            h.Write("model/trigger-points/ticket-gate.json", Point(TicketGate, "TicketGate",
                $$""" "subjects":[{"entity":"{{Ticket}}"}],"type":"check","verbs":["promote"],"condition":{{T("subject.ready = true")}} """));
        }

        h.Write("model/scenarios/onboarding/create.json", $$"""
            {"kind":"scenario","id":"01JNTZ00000000000000000001","name":"create","package":"{{Package}}","process":"{{Onboarding}}",
             "objects":[{"id":"01JNTJ00000000000000000001","name":"c","entity":"{{Customer}}"}],"subject":"01JNTJ00000000000000000001",
             "steps":[{"id":"{{Step}}","input":"verb","verb":"create"{{(assume is null ? "" : ",\"assume\":" + assume)}} }]}
            """);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (h, store);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement[] Object(string entity, string values = "") =>
        [Json($$"""{"id":"o1","name":"it","entity":"{{entity}}","values":{ {{values}} } }""")];

    private static async Task<SimulationResult> SimulateAsync(ModelStore store, string process, JsonElement[] steps, JsonElement[] objects)
    {
        var call = await store.SimulateProcessAsync(process, new SimulateRequest { Steps = steps, Objects = objects, Subject = "o1" }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Message)));
        return call.Value!;
    }

    private static IReadOnlyList<(string, TriggerOutcome)> Ran(StepTrace trace) =>
        trace.TriggerPoints?.Select(r => (r.TriggerPoint, r.Outcome)).ToList() ?? [];

    private static string HashOf(ModelSnapshot snapshot, IEnumerable<string> keys) =>
        new DependencyHasher(snapshot, ResolutionKit.Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>())
            .InputHash("unit", [.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    [Fact]
    public async Task The_model_validates_without_errors()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var found = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
        Assert.Empty(found.Where(d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("model/", StringComparison.Ordinal)
            && !(d.FilePath ?? "").Contains("order", StringComparison.Ordinal)).Select(d => d.Rule + " " + d.Message));
    }

    [Fact]
    public async Task A_create_on_a_type_with_no_lifecycle_runs_its_named_points_in_decision_34s_order()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var create = (await SimulateAsync(store, Onboarding, [Json("""{"input":"verb","verb":"create"}""")], Object(Customer))).Trace[1];
        Assert.True(create.Accepted, create.Refusal);
        // The farthest base first; at the customer's depth an entity's points before the stereotype's, then by name; checks after.
        Assert.Equal([(PartyAudit, TriggerOutcome.Ran), (AZero, TriggerOutcome.Ran), (CustomerNumber, TriggerOutcome.Ran),
            (ApprovableStamp, TriggerOutcome.Ran), (TeamCode, TriggerOutcome.Passed)], Ran(create));
        Assert.All(create.TriggerPoints!, r => Assert.Null(r.Lifecycle));
        Assert.True(create.TriggerPoints![4].Assumed);
        Assert.Equal("C-00042", create.ValuesChanged![Number].GetString());
        Assert.Equal("C-00042", create.TriggerPoints[2].Sets![Number].GetString());
        Assert.DoesNotContain("\"lifecycle\"", JsonSerializer.Serialize(create.TriggerPoints[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_check_refuses_a_move_as_a_false_guard_would()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var refused = (await SimulateAsync(store, Tickets, [Json($$"""{"event":"{{Submit}}"}""")], Object(Ticket, $$""" "{{Ready}}":false """))).Trace[1];
        Assert.False(refused.Accepted);
        Assert.Equal((Refusals.Check, TicketGate), (refused.Refusal, refused.RefusedBy));
        Assert.Equal([Open], refused.Configuration);

        var taken = (await SimulateAsync(store, Tickets, [Json($$"""{"event":"{{Submit}}"}""")], Object(Ticket, $$""" "{{Ready}}":true """))).Trace[1];
        Assert.True(taken.Accepted);
        Assert.Equal([Done], taken.Configuration);
        Assert.Equal([(TicketGate, TriggerOutcome.Passed)], Ran(taken));
        Assert.Null(taken.TriggerPoints![0].Lifecycle);
    }

    [Theory]
    [InlineData(TeamCode)]
    [InlineData("Sales.TeamCode")]
    public async Task Assume_names_a_named_team_code_check_by_id_or_by_qualified_name(string key)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var step = Json($$"""{"input":"verb","verb":"create","assume":{"{{key}}":"error"} }""");
        var create = (await SimulateAsync(store, Onboarding, [step], Object(Customer))).Trace[1];
        Assert.False(create.Accepted);
        Assert.Equal((Refusals.Check, TeamCode), (create.Refusal, create.RefusedBy));
    }

    [Fact]
    public async Task A_scenario_keys_its_assumed_named_check_by_qualified_name_and_replays_when_a_covering_point_changes()
    {
        var (h, store) = await OpenAsync(assume: $$"""{"{{TeamCode}}":"pass"}""");
        using var _ = h;
        await using var __ = store;
        var before = store.Current!;
        var model = ResolutionKit.Resolve(before);
        var step = Assert.IsType<RStep>(model.Find(Step));
        Assert.Equal(new Dictionary<string, object?> { ["Sales.TeamCode"] = "pass" }, step.Assume);
        Assert.Contains(TriggerNaming.Key, step.Dependencies);
        Assert.Contains("e:" + CustomerNumber, step.Dependencies);
        Assert.Equal(["AZero", "ApprovableStamp", "CustomerNumber", "PartyAudit", "TeamCode"],
            model.Verbs.Single(v => v.Key == "create").TriggerPoints.Select(p => p.Name));

        // CustomerNumber gains a verb: the naming key changes, so the scenario replays.
        var (h2, changed) = await OpenAsync(numberVerbs: "\"create\",\"modify\"", assume: $$"""{"{{TeamCode}}":"pass"}""");
        using var ___ = h2;
        await using var ____ = changed;
        Assert.NotEqual(HashOf(before, step.Dependencies), HashOf(changed.Current!, step.Dependencies));

        // A project without named points: the scenario keys as part 1 does, and the naming key's hash is the one of no trigger points.
        var (h3, plain) = await OpenAsync(named: false);
        using var _____ = h3;
        await using var ______ = plain;
        var plainStep = Assert.IsType<RStep>(ResolutionKit.Resolve(plain.Current!).Find(Step));
        Assert.DoesNotContain("e:" + CustomerNumber, plainStep.Dependencies);
        Assert.Empty(ResolutionKit.Resolve(plain.Current!).Verbs.Single(v => v.Key == "create").TriggerPoints);
    }

    [Fact]
    public async Task An_entity_alone_answers_the_sequence_for_object_verbs_only()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var call = await store.GetEntityTriggerSequenceAsync(Customer, "create", new TriggerSequenceFilter(), Ct);
        Assert.Equal(ProcessCallStatus.Ok, call.Status);
        Assert.Equal([PartyAudit, AZero, CustomerNumber, ApprovableStamp, TeamCode], call.Value!.Steps.Select(s => s.TriggerPoint).OfType<string>());
        Assert.All(call.Value.Steps, s => Assert.Null(s.Lifecycle));
        Assert.True(call.Value.Steps.Single(s => s.TriggerPoint == CustomerNumber).NoBypass);
        Assert.Equal(ProcessCallStatus.BadRequest, (await store.GetEntityTriggerSequenceAsync(Customer, "promote", new TriggerSequenceFilter(), Ct)).Status);
        Assert.Equal(ProcessCallStatus.NotFound, (await store.GetEntityTriggerSequenceAsync(Onboarding, "create", new TriggerSequenceFilter(), Ct)).Status);
    }
}
