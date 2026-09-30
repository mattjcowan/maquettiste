using System.Text.Json;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>The shape of the example packs themselves: manifests, documentation and file hygiene.</summary>
public sealed class PackLayoutTests
{
    public static TheoryData<string> PackNames => ["sql-ddl", "csharp-dapper", "process-docs"];

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
}
