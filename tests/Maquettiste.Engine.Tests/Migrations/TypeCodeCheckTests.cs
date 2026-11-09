using System.Text.Json;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Migrations;

/// <summary>
/// MQ4119 (several-types-design.md decision 52): a type code a relation mapping's type column holds that changed since the baseline. Codes come
/// from the files on each side (the binding's <c>discriminatorValue</c>, then the mapping's, then the name), what each type column holds from
/// the database run; an entity the baseline lacks, or a code that didn't change, gives nothing; the finding sits where the code is set.
/// </summary>
public sealed class TypeCodeCheckTests
{
    private const string Main = "01JDBS00000000000000000001";
    private const string Part = "01JENT00000000000000000001";
    private const string Assembly = "01JENT00000000000000000002";

    private static Database Db => new() { Id = Main, Name = "main", Dialect = Dialect.PostgreSql };

    private const string AttachedTo = "01JRXN00000000000000000001", AttachedToEnd = "01JEND00000000000000000002";
    private const string TypeColumn = "01JCMN00000000000000000001";

    private static ModelSnapshot Model(string partName, JsonElement? bindingCode = null, JsonElement? mappingCode = null, bool bound = false,
        string? typeColumn = TypeColumn)
    {
        var b = new ModelBuilder().Add(Db);
        b.Add(new Entity
        {
            Id = Part, Name = partName,
            Bindings = bindingCode is null && !bound ? [] : [new EntityBinding { Id = "01JBND00000000000000000001", Database = Main, Source = "parts", DiscriminatorValue = bindingCode }],
        });
        b.Add(new Entity { Id = Assembly, Name = "Assembly" });
        if (mappingCode is not null)
            b.Add(new Mapping { Id = "01JMAP00000000000000000001", Name = "Part in main", Database = Main, Entity = Part, DiscriminatorValue = mappingCode });
        // The relation mapping whose type column holds the codes; the baseline's tells whether links were stored with them.
        if (typeColumn is not null)
        {
            b.Add(new Mapping
            {
                Id = "01JMAP00000000000000000009", Name = "AttachedTo in main", Database = Main, Relation = AttachedTo,
                Ends = [new RelationEndMapping { End = AttachedToEnd, TypeColumn = typeColumn, IdColumn = "01JCMN00000000000000000002" }],
            });
        }

        return b.Build();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<TypeCodeHolder>> Held = new Dictionary<string, IReadOnlyList<TypeCodeHolder>>
    {
        [Main] =
        [
            new(Part, "Part", "Part", "attached_type", "comments", "AttachedTo", AttachedTo, AttachedToEnd),
            new(Assembly, "Assembly", "Assembly", "attached_type", "comments", "AttachedTo", AttachedTo, AttachedToEnd),
        ],
    };

    [Fact]
    public void A_code_read_from_files_takes_the_binding_then_the_mapping_then_the_name()
    {
        Assert.Equal(("Part", Part, "/name"), Simple(TypeCodeCheck.CodeOf(Model("Part"), Get(Model("Part")), Main)));
        var mapped = Model("Part", mappingCode: Json("17"));
        Assert.Equal(((object)17L, "01JMAP00000000000000000001", "/discriminatorValue"), Simple(TypeCodeCheck.CodeOf(mapped, Get(mapped), Main)));
        var bound = Model("Part", Json("\"P\""), Json("17"));
        Assert.Equal(((object)"P", Part, "/bindings/0/discriminatorValue"), Simple(TypeCodeCheck.CodeOf(bound, Get(bound), Main)));

        // A binding wins (MQ4054): with no value of its own the name is the code, and the mapping's leftover value is ignored, as the run does.
        var boundNoCode = Model("Part", mappingCode: Json("\"P\""), bound: true);
        Assert.Equal(((object)"Part", Part, "/name"), Simple(TypeCodeCheck.CodeOf(boundNoCode, Get(boundNoCode), Main)));
        Assert.Empty(TypeCodeCheck.Findings(Model("Part", bound: true), boundNoCode, [Db], Held, "main@v1"));

        static Entity Get(ModelSnapshot m) => m.Get<Entity>(Part)!;
        static (object, string, string) Simple((object Code, string ElementId, string Pointer) x) => (x.Code is int i ? (long)i : x.Code, x.ElementId, x.Pointer);
    }

    [Fact]
    public void A_renamed_entity_with_no_code_of_its_own_is_reported_where_its_name_is()
    {
        var found = TypeCodeCheck.Findings(Model("Item"), Model("Part"), [Db], Held, "main@v1");
        var finding = Assert.Single(found);
        Assert.Equal("MQ4119", finding.Rule);
        Assert.Equal(Part, finding.ElementId);
        Assert.Equal("/name", finding.JsonPointer);
        Assert.Equal(
            "Since the baseline, the type code of 'Item' in database 'main' changed from 'Part' to 'Item', and column 'attached_type' of table 'comments' holds codes of relation 'AttachedTo'; links stored with the old code no longer find their object. Update those rows, or keep the stored code.",
            finding.Message);
    }

    [Fact]
    public void Keeping_the_stored_code_clears_it_and_an_unchanged_code_gives_nothing()
    {
        Assert.Empty(TypeCodeCheck.Findings(Model("Item", mappingCode: Json("\"Part\"")), Model("Part"), [Db], Held, "main@v1"));
        Assert.Empty(TypeCodeCheck.Findings(Model("Part"), Model("Part"), [Db], Held, "main@v1"));
        // A text code and an integer code with the same digits differ.
        var found = TypeCodeCheck.Findings(Model("Part", mappingCode: Json("17")), Model("Part", mappingCode: Json("\"17\"")), [Db], Held, "main@v1");
        Assert.Contains("changed from '17' to 17,", Assert.Single(found).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_type_column_or_no_baseline_entity_gives_nothing()
    {
        Assert.Empty(TypeCodeCheck.Findings(Model("Item"), Model("Part"), [Db], new Dictionary<string, IReadOnlyList<TypeCodeHolder>>(), "main@v1"));
        var baseline = new ModelBuilder().Add(Db).Add(new Entity { Id = Assembly, Name = "Assembly" }).Build();
        Assert.Empty(TypeCodeCheck.Findings(Model("Item"), baseline, [Db], Held, "main@v1"));
    }

    [Fact]
    public void A_type_column_the_baseline_did_not_have_holds_no_stored_links()
    {
        // The mapping (and so the type column) added in the same change as the rename: no links were stored with the old code.
        Assert.Empty(TypeCodeCheck.Findings(Model("Item"), Model("Part", typeColumn: null), [Db], Held, "main@v1"));
        // A different type column since the baseline holds none either.
        Assert.Empty(TypeCodeCheck.Findings(Model("Item"), Model("Part", typeColumn: "01JCMN00000000000000000003"), [Db], Held, "main@v1"));
        Assert.Single(TypeCodeCheck.Findings(Model("Item"), Model("Part"), [Db], Held, "main@v1"));
    }

    [Fact]
    public void A_database_with_no_relation_storage_holds_no_type_codes()
    {
        Assert.Empty(TypeCodeCheck.HoldersOf(new RDatabase { Id = Main, Name = "main" }));
    }
}
