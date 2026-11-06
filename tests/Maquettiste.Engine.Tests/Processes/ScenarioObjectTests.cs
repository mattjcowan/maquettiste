using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// A scenario's objects (lifecycle-design.md decision 22) over the <c>process-basics</c> fixture: objects are parts of the scenario with an
/// index kind, name forms and references; <c>subject</c> and each link's <c>object</c> are indexed references; deleting an object's entity
/// drops the object and the links to it, and a subject left naming nothing refuses <c>remove-references</c>; the resolved model and the
/// records carry the objects only where a scenario has them; and <c>sync-enum</c>'s check counts object values.
/// </summary>
public sealed class ScenarioObjectTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, bound to Order.status (enum OrderStatus)
    private const string Order = "01JENT00000000000000000001";
    private const string RushOrder = "01JENT00000000000000000003"; // derives from Order
    private const string Customer = "01JENT00000000000000000005";
    private const string Status = "01JATT00000000000000000002"; // Order.status, inherited by RushOrder
    private const string CustomerName = "01JATT00000000000000000052";
    private const string Relation = "01JRXN00000000000000000001"; // OrderCustomer
    private const string OrdersEnd = "01JEND00000000000000000001";
    private const string CustomerEnd = "01JEND00000000000000000002";
    private const string Sc = "01JSCN00000000000000000009"; // WithObjects
    private const string HappyPath = "01JSCN00000000000000000001";
    private const string OrderObject = "01JXBJ00000000000000000001";
    private const string CustomerObject = "01JXBJ00000000000000000002";
    private const string Package = "01JPKG00000000000000000001";
    private const string ScenarioFile = "model/scenarios/order-lifecycle/with-objects.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ScenarioJson(string status) => $$"""
        {"$schema":"../../../.schema/v1/scenario.json","kind":"scenario","id":"{{Sc}}","name":"WithObjects","process":"{{P}}",
         "objects":[
           {"id":"{{OrderObject}}","name":"order","entity":"{{RushOrder}}","values":{"{{Status}}":"{{status}}"},
            "links":[{"end":"{{CustomerEnd}}","object":"{{CustomerObject}}"}]},
           {"id":"{{CustomerObject}}","name":"customer","entity":"{{Customer}}","values":{"{{CustomerName}}":"Ada"} }],
         "subject":"{{OrderObject}}",
         "steps":[{"id":"01JSTP00000000000000000091","event":"01JPRX00000000000000000001"}]}
        """;

    // Customer, RushOrder (derived from Order), OrderCustomer, and a scenario of OrderLifecycle with an order and its customer.
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string status = "Draft", Action<LoaderHarness>? prepare = null, bool errors = false)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/customer.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}",
             "key":{"attributes":["01JATT00000000000000000051"]},
             "attributes":[{"id":"01JATT00000000000000000051","name":"id","type":"uuid"},{"id":"{{CustomerName}}","name":"name","type":"string","length":40,"default":"anonymous"}]}
            """);
        harness.Write("model/entities/rush-order.json", $$"""{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{RushOrder}}","name":"RushOrder","package":"{{Package}}","base":"{{Order}}"}""");
        harness.Write("model/relations/order-customer.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{Relation}}","name":"OrderCustomer",
             "ends":[{"id":"{{OrdersEnd}}","entity":"{{Order}}","role":"orders"},{"id":"{{CustomerEnd}}","entity":"{{Customer}}","role":"customer","max":1}]}
            """);
        harness.Write(ScenarioFile, ScenarioJson(status));
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        if (!errors)
            Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonObject File(LoaderHarness h, string path) => JsonNode.Parse(h.Read(path))!.AsObject();

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonObject Record(Maquettiste.Engine.Resolution.ResolvedModel model, string id) =>
        JsonSerializer.SerializeToNode(ResolvedRecords.Entries(model, "all", null).Single(e => e.Id == id).Project(), Web)!.AsObject();

    private static IReadOnlyList<NameEntry> Find(ModelSnapshot model, string text)
    {
        Assert.True(QualifiedName.TryParse(text, out var name, out var error), error);
        return model.Names(name.Kind).Find(name.Path);
    }

    // ---- the model, the index and the names ----

    [Fact]
    public async Task Objects_are_parts_of_the_scenario_with_references_and_name_forms()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        var scenario = model.Get<Scenario>(Sc)!;
        Assert.Equal([(OrderObject, "order", RushOrder), (CustomerObject, "customer", Customer)], scenario.Objects.Select(o => (o.Id, o.Name, o.Entity)));
        Assert.Equal(OrderObject, scenario.Subject);
        var link = Assert.Single(scenario.Objects[0].Links);
        Assert.Equal((CustomerEnd, CustomerObject), (link.End, link.Object));
        Assert.Equal("Draft", scenario.Objects[0].Values[Status].GetString());
        Assert.Empty(model.Get<Scenario>(HappyPath)!.Objects);
        Assert.Null(model.Get<Scenario>(HappyPath)!.Subject);

        // Parts of the scenario: the index kind object, owned by the scenario.
        Assert.Equal(("object", Sc, "/objects/0"), (model.Index.Entries[OrderObject].Kind, model.Index.Entries[OrderObject].OwnerId, model.Index.Entries[OrderObject].JsonPointer));
        Assert.Equal(("object", Sc, "/objects/1"), (model.Index.Entries[CustomerObject].Kind, model.Index.Entries[CustomerObject].OwnerId, model.Index.Entries[CustomerObject].JsonPointer));

        // subject and each link's object are references to the object, as are the entity, the end and the values' attributes.
        Assert.Contains(model.ReferencesTo(OrderObject), r => r.FromElementId == Sc && r.JsonPointer == "/subject");
        Assert.Contains(model.ReferencesTo(CustomerObject), r => r.FromId == OrderObject && r.JsonPointer == "/objects/0/links/0/object");
        Assert.Contains(model.ReferencesTo(CustomerEnd), r => r.FromId == OrderObject && r.JsonPointer == "/objects/0/links/0/end");
        Assert.Contains(model.ReferencesTo(RushOrder), r => r.FromId == OrderObject && r.JsonPointer == "/objects/0/entity");
        Assert.Contains(model.ReferencesTo(Customer), r => r.FromId == CustomerObject && r.JsonPointer == "/objects/1/entity");
        Assert.Contains(model.ReferencesTo(Status), r => r.FromId == OrderObject && r.FromElementId == Sc);
        Assert.Contains(model.ReferencesTo(CustomerName), r => r.FromId == CustomerObject && r.FromElementId == Sc);

        // <scenario form>.<object>.
        Assert.Equal(OrderObject, Assert.Single(Find(model, "@object:WithObjects.order")).Id);
        Assert.Equal(CustomerObject, Assert.Single(Find(model, "@object:OrderLifecycle.WithObjects.customer")).Id);
        Assert.Equal("Sales.OrderLifecycle.WithObjects.order", model.Names("object").Get(OrderObject)!.Longest);
        Assert.Empty(Find(model, "@object:order"));

        // No new findings about references: every id the objects name resolves to its kind.
        var findings = (await EngineServices.Create(h.Options).Validator.ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics;
        Assert.DoesNotContain(findings, d => d.ElementId == Sc && d.Rule is "MQ2001" or "MQ2002");
    }

    // ---- deletes ----

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_an_objects_entity_drops_the_object_and_the_links_to_it(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // The relation goes too: remove-references refuses to leave it with one end.
        var plan = await store.GetDeletePlanAsync([Customer, Relation], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, string.Join(" | ", plan.Refused.Select(r => r.Rule + " " + r.Why)));
        Assert.Contains(plan.Removes, r => r.Id == Sc && r.Pointer == "/objects/1");
        Assert.Contains(plan.Removes, r => r.Id == Sc && r.Pointer == "/objects/0/links/0");
        Assert.DoesNotContain(plan.Deletes, d => d.Id == Sc);

        var result = await store.ApplyBatchAsync(new ModelBatch(
        [
            new BatchOperation(BatchOp.Delete, Customer, (await store.GetElementAsync(Customer, Ct))!.Hash, null, Resolution: resolution),
            new BatchOperation(BatchOp.Delete, Relation, (await store.GetElementAsync(Relation, Ct))!.Hash, null, Resolution: resolution),
        ]), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        var file = File(h, ScenarioFile);
        var kept = Assert.Single(file["objects"]!.AsArray());
        Assert.Equal(OrderObject, kept!["id"]!.GetValue<string>());
        Assert.False(kept.AsObject().ContainsKey("links")); // the emptied list is the default, so the file leaves it out
        Assert.Equal(OrderObject, file["subject"]!.GetValue<string>());
        Assert.Empty(store.Current!.ReferencesTo(Customer));
    }

    [Fact]
    public async Task A_subject_left_naming_nothing_refuses_remove_references_unless_the_scenario_goes_too()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var refused = await store.GetDeletePlanAsync([RushOrder], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var why = Assert.Single(refused.Refused, r => r.Id == Sc);
        Assert.Equal(("/subject", "MQ2001"), (why.Pointer, why.Rule));
        Assert.Contains("subject", why.Why, StringComparison.Ordinal);

        // The scenario deleted in the same change, or by its process going later in the change.
        var both = await store.GetDeletePlanAsync([RushOrder, Sc], DeleteResolution.RemoveReferences, Ct);
        Assert.True(both.Outcome == SaveOutcome.Saved, string.Join(" | ", both.Refused.Select(r => r.Rule + " " + r.Why)));
        var withProcess = await store.GetDeletePlanAsync([RushOrder, P], DeleteResolution.RemoveReferences, Ct);
        Assert.DoesNotContain(withProcess.Refused, r => r.Id == Sc);
        Assert.Contains(withProcess.Deletes, d => d.Id == Sc);

        // delete-dependents deletes the scenario.
        var cascade = await store.GetDeletePlanAsync([RushOrder], DeleteResolution.DeleteDependents, Ct);
        Assert.True(cascade.Outcome == SaveOutcome.Saved, string.Join(" | ", cascade.Refused.Select(r => r.Rule + " " + r.Why)));
        Assert.Contains(cascade.Deletes, d => d.Id == Sc);
        var hash = (await store.GetElementAsync(RushOrder, Ct))!.Hash;
        Assert.Equal(SaveOutcome.Saved, (await store.DeleteAsync(RushOrder, hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct)).Outcome);
        Assert.False(h.Exists(ScenarioFile));
        Assert.True(h.Exists("model/scenarios/order-lifecycle/happy-path.json"));
    }

    // ---- resolution and records ----

    [Fact]
    public async Task Objects_resolve_and_records_carry_them_only_where_a_scenario_has_them()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var resolved = ResolutionKit.Resolve(store.Current!);

        var s = resolved.Scenarios.Single(x => x.Id == Sc);
        Assert.Equal(["order", "customer"], s.Objects.Select(o => o.Name));
        Assert.Same(s.Objects[0], s.Subject);
        Assert.Equal("RushOrder", s.Objects[0].Entity.Name);
        Assert.Equal(new Dictionary<string, object?> { ["status"] = "Draft" }, s.Objects[0].Values); // an inherited attribute, by name
        Assert.Equal(new Dictionary<string, object?> { ["name"] = "Ada" }, s.Objects[1].Values);
        var link = Assert.Single(s.Objects[0].Links);
        Assert.Equal("customer", link.End.Role);
        Assert.Same(s.Objects[1], link.Object);
        Assert.Empty(s.Objects[1].Links);
        Assert.Contains(s.Objects[0].Dependencies, k => k.Contains(Order, StringComparison.Ordinal)); // the base declares status
        Assert.Contains(s.Objects[0].Dependencies, k => k.Contains(Relation, StringComparison.Ordinal));
        var happy = resolved.Scenarios.Single(x => x.Id == HappyPath);
        Assert.Empty(happy.Objects);
        Assert.Null(happy.Subject);

        var record = Record(resolved, Sc);
        Assert.Equal(OrderObject, record["subject"]!.GetValue<string>());
        var objects = record["objects"]!.AsArray();
        Assert.Equal(
            $$"""{"id":"{{OrderObject}}","name":"order","entity":"{{RushOrder}}","values":{"status":"Draft"},"links":[{"end":"{{CustomerEnd}}","object":"{{CustomerObject}}"}]}""",
            objects[0]!.ToJsonString());
        Assert.Equal($$"""{"id":"{{CustomerObject}}","name":"customer","entity":"{{Customer}}","values":{"name":"Ada"},"links":[]}""", objects[1]!.ToJsonString());
        var plain = Record(resolved, HappyPath);
        Assert.False(plain.ContainsKey("objects"));
        Assert.False(plain.ContainsKey("subject"));
    }

    // ---- literal uses (sync-enum) ----

    [Theory]
    [InlineData("Draft", false)]
    [InlineData("Archived", true)]
    public async Task Sync_enum_counts_an_objects_values(string status, bool refused)
    {
        // OrderStatus gains Archived, which no state has: sync-enum removes it unless something holds it.
        var (h, store) = await OpenAsync(status, x =>
        {
            var e = File(x, "model/enums/order-status.json");
            e["members"]!.AsArray().Add(new JsonObject { ["id"] = "01JMEM00000000000000000099", ["name"] = "Archived" });
            x.Write("model/enums/order-status.json", e.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        var plan = await store.PlanSyncEnumAsync(P, Ct);
        Assert.Null(plan.Problem);
        Assert.Contains("Archived", plan.Removed);
        if (refused)
            Assert.Equal([Sc], Assert.Single(plan.Refused, r => r.Member == "Archived").ReferencedBy);
        else
            Assert.Empty(plan.Refused);
    }

    // ---- the object the interpreter sees ----

    private const string Party = "01JENT00000000000000000007"; // Customer's base in the tests below
    private const string PartyKind = "01JATT00000000000000000071";

    // OrderCustomer navigable both ways: Order.customer (to-one) and Customer.orders (to-many).
    private static void Navigable(LoaderHarness x) => x.Write("model/relations/order-customer.json", $$"""
        {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{Relation}}","name":"OrderCustomer",
         "ends":[{"id":"{{OrdersEnd}}","entity":"{{Order}}","role":"orders","navigation":"orders"},
                 {"id":"{{CustomerEnd}}","entity":"{{Customer}}","role":"customer","navigation":"customer","max":1}]}
        """);

    // Party, which declares kind (a string of at most 4, "anon" by default), and Customer derived from it.
    private static void Based(LoaderHarness x, string partyKind = "\"anon\"")
    {
        x.Write("model/entities/party.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Party}}","name":"Party","package":"{{Package}}","abstract":true,
             "attributes":[{"id":"{{PartyKind}}","name":"kind","type":"string","length":4,"default":{{partyKind}}}]}
            """);
        var customer = File(x, "model/entities/customer.json");
        customer["base"] = Party;
        x.Write("model/entities/customer.json", customer.ToJsonString());
    }

    private static void Objects(LoaderHarness x, string objects)
    {
        var file = File(x, ScenarioFile);
        file["objects"] = JsonNode.Parse(objects);
        x.Write(ScenarioFile, file.ToJsonString());
    }

    [Fact]
    public async Task The_subject_row_reads_values_over_the_declaring_bases_defaults_and_nests_links_by_navigation()
    {
        var (h, store) = await OpenAsync(prepare: x =>
        {
            Navigable(x);
            Based(x);
            // The order gives no status (Order, its base, declares Draft); the customer links back to the order, which is on the path.
            Objects(x, $$"""
                [{"id":"{{OrderObject}}","name":"order","entity":"{{RushOrder}}","links":[{"end":"{{CustomerEnd}}","object":"{{CustomerObject}}"}]},
                 {"id":"{{CustomerObject}}","name":"customer","entity":"{{Customer}}","values":{"{{CustomerName}}":"Ada"},
                  "links":[{"end":"{{OrdersEnd}}","object":"{{OrderObject}}"}]}]
                """);
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var scenario = model.Get<Scenario>(Sc)!;

        var row = ScenarioObjects.Row(model, scenario.Objects, scenario.Subject, 1);
        Assert.Equal("""{"customer":{"id":null,"kind":"anon","name":"Ada"},"id":null,"status":"Draft"}""", row.GetRawText());
        // The customer as the subject: its orders, a to-many end no condition can read, are left out.
        Assert.Equal("""{"id":null,"kind":"anon","name":"Ada"}""", ScenarioObjects.Row(model, scenario.Objects, CustomerObject, 1).GetRawText());
        Assert.Equal("{}", ScenarioObjects.Row(model, [], null, 1).GetRawText());

        // The replay's interpreter sees the row; no condition of the process reads the object, so it holds no links. A scenario without
        // objects gives an empty one.
        using var runtime = new ProcessRuntime(model, 1, Ct);
        var chart = runtime.Chart(P)!;
        var seen = Assert.Single(ScenarioReplayer.Interpreter(scenario, chart, runtime).SubjectsSeen);
        Assert.Equal((P, """{"id":null,"status":"Draft"}"""), (seen.Process, seen.Subject.GetRawText()));
        Assert.Equal("{}", Assert.Single(ScenarioReplayer.Interpreter(model.Get<Scenario>(HappyPath)!, chart, runtime).SubjectsSeen).Subject.GetRawText());
        Assert.True(ScenarioReplayer.Replay(scenario, runtime)!.Complete);
    }

    [Fact]
    public void An_invoked_sub_process_sees_no_object()
    {
        var child = new Process
        {
            Id = "Child", Name = "Child", States = [new ProcessState { Id = "Work", Name = "Work" }], Transitions = [],
        };
        var parent = new Process
        {
            Id = "Parent", Name = "Parent",
            States = [new ProcessState { Id = "Wait", Name = "Wait", Invoke = [new ProcessInvoke { Id = "sub", Name = "sub", Type = InvokeType.Process, Process = "Child" }] }],
            Transitions = [],
        };
        var charts = new Dictionary<string, StatechartModel> { ["Child"] = StatechartModel.Get(child, null) };
        var subject = JsonSerializer.SerializeToElement(new { status = "Draft" });
        var run = new StatechartInterpreter(StatechartModel.Get(parent, null),
            new InterpreterOptions { SubProcess = charts.GetValueOrDefault, Subject = subject });
        run.Start();

        Assert.Equal([("Parent", """{"status":"Draft"}"""), ("Child", "{}")], run.SubjectsSeen.Select(s => (s.Process, s.Subject.GetRawText())));
        Assert.Equal("{}", Assert.Single(new StatechartInterpreter(StatechartModel.Get(child, null)).SubjectsSeen).Subject.GetRawText());
    }

    // ---- replay and its reach ----

    [Theory]
    [InlineData("Archived", "/objects/0/values", "not a member")] // not a member of OrderStatus
    [InlineData("Draft", "/objects/1/values", "not an attribute")] // the customer gives a value for the order's status
    public async Task A_bad_object_value_is_MQ9305_and_stops_the_replay_without_refusing_a_save(string status, string pointer, string why)
    {
        var (h, store) = await OpenAsync(status, x =>
        {
            if (status == "Draft")
            {
                var file = File(x, ScenarioFile);
                file["objects"]![1]!["values"]![Status] = "Draft";
                x.Write(ScenarioFile, file.ToJsonString());
            }
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        using (var runtime = new ProcessRuntime(model, 1, Ct))
        {
            var replay = ScenarioReplayer.Replay(model.Get<Scenario>(Sc)!, runtime)!;
            var finding = Assert.Single(replay.Diagnostics);
            Assert.Equal(("MQ9305", pointer, Sc), (finding.Rule, finding.JsonPointer, finding.ElementId));
            Assert.Contains(why == "not an attribute" ? "not an attribute of entity 'Customer'" : "does not fit", finding.Message, StringComparison.Ordinal);
            Assert.False(replay.Complete);
            Assert.Empty(replay.Steps);
        }

        var findings = (await EngineServices.Create(h.Options).Validator.ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics;
        Assert.Contains(findings, d => d.Rule == "MQ9305" && d.ElementId == Sc && d.JsonPointer == pointer);

        // A replay finding: saving the scenario is not refused.
        var current = (await store.GetElementAsync(Sc, Ct))!;
        var node = File(h, ScenarioFile);
        node["displayName"] = "With objects";
        var saved = await store.SaveAsync(Sc, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), current.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
    }

    [Fact]
    public async Task A_change_to_an_objects_entity_or_the_base_that_declares_its_defaults_replays_the_scenario()
    {
        // The customer's kind is too long for Party's declaration; Customer is no subject of the process, so only the objects reach it.
        var (h, store) = await OpenAsync(prepare: x =>
        {
            Navigable(x);
            Based(x);
            var file = File(x, ScenarioFile);
            file["objects"]![1]!["values"]![PartyKind] = "person";
            x.Write(ScenarioFile, file.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var validator = EngineServices.Create(h.Options).Validator;

        foreach (var changed in new[] { Party, Customer, Relation })
        {
            var scoped = (await validator.ValidateAsync(model, new ValidationScope([changed]), null, Ct)).Diagnostics;
            Assert.Contains(scoped, d => d.Rule == "MQ9305" && d.ElementId == Sc && d.JsonPointer == "/objects/1/values");
            Assert.DoesNotContain(scoped, d => d.ElementId == HappyPath); // a scenario without objects is not reached through them
        }

        // A changed default (and length) on the declaring base, saved: the base's validation replays the scenario, which now passes.
        var party = (await store.GetElementAsync(Party, Ct))!;
        var node = File(h, "model/entities/party.json");
        node["attributes"]![0]!["default"] = "anyone";
        node["attributes"]![0]!["length"] = 6;
        var saved = await store.SaveAsync(Party, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), party.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var after = (await validator.ValidateAsync(store.Current!, new ValidationScope([Party]), null, Ct)).Diagnostics;
        Assert.DoesNotContain(after, d => d.Rule == "MQ9305" && d.ElementId == Sc);

        // The steps' trace reads the subjects' entities, the objects' entities with their bases, and the links' relations.
        var resolved = ResolutionKit.Resolve(store.Current!);
        var step = resolved.Scenarios.Single(x => x.Id == Sc).Steps[0];
        foreach (var id in new[] { Order, RushOrder, Customer, Party, Relation })
            Assert.Contains(step.Dependencies, k => k.Contains(id, StringComparison.Ordinal));
        var plain = resolved.Scenarios.Single(x => x.Id == HappyPath).Steps[0];
        Assert.DoesNotContain(plain.Dependencies, k => k.Contains(Customer, StringComparison.Ordinal));
    }

    // ---- what a project that uses none of this keeps, and how far the reads reach ----

    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string SubjectReader = "01JPRX00000000000000000098";

    // A guard of OrderLifecycle, on no transition, whose condition is the text given.
    private static void Guard(LoaderHarness x, string text)
    {
        var process = File(x, ProcessFile);
        process["guards"]!.AsArray().Add(new JsonObject { ["id"] = SubjectReader, ["name"] = "readsObject", ["condition"] = new JsonObject { ["text"] = text } });
        x.Write(ProcessFile, process.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("amount > 0")]
    [InlineData("subject.id is not null")]
    public async Task A_scenario_without_objects_reads_the_subjects_entity_only_when_a_condition_reads_the_object(string? condition)
    {
        var (h, store) = await OpenAsync(prepare: x =>
        {
            if (condition is not null)
                Guard(x, condition);
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var reads = condition?.StartsWith("subject.", StringComparison.Ordinal) == true;
        Assert.Equal(reads ? 0 : null, Maquettiste.Engine.Validation.ExprSites.SubjectDepth(model, model.Get<Process>(P)!));

        // Today's keys: the scenario's and its process's, and nothing of the subject's entity unless a condition reads the object.
        var plain = ResolutionKit.Resolve(model).Scenarios.Single(x => x.Id == HappyPath);
        foreach (var step in plain.Steps)
        {
            Assert.Equal(reads, step.Dependencies.Any(k => k.Contains(Order, StringComparison.Ordinal)));
            Assert.DoesNotContain(step.Dependencies, k => k.Contains(RushOrder, StringComparison.Ordinal) || k.Contains(Customer, StringComparison.Ordinal));
        }
    }

    // OrderCustomer navigable both ways, and two orders of one customer: the customer's orders link back to them.
    private static string CrossLinked => $$"""
        [{"id":"{{OrderObject}}","name":"order","entity":"{{RushOrder}}","links":[{"end":"{{CustomerEnd}}","object":"{{CustomerObject}}"}]},
         {"id":"{{CustomerObject}}","name":"customer","entity":"{{Customer}}","values":{"{{CustomerName}}":"Ada"},
          "links":[{"end":"{{OrdersEnd}}","object":"{{OrderObject}}"},{"end":"{{OrdersEnd}}","object":"01JXBJ00000000000000000003"}]},
         {"id":"01JXBJ00000000000000000003","name":"other","entity":"{{Order}}","links":[{"end":"{{CustomerEnd}}","object":"{{CustomerObject}}"}]}]
        """;

    [Theory]
    [InlineData(null, """{"id":null,"status":"Draft"}""")]
    [InlineData("subject.id is not null", """{"id":null,"status":"Draft"}""")]
    [InlineData("subject.customer.name = 'Ada'", """{"customer":{"id":null,"name":"Ada"},"id":null,"status":"Draft"}""")]
    public async Task The_row_nests_to_one_links_only_as_deep_as_a_condition_reads(string? condition, string expected)
    {
        var (h, store) = await OpenAsync(prepare: x =>
        {
            Navigable(x);
            Objects(x, CrossLinked);
            if (condition is not null)
                Guard(x, condition);
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var scenario = model.Get<Scenario>(Sc)!;

        Assert.Equal(expected, ScenarioObjects.Row(model, model.Get<Process>(P)!, scenario.Objects, scenario.Subject).GetRawText());
        using var runtime = new ProcessRuntime(model, 1, Ct);
        Assert.Equal(expected, Assert.Single(ScenarioReplayer.Interpreter(scenario, runtime.Chart(P)!, runtime).SubjectsSeen).Subject.GetRawText());

        // However deep, a to-many end (the customer's orders) is never nested, and an object already on the path is not nested again.
        Assert.Equal("""{"id":null,"name":"Ada"}""", ScenarioObjects.Row(model, scenario.Objects, CustomerObject, 9).GetRawText());
        Assert.Equal("""{"customer":{"id":null,"name":"Ada"},"id":null,"status":"Draft"}""", ScenarioObjects.Row(model, scenario.Objects, OrderObject, 9).GetRawText());
    }

    [Fact]
    public async Task Recording_twice_from_one_scenarios_objects_gives_each_recording_its_own_object_ids()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var objects = JsonDocument.Parse(File(h, ScenarioFile)["objects"]!.ToJsonString()).RootElement.EnumerateArray().Select(o => o.Clone()).ToList();
        var steps = new[] { JsonDocument.Parse("""{ "input": "event", "event": "01JPRX00000000000000000001" }""").RootElement.Clone() };

        var seen = new HashSet<string>(StringComparer.Ordinal) { OrderObject, CustomerObject };
        foreach (var name in new[] { "Again", "OnceMore" })
        {
            var call = await store.RecordScenarioAsync(P, new RecordScenarioRequest { Name = name, Objects = objects, Subject = OrderObject, Steps = steps },
                dryRun: false, ChangeSource.Editor, Ct);
            Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail + " " + string.Join(" | ", call.Diagnostics.Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
            var recorded = store.Current!.Get<Scenario>(call.Value!.Id)!;
            var order = recorded.Objects.Single(o => o.Name == "order");
            var customer = recorded.Objects.Single(o => o.Name == "customer");
            Assert.True(seen.Add(order.Id) && seen.Add(customer.Id), "each recording mints its own object ids");
            Assert.Equal(order.Id, recorded.Subject);
            var link = Assert.Single(order.Links);
            Assert.Equal((CustomerEnd, customer.Id), (link.End, link.Object));
            Assert.Equal("Ada", customer.Values[CustomerName].GetString());
        }
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_a_relation_a_link_goes_through_drops_the_link(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var plan = await store.GetDeletePlanAsync([Relation], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, string.Join(" | ", plan.Refused.Select(r => r.Rule + " " + r.Pointer + " " + r.Why)));
        Assert.Contains(plan.Removes, r => r.Id == Sc && r.Pointer == "/objects/0/links/0");
        Assert.DoesNotContain(plan.Deletes, d => d.Id == Sc);

        var hash = (await store.GetElementAsync(Relation, Ct))!.Hash;
        Assert.Equal(SaveOutcome.Saved, (await store.DeleteAsync(Relation, hash, resolution, ChangeSource.Editor, Ct)).Outcome);
        var file = File(h, ScenarioFile);
        Assert.Equal(2, file["objects"]!.AsArray().Count);
        Assert.False(file["objects"]![0]!.AsObject().ContainsKey("links"));
        Assert.Equal(OrderObject, file["subject"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("subject")] // the customer, whose entity is no subject of OrderLifecycle
    [InlineData("end")] // the order links to the customer through the end that leads to orders
    [InlineData("name")] // two objects named order
    public async Task A_cast_that_does_not_fit_its_process_or_its_relations_is_a_finding(string what)
    {
        var (h, store) = await OpenAsync(prepare: x =>
        {
            var file = File(x, ScenarioFile);
            if (what == "subject")
                file["subject"] = CustomerObject;
            else if (what == "end")
                file["objects"]![0]!["links"]![0]!["end"] = OrdersEnd;
            else
                file["objects"]![1]!["name"] = "order";
            x.Write(ScenarioFile, file.ToJsonString());
        }, errors: what == "name");
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        var findings = (await EngineServices.Create(h.Options).Validator.ValidateAsync(model, ValidationScope.All, null, Ct)).Diagnostics
            .Where(d => d.ElementId is Sc or OrderObject or CustomerObject).ToList();
        var (rule, pointer) = what switch
        {
            "subject" => ("MQ9305", "/subject"),
            "end" => ("MQ9305", "/objects/0/links/0/end"),
            _ => ("MQ3001", "/objects/1/name"),
        };
        var finding = Assert.Single(findings, d => d.Rule == rule);
        Assert.Equal(pointer, finding.JsonPointer);
        if (what == "name")
            return;
        using var runtime = new ProcessRuntime(model, 1, Ct);
        Assert.False(ScenarioReplayer.Replay(model.Get<Scenario>(Sc)!, runtime)!.Complete);
    }

    [Fact]
    public async Task A_change_to_a_reference_type_an_objects_attribute_has_replays_the_scenario()
    {
        const string Tier = "01JRFT00000000000000000001";
        var (h, store) = await OpenAsync(prepare: x =>
        {
            x.Write("model/reference-types/tier.json", $$"""
                {"$schema":"../../.schema/v1/reference-type.json","kind":"reference-type","id":"{{Tier}}","name":"Tier","package":"{{Package}}",
                 "code":{"id":"01JATT00000000000000000081","length":8},"label":{"id":"01JATT00000000000000000082","length":32} }
                """);
            var customer = File(x, "model/entities/customer.json");
            customer["attributes"]!.AsArray().Add(new JsonObject { ["id"] = "01JATT00000000000000000083", ["name"] = "tier", ["type"] = new JsonObject { ["ref"] = Tier } });
            x.Write("model/entities/customer.json", customer.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        var peers = Maquettiste.Engine.Validation.ModelValidator.ProcessPeers(model, model.Get<ReferenceType>(Tier)!).Select(d => d.Element.Id).ToList();
        Assert.Contains(Sc, peers);
        Assert.DoesNotContain(HappyPath, peers);
    }

    [Fact]
    public async Task A_sub_process_whose_condition_reads_its_subject_brings_its_subjects_entity_into_the_parents_replays()
    {
        const string Child = "01JPRC00000000000000000002";
        var (h, store) = await OpenAsync(prepare: x =>
        {
            Based(x);
            x.Write("model/processes/customer-check.json", $$"""
                {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{Child}}","name":"CustomerCheck","package":"{{Package}}","use":"lifecycle",
                 "subjects":[{"id":"01JPSB00000000000000000002","entity":"{{Customer}}"}],
                 "events":[{"id":"01JPRX00000000000000000201","name":"check"}],
                 "guards":[{"id":"01JPRX00000000000000000202","name":"named","condition":{"text":"subject.name = 'Ada'"} }],
                 "states":[{"id":"01JSTA00000000000000000201","name":"Open"},{"id":"01JSTA00000000000000000202","name":"Done","type":"final"}],
                 "initial":"01JSTA00000000000000000201",
                 "transitions":[{"id":"01JTRN00000000000000000201","source":"01JSTA00000000000000000201","event":"01JPRX00000000000000000201","guard":"01JPRX00000000000000000202","targets":["01JSTA00000000000000000202"]}]}
                """);
            var process = File(x, ProcessFile);
            process["states"]![0]!["invoke"] = JsonNode.Parse($$"""[{"id":"01JPRX00000000000000000203","name":"check","type":"process","process":"{{Child}}"}]""");
            x.Write(ProcessFile, process.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        // A change to the sub-process's subject entity, or the base that declares its defaults, replays the parent's scenarios.
        foreach (var changed in new[] { Customer, Party })
            Assert.Contains(Maquettiste.Engine.Validation.ModelValidator.ProcessPeers(model, model.Get<Entity>(changed)!), d => d.Element.Id == HappyPath);
        var steps = ResolutionKit.Resolve(model).Scenarios.Single(x => x.Id == HappyPath).Steps;
        Assert.All(steps, step => Assert.Contains(step.Dependencies, k => k.Contains(Customer, StringComparison.Ordinal)));
        Assert.All(steps, step => Assert.Contains(step.Dependencies, k => k.Contains(Party, StringComparison.Ordinal)));
        Assert.All(steps, step => Assert.DoesNotContain(step.Dependencies, k => k.Contains(Order, StringComparison.Ordinal))); // the parent reads no object
    }
}
