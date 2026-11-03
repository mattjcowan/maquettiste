using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// What a whole-model run reuses instead of building it again (next-phase-2-design.md section 5.5): a plan after a validate of the same
/// snapshot takes the store's resolved model and its host, the calculation files load once for validation and the host, and the checks
/// after resolution build no rule pool when validation already loaded the rule files.
/// </summary>
public sealed class CalculatedValueReuseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Names = """
        maquettiste.calculated({ name: "publicName", kinds: ["entity"], schema: { type: "string" }, value(e) { return e.name.toLowerCase(); } });
        """;

    private const string ElementRule = """
        maquettiste.rule({ id: "short-names", kinds: ["entity"], check(e, model, report) {
          if (e.name.length > 60) report("Names stay short.");
        } });
        """;

    private const string ResolvedRule = """
        maquettiste.rule({ id: "routes", scope: "resolved", kinds: ["entity"], check(e, model, report) {
          if (e.name === "Customer") report("Customer is routed as " + e.calculated.publicName, { severity: "info" });
        } });
        """;

    private sealed record Setup(E2ERepo Repo, ModelStore Store, GenerationService Service, CountingSandboxFactory Factory,
        CalculationService Calculations, ResolvedChecks Checks, CountingResolver Resolver) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            await Repo.DisposeAsync();
        }
    }

    private static async Task<Setup> Create(string? rules = null)
    {
        var repo = E2ERepo.Create(demo: false);
        var root = repo.Repo.ModelRoot;
        Directory.CreateDirectory(Path.Combine(root, "extensions", "calculated"));
        await File.WriteAllTextAsync(Path.Combine(root, "extensions", "calculated", "names.js"), Names, Ct);
        if (rules is not null)
        {
            Directory.CreateDirectory(Path.Combine(root, "extensions", "rules"));
            await File.WriteAllTextAsync(Path.Combine(root, "extensions", "rules", "invoicing.js"), rules, Ct);
        }

        var factory = new CountingSandboxFactory();
        var services = EngineServices.Create(repo.Options);
        var calculations = new CalculationService(factory, 2);
        var inner = new ModelResolver(repo.Options, factory, services.SeedRows, calculations);
        var resolver = new CountingResolver(inner);
        var checks = new ResolvedChecks(repo.Options, factory, calculations);
        services = services with
        {
            Scripts = factory, Resolver = resolver, Calculations = calculations, Checks = checks,
            Validator = new ModelValidator(repo.Options, services.Schemas, factory, inner, services.SeedRows, calculations),
        };
        var store = new ModelStore(repo.Options, services);
        return new Setup(repo, store, new GenerationService(store, repo.Options, services), factory, calculations, checks, resolver);
    }

    [Fact]
    public async Task A_plan_after_a_validate_of_the_same_snapshot_resolves_nothing_again_and_keeps_the_cached_values()
    {
        await using var setup = await Create();

        var report = await setup.Store.ValidateAsync(ValidationScope.All, Ct);
        var (resolves, calls, cached) = (setup.Resolver.Calls, setup.Factory.CalculationCalls, setup.Calculations.Cache.Count);
        var plan = await setup.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);

        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(RunOutcome.Succeeded, plan.Outcome);
        Assert.Equal(1, resolves);
        Assert.True(calls > 0);
        Assert.Equal((1, calls), (setup.Resolver.Calls, setup.Factory.CalculationCalls));
        // The plan's checks read the host's memo; its sweep still keeps every value the validate stored, for the next snapshot.
        Assert.Equal(cached, setup.Calculations.Cache.Count);
    }

    [Fact]
    public async Task A_validate_after_a_plan_of_the_same_snapshot_takes_the_plan_model()
    {
        await using var setup = await Create();

        var plan = await setup.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
        var calls = setup.Factory.CalculationCalls;
        var report = await setup.Store.ValidateAsync(ValidationScope.All, Ct);

        Assert.Equal(RunOutcome.Succeeded, plan.Outcome);
        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(1, setup.Resolver.Calls);
        Assert.Equal(calls, setup.Factory.CalculationCalls);
    }

    [Fact]
    public async Task A_whole_model_validate_loads_the_calculation_files_once()
    {
        await using var setup = await Create();

        var first = await setup.Store.ValidateAsync(ValidationScope.All, Ct);
        var pools = setup.Factory.PoolsCreated;
        var plan = await setup.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);

        Assert.DoesNotContain(first.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(RunOutcome.Succeeded, plan.Outcome);
        Assert.Equal(1, pools); // validation reports from the load the host then evaluates with
        Assert.Equal(1, setup.Calculations.PoolsCreated); // the plan's validation reads the same load
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_checks_build_no_rule_pool_after_validation_loaded_the_rule_files(bool resolvedRule)
    {
        await using var setup = await Create(ElementRule + (resolvedRule ? ResolvedRule : ""));

        var report = await setup.Store.ValidateAsync(ValidationScope.All, Ct);
        var plan = await setup.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);

        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(RunOutcome.Succeeded, plan.Outcome);
        Assert.Equal(0, setup.Checks.PoolsCreated);
        Assert.Equal(resolvedRule, report.Diagnostics.Any(d => d.Rule == "x/routes" && d.Message == "Customer is routed as customer"));
        Assert.Equal(resolvedRule, setup.Factory.ResolvedRuleCalls > 0);
    }

    private sealed class CountingResolver(IModelResolver inner) : IModelResolver
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return inner.ResolveAsync(model, progress, ct);
        }
    }
}
