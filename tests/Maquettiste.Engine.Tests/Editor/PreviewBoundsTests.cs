using System.Collections.Concurrent;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// The bounds of what writing a template costs (generation-ui.md section 5.2, "Bounds"; the owner: "if I'm not clicking generate
/// somewhere, you should not be generating the full model ever"): a preview plans no unit and renders one unit for one element; a path
/// listing plans its own unit only and renders only the elements asked for; concurrent requests share one session and one resolution,
/// kept until the model, the settings or the pack change; and a template that reads no seed and no schema diff converts no seed row and
/// diffs no database.
/// </summary>
public sealed class PreviewBoundsTests
{
    private static CancellationToken Ct => EditorRepo.Ct;

    private static string TableKey => EditorRepo.CustomerId + "@" + EditorRepo.MainDatabaseId;

    [Fact]
    public async Task A_preview_plans_no_unit_and_renders_one_unit_for_one_element()
    {
        await using var repo = EditorRepo.Create();
        var (service, planner, renderer) = Counted(repo);

        var table = await service.PreviewAsync("sql-ddl", "table", TableKey, null, Ct);
        var entity = await service.PreviewAsync("csharp-dapper", "entity", EditorRepo.CustomerId, null, Ct);

        Assert.DoesNotContain(table.Diagnostics.Concat(entity.Diagnostics), d => d.Severity == DiagnosticSeverity.Error);
        Assert.NotEmpty(table.Files);
        Assert.NotEmpty(entity.Files);
        Assert.Equal(0, planner.Calls);
        Assert.Equal(["sql-ddl/table:" + TableKey, "csharp-dapper/entity:" + EditorRepo.CustomerId], renderer.Keys);
    }

