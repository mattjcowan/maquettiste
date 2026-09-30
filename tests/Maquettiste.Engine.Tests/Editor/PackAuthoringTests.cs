using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>Pack authoring, the unit rules and the plan explanation (generation-ui.md sections 3, 4 and 5).</summary>
public sealed class PackAuthoringTests
{
    private static CancellationToken Ct => EditorRepo.Ct;

    private static string PackPath(EditorRepo repo, string pack, string path) => repo.Repo.PathOf(".maquettiste/templates/" + pack + "/" + path);

    [Fact]
    public async Task A_pack_reads_with_its_hash_files_roles_users_and_parameters()
    {
        await using var repo = EditorRepo.Create();
        var pack = await repo.Service.GetPackAsync("sql-ddl", Ct);

        Assert.NotNull(pack);
        Assert.Equal(ContentHash.Of(File.ReadAllBytes(PackPath(repo, "sql-ddl", "pack.json"))), pack.Hash);
        Assert.True(pack.Enabled);
        Assert.Equal("db", pack.Output);
        Assert.Equal("sql-ddl", pack.Document!.Value.GetProperty("name").GetString());
        var table = Assert.Single(pack.Files, f => f.Path == "table.scriban");
        Assert.Equal("template", table.Role);
        Assert.Equal(["unit:table"], table.UsedBy);
        var partial = Assert.Single(pack.Files, f => f.Path == "_table.scriban");
        Assert.Equal("partial", partial.Role);
        Assert.Contains("include:table.scriban", partial.UsedBy);
        Assert.Equal("script", Assert.Single(pack.Files, f => f.Path == "helpers.js").Role);
        Assert.Equal("manifest", Assert.Single(pack.Files, f => f.Path == "pack.json").Role);
        Assert.Equal(pack.Files.Select(f => f.Path).Order(StringComparer.Ordinal), pack.Files.Select(f => f.Path));
        Assert.DoesNotContain(pack.Diagnostics, d => d.Severity == Diagnostics.DiagnosticSeverity.Error);
        Assert.Null(await repo.Service.GetPackAsync("ghost", Ct));
        await Assert.ThrowsAsync<PackPathException>(() => repo.Service.GetPackAsync("../model", Ct));
    }

