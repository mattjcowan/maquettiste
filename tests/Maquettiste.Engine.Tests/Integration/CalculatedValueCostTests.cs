using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>The owner's cost requirement for calculated values (next-phase-2-design.md section 5.5): a project that declares none pays nothing.</summary>
public sealed class CalculatedValueCostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_project_without_calculated_values_creates_no_pool_and_evaluates_nothing()
    {
        await using var repo = E2ERepo.Create(demo: false);
        var factory = new CountingSandboxFactory();
        var services = EngineServices.Create(repo.Options);
        var calculations = new CalculationService(factory, 2);
        var resolver = new ModelResolver(repo.Options, factory, services.SeedRows, calculations);
        services = services with
        {
            Scripts = factory, Resolver = resolver, Calculations = calculations,
            Validator = new ModelValidator(repo.Options, services.Schemas, factory, resolver, services.SeedRows),
        };
        await using var store = new ModelStore(repo.Options, services);
        var service = new GenerationService(store, repo.Options);

        var report = await store.ValidateAsync(ValidationScope.All, Ct);
        var plan = await service.RunAsync(new GenerationRequest { Mode = GenerationMode.DryRun, Jobs = 2 }, null, Ct);
        var apply = await service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2 }, null, Ct);
        var resolved = await resolver.ResolveAsync(await store.GetSnapshotAsync(Ct), null, Ct);

        Assert.DoesNotContain(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, plan);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, apply);
        Assert.Equal(0, calculations.PoolsCreated);
        Assert.Equal(0, calculations.Evaluations);
        Assert.Equal(0, factory.CalculationCalls);
        Assert.Equal(0, calculations.Cache.Count);
        Assert.Null(resolved.Calculated);
        Assert.Same(RCalculated.Empty, resolved.Entities[0].Calculated);
        Assert.Empty(resolved.Entities[0].Calculated.Names);
    }
}
