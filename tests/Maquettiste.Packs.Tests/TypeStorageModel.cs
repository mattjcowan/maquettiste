using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The several-types storage model (several-types-design.md section 19.4): one database of designed tables that store every usual way a
/// relation with an end of several types is held, with the checks and uniques MQ4118 suggests written into the tables by the team, and a
/// relation mapping per way. Entities Item (key <c>id</c>), Part and Assembly (TPT under Item), Folder, Note and Comment, each bound to its
/// table. Ways: 1 a key on each type's table (FolderItems), 2 a column per type (PerType), 3 a type and an id (TypeAndId), 4 an id unique
/// across types (UniqueId), 5 a key to the shared base table (BaseKey), 6 a link table with an attribute column (LinkTable), 7 a table per type
/// (PerTypeTables), 8 one link table for several relations with constants (Shared), 9 both ends a type and an id (BothEnds), 10 both ends ids
/// unique across types (BothIds). Step 2 adds a type's column (comments.tool_id for a new type Tool in way 2) and drops another's
/// (comments.assembly_id). The DDL is the tables' own: sql-ddl changes no template for it (section 19.9).
/// </summary>
internal static class TypeStorageModel
{
    public const string Database = "db1";

    /// <summary>Dialects other than PostgreSQL write only the tables whose output differs by dialect: checks, uniques and the filtered index.</summary>
    public static readonly string[] OtherDialects = ["sqlserver", "mysql", "oracle", "sqlite"];

    public static string Id(int n) => "01TS" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>A repo with the sql-ddl pack writing to <c>db</c>, the model at step 1, in a dialect, with or without the relation mappings.</summary>
    public static PackRepo Create(string dialect, bool relationMappings)
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Maquettiste.Testing.Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        repo.Write(".maquettiste/maquettiste.json", Json(new JsonObject
        {
            ["$schema"] = ".schema/v1/maquettiste.json",
            ["formatVersion"] = 1,
            ["name"] = "type-storage",
            ["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "db" }) },
            ["packs"] = new JsonObject { ["sql-ddl"] = new JsonObject { ["output"] = "db" } },
        }));
        Write(repo, dialect, relationMappings, step: 1);
        return repo;
    }

    /// <summary>Replaces the model with its state at a step.</summary>
    public static void Write(PackRepo repo, string dialect, bool relationMappings, int step)
    {
        var folder = repo.PathOf(".maquettiste/model");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        var all = dialect == "postgresql";
        foreach (var (path, node) in Elements(dialect, all, relationMappings, step))
            repo.Write($".maquettiste/model/{path}.json", Json(node));
    }

    // ---- ids ----
    private const int Db = 1, Schema = 2, Package = 3;
    private const int Item = 10, Part = 11, Assembly = 12, Folder = 13, Note = 14, Comment = 15, Tool = 16;
    private const int Items = 100, Parts = 200, Assemblies = 300, Folders = 400, Notes = 500, Comments = 600, CommentLinks = 700, CommentParts = 800,
        CommentAssemblies = 1100, Links = 1200, IdLinks = 1300, Tools = 1400;

    private static IEnumerable<(string Path, JsonObject Node)> Elements(string dialect, bool all, bool relationMappings, int step)
    {
        yield return ("databases/db1/database", new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/database.json", ["kind"] = "database", ["id"] = Id(Db), ["name"] = Database, ["dialect"] = dialect,
            ["byConvention"] = "none", ["schemas"] = new JsonArray(new JsonObject { ["id"] = Id(Schema), ["name"] = "app" }),
        });
        yield return ("packages/sales", new JsonObject { ["$schema"] = "../../.schema/v1/package.json", ["kind"] = "package", ["id"] = Id(Package), ["name"] = "Sales" });

