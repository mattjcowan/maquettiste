using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;
using Maquettiste.Engine.Validation;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The rules of relations with several types at an end, part 1 (several-types-design.md decisions 2 to 12 and 21 to 23, section 6): MQ3041,
/// MQ3042, MQ3043, MQ2003 and MQ9211 on entries, MQ2020 on entity entries only, MQ3009 holders, MQ3016, MQ4009, MQ4115 and MQ7105,
/// their failing and passing cases, the stale findings of MQ4115, and today's models keeping their findings. Over <see cref="SeveralTypesKit"/>.
/// </summary>
public sealed class RelationTypesRuleTests
{
    private const string Main = "01JDBS00000000000000000901";
    private static readonly string[] NewRules = ["MQ3041", "MQ3042", "MQ3043", "MQ4115"];

    private static string Database(string extra = "") =>
        $$"""{"kind":"database","id":"{{Main}}","name":"main","dialect":"postgresql","byConvention":"none"{{extra}}}""";

    private static string RelationWith(string far, string id = "01JRXN00000000000000000990", string kind = "association", string farMax = "1",
        string navigation = "", string nearMax = "\"*\"") =>
        RelationFile(id, "Link", "01JEND00000000000000000991", "comment", "", "01JEND00000000000000000992", far, "target", navigation, farMax, kind, nearMax);

    [Fact]
    public async Task The_model_validates_without_errors_and_the_new_rules_stay_silent()
    {
        var found = await ValidateAsync();
        Assert.DoesNotContain(found, d => d.Severity == DiagnosticSeverity.Error && (d.FilePath ?? "").Contains("/model/", StringComparison.Ordinal)
            && !(d.FilePath ?? "").Contains("order", StringComparison.Ordinal));
        Assert.DoesNotContain(found, d => NewRules.Contains(d.Rule));
    }

    [Fact]
    public async Task Todays_models_keep_their_findings()
    {
        // The billing fixture has no end with several types: none of the new rules or cases, and the helper stays empty.
        using var h = new LoaderHarness();
        h.CopyFixture("models", "billing");
        await using var store = new ModelStore(h.Options, h.Services() with { Validator = EngineServices.Create(h.Options).Validator });
        await store.LoadAsync(Ct);
        var found = await ActorKit.ValidateAsync(h, store.Current!);
        Assert.DoesNotContain(found, d => NewRules.Contains(d.Rule) || d.Message.Contains("several types", StringComparison.Ordinal));
        Assert.False(EndCoverage.Of(store.Current!).Any);
    }

    // ---- MQ3041 ----

