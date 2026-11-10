using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Tests.Loading;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// A family of types with revisions over the <c>process-basics</c> fixture, invented for the tests of the block's references, deletes and
/// merging (revisions-design.md decision 9, section 10). <c>Record</c> holds <c>revision</c> and <c>approvedBy</c>; <c>Part</c> derives from it and
/// writes the block; <c>PurchasedPart</c> restates only what differs; <c>MadePart</c> has its own levels and separator, carries the stereotype
/// <c>tracked</c> and names its <c>trackingCode</c>; <c>Memo</c> and <c>Sheet</c> hold blocks of one key each, which a delete empties.
/// Lifecycles: <c>Delivery</c> and <c>Approval</c> govern <c>Part</c> (so its derived types); <c>Machining</c> governs only <c>MadePart</c>;
/// <c>Tracking</c> governs the carriers of <c>tracked</c>; <c>Shipping</c> is an orchestration of <c>Part</c>, which governs nothing. The
/// lifecycles' ids don't follow their names, so name order shows.
/// </summary>
internal static class RevisionFamily
{
    public const string Package = "01JPKG00000000000000000001"; // Sales, in process-basics
    public const string Order = "01JENT00000000000000000001"; // Order, in process-basics: no block anywhere

    public const string RecordType = "01JENT00000000000000000101";
    public const string Part = "01JENT00000000000000000102";
    public const string PurchasedPart = "01JENT00000000000000000103";
    public const string MadePart = "01JENT00000000000000000104";
    public const string Document = "01JENT00000000000000000105";
    public const string Tool = "01JENT00000000000000000106";
    public const string Memo = "01JENT00000000000000000107";
    public const string Sheet = "01JENT00000000000000000108";

    public const string Revision = "01JATT00000000000000000101"; // Record.revision
    public const string ApprovedBy = "01JATT00000000000000000102"; // Record.approvedBy
    public const string Note = "01JATT00000000000000000103"; // Part.note
    public const string SupplierQuote = "01JATT00000000000000000104"; // PurchasedPart.supplierQuote
    public const string TrackingCode = "01JATT00000000000000000105"; // tracked.trackingCode

    public const string Tracked = "01JSTR00000000000000000101";

    public const string Approval = "01JPRC00000000000000000103";
    public const string Delivery = "01JPRC00000000000000000101";
    public const string Machining = "01JPRC00000000000000000102";
    public const string Tracking = "01JPRC00000000000000000104";
    public const string Shipping = "01JPRC00000000000000000105";

    public const string Waiting = "01JSTA00000000000000000101";
    public const string Shipped = "01JSTA00000000000000000102";
    public const string Queued = "01JSTA00000000000000000103";
    public const string Open = "01JSTA00000000000000000104";
    public const string Approved = "01JSTA00000000000000000105";
    public const string Watched = "01JSTA00000000000000000106";
    public const string Running = "01JSTA00000000000000000107";

    public const string PartDocuments = "01JRXN00000000000000000101";
    public const string PartsEnd = "01JEND00000000000000000101";
    public const string DocumentsEnd = "01JEND00000000000000000102";
    public const string MadePartTools = "01JRXN00000000000000000102";
    public const string MadePartsEnd = "01JEND00000000000000000103";
    public const string ToolsEnd = "01JEND00000000000000000104";

    public const string PartFile = "model/entities/part.json";
    public const string PurchasedPartFile = "model/entities/purchased-part.json";
    public const string MadePartFile = "model/entities/made-part.json";
    public const string MemoFile = "model/entities/memo.json";
    public const string SheetFile = "model/entities/sheet.json";

    public static readonly string PartBlock = $$"""
        {"levels":[{"name":"major","format":"letters"},{"name":"minor","format":"numbers","startEmpty":true}],
         "boundAttribute":"{{Revision}}",
         "lifecycles":[{"lifecycle":"{{Delivery}}","onRevise":"keep"},{"lifecycle":"{{Machining}}","onRevise":"keep"}],
         "attributes":[{"attribute":"{{ApprovedBy}}","onRevise":"reset"},{"attribute":"{{Note}}","onRevise":"clear"}],
         "links":[{"end":"{{DocumentsEnd}}","onRevise":"duplicate"},{"end":"{{ToolsEnd}}","onRevise":"move"}]}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Opens the family in a store with the real validator; <paramref name="prepare"/> may change files first.</summary>
    /// <param name="prepare">Edits to the files before the load.</param>
    /// <returns>The harness and the loaded store.</returns>
    public static async Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null)
    {
        var h = new LoaderHarness();
        h.CopyFixture("models", "process-basics");
        Write(h);
        prepare?.Invoke(h);
        var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        Assert.DoesNotContain(store.Current!.LoadDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        return (h, store);
    }

    /// <summary>The extra keys of an abstract entity: Part and Memo derive from Record, and stay valid without a key when Record goes.</summary>
    public const string Abstract = ",\"abstract\":true";

    /// <summary>An entity file.</summary>
    public static string Entity(string id, string name, string? baseId, string attributes, string? block, string extra = "") => $$"""
        {"$schema":"../../.schema/v1/entity.json","kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Package}}"{{(baseId is null ? "" : $",\"base\":\"{baseId}\"")}}{{extra}},
         "attributes":[{{attributes}}]{{(block is null ? "" : ",\"revisions\":" + block)}}}
        """;

    /// <summary>A string attribute.</summary>
    public static string Attr(string id, string name) => $$"""{"id":"{{id}}","name":"{{name}}","type":"string","length":40}""";

    private static string Lifecycle(string id, string name, string subjects, string states, string use = "lifecycle") => $$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{id}}","name":"{{name}}","package":"{{Package}}","use":"{{use}}",
         "subjects":[{{subjects}}],"states":[{{states}}]}
        """;

