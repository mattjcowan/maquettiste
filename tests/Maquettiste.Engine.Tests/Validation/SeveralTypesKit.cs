using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// An invented model for relations with several types at an end (several-types-design.md part 1), beside the <c>process-basics</c> fixture:
/// Item with Part (and PurchasedPart below it) and Assembly derived from it; Document, which carries <c>approvable</c>, with Memo derived from
/// it; Tool; and Comment. Comments are attached to a part or an assembly (AttachedTo, to-one), concern a part or anything approvable (Concerns,
/// to-one), sit on one part (OnPart, a one-type end) and tag parts and assemblies (Tagged, to-many). Lifecycle Review governs what is
/// approvable and PurchasedPart; lifecycle Triage runs on a comment, whose owner is what it is attached to.
/// </summary>
internal static class SeveralTypesKit
{
    public const string Sales = "01JPKG00000000000000000001";
    public const string Approvable = "01JSTR00000000000000000901";
    public const string TableOnly = "01JSTR00000000000000000902";
    public const string ApprovalCode = "01JATT00000000000000000990";
    public const string Item = "01JENT00000000000000000901";
    public const string Label = "01JATT00000000000000000912";
    public const string Part = "01JENT00000000000000000902";
    public const string PartNo = "01JATT00000000000000000921";
    public const string PurchasedPart = "01JENT00000000000000000903";
    public const string Assembly = "01JENT00000000000000000904";
    public const string Document = "01JENT00000000000000000905";
    public const string Memo = "01JENT00000000000000000906";
    public const string Tool = "01JENT00000000000000000907";
    public const string Comment = "01JENT00000000000000000908";
    public const string TextAttribute = "01JATT00000000000000000982";
    public const string Note2 = "01JATT00000000000000000983";
    public const string AttachedTo = "01JRXN00000000000000000901";
    public const string CommentsEnd = "01JEND00000000000000000911";
    public const string AttachedEnd = "01JEND00000000000000000912";
    public const string Concerns = "01JRXN00000000000000000902";
    public const string ConcernsEnd = "01JEND00000000000000000922";
    public const string OnPart = "01JRXN00000000000000000903";
    public const string OnPartEnd = "01JEND00000000000000000932";
    public const string Tagged = "01JRXN00000000000000000904";
    public const string TaggedEnd = "01JEND00000000000000000942";
    public const string Review = "01JPRC00000000000000000901";
    public const string Open = "01JSTA00000000000000000911";
    public const string Triage = "01JPRC00000000000000000902";
    public const string Go = "01JPRX00000000000000000911";
    public const string Check = "01JPRX00000000000000000912";
    public const string CheckNot = "01JPRX00000000000000000913";
    public const string CheckOnPart = "01JPRX00000000000000000914";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string EntityFile(string id, string name, string? @base = null, string? stereotype = null, string attributes = "") =>
        $$"""{"kind":"entity","id":"{{id}}","name":"{{name}}","package":"{{Sales}}"{{(@base is null ? "" : ",\"base\":\"" + @base + "\"")}}{{(stereotype is null ? "" : ",\"stereotypes\":[\"" + stereotype + "\"]")}}{{attributes}}}""";

    private static string Keyed(string idAttribute, string more = "") =>
        $$""","key":{"attributes":["{{idAttribute}}"]},"attributes":[{"id":"{{idAttribute}}","name":"id","type":"uuid"}{{more}}]""";

    /// <summary>A relation of a comment end and a far end, the far end's type written as given.</summary>
    public static string RelationFile(string id, string name, string nearEnd, string nearRole, string nearNavigation, string farEnd, string far, string farRole,
        string farNavigation, string farMax, string kind = "association", string nearMax = "\"*\"") =>
        $$"""{"kind":"relation","id":"{{id}}","name":"{{name}}","package":"{{Sales}}","relationKind":"{{kind}}","ends":[{"id":"{{nearEnd}}","entity":"{{Comment}}","role":"{{nearRole}}","navigation":"{{nearNavigation}}","max":{{nearMax}}},{"id":"{{farEnd}}",{{far}},"role":"{{farRole}}","navigation":"{{farNavigation}}","max":{{farMax}}}]}""";

