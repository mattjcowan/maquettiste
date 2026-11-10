using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Writing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Revisions;

/// <summary>
/// Section 13 for a pack's units, over <c>csharp-dapper</c>'s <c>revise-no-block</c> case (a project without a block whose events name
/// <c>revise</c>): its scenario tests read the keys they read before revisions (0.22.0), compared with the template less its revision skip
/// check. Reading the transitions' verbs adds no key. Only a scenario with a step that revises reads its subject's entity, to see whether the
/// type has revisions; a template can't tell that without the entity, and the bytes stay those of 0.22.0 (the case's goldens).
/// </summary>
public sealed class RevisionPackKeyTests
{
    private const string Pack = "csharp-dapper";
    private const string Entity = "01JRV000000000000000000E01";
    private const string SkipStart = "# Whether the scenario makes a revision";
    private const string SkipFact = "[Fact{{ if makes_revision }}(Skip = \"This scenario makes a revision; storing revisions is the team's\"){{ end }}]";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scenario_tests_without_a_block_read_no_new_key_unless_a_step_revises_and_then_only_the_subjects_entity()
    {
        var plain = await AddedKeysAsync(revising: false);
        Assert.All(plain, unit => Assert.Empty(unit.Value));

        var revising = await AddedKeysAsync(revising: true);
        Assert.All(revising, unit => Assert.Equal(["k:database", "r:" + Entity, "s:conventions"], unit.Value));
    }

    // Each scenario-tests unit's read keys with the shipped template, less those without the skip check; the scenario keeps or drops its revise step.
    private static async Task<SortedDictionary<string, string[]>> AddedKeysAsync(bool revising)
    {
        var shipped = await ScenarioKeysAsync(revising, template => template);
        var without = await ScenarioKeysAsync(revising, template =>
        {
            var start = template.IndexOf(SkipStart, StringComparison.Ordinal);
            var end = template.IndexOf("start = \"\"\n", start, StringComparison.Ordinal);
            Assert.True(start > 0 && end > start && template.Contains(SkipFact, StringComparison.Ordinal));
            return template[..start] + template[end..].Replace(SkipFact, "[Fact]", StringComparison.Ordinal);
        });
        Assert.NotEmpty(shipped);
        Assert.Equal(without.Keys, shipped.Keys);
        var added = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (unit, keys) in shipped)
        {
            Assert.Empty(without[unit].Except(keys, StringComparer.Ordinal));
            added[unit] = [.. keys.Except(without[unit], StringComparer.Ordinal)];
        }

        return added;
    }

    // Generates the case with the pack's scenario-tests template changed by `edit`, and answers each scenario-tests unit's read keys.
    private static async Task<SortedDictionary<string, IReadOnlyList<string>>> ScenarioKeysAsync(bool revising, Func<string, string> edit)
    {
        using var repo = new TempRepo();
        var source = Path.Combine(Fixtures.RepoRoot, "packs", Pack);
        var templates = Path.Combine(repo.ModelRoot, "templates", Pack);
        var testCase = Path.Combine(source, "tests", "revise-no-block");
        Copy(source, templates, skip: "tests");
        Copy(Path.Combine(testCase, "model"), Path.Combine(repo.ModelRoot, "model"));
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(testCase, "maquettiste.json")))!.AsObject();
        settings["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "out" }) };
        File.WriteAllText(Path.Combine(repo.ModelRoot, "maquettiste.json"), settings.ToJsonString());
        if (!revising)
        {
            // The third step sends the event that names revise; without it no step revises.
            var scenario = Path.Combine(repo.ModelRoot, "model", "scenarios", "drawing-lifecycle", "release-and-revise.json");
            var node = JsonNode.Parse(File.ReadAllText(scenario))!.AsObject();
            node["steps"]!.AsArray().RemoveAt(2);
            File.WriteAllText(scenario, node.ToJsonString());
        }

        var template = Path.Combine(templates, "scenario-tests.scriban");
        File.WriteAllText(template, edit(File.ReadAllText(template).ReplaceLineEndings("\n")));

        await using var store = new ModelStore(repo.Options);
        var service = new GenerationService(store, repo.Options);
        var planned = await service.PlanAsync(new GenerationRequest { Packs = [Pack] }, null, Ct);
        var applied = await service.ApplyAsync(planned.Plan!.Id, null, Ct);
        Assert.True(applied.Outcome == RunOutcome.Succeeded, string.Join(" | ", planned.Plan.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        var states = await new UnitStateStore(repo.Options, new OutputPathPolicy(repo.Options, null)).LoadAsync(Pack, Ct);
        var keys = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var state in states.Values.Where(s => s.Key.Contains("/scenario-tests:", StringComparison.Ordinal)))
            keys[state.Key] = state.ReadKeys;
        return keys;
    }

    private static void Copy(string source, string target, string? skip = null)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (skip is not null && relative.StartsWith(skip + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            var to = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }
}
