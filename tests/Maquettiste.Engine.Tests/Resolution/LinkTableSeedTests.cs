using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Loading;
using Maquettiste.Engine.Tests.Processes;
using static Maquettiste.Engine.Tests.Validation.SeveralTypesKit;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Relation seeds into a designed link table whose mapping writes <c>constants</c> (several-types-design.md decision 44 and answer 9): each row
/// sets the constants, so it is one of its relation's links and its key holds the constants' columns, and each end's key comes from that
/// relation's own entries. Uses and Spares (Part to Tool, many to many) share <c>part_links</c> and its two keys, told apart by
/// <c>rel_code</c> 'U' and 'S'; each key's end names only the relation resolved last, so the other relation's rows used to lose their keys.
/// </summary>
public sealed class LinkTableSeedTests
{
    private const string Db = "01JQTS00000000000000000001", SPart = "01JQTS00000000000000000002", STool = "01JQTS00000000000000000003";
    private const string PartId = "01JQTS00000000000000000004", ToolId = "01JQTS00000000000000000005";
    private const string Parts = "01JQTS00000000000000000010", PartsId = "01JQTS00000000000000000011";
    private const string Tools = "01JQTS00000000000000000020", ToolsId = "01JQTS00000000000000000021";
    private const string Links = "01JQTS00000000000000000030", LinkPart = "01JQTS00000000000000000031", LinkTool = "01JQTS00000000000000000032",
        LinkCode = "01JQTS00000000000000000033", FkPart = "01JQTS00000000000000000034", FkTool = "01JQTS00000000000000000035";
    private const string Uses = "01JQTS00000000000000000040", UsesPart = "01JQTS00000000000000000041", UsesTool = "01JQTS00000000000000000042";
    private const string Spares = "01JQTS00000000000000000050", SparesPart = "01JQTS00000000000000000051", SparesTool = "01JQTS00000000000000000052";

    private static Task<(LoaderHarness Harness, ModelStore Store)> OpenAsync(bool shared) => ActorKit.OpenAsync(h =>
    {
        h.Write("model/databases/links/database.json", $$"""{"kind":"database","id":"{{Db}}","name":"links","dialect":"postgresql","byConvention":"none"}""");
        h.Write("model/entities/lt-part.json", $$"""
            {"kind":"entity","id":"{{SPart}}","name":"LtPart","package":"{{Sales}}","key":{"attributes":["{{PartId}}"]},"attributes":[{"id":"{{PartId}}","name":"id","type":"uuid"}]}
            """);
        h.Write("model/entities/lt-tool.json", $$"""
            {"kind":"entity","id":"{{STool}}","name":"LtTool","package":"{{Sales}}","key":{"attributes":["{{ToolId}}"]},"attributes":[{"id":"{{ToolId}}","name":"id","type":"uuid"}]}
            """);
        h.Write("model/databases/links/tables/parts.json", $$"""
            {"kind":"table","id":"{{Parts}}","name":"parts","database":"{{Db}}","columns":[{"id":"{{PartsId}}","name":"id","type":"uuid","nullable":false}],"primaryKey":{"columns":["{{PartsId}}"]} }
            """);
        h.Write("model/databases/links/tables/tools.json", $$"""
            {"kind":"table","id":"{{Tools}}","name":"tools","database":"{{Db}}","columns":[{"id":"{{ToolsId}}","name":"id","type":"uuid","nullable":false}],"primaryKey":{"columns":["{{ToolsId}}"]} }
            """);
        h.Write("model/databases/links/tables/part-links.json", $$"""
            {"kind":"table","id":"{{Links}}","name":"part_links","database":"{{Db}}","columns":[
              {"id":"{{LinkPart}}","name":"part_id","type":"uuid","nullable":false},{"id":"{{LinkTool}}","name":"tool_id","type":"uuid","nullable":false},
              {"id":"{{LinkCode}}","name":"rel_code","type":"string","length":1,"nullable":false}],
             "primaryKey":{"columns":["{{LinkPart}}","{{LinkTool}}","{{LinkCode}}"]},
             "foreignKeys":[{"id":"{{FkPart}}","name":"fk_part_links_part","columns":["{{LinkPart}}"],"referencesTable":"{{Parts}}"},
               {"id":"{{FkTool}}","name":"fk_part_links_tool","columns":["{{LinkTool}}"],"referencesTable":"{{Tools}}"}]}
            """);
        h.Write("model/mappings/lt-part-in-links.json", $$"""{"kind":"mapping","id":"01JQTS00000000000000000060","name":"part","database":"{{Db}}","entity":"{{SPart}}","table":"{{Parts}}"}""");
        h.Write("model/mappings/lt-tool-in-links.json", $$"""{"kind":"mapping","id":"01JQTS00000000000000000061","name":"tool","database":"{{Db}}","entity":"{{STool}}","table":"{{Tools}}"}""");
        foreach (var (relation, name, part, tool, code, n) in Relations(shared))
        {
            h.Write($"model/relations/{name.ToLowerInvariant()}.json", $$"""
                {"kind":"relation","id":"{{relation}}","name":"{{name}}","package":"{{Sales}}","ends":[
                  {"id":"{{part}}","entity":"{{SPart}}","role":"part","navigation":"{{name.ToLowerInvariant()}}Parts","max":"*"},
                  {"id":"{{tool}}","entity":"{{STool}}","role":"tool","navigation":"{{name.ToLowerInvariant()}}Tools","max":"*"}]}
                """);
            h.Write($"model/mappings/{name.ToLowerInvariant()}-in-links.json", $$"""
                {"kind":"mapping","id":"01JQTS0000000000000000007{{n}}","name":"{{name}}","database":"{{Db}}","relation":"{{relation}}","junctionTable":"{{Links}}",
                 "ends":[{"end":"{{part}}","foreignKey":"{{FkPart}}"},{"end":"{{tool}}","foreignKey":"{{FkTool}}"}],
                 "constants":[{"column":"{{LinkCode}}","value":"{{code}}"}]}
                """);
            h.Write($"model/seeds/{name.ToLowerInvariant()}/{name.ToLowerInvariant()}.json", $$"""
                {"kind":"seed","id":"01JQTS0000000000000000008{{n}}","name":"{{name}}","target":"{{relation}}","columns":["{{part}}","{{tool}}"],
                 "rows":[{"id":"01JQTS000000000000000008{{n}}1","values":["01JQTS00000000000000000901","01JQTS00000000000000000911"]}]}
                """);
        }

        h.Write("model/seeds/lt-part/parts.json", $$"""
            {"kind":"seed","id":"01JQTS00000000000000000900","name":"parts","target":"{{SPart}}","columns":["{{PartId}}"],
             "rows":[{"id":"01JQTS00000000000000000901","values":["0193a3c2-0000-7000-8000-000000000001"]}]}
            """);
        h.Write("model/seeds/lt-tool/tools.json", $$"""
            {"kind":"seed","id":"01JQTS00000000000000000910","name":"tools","target":"{{STool}}","columns":["{{ToolId}}"],
             "rows":[{"id":"01JQTS00000000000000000911","values":["0193a3c2-0000-7000-8000-000000000002"]}]}
            """);
    });