    [Fact]
    public async Task A_file_write_checks_the_hash_and_answers_conflict_with_the_disk_text()
    {
        await using var repo = EditorRepo.Create();
        var read = await repo.Service.ReadPackFileAsync("sql-ddl", "seed.scriban", Ct);
        Assert.NotNull(read);

        var saved = await repo.Service.WritePackFileAsync("sql-ddl", "seed.scriban", read.Text + "\n", read.Hash, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(ContentHash.Of(Encoding.UTF8.GetBytes(read.Text + "\n")), saved.Hash);

        var stale = await repo.Service.WritePackFileAsync("sql-ddl", "seed.scriban", "mine", read.Hash, Ct);
        Assert.Equal(SaveOutcome.Conflict, stale.Outcome);
        Assert.Equal((saved.Hash, read.Text + "\n"), (stale.Hash, stale.Current));
        Assert.Equal(read.Text + "\n", await File.ReadAllTextAsync(PackPath(repo, "sql-ddl", "seed.scriban"), Ct));

        var exists = await repo.Service.WritePackFileAsync("sql-ddl", "seed.scriban", "x", null, Ct);
        Assert.Equal(SaveOutcome.Conflict, exists.Outcome);
        var created = await repo.Service.WritePackFileAsync("sql-ddl", "docs/notes.scriban", "{{ 1 + 1 }}", null, Ct);
        Assert.Equal(SaveOutcome.Saved, created.Outcome);

        // GU6: a template that does not parse is saved and reported (MQ6003 at the unit that names it).
        var broken = await repo.Service.WritePackFileAsync("sql-ddl", "table.scriban", "{{ if }", (await repo.Service.ReadPackFileAsync("sql-ddl", "table.scriban", Ct))!.Hash, Ct);
        Assert.Equal(SaveOutcome.Saved, broken.Outcome);
        Assert.Contains(broken.Diagnostics, d => d.Rule == "MQ6003" && d.Line is not null && d.Message.Contains("unit 'table'", StringComparison.Ordinal));
        Assert.Equal("{{ if }", await File.ReadAllTextAsync(PackPath(repo, "sql-ddl", "table.scriban"), Ct));

        var nul = await repo.Service.WritePackFileAsync("sql-ddl", "bin.dat", "a\0b", null, Ct);
        Assert.Equal(SaveOutcome.Invalid, nul.Outcome);
        var large = await repo.Service.WritePackFileAsync("sql-ddl", "big.txt", new string('x', PackAuthoring.MaxFileBytes + 1), null, Ct);
        Assert.Equal(SaveOutcome.Invalid, large.Outcome);
    }

    [Theory]
    [InlineData("pack.json")]
    [InlineData("../csharp-dapper/pack.json")]
    [InlineData("/etc/passwd")]
    [InlineData("a/./b.scriban")]
    [InlineData(".hidden")]
    [InlineData("a\\b.scriban")]
    public async Task Refused_paths_never_write(string path)
    {
        await using var repo = EditorRepo.Create();
        var before = repo.Files();
        await Assert.ThrowsAsync<PackPathException>(() => repo.Service.WritePackFileAsync("sql-ddl", path, "x", null, Ct));
        Assert.Equal(before, repo.Files());
    }

    [Fact]
    public async Task A_link_that_leaves_the_pack_folder_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return;
        await using var repo = EditorRepo.Create();
        Directory.CreateSymbolicLink(PackPath(repo, "sql-ddl", "other"), repo.Repo.PathOf(".maquettiste/templates/csharp-dapper"));
        File.CreateSymbolicLink(PackPath(repo, "sql-ddl", "settings.json"), repo.Repo.PathOf(EditorRepo.SettingsPath));

        await Assert.ThrowsAsync<PackPathException>(() => repo.Service.WritePackFileAsync("sql-ddl", "other/new.scriban", "x", null, Ct));
        await Assert.ThrowsAsync<PackPathException>(() => repo.Service.ReadPackFileAsync("sql-ddl", "settings.json", Ct));
        Assert.False(File.Exists(repo.Repo.PathOf(".maquettiste/templates/csharp-dapper/new.scriban")));
    }

    [Fact]
    public async Task A_file_in_use_is_not_deleted_and_an_unused_one_is()
    {
        await using var repo = EditorRepo.Create();
        var table = (await repo.Service.ReadPackFileAsync("sql-ddl", "_table.scriban", Ct))!;
        var refused = await repo.Service.DeletePackFileAsync("sql-ddl", "_table.scriban", table.Hash, Ct);
        Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        Assert.Contains("include:table.scriban", refused.Diagnostics[0].Message, StringComparison.Ordinal);

        var readme = (await repo.Service.ReadPackFileAsync("sql-ddl", "README.md", Ct))!;
        Assert.Equal(SaveOutcome.Conflict, (await repo.Service.DeletePackFileAsync("sql-ddl", "README.md", new string('0', 64), Ct)).Outcome);
        Assert.Equal(SaveOutcome.Saved, (await repo.Service.DeletePackFileAsync("sql-ddl", "README.md", readme.Hash, Ct)).Outcome);
        Assert.False(File.Exists(PackPath(repo, "sql-ddl", "README.md")));
        Assert.Equal(SaveOutcome.NotFound, (await repo.Service.DeletePackFileAsync("sql-ddl", "README.md", readme.Hash, Ct)).Outcome);
    }

