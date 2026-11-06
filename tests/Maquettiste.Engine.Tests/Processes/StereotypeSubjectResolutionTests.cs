using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// A subject entry that names a stereotype, as the resolver, the records and the index row see it (stereotype-sets-design.md decisions 4
/// and 12, sections 6 and 8), over the <c>process-basics</c> fixture: an <c>Approval</c> lifecycle lists <c>approvable</c>, which zero,
/// one or two entities carry. <c>RushOrder</c> derives from <c>Order</c>.
/// </summary>
public sealed class StereotypeSubjectResolutionTests
{
    private const string Approval = "01JPRC00000000000000000009";
    private const string Entry = "01JSBJ00000000000000000009";
    private const string OrderEntry = "01JSBJ00000000000000000008";
    private const string Approvable = "01JSTR00000000000000000009";
    private const string ApprovalStatus = "01JATT00000000000000000097";
    private const string RequestedBy = "01JATT00000000000000000098";
    private const string Order = "01JENT00000000000000000001";
    private const string Invoice = "01JENT00000000000000000002";
    private const string RushOrder = "01JENT00000000000000000003";
    private const string OrderStatus = "01JENM00000000000000000001";
    private const string Package = "01JPKG00000000000000000001";
    private const string InvoiceFile = "model/entities/invoice.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string InvoiceJson(bool carries) =>
        $$"""{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{Invoice}}","name":"Invoice","package":"{{Package}}"{{(carries ? ",\"stereotypes\":[\"approvable\"]" : "")}},"key":{"attributes":["01JATT00000000000000000091"]},"attributes":[{"id":"01JATT00000000000000000091","name":"id","type":"uuid"}]}""";

