using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Scripting;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>MQ6020, unit paths, explain, pack outputs, file move and pack settings (generation-ui.md sections 4.3, 5.1 to 5.3).</summary>
public sealed class PackAuthoringMoreTests
{
    private static CancellationToken Ct => EditorRepo.Ct;

    private static string PackPath(EditorRepo repo, string path) => repo.Repo.PathOf(".maquettiste/templates/sql-ddl/" + path);

    private static void SetTableOutput(EditorRepo repo, string output)
    {
        var node = JsonNode.Parse(File.ReadAllText(PackPath(repo, "pack.json")))!;
        node["units"]!.AsArray().Single(u => u!["id"]!.GetValue<string>() == "table")!["output"] = output;
        File.WriteAllText(PackPath(repo, "pack.json"), node.ToJsonString());
    }

    [Fact]
    public async Task Two_elements_of_one_unit_on_one_path_are_MQ6020_at_plan_not_MQ6005()
    {
        await using var repo = EditorRepo.Create();
        SetTableOutput(repo, "same.sql");

        var plan = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);

        var tables = plan.Plan!.Units.Count(u => u.Unit == "table");
        var collisions = plan.Plan.Diagnostics.Where(d => d.Rule == "MQ6020").ToList();
        Assert.Equal(tables - 1, collisions.Count); // every element after the first that claims the path
        Assert.All(collisions, c => Assert.Contains("renders db/same.sql for two elements", c.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Plan.Diagnostics, d => d.Rule == "MQ6005");
    }

    [Fact]
    public async Task Paths_count_the_scope_render_up_to_the_limit_and_report_collisions()
    {
        await using var repo = EditorRepo.Create();
        var plan = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);
        var tables = plan.Plan!.Units.Count(u => u.Unit == "table");

        var all = await repo.Service.PathsAsync("sql-ddl", "table", null, null, 0, Ct);
        Assert.Equal((tables, tables), (all.Count, all.Rendered));
        Assert.All(all.Paths, p => Assert.True(p.Allowed));
        Assert.Equal(all.Paths.Select(p => p.Path).Order(StringComparer.Ordinal), plan.Plan.Units.Where(u => u.Unit == "table").SelectMany(u => u.Outputs).Select(o => o.Path).Order(StringComparer.Ordinal));

        var limited = await repo.Service.PathsAsync("sql-ddl", "table", null, null, 1, Ct);
        Assert.Equal((tables, 1), (limited.Count, limited.Rendered));

