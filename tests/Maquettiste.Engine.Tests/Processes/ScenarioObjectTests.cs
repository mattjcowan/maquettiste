using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
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
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string status = "Draft", Action<LoaderHarness>? prepare = null)
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
}
