using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The csharp-dapper <c>gitignore</c> unit (mode <c>block</c>, <c>createFile</c>) over the billing fixture: the file created with just
/// its block, a block added to a file the team has, the block updated with the team's lines kept, and the block removed. The
/// ignore file's bytes are compared with <c>tests/fixtures/golden/csharp-dapper/gitignore/&lt;case&gt;/gitignore.txt</c> (stored under
/// another name, so the golden is not itself an ignore file); <c>MAQUETTISTE_UPDATE_GOLDEN=1</c> rewrites them.
/// </summary>
public sealed class BlockGoldenTests
{
    private const string TeamLines = "bin/\nobj/\n";

    /// <summary>The billing fixture with csharp-dapper writing under <c>src</c>: its files in <c>src/Generated</c> as usual, and the
    /// ignore file <c>src/.gitignore</c> allowed as a file.</summary>
    private static PackRepo Repo(string gitignorePath = ".gitignore", string folder = "Generated")
    {
        var repo = PackRepo.Billing();
        Configure(repo, gitignorePath, folder);
        return repo;
    }

    private static void Configure(PackRepo repo, string gitignorePath, string folder) =>
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"] = new JsonObject
            {
                ["allow"] = new JsonArray(new JsonObject { ["path"] = "db" }, new JsonObject { ["path"] = "src/Generated" }, new JsonObject { ["path"] = "src/Gen" },
                    new JsonObject { ["path"] = "src/.gitignore" }),
            };
            settings["packs"]!["csharp-dapper"] = new JsonObject
            {
                ["output"] = "src",
                ["parameters"] = new JsonObject { ["generatedFolder"] = folder, ["partialFolder"] = folder, ["gitignorePath"] = gitignorePath },
            };
        });

    private static void AssertGolden(PackRepo repo, string @case)
    {
        using var temp = new TempDirectory();
        File.Copy(repo.PathOf("src/.gitignore"), Path.Combine(temp.Root, "gitignore.txt"));
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "gitignore", @case), temp.Root);
    }

    [Fact]
    public async Task The_ignore_file_is_created_with_just_the_block_and_the_rest_of_the_output_is_unchanged()
    {
        using var repo = Repo();

        var result = await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        Assert.Contains(result.Changes, c => c.Path == "src/.gitignore" && c.Kind == FileChangeKind.Added);
        AssertGolden(repo, "created");
        Assert.Contains("[\"src/.gitignore\", \"bc:", repo.Read(".maquettiste/manifest/csharp-dapper.json"), StringComparison.Ordinal);
        Golden.AssertMatches(Fixtures.Path("golden", "csharp-dapper", "billing"), repo.PathOf("src/Generated"));

        // A second run changes nothing; check agrees.
        var bytes = File.ReadAllBytes(repo.PathOf("src/.gitignore"));
        Assert.DoesNotContain((await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"])).Changes, c => c.Kind != FileChangeKind.Kept);
        Assert.Equal(bytes, File.ReadAllBytes(repo.PathOf("src/.gitignore")));
        await repo.GenerateCleanlyAsync(GenerationMode.Check, packs: ["csharp-dapper"]);

        // The parameter emptied: the block goes, and with it the file the engine created.
        Configure(repo, "", "Generated");
        var removed = await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.Contains(removed.Changes, c => c.Path == "src/.gitignore" && c.Kind == FileChangeKind.Deleted);
        Assert.False(File.Exists(repo.PathOf("src/.gitignore")));
    }

    [Fact]
    public async Task The_block_is_added_to_the_teams_file_updated_in_place_and_removed_leaving_the_teams_lines()
    {
        using var repo = Repo();
        repo.Write("src/.gitignore", TeamLines);

        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        AssertGolden(repo, "inserted");
        Assert.Contains("[\"src/.gitignore\", \"b:", repo.Read(".maquettiste/manifest/csharp-dapper.json"), StringComparison.Ordinal);

        // The team adds a line after the block; the generated folder moves: only the block's lines change.
        repo.Write("src/.gitignore", repo.Read("src/.gitignore") + "*.user\n");
        Configure(repo, ".gitignore", "Gen");
        var updated = await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.Contains(updated.Changes, c => c.Path == "src/.gitignore" && c.Kind == FileChangeKind.Modified);
        AssertGolden(repo, "updated");

        // The unit no longer writes it: the block goes, the team's lines stay, and so does the file.
        Configure(repo, "", "Gen");
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.Equal(TeamLines + "\n*.user\n", repo.Read("src/.gitignore"));
    }

    [Fact]
    public async Task A_generated_folder_that_is_the_files_own_folder_is_ignored_but_the_file_itself()
    {
        using var repo = PackRepo.Billing();
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"]!["allow"]!.AsArray().Add(new JsonObject { ["path"] = "src/Generated/.gitignore" });
            settings["packs"]!["csharp-dapper"]!["parameters"] = new JsonObject { ["gitignorePath"] = ".gitignore" };
        });

        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        Assert.Equal("# maquettiste: begin csharp-dapper/gitignore\n/*\n!/.gitignore\n# maquettiste: end csharp-dapper/gitignore\n", repo.Read("src/Generated/.gitignore"));
    }

    [Fact]
    public async Task Without_the_parameter_the_unit_writes_nothing()
    {
        using var repo = PackRepo.Billing();

        var result = await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        Assert.DoesNotContain(result.Changes, c => c.Path.EndsWith(".gitignore", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule is "MQ6028" or "MQ6011");
    }

    /// <summary>A temporary folder.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("mq-golden-").FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
