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
/// References and deletes of object effects (object-effects-design.md section 8) over the <c>process-basics</c> fixture: a set's attribute,
/// the ids in its value, an <c>in_state</c>'s states and ends, and a scenario object's <c>states</c> are references that
/// <c>get_references</c> and <c>referenced_by</c> see; deleting what a set names drops the set in either resolution, an id in a set's value
/// or an <c>in_state</c> is read as an id in a condition is, and an object's <c>states</c> entry goes with remove-references; and an
/// action's sets travel through an XState export and re-import.
/// </summary>
public sealed class ObjectEffectsReferenceTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, bound to Order.status
    private const string C = "01JPRC00000000000000000005"; // CustomerLifecycle, subjects [Customer]
    private const string Order = "01JENT00000000000000000001";
    private const string Customer = "01JENT00000000000000000005";
    private const string Supplier = "01JENT00000000000000000006"; // C's second subject, so C keeps one when Customer goes
    private const string CustomerName = "01JATT00000000000000000052";
    private const string SignedBy = "01JATT00000000000000000053";
    private const string Note = "01JATT00000000000000000054";
    private const string Relation = "01JRXN00000000000000000001"; // OrderCustomer
    private const string CustomerEnd = "01JEND00000000000000000002";
    private const string Active = "01JSTA00000000000000000051";
    private const string Left = "01JSTA00000000000000000052";
    private const string Sign = "01JPRX00000000000000000062";
    private const string SetA = "01JSET00000000000000000001"; // signedBy = actor()
    private const string SetB = "01JSET00000000000000000002"; // note = name, or a literal
    private const string Guard = "01JPRX00000000000000000063"; // in_state(subject.customer, CustomerLifecycle.Active)
    private const string Sc = "01JSCN00000000000000000009";
    private const string OrderObject = "01JXBJ00000000000000000001";
    private const string CustomerObject = "01JXBJ00000000000000000002";
    private const string Package = "01JPKG00000000000000000001";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string CFile = "model/processes/customer-lifecycle.json";
    private const string ScenarioFile = "model/scenarios/order-lifecycle/with-customer.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Customer (and Supplier) with their lifecycle, whose action sign sets signedBy and note; OrderCustomer; optionally a guard of OrderLifecycle that
    // reads the customer's state, and a scenario whose customer object gives its state.
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(bool readsName = true, bool guard = true, bool scenario = true)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/customer.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}",
             "key":{"attributes":["01JATT00000000000000000051"]},
             "attributes":[{"id":"01JATT00000000000000000051","name":"id","type":"uuid"},{"id":"{{CustomerName}}","name":"name","type":"string","length":40},
               {"id":"{{SignedBy}}","name":"signedBy","type":"string","length":40},{"id":"{{Note}}","name":"note","type":"string","length":40}]}
            """);
        harness.Write("model/entities/supplier.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Supplier}}","name":"Supplier","package":"{{Package}}",
             "key":{"attributes":["01JATT00000000000000000061"]},"attributes":[{"id":"01JATT00000000000000000061","name":"id","type":"uuid"}]}
            """);
        harness.Write("model/relations/order-customer.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{Relation}}","name":"OrderCustomer",
             "ends":[{"id":"01JEND00000000000000000001","entity":"{{Order}}","role":"orders"},{"id":"{{CustomerEnd}}","entity":"{{Customer}}","role":"customer","max":1}]}
            """);
        var value = readsName ? $$"""{"path":["{{CustomerName}}"]}""" : """{"value":"seen"}""";
        harness.Write(CFile, $$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{C}}","name":"CustomerLifecycle","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000005","entity":"{{Customer}}"},{"id":"01JSBJ00000000000000000006","entity":"{{Supplier}}"}],
             "events":[{"id":"01JPRX00000000000000000061","name":"leave"}],
             "actions":[{"id":"{{Sign}}","name":"sign","sets":[
               {"id":"{{SetA}}","attribute":"{{SignedBy}}","value":{"actor":true} },
               {"id":"{{SetB}}","attribute":"{{Note}}","value":{{value}} }]}],
             "states":[{"id":"{{Active}}","name":"Active"},{"id":"{{Left}}","name":"Left","type":"final"}],
             "initial":"{{Active}}",
             "transitions":[{"id":"01JTRN00000000000000000051","source":"{{Active}}","event":"01JPRX00000000000000000061","targets":["{{Left}}"],"actions":["{{Sign}}"]}]}
            """);
        if (guard)
        {
            var process = JsonNode.Parse(harness.Read(ProcessFile))!.AsObject();
            process["guards"]!.AsArray().Add(JsonNode.Parse($$"""
                {"id":"{{Guard}}","name":"customerActive","condition":{"inState":["{{Active}}"],"of":["{{CustomerEnd}}"]} }
                """));
            harness.Write(ProcessFile, process.ToJsonString());
        }

        if (scenario)
        {
            harness.Write(ScenarioFile, $$"""
                {"$schema":"../../../.schema/v1/scenario.json","kind":"scenario","id":"{{Sc}}","name":"WithCustomer","process":"{{P}}",
                 "objects":[
                   {"id":"{{OrderObject}}","name":"order","entity":"{{Order}}","links":[{"end":"{{CustomerEnd}}","object":"{{CustomerObject}}"}]},
                   {"id":"{{CustomerObject}}","name":"customer","entity":"{{Customer}}","states":["{{Active}}"]}],
                 "subject":"{{OrderObject}}",
                 "steps":[{"id":"01JSTP00000000000000000091","event":"01JPRX00000000000000000001"}]}
                """);
        }

        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonObject File(LoaderHarness h, string path) => JsonNode.Parse(h.Read(path))!.AsObject();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string Why(DeletePlan plan) => string.Join(" | ", plan.Refused.Select(r => r.Id + " " + r.Pointer + " " + r.Why));

    private static async Task Applied(ModelStore store, DeleteResolution resolution, params string[] ids)
    {
        var operations = new List<BatchOperation>();
        foreach (var id in ids)
            operations.Add(new BatchOperation(BatchOp.Delete, id, (await store.GetElementAsync(id, Ct))!.Hash, null, Resolution: resolution));
        var result = await store.ApplyBatchAsync(new ModelBatch(operations), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
    }

    // ---- references ----

    [Fact]
    public async Task Sets_in_state_and_object_states_are_references_get_references_and_referenced_by_see()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        // A set is a part of the process with an index kind of its own.
        Assert.Equal(("set", C), (model.Index.Entries[SetA].Kind, model.Index.Entries[SetA].OwnerId));
        Assert.Equal("/actions/0/sets/1", model.Index.Entries[SetB].JsonPointer);

        // A set's attribute and the ids in its value.
        Assert.Contains(await store.GetReferencesAsync(SignedBy, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (C, SetA, "/actions/0/sets/0/attribute"));
        Assert.Contains(await store.GetReferencesAsync(CustomerName, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (C, SetB, "/actions/0/sets/1/value/path/0"));
        Assert.Contains(await store.GetReferencesAsync(Note, Ct), r => (r.FromElementId, r.JsonPointer) == (C, "/actions/0/sets/1/attribute"));

        // An in_state's states and ends, and an object's states.
        var active = await store.GetReferencesAsync(Active, Ct);
        Assert.Contains(active, r => (r.FromElementId, r.FromId, r.JsonPointer) == (P, Guard, "/guards/1/condition/inState/0"));
        Assert.Contains(active, r => (r.FromElementId, r.FromId, r.JsonPointer) == (Sc, CustomerObject, "/objects/1/states/0"));
        Assert.Contains(await store.GetReferencesAsync(CustomerEnd, Ct), r => (r.FromElementId, r.JsonPointer) == (P, "/guards/1/condition/of/0"));

        // referenced_by and declared rules read the same links, without the process's own parts.
        Assert.Equal([(P, "/guards/1/condition/inState/0"), (Sc, "/objects/1/states/0")],
            model.LinksTo(Active).Where(r => r.FromElementId != C).Select(r => (r.FromElementId, r.JsonPointer)).Order().ToArray());
        Assert.Contains(model.LinksTo(SignedBy), r => r.FromElementId == C);

        // Deleting what they name with refuse names them.
        var refused = await store.DeleteAsync(C, model.GetDocument(C)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
        Assert.Equal([P, Sc], refused.Referrers.Select(r => r.FromElementId).Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    // ---- deletes ----

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_the_attribute_a_set_names_drops_the_set_in_either_resolution(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync(readsName: false, guard: false, scenario: false);
        using var _ = h;
        await using var __ = store;

        var refused = await store.DeleteAsync(Customer, store.Current!.GetDocument(Customer)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
        Assert.Contains(refused.Referrers, r => r.FromElementId == C && r.JsonPointer == "/actions/0/sets/0/attribute");

        // The relation goes too: remove-references refuses to leave it with one end.
        var plan = await store.GetDeletePlanAsync([Customer, Relation], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Contains(plan.Removes, r => r.Id == C && r.Pointer == "/actions/0/sets/0");
        Assert.Contains(plan.Removes, r => r.Id == C && r.Pointer == "/actions/0/sets/1");
        Assert.DoesNotContain(plan.Deletes, d => d.Id == C);

        await Applied(store, resolution, Customer, Relation);
        var action = Assert.Single(File(h, CFile)["actions"]!.AsArray())!.AsObject();
        Assert.Equal((Sign, "sign"), (action["id"]!.GetValue<string>(), action["name"]!.GetValue<string>()));
        Assert.False(action.ContainsKey("sets")); // the emptied list is the default, so the file leaves it out
        Assert.Empty(store.Current!.ReferencesTo(SignedBy));
    }

    [Fact]
    public async Task An_id_in_a_sets_value_refuses_remove_references_and_goes_with_its_set_under_delete_dependents()
    {
        var (h, store) = await OpenAsync(guard: false, scenario: false);
        using var _ = h;
        await using var __ = store;

        var refused = await store.GetDeletePlanAsync([Customer, Relation], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var why = Assert.Single(refused.Refused, r => r.Id == C);
        Assert.Equal("/actions/0/sets/1/value/path/0", why.Pointer);
        Assert.Contains("remove the set that holds it", why.Why, StringComparison.Ordinal);
        Assert.Contains(refused.Removes, r => r.Id == C && r.Pointer == "/actions/0/sets/0"); // the set of the attribute still goes

        var cascade = await store.GetDeletePlanAsync([Customer, Relation], DeleteResolution.DeleteDependents, Ct);
        Assert.True(cascade.Outcome == SaveOutcome.Saved, Why(cascade));
        Assert.Contains(cascade.Removes, r => r.Id == C && r.Pointer == "/actions/0/sets/1");
        Assert.DoesNotContain(cascade.Deletes, d => d.Id == C);
    }

    [Fact]
    public async Task A_state_or_end_an_in_state_reads_is_read_as_an_id_in_a_condition()
    {
        var (h, store) = await OpenAsync(scenario: false);
        using var _ = h;
        await using var __ = store;

        // The lifecycle whose state the guard reads.
        var refused = await store.GetDeletePlanAsync([C], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var why = Assert.Single(refused.Refused, r => r.Id == P);
        Assert.Equal("/guards/1/condition/inState/0", why.Pointer);
        Assert.Contains("remove the check or guard that holds it", why.Why, StringComparison.Ordinal);
        var cascade = await store.GetDeletePlanAsync([C], DeleteResolution.DeleteDependents, Ct);
        Assert.True(cascade.Outcome == SaveOutcome.Saved, Why(cascade));
        Assert.Contains(cascade.Removes, r => r.Id == P && r.Pointer == "/guards/1");

        // The relation whose end its of follows.
        var end = await store.GetDeletePlanAsync([Relation], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal("/guards/1/condition/of/0", Assert.Single(end.Refused, r => r.Id == P).Pointer);
        var endCascade = await store.GetDeletePlanAsync([Relation], DeleteResolution.DeleteDependents, Ct);
        Assert.True(endCascade.Outcome == SaveOutcome.Saved, Why(endCascade));
        Assert.Contains(endCascade.Removes, r => r.Id == P && r.Pointer == "/guards/1");

        await Applied(store, DeleteResolution.DeleteDependents, Relation);
        Assert.Equal(["isLarge"], File(h, ProcessFile)["guards"]!.AsArray().Select(g => g!["name"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task An_objects_states_entry_goes_with_its_lifecycle(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync(guard: false);
        using var _ = h;
        await using var __ = store;

        var plan = await store.GetDeletePlanAsync([C], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Contains(plan.Clears, c => c.Id == Sc && c.Pointer == "/objects/1/states/0");
        Assert.DoesNotContain(plan.Deletes, d => d.Id == Sc);

        await Applied(store, resolution, C);
        var customer = File(h, ScenarioFile)["objects"]!.AsArray()[1]!.AsObject();
        Assert.Equal(CustomerObject, customer["id"]!.GetValue<string>());
        Assert.False(customer.ContainsKey("states"));
        Assert.Empty(store.Current!.ReferencesTo(Active));
    }

    // ---- XState ----

    [Fact]
    public async Task An_actions_sets_travel_through_an_export_and_a_re_import()
    {
        var (h, store) = await OpenAsync(guard: false, scenario: false);
        using var _ = h;
        await using var __ = store;

        await store.FormatAsync([CFile], ChangeSource.Editor, Ct); // canonical bytes, as every write leaves them
        var before = h.Read(CFile);
        var export = (await store.ExportProcessAsync(C, Ct)).Value!;
        var config = JsonNode.Parse(export.Json)!.AsObject();
        var sets = config["meta"]!["maquettiste"]!["actions"]![0]!["sets"]!.AsArray();
        Assert.Equal([SetA, SetB], sets.Select(s => s!["id"]!.GetValue<string>()));
        Assert.Contains(export.Diagnostics, d => d.Rule == "MQ9406" && d.Message.Contains("the object's data actions set", StringComparison.Ordinal));

        var again = await store.ImportProcessAsync(new ProcessImportRequest { Config = Json(export.Json), Into = C }, dryRun: false, ChangeSource.Editor, Ct);
        Assert.True(again.Status == ProcessCallStatus.Ok, string.Join(" | ", again.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(before, h.Read(CFile)); // the bytes: a dropped or reordered set fails

        // A set given without an id takes the id of the set of its attribute in the process it goes into.
        sets[0]!.AsObject().Remove("id");
        var dry = await store.ImportProcessAsync(new ProcessImportRequest { Config = Json(config.ToJsonString()), Into = C }, dryRun: true, ChangeSource.Editor, Ct);
        Assert.True(dry.Status == ProcessCallStatus.Ok, string.Join(" | ", dry.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var imported = dry.Value!.Document!.Value.GetProperty("actions")[0].GetProperty("sets");
        Assert.Equal([SetA, SetB], imported.EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(SignedBy, imported[0].GetProperty("attribute").GetString());
    }
}
