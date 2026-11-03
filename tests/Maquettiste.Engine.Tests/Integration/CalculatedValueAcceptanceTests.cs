using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Acceptance for scope 5.2 (next-phase-2-design.md section 5.5): one definition of an entity's public name, read by two packs and a
/// resolved rule, and previews that evaluate only the values of the objects they read.
/// </summary>
public sealed class CalculatedValueAcceptanceTests
{
    private const string Names = """
        // extensions/calculated/names.js
        function kebab(text) {
          return text.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
        }

        maquettiste.calculated({
          name: "publicName",
          kinds: ["entity"],
          schema: { type: "string", pattern: "^[a-z][a-z0-9-]*$" },
          description: "The name clients see in URLs and payloads.",
          value(entity) {
            return kebab(entity.pluralName);
          },
        });

        """;

    private const string PublicNames = """
        // extensions/rules/public-names.js
        maquettiste.rule({
          id: "public-names-unique",
          scope: "resolved",
          kinds: ["entity"],
          check(entity, model, report) {
            const name = entity.calculated.publicName;
            const other = model.entities.find((e) => e.id !== entity.id && e.calculated.publicName === name);
            if (other) report(`${entity.name} and ${other.name} share the public name ${name}.`);
          },
        });

        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The billing copy with the calculation file, the rule, and the <c>api</c> and <c>client</c> packs under <c>src/Generated</c>.</summary>
    private static E2ERepo Create()
    {
        var repo = E2ERepo.Create(demo: false, settings: settings =>
        {
            E2ERepo.RecordOff(settings);
            var packs = settings["packs"]!.AsObject();
            packs["api"] = new JsonObject { ["output"] = "src/Generated" };
            packs["client"] = new JsonObject { ["output"] = "src/Generated" };
        });
        repo.Repo.WriteFile(".maquettiste/extensions/calculated/names.js", Names);
        repo.Repo.WriteFile(".maquettiste/extensions/rules/public-names.js", PublicNames);
        repo.Repo.WriteFile(".maquettiste/templates/api/pack.json", """
            { "name": "api", "version": "1.0.0", "engine": ">=1.0",
              "units": [ { "id": "api", "template": "api.scriban", "for": "each entity", "output": "api/{{ element.name }}.txt" } ] }
            """);
        repo.Repo.WriteFile(".maquettiste/templates/api/api.scriban", "GET /{{ element.calculated.publicName }}\n");
        repo.Repo.WriteFile(".maquettiste/templates/client/pack.json", """
            { "name": "client", "version": "1.0.0", "engine": ">=1.0",
              "units": [ { "id": "client", "template": "client.scriban", "for": "each entity", "output": "client/{{ element.name }}.txt" } ] }
            """);
        repo.Repo.WriteFile(".maquettiste/templates/client/helpers.js", """
            maquettiste.helper("endpoint", (e) => "/api/" + e.calculated.publicName);
            """);
        repo.Repo.WriteFile(".maquettiste/templates/client/client.scriban", "{{ endpoint element }}\n");
        return repo;
    }

    [Fact]
    public async Task The_public_name_of_an_entity_has_one_definition_for_two_packs_and_a_rule()
    {
        await using var repo = Create();

        await repo.ApplyAsync();
        var outputs = repo.Outputs();
        Assert.Equal("GET /invoices\n", outputs["src/Generated/api/Invoice.txt"]);
        Assert.Equal("/api/invoices\n", outputs["src/Generated/client/Invoice.txt"]);
        Assert.Equal("GET /invoice-lines\n", outputs["src/Generated/api/InvoiceLine.txt"]);
        Assert.Equal("/api/customers\n", outputs["src/Generated/client/Customer.txt"]);
        Assert.DoesNotContain((await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // A second entity with the same plural: the resolved rule reports both, and generation stops before planning.
        var archive = await repo.CreateElementAsync("""
            {"kind": "entity", "name": "InvoiceArchive", "pluralName": "Invoices", "package": "01J92P0V01KDRN8GX5PGYCNKSX",
             "key": {"attributes": ["01J92P0VZZ0000000000000010"]},
             "attributes": [{"id": "01J92P0VZZ0000000000000010", "name": "id", "type": "uuid", "required": true}]}
            """);
        // Kept out of the main database, so its conventional table does not take the name of Invoice's: only the rule finds the clash.
        await repo.CreateElementAsync($$"""
            {"kind": "mapping", "name": "InvoiceArchive in main", "database": "{{E2ERepo.MainDatabaseId}}", "entity": "{{archive.Id}}", "ignore": true}
            """);
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        var shared = report.Diagnostics.Where(d => d.Rule == "x/public-names-unique").ToList();
        Assert.Equal(2, shared.Count);
        Assert.All(shared, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains(shared, d => d.FilePath == ".maquettiste/model/entities/invoice.json" && d.ElementId == E2ERepo.InvoiceId);
        Assert.Contains(shared, d => d.ElementId == archive.Id && d.FilePath != ".maquettiste/model/entities/invoice.json");
        Assert.Contains("share the public name invoices", shared[0].Message, StringComparison.Ordinal);
        var stopped = await repo.RunAsync(GenerationMode.Apply);
        Assert.NotEqual(RunOutcome.Succeeded, stopped.Outcome);
        Assert.Contains(stopped.Diagnostics, d => d.Rule == "x/public-names-unique");
        Assert.DoesNotContain(stopped.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && !d.Rule.StartsWith("x/", StringComparison.Ordinal));
        Assert.Equal(0, stopped.UnitsRendered);
        Assert.Empty(stopped.Changes);

        // The entity removed and the definition changed to the kebab-case singular name: one plan rewrites both packs' files.
        var created = await repo.Store.GetElementAsync(archive.Id!, Ct);
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.DeleteAsync(archive.Id!, created!.Hash, DeleteResolution.DeleteDependents, ChangeSource.Editor, Ct)).Outcome);
        var file = (await repo.Store.ReadExtensionFileAsync("calculated/names.js", Ct))!;
        var singular = await repo.Store.WriteExtensionFileAsync("calculated/names.js",
            Names.Replace("kebab(entity.pluralName)", "kebab(entity.name)", StringComparison.Ordinal), file.Hash, Ct);
        Assert.Equal(SaveOutcome.Saved, singular.Outcome);
        var plan = (await repo.Service.PlanAsync(new GenerationRequest { Mode = GenerationMode.DryRun }, null, Ct)).Plan!;

        Assert.Contains(plan.Changes, c => c.Path == "src/Generated/api/Invoice.txt" && c.Kind == FileChangeKind.Modified);
        Assert.Contains(plan.Changes, c => c.Path == "src/Generated/client/Invoice.txt" && c.Kind == FileChangeKind.Modified);
        var entities = (await repo.Store.GetIndexAsync(Ct)).Count(s => s.Kind == "entity");
        Assert.Equal(entities, plan.Units.Count(u => u.Pack == "api" && !u.Skipped));
        Assert.Equal(entities, plan.Units.Count(u => u.Pack == "client" && !u.Skipped));
        Assert.Contains(plan.Units, u => u.Pack == "e2e");
        Assert.All(plan.Units.Where(u => u.Pack == "e2e"), u => Assert.True(u.Skipped, u.Key + " " + u.Reason));
        Assert.Equal(RunOutcome.Succeeded, (await repo.Service.ApplyAsync(plan.Id, null, Ct)).Outcome);
        Assert.Equal("GET /invoice\n", repo.Outputs()["src/Generated/api/Invoice.txt"]);
        Assert.Equal("/api/invoice\n", repo.Outputs()["src/Generated/client/Invoice.txt"]);
    }

    [Fact]
    public async Task A_preview_evaluates_the_calculated_values_of_the_objects_it_reads_only()
    {
        await using var repo = Create();
        var factory = new CountingSandboxFactory();
        var services = EngineServices.Create(repo.Options);
        var calculations = new CalculationService(factory, repo.Options.EffectiveParallelism);
        var resolver = new ModelResolver(repo.Options, factory, services.SeedRows, calculations);
        services = services with
        {
            Scripts = factory, Resolver = resolver, Calculations = calculations,
            Validator = new ModelValidator(repo.Options, services.Schemas, factory, resolver, services.SeedRows),
            Checks = new ResolvedChecks(repo.Options, factory, calculations),
        };
        await using var store = new ModelStore(repo.Options, services);
        var service = new GenerationService(store, repo.Options);

        var preview = await service.PreviewAsync("api", "api", E2ERepo.InvoiceId, Ct);
        Assert.Equal("GET /invoices\n", Assert.Single(preview.Files).Text);
        Assert.Equal(1, factory.CalculationCalls);

        var customer = await service.GetCalculatedAsync(new CalculatedValueQuery([E2ERepo.CustomerId]), Ct);
        Assert.Equal("customers", Assert.Single(customer.Items).Calculated["publicName"]);
        Assert.Equal(2, factory.CalculationCalls);

        var entities = (await store.GetIndexAsync(Ct)).Count(s => s.Kind == "entity");
        Assert.Equal(8, entities);
        Assert.DoesNotContain((await store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(entities, factory.CalculationCalls);
    }
}
