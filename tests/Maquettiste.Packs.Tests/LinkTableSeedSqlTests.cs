using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// sql-ddl's seed script for relation seeds into a designed link table whose mapping writes <c>constants</c> (several-types-design.md
/// decision 44 and answer 9): each row sets the constants, so the insert's key holds them and a second run inserts nothing, and each end's key
/// comes from the relation's own link. Uses alone, then Uses and Spares sharing <c>part_links</c> and its two keys, told apart by
/// <c>rel_code</c>; each key's end names one relation only. Goldens under <c>tests/fixtures/golden/sql-ddl/link-table-seeds</c>.
/// </summary>
public sealed class LinkTableSeedSqlTests
{
    private static string Id(int n) => "01JQTSQ" + n.ToString("D19", System.Globalization.CultureInfo.InvariantCulture);

    private static PackRepo Repo(bool shared)
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        repo.Write(".maquettiste/maquettiste.json", """
            {
              "formatVersion": 1,
              "name": "link-table-seeds",
              "outputs": { "allow": [ { "path": "db" } ] },
              "packs": { "sql-ddl": { "output": "db" } }
            }

            """);
        repo.Write(".maquettiste/model/packages/shop.json", $$"""{"kind":"package","id":"{{Id(1)}}","name":"Shop"}""");
        repo.Write(".maquettiste/model/databases/main/database.json", $$"""{"kind":"database","id":"{{Id(2)}}","name":"main","dialect":"postgresql","byConvention":"none"}""");
        foreach (var (entity, key, name, table, column, seed, row, value) in new[]
        {
            (Id(10), Id(11), "Part", Id(12), Id(13), Id(14), Id(15), "0193a3c2-0000-7000-8000-000000000001"),
            (Id(20), Id(21), "Tool", Id(22), Id(23), Id(24), Id(25), "0193a3c2-0000-7000-8000-000000000002"),
        })
        {
            var lower = name.ToLowerInvariant();
            repo.Write($".maquettiste/model/entities/{lower}.json", $$"""
                {"kind":"entity","id":"{{entity}}","name":"{{name}}","package":"{{Id(1)}}","key":{"attributes":["{{key}}"]},"attributes":[{"id":"{{key}}","name":"id","type":"uuid"}]}
                """);
            repo.Write($".maquettiste/model/databases/main/tables/{lower}s.json", $$"""
                {"kind":"table","id":"{{table}}","name":"{{lower}}s","database":"{{Id(2)}}","columns":[{"id":"{{column}}","name":"id","type":"uuid","nullable":false}],"primaryKey":{"columns":["{{column}}"]} }
                """);
            repo.Write($".maquettiste/model/mappings/{lower}-in-main.json", $$"""{"kind":"mapping","id":"{{Id(entity == Id(10) ? 16 : 26)}}","name":"{{lower}}","database":"{{Id(2)}}","entity":"{{entity}}","table":"{{table}}"}""");
            repo.Write($".maquettiste/model/seeds/{lower}/{lower}s.json", $$"""
                {"kind":"seed","id":"{{seed}}","name":"{{lower}}s","target":"{{entity}}","columns":["{{key}}"],"rows":[{"id":"{{row}}","values":["{{value}}"]}]}
                """);
        }

        repo.Write(".maquettiste/model/databases/main/tables/part-links.json", $$"""
            {"kind":"table","id":"{{Id(30)}}","name":"part_links","database":"{{Id(2)}}","columns":[
              {"id":"{{Id(31)}}","name":"part_id","type":"uuid","nullable":false},{"id":"{{Id(32)}}","name":"tool_id","type":"uuid","nullable":false},
              {"id":"{{Id(33)}}","name":"rel_code","type":"string","length":1,"nullable":false}],
             "primaryKey":{"columns":["{{Id(31)}}","{{Id(32)}}","{{Id(33)}}"]},
             "foreignKeys":[{"id":"{{Id(34)}}","name":"fk_part_links_part","columns":["{{Id(31)}}"],"referencesTable":"{{Id(12)}}"},
               {"id":"{{Id(35)}}","name":"fk_part_links_tool","columns":["{{Id(32)}}"],"referencesTable":"{{Id(22)}}"}]}
            """);
        foreach (var (name, code, n) in shared ? new[] { ("Uses", "U", 40), ("Spares", "S", 50) } : [("Uses", "U", 40)])
        {
            var lower = name.ToLowerInvariant();
            repo.Write($".maquettiste/model/relations/{lower}.json", $$"""
                {"kind":"relation","id":"{{Id(n)}}","name":"{{name}}","package":"{{Id(1)}}","ends":[
                  {"id":"{{Id(n + 1)}}","entity":"{{Id(10)}}","role":"part","navigation":"{{lower}}Parts","max":"*"},
                  {"id":"{{Id(n + 2)}}","entity":"{{Id(20)}}","role":"tool","navigation":"{{lower}}Tools","max":"*"}]}
                """);
            repo.Write($".maquettiste/model/mappings/{lower}-in-main.json", $$"""
                {"kind":"mapping","id":"{{Id(n + 3)}}","name":"{{lower}}","database":"{{Id(2)}}","relation":"{{Id(n)}}","junctionTable":"{{Id(30)}}",
                 "ends":[{"end":"{{Id(n + 1)}}","foreignKey":"{{Id(34)}}"},{"end":"{{Id(n + 2)}}","foreignKey":"{{Id(35)}}"}],
                 "constants":[{"column":"{{Id(33)}}","value":"{{code}}"}]}
                """);
            repo.Write($".maquettiste/model/seeds/{lower}/{lower}.json", $$"""
                {"kind":"seed","id":"{{Id(n + 4)}}","name":"{{lower}}","target":"{{Id(n)}}","columns":["{{Id(n + 1)}}","{{Id(n + 2)}}"],
                 "rows":[{"id":"{{Id(n + 5)}}","values":["{{Id(15)}}","{{Id(25)}}"]}]}
                """);
        }

        return repo;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Relation_seeds_into_a_link_table_with_constants_set_them_and_their_own_keys(bool shared)
    {
        using var repo = Repo(shared);
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Fixtures.Path("golden", "sql-ddl", "link-table-seeds", shared ? "shared" : "one"), repo.PathOf("db"));
        var seed = string.Concat(Directory.EnumerateFiles(repo.PathOf("db"), "seed*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(File.ReadAllText));
        Assert.Contains("INSERT INTO public.part_links (part_id, tool_id, rel_code) VALUES\n    ('0193a3c2-0000-7000-8000-000000000001', '0193a3c2-0000-7000-8000-000000000002', 'U')\nON CONFLICT DO NOTHING;",
            seed, StringComparison.Ordinal);
        if (shared)
        {
            Assert.Contains("INSERT INTO public.part_links (part_id, tool_id, rel_code) VALUES\n    ('0193a3c2-0000-7000-8000-000000000001', '0193a3c2-0000-7000-8000-000000000002', 'S')\nON CONFLICT DO NOTHING;",
                seed, StringComparison.Ordinal);
        }
    }
}
