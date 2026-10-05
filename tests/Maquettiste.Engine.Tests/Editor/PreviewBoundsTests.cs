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
}
