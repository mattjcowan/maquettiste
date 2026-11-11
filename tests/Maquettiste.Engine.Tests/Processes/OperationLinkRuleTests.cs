using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Processes;

/// <summary>
/// The rules on processes linked to operations (processes-operations-design.md sections 4, 8 and 18): MQ3032, MQ3044, MQ9024, MQ9025, MQ9026
/// and the MQ9020 case, each with failing and passing cases; a call input's value typed like a set's value (MQ3102, MQ3103, text to tree,
/// renames); the preview's <c>input</c> site; and the incremental validation that keeps each finding fresh. The model is the invoice example,
/// invented: an <c>Invoice</c> lifecycle Draft, Issued, Paid; <c>issue</c> fires <c>Issue</c>, whose payload carries dueDays and issuedOn.
/// </summary>
public sealed partial class OperationLinkRuleTests
{
    private const string Package = "01JPKG00000000000000000001";
    private const string Invoice = "01JENT00000000000000000101";
    private const string InvoiceId = "01JATT00000000000000000101";
    private const string TermsDays = "01JATT00000000000000000102";
    private const string Part = "01JENT00000000000000000111";
    private const string PurchasedPart = "01JENT00000000000000000112";
    private const string Customer = "01JENT00000000000000000121";
    private const string Pair = "01JENT00000000000000000131";
    private const string Billed = "01JSTR00000000000000000101";

    private const string Lifecycle = "01JPRC00000000000000000101"; // InvoiceLifecycle, subject Invoice
    private const string PartLifecycle = "01JPRC00000000000000000102"; // subject PurchasedPart
    private const string Library = "01JPRC00000000000000000103";
    private const string Issue = "01JPRX00000000000000000101"; // payload dueDays (int32, required), issuedOn (date, required)
    private const string Pay = "01JPRX00000000000000000102";
    private const string Remind = "01JPRX00000000000000000103"; // raised by Nudge
    private const string Idle = "01JPRX00000000000000000104"; // used by no transition
    private const string RequestIssue = "01JPRX00000000000000000111"; // runs on Issue
    private const string Nudge = "01JPRX00000000000000000112";
    private const string OnEnter = "01JPRX00000000000000000113"; // runs on entering Issued
    private const string ChargeCard = "01JPRX00000000000000000131"; // the service step in Issued
    private const string InputA = "01JCXN00000000000000000101";
    private const string InputB = "01JCXN00000000000000000102";
    private const string InputC = "01JCXN00000000000000000103";
    private const string IssuedOn = "01JQPN00000000000000000103"; // issue.issuedOn, date, required
    private const string Settle = "01JQPN00000000000000000151"; // settle, on Invoice, named by no process
    private const string Draft = "01JSTA00000000000000000101";
    private const string Issued = "01JSTA00000000000000000102";
    private const string Paid = "01JSTA00000000000000000103";

    private const string IssueOp = "01JQPN00000000000000000101"; // issue, on Invoice, record
    private const string DueDays = "01JQPN00000000000000000102"; // int32, required
    private const string Notify = "01JQPN00000000000000000111"; // notify, on Customer, record
    private const string Message = "01JQPN00000000000000000112"; // string
    private const string Charge = "01JQPN00000000000000000121"; // chargeCard, on Invoice, record

    private const string LifecycleFile = "model/processes/invoice-lifecycle.json";
    private const string IssueFile = "model/operations/issue.json";

