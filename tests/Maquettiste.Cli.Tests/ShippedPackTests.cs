using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// The shipped packs' own tests (next-phase-2-design.md section 5.4): each of <c>packs/sql-ddl</c>, <c>packs/csharp-dapper</c>,
/// <c>packs/process-docs</c> and <c>packs/seed-data</c> holds one small case, <c>billing-small</c>, and helper tests under its
/// <c>tests/</c> folder; csharp-dapper also holds <c>lifecycle-codes</c> (lifecycle-design.md section 9), and csharp-dapper and
/// process-docs hold <c>actors</c> (actors-design.md section 8: actor types, nested membership, the system actor, actors taken from the object, a subject owner and a scenario with participants)
/// and <c>verbs</c> (verbs-design.md section 8: built-in and own verbs on events, an automatic move and an operation, two hidden). The test copies the packs,
/// <c>tests/</c> included, into a temporary project and runs
/// <c>maquettiste pack test</c>. With <c>MAQUETTISTE_UPDATE_GOLDEN=1</c> it runs <c>pack test --update</c> and copies each rewritten
/// <c>expected/</c> back under <c>packs/&lt;pack&gt;/tests/</c>; review the diff before keeping it.
/// </summary>
public sealed class ShippedPackTests
{
    private static readonly string[] Packs = ["csharp-dapper", "process-docs", "seed-data", "sql-ddl"];

    private const string Case = "billing-small";

    private static string PackFolder(string pack) => Path.Combine(Fixtures.RepoRoot, "packs", pack);

    // The cases of a pack, by name: billing-small; actors, freezing, object-effects, rights, named-trigger-points, trigger-points and verbs for csharp-dapper and process-docs; lifecycle-codes for csharp-dapper.
    private static string[] CasesOf(string pack) => pack switch
    {
        "csharp-dapper" => ["actors", Case, "freezing", "lifecycle-codes", "named-trigger-points", "object-effects", "rights", "trigger-points", "verbs"],
        "process-docs" => ["actors", Case, "freezing", "named-trigger-points", "object-effects", "rights", "trigger-points", "verbs"],
        _ => [Case],
    };

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
                foreach (var name in CasesOf(pack))
                {
                    var target = Path.Combine(PackFolder(pack), "tests", name, "expected");
                    if (Directory.Exists(target))
                        Directory.Delete(target, recursive: true);
                    CliRepo.CopyTree(repo.PathOf($".maquettiste/templates/{pack}/tests/{name}/expected"), target);
                }
            }
        }

        var result = await repo.RunAsync("pack", "test", "--format", "json");

        Assert.True(result.ExitCode == 0, result.ToString());
        var report = JsonNode.Parse(result.Out)!;
        var packs = report["packs"]!.AsArray();
        Assert.Equal(Packs, packs.Select(p => (string)p!["pack"]!));
        foreach (var pack in packs)
        {
            var cases = pack!["cases"]!.AsArray();
            Assert.Equal(CasesOf((string)pack["pack"]!), cases.Select(c => (string)c!["name"]!));
            Assert.All(cases, c => Assert.Equal("passed", (string)c!["outcome"]!));
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
            Assert.Equal(CasesOf(pack), Directory.EnumerateDirectories(tests).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            foreach (var name in CasesOf(pack))
            {
                Assert.True(File.Exists(Path.Combine(tests, name, "maquettiste.json")), pack);
                Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(tests, name, "model"), "*.json", SearchOption.AllDirectories));
                Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(tests, name, "expected"), "*", SearchOption.AllDirectories));
            }
        }
    }
}
