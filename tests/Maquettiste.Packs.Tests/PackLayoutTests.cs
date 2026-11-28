using System.Text.Json;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>The shape of the example packs themselves: manifests, documentation and file hygiene.</summary>
public sealed class PackLayoutTests
{
    public static TheoryData<string> PackNames => ["sql-ddl", "csharp-dapper", "process-docs", "seed-data"];

    [Fact]
    public void Packs_folder_names_the_two_phase_1_example_packs()
    {
        var readme = File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "packs", "README.md"));

        Assert.Contains("sql-ddl", readme, StringComparison.Ordinal);
        Assert.Contains("csharp-dapper", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_manifest_schema_is_embedded()
    {
        Assert.Contains("pack.json", TestServices.Schemas.FileNames);
    }

    [Theory]
    [MemberData(nameof(PackNames))]
    public void Each_pack_is_named_after_its_folder_and_documents_every_parameter(string pack)
    {
        var folder = Path.Combine(Fixtures.RepoRoot, "packs", pack);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "pack.json")));
        Assert.Equal(pack, manifest.RootElement.GetProperty("name").GetString());
        var readme = File.ReadAllText(Path.Combine(folder, "README.md"));
        foreach (var parameter in manifest.RootElement.GetProperty("parameters").EnumerateObject())
            Assert.Contains("`" + parameter.Name + "`", readme, StringComparison.Ordinal);
        foreach (var unit in manifest.RootElement.GetProperty("units").EnumerateArray())
        {
            Assert.Contains("`" + unit.GetProperty("id").GetString() + "`", readme, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(folder, unit.GetProperty("template").GetString()!)));
        }
    }

    [Theory]
    [MemberData(nameof(PackNames))]
    public void Pack_files_are_utf8_with_lf_line_endings_and_no_trailing_whitespace(string pack)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Fixtures.RepoRoot, "packs", pack), "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, file + " starts with a BOM");
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
            if (Path.GetExtension(file) != ".md")
            {
                var lines = File.ReadAllLines(file);
                Assert.All(lines, line => Assert.False(line.EndsWith(' ') || line.EndsWith('\t'), file + ": trailing whitespace in '" + line + "'"));
            }
        }
    }

    [Fact]
    public async Task Unresolved_cells_are_listed_in_the_manifest()
    {
        // next-phase-2-design.md section 3.4: an invoice names its customer by email, and the database fills customers.id.
        using var repo = TableSeedSqlTests.WithSeedData(TableSeedSqlTests.NaturalKeyRepo(identity: true), "csv");
        await repo.GenerateCleanlyAsync(packs: ["seed-data"]);

        var manifest = System.Text.Json.Nodes.JsonNode.Parse(repo.Read("data/main/manifest.json"))!;
        var unresolved = manifest["unresolved"]!.AsArray();
        Assert.Equal(3, unresolved.Count);
        var first = unresolved[0]!;
        Assert.Equal(("invoices", "sales", TableSeedSqlTests.N(31), 0, "customer_id"),
            ((string)first["table"]!, (string)first["schema"]!, (string)first["row"]!, (int)first["index"]!, (string)first["column"]!));
        Assert.Equal(("customers", "sales", "id"), ((string)first["referencedTable"]!, (string)first["referencedSchema"]!, (string)first["referencedColumn"]!));
        Assert.Equal(["email"], first["keyColumns"]!.AsArray().Select(k => (string)k!));
        Assert.Equal(["ada@example.com"], first["key"]!.AsArray().Select(k => (string)k!));
        Assert.Equal("grace@example.com", (string)unresolved[1]!["key"]![0]!);

        // The cell itself is left empty.
        var invoices = repo.Read("data/main/sales.invoices.csv").ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("id,number,issued_on,customer_id", invoices[0]);
        Assert.EndsWith(",INV-1,2026-01-05,", invoices[1], StringComparison.Ordinal);

        // Known values leave no list.
        using var known = TableSeedSqlTests.WithSeedData(TableSeedSqlTests.NaturalKeyRepo(identity: false), "json");
        await known.GenerateCleanlyAsync(packs: ["seed-data"]);
        Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(known.Read("data/main/seed-data.json"))!["unresolved"]);
    }
}