    private static string State(string id, string name) => $$"""{"id":"{{id}}","name":"{{name}}"}""";

    private static void Write(LoaderHarness h)
    {
        h.Write("model/vocabularies/stereotypes/tracked.json", $$"""
            {"$schema":"../../../.schema/v1/stereotype.json","kind":"stereotype","id":"{{Tracked}}","key":"tracked","name":"Tracked","appliesTo":["entity"],
             "attributes":[{{Attr(TrackingCode, "trackingCode")}}]}
            """);
        h.Write("model/entities/record.json", Entity(RecordType, "Record", null, Attr(Revision, "revision") + "," + Attr(ApprovedBy, "approvedBy"), null));
        h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"), PartBlock, Abstract));
        h.Write(PurchasedPartFile, Entity(PurchasedPart, "PurchasedPart", Part, Attr(SupplierQuote, "supplierQuote"), $$"""
            {"lifecycles":[{"lifecycle":"{{Delivery}}","onRevise":"restart"}],
             "attributes":[{"attribute":"{{SupplierQuote}}","onRevise":"clear"},{"attribute":"{{Note}}","onRevise":"copy"}]}
            """));
        h.Write(MadePartFile, Entity(MadePart, "MadePart", Part, "", $$"""
            {"levels":[{"format":"numbers"}],"separator":"-",
             "lifecycles":[{"lifecycle":"{{Approval}}","onRevise":"{{Approved}}"}],
             "attributes":[{"attribute":"{{TrackingCode}}","onRevise":"reset"},{"attribute":"{{ApprovedBy}}","onRevise":"clear"}]}
            """, ",\"stereotypes\":[\"tracked\"]"));
        h.Write("model/entities/document.json", Entity(Document, "Document", null, "", null));
        h.Write("model/entities/tool.json", Entity(Tool, "Tool", null, "", null));
        h.Write(MemoFile, Entity(Memo, "Memo", RecordType, "", $$"""{"boundAttribute":"{{Revision}}"}""", Abstract));
        h.Write(SheetFile, Entity(Sheet, "Sheet", Part, "", $$"""{"links":[{"end":"{{DocumentsEnd}}","onRevise":"stay"}]}"""));

        static string Subject(string id, string entity) => $$"""{"id":"{{id}}","entity":"{{entity}}"}""";
        h.Write("model/processes/delivery.json", Lifecycle(Delivery, "Delivery", Subject("01JSBJ00000000000000000101", Part),
            State(Waiting, "Waiting") + "," + State(Shipped, "Shipped")));
        h.Write("model/processes/machining.json", Lifecycle(Machining, "Machining", Subject("01JSBJ00000000000000000102", MadePart), State(Queued, "Queued")));
        h.Write("model/processes/approval.json", Lifecycle(Approval, "Approval", Subject("01JSBJ00000000000000000103", Part),
            State(Open, "Open") + "," + State(Approved, "Approved")));
        h.Write("model/processes/tracking.json", Lifecycle(Tracking, "Tracking", """{"id":"01JSBJ00000000000000000104","stereotype":"tracked"}""", State(Watched, "Watched")));
        h.Write("model/processes/shipping.json", Lifecycle(Shipping, "Shipping", Subject("01JSBJ00000000000000000105", Part), State(Running, "Running"), "orchestration"));

        h.Write("model/relations/part-documents.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{PartDocuments}}","name":"PartDocuments","package":"{{Package}}",
             "ends":[{"id":"{{PartsEnd}}","entity":"{{Part}}","role":"parts"},{"id":"{{DocumentsEnd}}","entity":"{{Document}}","role":"documents"}]}
            """);
        h.Write("model/relations/made-part-tools.json", $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{MadePartTools}}","name":"MadePartTools","package":"{{Package}}",
             "ends":[{"id":"{{MadePartsEnd}}","entity":"{{MadePart}}","role":"madeParts"},{"id":"{{ToolsEnd}}","entity":"{{Tool}}","role":"tools"}]}
            """);
    }
}
