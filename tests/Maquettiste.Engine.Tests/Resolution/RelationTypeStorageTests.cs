using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Relations with several types, part 2: storage in the team's designed tables (several-types-design.md section 19). A small invented model:
/// Part, Assembly, Comment and Folder bound to designed tables of database 'main'; AttachedTo (a comment attached to one part or assembly)
/// and Filed (a folder holds parts and assemblies). Each test writes one relation mapping and reads the resolved members, the run's findings
/// (MQ4117, MQ4118, MQ4120, MQ4051) and the database's storage notes.
/// </summary>
public sealed partial class RelationTypeStorageTests
{
    private const string Main = "01JDBS00000000000000000951";
    private const string P = "01JST2", Pt = "01JST3", Ft = "01JST4";
    private static string N(string prefix, int n) => prefix + n.ToString("D20", CultureInfo.InvariantCulture);

    // Entities, keys, relations and ends.
    private static readonly string SPart = N(P, 1), SAssembly = N(P, 2), SComment = N(P, 3), SFolder = N(P, 4);
    private static readonly string Attached = N(P, 11), CommentEnd = N(P, 12), AttachedToEnd = N(P, 13), NoteAttribute = N(P, 14);
    private static readonly string Filed = N(P, 21), FolderEnd = N(P, 22), ItemsEnd = N(P, 23);

    // Tables, their columns (table id + 100 + n) and keys (table id + 200 + n).
    private static readonly string Folders = N(Pt, 1), Parts = N(Pt, 2), Assemblies = N(Pt, 3), Comments = N(Pt, 4), Links = N(Pt, 5);
    private static string Col(string table, int n) => N(Ft, (int.Parse(table[^2..], CultureInfo.InvariantCulture) * 100) + n);
    private static string Fk(string table, int n) => N(Ft, 5000 + (int.Parse(table[^2..], CultureInfo.InvariantCulture) * 10) + n);

    private static string Entity(string id, string name, int key) =>
        EntityFile(id, name, attributes: $$""","key":{"attributes":["{{N(P, 100 + key)}}"]},"attributes":[{"id":"{{N(P, 100 + key)}}","name":"id","type":"uuid"}]""");

    private static string Column(string table, int n, string name, string type = "uuid", int? length = null, bool nullable = true) =>
        $$"""{"id":"{{Col(table, n)}}","name":"{{name}}","type":"{{type}}"{{(length is { } l ? ",\"length\":" + l.ToString(CultureInfo.InvariantCulture) : "")}}{{(nullable ? "" : ",\"nullable\":false")}}}""";

    private static string ForeignKey(string table, int n, string name, int column, string target) =>
        $$"""{"id":"{{Fk(table, n)}}","name":"{{name}}","columns":["{{Col(table, column)}}"],"referencesTable":"{{target}}","referencesColumns":["{{Col(target, 1)}}"]}""";

    private static string Table(string id, string name, string columns, string foreignKeys = "", string extra = "") =>
        $$"""{"kind":"table","id":"{{id}}","name":"{{name}}","database":"{{Main}}","columns":[{{columns}}],"primaryKey":{"columns":["{{Col(id, 1)}}"]},"foreignKeys":[{{foreignKeys}}]{{extra}}}""";

    private static string EntityMapping(int n, string entity, string table, string extra = "") =>
        $$"""{"kind":"mapping","id":"{{N(P, 900 + n)}}","name":"m{{n.ToString(CultureInfo.InvariantCulture)}}","database":"{{Main}}","entity":"{{entity}}","table":"{{table}}"{{extra}}}""";

    private static string RelationMapping(string relation, string body) =>
        $$"""{"kind":"mapping","id":"{{N(P, 990)}}","name":"storage","database":"{{Main}}","relation":"{{relation}}"{{body}}}""";

