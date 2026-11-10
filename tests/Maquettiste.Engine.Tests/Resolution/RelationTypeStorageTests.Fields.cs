using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Decision 46's field table, row by row, and what the records and the database view carry (several-types-design.md section 19.8), with the
/// two gap kinds the ways above don't reach: keys per type in a link table, and rows a target's table can't tell apart.
/// </summary>
public sealed partial class RelationTypeStorageTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string Field(string attribute, string column) => $$"""{"attribute":"{{attribute}}","column":"{{column}}"}""";

    [Fact]
    public async Task Each_row_of_the_binding_field_table()
    {
        // Comment and Part bound by bindings; AttachedTo stored as a key per type, Filed as a key on each type's table; Assembly not placed.
        var comment = EntityFile(SComment, "Comment", attributes: $$""","key":{"attributes":["{{N(P, 103)}}"]},"attributes":[{"id":"{{N(P, 103)}}","name":"id","type":"uuid"}],"""
            + $$""" "bindings":[{"id":"{{N(P, 800)}}","database":"{{Main}}","source":"{{Comments}}","fields":["""
            + string.Join(",", Field(N(P, 103), "id"), Field(AttachedToEnd + "." + SPart, "part_id"), Field(AttachedToEnd + "." + SAssembly, "assembly_id"),
                Field(AttachedToEnd + "." + SFolder, "attached_id"), Field(CommentEnd, "attached_type"))
            + "]}]");
        var part = EntityFile(SPart, "Part", attributes: $$""","key":{"attributes":["{{N(P, 101)}}"]},"attributes":[{"id":"{{N(P, 101)}}","name":"id","type":"uuid"}],"""
            + $$""" "bindings":[{"id":"{{N(P, 801)}}","database":"{{Main}}","source":"{{Parts}}","fields":["""
            + string.Join(",", Field(N(P, 101), "id"), Field(FolderEnd, "folder_id"), Field(FolderEnd + ".type", "folder_id"), Field(CommentEnd, "id"))
            + "]}]");
        var (model, _) = await ResolveAsync(h =>
        {
            h.Write("model/entities/comment.json", comment);
            h.Write("model/entities/part.json", part);
            Delete(h, "model/mappings/comment.json");
            Delete(h, "model/mappings/part.json");
            h.Write("model/mappings/assembly.json", EntityMapping(3, SAssembly, Assemblies, ",\"ignore\":true"));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, $$""","ends":[{"end":"{{AttachedToEnd}}","types":["""
                + $$"""{"entity":"{{SPart}}","foreignKey":"{{Fk(Comments, 1)}}"},{"entity":"{{SAssembly}}","foreignKey":"{{Fk(Comments, 2)}}"}]}]"""));
            h.Write("model/mappings/filed.json", RelationMapping(Filed, $$""","ends":[{"end":"{{FolderEnd}}","foreignKeys":["{{Fk(Parts, 1)}}"]}]""")
                .Replace(N(P, 990), N(P, 992), StringComparison.Ordinal));
        });
        Maquettiste.Engine.Tests.Editor.ContractCheck.Validate(model, "relationEndEntityRecord");

        // <endId>.<entityId> resolves to a key (a target's, as the mapping names it, or one that isn't a target: MQ4117); an entity the end
        // doesn't cover, and an end the entity doesn't hold, are MQ4045.
        var commentBinding = model.Entities.Single(e => e.Id == SComment).Bindings["main"];
        Assert.Equal([("id", null, null), ("attachedToPartId", "key", "Part"), ("attachedToAssemblyId", "key", "Assembly")],
            commentBinding.Fields.Take(3).Select(f => (f.Name, f.Holds, f.KeyOf?.Name)));
        Assert.All(commentBinding.Fields.Skip(3), f => Assert.Null(f.Holds)); // MQ4045's fields stay as written
        Assert.Equal(["Field 'attachedToAssemblyId' of the binding of entity 'Comment' to database 'main' names type 'Assembly' of end 'attachedTo', which isn't a target there: it isn't placed in the database, or its rows are in a covered base's table."],
            Messages(model, "MQ4117"));

        // <endId> on a one-type end the entity holds (way 1) maps its key column, holds nothing; <endId>.type on a one-type end and a to-many
        // end are MQ4045.
        var partBinding = model.Entities.Single(e => e.Id == SPart).Bindings["main"];
        Assert.Equal([("id", null), ("folderId", null)], partBinding.Fields.Take(2).Select(f => (f.Name, f.Holds)));
        var mq4045 = Messages(model, "MQ4045");
        foreach (var (entity, index, attribute) in new[]
        {
            ("Comment", 3, AttachedToEnd + "." + SFolder), ("Comment", 4, CommentEnd), ("Part", 2, FolderEnd + ".type"), ("Part", 3, CommentEnd),
        })
        {
            var expected = $"Field {index} of the binding of entity '{entity}' to database 'main' names '{attribute}', which is neither an attribute of the entity, "
                + "a member of one of its value object attributes, nor the end of a to-one relation of the entity.";
            Assert.True(mq4045.Contains(expected), expected + "\nin:\n" + string.Join("\n", mq4045));
        }

        Assert.Equal(4, mq4045.Count(m => m.StartsWith("Field ", StringComparison.Ordinal)));

        // With no mapping, the fields stand alone: no MQ4117.
        var (alone, _) = await ResolveAsync(h =>
        {
            h.Write("model/entities/comment.json", comment);
            Delete(h, "model/mappings/comment.json");
            h.Write("model/mappings/assembly.json", EntityMapping(3, SAssembly, Assemblies, ",\"ignore\":true"));
        });
        Assert.Empty(Messages(alone, "MQ4117"));
        Assert.Equal("Assembly", alone.Entities.Single(e => e.Id == SComment).Bindings["main"].Fields.Single(f => f.Name == "attachedToAssemblyId").KeyOf!.Name);
    }

    [Fact]
    public async Task The_records_and_the_database_view_carry_the_storage()
    {
        var attributes = $$""","attributes":[{"id":"{{NoteAttribute}}","name":"note","type":"string","length":40}]""";
        var body = $$""","junctionTable":"{{Links}}","ends":[{"end":"{{CommentEnd}}","foreignKey":"{{Fk(Links, 1)}}"},{"end":"{{AttachedToEnd}}","typeColumn":"{{Col(Links, 3)}}","idColumn":"{{Col(Links, 4)}}"}],"""
            + $$""" "attributes":[{"attribute":"{{NoteAttribute}}","column":"{{Col(Links, 5)}}"}],"constants":[{"column":"{{Col(Links, 6)}}","value":"AT"}]""";
        var (model, _) = await ResolveAsync(h =>
        {
            h.Write("model/mappings/attached.json", RelationMapping(Attached, body));
            h.Write("model/mappings/part.json", EntityMapping(2, SPart, Parts, ",\"discriminatorValue\":\"P\""));
        }, bindComment: true, attachedAttributes: attributes);
        var record = JsonSerializer.Serialize(Engine.ResolvedRecords.Entries(model, "relations", null).Single(e => e.Id == Attached).Project(), Web);
        foreach (var member in new[] { "\"links\":", "\"isHolder\":false", "\"referencedTable\":", "\"referencedColumns\":", "\"typeColumn\":", "\"codes\":[\"P\"]",
            "\"constants\":", "\"attributes\":", "\"typeCodes\":" })
            Assert.Contains(member, record, StringComparison.Ordinal);
        Assert.DoesNotContain("\"leftOut\"", record, StringComparison.Ordinal);
        Assert.DoesNotContain("\"notStored\"", record, StringComparison.Ordinal);

        var view = JsonSerializer.Serialize(DatabaseViews.From(model.Db("main")), Web);
        Assert.Contains("\"relationStorage\":", view, StringComparison.Ordinal);
        Assert.Contains($"\"relationId\":\"{Attached}\"", view, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"link-unique\",\"text\":\"CONSTRAINT uq_links_comment_id_target_type_target_id_relation_code UNIQUE", view, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"no-foreign-key\"}", view, StringComparison.Ordinal);

        var binding = JsonSerializer.Serialize(BindingViews.Record(model.Entities.Single(e => e.Id == SComment).Bindings["main"]), Web);
        Assert.Contains("\"holds\":\"type\"", binding, StringComparison.Ordinal);
        Assert.Contains("\"holds\":\"id\"", binding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_per_type_in_a_link_table_and_rows_a_table_cannot_tell_apart_are_gaps_too()
    {
        // Keys per type in the link table: duplicates can't be refused.
        var (perType, _) = await ResolveAsync(h =>
        {
            h.Write("model/databases/main/tables/links.json", Table(Links, "links",
                string.Join(",", Column(Links, 1, "id", nullable: false), Column(Links, 2, "comment_id"), Column(Links, 3, "part_id"), Column(Links, 4, "assembly_id")),
                string.Join(",", ForeignKey(Links, 1, "fk_links_comment", 2, Comments), ForeignKey(Links, 2, "fk_links_part", 3, Parts),
                    ForeignKey(Links, 3, "fk_links_assembly", 4, Assemblies))));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, $$""","junctionTable":"{{Links}}","ends":[{"end":"{{CommentEnd}}","foreignKey":"{{Fk(Links, 1)}}"},"""
                + $$"""{"end":"{{AttachedToEnd}}","types":[{"entity":"{{SPart}}","foreignKey":"{{Fk(Links, 2)}}"},{"entity":"{{SAssembly}}","foreignKey":"{{Fk(Links, 3)}}"}]}]"""));
        });
        Assert.Empty(Messages(perType, "MQ4117"));
        Assert.Equal(["count-check", "per-type-duplicates"], Kinds(perType, Attached));
        Assert.Contains("it can't refuse duplicate links in table 'links', since a unique over nullable key columns doesn't stop them in most databases",
            Assert.Single(Messages(perType, "MQ4118")), StringComparison.Ordinal);
        Assert.Null(perType.Db("main").RelationStorage.Single(n => n.Relation.Id == Attached).Gaps[1].Text);

        // Part and Assembly mapped to one designed table, with nothing to tell their rows apart.
        const string Items = "01JST300000000000000000007";
        var (shared, _) = await ResolveAsync(h =>
        {
            Delete(h, "model/relations/filed.json");
            h.Write("model/databases/main/tables/items.json", Table(Items, "items", Column(Items, 1, "id", nullable: false)));
            h.Write("model/mappings/part.json", EntityMapping(2, SPart, Items));
            h.Write("model/mappings/assembly.json", EntityMapping(3, SAssembly, Items));
            h.Write("model/mappings/attached.json", RelationMapping(Attached, Way3));
        });
        Assert.Equal(["no-foreign-key", "type-check", "rows-not-told-apart", "rows-not-told-apart"], Kinds(shared, Attached));
        Assert.Contains("nothing tells the rows of 'Part' apart from the other rows of table 'items'", Assert.Single(Messages(shared, "MQ4118")), StringComparison.Ordinal);
        // The join step into items then has no condition but the type column's.
        Assert.Equal(["comments(attached_id) > items(id) [attached_type in Part] to=Part", "comments(attached_id) > items(id) [attached_type in Assembly] to=Assembly"],
            NavigationOf(shared, SComment, "attachedTo").TypeJoins["main"].Select(Describe));
    }
}