    private static readonly string[] LinkRules = ["MQ3032", "MQ3044", "MQ9020", "MQ9024", "MQ9025", "MQ9026", "MQ3102", "MQ3103"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The model's files by path, as JSON a case changes before it loads.</summary>
    private sealed class World
    {
        public SortedDictionary<string, JsonObject> Files { get; } = new(StringComparer.Ordinal);

        public JsonObject? Rules { get; set; }

        public JsonObject Process => Files[LifecycleFile];

        public JsonObject IssueOperation => Files[IssueFile];

        public JsonObject Action(string id) => Process["actions"]!.AsArray().Single(a => (string?)a!["id"] == id)!.AsObject();

        public JsonObject Call => Action(RequestIssue)["call"]!.AsObject();

        public JsonArray Inputs => Call["inputs"]!.AsArray();

        public JsonObject Step => Process["states"]![1]!["invoke"]![0]!.AsObject();

        public JsonArray Payload => Process["events"]![0]!["payload"]!.AsArray();

        public void Operation(string file, string json) => Files["model/operations/" + file + ".json"] = Obj(json);
    }

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Entity(string id, string name, string extra, string attributes) => $$"""
        {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Package}}"{{extra}},"attributes":[{{attributes}}]}
        """;

    private static string Op(string id, string name, string subject, string input, string extra = "") => $$"""
        {"$schema":"../../.schema/v1/operation.json","kind":"operation","id":"{{id}}","name":"{{name}}","package":"{{Package}}"{{(subject.Length > 0 ? $",\"subject\":\"{subject}\"" : "")}},
         "effect":"change","input":[{{input}}],"outcomes":[{"id":"{{id[..^3]}}9{{id[^2..]}}","name":"done"}]{{extra}}}
        """;

    private static World Model()
    {
        var w = new World();
        w.Files["model/vocabularies/stereotypes/billed.json"] = Obj($$"""
            {"$schema":"../../../.schema/v1/stereotype.json","kind":"stereotype","id":"{{Billed}}","key":"billed","name":"Billed","appliesTo":["entity"]}
            """);
        w.Files["model/entities/invoice.json"] = Obj(Entity(Invoice, "Invoice", $$""","key":{"attributes":["{{InvoiceId}}"]}""",
            $$"""{"id":"{{InvoiceId}}","name":"id","type":"uuid","required":true},{"id":"{{TermsDays}}","name":"termsDays","type":"int32"}"""));
        w.Files["model/entities/part.json"] = Obj(Entity(Part, "Part", ""","key":{"attributes":["01JATT00000000000000000111"]}""",
            """{"id":"01JATT00000000000000000111","name":"id","type":"uuid","required":true}"""));
        w.Files["model/entities/purchased-part.json"] = Obj(Entity(PurchasedPart, "PurchasedPart", $$""","base":"{{Part}}" """,
            """{"id":"01JATT00000000000000000112","name":"vendor","type":"string","length":40}"""));
        w.Files["model/entities/customer.json"] = Obj(Entity(Customer, "Customer", ""","stereotypes":["billed"],"key":{"attributes":["01JATT00000000000000000121"]}""",
            """{"id":"01JATT00000000000000000121","name":"id","type":"uuid","required":true}"""));
        w.Files["model/entities/pair.json"] = Obj(Entity(Pair, "Pair", ""","key":{"attributes":["01JATT00000000000000000131","01JATT00000000000000000132"]}""",
            """{"id":"01JATT00000000000000000131","name":"a","type":"uuid","required":true},{"id":"01JATT00000000000000000132","name":"b","type":"uuid","required":true}"""));
        w.Files[LifecycleFile] = Obj($$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{Lifecycle}}","name":"InvoiceLifecycle","package":"{{Package}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000101","entity":"{{Invoice}}"}],
             "states":[{"id":"{{Draft}}","name":"Draft"},
               {"id":"{{Issued}}","name":"Issued","entry":["{{OnEnter}}"],"invoke":[{"id":"{{ChargeCard}}","name":"ChargeCard","type":"service","operation":"{{Charge}}"}]},
               {"id":"{{Paid}}","name":"Paid","type":"final"}],
             "initial":"{{Draft}}",
             "transitions":[
               {"id":"01JTRN00000000000000000101","source":"{{Draft}}","event":"{{Issue}}","targets":["{{Issued}}"],"actions":["{{RequestIssue}}","{{Nudge}}"]},
               {"id":"01JTRN00000000000000000102","source":"{{Issued}}","event":"{{Pay}}","targets":["{{Paid}}"]},
               {"id":"01JTRN00000000000000000103","source":"{{Issued}}","event":"{{Remind}}","targets":["{{Issued}}"]},
               {"id":"01JTRN00000000000000000104","source":"{{Issued}}","trigger":"invoke-done","invoke":"{{ChargeCard}}","targets":["{{Paid}}"]}],
             "events":[
               {"id":"{{Issue}}","name":"Issue","payload":[{"id":"01JATT00000000000000000181","name":"dueDays","type":"int32","required":true},
                 {"id":"01JATT00000000000000000182","name":"issuedOn","type":"date","required":true}]},
               {"id":"{{Pay}}","name":"Pay"},{"id":"{{Remind}}","name":"Remind"},{"id":"{{Idle}}","name":"Idle"}],
             "actions":[
               {"id":"{{RequestIssue}}","name":"RequestIssue","call":{"operation":"{{IssueOp}}","inputs":[{"id":"{{InputA}}","member":"{{DueDays}}","value":{"value":30} },
                 {"id":"{{InputB}}","member":"{{IssuedOn}}","value":{"path":["01JATT00000000000000000182"]} }]} },
               {"id":"{{Nudge}}","name":"Nudge","raises":["{{Remind}}"]},
               {"id":"{{OnEnter}}","name":"OnEnter"}]}
            """);
        w.Operation("issue", Op(IssueOp, "issue", Invoice, $$"""{"id":"{{DueDays}}","name":"dueDays","type":"int32","required":true},{"id":"{{IssuedOn}}","name":"issuedOn","type":"date","required":true}""",
            $$""","fires":["{{Issue}}"]"""));
        w.Operation("notify", Op(Notify, "notify", Customer, $$"""{"id":"{{Message}}","name":"message","type":"string","length":80}"""));
        w.Operation("settle", Op(Settle, "settle", Invoice, ""));
        w.Operation("charge-card", Op(Charge, "chargeCard", Invoice, """{"id":"01JQPN00000000000000000122","name":"amount","type":"decimal","precision":12,"scale":2}"""));
        return w;
    }

    private static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(World w)
    {
        var harness = new LoaderHarness();
        harness.CopyFixture("models", "process-basics");
        foreach (var (path, json) in w.Files)
            harness.Write(path, json.ToJsonString());
        if (w.Rules is { } rules)
        {
            var settings = Obj(harness.Read("maquettiste.json"));
            settings["validation"] = new JsonObject { ["rules"] = rules.DeepClone() };
            harness.Write("maquettiste.json", settings.ToJsonString());
        }

        var store = new ModelStore(harness.Options, harness.Services() with { Validator = EngineServices.Create(harness.Options).Validator });
        await store.LoadAsync(Ct);
        var errors = store.Current!.LoadDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(d => d.Rule + " " + d.FilePath + " " + d.JsonPointer + " " + d.Message)));
        return (harness, store);
    }

    private static async Task<List<Diagnostic>> FindingsAsync(World w)
    {
        var (h, store) = await OpenAsync(w);
        using var _ = h;
        await using var __ = store;
        var report = await ValidationFixture.Validator().ValidateAsync(store.Current!, ValidationScope.All, null, Ct);
        // The fixture's own lifecycle uses events no operation fires: left out, so MQ9024 is judged on this model's files.
        return [.. report.Diagnostics.Where(d => LinkRules.Contains(d.Rule) && d.FilePath?.EndsWith("order-lifecycle.json", StringComparison.Ordinal) != true)];
    }

    private static string Show(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(d => d.Rule + " " + Path.GetFileName(d.FilePath) + " " + d.JsonPointer + " " + d.Message));

    private static void Single(List<Diagnostic> found, string rule, string file, string pointer, string? text = null)
    {
        var d = Assert.Single(found, f => f.Rule == rule && Path.GetFileName(f.FilePath) == file);
        Assert.True(Path.GetFileName(d.FilePath) == file && d.JsonPointer == pointer && (text is null || d.Message.Contains(text, StringComparison.Ordinal)), Show(found));
    }

    private static JsonObject Input(string id, string member, string value) => Obj($$"""{"id":"{{id}}","member":"{{member}}","value":{{value}}}""");

    // ---- the failing and passing cases ----

    private static readonly Dictionary<string, (Action<World> Change, string Rule, string File, string Pointer, string Text)> Failing = new(StringComparer.Ordinal)
    {
        ["MQ3032 fires an event of a process about another entity"] = (w => w.Files["model/operations/notify.json"]["fires"] = new JsonArray(Pay),
            "MQ3032", "notify.json", "/fires/0", "'Customer'"),
        ["MQ3032 a collection operation fires too"] = (w =>
        {
            w.Files["model/operations/notify.json"]["fires"] = new JsonArray(Pay);
            w.Files["model/operations/notify.json"]["level"] = "collection";
        }, "MQ3032", "notify.json", "/fires/0", "'InvoiceLifecycle'"),
        ["MQ3032 a call of a record operation on another entity"] = (w => w.Call["operation"] = Notify, "MQ3032", "invoice-lifecycle.json", "/actions/0/call/operation", "notify"),
        ["MQ3032 a service step on another entity"] = (w => w.Step["operation"] = Notify, "MQ3032", "invoice-lifecycle.json", "/states/1/invoke/0/operation", "ChargeCard"),
        ["MQ3032 a stereotype entry that covers no base of the subject"] = (w =>
        {
            w.Process["subjects"] = JsonNode.Parse("""[{"id":"01JSBJ00000000000000000101","stereotype":"billed"}]""");
            w.Files["model/operations/part-op.json"] = Obj(Op("01JQPN00000000000000000141", "touchPart", Part, "", $$""","fires":["{{Pay}}"]"""));
        }, "MQ3032", "part-op.json", "/fires/0", "'billed'"),
        ["MQ3044 a required payload attribute with no input of its name"] = (w => w.IssueOperation["input"]!.AsArray().RemoveAt(1),
            "MQ3044", "issue.json", "/fires/0", "'issuedOn' (date, required)"),
        ["MQ3044 an input of another type"] = (w => w.IssueOperation["input"]![1]!["type"] = "string", "MQ3044", "issue.json", "/fires/0", "is string"),
        ["MQ3044 a list for one value"] = (w => w.IssueOperation["input"]![1]!["collection"] = true, "MQ3044", "issue.json", "/fires/0", "a list of date"),
        ["MQ3044 a file input"] = (w =>
        {
            var member = w.IssueOperation["input"]![1]!.AsObject();
            member.Remove("type");
            member["file"] = true;
        }, "MQ3044", "issue.json", "/fires/0", "a file"),
        ["MQ3044 a record key of a composite key"] = (w =>
        {
            w.Payload.Add(Obj("""{"id":"01JATT00000000000000000183","name":"pair","type":"uuid"}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"01JQPN00000000000000000104","name":"pair","keyOf":"{{Pair}}"}"""));
        }, "MQ3044", "issue.json", "/fires/0", "the key of 'Pair'"),
        ["MQ3044 the implicit record key supplies no payload"] = (w => w.Payload.Add(Obj("""{"id":"01JATT00000000000000000183","name":"invoice","type":"uuid","required":true}""")),
            "MQ3044", "issue.json", "/fires/0", "'invoice' (uuid, required)"),
        ["MQ9024 an event no operation fires, when on"] = (w => w.Rules = Obj("""{"MQ9024":"warning"}"""), "MQ9024", "invoice-lifecycle.json", "/events/1", "'Pay'"),
        ["MQ9025 a member of another operation"] = (w => w.Inputs.Add(Input(InputC, Message, "{\"value\":\"x\"}")),
            "MQ9025", "invoice-lifecycle.json", "/actions/0/call/inputs/2/member", "not an input member"),
        ["MQ9025 a member given twice"] = (w => w.Inputs.Add(Input(InputC, DueDays, "{\"value\":3}")),
            "MQ9025", "invoice-lifecycle.json", "/actions/0/call/inputs/2/member", "twice"),
        ["MQ9025 a file member"] = (w =>
        {
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"scan","file":true}"""));
            w.Inputs.Add(Input(InputC, "01JQPN00000000000000000104", "{\"value\":\"x\"}"));
        }, "MQ9025", "invoice-lifecycle.json", "/actions/0/call/inputs/2/member", "a file"),
        ["MQ9025 a collection member"] = (w =>
        {
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"tags","type":"string","collection":true}"""));
            w.Inputs.Add(Input(InputC, "01JQPN00000000000000000104", "{\"value\":\"x\"}"));
        }, "MQ9025", "invoice-lifecycle.json", "/actions/0/call/inputs/2/member", "a list"),
        ["MQ9025 a record key of a composite key"] = (w =>
        {
            w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"01JQPN00000000000000000104","name":"pair","keyOf":"{{Pair}}"}"""));
            w.Inputs.Add(Input(InputC, "01JQPN00000000000000000104", "{\"null\":true}"));
        }, "MQ9025", "invoice-lifecycle.json", "/actions/0/call/inputs/2/member", "no one key attribute"),
        ["MQ9025 a member of a type the expression language lacks"] = (w =>
        {
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"scan","type":"binary"}"""));
            w.Inputs.Add(Input(InputC, "01JQPN00000000000000000104", "{\"value\":\"x\"}"));
        }, "MQ9025", "invoice-lifecycle.json", "/actions/0/call/inputs/2/member", "of type binary, which the expression language has no values of"),
        ["MQ9025 a record key of an entity with no key"] = (w =>
        {
            w.Files["model/entities/note.json"] = Obj(Entity("01JENT00000000000000000151", "Note", "", """{"id":"01JATT00000000000000000151","name":"text","type":"string","length":40}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"note","keyOf":"01JENT00000000000000000151"}"""));
            w.Inputs.Add(Input(InputC, "01JQPN00000000000000000104", "{\"null\":true}"));
        }, "MQ9025", "invoice-lifecycle.json", "/actions/0/call/inputs/2/member", "the key of 'Note', which has no one key attribute"),
        ["MQ9025 a record key whose key attribute has a type the expression language lacks"] = (w =>
        {
            w.Files["model/entities/blob.json"] = Obj(Entity("01JENT00000000000000000152", "Blob", ""","key":{"attributes":["01JATT00000000000000000152"]}""",
                """{"id":"01JATT00000000000000000152","name":"hash","type":"binary","required":true}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"blob","keyOf":"01JENT00000000000000000152"}"""));
            w.Inputs.Add(Input(InputC, "01JQPN00000000000000000104", "{\"null\":true}"));
        }, "MQ9025", "invoice-lifecycle.json", "/actions/0/call/inputs/2/member", "whose key attribute is binary"),
        ["MQ9025 an operation on a process invoke"] = (w =>
        {
            w.Step["type"] = "process";
            w.Step["process"] = PartLifecycle;
            w.Files["model/processes/part-lifecycle.json"] = PartLifecycleFile();
        }, "MQ9025", "invoice-lifecycle.json", "/states/1/invoke/0/operation", "only a service step"),
        ["MQ9025 an operation on a human task"] = (w =>
        {
            w.Step["type"] = "human-task";
            w.Step["actors"] = new JsonArray("01JACT00000000000000000002");
        }, "MQ9025", "invoice-lifecycle.json", "/states/1/invoke/0/operation", "only a service step"),
        ["MQ9026 a required input left out"] = (w => w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"reason","type":"string","required":true}""")),
            "MQ9026", "invoice-lifecycle.json", "/actions/0/call", "'reason'"),
        ["MQ9020 a library action with a call"] = (w => w.Files["model/processes/library.json"] = Obj($$"""
            {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{Library}}","name":"Shared","package":"{{Package}}","use":"library",
             "actions":[{"id":"01JPRX00000000000000000191","name":"Ask","call":{"operation":"{{IssueOp}}"} }]}
            """), "MQ9020", "library.json", "/actions/0/call", "calls an operation"),
    };

    private static readonly Dictionary<string, Action<World>> Passing = new(StringComparer.Ordinal)
    {
        ["MQ3032 the operation on the governed entity"] = _ => { },
        ["MQ3032 an operation with no subject"] = w =>
        {
            w.Files["model/operations/notify.json"].Remove("subject");
            w.Files["model/operations/notify.json"]["fires"] = new JsonArray(Pay);
            w.Call["operation"] = Notify;
            w.Call["inputs"] = new JsonArray();
        },
        ["MQ3032 a process with no subjects"] = w =>
        {
            w.Process["use"] = "orchestration";
            w.Process.Remove("subjects");
            w.Files["model/operations/notify.json"]["fires"] = new JsonArray(Pay);
            w.Step["operation"] = Notify;
        },
        ["MQ3032 a collection operation called on another entity"] = w =>
        {
            w.Files["model/operations/notify.json"]["level"] = "collection";
            w.Call["operation"] = Notify;
            w.Call["inputs"] = new JsonArray();
            w.Step["operation"] = Notify;
        },
        ["MQ3032 an operation on a base of the governed entity"] = w =>
        {
            w.Files["model/processes/part-lifecycle.json"] = PartLifecycleFile();
            w.Files["model/operations/part-op.json"] = Obj(Op("01JQPN00000000000000000141", "touchPart", Part, "", ""","fires":["01JPRX00000000000000000151"]"""));
        },
        ["MQ3032 an operation on an entity derived from the governed one"] = w =>
        {
            w.Process["subjects"] = JsonNode.Parse($$"""[{"id":"01JSBJ00000000000000000101","entity":"{{Part}}"}]""");
            w.Files["model/operations/part-op.json"] = Obj(Op("01JQPN00000000000000000141", "touchPart", PurchasedPart, "", $$""","fires":["{{Pay}}"]"""));
            w.IssueOperation.Remove("fires");
            w.Call["operation"] = "01JQPN00000000000000000141";
            w.Call["inputs"] = new JsonArray();
            w.Step.Remove("operation");
        },
        ["MQ3032 a stereotype entry that covers the subject"] = w =>
        {
            w.Process["subjects"] = JsonNode.Parse("""[{"id":"01JSBJ00000000000000000101","stereotype":"billed"}]""");
            w.Files["model/operations/notify.json"]["fires"] = new JsonArray(Pay);
            w.IssueOperation.Remove("fires");
            w.Call["operation"] = Notify;
            w.Call["inputs"] = new JsonArray();
            w.Step.Remove("operation");
        },
        ["MQ3044 every attribute supplied"] = _ => { },
        ["MQ3044 a missing attribute with a default"] = w =>
        {
            w.IssueOperation["input"]!.AsArray().RemoveAt(1);
            w.Payload[1]!["default"] = "2026-01-01";
        },
        ["MQ3044 an optional attribute with no input"] = w => w.Payload.Add(Obj("""{"id":"01JATT00000000000000000183","name":"note","type":"string","length":40}""")),
        ["MQ3044 a record key whose key attribute has the type"] = w =>
        {
            w.Payload.Add(Obj("""{"id":"01JATT00000000000000000183","name":"invoice","type":"uuid","required":true}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"01JQPN00000000000000000104","name":"invoice","keyOf":"{{Invoice}}"}"""));
        },
        ["MQ3044 a list for a list"] = w =>
        {
            w.Payload[1]!["collection"] = true;
            w.IssueOperation["input"]![1]!["collection"] = true;
        },
        ["MQ9024 off by default"] = _ => { },
        ["MQ9024 every used event fired or raised, when on"] = w =>
        {
            w.Rules = Obj("""{"MQ9024":"warning"}""");
            w.Files["model/operations/charge-card.json"]["fires"] = new JsonArray(Pay);
        },
        ["MQ9025 inputs of the operation, once each"] = _ => { },
        ["MQ9025 an operation on a service step"] = _ => { },
        ["MQ9026 a required input given"] = _ => { },
        ["MQ9026 a required input with a default left out"] = w => w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"reason","type":"string","required":true,"default":"none"}""")),
        ["MQ9026 required file and collection inputs left out"] = w =>
        {
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"scan","file":true,"required":true}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000105","name":"tags","type":"string","collection":true,"required":true}"""));
        },
        ["MQ9026 required inputs a call can't give left out"] = w =>
        {
            w.Files["model/entities/note.json"] = Obj(Entity("01JENT00000000000000000151", "Note", "", """{"id":"01JATT00000000000000000151","name":"text","type":"string","length":40}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"scan","type":"binary","required":true}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000105","name":"ratio","type":"float","required":true}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"01JQPN00000000000000000106","name":"pair","keyOf":"{{Pair}}","required":true}"""));
            w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000107","name":"note","keyOf":"01JENT00000000000000000151","required":true}"""));
        },
        ["MQ9020 a running process's action with a call"] = _ => { },
    };

    private static JsonObject PartLifecycleFile() => Obj($$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{PartLifecycle}}","name":"PartLifecycle","package":"{{Package}}","use":"lifecycle",
         "subjects":[{"id":"01JSBJ00000000000000000151","entity":"{{PurchasedPart}}"}],
         "states":[{"id":"01JSTA00000000000000000151","name":"Open"},{"id":"01JSTA00000000000000000152","name":"Closed","type":"final"}],
         "initial":"01JSTA00000000000000000151",
         "transitions":[{"id":"01JTRN00000000000000000151","source":"01JSTA00000000000000000151","event":"01JPRX00000000000000000151","targets":["01JSTA00000000000000000152"]}],
         "events":[{"id":"01JPRX00000000000000000151","name":"Close"}]}
        """);

    public static TheoryData<string> FailingCases() => [.. Failing.Keys.Order(StringComparer.Ordinal)];

    public static TheoryData<string> PassingCases() => [.. Passing.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(FailingCases))]
    public async Task A_failing_case_gives_its_finding_where_the_fix_belongs(string name)
    {
        var (change, rule, file, pointer, text) = Failing[name];
        var w = Model();
        change(w);
        var found = await FindingsAsync(w);
        Single(found, rule, file, pointer, text);
    }

    [Theory]
    [MemberData(nameof(PassingCases))]
    public async Task A_passing_case_gives_no_finding_of_its_rule(string name)
    {
        var w = Model();
        Passing[name](w);
        var found = await FindingsAsync(w);
        Assert.True(!found.Any(d => d.Rule == name[..6]), Show(found));
    }

    [Fact]
    public async Task The_base_model_gives_no_link_finding()
    {
        Assert.Empty(await FindingsAsync(Model()));
    }

    [Fact]
    public void Every_link_rule_has_a_catalog_entry_a_failing_case_and_a_passing_case()
    {
        string[] rules = ["MQ3032", "MQ3044", "MQ9020", "MQ9024", "MQ9025", "MQ9026"];
        foreach (var rule in rules)
        {
            Assert.True(RuleCatalog.TryGet(rule, out _), rule);
            Assert.Contains(Failing.Keys, k => k.StartsWith(rule, StringComparison.Ordinal));
            Assert.Contains(Passing.Keys, k => k.StartsWith(rule, StringComparison.Ordinal));
        }

        Assert.Equal(DiagnosticSeverity.Info, RuleCatalog.Get("MQ9024").DefaultSeverity);
    }

    [Fact]
    public async Task MQ9024_reports_only_used_events_that_nothing_fires_or_raises_at_the_severity_the_project_gives()
    {
        var w = Model();
        w.Rules = Obj("""{"MQ9024":"error"}""");
        var found = await FindingsAsync(w);
        // Issue is fired by issue, Remind raised by Nudge, Idle used by no transition (MQ9013's case): only Pay.
        var d = Assert.Single(found, f => f.Rule == "MQ9024" && Path.GetFileName(f.FilePath) == "invoice-lifecycle.json");
        Assert.Equal(("/events/1", DiagnosticSeverity.Error), (d.JsonPointer, d.Severity));
    }

    [Fact]
    public async Task A_repeated_fired_event_is_judged_once()
    {
        var w = Model();
        w.IssueOperation["fires"] = new JsonArray(Issue, Issue);
        w.IssueOperation["input"]!.AsArray().RemoveAt(1);
        var found = await FindingsAsync(w);
        Single(found, "MQ3044", "issue.json", "/fires/0");
    }

    // ---- input values typed like a set's value ----

    [Fact]
    public async Task An_input_value_is_typed_against_its_member()
    {
        var w = Model();
        w.Inputs[0]!["value"] = Obj("""{"value":"thirty"}""");
        Single(await FindingsAsync(w), "MQ3103", "invoice-lifecycle.json", "/actions/0/call/inputs/0/value", "the input's value must be int32");

        // The object's attribute fits; a value with no member to type against types freely.
        w = Model();
        w.Inputs[0]!["value"] = Obj($$"""{"path":["{{TermsDays}}"]}""");
        Assert.Empty(await FindingsAsync(w));
    }

    [Fact]
    public async Task A_record_key_member_types_as_its_entitys_key_attribute_and_null_fits_an_optional_member()
    {
        var w = Model();
        w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"01JQPN00000000000000000104","name":"invoice","keyOf":"{{Invoice}}"}"""));
        w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000105","name":"note","type":"string","length":40}"""));
        w.Inputs.Add(Input(InputC, "01JQPN00000000000000000104", $$"""{"path":["{{InvoiceId}}"]}"""));
        w.Inputs.Add(Input("01JCXN00000000000000000104", "01JQPN00000000000000000105", """{"null":true}"""));
        Assert.Empty(await FindingsAsync(w));

        w.Inputs[2]!["value"] = Obj("""{"value":3}""");
        Single(await FindingsAsync(w), "MQ3103", "invoice-lifecycle.json", "/actions/0/call/inputs/2/value", "must be uuid");
    }

    [Fact]
    public async Task An_entry_actions_input_reads_no_payload()
    {
        var w = Model();
        w.Action(OnEnter)["call"] = Obj($$"""{"operation":"{{IssueOp}}","inputs":[{"id":"{{InputC}}","member":"{{DueDays}}","value":{"path":["01JATT00000000000000000181"]} }]}""");
        var found = await FindingsAsync(w);
        Single(found, "MQ3102", "invoice-lifecycle.json", "/actions/2/call/inputs/0/value", "01JATT00000000000000000181");

        // On the transition of an event that carries it, the same read is fine.
        w = Model();
        w.Inputs[0]!["value"] = Obj("""{"path":["01JATT00000000000000000181"]}""");
        Assert.Empty(await FindingsAsync(w));

        // The action also runs on Pay, which carries neither attribute: each read is MQ3102, as for a set.
        w.Process["transitions"]![1]!["actions"] = new JsonArray(RequestIssue);
        var both = await FindingsAsync(w);
        Assert.Equal(["/actions/0/call/inputs/0/value", "/actions/0/call/inputs/1/value"],
            both.Where(d => d.Rule == "MQ3102" && d.Message.StartsWith("The input's value reads the payload attribute", StringComparison.Ordinal)).Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Input_text_is_stored_as_a_tree_and_prints_from_current_names()
    {
        var (h, store) = await OpenAsync(Model());
        using var _ = h;
        await using var __ = store;

        var document = Obj(h.Read(LifecycleFile));
        document["actions"]![0]!["call"]!["inputs"]![0]!["value"] = Obj("""{"text":"subject.termsDays + 1"}""");
        var saved = await SaveAsync(store, Lifecycle, document);
        Assert.True(saved.Outcome == SaveOutcome.Saved, Show(saved.Items.SelectMany(i => i.Diagnostics)));
        var stored = Obj(h.Read(LifecycleFile))["actions"]![0]!["call"]!["inputs"]![0]!["value"]!;
        Assert.Contains(TermsDays, stored.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("text", stored.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("subject.termsDays + 1", PrintInput(store.Current!));

        // Renaming the attribute reprints the text; the file keeps its ids.
        var entity = Obj(h.Read("model/entities/invoice.json"));
        entity["attributes"]![1]!["name"] = "days";
        Assert.Equal(SaveOutcome.Saved, (await SaveAsync(store, Invoice, entity)).Outcome);
        Assert.Equal("subject.days + 1", PrintInput(store.Current!));

        static string PrintInput(ModelSnapshot model)
        {
            var process = model.Get<Process>(Lifecycle)!;
            var site = ExprSites.Of(model, process).Single(s => s.Key == InputA);
            Assert.Equal((ExprSiteKind.Set, DueDays, "/actions/0/call/inputs/0/value"), (site.Kind, site.Member, site.Pointer));
            return ExprText.Print(site.Tree!, ExprSites.Scope(model, process, model.GetDocument(Lifecycle), site, text: false));
        }
    }

    // ---- the preview's input site ----

    [Fact]
    public async Task The_preview_types_input_text_against_the_member_and_a_saved_input_by_its_id()
    {
        var (h, store) = await OpenAsync(Model());
        using var _ = h;
        await using var __ = store;

        var typed = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = RequestIssue, Site = "input", Member = DueDays, Text = "subject.termsDays" }, Ct);
        Assert.Equal(("subject.termsDays", "int32"), (typed.Preview!.Text, typed.Preview.Type));

        var misfit = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = RequestIssue, Site = "input", Member = DueDays, Text = "'x'" }, Ct);
        Assert.Null(misfit.Preview);
        Assert.Contains(misfit.Diagnostics, d => d.Rule == "MQ3103" && d.Message.Contains("input's value", StringComparison.Ordinal));

        // The payload of the events whose moves run the action reads under event.
        var payload = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = RequestIssue, Site = "input", Member = DueDays, Text = "event.dueDays" }, Ct);
        Assert.Equal("int32", payload.Preview!.Type);

        var saved = await store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = InputB }, Ct);
        Assert.Equal(("event.issuedOn", "date"), (saved.Preview!.Text, saved.Preview.Type));

        await Assert.ThrowsAsync<ArgumentException>(() => store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = RequestIssue, Site = "input", Text = "1" }, Ct));
        // A member the called operation doesn't have is refused, not typed freely.
        var other = await Assert.ThrowsAsync<ArgumentException>(() => store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = RequestIssue, Site = "input", Member = Message, Text = "'x'" }, Ct));
        Assert.Contains("not an input member of operation 'issue'", other.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = Nudge, Site = "input", Member = DueDays, Text = "1" }, Ct));
    }

    // ---- incremental validation ----

    private static async Task<BatchResult> SaveAsync(ModelStore store, string id, JsonObject document)
    {
        var hash = (await store.GetElementAsync(id, Ct))!.Hash;
        return await store.ApplyBatchAsync(new ModelBatch([new BatchOperation(BatchOp.Update, id, hash, JsonDocument.Parse(document.ToJsonString()).RootElement.Clone())]),
            ChangeSource.Editor, Ct);
    }

    private static List<Diagnostic> All(BatchResult result) => [.. result.Items.SelectMany(i => i.Diagnostics)];

    [Fact]
    public async Task A_process_save_checks_the_operations_that_fire_its_events()
    {
        var (h, store) = await OpenAsync(Model());
        using var _ = h;
        await using var __ = store;

        // Issue gains a required attribute issue has no input for: MQ3044 shows on issue.json.
        var document = Obj(h.Read(LifecycleFile));
        document["events"]![0]!["payload"]!.AsArray().Add(Obj("""{"id":"01JATT00000000000000000183","name":"reason","type":"string","required":true}"""));
        var saved = All(await SaveAsync(store, Lifecycle, document));
        Assert.Contains(saved, d => d.Rule == "MQ3044" && Path.GetFileName(d.FilePath) == "issue.json" && d.Message.Contains("'reason'", StringComparison.Ordinal));

        // The process changes its subject to Customer: MQ3032 shows on issue.json too.
        document["subjects"] = JsonNode.Parse($$"""[{"id":"01JSBJ00000000000000000101","entity":"{{Customer}}"}]""");
        saved = All(await SaveAsync(store, Lifecycle, document));
        Assert.Contains(saved, d => d.Rule == "MQ3032" && Path.GetFileName(d.FilePath) == "issue.json");
    }

    [Fact]
    public async Task An_operation_save_checks_the_processes_that_call_it_or_name_it_on_a_step()
    {
        var (h, store) = await OpenAsync(Model());
        using var _ = h;
        await using var __ = store;

        // issue gains a required input: MQ9026 on the call. chargeCard moves to Customer: MQ3032 on the step.
        var issue = Obj(h.Read(IssueFile));
        issue["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"reason","type":"string","required":true}"""));
        var saved = All(await SaveAsync(store, IssueOp, issue));
        Assert.Contains(saved, d => d.Rule == "MQ9026" && Path.GetFileName(d.FilePath) == "invoice-lifecycle.json");

        var charge = Obj(h.Read("model/operations/charge-card.json"));
        charge["subject"] = Customer;
        saved = All(await SaveAsync(store, Charge, charge));
        Assert.Contains(saved, d => d.Rule == "MQ3032" && d.JsonPointer == "/states/1/invoke/0/operation");
    }

    [Fact]
    public async Task An_entity_save_checks_the_links_its_base_changes()
    {
        var w = Model();
        w.Files["model/processes/part-lifecycle.json"] = PartLifecycleFile();
        w.Files["model/operations/part-op.json"] = Obj(Op("01JQPN00000000000000000141", "touchPart", Part, "", ""","fires":["01JPRX00000000000000000151"]"""));
        var (h, store) = await OpenAsync(w);
        using var _ = h;
        await using var __ = store;

        // PurchasedPart stops deriving from Part: touchPart, on Part, fires an event of a process about PurchasedPart alone. Neither the
        // operation nor the process references PurchasedPart's own file as a subject of the operation, so only the entity's link peers reach it.
        var entity = Obj(h.Read("model/entities/purchased-part.json"));
        entity.Remove("base");
        entity["key"] = JsonNode.Parse("""{"attributes":["01JATT00000000000000000112"]}""");
        var saved = All(await SaveAsync(store, PurchasedPart, entity));
        Assert.Contains(saved, d => d.Rule == "MQ3032" && Path.GetFileName(d.FilePath) == "part-op.json");
    }

    [Fact]
    public async Task MQ9024_follows_an_operation_that_starts_or_stops_firing_only_while_it_is_on()
    {
        var w = Model();
        w.Rules = Obj("""{"MQ9024":"warning"}""");
        var (h, store) = await OpenAsync(w);
        using (h)
        await using (store)
        {
            // settle, which no process names, starts firing Pay: the lifecycle is one of its peers and loses its MQ9024.
            Assert.DoesNotContain(Lifecycle, ModelValidator.TargetIds(store.Current!, new ValidationScope([Settle], IncludeReferrers: true)));
            var settle = Obj(h.Read("model/operations/settle.json"));
            settle["fires"] = new JsonArray(Pay);
            var started = All(await SaveAsync(store, Settle, settle));
            Assert.DoesNotContain(started, d => d.Rule == "MQ9024" && Path.GetFileName(d.FilePath) == "invoice-lifecycle.json");
            Assert.Contains(Lifecycle, ModelValidator.TargetIds(store.Current!, new ValidationScope([Settle], IncludeReferrers: false)));

            // It stops: the store brings the process it fired before, and the finding is back.
            settle.Remove("fires");
            var stopped = All(await SaveAsync(store, Settle, settle));
            Assert.Contains(stopped, d => d.Rule == "MQ9024" && d.JsonPointer == "/events/1" && Path.GetFileName(d.FilePath) == "invoice-lifecycle.json");
        }

        // Off: an operation that fires brings no process beyond its referrers.
        var off = Model();
        off.Files["model/operations/settle.json"]["fires"] = new JsonArray(Pay);
        var (h2, store2) = await OpenAsync(off);
        using var _ = h2;
        await using var __ = store2;
        Assert.DoesNotContain(Lifecycle, ModelValidator.TargetIds(store2.Current!, new ValidationScope([Settle], IncludeReferrers: false)));
    }

    // ---- incremental validation equals full validation ----

    private const string Mid = "01JENT00000000000000000141";
    private const string MidId = "01JATT00000000000000000141";
    private const string Leaf = "01JENT00000000000000000142";
    private const string LeafOp = "01JQPN00000000000000000161";

    // A file's form and place (MQ1003, MQ1005) are judged on load, not in the validator's scope: left out, since the test writes compact JSON
    // and keeps operations in one folder.
    private static List<string> Keys(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.Where(d => d.Rule is not ("MQ1003" or "MQ1005")).Select(d => $"{d.Rule} {d.ElementId} {d.JsonPointer}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Saves a change to one file and checks that what the save adds and removes over its scope is what a full validation gains and loses. A
    /// save that adds an error is refused: then the errors it reports are the errors a full validation of the changed model adds.
    /// </summary>
    /// <returns>Whether it saved, and the diagnostics the save added (or the errors it was refused with) and removed.</returns>
    private static async Task<(SaveOutcome Outcome, List<Diagnostic> Added, List<Diagnostic> Removed)> IncrementalEqualsFullAsync(World w, string id, string path,
        Action<JsonObject> change)
    {
        var validator = ValidationFixture.Validator();
        var (h, store) = await OpenAsync(w);
        using var _ = h;
        await using var __ = store;
        var before = await validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct);
        var document = Obj(h.Read(path));
        change(document);
        var hash = (await store.GetElementAsync(id, Ct))!.Hash;
        var batch = new ModelBatch([new BatchOperation(BatchOp.Update, id, hash, JsonDocument.Parse(document.ToJsonString()).RootElement.Clone())]);
        var preview = await store.PreviewBatchAsync(batch, Ct);
        var (beforeKeys, afterKeys) = (Keys(before.Diagnostics), new List<string>());
        if (preview.Outcome == SaveOutcome.Invalid)
        {
            var refused = preview.Items.SelectMany(i => i.Diagnostics).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            w.Files[path] = document;
            var (h2, changed) = await OpenAsync(w);
            using var ___ = h2;
            await using var ____ = changed;
            var full = await validator.ValidateAsync(changed.Current!, ValidationScope.All, null, Ct);
            afterKeys = Keys(full.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Equal(afterKeys.Except(Keys(before.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)), StringComparer.Ordinal), Keys(refused));
            return (preview.Outcome, refused, []);
        }

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Equal(SaveOutcome.Saved, (await store.ApplyBatchAsync(batch, ChangeSource.Editor, Ct)).Outcome);
        var after = await validator.ValidateAsync(store.Current!, ValidationScope.All, null, Ct);
        afterKeys = Keys(after.Diagnostics);
        Assert.Equal(afterKeys.Except(beforeKeys, StringComparer.Ordinal), Keys(preview.DiagnosticsAdded));
        Assert.Equal(beforeKeys.Except(afterKeys, StringComparer.Ordinal), Keys(preview.DiagnosticsRemoved));
        return (preview.Outcome, [.. preview.DiagnosticsAdded], [.. preview.DiagnosticsRemoved]);
    }

    /// <summary>Leaf derives from Mid; leafOp, on Leaf, fires Issue of the Invoice lifecycle. Mid derives from Invoice, or has its own key.</summary>
    private static World Chain(bool midOnInvoice)
    {
        var w = Model();
        w.Files["model/entities/mid.json"] = Obj(Entity(Mid, "Mid", midOnInvoice ? $$""","base":"{{Invoice}}" """ : $$""","key":{"attributes":["{{MidId}}"]}""",
            $$"""{"id":"{{MidId}}","name":"midId","type":"uuid","required":true}"""));
        w.Files["model/entities/leaf.json"] = Obj(Entity(Leaf, "Leaf", $$""","base":"{{Mid}}" """, """{"id":"01JATT00000000000000000142","name":"note","type":"string","length":40}"""));
        w.Operation("leaf-op", Op(LeafOp, "touchLeaf", Leaf,
            """{"id":"01JQPN00000000000000000162","name":"dueDays","type":"int32","required":true},{"id":"01JQPN00000000000000000163","name":"issuedOn","type":"date","required":true}""",
            $$""","fires":["{{Issue}}"]"""));
        return w;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_base_that_changes_above_an_operations_subject_refreshes_MQ3032_on_its_fires(bool reverse)
    {
        if (!reverse)
        {
            // Mid comes to derive from Invoice: leafOp, two steps below, now fires an event of a process that governs its subject.
            var (_, added, removed) = await IncrementalEqualsFullAsync(Chain(midOnInvoice: false), Mid, "model/entities/mid.json", mid =>
            {
                mid.Remove("key");
                mid["base"] = Invoice;
            });
            Assert.Contains(removed, d => d.Rule == "MQ3032" && d.ElementId == LeafOp && d.JsonPointer == "/fires/0");
            Assert.DoesNotContain(added, d => d.Rule == "MQ3032");
        }
        else
        {
            // Mid stops deriving from Invoice, and the warning comes back.
            var (_, added, _) = await IncrementalEqualsFullAsync(Chain(midOnInvoice: true), Mid, "model/entities/mid.json", mid =>
            {
                mid.Remove("base");
                mid["key"] = JsonNode.Parse($$"""{"attributes":["{{MidId}}"]}""");
            });
            Assert.Contains(added, d => d.Rule == "MQ3032" && d.ElementId == LeafOp && d.JsonPointer == "/fires/0");
        }
    }

    /// <summary>issue gains a record key member of Customer (customer) and of PurchasedPart (part), which a call input and the payload of Issue match.</summary>
    private static World RecordKeys()
    {
        var w = Model();
        w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"01JQPN00000000000000000107","name":"customer","keyOf":"{{Customer}}"}"""));
        w.IssueOperation["input"]!.AsArray().Add(Obj($$"""{"id":"01JQPN00000000000000000108","name":"part","keyOf":"{{PurchasedPart}}"}"""));
        w.Payload.Add(Obj("""{"id":"01JATT00000000000000000184","name":"customer","type":"uuid"}"""));
        w.Payload.Add(Obj("""{"id":"01JATT00000000000000000185","name":"part","type":"uuid","required":true}"""));
        w.Inputs.Add(Input(InputC, "01JQPN00000000000000000107", """{"path":["01JATT00000000000000000184"]}"""));
        return w;
    }

    [Fact]
    public async Task The_record_key_model_gives_no_link_finding()
    {
        Assert.Empty(await FindingsAsync(RecordKeys()));
    }

    [Theory]
    [InlineData("key type")]
    [InlineData("composite key")]
    [InlineData("inherited key")]
    public async Task A_record_key_entity_that_changes_refreshes_the_calls_and_fires_of_its_operations(string change)
    {
        switch (change)
        {
            case "key type":
            {
                // Customer's key attribute becomes an int64: the call's value, a uuid, no longer fits (MQ3103 on the process, two steps away),
                // and the save is refused with it.
                var (outcome, added, _) = await IncrementalEqualsFullAsync(RecordKeys(), Customer, "model/entities/customer.json",
                    customer => customer["attributes"]![0]!["type"] = "int64");
                Assert.Equal(SaveOutcome.Invalid, outcome);
                Assert.Contains(added, d => d.Rule == "MQ3103" && d.ElementId == InputC);
                break;
            }

            case "composite key":
            {
                // Customer's key becomes composite: the input can't be given (MQ9025).
                var (outcome, added, _) = await IncrementalEqualsFullAsync(RecordKeys(), Customer, "model/entities/customer.json", customer =>
                {
                    customer["attributes"]!.AsArray().Add(Obj("""{"id":"01JATT00000000000000000122","name":"region","type":"string","length":10,"required":true}"""));
                    customer["key"] = JsonNode.Parse("""{"attributes":["01JATT00000000000000000121","01JATT00000000000000000122"]}""");
                });
                Assert.Equal(SaveOutcome.Invalid, outcome);
                Assert.Contains(added, d => d.Rule == "MQ9025" && d.ElementId == InputC && d.JsonPointer == "/actions/0/call/inputs/2/member");
                break;
            }

            default:
            {
                // Part, whose key PurchasedPart inherits, changes its key attribute's type: part no longer supplies Issue's part (MQ3044 on
                // issue's fires, which references PurchasedPart, not Part).
                var (_, added, _) = await IncrementalEqualsFullAsync(RecordKeys(), Part, "model/entities/part.json", part => part["attributes"]![0]!["type"] = "int64");
                Assert.Contains(added, d => d.Rule == "MQ3044" && d.ElementId == IssueOp && d.Message.Contains("'part'", StringComparison.Ordinal));
                break;
            }
        }
    }

    // ---- quick fixes ----

    [Fact]
    public async Task The_pointers_the_editors_quick_fixes_read_stay_as_the_engine_reports_them()
    {
        // MQ9025 on an input points into /actions/<i>/call/inputs/<j>; on a step, at the invoke's operation, nested states included; MQ9026 at
        // the call. The editor removes the input, clears the step's operation, or opens the action's Calls dialog from these.
        var w = Model();
        w.Inputs.Add(Input(InputC, Message, "{\"value\":\"x\"}"));
        w.IssueOperation["input"]!.AsArray().Add(Obj("""{"id":"01JQPN00000000000000000104","name":"reason","type":"string","required":true}"""));
        w.Process["states"]!.AsArray().Add(Obj($$"""
            {"id":"01JSTA00000000000000000104","name":"Hold","initial":"01JSTA00000000000000000105","states":[{"id":"01JSTA00000000000000000105","name":"Waiting",
             "invoke":[{"id":"01JPRX00000000000000000132","name":"Review","type":"human-task","actors":["01JACT00000000000000000002"],"operation":"{{Charge}}"}]}]}
            """));
        var found = await FindingsAsync(w);
        Assert.Equal(["/actions/0/call/inputs/2/member", "/states/3/states/0/invoke/0/operation"],
            found.Where(d => d.Rule == "MQ9025").Select(d => d.JsonPointer).Order(StringComparer.Ordinal));
        Assert.Equal(["/actions/0/call"], found.Where(d => d.Rule == "MQ9026").Select(d => d.JsonPointer));
        Assert.All(found.Where(d => d.Rule == "MQ9025"), d => Assert.Matches(@"^/actions/\d+/call/inputs/\d+(/|$)|/invoke/\d+/operation$", d.JsonPointer));
    }
}