    [Fact]
    public async Task A_path_listing_plans_only_its_unit_and_renders_only_the_elements_asked_for()
    {
        await using var repo = EditorRepo.Create();
        var (service, planner, renderer) = Counted(repo);

        var listing = await service.PathsAsync("sql-ddl", "table", null, null, 0, Ct);
        Assert.True(listing.Count > 2);
        Assert.Equal(0, listing.Rendered);
        Assert.Empty(renderer.Keys);
        Assert.Equal([1], planner.UnitsPerCall);

        var asked = listing.Elements.Take(2).Select(e => e.Id).ToList();
        var two = await service.PathsAsync("sql-ddl", "table", asked, null, 0, Ct);
        Assert.Equal(2, two.Rendered);
        Assert.Equal(asked.Select(id => "sql-ddl/table:" + id).Order(StringComparer.Ordinal), renderer.Keys.Order(StringComparer.Ordinal));
        // The saved unit's listing is kept with the session: asking again plans nothing.
        Assert.Equal([1], planner.UnitsPerCall);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PathsAsync("sql-ddl", "table", [.. Enumerable.Range(0, GenerationService.MaxRenderedPaths + 1).Select(i => "x" + i)], null, 0, Ct));
    }

    [Fact]
    public async Task Explaining_one_unit_plans_and_renders_that_unit_alone_with_the_answer_a_whole_plan_gives()
    {
        await using var repo = EditorRepo.Create();
        var (service, planner, renderer) = Counted(repo);

        var explained = await service.ExplainAsync("sql-ddl", "table", TableKey, null, Ct);
        Assert.NotNull(explained);
        Assert.Equal((true, "new"), (explained.Planned, explained.Reason));
        // One unit planned (the pack has more), and only that unit for that element rendered.
        Assert.Equal([1], planner.UnitsPerCall);
        Assert.Equal(["sql-ddl/table:" + TableKey], renderer.Keys);
        var scoped = (await service.GetPlanAsync(explained.PlanId!, Ct))!;
        Assert.Equal([explained.Key], scoped.Units.Select(u => u.Key));

        // The same answer as the unit's in a plan of the whole pack, input hash and outputs included.
        var whole = (await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct)).Plan!;
        var unit = whole.Units.Single(u => u.Key == explained.Key);
        var mine = explained.PlanUnit!;
        Assert.Equal((unit.Reason, unit.InputHash, unit.Skipped, unit.CauseCount), (mine.Reason, mine.InputHash, mine.Skipped, mine.CauseCount));
        Assert.Equal(unit.ReadKeys, mine.ReadKeys);
        Assert.Equal(unit.Outputs.Select(o => o.Path), mine.Outputs.Select(o => o.Path));
        Assert.True(whole.Units.Select(u => u.Unit).Distinct(StringComparer.Ordinal).Count() > 1);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_session_and_one_resolution_kept_until_the_model_changes()
    {
        await using var repo = EditorRepo.Create();
        var services = EngineServices.Create(repo.Repo.Options);
        var resolver = new CountingResolver(services.Resolver);
        await using var store = new ModelStore(repo.Repo.Options, services with { Resolver = resolver });
        var service = new GenerationService(store, repo.Repo.Options);

        // What opening a template fires at once, and the editor's validation loop beside it.
        await Task.WhenAll(
            service.PreviewAsync("sql-ddl", "table", TableKey, null, Ct),
            service.PreviewAsync("sql-ddl", "table", TableKey, null, Ct),
            service.PathsAsync("sql-ddl", "table", null, null, 0, Ct),
            service.GetTemplateContextAsync("sql-ddl", "table", Ct),
            store.ValidateAsync(ValidationScope.All, Ct));
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(1, service.SessionsPrepared);

        // No time limit: a preview a few seconds later reuses the session.
        await Task.Delay(TimeSpan.FromMilliseconds(2500), Ct);
        await service.PreviewAsync("sql-ddl", "table", TableKey, null, Ct);
        Assert.Equal((1, 1), (resolver.Calls, service.SessionsPrepared));

        // A model change makes a new session over a new resolution.
        var customer = (await store.GetElementAsync(EditorRepo.CustomerId, Ct))!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(customer.Json.GetRawText())!.AsObject();
        node["description"] = "Changed.";
        var saved = await store.SaveAsync(EditorRepo.CustomerId, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), customer.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        await service.PreviewAsync("sql-ddl", "table", TableKey, null, Ct);
        Assert.Equal((2, 2), (resolver.Calls, service.SessionsPrepared));
    }

    [Fact]
    public async Task A_preview_session_sees_an_edit_of_a_used_partial()
    {
        await using var repo = EditorRepo.Create(packs: false);
        repo.WriteLibraryPacks();
        var service = repo.Service;

        var first = await service.PreviewAsync("records", "record", EditorRepo.CustomerId, null, Ct);
        Assert.DoesNotContain(first.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var file = Assert.Single(first.Files);
        Assert.Equal("src/Generated/records/CustomerRecord.cs", file.Path);
        Assert.StartsWith("// Customer (CustomerRecord)\n", file.Text, StringComparison.Ordinal);

        // The session's stamp covers the used pack's folder: an edit there opens a new session, and the preview renders it.
        var key = await service.PreviewSessionKeyAsync("records", Ct);
        File.WriteAllText(repo.Repo.PathOf(".maquettiste/templates/naming/partials/header.scriban"), "// edited header for {{ element.name }}\n");
        Assert.NotEqual(key, await service.PreviewSessionKeyAsync("records", Ct));
        var second = await service.PreviewAsync("records", "record", EditorRepo.CustomerId, null, Ct);
        Assert.StartsWith("// edited header for Customer\n", Assert.Single(second.Files).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preview_converts_no_seed_row_and_diffs_no_database_unless_its_template_reads_them()
    {
        await using var repo = EditorRepo.Create();
        Directory.CreateDirectory(repo.Repo.PathOf(".maquettiste/model/seeds"));
        File.WriteAllText(repo.Repo.PathOf(".maquettiste/model/seeds/customer.json"), CustomerSeed);

        var entity = await repo.Service.PreviewAsync("csharp-dapper", "entity", EditorRepo.CustomerId, null, Ct);
        var table = await repo.Service.PreviewAsync("sql-ddl", "table", TableKey, null, Ct);
        Assert.DoesNotContain(entity.Diagnostics.Concat(table.Diagnostics), d => d.Severity == DiagnosticSeverity.Error);
        var resolved = (await repo.Store.ResolvedAsync(await repo.Store.GetSnapshotAsync(Ct), Ct)).Model!;
        var seed = Assert.Single(resolved.Seeds, s => s.Target is REntity { Name: "Customer" });
        Assert.Equal(3, seed.Rows.Count);
        Assert.All(seed.Rows, r => Assert.False(r.ValuesBuilt));
        Assert.Equal(0, repo.Service.SchemaDiffsComputed("sql-ddl"));

        // The seed script reads the rows; the migration reads one database's diff.
        var seedScript = await repo.Service.PreviewAsync("sql-ddl", "seed", EditorRepo.MainDatabaseId, null, Ct);
        Assert.Contains(seedScript.Files, f => f.Text.Contains("Ada", StringComparison.Ordinal));
        Assert.All(seed.Rows, r => Assert.True(r.ValuesBuilt));
        var migration = await repo.Service.PreviewAsync("sql-ddl", "migration", EditorRepo.MainDatabaseId, null, Ct);
        Assert.DoesNotContain(migration.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(1, repo.Service.SchemaDiffsComputed("sql-ddl"));
    }

    private const string CustomerSeed = """
        {
          "$schema": "../../.schema/v1/seed.json",
          "kind": "seed",
          "id": "01J92P0V2S0000000000000001",
          "name": "Customers",
          "target": "01J92P0V0ETQKXXP951CMMNHH3",
          "columns": ["01J92P0V0KGPC29TQQG8R57EBM", "01J92P0V0MS09YFZHX07JQ3KMN", "01J92P0V0NRX99014KNVAZGPEW", "01J92P0V26XYZRDQ8FJ6KZXT6S"],
          "rows": [
            { "id": "01J92P0V2S0000000000000011", "values": ["0190a0a0-0000-7000-8000-000000000001", "Ada", "ada@example.com", "2024-01-01T00:00:00Z"] },
            { "id": "01J92P0V2S0000000000000012", "values": ["0190a0a0-0000-7000-8000-000000000002", "Grace", "grace@example.com", "2024-01-02T00:00:00Z"] },
            { "id": "01J92P0V2S0000000000000013", "values": ["0190a0a0-0000-7000-8000-000000000003", "Edsger", "edsger@example.com", "2024-01-03T00:00:00Z"] }
          ]
        }
        """;

    private static (GenerationService Service, CountingPlanner Planner, CountingRenderer Renderer) Counted(EditorRepo repo)
    {
        var services = EngineServices.Create(repo.Repo.Options);
        var planner = new CountingPlanner(services.Planner);
        var renderer = new CountingRenderer(new Renderer(repo.Repo.Options, new TemplateCache()));
        return (new GenerationService(repo.Store, repo.Repo.Options, services with { Planner = planner, RendererFactory = () => renderer }), planner, renderer);
    }

    private sealed class CountingPlanner(IUnitPlanner inner) : IUnitPlanner
    {
        private readonly ConcurrentQueue<int> _units = new();

        public int Calls => _units.Count;

        /// <summary>How many units each call was asked to plan (the pack set's units).</summary>
        public IReadOnlyList<int> UnitsPerCall => [.. _units];

        public Task<UnitPlan> PlanAsync(ResolvedModel model, PackSet packs, Maquettiste.Engine.Scripting.IScriptSandboxFactory scripts, IProgress<ProgressUpdate>? progress,
            CancellationToken ct)
        {
            _units.Enqueue(packs.Packs.Sum(p => p.Manifest.Units.Count));
            return inner.PlanAsync(model, packs, scripts, progress, ct);
        }
    }

    private sealed class CountingRenderer(IRenderer inner) : IRenderer
    {
        private readonly ConcurrentQueue<string> _keys = new();

        public IReadOnlyList<string> Keys => [.. _keys];

        public IAsyncEnumerable<RenderedUnit> RenderAsync(IReadOnlyList<PlannedUnit> units, RenderContext context, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            foreach (var unit in units)
                _keys.Enqueue(unit.Key);
            return inner.RenderAsync(units, context, progress, ct);
        }

        public Task<RenderedUnit> RenderOneAsync(PlannedUnit unit, RenderContext context, CancellationToken ct)
        {
            _keys.Enqueue(unit.Key);
            return inner.RenderOneAsync(unit, context, ct);
        }
    }

    private sealed class CountingResolver(IModelResolver inner) : IModelResolver
    {
        public int Calls;

        public Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.ResolveAsync(model, progress, ct);
        }
    }

    [Fact]
    public async Task A_preview_evaluates_the_records_of_its_element_only()
    {
        await using var repo = EditorRepo.Create();
        WriteExportPacks(repo);

        var preview = await repo.Service.PreviewAsync("services", "service", EditorRepo.CustomerId, null, TestContext.Current.CancellationToken);

        Assert.Equal("uses CustomerRecord", preview.Files[0].Text);
        Assert.Equal(["px:records/" + EditorRepo.CustomerId], preview.ReadKeys.Where(k => k.StartsWith("px:", StringComparison.Ordinal)));
    }

    private static void WriteExportPacks(EditorRepo repo)
    {
        void Write(string pack, string path, string text)
        {
            var full = repo.Repo.PathOf(".maquettiste/templates/" + pack + "/" + path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        Write("records", "pack.json", """
            { "name": "records", "version": "1.0.0", "engine": ">=1.0",
              "units": [ { "id": "record", "template": "record.scriban", "for": "each entity", "output": "src/Generated/records/{{ exports.class_name }}.cs",
                "exports": { "class_name": "{{ pascal entity.name }}Record", "members": { "each": "attributes", "value": "{{ pascal item.name }}" } } } ] }
            """);
        Write("records", "record.scriban", "class {{ exports.class_name }}");
        Write("services", "pack.json", """
            { "name": "services", "version": "1.0.0", "engine": ">=1.0", "uses": [ { "pack": "records", "version": "^1.0.0" } ],
              "units": [ { "id": "service", "template": "service.scriban", "for": "each entity", "output": "src/Generated/services/{{ entity.name }}Service.cs" } ] }
            """);
        Write("services", "service.scriban", "{{ r = entity | exported \"records\" }}uses {{ r.class_name }}");
    }
}
