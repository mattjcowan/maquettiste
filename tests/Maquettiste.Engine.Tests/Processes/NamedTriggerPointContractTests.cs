using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The contract of trigger points, part 2 (trigger-points-design.md sections 5 and 9, decisions 20 and 33): the schema of a named trigger point
/// and the shared <c>noBypass</c>; a model holding named trigger points loads, indexes them, their sets and their references, names them by
/// package, counts them, creates them in their folder and round-trips them.
/// </summary>
public sealed class NamedTriggerPointContractTests
{
    private const string Package = "01JPKG00000000000000000001"; // Sales
    private const string Order = "01JENT00000000000000000001";
    private const string Customer = "01JENT00000000000000000011"; // no lifecycle, carries approvable
    private const string Supplier = "01JENT00000000000000000012"; // no lifecycle, carries approvable
    private const string CustomerId = "01JATT00000000000000000091";
    private const string CustomerNo = "01JATT00000000000000000092";
    private const string SupplierId = "01JATT00000000000000000093";
    private const string Approvable = "01JSTR00000000000000000011";
    private const string Number = "01JTPN00000000000000000001"; // a before action on create with a set, noBypass
    private const string Archive = "01JTPN00000000000000000002"; // a replace on delete, the team's code, by stereotype, less Supplier
    private const string Kept = "01JTPN00000000000000000003"; // a warning check on modify of one attribute
    private const string NumberSet = "01JTPS00000000000000000001";
    private const string NumberFile = "model/trigger-points/customer-number.json";
    private const string ArchiveFile = "model/trigger-points/archive-approvables.json";
    private const string KeptFile = "model/trigger-points/number-kept.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string NumberPoint = $$"""
        {"kind":"trigger-point","id":"{{Number}}","name":"CustomerNumber","type":"action","timing":"before","verbs":["create"],
         "subjects":[{"entity":"{{Customer}}"}],"noBypass":true,
         "sets":[{"id":"{{NumberSet}}","attribute":"{{CustomerNo}}","value":{"value":"C-00042"} }],
         "description":"Gives each customer its number, such as C-00042."}
        """;

    private static readonly string ArchivePoint = $$"""
        {"kind":"trigger-point","id":"{{Archive}}","name":"ArchiveApprovables","package":"{{Package}}","type":"replace","verbs":["delete"],
         "subjects":[{"stereotype":"approvable"}],"exceptEntities":["{{Supplier}}"],"displayName":"Archive approvables"}
        """;

    private static readonly string KeptPoint = $$"""
        {"kind":"trigger-point","id":"{{Kept}}","name":"NumberKept","type":"check","severity":"warning","verbs":["modify"],
         "subjects":[{"entity":"{{Customer}}"}],"entities":["{{Customer}}"],"attributes":["{{CustomerNo}}"],
         "condition":{"op":"or","args":[{"op":"isNull","args":[{"path":["{{CustomerNo}}"],"old":true}]},{"op":"isNotNull","args":[{"path":["{{CustomerNo}}"]}]}]} }
        """;

    private static string Canonical(string json, string schema, string modelPath) =>
        Encoding.UTF8.GetString(TestServices.Json.Write(JsonNode.Parse(json)!, schema, ".maquettiste/" + modelPath));

    private static IReadOnlyList<Diagnostic> Evaluate(string schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        return TestServices.Schemas.Evaluate(schema, document.RootElement, "model/x.json");
    }

    private static void Valid(string schema, string json) =>
        Assert.True(Evaluate(schema, json) is { Count: 0 }, string.Join("; ", Evaluate(schema, json).Select(d => d.JsonPointer + " " + d.Message)));

    private static void Invalid(string schema, string json, string pointer)
    {
        var found = Evaluate(schema, json);
        Assert.True(found.Any(d => d.Rule == "MQ1002" && (d.JsonPointer ?? "").StartsWith(pointer, StringComparison.Ordinal)),
            pointer + ": " + string.Join("; ", found.Select(d => d.JsonPointer + " " + d.Message)));
    }

    private static string Point(string body) => $$"""{"kind":"trigger-point","id":"{{Number}}","name":"T",{{body}}}""";

    private static IReadOnlyList<NameEntry> Find(ModelSnapshot model, string text)
    {
        Assert.True(QualifiedName.TryParse(text, out var name, out var error), error);
        return model.Names(name.Kind).Find(name.Path);
    }

    private static void WriteModel(LoaderHarness h)
    {
        h.CopyFixture("models", "process-basics");
        h.Write("model/vocabularies/stereotypes/approvable.json", Canonical($$"""
            {"kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"Approvable","appliesTo":["entity"]}
            """, "stereotype.json", "model/vocabularies/stereotypes/approvable.json"));
        h.Write("model/entities/customer.json", Canonical($$"""
            {"kind":"entity","id":"{{Customer}}","name":"Customer","package":"{{Package}}","stereotypes":["approvable"],
             "key":{"attributes":["{{CustomerId}}"],"strategy":"uuid-v7"},
             "attributes":[{"id":"{{CustomerId}}","name":"id","type":"uuid","required":true},{"id":"{{CustomerNo}}","name":"number","type":"string"}]}
            """, "entity.json", "model/entities/customer.json"));
        h.Write("model/entities/supplier.json", Canonical($$"""
            {"kind":"entity","id":"{{Supplier}}","name":"Supplier","package":"{{Package}}","stereotypes":["approvable"],
             "key":{"attributes":["{{SupplierId}}"],"strategy":"uuid-v7"},
             "attributes":[{"id":"{{SupplierId}}","name":"id","type":"uuid","required":true}]}
            """, "entity.json", "model/entities/supplier.json"));
        h.Write(NumberFile, Canonical(NumberPoint, "trigger-point.json", NumberFile));
        h.Write(ArchiveFile, Canonical(ArchivePoint, "trigger-point.json", ArchiveFile));
        h.Write(KeptFile, Canonical(KeptPoint, "trigger-point.json", KeptFile));
    }

    // ---- schemas ----

    [Fact]
    public void A_named_trigger_point_takes_the_shared_body_with_subjects_and_except_entities()
    {
        Valid("trigger-point.json", NumberPoint);
        Valid("trigger-point.json", ArchivePoint);
        Valid("trigger-point.json", KeptPoint);
        Valid("trigger-point.json", Point($$""" "type":"check","verbs":["delete"],"subjects":[{"entity":"{{Customer}}"},{"stereotype":"approvable"}] """));

        // Subjects: at least one, each exactly one of an entity id and a stereotype key, with no id of its own.
        Invalid("trigger-point.json", Point(""" "type":"check","verbs":["delete"] """), "");
        Invalid("trigger-point.json", Point(""" "type":"check","verbs":["delete"],"subjects":[] """), "/subjects");
        Invalid("trigger-point.json", Point($$""" "type":"check","verbs":["delete"],"subjects":[{"entity":"{{Customer}}","stereotype":"approvable"}] """), "/subjects/0");
        Invalid("trigger-point.json", Point(""" "type":"check","verbs":["delete"],"subjects":[{}] """), "/subjects/0");
        Invalid("trigger-point.json", Point($$""" "type":"check","verbs":["delete"],"subjects":[{"id":"{{Number}}","entity":"{{Customer}}"}] """), "/subjects/0");
        Invalid("trigger-point.json", Point(""" "type":"check","verbs":["delete"],"subjects":[{"entity":"Customer"}] """), "/subjects/0/entity");
        Invalid("trigger-point.json", Point(""" "type":"check","verbs":["delete"],"subjects":[{"stereotype":""}] """), "/subjects/0/stereotype");
        Invalid("trigger-point.json", Point($$""" "type":"check","verbs":["delete"],"subjects":[{"entity":"{{Customer}}"},{"entity":"{{Customer}}"}] """), "/subjects");

        // exceptEntities are entity ids. States, targets and transitions belong to one lifecycle: the schema lets them through as id lists, and
        // MQ8102 says why they don't fit (NamedTriggerPointTests).
        string Body(string more) => Point($$""" "type":"check","verbs":["delete"],"subjects":[{"entity":"{{Customer}}"}],{{more}} """);
        Invalid("trigger-point.json", Body(""" "exceptEntities":["Supplier"] """), "/exceptEntities");
        Invalid("trigger-point.json", Body($$""" "exceptEntities":["{{Supplier}}","{{Supplier}}"] """), "/exceptEntities");
        Valid("trigger-point.json", Body($$""" "states":["{{Supplier}}"] """));
        Valid("trigger-point.json", Body($$""" "targets":["{{Supplier}}"] """));
        Valid("trigger-point.json", Body($$""" "transitions":["{{Supplier}}"] """));
        Invalid("trigger-point.json", Body(""" "states":["Open"] """), "/states/0");
        Invalid("trigger-point.json", Body(""" "noBypass":1 """), "/noBypass");
        Invalid("trigger-point.json", Body(""" "timing":"later" """), "/timing");
        Invalid("trigger-point.json", Point($$""" "verbs":["delete"],"subjects":[{"entity":"{{Customer}}"}] """), "");
        Invalid("trigger-point.json", Point($$""" "type":"check","subjects":[{"entity":"{{Customer}}"}] """), "");

        // The shared body takes noBypass, with false as its default, for inline and named points alike.
        var body = JsonNode.Parse(TestServices.Schemas.GetFileBytes("common.json").Span)!["$defs"]!["triggerBody"]!;
        Assert.Equal((false, "boolean"), (body["properties"]!["noBypass"]!["default"]!.GetValue<bool>(), body["properties"]!["noBypass"]!["type"]!.GetValue<string>()));
        Assert.Equal("noBypass", body["x-order"]!.AsArray()[^1]!.GetValue<string>());
    }

    // ---- loading ----

    [Fact]
    public async Task Named_trigger_points_load_index_name_count_and_round_trip_in_schema_order()
    {
        using var h = new LoaderHarness();
        WriteModel(h);
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var model = store.Current!;

        foreach (var file in new[] { NumberFile, ArchiveFile, KeptFile })
            Assert.DoesNotContain(model.LoadDiagnostics, d => (d.FilePath ?? "").EndsWith(file, StringComparison.Ordinal) && d.Rule is "MQ1002" or "MQ1003");
        var errors = model.LoadDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && !RuleCatalog.IsReplayFinding(d.Rule)).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(d => d.Rule + " " + d.FilePath + d.JsonPointer + " " + d.Message)));

        // The files keep their keys in schema order: the subjects and the opt-out after the package, the body, then the description.
        Assert.Equal(["$schema", "kind", "id", "name", "subjects", "type", "timing", "verbs", "sets", "noBypass", "description"],
            JsonNode.Parse(h.Read(NumberFile))!.AsObject().Select(p => p.Key));
        Assert.Equal(["$schema", "kind", "id", "name", "displayName", "package", "subjects", "exceptEntities", "type", "verbs"],
            JsonNode.Parse(h.Read(ArchiveFile))!.AsObject().Select(p => p.Key));
        Assert.Equal(["$schema", "kind", "id", "name", "type", "severity", "verbs", "entities", "attributes", "condition"],
            JsonNode.Parse(h.Read(KeptFile))!.AsObject().Select(p => p.Key).Where(k => k != "subjects"));

        // The records.
        var number = Assert.IsType<TriggerPoint>(model.GetDocument(Number)!.Element);
        Assert.Equal((ElementKind.TriggerPoint, TriggerPointType.Action, (TriggerTiming?)TriggerTiming.Before, true, false, (string?)null),
            (number.Kind, number.Type, number.Timing, number.NoBypass, number.TeamCode, number.Package));
        Assert.Equal([Customer], number.Subjects.Select(s => s.Entity));
        Assert.Null(Assert.Single(number.Subjects).Stereotype);
        Assert.Equal(["create"], number.Verbs);
        Assert.Equal((NumberSet, CustomerNo), (Assert.Single(number.Sets).Id, number.Sets[0].Attribute));
        Assert.Equal("Gives each customer its number, such as C-00042.", number.Description!.Text);
        var archive = Assert.IsType<TriggerPoint>(model.GetDocument(Archive)!.Element);
        Assert.Equal((TriggerPointType.Replace, true, false, Package, "Archive approvables"),
            (archive.Type, archive.TeamCode, archive.NoBypass, archive.Package, archive.DisplayName));
        Assert.Equal(("approvable", (string?)null), (Assert.Single(archive.Subjects).Stereotype, archive.Subjects[0].Entity));
        Assert.Equal([Supplier], archive.ExceptEntities);
        var kept = Assert.IsType<TriggerPoint>(model.GetDocument(Kept)!.Element);
        Assert.Equal((TriggerSeverity.Warning, false), (kept.Severity!.Value, kept.TeamCode));
        Assert.Equal([Customer], kept.Entities);
        Assert.Equal([CustomerNo], kept.Attributes);
        Assert.True(kept.Condition!.Args![0].Args![0].Old);

        // The records write back to the same bytes.
        foreach (var (path, id) in new[] { (NumberFile, Number), (ArchiveFile, Archive), (KeptFile, Kept) })
        {
            var element = model.GetDocument(id)!.Element;
            var node = JsonSerializer.SerializeToNode(element, element.GetType(), EngineJson.Options)!;
            Assert.Equal(h.Read(path), Encoding.UTF8.GetString(TestServices.Json.Write(node, "trigger-point.json", ".maquettiste/" + path)));
        }

        // The index: a named trigger point is an element of kind trigger-point in its folder; its sets are its parts.
        Assert.Equal("trigger-point", model.Index.Entries[Number].Kind);
        Assert.Equal(("set", Number, "/sets/0"), (model.Index.Entries[NumberSet].Kind, model.Index.Entries[NumberSet].OwnerId, model.Index.Entries[NumberSet].JsonPointer));
        Assert.Equal("trigger-point", Assert.Single(model.Summaries(), s => s.Id == Archive).Kind);
        Assert.EndsWith(ArchiveFile, model.GetDocument(Archive)!.Path, StringComparison.Ordinal);
        Assert.Equal(3, Assert.Single(ModelPages.Kinds(model.Summaries(), false).Kinds, k => k.Kind == "trigger-point").Count);

        // What it names are references: its subjects (the stereotype by its id), its opt-out, its places and its sets' attributes.
        Assert.Contains(await store.GetReferencesAsync(Customer, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (Number, Number, "/subjects/0/entity"));
        Assert.Contains(await store.GetReferencesAsync(Approvable, Ct), r => (r.FromElementId, r.JsonPointer) == (Archive, "/subjects/0/stereotype"));
        Assert.Contains(await store.GetReferencesAsync(Supplier, Ct), r => (r.FromElementId, r.JsonPointer) == (Archive, "/exceptEntities/0"));
        Assert.Contains(await store.GetReferencesAsync(Package, Ct), r => (r.FromElementId, r.JsonPointer) == (Archive, "/package"));
        Assert.Contains(await store.GetReferencesAsync(CustomerNo, Ct), r => (r.FromElementId, r.FromId, r.JsonPointer) == (Number, NumberSet, "/sets/0/attribute"));
        Assert.Contains(await store.GetReferencesAsync(CustomerNo, Ct), r => (r.FromElementId, r.JsonPointer) == (Kept, "/attributes/0"));
        Assert.Contains(await store.GetReferencesAsync(Customer, Ct), r => (r.FromElementId, r.JsonPointer) == (Kept, "/entities/0"));
        Assert.DoesNotContain(await store.GetReferencesAsync(Order, Ct), r => r.FromElementId is Number or Archive or Kept);

        // A named trigger point takes its package's forms; it is localizable as every domain kind is.
        Assert.Equal(Number, Assert.Single(Find(model, "@trigger-point:CustomerNumber")).Id);
        Assert.Equal(Archive, Assert.Single(Find(model, "@trigger-point:Sales.ArchiveApprovables")).Id);
        Assert.Empty(Find(model, "@process-trigger-point:CustomerNumber"));
        Assert.Contains(ElementKind.TriggerPoint, (IReadOnlySet<ElementKind>)LocaleChains.ElementKinds);
        Assert.Contains("trigger-point", (IReadOnlySet<string>)LocaleChains.NodeKinds);
    }

    [Fact]
    public async Task A_named_trigger_point_created_through_the_store_lands_in_the_trigger_points_folder()
    {
        using var h = new LoaderHarness();
        WriteModel(h);
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);

        var created = await store.CreateAsync(Encoding.UTF8.GetBytes($$"""
            {"kind":"trigger-point","name":"NoDeleteWithOrders","type":"check","verbs":["delete"],"subjects":[{"entity":"{{Customer}}"}]}
            """), ChangeSource.Editor, Ct);

        Assert.True(created.Outcome == SaveOutcome.Saved, string.Join("; ", created.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        Assert.EndsWith("model/trigger-points/no-delete-with-orders.json", store.Current!.GetDocument(created.Id!)!.Path, StringComparison.Ordinal);
        Assert.IsType<TriggerPoint>(store.Current.GetDocument(created.Id!)!.Element);
    }

    [Fact]
    public void A_points_no_bypass_is_left_out_of_its_file_when_false()
    {
        // The canonical writer leaves out a key at its default, so a project that never writes noBypass keeps its files.
        var point = new ProcessTriggerPoint { Id = Number, Name = "N", Type = TriggerPointType.Check, Verbs = ["delete"] };
        var process = new Process { Id = "01JPRC00000000000000000001", Name = "Flow", Use = ProcessUse.Lifecycle, TriggerPoints = [point] };
        JsonNode Written(Element element, string schema, string path) =>
            JsonNode.Parse(TestServices.Json.Write(JsonSerializer.SerializeToNode<Element>(element, EngineJson.Options)!, schema, ".maquettiste/" + path))!;
        Assert.False(Written(process, "process.json", "model/processes/flow.json")["triggerPoints"]![0]!.AsObject().ContainsKey("noBypass"));
        Assert.True(Written(process with { TriggerPoints = [point with { NoBypass = true }] }, "process.json", "model/processes/flow.json")["triggerPoints"]![0]!["noBypass"]!.GetValue<bool>());

        var named = new TriggerPoint { Id = Number, Name = "N", Type = TriggerPointType.Check, Verbs = ["delete"], Subjects = [new TriggerPointSubject { Entity = Customer }] };
        var plain = Written(named, "trigger-point.json", "model/trigger-points/n.json").AsObject();
        Assert.Equal("trigger-point", plain["kind"]!.GetValue<string>());
        Assert.False(plain.ContainsKey("noBypass") || plain.ContainsKey("exceptEntities"));
        Assert.Equal($$"""[{"entity":"{{Customer}}"}]""", plain["subjects"]!.ToJsonString());
    }
}
