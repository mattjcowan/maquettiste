using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// A <c>block</c> unit through the whole pipeline (engine-design.md section 12.3b): an <c>outputs.allow</c> entry naming the file,
/// <c>target-missing</c> in the plan and the run, plan and apply, check, and the retired <c>commit</c> flag (MQ1010).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class BlockUnitTests
{
    private const string Two = "bin/\nobj/\n";
    private const string Block = "# maquettiste: begin ignore/gitignore\n/db/e2e/\n# maquettiste: end ignore/gitignore\n";

    private static E2ERepo Create(bool createFile = false)
    {
        var repo = E2ERepo.Create(demo: false, settings: s =>
        {
            s["outputs"]!["allow"]!.AsArray().Add(new JsonObject { ["path"] = ".gitignore" });
            s["packs"]!["ignore"] = new JsonObject();
        });
        var folder = Path.Combine(repo.Repo.ModelRoot, "templates", "ignore");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), $$"""
            {"name": "ignore", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "gitignore", "template": "gitignore.scriban", "for": "model", "output": ".gitignore", "mode": "block", "createFile": {{(createFile ? "true" : "false")}}}]}
            """);
        File.WriteAllText(Path.Combine(folder, "gitignore.scriban"), "/db/e2e/");
        return repo;
    }

    [Fact]
    public async Task A_missing_target_is_listed_as_target_missing_writes_nothing_and_is_not_drift()
    {
        await using var repo = Create();

        var plan = (await repo.Service.PlanAsync(new GenerationRequest { Packs = ["ignore"] }, null, Ct)).Plan!;
        var unit = Assert.Single(plan.Units);
        Assert.Equal("target-missing", unit.Reason);
        Assert.Equal(("target-missing", ".gitignore"), (Assert.Single(unit.Causes).Kind, unit.Causes[0].Path));
        Assert.Empty(plan.Changes);

        var run = await repo.RunAsync();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, run);
        var info = Assert.Single(run.Diagnostics, d => d.Rule == "MQ6028");
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.False(repo.Repo.Exists(".gitignore"));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));

        // The unit renders again on every run, so a file created later gets its block.
        repo.Repo.WriteFile(".gitignore", Two);
        var second = await repo.RunAsync();
        Assert.Contains(second.Changes, c => c.Path == ".gitignore" && c.Kind == FileChangeKind.Added);
        Assert.Equal(Two + "\n" + Block, repo.Repo.ReadFile(".gitignore"));
    }

    [Fact]
    public async Task Plan_apply_check_and_removal_keep_the_lines_around_the_block()
    {
        await using var repo = Create();
        repo.Repo.WriteFile(".gitignore", Two);

        var plan = (await repo.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!;
        Assert.Contains(plan.Changes, c => c.Path == ".gitignore" && c.Kind == FileChangeKind.Added);
        var applied = await repo.Service.ApplyAsync(plan.Id, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, applied.Outcome);
        Assert.Equal(Two + "\n" + Block, repo.Repo.ReadFile(".gitignore"));
        Assert.Contains("\".gitignore\", \"b:", repo.Manifest("ignore"), StringComparison.Ordinal);

        // A second run and check: nothing changes; an edit outside the block is never drift.
        var bytes = File.ReadAllBytes(repo.Repo.PathOf(".gitignore"));
        Assert.DoesNotContain((await repo.RunAsync()).Changes, c => c.Path == ".gitignore");
        Assert.Equal(bytes, File.ReadAllBytes(repo.Repo.PathOf(".gitignore")));
        repo.Repo.WriteFile(".gitignore", "*.log\n" + repo.Repo.ReadFile(".gitignore"));
        var check = await repo.RunAsync(GenerationMode.Check);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, check);
        Assert.DoesNotContain(check.Changes, c => c.Path == ".gitignore");

        // A changed template is drift for check, then an update of the block alone.
        File.WriteAllText(Path.Combine(repo.Repo.ModelRoot, "templates", "ignore", "gitignore.scriban"), "/db/e2e/\n/db/extra/\n");
        E2ERepo.AssertOutcome(RunOutcome.Drift, await repo.RunAsync(GenerationMode.Check));
        await repo.ApplyAsync();
        Assert.Equal("*.log\n" + Two + "\n" + Block.Replace("/db/e2e/\n", "/db/e2e/\n/db/extra/\n", StringComparison.Ordinal), repo.Repo.ReadFile(".gitignore"));

        // The pack disabled: its block goes, the other lines stay.
        var settingsPath = repo.Repo.PathOf(".maquettiste/maquettiste.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["packs"]!["ignore"] = new JsonObject { ["enabled"] = false };
        File.WriteAllBytes(settingsPath, Testing.TestServices.Json.Write(settings, "maquettiste.json", "maquettiste.json"));
        var removed = await repo.RunAsync();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, removed);
        Assert.Contains(removed.Changes, c => c.Path == ".gitignore" && c.Kind == FileChangeKind.Deleted);
        Assert.Equal("*.log\n" + Two, repo.Repo.ReadFile(".gitignore"));
    }

    [Fact]
    public async Task A_created_file_goes_with_its_block()
    {
        await using var repo = Create(createFile: true);

        await repo.ApplyAsync();
        Assert.Equal(Block, repo.Repo.ReadFile(".gitignore"));
        Assert.Contains("\".gitignore\", \"bc:", repo.Manifest("ignore"), StringComparison.Ordinal);

        Directory.Delete(Path.Combine(repo.Repo.ModelRoot, "templates", "ignore"), recursive: true);
        var settingsPath = repo.Repo.PathOf(".maquettiste/maquettiste.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["packs"]!.AsObject().Remove("ignore");
        File.WriteAllBytes(settingsPath, Testing.TestServices.Json.Write(settings, "maquettiste.json", "maquettiste.json"));
        var removed = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, removed);
        Assert.False(repo.Repo.Exists(".gitignore"));
    }

    [Fact]
    public async Task A_settings_file_with_the_retired_commit_flag_loads_with_one_info_per_entry()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s =>
        {
            var allow = s["outputs"]!["allow"]!.AsArray();
            allow[0]!["commit"] = true;
            allow.Add(new JsonObject { ["path"] = "gen", ["commit"] = false });
        });

        var report = await repo.Store.ValidateAsync(new ValidationScope(), Ct);

        var infos = report.Diagnostics.Where(d => d.Rule == "MQ1010").ToList();
        Assert.Equal(["/outputs/allow/0/commit", "/outputs/allow/2/commit"], infos.Select(d => d.JsonPointer));
        Assert.All(infos, d => Assert.Equal(DiagnosticSeverity.Info, d.Severity));
        Assert.Contains("`commit` is ignored since 0.5.5; remove it", infos[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync());

        // A settings save drops the flag, even when an older client sends it back.
        var current = await repo.Store.GetSettingsAsync(Ct);
        var body = JsonNode.Parse(current.Json.GetRawText())!;
        body["outputs"]!["allow"]![0]!["commit"] = true;
        var saved = await repo.Store.SaveSettingsAsync(System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()), current.Hash, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.DoesNotContain("commit", repo.Repo.ReadFile(".maquettiste/maquettiste.json"), StringComparison.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
