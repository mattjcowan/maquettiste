using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The rows of several-types-design.md section 19.16 over the same model: each way in each cardinality over TPH, TPT and TPC (an abstract TPC
/// root at a one-type end included), MQ4118 in five dialects, codes from a binding and integer codes, <c>referencesColumn</c> to an
/// engine-made table, primary keys of several columns or none, way 5's one join path, and the MQ4117 and MQ4009 cases not covered above.
/// </summary>
public sealed partial class RelationTypeStorageTests
{
    private static readonly string SItem = N(P, 5), ItemKey = N(P, 105);
    private static readonly string Pinned = N(P, 31), PinnedEnd = N(P, 32), PinnedItemEnd = N(P, 33);
    private static readonly string Pins = N(Pt, 6);

    private static void Delete(LoaderHarness h, string path) => File.Delete(h.Model(path));

    private static string KeyTo(string table, int n, string name, int column, string referencesTable) =>
        $$"""{"id":"{{Fk(table, n)}}","name":"{{name}}","columns":["{{Col(table, column)}}"],"referencesTable":"{{referencesTable}}","referencesColumns":[]}""";

    private static string AttachedRelation(string commentMax, string attachedMax) => $$"""
        {"kind":"relation","id":"{{Attached}}","name":"AttachedTo","package":"{{Sales}}","ends":[
         {"id":"{{CommentEnd}}","entity":"{{SComment}}","role":"comment","navigation":"comments","max":{{commentMax}}},
         {"id":"{{AttachedToEnd}}","types":[{"entity":"{{SPart}}"},{"entity":"{{SAssembly}}"}],"role":"attachedTo","navigation":"attachedTo","max":{{attachedMax}}}]}
        """;

    /// <summary>The comments table with a key per type and a key to the shared table, each to a table the engine makes.</summary>
    private static string CommentsTo(string parts, string assemblies, string? items) => Table(Comments, "comments",
        string.Join(",", Column(Comments, 1, "id", nullable: false), Column(Comments, 2, "attached_type", "string", 20), Column(Comments, 3, "attached_id"),
            Column(Comments, 4, "part_id"), Column(Comments, 5, "assembly_id"), Column(Comments, 6, "item_id")),
        string.Join(",", new[] { KeyTo(Comments, 1, "fk_part", 4, parts), KeyTo(Comments, 2, "fk_assembly", 5, assemblies) }
            .Concat(items is null ? [] : [KeyTo(Comments, 3, "fk_item", 6, items)])));

    private static string Describe(RJoinPath path) => string.Join(" | ", path.Steps.Select(s =>
        $"{s.FromTable.Name}({string.Join(',', s.FromColumns.Select(c => c.Name))}) > {s.ToTable.Name}({string.Join(',', s.ToColumns.Select(c => c.Name))})"
        + string.Concat(s.Conditions.Select(c => $" [{c.Column.Name} in {string.Join(',', c.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))}]"))))
        + (path.FromType is { } from ? " from=" + from.Name : "") + (path.ToType is { } to ? " to=" + to.Name : "");

    private static RNavigation NavigationOf(ResolvedModel model, string entity, string name) =>
        model.Entities.Single(e => e.Id == entity).Navigations.Single(n => n.Name == name);

    private static List<string> Kinds(ResolvedModel model, string relation) =>
        [.. model.Db("main").RelationStorage.Single(n => n.Relation.Id == relation).Gaps.Select(g => g.Kind)];

    [Fact]
    public async Task Way_5_sets_joins_to_its_one_path_to_the_shared_table_with_no_per_target_row_condition()
    {
        // Part and Assembly bound to one designed table, items, told apart by their bindings' constants; comments.item_id refers to items.
        const string Items = "01JST300000000000000000007";
        string Bound(string id, string name, int key, string code) => EntityFile(id, name,
            attributes: $$""","key":{"attributes":["{{N(P, 100 + key)}}"]},"attributes":[{"id":"{{N(P, 100 + key)}}","name":"id","type":"uuid"}],"""
                + $$""" "bindings":[{"id":"{{N(P, 810 + key)}}","database":"{{Main}}","source":"{{Items}}","fields":[{"attribute":"{{N(P, 100 + key)}}","column":"id"}],"""
                + $$""" "constants":[{"column":"kind","value":"{{code}}"}]}]""");
        var (model, validation) = await ResolveAsync(h =>
        {
            h.Write("model/entities/part.json", Bound(SPart, "Part", 1, "P"));
            h.Write("model/entities/assembly.json", Bound(SAssembly, "Assembly", 2, "A"));
            Delete(h, "model/relations/filed.json");
            h.Write("model/databases/main/tables/items.json", Table(Items, "items", Column(Items, 1, "id", nullable: false) + "," + Column(Items, 2, "kind", "string", 1)));
            h.Write("model/databases/main/tables/comments.json", CommentsTo(Parts, Assemblies, Items));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, $$""","ends":[{"end":"{{AttachedToEnd}}","foreignKey":"{{Fk(Comments, 3)}}"}]"""));
        });
        Assert.DoesNotContain(validation, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(Messages(model, "MQ4117"));
        var forth = NavigationOf(model, SComment, "attachedTo");
        Assert.Equal("comments(item_id) > items(id)", Describe(forth.Joins["main"]));
        Assert.Equal(["comments(item_id) > items(id) [kind in P] to=Part", "comments(item_id) > items(id) [kind in A] to=Assembly"],
            forth.TypeJoins["main"].Select(Describe));
        var back = NavigationOf(model, SPart, "comments");
        Assert.Equal("items(id) > comments(item_id)", Describe(back.Joins["main"]));
        Assert.Equal(["items(id) > comments(item_id) from=Part", "items(id) > comments(item_id) from=Assembly"], back.TypeJoins["main"].Select(Describe));
        Assert.Equal(["shared-table"], Kinds(model, Attached));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_bound_target_picks_its_rows_by_the_constants_of_every_entity_it_stands_for(bool oneColumn)
    {
        // Part, PurchasedPart (derived from Part, so covered and in Part's table) and Gizmo (not covered) are bound to items, each with a
        // constant; stored as a type and an id. Part stands for PurchasedPart (decision 48): the step into items reads both codes, and their
        // constants pick their rows when they set one column. Otherwise nothing picks them, and MQ4118 says so.
        const string Items = "01JST300000000000000000007";
        string Bound(string id, string name, int key, string column, string code, string? @base = null) => EntityFile(id, name, @base,
            attributes: (@base is null ? $$""","key":{"attributes":["{{N(P, 100 + key)}}"]},"attributes":[{"id":"{{N(P, 100 + key)}}","name":"id","type":"uuid"}],""" : ",")
                + $$""" "bindings":[{"id":"{{N(P, 810 + key)}}","database":"{{Main}}","source":"{{Items}}","fields":[{"attribute":"{{N(P, 100 + (@base is null ? key : 1))}}","column":"id"}],"""
                + $$""" "constants":[{"column":"{{column}}","value":"{{code}}"}]}]""");
        var (model, validation) = await ResolveAsync(h =>
        {
            h.Write("model/entities/part.json", Bound(SPart, "Part", 1, "kind", "P"));
            h.Write("model/entities/purchased-part.json", Bound(N(P, 6), "PurchasedPart", 6, oneColumn ? "kind" : "sub", "PP", SPart));
            h.Write("model/entities/gizmo.json", Bound(N(P, 7), "Gizmo", 7, "kind", "G"));
            Delete(h, "model/relations/filed.json");
            h.Write("model/databases/main/tables/items.json", Table(Items, "items",
                Column(Items, 1, "id", nullable: false) + "," + Column(Items, 2, "kind", "string", 2) + "," + Column(Items, 3, "sub", "string", 2)));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3));
        });
        Assert.DoesNotContain(validation, d => d.Severity == DiagnosticSeverity.Error);
        var toPart = NavigationOf(model, SComment, "attachedTo").TypeJoins["main"].Single(p => p.ToType?.Name == "Part");
        Assert.Equal(oneColumn
                ? "comments(attached_id) > items(id) [attached_type in Part,PurchasedPart] [kind in P,PP] to=Part"
                : "comments(attached_id) > items(id) [attached_type in Part,PurchasedPart] to=Part",
            Describe(toPart));
        Assert.Equal(!oneColumn, Kinds(model, Attached).Contains("rows-not-told-apart"));
    }

    [Fact]
    public async Task A_bound_target_picks_its_rows_by_the_type_column_its_base_names()
    {
        // As above, but Part's binding names the type column (discriminatorColumn) and PurchasedPart takes it from Part: no constants. The
        // type column tells their rows apart from Gizmo's (a constant on the same column), so the step into items reads both codes there,
        // and MQ4118 does not say the rows are not told apart.
        const string Items = "01JST300000000000000000007";
        string Bound(string id, string name, int key, string extra, string? @base = null) => EntityFile(id, name, @base,
            attributes: (@base is null ? $$""","key":{"attributes":["{{N(P, 100 + key)}}"]},"attributes":[{"id":"{{N(P, 100 + key)}}","name":"id","type":"uuid"}],""" : ",")
                + $$""" "bindings":[{"id":"{{N(P, 810 + key)}}","database":"{{Main}}","source":"{{Items}}","fields":[{"attribute":"{{N(P, 100 + (@base is null ? key : 1))}}","column":"id"}],"""
                + extra + "}]");
        var (model, validation) = await ResolveAsync(h =>
        {
            h.Write("model/entities/part.json", Bound(SPart, "Part", 1, """ "discriminatorColumn":"kind","discriminatorValue":"P" """));
            h.Write("model/entities/purchased-part.json", Bound(N(P, 6), "PurchasedPart", 6, """ "discriminatorValue":"PP" """, SPart));
            h.Write("model/entities/gizmo.json", Bound(N(P, 7), "Gizmo", 7, """ "constants":[{"column":"kind","value":"G"}] """));
            Delete(h, "model/relations/filed.json");
            h.Write("model/databases/main/tables/items.json", Table(Items, "items",
                Column(Items, 1, "id", nullable: false) + "," + Column(Items, 2, "kind", "string", 2) + "," + Column(Items, 3, "sub", "string", 2)));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3));
        });
        Assert.DoesNotContain(validation, d => d.Severity == DiagnosticSeverity.Error);
        var toPart = NavigationOf(model, SComment, "attachedTo").TypeJoins["main"].Single(p => p.ToType?.Name == "Part");
        Assert.Equal("comments(attached_id) > items(id) [attached_type in P,PP] [kind in P,PP] to=Part", Describe(toPart));
        Assert.DoesNotContain("rows-not-told-apart", Kinds(model, Attached));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("oracle")]
    [InlineData("sqlite")]
    public async Task MQ4118_writes_its_texts_in_each_dialect(string dialect)
    {
        // One to one, a type and an id: the check on the codes and the unique on the pointer, in the dialect's quoting.
        var (model, _) = await ResolveAsync(h =>
        {
            h.Write("model/databases/main/database.json", $$"""{"kind":"database","id":"{{Main}}","name":"main","dialect":"{{dialect}}"}""");
            h.Write("model/relations/attached-to.json", AttachedRelation("1", "1"));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3));
        });
        var text = Assert.Single(Messages(model, "MQ4118"));
        // Identifiers are quoted as the database's quoting says (here only where needed); SQL Server writes national literals, and a unique
        // over nullable columns as an index filtered to rows where they are set, on the schema-qualified table (decision 31).
        var (codes, unique) = dialect switch
        {
            "sqlserver" => ("N'Assembly', N'Part'",
                "CREATE UNIQUE INDEX ix_comments_attached_type_attached_id ON dbo.comments (attached_type, attached_id) WHERE attached_type IS NOT NULL AND attached_id IS NOT NULL"),
            _ => ("'Assembly', 'Part'", "CONSTRAINT uq_comments_attached_type_attached_id UNIQUE (attached_type, attached_id)"),
        };
        Assert.Equal(
            "Database 'main' holds end 'attachedTo' of relation 'AttachedTo' as a type and an id in table 'comments', so it can't refuse a link to a missing 'Part' or 'Assembly'; "
            + "and it finds no check that names column 'attached_type' of table 'comments'; and it finds no unique on 'attached_type', 'attached_id' of table 'comments', so two rows can link to one object. "
            + $"To check the codes, add: CONSTRAINT ck_comments_attached_to CHECK (attached_type IN ({codes})) To keep the links one to one, add: {unique}",
            text);
        Assert.Equal(["no-foreign-key", "type-check", "one-to-one-unique"], Kinds(model, Attached));
    }

    [Fact]
    public async Task Codes_come_from_a_binding_then_a_mapping_and_an_integer_type_column_holds_integer_codes()
    {
        // Part bound with an integer discriminatorValue; Assembly's mapping sets another; Comment's type column is an integer, then text.
        string PartBound(string value) => EntityFile(SPart, "Part",
            attributes: $$""","key":{"attributes":["{{N(P, 101)}}"]},"attributes":[{"id":"{{N(P, 101)}}","name":"id","type":"uuid"}],"""
                + $$""" "bindings":[{"id":"{{N(P, 811)}}","database":"{{Main}}","source":"{{Parts}}","discriminatorValue":{{value}},"fields":[{"attribute":"{{N(P, 101)}}","column":"id"}],"""
                + """ "columns":[{"column":"folder_id","status":"ignored"}]}]""");
        string CommentsWith(string type) => Table(Comments, "comments",
            string.Join(",", Column(Comments, 1, "id", nullable: false), Column(Comments, 2, "attached_type", type, type == "string" ? 20 : null), Column(Comments, 3, "attached_id")));
        Task<(ResolvedModel, IReadOnlyList<Diagnostic>)> Run(string type, string partCode, string assemblyCode) => ResolveAsync(h =>
        {
            h.Write("model/entities/part.json", PartBound(partCode));
            h.Write("model/databases/main/tables/comments.json", CommentsWith(type));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3));
        }, assemblyMapping: ",\"discriminatorValue\":" + assemblyCode);

        var (integer, validation) = await Run("int32", "17", "18");
        Assert.DoesNotContain(validation, d => d.Rule == "MQ4009");
        Assert.Empty(Messages(integer, "MQ4117"));
        var mapping = MappingOf(integer, Attached);
        Assert.Equal([("Assembly", (object)18L), ("Part", 17L)], mapping.TypeCodes.Select(c => (c.Entity.Name, c.Code)));
        Assert.Equal([17L, 18L], mapping.Links.Select(l => l.Ends[1].Codes.Single()));
        Assert.Contains("CHECK (attached_type IN (17, 18))", Assert.Single(Messages(integer, "MQ4118")), StringComparison.Ordinal);
        Assert.Equal(17L, integer.Entities.Single(e => e.Id == SPart).Bindings["main"].DiscriminatorValue);

        // In a text column the integer codes are text.
        var (text, _) = await Run("string", "17", "18");
        Assert.Equal(["17", "18"], MappingOf(text, Attached).Links.Select(l => l.Ends[1].Codes.Single()));
        Assert.Contains("CHECK (attached_type IN ('17', '18'))", Assert.Single(Messages(text, "MQ4118")), StringComparison.Ordinal);

        // A code an integer column can't hold, and a type column that holds neither text nor integers.
        var (misfit, _) = await Run("int32", "17", "\"Asm\"");
        Assert.Equal(["Column 'attached_type' of table 'comments' holds the type codes of end 'attachedTo', but code 'Asm' isn't an integer; set an integer discriminatorValue."],
            Messages(misfit, "MQ4117"));
        var (uuid, _) = await Run("uuid", "17", "18");
        Assert.Contains("Column 'attached_type' of table 'comments' holds the type codes of end 'attachedTo', but it is a uuid column; a type column holds text or integers.",
            Messages(uuid, "MQ4117"));
    }

    [Fact]
    public async Task A_primary_key_of_several_columns_or_none_needs_referencesColumn()
    {
        var (model, _) = await ResolveAsync(h =>
        {
            h.Write("model/databases/main/tables/parts.json",
                $$"""{"kind":"table","id":"{{Parts}}","name":"parts","database":"{{Main}}","columns":[{{Column(Parts, 1, "id", nullable: false)}},{{Column(Parts, 2, "folder_id")}},{{Column(Parts, 3, "rev", "int32", nullable: false)}}],"primaryKey":{"columns":["{{Col(Parts, 1)}}","{{Col(Parts, 3)}}"]},"foreignKeys":[{{ForeignKey(Parts, 1, "fk_parts_folder", 2, Folders)}}]}""");
            h.Write("model/databases/main/tables/assemblies.json",
                $$"""{"kind":"table","id":"{{Assemblies}}","name":"assemblies","database":"{{Main}}","columns":[{{Column(Assemblies, 1, "id", nullable: false)}},{{Column(Assemblies, 2, "folder_id")}}],"foreignKeys":[{{ForeignKey(Assemblies, 1, "fk_assemblies_folder", 2, Folders)}}]}""");
            h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3));
        });
        Assert.Equal(
            [
                "Table 'parts' of type 'Part' has a primary key of 2 columns, so column 'attached_id' of end 'attachedTo' needs 'referencesColumn' naming the unique column it refers to.",
                "Table 'assemblies' of type 'Assembly' has no primary key, so column 'attached_id' of end 'attachedTo' needs 'referencesColumn' naming the unique column it refers to.",
            ],
            Messages(model, "MQ4117").Order(StringComparer.Ordinal).Reverse());
        Assert.Empty(MappingOf(model, Attached).Links);
    }

    [Fact]
    public async Task MQ4009_refuses_a_key_that_is_not_a_foreign_key_names_of_another_database_and_a_compound_attribute_column()
    {
        const string Other = "01JDBS00000000000000000952", Elsewhere = "01JST300000000000000000009";
        var unique = N(Ft, 4900);
        var attributes = $$""","attributes":[{"id":"{{NoteAttribute}}","name":"notes","type":"string","collection":true}]""";
        var (_, validation) = await ResolveAsync(h =>
        {
            h.Write("model/databases/other/database.json", $$"""{"kind":"database","id":"{{Other}}","name":"other","dialect":"postgresql","byConvention":"none"}""");
            h.Write("model/databases/other/tables/elsewhere.json",
                $$"""{"kind":"table","id":"{{Elsewhere}}","name":"elsewhere","database":"{{Other}}","columns":[{{Column(Elsewhere, 1, "id", nullable: false)}},{{Column(Elsewhere, 2, "x_type", "string", 20)}},{{Column(Elsewhere, 3, "x_id")}}],"primaryKey":{"columns":["{{Col(Elsewhere, 1)}}"]},"foreignKeys":[{"id":"{{Fk(Elsewhere, 1)}}","name":"fk_x","columns":["{{Col(Elsewhere, 3)}}"],"referencesTable":"{{Elsewhere}}","referencesColumns":["{{Col(Elsewhere, 1)}}"]}]}""");
            h.Write("model/databases/main/tables/links.json", Table(Links, "links",
                string.Join(",", Column(Links, 1, "id", nullable: false), Column(Links, 2, "comment_id"), Column(Links, 3, "target_type", "string", 20),
                    Column(Links, 4, "target_id"), Column(Links, 5, "note", "string", 40), Column(Links, 6, "relation_code", "string", 10)),
                ForeignKey(Links, 1, "fk_links_comment", 2, Comments), $$""","uniques":[{"id":"{{unique}}","columns":["{{Col(Links, 4)}}"]}]"""));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, $$""","junctionTable":"{{Links}}","ends":["""
                + $$"""{"end":"{{CommentEnd}}","foreignKey":"{{unique}}"},{"end":"{{AttachedToEnd}}","typeColumn":"{{Col(Elsewhere, 2)}}","idColumn":"{{Col(Elsewhere, 3)}}"}],"""
                + $$""" "attributes":[{"attribute":"{{NoteAttribute}}","column":"{{Col(Links, 5)}}"}]"""));
            h.Write("model/mappings/filed.json", RelationMapping(Filed, $$""","ends":[{"end":"{{FolderEnd}}","foreignKeys":["{{Fk(Elsewhere, 1)}}"]}]""")
                .Replace(N(P, 990), N(P, 992), StringComparison.Ordinal));
        }, attachedAttributes: attributes);
        var found = Of(validation, "MQ4009", "/mappings/");
        Assert.Contains(("/ends/0/foreignKey", $"'{unique}' is a key of table 'links' but not a foreign key."), found);
        Assert.Contains(("/ends/1/typeColumn", "Column 'x_type' belongs to table 'elsewhere' of another database."), found);
        Assert.Contains(("/ends/1/idColumn", "Column 'x_id' belongs to table 'elsewhere' of another database."), found);
        Assert.Contains(("/ends/0/foreignKeys/0", "Foreign key 'fk_x' belongs to table 'elsewhere' of another database."), found);
        Assert.Contains(("/attributes/0/column", "Attribute 0 is a collection or value object, so it can't map one column."), found);
    }
}
