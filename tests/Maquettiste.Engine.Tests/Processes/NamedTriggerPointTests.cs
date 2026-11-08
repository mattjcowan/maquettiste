using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// Trigger points, part 2 (trigger-points-design.md decisions 33 and 34, sections 6, 7, 9 and 15): named trigger points in the rules, the
/// references and deletes, the index, the one evaluator (a type with no lifecycle, and the order of decision 34) and the resolved model.
/// </summary>
public sealed class NamedTriggerPointTests
{
    private const string Package = "01JPKG00000000000000000001"; // Sales
    private const string P = "01JPRC00000000000000000001"; // OrderLifecycle, of Order
    private const string Order = "01JENT00000000000000000001";
    private const string Party = "01JENT00000000000000000021"; // a base, no lifecycle
    private const string Customer = "01JENT00000000000000000022"; // derived from Party, carries approvable, no lifecycle
    private const string Supplier = "01JENT00000000000000000023"; // carries approvable, no lifecycle
    private const string PartyId = "01JATT00000000000000000921";
    private const string PartyNo = "01JATT00000000000000000922";
    private const string SupplierId = "01JATT00000000000000000923";
    private const string Approvable = "01JSTR00000000000000000021";
    private const string PartyAudit = "01JTPN00000000000000000021"; // Party, before create
    private const string AZero = "01JTPN00000000000000000022"; // Customer, before create
    private const string CustomerNumber = "01JTPN00000000000000000023"; // Customer, before create, sets the number, noBypass
    private const string ApprovableStamp = "01JTPN00000000000000000024"; // approvable, before create
    private const string OrderModify = "01JTPN00000000000000000025"; // Order, a check on modify
    private const string LifecycleModify = "01JTGP00000000000000000025"; // OrderLifecycle's check on modify
    private const string NumberSet = "01JTPS00000000000000000021";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Point(string id, string name, string body) =>
        $$"""{"kind":"trigger-point","id":"{{id}}","name":"{{name}}","package":"{{Package}}",{{body}}}""";

