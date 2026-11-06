using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The resolved model of object effects (object-effects-design.md section 6) over the <c>process-basics</c> fixture: an action's sets with
/// their attributes and typed values, the <c>actor</c> and <c>in-state</c> nodes (an in-state node's lifecycle, states, ends, binding and
/// codes under an enum binding, a code binding and no binding), a scenario object's states and a step's expected values by name, their
/// records, and the keys they add.
/// </summary>
public sealed class ResolvedObjectEffectsTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, bound to Order.status (OrderStatus)
    private const string C = "01JPRC00000000000000000005"; // CustomerLifecycle, bound to Customer.level by codes
    private const string Order = "01JENT00000000000000000001";
    private const string OrderStatus = "01JENM00000000000000000001";
    private const string Customer = "01JENT00000000000000000005";
    private const string Level = "01JATT00000000000000000052";
    private const string ApprovedBy = "01JATT00000000000000000041";
    private const string ApprovedOn = "01JATT00000000000000000042";
    private const string CustomerEnd = "01JEND00000000000000000002";
    private const string Active = "01JSTA00000000000000000051";
    private const string Paused = "01JSTA00000000000000000052";
    private const string Away = "01JSTA00000000000000000053";
    private const string Sick = "01JSTA00000000000000000054";
    private const string Review = "01JSTA00000000000000000003";
    private const string Pending = "01JSTA00000000000000000004";
    private const string Stamp = "01JPRX00000000000000000071"; // sets approvedBy = current_actor(), approvedOn = now()
    private const string NotTheApprover = "01JPRX00000000000000000072"; // subject.approvedBy <> current_actor()
    private const string CustomerAway = "01JPRX00000000000000000073"; // in_state(subject.customer, CustomerLifecycle.Paused)
    private const string InReview = "01JPRX00000000000000000074"; // in_state(subject, OrderLifecycle.Review)
    private const string InPending = "01JPRX00000000000000000075"; // in_state(subject, OrderLifecycle.Review.Pending)
    private const string Sc = "01JSCN00000000000000000009";
    private const string CustomerObject = "01JXBJ00000000000000000002";
    private const string Package = "01JPKG00000000000000000001";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string OrderFile = "model/entities/order.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Order gains approvedBy and approvedOn; Customer with level and its lifecycle (codes on Active, Away, Sick; Paused holds Away and Sick);
    // OrderCustomer; OrderLifecycle's stamp action and four guards; a scenario whose customer object gives its state and whose step expects
    // a value.
    private static async Task<ResolvedModel> ResolveAsync(bool codeBinding = true)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        var order = JsonNode.Parse(harness.Read(OrderFile))!.AsObject();
        order["attributes"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"{{ApprovedBy}}","name":"approvedBy","type":"string","length":40}"""));
        order["attributes"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"{{ApprovedOn}}","name":"approvedOn","type":"datetimeoffset"}"""));
        harness.Write(OrderFile, order.ToJsonString());
        harness.Write("model/entities/customer.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}",
             "key":{"attributes":["01JATT00000000000000000051"]},
             "attributes":[{"id":"01JATT00000000000000000051","name":"id","type":"uuid"},{"id":"{{Level}}","name":"level","type":"string","length":1}]}
            """);
        harness.Write("model/relations/order-customer.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"01JRXN00000000000000000001","name":"OrderCustomer",
             "ends":[{"id":"01JEND00000000000000000001","entity":"{{Order}}","role":"orders","navigation":"orders"},
               {"id":"{{CustomerEnd}}","entity":"{{Customer}}","role":"customer","navigation":"customer","max":1}]}
            """);
        var binding = codeBinding ? $",\"boundAttribute\":\"{Level}\"" : "";
        harness.Write("model/processes/customer-lifecycle.json", $$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{C}}","name":"CustomerLifecycle","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000005","entity":"{{Customer}}"{{binding}}}],
             "events":[{"id":"01JPRX00000000000000000061","name":"pause"}],
             "states":[{"id":"{{Active}}","name":"Active","code":"A"},
               {"id":"{{Paused}}","name":"Paused","type":"compound","states":[{"id":"{{Away}}","name":"Away","code":"W"},{"id":"{{Sick}}","name":"Sick","code":"S"}]}],
             "initial":"{{Active}}",
             "transitions":[{"id":"01JTRN00000000000000000051","source":"{{Active}}","event":"01JPRX00000000000000000061","targets":["{{Paused}}"]}]}
            """);
        var process = JsonNode.Parse(harness.Read(ProcessFile))!.AsObject();
        process["actions"]!.AsArray().Add(JsonNode.Parse($$"""
            {"id":"{{Stamp}}","name":"stamp","sets":[{"id":"01JSET00000000000000000001","attribute":"{{ApprovedBy}}","value":{"currentActor":true} },
              {"id":"01JSET00000000000000000002","attribute":"{{ApprovedOn}}","value":{"now":true} }]}
            """));
        foreach (var guard in new[]
        {
            $$"""{"id":"{{NotTheApprover}}","name":"notTheApprover","condition":{"op":"ne","args":[{"path":["{{ApprovedBy}}"]},{"currentActor":true}]} }""",
            $$"""{"id":"{{CustomerAway}}","name":"customerAway","condition":{"inState":["{{Paused}}"],"of":["{{CustomerEnd}}"]} }""",
            $$"""{"id":"{{InReview}}","name":"inReview","condition":{"inState":["{{Review}}"]} }""",
            $$"""{"id":"{{InPending}}","name":"inPending","condition":{"inState":["{{Pending}}"]} }""",
        })
            process["guards"]!.AsArray().Add(JsonNode.Parse(guard));
        process["transitions"]![0]!["actions"]!.AsArray().Add(Stamp);
        harness.Write(ProcessFile, process.ToJsonString());
        harness.Write("model/scenarios/order-lifecycle/with-customer.json", $$"""
            {"$schema":"../../../.schema/v1/scenario.json","kind":"scenario","id":"{{Sc}}","name":"WithCustomer","process":"{{P}}",
             "objects":[
               {"id":"01JXBJ00000000000000000001","name":"order","entity":"{{Order}}","links":[{"end":"{{CustomerEnd}}","object":"{{CustomerObject}}"}]},
               {"id":"{{CustomerObject}}","name":"customer","entity":"{{Customer}}","states":["{{Away}}"]}],
             "subject":"01JXBJ00000000000000000001",
             "steps":[{"id":"01JSTP00000000000000000091","event":"01JPRX00000000000000000001","actor":"01JACT00000000000000000001","expect":{"values":{"{{ApprovedBy}}":"bob"} } }]}
            """);
        await using var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var errors = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error && !d.Rule.StartsWith("MQ930", StringComparison.Ordinal)); // what a replay finds is the simulation's
        Assert.True(!errors.Any(), string.Join(" | ", errors.Select(d => d.Rule + " " + d.Message)));
        var resolved = ResolutionKit.Resolve(store.Current!);
        harness.Dispose();
        return resolved;
    }

    private static JsonObject Record(ResolvedModel model, string id) =>
        JsonSerializer.SerializeToNode(ResolvedRecords.Entries(model, "all", null).Single(e => e.Id == id).Project(), Web)!.AsObject();

    private static RGuard Guard(ResolvedModel model, string id) => model.Processes.Single(p => p.Id == P).Guards.Single(g => g.Id == id);

    [Fact]
    public async Task An_actions_sets_resolve_with_their_declaring_attribute_and_typed_value()
    {
        var model = await ResolveAsync();
        var process = model.Processes.Single(p => p.Id == P);
        var stamp = process.Actions.Single(a => a.Id == Stamp);
        Assert.False(stamp.IsStub); // sets and no expression
        Assert.Null(stamp.Expression);
        Assert.Equal(["approvedBy", "approvedOn"], stamp.Sets.Select(s => s.Attribute.Name));
        Assert.All(stamp.Sets, s => Assert.Same(stamp, s.Action));
        Assert.Same(model.Entity("Order").OwnAttributes.Single(a => a.Id == ApprovedBy), stamp.Sets[0].Attribute);

        var actor = stamp.Sets[0].Value;
        Assert.Equal("current-actor", actor.Node);
        Assert.Equal("current_actor()", actor.Text);
        Assert.Equal("string", actor.Type.Kind); // the attribute's type, which a set's value meets
        Assert.Null(actor.JavaScript); // engine-only
        var now = stamp.Sets[1].Value;
        Assert.Equal("now", now.Node);
        Assert.NotNull(now.JavaScript);

        // The list keys on the process and on the entity that declares the attributes set; each set too.
        Assert.Contains("e:" + Order, stamp.Sets.MembershipKeys);
        Assert.Contains("e:" + P, stamp.Sets.MembershipKeys);
        Assert.Contains("e:" + Order, stamp.Sets[0].Dependencies);

        // An action without sets has none, and is a stub only without an expression.
        Assert.Empty(process.Actions.Single(a => a.Name == "recordSubmission").Sets);
        Assert.True(process.Actions.Single(a => a.Name == "notifyClerk").IsStub);
        Assert.Equal("current-actor", Guard(model, NotTheApprover).Condition!.Args[1].Node);
        Assert.Equal("string", Guard(model, NotTheApprover).Condition!.Args[1].Type.Kind);
    }

    [Fact]
    public async Task An_in_state_node_gives_its_lifecycle_states_path_binding_and_codes()
    {
        var model = await ResolveAsync();

        // A code binding whose codes form a map: Paused has none, so the codes of the states inside it.
        var away = Guard(model, CustomerAway).Condition!;
        Assert.Equal("in-state", away.Node);
        Assert.Equal("in_state(subject.customer, CustomerLifecycle.Paused)", away.Text);
        Assert.Equal("CustomerLifecycle", away.Process!.Name);
        Assert.Equal(["Paused"], away.States.Select(s => s.Name));
        Assert.Equal(["customer"], away.Through.Select(e => e.Role));
        Assert.Equal("level", away.BoundAttribute!.Name);
        Assert.Null(away.Enum);
        Assert.Equal(["W", "S"], away.Codes);
        Assert.Null(away.JavaScript);
        var awayKeys = Guard(model, CustomerAway).Dependencies;
        Assert.Contains("e:" + C, awayKeys);
        Assert.Contains("e:" + Customer, awayKeys); // declares the bound attribute, and the path reaches it

        // An enum binding on the running chart: the member of the root-level state named, not its name.
        var review = Guard(model, InReview).Condition!;
        Assert.Equal("OrderLifecycle", review.Process!.Name);
        Assert.Empty(review.Through);
        Assert.Equal("status", review.BoundAttribute!.Name);
        Assert.Equal("OrderStatus", review.Enum!.Name);
        Assert.Equal("Review", Assert.IsType<REnumMember>(Assert.Single(review.Codes)).Name);
        Assert.Contains("e:" + OrderStatus, Guard(model, InReview).Dependencies);

        // A state inside a bound one: the stored member cannot tell, so no codes.
        var pending = Guard(model, InPending).Condition!;
        Assert.Equal("OrderStatus", pending.Enum!.Name);
        Assert.Empty(pending.Codes);

        // Other nodes carry none of it.
        var plain = Guard(model, NotTheApprover).Condition!;
        Assert.Null(plain.Process);
        Assert.Empty(plain.States);
        Assert.Empty(plain.Codes);
    }

    [Fact]
    public async Task An_in_state_node_of_a_lifecycle_with_no_binding_has_no_attribute_and_no_codes()
    {
        var away = Guard(await ResolveAsync(codeBinding: false), CustomerAway).Condition!;
        Assert.Equal(["Paused"], away.States.Select(s => s.Name));
        Assert.Null(away.BoundAttribute);
        Assert.Null(away.Enum);
        Assert.Empty(away.Codes);
    }

    [Fact]
    public async Task Objects_states_and_expected_values_resolve_and_records_show_them_only_where_present()
    {
        var model = await ResolveAsync();
        var scenario = model.Scenarios.Single(s => s.Id == Sc);
        Assert.Equal(["Away"], scenario.Objects.Single(o => o.Id == CustomerObject).States.Select(s => s.Name));
        Assert.Contains("e:" + C, scenario.Objects.Single(o => o.Id == CustomerObject).States.MembershipKeys);
        Assert.Empty(scenario.Objects.Single(o => o.Name == "order").States);
        Assert.Equal("bob", scenario.Steps[0].Expect!.Values["approvedBy"]);
        Assert.Contains(scenario.Steps[0].Dependencies, k => k == "e:" + Order); // the attribute's name

        var process = Record(model, P);
        var actions = process["actions"]!.AsArray();
        var stamp = actions.Single(a => a!["id"]!.GetValue<string>() == Stamp)!;
        Assert.False(stamp["isStub"]!.GetValue<bool>());
        var sets = stamp["sets"]!.AsArray();
        Assert.Equal(ApprovedBy, sets[0]!["attribute"]!.GetValue<string>());
        Assert.Equal("current_actor()", sets[0]!["value"]!["text"]!.GetValue<string>());
        Assert.Equal("now()", sets[1]!["value"]!["text"]!.GetValue<string>());
        Assert.All(actions.Where(a => a!["id"]!.GetValue<string>() != Stamp), a => Assert.False(a!.AsObject().ContainsKey("sets")));

        var record = Record(model, Sc);
        var objects = record["objects"]!.AsArray();
        Assert.Equal(Away, objects.Single(o => o!["id"]!.GetValue<string>() == CustomerObject)!["states"]![0]!.GetValue<string>());
        Assert.False(objects.Single(o => o!["name"]!.GetValue<string>() == "order")!.AsObject().ContainsKey("states"));
        Assert.Equal("bob", record["steps"]![0]!["expect"]!["values"]!["approvedBy"]!.GetValue<string>());

        var happy = Record(model, "01JSCN00000000000000000001");
        Assert.All(happy["steps"]!.AsArray(), s => Assert.False(s!["expect"] is JsonObject x && x.ContainsKey("values")));
    }
}
