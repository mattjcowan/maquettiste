using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Rules registered with scope "resolved" (next-phase-2-design.md section 5.5).</summary>
public sealed class ResolvedRuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptSource Rule(string code) => new(".maquettiste/extensions/rules/resolved.js", code, ContentHash.Of(code));

    private const string PublicNames = """
        maquettiste.rule({
          id: "public-names",
          scope: "resolved",
          kinds: ["entity"],
          check(entity, model, report) {
            report(entity.name + " is public as " + entity.calculated.publicName, { pointer: "/name", severity: "warning" });
          },
        });
        """;

    private static readonly ScriptSource Names = CalculationKit.File("names.js",
        "maquettiste.calculated({ name: 'publicName', kinds: ['entity'], schema: { type: 'string' }, value(e) { return e.name.toLowerCase(); } });");

    private static ModelSnapshot Snapshot(ModelBuilder builder, ScriptSource rule, ScriptSource[]? calculations = null, ScriptLibrarySource[]? libraries = null)
    {
        var basic = builder.Build();
        return ModelSnapshot.Create(basic.Documents, basic.Settings, basic.SettingsHash, [], [rule], 1, scriptLibraries: libraries,
            calculationScripts: calculations ?? [Names]);
    }

    private static ResolvedCheckResult Check(CalculationKit kit, ModelSnapshot snapshot) =>
        new ResolvedChecks(ResolutionKit.Options with { MaxDegreeOfParallelism = 2 }, kit.Factory, kit.Service)
            .RunAsync(snapshot, kit.Resolve(snapshot), Ct).GetAwaiter().GetResult();

    [Fact]
    public void A_resolved_rule_runs_once_per_object_of_its_kinds()
    {
        var kit = new CalculationKit(2);
        var result = Check(kit, Snapshot(CalculationKit.Billing(), Rule(PublicNames)));

        Assert.Empty(result.Findings);
        Assert.Equal(["Customer is public as customer", "Invoice is public as invoice", "InvoiceLine is public as invoiceline"],
            result.ScriptFindings.Select(d => d.Message).Order(StringComparer.Ordinal));
        Assert.All(result.ScriptFindings, d => Assert.Equal(("x/public-names", DiagnosticSeverity.Warning), (d.Rule, d.Severity)));
        Assert.All(result.ScriptFindings, d => Assert.EndsWith("/name", d.JsonPointer, StringComparison.Ordinal));
        Assert.Equal(3, kit.Factory.ResolvedRuleCalls);
    }

    [Theory]
    [InlineData("maquettiste.rule({ id: 'r', scope: 'resolved', check() {} });", "names the kinds it checks")]
    [InlineData("maquettiste.rule({ id: 'r', scope: 'resolved', kinds: ['diagram'], check() {} });", "is not a kind of the resolved model")]
    public void Scope_resolved_needs_kinds(string code, string message)
    {
        var factory = new ScriptSandboxFactory();
        var error = Assert.Throws<ScriptErrorException>(() => factory.CreatePool([Rule(code)], new SandboxLimits(), 1, Ct));

        Assert.Equal("MQ5002", error.Diagnostic.Rule);
        Assert.Contains(message, error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resolved_rule_sees_calculated_values_and_virtual_attributes()
    {
        // The billing fixture's soft-delete stereotype gives Invoice and Product a virtual deletedAt attribute.
        await using var repo = Integration.E2ERepo.Create(demo: false);
        var extensions = Path.Combine(repo.Repo.ModelRoot, "extensions");
        Directory.CreateDirectory(Path.Combine(extensions, "rules"));
        Directory.CreateDirectory(Path.Combine(extensions, "calculated"));
        await File.WriteAllTextAsync(Path.Combine(extensions, "calculated", "names.js"), Names.Code, Ct);
        await File.WriteAllTextAsync(Path.Combine(extensions, "rules", "flags.js"), """
            maquettiste.rule({ id: "flags", scope: "resolved", kinds: ["attribute"], severity: "info", check(attribute, model, report) {
              if (attribute.name === "deletedAt") report(attribute.owner.name + " " + attribute.owner.calculated.publicName);
            } });
            """, Ct);
        await using var store = new ModelStore(repo.Options);

        var report = await store.ValidateAsync(Diagnostics.ValidationScope.All, Ct);
        var snapshot = await store.GetSnapshotAsync(Ct);

        var findings = report.Diagnostics.Where(d => d.Rule == "x/flags").ToList();
        Assert.Equal(["Invoice invoice", "Product product"], findings.Select(d => d.Message).Order(StringComparer.Ordinal));
        var invoice = findings.Single(d => d.Message.StartsWith("Invoice", StringComparison.Ordinal));
        Assert.Equal(snapshot.GetDocument(Integration.E2ERepo.InvoiceId)!.Path, invoice.FilePath); // located on its entity
    }

    [Fact]
    public async Task Element_saves_and_previews_do_not_run_resolved_rules()
    {
        var kit = new CalculationKit();
        var snapshot = Snapshot(CalculationKit.Billing(), Rule(PublicNames));
        var validator = new ModelValidator(ResolutionKit.Options, new Maquettiste.Engine.Json.SchemaRegistry(), kit.Factory, kit.Resolver(), null);

        var all = await validator.ValidateAsync(snapshot, Diagnostics.ValidationScope.All, null, Ct);
        var previews = await validator.ValidateAsync(snapshot, Diagnostics.ValidationScope.Previews, null, Ct);

        Assert.DoesNotContain(all.Diagnostics.Concat(previews.Diagnostics), d => d.Rule == "x/public-names");
        Assert.Equal(0, kit.Factory.ResolvedRuleCalls);
        Assert.Equal(0, kit.Factory.RuleCalls);
    }

    [Fact]
    public void Results_are_cached_by_site_and_read_keys()
    {
        var kit = new CalculationKit();
        var first = Check(kit, Snapshot(CalculationKit.Billing(), Rule(PublicNames)));
        var again = Check(kit, Snapshot(CalculationKit.Billing(), Rule(PublicNames)));
        Assert.Equal(3, kit.Factory.ResolvedRuleCalls);
        Assert.Equal(first.ScriptFindings, again.ScriptFindings);

        // Renaming one entity changes what its rule call read (its name and its calculated value): only that call runs again.
        var renamed = Check(kit, Snapshot(CalculationKit.Billing(invoice: "Bill"), Rule(PublicNames)));
        Assert.Equal(4, kit.Factory.ResolvedRuleCalls);
        Assert.Contains(renamed.ScriptFindings, d => d.Message == "Bill is public as bill");

        // The rule file is part of the key.
        Check(kit, Snapshot(CalculationKit.Billing(invoice: "Bill"), Rule(PublicNames + "\n")));
        Assert.Equal(7, kit.Factory.ResolvedRuleCalls);
    }

    [Fact]
    public void A_resolved_rule_calls_a_project_library()
    {
        const string helper = "maquettiste.helper('shout', (text) => text.toUpperCase() + maquettiste.params.mark);";
        var library = new ScriptLibrarySource("loud", "1.0.0", [], new Dictionary<string, JsonElement> { ["mark"] = JsonDocument.Parse("\"!\"").RootElement },
            [new ScriptSource(".maquettiste/templates/loud/helpers.js", helper, ContentHash.Of(helper))], ContentHash.Of(helper));
        var code = """
            maquettiste.rule({ id: "loud", scope: "resolved", kinds: ["entity"], check(entity, model, report) {
              if (entity.name === "Customer") report(maquettiste.use("loud").shout(entity.name));
            } });
            """;

        var result = Check(new CalculationKit(), Snapshot(CalculationKit.Billing(), Rule(code), libraries: [library]));

        Assert.Equal("CUSTOMER!", Assert.Single(result.ScriptFindings).Message);
    }

    [Fact]
    public async Task Include_script_rules_false_leaves_them_out()
    {
        await using var repo = Integration.E2ERepo.Create(demo: false);
        var rules = Path.Combine(repo.Repo.ModelRoot, "extensions", "rules");
        Directory.CreateDirectory(rules);
        await File.WriteAllTextAsync(Path.Combine(rules, "resolved.js"), PublicNames.Replace("entity.calculated.publicName", "entity.name.length", StringComparison.Ordinal), Ct);
        await using var store = new ModelStore(repo.Options);

        var with = await store.ValidateAsync(Diagnostics.ValidationScope.All, Ct);
        var without = await store.ValidateAsync(new Diagnostics.ValidationScope(IncludeScriptRules: false), Ct);

        Assert.Contains(with.Diagnostics, d => d.Rule == "x/public-names" && d.FilePath is not null && d.Line is not null);
        Assert.DoesNotContain(without.Diagnostics, d => d.Rule == "x/public-names");
    }
}