    private static readonly Dictionary<string, string> BasePoints = new(StringComparer.Ordinal)
    {
        [PartyAudit] = Point(PartyAudit, "PartyAudit", $$""" "subjects":[{"entity":"{{Party}}"}],"type":"action","timing":"before","verbs":["create"] """),
        [AZero] = Point(AZero, "AZero", $$""" "subjects":[{"entity":"{{Customer}}"}],"type":"action","timing":"before","verbs":["create"] """),
        [CustomerNumber] = Point(CustomerNumber, "CustomerNumber",
            $$""" "subjects":[{"entity":"{{Customer}}"}],"type":"action","timing":"before","verbs":["create"],"noBypass":true,"sets":[{"id":"{{NumberSet}}","attribute":"{{PartyNo}}","value":{"value":"C-00042"} } ] """),
        [ApprovableStamp] = Point(ApprovableStamp, "ApprovableStamp", """ "subjects":[{"stereotype":"approvable"}],"type":"action","timing":"before","verbs":["create"] """),
        [OrderModify] = Point(OrderModify, "OrderModify", $$""" "subjects":[{"entity":"{{Order}}"}],"type":"check","verbs":["modify"] """),
    };

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(IReadOnlyDictionary<string, string>? points = null, bool lifecyclePoint = true)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/vocabularies/stereotypes/approvable.json",
            $$"""{"kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"Approvable","appliesTo":["entity"]}""");
        h.Write("model/entities/party.json", $$"""
            {"kind":"entity","id":"{{Party}}","name":"Party","package":"{{Package}}","key":{"attributes":["{{PartyId}}"],"strategy":"uuid-v7"},
             "attributes":[{"id":"{{PartyId}}","name":"id","type":"uuid","required":true},{"id":"{{PartyNo}}","name":"number","type":"string"}]}
            """);
        h.Write("model/entities/customer.json",
            $$"""{"kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}","base":"{{Party}}","stereotypes":["approvable"]}""");
        h.Write("model/entities/supplier.json", $$"""
            {"kind":"entity","id":"{{Supplier}}","name":"Supplier","package":"{{Package}}","stereotypes":["approvable"],
             "key":{"attributes":["{{SupplierId}}"],"strategy":"uuid-v7"},"attributes":[{"id":"{{SupplierId}}","name":"id","type":"uuid","required":true}]}
            """);
        foreach (var (id, json) in points ?? BasePoints)
            h.Write("model/trigger-points/p-" + id[^2..] + ".json", json);
        if (lifecyclePoint)
        {
            const string file = "model/processes/order-lifecycle.json";
            var process = JsonNode.Parse(h.Read(file))!.AsObject();
            process["triggerPoints"] = JsonNode.Parse($$"""[{"id":"{{LifecycleModify}}","name":"LifecycleModify","type":"check","verbs":["modify"]}]""");
            h.Write(file, process.ToJsonString());
        }

        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        return (h, store);
    }

    private static async Task<List<Diagnostic>> ValidateAsync(IReadOnlyDictionary<string, string> points)
    {
        var (h, store) = await OpenAsync(points);
        using var _ = h;
        await using var __ = store;
        return [.. (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics];
    }

    private static Dictionary<string, string> With(params (string Id, string Json)[] more)
    {
        var points = new Dictionary<string, string>(BasePoints, StringComparer.Ordinal);
        foreach (var (id, json) in more)
            points[id] = json;
        return points;
    }

    private static List<string> Hits(IEnumerable<Diagnostic> found, string rule) =>
        [.. found.Where(d => d.Rule == rule).Select(d => Path.GetFileName(d.FilePath) + ":" + d.JsonPointer).Order(StringComparer.Ordinal)];

    private static string Describe(IEnumerable<Diagnostic> found) => string.Join(" | ", found.Select(d => d.Rule + " " + Path.GetFileName(d.FilePath) + d.JsonPointer + " " + d.Message));

    [Fact]
    public async Task A_model_with_named_points_has_no_findings_and_the_rules_catch_what_does_not_fit()
    {
        var clean = await ValidateAsync(BasePoints);
        var errors = clean.Where(d => d.Severity == DiagnosticSeverity.Error && !RuleCatalog.IsReplayFinding(d.Rule)).ToList();
        Assert.True(errors.Count == 0, Describe(errors));

        // MQ8102: an exceptEntities entry no subject covers, and an entities entry outside what the subjects cover; inside them is fine.
        const string Odd = "01JTPN00000000000000000031";
        var found = await ValidateAsync(With((Odd, Point(Odd, "Odd",
            $$""" "subjects":[{"entity":"{{Party}}"}],"exceptEntities":["{{Customer}}","{{Order}}"],"entities":["{{Party}}","{{Supplier}}"],"type":"check","verbs":["delete"] """))));
        Assert.Equal(["p-31.json:/entities/1", "p-31.json:/exceptEntities/1"], Hits(found, "MQ8102"));

        // MQ8104: two named replaces on delete that both cover Customer, reported once by the later by name; a named replace and a lifecycle's
        // replace that both cover Order. exceptEntities settles the first.
        const string ArchiveAll = "01JTPN00000000000000000032";
        const string ArchiveCustomers = "01JTPN00000000000000000033";
        const string ArchiveOrders = "01JTPN00000000000000000034";
        var clash = With(
            (ArchiveAll, Point(ArchiveAll, "ArchiveAll", """ "subjects":[{"stereotype":"approvable"}],"type":"replace","verbs":["delete"] """)),
            (ArchiveCustomers, Point(ArchiveCustomers, "ArchiveCustomers", $$""" "subjects":[{"entity":"{{Customer}}"}],"type":"replace","verbs":["delete"] """)),
            (ArchiveOrders, Point(ArchiveOrders, "ArchiveOrders", $$""" "subjects":[{"entity":"{{Order}}"}],"type":"replace","verbs":["delete"] """)));
        Assert.Equal(["p-33.json:"], Hits(await ValidateAsync(clash), "MQ8104"));
        clash[ArchiveAll] = Point(ArchiveAll, "ArchiveAll", $$""" "subjects":[{"stereotype":"approvable"}],"exceptEntities":["{{Customer}}"],"type":"replace","verbs":["delete"] """);
        Assert.Empty(Hits(await ValidateAsync(clash), "MQ8104"));

        // MQ3001 for a name used twice in one package; MQ2003 for an unknown stereotype.
        const string Twin = "01JTPN00000000000000000035";
        const string Unknown = "01JTPN00000000000000000036";
        found = await ValidateAsync(With(
            (Twin, Point(Twin, "AZero", $$""" "subjects":[{"entity":"{{Supplier}}"}],"type":"check","verbs":["delete"] """)),
            (Unknown, Point(Unknown, "Unknown", """ "subjects":[{"stereotype":"no-such"}],"type":"check","verbs":["delete"] """))));
        Assert.Single(found, d => d.Rule == "MQ3001" && (d.FilePath ?? "").EndsWith("p-35.json", StringComparison.Ordinal));
        Assert.Equal(["p-36.json:/subjects/0/stereotype"], Hits(found, "MQ2003"));
    }

    [Fact]
    public async Task Named_points_run_for_an_entity_with_no_lifecycle_in_the_order_of_decision_34_and_before_lifecycle_points()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        var sequences = TriggerSequences.Of(model);
        var customer = model.Get<Entity>(Customer)!;

        // Customer has no lifecycle: its create runs the farthest base's point first, then at depth 0 the entity's by name, then the stereotype's.
        var run = sequences.For(new TriggerQuery { Verb = "create", Entity = customer });
        Assert.Equal(["PartyAudit", "AZero", "CustomerNumber", "ApprovableStamp"], run.Before.Select(r => r.Point.Name));
        Assert.All(run.Before, r => Assert.Null(r.Lifecycle));
        Assert.Equal(CustomerNumber, run.Before[2].Named!.Id);
        Assert.Equal(["PartyAudit", "AZero", "CustomerNumber", "ApprovableStamp"], sequences.NamedFor(customer).Select(p => p.Name));

        // Supplier only carries approvable; Party only has its own point.
        Assert.Equal(["ApprovableStamp"], sequences.For(new TriggerQuery { Verb = "create", Entity = model.Get<Entity>(Supplier) }).Before.Select(r => r.Point.Name));
        Assert.Equal(["PartyAudit"], sequences.For(new TriggerQuery { Verb = "create", Entity = model.Get<Entity>(Party) }).Before.Select(r => r.Point.Name));

        // Order's lifecycle acting: the named check comes before the lifecycle's at the same moment.
        var order = sequences.For(new TriggerQuery { Verb = "modify", Entity = model.Get<Entity>(Order), Process = model.Get<Process>(P) });
        Assert.Equal(["OrderModify", "LifecycleModify"], order.Checks.Select(r => r.Point.Name));
        Assert.Equal([null, P], order.Checks.Select(r => r.Lifecycle?.Id));

        // The entity route's answer: no lifecycle, the named point's subjects and noBypass; a move verb has no answer there.
        var described = sequences.Describe(customer, "create", new TriggerSequenceFilter())!;
        Assert.Null(described.Lifecycle);
        var number = Assert.Single(described.Steps, s => s.TriggerPoint == CustomerNumber);
        Assert.Equal((8, "before", (bool?)true, (string?)null), (number.Order, number.Step, number.NoBypass, number.Lifecycle));
        Assert.Equal(Customer, Assert.Single(number.Subjects!).Entity);
        Assert.Contains(described.Steps, s => s is { Order: 10, Step: "effect", TriggerPoint: null });
        Assert.Null(sequences.Describe(customer, "promote", new TriggerSequenceFilter()));
    }

    [Fact]
    public async Task Named_points_resolve_index_and_key_and_the_last_subject_takes_its_point_on_delete()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var snapshot = store.Current!;

        // The resolved model.
        var resolved = ResolutionKit.Resolve(snapshot);
        Assert.Equal(["AZero", "ApprovableStamp", "CustomerNumber", "OrderModify", "PartyAudit"], resolved.TriggerPoints.Select(p => p.Name));
        var number = Assert.IsType<RTriggerPoint>(resolved.Find(CustomerNumber));
        Assert.Equal(("trigger-point", true, (RProcess?)null, Package), (number.Kind, number.NoBypass, number.Process, number.Package!.Id));
        Assert.Equal("Customer", Assert.Single(number.Subjects).Entity!.Name);
        Assert.Equal([Customer], number.Covers.Select(e => e.Id));
        var stamp = Assert.IsType<RTriggerPoint>(resolved.Find(ApprovableStamp));
        Assert.Equal(Approvable, Assert.Single(stamp.Subjects).Stereotype!.Id);
        Assert.Equal(["Customer", "Supplier"], stamp.Covers.Select(e => e.Name));
        Assert.Equal(["PartyAudit", "AZero", "CustomerNumber", "ApprovableStamp"], resolved.Entity("Customer").TriggerPoints.Select(p => p.Name));
        Assert.Equal(["OrderModify"], resolved.Entity("Order").TriggerPoints.Select(p => p.Name));
        Assert.Equal("process-trigger-point", Assert.IsType<RTriggerPoint>(resolved.Find(LifecycleModify)).Kind);

        // A new entity derived from Customer renders covers again.
        string Hash(ModelSnapshot model, IEnumerable<string> keys) =>
            new DependencyHasher(model, ResolutionKit.Resolve(model), new PackSet([], []), new Dictionary<string, SchemaDiffResult>())
                .InputHash("unit", [.. keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        var coverKeys = number.Covers.MembershipKeys;
        var before = Hash(snapshot, coverKeys);
        var naming = TriggerNaming.Hash(snapshot);

        // The index rows.
        var row = Assert.Single(snapshot.Summaries(), s => s.Id == ApprovableStamp);
        Assert.Equal(("trigger-point", "action", (IReadOnlyList<string>?)null), (row.Kind, row.Type, row.Subjects));
        Assert.Equal(["create"], row.Verbs!);
        Assert.Equal([Approvable], row.SubjectStereotypes!);
        Assert.Equal([Customer], Assert.Single(snapshot.Summaries(), s => s.Id == CustomerNumber).Subjects!);

        var created = await store.CreateAsync(Encoding.UTF8.GetBytes(
            $$"""{"kind":"entity","name":"VipCustomer","package":"{{Package}}","base":"{{Customer}}"}"""), ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);
        Assert.NotEqual(before, Hash(store.Current!, coverKeys));
        Assert.Equal(["Customer", "VipCustomer"], Assert.IsType<RTriggerPoint>(ResolutionKit.Resolve(store.Current!).Find(CustomerNumber)).Covers.Select(e => e.Name));
        Assert.Equal(naming, TriggerNaming.Hash(store.Current!)); // which points name which verbs and subjects did not change

        // Deleting Customer: the points whose only subject it is go; a point keeps its other subjects; an exceptEntities entry is dropped.
        const string Both = "01JTPN00000000000000000041";
        const string Skips = "01JTPN00000000000000000042";
        await store.CreateAsync(Encoding.UTF8.GetBytes(Point(Both, "Both", $$""" "subjects":[{"entity":"{{Customer}}"},{"entity":"{{Supplier}}"}],"type":"check","verbs":["delete"] """)), ChangeSource.Editor, Ct);
        await store.CreateAsync(Encoding.UTF8.GetBytes(Point(Skips, "Skips", $$""" "subjects":[{"entity":"{{Party}}"}],"exceptEntities":["{{Customer}}"],"type":"check","verbs":["delete"] """)), ChangeSource.Editor, Ct);
        Assert.NotEqual(naming, TriggerNaming.Hash(store.Current!));
        var vip = store.Current!.All<Entity>().Single(e => e.Name == "VipCustomer");
        Assert.Equal(SaveOutcome.Saved, (await store.DeleteAsync(vip.Id, store.Current.GetDocument(vip.Id)!.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct)).Outcome);
        var deleted = await store.DeleteAsync(Customer, store.Current!.GetDocument(Customer)!.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct);
        Assert.True(deleted.Outcome == SaveOutcome.Saved, string.Join("; ", deleted.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var after = store.Current!;
        Assert.Null(after.GetDocument(CustomerNumber));
        Assert.Null(after.GetDocument(AZero));
        Assert.Equal([Supplier], Assert.IsType<TriggerPoint>(after.GetDocument(Both)!.Element).Subjects.Select(s => s.Entity));
        Assert.Empty(Assert.IsType<TriggerPoint>(after.GetDocument(Skips)!.Element).ExceptEntities);
        Assert.NotNull(after.GetDocument(ApprovableStamp));
    }

    [Fact]
    public async Task A_project_without_named_points_keeps_its_naming_hash_and_uses_no_named_lists()
    {
        var (h, store) = await OpenAsync(new Dictionary<string, string>());
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;
        Assert.Empty(TriggerSequences.Of(model).Named);
        var resolved = ResolutionKit.Resolve(model);
        Assert.Empty(resolved.TriggerPoints);
        Assert.Empty(resolved.Entity("Order").TriggerPoints);
        Assert.DoesNotContain(model.Summaries(), s => s.Kind == "trigger-point");

        // The naming key hashes only the lifecycles, as part 1 did.
        using var hash = new Maquettiste.Engine.Hashing.HashBuilder();
        hash.Add("tn").Add(P).Add("verbs").Add("modify").Add("replaces").Add("/");
        Assert.Equal(hash.Finish(), TriggerNaming.Hash(model));
    }
}
