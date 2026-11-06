using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// <c>sync-codes</c> (lifecycle-design.md decision 19) over the <c>process-basics</c> fixture, to which the tests add a reference type Stage
/// (string codes), a reference type Level (integer codes), Order.stage and Order.level, and a lifecycle Workflow with no subject: a state
/// keeps its code, else matches a code by name (case, <c>_</c> and <c>-</c> ignored), else a row by label; an ambiguous match is unmatched
/// and <c>stateCodes</c> gives the rest; the binding goes in the form the file has; no row is created; refusals are MQ9019.
/// </summary>
public sealed class SyncCodesTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Order = "01JENT00000000000000000001";
    private const string P = "01JPRC00000000000000000001";
    private const string W = "01JPRC00000000000000000006";
    private const string Stage = "01JRTP00000000000000000021";
    private const string Level = "01JRTP00000000000000000031";
    private const string StageSeed = "01JSDE00000000000000000021";
    private const string StageAttr = "01JATT00000000000000000060";
    private const string LevelAttr = "01JATT00000000000000000070";
    private const string Draft = "01JSTA00000000000000000061";
    private const string InReview = "01JSTA00000000000000000062";
    private const string Shipped = "01JSTA00000000000000000063";
    private const string Done = "01JSTA00000000000000000064";
    private const string Cancelled = "01JSTA00000000000000000065";
    private const string WFile = "model/processes/workflow.json";
    private const string PFile = "model/processes/order-lifecycle.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Workflow(string extra = "") => $$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{W}}","name":"Workflow","package":"{{Package}}","use":"lifecycle"{{extra}},
         "states":[{"id":"{{Draft}}","name":"Draft"},{"id":"{{InReview}}","name":"InReview","displayName":"In review"},{"id":"{{Shipped}}","name":"Shipped"},
                   {"id":"{{Done}}","name":"Done","type":"final"},{"id":"{{Cancelled}}","name":"Cancelled","type":"final"}],
         "events":[{"id":"01JSTA00000000000000000066","name":"next"},{"id":"01JSTA00000000000000000067","name":"cancel"}],
         "transitions":[{"id":"01JSTA00000000000000000071","source":"{{Draft}}","event":"01JSTA00000000000000000066","targets":["{{InReview}}"]},
                        {"id":"01JSTA00000000000000000072","source":"{{InReview}}","event":"01JSTA00000000000000000066","targets":["{{Shipped}}"]},
                        {"id":"01JSTA00000000000000000073","source":"{{Shipped}}","event":"01JSTA00000000000000000066","targets":["{{Done}}"]},
                        {"id":"01JSTA00000000000000000074","source":"{{Draft}}","event":"01JSTA00000000000000000067","targets":["{{Cancelled}}"]}]}
        """;

    private static string Rows(params (string Id, string Code, string Label)[] rows) =>
        string.Join(",", rows.Select(r => $$"""{"id":"{{r.Id}}","values":[{{r.Code}},"{{r.Label}}"]}"""));

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        h.Write("model/reference-types/stage.json", $$"""{"$schema":"../../.schema/v1/reference-type.json","kind":"reference-type","id":"{{Stage}}","name":"Stage","package":"{{Package}}","code":{"id":"01JRTP00000000000000000022","length":16},"label":{"id":"01JRTP00000000000000000023"} }""");
        h.Write("model/seeds/stage/stage.json", $$"""{"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"{{StageSeed}}","name":"Stage","target":"{{Stage}}","columns":["code","label"],"rows":[{{Rows(
            ("01JRWS00000000000000000021", "\"draft\"", "Draft (new)"), ("01JRWS00000000000000000022", "\"IN_REVIEW\"", "Being reviewed"), ("01JRWS00000000000000000023", "\"SHP\"", "Shipped"),
            ("01JRWS00000000000000000024", "\"D1\"", "Done"), ("01JRWS00000000000000000025", "\"D2\"", "Done"), ("01JRWS00000000000000000026", "\"CXL\"", "Void"))}}]}""");
        h.Write("model/reference-types/level.json", $$"""{"$schema":"../../.schema/v1/reference-type.json","kind":"reference-type","id":"{{Level}}","name":"Level","package":"{{Package}}","code":{"id":"01JRTP00000000000000000032","type":"int16"},"label":{"id":"01JRTP00000000000000000033"} }""");
        h.Write("model/seeds/level/level.json", $$"""{"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01JSDE00000000000000000031","name":"Level","target":"{{Level}}","columns":["code","label"],"rows":[{{Rows(
            ("01JRWS00000000000000000031", "1", "Draft"), ("01JRWS00000000000000000032", "2", "In review"), ("01JRWS00000000000000000033", "3", "Shipped"),
            ("01JRWS00000000000000000034", "4", "Done"), ("01JRWS00000000000000000035", "5", "Cancelled"))}}]}""");
        var order = JsonNode.Parse(h.Read("model/entities/order.json"))!.AsObject();
        order["attributes"]!.AsArray().Add(new JsonObject { ["id"] = StageAttr, ["name"] = "stage", ["type"] = new JsonObject { ["ref"] = Stage }, ["default"] = "draft" });
        order["attributes"]!.AsArray().Add(new JsonObject { ["id"] = LevelAttr, ["name"] = "level", ["type"] = new JsonObject { ["ref"] = Level }, ["default"] = 1 });
        h.Write("model/entities/order.json", order.ToJsonString());
        h.Write(WFile, Workflow());
        prepare?.Invoke(h);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (h, store);
    }

    private static JsonObject File(LoaderHarness h, string path) => JsonNode.Parse(h.Read(path))!.AsObject();

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static BatchOperation SyncCodes(string process, string entity, string attribute, IReadOnlyDictionary<string, JsonElement>? stateCodes = null) =>
        new(BatchOp.SyncCodes, process, null, null, Entity: entity, Attribute: attribute, StateCodes: stateCodes);

    private static void Applied(BatchResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join(" | ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

    private static Diagnostic Refusal(BatchResult result)
    {
        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var refusal = Assert.Single(result.Items[0].Diagnostics);
        Assert.Equal(("MQ9019", "/operations/0"), (refusal.Rule, refusal.JsonPointer));
        return refusal;
    }

    private static string? CodeOf(JsonObject process, int state) => process["states"]![state]!["code"]?.ToJsonString();

    [Fact]
    public async Task A_dry_run_lists_what_it_would_write_and_what_is_unmatched()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        var before = h.Read(WFile);

        var call = await store.SyncCodesAsync(W, Order, StageAttr, null, dryRun: true, null, ChangeSource.Editor, Ct);

        Assert.Equal(ProcessCallStatus.Ok, call.Status);
        var plan = call.Value!;
        Assert.False(plan.Applied);
        Assert.True(plan.Binds);
        Assert.Equal([("Draft", "\"draft\"", "name"), ("InReview", "\"IN_REVIEW\"", "name"), ("Shipped", "\"SHP\"", "label")],
            plan.Codes.Select(c => (c.Path, c.Code.GetRawText(), c.Source)));
        Assert.Equal([("Done", "\"D1\",\"D2\""), ("Cancelled", "")], plan.Unmatched.Select(u => (u.Path, string.Join(",", u.Candidates.Select(c => c.GetRawText())))));
        Assert.Equal(before, h.Read(WFile));
    }

    [Fact]
    public async Task Applying_binds_in_subjects_and_writes_the_matched_codes_and_no_row()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Applied(await store.ApplyBatchAsync(new ModelBatch([SyncCodes(W, Order, StageAttr)]), ChangeSource.Editor, Ct));

        var workflow = File(h, WFile);
        var entry = Assert.Single(workflow["subjects"]!.AsArray())!;
        Assert.Equal((Order, StageAttr), (entry["entity"]!.GetValue<string>(), entry["boundAttribute"]!.GetValue<string>()));
        Assert.Null(workflow["subject"]);
        Assert.Equal(["\"draft\"", "\"IN_REVIEW\"", "\"SHP\"", null, null], Enumerable.Range(0, 5).Select(i => CodeOf(workflow, i)));
        Assert.Equal(6, store.Current!.Get<Seed>(StageSeed)!.Rows.Count);
        var findings = (await EngineServices.Create(h.Options).Validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct)).Diagnostics;
        Assert.Equal([Done, Cancelled], findings.Where(d => d.Rule == "MQ9210").Select(d => d.ElementId));
    }

    [Fact]
    public async Task State_codes_give_the_rest_and_a_kept_code_is_never_taken_twice()
    {
        var (h, store) = await OpenAsync(x => x.Write(WFile, Workflow().Replace("\"name\":\"Cancelled\",", "\"name\":\"Cancelled\",\"code\":\"SHP\",", StringComparison.Ordinal)));
        using var _ = h;
        await using var __ = store;

        var plan = await store.PlanSyncCodesAsync(W, Order, StageAttr, new Dictionary<string, JsonElement> { [Done] = Json("\"D2\"") }, Ct);

        Assert.Null(plan.Problem);
        Assert.Equal([("Draft", "name"), ("InReview", "name"), ("Done", "given"), ("Cancelled", "kept")], plan.Codes.Select(c => (c.Path, c.Source)));
        Assert.Equal(("Shipped", "\"SHP\""), plan.Unmatched.Select(u => (u.Path, string.Join(",", u.Candidates.Select(c => c.GetRawText())))).Single());

        // stateCodes replaces a code a state has.
        Applied(await store.ApplyBatchAsync(new ModelBatch([SyncCodes(W, Order, StageAttr, new Dictionary<string, JsonElement>
        {
            [Done] = Json("\"D2\""), [Cancelled] = Json("\"CXL\""), [Shipped] = Json("\"SHP\""),
        })]), ChangeSource.Editor, Ct));
        Assert.Equal(["\"draft\"", "\"IN_REVIEW\"", "\"SHP\"", "\"D2\"", "\"CXL\""], Enumerable.Range(0, 5).Select(i => CodeOf(File(h, WFile), i)));
    }

    [Fact]
    public async Task Integer_codes_are_written_as_numbers()
    {
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;

        Applied(await store.ApplyBatchAsync(new ModelBatch([SyncCodes(W, Order, LevelAttr)]), ChangeSource.Editor, Ct));

        Assert.Equal(["1", "2", "3", "4", "5"], Enumerable.Range(0, 5).Select(i => CodeOf(File(h, WFile), i)));
    }

    [Fact]
    public async Task The_one_subject_form_keeps_its_form_and_takes_only_its_subject()
    {
        var (h, store) = await OpenAsync(x => x.Write("model/entities/return.json", $$"""{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"01JENT00000000000000000005","name":"Return","package":"{{Package}}"}"""));
        using var _ = h;
        await using var __ = store;

        var other = Refusal(await store.ApplyBatchAsync(new ModelBatch([SyncCodes(P, "01JENT00000000000000000009", StageAttr)]), ChangeSource.Editor, Ct));
        Assert.Contains("is not an entity", other.Message, StringComparison.Ordinal);
        const string returnId = "01JENT00000000000000000005";
        var refused = Refusal(await store.ApplyBatchAsync(new ModelBatch([SyncCodes(P, returnId, StageAttr)]), ChangeSource.Editor, Ct));
        Assert.Contains("names entity 'Order' as its one subject", refused.Message, StringComparison.Ordinal);

        Applied(await store.ApplyBatchAsync(new ModelBatch([SyncCodes(P, Order, StageAttr)]), ChangeSource.Editor, Ct));

        var lifecycle = File(h, PFile);
        Assert.Equal((Order, StageAttr), (lifecycle["subject"]!.GetValue<string>(), lifecycle["boundAttribute"]!.GetValue<string>()));
        Assert.Null(lifecycle["subjects"]);
        Assert.Equal("\"draft\"", CodeOf(lifecycle, 0));
    }

    [Fact]
    public async Task A_lifecycle_that_lists_its_subjects_gains_an_entry_for_another_entity()
    {
        var (h, store) = await OpenAsync(x =>
        {
            x.Write("model/entities/return.json", $$"""{"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"01JENT00000000000000000005","name":"Return","package":"{{Package}}","attributes":[{"id":"01JATT00000000000000000075","name":"stage","type":{"ref":"{{Stage}}"},"default":"draft"}]}""");
            x.Write(WFile, Workflow(",\"subjects\":[{\"id\":\"01JSBJ00000000000000000061\",\"entity\":\"01JENT00000000000000000005\"}]"));
        });
        using var _ = h;
        await using var __ = store;

        Applied(await store.ApplyBatchAsync(new ModelBatch([SyncCodes(W, "01JENT00000000000000000005", "01JATT00000000000000000075")]), ChangeSource.Editor, Ct));
        Applied(await store.ApplyBatchAsync(new ModelBatch([SyncCodes(W, Order, StageAttr)]), ChangeSource.Editor, Ct));

        var entries = File(h, WFile)["subjects"]!.AsArray();
        Assert.Equal([("01JENT00000000000000000005", "01JATT00000000000000000075"), (Order, StageAttr)],
            entries.Select(e => (e!["entity"]!.GetValue<string>(), e["boundAttribute"]!.GetValue<string>())));
        Assert.Equal("01JSBJ00000000000000000061", entries[0]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("not a process", "is not a process")]
    [InlineData("library", "is a library")]
    [InlineData("orchestration", "is an orchestration")]
    [InlineData("attribute of another entity", "is not an attribute of entity 'Order'")]
    [InlineData("uuid attribute", "is typed by uuid, which cannot hold a state")]
    [InlineData("unknown state", "is not a state of process 'Workflow'")]
    [InlineData("choice state", "is a choice state")]
    [InlineData("written twice", "is also written by another operation of this batch")]
    public async Task Refusals_are_MQ9019(string why, string message)
    {
        var (h, store) = await OpenAsync(x =>
        {
            if (why is "library" or "orchestration")
                x.Write(WFile, Workflow().Replace("\"use\":\"lifecycle\"", why == "library" ? "\"use\":\"library\"" : "\"use\":\"orchestration\"", StringComparison.Ordinal));
        });
        using var _ = h;
        await using var __ = store;
        var operations = why switch
        {
            "not a process" => [SyncCodes(Order, Order, StageAttr)],
            "attribute of another entity" => [SyncCodes(W, Order, "01JATT00000000000000000099")],
            "uuid attribute" => [SyncCodes(W, Order, "01JATT00000000000000000001")],
            "unknown state" => [SyncCodes(W, Order, StageAttr, new Dictionary<string, JsonElement> { ["01JSTA00000000000000000099"] = Json("\"X\"") })],
            "choice state" => [SyncCodes(P, Order, StageAttr, new Dictionary<string, JsonElement> { ["01JSTA00000000000000000002"] = Json("\"X\"") })],
            "written twice" => [SyncCodes(W, Order, StageAttr), new BatchOperation(BatchOp.Update, W, store.Current!.GetDocument(W)!.Hash, Json(Workflow()))],
            _ => new List<BatchOperation> { SyncCodes(W, Order, StageAttr) },
        };

        var result = await store.ApplyBatchAsync(new ModelBatch(operations), ChangeSource.Editor, Ct);

        Assert.Contains(message, Refusal(result).Message, StringComparison.Ordinal);
        Assert.Null(File(h, WFile)["subjects"]);
    }
}
