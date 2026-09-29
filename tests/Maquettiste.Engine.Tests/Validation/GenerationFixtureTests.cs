using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The generation rules MQ6003 and MQ6019 to MQ6025 (generation-ui.md section 5.3) on one fixture,
/// <c>tests/fixtures/validation/generation/</c>: its packs go under the billing model's <c>templates/</c> and <c>packs.json</c> becomes
/// the project's <c>packs</c> settings. The golden list is what <c>maquettiste validate</c> reports (the pack load, with its parse pass)
/// followed by a dry-run plan of the one pack that loads with units to plan (the path rules and the render's MQ6003).
/// </summary>
public sealed class GenerationFixtureTests
{
    private static CancellationToken Ct => EditorRepo.Ct;

    private static async Task<EditorRepo> CreateAsync()
    {
        var repo = EditorRepo.Create(packs: false);
        var fixture = ValidationFixture.RepoRoot("generation");
        var source = Path.Combine(fixture, ".maquettiste", "templates");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(repo.Repo.ModelRoot, "templates", Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        repo.EditSettingsOnDisk(n => n["packs"] = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture, "packs.json"))));
        await repo.Store.RescanAsync(false, Ct);
        return repo;
    }

    [Fact]
    public async Task Generation_rules_match_their_golden_diagnostics()
    {
        await using var repo = await CreateAsync();
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);

        var load = await EngineServices.Create(repo.Repo.Options).Packs.LoadAsync(snapshot, null, null, Ct);
        var plan = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["outputs"] }, null, Ct);
        var planned = plan.Plan!.Diagnostics.Where(d => d.Rule.StartsWith("MQ60", StringComparison.Ordinal));
        var diagnostics = PackLoader.Sort(load.Diagnostics).Concat(PackLoader.Sort(planned.Distinct())).ToList();

        ValidationFixture.AssertGolden("generation", diagnostics);
        Assert.Equal(["MQ6003", "MQ6019", "MQ6020", "MQ6021", "MQ6022", "MQ6023", "MQ6024", "MQ6025"],
            diagnostics.Select(d => d.Rule).Distinct().Order(StringComparer.Ordinal));
        Assert.All(diagnostics, d =>
        {
            Assert.NotNull(d.FilePath);
            Assert.Equal(RuleCatalog.Get(d.Rule).DefaultSeverity, d.Severity);
        });
    }

    [Fact]
    public async Task A_file_no_unit_reaches_that_does_not_parse_is_MQ6025_at_load_and_never_stops_a_run()
    {
        await using var repo = await CreateAsync();
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);

        var load = await EngineServices.Create(repo.Repo.Options).Packs.LoadAsync(snapshot, ["outputs"], null, Ct);

        var unreached = load.Diagnostics.Where(d => d.Rule == "MQ6025").ToList();
        Assert.NotEmpty(unreached);
        Assert.All(unreached, d =>
        {
            Assert.Equal(".maquettiste/templates/outputs/scratch.scriban", d.FilePath);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
            Assert.Equal(2, d.Line);
        });
        var reached = load.Diagnostics.Where(d => d.Rule == "MQ6003").ToList();
        Assert.NotEmpty(reached);
        Assert.All(reached, d =>
        {
            Assert.Equal(".maquettiste/templates/outputs/broken.scriban", d.FilePath);
            Assert.EndsWith("(unit 'broken')", d.Message, StringComparison.Ordinal);
        });
        Assert.Single(load.Packs); // a parse error fails the units that reach the file, not the pack

        var plan = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["outputs"] }, null, Ct);
        Assert.NotNull(plan.Plan);
        Assert.Contains(plan.Plan.Diagnostics, d => d.Rule == "MQ6025");
        Assert.Contains(plan.Plan.Units, u => u.Unit == "same");
    }
}