        // Entities, each with a uuid key, bound to its table; Part and Assembly derive from Item (TPT: parts.id and assemblies.id refer to items.id).
        var entities = new List<(int Id, string Name, int? Base, int Table)>
        {
            (Item, "Item", null, Items), (Part, "Part", Item, Parts), (Assembly, "Assembly", Item, Assemblies), (Folder, "Folder", null, Folders),
            (Note, "Note", null, Notes), (Comment, "Comment", null, Comments),
        };
        if (step == 2)
            entities.Add((Tool, "Tool", null, Tools));
        foreach (var (id, name, @base, table) in entities)
        {
            var entity = new JsonObject { ["$schema"] = "../../.schema/v1/entity.json", ["kind"] = "entity", ["id"] = Id(id), ["name"] = name, ["package"] = Id(Package) };
            if (@base is { } b)
                entity["base"] = Id(b);
            else
            {
                entity["key"] = new JsonObject { ["attributes"] = new JsonArray(Id(900 + id)) };
                entity["attributes"] = new JsonArray(new JsonObject { ["id"] = Id(900 + id), ["name"] = "id", ["type"] = "uuid" });
            }

            yield return ("entities/" + name.ToLowerInvariant(), entity);
            var mapping = Element("mapping", 1900 + id, name + " in db1", "../../");
            mapping["entity"] = Id(id);
            mapping["table"] = Id(table);
            if (id == Item)
                mapping["inheritance"] = "tpt";
            yield return ($"mappings/{name.ToLowerInvariant()}-in-db1", mapping);
        }

        foreach (var table in Tables(all, step))
            yield return ("databases/db1/tables/" + table["name"]!.GetValue<string>().Replace('_', '-'), table);

