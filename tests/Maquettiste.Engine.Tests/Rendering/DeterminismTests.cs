using System.Collections.Concurrent;
using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Scripting;
using Scriban;

namespace Maquettiste.Engine.Tests.Rendering;

/// <summary>Culture independence, parallel rendering with one cache and one sandbox pool, ordering and cancellation.</summary>
public sealed class DeterminismTests
{
    private static async Task<IReadOnlyList<RenderedUnit>> RenderDemoAsync(int parallelism, ITemplateCache? cache = null, IScriptSandboxFactory? scripts = null)
    {
        var model = await BillingModel.GetAsync();
        var pack = RenderKit.LoadPack(RenderKit.FixturePackRoot(RenderKit.DemoPack), RenderKit.DemoPack, new PackSettings { Output = "out" });
        var context = RenderKit.Context(model, [pack], parallelism);
        if (scripts is not null)
            context = context with { Scripts = scripts };
        return await RenderKit.RenderAllAsync(RenderKit.NewRenderer(cache), RenderKit.Plan(model, pack), context);
    }

    private static string Flatten(IEnumerable<RenderedUnit> units) =>
        string.Join("\n\u0001\n", units.SelectMany(u => u.Files.Select(f => f.Path + "\n" + f.Text).Append(u.InputHash).Append(string.Join(' ', u.ReadKeys))));