    /// <summary>The model, with <paramref name="prepare"/> writing the relation mapping and any change.</summary>
    private static Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(Action<LoaderHarness> prepare, bool bindComment = false,
        string attachedAttributes = "", string assemblyMapping = "") =>
        ActorKit.OpenAsync(h =>
        {
            h.Write("model/databases/main/database.json", $$"""{"kind":"database","id":"{{Main}}","name":"main","dialect":"postgresql"}""");
            h.Write("model/entities/part.json", Entity(SPart, "Part", 1));
            h.Write("model/entities/assembly.json", Entity(SAssembly, "Assembly", 2));
            h.Write("model/entities/folder.json", Entity(SFolder, "Folder", 4));
            h.Write("model/entities/comment.json", bindComment
                ? EntityFile(SComment, "Comment", attributes: $$""","key":{"attributes":["{{N(P, 103)}}"]},"attributes":[{"id":"{{N(P, 103)}}","name":"id","type":"uuid"}],"""
                    + $$""" "bindings":[{"id":"{{N(P, 800)}}","database":"{{Main}}","source":"{{Comments}}","fields":[{"attribute":"{{N(P, 103)}}","column":"id"},"""
                    + $$"""{"attribute":"{{AttachedToEnd}}.type","column":"attached_type"},{"attribute":"{{AttachedToEnd}}","column":"part_id"}],"""
                    + """ "columns":[{"column":"attached_id","status":"ignored"},{"column":"assembly_id","status":"ignored"}]}]""")
                : Entity(SComment, "Comment", 3));
            h.Write("model/relations/attached-to.json", $$"""
                {"kind":"relation","id":"{{Attached}}","name":"AttachedTo","package":"{{Sales}}","ends":[
                 {"id":"{{CommentEnd}}","entity":"{{SComment}}","role":"comment","navigation":"comments","max":"*"},
                 {"id":"{{AttachedToEnd}}","types":[{"entity":"{{SPart}}"},{"entity":"{{SAssembly}}"}],"role":"attachedTo","navigation":"attachedTo","max":1}]{{attachedAttributes}}}
                """);
            h.Write("model/relations/filed.json", $$"""
                {"kind":"relation","id":"{{Filed}}","name":"Filed","package":"{{Sales}}","ends":[
                 {"id":"{{FolderEnd}}","entity":"{{SFolder}}","role":"folder","navigation":"folder","max":1},
                 {"id":"{{ItemsEnd}}","types":[{"entity":"{{SPart}}"},{"entity":"{{SAssembly}}"}],"role":"items","navigation":"items","max":"*"}]}
                """);
            h.Write("model/databases/main/tables/folders.json", Table(Folders, "folders", Column(Folders, 1, "id", nullable: false)));
            h.Write("model/databases/main/tables/parts.json", Table(Parts, "parts", Column(Parts, 1, "id", nullable: false) + "," + Column(Parts, 2, "folder_id"),
                ForeignKey(Parts, 1, "fk_parts_folder", 2, Folders)));
            h.Write("model/databases/main/tables/assemblies.json", Table(Assemblies, "assemblies",
                Column(Assemblies, 1, "id", nullable: false) + "," + Column(Assemblies, 2, "folder_id"), ForeignKey(Assemblies, 1, "fk_assemblies_folder", 2, Folders)));
            h.Write("model/databases/main/tables/comments.json", Table(Comments, "comments",
                string.Join(",", Column(Comments, 1, "id", nullable: false), Column(Comments, 2, "attached_type", "string", 20), Column(Comments, 3, "attached_id"),
                    Column(Comments, 4, "part_id"), Column(Comments, 5, "assembly_id")),
                ForeignKey(Comments, 1, "fk_part", 4, Parts) + "," + ForeignKey(Comments, 2, "fk_assembly", 5, Assemblies)));
            h.Write("model/databases/main/tables/links.json", Table(Links, "links",
                string.Join(",", Column(Links, 1, "id", nullable: false), Column(Links, 2, "comment_id"), Column(Links, 3, "target_type", "string", 20),
                    Column(Links, 4, "target_id"), Column(Links, 5, "note", "string", 40), Column(Links, 6, "relation_code", "string", 10)),
                ForeignKey(Links, 1, "fk_links_comment", 2, Comments)));
            h.Write("model/mappings/folder.json", EntityMapping(1, SFolder, Folders));
            h.Write("model/mappings/part.json", EntityMapping(2, SPart, Parts));
            h.Write("model/mappings/assembly.json", EntityMapping(3, SAssembly, Assemblies, assemblyMapping));
            if (!bindComment)
                h.Write("model/mappings/comment.json", EntityMapping(4, SComment, Comments));
            prepare(h);
        });

    private static async Task<(ResolvedModel Model, IReadOnlyList<Diagnostic> Validation)> ResolveAsync(Action<LoaderHarness> prepare, bool bindComment = false,
        string attachedAttributes = "", string assemblyMapping = "")
    {
        var (h, store) = await OpenAsync(prepare, bindComment, attachedAttributes, assemblyMapping);
        using var _ = h;
        await using var __ = store;
        return (ResolutionKit.Resolve(store.Current!), await ActorKit.ValidateAsync(h, store.Current!));
    }

    private static List<string> Messages(ResolvedModel model, string rule) => [.. model.Diagnostics.Where(d => d.Rule == rule).Select(d => d.Message)];

    private static RRelationMapping MappingOf(ResolvedModel model, string relation) => model.Relations.Single(r => r.Id == relation).Mappings["main"];

