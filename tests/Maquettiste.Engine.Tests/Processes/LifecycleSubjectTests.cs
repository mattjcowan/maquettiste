using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Subjects and codes (lifecycle-design.md decisions 1 to 10 and 23) over the <c>process-basics</c> fixture, whose lifecycle stays in the
/// one-subject form: both forms read as one list, entries have ids, derived entities are covered, several lifecycles may govern one
/// entity, <c>set-lifecycle</c> follows decision 9, <c>use-subjects</c> converts only on request, imports keep the form a process has,
/// deleting a listed entity drops its entry, and records, the index row and the resolved model carry the new members only where used.
/// </summary>
public sealed class LifecycleSubjectTests
{
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, one-subject form, binds Order.status
    private const string Q = "01JPRC00000000000000000002"; // Billing, subjects form
    private const string S = "01JPRC00000000000000000003"; // Shipping, subjects form
    private const string R = "01JPRC00000000000000000004"; // an orchestration with no subject
    private const string Order = "01JENT00000000000000000001";
    private const string Invoice = "01JENT00000000000000000002";
    private const string RushOrder = "01JENT00000000000000000003";
    private const string Status = "01JATT00000000000000000002";
    private const string OrderStatus = "01JENM00000000000000000001";
    private const string InvoiceNumber = "01JATT00000000000000000092";
    private const string QInvoice = "01JSBJ00000000000000000001";
    private const string QOrder = "01JSBJ00000000000000000002";
    private const string SOrder = "01JSBJ00000000000000000003";
    private const string ProcessFile = "model/processes/order-lifecycle.json";
    private const string QFile = "model/processes/billing.json";
    private const string SFile = "model/processes/shipping.json";
    private const string RFile = "model/processes/handover.json";
    private const string Package = "01JPKG00000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ProcessJson(string id, string name, string? use, string subjects, string state) => $$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{id}}","name":"{{name}}","package":"{{Package}}"{{(use is null ? "" : $",\"use\":\"{use}\"")}}{{subjects}},
         "states":[{"id":"{{state}}1","name":"Open"},{"id":"{{state}}2","name":"Done","type":"final"}],
         "events":[{"id":"{{state}}3","name":"finish"}],
         "transitions":[{"id":"{{state}}4","source":"{{state}}1","event":"{{state}}3","targets":["{{state}}2"]}]}
        """;

    private static string Entries(params (string Id, string Entity, string? Bound)[] entries) =>
        ",\"subjects\":[" + string.Join(",", entries.Select(e => $"{{\"id\":\"{e.Id}\",\"entity\":\"{e.Entity}\"" + (e.Bound is null ? "" : $",\"boundAttribute\":\"{e.Bound}\"") + "}")) + "]";

    // Invoice and RushOrder (derived from Order); Billing lists Invoice and Order; optionally Shipping lists Order and Handover has no subject.
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null, bool billing = true)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/entities/invoice.json", $$"""{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Invoice}}","name":"Invoice","package":"{{Package}}","key":{"attributes":["01JATT00000000000000000091"]},"attributes":[{"id":"01JATT00000000000000000091","name":"id","type":"uuid"},{"id":"{{InvoiceNumber}}","name":"number","type":"string","length":20}]}""");
        harness.Write("model/entities/rush-order.json", $$"""{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{RushOrder}}","name":"RushOrder","package":"{{Package}}","base":"{{Order}}"}""");
        if (billing)
            harness.Write(QFile, ProcessJson(Q, "Billing", "lifecycle", Entries((QInvoice, Invoice, null), (QOrder, Order, null)), "01JSTA0000000000000000020"));
        prepare?.Invoke(harness);
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static void WithShipping(LoaderHarness h) => h.Write(SFile, ProcessJson(S, "Shipping", "lifecycle", Entries((SOrder, Order, null)), "01JSTA0000000000000000030"));

    private static void WithHandover(LoaderHarness h) => h.Write(RFile, ProcessJson(R, "Handover", null, "", "01JSTA0000000000000000040"));

    private static async Task<IReadOnlyList<Diagnostic>> LifecycleFindings(LoaderHarness h, ModelStore store) =>
        [.. (await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct)).Diagnostics
            .Where(d => d.Rule.StartsWith("MQ92", StringComparison.Ordinal) || (d.Rule.StartsWith("MQ1", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error))];

    private static ModelBatch Batch(BatchOperation operation) => new([operation]);

    private static void Applied(BatchResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

    private static JsonObject File(LoaderHarness h, string path) => JsonNode.Parse(h.Read(path))!.AsObject();

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonObject Record(ResolvedModel model, string id) =>
        JsonSerializer.SerializeToNode(ResolvedRecords.Entries(model, "all", null).Single(e => e.Id == id).Project(), Web)!.AsObject();

    // ---- one list ----

    [Fact]
    public async Task Both_forms_read_as_one_list_and_resolve_alike()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        var one = Assert.Single(model.Get<Process>(P)!.Subjects);
        Assert.Equal((P, Order, Status), (one.Id, one.Entity, one.BoundAttribute)); // the one-subject form's entry has the process's id
        Assert.Equal([(QInvoice, Invoice), (QOrder, Order)], model.Get<Process>(Q)!.Subjects.Select(s => (s.Id, s.Entity)));
        Assert.Equal(("subject", Q), (model.Index.Entries[QOrder].Kind, model.Index.Entries[QOrder].OwnerId));
        Assert.Contains(model.ReferencesTo(Order), r => r.FromId == QOrder && r.JsonPointer == "/subjects/1/entity");

        // The index row: subject with exactly one subject, in either form; subjects with two or more (decision 23).
        var rows = await store.GetIndexAsync(Ct);
        var p = rows.Single(r => r.Id == P);
        var q = rows.Single(r => r.Id == Q);
        Assert.Equal(Order, p.Subject);
        Assert.Null(p.Subjects);
        Assert.Null(q.Subject);
        Assert.Equal([Invoice, Order], q.Subjects!);
        Assert.False(JsonSerializer.SerializeToNode(p, Web)!.AsObject().ContainsKey("subjects"));

        var resolved = ResolutionKit.Resolve(model);
        var rp = resolved.Processes.Single(x => x.Id == P);
        var rq = resolved.Processes.Single(x => x.Id == Q);
        var subject = Assert.Single(rp.Subjects);
        Assert.Equal((P, "Order", "status", "OrderStatus"), (subject.Id, subject.Entity.Name, subject.BoundAttribute!.Name, subject.BoundEnum!.Name));
        Assert.Equal(("Order", "status", "OrderStatus"), (rp.Subject!.Name, rp.BoundAttribute!.Name, rp.BoundEnum!.Name));
        Assert.Equal(["Invoice", "Order"], rq.Subjects.Select(x => x.Entity.Name));
        Assert.Equal([QInvoice, QOrder], rq.Subjects.Select(x => x.Id));
        Assert.Null(rq.Subject); // set only with exactly one subject
        Assert.Contains("e:" + Invoice, rq.Subjects[0].Dependencies);
        Assert.All(rq.AllStates, st => Assert.Contains("e:" + Invoice, st.Dependencies)); // every node reads every subject

        // Records: subjects only for the subjects form.
        Assert.False(Record(resolved, P).ContainsKey("subjects"));
        Assert.Equal([QInvoice, QOrder], Record(resolved, Q)["subjects"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()));
        Assert.Equal(Order, Record(resolved, Q)["subjects"]![1]!["entity"]!.GetValue<string>());
    }

    [Fact]
    public async Task Entity_lifecycles_list_every_lifecycle_of_the_entity_or_a_base_by_name()
    {
        var (h, store) = await OpenAsync(WithShipping);
        using var _ = h;
        await using var __ = store;
        var resolved = ResolutionKit.Resolve(store.Current!);

        var order = resolved.Entity("Order");
        var rush = resolved.Entity("RushOrder");
        Assert.Equal(["Billing", "OrderLifecycle", "Shipping"], order.Lifecycles.Select(l => l.Name));
        Assert.Equal(P, order.Lifecycle!.Id); // entity.lifecycle keeps its meaning (decision 8)
        Assert.Equal(["Billing", "OrderLifecycle", "Shipping"], rush.Lifecycles.Select(l => l.Name)); // derived entities are covered
        Assert.Null(rush.Lifecycle);
        Assert.Equal(["Billing"], resolved.Entity("Invoice").Lifecycles.Select(l => l.Name));
        Assert.Equal(["k:process", "r:" + RushOrder, "r:" + Order], rush.Lifecycles.MembershipKeys);

        var record = Record(resolved, Order);
        Assert.Equal([Q, P, S], record["lifecycles"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public async Task A_model_in_the_one_subject_form_gets_no_new_record_members()
    {
        var (h, store) = await OpenAsync(billing: false);
        using var _ = h;
        await using var __ = store;
        var resolved = ResolutionKit.Resolve(store.Current!);

        var process = Record(resolved, P);
        var entity = Record(resolved, Order);
        Assert.False(process.ContainsKey("subjects"));
        Assert.False(process.ContainsKey("codesMap"));
        Assert.All(process["states"]!.AsArray(), s => Assert.False(s!.AsObject().ContainsKey("code")));
        Assert.False(entity.ContainsKey("lifecycles"));
        Assert.Equal(P, entity["lifecycle"]!.GetValue<string>());
        Assert.Equal(["OrderLifecycle"], resolved.Entity("Order").Lifecycles.Select(l => l.Name)); // the template member is there all the same
        var rows = await store.GetIndexAsync(Ct);
        Assert.Equal(Order, rows.Single(r => r.Id == P).Subject);
    }

    [Fact]
    public async Task A_file_with_both_forms_is_refused()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var document = (await store.GetElementAsync(Q, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["subject"] = Invoice;

        var result = await store.SaveAsync(Q, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule.StartsWith("MQ1", StringComparison.Ordinal));
        Assert.DoesNotContain("\"subject\"", h.Read(QFile), StringComparison.Ordinal);
    }

    // ---- rules ----

    [Fact]
    public async Task Several_lifecycles_may_govern_an_entity_that_names_one_of_them()
    {
        var (h, store) = await OpenAsync(WithShipping);
        using var _ = h;
        await using var __ = store;

        // Order names OrderLifecycle; Billing and Shipping list it without being named back, and Invoice names none.
        var findings = await LifecycleFindings(h, store);
        Assert.Equal(["MQ9209"], findings.Select(d => d.Rule));
    }

    [Fact]
    public async Task An_entity_may_name_a_lifecycle_that_lists_it_or_a_base_of_it()
    {
        var (h, store) = await OpenAsync(x =>
        {
            var rush = File(x, "model/entities/rush-order.json");
            rush["lifecycle"] = Q; // Billing lists Order, RushOrder's base
            x.Write("model/entities/rush-order.json", rush.ToJsonString());
            var invoice = File(x, "model/entities/invoice.json");
            invoice["lifecycle"] = P; // OrderLifecycle is Order's, in the one-subject form
            x.Write("model/entities/invoice.json", invoice.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        var findings = (await LifecycleFindings(h, store)).Where(d => d.Rule == "MQ9201").ToList();
        var only = Assert.Single(findings);
        Assert.Equal((Invoice, "/lifecycle"), (only.ElementId, only.JsonPointer));
    }

    [Fact]
    public async Task A_suppression_per_subject_accepts_the_findings_of_that_entry_only()
    {
        var (h, store) = await OpenAsync(x => x.Write(QFile, ProcessJson(Q, "Billing", "lifecycle",
            Entries((QInvoice, Invoice, "01JATT00000000000000000091"), (QOrder, Order, "01JATT00000000000000000001")), "01JSTA0000000000000000020")));
        using var _ = h;
        await using var __ = store;

        var both = (await LifecycleFindings(h, store)).Where(d => d.Rule == "MQ9202").ToList();
        Assert.Equal([(QInvoice, "/subjects/0/boundAttribute"), (QOrder, "/subjects/1/boundAttribute")], both.Select(d => (d.ElementId!, d.JsonPointer!)));

        var document = (await store.GetElementAsync(Q, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["suppressions"] = new JsonArray(new JsonObject { ["rule"] = "MQ9202", ["member"] = QInvoice, ["reason"] = "Invoices store their state elsewhere." });
        Assert.Equal(SaveOutcome.Saved, (await store.SaveAsync(Q, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct)).Outcome);

        var report = await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct);
        Assert.Equal([QOrder], report.Diagnostics.Where(d => d.Rule == "MQ9202").Select(d => d.ElementId));
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5019" or "MQ5020");
    }

    [Fact]
    public async Task An_enum_change_revalidates_a_lifecycle_that_binds_it_in_the_subjects_form()
    {
        var (h, store) = await OpenAsync(x => x.Write(QFile, ProcessJson(Q, "Billing", "lifecycle", Entries((QOrder, Order, Status)), "01JSTA0000000000000000020")));
        using var _ = h;
        await using var __ = store;

        // Billing's states are not the enum's members: validating the enum alone must reach the lifecycle that binds it.
        var report = await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, new ValidationScope([OrderStatus]), null, Ct);

        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ9203" && d.ElementId == QOrder);
    }

    [Fact]
    public async Task An_entity_change_replays_the_scenarios_of_a_lifecycle_that_lists_it()
    {
        var (h, store) = await OpenAsync(x =>
        {
            var process = File(x, ProcessFile);
            process.Remove("subject");
            process.Remove("boundAttribute");
            process["subjects"] = new JsonArray(new JsonObject { ["id"] = QInvoice.Replace("01JSBJ", "01JSBK", StringComparison.Ordinal), ["entity"] = Order, ["boundAttribute"] = Status });
            x.Write(ProcessFile, process.ToJsonString());
            const string happy = "model/scenarios/order-lifecycle/happy-path.json";
            var scenario = File(x, happy);
            scenario["steps"]![0]!["expect"]!["states"] = new JsonArray("01JSTA00000000000000000001"); // wrong: the order moves on
            x.Write(happy, scenario.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        var report = await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, new ValidationScope([Order]), null, Ct);

        Assert.Contains(report.Diagnostics, d => d.Rule == "MQ9302" && d.ElementId == "01JSCN00000000000000000001" && d.JsonPointer == "/steps/0/expect/states");
    }

    // ---- set-lifecycle (decision 9) ----

    [Fact]
    public async Task Set_lifecycle_gives_a_process_with_no_subject_subjects_and_demotes_the_previous_one_subject_lifecycle()
    {
        var (h, store) = await OpenAsync(WithHandover);
        using var _ = h;
        await using var __ = store;

        var result = await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Order, null, null, Target: R)), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var handover = File(h, RFile);
        Assert.Equal("lifecycle", handover["use"]!.GetValue<string>());
        Assert.False(handover.ContainsKey("subject"));
        var entry = Assert.Single(handover["subjects"]!.AsArray())!.AsObject();
        Assert.Equal(Order, entry["entity"]!.GetValue<string>());
        Assert.True(IdFormat.IsValid(entry["id"]!.GetValue<string>()));
        var previous = File(h, ProcessFile);
        Assert.False(previous.ContainsKey("use")); // the one-subject lifecycle becomes an orchestration, keeping its subject
        Assert.False(previous.ContainsKey("boundAttribute"));
        Assert.Equal(Order, previous["subject"]!.GetValue<string>());
        Assert.Equal(R, store.Current!.Get<Entity>(Order)!.Lifecycle);
        Assert.DoesNotContain(await LifecycleFindings(h, store), d => d.Rule is "MQ9201" or "MQ9202");
    }

    [Fact]
    public async Task Set_lifecycle_on_a_one_subject_target_keeps_its_form()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var result = await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Invoice, null, null, Target: P)), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var process = File(h, ProcessFile);
        Assert.Equal(Invoice, process["subject"]!.GetValue<string>());
        Assert.False(process.ContainsKey("subjects"));
        Assert.False(process.ContainsKey("boundAttribute"));
        Assert.Null(store.Current!.Get<Entity>(Order)!.Lifecycle);
    }

    [Fact]
    public async Task Set_lifecycle_onto_a_subjects_lifecycle_adds_the_entity_and_leaves_its_other_subjects_lifecycles()
    {
        var (h, store) = await OpenAsync(x =>
        {
            WithShipping(x);
            x.Write(QFile, ProcessJson(Q, "Billing", "lifecycle", Entries((QInvoice, Invoice, null)), "01JSTA0000000000000000020"));
        });
        using var _ = h;
        await using var __ = store;
        var shipping = h.Read(SFile);

        var result = await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Order, null, null, Target: Q)), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var billing = File(h, QFile);
        Assert.Equal([Invoice, Order], billing["subjects"]!.AsArray().Select(e => e!["entity"]!.GetValue<string>()));
        Assert.Equal(QInvoice, billing["subjects"]![0]!["id"]!.GetValue<string>());
        Assert.False(File(h, ProcessFile).ContainsKey("use")); // the previous one-subject lifecycle would fail MQ9201
        Assert.Equal(shipping, h.Read(SFile)); // several lifecycles may govern Order
        Assert.Equal(Q, store.Current!.Get<Entity>(Order)!.Lifecycle);
        Assert.DoesNotContain(await LifecycleFindings(h, store), d => d.Rule is "MQ9201" or "MQ9202");

        // Again: the entity is listed already, so nothing changes.
        var again = await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Order, null, null, Target: Q)), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, again.Outcome);
        Assert.Equal(2, File(h, QFile)["subjects"]!.AsArray().Count);
    }

    [Fact]
    public async Task Set_lifecycle_onto_a_lifecycle_that_lists_a_base_of_the_entity_does_not_add_it()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var billing = h.Read(QFile);

        Applied(await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, RushOrder, null, null, Target: Q)), ChangeSource.Editor, Ct));

        Assert.Equal(billing, h.Read(QFile)); // Billing lists Order, which RushOrder derives from
        Assert.Equal(Q, store.Current!.Get<Entity>(RushOrder)!.Lifecycle);
        Assert.DoesNotContain(await LifecycleFindings(h, store), d => d.Rule is "MQ9201" or "MQ9202");
    }

    [Fact]
    public async Task Clearing_a_lifecycle_takes_the_entity_out_of_the_subjects_of_its_lifecycles()
    {
        var (h, store) = await OpenAsync(WithShipping);
        using var _ = h;
        await using var __ = store;

        var result = await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Order, null, null)), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.False(File(h, ProcessFile).ContainsKey("use")); // the one-subject lifecycle, as before
        var billing = File(h, QFile);
        Assert.Equal("lifecycle", billing["use"]!.GetValue<string>()); // it still governs Invoice
        Assert.Equal([QInvoice], billing["subjects"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        var shipping = File(h, SFile);
        Assert.False(shipping.ContainsKey("use")); // left with no subject: an orchestration, as before
        Assert.False(shipping.ContainsKey("subjects"));
        Assert.Null(store.Current!.Get<Entity>(Order)!.Lifecycle);
        Assert.DoesNotContain(await LifecycleFindings(h, store), d => d.Rule is "MQ9201" or "MQ9202");
    }

    [Fact]
    public async Task An_entity_leaving_a_subjects_lifecycle_leaves_the_other_entries()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        // Order names OrderLifecycle; naming Billing for Invoice and then clearing Invoice's leaves Billing with Order.
        Applied((await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Invoice, null, null, Target: Q)), ChangeSource.Editor, Ct)));
        Applied((await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Invoice, null, null)), ChangeSource.Editor, Ct)));
        var billing = File(h, QFile);
        Assert.Equal("lifecycle", billing["use"]!.GetValue<string>());
        Assert.Equal([QOrder], billing["subjects"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.Equal(P, store.Current!.Get<Entity>(Order)!.Lifecycle);

        // Clearing Order's: Billing has no subject left and becomes an orchestration.
        Applied((await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Order, null, null)), ChangeSource.Editor, Ct)));
        billing = File(h, QFile);
        Assert.False(billing.ContainsKey("use"));
        Assert.False(billing.ContainsKey("subjects"));
    }

    // ---- use-subjects (decision 6) ----

    [Fact]
    public async Task Use_subjects_converts_on_request_and_nothing_converts_on_its_own()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var before = h.Read(ProcessFile);

        // An edit of a one-subject file keeps its form.
        var document = (await store.GetElementAsync(P, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["description"] = "Edited.";
        Assert.Equal(SaveOutcome.Saved, (await store.SaveAsync(P, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct)).Outcome);
        Assert.Equal(before.Replace("From draft to closed.", "Edited.", StringComparison.Ordinal), h.Read(ProcessFile));

        Assert.Contains("is not a process", Assert.Single((await store.ApplyBatchAsync(
            Batch(new BatchOperation(BatchOp.UseSubjects, null, null, null, ElementIds: [Order])), ChangeSource.Editor, Ct)).Items.SelectMany(i => i.Diagnostics)).Message,
            StringComparison.Ordinal);

        var result = await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.UseSubjects, null, null, null, ElementIds: [P, Q])), ChangeSource.Editor, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        var process = File(h, ProcessFile);
        Assert.False(process.ContainsKey("subject"));
        Assert.False(process.ContainsKey("boundAttribute"));
        var entry = Assert.Single(process["subjects"]!.AsArray())!.AsObject();
        Assert.Equal((Order, Status), (entry["entity"]!.GetValue<string>(), entry["boundAttribute"]!.GetValue<string>()));
        Assert.NotEqual(P, entry["id"]!.GetValue<string>());
        Assert.Empty(await LifecycleFindings(h, store));

        // Nothing left to convert: nothing is written.
        var hashes = (await store.GetIndexAsync(Ct)).Select(r => r.Hash).ToList();
        Applied((await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.UseSubjects, null, null, null)), ChangeSource.Editor, Ct)));
        Assert.Equal(hashes, (await store.GetIndexAsync(Ct)).Select(r => r.Hash));
    }

    [Fact]
    public async Task A_bound_attribute_without_a_subject_is_not_the_deprecated_form()
    {
        // use-subjects converts only a process with a subject, so MQ9209 warns only there; the stray binding keeps its own finding.
        var (h, store) = await OpenAsync(x => x.Write(RFile, ProcessJson(R, "Handover", "lifecycle", $",\"boundAttribute\":\"{Status}\"", "01JSTA0000000000000000040")));
        using var _ = h;
        await using var __ = store;
        var before = h.Read(RFile);

        Assert.Equal(["MQ9201"], (await LifecycleFindings(h, store)).Where(d => d.ElementId == R).Select(d => d.Rule));
        Applied(await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.UseSubjects, null, null, null, ElementIds: [R])), ChangeSource.Editor, Ct));
        Assert.Equal(before, h.Read(RFile));
    }

    // ---- import (decision 7) ----

    [Fact]
    public async Task A_new_import_writes_subjects_and_a_re_import_keeps_the_form_the_process_has()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        var fresh = await store.ImportProcessAsync(new ProcessImportRequest
        {
            Config = Json("""{"id":"Fresh","initial":"a","states":{"a":{"on":{"go":"b"}},"b":{"type":"final"}}}"""), Package = Package, Use = "lifecycle", Subject = "Invoice",
        }, dryRun: true, ChangeSource.Editor, Ct);
        var document = fresh.Value!.Document!.Value;
        Assert.False(document.TryGetProperty("subject", out var _));
        var entry = Assert.Single(document.GetProperty("subjects").EnumerateArray().ToList());
        Assert.Equal(Invoice, entry.GetProperty("entity").GetString());
        Assert.Contains(entry.GetProperty("id").GetString(), fresh.Value.Created);

        await store.FormatAsync([ProcessFile, QFile], ChangeSource.Editor, Ct); // canonical bytes, as every write leaves them
        foreach (var (id, path) in new[] { (P, ProcessFile), (Q, QFile) })
        {
            var before = h.Read(path);
            var export = await store.ExportProcessAsync(id, Ct);
            var again = await store.ImportProcessAsync(new ProcessImportRequest { Config = Json(export.Value!.Json), Into = id }, dryRun: false, ChangeSource.Editor, Ct);
            Assert.True(again.Status == ProcessCallStatus.Ok, string.Join(" | ", again.Diagnostics.Select(d => d.Rule + " " + d.Message)));
            Assert.Equal(before, h.Read(path)); // the bytes: a reordered or reformatted file fails

            // A plain config carries no meta: the form comes from the process it goes into.
            var plain = await store.ImportProcessAsync(new ProcessImportRequest { Config = Json("""{"id":"X","initial":"Open","states":{"Open":{},"Done":{"type":"final"}}}"""), Into = id },
                dryRun: true, ChangeSource.Editor, Ct);
            var kept = plain.Value!.Document!.Value;
            Assert.Equal(id == P, kept.TryGetProperty("subject", out var _));
            Assert.Equal(id == Q, kept.TryGetProperty("subjects", out var _));
        }
    }

    [Fact]
    public async Task A_re_import_converts_carried_subjects_into_the_form_the_target_has()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        async Task ReImport(string config, string id)
        {
            var result = await store.ImportProcessAsync(new ProcessImportRequest { Config = Json(config), Into = id }, dryRun: false, ChangeSource.Editor, Ct);
            Assert.True(result.Status == ProcessCallStatus.Ok, string.Join(" | ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        async Task Overwrite(string id, string path, Action<JsonObject> change)
        {
            var node = File(h, path);
            change(node);
            var document = (await store.GetElementAsync(id, Ct))!;
            var saved = await store.SaveAsync(id, Encoding.UTF8.GetBytes(node.ToJsonString()), document.Hash, ChangeSource.Editor, Ct);
            Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join(" | ", saved.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        }

        var oneSubject = h.Read(ProcessFile);
        var oneSubjectExport = (await store.ExportProcessAsync(P, Ct)).Value!.Json; // carries subject and boundAttribute
        Applied(await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.UseSubjects, null, null, null, ElementIds: [P])), ChangeSource.Editor, Ct));
        var listed = h.Read(ProcessFile);
        var listedExport = (await store.ExportProcessAsync(P, Ct)).Value!.Json; // carries subjects

        // The old export into the converted process: its subject and binding become the one entry, which keeps its id.
        await ReImport(oneSubjectExport, P);
        Assert.Equal(listed, h.Read(ProcessFile));

        // Back in the one-subject form, the new export's one entry becomes the subject and binding.
        await Overwrite(P, ProcessFile, node =>
        {
            node.Remove("subjects");
            node["subject"] = Order;
            node["boundAttribute"] = Status;
        });
        Assert.Equal(oneSubject, h.Read(ProcessFile));
        await ReImport(listedExport, P);
        Assert.Equal(oneSubject, h.Read(ProcessFile));

        // A binding with no subject can't be an entry: a target that lists its subjects keeps its own.
        var billingExport = (await store.ExportProcessAsync(Q, Ct)).Value!.Json;
        var stray = JsonNode.Parse(billingExport)!.AsObject();
        var meta = stray["meta"]!["maquettiste"]!.AsObject();
        meta.Remove("subjects");
        meta["boundAttribute"] = InvoiceNumber;
        await store.FormatAsync([QFile], ChangeSource.Editor, Ct);
        var listedBilling = h.Read(QFile);
        await ReImport(stray.ToJsonString(), Q);
        Assert.Equal(listedBilling, h.Read(QFile));

        // Two entries can't be one subject: a one-subject target keeps its own.
        Applied(await store.ApplyBatchAsync(Batch(new BatchOperation(BatchOp.SetLifecycle, Invoice, null, null, Target: Q)), ChangeSource.Editor, Ct));
        await Overwrite(Q, QFile, node =>
        {
            node.Remove("subjects");
            node["subject"] = Invoice;
        });
        var billing = h.Read(QFile);
        await ReImport(billingExport, Q);
        Assert.Equal(billing, h.Read(QFile));
    }

    [Fact]
    public async Task A_plain_re_import_keeps_the_state_codes()
    {
        var (h, store) = await OpenAsync(x =>
        {
            var billing = File(x, QFile);
            billing["states"]![0]!["code"] = 10;
            billing["states"]![1]!["code"] = 20;
            x.Write(QFile, billing.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;

        // A config with no meta: its states keep the codes of the states they match.
        var plain = await store.ImportProcessAsync(new ProcessImportRequest
        {
            Config = Json("""{"initial":"Open","states":{"Open":{"on":{"finish":"Done"}},"Done":{"type":"final"}}}"""), Into = Q,
        }, dryRun: true, ChangeSource.Editor, Ct);

        var states = plain.Value!.Document!.Value.GetProperty("states").EnumerateArray().ToList();
        Assert.Equal([10, 20], states.Select(s => s.GetProperty("code").GetInt32()));
    }

    // ---- deletes (decision 5) ----

    [Theory]
    [InlineData(DeleteResolution.RemoveReferences)]
    [InlineData(DeleteResolution.DeleteDependents)]
    public async Task Deleting_a_listed_entity_drops_its_entry_and_an_emptied_list(DeleteResolution resolution)
    {
        var (h, store) = await OpenAsync(x => x.Write(QFile, ProcessJson(Q, "Billing", "lifecycle",
            Entries((QInvoice, Invoice, InvoiceNumber), (QOrder, Order, null)), "01JSTA0000000000000000020")));
        using var _ = h;
        await using var __ = store;

        var invoice = (await store.GetElementAsync(Invoice, Ct))!;
        var deleted = await store.DeleteAsync(Invoice, invoice.Hash, resolution, ChangeSource.Editor, Ct);

        Assert.True(deleted.Outcome == SaveOutcome.Saved, string.Join(" | ", deleted.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var billing = File(h, QFile);
        Assert.Equal("lifecycle", billing["use"]!.GetValue<string>());
        Assert.Equal([QOrder], billing["subjects"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));

        // The last one: the list goes, and the lifecycle has no subject, so the delete is refused with MQ9201, as when the one subject
        // of OrderLifecycle goes.
        var plan = await store.GetDeletePlanAsync([Order, RushOrder], resolution, Ct);
        Assert.Equal(SaveOutcome.Invalid, plan.Outcome);
        Assert.Contains(plan.Removes, r => r.Id == Q && r.Pointer == "/subjects");
        Assert.DoesNotContain(plan.Removes, r => r.Id == Q && r.Pointer.StartsWith("/subjects/", StringComparison.Ordinal));
        Assert.Equal([P, Q], plan.Refused.Where(r => r.Rule == "MQ9201").Select(r => r.Id).Order(StringComparer.Ordinal));
    }

    // ---- codes (decisions 1 to 4) ----

    [Fact]
    public async Task State_codes_resolve_with_whether_they_form_a_map()
    {
        var (h, store) = await OpenAsync(x =>
        {
            var process = File(x, ProcessFile);
            var states = process["states"]!.AsArray();
            states[0]!["code"] = "DRF"; // Draft
            states[2]!["code"] = "REV"; // Review, compound: its children take its code
            states[^1]!["code"] = "CLS"; // Closed
            x.Write(ProcessFile, process.ToJsonString());
            var billing = File(x, QFile);
            billing["states"]![0]!["code"] = 10;
            billing["states"]![1]!["code"] = 20;
            x.Write(QFile, billing.ToJsonString());
        });
        using var _ = h;
        await using var __ = store;
        Assert.DoesNotContain(await LifecycleFindings(h, store), d => d.Rule == "MQ9206");

        var resolved = ResolutionKit.Resolve(store.Current!);
        var p = resolved.Processes.Single(x => x.Id == P);
        var q = resolved.Processes.Single(x => x.Id == Q);
        Assert.Equal("DRF", p.AllStates.Single(s => s.Name == "Draft").Code);
        Assert.Null(p.AllStates.Single(s => s.Name == "Pending").Code); // its own code: none
        Assert.Equal(new object?[] { 10L, 20L }, q.AllStates.Select(s => s.Code).ToArray());
        Assert.True(p.CodesMap); // Fulfilment's parallel regions hold no code
        Assert.True(q.CodesMap);
        var codes = ProcessCodes.Of(store.Current!.Get<Process>(P)!);
        Assert.Equal("REV", codes.CodedStateOf("01JSTA00000000000000000004")!.Code!.Value.GetString()); // Pending maps to Review's code
        Assert.Null(codes.CodedStateOf("01JSTA00000000000000000009")); // Packing: no coded state on its path, so no code
        Assert.Equal(CodeKind.String, codes.Kind);

        var record = Record(resolved, P);
        Assert.True(record["codesMap"]!.GetValue<bool>());
        Assert.Equal("DRF", record["states"]![0]!["code"]!.GetValue<string>());
        Assert.False(record["states"]![1]!.AsObject().ContainsKey("code"));
        Assert.Equal(10, Record(resolved, Q)["states"]![0]!["code"]!.GetValue<long>());
    }

    [Fact]
    public void Codes_that_share_a_path_or_sit_in_a_region_do_not_form_a_map()
    {
        ProcessState State(string id, string? code, StateType type = StateType.Atomic, params ProcessState[] children) => new()
        {
            Id = id, Name = "s" + id, Type = type, States = children,
            Code = code is null ? null : JsonDocument.Parse(code).RootElement.Clone(),
        };
        Engine.Model.Process Chart(params ProcessState[] states) => new() { Id = "P", Name = "P", States = states };

        Assert.True(ProcessCodes.Of(Chart(State("1", "\"a\""), State("2", null, StateType.Compound, State("3", "\"b\"")))).IsMap);
        Assert.False(ProcessCodes.Of(Chart(State("1", "\"a\"", StateType.Compound, State("2", "\"b\"")))).IsMap); // two on one path
        Assert.False(ProcessCodes.Of(Chart(State("1", null, StateType.Parallel, State("2", "\"a\""), State("3", null)))).IsMap); // in a region
        Assert.True(ProcessCodes.Of(Chart(State("1", "\"a\"", StateType.Parallel, State("2", null), State("3", null)))).IsMap); // on the parallel state
        Assert.False(ProcessCodes.Of(Chart(State("1", null))).IsMap); // no code at all

        var clash = ProcessCodes.Of(Chart(State("1", "1"), State("2", "\"1\""), State("3", "\"x\"", StateType.History)));
        Assert.Equal(("1", "2"), (clash.Duplicates.Single().First.State.Id, clash.Duplicates.Single().Second.State.Id)); // 1 and "1" are one code
        Assert.Equal(CodeKind.Mixed, clash.Kind);
        Assert.Equal(["2"], clash.OtherKind.Select(c => c.State.Id));
        Assert.Equal(["3"], clash.OnHistoryOrChoice.Select(c => c.State.Id));
        Assert.False(clash.IsSound);
    }
}
