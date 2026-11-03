using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// The shipped packs' own tests (next-phase-2-design.md section 5.4): each of <c>packs/sql-ddl</c>, <c>packs/csharp-dapper</c>,
/// <c>packs/process-docs</c> and <c>packs/seed-data</c> holds one small case, <c>billing-small</c>, and helper tests under its
/// <c>tests/</c> folder. The test copies the packs, <c>tests/</c> included, into a temporary project and runs
/// <c>maquettiste pack test</c>. With <c>MAQUETTISTE_UPDATE_GOLDEN=1</c> it runs <c>pack test --update</c> and copies each rewritten
/// <c>expected/</c> back under <c>packs/&lt;pack&gt;/tests/</c>; review the diff before keeping it.
/// </summary>
public sealed class ShippedPackTests
{
    private static readonly string[] Packs = ["csharp-dapper", "process-docs", "seed-data", "sql-ddl"];

    private const string Case = "billing-small";

    private static string PackFolder(string pack) => Path.Combine(Fixtures.RepoRoot, "packs", pack);

    [Fact]
    public async Task Pack_test_passes_for_every_shipped_pack()
    {
        using var repo = CliRepo.Empty();
        repo.Write(".maquettiste/maquettiste.json", """{ "formatVersion": 1, "name": "shipped-packs" }""" + "\n");
        foreach (var pack in Packs)
            CliRepo.CopyTree(PackFolder(pack), repo.PathOf(".maquettiste/templates/" + pack));

        if (Environment.GetEnvironmentVariable(Golden.UpdateVariable) == "1")
        {
            var update = await repo.RunAsync("pack", "test", "--update");
            Assert.True(update.ExitCode == 0, update.ToString());
            foreach (var pack in Packs)
            {
                var target = Path.Combine(PackFolder(pack), "tests", Case, "expected");
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                CliRepo.CopyTree(repo.PathOf($".maquettiste/templates/{pack}/tests/{Case}/expected"), target);
            }
        }

        var result = await repo.RunAsync("pack", "test", "--format", "json");

        Assert.True(result.ExitCode == 0, result.ToString());
        var report = JsonNode.Parse(result.Out)!;
        var packs = report["packs"]!.AsArray();
        Assert.Equal(Packs, packs.Select(p => (string)p!["pack"]!));
        foreach (var pack in packs)
        {
            var only = Assert.Single(pack!["cases"]!.AsArray());
            Assert.Equal((Case, "passed"), ((string)only!["name"]!, (string)only["outcome"]!));
            Assert.All(pack["tests"]!.AsArray(), t => Assert.Equal("passed", (string)t!["outcome"]!));
        }

        Assert.Equal(0, (int)report["failed"]!);
    }

    [Fact]
    public void Every_shipped_pack_has_a_case()
    {
        var shipped = Directory.EnumerateDirectories(Path.Combine(Fixtures.RepoRoot, "packs")).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(Packs, shipped);
        foreach (var pack in Packs)
        {
            var tests = Path.Combine(PackFolder(pack), "tests");
            Assert.Equal("* -text", File.ReadAllLines(Path.Combine(tests, ".gitattributes")).Last());
            Assert.Equal([Case], Directory.EnumerateDirectories(tests).Select(Path.GetFileName));
            Assert.True(File.Exists(Path.Combine(tests, Case, "maquettiste.json")), pack);
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(tests, Case, "model"), "*.json", SearchOption.AllDirectories));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(tests, Case, "expected"), "*", SearchOption.AllDirectories));
        }
    }
}
