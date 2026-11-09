using System.Globalization;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The several-types storage model (<see cref="TypeStorageModel"/>, postgresql, with its relation mappings) beyond DDL
/// (several-types-design.md sections 19.9 and 19.16): csharp-dapper builds with warnings as errors while Comment, the holder of ways 2 to 5,
/// is bound to its table by a binding that maps the new field forms; and every way's join paths through resolution.
/// </summary>
public sealed class TypeStorageCodeTests
{
    private static string Id(int n) => TypeStorageModel.Id(n);

    /// <summary>Comment bound to comments by a binding: its id, a key per type (way 2), a type and an id (way 3), an id (way 4) and a key (way 5).</summary>
    private static void BindComment(PackRepo repo)
    {
        const int Comments = 600, Comment = 15, Part = 11, Assembly = 12;
        var fields = new JsonArray(
            Field(Id(900 + Comment), "id"),
            Field($"{Id(3002)}.{Id(Part)}", "part_id"),
            Field($"{Id(3002)}.{Id(Assembly)}", "assembly_id"),
            Field($"{Id(3012)}.type", "attached_type"),
            Field(Id(3012), "attached_id"),
            Field(Id(3022), "attached_uid"),
            Field(Id(3032), "item_id"));
        repo.EditJson(".maquettiste/model/entities/comment.json", entity => entity["bindings"] = new JsonArray(new JsonObject
        {
            ["id"] = Id(1990), ["database"] = Id(1), ["source"] = Id(Comments), ["fields"] = fields,
        }));
        File.Delete(repo.PathOf(".maquettiste/model/mappings/comment-in-db1.json"));

        static JsonObject Field(string attribute, string column) => new() { ["attribute"] = attribute, ["column"] = column };
    }