    [Fact]
    public async Task Pack_json_saves_whole_and_canonical_and_refuses_a_stale_hash_or_an_invalid_document()
    {
        await using var repo = EditorRepo.Create();
        var path = PackPath(repo, "sql-ddl", "pack.json");
        var original = File.ReadAllBytes(path);
        var hash = ContentHash.Of(original);
        var node = JsonNode.Parse(original)!.AsObject();
        node["description"] = "Edited in the editor.";
        node["parameterSchema"] = JsonNode.Parse("""{ "properties": { "quoting": { "type": "string", "enum": ["always", "never"] } } }""");
        var compact = Encoding.UTF8.GetBytes(node.ToJsonString());

        var saved = await repo.Service.SavePackAsync("sql-ddl", compact, hash, Ct);

        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        var written = File.ReadAllBytes(path);
        Assert.Equal(TestServices.Json.Write(JsonNode.Parse(compact)!, "pack.json", "templates/sql-ddl/pack.json"), written);
        Assert.Equal(ContentHash.Of(written), saved.Hash);
        Assert.Contains("\n", Encoding.UTF8.GetString(written), StringComparison.Ordinal);

        Assert.Equal(SaveOutcome.Conflict, (await repo.Service.SavePackAsync("sql-ddl", compact, hash, Ct)).Outcome);
        node["name"] = "other";
        Assert.Equal(SaveOutcome.Invalid, (await repo.Service.SavePackAsync("sql-ddl", Encoding.UTF8.GetBytes(node.ToJsonString()), saved.Hash!, Ct)).Outcome);
        node["name"] = "sql-ddl";
        node["units"]![0]!["for"] = "each tables";
        var scope = await repo.Service.SavePackAsync("sql-ddl", Encoding.UTF8.GetBytes(node.ToJsonString()), saved.Hash!, Ct);
        Assert.Equal(SaveOutcome.Invalid, scope.Outcome);
        var mq6021 = Assert.Single(scope.Diagnostics);
        Assert.Equal(("MQ6021", "/units/0/for"), (mq6021.Rule, mq6021.JsonPointer));
        Assert.Contains("'each table'", mq6021.Message, StringComparison.Ordinal);
        Assert.Equal(written, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_new_pack_comes_from_empty_or_from_a_pack_of_the_project()
    {
        await using var repo = EditorRepo.Create();
        var empty = await repo.Service.CreatePackAsync("docs", "empty", null, Ct);
        Assert.Equal(SaveOutcome.Saved, empty.Outcome);
        Assert.Equal(["entity.scriban", "pack.json"], empty.Files);
        var copy = await repo.Service.CreatePackAsync("ddl-copy", "sql-ddl", null, Ct);
        Assert.Equal(SaveOutcome.Saved, copy.Outcome);
        Assert.Contains("table.scriban", copy.Files);
        Assert.Equal("ddl-copy", (await repo.Service.GetPackAsync("ddl-copy", Ct))!.Document!.Value.GetProperty("name").GetString());
        Assert.Equal(SaveOutcome.Conflict, (await repo.Service.CreatePackAsync("docs", "empty", null, Ct)).Outcome);
        Assert.Equal(SaveOutcome.Invalid, (await repo.Service.CreatePackAsync("nope", "unknown-starter", null, Ct)).Outcome);
        var list = await repo.Service.ListPacksAsync(Ct);
        Assert.Equal(["csharp-dapper", "ddl-copy", "docs", "sql-ddl"], list.Select(p => p.Name));
        Assert.Equal(5, list.Single(p => p.Name == "sql-ddl").Units.Count);
    }

    [Fact]
    public async Task A_preview_renders_unsaved_template_text_and_a_unit_override_without_writing()
    {
        await using var repo = EditorRepo.Create();
        var before = repo.Files();
        var overlay = new Dictionary<string, string> { ["schema.scriban"] = "UNSAVED {{ 20 + 22 }} {{ database.name }}" };

        var result = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId, new PreviewOptions { Overlay = overlay }, Ct);

        var file = Assert.Single(result.Files);
        Assert.StartsWith("UNSAVED 42 ", file.Text, StringComparison.Ordinal);
        Assert.NotEmpty(result.ReadKeys);
        Assert.Equal(before, repo.Files());

        var saved = (await repo.Service.GetPackAsync("sql-ddl", Ct))!;
        var unit = JsonSerializer.Deserialize<PackUnit>(saved.Document!.Value.GetProperty("units")[1].GetRawText(), EngineJson.Options)! with { Output = "{{ kebab database.name }}/renamed.sql" };
        var moved = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId, new PreviewOptions { UnitOverride = unit }, Ct);
        Assert.EndsWith("/renamed.sql", Assert.Single(moved.Files).Path, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.Service.PreviewAsync("sql-ddl", "table", null, new PreviewOptions { UnitOverride = unit }, Ct));
    }

