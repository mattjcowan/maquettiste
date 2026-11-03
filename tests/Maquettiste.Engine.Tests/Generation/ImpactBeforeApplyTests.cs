using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Engine.Tests.Integration;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// Impact before apply, the scope's "Done when" (next-phase-2-design.md section 6.1): the dry run of renaming an attribute lists what a
/// plan renders once the batch is applied, and it resolves, plans and renders nothing on the way.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ImpactBeforeApplyTests
{
    private const string Number = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string Overlay = EditorRepo.OverlayTableId;
    private const string InvoicesByCustomer = "01K6QRY0000000000000000001";
    private const string Main = EditorRepo.MainDatabaseId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Full runs until nothing is left to do, as <c>ScopedGenerationTests</c> settles its repo.</summary>
    private static async Task SettleAsync(EditorRepo repo)
    {
        for (var i = 0; i < 3; i++)
        {
            var result = await repo.Service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
            if (i == 2)
                Assert.Equal(0, result.UnitsRendered);
        }
    }

    /// <summary>The batch that renames Invoice's attribute <c>number</c> to <c>code</c>, keeping its id.</summary>
    private static async Task<ModelBatch> RenameNumberAsync(ModelStore store)
    {
        var document = (await store.GetElementAsync(EditorRepo.InvoiceId, Ct))!;
        var node = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        node["attributes"]!.AsArray().Single(a => (string?)a!["id"] == Number)!["name"] = "code";
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes($$"""
            { "operations": [ { "op": "update", "id": "@entity:Billing.Invoice", "expectedHash": "{{document.Hash}}", "element": {{node.ToJsonString()}} } ] }
            """));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return parsed.Batch;
    }

    private static bool ReachOnly(string key) =>
        key.StartsWith("d:", StringComparison.Ordinal) || key.StartsWith("n:table:", StringComparison.Ordinal) || key == "k:migration" || key == "r:" + Main;

    [Fact]
    public async Task The_dry_run_of_renaming_Invoice_number_lists_what_a_plan_renders_after_apply()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        var batch = await RenameNumberAsync(repo.Store);

        var preview = await repo.Service.PreviewBatchAsync(batch, Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Empty(preview.DiagnosticsAdded);
        Assert.Null(preview.Facts);
        var impact = preview.Impact!;
        Assert.Equal([new ImpactDatabase(Main, "main")], impact.Databases);
        Assert.Equal([(Overlay, Number, "changed"), (InvoicesByCustomer, Number, "changed")],
            impact.Referrers.Select(r => (r.Reference.FromElementId, r.Target, r.Change)).Distinct());
        Assert.DoesNotContain(impact.Referrers, r => r.FromKind == "relation");
        Assert.Equal([("csharp-dapper", "known"), ("sql-ddl", "known")], impact.Packs!.Select(p => (p.Pack, p.State)));
        var listed = impact.Packs!.SelectMany(p => p.Units).ToDictionary(u => u.Key, StringComparer.Ordinal);
        Assert.All(impact.Packs!, p => Assert.True(p.UnitsTotal <= ImpactPack.MaxUnits));

        var applied = await repo.Store.ApplyBatchAsync(batch, ChangeSource.Cli, Ct);
        Assert.Equal(SaveOutcome.Saved, applied.Outcome);
        var plan = (await repo.Service.PlanAsync(new GenerationRequest(), null, Ct)).Plan!;

        var rendered = plan.Units.Where(u => !u.Skipped).Select(u => u.Key).ToList();
        Assert.NotEmpty(rendered);
        Assert.All(rendered, key => Assert.True(listed.TryGetValue(key, out var unit) && unit.Change == "inputs", key + " rendered but not listed"));
        foreach (var unit in listed.Values.Where(u => plan.Units.Any(p => p.Key == u.Key && p.Skipped)))
            Assert.True(unit.Keys.All(ReachOnly), unit.Key + " skipped but listed through " + string.Join(", ", unit.Keys));
        var outputs = listed.Values.SelectMany(u => u.Outputs).ToHashSet(StringComparer.Ordinal);
        var changed = plan.Changes.Where(c => c.Kind is FileChangeKind.Added or FileChangeKind.Modified or FileChangeKind.Deleted).ToList();
        Assert.NotEmpty(changed);
        // A path a listed unit has not written before (the migration's next file) is known only after rendering: its unit is listed.
        Assert.All(changed, c => Assert.True(outputs.Contains(c.Path) || (c.Kind == FileChangeKind.Added && listed.ContainsKey(c.UnitKey)),
            c.Path + " changes but is not listed"));
        Assert.Contains(changed, c => outputs.Contains(c.Path));
    }

    [Fact]
    public async Task The_dry_run_resolves_plans_and_renders_nothing()
    {
        await using var repo = EditorRepo.Create();
        await SettleAsync(repo);
        var options = repo.Repo.Options;
        var services = EngineServices.Create(options);
        var resolver = new CountingResolver(services.Resolver);
        var planner = new CountingPlanner(services.Planner);
        var renderer = new CountingRenderer(new Renderer(options, new TemplateCache()));
        services = services with { Resolver = resolver, Planner = planner, RendererFactory = () => renderer };
        await using var store = new ModelStore(options, services);
        var service = new GenerationService(store, options, services);
        await store.LoadAsync(Ct);
        var batch = await RenameNumberAsync(store);
        var files = repo.Files();
        var states = StateFiles(repo);
        var (resolves, plans) = (resolver.Calls, planner.Calls);

        var preview = await service.PreviewBatchAsync(batch, Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.NotEmpty(preview.Impact!.Packs!.SelectMany(p => p.Units));
        Assert.Equal((resolves, plans, 0), (resolver.Calls, planner.Calls, renderer.Calls));
        Assert.Equal(0, resolves);
        Assert.Equal(files, repo.Files());
        Assert.Equal(states, StateFiles(repo));
    }

    private static SortedDictionary<string, string> StateFiles(EditorRepo repo)
    {
        var folder = Path.Combine(repo.Repo.CacheDirectory, "units");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(folder, file)] = Convert.ToHexString(File.ReadAllBytes(file));
        Assert.NotEmpty(files);
        return files;
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

    private sealed class CountingPlanner(IUnitPlanner inner) : IUnitPlanner
    {
        public int Calls;

        public Task<UnitPlan> PlanAsync(ResolvedModel model, PackSet packs, IScriptSandboxFactory scripts, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.PlanAsync(model, packs, scripts, progress, ct);
        }
    }

    private sealed class CountingRenderer(IRenderer inner) : IRenderer
    {
        private readonly ConcurrentQueue<string> _keys = new();

        public int Calls => _keys.Count;

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
}
