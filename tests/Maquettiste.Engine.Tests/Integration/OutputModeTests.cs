using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Output modes through Scriban and the writer (integration task 11; engine-design.md section 8): regions keep hand-written bodies
/// across regeneration, pair writes its companion once, once never overwrites.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class OutputModeTests
{
    private const string Regions = "db/e2e/regions/customer.txt";

    [Fact]
    public async Task Regions_mode_keeps_a_hand_written_region_across_regeneration()
    {
        await using var repo = await CheckTests.AppliedAsync();
        var edited = repo.Repo.ReadFile(Regions).Replace("// default body", "// kept by hand\nvar answer = 42;", StringComparison.Ordinal);
        repo.Repo.WriteFile(Regions, edited);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check)); // an edit inside a region is no hand edit

        await AddAttributeAsync(repo);
        var result = await repo.ApplyAsync();

        Assert.Contains(result.Changes, c => c.Path == Regions && c.Kind == FileChangeKind.Modified);
        Assert.Equal("header Customer (7 attributes)\n// maquettiste:keep id=body\n// kept by hand\nvar answer = 42;\n// maquettiste:end-keep\nfooter\n",
            repo.Repo.ReadFile(Regions));
        var forced = await repo.ApplyAsync(force: true);
        Assert.DoesNotContain(forced.Changes, c => c.Path == Regions);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));

        // An edit outside the regions is a hand edit: refused under the default policy, overwritten (bodies kept) when asked.
        repo.Repo.WriteFile(Regions, repo.Repo.ReadFile(Regions).Replace("footer", "footer edited", StringComparison.Ordinal));
        var refused = await repo.RunAsync(force: true);
        E2ERepo.AssertOutcome(RunOutcome.Conflicts, refused);
        Assert.Contains(refused.Changes, c => c.Path == Regions && c.Kind == FileChangeKind.Conflict);
        Assert.Contains("footer edited", repo.Repo.ReadFile(Regions), StringComparison.Ordinal);
        var overwritten = await repo.RunAsync(force: true, handEdits: HandEditPolicy.Overwrite);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, overwritten);
        Assert.Contains("// kept by hand\nvar answer = 42;\n", repo.Repo.ReadFile(Regions), StringComparison.Ordinal);
        Assert.DoesNotContain("footer edited", repo.Repo.ReadFile(Regions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_region_lost_on_disk_is_a_conflict_and_the_file_is_kept()
    {
        await using var repo = await CheckTests.AppliedAsync();
        // A hand-written region whose id the template no longer has: its body would be lost.
        var lost = repo.Repo.ReadFile(Regions).Replace("id=body", "id=renamed", StringComparison.Ordinal)
            .Replace("// default body", "// precious", StringComparison.Ordinal);
        repo.Repo.WriteFile(Regions, lost);

        var result = await repo.RunAsync(force: true, handEdits: HandEditPolicy.Overwrite);

        E2ERepo.AssertOutcome(RunOutcome.Conflicts, result);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6010");
        Assert.Equal(lost, repo.Repo.ReadFile(Regions));
    }

    [Fact]
    public async Task Pair_mode_writes_the_companion_once_and_regenerates_the_generated_half()
    {
        await using var repo = await CheckTests.AppliedAsync();
        const string companion = "db/e2e/pair/Customer.cs";
        Assert.Equal("// hand-written half of Customer\n", repo.Repo.ReadFile(companion));
        repo.Repo.WriteFile(companion, "// my own code\n");

        await AddAttributeAsync(repo);
        var result = await repo.ApplyAsync();

        Assert.Contains(result.Changes, c => c.Path == "db/e2e/pair/Customer.g.cs" && c.Kind == FileChangeKind.Modified);
        Assert.Contains(result.Changes, c => c.Path == companion && c.Kind == FileChangeKind.Kept);
        Assert.Contains("nickname", repo.Repo.ReadFile("db/e2e/pair/Customer.g.cs"), StringComparison.Ordinal);
        Assert.Equal("// my own code\n", repo.Repo.ReadFile(companion));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));

        // A deleted companion is written again, once.
        File.Delete(repo.Repo.PathOf(companion));
        var recreated = await repo.ApplyAsync();
        Assert.Equal(("db/e2e/pair/Customer.cs", FileChangeKind.Added), recreated.Changes.Where(c => c.Kind != FileChangeKind.Kept).Select(c => (c.Path, c.Kind)).Single());
        Assert.Equal("// hand-written half of Customer\n", repo.Repo.ReadFile(companion));
    }

    [Fact]
    public async Task Once_mode_never_overwrites_even_when_forced()
    {
        await using var repo = await CheckTests.AppliedAsync();
        const string scaffold = "db/e2e/scaffold/customer.txt";
        Assert.Equal("scaffold for Customer (6 attributes)\n", repo.Repo.ReadFile(scaffold));
        repo.Repo.WriteFile(scaffold, "owned by the team\n");

        await AddAttributeAsync(repo);
        var result = await repo.ApplyAsync();
        var forced = await repo.ApplyAsync(force: true);

        Assert.DoesNotContain(result.Changes.Concat(forced.Changes), c => c.Path == scaffold && c.Kind != FileChangeKind.Kept);
        Assert.Equal("owned by the team\n", repo.Repo.ReadFile(scaffold));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));

        File.Delete(repo.Repo.PathOf(scaffold));
        await repo.ApplyAsync();
        Assert.Equal("scaffold for Customer (7 attributes)\n", repo.Repo.ReadFile(scaffold));
    }

    private static Task AddAttributeAsync(E2ERepo repo) =>
        repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01J92P0VZZ0000000000000020", ["name"] = "nickname", ["type"] = "string",
        }));
}