    [Fact]
    public async Task A_selector_units_preview_refuses_an_element_the_selector_does_not_return()
    {
        await using var repo = EditorRepo.Create();
        var result = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.CustomerId, null, Ct);
        Assert.Empty(result.Files);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MQ6026", diagnostic.Rule);
        Assert.Equal(EditorRepo.CustomerId, diagnostic.ElementId);
        Assert.Contains("(entity); pick one.", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newer_preview_on_the_same_connection_cancels_the_older_one_on_the_server()
    {
        await using var repo = EditorRepo.Create();
        repo.EditSettingsOnDisk(s => s["limits"] = new JsonObject { ["scriptTimeoutMs"] = 60000 });
        var loop = "{{ for a in 1..100000 }}{{ for b in 1..100000 }}{{ for c in 1..100000 }}{{ end }}{{ end }}{{ end }}";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var older = repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId,
            new PreviewOptions { Overlay = new Dictionary<string, string> { ["schema.scriban"] = loop }, Connection = "preview:c1" }, Ct);
        Assert.False(older.IsCompleted); // it waits on the snapshot at least; the newer request takes the connection's turn now

        var newer = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId, new PreviewOptions { Connection = "preview:c1" }, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => older);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
        Assert.Single(newer.Files);
        // Another connection is not affected.
        var other = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId, new PreviewOptions { Connection = "preview:c2" }, Ct);
        Assert.Equal(newer.Files[0].Text, other.Files[0].Text);
    }

    [Fact]
    public async Task The_preview_session_is_rebuilt_when_a_pack_file_or_the_model_changes()
    {
        await using var repo = EditorRepo.Create();
        var first = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId, null, Ct);
        var again = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId, null, Ct);
        Assert.Equal(first.Files[0].Text, again.Files[0].Text);

        File.AppendAllText(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/schema.scriban"), "-- appended on disk\n");
        var edited = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId, null, Ct);
        Assert.Contains("-- appended on disk", edited.Files[0].Text, StringComparison.Ordinal);

        var customer = repo.Repo.PathOf(".maquettiste/model/entities/customer.json");
        var node = JsonNode.Parse(File.ReadAllText(customer))!.AsObject();
        node["name"] = "Client";
        File.WriteAllText(customer, node.ToJsonString());
        var renamed = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId, null, Ct);
        Assert.NotEqual(edited.Files[0].Text, renamed.Files[0].Text);
    }

    [Fact]
    public async Task The_preview_session_key_changes_with_the_model_version_the_settings_and_the_pack_files_only()
    {
        await using var repo = EditorRepo.Create();
        var initial = await repo.Service.PreviewSessionKeyAsync("sql-ddl", Ct);
        Assert.Equal(initial, await repo.Service.PreviewSessionKeyAsync("sql-ddl", Ct));

        repo.EditSettingsOnDisk(s => s["limits"] = new JsonObject { ["scriptTimeoutMs"] = 3000 });
        var settings = await repo.Service.PreviewSessionKeyAsync("sql-ddl", Ct);
        Assert.NotEqual(initial, settings);

        File.AppendAllText(repo.Repo.PathOf(".maquettiste/templates/sql-ddl/table.scriban"), "-- template edit\n");
        var template = await repo.Service.PreviewSessionKeyAsync("sql-ddl", Ct);
        Assert.NotEqual(settings, template);
        // Another pack's files do not touch this pack's session.
        File.AppendAllText(repo.Repo.PathOf(".maquettiste/templates/csharp-dapper/pack.json"), "\n");
        Assert.Equal(template, await repo.Service.PreviewSessionKeyAsync("sql-ddl", Ct));

        var customer = repo.Repo.PathOf(".maquettiste/model/entities/customer.json");
        var node = JsonNode.Parse(File.ReadAllText(customer))!.AsObject();
        node["name"] = "Client";
        File.WriteAllText(customer, node.ToJsonString());
        var model = await repo.Service.PreviewSessionKeyAsync("sql-ddl", Ct);
        Assert.NotEqual(template, model);
        Assert.Equal(model, await repo.Service.PreviewSessionKeyAsync("sql-ddl", Ct));
    }

    [Fact]
    public async Task A_preview_that_does_not_end_fails_with_MQ6007_within_the_deadline()
    {
        await using var repo = EditorRepo.Create();
        repo.EditSettingsOnDisk(s => s["limits"] = new JsonObject { ["scriptTimeoutMs"] = 100 });
        var loop = "{{ for a in 1..100000 }}{{ for b in 1..100000 }}{{ for c in 1..100000 }}{{ end }}{{ end }}{{ end }}";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await repo.Service.PreviewAsync("sql-ddl", "schema", EditorRepo.MainDatabaseId,
            new PreviewOptions { Overlay = new Dictionary<string, string> { ["schema.scriban"] = loop } }, Ct);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
        Assert.Empty(result.Files);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ6007");
    }

    [Theory]
    [InlineData("{{ name }}.sql", "db", null)]
    [InlineData("gen/{{ name }}.sql", "", "'gen'")]
    [InlineData("../{{ name }}.sql", "db", "'.' or '..'")]
    [InlineData("/abs/{{ name }}.sql", "db", "absolute")]
    [InlineData("{{ name }}.cs", "src", null)]
    [InlineData("x.sql", "elsewhere", "'elsewhere/x.sql'")]
    public void MQ6019_checks_the_literal_prefix_against_the_roots(string pattern, string outputBase, string? problem)
    {
        OutputRoot[] roots = [new() { Path = "db" }, new() { Path = "src/Generated" }];
        var found = UnitRules.OutputRootProblem(pattern, outputBase, roots);
        if (problem is null)
            Assert.Null(found);
        else
            Assert.Contains(problem, found, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameter_values_are_checked_against_the_schema_and_undeclared_ones_warned()
    {
        var manifest = new PackManifest
        {
            Name = "p",
            Version = "1.0.0",
            Engine = ">=1.0",
            Units = [],
            Parameters = new Dictionary<string, JsonElement> { ["quoting"] = JsonSerializer.SerializeToElement("always") },
            ParameterSchema = JsonSerializer.SerializeToElement(new
            {
                properties = new { quoting = new { type = "string", @enum = new[] { "always", "never" } }, width = new { type = "integer", minimum = 1 } },
            }),
        };
        var settings = new PackSettings
        {
            Parameters = new Dictionary<string, JsonElement>
            {
                ["quoting"] = JsonSerializer.SerializeToElement("sometimes"),
                ["width"] = JsonSerializer.SerializeToElement(0),
                ["extra"] = JsonSerializer.SerializeToElement(true),
            },
        };

        var diagnostics = UnitRules.Parameters("p", manifest, settings, ".maquettiste/maquettiste.json");

        Assert.Equal([("MQ6024", "/packs/p/parameters/extra"), ("MQ6023", "/packs/p/parameters/quoting"), ("MQ6023", "/packs/p/parameters/width")],
            diagnostics.Select(d => (d.Rule, d.JsonPointer!)));
        Assert.Contains("one of", diagnostics[1].Message, StringComparison.Ordinal);
        Assert.Contains("at least 1", diagnostics[2].Message, StringComparison.Ordinal);
        Assert.Contains("did you mean 'each table'", UnitRules.UnknownScope("t", "each tables"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_explanation_gives_the_first_reason_that_applies_and_ordered_causes()
    {
        var unit = new PlanUnit("p/u/1", "h2", ["e:A", "e:C", "s:conventions"], false, []);
        var stored = new UnitState("p/u/1", "h1", ["e:A", "e:B", "s:conventions"], []);
        Assert.Equal("unchanged", PlanExplainer.Explain(unit with { Skipped = true }, stored, false, "/", _ => true).Reason);
        Assert.Equal("forced", PlanExplainer.Explain(unit, stored, true, "/", _ => true).Reason);
        Assert.Equal("new", PlanExplainer.Explain(unit, null, false, "/", _ => true).Reason);
        // generation-ui.md 4.2's order: new, forced, check, inputs, outputs, unchanged.
        Assert.Equal("new", PlanExplainer.Explain(unit, null, true, "/", _ => true).Reason);
        Assert.Equal("check", PlanExplainer.Explain(unit, stored, false, "/", _ => true, check: true).Reason);
        Assert.Equal("forced", PlanExplainer.Explain(unit, stored, true, "/", _ => true, check: true).Reason);

        // Element causes carry the element's name and kind when it is known, its id otherwise.
        var named = PlanExplainer.Explain(unit with { ReadKeys = ["e:A", "e:C", "r:C", "s:conventions"] }, stored, false, "/", id => id != "B",
            nameOf: id => id switch { "B" => "Invoice (entity)", "C" => "Customer (entity)", _ => null });
        Assert.Equal(["Invoice (entity) was deleted", "Customer (entity) is read for the first time", "Something that references Customer (entity) is read for the first time"],
            named.Causes.Select(c => c.Detail));
        Assert.Equal("B was deleted", PlanExplainer.Explain(unit, stored, false, "/", id => id != "B").Causes[0].Detail);

        var (reason, causes) = PlanExplainer.Explain(unit, stored, false, "/", id => id != "B");
        Assert.Equal("inputs", reason);
        Assert.Equal([("absent", "e:B"), ("element", "e:C")], causes.Select(c => (c.Kind, c.Key)));

        var same = PlanExplainer.Explain(unit with { InputHash = "h1" }, stored with { Outputs = [new UnitOutput("no/such/file.sql", new string('a', 64), 1, 1)] },
            false, Path.GetTempPath(), _ => true);
        Assert.Equal("outputs", same.Reason);
        Assert.Equal(("output-missing", "no/such/file.sql"), (same.Causes[0].Kind, same.Causes[0].Path));
    }

    [Fact]
    public async Task A_plan_explains_every_unit_and_why_not_groups_a_skipped_units_keys()
    {
        await using var repo = EditorRepo.Create();
        var first = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);
        Assert.NotNull(first.Plan);
        Assert.All(first.Plan.Units, u => Assert.Equal(("sql-ddl", "new"), (u.Pack, u.Reason)));
        Assert.Contains(first.Plan.Units, u => u.Unit == "table" && u.Template == "table.scriban" && u.ElementId is not null);

        await repo.Service.ApplyAsync(first.Plan.Id, null, Ct);
        var output = first.Plan.Units.First(u => u.Unit == "schema").Outputs[0].Path;
        File.AppendAllText(repo.Repo.PathOf(output), "-- hand edit\n");
        var second = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);

        var schema = second.Plan!.Units.Single(u => u.Unit == "schema");
        Assert.Equal("outputs", schema.Reason);
        Assert.Equal(("output-edited", output), (schema.Causes[0].Kind, schema.Causes[0].Path));
        var skipped = second.Plan.Units.First(u => u.Skipped);
        Assert.Equal(("unchanged", 0), (skipped.Reason, skipped.CauseCount));

        var detail = await repo.Service.GetPlanUnitAsync(second.Plan.Id, skipped.Key, Ct);
        Assert.NotNull(detail);
        Assert.StartsWith("Skipped: its ", detail.Summary, StringComparison.Ordinal);
        Assert.Equal(skipped.ReadKeys.Count, detail.Groups.Sum(g => g.Keys.Count));
        Assert.Null(await repo.Service.GetPlanUnitAsync(second.Plan.Id, "nope", Ct));

        // Element causes name the element on the real engine (generation-ui.md 4.2), never its id.
        await repo.Service.ApplyAsync(second.Plan.Id, null, Ct);
        var customer = repo.Repo.PathOf(".maquettiste/model/entities/customer.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(customer))!.AsObject();
        node["description"] = "Changed.";
        File.WriteAllText(customer, node.ToJsonString());
        await repo.Store.RescanAsync(false, Ct);
        var third = await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct);
        var details = third.Plan!.Units.SelectMany(u => u.Causes).Where(c => c.Key is not null && c.Key.EndsWith(EditorRepo.CustomerId, StringComparison.Ordinal))
            .Select(c => c.Detail).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(details);
        Assert.Contains("Customer (entity) changed", details);
        Assert.All(details, d => Assert.DoesNotContain(EditorRepo.CustomerId, d, StringComparison.Ordinal));
    }
}