        foreach (var (relation, mapping) in Relations(all, step))
        {
            yield return ("relations/" + Kebab(relation["name"]!.GetValue<string>()), relation);
            if (relationMappings)
                yield return ("mappings/" + Kebab(relation["name"]!.GetValue<string>()) + "-in-db1", mapping);
        }
    }

    private static string Kebab(string name) => string.Concat(name.Select((ch, i) => char.IsUpper(ch) && i > 0 ? "-" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));

    private static JsonObject Element(string kind, int n, string name, string up = "../../../../")
    {
        var element = new JsonObject { ["$schema"] = up + $".schema/v1/{kind}.json", ["kind"] = kind, ["id"] = Id(n), ["name"] = name, ["database"] = Id(Db) };
        if (kind == "table")
            element["schema"] = Id(Schema);
        return element;
    }

    // Column n of table t (ids a hundred apart) is id t+n; keys, checks, uniques and indexes take ids from t+50.
    private static JsonObject Column(int id, string name, string type, bool nullable = true, int? length = null)
    {
        var column = new JsonObject { ["id"] = Id(id), ["name"] = name, ["type"] = type, ["nullable"] = nullable };
        if (length is { } l)
            column["length"] = l;
        return column;
    }

    private static JsonObject Table(int t, string name, JsonArray columns, params int[] primaryKey)
    {
        var table = Element("table", t, name);
        table["origin"] = "designed";
        table["columns"] = columns;
        table["primaryKey"] = new JsonObject { ["name"] = "pk_" + name, ["columns"] = new JsonArray([.. primaryKey.Select(c => (JsonNode)Id(c))]) };
        return table;
    }

    private static void Key(JsonObject table, int id, string name, int column, int referencedTable, int referencedColumn)
    {
        var keys = table["foreignKeys"] as JsonArray ?? [];
        keys.Add(new JsonObject
        {
            ["id"] = Id(id), ["name"] = name, ["columns"] = new JsonArray(Id(column)), ["referencesTable"] = Id(referencedTable),
            ["referencesColumns"] = new JsonArray(Id(referencedColumn)),
        });
        table["foreignKeys"] = keys;
    }

    private static void Check(JsonObject table, int id, string name, string expression)
    {
        var checks = table["checks"] as JsonArray ?? [];
        checks.Add(new JsonObject { ["id"] = Id(id), ["name"] = name, ["expression"] = new JsonObject { ["*"] = expression } });
        table["checks"] = checks;
    }

    private static void Unique(JsonObject table, int id, string name, params int[] columns)
    {
        var uniques = table["uniques"] as JsonArray ?? [];
        uniques.Add(new JsonObject { ["id"] = Id(id), ["name"] = name, ["columns"] = new JsonArray([.. columns.Select(c => (JsonNode)Id(c))]) });
        table["uniques"] = uniques;
    }

    private static IEnumerable<JsonObject> Tables(bool all, int step)
    {
        yield return Table(Items, "items", [Column(Items + 1, "id", "uuid", false)], Items + 1);
        foreach (var (t, name, singular) in new[] { (Parts, "parts", "part"), (Assemblies, "assemblies", "assembly") })
        {
            var table = Table(t, name, [Column(t + 1, "id", "uuid", false), Column(t + 2, "uid", "uuid", false), Column(t + 3, "folder_id", "uuid")], t + 1);
            Key(table, t + 50, $"fk_{name}_item", t + 1, Items, Items + 1);
            Key(table, t + 51, $"fk_{name}_folder", t + 3, Folders, Folders + 1);
            Unique(table, t + 52, $"uq_{name}_uid", t + 2);
            yield return table;
        }

        yield return Table(Folders, "folders", [Column(Folders + 1, "id", "uuid", false)], Folders + 1);
        yield return Table(Notes, "notes", [Column(Notes + 1, "id", "uuid", false)], Notes + 1);
        if (step == 2)
            yield return Table(Tools, "tools", [Column(Tools + 1, "id", "uuid", false)], Tools + 1);

        // comments: ways 2 (a key per type, with the team's count check), 3 (a type and an id, with the team's code check), 4 (an id unique
        // across types, one-to-one: a unique filtered to set rows) and 5 (a key to items).
        var c = Comments;
        var columns = new JsonArray(Column(c + 1, "id", "uuid", false), Column(c + 2, "part_id", "uuid"));
        if (step == 1)
            columns.Add(Column(c + 3, "assembly_id", "uuid"));
        if (step == 2)
            columns.Add(Column(c + 9, "tool_id", "uuid"));
        columns.Add(Column(c + 4, "attached_type", "string", length: 20));
        columns.Add(Column(c + 5, "attached_id", "uuid"));
        columns.Add(Column(c + 6, "attached_uid", "uuid"));
        columns.Add(Column(c + 7, "item_id", "uuid"));
        var comments = Table(c, "comments", columns, c + 1);
        Key(comments, c + 50, "fk_comments_part", c + 2, Parts, Parts + 1);
        if (step == 1)
            Key(comments, c + 51, "fk_comments_assembly", c + 3, Assemblies, Assemblies + 1);
        else
            Key(comments, c + 59, "fk_comments_tool", c + 9, Tools, Tools + 1);
        Key(comments, c + 52, "fk_comments_item", c + 7, Items, Items + 1);
        Check(comments, c + 53, "ck_comments_attached_to", step == 1 ? "part_id IS NULL OR assembly_id IS NULL" : "part_id IS NULL OR tool_id IS NULL");
        Check(comments, c + 54, "ck_comments_attached_type", "attached_type IN ('Assembly', 'Part')");
        comments["indexes"] = new JsonArray(new JsonObject
        {
            ["id"] = Id(c + 55), ["name"] = "ux_comments_attached_uid", ["columns"] = new JsonArray(new JsonObject { ["column"] = Id(c + 6) }), ["unique"] = true,
            ["where"] = "attached_uid IS NOT NULL",
        });
        yield return comments;

        // links: ways 8 and 9 share it, told apart by relation_code; the unique covers the constants' column.
        var l = Links;
        var links = Table(l, "links", [Column(l + 1, "id", "uuid", false), Column(l + 2, "relation_code", "string", false, 30), Column(l + 3, "from_type", "string", false, 20),
            Column(l + 4, "from_id", "uuid", false), Column(l + 5, "to_type", "string", false, 20), Column(l + 6, "to_id", "uuid", false), Column(l + 7, "note", "string", length: 200)], l + 1);
        Unique(links, l + 50, "uq_links", l + 2, l + 3, l + 4, l + 5, l + 6);
        Check(links, l + 51, "ck_links_to_type", "to_type IN ('Assembly', 'Part')");
        yield return links;
        if (!all)
            yield break;

        // comment_links: way 6, a key to comments and a type and an id, with an attribute column.
        var cl = CommentLinks;
        var commentLinks = Table(cl, "comment_links", [Column(cl + 1, "comment_id", "uuid", false), Column(cl + 2, "target_type", "string", false, 20),
            Column(cl + 3, "target_id", "uuid", false), Column(cl + 4, "note", "string", length: 200)], cl + 1, cl + 2, cl + 3);
        Key(commentLinks, cl + 50, "fk_comment_links_comment", cl + 1, Comments, Comments + 1);
        Check(commentLinks, cl + 51, "ck_comment_links_target_type", "target_type IN ('Assembly', 'Part')");
        yield return commentLinks;

        // comment_parts and comment_assemblies: way 7, a table per type.
        foreach (var (t, name, other, otherTable) in new[] { (CommentParts, "comment_parts", "part", Parts), (CommentAssemblies, "comment_assemblies", "assembly", Assemblies) })
        {
            var table = Table(t, name, [Column(t + 1, "comment_id", "uuid", false), Column(t + 2, other + "_id", "uuid", false)], t + 1, t + 2);
            Key(table, t + 50, $"fk_{name}_comment", t + 1, Comments, Comments + 1);
            Key(table, t + 51, $"fk_{name}_{other}", t + 2, otherTable, otherTable + 1);
            yield return table;
        }

        // id_links: way 10, ids unique across types at both ends.
        yield return Table(IdLinks, "id_links", [Column(IdLinks + 1, "from_id", "uuid", false), Column(IdLinks + 2, "to_id", "uuid", false)], IdLinks + 1, IdLinks + 2);
    }

    private static JsonObject End(int id, string role, string navigation, string max, params int[] types)
    {
        var end = new JsonObject { ["id"] = Id(id), ["role"] = role, ["navigation"] = navigation, ["max"] = max == "1" ? 1 : max };
        if (types.Length == 1)
            end["entity"] = Id(types[0]);
        else
            end["types"] = new JsonArray([.. types.Select(t => (JsonNode)new JsonObject { ["entity"] = Id(t) })]);
        return end;
    }

    private static IEnumerable<(JsonObject Relation, JsonObject Mapping)> Relations(bool all, int step)
    {
        var both = step == 1 ? new[] { Part, Assembly } : [Part, Tool];
        (JsonObject, JsonObject) Relation(int n, string name, JsonObject first, JsonObject second, JsonArray ends, Action<JsonObject>? more = null)
        {
            var relation = new JsonObject
            {
                ["$schema"] = "../../.schema/v1/relation.json", ["kind"] = "relation", ["id"] = Id(n), ["name"] = name, ["package"] = Id(Package),
                ["ends"] = new JsonArray(first, second),
            };
            var mapping = Element("mapping", n + 1, name + " in db1", "../../");
            mapping["relation"] = Id(n);
            more?.Invoke(mapping);
            mapping["ends"] = ends;
            return (relation, mapping);
        }

        JsonObject Entry(int end, params (string Key, JsonNode Value)[] keys)
        {
            var entry = new JsonObject { ["end"] = Id(end) };
            foreach (var (key, value) in keys)
                entry[key] = value;
            return entry;
        }

        JsonArray Ids(params int[] ns) => [.. ns.Select(x => (JsonNode)Id(x))];
        JsonArray Types(params (int Entity, string Key, int Id)[] items) => [.. items.Select(i => (JsonNode)new JsonObject { ["entity"] = Id(i.Entity), [i.Key] = Id(i.Id) })];

        // 2: a column per type (the step 2 model swaps Assembly's for Tool's).
        yield return Relation(3000, "PerType", End(3001, "comment", "perTypeComments", "*", Comment), End(3002, "attachedTo", "attachedTo", "1", both),
            [Entry(3002, ("types", step == 1
                ? Types((Part, "foreignKey", Comments + 50), (Assembly, "foreignKey", Comments + 51))
                : Types((Part, "foreignKey", Comments + 50), (Tool, "foreignKey", Comments + 59))))]);
        // 3: a type and an id.
        yield return Relation(3010, "TypeAndId", End(3011, "comment", "subjectComments", "*", Comment), End(3012, "subject", "subject", "1", Part, Assembly),
            [Entry(3012, ("typeColumn", Id(Comments + 4)), ("idColumn", Id(Comments + 5)))]);
        // 4: an id unique across types, each referring to its table's uid; one-to-one.
        yield return Relation(3020, "UniqueId", End(3021, "comment", "aboutComment", "1", Comment), End(3022, "about", "about", "1", Part, Assembly),
            [Entry(3022, ("idColumn", Id(Comments + 6)), ("types", Types((Part, "referencesColumn", Parts + 2), (Assembly, "referencesColumn", Assemblies + 2))))]);
        // 5: a key to the shared base table.
        yield return Relation(3030, "BaseKey", End(3031, "comment", "itemComments", "*", Comment), End(3032, "item", "item", "1", Part, Assembly),
            [Entry(3032, ("foreignKey", Id(Comments + 52)))]);
        // 8: one link table for several relations; the note end's one type has a type column too.
        yield return Relation(3070, "Shared", End(3071, "note", "sharedNotes", "*", Note), End(3072, "refs", "refs", "*", Part, Assembly),
            [Entry(3071, ("typeColumn", Id(Links + 3)), ("idColumn", Id(Links + 4))), Entry(3072, ("typeColumn", Id(Links + 5)), ("idColumn", Id(Links + 6)))],
            m =>
            {
                m["junctionTable"] = Id(Links);
                m["constants"] = new JsonArray(new JsonObject { ["column"] = Id(Links + 2), ["value"] = "Shared" });
            });
        // 9: both ends a type and an id, with an attribute column, in the same link table.
        var (bothEnds, bothMapping) = Relation(3080, "BothEnds", End(3081, "source", "sources", "*", Comment, Note), End(3082, "target", "targets", "*", Part, Assembly),
            [Entry(3081, ("typeColumn", Id(Links + 3)), ("idColumn", Id(Links + 4))), Entry(3082, ("typeColumn", Id(Links + 5)), ("idColumn", Id(Links + 6)))],
            m =>
            {
                m["junctionTable"] = Id(Links);
                m["constants"] = new JsonArray(new JsonObject { ["column"] = Id(Links + 2), ["value"] = "BothEnds" });
                m["attributes"] = new JsonArray(new JsonObject { ["attribute"] = Id(3089), ["column"] = Id(Links + 7) });
            });
        bothEnds["attributes"] = new JsonArray(new JsonObject { ["id"] = Id(3089), ["name"] = "note", ["type"] = "string", ["length"] = 200 });
        yield return (bothEnds, bothMapping);
        if (!all)
            yield break;

        // 1: a key on each type's table.
        yield return Relation(3040, "FolderItems", End(3041, "folder", "folder", "1", Folder), End(3042, "items", "items", "*", Part, Assembly),
            [Entry(3041, ("foreignKeys", Ids(Parts + 51, Assemblies + 51)))]);
        // 6: a link table, with an attribute column.
        var (linkTable, linkMapping) = Relation(3050, "LinkTable", End(3051, "comment", "linkedComments", "*", Comment), End(3052, "targets", "linkTargets", "*", Part, Assembly),
            [Entry(3051, ("foreignKey", Id(CommentLinks + 50))), Entry(3052, ("typeColumn", Id(CommentLinks + 2)), ("idColumn", Id(CommentLinks + 3)))],
            m =>
            {
                m["junctionTable"] = Id(CommentLinks);
                m["attributes"] = new JsonArray(new JsonObject { ["attribute"] = Id(3059), ["column"] = Id(CommentLinks + 4) });
            });
        linkTable["attributes"] = new JsonArray(new JsonObject { ["id"] = Id(3059), ["name"] = "note", ["type"] = "string", ["length"] = 200 });
        yield return (linkTable, linkMapping);
        // 7: a table per type.
        yield return Relation(3060, "PerTypeTables", End(3061, "comment", "tableComments", "*", Comment), End(3062, "parts", "tableParts", "*", Part, Assembly),
            [Entry(3061, ("foreignKeys", Ids(CommentParts + 50, CommentAssemblies + 50))),
             Entry(3062, ("types", Types((Part, "foreignKey", CommentParts + 51), (Assembly, "foreignKey", CommentAssemblies + 51))))]);
        // 10: both ends ids unique across types.
        yield return Relation(3090, "BothIds", End(3091, "source", "idSources", "*", Comment, Note), End(3092, "target", "idTargets", "*", Part, Assembly),
            [Entry(3091, ("idColumn", Id(IdLinks + 1))), Entry(3092, ("idColumn", Id(IdLinks + 2)))], m => m["junctionTable"] = Id(IdLinks));
    }
}