    private static string Way3 => $$""","ends":[{"end":"{{AttachedToEnd}}","typeColumn":"{{Col(Comments, 2)}}","idColumn":"{{Col(Comments, 3)}}"}]""";

    [Fact]
    public async Task A_type_and_an_id_links_each_pair_of_targets_with_codes_and_says_what_the_database_cannot_enforce()
    {
        var (model, validation) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3)));
        Assert.DoesNotContain(validation, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(Messages(model, "MQ4117"));
        Assert.Empty(Messages(model, "MQ4120"));
        var mapping = MappingOf(model, Attached);
        Assert.Equal("foreign-key", mapping.Shape);
        Assert.Null(mapping.JunctionTable);
        Assert.Equal(["Part", "Assembly"], mapping.Links.Select(l => l.Ends[1].Type.Name));
        var part = mapping.Links[0];
        Assert.Equal("comments", part.Table.Name);
        Assert.True(part.Ends[0].IsHolder);
        Assert.Empty(part.Ends[0].Columns);
        Assert.Equal(("attached_id", "parts", "id", "attached_type"),
            (part.Ends[1].Columns.Single().Name, part.Ends[1].ReferencedTable!.Name, part.Ends[1].ReferencedColumns.Single().Name, part.Ends[1].TypeColumn!.Name));
        Assert.Equal(["Part"], part.Ends[1].Codes);
        Assert.Equal(["Assembly"], mapping.Links[1].Ends[1].Codes);
        Assert.Equal([("Assembly", (object)"Assembly"), ("Part", "Part")], mapping.TypeCodes.Select(c => (c.Entity.Name, c.Code)));

        var gap = Assert.Single(Messages(model, "MQ4118"));
        Assert.Equal("Database 'main' holds end 'attachedTo' of relation 'AttachedTo' as a type and an id in table 'comments', so it can't refuse a link to a missing 'Part' or 'Assembly'; and it finds no check that names column 'attached_type' of table 'comments'. To check the codes, add: CONSTRAINT ck_comments_attached_to CHECK (attached_type IN ('Assembly', 'Part'))", gap);
        var note = Assert.Single(model.Db("main").RelationStorage, n => n.Relation.Id == Attached);
        Assert.Equal(["no-foreign-key", "type-check"], note.Gaps.Select(g => g.Kind));
        Assert.Equal(["comments"], note.Tables.Select(t => t.Name));
        // Filed has no mapping: listed with no storage.
        var filed = Assert.Single(model.Db("main").RelationStorage, n => n.Relation.Id == Filed);
        Assert.Null(filed.MappingId);
        Assert.Empty(filed.Tables);
    }

    [Fact]
    public async Task A_key_per_type_asks_for_a_count_check_and_a_type_left_out_is_not_stored()
    {
        var both = $$""","ends":[{"end":"{{AttachedToEnd}}","types":[{"entity":"{{SPart}}","foreignKey":"{{Fk(Comments, 1)}}"},{"entity":"{{SAssembly}}","foreignKey":"{{Fk(Comments, 2)}}"}]}]""";
        var (model, _) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, both)));
        var mapping = MappingOf(model, Attached);
        Assert.Equal(["fk_part", "fk_assembly"], mapping.Links.Select(l => l.Ends[1].ForeignKey!.Name));
        Assert.Empty(mapping.TypeCodes);
        var gap = Assert.Single(Messages(model, "MQ4118"));
        Assert.Contains("as a key per type in table 'comments'", gap, StringComparison.Ordinal);
        Assert.Contains("CHECK ((CASE WHEN part_id IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN assembly_id IS NOT NULL THEN 1 ELSE 0 END) <= 1)", gap, StringComparison.Ordinal);

        var partOnly = $$""","ends":[{"end":"{{AttachedToEnd}}","types":[{"entity":"{{SPart}}","foreignKey":"{{Fk(Comments, 1)}}"}]}]""";
        var (left, _) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, partOnly)));
        Assert.Equal(["Relation 'AttachedTo' covers entity 'Assembly' in database 'main', but its mapping names nothing that points at it there, so links to a Assembly aren't stored."],
            Messages(left, "MQ4120"));
        Assert.Equal([("attachedTo", "Assembly")], MappingOf(left, Attached).NotStored.Select(x => (x.End.Role, x.Entity.Name)));
    }

    [Fact]
    public async Task A_key_on_each_types_table_is_held_by_the_end_with_several_types()
    {
        var way1 = $$""","ends":[{"end":"{{FolderEnd}}","foreignKeys":["{{Fk(Parts, 1)}}","{{Fk(Assemblies, 1)}}"]}]""";
        var (model, validation) = await ResolveAsync(h => h.Write("model/mappings/filed.json", RelationMapping(Filed, way1)));
        Assert.DoesNotContain(validation, d => d.Severity == DiagnosticSeverity.Error);
        var mapping = MappingOf(model, Filed);
        Assert.Equal([("parts", "Part"), ("assemblies", "Assembly")], mapping.Links.Select(l => (l.Table.Name, l.Ends[1].Type.Name)));
        Assert.All(mapping.Links, l => Assert.True(l.Ends[1].IsHolder));
        Assert.Equal(["fk_parts_folder", "fk_assemblies_folder"], mapping.Links.Select(l => l.Ends[0].ForeignKey!.Name));
        Assert.Equal("items", mapping.Links[0].Ends[0].ForeignKey!.End!.Role);
        Assert.Empty(Messages(model, "MQ4117"));
        Assert.Empty(Messages(model, "MQ4118"));
        // MQ4115 is silenced by a mapping that stores the relation (decision 49).
        Assert.DoesNotContain(validation, d => d.Rule == "MQ4115" && d.ElementId == Filed);

        var partsOnly = $$""","ends":[{"end":"{{FolderEnd}}","foreignKeys":["{{Fk(Parts, 1)}}"]}]""";
        var (left, _) = await ResolveAsync(h => h.Write("model/mappings/filed.json", RelationMapping(Filed, partsOnly)));
        Assert.Equal([("items", "Assembly")], MappingOf(left, Filed).NotStored.Select(x => (x.End.Role, x.Entity.Name)));
    }

    [Fact]
    public async Task A_link_table_with_constants_and_an_attribute_column()
    {
        var attributes = $$""","attributes":[{"id":"{{NoteAttribute}}","name":"note","type":"string","length":40}]""";
        var body = $$""","junctionTable":"{{Links}}","ends":[{"end":"{{CommentEnd}}","foreignKey":"{{Fk(Links, 1)}}"},{"end":"{{AttachedToEnd}}","typeColumn":"{{Col(Links, 3)}}","idColumn":"{{Col(Links, 4)}}"}],"""
            + $$""" "attributes":[{"attribute":"{{NoteAttribute}}","column":"{{Col(Links, 5)}}"}],"constants":[{"column":"{{Col(Links, 6)}}","value":"AT"}]""";
        var (model, validation) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, body)), attachedAttributes: attributes);
        Assert.DoesNotContain(validation, d => d.Severity == DiagnosticSeverity.Error);
        var mapping = MappingOf(model, Attached);
        Assert.Equal(("junction", "links"), (mapping.Shape, mapping.JunctionTable!.Name));
        Assert.All(mapping.Links, l => Assert.Equal("links", l.Table.Name));
        Assert.Equal([("relation_code", (object?)"AT")], mapping.Constants.Select(c => (c.Column.Name, c.Values.Single())));
        Assert.Equal([("note", "note")], mapping.Attributes.Select(a => (a.Attribute.Name, a.Column.Name)));
        var links = model.Db("main").Table("links");
        Assert.True(links.IsJunction);
        Assert.Equal("AttachedTo", links.Relation!.Name);
        Assert.Equal("note", links.Columns.Single(c => c.Name == "note").Attribute!.Name);
        var gap = Assert.Single(Messages(model, "MQ4118"));
        Assert.Contains("CONSTRAINT uq_links_comment_id_target_type_target_id_relation_code UNIQUE (comment_id, target_type, target_id, relation_code)", gap, StringComparison.Ordinal);
        Assert.Equal(["no-foreign-key", "type-check", "link-unique"], model.Db("main").RelationStorage.Single(n => n.Relation.Id == Attached).Gaps.Select(g => g.Kind));

        // MQ4011: a link table needs an entry for every end; MQ4051: a constant its column can't hold.
        var missing = $$""","junctionTable":"{{Links}}","ends":[{"end":"{{AttachedToEnd}}","typeColumn":"{{Col(Links, 3)}}","idColumn":"{{Col(Links, 4)}}"}]""";
        var (_, files) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, missing)));
        Assert.Equal(["Link table 'links' holds relation 'AttachedTo', so end 'comment' needs an 'ends' entry that says how the table points at it."],
            files.Where(d => d.Rule == "MQ4011").Select(d => d.Message));
        var tooLong = body.Replace("\"AT\"", "\"ATTACHED-TO-PART\"", StringComparison.Ordinal);
        var (fit, _) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, tooLong)), attachedAttributes: attributes);
        Assert.Equal(["The constant for column 'relation_code' of the mapping of relation 'AttachedTo' to database 'main' is 16 characters long, over the column's 10 (the column is string)."],
            Messages(fit, "MQ4051"));
    }

    [Fact]
    public async Task MQ4117_refuses_storage_that_does_not_fit_together()
    {
        var wrongTable = $$""","ends":[{"end":"{{AttachedToEnd}}","types":[{"entity":"{{SPart}}","foreignKey":"{{Fk(Comments, 2)}}"},{"entity":"{{SAssembly}}","foreignKey":"{{Fk(Comments, 1)}}"}]}]""";
        var (a, _) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, wrongTable)));
        Assert.Contains("Foreign key 'fk_assembly' of table 'comments' is named for type 'Part' of end 'attachedTo', but it refers to table 'assemblies', which doesn't hold Part rows.",
            Messages(a, "MQ4117"));

        var mismatch = $$""","ends":[{"end":"{{AttachedToEnd}}","idColumn":"{{Col(Comments, 2)}}"}]""";
        var (b, _) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, mismatch)));
        Assert.Contains("Column 'attached_type' of table 'comments' is string(20), but it refers to column 'id' of table 'parts', which is uuid; make the types match.", Messages(b, "MQ4117"));

        var (c, validation) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3)), assemblyMapping: ",\"discriminatorValue\":\"Part\"");
        Assert.DoesNotContain(validation, d => d.Rule == "MQ4009"); // discriminatorValue outside a hierarchy: an end with several types covers Assembly
        Assert.Contains("Types 'Part' and 'Assembly' both have code 'Part' in database 'main', so column 'attached_type' can't tell them apart; set discriminatorValue on one.",
            Messages(c, "MQ4117"));

        var noHolder = $$""","ends":[{"end":"{{CommentEnd}}","foreignKey":"{{Fk(Links, 1)}}"},{"end":"{{AttachedToEnd}}","idColumn":"{{Col(Links, 4)}}"}]""";
        var (d, _) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, noHolder)));
        Assert.Contains("Every end of relation 'AttachedTo' has an entry and there is no 'junctionTable', so no table holds its links in database 'main'; name a link table, or leave out the entry of the end whose rows hold the links.",
            Messages(d, "MQ4117"));
    }

    [Fact]
    public async Task MQ4009_reports_the_file_cases_of_a_mapping_of_such_a_relation()
    {
        var body = $$""","constants":[{"column":"{{Col(Comments, 2)}}","value":"x"}],"ends":["""
            + $$"""{"end":"{{AttachedToEnd}}","foreignKey":"{{Fk(Comments, 1)}}","idColumn":"{{Col(Comments, 3)}}"},"""
            + $$"""{"end":"{{CommentEnd}}","typeColumn":"{{Col(Comments, 2)}}"}]""";
        var (_, validation) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, body)));
        Assert.Equal(
        [
            ("/constants/0", "Constant 0 needs a designed 'junctionTable' that holds its column."),
            ("/ends/0", "End entry 0 of relation 'AttachedTo' names more than one way to point at end 'attachedTo' (foreignKey, idColumn); keep one."),
            ("/ends/1", "End entry 1 of relation 'AttachedTo' names no way to point at end 'comment'; give it a foreignKey, foreignKeys, types with foreignKey items or an idColumn."),
            ("/ends/1/typeColumn", "End entry 1 of relation 'AttachedTo' sets 'typeColumn' with no 'idColumn'; it describes the id column."),
        ], validation.Where(x => x.Rule == "MQ4009" && x.ElementId == N(P, 990)).Select(x => (x.JsonPointer, x.Message)).Order());
    }

    [Fact]
    public async Task A_binding_maps_the_type_column_and_the_id_and_MQ4117_says_when_it_maps_another_column_than_the_mapping()
    {
        var (model, validation) = await ResolveAsync(h => h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3)), bindComment: true);
        Assert.DoesNotContain(validation, d => d.Rule is "MQ4116" or "MQ4045");
        var binding = model.Entities.Single(e => e.Id == SComment).Bindings["main"];
        Assert.Equal([(null, (string?)null), ("type", null), ("id", null)], binding.Fields.Select(f => (f.Holds, f.KeyOf?.Name)));
        Assert.Equal(["id", "attachedToType", "attachedToId"], binding.Fields.Select(f => f.Name));
        Assert.Equal(["Field 'attachedToId' of the binding of entity 'Comment' to database 'main' maps column 'part_id', but mapping 'storage' names column 'attached_id' of table 'comments' for it."],
            model.Diagnostics.Where(d => d.Rule == "MQ4117").Select(d => d.Message));
    }
}
