using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>The checks after resolution on the validate and generation paths (next-phase-2-design.md section 5.5).</summary>
public sealed class ResolvedChecksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Names = """
        maquettiste.calculated({ name: "publicName", kinds: ["entity"], schema: { type: "string" }, value(e) {
          if (e.name === "Invoice") throw new Error("no public name");
          return e.name.toLowerCase();
        } });
        """;

    private const string Route = """
        maquettiste.rule({ id: "routes", scope: "resolved", kinds: ["entity"], check(e, model, report) {
          if (e.name === "Customer") report("Customer is routed as " + e.calculated.publicName, { severity: "info" });
          if (e.name === "Invoice") report("never reported: " + e.calculated.publicName);
        } });
        """;

    private sealed record Setup(E2ERepo Repo, ModelStore Store, GenerationService Service, CountingSandboxFactory Factory) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            await Repo.DisposeAsync();
        }
    }

    private static async Task<Setup> Create(string? elementRule = null)
    {
        var repo = E2ERepo.Create(demo: false);
        var root = repo.Repo.ModelRoot;
        Directory.CreateDirectory(Path.Combine(root, "extensions", "calculated"));
        Directory.CreateDirectory(Path.Combine(root, "extensions", "rules"));
        await File.WriteAllTextAsync(Path.Combine(root, "extensions", "calculated", "names.js"), Names, Ct);
        await File.WriteAllTextAsync(Path.Combine(root, "extensions", "rules", "routes.js"), Route + (elementRule ?? ""), Ct);
        var factory = new CountingSandboxFactory();
        var services = EngineServices.Create(repo.Options);
        var calculations = new CalculationService(factory, 2);
        var resolver = new ModelResolver(repo.Options, factory, services.SeedRows, calculations);
        services = services with
        {
            Scripts = factory, Resolver = resolver, Calculations = calculations,
            Validator = new ModelValidator(repo.Options, services.Schemas, factory, resolver, services.SeedRows),
            Checks = new ResolvedChecks(repo.Options, factory, calculations),
        };
        var store = new ModelStore(repo.Options, services);
        return new Setup(repo, store, new GenerationService(store, repo.Options), factory);
    }

    private static async Task<string> IdOf(ModelStore store, string name) =>
        (await store.GetSnapshotAsync(Ct)).All<Entity>().Single(e => e.Name == name).Id;

    [Fact]
    public async Task Validate_reports_calculation_failures_and_resolved_findings_once_per_snapshot()
    {
        await using var setup = await Create();

        var first = await setup.Store.ValidateAsync(ValidationScope.All, Ct);
        var calls = (setup.Factory.CalculationCalls, setup.Factory.ResolvedRuleCalls);
        var second = await setup.Store.ValidateAsync(ValidationScope.All, Ct);

        Assert.Equal(calls, (setup.Factory.CalculationCalls, setup.Factory.ResolvedRuleCalls));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        var failure = Assert.Single(first.Diagnostics, d => d.Rule == "MQ5010");
        Assert.Equal(await IdOf(setup.Store, "Invoice"), failure.ElementId);
        Assert.Contains(first.Diagnostics, d => d.Rule == "x/routes" && d.Message == "Customer is routed as customer");
        Assert.DoesNotContain(first.Diagnostics, d => d.Message.StartsWith("never reported", StringComparison.Ordinal)); // reported once, on the value
    }

    [Fact]
    public async Task Generation_stops_on_an_error_the_checks_report()
    {
        await using var setup = await Create();

        var result = await setup.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.DryRun, Jobs = 2 }, null, Ct);

        Assert.NotEqual(RunOutcome.Succeeded, result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ5010");
        Assert.Equal(0, result.UnitsRendered);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task A_model_with_validation_errors_runs_no_checks()
    {
        await using var setup = await Create("""
            maquettiste.rule({ id: "no-customers", kinds: ["entity"], check(e, model, report) {
              if (e.name === "Customer") report("No customers.");
            } });
            """);

        var report = await setup.Store.ValidateAsync(ValidationScope.All, Ct);

        Assert.Contains(report.Diagnostics, d => d.Rule == "x/no-customers" && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5010" or "x/routes");
        Assert.Equal((0, 0), (setup.Factory.CalculationCalls, setup.Factory.ResolvedRuleCalls));
    }

    [Fact]
    public async Task A_scoped_validate_filters_the_checks_to_its_elements()
    {
        await using var setup = await Create();

        var invoice = await setup.Store.ValidateAsync(new ValidationScope([await IdOf(setup.Store, "Invoice")]), Ct);
        var customer = await setup.Store.ValidateAsync(new ValidationScope([await IdOf(setup.Store, "Customer")]), Ct);

        Assert.Contains(invoice.Diagnostics, d => d.Rule == "MQ5010");
        Assert.DoesNotContain(invoice.Diagnostics, d => d.Rule == "x/routes");
        Assert.Contains(customer.Diagnostics, d => d.Rule == "x/routes");
        Assert.DoesNotContain(customer.Diagnostics, d => d.Rule == "MQ5010");
    }
}
