using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using static Maquettiste.Engine.Tests.Editor.EditorRepo;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// The plan's causes from unit state format 4 (generation-ui.md section 4.2): each change names the key or static part that changed,
/// the same at any parallelism, and a state file of the previous format gives every unit <c>new</c> with <c>state-reset</c>.
/// </summary>
public sealed class PlanCauseTests
{
    private static readonly GenerationRequest SqlDdl = new() { Packs = ["sql-ddl"] };

    [Fact]
    public async Task Each_kind_of_change_names_its_cause()
    {
        await using var repo = Create();
        var first = await PlanAndApplyAsync(repo);
        Assert.All(first.Units, u => Assert.Equal(("new", 0), (u.Reason, u.CauseCount)));

        // A template edit: the units of that template name it (a static part), nothing else.
        File.AppendAllText(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/table.scriban"), "{{ '' }}\n");
        await repo.Store.RescanAsync(false, Ct);
        var template = await PlanAndApplyAsync(repo);
        var table = template.Units.First(u => u.Unit == "table" && !u.Skipped);
        Assert.Equal("inputs", table.Reason);
        Assert.Contains(table.Causes, c => (c.Kind, c.Key) == ("template", "t:sql-ddl/table.scriban"));
        Assert.DoesNotContain(table.Causes, c => c.Kind is "parameter" or "pack-version" or "inputs");

        // An element edit: the units that read it name the element, with its id.
        EditJson(repo, ".maquettiste/model/entities/customer.json", n => n["description"] = "Someone we bill, monthly.");
        await repo.Store.RescanAsync(false, Ct);
        var element = await PlanAndApplyAsync(repo);
        Assert.Contains(element.Units.Where(u => !u.Skipped).SelectMany(u => u.Causes),
            c => (c.Kind, c.Key, c.ElementId) == ("element", "e:" + CustomerId, CustomerId) && c.Detail.EndsWith(" changed", StringComparison.Ordinal));
        Assert.All(element.Units.Where(u => !u.Skipped), u => Assert.DoesNotContain(u.Causes, c => c.Kind == "inputs"));

        // A parameter value: named by the parameter alone.
        repo.EditSettingsOnDisk(s => s["packs"]!["sql-ddl"]!.AsObject()["parameters"] = new JsonObject { ["comments"] = false });
        await repo.Store.RescanAsync(false, Ct);
        var parameter = await PlanAndApplyAsync(repo);
        var rendered = parameter.Units.Where(u => !u.Skipped).ToList();
        Assert.NotEmpty(rendered);
        Assert.All(rendered, u => Assert.Contains(u.Causes, c => (c.Kind, c.Key) == ("parameter", "comments")));
        Assert.All(rendered, u => Assert.DoesNotContain(u.Causes, c => c.Kind == "parameter" && c.Key != "comments"));

        // A pack version bump.
        var packJson = repo.Repo.PathOf(".maquettiste/templates/sql-ddl/pack.json");
        File.WriteAllText(packJson, File.ReadAllText(packJson).Replace("\"version\": \"1.0.0\"", "\"version\": \"1.1.0\"", StringComparison.Ordinal));
        await repo.Store.RescanAsync(false, Ct);
        var version = await PlanAndApplyAsync(repo);
        Assert.All(version.Units, u => Assert.Contains(u.Causes, c => c.Kind == "pack-version" && c.Detail == "sql-ddl went from 1.0.0 to 1.1.0"));

        // A state file of the previous format: every unit is new with the single cause state-reset.
        var units = Path.Combine(repo.Repo.CacheDirectory, "units");
        File.Move(Path.Combine(units, "sql-ddl.v4.bin"), Path.Combine(units, "sql-ddl.v1.bin"));
        var reset = (await repo.Service.PlanAsync(SqlDdl, null, Ct)).Plan!;
        Assert.All(reset.Units, u => Assert.Equal(("new", "state-reset", 1), (u.Reason, u.Causes.Single().Kind, u.CauseCount)));
    }

    [Fact]
    public async Task A_deleted_element_is_named_by_the_name_it_had_at_the_last_render()
    {
        await using var repo = Create();
        await PlanAndApplyAsync(repo);
        var store = new UnitStateStore(repo.Repo.Options, new Maquettiste.Engine.Writing.OutputPathPolicy(repo.Repo.Options, null));
        var states = await store.LoadAsync("sql-ddl", Ct);
        var key = "e:" + CustomerId;
        var stored = states.Values.First(s => s.ReadKeys.Contains(key));
        Assert.NotNull(stored.Names);
        Assert.Equal("Customer (entity)", stored.Names[key]);
        Assert.All(stored.Names.Keys, k => Assert.StartsWith("e:", k, StringComparison.Ordinal));

        // The model no longer has it (nameOf answers null): the cause still names it.
        var unit = new PlanUnit(stored.Key, "changed", [.. stored.ReadKeys.Where(k => k != key)], false, []);
        var (reason, causes) = Maquettiste.Engine.Generation.PlanExplainer.Explain(unit, stored, false, repo.Repo.RepoRoot, id => id != CustomerId, nameOf: _ => null);
        Assert.Equal("inputs", reason);
        Assert.Contains(causes, c => (c.Kind, c.Key, c.Detail) == ("absent", key, "Customer (entity) was deleted"));
    }

    [Fact]
    public async Task A_check_plan_gives_its_units_the_check_reason()
    {
        await using var repo = Create();
        await PlanAndApplyAsync(repo);
        var check = (await repo.Service.PlanAsync(SqlDdl with { Mode = GenerationMode.Check }, null, Ct)).Plan!;
        Assert.NotEmpty(check.Units);
        Assert.All(check.Units.Where(u => !u.Skipped), u => Assert.Equal(("check", 0), (u.Reason, u.CauseCount)));
        Assert.Contains(check.Units, u => !u.Skipped);
        var dryRun = (await repo.Service.PlanAsync(SqlDdl, null, Ct)).Plan!;
        Assert.DoesNotContain(dryRun.Units, u => u.Reason == "check");
    }

    [Fact]
    public async Task Causes_are_the_same_at_one_job_and_at_eight()
    {
        await using var repo = Create();
        await PlanAndApplyAsync(repo);
        File.AppendAllText(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/_table.scriban"), "{{ '' }}\n");
        EditJson(repo, ".maquettiste/model/entities/customer.json", n => n["description"] = "Changed.");
        await repo.Store.RescanAsync(false, Ct);

        var one = (await repo.Service.PlanAsync(SqlDdl with { Jobs = 1 }, null, Ct)).Plan!;
        var eight = (await repo.Service.PlanAsync(SqlDdl with { Jobs = 8 }, null, Ct)).Plan!;
        static string Causes(GenerationPlan plan) => string.Join('\n', plan.Units.Select(u =>
            $"{u.Key} {u.Reason} {u.CauseCount} " + string.Join(';', u.Causes.Select(c => $"{c.Kind}|{c.Key}|{c.Detail}"))));
        Assert.Equal(Causes(one), Causes(eight));
        Assert.Contains(one.Units, u => u.Causes.Any(c => c.Kind == "template"));
    }

    private static async Task<GenerationPlan> PlanAndApplyAsync(EditorRepo repo)
    {
        var plan = (await repo.Service.PlanAsync(SqlDdl, null, Ct)).Plan!;
        await repo.Service.ApplyAsync(plan.Id, null, Ct);
        return plan;
    }

    private static void EditJson(EditorRepo repo, string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(repo.Repo.PathOf(path)))!.AsObject();
        change(node);
        File.WriteAllText(repo.Repo.PathOf(path), node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

/// <summary>MQ6019 at planning (generation-ui.md section 5.3): a unit-level error that skips that unit only.</summary>
public sealed class PlanOutputRootTests
{
    [Fact]
    public async Task A_unit_whose_pattern_leaves_every_root_is_skipped_and_the_packs_other_units_run()
    {
        await using var repo = Create();
        var packJson = repo.Repo.PathOf(".maquettiste/templates/sql-ddl/pack.json");
        var node = JsonNode.Parse(File.ReadAllText(packJson))!.AsObject();
        var units = node["units"]!.AsArray();
        var index = units.Select((u, i) => (u, i)).Single(x => (string?)x.u!["id"] == "table").i;
        units[index]!["output"] = "../outside/{{ table.name }}.sql";
        File.WriteAllText(packJson, node.ToJsonString());
        await repo.Store.RescanAsync(false, Ct);

        var plan = (await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct)).Plan!;

        var rule = Assert.Single(plan.Diagnostics, d => d.Rule == "MQ6019");
        Assert.Equal($"/units/{index}/output", rule.JsonPointer);
        Assert.DoesNotContain(plan.Units, u => u.Unit == "table");
        Assert.Contains(plan.Units, u => u.Unit == "schema");
    }
}

/// <summary>Explain's selection reasons and the failing where clause (generation-ui.md section 4.3).</summary>
public sealed class ExplainSelectionTests
{
    [Fact]
    public async Task Explain_names_the_run_selection_and_the_failing_where_clause()
    {
        await using var repo = Create();
        var plan = (await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct)).Plan!;
        var table = plan.Units.First(u => u.Unit == "table");

        var notSelected = await repo.Service.ExplainAsync("sql-ddl", "table", table.ElementId, null, Ct, ["csharp-dapper"]);
        Assert.Equal(("not-selected", false), (notSelected!.Reason, notSelected.Planned));

        Assert.True((await repo.Service.ExplainAsync("sql-ddl", "table", table.ElementId, null, Ct))!.Planned);

        var packJson = repo.Repo.PathOf(".maquettiste/templates/sql-ddl/pack.json");
        var node = JsonNode.Parse(File.ReadAllText(packJson))!.AsObject();
        node["units"]!.AsArray().Single(u => (string?)u!["id"] == "table")!["where"] = new JsonObject
        {
            ["abstract"] = false,
            ["tags"] = new JsonArray("no-such-tag"),
        };
        File.WriteAllText(packJson, node.ToJsonString());
        await repo.Store.RescanAsync(false, Ct);
        var filter = await repo.Service.ExplainAsync("sql-ddl", "table", table.ElementId, null, Ct);
        Assert.Equal("filter", filter!.Reason);
        Assert.Contains("(where.tags: no-such-tag)", filter.Detail, StringComparison.Ordinal);
    }
}