    private static IEnumerable<(string Relation, string Name, string Part, string Tool, string Code, int N)> Relations(bool shared)
    {
        yield return (Uses, "Uses", UsesPart, UsesTool, "U", 1);
        if (shared)
            yield return (Spares, "Spares", SparesPart, SparesTool, "S", 2);
    }

    private static async Task<RSeedTable> LinksAsync(bool shared)
    {
        var (h, store) = await OpenAsync(shared);
        using (h)
        await using (store)
        {
            var model = store.Current!;
            var found = await ActorKit.ValidateAsync(h, model);
            Assert.DoesNotContain(found, d => d.Severity == Diagnostics.DiagnosticSeverity.Error && d.ElementId?.StartsWith("01JQTS", StringComparison.Ordinal) == true);
            return ResolutionKit.Resolve(model).Databases.Single(d => d.Name == "links").SeedTables.Single(t => t.Table.Name == "part_links");
        }
    }

    [Fact]
    public async Task A_one_type_relation_seed_into_a_link_table_with_constants_writes_the_constants()
    {
        var links = await LinksAsync(shared: false);
        var row = Assert.Single(links.Rows);
        Assert.Equal(
            [("part_id", (object?)"0193a3c2-0000-7000-8000-000000000001"), ("rel_code", "U"), ("tool_id", "0193a3c2-0000-7000-8000-000000000002")],
            row.Values.Select(v => (v.Key, v.Value is Guid g ? g.ToString() : v.Value)));
        // The row key holds the constant's column, so a load that inserts once finds the row by it.
        Assert.Equal(["part_id", "tool_id", "rel_code"], links.KeyColumns.Select(c => c.Name));
        Assert.DoesNotContain(row.Key, v => v is null);
    }

    [Fact]
    public async Task Two_relations_sharing_one_link_table_each_write_their_own_keys_and_constants()
    {
        var links = await LinksAsync(shared: true);
        Assert.Equal(2, links.Rows.Count);
        foreach (var (row, code) in links.Rows.OrderBy(r => r.Id, StringComparer.Ordinal).Zip(["U", "S"]))
        {
            Assert.Equal(["part_id", "rel_code", "tool_id"], row.Values.Keys);
            Assert.Equal(code, row.Values["rel_code"]);
            Assert.DoesNotContain(row.Values.Values, v => v is null);
        }
    }
}