    [Fact]
    public async Task MQ3041_refuses_an_n_ary_end_and_an_any_entity_child_but_no_longer_both_ends()
    {
        var nAry = await ValidateAsync(h => h.Write("model/relations/link.json", $$"""
            {"kind":"relation","id":"01JRXN00000000000000000990","name":"Link","package":"{{Sales}}","relationKind":"n-ary","ends":[
              {"id":"01JEND00000000000000000991","entity":"{{Comment}}","role":"comment"},{"id":"01JEND00000000000000000992","entity":"{{Tool}}","role":"tool"},
              {"id":"01JEND00000000000000000993","types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"}],"role":"target"}]}
            """));
        Assert.Equal([("/ends/2", "Relation 'Link' is n-ary, so its end 'target' can't have several types; give it one entity.")], Of(nAry, "MQ3041"));

        var both = await ValidateAsync(h => h.Write("model/relations/link.json", $$"""
            {"kind":"relation","id":"01JRXN00000000000000000990","name":"Link","package":"{{Sales}}","ends":[
              {"id":"01JEND00000000000000000991","types":[{"entity":"{{Comment}}"},{"entity":"{{Tool}}"}],"role":"source","max":"*"},
              {"id":"01JEND00000000000000000992","anyEntity":true,"role":"target","max":1}]}
            """));
        // Part 2 lifts the one-end limit (several-types-design.md decision 53, section 19.12): several types or any entity at both ends.
        Assert.Empty(Of(both, "MQ3041"));
        var anyToAny = await ValidateAsync(h => h.Write("model/relations/link.json", $$"""
            {"kind":"relation","id":"01JRXN00000000000000000990","name":"Link","package":"{{Sales}}","ends":[
              {"id":"01JEND00000000000000000991","anyEntity":true,"role":"source","max":"*"},
              {"id":"01JEND00000000000000000992","anyEntity":true,"role":"target","max":"*"}]}
            """));
        Assert.Empty(Of(anyToAny, "MQ3041"));
        var composedBoth = await ValidateAsync(h => h.Write("model/relations/link.json", $$"""
            {"kind":"relation","id":"01JRXN00000000000000000990","name":"Link","package":"{{Sales}}","relationKind":"composition","ends":[
              {"id":"01JEND00000000000000000991","types":[{"entity":"{{Comment}}"},{"entity":"{{Tool}}"}],"role":"owner","max":1},
              {"id":"01JEND00000000000000000992","types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"}],"role":"part","max":"*"}]}
            """));
        Assert.Empty(Of(composedBoth, "MQ3041"));

        // An n-ary relation still refuses several types at each such end, two of them included.
        var nAryTwo = await ValidateAsync(h => h.Write("model/relations/link.json", $$"""
            {"kind":"relation","id":"01JRXN00000000000000000990","name":"Link","package":"{{Sales}}","relationKind":"n-ary","ends":[
              {"id":"01JEND00000000000000000991","anyEntity":true,"role":"comment"},{"id":"01JEND00000000000000000992","entity":"{{Tool}}","role":"tool"},
              {"id":"01JEND00000000000000000993","types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"}],"role":"target"}]}
            """));
        Assert.Equal(["/ends/0", "/ends/2"], Of(nAryTwo, "MQ3041").Select(f => f.Item1));

        var child = await ValidateAsync(h => h.Write("model/relations/link.json", RelationWith(""" "anyEntity":true """, kind: "composition", farMax: "\"*\"", nearMax: "1")));
        Assert.Equal([("/ends/1/anyEntity", "End 'target' is the part in composition 'Link', so it can't be any entity; list the types it may be.")], Of(child, "MQ3041"));

        // Allowed: one end of a two-ended association, aggregation or composition, the child end of a composition listing types.
        var fine = await ValidateAsync(h => h.Write("model/relations/link.json",
            RelationWith($$""" "types":[{"entity":"{{Tool}}"},{"stereotype":"approvable"}] """, kind: "composition", farMax: "\"*\"", nearMax: "1")));
        Assert.Empty(Of(fine, "MQ3041"));
    }

    // ---- MQ3042, MQ3043 ----

    [Fact]
    public async Task MQ3042_offers_entity_for_a_one_entity_list_which_is_a_one_type_end()
    {
        var found = await ValidateAsync(h => h.Write("model/relations/link.json", RelationWith($$""" "types":[{"entity":"{{Tool}}"}] """)));
        Assert.Equal([("/ends/1/types", "End 'target' lists only entity 'Tool' in types; write it as entity.")], Of(found, "MQ3042"));
        Assert.Contains(found, d => d.Rule == "MQ3042" && d.Severity == DiagnosticSeverity.Warning);

        // Decision 2: a one-entity list is a one-type end (no MQ4115 or helper entry), a list of a base and its derived entity is not.
        var (h2, store) = await OpenAsync(h => h.Write("model/relations/link.json", RelationWith($$""" "types":[{"entity":"{{Tool}}"}] """)));
        using var _ = h2;
        await using var __ = store;
        Assert.DoesNotContain("01JEND00000000000000000992", EndCoverage.Of(store.Current!).Ends);
        Assert.True(new RelationEnd { Id = "x", Role = "r", Types = [new() { Entity = Part }, new() { Entity = PurchasedPart }] }.HasSeveralTypes);
        Assert.True(new RelationEnd { Id = "x", Role = "r", Types = [new() { Stereotype = "approvable" }] }.HasSeveralTypes);
    }

