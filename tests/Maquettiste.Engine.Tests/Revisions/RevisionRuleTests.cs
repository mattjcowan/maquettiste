using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Revisions;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Validation;
using static Maquettiste.Engine.Tests.Revisions.RevisionFamily;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// The rules of revisions (revisions-design.md section 7) over <see cref="RevisionFamily"/>: failing and passing cases of every MQ8301 message,
/// MQ8303, each case of MQ8305 (decision 11), MQ8306, MQ8308 and every MQ8312 case, with findings on the file that holds the offending key and
/// on the derived entity when the conflict comes only from merging; MQ2020 on the block's ids; MQ9802's revision case; each incremental
/// dependent of a save; and today's models keeping their findings with no extra work.
/// </summary>
public sealed class RevisionRuleTests
{
    private static readonly string[] Rules = ["MQ8301", "MQ8303", "MQ8305", "MQ8306", "MQ8308", "MQ8312"];

    private const string Order = "01JENT00000000000000000001";
    private const string Total = "01JATT00000000000000000106";
    private const string Code = "01JSCT00000000000000000101";
    private const string Back = "01JSTA00000000000000000108";
    private const string Ship = "01JEVT00000000000000000101";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(Action<LoaderHarness>? prepare = null)
    {
        var (h, store) = await OpenAsync(prepare);
        using var _ = h;
        await using var __ = store;
        return (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;
    }

    /// <summary>The findings of a rule as (file name, pointer, message), ordered.</summary>
    private static (string File, string Pointer, string Message)[] Of(IEnumerable<Diagnostic> found, string rule) =>
        [.. found.Where(d => d.Rule == rule).Select(d => (Path.GetFileName(d.FilePath ?? ""), d.JsonPointer ?? "", d.Message)).Distinct().Order()];

    private static string Levels(string levels, string extra = "") => $$"""{"levels":[{{levels}}]{{extra}}}""";

    private static void Document(LoaderHarness h, string block) => h.Write("model/entities/document.json", Entity(RevisionFamily.Document, "Document", null, "", block));

    /// <summary>Memo with levels of its own, so the family's one finding (a block naming no levels) stays out of the way.</summary>
    private static void MemoWithLevels(LoaderHarness h, string? extra = null, string bound = Revision) =>
        h.Write(MemoFile, Entity(Memo, "Memo", RecordType, "", Levels("""{"format":"numbers"}""", $",\"boundAttribute\":\"{bound}\"" + (extra ?? "")), Abstract));

    private static string Record(string revision = "", string approvedBy = "", string more = "", string extra = "") =>
        Entity(RecordType, "Record", null,
            $$"""{"id":"{{Revision}}","name":"revision","type":"string","length":40{{revision}}},{"id":"{{ApprovedBy}}","name":"approvedBy","type":"string","length":40{{approvedBy}}}{{more}}""",
            null, extra);

    private static string Lifecycle(string id, string name, string subjects, string states, string use = "lifecycle", string transitions = "", string events = "", string actions = "") => $$"""
        {"$schema":"../../.schema/v1/process.json","kind":"process","id":"{{id}}","name":"{{name}}","package":"{{RevisionFamily.Package}}","use":"{{use}}",
         "subjects":[{{subjects}}]{{(events.Length > 0 ? ",\"events\":[" + events + "]" : "")}}{{(actions.Length > 0 ? ",\"actions\":[" + actions + "]" : "")}},
         "states":[{{states}}]{{(transitions.Length > 0 ? ",\"transitions\":[" + transitions + "]" : "")}}}
        """;

    private static string Subject(string id, string entity, string bound = "") =>
        $$"""{"id":"{{id}}","entity":"{{entity}}"{{(bound.Length > 0 ? $",\"boundAttribute\":\"{bound}\"" : "")}}}""";

    private static string State(string id, string name, string type = "") => $$"""{"id":"{{id}}","name":"{{name}}"{{(type.Length > 0 ? $",\"type\":\"{type}\"" : "")}}}""";

    private static string DeliveryFile(string transitions = "", string actions = "", string bound = "", string extraStates = "") =>
        Lifecycle(RevisionFamily.Delivery, "Delivery", Subject("01JSBJ00000000000000000101", Part, bound), State(Waiting, "Waiting") + "," + State(Shipped, "Shipped") + extraStates,
            transitions: transitions, events: transitions.Length > 0 ? $$"""{"id":"{{Ship}}","name":"ship"}""" : "", actions: actions);

    private static string ShipMove(int level) =>
        $$"""{"id":"01JTRN00000000000000000101","source":"{{Waiting}}","event":"{{Ship}}","targets":["{{Shipped}}"],"nextRevision":{{level}}}""";

    private static string ApprovalFile(string bound = "") =>
        Lifecycle(RevisionFamily.Approval, "Approval", Subject("01JSBJ00000000000000000103", Part, bound), State(Open, "Open") + "," + State(Approved, "Approved"));

    private static string MachiningFile(string bound = "") =>
        Lifecycle(RevisionFamily.Machining, "Machining", Subject("01JSBJ00000000000000000102", MadePart, bound), State(Queued, "Queued"));

    private static async Task<SaveResult> SaveAsync(ModelStore store, string id, string json) =>
        await store.SaveAsync(id, Encoding.UTF8.GetBytes(json), store.Current!.GetDocument(id)!.Hash, ChangeSource.Editor, Ct);

    // ---- the family ----

    [Fact]
    public async Task The_family_gives_only_the_finding_it_writes()
    {
        var found = await ValidateAsync();

        // Memo names only boundAttribute, and neither it nor Record names levels; the rest of the family stands.
        Assert.Equal([("memo.json", "/revisions", "'Memo' has a revisions block, but neither it nor a base names levels; give the block levels.")], Of(found, "MQ8301"));
        Assert.DoesNotContain(found, d => Rules.Contains(d.Rule) && d.Rule != "MQ8301");

        // With levels on Memo, nothing; a derived entity without a block of its own leaves "no levels" to the entity whose block it is.
        Assert.DoesNotContain(await ValidateAsync(h => MemoWithLevels(h)), d => Rules.Contains(d.Rule));
        var derived = await ValidateAsync(h =>
        {
            h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"), $$"""{"boundAttribute":"{{Revision}}"}""", Abstract));
            h.Write(MadePartFile, Entity(MadePart, "MadePart", Part, "", null));
            h.Write(PurchasedPartFile, Entity(PurchasedPart, "PurchasedPart", Part, Attr(SupplierQuote, "supplierQuote"), null));
            h.Write(SheetFile, Entity(Sheet, "Sheet", Part, "", null));
        });
        Assert.Equal(["memo.json", "part.json"], Of(derived, "MQ8301").Select(f => f.File));
    }

    // ---- MQ8301 ----

    [Fact]
    public async Task MQ8301_reports_both_or_neither_of_format_and_values()
    {
        var found = await ValidateAsync(h => Document(h, Levels("""{"format":"letters","values":["X"]},{}""")));
        Assert.Equal(
        [
            ("document.json", "/revisions/levels/0", "Level 1 of 'Document' has both format and values; keep one."),
            ("document.json", "/revisions/levels/1", "Level 2 of 'Document' has neither format nor values; give it a format, letters or numbers, or a list of values."),
        ], Of(found, "MQ8301").Where(f => f.File == "document.json"));
    }

    [Fact]
    public async Task MQ8301_reports_keys_that_do_not_fit_the_level_and_a_repeated_name()
    {
        var found = await ValidateAsync(h => Document(h, Levels("""
            {"name":"major","format":"letters","width":2},{"name":"minor","format":"numbers","skip":["I"]},
            {"name":"minor","values":["Pilot"],"first":"Pilot","skip":["A"]}
            """)));
        Assert.Equal(
        [
            ("document.json", "/revisions/levels/0/width", "Level 1 'major' of 'Document' is letters, and width is for a numbers level only; remove it."),
            ("document.json", "/revisions/levels/1/skip", "Level 2 'minor' of 'Document' is numbers, and skip is for a letters level only; remove it."),
            ("document.json", "/revisions/levels/2/first", "Level 3 'minor' of 'Document' is a list of values, so first, skip and width don't apply; remove first and skip."),
            ("document.json", "/revisions/levels/2/name", "Levels 2 and 3 of 'Document' are both named 'minor'; give each its own name."),
        ], Of(found, "MQ8301").Where(f => f.File == "document.json"));
    }

    [Fact]
    public async Task MQ8301_reports_a_first_that_is_not_a_value_and_a_level_below_an_empty_one()
    {
        var found = await ValidateAsync(h => Document(h, Levels("""
            {"format":"letters","first":"a","startEmpty":true},{"format":"numbers","first":"1"},{"format":"letters","skip":["I"],"first":"AI","startEmpty":true}
            """)));
        Assert.Equal(
        [
            ("document.json", "/revisions/levels/0/first", "Level 1 of 'Document' starts at 'a', which is not a value of its letters: its letters are upper case; give a run of the letters it uses, such as 'A'."),
            ("document.json", "/revisions/levels/1", "Level 2 of 'Document' doesn't start empty, below level 1, which does; give it startEmpty, since empty levels only come at the end."),
            ("document.json", "/revisions/levels/1/first", "Level 2 of 'Document' starts at '1', which is not a whole number; give a number such as 1, without quotes."),
            ("document.json", "/revisions/levels/2/first", "Level 3 of 'Document' starts at 'AI', which is not a value of its letters: 'I' is skipped; give a run of the letters it uses, such as 'A'."),
        ], Of(found, "MQ8301").Where(f => f.File == "document.json"));

        var letters = await ValidateAsync(h => Document(h, Levels("""
            {"format":"letters","skip":["A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z"]},{"format":"letters","first":1}
            """)));
        Assert.Equal(
        [
            ("document.json", "/revisions/levels/0/skip", "Level 1 of 'Document' skips every letter, so it has no values; skip fewer letters."),
            ("document.json", "/revisions/levels/1/first", "Level 2 of 'Document' starts at 1, which is not a value of its letters: a letters level starts at letters, written as a string; give a run of the letters it uses, such as 'A'."),
        ], Of(letters, "MQ8301").Where(f => f.File == "document.json"));
    }

    [Fact]
    public async Task MQ8301_reports_a_list_value_that_holds_the_separator_where_the_list_or_the_separator_is_written()
    {
        var found = await ValidateAsync(h => Document(h, Levels("""{"values":["A.1","B"]}""")));
        Assert.Equal([("document.json", "/revisions/levels/0/values/0", "Value 'A.1' of level 1 of 'Document' holds the separator '.', so its revisions can't be read back; change the value or the separator.")],
            Of(found, "MQ8301").Where(f => f.File == "document.json"));

        // The list on Part, the separator on PurchasedPart: only the derived entity writes both, and its separator is the offending key.
        var merged = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"), Levels("""{"values":["A-1","B"]}"""), Abstract));
            h.Write(PurchasedPartFile, Entity(PurchasedPart, "PurchasedPart", Part, Attr(SupplierQuote, "supplierQuote"), """{"separator":"-"}"""));
        });
        Assert.Equal([("purchased-part.json", "/revisions/separator", "Value 'A-1' of level 1 of 'PurchasedPart' holds the separator '-', so its revisions can't be read back; change the value or the separator.")],
            Of(merged, "MQ8301"));

        // Passing: the family's levels, and a list without the separator.
        Assert.DoesNotContain(Of(await ValidateAsync(h => Document(h, Levels("""{"values":["Prototype","Pilot"]},{"format":"numbers","startEmpty":true}"""))), "MQ8301"),
            f => f.File == "document.json");
    }

    // ---- MQ8303 ----

    [Fact]
    public async Task MQ8303_reports_a_separator_with_a_letter_or_digit_and_an_empty_one_between_levels_of_one_kind()
    {
        var chars = await ValidateAsync(h => Document(h, Levels("""{"format":"letters"}""", ""","separator":"x1" """)));
        Assert.Equal([("document.json", "/revisions/separator", "The separator 'x1' of 'Document' holds a letter or a digit, so its revisions can't be read back; give a separator such as '.' or '-'.")],
            Of(chars, "MQ8303"));

        var numbers = await ValidateAsync(h => Document(h, Levels("""{"format":"numbers"},{"format":"numbers"},{"format":"letters"}""", ""","separator":"" """)));
        Assert.Equal([("document.json", "/revisions/separator", "'Document' writes two numbers levels with no separator, so '111' could be 1.11 or 11.1; give a separator.")],
            Of(numbers, "MQ8303"));
        var letters = await ValidateAsync(h => Document(h, Levels("""{"format":"letters"},{"format":"letters"}""", ""","separator":"" """)));
        Assert.Equal([("document.json", "/revisions/separator", "'Document' writes two letters levels with no separator, so 'AAA' could be A.AA or AA.A; give a separator.")],
            Of(letters, "MQ8303"));
        var list = await ValidateAsync(h => Document(h, Levels("""{"values":["P","Q"]},{"format":"numbers"}""", ""","separator":"" """)));
        Assert.Equal([("document.json", "/revisions/separator", "'Document' writes a list level beside another level with no separator, so its revisions can't be split into levels again; give a separator.")],
            Of(list, "MQ8303"));

        // Passing: letters beside numbers with no separator, and a dash.
        Assert.Empty(Of(await ValidateAsync(h => Document(h, Levels("""{"format":"letters"},{"format":"numbers"}""", ""","separator":"" """))), "MQ8303"));
        Assert.Empty(Of(await ValidateAsync(h => Document(h, Levels("""{"format":"numbers"},{"format":"numbers"}""", ""","separator":"-" """))), "MQ8303"));
    }

    [Fact]
    public async Task MQ8303_from_merging_goes_on_the_derived_entity_that_writes_the_levels()
    {
        // Part writes "" between letters and numbers, which stands; PurchasedPart's own numbers levels don't, under Part's separator.
        var found = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"), Levels("""{"format":"letters"},{"format":"numbers"}""", ""","separator":"" """), Abstract));
            h.Write(PurchasedPartFile, Entity(PurchasedPart, "PurchasedPart", Part, Attr(SupplierQuote, "supplierQuote"), Levels("""{"format":"numbers"},{"format":"numbers"}""")));
        });
        Assert.Equal([("purchased-part.json", "/revisions/levels/1", "'PurchasedPart' writes two numbers levels with no separator, so '111' could be 1.11 or 11.1; give a separator.")],
            Of(found, "MQ8303"));
    }

    // ---- MQ8305 ----

    [Fact]
    public async Task MQ8305_reports_a_bound_attribute_of_the_wrong_type_derived_or_multi_valued()
    {
        var number = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/entities/record.json", Record().Replace("\"type\":\"string\",\"length\":40}", "\"type\":\"int32\"}", StringComparison.Ordinal));
        });
        Assert.Equal(
        [
            ("memo.json", "/revisions/boundAttribute", "Attribute 'revision' of 'Memo' is an int32, and its revisions are numbers; hold the revision in a string attribute."),
            ("part.json", "/revisions/boundAttribute", "Attribute 'revision' of 'Part' is an int32, and its revisions are letters and numbers; hold the revision in a string attribute."),
        ], Of(number, "MQ8305"));

        var scalar = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/scalar-types/code.json", $$"""{"kind":"scalar-type","id":"{{Code}}","name":"Code","package":"{{RevisionFamily.Package}}","base":"int64"}""");
            h.Write("model/entities/record.json", Record().Replace($"\"name\":\"revision\",\"type\":\"string\",\"length\":40", $"\"name\":\"revision\",\"type\":{{\"ref\":\"{Code}\"}}", StringComparison.Ordinal));
        });
        Assert.Contains(("part.json", "/revisions/boundAttribute", "Attribute 'revision' of 'Part' is typed by scalar type 'Code', an int64, and its revisions are letters and numbers; hold the revision in a string attribute."),
            Of(scalar, "MQ8305"));

        var derived = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/entities/record.json", Record(revision: ""","derived":{"value":{"value":"A"}}"""));
        });
        Assert.Contains(("part.json", "/revisions/boundAttribute", "Attribute 'revision' of 'Part' is derived, worked out from others, so it can't hold the revision; hold the revision in a plain string attribute."),
            Of(derived, "MQ8305"));

        var collection = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/entities/record.json", Record(revision: ""","collection":true"""));
        });
        Assert.Contains(("part.json", "/revisions/boundAttribute", "Attribute 'revision' of 'Part' holds a collection, so it can't hold the revision; hold the revision in a single string attribute."),
            Of(collection, "MQ8305"));

        // Passing: a text attribute, and a scalar type on string.
        Assert.Empty(Of(await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/scalar-types/code.json", $$"""{"kind":"scalar-type","id":"{{Code}}","name":"Code","package":"{{RevisionFamily.Package}}","base":"string"}""");
            h.Write("model/entities/record.json", Record().Replace($"\"name\":\"revision\",\"type\":\"string\",\"length\":40", $"\"name\":\"revision\",\"type\":{{\"ref\":\"{Code}\"}}", StringComparison.Ordinal));
        }), "MQ8305"));
    }

    [Fact]
    public async Task MQ8305_reports_an_attribute_the_entity_does_not_hold_and_a_lifecycles_bound_attribute()
    {
        var foreign = await ValidateAsync(h => MemoWithLevels(h, bound: Note));
        Assert.Equal([("memo.json", "/revisions/boundAttribute",
            "Attribute 'note', which holds the revision of 'Memo', is not one of its attributes (its own, an inherited one or one from a stereotype it carries); name one of them.")],
            Of(foreign, "MQ8305"));

        // Delivery binds its state to revision on Part: Part reports it, its derived entities inherit both and stay quiet.
        var delivery = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/delivery.json", DeliveryFile(bound: Revision));
        });
        Assert.Equal([("part.json", "/revisions/boundAttribute",
            "Attribute 'revision' of 'Part' holds the state of lifecycle 'Delivery', which only its chart moves, so it can't hold the revision too; hold the revision in its own attribute.")],
            Of(delivery, "MQ8305"));

        // Machining governs only MadePart: the conflict comes from merging Part's boundAttribute, so MadePart reports it at its base.
        var machining = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/machining.json", MachiningFile(Revision));
        });
        Assert.Equal([("made-part.json", "/base",
            "Attribute 'revision' of 'MadePart' holds the state of lifecycle 'Machining', which only its chart moves, so it can't hold the revision too; hold the revision in its own attribute.")],
            Of(machining, "MQ8305"));
    }

    [Fact]
    public async Task MQ8305_reports_a_default_that_is_not_a_revision_and_an_entry_naming_the_bound_attribute()
    {
        // "A" is a revision of Part's letters, not of MadePart's numbers, which MadePart writes.
        var start = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/entities/record.json", Record(revision: ""","default":"A" """));
        });
        Assert.Equal(
        [
            ("made-part.json", "/revisions/levels", "The default 'A' of attribute 'revision' of 'MadePart' is not a revision of its sequence, and a new object starts from it; give a default such as '1', or none."),
            ("memo.json", "/revisions/boundAttribute", "The default 'A' of attribute 'revision' of 'Memo' is not a revision of its sequence, and a new object starts from it; give a default such as '1', or none."),
        ], Of(start, "MQ8305"));
        var lower = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/entities/record.json", Record(revision: ""","default":"a" """));
        });
        Assert.Contains(("part.json", "/revisions/boundAttribute",
            "The default 'a' of attribute 'revision' of 'Part' is not a revision of its sequence, and a new object starts from it; give a default such as 'A', or none."), Of(lower, "MQ8305"));

        var entry = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"),
                PartBlock.Replace("{\"attribute\":\"" + Note + "\",\"onRevise\":\"clear\"}", "{\"attribute\":\"" + Revision + "\",\"onRevise\":\"reset\"}", StringComparison.Ordinal), Abstract));
        });
        Assert.Equal([("part.json", "/revisions/boundAttribute",
            "Attribute 'revision' of 'Part' holds the revision, which only revising moves, so the block's attributes can't reset it; remove the entry.")], Of(entry, "MQ8305"));

        // Passing: copy on the bound attribute undoes nothing harmful.
        Assert.Empty(Of(await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"),
                PartBlock.Replace("{\"attribute\":\"" + Note + "\",\"onRevise\":\"clear\"}", "{\"attribute\":\"" + Revision + "\",\"onRevise\":\"copy\"}", StringComparison.Ordinal), Abstract));
        }), "MQ8305"));
    }

    // ---- MQ8306 ----

    [Fact]
    public async Task MQ8306_reports_a_level_a_governed_entity_lacks_a_lifecycle_governing_none_and_an_orchestration()
    {
        // Delivery governs Part (two levels) and MadePart (one level of its own).
        var level = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/delivery.json", DeliveryFile(ShipMove(2)));
        });
        Assert.Equal([("delivery.json", "/transitions/0/nextRevision", "Transition Waiting → Shipped names revision level 2, but entity 'MadePart' has one level; name level 1, or add a level.")],
            Of(level, "MQ8306"));
        Assert.Empty(Of(await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/delivery.json", DeliveryFile(ShipMove(1)));
        }), "MQ8306"));

        var none = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/delivery.json", DeliveryFile(ShipMove(1)).Replace($"\"entity\":\"{Part}\"", $"\"entity\":\"{Order}\"", StringComparison.Ordinal));
        });
        Assert.Contains(("delivery.json", "/transitions/0/nextRevision",
            "Transition Waiting → Shipped names revision level 1, but lifecycle 'Delivery' governs no entity with revisions; give one of its entities a revisions block, or remove nextRevision."), Of(none, "MQ8306"));

        var orchestration = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/delivery.json", DeliveryFile(ShipMove(1)).Replace("\"use\":\"lifecycle\"", "\"use\":\"orchestration\"", StringComparison.Ordinal));
        });
        Assert.Contains(("delivery.json", "/transitions/0/nextRevision",
            "Transition Waiting → Shipped names revision level 1, but 'Delivery' is an orchestration, which holds no revisions; remove nextRevision."), Of(orchestration, "MQ8306"));
    }

    // ---- MQ8308 ----

    [Fact]
    public async Task MQ8308_reports_duplicate_where_the_entitys_own_end_has_max_one()
    {
        static string Relation(string max) => $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{PartDocuments}}","name":"PartDocuments","package":"{{RevisionFamily.Package}}",
             "ends":[{"id":"{{PartsEnd}}","entity":"{{Part}}","role":"parts"{{max}}},{"id":"{{DocumentsEnd}}","entity":"{{RevisionFamily.Document}}","role":"documents"}]}
            """;
        var found = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/relations/part-documents.json", Relation(",\"max\":1"));
        });
        Assert.Equal([("part.json", "/revisions/links/0/onRevise",
            "Revising 'Part' can't duplicate its 'documents' links: its own end 'parts' has max 1, so each linked object may link to one 'Part', and would link to two; use move or stay.")],
            Of(found, "MQ8308"));
        Assert.Empty(Of(await ValidateAsync(h => MemoWithLevels(h)), "MQ8308"));
    }

    // ---- MQ8312 ----

    [Fact]
    public async Task MQ8312_reports_two_entries_for_one_item_and_lifecycles_or_states_that_cannot_stand()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write("model/processes/delivery.json", DeliveryFile(extraStates: "," + State(Back, "Back", "history")));
            MemoWithLevels(h, $$"""
                ,"lifecycles":[{"lifecycle":"{{RevisionFamily.Delivery}}","onRevise":"keep"},{"lifecycle":"{{Shipping}}","onRevise":"keep"}],
                 "links":[{"end":"{{DocumentsEnd}}","onRevise":"stay"}]
                """);
            h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"), $$"""
                {"levels":[{"format":"letters"}],
                 "lifecycles":[{"lifecycle":"{{RevisionFamily.Delivery}}","onRevise":"{{Approved}}"},{"lifecycle":"{{RevisionFamily.Delivery}}","onRevise":"keep"},
                   {"lifecycle":"{{RevisionFamily.Approval}}","onRevise":"{{Open}}"},{"lifecycle":"{{Machining}}","onRevise":"{{Queued}}"},{"lifecycle":"{{Tracking}}","onRevise":"restart"}],
                 "attributes":[{"attribute":"{{Note}}","onRevise":"clear"},{"attribute":"{{Note}}","onRevise":"reset"},{"attribute":"{{SupplierQuote}}","onRevise":"clear"}],
                 "links":[{"end":"{{ToolsEnd}}","onRevise":"move"},{"end":"{{ToolsEnd}}","onRevise":"stay"}]}
                """, Abstract));
            h.Write("model/processes/approval.json", Lifecycle(RevisionFamily.Approval, "Approval", Subject("01JSBJ00000000000000000103", Part),
                State(Open, "Open") + "," + State(Approved, "Approved") + "," + State("01JSTA00000000000000000109", "Pick", "choice")));
            h.Write(SheetFile, Entity(Sheet, "Sheet", Part, "", $$"""{"lifecycles":[{"lifecycle":"{{RevisionFamily.Delivery}}","onRevise":"{{Back}}"}]}"""));
        });
        Assert.Equal(
        [
            ("memo.json", "/revisions/lifecycles/0/lifecycle", "Lifecycle 'Delivery' governs neither 'Memo' nor an entity derived from it; remove the entry."),
            ("memo.json", "/revisions/lifecycles/1/lifecycle", "Process 'Shipping' is an orchestration, not a lifecycle, so it governs no entity; remove the entry."),
            ("memo.json", "/revisions/links/0/end", "Neither 'Memo' nor an entity derived from it reaches end 'documents' of relation 'PartDocuments'; remove the entry."),
            ("part.json", "/revisions/attributes/1/attribute", "'Part' has two entries for attribute 'note'; remove the second."),
            ("part.json", "/revisions/attributes/2/attribute",
                "Attribute 'supplierQuote' is not one of the attributes of 'Part' (its own, an inherited one or one from a stereotype it carries); remove the entry."),
            ("part.json", "/revisions/lifecycles/0/onRevise", "State 'Approved' belongs to lifecycle 'Approval', not 'Delivery'; name one of its states, restart or keep."),
            ("part.json", "/revisions/lifecycles/1/lifecycle", "'Part' has two entries for lifecycle 'Delivery'; remove the second."),
            ("part.json", "/revisions/links/1/end", "'Part' has two entries for end 'tools'; remove the second."),
            ("sheet.json", "/revisions/lifecycles/0/onRevise", "State 'Back' of lifecycle 'Delivery' is a history state, which a revise can't enter; name another state, restart or keep."),
        ], Of(found, "MQ8312"));
    }

    [Fact]
    public async Task MQ8312_reports_attributes_that_cannot_be_carried_over_and_ends_of_relations_with_more_than_two_ends()
    {
        const string Trio = "01JRXN00000000000000000109";
        var found = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/entities/record.json", Record(more: ",{\"id\":\"" + Total + "\",\"name\":\"total\",\"type\":\"string\",\"derived\":{\"value\":{\"value\":\"x\"}}}",
                extra: $$""","key":{"attributes":["{{ApprovedBy}}"]}"""));
            h.Write("model/processes/approval.json", ApprovalFile(Note));
            h.Write("model/relations/trio.json", $$"""
                {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{Trio}}","name":"Trio","package":"{{RevisionFamily.Package}}","relationKind":"n-ary",
                 "ends":[{"id":"01JEND00000000000000000191","entity":"{{Part}}","role":"part"},{"id":"01JEND00000000000000000192","entity":"{{RevisionFamily.Document}}","role":"document"},
                   {"id":"01JEND00000000000000000193","entity":"{{RevisionFamily.Tool}}","role":"tool"}]}
                """);
            h.Write(SheetFile, Entity(Sheet, "Sheet", Part, "", $$"""
                {"attributes":[{"attribute":"{{Total}}","onRevise":"clear"}],"links":[{"end":"01JEND00000000000000000193","onRevise":"stay"}]}
                """));
        });
        Assert.Equal(
        [
            // MadePart clears approvedBy itself; Part resets it and clears note, which Approval binds on Part and so on MadePart, PurchasedPart and Sheet.
            ("made-part.json", "/revisions/attributes/1/attribute", "Attribute 'approvedBy' is part of the key of 'MadePart', so a revision can't clear it; remove the entry."),
            ("part.json", "/revisions/attributes/0/attribute", "Attribute 'approvedBy' is part of the key of 'Part', so a revision can't reset it; remove the entry."),
            ("part.json", "/revisions/attributes/1/attribute", "Attribute 'note' of 'Part' holds the state of lifecycle 'Approval' and follows the state, so a revise can't clear it; remove the entry."),
            ("sheet.json", "/revisions/attributes/0/attribute", "Attribute 'total' of 'Sheet' is derived, worked out from others, so a revise doesn't carry it over; remove the entry."),
            ("sheet.json", "/revisions/links/0/end", "End 'tool' belongs to relation 'Trio', which has more than two ends, so a revise can't say what its links do; remove the entry."),
        ], Of(found, "MQ8312"));
    }

    [Fact]
    public async Task MQ8312_from_merging_goes_on_the_derived_entity_and_copy_undoes_it()
    {
        // Machining governs only MadePart and binds note, which Part's entry clears: only MadePart has the conflict, at its base.
        var found = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/machining.json", MachiningFile(Note));
        });
        Assert.Equal([("made-part.json", "/base",
            "Attribute 'note' of 'MadePart' holds the state of lifecycle 'Machining' and follows the state, so a revise can't clear it; remove the entry.")], Of(found, "MQ8312"));

        // MadePart undoes Part's entry with copy.
        var undone = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/machining.json", MachiningFile(Note));
            h.Write(MadePartFile, Entity(MadePart, "MadePart", Part, "", $$"""{"levels":[{"format":"numbers"}],"attributes":[{"attribute":"{{Note}}","onRevise":"copy"}]}""",
                ",\"stereotypes\":[\"tracked\"]"));
        });
        Assert.Empty(Of(undone, "MQ8312"));
    }

    // ---- MQ2020 ----

    [Fact]
    public async Task MQ2020_reports_every_id_of_the_block_from_a_package_the_entitys_package_does_not_depend_on()
    {
        const string Other = "01JPKG00000000000000000109";
        var found = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/packages/other.json", $$"""{"kind":"package","id":"{{Other}}","name":"Other"}""");
            h.Write("model/packages/sales.json", $$"""{"kind":"package","id":"{{RevisionFamily.Package}}","name":"Sales","dependsOn":[]}""");
            h.Write("model/processes/approval.json", ApprovalFile().Replace($"\"package\":\"{RevisionFamily.Package}\"", $"\"package\":\"{Other}\"", StringComparison.Ordinal));
            h.Write("model/entities/tool.json", Entity(RevisionFamily.Tool, "Tool", null, "", null).Replace($"\"package\":\"{RevisionFamily.Package}\"", $"\"package\":\"{Other}\"", StringComparison.Ordinal));
            h.Write("model/entities/record.json", Record().Replace($"\"package\":\"{RevisionFamily.Package}\"", $"\"package\":\"{Other}\"", StringComparison.Ordinal));
        });

        // MadePart names Approval and its state Approved, and Record's approvedBy; Part names Record's revision and approvedBy. The relation's
        // end tools sits in Sales, so Part's link entry is no finding.
        var entries = found.Where(d => d.Rule == "MQ2020" && (d.JsonPointer ?? "").StartsWith("/revisions", StringComparison.Ordinal))
            .Select(d => (Path.GetFileName(d.FilePath ?? ""), d.JsonPointer)).Distinct().Order().ToList();
        Assert.Equal(
        [
            ("made-part.json", "/revisions/attributes/1/attribute"),
            ("made-part.json", "/revisions/lifecycles/0/lifecycle"),
            ("made-part.json", "/revisions/lifecycles/0/onRevise"),
            ("memo.json", "/revisions/boundAttribute"),
            ("part.json", "/revisions/attributes/0/attribute"),
            ("part.json", "/revisions/boundAttribute"),
        ], entries);
        Assert.DoesNotContain(found, d => d.Rule == "MQ2020" && (d.JsonPointer ?? "").StartsWith("/revisions/links", StringComparison.Ordinal));
    }

    // ---- MQ9802 ----

    [Fact]
    public async Task MQ9802_refuses_a_set_naming_the_bound_revision_attribute()
    {
        static string Set(string attribute) =>
            $$$"""{"id":"01JPRX00000000000000000101","name":"stamp","sets":[{"id":"01JSET00000000000000000101","attribute":"{{{attribute}}}","value":{"value":"B"}}]}""";
        var found = await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/delivery.json", DeliveryFile(actions: Set(Revision)));
        });
        Assert.Equal([("delivery.json", "/actions/0/sets/0/attribute", "The set's attribute 'revision' holds the revision, which only revising moves; remove the set.")], Of(found, "MQ9802"));

        // Passing: another attribute, and the same attribute where no governed entity binds it (Order's lifecycle governs no block).
        Assert.Empty(Of(await ValidateAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/processes/delivery.json", DeliveryFile(actions: Set(ApprovedBy)));
        }), "MQ9802"));
    }

    // ---- incremental validation ----

    [Fact]
    public async Task An_entity_save_checks_its_family_the_lifecycles_naming_a_level_and_the_scenarios_with_its_objects()
    {
        const string Scenario = "01JSCN00000000000000000109";
        var (h, store) = await OpenAsync(h =>
        {
            MemoWithLevels(h);
            h.Write(MadePartFile, Entity(MadePart, "MadePart", Part, "", """{"levels":[{"format":"numbers"},{"format":"numbers"}]}""", ",\"stereotypes\":[\"tracked\"]"));
            h.Write("model/processes/delivery.json", DeliveryFile(ShipMove(2)));
            h.Write("model/scenarios/delivery/ship.json", $$"""
                {"$schema":"../../../.schema/v1/scenario.json","kind":"scenario","id":"{{Scenario}}","name":"Ship","process":"{{RevisionFamily.Delivery}}",
                 "objects":[{"id":"01JXBJ00000000000000000109","name":"p","entity":"{{PurchasedPart}}"}],"subject":"01JXBJ00000000000000000109",
                 "steps":[{"id":"01JSTP00000000000000000109","event":"{{Ship}}"}]}
                """);
        });
        using var _ = h;
        await using var __ = store;
        var model = store.Current!;

        // Record (no block) brings its family's blocks: Part, its derived entities and Memo, the lifecycles governing them with a nextRevision,
        // and its objects' scenarios; Document and Tool, outside the family, aren't brought.
        var targets = ModelValidator.TargetIds(model, new ValidationScope([RecordType], IncludeReferrers: false));
        Assert.Superset(new HashSet<string>([Part, PurchasedPart, MadePart, Sheet, Memo, RevisionFamily.Delivery, Scenario]), targets.ToHashSet());
        Assert.DoesNotContain(RevisionFamily.Document, targets);
        Assert.DoesNotContain(RevisionFamily.Tool, targets);
        // A derived entity brings its bases with a block (Part), whose entries may name what only it holds or reaches.
        Assert.Contains(Part, ModelValidator.TargetIds(model, new ValidationScope([Sheet], IncludeReferrers: false)));

        // MadePart down to one level: Delivery, which names no MadePart id, now names a level MadePart lacks.
        var saved = await SaveAsync(store, MadePart, Entity(MadePart, "MadePart", Part, "", """{"levels":[{"format":"numbers"}]}""", ",\"stereotypes\":[\"tracked\"]"));
        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ8306" && Path.GetFileName(d.FilePath) == "delivery.json");
    }

    [Fact]
    public async Task An_entity_leaving_the_family_checks_its_former_bases_again()
    {
        var (h, store) = await OpenAsync(h => MemoWithLevels(h));
        using var _ = h;
        await using var __ = store;

        // MadePart derives from Record now: Part's entry for Machining and its link entry for tools, which only MadePart answered, can't stand.
        // Part names no MadePart id, and is no longer its base: the store brings the bases the entity had.
        var saved = await SaveAsync(store, MadePart, Entity(MadePart, "MadePart", RecordType, "", """{"levels":[{"format":"numbers"}]}""", ",\"stereotypes\":[\"tracked\"]"));
        Assert.Equal(
        [
            ("part.json", "/revisions/lifecycles/1/lifecycle", "Lifecycle 'Machining' governs neither 'Part' nor an entity derived from it; remove the entry."),
            ("part.json", "/revisions/links/1/end", "Neither 'Part' nor an entity derived from it reaches end 'tools' of relation 'MadePartTools'; remove the entry."),
        ], Of(saved.Diagnostics, "MQ8312"));
    }

    [Fact]
    public async Task A_process_save_checks_the_entities_it_governs_and_those_it_stopped_governing()
    {
        var (h, store) = await OpenAsync(h =>
        {
            // Part repeats a level name, so a check of Part shows in a save's findings whatever else it finds.
            MemoWithLevels(h);
            h.Write(PartFile, Entity(Part, "Part", RecordType, Attr(Note, "note"),
                PartBlock.Replace("\"name\":\"minor\"", "\"name\":\"major\"", StringComparison.Ordinal), Abstract));
        });
        using var _ = h;
        await using var __ = store;

        // Approval binds note on Part, whose block clears it and names no Approval id.
        var bound = await SaveAsync(store, RevisionFamily.Approval, ApprovalFile(Note));
        Assert.Contains(bound.Diagnostics, d => d.Rule == "MQ8312" && Path.GetFileName(d.FilePath) == "part.json"
            && d.Message.Contains("lifecycle 'Approval'", StringComparison.Ordinal));

        // Approval moves to Document: Part is checked again, its finding gone.
        var moved = await SaveAsync(store, RevisionFamily.Approval, ApprovalFile(Note).Replace($"\"entity\":\"{Part}\"", $"\"entity\":\"{RevisionFamily.Document}\"", StringComparison.Ordinal));
        Assert.Contains(moved.Diagnostics, d => d.Rule == "MQ8301" && Path.GetFileName(d.FilePath) == "part.json");
        Assert.DoesNotContain(moved.Diagnostics, d => d.Rule == "MQ8312" && d.Message.Contains("lifecycle 'Approval'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_relation_save_checks_the_entities_at_its_ends()
    {
        var (h, store) = await OpenAsync(h => MemoWithLevels(h));
        using var _ = h;
        await using var __ = store;
        var targets = ModelValidator.TargetIds(store.Current!, new ValidationScope([PartDocuments], IncludeReferrers: false));
        Assert.Superset(new HashSet<string>([Part, PurchasedPart, MadePart, Sheet]), targets.ToHashSet());
        Assert.DoesNotContain(Memo, targets);

        var saved = await SaveAsync(store, PartDocuments, $$"""
            {"$schema":"../../.schema/v1/relation.json","kind":"relation","id":"{{PartDocuments}}","name":"PartDocuments","package":"{{RevisionFamily.Package}}",
             "ends":[{"id":"{{PartsEnd}}","entity":"{{Part}}","role":"parts","max":1},{"id":"{{DocumentsEnd}}","entity":"{{RevisionFamily.Document}}","role":"documents"}]}
            """);
        Assert.Contains(saved.Diagnostics, d => d.Rule == "MQ8308" && Path.GetFileName(d.FilePath) == "part.json");
    }

    [Fact]
    public async Task A_scalar_type_save_checks_the_blocks_whose_bound_attribute_it_types()
    {
        var (h, store) = await OpenAsync(h =>
        {
            MemoWithLevels(h);
            h.Write("model/scalar-types/code.json", $$"""{"kind":"scalar-type","id":"{{Code}}","name":"Code","package":"{{RevisionFamily.Package}}","base":"string"}""");
            h.Write("model/entities/record.json", Record().Replace($"\"name\":\"revision\",\"type\":\"string\",\"length\":40", $"\"name\":\"revision\",\"type\":{{\"ref\":\"{Code}\"}}", StringComparison.Ordinal));
        });
        using var _ = h;
        await using var __ = store;

        // Part and Memo bind revision, typed by Code; neither names Code.
        var saved = await SaveAsync(store, Code, $$"""{"kind":"scalar-type","id":"{{Code}}","name":"Code","package":"{{RevisionFamily.Package}}","base":"int32"}""");
        Assert.Equal(["memo.json", "part.json"], saved.Diagnostics.Where(d => d.Rule == "MQ8305").Select(d => Path.GetFileName(d.FilePath)).Distinct().Order());
    }

    // ---- today's models ----

    [Theory]
    [InlineData("billing")]
    [InlineData("process-basics")]
    public async Task Todays_models_keep_their_findings_and_do_no_revision_work(string fixture)
    {
        using var h = new LoaderHarness();
        h.CopyFixture("models", fixture);
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var model = store.Current!;
        var found = (await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

        Assert.DoesNotContain(found, d => Rules.Contains(d.Rule) || d.Message.Contains("revision", StringComparison.OrdinalIgnoreCase));
        Assert.False(RevisionCoverage.Of(model).Any);
        // No save widens its scope for revisions.
        Assert.All(model.Documents, d => Assert.Empty(ModelValidator.RevisionPeers(model, d.Element)));
    }
}
