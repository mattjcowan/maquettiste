using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Tests.Integration;
using Maquettiste.Engine.Tests.Scripting;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Whole-model rules (next-phase-1-design.md section 5.7): <c>maquettiste.rule({ id, severity, scope: "model", check(model, report) })</c>
/// runs once per whole-model validation, never in a validation of some elements (an element save), and its findings land on the
/// files of the elements they name.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ModelRuleTests
{
    private const string CurrencyId = "01JRMQ00000000000000000001";
    private const string CountryId = "01JRMQ00000000000000000004";
    private const string CountryPath = ".maquettiste/model/reference-types/country.json";

    private const string UnusedReferenceTypes = """
        // Every reference type should be used: a whole-model rule, so it runs once and not once per element.
        maquettiste.rule({
          id: "unused-reference-types",
          severity: "warning",
          scope: "model",
          check(model, report) {
            for (const type of model.all("reference-type")) {
              if (model.referencesTo(type.id).length === 0) {
                report(`${type.name} is not used.`, { element: type.id, pointer: "/name" });
              }
            }
          },
        });
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ReferenceType(string id, string name, int first) => $$"""
        {
          "kind": "reference-type",
          "id": "{{id}}",
          "name": "{{name}}",
          "code": { "id": "01JRMQ0000000000000000000{{first + 1}}", "length": 3 },
          "label": { "id": "01JRMQ0000000000000000000{{first + 2}}", "length": 64 }
        }
        """;

    [Fact]
    public async Task A_whole_model_rule_that_every_reference_type_is_used_runs_once_per_validate()
    {
        // A billing copy gains currency, used by an Invoice attribute, and country, used by nothing.
        using var repo = new TempRepo();
        E2ERepo.CopyTree(Fixtures.Path("models", "billing"), repo.RepoRoot);
        repo.WriteFile(".maquettiste/model/reference-types/currency.json", ReferenceType(CurrencyId, "Currency", 1));
        repo.WriteFile(CountryPath, ReferenceType(CountryId, "Country", 4));
        const string InvoicePath = ".maquettiste/model/entities/invoice.json";
        var invoice = JsonNode.Parse(repo.ReadFile(InvoicePath))!.AsObject();
        invoice["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01JRMQ00000000000000000007", ["name"] = "currency", ["type"] = new JsonObject { ["ref"] = CurrencyId },
        });
        repo.WriteFile(InvoicePath, invoice.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        repo.WriteFile(".maquettiste/extensions/rules/unused-reference-types.js", UnusedReferenceTypes);
        var model = ValidationFixture.LoadRepo(repo.RepoRoot);
        var factory = new CountingSandboxFactory();

        var report = await ValidationFixture.Validator(repo.RepoRoot, factory).ValidateAsync(model, ValidationScope.All, null, Ct);

        Assert.Equal(1, factory.ModelRuleCalls);
        var warning = Assert.Single(report.Diagnostics, d => d.Rule == "x/unused-reference-types");
        Assert.Equal((DiagnosticSeverity.Warning, "Country is not used.", CountryId, CountryPath, "/name"),
            (warning.Severity, warning.Message, warning.ElementId, warning.FilePath, warning.JsonPointer));
        Assert.Equal(4, warning.Line);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ5002" or "MQ5003");

        // A validation of some elements does not run it.
        await ValidationFixture.Validator(repo.RepoRoot, factory).ValidateAsync(model, new ValidationScope([CountryId]), null, Ct);
        Assert.Equal(1, factory.ModelRuleCalls);
    }

    [Fact]
    public async Task Element_saves_do_not_run_model_rules()
    {
        // The rule finds an error on any entity displayed as "Client": an element save that introduces it would be refused if the
        // save's validation ran the rule.
        const string Rule = """
            maquettiste.rule({
              id: "no-clients",
              scope: "model",
              check(model, report) {
                for (const entity of model.all("entity")) {
                  if (entity.displayName === "Client") report("Say customer, not client.", { element: entity.id, pointer: "/displayName" });
                }
              },
            });
            """;
        await using var repo = E2ERepo.Create(demo: false);
        var written = await repo.Store.WriteExtensionFileAsync("rules/no-clients.js", Rule, null, Ct);
        Assert.Equal(SaveOutcome.Saved, written.Outcome);
        Assert.Empty(written.Diagnostics);
        var listed = Assert.Single((await repo.Store.ListExtensionFilesAsync(Ct)).Files, f => f.Kind == "rule");
        Assert.Equal([new ExtensionRuleInfo("x/no-clients", DiagnosticSeverity.Error) { WholeModel = true }], listed.Rules);

        var saved = await repo.EditAsync(E2ERepo.CustomerId, e => e["displayName"] = "Client");

        Assert.DoesNotContain(saved.Diagnostics, d => d.Rule == "x/no-clients");
        var scoped = await repo.Store.ValidateAsync(new ValidationScope([E2ERepo.CustomerId]), Ct);
        Assert.DoesNotContain(scoped.Diagnostics, d => d.Rule == "x/no-clients");
        var whole = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        var error = Assert.Single(whole.Diagnostics, d => d.Rule == "x/no-clients");
        Assert.Equal((DiagnosticSeverity.Error, E2ERepo.CustomerId, "/displayName"), (error.Severity, error.ElementId, error.JsonPointer));
    }

    [Fact]
    public async Task A_settings_save_runs_model_rules_and_refuses_an_error_they_introduce()
    {
        const string Rule = """
            maquettiste.rule({
              id: "review-products",
              severity: "warning",
              scope: "model",
              check(model, report) {
                for (const entity of model.all("entity")) {
                  if (entity.name === "Product") report("Products need a review.", { element: entity.id });
                }
              },
            });
            """;
        await using var repo = E2ERepo.Create(demo: false);
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.WriteExtensionFileAsync("rules/review-products.js", Rule, null, Ct)).Outcome);

        // Raising the rule to error makes its finding an error the save introduces: refused, as for any rule.
        var refused = await SaveRuleSettingAsync(repo, "error");
        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var introduced = Assert.Single(refused.Diagnostics, d => d.Rule == "x/review-products");
        Assert.Equal((DiagnosticSeverity.Error, E2ERepo.ProductId), (introduced.Severity, introduced.ElementId));

        // Lowering it to info is saved, and the answer lists the finding the save changed.
        var saved = await SaveRuleSettingAsync(repo, "info");
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(DiagnosticSeverity.Info, Assert.Single(saved.Diagnostics, d => d.Rule == "x/review-products").Severity);
    }

    private static async Task<SettingsSaveResult> SaveRuleSettingAsync(E2ERepo repo, string setting)
    {
        var settings = await repo.Store.GetSettingsAsync(Ct);
        var json = JsonNode.Parse(settings.Json.GetRawText())!.AsObject();
        json["validation"] = new JsonObject { ["rules"] = new JsonObject { ["x/review-products"] = setting } };
        return await repo.Store.SaveSettingsAsync(Encoding.UTF8.GetBytes(json.ToJsonString()), settings.Hash, ChangeSource.Editor, Ct);
    }

    [Fact]
    public async Task A_model_rule_reporting_an_unknown_element_is_MQ5002()
    {
        var b = new ModelBuilder(seed: 51);
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("total", "decimal");
        var built = b.Build();
        const string Path = ".maquettiste/extensions/rules/model.js";
        var code = $$"""
            maquettiste.rule({ id: 'parts', severity: 'info', scope: 'model', check(model, report) {
              report('the total', { element: '{{invoice.AttrId("total")}}', pointer: '/name' });
              report('the whole', { element: '{{invoice.Id}}' });
              report('lost', { element: '01JNOTANID0000000000000000' });
              report('never');
            } });
            maquettiste.rule({ id: 'unnamed', scope: 'model', check(model, report) {
              report('where?');
            } });
            """;
        var model = ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], [new ScriptSource(Path, code, "model-rules")], 1);
        var invoicePath = model.GetDocument(invoice.Id)!.Path;

        var report = await ValidationFixture.Validator(scripts: new ScriptSandboxFactory()).ValidateAsync(model, ValidationScope.All, null, Ct);

        // A finding on a sub-element takes its owner's file, the entry's pointer before the given one; the findings before the failure
        // are kept, and an unknown or missing element is a TypeError, MQ5002 on the rule file at the call.
        var parts = report.Diagnostics.Where(d => d.Rule == "x/parts").OrderBy(d => d.Message, StringComparer.Ordinal).ToList();
        Assert.Equal(
        [
            ("the total", invoice.AttrId("total"), invoicePath, "/attributes/1/name"),
            ("the whole", invoice.Id, invoicePath, ""),
        ], parts.Select(d => (d.Message, d.ElementId!, d.FilePath!, d.JsonPointer!)));
        var failures = report.Diagnostics.Where(d => d.Rule == "MQ5002").OrderBy(d => d.Line).ToList();
        Assert.Equal([(Path, 4), (Path, 8)], failures.Select(d => (d.FilePath!, d.Line ?? 0)));
        Assert.Contains("no element or sub-element has the id '01JNOTANID0000000000000000'", failures[0].Message, StringComparison.Ordinal);
        Assert.Contains("names the element or sub-element of each finding", failures[1].Message, StringComparison.Ordinal);
        Assert.All(failures, f => Assert.Null(f.ElementId));
        Assert.DoesNotContain(report.Diagnostics, d => d.Message == "never");
    }

    [Fact]
    public void Kinds_with_scope_model_is_a_load_error()
    {
        var error = Assert.Throws<ScriptErrorException>(() => Scripts.Pool(
            "maquettiste.rule({ id: 'm', scope: 'model', check() {} });\nmaquettiste.rule({ id: 'k', scope: 'model', kinds: ['entity'], check() {} });",
            path: Scripts.RuleScript));

        Assert.Equal(("MQ5002", Scripts.RuleScript, 2), (error.Diagnostic.Rule, error.Diagnostic.FilePath, error.Diagnostic.Line));
        Assert.Contains("takes no kinds", error.Diagnostic.Message, StringComparison.Ordinal);

        // Without kinds it loads, registered as a whole-model rule; an element rule is not one.
        using var pool = Scripts.Pool("maquettiste.rule({ id: 'm', scope: 'model', check() {} });\nmaquettiste.rule({ id: 'e', scope: 'element', check() {} });",
            path: Scripts.RuleScript);
        Assert.Equal([("m", true), ("e", false)], pool.Registrations.Select(r => (r.Name, r.WholeModel)));
    }
}