    [Fact]
    public async Task MQ3043_warns_of_an_entry_another_entry_covers()
    {
        var found = await ValidateAsync(h => h.Write("model/relations/link.json",
            RelationWith($$""" "types":[{"entity":"{{Part}}"},{"entity":"{{PurchasedPart}}"},{"stereotype":"approvable"},{"entity":"{{Document}}"},{"entity":"{{Memo}}"},{"entity":"{{Tool}}"}] """)));
        Assert.Equal(
        [
            ("/ends/1/types/1", "End 'target' lists entity 'PurchasedPart', which entity 'Part' already covers, since it derives from it; remove the entry."),
            ("/ends/1/types/3", "End 'target' lists entity 'Document', which carries stereotype 'approvable', also listed; remove the entry."),
            ("/ends/1/types/4", "End 'target' lists entity 'Memo', which entity 'Document' already covers, since it derives from it; remove the entry."),
        ], Of(found, "MQ3043"));
        Assert.All(found.Where(d => d.Rule == "MQ3043"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));

        var memo = await ValidateAsync(h => h.Write("model/relations/link.json", RelationWith($$""" "types":[{"stereotype":"approvable"},{"entity":"{{Memo}}"}] """)));
        Assert.Equal([("/ends/1/types/1", "End 'target' lists entity 'Memo', which stereotype 'approvable' already covers, since it derives from entity 'Document', which carries it; remove the entry.")],
            Of(memo, "MQ3043"));
    }

    // ---- MQ2003, MQ9211, MQ2020 ----

    [Fact]
    public async Task MQ2003_and_MQ9211_judge_an_entrys_stereotype()
    {
        var found = await ValidateAsync(h => h.Write("model/relations/link.json",
            RelationWith($$""" "types":[{"entity":"{{Tool}}"},{"stereotype":"nothing"},{"stereotype":"table-only"}] """)));
        Assert.Equal([("/ends/1/types/1/stereotype", "Stereotype 'nothing' is not defined in model/vocabularies/stereotypes.")], Of(found, "MQ2003", "/relations/link"));
        Assert.Equal([("/ends/1/types/2/stereotype", "Type 3 of end 'target' names stereotype 'table-only', which applies to table, not to entities, so no entity can carry it; name a stereotype entities carry, or an entity.")],
            Of(found, "MQ9211", "/relations/link"));
    }

    [Fact]
    public async Task MQ2020_applies_to_entity_entries_only()
    {
        const string Tools = "01JPKG00000000000000000902";
        var found = await ValidateAsync(h =>
        {
            h.Write("model/packages/tools.json", $$"""{"kind":"package","id":"{{Tools}}","name":"Tools"}""");
            h.Write("model/packages/sales.json", $$"""{"kind":"package","id":"{{Sales}}","name":"Sales","dependsOn":[]}""");
            h.Write("model/entities/tool.json", $$"""{"kind":"entity","id":"{{Tool}}","name":"Tool","package":"{{Tools}}"}""");
            h.Write("model/entities/memo.json", $$"""{"kind":"entity","id":"{{Memo}}","name":"Memo","package":"{{Tools}}","base":"{{Document}}"}""");
            h.Write("model/relations/link.json", RelationWith($$""" "types":[{"entity":"{{Tool}}"},{"stereotype":"approvable"}] """));
            h.Write("model/relations/any.json", RelationFile("01JRXN00000000000000000991", "Any", "01JEND00000000000000000981", "comment", "", "01JEND00000000000000000982",
                """ "anyEntity":true """, "target", "", "1"));
        });

        // The entity entry naming Tool is reported; the stereotype entry, whose carrier Memo derives into Tools, and anyEntity are not.
        Assert.Equal(["/ends/1/types/0/entity"], Of(found, "MQ2020", "/relations/link").Select(f => f.Pointer));
        Assert.Empty(Of(found, "MQ2020", "/relations/any"));
    }

    // ---- MQ3009, MQ3016 ----