        var unit = (await repo.Service.ListPacksAsync(Ct)).Single(p => p.Name == "sql-ddl").Units.Single(u => u.Id == "table");
        var same = await repo.Service.PathsAsync("sql-ddl", "table", null, new PreviewOptions { UnitOverride = unit with { Output = "../same.sql" } }, 0, Ct);
        Assert.Contains(same.Diagnostics, d => d.Rule == "MQ6019");
        Assert.Contains(same.Diagnostics, d => d.Rule == "MQ6020");
        Assert.All(same.Paths, p => Assert.False(p.Allowed));
    }

    [Fact]
    public async Task Explain_gives_the_first_reason_that_applies()
    {
        await using var repo = EditorRepo.Create();
        var first = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);
        var table = first.Plan!.Units.First(u => u.Unit == "table");
        await repo.Service.ApplyAsync(first.Plan.Id, null, Ct);

        var skipped = await repo.Service.ExplainAsync("sql-ddl", "table", table.ElementId, null, Ct);
        Assert.NotNull(skipped);
        Assert.Equal((true, "unchanged"), (skipped.Planned, skipped.Reason));
        Assert.StartsWith("Skipped: its ", skipped.Detail, StringComparison.Ordinal);
        Assert.NotNull(skipped.PlanId);

        var entity = table.ElementId!.Split('@')[0];
        var scope = await repo.Service.ExplainAsync("sql-ddl", "table", entity, null, Ct);
        Assert.Equal(("scope", false), (scope!.Reason, scope.Planned));
        Assert.Contains("each table", scope.Detail, StringComparison.Ordinal);
        Assert.Equal("unknown-unit", (await repo.Service.ExplainAsync("sql-ddl", "ghost", null, null, Ct))!.Reason);
        Assert.Equal("unknown-element", (await repo.Service.ExplainAsync("sql-ddl", "table", "01J92P0V0ZZZZZZZZZZZZZZZZZ", null, Ct))!.Reason);
        Assert.Null(await repo.Service.ExplainAsync("ghost", "table", null, null, Ct));

        var settings = await repo.Store.GetSettingsAsync(Ct);
        var saved = await repo.Service.SavePackSettingsAsync("sql-ddl", JsonSerializer.SerializeToElement(new { enabled = false }), settings.Hash, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal("pack-disabled", (await repo.Service.ExplainAsync("sql-ddl", "table", table.ElementId, null, Ct))!.Reason);
        var stale = await repo.Service.SavePackSettingsAsync("sql-ddl", JsonSerializer.SerializeToElement(new { enabled = true }), settings.Hash, Ct);
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
    }

    [Fact]
    public async Task Outputs_report_each_manifest_entry_intact_edited_or_missing()
    {
        await using var repo = EditorRepo.Create();
        Assert.Empty((await repo.Service.GetPackOutputsAsync("sql-ddl", Ct))!.Outputs);
        var plan = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);
        await repo.Service.ApplyAsync(plan.Plan!.Id, null, Ct);

        var outputs = await repo.Service.GetPackOutputsAsync("sql-ddl", Ct);
        Assert.NotNull(outputs);
        Assert.NotNull(outputs.LastWritten);
        Assert.All(outputs.Outputs, o => Assert.Equal("intact", o.State));
        Assert.Contains(outputs.Outputs, o => o.Unit == "table" && o.ElementId is not null && o.Root == "db");
        var edited = outputs.Outputs[0].Path;
        var missing = outputs.Outputs[1].Path;
        File.AppendAllText(repo.Repo.PathOf(edited), "-- edit\n");
        File.Delete(repo.Repo.PathOf(missing));

        var after = await repo.Service.GetPackOutputsAsync("sql-ddl", Ct);
        Assert.Equal("edited", after!.Outputs.Single(o => o.Path == edited).State);
        Assert.Equal("missing", after.Outputs.Single(o => o.Path == missing).State);
        Assert.Null(await repo.Service.GetPackOutputsAsync("ghost", Ct));
    }

    [Fact]
    public async Task A_move_rewrites_the_units_in_canonical_form_and_refuses_an_included_file()
    {
        await using var repo = EditorRepo.Create();
        var seed = await repo.Service.ReadPackFileAsync("sql-ddl", "seed.scriban", Ct);
        var pack = await repo.Service.GetPackAsync("sql-ddl", Ct);

        var refused = await repo.Service.MovePackFileAsync("sql-ddl", new PackFileMove("seed.scriban", "seeds/seed.scriban", false, null), seed!.Hash, Ct);
        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        var partial = await repo.Service.ReadPackFileAsync("sql-ddl", "_table.scriban", Ct);
        var included = await repo.Service.MovePackFileAsync("sql-ddl", new PackFileMove("_table.scriban", "_t.scriban", true, pack!.Hash), partial!.Hash, Ct);
        Assert.Equal(SaveOutcome.Referenced, included.Outcome);

        var moved = await repo.Service.MovePackFileAsync("sql-ddl", new PackFileMove("seed.scriban", "seeds/seed.scriban", true, pack.Hash), seed.Hash, Ct);
        Assert.Equal(SaveOutcome.Saved, moved.Outcome);
        Assert.Equal(seed.Hash, moved.Hash);
        Assert.False(File.Exists(PackPath(repo, "seed.scriban")));
        var after = await repo.Service.GetPackAsync("sql-ddl", Ct);
        Assert.Contains("\"template\": \"seeds/seed.scriban\"", await File.ReadAllTextAsync(PackPath(repo, "pack.json"), Ct), StringComparison.Ordinal);
        Assert.Equal(ContentHash.Of(File.ReadAllBytes(PackPath(repo, "pack.json"))), after!.Hash);

        var stale = await repo.Service.MovePackFileAsync("sql-ddl", new PackFileMove("seeds/seed.scriban", "seed.scriban", true, pack.Hash), seed.Hash, Ct);
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        await Assert.ThrowsAsync<PackPathException>(() => repo.Service.MovePackFileAsync("sql-ddl", new PackFileMove("seeds/seed.scriban", "../x.scriban", false, null), seed.Hash, Ct));
    }

    [Fact]
    public async Task The_template_context_lists_variables_members_and_helpers()
    {
        await using var repo = EditorRepo.Create();
        var context = await repo.Service.GetTemplateContextAsync("sql-ddl", "table", Ct);
        Assert.NotNull(context);
        Assert.Equal("each table", context.Scope);
        Assert.Contains(context.Variables, v => v.Name == "table");
        Assert.Contains(context.Members["element"], m => m.Name == "columns" && m.Type == "list");
        // A table carries its file's annotations like an entity does (stereotypes as objects, properties and generation as maps).
        foreach (var (name, type) in new[]
        {
            ("display_name", "string"), ("plural_name", "string"), ("description", "string"), ("tags", "list"), ("category", "object"),
            ("stereotypes", "list"), ("properties", "map"), ("generation", "map"), ("comment", "string"),
        })
            Assert.Contains(new Maquettiste.Engine.TemplateMember(name, type), context.Members["element"]);
        Assert.Equal(context.Helpers.Order(StringComparer.Ordinal), context.Helpers);
        Assert.Null(await repo.Service.GetTemplateContextAsync("sql-ddl", "ghost", Ct));
    }

    [Theory]
    [InlineData("each process", "process", "all_states")]
    [InlineData("each actor", "actor", "gates")]
    [InlineData("each scenario", "scenario", "steps")]
    public async Task The_template_context_of_a_process_actor_or_scenario_unit_lists_its_alias_members_and_helpers(string scope, string alias, string member)
    {
        await using var repo = EditorRepo.Create();
        var node = JsonNode.Parse(File.ReadAllText(PackPath(repo, "pack.json")))!;
        node["units"]!.AsArray().Add(new JsonObject { ["id"] = "flow", ["template"] = "table.scriban", ["for"] = scope, ["output"] = "flow/{{ element.id }}.txt" });
        File.WriteAllText(PackPath(repo, "pack.json"), node.ToJsonString());

        var context = await repo.Service.GetTemplateContextAsync("sql-ddl", "flow", Ct);

        Assert.NotNull(context);
        Assert.Equal(scope, context.Scope);
        Assert.Contains(context.Variables, v => v.Name == alias);
        Assert.Contains(context.Members["element"], m => m.Name == member && m.Type == "list");
        Assert.Contains(context.Members["model"], m => m.Name is "processes" or "actors" or "scenarios");
        Assert.Contains("state_path", context.Helpers);
        Assert.Contains("iso_duration_ms", context.Helpers);
    }

    [Fact]
    public async Task The_template_context_and_the_pack_read_list_what_the_packs_own_scripts_register()
    {
        await using var repo = EditorRepo.Create();
        var context = await repo.Service.GetTemplateContextAsync("sql-ddl", "table", Ct);
        Assert.NotNull(context);
        Assert.Contains("ddl_order", context.Helpers);
        Assert.Contains(context.Registrations, r => r is { Kind: ScriptRegistrationKind.Selector, Name: "databases", DeclaredIn: var path } && path.EndsWith("helpers.js", StringComparison.Ordinal));
        Assert.Equal(context.Registrations.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.Ordinal), context.Registrations);

        // A script edit shows at the next read: a filter and a transform are listed with their kinds.
        var helpers = repo.Repo.PathOf(".maquettiste/templates/sql-ddl/helpers.js");
        File.AppendAllText(helpers, "\nmaquettiste.filter(\"only_named\", (element) => !!element.name);\nmaquettiste.transform(\"shape\", (element) => ({ n: 1 }));\n");
        var pack = await repo.Service.GetPackAsync("sql-ddl", Ct);
        Assert.NotNull(pack);
        Assert.Contains(pack.Registrations, r => r is { Kind: ScriptRegistrationKind.Filter, Name: "only_named" });
        Assert.Contains(pack.Registrations, r => r is { Kind: ScriptRegistrationKind.Transform, Name: "shape" });
        Assert.DoesNotContain("only_named", (await repo.Service.GetTemplateContextAsync("sql-ddl", "table", Ct))!.Helpers);

        // A script that fails lists nothing (the pack's diagnostics say why) rather than failing the read.
        File.AppendAllText(helpers, "\nthrow new Error(\"broken\");\n");
        Assert.Empty((await repo.Service.GetPackAsync("sql-ddl", Ct))!.Registrations);
    }
}
