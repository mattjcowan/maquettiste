using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Generation;

/// <summary>
/// A pipelined run (<see cref="GenerationRequest.StageBarriers"/> off) starts resolution beside validation, so the resolver sees
/// snapshots validation rejects. On every invalid shape the run must end <see cref="RunOutcome.Invalid"/>, promptly, and the
/// speculative resolution must have stopped before the run returns (it never outlives the run or its lock).
/// </summary>
public sealed class SpeculativeResolutionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    public static TheoryData<string> Families => ["cycles", "references", "relations", "values", "names-keys", "physical", "mappings"];

    [Theory]
    [MemberData(nameof(Families))]
    public async Task Invalid_fixture_models_end_invalid_and_the_speculative_resolution_stops_first(string family)
    {
        var tracking = new TrackingResolver();
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b), s => Track(s, tracking), "basic");
        CopyFixtureModel(f, family);

        var result = await RunPipelinedAsync(f);

        Assert.Equal(RunOutcome.Invalid, result.Outcome);
        Assert.True(tracking.Calls > 0, "the pipelined run did not start the speculative resolution");
        Assert.Equal(tracking.Calls, tracking.Finished);
        Assert.Empty(f.Renderer.Rendered);
    }

    [Fact]
    public async Task A_self_embedding_value_object_ends_invalid_and_the_speculative_resolution_stops_first()
    {
        var tracking = new TrackingResolver();
        await using var f = await GenerationFixture.CreateAsync(b => Models.Shop(b, extra: (m, customer, _) =>
        {
            // Four members of its own type: expanded to the depth limit this is 4^16 embeddings (MQ3015 rejects it).
            var node = m.ValueObject("Node");
            node.Attr("a", node).Attr("b", node).Attr("c", node).Attr("d", node);
            customer.Attr("node", node);
        }), s => Track(s, tracking), "basic");

        var result = await RunPipelinedAsync(f);

        Assert.Equal(RunOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ3015");
        Assert.Equal(1, tracking.Calls);
        Assert.Equal(1, tracking.Finished);
    }

    [Fact]
    public async Task The_resolver_terminates_on_a_value_object_containment_cycle()
    {
        var b = new ModelBuilder(3);
        var main = b.Database("main", Dialect.PostgreSql);
        var node = b.ValueObject("Node");
        var edge = b.ValueObject("Edge");
        node.Attr("a", node).Attr("b", edge).Attr("c", node).Attr("d", edge);
        edge.Attr("from", node).Attr("to", node).Attr("self", edge);
        var site = b.Entity("Site").Key("id", "uuid").Attr("n", node).Attr("e", edge);
        b.Table("sites", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Binds(site);
        var built = b.Build();

        // Bounded, so a resolver that does not terminate fails the test instead of hanging the run.
        var resolve = Task.Run(() => new ModelResolver(ResolutionKit.Options).ResolveAsync(built, null, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(resolve, Task.Delay(Bound, TestContext.Current.CancellationToken));
        Assert.Same(resolve, finished);
        var model = await resolve;

        Assert.Equal(["id", "n", "e"], model.Entity("Site").Attributes.Select(a => a.Name));
        Assert.Single(model.Db("main").Table("sites").Columns);
    }

    [Fact]
    public async Task The_resolver_terminates_on_an_inheritance_cycle_in_a_database()
    {
        var b = new ModelBuilder(4);
        var db = b.Database("main", Dialect.PostgreSql);
        var a = b.Entity("Alpha").Key("id", "uuid").Attr("code", "string", x => x.Unique());
        var beta = b.Entity("Beta").Base(a).Attr("size", "int32");
        var gamma = b.Entity("Gamma").Base(beta).Attr("rank", "int32", x => x.Indexed());
        a.Base(gamma); // Alpha → Gamma → Beta → Alpha (MQ3002)
        b.Entity("Owner").Key("id", "uuid");
        b.Relation("owns", b.Entity("Holder").Key("id", "uuid"), beta);
        b.Table("alphas", db).Column("id", "uuid", nullable: false).Column("code", "string").PrimaryKey("id").Binds(a);
        b.Table("betas", db).Column("id", "uuid", nullable: false).Column("size", "int32").PrimaryKey("id").Binds(beta);
        b.Table("gammas", db).Column("id", "uuid", nullable: false).Column("rank", "int32").PrimaryKey("id").Binds(gamma);
        var model = b.Build();

        var resolve = Task.Run(() => new ModelResolver(ResolutionKit.Options).ResolveAsync(model, null, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var finished = await Task.WhenAny(resolve, Task.Delay(Bound, TestContext.Current.CancellationToken));
        Assert.Same(resolve, finished);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public async Task The_resolver_terminates_on_every_invalid_fixture_model(string family)
    {
        var model = Validation.ValidationFixture.Load(family);
        var resolve = Task.Run(() => new ModelResolver(Resolution.ResolutionKit.Options).ResolveAsync(model, null, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        // Its result, or its exception, is discarded for an invalid snapshot; it only has to end.
        var finished = await Task.WhenAny(resolve, Task.Delay(Bound, TestContext.Current.CancellationToken));
        Assert.Same(resolve, finished);
    }

    private static async Task<GenerationResult> RunPipelinedAsync(GenerationFixture f)
    {
        var run = f.Service.RunAsync(new GenerationRequest { StageBarriers = false, Jobs = 2 }, null, GenerationFixture.Ct);
        var finished = await Task.WhenAny(run, Task.Delay(Bound, GenerationFixture.Ct));
        Assert.Same(run, finished);
        return await run;
    }

    private static EngineServices Track(EngineServices services, TrackingResolver tracking)
    {
        tracking.Inner = services.Resolver;
        return services with { Resolver = tracking };
    }

    private static void CopyFixtureModel(GenerationFixture f, string family)
    {
        var source = Path.Combine(Validation.ValidationFixture.RepoRoot(family), ".maquettiste", "model");
        foreach (var file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories))
        {
            var target = Path.Combine(f.Repo.ModelRoot, "model", Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Counts resolutions started and finished (successfully, cancelled or failed).</summary>
    private sealed class TrackingResolver : IModelResolver
    {
        private int _calls;
        private int _finished;

        public IModelResolver Inner { get; set; } = null!;

        public int Calls => Volatile.Read(ref _calls);

        public int Finished => Volatile.Read(ref _finished);

        public async Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                return await Inner.ResolveAsync(model, progress, ct);
            }
            finally
            {
                Interlocked.Increment(ref _finished);
            }
        }
    }
}