    [Fact]
    public async Task MQ3009_reads_the_holders_from_the_listed_types_carriers_derived_entities_and_any_entity()
    {
        // Listed types and their derived entities: PurchasedPart's own attribute named like the navigation.
        var listed = await ValidateAsync(h => h.Write("model/entities/purchased-part.json",
            EntityFile(PurchasedPart, "PurchasedPart", Part, attributes: ""","attributes":[{"id":"01JATT00000000000000000931","name":"comments","type":"string"}]""")));
        Assert.Equal([("/ends/0/navigation", "Navigation 'comments' on entity 'Part' collides with attribute 'comments' of derived entity 'PurchasedPart', which inherits the navigation.")],
            Of(listed, "MQ3009", "/relations/attached-to"));

        // A carrier of a listed stereotype: Document holds concernedBy.
        var carrier = await ValidateAsync(h => h.Write("model/entities/document.json", EntityFile(Document, "Document", stereotype: "approvable",
            attributes: ""","attributes":[{"id":"01JATT00000000000000000953","name":"concernedBy","type":"string"}]""")));
        Assert.Equal([("/ends/0/navigation", "Navigation 'concernedBy' collides with attribute 'concernedBy' of entity 'Document'.")], Of(carrier, "MQ3009", "/relations/concerns"));

        // Any entity: every entity holds the navigation, so Tool's attribute collides.
        var any = await ValidateAsync(h =>
        {
            h.Write("model/entities/tool.json", EntityFile(Tool, "Tool", attributes: ""","attributes":[{"id":"01JATT00000000000000000973","name":"mentionedIn","type":"string"}]"""));
            h.Write("model/relations/any.json", RelationFile("01JRXN00000000000000000991", "Mentions", "01JEND00000000000000000981", "comment", "mentionedIn",
                "01JEND00000000000000000982", """ "anyEntity":true """, "mentions", "", "1"));
        });
        Assert.Equal([("/ends/0/navigation", "Navigation 'mentionedIn' collides with attribute 'mentionedIn' of entity 'Tool'.")], Of(any, "MQ3009", "/relations/any"));

        // The model as it is: no collision.
        Assert.Empty(Of(await ValidateAsync(), "MQ3009"));
    }

    [Fact]
    public async Task MQ3016_counts_an_owner_end_with_several_types_once_and_checks_each_covered_child()
    {
        // A note owned by one part, one assembly or one document: one composition, no MQ3016.
        static string Owns(string id, string name, string owner, string child, string ownerEnd, string childEnd) =>
            $$"""{"kind":"relation","id":"{{id}}","name":"{{name}}","package":"{{Sales}}","relationKind":"composition","ends":[{"id":"{{ownerEnd}}",{{owner}},"role":"owner","max":1},{"id":"{{childEnd}}",{{child}},"role":"part","max":"*"}]}""";
        var once = await ValidateAsync(h => h.Write("model/relations/holds.json",
            Owns("01JRXN00000000000000000980", "Holds", $$""" "types":[{"entity":"{{Part}}"},{"entity":"{{Assembly}}"},{"stereotype":"approvable"}] """, $$""" "entity":"{{Comment}}" """,
                "01JEND00000000000000000971", "01JEND00000000000000000972")));
        Assert.Empty(Of(once, "MQ3016"));

        // A child end listing types, one of which (by its carrier Memo, derived from Document) another composition already owns.
        var twice = await ValidateAsync(h =>
        {
            h.Write("model/relations/a-holds.json", Owns("01JRXN00000000000000000980", "AHolds", $$""" "entity":"{{Tool}}" """,
                $$""" "types":[{"entity":"{{Part}}"},{"stereotype":"approvable"}] """, "01JEND00000000000000000971", "01JEND00000000000000000972"));
            h.Write("model/relations/b-holds.json", Owns("01JRXN00000000000000000981", "BHolds", $$""" "entity":"{{Comment}}" """, $$""" "entity":"{{Memo}}" """,
                "01JEND00000000000000000973", "01JEND00000000000000000974"));
        });
        Assert.Equal([("/ends/1/entity", "Entity 'Memo' is already owned through composition .maquettiste/model/relations/a-holds.json; a composition child has one owner.")], Of(twice, "MQ3016", "/relations/b-holds"));
        Assert.Empty(Of(twice, "MQ3016", "/relations/a-holds"));

        // The other way round, the end with several types reports at the entry that covers the child.
        var reversed = await ValidateAsync(h =>
        {
            h.Write("model/relations/b-holds.json", Owns("01JRXN00000000000000000980", "BHolds", $$""" "entity":"{{Tool}}" """,
                $$""" "types":[{"entity":"{{Part}}"},{"stereotype":"approvable"}] """, "01JEND00000000000000000971", "01JEND00000000000000000972"));
            h.Write("model/relations/a-holds.json", Owns("01JRXN00000000000000000981", "AHolds", $$""" "entity":"{{Comment}}" """, $$""" "entity":"{{Memo}}" """,
                "01JEND00000000000000000973", "01JEND00000000000000000974"));
        });
        Assert.Equal([("/ends/1/types/1", "Entity 'Memo' is already owned through composition .maquettiste/model/relations/a-holds.json; a composition child has one owner.")], Of(reversed, "MQ3016", "/relations/b-holds"));
    }

