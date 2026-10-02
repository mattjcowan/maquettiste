using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Pack features through the whole pipeline that the fixture packs do not cover: a JavaScript <c>where.script</c> filter, text outside
/// file blocks (MQ6011), regions mode under any root, and a template error (MQ6006) that fails its unit and keeps its files.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class PackFeatureTests
{
    [Fact]
    public async Task A_where_script_filter_and_loose_text_outside_file_blocks()
    {
        await using var repo = E2ERepo.Create(demo: false);
        WritePack(repo, "extras", """
            {"name": "extras", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "long", "template": "name.scriban", "for": "each entity", "where": {"script": "longName"}, "output": "long/{{ kebab entity.name }}.txt"},
              {"id": "loose", "template": "loose.scriban", "for": "model"}]}
            """,
            ("helpers.js", "maquettiste.filter(\"longName\", (element) => element.name.length > 7);\n"),
            ("name.scriban", "{{ entity.name }}\n"),
            ("loose.scriban", "stray text\n{{ capture c }}block{{ end }}{{ file \"loose/block.txt\" c }}\n"));

        var result = await repo.ApplyAsync();

        Assert.Equal(["db/extras/long/customer.txt", "db/extras/long/invoice-line.txt"],
            repo.Outputs().Keys.Where(p => p.StartsWith("db/extras/long/", StringComparison.Ordinal)));
        Assert.Equal("block", repo.Repo.ReadFile("db/extras/loose/block.txt"));
        var warning = Assert.Single(result.Diagnostics, d => d.Rule == "MQ6011");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    [Fact]
    public async Task Regions_mode_works_under_any_root()
    {
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["packs"]!["built"] = new JsonObject { ["output"] = "src/Generated/built" });
        WritePack(repo, "built", """
            {"name": "built", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "regions", "template": "r.scriban", "for": "model", "output": "r.txt", "mode": "regions"}]}
            """,
            ("r.scriban", "// maquettiste:keep id=a\n// maquettiste:end-keep\n"));

        var result = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        Assert.True(repo.Repo.Exists("src/Generated/built/r.txt"));
    }

    [Fact]
    public async Task A_template_error_fails_its_unit_and_keeps_the_previous_files()
    {
        await using var repo = E2ERepo.Create(demo: false);
        await repo.ApplyAsync();
        var before = repo.Repo.ReadFile("db/e2e/entities/customer.txt");

        var template = Path.Combine(repo.Repo.ModelRoot, "templates", "e2e", "entity.scriban");
        File.WriteAllText(template, "{{ entity.no_such_member.name }}\n");
        var failed = await repo.RunAsync();

        E2ERepo.AssertOutcome(RunOutcome.Invalid, failed);
        Assert.Equal(5, failed.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)); // one per entity unit
        var error = Assert.Single(failed.Diagnostics, d => d.ElementId == E2ERepo.CustomerId);
        Assert.Equal("MQ6006", error.Rule);
        Assert.Equal(".maquettiste/templates/e2e/entity.scriban", error.FilePath);
        Assert.Equal(1, error.Line);
        Assert.Equal(before, repo.Repo.ReadFile("db/e2e/entities/customer.txt")); // failed units are never orphaned
        Assert.True(repo.Repo.Exists("db/e2e/entities/product.txt"));

        // Fixing the template renders the units again (a failed unit keeps its previous state).
        File.WriteAllText(template, "fixed {{ entity.name }}\n");
        await repo.ApplyAsync();
        Assert.Equal("fixed Customer\n", repo.Repo.ReadFile("db/e2e/entities/customer.txt"));
        await IncrementalTests.AssertIncrementalEqualsForcedAsync(repo);
    }

    private static void WritePack(E2ERepo repo, string name, string packJson, params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(repo.Repo.ModelRoot, "templates", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), packJson);
        foreach (var (path, text) in files)
            File.WriteAllText(Path.Combine(folder, path), text);
        if (name == "extras")
        {
            var settingsPath = repo.Repo.PathOf(".maquettiste/maquettiste.json");
            var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            settings["packs"]!["extras"] = new JsonObject { ["output"] = "db/extras" };
            File.WriteAllBytes(settingsPath, Maquettiste.Testing.TestServices.Json.Write(settings, "maquettiste.json", "maquettiste.json"));
        }
    }
}