    private static string ApprovalJson(bool orderEntry) => $$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{Approval}}","name":"Approval","package":"{{Package}}","use":"lifecycle",
         "subjects":[{{(orderEntry ? $"{{\"id\":\"{OrderEntry}\",\"entity\":\"{Order}\"}}," : "")}}{"id":"{{Entry}}","stereotype":"approvable","boundAttribute":"{{ApprovalStatus}}","owner":"{{RequestedBy}}"}],
         "states":[{"id":"01JSTA00000000000000000091","name":"Open"},{"id":"01JSTA00000000000000000092","name":"Done","type":"final"}],
         "events":[{"id":"01JSTA00000000000000000093","name":"finish"}],
         "transitions":[{"id":"01JSTA00000000000000000094","source":"01JSTA00000000000000000091","event":"01JSTA00000000000000000093","targets":["01JSTA00000000000000000092"]}]}
        """;

    /// <summary>The model with <paramref name="carriers"/> of Invoice and Order carrying <c>approvable</c> (Invoice first).</summary>
    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(int carriers, bool orderEntry = false, bool rushCarries = false)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        harness.Write("model/vocabularies/stereotypes/approvable.json", $$"""
            {"$schema":"../../../.schema/v1/stereotype.json","kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"Approvable","appliesTo":["entity"],
             "attributes":[{"id":"{{ApprovalStatus}}","name":"approvalStatus","type":{"ref":"{{OrderStatus}}" } },{"id":"{{RequestedBy}}","name":"requestedBy","type":"string","length":80}]}
            """);
        harness.Write(InvoiceFile, InvoiceJson(carries: carriers >= 1));
        harness.Write("model/entities/rush-order.json",
            $$"""{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{RushOrder}}","name":"RushOrder","package":"{{Package}}","base":"{{Order}}"{{(rushCarries ? ",\"stereotypes\":[\"approvable\"]" : "")}}}""");
        if (carriers >= 2)
        {
            var order = JsonNode.Parse(harness.Read("model/entities/order.json"))!.AsObject();
            order["stereotypes"] = new JsonArray("approvable");
            harness.Write("model/entities/order.json", order.ToJsonString());
        }

        harness.Write("model/processes/approval.json", ApprovalJson(orderEntry));
        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (harness, store);
    }

    private static JsonObject Record(ResolvedModel model, string id) =>
        JsonSerializer.SerializeToNode(ResolvedRecords.Entries(model, "all", null).Single(e => e.Id == id).Project(), Web)!.AsObject();

    [Fact]
    public async Task Zero_carriers_give_no_subject_and_the_entry_as_written()
    {
        var (h, store) = await OpenAsync(carriers: 0);
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var resolved = ResolutionKit.Resolve(model);
        var p = resolved.Processes.Single(x => x.Id == Approval);

        Assert.Empty(p.Subjects);
        Assert.Null(p.Subject);
        Assert.Null(p.BoundAttribute);
        Assert.Null(p.BoundEnum);
        var entry = Assert.Single(p.StereotypeSubjects);
        Assert.Equal((Entry, "approvable", "approvalStatus", "requestedBy"), (entry.Id, entry.Stereotype!.Key, entry.BoundAttribute!.Name, entry.OwnerAttribute!.Name));
        Assert.Empty(entry.Entities);

        // Any entity gaining the stereotype changes the subjects: the list keys on every entity's file and on which entities exist.
        Assert.Contains("e:" + Invoice, p.Subjects.MembershipKeys);
        Assert.Contains("k:entity", p.Subjects.MembershipKeys);
        Assert.Contains("e:" + Package, p.Subjects.MembershipKeys);
        Assert.Contains("e:" + Approvable, p.Subjects.MembershipKeys);

        var record = Record(resolved, Approval);
        Assert.Empty(record["subjects"]!.AsArray());
        var written = Assert.Single(record["stereotypeSubjects"]!.AsArray())!.AsObject();
        Assert.Equal(Entry, written["id"]!.GetValue<string>());
        Assert.Equal(Approvable, written["stereotype"]!.GetValue<string>());
        Assert.Equal(ApprovalStatus, written["boundAttribute"]!.GetValue<string>());
        Assert.Equal(RequestedBy, written["owner"]!.GetValue<string>());
        Assert.Empty(written["entities"]!.AsArray());

        var row = (await store.GetIndexAsync(Ct)).Single(r => r.Id == Approval);
        Assert.Equal([Approvable], row.SubjectStereotypes!);
        Assert.Null(row.Covers);
        Assert.Null(row.Subject);
        Assert.Null(row.Subjects);
        Assert.Empty(resolved.Entities.Single(e => e.Id == Invoice).Lifecycles);
    }

    [Fact]
    public async Task One_carrier_gives_the_one_subject_fields_and_its_copies_of_the_attributes()
    {
        var (h, store) = await OpenAsync(carriers: 1);
        using var _ = h;
        await using var __ = store;
        var resolved = ResolutionKit.Resolve(store.Current!);
        var p = resolved.Processes.Single(x => x.Id == Approval);

        var subject = Assert.Single(p.Subjects);
        Assert.Equal(Entry + "/" + Invoice, subject.Id);
        Assert.Equal("Invoice", subject.Entity.Name);
        Assert.Equal("approvable", subject.Stereotype!.Key);
        Assert.Same(subject.Entity, subject.BoundAttribute!.Owner); // the carrier's copy
        Assert.Equal(("approvalStatus", "OrderStatus", "requestedBy"), (subject.BoundAttribute.Name, subject.BoundEnum!.Name, subject.OwnerAttribute!.Name));
        Assert.Equal(("Invoice", "approvalStatus", "OrderStatus"), (p.Subject!.Name, p.BoundAttribute!.Name, p.BoundEnum!.Name));
        Assert.Equal(["Invoice"], p.StereotypeSubjects.Single().Entities.Select(e => e.Name));

        var invoice = resolved.Entities.Single(e => e.Id == Invoice);
        Assert.Equal(["Approval"], invoice.Lifecycles.Select(l => l.Name));
        Assert.Contains("k:stereotype", invoice.Lifecycles.MembershipKeys);
        Assert.Contains("k:package", invoice.Lifecycles.MembershipKeys);
        Assert.DoesNotContain(resolved.Entities.Single(e => e.Id == Order).Lifecycles, l => l.Name == "Approval");

        var record = Record(resolved, Approval);
        var listed = Assert.Single(record["subjects"]!.AsArray())!.AsObject();
        Assert.Equal((Entry + "/" + Invoice, Invoice, Approvable), (listed["id"]!.GetValue<string>(), listed["entity"]!.GetValue<string>(), listed["stereotype"]!.GetValue<string>()));
        Assert.Equal(Invoice, record["subject"]!.GetValue<string>());
        Assert.Equal([Invoice], record["stereotypeSubjects"]![0]!["entities"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal([Approval], Record(resolved, Invoice)["lifecycles"]!.AsArray().Select(x => x!.GetValue<string>()));

        var row = (await store.GetIndexAsync(Ct)).Single(r => r.Id == Approval);
        Assert.Equal([Invoice], row.Covers!);
        Assert.Null(row.Subject);
    }

    [Fact]
    public async Task Two_carriers_expand_by_name_and_leave_the_one_subject_fields_empty()
    {
        var (h, store) = await OpenAsync(carriers: 2);
        using var _ = h;
        await using var __ = store;
        var resolved = ResolutionKit.Resolve(store.Current!);
        var p = resolved.Processes.Single(x => x.Id == Approval);

        Assert.Equal([Entry + "/" + Invoice, Entry + "/" + Order], p.Subjects.Select(s => s.Id));
        Assert.All(p.Subjects, s => Assert.Equal("approvalStatus", s.BoundAttribute!.Name));
        Assert.Null(p.Subject);
        Assert.Null(p.BoundAttribute);
        Assert.Null(p.BoundEnum);

        // RushOrder derives from a carrier: covered, so it lists the lifecycle, but it gives no subject of its own.
        Assert.Contains(resolved.Entities.Single(e => e.Id == RushOrder).Lifecycles, l => l.Name == "Approval");
        Assert.Equal([Invoice, Order], (await store.GetIndexAsync(Ct)).Single(r => r.Id == Approval).Covers!);
    }

    [Fact]
    public async Task An_entry_naming_the_entity_or_a_base_wins_over_the_stereotype()
    {
        // Order is listed by its own entry and carries approvable; RushOrder carries it directly but its base's entry wins (decision 4).
        var (h, store) = await OpenAsync(carriers: 2, orderEntry: true, rushCarries: true);
        using var _ = h;
        await using var __ = store;
        var resolved = ResolutionKit.Resolve(store.Current!);
        var p = resolved.Processes.Single(x => x.Id == Approval);

        Assert.Equal([OrderEntry, Entry + "/" + Invoice], p.Subjects.Select(s => s.Id)); // expanded at its place
        Assert.Null(p.Subjects[0].Stereotype);
        Assert.Equal(["Invoice"], p.StereotypeSubjects.Single().Entities.Select(e => e.Name));
        Assert.Equal([Invoice], (await store.GetIndexAsync(Ct)).Single(r => r.Id == Approval).Covers!);
        Assert.False(Record(resolved, Approval)["subjects"]![0]!.AsObject().ContainsKey("stereotype"));
    }

    [Fact]
    public async Task The_index_row_follows_an_entity_gaining_the_stereotype()
    {
        var (h, store) = await OpenAsync(carriers: 0);
        using var _ = h;
        await using var __ = store;
        Assert.Null((await store.GetIndexAsync(Ct)).Single(r => r.Id == Approval).Covers);

        var saved = await store.SaveAsync(Invoice, Encoding.UTF8.GetBytes(InvoiceJson(carries: true)), store.Current!.GetDocument(Invoice)!.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal([Invoice], (await store.GetIndexAsync(Ct)).Single(r => r.Id == Approval).Covers!);
        Assert.Single(ResolutionKit.Resolve(store.Current!).Processes.Single(x => x.Id == Approval).Subjects);
    }

    [Fact]
    public async Task A_model_without_a_stereotype_entry_keeps_its_rows_and_keys()
    {
        var (h, store) = await OpenAsync(carriers: 1);
        using var _ = h;
        await using var __ = store;
        var resolved = ResolutionKit.Resolve(store.Current!);
        var order = resolved.Processes.Single(x => x.Name == "OrderLifecycle");
        Assert.Empty(order.StereotypeSubjects);
        Assert.DoesNotContain("k:entity", order.Subjects.MembershipKeys);
        Assert.False(Record(resolved, order.Id).ContainsKey("stereotypeSubjects"));
        var row = (await store.GetIndexAsync(Ct)).Single(r => r.Id == order.Id);
        Assert.Equal(Order, row.Subject);
        Assert.False(JsonSerializer.SerializeToNode(row, Web)!.AsObject().ContainsKey("covers"));
    }
}