    // ---- MQ4009, MQ4115, MQ7105, and binding fields through such an end ----

    [Fact]
    public async Task MQ4009_refuses_the_keys_that_shape_engine_made_storage_on_a_mapping_of_such_a_relation()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write("model/databases/main/database.json", Database());
            h.Write("model/mappings/attached.json", $$"""{"kind":"mapping","id":"01JMAP00000000000000000901","name":"attached in main","database":"{{Main}}","relation":"{{AttachedTo}}","shape":"junction","foreignKeyEnd":"{{AttachedEnd}}"}""");
        });
        Assert.Equal(
        [
            ("/shape", "Relation 'AttachedTo' has an end with several types, so a mapping of it doesn't take 'shape'; its 'ends' entries say how it is stored. Remove it."),
            ("/foreignKeyEnd", "Relation 'AttachedTo' has an end with several types, so a mapping of it doesn't take 'foreignKeyEnd'; its 'ends' entries say how it is stored. Remove it."),
        ], Of(found, "MQ4009", "/mappings/attached"));
    }

    // An entity is placed in a database by a binding there: a table with an id column, and the entity bound to it by its key.
    private const string Comments = "01JTAB00000000000000000951", Parts = "01JTAB00000000000000000952", Documents = "01JTAB00000000000000000953";
    private const string CommentKey = "01JATT00000000000000000981", ItemKey = "01JATT00000000000000000911", DocumentKey = "01JATT00000000000000000951";

    private static void WriteTable(LoaderHarness h, string id, string name) =>
        h.Write($"model/databases/main/tables/{name}.json", $$"""
            {"kind":"table","id":"{{id}}","name":"{{name}}","database":"{{Main}}","columns":[{"id":"01JCMN{{id[6..]}}","name":"id","type":"uuid"}]}
            """);

    private static string BoundTo(string entityJson, string table, string key) =>
        entityJson.TrimEnd()[..^1] + $$""","bindings":[{"id":"01JBND{{table[6..]}}","database":"{{Main}}","source":"{{table}}","fields":[{"attribute":"{{key}}","column":"id"}]}]}""";

    private static void Place(LoaderHarness h, string file, string table, string tableName, string key)
    {
        WriteTable(h, table, tableName);
        h.Write($"model/entities/{file}.json", BoundTo(h.Read($"model/entities/{file}.json"), table, key));
    }

    [Fact]
    public async Task MQ4115_says_a_database_stores_nothing_where_every_end_has_a_placed_type()
    {
        // Comment and Part are bound to main: each relation with an end of several types is reported, at that end, as info; the one-type
        // OnPart is stored as today.
        var found = await ValidateAsync(h =>
        {
            h.Write("model/databases/main/database.json", Database());
            Place(h, "comment", Comments, "comments", CommentKey);
            Place(h, "part", Parts, "parts", ItemKey);
        });
        Assert.Equal([("/ends/1", "Relation 'AttachedTo' has an end with several types, so database 'main' stores nothing for it.")], Of(found, "MQ4115", "/relations/attached-to"));
        Assert.Single(Of(found, "MQ4115", "/relations/concerns"));
        Assert.Single(Of(found, "MQ4115", "/relations/tagged"));
        Assert.Empty(Of(found, "MQ4115", "/relations/on-part"));
        Assert.All(found.Where(d => d.Rule == "MQ4115"), d => Assert.Equal(DiagnosticSeverity.Info, d.Severity));

        // With no bound type at an end (neither Part nor Assembly bound), nothing; Concerns still has Document, which carries approvable.
        var unplaced = await ValidateAsync(h =>
        {
            h.Write("model/databases/main/database.json", Database());
            Place(h, "comment", Comments, "comments", CommentKey);
            Place(h, "document", Documents, "documents", DocumentKey);
        });
        Assert.Empty(Of(unplaced, "MQ4115", "/relations/attached-to"));
        Assert.Empty(Of(unplaced, "MQ4115", "/relations/tagged"));
        Assert.Single(Of(unplaced, "MQ4115", "/relations/concerns"));
    }

    [Fact]
    public async Task A_binding_field_naming_an_end_with_several_types_resolves()
    {
        const string Table = "01JTAB00000000000000000901";
        var found = await ValidateAsync(h =>
        {
            h.Write("model/databases/main/database.json", Database());
            h.Write("model/databases/main/tables/comments.json", $$"""
                {"kind":"table","id":"{{Table}}","name":"comments","database":"{{Main}}",
                 "columns":[{"id":"01JCMN00000000000000000901","name":"id","type":"uuid"},{"id":"01JCMN00000000000000000902","name":"attached_id","type":"uuid"}]}
                """);
            h.Write("model/entities/comment.json", EntityFile(Comment, "Comment", attributes: $$"""
                ,"key":{"attributes":["01JATT00000000000000000981"]},"attributes":[{"id":"01JATT00000000000000000981","name":"id","type":"uuid"},
                 {"id":"{{TextAttribute}}","name":"text","type":"string","length":40},{"id":"{{Note2}}","name":"note2","type":"string","length":40}],
                "bindings":[{"id":"01JBND00000000000000000901","database":"{{Main}}","source":"{{Table}}","fields":[{"attribute":"01JATT00000000000000000981","column":"id"},{"attribute":"{{AttachedEnd}}","column":"attached_id"}]}]
                """));
        });
        // Part 2: the binding field resolves (several-types-design.md decision 46): Comment holds the to-one end, which has several types.
        Assert.DoesNotContain(found, d => d.Rule == "MQ4045" && (d.FilePath ?? "").Contains("/entities/comment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_binding_field_through_such_a_relation_from_the_one_type_side_resolves()
    {
        // Filed: a comment (to-one) filed under tools or documents. Tool's binding maps the comment end, a to-one end Tool holds through its
        // type.
        const string Tools = "01JTAB00000000000000000911", Parts = "01JTAB00000000000000000912", Comments = "01JTAB00000000000000000913";
        const string Filed = "01JRXN00000000000000000991", FiledComment = "01JEND00000000000000000995", FiledUnder = "01JEND00000000000000000996";
        var found = await ValidateAsync(h =>
        {
            h.Write("model/databases/main/database.json", Database());
            foreach (var (id, name, second) in new[] { (Tools, "tools", "comment_id"), (Parts, "parts", "note"), (Comments, "comments", "text") })
            {
                h.Write($"model/databases/main/tables/{name}.json", $$"""
                    {"kind":"table","id":"{{id}}","name":"{{name}}","database":"{{Main}}",
                     "columns":[{"id":"{{id[..^3]}}A{{id[^2..]}}","name":"id","type":"uuid"},{"id":"{{id[..^3]}}B{{id[^2..]}}","name":"{{second}}","type":"uuid"}]}
                    """);
            }

            h.Write("model/relations/filed.json", RelationFile(Filed, "Filed", FiledComment, "comment", "comment", FiledUnder,
                $$""" "types":[{"entity":"{{Tool}}"},{"entity":"{{Document}}"}] """, "filed", "filedUnder", "\"*\"", nearMax: "1"));
            h.Write("model/entities/tool.json", EntityFile(Tool, "Tool", attributes: $$"""
                ,"key":{"attributes":["01JATT00000000000000000971"]},"attributes":[{"id":"01JATT00000000000000000971","name":"id","type":"uuid"}],
                "bindings":[{"id":"01JBND00000000000000000911","database":"{{Main}}","source":"{{Tools}}","fields":[{"attribute":"01JATT00000000000000000971","column":"id"},{"attribute":"{{FiledComment}}","column":"comment_id"}]}]
                """));
        });
        // Part 2: Tool's binding field resolves, as a key Tool holds through its type (way 1, decision 46).
        Assert.DoesNotContain(found, d => d.Rule == "MQ4045" && (d.FilePath ?? "").Contains("/entities/tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seed_column_names_leave_out_every_end_of_such_a_relation()
    {
        // A seed's bare column names become ids (SeedInput.Names): the to-one end of a one-type relation does; a to-one end with several types
        // is not offered, so it stays an unknown name (the other end of such a relation has no one entity to be offered on).
        var (h, store) = await OpenAsync();
        using var _ = h;
        await using var __ = store;
        async Task<List<string?>> UnknownAsync(string target, params string[] columns)
        {
            var seed = $$"""{"kind":"seed","name":"S","target":"{{target}}","columns":[{{string.Join(",", columns.Select(c => "\"" + c + "\""))}}],"rows":[]}""";
            var result = await store.CreateAsync(Encoding.UTF8.GetBytes(seed), ChangeSource.Editor, Ct);
            return [.. result.Diagnostics.Where(d => d.Rule == "MQ1002").Select(d => d.JsonPointer)];
        }

        Assert.Equal(["/columns/2"], await UnknownAsync(Comment, "id", "onPart", "attachedTo"));
    }

    [Fact]
    public async Task MQ7105_refuses_a_seed_column_and_a_relation_seed_of_such_a_relation()
    {
        var found = await ValidateAsync(h =>
        {
            h.Write("model/seeds/comments.json", $$"""
                {"kind":"seed","id":"01JSED00000000000000000901","name":"Notes","target":"{{Comment}}","columns":["01JATT00000000000000000981","{{AttachedEnd}}","{{OnPartEnd}}"],"rows":[]}
                """);
            h.Write("model/seeds/parts.json", $$"""
                {"kind":"seed","id":"01JSED00000000000000000902","name":"Parts","target":"{{Part}}","columns":["01JATT00000000000000000911","01JEND00000000000000000921"],"rows":[]}
                """);
            h.Write("model/seeds/attached.json", $$"""
                {"kind":"seed","id":"01JSED00000000000000000903","name":"Attached","target":"{{AttachedTo}}","columns":["{{CommentsEnd}}","{{AttachedEnd}}"],"rows":[]}
                """);
        });
        Assert.Equal([("/columns/1", "Seed 'Notes' has a column for end 'attachedTo', which leads to several types; seeds can't hold such links yet.")], Of(found, "MQ7105", "/seeds/comments"));
        Assert.Equal([("/columns/1", "Seed 'Parts' has a column for end 'concernedBy' of relation 'Concerns', which has an end with several types; seeds can't hold such links yet.")],
            Of(found, "MQ7105", "/seeds/parts"));
        Assert.Equal([("/target", "Seed 'Attached' holds rows of relation 'AttachedTo', which has an end with several types; seeds can't hold such links yet.")], Of(found, "MQ7105", "/seeds/attached"));
        Assert.Empty(Of(found, "MQ7005", "/seeds/"));
    }

    // ---- stale findings ----

    [Fact]
    public async Task MQ4115_comes_and_goes_with_binding_database_and_settings_saves()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write("model/databases/main/database.json", Database());
            WriteTable(h, Comments, "comments");
            WriteTable(h, Parts, "parts");
            WriteTable(h, Documents, "documents");
        });
        using var _ = h;
        await using var __ = store;
        static bool Reported(SaveResult saved, string relation) =>
            saved.Diagnostics.Any(d => d.Rule == "MQ4115" && d.ElementId is { } id && id.StartsWith("01JEND", StringComparison.Ordinal)
                && (d.FilePath ?? "").Contains(relation, StringComparison.Ordinal));
        Task<SaveResult> SaveEntityAsync(string id, string json) =>
            store.SaveAsync(id, Encoding.UTF8.GetBytes(json), store.Current!.GetDocument(id)!.Hash, ChangeSource.Editor, Ct);

        // Bindings place Comment and a carrier (Document): Concerns now has a placed type at each end.
        var comment = await SaveEntityAsync(Comment, BoundTo(h.Read("model/entities/comment.json"), Comments, CommentKey));
        Assert.Equal(SaveOutcome.Saved, comment.Outcome);
        Assert.False(Reported(comment, "/relations/concerns"));
        var documentJson = h.Read("model/entities/document.json");
        var document = await SaveEntityAsync(Document, BoundTo(documentJson, Documents, DocumentKey));
        Assert.Equal(SaveOutcome.Saved, document.Outcome);
        Assert.True(Reported(document, "/relations/concerns"), string.Join(" | ", document.Diagnostics.Select(d => d.Rule + " " + d.FilePath)));

        // The binding removed: it goes.
        var unbound = await SaveEntityAsync(Document, documentJson);
        Assert.Equal(SaveOutcome.Saved, unbound.Outcome);
        Assert.False(Reported(unbound, "/relations/concerns"));

        // Part bound: AttachedTo and Tagged are reported on the entity save, and on a database save, which checks every such relation.
        var part = await SaveEntityAsync(Part, BoundTo(h.Read("model/entities/part.json"), Parts, ItemKey));
        Assert.Equal(SaveOutcome.Saved, part.Outcome);
        Assert.True(Reported(part, "/relations/attached-to"));
        Assert.True(Reported(part, "/relations/tagged"));
        var database = JsonNode.Parse(h.Read("model/databases/main/database.json"))!;
        database["description"] = "The main database.";
        var saved = await store.SaveAsync(Main, Encoding.UTF8.GetBytes(database.ToJsonString()), store.Current!.GetDocument(Main)!.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.True(Reported(saved, "/relations/attached-to"));
        Assert.True(Reported(saved, "/relations/tagged"));

        // Settings that make the info an error refuse the save, which the whole-model check of a settings save finds.
        var text = JsonNode.Parse(h.Read("maquettiste.json"))!;
        text["validation"] = JsonNode.Parse("""{"rules":{"MQ4115":"error"}}""");
        var settings = await store.SaveSettingsAsync(Encoding.UTF8.GetBytes(text.ToJsonString()), store.Current!.SettingsHash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Invalid, settings.Outcome);
        Assert.Contains(settings.Diagnostics, d => d.Rule == "MQ4115");
    }

    [Fact]
    public async Task A_carrier_gaining_the_stereotype_checks_the_relations_naming_it_again()
    {
        var (h, store) = await OpenAsync(h =>
        {
            h.Write("model/databases/main/database.json", Database());
            h.Write("model/mappings/on-part.json", $$"""{"kind":"mapping","id":"01JMAP00000000000000000910","name":"on part in main","database":"{{Main}}","relation":"{{OnPart}}"}""");
            h.Write("model/relations/concerns.json", RelationFile(Concerns, "Concerns", "01JEND00000000000000000921", "concernedBy",
                "toolNo", ConcernsEnd, $$""" "types":[{"entity":"{{Part}}"},{"stereotype":"approvable"}] """, "concerns", "concerns", "1"));
        });
        using var _ = h;
        await using var __ = store;

        // Tool carries approvable: the navigation toolNo now lands on Tool, which has an attribute of that name (MQ3009 on the relation).
        var tool = await store.SaveAsync(Tool, Encoding.UTF8.GetBytes(EntityFile(Tool, "Tool", stereotype: "approvable",
            attributes: ""","key":{"attributes":["01JATT00000000000000000971"]},"attributes":[{"id":"01JATT00000000000000000971","name":"id","type":"uuid"},{"id":"01JATT00000000000000000972","name":"toolNo","type":"string"}]""")),
            store.Current!.GetDocument(Tool)!.Hash, ChangeSource.Editor, Ct);
        Assert.Contains(tool.Diagnostics, d => d.Rule == "MQ3009" && (d.FilePath ?? "").Contains("/relations/concerns", StringComparison.Ordinal));

        // What a save checks again beyond its referrers (decision 12): a stereotype the relations naming it; an entity those and the relations
        // whose end with several types covers it; a mapping or database every relation with such an end.
        IEnumerable<string> Relations(string id) => ModelValidator.TargetIds(store.Current!, new ValidationScope([id], IncludeReferrers: false))
            .Where(t => t.StartsWith("01JRXN", StringComparison.Ordinal)).Order(StringComparer.Ordinal);
        Assert.Equal([Concerns], Relations(Approvable));
        Assert.Equal([Concerns], Relations(Tool));
        Assert.Equal([AttachedTo, Concerns, Tagged], Relations(PurchasedPart));
        Assert.Superset(new HashSet<string>([AttachedTo, Concerns, Tagged]), Relations("01JMAP00000000000000000910").ToHashSet());
        Assert.Superset(new HashSet<string>([AttachedTo, Concerns, Tagged]), Relations(Main).ToHashSet());
    }
}
