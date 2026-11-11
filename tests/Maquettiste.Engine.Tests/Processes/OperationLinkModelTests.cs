using System.Text;
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
/// The model side of processes linked to operations (processes-operations-design.md sections 6 and 11): an action's <c>call</c> and a
/// service step's <c>operation</c> load, keep their canonical order through <c>format</c>, mint their inputs' ids, are references that
/// <c>get_references</c> sees and qualified names reach, go with what they name in either delete resolution, and travel through an XState
/// export and re-import; the invoice example of section 6 validates against the schemas.
/// </summary>
public sealed class OperationLinkModelTests
{
    private const string C = "01JPRC00000000000000000005"; // CustomerLifecycle, subjects [Customer, Supplier]
    private const string Customer = "01JENT00000000000000000005";
    private const string Supplier = "01JENT00000000000000000006";
    private const string CustomerName = "01JATT00000000000000000052";
    private const string Op = "01JQPN00000000000000000001"; // notify, no subject
    private const string Note = "01JQPN00000000000000000002"; // notify.note, string
    private const string Count = "01JQPN00000000000000000003"; // notify.count, int32, required
    private const string Other = "01JQPN00000000000000000011"; // ping, no subject
    private const string OtherMember = "01JQPN00000000000000000012"; // ping.level
    private const string Notify = "01JPRX00000000000000000062"; // the action that calls notify
    private const string InputA = "01JCXN00000000000000000001"; // count = 3
    private const string InputB = "01JCXN00000000000000000002"; // note = name
    private const string Step = "01JPRX00000000000000000071"; // the service step in Active
    private const string Active = "01JSTA00000000000000000051";
    private const string Package = "01JPKG00000000000000000001";
    private const string CFile = "model/processes/customer-lifecycle.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(string? process = null)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/customer.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}",
             "key":{"attributes":["01JATT00000000000000000051"]},
             "attributes":[{"id":"01JATT00000000000000000051","name":"id","type":"uuid"},{"id":"{{CustomerName}}","name":"name","type":"string","length":40}]}
            """);
        harness.Write("model/entities/supplier.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Supplier}}","name":"Supplier","package":"{{Package}}",
             "key":{"attributes":["01JATT00000000000000000061"]},"attributes":[{"id":"01JATT00000000000000000061","name":"id","type":"uuid"}]}
            """);
        harness.Write("model/operations/notify.json", $$"""
            {"$schema":"../../.schema/v1/operation.json","kind":"operation","id":"{{Op}}","name":"notify","package":"{{Package}}","effect":"change",
             "input":[{"id":"{{Note}}","name":"note","type":"string","length":40},{"id":"{{Count}}","name":"count","type":"int32","required":true}],
             "outcomes":[{"id":"01JQPN00000000000000000004","name":"sent"}]}
            """);
        harness.Write("model/operations/ping.json", $$"""
            {"$schema":"../../.schema/v1/operation.json","kind":"operation","id":"{{Other}}","name":"ping","package":"{{Package}}","effect":"change",
             "input":[{"id":"{{OtherMember}}","name":"level","type":"int32"}],
             "outcomes":[{"id":"01JQPN00000000000000000014","name":"done"}]}
            """);
        harness.Write(CFile, process ?? Lifecycle());
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        var errors = store.Current!.LoadDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(d => d.Rule + " " + d.FilePath + " " + d.JsonPointer + " " + d.Message)));
        return (harness, store);
    }

    // The action notify calls notify with count = 3 and note = the customer's name; the state Active runs a service step named by its
    // operation. Keys are written out of canonical order on purpose: format puts them back.
    private static string Lifecycle(string? inputB = null, string call = "") => $$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{C}}","name":"CustomerLifecycle","package":"{{Package}}","use":"lifecycle",
         "subjects":[{"id":"01JSBJ00000000000000000005","entity":"{{Customer}}"},{"id":"01JSBJ00000000000000000006","entity":"{{Supplier}}"}],
         "events":[{"id":"01JPRX00000000000000000061","name":"leave"}],
         "actions":[{"id":"{{Notify}}","name":"notify",{{call}}"call":{"inputs":[
             {"value":{"value":3},"member":"{{Count}}","id":"{{InputA}}"},
             {{inputB ?? $$"""{"id":"{{InputB}}","member":"{{Note}}","value":{"path":["{{CustomerName}}"]} }"""}}],
           "operation":"{{Op}}"} }],
         "states":[{"id":"{{Active}}","name":"Active","invoke":[{"id":"{{Step}}","operation":"{{Op}}","name":"remind","type":"service"}]},
           {"id":"01JSTA00000000000000000052","name":"Left","type":"final"}],
         "initial":"{{Active}}",
         "transitions":[{"id":"01JTRN00000000000000000051","source":"{{Active}}","event":"01JPRX00000000000000000061","targets":["01JSTA00000000000000000052"],"actions":["{{Notify}}"]}]}
        """;

    private static JsonObject File(LoaderHarness h, string path) => JsonNode.Parse(h.Read(path))!.AsObject();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string Why(DeletePlan plan) => string.Join(" | ", plan.Refused.Select(r => r.Id + " " + r.Pointer + " " + r.Why));

    private static string Problems(SaveResult result) => string.Join(" | ", result.Diagnostics.Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message));

    private static async Task Applied(ModelStore store, DeleteResolution resolution, params string[] ids)
    {
        var operations = new List<BatchOperation>();
        foreach (var id in ids)
            operations.Add(new BatchOperation(BatchOp.Delete, id, (await store.GetElementAsync(id, Ct))!.Hash, null, Resolution: resolution));
        var result = await store.ApplyBatchAsync(new ModelBatch(operations), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
    }

    // ---- loading, format, ids ----

    [Fact]
    public async Task A_call_and_a_steps_operation_load_and_format_writes_them_in_canonical_order()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var process = store.Current!.Get<Process>(C)!;
        var call = Assert.IsType<ProcessCall>(process.Actions[0].Call);
        Assert.Equal(Op, call.Operation);
        Assert.Equal([(InputA, Count), (InputB, Note)], call.Inputs.Select(i => (i.Id, i.Member)));
        Assert.Equal(3, call.Inputs[0].Value.Value!.Value.GetInt32());
        Assert.Equal(Op, process.States[0].Invoke[0].Operation);
        Assert.Null(process.States[0].Invoke[0].Process);

        await store.FormatAsync([CFile], ChangeSource.Editor, Ct);
        var formatted = File(h, CFile);
        var action = formatted["actions"]![0]!.AsObject();
        Assert.Equal(["id", "name", "call"], action.Select(p => p.Key));
        Assert.Equal(["operation", "inputs"], action["call"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "member", "value"], action["call"]!["inputs"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["id", "name", "type", "operation"], formatted["states"]![0]!["invoke"]![0]!.AsObject().Select(p => p.Key));

        // Once canonical, format changes no byte, and the model reads the same.
        var before = h.Read(CFile);
        await store.FormatAsync([CFile], ChangeSource.Editor, Ct);
        Assert.Equal(before, h.Read(CFile));
        Assert.Equal([InputA, InputB], store.Current!.Get<Process>(C)!.Actions[0].Call!.Inputs.Select(i => i.Id));
    }

    [Fact]
    public async Task An_empty_inputs_list_is_left_out_by_format_and_a_call_needs_its_operation()
    {
        var empty = JsonNode.Parse(Lifecycle())!.AsObject();
        empty["actions"]![0]!["call"]!.AsObject()["inputs"] = new JsonArray();
        var (h, store) = await OpenAsync(empty.ToJsonString());
        using (h)
        await using (store)
        {
            // A call with no inputs keeps only its operation once formatted, as an action keeps no empty sets.
            await store.FormatAsync([CFile], ChangeSource.Editor, Ct);
            var action = File(h, CFile)["actions"]![0]!.AsObject();
            Assert.Equal(["id", "name", "call"], action.Select(p => p.Key));
            Assert.Equal(["operation"], action["call"]!.AsObject().Select(p => p.Key));
            Assert.Empty(store.Current!.Get<Process>(C)!.Actions[0].Call!.Inputs);
        }

        // Without its operation, a call fails the schema.
        var missing = JsonNode.Parse(Lifecycle())!.AsObject();
        missing["actions"]![0]!["call"]!.AsObject().Remove("operation");
        using var h2 = new LoaderHarness();
        h2.CopyFixture("models", "process-basics");
        h2.Write(CFile, missing.ToJsonString());
        await using var store2 = new ModelStore(h2.Options, h2.Services());
        await store2.LoadAsync(Ct);
        Assert.Contains(store2.Current!.LoadDiagnostics, d => d.Rule == "MQ1002" && d.FilePath!.EndsWith("customer-lifecycle.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_saved_call_input_without_an_id_gets_one_and_qualified_names_reach_the_operation_and_its_member()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var document = File(h, CFile);
        var call = document["actions"]![0]!["call"]!.AsObject();
        call["operation"] = "@operation:Sales.notify";
        call["inputs"] = new JsonArray(new JsonObject { ["member"] = "@operation-member:Sales.notify.count", ["value"] = new JsonObject { ["value"] = 4 } });
        document["states"]![0]!["invoke"]![0]!["operation"] = "@operation:Sales.ping";
        var hash = (await store.GetElementAsync(C, Ct))!.Hash;
        var result = await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.Update, C, hash, Json(document.ToJsonString()))]), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

        var saved = store.Current!.Get<Process>(C)!;
        var input = Assert.Single(saved.Actions[0].Call!.Inputs);
        Assert.Equal((Op, Count), (saved.Actions[0].Call!.Operation, input.Member));
        Assert.True(IdFormat.IsValid(input.Id));
        Assert.Equal(("call-input", C, "/actions/0/call/inputs/0"),
            (store.Current!.Index.Entries[input.Id].Kind, store.Current!.Index.Entries[input.Id].OwnerId, store.Current!.Index.Entries[input.Id].JsonPointer));
        Assert.Equal(Other, saved.States[0].Invoke[0].Operation);
    }

    // ---- references ----

    [Fact]
    public async Task A_calls_operation_and_members_and_a_steps_operation_are_references()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        // A call input is a part of the process with an index kind of its own; the call is no part.
        Assert.Equal(("call-input", C, "/actions/0/call/inputs/1"), (model.Index.Entries[InputB].Kind, model.Index.Entries[InputB].OwnerId, model.Index.Entries[InputB].JsonPointer));

        var toOperation = await store.GetReferencesAsync(Op, Ct);
        Assert.Contains(toOperation, r => (r.FromElementId, r.FromId, r.JsonPointer, r.Field) == (C, Notify, "/actions/0/call/operation", "operation"));
        Assert.Contains(toOperation, r => (r.FromElementId, r.FromId, r.JsonPointer, r.Field) == (C, Step, "/states/0/invoke/0/operation", "operation"));
        Assert.Contains(await store.GetReferencesAsync(Count, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (C, InputA, "/actions/0/call/inputs/0/member"));
        Assert.Contains(await store.GetReferencesAsync(CustomerName, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (C, InputB, "/actions/0/call/inputs/1/value/path/0"));

        // A rename of the operation changes no byte of the process: references are ids.
        var before = h.Read(CFile);
        var operation = File(h, "model/operations/notify.json");
        operation["name"] = "remind";
        var saved = await store.SaveAsync(Op, Encoding.UTF8.GetBytes(operation.ToJsonString()), (await store.GetElementAsync(Op, Ct))!.Hash, ChangeSource.Editor, Ct);
        Assert.True(saved.Outcome == SaveOutcome.Saved, Problems(saved));
        Assert.Equal(before, h.Read(CFile));
        Assert.Contains(store.Current!.ReferencesTo(Op), r => r.FromElementId == C);

        // Deleting the operation with refuse names the process.
        var refused = await store.DeleteAsync(Op, store.Current!.GetDocument(Op)!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
        Assert.NotEqual(SaveOutcome.Saved, refused.Outcome);
        Assert.Contains(refused.Referrers, r => r.FromElementId == C && r.JsonPointer == "/actions/0/call/operation");
    }

    // ---- deletes ----

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_the_called_operation_removes_the_call_and_clears_the_steps_operation_in_either_resolution(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var plan = await store.GetDeletePlanAsync([Op], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Contains(plan.Removes, r => r.Id == C && r.Pointer == "/actions/0/call");
        Assert.DoesNotContain(plan.Removes, r => r.Id == C && r.Pointer.StartsWith("/actions/0/call/", StringComparison.Ordinal));
        Assert.Contains(plan.Clears, c => c.Id == C && c.Pointer == "/states/0/invoke/0/operation");
        Assert.DoesNotContain(plan.Deletes, d => d.Id == C);

        await Applied(store, resolution, Op);
        var file = File(h, CFile);
        var action = Assert.Single(file["actions"]!.AsArray())!.AsObject();
        Assert.Equal(["id", "name"], action.Select(p => p.Key)); // the action stays, a stub again
        var step = file["states"]![0]!["invoke"]![0]!.AsObject();
        Assert.Equal((Step, "service"), (step["id"]!.GetValue<string>(), step["type"]!.GetValue<string>()));
        Assert.False(step.ContainsKey("operation"));
        Assert.Empty(store.Current!.ReferencesTo(Op));
        Assert.False(store.Current!.Index.Entries.ContainsKey(InputA));
    }

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_what_an_inputs_member_names_removes_the_input_and_keeps_the_call(DeleteResolution resolution)
    {
        // An input naming another operation's member (MQ9025 territory): when that operation goes, only the input goes.
        var (h, store) = await OpenAsync(Lifecycle(inputB: $$"""{"id":"{{InputB}}","member":"{{OtherMember}}","value":{"value":1} }"""));
        using var _ = h;
        await using var __ = store;

        var plan = await store.GetDeletePlanAsync([Other], resolution, Ct);
        Assert.True(plan.Outcome == SaveOutcome.Saved, Why(plan));
        Assert.Contains(plan.Removes, r => r.Id == C && r.Pointer == "/actions/0/call/inputs/1");
        Assert.DoesNotContain(plan.Removes, r => r.Id == C && r.Pointer == "/actions/0/call");

        await Applied(store, resolution, Other);
        var call = store.Current!.Get<Process>(C)!.Actions[0].Call!;
        Assert.Equal(Op, call.Operation);
        Assert.Equal([InputA], call.Inputs.Select(i => i.Id));
    }

    [Fact]
    public async Task An_id_in_an_inputs_value_refuses_remove_references_and_goes_with_its_input_under_delete_dependents()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var refused = await store.GetDeletePlanAsync([Customer], DeleteResolution.RemoveReferences, Ct);
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var why = Assert.Single(refused.Refused, r => r.Id == C);
        Assert.Equal("/actions/0/call/inputs/1/value/path/0", why.Pointer);
        Assert.Contains("remove the call's input that holds it", why.Why, StringComparison.Ordinal);

        var cascade = await store.GetDeletePlanAsync([Customer], DeleteResolution.DeleteDependents, Ct);
        Assert.True(cascade.Outcome == SaveOutcome.Saved, Why(cascade));
        Assert.Contains(cascade.Removes, r => r.Id == C && r.Pointer == "/actions/0/call/inputs/1");
        Assert.DoesNotContain(cascade.Removes, r => r.Id == C && r.Pointer == "/actions/0/call");
        Assert.DoesNotContain(cascade.Deletes, d => d.Id == C);
    }

    // ---- XState ----

    [Fact]
    public async Task A_call_and_a_steps_operation_travel_through_an_export_and_a_re_import()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        await store.FormatAsync([CFile], ChangeSource.Editor, Ct);
        var before = h.Read(CFile);
        var export = (await store.ExportProcessAsync(C, Ct)).Value!;
        var config = JsonNode.Parse(export.Json)!.AsObject();
        var call = config["meta"]!["maquettiste"]!["actions"]![0]!["call"]!.AsObject();
        Assert.Equal(Op, call["operation"]!.GetValue<string>());
        Assert.Equal([InputA, InputB], call["inputs"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()));
        var invoke = config["states"]!["Active"]!["invoke"]![0]!.AsObject();
        Assert.Equal(Op, invoke["meta"]!["maquettiste"]!["operation"]!.GetValue<string>());
        Assert.Contains(export.Diagnostics, d => d.Rule == "MQ9406" && d.Message.Contains("the operations actions call", StringComparison.Ordinal)
            && d.Message.Contains("the operations service steps run", StringComparison.Ordinal));

        var again = await store.ImportProcessAsync(new ProcessImportRequest { Config = Json(export.Json), Into = C }, dryRun: false, ChangeSource.Editor, Ct);
        Assert.True(again.Status == ProcessCallStatus.Ok, string.Join(" | ", again.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(before, h.Read(CFile)); // the bytes: a dropped call, input or step operation fails

        // An input given without an id takes the id of the input of its member in the process it goes into.
        call["inputs"]![1]!.AsObject().Remove("id");
        var dry = await store.ImportProcessAsync(new ProcessImportRequest { Config = Json(config.ToJsonString()), Into = C }, dryRun: true, ChangeSource.Editor, Ct);
        Assert.True(dry.Status == ProcessCallStatus.Ok, string.Join(" | ", dry.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var imported = dry.Value!.Document!.Value.GetProperty("actions")[0].GetProperty("call").GetProperty("inputs");
        Assert.Equal([InputA, InputB], imported.EnumerateArray().Select(i => i.GetProperty("id").GetString()));
    }

    // ---- the invoice example (section 6) ----

    [Fact]
    public async Task The_invoice_example_validates_against_the_schemas()
    {
        const string Invoice = "01JENT00000000000000000101";
        const string Terms = "01JATT00000000000000000110";
        const string Issue = "01JPRX00000000000000000101";
        using var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/entities/invoice.json", $$"""
            {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Invoice}}","name":"Invoice","package":"{{Package}}",
             "key":{"attributes":["01JATT00000000000000000102"]},"attributes":[{"id":"01JATT00000000000000000102","name":"id","type":"uuid"}]}
            """);
        h.Write("model/operations/invoice/issue.json", $$"""
            {"$schema":"../../../.schema/v1/operation.json","kind":"operation","id":"01JQPN00000000000000000101","name":"issue","displayName":"Issue invoice",
             "package":"{{Package}}","subject":"{{Invoice}}","effect":"change","verb":"promote",
             "input":[{"id":"01JQPN00000000000000000102","name":"dueDays","type":"int32","required":true}],
             "outcomes":[{"id":"01JQPN00000000000000000103","name":"issued"}],
             "fires":["{{Issue}}"]}
            """);
        h.Write("model/operations/charge-card.json", $$"""
            {"$schema":"../../.schema/v1/operation.json","kind":"operation","id":"01JQPN00000000000000000111","name":"chargeCard","package":"{{Package}}",
             "effect":"change","outcomes":[{"id":"01JQPN00000000000000000112","name":"charged"}]}
            """);
        h.Write("model/processes/invoice-lifecycle.json", $$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"01JPRC00000000000000000101","name":"InvoiceLifecycle","package":"{{Package}}",
             "use":"lifecycle","subjects":[{"id":"01JSBJ00000000000000000101","entity":"{{Invoice}}"}],
             "events":[{"id":"{{Issue}}","name":"Issue","payload":[{"id":"01JATT00000000000000000103","name":"dueDays","type":"int32","required":true},
               {"id":"01JATT00000000000000000104","name":"issuedOn","type":"date","required":true}]},{"id":"01JPRX00000000000000000102","name":"Pay"}],
             "states":[{"id":"01JSTA00000000000000000101","name":"Draft"},{"id":"01JSTA00000000000000000102","name":"Issued"},{"id":"01JSTA00000000000000000103","name":"Paid","type":"final"}],
             "initial":"01JSTA00000000000000000101",
             "transitions":[{"id":"01JTRN00000000000000000101","source":"01JSTA00000000000000000101","event":"{{Issue}}","targets":["01JSTA00000000000000000102"]},
               {"id":"01JTRN00000000000000000102","source":"01JSTA00000000000000000102","event":"01JPRX00000000000000000102","targets":["01JSTA00000000000000000103"]}]}
            """);
        h.Write("model/processes/invoice-run.json", $$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"01JPRC00000000000000000111","name":"InvoiceRun","package":"{{Package}}",
             "use":"orchestration","subjects":[{"id":"01JSBJ00000000000000000111","entity":"{{Invoice}}"}],
             "context":[{"id":"{{Terms}}","name":"termsDays","type":"int32"}],
             "events":[{"id":"01JPRX00000000000000000111","name":"go"}],
             "actions":[{"id":"01JPRX00000000000000000112","name":"RequestIssue",
               "call":{"operation":"01JQPN00000000000000000101","inputs":[{"id":"01JCXN00000000000000000101","member":"01JQPN00000000000000000102","value":{"path":["{{Terms}}"]} }]} }],
             "states":[{"id":"01JSTA00000000000000000111","name":"Start"},
               {"id":"01JSTA00000000000000000112","name":"Issued","invoke":[{"id":"01JPRX00000000000000000113","name":"ChargeCard","type":"service","operation":"01JQPN00000000000000000111"}]},
               {"id":"01JSTA00000000000000000113","name":"Done","type":"final"}],
             "initial":"01JSTA00000000000000000111",
             "transitions":[{"id":"01JTRN00000000000000000111","source":"01JSTA00000000000000000111","event":"01JPRX00000000000000000111","targets":["01JSTA00000000000000000112"],"actions":["01JPRX00000000000000000112"]},
               {"id":"01JTRN00000000000000000112","source":"01JSTA00000000000000000112","trigger":"invoke-done","invoke":"01JPRX00000000000000000113","targets":["01JSTA00000000000000000113"]}]}
            """);

        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var errors = store.Current!.LoadDiagnostics.Where(d => d.Rule is "MQ1001" or "MQ1002" || d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(d => d.Rule + " " + d.FilePath + " " + d.JsonPointer + " " + d.Message)));
        var run = store.Current!.Get<Process>("01JPRC00000000000000000111")!;
        Assert.Equal("01JQPN00000000000000000101", run.Actions[0].Call!.Operation);
        Assert.Equal("01JQPN00000000000000000111", run.States[1].Invoke[0].Operation);

        // A key the call does not know fails the schema.
        var document = JsonNode.Parse(h.Read("model/processes/invoice-run.json"))!.AsObject();
        document["actions"]![0]!["call"]!["outcome"] = "issued";
        h.Write("model/processes/invoice-run.json", document.ToJsonString());
        await using var again = new ModelStore(h.Options, h.Services());
        await again.LoadAsync(Ct);
        Assert.Contains(again.Current!.LoadDiagnostics, d => d.Rule == "MQ1002" && d.FilePath!.EndsWith("invoice-run.json", StringComparison.Ordinal));
    }
}