    [Fact]
    public async Task Rendering_is_byte_identical_under_tr_TR_de_DE_and_the_invariant_culture()
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var outputs = new List<string>();
        try
        {
            foreach (var name in new[] { "", "tr-TR", "de-DE" })
            {
                var culture = new CultureInfo(name);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                outputs.Add(Flatten(await RenderDemoAsync(4)));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous.CurrentCulture;
            CultureInfo.CurrentUICulture = previous.CurrentUICulture;
        }

        Assert.Equal(outputs[0], outputs[1]);
        Assert.Equal(outputs[0], outputs[2]);
        Assert.Contains("1234567.891", outputs[0], StringComparison.Ordinal);
        Assert.Contains("3.25", outputs[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_worker_and_many_workers_give_the_same_units_in_the_same_order()
    {
        var sequential = await RenderDemoAsync(1);
        var parallel = await RenderDemoAsync(8);
        Assert.Equal(sequential.Select(u => u.Unit.Key), parallel.Select(u => u.Unit.Key));
        Assert.Equal(Flatten(sequential), Flatten(parallel));
    }

    [Fact]
    public async Task Many_units_share_one_template_cache_and_one_sandbox_pool_per_pack()
    {
        var model = await BillingModel.GetAsync();
        var units = new List<PackUnit>();
        for (var i = 0; i < 40; i++)
            units.Add(new PackUnit { Id = "u" + i.ToString(CultureInfo.InvariantCulture), Template = "main.scriban", For = "each entity", Output = "{{ unit.id }}/{{ entity.name }}.txt" });
        using var pack = new TempPack("many", new Dictionary<string, string>
        {
            ["main.scriban"] = "{{ unit.id }} {{ shout entity.name }} {{ for a in entity.attributes }}{{ a.name | pascal }}{{ end }} {{ include '_p.scriban' }}",
            ["_p.scriban"] = "{{ pluralize entity }}",
            ["helpers.js"] = "maquettiste.helper('shout', (s) => s.toUpperCase());",
        });
        var loaded = pack.Load(RenderKit.Manifest("many", [.. units]));
        var planned = RenderKit.Plan(model, loaded);
        Assert.Equal(40 * model.Entities.Count, planned.Count);

        var cache = new CountingCache(new TemplateCache());
        var factory = new CountingFactory(new ScriptSandboxFactory());
        var context = RenderKit.Context(model, [loaded], 8) with { Scripts = factory };
        var progress = new ConcurrentQueue<ProgressUpdate>();
        var rendered = new List<RenderedUnit>();
        await foreach (var unit in RenderKit.NewRenderer(cache).RenderAsync(planned, context, new SyncProgress(progress.Enqueue), TestContext.Current.CancellationToken))
            rendered.Add(unit);

        Assert.Equal(planned.Select(p => p.Key), rendered.Select(r => r.Unit.Key));
        Assert.All(rendered, r => Assert.False(r.Failed, string.Join("; ", r.Diagnostics.Select(d => d.Message))));
        Assert.Equal(2, cache.Distinct.Count);
        Assert.Equal(1, factory.Pools);
        Assert.Equal(8, factory.LastSize);
        var sample = rendered.Single(r => r.Unit.Unit.Id == "u7" && r.Unit.Element!.Id == model.Entities[1].Id);
        var entity = model.Entities[1];
        Assert.Equal($"u7 {entity.Name.ToUpperInvariant()} {string.Concat(entity.Attributes.Select(a => char.ToUpperInvariant(a.Name[0]) + a.Name[1..]))} {entity.PluralName}", sample.Files[0].Text);
        Assert.Equal(planned.Count, progress.Count);
        Assert.Equal(Enumerable.Range(1, planned.Count), progress.Select(p => p.Done));
        Assert.All(progress, p => Assert.Equal(PipelineStage.Render, p.Stage));
    }

    [Fact]
    public async Task Cancellation_stops_the_enumeration()
    {
        var model = await BillingModel.GetAsync();
        var pack = RenderKit.LoadPack(RenderKit.FixturePackRoot(RenderKit.DemoPack), RenderKit.DemoPack);
        var units = Enumerable.Repeat(RenderKit.Plan(model, pack), 20).SelectMany(u => u).ToList();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in RenderKit.NewRenderer().RenderAsync(units, RenderKit.Context(model, [pack], 4), null, cts.Token))
            {
                if (++seen == 3)
                    await cts.CancelAsync();
            }
        });
        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task Render_one_matches_the_streamed_render()
    {
        var model = await BillingModel.GetAsync();
        var pack = RenderKit.LoadPack(RenderKit.FixturePackRoot(RenderKit.DemoPack), RenderKit.DemoPack);
        var units = RenderKit.Plan(model, pack, "entity");
        var context = RenderKit.Context(model, [pack], 2);
        var streamed = await RenderKit.RenderAllAsync(RenderKit.NewRenderer(), units, context);
        var one = await RenderKit.NewRenderer().RenderOneAsync(units[2], context, TestContext.Current.CancellationToken);
        Assert.Equal(Flatten([streamed[2]]), Flatten([one]));
    }

    [Fact]
    public async Task An_empty_unit_list_yields_nothing()
    {
        var model = await BillingModel.GetAsync();
        Assert.Empty(await RenderKit.RenderAllAsync(RenderKit.NewRenderer(), [], RenderKit.Context(model, [])));
    }

    private sealed class SyncProgress(Action<ProgressUpdate> report) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => report(value);
    }

    private sealed class CountingCache(ITemplateCache inner) : ITemplateCache
    {
        public ConcurrentDictionary<Template, byte> Distinct { get; } = new(ReferenceEqualityComparer.Instance);

        public Template Get(LoadedPack pack, string path, Delimiters? delimiters)
        {
            var template = inner.Get(pack, path, delimiters);
            Distinct.TryAdd(template, 0);
            return template;
        }

        public Template GetInline(string text, string sourcePath, string jsonPointer) => inner.GetInline(text, sourcePath, jsonPointer);

        public string ReadText(LoadedPack pack, string path) => inner.ReadText(pack, path);

        public TemplateInfo? Describe(Template template) => inner.Describe(template);
    }

    private sealed class CountingFactory(IScriptSandboxFactory inner) : IScriptSandboxFactory
    {
        private int _pools;

        public int Pools => _pools;

        public int LastSize { get; private set; }

        public IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, SandboxLimits limits, int size, CancellationToken ct)
        {
            Interlocked.Increment(ref _pools);
            LastSize = size;
            return inner.CreatePool(scripts, limits, size, ct);
        }
    }
}