    [Fact]
    public async Task Csharp_dapper_builds_with_a_bound_holder_of_every_column_way()
    {
        using var repo = TypeStorageModel.Create("postgresql", relationMappings: true);
        PackRepo.CopyTree(Path.Combine(Maquettiste.Testing.Fixtures.RepoRoot, "packs", "csharp-dapper"), Path.Combine(repo.Repo.ModelRoot, "templates", "csharp-dapper"));
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"]!["allow"]!.AsArray().Add(new JsonObject { ["path"] = "src/Generated" });
            settings["packs"]!["csharp-dapper"] = new JsonObject { ["output"] = "src/Generated", ["parameters"] = new JsonObject { ["namespace"] = "Storage.Model" } };
        });
        // Keys are required, as an application's are, so their properties aren't nullable (the DDL goldens read the tables only).
        foreach (var entity in new[] { "item", "folder", "note", "comment" })
            repo.EditJson($".maquettiste/model/entities/{entity}.json", e => e["attributes"]![0]!["required"] = true);
        BindComment(repo);
        var result = await repo.GenerateCleanlyAsync();
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule is "MQ4045" or "MQ4116" or "MQ4117");

        // The binding's new fields are the entity's properties, read and written by its repository.
        var comment = string.Concat(Directory.EnumerateFiles(repo.PathOf("src/Generated"), "*.cs", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).StartsWith("Comment", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(File.ReadAllText));
        foreach (var name in new[] { "AttachedToPartId", "AttachedToAssemblyId", "SubjectType", "SubjectId", "AboutId", "ItemId" })
            Assert.Contains(name, comment, StringComparison.Ordinal);

        var project = Path.Combine(repo.Repo.Root, "compile");
        CompileTests.WriteProject(project, repo.PathOf("src/Generated"), program: null);
        await CompileTests.BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Every_ways_join_paths_through_resolution()
    {
        using var repo = TypeStorageModel.Create("postgresql", relationMappings: true);
        await using var store = new ModelStore(repo.Repo.Options);
        await store.LoadAsync(TestContext.Current.CancellationToken);
        var model = await new ModelResolver(repo.Repo.Options).ResolveAsync(store.Current!, null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(model.Diagnostics, d => d.Rule is "MQ4117" or "MQ4120");

        static string Describe(RJoinPath path) => string.Join(" | ", path.Steps.Select(s =>
            $"{s.FromTable.Name}({string.Join(',', s.FromColumns.Select(c => c.Name))}) > {s.ToTable.Name}({string.Join(',', s.ToColumns.Select(c => c.Name))})"
            + string.Concat(s.Conditions.Select(c => $" [{c.Column.Name} in {string.Join(',', c.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))}]"))))
            + (path.FromType is { } from ? " from=" + from.Name : "") + (path.ToType is { } to ? " to=" + to.Name : "");
        var lines = new List<string>();
        foreach (var relation in model.Relations.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            foreach (var navigation in model.Entities.SelectMany(e => e.Navigations).Where(n => ReferenceEquals(n.Relation, relation)).Distinct()
                .OrderBy(n => n.Name, StringComparer.Ordinal))
            {
                if (navigation.Joins.TryGetValue(TypeStorageModel.Database, out var one))
                    lines.Add($"{relation.Name}.{navigation.Name} joins: {Describe(one)}");
                foreach (var path in navigation.TypeJoins.GetValueOrDefault(TypeStorageModel.Database) ?? [])
                    lines.Add($"{relation.Name}.{navigation.Name} pair: {Describe(path)}");
            }
        }

        Assert.Equal(Expected, lines);
    }

    /// <summary>
    /// Every way's paths (decision 48): a path per pair of targets with the type column's codes and the shared link table's constants as
    /// conditions on the step they belong to; way 5 also with its one path in <c>joins</c>, with no per-target row condition.
    /// </summary>
    private static readonly string[] Expected =
    [
        "BaseKey.item joins: comments(item_id) > items(id)",
        "BaseKey.item pair: comments(item_id) > items(id) to=Part",
        "BaseKey.item pair: comments(item_id) > items(id) to=Assembly",
        "BaseKey.itemComments joins: items(id) > comments(item_id)",
        "BaseKey.itemComments pair: items(id) > comments(item_id) from=Part",
        "BaseKey.itemComments pair: items(id) > comments(item_id) from=Assembly",
        "BothEnds.sources pair: parts(id) > links(to_id) [to_type in Part] [relation_code in BothEnds] | links(from_id) > comments(id) [from_type in Comment] from=Part to=Comment",
        "BothEnds.sources pair: parts(id) > links(to_id) [to_type in Part] [relation_code in BothEnds] | links(from_id) > notes(id) [from_type in Note] from=Part to=Note",
        "BothEnds.sources pair: assemblies(id) > links(to_id) [to_type in Assembly] [relation_code in BothEnds] | links(from_id) > comments(id) [from_type in Comment] from=Assembly to=Comment",
        "BothEnds.sources pair: assemblies(id) > links(to_id) [to_type in Assembly] [relation_code in BothEnds] | links(from_id) > notes(id) [from_type in Note] from=Assembly to=Note",
        "BothEnds.targets pair: comments(id) > links(from_id) [from_type in Comment] [relation_code in BothEnds] | links(to_id) > parts(id) [to_type in Part] from=Comment to=Part",
        "BothEnds.targets pair: comments(id) > links(from_id) [from_type in Comment] [relation_code in BothEnds] | links(to_id) > assemblies(id) [to_type in Assembly] from=Comment to=Assembly",
        "BothEnds.targets pair: notes(id) > links(from_id) [from_type in Note] [relation_code in BothEnds] | links(to_id) > parts(id) [to_type in Part] from=Note to=Part",
        "BothEnds.targets pair: notes(id) > links(from_id) [from_type in Note] [relation_code in BothEnds] | links(to_id) > assemblies(id) [to_type in Assembly] from=Note to=Assembly",
        "BothIds.idSources pair: parts(id) > id_links(to_id) | id_links(from_id) > comments(id) from=Part to=Comment",
        "BothIds.idSources pair: parts(id) > id_links(to_id) | id_links(from_id) > notes(id) from=Part to=Note",
        "BothIds.idSources pair: assemblies(id) > id_links(to_id) | id_links(from_id) > comments(id) from=Assembly to=Comment",
        "BothIds.idSources pair: assemblies(id) > id_links(to_id) | id_links(from_id) > notes(id) from=Assembly to=Note",
        "BothIds.idTargets pair: comments(id) > id_links(from_id) | id_links(to_id) > parts(id) from=Comment to=Part",
        "BothIds.idTargets pair: comments(id) > id_links(from_id) | id_links(to_id) > assemblies(id) from=Comment to=Assembly",
        "BothIds.idTargets pair: notes(id) > id_links(from_id) | id_links(to_id) > parts(id) from=Note to=Part",
        "BothIds.idTargets pair: notes(id) > id_links(from_id) | id_links(to_id) > assemblies(id) from=Note to=Assembly",
        "FolderItems.folder pair: parts(folder_id) > folders(id) from=Part",
        "FolderItems.folder pair: assemblies(folder_id) > folders(id) from=Assembly",
        "FolderItems.items pair: folders(id) > parts(folder_id) to=Part",
        "FolderItems.items pair: folders(id) > assemblies(folder_id) to=Assembly",
        "LinkTable.linkTargets pair: comments(id) > comment_links(comment_id) | comment_links(target_id) > parts(id) [target_type in Part] to=Part",
        "LinkTable.linkTargets pair: comments(id) > comment_links(comment_id) | comment_links(target_id) > assemblies(id) [target_type in Assembly] to=Assembly",
        "LinkTable.linkedComments pair: parts(id) > comment_links(target_id) [target_type in Part] | comment_links(comment_id) > comments(id) from=Part",
        "LinkTable.linkedComments pair: assemblies(id) > comment_links(target_id) [target_type in Assembly] | comment_links(comment_id) > comments(id) from=Assembly",
        "PerType.attachedTo pair: comments(part_id) > parts(id) to=Part",
        "PerType.attachedTo pair: comments(assembly_id) > assemblies(id) to=Assembly",
        "PerType.perTypeComments pair: parts(id) > comments(part_id) from=Part",
        "PerType.perTypeComments pair: assemblies(id) > comments(assembly_id) from=Assembly",
        "PerTypeTables.tableComments pair: parts(id) > comment_parts(part_id) | comment_parts(comment_id) > comments(id) from=Part",
        "PerTypeTables.tableComments pair: assemblies(id) > comment_assemblies(assembly_id) | comment_assemblies(comment_id) > comments(id) from=Assembly",
        "PerTypeTables.tableParts pair: comments(id) > comment_parts(comment_id) | comment_parts(part_id) > parts(id) to=Part",
        "PerTypeTables.tableParts pair: comments(id) > comment_assemblies(comment_id) | comment_assemblies(assembly_id) > assemblies(id) to=Assembly",
        "Shared.refs pair: notes(id) > links(from_id) [from_type in Note] [relation_code in Shared] | links(to_id) > parts(id) [to_type in Part] to=Part",
        "Shared.refs pair: notes(id) > links(from_id) [from_type in Note] [relation_code in Shared] | links(to_id) > assemblies(id) [to_type in Assembly] to=Assembly",
        "Shared.sharedNotes pair: parts(id) > links(to_id) [to_type in Part] [relation_code in Shared] | links(from_id) > notes(id) [from_type in Note] from=Part",
        "Shared.sharedNotes pair: assemblies(id) > links(to_id) [to_type in Assembly] [relation_code in Shared] | links(from_id) > notes(id) [from_type in Note] from=Assembly",
        "TypeAndId.subject pair: comments(attached_id) > parts(id) [attached_type in Part] to=Part",
        "TypeAndId.subject pair: comments(attached_id) > assemblies(id) [attached_type in Assembly] to=Assembly",
        "TypeAndId.subjectComments pair: parts(id) > comments(attached_id) [attached_type in Part] from=Part",
        "TypeAndId.subjectComments pair: assemblies(id) > comments(attached_id) [attached_type in Assembly] from=Assembly",
        "UniqueId.about pair: comments(attached_uid) > parts(uid) to=Part",
        "UniqueId.about pair: comments(attached_uid) > assemblies(uid) to=Assembly",
        "UniqueId.aboutComment pair: parts(uid) > comments(attached_uid) from=Part",
        "UniqueId.aboutComment pair: assemblies(uid) > comments(attached_uid) from=Assembly",
    ];
}