    // case when in_state(subject.<end>, Review.Open) then 'yes' when not in_state(...) then 'no' else 'unknown' end
    private static string Probe(string end) =>
        $$$"""{"case":[{"when":{"inState":["{{{Open}}}"],"of":["{{{end}}}"]},"then":{"value":"yes"}},{"when":{"op":"not","args":[{"inState":["{{{Open}}}"],"of":["{{{end}}}"]}]},"then":{"value":"no"}}],"else":{"value":"unknown"}}""";

    public static void Write(LoaderHarness h)
    {
        h.Write("model/stereotypes/approvable.json", $$"""
            {"kind":"stereotype","id":"{{Approvable}}","key":"approvable","name":"Approvable","appliesTo":["entity"],
             "attributes":[{"id":"{{ApprovalCode}}","name":"approvalCode","type":"string","length":10}]}
            """);
        h.Write("model/stereotypes/table-only.json", $$"""{"kind":"stereotype","id":"{{TableOnly}}","key":"table-only","name":"TableOnly","appliesTo":["table"]}""");
        h.Write("model/entities/item.json", EntityFile(Item, "Item", attributes: Keyed("01JATT00000000000000000911", $$""",{"id":"{{Label}}","name":"label","type":"string","length":40}""")));
        h.Write("model/entities/part.json", EntityFile(Part, "Part", Item, attributes: $$""","attributes":[{"id":"{{PartNo}}","name":"partNo","type":"string","length":20}]"""));
        h.Write("model/entities/purchased-part.json", EntityFile(PurchasedPart, "PurchasedPart", Part));
        h.Write("model/entities/assembly.json", EntityFile(Assembly, "Assembly", Item));
        h.Write("model/entities/document.json", EntityFile(Document, "Document", stereotype: "approvable",
            attributes: Keyed("01JATT00000000000000000951", """,{"id":"01JATT00000000000000000952","name":"title","type":"string","length":40}""")));
        h.Write("model/entities/memo.json", EntityFile(Memo, "Memo", Document));
        h.Write("model/entities/tool.json", EntityFile(Tool, "Tool", attributes: Keyed("01JATT00000000000000000971")));
        h.Write("model/entities/comment.json", EntityFile(Comment, "Comment", attributes: Keyed("01JATT00000000000000000981",
            $$""",{"id":"{{TextAttribute}}","name":"text","type":"string","length":40},{"id":"{{Note2}}","name":"note2","type":"string","length":40}""")));
        h.Write("model/relations/attached-to.json", RelationFile(AttachedTo, "AttachedTo", CommentsEnd, "comment", "comments", AttachedEnd,
            $$""" "types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"}] """, "attachedTo", "attachedTo", "1"));
        h.Write("model/relations/concerns.json", RelationFile(Concerns, "Concerns", "01JEND00000000000000000921", "concernedBy", "concernedBy", ConcernsEnd,
            $$""" "types":[{"entity":"{{Part}}"},{"stereotype":"approvable"}] """, "concerns", "concerns", "1"));
        h.Write("model/relations/on-part.json", RelationFile(OnPart, "OnPart", "01JEND00000000000000000931", "onPartOf", "", OnPartEnd,
            $$""" "entity":"{{Part}}" """, "onPart", "onPart", "1"));
        h.Write("model/relations/tagged.json", RelationFile(Tagged, "Tagged", "01JEND00000000000000000941", "taggedBy", "", TaggedEnd,
            $$""" "types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"}] """, "tagged", "tagged", "\"*\""));
        h.Write("model/processes/review.json", $$"""
            {"kind":"process","id":"{{Review}}","name":"Review","package":"{{Sales}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000911","stereotype":"approvable"},{"id":"01JSBJ00000000000000000912","entity":"{{PurchasedPart}}"}],
             "states":[{"id":"{{Open}}","name":"Open"},{"id":"01JSTA00000000000000000912","name":"Closed"}],
             "initial":"{{Open}}"}
            """);
        h.Write("model/processes/triage.json", $$"""
            {"kind":"process","id":"{{Triage}}","name":"Triage","package":"{{Sales}}","use":"lifecycle",
             "subjects":[{"id":"01JSBJ00000000000000000921","entity":"{{Comment}}","owner":"{{AttachedEnd}}"}],
             "events":[{"id":"{{Go}}","name":"go"},{"id":"{{Check}}","name":"check"},{"id":"{{CheckNot}}","name":"checkNot"},{"id":"{{CheckOnPart}}","name":"checkOnPart"}],
             "guards":[{"id":"01JPRX00000000000000000921","name":"concernsOpen","condition":{"inState":["{{Open}}"],"of":["{{ConcernsEnd}}"]} },
               {"id":"01JPRX00000000000000000922","name":"concernsNotOpen","condition":{"op":"not","args":[{"inState":["{{Open}}"],"of":["{{ConcernsEnd}}"]}]} },
               {"id":"01JPRX00000000000000000923","name":"onPartOpen","condition":{"inState":["{{Open}}"],"of":["{{OnPartEnd}}"]} }],
             "actions":[{"id":"01JPRX00000000000000000931","name":"probe","sets":[
               {"id":"01JSET00000000000000000931","attribute":"{{TextAttribute}}","value":{{Probe(ConcernsEnd)}} },
               {"id":"01JSET00000000000000000932","attribute":"{{Note2}}","value":{{Probe(OnPartEnd)}} }]}],
             "states":[{"id":"01JSTA00000000000000000921","name":"S1"},{"id":"01JSTA00000000000000000922","name":"S2"}],
             "initial":"01JSTA00000000000000000921",
             "transitions":[
               {"id":"01JTRN00000000000000000921","source":"01JSTA00000000000000000921","event":"{{Go}}","targets":["01JSTA00000000000000000922"],"actions":["01JPRX00000000000000000931"]},
               {"id":"01JTRN00000000000000000922","source":"01JSTA00000000000000000921","event":"{{Check}}","guard":"01JPRX00000000000000000921","targets":["01JSTA00000000000000000922"]},
               {"id":"01JTRN00000000000000000923","source":"01JSTA00000000000000000921","event":"{{CheckNot}}","guard":"01JPRX00000000000000000922","targets":["01JSTA00000000000000000922"]},
               {"id":"01JTRN00000000000000000924","source":"01JSTA00000000000000000921","event":"{{CheckOnPart}}","guard":"01JPRX00000000000000000923","targets":["01JSTA00000000000000000922"]}]}
            """);
    }

    /// <summary>Opens the model, after <paramref name="prepare"/> writes or changes files.</summary>
    public static Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness>? prepare = null) =>
        ActorKit.OpenAsync(h =>
        {
            Write(h);
            prepare?.Invoke(h);
        });

    /// <summary>The findings of the whole model, after <paramref name="prepare"/>.</summary>
    public static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(Action<LoaderHarness>? prepare = null)
    {
        var (h, store) = await OpenAsync(prepare);
        using var _ = h;
        await using var __ = store;
        return await ActorKit.ValidateAsync(h, store.Current!);
    }

    /// <summary>The findings of one rule, as (pointer, message), in the files under a path.</summary>
    public static List<(string? Pointer, string Message)> Of(IReadOnlyList<Diagnostic> found, string rule, string? path = null) =>
        [.. found.Where(d => d.Rule == rule && (path is null || (d.FilePath ?? "").Contains(path, StringComparison.Ordinal))).Select(d => (d.JsonPointer, d.Message))];

    public static RelationEnd EndOf(ModelSnapshot model, string relation, string end) => model.Get<Relation>(relation)!.Ends.Single(e => e.Id == end);
}
